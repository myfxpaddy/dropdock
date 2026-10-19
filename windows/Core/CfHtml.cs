using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DropDock.Core
{
    /// <summary>The Windows "HTML Format" clipboard payload: a header of UTF-8 byte offsets, then the HTML.</summary>
    public static class CfHtml
    {
        /// <summary>Windows wraps clipboard HTML in a header with byte offsets. Return the plain HTML.</summary>
        public static string Extract(byte[] bytes)
        {
            if (bytes == null) return null;
            int n = bytes.Length;
            while (n > 0 && bytes[n - 1] == 0) n--;
            if (n != bytes.Length) { var t = new byte[n]; Buffer.BlockCopy(bytes, 0, t, 0, n); bytes = t; }
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 400));
            if (head.StartsWith("Version:", StringComparison.Ordinal))
            {
                int startHtml = Offset(head, "StartHTML:"), endHtml = Offset(head, "EndHTML:");
                int startFrag = Offset(head, "StartFragment:"), endFrag = Offset(head, "EndFragment:");
                if (startHtml >= 0 && endHtml > startHtml && endHtml <= bytes.Length)
                    return Encoding.UTF8.GetString(bytes, startHtml, endHtml - startHtml);
                if (startFrag >= 0 && endFrag > startFrag && endFrag <= bytes.Length)
                    return "<html><body>" + Encoding.UTF8.GetString(bytes, startFrag, endFrag - startFrag) + "</body></html>";
            }
            var all = Encoding.UTF8.GetString(bytes);
            int lt = all.IndexOf('<');
            return lt >= 0 ? all.Substring(lt) : null;
        }

        static int Offset(string head, string key)
        {
            int i = head.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += key.Length;
            int j = i;
            while (j < head.Length && (char.IsDigit(head[j]) || head[j] == '-')) j++;
            int v;
            return int.TryParse(head.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        /// <summary>Builds the "HTML Format" clipboard payload: a header with UTF-8 byte offsets, then the document.</summary>
        public static byte[] Build(string html)
        {
            const string start = "<!--StartFragment-->", end = "<!--EndFragment-->";
            string doc;
            if (html.IndexOf(start, StringComparison.OrdinalIgnoreCase) >= 0 && html.IndexOf(end, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                doc = html;
            }
            else
            {
                var bodyOpen = Regex.Match(html, "<body[^>]*>", RegexOptions.IgnoreCase);
                int bodyClose = html.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
                if (bodyOpen.Success && bodyClose > bodyOpen.Index)
                {
                    int afterOpen = bodyOpen.Index + bodyOpen.Length;
                    doc = html.Substring(0, afterOpen) + start + html.Substring(afterOpen, bodyClose - afterOpen) + end + html.Substring(bodyClose);
                }
                else
                {
                    doc = "<html><body>" + start + html + end + "</body></html>";
                }
            }
            const string headerFormat = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            int headerLen = Encoding.ASCII.GetByteCount(string.Format(CultureInfo.InvariantCulture, headerFormat, 0, 0, 0, 0));
            var docBytes = Encoding.UTF8.GetBytes(doc);
            int fragStart = headerLen + Encoding.UTF8.GetByteCount(doc.Substring(0, doc.IndexOf(start, StringComparison.OrdinalIgnoreCase) + start.Length));
            int fragEnd = headerLen + Encoding.UTF8.GetByteCount(doc.Substring(0, doc.IndexOf(end, StringComparison.OrdinalIgnoreCase)));
            var header = string.Format(CultureInfo.InvariantCulture, headerFormat, headerLen, headerLen + docBytes.Length, fragStart, fragEnd);
            var result = new byte[headerLen + docBytes.Length + 1];
            Encoding.ASCII.GetBytes(header, 0, header.Length, result, 0);
            Buffer.BlockCopy(docBytes, 0, result, headerLen, docBytes.Length);
            return result;
        }

    }
}
