using System;
using System.Drawing;
using System.Windows.Forms;
using DropDock.Core;

namespace DropDock.App
{
    /// <summary>Asks before another computer joins this one's group. Says no by itself after 90 seconds.</summary>
    sealed class LinkPrompt : Form
    {
        readonly Timer timeout = new Timer { Interval = 90000 };

        public LinkPrompt(string name, string code)
        {
            Text = "Link a computer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = new Font("Segoe UI", 9.75f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = TrayApp.AppIcon(SystemInformation.IconSize);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20) };
            layout.Controls.Add(new Label
            {
                Text = "Link " + name + " with this computer?", AutoSize = true, Font = new Font("Segoe UI Semibold", 12f),
                Margin = new Padding(0, 0, 0, 8)
            });
            layout.Controls.Add(new Label
            {
                Text = name + " will be able to send files and clips to this computer, and this computer to it.\r\nOnly allow it if " + name + " shows this code:",
                AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 8)
            });
            layout.Controls.Add(new Label { Text = code, AutoSize = true, Font = new Font("Segoe UI Semibold", 22f), Margin = new Padding(0, 0, 0, 16) });
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            var deny = new Button { Text = "Don't Allow", DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new Size(110, 32) };
            var allow = new Button { Text = "Allow", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(110, 32) };
            buttons.Controls.Add(deny);
            buttons.Controls.Add(allow);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            CancelButton = deny;
            timeout.Tick += (s, e) => { timeout.Stop(); DialogResult = DialogResult.No; };
            Shown += (s, e) => { timeout.Start(); Activate(); deny.Focus(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timeout.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class EnterKeyDialog : Form
    {
        readonly TextBox box = new TextBox { Width = 300, CharacterCasing = CharacterCasing.Upper };
        readonly Label error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Visible = false, MaximumSize = new Size(380, 0) };
        public string Code { get; private set; }

        public EnterKeyDialog()
        {
            Text = "Enter a Link Key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = new Font("Segoe UI", 9.75f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = TrayApp.AppIcon(SystemInformation.IconSize);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20) };
            layout.Controls.Add(new Label
            {
                Text = "Copy the key from another computer's DropDock menu (Link Key > Copy Link Key). This computer will join that computer's group.",
                AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 10)
            });
            box.AccessibleName = "Link key";
            layout.Controls.Add(box);
            layout.Controls.Add(error);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 30) };
            var ok = new Button { Text = "Link", AutoSize = true, MinimumSize = new Size(96, 30) };
            ok.Click += (s, e) =>
            {
                Code = DDCrypto.Normalize(box.Text);
                if (Code != null) { DialogResult = DialogResult.OK; return; }
                error.Text = "That key is not valid. A link key has 20 letters and numbers, like ABCDE-12345-FGHJK-67890.";
                error.Visible = true;
                box.Focus();
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) =>
            {
                Native.SendMessage(box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, "XXXXX-XXXXX-XXXXX-XXXXX");
                Activate();
                box.Focus();
            };
        }
    }
}
