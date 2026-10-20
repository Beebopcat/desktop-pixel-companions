"""Builds Avatar-Toolbox.ps1 from hub.template.ps1 (tools, checks, avatars) + repair.template.ps1 (the Jaina repair widget's tools).
Run from anywhere:  python build_toolbox.py"""
import os
here = os.path.dirname(os.path.abspath(__file__)); root = os.path.dirname(here)
hub = open(os.path.join(here, "hub.template.ps1"), encoding="utf-8").read().replace("\r\n", "\n")
rep = open(os.path.join(here, "repair.template.ps1"), encoding="utf-8").read().replace("\r\n", "\n")


def sub(old, new, count=1):
    global hub
    assert hub.count(old) == count, (hub.count(old), old[:70])
    hub = hub.replace(old, new)


def between(text, start, end):
    a = text.index(start)
    b = text.index(end, a)
    return text[a:b]


# ---- pieces lifted from the Jaina repair widget
commands = between(rep, "$Commands = @(", "$SpriteB64")
repair_defs = between(rep, "function Copy-Text", "# ---------------------------------------------------------------- window")
icon_btn = between(rep, "# Small flat icon button", "# ---------------------------------------------------------------- actions")
repair_defs = repair_defs.replace("$Tools = @(", "$RepairTools = @(").replace("function Invoke-Tool($tool)", "function Invoke-RepairTool($tool)")
info_block = between(rep, "$h = New-Object Windows.Controls.TextBlock\n$h.Text = 'What I need you to do'", "[void]$stack.Children.Add($infoPanel)")

# ---- header, names
a = hub.index("<#")
b = hub.index("#>") + 2
hub = hub[:a] + """<#
  Avatar Toolbox  -  one tiny always-on-top desktop companion with: your tools (start/stop/restart with live status),
  automated checks and repairs, the PC repair helpers, and a picker to swap between all your pixel companions.
  Run:  double-click "Avatar Toolbox" in the AutoHotkey folder (or on the Desktop)   -   powershell -STA -File Avatar-Toolbox.ps1
  Your tools live in tools.json next to this file - add a block there to add a tool.
  Nothing runs until you click. Anything that needs admin shows a Windows UAC prompt first.
  Right-click your companion (or press the x) to close the toolbox; your tools keep running.
  Test switches:  -Snapshot out.png [-Open tools|repair|avatar]   -ToolsFile other.json   -SelfTest out.txt
#>""" + hub[b:]
sub("'Local\\AvatarToolHub'", "'Local\\AvatarToolbox'")
sub("'AvatarToolHub'", "'AvatarToolbox'", 3)
sub("Avatar-Tool-Hub.ps1", "Avatar-Toolbox.ps1")
sub('Title="Avatar Tool Hub"', 'Title="Avatar Toolbox"')

# ---- settings: repair constants, avatar paths (the single baked-in sprite is gone)
sub("$SpriteB64 = @'\n@@SPRITE@@\n'@\n",
    "$G_COPY = [string][char]0x29C9\n$ArrowR = [string][char]0x2192\n" + commands +
    "$AvatarDir = Join-Path $HubDir 'avatars'\n$SettingsPath = Join-Path $HubDir 'settings.json'\n$script:AvatarCells = @{}\n$script:AvatarPanelBuilt = $false\n")

# ---- repair helpers + Stop-AllTools
sub("function Run-HubScript(", icon_btn + repair_defs + r'''
function Stop-AllTools {
    $r = [Windows.MessageBox]::Show('Stop all your tools? (The Kokoro voice service is managed by Kokoro Player and is left running.)', 'Stop all my tools', 'YesNo', 'Question')
    if ($r -ne 'Yes') { Say 'Cancelled.'; return }
    foreach ($t in $script:ToolList) { [void](Stop-Tool $t) }
    Say 'Stopped your tools.'
    if ($script:OpenPanel -eq 'tools') { Start-Sleep -Milliseconds 600; Update-Rows }
}
function Run-HubScript(''')

# the repair launcher is now part of this toolbox: drop its separate button
a = hub.index("    @{ Name = 'Open the PC Repair Launcher'")
b = hub.index("    @{ Name = 'Open Kokoro Player'")
hub = hub[:a] + hub[b:]

# ---- sprite -> avatar loader
a = hub.index("# --- sprite")
b = hub.index("[Windows.Media.RenderOptions]::SetBitmapScalingMode($avatar")
hub = hub[:a] + r'''# --- avatars: avatars\<id>.png is a strip of 18 frames (126x150): 0-11 idle and gestures, 12-17 walk
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
''' + hub[b:]

# ---- the four buttons around the avatar
old_btns_start = hub.index("$btnTools = New-Btn 'My tools'")
tail = "[void]$rightCol.Children.Add($btnStop); [void]$rightCol.Children.Add($btnMore)"
old_btns_end = hub.index(tail) + len(tail)
hub = hub[:old_btns_start] + r'''$btnTools  = New-Btn 'My tools and checks' $G_INFO '#F0F0F3'
$btnStart  = New-Btn 'Start all my tools'  $G_ALL  '#7EE787'
$btnRepair = New-Btn 'PC repair and fixes'  $G_DOWN '#FF7B72'
$btnAvatar = New-Btn 'Choose avatar'        $G_DOWN '#D2A8FF'
[void]$leftCol.Children.Add($btnTools);   [void]$leftCol.Children.Add($btnStart)
[void]$rightCol.Children.Add($btnRepair); [void]$rightCol.Children.Add($btnAvatar)''' + hub[old_btns_end:]

# ---- tools panel: header, Stop all, then the automated checks list
sub("New-Header 'My tools' 'Green = running. Start, stop or restart each one. Tick \"auto\" to start a tool whenever the hub opens.')) { [void]$toolsStack.Children.Add($e) }",
    "New-Header 'My tools and checks' 'Green = running. Start, stop or restart each tool. Tick \"auto\" to start a tool whenever the toolbox opens.')) { [void]$toolsStack.Children.Add($e) }\n"
    "$btnStopAll = New-Btn 'Stop all my tools' $G_STOP '#FF7B72' 200; $btnStopAll.HorizontalAlignment = 'Left'; $btnStopAll.Add_Click({ Stop-AllTools })\n[void]$toolsStack.Children.Add($btnStopAll)")
sub("[void]$stack.Children.Add($toolsPanel)\n\nfunction Update-Rows {", r'''foreach ($e in (New-Header 'Automated checks, reports and repairs' 'Each button does one thing. Anything that changes something asks you first. Nothing here needs administrator permission.')) { $e.Margin = if ($e.FontSize -gt 12) { '0,10,0,2' } else { '0,0,0,8' }; [void]$toolsStack.Children.Add($e) }
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
    [void]$toolsStack.Children.Add($g)
}
[void]$stack.Children.Add($toolsPanel)

function Update-Rows {''')

# ---- repair panel (Jaina's steps + commands + tool list) and the lazy avatar picker replace the old "more" panel
a = hub.index("# ===== Panel 2: more tools and repairs =====")
tail = "[void]$stack.Children.Add($morePanel)"
b = hub.index(tail) + len(tail)
hub = hub[:a] + r'''# ===== Panel 2: PC repair and fixes (the Jaina repair widget) =====
$repairPanel = New-Panel
$infoStack = New-Object Windows.Controls.StackPanel
$scrollR = New-Object Windows.Controls.ScrollViewer
$scrollR.VerticalScrollBarVisibility = 'Auto'; $scrollR.MaxHeight = 460; $scrollR.Content = $infoStack
$repairPanel.Child = $scrollR
''' + info_block.replace("[void]$stack.Children.Add($infoPanel)", "") + r'''
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
    $d.Text = $tool.Info; $d.TextWrapping = 'Wrap'; $d.Foreground = Brush '#9CA3AF'; $d.FontSize = 11.5; $d.Margin = '12,5,0,0'; $d.VerticalAlignment = 'Top'
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
        $cell.Width = 108; $cell.CornerRadius = 9; $cell.Background = Brush '#1E1F23'; $cell.BorderThickness = 2
        $cell.BorderBrush = if ($a.id -eq $script:AvatarId) { Brush '#D2A8FF' } else { Brush '#383A41' }
        $cell.Padding = '4,6,4,4'; $cell.Margin = '4'; $cell.Cursor = 'Hand'; $cell.Tag = $a.id
        $sp = New-Object Windows.Controls.StackPanel
        $im = New-Object Windows.Controls.Image
        $im.Width = 76; $im.Height = 90; $im.Source = $script:AvatarCache[$a.id][2]
        [Windows.Media.RenderOptions]::SetBitmapScalingMode($im, 'HighQuality')
        $tb = New-Object Windows.Controls.TextBlock
        $tb.Text = $a.name; $tb.Foreground = Brush '#D4D4D8'; $tb.FontFamily = 'Consolas'; $tb.FontSize = 12; $tb.TextAlignment = 'Center'; $tb.Margin = '0,4,0,0'
        [void]$sp.Children.Add($im); [void]$sp.Children.Add($tb)
        $cell.Child = $sp
        $cell.Add_MouseLeftButtonUp({ param($s, $e) Set-Avatar $s.Tag })
        $cell.Add_MouseEnter({ param($s, $e) $s.Background = Brush '#2A2C32' })
        $cell.Add_MouseLeave({ param($s, $e) $s.Background = Brush '#1E1F23' })
        $script:AvatarCells[$a.id] = $cell
        [void]$avWrap2.Children.Add($cell)
    }
}''' + hub[b:]

# ---- panel switching and handlers
a = hub.index("$script:OpenPanel = ''")
tail = "$btnMore.Add_Click({ Set-Panel 'more' })"
b = hub.index(tail) + len(tail)
hub = hub[:a] + r'''$script:OpenPanel = ''
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
$btnAvatar.Add_Click({ Set-Panel 'avatar' })''' + hub[b:]
a = hub.index("$btnStop.Add_Click({")
b = hub.index("$closeBtn.Add_Click({ $win.Close() })")
hub = hub[:a] + hub[b:]

# ---- right-click menu: change avatar
sub("[void]$menu.Items.Add($mi); $avatar.ContextMenu = $menu", r'''$miAv = New-Object Windows.Controls.MenuItem; $miAv.Header = 'Change avatar'
foreach ($a in $AvatarList) {
    $m2 = New-Object Windows.Controls.MenuItem; $m2.Header = $a.name; $m2.Tag = $a.id
    $m2.Add_Click({ param($s, $e) Set-Avatar $s.Tag })
    [void]$miAv.Items.Add($m2)
}
[void]$menu.Items.Add($miAv); [void]$menu.Items.Add($mi); $avatar.ContextMenu = $menu''')
sub("if ($Open -eq 'tools') { Set-Panel 'tools' } elseif ($Open -eq 'more') { Set-Panel 'more' }",
    "if ($Open -eq 'tools') { Set-Panel 'tools' } elseif ($Open -eq 'repair') { Set-Panel 'repair' } elseif ($Open -eq 'avatar') { Set-Panel 'avatar' }")
hub = hub.replace("$frames[", "$script:Frames[")
hub = hub.replace("the hub", "the toolbox").replace("The hub", "The toolbox")
hub = hub.replace('#1E1F23', '#161618').replace('#2A2C32', '#242426').replace('#D4D4D8', '#F4EDE3').replace('#9CA3AF', '#C8C2B8')
bad = [c for c in hub if ord(c) >= 128]
assert not bad, bad[:5]
out = os.path.join(root, "Avatar-Toolbox.ps1")
open(out, "w", encoding="utf-8", newline="").write(hub.replace("\n", "\r\n"))
print("built", out, len(hub))
