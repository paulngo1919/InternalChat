<#
.SYNOPSIS
    Gets a clean local machine ready for the "Full Stack (API + Worker + Web)" debug launch.

.DESCRIPTION
    Run by VS Code as the compound's preLaunchTask (.vscode/tasks.json, "fullstack: prepare").
    Safe to run by hand, and safe to run twice.

      1. Stops anything left over from a previous run — the API, the Worker, `dotnet run` sessions
         of either, and a Vite dev server on port 8080. Leftovers lock bin/ DLLs, which is what makes
         the next build fail with MSB3021 "file is being used by another process".
      2. Starts the infrastructure containers (deploy/docker-compose.infra.yml) and waits for
         PostgreSQL and RabbitMQ to accept connections.
      3. Builds the solution ONCE. Building the API and the Worker as two parallel tasks compiles the
         shared projects twice at the same time, and the two builds collide on the same files.
      4. Applies EF Core migrations to the local database (idempotent).
      5. Installs web dependencies if node_modules is missing.

    The Vite dev server itself is started by a separate background task, because it has to keep
    running after this script ends.

.PARAMETER SkipInfra
    Do not touch Docker (for when the infrastructure is managed some other way).

.PARAMETER SkipMigrations
    Do not apply migrations.
#>
[CmdletBinding()]
param(
    [switch] $SkipInfra,
    [switch] $SkipMigrations
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Stop-Leftovers {
    Write-Step 'Stopping leftover API / Worker / Vite processes'

    # Match on the command line rather than the process name alone: a debug session runs the API as
    # `dotnet InternalChat.Api.dll`, a terminal as `dotnet run --project src/InternalChat.Api`, and a
    # previous build may have left `InternalChat.Api.exe`. All three lock the same files.
    $patterns = @(
        'InternalChat\.Api(\.dll|\.exe|"|\s|$)',
        'InternalChat\.Worker(\.dll|\.exe|"|\s|$)',
        'src[\\/]+InternalChat\.(Api|Worker)'
    )

    $self = $PID
    $victims = @(Get-CimInstance Win32_Process | Where-Object {
        $p = $_
        $p.ProcessId -ne $self -and $p.CommandLine -and
        ($p.Name -eq 'InternalChat.Api.exe' -or $p.Name -eq 'InternalChat.Worker.exe' -or
            ($p.Name -eq 'dotnet.exe' -and $p.CommandLine -notmatch 'MSBuild\.dll' -and
                ($patterns | Where-Object { $p.CommandLine -match $_ })))
    })

    foreach ($process in $victims) {
        Write-Host ("    stopping {0} (pid {1})" -f $process.Name, $process.ProcessId)
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    # Vite is strictPort on 8080 (vite.config.ts), so a leftover dev server makes the new one fail.
    # Only node processes are stopped — port 8080 is also Keycloak's inside Docker, but Docker
    # publishes Keycloak on 8082, so a node process is the only thing expected here.
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        if ($owner -and $owner.ProcessName -eq 'node') {
            Write-Host ("    stopping Vite (node pid {0}) on port 8080" -f $owner.Id)
            Stop-Process -Id $owner.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($victims.Count -eq 0 -and $listeners.Count -eq 0) {
        Write-Host '    nothing to stop'
    }

    # Give Windows a moment to release file handles before the build copies over them.
    Start-Sleep -Milliseconds 500
}

function Wait-Until([scriptblock] $condition, [string] $what, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) {
            Write-Host "    $what is ready"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "$what did not become ready within $seconds s. Check: docker compose -f deploy/docker-compose.infra.yml ps"
}

function Test-Port([int] $port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        return $connect.AsyncWaitHandle.WaitOne(1000) -and $client.Connected
    }
    catch { return $false }
    finally { $client.Close() }
}

function Start-Infra {
    Write-Step 'Starting infrastructure containers'

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is not on PATH. Start Docker Desktop, or run with -SkipInfra.'
    }

    # The migrations service is left out: it builds an image, and step 4 migrates from source.
    # Deliberately no --wait: Keycloak's health check can report unhealthy while it serves fine,
    # and --wait would fail the whole launch over it. Readiness is checked per dependency below.
    docker compose -f deploy/docker-compose.infra.yml up -d postgres redis rabbitmq keycloak minio minio-init clamav
    if ($LASTEXITCODE -ne 0) {
        throw 'docker compose up failed. Is Docker Desktop running, and does deploy/.env exist (copy deploy/.env.example)?'
    }

    Wait-Until { (docker inspect -f '{{.State.Health.Status}}' internalchat-postgres-1 2>$null) -eq 'healthy' } 'PostgreSQL' 90
    Wait-Until { Test-Port 5672 } 'RabbitMQ' 90
    Wait-Until { Test-Port 6379 } 'Redis' 60
}

function Invoke-Build {
    Write-Step 'Building the solution'

    dotnet build InternalChat.slnx /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
    if ($LASTEXITCODE -ne 0) {
        throw 'Build failed — see the errors above (they are also in the Problems panel).'
    }
}

function Invoke-Migrations {
    Write-Step 'Applying database migrations'

    if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) {
        Write-Host '    installing dotnet-ef'
        dotnet tool install --global dotnet-ef
    }

    # Uses ChatDbContextFactory: INTERNALCHAT_DB_CONNECTION if set, otherwise the local development
    # database the API's appsettings.Development.json points at.
    dotnet ef database update --project src/InternalChat.Infrastructure --no-build
    if ($LASTEXITCODE -ne 0) {
        throw 'Migrations failed. Is PostgreSQL up, and does its password match appsettings.Development.json?'
    }
}

function Install-WebDependencies {
    if (-not (Test-Path 'src/internalchat-web/node_modules')) {
        Write-Step 'Installing web dependencies'
        npm --prefix src/internalchat-web ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }
}

Stop-Leftovers
if (-not $SkipInfra) { Start-Infra }
Invoke-Build
if (-not $SkipMigrations) { Invoke-Migrations }
Install-WebDependencies

Write-Step 'Ready — starting API, Worker, and Web'
