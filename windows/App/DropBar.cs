using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DropDock.Core;
using Microsoft.Win32;

namespace DropDock.App
{
    static class Theme
    {
        public static readonly Color Bar = Color.FromArgb(28, 28, 30);
        public static readonly Color Border = Color.FromArgb(64, 64, 68);
        public static readonly Color Tile = Color.FromArgb(44, 44, 46);
        public static readonly Color TileHover = Color.FromArgb(58, 58, 60);
        public static readonly Color TilePressed = Color.FromArgb(72, 72, 74);
        public static readonly Color Text = Color.White;
        public static readonly Color Subtle = Color.FromArgb(199, 199, 204);
        public static readonly Color Good = Color.FromArgb(48, 209, 88);
        public static readonly Color Bad = Color.FromArgb(255, 140, 128);
        public static readonly Color Accent = ReadAccent();

        public const string GlyphFont = "Segoe MDL2 Assets";
        public const string Paste = "\uE77F", Cancel = "\uE711", Globe = "\uE774", Laptop = "\uE7F8",
            Pc = "\uE977", Monitor = "\uE7F4", Wifi = "\uE701", Document = "\uE8A5", Info = "\uE946";

        static Color ReadAccent()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    var v = key == null ? null : key.GetValue("AccentColor");
                    if (v is int)
                    {
                        int abgr = (int)v;
                        return Color.FromArgb(abgr & 0xff, (abgr >> 8) & 0xff, (abgr >> 16) & 0xff);
                    }
                }
            }
            catch { }
            return Color.FromArgb(10, 132, 255);
        }

        /// <summary>White or black, whichever reads better on the given color.</summary>
        public static Color On(Color c)
        {
            double l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            return l > 150 ? Color.Black : Color.White;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    sealed class TileModel
    {
        public string Key, Glyph, Title, Subtitle, ActionTitle, Help;
        public float Width = 100;
        public bool Wide, Compact, Highlight, Dimmed, AcceptsDrops = true;
        public double? Progress;
        public Color? SubtitleColor;
    }

    /// <summary>One square in the bar: a computer, General, or the Paste button.</summary>
    sealed class Tile : Control
    {
        public TileModel Model;
        public Action Clicked, ActionClicked;
        public Func<IDataObject, bool> Dropped;
        public Action DragEntered;
        readonly float s;
        bool hover, pressed, dragOver;
        string flash;
        readonly Timer flashTimer = new Timer { Interval = 1600 };
        RectangleF actionRect;
        static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        public Tile(TileModel model, float scale)
        {
            s = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            AllowDrop = true;
            TabStop = false;
            BackColor = Theme.Bar;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            flashTimer.Tick += (o, e) => { flashTimer.Stop(); flash = null; Invalidate(); };
            SetModel(model);
        }

        public void SetModel(TileModel m)
        {
            Model = m;
            AccessibleName = m.Compact ? m.Help : m.Title + ". " + (flash ?? m.Subtitle);
            AccessibleDescription = m.Help;
            Size = new Size((int)(m.Width * s), (int)(84 * s));
            Invalidate();
        }

        public void Flash(string text)
        {
            flash = text;
            flashTimer.Stop();
            flashTimer.Start();
            Invalidate();
        }

        static Font F(string family, float size, FontStyle style = FontStyle.Regular)
        {
            var key = family + size + style;
            Font f;
            if (!Fonts.TryGetValue(key, out f)) Fonts[key] = f = new Font(family, size, style);
            return f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var m = Model;
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Theme.Tile, border = Color.Empty;
            if (m.Highlight) { fill = Theme.Mix(Theme.Tile, Theme.Accent, 0.25); border = Theme.Accent; }
            if (hover) fill = m.Highlight ? Theme.Mix(Theme.Tile, Theme.Accent, 0.4) : Theme.TileHover;
            if (pressed) fill = Theme.TilePressed;
            if (dragOver) { fill = Theme.Mix(Theme.Tile, Theme.Accent, 0.6); border = Theme.Accent; }
            using (var path = Theme.Rounded(bounds, 12 * s))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty) using (var p = new Pen(border, 1.5f * s)) g.DrawPath(p, path);
            }
            int alpha = m.Dimmed ? 150 : 255;
            var text = Color.FromArgb(alpha, Theme.Text);
            var sub = flash != null ? Theme.Text : (m.SubtitleColor ?? Theme.Subtle);
            sub = Color.FromArgb(alpha, sub);
            var subtitle = flash ?? m.Subtitle;
            const TextFormatFlags line = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int w = Width;
            actionRect = RectangleF.Empty;

            if (m.Compact)
            {
                TextRenderer.DrawText(g, m.Glyph, F(Theme.GlyphFont, 14), ClientRectangle, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            if (m.Wide)
            {
                TextRenderer.DrawText(g, m.Glyph, F(Theme.GlyphFont, 15), new Rectangle((int)(10 * s), (int)(10 * s), (int)(30 * s), (int)(28 * s)), text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, m.Title, F("Segoe UI Semibold", 9.5f), new Rectangle((int)(44 * s), (int)(10 * s), w - (int)(54 * s), (int)(20 * s)), text, line | TextFormatFlags.Left);
                bool hasAction = m.ActionTitle != null && flash == null;
                TextRenderer.DrawText(g, subtitle, F("Segoe UI", 8.5f), new Rectangle((int)(44 * s), (int)(31 * s), w - (int)(54 * s), (int)((hasAction ? 26 : 42) * s)), sub,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                actionRect = RectangleF.Empty;
                if (hasAction) DrawAction(g, m.ActionTitle, new PointF(w - 10 * s, Height - 26 * s), true);
                return;
            }

            TextRenderer.DrawText(g, m.Glyph, F(Theme.GlyphFont, 17), new Rectangle(0, (int)(8 * s), w, (int)(32 * s)), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, m.Title, F("Segoe UI Semibold", 9f), new Rectangle((int)(6 * s), (int)(41 * s), w - (int)(12 * s), (int)(18 * s)), text, line | TextFormatFlags.HorizontalCenter);
            actionRect = RectangleF.Empty;
            if (m.ActionTitle != null && flash == null)
            {
                DrawAction(g, m.ActionTitle, new PointF(w / 2f, 58 * s), false);
            }
            else
            {
                TextRenderer.DrawText(g, subtitle, F("Segoe UI", 8f), new Rectangle((int)(4 * s), (int)(59 * s), w - (int)(8 * s), (int)(16 * s)), sub, line | TextFormatFlags.HorizontalCenter);
            }
            if (m.Progress.HasValue)
            {
                var track = new RectangleF(12 * s, Height - 9 * s, w - 24 * s, 3 * s);
                using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, track);
                var p = (float)Math.Max(0.02, Math.Min(1, m.Progress.Value));
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, track.X, track.Y, track.Width * p, track.Height);
            }
        }

        void DrawAction(Graphics g, string title, PointF anchor, bool rightAligned)
        {
            var font = F("Segoe UI Semibold", 8.5f);
            var size = TextRenderer.MeasureText(g, title, font, Size.Empty, TextFormatFlags.NoPadding);
            float pw = Math.Max(size.Width + 18 * s, 54 * s), ph = 20 * s;
            float x = rightAligned ? anchor.X - pw : anchor.X - pw / 2;
            actionRect = new RectangleF(x, anchor.Y, pw, ph);
            using (var path = Theme.Rounded(actionRect, ph / 2))
            using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            TextRenderer.DrawText(g, title, font, Rectangle.Round(actionRect), Theme.On(Theme.Accent),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = false;
            Invalidate();
            if (!ClientRectangle.Contains(e.Location)) return;
            if (!actionRect.IsEmpty && actionRect.Contains(e.Location)) { if (ActionClicked != null) ActionClicked(); }
            else if (Clicked != null) Clicked();
        }

        static DragDropEffects CopyOnly(DragEventArgs e)
        {
            // Never report Move: Explorer would delete the original after the drop.
            return (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (DragEntered != null) DragEntered();
            if (!Model.AcceptsDrops || !WinClipboard.HasDroppableData(e.Data)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = CopyOnly(e);
            dragOver = true;
            Invalidate();
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            e.Effect = Model.AcceptsDrops && WinClipboard.HasDroppableData(e.Data) ? CopyOnly(e) : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e) { dragOver = false; Invalidate(); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            dragOver = false;
            Invalidate();
            if (Dropped != null) Dropped(e.Data);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) flashTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The hidden bar at the top of the screen. It appears when a drag reaches the top edge, when the
    /// pointer rests at the top center, or from the tray icon. It never takes focus from your work.
    /// </summary>
    sealed class DropBar : Form
    {
        public enum Reason { Drag, Hover, Pinned }

        readonly Hub hub;
        readonly TrayApp app;
        readonly Dictionary<string, Tile> tiles = new Dictionary<string, Tile>();
        List<string> order = new List<string>();
        readonly Timer tick = new Timer { Interval = 150 };
        readonly Timer fade = new Timer { Interval = 15 };
        readonly float s;
        Reason reason;
        DateTime? outsideSince;
        bool everInside, pickMode, roundedByRegion;
        DateTime holdUntil = DateTime.MinValue;
        Rectangle screen;
        List<string> pendingFiles;
        int separatorX = -1, anchorX;

        public DropBar(Hub hub, TrayApp app)
        {
            this.hub = hub;
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bar;
            DoubleBuffered = true;
            AllowTransparency = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "DropDock";
            AccessibleName = "DropDock bar";
            AccessibleRole = AccessibleRole.ToolBar;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            tick.Tick += (o, e) => Tick();
            fade.Tick += (o, e) =>
            {
                Opacity = Math.Min(1, Opacity + 0.2);
                if (Opacity >= 1) fade.Stop();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = Native.DWMWCP_ROUND;
            try { roundedByRegion = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4) != 0; }
            catch { roundedByRegion = true; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            if (separatorX > 0)
                using (var p = new Pen(Color.FromArgb(72, 72, 76), Math.Max(1, s)))
                    e.Graphics.DrawLine(p, separatorX, (int)(24 * s), separatorX, Height - (int)(24 * s));
        }

        // Showing and hiding

        public void Reveal(Reason r, Point at)
        {
            if (Visible)
            {
                if (r == Reason.Pinned || r == Reason.Drag) reason = r;
                outsideSince = null;
                return;
            }
            screen = Screen.FromPoint(at).Bounds;
            anchorX = at.X;
            reason = r;
            everInside = false;
            outsideSince = null;
            if (r != Reason.Pinned) pickMode = false;
            Apply(Models());
            PlaceOnScreen();
            Opacity = Native.AnimationsEnabled ? 0 : 1;
            Show();
            if (Opacity < 1) fade.Start();
            tick.Start();
        }

        public void HideBar()
        {
            if (!Visible) return;
            tick.Stop();
            fade.Stop();
            pickMode = false;
            pendingFiles = null;
            Hide();
        }

        public void ChooseFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            HideBar();
            pendingFiles = files;
            Reveal(Reason.Pinned, Cursor.Position);
        }

        public void StartPaste()
        {
            Reveal(Reason.Pinned, Cursor.Position);
            pickMode = true;
            Reload();
        }

        public void GlobalMouseDown(Point p)
        {
            if (Visible && reason != Reason.Drag && !KeepRegion.Contains(p))
                BeginInvoke((Action)HideBar);
        }

        Rectangle KeepRegion
        {
            get
            {
                int m = (int)(30 * s);
                return new Rectangle(Left - m, screen.Top, Width + 2 * m, Bottom - screen.Top + m);
            }
        }

        void PlaceOnScreen()
        {
            // Centered under the pointer, kept fully on the screen.
            int margin = (int)(8 * s);
            var x = Math.Min(Math.Max(anchorX - Width / 2, screen.Left + margin), screen.Right - Width - margin);
            var y = screen.Top + (int)(6 * s);
            Location = new Point(x, y);
            if (roundedByRegion)
                using (var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), 14 * s))
                    Region = new Region(path);
        }

        void Tick()
        {
            if (!Visible) return;
            var p = Cursor.Position;
            if (KeepRegion.Contains(p))
            {
                outsideSince = null;
                if (Bounds.Contains(p)) everInside = true;
                return;
            }
            if (DateTime.UtcNow < holdUntil) return;
            var now = DateTime.UtcNow;
            if (outsideSince == null) outsideSince = now;
            double away = (now - outsideSince.Value).TotalSeconds;
            double limit;
            switch (reason)
            {
                case Reason.Drag: limit = (MouseButtons & MouseButtons.Left) != 0 ? 0.35 : 0.15; break;
                case Reason.Hover: limit = 0.4; break;
                default: limit = everInside ? 0.6 : 10; break;
            }
            if (pickMode || pendingFiles != null || hub.AnyLinking) limit = Math.Max(limit, everInside ? 1.2 : 10);
            if (away > limit) HideBar();
        }

        // Content

        public void Reload()
        {
            if (Visible) Apply(Models());
        }

        static string GlyphFor(Peer p)
        {
            if (p.Kind == "laptop") return Theme.Laptop;
            return p.Os == "windows" ? Theme.Pc : Theme.Monitor;
        }

        List<TileModel> Models()
        {
            bool choosing = pickMode || pendingFiles != null;
            var list = new List<TileModel>();
            if (pendingFiles != null)
                list.Add(new TileModel { Key = "paste", Glyph = Theme.Document, Width = 96, AcceptsDrops = false,
                    Title = pendingFiles.Count == 1 ? System.IO.Path.GetFileName(pendingFiles[0]) : pendingFiles.Count + " items",
                    Subtitle = "Click to cancel", Help = "Pick a computer to send to, or click here to cancel." });
            else if (pickMode)
                list.Add(new TileModel { Key = "paste", Glyph = Theme.Cancel, Title = "Cancel", Subtitle = "Pick a target", Width = 84,
                    AcceptsDrops = false, Help = "Cancel pasting." });
            else
                list.Add(new TileModel { Key = "paste", Glyph = Theme.Paste, Title = "Paste", Subtitle = "Clipboard", Width = 84,
                    AcceptsDrops = false, Help = "Send what you copied to one computer, or to General for all of them." });

            var general = new TileModel { Key = "general", Glyph = Theme.Globe, Title = "General", Width = 214, Wide = true,
                Subtitle = "Paste here to share with every computer",
                Help = "General is shared by all your computers. Drop files here to send them to every computer." };
            var g = hub.General;
            if (g != null) { general.Subtitle = g.FromName + ": " + g.Preview; general.ActionTitle = "Copy"; }
            if (choosing)
            {
                general.Highlight = true;
                general.ActionTitle = null;
                general.Subtitle = pickMode ? "Paste to every computer" : "Send to every linked computer";
            }
            list.Add(general);

            foreach (var p in hub.LinkedPeers)
            {
                var m = new TileModel { Key = p.Id, Glyph = GlyphFor(p), Title = p.Name, Subtitle = "Ready",
                    Help = p.Name + ". Drop files here to send them. Click to choose files." };
                switch (p.State)
                {
                    case PeerState.Sending: m.Subtitle = "Sending " + (int)(p.Fraction * 100) + "%"; m.Progress = p.Fraction; break;
                    case PeerState.Receiving: m.Subtitle = "Receiving " + (int)(p.Fraction * 100) + "%"; m.Progress = p.Fraction; break;
                    case PeerState.Done: m.Subtitle = p.StatusText; m.SubtitleColor = Theme.Good; break;
                    case PeerState.Failed: m.Subtitle = p.StatusText; m.SubtitleColor = Theme.Bad; break;
                    case PeerState.Linking: m.Subtitle = p.StatusText; break;
                    default: if (hub.DirectClip(p.Id) != null) m.ActionTitle = "Copy"; break;
                }
                if (choosing)
                {
                    m.Highlight = true;
                    m.ActionTitle = null;
                    if (p.State == PeerState.Idle) m.Subtitle = pickMode ? "Paste here" : "Send here";
                }
                list.Add(m);
            }

            foreach (var p in hub.NearbyPeers)
            {
                var m = new TileModel { Key = p.Id, Glyph = GlyphFor(p), Title = p.Name, Subtitle = "Click to link", Dimmed = true,
                    AcceptsDrops = false, Help = p.Name + " has DropDock but is not linked yet. Click to link it." };
                if (p.State == PeerState.Linking) { m.Subtitle = p.StatusText; m.Dimmed = false; }
                else if (p.State == PeerState.Failed) { m.Subtitle = p.StatusText; m.SubtitleColor = Theme.Bad; }
                else if (p.State == PeerState.Done) m.Subtitle = p.StatusText;
                list.Add(m);
            }

            if (!hub.AnyPeers)
                list.Add(new TileModel { Key = "empty", Glyph = Theme.Wifi, Title = "Looking for your computers", Width = 236, Wide = true,
                    AcceptsDrops = false, Subtitle = "Install DropDock on your other computers on this Wi-Fi.",
                    Help = "No other computers found yet. Click to see how to set one up." });
            if (!choosing)
                list.Add(new TileModel { Key = "setup", Glyph = Theme.Info, Title = "", Subtitle = "", Width = 36, Compact = true,
                    AcceptsDrops = false, Help = "Set up DropDock on another computer" });
            return list;
        }

        void Apply(List<TileModel> models)
        {
            SuspendLayout();
            var keys = models.Select(m => m.Key).ToList();
            foreach (var key in tiles.Keys.Where(k => !keys.Contains(k)).ToList())
            {
                Controls.Remove(tiles[key]);
                tiles[key].Dispose();
                tiles.Remove(key);
            }
            foreach (var m in models)
            {
                Tile tile;
                if (tiles.TryGetValue(m.Key, out tile)) tile.SetModel(m);
                else
                {
                    tile = new Tile(m, s);
                    Connect(tile, m.Key);
                    tiles[m.Key] = tile;
                    Controls.Add(tile);
                }
            }
            order = keys;
            int pad = (int)(10 * s), gap = (int)(8 * s), x = pad;
            separatorX = -1;
            for (int i = 0; i < order.Count; i++)
            {
                var tile = tiles[order[i]];
                tile.Location = new Point(x, pad);
                x += tile.Width + gap;
                if (order[i] == "general" && i < order.Count - 1)
                {
                    separatorX = x + (int)(2 * s);
                    x += (int)(13 * s);
                }
            }
            var size = new Size(x - gap + pad, (int)(84 * s) + 2 * pad);
            if (Size != size)
            {
                Size = size;
                if (Visible) PlaceOnScreen();
            }
            ResumeLayout();
            Invalidate();
        }

        void Connect(Tile tile, string key)
        {
            tile.Clicked = () => Clicked(key);
            tile.ActionClicked = () => Copy(key);
            tile.Dropped = data => Dropped(data, key);
            tile.DragEntered = () => everInside = true;
        }

        // Actions

        void Clicked(string key)
        {
            if (key == "paste")
            {
                if (pendingFiles != null) { HideBar(); return; }
                pickMode = !pickMode;
                Reload();
                return;
            }
            if (key == "general")
            {
                if (pendingFiles != null)
                {
                    var files = pendingFiles;
                    pendingFiles = null;
                    hub.SendToAll(files);
                    holdUntil = DateTime.UtcNow.AddSeconds(1.2);
                    Reload();
                }
                else if (pickMode)
                {
                    pickMode = false;
                    PasteClipboard(null);
                    Reload();
                }
                else Copy(key);
                return;
            }
            if (key == "empty" || key == "setup")
            {
                HideBar();
                SetupForm.ShowOrFocus();
                return;
            }
            var peer = hub.FindPeer(key);
            if (peer == null) return;
            if (!peer.Linked) hub.RequestLink(peer);
            else if (pendingFiles != null)
            {
                var files = pendingFiles;
                pendingFiles = null;
                hub.SendFiles(files, peer);
                holdUntil = DateTime.UtcNow.AddSeconds(1.2);
                Reload();
            }
            else if (pickMode)
            {
                pickMode = false;
                PasteClipboard(peer);
                Reload();
            }
            else if (hub.DirectClip(key) != null) Copy(key);
            else
            {
                HideBar();
                app.ChooseAndSend(peer);
            }
        }

        void PasteClipboard(Peer peer)
        {
            var clip = WinClipboard.Read();
            if (clip == null)
            {
                app.Toast("Your clipboard is empty", "Copy some text or an image first, then click Paste.", null);
                return;
            }
            hub.Share(clip, peer);
        }

        void Copy(string key)
        {
            Tile tile;
            tiles.TryGetValue(key, out tile);
            var clip = key == "general" ? hub.General : hub.DirectClip(key);
            if (clip != null) app.WriteClipboard(clip);
            if (tile != null)
                tile.Flash(clip == null ? (key == "general" ? "Nothing shared yet" : "Nothing to copy")
                    : key == "general" ? "Copied. Press Ctrl+V to paste." : "Copied");
        }

        bool Dropped(IDataObject data, string key)
        {
            var target = key == "general" ? null : hub.FindPeer(key);
            if (key != "general" && (target == null || !target.Linked)) return false;
            holdUntil = DateTime.UtcNow.AddSeconds(1.4);
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    var list = files.ToList();
                    if (target == null) hub.SendToAll(list); else hub.SendFiles(list, target);
                    return true;
                }
            }
            var clip = WinClipboard.FromData(data);
            if (clip == null) return false;
            hub.Share(clip, target);
            return true;
        }
    }
}
