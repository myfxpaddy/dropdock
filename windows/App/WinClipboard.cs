using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DropDock.Core;

namespace DropDock.App
{
    /// <summary>Reads and writes the clipboard in plain text, HTML, RTF and PNG so formatting survives.</summary>
    static class WinClipboard
    {
        public static Clip Read()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                return data == null ? null : FromData(data);
            }
            catch (ExternalException) { return null; }
        }

        public static bool HasDroppableData(IDataObject d)
        {
            return d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.UnicodeText) ||
                   d.GetDataPresent(DataFormats.Html) || d.GetDataPresent(DataFormats.Rtf) || d.GetDataPresent(DataFormats.Bitmap);
        }

        public static Clip FromData(IDataObject d)
        {
            var c = new Clip();
            try
            {
                if (d.GetDataPresent(DataFormats.UnicodeText)) c.Text = d.GetData(DataFormats.UnicodeText) as string;
                else if (d.GetDataPresent(DataFormats.Text)) c.Text = d.GetData(DataFormats.Text) as string;
                if (d.GetDataPresent(DataFormats.Html))
                {
                    var h = d.GetData(DataFormats.Html);
                    var hs = h as string;
                    var hm = h as MemoryStream;
                    c.Html = CfHtml.Extract(hs != null ? Encoding.UTF8.GetBytes(hs) : hm != null ? hm.ToArray() : null);
                }
                if (d.GetDataPresent(DataFormats.Rtf))
                {
                    var r = d.GetData(DataFormats.Rtf);
                    var s = r as string;
                    var ms = r as MemoryStream;
                    if (s != null) c.Rtf = Encoding.Default.GetBytes(s.TrimEnd('\0'));
                    else if (ms != null) c.Rtf = TrimNul(ms.ToArray());
                }
                if (c.Text == null && c.Html == null && c.Rtf == null)
                {
                    var png = d.GetDataPresent("PNG") ? d.GetData("PNG") as MemoryStream : null;
                    if (png != null) c.Png = png.ToArray();
                    else if (d.GetDataPresent(DataFormats.Bitmap))
                    {
                        var img = d.GetData(DataFormats.Bitmap) as Image;
                        if (img != null)
                            using (var outStream = new MemoryStream())
                            {
                                img.Save(outStream, ImageFormat.Png);
                                c.Png = outStream.ToArray();
                            }
                    }
                    if (c.Png != null && c.Png.Length > Clip.MaxImageBytes) c.Png = null;
                }
            }
            catch (ExternalException) { }
            return c.IsEmpty ? null : c;
        }

        public static void Write(Clip clip)
        {
            var obj = new DataObject();
            var text = clip.Text;
            if (text == null && clip.Html != null) text = HtmlToText(clip.Html);
            if (text == null && clip.Rtf != null)
            {
                try { using (var box = new RichTextBox { Rtf = Encoding.Default.GetString(clip.Rtf) }) text = box.Text; } catch { }
            }
            if (text != null) obj.SetData(DataFormats.UnicodeText, true, text);
            if (clip.Html != null) obj.SetData(DataFormats.Html, false, new MemoryStream(CfHtml.Build(clip.Html)));
            if (clip.Rtf != null) obj.SetData(DataFormats.Rtf, false, Encoding.Default.GetString(clip.Rtf));
            if (clip.Png != null)
            {
                try
                {
                    using (var ms = new MemoryStream(clip.Png))
                    using (var img = Image.FromStream(ms))
                        obj.SetData(DataFormats.Bitmap, true, new Bitmap(img));
                    obj.SetData("PNG", false, new MemoryStream(clip.Png));
                }
                catch (ArgumentException) { }
            }
            try { Clipboard.SetDataObject(obj, true, 5, 100); }
            catch (ExternalException e) { Log.Info("clipboard busy: " + e.Message); }
        }

        static byte[] TrimNul(byte[] b)
        {
            int n = b.Length;
            while (n > 0 && b[n - 1] == 0) n--;
            if (n == b.Length) return b;
            var r = new byte[n];
            Buffer.BlockCopy(b, 0, r, 0, n);
            return r;
        }

        static string HtmlToText(string html)
        {
            var s = Regex.Replace(html, "<(script|style|head)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<br\\s*/?>|</p>|</div>|</li>|</h[1-6]>|</tr>", "\r\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            return WebUtility.HtmlDecode(s).Trim();
        }
    }
}
