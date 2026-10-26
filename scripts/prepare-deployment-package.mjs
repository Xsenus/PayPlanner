import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { safeRelativePath } from './xsenus-artifact-store.mjs';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;

// The input is the already qualified artifact tree. No compilation or mutation
// of application files happens here; tar receives only an explicit file list.
export function preparePackage(root, revision) {
  assert.match(revision ?? '', /^[a-f0-9]{40}$/);
  assert.ok(lstatSync(root).isDirectory() && !lstatSync(root).isSymbolicLink());
  for (const name of ['deployment-manifest.json', 'SHA256SUMS', 'deployment-files.nul']) {
    assert.ok(!existsSync(join(root, name)), 'Deployment metadata already exists');
  }
  const files = [];
  const folded = new Set();
  function visit(name) {
    safeRelativePath(name);
    assert.ok(!folded.has(name.toLowerCase()), 'Case-insensitive package path collision');
    folded.add(name.toLowerCase());
    const path = join(root, ...name.split('/'));
    const stat = lstatSync(path);
    assert.ok(!stat.isSymbolicLink(), 'Package links are forbidden');
    if (stat.isDirectory()) {
      for (const child of readdirSync(path).sort(compare)) visit(name + '/' + child);
    } else {
      assert.ok(stat.isFile() && stat.nlink === 1, 'Only independent ordinary package files are allowed');
      const bytes = readFileSync(path);
      assert.equal(bytes.length, stat.size);
      const after = lstatSync(path);
      assert.deepEqual([after.dev, after.ino, after.size, after.mtimeMs, after.ctimeMs],
        [stat.dev, stat.ino, stat.size, stat.mtimeMs, stat.ctimeMs], 'Package changed during inventory');
      files.push({ path: name, sha256: sha256(bytes), bytes: bytes.length });
    }
  }
  for (const prefix of ['be', 'fe']) {
    assert.ok(lstatSync(join(root, prefix)).isDirectory());
    visit(prefix);
  }
  files.sort((a, b) => compare(a.path, b.path));
  for (const name of ['be/PayPlanner.Api.dll', 'be/PayPlanner.Api.Migrations.dll', 'be/AppReleases/releases.json',
    'fe/index.html', 'fe/payplanner-app-cache.js', 'fe/payplanner-push-worker.js', 'fe/payplanner-worker.js']) {
    assert.ok(files.some(file => file.path === name && file.bytes > 0), 'Missing qualified file: ' + name);
  }
  assert.deepEqual(readFileSync(join(root, 'fe/deploy-revision.txt')), Buffer.from(revision + '\n'), 'Frontend revision differs');
  const releases = JSON.parse(readFileSync(join(root, 'be/AppReleases/releases.json'), 'utf8'));
  assert.ok(Array.isArray(releases) && releases.length > 0, 'Release catalog is empty');
  assert.equal(new Set(releases.map(row => row.releaseId)).size, releases.length, 'Duplicate release identity');
  const manifest = { schemaVersion: 1, revision, files };
  const raw = Buffer.from(JSON.stringify(manifest, null, 2) + '\n');
  const sums = [...files, { path: 'deployment-manifest.json', sha256: sha256(raw) }]
    .map(file => `${file.sha256}  ${file.path}\n`).join('');
  // Refuse stale metadata rather than silently adopting a prior preparation.
  writeFileSync(join(root, 'deployment-manifest.json'), raw, { flag: 'wx' });
  writeFileSync(join(root, 'SHA256SUMS'), sums, { flag: 'wx' });
  writeFileSync(join(root, 'deployment-files.nul'),
    Buffer.from([...files.map(file => file.path), 'deployment-manifest.json', 'SHA256SUMS'].join('\0') + '\0'), { flag: 'wx' });
  return manifest;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  assert.equal(process.argv.length, 4, 'Expected package directory and complete revision');
  const manifest = preparePackage(resolve(process.argv[2]), process.argv[3]);
  console.log(JSON.stringify({ status: 'package-inventoried', revision: manifest.revision, fileCount: manifest.files.length }));
}
