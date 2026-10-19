using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace DropDock.App
{
    /// <summary>Everything needed to put DropDock on another computer. The prompts live in install/*.txt.</summary>
    static class Setup
    {
        public const string Guide = "https://github.com/myfxpaddy/dropdock/blob/main/INSTALL.md";
        public const string MacCommand = "curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash";
        public const string WindowsCommand = "irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex";

        public static string Prompt(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".txt"))
            {
                if (stream == null) return "Install DropDock on this computer by following the guide at " + Guide + ".";
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
        }
    }

    /// <summary>"Set Up Another Computer": a Mac card and a Windows card, each with a prompt for Claude Code and a command.</summary>
    sealed class SetupForm : Form
    {
        static SetupForm open;

        public static void ShowOrFocus()
        {
            if (open == null || open.IsDisposed) open = new SetupForm();
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        SetupForm()
        {
            Text = "Set Up Another Computer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = new Font("Segoe UI", 9.75f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = TrayApp.AppIcon(SystemInformation.IconSize);

            var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));

            var title = new Label { Text = "Put DropDock on your other computers", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 6) };
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);
            var intro = new Label
            {
                Text = "On the other computer, open Claude Code and paste the prompt for that kind of computer. Claude installs DropDock, checks it works, and tells you what to click. Or run the one-line command yourself in Terminal (Mac) or PowerShell (Windows).",
                AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 12)
            };
            root.Controls.Add(intro, 0, 1);
            root.SetColumnSpan(intro, 2);

            root.Controls.Add(Card(Theme.Laptop, "Mac", "macOS 13 or newer, Apple Silicon or Intel", Setup.Prompt("prompt-mac"), Setup.MacCommand, "Copy Terminal Command"), 0, 2);
            root.Controls.Add(Card(Theme.Pc, "Windows", "Windows 10 or 11, no admin rights needed", Setup.Prompt("prompt-windows"), Setup.WindowsCommand, "Copy PowerShell Command"), 1, 2);

            var after = new Label
            {
                Text = "Once it is installed there, open the DropDock bar on that computer, click this computer (marked \"Click to link\"), check both screens show the same six-digit code, and click Allow here.",
                AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(0, 14, 0, 10)
            };
            root.Controls.Add(after, 0, 3);
            root.SetColumnSpan(after, 2);
            var guide = new LinkLabel { Text = "Open the full setup guide", AutoSize = true };
            guide.LinkClicked += (s, e) => { try { Process.Start(Setup.Guide); } catch { } };
            root.Controls.Add(guide, 0, 4);
            Controls.Add(root);
        }

        static Control Card(string glyph, string name, string detail, string prompt, string command, string commandTitle)
        {
            var card = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
                Margin = new Padding(0, 0, 12, 0), BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window, Dock = DockStyle.Fill
            };
            var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            header.Controls.Add(new Label { Text = glyph, Font = new Font(Theme.GlyphFont, 20f), AutoSize = true, Margin = new Padding(0, 2, 8, 0) });
            var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            names.Controls.Add(new Label { Text = name, Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Margin = Padding.Empty });
            names.Controls.Add(new Label { Text = detail, ForeColor = SystemColors.GrayText, AutoSize = true, Margin = Padding.Empty });
            header.Controls.Add(names);
            card.Controls.Add(header);
            card.Controls.Add(new Label { Text = "Prompt for Claude Code on that " + (name == "Mac" ? "Mac" : "PC"), Font = new Font("Segoe UI Semibold", 9.75f), AutoSize = true });
            var text = new TextBox
            {
                Text = prompt, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Font = new Font("Consolas", 9f), Size = new Size(370, 260), BackColor = SystemColors.Window, AccessibleName = name + " prompt for Claude Code"
            };
            card.Controls.Add(text);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
            buttons.Controls.Add(CopyButton("Copy Prompt", prompt));
            buttons.Controls.Add(CopyButton(commandTitle, command));
            card.Controls.Add(buttons);
            card.Controls.Add(new Label { Text = command, AutoSize = true, MaximumSize = new Size(370, 0), Font = new Font("Consolas", 8f), ForeColor = SystemColors.GrayText });
            return card;
        }

        static Button CopyButton(string title, string value)
        {
            var b = new Button { Text = title, AutoSize = true, MinimumSize = new Size(0, 30) };
            var reset = new Timer { Interval = 1500 };
            reset.Tick += (s, e) => { reset.Stop(); b.Text = title; };
            b.Click += (s, e) =>
            {
                try { Clipboard.SetText(value); } catch { }
                b.Text = "Copied";
                reset.Start();
            };
            return b;
        }
    }
}
