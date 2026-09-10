namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    /// <summary>
    /// The nine keys as one screen.
    ///
    /// The MX Creative Keypad is a single 480x480 LCD under a 3x3 grid of 118 px key windows (first window at
    /// 23,6; next ones every 158 px). While a <see cref="Scene"/> plays, every Neon key shows its own window of one
    /// shared frame instead of its face, so things can move across the whole panel and the bezels simply hide the gaps.
    /// A scene can also tint all keys at once on top of their normal faces (used for the quota alert and the fade-in).
    /// </summary>
    public static class Panel
    {
        public const Int32 Width = 480;
        public const Int32 Height = 480;
        public const Int32 TileSize = 118;
        public const Int32 OriginX = 23;
        public const Int32 OriginY = 6;
        public const Int32 Pitch = 158;

        public static Int32 CellX(Int32 cell) => OriginX + (cell % 3) * Pitch;
        public static Int32 CellY(Int32 cell) => OriginY + (cell / 3) * Pitch;

        public abstract class Scene
        {
            /// <summary>Seconds the scene runs.</summary>
            public abstract Double Duration { get; }

            public virtual Int32 Fps => 8;

            /// <summary>
            /// Draw the whole 480x480 panel at time <paramref name="t"/>. Return false to show the keys' own faces
            /// instead (the <see cref="Overlay"/> still applies).
            /// </summary>
            public abstract Boolean DrawFrame(BitmapBuilder b, Double t);

            /// <summary>Tint laid over every key at time <paramref name="t"/>; alpha 0 = none.</summary>
            public virtual BitmapColor Overlay(Double t) => BitmapColor.Transparent;
        }

        private static readonly Object Gate = new();
        private static Scene _scene;
        private static Timer _timer;
        private static DateTime _started;
        private static BitmapImage _frame;          // current shared frame, or null while the scene shows the faces
        private static BitmapColor _overlay = BitmapColor.Transparent;
        private static Int32 _frameNo;
        private static Int32 _rendering;

        public static Boolean Active => _scene != null;

        /// <summary>Start a scene (replacing any running one).</summary>
        public static void Play(Scene scene)
        {
            lock (Gate)
            {
                StopCore();
                _scene = scene;
                _started = DateTime.UtcNow;
                _frameNo = 0;
                _timer = new Timer(_ => Step(), null, 0, Math.Max(20, 1000 / Math.Max(1, scene.Fps)));
            }
            PluginLog.Info($"panel: playing {scene.GetType().Name} ({scene.Duration:0.0} s @ {scene.Fps} fps)");
        }

        /// <summary>Flash the whole panel in a colour (quota hit, etc.). Ignored while another scene is running.</summary>
        public static void Alert(BitmapColor color, String reason)
        {
            if (Active)
            {
                return;
            }
            PluginLog.Info($"panel: alert - {reason}");
            Play(new AlertScene(color));
        }

        /// <summary>End the running scene; the keys go back to their own faces (unless the plugin is unloading).</summary>
        public static void Stop(Boolean refresh = true)
        {
            lock (Gate)
            {
                StopCore();
            }
            if (refresh)
            {
                RefreshAll();
            }
        }

        private static void StopCore()
        {
            _timer?.Dispose();
            _timer = null;
            _scene = null;
            _frame = null;
            _overlay = BitmapColor.Transparent;
        }

        private static void Step()
        {
            if (Interlocked.Exchange(ref _rendering, 1) == 1)
            {
                return; // previous frame still rendering: drop this tick rather than queue up
            }
            try
            {
                var scene = _scene;
                if (scene == null)
                {
                    return;
                }
                var t = (DateTime.UtcNow - _started).TotalSeconds;
                if (t >= scene.Duration)
                {
                    Stop();
                    return;
                }
                BitmapImage frame = null;
                using (var b = new BitmapBuilder(Width, Height))
                {
                    b.Clear(Neon.Bg);
                    if (scene.DrawFrame(b, t))
                    {
                        frame = b.ToImage();
                    }
                }
                _overlay = scene.Overlay(t);
                _frame = frame;
                _frameNo++;
                SaveDebugFrame(frame, _frameNo);
                RefreshAll();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "panel: frame failed");
            }
            finally
            {
                Interlocked.Exchange(ref _rendering, 0);
            }
        }

        private static void RefreshAll()
        {
            NeonCommand[] all;
            lock (NeonCommand.All)
            {
                all = NeonCommand.All.ToArray();
            }
            foreach (var c in all)
            {
                try
                {
                    c.RefreshFace();
                }
                catch
                {
                    // an action that is on its way out; nothing to show there anyway
                }
            }
        }

        /// <summary>
        /// What <paramref name="key"/> should show right now: its window of the shared frame, or its own face under the
        /// scene's tint. Null when no scene is playing or the key is not on the 3x3 grid (normal face applies).
        /// </summary>
        public static BitmapImage Tile(NeonCommand key, PluginImageSize size, Func<Neon.Face> face)
        {
            if (_scene == null)
            {
                return null;
            }
            var cell = Layout.CellOf(key);
            if (cell < 0)
            {
                return null;
            }
            var frame = _frame;
            var overlay = _overlay;
            if (frame == null)
            {
                return overlay.A == 0 ? null : Neon.Draw(size, face(), overlay);
            }

            using var b = new BitmapBuilder(size);
            var s = b.Width / (Double)TileSize;                   // 116 px keys show a 118 px window
            var panel = (Int32)Math.Round(Width * s);
            b.Clear(Neon.Bg);
            b.DrawImage(frame, -(Int32)Math.Round(CellX(cell) * s), -(Int32)Math.Round(CellY(cell) * s), panel, panel, BitmapRotation.None);
            if (overlay.A > 0)
            {
                b.FillRectangle(0, 0, b.Width, b.Height, overlay);
            }
            var tile = b.ToImage();
            if (DeckConfig.Current.DebugSnapshots && NeonDeckPlugin.Instance != null)
            {
                try
                {
                    tile.SaveToFile(Path.Combine(NeonDeckPlugin.Instance.SnapshotDirectory, $"tile_{cell}_{size}.png"));
                }
                catch
                {
                    // debug aid only
                }
            }
            return tile;
        }

        // ---- debug: dump frames as PNG so the scene can be checked without the device ----

        private static void SaveDebugFrame(BitmapImage frame, Int32 no)
        {
            if (frame == null || !DeckConfig.Current.DebugSnapshots || NeonDeckPlugin.Instance == null)
            {
                return;
            }
            try
            {
                var dir = NeonDeckPlugin.Instance.SnapshotDirectory;
                frame.SaveToFile(Path.Combine(dir, $"panel_{no:00}.png"));

                // "as seen on the device": the frame with the bezels blacked out
                using var b = new BitmapBuilder(Width, Height);
                b.Clear(BitmapColor.Black);
                b.DrawImage(frame, 0, 0, BitmapRotation.None);
                var black = BitmapColor.Black;
                b.FillRectangle(0, 0, Width, OriginY, black);
                b.FillRectangle(0, OriginY + 2 * Pitch + TileSize, Width, Height, black);
                b.FillRectangle(0, 0, OriginX, Height, black);
                b.FillRectangle(OriginX + 2 * Pitch + TileSize, 0, Width, Height, black);
                for (var i = 0; i < 2; i++)
                {
                    b.FillRectangle(OriginX + TileSize + i * Pitch, 0, Pitch - TileSize, Height, black);
                    b.FillRectangle(0, OriginY + TileSize + i * Pitch, Width, Pitch - TileSize, black);
                }
                using var view = b.ToImage();
                view.SaveToFile(Path.Combine(dir, $"panelview_{no:00}.png"));
            }
            catch
            {
                // debug aid only
            }
        }
    }

    /// <summary>
    /// Which key sits in which window of the grid. Read from the Logi profile that holds the Neon keys
    /// (control ids 0..8 are row-major), re-read when the profile changes; falls back to the README layout.
    /// </summary>
    public static class Layout
    {
        private const String ActionPrefix = "$NeonDeck___Loupedeck.NeonDeckPlugin.";

        private static Dictionary<String, Int32> _cells = Default();
        private static String _signature;
        private static DateTime _checked = DateTime.MinValue;

        private static Dictionary<String, Int32> Default() => new()
        {
            ["AskClaudeCommand"] = 0, ["DiscordMicCommand"] = 1, ["PomodoroCommand"] = 2,
            ["ClaudeUsageCommand"] = 3, ["CodexUsageCommand"] = 4, ["ClaudeLiveCommand"] = 5,
            ["DevCockpitCommand"] = 6, ["ClaudeCodeHereCommand"] = 7, ["SysPulseCommand"] = 8,
        };

        /// <summary>Grid cell 0..8 (row-major) of a key, or -1 when it is not on the grid.</summary>
        public static Int32 CellOf(NeonCommand key)
        {
            Refresh();
            return _cells.TryGetValue(key.GetType().Name, out var c) ? c : -1;
        }

        private static String ProfilesRoot =>
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Logi", "LogiPluginService", "Applications");

        private static void Refresh()
        {
            if ((DateTime.UtcNow - _checked).TotalSeconds < 30)
            {
                return;
            }
            _checked = DateTime.UtcNow;
            try
            {
                if (!Directory.Exists(ProfilesRoot))
                {
                    return;
                }
                var files = Directory.EnumerateFiles(ProfilesRoot, "ProfileInfo.json", SearchOption.AllDirectories).ToList();
                var sig = new StringBuilder();
                foreach (var f in files)
                {
                    sig.Append(f).Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks).Append(';');
                }
                var signature = sig.ToString();
                if (signature == _signature)
                {
                    return;
                }
                _signature = signature;

                Dictionary<String, Int32> best = null;
                foreach (var f in files)
                {
                    var page = BestPage(f);
                    if (page != null && (best == null || page.Count > best.Count))
                    {
                        best = page;
                    }
                }
                if (best != null && best.Count > 0)
                {
                    _cells = best;
                    PluginLog.Info($"panel: layout from profile - {String.Join(" ", best.OrderBy(kv => kv.Value).Select(kv => $"{kv.Value}:{kv.Key.Replace("Command", "")}"))}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "panel: layout read failed");
            }
        }

        /// <summary>The page of this profile with the most Neon keys, as class name -> cell.</summary>
        private static Dictionary<String, Int32> BestPage(String file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Dictionary<String, Int32> best = null;
            if (!doc.RootElement.TryGetProperty("layout", out var layout) || !layout.TryGetProperty("layoutModes", out var modes))
            {
                return null;
            }
            foreach (var mode in modes.EnumerateArray())
            {
                if (!mode.TryGetProperty("workspaces", out var workspaces))
                {
                    continue;
                }
                foreach (var ws in workspaces.EnumerateArray())
                {
                    if (!ws.TryGetProperty("pressPages", out var pages))
                    {
                        continue;
                    }
                    foreach (var page in pages.EnumerateArray())
                    {
                        if (!page.TryGetProperty("controls", out var controls))
                        {
                            continue;
                        }
                        var map = new Dictionary<String, Int32>();
                        foreach (var control in controls.EnumerateArray())
                        {
                            if (!control.TryGetProperty("controlId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                            {
                                continue;
                            }
                            var id = idEl.GetInt32();
                            var action = control.TryGetProperty("pressAction", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null;
                            if (id < 0 || id > 8 || action == null || !action.StartsWith(ActionPrefix, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var cls = action.Substring(ActionPrefix.Length);
                            if (!map.ContainsKey(cls))
                            {
                                map[cls] = id;
                            }
                        }
                        if (map.Count > 0 && (best == null || map.Count > best.Count))
                        {
                            best = map;
                        }
                    }
                }
            }
            return best;
        }
    }
}
