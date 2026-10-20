<#
  Avatar Toolbox  -  one tiny always-on-top desktop companion with: your tools (start/stop/restart with live status),
  automated checks and repairs, the PC repair helpers, and a picker to swap between all your pixel companions.
  Run:  double-click "Avatar Toolbox" in the AutoHotkey folder (or on the Desktop)   -   powershell -STA -File Avatar-Toolbox.ps1
  Your tools live in tools.json next to this file - add a block there to add a tool.
  Nothing runs until you click. Anything that needs admin shows a Windows UAC prompt first.
  Right-click your companion (or press the x) to close the toolbox; your tools keep running.
  Test switches:  -Snapshot out.png [-Open tools|repair|avatar]   -ToolsFile other.json   -SelfTest out.txt
#>
param([string]$Snapshot = '', [string]$Open = '', [string]$ToolsFile = '', [string]$SelfTest = '', [string]$PickAvatar = '')

# WPF needs a single-threaded apartment. Re-launch ourselves in one if needed.
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', "`"$PSCommandPath`"")
    if ($Snapshot)  { $argList += @('-Snapshot', "`"$Snapshot`"", '-Open', $Open) }
    if ($PickAvatar) { $argList += @('-PickAvatar', $PickAvatar) }
    if ($ToolsFile) { $argList += @('-ToolsFile', "`"$ToolsFile`"") }
    if ($SelfTest)  { $argList += @('-SelfTest', "`"$SelfTest`"") }
    Start-Process powershell.exe -ArgumentList $argList -WindowStyle Hidden
    return
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms

$script:instanceCreated = $false
$script:instanceMutex = New-Object Threading.Mutex($true, 'Local\AvatarToolbox', [ref]$script:instanceCreated)
if (-not $script:instanceCreated -and -not $Snapshot -and -not $SelfTest) { return }

# ---------------------------------------------------------------- settings
$FrameW = 126; $FrameH = 150
$G_RUN  = [string][char]0x25B7
$G_STOP = [string][char]0x25A0
$G_INFO = [string][char]0x24D8
$G_DOWN = [string][char]0x25BE
$G_ALL  = [string][char]0x25B6
$HubDir = $PSScriptRoot
$ToolsPath = if ($ToolsFile) { $ToolsFile } else { Join-Path $HubDir 'tools.json' }
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$G_COPY = [string][char]0x29C9
$ArrowR = [string][char]0x2192
$Commands = @(
    'sfc /scannow',
    'DISM /Online /Cleanup-Image /RestoreHealth',
    'vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10%'
)

$AvatarDir = Join-Path $HubDir 'avatars'
$SettingsPath = Join-Path $HubDir 'settings.json'
$script:AvatarCells = @{}
$script:AvatarPanelBuilt = $false

# ---------------------------------------------------------------- helpers
$bc = New-Object Windows.Media.BrushConverter
function Brush([string]$hex) { $bc.ConvertFromString($hex) }
function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
function Parse-Xaml([string]$x) { [Windows.Markup.XamlReader]::Parse($x) }
$NS = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'

function New-Btn([string]$text, [string]$glyph, [string]$color, [double]$width = 0) {
    $w = if ($width -gt 0) { "Width=`"$width`"" } else { '' }
    Parse-Xaml @"
<Button $NS $w Cursor="Hand" Focusable="False" Margin="0,3,0,3" HorizontalContentAlignment="Stretch">
  <Button.Template>
    <ControlTemplate TargetType="Button">
      <Border x:Name="b" CornerRadius="9" Background="#161618" BorderBrush="#383A41" BorderThickness="1" Padding="10,7">
        <ContentPresenter HorizontalAlignment="Stretch"/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="b" Property="Background" Value="#242426"/>
          <Setter TargetName="b" Property="BorderBrush" Value="$color"/>
        </Trigger>
        <Trigger Property="IsPressed" Value="True">
          <Setter TargetName="b" Property="Background" Value="#131417"/>
        </Trigger>
        <Trigger Property="IsEnabled" Value="False">
          <Setter TargetName="b" Property="Opacity" Value="0.45"/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Button.Template>
  <Grid>
    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
    <TextBlock Text="$(Esc $text)" FontFamily="Consolas" FontSize="12" Foreground="$color" TextWrapping="Wrap" VerticalAlignment="Center"/>
    <TextBlock Grid.Column="1" Text="$(Esc $glyph)" FontFamily="Segoe UI Symbol" FontSize="14" Foreground="$color" Margin="10,0,0,0" VerticalAlignment="Center"/>
  </Grid>
</Button>
"@
}

# Small text button for the row actions.
function New-SmallBtn([string]$text, [string]$tip, [string]$color) {
    Parse-Xaml @"
<Button $NS Cursor="Hand" Focusable="False" ToolTip="$(Esc $tip)" Margin="0,0,4,0">
  <Button.Template>
    <ControlTemplate TargetType="Button">
      <Border x:Name="b" CornerRadius="6" Background="#161618" BorderBrush="#383A41" BorderThickness="1" Padding="7,2">
        <TextBlock x:Name="t" Text="$(Esc $text)" FontFamily="Consolas" FontSize="11" Foreground="#A8ADB7"/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="b" Property="Background" Value="#242426"/>
          <Setter TargetName="t" Property="Foreground" Value="$color"/>
        </Trigger>
        <Trigger Property="IsEnabled" Value="False">
          <Setter TargetName="b" Property="Opacity" Value="0.4"/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Button.Template>
</Button>
"@
}

# ---------------------------------------------------------------- tools (data-driven: tools.json)
function Expand([string]$s) { if ($s) { [Environment]::ExpandEnvironmentVariables($s) } else { '' } }
function Load-Tools {
    $script:ToolsJson = [IO.File]::ReadAllText($ToolsPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $script:Ahk = Expand $script:ToolsJson.ahk
    $script:ToolList = @($script:ToolsJson.tools)
}
function Save-Tools {
    $text = $script:ToolsJson | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($ToolsPath, $text, (New-Object Text.UTF8Encoding $false))
}
function Get-ProcSnap { @(Get-CimInstance Win32_Process -Property Name, ProcessId, CommandLine -ErrorAction SilentlyContinue) }
function Test-Url([string]$u) {
    try { $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 3; return ($r.StatusCode -eq 200) }
    catch { if ($Error.Count) { $Error.RemoveAt(0) }; return $false }   # a busy or stopped service is a normal answer, not an error to log
}
function Get-ToolProcs($tool, $snap) {
    $names = if ($tool.kind -eq 'ahk') { @('AutoHotkey64.exe', 'AutoHotkey32.exe', 'AutoHotkey.exe') } else { @('pythonw.exe', 'python.exe') }
    $needle = Expand $tool.path
    @($snap | Where-Object { ($names -contains $_.Name) -and $_.CommandLine -and ($_.CommandLine.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) })
}
function Test-ToolRunning($tool, $snap) {
    if ($tool.healthUrl -and (Test-Url $tool.healthUrl)) { return $true }
    return (@(Get-ToolProcs $tool $snap).Count -gt 0)   # @() - a single match has no .Count in Windows PowerShell 5.1
}
function Start-Tool($tool) {
    $path = Expand $tool.path
    if (-not (Test-Path -LiteralPath $path)) { return "Missing file: $path" }
    if (Test-ToolRunning $tool (Get-ProcSnap)) { return 'already running' }
    $dir = Split-Path -Parent $path
    if ($tool.kind -eq 'ahk') {
        if (-not (Test-Path -LiteralPath $script:Ahk)) { return "AutoHotkey v2 not found at $($script:Ahk)" }
        Start-Process -FilePath $script:Ahk -ArgumentList ('"{0}"' -f $path) -WorkingDirectory $dir
    } else {
        $exe = Expand $tool.exe
        if (-not (Test-Path -LiteralPath $exe)) { return "Missing program: $exe" }
        $a = '"{0}"' -f $path
        if ($tool.args) { $a += ' ' + $tool.args }
        Start-Process -FilePath $exe -ArgumentList $a -WorkingDirectory $dir -WindowStyle Hidden
    }
    return 'started'
}
function Stop-Tool($tool) {
    if ($tool.kind -eq 'service') { return 'managed elsewhere' }
    if ($tool.stopUrl) {
        try { Invoke-WebRequest -Uri $tool.stopUrl -Method Post -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 2 | Out-Null } catch { }
        Start-Sleep -Milliseconds 500
    }
    foreach ($p in (Get-ToolProcs $tool (Get-ProcSnap))) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    return 'stopped'
}
function Wait-Tool($tool, [bool]$wantRunning, [int]$seconds = 8) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if ((Test-ToolRunning $tool (Get-ProcSnap)) -eq $wantRunning) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# ---------------------------------------------------------------- self-test (no window)
if ($SelfTest) {
    $o = New-Object Collections.Generic.List[string]
    function P([string]$name, [bool]$ok, [string]$d = '') { $o.Add($(if ($ok) { 'PASS ' } else { 'FAIL ' }) + $name + $(if ($d) { "  [$d]" })) }
    try { Load-Tools; P 'load tools.json' ($script:ToolList.Count -ge 1) "$($script:ToolList.Count) tools" } catch { P 'load tools.json' $false $_.Exception.Message }
    foreach ($t in $script:ToolList) {
        $exists = Test-Path -LiteralPath (Expand $t.path)
        $snap = Get-ProcSnap
        $running = Test-ToolRunning $t $snap
        if ($t.test) {
            $r1 = Start-Tool $t;  $up = Wait-Tool $t $true 10
            P "$($t.id): start" ($r1 -eq 'started' -and $up) "$r1"
            $r2 = Start-Tool $t;  P "$($t.id): second start is a no-op" ($r2 -eq 'already running') $r2
            [void](Stop-Tool $t); $down = Wait-Tool $t $false 8
            P "$($t.id): stop" $down
            [void](Start-Tool $t); [void](Wait-Tool $t $true 10); [void](Stop-Tool $t); [void](Stop-Tool $t)
            P "$($t.id): restart cycle ends stopped" (Wait-Tool $t $false 8)
        } else {
            P "$($t.id): file present" $exists (Expand $t.path)
            $o.Add("INFO $($t.id): currently " + $(if ($running) { 'running' } else { 'stopped' }))
        }
    }
    [IO.File]::WriteAllLines($SelfTest, $o, (New-Object Text.UTF8Encoding $false))
    return
}

# ---------------------------------------------------------------- actions
$script:Status = $null
function Say([string]$msg) {
    $script:Status.Text = $msg
    $script:StatusBox.Visibility = if ($msg) { 'Visible' } else { 'Collapsed' }
    $script:StatusTimer.Stop()
    if ($msg) { $script:StatusTimer.Start() }
}
function Busy-Anim { $script:WalkUntil = (Get-Date).AddSeconds(2.5) }

# Small flat icon button used inside code boxes.
function New-IconBtn([string]$glyph, [string]$tip, [string]$color) {
    Parse-Xaml @"
<Button $NS Cursor="Hand" Focusable="False" ToolTip="$(Esc $tip)" Margin="2,0,0,0">
  <Button.Template>
    <ControlTemplate TargetType="Button">
      <Border x:Name="b" CornerRadius="6" Background="Transparent" Padding="8,3">
        <TextBlock x:Name="t" Text="$(Esc $glyph)" FontFamily="Segoe UI Symbol" FontSize="15" Foreground="#A8ADB7"/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="b" Property="Background" Value="#2E3036"/>
          <Setter TargetName="t" Property="Foreground" Value="$color"/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Button.Template>
</Button>
"@
}

function Copy-Text([string]$t, [string]$what) {
    try { [Windows.Clipboard]::SetText($t); Say "Copied $what to the clipboard." }
    catch { Say 'Clipboard was busy - try again.' }
}

function Run-Elevated([string[]]$cmds, [string]$title) {
    $list = ($cmds | ForEach-Object { '  ' + $_ }) -join "`n"
    $ok = [Windows.MessageBox]::Show("This opens an ADMIN PowerShell window (Windows will show a UAC prompt first) and runs:`n`n$list`n`nContinue?", $title, 'YesNo', 'Question')
    if ($ok -ne 'Yes') { Say 'Cancelled - nothing was run.'; return }
    $lines = @("`$Host.UI.RawUI.WindowTitle = 'PC Repair (admin)'")
    $i = 0
    foreach ($c in $cmds) {
        $i++
        $lines += "Write-Host ''; Write-Host '== $i/$($cmds.Count): $($c.Replace("'", "''")) ==' -ForegroundColor Cyan"
        $lines += $c
    }
    $lines += "Write-Host ''; Write-Host 'Finished. Do a real Restart (not Shut down) to complete the repair.' -ForegroundColor Green"
    $file = Join-Path $env:TEMP 'pc-repair-run.ps1'
    Set-Content -LiteralPath $file -Value $lines -Encoding UTF8
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$file`"")
        Busy-Anim; Say 'Admin terminal opened - watch it for results.'
    } catch { Say 'Admin prompt was declined - nothing was run.' }
}

Add-Type -Namespace Win32 -Name Keys -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
'@

$RepairTools = @(
    @{ Name = 'Reset graphics stack'; Color = '#7EE787'
       Info = 'Sends Win+Ctrl+Shift+B. The screen flickers for a second while the display driver and desktop compositor (dwm) re-initialise. Fixes blank menus and glitchy rendering.'
       Do = {
           $VK = @(0x5B, 0x11, 0x10, 0x42)
           foreach ($k in $VK) { [Win32.Keys]::keybd_event([byte]$k, 0, 0, [UIntPtr]::Zero) }
           Start-Sleep -Milliseconds 80
           foreach ($k in $VK[3..0]) { [Win32.Keys]::keybd_event([byte]$k, 0, 2, [UIntPtr]::Zero) }
           Busy-Anim; Say 'Graphics reset sent - the screen may flicker.'
       } }
    @{ Name = 'Restart Start menu + Search'; Color = '#79B8FF'
       Info = 'Closes the Start menu, Search and Shell UI host processes. Windows relaunches them in a few seconds. Fixes a blank or frozen Start/Search panel. Nothing is lost.'
       Do = {
           Stop-Process -Name StartMenuExperienceHost, SearchHost, ShellExperienceHost -Force -ErrorAction SilentlyContinue
           Busy-Anim; Say 'Start menu and Search restarted - try them again in a few seconds.'
       } }
    @{ Name = 'Restart Explorer'; Color = '#79B8FF'; Confirm = 'Restart Windows Explorer? The taskbar and any open File Explorer windows will close and reopen.'
       Info = 'Restarts the Windows shell (taskbar, desktop, File Explorer windows). Fixes a frozen taskbar or menus that still misbehave after the step above.'
       Do = {
           Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
           Start-Sleep -Seconds 3
           if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
           Busy-Anim; Say 'Explorer restarted.'
       } }
    @{ Name = 'Test Chrome without GPU'; Color = '#F2CC60'
       Info = 'Opens Chrome with GPU acceleration switched off for this launch only. If broken images load normally here, the GPU path is the culprit (then turn the Chrome setting off).'
       Do = {
           if (Get-Process chrome -ErrorAction SilentlyContinue) {
               Say 'Chrome is already running - close all Chrome windows first, then click again.'; return
           }
           $cands = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
           $exe = $cands | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
           if (-not $exe) { Say 'Could not find Chrome.'; return }
           Start-Process $exe -ArgumentList '--disable-gpu'
           Busy-Anim; Say 'Chrome opened with --disable-gpu. Do the images load now?'
       } }
    @{ Name = 'Clean temp files'; Color = '#D2A8FF'
       Confirm = 'Delete files in your user Temp folder that are older than 2 days? (Anything in use is skipped. They regenerate.)'
       Info = 'Deletes user Temp files older than 2 days and reports the space freed. Safe: apps recreate what they need. Skips anything currently in use.'
       Do = {
           $cut = (Get-Date).AddDays(-2); $freed = 0
           Get-ChildItem -LiteralPath $env:TEMP -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $cut -and $_.Name -notlike 'claude*' } | ForEach-Object {
               $sz = if ($_.PSIsContainer) { (Get-ChildItem $_.FullName -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } else { $_.Length }
               Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
               if (-not (Test-Path -LiteralPath $_.FullName)) { $freed += [double]$sz }
           }
           Busy-Anim; Say ('Freed about {0:N2} GB of temp files.' -f ($freed / 1GB))
       } }
    @{ Name = 'Quick health snapshot'; Color = '#7EE787'
       Info = 'Read-only report: uptime, free space on the system drive, and how many desktop-compositor crashes, GPU resets and unexpected reboots Windows logged recently. Also copied to the clipboard.'
       Do = {
           [Windows.Input.Mouse]::OverrideCursor = [Windows.Input.Cursors]::Wait
           try {
               $os = Get-CimInstance Win32_OperatingSystem; $up = (Get-Date) - $os.LastBootUpTime
               $d = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'"
               $dwm = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 3000 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'dwm\.exe' }).Count
               $tdr = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 4101; StartTime = (Get-Date).AddDays(-7) } -ErrorAction SilentlyContinue).Count
               $kp  = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41; StartTime = (Get-Date).AddDays(-30) } -ErrorAction SilentlyContinue).Count
               $txt = @(
                   "Windows:   $($os.Caption) ($($os.Version))",
                   ('Uptime:    {0}d {1}h' -f $up.Days, $up.Hours),
                   ('{0} free:  {1:N1} GB of {2:N1} GB ({3:N0}%)' -f $env:SystemDrive, ($d.FreeSpace / 1GB), ($d.Size / 1GB), ($d.FreeSpace / $d.Size * 100)),
                   "Desktop compositor (dwm.exe) crashes, 7 days:  $dwm",
                   "Display driver resets, 7 days:                 $tdr",
                   "Unexpected reboots, 30 days:                   $kp"
               ) -join "`n"
           } finally { [Windows.Input.Mouse]::OverrideCursor = $null }
           [Windows.Clipboard]::SetText($txt)
           [void][Windows.MessageBox]::Show($txt + "`n`n(copied to clipboard)", 'Quick health snapshot', 'OK', 'Information')
           Say 'Snapshot copied to the clipboard.'
       } }
    @{ Name = 'Open Startup Apps'; Color = '#F2CC60'
       Info = 'Opens Settings > Apps > Startup so you can switch off things you do not need at login (launchers, updaters). Fewer startup apps = faster boot and a lighter desktop.'
       Do = { Start-Process 'ms-settings:startupapps'; Say 'Opened Startup Apps.' } }
    @{ Name = 'Deep scan + auto-repair'; Color = '#FF7B72'
       Info = 'Admin. Scans for malware (Defender), Windows file corruption (DISM + SFC), disk errors, bad startup entries and error-log patterns, then fixes what it safely can and writes a plain-language report. Takes 10-40 minutes.'
       Do = {
           $sp = Join-Path $PSScriptRoot 'scripts\Deep-Scan-And-Repair.ps1'
           if (-not (Test-Path -LiteralPath $sp)) { Say 'The scripts folder is missing next to the launcher.'; return }
           $ok = [Windows.MessageBox]::Show("This opens an ADMIN window (Windows will show a UAC prompt) and runs a full scan with automatic repair.`n`nIt can take 10-40 minutes. It only changes things that have a well-known safe fix, and logs everything it does.`n`nContinue?", 'Deep scan + auto-repair', 'YesNo', 'Question')
           if ($ok -ne 'Yes') { Say 'Cancelled.'; return }
           try { Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$sp`""); Busy-Anim; Say 'Deep scan opened in an admin window. The report opens when it finishes.' }
           catch { Say 'Admin prompt was declined - nothing was run.' }
       } }
    @{ Name = 'Full health report'; Color = '#7EE787'
       Info = 'Read-only. Looks at crashes, hangs, startup entries, memory/handle hogs and error-log patterns, then opens a short report. Changes nothing and needs no admin.'
       Do = {
           $sp = Join-Path $PSScriptRoot 'scripts\Deep-Scan-And-Repair.ps1'
           if (-not (Test-Path -LiteralPath $sp)) { Say 'The scripts folder is missing next to the launcher.'; return }
           Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Minimized', '-File', "`"$sp`"", '-ReportOnly', '-Quick', '-NoElevate', '-NoPause')
           Busy-Anim; Say 'Building the report - it opens in Notepad in about a minute.'
       } }
    @{ Name = 'Clean broken startup items'; Color = '#F2CC60'
       Info = 'Finds startup entries whose program no longer exists (uninstalled or moved) and removes them for your account. A backup you can double-click to undo is saved first. Entries you disabled on purpose are left alone.'
       Do = {
           $sp = Join-Path $PSScriptRoot 'scripts\Startup-Audit.ps1'
           if (-not (Test-Path -LiteralPath $sp)) { Say 'The scripts folder is missing next to the launcher.'; return }
           $ok = [Windows.MessageBox]::Show('Remove startup entries whose program no longer exists? A backup is saved first.', 'Clean broken startup items', 'YesNo', 'Question')
           if ($ok -ne 'Yes') { Say 'Cancelled.'; return }
           Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$sp`"", '-Apply')
           Busy-Anim; Say 'Startup clean-up opened in a window.'
       } }
    @{ Name = 'Restart Razer Synapse'; Color = '#79B8FF'; Confirm = 'Restart Razer Synapse? Its lighting/macro settings keep working; the window closes and reopens.'
       Info = 'Razer Synapse can leak handles and slow the whole PC over days. This closes its background processes and starts them fresh. Does not touch drivers or your settings.'
       Do = {
           Get-Process -Name 'RazerAppEngine', 'RzDLLService*', RzEngineMon -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
           Start-Sleep -Seconds 2
           $exe = "$env:ProgramFiles\Razer\RazerAppEngine\RazerAppEngine.exe"
           if (Test-Path -LiteralPath $exe) { Start-Process $exe -ArgumentList '--url-params=apps=synapse', '--launch-force-hidden=synapse' }
           Busy-Anim; Say 'Razer Synapse restarted.'
       } }
    @{ Name = 'Rebuild icon + thumbnail cache'; Color = '#D2A8FF'; Confirm = 'Rebuild the icon and thumbnail caches? The taskbar and any File Explorer windows will close and reopen.'
       Info = 'Fixes blank, wrong or missing icons and thumbnails. Deletes only the cache files Windows rebuilds on its own.'
       Do = {
           Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
           Start-Sleep -Seconds 2
           Remove-Item "$env:LOCALAPPDATA\Microsoft\Windows\Explorer\iconcache*", "$env:LOCALAPPDATA\Microsoft\Windows\Explorer\thumbcache*" -Force -ErrorAction SilentlyContinue
           Start-Process explorer.exe
           Busy-Anim; Say 'Caches rebuilt - icons refill over a few seconds.'
       } }
    @{ Name = 'Open Reliability Monitor'; Color = '#F2CC60'
       Info = 'Opens Windows Reliability Monitor: a day-by-day chart of app crashes, hangs and failed updates. The quickest way to see which program is behind your freezes.'
       Do = { Start-Process perfmon.exe -ArgumentList '/rel'; Say 'Opened Reliability Monitor.' } }
    @{ Name = 'Restart PC (real reboot)'; Color = '#FF7B72'
       Confirm = 'Restart the PC in 20 seconds? Save your work first. (To cancel, run: shutdown /a)'
       Info = 'A true restart (not Shut down), which fully resets drivers and the graphics stack. Starts a 20 second countdown; cancel any time with: shutdown /a'
       Do = { & shutdown.exe /r /t 20 /c 'Restart requested by PC Repair Launcher. Run shutdown /a to cancel.'; Say 'Restarting in 20 seconds. Run "shutdown /a" to cancel.' } }
)

function Invoke-RepairTool($tool) {
    if ($tool.Confirm) {
        $r = [Windows.MessageBox]::Show($tool.Confirm, $tool.Name, 'YesNo', 'Question')
        if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    }
    try { & $tool.Do } catch { Say ("That failed: " + $_.Exception.Message) }
}


function Stop-AllTools {
    $r = [Windows.MessageBox]::Show('Stop all your tools? (The Kokoro voice service is managed by Kokoro Player and is left running.)', 'Stop all my tools', 'YesNo', 'Question')
    if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    foreach ($t in $script:ToolList) { [void](Stop-Tool $t) }
    Say 'Stopped your tools.'
    if ($script:OpenPanel -eq 'tools') { Start-Sleep -Milliseconds 600; Update-Rows }
}
function Run-HubScript([string]$name, [string[]]$extra, [bool]$visible = $false) {
    $sp = Join-Path $HubDir "scripts\$name"
    if (-not (Test-Path -LiteralPath $sp)) { Say "Missing script: $name"; return $false }
    $al = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$sp`"", '-ToolsFile', "`"$ToolsPath`"") + $extra
    if ($visible) { Start-Process powershell.exe -ArgumentList $al -WindowStyle Normal }
    else { Start-Process (Join-Path $env:SystemRoot 'System32\conhost.exe') -ArgumentList (@('--headless', 'powershell.exe') + $al) -WindowStyle Hidden }   # headless: with Windows Terminal as the default, -WindowStyle Hidden still opens a terminal window
    return $true
}
function Get-HubAutostart { try { [bool](Get-ItemProperty -Path $RunKey -Name 'AvatarToolbox' -ErrorAction Stop) } catch { $false } }

$Extras = @(
    @{ Name = 'Check my tools'; Color = '#7EE787'
       Info = 'Read-only. Checks that each tool exists and is not running twice, that the voice service and RapidOCR answer, how full the GPU is, and how big the logs are. Opens a short report. Changes nothing.'
       Do = { if (Run-HubScript 'Tool-Health.ps1' @('-OpenReport')) { Busy-Anim; Say 'Checking - the report opens in Notepad in a few seconds.' } } }
    @{ Name = 'Check + auto-repair my tools'; Color = '#FF7B72'
       Confirm = 'Fix what can be fixed safely? This closes duplicate copies of a script (keeping the newest), starts the voice service or RapidOCR helper if they are down, rotates huge logs and removes stale temp files. It never touches your scripts or settings.'
       Info = 'Same check, then repairs the safe things: duplicate script copies, a stopped voice service or RapidOCR helper, oversized logs, stale temp files. Never edits your scripts.'
       Do = { if (Run-HubScript 'Tool-Health.ps1' @('-Fix', '-OpenReport')) { Busy-Anim; Say 'Repairing - the report opens in Notepad when it is done.' } } }
    @{ Name = 'Voice + OCR report'; Color = '#79B8FF'
       Info = 'Reads the Game Voice Reader log and shows how fast OCR was per engine (Windows vs RapidOCR), how long each voice took to start speaking, and when the fast-voice fallback kicked in. Shows your timings only - never the text you read.'
       Do = { if (Run-HubScript 'Voice-OCR-Report.ps1' @('-OpenReport')) { Busy-Anim; Say 'Building the report - it opens in Notepad.' } } }
    @{ Name = 'Free GPU for gaming'; Color = '#F2CC60'
       Confirm = 'Unload local AI models (Ollama) from the GPU? They reload by themselves the next time something uses them. The Kokoro voice service is not touched.'
       Info = 'Unloads local AI models (Ollama) so your game and the voice get the GPU memory. They reload automatically when next used. Does not touch the Kokoro voice service.'
       Do = {
           $ol = (Get-Command ollama -ErrorAction SilentlyContinue).Source
           if (-not $ol) { $ol = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
           if (-not (Test-Path -LiteralPath $ol)) { Say 'Ollama was not found - nothing to unload.'; return }
           $names = @(& $ol ps 2>$null | Select-Object -Skip 1 | ForEach-Object { ($_ -split '\s+')[0] } | Where-Object { $_ })
           foreach ($n in $names) { & $ol stop $n 2>$null | Out-Null }
           Busy-Anim; Say $(if ($names.Count) { "Unloaded $($names.Count) AI model(s) from the GPU." } else { 'No AI models were loaded.' })
       } }
    @{ Name = 'Start the voice service'; Color = '#7EE787'
       Info = 'Starts the Kokoro voice service (the same command Kokoro Player uses) if it is not already running. Game Voice Reader needs it for Kokoro voices.'
       Do = {
           $t = $script:ToolList | Where-Object { $_.id -eq 'kokoro' } | Select-Object -First 1
           if (-not $t) { Say 'No Kokoro entry in tools.json.'; return }
           Busy-Anim; Say (Start-Tool $t)
       } }
    @{ Name = 'Back up my tool settings'; Color = '#D2A8FF'
       Info = 'Zips your scripts, settings (.ini) and tools.json into the _backups folder next to your scripts, so a bad edit is one click to undo. Skips the Python environment.'
       Do = {
           $sp = Join-Path $HubDir 'scripts\Backup-Settings.ps1'
           $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sp -ToolsFile $ToolsPath 2>&1 | Select-Object -Last 1
           Busy-Anim; Say "Backup saved: $out"
       } }
    @{ Name = 'Open Kokoro Player'; Color = '#79B8FF'
       Info = 'Opens the Kokoro Player window from its Desktop shortcut.'
       Do = {
           $l = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Kokoro Player.lnk'
           if (Test-Path -LiteralPath $l) { Start-Process $l; Say 'Opening Kokoro Player.' } else { Say 'The Kokoro Player shortcut was not found on the Desktop.' }
       } }
    @{ Name = 'Hotkey cheat sheet'; Color = '#F2CC60'
       Info = 'One list of every hotkey from your tools, so you can see at a glance what each key does (and spot clashes).'
       Do = {
           $lines = foreach ($t in $script:ToolList) { if ($t.hotkeys) { "{0}`n    {1}" -f $t.name, $t.hotkeys } }
           $lines += "Kokoro Player`n    F20 = read the highlighted text"
           $lines += "Desktop Pixel Companions`n    Ctrl+Shift+F10 read screen   Ctrl+Shift+F11 talk   Ctrl+Shift+F12 quiet`n    Ctrl+Alt+1 / 2 / 3 = polish selected text (up to 1,200 characters)"
           [void][Windows.MessageBox]::Show(($lines -join "`n`n"), 'Hotkey cheat sheet', 'OK', 'Information')
       } }
    @{ Name = 'Start the toolbox when I sign in'; Color = '#D2A8FF'; Toggle = 'autostart'
       Info = 'Starts this widget at login (it only shows Jaina; nothing else runs unless you tick "auto" on a tool). Click again to turn it off.'
       Do = {
           if (Get-HubAutostart) { Remove-ItemProperty -Path $RunKey -Name 'AvatarToolbox' -ErrorAction SilentlyContinue; Say 'The toolbox will no longer start at sign-in.' }
           else {
               $cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "{0}"' -f (Join-Path $HubDir 'Avatar-Toolbox.ps1')
               Set-ItemProperty -Path $RunKey -Name 'AvatarToolbox' -Value $cmd; Say 'The toolbox will start at sign-in.'
           }
       } }
    @{ Name = 'Edit my tools list'; Color = '#F2CC60'
       Info = 'Opens tools.json in Notepad. Copy one of the blocks to add another script or program, save, then close and reopen the toolbox.'
       Do = { Start-Process notepad.exe -ArgumentList "`"$ToolsPath`""; Say 'Opened tools.json.' } }
)

function Invoke-Extra($x) {
    if ($x.Confirm) {
        $r = [Windows.MessageBox]::Show($x.Confirm, $x.Name, 'YesNo', 'Question')
        if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    }
    try { & $x.Do } catch { Say ('That failed: ' + $_.Exception.Message) }
}

# ---------------------------------------------------------------- window
Load-Tools
$win = Parse-Xaml @"
<Window $NS WindowStyle="None" AllowsTransparency="True" Background="Transparent" Topmost="True"
        ShowInTaskbar="False" SizeToContent="WidthAndHeight" ResizeMode="NoResize" Title="Avatar Toolbox">
  <Border x:Name="root" Margin="6" Padding="0">
    <StackPanel x:Name="stack" Width="520"/>
  </Border>
</Window>
"@
$stack = $win.FindName('stack'); $root = $win.FindName('root')

# --- avatar row: [2 buttons] [avatar] [2 buttons]
$row = New-Object Windows.Controls.Grid
$c0 = New-Object Windows.Controls.ColumnDefinition; $c0.Width = '*'
$c1 = New-Object Windows.Controls.ColumnDefinition; $c1.Width = [Windows.GridLength]$FrameW
$c2 = New-Object Windows.Controls.ColumnDefinition; $c2.Width = '*'
$row.ColumnDefinitions.Add($c0); $row.ColumnDefinitions.Add($c1); $row.ColumnDefinitions.Add($c2)
$leftCol  = New-Object Windows.Controls.StackPanel; $leftCol.VerticalAlignment = 'Center';  $leftCol.Margin  = '0,0,6,0'
$rightCol = New-Object Windows.Controls.StackPanel; $rightCol.VerticalAlignment = 'Center'; $rightCol.Margin = '6,0,0,0'
[Windows.Controls.Grid]::SetColumn($leftCol, 0); [Windows.Controls.Grid]::SetColumn($rightCol, 2)
$btnTools  = New-Btn 'My tools and checks' $G_INFO '#F0F0F3'
$btnStart  = New-Btn 'Start all my tools'  $G_ALL  '#7EE787'
$btnRepair = New-Btn 'PC repair and fixes'  $G_DOWN '#FF7B72'
$btnAvatar = New-Btn 'Choose avatar'        $G_DOWN '#D2A8FF'
[void]$leftCol.Children.Add($btnTools);   [void]$leftCol.Children.Add($btnStart)
[void]$rightCol.Children.Add($btnRepair); [void]$rightCol.Children.Add($btnAvatar)

# --- avatars: avatars\<id>.png is a strip of 18 frames (126x150): 0-11 idle and gestures, 12-17 walk
$AvatarList = @()
$catalogPath = Join-Path $AvatarDir 'avatars.json'
if (Test-Path -LiteralPath $catalogPath) { try { foreach ($x in ([IO.File]::ReadAllText($catalogPath, [Text.Encoding]::UTF8) | ConvertFrom-Json)) { $AvatarList += $x } } catch { } }
if (-not $AvatarList.Count) { $AvatarList = @([pscustomobject]@{ id = 'neutral'; name = 'Orbit' }) }
$script:AvatarCache = @{}
function Load-Avatar([string]$id) {
    if ($script:AvatarCache.ContainsKey($id)) { return $true }
    $p = Join-Path $AvatarDir "$id.png"
    if (-not (Test-Path -LiteralPath $p)) {
        if ($id -ne 'neutral') { return $false }
        $visual = New-Object Windows.Media.DrawingVisual
        $draw = $visual.RenderOpen()
        $draw.DrawRoundedRectangle((Brush '#161618'),(New-Object Windows.Media.Pen (Brush '#E0A15A'),2),([Windows.Rect]::new(22,25,82,102)),18,18)
        $draw.DrawRoundedRectangle((Brush '#242426'),$null,([Windows.Rect]::new(32,42,62,44)),8,8)
        $draw.DrawEllipse((Brush '#F4EDE3'),$null,([Windows.Point]::new(48,62)),5,7)
        $draw.DrawEllipse((Brush '#F4EDE3'),$null,([Windows.Point]::new(78,62)),5,7)
        $draw.DrawRoundedRectangle((Brush '#E0A15A'),$null,([Windows.Rect]::new(48,101,30,5)),2,2)
        $draw.Close()
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap 126,150,96,96,([Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual); $bitmap.Freeze()
        $script:AvatarCache[$id] = @($bitmap) * 18
        return $true
    }
    $img = New-Object Windows.Media.Imaging.BitmapImage
    $img.BeginInit(); $img.UriSource = New-Object Uri $p; $img.CacheOption = 'OnLoad'; $img.EndInit(); $img.Freeze()
    $fr = New-Object Collections.Generic.List[object]
    foreach ($i in 0..17) {
        $f = New-Object Windows.Media.Imaging.CroppedBitmap ($img, (New-Object Windows.Int32Rect ($i * $FrameW), 0, $FrameW, $FrameH))
        $f.Freeze(); $fr.Add($f)
    }
    $script:AvatarCache[$id] = $fr.ToArray()
    return $true
}
function Get-SavedAvatar { if (-not (Test-Path -LiteralPath $SettingsPath)) { return $null }; try { ([IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json).avatar } catch { $null } }
$script:AvatarId = Get-SavedAvatar
if (-not $script:AvatarId -or -not (Load-Avatar $script:AvatarId)) { $script:AvatarId = $AvatarList[0].id; [void](Load-Avatar $script:AvatarId) }
$script:Frames = $script:AvatarCache[$script:AvatarId]
function Set-Avatar([string]$id) {
    if (-not (Load-Avatar $id)) { Say "Missing artwork for $id."; return }
    $script:AvatarId = $id
    $script:Frames = $script:AvatarCache[$id]
    $avatar.Source = $script:Frames[0]
    try { [IO.File]::WriteAllText($SettingsPath, (@{ avatar = $id } | ConvertTo-Json), (New-Object Text.UTF8Encoding $false)) } catch { }
    foreach ($k in @($script:AvatarCells.Keys)) { $script:AvatarCells[$k].BorderBrush = if ($k -eq $id) { Brush '#D2A8FF' } else { Brush '#383A41' } }
    $nm = ($AvatarList | Where-Object { $_.id -eq $id } | Select-Object -First 1).name
    Busy-Anim; Say "Switched to $nm."
}
$avatar = New-Object Windows.Controls.Image
$avatar.Width = $FrameW; $avatar.Height = $FrameH; $avatar.Source = $script:Frames[0]; $avatar.Cursor = 'SizeAll'
[Windows.Media.RenderOptions]::SetBitmapScalingMode($avatar, 'HighQuality')
$avatar.ToolTip = 'Drag to move. Right-click to close the toolbox (your tools keep running).'
$bob = New-Object Windows.Media.TranslateTransform
$avatar.RenderTransform = $bob
$anim = New-Object Windows.Media.Animation.DoubleAnimation (-2.5, 2.5, [Windows.Duration][TimeSpan]::FromMilliseconds(1100))
$anim.AutoReverse = $true; $anim.RepeatBehavior = [Windows.Media.Animation.RepeatBehavior]::Forever
$anim.EasingFunction = New-Object Windows.Media.Animation.SineEase
$bob.BeginAnimation([Windows.Media.TranslateTransform]::YProperty, $anim)
$avWrap = New-Object Windows.Controls.Grid
[Windows.Controls.Grid]::SetColumn($avWrap, 1)
[void]$avWrap.Children.Add($avatar)
$closeBtn = Parse-Xaml @"
<Button $NS Content="x" Width="16" Height="16" FontSize="10" Cursor="Hand" Focusable="False" ToolTip="Close the toolbox"
        HorizontalAlignment="Right" VerticalAlignment="Top" Foreground="#A8ADB7" Background="#161618" BorderBrush="#383A41" Padding="0"/>
"@
[void]$avWrap.Children.Add($closeBtn)
[void]$row.Children.Add($leftCol); [void]$row.Children.Add($avWrap); [void]$row.Children.Add($rightCol)
[void]$stack.Children.Add($row)

# --- status bubble
$script:StatusBox = New-Object Windows.Controls.Border
$script:StatusBox.CornerRadius = 8; $script:StatusBox.Background = Brush '#EE1B1C20'; $script:StatusBox.BorderBrush = Brush '#383A41'
$script:StatusBox.BorderThickness = 1; $script:StatusBox.Padding = '10,5'; $script:StatusBox.Margin = '0,4,0,0'; $script:StatusBox.Visibility = 'Collapsed'
$script:Status = New-Object Windows.Controls.TextBlock
$script:Status.Foreground = Brush '#F2CC60'; $script:Status.FontFamily = 'Consolas'; $script:Status.FontSize = 11
$script:Status.TextWrapping = 'Wrap'; $script:Status.TextAlignment = 'Center'
$script:StatusBox.Child = $script:Status
[void]$stack.Children.Add($script:StatusBox)

function New-Panel {
    $p = New-Object Windows.Controls.Border
    $p.CornerRadius = 12; $p.Background = Brush '#F2141518'; $p.BorderBrush = Brush '#2C2E33'; $p.BorderThickness = 1
    $p.Padding = '14,12'; $p.Margin = '0,6,0,0'; $p.Visibility = 'Collapsed'
    $p
}
function New-Header([string]$title, [string]$sub) {
    $h = New-Object Windows.Controls.TextBlock
    $h.Text = $title; $h.FontSize = 15; $h.FontWeight = 'Bold'; $h.Foreground = Brush '#F0F0F3'; $h.Margin = '0,0,0,2'
    $s = New-Object Windows.Controls.TextBlock
    $s.Text = $sub; $s.Foreground = Brush '#8B8F98'; $s.FontSize = 11; $s.Margin = '0,0,0,8'; $s.TextWrapping = 'Wrap'
    $h; $s
}

# ===== Panel 1: my tools (live status) =====
$script:Rows = @{}
$toolsPanel = New-Panel
$toolsStack = New-Object Windows.Controls.StackPanel
$scroll1 = New-Object Windows.Controls.ScrollViewer
$scroll1.VerticalScrollBarVisibility = 'Auto'; $scroll1.MaxHeight = 440; $scroll1.Content = $toolsStack
$toolsPanel.Child = $scroll1
foreach ($e in (New-Header 'My tools and checks' 'Green = running. Start, stop or restart each tool. Tick "auto" to start a tool whenever the toolbox opens.')) { [void]$toolsStack.Children.Add($e) }
$btnStopAll = New-Btn 'Stop all my tools' $G_STOP '#FF7B72' 200; $btnStopAll.HorizontalAlignment = 'Left'; $btnStopAll.Add_Click({ Stop-AllTools })
[void]$toolsStack.Children.Add($btnStopAll)

foreach ($tool in $script:ToolList) {
    $g = New-Object Windows.Controls.Grid
    $g.Margin = '0,0,0,10'
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = [Windows.GridLength]20
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = '*'
    $cc = New-Object Windows.Controls.ColumnDefinition; $cc.Width = [Windows.GridLength]150
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb); $g.ColumnDefinitions.Add($cc)
    $dot = New-Object Windows.Shapes.Ellipse
    $dot.Width = 11; $dot.Height = 11; $dot.Fill = Brush '#4B4F58'; $dot.VerticalAlignment = 'Top'; $dot.Margin = '0,6,0,0'
    $info = New-Object Windows.Controls.StackPanel
    [Windows.Controls.Grid]::SetColumn($info, 1)
    $nm = New-Object Windows.Controls.TextBlock
    $nm.FontWeight = 'Bold'; $nm.FontSize = 13; $nm.Foreground = Brush '#F0F0F3'
    $r1 = New-Object Windows.Documents.Run $tool.name
    $r2 = New-Object Windows.Documents.Run '  stopped'; $r2.Foreground = Brush '#8B8F98'; $r2.FontWeight = 'Normal'; $r2.FontSize = 11
    [void]$nm.Inlines.Add($r1); [void]$nm.Inlines.Add($r2)
    $ds = New-Object Windows.Controls.TextBlock
    $ds.Text = $tool.info; $ds.TextWrapping = 'Wrap'; $ds.Foreground = Brush '#C8C2B8'; $ds.FontSize = 11.5; $ds.Margin = '0,2,10,0'
    $hk = New-Object Windows.Controls.TextBlock
    $hk.Text = $tool.hotkeys; $hk.TextWrapping = 'Wrap'; $hk.Foreground = Brush '#79B8FF'; $hk.FontFamily = 'Consolas'; $hk.FontSize = 10.5; $hk.Margin = '0,3,10,0'
    [void]$info.Children.Add($nm); [void]$info.Children.Add($ds); [void]$info.Children.Add($hk)
    $btns = New-Object Windows.Controls.StackPanel
    [Windows.Controls.Grid]::SetColumn($btns, 2)
    $toggle = New-Btn 'Start' $G_RUN '#7EE787'; $toggle.Tag = $tool
    $toggle.Add_Click({ param($s, $e) Toggle-Tool $s.Tag })
    $small = New-Object Windows.Controls.StackPanel; $small.Orientation = 'Horizontal'
    $bRe = New-SmallBtn 'restart' 'Stop and start again' '#79B8FF'; $bRe.Tag = $tool
    $bRe.Add_Click({ param($s, $e) Restart-ToolUi $s.Tag })
    $bFo = New-SmallBtn 'folder' 'Open the tool''s folder' '#F2CC60'; $bFo.Tag = $tool
    $bFo.Add_Click({ param($s, $e) Start-Process explorer.exe -ArgumentList ('/select,"{0}"' -f (Expand $s.Tag.path)) })
    [void]$small.Children.Add($bRe); [void]$small.Children.Add($bFo)
    if ($tool.log) {
        $bLg = New-SmallBtn 'log' 'Open the tool''s log' '#D2A8FF'; $bLg.Tag = $tool
        $bLg.Add_Click({ param($s, $e) $lp = Expand $s.Tag.log; if (Test-Path -LiteralPath $lp) { Start-Process notepad.exe -ArgumentList "`"$lp`"" } else { Say 'No log yet.' } })
        [void]$small.Children.Add($bLg)
    }
    $auto = New-Object Windows.Controls.CheckBox
    $auto.Content = 'auto'; $auto.Foreground = Brush '#C8C2B8'; $auto.FontSize = 11; $auto.Margin = '0,6,0,0'; $auto.Tag = $tool
    $auto.ToolTip = 'Start this tool whenever the toolbox opens'
    $auto.IsChecked = [bool]$tool.autostart
    $auto.Add_Click({ param($s, $e)
        $v = [bool]$s.IsChecked
        if ($s.Tag.PSObject.Properties['autostart']) { $s.Tag.autostart = $v } else { $s.Tag | Add-Member -NotePropertyName autostart -NotePropertyValue $v }
        Save-Tools
    })
    [void]$btns.Children.Add($toggle); [void]$btns.Children.Add($small)
    if ($tool.kind -ne 'service') { [void]$btns.Children.Add($auto) } else { $bRe.IsEnabled = $false }
    [void]$g.Children.Add($dot); [void]$g.Children.Add($info); [void]$g.Children.Add($btns)
    [void]$toolsStack.Children.Add($g)
    $script:Rows[$tool.id] = @{ Dot = $dot; State = $r2; Toggle = $toggle; Restart = $bRe }
}
foreach ($e in (New-Header 'Automated checks, reports and repairs' 'Each button does one thing. Anything that changes something asks you first. Nothing here needs administrator permission.')) { $e.Margin = if ($e.FontSize -gt 12) { '0,10,0,2' } else { '0,0,0,8' }; [void]$toolsStack.Children.Add($e) }
foreach ($x in $Extras) {
    $g = New-Object Windows.Controls.Grid
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = [Windows.GridLength]190
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = '*'
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb)
    $b = New-Btn $x.Name $G_RUN $x.Color; $b.Tag = $x; $b.VerticalAlignment = 'Top'
    $b.Add_Click({ param($s, $e) Invoke-Extra $s.Tag })
    $d = New-Object Windows.Controls.TextBlock
    $d.Text = $x.Info; $d.TextWrapping = 'Wrap'; $d.Foreground = Brush '#C8C2B8'; $d.FontSize = 11.5; $d.Margin = '12,5,0,0'; $d.VerticalAlignment = 'Top'
    [Windows.Controls.Grid]::SetColumn($d, 1)
    [void]$g.Children.Add($b); [void]$g.Children.Add($d)
    [void]$toolsStack.Children.Add($g)
}
[void]$stack.Children.Add($toolsPanel)

function Update-Rows {
    $snap = Get-ProcSnap
    foreach ($t in $script:ToolList) {
        $r = $script:Rows[$t.id]
        if (-not $r) { continue }
        $run = Test-ToolRunning $t $snap
        $r.Dot.Fill = if ($run) { Brush '#7EE787' } else { Brush '#4B4F58' }
        $r.State.Text = if ($run) { '  running' } else { '  stopped' }
        $r.State.Foreground = if ($run) { Brush '#7EE787' } else { Brush '#8B8F98' }
        $lab = $r.Toggle.Content.Children[0]; $gly = $r.Toggle.Content.Children[1]
        if ($run) {
            $lab.Text = if ($t.kind -eq 'service') { 'Running' } else { 'Stop' }
            $gly.Text = $G_STOP; $lab.Foreground = Brush '#FF7B72'; $gly.Foreground = Brush '#FF7B72'
            $r.Toggle.IsEnabled = ($t.kind -ne 'service')
        } else {
            $lab.Text = 'Start'; $gly.Text = $G_RUN; $lab.Foreground = Brush '#7EE787'; $gly.Foreground = Brush '#7EE787'
            $r.Toggle.IsEnabled = $true
        }
        $r.Restart.IsEnabled = ($t.kind -ne 'service')
    }
}
function Toggle-Tool($t) {
    if (Test-ToolRunning $t (Get-ProcSnap)) { [void](Stop-Tool $t); Say "$($t.name) stopped." }
    else { $m = Start-Tool $t; Busy-Anim; Say $(if ($m -eq 'started') { "$($t.name) started." } else { "$($t.name): $m" }) }
    Update-Rows
}
function Restart-ToolUi($t) {
    [void](Stop-Tool $t); [void](Wait-Tool $t $false 6)
    $m = Start-Tool $t; Busy-Anim
    Say $(if ($m -eq 'started') { "$($t.name) restarted." } else { "$($t.name): $m" })
    Update-Rows
}

# ===== Panel 2: PC repair and fixes (the Jaina repair widget) =====
$repairPanel = New-Panel
$infoStack = New-Object Windows.Controls.StackPanel
$scrollR = New-Object Windows.Controls.ScrollViewer
$scrollR.VerticalScrollBarVisibility = 'Auto'; $scrollR.MaxHeight = 460; $scrollR.Content = $infoStack
$repairPanel.Child = $scrollR
$h = New-Object Windows.Controls.TextBlock
$h.Text = 'What I need you to do'; $h.FontSize = 15; $h.FontWeight = 'Bold'; $h.Foreground = Brush '#F0F0F3'; $h.Margin = '0,0,0,8'
[void]$infoStack.Children.Add($h)

function Add-Step([int]$n, $parts) {
    $tb = New-Object Windows.Controls.TextBlock
    $tb.TextWrapping = 'Wrap'; $tb.Foreground = Brush '#F4EDE3'; $tb.FontSize = 12.5; $tb.Margin = '0,0,0,6'
    $r = New-Object Windows.Documents.Run "$n.  "; $r.Foreground = Brush '#8B8F98'; [void]$tb.Inlines.Add($r)
    foreach ($p in $parts) {
        $r = New-Object Windows.Documents.Run $p[1]
        switch ($p[0]) {
            'b' { $r.FontWeight = 'Bold'; $r.Foreground = Brush '#F0F0F3' }
            'c' { $r.FontFamily = 'Consolas'; $r.Background = Brush '#2B2D33'; $r.Foreground = Brush '#F4A6A6' }
        }
        [void]$tb.Inlines.Add($r)
    }
    [void]$infoStack.Children.Add($tb)
}
Add-Step 1 @(@('b', 'Reset the graphics stack.'), @('t', ' Press '), @('c', ' Win + Ctrl + Shift + B '), @('t', '. The screen flickers and '), @('c', ' dwm '), @('t', ' re-initialises.'))
Add-Step 2 @(@('b', 'Chrome:'), @('t', " go to Settings $ArrowR System, turn off `"Use graphics acceleration when available`", and relaunch. If images come back, the GPU path is the culprit."))
Add-Step 3 @(@('b', 'Do a real Restart, not Shut down.'), @('t', " Fast startup is partly failing, so Shut down doesn't fully reset anything."))
Add-Step 4 @(@('b', 'Run the repair in an admin terminal.'), @('t', ' Use the three commands below - or press '), @('c', ' Run in Terminal '), @('t', ' above to run them all in order.'))

foreach ($cmd in $Commands) {
    $box = New-Object Windows.Controls.Border
    $box.CornerRadius = 8; $box.Background = Brush '#1B1C20'; $box.BorderBrush = Brush '#34363C'; $box.BorderThickness = 1
    $box.Padding = '12,6,6,6'; $box.Margin = '0,6,0,0'
    $g = New-Object Windows.Controls.Grid
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = '*'
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = 'Auto'
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb)
    $t = New-Object Windows.Controls.TextBlock
    $t.FontFamily = 'Consolas'; $t.FontSize = 13; $t.VerticalAlignment = 'Center'; $t.TextWrapping = 'Wrap'
    $first, $rest = $cmd -split ' ', 2
    $r1 = New-Object Windows.Documents.Run $first; $r1.Foreground = Brush '#79B8FF'
    $r2 = New-Object Windows.Documents.Run (' ' + $rest); $r2.Foreground = Brush '#85E89D'
    [void]$t.Inlines.Add($r1); [void]$t.Inlines.Add($r2)
    $icons = New-Object Windows.Controls.StackPanel; $icons.Orientation = 'Horizontal'
    [Windows.Controls.Grid]::SetColumn($icons, 1)
    $ib1 = New-IconBtn $G_RUN  'Run this command in an admin terminal' '#7EE787'; $ib1.Tag = $cmd; $ib1.Name = 'run'
    $ib2 = New-IconBtn $G_COPY 'Copy this command' '#79B8FF';                      $ib2.Tag = $cmd; $ib2.Name = 'copy'
    $ib1.Add_Click({ param($s, $e) Run-Elevated @($s.Tag) 'Run in admin terminal' })
    $ib2.Add_Click({ param($s, $e) Copy-Text $s.Tag 'the command' })
    [void]$icons.Children.Add($ib1); [void]$icons.Children.Add($ib2)
    [void]$g.Children.Add($t); [void]$g.Children.Add($icons)
    $box.Child = $g
    [void]$infoStack.Children.Add($box)
}

$rbRow = New-Object Windows.Controls.StackPanel; $rbRow.Orientation = 'Horizontal'; $rbRow.Margin = '0,8,0,4'
$btnRun  = New-Btn 'Run in Terminal' $G_RUN  '#7EE787' 190
$btnCopy = New-Btn 'Copy Code'       $G_COPY '#79B8FF' 150
$btnCopy.Margin = '8,3,0,3'
$btnRun.Add_Click({ Run-Elevated $Commands 'Run in Terminal' })
$btnCopy.Add_Click({ Copy-Text ($Commands -join "`r`n") 'all 3 commands' })
[void]$rbRow.Children.Add($btnRun); [void]$rbRow.Children.Add($btnCopy)
[void]$infoStack.Children.Add($rbRow)
foreach ($e in (New-Header 'More automated tools and automated repairs' 'Each button does one thing. Anything risky asks you first. Only the deep scan asks for administrator permission.')) { $e.Margin = if ($e.FontSize -gt 12) { '0,10,0,2' } else { '0,0,0,8' }; [void]$infoStack.Children.Add($e) }
foreach ($tool in $RepairTools) {
    $g = New-Object Windows.Controls.Grid
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = [Windows.GridLength]190
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = '*'
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb)
    $b = New-Btn $tool.Name $G_RUN $tool.Color; $b.Tag = $tool; $b.VerticalAlignment = 'Top'
    $b.Add_Click({ param($s, $e) Invoke-RepairTool $s.Tag })
    $d = New-Object Windows.Controls.TextBlock
    $d.Text = $tool.Info; $d.TextWrapping = 'Wrap'; $d.Foreground = Brush '#C8C2B8'; $d.FontSize = 11.5; $d.Margin = '12,5,0,0'; $d.VerticalAlignment = 'Top'
    [Windows.Controls.Grid]::SetColumn($d, 1)
    [void]$g.Children.Add($b); [void]$g.Children.Add($d)
    [void]$infoStack.Children.Add($g)
}
[void]$stack.Children.Add($repairPanel)

# ===== Panel 3: choose avatar (built the first time it is opened) =====
$avatarPanel = New-Panel
$avStack = New-Object Windows.Controls.StackPanel
$avatarPanel.Child = $avStack
foreach ($e in (New-Header 'Choose avatar' 'Click a companion to swap instantly. Your choice is remembered.')) { [void]$avStack.Children.Add($e) }
$avWrap2 = New-Object Windows.Controls.WrapPanel
[void]$avStack.Children.Add($avWrap2)
[void]$stack.Children.Add($avatarPanel)
function Ensure-AvatarPanel {
    if ($script:AvatarPanelBuilt) { return }
    $script:AvatarPanelBuilt = $true
    foreach ($a in $AvatarList) {
        if (-not (Load-Avatar $a.id)) { continue }
        $cell = New-Object Windows.Controls.Border
        $cell.Width = 108; $cell.CornerRadius = 9; $cell.Background = Brush '#161618'; $cell.BorderThickness = 2
        $cell.BorderBrush = if ($a.id -eq $script:AvatarId) { Brush '#D2A8FF' } else { Brush '#383A41' }
        $cell.Padding = '4,6,4,4'; $cell.Margin = '4'; $cell.Cursor = 'Hand'; $cell.Tag = $a.id
        $sp = New-Object Windows.Controls.StackPanel
        $im = New-Object Windows.Controls.Image
        $im.Width = 76; $im.Height = 90; $im.Source = $script:AvatarCache[$a.id][2]
        [Windows.Media.RenderOptions]::SetBitmapScalingMode($im, 'HighQuality')
        $tb = New-Object Windows.Controls.TextBlock
        $tb.Text = $a.name; $tb.Foreground = Brush '#F4EDE3'; $tb.FontFamily = 'Consolas'; $tb.FontSize = 12; $tb.TextAlignment = 'Center'; $tb.Margin = '0,4,0,0'
        [void]$sp.Children.Add($im); [void]$sp.Children.Add($tb)
        $cell.Child = $sp
        $cell.Add_MouseLeftButtonUp({ param($s, $e) Set-Avatar $s.Tag })
        $cell.Add_MouseEnter({ param($s, $e) $s.Background = Brush '#242426' })
        $cell.Add_MouseLeave({ param($s, $e) $s.Background = Brush '#161618' })
        $script:AvatarCells[$a.id] = $cell
        [void]$avWrap2.Children.Add($cell)
    }
}

# ---------------------------------------------------------------- behaviour
$script:OpenPanel = ''
function Set-Panel([string]$which) {
    if ($script:OpenPanel -eq $which) { $which = '' }
    $script:OpenPanel = $which
    if ($which -eq 'avatar') { Ensure-AvatarPanel }
    $toolsPanel.Visibility  = if ($which -eq 'tools')  { 'Visible' } else { 'Collapsed' }
    $repairPanel.Visibility = if ($which -eq 'repair') { 'Visible' } else { 'Collapsed' }
    $avatarPanel.Visibility = if ($which -eq 'avatar') { 'Visible' } else { 'Collapsed' }
    if ($which -eq 'tools') { Update-Rows; $script:RowTimer.Start() } else { $script:RowTimer.Stop() }
}
$btnTools.Add_Click({ Set-Panel 'tools' })
$btnRepair.Add_Click({ Set-Panel 'repair' })
$btnAvatar.Add_Click({ Set-Panel 'avatar' })
$btnStart.Add_Click({
    $n = 0; $bad = @()
    foreach ($t in $script:ToolList) {
        $m = Start-Tool $t
        if ($m -eq 'started') { $n++ } elseif ($m -ne 'already running') { $bad += "$($t.name): $m" }
    }
    Busy-Anim
    Say $(if ($bad.Count) { ($bad -join '; ') } elseif ($n) { "Started $n tool(s)." } else { 'Everything was already running.' })
    if ($script:OpenPanel -eq 'tools') { Start-Sleep -Milliseconds 800; Update-Rows }
})
$closeBtn.Add_Click({ $win.Close() })
$avatar.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch {} })
$menu = New-Object Windows.Controls.ContextMenu
$mi = New-Object Windows.Controls.MenuItem; $mi.Header = 'Close the toolbox (tools keep running)'; $mi.Add_Click({ $win.Close() })
$miAv = New-Object Windows.Controls.MenuItem; $miAv.Header = 'Change avatar'
foreach ($a in $AvatarList) {
    $m2 = New-Object Windows.Controls.MenuItem; $m2.Header = $a.name; $m2.Tag = $a.id
    $m2.Add_Click({ param($s, $e) Set-Avatar $s.Tag })
    [void]$miAv.Items.Add($m2)
}
[void]$menu.Items.Add($miAv); [void]$menu.Items.Add($mi); $avatar.ContextMenu = $menu

$win.Add_SizeChanged({
    $wa = [Windows.SystemParameters]::WorkArea
    if ($win.Top + $win.ActualHeight -gt $wa.Bottom) { $win.Top = [Math]::Max($wa.Top, $wa.Bottom - $win.ActualHeight) }
    if ($win.Left + $win.ActualWidth -gt $wa.Right)  { $win.Left = [Math]::Max($wa.Left, $wa.Right - $win.ActualWidth) }
})

$idle = 0, 0, 1, 0, 2, 0, 3, 4, 5, 3, 6, 7, 8, 0, 9, 10, 11, 10, 9
$script:IdleI = 0; $script:WalkI = 0; $script:WalkUntil = [datetime]::MinValue
$frameTimer = New-Object Windows.Threading.DispatcherTimer
$frameTimer.Interval = [TimeSpan]::FromMilliseconds(380)
$frameTimer.Add_Tick({
    if ((Get-Date) -lt $script:WalkUntil) { $avatar.Source = $script:Frames[12 + ($script:WalkI % 6)]; $script:WalkI++ }
    else { $avatar.Source = $script:Frames[$idle[$script:IdleI % $idle.Count]]; $script:IdleI++ }
})
$script:StatusTimer = New-Object Windows.Threading.DispatcherTimer
$script:StatusTimer.Interval = [TimeSpan]::FromSeconds(9)
$script:StatusTimer.Add_Tick({ $script:StatusTimer.Stop(); Say '' })
$script:RowTimer = New-Object Windows.Threading.DispatcherTimer
$script:RowTimer.Interval = [TimeSpan]::FromSeconds(3)
$script:RowTimer.Add_Tick({ Update-Rows })

$wa = [Windows.SystemParameters]::WorkArea
$win.WindowStartupLocation = 'Manual'
$win.Left = $wa.Right - 540; $win.Top = $wa.Bottom - 240

if ($Snapshot) {
    # test mode: draw the widget on the laptop display (never the main one), save a PNG, exit
    $win.Left = 2000; $win.Top = 40
    if ($Open -eq 'tools') { Set-Panel 'tools' } elseif ($Open -eq 'repair') { Set-Panel 'repair' } elseif ($Open -eq 'avatar') { Set-Panel 'avatar' }
    if ($PickAvatar) { Set-Avatar $PickAvatar }
    $avatar.Source = $script:Frames[11]
    $win.Add_ContentRendered({
        $win.Dispatcher.BeginInvoke([Action]{
            $root.UpdateLayout()
            $w = [int]$root.ActualWidth; $hh = [int]$root.ActualHeight
            $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ($w, $hh, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
            $rtb.Render($root)
            $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
            $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
            $fs = [IO.File]::Create($Snapshot); $enc.Save($fs); $fs.Close()
            if ($Error.Count) { [IO.File]::WriteAllLines($Snapshot + '.errors.txt', @($Error | ForEach-Object { $_.ToString() })) }
            $win.Close()
        }, [Windows.Threading.DispatcherPriority]::ApplicationIdle) | Out-Null
    })
}
else {
    $frameTimer.Start()
    $win.Add_ContentRendered({
        if ($script:AutoStarted) { return }
        $script:AutoStarted = $true
        $n = 0
        foreach ($t in $script:ToolList) { if ($t.autostart) { if ((Start-Tool $t) -eq 'started') { $n++ } } }
        if ($n) { Busy-Anim; Say "Started $n tool(s) marked auto." }
    })
}

[void]$win.ShowDialog()
if ($Error.Count -and -not $Snapshot) {
    try {
        $d = Join-Path $HubDir 'reports'; New-Item -ItemType Directory -Path $d -Force | Out-Null
        $lines = @('--- ' + (Get-Date -Format s) + '  ' + $Error.Count + ' error(s) in this session') + @($Error | Select-Object -First 20 | ForEach-Object { $_.ToString() })
        Add-Content -LiteralPath (Join-Path $d 'toolbox-errors.log') -Value $lines -Encoding UTF8
    } catch { }
}
