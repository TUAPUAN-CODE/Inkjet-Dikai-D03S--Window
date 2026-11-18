using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Dikai.App
{
    /// <summary>
    /// เริ่มโปรแกรม: เปิด server ภายในเครื่อง (127.0.0.1) → เปิดหน้าต่างแอป → ไอคอนที่ system tray
    /// ปิดหน้าต่างแล้วโปรแกรมจะปิดตามภายในไม่กี่วินาที
    ///
    /// ตัวเลือก (สำหรับทดสอบ/ดูแลระบบ):
    ///   --no-browser   ไม่เปิดหน้าต่าง (พิมพ์ URL ออก console) และไม่ปิดตัวเอง
    ///   --no-tray      ไม่สร้างไอคอนที่ tray
    ///   --port N       ใช้ port คงที่ (ปกติสุ่ม)
    ///   --token T      กำหนด token เอง
    ///   --ui FILE      ใช้ไฟล์ HTML จากดิสก์แทนที่ฝังใน exe (แก้หน้าจอแล้ว refresh ได้ทันที)
    /// </summary>
    internal static class Program
    {
        private sealed class Options
        {
            public bool NoBrowser;
            public bool NoTray;
            public int Port;
            public string Token;
            public string UiFile;
        }

        private static string _cachedHtml;

        [STAThread]
        private static int Main(string[] args)
        {
            Options opt;
            try
            {
                opt = ParseArgs(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            AppSettings.ResolveBaseDirectory();
            var instanceFile = Path.Combine(AppSettings.BaseDirectory, "DikaiD03S.instance");

            using (var mutex = new Mutex(true, "Dikai.D03S.Sender.SingleInstance", out bool first))
            {
                if (!first)
                {
                    // เปิดอยู่แล้ว → แค่เปิดหน้าต่างใหม่ไปที่โปรแกรมเดิม
                    var existing = File.Exists(instanceFile) ? File.ReadAllText(instanceFile).Trim() : null;
                    if (existing != null) AppWindow.Open(existing);
                    else Console.Error.WriteLine("โปรแกรมเปิดอยู่แล้ว");
                    return 0;
                }

                TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    AppLog.Write(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = "Unhandled: " + e.ExceptionObject });

                var settings = AppSettings.Load(out string warning);
                AppLog.Enabled = settings.LogToFile;
                AppLog.CleanupOld();

                var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                var token = opt.Token ?? NewToken();
                using (var controller = new AppController(settings, warning) { Version = version })
                using (var server = new WebServer(opt.Port, token, controller.HandleApi, () => LoadHtml(opt.UiFile)))
                {
                    controller.Server = server;
                    try
                    {
                        server.Start();
                    }
                    catch (Exception ex)
                    {
                        Fail("เปิดโปรแกรมไม่ได้: " + ex.Message, opt);
                        return 1;
                    }

                    var url = "http://127.0.0.1:" + server.Port + "/?t=" + token;
                    TryWrite(instanceFile, url);
                    Console.WriteLine("Dikai D03S v" + version + " — " + url);

                    Process window = null;
                    if (!opt.NoBrowser)
                    {
                        try
                        {
                            window = AppWindow.Open(url);
                        }
                        catch (Exception ex)
                        {
                            Fail("เปิดหน้าต่างไม่ได้: " + ex.Message + "\nเปิดเบราว์เซอร์ไปที่:\n" + url, opt);
                        }
                    }

                    var monitor = new LifeMonitor(server, window, opt.NoBrowser);
                    if (opt.NoTray)
                    {
                        while (!monitor.ShouldExit()) Thread.Sleep(2000);
                    }
                    else
                    {
                        RunTray(url, monitor);
                    }

                    try { File.Delete(instanceFile); } catch { }
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>
        /// ปิดโปรแกรมเมื่อไม่มีหน้าจอเปิดอยู่ (นับรอบ ไม่ใช้เวลานาฬิกา — กันปิดเองหลังเครื่องคอมตื่นจาก sleep)
        /// </summary>
        private sealed class LifeMonitor
        {
            private readonly WebServer _server;
            private readonly Process _window;
            private readonly bool _never;
            private readonly DateTime _start = DateTime.Now;
            private int _emptyTicks;

            public LifeMonitor(WebServer server, Process window, bool never)
            {
                _server = server;
                _window = window;
                _never = never;
            }

            /// <summary>เรียกทุก 2 วินาที</summary>
            public bool ShouldExit()
            {
                if (_never) return false;
                if (!_server.EverConnected)
                {
                    // หน้าต่างเปิดไม่สำเร็จ / ผู้ใช้ปิดก่อนโหลดเสร็จ
                    bool windowGone = _window == null || HasExited(_window);
                    return windowGone && (DateTime.Now - _start).TotalSeconds > 90;
                }
                if (_server.ClientCount > 0)
                {
                    _emptyTicks = 0;
                    return false;
                }
                _emptyTicks++;
                bool windowClosed = _window != null && HasExited(_window);
                int limit = windowClosed ? 2 : _window != null ? 6 : 15; // ~4 / 12 / 30 วินาที
                return _emptyTicks >= limit;
            }

            private static bool HasExited(Process p)
            {
                try
                {
                    return p.HasExited;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private static void RunTray(string url, LifeMonitor monitor)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var menu = new ContextMenuStrip();
            menu.Items.Add("เปิดหน้าจอโปรแกรม", null, (s, e) => SafeOpen(url));
            menu.Items.Add("เปิดโฟลเดอร์ log", null, (s, e) => SafeStart(AppSettings.LogDirectory));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("ออกจากโปรแกรม", null, (s, e) => Application.Exit());

            using (var icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Dikai D03S — ส่งข้อมูลเครื่องพิมพ์",
                ContextMenuStrip = menu,
                Visible = true,
            })
            using (var timer = new System.Windows.Forms.Timer { Interval = 2000 })
            {
                icon.DoubleClick += (s, e) => SafeOpen(url);
                timer.Tick += (s, e) =>
                {
                    if (monitor.ShouldExit()) Application.Exit();
                };
                timer.Start();
                Application.Run();
                icon.Visible = false;
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch (Exception)
            {
                return SystemIcons.Application;
            }
        }

        private static void SafeOpen(string url)
        {
            try
            {
                AppWindow.Open(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("เปิดหน้าต่างไม่ได้: " + ex.Message + "\n\nเปิดเบราว์เซอร์ไปที่:\n" + url, "Dikai D03S");
            }
        }

        private static void SafeStart(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private static string LoadHtml(string uiFile)
        {
            if (uiFile != null) return File.ReadAllText(uiFile);
            if (_cachedHtml != null) return _cachedHtml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DikaiD03S.ui.index.html"))
            using (var r = new StreamReader(s ?? throw new InvalidOperationException("ไม่พบหน้าจอใน exe")))
            {
                _cachedHtml = r.ReadToEnd();
            }
            return _cachedHtml;
        }

        private static string NewToken()
        {
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        private static void TryWrite(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception)
            {
                // ไม่จำเป็นต่อการทำงาน
            }
        }

        private static void Fail(string message, Options opt)
        {
            AppLog.Write(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = message });
            Console.Error.WriteLine(message);
            if (!opt.NoTray && AppWindow.IsWindows) MessageBox.Show(message, "Dikai D03S", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("ขาดค่าของ " + args[i]);
                switch (args[i].ToLowerInvariant())
                {
                    case "--no-browser": o.NoBrowser = true; break;
                    case "--no-tray": o.NoTray = true; break;
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--token": o.Token = Next(); break;
                    case "--ui": o.UiFile = Path.GetFullPath(Next()); break;
                    default: throw new ArgumentException("ไม่รู้จักตัวเลือก " + args[i]);
                }
            }
            return o;
        }
    }
}
