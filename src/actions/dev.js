// Row 3 - Dev keys: cockpit, Claude Code, system pulse.
import { CommandAction } from '@logitech/plugin-sdk';
import { ps, toast, launch } from '../lib/win.js';
import { config } from '../config.js';

const GROUP = 'Neon Deck / Dev';

/** One press: VS Code + Windows Terminal, both in the configured project. */
export class DevCockpitAction extends CommandAction {
  name = 'dev_cockpit';
  displayName = 'Dev Cockpit';
  description = 'Opens VS Code and Windows Terminal in your project folder in one press.';
  groupName = GROUP;

  async onKeyDown() {
    const dir = config.projectPath;
    launch(`code "${dir}"`, { cwd: dir });
    launch(`wt -d "${dir}"`, { cwd: dir });
    await toast('Dev Cockpit', dir, { silent: true });
  }
}

/** Summon Claude Code in a terminal at the project. */
export class ClaudeCodeHereAction extends CommandAction {
  name = 'claude_code_here';
  displayName = 'Claude Code';
  description = 'Opens Windows Terminal and starts Claude Code in your project folder.';
  groupName = GROUP;

  async onKeyDown() {
    const dir = config.projectPath;
    launch(`wt -d "${dir}" cmd /k claude`, { cwd: dir });
  }
}

/** Instant system dashboard as a toast. */
export class SysPulseAction extends CommandAction {
  name = 'sys_pulse';
  displayName = 'System Pulse';
  description = 'Shows CPU, RAM, disk, uptime and IP in a single notification.';
  groupName = GROUP;

  async onKeyDown() {
    try {
      const info = await ps(`
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
$ramUsed = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 1)
$ramTot = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
$up = (Get-Date) - $os.LastBootUpTime
$disks = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object { "$($_.DeviceID) $([math]::Round($_.FreeSpace/1GB))GB free" }
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in 'Dhcp','Manual' -and $_.IPAddress -notlike '169.*' -and $_.InterfaceAlias -notmatch 'Loopback|vEthernet|WSL' } | Select-Object -First 1).IPAddress
$bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
$batTxt = if ($bat) { " · 🔋 $($bat.EstimatedChargeRemaining)%" } else { "" }
"CPU $cpu% · RAM $ramUsed/$ramTot GB$batTxt"
"$($disks -join ' · ')"
"Up $([int]$up.TotalHours)h $($up.Minutes)m · IP $ip"
`);
      const [l1, ...rest] = info.split(/\r?\n/);
      await toast(l1, rest.join('\n'), { silent: true });
    } catch (e) {
      await toast('System Pulse failed', e.message.slice(0, 160));
    }
  }
}
