[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Supabase.Local.ps1')
Assert-MiniTasksLocal
Invoke-MiniTasksSupabase -Arguments @('stop', '--project-id', 'minitasks') | Out-Null
Write-Host 'Stopped minitasks only. Local database data is retained.'
