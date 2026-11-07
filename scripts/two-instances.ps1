<#
.SYNOPSIS
  Proves the throttle holds across instances: starts two real API processes on one SQLite
  file, sends 200 parallel requests for one supplier (alternating between the instances),
  and checks that exactly 100 are accepted and 100 are throttled.
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'src\SupplierFeed.Api\bin\Debug\net9.0\SupplierFeed.Api.dll'
$ports = 5001, 5002
$dbDir = Join-Path $env:TEMP ('supplierfeed-' + [guid]::NewGuid().ToString('N'))
$processes = @()

Add-Type -AssemblyName System.Net.Http
# Windows PowerShell allows only 2 connections per host by default, which would serialise the load.
[System.Net.ServicePointManager]::DefaultConnectionLimit = 200
$client = [System.Net.Http.HttpClient]::new()

try {
    Write-Host 'Building...'
    dotnet build (Join-Path $root 'SupplierFeed.sln') -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    # Both processes inherit this, so they share one database file.
    $env:Database__Path = Join-Path $dbDir 'shared.db'
    New-Item -ItemType Directory -Force $dbDir | Out-Null

    foreach ($port in $ports) {
        $log = Join-Path $dbDir "instance-$port.log"
        # Run the dll through dotnet: the apphost .exe may be blocked by endpoint security.
        $processes += Start-Process dotnet -ArgumentList $dll, '--urls', "http://localhost:$port" `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
    }

    foreach ($port in $ports) {
        $ready = $false
        for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
            try {
                $client.GetAsync("http://localhost:$port/api/reservations/stats/ping").GetAwaiter().GetResult() | Out-Null
                $ready = $true
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "Instance on port $port did not start." }
    }

    $supplier = 'multi-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $tasks = 1..200 | ForEach-Object {
        $port = $ports[$_ % 2]
        $json = '{{"supplierId":"{0}","reservationId":"r{1}","roomId":"101","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":450,"updatedAtUtc":"2026-07-01T10:15:00Z"}}' -f $supplier, $_
        $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        $client.PostAsync("http://localhost:$port/api/reservations/ingest", $content)
    }
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks)

    $codes = $tasks | ForEach-Object { [int]$_.Result.StatusCode }
    Write-Host "200 parallel requests for $supplier across ports $($ports -join ', '):"
    $codes | Group-Object | Sort-Object Name | ForEach-Object { Write-Host ('  {0} x {1}' -f $_.Name, $_.Count) }

    foreach ($port in $ports) {
        $stats = $client.GetAsync("http://localhost:$port/api/reservations/stats/$supplier").GetAwaiter().GetResult()
        Write-Host ("stats via port ${port}: " + $stats.Content.ReadAsStringAsync().GetAwaiter().GetResult())
    }

    $accepted = ($codes | Where-Object { $_ -eq 201 }).Count
    $throttled = ($codes | Where-Object { $_ -eq 429 }).Count
    if ($accepted -eq 100 -and $throttled -eq 100) {
        Write-Host 'PASS: exactly 100 accepted and 100 throttled.' -ForegroundColor Green
    } else {
        Write-Host "FAIL: expected 100 accepted and 100 throttled, got $accepted and $throttled." -ForegroundColor Red
        exit 1
    }
}
finally {
    $processes | ForEach-Object { if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force } }
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $dbDir -ErrorAction SilentlyContinue
    $env:Database__Path = $null
}
