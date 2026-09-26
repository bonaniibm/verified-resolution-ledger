# Lists ledger rows and verdicts for one source system (omnichannel = live Contact Center conversations).
param([string] $Source = 'omnichannel', [string] $Config = '')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
Invoke-Vrl $cfg @('show', '--source', $Source)
