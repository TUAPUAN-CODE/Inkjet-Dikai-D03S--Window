using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Dikai.App
{
    /// <summary>
    /// เปิดหน้าจอโปรแกรมเป็น "หน้าต่างแอป" (ไม่มีแถบที่อยู่/แท็บ) ด้วย Microsoft Edge ที่มากับ Windows 10/11
    /// ถ้าไม่พบ Edge/Chrome จะเปิดด้วยเบราว์เซอร์เริ่มต้นแทน
    /// </summary>
    internal static class AppWindow
    {
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <returns>process ของหน้าต่าง (null ถ้าเปิดด้วยเบราว์เซอร์เริ่มต้น)</returns>
        public static Process Open(string url)
        {
            if (!IsWindows) return null;
            var browser = FindAppBrowser();
            if (browser != null)
            {
                try
                {
                    // โปรไฟล์แยกของโปรแกรม: หน้าต่างเป็นอิสระจาก Edge ที่ผู้ใช้เปิดอยู่ และไม่ขึ้นหน้าต้อนรับ
                    var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DikaiD03S", "app-window");
                    Directory.CreateDirectory(profile);
                    var args = "--app=\"" + url + "\" --user-data-dir=\"" + profile + "\"" +
                               " --no-first-run --no-default-browser-check --hide-crash-restore-bubble --disable-features=Translate,msUndersideButton" +
                               " --window-size=1400,900";
                    return Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false });
                }
                catch (Exception)
                {
                    // ตกไปใช้เบราว์เซอร์เริ่มต้น
                }
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }

        private static string FindAppBrowser()
        {
            foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
            {
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    try
                    {
                        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                        {
                            var path = key?.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path.Trim('"'))) return path.Trim('"');
                        }
                    }
                    catch (Exception)
                    {
                        // ไม่มีสิทธิ์อ่าน registry
                    }
                }
            }

            var candidates = new[]
            {
                Path.Combine(Env("ProgramFiles(x86)"), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Env("ProgramFiles"), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Env("ProgramFiles"), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Env("ProgramFiles(x86)"), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Env("LocalAppData"), @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        private static string Env(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? "";
        }
    }
}
