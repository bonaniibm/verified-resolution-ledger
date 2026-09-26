#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end smoke test of the deployed cloud path:
    HTTP -> Service Bus session queue -> Function -> Key Vault secret -> Dataverse (cross-tenant) + Azure OpenAI.

.DESCRIPTION
    Posts two interactions for the same (synthetic) customer: a bot conversation the platform reports as
    "implied resolved", then a same-issue voice call 30 minutes later. Expected ledger result:
      SMOKE-...-1  FalseContainment  (RepeatAfterBot)
      SMOKE-...-2  Pending           (its own window is still open)
    Also registers Microsoft.AlertsManagement so App Insights' built-in Failure Anomalies alert can deploy.
#>
param([string] $Config = '')

. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
$DataverseUrl = $cfg.dataverse.url
$DataverseTenantId = $cfg.dataverse.tenantId
$SubscriptionId = $cfg.azure.subscriptionId
$ResourceGroup = $cfg.azure.resourceGroup
$BaseName = $cfg.azure.baseName

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $PSScriptRoot 'logs'
Start-Transcript -Path (Join-Path $logDir "smoketest-$(Get-Date -Format yyyyMMdd-HHmmss).log") | Out-Null

try {
    $state = Get-VrlState $cfg
    az account set --subscription $SubscriptionId | Out-Null

    Write-Host '=== Register Microsoft.AlertsManagement (fixes the Failure Anomalies alert rule) ==='
    az provider register --namespace Microsoft.AlertsManagement -o none

    Write-Host "=== Function App $($state.functionApp) ==="
    # Flex Consumption apps get a unique default host name (<name>-<hash>.<region>-01.azurewebsites.net).
    $hostName = az functionapp show -g $ResourceGroup -n $state.functionApp --query "defaultHostName || properties.defaultHostName || hostNames[0]" -o tsv
    if (-not $hostName) { throw 'Could not resolve the Function App host name.' }
    Write-Host "  host: $hostName"
    $key = az functionapp keys list -g $ResourceGroup -n $state.functionApp --query functionKeys.default -o tsv
    if (-not $key) { throw 'Could not read the function key.' }
    $uri = "https://$hostName/api/interactions?code=$key"
    $safeUri = "https://$hostName/api/interactions?code=***"

    $run = Get-Date -Format 'yyyyMMddHHmmss'
    $t0 = (Get-Date).ToUniversalTime().AddMinutes(-60)
    function Post($suffix, $startMin, $durMin, $channel, $mode, $outcome, $summary) {
        $body = @{
            id               = '00000000-0000-0000-0000-000000000000'
            sourceSystem     = 'smoketest'
            sourceRecordId   = "SMOKE-$run-$suffix"
            channel          = $channel
            handlingMode     = $mode
            nativeBotOutcome = $outcome
            startedOn        = $t0.AddMinutes($startMin).ToString('o')
            endedOn          = $t0.AddMinutes($startMin + $durMin).ToString('o')
            customer         = @{ email = "smoke.$run@example.com" }
            intentCode       = 'billing.refund.status'
            summary          = $summary
            botTopic         = 'Refund status'
            aiCredits        = 10
            handleMinutes    = $(if ($mode -eq 'HumanOnly') { 9 } else { 0 })
        } | ConvertTo-Json -Depth 5
        try {
            $resp = Invoke-WebRequest -Method Post -Uri $uri -Body $body -ContentType 'application/json' -UseBasicParsing -ErrorAction Stop
            Write-Host "  POST SMOKE-$run-$suffix -> HTTP $($resp.StatusCode)"
        }
        catch {
            # Never echo the function key.
            $msg = $_.Exception.Message -replace [regex]::Escape($key), '***'
            throw "POST to $safeUri failed: $msg"
        }
    }

    Write-Host '=== Posting two same-issue contacts ==='
    Post 1 0 6 'Chat' 'BotOnly' 'ResolvedImplied' 'Customer asks when the refund for ORD-7788123 will arrive; bot explained refund timelines.'
    Post 2 36 12 'Voice' 'HumanOnly' 'None' 'Refund for order ORD-7788123 still not received, customer calling again.'

    Write-Host '=== Waiting for the Function to process (cold start can take ~1 minute) ==='
    $cli = Join-Path $root 'tools/Vrl.Tools'
    $dvSub = az account list --all --query "[?tenantId=='$DataverseTenantId'] | [0].id" -o tsv
    for ($i = 1; $i -le 12; $i++) {
        Start-Sleep -Seconds 15
        $out = (dotnet run --project $cli -c Release -- show --url $DataverseUrl --subscription $dvSub --source smoketest 2>&1 | Out-String)
        $mine = ($out -split "`n") | Where-Object { $_ -match "SMOKE-$run" }
        Write-Host "  attempt $i"; $mine | ForEach-Object { Write-Host "    $_" }
        if (($mine | Where-Object { $_ -match 'FalseContainment' }) -and ($mine.Count -ge 2)) {
            Write-Host "`nPASS: cloud pipeline verified (HTTP -> Service Bus -> Function -> Dataverse + Azure OpenAI)." -ForegroundColor Green
            return
        }
    }
    Write-Host "`nNOT CONFIRMED: rows did not reach the expected verdicts in 3 minutes. Check Application Insights (appi-$BaseName) > Failures." -ForegroundColor Yellow
}
finally {
    # The default key was printed by an earlier version of this script; rotate it so any copy in logs is useless.
    if ($state -and $state.functionApp) {
        az functionapp keys set -g $ResourceGroup -n $state.functionApp --key-type functionKeys --key-name default -o none 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host '  function key rotated' }
    }
    Stop-Transcript | Out-Null
}
