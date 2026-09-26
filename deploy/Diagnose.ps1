# Diagnoses the live pipeline: Dataverse (conversations, service-endpoint jobs) -> Service Bus -> Function App.
param([string] $Config = '')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
$state = Get-VrlState $cfg
$rg = $cfg.azure.resourceGroup
Start-Transcript -Path (Join-Path $PSScriptRoot "logs\diag-$(Get-Date -Format yyyyMMdd-HHmmss).log") | Out-Null
try {
    Write-Host '=== 1-2. Dataverse: recent conversations and service-endpoint system jobs ==='
    Invoke-Vrl $cfg @('diag')

    az account set --subscription $cfg.azure.subscriptionId | Out-Null
    Write-Host "=== 3. Service Bus $($state.serviceBus) ==="
    foreach ($q in 'vrl-dataverse-events', 'vrl-interactions') {
        $c = az servicebus queue show -g $rg --namespace-name $state.serviceBus -n $q --query countDetails -o json | ConvertFrom-Json
        Write-Host ("  {0,-22} active={1}  deadletter={2}" -f $q, $c.activeMessageCount, $c.deadLetterMessageCount)
    }

    Write-Host '=== 4. Function App (Application Insights, last 60 min) ==='
    az config set extension.use_dynamic_install=yes_without_prompt 2>$null | Out-Null
    $q = "union exceptions, traces, requests | where timestamp > ago(60m) | where itemType == 'exception' or severityLevel >= 2 or itemType == 'request' | project timestamp, itemType, operation_Name, success, msg = coalesce(outerMessage, message, name) | order by timestamp desc | take 25"
    az monitor app-insights query --app "appi-$($cfg.azure.baseName)" -g $rg --analytics-query $q --query "tables[0].rows" -o tsv 2>&1 | ForEach-Object { "  $_" } | Out-Host
}
finally { Stop-Transcript | Out-Null }
