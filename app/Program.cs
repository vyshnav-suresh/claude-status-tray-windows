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

    public sealed record View(string IconState, string Tooltip, List<string> Rows);

    // Given the live sessions, pick the icon state, tooltip, and the started-session rows (FR2).
    public static View Evaluate(IReadOnlyList<Session> live)
    {
        var top = live.OrderByDescending(s => Priority(s.State)).FirstOrDefault();
        string iconState = top?.State ?? "idle";

        string tooltip;
        if (top is null)
            tooltip = "Claude: idle";
        else
        {
            string what = string.IsNullOrEmpty(top.Label) ? top.State : top.Label;
            tooltip = string.IsNullOrEmpty(top.Project) ? $"Claude: {what}" : $"{what} — {top.Project}";
        }
        if (tooltip.Length > 63) tooltip = tooltip[..63]; // NotifyIcon.Text hard limit

        var rows = live
            .Where(s => s.Started)
            .OrderByDescending(s => Priority(s.State))
            .Select(s =>
            {
                string what = string.IsNullOrEmpty(s.Label) ? s.State : s.Label;
                string proj = string.IsNullOrEmpty(s.Project) ? "(unknown)" : s.Project;
                return $"{proj} — {what}";
            })
            .ToList();

        return new View(iconState, tooltip, rows);
    }
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
        string _lastSig = "";

        public TrayApp()
        {
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

            // Only touch the UI when something actually changed (idle-cheap).
            string sig = view.IconState + "|" + view.Tooltip + "|" + string.Join(";", view.Rows);
            if (sig == _lastSig) return;
            _lastSig = sig;

            _icon.Icon = IconFor(view.IconState);
            _icon.Text = view.Tooltip;

            var menu = _icon.ContextMenuStrip!;
            menu.Items.Clear();
            if (view.Rows.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
            else
                foreach (var row in view.Rows)
                    menu.Items.Add(new ToolStripMenuItem(row) { Enabled = false }); // click-to-focus is M3
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { _icon.Visible = false; Application.Exit(); }));
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

        Icon IconFor(string state)
        {
            if (_icons.TryGetValue(state, out var cached)) return cached;
            var ico = MakeIcon(state);
            _icons[state] = ico;
            return ico;
        }

        public void Dispose()
        {
            _timer.Dispose();
            _icon.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
    }

    // Draw a 16px dot colored by state. permission gets an attention badge.
    // ponytail: fixed colors that read on both light/dark taskbars; System-theme tint is M3.
    static Icon MakeIcon(string state)
    {
        Color c = state switch
        {
            "permission" => Color.Gold,
            "tool" => Color.DarkOrange,
            "thinking" => Color.Orange,
            "done" => Color.MediumSeaGreen,
            _ => Color.SlateGray, // idle / unknown
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

        Console.WriteLine("selftest OK");
        return 0;
    }
}
