using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Dikai.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // เปิดได้ทีละ 1 หน้าต่าง: กันสองโปรแกรมส่งชนกันไปเครื่องเดียว และเขียนไฟล์ตั้งค่าทับกัน
            using (var mutex = new Mutex(true, "Dikai.D03S.Sender.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("โปรแกรมเปิดอยู่แล้ว (ดูที่แถบงานด้านล่าง)", "Dikai D03S", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ShowError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
                TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();

                AppSettings.ResolveBaseDirectory();
                var settings = AppSettings.Load(out string warning);
                AppLog.Enabled = settings.LogToFile;
                AppLog.CleanupOld();

                Application.Run(new MainForm(settings, warning));
                GC.KeepAlive(mutex);
            }
        }

        private static void ShowError(Exception ex)
        {
            var msg = ex == null ? "ไม่ทราบสาเหตุ" : ex.GetType().Name + ": " + ex.Message;
            AppLog.Write(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = "Unhandled: " + (ex?.ToString() ?? msg) });
            MessageBox.Show("เกิดข้อผิดพลาดที่ไม่คาดคิด:\n" + msg + "\n\nรายละเอียดอยู่ในไฟล์ log", "Dikai D03S", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
