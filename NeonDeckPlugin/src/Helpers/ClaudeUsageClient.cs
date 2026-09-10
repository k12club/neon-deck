namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
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
    /// in %USERPROFILE%\.claude\.credentials.json. The token never leaves this machine except to Anthropic.
    ///
    /// The access token only lives 8 hours. When it has expired (typically: the PC was off overnight) this client
    /// renews it exactly the way Claude Code does - same token endpoint, same OAuth client id, same request - and
    /// writes the new pair back into .credentials.json, so the key shows real numbers right after boot without
    /// Claude Code having been opened. Claude Code notices the file change (it watches the mtime and re-reads on
    /// 401) and simply uses the renewed token. A cooperative lock directory keeps the two from renewing at the
    /// same moment.
    /// </summary>
    internal static class ClaudeUsageClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private const String Endpoint = "https://api.anthropic.com/api/oauth/usage";
        private const String TokenUrl = "https://platform.claude.com/v1/oauth/token";
        private const String ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";   // Claude Code's public OAuth client id
        private static readonly String[] DefaultScopes = { "user:inference", "user:profile" };
        private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(2);       // renew when this close to expiry
        private static readonly TimeSpan LockStale = TimeSpan.FromSeconds(15);        // a lock older than this is a leftover

        private static String CredentialsFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        private static String LockDir => CredentialsFile + ".lock";

        private sealed class Cred
        {
            public String Access;
            public String Refresh;
            public DateTimeOffset Expires;
            public DateTimeOffset? RefreshExpires;
            public String[] Scopes;

            public Boolean Fresh => this.Expires > DateTimeOffset.UtcNow + RenewAhead;
        }

        private static Cred ReadCred()
        {
            try
            {
                if (!File.Exists(CredentialsFile))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsFile));
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                var c = new Cred
                {
                    Access = Str(oauth, "accessToken"),
                    Refresh = Str(oauth, "refreshToken"),
                    Expires = DateTimeOffset.FromUnixTimeMilliseconds(Num(oauth, "expiresAt")),
                };
                var re = Num(oauth, "refreshTokenExpiresAt");
                c.RefreshExpires = re > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(re) : null;
                if (oauth.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
                {
                    c.Scopes = sc.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()).ToArray();
                }
                return String.IsNullOrEmpty(c.Access) ? null : c;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: cannot read Claude credentials");
                return null;
            }
        }

        private static String Str(JsonElement o, String name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Int64 Num(JsonElement o, String name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (Int64)v.GetDouble() : 0;

        public static async Task<UsageSnapshot> Fetch()
        {
            var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.UtcNow };
            var cred = ReadCred();
            if (cred == null)
            {
                snap.Error = "sign in";
                return snap;
            }

            var renewed = false;
            if (!cred.Fresh)
            {
                var (next, error) = await Renew(cred);
                if (error != null)
                {
                    snap.Error = error;
                    return snap;
                }
                cred = next;
                renewed = true;
            }

            var status = await GetUsage(cred.Access, snap);
            if (status == 401 && !renewed)
            {
                // The server disagrees with expiresAt (clock skew, revoked token): renew once and retry.
                var (next, error) = await Renew(cred, force: true);
                if (error != null)
                {
                    snap.Error = error;
                    return snap;
                }
                snap.Error = null;
                await GetUsage(next.Access, snap);
            }
            return snap;
        }

        // ---- token renewal (mirrors Claude Code's own refresh flow) ----

        private static async Task<(Cred cred, String error)> Renew(Cred cred, Boolean force = false)
        {
            if (String.IsNullOrEmpty(cred.Refresh))
            {
                return (null, "sign in");
            }
            if (cred.RefreshExpires != null && cred.RefreshExpires < DateTimeOffset.UtcNow)
            {
                PluginLog.Warning("usage: Claude refresh token has expired - sign in to Claude Code again");
                return (null, "sign in");
            }

            if (!TryLock())
            {
                // Claude Code is renewing right now: give it a moment and use what it wrote.
                await Task.Delay(3000);
                var theirs = ReadCred();
                return theirs != null && theirs.Fresh ? (theirs, null) : (null, "token expired");
            }
            try
            {
                // Under the lock, re-read: another process may have renewed while we were getting here.
                var latest = ReadCred() ?? cred;
                if (!force && latest.Fresh)
                {
                    return (latest, null);
                }
                cred = latest;

                var body = new JsonObject
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = cred.Refresh,
                    ["client_id"] = ClientId,
                    ["scope"] = String.Join(" ", cred.Scopes is { Length: > 0 } ? cred.Scopes : DefaultScopes),
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.UserAgent.ParseAdd("NeonDeck/0.3");
                using var res = await Http.SendAsync(req);
                var status = (Int32)res.StatusCode;
                if (status == 400 || status == 401 || status == 403)
                {
                    PluginLog.Warning($"usage: token renewal rejected (HTTP {status}) - sign in to Claude Code again");
                    return (null, "sign in");
                }
                if (!res.IsSuccessStatusCode)
                {
                    PluginLog.Warning($"usage: token renewal failed (HTTP {status})");
                    return (null, $"HTTP {status}");
                }

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var access = Str(root, "access_token");
                if (String.IsNullOrEmpty(access))
                {
                    return (null, "error");
                }
                var now = DateTimeOffset.UtcNow;
                var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetDouble() : 8 * 3600;
                var next = new Cred
                {
                    Access = access,
                    Refresh = Str(root, "refresh_token") ?? cred.Refresh,
                    Expires = now.AddSeconds(expiresIn),
                    RefreshExpires = root.TryGetProperty("refresh_token_expires_in", out var rei) && rei.ValueKind == JsonValueKind.Number
                        ? now.AddSeconds(rei.GetDouble())
                        : cred.RefreshExpires,
                    Scopes = Str(root, "scope") is { Length: > 0 } scope ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) : cred.Scopes,
                };
                WriteCred(next);
                PluginLog.Info($"usage: Claude access token renewed, valid until {next.Expires.ToLocalTime():HH:mm}");
                return (next, null);
            }
            catch (TaskCanceledException)
            {
                return (null, "offline");
            }
            catch (HttpRequestException ex)
            {
                PluginLog.Warning($"usage: token renewal: {ex.Message}");
                return (null, "offline");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage: token renewal failed");
                return (null, "error");
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>Update only the claudeAiOauth fields; everything else in the file (mcpOAuth etc.) is kept as is.</summary>
        private static void WriteCred(Cred c)
        {
            var root = JsonNode.Parse(File.ReadAllText(CredentialsFile)) as JsonObject ?? new JsonObject();
            if (root["claudeAiOauth"] is not JsonObject oauth)
            {
                oauth = new JsonObject();
                root["claudeAiOauth"] = oauth;
            }
            oauth["accessToken"] = c.Access;
            oauth["refreshToken"] = c.Refresh;
            oauth["expiresAt"] = c.Expires.ToUnixTimeMilliseconds();
            if (c.RefreshExpires != null)
            {
                oauth["refreshTokenExpiresAt"] = c.RefreshExpires.Value.ToUnixTimeMilliseconds();
            }
            if (c.Scopes is { Length: > 0 })
            {
                oauth["scopes"] = new JsonArray(c.Scopes.Select(s => (JsonNode)s).ToArray());
            }
            // Atomic replace so a reader never sees a half-written file.
            var tmp = CredentialsFile + ".neondeck-tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, CredentialsFile, overwrite: true);
        }

        private static Boolean TryLock()
        {
            try
            {
                if (Directory.Exists(LockDir))
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(LockDir) < LockStale)
                    {
                        return false;
                    }
                    Directory.Delete(LockDir, recursive: true); // leftover from a crashed renewal
                }
                Directory.CreateDirectory(LockDir);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Unlock()
        {
            try
            {
                Directory.Delete(LockDir, recursive: true);
            }
            catch
            {
            }
        }

        // ---- the usage endpoint ----

        /// <summary>Fills <paramref name="snap"/>; returns the HTTP status, or -1 when the request never got an answer.</summary>
        private static async Task<Int32> GetUsage(String token, UsageSnapshot snap)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.UserAgent.ParseAdd("NeonDeck/0.3");
                using var res = await Http.SendAsync(req);
                var status = (Int32)res.StatusCode;
                if (status == 401 || status == 403)
                {
                    snap.Error = "sign in";
                    return status;
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
                    return status;
                }
                if (!res.IsSuccessStatusCode)
                {
                    snap.Error = $"HTTP {status}";
                    return status;
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
                return status;
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
            return -1;
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
