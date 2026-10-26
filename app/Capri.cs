using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
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
                if (!first) { Native.AllowSetForegroundWindow(-1); restore.Set(); return 0; }
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
        [DataMember] public long UpdateCheckedAt;
        [DataMember] public string Model;
        [DataMember] public bool Welcomed;
        [DataMember] public bool NoSitting;
        [DataMember] public string VoiceKey;
        [DataMember] public string VoiceId;
        [DataMember] public string VoiceModel;
        [DataMember] public string AgentId;
        [DataMember] public int AgentVersion;
        [DataMember] public string Mic;
        [DataMember] public string TextKey;
        [DataMember] public string TextModel;
        [DataMember] public bool Watch;
        [DataMember] public int GlanceGap;
        [DataMember] public bool Quiet;
        [DataMember] public bool Wander;
        [DataMember] public bool FreeVoice;
        [DataMember] public string VoiceStyle;
    }

    [DataContract]
    internal sealed class Release
    {
        [DataMember(Name = "tag_name")] public string Tag;
        [DataMember(Name = "html_url")] public string Page;
        [DataMember(Name = "draft")] public bool Draft;
        [DataMember(Name = "prerelease")] public bool Prerelease;
    }

    internal sealed class Clip
    {
        internal readonly Bitmap Strip;
        internal readonly int Count;
        internal readonly double Fps;
        internal readonly float Height;

        internal Clip(string name, int count, double fps, float height)
        {
            Count = count;
            Fps = fps;
            Height = height;
            Strip = Companion.LoadArt("Capri.Sprite." + name + ".png");
            if (Strip == null) throw new FileNotFoundException("Capri's " + name + " animation is missing. Rebuild with assets/sprites.");
        }
    }

    internal sealed class Companion : Form
    {
        private readonly Clip idle = new Clip("idle", 16, 8, 1.11f);
        private readonly Clip blink = new Clip("blink", 4, 8, 1.11f);
        private readonly Clip blinkGlitch = new Clip("blink_glitch", 4, 8, 1.11f);
        private readonly Clip grin = new Clip("grin", 16, 8, 1.11f);
        private readonly Clip glitch = new Clip("glitch", 10, 15, 1.11f);
        private readonly Clip dangle = new Clip("dangle", 24, 10, 1.11f);
        private readonly Clip sleep = new Clip("sleep", 16, 6, 1.11f);
        private readonly Clip wake = new Clip("wake", 13, 15, 1.11f);
        private readonly Clip perch = new Clip("perch", 16, 8, 1);
        private readonly Clip perchBlink = new Clip("perch_blink", 4, 8, 1);
        private readonly Clip perchBlinkGlitch = new Clip("perch_blink_glitch", 4, 8, 1);
        private readonly Clip perchGlitch = new Clip("perch_glitch", 10, 15, 1);
        private readonly Clip mouth = new Clip("mouth", 5, 12, 1.11f);
        private readonly Random random = new Random();
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem visibilityItem;
        private readonly ToolStripMenuItem wanderItem;
        private double nextWander = -1;
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
        private Clip clip;
        private double clipStart;
        private int idleCycle;
        private Clip blinkWith;
        private IntPtr lastForeground;
        private int topTicks;
        private double nextGlitch;
        private int clicks;
        private bool riding;
        private bool pendingClick;
        private Point pendingPoint;
        private long pendingTime;
        private bool hotkey;
        private double leanAngle;
        private Ledge seat;
        private bool falling;
        private Ledge fallTo;
        private double fallSpeed;
        private double fallStep;
        private double coveredSince = -1;
        private double seatLost = -1;
        private System.Collections.Generic.List<Rectangle> icons = new System.Collections.Generic.List<Rectangle>();
        private bool iconsBusy;
        private double iconsAt = -10;
        private const float SitX = 0.30f;
        private const float SitY = 0.79f;
        private static readonly RectangleF Body = RectangleF.FromLTRB(0.25f, 0f, 0.76f, 1f);
        private static readonly RectangleF PerchedBody = RectangleF.FromLTRB(0.21f, 0.17f, 0.69f, 0.98f);
        private readonly System.Windows.Forms.Timer clickDelay;
        private readonly ToolStripMenuItem rideItem;
        private readonly ToolStripMenuItem sitItem;
        private readonly ToolStripMenuItem sitHereItem;
        private readonly ToolStripMenuItem hangHereItem;
        private bool hanging;
        private readonly ToolStripMenuItem updateItem;
        private readonly ToolStripMenuItem helpItem;
        private bool updating;
        private string updatePage;
        private string balloonPage;
        private string speech;
        private double speechStart;
        private const double LettersPerSecond = 28;
        private const double MouthRate = 12;
        private readonly Brain brain = new Brain { Act = Hands.Do };
        private System.Collections.Generic.List<string> cloudModels;
        private readonly TalkPill pill = new TalkPill();
        private readonly SpeechBox box = new SpeechBox();
        private readonly TypeBox typer = new TypeBox();
        private readonly Mainframe mainframe;
        private readonly Engine engine;
        private Machine machine;
        private Pick fit;
        private bool ollamaRunning;
        internal bool SettingUp;
        private TalkState talkState;
        private string heard;
        private string reply;
        private double replyStart;
        private double replyDone = -1;
        private double pillUntil = 10;
        private int asking;
        private bool finding;
        internal Func<string, string> Think;
        internal Func<string, byte[]> Speak;
        private readonly Voice voice = new Voice();
        private readonly LocalVoice kokoro;
        internal bool SettingUpVoice;
        private readonly Ears ears = new Ears();
        private readonly Speaker speaker;
        private volatile Agent agent;
        private bool chatting;
        private bool dropping;
        private int chatTicket;
        private const double WakeLevel = 0.02;
        private readonly object waking = new object();
        private readonly System.Collections.Generic.List<short[]> preroll = new System.Collections.Generic.List<short[]>();
        private System.Collections.Generic.List<short[]> backlog;
        private bool resting;
        private int loudRun;
        private readonly Whisper whisper;
        private volatile bool local;
        private volatile bool earsShut;
        private volatile bool transcribing;
        private volatile bool fetchingEars;
        private System.Collections.Generic.List<short[]> utterance;
        private int quietRun;
        private double voiceUntil;
        private bool spoke;
        private bool checkedIn;
        private const string GlanceLine = "(You glance at their screen while they do their thing. React in one short line, in character, to something specific you can see right now: tease them, cheer them on, or ask about it. If it's only a desktop, a menu, a loading screen or nothing much is happening, reply with just SKIP.)";
        internal static Func<Rectangle, Bitmap> Grab = GrabScreen;
        private double nextGlance = -1;
        private bool glancing;
        private byte[] lastPrint;
        private volatile bool fetchingEyes;
        private double thinkingSince = double.NaN;
        private bool slowWarned;
        private double quietSince = -1;
        private double wokeAt = -1;
        private VoiceListing voices;
        private int[] mouths;
        private double pace = LettersPerSecond;
        private const string ReleasesPage = "https://github.com/yerocsin42/capri-desktop/releases/";
        private const string OllamaPage = "https://ollama.com/download";
        private const string FreeKeyPage = "https://build.nvidia.com/settings/api-keys";
        private const string GuidePage = "https://yerocsin42.github.io/capri-desktop/setup/#voice";
        private const string DiscordPage = "https://discord.gg/F4fDHfGtFk";
        private const string GuideLine = "Follow the guide I just opened. Then paste your ElevenLabs key in the box under VOICE, press Connect and pick my voice in the Voice ID box." + Brain.Stuck;
        private const string FreeKeyLine = "Sign in on that page, press Generate API Key and copy it. Then paste it in the box under BRAIN and press Connect. It's free." + Brain.Stuck;
        internal static Action<string> Browse = delegate(string page) { try { Process.Start(page); } catch (System.ComponentModel.Win32Exception) { } };
        private const int PaddingSize = 36;
        private const int DismountId = 0x4361;
        private const int TalkKeyId = 0x4356;
        private const int MuteKeyId = 0x434D;
        private bool talkKey;
        private bool muteKey;
        private const int BlinkFrom = 9;
        internal bool TestFailed;

        internal Companion(string data, EventWaitHandle signal, bool smoke)
        {
            testing = smoke;
            speaker = new Speaker(smoke);
            restore = signal;
            settingsPath = Path.Combine(data, "settings.json");
            Brain.LogPath = Path.Combine(data, "errors.txt");
            engine = new Engine(data);
            kokoro = new LocalVoice(data);
            whisper = new Whisper(data);
            preferences = smoke ? new Preferences() : LoadPreferences();
            ears.Chosen = preferences.Mic;
            kokoro.Style = LocalVoice.Find(preferences.VoiceStyle);
            clip = idle;
            nextGlitch = 30 + random.NextDouble() * 30;
            Text = "Capri";
            AccessibleName = "Capri desktop companion";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = preferences.OnTop;
            Layer();
            Cursor = Look.Pointer;
            AutoScaleMode = AutoScaleMode.None;
            menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new CapriMenuColors());
            menu.ForeColor = Color.FromArgb(238, 239, 241);
            menu.BackColor = Color.FromArgb(26, 27, 31);
            menu.ShowImageMargin = false;
            ToolStripMenuItem title = new ToolStripMenuItem("CAPRI  /  " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open Capri Mainframe", null, delegate { mainframe.Restore(); }));
            visibilityItem = new ToolStripMenuItem("Hide Capri", null, delegate { SetHidden(!hidden); });
            menu.Items.Add(visibilityItem);
            ToolStripMenuItem sizes = new ToolStripMenuItem("Size");
            AddSize(sizes, "Small", 170);
            AddSize(sizes, "Medium", 240);
            AddSize(sizes, "Large", 320);
            AddSize(sizes, "Huge", 440);
            menu.Items.Add(sizes);
            ToolStripMenuItem moves = new ToolStripMenuItem("Move");
            wanderItem = new ToolStripMenuItem("Wander around", null, delegate { ToggleWander(); });
            wanderItem.Checked = preferences.Wander;
            moves.DropDownItems.Add(wanderItem);
            rideItem = new ToolStripMenuItem("Perch on cursor", null, delegate { if (riding) StopRide(); else StartRide(); });
            moves.DropDownItems.Add(rideItem);
            sitItem = new ToolStripMenuItem("Sit on windows", null, delegate { ToggleSitting(); });
            sitItem.Checked = !preferences.NoSitting;
            moves.DropDownItems.Add(sitItem);
            sitHereItem = new ToolStripMenuItem("Sit here", null, delegate { if (seat != null && seat.Free) StandUp(); else SitHere(); });
            moves.DropDownItems.Add(sitHereItem);
            hangHereItem = new ToolStripMenuItem("Hang here", null, delegate { if (hanging) LetGo(); else HangHere(); });
            moves.DropDownItems.Add(hangHereItem);
            moves.DropDownItems.Add(new ToolStripMenuItem("Bring to this screen", null, delegate { RestoreAtCursor(); }));
            menu.Items.Add(moves);
            menu.Opening += delegate { sitHereItem.Text = seat != null && seat.Free ? "Stand up" : "Sit here"; hangHereItem.Text = hanging ? "Let go" : "Hang here"; };
            clickDelay = new System.Windows.Forms.Timer();
            clickDelay.Interval = Math.Max(100, SystemInformation.DoubleClickTime);
            clickDelay.Tick += delegate { OnClickDelay(); };
            helpItem = new ToolStripMenuItem("Help");
            helpItem.DropDownItems.Add(new ToolStripMenuItem("Join the Discord", null, delegate { OpenPage(DiscordPage); }));
            helpItem.DropDownItems.Add(new ToolStripMenuItem("Copy error details", null, delegate { CopyErrors(); }));
            updateItem = new ToolStripMenuItem("Check for updates", null, delegate { if (updatePage != null) OpenPage(updatePage); else CheckForUpdates(true); });
            helpItem.DropDownItems.Add(updateItem);
            menu.Items.Add(helpItem);
            foreach (ToolStripMenuItem parent in new[] { sizes, moves, helpItem })
            {
                ToolStripDropDownMenu drop = (ToolStripDropDownMenu)parent.DropDown;
                drop.Renderer = menu.Renderer;
                drop.ForeColor = menu.ForeColor;
                drop.BackColor = menu.BackColor;
                drop.ShowImageMargin = false;
                drop.ShowCheckMargin = parent != helpItem;
            }
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
            tray.BalloonTipClicked += delegate { if (balloonPage != null) OpenPage(balloonPage); };
            tray.Visible = !testing;
            mainframe = new Mainframe(this, tray.Icon, Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            voice.Key = Voice.Unprotect(preferences.VoiceKey);
            Hands.Look = LookAt;
            string textKey = Voice.Unprotect(preferences.TextKey);
            if (textKey != null) brain.Remote = new Cloud(textKey, preferences.TextModel);
            mainframe.ShowTextKey(textKey == null ? null : Voice.Tail(textKey));
            voice.VoiceId = string.IsNullOrEmpty(preferences.VoiceId) ? null : preferences.VoiceId;
            voice.Model = string.IsNullOrEmpty(preferences.VoiceModel) ? null : preferences.VoiceModel;
            ShowVoice();
            mainframe.ShowMic(MicName(preferences.Mic));
            pill.Typing += OpenTyper;
            pill.Voice += ToggleChat;
            pill.Mute += ToggleMic;
            ears.Heard += Listen;
            ears.Level += delegate(double value) { Post(delegate { pill.SetLevel(value); }); };
            box.Dismissed += delegate { if (box.Leaving < 0) box.Leaving = clock.Elapsed.TotalSeconds; };
            typer.Submitted += Hear;
            typer.VisibleChanged += delegate { if (!typer.Visible && replyDone >= 0) replyDone = clock.Elapsed.TotalSeconds; };
            frames = new System.Windows.Forms.Timer();
            frames.Interval = 33;
            frames.Tick += delegate { if (riding) FollowCursor(); else if (falling) Tumble(clock.Elapsed.TotalSeconds); else if (seat != null) HoldSeat(clock.Elapsed.TotalSeconds); RenderFrame(); if (!riding && !dragging && !preferences.Motion && clip == idle && speech == null && talkState == TalkState.Off) frames.Stop(); };
            signals = new System.Windows.Forms.Timer();
            signals.Interval = 250;
            signals.Tick += delegate
            {
                if (restore.WaitOne(0)) { RestoreAtCursor(); mainframe.Restore(); }
                if (!testing) CheckIdle(Native.IdleMilliseconds());
                if (!testing) CheckGlance(clock.Elapsed.TotalSeconds, Native.IdleMilliseconds());
                if (!testing) CheckWander(clock.Elapsed.TotalSeconds, Native.IdleMilliseconds());
                StayOnTop();
                CheckSeat(clock.Elapsed.TotalSeconds);
                CheckChat(clock.Elapsed.TotalSeconds);
                earsShut = local && (transcribing || talkState == TalkState.Thinking || clock.Elapsed.TotalSeconds < voiceUntil);
                CheckSlow(clock.Elapsed.TotalSeconds);
                UpdatePill(clock.Elapsed.TotalSeconds);
                if (!testing && clock.Elapsed.TotalSeconds > 20 && UpdateDue(preferences.UpdateCheckedAt, DateTime.UtcNow.Ticks)) CheckForUpdates(false);
            };
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
            Play(grin);
            RenderFrame();
            UpdateAnimation();
            if (testing || preferences.Welcomed)
            {
                mainframe.WindowState = FormWindowState.Minimized;
                mainframe.Show();
            }
            else
            {
                mainframe.Restore();
                preferences.Welcomed = true;
                SavePreferences();
            }
            if (!testing) FindBrain();
            if (!testing && voice.Key != null) ListVoices(voice.Key, false);
            if (!testing) talkKey = Native.RegisterHotKey(Handle, TalkKeyId, 0x0001 | 0x0002 | 0x4000, 0x56);
            bool muteFree = !testing && Native.RegisterHotKey(Handle, MuteKeyId, 0x0001 | 0x0002 | 0x4000, 0x4E) && Native.UnregisterHotKey(Handle, MuteKeyId);
            mainframe.ShowShortcut(talkKey, muteFree);
            RefreshIcons();
        }

        internal static Bitmap LoadArt(string name)
        {
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (image == null) return null;
                using (Bitmap original = new Bitmap(image))
                {
                    Bitmap copy = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics graphics = Graphics.FromImage(copy))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImage(original, new Rectangle(0, 0, copy.Width, copy.Height));
                    }
                    return copy;
                }
            }
        }

        private void Play(Clip next)
        {
            clip = next;
            clipStart = clock.Elapsed.TotalSeconds;
            idleCycle = 0;
            blinkWith = null;
        }

        private int Advance(double now)
        {
            bool seated = clip == perch || clip == perchGlitch;
            bool perched = riding || seat != null;
            if (perched && !seated) Play(perch);
            if ((!perched && seated) || (clip == dangle && !dragging && !falling && !hanging)) Play(idle);
            int index = (int)((now - clipStart) * clip.Fps);
            if ((clip == glitch && index >= glitch.Count) || (clip == wake && index >= wake.Count) || (clip == grin && index >= grin.Count * 2)) { Play(idle); index = 0; }
            if (clip == perchGlitch && index >= perchGlitch.Count) { Play(perch); index = 0; }
            if (clip != idle && clip != perch) return index % clip.Count;
            if (!preferences.Motion) return 0;
            int cycle = index / clip.Count;
            if (cycle != idleCycle)
            {
                idleCycle = cycle;
                double roll = random.NextDouble();
                blinkWith = roll < 0.1 ? (clip == perch ? perchBlinkGlitch : blinkGlitch) : roll < 0.55 ? (clip == perch ? perchBlink : blink) : null;
                if (now >= nextGlitch && speech == null && talkState == TalkState.Off) { nextGlitch = now + 30 + random.NextDouble() * 30; Play(clip == perch ? perchGlitch : glitch); return 0; }
            }
            return index % clip.Count;
        }

        private void StayOnTop()
        {
            if (!preferences.OnTop || hidden || !IsHandleCreated || menu.Visible) return;
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == lastForeground && ++topTicks < 8) return;
            lastForeground = foreground;
            topTicks = 0;
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            foreach (Form above in new Form[] { pill, box, typer }) if (above.Visible) Native.SetWindowPos(above.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        internal static bool UpdateDue(long checkedAt, long now) { return now - checkedAt >= TimeSpan.TicksPerDay || now < checkedAt; }

        private void CheckForUpdates(bool asked)
        {
            if (updating) return;
            updating = true;
            preferences.UpdateCheckedAt = DateTime.UtcNow.Ticks;
            SavePreferences();
            if (asked) updateItem.Text = "Checking for updates...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                Release found = null;
                try { found = FetchLatestRelease(); } catch { }
                try { if (!IsDisposed) BeginInvoke((Action)delegate { ShowUpdate(found, asked); }); } catch (InvalidOperationException) { }
            });
        }

        internal static Release FetchLatestRelease()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/yerocsin42/capri-desktop/releases/latest");
            request.UserAgent = "Capri-Desktop";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            using (WebResponse response = request.GetResponse())
            using (Stream body = response.GetResponseStream()) return ParseRelease(body);
        }

        internal static Release ParseRelease(Stream body) { return (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(body); }

        internal static bool IsNewer(string tag, Version current)
        {
            Version found;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out found)) return false;
            return new Version(found.Major, found.Minor, Math.Max(0, found.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        }

        private void ShowUpdate(Release found, bool asked)
        {
            updating = false;
            Version current = Assembly.GetExecutingAssembly().GetName().Version;
            bool usable = found != null && !found.Draft && !found.Prerelease && found.Page != null && found.Page.StartsWith(ReleasesPage, StringComparison.Ordinal);
            if (usable && IsNewer(found.Tag, current))
            {
                string version = found.Tag.TrimStart('v', 'V');
                updatePage = found.Page;
                updateItem.Text = "Download Capri " + version;
                if (!menu.Items.Contains(updateItem)) menu.Items.Insert(menu.Items.Count - 2, updateItem);
                balloonPage = found.Page;
                tray.ShowBalloonTip(8000, "Capri " + version + " is out", "Click here to download the update.", ToolTipIcon.Info);
                return;
            }
            updateItem.Text = "Check for updates";
            if (!usable) { preferences.UpdateCheckedAt = DateTime.UtcNow.Ticks - TimeSpan.TicksPerDay + TimeSpan.TicksPerHour; SavePreferences(); }
            if (!asked) return;
            balloonPage = null;
            tray.ShowBalloonTip(4000, "Capri", usable ? "You're on the latest version (" + current.ToString(3) + ")." : "Couldn't check for updates right now. Try again later.", ToolTipIcon.Info);
        }

        private static void OpenPage(string page)
        {
            if (!page.StartsWith(ReleasesPage, StringComparison.Ordinal) && page != OllamaPage && page != FreeKeyPage && page != GuidePage && page != DiscordPage) return;
            Browse(page);
        }

        private void CheckIdle(long quiet)
        {
            if (clip == sleep) { if (quiet < 1000) Play(wake); return; }
            if (quiet >= 120000 && clip == idle && preferences.Motion && !riding && !dragging && !hidden && !chatting && speech == null && talkState == TalkState.Off && !typer.Visible) Play(sleep);
        }

        private static Size CanvasSize(int size) { return new Size(size + PaddingSize * 2, size + PaddingSize * 2); }

        private void AddSize(ToolStripMenuItem parent, string label, int size)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label, null, delegate { SetCharacterSize(size); });
            item.Tag = size;
            item.Checked = preferences.Size == size;
            parent.DropDownItems.Add(item);
        }

        internal void SetCharacterSize(int size)
        {
            Point center = new Point(anchor.X + Width / 2, anchor.Y + Height / 2);
            int hipX = Hip().X;
            preferences.Size = size;
            Size = CanvasSize(size);
            anchor = seat != null ? Seated(hipX, seat.Y) : KeepOnScreen(new Point(center.X - Width / 2, center.Y - Height / 2));
            Location = anchor;
            foreach (ToolStripItem entry in menu.Items)
            {
                ToolStripMenuItem group = entry as ToolStripMenuItem;
                if (group == null || group.Text != "Size") continue;
                foreach (ToolStripMenuItem option in group.DropDownItems) option.Checked = (int)option.Tag == size;
            }
            RenderFrame();
            SavePreferences();
            mainframe.Sync();
        }

        private Point KeepOnScreen(Point point) { return Within(point, Screen.FromPoint(new Point(point.X + Width / 2, point.Y + Height / 2)).WorkingArea, Body); }

        private Point Within(Point point, Rectangle area, RectangleF body)
        {
            int size = preferences.Size;
            int left = area.Left - PaddingSize - (int)(size * body.Left);
            int right = area.Right - PaddingSize - (int)Math.Ceiling(size * body.Right);
            int top = area.Top - PaddingSize - (int)(size * body.Top);
            int bottom = area.Bottom - PaddingSize - (int)Math.Ceiling(size * body.Bottom);
            return new Point(Math.Max(left, Math.Min(point.X, right)), Math.Max(top, Math.Min(point.Y, bottom)));
        }

        private Rectangle BodyOnScreen(Point point, RectangleF body)
        {
            int size = preferences.Size;
            return Rectangle.FromLTRB(point.X + PaddingSize + (int)(size * body.Left), point.Y + PaddingSize + (int)(size * body.Top), point.X + PaddingSize + (int)Math.Ceiling(size * body.Right), point.Y + PaddingSize + (int)Math.Ceiling(size * body.Bottom));
        }

        private void DisplaysChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate { if (seat != null) StandUp(); anchor = KeepOnScreen(anchor); Location = anchor; RenderFrame(); });
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
            if (!dragging) { seat = null; falling = false; hanging = false; Play(dangle); frames.Start(); RefreshIcons(); }
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
            if (dragging)
            {
                pendingClick = false;
                clickDelay.Stop();
                if ((ModifierKeys & Keys.Control) != 0) HangHere();
                else if ((ModifierKeys & Keys.Shift) != 0) SitHere();
                else if (!Drop()) SavePreferences();
            }
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
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == TalkKeyId) { if (hidden) SetHidden(false); ToggleChat(); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == MuteKeyId) { ToggleMic(); return; }
            if (message.Msg == 0x02E0) { anchor = KeepOnScreen(anchor); Location = anchor; RenderFrame(); }
            base.WndProc(ref message);
        }

        private bool Solid(Point point, bool uncovered = false)
        {
            for (int dx = -4; dx <= 4; dx += 4)
                for (int dy = -4; dy <= 4; dy += 4)
                {
                    int x = point.X + dx;
                    int y = point.Y + dy;
                    if (x >= 0 && y >= 0 && x < frame.Width && y < frame.Height && frame.GetPixel(x, y).A >= 12 && (!uncovered || Uncovered(PointToScreen(new Point(x, y))))) return true;
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
            seat = null;
            falling = false;
            hanging = false;
            hotkey = Native.RegisterHotKey(Handle, DismountId, 0x0001 | 0x0002 | 0x4000, 0x43);
            riding = true;
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x20);
            rideItem.Text = "Hop off";
            tray.Text = hotkey ? "Capri · Ctrl + Alt + C to hop off" : "Capri · Hop off from this menu";
            if (!testing) { balloonPage = null; tray.ShowBalloonTip(6000, "Capri is riding your cursor", hotkey ? "Press Ctrl + Alt + C to get her off." : "Right-click her tray icon, then choose Move and Hop off.", ToolTipIcon.Info); }
            Play(perchGlitch);
            frames.Start();
            pill.Hide();
            mainframe.Sync();
        }

        private void StopRide()
        {
            if (hotkey && IsHandleCreated) Native.UnregisterHotKey(Handle, DismountId);
            hotkey = false;
            if (!riding) return;
            riding = false;
            leanAngle = 0;
            Play(grin);
            if (IsHandleCreated) Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) & ~0x20);
            if (rideItem != null) rideItem.Text = "Perch on cursor";
            if (tray != null) tray.Text = "Capri · Double-click to restore";
            if (!IsDisposed) { Location = anchor; UpdateAnimation(); SavePreferences(); mainframe.Sync(); }
        }

        private void FollowCursor()
        {
            Point cursor = Cursor.Position;
            Rectangle area = Screen.FromPoint(cursor).WorkingArea;
            int seatX = PaddingSize + (int)(preferences.Size * 0.267f) - 12;
            int seatY = PaddingSize + (int)(preferences.Size * 0.73f) + 12;
            Point target = Within(new Point(cursor.X - seatX, cursor.Y - seatY), area, PerchedBody);
            int nx = anchor.X + (int)Math.Round((target.X - anchor.X) * 0.25);
            int ny = anchor.Y + (int)Math.Round((target.Y - anchor.Y) * 0.25);
            double velocity = Math.Max(-8, Math.Min(8, (nx - anchor.X) * 0.4));
            leanAngle = leanAngle * 0.8 + velocity * 0.2;
            anchor = Within(new Point(nx, ny), area, PerchedBody);
        }

        private Point Hip() { return new Point(anchor.X + PaddingSize + (int)(preferences.Size * SitX), anchor.Y + PaddingSize + (int)(preferences.Size * SitY)); }

        private Point Seated(int x, int y) { return new Point(x - PaddingSize - (int)(preferences.Size * SitX), y - PaddingSize - (int)(preferences.Size * SitY)); }

        private System.Collections.Generic.List<IntPtr> Skip()
        {
            System.Collections.Generic.List<IntPtr> skip = new System.Collections.Generic.List<IntPtr>();
            foreach (Form own in new Form[] { this, pill, box, typer, mainframe }) if (own.IsHandleCreated) skip.Add(own.Handle);
            return skip;
        }

        private bool Drop()
        {
            if (preferences.NoSitting) return false;
            Ledge target = Ledges.Below(Ledges.Find(Skip(), icons), Hip(), preferences.Size / 6, (int)(preferences.Size * 0.75f), 24, PaddingSize + (int)(preferences.Size * SitY));
            if (target == null) return false;
            falling = true;
            fallTo = target;
            fallSpeed = 0;
            fallStep = clock.Elapsed.TotalSeconds;
            frames.Start();
            return true;
        }

        internal string ErrorDetails()
        {
            Cloud remote = brain.Remote;
            string head = "Capri v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " | brain: " + (remote != null ? remote.Name + " " + remote.Model : brain.Local != null ? "built-in " + brain.Model : brain.Model != null ? "Ollama " + brain.Model : "none") + " | watch: " + (preferences.Watch ? GlanceGap + " s" : "off") + " | voice: " + (voice.Key != null ? "ElevenLabs" : "none");
            string[] lines = new string[0];
            try { if (Brain.LogPath != null && File.Exists(Brain.LogPath)) lines = File.ReadAllLines(Brain.LogPath); } catch (IOException) { }
            int from = Math.Max(0, lines.Length - 25);
            return head + Environment.NewLine + (lines.Length == 0 ? "No errors logged." : string.Join(Environment.NewLine, lines, from, lines.Length - from));
        }

        private void CopyErrors()
        {
            try { Clipboard.SetText(ErrorDetails()); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            Tell("copy error details", "Copied my error log. Paste it in the Discord so they can fix me, choom.");
        }

        internal void ToggleWander()
        {
            preferences.Wander = !preferences.Wander;
            wanderItem.Checked = preferences.Wander;
            nextWander = preferences.Wander ? clock.Elapsed.TotalSeconds + 3 : -1;
            SavePreferences();
        }

        private void CheckWander(double now, long quiet)
        {
            bool settled = talkState == TalkState.Off || (talkState == TalkState.Talking && replyDone >= 0 && now - replyDone > 8);
            if (!preferences.Wander || hidden || dragging || pressed || riding || falling || hanging || chatting || typer.Visible || menu.Visible || speech != null || !settled || quiet > 90000 || (seat != null && seat.Free) || (clip != idle && clip != perch)) return;
            if (nextWander < 0) nextWander = now + 20 + random.NextDouble() * 40;
            if (now < nextWander) return;
            nextWander = now + 45 + random.NextDouble() * 75;
            if (talkState != TalkState.Off) CloseTalk();
            Wander();
        }

        private System.Collections.Generic.List<Ledge> Spots()
        {
            System.Collections.Generic.List<Ledge> spots = new System.Collections.Generic.List<Ledge>();
            int room = PaddingSize + (int)(preferences.Size * SitY);
            foreach (Ledge ledge in Ledges.Find(Skip(), icons))
            {
                if (!ledge.Icon.IsEmpty || ledge.Right - ledge.Left < preferences.Size / 2) continue;
                if (seat != null && ledge.Window == seat.Window && ledge.Floor == seat.Floor) continue;
                if (ledge.Y - room < Screen.FromPoint(new Point(ledge.Left, ledge.Y)).Bounds.Top) continue;
                spots.Add(ledge);
            }
            return spots;
        }

        private bool Wander()
        {
            System.Collections.Generic.List<Ledge> spots = Spots();
            if (spots.Count == 0) return false;
            Ledge spot = spots[random.Next(spots.Count)];
            int margin = preferences.Size / 4;
            hanging = false;
            anchor = Seated(spot.Left + margin + random.Next(Math.Max(1, spot.Right - spot.Left - 2 * margin)), spot.Y);
            Land(spot);
            frames.Start();
            return seat == spot;
        }

        private void SitHere()
        {
            if (hidden || riding) return;
            hanging = false;
            Point hip = Hip();
            Land(new Ledge { Left = hip.X - 8, Right = hip.X + 8, Y = hip.Y, Floor = true, Free = true });
            frames.Start();
        }

        private void HangHere()
        {
            if (hidden || riding) return;
            seat = null;
            falling = false;
            fallTo = null;
            hanging = true;
            Play(dangle);
            frames.Start();
            SavePreferences();
        }

        private void LetGo()
        {
            hanging = false;
            Play(glitch);
            frames.Start();
            if (!Drop()) SavePreferences();
        }

        private void StandUp()
        {
            hanging = false;
            seat = null;
            coveredSince = -1;
            seatLost = -1;
            falling = false;
            fallTo = null;
            leanAngle = 0;
            Play(glitch);
            frames.Start();
        }

        private void Tumble(double now)
        {
            double step = Math.Max(0, Math.Min(0.05, now - fallStep));
            fallStep = now;
            Point hip = Hip();
            if (fallTo == null) fallTo = Ledges.Below(Ledges.Find(Skip(), icons), hip, 0, int.MaxValue, 0, PaddingSize + (int)(preferences.Size * SitY));
            if (fallTo == null) { falling = false; SavePreferences(); return; }
            fallSpeed += preferences.Size * 11 * step;
            int drop = (int)Math.Ceiling(fallSpeed * step);
            if (hip.Y + drop >= fallTo.Y) Land(fallTo);
            else anchor.Y += drop;
        }

        private void Land(Ledge ledge)
        {
            fallTo = null;
            if (ledge.Window != IntPtr.Zero)
            {
                Rectangle bounds;
                if (!Ledges.Showing(ledge.Window, out bounds) || !Ledges.Seatable(ledge.Window, bounds)) return;
                if (bounds != ledge.Bounds) { ledge.Left = bounds.Left; ledge.Right = bounds.Right; ledge.Y = bounds.Top; ledge.Bounds = bounds; }
            }
            falling = false;
            coveredSince = -1;
            seatLost = -1;
            int x = ledge.Icon.IsEmpty ? Math.Max(ledge.Left + 8, Math.Min(Hip().X, ledge.Right - 8)) : ledge.Icon.Left + ledge.Icon.Width / 2;
            Rectangle area = Screen.FromPoint(new Point(x, ledge.Y - 1)).WorkingArea;
            int rightmost = area.Right - (int)Math.Ceiling(preferences.Size * (PerchedBody.Right - SitX));
            if (ledge.Icon.IsEmpty && ledge.Right >= area.Right - 8 && BodyOnScreen(anchor, Body).Right >= area.Right - 2) x = rightmost;
            x = Math.Max(area.Left + (int)Math.Ceiling(preferences.Size * (SitX - PerchedBody.Left)), Math.Min(x, rightmost));
            seat = ledge;
            leanAngle = 0;
            anchor = Seated(x, ledge.Y);
            Play(perchGlitch);
            SavePreferences();
        }

        private void HoldSeat(double now)
        {
            if (seat.Window == IntPtr.Zero) return;
            Rectangle bounds;
            if (!Ledges.Showing(seat.Window, out bounds) || !Ledges.Seatable(seat.Window, bounds))
            {
                if (seatLost < 0) seatLost = now;
                if (!Ledges.Alive(seat.Window) || now - seatLost >= 0.3) StandUp();
                return;
            }
            seatLost = -1;
            if (bounds == seat.Bounds) return;
            int dx = bounds.Width == seat.Bounds.Width ? bounds.Left - seat.Bounds.Left : 0;
            int x = Math.Max(bounds.Left + 8, Math.Min(Hip().X + dx, bounds.Right - 8));
            seat.Bounds = bounds;
            seat.Left = bounds.Left;
            seat.Right = bounds.Right;
            seat.Y = bounds.Top;
            anchor = Seated(x, bounds.Top);
        }

        private void CheckSeat(double now)
        {
            if (seat == null || falling || seat.Floor) return;
            if (!seat.Icon.IsEmpty && now - iconsAt > 2) RefreshIcons();
            if (!Ledges.Covered(seat, Hip().X, Skip())) { coveredSince = -1; return; }
            if (coveredSince < 0) coveredSince = now;
            if (now - coveredSince >= 0.7) StandUp();
        }

        private void RefreshIcons()
        {
            if (iconsBusy) return;
            iconsBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                System.Collections.Generic.List<Rectangle> found;
                try { found = Ledges.Icons(); } catch { found = new System.Collections.Generic.List<Rectangle>(); }
                Post(delegate
                {
                    iconsBusy = false;
                    icons = found;
                    iconsAt = clock.Elapsed.TotalSeconds;
                    if (seat != null && !seat.Icon.IsEmpty && !falling && !found.Contains(seat.Icon)) StandUp();
                });
            });
        }

        internal bool Sitting { get { return !preferences.NoSitting; } }

        internal void ToggleSitting()
        {
            preferences.NoSitting = !preferences.NoSitting;
            sitItem.Checked = !preferences.NoSitting;
            if (preferences.NoSitting && (seat != null || falling)) { seat = null; falling = false; Play(idle); UpdateAnimation(); }
            SavePreferences();
        }

        private void React()
        {
            if (hanging) { LetGo(); return; }
            clicks++;
            Play(seat != null ? perchGlitch : glitch);
            if (!hidden) frames.Start();
            RenderFrame();
        }

        private void SetHidden(bool value)
        {
            hidden = value;
            visibilityItem.Text = value ? "Show Capri" : "Hide Capri";
            if (value) { StopRide(); seat = null; falling = false; hanging = false; pendingClick = false; clickDelay.Stop(); frames.Stop(); Hide(); pill.Hide(); typer.Hide(); CloseTalk(); StopChat(); }
            else { anchor = KeepOnScreen(anchor); Location = anchor; Play(grin); Show(); RenderFrame(); UpdateAnimation(); }
            mainframe.Sync();
        }

        private void RestoreAtCursor()
        {
            StopRide();
            seat = null;
            falling = false;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            anchor = new Point(area.Right - Width - 28, area.Bottom - Height - 12);
            SetHidden(false);
            SavePreferences();
        }

        internal bool IsHidden { get { return hidden; } }
        internal bool Riding { get { return riding; } }
        internal int CharacterSize { get { return preferences.Size; } }
        internal bool Motion { get { return preferences.Motion; } }
        internal bool Watching { get { return preferences.Watch; } }
        internal bool Speaking { get { return !preferences.Quiet; } }

        internal void ToggleSpeaking()
        {
            preferences.Quiet = !preferences.Quiet;
            if (preferences.Quiet) voice.Stop();
            SavePreferences();
            mainframe.Sync();
        }

        internal bool FreeVoice { get { return preferences.FreeVoice; } }

        internal void ToggleFreeVoice()
        {
            preferences.FreeVoice = !preferences.FreeVoice;
            StopChat();
            SavePreferences();
            mainframe.Sync();
        }

        private void CheckSlow(double now)
        {
            if (slowWarned || talkState != TalkState.Thinking || chatting || brain.Remote != null || double.IsNaN(thinkingSince) || now - thinkingSince < 25) return;
            slowWarned = true;
            mainframe.ShowProblem("Slow? Press Free key for a free NVIDIA key.");
            if (testing) return;
            balloonPage = FreeKeyPage;
            tray.ShowBalloonTip(15000, "Capri is replying slowly", "Her brain runs on your PC, and open games share that power. For fast replies, click here, make a free NVIDIA key, then paste it in Capri Mainframe under BRAIN and press Connect.", ToolTipIcon.Info);
        }

        internal int GlanceGap { get { return preferences.GlanceGap > 0 ? preferences.GlanceGap : 120; } }

        internal void SetGlanceGap(int seconds)
        {
            preferences.GlanceGap = seconds;
            if (preferences.Watch) nextGlance = clock.Elapsed.TotalSeconds + Math.Min(20, seconds);
            SavePreferences();
            mainframe.Sync();
        }

        internal void ToggleWatch()
        {
            preferences.Watch = !preferences.Watch;
            nextGlance = -1;
            lastPrint = null;
            SavePreferences();
            mainframe.Sync();
            if (!preferences.Watch) return;
            Cloud remote = brain.Remote;
            Engine local = brain.Local;
            Pick pick = brain.LocalPick;
            if (remote != null && remote.Kind == "deepseek") mainframe.ShowProblem("DeepSeek can't see. Use an NVIDIA key to watch.");
            else if (remote == null && local != null && pick != null)
            {
                if (pick.EyesFile == null) mainframe.ShowProblem("This brain can't see. Press Free key to fix.");
                else if (!local.HasEyes(pick)) FetchEyes(local, pick);
            }
        }

        private string Unseeing()
        {
            Cloud remote = brain.Remote;
            if (remote != null) return remote.Kind == "deepseek" ? Brain.Blind : remote.Model == brain.Unsighted ? Brain.Sightless(remote) : null;
            if (brain.Model == null) return "You can't see their screen: your brain is offline.";
            Engine local = brain.Local;
            Pick pick = brain.LocalPick;
            if (local == null || pick == null) return null;
            if (pick.EyesFile == null) return "Your brain can't see. Tell them to press Free key in Capri Mainframe and connect a free NVIDIA key so you can.";
            if (local.HasEyes(pick)) return null;
            FetchEyes(local, pick);
            return "Your eyes are still downloading, so you can't see their screen yet.";
        }

        private void FetchEyes(Engine local, Pick pick)
        {
            if (fetchingEyes) return;
            fetchingEyes = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null;
                try { local.InstallEyes(pick, delegate(string label, long have, long size) { Post(delegate { mainframe.ShowProblem(label + " " + (have * 100 / Math.Max(1, size)) + "%"); }); }); }
                catch (Exception error) { problem = error.Message; }
                fetchingEyes = false;
                Post(delegate { if (brain.Local == local) mainframe.ShowProblem(problem ?? "Her eyes are in. She can see your screen now."); });
            });
        }

        private string LookAt(string focus)
        {
            string blind = Unseeing();
            if (blind != null) return blind;
            byte[] shot;
            using (Bitmap screen = InvokeRequired ? (Bitmap)Invoke(new Func<Bitmap>(CaptureScreen)) : CaptureScreen()) shot = Jpeg(screen, 1280);
            string seen;
            try
            {
                seen = brain.See(shot, "You describe screenshots accurately and briefly.", "Describe what's on this screen in two or three plain sentences: what kind of game or app it is (name it only if the name is on screen), what's happening, and any text or numbers that matter." + (string.IsNullOrWhiteSpace(focus) ? "" : " Pay attention to: " + focus.Trim()), 160);
            }
            catch (BrainException)
            {
                string unseen = Unseeing();
                if (unseen == null) throw;
                return unseen;
            }
            return seen.Length == 0 ? "You looked but couldn't make out their screen." : "You looked at their screen. You see: " + seen;
        }

        private void CheckGlance(double now, long quiet)
        {
            if (!preferences.Watch || glancing) return;
            if (nextGlance < 0) nextGlance = now + Math.Min(20, GlanceGap);
            if (now < nextGlance || quiet > 90000 || brain.Model == null || hidden || chatting || dragging || typer.Visible || talkState != TalkState.Off || clip == sleep) return;
            nextGlance = now + GlanceGap * (0.8 + random.NextDouble() * 0.4);
            if (Unseeing() != null) return;
            byte[] shot;
            try
            {
                using (Bitmap screen = CaptureScreen())
                {
                    if (!Changed(screen)) return;
                    shot = Jpeg(screen, 1280);
                }
            }
            catch (Exception) { return; }
            glancing = true;
            int ticket = asking;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said = null;
                try { said = Brain.Clean(Safety.Filter(brain.See(shot, Brain.Persona, GlanceLine, 90))); }
                catch (Exception) { }
                Post(delegate
                {
                    glancing = false;
                    string unseen = Unseeing();
                    if (unseen != null) mainframe.ShowProblem(unseen);
                    if (string.IsNullOrEmpty(said) || System.Text.RegularExpressions.Regex.IsMatch(said, @"\bSKIP\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
                    if (ticket != asking || talkState != TalkState.Off || typer.Visible || chatting || hidden) return;
                    brain.Remember("(You glanced at their screen.)", said);
                    Tell("watching", said);
                });
            });
        }

        private bool Changed(Bitmap screen)
        {
            byte[] print = new byte[32 * 18];
            using (Bitmap tiny = new Bitmap(32, 18, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(tiny))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(screen, 0, 0, 32, 18);
                }
                for (int y = 0; y < 18; y++) for (int x = 0; x < 32; x++) { Color pixel = tiny.GetPixel(x, y); print[y * 32 + x] = (byte)((pixel.R * 3 + pixel.G * 6 + pixel.B) / 10); }
            }
            byte[] before = lastPrint;
            lastPrint = print;
            if (before == null) return true;
            long difference = 0;
            for (int i = 0; i < print.Length; i++) difference += Math.Abs(print[i] - before[i]);
            return difference > print.Length * 6;
        }

        private Bitmap CaptureScreen()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).Bounds;
            Bitmap shot = Grab(area);
            Bitmap drawn = frame;
            if (drawn != null && !hidden) Smear(shot, drawn, anchor.X - area.X, anchor.Y - area.Y, drawn.Width, drawn.Height);
            if (box.Visible) Smear(shot, null, box.Left - area.X, box.Top - area.Y, box.Width, box.Height);
            if (pill.Visible) Smear(shot, null, pill.Left - area.X, pill.Top - area.Y, pill.Width, pill.Height);
            return shot;
        }

        internal static void Smear(Bitmap shot, Bitmap mask, int left, int top, int width, int height)
        {
            Rectangle inside = Rectangle.Intersect(new Rectangle(left, top, width, height), new Rectangle(0, 0, shot.Width, shot.Height));
            if (inside.IsEmpty) return;
            BitmapData picture = shot.LockBits(new Rectangle(0, 0, shot.Width, shot.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            BitmapData cover = mask == null ? null : mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[picture.Stride];
                byte[] alpha = cover == null ? null : new byte[cover.Stride];
                for (int y = inside.Top; y < inside.Bottom; y++)
                {
                    IntPtr line = picture.Scan0 + y * picture.Stride;
                    Marshal.Copy(line, row, 0, row.Length);
                    if (cover != null) Marshal.Copy(cover.Scan0 + (y - top) * cover.Stride, alpha, 0, alpha.Length);
                    int x = inside.Left;
                    while (x < inside.Right)
                    {
                        if (cover != null && alpha[(x - left) * 4 + 3] <= 8) { x++; continue; }
                        int start = x;
                        while (x < inside.Right && (cover == null || alpha[(x - left) * 4 + 3] > 8)) x++;
                        int before = start - 1;
                        int after = x;
                        for (int i = start; i < after; i++)
                        {
                            double share = (i - before) / (double)(after - before);
                            for (int c = 0; c < 3; c++)
                            {
                                int from = before >= 0 ? row[before * 3 + c] : after < shot.Width ? row[after * 3 + c] : 128;
                                int to = after < shot.Width ? row[after * 3 + c] : from;
                                row[i * 3 + c] = (byte)(from + (to - from) * share);
                            }
                        }
                    }
                    Marshal.Copy(row, 0, line, row.Length);
                }
            }
            finally
            {
                shot.UnlockBits(picture);
                if (cover != null) mask.UnlockBits(cover);
            }
        }

        private static Bitmap GrabScreen(Rectangle area)
        {
            Bitmap shot = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return shot;
        }

        internal static byte[] Jpeg(Bitmap picture, int longest)
        {
            double scale = Math.Min(1.0, (double)longest / Math.Max(picture.Width, picture.Height));
            int width = Math.Max(1, (int)Math.Round(picture.Width * scale));
            int height = Math.Max(1, (int)Math.Round(picture.Height * scale));
            using (Bitmap small = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(small))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(picture, 0, 0, width, height);
                }
                ImageCodecInfo codec = Array.Find(ImageCodecInfo.GetImageEncoders(), found => found.FormatID == ImageFormat.Jpeg.Guid);
                using (EncoderParameters options = new EncoderParameters(1))
                using (MemoryStream output = new MemoryStream())
                {
                    options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    small.Save(output, codec, options);
                    return output.ToArray();
                }
            }
        }

        internal bool OnTop { get { return preferences.OnTop; } }
        internal void ToggleHidden() { SetHidden(!hidden); }
        internal void BringHere() { RestoreAtCursor(); }
        internal void TogglePerch() { if (riding) StopRide(); else StartRide(); }
        internal void CheckUpdates() { if (updatePage != null) OpenPage(updatePage); else CheckForUpdates(true); }
        internal void OpenOllamaPage() { OpenPage(OllamaPage); }
        internal void Quit() { Close(); }

        internal void ToggleMotion()
        {
            preferences.Motion = !preferences.Motion;
            UpdateAnimation();
            SavePreferences();
            mainframe.Sync();
        }

        internal void ToggleOnTop()
        {
            preferences.OnTop = !preferences.OnTop;
            TopMost = preferences.OnTop;
            Layer();
            SavePreferences();
            mainframe.Sync();
        }

        private void Layer()
        {
            pill.Above = preferences.OnTop;
            box.Above = preferences.OnTop;
            typer.TopMost = preferences.OnTop;
            foreach (Form above in new Form[] { pill, box, typer })
            {
                if (above.Owner != this) above.Owner = this;
                if (above.IsHandleCreated) Native.SetWindowPos(above.Handle, new IntPtr(preferences.OnTop ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
        }

        internal void FindBrain()
        {
            Cloud remote = brain.Remote;
            if (remote != null)
            {
                brain.Local = null;
                if (remote.Model != null) { brain.Model = remote.Model; mainframe.ShowCloud(remote, cloudModels); }
                ListCloud(remote.Key, false);
                return;
            }
            if (finding || SettingUp) return;
            finding = true;
            mainframe.Searching();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string[] installed = null;
                try { installed = Brain.InstalledModels(); } catch (WebException) { } catch (IOException) { } catch (SerializationException) { }
                Machine found = Machine.Read();
                Pick local = engine.Installed();
                Post(delegate { finding = false; UseBrain(installed, found, local); });
            });
        }

        private void Post(Action action)
        {
            try { if (!IsDisposed) BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void UseBrain(string[] installed, Machine found, Pick local)
        {
            machine = found;
            fit = Engine.Fit(found);
            ollamaRunning = installed != null;
            Cloud remote = brain.Remote;
            if (remote != null)
            {
                brain.Local = null;
                brain.Model = remote.Model;
                mainframe.ShowCloud(remote, cloudModels);
                return;
            }
            if (installed != null && installed.Length > 0)
            {
                brain.Local = null;
                brain.Model = Brain.PickModel(installed, preferences.Model, fit);
                mainframe.ShowOllama(installed, brain.Model);
            }
            else if (local != null)
            {
                brain.LocalPick = local;
                brain.Local = engine;
                brain.Model = local.Name;
                mainframe.ShowLocal(local);
            }
            else
            {
                brain.Local = null;
                brain.Model = null;
                mainframe.ShowSetup(found, fit, ollamaRunning, null);
            }
        }

        internal void SetUpBrain()
        {
            if (SettingUp || machine == null) return;
            SettingUp = true;
            engine.Cancel = false;
            Pick pick = fit;
            bool viaOllama = ollamaRunning;
            mainframe.ShowProgress(1, "Starting download", 0, 0);
            long shownAt = 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Action<string, long, long> progress = delegate(string text, long done, long total)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (now - Interlocked.Read(ref shownAt) < 200 && done < total) return;
                    Interlocked.Exchange(ref shownAt, now);
                    string label = viaOllama ? "Downloading " + pick.Name : text;
                    Post(delegate { if (SettingUp) mainframe.ShowProgress(1, label, done, total); });
                };
                try
                {
                    if (viaOllama) Brain.Pull(pick.Ollama, progress, delegate { return engine.Cancel; });
                    else engine.Install(pick, progress);
                    Post(delegate { mainframe.ShowProgress(2, "Loading " + pick.Name, 0, 0); });
                    if (viaOllama) { brain.Local = null; brain.Model = pick.Ollama; brain.Warm(); }
                    else { brain.LocalPick = pick; brain.Local = engine; brain.Model = pick.Name; engine.Ensure(pick); }
                    Post(delegate
                    {
                        SettingUp = false;
                        if (viaOllama) { preferences.Model = pick.Ollama; SavePreferences(); FindBrain(); }
                        else mainframe.ShowLocal(pick);
                        Greet();
                    });
                }
                catch (OperationCanceledException) { Post(delegate { SettingUp = false; mainframe.ShowSetup(machine, pick, viaOllama, "Cancelled. It picks up where it left off."); }); }
                catch (Exception error) { string problem = error.Message.Length > 70 ? error.Message.Substring(0, 67) + "..." : error.Message; Post(delegate { SettingUp = false; mainframe.ShowSetup(machine, pick, viaOllama, problem); }); }
            });
        }

        internal void CancelSetup() { engine.Cancel = true; }

        private void Greet()
        {
            if (hidden) SetHidden(false);
            Hear("(You just woke up on this PC with a brand new brain. Say hi to the user in one short line.)", "brain online", "> sys: ");
        }

        internal void ChooseModel(string name)
        {
            if (name == brain.Model) return;
            brain.Model = name;
            brain.Forget();
            Cloud remote = brain.Remote;
            if (remote != null) { remote.Model = name; preferences.TextModel = name; }
            else preferences.Model = name;
            SavePreferences();
        }

        internal void ConnectText(string key)
        {
            key = (key ?? "").Trim();
            if (Cloud.KindOf(key) == null) { mainframe.ShowProblem("That isn't an NVIDIA, OpenAI, Claude or DeepSeek key."); return; }
            ListCloud(key, true);
        }

        internal void ForgetText()
        {
            brain.Cancel();
            brain.Remote = null;
            brain.Forget();
            cloudModels = null;
            preferences.TextKey = null;
            preferences.TextModel = null;
            SavePreferences();
            mainframe.ShowTextKey(null);
            FindBrain();
        }

        private void ListCloud(string key, bool connecting)
        {
            Cloud probe = new Cloud(key, null);
            if (connecting) mainframe.ShowProblem("Checking key...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                System.Collections.Generic.List<string> found = null;
                string problem = null;
                try { found = Brain.CloudModels(key); }
                catch (BrainException error) { problem = error.Message; }
                catch (Exception error) { Brain.Log("https://" + probe.Company.ToLowerInvariant() + "/models", -1, error.Message); problem = brain.Remote != null && brain.Remote.Key == key && brain.Remote.Model != null ? probe.Company + " is slow. Using " + brain.Remote.Model + "." : "Couldn't reach " + probe.Company + ". Check your internet."; }
                Post(delegate
                {
                    if (problem != null) { if (connecting || brain.Remote != null) mainframe.ShowProblem(problem.Length > 60 ? problem.Substring(0, 57) + "..." : problem); return; }
                    if (connecting)
                    {
                        Cloud remote = new Cloud(key, Brain.DefaultCloudModel(probe.Kind, found));
                        if (remote.Model == null) { mainframe.ShowProblem("That key can't use any chat models."); return; }
                        brain.Cancel();
                        brain.Remote = remote;
                        brain.Forget();
                        preferences.TextKey = Voice.Protect(key);
                        preferences.TextModel = remote.Model;
                        SavePreferences();
                        mainframe.ShowTextKey(Voice.Tail(key));
                    }
                    if (brain.Remote == null || brain.Remote.Key != key) return;
                    cloudModels = found;
                    if (brain.Remote.Model == null || !found.Contains(brain.Remote.Model)) brain.Remote.Model = Brain.DefaultCloudModel(brain.Remote.Kind, found) ?? brain.Remote.Model;
                    brain.Model = brain.Remote.Model;
                    mainframe.ShowCloud(brain.Remote, cloudModels);
                    if (connecting) Greet();
                });
            });
        }

        internal void ConnectVoice(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length > 0) ListVoices(key, true);
        }

        internal void ForgetVoice()
        {
            StopChat();
            voice.Stop();
            voice.Key = null;
            voices = null;
            preferences.VoiceKey = null;
            preferences.AgentId = null;
            preferences.AgentVersion = 0;
            SavePreferences();
            ShowVoice();
        }

        internal void ChooseVoice(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) id = null;
            if (id == voice.VoiceId) { ShowVoice(false); return; }
            voice.VoiceId = id;
            preferences.VoiceId = id;
            SavePreferences();
            ShowVoice();
        }

        internal void ChooseVoiceModel(string id)
        {
            voice.Model = string.IsNullOrEmpty(id) ? null : id;
            preferences.VoiceModel = voice.Model;
            SavePreferences();
            ShowVoice();
        }

        internal string MicId { get { return preferences.Mic; } }

        private static string MicName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (Mic mic in Ears.List()) if (mic.Id == id) return mic.Name;
            return null;
        }

        internal void ChooseMic(string id)
        {
            preferences.Mic = string.IsNullOrEmpty(id) ? null : id;
            ears.Chosen = preferences.Mic;
            SavePreferences();
            mainframe.ShowMic(MicName(preferences.Mic));
            if (!ears.Listening) return;
            ears.Stop();
            try { ears.Start(); }
            catch (VoiceException error) { mainframe.VoiceNote(error.Message, true); StopChat(); }
        }

        internal void OpenKeyPage() { OpenPage(GuidePage); Tell("get a key", GuideLine); }

        internal void OpenFreeKeyPage() { OpenPage(FreeKeyPage); Tell("free key", FreeKeyLine); }

        private void Tell(string shown, string line)
        {
            if (hidden) SetHidden(false);
            heard = shown;
            box.Prompt = "> sys: ";
            Answer(++asking, line, TalkState.Talking);
        }

        internal void SetUpFreeVoice()
        {
            if (SettingUpVoice) return;
            SettingUpVoice = true;
            kokoro.Cancel = false;
            ShowVoice(false);
            mainframe.VoiceNote("Getting her free voice...", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null;
                Action<string, long, long> progress = delegate(string text, long done, long total)
                {
                    Post(delegate { mainframe.VoiceNote(total > 0 && done < total ? text + " · " + Engine.Amount(done) + " of " + Engine.Amount(total) : text + "...", false); });
                };
                try { kokoro.Install(progress); kokoro.Warm(); }
                catch (OperationCanceledException) { problem = "Cancelled. It picks up where it left off."; }
                catch (Exception error) { problem = Voice.Short(error.Message); }
                Post(delegate
                {
                    SettingUpVoice = false;
                    ShowVoice();
                    if (problem != null) mainframe.VoiceNote(problem, true);
                    else if (voice.Key == null) TestVoice();
                });
            });
        }

        internal void CancelFreeVoice() { kokoro.Cancel = true; }

        internal void ChooseVoiceStyle(string name)
        {
            kokoro.Style = LocalVoice.Find(name);
            preferences.VoiceStyle = kokoro.Style == LocalVoice.Styles[0] ? null : kokoro.Style.Name;
            SavePreferences();
            ShowVoice();
            if (voice.Key == null && kokoro.Installed) TestVoice();
        }

        internal void TestVoice()
        {
            if (voice.Key == null && Speak == null && !kokoro.Installed) { mainframe.VoiceNote("Get her free voice, or paste an ElevenLabs key.", true); return; }
            if (voice.Key != null && voice.VoiceId == null && Speak == null) { mainframe.VoiceNote("Paste your Voice ID first.", true); return; }
            if (hidden) SetHidden(false);
            brain.Cancel();
            heard = "voice check";
            box.Prompt = "> sys: ";
            Answer(++asking, "Voice link online. Miss me, choom?", TalkState.Talking);
        }

        private void ListVoices(string key, bool connecting)
        {
            mainframe.VoiceNote(connecting ? "Checking key..." : "Loading voices...", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                VoiceListing found = null;
                string problem = null;
                int status = 0;
                try { found = Voice.Fetch(key); }
                catch (VoiceException error) { problem = error.Message; status = error.Status; }
                catch (Exception error) { problem = Voice.Short("Couldn't read your ElevenLabs voices: " + error.Message); }
                Post(delegate
                {
                    if (connecting && status == 401) { mainframe.VoiceNote(problem, true); return; }
                    if (connecting) { voice.Key = key; preferences.VoiceKey = Voice.Protect(key); SavePreferences(); }
                    if (voice.Key != key) return;
                    if (found != null) voices = found;
                    ShowVoice();
                    if (problem != null) mainframe.VoiceNote(problem, true);
                });
            });
        }

        private void ShowVoice(bool note = true)
        {
            bool ready = kokoro.Installed;
            mainframe.ShowVoice(voice.Key == null ? null : Voice.Tail(voice.Key), voice.VoiceId, voice.Model, voices);
            mainframe.ShowFree(ready, kokoro.Style.Name, SettingUpVoice);
            if (!note) return;
            string name = "custom voice";
            if (voices != null) foreach (VoiceEntry entry in voices.Voices) if (entry.Id == voice.VoiceId && entry.Name != null) name = entry.Name;
            mainframe.VoiceNote(voice.Key == null ? (ready ? "Free voice, runs on your PC." : "Get her free voice (" + Engine.Amount(LocalVoice.PackSize) + "), or paste a key.") : voice.VoiceId == null ? "Needs your Voice ID." : "ElevenLabs · " + name, false);
        }

        private float TalkScale()
        {
            int dpi = 96;
            try { if (IsHandleCreated) dpi = Native.GetDpiForWindow(Handle); } catch (EntryPointNotFoundException) { }
            return Math.Max(1f, dpi / 96f);
        }

        private Point Head() { return new Point(anchor.X + PaddingSize + (int)(preferences.Size * 0.52f), anchor.Y + PaddingSize - 4); }

        private Point Feet() { return new Point(anchor.X + PaddingSize + (int)(preferences.Size * 0.58f), anchor.Y + PaddingSize + (int)(preferences.Size * 0.99f)); }

        private static string FakeServer(string[] replies, System.Collections.Generic.List<string> asked, out System.Net.Sockets.TcpListener listener)
        {
            System.Net.Sockets.TcpListener server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            server.Start();
            listener = server;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    foreach (string reply in replies)
                        using (System.Net.Sockets.TcpClient client = server.AcceptTcpClient())
                        using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                        {
                            MemoryStream got = new MemoryStream();
                            byte[] buffer = new byte[8192];
                            string text = "";
                            while (true)
                            {
                                int read = stream.Read(buffer, 0, buffer.Length);
                                if (read <= 0) break;
                                got.Write(buffer, 0, read);
                                text = System.Text.Encoding.UTF8.GetString(got.ToArray());
                                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (end < 0) continue;
                                System.Text.RegularExpressions.Match length = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (got.Length >= end + 4 + (length.Success ? int.Parse(length.Groups[1].Value) : 0)) break;
                            }
                            lock (asked) asked.Add(text);
                            byte[] answer = System.Text.Encoding.UTF8.GetBytes(reply);
                            byte[] head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + answer.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(head, 0, head.Length);
                            stream.Write(answer, 0, answer.Length);
                        }
                }
                catch (Exception) { }
            });
            return "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        }

        private bool Uncovered(Point screen) { return Native.GetAncestor(Native.WindowFromPoint(new Native.Point(screen.X, screen.Y)), 2) == Handle; }

        private void UpdatePill(double now)
        {
            if (!IsHandleCreated) return;
            bool allowed = !hidden && !riding && !dragging && !pressed && !falling;
            Point cursor = Cursor.Position;
            Point inside = PointToClient(cursor);
            bool near = frame != null && inside.X >= 0 && inside.Y >= 0 && inside.X < Width && inside.Y < Height && Solid(inside, true);
            if (allowed && (near || (pill.Visible && pill.Bounds.Contains(cursor)) || typer.Visible || chatting || talkState != TalkState.Off)) pillUntil = Math.Max(pillUntil, now + 1.5);
            if (!allowed || now > pillUntil) { if (pill.Visible) pill.Hide(); return; }
            float s = TalkScale();
            Point feet = Feet();
            Rectangle area = Screen.FromPoint(feet).WorkingArea;
            int top = Math.Min(feet.Y + (int)(2 * s), area.Bottom - TalkPill.Measure(s).Height - 2);
            pill.Place(new Point(feet.X - (int)(4 * s), top), s);
        }

        private void OpenTyper()
        {
            if (typer.Visible) { typer.Hide(); return; }
            float s = TalkScale();
            typer.Open(Head(), new Point(Feet().X, Feet().Y + (int)(44 * s)), Screen.FromPoint(Head()).WorkingArea, s, box.Visible ? box.PanelOnScreen : Rectangle.Empty);
            pillUntil = clock.Elapsed.TotalSeconds + 1.5;
            if (Think != null || testing) return;
            if (brain.Model == null) FindBrain();
            else ThreadPool.QueueUserWorkItem(delegate { brain.Warm(); });
        }

        private void ToggleChat()
        {
            if (chatting) { StopChat(); return; }
            if (preferences.FreeVoice || voice.Key == null) { StartLocal(); return; }
            if (voice.VoiceId == null)
            {
                heard = "(voice)";
                box.Prompt = "> you: ";
                Answer(++asking, "Voice chat needs my Voice ID, choom. Right-click me, open Capri Mainframe and pick a voice in the Voice ID box under VOICE. Type to me for now.", TalkState.Talking);
                mainframe.Restore();
                return;
            }
            chatting = true;
            ears.Muted = false;
            if (!testing && !muteKey) muteKey = Native.RegisterHotKey(Handle, MuteKeyId, 0x0001 | 0x0002 | 0x4000, 0x4E);
            pill.SetChat(true, false);
            pillUntil = clock.Elapsed.TotalSeconds + 1.5;
            brain.Cancel();
            CloseTalk();
            OpenLink();
        }

        private void StartLocal()
        {
            chatting = true;
            local = true;
            earsShut = false;
            ears.Muted = false;
            if (!testing && !muteKey) muteKey = Native.RegisterHotKey(Handle, MuteKeyId, 0x0001 | 0x0002 | 0x4000, 0x4E);
            pill.SetChat(true, false);
            pillUntil = clock.Elapsed.TotalSeconds + 1.5;
            brain.Cancel();
            CloseTalk();
            if (whisper.Ready) { ListenLocally(); return; }
            heard = "(voice)";
            box.Prompt = "> you: ";
            Begin(++asking, "Getting my ears first, choom. It's a " + Whisper.Download + " download, then talk and I'll type back.", TalkState.Talking, null);
            mainframe.VoiceNote("Downloading her ears (" + Whisper.Download + ")...", false);
            if (fetchingEars) return;
            fetchingEars = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null;
                try { whisper.Install(delegate(string what, long have, long size) { Post(delegate { if (local) mainframe.VoiceNote(what + " " + (int)(100 * have / Math.Max(1, size)) + "%", false); }); }); }
                catch (Exception error) { problem = error.Message; }
                fetchingEars = false;
                Post(delegate
                {
                    if (!chatting || !local) return;
                    if (problem == null) { ListenLocally(); return; }
                    StopChat();
                    mainframe.VoiceNote(problem, true);
                    heard = "(voice)";
                    box.Prompt = "> you: ";
                    Begin(++asking, "Couldn't get my ears. The reason is under VOICE in Capri Mainframe. Type to me for now." + Brain.Stuck, TalkState.Talking, null);
                });
            });
        }

        private void ListenLocally()
        {
            lock (waking) { utterance = null; quietRun = 0; loudRun = 0; preroll.Clear(); }
            try { if (!testing) ears.Start(); }
            catch (VoiceException error)
            {
                StopChat();
                mainframe.VoiceNote(error.Message, true);
                heard = "(voice)";
                box.Prompt = "> you: ";
                Begin(++asking, error.Message + " It's in Capri Mainframe.", TalkState.Talking, null);
                return;
            }
            mainframe.VoiceNote(muteKey ? "Free voice chat on. Talk, she types back. Ctrl + Alt + N mutes your mic." : "Free voice chat on. Talk, she types back.", false);
        }

        private void Gather(short[] chunk)
        {
            bool loud = Speaker.Loudness(chunk, 0, chunk.Length) >= WakeLevel;
            lock (waking)
            {
                if (earsShut || transcribing) { utterance = null; preroll.Clear(); loudRun = 0; quietRun = 0; return; }
                if (utterance == null)
                {
                    preroll.Add(chunk);
                    if (preroll.Count > 4) preroll.RemoveAt(0);
                    loudRun = loud ? loudRun + 1 : 0;
                    if (loudRun < 2) return;
                    utterance = new System.Collections.Generic.List<short[]>(preroll);
                    preroll.Clear();
                    loudRun = 0;
                    quietRun = 0;
                    return;
                }
                utterance.Add(chunk);
                quietRun = loud ? 0 : quietRun + 1;
                if (quietRun < 9 && utterance.Count < 150) return;
                short[][] taken = utterance.ToArray();
                utterance = null;
                quietRun = 0;
                transcribing = true;
                Post(delegate { Transcribe(taken); });
            }
        }

        private void Transcribe(short[][] taken)
        {
            int total = 0;
            foreach (short[] piece in taken) total += piece.Length;
            if (!local || !chatting || total < Ears.Rate * 6 / 10) { transcribing = false; return; }
            short[] samples = new short[total];
            int at = 0;
            foreach (short[] piece in taken) { Array.Copy(piece, 0, samples, at, piece.Length); at += piece.Length; }
            int ticket = chatTicket;
            mainframe.VoiceNote("Hearing you...", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null;
                string problem = null;
                try { text = whisper.Text(samples); }
                catch (Exception error) { problem = Voice.Short(error.Message); }
                Post(delegate
                {
                    transcribing = false;
                    if (ticket != chatTicket || !chatting || !local) return;
                    if (problem != null) { mainframe.VoiceNote(problem, true); return; }
                    mainframe.VoiceNote("Free voice chat on. Talk, she types back.", false);
                    if (text.Length > 0) Hear(text, text, "> you: ");
                });
            });
        }

        private void OpenLink()
        {
            mainframe.VoiceNote("Opening voice chat...", false);
            int ticket = ++chatTicket;
            string key = voice.Key;
            string voiceId = voice.VoiceId;
            string agentId = preferences.AgentId;
            bool stale = preferences.AgentVersion < Agent.Version;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string url = null;
                string problem = null;
                try
                {
                    if (agentId == null) agentId = Agent.Create(key, voiceId);
                    else if (stale)
                    {
                        try { Agent.Tune(key, agentId); }
                        catch (VoiceException error)
                        {
                            if (error.Status != 404 && error.Status != 400) throw;
                            agentId = Agent.Create(key, voiceId);
                        }
                    }
                    try { url = Agent.Address(key, agentId); }
                    catch (VoiceException error)
                    {
                        if (error.Status != 404 && error.Status != 400) throw;
                        agentId = Agent.Create(key, voiceId);
                        url = Agent.Address(key, agentId);
                    }
                }
                catch (VoiceException error) { problem = error.Message; }
                catch (Exception error) { problem = Voice.Short("Voice chat failed: " + error.Message); }
                Post(delegate
                {
                    if (ticket != chatTicket || !chatting) return;
                    if (problem != null) { ChatFailed(problem); return; }
                    if (preferences.AgentId != agentId || preferences.AgentVersion != Agent.Version) { preferences.AgentId = agentId; preferences.AgentVersion = Agent.Version; SavePreferences(); }
                    Connect(url, voiceId);
                });
            });
        }

        private void Connect(string url, string voiceId)
        {
            Agent opened = new Agent();
            opened.Act = Hands.Do;
            opened.Opened += delegate(int rate) { Post(delegate { if (agent == opened) ChatOpened(rate); }); };
            opened.Heard += delegate(string text) { Post(delegate { if (agent == opened) ChatHeard(text); }); };
            opened.Replied += delegate(string text) { Post(delegate { if (agent == opened) ChatReplied(text); }); };
            opened.Audio += delegate(short[] samples) { Post(delegate { if (agent != opened || dropping) return; speaker.Play(samples); if (!frames.Enabled && !hidden) frames.Start(); }); };
            opened.Interrupted += delegate { Post(delegate { if (agent == opened) speaker.Clear(); }); };
            opened.Closed += delegate(string problem) { Post(delegate { if (agent != opened) return; if (problem != null) ChatFailed(problem); else Rest(); }); };
            agent = opened;
            opened.Start(url, voiceId);
        }

        private void ChatOpened(int rate)
        {
            try
            {
                speaker.Open(rate);
                if (!testing) ears.Start();
            }
            catch (VoiceException error) { ChatFailed(error.Message); return; }
            spoke = false;
            checkedIn = false;
            quietSince = -1;
            lock (waking)
            {
                wokeAt = backlog != null ? clock.Elapsed.TotalSeconds : -1;
                if (backlog != null) foreach (short[] held in backlog) agent.Send(held);
                backlog = null;
            }
            mainframe.VoiceNote(muteKey ? "Voice chat on. Ctrl + Alt + N mutes your mic." : "Voice chat on. Talk to her.", false);
        }

        private void ChatFailed(string problem)
        {
            StopChat();
            mainframe.VoiceNote(problem, true);
            if (System.Text.RegularExpressions.Regex.IsMatch(problem, "quota_exceeded|run out of credits|out of ElevenLabs credits", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                preferences.FreeVoice = true;
                SavePreferences();
                mainframe.Sync();
                StartLocal();
                if (!whisper.Ready) return;
                heard = "(voice)";
                box.Prompt = "> you: ";
                Begin(++asking, "Out of ElevenLabs credits, choom, so I ticked Free voice chat. Talk and I'll type back.", TalkState.Talking, null);
                return;
            }
            heard = "(voice)";
            box.Prompt = "> you: ";
            Begin(++asking, "Can't open my voice link. Right-click me and open Capri Mainframe. The reason and the fix are under VOICE." + Brain.Stuck, TalkState.Dormant, null);
        }

        private void ChatHeard(string text)
        {
            if (!chatting) return;
            spoke = true;
            checkedIn = false;
            wokeAt = -1;
            int ticket = ++asking;
            heard = text;
            box.Prompt = "> you: ";
            string blocked = Safety.Screen(text);
            dropping = blocked != null;
            if (dropping) { speaker.Clear(); Begin(ticket, blocked, TalkState.Talking, null); return; }
            reply = null;
            replyDone = -1;
            Say(null);
            talkState = TalkState.Thinking;
            box.Leaving = -1;
            UpdateAnimation();
        }

        private void ChatReplied(string text)
        {
            if (!chatting) return;
            checkedIn = !spoke;
            spoke = false;
            if (dropping) return;
            string said = Safety.Filter(Voice.Spoken(text));
            if (said != Voice.Spoken(text)) { dropping = true; speaker.Clear(); }
            reply = said;
            replyStart = clock.Elapsed.TotalSeconds;
            replyDone = -1;
            talkState = TalkState.Talking;
            box.Leaving = -1;
            pace = Agent.Pace;
            Say(null);
        }

        private void StopChat()
        {
            if (!chatting) return;
            chatting = false;
            if (muteKey) Native.UnregisterHotKey(Handle, MuteKeyId);
            muteKey = false;
            chatTicket++;
            Agent closing = agent;
            agent = null;
            if (closing != null) closing.Stop();
            ears.Stop();
            ears.Muted = false;
            speaker.Clear();
            dropping = false;
            checkedIn = false;
            quietSince = -1;
            wokeAt = -1;
            lock (waking) { resting = false; backlog = null; preroll.Clear(); loudRun = 0; utterance = null; quietRun = 0; }
            local = false;
            earsShut = false;
            transcribing = false;
            pill.SetChat(false, false);
            ShowVoice();
        }

        private void CheckChat(double now)
        {
            bool due = agent != null && (checkedIn || (wokeAt >= 0 && now - wokeAt >= 8));
            if (!due || speaker.Busy) { quietSince = -1; return; }
            if (quietSince < 0) quietSince = now;
            else if (now - quietSince >= 1.2) Rest();
        }

        private void Rest()
        {
            if (!chatting) return;
            Agent closing = agent;
            agent = null;
            chatTicket++;
            if (closing != null) closing.Stop();
            dropping = false;
            checkedIn = false;
            quietSince = -1;
            wokeAt = -1;
            lock (waking) { resting = true; backlog = null; preroll.Clear(); loudRun = 0; }
            mainframe.VoiceNote("Voice chat resting. Talk and she'll pick up.", false);
        }

        private void Wake()
        {
            if (!chatting || agent != null) { lock (waking) backlog = null; return; }
            OpenLink();
        }

        private void ToggleMic()
        {
            if (!chatting) return;
            ears.Muted = !ears.Muted;
            pill.SetChat(true, ears.Muted);
        }

        private void Listen(short[] chunk)
        {
            if (local) { Gather(chunk); return; }
            lock (waking)
            {
                if (resting)
                {
                    preroll.Add(chunk);
                    if (preroll.Count > 5) preroll.RemoveAt(0);
                    loudRun = !speaker.Busy && Speaker.Loudness(chunk, 0, chunk.Length) >= WakeLevel ? loudRun + 1 : 0;
                    if (loudRun < 2) return;
                    resting = false;
                    loudRun = 0;
                    backlog = new System.Collections.Generic.List<short[]>(preroll);
                    preroll.Clear();
                    Post(Wake);
                    return;
                }
                if (backlog != null)
                {
                    backlog.Add(chunk);
                    if (backlog.Count > 80) backlog.RemoveAt(0);
                    return;
                }
            }
            Agent open = agent;
            if (open != null) open.Send(speaker.Busy ? new short[chunk.Length] : chunk);
        }

        internal void Hear(string text) { Hear(text, text, "> you: "); }

        private void Hear(string text, string shown, string prompt)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 240) text = text.Substring(0, 240);
            if (hidden) SetHidden(false);
            brain.Cancel();
            voice.Stop();
            int ticket = ++asking;
            heard = shown;
            box.Prompt = prompt;
            string blocked = Safety.Screen(text);
            if (blocked != null) { Answer(ticket, blocked, TalkState.Talking); return; }
            Func<string, string> think = Think;
            if (think == null)
            {
                if (brain.Model == null) { Answer(ticket, Brain.Offline, TalkState.Dormant); FindBrain(); mainframe.Restore(); return; }
                think = brain.Ask;
            }
            reply = null;
            replyDone = -1;
            Say(null);
            talkState = TalkState.Thinking;
            thinkingSince = clock.Elapsed.TotalSeconds;
            box.Leaving = -1;
            UpdateAnimation();
            if (!testing && voice.Key == null && kokoro.Installed && !preferences.Quiet) ThreadPool.QueueUserWorkItem(delegate { try { kokoro.Warm(); } catch (Exception) { } });
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said;
                TalkState state = TalkState.Talking;
                string problem = null;
                try { said = Safety.Filter(think(text)); }
                catch (BrainException error) { said = error.Message == Brain.Offline ? Brain.Offline : error.Told ? "[SIGNAL CORRUPTED] " + error.Message + " It's under BRAIN in Capri Mainframe." + Brain.Stuck : "[SIGNAL CORRUPTED] My brain threw an error. Right-click me, open Capri Mainframe and press Refresh. The error shows under BRAIN." + Brain.Stuck; problem = error.Message; state = TalkState.Dormant; }
                catch (Exception error) { said = Brain.Explain(error, brain.Remote); state = TalkState.Dormant; }
                if (said == null) return;
                try
                {
                    if (!IsDisposed) BeginInvoke((Action)delegate
                    {
                        if (problem != null && ticket == asking) mainframe.ShowProblem(problem.Length > 60 ? problem.Substring(0, 57) + "..." : problem);
                        Answer(ticket, said, state);
                        if (said == Brain.Offline) FindBrain();
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void Answer(int ticket, string said, TalkState state)
        {
            if (ticket != asking || IsDisposed) return;
            voice.Stop();
            Func<string, byte[]> speak = preferences.Quiet ? null : Speak;
            if (speak == null && !testing && voice.Ready && !preferences.Quiet) speak = voice.Speak;
            if (speak == null && !testing && voice.Key == null && kokoro.Installed && !preferences.Quiet) speak = kokoro.Speak;
            string words = Voice.Spoken(said);
            if (speak == null || words.Length == 0) { Begin(ticket, said, state, null); return; }
            reply = null;
            replyDone = -1;
            Say(null);
            talkState = TalkState.Thinking;
            box.Leaving = -1;
            UpdateAnimation();
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] wav = null;
                string problem = null;
                try { wav = speak(words); }
                catch (VoiceException error) { problem = error.Message; }
                catch (Exception error) { problem = Voice.Short("Voice glitched: " + error.Message); }
                Post(delegate
                {
                    if (problem != null && ticket == asking) mainframe.VoiceNote(problem, true);
                    Begin(ticket, said, state, wav);
                });
            });
        }

        private void Begin(int ticket, string said, TalkState state, byte[] wav)
        {
            if (ticket != asking || IsDisposed) return;
            reply = said;
            replyStart = clock.Elapsed.TotalSeconds;
            replyDone = -1;
            talkState = state;
            box.Leaving = -1;
            pace = LettersPerSecond;
            Say(said);
            double seconds = wav == null ? 0 : Voice.Seconds(wav);
            if (seconds < 0.2) return;
            pace = Math.Max(8, said.Length / seconds);
            mouths = Voice.Mouths(wav, MouthRate);
            voiceUntil = clock.Elapsed.TotalSeconds + seconds + 0.6;
            if (!testing) voice.Play(wav);
        }

        private void CloseTalk()
        {
            voice.Stop();
            voiceUntil = 0;
            talkState = TalkState.Off;
            reply = null;
            replyDone = -1;
            box.Opened = -1;
            box.Leaving = -1;
            if (box.Visible) box.Hide();
            speech = null;
        }

        private void UpdateTalk(double now)
        {
            if (talkState == TalkState.Off) { if (box.Visible) box.Hide(); return; }
            int revealed = reply == null ? 0 : (int)((now - replyStart) * pace);
            if (reply != null && revealed >= reply.Length && replyDone < 0)
            {
                replyDone = now;
                if (talkState == TalkState.Talking) talkState = TalkState.Online;
            }
            if (replyDone >= 0 && box.Leaving < 0 && !typer.Visible && now > replyDone + Math.Min(14, 4 + reply.Length * 0.05)) box.Leaving = now;
            if (box.Leaving >= 0 && now - box.Leaving > 0.25) { CloseTalk(); return; }
            if (!box.Visible && box.Opened < 0) box.Opened = now;
            box.State = talkState;
            box.Heard = heard;
            box.Reply = talkState == TalkState.Thinking ? null : reply;
            box.Revealed = revealed;
            float s = TalkScale();
            Point head = Head();
            Point feet = Feet();
            Rectangle area = Screen.FromPoint(head).WorkingArea;
            box.Update(now, s, head, new Point(feet.X, feet.Y + (int)(44 * s)), area);
            if (typer.Visible) typer.Follow(box.PanelOnScreen, area);
        }

        private void UpdateAnimation()
        {
            if (!preferences.Motion && !riding && seat == null && !falling && clip != glitch && clip != dangle && clip != wake) Play(idle);
            if ((preferences.Motion || riding || seat != null || falling || clip != idle || speech != null || talkState != TalkState.Off) && !hidden) frames.Start(); else frames.Stop();
            RenderFrame();
        }

        private void RenderFrame()
        {
            if (!IsHandleCreated || hidden || IsDisposed) return;
            double now = clock.Elapsed.TotalSeconds;
            int cell = Advance(now);
            Clip shown = clip;
            if ((clip == idle || clip == perch) && blinkWith != null && cell >= BlinkFrom && cell < BlinkFrom + blinkWith.Count) { shown = blinkWith; cell -= BlinkFrom; }
            int talk = MouthCell(now, shown);
            Bitmap next = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(next)) Draw(graphics, shown, cell, talk);
            Native.DrawLayered(Handle, next, anchor);
            Bitmap old = frame;
            frame = next;
            if (old != null) old.Dispose();
            UpdateTalk(now);
            if (pill.Visible) UpdatePill(now);
        }

        internal void Say(string text)
        {
            speech = string.IsNullOrEmpty(text) ? null : text;
            speechStart = clock.Elapsed.TotalSeconds;
            mouths = null;
            if (speech != null && (clip == sleep || clip == wake || clip == glitch || clip == grin)) Play(idle);
            UpdateAnimation();
        }

        private int MouthCell(double now, Clip shown)
        {
            if (chatting && speaker.Busy) return clip == idle && shown != blinkGlitch ? speaker.Shape() : -1;
            if (speech == null) return -1;
            int step = (int)((now - speechStart) * MouthRate);
            if (mouths != null)
            {
                if (step >= mouths.Length) { speech = null; mouths = null; return -1; }
                return clip == idle && shown != blinkGlitch ? mouths[step] : -1;
            }
            int letter = (int)(step / MouthRate * LettersPerSecond);
            if (letter >= speech.Length) { speech = null; return -1; }
            return clip == idle && shown != blinkGlitch ? MouthFor(speech[letter], step) : -1;
        }

        internal static int MouthFor(char letter, int step)
        {
            char lower = char.ToLowerInvariant(letter);
            if ("aeo".IndexOf(lower) >= 0) return char.IsUpper(letter) ? 4 : 3;
            if ("iuy".IndexOf(lower) >= 0) return 2;
            if ("mbp".IndexOf(lower) >= 0 || !char.IsLetter(letter)) return 0;
            return step % 2 == 1 ? 1 : 2;
        }

        private void Draw(Graphics graphics, Clip shown, int cell, int talk = -1)
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            double now = clock.Elapsed.TotalSeconds;
            bool settled = preferences.Motion && !dragging && clip != dangle && clip != sleep;
            float breathe = settled ? (float)Math.Sin(now * 1.75) * 2.2f : 0;
            float tilt = settled ? (float)Math.Sin(now * 0.95) * 0.65f : 0;
            graphics.TranslateTransform(Width / 2f, Height / 2f + breathe);
            graphics.RotateTransform(tilt + (float)leanAngle);
            int width = shown.Strip.Width / shown.Count;
            float tall = preferences.Size * shown.Height;
            float wide = tall * width / shown.Strip.Height;
            graphics.DrawImage(shown.Strip, new RectangleF(-wide / 2f, -tall / 2f, wide, tall), new RectangleF(cell * width, 0, width, shown.Strip.Height), GraphicsUnit.Pixel);
            if (talk >= 0) graphics.DrawImage(mouth.Strip, new RectangleF(-wide / 2f, -tall / 2f, wide, tall), new RectangleF(talk * width, 0, width, mouth.Strip.Height), GraphicsUnit.Pixel);
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
            brain.Cancel();
            StopChat();
            if (talkKey) Native.UnregisterHotKey(Handle, TalkKeyId);
            talkKey = false;
            speaker.Close();
            voice.Stop();
            engine.Cancel = true;
            kokoro.Cancel = true;
            engine.Stop();
            mainframe.Quitting = true;
            mainframe.Close();
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
                pill.Dispose();
                box.Dispose();
                typer.Dispose();
                mainframe.Dispose();
                foreach (Clip owned in new[] { idle, blink, blinkGlitch, grin, glitch, dangle, sleep, wake, perch, perchBlink, perchBlinkGlitch, perchGlitch, mouth }) owned.Strip.Dispose();
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
                int thoughts = 0;
                byte[] tone = null;
                string voiced = null;
                System.Collections.Generic.List<string> asked = new System.Collections.Generic.List<string>();
                HttpListener fakeAgent = null;
                string agentHost = null;
                Action<string> handsRun = null;
                Func<System.Collections.Generic.List<InstalledApp>> handsCatalog = null;
                Form backdrop = null;
                System.Collections.Generic.List<string> whispered = new System.Collections.Generic.List<string>();
                Func<string, string, string, string> realWhisper = null;
                Func<string, string> realThink = null;
                System.Collections.Generic.List<string> checks = new System.Collections.Generic.List<string>();
                test.Tick += delegate
                {
                    try
                    {
                        if (step == 0)
                        {
                            preferences.NoSitting = true;
                            checks.Add("PASS native window visible: " + Visible);
                            if (!mainframe.Visible || !mainframe.ShowInTaskbar || mainframe.WindowState != FormWindowState.Minimized) throw new Exception("Mainframe did not start minimized in the taskbar.");
                            if (!Visible || frame == null) throw new Exception("Native window did not render.");
                            if ((Native.GetWindowLong(Handle, -20) & 0x00080000) == 0) throw new Exception("Window is not layered.");
                            if (frame.GetPixel(0, 0).A != 0) throw new Exception("Frame corner is opaque.");
                            checks.Add("PASS per-pixel alpha and layered window");
                            Point before = anchor;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            Point moved = new Point(pointerStart.X - 80, pointerStart.Y - 35);
                            MovePointer(moved);
                            if (clip != dangle || !frames.Enabled) throw new Exception("Drag did not play the dangle clip.");
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (anchor == before || pendingClick || riding) throw new Exception("Drag did not change position or did not cancel click.");
                            RenderFrame();
                            if (clip != idle) throw new Exception("Drop did not return to idle.");
                            checks.Add("PASS drag handler changed position, dangled and settled");
                            int previous = clicks;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!pendingClick || clicks != previous) throw new Exception("Single click was not delayed.");
                            OnClickDelay();
                            if (clicks != previous + 1 || !frames.Enabled || clip != glitch) throw new Exception("Click reaction did not start.");
                            clipStart -= 1;
                            RenderFrame();
                            if (clip != idle) throw new Exception("Glitch did not return to idle.");
                            checks.Add("PASS delayed single click glitched once and returned to idle");
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!riding || (Native.GetWindowLong(Handle, -20) & 0x20) == 0) throw new Exception("Double click did not perch with click-through.");
                            if (clip != perchGlitch) throw new Exception("Perching did not glitch on.");
                            clipStart -= 1;
                            RenderFrame();
                            if (clip != perch) throw new Exception("Perch glitch did not settle into the seated loop.");
                            checks.Add("PASS double click perched with click-through, glitched on and settled into the seated loop");
                            checks.Add(hotkey ? "PASS Ctrl+Alt+C registered while perched" : "LIMIT Ctrl+Alt+C taken; tray Hop off fallback");
                            if (IsDoubleClick(Point.Empty, new Point(200, 200), 10)) throw new Exception("Double click predicate failed.");
                            Message key = Message.Create(Handle, 0x0312, new IntPtr(DismountId), IntPtr.Zero);
                            WndProc(ref key);
                            if (riding || hotkey || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hotkey did not hop off.");
                            if (clip != grin) throw new Exception("Hop off did not grin.");
                            checks.Add("PASS Ctrl+Alt+C message hopped off, grinned and released the hotkey");
                            rideItem.PerformClick();
                            if (!riding || rideItem.Text != "Hop off") throw new Exception("Menu perch failed.");
                            rideItem.PerformClick();
                            if (riding || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hop off menu did not clean up.");
                            checks.Add("PASS tray Perch on cursor and Hop off");
                            System.Collections.Generic.List<int[]> spans = Ledges.Open(0, 100, 10, new[] { new Rectangle(20, 0, 30, 50), new Rectangle(90, 5, 40, 40), new Rectangle(0, 11, 100, 40) });
                            if (spans.Count != 2 || spans[0][0] != 0 || spans[0][1] != 20 || spans[1][0] != 50 || spans[1][1] != 90) throw new Exception("Ledge covering math failed.");
                            Point home = anchor;
                            sitItem.PerformClick();
                            if (preferences.NoSitting || !sitItem.Checked) throw new Exception("Sit on windows menu did not turn sitting on.");
                            using (Shelf shelf = new Shelf())
                            {
                                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                                shelf.Bounds = new Rectangle(work.Left + work.Width / 2 - 220, work.Top + work.Height / 2 - 40, 440, 180);
                                shelf.Show();
                                anchor = Seated(shelf.Left + 220, shelf.Top - 12);
                                if (!Drop() || !falling) throw new Exception("Dropping her just above a window did not start a fall.");
                                for (int i = 0; i < 60 && falling; i++) Tumble(fallStep + 0.033);
                                if (seat == null || seat.Window != shelf.Handle || Hip().Y != shelf.Top || clip != perchGlitch) throw new Exception("She did not land on the window's top edge.");
                                clipStart -= 1;
                                RenderFrame();
                                if (clip != perch) throw new Exception("Landing did not settle into the seated loop.");
                                int seatX = Hip().X;
                                shelf.Location = new Point(shelf.Left + 40, shelf.Top + 25);
                                HoldSeat(clock.Elapsed.TotalSeconds);
                                if (Hip().X != seatX + 40 || Hip().Y != shelf.Top) throw new Exception("She did not ride along when the window moved.");
                                if (Spots().Exists(candidate => candidate.Window == shelf.Handle)) throw new Exception("Wander offered the window she already sits on.");
                                for (int i = 0; i < 5; i++)
                                {
                                    Ledge was = seat;
                                    if (!Wander() || seat == null || falling || Hip().Y != seat.Y || (seat.Window == was.Window && seat.Floor == was.Floor)) throw new Exception("Wander did not land her on another window or the taskbar.");
                                }
                                StandUp();
                                if (!Spots().Exists(candidate => candidate.Window == shelf.Handle)) throw new Exception("Wander did not offer an open window to hop onto.");
                                anchor = Seated(shelf.Left + 220, shelf.Top - 12);
                                if (!Drop()) throw new Exception("She could not get back onto the window after wandering.");
                                for (int i = 0; i < 60 && falling; i++) Tumble(fallStep + 0.033);
                                if (seat == null || seat.Window != shelf.Handle) throw new Exception("She did not land back on the window after wandering.");
                                wanderItem.PerformClick();
                                bool wanderOn = preferences.Wander && wanderItem.Checked;
                                wanderItem.PerformClick();
                                if (!wanderOn || preferences.Wander || wanderItem.Checked || nextWander >= 0) throw new Exception("Wander around did not toggle.");
                                checks.Add("PASS wander: hops onto another open window or the taskbar each time, never the one she's on, and the menu toggles it");
                                if (Ledges.Covered(seat, Hip().X, Skip())) throw new Exception("Top window reported as covered.");
                                using (Shelf ghost = new Shelf(0x80000 | 0x80))
                                {
                                    ghost.Bounds = Screen.PrimaryScreen.Bounds;
                                    ghost.Show();
                                    if (Ledges.Covered(seat, Hip().X, Skip())) throw new Exception("An invisible overlay knocked her off.");
                                }
                                using (Shelf tip = new Shelf())
                                {
                                    tip.Bounds = new Rectangle(Hip().X - 60, shelf.Top - 12, 120, 40);
                                    tip.Show();
                                    if (Ledges.Covered(seat, Hip().X, Skip())) throw new Exception("A popup like a tooltip or menu knocked her off.");
                                }
                                Ledge held = seat;
                                Point sat = anchor;
                                using (Shelf cover = new Shelf())
                                {
                                    cover.FormBorderStyle = FormBorderStyle.FixedSingle;
                                    cover.Bounds = new Rectangle(Hip().X - 160, shelf.Top - 50, 320, 200);
                                    cover.Show();
                                    double over = clock.Elapsed.TotalSeconds;
                                    CheckSeat(over);
                                    CheckSeat(over + 0.4);
                                    if (seat == null || falling) throw new Exception("A window passing over her knocked her off straight away.");
                                    CheckSeat(over + 0.8);
                                    if (falling || seat != null || anchor != sat || clip != glitch) throw new Exception("A window parked over her seat did not stand her up where she sat.");
                                }
                                Land(held);
                                clipStart -= 1;
                                RenderFrame();
                                if (seat != held || falling) throw new Exception("She did not get back on the window.");
                                if (Ledges.Covered(seat, Hip().X, Skip()) || seat.Window != shelf.Handle) throw new Exception("Her own Mainframe or a closed window still covered her.");
                                int originX = shelf.Left - 80;
                                int originY = anchor.Y - 20;
                                using (Bitmap still = new Bitmap(shelf.Width + 160, shelf.Bottom - originY + 20, PixelFormat.Format32bppArgb))
                                using (Graphics graphics = Graphics.FromImage(still))
                                using (SolidBrush fill = new SolidBrush(shelf.BackColor))
                                {
                                    graphics.Clear(Color.FromArgb(214, 220, 228));
                                    graphics.FillRectangle(fill, shelf.Left - originX, shelf.Top - originY, shelf.Width, shelf.Height);
                                    graphics.TranslateTransform(anchor.X - originX, anchor.Y - originY);
                                    Draw(graphics, perch, 0);
                                    still.Save(Path.Combine(output, "sit.png"), ImageFormat.Png);
                                }
                                shelf.Hide();
                                double gone = clock.Elapsed.TotalSeconds;
                                HoldSeat(gone);
                                if (falling || seat == null) throw new Exception("A window blinking out for a moment knocked her off.");
                                HoldSeat(gone + 0.4);
                                if (falling || seat != null || anchor != sat || clip != glitch) throw new Exception("She did not stand up where she sat when the window went away.");
                                clipStart -= 1;
                                RenderFrame();
                                if (clip != idle || anchor != sat) throw new Exception("Standing up did not settle into idle in place.");
                                checks.Add("PASS dropped above a window she fell, sat on its top edge, rode along when it moved, ignored popups and windows passing over, and stood up where she sat when a window parked over her or it closed");
                            }
                            checks.Add("PASS ledge covering math; desktop icons read: " + Ledges.Icons().Count);
                            seat = null;
                            sitItem.PerformClick();
                            if (!preferences.NoSitting || sitItem.Checked) throw new Exception("Sit on windows menu did not turn sitting off.");
                            seat = null;
                            hanging = false;
                            Play(idle);
                            Point hipBefore = Hip();
                            SitHere();
                            if (seat == null || !seat.Free || clip != perchGlitch || Math.Abs(Hip().X - hipBefore.X) > 2 || Hip().Y != hipBefore.Y) throw new Exception("Sit here did not sit her where she was.");
                            CheckSeat(clock.Elapsed.TotalSeconds + 5);
                            HoldSeat(clock.Elapsed.TotalSeconds + 5);
                            if (seat == null) throw new Exception("A free seat got knocked away.");
                            menu.Show(Cursor.Position);
                            bool sitShown = sitHereItem.Text == "Stand up";
                            menu.Close();
                            if (!sitShown) throw new Exception("The menu did not offer Stand up while she sat: " + sitHereItem.Text);
                            sitHereItem.PerformClick();
                            if (seat != null) throw new Exception("Stand up did not stand her up.");
                            hangHereItem.PerformClick();
                            Advance(clock.Elapsed.TotalSeconds + 3);
                            if (!hanging || clip != dangle || seat != null) throw new Exception("Hang here did not keep her hanging.");
                            React();
                            if (hanging || clip == dangle) throw new Exception("A click did not make her let go.");
                            Play(idle);
                            checks.Add("PASS sit and hang anywhere: Sit here sits her on the spot and nothing knocks her off, the menu offers Stand up, Hang here keeps her dangling, a click lets go");
                            anchor = home;
                            Play(idle);
                            rideItem.PerformClick();
                            visibilityItem.PerformClick();
                            if (Visible || !hidden || riding || hotkey) throw new Exception("Hide menu did not hide window or clean up perch.");
                            checks.Add("PASS hide menu");
                            restore.Set();
                        }
                        else if (step == 1)
                        {
                            if (!Visible || hidden || clip != grin) throw new Exception("Second-launch restore signal did not show Capri grinning.");
                            checks.Add("PASS restore signal");
                            if (!mainframe.ShowInTaskbar || !mainframe.Visible) throw new Exception("Mainframe is not in the taskbar.");
                            if (Native.IdleMilliseconds() < 0) throw new Exception("Idle time unavailable.");
                            Play(idle);
                            CheckIdle(119000);
                            if (clip != idle) throw new Exception("Slept too early.");
                            CheckIdle(130000);
                            if (clip != sleep) throw new Exception("Did not sleep after two quiet minutes.");
                            CheckIdle(0);
                            if (clip != wake) throw new Exception("Did not wake with the boot glitch.");
                            clipStart -= 1;
                            RenderFrame();
                            if (clip != idle) throw new Exception("Boot glitch did not settle into idle.");
                            Play(idle);
                            checks.Add("PASS sleeps after two quiet minutes, boots awake and settles into idle");
                            if (MouthFor('a', 0) != 3 || MouthFor('O', 0) != 4 || MouthFor('i', 0) != 2 || MouthFor('m', 0) != 0 || MouthFor(' ', 0) != 0 || MouthFor('!', 0) != 0 || MouthFor('k', 1) != 1 || MouthFor('k', 2) != 2) throw new Exception("Mouth shapes picked the wrong letters.");
                            const string spoken = "Hey OK, bye!";
                            int mouthPixels = 0;
                            Say(spoken);
                            if (speech != spoken || !frames.Enabled) throw new Exception("Say did not start talking.");
                            double said = clock.Elapsed.TotalSeconds;
                            speechStart = said;
                            if (MouthCell(said, idle) != 2) throw new Exception("First letter did not open her teeth.");
                            speechStart = said - 2.5 / MouthRate;
                            if (MouthCell(said, idle) != 4) throw new Exception("Capital O did not scream.");
                            if (MouthCell(said, blinkGlitch) != -1) throw new Exception("Mouth drew over a glitched blink.");
                            using (Bitmap quiet = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
                            using (Bitmap open = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
                            {
                                bool motion = preferences.Motion;
                                preferences.Motion = false;
                                using (Graphics graphics = Graphics.FromImage(quiet)) Draw(graphics, idle, 0);
                                using (Graphics graphics = Graphics.FromImage(open)) Draw(graphics, idle, 0, 3);
                                preferences.Motion = motion;
                                for (int x = 0; x < Width; x++) for (int y = 0; y < Height; y++) if (Math.Abs(quiet.GetPixel(x, y).GetBrightness() - open.GetPixel(x, y).GetBrightness()) > 0.3f) mouthPixels++;
                                if (mouthPixels < 4 || mouthPixels > 1200) throw new Exception("Open mouth changed " + mouthPixels + " pixels.");
                            }
                            CheckIdle(130000);
                            if (clip != idle) throw new Exception("Fell asleep mid-sentence.");
                            Play(idle);
                            Say(spoken);
                            nextGlitch = 0;
                            clipStart -= 2.1;
                            RenderFrame();
                            if (clip != idle) throw new Exception("Glitched mid-sentence.");
                            speechStart = clock.Elapsed.TotalSeconds - spoken.Length / LettersPerSecond - 1;
                            RenderFrame();
                            if (speech != null || MouthCell(clock.Elapsed.TotalSeconds, idle) != -1) throw new Exception("Did not stop talking at the end of the line.");
                            nextGlitch = clock.Elapsed.TotalSeconds + 60;
                            checks.Add("PASS talks: letter mouths, typing-synced flaps, jaw overlay (" + mouthPixels + " px), no sleep or glitch mid-sentence, stops at the end");
                            Version mine = Assembly.GetExecutingAssembly().GetName().Version;
                            if (!IsNewer("v9.0.0", mine) || IsNewer("v" + mine.ToString(3), mine) || IsNewer("v0.0.1", mine) || IsNewer("nonsense", mine) || IsNewer(null, mine)) throw new Exception("Update version compare failed.");
                            long now = DateTime.UtcNow.Ticks;
                            if (!UpdateDue(0, now) || UpdateDue(now - TimeSpan.TicksPerHour, now) || !UpdateDue(now - TimeSpan.TicksPerDay - 1, now)) throw new Exception("Daily update timing failed.");
                            using (MemoryStream sample = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"tag_name\":\"v9.0.0\",\"html_url\":\"" + ReleasesPage + "tag/v9.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Capri-Setup.exe\"}],\"author\":{\"login\":\"capri\"}}")))
                                ShowUpdate(ParseRelease(sample), false);
                            if (!menu.Items.Contains(updateItem) || updatePage != ReleasesPage + "tag/v9.0.0" || updateItem.Text != "Download Capri 9.0.0") throw new Exception("A newer release was not offered.");
                            updatePage = null;
                            balloonPage = null;
                            updateItem.Text = "Check for updates";
                            checks.Add("PASS update check: version compare, daily timing, GitHub release parsing and download offer");
                            Point bounded = KeepOnScreen(new Point(int.MaxValue / 4, int.MaxValue / 4));
                            Rectangle corner = Screen.FromPoint(new Point(bounded.X + Width / 2, bounded.Y + Height / 2)).WorkingArea;
                            Rectangle standing = BodyOnScreen(bounded, Body);
                            if (!corner.Contains(standing) || corner.Right - standing.Right > 2 || corner.Bottom - standing.Bottom > 2) throw new Exception("She can't stand in the screen corner: " + standing + " in " + corner);
                            Rectangle leftmost = BodyOnScreen(Within(new Point(corner.Left - 5000, bounded.Y), corner, Body), Body);
                            if (leftmost.Left < corner.Left || leftmost.Left - corner.Left > 2) throw new Exception("She can't reach the left edge: " + leftmost + " in " + corner);
                            Point stoodAt = anchor;
                            anchor = bounded;
                            Land(new Ledge { Floor = true, Left = corner.Left, Right = corner.Right, Y = corner.Bottom });
                            Rectangle perched = BodyOnScreen(anchor, PerchedBody);
                            bool cornered = seat != null && perched.Left >= corner.Left && perched.Right <= corner.Right && corner.Right - perched.Right <= 3;
                            seat = null;
                            anchor = stoodAt;
                            Location = anchor;
                            Play(idle);
                            if (!cornered) throw new Exception("She can't sit in the bottom corner: " + perched + " in " + corner);
                            checks.Add("PASS screen bounds: her body, not her see-through window, stays on screen, so she stands and sits right in the corners");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[4]).DropDownItems[0]).PerformClick();
                            if (Width != 242) throw new Exception("Small size failed.");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[4]).DropDownItems[1]).PerformClick();
                            checks.Add("PASS size menu");
                            string[] tops = new string[menu.Items.Count];
                            for (int i = 0; i < tops.Length; i++) tops[i] = menu.Items[i].Text;
                            if (string.Join(",", tops) != "CAPRI  /  " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + ",,Open Capri Mainframe,Hide Capri,Size,Move,Help,Download Capri 9.0.0,,Quit Capri" && string.Join(",", tops) != "CAPRI  /  " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + ",,Open Capri Mainframe,Hide Capri,Size,Move,Help,Check for updates,,Quit Capri") throw new Exception("The right-click menu is not the short one: " + string.Join(",", tops));
                            ToggleMotion();
                            if (frames.Enabled) throw new Exception("Motion toggle did not stop idle timer.");
                            checks.Add("PASS motion pause");
                            SavePreferences();
                            if (LoadPreferences().Size != 240) throw new Exception("Settings did not round-trip.");
                            checks.Add("PASS settings round-trip in isolated test directory");
                            Clip[] strips = { idle, blink, blinkGlitch, grin, glitch, dangle, sleep, wake, perch, perchBlink, perchBlinkGlitch, perchGlitch, idle };
                            int[] cells = { 0, 1, 1, 0, 1, 3, 0, 4, 8, 1, 1, 1, 0 };
                            if (mouth.Strip.Width % mouth.Count != 0 || mouth.Strip.Width / mouth.Count != idle.Strip.Width / idle.Count || mouth.Strip.Height != idle.Strip.Height) throw new Exception("Mouth strip does not match the idle cells.");
                            using (Bitmap sheet = new Bitmap(Width * strips.Length, Height, PixelFormat.Format32bppArgb))
                            {
                                for (int i = 0; i < strips.Length; i++)
                                    using (Bitmap still = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
                                    {
                                        if (strips[i].Strip.Width % strips[i].Count != 0) throw new Exception("Clip " + i + " has uneven cells.");
                                        using (Graphics graphics = Graphics.FromImage(still)) Draw(graphics, strips[i], cells[i], i == strips.Length - 1 ? 3 : -1);
                                        int solid = 0;
                                        for (int x = 0; x < Width; x += 4) for (int y = 0; y < Height; y += 4) if (still.GetPixel(x, y).A > 128) solid++;
                                        if (solid < 200) throw new Exception("Clip " + i + " drew nothing.");
                                        using (Graphics graphics = Graphics.FromImage(sheet)) graphics.DrawImage(still, i * Width, 0);
                                    }
                                sheet.Save(Path.Combine(output, "clips.png"), ImageFormat.Png);
                            }
                            checks.Add("PASS all 12 clips draw: idle, blinks, grin, glitch, drag, sleep, wake and perch, plus the talking mouth");
                            using (Font display = Look.Mono(14, FontStyle.Bold))
                            using (Font slanted = Look.Mono(9f, FontStyle.Italic, GraphicsUnit.Point))
                                if (display.Name != "Share Tech Mono" || slanted.Name != "Share Tech Mono") throw new Exception("Embedded font did not load: " + display.Name);
                            checks.Add("PASS Share Tech Mono is embedded for her messages and the Mainframe");
                            if (Brain.PickModel(new[] { "deepseek-r1:7b", "qwen3:1.7b", "gemma4:e4b" }, null, null) != "gemma4:e4b" || Brain.PickModel(new[] { "qwen3:1.7b" }, "gone:1b", null) != "qwen3:1.7b" || Brain.PickModel(new[] { "deepseek-r1:7b", "mystery:3b" }, null, null) != "mystery:3b" || Brain.PickModel(new[] { "qwen3:4b-instruct-2507-q4_K_M", "qwen3:8b" }, null, null) != "qwen3:4b-instruct-2507-q4_K_M" || Brain.PickModel(new string[0], null, null) != null || Brain.PickModel(new[] { "a:1b", "b:2b" }, "b:2b", null) != "b:2b" || Brain.PickModel(new[] { "gemma4:e4b", "qwen3:4b-instruct-2507-q4_K_M" }, null, Engine.Picks[2]) != "qwen3:4b-instruct-2507-q4_K_M") throw new Exception("Model pick failed.");
                            const long Gb = 1L << 30;
                            if (Engine.Fit(new Machine { Ram = 16 * Gb, Vram = 8 * Gb }) != Engine.Picks[0] || Engine.Fit(new Machine { Ram = 32 * Gb, Vram = 2 * Gb }) != Engine.Picks[1] || Engine.Fit(new Machine { Ram = 8 * Gb - Gb / 4 }) != Engine.Picks[1] || Engine.Fit(new Machine { Ram = 4 * Gb, Vram = Gb / 2 }) != Engine.Picks[2]) throw new Exception("Hardware fit picked the wrong model.");
                            Machine here = Machine.Read();
                            if (here.Ram < Gb || here.Describe().IndexOf("GB RAM", StringComparison.Ordinal) < 0) throw new Exception("Could not read this PC's memory.");
                            string hashed = Path.Combine(output, "hash-check.txt");
                            File.WriteAllText(hashed, "capri");
                            if (Engine.Hash(hashed) != "56616ae0a91285159d0198d176ab3dae37996b5f1f4cb81d7d45f2aa99bdf565") throw new Exception("Download checksum is wrong.");
                            File.Delete(hashed);
                            foreach (Pick option in Engine.Picks) if (!option.Url.StartsWith("https://huggingface.co/", StringComparison.Ordinal) || option.Url.IndexOf("/resolve/main/", StringComparison.Ordinal) >= 0 || option.Sha256.Length != 64 || option.Size < 500000000) throw new Exception("Model download is not pinned: " + option.Name);
                            if (engine.Installed() != null) throw new Exception("Test data already has a brain.");
                            checks.Add("PASS brain setup: this PC reads " + here.Describe() + ", hardware fit, pinned downloads, checksum");
                            string messy = "<think>hmm</think> *waves* Capri: \"Hey choom!\" 😀";
                            if (Brain.Clean(messy) != "Hey choom!" || Brain.Clean(new string('a', 300)).Length > 222) throw new Exception("Reply cleanup failed: " + Brain.Clean(messy));
                            if (Safety.Screen("i want to kill myself") != Safety.HelpLine || Safety.Screen("send nudes") == null || Safety.Screen("how do I kill the boss in Essex?") != null || Safety.Filter("well shit, choom") == "well shit, choom" || Safety.Filter("Hey choom.") != "Hey choom.") throw new Exception("Safety screen failed.");
                            if (Safety.Screen("should i jump off my roof for a tiktok") == null || Safety.Screen("how do i get my parents vodka") == null || Safety.Screen("my friend said i should try vaping") == null || Safety.Screen("my code is on fire lol") != null || Safety.Screen("whats the best gun in valorant") != null || Safety.Filter("Sneak out and grab a beer, choom.") == "Sneak out and grab a beer, choom.") throw new Exception("Risky topics got through.");
                            checks.Add("PASS brain helpers: model pick, reply cleanup, safety screen and filter");
                            Brain.Log("https://integrate.api.nvidia.com/v1/chat/completions", 60012, "Timeout");
                            string details = ErrorDetails();
                            if (details.IndexOf("| brain: ", StringComparison.Ordinal) < 0 || details.IndexOf("integrate.api.nvidia.com/v1/chat/completions | 60012 ms | Timeout", StringComparison.Ordinal) < 0) throw new Exception("Error details were wrong: " + details);
                            string late = Brain.Explain(new WebException("late", WebExceptionStatus.Timeout), new Cloud("nvapi-test", "google/gemma-4-31b-it"));
                            if (!late.StartsWith("NVIDIA didn't answer in time.", StringComparison.Ordinal) || late.IndexOf("pick another model", StringComparison.Ordinal) < 0) throw new Exception("A slow NVIDIA reply was not explained: " + late);
                            checks.Add("PASS error details: brain errors are logged with the endpoint and time, Copy error details includes them, and a slow cloud brain says to pick another model");
                            using (Bitmap pointerArt = Look.PointerArt(128)) pointerArt.Save(Path.Combine(output, "cursor.png"), ImageFormat.Png);
                            if (Cursor != Look.Pointer || Look.Pointer == Cursors.Hand || pill.Cursor != Look.Pointer || box.Cursor != Look.Pointer) throw new Exception("Her pink cursor did not load.");
                            checks.Add("PASS cursor: her pink glitch pointer shows over her, her pill and her bubble");
                            System.Collections.Generic.List<string> ran = new System.Collections.Generic.List<string>();
                            System.Collections.Generic.List<byte> tapped = new System.Collections.Generic.List<byte>();
                            Action<string> realRun = Hands.Run;
                            Action<byte> realPress = Hands.Press;
                            Func<System.Collections.Generic.List<InstalledApp>> realCatalog = Hands.Catalog;
                            string realOllama = Brain.Ollama;
                            System.Net.Sockets.TcpListener fakeBrain = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                            try
                            {
                                Hands.Run = delegate(string id) { ran.Add(id); };
                                Hands.Press = delegate(byte key) { tapped.Add(key); };
                                Hands.Catalog = delegate
                                {
                                    return new System.Collections.Generic.List<InstalledApp>
                                    {
                                        new InstalledApp("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), new InstalledApp("Uninstall Spotify", "uninstall"),
                                        new InstalledApp("Roblox", "com.Roblox.Player"), new InstalledApp("Roblox Player", @"C:\Roblox\RobloxPlayerBeta.exe"), new InstalledApp("Roblox Studio", @"C:\Roblox\RobloxStudioBeta.exe"),
                                        new InstalledApp("Minecraft Launcher", "Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft"), new InstalledApp("Minecraft: Java Edition", "Microsoft.MinecraftJavaEdition_8wekyb3d8bbwe!Game"),
                                        new InstalledApp("Steam Support Center", "http://support.steampowered.com/"), new InstalledApp("Blender 5.1", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Blender Foundation\Blender 5.1\blender-launcher.exe")
                                    };
                                };
                                Hands.Forget();
                                string[] outcomes =
                                {
                                    Hands.Do("open_app", new ToolArguments { Name = "spotify" }), Hands.Do("open_app", new ToolArguments { Name = "Roblox" }), Hands.Do("open_app", new ToolArguments { Name = "the blender app" }),
                                    Hands.Do("open_app", new ToolArguments { Name = "minecraft" }), Hands.Do("open_app", new ToolArguments { Name = "steam support center" }), Hands.Do("open_app", new ToolArguments { Name = "Zorblax" }),
                                    Hands.Do("open_website", new ToolArguments { Site = "YouTube", Search = "lofi beats" }), Hands.Do("open_website", new ToolArguments { Site = "discord" }), Hands.Do("open_website", new ToolArguments { Site = "pornhub.com" }),
                                    Hands.Do("media", new ToolArguments { Action = "volume_up" }), Hands.Do("media", new ToolArguments { Action = "next" }), Hands.Do("format_drive", null)
                                };
                                string[] expected =
                                {
                                    "Opened Spotify.", "Opened Roblox.", "Opened Blender 5.1.", "More than one app matches minecraft: Minecraft Launcher, Minecraft: Java Edition. Which one?",
                                    "No app called steam support center is installed on this PC.", "No app called Zorblax is installed on this PC.", "Searched youtube.com for lofi beats.", "Opened discord.com.",
                                    "Not opening that, choom.", "Turned the volume up.", "Skipped to the next track.", "There's no tool called format_drive."
                                };
                                for (int i = 0; i < expected.Length; i++) if (outcomes[i] != expected[i]) throw new Exception("Harness answered '" + outcomes[i] + "', expected '" + expected[i] + "'.");
                                string launched = string.Join(" | ", ran.ToArray());
                                if (launched != @"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify | com.Roblox.Player | {6D809377-6AF0-444B-8957-A3773F02200E}\Blender Foundation\Blender 5.1\blender-launcher.exe | https://www.youtube.com/results?search_query=lofi%20beats | https://discord.com/") throw new Exception("Harness launched the wrong things: " + launched);
                                if (tapped.Count != 6 || tapped[0] != 0xAF || tapped[5] != 0xB0) throw new Exception("Media keys were wrong: " + tapped.Count);
                                if (Hands.Address("javascript:alert(1)", null) != null || Hands.Address("user@evil.com", null) != null || Hands.Address("", null) != null || Hands.Address("spotify", "lofi") != "https://open.spotify.com/search/lofi") throw new Exception("Website addresses were not checked.");
                                fakeBrain.Start();
                                string[] brainReplies =
                                {
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"open_app\",\"arguments\":{\"name\":\"Spotify\"}}}]}}",
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"Spotify's up. Try not to play anything corpo.\"}}",
                                    "HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\n\r\n{\"error\":\"registry.ollama.ai/library/gemma3:1b does not support tools\"}",
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"chat\"}}",
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"No notes app here, choom.\"}}",
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"Still here.\"}}"
                                };
                                System.Collections.Generic.List<string> brainAsked = new System.Collections.Generic.List<string>();
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    try
                                    {
                                        foreach (string replyText in brainReplies)
                                            using (System.Net.Sockets.TcpClient client = fakeBrain.AcceptTcpClient())
                                            using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                                            {
                                                MemoryStream got = new MemoryStream();
                                                byte[] buffer = new byte[8192];
                                                string text = "";
                                                while (true)
                                                {
                                                    int read = stream.Read(buffer, 0, buffer.Length);
                                                    if (read <= 0) break;
                                                    got.Write(buffer, 0, read);
                                                    text = System.Text.Encoding.UTF8.GetString(got.ToArray());
                                                    int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                                    if (end < 0) continue;
                                                    System.Text.RegularExpressions.Match length = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                    if (got.Length >= end + 4 + (length.Success ? int.Parse(length.Groups[1].Value) : 0)) break;
                                                }
                                                lock (brainAsked) brainAsked.Add(text);
                                                int split = replyText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                                byte[] answer = System.Text.Encoding.UTF8.GetBytes(replyText.Substring(split + 4));
                                                byte[] head = System.Text.Encoding.ASCII.GetBytes(replyText.Substring(0, split) + "\r\nContent-Length: " + answer.Length + "\r\nConnection: close\r\n\r\n");
                                                stream.Write(head, 0, head.Length);
                                                stream.Write(answer, 0, answer.Length);
                                            }
                                    }
                                    catch (Exception) { }
                                });
                                Brain.Ollama = "http://127.0.0.1:" + ((IPEndPoint)fakeBrain.LocalEndpoint).Port;
                                Brain handy = new Brain();
                                handy.Model = "gemma4:e4b";
                                handy.Act = Hands.Do;
                                string opened = handy.Ask("get spotify going");
                                handy.Model = "gemma3:1b";
                                string plain = handy.Ask("open my notes thing");
                                string again = handy.Ask("still there?");
                                string[] brainSent;
                                lock (brainAsked) brainSent = brainAsked.ToArray();
                                if (opened != "Spotify's up. Try not to play anything corpo." || plain != "No notes app here, choom." || again != "Still here.") throw new Exception("Brain tool replies were wrong: " + opened + " / " + plain + " / " + again);
                                if (brainSent.Length != 6 || brainSent[0].IndexOf("\"tools\":[{\"type\":\"function\"", StringComparison.Ordinal) < 0 || brainSent[0].IndexOf("You are the hands of Capri", StringComparison.Ordinal) < 0 || brainSent[0].IndexOf("Project Capricorn", StringComparison.Ordinal) >= 0) throw new Exception("The request pass did not get the tool prompt alone: " + brainSent.Length);
                                if (brainSent[1].IndexOf("Most messages are just chat", StringComparison.Ordinal) < 0 || brainSent[1].IndexOf("Project Capricorn", StringComparison.Ordinal) < 0 || brainSent[1].IndexOf("Right now it's " + DateTime.Now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) < 0) throw new Exception("Her prompt did not say to just chat or what time it is.");
                                if (brainSent[1].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || brainSent[1].IndexOf("(What your tools just did: Opened Spotify. Tell them", StringComparison.Ordinal) < 0) throw new Exception("The follow-up did not pass the tool result: " + brainSent[1]);
                                if (brainSent[2].IndexOf("\"tools\":", StringComparison.Ordinal) < 0 || brainSent[3].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || brainSent[3].IndexOf("To use one, reply with only the call", StringComparison.Ordinal) < 0 || brainSent[3].IndexOf("media{\\\"action\\\": \\\"play_pause|next|previous|volume_up|volume_down|mute|now_playing\\\"}", StringComparison.Ordinal) < 0) throw new Exception("A model without tools did not get its tools as text: " + brainSent[3]);
                                if (brainSent[4].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || brainSent[4].IndexOf("Project Capricorn", StringComparison.Ordinal) < 0 || brainSent[5].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || brainSent[5].IndexOf("You are the hands", StringComparison.Ordinal) >= 0) throw new Exception("Plain chat still went through the tool prompt.");
                                if (!Hands.Wants("can you see what im doing?") || !Hands.Wants("play me some music") || !Hands.Wants("open notepad") || !Hands.Wants("go to youtube.com") || Hands.Wants("hey") || Hands.Wants("how was your day?") || Hands.Wants("still there?") || Hands.Wants("out of five playtesters, who's the best?")) throw new Exception("Requests and chat were told apart wrong.");
                                if (ran.Count != 6 || ran[5] != "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") throw new Exception("The brain's tool call did not open Spotify.");
                                System.Collections.Generic.List<string> thinkAsked = new System.Collections.Generic.List<string>();
                                System.Net.Sockets.TcpListener thinkFake;
                                string brainUrl = Brain.Ollama;
                                Brain.Ollama = FakeServer(new string[]
                                {
                                    "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"Okay, the user asked what's up. I should\"},\"done_reason\":\"length\"}",
                                    "{\"message\":{\"role\":\"assistant\",\"content\":\"Not much, choom.\",\"thinking\":\"Short answer.\"},\"done_reason\":\"stop\"}",
                                    "{\"message\":{\"role\":\"assistant\",\"content\":\"Still here.\"},\"done_reason\":\"stop\"}"
                                }, thinkAsked, out thinkFake);
                                try
                                {
                                    Brain deep = new Brain();
                                    deep.Model = "deepseek-r1:7b";
                                    string first = deep.Ask("what's up?");
                                    string second = deep.Ask("and now?");
                                    string[] thinkSent;
                                    lock (thinkAsked) thinkSent = thinkAsked.ToArray();
                                    if (first != "Not much, choom." || second != "Still here." || thinkSent.Length != 3) throw new Exception("A thinking model's reply was lost: " + first + " / " + second);
                                    if (thinkSent[0].IndexOf("\"num_predict\":90", StringComparison.Ordinal) < 0 || thinkSent[1].IndexOf("\"num_predict\":2138", StringComparison.Ordinal) < 0 || thinkSent[2].IndexOf("\"num_predict\":2138", StringComparison.Ordinal) < 0) throw new Exception("A thinking model was not given room to think.");
                                }
                                finally
                                {
                                    thinkFake.Stop();
                                    Brain.Ollama = brainUrl;
                                }
                                Func<string, string> realFetch = Hands.Fetch;
                                Func<System.Collections.Generic.List<OpenWindow>> realWindows = Hands.Windows;
                                Func<OpenWindow, string, bool> realSteer = Hands.Steer;
                                Func<System.Collections.Generic.List<InstalledApp>> fakeCatalog = Hands.Catalog;
                                System.Collections.Generic.List<string> lookups = new System.Collections.Generic.List<string>();
                                System.Collections.Generic.List<string> steered = new System.Collections.Generic.List<string>();
                                try
                                {
                                    Hands.Fetch = delegate(string address)
                                    {
                                        lookups.Add(address);
                                        if (address.IndexOf("nothing", StringComparison.Ordinal) >= 0) return "<html></html>";
                                        if (address.IndexOf("%2Fplaylist", StringComparison.Ordinal) >= 0) return "<a href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fopen.spotify.com%2Fplaylist%2F37i9dQZF1DWWQRwui0ExPn\">";
                                        if (address.IndexOf("&sp=", StringComparison.Ordinal) >= 0) return "{\"watchEndpoint\":{\"videoId\":\"qboQlr7lAAk\",\"playlistId\":\"RDCLAK5uy_kb7EBi6y3GrtJri4_ZH56Ms786DFEimbM\"}}";
                                        return address.IndexOf("duckduckgo", StringComparison.Ordinal) >= 0 ? "<a href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fopen.spotify.com%2Ftrack%2F0VjIjW4GlUZAMYd2vXMi3b\">" : "{\"videoRenderer\":{\"videoId\":\"4NRXx6U8ABQ\"}}";
                                    };
                                    Hands.Windows = delegate
                                    {
                                        return new System.Collections.Generic.List<OpenWindow>
                                        {
                                            new OpenWindow(new IntPtr(1), "#general | Friends - Discord", "Discord"), new OpenWindow(new IntPtr(2), "YouTube - Google Chrome", "chrome"),
                                            new OpenWindow(new IntPtr(3), "Untitled - Notepad", "notepad"), new OpenWindow(new IntPtr(4), "Roblox", "RobloxPlayerBeta"), new OpenWindow(new IntPtr(5), "Roblox Studio", "RobloxStudioBeta"),
                                            new OpenWindow(new IntPtr(6), "Document1 - Word", "WINWORD"), new OpenWindow(new IntPtr(7), "Minecraft Launcher", "MinecraftLauncher"), new OpenWindow(new IntPtr(8), "Minecraft 1.21", "javaw")
                                        };
                                    };
                                    Hands.Steer = delegate(OpenWindow window, string action) { steered.Add(action + " " + window.Handle); return window.Program != "notepad"; };
                                    Func<string, bool> realPressPlay = Hands.PressPlay;
                                    System.Collections.Generic.List<string> playPresses = new System.Collections.Generic.List<string>();
                                    Hands.PressPlay = delegate(string name) { playPresses.Add(name); return name != "Liked Songs" && name != "nothing at all"; };
                                    Func<string> realNowPlaying = Hands.NowPlaying;
                                    Hands.NowPlaying = delegate { return ran.Count > 0 && ran[ran.Count - 1].StartsWith("spotify:track:", StringComparison.Ordinal) ? "The Weeknd - Blinding Lights" : "Spotify Free"; };
                                    int ranBefore = ran.Count;
                                    string[] handled =
                                    {
                                        Hands.Do("play_music", new ToolArguments { Song = "Blinding Lights by The Weeknd" }), Hands.Do("play_music", new ToolArguments { Song = "lofi beats", App = "youtube" }),
                                        Hands.Do("play_music", new ToolArguments { Song = "nothing at all" }), Hands.Do("play_music", new ToolArguments()), Hands.Do("play_music", new ToolArguments { Song = "pornhub" }),
                                        Hands.Do("app_window", new ToolArguments { Name = "discord", Action = "close" }), Hands.Do("app_window", new ToolArguments { Name = "YouTube", Action = "close" }),
                                        Hands.Do("app_window", new ToolArguments { Name = "notepad", Action = "close" }), Hands.Do("app_window", new ToolArguments { Name = "roblox", Action = "switch_to" }),
                                        Hands.Do("app_window", new ToolArguments { Name = "word", Action = "minimize" }), Hands.Do("app_window", new ToolArguments { Name = "minecraft", Action = "close" }),
                                        Hands.Do("app_window", new ToolArguments { Name = "Fortnite", Action = "close" }), Hands.Do("app_window", new ToolArguments { Name = "chrome", Action = "explode" }),
                                        Hands.Do("play_music", new ToolArguments { Song = "lofi beats", Kind = "playlist" }), Hands.Do("play_music", new ToolArguments { Song = "my liked songs", Kind = "playlist" }),
                                        Hands.Do("play_music", new ToolArguments { Song = "lofi beats", Kind = "playlist", App = "youtube" })
                                    };
                                    Hands.NowPlaying = delegate { return "Chase Atlantic - Consume (feat. Goon Des Garcons)"; };
                                    string nowPlaying = Hands.Do("media", new ToolArguments { Action = "now_playing" }) + " | " + Hands.Do("play_music", new ToolArguments { Song = "currently playing", Kind = "playlist" });
                                    Hands.NowPlaying = delegate { return "Spotify Premium"; };
                                    nowPlaying += " | " + Hands.Do("media", new ToolArguments { Action = "now_playing" });
                                    Hands.NowPlaying = delegate { return ""; };
                                    nowPlaying += " | " + Hands.Do("media", new ToolArguments { Action = "now_playing" });
                                    Func<System.Collections.Generic.List<OpenWindow>> listedWindows = Hands.Windows;
                                    Hands.Windows = delegate { return new System.Collections.Generic.List<OpenWindow> { new OpenWindow(new IntPtr(9), "(3) Consume - Chase Atlantic - YouTube - Google Chrome", "chrome") }; };
                                    nowPlaying += " | " + Hands.Do("media", new ToolArguments { Action = "now_playing" });
                                    Hands.Windows = listedWindows;
                                    nowPlaying += " | " + Hands.Do("play_music", new ToolArguments { Song = "play me some music" });
                                    if (nowPlaying != "Spotify is playing Consume (feat. Goon Des Garcons) by Chase Atlantic. | Spotify is playing Consume (feat. Goon Des Garcons) by Chase Atlantic. | Spotify is open but paused. | Nothing's playing that I can see. | Their YouTube tab is on Consume - Chase Atlantic. | What should I play?") throw new Exception("What's playing came back wrong: " + nowPlaying);
                                    Hands.PressPlay = realPressPlay;
                                    Hands.NowPlaying = realNowPlaying;
                                    Hands.Catalog = delegate { return new System.Collections.Generic.List<InstalledApp>(); };
                                    Hands.Forget();
                                    string noSpotify = Hands.Do("play_music", new ToolArguments { Song = "Blinding Lights", App = "spotify" });
                                    string[] handledWanted =
                                    {
                                        "Playing Blinding Lights by The Weeknd on Spotify.", "Playing lofi beats on YouTube.", "Couldn't find nothing at all to play, so Spotify is open on a search for it.",
                                        "What should I play?", "Not playing that, choom.", "Closed discord.", "Closed the YouTube tab.",
                                        "Asked notepad to close, but it's still open. It might be asking you to save.", "Switched to roblox.", "Minimized word.",
                                        "More than one open app matches minecraft: Minecraft Launcher, Minecraft 1.21. Which one?", "Fortnite isn't open.", "There's no window action called explode.",
                                        "Playing the lofi beats playlist on Spotify.", "Opened their Liked Songs in Spotify, but couldn't press play. Hit the green Play button.", "Playing the lofi beats playlist on YouTube."
                                    };
                                    for (int i = 0; i < handledWanted.Length; i++) if (handled[i] != handledWanted[i]) throw new Exception("Hands answered '" + handled[i] + "', expected '" + handledWanted[i] + "'.");
                                    if (noSpotify != "Spotify isn't installed, so playing Blinding Lights on YouTube.") throw new Exception("Without Spotify the song did not go to YouTube: " + noSpotify);
                                    string played = string.Join(" | ", ran.GetRange(ranBefore, ran.Count - ranBefore).ToArray());
                                    if (played != "spotify:track:0VjIjW4GlUZAMYd2vXMi3b | https://www.youtube.com/watch?v=4NRXx6U8ABQ | spotify:search:nothing%20at%20all | spotify:playlist:37i9dQZF1DWWQRwui0ExPn | spotify:collection:tracks | https://www.youtube.com/watch?v=qboQlr7lAAk&list=RDCLAK5uy_kb7EBi6y3GrtJri4_ZH56Ms786DFEimbM | https://www.youtube.com/watch?v=4NRXx6U8ABQ" || string.Join(",", playPresses.ToArray()) != "nothing at all,lofi beats,Liked Songs") throw new Exception("Songs were played wrong: " + played);
                                    if (lookups.Count != 6 || lookups[0] != "https://html.duckduckgo.com/html/?q=site%3Aopen.spotify.com%2Ftrack%20Blinding%20Lights%20by%20The%20Weeknd" || lookups[1] != "https://www.youtube.com/results?search_query=lofi%20beats") throw new Exception("Songs were looked up wrong: " + string.Join(" | ", lookups.ToArray()));
                                    if (string.Join(" | ", steered.ToArray()) != "close 1 | close_tab 2 | close 3 | switch_to 4 | minimize 6") throw new Exception("Windows were steered wrong: " + string.Join(" | ", steered.ToArray()));
                                    Hands.Catalog = fakeCatalog;
                                    Hands.Forget();
                                    string[,] routes =
                                    {
                                        { "hey capri, can you open spotify for me?", "open_app|spotify||" }, { "Play Roblox", "open_app|Roblox||" }, { "open youtube", "open_website|youtube||" },
                                        { "launch google.com please", "open_website|google.com||" }, { "play Blinding Lights by The Weeknd on YouTube.", "play_music|Blinding Lights by The Weeknd|song|youtube" },
                                        { "play my lofi beats playlist", "play_music|lofi beats|playlist|" }, { "play something by Drake", "play_music|Drake|artist|" }, { "play my liked songs", "play_music|Liked Songs|playlist|" },
                                        { "skip this song", "media||next|" }, { "turn it up", "media||volume_up|" }, { "close discord", "app_window|discord|close|" }, { "minimise word", "app_window|word|minimize|" },
                                        { "switch to roblox", "app_window|roblox|switch_to|" }, { "play with me", "" }, { "play something", "" }, { "play a song", "" }, { "open my downloads folder", "" },
                                        { "close it", "" }, { "close fortnite", "" }, { "what's on my screen?", "" }, { "open zorblax", "" },
                                        { "open spotify and shuffle my liked songs", "play_music|Liked Songs|playlist|spotify" }, { "play your liked songs playlist", "play_music|Liked Songs|playlist|" },
                                        { "play the album Blonde by Frank Ocean", "play_music|Blonde by Frank Ocean|album|" }, { "shuffle my lofi beats playlist", "play_music|lofi beats|playlist|" },
                                        { "whats playing?", "media||now_playing|" }, { "capri, what song is this", "media||now_playing|" }, { "what’s playing right now", "media||now_playing|" }
                                    };
                                    for (int i = 0; i < routes.GetLength(0); i++)
                                    {
                                        string routeTool;
                                        ToolArguments routeArgs;
                                        string route = Hands.Direct(routes[i, 0], out routeTool, out routeArgs) ? routeTool + "|" + (routeArgs.Name ?? routeArgs.Site ?? routeArgs.Song) + "|" + (routeArgs.Action ?? routeArgs.Kind) + "|" + routeArgs.App : "";
                                        if (route != routes[i, 1]) throw new Exception("'" + routes[i, 0] + "' routed to '" + route + "', expected '" + routes[i, 1] + "'.");
                                    }
                                    string[,] written =
                                    {
                                        { "look_at_screen{}", "look_at_screen|||" }, { "Sure! open_app({\"name\": \"Spotify\"})", "open_app|Spotify||" },
                                        { "call:play_music{song:<|\"|>Blinding Lights<|\"|>,kind:<|\"|>song<|\"|>}", "play_music|Blinding Lights|song|" },
                                        { "app_window(name='discord', action='close')", "app_window|discord|close|" }, { "open_website(site=https://youtube.com)", "open_website|https://youtube.com||" },
                                        { "media(\"next\")", "media||next|" }, { "I could use open_app but I won't.", "" }
                                    };
                                    for (int i = 0; i < written.GetLength(0); i++)
                                    {
                                        string writtenTool;
                                        ToolArguments writtenArgs;
                                        string parsed = Hands.Typed(written[i, 0], out writtenTool, out writtenArgs) ? writtenTool + "|" + (writtenArgs.Name ?? writtenArgs.Site ?? writtenArgs.Song) + "|" + (writtenArgs.Action ?? writtenArgs.Kind) + "|" + writtenArgs.App : "";
                                        if (parsed != written[i, 1]) throw new Exception("'" + written[i, 0] + "' parsed to '" + parsed + "', expected '" + written[i, 1] + "'.");
                                    }
                                    string narratedTool;
                                    ToolArguments narratedArgs;
                                    if (!Hands.Narrated("open spotify and shuffle my liked songs", "open spotify and play your liked songs playlist.", out narratedTool, out narratedArgs) || narratedTool != "play_music" || narratedArgs.Song != "Liked Songs"
                                        || Hands.Narrated("what's up?", "open spotify", out narratedTool, out narratedArgs) || Hands.Narrated("open discord", "close discord", out narratedTool, out narratedArgs)) throw new Exception("Narrated actions were read wrong.");
                                    if (!Engine.Lost("got exception: vk::Queue::submit: ErrorDeviceLost") || Engine.Lost("model not found")) throw new Exception("A lost graphics card was not spotted.");
                                    const string broke = "No credits on this key. Add credits on their site, or press Free key for a free NVIDIA key.";
                                    if (Brain.Meaning(429, "{\"error\":{\"message\":\"You have no credits remaining. Add credits to continue using the API.\",\"type\":\"insufficient_quota\"}}") != broke || Brain.Meaning(402, "{\"error\":{\"message\":\"Insufficient Balance\"}}") != broke
                                        || Brain.Meaning(400, "{\"type\":\"error\",\"error\":{\"message\":\"Your credit balance is too low to access the Anthropic API.\"}}") != broke || Brain.Meaning(429, "{\"status\":429,\"title\":\"Too Many Requests\"}") != "Too many requests. Wait a minute, or watch slower."
                                        || Brain.Meaning(401, "{\"error\":\"bad key\"}") != "Key rejected. Press Forget, then paste it again." || Brain.Meaning(400, "{\"error\":\"bad request\"}") != null) throw new Exception("Cloud errors were explained wrong.");
                                    System.Collections.Generic.List<string> directAsked = new System.Collections.Generic.List<string>();
                                    System.Net.Sockets.TcpListener directFake;
                                    string ollamaUrl = Brain.Ollama;
                                    Brain.Ollama = FakeServer(new string[]
                                    {
                                        "{\"message\":{\"role\":\"assistant\",\"content\":\"Spotify's up, choom.\"},\"done_reason\":\"stop\"}",
                                        "{\"message\":{\"role\":\"assistant\",\"content\":\"look_at_screen{}\"},\"done_reason\":\"stop\"}",
                                        "{\"message\":{\"role\":\"assistant\",\"content\":\"Nah, my eyes are off right now.\"},\"done_reason\":\"stop\"}"
                                    }, directAsked, out directFake);
                                    try
                                    {
                                        Brain direct = new Brain();
                                        direct.Model = "gemma4:e4b";
                                        direct.Act = Hands.Do;
                                        int ranDirect = ran.Count;
                                        string directOpened = direct.Ask("hey capri, can you open spotify for me?");
                                        string directStuck = direct.Ask("close notepad");
                                        Func<string, string> realLook = Hands.Look;
                                        Hands.Look = delegate { return "Their screen shows a code editor."; };
                                        string directLooked;
                                        try { directLooked = direct.Ask("can you see what im doing?"); }
                                        finally { Hands.Look = realLook; }
                                        string[] directSent;
                                        lock (directAsked) directSent = directAsked.ToArray();
                                        if (directOpened != "Spotify's up, choom." || directStuck != "Asked notepad to close, but it's still open. It might be asking you to save." || directLooked != "Nah, my eyes are off right now.") throw new Exception("Direct commands replied wrong: " + directOpened + " / " + directStuck + " / " + directLooked);
                                        if (directSent.Length != 3 || directSent[0].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || directSent[0].IndexOf("(What your tools just did: Opened Spotify.", StringComparison.Ordinal) < 0) throw new Exception("Direct commands still asked the brain to pick a tool.");
                                        if (directSent[1].IndexOf("\"tools\":", StringComparison.Ordinal) < 0 || directSent[2].IndexOf("(What your tools just did: Their screen shows a code editor.", StringComparison.Ordinal) < 0) throw new Exception("A tool call written as text was not run: " + directSent[2]);
                                        if (ran.Count != ranDirect + 1 || ran[ran.Count - 1] != "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") throw new Exception("A direct command did not open Spotify.");
                                    }
                                    finally
                                    {
                                        directFake.Stop();
                                        Brain.Ollama = ollamaUrl;
                                    }
                                }
                                finally
                                {
                                    Hands.Fetch = realFetch;
                                    Hands.Windows = realWindows;
                                    Hands.Steer = realSteer;
                                    Hands.Catalog = fakeCatalog;
                                    Hands.Forget();
                                }
                                System.Collections.Generic.List<OpenWindow> live = Hands.Windows();
                                if (live.Exists(window => window.Handle == Handle || window.Handle == mainframe.Handle)) throw new Exception("Capri listed her own window as an app to close.");
                                System.Collections.Generic.List<string> cloudAsked = new System.Collections.Generic.List<string>();
                                System.Net.Sockets.TcpListener cloudFake;
                                string cloudUrl = FakeServer(new string[]
                                {
                                    "{\"data\":[{\"id\":\"claude-opus-5-5\",\"created_at\":\"2026-08-01T00:00:00Z\"},{\"id\":\"claude-haiku-5-5\",\"created_at\":\"2026-09-01T00:00:00Z\"}],\"has_more\":false}",
                                    "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"x\"},{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"open_app\",\"input\":{\"name\":\"Spotify\"}}],\"stop_reason\":\"tool_use\"}",
                                    "{\"content\":[{\"type\":\"text\",\"text\":\"Spotify's up.\"}],\"stop_reason\":\"end_turn\"}",
                                    "{\"data\":[{\"id\":\"gpt-5\",\"created\":100},{\"id\":\"gpt-4o-audio-preview\",\"created\":300},{\"id\":\"gpt-5-mini\",\"created\":200},{\"id\":\"text-embedding-3-small\",\"created\":400},{\"id\":\"gpt-5-2025-08-07\",\"created\":150}]}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"media\",\"arguments\":\"{\\\"action\\\":\\\"next\\\"}\"}}]}}]}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Skipped.\"}}]}",
                                    "{\"content\":[],\"stop_reason\":\"refusal\"}",
                                    "{\"object\":\"list\",\"data\":[{\"id\":\"deepseek-v4-pro\",\"object\":\"model\",\"owned_by\":\"deepseek\"},{\"id\":\"deepseek-flash\",\"object\":\"model\",\"owned_by\":\"deepseek\"}]}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"DeepSeek here.\",\"reasoning_content\":null}}]}",
                                    "{\"object\":\"list\",\"data\":[{\"id\":\"z-ai/glm-5.3\",\"created\":735790403},{\"id\":\"nvidia/nv-embedqa-mistral-7b-v2\",\"created\":735790403},{\"id\":\"meta/codellama-70b\",\"created\":735790403},{\"id\":\"mistralai/mistral-large-3-675b-instruct-2512\",\"created\":735790403},{\"id\":\"google/gemma-4-31b-it\",\"created\":735790403},{\"id\":\"nvidia/nemotron-4-340b-reward\",\"created\":735790403},{\"id\":\"moonshotai/kimi-k2.6\",\"created\":735790403},{\"id\":\"nvidia/ai-synthetic-video-detector\",\"created\":735790403},{\"id\":\"bigcode/starcoder2-15b\",\"created\":735790403}]}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Free brain online.\"}}]}"
                                }, cloudAsked, out cloudFake);
                                string realOpenAi = Brain.OpenAi;
                                string realAnthropic = Brain.Anthropic;
                                string realDeepSeek = Brain.DeepSeek;
                                string realNvidia = Brain.Nvidia;
                                try
                                {
                                    Brain.OpenAi = cloudUrl;
                                    Brain.Anthropic = cloudUrl;
                                    Brain.DeepSeek = cloudUrl;
                                    Brain.Nvidia = cloudUrl;
                                    if (Cloud.KindOf("sk-ant-api03-x") != "anthropic" || Cloud.KindOf("nvapi-x") != "nvidia" || Cloud.KindOf("sk-0123456789abcdef0123456789abcdef") != "deepseek" || Cloud.KindOf("sk-proj-x") != "openai" || Cloud.KindOf("sk-AbCdEf0123456789abcdef0123456789T3Blbk" + "FJ0123456789ab") != "openai" || Cloud.KindOf("hello") != null) throw new Exception("API keys were sorted wrong.");
                                    System.Collections.Generic.List<string> claudeModels = Brain.CloudModels("sk-ant-test");
                                    if (string.Join(",", claudeModels.ToArray()) != "claude-haiku-5-5,claude-opus-5-5" || Brain.DefaultCloudModel("anthropic", claudeModels) != "claude-opus-5-5") throw new Exception("Claude models were read wrong: " + string.Join(",", claudeModels.ToArray()));
                                    Brain cloudy = new Brain();
                                    cloudy.Act = Hands.Do;
                                    cloudy.Remote = new Cloud("sk-ant-test", "claude-opus-5-5");
                                    cloudy.Model = "claude-opus-5-5";
                                    string claudeSaid = cloudy.Ask("get spotify going");
                                    System.Collections.Generic.List<string> openModels = Brain.CloudModels("sk-proj-test");
                                    if (string.Join(",", openModels.ToArray()) != "gpt-5-mini,gpt-5" || Brain.DefaultCloudModel("openai", openModels) != "gpt-5-mini") throw new Exception("OpenAI models were read wrong: " + string.Join(",", openModels.ToArray()));
                                    cloudy.Remote = new Cloud("sk-proj-test", "gpt-5-mini");
                                    cloudy.Model = "gpt-5-mini";
                                    cloudy.Forget();
                                    string openSaid = cloudy.Ask("this song sucks, skip it");
                                    cloudy.Remote = new Cloud("sk-ant-test", "claude-opus-5-5");
                                    cloudy.Model = "claude-opus-5-5";
                                    string refused = cloudy.Ask("something it declines");
                                    const string deepKey = "sk-0123456789abcdef0123456789abcdef";
                                    System.Collections.Generic.List<string> deepModels = Brain.CloudModels(deepKey);
                                    if (Brain.DefaultCloudModel("deepseek", deepModels) != "deepseek-flash" || deepModels.Count != 2) throw new Exception("DeepSeek models were read wrong: " + string.Join(",", deepModels.ToArray()));
                                    cloudy.Remote = new Cloud(deepKey, "deepseek-flash");
                                    cloudy.Model = "deepseek-flash";
                                    cloudy.Forget();
                                    string deepSaid = cloudy.Ask("hey");
                                    System.Collections.Generic.List<string> freeModels = Brain.CloudModels("nvapi-test");
                                    if (string.Join(",", freeModels.ToArray()) != "google/gemma-4-31b-it,moonshotai/kimi-k2.6,z-ai/glm-5.3,mistralai/mistral-large-3-675b-instruct-2512" || Brain.DefaultCloudModel("nvidia", freeModels) != "google/gemma-4-31b-it") throw new Exception("NVIDIA models were read wrong: " + string.Join(",", freeModels.ToArray()));
                                    cloudy.Remote = new Cloud("nvapi-test", "google/gemma-4-31b-it");
                                    cloudy.Model = "google/gemma-4-31b-it";
                                    cloudy.Forget();
                                    string freeSaid = cloudy.Ask("hey");
                                    if (claudeSaid != "Spotify's up." || openSaid != "Skipped." || refused != Brain.Declined || deepSaid != "DeepSeek here." || freeSaid != "Free brain online.") throw new Exception("Cloud replies were wrong: " + claudeSaid + " / " + openSaid + " / " + refused + " / " + deepSaid + " / " + freeSaid);
                                    string[] cloudSent;
                                    lock (cloudAsked) cloudSent = cloudAsked.ToArray();
                                    if (cloudSent.Length != 11) throw new Exception("Cloud brain made " + cloudSent.Length + " requests.");
                                    string[][] cloudWanted =
                                    {
                                        new[] { "GET /v1/models?limit=100", "x-api-key: sk-ant-test", "anthropic-version: 2026-10-21" },
                                        new[] { "POST /v1/messages", "anthropic-beta: server-side-fallback-2026-07-01", "\"fallbacks\":\"default\"", "\"output_config\":{\"effort\":\"low\"}", "\"system\":\"You are the hands of Capri","\"tools\":[{\"name\":\"open_app\",\"description\":", "\"input_schema\":{\"type\":\"object\"" },
                                        new[] { "POST /v1/messages", "(What your tools just did: Opened Spotify." },
                                        new[] { "GET /v1/models", "Authorization: Bearer sk-proj-test" },
                                        new[] { "POST /v1/chat/completions", "\"max_completion_tokens\":2000", "\"reasoning_effort\":\"low\"", "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"open_app\"" },
                                        new[] { "POST /v1/chat/completions", "(What your tools just did: Skipped to the next track." },
                                        new[] { "POST /v1/messages" },
                                        new[] { "GET /models ", "Authorization: Bearer " + deepKey },
                                        new[] { "POST /chat/completions ", "\"thinking\":{\"type\":\"disabled\"}", "\"max_tokens\":2000", "\"model\":\"deepseek-flash\"", "You are Capri" },
                                        new[] { "GET /v1/models ", "Authorization: Bearer nvapi-test" },
                                        new[] { "POST /v1/chat/completions ", "\"max_tokens\":2000", "\"model\":\"google\\/gemma-4-31b-it\"", "You are Capri" }
                                    };
                                    for (int i = 0; i < cloudWanted.Length; i++)
                                        foreach (string part in cloudWanted[i])
                                            if (cloudSent[i].IndexOf(part, StringComparison.Ordinal) < 0) throw new Exception("Cloud request " + i + " is missing " + part + ": " + cloudSent[i]);
                                    if (cloudSent[2].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || cloudSent[8].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 || cloudSent[10].IndexOf("\"tools\":", StringComparison.Ordinal) >= 0 ||cloudSent[1].IndexOf("temperature", StringComparison.Ordinal) >= 0 || cloudSent[4].IndexOf("temperature", StringComparison.Ordinal) >= 0) throw new Exception("Cloud requests sent tools or temperature where they shouldn't.");
                                    if (cloudSent[8].IndexOf("max_completion_tokens", StringComparison.Ordinal) >= 0 || cloudSent[8].IndexOf("reasoning_effort", StringComparison.Ordinal) >= 0 || cloudSent[10].IndexOf("max_completion_tokens", StringComparison.Ordinal) >= 0 || cloudSent[10].IndexOf("thinking", StringComparison.Ordinal) >= 0 || cloudSent[4].IndexOf("max_tokens", StringComparison.Ordinal) >= 0 || cloudSent[4].IndexOf("thinking", StringComparison.Ordinal) >= 0) throw new Exception("DeepSeek, NVIDIA or OpenAI got another provider's settings.");
                                    if (ran[ran.Count - 1] != "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" || tapped[tapped.Count - 1] != 0xB0) throw new Exception("Cloud tool calls did not run.");
                                    if (!mainframe.TextStatus.EndsWith("| Optional: NVIDIA/GPT/Claude/DeepSeek | Connect | free", StringComparison.Ordinal)) throw new Exception("The API key row is wrong: " + mainframe.TextStatus);
                                    mainframe.ShowCloud(new Cloud("sk-ant-test", "claude-opus-5-5"), claudeModels);
                                    mainframe.ShowTextKey("test");
                                    if (mainframe.TextStatus != "Claude · claude-opus-5-5 | Using your Anthropic key | Key saved, ends test | Forget | -") throw new Exception("The Mainframe did not show the API brain: " + mainframe.TextStatus);
                                    mainframe.ShowTextKey(null);
                                }
                                finally
                                {
                                    Brain.OpenAi = realOpenAi;
                                    Brain.Anthropic = realAnthropic;
                                    Brain.DeepSeek = realDeepSeek;
                                    Brain.Nvidia = realNvidia;
                                    cloudFake.Stop();
                                }
                                checks.Add("PASS API brain: sorts NVIDIA, OpenAI, Claude and DeepSeek keys, reads their model lists, picks defaults, sends each provider's request shape with tools, runs their tool calls, handles a refusal, and shows the key row");
                                System.Collections.Generic.List<string> seeAsked = new System.Collections.Generic.List<string>();
                                System.Net.Sockets.TcpListener seeFake;
                                string seeUrl = FakeServer(new string[]
                                {
                                    "{\"content\":[{\"type\":\"text\",\"text\":\"A crate menu in a space game.\"}],\"stop_reason\":\"end_turn\"}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Gemma sees a crate.\"}}]}",
                                    "{\"model\":\"gemma4:e4b\",\"message\":{\"role\":\"assistant\",\"content\":\"Ollama sees a crate.\"},\"done\":true}",
                                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"A level 26 player opening a salvage crate.\"}}]}"
                                }, seeAsked, out seeFake);
                                string seeAnthropic = Brain.Anthropic;
                                string seeNvidia = Brain.Nvidia;
                                string seeOllama = Brain.Ollama;
                                Func<Rectangle, Bitmap> realGrab = Grab;
                                Cloud brainRemote = brain.Remote;
                                string brainModel = brain.Model;
                                try
                                {
                                    Brain.Anthropic = seeUrl;
                                    Brain.Nvidia = seeUrl;
                                    Brain.Ollama = seeUrl;
                                    byte[] picture = { 1, 2, 3, 4, 5, 6 };
                                    Brain eyes = new Brain();
                                    eyes.Remote = new Cloud("sk-ant-test", "claude-opus-5-5");
                                    eyes.Model = "claude-opus-5-5";
                                    string claudeSaw = eyes.See(picture, "Plain.", "What is this?", 160);
                                    eyes.Remote = new Cloud("nvapi-test", "google/gemma-4-31b-it");
                                    eyes.Model = "google/gemma-4-31b-it";
                                    string nvidiaSaw = eyes.See(picture, "Plain.", "What is this?", 160);
                                    eyes.Remote = null;
                                    eyes.Model = "gemma4:e4b";
                                    string ollamaSaw = eyes.See(picture, "Plain.", "What is this?", 160);
                                    eyes.Remote = new Cloud("sk-0123456789abcdef0123456789abcdef", "deepseek-flash");
                                    eyes.Model = "deepseek-flash";
                                    string deepSaw = null;
                                    try { eyes.See(picture, "Plain.", "What is this?", 160); }
                                    catch (BrainException error) { deepSaw = error.Message; }
                                    if (claudeSaw != "A crate menu in a space game." || nvidiaSaw != "Gemma sees a crate." || ollamaSaw != "Ollama sees a crate." || deepSaw != Brain.Blind) throw new Exception("Vision replies were wrong: " + claudeSaw + " / " + nvidiaSaw + " / " + ollamaSaw + " / " + deepSaw);
                                    System.Net.Sockets.TcpListener textOnly = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                                    textOnly.Start();
                                    ThreadPool.QueueUserWorkItem(delegate
                                    {
                                        try
                                        {
                                            using (System.Net.Sockets.TcpClient client = textOnly.AcceptTcpClient())
                                            using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                                            {
                                                byte[] buffer = new byte[65536];
                                                string request = "";
                                                while (request.IndexOf("\"model\"", StringComparison.Ordinal) < 0 || !request.TrimEnd().EndsWith("}", StringComparison.Ordinal))
                                                {
                                                    int read = stream.Read(buffer, 0, buffer.Length);
                                                    if (read <= 0) break;
                                                    request += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                                                }
                                                byte[] refusal = System.Text.Encoding.UTF8.GetBytes("{\"object\":\"error\",\"message\":\"ValueError: Received multimodal data but multimodal processing is not enabled. Use --enable-multimodal flag to enable multimodal processing.\",\"type\":\"BadRequestError\",\"code\":400}");
                                                byte[] head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: " + refusal.Length + "\r\nConnection: close\r\n\r\n");
                                                stream.Write(head, 0, head.Length);
                                                stream.Write(refusal, 0, refusal.Length);
                                            }
                                        }
                                        catch (Exception) { }
                                    });
                                    Brain.Nvidia = "http://127.0.0.1:" + ((IPEndPoint)textOnly.LocalEndpoint).Port;
                                    Cloud bolt = new Cloud("nvapi-test", "nvidia/nemotron-3.5-bolt-e4b");
                                    eyes.Remote = bolt;
                                    eyes.Model = bolt.Model;
                                    string boltSaw = null;
                                    try { eyes.See(picture, "Plain.", "What is this?", 160); }
                                    catch (BrainException error) { boltSaw = error.Message; }
                                    finally { textOnly.Stop(); Brain.Nvidia = seeUrl; }
                                    if (boltSaw != "nvidia/nemotron-3.5-bolt-e4b can't see screens. In Capri Mainframe, pick a model that can, like google/gemma-4-31b-it." || eyes.Unsighted != bolt.Model) throw new Exception("A text-only model's image error was not explained: " + boltSaw);
                                    brain.Remote = bolt;
                                    brain.Model = bolt.Model;
                                    brain.Unsighted = bolt.Model;
                                    string boltLooked = Hands.Do("look_at_screen", new ToolArguments());
                                    brain.Unsighted = null;
                                    if (boltLooked != Brain.Sightless(bolt)) throw new Exception("Looking with a text-only model did not say why: " + boltLooked);
                                    checks.Add("PASS text-only cloud model: NVIDIA's multimodal error becomes 'can't see screens, pick a model that can', and look and glances stop trying");
                                    Grab = delegate(Rectangle place) { Bitmap shot = new Bitmap(64, 36, PixelFormat.Format24bppRgb); using (Graphics graphics = Graphics.FromImage(shot)) graphics.Clear(Color.SteelBlue); return shot; };
                                    brain.Remote = new Cloud("nvapi-test", "google/gemma-4-31b-it");
                                    brain.Model = "google/gemma-4-31b-it";
                                    string looked = Hands.Do("look_at_screen", new ToolArguments { Focus = "the crate" });
                                    brain.Remote = new Cloud("sk-0123456789abcdef0123456789abcdef", "deepseek-flash");
                                    string blindly = Hands.Do("look_at_screen", new ToolArguments());
                                    if (looked != "You looked at their screen. You see: A level 26 player opening a salvage crate." || blindly != Brain.Blind) throw new Exception("The look tool was wrong: " + looked + " / " + blindly);
                                    string[] seeSent;
                                    lock (seeAsked) seeSent = seeAsked.ToArray();
                                    if (seeSent.Length != 4) throw new Exception("Vision made " + seeSent.Length + " requests.");
                                    string[][] seeWanted =
                                    {
                                        new[] { "POST /v1/messages", "\"system\":\"Plain.\"", "\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"AQIDBAUG\"}},{\"type\":\"text\",\"text\":\"What is this?\"}]" },
                                        new[] { "POST /v1/chat/completions", "\"content\":[{\"type\":\"text\",\"text\":\"What is this?\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,AQIDBAUG\"}}]" },
                                        new[] { "POST /api/chat", "\"images\":[\"AQIDBAUG\"]", "\"num_predict\":160", "\"content\":\"What is this?\"" },
                                        new[] { "POST /v1/chat/completions", "Pay attention to: the crate", "data:image/jpeg;base64,/9j/" }
                                    };
                                    for (int i = 0; i < seeWanted.Length; i++)
                                        foreach (string part in seeWanted[i])
                                            if (seeSent[i].IndexOf(part, StringComparison.Ordinal) < 0) throw new Exception("Vision request " + i + " is missing " + part + ": " + (seeSent[i].Length > 900 ? seeSent[i].Substring(0, 900) : seeSent[i]));
                                    foreach (string seeing in seeSent) if (seeing.IndexOf("@capri-image@", StringComparison.Ordinal) >= 0 || seeing.IndexOf("\"tools\":", StringComparison.Ordinal) >= 0) throw new Exception("A vision request kept its placeholder or sent tools.");
                                    lastPrint = null;
                                    using (Bitmap dark = new Bitmap(64, 36))
                                    using (Bitmap bright = new Bitmap(64, 36))
                                    {
                                        using (Graphics graphics = Graphics.FromImage(dark)) graphics.Clear(Color.Black);
                                        using (Graphics graphics = Graphics.FromImage(bright)) graphics.Clear(Color.White);
                                        if (!Changed(dark) || Changed(dark) || !Changed(bright)) throw new Exception("Screen change check was wrong.");
                                    }
                                    lastPrint = null;
                                    using (Bitmap wide = new Bitmap(2560, 1440))
                                    using (MemoryStream read = new MemoryStream(Jpeg(wide, 1280)))
                                    using (Image back = Image.FromStream(read))
                                        if (back.Width != 1280 || back.Height != 720) throw new Exception("Screens were not shrunk to 1280: " + back.Width + "x" + back.Height);
                                    using (Bitmap strip = new Bitmap(10, 2, PixelFormat.Format24bppRgb))
                                    using (Bitmap her = new Bitmap(4, 2, PixelFormat.Format32bppArgb))
                                    {
                                        for (int x = 0; x < 10; x++) for (int y = 0; y < 2; y++) strip.SetPixel(x, y, x == 0 ? Color.Black : x == 9 ? Color.White : y == 0 && (x == 3 || x == 5) ? Color.Blue : Color.Red);
                                        her.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));
                                        Smear(strip, null, 1, 1, 8, 1);
                                        Smear(strip, her, 3, 0, 4, 2);
                                        Color middle = strip.GetPixel(5, 1);
                                        Color hers = strip.GetPixel(4, 0);
                                        if (middle.R - middle.B > 10 || middle.R < 60 || middle.R > 200 || hers.R != 0 || hers.B != 255 || strip.GetPixel(6, 0).R != 255) throw new Exception("Her own pixels were not painted out of the capture: " + middle + " / " + hers + " / " + strip.GetPixel(6, 0));
                                    }
                                    brain.Remote = null;
                                    preferences.GlanceGap = 0;
                                    ToggleWatch();
                                    if (!preferences.Watch || !mainframe.WatchShown || mainframe.GapShown != "2 min") throw new Exception("Watch my screen did not turn on at 2 min.");
                                    mainframe.SlideGap(2);
                                    if (preferences.GlanceGap != 300 || mainframe.GapShown != "5 min" || nextGlance > clock.Elapsed.TotalSeconds + 21) throw new Exception("The glance slider did not set 5 min: " + preferences.GlanceGap + " / " + mainframe.GapShown);
                                    mainframe.SlideGap(-9);
                                    if (preferences.GlanceGap != 10 || mainframe.GapShown != "10 s") throw new Exception("The glance slider did not stop at 10 s: " + preferences.GlanceGap);
                                    TalkState talkWas = talkState;
                                    talkState = TalkState.Thinking;
                                    thinkingSince = clock.Elapsed.TotalSeconds - 10;
                                    CheckSlow(clock.Elapsed.TotalSeconds);
                                    if (slowWarned) throw new Exception("The slow-brain hint came too early.");
                                    thinkingSince = clock.Elapsed.TotalSeconds - 30;
                                    CheckSlow(clock.Elapsed.TotalSeconds);
                                    if (!slowWarned || mainframe.Status.IndexOf("free NVIDIA key", StringComparison.Ordinal) < 0) throw new Exception("A slow brain did not point to the free NVIDIA key: " + mainframe.Status);
                                    talkState = talkWas;
                                    thinkingSince = double.NaN;
                                    preferences.GlanceGap = 0;
                                    ToggleWatch();
                                    if (preferences.Watch || mainframe.WatchShown) throw new Exception("Watch my screen did not turn off.");
                                }
                                finally
                                {
                                    Brain.Anthropic = seeAnthropic;
                                    Brain.Nvidia = seeNvidia;
                                    Brain.Ollama = seeOllama;
                                    Grab = realGrab;
                                    brain.Remote = brainRemote;
                                    brain.Model = brainModel;
                                    seeFake.Stop();
                                }
                                checks.Add("PASS vision: Claude, OpenAI-style and Ollama image requests, DeepSeek refused, the look tool describes the screen, screens shrink to 1280 and repeat screens are skipped, a reply slower than 25 s points to a free NVIDIA key, Watch my screen toggles and its slider sets 10 s to 10 min, her own sprite and bubbles are painted out of the capture");
                            }
                            finally
                            {
                                Hands.Run = realRun;
                                Hands.Press = realPress;
                                Hands.Catalog = realCatalog;
                                Hands.Forget();
                                Brain.Ollama = realOllama;
                                fakeBrain.Stop();
                            }
                            checks.Add("PASS harness: finds apps by name and asks when several match, skips uninstallers and web links, opens checked websites and searches, refuses blocked sites, presses media keys, and the brain runs its tool calls then answers, falling back for models without tools and giving thinking models room to answer; plays named songs on Spotify or YouTube, and switches to, minimizes or closes open apps, closing a browser tab by its site and asking when several apps match");
                            if (!mainframe.Visible || !mainframe.ShowInTaskbar || mainframe.WindowState != FormWindowState.Normal) throw new Exception("Second launch did not open the Mainframe from the taskbar.");
                            Action<string> snap = delegate(string name)
                            {
                                using (Bitmap panel = new Bitmap(mainframe.Width, mainframe.Height))
                                {
                                    mainframe.DrawToBitmap(panel, new Rectangle(Point.Empty, mainframe.Size));
                                    panel.Save(Path.Combine(output, name), ImageFormat.Png);
                                }
                            };
                            mainframe.ShowCloud(new Cloud("sk-ant-test", "claude-opus-5-5"), new System.Collections.Generic.List<string> { "claude-opus-5-5", "claude-haiku-5-5" });
                            mainframe.ShowTextKey("test");
                            snap("mainframe-cloud.png");
                            preferences.Watch = true;
                            mainframe.Sync();
                            snap("mainframe-watch.png");
                            string overlap = mainframe.Overlap();
                            if (overlap != null) throw new Exception("Mainframe controls overlap: " + overlap);
                            preferences.Watch = false;
                            mainframe.Sync();
                            mainframe.ShowTextKey(null);
                            mainframe.ShowSetup(here, Engine.Fit(here), false, null);
                            if (mainframe.Status.IndexOf("No brain yet", StringComparison.Ordinal) < 0) throw new Exception("Setup card did not show.");
                            snap("mainframe-setup.png");
                            mainframe.ShowProgress(1, "Downloading " + Engine.Fit(here).Name, Engine.Fit(here).Size / 3, Engine.Fit(here).Size);
                            snap("mainframe-progress.png");
                            mainframe.ShowOllama(new[] { "gemma4:e4b", "qwen3:1.7b" }, "gemma4:e4b");
                            int tall = mainframe.ClientSize.Height;
                            snap("mainframe.png");
                            mainframe.ShowSetup(here, Engine.Fit(here), true, null);
                            if (mainframe.ClientSize.Height <= tall) throw new Exception("Setup card did not make room.");
                            mainframe.ShowOllama(new[] { "gemma4:e4b", "qwen3:1.7b" }, "gemma4:e4b");
                            ChooseModel("qwen3:1.7b");
                            if (brain.Model != "qwen3:1.7b" || preferences.Model != "qwen3:1.7b") throw new Exception("Model choice did not stick.");
                            string sealedKey = Voice.Protect("sk_test_capri_1234");
                            if (Voice.Unprotect(sealedKey) != "sk_test_capri_1234" || sealedKey.Contains("sk_test") || Voice.Unprotect("not a key") != null || Voice.Tail("sk_test_capri_1234") != "1234") throw new Exception("Key storage failed.");
                            if (Voice.Spoken("[GLITCH] Hey *choom*, [static] ready?") != "Hey choom, ready?" || Voice.Spoken("[STATIC]") != "") throw new Exception("Spoken text kept her tags: " + Voice.Spoken("[GLITCH] Hey *choom*, [static] ready?"));
                            if (Voice.Explain(401, "") != "ElevenLabs rejected the key. Press Forget, paste a new one." || Voice.Explain(401, "{\"detail\":{\"status\":\"quota_exceeded\"}}") != "Out of ElevenLabs credits. Untick Speak replies to type." || Voice.Explain(400, "{\"detail\":{\"status\":\"invalid\",\"message\":\"Text is \\\"too\\\" long\"}}") != "Text is \"too\" long" || Voice.Explain(500, "oops") != "ElevenLabs error 500.") throw new Exception("ElevenLabs errors read wrong.");
                            using (MemoryStream raw = new MemoryStream())
                            using (BinaryWriter writer = new BinaryWriter(raw))
                            {
                                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                                writer.Write(-1);
                                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                                writer.Write(16);
                                writer.Write((short)1);
                                writer.Write((short)1);
                                writer.Write(16000);
                                writer.Write(32000);
                                writer.Write((short)2);
                                writer.Write((short)16);
                                writer.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));
                                writer.Write(4);
                                writer.Write(System.Text.Encoding.ASCII.GetBytes("INFOdata"));
                                writer.Write(-1);
                                for (int i = 0; i < 16000; i++) writer.Write((short)(i < 8000 ? 0 : Math.Sin(i * 0.2) * (2000 + 14000 * Math.Abs(Math.Sin(i / 1300.0)))));
                                writer.Flush();
                                tone = Voice.Clean(raw.ToArray());
                            }
                            int toneRate;
                            if (Voice.Samples(tone, out toneRate).Length != 16000 || toneRate != 16000 || BitConverter.ToInt32(tone, 40) != 32000 || Math.Abs(Voice.Seconds(tone) - 1) > 0.001 || Voice.Samples(new byte[] { 82, 73, 70, 70, 0, 0, 0, 0, 87, 65, 86, 69, 102, 109, 116, 32, 248, 255, 255, 255 }, out toneRate).Length != 0) throw new Exception("WAV cleanup failed.");
                            int[] shapes = Voice.Mouths(tone, MouthRate);
                            int widest = -1;
                            for (int i = 7; i < shapes.Length; i++) widest = Math.Max(widest, shapes[i]);
                            if (shapes.Length != 12 || shapes[0] != -1 || shapes[4] != -1 || widest < 3) throw new Exception("Mouth envelope failed: " + string.Join(",", shapes));
                            if (Voice.Explain(401, "{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}") != "ElevenLabs rejected the key. Press Forget, paste a new one." || Voice.Explain(401, "{\"detail\":{\"status\":\"detected_unusual_activity\",\"message\":\"Unusual activity detected.\"}}") != "Unusual activity detected.") throw new Exception("ElevenLabs messages read wrong.");
                            System.Net.Sockets.TcpListener fake = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                            fake.Start();
                            string[] fakeHeads = { "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json", "HTTP/1.1 200 OK\r\nContent-Type: audio/wav" };
                            byte[][] fakeBodies = { System.Text.Encoding.UTF8.GetBytes("{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}"), tone };
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                try
                                {
                                    for (int i = 0; i < fakeHeads.Length; i++)
                                        using (System.Net.Sockets.TcpClient client = fake.AcceptTcpClient())
                                        using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                                        {
                                            MemoryStream got = new MemoryStream();
                                            byte[] buffer = new byte[4096];
                                            string text = "";
                                            while (true)
                                            {
                                                int read = stream.Read(buffer, 0, buffer.Length);
                                                if (read <= 0) break;
                                                got.Write(buffer, 0, read);
                                                text = System.Text.Encoding.UTF8.GetString(got.ToArray());
                                                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                                if (end < 0) continue;
                                                System.Text.RegularExpressions.Match length = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                if (got.Length >= end + 4 + (length.Success ? int.Parse(length.Groups[1].Value) : 0)) break;
                                            }
                                            lock (asked) asked.Add(text);
                                            byte[] head = System.Text.Encoding.ASCII.GetBytes(fakeHeads[i] + "\r\nContent-Length: " + fakeBodies[i].Length + "\r\nConnection: close\r\n\r\n");
                                            stream.Write(head, 0, head.Length);
                                            stream.Write(fakeBodies[i], 0, fakeBodies[i].Length);
                                        }
                                }
                                catch (Exception) { }
                            });
                            string realHost = Voice.Host;
                            Voice.Host = "http://127.0.0.1:" + ((IPEndPoint)fake.LocalEndpoint).Port;
                            byte[] fetched;
                            try
                            {
                                Voice probe = new Voice();
                                probe.Key = "sk_test_capri_1234";
                                probe.VoiceId = "abc";
                                probe.Model = "eleven_v3";
                                try { probe.Speak("Hi \"choom\"."); throw new Exception("A 401 reply did not throw."); }
                                catch (VoiceException error) { if (error.Status != 401 || error.Message != "ElevenLabs rejected the key. Press Forget, paste a new one.") throw new Exception("HTTP errors read wrong: " + error.Status + " " + error.Message); }
                                fetched = probe.Speak("Hi \"choom\".");
                            }
                            finally
                            {
                                Voice.Host = realHost;
                                fake.Stop();
                            }
                            string sent;
                            lock (asked) sent = string.Join("\n", asked.ToArray());
                            if (fetched.Length != tone.Length || sent.IndexOf("POST /v1/text-to-speech/abc?output_format=wav_24000", StringComparison.Ordinal) < 0 || sent.IndexOf("xi-api-key: sk_test_capri_1234", StringComparison.OrdinalIgnoreCase) < 0 || sent.IndexOf("{\"text\":\"Hi \\\"chewm\\\".\",\"model_id\":\"eleven_v3\"}", StringComparison.Ordinal) < 0) throw new Exception("ElevenLabs request was wrong: " + sent);
                            if (Voice.Sayable("Yezzirrr! Choom, that's PREEM, chooms. Supreme.") != "Yessirrr! Chewm, that's PREME, chewms. Supreme." || Voice.Unsaid("Chewm, a preme rig. Supreme chewms.") != "Choom, a preem rig. Supreme chooms.") throw new Exception("Slang respelling is wrong: " + Voice.Sayable("Choom, that's PREEM, chooms. Supreme."));
                            VoicePage page = Voice.Read<VoicePage>(System.Text.Encoding.UTF8.GetBytes("{\"voices\":[{\"voice_id\":\"abc\",\"name\":\"Capri 1\",\"category\":\"generated\",\"labels\":{}},{\"voice_id\":\"def\",\"name\":\"Rachel\",\"category\":\"premade\"}],\"has_more\":false,\"total_count\":2,\"next_page_token\":null}"));
                            System.Collections.Generic.List<VoiceModel> listed = Voice.Read<System.Collections.Generic.List<VoiceModel>>(System.Text.Encoding.UTF8.GetBytes("[{\"model_id\":\"eleven_v3\",\"name\":\"Eleven v3\",\"can_do_text_to_speech\":true,\"languages\":[]}]"));
                            if (page.Voices.Count != 2 || page.More || page.Voices[0].Name != "Capri 1" || listed.Count != 1 || !listed[0].Speaks) throw new Exception("ElevenLabs listing did not parse.");
                            voices = new VoiceListing();
                            voices.Voices.AddRange(page.Voices);
                            voices.Models.AddRange(listed);
                            voice.Key = "sk_test_capri_1234";
                            ChooseVoice("abc");
                            ChooseVoiceModel("eleven_v3");
                            if (preferences.VoiceId != "abc" || preferences.VoiceModel != "eleven_v3" || mainframe.VoiceStatus != "ElevenLabs · Capri 1 | abc | v3 | Forget") throw new Exception("Voice picks did not show: " + mainframe.VoiceStatus);
                            mainframe.ShowShortcut(true, true);
                            if (!mainframe.MicStatus.EndsWith("| - | keys | mute", StringComparison.Ordinal)) throw new Exception("The Ctrl + Alt + V and N hints did not show with a key: " + mainframe.MicStatus);
                            snap("mainframe-voice.png");
                            string crowded = mainframe.Overlap();
                            if (crowded != null) throw new Exception("Mainframe voice controls overlap: " + crowded);
                            ChooseVoice("  ");
                            ChooseVoiceModel(null);
                            if (voice.VoiceId != null || preferences.VoiceId != null || voice.Model != null || mainframe.VoiceStatus != "Needs your Voice ID. |  | Default model | Forget") throw new Exception("Blank voice was not cleared: " + mainframe.VoiceStatus);
                            CloseTalk();
                            pill.Press(2);
                            if (pill.Chatting || chatting || reply != "Voice chat needs my Voice ID, choom. Right-click me, open Capri Mainframe and pick a voice in the Voice ID box under VOICE. Type to me for now.") throw new Exception("Wave without a Voice ID did not point to the Mainframe.");
                            CloseTalk();
                            preferences.VoiceKey = sealedKey;
                            ForgetVoice();
                            string keylessNote = kokoro.Installed ? "Free voice, runs on your PC." : "Get her free voice (" + Engine.Amount(LocalVoice.PackSize) + "), or paste a key.";
                            if (voice.Key != null || voices != null || preferences.VoiceKey != null || mainframe.VoiceStatus != keylessNote + " |  | Default model | Connect") throw new Exception("Forget did not clear the key: " + mainframe.VoiceStatus);
                            snap("mainframe-voice-empty.png");
                            if (!mainframe.MicStatus.EndsWith("| help | - | mute", StringComparison.Ordinal)) throw new Exception("Without a key the Mainframe should offer Get a key, not the shortcut: " + mainframe.MicStatus);
                            mainframe.ShowShortcut(false, false);
                            if (!mainframe.MicStatus.EndsWith("| help | - | -", StringComparison.Ordinal)) throw new Exception("The Ctrl + Alt + N hint showed while the key was taken: " + mainframe.MicStatus);
                            System.Collections.Generic.List<Mic> found = Ears.List();
                            ChooseMic("{0.0.1.00000000}.{missing}");
                            if (preferences.Mic != "{0.0.1.00000000}.{missing}" || ears.Chosen != preferences.Mic || mainframe.MicStatus.IndexOf("| Mic: default |", StringComparison.Ordinal) < 0) throw new Exception("Missing mic did not fall back to the default label: " + mainframe.MicStatus);
                            if (found.Count > 0)
                            {
                                ChooseMic(found[0].Id);
                                if (mainframe.MicStatus.IndexOf("| Mic: " + found[0].Name + " |", StringComparison.Ordinal) < 0) throw new Exception("Mic pick did not show its name: " + mainframe.MicStatus);
                                snap("mainframe-mic.png");
                            }
                            ChooseMic(null);
                            if (preferences.Mic != null || ears.Chosen != null) throw new Exception("Default mic did not clear the pick.");
                            checks.Add("PASS mic list (" + found.Count + " found) with full names, a saved pick, and a missing mic falls back to the default");
                            string freeSetupText = kokoro.Installed ? "-" : "Get free voice";
                            if (mainframe.FreeStatus != "Free voice: Her own | " + freeSetupText) throw new Exception("Free voice row did not show without a key: " + mainframe.FreeStatus);
                            if (mainframe.Overlap() != null) throw new Exception("The free voice row overlaps: " + mainframe.Overlap());
                            ChooseVoiceStyle("Sly");
                            if (kokoro.Style.Speaker != 6 || preferences.VoiceStyle != "Sly" || mainframe.FreeStatus != "Free voice: Sly | " + freeSetupText) throw new Exception("Free voice pick did not stick: " + mainframe.FreeStatus);
                            ChooseVoiceStyle("nobody");
                            if (kokoro.Style != LocalVoice.Styles[0] || preferences.VoiceStyle != null) throw new Exception("Unknown free voice did not fall back to her own.");
                            byte[] pack;
                            using (MemoryStream zipped = new MemoryStream())
                            {
                                using (System.IO.Compression.ZipArchive archive = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, true))
                                    foreach (string name in LocalVoice.Needed)
                                        using (Stream entry = archive.CreateEntry(name.Replace('\\', '/')).Open()) entry.WriteByte(1);
                                pack = zipped.ToArray();
                            }
                            System.Collections.Generic.List<string> packAsked = new System.Collections.Generic.List<string>();
                            System.Net.Sockets.TcpListener packFake;
                            string packHost = FakeServer(new string[0], packAsked, out packFake);
                            packFake.Stop();
                            System.Net.Sockets.TcpListener packServer = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                            packServer.Start();
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                try
                                {
                                    using (System.Net.Sockets.TcpClient client = packServer.AcceptTcpClient())
                                    using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                                    {
                                        byte[] buffer = new byte[4096];
                                        string request = "";
                                        while (request.IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                                        {
                                            int read = stream.Read(buffer, 0, buffer.Length);
                                            if (read <= 0) break;
                                            request += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                                        }
                                        byte[] head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: " + pack.Length + "\r\nConnection: close\r\n\r\n");
                                        stream.Write(head, 0, head.Length);
                                        stream.Write(pack, 0, pack.Length);
                                    }
                                }
                                catch (Exception) { }
                            });
                            string packUrl = LocalVoice.PackUrl;
                            string packSha = LocalVoice.PackSha;
                            long packSize = LocalVoice.PackSize;
                            string packRoot = Path.Combine(Path.GetDirectoryName(settingsPath), "pack-test");
                            LocalVoice fetchedVoice = new LocalVoice(packRoot);
                            System.Collections.Generic.List<string> packSteps = new System.Collections.Generic.List<string>();
                            try
                            {
                                LocalVoice.PackUrl = "http://127.0.0.1:" + ((IPEndPoint)packServer.LocalEndpoint).Port + "/capri-voice-1.zip";
                                LocalVoice.PackSize = pack.Length;
                                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) LocalVoice.PackSha = BitConverter.ToString(sha.ComputeHash(pack)).Replace("-", "").ToLowerInvariant();
                                if (fetchedVoice.Installed) throw new Exception("An empty folder counted as a voice pack.");
                                fetchedVoice.Install(delegate(string text, long done, long total) { packSteps.Add(text); });
                            }
                            finally
                            {
                                LocalVoice.PackUrl = packUrl;
                                LocalVoice.PackSha = packSha;
                                LocalVoice.PackSize = packSize;
                                packServer.Stop();
                            }
                            string packFolder = Path.Combine(Path.Combine(packRoot, "voice"), LocalVoice.Pack);
                            if (!fetchedVoice.Installed || File.Exists(packFolder + ".zip") || Directory.Exists(packFolder + ".new") || !packSteps.Contains("Checking her free voice") || !packSteps.Contains("Unpacking her free voice")) throw new Exception("Voice pack did not install: " + string.Join(", ", packSteps.ToArray()));
                            Directory.Delete(packRoot, true);
                            checks.Add("PASS free voice: Free voice row and Get free voice button without a key, four styles with fallback, pack download + checksum + unpack from a fake server");
                            if (kokoro.Installed)
                            {
                                byte[] freeLine = kokoro.Speak("Hey choom, this is my free voice.");
                                if (freeLine == null || freeLine.Length < 24000) throw new Exception("Free voice made no sound: " + (freeLine == null ? "null" : freeLine.Length + " bytes"));
                                checks.Add("PASS free voice speech: " + kokoro.Style.Name + " said a line, " + freeLine.Length + " bytes of WAV");
                            }
                            else checks.Add("LIMIT free voice synthesis not run; the voice pack is not installed on this PC");
                            checks.Add("PASS voice: key sealed with DPAPI, tags stripped, errors read, real HTTP round trips against a fake ElevenLabs (401 explained, audio fetched), WAV cleanup, mouth from the audio, listing parse, voice and model picks, a blank Voice ID asks for one, forget");
                            mainframe.Close();
                            if (mainframe.IsDisposed || !mainframe.Visible || mainframe.WindowState != FormWindowState.Minimized) throw new Exception("Closing the Mainframe quit instead of minimizing.");
                            checks.Add("PASS Mainframe starts in the taskbar, a second launch opens it, it picks a model, and its close button minimizes it");
                            Think = delegate(string words) { thoughts++; return words == "hi" ? "Hey choom! Net's been quiet, too quiet. Time to stir up some trouble." : "Huh?"; };
                            Point body = Point.Empty;
                            for (int y = 0; y < frame.Height && body.IsEmpty; y += 4) for (int x = 0; x < frame.Width; x += 4) if (frame.GetPixel(x, y).A == 255) { body = new Point(x, y); break; }
                            if (body.IsEmpty) throw new Exception("No solid pixel on her body.");
                            if (!Solid(body, true)) throw new Exception("Her window is not what's under her body at " + PointToScreen(body) + ".");
                            using (Form cover = new Form())
                            {
                                Point spot = PointToScreen(body);
                                cover.FormBorderStyle = FormBorderStyle.None;
                                cover.ShowInTaskbar = false;
                                cover.StartPosition = FormStartPosition.Manual;
                                cover.TopMost = true;
                                cover.Bounds = new Rectangle(spot.X - 20, spot.Y - 20, 40, 40);
                                cover.Show();
                                if (Solid(body, true)) throw new Exception("A window over her still counted as her for the pill.");
                            }
                            checks.Add("PASS the pill only wakes for her body when no window covers her");
                            pillUntil = clock.Elapsed.TotalSeconds + 30;
                            UpdatePill(clock.Elapsed.TotalSeconds);
                            if (!pill.Visible || pill.Top < Bottom - 60 || Math.Abs(pill.Left + pill.Width / 2 - (Left + Width / 2)) > Width / 2) throw new Exception("Talk pill is not under her.");
                            pill.Press(1);
                            if (!typer.Visible || typer.Bottom > Top + PaddingSize) throw new Exception("Pencil did not open the typing box above her.");
                            typer.Send("hi");
                            if (!typer.Visible) throw new Exception("Sending closed the typing box.");
                            if (typer.Draft.Length > 0 || talkState != TalkState.Thinking || heard != "hi") throw new Exception("Sending did not start thinking: " + talkState + " draft '" + typer.Draft + "'");
                            RenderFrame();
                            if (!box.Visible || box.State != TalkState.Thinking) throw new Exception("Speech box did not open on THINKING.");
                            typer.Hide();
                            checks.Add("PASS pill under her, pencil opens the typing box above her head, Enter sends, THINKING shows and the typing box stays open");
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
                            lastForeground = new IntPtr(-1);
                            StayOnTop();
                            bool above = false;
                            for (IntPtr below = Native.GetWindow(Handle, 2); below != IntPtr.Zero; below = Native.GetWindow(below, 2)) if (below == backdrop.Handle) { above = true; break; }
                            if (!above) throw new Exception("Did not rise above a newer topmost window.");
                            checks.Add("PASS rises back above a newer always-on-top window");
                            RenderFrame();
                        }
                        else if (step == 2)
                        {
                            const string expected = "Hey choom! Net's been quiet, too quiet. Time to stir up some trouble.";
                            if (talkState != TalkState.Talking || reply != expected || speech != expected) throw new Exception("Brain reply did not start talking: " + talkState + " " + reply);
                            replyStart = clock.Elapsed.TotalSeconds - 0.9;
                            speechStart = replyStart;
                            RenderFrame();
                            if (box.State != TalkState.Talking || box.Revealed < 20 || box.Revealed >= expected.Length) throw new Exception("Typewriter did not reveal part of the reply.");
                            using (Bitmap typing = new Bitmap(box.Image)) typing.Save(Path.Combine(output, "talk-typing.png"), ImageFormat.Png);
                            replyStart -= 10;
                            speechStart = replyStart;
                            RenderFrame();
                            if (talkState != TalkState.Online || speech != null || box.State != TalkState.Online) throw new Exception("Did not finish talking into ONLINE.");
                            using (Bitmap sheet = new Bitmap(Math.Max(box.Image.Width, pill.Image.Width) + 20, box.Image.Height + pill.Image.Height + 30, PixelFormat.Format32bppArgb))
                            using (Graphics graphics = Graphics.FromImage(sheet))
                            {
                                graphics.Clear(Color.FromArgb(44, 47, 56));
                                graphics.DrawImage(box.Image, 10, 10, box.Image.Width, box.Image.Height);
                                graphics.DrawImage(pill.Image, 10 + (box.Image.Width - pill.Image.Width) / 2, box.Image.Height + 20, pill.Image.Width, pill.Image.Height);
                                sheet.Save(Path.Combine(output, "talk.png"), ImageFormat.Png);
                            }
                            box.Leaving = clock.Elapsed.TotalSeconds - 1;
                            RenderFrame();
                            if (box.Visible || talkState != TalkState.Off) throw new Exception("Dismissing did not close the speech box.");
                            Hear("send nudes");
                            string refused = reply;
                            Hear("i want to kill myself");
                            if (thoughts != 1 || refused == null || refused == expected || talkState != TalkState.Talking || reply != Safety.HelpLine) throw new Exception("Safety lines reached the brain.");
                            Think = null;
                            brain.Model = null;
                            Hear("hello");
                            if (talkState != TalkState.Dormant || reply != Brain.Offline) throw new Exception("Missing brain did not go DORMANT.");
                            replyStart -= 10;
                            speechStart = replyStart;
                            RenderFrame();
                            box.Opened = clock.Elapsed.TotalSeconds - 1;
                            RenderFrame();
                            pill.Press(1);
                            typer.Draft = "nice, what are you up to?";
                            typer.Refresh();
                            if (!typer.Visible || !box.Visible || typer.Bottom > box.PanelOnScreen.Top || typer.Left != box.PanelOnScreen.Left) throw new Exception("Typing box did not sit above her message.");
                            using (Bitmap pair = new Bitmap(box.Width + 20, box.Bottom - typer.Top + 20, PixelFormat.Format32bppArgb))
                            using (Graphics graphics = Graphics.FromImage(pair))
                            {
                                graphics.Clear(Color.FromArgb(44, 47, 56));
                                graphics.CopyFromScreen(typer.Left, typer.Top, 10 + typer.Left - box.Left, 10, typer.Size);
                                graphics.DrawImage(box.Image, 10, 10 + box.Top - typer.Top, box.Image.Width, box.Image.Height);
                                pair.Save(Path.Combine(output, "reply-above.png"), ImageFormat.Png);
                            }
                            typer.Hide();
                            checks.Add("PASS talk loop: TALKING types out with her mouth, ONLINE, dismiss closes; refusal and help line skip the brain; no brain shows DORMANT; reply box sits above her message");
                            Speak = delegate(string words) { voiced = words; return tone; };
                            heard = "voice check";
                            Answer(++asking, "[GLITCH] Voice link online. Miss me, choom?", TalkState.Talking);
                            if (talkState != TalkState.Thinking || reply != null) throw new Exception("Voice did not hold THINKING while she found her words.");
                        }
                        else if (step == 3)
                        {
                            const string line = "[GLITCH] Voice link online. Miss me, choom?";
                            if (voiced != "Voice link online. Miss me, choom?") throw new Exception("Voice was sent the wrong words: " + voiced);
                            if (talkState != TalkState.Talking || reply != line || mouths == null || mouths.Length != 12 || Math.Abs(pace - line.Length / Voice.Seconds(tone)) > 0.01) throw new Exception("Voice reply did not start in time with the audio: " + talkState + " " + pace);
                            double now = clock.Elapsed.TotalSeconds;
                            speechStart = now - 0.1;
                            if (MouthCell(now, idle) != -1) throw new Exception("Mouth moved during silence.");
                            speechStart = now - 0.75;
                            if (MouthCell(now, idle) < 0) throw new Exception("Mouth stayed shut while she spoke.");
                            replyStart = now - 0.5;
                            RenderFrame();
                            if (box.Revealed < line.Length / 2 - 2 || box.Revealed > line.Length / 2 + 2) throw new Exception("Typing did not keep pace with the audio: " + box.Revealed);
                            speechStart = now - 1.1;
                            if (MouthCell(now, idle) != -1 || speech != null) throw new Exception("Mouth kept going after the audio ended.");
                            Speak = delegate(string words) { throw new VoiceException("Out of ElevenLabs credits. Untick Speak replies to type."); };
                            Answer(++asking, "Still here.", TalkState.Talking);
                        }
                        else if (step == 4)
                        {
                            if ((talkState != TalkState.Talking && talkState != TalkState.Online) || reply != "Still here." || mouths != null || pace != LettersPerSecond || mainframe.VoiceStatus.IndexOf("Out of ElevenLabs credits. Untick Speak replies to type.", StringComparison.Ordinal) != 0) throw new Exception("Voice failure did not fall back to text: " + mainframe.VoiceStatus);
                            checks.Add("PASS voice reply: THINKING while it speaks, tags not read aloud, typing paced to the audio, mouth follows the audio, failure falls back to text and shows in the Mainframe");
                            bool talked = false;
                            Speak = delegate(string words) { talked = true; return tone; };
                            ToggleSpeaking();
                            if (!preferences.Quiet || mainframe.SpeakShown) throw new Exception("Speak replies did not turn off.");
                            Answer(++asking, "Quiet mode.", TalkState.Talking);
                            if (talked || reply != "Quiet mode." || mouths != null) throw new Exception("She still spoke with Speak replies off.");
                            ToggleSpeaking();
                            if (preferences.Quiet || !mainframe.SpeakShown) throw new Exception("Speak replies did not turn back on.");
                            checks.Add("PASS speak replies: off shows her reply as text without calling ElevenLabs, on again speaks");
                            if (!preferences.OnTop) ToggleOnTop();
                            ToggleOnTop();
                            bool lowered = true;
                            foreach (Form above in new Form[] { pill, box, typer }) if (above.Owner != this || (above.IsHandleCreated && (Native.GetWindowLong(above.Handle, -20) & 0x8) != 0)) lowered = false;
                            ToggleOnTop();
                            bool raised = box.IsHandleCreated && (Native.GetWindowLong(box.Handle, -20) & 0x8) != 0;
                            if (!lowered || !raised || !preferences.OnTop) throw new Exception("Her bubble did not follow Always on top: " + lowered + " / " + raised);
                            checks.Add("PASS bubbles follow Always on top: with it off her reply bubble, pill and typing box sit with her behind other windows");
                            Speak = null;
                            CloseTalk();
                            Directory.CreateDirectory(Path.GetDirectoryName(whisper.Exe));
                            File.WriteAllText(whisper.Exe, "");
                            File.WriteAllText(whisper.Model, "");
                            pill.Press(2);
                            if (!pill.Chatting || !chatting || !local) throw new Exception("Wave without a key did not start free voice chat.");
                            pill.Press(2);
                            if (pill.Chatting || chatting || local) throw new Exception("Wave did not end free voice chat.");
                            Message talkPress = Message.Create(Handle, 0x0312, new IntPtr(TalkKeyId), IntPtr.Zero);
                            WndProc(ref talkPress);
                            if (!pill.Chatting || !chatting || !local) throw new Exception("Ctrl + Alt + V without a key did not start free voice chat.");
                            WndProc(ref talkPress);
                            if (pill.Chatting || chatting || local) throw new Exception("Ctrl + Alt + V did not end free voice chat.");
                            CloseTalk();
                            System.Collections.Generic.List<string> browsed = new System.Collections.Generic.List<string>();
                            Action<string> realBrowse = Browse;
                            try
                            {
                                Browse = browsed.Add;
                                OpenKeyPage();
                                if (reply != GuideLine || heard != "get a key") throw new Exception("Get a key did not tell them to follow the guide: " + reply);
                                CloseTalk();
                                OpenFreeKeyPage();
                                if (reply != FreeKeyLine || heard != "free key") throw new Exception("Free key did not explain the NVIDIA key: " + reply);
                                CloseTalk();
                                ((ToolStripMenuItem)helpItem.DropDownItems[0]).PerformClick();
                                if (string.Join(" ", browsed.ToArray()) != GuidePage + " " + FreeKeyPage + " " + DiscordPage) throw new Exception("Key buttons or Join the Discord opened the wrong pages: " + string.Join(" ", browsed.ToArray()));
                            }
                            finally { Browse = realBrowse; }
                            checks.Add("PASS key buttons: Get a key opens the setup guide's voice steps and Free key opens NVIDIA's key page, and she says what to do for each");
                            mainframe.WindowState = FormWindowState.Minimized;
                            if (TalkPill.Render(1, 0).Width != TalkPill.Measure(1).Width) throw new Exception("Mic button showed outside voice chat.");
                            System.Net.Sockets.TcpListener free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                            free.Start();
                            int port = ((IPEndPoint)free.LocalEndpoint).Port;
                            free.Stop();
                            fakeAgent = new HttpListener();
                            fakeAgent.Prefixes.Add("http://localhost:" + port + "/");
                            fakeAgent.Start();
                            short[] line = new short[24000];
                            for (int i = 0; i < line.Length; i++) line[i] = (short)(Math.Sin(i * 0.25) * 9000);
                            byte[] lineBytes = new byte[line.Length * 2];
                            Buffer.BlockCopy(line, 0, lineBytes, 0, lineBytes.Length);
                            string lineAudio = Convert.ToBase64String(lineBytes);
                            HttpListener server = fakeAgent;
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                try
                                {
                                    Action<HttpListenerContext, string> answer = delegate(HttpListenerContext context, string json)
                                    {
                                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                                        context.Response.ContentType = "application/json";
                                        context.Response.ContentLength64 = bytes.Length;
                                        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                                        context.Response.Close();
                                    };
                                    System.Net.WebSockets.WebSocket socket = null;
                                    Func<string> receive = delegate
                                    {
                                        byte[] buffer = new byte[65536];
                                        using (MemoryStream message = new MemoryStream())
                                            while (true)
                                            {
                                                System.Net.WebSockets.WebSocketReceiveResult got = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                                                if (got.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
                                                message.Write(buffer, 0, got.Count);
                                                if (got.EndOfMessage) return System.Text.Encoding.UTF8.GetString(message.ToArray());
                                            }
                                    };
                                    Action<string> send = delegate(string json) { socket.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(json)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait(); };
                                    Func<string, string> chunk = delegate(string text)
                                    {
                                        int at = text.IndexOf("\"user_audio_chunk\":\"", StringComparison.Ordinal);
                                        if (at < 0) return null;
                                        at += 20;
                                        byte[] audio = Convert.FromBase64String(text.Substring(at, text.IndexOf('"', at) - at));
                                        foreach (byte value in audio) if (value != 0) return "loud";
                                        return "silent";
                                    };
                                    int made = 0;
                                    string keptTools = "{\"tools\":[{\"id\":\"tool_kept\",\"tool_config\":{\"type\":\"client\",\"name\":\"open_app\",\"description\":" + Voice.Quote(Hands.Description(0)) + "}},{\"id\":\"tool_stale\",\"tool_config\":{\"type\":\"client\",\"name\":\"media\",\"description\":\"old\"}}],\"has_more\":false}";
                                    for (int conversation = 1; ; conversation++)
                                    {
                                        HttpListenerContext talk;
                                        while (true)
                                        {
                                            HttpListenerContext request = server.GetContext();
                                            if (request.Request.IsWebSocketRequest) { talk = request; break; }
                                            string body;
                                            using (StreamReader reader = new StreamReader(request.Request.InputStream)) body = reader.ReadToEnd();
                                            string path = request.Request.Url.PathAndQuery;
                                            lock (asked) asked.Add("http " + request.Request.HttpMethod + " " + path + " " + request.Request.Headers["xi-api-key"] + " " + body);
                                            answer(request, path.IndexOf("get-signed-url", StringComparison.Ordinal) >= 0 ? "{\"signed_url\":\"ws://localhost:" + port + "/talk?token=t\"}" : path.StartsWith("/v1/convai/tools", StringComparison.Ordinal) ? (request.Request.HttpMethod == "GET" ? keptTools : "{\"id\":\"tool_" + (++made) + "\"}") : "{\"agent_id\":\"agent_test\"}");
                                        }
                                        socket = talk.AcceptWebSocketAsync(null).Result.WebSocket;
                                        lock (asked) asked.Add("init " + receive());
                                        send("{\"type\":\"conversation_initiation_metadata\",\"conversation_initiation_metadata_event\":{\"conversation_id\":\"conv_test\",\"agent_output_audio_format\":\"pcm_16000\",\"user_input_audio_format\":\"pcm_16000\"}}");
                                        if (conversation > 1)
                                        {
                                            int woke = 0;
                                            while (woke < 3)
                                            {
                                                string text = receive();
                                                if (text == null) break;
                                                string kind = chunk(text);
                                                if (kind == null) continue;
                                                lock (asked) asked.Add("woke " + kind);
                                                if (kind == "loud") woke++;
                                            }
                                            socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait();
                                            continue;
                                        }
                                        send("{\"type\":\"ping\",\"ping_event\":{\"event_id\":7,\"ping_ms\":20}}");
                                        bool ponged = false;
                                        int chunks = 0;
                                        while (!ponged || chunks < 3)
                                        {
                                            string text = receive();
                                            if (text == null) return;
                                            if (text.IndexOf("\"pong\"", StringComparison.Ordinal) >= 0) { ponged = true; lock (asked) asked.Add("pong " + text); }
                                            string kind = chunk(text);
                                            if (kind != null) { chunks++; lock (asked) asked.Add("chunk " + kind); }
                                        }
                                        send("{\"type\":\"user_transcript\",\"user_transcription_event\":{\"user_transcript\":\"hi\"}}");
                                        send("{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"[GLITCH] Loud and clear, chewm.\"}}");
                                        send("{\"type\":\"audio\",\"audio_event\":{\"audio_base_64\":\"" + lineAudio + "\",\"event_id\":1,\"alignment\":{\"chars\":[\"L\"],\"char_durations_ms\":[40],\"char_start_times_ms\":[0]}}}");
                                        send("{\"type\":\"client_tool_call\",\"client_tool_call\":{\"tool_name\":\"open_app\",\"tool_call_id\":\"call_1\",\"parameters\":{\"name\":\"Spotify\"},\"event_id\":3,\"expects_response\":true}}");
                                        try
                                        {
                                            while (true)
                                            {
                                                string text = receive();
                                                if (text == null) break;
                                                string kind = chunk(text);
                                                if (kind != null) lock (asked) asked.Add("late " + kind);
                                                else if (text.IndexOf("client_tool_result", StringComparison.Ordinal) >= 0) lock (asked) asked.Add("tool " + text);
                                            }
                                        }
                                        catch (Exception) { }
                                    }
                                }
                                catch (Exception error) { lock (asked) asked.Add("server " + error.GetType().Name); }
                            });
                            agentHost = Voice.Host;
                            handsRun = Hands.Run;
                            handsCatalog = Hands.Catalog;
                            Hands.Catalog = delegate { return new System.Collections.Generic.List<InstalledApp> { new InstalledApp("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") }; };
                            Hands.Run = delegate(string id) { lock (asked) asked.Add("ran " + id); };
                            Hands.Forget();
                            Voice.Host = "http://localhost:" + port;
                            voice.Key = "sk_test_capri_1234";
                            voice.VoiceId = "abc";
                            if (Agent.Create(voice.Key, voice.VoiceId) != "agent_test") throw new Exception("ElevenLabs agent was not made.");
                            preferences.AgentId = "agent_old";
                            preferences.AgentVersion = 1;
                            CloseTalk();
                            pill.Press(2);
                            if (!pill.Chatting || pill.Muted || !chatting || TalkPill.Render(1, 0, true).Width <= TalkPill.Measure(1).Width + 30) throw new Exception("Wave did not start voice chat with the mic button apart on the right.");
                            Bitmap[] chips = { TalkPill.Render(2, 0, true, false, 2), TalkPill.Render(2, 3, true, false, 6), TalkPill.Render(2, 0, true, true), TalkPill.Render(2, 0) };
                            using (Bitmap sheet = new Bitmap(chips[0].Width + 40, chips.Length * (chips[0].Height + 16) + 16, PixelFormat.Format32bppArgb))
                            using (Graphics graphics = Graphics.FromImage(sheet))
                            {
                                graphics.Clear(Color.FromArgb(214, 220, 228));
                                for (int i = 0; i < chips.Length; i++) graphics.DrawImage(chips[i], 20, 16 + i * (chips[0].Height + 16), chips[i].Width, chips[i].Height);
                                sheet.Save(Path.Combine(output, "pill-mic.png"), ImageFormat.Png);
                            }
                            foreach (Bitmap chip in chips) chip.Dispose();
                        }
                        else if (step == 5)
                        {
                            string log;
                            lock (asked) log = string.Join(" | ", asked.ToArray());
                            if (agent == null || !agent.Ready || preferences.AgentId != "agent_old" || preferences.AgentVersion != Agent.Version || mainframe.VoiceStatus.IndexOf("Voice chat on", StringComparison.Ordinal) != 0) throw new Exception("Voice chat did not open the agent link: " + mainframe.VoiceStatus + " / " + log);
                            short[] loud = new short[Ears.Chunk];
                            for (int i = 0; i < loud.Length; i++) loud[i] = (short)(Math.Sin(i * 0.3) * 9000);
                            pill.Press(3);
                            if (!pill.Muted || !ears.Muted) throw new Exception("Mic button did not mute the mic.");
                            ears.Feed(loud);
                            pill.Press(3);
                            if (pill.Muted || ears.Muted) throw new Exception("Mic button did not unmute the mic.");
                            Message mutePress = Message.Create(Handle, 0x0312, new IntPtr(MuteKeyId), IntPtr.Zero);
                            WndProc(ref mutePress);
                            if (!pill.Muted || !ears.Muted) throw new Exception("Ctrl + Alt + N did not mute the mic.");
                            WndProc(ref mutePress);
                            if (pill.Muted || ears.Muted) throw new Exception("Ctrl + Alt + N did not unmute the mic.");
                            ears.Feed(loud);
                            ears.Feed(loud);
                        }
                        else if (step == 6)
                        {
                            string log;
                            lock (asked) log = string.Join(" | ", asked.ToArray());
                            if (heard != "hi" || reply != "Loud and clear, choom." || (talkState != TalkState.Talking && talkState != TalkState.Online) || !speaker.Busy) throw new Exception("Voice chat did not hear and answer: " + heard + " / " + reply + " / " + talkState + " / " + log);
                            Play(idle);
                            if (MouthCell(clock.Elapsed.TotalSeconds, idle) < 0) throw new Exception("Her mouth did not follow the agent's audio.");
                            string[] wanted = { "http POST /v1/convai/agents/create sk_test_capri_1234 {\"name\":\"Capri\"", "\"turn\":{\"turn_timeout\":15,\"silence_end_call_timeout\":45}", "http PATCH /v1/convai/agents/agent_old sk_test_capri_1234 {\"conversation_config\":{\"turn\":{\"turn_timeout\":15,\"silence_end_call_timeout\":45},\"agent\":{\"prompt\":{\"prompt\":\"You are Capri", "\"tool_ids\":[\"tool_kept\",\"tool_1\",\"tool_2\",\"tool_3\",\"tool_4\",\"tool_5\"]", "\"tool_ids\":[\"tool_kept\",\"tool_6\",\"tool_7\",\"tool_8\",\"tool_9\",\"tool_10\"]}}}}", "\"name\":\"play_music\"", "\"name\":\"app_window\"", "\"name\":\"look_at_screen\"", "http GET /v1/convai/tools sk_test_capri_1234", "http POST /v1/convai/tools sk_test_capri_1234 {\"tool_config\":{\"type\":\"client\",\"name\":\"open_website\"", "\"name\":\"media\",\"description\":\"Control the music", "\"expects_response\":true,\"response_timeout_secs\":20}}", "You have tools for their PC", "tool {\"type\":\"client_tool_result\",\"tool_call_id\":\"call_1\",\"result\":\"Opened Spotify.\",\"is_error\":false}", "ran SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "check in once", "Don\u0027t beg them to talk", "\"prompt\":{\"prompt\":\"You are Capri", "\"llm\":\"gemini-2.5-flash\"", "\"tts\":{\"voice_id\":\"abc\"}", "\"overrides\":{\"conversation_config_override\":{\"agent\":{\"prompt\":{\"prompt\":true}},\"tts\":{\"voice_id\":true}}}", "http GET /v1/convai/conversation/get-signed-url?agent_id=agent_old sk_test_capri_1234", "init {\"type\":\"conversation_initiation_client_data\",\"conversation_config_override\":{\"agent\":{\"prompt\":{\"prompt\":\"You are Capri", "pong {\"type\":\"pong\",\"event_id\":7}", "chunk silent | chunk loud | chunk loud" };
                            foreach (string piece in wanted) if (log.IndexOf(piece, StringComparison.Ordinal) < 0) throw new Exception("Agent traffic was missing " + piece + ": " + log);
                            if (log.IndexOf("tool_stale", StringComparison.Ordinal) >= 0 || System.Text.RegularExpressions.Regex.Matches(log, "http POST /v1/convai/tools").Count != 10) throw new Exception("Voice chat reused a stale tool or remade a kept one: " + log);
                            Hands.Run = handsRun;
                            Hands.Catalog = handsCatalog;
                            Hands.Forget();
                            short[] loud = new short[Ears.Chunk];
                            for (int i = 0; i < loud.Length; i++) loud[i] = (short)(Math.Sin(i * 0.3) * 9000);
                            ears.Feed(loud);
                        }
                        else if (step == 7)
                        {
                            string log;
                            lock (asked) log = string.Join(" | ", asked.ToArray());
                            if (log.IndexOf("late silent", StringComparison.Ordinal) < 0 || log.IndexOf("late loud", StringComparison.Ordinal) >= 0) throw new Exception("Mic was not silenced while she talked: " + log);
                            ChatHeard("send nudes");
                            if (!dropping || reply == null || reply == "Loud and clear, choom.") throw new Exception("Voice chat let a blocked line through.");
                            ChatReplied("Sure thing.");
                            if (reply == "Sure thing.") throw new Exception("The agent's reply to a blocked line got through.");
                            ChatHeard("hi again");
                            ChatReplied("well shit, choom");
                            if (!dropping || reply == "well shit, choom") throw new Exception("A rude agent reply got through.");
                            ChatReplied("Still there, choom?");
                            if (!checkedIn) throw new Exception("A line she said unprompted was not taken as a check-in.");
                            speaker.Clear();
                            double quiet = clock.Elapsed.TotalSeconds;
                            CheckChat(quiet);
                            CheckChat(quiet + 0.6);
                            if (agent == null) throw new Exception("She hung up before her check-in had time to land.");
                            CheckChat(quiet + 1.3);
                            if (agent != null || !chatting || !resting || !pill.Chatting || mainframe.VoiceStatus.IndexOf("Voice chat resting", StringComparison.Ordinal) != 0) throw new Exception("She did not hang up and rest after one check-in: " + mainframe.VoiceStatus);
                            short[] speech = new short[Ears.Chunk];
                            for (int i = 0; i < speech.Length; i++) speech[i] = (short)(Math.Sin(i * 0.3) * 9000);
                            ears.Feed(new short[Ears.Chunk]);
                            ears.Feed(speech);
                            if (!resting) throw new Exception("One loud blip woke her.");
                            ears.Feed(speech);
                            if (resting) throw new Exception("Talking did not wake her.");
                            ears.Feed(speech);
                        }
                        else if (step == 8)
                        {
                        }
                        else if (step == 9)
                        {
                            string log;
                            lock (asked) log = string.Join(" | ", asked.ToArray());
                            if (System.Text.RegularExpressions.Regex.Matches(log, "get-signed-url").Count < 2 || System.Text.RegularExpressions.Regex.Matches(log, "init \\{").Count < 2 || log.IndexOf("woke silent | woke loud | woke loud | woke loud", StringComparison.Ordinal) < 0) throw new Exception("Talking did not reopen the link and replay what she missed: " + log);
                            if (agent != null || !chatting || !resting || mainframe.VoiceStatus.IndexOf("Voice chat resting", StringComparison.Ordinal) != 0) throw new Exception("A clean hang-up from ElevenLabs did not leave her resting: " + mainframe.VoiceStatus + " / " + log);
                            pill.Press(2);
                            if (pill.Chatting || chatting || agent != null || ears.Listening || speaker.Busy || resting) throw new Exception("Wave did not end voice chat.");
                            Voice.Host = agentHost;
                            fakeAgent.Stop();
                            voice.Key = null;
                            ShowVoice();
                            checks.Add("PASS voice chat: makes an ElevenLabs agent with her persona, voice and app/web/music tools (reusing ones already saved) that checks in once after 15 s of quiet, patches an older agent once, opens a signed live link, answers pings, runs the agent's tool calls and sends back the result, streams the mic, mic button sits apart on the right only while it's on and mutes the mic, her mouth follows the agent's audio, the mic goes quiet while she talks, blocked lines and rude replies are caught, one check-in then she hangs up and rests, talking wakes her and replays the first words, a clean hang-up from ElevenLabs rests too, wave ends it");
                            Speak = null;
                            realWhisper = Whisper.Run;
                            realThink = Think;
                            Whisper.Run = delegate(string exe, string model, string wav) { lock (whispered) whispered.Add(exe == whisper.Exe && model == whisper.Model ? "wav " + new FileInfo(wav).Length : "wrong files"); return " [BLANK_AUDIO] What's up, choom? (wind blowing)"; };
                            Think = delegate(string words) { lock (whispered) whispered.Add("think " + words); return "Just vibing, choom."; };
                            pill.Press(2);
                            if (!pill.Chatting || !chatting || !local || mainframe.VoiceStatus.IndexOf("Free voice chat on.", StringComparison.Ordinal) != 0) throw new Exception("Wave without a key did not start free voice chat: " + mainframe.VoiceStatus);
                            short[] hush = new short[Ears.Chunk];
                            short[] talk = new short[Ears.Chunk];
                            for (int i = 0; i < talk.Length; i++) talk[i] = (short)(Math.Sin(i * 0.3) * 9000);
                            ears.Feed(hush);
                            ears.Feed(talk);
                            for (int i = 0; i < 12; i++) ears.Feed(hush);
                            if (transcribing) throw new Exception("One loud blip was sent to her ears.");
                            for (int i = 0; i < 8; i++) ears.Feed(talk);
                            for (int i = 0; i < 9; i++) ears.Feed(hush);
                            if (!transcribing) throw new Exception("A finished line was not sent to her ears.");
                            for (int i = 0; i < 3; i++) ears.Feed(talk);
                            lock (waking) if (utterance != null) throw new Exception("She kept listening while still hearing the last line.");
                        }
                        else if (step == 10)
                        {
                            string[] whisperLog;
                            lock (whispered) whisperLog = whispered.ToArray();
                            if (string.Join(" | ", whisperLog) != "wav 60844 | think What's up, choom?") throw new Exception("Free voice chat heard wrong: " + string.Join(" | ", whisperLog));
                            if (heard != "What's up, choom?" || reply != "Just vibing, choom." || mainframe.VoiceStatus.IndexOf("Free voice chat on.", StringComparison.Ordinal) != 0) throw new Exception("Free voice chat did not answer in text: " + heard + " / " + reply + " / " + mainframe.VoiceStatus);
                            if (Whisper.Clean("[BLANK_AUDIO]") != "" || Whisper.Clean(" (music) \u266a ") != "" || Whisper.Clean("*laughs* okay then") != "okay then") throw new Exception("Whisper's noise tags were not dropped.");
                            if (Whisper.Clean(" Thank you. ") != "" || Whisper.Clean("Thanks for watching!") != "" || Whisper.Clean(" you") != "" || Whisper.Clean("Merci d'avoir regard\u00e9.") != "" || Whisper.Clean("Sous-titrage ST' 501") != "" || Whisper.Clean("Thank you, Capri, that was nova.") != "Thank you, Capri, that was nova.") throw new Exception("Whisper's made-up lines from background noise were not dropped.");
                            if (Whisper.Clean(" Salut Capri, \u00e7a va ? ") != "Salut Capri, \u00e7a va ?" || Whisper.Clean("\u3053\u3093\u306b\u3061\u306f") != "\u3053\u3093\u306b\u3061\u306f") throw new Exception("Whisper dropped words that aren't English.");
                            checks.Add("PASS free voice chat languages: " + (Whisper.English ? "English Windows uses the English ears model with -l en" : "this Windows isn't English, so the multilingual ears model with -l auto") + "; French and Japanese lines are kept; Whisper's made-up 'Thank you.' / 'Merci d'avoir regard\u00e9.' noise lines are dropped");
                            pill.Press(2);
                            if (pill.Chatting || chatting || local || transcribing) throw new Exception("Wave did not end free voice chat.");
                            ToggleFreeVoice();
                            bool freeOn = preferences.FreeVoice && mainframe.FreeVoiceShown;
                            ToggleFreeVoice();
                            if (!freeOn || preferences.FreeVoice || mainframe.FreeVoiceShown) throw new Exception("The Free voice chat box did not toggle.");
                            Whisper.Run = realWhisper;
                            Think = realThink;
                            checks.Add("PASS free voice chat: with no ElevenLabs key (or Free voice chat ticked) the wave and Ctrl + Alt + V listen on the PC, a blip is ignored, a finished line goes to whisper as a 16 kHz WAV, noise tags are dropped, the words go to her brain and she types back, the mic is ignored while she's still hearing, wave ends it");
                            checks.Add("LIMIT free voice chat used a stand-in for whisper-cli; the real download and transcription need a manual check");
                            heard = "Hello Capri.";
                            Answer(++asking, "Hey choom! Net's been quiet, too quiet. Time to stir up some trouble.", TalkState.Talking);
                            replyStart -= 10;
                            speechStart = replyStart;
                            RenderFrame();
                        }
                        else if (step == 11)
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

    internal sealed class Shelf : Form
    {
        private readonly int extra;

        internal Shelf(int extended = 0)
        {
            extra = extended;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 44, 52);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x8 | extra;
                return value;
            }
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
        [StructLayout(LayoutKind.Sequential)] private struct InputInfo { internal int Size; internal uint Time; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref InputInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, int flags);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, int flags);
        [DllImport("user32.dll")] internal static extern void keybd_event(byte key, byte scan, int flags, UIntPtr extra);
        [DllImport("user32.dll")] internal static extern int GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point origin, int colorKey, ref Blend blend, int flags);

        internal static long IdleMilliseconds()
        {
            InputInfo info = new InputInfo { Size = Marshal.SizeOf(typeof(InputInfo)) };
            return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : 0;
        }

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
