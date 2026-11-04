# Verifies the working tree is healthy: no empty/truncated source files, build is clean, all tests pass,
# and every query parameter the UI sends is accepted by the server. Exit code 0 = healthy.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$fail = @()

# 1. Empty or suspiciously truncated source files
Get-ChildItem src, tests -Recurse -Include *.cs, *.cshtml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
	if ($_.Length -eq 0) { $fail += "EMPTY FILE: $($_.FullName)" }
	$t = Get-Content $_.FullName -Raw
	if ($t -match '=>\s*\r?\n\s*\r?\n') { $fail += "DANGLING EXPRESSION BODY (=> then blank line): $($_.Name)" }
	if ($t -match 'cf with \{ Text\s*\r?\n') { $fail += "TRUNCATED 'with' EXPRESSION: $($_.Name)" }
}

# 2. Every parameter the page script forwards must exist on the controller actions
$view = Get-Content src\WaybackViewer\Views\Home\Index.cshtml -Raw
$ctrl = Get-Content src\WaybackViewer\Controllers\HomeController.cs -Raw
foreach ($p in 'sort', 'popularityMetric', 'minPopularity') {
	if ($view -notmatch $p) { $fail += "View never mentions '$p'" }
	$hits = ([regex]::Matches($ctrl, "\b$p\b")).Count
	if ($hits -lt 2) { $fail += "Controller handles '$p' in fewer than Index + CaptureSet" }
}

# 3. Layout/theme controls present
$layout = Get-Content src\WaybackViewer\Views\Shared\_Layout.cshtml -Raw
foreach ($id in 'themeSelect', 'layoutSelect', 'wb-theme', 'wb-layout') {
	if ($layout -notmatch $id) { $fail += "Layout missing '$id'" }
}

# 4. Build + tests
dotnet build --nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { $fail += 'dotnet build failed' }
elseif (-not $SkipTests) {
	dotnet test --no-build --nologo -v q 2>&1 | Out-Null
	if ($LASTEXITCODE -ne 0) { $fail += 'dotnet test failed' }
}

if ($fail.Count) { $fail | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }; exit 1 }
Write-Host 'Integrity check passed.' -ForegroundColor Green
