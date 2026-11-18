# Local terminal assets

These files are distributed locally; the application never loads terminal scripts from a CDN.

- `@xterm/xterm` 6.0.0: `xterm.js`, `xterm.css`, `xterm.LICENSE` (MIT)
- `@xterm/addon-fit` 0.11.0: `addon-fit.js`, `addon-fit.LICENSE` (MIT)

Upstream: https://github.com/xtermjs/xterm.js

The unmodified distribution bundles were obtained from the npm registry tarballs for these exact versions.

## Native dependency notices

License and notice files for SSH.NET 2026.0.0, WebView2 SDK 1.0.4314.35, BouncyCastle.Cryptography 2.7.0, Microsoft.Extensions.Logging.Abstractions 10.0.13, and Microsoft.Extensions.DependencyInjection.Abstractions are included alongside the terminal licenses. Their runtime assemblies are included in the published desktop application. SSH.NET is the local TermLume security revision 2026.0.0.2, not an official upstream release: it limits ShellStream receive buffering to 4 MiB before allocation, including pending shell creation. The pinned upstream source and rebuild instructions are documented in the repository's `vendor/packages/README.md` and `tools/Build-SshNet.ps1`.

Account authentication includes Duende.IdentityModel.OidcClient 7.1.1 and Duende.IdentityModel 8.1.0 (Apache-2.0, `duende.LICENSE.txt`), plus Microsoft.IdentityModel.JsonWebTokens and its IdentityModel dependencies 8.23.0 (MIT, `identitymodel.LICENSE.txt`). Source: https://github.com/DuendeSoftware/foss and https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet.
