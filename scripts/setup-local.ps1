[CmdletBinding()]
param([switch] $SkipUserSecrets)

. (Join-Path $PSScriptRoot 'Supabase.Local.ps1')
Assert-MiniTasksLocal
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker with a running Linux engine is required.' }
& docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker engine is not available.' }

Write-Host 'Starting dedicated local Supabase project minitasks (existing data is retained)...'
Invoke-MiniTasksSupabase -Arguments @('start') | Out-Null
Invoke-MiniTasksSupabase -Arguments @('migration', 'up', '--local') | Out-Null
$status = Get-MiniTasksStatus
$adminKey = Get-MiniTasksStatusValue -Status $status -Names @('SERVICE_ROLE_KEY', 'SECRET_KEY')
$adminHeaders = @{ apikey = $adminKey }
if ($adminKey.StartsWith('eyJ')) { $adminHeaders.Authorization = "Bearer $adminKey" }

# Create real password identities through Auth; raw auth.users seed rows cannot log in.
$usersResponse = Invoke-MiniTasksHttp -Status $status -Method GET -Path '/auth/v1/admin/users?page=1&per_page=1000' -Headers $adminHeaders
if ($usersResponse.StatusCode -ne 200) { throw "Local Auth user listing failed (HTTP $($usersResponse.StatusCode))." }
foreach ($email in $script:DemoEmails) {
    $existing = @($usersResponse.Json.users | Where-Object email -eq $email)
    if ($existing.Count -eq 0) {
        $created = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/auth/v1/admin/users' -Headers $adminHeaders -Body @{ email = $email; password = $script:DemoPassword; email_confirm = $true }
        if ($created.StatusCode -notin @(200, 201)) { throw "Local demo user creation failed (HTTP $($created.StatusCode))." }
    }
    Get-MiniTasksLogin -Status $status -Email $email | Out-Null
}

if (-not $SkipUserSecrets) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK is required to set app user secrets (or use -SkipUserSecrets in CI).' }
    $publicKey = Get-MiniTasksStatusValue -Status $status -Names @('PUBLISHABLE_KEY', 'ANON_KEY')
    & dotnet user-secrets set 'Supabase:Url' $status.API_URL --id 'MiniTasks.Web.Local' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not save local Supabase URL in .NET user secrets.' }
    & dotnet user-secrets set 'Supabase:ApiKey' $publicKey --id 'MiniTasks.Web.Local' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not save local public API key in .NET user secrets.' }
}
# Admin key and login tokens are intentionally never persisted or printed.
$adminKey = $null
$adminHeaders = $null
Write-Host 'Local setup complete: API http://127.0.0.1:54421, Studio http://127.0.0.1:54423; two demo users are ready.'
