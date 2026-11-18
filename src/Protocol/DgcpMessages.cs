using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dikai.Dgcp
{
    public sealed class TemplateVariable
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public TemplateVariable() { }

        public TemplateVariable(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>สร้างแพ็กเก็ตคำสั่งตามหัวข้อ 3–4 ของเอกสาร</summary>
    public static class DgcpCommands
    {
        public static byte[] Simple(byte station, byte cmd)
        {
            return DgcpFrame.Build(station, cmd);
        }

        /// <summary>34h เลือกแม่แบบ (+ ตัวแปร) — ไม่มีตัวแปรจะใส่ FF ตามเอกสาร</summary>
        public static byte[] SelectFile(byte station, ushort allocation, string fileName, IList<TemplateVariable> variables)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new DgcpException("กรุณาระบุชื่อแม่แบบ");
            var body = new List<byte>();
            AddU16(body, allocation);
            AddShortText(body, fileName.Trim(), "ชื่อแม่แบบ");
            if (variables == null || variables.Count == 0)
            {
                body.Add(0xFF);
            }
            else
            {
                AddVariables(body, variables);
            }
            return DgcpFrame.Build(station, Cmd.SelectFile, body.ToArray());
        }

        /// <summary>35h (ใช้ buffer, เร็ว) หรือ 49h (ตอบ 4Ah เมื่อพิมพ์เสร็จ)</summary>
        public static byte[] UpdateVariables(byte station, ushort allocation, IList<TemplateVariable> variables, bool waitPrintDone)
        {
            if (variables == null || variables.Count == 0) throw new DgcpException("กรุณาใส่ตัวแปรอย่างน้อย 1 ตัว");
            var body = new List<byte>();
            AddU16(body, allocation);
            AddVariables(body, variables);
            return DgcpFrame.Build(station, waitPrintDone ? Cmd.UpdateVariablesWithFeedback : Cmd.UpdateVariables, body.ToArray());
        }

        public static byte[] SetTime(byte station, DateTime t)
        {
            var body = new List<byte>();
            AddU16(body, (ushort)t.Year);
            body.Add((byte)t.Month);
            body.Add((byte)t.Day);
            body.Add((byte)t.Hour);
            body.Add((byte)t.Minute);
            body.Add((byte)t.Second);
            return DgcpFrame.Build(station, Cmd.SetTime, body.ToArray());
        }

        private static void AddVariables(List<byte> body, IList<TemplateVariable> variables)
        {
            foreach (var v in variables)
            {
                if (string.IsNullOrEmpty(v.Name)) throw new DgcpException("มีตัวแปรที่ไม่มีชื่อ");
                AddShortText(body, v.Name, "ชื่อตัวแปร \"" + v.Name + "\"");
                AddShortText(body, v.Value ?? "", "ค่าของตัวแปร \"" + v.Name + "\"", allowEmpty: true);
            }
        }

        private static void AddShortText(List<byte> body, string text, string what, bool allowEmpty = false)
        {
            var b = DgcpText.Encode(text);
            if (b.Length == 0 && !allowEmpty) throw new DgcpException(what + " ว่างไม่ได้");
            if (b.Length > 255) throw new DgcpException(what + " ยาว " + b.Length + " ไบต์ เกิน 255 ไบต์ (ภาษาไทย 1 ตัว = 3 ไบต์)");
            body.Add((byte)b.Length);
            body.AddRange(b);
        }

        private static void AddU16(List<byte> body, ushort v)
        {
            body.Add((byte)(v >> 8));
            body.Add((byte)v);
        }
    }

    public sealed class StatusVariable
    {
        /// <summary>null เมื่อมาจาก 3Fh (ไม่มีชื่อตัวแปร)</summary>
        public string Name { get; set; }
        public string Value { get; set; }
    }

    /// <summary>ผลของ 3Ah (อ่านสถานะ) หรือ 3Fh (รายงานสถานะ)</summary>
    public sealed class DeviceStatus
    {
        public byte StatusCode { get; set; }
        public uint ErrorBits { get; set; }
        public DateTime? Time { get; set; }
        public byte RibbonRemaining { get; set; }
        public byte LineSpeed { get; set; }
        public ushort PrintFrequency { get; set; }
        public uint TotalCounter { get; set; }
        public uint FileCounter { get; set; }
        public uint RibbonCounter { get; set; }
        public uint PreviousRibbonCounter { get; set; }
        public ushort PrintHeight { get; set; }
        public DateTime? RibbonChangeTime { get; set; }
        public string FileName { get; set; }
        public List<StatusVariable> Variables { get; } = new List<StatusVariable>();
        public byte? LocalStation { get; set; }
        public bool? ParametersUpdated { get; set; }
        /// <summary>true = มีชื่อแม่แบบ/ชื่อตัวแปร (รูปแบบ 3Ah), false = รูปแบบ 3Fh</summary>
        public bool HasNames { get; set; }
        /// <summary>ข้อมูลสั้นหรือยาวกว่าที่เอกสารระบุ — แสดงเท่าที่อ่านได้</summary>
        public bool LayoutMismatch { get; set; }

        public string StatusText => DgcpStatusText.Status(StatusCode);

        public IEnumerable<string> ActiveAlarms()
        {
            return DgcpStatusText.Alarms(ErrorBits);
        }
    }

    public static class DgcpStatusText
    {
        public static string Status(byte code)
        {
            switch (code)
            {
                case 0: return "ฝาเปิด";
                case 1: return "ว่าง (พร้อม)";
                case 2: return "กำลังเริ่มระบบ";
                case 3: return "กำลังพิมพ์";
                case 4: return "ผิดพลาด";
                case 5: return "แจ้งเตือน";
                case 6: return "SD card ผิดพลาด";
                case 7: return "ไฟล์ผิดพลาด";
                case 8: return "เวลาผิดพลาด";
                default: return "ไม่ทราบ (" + code + ")";
            }
        }

        private static readonly string[] BitNames =
        {
            "สื่อสารไม่ทัน ทำให้พิมพ์ตก",   // 0
            "แรงตึงริบบอนผิดปกติ",            // 1
            "สัญญาณสั่งพิมพ์ผิดปกติ",          // 2
            "เซนเซอร์ Hall หัวพิมพ์",          // 3
            "พิมพ์ไม่สมบูรณ์",                 // 4
            "ริบบอนขาด",                       // 5
            "พิมพ์ตก",                         // 6
            "สัญญาณ encoder ผิดปกติ",          // 7
            "ไดรเวอร์มอเตอร์",                 // 8
            "ไดรเวอร์มอเตอร์หลัก",             // 9
            "ไดรเวอร์มอเตอร์รอง",              // 10
            null, null, null, null, null,       // 11-15 สำรอง
            "ฝาเปิด",                          // 16
            "SD card เสีย",                    // 17
            "ไฟล์พิมพ์ผิดพลาด",                 // 18
            "เวลาผิดพลาด",                     // 19
            "แบตเตอรี่หน้าจอต่ำ",               // 20
            "ริบบอนใกล้หมด",                   // 21
        };

        /// <summary>bit 0–21 = ความผิดปกติ (bit 22–23 ของ 3Ah เป็นแค่สถานะสัญญาณ ไม่ใช่ error)</summary>
        public static IEnumerable<string> Alarms(uint bits)
        {
            for (int i = 0; i < BitNames.Length; i++)
            {
                if ((bits & (1u << i)) == 0) continue;
                yield return BitNames[i] ?? ("bit " + i);
            }
            for (int i = 24; i < 32; i++)
            {
                if ((bits & (1u << i)) != 0) yield return "bit " + i;
            }
        }

        public static bool HasPrintSignal(uint bits) => (bits & (1u << 22)) != 0;
        public static bool HasEncoderSignal(uint bits) => (bits & (1u << 23)) != 0;
    }

    public sealed class FileListPacket
    {
        public int Total { get; set; }
        public int Start { get; set; }
        public int Count { get; set; }
        public List<string> Names { get; } = new List<string>();
    }

    public sealed class ParameterItem
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string RawHex { get; set; }
    }

    /// <summary>แปลงคำตอบจากเครื่อง (ส่วน DATA หลัง ST, CMD)</summary>
    public static class DgcpResponses
    {
        /// <summary>
        /// 33h — ตารางในเอกสารมีไบต์ความยาวนำหน้า แต่ตัวอย่าง hex ไม่มี จึงรองรับทั้งสองแบบ
        /// </summary>
        public static string ParseFileName(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            if (data[0] == data.Length - 1)
            {
                var b = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, b, 0, b.Length);
                return DgcpText.Decode(b);
            }
            return DgcpText.Decode(data);
        }

        public static DateTime? ParseTime(byte[] data)
        {
            try
            {
                return ReadTime(new ByteReader(data));
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// 3Ah / 3Fh — ส่วนต้น 41 ไบต์เหมือนกัน ส่วนท้ายต่างกัน
        /// (เอกสารเขียน CMD ของคำตอบ 3Ah เป็น 3F จึงดูจากรูปแบบข้อมูลแทนการเชื่อ CMD)
        /// </summary>
        public static DeviceStatus ParseStatus(byte[] data)
        {
            var s = new DeviceStatus();
            var r = new ByteReader(data);
            try
            {
                s.StatusCode = r.U8();
                s.ErrorBits = r.U32();
                s.Time = ReadTime(r);
                s.RibbonRemaining = r.U8();
                s.LineSpeed = r.U8();
                s.PrintFrequency = r.U16();
                s.TotalCounter = r.U32();
                s.FileCounter = r.U32();
                s.RibbonCounter = r.U32();
                s.PreviousRibbonCounter = r.U32();
                s.PrintHeight = r.U16();
                s.RibbonChangeTime = ReadTime(r);
            }
            catch (IndexOutOfRangeException)
            {
                s.LayoutMismatch = true;
                return s;
            }

            int tail = r.Pos;
            if (TryTailWithNames(data, tail, s, out bool exactA) && exactA) return s;
            var withNames = Snapshot(s);
            ResetTail(s);
            if (TryTailWithoutNames(data, tail, s, out bool exactB) && exactB) return s;

            // ไม่ตรงทั้งสองแบบ — ใช้แบบที่อ่านได้มากกว่า
            ResetTail(s);
            if (withNames != null)
            {
                Restore(s, withNames);
            }
            else
            {
                TryTailWithoutNames(data, tail, s, out _);
            }
            s.LayoutMismatch = true;
            return s;
        }

        private static bool TryTailWithNames(byte[] data, int pos, DeviceStatus s, out bool exact)
        {
            exact = false;
            var r = new ByteReader(data, pos);
            try
            {
                int nameLen = r.U8();
                if (nameLen > 0)
                {
                    s.FileName = r.Text(nameLen);
                    int n = r.U8();
                    for (int i = 0; i < n; i++)
                    {
                        var v = new StatusVariable { Name = r.Text(r.U8()) };
                        v.Value = r.Text(r.U8());
                        s.Variables.Add(v);
                    }
                }
                else
                {
                    s.FileName = "";
                }
                s.LocalStation = r.U8();
                s.ParametersUpdated = r.U8() != 0;
                s.HasNames = true;
                exact = r.Remaining == 0;
                return true;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        private static bool TryTailWithoutNames(byte[] data, int pos, DeviceStatus s, out bool exact)
        {
            exact = false;
            var r = new ByteReader(data, pos);
            try
            {
                if (r.Remaining == 0)
                {
                    exact = true;
                    return true;
                }
                int n = r.U8();
                for (int i = 0; i < n; i++)
                {
                    s.Variables.Add(new StatusVariable { Value = r.Text(r.U8()) });
                }
                s.HasNames = false;
                exact = r.Remaining == 0;
                return true;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        private sealed class TailSnapshot
        {
            public string FileName;
            public List<StatusVariable> Variables;
            public byte? LocalStation;
            public bool? Updated;
            public bool HasNames;
        }

        private static TailSnapshot Snapshot(DeviceStatus s)
        {
            if (!s.HasNames && s.Variables.Count == 0 && s.FileName == null) return null;
            return new TailSnapshot
            {
                FileName = s.FileName,
                Variables = new List<StatusVariable>(s.Variables),
                LocalStation = s.LocalStation,
                Updated = s.ParametersUpdated,
                HasNames = s.HasNames,
            };
        }

        private static void Restore(DeviceStatus s, TailSnapshot t)
        {
            s.FileName = t.FileName;
            s.Variables.AddRange(t.Variables);
            s.LocalStation = t.LocalStation;
            s.ParametersUpdated = t.Updated;
            s.HasNames = t.HasNames;
        }

        private static void ResetTail(DeviceStatus s)
        {
            s.FileName = null;
            s.Variables.Clear();
            s.LocalStation = null;
            s.ParametersUpdated = null;
            s.HasNames = false;
        }

        /// <summary>41h / A0h — เครื่องอาจตอบหลายแพ็กเก็ตต่อ 1 คำขอ</summary>
        public static FileListPacket ParseFileList(byte[] data)
        {
            var r = new ByteReader(data);
            var p = new FileListPacket
            {
                Total = r.U16(),
                Start = r.U16(),
                Count = r.U16(),
            };
            if (p.Count == 0) return p;
            int nameLen = r.U8();
            for (int i = 0; i < p.Count; i++)
            {
                if (r.Remaining < nameLen) break;
                p.Names.Add(r.Text(nameLen).Trim());
            }
            return p;
        }

        private static DateTime? ReadTime(ByteReader r)
        {
            int y = r.U16(), mo = r.U8(), d = r.U8(), h = r.U8(), mi = r.U8(), se = r.U8();
            if (y < 2000 || y > 2099 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || se > 59)
                return null;
            return new DateTime(y, mo, d, h, mi, se);
        }

        // ------------------------------------------------------------------ 40h

        private enum PKind { U16, Ascii4, Ip, Raw }

        private sealed class PDef
        {
            public readonly string Name;
            public readonly int FrameOffset;
            public readonly int Size;
            public readonly PKind Kind;
            public readonly string[] Options;

            public PDef(string name, int frameOffset, int size, PKind kind, params string[] options)
            {
                Name = name;
                FrameOffset = frameOffset;
                Size = size;
                Kind = kind;
                Options = options;
            }
        }

        private static readonly string[] RibbonTypes =
        {
            "15-25mm DC", "15-25mm DG", "15-25mm DT", "(3)", "15-25mm DGD", "15-25mm DCLL",
            "30-40mm DC", "30-40mm DG", "30-40mm DT", "30-40mm DGD", "30-40mm DCLL",
            "55-60mm DC", "55-60mm DG", "55-60mm DT", "55-60mm DGD", "55-60mm DCLL",
        };

        /// <summary>ตารางหัวข้อ 4.0 — offset นับจากต้นแพ็กเก็ต (รวม 5A A5)</summary>
        private static readonly PDef[] ParamDefs =
        {
            new PDef("แนวนอน (Horizontal)", 6, 2, PKind.U16),
            new PDef("หน่วงเวลา (Delay)", 8, 2, PKind.U16),
            new PDef("ระยะห่าง (Gap)", 10, 2, PKind.U16),
            new PDef("ความเข้มพิมพ์ (Density)", 12, 2, PKind.U16),
            new PDef("พลิกภาพตัวอย่าง (Preview flip)", 14, 2, PKind.U16, "ปิด", "เปิด"),
            new PDef("ความต้านทานหัวพิมพ์", 24, 2, PKind.U16),
            new PDef("แนวตั้ง (Vertical)", 26, 2, PKind.U16),
            new PDef("ความดำ (Blackness)", 28, 2, PKind.U16),
            new PDef("แรงกดหัวพิมพ์", 30, 2, PKind.U16),
            new PDef("กันสั่น (Anti-vibration)", 32, 2, PKind.U16),
            new PDef("ชนิดสัญญาณเตือน", 34, 2, PKind.U16, "ปกติเปิด (NO)", "ปกติปิด (NC)"),
            new PDef("ยกหัวพิมพ์", 36, 2, PKind.U16),
            new PDef("ช่วงห่างต่ำสุด", 38, 2, PKind.U16),
            new PDef("สวิตช์เตือน", 40, 2, PKind.U16, "ปิด", "เปิด"),
            new PDef("ความเร็วพิมพ์ (Intermittent)", 42, 2, PKind.U16),
            new PDef("ชนิดริบบอน", 44, 2, PKind.U16, RibbonTypes),
            new PDef("พิมพ์ซ้ำ (Reprint)", 46, 2, PKind.U16, "ปิด", "เปิด"),
            new PDef("โหมดเครื่อง", 48, 2, PKind.U16, "Intermittent", "Continuous"),
            new PDef("ทิศทาง", 50, 2, PKind.U16, "ซ้าย", "ขวา"),
            new PDef("การหมุนภาพ", 52, 2, PKind.U16, "0°", "90°", "180°", "270°", "กระจก", "90° กระจก", "180° กระจก", "270° กระจก"),
            new PDef("Optimization", 54, 2, PKind.U16, "ระยะริบบอน", "ตำแหน่งพิมพ์"),
            new PDef("Device ID", 56, 4, PKind.Ascii4),
            new PDef("Production line ID", 60, 4, PKind.Ascii4),
            new PDef("โหมดประหยัดริบบอน", 64, 2, PKind.U16, "ปกติ", "R-mode", "บีบอัด", "ละเอียด"),
            new PDef("โหมดสัญญาณพิมพ์", 66, 2, PKind.U16, "ปกติ", "ระยะเท่ากัน"),
            new PDef("ระยะพิมพ์เท่ากัน", 68, 2, PKind.U16),
            new PDef("R-mode จำนวนซ้ำ", 70, 2, PKind.U16),
            new PDef("R-mode เลื่อนแนวนอน", 72, 2, PKind.U16),
            new PDef("เปอร์เซ็นต์บีบอัด", 74, 2, PKind.U16),
            new PDef("โหมดทำงาน", 76, 2, PKind.U16, "ปกติ", "คัดแยกอัตโนมัติ"),
            new PDef("ชดเชยตำแหน่ง", 78, 2, PKind.U16),
            new PDef("ความเร็วต่ำสุด (Continuous)", 80, 2, PKind.U16),
            new PDef("เวลาขึ้นวันใหม่ (Date flip)", 82, 4, PKind.Ascii4),
            new PDef("ชนิดหัวพิมพ์", 86, 2, PKind.U16, "32mm", "53mm"),
            new PDef("Print-head offset", 98, 2, PKind.U16),
            new PDef("Ribbon feed", 100, 2, PKind.U16),
            new PDef("แรงดันหัวพิมพ์", 102, 2, PKind.U16),
            new PDef("Sensor detection", 104, 2, PKind.U16),
            new PDef("Motor calibration 1", 106, 2, PKind.U16),
            new PDef("Motor calibration 2", 108, 2, PKind.U16),
            new PDef("เลขเครื่อง (Local ID / ST)", 118, 2, PKind.U16),
            new PDef("Baud rate", 120, 2, PKind.U16),
            new PDef("IP address", 122, 4, PKind.Ip),
            new PDef("Subnet mask", 126, 4, PKind.Ip),
            new PDef("Gateway", 130, 4, PKind.Ip),
            new PDef("รอบรายงานสถานะ", 134, 2, PKind.U16),
            new PDef("IO config 1", 144, 2, PKind.U16),
            new PDef("IO config 2", 146, 2, PKind.U16),
            new PDef("IO config 3", 148, 2, PKind.U16),
        };

        public static List<ParameterItem> ParseParameters(byte[] data)
        {
            var list = new List<ParameterItem>();
            foreach (var p in ParamDefs)
            {
                int off = p.FrameOffset - 6; // DATA เริ่มหลัง 5A A5 LEN ST CMD
                if (off + p.Size > data.Length) break;
                var raw = new byte[p.Size];
                Buffer.BlockCopy(data, off, raw, 0, p.Size);
                list.Add(new ParameterItem { Name = p.Name, Value = FormatParam(p, raw), RawHex = Hex.Format(raw) });
            }
            return list;
        }

        private static string FormatParam(PDef p, byte[] raw)
        {
            switch (p.Kind)
            {
                case PKind.U16:
                {
                    int v = (raw[0] << 8) | raw[1];
                    if (p.Options != null && p.Options.Length > 0 && v < p.Options.Length) return v + " = " + p.Options[v];
                    return v.ToString(CultureInfo.InvariantCulture);
                }
                case PKind.Ip:
                    return raw[0] + "." + raw[1] + "." + raw[2] + "." + raw[3];
                case PKind.Ascii4:
                {
                    bool printable = true;
                    foreach (var b in raw)
                    {
                        if (b != 0 && (b < 0x20 || b > 0x7E)) printable = false;
                    }
                    var text = Encoding.ASCII.GetString(raw).Trim('\0', ' ');
                    uint num = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
                    return printable && text.Length > 0 ? "\"" + text + "\"" : num.ToString(CultureInfo.InvariantCulture);
                }
                default:
                    return Hex.Format(raw);
            }
        }
    }
}
