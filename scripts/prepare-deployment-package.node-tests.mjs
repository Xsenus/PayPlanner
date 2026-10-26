import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { preparePackage, sha256 } from './prepare-deployment-package.mjs';

const revision = 'a'.repeat(40);
function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'pp-qualified-package-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, 'be/AppReleases'), { recursive: true });
  mkdirSync(join(root, 'fe/assets'), { recursive: true });
  const files = {
    'be/PayPlanner.Api.dll': 'qualified api', 'be/PayPlanner.Api.Migrations.dll': 'qualified migrations',
    'be/AppReleases/releases.json': '[{"releaseId":"existing"}]',
    'fe/index.html': '<html>qualified</html>', 'fe/assets/русский файл.js': 'nested asset',
    'fe/payplanner-app-cache.js': 'cache', 'fe/payplanner-worker.js': 'worker',
    'fe/payplanner-push-worker.js': 'push', 'fe/deploy-revision.txt': revision + '\n',
  };
  for (const [path, text] of Object.entries(files)) writeFileSync(join(root, path), text);
  return { root, files };
}

test('inventories every qualified byte, including nested unicode assets, and packs only explicit ordinary files', t => {
  const { root, files } = fixture(t);
  mkdirSync(join(root, 'deploy'));
  writeFileSync(join(root, 'deploy/not-an-application.sh'), 'not part of package');
  const manifest = preparePackage(root, revision);
  assert.equal(manifest.revision, revision);
  assert.equal(manifest.files.length, Object.keys(files).length);
  for (const file of manifest.files) {
    assert.equal(file.sha256, sha256(Buffer.from(files[file.path])));
    assert.equal(file.bytes, Buffer.byteLength(files[file.path]));
  }
  const names = readFileSync(join(root, 'deployment-files.nul'), 'utf8').split('\0');
  assert.deepEqual(names, [...manifest.files.map(file => file.path), 'deployment-manifest.json', 'SHA256SUMS', '']);
  const sums = readFileSync(join(root, 'SHA256SUMS'), 'utf8');
  assert.ok(sums.includes(sha256(readFileSync(join(root, 'deployment-manifest.json'))) + '  deployment-manifest.json\n'));
  assert.ok(!sums.includes('deploy/not-an-application.sh'));
});

test('rejects a stale frontend revision before creating deployment metadata', t => {
  const { root } = fixture(t);
  writeFileSync(join(root, 'fe/deploy-revision.txt'), 'b'.repeat(40) + '\n');
  assert.throws(() => preparePackage(root, revision), /Frontend revision differs/);
  assert.throws(() => readFileSync(join(root, 'deployment-manifest.json')), { code: 'ENOENT' });
});

test('rejects duplicate release identities', t => {
  const { root } = fixture(t);
  writeFileSync(join(root, 'be/AppReleases/releases.json'), '[{"releaseId":"x"},{"releaseId":"x"}]');
  assert.throws(() => preparePackage(root, revision), /Duplicate release identity/);
});

test('rejects unexpected links without following a junction into another directory', t => {
  const { root } = fixture(t);
  symlinkSync(join(root, 'be'), join(root, 'fe/assets/link'), 'junction');
  assert.throws(() => preparePackage(root, revision), /Package links/);
});

test('rejects Windows control/device names and repeated preparation', t => {
  const { root } = fixture(t);
  writeFileSync(join(root, 'fe/assets/con.txt'), 'not portable');
  assert.throws(() => preparePackage(root, revision), /device path/);
  rmSync(join(root, 'fe/assets/con.txt'));
  preparePackage(root, revision);
  assert.throws(() => preparePackage(root, revision), /metadata already exists/);
});
