using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace DropDock.Core
{
    public sealed class Identity
    {
        public string Id, Name, Os, Kind, Code, Tag;
        public byte[] Key;
    }

    /// <summary>Where DropDock keeps its files. DROPDOCK_HOME moves everything into one folder (tests).</summary>
    public static class Paths
    {
        public static readonly bool IsTest = Environment.GetEnvironmentVariable("DROPDOCK_HOME") != null;

        public static string Home
        {
            get
            {
                var h = Environment.GetEnvironmentVariable("DROPDOCK_HOME");
                if (h != null) return h;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DropDock");
            }
        }

        public static string State { get { return Path.Combine(Home, "state.json"); } }
        public static string General { get { return Path.Combine(Home, "general.json"); } }
        public static string Log { get { return Path.Combine(Home, "dropdock.log"); } }

        public static string DefaultReceive
        {
            get
            {
                if (IsTest) return Path.Combine(Home, "Received");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DropDock");
            }
        }
    }

    public sealed class Settings
    {
        public static readonly Settings Shared = new Settings();

        readonly object sync = new object();
        Dictionary<string, object> stored;
        byte[] key;
        string tag;

        public readonly string DeviceName;
        public readonly int PreferredPort;
        public string Os = Environment.GetEnvironmentVariable("DROPDOCK_OS") ?? "windows";
        public string Kind = "desktop";

        Settings()
        {
            Directory.CreateDirectory(Paths.Home);
            try
            {
                stored = Json.ParseObject(File.ReadAllText(Paths.State, Encoding.UTF8));
                if (DDCrypto.Normalize(Json.Str(stored, "groupCode")) == null || Json.Str(stored, "deviceId") == null) stored = null;
            }
            catch { stored = null; }
            if (stored == null)
            {
                stored = new Dictionary<string, object>
                {
                    { "deviceId", Guid.NewGuid().ToString().ToLowerInvariant() },
                    { "groupCode", DDCrypto.NewGroupCode() }
                };
            }
            DeviceName = Environment.GetEnvironmentVariable("DROPDOCK_NAME") ?? SafeHostName();
            int port;
            PreferredPort = int.TryParse(Environment.GetEnvironmentVariable("DROPDOCK_PORT"), out port) ? port : 47821;
            Recompute();
            Save();
        }

        static string SafeHostName()
        {
            try { return Dns.GetHostName(); } catch { return Environment.MachineName; }
        }

        void Recompute()
        {
            key = DDCrypto.GroupKey(Json.Str(stored, "groupCode"));
            tag = DDCrypto.GroupTag(key);
        }

        void Save()
        {
            try { File.WriteAllText(Paths.State, Json.Write(stored), new UTF8Encoding(false)); } catch { }
        }

        public Identity Identity
        {
            get
            {
                lock (sync)
                {
                    return new Identity
                    {
                        Id = Json.Str(stored, "deviceId"), Name = DeviceName, Os = Os, Kind = Kind,
                        Code = Json.Str(stored, "groupCode"), Key = key, Tag = tag
                    };
                }
            }
        }

        public string GroupTag { get { lock (sync) return tag; } }
        public string GroupCode { get { lock (sync) return Json.Str(stored, "groupCode"); } }

        public void SetGroupCode(string code)
        {
            lock (sync)
            {
                stored["groupCode"] = code;
                Recompute();
                Save();
            }
        }

        bool Flag(string name, bool fallback)
        {
            lock (sync)
            {
                object v;
                return stored.TryGetValue(name, out v) && v is bool ? (bool)v : fallback;
            }
        }

        void SetFlag(string name, bool value)
        {
            lock (sync) { stored[name] = value; Save(); }
        }

        public bool StartAtLogin { get { return Flag("startAtLogin", true); } set { SetFlag("startAtLogin", value); } }
        public bool AutoCopyDirect { get { return Flag("autoCopyDirect", true); } set { SetFlag("autoCopyDirect", value); } }
        public bool HoverReveal { get { return Flag("hoverReveal", true); } set { SetFlag("hoverReveal", value); } }
        public bool Welcomed { get { return Flag("welcomed", false); } set { SetFlag("welcomed", value); } }

        public string ReceiveFolder
        {
            get { lock (sync) return Json.Str(stored, "receiveFolder") ?? Paths.DefaultReceive; }
        }
    }

    public static class Log
    {
        static readonly object sync = new object();

        public static void Info(string message)
        {
            var line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " " + message + Environment.NewLine;
            if (Paths.IsTest) Console.Error.Write(line);
            lock (sync)
            {
                try
                {
                    var path = Paths.Log;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1000000)
                    {
                        var old = Path.Combine(Paths.Home, "dropdock.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
