using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DropDock.Core;
using Microsoft.Win32;

namespace DropDock.App
{
    /// <summary>The tray icon and everything the shared core needs from Windows.</summary>
    sealed class TrayApp : ApplicationContext, IPlatform
    {
        readonly Control invoker = new Control();
        readonly NotifyIcon tray;
        readonly Hub hub;
        readonly DropBar bar;
        readonly MouseHook hook;
        Action balloonAction;

        public TrayApp(List<string> files)
        {
            var handle = invoker.Handle;
            Settings.Shared.Kind = DetectKind();
            hub = new Hub(this);
            bar = new DropBar(hub, this);
            bar.CreateControl();

            tray = new NotifyIcon { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "DropDock", Visible = true };
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => { e.Cancel = false; BuildMenu(tray.ContextMenuStrip); };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) bar.Reveal(DropBar.Reason.Pinned, Cursor.Position); };
            tray.BalloonTipClicked += (s, e) => { var a = balloonAction; balloonAction = null; if (a != null) a(); };
            tray.BalloonTipClosed += (s, e) => balloonAction = null;

            hub.Start();
            hook = new MouseHook(bar);
            hook.Start();
            CommandPipe.Listen(cmd => Post(() => HandleCommand(cmd)));
            SystemEvents.PowerModeChanged += OnPower;

            if (!Settings.Shared.Welcomed)
            {
                Settings.Shared.Welcomed = true;
                Toast("DropDock is ready", "Drag a file to the top edge of the screen, or rest the pointer there for a moment. Install DropDock on your other computers, then click Link.", null);
            }
            if (files.Count > 0) Post(() => bar.ChooseFiles(files));
        }

        public static Icon AppIcon(Size size)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DropDock.ico"))
                return stream == null ? SystemIcons.Application : new Icon(stream, size);
        }

        static string DetectKind()
        {
            var status = SystemInformation.PowerStatus.BatteryChargeStatus;
            return (status & BatteryChargeStatus.NoSystemBattery) != 0 || status == BatteryChargeStatus.Unknown ? "desktop" : "laptop";
        }

        void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) hub.Discovery.Goodbye();
            else if (e.Mode == PowerModes.Resume)
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(3000); hub.Discovery.Announce(); });
        }

        void HandleCommand(string cmd)
        {
            var lines = cmd.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            if (lines.Count == 0) return;
            if (lines[0] == "quit") ExitThread();
            else if (lines[0] == "files") bar.ChooseFiles(lines.Skip(1).ToList());
            else bar.Reveal(DropBar.Reason.Pinned, Cursor.Position);
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.PowerModeChanged -= OnPower;
            hook.Dispose();
            hub.Shutdown();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        // IPlatform

        public void Post(Action action)
        {
            try { invoker.BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        public void Toast(string title, string body, Action onClick)
        {
            if (invoker.InvokeRequired) { Post(() => Toast(title, body, onClick)); return; }
            balloonAction = onClick;
            tray.ShowBalloonTip(onClick != null ? 8000 : 5000, Trim(title, 63), Trim(body, 255), ToolTipIcon.None);
        }

        static string Trim(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 3) + "..."; }

        public bool ConfirmLink(string name, string code)
        {
            var done = new ManualResetEvent(false);
            bool allowed = false;
            Post(() =>
            {
                using (var dialog = new LinkPrompt(name, code)) allowed = dialog.ShowDialog() == DialogResult.Yes;
                done.Set();
            });
            done.WaitOne(TimeSpan.FromSeconds(100));
            return allowed;
        }

        public void WriteClipboard(Clip clip) { WinClipboard.Write(clip); }

        public void Changed()
        {
            bar.Reload();
            var p = hub.ActiveProgress;
            tray.Text = p.HasValue ? "DropDock: " + (int)(p.Value * 100) + "%" : "DropDock";
        }

        // Menu

        public void ChooseAndSend(Peer peer)
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to send to " + peer.Name })
            {
                if (dialog.ShowDialog() == DialogResult.OK) hub.SendFiles(dialog.FileNames, peer);
            }
        }

        void ChooseFolderAndSend(Peer peer)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose a folder to send to " + peer.Name })
            {
                if (dialog.ShowDialog() == DialogResult.OK) hub.SendFiles(new[] { dialog.SelectedPath }, peer);
            }
        }

        void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var me = Settings.Shared.Identity;
            menu.Items.Add(new ToolStripMenuItem("This computer: " + me.Name) { Enabled = false });
            Add(menu.Items, "Show DropDock Bar", () => bar.Reveal(DropBar.Reason.Pinned, Cursor.Position));
            Add(menu.Items, "Paste Clipboard To...", () => bar.StartPaste());
            Add(menu.Items, "Set Up Another Computer...", SetupForm.ShowOrFocus);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("Your computers") { Enabled = false });
            var linked = hub.LinkedPeers;
            if (linked.Count == 0) menu.Items.Add(new ToolStripMenuItem("    None linked yet") { Enabled = false });
            foreach (var p in linked)
            {
                var peer = p;
                var item = new ToolStripMenuItem("    " + p.Name);
                Add(item.DropDownItems, "Send Files...", () => ChooseAndSend(peer));
                Add(item.DropDownItems, "Send a Folder...", () => ChooseFolderAndSend(peer));
                Add(item.DropDownItems, "Paste Clipboard Here", () =>
                {
                    var clip = WinClipboard.Read();
                    if (clip == null) Toast("Your clipboard is empty", "Copy some text or an image first.", null);
                    else hub.Share(clip, peer);
                });
                var direct = hub.DirectClip(p.Id);
                if (direct != null) Add(item.DropDownItems, "Copy Their Last Clip", () => WriteClipboard(direct));
                menu.Items.Add(item);
            }
            var nearby = hub.NearbyPeers;
            if (nearby.Count > 0)
            {
                menu.Items.Add(new ToolStripMenuItem("Nearby, not linked") { Enabled = false });
                foreach (var p in nearby)
                {
                    var peer = p;
                    Add(menu.Items, "    Link with " + p.Name + "...", () => hub.RequestLink(peer));
                }
            }
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("General") { Enabled = false });
            var g = hub.General;
            if (g != null) Add(menu.Items, "    Copy \"" + (g.Preview.Length > 40 ? g.Preview.Substring(0, 40) : g.Preview) + "\"", () => WriteClipboard(g));
            else menu.Items.Add(new ToolStripMenuItem("    Nothing shared yet") { Enabled = false });
            Add(menu.Items, "    Paste Clipboard to General", () =>
            {
                var clip = WinClipboard.Read();
                if (clip == null) Toast("Your clipboard is empty", "Copy some text or an image first.", null);
                else hub.Share(clip, null);
            });
            menu.Items.Add(new ToolStripSeparator());

            Add(menu.Items, "Open Received Files", () =>
            {
                var folder = Settings.Shared.ReceiveFolder;
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            });
            var keys = new ToolStripMenuItem("Link Key");
            Add(keys.DropDownItems, "Copy Link Key", () =>
            {
                Clipboard.SetText(DDCrypto.Display(Settings.Shared.GroupCode));
                Toast("Link key copied", "Enter it on another computer with Link Key > Enter a Link Key.", null);
            });
            Add(keys.DropDownItems, "Enter a Link Key...", () =>
            {
                using (var dialog = new EnterKeyDialog())
                    if (dialog.ShowDialog() == DialogResult.OK) hub.Adopt(dialog.Code, "your other computers", null);
            });
            keys.DropDownItems.Add(new ToolStripSeparator());
            Add(keys.DropDownItems, "Unlink This Computer...", () =>
            {
                if (MessageBox.Show("This computer gets a new link key. Your other computers will stop seeing it until you link it again.",
                        "Unlink this computer?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    hub.NewGroup();
            });
            menu.Items.Add(keys);
            menu.Items.Add(new ToolStripSeparator());

            Toggle(menu.Items, "Start with Windows", Settings.Shared.StartAtLogin, v =>
            {
                Settings.Shared.StartAtLogin = v;
                Install.SetAutostart(v);
            });
            Toggle(menu.Items, "Put Clips Sent Here on the Clipboard", Settings.Shared.AutoCopyDirect, v => Settings.Shared.AutoCopyDirect = v);
            Toggle(menu.Items, "Open Bar When Pointer Rests at the Top Edge", Settings.Shared.HoverReveal, v => Settings.Shared.HoverReveal = v);
            menu.Items.Add(new ToolStripSeparator());
            Add(menu.Items, "Uninstall DropDock...", () =>
            {
                if (MessageBox.Show("Remove DropDock from this computer? Files you received stay in Documents\\DropDock.",
                        "Uninstall DropDock", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Install.Uninstall();
                ExitThread();
            });
            Add(menu.Items, "Quit DropDock", ExitThread);
        }

        static void Add(ToolStripItemCollection items, string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => action();
            items.Add(item);
        }

        static void Toggle(ToolStripItemCollection items, string text, bool on, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Checked = on, CheckOnClick = true };
            item.CheckedChanged += (s, e) => set(item.Checked);
            items.Add(item);
        }
    }
}
