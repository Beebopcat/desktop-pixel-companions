<#
  Avatar Tool Hub  -  a tiny always-on-top desktop companion (Jaina) that starts, stops, checks and repairs YOUR tools with one click.
  Run:  double-click "Avatar Tool Hub" in the AutoHotkey folder   (or: powershell -STA -File Avatar-Tool-Hub.ps1)
  Your tools live in tools.json next to this file - add a block there to add a tool.
  Nothing runs until you click. Right-click Jaina (or press the x) to close the hub; your tools keep running.
  Test switches:  -Snapshot out.png [-Open tools|more]   -ToolsFile other.json   -SelfTest out.txt
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
$script:instanceMutex = New-Object Threading.Mutex($true, 'Local\AvatarToolHub', [ref]$script:instanceCreated)
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

$SpriteB64 = @'
@@SPRITE@@
'@

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
      <Border x:Name="b" CornerRadius="6" Background="#1E1F23" BorderBrush="#383A41" BorderThickness="1" Padding="7,2">
        <TextBlock x:Name="t" Text="$(Esc $text)" FontFamily="Consolas" FontSize="11" Foreground="#A8ADB7"/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter TargetName="b" Property="Background" Value="#2A2C32"/>
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

function Run-HubScript([string]$name, [string[]]$extra, [bool]$visible = $false) {
    $sp = Join-Path $HubDir "scripts\$name"
    if (-not (Test-Path -LiteralPath $sp)) { Say "Missing script: $name"; return $false }
    $al = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$sp`"", '-ToolsFile', "`"$ToolsPath`"") + $extra
    if ($visible) { Start-Process powershell.exe -ArgumentList $al -WindowStyle Normal }
    else { Start-Process (Join-Path $env:SystemRoot 'System32\conhost.exe') -ArgumentList (@('--headless', 'powershell.exe') + $al) -WindowStyle Hidden }   # headless: with Windows Terminal as the default, -WindowStyle Hidden still opens a terminal window
    return $true
}
function Get-HubAutostart { try { [bool](Get-ItemProperty -Path $RunKey -Name 'AvatarToolHub' -ErrorAction Stop) } catch { $false } }

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
    @{ Name = 'Open the PC Repair Launcher'; Color = '#79B8FF'
       Info = 'Opens the Jaina repair widget (deep scan, graphics reset, startup clean-up and more).'
       Do = {
           $l = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Avatar Repair Launcher (Jaina).lnk'
           if (Test-Path -LiteralPath $l) { Start-Process $l; Say 'Opening the repair launcher.' } else { Say 'The repair launcher shortcut was not found on the Desktop.' }
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
    @{ Name = 'Start the hub when I sign in'; Color = '#D2A8FF'; Toggle = 'autostart'
       Info = 'Starts this widget at login (it only shows Jaina; nothing else runs unless you tick "auto" on a tool). Click again to turn it off.'
       Do = {
           if (Get-HubAutostart) { Remove-ItemProperty -Path $RunKey -Name 'AvatarToolHub' -ErrorAction SilentlyContinue; Say 'The hub will no longer start at sign-in.' }
           else {
               $cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "{0}"' -f (Join-Path $HubDir 'Avatar-Tool-Hub.ps1')
               Set-ItemProperty -Path $RunKey -Name 'AvatarToolHub' -Value $cmd; Say 'The hub will start at sign-in.'
           }
       } }
    @{ Name = 'Edit my tools list'; Color = '#F2CC60'
       Info = 'Opens tools.json in Notepad. Copy one of the blocks to add another script or program, save, then close and reopen the hub.'
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
        ShowInTaskbar="False" SizeToContent="WidthAndHeight" ResizeMode="NoResize" Title="Avatar Tool Hub">
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
$btnTools = New-Btn 'My tools'            $G_INFO '#F0F0F3'
$btnStart = New-Btn 'Start all my tools'  $G_ALL  '#7EE787'
$btnStop  = New-Btn 'Stop all my tools'   $G_STOP '#FF7B72'
$btnMore  = New-Btn 'More tools and repairs' $G_DOWN '#D2A8FF'
[void]$leftCol.Children.Add($btnTools); [void]$leftCol.Children.Add($btnStart)
[void]$rightCol.Children.Add($btnStop); [void]$rightCol.Children.Add($btnMore)

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
$avatar.ToolTip = 'Drag to move. Right-click to close the hub (your tools keep running).'
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
<Button $NS Content="x" Width="16" Height="16" FontSize="10" Cursor="Hand" Focusable="False" ToolTip="Close the hub"
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
foreach ($e in (New-Header 'My tools' 'Green = running. Start, stop or restart each one. Tick "auto" to start a tool whenever the hub opens.')) { [void]$toolsStack.Children.Add($e) }

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
    $ds.Text = $tool.info; $ds.TextWrapping = 'Wrap'; $ds.Foreground = Brush '#9CA3AF'; $ds.FontSize = 11.5; $ds.Margin = '0,2,10,0'
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
    $auto.Content = 'auto'; $auto.Foreground = Brush '#9CA3AF'; $auto.FontSize = 11; $auto.Margin = '0,6,0,0'; $auto.Tag = $tool
    $auto.ToolTip = 'Start this tool whenever the hub opens'
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

# ===== Panel 2: more tools and repairs =====
$morePanel = New-Panel
$moreStack = New-Object Windows.Controls.StackPanel
$scroll2 = New-Object Windows.Controls.ScrollViewer
$scroll2.VerticalScrollBarVisibility = 'Auto'; $scroll2.MaxHeight = 440; $scroll2.Content = $moreStack
$morePanel.Child = $scroll2
foreach ($e in (New-Header 'More tools and repairs' 'Each button does one thing. Anything that changes something asks you first. Nothing here needs administrator permission.')) { [void]$moreStack.Children.Add($e) }
foreach ($x in $Extras) {
    $g = New-Object Windows.Controls.Grid
    $ca = New-Object Windows.Controls.ColumnDefinition; $ca.Width = [Windows.GridLength]190
    $cb = New-Object Windows.Controls.ColumnDefinition; $cb.Width = '*'
    $g.ColumnDefinitions.Add($ca); $g.ColumnDefinitions.Add($cb)
    $b = New-Btn $x.Name $G_RUN $x.Color; $b.Tag = $x; $b.VerticalAlignment = 'Top'
    $b.Add_Click({ param($s, $e) Invoke-Extra $s.Tag })
    $d = New-Object Windows.Controls.TextBlock
    $d.Text = $x.Info; $d.TextWrapping = 'Wrap'; $d.Foreground = Brush '#9CA3AF'; $d.FontSize = 11.5; $d.Margin = '12,5,0,0'; $d.VerticalAlignment = 'Top'
    [Windows.Controls.Grid]::SetColumn($d, 1)
    [void]$g.Children.Add($b); [void]$g.Children.Add($d)
    [void]$moreStack.Children.Add($g)
}
[void]$stack.Children.Add($morePanel)

# ---------------------------------------------------------------- behaviour
$script:OpenPanel = ''
function Set-Panel([string]$which) {
    if ($script:OpenPanel -eq $which) { $which = '' }
    $script:OpenPanel = $which
    $toolsPanel.Visibility = if ($which -eq 'tools') { 'Visible' } else { 'Collapsed' }
    $morePanel.Visibility  = if ($which -eq 'more')  { 'Visible' } else { 'Collapsed' }
    if ($which -eq 'tools') { Update-Rows; $script:RowTimer.Start() } else { $script:RowTimer.Stop() }
}
$btnTools.Add_Click({ Set-Panel 'tools' })
$btnMore.Add_Click({ Set-Panel 'more' })
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
$btnStop.Add_Click({
    $r = [Windows.MessageBox]::Show('Stop all your tools? (The Kokoro voice service is managed by Kokoro Player and is left running.)', 'Stop all my tools', 'YesNo', 'Question')
    if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    foreach ($t in $script:ToolList) { [void](Stop-Tool $t) }
    Say 'Stopped your tools.'
    if ($script:OpenPanel -eq 'tools') { Start-Sleep -Milliseconds 600; Update-Rows }
})
$closeBtn.Add_Click({ $win.Close() })
$avatar.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch {} })
$menu = New-Object Windows.Controls.ContextMenu
$mi = New-Object Windows.Controls.MenuItem; $mi.Header = 'Close the hub (tools keep running)'; $mi.Add_Click({ $win.Close() })
[void]$menu.Items.Add($mi); $avatar.ContextMenu = $menu

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
    if ((Get-Date) -lt $script:WalkUntil) { $avatar.Source = $frames[12 + ($script:WalkI % 6)]; $script:WalkI++ }
    else { $avatar.Source = $frames[$idle[$script:IdleI % $idle.Count]]; $script:IdleI++ }
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
    if ($Open -eq 'tools') { Set-Panel 'tools' } elseif ($Open -eq 'more') { Set-Panel 'more' }
    if ($PickAvatar) { Set-Avatar $PickAvatar }
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
