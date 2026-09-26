# Replays every live conversation through the CURRENT adapter and engine (idempotent), e.g. after an upgrade.
param([string] $Config = '')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
Start-Transcript -Path (Join-Path $PSScriptRoot "logs\reingest-$(Get-Date -Format yyyyMMdd-HHmmss).log") | Out-Null
try {
    Write-Host '=== Re-ingest all conversations ==='
    Invoke-Vrl $cfg (@('reingest', '--source', 'omnichannel') + (Get-VrlAoaiArgs $cfg))
    Write-Host '=== Ledger ==='
    Invoke-Vrl $cfg @('show', '--source', 'omnichannel')
}
finally { Stop-Transcript | Out-Null }
