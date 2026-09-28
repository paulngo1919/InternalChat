<#
.SYNOPSIS
    Makes the PostgreSQL application role match deploy/.env (POSTGRES_APP_USER / POSTGRES_APP_PASSWORD).

.DESCRIPTION
    The Compose stack's migrations, API, and Worker log in as POSTGRES_APP_USER. The init scripts do
    not create that role, so on a database that was set up some other way it may exist with a
    different password — and every one of those services then fails with 28P01
    "password authentication failed".

    This creates the role if it is missing, sets its password to the value in deploy/.env, and grants
    it what the application needs on the application database. The password is read from deploy/.env
    on this machine and handed to psql through an environment variable, so it never appears on a
    command line, in `docker inspect`, or in this script's output.

    Idempotent. Does not touch any other role, including the one local `dotnet run` uses.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envFile = Join-Path $root 'deploy\.env'

if (-not (Test-Path $envFile)) { throw "deploy/.env not found at $envFile." }

$values = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
        $values[$Matches[1]] = $Matches[2].Trim().Trim('"').Trim("'")
    }
}

$user = $values['POSTGRES_APP_USER']
$password = $values['POSTGRES_APP_PASSWORD']
$database = $values['POSTGRES_DB']

if (-not $user -or -not $password -or -not $database) {
    throw 'deploy/.env must define POSTGRES_APP_USER, POSTGRES_APP_PASSWORD, and POSTGRES_DB.'
}
if ($user -notmatch '^[a-z_][a-z0-9_]*$') { throw "POSTGRES_APP_USER '$user' is not a plain lower-case identifier." }

# psql variables (:'pw', :"role") quote the values safely; nothing is spliced into SQL text here.
$sql = @'
SELECT format('CREATE ROLE %I LOGIN', :'role') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
ALTER ROLE :"role" WITH LOGIN PASSWORD :'pw';
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO :"role";
SELECT format('GRANT USAGE, CREATE ON SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'keycloak') \gexec
SELECT format('GRANT ALL ON ALL TABLES IN SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'keycloak') \gexec
SELECT format('GRANT ALL ON ALL SEQUENCES IN SCHEMA %I TO %I', nspname, :'role')
  FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'keycloak') \gexec
'@

$env:INTERNALCHAT_APP_PW = $password
try {
    $sql | docker exec -i -e INTERNALCHAT_APP_PW internalchat-postgres-1 sh -c `
        "psql -v ON_ERROR_STOP=1 -q -U `"`$POSTGRES_USER`" -d '$database' -v role='$user' -v db='$database' -v pw=`"`$INTERNALCHAT_APP_PW`""
    if ($LASTEXITCODE -ne 0) { throw 'psql failed — see above.' }
}
finally {
    Remove-Item Env:\INTERNALCHAT_APP_PW -ErrorAction SilentlyContinue
}

Write-Host "Role '$user' now matches deploy/.env." -ForegroundColor Green
