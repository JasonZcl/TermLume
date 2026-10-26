[CmdletBinding()]
param([string]$SourceArchive)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
$commit = '7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e'
$expectedHash = 'c75daf0093744f12bf50f9e7708c6460203ddd824ea7d2f441def7066f114cf0'
$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLume-sshnet-' + [guid]::NewGuid().ToString('N'))
$outputDirectory = Join-Path $repository 'vendor\packages'
New-Item -ItemType Directory -Path $workingDirectory, $outputDirectory -Force | Out-Null
if (-not $SourceArchive) {
    $SourceArchive = Join-Path $workingDirectory 'source.zip'
    Invoke-WebRequest "https://codeload.github.com/sshnet/SSH.NET/zip/$commit" -OutFile $SourceArchive
}
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'SSH.NET source archive does not match the pinned upstream SHA-256.'
}
Expand-Archive -LiteralPath $SourceArchive -DestinationPath (Join-Path $workingDirectory 'source')
$sourceRoot = Join-Path $workingDirectory "source\SSH.NET-$commit"
$streamPath = Join-Path $sourceRoot 'src\Renci.SshNet\ShellStream.cs'
$source = [IO.File]::ReadAllText($streamPath).Replace("`r`n", "`n")
$constantMarker = '        private const int DefaultBufferSize = 1024;'
$bufferMarker = '                _readBuffer.EnsureAvailableSpace(e.Data.Count);'
if ([regex]::Matches($source, [regex]::Escape($constantMarker)).Count -ne 1 -or
    [regex]::Matches($source, [regex]::Escape($bufferMarker)).Count -ne 1) {
    throw 'Pinned ShellStream source no longer matches the security patch.'
}
$constant = @'
        public const int MaximumReadBufferLength = 4 * 1024 * 1024;
'@
$guard = @'
                if (e.Data.Count > MaximumReadBufferLength - _readBuffer.ActiveLength)
                {
                    throw new ReceiveBufferLimitException();
                }

'@
$source = $source.Replace($constantMarker, $constantMarker + "`n`n" + $constant.TrimEnd())
$source = $source.Replace($bufferMarker, $guard.TrimEnd() + "`n`n" + $bufferMarker)
$subscribe = '            _channel.DataReceived += Channel_DataReceived;'
$unsubscribe = '                _channel.DataReceived -= Channel_DataReceived;'
$method = '        private void Channel_DataReceived(object? sender, ChannelDataEventArgs e)'
foreach ($marker in @($subscribe, $unsubscribe, $method)) {
    if ([regex]::Matches($source, [regex]::Escape($marker)).Count -ne 1) { throw 'ShellStream channel error markers do not match.' }
}
$errorHandler = @'
        private sealed class ReceiveBufferLimitException : SshConnectionException
        {
            public ReceiveBufferLimitException()
                : base("Terminal receive buffer limit exceeded.", Renci.SshNet.Messages.Transport.DisconnectReason.ProtocolError) { }
        }

        private static void Channel_Exception(object? sender, ExceptionEventArgs args)
        {
            if (args.Exception is ReceiveBufferLimitException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(args.Exception).Throw();
            }
        }

'@
$source = $source.Replace($subscribe, $subscribe + "`n            _channel.Exception += Channel_Exception;")
$source = $source.Replace($unsubscribe, $unsubscribe + "`n                _channel.Exception -= Channel_Exception;")
$source = $source.Replace($method, $errorHandler.TrimEnd() + "`n`n" + $method)
[IO.File]::WriteAllText($streamPath, $source, [Text.UTF8Encoding]::new($false))

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Renci.SshNet</AssemblyName>
    <PackageId>SSH.NET</PackageId>
    <Version>2026.0.0.2</Version>
    <AssemblyVersion>2026.0.0.0</AssemblyVersion>
    <Authors>SSH.NET contributors; TermLume security patch</Authors>
    <Description>SSH.NET 2026.0.0 with a 4 MiB pre-allocation ShellStream receive limit. This is a local TermLume build, not an official SSH.NET release.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Nullable>disable</Nullable>
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>UPSTREAM/Renci.SshNet.snk</AssemblyOriginatorKeyFile>
    <PathMap>UPSTREAM=/_/sshnet</PathMap>
    <RepositoryUrl>https://github.com/sshnet/SSH.NET</RepositoryUrl>
    <RepositoryCommit>UPSTREAMCOMMIT</RepositoryCommit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="UPSTREAM/src/Renci.SshNet/**/*.cs" Exclude="UPSTREAM/src/Renci.SshNet/obj/**;UPSTREAM/src/Renci.SshNet/bin/**" />
    <Compile Include="ThisAssembly.cs" />
    <PackageReference Include="BouncyCastle.Cryptography" Version="2.7.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.3" />
    <None Include="UPSTREAM/LICENSE" Pack="true" PackagePath="" />
    <None Include="PATCHREADME" Pack="true" PackagePath="README.md" />
  </ItemGroup>
</Project>
'@
$project = $project.Replace('UPSTREAMCOMMIT', $commit).Replace('UPSTREAM', $sourceRoot.Replace('\', '/'))
$project = $project.Replace('PATCHREADME', (Join-Path $repository 'vendor\packages\README.md').Replace('\', '/'))
$projectPath = Join-Path $workingDirectory 'PatchedSshNet.csproj'
[IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $workingDirectory 'ThisAssembly.cs'), 'internal static class ThisAssembly { internal const string NuGetPackageVersion = "2026.0.0.2"; }')
& dotnet pack $projectPath -c Release -o $outputDirectory --source 'https://api.nuget.org/v3/index.json' --nologo
if ($LASTEXITCODE -ne 0) { throw 'Patched SSH.NET package build failed.' }
Get-FileHash -LiteralPath (Join-Path $outputDirectory 'SSH.NET.2026.0.0.2.nupkg') -Algorithm SHA256
