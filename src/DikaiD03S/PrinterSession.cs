using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Dikai.Dgcp;

namespace Dikai.App
{
    public enum ConnState
    {
        Disconnected,
        Connecting,
        Connected,
        Failed
    }

    public enum LogKind
    {
        Info,
        Tx,
        Rx,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Time { get; } = DateTime.Now;
        public string Printer { get; set; }
        public LogKind Kind { get; set; }
        public string Text { get; set; }
        public string HexText { get; set; }
        /// <summary>แพ็กเก็ตอ่านสถานะ (3Ah/3Fh) — ซ่อนได้</summary>
        public bool IsStatusFrame { get; set; }
    }

    /// <summary>1 เครื่องพิมพ์ = profile + การเชื่อมต่อ</summary>
    public sealed class PrinterSession : IDisposable
    {
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private DgcpConnection _conn;

        public PrinterProfile Profile { get; }
        public DgcpClient Client { get; private set; }
        public ConnState State { get; private set; } = ConnState.Disconnected;
        public string LastError { get; private set; }
        public DeviceStatus LastStatus { get; set; }
        public int TimeoutMs { get; set; } = 3000;

        public event Action<PrinterSession> StateChanged;
        public event Action<LogEntry> Log;
        public event Action<PrinterSession, DgcpFrame> Unsolicited;

        public PrinterSession(PrinterProfile profile)
        {
            Profile = profile;
        }

        public bool IsConnected => State == ConnState.Connected && _conn != null && _conn.IsOpen;

        /// <summary>ใช้ก่อนทุกคำสั่ง — ถ้ายังไม่เชื่อมต่อจะเชื่อมต่อให้อัตโนมัติ</summary>
        public async Task<DgcpClient> EnsureConnectedAsync()
        {
            if (IsConnected)
            {
                Client.Station = (byte)Profile.Station;
                _conn.TimeoutMs = TimeoutMs;
                return Client;
            }
            await ConnectAsync().ConfigureAwait(true);
            return Client;
        }

        public async Task ConnectAsync()
        {
            await _connectLock.WaitAsync().ConfigureAwait(true);
            try
            {
                if (IsConnected) return;
                CloseConnection(null);
                SetState(ConnState.Connecting, null);
                Write(LogKind.Info, "กำลังเชื่อมต่อ " + Profile.Address + " ...");
                DgcpConnection conn;
                try
                {
                    conn = Profile.IsSerial
                        ? await Task.Run(() => OpenSerial()).ConfigureAwait(true)
                        : await DgcpConnection.ConnectTcpAsync(Profile.Host.Trim(), Profile.Port, Math.Max(TimeoutMs, 3000)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    var msg = ex is DgcpException ? ex.Message : "เชื่อมต่อไม่ได้: " + ex.Message;
                    SetState(ConnState.Failed, msg);
                    Write(LogKind.Error, msg);
                    throw new DgcpException(msg, ex);
                }

                conn.TimeoutMs = TimeoutMs;
                conn.Sent += OnSent;
                conn.Received += f => OnFrame(f, false);
                conn.Garbage += junk => Write(LogKind.Rx, "ข้อมูลที่ไม่ใช่แพ็กเก็ต DGCP " + junk.Length + " ไบต์", Hex.Format(junk));
                conn.Unsolicited += f => Unsolicited?.Invoke(this, f);
                conn.Closed += err => OnClosed(conn, err);
                _conn = conn;
                Client = new DgcpClient(conn, (byte)Profile.Station);
                SetState(ConnState.Connected, null);
                Write(LogKind.Info, "เชื่อมต่อแล้ว " + Profile.Address);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private DgcpConnection OpenSerial()
        {
            if (string.IsNullOrWhiteSpace(Profile.ComPort)) throw new DgcpException("ยังไม่ได้เลือก COM port");
            var port = new SerialPort(Profile.ComPort, Profile.BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                WriteTimeout = 2000,
            };
            try
            {
                port.Open();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                port.Dispose();
                throw new DgcpException("เปิด " + Profile.ComPort + " ไม่ได้: " + ex.Message, ex);
            }
            var conn = new DgcpConnection(port.BaseStream, DgcpLimits.SerialMaxRequestFrame, port);
            conn.Start();
            return conn;
        }

        public void Disconnect()
        {
            if (_conn == null && State == ConnState.Disconnected) return;
            CloseConnection(null);
            SetState(ConnState.Disconnected, null);
            Write(LogKind.Info, "ตัดการเชื่อมต่อแล้ว");
        }

        private void CloseConnection(Exception reason)
        {
            var c = _conn;
            _conn = null;
            Client = null;
            c?.Dispose();
        }

        private void OnClosed(DgcpConnection conn, Exception err)
        {
            if (!ReferenceEquals(conn, _conn)) return; // ปิดเอง / เชื่อมต่อใหม่แล้ว
            _conn = null;
            Client = null;
            var msg = "การเชื่อมต่อหลุด" + (err != null ? ": " + err.Message : "");
            SetState(ConnState.Failed, msg);
            Write(LogKind.Error, msg);
        }

        private void OnSent(byte[] raw)
        {
            DgcpFrame f;
            try
            {
                f = DgcpFrame.FromRaw(raw);
            }
            catch (DgcpException)
            {
                Write(LogKind.Tx, "ส่งไบต์ดิบ " + raw.Length + " ไบต์ (ไม่ใช่แพ็กเก็ต DGCP ที่สมบูรณ์)", Hex.Format(raw));
                return;
            }
            OnFrame(f, true);
        }

        private void OnFrame(DgcpFrame f, bool outgoing)
        {
            Log?.Invoke(new LogEntry
            {
                Printer = Profile.Name,
                Kind = outgoing ? LogKind.Tx : LogKind.Rx,
                Text = DgcpDescriber.Describe(f, outgoing),
                HexText = Hex.Format(f.Raw),
                IsStatusFrame = f.Command == Cmd.GetStatus || f.Command == Cmd.StatusReport,
            });
        }

        public void Write(LogKind kind, string text, string hex = null)
        {
            Log?.Invoke(new LogEntry { Printer = Profile.Name, Kind = kind, Text = text, HexText = hex ?? "" });
        }

        private void SetState(ConnState state, string error)
        {
            State = state;
            LastError = error;
            StateChanged?.Invoke(this);
        }

        public void Dispose()
        {
            CloseConnection(null);
        }
    }
}
