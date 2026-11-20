[CmdletBinding()]
param(
    [string]$IamDirectory,
    [switch]$SkipBuild,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $IamDirectory) { $IamDirectory = Join-Path (Split-Path -Parent $repository) 'Aegis-IAM' }
$localDirectory = Join-Path $repository '.local'
New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
& (Join-Path $PSScriptRoot 'Protect-WorkspaceLocalDirectory.ps1')
$environmentPath = Join-Path $IamDirectory '.env.test'
if (-not (Test-Path -LiteralPath $environmentPath)) { throw 'Aegis-IAM/.env.test was not found.' }
$iamEnvironment = @{}
foreach ($line in [IO.File]::ReadAllLines($environmentPath)) {
    if ($line -match '^([A-Z][A-Z0-9_]*)=(.*)$') { $iamEnvironment[$matches[1]] = $matches[2].Trim().Trim('"', "'") }
}
$containers = & docker ps --filter 'name=aegis-iam-test-postgres-1' --format '{{.Names}}'
if ($LASTEXITCODE -ne 0 -or $containers -notcontains 'aegis-iam-test-postgres-1') { throw 'Start the Aegis-IAM test Docker environment first.' }
$statePath = Join-Path $localDirectory 'database.local.json'
if (Test-Path -LiteralPath $statePath) { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
else {
    $state = [pscustomobject]@{ ownerPassword = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')); appPassword = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')) }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
function SqlLiteral([string]$value) { return "'" + $value.Replace("'", "''") + "'" }
$sql = @'
DO $setup$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='termlume_owner') THEN
    CREATE ROLE termlume_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='termlume_app') THEN
    CREATE ROLE termlume_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
  END IF;
  IF EXISTS (SELECT FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba WHERE d.datname='termlume' AND r.rolname <> 'termlume_owner') THEN
    RAISE EXCEPTION 'An existing termlume database has a different owner';
  END IF;
END $setup$;
ALTER ROLE termlume_owner NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD __OWNER_PASSWORD__;
ALTER ROLE termlume_app NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD __APP_PASSWORD__;
SELECT 'CREATE DATABASE termlume OWNER termlume_owner' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='termlume') \gexec
REVOKE ALL ON DATABASE termlume FROM PUBLIC;
GRANT CONNECT ON DATABASE termlume TO termlume_app;
'@
$sql = $sql.Replace('__OWNER_PASSWORD__', (SqlLiteral $state.ownerPassword)).Replace('__APP_PASSWORD__', (SqlLiteral $state.appPassword))
# 密码通过 stdin 进入 psql，不放进命令行或控制台输出。
$sql | & docker exec -i aegis-iam-test-postgres-1 sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Workspace database initialization failed.' }
$port = if ($iamEnvironment.POSTGRES_PORT) { [int]$iamEnvironment.POSTGRES_PORT } else { 54330 }
$issuer = if ($iamEnvironment.OIDC_ISSUER) { $iamEnvironment.OIDC_ISSUER } else { 'http://localhost:18080' }
$applicationConnection = "Host=127.0.0.1;Port=$port;Database=termlume;Username=termlume_app;Password=$($state.appPassword);SSL Mode=Disable;Timeout=10;Command Timeout=20;Maximum Pool Size=20;Include Error Detail=false"
$ownerConnection = "Host=127.0.0.1;Port=$port;Database=termlume;Username=termlume_owner;Password=$($state.ownerPassword);SSL Mode=Disable;Timeout=10;Include Error Detail=false"
$settingsPath = Join-Path $localDirectory 'api-settings.local.json'
$migrationPath = Join-Path $localDirectory 'migration-settings.local.json'
$settings = @{ Urls = 'http://127.0.0.1:18741'; Aegis = @{ Issuer = $issuer; ClientId = 'termlume-desktop' }; ConnectionStrings = @{ Workspace = $applicationConnection } }
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($migrationPath, (@{ ConnectionStrings = @{ Workspace = $ownerConnection } } | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
$publishDirectory = Join-Path $localDirectory 'api'
$executable = Join-Path $publishDirectory 'TermLume.Api.exe'
$pidPath = Join-Path $localDirectory 'api.pid'
if (Test-Path -LiteralPath $pidPath) {
    $previous = Get-Process -Id ([int](Get-Content -LiteralPath $pidPath -Raw)) -ErrorAction SilentlyContinue
    if ($previous -and $previous.Path -eq $executable) { Stop-Process -Id $previous.Id; $previous.WaitForExit(10000) | Out-Null }
}
if (-not $SkipBuild) {
    & dotnet publish (Join-Path $repository 'TermLume.Api/TermLume.Api.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Workspace API publish failed.' }
}
if (-not (Test-Path -LiteralPath $executable)) { throw 'The workspace API executable is missing.' }
& $executable --migrate --settings $migrationPath
if ($LASTEXITCODE -ne 0) { throw 'Workspace database migrations failed.' }
if (-not $PrepareOnly) {
    $process = Start-Process -FilePath $executable -ArgumentList @('--settings', ('"' + $settingsPath + '"')) -WorkingDirectory $publishDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $localDirectory 'api.stdout.log') -RedirectStandardError (Join-Path $localDirectory 'api.stderr.log')
    [IO.File]::WriteAllText($pidPath, [string]$process.Id)
    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Workspace API stopped during startup; inspect .local/api.stderr.log.' }
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18741/healthz' -TimeoutSec 2
            if ($health.status -eq 'ok') { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $healthy) { Stop-Process -Id $process.Id; throw 'Workspace API health check failed.' }
    Write-Output "Workspace API process: $($process.Id); address: http://127.0.0.1:18741"
}
Write-Output "Workspace database: 127.0.0.1:$port/termlume; runtime role: termlume_app"
