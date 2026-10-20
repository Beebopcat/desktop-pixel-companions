<#
.SYNOPSIS  Lists everything that starts with Windows and removes entries whose target no longer exists.
.PARAMETER Apply  Actually remove broken entries (current user only). Without it, only reports. A registry backup is written first.
#>
param([switch]$Apply)
$ErrorActionPreference = 'SilentlyContinue'
function Get-Target([string]$c) { if (-not $c) { return '' }; $c = [Environment]::ExpandEnvironmentVariables($c.Trim()); if ($c.StartsWith('"')) { return $c.Substring(1, $c.IndexOf('"', 1) - 1) }; $m = [regex]::Match($c, '^(.+?\.(exe|cmd|bat|ps1|vbs|lnk|pyw|py))', 'IgnoreCase'); if ($m.Success) { $m.Groups[1].Value } else { ($c -split ' ')[0] } }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bk = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "PC-Repair-Reports\startup-backup-$stamp"
$rows = @()
$map = @{
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'                   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'                   = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'       = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32'
}
foreach ($k in $map.Keys) {
    $p = Get-ItemProperty $k; $a = Get-ItemProperty $map[$k]
    foreach ($n in ($p.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' })) {
        $t = Get-Target ([string]$n.Value); $b = $a.($n.Name)
        $rows += [pscustomobject]@{ Key = $k; Approved = $map[$k]; Name = $n.Name; Enabled = -not ($b -and $b[0] -in 3, 7); Target = $t; Exists = ((Test-Path -LiteralPath $t) -or ($t -and $t -notmatch '[\\/]' -and [bool](Get-Command $t))); User = ($k -like 'HKCU:*') }
    }
}
Write-Host ''
$rows | Sort-Object Exists, Enabled | Format-Table Name, @{n = 'On'; e = { $_.Enabled } }, Exists, Target -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$broken = @($rows | Where-Object { -not $_.Exists -and $_.Target })
if (-not $broken.Count) { Write-Host 'No broken startup entries.' -ForegroundColor Green; return }
Write-Host "$($broken.Count) broken entr$(if ($broken.Count -eq 1) { 'y' } else { 'ies' }):" -ForegroundColor Yellow
$broken | ForEach-Object { Write-Host "  - $($_.Name)  ->  $($_.Target)" }
if (-not $Apply) { Write-Host 'Run with -Apply to remove them (a backup is saved first).'; return }
New-Item -ItemType Directory -Force $bk | Out-Null
reg.exe export 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run' "$bk\HKCU-Run.reg" /y | Out-Null
reg.exe export 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' "$bk\HKCU-StartupApproved-Run.reg" /y | Out-Null
foreach ($b in $broken) {
    if (-not $b.User) { Write-Host "  (skipped $($b.Name): machine-wide entry needs administrator)" -ForegroundColor DarkGray; continue }
    Remove-ItemProperty -Path $b.Key -Name $b.Name; Remove-ItemProperty -Path $b.Approved -Name $b.Name
    Write-Host "  removed $($b.Name)" -ForegroundColor Green
}
Write-Host "Backup saved to $bk (double-click the .reg files to undo)."
