using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Dikai.Dgcp;

namespace Dikai.App
{
    public enum SendMode
    {
        SelectFile = 0,       // 34h
        Update = 1,           // 35h
        UpdateWithFeedback = 2 // 49h
    }

    [DataContract]
    public sealed class VariableEntry
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Value { get; set; }
    }

    /// <summary>
    /// "ฟอนต์" ที่ให้เลือกในหน้าส่งข้อมูล = แม่แบบที่ผู้ดูแลสร้างไว้ในเครื่องแล้ว (ฟอนต์ถูกกำหนดในไฟล์แม่แบบ ไม่มีคำสั่งเปลี่ยนฟอนต์ใน DGCP)
    /// เลือกฟอนต์ = สลับไปแม่แบบนั้นด้วย 34h
    /// </summary>
    [DataContract]
    public sealed class FontEntry
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Template { get; set; }
    }

    [DataContract]
    public sealed class PrinterProfile
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        /// <summary>"tcp" หรือ "serial"</summary>
        [DataMember(Order = 3)] public string Kind { get; set; }
        [DataMember(Order = 4)] public string Host { get; set; }
        [DataMember(Order = 5)] public int Port { get; set; }
        [DataMember(Order = 6)] public string ComPort { get; set; }
        [DataMember(Order = 7)] public int BaudRate { get; set; }
        [DataMember(Order = 8)] public int Station { get; set; }
        [DataMember(Order = 9)] public string LastTemplate { get; set; }
        [DataMember(Order = 10)] public List<VariableEntry> Variables { get; set; }
        [DataMember(Order = 11)] public int Allocation { get; set; }
        [DataMember(Order = 12)] public int Mode { get; set; }
        [DataMember(Order = 13)] public bool ClearBeforeSend { get; set; }
        [DataMember(Order = 14)] public List<string> KnownTemplates { get; set; }
        [DataMember(Order = 15)] public List<FontEntry> Fonts { get; set; }

        public bool IsSerial => string.Equals(Kind, "serial", StringComparison.OrdinalIgnoreCase);

        public string Address => IsSerial ? (ComPort ?? "COM?") + " @" + BaudRate : Host + ":" + Port;

        public int MaxFrame => IsSerial ? DgcpLimits.SerialMaxRequestFrame : DgcpLimits.TcpMaxRequestFrame;

        public static PrinterProfile CreateDefault(string name, string host)
        {
            var p = new PrinterProfile { Name = name, Host = host, Station = 1 }; // ตัวอย่างในเอกสารใช้ ST = 1
            p.Normalize();
            return p;
        }

        /// <summary>DataContractJsonSerializer ไม่เรียก constructor — เติมค่าเริ่มต้นตรงนี้</summary>
        public void Normalize()
        {
            if (string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(Name)) Name = "เครื่องพิมพ์";
            if (string.IsNullOrEmpty(Kind)) Kind = "tcp";
            if (string.IsNullOrEmpty(Host)) Host = "192.168.1.2";
            if (Port <= 0 || Port > 65535) Port = DgcpLimits.DefaultTcpPort;
            if (BaudRate <= 0) BaudRate = DgcpLimits.DefaultBaudRate;
            if (Station < 0 || Station > 255) Station = 1;
            if (Variables == null) Variables = new List<VariableEntry>();
            if (KnownTemplates == null) KnownTemplates = new List<string>();
            if (Fonts == null) Fonts = new List<FontEntry>();
            if (Allocation < 0 || Allocation > 65535) Allocation = 0;
            if (Mode < 0 || Mode > 2) Mode = 0;
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Order = 1)] public List<PrinterProfile> Printers { get; set; }
        [DataMember(Order = 2)] public int TimeoutMs { get; set; }
        [DataMember(Order = 3)] public bool LogToFile { get; set; }
        [DataMember(Order = 4)] public bool ShowStatusFrames { get; set; }
        [DataMember(Order = 5)] public int AutoStatusSeconds { get; set; }
        /// <summary>รหัสผ่านผู้ดูแล (PBKDF2) — ว่าง = ใช้รหัสเริ่มต้น ดู AdminLock</summary>
        [DataMember(Order = 6)] public string AdminPassword { get; set; }
        /// <summary>ตั้งเวลาเครื่องพิมพ์ตามคอมพิวเตอร์อัตโนมัติ (ตอนเชื่อมต่อ + ทุก 30 นาที ถ้าคลาดเกิน 3 วินาที) — ปิดไว้เป็นค่าเริ่มต้น</summary>
        [DataMember(Order = 7)] public bool AutoSyncTime { get; set; }

        public static string BaseDirectory { get; private set; }
        public static string FilePath => Path.Combine(BaseDirectory, "DikaiD03S.settings.json");
        public static string LogDirectory => Path.Combine(BaseDirectory, "logs");

        public void Normalize()
        {
            if (Printers == null) Printers = new List<PrinterProfile>();
            foreach (var p in Printers) p.Normalize();
            if (TimeoutMs < 500 || TimeoutMs > 60000) TimeoutMs = 3000;
            if (AutoStatusSeconds < 1 || AutoStatusSeconds > 3600) AutoStatusSeconds = 5;
        }

        /// <summary>เก็บไฟล์ข้างโปรแกรม (copy ทั้งโฟลเดอร์ไปเครื่องอื่นได้) ถ้าเขียนไม่ได้ใช้ %LOCALAPPDATA%</summary>
        public static void ResolveBaseDirectory()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (CanWrite(exeDir))
            {
                BaseDirectory = exeDir;
                return;
            }
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DikaiD03S");
            Directory.CreateDirectory(local);
            BaseDirectory = local;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static AppSettings Load(out string warning)
        {
            warning = null;
            AppSettings s = null;
            if (File.Exists(FilePath))
            {
                try
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs);
                    }
                }
                catch (Exception ex)
                {
                    var bad = FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Copy(FilePath, bad, true); } catch { }
                    warning = "อ่านไฟล์ตั้งค่าไม่ได้ (" + ex.Message + ") — สำรองไฟล์เดิมไว้ที่ " + bad;
                }
            }
            if (s == null)
            {
                s = new AppSettings { LogToFile = true, Printers = new List<PrinterProfile>() };
                s.Printers.Add(PrinterProfile.CreateDefault("เครื่อง 1", "192.168.1.2"));
            }
            s.Normalize();
            return s;
        }

        public void Save()
        {
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                w.Flush();
            }
            if (!File.Exists(FilePath))
            {
                File.Move(tmp, FilePath);
                return;
            }
            try
            {
                File.Replace(tmp, FilePath, null);
            }
            catch (IOException)
            {
                // บางไดรฟ์ (network share) ไม่รองรับ Replace
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
        }
    }
}
