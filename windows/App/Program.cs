using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DropDock.Core;
using Microsoft.Win32;

namespace DropDock.App
{
    static class Program
    {
        const string MutexName = "Local\\DropDock.Instance";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool portable = args.Contains("--portable") || Paths.IsTest;
            var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

            // Running a fresh copy (from Downloads, a USB stick or a network share) installs it for this user.
            if (!portable && !Install.IsInstalledCopy)
            {
                if (CommandPipe.Send("quit")) WaitForOtherInstance();
                try
                {
                    Install.CopySelf();
                    Process.Start(new ProcessStartInfo(Install.Exe, string.Join(" ", files.Select(f => "\"" + f + "\""))) { UseShellExecute = false });
                    return;
                }
                catch (Exception e)
                {
                    Log.Info("install failed: " + e.Message);
                    MessageBox.Show("DropDock could not install itself (" + e.Message + "). It will run from where it is for now.",
                        "DropDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    CommandPipe.Send(files.Count > 0 ? "files\n" + string.Join("\n", files) : "show");
                    return;
                }
                if (!portable && Install.IsInstalledCopy)
                {
                    Install.EnsureShortcuts();
                    Install.SetAutostart(Settings.Shared.StartAtLogin);
                }
                Log.Info("starting as " + Settings.Shared.DeviceName);
                Application.Run(new TrayApp(files));
            }
        }

        static void WaitForOtherInstance()
        {
            for (int i = 0; i < 50; i++)
            {
                bool created;
                using (var m = new Mutex(false, MutexName))
                {
                    try { created = m.WaitOne(0); } catch (AbandonedMutexException) { created = true; }
                    if (created) { m.ReleaseMutex(); return; }
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Per-user install in %LOCALAPPDATA%\Programs\DropDock: no admin rights needed.</summary>
    static class Install
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DropDock"); }
        }

        public static string Exe { get { return Path.Combine(Dir, "DropDock.exe"); } }

        static string Current { get { return Assembly.GetExecutingAssembly().Location; } }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Current), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
        }

        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DropDock.lnk"); }
        }

        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DropDock.lnk"); }
        }

        public static void CopySelf()
        {
            Directory.CreateDirectory(Dir);
            File.Copy(Current, Exe, true);
            // A downloaded copy carries a "came from the internet" mark; the user already chose to run it.
            Native.DeleteFile(Exe + ":Zone.Identifier");
        }

        public static void EnsureShortcuts()
        {
            try
            {
                if (!File.Exists(StartMenuLink)) Shortcut(StartMenuLink);
                var marker = Path.Combine(Paths.Home, "desktop-shortcut-made");
                if (!File.Exists(marker))
                {
                    Shortcut(DesktopLink);
                    File.WriteAllText(marker, "Delete the desktop shortcut whenever you like; DropDock will not put it back.");
                }
            }
            catch (Exception e) { Log.Info("shortcuts: " + e.Message); }
        }

        static void Shortcut(string path)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(type);
            var link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Exe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Dir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Send files and clips to your other computers" });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Exe + ",0" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (on) key.SetValue("DropDock", "\"" + Exe + "\" --background");
                    else key.DeleteValue("DropDock", false);
                }
            }
            catch (Exception e) { Log.Info("autostart: " + e.Message); }
        }

        public static void Uninstall()
        {
            SetAutostart(false);
            try { File.Delete(StartMenuLink); } catch { }
            try { File.Delete(DesktopLink); } catch { }
            // The exe cannot delete itself while running, so a short-lived cmd removes it after we exit.
            var cmd = "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Dir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }

    /// <summary>Lets a second launch (a desktop shortcut, or files dropped on it) talk to the running copy.</summary>
    static class CommandPipe
    {
        static string Name { get { return "DropDock.Command." + Environment.UserName; } }

        public static bool Send(string message)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", Name, PipeDirection.Out))
                {
                    c.Connect(1500);
                    var b = Encoding.UTF8.GetBytes(message);
                    c.Write(b, 0, b.Length);
                    c.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        public static void Listen(Action<string> onMessage)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte))
                        {
                            server.WaitForConnection();
                            using (var reader = new StreamReader(server, Encoding.UTF8)) onMessage(reader.ReadToEnd());
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Info("pipe: " + e.Message);
                        Thread.Sleep(1000);
                    }
                }
            }) { IsBackground = true, Name = "DropDock commands" };
            t.Start();
        }
    }
}
