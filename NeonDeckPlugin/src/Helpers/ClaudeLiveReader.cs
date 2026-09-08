namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    /// <summary>What Claude Code sessions on this PC are doing right now, as recorded by ~/.claude/neon-claude-hook.js.</summary>
    public sealed class ClaudeLiveState
    {
        public enum Kind { Off, Ready, Working, Done, NeedsInput }

        public Kind State = Kind.Off;
        public Int32 Sessions;                 // sessions seen in the last few hours
        public String Message;                 // permission / question text when NeedsInput
        public String Cwd;                     // cwd of the session that decided the state
        public DateTimeOffset UpdatedAt;
        public Int32? ContextPercent;          // from the status-line mirror (latest session)
        public String Model;
    }

    internal static class ClaudeLiveReader
    {
        private static readonly String LiveFile = Path.Combine(DeckConfig.DataDirectory, "claude-live.json");
        private static readonly String StatusMirror = Path.Combine(DeckConfig.DataDirectory, "claude-status.json");

        /// <summary>How long a "done" stays green before it becomes plain "ready".</summary>
        public static readonly TimeSpan DoneGlow = TimeSpan.FromSeconds(90);
        /// <summary>A session that has not reported for this long is considered gone.</summary>
        private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(3);

        public static DateTime LiveFileWriteTime()
        {
            try
            {
                return File.Exists(LiveFile) ? File.GetLastWriteTimeUtc(LiveFile) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static ClaudeLiveState Read()
        {
            var result = new ClaudeLiveState();
            try
            {
                if (File.Exists(LiveFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(LiveFile));
                    if (doc.RootElement.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Object)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var best = ClaudeLiveState.Kind.Off;
                        var bestWhen = DateTimeOffset.MinValue;
                        foreach (var s in sessions.EnumerateObject())
                        {
                            var v = s.Value;
                            if (!v.TryGetProperty("updatedAt", out var ua) || !DateTimeOffset.TryParse(ua.GetString(), out var when) || now - when > SessionTtl)
                            {
                                continue;
                            }
                            result.Sessions++;
                            var state = v.TryGetProperty("state", out var st) ? st.GetString() : "ready";
                            var kind = state switch
                            {
                                "working" => ClaudeLiveState.Kind.Working,
                                "needs_input" => ClaudeLiveState.Kind.NeedsInput,
                                "done" => now - when <= DoneGlow ? ClaudeLiveState.Kind.Done : ClaudeLiveState.Kind.Ready,
                                _ => ClaudeLiveState.Kind.Ready,
                            };
                            // priority: needs input > working > done > ready; ties broken by recency
                            if (Rank(kind) > Rank(best) || (Rank(kind) == Rank(best) && when > bestWhen))
                            {
                                best = kind;
                                bestWhen = when;
                                result.Message = v.TryGetProperty("message", out var m) ? m.GetString() : null;
                                result.Cwd = v.TryGetProperty("cwd", out var c) ? c.GetString() : null;
                            }
                        }
                        result.State = best;
                        result.UpdatedAt = bestWhen;
                    }
                }
                if (File.Exists(StatusMirror))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatusMirror));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("context_used_percentage", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
                    {
                        result.ContextPercent = (Int32)Math.Round(ctx.GetDouble());
                    }
                    if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                    {
                        result.Model = model.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "claude live: read failed");
            }
            return result;
        }

        private static Int32 Rank(ClaudeLiveState.Kind k) => k switch
        {
            ClaudeLiveState.Kind.NeedsInput => 4,
            ClaudeLiveState.Kind.Working => 3,
            ClaudeLiveState.Kind.Done => 2,
            ClaudeLiveState.Kind.Ready => 1,
            _ => 0,
        };
    }
}
