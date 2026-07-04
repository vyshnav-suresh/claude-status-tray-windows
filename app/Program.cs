using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudeStatusTray;

// One session's state file: %USERPROFILE%\.claude\statusbar\state.d\<id>.json
// Field names/casing match the hooks' output (update.js / lifecycle.js) exactly.
sealed class Session
{
    [JsonPropertyName("state")] public string State { get; set; } = "idle";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("started")] public bool Started { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
}

// Pure rendering logic — no WinForms, no filesystem. Unit-testable (see --selftest).
static class Model
{
    // Higher wins. permission > tool > thinking > idle/done  (FR1).
    public static int Priority(string state) => state switch
    {
        "permission" => 3,
        "tool" => 2,
        "thinking" => 1,
        _ => 0, // idle, done, unknown
    };

    public sealed record View(string IconState, string Tooltip, List<string> Rows,
        string TopState, long TopStartedAt);

    static string What(Session s) => string.IsNullOrEmpty(s.Label) ? s.State : s.Label;

    public static string RowLabel(Session s)
        => $"{(string.IsNullOrEmpty(s.Project) ? "(unknown)" : s.Project)} — {What(s)}";

    // "Xm Ys" elapsed since a turn started (FR3). Pure/testable.
    public static string Elapsed(long startedAt, long now)
    {
        long e = now - startedAt;
        if (e < 0) e = 0;
        return $"{e / 60}m {e % 60}s";
    }

    // Given the live sessions, pick the icon state, base tooltip, and the started-session rows (FR2).
    // The elapsed timer (time-dependent) is appended by the caller so this stays deterministic.
    public static View Evaluate(IReadOnlyList<Session> live)
    {
        var top = live.OrderByDescending(s => Priority(s.State)).FirstOrDefault();
        string iconState = top?.State ?? "idle";

        string tooltip = top is null
            ? "Claude: idle"
            : (string.IsNullOrEmpty(top.Project) ? $"Claude: {What(top)}" : $"{What(top)} — {top.Project}");
        if (tooltip.Length > 63) tooltip = tooltip[..63]; // NotifyIcon.Text hard limit

        var rows = live
            .Where(s => s.Started)
            .OrderByDescending(s => Priority(s.State))
            .Select(RowLabel)
            .ToList();

        return new View(iconState, tooltip, rows, iconState, top?.StartedAt ?? 0);
    }
}

sealed class Settings
{
    public bool ShowTimer { get; set; } = true;         // FR6
    public bool CompletionSound { get; set; } = false;  // FR6: off by default
    public string IconColor { get; set; } = "Orange";   // FR6: "Orange" | "System"
}

static class Program
{
    static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "statusbar", "state.d");

    // pid liveness is the real check; ts is a backstop for a crashed session whose pid got
    // recycled onto an unrelated process. Idle files aren't rewritten, so keep it generous.
    const long StaleSeconds = 24 * 3600;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();

        ApplicationConfiguration.Initialize();
        EnsureAutostart();
        using var app = new TrayApp();
        Application.Run();
        return 0;
    }

    // ---- WinForms shell ----
    sealed class TrayApp : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly System.Windows.Forms.Timer _timer;
        readonly Dictionary<string, (DateTime mtime, Session data)> _cache = new();
        readonly Dictionary<string, Icon> _icons = new();
        readonly Dictionary<string, string> _lastState = new(); // sid -> last state, for done chime
        readonly Settings _cfg = LoadSettings();
        string _lastSig = "";
        bool _lastLight;

        public TrayApp()
        {
            _lastLight = LightTaskbar();
            _icon = new NotifyIcon { Visible = true, Text = "Claude: idle", Icon = IconFor("idle") };
            _icon.ContextMenuStrip = new ContextMenuStrip();
            _timer = new System.Windows.Forms.Timer { Interval = 400 }; // FR5
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
            Poll();
        }

        void Poll()
        {
            var live = ReadLive();
            var view = Model.Evaluate(live);

            ChimeOnDone(live); // FR6 completion sound

            // If the taskbar theme flipped, cached icons are the wrong contrast — drop them.
            bool light = LightTaskbar();
            if (light != _lastLight) { _lastLight = light; foreach (var i in _icons.Values) i.Dispose(); _icons.Clear(); _lastSig = ""; }

            // Tooltip carries the elapsed timer (FR3) — time-dependent, so set it every tick (cheap).
            string tooltip = view.Tooltip;
            if (_cfg.ShowTimer && (view.TopState == "thinking" || view.TopState == "tool") && view.TopStartedAt > 0)
            {
                string t = $" ({Model.Elapsed(view.TopStartedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds())})";
                tooltip = (view.Tooltip + t) is { Length: <= 63 } s ? s : view.Tooltip;
            }
            _icon.Text = tooltip;

            // Icon + menu are structural — rebuild only when state/rows change (idle-cheap).
            var started = live.Where(s => s.Started).OrderByDescending(s => Model.Priority(s.State)).ToList();
            string sig = view.IconState + "|" + string.Join(";", started.Select(s => s.SessionId + s.State));
            if (sig == _lastSig) return;
            _lastSig = sig;

            _icon.Icon = IconFor(view.IconState);
            BuildMenu(started);
        }

        void BuildMenu(List<Session> started)
        {
            var menu = _icon.ContextMenuStrip!;
            menu.Items.Clear();
            if (started.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
            else
                foreach (var s in started)
                {
                    int pid = s.Pid;
                    menu.Items.Add(new ToolStripMenuItem(Model.RowLabel(s), null, (_, _) => FocusSession(pid))); // FR4
                }

            menu.Items.Add(new ToolStripSeparator());

            // Settings (FR6) — checkable, persisted on toggle.
            var timer = new ToolStripMenuItem("Show timer", null, (_, _) => { _cfg.ShowTimer = !_cfg.ShowTimer; SaveSettings(_cfg); }) { Checked = _cfg.ShowTimer };
            var sound = new ToolStripMenuItem("Completion sound", null, (_, _) => { _cfg.CompletionSound = !_cfg.CompletionSound; SaveSettings(_cfg); }) { Checked = _cfg.CompletionSound };
            var color = new ToolStripMenuItem("Icon color");
            foreach (var mode in new[] { "Orange", "System" })
            {
                var m = mode;
                color.DropDownItems.Add(new ToolStripMenuItem(m, null, (_, _) =>
                {
                    _cfg.IconColor = m; SaveSettings(_cfg);
                    foreach (var i in _icons.Values) i.Dispose(); _icons.Clear(); _lastSig = ""; // re-tint
                }) { Checked = _cfg.IconColor == m });
            }
            menu.Items.Add(timer);
            menu.Items.Add(sound);
            menu.Items.Add(color);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { _icon.Visible = false; Application.Exit(); }));
        }

        // Fire the completion chime once per session as it transitions into `done`.
        void ChimeOnDone(List<Session> live)
        {
            var ids = new HashSet<string>();
            foreach (var s in live)
            {
                ids.Add(s.SessionId);
                _lastState.TryGetValue(s.SessionId, out var prev);
                if (s.State == "done" && prev != "done" && _cfg.CompletionSound)
                    System.Media.SystemSounds.Asterisk.Play(); // ponytail: system chime now; bundled mp3 is M4
                _lastState[s.SessionId] = s.State;
            }
            foreach (var gone in _lastState.Keys.Where(k => !ids.Contains(k)).ToList()) _lastState.Remove(gone);
        }

        Icon IconFor(string state)
        {
            string key = $"{state}|{_cfg.IconColor}|{_lastLight}";
            if (_icons.TryGetValue(key, out var cached)) return cached;
            var ico = MakeIcon(state, _cfg.IconColor, _lastLight);
            _icons[key] = ico;
            return ico;
        }

        List<Session> ReadLive()
        {
            var live = new List<Session>();
            if (!Directory.Exists(StateDir)) return live;

            var seen = new HashSet<string>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var f in Directory.EnumerateFiles(StateDir, "*.json"))
            {
                seen.Add(f);
                Session? s;
                try
                {
                    var mtime = File.GetLastWriteTimeUtc(f);
                    if (_cache.TryGetValue(f, out var hit) && hit.mtime == mtime)
                        s = hit.data; // FR5: re-parse only changed files
                    else
                    {
                        s = JsonSerializer.Deserialize<Session>(File.ReadAllText(f));
                        if (s is not null) _cache[f] = (mtime, s);
                    }
                }
                catch { continue; } // locked / partial write — skip this tick
                if (s is null) continue;
                if (!Alive(s.Pid) || now - s.Ts > StaleSeconds) continue; // FR5: drop dead/stale
                live.Add(s);
            }
            foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
                _cache.Remove(gone);
            return live;
        }

        public void Dispose()
        {
            _timer.Dispose();
            _icon.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
    }

    // Draw a 16px dot colored by state. permission gets an attention badge.
    // Working states are always their brand color; the neutral idle dot follows the "Icon color"
    // setting — "System" adapts to the taskbar theme for contrast, "Orange" stays a warm gray (FR6).
    static Icon MakeIcon(string state, string iconColor, bool lightTaskbar)
    {
        Color neutral = iconColor == "System"
            ? (lightTaskbar ? Color.FromArgb(90, 90, 90) : Color.FromArgb(220, 220, 220))
            : Color.SlateGray;
        Color c = state switch
        {
            "permission" => Color.Gold,
            "tool" => Color.DarkOrange,
            "thinking" => Color.Orange,
            "done" => Color.MediumSeaGreen,
            _ => neutral, // idle / unknown
        };
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 2, 2, 12, 12);
            if (state == "permission")
            {
                using var badge = new SolidBrush(Color.OrangeRed);
                g.FillEllipse(badge, 9, 0, 7, 7);
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); } // Clone -> owns its own copy
        finally { DestroyIcon(h); }
    }

    static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; } // ArgumentException => not running
    }

    static void EnsureAutostart() // FR8
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var name = Path.GetFileName(exe);
            // Skip when running under the SDK host (dev: `dotnet run` -> dotnet.exe), not the real app.
            if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            k?.SetValue("ClaudeStatusTray", $"\"{exe}\"");
        }
        catch { /* autostart is best-effort */ }
    }

    // ---- settings (FR6): %USERPROFILE%\.claude\statusbar\settings.json ----
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "statusbar", "settings.json");

    static Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { return new(); } // missing/corrupt => defaults
    }

    static void SaveSettings(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // Light taskbar? Registry SystemUsesLightTheme (0=dark, 1=light). Default dark on any error.
    static bool LightTaskbar()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    // ---- click-to-focus (FR4), best-effort ----
    // A CLI session's `claude` pid owns no window — the terminal (an ancestor) does. Walk up the
    // parent chain to the first process with a main window and bring it forward.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, ref ProcessBasicInformation pbi, int len, out int ret);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    const int SW_RESTORE = 9;

    static int ParentPid(Process p)
    {
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return 0;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch { return 0; }
    }

    static void FocusSession(int pid)
    {
        try
        {
            for (int i = 0; i < 8 && pid > 0; i++)
            {
                Process p;
                try { p = Process.GetProcessById(pid); } catch { return; }
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                    return;
                }
                pid = ParentPid(p);
            }
        }
        catch { /* best-effort; some terminals expose no focusable window */ }
    }

    // ---- self-check (no framework): dotnet run -- --selftest ----
    static int SelfTest()
    {
        void Check(bool ok, string msg) { if (!ok) throw new Exception("FAIL: " + msg); }

        int self = Environment.ProcessId;
        int dead = 999_999_990; // implausible pid

        Check(Alive(self), "current process should be alive");
        Check(!Alive(dead), "bogus pid should be dead");
        Check(!Alive(0), "pid 0 should be dead");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sessions = new List<Session>
        {
            new() { State = "thinking", Project = "alpha", Started = true, Pid = self, Ts = now, Label = "Thinking…" },
            new() { State = "permission", Project = "beta", Started = true, Pid = self, Ts = now, Label = "Awaiting permission" },
            new() { State = "idle", Project = "gamma", Started = false, Pid = self, Ts = now },
        };
        var v = Model.Evaluate(sessions);
        Check(v.IconState == "permission", "permission must win priority");
        Check(v.Tooltip.Contains("beta"), "tooltip should name the winning project");
        Check(v.Rows.Count == 2, "only started sessions listed (idle/unstarted excluded)");
        Check(v.Rows[0].Contains("beta"), "rows sorted by priority, permission first");

        Check(Model.Evaluate(new List<Session>()).IconState == "idle", "no sessions => idle");
        Check(Model.Evaluate(new List<Session>()).Tooltip == "Claude: idle", "empty tooltip");

        // FR3 elapsed timer formatting
        Check(Model.Elapsed(now - 75, now) == "1m 15s", "elapsed formats mm ss");
        Check(Model.Elapsed(now + 10, now) == "0m 0s", "negative elapsed clamps to zero");
        Check(v.TopStartedAt == 0, "permission winner has startedAt 0 (no timer)");

        // FR6 settings round-trip (defaults + persistence)
        var def = new Settings();
        Check(def.ShowTimer && !def.CompletionSound && def.IconColor == "Orange", "settings defaults");
        var json = JsonSerializer.Serialize(new Settings { ShowTimer = false, IconColor = "System" });
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Check(!back.ShowTimer && back.IconColor == "System", "settings round-trip through json");

        Console.WriteLine("selftest OK");
        return 0;
    }
}
