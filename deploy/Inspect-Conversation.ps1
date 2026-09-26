# Read-only: shows how the adapter maps one Contact Center conversation (handling mode, bot outcome, identity, text).
param([Parameter(Mandatory = $true)][string] $Id, [string] $Config = '')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
Invoke-Vrl $cfg @('inspect-conversation', '--id', $Id.Trim().Trim('{', '}'))
