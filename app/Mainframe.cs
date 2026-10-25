using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CapriDesktop
{
    internal sealed class Mainframe : Form
    {
        private static readonly Color Field = Color.FromArgb(17, 21, 28);
        private static readonly Color Hover = Color.FromArgb(26, 32, 42);
        private static readonly Color Pressed = Color.FromArgb(34, 42, 55);
        private static readonly Color Edge = Color.FromArgb(52, 60, 74);
        private static readonly Color Track = Color.FromArgb(30, 36, 46);
        private const int Wide = 380;
        private const int Tall = 374;
        private const int SetupHeight = 118;
        private readonly Companion capri;
        private readonly Label brainName;
        private readonly Label brainNote;
        private readonly Key models;
        private readonly ContextMenuStrip modelMenu;
        private readonly Key refresh;
        private readonly Label[] steps = new Label[3];
        private readonly string[] stepText = new string[3];
        private readonly Key setup;
        private readonly Label hint;
        private readonly Key showHide;
        private readonly Key perch;
        private readonly Key[] sizes;
        private readonly Toggle motion;
        private readonly Toggle onTop;
        private readonly List<KeyValuePair<Control, int>> lower = new List<KeyValuePair<Control, int>>();
        private string[] lastInstalled;
        private float filled;
        private bool expanded = true;
        private bool loaded;
        private bool syncing;
        internal bool Quitting;

        internal Mainframe(Companion owner, Icon icon, string version)
        {
            capri = owner;
            Text = "Capri Mainframe";
            Icon = icon;
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = Look.Mono(9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Look.Panel;
            ForeColor = Look.Ink;
            DoubleBuffered = true;
            ClientSize = new Size(Wide, Tall + SetupHeight);
            Caption("MAINFRAME", 26, 20, Look.Mono(13f, FontStyle.Bold, GraphicsUnit.Point), Look.Ink);
            Label build = Caption("v" + version, 200, 20, Look.Mono(8.5f, FontStyle.Regular, GraphicsUnit.Point), Look.Dim);
            build.AutoSize = false;
            build.TextAlign = ContentAlignment.MiddleRight;
            build.Bounds = new Rectangle(200, 20, 92, 22);
            Key minimize = Make("", 300, 20, 26, 22, delegate { WindowState = FormWindowState.Minimized; });
            minimize.Glyph = 1;
            minimize.AccessibleName = "Minimize";
            Key close = Make("", 330, 20, 26, 22, delegate { Close(); });
            close.Glyph = 2;
            close.AccessibleName = "Close";
            Caption("BRAIN >", 28, 58, Look.Mono(9f, FontStyle.Bold, GraphicsUnit.Point), Look.Yellow);
            brainName = Caption("", 28, 78, Look.Mono(10.5f, FontStyle.Bold, GraphicsUnit.Point), Look.Ink);
            brainNote = Caption("", 28, 100, Font, Look.Dim);
            modelMenu = new ContextMenuStrip();
            modelMenu.Renderer = new ToolStripProfessionalRenderer(new CapriMenuColors());
            modelMenu.BackColor = Look.Panel;
            modelMenu.ForeColor = Look.Ink;
            modelMenu.Font = Font;
            modelMenu.ShowImageMargin = false;
            modelMenu.ShowCheckMargin = true;
            models = Make("", 28, 134, 244, 28, delegate { modelMenu.Show(models, new Point(0, models.Height)); });
            models.Chevron = true;
            refresh = Make("Refresh", 280, 134, 76, 28, delegate { capri.FindBrain(); });
            for (int i = 0; i < 3; i++) steps[i] = Caption("", 28, 138 + i * 22, Font, Look.Dim);
            setup = Make("Set up her brain", 28, 226, 170, 28, delegate { if (capri.SettingUp) capri.CancelSetup(); else capri.SetUpBrain(); });
            setup.Accent = Look.Cyan;
            setup.Loud = true;
            hint = Caption("Have Ollama? Open it, then press Refresh.", 28, 264, Look.Mono(9f, FontStyle.Italic, GraphicsUnit.Point), Look.Dim);
            Lower(Caption("CAPRI >", 28, 184, Look.Mono(9f, FontStyle.Bold, GraphicsUnit.Point), Look.Yellow));
            showHide = Lower(Make("Hide Capri", 28, 206, 104, 28, delegate { capri.ToggleHidden(); }));
            Lower(Make("Bring here", 140, 206, 104, 28, delegate { capri.BringHere(); }));
            perch = Lower(Make("Perch", 252, 206, 104, 28, delegate { capri.TogglePerch(); }));
            Lower(Caption("Size", 28, 251, Font, Look.Dim));
            sizes = new Key[3];
            int[] values = { 170, 240, 320 };
            string[] names = { "Small", "Medium", "Large" };
            for (int i = 0; i < 3; i++)
            {
                int size = values[i];
                sizes[i] = Lower(Make(names[i], 84 + i * 93, 246, 86, 26, delegate { capri.SetCharacterSize(size); }));
                sizes[i].Tag = size;
            }
            motion = Lower(Check("Idle motion", 28, 286, 150, delegate { if (!syncing) capri.ToggleMotion(); }));
            onTop = Lower(Check("Always on top", 186, 286, 170, delegate { if (!syncing) capri.ToggleOnTop(); }));
            Lower(Make("Check for updates", 28, 328, 170, 28, delegate { capri.CheckUpdates(); }));
            Key quit = Lower(Make("Quit Capri", 256, 328, 100, 28, delegate { capri.Quit(); }));
            quit.Accent = Look.Red;
            Searching();
            Sync();
        }

        private T Lower<T>(T control) where T : Control
        {
            lower.Add(new KeyValuePair<Control, int>(control, control.Top));
            return control;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.Style = (value.Style | 0x00020000 | 0x00080000) & ~0x00010000;
                return value;
            }
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0084 && message.Result == new IntPtr(1)) message.Result = new IntPtr(2);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            loaded = true;
            Arrange(expanded);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Shape();
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float s = DeviceScale();
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Backdrop(graphics, this, s);
            using (GraphicsPath edge = Look.Plate(new RectangleF(s, s, Width - 2 * s - 1, Height - 2 * s - 1), 12 * s, -1, Height / 2f, 0, true))
            using (Pen cyan = new Pen(Look.Cyan, 2 * s))
                graphics.DrawPath(cyan, edge);
            int shift = expanded ? SetupHeight : 0;
            using (Brush red = new SolidBrush(Look.Red))
            {
                graphics.FillRectangle(red, 13 * s, 58 * s, 3 * s, ((expanded ? 280 : 162) - 58) * s);
                graphics.FillRectangle(red, 13 * s, (184 + shift) * s, 3 * s, (308 - 184) * s);
            }
            if (expanded)
            {
                float pitch = 6 * s;
                int count = (int)(328 / 6f);
                int lit = (int)Math.Round(count * filled);
                using (Brush on = new SolidBrush(Look.Cyan))
                using (Brush off = new SolidBrush(Track))
                    for (int i = 0; i < count; i++) graphics.FillRectangle(i < lit ? on : off, 28 * s + i * pitch, 210 * s, pitch - 2 * s, 6 * s);
            }
        }

        private static void Backdrop(Graphics graphics, Control control, float s)
        {
            graphics.Clear(Look.Panel);
            float pitch = 3 * s;
            float first = control is Form ? 0 : (pitch - control.Top % pitch) % pitch;
            using (Pen scan = new Pen(Color.FromArgb(9, 255, 255, 255), Math.Max(1, s)))
                for (float row = first; row < control.Height; row += pitch) graphics.DrawLine(scan, 0, row, control.Width, row);
        }

        private void Shape()
        {
            float s = DeviceScale();
            Region old = Region;
            using (GraphicsPath plate = Look.Plate(new RectangleF(0, 0, Width, Height), 12 * s, -1, Height / 2f, 0, true)) Region = new Region(plate);
            if (old != null) old.Dispose();
        }

        private float DeviceScale() { return CurrentAutoScaleDimensions.Width / 96f; }

        private int Scaled(int value) { return loaded ? (int)Math.Round(value * DeviceScale()) : value; }

        private void Arrange(bool setupShown)
        {
            expanded = setupShown;
            int shift = setupShown ? SetupHeight : 0;
            foreach (KeyValuePair<Control, int> entry in lower) entry.Key.Top = Scaled(entry.Value + shift);
            foreach (Label step in steps) step.Visible = setupShown;
            setup.Visible = setupShown;
            refresh.Top = Scaled(setupShown ? 226 : 134);
            brainNote.MaximumSize = new Size(Scaled(328), 0);
            ClientSize = new Size(Scaled(Wide), Scaled(Tall + shift));
            Invalidate(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; WindowState = FormWindowState.Minimized; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) modelMenu.Dispose();
            base.Dispose(disposing);
        }

        internal void Restore()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        internal void Sync()
        {
            syncing = true;
            showHide.Text = capri.IsHidden ? "Show Capri" : "Hide Capri";
            perch.Text = capri.Riding ? "Hop off" : "Perch";
            foreach (Key option in sizes)
            {
                option.Picked = (int)option.Tag == capri.CharacterSize;
                option.Invalidate();
            }
            motion.Checked = capri.Motion;
            onTop.Checked = capri.OnTop;
            syncing = false;
        }

        internal string Status { get { return brainName.Text + " | " + brainNote.Text; } }

        internal void Searching()
        {
            brainName.Text = "Looking for a brain...";
            brainNote.Text = "";
            models.Visible = false;
            hint.Visible = false;
            Arrange(false);
        }

        internal void ShowOllama(string[] installed, string chosen)
        {
            brainName.Text = "Ollama · " + chosen;
            brainNote.Text = "Running on this PC";
            models.Text = chosen;
            models.Visible = true;
            hint.Visible = false;
            lastInstalled = installed;
            modelMenu.Items.Clear();
            foreach (string name in installed)
            {
                string pick = name;
                ToolStripMenuItem item = new ToolStripMenuItem(name, null, delegate { capri.ChooseModel(pick); ShowOllama(lastInstalled, pick); });
                item.Checked = name == chosen;
                modelMenu.Items.Add(item);
            }
            Arrange(false);
        }

        internal void ShowLocal(Pick pick)
        {
            brainName.Text = "Built-in · " + pick.Name;
            brainNote.Text = "Running on this PC";
            models.Visible = false;
            hint.Visible = false;
            Arrange(false);
        }

        internal void ShowSetup(Machine machine, Pick pick, bool viaOllama, string problem)
        {
            brainName.Text = problem == null ? "No brain yet" : "Setup stopped";
            brainNote.Text = problem ?? "Best fit for this PC: " + pick.Name;
            models.Visible = false;
            Step(0, "✓", machine.Describe(), Look.Ink);
            Step(1, "2", "Download " + pick.Name + " (" + pick.Download + ")" + (viaOllama ? " with Ollama" : ""), Look.Dim);
            Step(2, "3", "Wake her up", Look.Dim);
            filled = 0;
            setup.Text = problem == null ? "Set up her brain" : "Try again";
            setup.Enabled = true;
            hint.Visible = !viaOllama;
            Arrange(true);
        }

        internal void ShowProgress(int step, string text, long done, long total)
        {
            Step(1, step > 1 ? "✓" : "2", stepText[1], step == 1 ? Look.Cyan : Look.Ink);
            Step(2, "3", stepText[2], step == 2 ? Look.Cyan : Look.Dim);
            brainName.Text = step == 1 ? "Setting up her brain" : "Waking her up";
            brainNote.Text = total > 0 ? text + " · " + Engine.Amount(done) + " of " + Engine.Amount(total) : text;
            filled = total > 0 ? Math.Min(1f, done / (float)total) : step == 2 ? 1f : 0f;
            setup.Text = step == 1 ? "Cancel" : "Starting...";
            setup.Enabled = step == 1;
            hint.Visible = false;
            if (!expanded) Arrange(true);
            else Invalidate(new Rectangle(0, Scaled(204), Width, Scaled(18)));
        }

        internal void ShowProblem(string problem) { brainNote.Text = problem; }

        private void Step(int index, string mark, string text, Color colour)
        {
            stepText[index] = text;
            steps[index].Text = mark + "  " + text;
            steps[index].ForeColor = colour;
        }

        private Label Caption(string text, int x, int y, Font font, Color colour)
        {
            Label label = new Note();
            label.Text = text;
            label.Font = font;
            label.ForeColor = colour;
            label.BackColor = Look.Panel;
            label.AutoSize = true;
            label.Location = new Point(x, y);
            Controls.Add(label);
            return label;
        }

        private Key Make(string text, int x, int y, int width, int height, EventHandler click)
        {
            Key key = new Key();
            key.Text = text;
            key.Bounds = new Rectangle(x, y, width, height);
            key.Click += click;
            Controls.Add(key);
            return key;
        }

        private Toggle Check(string text, int x, int y, int width, EventHandler changed)
        {
            Toggle box = new Toggle();
            box.Text = text;
            box.Bounds = new Rectangle(x, y, width, 22);
            box.CheckedChanged += changed;
            Controls.Add(box);
            return box;
        }

        private sealed class Note : Label
        {
            internal Note() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }

            protected override void OnPaintBackground(PaintEventArgs e) { Backdrop(e.Graphics, this, ((Mainframe)Parent).DeviceScale()); }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; }
                base.WndProc(ref message);
            }
        }

        private sealed class Key : Button
        {
            internal Color Accent = Look.Ink;
            internal bool Loud;
            internal bool Picked;
            internal bool Chevron;
            internal int Glyph;
            private bool hot;
            private bool down;

            internal Key()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                BackColor = Look.Panel;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }

            protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }

            protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                float s = ((Mainframe)Parent).DeviceScale();
                Graphics graphics = e.Graphics;
                Backdrop(graphics, this, s);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool lit = hot || Picked || (Focused && ShowFocusCues);
                Color hotColour = Accent == Look.Ink ? Look.Cyan : Accent;
                if (Glyph == 0 || hot)
                {
                    using (GraphicsPath plate = Look.Plate(new RectangleF(0.7f * s, 0.7f * s, Width - 1.4f * s - 1, Height - 1.4f * s - 1), 6 * s, -1, Height / 2f, 0, true))
                    {
                        using (Brush fill = new SolidBrush(down ? Pressed : hot ? Hover : Field)) graphics.FillPath(fill, plate);
                        if (Glyph == 0)
                        {
                            Color edge = !Enabled ? Field : lit ? hotColour : Loud ? Color.FromArgb(170, Accent) : Edge;
                            using (Pen pen = new Pen(edge, 1.4f * s)) graphics.DrawPath(pen, plate);
                        }
                    }
                }
                Color ink = !Enabled ? Look.Dim : Picked ? Look.Cyan : Accent;
                float x = Width / 2f;
                float y = Height / 2f;
                if (Glyph != 0)
                {
                    using (Pen pen = new Pen(hot ? (Glyph == 2 ? Look.Red : Look.Cyan) : Look.Dim, 1.5f * s))
                    {
                        if (Glyph == 1) graphics.DrawLine(pen, x - 5 * s, y, x + 5 * s, y);
                        else
                        {
                            graphics.DrawLine(pen, x - 4.5f * s, y - 4.5f * s, x + 4.5f * s, y + 4.5f * s);
                            graphics.DrawLine(pen, x - 4.5f * s, y + 4.5f * s, x + 4.5f * s, y - 4.5f * s);
                        }
                    }
                    return;
                }
                if (Chevron)
                {
                    float left = Width - 18 * s;
                    using (Brush mark = new SolidBrush(Look.Dim)) graphics.FillPolygon(mark, new[] { new PointF(left, y - 2 * s), new PointF(left + 8 * s, y - 2 * s), new PointF(left + 4 * s, y + 3 * s) });
                }
                Rectangle area = Chevron ? new Rectangle((int)(10 * s), 0, Width - (int)(30 * s), Height) : ClientRectangle;
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (Chevron ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(graphics, Text, Font, area, ink, flags);
            }
        }

        private sealed class Toggle : CheckBox
        {
            private bool hot;

            internal Toggle()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                BackColor = Look.Panel;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }

            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                float s = ((Mainframe)Parent).DeviceScale();
                Graphics graphics = e.Graphics;
                Backdrop(graphics, this, s);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float side = 13 * s;
                RectangleF box = new RectangleF(s, (Height - side) / 2, side, side);
                using (Brush fill = new SolidBrush(Field)) graphics.FillRectangle(fill, box);
                using (Pen edge = new Pen(hot || (Focused && ShowFocusCues) ? Look.Cyan : Edge, 1.4f * s)) graphics.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
                if (Checked) using (Brush mark = new SolidBrush(Look.Cyan)) graphics.FillRectangle(mark, box.X + 3.5f * s, box.Y + 3.5f * s, box.Width - 7 * s, box.Height - 7 * s);
                int left = (int)(box.Right + 9 * s);
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(left, 0, Width - left, Height), Look.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }
}
