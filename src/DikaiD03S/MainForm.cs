using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dikai.Dgcp;

namespace Dikai.App
{
    internal sealed class MainForm : Form
    {
        private const int MaxLogRows = 2000;

        private readonly AppSettings _settings;
        private readonly List<PrinterSession> _sessions = new List<PrinterSession>();
        private readonly HashSet<PrinterSession> _awaitingPrintDone = new HashSet<PrinterSession>();
        private readonly List<Control> _actionControls = new List<Control>();
        private PrinterSession _selected;
        private bool _busy;
        private bool _polling;
        private bool _loadingUi;

        // ซ้าย: รายชื่อเครื่อง
        private ListView _lvPrinters;
        private Button _btnEdit, _btnDelete, _btnConnect, _btnDisconnect;

        // หัวด้านขวา
        private Label _lblTitle, _lblAddress, _lblConn;
        private TabControl _tabs;

        // แท็บส่งข้อมูล
        private ComboBox _cboTemplate;
        private DataGridView _grid;
        private RadioButton _rb34, _rb35, _rb49;
        private CheckBox _chkClearFirst;
        private NumericUpDown _numAllocation;
        private Label _lblSize, _lblSendResult;
        private TextBox _txtPreview;
        private Button _btnSend;

        // แท็บสถานะ
        private Label _lblBigStatus, _lblStatusNote;
        private ListView _lvStatus;
        private ListBox _lbAlarms;
        private CheckBox _chkAuto;
        private NumericUpDown _numAutoSec;
        private Timer _autoTimer;

        // แท็บควบคุม / ข้อมูลเครื่อง / HEX
        private Label _lblControlResult, _lblInfoResult, _lblRawResult;
        private ListView _lvParams;
        private ListBox _lbUsb;
        private TextBox _txtRaw, _txtRawReply;
        private CheckBox _chkAutoHeader, _chkWaitReply;

        // log
        private ListView _lvLog;
        private CheckBox _chkShowHex, _chkShowStatus;
        private ToolStripStatusLabel _statusText;

        public MainForm(AppSettings settings, string startupWarning)
        {
            _settings = settings;
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Text = "Dikai D03S — ส่งข้อมูลเครื่องพิมพ์ (DGCP v2.0)  v" + version.ToString(3);
            Font = Ui.Font;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1220, 820);
            MinimumSize = new Size(980, 640);

            BuildLayout();

            foreach (var p in _settings.Printers) AddSession(p);
            RefreshPrinterList();
            if (_lvPrinters.Items.Count > 0) _lvPrinters.Items[0].Selected = true;
            UpdateSelectionUi();

            // คำนวณตัวอย่าง HEX ใหม่เมื่อหน้าต่างแสดงจริง (ตอนสร้างฟอร์ม ตารางยังไม่พร้อมบางเครื่อง)
            Shown += (s, e) => UpdatePreview();

            _autoTimer = new Timer { Interval = _settings.AutoStatusSeconds * 1000 };
            _autoTimer.Tick += async (s, e) => await AutoPollAsync();

            _statusText.Text = "ไฟล์ตั้งค่า: " + AppSettings.FilePath;
            if (startupWarning != null) AddLog(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = startupWarning });
            AddLog(new LogEntry { Printer = "-", Kind = LogKind.Info, Text = "เริ่มโปรแกรม — log อยู่ที่ " + AppSettings.LogDirectory });
        }

        // ================================================================== layout

        private void BuildLayout()
        {
            var status = new StatusStrip();
            _statusText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            status.Items.Add(_statusText);

            var splitMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
            var splitTop = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            splitTop.Panel1.Controls.Add(BuildPrinterPanel());
            splitTop.Panel2.Controls.Add(BuildRightPanel());
            splitMain.Panel1.Controls.Add(splitTop);
            splitMain.Panel2.Controls.Add(BuildLogPanel());

            Controls.Add(splitMain);
            Controls.Add(status);

            Load += (s, e) =>
            {
                splitTop.SplitterDistance = 340;
                splitMain.SplitterDistance = Math.Max(300, splitMain.Height - 220);
            };
        }

        private Control BuildPrinterPanel()
        {
            var box = new GroupBox { Text = "เครื่องพิมพ์", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var t = Ui.Stack();
            _lvPrinters = Ui.Details(("ชื่อ", 105), ("ที่อยู่", 125), ("สถานะ", 75));
            _lvPrinters.SelectedIndexChanged += (s, e) => UpdateSelectionUi();
            _lvPrinters.DoubleClick += (s, e) => EditPrinter();
            Ui.AddRow(t, _lvPrinters, fill: true);

            _btnEdit = Ui.Button("แก้ไข", (s, e) => EditPrinter());
            _btnDelete = Ui.Button("ลบ", (s, e) => DeletePrinter());
            Ui.AddRow(t, Ui.Row(Ui.Button("+ เพิ่มเครื่อง", (s, e) => AddPrinter()), _btnEdit, _btnDelete));

            _btnConnect = Ui.Button("เชื่อมต่อ", async (s, e) => await ConnectSelectedAsync());
            _btnDisconnect = Ui.Button("ตัดการเชื่อมต่อ", (s, e) => _selected?.Disconnect());
            Ui.AddRow(t, Ui.Row(_btnConnect, _btnDisconnect));

            var hint = Ui.Label("1 เครื่อง = 1 IP — เลือกเครื่องทางซ้ายแล้วส่งข้อมูลทางขวา\nกดส่งได้เลย โปรแกรมจะเชื่อมต่อให้อัตโนมัติ", null, Ui.Muted);
            Ui.AddRow(t, hint);
            box.Controls.Add(t);
            return box;
        }

        private Control BuildRightPanel()
        {
            var t = Ui.Stack();
            _lblTitle = Ui.Label("-", Ui.Big);
            _lblAddress = Ui.Label("", null, Ui.Muted);
            _lblAddress.Margin = new Padding(12, 12, 3, 3);
            _lblConn = Ui.Label("", Ui.Bold);
            _lblConn.Margin = new Padding(12, 12, 3, 3);
            Ui.AddRow(t, Ui.Row(_lblTitle, _lblAddress, _lblConn));

            _tabs = new TabControl { Padding = new Point(14, 5) };
            _tabs.TabPages.Add(BuildSendTab());
            _tabs.TabPages.Add(BuildStatusTab());
            _tabs.TabPages.Add(BuildControlTab());
            _tabs.TabPages.Add(BuildInfoTab());
            _tabs.TabPages.Add(BuildRawTab());
            Ui.AddRow(t, _tabs, fill: true);
            return t;
        }

        private TabPage BuildSendTab()
        {
            var page = new TabPage("ส่งข้อมูล");
            var t = Ui.Stack();

            _cboTemplate = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3, 4, 3, 3) };
            _cboTemplate.TextChanged += (s, e) => UpdatePreview();
            var btnFiles = Ui.Button("โหลดรายชื่อแม่แบบ (41h)", async (s, e) => await LoadTemplateListAsync());
            var btnFromDevice = Ui.Button("ดึงแม่แบบ + ตัวแปรจากเครื่อง (3Ah)", async (s, e) => await LoadFromDeviceAsync());
            Track(btnFiles, btnFromDevice);
            Ui.AddRow(t, Ui.Row(Ui.Label("แม่แบบ:", Ui.Bold), _cboTemplate, btnFiles, btnFromDevice));

            _grid = new DataGridView
            {
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AllowUserToResizeRows = false,
                RowHeadersWidth = 28,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MinimumSize = new Size(0, 130),
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "ชื่อตัวแปร (ตัวพิมพ์เล็ก-ใหญ่ต้องตรง)", Width = 260 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "ค่าที่จะพิมพ์", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "bytes", HeaderText = "ไบต์", Width = 60, ReadOnly = true });
            _grid.Columns["bytes"].DefaultCellStyle.ForeColor = Ui.Muted;
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.ColumnIndex != 2) UpdatePreview(); // คอลัมน์ไบต์ถูกเขียนโดย UpdatePreview เอง
            };
            _grid.RowsRemoved += (s, e) => UpdatePreview();
            _grid.UserDeletedRow += (s, e) => UpdatePreview();
            Ui.AddRow(t, _grid, fill: true);

            var btnRemove = Ui.Button("ลบแถวที่เลือก", (s, e) => RemoveSelectedVariableRows());
            var btnClear = Ui.Button("ล้างตาราง", (s, e) =>
            {
                _grid.Rows.Clear();
                UpdatePreview();
            });
            Ui.AddRow(t, Ui.Row(btnRemove, btnClear, Ui.Label("ภาษาไทย 1 ตัวอักษร = 3 ไบต์ • ค่าแต่ละช่องไม่เกิน 255 ไบต์", null, Ui.Muted)));

            // ไม่ใช้ GroupBox แบบ AutoSize (คำนวณขนาดไม่แน่นอน ทำให้ปุ่มส่งหลุดจอ) — ใช้แถวธรรมดาแทน
            _rb34 = new RadioButton { Text = "เลือกแม่แบบ + ใส่ค่า (34h) — ใช้ตอนเปลี่ยนสินค้า / ล็อต", AutoSize = true, Checked = true, Margin = new Padding(12, 2, 3, 2) };
            _rb35 = new RadioButton { Text = "เปลี่ยนค่าอย่างเดียว (35h) — เร็ว ใช้แม่แบบที่เลือกไว้แล้ว ค่าเข้าคิวของเครื่อง", AutoSize = true, Margin = new Padding(12, 2, 3, 2) };
            _rb49 = new RadioButton { Text = "เปลี่ยนค่า + แจ้งเมื่อพิมพ์เสร็จ (49h) — ไม่มีงานค้าง ส่งชุดถัดไปหลังพิมพ์เสร็จ", AutoSize = true, Margin = new Padding(12, 2, 3, 2) };
            foreach (var rb in new[] { _rb34, _rb35, _rb49 }) rb.CheckedChanged += (s, e) => UpdatePreview();
            _chkClearFirst = new CheckBox { Text = "ล้างคิวก่อนส่ง (38h)", AutoSize = true, Margin = new Padding(12, 8, 20, 3) };
            _numAllocation = new NumericUpDown { Minimum = 0, Maximum = 65535, Width = 80, Margin = new Padding(3, 5, 3, 3) };
            _numAllocation.ValueChanged += (s, e) => UpdatePreview();
            var modeTitle = Ui.Label("วิธีส่ง:", Ui.Bold);
            modeTitle.Margin = new Padding(3, 8, 3, 2);
            Ui.AddRow(t, modeTitle);
            Ui.AddRow(t, _rb34);
            Ui.AddRow(t, _rb35);
            Ui.AddRow(t, _rb49);
            Ui.AddRow(t, Ui.Row(_chkClearFirst, Ui.Label("จำนวนพิมพ์:"), _numAllocation, Ui.Label("(0 = พิมพ์ไม่จำกัดจนกว่าจะส่งค่าใหม่)", null, Ui.Muted)));

            _lblSize = Ui.Label("", null, Ui.Muted);
            Ui.AddRow(t, _lblSize);
            _txtPreview = new TextBox { ReadOnly = true, Multiline = true, Height = 40, Font = Ui.Mono, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control };
            Ui.AddRow(t, _txtPreview);

            _btnSend = Ui.Button("ส่งไปเครื่องพิมพ์", async (s, e) => await SendAsync(), 200);
            _btnSend.Font = new Font(Ui.Font.FontFamily, 12f, FontStyle.Bold);
            _btnSend.BackColor = Ui.Accent;
            _btnSend.ForeColor = Color.White;
            _btnSend.UseVisualStyleBackColor = false;
            _btnSend.FlatStyle = FlatStyle.Flat;
            _btnSend.Padding = new Padding(16, 6, 16, 6);
            _lblSendResult = Ui.Label("", Ui.Bold);
            _lblSendResult.Margin = new Padding(12, 14, 3, 3);
            Track(_btnSend);
            Ui.AddRow(t, Ui.Row(_btnSend, _lblSendResult));

            page.Controls.Add(t);
            return page;
        }

        private TabPage BuildStatusTab()
        {
            var page = new TabPage("สถานะ");
            var t = Ui.Stack();
            var btn = Ui.Button("อ่านสถานะ (3Ah)", async (s, e) => await ReadStatusAsync());
            Track(btn);
            _chkAuto = new CheckBox { Text = "อ่านอัตโนมัติทุก", AutoSize = true, Margin = new Padding(20, 8, 3, 3) };
            _numAutoSec = new NumericUpDown { Minimum = 1, Maximum = 3600, Value = _settings.AutoStatusSeconds, Width = 60, Margin = new Padding(3, 5, 3, 3) };
            _chkAuto.CheckedChanged += (s, e) => UpdateAutoTimer();
            _numAutoSec.ValueChanged += (s, e) =>
            {
                _settings.AutoStatusSeconds = (int)_numAutoSec.Value;
                UpdateAutoTimer();
            };
            Ui.AddRow(t, Ui.Row(btn, _chkAuto, _numAutoSec, Ui.Label("วินาที")));

            _lblBigStatus = Ui.Label("ยังไม่ได้อ่านสถานะ", Ui.Big, Ui.Muted);
            _lblStatusNote = Ui.Label("", null, Ui.Muted);
            Ui.AddRow(t, Ui.Row(_lblBigStatus, _lblStatusNote));

            var split = new SplitContainer { Width = 1000 };
            split.SplitterDistance = 480; // ต้องกำหนดหลังมีความกว้าง ไม่งั้น WinForms โยน exception
            _lvStatus = Ui.Details(("รายการ", 175), ("ค่า", 200));
            split.Panel1.Controls.Add(_lvStatus);
            _lvStatus.Dock = DockStyle.Fill;
            var alarmBox = new GroupBox { Text = "การแจ้งเตือน / ความผิดปกติ", Dock = DockStyle.Fill };
            _lbAlarms = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            alarmBox.Controls.Add(_lbAlarms);
            var btnClearAlarm = Ui.Button("ล้างการแจ้งเตือน (46h)", async (s, e) => await ControlAsync(Cmd.ClearAlarm, null, _lblStatusNote));
            Track(btnClearAlarm);
            btnClearAlarm.Dock = DockStyle.Bottom;
            alarmBox.Controls.Add(btnClearAlarm);
            split.Panel2.Controls.Add(alarmBox);
            Ui.AddRow(t, split, fill: true);
            page.Controls.Add(t);
            return page;
        }

        private TabPage BuildControlTab()
        {
            var page = new TabPage("ควบคุม");
            var t = Ui.Stack();
            _lblControlResult = Ui.Label("", Ui.Bold);

            void Group(string title, params Control[] buttons)
            {
                var head = Ui.Label(title, Ui.Bold);
                head.Margin = new Padding(3, 10, 3, 2);
                Ui.AddRow(t, head);
                Ui.AddRow(t, Ui.Row(buttons));
            }

            Button Cmd1(string text, byte cmd, string confirm = null)
            {
                var b = Ui.Button(text, async (s, e) => await ControlAsync(cmd, confirm, _lblControlResult), 150);
                Track(b);
                return b;
            }

            Group("การพิมพ์",
                Cmd1("▶ เริ่ม (42h)", Cmd.Start),
                Cmd1("■ หยุด (43h)", Cmd.Stop),
                Cmd1("ทดสอบพิมพ์ (44h)", Cmd.TestPrint),
                Cmd1("สั่งพิมพ์ 1 ครั้ง (4Bh)", Cmd.PrintTrigger));
            Ui.AddRow(t, Ui.Label("4Bh ใช้เมื่อให้โปรแกรมสั่งพิมพ์แทนสัญญาณเซนเซอร์จากเครื่องแพ็ค", null, Ui.Muted));

            Group("ล้างค่า",
                Cmd1("ล้างคิว / บัฟเฟอร์ (38h)", Cmd.ClearBuffer, "ล้างคิวงานที่ค้างในเครื่อง? (เอกสารระบุว่าล้างแม่แบบที่ใช้อยู่และบัฟเฟอร์)"),
                Cmd1("ล้างการแจ้งเตือน (46h)", Cmd.ClearAlarm),
                Cmd1("ล้างคำเตือน (36h)", Cmd.ClearWarning),
                Cmd1("ล้างตัวนับพิมพ์ (45h)", Cmd.ClearPrintCounter, "ล้างตัวนับจำนวนพิมพ์เป็น 0?"),
                Cmd1("ล้างตัวนับไฟล์ (37h)", Cmd.ClearFileCounter, "ล้างตัวนับของแม่แบบที่ใช้อยู่เป็น 0?"),
                Cmd1("ล้างตัวนับริบบอน (39h)", Cmd.ClearRibbonCounter, "ล้างตัวนับริบบอน (ใช้ตอนเปลี่ยนม้วนใหม่)?"));

            var btnGetTime = Ui.Button("อ่านเวลาเครื่อง (47h)", async (s, e) => await GetTimeAsync(), 150);
            var btnSetTime = Ui.Button("ตั้งเวลาเครื่องตามคอมพิวเตอร์ (48h)", async (s, e) => await SetTimeAsync(), 150);
            Track(btnGetTime, btnSetTime);
            Group("เวลาเครื่อง (ใช้พิมพ์วันผลิต / วันหมดอายุ)", btnGetTime, btnSetTime);

            _lblControlResult.Margin = new Padding(3, 14, 3, 3);
            Ui.AddRow(t, _lblControlResult);
            Ui.AddRow(t, new Panel(), fill: true);
            t.AutoScroll = true;
            page.Controls.Add(t);
            return page;
        }

        private TabPage BuildInfoTab()
        {
            var page = new TabPage("ข้อมูลเครื่อง");
            var t = Ui.Stack();
            var btnParams = Ui.Button("อ่านค่าเครื่อง (40h)", async (s, e) => await ReadParametersAsync());
            var btnUsb = Ui.Button("รายชื่อไฟล์ใน USB (A0h)", async (s, e) => await ReadUsbAsync());
            Track(btnParams, btnUsb);
            _lblInfoResult = Ui.Label("", Ui.Bold);
            Ui.AddRow(t, Ui.Row(btnParams, btnUsb, _lblInfoResult));
            Ui.AddRow(t, Ui.Label("อ่านอย่างเดียว — โปรแกรมนี้ไม่เขียนค่าเครื่อง (7xh/8xh) เพื่อกันตั้งค่าผิดกลางไลน์ผลิต; IP/Baud ตั้งที่หน้าจอเครื่อง", null, Ui.Muted));

            var split = new SplitContainer { Width = 1000 };
            split.SplitterDistance = 620;
            _lvParams = Ui.Details(("รายการ", 240), ("ค่า", 200), ("HEX", 120));
            _lvParams.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(_lvParams);
            var usbBox = new GroupBox { Text = "ไฟล์ใน USB", Dock = DockStyle.Fill };
            _lbUsb = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            usbBox.Controls.Add(_lbUsb);
            split.Panel2.Controls.Add(usbBox);
            Ui.AddRow(t, split, fill: true);
            page.Controls.Add(t);
            return page;
        }

        private TabPage BuildRawTab()
        {
            var page = new TabPage("HEX (ขั้นสูง)");
            var t = Ui.Stack();
            Ui.AddRow(t, Ui.Label("ส่งคำสั่งเองเป็นเลขฐานสิบหก สำหรับทดสอบ/ตรวจปัญหา เช่น  01 3A = อ่านสถานะ,  01 33 = อ่านแม่แบบที่ใช้อยู่", null, Ui.Muted));
            _chkAutoHeader = new CheckBox { Text = "พิมพ์แค่ ST CMD DATA แล้วให้โปรแกรมเติม 5A A5 + ความยาว", AutoSize = true, Checked = true };
            _chkWaitReply = new CheckBox { Text = "รอคำตอบ", AutoSize = true, Checked = true, Margin = new Padding(20, 3, 3, 3) };
            Ui.AddRow(t, Ui.Row(_chkAutoHeader, _chkWaitReply));
            _txtRaw = new TextBox { Multiline = true, Height = 70, Font = Ui.Mono, Text = "01 3A", ScrollBars = ScrollBars.Vertical };
            Ui.AddRow(t, _txtRaw);
            var btn = Ui.Button("ส่ง HEX", async (s, e) => await SendRawAsync(), 120);
            Track(btn);
            _lblRawResult = Ui.Label("", Ui.Bold);
            Ui.AddRow(t, Ui.Row(btn, _lblRawResult));
            _txtRawReply = new TextBox { Multiline = true, ReadOnly = true, Font = Ui.Mono, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
            Ui.AddRow(t, _txtRawReply, fill: true);
            page.Controls.Add(t);
            return page;
        }

        private Control BuildLogPanel()
        {
            var box = new GroupBox { Text = "บันทึกการสื่อสาร", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var t = Ui.Stack();
            t.Padding = new Padding(0);
            _chkShowHex = new CheckBox { Text = "แสดง HEX", AutoSize = true, Checked = true, Margin = new Padding(3, 7, 12, 3) };
            _chkShowHex.CheckedChanged += (s, e) => _lvLog.Columns[4].Width = _chkShowHex.Checked ? 520 : 0;
            _chkShowStatus = new CheckBox { Text = "แสดงการอ่านสถานะ (3A/3F)", AutoSize = true, Checked = _settings.ShowStatusFrames, Margin = new Padding(3, 7, 12, 3) };
            _chkShowStatus.CheckedChanged += (s, e) => _settings.ShowStatusFrames = _chkShowStatus.Checked;
            Ui.AddRow(t, Ui.Row(
                _chkShowHex, _chkShowStatus,
                Ui.Button("คัดลอกที่เลือก", (s, e) => CopyLog()),
                Ui.Button("ล้าง", (s, e) => _lvLog.Items.Clear()),
                Ui.Button("เปิดโฟลเดอร์ log", (s, e) => OpenLogFolder())));
            _lvLog = Ui.Details(("เวลา", 90), ("เครื่อง", 110), ("", 70), ("รายละเอียด", 460), ("HEX", 520));
            _lvLog.MultiSelect = true;
            _lvLog.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) CopyLog();
            };
            Ui.AddRow(t, _lvLog, fill: true);
            box.Controls.Add(t);
            return box;
        }

        private void Track(params Control[] controls)
        {
            _actionControls.AddRange(controls);
        }

        // ================================================================== เครื่องพิมพ์

        private PrinterSession AddSession(PrinterProfile p)
        {
            var s = new PrinterSession(p) { TimeoutMs = _settings.TimeoutMs };
            s.StateChanged += ss => OnUi(() =>
            {
                UpdatePrinterItem(ss);
                if (ss == _selected) UpdateHeader();
            });
            s.Log += e => OnUi(() => AddLog(e));
            s.Unsolicited += (ss, f) => OnUi(() => OnUnsolicited(ss, f));
            _sessions.Add(s);
            return s;
        }

        private void RefreshPrinterList()
        {
            _lvPrinters.BeginUpdate();
            _lvPrinters.Items.Clear();
            foreach (var s in _sessions)
            {
                var item = new ListViewItem(new[] { "", "", "" }) { Tag = s };
                _lvPrinters.Items.Add(item);
                FillPrinterItem(item, s);
            }
            _lvPrinters.EndUpdate();
        }

        private void UpdatePrinterItem(PrinterSession s)
        {
            foreach (ListViewItem item in _lvPrinters.Items)
            {
                if (item.Tag == s) FillPrinterItem(item, s);
            }
        }

        private static void FillPrinterItem(ListViewItem item, PrinterSession s)
        {
            item.SubItems[0].Text = s.Profile.Name;
            item.SubItems[1].Text = s.Profile.Address;
            item.SubItems[2].Text = StateText(s.State);
            item.ForeColor = s.State == ConnState.Connected ? Ui.Good : s.State == ConnState.Failed ? Ui.Bad : SystemColors.WindowText;
        }

        private static string StateText(ConnState st)
        {
            switch (st)
            {
                case ConnState.Connected: return "เชื่อมต่อ";
                case ConnState.Connecting: return "กำลังต่อ…";
                case ConnState.Failed: return "ผิดพลาด";
                default: return "—";
            }
        }

        private void UpdateSelectionUi()
        {
            var next = _lvPrinters.SelectedItems.Count > 0 ? (PrinterSession)_lvPrinters.SelectedItems[0].Tag : null;
            if (next == _selected && next != null)
            {
                UpdateHeader();
                return;
            }
            if (_selected != null) SaveUiToProfile(_selected.Profile);
            _selected = next;
            SaveSettings();
            if (_selected != null) LoadProfileToUi(_selected.Profile);
            ShowStatus(_selected?.LastStatus);
            _lvParams.Items.Clear();
            _lbUsb.Items.Clear();
            _lblSendResult.Text = "";
            _lblControlResult.Text = "";
            _lblInfoResult.Text = "";
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            bool has = _selected != null;
            _tabs.Enabled = has;
            _btnEdit.Enabled = _btnDelete.Enabled = has && !_busy;
            if (!has)
            {
                _lblTitle.Text = "ยังไม่ได้เลือกเครื่อง";
                _lblAddress.Text = "";
                _lblConn.Text = "";
                _btnConnect.Enabled = _btnDisconnect.Enabled = false;
                return;
            }
            var p = _selected.Profile;
            _lblTitle.Text = p.Name;
            _lblAddress.Text = p.Address + "  •  ST " + p.Station + "  •  สูงสุด " + p.MaxFrame + " ไบต์/ครั้ง";
            switch (_selected.State)
            {
                case ConnState.Connected:
                    Ui.SetResult(_lblConn, "● เชื่อมต่อแล้ว", Ui.Good);
                    break;
                case ConnState.Connecting:
                    Ui.SetResult(_lblConn, "● กำลังเชื่อมต่อ…", Ui.Warn);
                    break;
                case ConnState.Failed:
                    Ui.SetResult(_lblConn, "● " + (_selected.LastError ?? "ผิดพลาด"), Ui.Bad);
                    break;
                default:
                    Ui.SetResult(_lblConn, "○ ยังไม่เชื่อมต่อ", Ui.Muted);
                    break;
            }
            _btnConnect.Enabled = !_busy && _selected.State != ConnState.Connected && _selected.State != ConnState.Connecting;
            _btnDisconnect.Enabled = !_busy && (_selected.State == ConnState.Connected || _selected.State == ConnState.Failed);
        }

        private void AddPrinter()
        {
            var n = _sessions.Count + 1;
            var p = PrinterProfile.CreateDefault("เครื่อง " + n, "192.168.1." + Math.Min(254, 1 + n));
            using (var f = new PrinterEditForm(p, true))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            _settings.Printers.Add(p);
            var s = AddSession(p);
            RefreshPrinterList();
            SelectSession(s);
            SaveSettings();
        }

        private void EditPrinter()
        {
            if (_selected == null || _busy) return;
            var p = _selected.Profile;
            var before = p.Kind + "|" + p.Host + "|" + p.Port + "|" + p.ComPort + "|" + p.BaudRate;
            using (var f = new PrinterEditForm(p, false))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            var after = p.Kind + "|" + p.Host + "|" + p.Port + "|" + p.ComPort + "|" + p.BaudRate;
            if (before != after) _selected.Disconnect();
            if (_selected.Client != null) _selected.Client.Station = (byte)p.Station;
            UpdatePrinterItem(_selected);
            UpdateHeader();
            UpdatePreview();
            SaveSettings();
        }

        private void DeletePrinter()
        {
            if (_selected == null || _busy) return;
            if (MessageBox.Show(this, "ลบ \"" + _selected.Profile.Name + "\" ออกจากรายการ?", "ลบเครื่องพิมพ์",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var s = _selected;
            _selected = null;
            s.Dispose();
            _sessions.Remove(s);
            _settings.Printers.Remove(s.Profile);
            _awaitingPrintDone.Remove(s);
            RefreshPrinterList();
            if (_lvPrinters.Items.Count > 0) _lvPrinters.Items[0].Selected = true;
            UpdateSelectionUi();
            SaveSettings();
        }

        private void SelectSession(PrinterSession s)
        {
            foreach (ListViewItem item in _lvPrinters.Items) item.Selected = item.Tag == s;
        }

        private async Task ConnectSelectedAsync()
        {
            await RunAsync("เชื่อมต่อ", _lblSendResult, c => Task.FromResult(0), "เชื่อมต่อแล้ว");
        }

        // ================================================================== profile <-> UI

        private void LoadProfileToUi(PrinterProfile p)
        {
            _loadingUi = true;
            try
            {
                _cboTemplate.Items.Clear();
                foreach (var name in p.KnownTemplates) _cboTemplate.Items.Add(name);
                _cboTemplate.Text = p.LastTemplate ?? "";
                _grid.Rows.Clear();
                foreach (var v in p.Variables) _grid.Rows.Add(v.Name, v.Value, "");
                _rb34.Checked = p.Mode == (int)SendMode.SelectFile;
                _rb35.Checked = p.Mode == (int)SendMode.Update;
                _rb49.Checked = p.Mode == (int)SendMode.UpdateWithFeedback;
                _numAllocation.Value = p.Allocation;
                _chkClearFirst.Checked = p.ClearBeforeSend;
            }
            finally
            {
                _loadingUi = false;
            }
            UpdatePreview();
        }

        private void SaveUiToProfile(PrinterProfile p)
        {
            _grid.EndEdit();
            p.LastTemplate = _cboTemplate.Text.Trim();
            p.Variables = ReadGridRows().Select(r => new VariableEntry { Name = r.Name, Value = r.Value }).ToList();
            p.Mode = (int)CurrentMode;
            p.Allocation = (int)_numAllocation.Value;
            p.ClearBeforeSend = _chkClearFirst.Checked;
        }

        private SendMode CurrentMode => _rb35.Checked ? SendMode.Update : _rb49.Checked ? SendMode.UpdateWithFeedback : SendMode.SelectFile;

        private sealed class GridRow
        {
            public int Index;
            public string Name;
            public string Value;
        }

        /// <summary>แถวที่มีข้อมูล (ข้ามแถวว่าง) — ชื่อตัด space หน้า/หลัง ค่าเก็บตามที่พิมพ์</summary>
        private List<GridRow> ReadGridRows()
        {
            var rows = new List<GridRow>();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.IsNewRow) continue;
                var name = (Convert.ToString(r.Cells[0].Value) ?? "").Trim();
                var value = Convert.ToString(r.Cells[1].Value) ?? "";
                if (name.Length == 0 && value.Length == 0) continue;
                rows.Add(new GridRow { Index = r.Index + 1, Name = name, Value = value });
            }
            return rows;
        }

        private List<TemplateVariable> ReadVariablesOrThrow()
        {
            var rows = ReadGridRows();
            var missing = rows.FirstOrDefault(r => r.Name.Length == 0);
            if (missing != null) throw new DgcpException("แถวที่ " + missing.Index + " ยังไม่มีชื่อตัวแปร");
            var dup = rows.GroupBy(r => r.Name).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new DgcpException("ชื่อตัวแปร \"" + dup.Key + "\" ซ้ำกัน");
            return rows.Select(r => new TemplateVariable(r.Name, r.Value)).ToList();
        }

        private byte[] BuildSendFrame()
        {
            var vars = ReadVariablesOrThrow();
            var st = (byte)_selected.Profile.Station;
            var alloc = (ushort)_numAllocation.Value;
            switch (CurrentMode)
            {
                case SendMode.Update: return DgcpCommands.UpdateVariables(st, alloc, vars, false);
                case SendMode.UpdateWithFeedback: return DgcpCommands.UpdateVariables(st, alloc, vars, true);
                default: return DgcpCommands.SelectFile(st, alloc, _cboTemplate.Text, vars);
            }
        }

        private void UpdatePreview()
        {
            if (_loadingUi || _selected == null) return;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.IsNewRow) continue;
                int n = Encoding.UTF8.GetByteCount(Convert.ToString(r.Cells[1].Value) ?? "");
                var cell = r.Cells[2];
                var text = n.ToString(CultureInfo.InvariantCulture);
                if (!Equals(cell.Value, text)) cell.Value = text;
                cell.Style.ForeColor = n > 255 ? Ui.Bad : Ui.Muted;
            }
            _cboTemplate.Enabled = CurrentMode == SendMode.SelectFile;
            try
            {
                var frame = BuildSendFrame();
                int max = _selected.Profile.MaxFrame;
                _txtPreview.Text = Hex.Format(frame);
                bool tooBig = frame.Length > max;
                Ui.SetResult(_lblSize,
                    "ข้อมูลที่จะส่ง: " + frame.Length + " / " + max + " ไบต์" + (tooBig ? "  — ยาวเกินที่เครื่องรับได้ ลดข้อความลง" : ""),
                    tooBig ? Ui.Bad : Ui.Muted);
            }
            catch (DgcpException ex)
            {
                _txtPreview.Text = "";
                Ui.SetResult(_lblSize, ex.Message, CurrentMode == SendMode.SelectFile && _cboTemplate.Text.Trim().Length == 0 ? Ui.Muted : Ui.Warn);
            }
        }

        private void RemoveSelectedVariableRows()
        {
            var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow)
                .Concat(_grid.SelectedRows.Cast<DataGridViewRow>())
                .Where(r => !r.IsNewRow).Distinct().ToList();
            foreach (var r in rows) _grid.Rows.Remove(r);
            UpdatePreview();
        }

        // ================================================================== งานที่คุยกับเครื่อง

        /// <summary>
        /// ตัวรันคำสั่งกลาง: กันกดซ้ำ (ปิดปุ่มระหว่างทำงาน), เชื่อมต่อให้อัตโนมัติ, แสดงผลสำเร็จ/ผิดพลาด
        /// </summary>
        private async Task<bool> RunAsync(string what, Label result, Func<DgcpClient, Task> op, string successText = null)
        {
            var session = _selected;
            if (session == null || _busy) return false;
            _busy = true;
            SetActionsEnabled(false);
            Ui.SetResult(result, "กำลัง" + what + "…", Ui.Muted);
            UseWaitCursor = true;
            try
            {
                session.TimeoutMs = _settings.TimeoutMs;
                var client = await session.EnsureConnectedAsync();
                await op(client);
                if (successText != null) Ui.SetResult(result, "✓ " + successText + "  " + DateTime.Now.ToString("HH:mm:ss"), Ui.Good);
                return true;
            }
            catch (Exception ex)
            {
                var msg = ex is DgcpException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                Ui.SetResult(result, "✗ " + what + "ไม่สำเร็จ — " + msg, Ui.Bad);
                bool alreadyLogged = session.State == ConnState.Failed && session.LastError == msg; // เชื่อมต่อไม่ได้ ถูก log แล้ว
                if (!alreadyLogged) session.Write(LogKind.Error, what + "ไม่สำเร็จ: " + msg);
                return false;
            }
            finally
            {
                UseWaitCursor = false;
                _busy = false;
                SetActionsEnabled(true);
                UpdateHeader();
            }
        }

        private void SetActionsEnabled(bool enabled)
        {
            foreach (var c in _actionControls) c.Enabled = enabled;
            _lvPrinters.Enabled = enabled;
            UpdateHeader();
        }

        private async Task SendAsync()
        {
            if (_selected == null) return;
            _grid.EndEdit();
            List<TemplateVariable> vars;
            byte[] frame;
            try
            {
                vars = ReadVariablesOrThrow();
                frame = BuildSendFrame();
            }
            catch (DgcpException ex)
            {
                Ui.SetResult(_lblSendResult, "✗ " + ex.Message, Ui.Bad);
                return;
            }
            if (frame.Length > _selected.Profile.MaxFrame)
            {
                Ui.SetResult(_lblSendResult, "✗ ข้อมูลยาว " + frame.Length + " ไบต์ เกิน " + _selected.Profile.MaxFrame + " ไบต์", Ui.Bad);
                return;
            }

            var session = _selected;
            var mode = CurrentMode;
            var template = _cboTemplate.Text.Trim();
            var alloc = (ushort)_numAllocation.Value;
            bool clearFirst = _chkClearFirst.Checked;

            bool ok = await RunAsync("ส่ง", _lblSendResult, async c =>
            {
                if (clearFirst) await c.ExecuteSimpleAsync(Cmd.ClearBuffer);
                switch (mode)
                {
                    case SendMode.Update:
                        await c.UpdateVariablesAsync(vars, alloc, false);
                        break;
                    case SendMode.UpdateWithFeedback:
                        lock (_awaitingPrintDone) _awaitingPrintDone.Add(session);
                        await c.UpdateVariablesAsync(vars, alloc, true);
                        break;
                    default:
                        await c.SelectFileAsync(template, vars, alloc);
                        break;
                }
            });
            if (!ok)
            {
                lock (_awaitingPrintDone) _awaitingPrintDone.Remove(session);
                return;
            }

            var now = DateTime.Now.ToString("HH:mm:ss");
            bool stillWaiting;
            lock (_awaitingPrintDone) stillWaiting = _awaitingPrintDone.Contains(session);
            if (mode == SendMode.UpdateWithFeedback && stillWaiting)
                Ui.SetResult(_lblSendResult, "✓ เครื่องรับแล้ว " + now + " — รอพิมพ์เสร็จ…", Ui.Warn);
            else if (mode == SendMode.UpdateWithFeedback)
                Ui.SetResult(_lblSendResult, "✓ พิมพ์เสร็จแล้ว " + now + " — ส่งชุดถัดไปได้", Ui.Good); // 4Ah มาถึงก่อน
            else
                Ui.SetResult(_lblSendResult, "✓ ส่งสำเร็จ " + now + (mode == SendMode.SelectFile ? " — ใช้แม่แบบ " + template : ""), Ui.Good);

            if (mode == SendMode.SelectFile && template.Length > 0)
            {
                var known = session.Profile.KnownTemplates;
                if (!known.Contains(template, StringComparer.OrdinalIgnoreCase))
                {
                    known.Add(template);
                    _cboTemplate.Items.Add(template);
                }
            }
            if (session == _selected)
            {
                SaveUiToProfile(session.Profile);
                UpdatePreview();
            }
            SaveSettings();
        }

        private void OnUnsolicited(PrinterSession s, DgcpFrame f)
        {
            if (f.Command == Cmd.PrintDone)
            {
                bool waited;
                lock (_awaitingPrintDone) waited = _awaitingPrintDone.Remove(s);
                s.Write(LogKind.Info, "เครื่องแจ้งว่าพิมพ์เสร็จแล้ว");
                if (waited && s == _selected)
                    Ui.SetResult(_lblSendResult, "✓ พิมพ์เสร็จแล้ว " + DateTime.Now.ToString("HH:mm:ss") + " — ส่งชุดถัดไปได้", Ui.Good);
            }
            else if (f.Command == Cmd.StatusReport || f.Command == Cmd.GetStatus)
            {
                var st = DgcpResponses.ParseStatus(f.Data);
                s.LastStatus = st;
                if (s == _selected) ShowStatus(st);
            }
        }

        private async Task LoadTemplateListAsync()
        {
            List<string> files = null;
            var ok = await RunAsync("อ่านรายชื่อแม่แบบ", _lblSendResult, async c => files = await c.GetFileListAsync());
            if (!ok) return;
            var p = _selected.Profile;
            p.KnownTemplates = files.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keep = _cboTemplate.Text;
            _cboTemplate.Items.Clear();
            foreach (var n in p.KnownTemplates) _cboTemplate.Items.Add(n);
            _cboTemplate.Text = keep;
            Ui.SetResult(_lblSendResult, "✓ พบ " + p.KnownTemplates.Count + " แม่แบบ — เลือกจากช่องแม่แบบ", Ui.Good);
            SaveSettings();
            if (p.KnownTemplates.Count > 0)
            {
                _cboTemplate.Focus();
                _cboTemplate.DroppedDown = true;
            }
        }

        private async Task LoadFromDeviceAsync()
        {
            DeviceStatus st = null;
            var ok = await RunAsync("อ่านสถานะ", _lblSendResult, async c => st = await c.GetStatusAsync());
            if (!ok) return;
            _selected.LastStatus = st;
            ShowStatus(st);
            if (!st.HasNames)
            {
                Ui.SetResult(_lblSendResult, "เครื่องไม่ได้ส่งชื่อแม่แบบ/ชื่อตัวแปรมา — ต้องพิมพ์ชื่อตัวแปรเอง", Ui.Warn);
                return;
            }
            _loadingUi = true;
            try
            {
                _cboTemplate.Text = st.FileName ?? "";
                _grid.Rows.Clear();
                foreach (var v in st.Variables) _grid.Rows.Add(v.Name, v.Value, "");
            }
            finally
            {
                _loadingUi = false;
            }
            UpdatePreview();
            Ui.SetResult(_lblSendResult,
                "✓ แม่แบบ \"" + st.FileName + "\" มี " + st.Variables.Count + " ตัวแปร — แก้ค่าในตารางแล้วกดส่ง", Ui.Good);
        }

        private async Task ReadStatusAsync()
        {
            DeviceStatus st = null;
            var ok = await RunAsync("อ่านสถานะ", _lblStatusNote, async c => st = await c.GetStatusAsync());
            if (!ok) return;
            _selected.LastStatus = st;
            ShowStatus(st);
        }

        private void UpdateAutoTimer()
        {
            _autoTimer.Interval = (int)_numAutoSec.Value * 1000;
            _autoTimer.Enabled = _chkAuto.Checked;
        }

        private async Task AutoPollAsync()
        {
            var s = _selected;
            if (s == null || _busy || _polling || !s.IsConnected) return;
            _polling = true;
            try
            {
                var st = await s.Client.GetStatusAsync();
                s.LastStatus = st;
                if (s == _selected) ShowStatus(st);
            }
            catch (Exception ex)
            {
                if (s == _selected) Ui.SetResult(_lblStatusNote, "อ่านอัตโนมัติไม่สำเร็จ: " + ex.Message, Ui.Bad);
            }
            finally
            {
                _polling = false;
            }
        }

        private void ShowStatus(DeviceStatus st)
        {
            _lvStatus.BeginUpdate();
            _lvStatus.Items.Clear();
            _lbAlarms.Items.Clear();
            if (st == null)
            {
                Ui.SetResult(_lblBigStatus, "ยังไม่ได้อ่านสถานะ", Ui.Muted);
                _lblStatusNote.Text = "";
                _lvStatus.EndUpdate();
                return;
            }

            var color = st.StatusCode == 3 ? Ui.Good : st.StatusCode == 1 ? Ui.Accent : st.StatusCode == 2 ? Ui.Warn : Ui.Bad;
            Ui.SetResult(_lblBigStatus, st.StatusText, color);
            Ui.SetResult(_lblStatusNote,
                "อ่านเมื่อ " + DateTime.Now.ToString("HH:mm:ss") + (st.LayoutMismatch ? "  • ข้อมูลไม่ตรงรูปแบบในเอกสารทั้งหมด แสดงเท่าที่อ่านได้" : ""),
                st.LayoutMismatch ? Ui.Warn : Ui.Muted);

            void Row(string k, string v) => _lvStatus.Items.Add(new ListViewItem(new[] { k, v }));
            Row("แม่แบบที่ใช้อยู่", st.FileName ?? "-");
            for (int i = 0; i < st.Variables.Count; i++)
            {
                var v = st.Variables[i];
                Row("  ตัวแปร " + (v.Name ?? "#" + (i + 1)), v.Value);
            }
            Row("เวลาเครื่อง", st.Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(อ่านไม่ได้)");
            Row("ริบบอนเหลือ", st.RibbonRemaining.ToString(CultureInfo.InvariantCulture));
            Row("ความเร็วไลน์", st.LineSpeed.ToString(CultureInfo.InvariantCulture));
            Row("ความถี่พิมพ์", st.PrintFrequency.ToString(CultureInfo.InvariantCulture));
            Row("ตัวนับพิมพ์รวม", st.TotalCounter.ToString("N0", CultureInfo.InvariantCulture));
            Row("ตัวนับแม่แบบนี้", st.FileCounter.ToString("N0", CultureInfo.InvariantCulture));
            Row("ตัวนับริบบอนม้วนนี้", st.RibbonCounter.ToString("N0", CultureInfo.InvariantCulture));
            Row("ตัวนับริบบอนม้วนก่อน", st.PreviousRibbonCounter.ToString("N0", CultureInfo.InvariantCulture));
            Row("ความสูงงานพิมพ์", st.PrintHeight.ToString(CultureInfo.InvariantCulture));
            Row("เปลี่ยนริบบอนล่าสุด", st.RibbonChangeTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-");
            if (st.HasNames)
            {
                Row("มีสัญญาณสั่งพิมพ์", DgcpStatusText.HasPrintSignal(st.ErrorBits) ? "มี" : "ไม่มี");
                Row("มีสัญญาณ encoder", DgcpStatusText.HasEncoderSignal(st.ErrorBits) ? "มี" : "ไม่มี");
            }
            if (st.LocalStation.HasValue) Row("เลขเครื่อง (ST)", st.LocalStation.Value.ToString(CultureInfo.InvariantCulture));
            if (st.ParametersUpdated.HasValue) Row("ค่าเครื่องถูกแก้ไข", st.ParametersUpdated.Value ? "ใช่" : "ไม่");
            Row("Error bits", "0x" + st.ErrorBits.ToString("X8"));
            _lvStatus.EndUpdate();

            var alarms = st.ActiveAlarms().ToList();
            if (alarms.Count == 0) _lbAlarms.Items.Add("ไม่มี");
            foreach (var a in alarms) _lbAlarms.Items.Add("⚠ " + a);
        }

        private async Task ControlAsync(byte cmd, string confirm, Label result)
        {
            if (_selected == null) return;
            if (confirm != null && MessageBox.Show(this, confirm, Cmd.Name(cmd), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await RunAsync(Cmd.Name(cmd), result, c => c.ExecuteSimpleAsync(cmd), Cmd.Name(cmd) + " สำเร็จ");
        }

        private async Task GetTimeAsync()
        {
            DateTime? t = null;
            var ok = await RunAsync("อ่านเวลา", _lblControlResult, async c => t = await c.GetTimeAsync());
            if (!ok) return;
            if (!t.HasValue)
            {
                Ui.SetResult(_lblControlResult, "เครื่องตอบเวลามาแต่อ่านไม่ได้ (ดู HEX ใน log)", Ui.Warn);
                return;
            }
            var diff = (t.Value - DateTime.Now).TotalSeconds;
            Ui.SetResult(_lblControlResult,
                "เวลาเครื่อง " + t.Value.ToString("yyyy-MM-dd HH:mm:ss") + "  (" + (Math.Abs(diff) < 2 ? "ตรงกับคอม" : (diff > 0 ? "เร็วกว่า" : "ช้ากว่า") + "คอม " + Math.Abs(diff).ToString("N0") + " วินาที") + ")",
                Math.Abs(diff) < 60 ? Ui.Good : Ui.Warn);
        }

        private async Task SetTimeAsync()
        {
            if (_selected == null) return;
            var now = DateTime.Now;
            if (MessageBox.Show(this, "ตั้งเวลาเครื่องเป็น " + now.ToString("yyyy-MM-dd HH:mm:ss") + " ตามคอมพิวเตอร์นี้?\n(มีผลกับวันที่/เวลาที่พิมพ์บนสินค้า)",
                    "ตั้งเวลาเครื่อง", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await RunAsync("ตั้งเวลา", _lblControlResult, c => c.SetTimeAsync(DateTime.Now), "ตั้งเวลาเครื่องตามคอมพิวเตอร์แล้ว");
        }

        private async Task ReadParametersAsync()
        {
            List<ParameterItem> items = null;
            var ok = await RunAsync("อ่านค่าเครื่อง", _lblInfoResult, async c => items = await c.GetParametersAsync());
            if (!ok) return;
            _lvParams.BeginUpdate();
            _lvParams.Items.Clear();
            foreach (var p in items) _lvParams.Items.Add(new ListViewItem(new[] { p.Name, p.Value, p.RawHex }));
            _lvParams.EndUpdate();
            Ui.SetResult(_lblInfoResult, "✓ อ่านได้ " + items.Count + " รายการ " + DateTime.Now.ToString("HH:mm:ss"), Ui.Good);
        }

        private async Task ReadUsbAsync()
        {
            List<string> files = null;
            var ok = await RunAsync("อ่านไฟล์ USB", _lblInfoResult, async c => files = await c.GetFileListAsync(usb: true));
            if (!ok) return;
            _lbUsb.Items.Clear();
            foreach (var f in files) _lbUsb.Items.Add(f);
            if (files.Count == 0) _lbUsb.Items.Add("(ไม่มีไฟล์)");
            Ui.SetResult(_lblInfoResult, "✓ ไฟล์ใน USB " + files.Count + " ไฟล์", Ui.Good);
        }

        private async Task SendRawAsync()
        {
            if (_selected == null) return;
            byte[] bytes;
            try
            {
                bytes = Hex.Parse(_txtRaw.Text);
                if (bytes.Length == 0) throw new FormatException("ยังไม่ได้พิมพ์ HEX");
                if (_chkAutoHeader.Checked)
                {
                    if (bytes.Length < 2) throw new FormatException("ต้องมีอย่างน้อย ST และ CMD");
                    var data = new byte[bytes.Length - 2];
                    Buffer.BlockCopy(bytes, 2, data, 0, data.Length);
                    bytes = DgcpFrame.Build(bytes[0], bytes[1], data);
                }
                else if (_chkWaitReply.Checked && bytes.Length < 6)
                {
                    throw new FormatException("แพ็กเก็ตเต็มต้องมีอย่างน้อย 6 ไบต์ (5A A5 LEN LEN ST CMD)");
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is DgcpException)
            {
                Ui.SetResult(_lblRawResult, "✗ " + ex.Message, Ui.Bad);
                return;
            }

            DgcpFrame reply = null;
            bool wait = _chkWaitReply.Checked;
            var ok = await RunAsync("ส่ง HEX", _lblRawResult, async c => reply = await c.Connection.SendRawAsync(bytes, wait));
            if (!ok) return;
            _txtRawReply.Text = "ส่ง: " + Hex.Format(bytes) + Environment.NewLine +
                                (reply == null ? "(ไม่รอคำตอบ)" : "ตอบ: " + Hex.Format(reply.Raw) + Environment.NewLine + "แปล: " + DgcpDescriber.Describe(reply, false));
            Ui.SetResult(_lblRawResult, "✓ " + (reply == null ? "ส่งแล้ว" : "ได้คำตอบ") + " " + DateTime.Now.ToString("HH:mm:ss"), Ui.Good);
        }

        // ================================================================== log

        private void AddLog(LogEntry e)
        {
            if (e.IsStatusFrame && !_settings.ShowStatusFrames) return;
            AppLog.Write(e);
            var item = new ListViewItem(new[]
            {
                e.Time.ToString("HH:mm:ss.fff"),
                e.Printer,
                AppLog.KindText(e.Kind),
                e.Text,
                e.HexText ?? "",
            });
            item.ForeColor = e.Kind == LogKind.Tx ? Ui.TxColor : e.Kind == LogKind.Rx ? Ui.RxColor : e.Kind == LogKind.Error ? Ui.Bad : Ui.Muted;
            _lvLog.BeginUpdate();
            _lvLog.Items.Add(item);
            if (_lvLog.Items.Count > MaxLogRows)
            {
                for (int i = 0; i < 200; i++) _lvLog.Items.RemoveAt(0);
            }
            _lvLog.EndUpdate();
            item.EnsureVisible();
        }

        private void CopyLog()
        {
            var items = _lvLog.SelectedItems.Count > 0 ? _lvLog.SelectedItems.Cast<ListViewItem>() : _lvLog.Items.Cast<ListViewItem>();
            var text = string.Join(Environment.NewLine, items.Select(i => string.Join("\t", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))));
            if (text.Length > 0) Clipboard.SetText(text);
        }

        private void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.LogDirectory);
                Process.Start(new ProcessStartInfo { FileName = AppSettings.LogDirectory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "เปิดโฟลเดอร์ไม่ได้: " + ex.Message, Text);
            }
        }

        // ================================================================== อื่นๆ

        private void SaveSettings()
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                AddLog(new LogEntry { Printer = "-", Kind = LogKind.Error, Text = "บันทึกไฟล์ตั้งค่าไม่ได้: " + ex.Message });
            }
        }

        private void OnUi(Action a)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(a);
                }
                catch (InvalidOperationException)
                {
                    // ฟอร์มปิดแล้ว
                }
            }
            else
            {
                a();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _autoTimer.Stop();
            if (_selected != null) SaveUiToProfile(_selected.Profile);
            SaveSettings();
            foreach (var s in _sessions) s.Dispose();
            base.OnFormClosing(e);
        }
    }
}
