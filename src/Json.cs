// Minimal dependency-free JSON reader (objects -> Dictionary<string,object>,
// arrays -> List<object>, numbers -> double, plus string/bool/null).
// Kept small on purpose so the whole app is one csc invocation with no extra
// assembly references.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BiliGetter
{
    internal static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || s.Substring(i, word.Length) != word)
                throw new FormatException("bad literal at " + i);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                object val = ParseValue(s, ref i);
                list.Add(val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            string tok = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number '" + tok + "'");
            return d;
        }

        // ------------------------------------------------------------- accessors
        public static Dictionary<string, object> AsObj(object o) { return o as Dictionary<string, object>; }
        public static List<object> AsList(object o) { return o as List<object>; }

        public static object Member(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        /// Walks a chain of object keys, returning null as soon as one is absent.
        public static object Path(object o, params string[] keys)
        {
            object cur = o;
            for (int i = 0; i < keys.Length; i++)
            {
                cur = Member(cur, keys[i]);
                if (cur == null) return null;
            }
            return cur;
        }

        public static string Str(object o, params string[] keys)
        {
            object v = Path(o, keys);
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "true" : "false";
            return v.ToString();
        }

        public static long Num(object o, long def, params string[] keys)
        {
            object v = Path(o, keys);
            if (v is double) return (long)(double)v;
            if (v is string)
            {
                double d;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d;
            }
            return def;
        }

        public static bool Flag(object o, bool def, params string[] keys)
        {
            object v = Path(o, keys);
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            return def;
        }

        /// Always returns a (possibly empty) list, so callers need no null checks.
        public static List<object> Arr(object o, params string[] keys)
        {
            var l = Path(o, keys) as List<object>;
            return l ?? new List<object>();
        }
    }
}
