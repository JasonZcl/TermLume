'use strict';

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');

async function main() {
  if (process.platform !== 'darwin') throw new Error('Build macOS applications on a Mac or with the macOS GitHub Actions workflow.');
  const architecture = process.argv[2];
  if (!['arm64', 'x64'].includes(architecture)) throw new Error('Choose arm64 or x64.');
  const desktop = path.resolve(__dirname, '..');
  process.chdir(desktop);
  const result = spawnSync(process.execPath, [path.join(__dirname, 'build-backend.cjs'), `osx-${architecture}`], { stdio: 'inherit', shell: false });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Backend publish failed.');
  const { build, Platform, Arch } = require('electron-builder');
  const staging = fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-mac-build-'));
  const artifacts = path.resolve(desktop, '../artifacts');
  await build({ targets: Platform.MAC.createTarget(['dmg', 'zip'], Arch[architecture]), config: { directories: { output: staging } }, publish: 'never' });
  fs.mkdirSync(artifacts, { recursive: true });
  const installers = fs.readdirSync(staging).filter(name => /\.(dmg|zip)$/.test(name));
  if (installers.length !== 2) throw new Error(`Expected a DMG and ZIP. Inspect ${staging}`);
  for (const name of installers) {
    const destination = path.join(artifacts, name);
    fs.copyFileSync(path.join(staging, name), destination);
    const hash = crypto.createHash('sha256').update(fs.readFileSync(destination)).digest('hex');
    console.log(`${hash}  ${destination}`);
  }
  console.log(`Intermediate build files remain outside the repository: ${staging}`);
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
