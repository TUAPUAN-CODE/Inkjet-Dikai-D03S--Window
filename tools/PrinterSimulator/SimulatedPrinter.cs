using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dikai.Dgcp;

namespace Dikai.Simulator
{
    /// <summary>
    /// Fake DGCP printer for testing without real hardware.
    /// Behaviour follows the DGCP v2.0 document; anything the document leaves open is a guess.
    /// </summary>
    public sealed class SimulatedPrinter : IDisposable
    {
        public sealed class Options
        {
            /// <summary>ค่าเริ่มต้น 127.0.0.1 (ทดสอบในเครื่องเดียว ไม่ขึ้นหน้าต่าง Firewall) — ใช้ 0.0.0.0 เมื่อทดสอบจากเครื่องอื่น</summary>
            public IPAddress Bind = IPAddress.Loopback;
            public int Port = DgcpLimits.DefaultTcpPort;
            public byte Station = 1;
            /// <summary>Automatic print every N ms while running (0 = only on 4Bh / 44h)</summary>
            public int PrintIntervalMs = 1000;
            public int QueueCapacity = 3;
            public int ExtraFiles;
            public int FileListPageSize = 100;
            public int ReportIntervalSec;
            public int MaxClients = 3;
            /// <summary>ตอบ 41h โดยชื่อไฟล์เป็น 00 ทั้งหมด (พบในเครื่องจริงบางรุ่น)</summary>
            public bool BlankFileNames;
            public Action<string> Log = s => { };
        }

        private sealed class Template
        {
            public string Name;
            public List<TemplateVariable> Variables = new List<TemplateVariable>();
        }

        private sealed class Job
        {
            public List<TemplateVariable> Variables;
            public Session FeedbackTo;
        }

        private sealed class Session
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public string Name;
            public readonly object WriteLock = new object();
        }

        private readonly Options _o;
        private readonly object _state = new object();
        private readonly Dictionary<string, Template> _templates = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _fileOrder = new List<string>();
        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly List<Session> _sessions = new List<Session>();
        private TcpListener _listener;
        private Timer _printTimer;
        private Timer _reportTimer;
        private volatile bool _disposed;

        private Template _current;
        private bool _running = true;
        private uint _total, _fileCounter, _ribbonCounter, _prevRibbonCounter;
        private uint _alarmBits;
        private TimeSpan _clockOffset;
        private DateTime _ribbonChange = DateTime.Now.Date;
        private bool _paramsUpdated;

        public SimulatedPrinter(Options options)
        {
            _o = options;
            AddTemplate("TEST", new TemplateVariable("text1", "1234"));
            AddTemplate("LOT", new TemplateVariable("lot", "A0001"), new TemplateVariable("date", "01/01/2026"));
            for (int i = 1; i <= 5; i++) AddTemplate(i.ToString(CultureInfo.InvariantCulture));
            for (int i = 1; i <= options.ExtraFiles; i++) AddTemplate("FILE" + i.ToString("000", CultureInfo.InvariantCulture));
            _current = _templates["TEST"];
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public uint TotalPrinted { get { lock (_state) return _total; } }

        public string CurrentValue(string variable)
        {
            lock (_state)
            {
                var v = _current?.Variables.FirstOrDefault(x => x.Name == variable);
                return v?.Value;
            }
        }

        private void AddTemplate(string name, params TemplateVariable[] vars)
        {
            _templates[name] = new Template { Name = name, Variables = vars.ToList() };
            _fileOrder.Add(name);
        }

        public void Start()
        {
            _listener = new TcpListener(_o.Bind, _o.Port);
            _listener.Start();
            Task.Run(AcceptLoopAsync);
            if (_o.PrintIntervalMs > 0)
                _printTimer = new Timer(_ => { if (_running) PrintOne("line"); }, null, _o.PrintIntervalMs, _o.PrintIntervalMs);
            if (_o.ReportIntervalSec > 0)
                _reportTimer = new Timer(_ => PushReport(), null, _o.ReportIntervalSec * 1000, _o.ReportIntervalSec * 1000);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_disposed)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (_disposed) return;
                    continue;
                }
                var s = new Session { Client = client, Stream = client.GetStream(), Name = client.Client.RemoteEndPoint.ToString() };
                lock (_state)
                {
                    if (_sessions.Count >= _o.MaxClients)
                    {
                        _o.Log("reject " + s.Name + " (max " + _o.MaxClients + " clients)");
                        client.Close();
                        continue;
                    }
                    _sessions.Add(s);
                }
                _o.Log("connected " + s.Name);
                var _ = Task.Run(() => SessionLoopAsync(s));
            }
        }

        private async Task SessionLoopAsync(Session s)
        {
            var parser = new DgcpFrameParser();
            var buf = new byte[4096];
            try
            {
                while (!_disposed)
                {
                    int n = await s.Stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                    var junk = new List<byte[]>();
                    foreach (var f in parser.Push(buf, 0, n, junk))
                    {
                        if (f.Raw.Length > DgcpLimits.TcpMaxRequestFrame)
                        {
                            Send(s, Error(14));
                            continue;
                        }
                        foreach (var reply in Handle(s, f)) Send(s, reply);
                    }
                    foreach (var j in junk) _o.Log("junk from " + s.Name + ": " + Hex.Format(j));
                }
            }
            catch (Exception)
            {
                // client gone
            }
            lock (_state) _sessions.Remove(s);
            try { s.Client.Close(); } catch { }
            _o.Log("disconnected " + s.Name);
        }

        private void Send(Session s, byte[] frame)
        {
            try
            {
                lock (s.WriteLock) s.Stream.Write(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private byte[] Ok(byte cmd) => DgcpFrame.Build(_o.Station, Cmd.Ok, new[] { cmd });
        private byte[] Error(byte code) => DgcpFrame.Build(_o.Station, Cmd.Error, new[] { code });

        private IEnumerable<byte[]> Handle(Session s, DgcpFrame f)
        {
            if (f.Station != 0 && f.Station != _o.Station)
            {
                _o.Log(s.Name + " -> " + f.Command.ToString("X2") + "h with ST=" + f.Station + " (mine " + _o.Station + ") -> error 10");
                return new[] { Error(10) };
            }
            List<byte[]> r;
            lock (_state) r = HandleLocked(s, f);
            return r;
        }

        private List<byte[]> HandleLocked(Session s, DgcpFrame f)
        {
            var replies = new List<byte[]>();
            string note = "";
            switch (f.Command)
            {
                case Cmd.GetCurrentFile:
                {
                    var name = Encoding.UTF8.GetBytes(_current?.Name ?? "");
                    var data = new byte[1 + name.Length];
                    data[0] = (byte)name.Length;
                    Buffer.BlockCopy(name, 0, data, 1, name.Length);
                    replies.Add(DgcpFrame.Build(_o.Station, Cmd.GetCurrentFile, data));
                    break;
                }
                case Cmd.SelectFile:
                {
                    var r = new ByteReader(f.Data);
                    string file;
                    List<TemplateVariable> vars;
                    try
                    {
                        r.U16();
                        file = r.Text(r.U8());
                        vars = r.Remaining == 1 && f.Data[f.Data.Length - 1] == 0xFF ? new List<TemplateVariable>() : ReadVars(r);
                    }
                    catch (Exception)
                    {
                        replies.Add(Error(2));
                        break;
                    }
                    if (!_templates.TryGetValue(file, out var t))
                    {
                        note = "unknown file '" + file + "'";
                        replies.Add(Error(6));
                        break;
                    }
                    if (!Apply(t, vars, out note))
                    {
                        replies.Add(Error(2));
                        break;
                    }
                    _current = t;
                    _queue.Clear();
                    _fileCounter = 0;
                    note = "file=" + t.Name + " " + Describe(vars);
                    replies.Add(Ok(Cmd.SelectFile));
                    break;
                }
                case Cmd.UpdateVariables:
                case Cmd.UpdateVariablesWithFeedback:
                {
                    if (_current == null)
                    {
                        replies.Add(Error(5));
                        break;
                    }
                    List<TemplateVariable> vars;
                    try
                    {
                        var r = new ByteReader(f.Data);
                        r.U16();
                        vars = ReadVars(r);
                    }
                    catch (Exception)
                    {
                        replies.Add(Error(2));
                        break;
                    }
                    if (!Validate(_current, vars, out note))
                    {
                        replies.Add(Error(2));
                        break;
                    }
                    if (_queue.Count >= _o.QueueCapacity)
                    {
                        note = "queue full";
                        replies.Add(Error(8));
                        break;
                    }
                    _queue.Enqueue(new Job { Variables = vars, FeedbackTo = f.Command == Cmd.UpdateVariablesWithFeedback ? s : null });
                    note = Describe(vars) + " (queued " + _queue.Count + ")";
                    replies.Add(Ok(f.Command));
                    break;
                }
                case Cmd.ClearWarning:
                case Cmd.ClearAlarm:
                    _alarmBits = 0;
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.ClearFileCounter:
                    _fileCounter = 0;
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.ClearBuffer:
                    _queue.Clear();
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.ClearRibbonCounter:
                    _prevRibbonCounter = _ribbonCounter;
                    _ribbonCounter = 0;
                    _ribbonChange = Now();
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.GetStatus:
                    replies.Add(DgcpFrame.Build(_o.Station, Cmd.GetStatus, StatusData(true)));
                    break;
                case Cmd.GetParameters:
                    replies.Add(DgcpFrame.Build(_o.Station, Cmd.GetParameters, ParameterData()));
                    break;
                case Cmd.GetFileList:
                    replies.AddRange(FileListFrames());
                    break;
                case Cmd.GetUsbFileList:
                    replies.Add(Error(16));
                    break;
                case Cmd.Start:
                    _running = true;
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.Stop:
                    _running = false;
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.TestPrint:
                case Cmd.PrintTrigger:
                    replies.Add(Ok(f.Command));
                    ThreadPool.QueueUserWorkItem(_ => PrintOne(f.Command == Cmd.TestPrint ? "test" : "trigger"));
                    break;
                case Cmd.ClearPrintCounter:
                    _total = 0;
                    replies.Add(Ok(f.Command));
                    break;
                case Cmd.GetTime:
                    replies.Add(DgcpFrame.Build(_o.Station, Cmd.GetTime, TimeBytes(Now())));
                    break;
                case Cmd.SetTime:
                {
                    var t = DgcpResponses.ParseTime(f.Data);
                    if (t == null)
                    {
                        replies.Add(Error(2));
                        break;
                    }
                    _clockOffset = t.Value - DateTime.Now;
                    note = t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    replies.Add(Ok(f.Command));
                    break;
                }
                case Cmd.ClearUpdateFlag:
                    _paramsUpdated = false;
                    replies.Add(Ok(f.Command));
                    break;
                default:
                    replies.Add(Error(7));
                    break;
            }

            var answer = replies.Count == 0 ? "-" : DescribeReply(replies[0]) + (replies.Count > 1 ? " +" + (replies.Count - 1) : "");
            _o.Log(s.Name + " -> " + f.Command.ToString("X2") + "h " + (note.Length > 0 ? note + " " : "") + "=> " + answer);
            return replies;
        }

        private static string DescribeReply(byte[] raw)
        {
            var f = DgcpFrame.FromRaw(raw);
            if (f.Command == Cmd.Ok) return "32h ok";
            if (f.Command == Cmd.Error) return "31h error " + (f.Data.Length > 0 ? f.Data[0] : 0);
            return f.Command.ToString("X2") + "h (" + f.Data.Length + " bytes)";
        }

        private static List<TemplateVariable> ReadVars(ByteReader r)
        {
            var list = new List<TemplateVariable>();
            while (r.Remaining > 0)
            {
                var name = r.Text(r.U8());
                var value = r.Text(r.U8());
                list.Add(new TemplateVariable(name, value));
            }
            return list;
        }

        private static string Describe(List<TemplateVariable> vars)
        {
            return string.Join(" ", vars.Select(v => v.Name + "=" + v.Value));
        }

        private static bool Validate(Template t, List<TemplateVariable> vars, out string note)
        {
            note = "";
            foreach (var v in vars)
            {
                if (!t.Variables.Any(x => x.Name == v.Name)) // variable names are case-sensitive
                {
                    note = "unknown variable '" + v.Name + "' in " + t.Name;
                    return false;
                }
            }
            return true;
        }

        private static bool Apply(Template t, List<TemplateVariable> vars, out string note)
        {
            if (!Validate(t, vars, out note)) return false;
            foreach (var v in vars) t.Variables.First(x => x.Name == v.Name).Value = v.Value;
            return true;
        }

        private void PrintOne(string why)
        {
            Session feedback = null;
            string content;
            lock (_state)
            {
                if (_current == null) return;
                if (_queue.Count > 0)
                {
                    var job = _queue.Dequeue();
                    Apply(_current, job.Variables, out _);
                    feedback = job.FeedbackTo;
                }
                else if (why == "line")
                {
                    return; // nothing new to print
                }
                _total++;
                _fileCounter++;
                _ribbonCounter++;
                content = Describe(_current.Variables);
            }
            _o.Log("PRINT (" + why + ") #" + _total + " " + _current.Name + " " + content);
            if (feedback != null) Send(feedback, DgcpFrame.Build(_o.Station, Cmd.PrintDone, new[] { Cmd.UpdateVariablesWithFeedback }));
        }

        private void PushReport()
        {
            byte[] frame;
            List<Session> targets;
            lock (_state)
            {
                frame = DgcpFrame.Build(0x00, Cmd.StatusReport, StatusData(false));
                targets = _sessions.ToList();
            }
            foreach (var s in targets) Send(s, frame);
        }

        private DateTime Now() => DateTime.Now + _clockOffset;

        private static byte[] TimeBytes(DateTime t)
        {
            return new[] { (byte)(t.Year >> 8), (byte)t.Year, (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second };
        }

        /// <summary>3Ah layout (withNames) or 3Fh layout</summary>
        private byte[] StatusData(bool withNames)
        {
            var d = new List<byte>();
            byte status = _alarmBits != 0 ? (byte)5 : _running ? (byte)3 : (byte)1;
            d.Add(status);
            uint bits = _alarmBits | (withNames && _running ? (1u << 22) : 0u);
            U32(d, bits);
            d.AddRange(TimeBytes(Now()));
            d.Add(80);                               // ribbon remaining
            d.Add((byte)(_running ? 30 : 0));        // line speed
            U16(d, (ushort)(_running ? 60 : 0));     // print frequency
            U32(d, _total);
            U32(d, _fileCounter);
            U32(d, _ribbonCounter);
            U32(d, _prevRibbonCounter);
            U16(d, 120);                             // print height
            d.AddRange(TimeBytes(_ribbonChange));
            if (withNames)
            {
                var name = Encoding.UTF8.GetBytes(_current?.Name ?? "");
                d.Add((byte)name.Length);
                if (name.Length > 0)
                {
                    d.AddRange(name);
                    d.Add((byte)_current.Variables.Count);
                    foreach (var v in _current.Variables)
                    {
                        Short(d, v.Name);
                        Short(d, v.Value);
                    }
                }
                d.Add(_o.Station);
                d.Add((byte)(_paramsUpdated ? 1 : 0));
            }
            else
            {
                var vars = _current?.Variables ?? new List<TemplateVariable>();
                d.Add((byte)vars.Count);
                foreach (var v in vars) Short(d, v.Value);
            }
            return d.ToArray();
        }

        private byte[] ParameterData()
        {
            var frame = new byte[158];
            void P16(int off, int v) { frame[off] = (byte)(v >> 8); frame[off + 1] = (byte)v; }
            P16(6, 20); P16(8, 20); P16(10, 20); P16(12, 100);
            P16(44, 15); P16(48, 1); P16(86, 0);
            frame[56] = 0x30; frame[57] = 0x31;      // device id "01"
            frame[60] = 0x30; frame[61] = 0x31;      // line id "01"
            P16(118, _o.Station);
            P16(120, 19200 / 100);
            var ip = ((IPEndPoint)_listener.LocalEndpoint).Address;
            var ipb = ip.Equals(IPAddress.Any) ? new byte[] { 192, 168, 1, 2 } : ip.GetAddressBytes();
            Buffer.BlockCopy(ipb, 0, frame, 122, 4);
            Buffer.BlockCopy(new byte[] { 255, 255, 255, 0 }, 0, frame, 126, 4);
            Buffer.BlockCopy(new byte[] { 192, 168, 1, 1 }, 0, frame, 130, 4);
            P16(134, _o.ReportIntervalSec);
            var data = new byte[158 - 6];
            Buffer.BlockCopy(frame, 6, data, 0, data.Length);
            return data;
        }

        private IEnumerable<byte[]> FileListFrames()
        {
            int total = _fileOrder.Count;
            int width = _o.BlankFileNames ? 12 : Math.Max(8, _fileOrder.Max(n => Encoding.UTF8.GetByteCount(n)));
            int page = Math.Max(1, _o.FileListPageSize);
            for (int start = 0; start < total; start += page)
            {
                int count = Math.Min(page, total - start);
                var d = new List<byte>();
                U16(d, (ushort)total);
                U16(d, (ushort)start);
                U16(d, (ushort)count);
                d.Add((byte)width);
                for (int i = 0; i < count; i++)
                {
                    var b = new byte[width];
                    var nb = Encoding.UTF8.GetBytes(_fileOrder[start + i]);
                    if (!_o.BlankFileNames) Buffer.BlockCopy(nb, 0, b, 0, nb.Length);
                    d.AddRange(b);
                }
                yield return DgcpFrame.Build(_o.Station, Cmd.GetFileList, d.ToArray());
            }
        }

        private static void U16(List<byte> d, ushort v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        private static void U32(List<byte> d, uint v) { d.Add((byte)(v >> 24)); d.Add((byte)(v >> 16)); d.Add((byte)(v >> 8)); d.Add((byte)v); }

        private static void Short(List<byte> d, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            d.Add((byte)b.Length);
            d.AddRange(b);
        }

        public void Dispose()
        {
            _disposed = true;
            _printTimer?.Dispose();
            _reportTimer?.Dispose();
            try { _listener?.Stop(); } catch { }
            lock (_state)
            {
                foreach (var s in _sessions)
                {
                    try { s.Client.Close(); } catch { }
                }
                _sessions.Clear();
            }
        }
    }
}
