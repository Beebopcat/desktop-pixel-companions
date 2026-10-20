<#
.SYNOPSIS
  Deep system scan + automatic repair: malware, Windows integrity, disk, event-log patterns, startup/persistence.
.DESCRIPTION
  Detects problems, fixes the ones that have a well-known safe fix, and writes a plain-language report.
  Everything it changes is logged. Nothing is deleted except temp/cache files older than 2 days.
.PARAMETER ReportOnly     Detect and report only; change nothing.
.PARAMETER FullMalwareScan  Run a Defender FULL scan (slow, can take an hour+) instead of a quick scan.
.PARAMETER Quick          Skip the slow steps (SFC, DISM, malware scan). Useful for a fast health report.
.PARAMETER NoElevate      Do not ask for administrator rights (some checks will be skipped).
.PARAMETER NoPause        Do not wait for a key at the end.
#>
param([switch]$ReportOnly, [switch]$FullMalwareScan, [switch]$Quick, [switch]$NoElevate, [switch]$NoPause)

$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $NoElevate) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    foreach ($p in 'ReportOnly', 'FullMalwareScan', 'Quick', 'NoPause') { if ((Get-Variable $p).Value) { $a += "-$p" } }
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $a } catch { Write-Host 'Administrator permission was declined - nothing was run.' -ForegroundColor Yellow }
    return
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "PC-Repair-Reports\scan-$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
Start-Transcript -Path (Join-Path $outDir 'transcript.txt') | Out-Null
$findings = New-Object System.Collections.Generic.List[object]
$fixes = New-Object System.Collections.Generic.List[string]
function Add-Finding([string]$Area, [string]$Severity, [string]$Title, [string]$Detail = '', [string]$Action = '') {
    $findings.Add([pscustomobject]@{ Area = $Area; Severity = $Severity; Title = $Title; Detail = $Detail; Action = $Action })
    $color = switch ($Severity) { 'Problem' { 'Red' } 'Warning' { 'Yellow' } default { 'Gray' } }
    Write-Host ("  [{0}] {1}" -f $Severity, $Title) -ForegroundColor $color
}
function Did([string]$what) { $fixes.Add($what); Write-Host "  FIXED: $what" -ForegroundColor Green }
function Step([string]$text) { Write-Host ''; Write-Host "== $text ==" -ForegroundColor Cyan }
function Skip([string]$why) { Write-Host "  (skipped: $why)" -ForegroundColor DarkGray }
$since = (Get-Date).AddDays(-14)
$mode = if ($ReportOnly) { 'REPORT ONLY (nothing will be changed)' } else { 'SCAN + AUTO-REPAIR' }
Write-Host "PC deep scan - $mode" -ForegroundColor White
Write-Host "Admin: $isAdmin   Report folder: $outDir"

# ---------------------------------------------------------------- 1. baseline
Step '1/8 System baseline'
$os = Get-CimInstance Win32_OperatingSystem
$up = (Get-Date) - $os.LastBootUpTime
$sys = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'"
"{0} ({1}), up {2}d {3}h; {4:N0} GB free of {5:N0} GB" -f $os.Caption, $os.Version, $up.Days, $up.Hours, ($sys.FreeSpace / 1GB), ($sys.Size / 1GB) | Write-Host
if ($sys.FreeSpace / $sys.Size -lt 0.10) { Add-Finding 'Disk' 'Problem' 'System drive is almost full' ("{0:N0}% free" -f ($sys.FreeSpace / $sys.Size * 100)) 'Free space: clean temp files, uninstall unused programs.' }
elseif ($sys.FreeSpace / $sys.Size -lt 0.20) { Add-Finding 'Disk' 'Warning' 'System drive is under 20% free' ("{0:N0}% free" -f ($sys.FreeSpace / $sys.Size * 100)) 'Consider freeing space.' }
if ($up.Days -ge 14) { Add-Finding 'Maintenance' 'Warning' "Windows has not restarted for $($up.Days) days" '' 'Restart (not Shut down) to apply updates and clear memory.' }
$pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
if ($pending) { Add-Finding 'Maintenance' 'Warning' 'A restart is pending' '' 'Restart Windows.' }
if ($os.Caption -match 'Insider' -or (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild -ge 26200) { Add-Finding 'Maintenance' 'Info' 'You are on a Windows Insider preview build' 'Preview builds are more likely to crash the desktop compositor and shell.' 'If crashes continue, leave the Insider channel (Settings > Windows Update > Windows Insider Program).' }

# ---------------------------------------------------------------- 2. event logs
Step '2/8 Event-log patterns (last 14 days)'
function Ev($filter, $max = 1500) { try { Get-WinEvent -FilterHashtable $filter -MaxEvents $max -ErrorAction Stop } catch { @() } }
$appCrash = Ev @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; StartTime = $since }
$dwm = @($appCrash | ? { $_.Message -match 'dwm\.exe' })
if ($dwm.Count -ge 1) { Add-Finding 'Stability' 'Problem' "Desktop compositor (dwm.exe) crashed $($dwm.Count) time(s)" 'Causes blank menus, broken images and sluggish UI.' 'Reset the graphics stack (Win+Ctrl+Shift+B), update GPU drivers, restart. Frequent on Insider builds.' }
$crashByApp = $appCrash | % { $_.Properties[0].Value } | Group-Object | Sort Count -Descending | Select -First 5
foreach ($c in $crashByApp) { if ($c.Count -ge 3 -and $c.Name -notmatch 'dwm' -and $c.Name -notmatch '(Checks|Tests?|Fixture|Regression|Audit|Repair|Probe)(-v\d+\w*)?\.exe$') { Add-Finding 'Apps' 'Warning' "$($c.Name) crashed $($c.Count) times" '' 'Update or reinstall that program.' } }
$hangs = Ev @{ LogName = 'Application'; Id = 1002; StartTime = $since } 500
$hangApps = $hangs | % { if ($_.Message -match 'program (\S+) version') { $matches[1] } } | Group-Object | Sort Count -Descending | Select -First 5
foreach ($h in $hangApps) { Add-Finding 'Apps' 'Warning' "$($h.Name) stopped responding $($h.Count) time(s)" '' 'Update it; if it is a background app you do not need, disable it at startup.' }
$kp = @(Ev @{ LogName = 'System'; Id = 41; StartTime = $since })
if ($kp.Count -gt 0) { Add-Finding 'Stability' 'Warning' "$($kp.Count) unexpected shutdown(s) (Kernel-Power 41)" 'The PC rebooted without a clean shutdown.' 'Check power/overheating; install updates; disable Fast Startup if boots look odd.' }
$whea = @(Ev @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since })
if ($whea.Count -gt 0) { Add-Finding 'Hardware' 'Problem' "$($whea.Count) hardware error report(s) (WHEA)" '' 'Possible failing hardware or unstable overclock/undervolt. Update BIOS/chipset drivers.' }
$diskEv = @(Ev @{ LogName = 'System'; ProviderName = 'disk', 'Ntfs', 'stornvme', 'storahci'; Level = 1, 2, 3; StartTime = $since })
if ($diskEv.Count -gt 0) { Add-Finding 'Disk' 'Problem' "$($diskEv.Count) disk/file-system error(s) in the log" '' 'Run the disk check below; back up important files.' }
$vol = @(Ev @{ LogName = 'System'; ProviderName = 'Volsnap'; Id = 36; StartTime = $since })
$tdr = @(Ev @{ LogName = 'System'; Id = 4101; StartTime = $since })
if ($tdr.Count -gt 0) { Add-Finding 'Graphics' 'Warning' "$($tdr.Count) display driver reset(s)" '' 'Update the GPU driver (clean install).' }
$wuFail = @(Ev @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WindowsUpdateClient'; Id = 20, 25, 31; StartTime = $since })
if ($wuFail.Count -gt 0) { Add-Finding 'Updates' 'Warning' "$($wuFail.Count) Windows Update failure(s)" 'Some updates failed to install.' 'Windows Update components can be reset (done automatically below).' }
$svcFail = Ev @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Level = 2; StartTime = $since } 400
$svcNames = $svcFail | % { if ($_.Message -match 'The (.+?) service') { $matches[1] } } | Group-Object | Sort Count -Descending | Select -First 5
foreach ($s in $svcNames) { if ($s.Count -ge 3) { Add-Finding 'Services' 'Warning' "Service '$($s.Name)' failed $($s.Count) times" '' 'Reinstall/repair that program or disable the service if unused.' } }

# ---------------------------------------------------------------- 3. heavy / leaking processes
Step '3/8 Processes that can cause slowdowns'
Get-Process | Where-Object { $_.Handles -gt 8000 } | Sort Handles -Descending | Select -First 5 | ForEach-Object {
    Add-Finding 'Performance' 'Warning' ("{0} holds {1:N0} handles (possible leak)" -f $_.ProcessName, $_.Handles) ("PID {0}, {1} MB" -f $_.Id, [int]($_.WS / 1MB)) 'Restart or update that program; if it is a vendor service (e.g. Razer Synapse) consider a clean reinstall.' }
$groups = Get-Process | Group-Object ProcessName | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Count = $_.Count; MB = [int](($_.Group | Measure-Object WS -Sum).Sum / 1MB) } } | Sort MB -Descending | Select -First 6
$groups | ForEach-Object { Write-Host ("  {0,-24} x{1,-3} {2,6} MB" -f $_.Name, $_.Count, $_.MB) }
$ram = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1); $free = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
if ($free / $ram -lt 0.1) { Add-Finding 'Performance' 'Problem' "Low memory: $free GB free of $ram GB" '' 'Close heavy apps.' }

# ---------------------------------------------------------------- 4. startup / persistence
Step '4/8 Startup entries and persistence'
function Get-Target([string]$c) { if (-not $c) { return '' }; $c = [Environment]::ExpandEnvironmentVariables($c.Trim()); if ($c.StartsWith('"')) { return $c.Substring(1, $c.IndexOf('"', 1) - 1) }; $m = [regex]::Match($c, '^(.+?\.(exe|cmd|bat|ps1|vbs|lnk|pyw|py))', 'IgnoreCase'); if ($m.Success) { $m.Groups[1].Value } else { ($c -split ' ')[0] } }
$entries = @()
foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    foreach ($n in ($p.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' })) { $entries += [pscustomobject]@{ Where = $k; Name = $n.Name; Target = (Get-Target $n.Value) } }
}
foreach ($t in Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -notmatch '^\\Microsoft\\' -and $_.State -ne 'Disabled' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -match 'Logon|Boot' }) }) {
    $a = $t.Actions | Select-Object -First 1; if ($a.Execute) { $entries += [pscustomobject]@{ Where = 'Task'; Name = $t.TaskName; Target = (Get-Target $a.Execute) } } }
$risky = '\\Temp\\|\\Downloads\\|\\AppData\\Local\\Temp|\\Users\\Public\\'
foreach ($e in $entries) {
    if (-not $e.Target) { continue }
    $found = (Test-Path -LiteralPath $e.Target) -or ($e.Target -and $e.Target -notmatch '[\\/]' -and (Get-Command $e.Target -ErrorAction SilentlyContinue))
    if ($e.Target -and -not $found) { Add-Finding 'Startup' 'Warning' "Broken startup entry: $($e.Name)" "Points to a file that no longer exists: $($e.Target)" 'Remove it (Startup-Audit tool does this safely, with a backup).' }
    elseif ($e.Target -match $risky) { Add-Finding 'Security' 'Problem' "Startup entry runs from a temporary/public folder: $($e.Name)" $e.Target 'Investigate - malware often persists this way.' }
    elseif ($e.Target -and (Test-Path -LiteralPath $e.Target) -and $e.Target -match '\.(exe|dll)$') {
        $sig = Get-AuthenticodeSignature -LiteralPath $e.Target -ErrorAction SilentlyContinue
        if ($sig -and $sig.Status -in 'HashMismatch', 'NotTrusted') { Add-Finding 'Security' 'Problem' "Startup program has an invalid signature: $($e.Name)" "$($e.Target) ($($sig.Status))" 'Verify the file; reinstall the program from its publisher.' } }
}
Write-Host "  $($entries.Count) startup/logon entries checked."
$hosts = Get-Content "$env:SystemRoot\System32\drivers\etc\hosts" -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch '^\s*#' -and $_.Trim() }
if (@($hosts | Where-Object { $_ -match 'microsoft|windows|google|defender|update' }).Count -gt 0) { Add-Finding 'Security' 'Problem' 'The hosts file redirects security/Microsoft/Google names' ($hosts -join '; ') 'Review the hosts file; this is a common malware trick.' }
$proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
if ($proxy.ProxyEnable -eq 1) { Add-Finding 'Security' 'Warning' 'A system proxy is enabled' $proxy.ProxyServer 'Confirm you set this; malware can force a proxy.' }

# ---------------------------------------------------------------- 5. malware
Step '5/8 Malware (Microsoft Defender)'
try {
    $mp = Get-MpComputerStatus -ErrorAction Stop
    if (-not $mp.RealTimeProtectionEnabled) { Add-Finding 'Security' 'Problem' 'Defender real-time protection is OFF' '' 'Turn it on in Windows Security.' }
    $age = (Get-Date) - $mp.AntivirusSignatureLastUpdated
    if ($age.TotalDays -gt 3) { Add-Finding 'Security' 'Warning' "Defender definitions are $([int]$age.TotalDays) days old" '' 'Update definitions (done below).' }
    $pref = Get-MpPreference
    $ex = @($pref.ExclusionPath) + @($pref.ExclusionProcess) | Where-Object { $_ }
    $badEx = $ex | Where-Object { $_ -match '^[A-Za-z]:\\?$|\\Users\\[^\\]+\\?$|\\Temp|\\Downloads|\\AppData\\Local\\?$' }
    if ($badEx) { Add-Finding 'Security' 'Problem' 'Defender has broad exclusions' ($badEx -join '; ') 'Malware adds exclusions to hide. Remove any you did not add.' }
    if ($isAdmin -and -not $ReportOnly) { try { Update-MpSignature -ErrorAction Stop; Did 'Updated Defender definitions' } catch { Write-Host "  could not update definitions: $($_.Exception.Message)" -ForegroundColor DarkGray } }
    if ($Quick) { Skip 'quick mode' }
    elseif (-not $isAdmin) { Skip 'needs administrator' }
    elseif ($ReportOnly) { Skip 'report-only mode (no scan started)' }
    else {
        $scanStart = Get-Date
        $type = if ($FullMalwareScan) { 'FullScan' } else { 'QuickScan' }
        Write-Host "  running Defender $type (this can take a while)..."
        Start-MpScan -ScanType $type -ErrorAction Stop
        $new = Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -ge $scanStart.AddMinutes(-1) }
        if ($new) {
            foreach ($t in $new) { Add-Finding 'Security' 'Problem' "Threat detected: $($t.ThreatID)" (($t.Resources | Select-Object -First 2) -join '; ') 'Defender remediates automatically; verify below.' }
            try { Get-MpThreat | Where-Object { $_.IsActive } | ForEach-Object { Remove-MpThreat -ErrorAction SilentlyContinue }; Did 'Asked Defender to remove active threats' } catch { }
        } else { Write-Host '  no new threats found.' -ForegroundColor Green }
    }
    $old = Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt $since }
    if ($old) { Add-Finding 'Security' 'Info' "$(@($old).Count) Defender detection(s) in the last 14 days" 'They may be false positives (e.g. security-research text in tool output). Review in Windows Security > Protection history.' }
} catch { Add-Finding 'Security' 'Warning' 'Could not query Windows Defender' $_.Exception.Message 'Check that Defender is not disabled by another antivirus.' }

# ---------------------------------------------------------------- 6. Windows integrity
Step '6/8 Windows system files (DISM + SFC)'
if ($Quick) { Skip 'quick mode' }
elseif (-not $isAdmin) { Skip 'needs administrator' }
else {
    $dismCheck = (& dism.exe /Online /Cleanup-Image /CheckHealth) -join "`n"
    $repairable = $dismCheck -match 'repairable'
    $needRestore = $repairable
    if (-not $repairable) { $scan = (& dism.exe /Online /Cleanup-Image /ScanHealth) -join "`n"; if ($scan -match 'repairable') { $needRestore = $true } }
    if ($needRestore) {
        Add-Finding 'Windows' 'Problem' 'The Windows component store is corrupted (repairable)' '' 'DISM RestoreHealth.'
        if (-not $ReportOnly) {
            Write-Host '  repairing component store (DISM RestoreHealth)...'
            & dism.exe /Online /Cleanup-Image /RestoreHealth | Out-Null
            $code = $LASTEXITCODE
            if ($code -ne 0) {
                # Often fixed by trimming superseded components first, then retrying.
                Write-Host '  retrying after a component clean-up...'
                & dism.exe /Online /Cleanup-Image /StartComponentCleanup | Out-Null
                & dism.exe /Online /Cleanup-Image /RestoreHealth | Out-Null
                $code = $LASTEXITCODE
            }
            if ($code -eq 0) { Did 'DISM repaired the component store' }
            else {
                $hex = '0x{0:X8}' -f ([uint32]([int]$code -band 0xFFFFFFFF))
                $why = if ($hex -in '0x800F0915', '0x800F081F', '0x800F0906') { 'Windows Update could not supply the repair files. This is common on Insider preview builds.' } else { 'DISM could not complete.' }
                Add-Finding 'Windows' 'Problem' "DISM could not repair the component store ($hex)" $why 'Option A: mount a Windows ISO of the SAME build and run: DISM /Online /Cleanup-Image /RestoreHealth /Source:wim:X:\sources\install.wim:1 /LimitAccess. Option B: leave the Insider channel and do an in-place repair install. SFC reported separately whether your files are intact.'
            }
        }
    } else { Write-Host '  component store is healthy.' -ForegroundColor Green }
    $sfcOut = Join-Path $outDir 'sfc.txt'
    $arg = if ($ReportOnly) { '/verifyonly' } else { '/scannow' }
    Write-Host "  running sfc $arg ..."
    cmd.exe /c "sfc $arg > `"$sfcOut`" 2>&1" | Out-Null
    $sfcText = try { [IO.File]::ReadAllText($sfcOut, [Text.Encoding]::Unicode) } catch { '' }
    if ($sfcText -match 'did not find any integrity violations') { Write-Host '  system files are intact.' -ForegroundColor Green }
    elseif ($sfcText -match 'unable to fix') {
        Add-Finding 'Windows' 'Problem' 'SFC found corrupt files it could not repair' '' 'DISM RestoreHealth then SFC again.'
        if (-not $ReportOnly) { & dism.exe /Online /Cleanup-Image /RestoreHealth | Out-Null; cmd.exe /c "sfc /scannow > `"$sfcOut`" 2>&1" | Out-Null; $sfcText = try { [IO.File]::ReadAllText($sfcOut, [Text.Encoding]::Unicode) } catch { '' }
            if ($sfcText -match 'did not find any integrity violations|successfully repaired') { Did 'DISM + SFC repaired the remaining corrupt files' } else { Add-Finding 'Windows' 'Problem' 'Some system files are still corrupt' '' 'Run an in-place repair install of Windows.' } }
    }
    elseif ($sfcText -match 'successfully repaired') { Did 'SFC repaired corrupt system files' }
    elseif ($sfcText -match 'found integrity violations') { Add-Finding 'Windows' 'Warning' 'SFC found corrupt files (report-only mode)' '' 'Run again without -ReportOnly to repair them.' }
    elseif ($sfcText) { Write-Host '  sfc finished (see sfc.txt).' }
}

# ---------------------------------------------------------------- 7. disk
Step '7/8 Disk health and file system'
foreach ($d in Get-PhysicalDisk -ErrorAction SilentlyContinue) {
    Write-Host ("  {0}: {1} / {2}" -f $d.FriendlyName, $d.HealthStatus, $d.OperationalStatus)
    if ($d.HealthStatus -ne 'Healthy') { Add-Finding 'Disk' 'Problem' "Disk '$($d.FriendlyName)' reports $($d.HealthStatus)" '' 'Back up your data now and replace the drive.' }
    $rc = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    if ($rc -and $rc.Wear -ge 90) { Add-Finding 'Disk' 'Warning' "SSD wear is $($rc.Wear)%" '' 'Plan a replacement.' }
}
if (-not $isAdmin) { Skip 'file-system scan needs administrator' }
else {
    try {
        $letter = $env:SystemDrive.TrimEnd(':')
        $r = Repair-Volume -DriveLetter $letter -Scan -ErrorAction Stop
        if ("$r" -match 'NoErrorsFound') { Write-Host '  file system: no errors found.' -ForegroundColor Green }
        else {
            Add-Finding 'Disk' 'Problem' 'The file system reports errors' "$r" 'Spot-fix (done below).'
            if (-not $ReportOnly) { Repair-Volume -DriveLetter $letter -SpotFix -ErrorAction SilentlyContinue | Out-Null; Did 'Queued a file-system spot-fix (finishes at next restart)' }
        }
    } catch { Write-Host "  file-system scan skipped: $($_.Exception.Message)" -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------- 8. safe automatic fixes
Step '8/8 Safe automatic fixes'
if ($ReportOnly -or -not $isAdmin) { Skip $(if ($ReportOnly) { 'report-only mode' } else { 'needs administrator' }) }
else {
    # shadow-copy storage cap too small
    if ($vol.Count -gt 0) {
        & vssadmin.exe resize shadowstorage "/for=$env:SystemDrive" "/on=$env:SystemDrive" '/maxsize=10%' | Out-Null
        if ($LASTEXITCODE -eq 0) { Did 'Raised the shadow-copy storage limit to 10% (fixes Volsnap errors)' }
    }
    # automatic services that are stopped (not trigger-start, not known on-demand)
    $skipSvc = 'sppsvc', 'edgeupdate', 'gupdate', 'GoogleUpdaterService*', 'GoogleUpdaterInternalService*', 'AnyDesk', 'DiagTrack', 'MapsBroker', 'RemoteRegistry', 'tiledatamodelsvc', 'wuauserv', 'BITS'
    Get-CimInstance Win32_Service | Where-Object { $_.StartMode -eq 'Auto' -and $_.State -eq 'Stopped' -and $_.ExitCode -ne 0 } | ForEach-Object {
        $n = $_.Name
        if ($skipSvc | Where-Object { $n -like $_ }) { return }
        if (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$n\TriggerInfo") { return }
        try { Start-Service -Name $n -ErrorAction Stop; Did "Started stopped service: $($_.DisplayName)" } catch { Add-Finding 'Services' 'Warning' "Service would not start: $($_.DisplayName)" $_.Exception.Message 'Repair/reinstall the owning program.' }
    }
    # Windows Update components
    if ($wuFail.Count -gt 0) {
        try {
            'wuauserv', 'bits', 'cryptsvc' | ForEach-Object { Stop-Service $_ -Force -ErrorAction SilentlyContinue }
            foreach ($p in "$env:SystemRoot\SoftwareDistribution", "$env:SystemRoot\System32\catroot2") { if (Test-Path $p) { Rename-Item $p ((Split-Path $p -Leaf) + ".bak-$stamp") -ErrorAction SilentlyContinue } }
            'cryptsvc', 'bits', 'wuauserv' | ForEach-Object { Start-Service $_ -ErrorAction SilentlyContinue }
            Did 'Reset Windows Update components (old caches kept as .bak folders)'
        } catch { Write-Host "  update reset failed: $($_.Exception.Message)" -ForegroundColor DarkGray }
    }
    # temp files older than 2 days
    $freed = 0
    foreach ($tmp in $env:TEMP, "$env:SystemRoot\Temp") {
        Get-ChildItem $tmp -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-2) -and $_.Name -notlike 'claude*' } | ForEach-Object {
            $sz = if ($_.PSIsContainer) { (Get-ChildItem $_.FullName -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } else { $_.Length }
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $_.FullName)) { $freed += [double]$sz } }
    }
    if ($freed -gt 50MB) { Did ("Cleaned {0:N1} GB of old temp files" -f ($freed / 1GB)) }
    # network sanity
    $dnsOk = try { [bool](Resolve-DnsName 'www.microsoft.com' -ErrorAction Stop) } catch { $false }
    if (-not $dnsOk) { & ipconfig.exe /flushdns | Out-Null; Add-Finding 'Network' 'Warning' 'DNS lookups failed' 'Flushed the DNS cache.' 'If it persists, reset the network (netsh winsock reset) and restart.' }
}

# ---------------------------------------------------------------- report
Stop-Transcript | Out-Null
$problems = @($findings | Where-Object Severity -eq 'Problem').Count
$warnings = @($findings | Where-Object Severity -eq 'Warning').Count
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# PC deep scan report - $(Get-Date -Format 'f')")
[void]$md.AppendLine("Mode: $mode. Administrator: $isAdmin. $problems problem(s), $warnings warning(s), $($fixes.Count) fix(es) applied.")
[void]$md.AppendLine('')
if ($fixes.Count) { [void]$md.AppendLine('## Fixed automatically'); $fixes | ForEach-Object { [void]$md.AppendLine("- $_") }; [void]$md.AppendLine('') }
foreach ($sev in 'Problem', 'Warning', 'Info') {
    $set = @($findings | Where-Object Severity -eq $sev)
    if ($set.Count) {
        [void]$md.AppendLine("## $sev" + 's'); foreach ($f in $set) { [void]$md.AppendLine("- **$($f.Title)** [$($f.Area)]"); if ($f.Detail) { [void]$md.AppendLine("  - $($f.Detail)") }; if ($f.Action) { [void]$md.AppendLine("  - What to do: $($f.Action)") } }; [void]$md.AppendLine('') }
}
if (-not $findings.Count) { [void]$md.AppendLine('Nothing wrong was found.') }
$md.ToString() | Set-Content (Join-Path $outDir 'report.md') -Encoding UTF8
$findings | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outDir 'findings.json') -Encoding UTF8
Write-Host ''; Write-Host '=============== SUMMARY ===============' -ForegroundColor White
Write-Host ("Problems: {0}   Warnings: {1}   Fixed automatically: {2}" -f $problems, $warnings, $fixes.Count) -ForegroundColor $(if ($problems) { 'Red' } elseif ($warnings) { 'Yellow' } else { 'Green' })
foreach ($f in ($findings | Where-Object Severity -in 'Problem', 'Warning')) { Write-Host (" - [{0}] {1}" -f $f.Severity, $f.Title) }
Write-Host "Report: $outDir\report.md"
if ($pending -or $fixes.Count) { Write-Host 'Restart Windows (Restart, not Shut down) to finish the repairs.' -ForegroundColor Cyan }
try { Start-Process notepad.exe (Join-Path $outDir 'report.md') } catch { }
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost' -and -not $NoElevate) { Write-Host ''; [void](Read-Host 'Press Enter to close') }
