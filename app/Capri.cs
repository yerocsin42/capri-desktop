using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace CapriDesktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool smoke = args.Length == 2 && args[0] == "--smoke-test";
            string identity = WindowsIdentity.GetCurrent().User.Value;
            bool first;
            using (Mutex mutex = new Mutex(true, "Local\\Capri.Desktop." + identity + (smoke ? ".Test" : ""), out first))
            using (EventWaitHandle restore = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Capri.Restore." + identity + (smoke ? ".Test" : "")))
            {
                if (!first) { restore.Set(); return 0; }
                try
                {
                    string data = smoke ? Path.GetFullPath(args[1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capri");
                    using (Companion app = new Companion(data, restore, smoke))
                    {
                        if (smoke) app.StartSmokeTest(data);
                        Application.Run(app);
                        return app.TestFailed ? 1 : 0;
                    }
                }
                catch (Exception error)
                {
                    if (smoke) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), error.ToString()); return 1; }
                    MessageBox.Show("Capri could not start.\n\n" + error.Message, "Capri", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }

    [DataContract]
    internal sealed class Preferences
    {
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public bool HasPosition;
        [DataMember] public int Size = 240;
        [DataMember] public bool Motion = true;
        [DataMember] public bool OnTop = true;
    }

    internal sealed class Companion : Form
    {
        private readonly Bitmap character;
        private readonly Bitmap perched;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem visibilityItem;
        private readonly ToolStripMenuItem motionItem;
        private readonly ToolStripMenuItem topItem;
        private readonly System.Windows.Forms.Timer frames;
        private readonly System.Windows.Forms.Timer signals;
        private readonly EventWaitHandle restore;
        private readonly string settingsPath;
        private readonly bool testing;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Preferences preferences;
        private Bitmap frame;
        private Point anchor;
        private Point pointerStart;
        private Point anchorStart;
        private bool pressed;
        private bool dragging;
        private bool hidden;
        private double reactionStart = -10;
        private int clicks;
        private bool riding;
        private bool pendingClick;
        private Point pendingPoint;
        private long pendingTime;
        private bool hotkey;
        private double leanAngle;
        private readonly System.Windows.Forms.Timer clickDelay;
        private readonly ToolStripMenuItem rideItem;
        private const int PaddingSize = 36;
        private const int DismountId = 0x4361;
        internal bool TestFailed;

        internal Companion(string data, EventWaitHandle signal, bool smoke)
        {
            testing = smoke;
            restore = signal;
            settingsPath = Path.Combine(data, "settings.json");
            preferences = smoke ? new Preferences() : LoadPreferences();
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Character.png"))
            {
                if (image == null) throw new FileNotFoundException("The original Capri artwork is missing. Rebuild with assets/capri.png.");
                using (Bitmap original = new Bitmap(image)) character = new Bitmap(original);
            }
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Perch.png"))
            {
                if (image != null) using (Bitmap original = new Bitmap(image)) perched = new Bitmap(original);
            }
            Text = "Capri";
            AccessibleName = "Capri desktop companion";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = preferences.OnTop;
            Cursor = Cursors.Hand;
            AutoScaleMode = AutoScaleMode.None;
            menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new CapriMenuColors());
            menu.ForeColor = Color.FromArgb(238, 239, 241);
            menu.BackColor = Color.FromArgb(26, 27, 31);
            menu.ShowImageMargin = false;
            ToolStripMenuItem title = new ToolStripMenuItem("CAPRI  /  0.2.0");
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());
            visibilityItem = new ToolStripMenuItem("Hide Capri", null, delegate { SetHidden(!hidden); });
            menu.Items.Add(visibilityItem);
            ToolStripMenuItem sizes = new ToolStripMenuItem("Size");
            AddSize(sizes, "Small", 170);
            AddSize(sizes, "Medium", 240);
            AddSize(sizes, "Large", 320);
            menu.Items.Add(sizes);
            motionItem = new ToolStripMenuItem("Idle motion", null, delegate { preferences.Motion = !preferences.Motion; motionItem.Checked = preferences.Motion; UpdateAnimation(); SavePreferences(); });
            motionItem.Checked = preferences.Motion;
            menu.Items.Add(motionItem);
            topItem = new ToolStripMenuItem("Always on top", null, delegate { preferences.OnTop = !preferences.OnTop; TopMost = preferences.OnTop; topItem.Checked = preferences.OnTop; SavePreferences(); });
            topItem.Checked = preferences.OnTop;
            menu.Items.Add(topItem);
            rideItem = new ToolStripMenuItem("Perch on cursor", null, delegate { if (riding) StopRide(); else StartRide(); });
            menu.Items.Add(rideItem);
            clickDelay = new System.Windows.Forms.Timer();
            clickDelay.Interval = Math.Max(100, SystemInformation.DoubleClickTime);
            clickDelay.Tick += delegate { OnClickDelay(); };
            menu.Items.Add(new ToolStripMenuItem("Bring to this screen", null, delegate { RestoreAtCursor(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit Capri", null, delegate { Close(); }));
            tray = new NotifyIcon();
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Chip.ico"))
            {
                tray.Icon = icon != null ? new Icon(icon) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            tray.Text = "Capri · Double-click to restore";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreAtCursor(); };
            tray.Visible = !testing;
            frames = new System.Windows.Forms.Timer();
            frames.Interval = 33;
            frames.Tick += delegate { if (riding) FollowCursor(); RenderFrame(); if (!riding && !preferences.Motion && clock.Elapsed.TotalSeconds - reactionStart > 0.7) frames.Stop(); };
            signals = new System.Windows.Forms.Timer();
            signals.Interval = 250;
            signals.Tick += delegate { if (restore.WaitOne(0)) RestoreAtCursor(); };
            signals.Start();
            Size = CanvasSize(preferences.Size);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            anchor = preferences.HasPosition ? new Point(preferences.X, preferences.Y) : new Point(work.Right - Width - 28, work.Bottom - Height - 12);
            anchor = KeepOnScreen(anchor);
            Location = anchor;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
                return value;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RenderFrame();
            UpdateAnimation();
        }

        private static Size CanvasSize(int size) { return new Size(size + PaddingSize * 2, size + PaddingSize * 2); }

        private void AddSize(ToolStripMenuItem parent, string label, int size)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label, null, delegate { SetCharacterSize(size); });
            item.Tag = size;
            item.Checked = preferences.Size == size;
            parent.DropDownItems.Add(item);
        }

        private void SetCharacterSize(int size)
        {
            Point center = new Point(anchor.X + Width / 2, anchor.Y + Height / 2);
            preferences.Size = size;
            Size = CanvasSize(size);
            anchor = KeepOnScreen(new Point(center.X - Width / 2, center.Y - Height / 2));
            Location = anchor;
            foreach (ToolStripItem entry in menu.Items)
            {
                ToolStripMenuItem group = entry as ToolStripMenuItem;
                if (group == null || group.Text != "Size") continue;
                foreach (ToolStripMenuItem option in group.DropDownItems) option.Checked = (int)option.Tag == size;
            }
            RenderFrame();
            SavePreferences();
        }

        private Point KeepOnScreen(Point point)
        {
            Rectangle area = Screen.FromPoint(new Point(point.X + Width / 2, point.Y + Height / 2)).WorkingArea;
            return new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - Width)), Math.Max(area.Top, Math.Min(point.Y, area.Bottom - Height)));
        }

        private void DisplaysChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate { anchor = KeepOnScreen(anchor); Location = anchor; RenderFrame(); });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            dragging = false;
            pointerStart = Cursor.Position;
            anchorStart = anchor;
            Capture = true;
        }

        private void MovePointer(Point position)
        {
            if (!pressed) return;
            int dx = position.X - pointerStart.X;
            int dy = position.Y - pointerStart.Y;
            if (!dragging && dx * dx + dy * dy < 25) return;
            dragging = true;
            pendingClick = false;
            clickDelay.Stop();
            anchor = KeepOnScreen(new Point(anchorStart.X + dx, anchorStart.Y + dy));
            Location = anchor;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); MovePointer(Cursor.Position); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { menu.Show(Cursor.Position); return; }
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            Capture = false;
            if (dragging) { pendingClick = false; clickDelay.Stop(); SavePreferences(); }
            else if (pendingClick && IsDoubleClick(pendingPoint, Cursor.Position, clock.ElapsedMilliseconds - pendingTime)) { pendingClick = false; clickDelay.Stop(); StartRide(); }
            else { pendingClick = true; pendingPoint = Cursor.Position; pendingTime = clock.ElapsedMilliseconds; clickDelay.Stop(); clickDelay.Start(); }
            dragging = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) pressed = false; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && frame != null)
            {
                long value = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(value & 65535), (short)((value >> 16) & 65535)));
                if (!Solid(point))
                {
                    message.Result = new IntPtr(-1);
                    return;
                }
            }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == DismountId) { StopRide(); return; }
            if (message.Msg == 0x02E0) { anchor = KeepOnScreen(anchor); Location = anchor; RenderFrame(); }
            base.WndProc(ref message);
        }

        private bool Solid(Point point)
        {
            for (int dx = -4; dx <= 4; dx += 4)
                for (int dy = -4; dy <= 4; dy += 4)
                {
                    int x = point.X + dx;
                    int y = point.Y + dy;
                    if (x >= 0 && y >= 0 && x < frame.Width && y < frame.Height && frame.GetPixel(x, y).A >= 12) return true;
                }
            return false;
        }

        private void OnClickDelay()
        {
            clickDelay.Stop();
            if (!pendingClick) return;
            pendingClick = false;
            if (hidden || riding || dragging || pressed) return;
            React();
        }

        internal static bool IsDoubleClick(Point a, Point b, long elapsed)
        {
            Size box = SystemInformation.DoubleClickSize;
            return elapsed >= 0 && elapsed <= SystemInformation.DoubleClickTime && Math.Abs(a.X - b.X) <= box.Width / 2 && Math.Abs(a.Y - b.Y) <= box.Height / 2;
        }

        private void StartRide()
        {
            if (riding || hidden || !IsHandleCreated) return;
            pendingClick = false;
            clickDelay.Stop();
            pressed = false;
            dragging = false;
            Capture = false;
            hotkey = Native.RegisterHotKey(Handle, DismountId, 0x0001 | 0x0002 | 0x4000, 0x43);
            riding = true;
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x20);
            rideItem.Text = "Hop off";
            tray.Text = hotkey ? "Capri · Ctrl + Alt + C to hop off" : "Capri · Hop off from this menu";
            if (!hotkey && !testing) tray.ShowBalloonTip(5000, "Capri", "Ctrl + Alt + C is taken by another app. Right-click Capri's tray icon and choose Hop off.", ToolTipIcon.Info);
            frames.Start();
        }

        private void StopRide()
        {
            if (hotkey && IsHandleCreated) Native.UnregisterHotKey(Handle, DismountId);
            hotkey = false;
            if (!riding) return;
            riding = false;
            leanAngle = 0;
            if (IsHandleCreated) Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) & ~0x20);
            if (rideItem != null) rideItem.Text = "Perch on cursor";
            if (tray != null) tray.Text = "Capri · Double-click to restore";
            if (!IsDisposed) { Location = anchor; UpdateAnimation(); SavePreferences(); }
        }

        private void FollowCursor()
        {
            Point cursor = Cursor.Position;
            Rectangle area = Screen.FromPoint(cursor).WorkingArea;
            float seatedHeight = perched == null ? preferences.Size : preferences.Size * perched.Height / (float)perched.Width;
            int seatX = perched == null ? Width / 2 - 24 : PaddingSize + (int)(preferences.Size * 0.267f) - 12;
            int seatY = perched == null ? Height - PaddingSize : PaddingSize + (int)((preferences.Size - seatedHeight) / 2 + seatedHeight * 0.73f) + 12;
            int tx = Math.Max(area.Left, Math.Min(cursor.X - seatX, area.Right - Width));
            int ty = Math.Max(area.Top, Math.Min(cursor.Y - seatY, area.Bottom - Height));
            int nx = anchor.X + (int)Math.Round((tx - anchor.X) * 0.25);
            int ny = anchor.Y + (int)Math.Round((ty - anchor.Y) * 0.25);
            double velocity = Math.Max(-8, Math.Min(8, (nx - anchor.X) * 0.4));
            leanAngle = leanAngle * 0.8 + velocity * 0.2;
            anchor = new Point(Math.Max(area.Left, Math.Min(nx, area.Right - Width)), Math.Max(area.Top, Math.Min(ny, area.Bottom - Height)));
        }

        private void React()
        {
            clicks++;
            reactionStart = clock.Elapsed.TotalSeconds;
            if (!hidden) frames.Start();
            RenderFrame();
        }

        private void SetHidden(bool value)
        {
            hidden = value;
            visibilityItem.Text = value ? "Show Capri" : "Hide Capri";
            if (value) { StopRide(); pendingClick = false; clickDelay.Stop(); frames.Stop(); Hide(); }
            else { anchor = KeepOnScreen(anchor); Location = anchor; Show(); RenderFrame(); UpdateAnimation(); }
        }

        private void RestoreAtCursor()
        {
            StopRide();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            anchor = new Point(area.Right - Width - 28, area.Bottom - Height - 12);
            SetHidden(false);
            SavePreferences();
        }

        private void UpdateAnimation() { if ((preferences.Motion || riding) && !hidden) frames.Start(); else frames.Stop(); RenderFrame(); }

        private void RenderFrame()
        {
            if (!IsHandleCreated || hidden || IsDisposed) return;
            Bitmap next = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(next))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                double now = clock.Elapsed.TotalSeconds;
                double age = now - reactionStart;
                float bounce = age >= 0 && age < 0.65 ? (float)(Math.Sin(age / 0.65 * Math.PI) * 15) : 0;
                float breathe = preferences.Motion && !dragging ? (float)Math.Sin(now * 1.75) * 2.2f : 0;
                float tilt = preferences.Motion && !dragging ? (float)Math.Sin(now * 0.95) * 0.65f : 0;
                graphics.TranslateTransform(Width / 2f, Height / 2f - bounce + breathe);
                graphics.RotateTransform(tilt + (float)leanAngle);
                Bitmap artwork = riding && perched != null ? perched : character;
                float height = preferences.Size * artwork.Height / (float)artwork.Width;
                graphics.DrawImage(artwork, new RectangleF(-preferences.Size / 2f, -height / 2f, preferences.Size, height));
            }
            Native.DrawLayered(Handle, next, anchor);
            Bitmap old = frame;
            frame = next;
            if (old != null) old.Dispose();
        }

        private Preferences LoadPreferences()
        {
            try
            {
                using (FileStream stream = File.OpenRead(settingsPath))
                {
                    Preferences value = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject(stream);
                    if (value.Size != 170 && value.Size != 240 && value.Size != 320) value.Size = 240;
                    return value;
                }
            }
            catch { return new Preferences(); }
        }

        private void SavePreferences()
        {
            preferences.X = anchor.X;
            preferences.Y = anchor.Y;
            preferences.HasPosition = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                string temporary = settingsPath + ".new";
                using (FileStream stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(Preferences)).WriteObject(stream, preferences);
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null); else File.Move(temporary, settingsPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            StopRide();
            clickDelay.Stop();
            frames.Stop();
            signals.Stop();
            tray.Visible = false;
            SavePreferences();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clickDelay.Dispose();
                frames.Dispose();
                signals.Dispose();
                tray.Dispose();
                menu.Dispose();
                character.Dispose();
                if (perched != null) perched.Dispose();
                if (frame != null) frame.Dispose();
            }
            base.Dispose(disposing);
        }

        internal void StartSmokeTest(string output)
        {
            Directory.CreateDirectory(output);
            Shown += delegate
            {
                System.Windows.Forms.Timer test = new System.Windows.Forms.Timer();
                test.Interval = 500;
                int step = 0;
                Form backdrop = null;
                System.Collections.Generic.List<string> checks = new System.Collections.Generic.List<string>();
                test.Tick += delegate
                {
                    try
                    {
                        if (step == 0)
                        {
                            checks.Add("PASS native window visible: " + Visible);
                            if (!Visible || frame == null) throw new Exception("Native window did not render.");
                            if ((Native.GetWindowLong(Handle, -20) & 0x00080000) == 0) throw new Exception("Window is not layered.");
                            if (frame.GetPixel(0, 0).A != 0) throw new Exception("Frame corner is opaque.");
                            checks.Add("PASS per-pixel alpha and layered window");
                            Point before = anchor;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            Point moved = new Point(pointerStart.X - 80, pointerStart.Y - 35);
                            MovePointer(moved);
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (anchor == before || pendingClick || riding) throw new Exception("Drag did not change position or did not cancel click.");
                            checks.Add("PASS drag handler changed position");
                            int previous = clicks;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!pendingClick || clicks != previous) throw new Exception("Single click was not delayed.");
                            OnClickDelay();
                            if (clicks != previous + 1 || !frames.Enabled) throw new Exception("Click reaction did not start.");
                            checks.Add("PASS delayed single click started reaction");
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!riding || (Native.GetWindowLong(Handle, -20) & 0x20) == 0) throw new Exception("Double click did not perch with click-through.");
                            checks.Add("PASS double click perched with click-through");
                            checks.Add(hotkey ? "PASS Ctrl+Alt+C registered while perched" : "LIMIT Ctrl+Alt+C taken; tray Hop off fallback");
                            if (IsDoubleClick(Point.Empty, new Point(200, 200), 10)) throw new Exception("Double click predicate failed.");
                            Message key = Message.Create(Handle, 0x0312, new IntPtr(DismountId), IntPtr.Zero);
                            WndProc(ref key);
                            if (riding || hotkey || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hotkey did not hop off.");
                            checks.Add("PASS Ctrl+Alt+C message hopped off and released the hotkey");
                            rideItem.PerformClick();
                            if (!riding || rideItem.Text != "Hop off") throw new Exception("Menu perch failed.");
                            rideItem.PerformClick();
                            if (riding || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hop off menu did not clean up.");
                            checks.Add("PASS tray Perch on cursor and Hop off");
                            rideItem.PerformClick();
                            visibilityItem.PerformClick();
                            if (Visible || !hidden || riding || hotkey) throw new Exception("Hide menu did not hide window or clean up perch.");
                            checks.Add(perched == null ? "LIMIT seated artwork unavailable" : "PASS seated artwork loaded");
                            checks.Add("PASS hide menu");
                            restore.Set();
                        }
                        else if (step == 1)
                        {
                            if (!Visible || hidden) throw new Exception("Second-launch restore signal did not show Capri.");
                            checks.Add("PASS restore signal");
                            Point bounded = KeepOnScreen(new Point(int.MaxValue / 4, int.MaxValue / 4));
                            if (!Screen.FromPoint(new Point(bounded.X + Width / 2, bounded.Y + Height / 2)).WorkingArea.Contains(new Rectangle(bounded, Size))) throw new Exception("Screen bounds failed.");
                            checks.Add("PASS screen bounds");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[3]).DropDownItems[0]).PerformClick();
                            if (Width != 242) throw new Exception("Small size failed.");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[3]).DropDownItems[1]).PerformClick();
                            checks.Add("PASS size menu");
                            motionItem.PerformClick();
                            if (frames.Enabled) throw new Exception("Motion toggle did not stop idle timer.");
                            checks.Add("PASS motion pause");
                            SavePreferences();
                            if (LoadPreferences().Size != 240) throw new Exception("Settings did not round-trip.");
                            checks.Add("PASS settings round-trip in isolated test directory");
                            backdrop = new Form();
                            backdrop.Text = "Capri clean test desktop";
                            backdrop.FormBorderStyle = FormBorderStyle.None;
                            backdrop.ShowInTaskbar = false;
                            backdrop.StartPosition = FormStartPosition.Manual;
                            Rectangle area = Screen.PrimaryScreen.WorkingArea;
                            backdrop.Bounds = new Rectangle(area.Left + Math.Max(0, (area.Width - 920) / 2), area.Top + Math.Max(0, (area.Height - 560) / 2), Math.Min(920, area.Width), Math.Min(560, area.Height));
                            backdrop.BackColor = Color.FromArgb(237, 239, 242);
                            backdrop.TopMost = true;
                            backdrop.Paint += delegate(object sender, PaintEventArgs paint)
                            {
                                using (Pen line = new Pen(Color.FromArgb(225, 228, 232)))
                                    for (int x = 32; x < backdrop.Width; x += 32) for (int y = 32; y < backdrop.Height; y += 32) paint.Graphics.DrawEllipse(line, x, y, 1, 1);
                                using (Font label = new Font("Segoe UI", 10))
                                using (Brush ink = new SolidBrush(Color.FromArgb(110, 116, 125)))
                                    paint.Graphics.DrawString("CAPRI  /  WINDOWS DESKTOP COMPANION", label, ink, 32, 28);
                                using (Brush bar = new SolidBrush(Color.FromArgb(249, 250, 251))) paint.Graphics.FillRectangle(bar, 0, backdrop.Height - 42, backdrop.Width, 42);
                                using (Font label = new Font("Segoe UI", 9))
                                using (Brush ink = new SolidBrush(Color.FromArgb(126, 131, 139)))
                                    paint.Graphics.DrawString("Clean test desktop · actual native app", label, ink, 24, backdrop.Height - 29);
                            };
                            backdrop.Show();
                            anchor = new Point(backdrop.Left + backdrop.Width / 2 - Width / 2 + 150, backdrop.Top + backdrop.Height / 2 - Height / 2 + 20);
                            Location = anchor;
                            TopMost = false;
                            TopMost = true;
                            RenderFrame();
                        }
                        else if (step == 2)
                        {
                            using (Bitmap capture = new Bitmap(backdrop.Width, backdrop.Height))
                            using (Graphics graphics = Graphics.FromImage(capture))
                            {
                                graphics.CopyFromScreen(backdrop.Location, Point.Empty, backdrop.Size);
                                capture.Save(Path.Combine(output, "desktop.png"), ImageFormat.Png);
                            }
                            checks.Add("PASS actual native app captured over app-owned neutral test backdrop");
                            checks.Add("LIMIT handler-driven integration test; physical mouse input and tray UI require a manual check");
                            checks.Add("PASS quit menu requested application exit");
                            File.WriteAllLines(Path.Combine(output, "smoke-test.txt"), checks);
                            test.Stop();
                            test.Dispose();
                            backdrop.Close();
                            ((ToolStripMenuItem)menu.Items[menu.Items.Count - 1]).PerformClick();
                        }
                        step++;
                    }
                    catch (Exception error)
                    {
                        TestFailed = true;
                        File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
                        test.Stop();
                        test.Dispose();
                        if (backdrop != null) backdrop.Close();
                        Close();
                    }
                };
                test.Start();
            };
        }
    }

    internal sealed class CapriMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(26, 27, 31); } }
        public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(65, 69, 53); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(167, 245, 88); } }
        public override Color CheckBackground { get { return Color.FromArgb(65, 69, 53); } }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; internal Size(int width, int height) { Width = width; Height = height; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point origin, int colorKey, ref Blend blend, int flags);

        internal static void DrawLayered(IntPtr window, Bitmap bitmap, System.Drawing.Point location)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr nativeBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = SelectObject(memory, nativeBitmap);
            try
            {
                Point destination = new Point(location.X, location.Y);
                Point origin = new Point(0, 0);
                Size size = new Size(bitmap.Width, bitmap.Height);
                Blend blend = new Blend { Operation = 0, Flags = 0, Alpha = 255, Format = 1 };
                if (!UpdateLayeredWindow(window, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SelectObject(memory, previous);
                DeleteObject(nativeBitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
