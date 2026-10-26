using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CapriDesktop
{
    internal enum TalkState { Off, Thinking, Talking, Online, Dormant }

    internal static class Look
    {
        internal static readonly Color Panel = Color.FromArgb(9, 11, 15);
        internal static readonly Color Cyan = Color.FromArgb(58, 214, 235);
        internal static readonly Color Red = Color.FromArgb(232, 36, 64);
        internal static readonly Color Yellow = Color.FromArgb(244, 214, 70);
        internal static readonly Color Neon = Color.FromArgb(56, 176, 255);
        internal static readonly Color Ink = Color.FromArgb(232, 236, 240);
        internal static readonly Color Dim = Color.FromArgb(122, 128, 138);

        internal static readonly Color Pink = Color.FromArgb(255, 46, 136);
        private static readonly FontFamily embedded = Embedded("Capri.Font.Mono.ttf");
        private static Cursor pointer;

        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { internal bool Icon; internal int X; internal int Y; internal IntPtr Mask; internal IntPtr Colour; }
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, ref IconInfo info);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);

        internal static Cursor Pointer
        {
            get
            {
                if (pointer != null) return pointer;
                float scale;
                using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, screen.DpiX / 96f);
                int size = (int)Math.Round(32 * scale);
                using (Bitmap art = PointerArt(size))
                {
                    IntPtr icon = art.GetHicon();
                    IconInfo info = new IconInfo();
                    GetIconInfo(icon, ref info);
                    info.Icon = false;
                    info.X = (int)Math.Round(2 * scale);
                    info.Y = (int)Math.Round(2 * scale);
                    IntPtr handle = CreateIconIndirect(ref info);
                    DeleteObject(info.Mask);
                    DeleteObject(info.Colour);
                    DestroyIcon(icon);
                    pointer = handle == IntPtr.Zero ? Cursors.Hand : new Cursor(handle);
                }
                return pointer;
            }
        }

        internal static Bitmap PointerArt(int size)
        {
            float s = size / 32f;
            PointF[] arrow = { new PointF(2, 2), new PointF(2, 24), new PointF(8, 18.5f), new PointF(12, 27), new PointF(16, 25.2f), new PointF(12, 16.8f), new PointF(19.5f, 16.8f) };
            Bitmap art = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Bitmap whole = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(whole))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Brush cyan = new SolidBrush(Color.FromArgb(200, Cyan))) graphics.FillPolygon(cyan, Shift(arrow, 1.6f, 0.4f, s));
                    using (Brush red = new SolidBrush(Color.FromArgb(170, Red))) graphics.FillPolygon(red, Shift(arrow, -1.1f, -0.3f, s));
                    PointF[] body = Shift(arrow, 0, 0, s);
                    using (Brush pink = new SolidBrush(Pink)) graphics.FillPolygon(pink, body);
                    using (Pen edge = new Pen(Color.FromArgb(20, 6, 14), 1.3f * s)) { edge.LineJoin = LineJoin.Round; graphics.DrawPolygon(edge, body); }
                    using (Pen shine = new Pen(Color.FromArgb(150, 255, 214, 236), 1f * s)) graphics.DrawLine(shine, 3.6f * s, 5.5f * s, 3.6f * s, 17 * s);
                }
                using (Graphics graphics = Graphics.FromImage(art))
                {
                    int cut = (int)Math.Round(11 * s);
                    int band = Math.Max(2, (int)Math.Round(2.5f * s));
                    int slip = Math.Max(1, (int)Math.Round(2.5f * s));
                    graphics.DrawImage(whole, new Rectangle(0, 0, size, cut), new Rectangle(0, 0, size, cut), GraphicsUnit.Pixel);
                    graphics.DrawImage(whole, new Rectangle(slip, cut, size - slip, band), new Rectangle(0, cut, size - slip, band), GraphicsUnit.Pixel);
                    graphics.DrawImage(whole, new Rectangle(0, cut + band, size, size - cut - band), new Rectangle(0, cut + band, size, size - cut - band), GraphicsUnit.Pixel);
                }
            }
            return art;
        }

        private static PointF[] Shift(PointF[] points, float x, float y, float s)
        {
            PointF[] moved = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++) moved[i] = new PointF((points[i].X + x) * s, (points[i].Y + y) * s);
            return moved;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint count);

        private static FontFamily Embedded(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);
                IntPtr memory = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, memory, data.Length);
                uint count = 0;
                AddFontMemResourceEx(memory, (uint)data.Length, IntPtr.Zero, ref count);
                PrivateFontCollection collection = new PrivateFontCollection();
                collection.AddMemoryFont(memory, data.Length);
                return collection.Families.Length > 0 ? collection.Families[0] : null;
            }
        }

        internal static Font Mono(float size, FontStyle style, GraphicsUnit unit = GraphicsUnit.Pixel)
        {
            if (embedded != null) return new Font(embedded, size, embedded.IsStyleAvailable(style) ? style : FontStyle.Regular, unit);
            foreach (string family in new[] { "Cascadia Mono", "Consolas", "Courier New" })
            {
                Font font = new Font(family, size, style, unit);
                if (font.Name == family) return font;
                font.Dispose();
            }
            return new Font(FontFamily.GenericMonospace, size, style, unit);
        }

        internal static float CharWidth(Font font)
        {
            using (Bitmap scratch = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(scratch))
                return graphics.MeasureString(new string('M', 20), font, PointF.Empty, StringFormat.GenericTypographic).Width / 20f;
        }

        internal static GraphicsPath Plate(RectangleF box, float cut, float tipX, float tipY, float tail, bool both = false)
        {
            float right = both ? cut : 0;
            List<PointF> points = new List<PointF>();
            float near = Math.Max(box.Left + cut + 2, tipX - tail * 0.8f);
            float far = Math.Min(box.Right - right - 2, tipX + tail * 0.2f);
            points.Add(new PointF(box.Left + cut, box.Top));
            if (tipY < box.Top) { points.Add(new PointF(near, box.Top)); points.Add(new PointF(tipX, tipY)); points.Add(new PointF(far, box.Top)); }
            points.Add(new PointF(box.Right - right, box.Top));
            if (both) points.Add(new PointF(box.Right, box.Top + cut));
            if (both) points.Add(new PointF(box.Right, box.Bottom - cut));
            points.Add(new PointF(box.Right - right, box.Bottom));
            if (tipY > box.Bottom) { points.Add(new PointF(far, box.Bottom)); points.Add(new PointF(tipX, tipY)); points.Add(new PointF(near, box.Bottom)); }
            points.Add(new PointF(box.Left + cut, box.Bottom));
            points.Add(new PointF(box.Left, box.Bottom - cut));
            points.Add(new PointF(box.Left, box.Top + cut));
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(points.ToArray());
            return path;
        }

        internal static GraphicsPath Rounded(RectangleF box, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(box.Left, box.Top, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static List<string> Wrap(string text, int columns)
        {
            List<string> lines = new List<string>();
            string line = "";
            foreach (string piece in (text ?? "").Split(' '))
            {
                string word = piece;
                while (word.Length > columns)
                {
                    if (line.Length > 0) { lines.Add(line); line = ""; }
                    lines.Add(word.Substring(0, columns));
                    word = word.Substring(columns);
                }
                if (line.Length == 0) line = word;
                else if (line.Length + 1 + word.Length <= columns) line += " " + word;
                else { lines.Add(line); line = word; }
            }
            if (line.Length > 0 || lines.Count == 0) lines.Add(line);
            return lines;
        }
    }

    internal class Overlay : Form
    {
        private Bitmap shown;
        internal bool Above = true;

        internal Overlay(string name)
        {
            Text = name;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | (Above ? 0x00000008 : 0);
                return value;
            }
        }

        internal Bitmap Image { get { return shown; } }

        internal void Present(Bitmap image, Point at)
        {
            Rectangle target = new Rectangle(at, image.Size);
            if (Bounds != target) Bounds = target;
            if (!Visible) Show();
            Native.DrawLayered(Handle, image, at);
            Bitmap old = shown;
            shown = image;
            if (old != null) old.Dispose();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && shown != null)
            {
                long value = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(value & 65535), (short)((value >> 16) & 65535)));
                if (point.X < 0 || point.Y < 0 || point.X >= shown.Width || point.Y >= shown.Height || shown.GetPixel(point.X, point.Y).A < 12)
                {
                    message.Result = new IntPtr(-1);
                    return;
                }
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && shown != null) { shown.Dispose(); shown = null; }
            base.Dispose(disposing);
        }
    }

    internal sealed class TalkPill : Overlay
    {
        internal event Action Typing;
        internal event Action Voice;
        internal event Action Mute;
        private int hover;
        private float scale = 1;
        private Point centre;
        private bool chatting;
        private bool muted;
        private int level;

        internal TalkPill() : base("Talk to Capri") { Cursor = Look.Pointer; }

        internal static Size Measure(float scale) { return new Size((int)Math.Round(100 * scale), (int)Math.Round(40 * scale)); }

        internal bool Chatting { get { return chatting; } }

        internal bool Muted { get { return muted; } }

        internal void Place(Point topCentre, float value)
        {
            if (Visible && topCentre == centre && value == scale) return;
            centre = topCentre;
            scale = value;
            Redraw();
        }

        internal void SetChat(bool on, bool micMuted)
        {
            if (on == chatting && micMuted == muted) return;
            chatting = on;
            muted = micMuted;
            level = 0;
            if (Visible) Redraw();
        }

        internal void SetLevel(double value)
        {
            int step = chatting && !muted ? Math.Min(6, (int)(Math.Sqrt(value) * 24)) : 0;
            if (step == level) return;
            level = step;
            if (Visible) Redraw();
        }

        private void Redraw()
        {
            Bitmap image = Render(scale, hover, chatting, muted, level);
            Present(image, new Point(centre.X - Measure(scale).Width / 2, centre.Y));
        }

        internal static Bitmap Render(float s, int hover, bool chatting = false, bool muted = false, int level = 0)
        {
            Size size = Measure(s);
            Bitmap image = new Bitmap(chatting ? size.Width + (int)Math.Round(4 * s) + size.Height : size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float margin = 3 * s;
                RectangleF pill = new RectangleF(margin, margin, size.Width - margin * 2, size.Height - margin * 2);
                Plate(graphics, pill, s);
                if (chatting)
                {
                    RectangleF button = new RectangleF(image.Width - size.Height + margin, margin, size.Height - margin * 2, size.Height - margin * 2);
                    Plate(graphics, button, s);
                    PointF spot = new PointF(button.X + button.Width / 2, button.Y + button.Height / 2);
                    if (hover == 3)
                    {
                        float radius = button.Height * 0.4f;
                        using (Brush glow = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) graphics.FillEllipse(glow, spot.X - radius, spot.Y - radius, radius * 2, radius * 2);
                    }
                    DrawMic(graphics, spot, s, hover == 3 ? Look.Cyan : Look.Ink, muted);
                }
                float middle = pill.X + pill.Width / 2;
                using (Pen divider = new Pen(Color.FromArgb(70, 73, 82), 1.2f * s)) graphics.DrawLine(divider, middle, pill.Y + pill.Height * 0.27f, middle, pill.Bottom - pill.Height * 0.27f);
                PointF pencil = new PointF(pill.X + pill.Width * 0.26f, pill.Y + pill.Height / 2);
                PointF wave = new PointF(pill.X + pill.Width * 0.74f, pill.Y + pill.Height / 2);
                if (hover == 1 || hover == 2)
                {
                    PointF lit = hover == 1 ? pencil : wave;
                    float radius = pill.Height * 0.4f;
                    using (Brush glow = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) graphics.FillEllipse(glow, lit.X - radius, lit.Y - radius, radius * 2, radius * 2);
                }
                DrawPencil(graphics, pencil, s, hover == 1 ? Look.Cyan : Look.Ink);
                DrawWave(graphics, wave, s, hover == 2 || chatting ? Look.Cyan : Look.Ink, chatting ? 0.45f + level * 0.14f : 1);
            }
            return image;
        }

        private static void Plate(Graphics graphics, RectangleF area, float s)
        {
            using (GraphicsPath path = Look.Rounded(area, area.Height / 2))
            {
                using (Brush fill = new SolidBrush(Color.FromArgb(244, 20, 21, 25))) graphics.FillPath(fill, path);
                using (Pen edge = new Pen(Color.FromArgb(70, 73, 82), 1.2f * s)) graphics.DrawPath(edge, path);
            }
        }

        private static void DrawMic(Graphics graphics, PointF centre, float s, Color colour, bool muted)
        {
            float x = centre.X;
            float y = centre.Y + 0.5f * s;
            using (Brush ink = new SolidBrush(colour))
            using (GraphicsPath capsule = Look.Rounded(new RectangleF(x - 3.2f * s, y - 9 * s, 6.4f * s, 11 * s), 3.2f * s))
                graphics.FillPath(ink, capsule);
            using (Pen line = new Pen(colour, 1.8f * s))
            {
                line.StartCap = LineCap.Round;
                line.EndCap = LineCap.Round;
                graphics.DrawArc(line, x - 6 * s, y - 8 * s, 12 * s, 12 * s, 0, 180);
                graphics.DrawLine(line, x, y + 4 * s, x, y + 7 * s);
                graphics.DrawLine(line, x - 3.5f * s, y + 7 * s, x + 3.5f * s, y + 7 * s);
            }
            if (!muted) return;
            using (Pen gap = new Pen(Color.FromArgb(20, 21, 25), 4.2f * s))
            using (Pen slash = new Pen(Look.Red, 2 * s))
            {
                slash.StartCap = LineCap.Round;
                slash.EndCap = LineCap.Round;
                graphics.DrawLine(gap, x - 7.5f * s, y - 9.5f * s, x + 7.5f * s, y + 7.5f * s);
                graphics.DrawLine(slash, x - 7.5f * s, y - 9.5f * s, x + 7.5f * s, y + 7.5f * s);
            }
        }

        private static void DrawPencil(Graphics graphics, PointF centre, float s, Color colour)
        {
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(centre.X, centre.Y);
            graphics.RotateTransform(-45);
            using (Brush ink = new SolidBrush(colour))
            using (GraphicsPath body = Look.Rounded(new RectangleF(-8 * s, -2.5f * s, 12 * s, 5 * s), 1.2f * s))
            {
                graphics.FillPath(ink, body);
                graphics.FillPolygon(ink, new[] { new PointF(4.6f * s, -2.5f * s), new PointF(9 * s, 0), new PointF(4.6f * s, 2.5f * s) });
            }
            using (Pen gap = new Pen(Color.FromArgb(20, 21, 25), 1.1f * s))
            {
                graphics.DrawLine(gap, -4.8f * s, -3 * s, -4.8f * s, 3 * s);
                graphics.DrawLine(gap, 4f * s, -3 * s, 4f * s, 3 * s);
            }
            graphics.Restore(state);
        }

        private static void DrawWave(Graphics graphics, PointF centre, float s, Color colour, float stretch = 1)
        {
            float[] heights = { 5, 10, 14, 9, 4 };
            using (Pen bar = new Pen(colour, 2.3f * s))
            {
                bar.StartCap = LineCap.Round;
                bar.EndCap = LineCap.Round;
                for (int i = 0; i < heights.Length; i++)
                {
                    float x = centre.X + (i - 2) * 3.6f * s;
                    float half = Math.Max(1.5f, heights[i] * stretch) * s / 2;
                    graphics.DrawLine(bar, x, centre.Y - half, x, centre.Y + half);
                }
            }
        }

        private int PartAt(int x)
        {
            int wide = Measure(scale).Width;
            if (chatting && x >= wide) return 3;
            return x < wide / 2 ? 1 : 2;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int part = PartAt(e.X);
            if (part != hover) { hover = part; Redraw(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != 0) { hover = 0; Redraw(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) Press(PartAt(e.X));
        }

        internal void Press(int part)
        {
            if (part == 1) { if (Typing != null) Typing(); }
            else if (part == 3) { if (Mute != null) Mute(); }
            else if (Voice != null) Voice();
        }
    }

    internal sealed class SpeechBox : Overlay
    {
        internal event Action Dismissed;
        internal TalkState State;
        internal string Heard;
        internal string Prompt = "> you: ";
        internal string Reply;
        internal int Revealed;
        internal double Opened = -1;
        internal double Leaving = -1;
        private readonly Random random = new Random();
        private Rectangle panelOnScreen;
        private const int Columns = 34;
        private const string Speaker = "CAPRI: ";

        internal SpeechBox() : base("Capri says") { Cursor = Look.Pointer; }

        internal Rectangle PanelOnScreen { get { return panelOnScreen; } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && Dismissed != null) Dismissed();
        }

        internal static float PanelWidth(float s)
        {
            using (Font font = Look.Mono(14 * s, FontStyle.Regular)) return 24 * s + Columns * Look.CharWidth(font) + 14 * s;
        }

        internal void Update(double now, float s, Point head, Point feet, Rectangle area)
        {
            float glitch = 0;
            if (Opened >= 0 && now - Opened < 0.2) glitch = (float)(1 - (now - Opened) / 0.2);
            if (Leaving >= 0) glitch = (float)Math.Min(1, (now - Leaving) / 0.25 + 0.3);
            bool cursor = State == TalkState.Thinking || State == TalkState.Talking ? (State == TalkState.Talking || (int)(now * 2.5) % 2 == 0) : (int)(now * 2) % 2 == 0;
            Point at;
            Bitmap image = Render(s, head, feet, area, cursor, out at);
            if (glitch > 0)
            {
                Bitmap broken = Glitch(image, glitch, s);
                image.Dispose();
                image = broken;
            }
            Present(image, at);
        }

        private Bitmap Render(float s, Point head, Point feet, Rectangle area, bool cursorOn, out Point at)
        {
            using (Font text = Look.Mono(14 * s, FontStyle.Regular))
            using (Font heard = Look.Mono(14 * s, FontStyle.Italic))
            using (Font bold = Look.Mono(14 * s, FontStyle.Bold))
            {
                float cw = Look.CharWidth(text);
                float line = 19 * s;
                float margin = 12 * s;
                float top = 10 * s;
                float tail = 16 * s;
                List<string> lines = Look.Wrap(Speaker + (Reply ?? ""), Columns);
                float panelWidth = 24 * s + Columns * cw + 14 * s;
                float panelHeight = 13 * s + line * (1 + lines.Count) + 10 * s;
                int width = (int)Math.Ceiling(panelWidth + margin * 2);
                int height = (int)Math.Ceiling(top + panelHeight + tail + 4 * s);
                bool below = head.Y - height < area.Top;
                Point tip = below ? feet : head;
                int left = Math.Max(area.Left, Math.Min(area.Right - width, (int)(tip.X - margin - panelWidth * 0.7f)));
                int y = below ? tip.Y : tip.Y - height;
                at = new Point(left, y);
                RectangleF panel = new RectangleF(margin, below ? tail + 4 * s : top, panelWidth, panelHeight);
                panelOnScreen = new Rectangle(left + (int)panel.X, y + (int)panel.Y, (int)panel.Width, (int)panel.Height);
                float tipX = Math.Max(panel.Left + 32 * s, Math.Min(panel.Right - 10 * s, tip.X - left));
                float tipY = below ? 0 : height - 1;
                Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                using (Graphics graphics = Graphics.FromImage(image))
                using (GraphicsPath path = Look.Plate(panel, 10 * s, tipX, tipY, 18 * s))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (Pen glow = new Pen(Color.FromArgb(46, Look.Cyan), 7 * s)) graphics.DrawPath(glow, path);
                    using (Brush fill = new SolidBrush(Color.FromArgb(238, Look.Panel))) graphics.FillPath(fill, path);
                    graphics.SetClip(path);
                    using (Pen scan = new Pen(Color.FromArgb(9, 255, 255, 255), Math.Max(1, s)))
                        for (float row = panel.Top; row < panel.Bottom; row += 3 * s) graphics.DrawLine(scan, panel.Left, row, panel.Right, row);
                    graphics.ResetClip();
                    using (Pen edge = new Pen(Look.Cyan, 2 * s)) graphics.DrawPath(edge, path);
                    using (Brush red = new SolidBrush(Look.Red)) graphics.FillRectangle(red, panel.Left + 10 * s, panel.Top + 13 * s, 3 * s, panel.Height - 26 * s);
                    float x = panel.Left + 24 * s;
                    float row2 = panel.Top + 12 * s;
                    string you = Prompt + (Heard ?? "");
                    if (you.Length > Columns) you = you.Substring(0, Columns - 3) + "...";
                    using (Brush dim = new SolidBrush(Look.Dim)) graphics.DrawString(you, heard, dim, x, row2, StringFormat.GenericTypographic);
                    row2 += line;
                    int remaining = Speaker.Length + (State == TalkState.Thinking ? 0 : Math.Max(0, Revealed));
                    PointF caret = new PointF(x, row2);
                    using (Brush red = new SolidBrush(Look.Red))
                    using (Brush ink = new SolidBrush(Look.Neon))
                    {
                        for (int i = 0; i < lines.Count && remaining > 0; i++)
                        {
                            int take = Math.Min(lines[i].Length, remaining);
                            string shown = lines[i].Substring(0, take);
                            int name = i == 0 ? Math.Min(take, Speaker.Length - 1) : 0;
                            if (name > 0) graphics.DrawString(shown.Substring(0, name), bold, red, x, row2, StringFormat.GenericTypographic);
                            graphics.DrawString(shown.Substring(name), text, ink, x + name * cw, row2 + i * line, StringFormat.GenericTypographic);
                            caret = new PointF(x + take * cw, row2 + i * line);
                            remaining -= take + 1;
                            if (remaining >= 0 && i + 1 < lines.Count) caret = new PointF(x, row2 + (i + 1) * line);
                        }
                        if (cursorOn) graphics.FillRectangle(ink, caret.X + s, caret.Y + s, cw * 0.85f, line - 5 * s);
                    }
                }
                return image;
            }
        }

        private Bitmap Glitch(Bitmap clean, float amount, float s)
        {
            Bitmap broken = new Bitmap(clean.Width, clean.Height, PixelFormat.Format32bppPArgb);
            if (random.NextDouble() < 0.25 * amount) return broken;
            using (Graphics graphics = Graphics.FromImage(broken))
            {
                int y = 0;
                while (y < clean.Height)
                {
                    int band = Math.Min(clean.Height - y, (int)((3 + random.Next(10)) * s));
                    int shift = (int)((random.NextDouble() * 2 - 1) * 10 * s * amount);
                    graphics.DrawImage(clean, new Rectangle(shift, y, clean.Width, band), new Rectangle(0, y, clean.Width, band), GraphicsUnit.Pixel);
                    y += band;
                }
                for (int i = 0; i < 2; i++)
                {
                    int band = (int)((2 + random.Next(6)) * s);
                    int row = random.Next(Math.Max(1, clean.Height - band));
                    int shift = (int)((random.NextDouble() * 2 - 1) * 14 * s * amount);
                    bool cyan = i == 0;
                    using (ImageAttributes tint = new ImageAttributes())
                    {
                        tint.SetColorMatrix(new ColorMatrix(new[]
                        {
                            new float[] { cyan ? 0 : 1, 0, 0, 0, 0 },
                            new float[] { 0, cyan ? 1 : 0, 0, 0, 0 },
                            new float[] { 0, 0, cyan ? 1 : 0, 0, 0 },
                            new float[] { 0, 0, 0, 0.6f * amount, 0 },
                            new float[] { 0, 0, 0, 0, 1 }
                        }));
                        graphics.DrawImage(clean, new Rectangle(shift, row, clean.Width, band), 0, row, clean.Width, band, GraphicsUnit.Pixel, tint);
                    }
                }
            }
            return broken;
        }
    }

    internal sealed class TypeBox : Form
    {
        internal event Action<string> Submitted;
        private readonly TextBox input;
        private readonly Label prompt;
        private float scale = 1;
        private Font typed;
        private Font italic;

        internal TypeBox()
        {
            Text = "Talk to Capri";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Look.Panel;
            prompt = new Label();
            prompt.Text = "> you:";
            prompt.ForeColor = Look.Dim;
            prompt.BackColor = Look.Panel;
            prompt.AutoSize = true;
            input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.BackColor = Look.Panel;
            input.ForeColor = Look.Ink;
            input.MaxLength = 240;
            input.KeyDown += delegate(object sender, KeyEventArgs key)
            {
                if (key.KeyCode == Keys.Enter) { key.SuppressKeyPress = true; Submit(); }
                else if (key.KeyCode == Keys.Escape) { key.SuppressKeyPress = true; Hide(); }
            };
            Controls.Add(prompt);
            Controls.Add(input);
            Deactivate += delegate { Hide(); };
        }

        internal string Draft { get { return input.Text; } set { input.Text = value; } }

        internal void Open(Point head, Point feet, Rectangle area, float s, Rectangle above)
        {
            if (typed == null || scale != s)
            {
                scale = s;
                Font oldTyped = typed;
                Font oldItalic = italic;
                typed = Look.Mono(14 * s, FontStyle.Regular);
                italic = Look.Mono(14 * s, FontStyle.Italic);
                input.Font = typed;
                prompt.Font = italic;
                if (oldTyped != null) { oldTyped.Dispose(); oldItalic.Dispose(); }
            }
            int width = (int)SpeechBox.PanelWidth(s);
            int height = (int)(44 * s);
            bool below = head.Y - height - 20 * s < area.Top;
            int left = Math.Max(area.Left, Math.Min(area.Right - width, (int)(head.X - width * 0.7f)));
            int top = below ? feet.Y + (int)(8 * s) : head.Y - height - (int)(18 * s);
            Bounds = new Rectangle(left, top, width, height);
            if (!above.IsEmpty) Follow(above, area);
            prompt.Location = new Point((int)(24 * s), (height - prompt.PreferredHeight) / 2);
            input.Location = new Point(prompt.Right + (int)(6 * s), (height - input.PreferredHeight) / 2 + (int)s);
            input.Width = width - input.Left - (int)(14 * s);
            using (GraphicsPath shape = Look.Plate(new RectangleF(0, 0, width, height), 10 * s, -1, height / 2f, 0, true)) Region = new Region(shape);
            Show();
            Activate();
            input.Focus();
            input.SelectionStart = input.Text.Length;
        }

        internal void Follow(Rectangle above, Rectangle area)
        {
            int top = above.Top - Height - (int)(16 * scale);
            if (top < area.Top) top = above.Bottom + (int)(8 * scale);
            Point target = new Point(above.Left, top);
            if (Location != target) Location = target;
        }

        private void Submit()
        {
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            input.Text = "";
            if (Submitted != null) Submitted(text);
        }

        internal void Send(string text) { input.Text = text; Submit(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float s = scale;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath edge = Look.Plate(new RectangleF(s, s, Width - 2 * s - 1, Height - 2 * s - 1), 10 * s, -1, Height / 2f, 0, true))
            using (Pen cyan = new Pen(Look.Cyan, 2 * s))
                e.Graphics.DrawPath(cyan, edge);
            using (Brush red = new SolidBrush(Look.Red)) e.Graphics.FillRectangle(red, 10 * s, 10 * s, 3 * s, Height - 20 * s);
        }
    }
}
