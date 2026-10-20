<#
  Voice-OCR-Report.ps1  -  summarises the Game Voice Reader log: OCR speed per engine, time-to-first-audio per voice, fallbacks.
  The log holds timings and counts only - never the text you read. Read-only.
  Usage:  powershell -File Voice-OCR-Report.ps1 [-ToolsFile tools.json] [-OpenReport]
#>
param([string]$ToolsFile = '', [switch]$OpenReport)
$hub = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $env:LOCALAPPDATA 'GameVoiceReader'
$lines = @()
foreach ($f in @((Join-Path $logDir 'gvr.log.old'), (Join-Path $logDir 'gvr.log'))) { if (Test-Path -LiteralPath $f) { $lines += [IO.File]::ReadAllLines($f) } }

function Stats($vals) {
    $v = @($vals | Sort-Object)
    if (-not $v.Count) { return 'n/a' }
    $med = $v[[int][math]::Floor(($v.Count - 1) / 2)]
    $p90 = $v[[int][math]::Min($v.Count - 1, [math]::Ceiling(0.9 * $v.Count) - 1)]
    '{0} samples   median {1}   90th pct {2}   worst {3}' -f $v.Count, $med, $p90, $v[-1]
}
$ocr = @(); $none = @(); $audio = @(); $fb = @(); $holds = @(); $errs = 0; $speak = @()
foreach ($l in $lines) {
    if ($l -match 'ocr ok (?:eng=(\w+) )?chars=(\d+) score=([\d.]+) pass=(\d+) tried=(\d+) settle=(\d+)ms ocr=(\d+)ms stable=\S+ app=(.*)$') {
        $ocr += [pscustomobject]@{ Eng = $(if ($Matches[1]) { $Matches[1] } else { 'windows' }); Chars = [int]$Matches[2]; Ms = [int]$Matches[7]; Settle = [int]$Matches[6]; App = $Matches[8].Trim() }
    } elseif ($l -match 'ocr none (?:eng=(\w+) )?settle') { $none += $(if ($Matches[1]) { $Matches[1] } else { 'windows' }) }
    elseif ($l -match 'first audio after (\d+) ms .*voice=(\S+) chars=(\d+)') { $audio += [pscustomobject]@{ Ms = [int]$Matches[1]; Voice = $Matches[2]; Chars = [int]$Matches[3] } }
    elseif ($l -match 'fallback: (\S+) silent after (\d+) ms -> (\S+)') { $fb += [pscustomobject]@{ From = $Matches[1]; Ms = [int]$Matches[2]; To = $Matches[3] } }
    elseif ($l -match '(F2[12]) held (\d+) ms') { $holds += [int]$Matches[2] }
    elseif ($l -match 'speak status=(\d+) ms=(\d+)') { $speak += [pscustomobject]@{ Status = [int]$Matches[1]; Ms = [int]$Matches[2] } }
    elseif ($l -match ' error: | unhandled error') { $errs++ }
}

$r = New-Object Collections.Generic.List[string]
$r.Add('Voice and OCR report - ' + (Get-Date -Format 'dddd, MMMM d, yyyy h:mm tt'))
$r.Add("Log lines read: $($lines.Count)   (timings and counts only - never the text you read)")
if (-not $ocr.Count -and -not $audio.Count) { $r.Add(''); $r.Add('No reads recorded yet. Use Game Voice Reader (F21 / F22) a few times, then run this again.') }

$r.Add(''); $r.Add('== OCR speed (milliseconds, lower is better)')
foreach ($g in ($ocr | Group-Object Eng)) { $r.Add(('  {0,-8} {1}   average text length {2} chars' -f $g.Name, (Stats ($g.Group | ForEach-Object Ms)), [int](($g.Group | Measure-Object Chars -Average).Average)))
    $r.Add(('  {0,-8} waiting for the page to settle: {1}' -f '', (Stats ($g.Group | ForEach-Object Settle)))) }
if ($none.Count) { $r.Add("  reads that found no text: $($none.Count)  (" + (($none | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ') + ')') }
$r.Add(''); $r.Add('== OCR by program')
foreach ($g in ($ocr | Group-Object App, Eng | Sort-Object Count -Descending | Select-Object -First 12)) { $r.Add(('  {0,-34} {1}' -f $g.Name, (Stats ($g.Group | ForEach-Object Ms)))) }

$r.Add(''); $r.Add('== Time until the voice starts speaking (milliseconds)')
foreach ($g in ($audio | Group-Object Voice)) { $r.Add(('  {0,-34} {1}' -f $g.Name, (Stats ($g.Group | ForEach-Object Ms)))) }
if (-not $audio.Count) { $r.Add('  none recorded (Kokoro engine only)') }
if ($fb.Count) { $r.Add(''); $r.Add("== Fast-voice fallbacks: $($fb.Count)"); foreach ($f in ($fb | Select-Object -Last 8)) { $r.Add(('  {0} was silent {1:N0} s -> switched to {2}' -f $f.From, ($f.Ms / 1000), $f.To)) } }

$r.Add(''); $r.Add('== Key presses')
if ($holds.Count) {
    $quick = @($holds | Where-Object { $_ -lt 100 }).Count
    $r.Add("  F21/F22 holds: " + (Stats $holds))
    if ($quick -gt ($holds.Count / 2)) { $r.Add('  NOTE: most holds were under 0.1 s - your mouse software may be sending F21/F22 as a quick tap instead of a hold.') }
} else { $r.Add('  none recorded') }
if ($errs) { $r.Add(''); $r.Add("Errors logged: $errs (open the log from the hub: Game Voice Reader > log)") }

$dir = Join-Path $hub 'reports'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
$out = Join-Path $dir ('voice-ocr-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
[IO.File]::WriteAllLines($out, $r, (New-Object Text.UTF8Encoding $true))
Get-ChildItem -LiteralPath $dir -Filter 'voice-ocr-*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue
if ($OpenReport) { Start-Process notepad.exe -ArgumentList "`"$out`"" } else { $r }
Write-Output $out
