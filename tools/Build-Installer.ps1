[CmdletBinding()]
param(
    [string]$IsccPath,
    [string]$BuildDirectory
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
$runtimeVersion = [version]$project.Project.PropertyGroup.RuntimeFrameworkVersion
if ($runtimeVersion -lt [version]'10.0.14') { throw 'The self-contained runtime must meet the .NET 10.0.14 security baseline.' }

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

# 发布输出只包含程序文件；真实用户配置始终位于独立的 LocalAppData 目录。
$artifacts = Join-Path $repository 'artifacts'
if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLume-installer-' + [guid]::NewGuid().ToString('N'))
}
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if ($BuildDirectory.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase) -or $BuildDirectory -eq $artifacts) {
    throw 'Keep build intermediates outside artifacts; this folder is reserved for installers.'
}
$publishDirectory = Join-Path $BuildDirectory 'publish'
if (Test-Path -LiteralPath $publishDirectory) { throw 'Use a fresh BuildDirectory to avoid packaging stale program files.' }
$prerequisites = Join-Path $BuildDirectory 'installer-prerequisites'
$bootstrapper = Join-Path $prerequisites 'MicrosoftEdgeWebview2Setup.exe'
New-Item -ItemType Directory -Path $artifacts, $prerequisites -Force | Out-Null

if (-not (Test-Path -LiteralPath $bootstrapper -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}
# 打包前验证官方引导程序签名，拒绝把来源或完整性无法确认的运行时程序装进安装包。
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
    throw "The WebView2 bootstrapper must have a valid Microsoft Authenticode signature. Status: $($signature.Status)"
}

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$readme = [IO.File]::ReadAllText((Join-Path $repository 'README.md'))
$readme = [regex]::Replace($readme, '(?ms)^### 校验安装包\r?\n.*?(?=^## 当前功能)', "安装包校验值请查看仓库当前版本 README；不在安装包内嵌入自身校验值。`n`n")
$readme = [regex]::Replace($readme, '(?m)^\| 安装包大小 \|.*$', '| 安装包大小 | 以下载的安装包文件属性为准 |')
[IO.File]::WriteAllText((Join-Path $publishDirectory 'README.md'), $readme, [Text.UTF8Encoding]::new($false))
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'TermLume.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq $frameworkName)
    if ($framework.Count -ne 1 -or [version]$framework[0].version -ne $runtimeVersion) {
        throw "Published $frameworkName does not match the pinned security runtime $runtimeVersion."
    }
}
@{ version = $version; runtimeVersion = $runtimeVersion.ToString(); publish = $publishDirectory; bootstrapper = $bootstrapper } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BuildDirectory 'build-info.json')

# 防止误把已有工作空间、日志或浏览器用户目录当作发布文件分发。
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
Write-Host "Build directory: $BuildDirectory"
Write-Host "Installer: $installer"
Write-Host "SHA256: $checksum"
