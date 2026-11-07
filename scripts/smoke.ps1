<#
.SYNOPSIS
  Walks a running instance through every ingest outcome, then sends 101 requests for one
  supplier to show the throttle. Start the API first, for example:
    dotnet src/SupplierFeed.Api/bin/Debug/net9.0/SupplierFeed.Api.dll --urls http://localhost:5099
  (the .exe apphost may be blocked by endpoint security; run the .dll through dotnet)
#>
param([string]$BaseUrl = 'http://localhost:5099')

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()

function Send([string]$Json) {
    $content = [System.Net.Http.StringContent]::new($Json, [System.Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync("$BaseUrl/api/reservations/ingest", $content).GetAwaiter().GetResult()
    '{0} {1}' -f [int]$response.StatusCode, $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
}

function Body([string]$Supplier, [string]$Reservation = 'r1', $Price = 450, [string]$Updated = '2026-07-01T10:15:00Z',
              [string]$CheckOut = '2026-08-03T00:00:00Z') {
    '{{"supplierId":"{0}","reservationId":"{1}","roomId":"101","checkIn":"2026-08-01T00:00:00Z","checkOut":"{2}","price":{3},"updatedAtUtc":"{4}"}}' -f `
        $Supplier, $Reservation, $CheckOut, $Price, $Updated
}

$supplier = 'smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
Write-Host "Supplier: $supplier"
Write-Host ('new               : ' + (Send (Body $supplier)))
Write-Host ('identical         : ' + (Send (Body $supplier)))
Write-Host ('same ts, new price: ' + (Send (Body $supplier -Price 500)))
Write-Host ('older timestamp   : ' + (Send (Body $supplier -Price 500 -Updated '2026-06-01T10:15:00Z')))
Write-Host ('newer timestamp   : ' + (Send (Body $supplier -Price 500 -Updated '2026-07-02T10:15:00Z')))
Write-Host ('checkOut<checkIn  : ' + (Send (Body $supplier -CheckOut '2026-07-30T00:00:00Z')))
Write-Host ('negative price    : ' + (Send (Body $supplier -Price -1)))
Write-Host ('empty object      : ' + (Send '{}'))

# 8 requests are already counted for $supplier; use a fresh supplier for a clean 101.
$flood = 'flood-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$codes = 1..101 | ForEach-Object { (Send (Body $flood -Reservation "r$_")).Split(' ')[0] }
Write-Host "101 requests for $flood :"
$codes | Group-Object | ForEach-Object { Write-Host ('  {0} x {1}' -f $_.Name, $_.Count) }

$stats = $client.GetAsync("$BaseUrl/api/reservations/stats/$flood").GetAwaiter().GetResult()
Write-Host ('stats for ' + $flood + ': ' + $stats.Content.ReadAsStringAsync().GetAwaiter().GetResult())
Write-Host 'Expected: 100 x 201, 1 x 429; stats ingested 100, throttled 1.'
