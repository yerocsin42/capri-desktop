using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace CapriDesktop
{
    [DataContract]
    internal sealed class ToolArguments
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "site")] public string Site;
        [DataMember(Name = "search")] public string Search;
        [DataMember(Name = "action")] public string Action;
        [DataMember(Name = "focus")] public string Focus;
        [DataMember(Name = "song")] public string Song;
        [DataMember(Name = "app")] public string App;
        [DataMember(Name = "kind")] public string Kind;
    }

    internal sealed class InstalledApp
    {
        internal readonly string Name;
        internal readonly string Id;
        internal InstalledApp(string name, string id) { Name = name; Id = id; }
    }

    internal sealed class OpenWindow
    {
        internal readonly IntPtr Handle;
        internal readonly string Title;
        internal readonly string Program;
        internal OpenWindow(IntPtr handle, string title, string program) { Handle = handle; Title = title; Program = program; }
    }

    internal static class Hands
    {
        internal const string Note =
            "\nYou have tools for their PC: open (launch, play) their apps and games, switch to, minimize or close apps that are open, play songs or videos they name, open websites or search them, control their music and volume, and look at their screen. " +
            "Use a tool whenever they ask you to do something like that, and never pretend you did it without one. Look at their screen when they ask what you see or what you think of something on it. Only close an app when they ask you to. You can't touch their files or type for them. " +
            "Most messages are just chat, not jobs: answer those as yourself, with your own opinions and tastes, and don't offer to open, play or look at anything unless they ask.";

        internal const string Prompt =
            "You are the hands of Capri, a desktop companion on the user's Windows PC. You don't chat.\n" +
            "Read their latest message. If it asks for something on their PC, call the one tool that does it, using the names they said: open an app, game or website, search a site, play music, control the music or volume, say what song is playing, switch to, minimize or close an open app, or look at their screen when they ask what you see, what they're doing or what you think of something on it.\n" +
            "If they want music but name none, pick a popular song that fits.\n" +
            "If it's just chat or a question, or you're not sure, call no tool and reply with only the word: chat";

        internal const string Chat =
            "\nMost messages are just chat, not jobs: answer as yourself, with your own opinions and tastes. Only say you opened, played or looked at something when your tools just did it.";

        private static readonly string[,] Specs =
        {
            { "open_app", "Open, launch or play an app or game installed on their PC, like Spotify, Discord, Steam or Roblox.",
              "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The app's name\"}},\"required\":[\"name\"]}" },
            { "open_website", "Open a website in their browser, or search a site like YouTube or Google.",
              "{\"type\":\"object\",\"properties\":{\"site\":{\"type\":\"string\",\"description\":\"The site's name or address, like youtube.com\"},\"search\":{\"type\":\"string\",\"description\":\"Words to search for on that site, if any\"}},\"required\":[\"site\"]}" },
            { "media", "Control the music or video playing on their PC, or the volume, or check what song is playing.",
              "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"What to do\",\"enum\":[\"play_pause\",\"next\",\"previous\",\"volume_up\",\"volume_down\",\"mute\",\"now_playing\"]}},\"required\":[\"action\"]}" },
            { "look_at_screen", "Look at their screen right now to see what they're doing, playing or showing you.",
              "{\"type\":\"object\",\"properties\":{\"focus\":{\"type\":\"string\",\"description\":\"What to look at, if they said\"}}}" },
            { "play_music", "Play a song, playlist, album or artist they name, on Spotify or YouTube. For their liked songs, use kind playlist and song Liked Songs.",
              "{\"type\":\"object\",\"properties\":{\"song\":{\"type\":\"string\",\"description\":\"What to play, like a song and its artist or a playlist's name\"},\"kind\":{\"type\":\"string\",\"description\":\"What it is\",\"enum\":[\"song\",\"playlist\",\"album\",\"artist\"]},\"app\":{\"type\":\"string\",\"description\":\"Where to play it, if they said\",\"enum\":[\"spotify\",\"youtube\"]}},\"required\":[\"song\"]}" },
            { "app_window", "Switch to, minimize or close an app or game that's open on their PC. Naming a site closes that browser tab.",
              "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The app's name, or the site in a browser tab\"},\"action\":{\"type\":\"string\",\"description\":\"What to do\",\"enum\":[\"switch_to\",\"minimize\",\"close\"]}},\"required\":[\"name\",\"action\"]}" }
        };

        internal static readonly string Tools = Render(false);
        internal static readonly string ClaudeTools = Render(true);
        internal static readonly string Listing = Written();
        private static readonly Regex Asks = new Regex(@"\b(open|launch|start|run|load up|boot up|fire up|play|playing|shuffle|put on|blast|queue|close|quit|exit|kill|shut|switch|go to|bring up|show me|minimi[sz]e|pause|unpause|resume|skip|volume|louder|quieter|turn (it|the volume|the music) (up|down)|mute|unmute|search|google|look|screen|see|watching|music|songs?|track|playlist|album|spotify|youtube|listening|website|tab|what am i doing|what do you think (of|about) (this|that|it))\b|\.(com|net|org|gg|io|tv)\b", RegexOptions.IgnoreCase);

        internal static Func<List<InstalledApp>> Catalog = ReadCatalog;
        internal static Action<string> Run = Start;
        internal static Action<byte> Press = Tap;
        internal static Func<string, string> Look = delegate { return "You can't see their screen right now."; };
        internal static Func<string, string> Fetch = Download;
        internal static Func<List<OpenWindow>> Windows = ReadWindows;
        internal static Func<OpenWindow, string, bool> Steer = Apply;
        internal static Func<string, bool> PressPlay = ClickPlay;
        internal static Func<string> NowPlaying = SpotifyTitle;
        private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
        private static readonly string[] Shell = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        private delegate bool Visit(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int process);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(int from, int to, bool attach);
        [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
        private static readonly Regex Junk = new Regex(@"\b(uninstall\w*|readme|read me|help|manual|documentation|release notes|licen[cs]e|website|support|changelog)\b", RegexOptions.IgnoreCase);
        private static readonly string[,] Searches =
        {
            { "youtube", "https://www.youtube.com/results?search_query=" },
            { "google", "https://www.google.com/search?q=" },
            { "twitch", "https://www.twitch.tv/search?term=" },
            { "reddit", "https://www.reddit.com/search/?q=" },
            { "wikipedia", "https://en.wikipedia.org/w/index.php?search=" },
            { "spotify", "https://open.spotify.com/search/" },
            { "roblox", "https://www.roblox.com/discover/?Keyword=" },
            { "github", "https://github.com/search?q=" }
        };
        private static readonly object gate = new object();
        private static List<InstalledApp> cached;
        private static DateTime cachedAt;

        internal static string Do(string tool, ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            try
            {
                if (tool == "open_app") return OpenApp(args.Name);
                if (tool == "open_website") return OpenSite(args.Site, args.Search);
                if (tool == "media") return Media(args.Action);
                if (tool == "look_at_screen") return Look(args.Focus);
                if (tool == "play_music") return PlayMusic(args.Song, args.App, args.Kind);
                if (tool == "app_window") return Window(args.Name, args.Action);
                return "There's no tool called " + tool + ".";
            }
            catch (Exception error) { return "That didn't work: " + error.Message; }
        }

        internal static void Forget() { lock (gate) cached = null; }

        internal static int Count { get { return Specs.GetLength(0); } }

        internal static string Name(int i) { return Specs[i, 0]; }

        internal static string Description(int i) { return Specs[i, 1]; }

        internal static string ElevenLabsTool(int i) { return "{\"tool_config\":{\"type\":\"client\",\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"parameters\":" + Specs[i, 2] + ",\"expects_response\":true,\"response_timeout_secs\":20}}"; }

        private static string Render(bool claude)
        {
            List<string> tools = new List<string>();
            for (int i = 0; i < Specs.GetLength(0); i++)
                tools.Add(claude
                    ? "{\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"input_schema\":" + Specs[i, 2] + "}"
                    : "{\"type\":\"function\",\"function\":{\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"parameters\":" + Specs[i, 2] + "}}");
            return "[" + string.Join(",", tools.ToArray()) + "]";
        }

        private static string Written()
        {
            StringBuilder text = new StringBuilder("\nYour tools:");
            for (int i = 0; i < Specs.GetLength(0); i++)
            {
                List<string> keys = new List<string>();
                foreach (Match key in Regex.Matches(Specs[i, 2], @"""(\w+)"":\{""type"":""string"",""description"":""[^""]*""(?:,""enum"":\[([^\]]*)\])?"))
                    keys.Add("\"" + key.Groups[1].Value + "\": \"" + (key.Groups[2].Success ? key.Groups[2].Value.Replace("\"", "").Replace(",", "|") : "...") + "\"");
                text.Append("\n- " + Specs[i, 0] + "{" + string.Join(", ", keys.ToArray()) + "}: " + Specs[i, 1]);
            }
            return text.Append("\nTo use one, reply with only the call, like: open_app{\"name\": \"Spotify\"}").ToString();
        }

        internal static bool Wants(string said) { return Asks.IsMatch(said ?? ""); }

        private static readonly Regex Lead = new Regex(@"^((hey|yo|ok|okay)[\s,]+)?(capri\b[\s,]*)?(((can|could|would|will) you|please|pls)\s+)*", RegexOptions.IgnoreCase);
        private static readonly string[] MediaWords =
        {
            @"^(pause|unpause|resume|stop the music|pause (the )?(music|song|video))$", "play_pause",
            @"^(skip|next|skip (this|the) (song|track)|next (song|track))$", "next",
            @"^(previous|go back|(last|previous) (song|track))$", "previous",
            @"^(turn (it|the volume|the music) up|volume up|louder)$", "volume_up",
            @"^(turn (it|the volume|the music) down|volume down|quieter)$", "volume_down",
            @"^(un)?mute( (it|the volume|the sound))?$", "mute",
            @"^((what('|\u2019)?s|what is) (playing|this song|this track|the song|on spotify)|what (song|track)( is|('|\u2019)s)? (this|playing|on)|what am i listening to|what are we listening to)$", "now_playing"
        };

        internal static bool Direct(string said, out string tool, out ToolArguments args)
        {
            tool = null;
            args = new ToolArguments();
            string text = Lead.Replace((said ?? "").Trim(), "");
            for (int i = 0; i < 2; i++) text = Regex.Replace(Regex.Replace(text, @"[\s.!?]+$", ""), @"\s+(for me|please|pls|rn|right now|now)$", "", RegexOptions.IgnoreCase);
            string lower = text.ToLowerInvariant();
            for (int i = 0; i < MediaWords.Length; i += 2)
                if (Regex.IsMatch(lower, MediaWords[i])) { tool = "media"; args.Action = MediaWords[i + 1]; return true; }
            Match found = Regex.Match(text, @"^(?:open|launch|start|go to|fire up|load up)\s+(spotify|youtube)\s*(?:,|and|&)?\s*(?:then\s+)?(?:play|shuffle|put on|blast)\s+(.+)$", RegexOptions.IgnoreCase);
            if (found.Success) text = "play " + found.Groups[2].Value + " on " + found.Groups[1].Value;
            found = Regex.Match(text, @"^(open|launch|start|run|load up|boot up|fire up)\s+(up\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (found.Success)
            {
                string target = found.Groups[3].Value.Trim();
                if (Installed(target)) { tool = "open_app"; args.Name = target; return true; }
                if (Site(target)) { tool = "open_website"; args.Site = target; return true; }
                return false;
            }
            found = Regex.Match(text, @"^(?:play|shuffle|put on|blast)\s+(.+?)(\s+on\s+(spotify|youtube))?$", RegexOptions.IgnoreCase);
            if (found.Success)
            {
                string target = found.Groups[1].Value.Trim();
                if (!found.Groups[3].Success && Installed(target)) { tool = "open_app"; args.Name = target; return true; }
                if (Regex.IsMatch(target, @"^(with|along|around|a game|games?\b|dead|nice|fair|it\b|me\b|us\b|together|music$|some music$|((a|any|some|random) )?(song|track|music|playlist)s?$|(something|anything)\b(?!\s+by\s))", RegexOptions.IgnoreCase)) return false;
                if (found.Groups[3].Success) args.App = found.Groups[3].Value.ToLowerInvariant();
                Match kind = Regex.Match(target, @"^(?:(?:the|my|your)\s+)?(.+?)\s+(playlist|album)$", RegexOptions.IgnoreCase);
                Match first = Regex.Match(target, @"^(?:the\s+)?(playlist|album)\s+(.+)$", RegexOptions.IgnoreCase);
                Match by = Regex.Match(target, @"^(?:some(?:thing)?|anything|(?:(?:some|a|any)\s+)?(?:songs?|music|tracks?))\s+by\s+(.+)$", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(target, @"^((my|your|the)\s+)?((liked|saved)\s+songs|likes)(\s+playlist)?$", RegexOptions.IgnoreCase)) { args.Kind = "playlist"; args.Song = "Liked Songs"; }
                else if (kind.Success) { args.Kind = kind.Groups[2].Value.ToLowerInvariant(); args.Song = kind.Groups[1].Value; }
                else if (first.Success) { args.Kind = first.Groups[1].Value.ToLowerInvariant(); args.Song = first.Groups[2].Value; }
                else if (by.Success) { args.Kind = "artist"; args.Song = by.Groups[1].Value; }
                else { args.Kind = "song"; args.Song = target; }
                tool = "play_music";
                return true;
            }
            found = Regex.Match(text, @"^(close|quit|exit|kill|shut down|switch to|go to|bring up|show me|minimi[sz]e)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!found.Success || Regex.IsMatch(found.Groups[2].Value, @"^(it|this|that|them|this one|that one|everything|all|all of (it|them)|the window|this window|that window)$", RegexOptions.IgnoreCase) || !Showing(found.Groups[2].Value)) return false;
            string verb = found.Groups[1].Value.ToLowerInvariant();
            tool = "app_window";
            args.Name = found.Groups[2].Value.Trim();
            args.Action = verb.StartsWith("minimi", StringComparison.Ordinal) ? "minimize" : Regex.IsMatch(verb, "^(close|quit|exit|kill|shut down)$") ? "close" : "switch_to";
            return true;
        }

        internal static bool Failed(string result) { return Regex.IsMatch(result ?? "", @"^(Which app|What should I play|Not (opening|playing) that|No app called|More than one|That didn't work|Couldn't|There's no|That isn't a website)|isn't open\.$|but (couldn't|it's still open)"); }

        private static readonly Regex ToolText = new Regex(@"\b(open_app|open_website|media|look_at_screen|play_music|app_window)\s*(\{[^{}]*\}|\([^()]*\))");
        private static readonly Regex ToolPair = new Regex(@"[""']?(\w+)[""']?\s*[:=]\s*(?:""([^""]*)""|'([^']*)'|([^,})\s][^,})]*))");
        private static readonly Regex Command = new Regex(@"^(open|launch|start|run|load up|boot up|fire up|play|shuffle|put on|blast|close|quit|exit|kill|shut down|switch to|go to|bring up|minimi[sz]e|pause|skip|resume|turn)\b", RegexOptions.IgnoreCase);

        internal static bool Typed(string reply, out string tool, out ToolArguments args)
        {
            tool = null;
            args = new ToolArguments();
            Match found = ToolText.Match(Regex.Replace(reply ?? "", @"<\|""\|>|<escape>", "\""));
            if (!found.Success) return false;
            tool = found.Groups[1].Value;
            string inside = found.Groups[2].Value.Substring(1, found.Groups[2].Value.Length - 2);
            bool paired = false;
            foreach (Match pair in ToolPair.Matches(inside))
            {
                paired = true;
                Set(args, pair.Groups[1].Value.ToLowerInvariant(), pair.Groups[2].Success ? pair.Groups[2].Value : pair.Groups[3].Success ? pair.Groups[3].Value : pair.Groups[4].Value.Trim());
            }
            string lone = inside.Trim().Trim('"', '\'');
            if (!paired && lone.Length > 0) Set(args, tool == "open_website" ? "site" : tool == "media" ? "action" : tool == "look_at_screen" ? "focus" : tool == "play_music" ? "song" : "name", lone);
            return true;
        }

        internal static bool Narrated(string asked, string reply, out string tool, out ToolArguments args)
        {
            tool = null;
            args = null;
            string plain = Lead.Replace((asked ?? "").Trim(), "");
            if (!Command.IsMatch(plain) || !Direct(reply, out tool, out args)) return false;
            return tool != "app_window" || args.Action != "close" || Regex.IsMatch(plain, @"\b(close|quit|exit|kill|shut)\b", RegexOptions.IgnoreCase);
        }

        private static void Set(ToolArguments args, string key, string value)
        {
            if (key == "name") args.Name = value;
            else if (key == "site") args.Site = value;
            else if (key == "search") args.Search = value;
            else if (key == "action") args.Action = value;
            else if (key == "focus") args.Focus = value;
            else if (key == "song") args.Song = value;
            else if (key == "app") args.App = value;
            else if (key == "kind") args.Kind = value;
        }

        private static bool Installed(string target)
        {
            string want = Regex.Replace(Plain(target), @"^the |( app| application| game)$", "");
            if (want.Length == 0) return false;
            try
            {
                foreach (InstalledApp app in Apps())
                    if (!Junk.IsMatch(app.Name) && !app.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Score(Plain(app.Name), want) >= 3) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static bool Site(string target)
        {
            string host = target.Trim().ToLowerInvariant();
            if (Regex.IsMatch(host, @"^(https?://)?[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$")) return true;
            for (int i = 0; i < Searches.GetLength(0); i++) if (host == Searches[i, 0]) return true;
            return false;
        }

        private static bool Showing(string target)
        {
            string want = Regex.Replace(Plain(target), @"^the |( app| application| game| tab| window)$", "");
            if (want.Length == 0) return false;
            try
            {
                foreach (OpenWindow window in Windows())
                    if (Math.Max(Score(Plain(window.Program), want), Score(Plain(window.Title), want)) > 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static string OpenApp(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return "Which app, choom?";
            if (Safety.Screen(wanted) != null) return "Not opening that, choom.";
            List<InstalledApp> found = Match(Apps(), wanted);
            if (found.Count == 0) return "No app called " + wanted.Trim() + " is installed on this PC.";
            if (found.Count > 1) return "More than one app matches " + wanted.Trim() + ": " + string.Join(", ", found.ConvertAll(app => app.Name).ToArray()) + ". Which one?";
            Run(found[0].Id);
            return "Opened " + found[0].Name + ".";
        }

        internal static List<InstalledApp> Match(List<InstalledApp> apps, string wanted)
        {
            string want = Regex.Replace(Plain(wanted), @"^the |( app| application| game)$", "");
            List<InstalledApp> top = new List<InstalledApp>();
            if (want.Length == 0) return top;
            int best = 0;
            foreach (InstalledApp app in apps)
            {
                if (Junk.IsMatch(app.Name) || app.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Plain(app.Name);
                int score = Score(name, want);
                if (score == 0 || score < best) continue;
                if (score > best) { best = score; top.Clear(); }
                if (!top.Exists(other => Plain(other.Name) == name)) top.Add(app);
            }
            if (top.Count > 3) top.RemoveRange(3, top.Count - 3);
            return top;
        }

        private static int Score(string name, string want)
        {
            if (name == want || name.Replace(" ", "") == want.Replace(" ", "")) return 4;
            if (name.StartsWith(want + " ", StringComparison.Ordinal)) return 3;
            if ((" " + name + " ").Contains(" " + want + " ")) return 2;
            if (want.Length >= 4 && name.Contains(want)) return 1;
            return 0;
        }

        private static string Plain(string text) { return Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " "), @"\s+", " ").Trim(); }

        private static List<InstalledApp> Apps()
        {
            lock (gate)
            {
                if (cached == null || DateTime.UtcNow - cachedAt > TimeSpan.FromMinutes(2)) { cached = Catalog(); cachedAt = DateTime.UtcNow; }
                return cached;
            }
        }

        private static List<InstalledApp> ReadCatalog()
        {
            List<InstalledApp> apps = new List<InstalledApp>();
            Exception failed = null;
            Thread reader = new Thread(delegate()
            {
                try
                {
                    object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    object items = Call(Call(shell, "NameSpace", "shell:AppsFolder"), "Items");
                    int count = (int)Get(items, "Count");
                    for (int i = 0; i < count; i++)
                    {
                        object item = Call(items, "Item", i);
                        string name = Get(item, "Name") as string;
                        string id = Get(item, "Path") as string;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) apps.Add(new InstalledApp(name, id));
                    }
                }
                catch (Exception error) { failed = error; }
            });
            reader.SetApartmentState(ApartmentState.STA);
            reader.IsBackground = true;
            reader.Start();
            if (!reader.Join(10000)) throw new TimeoutException("The app list took too long.");
            if (failed != null) throw failed;
            return apps;
        }

        private static object Call(object target, string method, params object[] args) { return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args); }

        private static object Get(object target, string property) { return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null); }

        private static void Start(string id)
        {
            ProcessStartInfo info = Regex.IsMatch(id, @"^[A-Za-z]:\\|^[a-z][a-z0-9+.-]*://|^spotify:", RegexOptions.IgnoreCase) ? new ProcessStartInfo(id) : new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id);
            info.UseShellExecute = true;
            using (Process.Start(info)) { }
        }

        private static string OpenSite(string site, string search)
        {
            if (Safety.Screen((site ?? "") + " " + (search ?? "")) != null) return "Not opening that, choom.";
            string address = Address(site, search);
            if (address == null) return "That isn't a website.";
            Run(address);
            string host = new Uri(address).Host;
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return string.IsNullOrWhiteSpace(search) ? "Opened " + host + "." : "Searched " + host + " for " + search.Trim() + ".";
        }

        internal static string Address(string site, string search)
        {
            string host = Regex.Replace((site ?? "").Trim().ToLowerInvariant(), @"^(https?://)?(www\.)?", "");
            bool searching = !string.IsNullOrWhiteSpace(search);
            if (searching)
            {
                for (int i = 0; i < Searches.GetLength(0); i++)
                {
                    string key = Searches[i, 0];
                    if (host == key || host.StartsWith(key + ".", StringComparison.Ordinal) || host.Contains("." + key + ".")) return Searches[i, 1] + Uri.EscapeDataString(search.Trim());
                }
                return Searches[1, 1] + Uri.EscapeDataString((search.Trim() + " " + host).Trim());
            }
            if (host.Length == 0) return null;
            if (host.IndexOf('.') < 0) host += ".com";
            Uri uri;
            if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out uri) || uri.Host.IndexOf('.') < 0 || uri.UserInfo.Length > 0) return null;
            return uri.AbsoluteUri;
        }

        private static string Media(string action)
        {
            if (action == "play_pause") { Press(0xB3); return "Pressed play/pause."; }
            if (action == "next") { Press(0xB0); return "Skipped to the next track."; }
            if (action == "previous") { Press(0xB1); return "Went back a track."; }
            if (action == "volume_up") { for (int i = 0; i < 5; i++) Press(0xAF); return "Turned the volume up."; }
            if (action == "volume_down") { for (int i = 0; i < 5; i++) Press(0xAE); return "Turned the volume down."; }
            if (action == "mute") { Press(0xAD); return "Toggled mute."; }
            if (action == "now_playing") return Playing();
            return "There's no media action called " + action + ".";
        }

        private static string Playing()
        {
            string title = NowPlaying();
            if (title.Length > 0 && !title.StartsWith("Spotify", StringComparison.Ordinal))
            {
                int dash = title.IndexOf(" - ", StringComparison.Ordinal);
                return "Spotify is playing " + (dash > 0 ? title.Substring(dash + 3) + " by " + title.Substring(0, dash) : title) + ".";
            }
            foreach (OpenWindow window in Windows())
            {
                Match video = Regex.Match(window.Title, @"^(?:\(\d+\)\s*)?(.+?) - YouTube( Music)?( - |$)");
                if (video.Success) return "Their YouTube tab is on " + video.Groups[1].Value + ".";
            }
            return title.Length > 0 ? "Spotify is open but paused." : "Nothing's playing that I can see.";
        }

        private static void Tap(byte key)
        {
            Native.keybd_event(key, 0, 0x1, UIntPtr.Zero);
            Native.keybd_event(key, 0, 0x1 | 0x2, UIntPtr.Zero);
        }

        private static string PlayMusic(string song, string app, string kind)
        {
            if (string.IsNullOrWhiteSpace(song)) return "What should I play?";
            song = song.Trim();
            if (Regex.IsMatch(song, @"^((what('|\u2019)?s|what is)\s+)?((currently|now)\s+)?playing( now)?$|^(this|that|the current) (song|track)$", RegexOptions.IgnoreCase)) return Playing();
            if (Regex.IsMatch(song, @"^(play\s+)?(me\s+)?((some|any|a|random|good)\s+)?(music|songs?|tunes|tracks?|something)(\s+for me)?$", RegexOptions.IgnoreCase)) return "What should I play?";
            if (Safety.Screen(song) != null) return "Not playing that, choom.";
            Match tail = Regex.Match(song, @"^(?:(?:the|my|your)\s+)?(.+?)\s+(playlist|album)$", RegexOptions.IgnoreCase);
            if (tail.Success && kind != "artist") { kind = tail.Groups[2].Value.ToLowerInvariant(); song = tail.Groups[1].Value; }
            bool liked = Regex.IsMatch(song, @"^((my|your|the)\s+)?((liked|saved)\s+songs|likes)$", RegexOptions.IgnoreCase);
            if (liked) kind = "playlist";
            if (kind != "playlist" && kind != "album" && kind != "artist") kind = "song";
            string named = kind == "song" ? song : "the " + song + " " + kind;
            if (app != "youtube" && Match(Apps(), "spotify").Count > 0)
            {
                if (liked) named = "their Liked Songs";
                string spotted = liked ? null : Find(Fetch("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString("site:open.spotify.com/" + (kind == "song" ? "track" : kind) + " " + song)), @"open\.spotify\.com(?:/|%2F)" + (kind == "song" ? "track" : kind) + @"(?:/|%2F)([A-Za-z0-9]{22})");
                if (kind == "song" && spotted != null)
                {
                    string before = NowPlaying();
                    Run("spotify:track:" + spotted);
                    return Started(before) || PressPlay(song) ? "Playing " + song + " on Spotify." : "Opened " + song + " in Spotify, but couldn't press play. Hit the green Play button.";
                }
                if (liked || spotted != null)
                {
                    Run(liked ? "spotify:collection:tracks" : "spotify:" + kind + ":" + spotted);
                    return PressPlay(liked ? "Liked Songs" : song) ? "Playing " + named + " on Spotify." : "Opened " + named + " in Spotify, but couldn't press play. Hit the green Play button.";
                }
                Run("spotify:search:" + Uri.EscapeDataString(song));
                return PressPlay(song) ? "Playing " + named + " on Spotify." : "Couldn't find " + named + " to play, so Spotify is open on a search for it.";
            }
            string where = Searches[0, 1] + Uri.EscapeDataString(song);
            System.Text.RegularExpressions.Match list = Regex.Match(kind == "song" ? "" : Fetch(where + "&sp=EgIQAw%253D%253D") ?? "", @"""watchEndpoint"":\{""videoId"":""([A-Za-z0-9_-]{11})"",""playlistId"":""([A-Za-z0-9_-]+)""");
            string video = list.Success ? null : Find(Fetch(where), "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            if (!list.Success && video == null) { Run(where); return "Couldn't find " + named + " to play, so YouTube is open on a search for it."; }
            Run(list.Success ? "https://www.youtube.com/watch?v=" + list.Groups[1].Value + "&list=" + list.Groups[2].Value : "https://www.youtube.com/watch?v=" + video);
            return (app == "spotify" ? "Spotify isn't installed, so playing " : "Playing ") + named + " on YouTube.";
        }

        private static bool ClickPlay(string name)
        {
            string[] want = Words(name);
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                foreach (Process process in Process.GetProcessesByName("Spotify"))
                    using (process)
                    {
                        if (process.MainWindowHandle == IntPtr.Zero) continue;
                        AutomationElement close = null;
                        foreach (AutomationElement button in AutomationElement.FromHandle(process.MainWindowHandle).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                        {
                            string label = button.Current.Name ?? "";
                            if (!label.StartsWith("Play ", StringComparison.Ordinal)) continue;
                            string[] shown = Words(label.Substring(5));
                            bool exact = Within(shown, want) && Within(want, shown);
                            if (!exact && (close != null || (!Within(shown, want) && !Within(want, shown)))) continue;
                            if (!exact) { close = button; continue; }
                            close = button;
                            break;
                        }
                        object pattern;
                        if (close == null || (i < 6 && !Within(Words(close.Current.Name.Substring(5)), want)) || !close.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) continue;
                        ((InvokePattern)pattern).Invoke();
                        return true;
                    }
            }
            return false;
        }

        private static string[] Words(string text) { return Plain(Regex.Replace(text ?? "", @"\s+by\s+.*$", "", RegexOptions.IgnoreCase)).Split(' '); }

        private static bool Within(string[] part, string[] whole)
        {
            if (part.Length == 0 || part[0].Length == 0) return false;
            foreach (string word in part) if (Array.IndexOf(whole, word) < 0) return false;
            return true;
        }

        private static string SpotifyTitle()
        {
            foreach (Process process in Process.GetProcessesByName("Spotify"))
                using (process) if (process.MainWindowTitle.Length > 0) return process.MainWindowTitle;
            return "";
        }

        private static bool Started(string before)
        {
            for (int i = 0; i < 24; i++)
            {
                string title = NowPlaying();
                if (title.Length > 0 && title != before && !title.StartsWith("Spotify", StringComparison.Ordinal)) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static string Find(string page, string pattern)
        {
            Group found = Regex.Match(page ?? "", pattern).Groups[1];
            return found.Success ? found.Value : null;
        }

        private static string Download(string address)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
            request.Headers["Accept-Language"] = "en";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (WebException) { return ""; }
            catch (IOException) { return ""; }
        }

        private static string Window(string wanted, string action)
        {
            if (action != "switch_to" && action != "minimize" && action != "close") return "There's no window action called " + action + ".";
            string want = Regex.Replace(Plain(wanted), @"^the |( app| application| game| tab| window)$", "");
            if (want.Length == 0) return "Which app, choom?";
            wanted = wanted.Trim();
            List<OpenWindow> top = new List<OpenWindow>();
            int best = 0;
            foreach (OpenWindow window in Windows())
            {
                int score = Math.Max(Score(Plain(window.Program), want), Score(Plain(window.Title), want));
                if (score == 0 || score < best) continue;
                if (score > best) { best = score; top.Clear(); }
                top.Add(window);
            }
            if (top.Count == 0) return wanted + " isn't open.";
            List<string> programs = new List<string>();
            List<string> titles = new List<string>();
            foreach (OpenWindow window in top)
                if (!programs.Contains(window.Program)) { programs.Add(window.Program); titles.Add(window.Title.Length > 40 ? window.Title.Substring(0, 37) + "..." : window.Title); }
            if (programs.Count > 1) return "More than one open app matches " + wanted + ": " + string.Join(", ", titles.GetRange(0, Math.Min(3, titles.Count)).ToArray()) + ". Which one?";
            if (action == "switch_to") return Steer(top[0], action) ? "Switched to " + wanted + "." : "Couldn't bring " + wanted + " to the front.";
            if (action == "close" && Array.IndexOf(Browsers, top[0].Program.ToLowerInvariant()) >= 0 && Score(Plain(top[0].Program), want) == 0)
                return Steer(top[0], "close_tab") ? "Closed the " + wanted + " tab." : "Couldn't reach the " + wanted + " tab to close it.";
            bool all = true;
            foreach (OpenWindow window in top) all &= Steer(window, action);
            if (action == "minimize") return "Minimized " + wanted + ".";
            return all ? "Closed " + wanted + "." : "Asked " + wanted + " to close, but it's still open. It might be asking you to save.";
        }

        private static List<OpenWindow> ReadWindows()
        {
            List<OpenWindow> found = new List<OpenWindow>();
            Dictionary<int, string> programs = new Dictionary<int, string>();
            int own = Process.GetCurrentProcess().Id;
            Visit visit = delegate(IntPtr window, IntPtr data)
            {
                int cloaked;
                if (!IsWindowVisible(window) || Native.GetWindow(window, 4) != IntPtr.Zero || (Native.GetWindowLong(window, -20) & 0x80) != 0) return true;
                if (DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int length = GetWindowTextLength(window);
                if (length == 0) return true;
                StringBuilder kind = new StringBuilder(64);
                GetClassName(window, kind, kind.Capacity);
                if (Array.IndexOf(Shell, kind.ToString()) >= 0) return true;
                int id;
                GetWindowThreadProcessId(window, out id);
                if (id == own) return true;
                string program;
                if (!programs.TryGetValue(id, out program))
                {
                    try { using (Process process = Process.GetProcessById(id)) program = process.ProcessName; }
                    catch (ArgumentException) { program = ""; }
                    catch (InvalidOperationException) { program = ""; }
                    programs[id] = program;
                }
                StringBuilder title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);
                found.Add(new OpenWindow(window, title.ToString(), program));
                return true;
            };
            EnumWindows(visit, IntPtr.Zero);
            GC.KeepAlive(visit);
            return found;
        }

        private static bool Apply(OpenWindow window, string action)
        {
            IntPtr handle = window.Handle;
            if (action == "minimize") { ShowWindow(handle, 6); return true; }
            if (action == "close")
            {
                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20 && IsWindow(handle); i++) Thread.Sleep(100);
                return !IsWindow(handle);
            }
            if (!Front(handle)) return false;
            if (action != "close_tab") return true;
            Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x57, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x57, 0, 0x2, UIntPtr.Zero);
            Native.keybd_event(0x11, 0, 0x2, UIntPtr.Zero);
            return true;
        }

        private static bool Front(IntPtr handle)
        {
            if (IsIconic(handle)) ShowWindow(handle, 9);
            int ignored;
            int mine = GetCurrentThreadId();
            int theirs = GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            if (attached) AttachThreadInput(mine, theirs, false);
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) Thread.Sleep(50);
            return Native.GetForegroundWindow() == handle;
        }
    }
}
