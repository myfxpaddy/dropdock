using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DropDock.Core
{
    /// <summary>
    /// A small JSON reader and writer, so the Windows build stays a single file with no extra DLLs.
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers long or double.
    /// </summary>
    public static class Json
    {
        public static Dictionary<string, object> ParseObject(string text)
        {
            var p = new Parser(text);
            var value = p.Value();
            p.SkipSpace();
            if (!p.AtEnd) throw new FormatException("Trailing data after JSON");
            var obj = value as Dictionary<string, object>;
            if (obj == null) throw new FormatException("JSON is not an object");
            return obj;
        }

        public static Dictionary<string, object> ParseObject(byte[] utf8)
        {
            return ParseObject(Encoding.UTF8.GetString(utf8));
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static byte[] WriteBytes(object value)
        {
            return Encoding.UTF8.GetBytes(Write(value));
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v as string : null;
        }

        public static long Long(Dictionary<string, object> o, string key, long fallback = 0)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is int) return (int)v;
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static byte[] Bytes(Dictionary<string, object> o, string key)
        {
            var s = Str(o, key);
            if (s == null) return null;
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is ushort || v is uint || v is byte)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float)
            {
                sb.Append(Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            int i;

            public Parser(string text) { s = text; }

            public bool AtEnd { get { return i >= s.Length; } }

            public void SkipSpace()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            }

            public object Value()
            {
                SkipSpace();
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return Num();
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Bad JSON literal");
                i += word.Length;
            }

            Dictionary<string, object> Obj()
            {
                var o = new Dictionary<string, object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipSpace();
                    if (i >= s.Length || s[i] != '"') throw new FormatException("Expected key");
                    string key = Str();
                    SkipSpace();
                    if (i >= s.Length || s[i] != ':') throw new FormatException("Expected colon");
                    i++;
                    o[key] = Value();
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("Unclosed object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("Expected , or }");
                }
            }

            List<object> Arr()
            {
                var a = new List<object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value());
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("Unclosed array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("Expected , or ]");
                }
            }

            string Str()
            {
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new FormatException("Unclosed string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw new FormatException("Bad escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("Bad escape");
                    }
                }
            }

            object Num()
            {
                int start = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                bool isDouble = false;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c >= '0' && c <= '9') { i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') { isDouble = true; i++; continue; }
                    break;
                }
                string t = s.Substring(start, i - start);
                if (t.Length == 0) throw new FormatException("Bad JSON value");
                long l;
                if (!isDouble && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
