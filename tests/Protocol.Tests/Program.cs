using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dikai.Dgcp;
using Dikai.Simulator;

// เทสต์แบบไม่พึ่ง framework: dotnet run --project tests/Protocol.Tests
internal static class Program
{
    private static int _passed, _failed;

    private static async Task<int> Main()
    {
        Run("ตัวอย่างในเอกสาร: สร้างแพ็กเก็ตได้ไบต์ตรงกัน", DocExamples);
        Run("ตัวอย่าง 3Ah ในเอกสาร: แยกแพ็กเก็ตที่มาเป็นก้อนเล็ก + อ่านสถานะ", StatusExample3A);
        Run("ตัวอย่าง 3Fh ในเอกสาร: ตัวแปรไม่มีชื่อ", StatusExample3F);
        Run("33h ทั้งแบบมี/ไม่มีไบต์ความยาว", CurrentFileName);
        Run("41h แพ็กเก็ตแรกในเอกสาร", FileListExample);
        Run("ตัวแยกแพ็กเก็ต: ข้ามขยะ / หัวปลอม / 5A ค้างท้าย", ParserResync);
        Run("Hex.Parse หลายรูปแบบ", HexParse);
        Run("ตรวจความยาวข้อความ (255 ไบต์, ภาษาไทย)", LengthChecks);
        await RunAsync("เชื่อมต่อโปรแกรมจำลอง: อ่านสถานะ / รายชื่อ / เลือกแม่แบบ / error", SimulatorBasics);
        await RunAsync("49h แจ้งพิมพ์เสร็จ (4Ah) + 4Bh สั่งพิมพ์", PrintDoneFeedback);
        await RunAsync("35h คิวเต็ม (08) แล้ว 38h ล้างคิว", QueueFull);
        await RunAsync("รายงานสถานะ 3Fh ที่เครื่องส่งเอง", StatusPush);
        await RunAsync("หมดเวลา เมื่อเครื่องไม่ตอบ", Timeout);
        await RunAsync("ตรวจจับการเชื่อมต่อหลุด", ConnectionDrop);

        Console.WriteLine();
        Console.WriteLine("ผ่าน " + _passed + " / ไม่ผ่าน " + _failed);
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- unit

    private static void DocExamples()
    {
        Bytes("5A A5 00 02 01 33", DgcpCommands.Simple(1, Cmd.GetCurrentFile), "ตัวอย่าง 1 (33h)");
        Bytes("5A A5 00 14 01 34 00 00 04 54 45 53 54 05 74 65 78 74 31 04 31 32 33 34",
            DgcpCommands.SelectFile(1, 0, "TEST", Vars("text1", "1234")), "ตัวอย่าง 2 (34h)");
        Bytes("5A A5 00 0F 01 35 00 00 05 74 65 78 74 31 04 34 34 34 34",
            DgcpCommands.UpdateVariables(1, 0, Vars("text1", "4444"), false), "ตัวอย่าง 3 (35h)");
        Bytes("5A A5 00 0F 01 49 00 00 05 74 65 78 74 31 04 34 34 34 34",
            DgcpCommands.UpdateVariables(1, 0, Vars("text1", "4444"), true), "ตัวอย่าง 4 (49h)");
        Bytes("5A A5 00 07 00 34 00 00 01 31 FF", DgcpCommands.SelectFile(0, 0, "1", null), "ตัวอย่าง 5 (34h ไม่มีตัวแปร)");
        Bytes("5A A5 00 07 00 34 00 01 01 35 FF", DgcpCommands.SelectFile(0, 1, "5", null), "ตัวอย่าง 6 (พิมพ์ 1 ครั้ง)");
        Bytes("5A A5 00 02 01 38", DgcpCommands.Simple(1, Cmd.ClearBuffer), "38h");
        Bytes("5A A5 00 02 01 4C", DgcpCommands.Simple(1, Cmd.ClearUpdateFlag), "4Ch");
        Bytes("5A A5 00 09 01 48 07 EA 0B 12 08 1E 2D", DgcpCommands.SetTime(1, new DateTime(2026, 11, 18, 8, 30, 45)), "48h");
    }

    private static void StatusExample3A()
    {
        var raw = Hex.Parse("5AA5 00 42 01 3A 04 00 20 00 08 08 30 01 01 01 03 04 00 01 00 00 53 68 61 6E 00 00 00 00 00 00 00 00 00 00 00 00 00 39 00 00 00 00 00 00 00 03 31 32 33 02 03 31 31 31 03 61 61 61 03 32 32 32 03 62 62 62 01 01");
        var parser = new DgcpFrameParser();
        var frames = new List<DgcpFrame>();
        var rnd = new Random(7);
        for (int i = 0; i < raw.Length;)
        {
            int n = Math.Min(rnd.Next(1, 6), raw.Length - i);
            frames.AddRange(parser.Push(raw, i, n));
            i += n;
        }
        Equal(1, frames.Count, "ได้ 1 แพ็กเก็ต");
        Equal(Cmd.GetStatus, frames[0].Command, "CMD");
        var s = DgcpResponses.ParseStatus(frames[0].Data);
        Equal(false, s.LayoutMismatch, "รูปแบบตรงเอกสาร");
        Equal(true, s.HasNames, "รูปแบบ 3Ah");
        Equal((byte)4, s.StatusCode, "สถานะ");
        Equal(0x00200008u, s.ErrorBits, "error bits");
        Equal(true, s.ActiveAlarms().Contains("ริบบอนใกล้หมด"), "bit21");
        Equal("123", s.FileName, "ชื่อแม่แบบ");
        Equal(2, s.Variables.Count, "จำนวนตัวแปร");
        Equal("111=aaa,222=bbb", string.Join(",", s.Variables.Select(v => v.Name + "=" + v.Value)), "ตัวแปร");
        Equal((byte?)1, s.LocalStation, "ST ของเครื่อง");
        Equal((bool?)true, s.ParametersUpdated, "ธงอัปเดต");
        Equal((ushort)0x39, s.PrintHeight, "ความสูง");
    }

    private static void StatusExample3F()
    {
        var data = new byte[41].Concat(Hex.Parse("02 03 31 32 33 04 31 32 33 34")).ToArray();
        var s = DgcpResponses.ParseStatus(data);
        Equal(false, s.LayoutMismatch, "รูปแบบตรงเอกสาร");
        Equal(false, s.HasNames, "รูปแบบ 3Fh");
        Equal("123|1234", string.Join("|", s.Variables.Select(v => v.Value)), "ค่าตัวแปร");
        Equal(null, s.Variables[0].Name, "ไม่มีชื่อ");
    }

    private static void CurrentFileName()
    {
        Equal("TEST", DgcpResponses.ParseFileName(Hex.Parse("54 45 53 54")), "แบบตัวอย่าง hex");
        Equal("TEST", DgcpResponses.ParseFileName(Hex.Parse("04 54 45 53 54")), "แบบตาราง");
        Equal("", DgcpResponses.ParseFileName(new byte[0]), "ว่าง");
    }

    private static void FileListExample()
    {
        var raw = Hex.Parse("5AA5 00 31 01 41 00 0f 00 00 00 05 08 00 00 00 00 00 00 00 01 00 00 00 00 00 00 00 02 00 00 00 00 00 00 00 03 00 00 00 00 00 00 00 04 00 00 00 00 00 00 00 05");
        var f = DgcpFrame.FromRaw(raw);
        var p = DgcpResponses.ParseFileList(f.Data);
        Equal(15, p.Total, "ทั้งหมด");
        Equal(0, p.Start, "เริ่ม");
        Equal(5, p.Count, "จำนวน");
        Equal(5, p.Names.Count, "ชื่อ");
    }

    private static void ParserResync()
    {
        var parser = new DgcpFrameParser();
        var junk = new List<byte[]>();
        // ขยะ + หัวปลอม (LEN ใหญ่เกิน) + แพ็กเก็ตจริง + 5A ค้างท้าย
        var frames = parser.Push(Hex.Parse("11 22 5A A5 FF FF 33 5A A5 00 03 01 32 34 5A"), 0, 15, junk);
        Equal(1, frames.Count, "ได้แพ็กเก็ตจริง 1");
        Equal(Cmd.Ok, frames[0].Command, "32h");
        Equal(1, parser.Pending, "เก็บ 5A ไว้รอ");
        frames = parser.Push(Hex.Parse("A5 00 03 01 31 08"), 0, 6, junk);
        Equal(1, frames.Count, "ต่อหัวที่แยกกันได้");
        Equal((byte)8, frames[0].Data[0], "error 08");
        Equal(true, junk.Count > 0, "รายงานขยะ");
    }

    private static void HexParse()
    {
        Bytes("5A A5 00 02", Hex.Parse("5AA5 0002"), "ติดกัน");
        Bytes("5A A5 00 02", Hex.Parse("0x5A,0xA5, 0x00 ,0x02"), "0x");
        Bytes("0A 31", Hex.Parse("A 31h"), "หลักเดียว + h");
        Throws<FormatException>(() => Hex.Parse("5G"), "ตัวอักษรผิด");
    }

    private static void LengthChecks()
    {
        Throws<DgcpException>(() => DgcpCommands.UpdateVariables(1, 0, Vars("v", new string('ก', 86)), false), "ไทย 86 ตัว = 258 ไบต์");
        DgcpCommands.UpdateVariables(1, 0, Vars("v", new string('ก', 85)), false); // 255 ไบต์ ผ่าน
        Throws<DgcpException>(() => DgcpCommands.SelectFile(1, 0, " ", null), "ชื่อแม่แบบว่าง");
        Throws<DgcpException>(() => DgcpCommands.UpdateVariables(1, 0, new List<TemplateVariable>(), false), "ไม่มีตัวแปร");
        var thai = DgcpCommands.UpdateVariables(1, 0, Vars("text1", "ไทย"), false);
        Bytes("5A A5 00 14 01 35 00 00 05 74 65 78 74 31 09 E0 B9 84 E0 B8 97 E0 B8 A2", thai, "UTF-8");
    }

    // ---------------------------------------------------------------- integration

    private static SimulatedPrinter StartSim(int printMs = 0, int extraFiles = 0, int page = 100, int queue = 3, int report = 0)
    {
        var sim = new SimulatedPrinter(new SimulatedPrinter.Options
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            PrintIntervalMs = printMs,
            ExtraFiles = extraFiles,
            FileListPageSize = page,
            QueueCapacity = queue,
            ReportIntervalSec = report,
        });
        sim.Start();
        return sim;
    }

    private static async Task SimulatorBasics()
    {
        using (var sim = StartSim(extraFiles: 150, page: 100))
        using (var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", sim.Port, 2000))
        {
            var c = new DgcpClient(conn, 1);
            var st = await c.GetStatusAsync();
            Equal(false, st.LayoutMismatch, "3Ah ตรงรูปแบบ");
            Equal("TEST", st.FileName, "แม่แบบเริ่มต้น");
            Equal("1234", st.Variables.Single(v => v.Name == "text1").Value, "ค่าเริ่มต้น");

            var files = await c.GetFileListAsync();
            Equal(2 + 5 + 150, files.Count, "รายชื่อหลายแพ็กเก็ต");
            Equal("FILE150", files.Last(), "ชื่อสุดท้าย");

            await c.SelectFileAsync("lot", Vars("lot", "B999"), 0);
            Equal("LOT", await c.GetCurrentFileAsync(), "33h หลังเปลี่ยนแม่แบบ");
            Equal("B999", sim.CurrentValue("lot"), "ค่าเข้าเครื่อง");

            await c.SelectFileAsync("TEST", Vars("text1", "ไทย123"), 0);
            st = await c.GetStatusAsync();
            Equal("ไทย123", st.Variables.Single(v => v.Name == "text1").Value, "ภาษาไทยไป-กลับ");

            var ex = await ThrowsAsync<DgcpDeviceException>(() => c.SelectFileAsync("NOPE", null, 0), "แม่แบบไม่มี");
            Equal(6, ex.Code, "error 06");
            ex = await ThrowsAsync<DgcpDeviceException>(() => c.UpdateVariablesAsync(Vars("TEXT1", "x"), 0, false), "ชื่อตัวแปรตัวพิมพ์ไม่ตรง");
            Equal(2, ex.Code, "error 02");

            c.Station = 7;
            ex = await ThrowsAsync<DgcpDeviceException>(() => c.GetStatusAsync(), "ST ผิด");
            Equal(10, ex.Code, "error 10");
            c.Station = 0;
            await c.ExecuteSimpleAsync(Cmd.ClearAlarm); // ST 0 = ทุกเครื่อง
            c.Station = 1;

            var t = new DateTime(2030, 1, 2, 3, 4, 5);
            await c.SetTimeAsync(t);
            var back = await c.GetTimeAsync();
            Equal(true, back.HasValue && Math.Abs((back.Value - t).TotalSeconds) < 3, "ตั้ง/อ่านเวลา");

            var ps = await c.GetParametersAsync();
            Equal("1", ps.Single(p => p.Name.StartsWith("เลขเครื่อง")).Value, "40h เลขเครื่อง");
            Equal("127.0.0.1", ps.Single(p => p.Name == "IP address").Value, "40h IP");

            ex = await ThrowsAsync<DgcpDeviceException>(() => c.GetFileListAsync(usb: true), "USB");
            Equal(16, ex.Code, "error 16");

            await ThrowsAsync<DgcpException>(() => c.UpdateVariablesAsync(Enumerable.Range(0, 5).Select(i => new TemplateVariable("v" + i, new string('ก', 80))).ToList(), 0, false), "แพ็กเก็ตเกิน 1010 ไบต์");
            Equal(true, conn.IsOpen, "ยังเชื่อมต่ออยู่หลัง error");
        }
    }

    private static async Task PrintDoneFeedback()
    {
        using (var sim = StartSim())
        using (var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", sim.Port, 2000))
        {
            var c = new DgcpClient(conn, 1);
            var done = new TaskCompletionSource<bool>();
            conn.Unsolicited += f =>
            {
                if (f.Command == Cmd.PrintDone) done.TrySetResult(true);
            };
            await c.UpdateVariablesAsync(Vars("text1", "5555"), 0, waitPrintDone: true);
            Equal(false, done.Task.IsCompleted, "ยังไม่พิมพ์");
            await c.ExecuteSimpleAsync(Cmd.PrintTrigger);
            var finished = await Task.WhenAny(done.Task, Task.Delay(3000)) == done.Task;
            Equal(true, finished, "ได้ 4Ah");
            Equal("5555", sim.CurrentValue("text1"), "พิมพ์ค่าใหม่");
            Equal(1u, sim.TotalPrinted, "นับ 1");
        }
    }

    private static async Task QueueFull()
    {
        using (var sim = StartSim(queue: 2))
        using (var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", sim.Port, 2000))
        {
            var c = new DgcpClient(conn, 1);
            await c.UpdateVariablesAsync(Vars("text1", "1"), 0, false);
            await c.UpdateVariablesAsync(Vars("text1", "2"), 0, false);
            var ex = await ThrowsAsync<DgcpDeviceException>(() => c.UpdateVariablesAsync(Vars("text1", "3"), 0, false), "คิวเต็ม");
            Equal(8, ex.Code, "error 08");
            await c.ExecuteSimpleAsync(Cmd.ClearBuffer);
            await c.UpdateVariablesAsync(Vars("text1", "3"), 0, false);
        }
    }

    private static async Task StatusPush()
    {
        using (var sim = StartSim(report: 1))
        using (var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", sim.Port, 2000))
        {
            var got = new TaskCompletionSource<DeviceStatus>();
            conn.Unsolicited += f =>
            {
                if (f.Command == Cmd.StatusReport) got.TrySetResult(DgcpResponses.ParseStatus(f.Data));
            };
            var ok = await Task.WhenAny(got.Task, Task.Delay(3000)) == got.Task;
            Equal(true, ok, "ได้ 3Fh");
            Equal(false, got.Task.Result.LayoutMismatch, "3Fh ตรงรูปแบบ");
            Equal("1234", got.Task.Result.Variables[0].Value, "ค่าตัวแปร");
        }
    }

    private static async Task Timeout()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var accept = silent.AcceptTcpClientAsync();
            using (var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port, 2000))
            {
                conn.TimeoutMs = 400;
                var c = new DgcpClient(conn, 1);
                await ThrowsAsync<DgcpTimeoutException>(() => c.GetStatusAsync(), "ไม่ตอบ");
                Equal(true, conn.IsOpen, "ยังเชื่อมต่อ");
                await ThrowsAsync<DgcpTimeoutException>(() => c.GetStatusAsync(), "ส่งต่อได้หลังหมดเวลา");
            }
            (await accept).Close();
        }
        finally
        {
            silent.Stop();
        }
        await ThrowsAsync<DgcpException>(() => DgcpConnection.ConnectTcpAsync("127.0.0.1", 1, 1000), "พอร์ตไม่มีใครฟัง");
    }

    private static async Task ConnectionDrop()
    {
        var sim = StartSim();
        var conn = await DgcpConnection.ConnectTcpAsync("127.0.0.1", sim.Port, 2000);
        var closed = new TaskCompletionSource<bool>();
        conn.Closed += e => closed.TrySetResult(true);
        sim.Dispose();
        var ok = await Task.WhenAny(closed.Task, Task.Delay(3000)) == closed.Task;
        Equal(true, ok, "ได้ event Closed");
        Equal(false, conn.IsOpen, "IsOpen = false");
        await ThrowsAsync<DgcpException>(() => new DgcpClient(conn, 1).GetStatusAsync(), "ส่งหลังหลุด");
        conn.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private static List<TemplateVariable> Vars(params string[] kv)
    {
        var list = new List<TemplateVariable>();
        for (int i = 0; i < kv.Length; i += 2) list.Add(new TemplateVariable(kv[i], kv[i + 1]));
        return list;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Pass(name);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            var t = test();
            if (await Task.WhenAny(t, Task.Delay(20000)) != t) throw new Exception("เทสต์ค้างเกิน 20 วินาที");
            await t;
            Pass(name);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine("  ✔ " + name);
    }

    private static void Fail(string name, Exception ex)
    {
        _failed++;
        Console.WriteLine("  ✘ " + name + "\n      " + ex.Message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(what + ": คาดว่า [" + expected + "] ได้ [" + actual + "]");
    }

    private static void Bytes(string expectedHex, byte[] actual, string what)
    {
        Equal(Hex.Format(Hex.Parse(expectedHex)), Hex.Format(actual), what);
    }

    private static void Throws<T>(Action a, string what) where T : Exception
    {
        try
        {
            a();
        }
        catch (T)
        {
            return;
        }
        throw new Exception(what + ": คาดว่าจะเกิด " + typeof(T).Name);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> a, string what) where T : Exception
    {
        try
        {
            await a();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new Exception(what + ": คาดว่าจะเกิด " + typeof(T).Name);
    }
}
