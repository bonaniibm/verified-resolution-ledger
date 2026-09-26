# Read-only: pairwise same-issue scores and every contributing signal for one customer's ledger history.
# -Customer accepts a contact id (GUID) or a ledger customer key (contact:..., email:..., phone:...).
param([Parameter(Mandatory = $true)][string] $Customer, [string] $Config = '')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
$key = $Customer.Trim().Trim('{', '}')
if ($key -match '^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$') { $key = 'contact:' + ($key -replace '-', '').ToLowerInvariant() }
Invoke-Vrl $cfg @('explain', '--customer', $key)
