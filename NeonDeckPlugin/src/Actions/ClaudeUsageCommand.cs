namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Live Claude plan usage on the key: ring + big number = current 5-hour window, title shows the weekly figure.
    ///
    /// Two sources, local first:
    ///  1. %LOCALAPPDATA%\NeonDeck\claude-status.json - written by the Claude Code status line script
    ///     (~/.claude/neon-statusline.js) after every API response. No network, no rate limits.
    ///  2. The Anthropic usage endpoint, only as a fallback when the local mirror is stale, polled very gently
    ///     (max every 15 min, exponential backoff on 429, state persisted across reloads).
    /// </summary>
    public sealed class ClaudeUsageCommand : NeonCommand
    {
        private const Int32 LocalPollSeconds = 15;
        private const Int32 LocalFreshMinutes = 30;          // mirror younger than this => never touch the endpoint
        private const Int32 EndpointEverySeconds = 900;
        private const Int32 MinBackoffSeconds = 900;
        private const Int32 MaxBackoffSeconds = 3600;
        private const Int32 CacheFreshSeconds = 180;

        private static readonly String CacheFile = Path.Combine(DeckConfig.DataDirectory, "usage-cache.json");
        private static readonly String BackoffFile = Path.Combine(DeckConfig.DataDirectory, "usage-backoff.txt");
        private static readonly String StatusMirror = Path.Combine(DeckConfig.DataDirectory, "claude-status.json");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
        private static ClaudeUsageCommand _instance;

        private UsageSnapshot _usage;                 // last GOOD numbers (either source), or null
        private String _source;                       // "Claude Code" | "usage API" | "cache"
        private String _lastError;                    // latest endpoint error, null when fine
        private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
        private Int32 _failures;
        private Int32 _fetching;
        private Boolean _flashRefresh;
        private DateTime _mirrorWrite;

        public ClaudeUsageCommand()
            : base("Claude Usage", "Shows your Claude plan usage live on the key (5-hour window ring, weekly in the label). Press for details.", "Neon Deck | AI")
        {
            _instance = this;
        }

        /// <summary>Called by the AI keys after a Claude call so the meter catches up without waiting for the next poll.</summary>
        public static void NotifyUsageChanged()
        {
            var me = _instance;
            if (me == null)
            {
                return;
            }
            _ = Task.Delay(15_000).ContinueWith(_ => me.ReadMirror(force: true));
        }

        protected override Boolean OnLoad()
        {
            this.LoadCache();
            this.LoadBackoff();
            this.ReadMirror(force: true);
            this.Deck.Tick += this.OnTick;
            if (!this.MirrorIsFresh() && (this._usage == null || (DateTimeOffset.UtcNow - this._usage.FetchedAt).TotalSeconds > CacheFreshSeconds)
                && DateTimeOffset.UtcNow >= this._nextAllowed)
            {
                _ = this.RefreshFromEndpoint();
            }
            return base.OnLoad();
        }

        protected override Boolean OnUnload()
        {
            if (this.Deck != null)
            {
                this.Deck.Tick -= this.OnTick;
            }
            if (_instance == this)
            {
                _instance = null;
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

            if (tick % LocalPollSeconds == 0)
            {
                this.ReadMirror(force: false);
            }
            if (tick % EndpointEverySeconds == 0 && !this.MirrorIsFresh())
            {
                _ = this.RefreshFromEndpoint();
            }
            else if (tick % 60 == 0 && this._lastError != null)
            {
                this.Refresh(); // keep the "retry in" hint honest while backing off
            }
        }

        // ---- source 1: the Claude Code status-line mirror ----

        private Boolean MirrorIsFresh()
        {
            try
            {
                return File.Exists(StatusMirror) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(StatusMirror)).TotalMinutes < LocalFreshMinutes;
            }
            catch
            {
                return false;
            }
        }

        private void ReadMirror(Boolean force)
        {
            try
            {
                if (!File.Exists(StatusMirror))
                {
                    return;
                }
                var write = File.GetLastWriteTimeUtc(StatusMirror);
                if (!force && write == this._mirrorWrite)
                {
                    return;
                }
                this._mirrorWrite = write;

                using var doc = JsonDocument.Parse(File.ReadAllText(StatusMirror));
                var root = doc.RootElement;
                if (!root.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object)
                {
                    return;
                }
                var five = Window(rl, "five_hour", "5h");
                var week = Window(rl, "seven_day", "week");
                if (five == null && week == null)
                {
                    return; // session has not had its first API response yet
                }
                var when = root.TryGetProperty("updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ua.GetString(), out var dt)
                    ? dt.ToUniversalTime()
                    : new DateTimeOffset(write, TimeSpan.Zero);

                // Only let the mirror override a newer endpoint reading.
                if (this._usage != null && this._source == "usage API" && this._usage.FetchedAt > when)
                {
                    return;
                }
                var changed = this._usage == null || this._lastError != null ||
                              (five?.Percent ?? -1) != (this._usage.Session?.Percent ?? -1) ||
                              (week?.Percent ?? -1) != (this._usage.Week?.Percent ?? -1);
                this._usage = new UsageSnapshot { Session = five, Week = week, WeekScoped = this._usage?.WeekScoped, FetchedAt = when };
                this._source = "Claude Code";
                this._lastError = null; // fresh local numbers make the endpoint problem irrelevant to the face
                this.SaveCache();
                if (changed)
                {
                    PluginLog.Info($"usage: from Claude Code status line - 5h {five?.Percent}% · week {week?.Percent}%");
                    this._lastChange = DateTimeOffset.UtcNow;
                    this.Refresh();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: mirror read failed");
            }
        }

        private static UsageWindow Window(JsonElement rl, String prop, String label)
        {
            if (!rl.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var pct = w.TryGetProperty("used_percentage", out var p) && p.ValueKind == JsonValueKind.Number ? (Int32)Math.Round(p.GetDouble()) : 0;
            DateTimeOffset? resets = null;
            if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number)
            {
                var v = r.GetDouble();
                resets = DateTimeOffset.FromUnixTimeSeconds((Int64)(v > 1e12 ? v / 1000 : v));
            }
            return new UsageWindow { Label = label, Percent = pct, ResetsAt = resets };
        }

        // ---- source 2: the usage endpoint (fallback) ----

        private async Task RefreshFromEndpoint(Boolean force = false)
        {
            if (!force && DateTimeOffset.UtcNow < this._nextAllowed)
            {
                return;
            }
            if (Interlocked.Exchange(ref this._fetching, 1) == 1)
            {
                return;
            }
            try
            {
                var next = await ClaudeUsageClient.Fetch();
                if (next.Ok)
                {
                    var changed = this._usage == null || this._lastError != null ||
                                  (next.Session?.Percent ?? -1) != (this._usage.Session?.Percent ?? -1) ||
                                  (next.Week?.Percent ?? -1) != (this._usage.Week?.Percent ?? -1);
                    this._usage = next;
                    this._source = "usage API";
                    this._lastError = null;
                    this._failures = 0;
                    this._nextAllowed = DateTimeOffset.MinValue;
                    this.SaveCache();
                    this.SaveBackoff();
                    if (changed)
                    {
                        this.Refresh();
                    }
                }
                else
                {
                    this._lastError = next.Error;
                    this._failures++;
                    var backoff = Math.Min(MaxBackoffSeconds, MinBackoffSeconds * (1 << Math.Min(2, this._failures - 1)));
                    var wait = Math.Max(backoff, next.RetryAfterSeconds ?? 0);
                    PluginLog.Info($"usage: endpoint {next.Error}; next attempt in {wait / 60} min");
                    this._nextAllowed = DateTimeOffset.UtcNow.AddSeconds(wait);
                    this.SaveBackoff();
                    this.Refresh();
                }
            }
            finally
            {
                Interlocked.Exchange(ref this._fetching, 0);
            }
        }

        // ---- persistence ----

        private void LoadCache()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var cached = JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllText(CacheFile), JsonOptions);
                    if (cached?.Session != null && cached.Error == null)
                    {
                        this._usage = cached;
                        this._source = "cache";
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: cache load failed");
            }
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(DeckConfig.DataDirectory);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(this._usage, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: cache save failed");
            }
        }

        private void LoadBackoff()
        {
            try
            {
                if (!File.Exists(BackoffFile))
                {
                    return;
                }
                var parts = File.ReadAllText(BackoffFile).Split('|');
                if (parts.Length >= 3 && DateTimeOffset.TryParse(parts[0], out var until) && Int32.TryParse(parts[1], out var failures) && until > DateTimeOffset.UtcNow)
                {
                    this._nextAllowed = until;
                    this._failures = failures;
                    this._lastError = this._usage == null ? parts[2] : null;
                }
            }
            catch
            {
            }
        }

        private void SaveBackoff()
        {
            try
            {
                Directory.CreateDirectory(DeckConfig.DataDirectory);
                if (this._nextAllowed <= DateTimeOffset.UtcNow)
                {
                    File.Delete(BackoffFile);
                }
                else
                {
                    File.WriteAllText(BackoffFile, $"{this._nextAllowed:o}|{this._failures}|{this._lastError ?? "HTTP 429"}");
                }
            }
            catch
            {
            }
        }

        // ---- face ----

        private static BitmapColor Level(Int32 pct) => pct >= 85 ? Neon.Red : pct >= 60 ? Neon.Amber : Neon.Cyan;

        private static String ShortError(String err) => err switch
        {
            "HTTP 429" => "rate limit",
            "token expired" => "expired",
            null => "…",
            _ => err,
        };

        protected override Neon.Face BuildFace()
        {
            var u = this._usage;
            if (u == null)
            {
                return new Neon.Face
                {
                    Icon = "claude_usage.svg",
                    Accent = this._lastError == null ? Neon.Cyan : Neon.Dim,
                    Title = "Claude",
                    Subtitle = ShortError(this._lastError),
                    Progress = 0,
                };
            }

            var session = u.Session?.Percent ?? 0;
            var week = u.Week?.Percent ?? 0;
            var age = DateTimeOffset.UtcNow - u.FetchedAt;
            var stale = age.TotalHours > 5; // the 5-hour window has fully rolled since we last heard anything
            return new Neon.Face
            {
                Icon = "claude_usage.svg",
                Accent = stale ? Neon.Dim : Level(Math.Max(session, week)),
                Title = $"Claude · wk {week}%",
                ShortTitle = $"wk {week}%",
                Subtitle = stale ? $"{session}% ·old" : $"{session}%",
                Progress = session / 100.0,
                Busy = this._flashRefresh,
                Heartbeat = this._beat,
                Corner = Neon.ResetsLabel(u.Session?.ResetsAt),
                Active = (DateTimeOffset.UtcNow - this._lastChange).TotalSeconds < 3, // flash the frame when a new number lands
            };
        }

        protected override void Execute(String actionParameter)
        {
            this._flashRefresh = true;
            this.Refresh();
            this.RunAsync("Claude usage", async () =>
            {
                this.ReadMirror(force: true);
                if (!this.MirrorIsFresh() && DateTimeOffset.UtcNow >= this._nextAllowed)
                {
                    await this.RefreshFromEndpoint(force: true);
                }
                this._flashRefresh = false;
                this.Refresh();

                var u = this._usage;
                if (u == null)
                {
                    await Win.Toast("Claude usage", this._lastError == "sign in"
                        ? "No Claude login found - open Claude Code once and sign in."
                        : "No numbers yet. Use Claude Code once (its status line feeds this key) - the usage API itself is rate limited right now.");
                    return;
                }

                var age = DateTimeOffset.UtcNow - u.FetchedAt;
                var ageTxt = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(Int32)age.TotalMinutes} min ago" : $"{(Int32)age.TotalHours}h {age.Minutes}m ago";
                var lines = new System.Collections.Generic.List<String>();
                if (u.Session != null)
                {
                    lines.Add($"5-hour window: {u.Session.Percent}%  · resets in {u.Session.ResetsIn()} ({Local(u.Session.ResetsAt)})");
                }
                if (u.Week != null)
                {
                    lines.Add($"Week (all models): {u.Week.Percent}%  · resets {Local(u.Week.ResetsAt, withDay: true)}");
                }
                if (u.WeekScoped != null)
                {
                    lines.Add($"{u.WeekScoped.Label}: {u.WeekScoped.Percent}%");
                }
                lines.Add($"from {this._source ?? "cache"} · {ageTxt}");
                if (this._lastError != null)
                {
                    var retry = Math.Max(0, (Int32)(this._nextAllowed - DateTimeOffset.UtcNow).TotalMinutes);
                    lines.Add($"⚠ usage API: {this._lastError} · retry in ~{retry} min");
                }
                await Win.Toast($"Claude usage · {u.Session?.Percent ?? 0}% of 5h · {u.Week?.Percent ?? 0}% of week", String.Join("\n", lines), silent: true);
            }, () => { this._flashRefresh = false; this.Refresh(); });
        }

        private static String Local(DateTimeOffset? t, Boolean withDay = false) =>
            t == null ? "?" : t.Value.ToLocalTime().ToString(withDay ? "ddd HH:mm" : "HH:mm");
    }
}
