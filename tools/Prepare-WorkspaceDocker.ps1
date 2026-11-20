[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Protect-WorkspaceLocalDirectory.ps1')
$directory = Join-Path (Split-Path -Parent $PSScriptRoot) '.local'
foreach ($name in @('api','migration')) {
    $source = Join-Path $directory ($name + '-settings.local.json')
    if (-not (Test-Path -LiteralPath $source)) { throw 'Run Start-WorkspaceApi.ps1 -PrepareOnly first.' }
    $settings = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    $settings.ConnectionStrings.Workspace = $settings.ConnectionStrings.Workspace -replace 'Host=127\.0\.0\.1;', 'Host=host.docker.internal;'
    # The local test database uses passwords, without Kerberos/GSSAPI in the runtime image.
    if ($settings.ConnectionStrings.Workspace -notmatch '(?:^|;)\s*GSS\s*Encryption\s*Mode\s*=') {
        $settings.ConnectionStrings.Workspace = $settings.ConnectionStrings.Workspace.TrimEnd(';') + ';GSS Encryption Mode=Disable;'
    }
    if ($name -eq 'api') {
        $settings.Urls = 'http://0.0.0.0:8080'
        $issuer = [uri]$settings.Aegis.Issuer
        if ($issuer.Scheme -eq 'http' -and $issuer.IsLoopback) {
            $channel = [UriBuilder]::new($issuer)
            $channel.Host = 'host.docker.internal'
            $settings.Aegis | Add-Member NoteProperty BackchannelUrl ($channel.Uri.AbsoluteUri.TrimEnd('/')) -Force
            $settings.Aegis | Add-Member NoteProperty AllowLocalDockerBackchannel $true -Force
        }
    }
    [IO.File]::WriteAllText((Join-Path $directory ($name + '-docker.local.json')), ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
Write-Output 'Docker settings prepared under .local; database credentials remain outside the image.'
