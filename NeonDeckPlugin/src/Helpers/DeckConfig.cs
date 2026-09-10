namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Deck settings shared with the JavaScript edition: %LOCALAPPDATA%\NeonDeck\deck.config.json
    /// </summary>
    public sealed class DeckConfig
    {
        [JsonPropertyName("projectPath")]
        public String ProjectPath { get; set; } = @"E:\projects\mx-keypad";

        [JsonPropertyName("pomodoroMinutes")]
        public Int32 PomodoroMinutes { get; set; } = 25;

        /// <summary>Claude model for the AI keys. Empty = CLI default. "haiku" is fastest.</summary>
        [JsonPropertyName("claudeModel")]
        public String ClaudeModel { get; set; } = "";

        /// <summary>Neutral working dir for `claude -p` so project instructions do not leak in.</summary>
        [JsonPropertyName("claudeCwd")]
        public String ClaudeCwd { get; set; } = DataDirectory;

        /// <summary>
        /// Optional global Discord keybind for "Toggle Mute" in SendKeys syntax (e.g. "^%m" = Ctrl+Alt+M).
        /// Set the same keybind in Discord → Settings → Keybinds. Empty = activate the Discord window and use Ctrl+Shift+M.
        /// </summary>
        [JsonPropertyName("discordMuteHotkey")]
        public String DiscordMuteHotkey { get; set; } = "";

        /// <summary>Play the boot splash (Clawd walks across all nine keys) when the plugin loads.</summary>
        [JsonPropertyName("bootSplash")]
        public Boolean BootSplash { get; set; } = true;

        /// <summary>Path to codex.exe for the live Codex quota. Empty = auto-detect (Codex installer folder, PATH, npm global).</summary>
        [JsonPropertyName("codexExe")]
        public String CodexExe { get; set; } = "";

        /// <summary>Write every rendered key face to snapshots\*.png (debug aid).</summary>
        [JsonPropertyName("debugSnapshots")]
        public Boolean DebugSnapshots { get; set; } = false;

        public static String DataDirectory =>
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NeonDeck");

        public static String ConfigFile => Path.Combine(DataDirectory, "deck.config.json");
        public static String StateFile => Path.Combine(DataDirectory, "state.json");

        public static DeckConfig Current { get; private set; } = new DeckConfig();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (File.Exists(ConfigFile))
                {
                    Current = JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(ConfigFile), JsonOptions) ?? new DeckConfig();
                    // Persist any keys added since the file was written (e.g. debugSnapshots).
                    File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Current, JsonOptions));
                }
                else
                {
                    Current = new DeckConfig();
                    File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Current, JsonOptions));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "config load failed, using defaults");
                Current = new DeckConfig();
            }
        }

        // ---- tiny persisted state (focus mode flag etc.) ----

        public static Boolean GetFlag(String key)
        {
            try
            {
                if (!File.Exists(StateFile))
                {
                    return false;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
                return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }

        public static void SetFlag(String key, Boolean value)
        {
            try
            {
                var dict = new Dictionary<String, Object>();
                if (File.Exists(StateFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        dict[p.Name] = p.Value.ValueKind == JsonValueKind.True ? true : p.Value.ValueKind == JsonValueKind.False ? false : (Object)p.Value.ToString();
                    }
                }
                dict[key] = value;
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(dict, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "state write failed");
            }
        }
    }
}
