using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Dikai.App
{
    /// <summary>เรียกคำสั่งของหน้าผู้ดูแล (ควบคุม / ข้อมูลเครื่อง / HEX) ขณะยังล็อกอยู่</summary>
    internal sealed class AdminLockedException : Exception
    {
        public AdminLockedException() : base("หน้านี้สำหรับผู้ดูแล — ใส่รหัสผ่านก่อน (หรือล็อกอัตโนมัติไปแล้วเพราะไม่ได้ใช้งาน)") { }
    }

    /// <summary>
    /// รหัสผ่านผู้ดูแล: ปลดล็อกแล้วใช้หน้า ควบคุม / ข้อมูลเครื่อง / HEX ได้ ไม่ได้ใช้งาน 10 นาทีล็อกเอง
    /// เก็บเป็น PBKDF2-SHA256 ในไฟล์ตั้งค่า (ถ้ายังไม่เคยตั้ง ใช้รหัสเริ่มต้น <see cref="DefaultPassword"/>)
    /// สถานะปลดล็อกอยู่ในหน่วยความจำเท่านั้น — เปิดโปรแกรมใหม่ล็อกเสมอ
    /// </summary>
    internal sealed class AdminLock
    {
        public const string DefaultPassword = "1234";
        public const int MinLength = 4;
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

        private const string Scheme = "pbkdf2-sha256";
        private const int Iterations = 50000;

        private readonly AppSettings _settings;
        private readonly TimeSpan _idle;
        private readonly object _gate = new object();
        private DateTime _until = DateTime.MinValue;
        private int _failures;

        public AdminLock(AppSettings settings, TimeSpan? idleTimeout = null)
        {
            _settings = settings;
            _idle = idleTimeout ?? IdleTimeout;
        }

        public bool IsDefaultPassword => string.IsNullOrEmpty(_settings.AdminPassword);

        public bool IsUnlocked
        {
            get { lock (_gate) return DateTime.UtcNow < _until; }
        }

        public int RemainingSeconds
        {
            get { lock (_gate) return Math.Max(0, (int)Math.Ceiling((_until - DateTime.UtcNow).TotalSeconds)); }
        }

        /// <returns>false ถ้ารหัสผิด (หน่วงเวลาก่อนตอบ กันเดารหัสรัวๆ)</returns>
        public async Task<bool> UnlockAsync(string password)
        {
            if (Verify(password ?? ""))
            {
                lock (_gate)
                {
                    _failures = 0;
                    _until = DateTime.UtcNow + _idle;
                }
                return true;
            }
            int n;
            lock (_gate) n = ++_failures;
            await Task.Delay(Math.Min(4000, 500 * n)).ConfigureAwait(false);
            return false;
        }

        public void Lock()
        {
            lock (_gate) _until = DateTime.MinValue;
        }

        /// <summary>ผู้ใช้ยังใช้งานอยู่ → เลื่อนเวลาล็อกอัตโนมัติออกไป (ถ้าล็อกไปแล้วไม่ทำอะไร)</summary>
        public void Touch()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow < _until) _until = DateTime.UtcNow + _idle;
            }
        }

        /// <summary>ต้องปลดล็อกอยู่ ไม่งั้นโยน <see cref="AdminLockedException"/></summary>
        public void Require()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow >= _until) throw new AdminLockedException();
                _until = DateTime.UtcNow + _idle;
            }
        }

        /// <summary>เปลี่ยนรหัส (ต้องปลดล็อกอยู่ และใส่รหัสปัจจุบันถูก) — ผู้เรียกต้องบันทึกไฟล์ตั้งค่าเอง</summary>
        public async Task ChangePasswordAsync(string current, string next)
        {
            Require();
            next = next ?? "";
            if (next.Length < MinLength) throw new ArgumentException("รหัสผ่านใหม่ต้องยาวอย่างน้อย " + MinLength + " ตัว");
            if (next.Length > 64) throw new ArgumentException("รหัสผ่านใหม่ยาวเกิน 64 ตัว");
            if (!Verify(current ?? ""))
            {
                await Task.Delay(500).ConfigureAwait(false);
                throw new ArgumentException("รหัสผ่านปัจจุบันไม่ถูกต้อง");
            }
            _settings.AdminPassword = Hash(next);
        }

        private bool Verify(string password)
        {
            var stored = _settings.AdminPassword;
            if (string.IsNullOrEmpty(stored)) return FixedEquals(password, DefaultPassword);

            // pbkdf2-sha256$รอบ$salt$hash
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iter) || iter < 1) return false;
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expect = Convert.FromBase64String(parts[3]);
                var got = Derive(password, salt, iter, expect.Length);
                int diff = got.Length ^ expect.Length;
                for (int i = 0; i < Math.Min(got.Length, expect.Length); i++) diff |= got[i] ^ expect[i];
                return diff == 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string Hash(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            var hash = Derive(password, salt, Iterations, 32);
            return Scheme + "$" + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }

        private static bool FixedEquals(string a, string b)
        {
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
