namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>One usage window as reported by Claude: percent used and when it resets.</summary>
    public sealed class UsageWindow
    {
        public String Label { get; set; }          // "5h", "week", "Fable week"
        public Int32 Percent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }

        public String ResetsIn()
        {
            if (this.ResetsAt == null)
            {
                return "";
            }
            var left = this.ResetsAt.Value - DateTimeOffset.UtcNow;
            if (left.TotalMinutes < 1)
            {
                return "now";
            }
            return left.TotalHours >= 24
                ? $"{(Int32)left.TotalDays}d {left.Hours}h"
                : $"{(Int32)left.TotalHours}h {left.Minutes:00}m";
        }
    }

    public sealed class UsageSnapshot
    {
        public UsageWindow Session { get; set; }       // rolling 5-hour window
        public UsageWindow Week { get; set; }          // 7-day, all models
        public UsageWindow WeekScoped { get; set; }    // 7-day for the currently scoped model (may be null)
        public DateTimeOffset FetchedAt { get; set; }
        public String Error { get; set; }              // null when OK; "sign in" / "offline" / "HTTP 429" otherwise
        public Int32? RetryAfterSeconds { get; set; }  // from a 429 response, when the server says so
        public Boolean Ok => this.Error == null;
    }

    /// <summary>
    /// Reads the plan usage that Claude Code shows under /usage, using the login Claude Code already stores
    /// in %USERPROFILE%\.claude\.credentials.json. The token never leaves this machine except to api.anthropic.com.
    /// </summary>
    internal static class ClaudeUsageClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private const String Endpoint = "https://api.anthropic.com/api/oauth/usage";

        private static String CredentialsFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        private static (String token, DateTimeOffset expires)? ReadToken()
        {
            try
            {
                if (!File.Exists(CredentialsFile))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsFile));
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                {
                    return null;
                }
                var token = oauth.GetProperty("accessToken").GetString();
                var expiresMs = oauth.TryGetProperty("expiresAt", out var e) ? e.GetInt64() : 0;
                return String.IsNullOrEmpty(token) ? null : (token, DateTimeOffset.FromUnixTimeMilliseconds(expiresMs));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: cannot read Claude credentials");
                return null;
            }
        }

        public static async Task<UsageSnapshot> Fetch()
        {
            var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.UtcNow };
            var cred = ReadToken();
            if (cred == null)
            {
                snap.Error = "sign in";
                return snap;
            }
            if (cred.Value.expires < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                // Claude Code refreshes this the next time it talks to the API; we just report it.
                snap.Error = "token expired";
                return snap;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Value.token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.UserAgent.ParseAdd("NeonDeck/0.3");
                using var res = await Http.SendAsync(req);
                var status = (Int32)res.StatusCode;
                if (status == 401 || status == 403)
                {
                    snap.Error = "sign in";
                    return snap;
                }
                if (status == 429)
                {
                    snap.Error = "HTTP 429";
                    if (res.Headers.RetryAfter != null)
                    {
                        if (res.Headers.RetryAfter.Delta.HasValue)
                        {
                            snap.RetryAfterSeconds = (Int32)res.Headers.RetryAfter.Delta.Value.TotalSeconds;
                        }
                        else if (res.Headers.RetryAfter.Date.HasValue)
                        {
                            snap.RetryAfterSeconds = (Int32)(res.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                        }
                    }
                    PluginLog.Warning($"usage: rate limited (retry-after {snap.RetryAfterSeconds?.ToString() ?? "n/a"} s)");
                    return snap;
                }
                if (!res.IsSuccessStatusCode)
                {
                    snap.Error = $"HTTP {status}";
                    return snap;
                }
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                snap.Session = Window(root, "five_hour", "5h");
                snap.Week = Window(root, "seven_day", "week");
                if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in limits.EnumerateArray())
                    {
                        if (l.TryGetProperty("kind", out var kind) && kind.GetString() == "weekly_scoped")
                        {
                            var model = "model";
                            if (l.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                                scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object &&
                                m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                            {
                                model = dn.GetString();
                            }
                            snap.WeekScoped = new UsageWindow
                            {
                                Label = $"{model} week",
                                Percent = l.TryGetProperty("percent", out var p) ? (Int32)Math.Round(p.GetDouble()) : 0,
                                ResetsAt = l.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(r.GetString()) : null,
                            };
                        }
                    }
                }
                if (snap.Session == null && snap.Week == null)
                {
                    snap.Error = "no data";
                }
            }
            catch (TaskCanceledException)
            {
                snap.Error = "offline";
            }
            catch (HttpRequestException ex)
            {
                PluginLog.Warning($"usage: {ex.Message}");
                snap.Error = "offline";
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: fetch failed");
                snap.Error = "error";
            }
            return snap;
        }

        private static UsageWindow Window(JsonElement root, String prop, String label)
        {
            if (!root.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? (Int32)Math.Round(u.GetDouble()) : 0;
            DateTimeOffset? resets = null;
            if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dt))
            {
                resets = dt;
            }
            return new UsageWindow { Label = label, Percent = pct, ResetsAt = resets };
        }
    }
}
