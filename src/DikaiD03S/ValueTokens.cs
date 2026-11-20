using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dikai.App
{
    /// <summary>
    /// ตัวแทนวันเวลาในช่องค่าตัวแปร: พิมพ์ <c>{dd/MM/yyyy}</c> แล้วโปรแกรมแปลงเป็นวันที่ของคอมพิวเตอร์ตอนกดส่ง
    /// ทำให้ส่งวันผลิต/วันหมดอายุ/รหัสวันที่ ไปเป็นค่าตัวแปรได้เลย ไม่ต้องตั้งโค้ดวันที่ที่เครื่องพิมพ์เอง
    ///
    /// รูปแบบ: <c>{รูปแบบ}</c> หรือ <c>{รูปแบบ+ระยะ}</c> เช่น <c>{dd/MM/yyyy+6M}</c> <c>{dd/MM/yyyy+1y-1d}</c>
    ///   yyyy ปี ค.ศ. 4 หลัก · yy 2 หลัก · BBBB ปี พ.ศ. 4 หลัก · BB 2 หลัก · MM เดือน 01–12 · MMM เดือนอังกฤษ (NOV)
    ///   dd วัน · HH ชั่วโมง 00–23 · mm นาที · ss วินาที · JJJ วันที่ของปี 001–366
    ///   ระยะ: +N/-N ตามด้วย d วัน · w สัปดาห์ · M เดือน · y ปี · h ชั่วโมง · m นาที · s วินาที
    /// <c>{{</c> และ <c>}}</c> = ตัวอักษร { และ }
    /// ตั้งใจให้เข้มงวด: ตัวอักษรอังกฤษที่ไม่รู้จัก (เช่น DD, YYYY) หรือ mm ที่น่าจะหมายถึงเดือน จะแจ้ง error แทนที่จะพิมพ์ผิดลงบรรจุภัณฑ์
    /// </summary>
    internal static class ValueTokens
    {
        private static readonly string[] MonthAbbr = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        private static readonly Regex Spec = new Regex(@"^(?<fmt>.*?)(?<off>(?:[+-]\d+[dwMymhs])*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex Offset = new Regex(@"([+-])(\d+)([dwMymhs])", RegexOptions.CultureInvariant);

        public static bool HasTokens(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('{') >= 0 || value.IndexOf('}') >= 0);
        }

        /// <exception cref="ArgumentException">รูปแบบไม่ถูกต้อง (ข้อความเป็นภาษาไทย บอกว่าผิดตรงไหน)</exception>
        public static string Expand(string value, DateTime now)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            if (!HasTokens(value)) return value;
            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                char ch = value[i];
                if (ch == '{')
                {
                    if (i + 1 < value.Length && value[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                    int end = value.IndexOf('}', i + 1);
                    if (end < 0) throw new ArgumentException("\"{\" ไม่มี \"}\" ปิด (ถ้าต้องการพิมพ์ตัวอักษร { ให้ใส่ {{ )");
                    sb.Append(Format(value.Substring(i + 1, end - i - 1), now));
                    i = end + 1;
                }
                else if (ch == '}')
                {
                    if (i + 1 < value.Length && value[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                    throw new ArgumentException("มี \"}\" โดยไม่มี \"{\" นำหน้า (ถ้าต้องการพิมพ์ตัวอักษร } ให้ใส่ }} )");
                }
                else
                {
                    sb.Append(ch);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static string Format(string spec, DateTime now)
        {
            var m = Spec.Match(spec);
            string fmt = m.Groups["fmt"].Value;
            if (fmt.Length == 0) throw new ArgumentException("{" + spec + "} ยังไม่ได้ระบุรูปแบบ เช่น {dd/MM/yyyy}");

            DateTime t = now;
            try
            {
                foreach (Match o in Offset.Matches(m.Groups["off"].Value))
                {
                    if (!int.TryParse(o.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > 100000)
                        throw new ArgumentException("ระยะเวลาใน {" + spec + "} มากเกินไป");
                    if (o.Groups[1].Value == "-") n = -n;
                    switch (o.Groups[3].Value[0])
                    {
                        case 'd': t = t.AddDays(n); break;
                        case 'w': t = t.AddDays(7.0 * n); break;
                        case 'M': t = t.AddMonths(n); break;
                        case 'y': t = t.AddYears(n); break;
                        case 'h': t = t.AddHours(n); break;
                        case 'm': t = t.AddMinutes(n); break;
                        case 's': t = t.AddSeconds(n); break;
                    }
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentException("ระยะเวลาใน {" + spec + "} ทำให้วันที่เกินช่วงที่รองรับ");
            }

            // mm = นาที: ถ้าใช้ mm โดยไม่มี HH/ss แทบแน่ว่าพิมพ์ผิดจะหมายถึงเดือน (MM) — กันพิมพ์ผิดลงสินค้า
            if (fmt.Contains("mm") && !fmt.Contains("HH") && !fmt.Contains("ss"))
                throw new ArgumentException("{" + spec + "}: mm คือ \"นาที\" ถ้าต้องการเดือนให้ใช้ MM (ตัวพิมพ์ใหญ่)");

            var sb = new StringBuilder();
            bool any = false;
            for (int k = 0; k < fmt.Length;)
            {
                string rest = fmt.Substring(k);
                if (rest.StartsWith("yyyy", StringComparison.Ordinal)) { sb.Append(t.Year.ToString("D4", CultureInfo.InvariantCulture)); k += 4; any = true; }
                else if (rest.StartsWith("yy", StringComparison.Ordinal)) { sb.Append((t.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("BBBB", StringComparison.Ordinal)) { sb.Append((t.Year + 543).ToString("D4", CultureInfo.InvariantCulture)); k += 4; any = true; }
                else if (rest.StartsWith("BB", StringComparison.Ordinal)) { sb.Append(((t.Year + 543) % 100).ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("MMM", StringComparison.Ordinal)) { sb.Append(MonthAbbr[t.Month - 1]); k += 3; any = true; }
                else if (rest.StartsWith("MM", StringComparison.Ordinal)) { sb.Append(t.Month.ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("dd", StringComparison.Ordinal)) { sb.Append(t.Day.ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("HH", StringComparison.Ordinal)) { sb.Append(t.Hour.ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("mm", StringComparison.Ordinal)) { sb.Append(t.Minute.ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("ss", StringComparison.Ordinal)) { sb.Append(t.Second.ToString("D2", CultureInfo.InvariantCulture)); k += 2; any = true; }
                else if (rest.StartsWith("JJJ", StringComparison.Ordinal)) { sb.Append(t.DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); k += 3; any = true; }
                else
                {
                    char c = fmt[k];
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        throw new ArgumentException("{" + spec + "}: ไม่รู้จัก \"" + c + "\" (ใช้ yyyy yy BBBB BB MM MMM dd HH mm ss JJJ — ตัวพิมพ์ใหญ่-เล็กต้องตรง)");
                    sb.Append(c);
                    k++;
                }
            }
            if (!any) throw new ArgumentException("{" + spec + "} ไม่มีตัวแทนวันเวลา เช่น {dd/MM/yyyy}");
            return sb.ToString();
        }
    }
}
