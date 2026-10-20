[CmdletBinding()]
param([switch] $SkipPgTap)

. (Join-Path $PSScriptRoot 'Supabase.Local.ps1')
Assert-MiniTasksLocal
$status = Get-MiniTasksStatus
if (-not $SkipPgTap) {
    Write-Host 'Running transaction-isolated database/RLS tests...'
    Invoke-MiniTasksSupabase -Arguments @('test', 'db', '--local') -ShowOutput | Out-Null
}

$publicKey = Get-MiniTasksStatusValue -Status $status -Names @('PUBLISHABLE_KEY', 'ANON_KEY')
$alice = Get-MiniTasksLogin -Status $status -Email $script:DemoEmails[0]
$bob = Get-MiniTasksLogin -Status $status -Email $script:DemoEmails[1]
$aliceHeaders = @{ apikey = $publicKey; Authorization = "Bearer $($alice.access_token)"; Prefer = 'return=representation' }
$bobHeaders = @{ apikey = $publicKey; Authorization = "Bearer $($bob.access_token)"; Prefer = 'return=representation' }
$cleanup = [Collections.Generic.List[object]]::new()
$script:RestChecks = 0

function Assert-RestCheck {
    param([bool] $Condition, [string] $Description)
    if (-not $Condition) { throw "REST isolation check failed: $Description" }
    $script:RestChecks++
    Write-Host "PASS: $Description"
}

try {
    Assert-RestCheck ($alice.user.id -ne $bob.user.id) 'two password logins belong to different users'
    $suffix = [guid]::NewGuid().ToString('N')
    $createdA = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/rest/v1/tasks' -Headers $aliceHeaders -Body @{ title = "REST Alice $suffix" }
    Assert-RestCheck ($createdA.StatusCode -eq 201 -and $createdA.Json.Count -eq 1) 'Alice can create her task'
    $taskA = $createdA.Json[0]
    $cleanup.Add(@{ Id = $taskA.id; Headers = $aliceHeaders })
    Assert-RestCheck ($taskA.owner_id -eq $alice.user.id -and -not $taskA.is_completed -and $taskA.created_at) 'default owner, completion and timestamp are supplied'
    $createdB = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/rest/v1/tasks' -Headers $bobHeaders -Body @{ title = "REST Bob $suffix"; owner_id = $bob.user.id }
    Assert-RestCheck ($createdB.StatusCode -eq 201 -and $createdB.Json.Count -eq 1) 'Bob can create his own task'
    $taskB = $createdB.Json[0]
    $cleanup.Add(@{ Id = $taskB.id; Headers = $bobHeaders })

    foreach ($userCase in @(@{ Login = $alice; Headers = $aliceHeaders }, @{ Login = $bob; Headers = $bobHeaders })) {
        # No owner filter: the database must enforce isolation itself.
        $list = Invoke-MiniTasksHttp -Status $status -Method GET -Path '/rest/v1/tasks?select=id,owner_id,title,is_completed,created_at&order=id.desc' -Headers $userCase.Headers
        $foreignRows = @($list.Json | Where-Object owner_id -ne $userCase.Login.user.id)
        Assert-RestCheck ($list.StatusCode -eq 200 -and $list.Json.Count -ge 1 -and $foreignRows.Count -eq 0) 'unfiltered list exposes only the signed-in user rows'
    }
    $crossRead = Invoke-MiniTasksHttp -Status $status -Method GET -Path "/rest/v1/tasks?id=eq.$($taskB.id)&select=*" -Headers $aliceHeaders
    Assert-RestCheck ($crossRead.StatusCode -eq 200 -and $crossRead.Json.Count -eq 0) 'Alice cannot read Bob task by its known ID'
    $crossUpdate = Invoke-MiniTasksHttp -Status $status -Method PATCH -Path "/rest/v1/tasks?id=eq.$($taskB.id)" -Headers $aliceHeaders -Body @{ title = 'stolen' }
    Assert-RestCheck ($crossUpdate.StatusCode -eq 200 -and $crossUpdate.Json.Count -eq 0) 'Alice cannot update Bob task by its known ID'
    $crossDelete = Invoke-MiniTasksHttp -Status $status -Method DELETE -Path "/rest/v1/tasks?id=eq.$($taskB.id)" -Headers $aliceHeaders
    Assert-RestCheck ($crossDelete.StatusCode -eq 200 -and $crossDelete.Json.Count -eq 0) 'Alice cannot delete Bob task by its known ID'
    $bobRead = Invoke-MiniTasksHttp -Status $status -Method GET -Path "/rest/v1/tasks?id=eq.$($taskB.id)&select=*" -Headers $bobHeaders
    Assert-RestCheck ($bobRead.StatusCode -eq 200 -and $bobRead.Json.Count -eq 1 -and $bobRead.Json[0].title -eq $taskB.title) 'Bob task remains unchanged after cross-user attempts'

    $forged = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/rest/v1/tasks' -Headers $aliceHeaders -Body @{ title = "forged $suffix"; owner_id = $bob.user.id }
    Assert-RestCheck ($forged.StatusCode -eq 403 -and $forged.Json.code -eq '42501') 'Alice cannot create a task owned by Bob'
    $transfer = Invoke-MiniTasksHttp -Status $status -Method PATCH -Path "/rest/v1/tasks?id=eq.$($taskA.id)" -Headers $aliceHeaders -Body @{ owner_id = $bob.user.id }
    Assert-RestCheck ($transfer.StatusCode -eq 403 -and $transfer.Json.code -eq '42501') 'Alice cannot change her task owner to Bob'
    $updated = Invoke-MiniTasksHttp -Status $status -Method PATCH -Path "/rest/v1/tasks?id=eq.$($taskA.id)" -Headers $aliceHeaders -Body @{ title = "updated $suffix"; is_completed = $true }
    Assert-RestCheck ($updated.StatusCode -eq 200 -and $updated.Json.Count -eq 1 -and $updated.Json[0].is_completed -and $updated.Json[0].title -eq "updated $suffix") 'Alice can edit title and completion of her own task'

    foreach ($badTitle in @('   ', ('a' * 201))) {
        $invalid = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/rest/v1/tasks' -Headers $aliceHeaders -Body @{ title = $badTitle }
        Assert-RestCheck ($invalid.StatusCode -eq 400 -and $invalid.Json.code -eq '23514') 'database rejects a blank or overlength title'
    }
    $anonymous = Invoke-MiniTasksHttp -Status $status -Method GET -Path '/rest/v1/tasks?select=*' -Headers @{ apikey = $publicKey }
    Assert-RestCheck ($anonymous.StatusCode -in @(401, 403)) 'public key alone cannot read tasks'
    $signup = Invoke-MiniTasksHttp -Status $status -Method POST -Path '/auth/v1/signup' -Headers @{ apikey = $publicKey } -Body @{ email = $script:DemoEmails[0]; password = $script:DemoPassword }
    Assert-RestCheck ($signup.StatusCode -eq 422 -and $signup.Json.error_code -eq 'signup_disabled') 'email password login is enabled while public signup is disabled'
    $invalidJwt = Invoke-MiniTasksHttp -Status $status -Method GET -Path '/rest/v1/tasks?select=*' -Headers @{ apikey = $publicKey; Authorization = 'Bearer invalid-test-token' }
    Assert-RestCheck ($invalidJwt.StatusCode -eq 401) 'invalid JWT is rejected'
    $deleted = Invoke-MiniTasksHttp -Status $status -Method DELETE -Path "/rest/v1/tasks?id=eq.$($taskA.id)" -Headers $aliceHeaders
    Assert-RestCheck ($deleted.StatusCode -eq 200 -and $deleted.Json.Count -eq 1) 'Alice can delete her own task'
    $afterDelete = Invoke-MiniTasksHttp -Status $status -Method GET -Path "/rest/v1/tasks?id=eq.$($taskA.id)&select=*" -Headers $aliceHeaders
    Assert-RestCheck ($afterDelete.StatusCode -eq 200 -and $afterDelete.Json.Count -eq 0) 'own task deletion persists'
}
finally {
    # Remove only this run's rows, with the corresponding user's JWT. No admin bypass.
    foreach ($item in $cleanup) {
        $result = Invoke-MiniTasksHttp -Status $status -Method DELETE -Path "/rest/v1/tasks?id=eq.$($item.Id)" -Headers $item.Headers
        if ($result.StatusCode -notin @(200, 204)) { Write-Warning 'Could not clean up one REST test row.' }
    }
}
Write-Host "REST isolation tests passed: $script:RestChecks checks. Tokens and API keys were not printed."
