# Builds the distributable Dataverse solution (unmanaged + managed zips) from the configured environment.
# Shows what is outside the allow-list, asks before removing it from the solution (components stay in the
# environment), exports, and verifies the managed zip contains only ledger components.
param([string] $Version = '1.0.0.0', [string] $Config = '', [switch] $Yes)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Config.ps1')
$cfg = Import-VrlConfig -Path $Config
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\solution'
Start-Transcript -Path (Join-Path $PSScriptRoot "logs\package-$(Get-Date -Format yyyyMMdd-HHmmss).log") | Out-Null
try {
    Write-Host "=== Package VerifiedResolutionLedger $Version from $($cfg.dataverse.url) ==="
    $sub = Get-VrlDataverseSubscription $cfg
    $cli = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/Vrl.Tools'
    $base = @('package', '--version', $Version, '--out', $out, '--url', $cfg.dataverse.url, '--subscription', $sub)
    $dry = dotnet run --project $cli -c Release -- @base 2>&1 | ForEach-Object { "$_" }
    $dry | Out-Host
    if ($LASTEXITCODE -eq 0) { Write-Host "`nPackage ready in $out" -ForegroundColor Green; return }
    if (-not ($dry -match 'WOULD remove')) { throw 'Packaging failed (see above).' }
    if (-not $Yes) {
        $answer = Read-Host "`nRemove the components listed above from the solution (they stay in the environment)? [y/N]"
        if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled; nothing changed.'; return }
    }
    dotnet run --project $cli -c Release -- @base --curate 2>&1 | ForEach-Object { "$_" } | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed (see above).' }
    Write-Host "`nPackage ready in $out" -ForegroundColor Green
    Get-ChildItem $out -Filter "VerifiedResolutionLedger_$($Version.Replace('.','_'))*.zip" | ForEach-Object { "  $($_.Name)  $([math]::Round($_.Length/1KB)) KB" } | Out-Host
}
finally { Stop-Transcript | Out-Null }
