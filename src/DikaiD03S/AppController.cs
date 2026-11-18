using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using Dikai.Dgcp;

namespace Dikai.App
{
    /// <summary>
    /// ตรรกะของโปรแกรม (แทน MainForm เดิม): จัดการรายชื่อเครื่อง, ส่งคำสั่ง, log
    /// หน้าจอ (HTML) เรียกผ่าน /api/* และรับ event สดผ่าน /api/events
    /// </summary>
    internal sealed class AppController : IDisposable
    {
        private const int LogBuffer = 400;

        private readonly AppSettings _settings;
        private readonly object _gate = new object();
        private readonly List<PrinterSession> _sessions = new List<PrinterSession>();
        private readonly HashSet<PrinterSession> _awaitingPrintDone = new HashSet<PrinterSession>();
        private readonly LinkedList<object> _log = new LinkedList<object>();
        private long _logSeq;

        public WebServer Server { get; set; }
        public string Version { get; set; } = "";

        /// <summary>คำสั่งควบคุมที่หน้าจอเรียกได้ (ไม่มีคำสั่งเขียนค่าเครื่อง / handshake)</summary>
        private static readonly byte[] AllowedControl =
        {
            Cmd.Start, Cmd.Stop, Cmd.TestPrint, Cmd.PrintTrigger, Cmd.ClearBuffer, Cmd.ClearAlarm,
            Cmd.ClearWarning, Cmd.ClearPrintCounter, Cmd.ClearFileCounter, Cmd.ClearRibbonCounter,
        };

        public AppController(AppSettings settings, string startupWarning)
        {
            _settings = settings;
            foreach (var p in settings.Printers) AddSession(p);
            if (startupWarning != null) AddLog(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = startupWarning }, null);
            AddLog(new LogEntry { Printer = "-", Kind = LogKind.Info, Text = "เริ่มโปรแกรม — log อยู่ที่ " + AppSettings.LogDirectory }, null);
        }

        private PrinterSession AddSession(PrinterProfile p)
        {
            var s = new PrinterSession(p) { TimeoutMs = _settings.TimeoutMs };
            s.StateChanged += ss => Server?.Broadcast("printer", PrinterDto(ss));
            s.Log += e => AddLog(e, ss: s);
            s.Unsolicited += OnUnsolicited;
            lock (_gate) _sessions.Add(s);
            return s;
        }

        // ================================================================== API

        public async Task<object> HandleApi(string route, JObj b)
        {
            switch (route)
            {
                case "state": return State();
                case "comports": return ComPorts();
                case "settings": return SaveAppSettings(b);
                case "logs/open": return OpenLogFolder();
                case "logs/clear":
                    lock (_log) _log.Clear();
                    return true;
                case "printers/save": return SavePrinter(b);
                case "printers/delete": return DeletePrinter(b);
                case "printers/draft": return SaveDraft(b);
                case "printers/connect":
                {
                    var s = Find(b);
                    s.TimeoutMs = _settings.TimeoutMs;
                    await s.ConnectAsync().ConfigureAwait(false);
                    return PrinterDto(s);
                }
                case "printers/disconnect":
                {
                    var s = Find(b);
                    s.Disconnect();
                    return PrinterDto(s);
                }
                case "preview": return Preview(b);
                case "send": return await SendAsync(b).ConfigureAwait(false);
                case "status":
                {
                    var s = Find(b);
                    var st = await (await Client(s)).GetStatusAsync().ConfigureAwait(false);
                    s.LastStatus = st;
                    return StatusDto(st);
                }
                case "files":
                {
                    var s = Find(b);
                    bool usb = b.Bool("usb");
                    var files = await (await Client(s)).GetFileListAsync(usb).ConfigureAwait(false);
                    if (!usb)
                    {
                        s.Profile.KnownTemplates = files.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        Save();
                    }
                    return files;
                }
                case "control":
                {
                    var s = Find(b);
                    var cmd = (byte)b.Int("cmd", -1);
                    if (Array.IndexOf(AllowedControl, cmd) < 0) throw new ArgumentException("คำสั่งนี้ไม่อนุญาตจากหน้าจอ");
                    await (await Client(s)).ExecuteSimpleAsync(cmd).ConfigureAwait(false);
                    return Cmd.Name(cmd) + " สำเร็จ";
                }
                case "time/get":
                {
                    var s = Find(b);
                    var t = await (await Client(s)).GetTimeAsync().ConfigureAwait(false);
                    return new Dictionary<string, object>
                    {
                        { "device", t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null },
                        { "diffSeconds", t.HasValue ? (object)Math.Round((t.Value - DateTime.Now).TotalSeconds) : null },
                    };
                }
                case "time/set":
                {
                    var s = Find(b);
                    var now = DateTime.Now;
                    await (await Client(s)).SetTimeAsync(now).ConfigureAwait(false);
                    return now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                case "params":
                {
                    var s = Find(b);
                    return await (await Client(s)).GetParametersAsync().ConfigureAwait(false);
                }
                case "raw": return await RawAsync(b).ConfigureAwait(false);
                default: throw new ArgumentException("ไม่รู้จักคำสั่ง " + route);
            }
        }

        private PrinterSession Find(JObj b)
        {
            var id = b.Str("id");
            lock (_gate)
            {
                var s = _sessions.FirstOrDefault(x => x.Profile.Id == id);
                if (s == null) throw new ArgumentException("ไม่พบเครื่องพิมพ์นี้ (อาจถูกลบไปแล้ว)");
                return s;
            }
        }

        private async Task<DgcpClient> Client(PrinterSession s)
        {
            s.TimeoutMs = _settings.TimeoutMs;
            return await s.EnsureConnectedAsync().ConfigureAwait(false);
        }

        private object State()
        {
            List<object> printers;
            lock (_gate) printers = _sessions.Select(PrinterDto).ToList();
            List<object> log;
            lock (_log) log = _log.ToList();
            return new Dictionary<string, object>
            {
                { "version", Version },
                { "printers", printers },
                { "log", log },
                {
                    "settings", new Dictionary<string, object>
                    {
                        { "showStatusFrames", _settings.ShowStatusFrames },
                        { "autoStatusSeconds", _settings.AutoStatusSeconds },
                        { "timeoutMs", _settings.TimeoutMs },
                    }
                },
                { "paths", new Dictionary<string, object> { { "settings", AppSettings.FilePath }, { "logs", AppSettings.LogDirectory } } },
                {
                    "limits", new Dictionary<string, object>
                    {
                        { "tcp", DgcpLimits.TcpMaxRequestFrame },
                        { "serial", DgcpLimits.SerialMaxRequestFrame },
                    }
                },
            };
        }

        private static object PrinterDto(PrinterSession s)
        {
            var p = s.Profile;
            return new Dictionary<string, object>
            {
                { "id", p.Id },
                { "name", p.Name },
                { "kind", p.IsSerial ? "serial" : "tcp" },
                { "host", p.Host },
                { "port", p.Port },
                { "comPort", p.ComPort },
                { "baudRate", p.BaudRate },
                { "station", p.Station },
                { "address", p.Address },
                { "maxFrame", p.MaxFrame },
                { "state", s.State.ToString().ToLowerInvariant() },
                { "lastError", s.LastError },
                {
                    "draft", new Dictionary<string, object>
                    {
                        { "template", p.LastTemplate ?? "" },
                        { "variables", p.Variables.Select(v => new Dictionary<string, object> { { "name", v.Name ?? "" }, { "value", v.Value ?? "" } }).ToList() },
                        { "mode", p.Mode },
                        { "allocation", p.Allocation },
                        { "clearFirst", p.ClearBeforeSend },
                        { "knownTemplates", p.KnownTemplates },
                    }
                },
            };
        }

        private static string[] ComPorts()
        {
            try
            {
                return SerialPort.GetPortNames().Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private object SaveAppSettings(JObj b)
        {
            if (b.Has("showStatusFrames")) _settings.ShowStatusFrames = b.Bool("showStatusFrames");
            if (b.Has("autoStatusSeconds")) _settings.AutoStatusSeconds = Math.Max(1, Math.Min(3600, b.Int("autoStatusSeconds", 5)));
            Save();
            return true;
        }

        private object OpenLogFolder()
        {
            Directory.CreateDirectory(AppSettings.LogDirectory);
            Process.Start(new ProcessStartInfo { FileName = AppSettings.LogDirectory, UseShellExecute = true });
            return true;
        }

        // ------------------------------------------------------------------ printers

        private object SavePrinter(JObj b)
        {
            var name = (b.Str("name") ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("กรุณาใส่ชื่อเครื่อง");
            var kind = b.Str("kind") == "serial" ? "serial" : "tcp";
            var host = (b.Str("host") ?? "").Trim();
            var com = (b.Str("comPort") ?? "").Trim().ToUpperInvariant();
            if (kind == "tcp" && host.Length == 0) throw new ArgumentException("กรุณาใส่ IP address");
            if (kind == "serial" && com.Length == 0) throw new ArgumentException("กรุณาเลือก COM port");
            int port = b.Int("port", DgcpLimits.DefaultTcpPort);
            if (port < 1 || port > 65535) throw new ArgumentException("Port ต้องอยู่ระหว่าง 1–65535");
            int st = b.Int("station", 1);
            if (st < 0 || st > 255) throw new ArgumentException("เลขเครื่อง (ST) ต้องอยู่ระหว่าง 0–255");
            int baud = b.Int("baudRate", DgcpLimits.DefaultBaudRate);

            PrinterSession s;
            var id = b.Str("id");
            if (string.IsNullOrEmpty(id))
            {
                var p = PrinterProfile.CreateDefault(name, host);
                lock (_gate) _settings.Printers.Add(p);
                s = AddSession(p);
            }
            else
            {
                s = Find(b);
            }

            var pr = s.Profile;
            var before = pr.Kind + "|" + pr.Host + "|" + pr.Port + "|" + pr.ComPort + "|" + pr.BaudRate;
            pr.Name = name;
            pr.Kind = kind;
            pr.Host = host.Length > 0 ? host : pr.Host;
            pr.Port = port;
            pr.ComPort = com;
            pr.BaudRate = baud;
            pr.Station = st;
            var after = pr.Kind + "|" + pr.Host + "|" + pr.Port + "|" + pr.ComPort + "|" + pr.BaudRate;
            if (before != after && s.State != ConnState.Disconnected) s.Disconnect();
            if (s.Client != null) s.Client.Station = (byte)st;
            Save();
            var dto = PrinterDto(s);
            Server?.Broadcast("printer", dto);
            return dto;
        }

        private object DeletePrinter(JObj b)
        {
            var s = Find(b);
            lock (_gate)
            {
                _sessions.Remove(s);
                _settings.Printers.Remove(s.Profile);
            }
            lock (_awaitingPrintDone) _awaitingPrintDone.Remove(s);
            s.Dispose();
            Save();
            Server?.Broadcast("printerRemoved", new Dictionary<string, object> { { "id", s.Profile.Id } });
            return true;
        }

        private object SaveDraft(JObj b)
        {
            var s = Find(b);
            ApplyDraft(s.Profile, b);
            Save();
            return true;
        }

        private static void ApplyDraft(PrinterProfile p, JObj b)
        {
            if (b.Has("template")) p.LastTemplate = (b.Str("template") ?? "").Trim();
            if (b.Has("variables"))
            {
                p.Variables = b.Objects("variables")
                    .Select(v => new VariableEntry { Name = (v.Str("name") ?? "").Trim(), Value = v.Str("value") ?? "" })
                    .Where(v => v.Name.Length > 0 || v.Value.Length > 0)
                    .ToList();
            }
            if (b.Has("mode")) p.Mode = Math.Max(0, Math.Min(2, b.Int("mode")));
            if (b.Has("allocation")) p.Allocation = Math.Max(0, Math.Min(65535, b.Int("allocation")));
            if (b.Has("clearFirst")) p.ClearBeforeSend = b.Bool("clearFirst");
        }

        // ------------------------------------------------------------------ send

        private static List<TemplateVariable> ReadVariables(JObj b)
        {
            var list = new List<TemplateVariable>();
            int row = 0;
            foreach (var v in b.Objects("variables"))
            {
                row++;
                var name = (v.Str("name") ?? "").Trim();
                var value = v.Str("value") ?? "";
                if (name.Length == 0 && value.Length == 0) continue;
                if (name.Length == 0) throw new DgcpException("ตัวแปรแถวที่ " + row + " ยังไม่มีชื่อ");
                if (list.Any(x => x.Name == name)) throw new DgcpException("ชื่อตัวแปร \"" + name + "\" ซ้ำกัน");
                list.Add(new TemplateVariable(name, value));
            }
            return list;
        }

        private static byte[] BuildFrame(PrinterProfile p, JObj b, out List<TemplateVariable> vars, out SendMode mode, out string template, out ushort alloc)
        {
            vars = ReadVariables(b);
            mode = (SendMode)Math.Max(0, Math.Min(2, b.Int("mode")));
            template = (b.Str("template") ?? "").Trim();
            alloc = (ushort)Math.Max(0, Math.Min(65535, b.Int("allocation")));
            var st = (byte)p.Station;
            switch (mode)
            {
                case SendMode.Update: return DgcpCommands.UpdateVariables(st, alloc, vars, false);
                case SendMode.UpdateWithFeedback: return DgcpCommands.UpdateVariables(st, alloc, vars, true);
                default: return DgcpCommands.SelectFile(st, alloc, template, vars);
            }
        }

        private object Preview(JObj b)
        {
            var s = Find(b);
            int max = s.Profile.MaxFrame;
            try
            {
                var frame = BuildFrame(s.Profile, b, out _, out _, out _, out _);
                return new Dictionary<string, object> { { "hex", Hex.Format(frame) }, { "length", frame.Length }, { "max", max }, { "error", null } };
            }
            catch (DgcpException ex)
            {
                return new Dictionary<string, object> { { "hex", "" }, { "length", 0 }, { "max", max }, { "error", ex.Message } };
            }
        }

        private async Task<object> SendAsync(JObj b)
        {
            var s = Find(b);
            var frame = BuildFrame(s.Profile, b, out var vars, out var mode, out var template, out var alloc);
            if (frame.Length > s.Profile.MaxFrame)
                throw new DgcpException("ข้อมูลยาว " + frame.Length + " ไบต์ เกินที่เครื่องรับได้ (" + s.Profile.MaxFrame + " ไบต์)");
            bool clearFirst = b.Bool("clearFirst");

            ApplyDraft(s.Profile, b);
            Save();

            var c = await Client(s).ConfigureAwait(false);
            try
            {
                if (clearFirst) await c.ExecuteSimpleAsync(Cmd.ClearBuffer).ConfigureAwait(false);
                switch (mode)
                {
                    case SendMode.Update:
                        await c.UpdateVariablesAsync(vars, alloc, false).ConfigureAwait(false);
                        break;
                    case SendMode.UpdateWithFeedback:
                        lock (_awaitingPrintDone) _awaitingPrintDone.Add(s);
                        await c.UpdateVariablesAsync(vars, alloc, true).ConfigureAwait(false);
                        break;
                    default:
                        await c.SelectFileAsync(template, vars, alloc).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                lock (_awaitingPrintDone) _awaitingPrintDone.Remove(s);
                throw;
            }

            if (mode == SendMode.SelectFile && template.Length > 0 &&
                !s.Profile.KnownTemplates.Contains(template, StringComparer.OrdinalIgnoreCase))
            {
                s.Profile.KnownTemplates.Add(template);
                Save();
            }

            bool waiting;
            lock (_awaitingPrintDone) waiting = _awaitingPrintDone.Contains(s);
            return new Dictionary<string, object>
            {
                { "time", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
                { "mode", (int)mode },
                { "waitingPrintDone", mode == SendMode.UpdateWithFeedback && waiting },
                { "printer", PrinterDto(s) },
            };
        }

        private void OnUnsolicited(PrinterSession s, DgcpFrame f)
        {
            if (f.Command == Cmd.PrintDone)
            {
                bool waited;
                lock (_awaitingPrintDone) waited = _awaitingPrintDone.Remove(s);
                s.Write(LogKind.Info, "เครื่องแจ้งว่าพิมพ์เสร็จแล้ว");
                Server?.Broadcast("printDone", new Dictionary<string, object>
                {
                    { "id", s.Profile.Id },
                    { "waited", waited },
                    { "time", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
                });
            }
            else if (f.Command == Cmd.StatusReport || f.Command == Cmd.GetStatus)
            {
                var st = DgcpResponses.ParseStatus(f.Data);
                s.LastStatus = st;
                Server?.Broadcast("status", new Dictionary<string, object> { { "id", s.Profile.Id }, { "status", StatusDto(st) } });
            }
        }

        private async Task<object> RawAsync(JObj b)
        {
            var s = Find(b);
            byte[] bytes;
            try
            {
                bytes = Hex.Parse(b.Str("hex") ?? "");
            }
            catch (FormatException ex)
            {
                throw new DgcpException(ex.Message);
            }
            if (bytes.Length == 0) throw new DgcpException("ยังไม่ได้พิมพ์ HEX");
            bool wait = b.Bool("wait", true);
            if (b.Bool("autoHeader", true))
            {
                if (bytes.Length < 2) throw new DgcpException("ต้องมีอย่างน้อย ST และ CMD");
                var data = new byte[bytes.Length - 2];
                Buffer.BlockCopy(bytes, 2, data, 0, data.Length);
                bytes = DgcpFrame.Build(bytes[0], bytes[1], data);
            }
            else if (wait && bytes.Length < 6)
            {
                throw new DgcpException("แพ็กเก็ตเต็มต้องมีอย่างน้อย 6 ไบต์ (5A A5 LEN LEN ST CMD)");
            }
            var c = await Client(s).ConfigureAwait(false);
            var reply = await c.Connection.SendRawAsync(bytes, wait).ConfigureAwait(false);
            return new Dictionary<string, object>
            {
                { "sent", Hex.Format(bytes) },
                { "reply", reply == null ? null : Hex.Format(reply.Raw) },
                { "meaning", reply == null ? null : DgcpDescriber.Describe(reply, false) },
            };
        }

        // ------------------------------------------------------------------ status

        private static object StatusDto(DeviceStatus st)
        {
            string tone = st.StatusCode == 3 ? "good" : st.StatusCode == 1 ? "info" : st.StatusCode == 2 ? "warn" : "bad";
            return new Dictionary<string, object>
            {
                { "code", st.StatusCode },
                { "text", st.StatusText },
                { "tone", tone },
                { "fileName", st.FileName },
                { "variables", st.Variables.Select(v => new Dictionary<string, object> { { "name", v.Name }, { "value", v.Value } }).ToList() },
                { "time", Fmt(st.Time) },
                { "ribbonRemaining", st.RibbonRemaining },
                { "lineSpeed", st.LineSpeed },
                { "printFrequency", st.PrintFrequency },
                { "totalCounter", st.TotalCounter },
                { "fileCounter", st.FileCounter },
                { "ribbonCounter", st.RibbonCounter },
                { "previousRibbonCounter", st.PreviousRibbonCounter },
                { "printHeight", st.PrintHeight },
                { "ribbonChangeTime", Fmt(st.RibbonChangeTime) },
                { "hasNames", st.HasNames },
                { "printSignal", st.HasNames ? (object)DgcpStatusText.HasPrintSignal(st.ErrorBits) : null },
                { "encoderSignal", st.HasNames ? (object)DgcpStatusText.HasEncoderSignal(st.ErrorBits) : null },
                { "localStation", st.LocalStation.HasValue ? (object)st.LocalStation.Value : null },
                { "parametersUpdated", st.ParametersUpdated },
                { "errorBits", "0x" + st.ErrorBits.ToString("X8") },
                { "alarms", st.ActiveAlarms().ToList() },
                { "layoutMismatch", st.LayoutMismatch },
                { "readAt", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
            };
        }

        private static string Fmt(DateTime? t)
        {
            return t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null;
        }

        // ------------------------------------------------------------------ log

        private void AddLog(LogEntry e, PrinterSession ss)
        {
            if (e.IsStatusFrame && !_settings.ShowStatusFrames) return;
            AppLog.Write(e);
            var dto = new Dictionary<string, object>
            {
                { "seq", System.Threading.Interlocked.Increment(ref _logSeq) },
                { "time", e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) },
                { "printerId", ss?.Profile.Id },
                { "printer", e.Printer },
                { "kind", e.Kind.ToString().ToLowerInvariant() },
                { "text", e.Text },
                { "hex", e.HexText ?? "" },
            };
            lock (_log)
            {
                _log.AddLast(dto);
                while (_log.Count > LogBuffer) _log.RemoveFirst();
            }
            Server?.Broadcast("log", dto);
        }

        private void Save()
        {
            try
            {
                lock (_gate) _settings.Save();
            }
            catch (Exception ex)
            {
                AddLog(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = "บันทึกไฟล์ตั้งค่าไม่ได้: " + ex.Message }, null);
            }
        }

        public void Dispose()
        {
            Save();
            List<PrinterSession> all;
            lock (_gate) all = _sessions.ToList();
            foreach (var s in all) s.Dispose();
        }
    }
}
