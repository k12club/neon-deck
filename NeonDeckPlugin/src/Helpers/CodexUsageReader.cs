namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Codex usage as Codex itself last reported it. Codex CLI / app writes a `token_count` event with `rate_limits`
    /// into every session log under %USERPROFILE%\.codex\sessions; we read the newest one. Local files only, no network,
    /// no tokens touched. The reading is as fresh as the user's last Codex turn.
    /// </summary>
    public sealed class CodexUsage
    {
        public Int32 PrimaryPercent { get; set; }        // 5-hour window
        public Int32 PrimaryWindowMinutes { get; set; }
        public DateTimeOffset? PrimaryResetsAt { get; set; }
        public Int32 SecondaryPercent { get; set; }      // weekly window
        public Int32 SecondaryWindowMinutes { get; set; }
        public DateTimeOffset? SecondaryResetsAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; }   // when Codex logged it
        public String SourceFile { get; set; }
    }

    internal static class CodexUsageReader
    {
        private static String SessionsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

        public static Boolean IsCodexPresent => Directory.Exists(SessionsDir);

        /// <summary>Newest session file by write time, or null.</summary>
        public static FileInfo NewestSessionFile()
        {
            try
            {
                if (!Directory.Exists(SessionsDir))
                {
                    return null;
                }
                return new DirectoryInfo(SessionsDir)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "codex: cannot list sessions");
                return null;
            }
        }

        /// <summary>
        /// Latest rate_limits reading. Looks at the newest few session files (a fresh session may not have
        /// reported limits yet) and returns the most recently observed one.
        /// </summary>
        public static CodexUsage ReadLatest()
        {
            try
            {
                if (!Directory.Exists(SessionsDir))
                {
                    return null;
                }
                var files = new DirectoryInfo(SessionsDir)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(5);
                CodexUsage best = null;
                foreach (var f in files)
                {
                    var u = ReadFile(f);
                    if (u != null && (best == null || u.ObservedAt > best.ObservedAt))
                    {
                        best = u;
                    }
                    if (best != null && f.LastWriteTimeUtc < best.ObservedAt)
                    {
                        break; // older files cannot beat what we already have
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "codex: read failed");
                return null;
            }
        }

        private static CodexUsage ReadFile(FileInfo file)
        {
            // Session logs can be large; scan the tail first (last 256 KB) and fall back to the whole file.
            var text = ReadTail(file, 256 * 1024);
            var u = ParseLast(text, file);
            if (u == null && file.Length > 256 * 1024)
            {
                u = ParseLast(File.ReadAllText(file.FullName), file);
            }
            return u;
        }

        private static String ReadTail(FileInfo file, Int32 maxBytes)
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var start = Math.Max(0, len - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new Byte[len - start];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            var s = Encoding.UTF8.GetString(buf, 0, read);
            if (start > 0)
            {
                var nl = s.IndexOf('\n');
                if (nl >= 0)
                {
                    s = s.Substring(nl + 1); // drop the partial first line
                }
            }
            return s;
        }

        private static CodexUsage ParseLast(String text, FileInfo file)
        {
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryFind(root, "rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    if (!rl.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var u = new CodexUsage { SourceFile = file.Name, ObservedAt = file.LastWriteTimeUtc };
                    if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(ts.GetString(), out var when))
                    {
                        u.ObservedAt = when;
                    }
                    Fill(primary, out var p1, out var w1, out var r1);
                    u.PrimaryPercent = p1;
                    u.PrimaryWindowMinutes = w1;
                    u.PrimaryResetsAt = r1;
                    if (rl.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                    {
                        Fill(secondary, out var p2, out var w2, out var r2);
                        u.SecondaryPercent = p2;
                        u.SecondaryWindowMinutes = w2;
                        u.SecondaryResetsAt = r2;
                    }
                    return u;
                }
                catch (JsonException)
                {
                    // partial line while Codex is writing; try the previous one
                }
            }
            return null;
        }

        private static void Fill(JsonElement w, out Int32 percent, out Int32 windowMinutes, out DateTimeOffset? resetsAt)
        {
            percent = w.TryGetProperty("used_percent", out var p) && p.ValueKind == JsonValueKind.Number ? (Int32)Math.Round(p.GetDouble()) : 0;
            windowMinutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            resetsAt = null;
            if (w.TryGetProperty("resets_at", out var r))
            {
                if (r.ValueKind == JsonValueKind.Number)
                {
                    var v = r.GetDouble();
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds((Int64)(v > 1e12 ? v / 1000 : v));
                }
                else if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dt))
                {
                    resetsAt = dt;
                }
            }
        }

        /// <summary>Depth-first search for a property name anywhere in the document.</summary>
        private static Boolean TryFind(JsonElement el, String name, out JsonElement found)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(name, out found))
                {
                    return true;
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (TryFind(prop.Value, name, out found))
                    {
                        return true;
                    }
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (TryFind(item, name, out found))
                    {
                        return true;
                    }
                }
            }
            found = default;
            return false;
        }
    }
}
