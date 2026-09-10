namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Asks Codex itself for the current rate limits, through the same JSON-RPC interface the Codex IDE extension
    /// uses: `codex app-server` on stdio, `initialize` then `account/rateLimits/read`. Codex takes care of its own
    /// login and token renewal (it reads and updates %USERPROFILE%\.codex\auth.json), no quota is consumed, and
    /// the process is gone again after about two seconds. Nothing is read from or written to the auth file here.
    /// </summary>
    internal static class CodexAppServerClient
    {
        private static String _exe;
        private static Boolean _searched;

        /// <summary>Full path of codex.exe (or a codex.cmd shim), or null when Codex is not installed.</summary>
        public static String CodexExe
        {
            get
            {
                if (!_searched)
                {
                    _exe = Find();
                    _searched = true;
                    PluginLog.Info(_exe == null ? "codex: executable not found - live quota disabled" : $"codex: using {_exe}");
                }
                return _exe;
            }
        }

        private static String Find()
        {
            var candidates = new List<String>();
            var configured = DeckConfig.Current.CodexExe;
            if (!String.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));   // Codex desktop / installer
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), "codex.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "codex.cmd"));
            }
            try
            {
                // npm install -g @openai/codex ships the native binary under vendor\<target>\codex\
                var vendor = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "npm", "node_modules", "@openai", "codex", "vendor");
                if (Directory.Exists(vendor))
                {
                    candidates.AddRange(Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories));
                }
            }
            catch
            {
            }
            return candidates.FirstOrDefault(c => { try { return File.Exists(c); } catch { return false; } });
        }

        /// <summary>Current Codex rate limits, or null when Codex is missing, not logged in, or did not answer in time.</summary>
        public static async Task<CodexUsage> ReadLive(Int32 timeoutMs = 20_000)
        {
            var exe = CodexExe;
            if (exe == null)
            {
                return null;
            }

            var psi = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"\"{exe}\" app-server\"" }
                : new ProcessStartInfo(exe) { Arguments = "app-server" };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = DeckConfig.DataDirectory;

            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi);
            if (p == null)
            {
                PluginLog.Warning("codex: app-server failed to start");
                return null;
            }
            var stderr = p.StandardError.ReadToEndAsync(); // keep the pipe drained
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                await stdin.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"neon-deck\",\"version\":\"0.3\"}}}\n");
                if (await ReadResponse(p.StandardOutput, 1, cts.Token) == null)
                {
                    PluginLog.Warning($"codex: app-server ended before initialize answered: {Win.Clip((await stderr).Trim(), 200)}");
                    return null;
                }
                await stdin.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\"}\n");
                var line = await ReadResponse(p.StandardOutput, 2, cts.Token);
                if (line == null)
                {
                    PluginLog.Warning("codex: app-server ended before rate limits answered");
                    return null;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    PluginLog.Warning($"codex: app-server: {Win.Clip(err.ToString(), 200)}");
                    return null;
                }
                if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("rateLimits", out var rl) || rl.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                var u = new CodexUsage { ObservedAt = DateTimeOffset.UtcNow, SourceFile = "codex app-server", Live = true };
                if (rl.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
                {
                    Fill(primary, out var pct, out var win, out var reset);
                    u.PrimaryPercent = pct;
                    u.PrimaryWindowMinutes = win;
                    u.PrimaryResetsAt = reset;
                }
                if (rl.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                {
                    Fill(secondary, out var pct, out var win, out var reset);
                    u.SecondaryPercent = pct;
                    u.SecondaryWindowMinutes = win;
                    u.SecondaryResetsAt = reset;
                }
                if (rl.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String)
                {
                    u.PlanType = plan.GetString();
                }
                PluginLog.Verbose($"codex: app-server answered in {sw.ElapsedMilliseconds} ms");
                return u;
            }
            catch (OperationCanceledException)
            {
                PluginLog.Warning($"codex: app-server did not answer within {timeoutMs / 1000} s");
                return null;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "codex: app-server read failed");
                return null;
            }
            finally
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>Next line whose "id" is <paramref name="id"/>; notifications (no id) are skipped. Null when the process ends.</summary>
        private static async Task<String> ReadResponse(StreamReader reader, Int32 id, CancellationToken ct)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    return null;
                }
                if (line.Length == 0 || line.IndexOf("\"id\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id)
                    {
                        return line;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        private static void Fill(JsonElement w, out Int32 percent, out Int32 windowMinutes, out DateTimeOffset? resetsAt)
        {
            percent = w.TryGetProperty("usedPercent", out var p) && p.ValueKind == JsonValueKind.Number ? (Int32)Math.Round(p.GetDouble()) : 0;
            windowMinutes = w.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            resetsAt = null;
            if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number)
            {
                var v = r.GetDouble();
                resetsAt = DateTimeOffset.FromUnixTimeSeconds((Int64)(v > 1e12 ? v / 1000 : v));
            }
        }
    }
}
