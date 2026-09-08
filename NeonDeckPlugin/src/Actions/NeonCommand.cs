namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Base for every Neon Deck key: renders its face through <see cref="Neon"/> and runs work off the service thread.
    /// </summary>
    public abstract class NeonCommand : PluginDynamicCommand
    {
        /// <summary>Every live instance, so the plugin can pre-render snapshot PNGs of all faces.</summary>
        public static readonly List<NeonCommand> All = new();

        protected NeonCommand(String displayName, String description, String groupName)
            : base(displayName: displayName, description: description, groupName: groupName)
        {
            this.AutoRepeatOnLongPress = false;
            lock (All)
            {
                All.Add(this);
            }
        }

        /// <summary>Describe the current face. Called on every redraw, so keep it cheap.</summary>
        protected abstract Neon.Face BuildFace();

        /// <summary>The key's action. Runs once per physical press (see <see cref="RunCommand"/>).</summary>
        protected abstract void Execute(String actionParameter);

        private DateTime _lastRun = DateTime.MinValue;
        private const Int32 DebounceMs = 350;

        // The keypad delivered RunCommand twice per press (press + release ~200 ms apart), which made every toggle
        // key undo itself. Only the Press edge counts, and anything within the debounce window is ignored.
        protected sealed override void RunCommand(String actionParameter)
        {
            var now = DateTime.UtcNow;
            if ((now - this._lastRun).TotalMilliseconds < DebounceMs)
            {
                PluginLog.Verbose($"{this.GetType().Name}: duplicate RunCommand ignored");
                return;
            }
            this._lastRun = now;
            this.Execute(actionParameter);
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            PluginLog.Verbose($"{this.GetType().Name}: button {buttonEvent.EventType} ({buttonEvent.PressDuration} ms)");
            if (buttonEvent.EventType == DeviceButtonEventType.Press)
            {
                return base.ProcessButtonEvent2(actionParameter, buttonEvent);
            }
            // Release / LongPress / RepeatPress: swallow so they never turn into a second RunCommand.
            return true;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var size = imageSize == PluginImageSize.None ? PluginImageSize.Width90 : imageSize;
            return Neon.Draw(size, this.BuildFace());
        }

        // The face already carries its title; keep the service from stacking a second label on top.
        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected void Refresh() => this.ActionImageChanged();

        protected NeonDeckPlugin Deck => this.Plugin as NeonDeckPlugin ?? NeonDeckPlugin.Instance;

        /// <summary>Run <paramref name="work"/> in the background; log + toast on failure.</summary>
        protected void RunAsync(String what, Func<Task> work, Action onError = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"{what} failed");
                    onError?.Invoke();
                    await Win.Toast($"{what} failed", Win.Clip(ex.Message, 160));
                }
            });
        }

        /// <summary>Render this key's face at the given size (debug aid; snapshots are written by <see cref="Neon.Draw"/>).</summary>
        public void RenderForSnapshot(PluginImageSize size)
        {
            BitmapImage img = null;
            this.TryGetCommandImage(null, size, out img);
            img?.Dispose();
        }
    }
}
