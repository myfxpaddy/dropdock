using System;
using System.Collections.Generic;

namespace DropDock.Core
{
    /// <summary>A piece of copied content in every format we could read, so formatting survives the trip.</summary>
    public sealed class Clip
    {
        public const int MaxImageBytes = 20 << 20;

        public string Id = Guid.NewGuid().ToString().ToLowerInvariant();
        public string Scope = "general";
        public string From = "";
        public string FromName = "";
        public long Time = NowMs();
        public string Text;
        public string Html;
        public byte[] Rtf;
        public byte[] Png;

        public static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        public bool IsEmpty { get { return Text == null && Html == null && Rtf == null && Png == null; } }

        /// <summary>One short line for tiles and menus.</summary>
        public string Preview
        {
            get
            {
                var t = Text == null ? null : Text.Trim();
                if (!string.IsNullOrEmpty(t))
                {
                    int nl = t.IndexOfAny(new[] { '\r', '\n' });
                    var line = nl >= 0 ? t.Substring(0, nl) : t;
                    return line.Length > 80 ? line.Substring(0, 80) + "..." : line;
                }
                if (Png != null) return "Image";
                if (Html != null || Rtf != null) return "Formatted text";
                return "Empty";
            }
        }

        public Clip Copy()
        {
            return (Clip)MemberwiseClone();
        }

        public Dictionary<string, object> ToJson()
        {
            var o = new Dictionary<string, object>
            {
                { "id", Id }, { "scope", Scope }, { "from", From }, { "fromName", FromName }, { "time", Time }
            };
            if (Text != null) o["text"] = Text;
            if (Html != null) o["html"] = Html;
            if (Rtf != null) o["rtf"] = Convert.ToBase64String(Rtf);
            if (Png != null) o["png"] = Convert.ToBase64String(Png);
            return o;
        }

        public static Clip FromJson(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var c = new Clip
            {
                Id = Json.Str(o, "id") ?? Guid.NewGuid().ToString().ToLowerInvariant(),
                Scope = Json.Str(o, "scope") == "direct" ? "direct" : "general",
                From = Json.Str(o, "from") ?? "",
                FromName = Json.Str(o, "fromName") ?? "",
                Time = Json.Long(o, "time"),
                Text = Json.Str(o, "text"),
                Html = Json.Str(o, "html"),
                Rtf = Json.Bytes(o, "rtf"),
                Png = Json.Bytes(o, "png")
            };
            return c.IsEmpty ? null : c;
        }
    }
}
