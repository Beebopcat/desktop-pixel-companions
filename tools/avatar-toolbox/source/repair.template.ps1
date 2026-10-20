<#
  PC Repair Launcher  -  a tiny always-on-top desktop companion (Jaina) with one-click repair helpers.
  Run:  double-click Start-PC-Repair-Launcher.cmd   (or: powershell -STA -File PC-Repair-Launcher.ps1)
  Nothing runs until you click a button. Anything that needs admin shows a Windows UAC prompt first.
  Right-click Jaina (or press the x) to close.
  Test switches:  -Snapshot out.png [-Open info|tools]   renders the widget to a PNG and exits.
#>
param([string]$Snapshot = '', [string]$Open = '')

# WPF needs a single-threaded apartment. Re-launch ourselves in one if needed.
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', "`"$PSCommandPath`"")
    if ($Snapshot) { $argList += @('-Snapshot', "`"$Snapshot`"", '-Open', $Open) }
    Start-Process powershell.exe -ArgumentList $argList -WindowStyle Hidden
    return
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms

# Only one widget at a time (e.g. when opened from Desktop Pixel Companions and from the Desktop shortcut).
$script:instanceCreated = $false
$script:instanceMutex = New-Object Threading.Mutex($true, 'Local\AvatarRepairLauncher', [ref]$script:instanceCreated)
if (-not $script:instanceCreated -and -not $Snapshot) { return }

# ---------------------------------------------------------------- settings
$FrameW = 126; $FrameH = 150                      # one sprite frame (pixels)
$ArrowR = [string][char]0x2192
$G_RUN  = [string][char]0x25B7                    # white right triangle
$G_COPY = [string][char]0x29C9                    # two joined squares
$G_INFO = [string][char]0x24D8                    # circled i
$G_DOWN = [string][char]0x25BE
$G_UP   = [string][char]0x25B4

$Commands = @(
    'sfc /scannow',
    'DISM /Online /Cleanup-Image /RestoreHealth',
    'vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10%'
)

$SpriteB64 = @'

'@

# ---------------------------------------------------------------- helpers
$bc = New-Object Windows.Media.BrushConverter
function Brush([string]$hex) { $bc.ConvertFromString($hex) }
function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
function Parse-Xaml([string]$x) { [Windows.Markup.XamlReader]::Parse($x) }
$NS = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'

# Dark rounded "box" button: label on the left, glyph on the right (same look as the code boxes).
function New-Btn([string]$text, [string]$glyph, [string]$color, [double]$width = 0) {
    $w = if ($width -gt 0) { "Width=`"$width`"" } else { '' }
    $btn = Parse-Xaml @"
<Button $NS $w Cursor="Hand" Focusable="False" Margin="0,3,0,3" HorizontalContentAlignment="Stretch">
  <Button.Template>
    <ControlTemplate TargetType="Button">
      <Border x:Name="b" CornerRadius="9" Background="#1E1F23" BorderBrush="#383A41" BorderThickness="1" Padding="10,7">
        <ContentPresenter HorizontalAlignment="Stretch"/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="b" Property="Background" Value="#2A2C32"/>
          <Setter TargetName="b" Property="BorderBrush" Value="$color"/>
        </Trigger>
        <Trigger Property="IsPressed" Value="True">
          <Setter TargetName="b" Property="Background" Value="#131417"/>
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
    $btn
}

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

# ---------------------------------------------------------------- actions
$script:Status = $null
function Say([string]$msg) {
    $script:Status.Text = $msg
    $script:StatusBox.Visibility = if ($msg) { 'Visible' } else { 'Collapsed' }
    $script:StatusTimer.Stop()
    if ($msg) { $script:StatusTimer.Start() }
}
function Busy-Anim { $script:WalkUntil = (Get-Date).AddSeconds(2.5) }

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

$Tools = @(
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

function Invoke-Tool($tool) {
    if ($tool.Confirm) {
        $r = [Windows.MessageBox]::Show($tool.Confirm, $tool.Name, 'YesNo', 'Question')
        if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    }
    try { & $tool.Do } catch { Say ("That failed: " + $_.Exception.Message) }
}

# ---------------------------------------------------------------- window
$win = Parse-Xaml @"
<Window $NS WindowStyle="None" AllowsTransparency="True" Background="Transparent" Topmost="True"
        ShowInTaskbar="False" SizeToContent="WidthAndHeight" ResizeMode="NoResize" Title="PC Repair Launcher">
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

$btnInfo  = New-Btn 'What I need you to do' $G_INFO '#F0F0F3'
$btnRun   = New-Btn 'Run in Terminal'       $G_RUN  '#7EE787'
$btnCopy  = New-Btn 'Copy Code'             $G_COPY '#79B8FF'
$btnMore  = New-Btn 'More automated tools and automated repairs' $G_DOWN '#D2A8FF'
[void]$leftCol.Children.Add($btnInfo);  [void]$leftCol.Children.Add($btnRun)
[void]$rightCol.Children.Add($btnCopy); [void]$rightCol.Children.Add($btnMore)

# --- sprite
$bytes = [Convert]::FromBase64String(($SpriteB64 -replace '\s', ''))
$ms = New-Object IO.MemoryStream (, $bytes)
$sheet = New-Object Windows.Media.Imaging.BitmapImage
$sheet.BeginInit(); $sheet.StreamSource = $ms; $sheet.CacheOption = 'OnLoad'; $sheet.EndInit(); $sheet.Freeze()
$frames = foreach ($i in 0..17) {
    $f = New-Object Windows.Media.Imaging.CroppedBitmap ($sheet, (New-Object Windows.Int32Rect ($i * $FrameW), 0, $FrameW, $FrameH))
    $f.Freeze(); $f
}
$avatar = New-Object Windows.Controls.Image
$avatar.Width = $FrameW; $avatar.Height = $FrameH; $avatar.Source = $frames[0]; $avatar.Cursor = 'SizeAll'
[Windows.Media.RenderOptions]::SetBitmapScalingMode($avatar, 'HighQuality')
$avatar.ToolTip = 'Drag to move. Right-click to close.'
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
<Button $NS Content="x" Width="16" Height="16" FontSize="10" Cursor="Hand" Focusable="False" ToolTip="Close"
        HorizontalAlignment="Right" VerticalAlignment="Top" Foreground="#A8ADB7" Background="#1E1F23" BorderBrush="#383A41" Padding="0"/>
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

# --- panel frame helper
function New-Panel {
    $p = New-Object Windows.Controls.Border
    $p.CornerRadius = 12; $p.Background = Brush '#F2141518'; $p.BorderBrush = Brush '#2C2E33'; $p.BorderThickness = 1
    $p.Padding = '14,12'; $p.Margin = '0,6,0,0'; $p.Visibility = 'Collapsed'
    $p
}

# ===== Panel 1: the information (as in the chat reply) =====
$infoPanel = New-Panel
$infoStack = New-Object Windows.Controls.StackPanel
$infoPanel.Child = $infoStack

$h = New-Object Windows.Controls.TextBlock
$h.Text = 'What I need you to do'; $h.FontSize = 15; $h.FontWeight = 'Bold'; $h.Foreground = Brush '#F0F0F3'; $h.Margin = '0,0,0,8'
[void]$infoStack.Children.Add($h)

function Add-Step([int]$n, $parts) {
    $tb = New-Object Windows.Controls.TextBlock
    $tb.TextWrapping = 'Wrap'; $tb.Foreground = Brush '#D4D4D8'; $tb.FontSize = 12.5; $tb.Margin = '0,0,0,6'
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
[void]$stack.Children.Add($infoPanel)

# ===== Panel 2: more automated tools =====
$toolsPanel = New-Panel
$toolsStack = New-Object Windows.Controls.StackPanel
$scroll = New-Object Windows.Controls.ScrollViewer
$scroll.VerticalScrollBarVisibility = 'Auto'; $scroll.MaxHeight = 440; $scroll.Content = $toolsStack
$toolsPanel.Child = $scroll
$h2 = New-Object Windows.Controls.TextBlock
$h2.Text = 'More automated tools and automated repairs'; $h2.FontSize = 15; $h2.FontWeight = 'Bold'; $h2.Foreground = Brush '#F0F0F3'; $h2.Margin = '0,0,0,2'
$sub = New-Object Windows.Controls.TextBlock
$sub.Text = 'Each button does one thing. Anything risky asks you first. Only the deep scan asks for administrator permission.'
$sub.Foreground = Brush '#8B8F98'; $sub.FontSize = 11; $sub.Margin = '0,0,0,6'; $sub.TextWrapping = 'Wrap'
[void]$toolsStack.Children.Add($h2); [void]$toolsStack.Children.Add($sub)
foreach ($tool in $Tools) {
    $g = New-Object Windows.Controls.Grid
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = [Windows.GridLength]190
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = '*'
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb)
    $b = New-Btn $tool.Name $G_RUN $tool.Color; $b.Tag = $tool; $b.VerticalAlignment = 'Top'
    $b.Add_Click({ param($s, $e) Invoke-Tool $s.Tag })
    $d = New-Object Windows.Controls.TextBlock
    $d.Text = $tool.Info; $d.TextWrapping = 'Wrap'; $d.Foreground = Brush '#9CA3AF'; $d.FontSize = 11.5; $d.Margin = '12,5,0,0'; $d.VerticalAlignment = 'Top'
    [Windows.Controls.Grid]::SetColumn($d, 1)
    [void]$g.Children.Add($b); [void]$g.Children.Add($d)
    [void]$toolsStack.Children.Add($g)
}
[void]$stack.Children.Add($toolsPanel)

# ---------------------------------------------------------------- behaviour
$script:OpenPanel = ''
function Set-Panel([string]$which) {
    if ($script:OpenPanel -eq $which) { $which = '' }
    $script:OpenPanel = $which
    $infoPanel.Visibility  = if ($which -eq 'info')  { 'Visible' } else { 'Collapsed' }
    $toolsPanel.Visibility = if ($which -eq 'tools') { 'Visible' } else { 'Collapsed' }
}
$btnInfo.Add_Click({ Set-Panel 'info' })
$btnMore.Add_Click({ Set-Panel 'tools' })
$btnRun.Add_Click({ Run-Elevated $Commands 'Run in Terminal' })
$btnCopy.Add_Click({ Copy-Text ($Commands -join "`r`n") 'all 3 commands' })
$closeBtn.Add_Click({ $win.Close() })
$avatar.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch {} })

$menu = New-Object Windows.Controls.ContextMenu
$mi = New-Object Windows.Controls.MenuItem; $mi.Header = 'Close launcher'; $mi.Add_Click({ $win.Close() })
[void]$menu.Items.Add($mi); $avatar.ContextMenu = $menu

# keep the window on screen as panels expand
$win.Add_SizeChanged({
    $wa = [Windows.SystemParameters]::WorkArea
    if ($win.Top + $win.ActualHeight -gt $wa.Bottom) { $win.Top = [Math]::Max($wa.Top, $wa.Bottom - $win.ActualHeight) }
    if ($win.Left + $win.ActualWidth -gt $wa.Right)  { $win.Left = [Math]::Max($wa.Left, $wa.Right - $win.ActualWidth) }
})

# animation: idle gestures, walk cycle while a task is running
$idle = 0, 0, 1, 0, 2, 0, 3, 4, 5, 3, 6, 7, 8, 0, 9, 10, 11, 10, 9
$script:IdleI = 0; $script:WalkI = 0; $script:WalkUntil = [datetime]::MinValue
$frameTimer = New-Object Windows.Threading.DispatcherTimer
$frameTimer.Interval = [TimeSpan]::FromMilliseconds(380)
$frameTimer.Add_Tick({
    if ((Get-Date) -lt $script:WalkUntil) { $avatar.Source = $frames[12 + ($script:WalkI % 6)]; $script:WalkI++ }
    else { $avatar.Source = $frames[$idle[$script:IdleI % $idle.Count]]; $script:IdleI++ }
})
$script:StatusTimer = New-Object Windows.Threading.DispatcherTimer
$script:StatusTimer.Interval = [TimeSpan]::FromSeconds(9)
$script:StatusTimer.Add_Tick({ $script:StatusTimer.Stop(); Say '' })

# start bottom-right above the taskbar
$wa = [Windows.SystemParameters]::WorkArea
$win.WindowStartupLocation = 'Manual'
$win.Left = $wa.Right - 540; $win.Top = $wa.Bottom - 240

if ($Snapshot) {
    # test mode: render the widget to a PNG and exit
    if ($Open -eq 'info') { Set-Panel 'info' } elseif ($Open -eq 'tools') { Set-Panel 'tools' }
    $avatar.Source = $frames[11]
    $win.Add_ContentRendered({
        $win.Dispatcher.BeginInvoke([Action]{
            $root.UpdateLayout()
            $w = [int]$root.ActualWidth; $hh = [int]$root.ActualHeight
            $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ($w, $hh, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
            $rtb.Render($root)
            $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
            $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
            $fs = [IO.File]::Create($Snapshot); $enc.Save($fs); $fs.Close()
            $win.Close()
        }, [Windows.Threading.DispatcherPriority]::ApplicationIdle) | Out-Null
    })
}
else { $frameTimer.Start() }

[void]$win.ShowDialog()
