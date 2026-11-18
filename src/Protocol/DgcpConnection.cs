using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Dikai.Dgcp
{
    /// <summary>
    /// การเชื่อมต่อ 1 เครื่อง (TCP หรือ Serial ก็ได้ — ส่ง Stream เข้ามา)
    /// เครื่องทำงานแบบ "ส่ง 1 รอตอบ 1" จึงส่งคำสั่งทีละคำสั่ง (คิวในตัว)
    /// แพ็กเก็ตที่ไม่ได้เป็นคำตอบของคำสั่งที่รออยู่ (เช่น 4Ah พิมพ์เสร็จ, 3Fh รายงานสถานะ) ออกทาง Unsolicited
    /// </summary>
    public sealed class DgcpConnection : IDisposable
    {
        private readonly Stream _stream;
        private readonly IDisposable _owner;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private readonly DgcpFrameParser _parser = new DgcpFrameParser();
        private Pending _pending;
        private int _closed;

        public int MaxRequestFrame { get; }
        public int TimeoutMs { get; set; } = 3000;
        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        /// <summary>ทุกแพ็กเก็ตที่ส่งออก (สำหรับ log)</summary>
        public event Action<byte[]> Sent;
        /// <summary>ทุกแพ็กเก็ตที่รับเข้า (สำหรับ log)</summary>
        public event Action<DgcpFrame> Received;
        /// <summary>แพ็กเก็ตที่ไม่ใช่คำตอบของคำสั่งที่รออยู่</summary>
        public event Action<DgcpFrame> Unsolicited;
        /// <summary>ไบต์ที่ไม่ใช่แพ็กเก็ต DGCP</summary>
        public event Action<byte[]> Garbage;
        /// <summary>การเชื่อมต่อปิด (null = ปิดเอง)</summary>
        public event Action<Exception> Closed;

        public DgcpConnection(Stream stream, int maxRequestFrame, IDisposable owner = null)
        {
            _stream = stream;
            _owner = owner;
            MaxRequestFrame = maxRequestFrame;
        }

        public static async Task<DgcpConnection> ConnectTcpAsync(string host, int port, int timeoutMs)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                var connect = client.ConnectAsync(host, port);
                var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != connect)
                {
                    client.Close();
                    ObserveFault(connect);
                    throw new DgcpException("เชื่อมต่อ " + host + ":" + port + " ไม่ได้ภายใน " + timeoutMs / 1000 + " วินาที (ตรวจ IP / สายแลน / ไฟเครื่อง)");
                }
                await connect.ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                client.Close();
                throw new DgcpException("เชื่อมต่อ " + host + ":" + port + " ไม่ได้: " + ex.Message, ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new DgcpException("เชื่อมต่อ " + host + ":" + port + " ไม่ได้", ex);
            }
            EnableKeepAlive(client.Client);
            var conn = new DgcpConnection(client.GetStream(), DgcpLimits.TcpMaxRequestFrame, client);
            conn.Start();
            return conn;
        }

        /// <summary>ตรวจสายหลุดทุก ~15 วินาทีขณะไม่ได้ส่งอะไร (ค่าเริ่มต้นของ Windows คือ 2 ชั่วโมง)</summary>
        private static void EnableKeepAlive(Socket s)
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var v = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(v, 0);      // on
                BitConverter.GetBytes(15000u).CopyTo(v, 4);  // เริ่มตรวจหลังว่าง 15 วินาที
                BitConverter.GetBytes(3000u).CopyTo(v, 8);   // ถามซ้ำทุก 3 วินาที
                s.IOControl(IOControlCode.KeepAliveValues, v, null);
            }
            catch (Exception)
            {
                // บางระบบไม่รองรับ — ไม่จำเป็นต่อการทำงาน
            }
        }

        private static void ObserveFault(Task t)
        {
            t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>เริ่มอ่านข้อมูลจากเครื่อง (เรียกครั้งเดียวหลังสร้าง)</summary>
        public void Start()
        {
            Task.Run(ReadLoopAsync);
        }

        private async Task ReadLoopAsync()
        {
            var buf = new byte[4096];
            Exception error = null;
            try
            {
                while (IsOpen)
                {
                    int n = await _stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        error = new IOException("เครื่องปิดการเชื่อมต่อ");
                        break;
                    }
                    var junk = new List<byte[]>();
                    var frames = _parser.Push(buf, 0, n, junk);
                    foreach (var j in junk) Raise(Garbage, j);
                    foreach (var f in frames) Dispatch(f);
                }
            }
            catch (Exception ex)
            {
                error = IsOpen ? ex : null;
            }
            Shutdown(error);
        }

        private void Dispatch(DgcpFrame frame)
        {
            Raise(Received, frame);
            Pending p;
            lock (_gate) p = _pending;
            if (p != null && p.Offer(frame)) return;
            Raise(Unsolicited, frame);
        }

        private static void Raise<T>(Action<T> handler, T arg)
        {
            if (handler == null) return;
            try
            {
                handler(arg);
            }
            catch
            {
                // ห้ามให้ event handler ทำ read loop ล้ม
            }
        }

        // ------------------------------------------------------------------ คำสั่ง

        /// <summary>ส่งคำสั่งที่ตอบ 32h (สำเร็จ) / 31h (ผิดพลาด)</summary>
        public async Task ExecuteAsync(byte[] frame, CancellationToken ct = default(CancellationToken))
        {
            byte cmd = frame[5];
            var reply = await RequestAsync(frame, f =>
            {
                if (f.Command == Cmd.Error) return Match.Done;
                if (f.Command == Cmd.Ok && (f.Data.Length == 0 || f.Data[0] == cmd)) return Match.Done;
                return Match.No;
            }, ct).ConfigureAwait(false);
            ThrowIfError(reply[0], cmd);
        }

        /// <summary>ส่งคำสั่งอ่านค่า — คำตอบมี CMD ตรงกับ accept</summary>
        public async Task<DgcpFrame> QueryAsync(byte[] frame, byte[] acceptCommands, CancellationToken ct = default(CancellationToken))
        {
            byte cmd = frame[5];
            var reply = await RequestAsync(frame, f =>
            {
                if (f.Command == Cmd.Error) return Match.Done;
                return Array.IndexOf(acceptCommands, f.Command) >= 0 ? Match.Done : Match.No;
            }, ct).ConfigureAwait(false);
            ThrowIfError(reply[0], cmd);
            return reply[0];
        }

        /// <summary>41h / A0h — รวมทุกแพ็กเก็ตจนได้ครบตามจำนวนไฟล์ทั้งหมด</summary>
        public async Task<List<string>> QueryFileListAsync(byte[] frame, CancellationToken ct = default(CancellationToken))
        {
            byte cmd = frame[5];
            var names = new List<string>();
            // เอกสารตัวอย่างของ 41h ใส่ CMD เป็น 40 จึงรับทั้งสองค่า
            var accept = cmd == Cmd.GetFileList ? new[] { Cmd.GetFileList, Cmd.GetParameters } : new[] { cmd };
            var replies = await RequestAsync(frame, f =>
            {
                if (f.Command == Cmd.Error) return Match.Done;
                if (Array.IndexOf(accept, f.Command) < 0) return Match.No;
                FileListPacket p;
                try
                {
                    p = DgcpResponses.ParseFileList(f.Data);
                }
                catch (IndexOutOfRangeException)
                {
                    return Match.No;
                }
                names.AddRange(p.Names);
                return p.Count == 0 || p.Start + p.Count >= p.Total ? Match.Done : Match.More;
            }, ct).ConfigureAwait(false);
            ThrowIfError(replies[replies.Count - 1], cmd);
            return names;
        }

        /// <summary>ส่งไบต์ดิบ แล้วรอแพ็กเก็ตถัดไป (waitReply=false = ไม่รอ)</summary>
        public async Task<DgcpFrame> SendRawAsync(byte[] raw, bool waitReply, CancellationToken ct = default(CancellationToken))
        {
            if (!waitReply)
            {
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await WriteAsync(raw, ct).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
                return null;
            }
            var reply = await RequestAsync(raw, f => Match.Done, ct).ConfigureAwait(false);
            return reply[0];
        }

        private static void ThrowIfError(DgcpFrame f, byte requestCmd)
        {
            if (f.Command != Cmd.Error) return;
            throw new DgcpDeviceException(f.Data.Length > 0 ? f.Data[0] : -1, requestCmd);
        }

        private enum Match { No, More, Done }

        private sealed class Pending
        {
            private readonly Func<DgcpFrame, Match> _match;
            public readonly List<DgcpFrame> Frames = new List<DgcpFrame>();
            public readonly TaskCompletionSource<List<DgcpFrame>> Tcs =
                new TaskCompletionSource<List<DgcpFrame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LastActivity = Environment.TickCount;

            public Pending(Func<DgcpFrame, Match> match)
            {
                _match = match;
            }

            public bool Offer(DgcpFrame f)
            {
                if (Tcs.Task.IsCompleted) return false;
                var m = _match(f);
                if (m == Match.No) return false;
                Frames.Add(f);
                Interlocked.Exchange(ref LastActivity, Environment.TickCount);
                if (m == Match.Done) Tcs.TrySetResult(Frames);
                return true;
            }
        }

        private async Task<List<DgcpFrame>> RequestAsync(byte[] frame, Func<DgcpFrame, Match> match, CancellationToken ct)
        {
            if (frame.Length > MaxRequestFrame)
                throw new DgcpException("ข้อมูลยาว " + frame.Length + " ไบต์ เกินที่เครื่องรับได้ (" + MaxRequestFrame + " ไบต์ต่อครั้ง)");
            byte cmd = frame.Length > 5 ? frame[5] : (byte)0;

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            var p = new Pending(match);
            try
            {
                lock (_gate) _pending = p;
                await WriteAsync(frame, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref p.LastActivity, Environment.TickCount);

                // หมดเวลาเมื่อไม่มีแพ็กเก็ตเข้ามานาน TimeoutMs (นับใหม่ทุกครั้งที่ได้แพ็กเก็ตของคำสั่งนี้)
                while (!p.Tcs.Task.IsCompleted)
                {
                    int idle = unchecked(Environment.TickCount - Volatile.Read(ref p.LastActivity));
                    int left = TimeoutMs - idle;
                    if (left <= 0) throw new DgcpTimeoutException(cmd, TimeoutMs);
                    var delay = Task.Delay(left, ct);
                    await Task.WhenAny(p.Tcs.Task, delay).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                return await p.Tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_pending == p) _pending = null;
                }
                _sendLock.Release();
            }
        }

        private async Task WriteAsync(byte[] frame, CancellationToken ct)
        {
            if (!IsOpen) throw new DgcpException("ยังไม่ได้เชื่อมต่อ หรือการเชื่อมต่อถูกตัดแล้ว");
            Raise(Sent, frame); // log ก่อนเขียน เพื่อให้ลำดับ log ส่ง→รับ ถูกต้อง
            try
            {
                await _stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Shutdown(ex);
                throw new DgcpException("ส่งข้อมูลไม่สำเร็จ การเชื่อมต่อถูกตัด", ex);
            }
        }

        // ------------------------------------------------------------------ ปิด

        private void Shutdown(Exception error)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Pending p;
            lock (_gate)
            {
                p = _pending;
                _pending = null;
            }
            p?.Tcs.TrySetException(new DgcpException("การเชื่อมต่อถูกตัด", error));
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // ignore
            }
            try
            {
                _owner?.Dispose();
            }
            catch
            {
                // ignore
            }
            Raise(Closed, error);
        }

        public void Dispose()
        {
            Shutdown(null);
        }
    }

    /// <summary>คำสั่งระดับสูงสำหรับ 1 เครื่อง (ใส่เลขเครื่อง ST ให้อัตโนมัติ)</summary>
    public sealed class DgcpClient
    {
        public DgcpConnection Connection { get; }
        public byte Station { get; set; }

        public DgcpClient(DgcpConnection connection, byte station)
        {
            Connection = connection;
            Station = station;
        }

        public async Task<string> GetCurrentFileAsync()
        {
            var f = await Connection.QueryAsync(DgcpCommands.Simple(Station, Cmd.GetCurrentFile), new[] { Cmd.GetCurrentFile }).ConfigureAwait(false);
            return DgcpResponses.ParseFileName(f.Data);
        }

        public Task SelectFileAsync(string fileName, IList<TemplateVariable> variables, ushort allocation)
        {
            return Connection.ExecuteAsync(DgcpCommands.SelectFile(Station, allocation, fileName, variables));
        }

        /// <summary>waitPrintDone=true ใช้ 49h: เครื่องตอบ 32h ทันที และส่ง 4Ah มาเมื่อพิมพ์เสร็จ (ดู PrintDone ของ Unsolicited)</summary>
        public Task UpdateVariablesAsync(IList<TemplateVariable> variables, ushort allocation, bool waitPrintDone)
        {
            return Connection.ExecuteAsync(DgcpCommands.UpdateVariables(Station, allocation, variables, waitPrintDone));
        }

        public async Task<DeviceStatus> GetStatusAsync()
        {
            var f = await Connection.QueryAsync(DgcpCommands.Simple(Station, Cmd.GetStatus), new[] { Cmd.GetStatus, Cmd.StatusReport }).ConfigureAwait(false);
            return DgcpResponses.ParseStatus(f.Data);
        }

        public async Task<List<ParameterItem>> GetParametersAsync()
        {
            var f = await Connection.QueryAsync(DgcpCommands.Simple(Station, Cmd.GetParameters), new[] { Cmd.GetParameters }).ConfigureAwait(false);
            return DgcpResponses.ParseParameters(f.Data);
        }

        public Task<List<string>> GetFileListAsync(bool usb = false)
        {
            return Connection.QueryFileListAsync(DgcpCommands.Simple(Station, usb ? Cmd.GetUsbFileList : Cmd.GetFileList));
        }

        public async Task<DateTime?> GetTimeAsync()
        {
            var f = await Connection.QueryAsync(DgcpCommands.Simple(Station, Cmd.GetTime), new[] { Cmd.GetTime }).ConfigureAwait(false);
            return DgcpResponses.ParseTime(f.Data);
        }

        public Task SetTimeAsync(DateTime time)
        {
            return Connection.ExecuteAsync(DgcpCommands.SetTime(Station, time));
        }

        /// <summary>คำสั่งที่มีแค่ ST + CMD และตอบ 32h เช่น 42h เริ่ม, 43h หยุด, 38h ล้างคิว</summary>
        public Task ExecuteSimpleAsync(byte cmd)
        {
            return Connection.ExecuteAsync(DgcpCommands.Simple(Station, cmd));
        }
    }
}
