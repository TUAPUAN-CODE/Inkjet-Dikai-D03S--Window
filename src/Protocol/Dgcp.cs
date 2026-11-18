using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dikai.Dgcp
{
    /// <summary>รหัสคำสั่งของโปรโตคอล DGCP v2.0 (ตามเอกสาร TTO DGCP)</summary>
    public static class Cmd
    {
        public const byte Error = 0x31;
        public const byte Ok = 0x32;
        public const byte GetCurrentFile = 0x33;
        public const byte SelectFile = 0x34;
        public const byte UpdateVariables = 0x35;
        public const byte ClearWarning = 0x36;
        public const byte ClearFileCounter = 0x37;
        public const byte ClearBuffer = 0x38;
        public const byte ClearRibbonCounter = 0x39;
        public const byte GetStatus = 0x3A;
        public const byte StatusReport = 0x3F;
        public const byte GetParameters = 0x40;
        public const byte GetFileList = 0x41;
        public const byte Start = 0x42;
        public const byte Stop = 0x43;
        public const byte TestPrint = 0x44;
        public const byte ClearPrintCounter = 0x45;
        public const byte ClearAlarm = 0x46;
        public const byte GetTime = 0x47;
        public const byte SetTime = 0x48;
        public const byte UpdateVariablesWithFeedback = 0x49;
        public const byte PrintDone = 0x4A;
        public const byte PrintTrigger = 0x4B;
        public const byte ClearUpdateFlag = 0x4C;
        public const byte GetUsbFileList = 0xA0;

        public static string Name(byte cmd)
        {
            switch (cmd)
            {
                case Error: return "ผิดพลาด";
                case Ok: return "สำเร็จ";
                case GetCurrentFile: return "อ่านแม่แบบที่ใช้อยู่";
                case SelectFile: return "เลือกแม่แบบ";
                case UpdateVariables: return "เปลี่ยนค่าตัวแปร";
                case ClearWarning: return "ล้างคำเตือน";
                case ClearFileCounter: return "ล้างตัวนับไฟล์";
                case ClearBuffer: return "ล้างคิว";
                case ClearRibbonCounter: return "ล้างตัวนับริบบอน";
                case GetStatus: return "อ่านสถานะ";
                case StatusReport: return "รายงานสถานะ";
                case GetParameters: return "อ่านค่าเครื่อง";
                case GetFileList: return "รายชื่อแม่แบบ";
                case Start: return "เริ่ม";
                case Stop: return "หยุด";
                case TestPrint: return "ทดสอบพิมพ์";
                case ClearPrintCounter: return "ล้างตัวนับพิมพ์";
                case ClearAlarm: return "ล้างการแจ้งเตือน";
                case GetTime: return "อ่านเวลา";
                case SetTime: return "ตั้งเวลา";
                case UpdateVariablesWithFeedback: return "เปลี่ยนค่า+แจ้งพิมพ์เสร็จ";
                case PrintDone: return "พิมพ์เสร็จ";
                case PrintTrigger: return "สั่งพิมพ์";
                case ClearUpdateFlag: return "ล้างธงอัปเดต";
                case GetUsbFileList: return "รายชื่อไฟล์ USB";
                default: return "คำสั่ง " + cmd.ToString("X2") + "h";
            }
        }
    }

    /// <summary>ขนาดสูงสุดของ 1 แพ็กเก็ตที่เครื่องรับได้ (รวมหัว) ตามหัวข้อ 1.2 ของเอกสาร</summary>
    public static class DgcpLimits
    {
        public const int TcpMaxRequestFrame = 1010;
        public const int SerialMaxRequestFrame = 256;
        public const int DefaultTcpPort = 5000;
        public const int DefaultBaudRate = 19200;
        /// <summary>เพดานกันข้อมูลขยะตอนอ่าน (เครื่องส่งได้สูงสุด 2010 ไบต์)</summary>
        public const int MaxResponsePayload = 4096;
    }

    public static class DgcpErrors
    {
        /// <summary>รหัส error ในเอกสารเป็นเลขฐานสิบ (เช่น USB ไม่พบ = 16 ส่งมาเป็นไบต์ 0x10)</summary>
        public static string Describe(int code)
        {
            switch (code)
            {
                case 1: return "ข้อมูลที่เครื่องได้รับเสียหาย";
                case 2: return "มีค่าที่ไม่ถูกต้องในข้อมูล (เช่น ชื่อตัวแปรไม่มีในแม่แบบ)";
                case 3: return "เครื่องรับข้อมูลไม่สำเร็จ ต้องส่งใหม่";
                case 4: return "เครื่องปฏิเสธคำสั่ง";
                case 5: return "ยังไม่มีแม่แบบที่ใช้งานอยู่";
                case 6: return "เปิดไฟล์ไม่สำเร็จ (ตรวจชื่อแม่แบบ)";
                case 7: return "คำสั่งไม่มีผลหรือคำสั่งไม่ถูกต้อง";
                case 8: return "คิวพิมพ์เต็ม";
                case 9: return "จัดการไฟล์ไม่สำเร็จ";
                case 10: return "เลขเครื่อง (ST) ไม่ตรงกับที่ตั้งในเครื่อง";
                case 11: return "ชื่อไฟล์ไม่ถูกต้อง";
                case 12: return "อ่านไฟล์ไม่สำเร็จ";
                case 13: return "Handshake ไม่สำเร็จ";
                case 14: return "ความยาวแพ็กเก็ตไม่ถูกต้อง";
                case 15: return "ส่งแพ็กเก็ตถัดไปได้";
                case 16: return "ไม่พบ USB";
                default: return "ไม่ทราบสาเหตุ";
            }
        }
    }

    public class DgcpException : Exception
    {
        public DgcpException(string message) : base(message) { }
        public DgcpException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>เครื่องตอบกลับด้วยคำสั่ง 31h (ผิดพลาด)</summary>
    public sealed class DgcpDeviceException : DgcpException
    {
        public int Code { get; }
        public byte RequestCommand { get; }

        public DgcpDeviceException(int code, byte requestCommand)
            : base(code < 0
                ? "เครื่องตอบผิดพลาดแต่ไม่ระบุรหัส"
                : "เครื่องตอบผิดพลาด " + code + ": " + DgcpErrors.Describe(code))
        {
            Code = code;
            RequestCommand = requestCommand;
        }
    }

    public sealed class DgcpTimeoutException : DgcpException
    {
        public DgcpTimeoutException(byte cmd, int timeoutMs)
            : base("เครื่องไม่ตอบกลับภายใน " + (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)
                   + " วินาที (" + Cmd.Name(cmd) + " " + cmd.ToString("X2") + "h)")
        {
        }
    }

    /// <summary>1 แพ็กเก็ต: 5A A5 + LEN(2) + ST + CMD + DATA</summary>
    public sealed class DgcpFrame
    {
        public const byte Stx1 = 0x5A;
        public const byte Stx2 = 0xA5;
        public const int HeaderSize = 4;

        public byte Station { get; }
        public byte Command { get; }
        /// <summary>ข้อมูลหลัง ST และ CMD</summary>
        public byte[] Data { get; }
        /// <summary>ไบต์ทั้งแพ็กเก็ตตามที่ส่ง/รับจริง</summary>
        public byte[] Raw { get; }

        public DgcpFrame(byte station, byte command, byte[] data, byte[] raw)
        {
            Station = station;
            Command = command;
            Data = data ?? new byte[0];
            Raw = raw;
        }

        public static byte[] Build(byte station, byte command, byte[] data = null)
        {
            data = data ?? new byte[0];
            int len = 2 + data.Length;
            if (len > 0xFFFF) throw new DgcpException("ข้อมูลยาวเกินกว่าที่โปรโตคอลรองรับ");
            var raw = new byte[HeaderSize + len];
            raw[0] = Stx1;
            raw[1] = Stx2;
            raw[2] = (byte)(len >> 8);
            raw[3] = (byte)len;
            raw[4] = station;
            raw[5] = command;
            Buffer.BlockCopy(data, 0, raw, 6, data.Length);
            return raw;
        }

        /// <summary>แปลงไบต์ที่มีหัว 5A A5 อยู่แล้วเป็น frame (ตรวจความยาว)</summary>
        public static DgcpFrame FromRaw(byte[] raw)
        {
            if (raw == null || raw.Length < HeaderSize + 2 || raw[0] != Stx1 || raw[1] != Stx2)
                throw new DgcpException("ไม่ใช่แพ็กเก็ต DGCP (ต้องขึ้นต้นด้วย 5A A5 และมี ST, CMD)");
            int len = (raw[2] << 8) | raw[3];
            if (len < 2 || HeaderSize + len > raw.Length)
                throw new DgcpException("ความยาวในหัวแพ็กเก็ตไม่ตรงกับข้อมูล");
            var data = new byte[len - 2];
            Buffer.BlockCopy(raw, 6, data, 0, data.Length);
            var exact = raw;
            if (raw.Length != HeaderSize + len)
            {
                exact = new byte[HeaderSize + len];
                Buffer.BlockCopy(raw, 0, exact, 0, exact.Length);
            }
            return new DgcpFrame(raw[4], raw[5], data, exact);
        }

        public override string ToString()
        {
            return Cmd.Name(Command) + " (" + Command.ToString("X2") + "h)";
        }
    }

    /// <summary>
    /// ตัวแยกแพ็กเก็ตจาก stream (TCP/Serial ส่งมาเป็นก้อนไม่แน่นอน)
    /// หา 5A A5 → อ่าน LEN → รอจนครบ; ไบต์ที่ไม่ใช่แพ็กเก็ตจะถูกทิ้งและรายงานผ่าน Discarded
    /// </summary>
    public sealed class DgcpFrameParser
    {
        private byte[] _buf = new byte[4096];
        private int _count;
        private readonly int _maxPayload;

        public DgcpFrameParser(int maxPayload = DgcpLimits.MaxResponsePayload)
        {
            _maxPayload = maxPayload;
        }

        public int Pending => _count;

        public List<DgcpFrame> Push(byte[] data, int offset, int count, List<byte[]> discarded = null)
        {
            EnsureCapacity(_count + count);
            Buffer.BlockCopy(data, offset, _buf, _count, count);
            _count += count;

            var frames = new List<DgcpFrame>();
            while (true)
            {
                int start = FindStx();
                if (start < 0)
                {
                    // เก็บไบต์สุดท้ายไว้ถ้าเป็น 5A (อาจเป็นครึ่งแรกของหัว)
                    int keep = _count > 0 && _buf[_count - 1] == DgcpFrame.Stx1 ? 1 : 0;
                    Discard(_count - keep, discarded);
                    break;
                }
                if (start > 0) Discard(start, discarded);
                if (_count < DgcpFrame.HeaderSize) break;

                int len = (_buf[2] << 8) | _buf[3];
                if (len < 2 || len > _maxPayload)
                {
                    Discard(1, discarded); // หัวปลอม ข้าม 5A แล้วหาใหม่
                    continue;
                }
                int total = DgcpFrame.HeaderSize + len;
                if (_count < total) break;

                var raw = new byte[total];
                Buffer.BlockCopy(_buf, 0, raw, 0, total);
                Remove(total);
                frames.Add(DgcpFrame.FromRaw(raw));
            }
            return frames;
        }

        public void Reset()
        {
            _count = 0;
        }

        private int FindStx()
        {
            for (int i = 0; i + 1 < _count; i++)
            {
                if (_buf[i] == DgcpFrame.Stx1 && _buf[i + 1] == DgcpFrame.Stx2) return i;
            }
            return -1;
        }

        private void Discard(int n, List<byte[]> discarded)
        {
            if (n <= 0) return;
            if (discarded != null)
            {
                var junk = new byte[n];
                Buffer.BlockCopy(_buf, 0, junk, 0, n);
                discarded.Add(junk);
            }
            Remove(n);
        }

        private void Remove(int n)
        {
            Buffer.BlockCopy(_buf, n, _buf, 0, _count - n);
            _count -= n;
        }

        private void EnsureCapacity(int size)
        {
            if (size <= _buf.Length) return;
            var nb = new byte[Math.Max(size, _buf.Length * 2)];
            Buffer.BlockCopy(_buf, 0, nb, 0, _count);
            _buf = nb;
        }
    }

    public static class Hex
    {
        public static string Format(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>รับได้ทั้ง "5A A5 00 02", "5AA5 0002", "0x5A,0xA5"</summary>
        public static byte[] Parse(string text)
        {
            var result = new List<byte>();
            if (string.IsNullOrWhiteSpace(text)) return result.ToArray();
            var cleaned = text.Replace("0x", " ").Replace("0X", " ");
            var tokens = cleaned.Split(new[] { ' ', ',', '-', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in tokens)
            {
                var t = raw.EndsWith("h", StringComparison.OrdinalIgnoreCase) && raw.Length <= 3 ? raw.Substring(0, raw.Length - 1) : raw;
                if (t.Length % 2 == 1) t = "0" + t;
                for (int i = 0; i < t.Length; i += 2)
                {
                    if (!byte.TryParse(t.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        throw new FormatException("HEX ไม่ถูกต้อง: \"" + raw + "\"");
                    result.Add(b);
                }
            }
            return result.ToArray();
        }
    }

    /// <summary>อ่านค่าแบบ big-endian ตามเอกสาร</summary>
    internal sealed class ByteReader
    {
        private readonly byte[] _d;
        public int Pos { get; private set; }

        public ByteReader(byte[] data, int pos = 0)
        {
            _d = data;
            Pos = pos;
        }

        public int Remaining => _d.Length - Pos;

        public byte U8()
        {
            Need(1);
            return _d[Pos++];
        }

        public ushort U16()
        {
            Need(2);
            var v = (ushort)((_d[Pos] << 8) | _d[Pos + 1]);
            Pos += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            var v = ((uint)_d[Pos] << 24) | ((uint)_d[Pos + 1] << 16) | ((uint)_d[Pos + 2] << 8) | _d[Pos + 3];
            Pos += 4;
            return v;
        }

        public byte[] Bytes(int n)
        {
            Need(n);
            var r = new byte[n];
            Buffer.BlockCopy(_d, Pos, r, 0, n);
            Pos += n;
            return r;
        }

        public string Text(int n)
        {
            return DgcpText.Decode(Bytes(n));
        }

        private void Need(int n)
        {
            if (Pos + n > _d.Length) throw new IndexOutOfRangeException("ข้อมูลสั้นกว่าที่คาด");
        }
    }

    public static class DgcpText
    {
        /// <summary>เอกสารกำหนดให้ข้อความที่ไม่ใช่ ASCII ใช้ UTF-8</summary>
        public static readonly Encoding Encoding = new UTF8Encoding(false);

        public static byte[] Encode(string s)
        {
            return Encoding.GetBytes(s ?? "");
        }

        public static string Decode(byte[] b)
        {
            return Encoding.GetString(b).Trim('\0');
        }
    }
}
