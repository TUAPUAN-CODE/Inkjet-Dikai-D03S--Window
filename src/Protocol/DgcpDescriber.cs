using System;
using System.Collections.Generic;
using System.Linq;

namespace Dikai.Dgcp
{
    /// <summary>แปลแพ็กเก็ตเป็นข้อความอ่านง่ายสำหรับ log</summary>
    public static class DgcpDescriber
    {
        public static string Describe(DgcpFrame f, bool outgoing)
        {
            try
            {
                return outgoing ? DescribeRequest(f) : DescribeReply(f);
            }
            catch (Exception)
            {
                return Cmd.Name(f.Command) + " (" + f.Command.ToString("X2") + "h)";
            }
        }

        private static string DescribeRequest(DgcpFrame f)
        {
            string head = Cmd.Name(f.Command) + " (" + f.Command.ToString("X2") + "h)";
            switch (f.Command)
            {
                case Cmd.SelectFile:
                {
                    var r = new ByteReader(f.Data);
                    int alloc = r.U16();
                    string file = r.Text(r.U8());
                    string vars = r.Remaining == 1 && f.Data[f.Data.Length - 1] == 0xFF ? "ไม่เปลี่ยนตัวแปร" : Vars(r);
                    return head + " " + file + " | " + vars + Allocation(alloc);
                }
                case Cmd.UpdateVariables:
                case Cmd.UpdateVariablesWithFeedback:
                {
                    var r = new ByteReader(f.Data);
                    int alloc = r.U16();
                    return head + " " + Vars(r) + Allocation(alloc);
                }
                case Cmd.SetTime:
                {
                    var t = DgcpResponses.ParseTime(f.Data);
                    return head + " " + (t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss") : "");
                }
                default:
                    return head;
            }
        }

        private static string DescribeReply(DgcpFrame f)
        {
            switch (f.Command)
            {
                case Cmd.Ok:
                    return f.Data.Length > 0 ? "สำเร็จ (" + Cmd.Name(f.Data[0]) + " " + f.Data[0].ToString("X2") + "h)" : "สำเร็จ";
                case Cmd.Error:
                    return f.Data.Length > 0 ? "ผิดพลาด " + f.Data[0] + ": " + DgcpErrors.Describe(f.Data[0]) : "ผิดพลาด (ไม่ระบุรหัส)";
                case Cmd.PrintDone:
                    return "พิมพ์เสร็จ (4Ah)";
                case Cmd.GetCurrentFile:
                    return "แม่แบบที่ใช้อยู่: " + DgcpResponses.ParseFileName(f.Data);
                case Cmd.GetStatus:
                case Cmd.StatusReport:
                {
                    var s = DgcpResponses.ParseStatus(f.Data);
                    var alarms = s.ActiveAlarms().ToList();
                    return (f.Command == Cmd.StatusReport ? "รายงานสถานะ: " : "สถานะ: ") + s.StatusText
                           + (string.IsNullOrEmpty(s.FileName) ? "" : " | " + s.FileName)
                           + (alarms.Count > 0 ? " | " + string.Join(", ", alarms) : "");
                }
                case Cmd.GetFileList:
                case Cmd.GetUsbFileList:
                {
                    var p = DgcpResponses.ParseFileList(f.Data);
                    return Cmd.Name(f.Command) + " " + (p.Start + 1) + "-" + (p.Start + p.Count) + " จาก " + p.Total + ": " + string.Join(", ", p.Names);
                }
                case Cmd.GetTime:
                {
                    var t = DgcpResponses.ParseTime(f.Data);
                    return "เวลาเครื่อง: " + (t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(อ่านไม่ได้)");
                }
                default:
                    return Cmd.Name(f.Command) + " (" + f.Command.ToString("X2") + "h) " + f.Data.Length + " ไบต์";
            }
        }

        private static string Vars(ByteReader r)
        {
            var parts = new List<string>();
            while (r.Remaining > 0)
            {
                string name = r.Text(r.U8());
                string value = r.Text(r.U8());
                parts.Add(name + "=" + value);
            }
            return string.Join(", ", parts);
        }

        private static string Allocation(int alloc)
        {
            return alloc == 0 ? "" : " | พิมพ์ " + alloc + " ครั้ง";
        }
    }
}
