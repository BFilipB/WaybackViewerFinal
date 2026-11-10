<#
.SYNOPSIS
  Finds a saved capture, decompresses it to a .html file, and shows where a number appears in it.

.EXAMPLE
  .\tools\Export-CaptureHtml.ps1 -Url "https://www.reddit.com/r/mkd_code/comments/1khlukd/..." -Timestamp 20250727121849 -Expect 13

  -Url        the full address exactly as shown in the app (the "Original" column)
  -Timestamp  the 14-digit capture time shown in the app (for example 20250727121849)
  -Expect     the number you can SEE on the real page (for example 13 comments)
  -HtmlRoot   the Storage:HtmlRoot folder from appsettings.json
#>
param(
	[Parameter(Mandatory)][string]$Url,
	[Parameter(Mandatory)][string]$Timestamp,
	[string]$Expect = '',
	[string]$HtmlRoot = 'E:\WaybackViewer\html-payloads',
	[string]$Out = (Join-Path $env:USERPROFILE 'Desktop\capture.html')
)

$uri = [Uri]$Url
$hostName = -join ($uri.Host.ToLowerInvariant().ToCharArray() | ForEach-Object { if ($_ -match '[a-z0-9\-\.]') { $_ } else { '_' } })
$bytes = [Text.Encoding]::UTF8.GetBytes("$Timestamp|$Url")
$sha = [Security.Cryptography.SHA256]::Create()
$hash = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
$path = Join-Path $HtmlRoot "$hostName\$($Timestamp.Substring(0,4))\$($Timestamp.Substring(4,2))\$($Timestamp.Substring(6,2))\$hash.gz"

Write-Host "Looking for: $path"
if (-not (Test-Path $path)) {
	Write-Host "NOT FOUND. This page is not saved in that folder." -ForegroundColor Yellow
	Write-Host "Check: 1) -HtmlRoot is the folder in appsettings.json  2) the page shows 'Saved locally: Yes' in the app  3) -Url and -Timestamp are copied exactly."
	return
}

$in = [IO.File]::OpenRead($path)
$gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
$reader = New-Object IO.StreamReader($gz, [Text.Encoding]::UTF8)
$html = $reader.ReadToEnd()
$reader.Dispose(); $gz.Dispose(); $in.Dispose()

Set-Content -Path $Out -Value $html -Encoding UTF8
Write-Host "Saved $($html.Length) characters to $Out" -ForegroundColor Green

if ($Expect) {
	Write-Host "`nPlaces where the number $Expect appears (first 8):" -ForegroundColor Cyan
	[regex]::Matches($html, ".{0,120}(?<![0-9])$([regex]::Escape($Expect))(?![0-9]).{0,120}") |
		Select-Object -First 8 | ForEach-Object { "- " + ($_.Value -replace '\s+', ' ') }
}

Write-Host "`nLikely count fields (first 8):" -ForegroundColor Cyan
[regex]::Matches($html, '.{0,60}(comment-?count|comments?_?count|num_comments|commentCount|replyCount|interactionStatistic|score=|data-[a-z-]*count).{0,60}', 'IgnoreCase') |
	Select-Object -First 8 | ForEach-Object { "- " + ($_.Value -replace '\s+', ' ') }
