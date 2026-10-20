<#
  Backup-Settings.ps1  -  zips your tool scripts + settings into  <AutoHotkey folder>\_backups\tool-backup-<time>.zip
  Includes each tool's script, the settings (.ini) and Lib folder beside it, rapid_server.py, and tools.json. Skips the Python environment.
  Prints the zip path as its last line.
  Usage:  powershell -File Backup-Settings.ps1 [-ToolsFile tools.json]
#>
param([string]$ToolsFile = '')
$hub = Split-Path -Parent $PSScriptRoot
if (-not $ToolsFile) { $ToolsFile = Join-Path $hub 'tools.json' }
$cfg = [IO.File]::ReadAllText($ToolsFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
function Expand([string]$s) { if ($s) { [Environment]::ExpandEnvironmentVariables($s) } else { '' } }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $env:TEMP "toolhub-backup-$stamp"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($t in $cfg.tools) {
    $p = Expand $t.path
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $dest = Join-Path $stage $t.id; New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -LiteralPath $p -Destination $dest -Force
    $dir = Split-Path -Parent $p
    if ($t.kind -eq 'ahk' -and (Split-Path -Leaf $dir) -ne 'AutoHotkey') {          # a tool with its own folder: take its settings and Lib too
        Get-ChildItem -LiteralPath $dir -Filter '*.ini' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $dest -Force
        $lib = Join-Path $dir 'Lib'
        if (Test-Path -LiteralPath $lib) { Copy-Item -LiteralPath $lib -Destination $dest -Recurse -Force }
    }
}
Copy-Item -LiteralPath $ToolsFile -Destination $stage -Force
$destDir = Join-Path (Split-Path -Parent $hub) '_backups'
New-Item -ItemType Directory -Path $destDir -Force | Out-Null
$zip = Join-Path $destDir "tool-backup-$stamp.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $destDir -Filter 'tool-backup-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 15 | Remove-Item -Force -ErrorAction SilentlyContinue
Write-Output $zip
