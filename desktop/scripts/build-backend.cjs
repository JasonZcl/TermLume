'use strict';

const { spawnSync } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');

const runtime = process.argv[2] || `${process.platform === 'darwin' ? 'osx' : process.platform === 'win32' ? 'win' : 'linux'}-${process.arch}`;
if (!/^(osx|win|linux)-(arm64|x64)$/.test(runtime)) throw new Error('Unsupported runtime');
const root = path.resolve(__dirname, '../..');
const destination = path.join(root, 'desktop/backend', runtime);
const result = spawnSync('dotnet', ['publish', path.join(root, 'TermLume.DesktopBackend/TermLume.DesktopBackend.csproj'), '-c', 'Release', '-r', runtime, '--self-contained', 'true', '-o', destination, '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true'], { stdio: 'inherit', shell: false });
if (result.error) throw result.error;
if (result.status !== 0) process.exit(result.status || 1);
if (runtime.startsWith('osx-')) fs.chmodSync(path.join(destination, 'TermLume.DesktopBackend'), 0o755);
