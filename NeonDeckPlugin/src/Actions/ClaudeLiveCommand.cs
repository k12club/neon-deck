namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// A physical status light for Claude Code. Fed by ~/.claude/neon-claude-hook.js:
    ///   cyan breathing = Claude is working · green = finished, your turn · amber blinking = Claude needs your answer
    /// The ring is the current session's context usage. Press to bring the Claude Code terminal to the front.
    /// </summary>
    public sealed class ClaudeLiveCommand : NeonCommand
    {
        private ClaudeLiveState _live = new ClaudeLiveState();
        private DateTime _fileWrite = DateTime.MinValue;
        private Int32 _beat;
        private ClaudeLiveState.Kind _lastKind = ClaudeLiveState.Kind.Off;

        public ClaudeLiveCommand()
            : base("Claude Live", "Status light for Claude Code: working, finished, or waiting for you. Ring = context used. Press to jump to the Claude Code terminal.", "Neon Deck | AI")
        {
        }

        protected override Boolean OnLoad()
        {
            this.Poll(force: true);
            this.Deck.Tick += this.OnTick;
            return base.OnLoad();
        }

        protected override Boolean OnUnload()
        {
            if (this.Deck != null)
            {
                this.Deck.Tick -= this.OnTick;
            }
            return base.OnUnload();
        }

        private void OnTick(Int32 tick)
        {
            this._beat = tick & 3;
            this.Poll(force: tick % 30 == 0); // re-evaluate "done" glow expiry even without file changes
            this.Refresh();                    // heartbeat / blink
        }

        private void Poll(Boolean force)
        {
            var write = ClaudeLiveReader.LiveFileWriteTime();
            if (!force && write == this._fileWrite)
            {
                return;
            }
            this._fileWrite = write;
            var next = ClaudeLiveReader.Read();
            var prev = this._lastKind;
            this._live = next;
            this._lastKind = next.State;
            if (next.State != prev)
            {
                PluginLog.Info($"claude live: {prev} -> {next.State} ({next.Sessions} session(s)) {next.Message}");
                if (next.State == ClaudeLiveState.Kind.NeedsInput)
                {
                    _ = Win.Toast("Claude needs you", Win.Clip(next.Message ?? "Waiting for your answer", 200), silent: true);
                }
            }
        }

        protected override Neon.Face BuildFace()
        {
            var l = this._live;
            var ctx = l.ContextPercent;
            var face = new Neon.Face
            {
                Icon = "claude_live.svg",
                Title = l.Sessions > 1 ? $"Claude · {l.Sessions} sessions" : "Claude",
                ShortTitle = "Claude",
                Progress = ctx.HasValue ? ctx.Value / 100.0 : 0,
                Corner = ctx.HasValue ? $"ctx {ctx}%" : null,
            };
            switch (l.State)
            {
                case ClaudeLiveState.Kind.Working:
                    face.Accent = Neon.Cyan;
                    face.Subtitle = "working…";
                    face.Heartbeat = this._beat;
                    break;
                case ClaudeLiveState.Kind.NeedsInput:
                    face.Accent = Neon.Amber;
                    face.Subtitle = "needs you";
                    face.Active = (this._beat & 1) == 0; // blink the frame
                    break;
                case ClaudeLiveState.Kind.Done:
                    face.Accent = Neon.Lime;
                    face.Subtitle = "DONE ✓";
                    face.Active = true;
                    break;
                case ClaudeLiveState.Kind.Ready:
                    face.Accent = Neon.Coral;
                    face.Subtitle = "ready";
                    break;
                default:
                    face.Accent = Neon.Dim;
                    face.Subtitle = "off";
                    break;
            }
            return face;
        }

        // ---- press: bring the Claude Code terminal to the front ----

        [DllImport("user32.dll")] private static extern Boolean EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern Boolean IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern Int32 GetWindowText(IntPtr hWnd, StringBuilder text, Int32 max);
        [DllImport("user32.dll")] private static extern Boolean SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern Boolean ShowWindow(IntPtr hWnd, Int32 cmd);
        [DllImport("user32.dll")] private static extern Boolean IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern UInt32 GetWindowThreadProcessId(IntPtr hWnd, out UInt32 pid);
        [DllImport("user32.dll")] private static extern Boolean AttachThreadInput(UInt32 a, UInt32 b, Boolean attach);
        [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, Boolean altTab);
        [DllImport("kernel32.dll")] private static extern UInt32 GetCurrentThreadId();
        private delegate Boolean EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Claude Code sets the terminal title to "<spinner glyph> <conversation title>", or contains "claude".
        private static readonly Regex ClaudeTitle = new Regex(@"^[✳✻◐◑◒◓◔◕●○◌⏺✶✷✸✹·⚡]\s|claude", RegexOptions.IgnoreCase);

        private static IntPtr FindClaudeWindow()
        {
            var found = IntPtr.Zero;
            var sb = new StringBuilder(512);
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h))
                {
                    return true;
                }
                sb.Clear();
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Length > 0 && ClaudeTitle.IsMatch(title))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        protected override void Execute(String actionParameter)
        {
            this.RunAsync("Claude Live", async () =>
            {
                var h = FindClaudeWindow();
                if (h == IntPtr.Zero)
                {
                    var l = this._live;
                    await Win.Toast("Claude Live", l.State == ClaudeLiveState.Kind.Off
                        ? "No Claude Code session is reporting yet. Start (or restart) Claude Code once so its hooks are active."
                        : $"Claude is {l.State.ToString().ToLower()}. Could not find the terminal window to focus.", silent: true);
                    return;
                }
                if (IsIconic(h))
                {
                    ShowWindow(h, 9);
                }
                if (!SetForegroundWindow(h))
                {
                    var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                    var me = GetCurrentThreadId();
                    AttachThreadInput(me, fgThread, true);
                    SetForegroundWindow(h);
                    AttachThreadInput(me, fgThread, false);
                    if (GetForegroundWindow() != h)
                    {
                        SwitchToThisWindow(h, true);
                    }
                }
            });
        }
    }
}
