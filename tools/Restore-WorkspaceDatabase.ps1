[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BackupFile,
    [ValidatePattern('^termlume_restore_[a-z0-9_]+$')][string]$DatabaseName = ('termlume_restore_' + (Get-Date -Format 'yyyyMMdd_hhmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6)),
    [ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$Container = 'aegis-iam-test-postgres-1',
    [string]$ApiExecutable
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Protect-WorkspaceLocalDirectory.ps1')
$BackupFile = [IO.Path]::GetFullPath($BackupFile)
$manifest = Get-Content -LiteralPath ($BackupFile + '.json') -Raw | ConvertFrom-Json
if ($manifest.application -ne 'TermLume' -or $manifest.formatVersion -ne 1 -or $manifest.sha256 -ne (Get-FileHash -LiteralPath $BackupFile -Algorithm SHA256).Hash) { throw 'The backup manifest or SHA256 is invalid.' }
if (-not $ApiExecutable) { $ApiExecutable = Join-Path $repository '.local/api/TermLume.Api.exe' }
$ApiExecutable = [IO.Path]::GetFullPath($ApiExecutable)
if (-not (Test-Path -LiteralPath $ApiExecutable) -or [version](Get-Item -LiteralPath $ApiExecutable).VersionInfo.FileVersion -lt [version]'0.8.0') { throw 'Publish the 0.8.0 API before restoring a backup.' }
foreach ($migration in $manifest.migrations) {
    if ($migration.version -notmatch '^TermLume[.]Api[.]Migrations[.][0-9]{3}_[a-z0-9_]+[.]sql$') { throw 'Invalid migration name in backup manifest.' }
    $file = Join-Path $repository ('TermLume.Api/Migrations/' + ([string]$migration.version).Replace('TermLume.Api.Migrations.', ''))
    if (-not (Test-Path -LiteralPath $file)) { throw 'The backup schema is newer than this application.' }
    $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($file).Replace("`r`n", "`n"))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $checksum = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '') } finally { $algorithm.Dispose() }
    if ($checksum -ne $migration.checksum) { throw 'The backup migration checksum does not match this application.' }
}
$temporary = '/tmp/termlume-restore-' + [guid]::NewGuid().ToString('N') + '.dump'
$localDirectory = Join-Path $repository '.local/restores'
New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
$settingsPath = Join-Path $localDirectory ($DatabaseName + '.local.json')
$ownerPath = Join-Path $localDirectory ($DatabaseName + '-owner.local.json')
$runtime = Get-Content -LiteralPath (Join-Path $repository '.local/api-settings.local.json') -Raw | ConvertFrom-Json
$owner = Get-Content -LiteralPath (Join-Path $repository '.local/migration-settings.local.json') -Raw | ConvertFrom-Json
$runtime.ConnectionStrings.Workspace = $runtime.ConnectionStrings.Workspace -replace 'Database=[^;]+;',("Database=$DatabaseName;")
$owner.ConnectionStrings.Workspace = $owner.ConnectionStrings.Workspace -replace 'Database=[^;]+;',("Database=$DatabaseName;")
# 先验证目标不存在，再创建新库；这个工具从不覆盖当前业务库或 IAM 数据库。
$create = @"
DO `$$ BEGIN IF EXISTS(SELECT FROM pg_database WHERE datname='$DatabaseName') THEN RAISE EXCEPTION 'Restore database already exists'; END IF; END `$$;
CREATE DATABASE $DatabaseName OWNER termlume_owner;
REVOKE ALL ON DATABASE $DatabaseName FROM PUBLIC;
GRANT CONNECT ON DATABASE $DatabaseName TO termlume_app;
"@
$create | & docker exec -i $Container sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -X -qAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres'
if ($LASTEXITCODE -ne 0) { throw 'Restore requires a new, unused database name.' }
try {
    & docker cp $BackupFile ($Container + ':' + $temporary)
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the backup into PostgreSQL.' }
    $restoreCommand = 'set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
pg_restore --exit-on-error --no-owner --role=termlume_owner -U "$POSTGRES_USER" -d __DATABASE__ __FILE__'
    $restoreCommand.Replace('__DATABASE__', $DatabaseName).Replace('__FILE__', $temporary) | & docker exec -i $Container sh -c "tr -d '\r' | sh"
    if ($LASTEXITCODE -ne 0) { throw "Restore failed; inspect the isolated database $DatabaseName before using it." }
    [IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    & $ApiExecutable --migrate --settings $ownerPath
    if ($LASTEXITCODE -ne 0) { throw 'Restored database migration verification failed.' }
    # 恢复后默认使用其他端口，只写出配置，切换正式连接由运维人员显式完成。
    $runtime.Urls = 'http://127.0.0.1:18745'
    [IO.File]::WriteAllText($settingsPath, ($runtime | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "Restored and migrated isolated database: $DatabaseName"
    Write-Output "Runtime settings: $settingsPath"
} finally { & docker exec $Container rm -f -- $temporary | Out-Null }
