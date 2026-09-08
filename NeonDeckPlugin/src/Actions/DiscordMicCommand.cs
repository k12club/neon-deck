namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Discord mic on a key. Presses Discord's own Mute button through UI Automation (no keybinds, no focus change),
    /// so Discord shows its red mic icon to everyone and the face mirrors Discord's real state (re-read every 3 s).
    /// When Discord is not running the key falls back to muting the Windows microphone (Core Audio).
    /// </summary>
    public sealed class DiscordMicCommand : NeonCommand
    {
        private Boolean? _discordMuted;   // null = Discord state unknown (not running / in tray / button not exposed)
        private Boolean _discordRunning;
        private Boolean? _winMuted;
        private Boolean _working;

        public DiscordMicCommand()
            : base("Discord Mic", "Toggles Discord's own mute button (works without keybinds). Falls back to the Windows microphone when Discord is not running.", "Neon Deck | Workspace")
        {
        }

        protected override Boolean OnLoad()
        {
            this.Sample();
            this.Deck.Tick += this.OnTick;
            return base.OnLoad();
        }

        protected override Boolean OnUnload()
        {
            if (this.Deck != null)
            {
                this.Deck.Tick -= this.OnTick;
            }
            return base.OnUnload();
        }

        private void OnTick(Int32 tick)
        {
            if (tick % 3 != 0 || this._working)
            {
                return;
            }
            var (a, b, c) = (this._discordRunning, this._discordMuted, this._winMuted);
            this.Sample();
            if (a != this._discordRunning || b != this._discordMuted || c != this._winMuted)
            {
                this.Refresh();
            }
        }

        private void Sample()
        {
            try
            {
                this._discordRunning = DiscordUia.IsRunning();
                this._discordMuted = this._discordRunning ? DiscordUia.IsMuted() : null;
                if (!this._discordRunning)
                {
                    this._winMuted = MicControl.IsMuted();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "mic: sample failed");
            }
        }

        protected override Neon.Face BuildFace()
        {
            if (this._discordRunning)
            {
                if (this._discordMuted == null)
                {
                    return new Neon.Face { Icon = "discord_mic_on.svg", Accent = Neon.Dim, Title = "Discord", Subtitle = "in tray", Busy = this._working };
                }
                return this._discordMuted.Value
                    ? new Neon.Face { Icon = "discord_mic_off.svg", Accent = Neon.Red, Title = "Discord", Subtitle = "MUTED", Active = true, Busy = this._working }
                    : new Neon.Face { Icon = "discord_mic_on.svg", Accent = Neon.Blurple, Title = "Discord", Subtitle = "live", Busy = this._working };
            }
            if (this._winMuted == null)
            {
                return new Neon.Face { Icon = "discord_mic_on.svg", Accent = Neon.Dim, Title = "Mic", Subtitle = "no mic" };
            }
            return this._winMuted.Value
                ? new Neon.Face { Icon = "discord_mic_off.svg", Accent = Neon.Red, Title = "Mic", Subtitle = "MUTED", Active = true, Busy = this._working }
                : new Neon.Face { Icon = "discord_mic_on.svg", Accent = Neon.Cyan, Title = "Mic", Subtitle = "live", Busy = this._working };
        }

        protected override void Execute(String actionParameter)
        {
            if (this._working)
            {
                return;
            }
            this._working = true;
            this.Refresh();
            this.RunAsync("Discord Mic", async () =>
            {
                if (DiscordUia.IsRunning())
                {
                    var after = DiscordUia.ToggleMute();
                    if (after == null)
                    {
                        this._working = false;
                        this.Sample();
                        this.Refresh();
                        await Win.Toast("Discord Mic", "Could not reach Discord's mute button. If Discord is hidden in the tray, open its window once.", silent: true);
                        return;
                    }
                    this._discordRunning = true;
                    this._discordMuted = after;
                }
                else
                {
                    var current = MicControl.IsMuted();
                    if (current == null)
                    {
                        this._working = false;
                        this.Refresh();
                        await Win.Toast("Mic", "No microphone found.", silent: true);
                        return;
                    }
                    this._winMuted = MicControl.SetMuted(!current.Value);
                    PluginLog.Info($"mic: windows muted -> {this._winMuted}");
                }
                this._working = false;
                this.Refresh();
            }, () => { this._working = false; this.Refresh(); });
        }
    }
}
