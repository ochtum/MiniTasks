# Shared by local-only scripts. Do not call hosted Supabase APIs from this helper.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:MiniTasksRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:RequiredSupabaseVersion = '2.114.0'
$script:DemoPassword = 'MiniTasks-local-2026!'
$script:DemoEmails = @('alice@minitasks.local', 'bob@minitasks.local')

function Assert-MiniTasksLocal {
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
    $configPath = Join-Path $script:MiniTasksRoot 'supabase/config.toml'
    $config = Get-Content -LiteralPath $configPath -Raw
    if ($config -notmatch '(?m)^project_id\s*=\s*"minitasks"\s*$') {
        throw 'Refusing operation: project_id must be minitasks.'
    }
    foreach ($section in @{ api = 54421; db = 54422; studio = 54423; local_smtp = 54424 }.GetEnumerator()) {
        $pattern = '(?ms)^\[' + [regex]::Escape($section.Key) + '\]\s*\r?\n(?:(?!^\[).)*?^port\s*=\s*' + $section.Value + '\s*$'
        if ($config -notmatch $pattern) { throw "Refusing operation: unexpected $($section.Key) port." }
    }
    if (Test-Path -LiteralPath (Join-Path $script:MiniTasksRoot 'supabase/.temp/project-ref')) {
        throw 'Refusing operation: this local demo must not be linked to a hosted project.'
    }
    if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) { throw 'Install Supabase CLI 2.114.0 first.' }
    $version = (& supabase --version 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -ne $script:RequiredSupabaseVersion) {
        throw "Supabase CLI $script:RequiredSupabaseVersion is required; other versions are not accepted."
    }
}

function Invoke-MiniTasksSupabase {
    param([Parameter(Mandatory)][string[]] $Arguments, [switch] $ShowOutput)
    # Callers use a fixed argument list; never accept --linked, --db-url or a remote ref.
    if ($Arguments -match '^--(linked|db-url|project-ref|all|no-backup)(=|$)') {
        throw 'Refusing non-local or destructive Supabase options.'
    }
    $output = & supabase @Arguments --workdir $script:MiniTasksRoot 2>&1
    $exitCode = $LASTEXITCODE
    $result = ($output | Out-String).Trim()
    if ($exitCode -ne 0) {
        # CLI output can include local admin keys; do not repeat it in logs.
        throw "Supabase $($Arguments[0]) failed (exit $exitCode). Inspect the dedicated minitasks containers locally."
    }
    if ($ShowOutput) { Write-Host $result }
    return $result
}

function Get-MiniTasksStatus {
    $raw = Invoke-MiniTasksSupabase -Arguments @('status', '-o', 'json')
    $begin = $raw.IndexOf('{')
    $end = $raw.LastIndexOf('}')
    if ($begin -lt 0 -or $end -le $begin) { throw 'Local Supabase status was not JSON.' }
    $status = $raw.Substring($begin, $end - $begin + 1) | ConvertFrom-Json
    $apiUrl = [uri] $status.API_URL
    if ($apiUrl.Scheme -ne 'http' -or $apiUrl.Host -ne '127.0.0.1' -or $apiUrl.Port -ne 54421 -or $apiUrl.AbsolutePath -ne '/') {
        throw 'Refusing request: expected local API http://127.0.0.1:54421.'
    }
    return $status
}

function Get-MiniTasksStatusValue {
    param([Parameter(Mandatory)] $Status, [Parameter(Mandatory)][string[]] $Names)
    foreach ($name in $Names) {
        $property = $Status.PSObject.Properties[$name]
        if ($property -and $property.Value) { return [string] $property.Value }
    }
    throw "Local status is missing the required field: $($Names -join ' or ')."
}

function Invoke-MiniTasksHttp {
    param(
        [Parameter(Mandatory)] $Status,
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][hashtable] $Headers,
        $Body
    )
    $apiUrl = [uri] $Status.API_URL
    if ($apiUrl.Scheme -ne 'http' -or $apiUrl.Host -ne '127.0.0.1' -or $apiUrl.Port -ne 54421) { throw 'Only the MiniTasks loopback API is allowed.' }
    if ($Path -notmatch '^/(auth/v1|rest/v1)/') { throw 'Unexpected local API path.' }
    $parameters = @{
        Uri = $apiUrl.AbsoluteUri.TrimEnd('/') + $Path
        Method = $Method
        Headers = $Headers
        SkipHttpErrorCheck = $true
        TimeoutSec = 30
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 8 -Compress
    }
    $response = Invoke-WebRequest @parameters
    $json = $null
    if ($response.Content) {
        try { $json = ConvertFrom-Json -InputObject $response.Content -NoEnumerate } catch { }
    }
    return [pscustomobject]@{ StatusCode = [int] $response.StatusCode; Json = $json }
}

function Get-MiniTasksLogin {
    param([Parameter(Mandatory)] $Status, [Parameter(Mandatory)][string] $Email)
    $key = Get-MiniTasksStatusValue -Status $Status -Names @('PUBLISHABLE_KEY', 'ANON_KEY')
    $response = Invoke-MiniTasksHttp -Status $Status -Method POST -Path '/auth/v1/token?grant_type=password' -Headers @{ apikey = $key } -Body @{ email = $Email; password = $script:DemoPassword }
    if ($response.StatusCode -ne 200) { throw "Local demo login failed for $Email (HTTP $($response.StatusCode)). Run setup-local.ps1 first." }
    return $response.Json
}
