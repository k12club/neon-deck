namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using Microsoft.Win32;

    // Row 2 - Workspace keys: theme flip, focus mode, pomodoro.

    /// <summary>Toggle Windows dark/light mode. The key always shows the mode you are in.</summary>
    public sealed class ThemeFlipCommand : NeonCommand
    {
        private const String PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, UInt32 msg, UIntPtr wParam, String lParam, UInt32 flags, UInt32 timeout, out UIntPtr result);

        private Boolean _light;

        public ThemeFlipCommand()
            : base("Dark / Light", "Flips Windows between dark and light mode instantly.", "Neon Deck | Workspace")
        {
        }

        protected override Boolean OnLoad()
        {
            this._light = ReadIsLight();
            return base.OnLoad();
        }

        private static Boolean ReadIsLight()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return (k?.GetValue("AppsUseLightTheme") as Int32?) == 1;
            }
            catch
            {
                return false;
            }
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "theme_flip.svg",
            Accent = this._light ? Neon.Amber : Neon.Cyan,
            Title = "Theme",
            Subtitle = this._light ? "☀ Light" : "☾ Dark",
        };

        protected override void Execute(String actionParameter)
        {
            this.RunAsync("Theme flip", async () =>
            {
                var next = ReadIsLight() ? 0 : 1;
                using (var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true))
                {
                    k.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
                    k.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
                }
                // WM_SETTINGCHANGE to every top-level window so apps repaint immediately.
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
                this._light = next == 1;
                this.Refresh();
                await Win.Toast("Theme", this._light ? "☀ Light mode" : "🌙 Dark mode", silent: true);
            });
        }
    }

    /// <summary>Focus mode: mute notification banners + minimise everything except the active window. Press again to leave.</summary>
    public sealed class FocusModeCommand : NeonCommand
    {
        private Boolean _on;

        public FocusModeCommand()
            : base("Focus", "Toggles focus mode: silences notification banners and minimizes every window except the one you are in.", "Neon Deck | Workspace")
        {
        }

        protected override Boolean OnLoad()
        {
            this._on = DeckConfig.GetFlag("focus");
            return base.OnLoad();
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "focus_mode.svg",
            Accent = this._on ? Neon.Magenta : Neon.Cyan,
            Title = "Focus",
            Subtitle = this._on ? "ON" : null,
            Active = this._on,
        };

        protected override void Execute(String actionParameter)
        {
            this.RunAsync("Focus", async () =>
            {
                if (!this._on)
                {
                    await Win.Toast("Focus ON", "Notifications muted. Press again to leave.", silent: true);
                    await Win.Ps(@"
Add-Type -Namespace Win32 -Name Wnd -MemberDefinition @'
[DllImport(""user32.dll"")] public static extern IntPtr GetForegroundWindow();
[DllImport(""user32.dll"")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport(""user32.dll"")] public static extern bool SetForegroundWindow(IntPtr h);
'@
$h = [Win32.Wnd]::GetForegroundWindow()
(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Milliseconds 400
[Win32.Wnd]::ShowWindow($h, 9) | Out-Null
[Win32.Wnd]::SetForegroundWindow($h) | Out-Null
$k = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty $k NOC_GLOBAL_SETTING_TOASTS_ENABLED 0 -Type DWord
");
                    this._on = true;
                }
                else
                {
                    await Win.Ps(@"
$k = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings'
Set-ItemProperty $k NOC_GLOBAL_SETTING_TOASTS_ENABLED 1 -Type DWord
");
                    this._on = false;
                    await Task.Delay(300);
                    _ = Win.Toast("Focus OFF", "Notifications back on.", silent: true);
                }
                DeckConfig.SetFlag("focus", this._on);
                this.Refresh();
            });
        }
    }

    /// <summary>Pomodoro with a live countdown and progress ring on the key. Press to start, press again to cancel.</summary>
    public sealed class PomodoroCommand : NeonCommand
    {
        private DateTime _endsAt = DateTime.MinValue;
        private Int32 _totalSeconds;
        private Boolean _finishedFlash;

        public PomodoroCommand()
            : base("Pomodoro", "Focus timer with a live countdown on the key. Press to start, press again to cancel.", "Neon Deck | Workspace")
        {
        }

        private Boolean Running => this._endsAt > DateTime.UtcNow;

        protected override Boolean OnLoad()
        {
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
            if (this._endsAt == DateTime.MinValue)
            {
                return;
            }
            if (this.Running)
            {
                this.Refresh();
                return;
            }
            // just finished
            this._endsAt = DateTime.MinValue;
            this._finishedFlash = true;
            this.Refresh();
            _ = Win.Toast("🍅 Pomodoro done", $"{this._totalSeconds / 60} minutes are up - take a break.");
            _ = Task.Delay(15_000).ContinueWith(__ => { this._finishedFlash = false; this.Refresh(); });
        }

        protected override Neon.Face BuildFace()
        {
            var minutes = Math.Max(1, DeckConfig.Current.PomodoroMinutes);
            if (this.Running)
            {
                var left = this._endsAt - DateTime.UtcNow;
                var frac = this._totalSeconds > 0 ? 1.0 - left.TotalSeconds / this._totalSeconds : 0;
                return new Neon.Face
                {
                    Icon = "pomodoro.svg",
                    Accent = left.TotalMinutes < 5 ? Neon.Red : Neon.Magenta,
                    Title = "Pomodoro",
                    Subtitle = $"{(Int32)left.TotalMinutes:00}:{left.Seconds:00}",
                    Progress = frac,
                    Active = true,
                };
            }
            return new Neon.Face
            {
                Icon = "pomodoro.svg",
                Accent = this._finishedFlash ? Neon.Lime : Neon.Magenta,
                Title = "Pomodoro",
                Subtitle = this._finishedFlash ? "done ✓" : $"{minutes:00}:00",
                Active = this._finishedFlash,
            };
        }

        protected override void Execute(String actionParameter)
        {
            if (this.Running)
            {
                var left = this._endsAt - DateTime.UtcNow;
                this._endsAt = DateTime.MinValue;
                this.Refresh();
                _ = Win.Toast("Pomodoro cancelled", $"{(Int32)Math.Round(left.TotalMinutes)} min were left.", silent: true);
                return;
            }
            var minutes = Math.Max(1, DeckConfig.Current.PomodoroMinutes);
            this._totalSeconds = minutes * 60;
            this._endsAt = DateTime.UtcNow.AddSeconds(this._totalSeconds);
            this._finishedFlash = false;
            this.Refresh();
            var ends = this._endsAt.ToLocalTime().ToString("HH:mm");
            _ = Win.Toast("🍅 Pomodoro started", $"{minutes} min · ends at {ends}. Press again to cancel.", silent: true);
        }
    }
}
