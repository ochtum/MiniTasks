[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Uri]$BaseUrl,

    [ValidateRange(1, 60)]
    [int]$Attempts = 12,

    [ValidateRange(0, 60)]
    [int]$DelaySeconds = 10
)

$ErrorActionPreference = 'Stop'

if (-not $BaseUrl.IsAbsoluteUri -or $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment) {
    throw 'BaseUrl must be absolute and must not contain credentials, a query or a fragment.'
}
if ($BaseUrl.Scheme -ne 'https' -and -not ($BaseUrl.Scheme -eq 'http' -and $BaseUrl.IsLoopback)) {
    throw 'HTTPS is required except for a loopback HTTP address used in local checks.'
}

Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
$healthUrl = [Uri]($BaseUrl.AbsoluteUri.TrimEnd('/') + '/healthz')
$lastFailure = 'No response.'

try {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $response = $null
        try {
            $response = $client.GetAsync($healthUrl).GetAwaiter().GetResult()
            $statusCode = [int]$response.StatusCode
            if ($statusCode -ne 200) {
                $lastFailure = "Received HTTP $statusCode; expected HTTP 200."
            } else {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                try {
                    if (-not $body.TrimStart().StartsWith('{')) {
                        throw 'Health JSON must be an object.'
                    }
                    $health = $body | ConvertFrom-Json -ErrorAction Stop
                    if ($health -is [PSCustomObject] -and $health.status -ceq 'ok' -and $health.app -ceq 'MiniTasks') {
                        Write-Host 'MiniTasks health check passed (HTTP 200).'
                        return
                    }
                    $lastFailure = 'The response JSON did not identify a healthy MiniTasks application.'
                } catch {
                    $lastFailure = 'The HTTP 200 response was not valid health JSON.'
                }
            }
        } catch {
            $lastFailure = 'The application could not be reached within the request timeout.'
        } finally {
            if ($null -ne $response) { $response.Dispose() }
        }

        if ($attempt -lt $Attempts) {
            Write-Host "Health check attempt $attempt failed; retrying."
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    throw "MiniTasks health check failed after $Attempts attempts. $lastFailure"
} finally {
    $client.Dispose()
    $handler.Dispose()
}
