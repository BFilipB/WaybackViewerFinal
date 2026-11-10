<#
.SYNOPSIS
  Summarizes WaybackViewer structured logs (key=value events) to rank optimization targets.
.EXAMPLE
  ./tools/Summarize-Logs.ps1 -LogDir E:\WaybackViewer\logs -Top 15
#>
param(
	[string]$LogDir = 'E:\WaybackViewer\logs',
	[string]$RunId,
	[int]$Top = 10
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $LogDir)) { throw "Log directory not found: $LogDir" }

function Get-Field([string]$line, [string]$name) {
	$m = [regex]::Match($line, "(?<![\w])$name=(\S+)")
	if ($m.Success) { $m.Groups[1].Value } else { $null }
}

$lines = Get-ChildItem $LogDir -File | Sort-Object Name |
	ForEach-Object { Get-Content $_.FullName } |
	Where-Object { (-not $RunId) -or $_ -like "*[[]$RunId[]]*" }
Write-Host "Lines: $($lines.Count)"

Write-Host "`n== Runs =="
$lines | ForEach-Object { if ($_ -match '^\S+ \w+ \[(\w{8})\]') { $Matches[1] } } |
	Group-Object | Sort-Object Count -Descending | Format-Table Name, Count -AutoSize

Write-Host "== Events by frequency =="
$events = $lines | ForEach-Object { if ($_ -match '(?-i)\s([a-z]+\.[a-z_]+)(?=\s|$)') { $Matches[1] } }
$events | Group-Object | Sort-Object Count -Descending | Select-Object -First ($Top * 2) |
	Format-Table Name, Count -AutoSize

Write-Host "== Extraction coverage by host (measured per extract.summary) =="
$agg = @{}
foreach ($l in $lines | Where-Object { $_ -match 'extract\.summary' }) {
	$h = Get-Field $l 'host'; if (-not $h) { continue }
	if (-not $agg[$h]) { $agg[$h] = @{ pages = 0; views = 0; likes = 0; comments = 0; published = 0; author = 0; measured = 0; estimated = 0; none = 0; slow = 0 } }
	foreach ($k in 'pages', 'views', 'likes', 'comments', 'published', 'author', 'measured', 'estimated', 'slow') { $agg[$h][$k] += [int](Get-Field $l $k) }
	$agg[$h]['none'] += [int](Get-Field $l 'noPopularity')
}
$agg.GetEnumerator() | ForEach-Object {
	$p = [math]::Max(1, $_.Value.pages)
	[pscustomobject]@{
		Host = $_.Key; Pages = $_.Value.pages
		'Views%' = [math]::Round(100 * $_.Value.views / $p); 'Likes%' = [math]::Round(100 * $_.Value.likes / $p)
		'Comments%' = [math]::Round(100 * $_.Value.comments / $p); 'Published%' = [math]::Round(100 * $_.Value.published / $p)
        'Measured%' = [math]::Round(100 * $_.Value.measured / $p); 'Estimated%' = [math]::Round(100 * $_.Value.estimated / $p)
		'NoPopularity%' = [math]::Round(100 * $_.Value.none / $p); Slow = $_.Value.slow
	}
} | Sort-Object Pages -Descending | Select-Object -First $Top | Format-Table -AutoSize

Write-Host "== Slowest requests (request.done / Slow request) =="
$lines | Where-Object { $_ -match 'request\.done|Slow request' } | ForEach-Object {
	$ms = Get-Field $_ 'ms'
	if (-not $ms -and $_ -match 'took (\d+) ms') { $ms = $Matches[1] }
	[pscustomobject]@{ Ms = [int]$ms; Line = $_.Substring(0, [math]::Min(160, $_.Length)) }
} | Sort-Object Ms -Descending | Select-Object -First $Top | Format-Table -Wrap -AutoSize

Write-Host "== Errors and archive failures =="
$lines | Where-Object { $_ -match ' (ERR|WRN) ' } | ForEach-Object {
	if ($_ -match '(?-i)\s([a-z]+\.[a-z_]+)(?=\s|$)') { $Matches[1] } else { 'other' }
} | Group-Object | Sort-Object Count -Descending | Format-Table Name, Count -AutoSize

Write-Host "== UX signals =="
foreach ($e in 'ui.empty_result', 'ui.deep_page', 'archive.pacing', 'request.error') {
	"{0,-18} {1}" -f $e, @($lines | Where-Object { $_ -match [regex]::Escape($e) }).Count
}
