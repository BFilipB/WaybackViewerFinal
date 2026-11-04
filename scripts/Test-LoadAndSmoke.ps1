param(
	[int]$Port = 5099,
	[int]$Concurrency = 50,
	[int]$Rounds = 5,
	[double]$MaxFailureRate = 0.0,
	[int]$P95BudgetMs = 2000
)

# Smoke + load + stress test against a locally hosted app using in-memory storage.
# Only requests that never reach the external Wayback archive are issued.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WaybackViewer\WaybackViewer.csproj'
$base = "http://127.0.0.1:$Port"

dotnet build $project -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$env:Storage__Provider = 'Memory'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Logging__LogLevel__Default = 'Warning'
$proc = Start-Process dotnet -ArgumentList @('run', '--project', $project, '-c', 'Release', '--no-build', '--urls', $base) -PassThru -WindowStyle Hidden

$failed = $false
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(30)

function Invoke-Batch([string[]]$Urls) {
	$sw = [System.Diagnostics.Stopwatch]::StartNew()
	$jobs = foreach ($u in $Urls) {
		$t = [System.Diagnostics.Stopwatch]::StartNew()
		[pscustomobject]@{ Url = $u; Timer = $t; Task = $client.GetAsync($u) }
	}
	$results = foreach ($j in $jobs) {
		try {
			$resp = $j.Task.GetAwaiter().GetResult()
			[pscustomobject]@{ Status = [int]$resp.StatusCode; Ms = $j.Timer.ElapsedMilliseconds }
		} catch { [pscustomobject]@{ Status = 0; Ms = $j.Timer.ElapsedMilliseconds } }
	}
	[pscustomobject]@{ Results = $results; TotalMs = $sw.ElapsedMilliseconds }
}

function Report([string]$Name, $Batch, [int[]]$OkStatus) {
	$ms = $Batch.Results | ForEach-Object Ms | Sort-Object
	$bad = @($Batch.Results | Where-Object { $OkStatus -notcontains $_.Status }).Count
	$p95 = $ms[[Math]::Min($ms.Count - 1, [int][Math]::Ceiling($ms.Count * 0.95) - 1)]
	$rate = $bad / [Math]::Max(1, $Batch.Results.Count)
	"{0,-28} n={1,-4} bad={2,-3} p50={3}ms p95={4}ms max={5}ms total={6}ms" -f $Name, $Batch.Results.Count, $bad, $ms[[int]($ms.Count / 2)], $p95, $ms[-1], $Batch.TotalMs | Write-Host
	if ($rate -gt $MaxFailureRate -or $p95 -gt $P95BudgetMs) { $script:failed = $true; Write-Host "  FAIL: failure rate $rate or p95 $p95 over budget" -ForegroundColor Red }
}

try {
	$ready = $false
	for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
		try { $ready = ($client.GetAsync("$base/healthz").GetAwaiter().GetResult().StatusCode -eq 'OK') } catch { Start-Sleep -Milliseconds 500 }
	}
	if (-not $ready) { throw 'App did not become healthy' }
	Write-Host "App healthy at $base"

	# Smoke
	$homeResp = $client.GetAsync("$base/").GetAwaiter().GetResult()
	if ([int]$homeResp.StatusCode -ne 200) { $failed = $true; Write-Host "FAIL: GET / returned $([int]$homeResp.StatusCode)" -ForegroundColor Red }
	else {
		$html = $homeResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
		foreach ($n in 'name="sort"', 'name="popularityMetric"', 'name="minPopularity"') {
			if (-not $html.Contains($n)) { $failed = $true; Write-Host "FAIL: home page missing control $n" -ForegroundColor Red }
		}
		Write-Host 'Smoke OK: home page and sort/popularity controls present'
	}

	$bad = "$base/?url=" + [Uri]::EscapeDataString('not a url')
	$sorts = 'newest', 'popular', 'liked', 'viewed', 'bogus'
	$ops = "$base/Home/OperationStatus"

	# Load: steady concurrent traffic
	for ($r = 1; $r -le $Rounds; $r++) {
		$urls = 1..$Concurrency | ForEach-Object {
			switch ($_ % 4) {
				0 { "$base/" }
				1 { "$base/healthz" }
				2 { "$base/?sort=$($sorts[$_ % $sorts.Count])&popularityMetric=likes&minPopularity=$_" }
				3 { $ops }
			}
		}
		Report "load round $r" (Invoke-Batch $urls) @(200)
	}

	# Stress: burst of invalid input (must be rejected locally with 200 + error page, never 5xx)
	Report 'stress invalid-url burst' (Invoke-Batch (1..($Concurrency * 4) | ForEach-Object { $bad })) @(200, 400)
	Report 'stress malformed params' (Invoke-Batch (1..($Concurrency * 2) | ForEach-Object { "$base/?sort=%00&popularityMetric=%27&minPopularity=99999999999&page=-1" })) @(200, 400)
	Report 'stress 404 paths' (Invoke-Batch (1..$Concurrency | ForEach-Object { "$base/no-such-$_" })) @(404)

	$after = $client.GetAsync("$base/healthz").GetAwaiter().GetResult().StatusCode
	if ($after -ne 'OK') { $failed = $true; Write-Host 'FAIL: app unhealthy after stress' -ForegroundColor Red }
}
finally {
	$client.Dispose()
	Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $proc.Id } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
	Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

if ($failed) { Write-Host 'RESULT: FAILED' -ForegroundColor Red; exit 1 }
Write-Host 'RESULT: PASSED' -ForegroundColor Green
