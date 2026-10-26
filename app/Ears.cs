using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CapriDesktop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormat
    {
        internal short Tag, Channels;
        internal int SampleRate, BytesPerSecond;
        internal short Align, Bits, Extra;

        internal static WaveFormat Mono(int rate) { return new WaveFormat { Tag = 1, Channels = 1, SampleRate = rate, BytesPerSecond = rate * 2, Align = 2, Bits = 16 }; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        internal IntPtr Data;
        internal int Length, Recorded;
        internal IntPtr User;
        internal int Flags, Loops;
        internal IntPtr Next, Reserved;
    }

    internal sealed class Mic
    {
        internal readonly string Id;
        internal readonly string Name;

        internal Mic(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    internal sealed class Ears : IDisposable
    {
        internal const int Rate = 16000;
        internal const int Chunk = Rate / 10;
        private const int Buffers = 4;
        internal event Action<short[]> Heard;
        internal event Action<double> Level;
        internal volatile bool Muted;
        internal volatile string Chosen;
        private IntPtr device;
        private IntPtr[] headers;
        private Thread worker;
        private AutoResetEvent signal;
        private volatile bool running;

        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr device);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr device);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr device);
        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCaps(IntPtr id, ref MicCaps caps, int size);
        [DllImport("winmm.dll")] private static extern int waveInMessage(IntPtr device, int message, out int size, IntPtr unused);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInMessage(IntPtr device, int message, StringBuilder text, IntPtr size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MicCaps
        {
            internal short Maker, Product;
            internal int Driver;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal int Formats;
            internal short Channels, Reserved;
        }

        internal bool Listening { get { return running; } }

        internal static List<Mic> List()
        {
            List<Mic> found = new List<Mic>();
            int count = waveInGetNumDevs();
            for (int i = 0; i < count; i++)
            {
                MicCaps caps = new MicCaps();
                if (waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(MicCaps))) != 0) continue;
                string id = Endpoint(i);
                found.Add(new Mic(id ?? caps.Name, FullName(id) ?? caps.Name));
            }
            return found;
        }

        private static string Endpoint(int index)
        {
            int size;
            if (waveInMessage(new IntPtr(index), 0x0812, out size, IntPtr.Zero) != 0 || size <= 0) return null;
            StringBuilder text = new StringBuilder(size / 2 + 1);
            if (waveInMessage(new IntPtr(index), 0x0811, text, new IntPtr(size)) != 0) return null;
            return text.Length == 0 ? null : text.ToString();
        }

        private static string FullName(string id)
        {
            if (id == null) return null;
            int cut = id.LastIndexOf("}.{", StringComparison.Ordinal);
            if (cut < 0) return null;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + id.Substring(cut + 2) + @"\Properties"))
                {
                    if (key == null) return null;
                    string kind = key.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                    string name = key.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                    if (string.IsNullOrEmpty(kind)) return string.IsNullOrEmpty(name) ? null : name;
                    return string.IsNullOrEmpty(name) ? kind : kind == "Microphone" ? name : kind + " (" + name + ")";
                }
            }
            catch (Exception) { return null; }
        }

        private static int Pick(string chosen)
        {
            if (string.IsNullOrEmpty(chosen)) return -1;
            List<Mic> mics = List();
            for (int i = 0; i < mics.Count; i++) if (mics[i].Id == chosen) return i;
            return -1;
        }

        internal void Start()
        {
            if (running) return;
            WaveFormat format = WaveFormat.Mono(Rate);
            signal = new AutoResetEvent(false);
            int size = Marshal.SizeOf(typeof(WaveHeader));
            int id = Pick(Chosen);
            IntPtr wake = signal.SafeWaitHandle.DangerousGetHandle();
            if (waveInOpen(out device, id, ref format, wake, IntPtr.Zero, 0x00050000) != 0 && (id == -1 || waveInOpen(out device, -1, ref format, wake, IntPtr.Zero, 0x00050000) != 0))
            {
                signal.Dispose();
                signal = null;
                throw new VoiceException("Can't open your mic. Pick another under VOICE.");
            }
            headers = new IntPtr[Buffers];
            for (int i = 0; i < Buffers; i++)
            {
                WaveHeader header = new WaveHeader { Data = Marshal.AllocHGlobal(Chunk * 2), Length = Chunk * 2 };
                headers[i] = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(header, headers[i], false);
                waveInPrepareHeader(device, headers[i], size);
                waveInAddBuffer(device, headers[i], size);
            }
            running = true;
            waveInStart(device);
            worker = new Thread(Pump);
            worker.IsBackground = true;
            worker.Name = "Capri ears";
            worker.Start();
        }

        internal void Stop()
        {
            if (!running) return;
            running = false;
            signal.Set();
            worker.Join(1000);
            waveInReset(device);
            int size = Marshal.SizeOf(typeof(WaveHeader));
            foreach (IntPtr pointer in headers)
            {
                waveInUnprepareHeader(device, pointer, size);
                WaveHeader header = (WaveHeader)Marshal.PtrToStructure(pointer, typeof(WaveHeader));
                Marshal.FreeHGlobal(header.Data);
                Marshal.FreeHGlobal(pointer);
            }
            waveInClose(device);
            device = IntPtr.Zero;
            headers = null;
            signal.Dispose();
            signal = null;
        }

        public void Dispose() { Stop(); }

        private void Pump()
        {
            int size = Marshal.SizeOf(typeof(WaveHeader));
            int flags = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");
            int next = 0;
            try
            {
                while (running)
                {
                    signal.WaitOne(250);
                    while (running)
                    {
                        IntPtr pointer = headers[next];
                        WaveHeader header = (WaveHeader)Marshal.PtrToStructure(pointer, typeof(WaveHeader));
                        if ((header.Flags & 1) == 0) break;
                        short[] chunk = new short[header.Recorded / 2];
                        Marshal.Copy(header.Data, chunk, 0, chunk.Length);
                        Marshal.WriteInt32(pointer, flags, header.Flags & ~1);
                        waveInAddBuffer(device, pointer, size);
                        next = (next + 1) % Buffers;
                        try { Feed(chunk); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        internal void Feed(short[] chunk)
        {
            Action<double> level = Level;
            if (level != null) level(Muted ? 0 : Speaker.Loudness(chunk, 0, chunk.Length));
            Action<short[]> heard = Heard;
            if (heard != null) heard(Muted ? new short[chunk.Length] : chunk);
        }
    }

    internal sealed class Speaker : IDisposable
    {
        private readonly bool silent;
        internal bool Plain;
        private readonly List<IntPtr> pending = new List<IntPtr>();
        private readonly List<int> shapes = new List<int>();
        private readonly List<short> leftover = new List<short>();
        private IntPtr device;
        private int rate;
        private double loudest = 0.05;
        private long began;
        private long until;

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);

        internal Speaker(bool quiet) { silent = quiet; }

        internal static double Loudness(short[] samples, int start, int count)
        {
            if (count <= 0) return 0;
            double sum = 0;
            for (int i = start; i < start + count; i++) sum += (double)samples[i] * samples[i];
            return Math.Sqrt(sum / count) / 32768.0;
        }

        internal void Open(int sampleRate)
        {
            if (sampleRate == rate && (silent || device != IntPtr.Zero)) return;
            Close();
            rate = sampleRate;
            if (silent) return;
            WaveFormat format = WaveFormat.Mono(rate);
            int failed = waveOutOpen(out device, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (failed != 0)
            {
                device = IntPtr.Zero;
                throw new VoiceException(Speakers(failed));
            }
        }

        internal static string Speakers(int failed)
        {
            if (failed == 2 || failed == 6) return "Can't open your speakers: Windows has no speakers or headphones on. Pick one in Settings > System > Sound.";
            if (failed == 4) return "Can't open your speakers: another app has them to itself. Close it, or untick exclusive mode in your speaker's Sound settings.";
            return "Can't open your speakers (Windows error " + failed + "). Check Settings > System > Sound, then try again.";
        }

        internal bool Busy { get { return Stopwatch.GetTimestamp() < Interlocked.Read(ref until) + Stopwatch.Frequency * 35 / 100; } }

        internal void Play(short[] samples)
        {
            if (rate == 0 || samples.Length == 0) return;
            Sweep();
            long now = Stopwatch.GetTimestamp();
            if (now >= Interlocked.Read(ref until)) { began = now; shapes.Clear(); leftover.Clear(); Interlocked.Exchange(ref until, now); }
            Interlocked.Add(ref until, samples.Length * Stopwatch.Frequency / rate);
            leftover.AddRange(samples);
            int step = Math.Max(1, rate / 12);
            short[] all = leftover.ToArray();
            int at = 0;
            for (; at + step <= all.Length; at += step)
            {
                double level = Loudness(all, at, step);
                loudest = Math.Max(loudest * 0.995, level);
                int index = shapes.Count;
                shapes.Add(level <= Math.Max(0.004, loudest * 0.08) ? -1 : level < loudest * 0.3 ? (index % 3 == 0 ? 0 : 1) : level < loudest * 0.55 ? (index % 2 == 1 ? 2 : 1) : level < loudest * 0.85 ? 3 : 4);
            }
            leftover.RemoveRange(0, at);
            if (silent || device == IntPtr.Zero) return;
            if (!Plain) samples = Voice.Louder(samples);
            int size = Marshal.SizeOf(typeof(WaveHeader));
            WaveHeader header = new WaveHeader { Data = Marshal.AllocHGlobal(samples.Length * 2), Length = samples.Length * 2 };
            Marshal.Copy(samples, 0, header.Data, samples.Length);
            IntPtr pointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(header, pointer, false);
            waveOutPrepareHeader(device, pointer, size);
            waveOutWrite(device, pointer, size);
            pending.Add(pointer);
        }

        internal int Shape()
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= Interlocked.Read(ref until) || shapes.Count == 0) return -1;
            int index = (int)((now - began) * 12 / Stopwatch.Frequency);
            return index >= 0 && index < shapes.Count ? shapes[index] : -1;
        }

        internal void Clear()
        {
            if (device != IntPtr.Zero) waveOutReset(device);
            Sweep();
            shapes.Clear();
            leftover.Clear();
            Interlocked.Exchange(ref until, 0);
        }

        private void Sweep()
        {
            int size = Marshal.SizeOf(typeof(WaveHeader));
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                WaveHeader header = (WaveHeader)Marshal.PtrToStructure(pending[i], typeof(WaveHeader));
                if ((header.Flags & 1) == 0) continue;
                waveOutUnprepareHeader(device, pending[i], size);
                Marshal.FreeHGlobal(header.Data);
                Marshal.FreeHGlobal(pending[i]);
                pending.RemoveAt(i);
            }
        }

        internal void Close()
        {
            Clear();
            if (device != IntPtr.Zero) waveOutClose(device);
            device = IntPtr.Zero;
            rate = 0;
        }

        public void Dispose() { Close(); }
    }

    internal static class Hearing
    {
        internal const string NoRecognizer = "Windows has no offline speech recognizer for your language. Turn on Online speech recognition in Settings > Privacy & security > Speech.";
        internal static Func<short[], string> Run = Recognize;
        private static readonly object gate = new object();
        private static SpeechRecognitionEngine engine;

        internal static string Text(short[] samples) { return Clean(Run(samples)); }

        internal static string Clean(string text)
        {
            string plain = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return Regex.IsMatch(plain, @"[\p{L}\p{N}]") ? plain : "";
        }

        internal static string Pick(IList<string> cultures, string wanted)
        {
            string language = wanted.Split('-')[0];
            foreach (string culture in cultures) if (string.Equals(culture, wanted, StringComparison.OrdinalIgnoreCase)) return culture;
            foreach (string culture in cultures) if (string.Equals(culture.Split('-')[0], language, StringComparison.OrdinalIgnoreCase)) return culture;
            foreach (string culture in cultures) if (string.Equals(culture, "en-US", StringComparison.OrdinalIgnoreCase)) return culture;
            return cultures.Count > 0 ? cultures[0] : null;
        }

        private static SpeechRecognitionEngine Open()
        {
            List<RecognizerInfo> installed = new List<RecognizerInfo>(SpeechRecognitionEngine.InstalledRecognizers());
            List<string> cultures = installed.ConvertAll(delegate(RecognizerInfo info) { return info.Culture.Name; });
            string chosen = Pick(cultures, CultureInfo.CurrentUICulture.Name);
            if (chosen == null) throw new IOException(NoRecognizer);
            SpeechRecognitionEngine made = new SpeechRecognitionEngine(installed[cultures.IndexOf(chosen)]);
            made.LoadGrammar(new DictationGrammar());
            return made;
        }

        private static string Recognize(short[] samples)
        {
            lock (gate)
            {
                if (engine == null) engine = Open();
                byte[] bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                StringBuilder words = new StringBuilder();
                ManualResetEvent done = new ManualResetEvent(false);
                EventHandler<SpeechRecognizedEventArgs> recognized = delegate(object sender, SpeechRecognizedEventArgs e) { lock (words) words.Append(e.Result.Text).Append(' '); };
                EventHandler<RecognizeCompletedEventArgs> completed = delegate { done.Set(); };
                engine.SpeechRecognized += recognized;
                engine.RecognizeCompleted += completed;
                try
                {
                    using (MemoryStream stream = new MemoryStream(bytes))
                    {
                        engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(Ears.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                        engine.RecognizeAsync(RecognizeMode.Multiple);
                        if (!done.WaitOne(20000))
                        {
                            engine.RecognizeAsyncCancel();
                            done.WaitOne(3000);
                            throw new IOException("Hearing that took too long. Try a shorter line.");
                        }
                    }
                }
                finally
                {
                    engine.SpeechRecognized -= recognized;
                    engine.RecognizeCompleted -= completed;
                    engine.SetInputToNull();
                }
                lock (words) return words.ToString();
            }
        }
    }
}
