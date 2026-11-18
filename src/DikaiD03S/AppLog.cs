using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dikai.App
{
    /// <summary>บันทึก log ลงไฟล์รายวัน logs/yyyy-MM-dd.log (เก็บ 60 วัน)</summary>
    public static class AppLog
    {
        private static readonly object Gate = new object();
        public static bool Enabled { get; set; } = true;

        public static void Write(LogEntry e)
        {
            if (!Enabled) return;
            try
            {
                var dir = AppSettings.LogDirectory;
                var line = e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                           + "\t" + e.Printer + "\t" + KindText(e.Kind) + "\t" + e.Text
                           + (string.IsNullOrEmpty(e.HexText) ? "" : "\t" + e.HexText) + Environment.NewLine;
                lock (Gate)
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, e.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // เขียน log ไม่ได้ต้องไม่ทำให้โปรแกรมหยุด
            }
        }

        public static void CleanupOld(int keepDays = 60)
        {
            try
            {
                var dir = AppSettings.LogDirectory;
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*.log"))
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        public static string KindText(LogKind k)
        {
            switch (k)
            {
                case LogKind.Tx: return "ส่ง →";
                case LogKind.Rx: return "← รับ";
                case LogKind.Error: return "ผิดพลาด";
                default: return "ข้อมูล";
            }
        }
    }
}
