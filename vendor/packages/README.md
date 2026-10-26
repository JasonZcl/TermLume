# SSH.NET local security build

`SSH.NET.2026.0.0.2.nupkg` is a TermLume-maintained build of SSH.NET 2026.0.0, **not an official upstream release**. The upstream MIT license is included in the package and in `TermLume/Web/vendor/sshnet.LICENSE`.

- Upstream commit: `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e` (tag `2026.0.0`).
- Source archive SHA-256: `c75daf0093744f12bf50f9e7708c6460203ddd824ea7d2f441def7066f114cf0`.
- Runtime dependencies remain BouncyCastle.Cryptography 2.7.0 and Microsoft.Extensions.Logging.Abstractions 8.0.3.
- Changes: expose `ShellStream.MaximumReadBufferLength = 4 MiB`; reject incoming channel data **before buffer allocation** when it would exceed this limit. The exception follows SSH.NET's existing session error path and terminates the offending connection. The limit also applies while PTY/shell creation is pending.
- The rest of the upstream runtime source is unchanged. PTY resize, SSH authentication and SFTP retain their existing implementations.
- The receive-limit exception is forwarded through the channel exception event into the existing session failure path; it must not be swallowed by SSH.NET's channel-level exception handler.
- Checked-in package SHA-256: `898c830dee04dbbfac0bbb1a96961adbf89cd8a7f0fdfb8027e2b8d089a502f2`.

Rebuild with `powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build-SshNet.ps1`. The script verifies the pinned source archive and produces the package from its runtime source for .NET 10. This is source-reproducible; NuGet archive timestamps can change the package hash between builds.

`NuGet.config` restricts `SSH.NET` resolution to this directory. Keep this package and its patch script together in Git. When upgrading upstream, re-evaluate the need for the patch and run the security regressions; do not silently replace it with an unbounded upstream build. NuGet audit alone is not sufficient for a locally maintained dependency: also review upstream SSH.NET advisories against base version 2026.0.0.
