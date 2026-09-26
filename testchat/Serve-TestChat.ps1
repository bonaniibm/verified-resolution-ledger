# Serves ONE test page on http://localhost:<Port>/ (the chat widget does not load from file://).
# Widget ids come from the testChat section of deploy/config.json, so no environment values live in the pages.
param([string] $Page = 'test-chat.html', [int] $Port = 8080, [ValidateSet('human', 'bot')][string] $Widget = 'human', [string] $Config = '')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy\Config.ps1')
$cfg = Import-VrlConfig -Path $Config
$tc = $cfg.testChat
$appId = if ($Widget -eq 'bot') { $tc.botWidgetAppId } else { $tc.humanWidgetAppId }
if (-not $tc -or -not $tc.orgId -or -not $tc.orgUrl -or -not $appId -or "$appId$($tc.orgId)" -match '<') {
    throw "Fill in testChat.orgId, testChat.orgUrl and testChat.$Widget" + "WidgetAppId in $($cfg.path) (from the chat widget's code snippet)."
}
$email = if ($tc.testCustomerEmail) { $tc.testCustomerEmail } else { 'testcustomer.a@example.com' }
$html = (Get-Content (Join-Path $PSScriptRoot $Page) -Raw -Encoding UTF8).
    Replace('{{APP_ID}}', $appId).Replace('{{ORG_ID}}', $tc.orgId).Replace('{{ORG_URL}}', $tc.orgUrl.TrimEnd('/')).Replace('{{TEST_EMAIL}}', $email)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($html)

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Test portal running at http://localhost:$Port/  (close this window to stop)"
Start-Process "http://localhost:$Port/"
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        if ($ctx.Request.Url.AbsolutePath -in @('/', "/$Page")) {
            $ctx.Response.ContentType = 'text/html; charset=utf-8'
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else { $ctx.Response.StatusCode = 404 }
        $ctx.Response.Close()
    }
}
finally { $listener.Stop() }
