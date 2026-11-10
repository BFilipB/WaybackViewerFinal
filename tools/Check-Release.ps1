<#
.SYNOPSIS
  Builds the solution and runs the full test suite; fails on any error.
#>
param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
	dotnet build WaybackViewer.slnx -c $Configuration --nologo -v q
	if ($LASTEXITCODE) { throw 'Build failed' }
	dotnet test tests/WaybackViewer.Tests -c $Configuration --no-build --nologo
	if ($LASTEXITCODE) { throw 'Tests failed' }
	Write-Host 'OK: build and tests passed'
}
finally { Pop-Location }
