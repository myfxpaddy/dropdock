using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DropDock.Core
{
    public class WireException : Exception
    {
        public string Code { get; private set; }

        public WireException(string code, string message) : base(message) { Code = code; }

        public static WireException Closed() { return new WireException("closed", "The connection closed"); }
    }

    /// <summary>Length-prefixed frames over TCP. Blocking calls with socket timeouts, run on worker threads.</summary>
    public sealed class Wire : IDisposable
    {
        public const int MaxFrame = 48 << 20;
        readonly TcpClient client;
        readonly NetworkStream stream;

        public Wire(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            stream = client.GetStream();
            TimeoutSeconds = 60;
        }

        public static Wire Connect(string host, int port, int timeoutMs = 6000)
        {
            var c = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var task = c.ConnectAsync(host, port);
                if (!task.Wait(timeoutMs)) throw new WireException("timeout", "The other computer did not answer");
                return new Wire(c);
            }
            catch (AggregateException e)
            {
                c.Close();
                throw new WireException("unreachable", e.InnerException != null ? e.InnerException.Message : e.Message);
            }
            catch
            {
                c.Close();
                throw;
            }
        }

        public int TimeoutSeconds
        {
            set
            {
                client.ReceiveTimeout = value * 1000;
                client.SendTimeout = value * 1000;
            }
        }

        public IPAddress RemoteAddress
        {
            get
            {
                var ep = client.Client.RemoteEndPoint as IPEndPoint;
                return ep == null ? IPAddress.None : ep.Address;
            }
        }

        public byte[] ReadExactly(int count)
        {
            var buf = new byte[count];
            int off = 0;
            try
            {
                while (off < count)
                {
                    int n = stream.Read(buf, off, count - off);
                    if (n <= 0) throw WireException.Closed();
                    off += n;
                }
            }
            catch (IOException) { throw new WireException("timeout", "The other computer stopped responding"); }
            return buf;
        }

        public byte[] ReadFrame(int max = MaxFrame)
        {
            var h = ReadExactly(4);
            long len = (long)h[0] << 24 | (long)h[1] << 16 | (long)h[2] << 8 | h[3];
            if (len > max) throw new WireException("too-large", "The message was too large");
            return ReadExactly((int)len);
        }

        public void WriteFrame(byte[] payload)
        {
            int n = payload.Length;
            try
            {
                if (n < 64 * 1024)
                {
                    var all = new byte[n + 4];
                    all[0] = (byte)(n >> 24); all[1] = (byte)(n >> 16); all[2] = (byte)(n >> 8); all[3] = (byte)n;
                    Buffer.BlockCopy(payload, 0, all, 4, n);
                    stream.Write(all, 0, all.Length);
                }
                else
                {
                    stream.Write(new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }, 0, 4);
                    stream.Write(payload, 0, n);
                }
            }
            catch (IOException) { throw new WireException("timeout", "The other computer stopped responding"); }
        }

        public Dictionary<string, object> ReadJson(int max = 64 * 1024)
        {
            return Json.ParseObject(ReadFrame(max));
        }

        public void WriteJson(Dictionary<string, object> obj) { WriteFrame(Json.WriteBytes(obj)); }

        public void Dispose()
        {
            try { stream.Dispose(); } catch { }
            try { client.Close(); } catch { }
        }
    }

    /// <summary>An authenticated, encrypted channel on top of a Wire.</summary>
    public sealed class SecureChannel : IDisposable
    {
        public readonly Wire Wire;
        readonly Sealer sealer;
        readonly Opener opener;

        public SecureChannel(Wire wire, Sealer sealer, Opener opener)
        {
            Wire = wire;
            this.sealer = sealer;
            this.opener = opener;
        }

        public void SendJson(Dictionary<string, object> obj)
        {
            var json = Json.WriteBytes(obj);
            var plain = new byte[json.Length + 1];
            plain[0] = 1;
            Buffer.BlockCopy(json, 0, plain, 1, json.Length);
            Wire.WriteFrame(sealer.Seal(plain));
        }

        public void SendData(byte[] buffer, int count)
        {
            var plain = new byte[count + 1];
            plain[0] = 2;
            Buffer.BlockCopy(buffer, 0, plain, 1, count);
            Wire.WriteFrame(sealer.Seal(plain));
        }

        /// <summary>Returns a JSON object, or the raw bytes of a data frame in <paramref name="data"/>.</summary>
        public Dictionary<string, object> Receive(out byte[] data)
        {
            byte[] plain;
            try { plain = opener.Open(Wire.ReadFrame()); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new WireException("auth", "The other computer has a different link key");
            }
            if (plain.Length == 0) throw new WireException("bad", "Empty frame");
            data = null;
            if (plain[0] == 2)
            {
                data = DDCrypto.Slice(plain, 1, plain.Length - 1);
                return null;
            }
            if (plain[0] != 1) throw new WireException("bad", "Unknown frame type");
            return Json.ParseObject(Encoding.UTF8.GetString(plain, 1, plain.Length - 1));
        }

        public Dictionary<string, object> ReceiveJson()
        {
            byte[] data;
            var obj = Receive(out data);
            if (obj == null) throw new WireException("bad", "Expected a message");
            if (Json.Str(obj, "t") == "error")
                throw new WireException("remote", Json.Str(obj, "message") ?? "The other computer reported an error");
            return obj;
        }

        public void Dispose() { Wire.Dispose(); }
    }
}
