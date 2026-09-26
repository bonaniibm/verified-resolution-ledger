# Shared helpers: loads deploy/config.json (git-ignored) and runs the vrl CLI against the configured environment.
# Dot-source it:  . (Join-Path $PSScriptRoot 'Config.ps1');  $cfg = Import-VrlConfig
# Select another file with -Path, or with the VRL_CONFIG environment variable (e.g. deploy\config.dev.json).

function Import-VrlConfig {
    param([string] $Path)
    if (-not $Path) { $Path = if ($env:VRL_CONFIG) { $env:VRL_CONFIG } else { Join-Path $PSScriptRoot 'config.json' } }
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path (Split-Path -Parent $PSScriptRoot) $Path }
    if (-not (Test-Path $Path)) {
        throw "Configuration not found: $Path`nCopy deploy\config.example.json to deploy\config.json and fill in your environment."
    }
    $cfg = Get-Content $Path -Raw | ConvertFrom-Json
    $missing = @()
    foreach ($p in 'dataverse.url', 'dataverse.tenantId', 'azure.tenantId', 'azure.subscriptionId', 'azure.resourceGroup', 'azure.baseName') {
        $v = $cfg
        foreach ($part in $p.Split('.')) { $v = if ($null -ne $v) { $v.$part } else { $null } }
        if (-not $v -or "$v" -match '<[^>]*>') { $missing += $p }
    }
    if ($missing) { throw "deploy config $Path is missing: $($missing -join ', ')" }
    # Optional sections left as <placeholders> are treated as not configured.
    foreach ($section in 'openAi', 'testChat') {
        if (-not $cfg.$section) { continue }
        foreach ($prop in @($cfg.$section.PSObject.Properties)) {
            if ($prop.Value -is [string] -and $prop.Value -match '<[^>]*>') { $cfg.$section.($prop.Name) = '' }
        }
    }
    $cfg | Add-Member -NotePropertyName path -NotePropertyValue $Path -Force
    return $cfg
}

# Subscription of the account signed in to the Dataverse tenant; pins the CLI credential for every vrl call.
function Get-VrlDataverseSubscription($cfg) {
    $t = $cfg.dataverse.tenantId
    $sub = az account list --all --query "[?tenantId=='$t'] | [0].id" -o tsv 2>$null
    if (-not $sub) { throw "Not signed in to the Dataverse tenant $t. Run: az login --tenant $t --allow-no-subscriptions" }
    return $sub
}

# Azure OpenAI embedding arguments for vrl commands that embed text (empty when no account is configured).
function Get-VrlAoaiArgs($cfg) {
    if (-not $cfg.openAi -or -not $cfg.openAi.accountName) { return @() }
    $endpoint = if ($cfg.openAi.endpoint) { $cfg.openAi.endpoint } else { "https://$($cfg.openAi.accountName).cognitiveservices.azure.com/" }
    $deployment = if ($cfg.openAi.deployment) { $cfg.openAi.deployment } else { 'text-embedding-3-small' }
    return @('--aoai-endpoint', $endpoint, '--aoai-deployment', $deployment, '--azure-subscription', $cfg.azure.subscriptionId)
}

# Runs a vrl CLI command against the configured Dataverse environment. Output goes to the host (and transcripts).
function Invoke-Vrl($cfg, [string[]] $VrlArgs) {
    $root = Split-Path -Parent $PSScriptRoot
    # Adapter settings (config "adapter" section) reach the CLI with the same names as the Function App settings.
    if ($cfg.adapter) {
        foreach ($prop in $cfg.adapter.PSObject.Properties) {
            if ($prop.Name -notlike '$*') { Set-Item -Path "Env:Vrl__Adapter__$($prop.Name)" -Value "$($prop.Value)" }
        }
    }
    $all = @($VrlArgs) + @('--url', $cfg.dataverse.url, '--subscription', (Get-VrlDataverseSubscription $cfg))
    dotnet run --project (Join-Path $root 'tools/Vrl.Tools') -c Release -- @all 2>&1 | ForEach-Object { "$_" } | Out-Host
}

function Get-VrlState($cfg) {
    $f = Join-Path $PSScriptRoot "logs/state-$($cfg.azure.baseName).json"
    if (-not (Test-Path $f)) { throw "No deployment state for '$($cfg.azure.baseName)' ($f). Run Deploy.cmd first." }
    return Get-Content $f -Raw | ConvertFrom-Json
}
