# diagnose-bc-autostart.ps1
# Read-only diagnostic for "Base Camp doesn't start at Windows boot" reports.
# Collects everything needed to tell apart: service start-type changed,
# stale/moved install path, a crash-on-launch, Fast Startup masking the Run
# key, or a Task Scheduler entry K2's own Run-key check never looks at.
# Writes a timestamped report next to this script; nothing is modified.

$ErrorActionPreference = 'SilentlyContinue'
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $PSScriptRoot "bc-autostart-report-$stamp.txt"

function Section($title) {
    "`r`n===== $title =====" | Out-File -FilePath $report -Append -Encoding utf8
}
function Line($text) {
    $text | Out-File -FilePath $report -Append -Encoding utf8
}

"BC autostart diagnostic - $(Get-Date)" | Out-File -FilePath $report -Encoding utf8

Section "Last boot time"
(Get-CimInstance Win32_OperatingSystem).LastBootUpTime | Out-String | Out-File -FilePath $report -Append -Encoding utf8

Section "BaseCampService - configured start type"
sc.exe qc BaseCampService 2>&1 | Out-File -FilePath $report -Append -Encoding utf8

Section "BaseCampService - current state"
sc.exe query BaseCampService 2>&1 | Out-File -FilePath $report -Append -Encoding utf8

Section "Fast Startup (HiberbootEnabled - 1 = on, can delay/skip Run-key launches)"
$hib = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled
Line "HiberbootEnabled = $($hib.HiberbootEnabled)"

Section "Registry Run-key entries matching Base Camp (HKCU + HKLM)"
$needles = 'displaypadworker','basecamp','base camp','mountain','makalu'
foreach ($hive in @(
    @{Root='HKCU:'; Name='HKCU'},
    @{Root='HKLM:'; Name='HKLM'}
)) {
    $runPath = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Run"
    $approvedPath = "$($hive.Root)\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
    $run = Get-Item -Path $runPath
    if (-not $run) { continue }
    foreach ($name in $run.Property) {
        $cmd = $run.GetValue($name)
        $matches = $false
        foreach ($n in $needles) {
            if ($name.ToLower().Contains($n) -or ("$cmd".ToLower().Contains($n))) { $matches = $true }
        }
        if ($name.ToLower().Contains('k2')) { $matches = $false }
        if (-not $matches) { continue }

        $flagBytes = (Get-ItemProperty -Path $approvedPath -Name $name).$name
        $flagState = "no flag (enabled by default)"
        if ($flagBytes) {
            $flagState = if (($flagBytes[0] -band 0x01) -eq 0) { "ENABLED (0x$('{0:X2}' -f $flagBytes[0]))" } else { "DISABLED (0x$('{0:X2}' -f $flagBytes[0]))" }
        }

        # Resolve exe path same way BaseCampProcessGuard does: quoted path, or
        # walk to first ".exe" that actually exists.
        $exePath = $null
        $c = "$cmd".Trim()
        if ($c.StartsWith('"')) {
            $end = $c.IndexOf('"', 1)
            if ($end -gt 0) { $exePath = $c.Substring(1, $end - 1) }
        } else {
            $searchFrom = 0
            while ($true) {
                $idx = $c.IndexOf('.exe', $searchFrom, [StringComparison]::OrdinalIgnoreCase)
                if ($idx -lt 0) { break }
                $candidate = $c.Substring(0, $idx + 4)
                if (Test-Path $candidate) { $exePath = $candidate; break }
                $searchFrom = $idx + 4
            }
        }
        $exeExists = if ($exePath) { Test-Path $exePath } else { "unresolved" }

        Line "[$($hive.Name)] $name"
        Line "  command       : $cmd"
        Line "  autostart flag: $flagState"
        Line "  exe path exists: $exeExists $(if ($exePath) { "($exePath)" })"
        Line ""
    }
}

Section "Scheduled Tasks matching Base Camp (Run-key check above does NOT cover these)"
Get-ScheduledTask 2>$null | Where-Object { $_.TaskName -match 'basecamp|mountain|makalu|displaypad' } |
    ForEach-Object {
        $info = Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath
        Line "$($_.TaskPath)$($_.TaskName)  State=$($_.State)  LastRunTime=$($info.LastRunTime)  LastResult=$($info.LastTaskResult)"
    }

Section "Application log - Base Camp crash/error events since last boot"
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
# Lookback capped at 7 days even if the machine hasn't rebooted in a while
# (sleep/hibernate instead of shutdown) - otherwise this can pull thousands
# of unrelated events on a long-uptime box.
$lookback = if (((Get-Date) - $boot).TotalDays -gt 7) { (Get-Date).AddDays(-7) } else { $boot }
# Match actual Base Camp executable/service names only - a loose "displaypad"/
# "mountain" substring also hits unrelated local traffic (confirmed: K2's own
# ASP.NET route "/Home/DisplayPadConnectedChanged" matched and flooded the report).
$bcExePattern = 'Base Camp\.exe|BaseCamp\.Service\.exe|MountainDisplayPadWorker\.exe|BaseCampService|electron\.app\.Base Camp'
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$lookback} -MaxEvents 500 2>$null |
    Where-Object { $_.Message -match $bcExePattern } |
    Select-Object -First 15 TimeCreated, ProviderName, Id, LevelDisplayName, @{N='Message';E={$_.Message.Substring(0,[Math]::Min(300,$_.Message.Length))}} |
    Format-List | Out-File -FilePath $report -Append -Encoding utf8

Section "System log - Service Control Manager events for BaseCampService since last boot"
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$lookback; ProviderName='Service Control Manager'} -MaxEvents 200 2>$null |
    Where-Object { $_.Message -match 'BaseCampService' } |
    Select-Object -First 15 TimeCreated, Id, LevelDisplayName, Message |
    Format-List | Out-File -FilePath $report -Append -Encoding utf8

Section "K2 runtime log - AutoStop/Restart/Kill lines around last boot"
$k2log = Join-Path $env:LocalAppData 'K2\K2.App\K2.App.log'
if (Test-Path $k2log) {
    Line "Log found: $k2log (showing last 50 matching lines - full history can be thousands)"
    # "[DpNative]" alone also tags routine per-device HID enumeration noise -
    # only its kill-related lines matter here.
    Select-String -Path $k2log -Pattern '\[AutoStop\]|\[Restart\]|\[DpNative\] (killing|could not kill)|\[BC\]' |
        Select-Object -Last 50 |
        ForEach-Object { Line $_.Line }
} else {
    Line "K2 log not found at $k2log"
}

Write-Host ""
Write-Host "Report written to: $report"
Write-Host "Send this file back for analysis."
