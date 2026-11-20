[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidatePattern('^termlume(?:_[a-z0-9_]+)?$')][string]$DatabaseName = 'termlume',
    [ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$Container = 'aegis-iam-test-postgres-1'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Protect-WorkspaceLocalDirectory.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository '.local/backups' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$name = 'TermLume-' + $DatabaseName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.dump'
$target = Join-Path $OutputDirectory $name
$temporary = '/tmp/termlume-backup-' + [guid]::NewGuid().ToString('N') + '.dump'
try {
    # 在容器内生成二进制文件，避免 Windows PowerShell 5 的管道重编码破坏备份。
    $dumpCommand = 'set -eu
umask 077
export PGPASSWORD="$POSTGRES_PASSWORD"
pg_dump -Fc --no-password -U "$POSTGRES_USER" -d __DATABASE__ -f __FILE__'
    $dumpCommand.Replace('__DATABASE__', $DatabaseName).Replace('__FILE__', $temporary) | & docker exec -i $Container sh -c "tr -d '\r' | sh"
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL backup failed.' }
    & docker cp ($Container + ':' + $temporary) $target
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the PostgreSQL backup.' }
    $query = "SELECT coalesce(json_agg(m ORDER BY version),'[]'::json) FROM (SELECT version,checksum FROM public.termlume_migrations) m;"
    $migrations = $query | & docker exec -i $Container sh -c ('PGPASSWORD="$POSTGRES_PASSWORD" psql -X -qAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d ' + $DatabaseName)
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the backup migration manifest.' }
    $records = @()
    foreach ($record in (($migrations -join "`n") | ConvertFrom-Json)) { $records += @{version=[string]$record.version; checksum=[string]$record.checksum} }
    $manifest = @{ application='TermLume'; formatVersion=1; database=$DatabaseName; createdAt=[DateTimeOffset]::UtcNow.ToString('o'); sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; migrations=$records }
    [IO.File]::WriteAllText(($target + '.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "Backup: $target"
    Write-Output "SHA256: $($manifest.sha256)"
} finally {
    & docker exec $Container rm -f -- $temporary | Out-Null
}
