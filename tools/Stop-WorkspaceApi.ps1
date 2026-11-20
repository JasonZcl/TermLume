[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$directory = Join-Path (Split-Path -Parent $PSScriptRoot) '.local'
$path = Join-Path $directory 'api.pid'
if (Test-Path -LiteralPath $path) {
    $process = Get-Process -Id ([int](Get-Content -LiteralPath $path -Raw)) -ErrorAction SilentlyContinue
    if ($process -and $process.Path -eq (Join-Path $directory 'api/TermLume.Api.exe')) {
        Stop-Process -Id $process.Id
        $process.WaitForExit(10000) | Out-Null
        Write-Output 'Workspace API stopped.'
    }
}
