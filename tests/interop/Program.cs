using System;
using System.IO;
using System.Linq;
using System.Threading;
using DropDock.Core;

// Same commands as the Mac app's --cli mode, but running the Windows core:
//   serve | info | set-code <code> | send <host> <port> <files...> | clip <host> <port> <scope> <text> [html] | link <host> <port>
sealed class ConsolePlatform : IPlatform
{
    readonly object sync = new object();
    public void Post(Action action) { lock (sync) action(); }
    public void Toast(string title, string body, Action onClick) { Console.WriteLine("toast: " + title + " | " + body); }
    public bool ConfirmLink(string name, string code) { return false; }
    public void WriteClipboard(Clip clip) { File.WriteAllText(Path.Combine(Paths.Home, "clipboard.json"), Json.Write(clip.ToJson())); }
    public void Changed() { }
}

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            switch (args[0])
            {
                case "serve":
                    var hub = new Hub(new ConsolePlatform());
                    hub.Start();
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                case "info":
                    var me = Settings.Shared.Identity;
                    Console.WriteLine(Json.Write(new System.Collections.Generic.Dictionary<string, object>
                        { { "id", me.Id }, { "name", me.Name }, { "tag", me.Tag }, { "code", me.Code } }));
                    return 0;
                case "set-code":
                    var code = DDCrypto.Normalize(args[1]);
                    if (code == null) return Usage();
                    Settings.Shared.SetGroupCode(code);
                    Console.WriteLine("tag " + Settings.Shared.GroupTag);
                    return 0;
                case "send":
                    var m = Manifest.Build(args.Skip(3));
                    var saved = Client.SendFiles(m, args[1], int.Parse(args[2]), null);
                    Console.WriteLine("saved " + string.Join(", ", saved));
                    return 0;
                case "clip":
                    var id = Settings.Shared.Identity;
                    var clip = new Clip { Scope = args[3] == "direct" ? "direct" : "general", From = id.Id, FromName = id.Name, Text = args[4] };
                    if (args.Length > 5) clip.Html = args[5];
                    Client.SendClip(clip, args[1], int.Parse(args[2]));
                    Console.WriteLine("ok");
                    return 0;
                case "selftest":
                    return SelfTest();
                case "link":
                    var got = Client.RequestLink(args[1], int.Parse(args[2]), sas => Console.WriteLine("code " + sas));
                    Settings.Shared.SetGroupCode(got);
                    Console.WriteLine("linked, tag " + Settings.Shared.GroupTag);
                    return 0;
                default:
                    return Usage();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    /// <summary>Checks the Windows clipboard HTML format and the crypto helpers against fixed expectations.</summary>
    static int SelfTest()
    {
        int failures = 0;
        Action<bool, string> check = (ok, what) => { Console.WriteLine((ok ? "pass " : "FAIL ") + what); if (!ok) failures++; };
        var utf8 = System.Text.Encoding.UTF8;

        // A full document like the Mac sends, with non-ASCII text before and inside the fragment.
        var doc = "<html><head><meta charset=\"utf-8\"><title>Caf\u00e9</title></head><body style=\"x\"><h1>R\u00e9sum\u00e9 \u65e5\u672c</h1><p><b>Bold</b> &amp; <i>italic</i></p></body></html>";
        var cf = CfHtml.Build(doc);
        var head = System.Text.Encoding.ASCII.GetString(cf, 0, 105);
        Func<string, int> num = key => int.Parse(head.Substring(head.IndexOf(key) + key.Length, 10));
        int sh = num("StartHTML:"), eh = num("EndHTML:"), sf = num("StartFragment:"), ef = num("EndFragment:");
        check(head.StartsWith("Version:0.9\r\nStartHTML:"), "header layout");
        check(sh == 105, "StartHTML points just past the header (" + sh + ")");
        check(eh == cf.Length - 1 && cf[cf.Length - 1] == 0, "EndHTML is the end of the document, followed by a NUL");
        check(utf8.GetString(cf, sf, ef - sf) == "<h1>R\u00e9sum\u00e9 \u65e5\u672c</h1><p><b>Bold</b> &amp; <i>italic</i></p>", "fragment offsets are UTF-8 byte offsets around the body");
        check(utf8.GetString(cf, sf - 20, 20) == "<!--StartFragment-->" && utf8.GetString(cf, ef, 18) == "<!--EndFragment-->", "fragment markers sit at the offsets");
        var back = CfHtml.Extract(cf);
        check(back != null && back.Contains("<h1>R\u00e9sum\u00e9 \u65e5\u672c</h1>") && back.StartsWith("<html>"), "extract returns the HTML document");

        // A bare fragment gets wrapped.
        var frag = CfHtml.Build("<b>hi \u00fc</b>");
        var fh = System.Text.Encoding.ASCII.GetString(frag, 0, 105);
        int fsf = int.Parse(fh.Substring(fh.IndexOf("StartFragment:") + 14, 10)), fef = int.Parse(fh.Substring(fh.IndexOf("EndFragment:") + 12, 10));
        check(utf8.GetString(frag, fsf, fef - fsf) == "<b>hi \u00fc</b>", "bare fragment wrapped with correct offsets");

        // What Word or a browser on Windows puts on the clipboard: fragment-only offsets (StartHTML -1).
        var winBody = "<html><body><!--StartFragment--><table><tr><td>\u20ac 5</td></tr></table><!--EndFragment--></body></html>";
        var winHead = "Version:1.0\r\nStartHTML:-1\r\nEndHTML:-1\r\n";
        var prefix = winHead + "StartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        int s0 = utf8.GetByteCount(prefix) + utf8.GetByteCount("<html><body><!--StartFragment-->");
        int e0 = s0 + utf8.GetByteCount("<table><tr><td>\u20ac 5</td></tr></table>");
        var win = utf8.GetBytes(winHead + "StartFragment:" + s0.ToString("D10") + "\r\nEndFragment:" + e0.ToString("D10") + "\r\n" + winBody);
        var got = CfHtml.Extract(win);
        check(got == "<html><body><table><tr><td>\u20ac 5</td></tr></table></body></html>", "Windows fragment-only HTML extracted");

        // Already-marked HTML (a Windows clip coming back) is not marked twice.
        var again = utf8.GetString(CfHtml.Build(winBody));
        check(again.Split(new[] { "<!--StartFragment-->" }, StringSplitOptions.None).Length == 2, "no double fragment markers");

        // Group code handling.
        check(DDCrypto.Normalize("abcde-fghjk-mnpqr-stvwx") == "ABCDEFGHJKMNPQRSTVWX", "code normalizes");
        check(DDCrypto.Normalize("O1IL0-00000-00000-00000") == "01110000000000000000", "O/I/L read as 0/1/1");
        check(DDCrypto.Normalize("too short") == null, "bad code rejected");
        check(Receiver.Clean("../etc/passwd") == null && Receiver.Clean("a//b") == null, "path escapes rejected");
        var cleaned = Receiver.Clean("Notes: draft?/CON.txt");
        check(cleaned != null && cleaned[0] == "Notes_ draft_" && cleaned[1] == "_CON.txt", "Windows-illegal names cleaned");

        Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: interop serve | info | set-code <code> | send <host> <port> <files> | clip <host> <port> <scope> <text> [html] | link <host> <port>");
        return 2;
    }
}
