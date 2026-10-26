[CmdletBinding()]
param(
    [string]$IsccPath = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $repository 'TermLume\TermLume.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$nextVersion = '{0}.{1}.{2}' -f ([version]$version).Major, ([version]$version).Minor, (([version]$version).Build + 1)
# 每次使用仓库内的唯一测试目录及独立安装标识，不安装或卸载真实用户版本。
$qaDirectory = [System.IO.Path]::GetFullPath((Join-Path $repository 'artifacts\qa'))
$root = Join-Path $qaDirectory ("installer-" + [guid]::NewGuid().ToString('N'))
if (-not $root.StartsWith($qaDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer tests must be confined to artifacts/qa.'
}
$appId = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$appDirectory = Join-Path $root 'App'
$dataDirectory = Join-Path $root 'UserData'
$externalDirectory = Join-Path $root 'ExternalFiles'
$payload = Join-Path $root 'Payload'
$bootstrapper = Join-Path $repository 'artifacts\installer-prerequisites\MicrosoftEdgeWebview2Setup.exe'
$publishedExecutable = Join-Path $repository "artifacts\publish-setup-v$version\TermLume.exe"
if (-not (Test-Path -LiteralPath $publishedExecutable) -or -not (Test-Path -LiteralPath $bootstrapper)) {
    throw 'Run tools/Build-Installer.ps1 first.'
}
New-Item -ItemType Directory -Path $root, $payload, $dataDirectory, $externalDirectory -Force | Out-Null
Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $payload 'TermLume.exe')
[System.IO.File]::WriteAllText((Join-Path $payload 'payload-marker.txt'), 'Installer-owned program file')
[System.IO.File]::WriteAllText((Join-Path $externalDirectory 'private-key'), 'Synthetic key outside app data')
[System.IO.File]::WriteAllText((Join-Path $externalDirectory 'download.txt'), 'Synthetic download outside app data')
$results = [System.Collections.Generic.List[string]]::new()

function Assert-Condition([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAILED: $Description (QA root: $root)" }
    $results.Add($Description)
    Write-Host "PASS: $Description"
}

function Write-SyntheticData {
    New-Item -ItemType Directory -Path (Join-Path $dataDirectory 'WebView2\Cache') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dataDirectory 'workspace.json'), '{"marker":"Do not overwrite","preferences":{"language":"en"}}')
    [System.IO.File]::WriteAllText((Join-Path $dataDirectory 'activity-log.json'), '[{"marker":"Preserved log"}]')
    [System.IO.File]::WriteAllText((Join-Path $dataDirectory 'WebView2\Cache\marker'), 'Preserved browser cache')
}

function Assert-SyntheticData {
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $dataDirectory 'workspace.json') -Raw) -ceq '{"marker":"Do not overwrite","preferences":{"language":"en"}}') 'Workspace is byte-for-byte unchanged'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $dataDirectory 'activity-log.json') -Raw) -ceq '[{"marker":"Preserved log"}]') 'Activity log is byte-for-byte unchanged'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $dataDirectory 'WebView2\Cache\marker') -Raw) -ceq 'Preserved browser cache') 'WebView2 cache is preserved'
}

function Build-QaInstaller([string]$QaVersion) {
    & $IsccPath '/Q' "/DAppVersion=$QaVersion" "/DPublishDir=$payload" "/DWebViewBootstrapper=$bootstrapper" "/DOutputDir=$root" '/DQaMode=1' "/DQaRoot=$root" "/DQaAppId=$appId" (Join-Path $PSScriptRoot 'installer\TermLume.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installer compilation failed.' }
    return Join-Path $root "TermLume-QA-$QaVersion.exe"
}

# 执行前限定 EXE 必须来自本次测试根目录，避免误启动正式安装器或卸载器。
function Invoke-QaProcess([string]$Executable, [string]$Arguments, [bool]$ExpectSuccess = $true) {
    $resolved = (Resolve-Path -LiteralPath $Executable).Path
    if (-not $resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to execute an installer outside the isolated QA directory.'
    }
    $process = Start-Process -FilePath $resolved -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    if ($ExpectSuccess -and $process.ExitCode -ne 0) {
        throw "QA installer failed with exit code $($process.ExitCode). Root: $root"
    }
    if (-not $ExpectSuccess -and $process.ExitCode -eq 0) {
        throw "QA installer unexpectedly succeeded. Root: $root"
    }
}

function Install-Qa([string]$Setup, [string]$Name, [bool]$ExpectSuccess = $true, [string]$Target = $appDirectory) {
    Invoke-QaProcess $Setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /LANG=english /DIR=`"$Target`" /LOG=`"$root\$Name.log`"" $ExpectSuccess
}

function Uninstall-Qa([string]$Name, [bool]$DeleteData = $false, [bool]$ExpectSuccess = $true) {
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$root\$Name.log`""
    if ($DeleteData) { $arguments += ' /DELETEUSERDATA' }
    Invoke-QaProcess (Join-Path $appDirectory 'unins000.exe') $arguments $ExpectSuccess
}

$setup = Build-QaInstaller $version
$upgrade = Build-QaInstaller $nextVersion
Write-SyntheticData
Install-Qa $setup 'first-install'
Assert-Condition (Test-Path -LiteralPath (Join-Path $appDirectory 'TermLume.exe')) 'Application files install into the isolated folder'
Assert-Condition (Test-Path -LiteralPath (Join-Path $appDirectory 'unins000.exe')) 'Uninstaller is present in the installed software folder'
$registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
Assert-Condition (Test-Path -LiteralPath $registryPath) 'Only the unique QA product is registered for this user'
Assert-SyntheticData
Install-Qa $setup 'reinstall'
Assert-SyntheticData
Install-Qa $upgrade 'upgrade'
Assert-SyntheticData
Assert-Condition ((Get-ItemProperty -LiteralPath $registryPath).DisplayVersion -eq $nextVersion) 'Upgrade reuses the product identity and updates the version'
Install-Qa $setup 'downgrade-blocked' $false
Assert-Condition ((Get-Content -LiteralPath (Join-Path $root 'downgrade-blocked.log') -Raw) -match 'A newer version') 'Older installer is rejected without overwriting data'
Install-Qa $upgrade 'data-path-blocked' $false $dataDirectory
Assert-Condition ((Get-Content -LiteralPath (Join-Path $root 'data-path-blocked.log') -Raw) -match 'Choose a separate program folder') 'Installing over the data directory is rejected'
$appLink = Join-Path $root 'LinkedInstallFolder'
New-Item -ItemType Junction -Path $appLink -Value $externalDirectory | Out-Null
Install-Qa $upgrade 'linked-path-blocked' $false $appLink
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $externalDirectory 'TermLume.exe'))) 'Program installation does not follow directory junctions'

$createdMutex = $false
$mutex = [System.Threading.Mutex]::new($true, 'Local\TermLume.InstallerQA', [ref]$createdMutex)
try {
    if (-not $createdMutex) { throw 'Another isolated installer test is running.' }
    Install-Qa $upgrade 'running-install-blocked' $false
    Uninstall-Qa 'running-uninstall-blocked' $false $false
    Assert-Condition (Test-Path -LiteralPath (Join-Path $appDirectory 'TermLume.exe')) 'A running instance blocks installation and uninstallation without forced termination'
}
finally {
    if ($createdMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

[System.IO.File]::WriteAllText((Join-Path $appDirectory 'user-added.txt'), 'Not owned by the installer')
Uninstall-Qa 'uninstall-keep'
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'TermLume.exe'))) 'Retain-data uninstall removes installed application files'
Assert-Condition (Test-Path -LiteralPath (Join-Path $appDirectory 'user-added.txt')) 'Uninstall does not recursively delete unknown program-folder files'
Assert-Condition (-not (Test-Path -LiteralPath $registryPath)) 'Uninstall removes only the QA product registration'
Assert-SyntheticData
Install-Qa $upgrade 'reinstall-after-retain'
Assert-SyntheticData
Uninstall-Qa 'uninstall-delete' $true
Assert-Condition (-not (Test-Path -LiteralPath $dataDirectory)) 'Explicit delete-data uninstall removes the whole ordinary application data tree'

Write-SyntheticData
New-Item -ItemType Junction -Path (Join-Path $dataDirectory 'external-link') -Value $externalDirectory | Out-Null
Install-Qa $upgrade 'nested-link-install'
Uninstall-Qa 'nested-link-uninstall' $true
Assert-Condition (Test-Path -LiteralPath (Join-Path $dataDirectory 'external-link')) 'Cleanup retains directory links rather than traversing them'
Assert-Condition ((Get-Content -LiteralPath (Join-Path $root 'nested-link-uninstall.log') -Raw) -match 'some personal data remains') 'Incomplete link-safe cleanup is recorded'
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'workspace.json'))) 'Ordinary files are cleaned even when a link must be retained'
$linkToRemove = [System.IO.Path]::GetFullPath((Join-Path $dataDirectory 'external-link'))
if (-not $linkToRemove.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe QA link removal.' }
([System.IO.DirectoryInfo]::new($linkToRemove)).Delete()
([System.IO.DirectoryInfo]::new($dataDirectory)).Delete()
New-Item -ItemType Junction -Path $dataDirectory -Value $externalDirectory | Out-Null
Install-Qa $upgrade 'root-link-install'
Uninstall-Qa 'root-link-uninstall' $true
Assert-Condition ((Get-Content -LiteralPath (Join-Path $root 'root-link-uninstall.log') -Raw) -match 'some personal data remains') 'A linked data root is refused without traversing its target'
Assert-Condition ((Get-Content -LiteralPath (Join-Path $externalDirectory 'private-key') -Raw) -ceq 'Synthetic key outside app data') 'Private key outside application data survives every operation'
Assert-Condition ((Get-Content -LiteralPath (Join-Path $externalDirectory 'download.txt') -Raw) -ceq 'Synthetic download outside app data') 'SFTP download outside application data survives every operation'
Assert-Condition (-not (Test-Path -LiteralPath $registryPath)) 'All isolated QA installation registration is removed'
$summary = [ordered]@{ passed = $results.Count; root = $root; productId = $appId; assertions = $results }
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
Write-Host "All $($results.Count) assertions passed. Logs and synthetic files: $root"
