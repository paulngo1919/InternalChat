<#
.SYNOPSIS
    Publishes the chat edge through the machine's existing Cloudflare Tunnel — the same tunnel that
    serves pr.benda.io.vn (E:\ReviewCode\docs\08-cloudflare-tunnel-setup.md). Run ONCE, elevated.

.DESCRIPTION
    1. Adds `- hostname: <PublicHost> / service: http://localhost:<Port>` to the tunnel's config.yml,
       above the http_status:404 catch-all (backing the file up first). Skipped if already present.
    2. Routes DNS for the hostname to the tunnel (`cloudflared tunnel route dns`), which creates the
       CNAME in Cloudflare. Skipped by Cloudflare if the record already points at this tunnel.
    3. Restarts the `cloudflared` Windows service so the new ingress rule takes effect.

    Step 3 briefly interrupts EVERY hostname on this tunnel (pr., demo., acm. ...) — a few seconds
    while the service reconnects.

.PARAMETER Tunnel
    Tunnel name. Defaults to the one the cloudflared service runs.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $PublicHost = 'chat.benda.io.vn',
    [int] $Port = 8090,
    [string] $Tunnel = 'pr-review',
    [string] $ConfigPath = (Join-Path $env:USERPROFILE '.cloudflared\config.yml')
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: restarting the cloudflared service needs administrator rights.'
}

$cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cloudflared) { $cloudflared = 'C:\Program Files (x86)\cloudflared\cloudflared.exe' }
if (-not (Test-Path $cloudflared)) { throw "cloudflared not found at $cloudflared." }
if (-not (Test-Path $ConfigPath)) { throw "Tunnel config not found at $ConfigPath." }

# --- 1. Ingress rule ---------------------------------------------------------------------------
$lines = [System.Collections.Generic.List[string]](Get-Content $ConfigPath)

if ($lines | Where-Object { $_ -match "^\s*-\s*hostname:\s*$([regex]::Escape($PublicHost))\s*$" }) {
    Write-Host "Ingress for $PublicHost already present in $ConfigPath"
}
else {
    $catchAll = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*-\s*service:\s*http_status:404') { $catchAll = $i; break }
    }
    if ($catchAll -lt 0) { throw "No 'service: http_status:404' catch-all found in $ConfigPath; add the rule by hand." }

    $indent = ([regex]::Match($lines[$catchAll], '^\s*')).Value
    $backup = "$ConfigPath.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"

    if ($PSCmdlet.ShouldProcess($ConfigPath, "add $PublicHost -> http://localhost:$Port")) {
        Copy-Item $ConfigPath $backup
        $lines.Insert($catchAll, "$indent  service: http://localhost:$Port")
        $lines.Insert($catchAll, "$indent- hostname: $PublicHost")
        Set-Content -Path $ConfigPath -Value $lines -Encoding ASCII
        Write-Host "Added $PublicHost -> http://localhost:$Port (backup: $backup)"
    }
}

& $cloudflared tunnel --config $ConfigPath ingress validate
if ($LASTEXITCODE -ne 0) { throw 'cloudflared rejected the ingress config. Restore the .bak file next to it.' }

# --- 2. DNS -------------------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($PublicHost, "route DNS to tunnel $Tunnel")) {
    & $cloudflared tunnel route dns $Tunnel $PublicHost
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "DNS route failed. If a record for $PublicHost already exists and points elsewhere, remove it in the Cloudflare dashboard, then re-run."
    }
}

# --- 3. Restart ---------------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess('cloudflared service', 'restart')) {
    Restart-Service cloudflared
    Start-Sleep -Seconds 5
    Get-Service cloudflared | Format-Table Name, Status -AutoSize
}

Write-Host ''
Write-Host "Done. https://$PublicHost now forwards to http://localhost:$Port." -ForegroundColor Green
Write-Host 'Start the stack with deploy/scripts/Start-Public.ps1 if it is not running.'
