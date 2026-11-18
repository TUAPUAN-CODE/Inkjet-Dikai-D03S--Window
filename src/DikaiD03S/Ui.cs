using System;
using System.Drawing;
using System.Windows.Forms;

namespace Dikai.App
{
    /// <summary>ฟอนต์/สี/ตัวช่วยสร้าง control (ไม่ใช้ designer เพื่อให้ build ได้ทุกเครื่อง)</summary>
    internal static class Ui
    {
        public static readonly Color Good = Color.FromArgb(22, 128, 61);
        public static readonly Color Bad = Color.FromArgb(200, 30, 30);
        public static readonly Color Warn = Color.FromArgb(190, 110, 0);
        public static readonly Color Muted = Color.FromArgb(100, 100, 100);
        public static readonly Color Accent = Color.FromArgb(0, 95, 184);
        public static readonly Color TxColor = Color.FromArgb(0, 70, 160);
        public static readonly Color RxColor = Color.FromArgb(0, 115, 60);

        private static Font _font, _bold, _big, _mono;

        /// <summary>Leelawadee UI (Windows 8+) อ่านภาษาไทยชัดที่สุด ถ้าไม่มีใช้ Tahoma</summary>
        public static Font Font => _font ?? (_font = Pick(10f, FontStyle.Regular));
        public static Font Bold => _bold ?? (_bold = new Font(Font, FontStyle.Bold));
        public static Font Big => _big ?? (_big = Pick(14f, FontStyle.Bold));
        public static Font Mono => _mono ?? (_mono = new Font(FontFamily.GenericMonospace, 9.5f));

        private static Font Pick(float size, FontStyle style)
        {
            foreach (var name in new[] { "Leelawadee UI", "Tahoma" })
            {
                var f = new Font(name, size, style);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        public static Button Button(string text, EventHandler click, int minWidth = 0)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(3),
                UseVisualStyleBackColor = true,
            };
            if (minWidth > 0) b.MinimumSize = new Size(minWidth, 0);
            if (click != null) b.Click += click;
            return b;
        }

        public static Label Label(string text, Font font = null, Color? color = null)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(3, 7, 3, 3),
            };
            if (font != null) l.Font = font;
            if (color.HasValue) l.ForeColor = color.Value;
            return l;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            p.Controls.AddRange(controls);
            return p;
        }

        public static TableLayoutPanel Stack()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(6),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        /// <summary>เพิ่มแถว (fill = แถวที่ยืดเต็มพื้นที่ที่เหลือ)</summary>
        public static void AddRow(TableLayoutPanel t, Control c, bool fill = false)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
            t.RowCount = t.RowStyles.Count;
        }

        public static ListView Details(params (string text, int width)[] columns)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false,
            };
            foreach (var c in columns) lv.Columns.Add(c.text, c.width);
            return lv;
        }

        public static void SetResult(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }
    }
}
