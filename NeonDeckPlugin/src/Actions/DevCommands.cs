namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;

    // Row 3 - Dev keys: cockpit, Claude Code, system pulse.

    /// <summary>One press: VS Code + Windows Terminal, both in the configured project.</summary>
    public sealed class DevCockpitCommand : NeonCommand
    {
        private String _flash;

        public DevCockpitCommand()
            : base("Terminal", "Opens Windows Terminal and VS Code in your project folder in one press.", "Neon Deck | Dev")
        {
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "dev_cockpit.svg",
            Accent = Neon.Lime,
            Title = "Terminal",
            Subtitle = this._flash,
            Active = this._flash != null,
        };

        protected override void Execute(String actionParameter)
        {
            var dir = DeckConfig.Current.ProjectPath;
            Win.Launch($"code \"{dir}\"", dir);
            Win.Launch($"wt -d \"{dir}\"", dir);
            this._flash = "launching";
            this.Refresh();
            _ = Task.Delay(3000).ContinueWith(_ => { this._flash = null; this.Refresh(); });
        }
    }

    /// <summary>Summon Claude Code in a terminal at the project. Clawd idles on the key (8 frames, 4 fps).</summary>
    public sealed class ClaudeCodeHereCommand : NeonCommand
    {
        private const Int32 FrameCount = 8;
        private const Int32 FrameMs = 250;

        private String _flash;
        private Int32 _frame;
        private System.Threading.Timer _anim;

        public ClaudeCodeHereCommand()
            : base("Claude Code", "Opens Windows Terminal and starts Claude Code in your project folder.", "Neon Deck | Dev")
        {
        }

        protected override Boolean OnLoad()
        {
            this._anim = new System.Threading.Timer(_ =>
            {
                this._frame = (this._frame + 1) % FrameCount;
                this.Refresh();
            }, null, FrameMs, FrameMs);
            return base.OnLoad();
        }

        protected override Boolean OnUnload()
        {
            this._anim?.Dispose();
            this._anim = null;
            return base.OnUnload();
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = $"clawd_f{this._frame}.svg",
            Accent = Neon.Coral,
            Title = "Claude Code",
            Subtitle = this._flash,
            Active = this._flash != null,
        };

        protected override void Execute(String actionParameter)
        {
            var dir = DeckConfig.Current.ProjectPath;
            Win.Launch($"wt -d \"{dir}\" cmd /k claude", dir);
            this._flash = "summoning";
            this.Refresh();
            _ = Task.Delay(3000).ContinueWith(_ => { this._flash = null; this.Refresh(); });
        }
    }

    /// <summary>Live CPU ring + RAM readout on the key (refreshed every 2 s). Press for a full toast.</summary>
    public sealed class SysPulseCommand : NeonCommand
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public UInt32 Low; public UInt32 High; public UInt64 Value => ((UInt64)this.High << 32) | this.Low; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Boolean GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public UInt32 Length; public UInt32 MemoryLoad; public UInt64 TotalPhys; public UInt64 AvailPhys;
            public UInt64 TotalPageFile; public UInt64 AvailPageFile; public UInt64 TotalVirtual; public UInt64 AvailVirtual; public UInt64 AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Boolean GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        private UInt64 _prevIdle, _prevBusy;
        private Int32 _cpu = -1;
        private Int32 _ram = -1;
        private Double _ramUsedGb, _ramTotalGb;
        private Int32 _gpuTemp = -1;   // °C, -1 = unavailable
        private Int32 _gpuUtil = -1;
        private Int32 _cpuTemp = -1;   // °C from LibreHardwareMonitor (needs its WMI provider), -1 = unavailable
        private Int32 _gpuBusy;
        private static readonly Boolean HasNvidiaSmi = System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

        public SysPulseCommand()
            : base("System Monitor", "Live CPU and RAM on the key. Press for CPU, RAM, disk, uptime and IP in one notification.", "Neon Deck | Dev")
        {
        }

        protected override Boolean OnLoad()
        {
            this.Sample();
            _ = this.SampleGpuAsync();
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
            if (tick % 10 == 0)
            {
                _ = this.SampleGpuAsync();
            }
            if (tick % 2 != 0)
            {
                return;
            }
            var (cpu, ram) = (this._cpu, this._ram);
            this.Sample();
            if (Math.Abs(cpu - this._cpu) >= 2 || ram != this._ram)
            {
                this.Refresh();
            }
        }

        private async Task SampleGpuAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref this._gpuBusy, 1) == 1)
            {
                return;
            }
            try
            {
                // LibreHardwareMonitor (running with its Remote Web Server on localhost:8085) gives CPU and GPU temperatures.
                var temps = await LhmTemps.ReadAsync();
                if (temps.HasValue)
                {
                    var (ct, gt) = temps.Value;
                    var changed = ct != this._cpuTemp || (gt > 0 && gt != this._gpuTemp);
                    this._cpuTemp = ct;
                    if (gt > 0)
                    {
                        this._gpuTemp = gt;
                    }
                    if (changed)
                    {
                        this.Refresh();
                    }
                }
                else if (this._cpuTemp >= 0)
                {
                    this._cpuTemp = -1; // LibreHardwareMonitor stopped
                    this.Refresh();
                }
                if (!HasNvidiaSmi || this._cpuTemp >= 0 && this._gpuTemp >= 0)
                {
                    return; // LibreHardwareMonitor already supplied both, or no NVIDIA tool
                }
                var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                    "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
                var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                await p.WaitForExitAsync();
                var parts = line.Split(',');
                if (parts.Length >= 2 && Int32.TryParse(parts[0].Trim(), out var t) && Int32.TryParse(parts[1].Trim(), out var u))
                {
                    var changed = t != this._gpuTemp || Math.Abs(u - this._gpuUtil) >= 5;
                    this._gpuTemp = t;
                    this._gpuUtil = u;
                    if (changed)
                    {
                        this.Refresh();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"gpu: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref this._gpuBusy, 0);
            }
        }

        private void Sample()
        {
            try
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    var busy = kernel.Value + user.Value; // kernel time includes idle
                    var dIdle = idle.Value - this._prevIdle;
                    var dBusy = busy - this._prevBusy;
                    if (this._prevBusy != 0 && dBusy > 0)
                    {
                        this._cpu = (Int32)Math.Clamp(100.0 * (dBusy - dIdle) / dBusy, 0, 100);
                    }
                    this._prevIdle = idle.Value;
                    this._prevBusy = busy;
                }
                var m = new MEMORYSTATUSEX { Length = (UInt32)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m))
                {
                    this._ram = (Int32)m.MemoryLoad;
                    this._ramTotalGb = m.TotalPhys / 1073741824.0;
                    this._ramUsedGb = (m.TotalPhys - m.AvailPhys) / 1073741824.0;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "sample failed");
            }
        }

        private String TempLabel()
        {
            if (this._cpuTemp >= 0)
            {
                return $"CPU {this._cpuTemp}°";
            }
            if (this._gpuTemp >= 0)
            {
                return $"GPU {this._gpuTemp}°";
            }
            return HasNvidiaSmi ? "…" : null;
        }

        private String TempLabel2() => this._cpuTemp >= 0 && this._gpuTemp >= 0 ? $"GPU {this._gpuTemp}°" : null;

        protected override Neon.Face BuildFace()
        {
            var cpu = Math.Max(0, this._cpu);
            return new Neon.Face
            {
                Icon = "sys_pulse.svg",
                Accent = cpu >= 85 ? Neon.Red : cpu >= 55 ? Neon.Amber : Neon.Cyan,
                Title = "Monitor",
                Subtitle = this._cpu < 0 ? "…" : $"{cpu}%  ·  {this._ram}%",
                Progress = cpu / 100.0,
                // temperature in the top band gives this key the same ring layout as the quota keys above it
                Corner = TempLabel(),
                Corner2 = TempLabel2(),
            };
        }

        protected override void Execute(String actionParameter)
        {
            this.RunAsync("System Pulse", async () =>
            {
                var info = await Win.Ps(@"
$os = Get-CimInstance Win32_OperatingSystem
$up = (Get-Date) - $os.LastBootUpTime
$disks = Get-CimInstance Win32_LogicalDisk -Filter ""DriveType=3"" | ForEach-Object { ""$($_.DeviceID) $([math]::Round($_.FreeSpace/1GB))GB free"" }
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in 'Dhcp','Manual' -and $_.IPAddress -notlike '169.*' -and $_.InterfaceAlias -notmatch 'Loopback|vEthernet|WSL' } | Select-Object -First 1).IPAddress
$bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
$batTxt = if ($bat) { "" · 🔋 $($bat.EstimatedChargeRemaining)%"" } else { """" }
""$($disks -join ' · ')$batTxt""
""Up $([int]$up.TotalHours)h $($up.Minutes)m · IP $ip""
");
                var lines = info.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var gpu = this._gpuTemp >= 0 ? $" · GPU {(this._gpuUtil >= 0 ? this._gpuUtil + "% " : "")}{this._gpuTemp}°C" : "";
                var cpuT = this._cpuTemp >= 0 ? $" · CPU {this._cpuTemp}°C" : " · CPU temp: start LibreHardwareMonitor";
                var title = $"CPU {Math.Max(0, this._cpu)}%{cpuT} · RAM {this._ramUsedGb:0.0}/{this._ramTotalGb:0.0} GB ({this._ram}%){gpu}";
                await Win.Toast(title, String.Join("\n", lines).Trim(), silent: true);
            });
        }
    }
}
