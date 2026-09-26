#Requires -Version 5.1
<#
.SYNOPSIS
    Deploys the Verified Resolution Ledger into a Dataverse environment and an Azure resource group that may live in
    DIFFERENT Entra tenants.

.DESCRIPTION
    Two install modes (config "install", or -Install):
      managed     customer environments: imports the managed solution package; never customises it, never seeds
                  synthetic data. Default steps: signin,solution,dataverse,appreg,infra,secret,code,appuser,endpoint,verify
      source      development environments: builds schema, roles and web resources from source (unmanaged).
                  Default steps: signin,dataverse,appreg,infra,secret,code,appuser,seed,webres,verify

    Phases (run the mode's defaults, or pick with -Steps):
      signin      az login to both tenants (interactive, only if not signed in yet)
      solution    import or upgrade the managed solution zip (-SolutionZip, config solution.zip, or newest artifacts/solution/*_managed.zip)
      promote     export the solution from -SourceDataverseUrl, async-import into -DataverseUrl (large orgs)
      dataverse   vrl provision + seed-config (provision is skipped when the managed solution is installed)
      appreg      app registration "vrl-ledger-dataverse"          (Dataverse tenant) - Function -> Dataverse identity
      infra       Bicep: Function App, Service Bus, Storage, KV...   (Azure tenant)
      secret      client secret -> Key Vault (only if missing, or with -RotateSecret)
      code        dotnet publish + zip deploy to the Function App
      appuser     Dataverse application user + roles for the app registration (vrl assign-app-user)
      seed        synthetic data through the real pipeline
      webres      side pane + dashboard web resources
      endpoint    Service Bus service endpoint + step on msdyn_ocliveworkitem (needs Contact Center installed)
      verify      ledger counts from Dataverse + Function App function list

    Security: Azure resources use a user-assigned managed identity. Only the Dataverse hop uses a client secret,
    because the managed identity cannot obtain tokens in another tenant; the secret goes straight from Entra into
    Key Vault and is never printed or written to the transcript.

    Every run writes a transcript to deploy/logs/.

.EXAMPLE
    # Values come from deploy/config.json (copy deploy/config.example.json). Any parameter overrides the config.
    ./Deploy-Ledger.ps1
    ./Deploy-Ledger.ps1 -Steps code,webres
    ./Deploy-Ledger.ps1 -Config deploy/config.dev.json -Steps verify
#>
[CmdletBinding()]
param(
    [string]   $Config = '',
    [string]   $DataverseUrl,
    [string]   $DataverseTenantId,
    [string]   $AzureTenantId,
    [string]   $SubscriptionId,
    [string]   $ResourceGroup,
    [string]   $BaseName,
    [string]   $SourceDataverseUrl = '',
    [string]   $FunctionLocation,
    [string]   $OpenAiAccountName,
    [int]      $SyntheticCustomers = 0,
    [ValidateSet('', 'managed', 'source')] [string] $Install = '',
    [string]   $SolutionZip = '',
    [string[]] $Steps = @(),
    [switch]   $RotateSecret
)

# Fill anything not passed explicitly from deploy/config.json (or -Config / VRL_CONFIG).
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = $null
try { $cfg = Import-VrlConfig -Path $Config }
catch {
    # A config file is optional when every required value is passed explicitly (e.g. from a CI pipeline).
    if (-not ($DataverseUrl -and $DataverseTenantId -and $AzureTenantId -and $SubscriptionId -and $ResourceGroup -and $BaseName)) { throw }
}
if (-not $DataverseUrl) { $DataverseUrl = $cfg.dataverse.url }
if (-not $DataverseTenantId) { $DataverseTenantId = $cfg.dataverse.tenantId }
if (-not $AzureTenantId) { $AzureTenantId = $cfg.azure.tenantId }
if (-not $SubscriptionId) { $SubscriptionId = $cfg.azure.subscriptionId }
if (-not $ResourceGroup) { $ResourceGroup = $cfg.azure.resourceGroup }
if (-not $BaseName) { $BaseName = $cfg.azure.baseName }
if (-not $FunctionLocation -and $cfg.azure.functionLocation) { $FunctionLocation = $cfg.azure.functionLocation }
if (-not $PSBoundParameters.ContainsKey('OpenAiAccountName') -and $cfg.openAi) { $OpenAiAccountName = "$($cfg.openAi.accountName)" }
if (-not $Install) { $Install = if ($cfg -and $cfg.install) { "$($cfg.install)".ToLowerInvariant() } else { 'source' } }
if ($Install -notin @('managed', 'source')) { throw "install must be 'managed' or 'source' (got '$Install')." }
if (-not $SolutionZip -and $cfg -and $cfg.solution -and $cfg.solution.zip) { $SolutionZip = $cfg.solution.zip }
if (-not $Steps -or $Steps.Count -eq 0) {
    $Steps = if ($Install -eq 'managed') { @('signin', 'solution', 'dataverse', 'appreg', 'infra', 'secret', 'code', 'appuser', 'endpoint', 'verify') }
             else { @('signin', 'dataverse', 'appreg', 'infra', 'secret', 'code', 'appuser', 'seed', 'webres', 'verify') }
}
if (-not $SyntheticCustomers) { $SyntheticCustomers = if ($cfg.synthetic -and $cfg.synthetic.customers) { [int]$cfg.synthetic.customers } else { 150 } }

# Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop', and az writes warnings to stderr.
# Native calls are therefore checked explicitly via $LASTEXITCODE (Invoke-Native) instead.
$ErrorActionPreference = 'Continue'
# With "powershell -File", "-Steps a,b,c" arrives as one string; normalise to a list either way.
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root 'tools/Vrl.Tools'
$logDir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Start-Transcript -Path (Join-Path $logDir "deploy-$stamp.log") | Out-Null
Write-Host "PowerShell $($PSVersionTable.PSVersion); dotnet $(dotnet --version)"
Write-Host "Install mode: $Install; steps: $($Steps -join ', ')"
az version -o table

$appName = 'vrl-ledger-dataverse'
# One state file per instance (BaseName) so several ledger instances can be deployed side by side.
$stateFile = Join-Path $logDir "state-$BaseName.json"
$state = @{}
if (Test-Path $stateFile) {
    # ConvertFrom-Json -AsHashtable is PowerShell 7 only; convert manually for 5.1.
    (Get-Content $stateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $state[$_.Name] = $_.Value }
}
function Save-State { $state | ConvertTo-Json | Set-Content $stateFile }

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  [ok] $text" -ForegroundColor Green }
function Invoke-Native([scriptblock] $block, [string] $what) {
    # Route native output through the host so Windows PowerShell 5.1 transcripts capture it.
    & $block 2>&1 | ForEach-Object { "$_" } | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}
function Vrl { $vrlArgs = $args; Invoke-Native { dotnet run --project $cli -c Release -- @vrlArgs } "vrl $($vrlArgs[0])" }
function Use-Tenant([string] $tenant) {
    $sub = az account list --all --query "[?tenantId=='$tenant'] | [0].id" -o tsv 2>$null
    if (-not $sub) { throw "Not signed in to tenant $tenant. Run with -Steps signin first." }
    az account set --subscription $sub | Out-Null
}
function Use-Azure { az account set --subscription $SubscriptionId | Out-Null }
function Want($s) { $Steps -contains $s }
# Subscription owned by the Dataverse-tenant account; pins the CLI credential to that account for every vrl call.
function Get-DataverseSubscription {
    $sub = az account list --all --query "[?tenantId=='$DataverseTenantId'] | [0].id" -o tsv 2>$null
    if (-not $sub) { throw "Not signed in to tenant $DataverseTenantId. Run with -Steps signin first." }
    return $sub
}

try {
    # -----------------------------------------------------------------------------------------------------------
    if (Want 'signin') {
        Step 'Sign-in (browser windows may open)'
        foreach ($t in @($DataverseTenantId, $AzureTenantId) | Select-Object -Unique) {
            $signedIn = az account list --all --query "[?tenantId=='$t'] | length(@)" -o tsv 2>$null
            if ([int]$signedIn -gt 0) { Ok "Azure CLI already signed in to $t"; continue }
            Invoke-Native { az login --tenant $t --allow-no-subscriptions --only-show-errors -o none } "az login $t"
            Ok "Signed in to $t"
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'promote') {
        Step "Promote solution $SourceDataverseUrl -> $DataverseUrl (async import)"
        if (-not $SourceDataverseUrl) { throw '-SourceDataverseUrl is required for the promote step.' }
        Vrl promote --url $SourceDataverseUrl --target-url $DataverseUrl --subscription (Get-DataverseSubscription)
        Ok 'Solution imported'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'solution') {
        if ($Install -ne 'managed') { throw "The solution step installs the MANAGED package; set install to 'managed' (this environment is configured as 'source')." }
        if (-not $SolutionZip) {
            $SolutionZip = Get-ChildItem (Join-Path $root 'artifacts/solution') -Filter 'VerifiedResolutionLedger_*_managed.zip' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $SolutionZip) { throw 'No managed solution zip. Download VerifiedResolutionLedger_<version>_managed.zip from the GitHub release into artifacts\solution, or pass -SolutionZip.' }
        if (-not [System.IO.Path]::IsPathRooted($SolutionZip)) { $SolutionZip = Join-Path $root $SolutionZip }
        Step "Managed solution -> $DataverseUrl ($(Split-Path -Leaf $SolutionZip))"
        Vrl import-solution --file $SolutionZip --url $DataverseUrl --subscription (Get-DataverseSubscription)
        Ok 'Managed solution installed'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'dataverse') {
        Step "Dataverse schema, roles and configuration ($DataverseUrl)"
        Vrl provision --url $DataverseUrl --subscription (Get-DataverseSubscription)
        Vrl seed-config --url $DataverseUrl --subscription (Get-DataverseSubscription)
        Ok 'Schema provisioned'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'appreg') {
        Step "App registration '$appName' in Dataverse tenant"
        Use-Tenant $DataverseTenantId
        $appId = az ad app list --display-name $appName --query '[0].appId' -o tsv --only-show-errors
        if (-not $appId) {
            $appId = az ad app create --display-name $appName --sign-in-audience AzureADMyOrg --query appId -o tsv --only-show-errors
            Ok "Created app $appId"
        }
        else { Ok "App exists: $appId" }
        $sp = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv --only-show-errors
        if (-not $sp) { az ad sp create --id $appId -o none --only-show-errors; Ok 'Created service principal' }
        $state.appId = $appId; Save-State
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'infra') {
        Step "Azure infrastructure -> $ResourceGroup"
        Use-Azure
        $me = az ad signed-in-user show --query id -o tsv --only-show-errors
        $rgLocation = az group show -n $ResourceGroup --query location -o tsv
        if (-not $FunctionLocation) { $FunctionLocation = $rgLocation }

        $roles = az role assignment list --assignee $me --scope "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup" --include-inherited --query '[].roleDefinitionName' -o tsv
        if (-not ($roles | Where-Object { $_ -in @('Owner', 'User Access Administrator', 'Role Based Access Control Administrator') })) {
            throw "Your account needs Owner, User Access Administrator or RBAC Administrator on $ResourceGroup to create the managed-identity role assignments. Current roles: $($roles -join ', ')"
        }
        Ok "RBAC rights confirmed ($($roles -join ', '))"

        $flex = az functionapp list-flexconsumption-locations --query '[].name' -o tsv
        if ($flex -notcontains $FunctionLocation) {
            $preferred = @('southindia', 'westindia', 'uaenorth', 'southeastasia', 'eastus') | Where-Object { $flex -contains $_ } | Select-Object -First 1
            Write-Warning "Flex Consumption is not offered in '$FunctionLocation'. Using '$preferred' for the Function App only."
            $FunctionLocation = $preferred
        }
        Ok "Function App region: $FunctionLocation"

        if (-not $state.appId) { throw 'Run the appreg step first (the Function App settings need the app id).' }
        $deployment = "vrl-$stamp"
        Invoke-Native {
            az deployment group create -g $ResourceGroup -n $deployment `
                --template-file (Join-Path $root 'infra/main.bicep') `
                --parameters baseName=$BaseName dataverseUrl=$DataverseUrl functionLocation=$FunctionLocation `
                openAiAccountName=$OpenAiAccountName dataverseTenantId=$DataverseTenantId dataverseClientId=$($state.appId) `
                deployerPrincipalId=$me -o none
        } 'Bicep deployment'
        $out = az deployment group show -g $ResourceGroup -n $deployment --query properties.outputs -o json | ConvertFrom-Json
        $state.functionApp = $out.functionAppName.value
        $state.keyVault = $out.keyVaultName.value
        $state.serviceBus = $out.serviceBusNamespace.value
        $state.openAiEndpoint = $out.openAiEndpoint.value
        $state.managedIdentityClientId = $out.managedIdentityClientId.value
        Save-State
        Ok "Deployed: $($state.functionApp), $($state.serviceBus), $($state.keyVault)"
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'secret') {
        Step 'Dataverse client secret -> Key Vault'
        Use-Azure
        $exists = az keyvault secret show --vault-name $state.keyVault -n dataverse-client-secret --query id -o tsv 2>$null
        if ($exists -and -not $RotateSecret) { Ok 'Secret already in Key Vault (use -RotateSecret to replace)' }
        else {
            Use-Tenant $DataverseTenantId
            $secret = az ad app credential reset --id $state.appId --append --display-name "vrl-func-$stamp" --years 1 --query password -o tsv --only-show-errors
            Use-Azure
            $tmp = New-TemporaryFile
            try {
                Set-Content -Path $tmp -Value $secret -NoNewline
                $expires = (Get-Date).AddYears(1).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
                # RBAC role for the deployer can take a minute to propagate after 'infra'.
                for ($i = 1; $i -le 6; $i++) {
                    az keyvault secret set --vault-name $state.keyVault -n dataverse-client-secret --file $tmp --encoding utf-8 --expires $expires -o none 2>$null
                    if ($LASTEXITCODE -eq 0) { break }
                    Write-Host "  waiting for Key Vault permissions ($i/6)..."; Start-Sleep -Seconds 20
                }
                if ($LASTEXITCODE -ne 0) { throw 'Could not write the secret to Key Vault.' }
            }
            finally { Remove-Item $tmp -Force; $secret = $null }
            Ok 'Secret stored (expires in 1 year)'
            az functionapp restart -g $ResourceGroup -n $state.functionApp -o none 2>$null
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'code') {
        Step "Function App code -> $($state.functionApp)"
        Use-Azure
        $publish = Join-Path $root 'artifacts/functions'
        $zip = Join-Path $root 'artifacts/functions.zip'
        Remove-Item $publish, $zip -Recurse -Force -ErrorAction SilentlyContinue
        Invoke-Native { dotnet publish (Join-Path $root 'src/Vrl.Functions') -c Release -o $publish --nologo } 'dotnet publish'
        # Build the zip with forward-slash entry names. Windows PowerShell's Compress-Archive writes backslashes,
        # which the Linux Flex Consumption host rejects ("Cannot find required .azurefunctions directory").
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $publishFull = (Resolve-Path $publish).Path.TrimEnd('\', '/')
        $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -Path $publishFull -Recurse -File -Force | ForEach-Object {
                $entry = $_.FullName.Substring($publishFull.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally { $archive.Dispose() }
        Invoke-Native { az functionapp deployment source config-zip -g $ResourceGroup -n $state.functionApp --src $zip --timeout 600 -o none } 'zip deploy'
        Start-Sleep -Seconds 20
        $fns = az functionapp function list -g $ResourceGroup -n $state.functionApp --query '[].name' -o tsv
        Write-Host "  functions: $($fns -join ', ')"
        Ok 'Code deployed'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'appuser') {
        Step "Dataverse application user for $($state.appId)"
        Vrl assign-app-user --url $DataverseUrl --subscription (Get-DataverseSubscription) --app-id $state.appId --roles 'Basic User,VRL Ledger Service'
        Ok 'Application user ready'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'seed') {
        # Only reachable in managed mode when requested explicitly with -Steps (the managed defaults never seed).
        if ($Install -eq 'managed') { Write-Warning 'Seeding SYNTHETIC data into a managed environment because -Steps asked for it. Rows are tagged sourceSystem=synthetic.' }
        Step "Synthetic data ($SyntheticCustomers customers, 30 days)"
        $seedArgs = @('seed', '--url', $DataverseUrl, '--subscription', (Get-DataverseSubscription), '--customers', $SyntheticCustomers)
        if ($state.openAiEndpoint) {
            $seedArgs += @('--aoai-endpoint', $state.openAiEndpoint, '--aoai-deployment', $(if ($cfg.openAi -and $cfg.openAi.deployment) { $cfg.openAi.deployment } else { 'text-embedding-3-small' }), '--azure-subscription', $SubscriptionId)
        }
        Vrl @seedArgs
        Ok 'Seeded'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'webres') {
        Step 'Web resources'
        if (Get-Command node -ErrorAction SilentlyContinue) { Invoke-Native { node (Join-Path $root 'webresources/build.mjs') } 'build web resources' }
        elseif (-not (Test-Path (Join-Path $root 'webresources/dist'))) { throw 'Node.js is required to build the web resources (https://nodejs.org).' }
        Vrl deploy-webresources --url $DataverseUrl --subscription (Get-DataverseSubscription) --path (Join-Path $root 'webresources/dist')
        Ok "Dashboard: $($DataverseUrl.TrimEnd('/'))/WebResources/bpc_/vrl/dashboard.html"
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'endpoint') {
        Step 'Dataverse service endpoint -> Service Bus (live conversations)'
        Use-Azure
        $env:VRL_SB_SAS_KEY = az servicebus queue authorization-rule keys list -g $ResourceGroup --namespace-name $state.serviceBus `
            --queue-name vrl-dataverse-events --name dataverse-send --query primaryKey -o tsv
        try { Vrl register-endpoint --url $DataverseUrl --subscription (Get-DataverseSubscription) --sb-namespace $state.serviceBus }
        finally { Remove-Item Env:\VRL_SB_SAS_KEY -ErrorAction SilentlyContinue }
        Ok 'Closed conversations now flow to the ledger'
    }

    # -----------------------------------------------------------------------------------------------------------
    if (Want 'verify') {
        Step 'Verify'
        Vrl verify --url $DataverseUrl --subscription (Get-DataverseSubscription)
        if ($state.functionApp) {
            Use-Azure
            az functionapp function list -g $ResourceGroup -n $state.functionApp --query '[].name' -o tsv
        }
    }

    Step 'Done'
}
catch {
    Write-Host "`nFAILED: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    Stop-Transcript | Out-Null
}
