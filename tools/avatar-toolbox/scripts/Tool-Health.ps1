<#
  Tool-Health.ps1  -  checks your tools (and repairs the safe things with -Fix).
  Read-only by default. -Fix only: closes DUPLICATE copies of an AutoHotkey script (keeps the newest), starts the Kokoro voice
  service / restarts a hung RapidOCR helper, rotates logs over 2 MB, deletes a stale temp page file. It never edits your scripts or settings.
  Usage:  powershell -File Tool-Health.ps1 [-ToolsFile tools.json] [-Fix] [-OpenReport]
#>
param([string]$ToolsFile = '', [switch]$Fix, [switch]$OpenReport)
$ErrorActionPreference = 'Continue'
$hub = Split-Path -Parent $PSScriptRoot
if (-not $ToolsFile) { $ToolsFile = Join-Path $hub 'tools.json' }
$cfg = [IO.File]::ReadAllText($ToolsFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
function Expand([string]$s) { if ($s) { [Environment]::ExpandEnvironmentVariables($s) } else { '' } }

$rep = New-Object Collections.Generic.List[string]
$script:problems = 0; $script:warns = 0; $script:fixed = New-Object Collections.Generic.List[string]
function Head([string]$t) { $rep.Add(''); $rep.Add("== $t") }
function Ok([string]$t)   { $rep.Add("  ok    $t") }
function Info([string]$t) { $rep.Add("  info  $t") }
function Warn([string]$t) { $rep.Add("  WARN  $t"); $script:warns++ }
function Bad([string]$t)  { $rep.Add("  PROBLEM  $t"); $script:problems++ }
function Fixed([string]$t){ $rep.Add("  fixed $t"); $script:fixed.Add($t) }
function Get-Json([string]$url) {
    try { return (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3).Content } catch { return $null }
}

$rep.Add("Tool health report - " + (Get-Date -Format 'dddd, MMMM d, yyyy h:mm tt'))
$rep.Add("Mode: " + $(if ($Fix) { 'CHECK + SAFE REPAIR' } else { 'CHECK ONLY (nothing is changed)' }))

# ---- AutoHotkey itself
Head 'AutoHotkey'
$ahk = Expand $cfg.ahk
if (Test-Path -LiteralPath $ahk) { Ok "AutoHotkey v2 found: $ahk" } else { Bad "AutoHotkey v2 not found at $ahk (the AHK tools cannot start). Install v2 from autohotkey.com." }
$snap = @(Get-CimInstance Win32_Process -Filter "Name='AutoHotkey64.exe' OR Name='AutoHotkey32.exe'" -ErrorAction SilentlyContinue)
if ($snap.Count) { foreach ($p in $snap) { Info ("running: " + (($p.CommandLine -replace '^.*?AutoHotkey(64|32)\.exe"?\s*', '') -replace '/restart /script ', '')) } } else { Info 'no AutoHotkey scripts are running' }

# ---- each tool
Head 'Your tools'
foreach ($t in $cfg.tools) {
    $path = Expand $t.path
    if (-not (Test-Path -LiteralPath $path)) { Bad "$($t.name): file is missing ($path)"; continue }
    if ($t.kind -eq 'ahk') {
        $mine = @($snap | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | Sort-Object CreationDate)
        if ($mine.Count -gt 1) {
            Warn "$($t.name): running $($mine.Count) times (hotkeys can fire twice)"
            if ($Fix) {
                foreach ($p in $mine[0..($mine.Count - 2)]) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
                Fixed "$($t.name): closed $($mine.Count - 1) older copy(ies), kept the newest"
            }
        } elseif ($mine.Count -eq 1) { Ok "$($t.name): running" } else { Info "$($t.name): not running (click Start in the hub)" }
    } elseif ($t.kind -eq 'python') {
        $exe = Expand $t.exe
        if (-not (Test-Path -LiteralPath $exe)) { Bad "$($t.name): Python program missing ($exe)"; continue }
        $py = Join-Path (Split-Path -Parent $exe) 'python.exe'
        $imp = & $py -c "import rapidocr_onnxruntime, mss, numpy; print('ok')" 2>&1 | Select-Object -Last 1
        if ($imp -eq 'ok') { Ok "$($t.name): Python packages import fine" }
        else { Bad "$($t.name): Python packages broken ($imp). Repair: `"$py`" -m pip install --force-reinstall rapidocr-onnxruntime mss  (downloads about 120 MB - not done automatically)" }
        $procs = @(Get-CimInstance Win32_Process -Filter "Name='pythonw.exe' OR Name='python.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        $h = if ($t.healthUrl) { Get-Json $t.healthUrl } else { $null }
        if ($h) { Ok "$($t.name): running and answering" }
        elseif ($procs.Count) {
            Warn "$($t.name): process exists but is not answering (hung?)"
            if ($Fix) {
                foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
                Start-Sleep -Milliseconds 800
                Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $path) -WorkingDirectory (Split-Path -Parent $path) -WindowStyle Hidden
                Fixed "$($t.name): restarted"
            }
        } else { Info "$($t.name): not running (Game Voice Reader starts it on first F22 use)" }
    } elseif ($t.kind -eq 'service') {
        $h = Get-Json $t.healthUrl
        if ($h) {
            if ($h -match '"ready"\s*:\s*true') { Ok "$($t.name): ready" } else { Warn "$($t.name): answering but not ready yet (still loading)" }
        } else {
            Bad "$($t.name): not answering - Kokoro voices will not work"
            if ($Fix -and (Test-Path -LiteralPath (Expand $t.exe))) {
                Start-Process -FilePath (Expand $t.exe) -ArgumentList ('"{0}" {1}' -f $path, $t.args) -WorkingDirectory (Split-Path -Parent $path) -WindowStyle Hidden
                $up = $false
                for ($i = 0; $i -lt 40 -and -not $up; $i++) { Start-Sleep -Milliseconds 700; $up = [bool](Get-Json $t.healthUrl) }
                if ($up) { Fixed "$($t.name): started" } else { $rep.Add('  (it did not come up within 30 seconds - open Kokoro Player to see why)') }
            }
        }
    }
}

# ---- hotkey clashes between your AutoHotkey scripts
Head 'Hotkey clashes'
$files = @($cfg.tools | Where-Object { $_.kind -eq 'ahk' } | ForEach-Object { Expand $_.path })
$kp = 'C:\AI_Workspace\_scripts\kokoro-hotkey\kokoro-hotkeys.ahk'
if (Test-Path -LiteralPath $kp) { $files += $kp }
$seen = @{}
foreach ($f in ($files | Where-Object { Test-Path -LiteralPath $_ })) {
    $txt = [IO.File]::ReadAllText($f)
    foreach ($m in [regex]::Matches($txt, '(?m)^\s*([~*$!^+#<>]*)(F\d{1,2}|XButton\d|[A-Za-z0-9])(\s+Up)?\s*::')) {
        $k = ($m.Groups[1].Value + $m.Groups[2].Value + $(if ($m.Groups[3].Success) { ' Up' } else { '' })).ToUpper()
        if (-not $seen.ContainsKey($k)) { $seen[$k] = New-Object Collections.Generic.HashSet[string] }
        [void]$seen[$k].Add((Split-Path -Leaf $f))
    }
}
$clash = @($seen.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 })
if ($clash.Count) { foreach ($c in $clash) { Warn ("$($c.Key) is defined in: " + ($c.Value -join ', ')) } } else { Ok "no key is claimed by two of your scripts ($($seen.Count) hotkeys checked)" }

# ---- GPU
Head 'GPU (voice speed depends on free GPU memory)'
$smi = (Get-Command nvidia-smi -ErrorAction SilentlyContinue).Source
if ($smi) {
    $g = (& $smi --query-gpu=name,memory.used,memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1) -split ',\s*'
    if ($g.Count -ge 3) {
        $pct = [math]::Round(100 * [double]$g[1] / [double]$g[2])
        $line = "$($g[0]): $([math]::Round([double]$g[1] / 1024, 1)) GB of $([math]::Round([double]$g[2] / 1024, 1)) GB used ($pct%)"
        if ($pct -ge 85) { Warn "$line - very full; heavy voices (Qwen) will be slow while a game is running. Try the 'Free GPU for gaming' button or a Kokoro voice." } else { Ok $line }
    }
} else { Info 'nvidia-smi not found' }
$ol = (Get-Command ollama -ErrorAction SilentlyContinue).Source
if (-not $ol) { $ol = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
if (Test-Path -LiteralPath $ol) {
    $loaded = @(& $ol ps 2>$null | Select-Object -Skip 1 | Where-Object { $_.Trim() })
    if ($loaded.Count) { Warn ("Ollama has $($loaded.Count) model(s) loaded in memory: " + (($loaded | ForEach-Object { ($_ -split '\s+')[0] }) -join ', ')) } else { Ok 'no Ollama models are loaded' }
}

# ---- logs and temp
Head 'Logs and temp files'
$logs = @(
    (Join-Path $env:LOCALAPPDATA 'GameVoiceReader\gvr.log'),
    'C:\AI_Workspace\tools\rapidocr-gvr\server.log'
)
foreach ($l in $logs) {
    if (-not (Test-Path -LiteralPath $l)) { Info "$(Split-Path -Leaf $l): none yet"; continue }
    $mb = [math]::Round((Get-Item -LiteralPath $l).Length / 1MB, 2)
    if ($mb -gt 2) {
        Warn "$(Split-Path -Leaf $l) is $mb MB"
        if ($Fix) { Move-Item -LiteralPath $l -Destination ($l + '.old') -Force; Fixed "$(Split-Path -Leaf $l): rotated to .old" }
    } else { Ok "$(Split-Path -Leaf $l): $mb MB" }
}
$page = Join-Path (Split-Path -Parent (Expand ($cfg.tools | Where-Object { $_.id -eq 'gvr' } | Select-Object -First 1).path)) 'GameVoiceReader-page.txt'
if (Test-Path -LiteralPath $page) {
    if ((Get-Item -LiteralPath $page).LastWriteTime -lt (Get-Date).AddDays(-7)) {
        Warn 'GameVoiceReader-page.txt is over a week old (only used by the Edge engine)'
        if ($Fix) { Remove-Item -LiteralPath $page -Force; Fixed 'deleted the stale GameVoiceReader-page.txt' }
    } else { Ok 'GameVoiceReader-page.txt is recent' }
}

# ---- summary
$rep.Insert(2, '')
$rep.Insert(3, "Summary: $($script:problems) problem(s), $($script:warns) warning(s), $($script:fixed.Count) repair(s) made.")
if (-not $Fix -and ($script:problems -or $script:warns)) { $rep.Add(''); $rep.Add("Tip: 'Check + auto-repair my tools' in the hub fixes the safe items above.") }
$dir = Join-Path $hub 'reports'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$out = Join-Path $dir ('health-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
[IO.File]::WriteAllLines($out, $rep, (New-Object Text.UTF8Encoding $true))
Get-ChildItem -LiteralPath $dir -Filter 'health-*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue
if ($OpenReport) { Start-Process notepad.exe -ArgumentList "`"$out`"" } else { $rep }
Write-Output $out
