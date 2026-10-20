[CmdletBinding()]
param([switch] $SupabaseOnly)

& (Join-Path $PSScriptRoot 'setup-local.ps1')
if (-not $?) { throw 'Local setup failed.' }
if ($SupabaseOnly) { return }
$project = Join-Path $PSScriptRoot '../src/MiniTasks.Web/MiniTasks.Web.csproj'
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    # --no-launch-profile would otherwise default to Production and skip user secrets.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    & dotnet run --project $project --no-launch-profile --urls 'http://localhost:5050'
    if ($LASTEXITCODE -ne 0) { throw 'MiniTasks.Web exited with an error.' }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}
