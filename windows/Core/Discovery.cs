using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace DropDock.Core
{
    public sealed class Beacon
    {
        public string Type, Id, Name, Os, Kind, Tag, Address;
        public int Port;
        public long GeneralTime;
    }

    /// <summary>
    /// Finds other DropDock computers with small UDP broadcasts. The receive thread sleeps inside the
    /// socket until a packet arrives; the only timer is a heartbeat every 50 seconds.
    /// </summary>
    public sealed class Discovery : IDisposable
    {
        public const int Port = 47820;

        public event Action<Beacon> BeaconReceived;
        public Func<Dictionary<string, object>> Fields = () => new Dictionary<string, object>();

        UdpClient udp;
        Thread thread;
        Timer heartbeat, networkDebounce;
        volatile bool disposed;
        int replyPending;

        public void Start()
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port)); }
            catch (SocketException e) { Log.Info("discovery: bind failed " + e.Message); }
            thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "DropDock discovery" };
            thread.Start();
            heartbeat = new Timer(_ => Send("here"), null, 50000, 50000);
            networkDebounce = new Timer(_ => Send("hello"), null, Timeout.Infinite, Timeout.Infinite);
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            Send("hello");
        }

        /// <summary>Ask everyone to announce themselves (after wake, network change or a new link).</summary>
        public void Announce() { Send("hello"); }

        public void Goodbye() { Send("bye"); }

        void OnNetworkChanged(object sender, EventArgs e)
        {
            if (!disposed) networkDebounce.Change(1500, Timeout.Infinite);
        }

        void ReceiveLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed)
            {
                try
                {
                    var data = udp.Receive(ref from);
                    Handle(data, from.Address.ToString());
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (disposed) return; Thread.Sleep(200); }
                catch (Exception e) { Log.Info("discovery: " + e.Message); }
            }
        }

        void Handle(byte[] data, string ip)
        {
            Dictionary<string, object> o;
            try { o = Json.ParseObject(data); } catch { return; }
            if (Json.Str(o, "app") != "dropdock" || Json.Long(o, "v") != 1) return;
            var type = Json.Str(o, "t");
            var id = Json.Str(o, "id");
            if (type == null || id == null || id == Settings.Shared.Identity.Id) return;
            var b = new Beacon
            {
                Type = type,
                Id = id,
                Name = Json.Str(o, "name") ?? "Computer",
                Os = Json.Str(o, "os") ?? "mac",
                Kind = Json.Str(o, "kind") ?? "desktop",
                Port = (int)Json.Long(o, "port", 47821),
                Tag = Json.Str(o, "tag") ?? "",
                GeneralTime = Json.Long(o, "gt"),
                Address = ip
            };
            var handler = BeaconReceived;
            if (handler != null) handler(b);
            if (type == "hello" && Interlocked.Exchange(ref replyPending, 1) == 0)
            {
                int delay = 50 + DDCrypto.Random(1)[0] * 350 / 255;
                Timer t = null;
                t = new Timer(_ =>
                {
                    Interlocked.Exchange(ref replyPending, 0);
                    Send("here");
                    t.Dispose();
                }, null, delay, Timeout.Infinite);
            }
        }

        void Send(string type)
        {
            if (udp == null || disposed) return;
            var o = Fields();
            o["app"] = "dropdock";
            o["v"] = 1;
            o["t"] = type;
            var data = Json.WriteBytes(o);
            foreach (var target in BroadcastTargets())
            {
                try { udp.Send(data, data.Length, new IPEndPoint(target, Port)); }
                catch (Exception) { }
            }
        }

        static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4 || (mask[0] == 255 && mask[1] == 255 && mask[2] == 255 && mask[3] == 255)) continue;
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        var addr = new IPAddress(b);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        public void Dispose()
        {
            disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            if (heartbeat != null) heartbeat.Dispose();
            if (networkDebounce != null) networkDebounce.Dispose();
            if (udp != null) udp.Close();
        }
    }
}
