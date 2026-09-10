namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Neon Deck - AI-native starter deck for the MX Creative Keypad.
    /// Owns a shared 1 s ticker that live actions (Pomodoro, System Pulse) subscribe to.
    /// </summary>
    public class NeonDeckPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        private Timer _ticker;
        private Int32 _tick;

        /// <summary>Raised once per second from a background thread. Handlers must be cheap.</summary>
        public event Action<Int32> Tick;

        public static NeonDeckPlugin Instance { get; private set; }

        public NeonDeckPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
            Instance = this;
        }

        private static readonly String LifecycleLog = Path.Combine(DeckConfig.DataDirectory, "lifecycle.log");
        private readonly String _instanceId = Guid.NewGuid().ToString("N").Substring(0, 6);

        private void Lifecycle(String what)
        {
            try
            {
                Directory.CreateDirectory(DeckConfig.DataDirectory);
                File.AppendAllText(LifecycleLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} inst={this._instanceId} {what}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        public override void Load()
        {
            DeckConfig.Load();
            this.Lifecycle("Load");
            PluginLog.Info($"Neon Deck loading. projectPath={DeckConfig.Current.ProjectPath} pomodoro={DeckConfig.Current.PomodoroMinutes}m snapshots={DeckConfig.Current.DebugSnapshots}");

            this._ticker = new Timer(_ => this.OnTick(), null, 1000, 1000);

            if (DeckConfig.Current.BootSplash)
            {
                // the nine keys as one screen: Clawd walks across the panel, then the keys fade in
                _ = System.Threading.Tasks.Task.Delay(1200).ContinueWith(_ => Panel.Play(new SplashScene()));
            }

            if (DeckConfig.Current.DebugSnapshots)
            {
                this.StartTriggerWatcher();

                // Pre-render every face so snapshots\*.png exist even before keys are assigned on the device.
                _ = System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                {
                    try
                    {
                        NeonCommand[] all;
                        lock (NeonCommand.All)
                        {
                            all = NeonCommand.All.ToArray();
                        }
                        foreach (var cmd in all)
                        {
                            cmd.RenderForSnapshot(PluginImageSize.Width116);
                            cmd.RenderForSnapshot(PluginImageSize.Width90);
                        }
                        PluginLog.Info($"Rendered {all.Length} faces into {this.SnapshotDirectory}. Fonts: {String.Join(", ", BitmapFonts.GetFontNames())}");
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning(ex, "snapshot render failed");
                    }
                });
            }
        }

        public override void Unload()
        {
            this.Lifecycle("Unload");
            Panel.Stop(refresh: false);
            this._ticker?.Dispose();
            this._ticker = null;
            this._trigger?.Dispose();
            this._trigger = null;
        }

        // ---- dev harness: drop a file named trigger.txt containing an action class name (e.g. "PomodoroCommand")
        //      into %LOCALAPPDATA%\NeonDeck and the plugin runs that key and re-renders every face snapshot. ----
        private FileSystemWatcher _trigger;

        private void StartTriggerWatcher()
        {
            try
            {
                Directory.CreateDirectory(DeckConfig.DataDirectory);
                this._trigger = new FileSystemWatcher(DeckConfig.DataDirectory, "trigger.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler handler = (_, __) => this.HandleTrigger();
                this._trigger.Created += handler;
                this._trigger.Changed += handler;
                this._trigger.Renamed += (_, __) => this.HandleTrigger();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "trigger watcher failed");
            }
        }

        private void HandleTrigger()
        {
            try
            {
                Thread.Sleep(150); // let the writer finish
                var path = Path.Combine(DeckConfig.DataDirectory, "trigger.txt");
                if (!File.Exists(path))
                {
                    return;
                }
                var name = File.ReadAllText(path).Trim();
                File.Delete(path);
                NeonCommand[] all;
                lock (NeonCommand.All)
                {
                    all = NeonCommand.All.ToArray();
                }
                if (name.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var c in all) { c.RenderForSnapshot(PluginImageSize.Width116); }
                    PluginLog.Info("trigger: snapshots re-rendered");
                    return;
                }
                if (name.Equals("splash", StringComparison.OrdinalIgnoreCase))
                {
                    Panel.Play(new SplashScene());
                    return;
                }
                if (name.Equals("alert", StringComparison.OrdinalIgnoreCase))
                {
                    Panel.Play(new AlertScene(Neon.Red));
                    return;
                }
                var cmd = Array.Find(all, c => c.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (cmd == null)
                {
                    PluginLog.Warning($"trigger: no action named '{name}'");
                    return;
                }
                PluginLog.Info($"trigger: running {cmd.GetType().Name}");
                cmd.TryRunCommand(null);
                Thread.Sleep(400);
                cmd.RenderForSnapshot(PluginImageSize.Width116);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "trigger failed");
            }
        }

        private void OnTick()
        {
            var handlers = this.Tick;
            if (handlers == null)
            {
                return;
            }

            this._tick++;
            foreach (Action<Int32> h in handlers.GetInvocationList())
            {
                try
                {
                    h(this._tick);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "tick handler failed");
                }
            }
        }

        /// <summary>Folder for key-face snapshots (debug aid so the faces can be inspected without the device).</summary>
        public String SnapshotDirectory
        {
            get
            {
                var dir = Path.Combine(DeckConfig.DataDirectory, "snapshots");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}
