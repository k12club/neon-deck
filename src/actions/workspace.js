// Row 2 - Workspace keys: theme flip, focus mode, pomodoro.
import { CommandAction } from '@logitech/plugin-sdk';
import { ps, toast } from '../lib/win.js';
import { config, readState, writeState } from '../config.js';

const GROUP = 'Neon Deck / Workspace';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Toggle Windows dark/light mode for apps + system, and broadcast the change. */
export class ThemeFlipAction extends CommandAction {
  name = 'theme_flip';
  displayName = 'Dark / Light';
  description = 'Flips Windows between dark and light mode instantly.';
  groupName = GROUP;

  async onKeyDown() {
    try {
      const mode = await ps(`
$k = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize'
$cur = (Get-ItemProperty $k).AppsUseLightTheme
$next = if ($cur -eq 1) { 0 } else { 1 }
Set-ItemProperty $k AppsUseLightTheme $next -Type DWord
Set-ItemProperty $k SystemUsesLightTheme $next -Type DWord
Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
[UIntPtr]$r = [UIntPtr]::Zero
[Win32.Native]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'ImmersiveColorSet', 2, 1000, [ref]$r) | Out-Null
if ($next -eq 1) { 'light' } else { 'dark' }
`);
      await toast('Theme', mode === 'light' ? '☀ Light mode' : '🌙 Dark mode', { silent: true });
    } catch (e) {
      await toast('Theme flip failed', e.message.slice(0, 160));
    }
  }
}

/** Focus mode: mute notification banners + minimize everything except the active window. Press again to leave. */
export class FocusModeAction extends CommandAction {
  name = 'focus_mode';
  displayName = 'Focus';
  description = 'Toggles focus mode: silences notification banners and minimizes every window except the one you are in.';
  groupName = GROUP;

  async onKeyDown() {
    const { focus = false } = readState();
    try {
      if (!focus) {
        await toast('Focus ON', 'Notifications muted. Press again to leave.', { silent: true });
        await ps(`
Add-Type -Namespace Win32 -Name Wnd -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
'@
$h = [Win32.Wnd]::GetForegroundWindow()
(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Milliseconds 400
[Win32.Wnd]::ShowWindow($h, 9) | Out-Null
[Win32.Wnd]::SetForegroundWindow($h) | Out-Null
$k = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty $k NOC_GLOBAL_SETTING_TOASTS_ENABLED 0 -Type DWord
`);
        writeState({ focus: true });
      } else {
        await ps(`
$k = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings'
Set-ItemProperty $k NOC_GLOBAL_SETTING_TOASTS_ENABLED 1 -Type DWord
`);
        writeState({ focus: false });
        await sleep(300);
        await toast('Focus OFF', 'Notifications back on.', { silent: true });
      }
    } catch (e) {
      await toast('Focus failed', e.message.slice(0, 160));
    }
  }
}

/** Pomodoro: press to start a timer, press again to cancel. Toast when done. */
export class PomodoroAction extends CommandAction {
  name = 'pomodoro';
  displayName = 'Pomodoro';
  description = `Starts a ${config.pomodoroMinutes}-minute focus timer with a notification when it ends. Press again to cancel.`;
  groupName = GROUP;
  #timer = null;
  #endsAt = 0;

  async onKeyDown() {
    if (this.#timer) {
      clearTimeout(this.#timer);
      this.#timer = null;
      const left = Math.max(0, Math.round((this.#endsAt - Date.now()) / 60000));
      return toast('Pomodoro cancelled', `${left} min were left.`, { silent: true });
    }
    const minutes = Number(config.pomodoroMinutes) || 25;
    this.#endsAt = Date.now() + minutes * 60_000;
    this.#timer = setTimeout(() => {
      this.#timer = null;
      toast('🍅 Pomodoro done', `${minutes} minutes are up - take a break.`);
    }, minutes * 60_000);
    const ends = new Date(this.#endsAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    await toast('🍅 Pomodoro started', `${minutes} min · ends at ${ends}. Press again to cancel.`, { silent: true });
  }
}
