namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// Codex quota on the key: ring + big number = 5-hour window, label shows the weekly window.
    /// Reads what Codex last logged in its own session files (no network), so it updates whenever Codex is used.
    /// </summary>
    public sealed class CodexUsageCommand : NeonCommand
    {
        private const Int32 PollEverySeconds = 20;

        private CodexUsage _usage;
        private String _lastFile;
        private Int64 _lastLength;
        private DateTime _lastWrite;
        private Int32 _busy;

        public CodexUsageCommand()
            : base("Codex Usage", "Shows your Codex quota live on the key (5-hour window ring, weekly in the label), read from Codex's own session logs. Press for details.", "Neon Deck | AI")
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

        private Int32 _beat;
        private DateTimeOffset _lastChange = DateTimeOffset.MinValue;

        private void OnTick(Int32 tick)
        {
            // heartbeat: the key visibly breathes once a second so you can see the meter is alive
            this._beat = tick & 3;
            this.Refresh();

            if (tick % PollEverySeconds == 0)
            {
                this.Poll(force: false);
            }
        }

        /// <summary>Cheap change detection on the newest session file; parse only when it moved.</summary>
        private void Poll(Boolean force)
        {
            if (System.Threading.Interlocked.Exchange(ref this._busy, 1) == 1)
            {
                return;
            }
            try
            {
                var newest = CodexUsageReader.NewestSessionFile();
                var changed = force || newest == null
                    ? force
                    : newest.FullName != this._lastFile || newest.Length != this._lastLength || newest.LastWriteTimeUtc != this._lastWrite;
                if (!changed)
                {
                    return;
                }
                if (newest != null)
                {
                    this._lastFile = newest.FullName;
                    this._lastLength = newest.Length;
                    this._lastWrite = newest.LastWriteTimeUtc;
                }
                var next = CodexUsageReader.ReadLatest();
                if (next == null)
                {
                    if (this._usage == null)
                    {
                        this.Refresh();
                    }
                    return;
                }
                var differs = this._usage == null || next.PrimaryPercent != this._usage.PrimaryPercent ||
                              next.SecondaryPercent != this._usage.SecondaryPercent || next.ObservedAt != this._usage.ObservedAt;
                this._usage = next;
                if (differs)
                {
                    this._lastChange = DateTimeOffset.UtcNow;
                    this.Refresh();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "codex: poll failed");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref this._busy, 0);
            }
        }

        private static BitmapColor Level(Int32 pct) => pct >= 85 ? Neon.Red : pct >= 60 ? Neon.Amber : Neon.Mint;

        protected override Neon.Face BuildFace()
        {
            var u = this._usage;
            if (u == null)
            {
                return new Neon.Face
                {
                    Icon = "codex_usage.svg",
                    Accent = CodexUsageReader.IsCodexPresent ? Neon.Mint : Neon.Dim,
                    Title = "Codex",
                    Subtitle = CodexUsageReader.IsCodexPresent ? "no data" : "not found",
                    Progress = 0,
                };
            }
            // A reading older than the 5-hour window itself is stale by definition: the window has rolled over.
            var age = DateTimeOffset.UtcNow - u.ObservedAt;
            var stale = age.TotalMinutes > Math.Max(60, u.PrimaryWindowMinutes);
            return new Neon.Face
            {
                Icon = "codex_usage.svg",
                Accent = stale ? Neon.Dim : Level(Math.Max(u.PrimaryPercent, u.SecondaryPercent)),
                Title = $"Codex · wk {u.SecondaryPercent}%",
                ShortTitle = $"wk {u.SecondaryPercent}%",
                Subtitle = stale ? $"{u.PrimaryPercent}% ·old" : $"{u.PrimaryPercent}%",
                Progress = u.PrimaryPercent / 100.0,
                Heartbeat = this._beat,
                Corner = Neon.ResetsLabel(u.PrimaryResetsAt),
                Active = (DateTimeOffset.UtcNow - this._lastChange).TotalSeconds < 3, // flash the frame when a new number lands
            };
        }

        protected override void Execute(String actionParameter)
        {
            this.RunAsync("Codex usage", async () =>
            {
                this.Poll(force: true);
                var u = this._usage;
                if (u == null)
                {
                    await Win.Toast("Codex usage", CodexUsageReader.IsCodexPresent
                        ? "No rate-limit data in Codex session logs yet - run one Codex turn and it will appear."
                        : "Codex is not installed for this user (no %USERPROFILE%\\.codex\\sessions).");
                    return;
                }
                var age = DateTimeOffset.UtcNow - u.ObservedAt;
                var ageTxt = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(Int32)age.TotalMinutes} min ago" : $"{(Int32)age.TotalHours}h {age.Minutes}m ago";
                var lines = new System.Collections.Generic.List<String>
                {
                    $"5-hour window: {u.PrimaryPercent}%  · resets in {ResetsIn(u.PrimaryResetsAt)} ({Local(u.PrimaryResetsAt)})",
                    $"Week: {u.SecondaryPercent}%  · resets {Local(u.SecondaryResetsAt, withDay: true)}",
                    $"as reported by Codex {ageTxt}",
                };
                await Win.Toast($"Codex usage · {u.PrimaryPercent}% of 5h · {u.SecondaryPercent}% of week", String.Join("\n", lines), silent: true);
            });
        }

        private static String ResetsIn(DateTimeOffset? t)
        {
            if (t == null)
            {
                return "?";
            }
            var left = t.Value - DateTimeOffset.UtcNow;
            if (left.TotalMinutes < 1)
            {
                return "now";
            }
            return left.TotalHours >= 24 ? $"{(Int32)left.TotalDays}d {left.Hours}h" : $"{(Int32)left.TotalHours}h {left.Minutes:00}m";
        }

        private static String Local(DateTimeOffset? t, Boolean withDay = false) =>
            t == null ? "?" : t.Value.ToLocalTime().ToString(withDay ? "ddd HH:mm" : "HH:mm");
    }
}
