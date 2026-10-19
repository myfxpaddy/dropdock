using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DropDock.Core
{
    public enum PeerState { Idle, Sending, Receiving, Done, Failed, Linking }

    public sealed class Peer
    {
        public string Id, Name, Os, Kind, Address, Tag;
        public int Port;
        public long GeneralTime, PushedGeneral;
        public PeerState State;
        public double Fraction;
        public string StatusText;
        internal Timer Expiry, StatusReset;

        public bool Linked { get { return Tag == Settings.Shared.GroupTag; } }

        public Peer Snapshot() { return (Peer)MemberwiseClone(); }
    }

    /// <summary>What the Windows UI (or the test harness) provides to the shared core.</summary>
    public interface IPlatform
    {
        /// <summary>Runs on the UI thread.</summary>
        void Post(Action action);
        void Toast(string title, string body, Action onClick);
        /// <summary>Called on a worker thread. Blocks until the user answers, at most 90 seconds.</summary>
        bool ConfirmLink(string name, string code);
        /// <summary>Called on the UI thread.</summary>
        void WriteClipboard(Clip clip);
        /// <summary>Peers, statuses or clips changed. Called on the UI thread.</summary>
        void Changed();
    }

    /// <summary>Owns the peer list, the General clip and every send and receive.</summary>
    public sealed class Hub
    {
        readonly IPlatform ui;
        readonly object sync = new object();
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        readonly Dictionary<string, Clip> directClips = new Dictionary<string, Clip>();
        Clip general;
        public readonly Discovery Discovery = new Discovery();
        public readonly Server Server = new Server();

        public Hub(IPlatform ui) { this.ui = ui; }

        public void Start()
        {
            general = LoadGeneral();
            Server.Hub = this;
            Server.Start(Settings.Shared.PreferredPort);
            Discovery.Fields = () =>
            {
                var me = Settings.Shared.Identity;
                Clip g;
                lock (sync) g = general;
                return new Dictionary<string, object>
                {
                    { "id", me.Id }, { "name", me.Name }, { "os", me.Os }, { "kind", me.Kind },
                    { "port", Server.Port }, { "tag", me.Tag }, { "gt", g == null ? 0L : g.Time }
                };
            };
            Discovery.BeaconReceived += OnBeacon;
            Discovery.Start();
        }

        public void Shutdown()
        {
            Discovery.Goodbye();
            Discovery.Dispose();
            Server.Dispose();
        }

        void Changed() { ui.Post(ui.Changed); }

        // Peers

        public List<Peer> LinkedPeers
        {
            get { lock (sync) return peers.Values.Where(p => p.Linked).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => p.Snapshot()).ToList(); }
        }

        public List<Peer> NearbyPeers
        {
            get { lock (sync) return peers.Values.Where(p => !p.Linked).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => p.Snapshot()).ToList(); }
        }

        public bool AnyPeers { get { lock (sync) return peers.Count > 0; } }

        public bool AnyLinking { get { lock (sync) return peers.Values.Any(p => p.State == PeerState.Linking); } }

        public Peer FindPeer(string id)
        {
            lock (sync)
            {
                Peer p;
                return peers.TryGetValue(id, out p) ? p.Snapshot() : null;
            }
        }

        public double? ActiveProgress
        {
            get
            {
                lock (sync)
                {
                    var busy = peers.Values.Where(p => p.State == PeerState.Sending || p.State == PeerState.Receiving).ToList();
                    if (busy.Count == 0) return null;
                    return busy.Average(p => p.Fraction);
                }
            }
        }

        void OnBeacon(Beacon b)
        {
            bool changed = false;
            Clip push = null;
            Peer target = null;
            lock (sync)
            {
                Peer p;
                if (b.Type == "bye")
                {
                    if (peers.TryGetValue(b.Id, out p)) { Drop(p); changed = true; }
                }
                else
                {
                    if (!peers.TryGetValue(b.Id, out p))
                    {
                        p = new Peer { Id = b.Id };
                        peers[b.Id] = p;
                        changed = true;
                        Log.Info("found " + b.Name + " at " + b.Address + ":" + b.Port);
                    }
                    else if (p.Name != b.Name || p.Tag != b.Tag || p.Address != b.Address || p.Port != b.Port)
                    {
                        changed = true;
                    }
                    p.Name = b.Name; p.Os = b.Os; p.Kind = b.Kind; p.Address = b.Address; p.Port = b.Port; p.Tag = b.Tag;
                    p.GeneralTime = b.GeneralTime;
                    var id = b.Id;
                    if (p.Expiry == null) p.Expiry = new Timer(_ => Expire(id), null, 125000, Timeout.Infinite);
                    else p.Expiry.Change(125000, Timeout.Infinite);
                    if (p.Linked && general != null && b.GeneralTime < general.Time && p.PushedGeneral != general.Time)
                    {
                        p.PushedGeneral = general.Time;
                        push = general;
                        target = p.Snapshot();
                    }
                }
            }
            if (push != null) Deliver(push, target, true);
            if (changed) Changed();
        }

        void Drop(Peer p)
        {
            peers.Remove(p.Id);
            if (p.Expiry != null) p.Expiry.Dispose();
            if (p.StatusReset != null) p.StatusReset.Dispose();
        }

        void Expire(string id)
        {
            lock (sync)
            {
                Peer p;
                if (!peers.TryGetValue(id, out p)) return;
                Drop(p);
            }
            Changed();
        }

        public void SetStatus(string id, PeerState state, double fraction, string text)
        {
            lock (sync)
            {
                Peer p;
                if (!peers.TryGetValue(id, out p)) return;
                p.State = state;
                p.Fraction = fraction;
                p.StatusText = text;
                if (p.StatusReset != null) { p.StatusReset.Dispose(); p.StatusReset = null; }
                if (state == PeerState.Done || state == PeerState.Failed)
                {
                    p.StatusReset = new Timer(_ =>
                    {
                        lock (sync)
                        {
                            Peer q;
                            if (peers.TryGetValue(id, out q) && (q.State == PeerState.Done || q.State == PeerState.Failed))
                            {
                                q.State = PeerState.Idle;
                                q.StatusText = null;
                            }
                        }
                        Changed();
                    }, null, 4000, Timeout.Infinite);
                }
            }
            Changed();
        }

        void Progress(string id, double f)
        {
            lock (sync)
            {
                Peer p;
                if (!peers.TryGetValue(id, out p) || p.State != PeerState.Sending) return;
            }
            SetStatus(id, PeerState.Sending, f, null);
        }

        // Files

        public void SendFiles(IList<string> paths, Peer peer)
        {
            if (peer == null || !peer.Linked || paths.Count == 0) return;
            var current = FindPeer(peer.Id);
            if (current != null && current.State == PeerState.Sending)
            {
                ui.Toast("Still sending to " + peer.Name, "Wait for the current transfer to finish.", null);
                return;
            }
            string id = peer.Id, host = peer.Address, name = peer.Name;
            int port = peer.Port;
            SetStatus(id, PeerState.Sending, 0, null);
            var list = paths.ToList();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var m = Manifest.Build(list);
                    if (m.Items.Count == 0) throw new WireException("remote", "Nothing to send");
                    Client.SendFiles(m, host, port, f => Progress(id, f));
                    Log.Info("sent " + m.Summary + " to " + name);
                    SetStatus(id, PeerState.Done, 1, "Sent");
                    ui.Post(() => ui.Toast("Sent to " + name, m.Summary + " is in Documents\\DropDock on " + name + ".", null));
                }
                catch (Exception e)
                {
                    Log.Info("send to " + name + " failed: " + e.Message);
                    SetStatus(id, PeerState.Failed, 0, "Failed");
                    ui.Post(() => ui.Toast("Could not send to " + name, Explain(e, name), null));
                }
            });
        }

        public void SendToAll(IList<string> paths)
        {
            var targets = LinkedPeers;
            if (targets.Count == 0)
            {
                ui.Toast("No linked computers", "Link a computer first. Open DropDock on it and click Link.", null);
                return;
            }
            foreach (var p in targets) SendFiles(paths, p);
        }

        public void ReceivedFiles(List<string> saved, string fromId, string fromName)
        {
            SetStatus(fromId, PeerState.Done, 1, "Received");
            Log.Info("received " + saved.Count + " item(s) from " + fromName);
            var what = saved.Count == 1 ? Path.GetFileName(saved[0]) : saved.Count + " items";
            var first = saved[0];
            ui.Post(() => ui.Toast("From " + fromName, what + " saved to Documents\\DropDock. Click to show it.", () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + first + "\""); } catch { }
            }));
        }

        // Clips

        /// <summary>Sends a clip to one computer, or to General when <paramref name="peer"/> is null.</summary>
        public void Share(Clip content, Peer peer)
        {
            var clip = content.Copy();
            var me = Settings.Shared.Identity;
            clip.Id = Guid.NewGuid().ToString().ToLowerInvariant();
            clip.From = me.Id;
            clip.FromName = me.Name;
            clip.Time = Clip.NowMs();
            if (peer != null)
            {
                clip.Scope = "direct";
                Deliver(clip, peer, false);
                return;
            }
            clip.Scope = "general";
            SetGeneral(clip);
            var targets = LinkedPeers;
            lock (sync) foreach (var t in targets) { Peer p; if (peers.TryGetValue(t.Id, out p)) p.PushedGeneral = clip.Time; }
            foreach (var t in targets) Deliver(clip, t, true);
            ui.Toast("Shared to General", targets.Count == 0 ? "Computers will pick it up when they come online."
                : "Any of your computers can copy it from the bar.", null);
        }

        void Deliver(Clip clip, Peer peer, bool quiet)
        {
            string id = peer.Id, host = peer.Address, name = peer.Name;
            int port = peer.Port;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Client.SendClip(clip, host, port);
                    if (!quiet) SetStatus(id, PeerState.Done, 1, "Pasted");
                }
                catch (Exception e)
                {
                    Log.Info("clip to " + name + " failed: " + e.Message);
                    if (!quiet)
                    {
                        SetStatus(id, PeerState.Failed, 0, "Failed");
                        ui.Post(() => ui.Toast("Could not paste to " + name, Explain(e, name), null));
                    }
                }
            });
        }

        public void Received(Clip clip)
        {
            if (clip.Scope == "general")
            {
                lock (sync)
                {
                    if (general != null && general.Time >= clip.Time) return;
                    Peer p;
                    if (peers.TryGetValue(clip.From, out p)) p.PushedGeneral = clip.Time;
                }
                SetGeneral(clip);
                ui.Post(() => ui.Toast("General from " + clip.FromName, clip.Preview + "  (click to copy)", () => ui.WriteClipboard(clip)));
                return;
            }
            lock (sync) directClips[clip.From] = clip;
            if (Settings.Shared.AutoCopyDirect)
            {
                ui.Post(() =>
                {
                    ui.WriteClipboard(clip);
                    ui.Toast("From " + clip.FromName + ", ready to paste", clip.Preview + "\nIt is on your clipboard. Press Ctrl+V.", null);
                });
            }
            else
            {
                ui.Post(() => ui.Toast(clip.FromName + " sent you a clip", clip.Preview + "  (click to copy)", () => ui.WriteClipboard(clip)));
            }
            Changed();
        }

        public Clip General { get { lock (sync) return general; } }

        public Clip DirectClip(string fromId)
        {
            lock (sync)
            {
                Clip c;
                return directClips.TryGetValue(fromId, out c) ? c : null;
            }
        }

        void SetGeneral(Clip clip)
        {
            lock (sync) general = clip;
            try { File.WriteAllText(Paths.General, Json.Write(clip.ToJson()), new UTF8Encoding(false)); } catch { }
            Changed();
        }

        static Clip LoadGeneral()
        {
            try { return Clip.FromJson(Json.ParseObject(File.ReadAllText(Paths.General, Encoding.UTF8))); }
            catch { return null; }
        }

        // Linking

        public void RequestLink(Peer peer)
        {
            if (peer == null || peer.Linked) return;
            var current = FindPeer(peer.Id);
            if (current != null && current.State == PeerState.Linking) return;
            string id = peer.Id, host = peer.Address, name = peer.Name;
            int port = peer.Port;
            SetStatus(id, PeerState.Linking, 0, "Asking...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var code = Client.RequestLink(host, port, sas =>
                    {
                        SetStatus(id, PeerState.Linking, 0, "Code " + sas);
                        ui.Post(() => ui.Toast("Click Allow on " + name, "Check that " + name + " shows the code " + sas + ".", null));
                    });
                    Adopt(code, name, id);
                }
                catch (Exception e)
                {
                    Log.Info("link with " + name + " failed: " + e.Message);
                    var we = e as WireException;
                    SetStatus(id, PeerState.Failed, 0, we != null && we.Code == "denied" ? "Declined" : "Failed");
                    ui.Post(() => ui.Toast("Not linked with " + name, Explain(e, name), null));
                }
            });
        }

        public void Adopt(string code, string fromName, string peerId)
        {
            Settings.Shared.SetGroupCode(code);
            lock (sync) foreach (var p in peers.Values) p.PushedGeneral = 0;
            if (peerId != null) SetStatus(peerId, PeerState.Done, 1, "Linked");
            Discovery.Announce();
            Changed();
            ui.Post(() => ui.Toast("Linked with " + fromName, "Your computers can now send each other files and clips.", null));
        }

        public void LinkGranted(string name)
        {
            Discovery.Announce();
            ui.Post(() => ui.Toast(name + " is linked", "It can now send files and clips to this computer.", null));
        }

        public void NewGroup()
        {
            Settings.Shared.SetGroupCode(DDCrypto.NewGroupCode());
            Discovery.Announce();
            Changed();
        }

        int linkPromptOpen;

        public bool ConfirmLink(string name, string code)
        {
            if (Paths.IsTest && Environment.GetEnvironmentVariable("DROPDOCK_TEST_AUTOAPPROVE") == "1") return true;
            if (Interlocked.Exchange(ref linkPromptOpen, 1) == 1) return false;
            try { return ui.ConfirmLink(name, code); }
            finally { Interlocked.Exchange(ref linkPromptOpen, 0); }
        }

        public static string Explain(Exception e, string name)
        {
            var w = e as WireException;
            if (w != null)
            {
                switch (w.Code)
                {
                    case "not-linked": return name + " is not linked with this computer. Link it from the DropDock menu.";
                    case "auth": return name + " has a different link key. Link the two computers again.";
                    case "timeout":
                    case "closed": return name + " stopped responding. Check it is awake and on the same Wi-Fi.";
                    case "unreachable": return "Could not reach " + name + ". Check it is awake, on the same Wi-Fi, and that DropDock is running there.";
                    case "denied": return name + " did not allow the link.";
                    default: return w.Message;
                }
            }
            if (e is System.Net.Sockets.SocketException || e is IOException)
                return "Could not reach " + name + ". Check it is awake, on the same Wi-Fi, and that DropDock is running there.";
            return e.Message;
        }
    }
}
