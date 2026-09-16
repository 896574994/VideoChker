/* -*- coding: utf-8 -*-
 * MiniJson.cs — 轻量 JSON 解析器（.NET Framework 4.x，无第三方依赖）
 * 用于解析 ffprobe -of json 输出。仅实现解析，不做序列化。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoChecker
{
    /// <summary>轻量 JSON 解析器：解析为 Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null。</summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            int pos = 0;
            object value = ParseValue(text, ref pos);
            return value;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{') return ParseObject(s, ref pos);
            if (c == '[') return ParseArray(s, ref pos);
            if (c == '"') return ParseString(s, ref pos);
            if (c == 't' || c == 'f') return ParseBool(s, ref pos);
            if (c == 'n') { pos += 4; return null; }
            return ParseNumber(s, ref pos);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            pos++; // '{'
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return obj; }
            while (pos < s.Length)
            {
                SkipWs(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ':') pos++;
                object val = ParseValue(s, ref pos);
                obj[key] = val;
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == '}') { pos++; break; }
            }
            return obj;
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            List<object> list = new List<object>();
            pos++; // '['
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (pos < s.Length)
            {
                object val = ParseValue(s, ref pos);
                list.Add(val);
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; break; }
            }
            return list;
        }

        private static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') return "";
            pos++;
            StringBuilder sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"') { pos++; break; }
                if (c == '\\' && pos + 1 < s.Length)
                {
                    char n = s[pos + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 5 < s.Length)
                            {
                                string hex = s.Substring(pos + 2, 4);
                                try { sb.Append((char)int.Parse(hex, NumberStyles.HexNumber)); }
                                catch { sb.Append('?'); }
                                pos += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    pos += 2;
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && s[pos] == '-') pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-'))
                pos++;
            string num = s.Substring(start, pos - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return 0.0;
        }

        private static object ParseBool(string s, ref int pos)
        {
            if (pos + 4 <= s.Length && s.Substring(pos, 4) == "true") { pos += 4; return true; }
            if (pos + 5 <= s.Length && s.Substring(pos, 5) == "false") { pos += 5; return false; }
            return false;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n'))
                pos++;
        }

        // ---- 取值辅助 ----
        public static string GetStr(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static double? GetDbl(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v != null)
            {
                if (v is double) return (double)v;
                double d;
                if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d;
            }
            return null;
        }

        public static long? GetLong(Dictionary<string, object> obj, string key)
        {
            double? d = GetDbl(obj, key);
            if (d.HasValue) return (long)d.Value;
            return null;
        }
    }
}
