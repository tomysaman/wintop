# Writes the CHANGELOG.md section for one version to a file, for the GitHub Release body.
# Falls back to a link to the commit list when the changelog has no section for it.
param([Parameter(Mandatory)][string]$Version, [string]$Out = 'notes.md')

$lines = Get-Content (Join-Path $PSScriptRoot '..\CHANGELOG.md') -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## \[?v?$([regex]::Escape($Version))\]?") { $start = $i + 1; break }
}
if ($start -lt 0) {
    $body = "No changelog entry for $Version. See the commits since the previous release."
} else {
    $end = $lines.Count
    for ($i = $start; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
    $body = ($lines[$start..($end - 1)] -join "`n").Trim()
}
$body += "`n`n**Download:** ``wintop.exe`` below. Single file, no installer, no .NET runtime needed. ``wintop.exe.sha256`` has its SHA-256 checksum."
Set-Content -Path $Out -Value $body -Encoding utf8
