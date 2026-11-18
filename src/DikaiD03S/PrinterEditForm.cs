using System;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Windows.Forms;
using Dikai.Dgcp;

namespace Dikai.App
{
    /// <summary>หน้าต่างเพิ่ม/แก้ไขเครื่องพิมพ์ 1 เครื่อง</summary>
    internal sealed class PrinterEditForm : Form
    {
        private readonly PrinterProfile _p;
        private readonly TextBox _name = new TextBox { Width = 240 };
        private readonly RadioButton _rbTcp = new RadioButton { Text = "LAN (TCP/IP)", AutoSize = true, Checked = true };
        private readonly RadioButton _rbSerial = new RadioButton { Text = "RS232 (COM)", AutoSize = true };
        private readonly TextBox _host = new TextBox { Width = 160 };
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 90 };
        private readonly ComboBox _com = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _baud = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _st = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 70 };

        public PrinterEditForm(PrinterProfile profile, bool isNew)
        {
            _p = profile;
            Text = isNew ? "เพิ่มเครื่องพิมพ์" : "แก้ไขเครื่องพิมพ์";
            Font = Ui.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            void Add(string label, Control c)
            {
                grid.Controls.Add(Ui.Label(label), 0, grid.RowCount);
                c.Margin = new Padding(3, 4, 3, 4);
                grid.Controls.Add(c, 1, grid.RowCount);
                grid.RowCount++;
            }

            var kind = Ui.Row(_rbTcp, _rbSerial);
            Add("ชื่อเครื่อง", _name);
            Add("การเชื่อมต่อ", kind);
            Add("IP address", _host);
            Add("Port", _port);
            Add("COM port", _com);
            Add("Baud rate", _baud);
            Add("เลขเครื่อง (ST)", _st);

            var hint = Ui.Label(
                "• LAN: ค่าจากโรงงานคือ 192.168.1.2 port 5000 — ทุกเครื่องต้องตั้ง IP ไม่ซ้ำกันที่หน้าจอเครื่อง\n" +
                "• ST ต้องตรงกับเลขที่ตั้งในเครื่อง (ตัวอย่างในเอกสารใช้ 1) ถ้าเครื่องตอบ error 10 แปลว่าไม่ตรง\n" +
                "• ST = 0 คือส่งถึงทุกเครื่อง", null, Ui.Muted);
            hint.MaximumSize = new Size(460, 0);
            grid.Controls.Add(hint, 0, grid.RowCount);
            grid.SetColumnSpan(hint, 2);
            grid.RowCount++;

            var ok = Ui.Button("บันทึก", OnOk, 90);
            var cancel = Ui.Button("ยกเลิก", null, 90);
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons, 0, grid.RowCount);
            grid.SetColumnSpan(buttons, 2);
            grid.RowCount++;

            Controls.Add(grid);
            AcceptButton = ok;
            CancelButton = cancel;

            _baud.Items.AddRange(new object[] { 9600, 19200, 38400, 115200 });
            try
            {
                _com.Items.AddRange(SerialPort.GetPortNames().Distinct().OrderBy(x => x).Cast<object>().ToArray());
            }
            catch (Exception)
            {
                // ไม่มี COM port
            }

            _name.Text = _p.Name;
            _rbSerial.Checked = _p.IsSerial;
            _rbTcp.Checked = !_p.IsSerial;
            _host.Text = _p.Host;
            _port.Value = _p.Port;
            _com.Text = _p.ComPort ?? (_com.Items.Count > 0 ? _com.Items[0].ToString() : "COM1");
            _baud.SelectedItem = _baud.Items.Contains(_p.BaudRate) ? (object)_p.BaudRate : DgcpLimits.DefaultBaudRate;
            _st.Value = _p.Station;

            _rbTcp.CheckedChanged += (s, e) => UpdateEnabled();
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool tcp = _rbTcp.Checked;
            _host.Enabled = tcp;
            _port.Enabled = tcp;
            _com.Enabled = !tcp;
            _baud.Enabled = !tcp;
        }

        private void OnOk(object sender, EventArgs e)
        {
            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                Warn("กรุณาใส่ชื่อเครื่อง", _name);
                return;
            }
            if (_rbTcp.Checked && _host.Text.Trim().Length == 0)
            {
                Warn("กรุณาใส่ IP address", _host);
                return;
            }
            if (_rbSerial.Checked && _com.Text.Trim().Length == 0)
            {
                Warn("กรุณาเลือก COM port", _com);
                return;
            }

            _p.Name = name;
            _p.Kind = _rbTcp.Checked ? "tcp" : "serial";
            _p.Host = _host.Text.Trim();
            _p.Port = (int)_port.Value;
            _p.ComPort = _com.Text.Trim().ToUpperInvariant();
            _p.BaudRate = (int)(_baud.SelectedItem ?? DgcpLimits.DefaultBaudRate);
            _p.Station = (int)_st.Value;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Warn(string message, Control focus)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focus.Focus();
        }
    }
}
