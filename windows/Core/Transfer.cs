using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DropDock.Core
{
    /// <summary>What a list of dropped files and folders turns into on the wire.</summary>
    public sealed class Manifest
    {
        public readonly List<object> Items = new List<object>();
        public readonly List<KeyValuePair<string, long>> Files = new List<KeyValuePair<string, long>>();
        public long Total;

        static readonly string[] Skip = { ".DS_Store", "Thumbs.db", "desktop.ini" };

        public string Summary
        {
            get
            {
                var tops = Items.Cast<Dictionary<string, object>>().Select(i => (string)i["path"]).Where(p => p.IndexOf('/') < 0).ToList();
                return tops.Count == 1 ? tops[0] : tops.Count + " items";
            }
        }

        public static Manifest Build(IEnumerable<string> paths)
        {
            var m = new Manifest();
            foreach (var raw in paths)
            {
                var path = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) continue;
                if (Directory.Exists(path))
                {
                    m.Add(name, 0, true);
                    m.Walk(path, name);
                }
                else if (File.Exists(path))
                {
                    var size = new FileInfo(path).Length;
                    m.Add(name, size, false);
                    m.Files.Add(new KeyValuePair<string, long>(path, size));
                    m.Total += size;
                }
            }
            return m;
        }

        void Walk(string dir, string rel)
        {
            var info = new DirectoryInfo(dir);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (Array.IndexOf(Skip, entry.Name) >= 0) continue;
                var childRel = rel + "/" + entry.Name;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Add(childRel, 0, true);
                    Walk(entry.FullName, childRel);
                }
                else
                {
                    var size = ((FileInfo)entry).Length;
                    Add(childRel, size, false);
                    Files.Add(new KeyValuePair<string, long>(entry.FullName, size));
                    Total += size;
                }
            }
        }

        void Add(string path, long size, bool dir)
        {
            Items.Add(new Dictionary<string, object> { { "path", path }, { "size", size }, { "dir", dir } });
        }
    }

    public static class Client
    {
        public const int Chunk = 512 * 1024;

        /// <summary>Connects and proves both sides hold the same group key. Returns an encrypted channel.</summary>
        public static SecureChannel Session(string host, int port)
        {
            var me = Settings.Shared.Identity;
            var wire = Wire.Connect(host, port);
            try
            {
                wire.TimeoutSeconds = 15;
                var cn = DDCrypto.Random(32);
                wire.WriteJson(new Dictionary<string, object>
                {
                    { "t", "hello" }, { "app", "dropdock" }, { "v", 1 }, { "id", me.Id }, { "name", me.Name },
                    { "os", me.Os }, { "kind", me.Kind }, { "tag", me.Tag }, { "nonce", Convert.ToBase64String(cn) }
                });
                var reply = wire.ReadJson();
                if (Json.Str(reply, "t") == "error")
                {
                    if (Json.Str(reply, "code") == "not-linked") throw new WireException("not-linked", "Not linked with this computer");
                    throw new WireException("auth", "The other computer has a different link key");
                }
                var sn = Json.Bytes(reply, "nonce");
                var proof = Json.Bytes(reply, "proof");
                if (Json.Str(reply, "t") != "hello" || sn == null || sn.Length != 32 ||
                    !DDCrypto.SameBytes(proof, DDCrypto.Hmac(me.Key, DDCrypto.Utf8("dropdock/server"), cn, sn)))
                    throw new WireException("auth", "The other computer has a different link key");
                var mine = DDCrypto.Hmac(me.Key, DDCrypto.Utf8("dropdock/client"), cn, sn);
                wire.WriteJson(new Dictionary<string, object> { { "t", "auth" }, { "proof", Convert.ToBase64String(mine) } });
                var okm = DDCrypto.Hkdf(me.Key, DDCrypto.Concat(cn, sn), "dropdock/session/v1", 128);
                var ch = new SecureChannel(wire,
                    new Sealer(DDCrypto.Slice(okm, 0, 32), DDCrypto.Slice(okm, 32, 32)),
                    new Opener(DDCrypto.Slice(okm, 64, 32), DDCrypto.Slice(okm, 96, 32)));
                var ready = ch.ReceiveJson();
                if (Json.Str(ready, "t") != "ready") throw new WireException("bad", "No ready message");
                wire.TimeoutSeconds = 60;
                return ch;
            }
            catch
            {
                wire.Dispose();
                throw;
            }
        }

        public static List<string> SendFiles(Manifest m, string host, int port, Action<double> progress)
        {
            using (var ch = Session(host, port))
            {
                ch.SendJson(new Dictionary<string, object>
                {
                    { "t", "files" }, { "items", m.Items }, { "total", m.Total }, { "count", m.Files.Count }
                });
                ch.Wire.TimeoutSeconds = 120;
                var answer = ch.ReceiveJson();
                if (Json.Str(answer, "t") == "reject")
                    throw new WireException("remote", Json.Str(answer, "reason") ?? "The other computer refused the files");
                if (Json.Str(answer, "t") != "accept") throw new WireException("bad", "No accept");
                ch.Wire.TimeoutSeconds = 60;

                long sent = 0;
                var buffer = new byte[Chunk];
                var lastReport = DateTime.MinValue;
                foreach (var file in m.Files)
                {
                    using (var fs = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Chunk))
                    {
                        long remaining = file.Value;
                        while (remaining > 0)
                        {
                            int want = (int)Math.Min(remaining, Chunk);
                            int got = 0;
                            while (got < want)
                            {
                                int n = fs.Read(buffer, got, want - got);
                                if (n <= 0) throw new WireException("remote", Path.GetFileName(file.Key) + " changed while sending");
                                got += n;
                            }
                            ch.SendData(buffer, got);
                            remaining -= got;
                            sent += got;
                            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                            {
                                lastReport = DateTime.UtcNow;
                                if (progress != null) progress(m.Total > 0 ? (double)sent / m.Total : 1);
                            }
                        }
                    }
                }
                if (progress != null) progress(1);
                ch.SendJson(new Dictionary<string, object> { { "t", "end" } });
                ch.Wire.TimeoutSeconds = 600;
                var done = ch.ReceiveJson();
                if (Json.Str(done, "t") != "done") throw new WireException("bad", "No done");
                var saved = new List<string>();
                object list;
                if (done.TryGetValue("saved", out list) && list is List<object>)
                    foreach (var s in (List<object>)list) saved.Add(s as string);
                return saved;
            }
        }

        public static void SendClip(Clip clip, string host, int port)
        {
            using (var ch = Session(host, port))
            {
                ch.SendJson(new Dictionary<string, object> { { "t", "clip" }, { "clip", clip.ToJson() } });
                ch.Wire.TimeoutSeconds = 30;
                var answer = ch.ReceiveJson();
                if (Json.Str(answer, "t") != "ok") throw new WireException("bad", "No ok");
            }
        }

        /// <summary>Asks another computer to share its group code. <paramref name="onCode"/> gets the check code.</summary>
        public static string RequestLink(string host, int port, Action<string> onCode)
        {
            var me = Settings.Shared.Identity;
            using (var wire = Wire.Connect(host, port))
            using (var key = new LinkKey())
            {
                wire.TimeoutSeconds = 15;
                wire.WriteJson(new Dictionary<string, object>
                {
                    { "t", "link" }, { "app", "dropdock" }, { "v", 1 }, { "id", me.Id }, { "name", me.Name },
                    { "os", me.Os }, { "kind", me.Kind }, { "pub", Convert.ToBase64String(key.PublicKey) }
                });
                var wait = wire.ReadJson();
                var theirs = Json.Bytes(wait, "pub");
                if (Json.Str(wait, "t") != "link-wait" || theirs == null) throw new WireException("bad", "No link-wait");
                var secret = key.Secret(theirs);
                onCode(DDCrypto.Sas(secret));
                wire.TimeoutSeconds = 120;
                var result = wire.ReadJson();
                if (Json.Str(result, "t") == "link-deny") throw new WireException("denied", "The link was declined");
                var box = Json.Bytes(result, "box");
                if (Json.Str(result, "t") != "link-ok" || box == null) throw new WireException("bad", "No link-ok");
                byte[] enc, mac;
                DDCrypto.LinkKeys(secret, out enc, out mac);
                var plain = new Opener(enc, mac).Open(box);
                if (plain.Length < 2 || plain[0] != 1) throw new WireException("bad", "Bad link box");
                var code = DDCrypto.Normalize(Json.Str(Json.ParseObject(Encoding.UTF8.GetString(plain, 1, plain.Length - 1)), "code"));
                if (code == null) throw new WireException("bad", "Bad link code");
                return code;
            }
        }
    }

    /// <summary>Accepts connections from other computers, one worker thread per connection.</summary>
    public sealed class Server : IDisposable
    {
        TcpListener listener;
        Thread thread;
        int active;
        volatile bool disposed;
        public int Port { get; private set; }
        public Hub Hub;

        public void Start(int preferred)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, preferred);
                listener.Start();
            }
            catch (SocketException)
            {
                listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
            }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Log.Info("server: listening on " + Port);
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "DropDock server" };
            thread.Start();
        }

        void AcceptLoop()
        {
            while (!disposed)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (disposed) return; Thread.Sleep(200); continue; }
                var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                if (!IsLocal(remote) || Interlocked.Increment(ref active) > 8)
                {
                    if (IsLocal(remote)) Interlocked.Decrement(ref active);
                    c.Close();
                    continue;
                }
                var worker = new Thread(() =>
                {
                    using (var wire = new Wire(c))
                    {
                        try { Handle(wire); }
                        catch (Exception e) { Log.Info("incoming from " + remote + ": " + e.Message); }
                    }
                    Interlocked.Decrement(ref active);
                }) { IsBackground = true, Name = "DropDock connection" };
                worker.Start();
            }
        }

        /// <summary>Only talk to machines on the local network.</summary>
        public static bool IsLocal(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = a.GetAddressBytes();
                return IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc;
            }
            return false;
        }

        void Handle(Wire wire)
        {
            wire.TimeoutSeconds = 15;
            var first = wire.ReadJson();
            if (Json.Str(first, "app") != "dropdock") throw new WireException("bad", "Not DropDock");
            var t = Json.Str(first, "t");
            if (t == "hello") Session(wire, first);
            else if (t == "link") Link(wire, first);
            else throw new WireException("bad", "Unknown request");
        }

        void Session(Wire wire, Dictionary<string, object> hello)
        {
            var me = Settings.Shared.Identity;
            if (Json.Str(hello, "tag") != me.Tag)
            {
                wire.WriteJson(new Dictionary<string, object> { { "t", "error" }, { "code", "not-linked" } });
                return;
            }
            var cn = Json.Bytes(hello, "nonce");
            if (cn == null || cn.Length != 32) throw new WireException("bad", "Bad nonce");
            var sn = DDCrypto.Random(32);
            var proof = DDCrypto.Hmac(me.Key, DDCrypto.Utf8("dropdock/server"), cn, sn);
            wire.WriteJson(new Dictionary<string, object>
            {
                { "t", "hello" }, { "app", "dropdock" }, { "v", 1 }, { "id", me.Id }, { "name", me.Name },
                { "os", me.Os }, { "kind", me.Kind }, { "nonce", Convert.ToBase64String(sn) },
                { "proof", Convert.ToBase64String(proof) }
            });
            var auth = wire.ReadJson();
            var theirs = Json.Bytes(auth, "proof");
            if (Json.Str(auth, "t") != "auth" ||
                !DDCrypto.SameBytes(theirs, DDCrypto.Hmac(me.Key, DDCrypto.Utf8("dropdock/client"), cn, sn)))
            {
                wire.WriteJson(new Dictionary<string, object> { { "t", "error" }, { "code", "auth" } });
                return;
            }
            var okm = DDCrypto.Hkdf(me.Key, DDCrypto.Concat(cn, sn), "dropdock/session/v1", 128);
            var ch = new SecureChannel(wire,
                new Sealer(DDCrypto.Slice(okm, 64, 32), DDCrypto.Slice(okm, 96, 32)),
                new Opener(DDCrypto.Slice(okm, 0, 32), DDCrypto.Slice(okm, 32, 32)));
            ch.SendJson(new Dictionary<string, object> { { "t", "ready" } });

            var peerId = Json.Str(hello, "id") ?? "";
            var peerName = Json.Str(hello, "name") ?? "Another computer";
            wire.TimeoutSeconds = 30;
            var request = ch.ReceiveJson();
            wire.TimeoutSeconds = 60;
            var kind = Json.Str(request, "t");
            if (kind == "files")
            {
                try { new Receiver(ch, Hub, peerId, peerName).Run(request); }
                catch
                {
                    if (Hub != null) Hub.SetStatus(peerId, PeerState.Failed, 0, "Receive failed");
                    throw;
                }
            }
            else if (kind == "clip")
            {
                object raw;
                request.TryGetValue("clip", out raw);
                var clip = Clip.FromJson(raw as Dictionary<string, object>);
                if (clip == null) throw new WireException("bad", "Empty clip");
                clip.From = peerId;
                if (string.IsNullOrEmpty(clip.FromName)) clip.FromName = peerName;
                if (Hub != null) Hub.Received(clip);
                ch.SendJson(new Dictionary<string, object> { { "t", "ok" } });
            }
            else throw new WireException("bad", "Unknown request");
        }

        void Link(Wire wire, Dictionary<string, object> request)
        {
            var me = Settings.Shared.Identity;
            var theirs = Json.Bytes(request, "pub");
            using (var key = new LinkKey())
            {
                var secret = key.Secret(theirs);
                wire.WriteJson(new Dictionary<string, object>
                {
                    { "t", "link-wait" }, { "app", "dropdock" }, { "v", 1 }, { "id", me.Id }, { "name", me.Name },
                    { "os", me.Os }, { "kind", me.Kind }, { "pub", Convert.ToBase64String(key.PublicKey) }
                });
                var name = Json.Str(request, "name") ?? "A computer";
                var allowed = Hub != null && Hub.ConfirmLink(name, DDCrypto.Sas(secret));
                if (!allowed)
                {
                    wire.WriteJson(new Dictionary<string, object> { { "t", "link-deny" } });
                    return;
                }
                byte[] enc, mac;
                DDCrypto.LinkKeys(secret, out enc, out mac);
                var json = Json.WriteBytes(new Dictionary<string, object> { { "code", Settings.Shared.GroupCode } });
                var plain = new byte[json.Length + 1];
                plain[0] = 1;
                Buffer.BlockCopy(json, 0, plain, 1, json.Length);
                var box = new Sealer(enc, mac).Seal(plain);
                wire.WriteJson(new Dictionary<string, object> { { "t", "link-ok" }, { "box", Convert.ToBase64String(box) } });
                Log.Info("linked with " + name);
                Hub.LinkGranted(name);
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (listener != null) listener.Stop();
        }
    }

    /// <summary>Receives one batch of files into a hidden staging folder, then moves it into place.</summary>
    public sealed class Receiver
    {
        readonly SecureChannel ch;
        readonly Hub hub;
        readonly string peerId, peerName;

        static readonly char[] Bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '\\', '/' }).Distinct().ToArray();
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public Receiver(SecureChannel ch, Hub hub, string peerId, string peerName)
        {
            this.ch = ch;
            this.hub = hub;
            this.peerId = peerId;
            this.peerName = peerName;
        }

        /// <summary>Splits a wire path into safe local names, or null if it tries to escape the folder.</summary>
        public static string[] Clean(string path)
        {
            if (path == null) return null;
            var parts = path.Split('/');
            if (parts.Length == 0 || parts.Length > 64) return null;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length == 0 || p == "." || p == ".." || p.IndexOf('\0') >= 0) return null;
                var sb = new StringBuilder(p.Length);
                foreach (var c in p) sb.Append(Array.IndexOf(Bad, c) >= 0 || c < 32 ? '_' : c);
                var clean = sb.ToString().TrimEnd(' ', '.');
                if (clean.Length == 0) clean = "_";
                var stem = clean.Split('.')[0].ToUpperInvariant();
                if (Array.IndexOf(Reserved, stem) >= 0) clean = "_" + clean;
                if (clean.Length > 200) clean = clean.Substring(0, 200);
                parts[i] = clean;
            }
            return parts;
        }

        public static string Unique(string folder, string name)
        {
            var candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            var ext = Path.GetExtension(name);
            var stem = ext.Length > 0 && ext.Length < name.Length ? name.Substring(0, name.Length - ext.Length) : name;
            if (stem == name) ext = "";
            for (int n = 2; ; n++)
            {
                candidate = Path.Combine(folder, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        public void Run(Dictionary<string, object> request)
        {
            object rawItems;
            request.TryGetValue("items", out rawItems);
            var list = rawItems as List<object>;
            if (list == null || list.Count == 0 || list.Count > 200000) throw new WireException("bad", "No items");
            var items = new List<KeyValuePair<string[], long>>();
            var dirs = new List<bool>();
            foreach (var r in list)
            {
                var o = r as Dictionary<string, object>;
                var parts = Clean(Json.Str(o, "path"));
                if (parts == null)
                {
                    ch.SendJson(new Dictionary<string, object> { { "t", "reject" }, { "reason", "A file name was not allowed" } });
                    return;
                }
                var size = Json.Long(o, "size");
                if (size < 0) throw new WireException("bad", "Bad size");
                items.Add(new KeyValuePair<string[], long>(parts, size));
                dirs.Add(Json.Bool(o, "dir"));
            }
            long total = 0;
            for (int i = 0; i < items.Count; i++) if (!dirs[i]) total += items[i].Value;

            var root = Settings.Shared.ReceiveFolder;
            Directory.CreateDirectory(root);
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))).AvailableFreeSpace;
                if (free < total + (64L << 20))
                {
                    ch.SendJson(new Dictionary<string, object>
                    {
                        { "t", "reject" }, { "reason", "Not enough free space on " + Settings.Shared.DeviceName }
                    });
                    return;
                }
            }
            catch (Exception) { }

            var staging = Path.Combine(root, ".dropdock-incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var dirInfo = Directory.CreateDirectory(staging);
            try { dirInfo.Attributes |= FileAttributes.Hidden; } catch { }
            bool finished = false;
            try
            {
                ch.SendJson(new Dictionary<string, object> { { "t", "accept" } });
                if (hub != null) hub.SetStatus(peerId, PeerState.Receiving, 0, null);
                long got = 0;
                var lastReport = DateTime.MinValue;
                for (int i = 0; i < items.Count; i++)
                {
                    var target = Path.Combine(staging, Path.Combine(items[i].Key));
                    if (dirs[i]) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        long remaining = items[i].Value;
                        while (remaining > 0)
                        {
                            byte[] data;
                            if (ch.Receive(out data) != null || data == null || data.Length > remaining)
                                throw new WireException("bad", "Bad file data");
                            fs.Write(data, 0, data.Length);
                            remaining -= data.Length;
                            got += data.Length;
                            if (hub != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                            {
                                lastReport = DateTime.UtcNow;
                                hub.SetStatus(peerId, PeerState.Receiving, total > 0 ? (double)got / total : 1, null);
                            }
                        }
                    }
                }
                var end = ch.ReceiveJson();
                if (Json.Str(end, "t") != "end") throw new WireException("bad", "No end");

                var tops = new List<string>();
                foreach (var it in items) if (!tops.Contains(it.Key[0])) tops.Add(it.Key[0]);
                var saved = new List<string>();
                foreach (var name in tops)
                {
                    var from = Path.Combine(staging, name);
                    var dest = Unique(root, name);
                    if (Directory.Exists(from)) Directory.Move(from, dest); else File.Move(from, dest);
                    saved.Add(dest);
                }
                try { Directory.Delete(staging, true); } catch { }
                finished = true;
                ch.SendJson(new Dictionary<string, object>
                {
                    { "t", "done" }, { "saved", saved.Select(s => (object)Path.GetFileName(s)).ToList() }
                });
                if (hub != null) hub.ReceivedFiles(saved, peerId, peerName);
            }
            finally
            {
                if (!finished) { try { Directory.Delete(staging, true); } catch { } }
            }
        }
    }
}
