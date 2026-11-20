<#
.SYNOPSIS
为 Windows 本地联调环境构建并启动 ABP 工作区 API 容器。
.DESCRIPTION
复用上一级 Aegis-IAM 的 Docker PostgreSQL；先备份已有 termlume 业务库，
再准备受限配置、构建镜像并检查迁移，最后等待常驻服务健康。
.NOTES
需要 Docker Desktop、.NET 10 SDK 和正在运行的 Aegis-IAM 测试容器。
凭据保存在被 Git 忽略的 .local 目录中，仅 API 地址和公共客户端配置可随源码分发。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $repository 'docker/compose.workspace.yml'
Push-Location $repository
try {
    # Reuse the single termlume database and keep an upgrade backup outside the image.
    $existing = 'SELECT 1 FROM pg_database WHERE datname=''termlume'';' | & docker exec -i aegis-iam-test-postgres-1 sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -X -qAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres'
    if ($LASTEXITCODE -ne 0) { throw 'Start the Aegis-IAM test PostgreSQL container first.' }
    if (($existing -join '').Trim() -eq '1') {
        & (Join-Path $PSScriptRoot 'Backup-WorkspaceDatabase.ps1')
    }
    & (Join-Path $PSScriptRoot 'Start-WorkspaceApi.ps1') -PrepareOnly
    & (Join-Path $PSScriptRoot 'Prepare-WorkspaceDocker.ps1')
    # Refresh base-image security fixes even when a version tag is cached locally.
    & docker compose -f $compose build --pull workspace-api
    if ($LASTEXITCODE -ne 0) { throw 'Workspace Docker image build failed.' }
    & docker compose -f $compose --profile migration run --rm migrate
    if ($LASTEXITCODE -ne 0) { throw 'Workspace Docker migration verification failed.' }

    & (Join-Path $PSScriptRoot 'Stop-WorkspaceApi.ps1')
    & docker compose -f $compose up -d --wait --wait-timeout 60 workspace-api
    if ($LASTEXITCODE -ne 0) { throw 'Workspace Docker service did not become healthy.' }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18741/healthz' -TimeoutSec 5
    if ($health.status -ne 'ok' -or $health.framework -ne 'ABP') { throw 'Unexpected workspace service health response.' }
    Write-Output ('Workspace API deployed: ABP; version=' + $health.version + '; address=http://127.0.0.1:18741')
} finally {
    Pop-Location
}
