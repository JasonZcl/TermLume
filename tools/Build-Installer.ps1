[CmdletBinding()]
param(
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repository 'TermLume\TermLume.csproj'
$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Expected a three-part app version, got: $version"
}

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Install Inno Setup 6.5 or newer, or specify -IsccPath.'
}

$artifacts = Join-Path $repository 'artifacts'
$publishDirectory = Join-Path $artifacts "publish-setup-v$version"
$prerequisites = Join-Path $artifacts 'installer-prerequisites'
$bootstrapper = Join-Path $prerequisites 'MicrosoftEdgeWebview2Setup.exe'
New-Item -ItemType Directory -Path $artifacts, $prerequisites -Force | Out-Null

if (-not (Test-Path -LiteralPath $bootstrapper -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
    throw "The WebView2 bootstrapper must have a valid Microsoft Authenticode signature. Status: $($signature.Status)"
}

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination (Join-Path $publishDirectory 'README.md')

$unexpectedData = Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force | Where-Object {
    $_.Name -in @('workspace.json', 'activity-log.json', 'WebView2')
}
if ($unexpectedData) { throw 'User data must never be included in the installer payload.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'coreclr.dll'))) {
    throw 'The publish output is not self-contained.'
}

& $IsccPath '/Qp' "/DAppVersion=$version" "/DPublishDir=$publishDirectory" "/DWebViewBootstrapper=$bootstrapper" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'installer\TermLume.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $artifacts "TermLume-$version-win-x64-Setup.exe"
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$installer.sha256", "$checksum  $([System.IO.Path]::GetFileName($installer))`n", [System.Text.Encoding]::ASCII)
Write-Host "Installer: $installer"
Write-Host "SHA256: $checksum"
