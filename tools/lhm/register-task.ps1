# Registers LibreHardwareMonitor to start at logon with highest privileges (its sensor driver needs admin),
# then starts it. Run once; it re-launches itself elevated (one UAC prompt).
#   powershell -ExecutionPolicy Bypass -File tools\lhm\register-task.ps1
param(
  [string]$TaskName = 'LibreHardwareMonitor (Neon Deck)',
  [string]$ExePath = ''
)

if (-not $ExePath) {
  $candidates = @(
    (Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter 'LibreHardwareMonitor.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName),
    'C:\Program Files\LibreHardwareMonitor\LibreHardwareMonitor.exe'
  ) | Where-Object { $_ -and (Test-Path $_) }
  $ExePath = $candidates | Select-Object -First 1
}
if (-not $ExePath) { throw 'LibreHardwareMonitor.exe not found. Install it first: winget install LibreHardwareMonitor.LibreHardwareMonitor' }

# copy our config next to the exe if the user has not created one yet (web server on port 8085, minimized to tray)
$cfgSrc = Join-Path $PSScriptRoot 'LibreHardwareMonitor.config'
$cfgDst = Join-Path (Split-Path $ExePath) 'LibreHardwareMonitor.config'
if ((Test-Path $cfgSrc) -and -not (Test-Path $cfgDst)) { Copy-Item $cfgSrc $cfgDst }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host 'Requesting administrator rights to register the logon task...'
  Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-TaskName', "`"$TaskName`"", '-ExePath', "`"$ExePath`""
  exit
}

$action    = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory (Split-Path $ExePath)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Registered and started task '$TaskName' -> $ExePath"
Write-Host 'Sensors: http://localhost:8085/data.json'
