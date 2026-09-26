# Read-only: lists every component of the ledger solution (type and name) in the configured environment.
param([string] $Config = '', [string] $Solution = 'VerifiedResolutionLedger')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
Write-Host "=== $($cfg.dataverse.url) ==="
Invoke-Vrl $cfg @('solution-report', '--solution', $Solution)
