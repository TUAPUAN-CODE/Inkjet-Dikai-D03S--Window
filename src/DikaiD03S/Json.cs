using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Dikai.App
{
    /// <summary>JSON ขนาดเล็ก (ไม่พึ่ง library) — พอสำหรับ API ระหว่างหน้าจอกับโปรแกรม</summary>
    internal static class Json
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object o, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("JSON ซ้อนลึกเกินไป");
            switch (o)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case char c:
                    WriteString(sb, c.ToString());
                    return;
                case DateTime dt:
                    WriteString(sb, dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case IConvertible conv when IsInteger(o):
                    sb.Append(conv.ToString(CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, CamelCase(p.Name));
                        sb.Append(':');
                        Write(sb, p.GetValue(o, null), depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
            }
        }

        private static bool IsInteger(object o)
        {
            return o is int || o is long || o is short || o is byte || o is uint || o is ulong || o is ushort || o is sbyte;
        }

        private static string CamelCase(string name)
        {
            return name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    case '&': sb.Append("\\u0026"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ parse

        public static object Parse(string text)
        {
            var p = new Parser(text ?? "");
            p.SkipWs();
            if (p.End) return null;
            var v = p.Value(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("JSON มีข้อมูลเกิน");
            return v;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public bool End => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object Value(int depth)
            {
                if (depth > 64) throw new FormatException("JSON ซ้อนลึกเกินไป");
                SkipWs();
                if (End) throw new FormatException("JSON จบก่อนกำหนด");
                char c = _s[_i];
                if (c == '{') return Obj(depth);
                if (c == '[') return Arr(depth);
                if (c == '"') return Str();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                return Num();
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
                _i += word.Length;
                return true;
            }

            private Dictionary<string, object> Obj(int depth)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++;
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return d;
                }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw new FormatException("JSON: ต้องมีชื่อ field");
                    var key = Str();
                    SkipWs();
                    Expect(':');
                    d[key] = Value(depth + 1);
                    SkipWs();
                    if (Peek(',')) continue;
                    Expect('}');
                    return d;
                }
            }

            private List<object> Arr(int depth)
            {
                var l = new List<object>();
                _i++;
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return l;
                }
                while (true)
                {
                    l.Add(Value(depth + 1));
                    SkipWs();
                    if (Peek(',')) continue;
                    Expect(']');
                    return l;
                }
            }

            private bool Peek(char c)
            {
                if (_i < _s.Length && _s[_i] == c)
                {
                    _i++;
                    return true;
                }
                return false;
            }

            private void Expect(char c)
            {
                if (!Peek(c)) throw new FormatException("JSON: ต้องมี '" + c + "'");
            }

            private string Str()
            {
                var sb = new StringBuilder();
                _i++; // "
                while (true)
                {
                    if (End) throw new FormatException("JSON: string ไม่ปิด");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (End) throw new FormatException("JSON: escape ไม่ครบ");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new FormatException("JSON: \\u ไม่ครบ");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw new FormatException("JSON: escape ไม่ถูกต้อง");
                    }
                }
            }

            private object Num()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                var t = _s.Substring(start, _i - start);
                if (t.Length == 0) throw new FormatException("JSON: ค่าไม่ถูกต้อง");
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>อ่าน field จาก JSON object แบบปลอดภัย</summary>
    internal sealed class JObj
    {
        private readonly Dictionary<string, object> _d;

        public JObj(object parsed)
        {
            _d = parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public bool Has(string key) => _d.ContainsKey(key) && _d[key] != null;

        public string Str(string key, string def = null)
        {
            return _d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        }

        public int Int(string key, int def = 0)
        {
            if (!_d.TryGetValue(key, out var v) || v == null) return def;
            if (v is double d) return (int)Math.Round(d);
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
        }

        public bool Bool(string key, bool def = false)
        {
            return _d.TryGetValue(key, out var v) && v is bool b ? b : def;
        }

        public List<JObj> Objects(string key)
        {
            var list = new List<JObj>();
            if (_d.TryGetValue(key, out var v) && v is List<object> l)
            {
                foreach (var item in l) list.Add(new JObj(item));
            }
            return list;
        }
    }
}
