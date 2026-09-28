<#
.SYNOPSIS
    Runs the full Docker Compose stack for the public URL (https://chat.benda.io.vn by default).

.DESCRIPTION
    1. Builds and starts deploy/docker-compose.yml + deploy/docker-compose.public.yml.
    2. Waits for the public edge (http://127.0.0.1:8090) to answer.
    3. Adds the public URL to the Keycloak web client's redirect URIs and web origins in the LIVE
       realm. --import-realm only imports a realm that does not exist yet, so editing
       realm-export.json alone never reaches a database that already has the realm.
    4. Verifies that Keycloak, reached through the edge, advertises the public issuer — the check
       that proves sign-in will work through the tunnel.

    The Cloudflare side (DNS + tunnel ingress) is Add-CloudflareRoute.ps1, run once, as admin.
    Idempotent: safe to re-run after a code change to rebuild and restart.

.PARAMETER PublicHost
    Public hostname. Must match PUBLIC_HOST in deploy/.env if you set one there.

.PARAMETER NoBuild
    Start without rebuilding images.
#>
[CmdletBinding()]
param(
    [string] $PublicHost = 'chat.benda.io.vn',
    [int] $PublicPort = 8090,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

$compose = @('compose', '-f', 'deploy/docker-compose.yml', '-f', 'deploy/docker-compose.public.yml')
$realm = 'internalchat'
$client = 'internalchat-web'

# Windows PowerShell turns a native command's stderr into an error record, and under 'Stop' that
# aborts the script — docker writes its build progress to stderr. Native commands are judged by
# their exit code instead.
function Invoke-Native {
    param([Parameter(Mandatory)] [string] $File, [string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $File @Arguments 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# Windows PowerShell 5.1 strips embedded double quotes from native-command arguments, which
# mangles any JSON in a `bash -c` script. Shipping the script as base64 keeps it byte-exact.
function ConvertTo-BashCommand([string] $script) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($script -replace "`r", ''))
    "echo $([Convert]::ToBase64String($bytes)) | base64 -d | bash"
}

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

if (-not (Test-Path 'deploy/.env')) {
    throw 'deploy/.env is missing. Copy deploy/.env.example to deploy/.env and fill in the secrets first.'
}

if (-not (Test-Path 'deploy/certs/internalchat.crt')) {
    Write-Host 'Note: deploy/certs has no certificate. The public edge does not need one (Cloudflare terminates TLS).'
}

Write-Step "Starting the stack for https://$PublicHost"
$env:PUBLIC_HOST = $PublicHost
$env:PUBLIC_PORT = "$PublicPort"

$up = $compose + @('up', '-d', '--remove-orphans')
if (-not $NoBuild) { $up += '--build' }
Invoke-Native docker $up | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed — see the output above.' }

Write-Step "Waiting for the public edge on http://127.0.0.1:$PublicPort"
$deadline = (Get-Date).AddMinutes(5)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $health = Invoke-WebRequest "http://127.0.0.1:$PublicPort/healthz" -UseBasicParsing -TimeoutSec 5
        if ($health.StatusCode -eq 200) { $ready = $true; break }
    }
    catch { }
    Start-Sleep -Seconds 3
}
if (-not $ready) {
    Invoke-Native docker ($compose + @('ps')) | Write-Host
    throw "The edge did not answer within 5 minutes. Check: docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.public.yml logs nginx api keycloak"
}
Write-Host '    edge is up'

Write-Step "Allowing https://$PublicHost on the Keycloak client '$client'"
# Runs kcadm inside the Keycloak container, authenticating with the bootstrap admin the container
# already holds in its environment — so no password passes through this script or its output.
$origin = "https://$PublicHost"
$kc = @'
set -e
KC=/opt/keycloak/bin/kcadm.sh
$KC config credentials --server http://localhost:8080 --realm master --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null
$KC get clients -r __REALM__ -q clientId=__CLIENT__ --fields id,redirectUris,webOrigins,attributes
'@.Replace('__REALM__', $realm).Replace('__CLIENT__', $client)

$deadline = (Get-Date).AddMinutes(3)
$json = $null
while ((Get-Date) -lt $deadline) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $json = & docker @($compose + @('exec', '-T', 'keycloak', 'bash', '-c', (ConvertTo-BashCommand $kc))) 2>$null
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -eq 0 -and $json) { break }
    Start-Sleep -Seconds 5
}
if (-not $json) { throw "Could not read client '$client' from Keycloak. Is Keycloak healthy? (docker compose ... logs keycloak)" }

$current = ($json | Out-String | ConvertFrom-Json)[0]
$redirects = @($current.redirectUris) + "$origin/*" | Select-Object -Unique
$origins = @($current.webOrigins) + $origin | Select-Object -Unique
$logout = @(($current.attributes.'post.logout.redirect.uris' -split '##') + "$origin/*" | Where-Object { $_ } | Select-Object -Unique) -join '##'

$originsJson = '[' + (($origins | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']'
$redirectsJson = '[' + (($redirects | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']'

$update = @'
set -e
KC=/opt/keycloak/bin/kcadm.sh
$KC config credentials --server http://localhost:8080 --realm master --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null
$KC update clients/__ID__ -r __REALM__ -s 'redirectUris=__REDIRECTS__' -s 'webOrigins=__ORIGINS__' -s 'attributes."post.logout.redirect.uris"=__LOGOUT__'
'@.Replace('__ID__', $current.id).Replace('__REALM__', $realm).Replace('__REDIRECTS__', $redirectsJson).Replace('__ORIGINS__', $originsJson).Replace('__LOGOUT__', $logout)

Invoke-Native docker ($compose + @('exec', '-T', 'keycloak', 'bash', '-c', (ConvertTo-BashCommand $update))) | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Updating the Keycloak client failed — see above.' }
Write-Host "    redirect URIs: $($redirects -join ', ')"

Write-Step 'Checking the issuer the browser will see'
# curl.exe rather than Invoke-RestMethod: Windows PowerShell refuses a custom Host header, and the
# Host is exactly what Keycloak builds the issuer from.
$raw = Invoke-Native curl.exe @('-s', '--max-time', '15', '-H', "Host: $PublicHost", "http://127.0.0.1:$PublicPort/realms/$realm/.well-known/openid-configuration")
if ($LASTEXITCODE -ne 0 -or -not $raw) { throw 'Could not fetch the OIDC discovery document through the edge.' }
$discovery = ($raw | Out-String | ConvertFrom-Json)
$expected = "https://$PublicHost/realms/$realm"
if ($discovery.issuer -ne $expected) {
    throw "Keycloak advertises issuer '$($discovery.issuer)', expected '$expected'. Sign-in through the tunnel would fail. Check KC_PROXY_HEADERS on the keycloak service."
}
Write-Host "    issuer: $($discovery.issuer)"

Write-Step 'Done'
Write-Host "Local edge : http://127.0.0.1:$PublicPort   (what the tunnel forwards to)"
Write-Host "Public URL : https://$PublicHost   (after Add-CloudflareRoute.ps1 has been run once)"
