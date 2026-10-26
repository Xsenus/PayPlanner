import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, readFile, rm, unlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { captureRetirementDependencyInputs, finalizeRetirementProductionBundle,
  openRetirementProductionBundle, verifyRetirementDependencyInputs } from './case-retirement-production-bundle.mjs';

async function fixture(t) {
  const owner = randomUUID().replaceAll('-', ''), root = join(tmpdir(), 'pp_case_retirement_bundle_' + owner);
  const repo = await mkdtemp(join(tmpdir(), 'pp_bundle_source_'));
  const api = new URL('http://127.0.0.1:55001/');
  await mkdir(root);
  for (const name of ['src', 'public', 'scripts', 'vendor', 'node_modules']) await mkdir(join(repo, name));
  await writeFile(join(repo, 'src', 'example.ts'), 'export const value = 1;');
  await writeFile(join(repo, 'index.html'), '<html></html>');
  await writeFile(join(repo, 'vite.config.ts'), 'export default {};');
  await writeFile(join(repo, 'package.json'), '{"private":true}');
  await writeFile(join(repo, 'node_modules', 'dependency.js'), 'dependency-one');
  await writeFile(join(root, 'owner.json'), JSON.stringify({ schema: 1, id: owner, root, apiOrigin: api.origin }));
  const previous = { ...process.env };
  Object.assign(process.env, { NODE_ENV: 'production', VITE_API_URL: new URL('/api', api).href, VITE_RESTORED_RUNTIME_ISOLATION: 'false' });
  t.after(async () => {
    for (const name of Object.keys(process.env)) if (!(name in previous)) delete process.env[name];
    Object.assign(process.env, previous);
    await rm(root, { recursive: true, force: true });
    await rm(repo, { recursive: true, force: true });
  });
  let builds = 0;
  const build = async config => {
    builds++;
    assert.ok(!Object.keys(process.env).some(name => name.startsWith('CASE_RETIREMENT_')), 'Scenario secrets must not reach the build');
    await writeFile(join(config.build.outDir, 'index.html'), '<html>real-entry-stand-in</html>');
    await writeFile(join(config.build.outDir, 'index.js'), 'compiled-index');
    await writeFile(join(config.build.outDir, 'fixture.js'), 'compiled-helper-stand-in');
    return { output: [
      { type: 'chunk', isEntry: true, name: 'index', code: 'compiled-index', fileName: 'index.js', exports: [],
        moduleIds: ['/src/services/realtimeNotifications.ts', '/src/components/Notifications/RealtimeNotificationToasts.tsx'] },
      { type: 'chunk', isEntry: true, name: 'case-retirement-fixture', fileName: 'fixture.js',
        exports: ['beginCaseDocumentUpload', 'getAuthToken'], moduleIds: [] },
    ] };
  };
  return { root, owner, repo, api, build, count: () => builds,
    open: overrides => openRetirementProductionBundle({ root, owner, repo, api, build, ...overrides }) };
}

test('one cold production build serves subsequent verified hits, never scenario outcomes', async t => {
  const f = await fixture(t);
  for (let scenario = 0; scenario < 3; scenario++) {
    process.env.CASE_RETIREMENT_PASSWORD = 'different-owned-secret-' + scenario;
    process.env.CASE_RETIREMENT_ID = String(scenario + 1);
    const bundle = await f.open();
    await bundle.verify();
    await bundle.close();
  }
  assert.equal(f.count(), 1);
  assert.ok(!(await readFile(join(f.root, 'bundle', 'descriptor.json'), 'utf8')).includes('different-owned-secret'));
  await finalizeRetirementProductionBundle(f.root, f.owner);
});

for (const change of ['source', 'configuration', 'environment', 'entry', 'asset', 'asset-removed', 'descriptor', 'ready-removed']) {
  test(`changed ${change} cannot qualify a hit or silently rebuild`, async t => {
    const f = await fixture(t), initial = await f.open();
    await initial.close();
    if (change === 'source') await writeFile(join(f.repo, 'src', 'example.ts'), 'export const value = 2;');
    if (change === 'configuration') await writeFile(join(f.repo, 'vite.config.ts'), 'export default {base:"/changed/"};');
    if (change === 'environment') process.env.VITE_NEW_INPUT = 'changed';
    if (change === 'entry') await writeFile(join(f.root, 'fixture.mjs'), 'wrong-entry');
    if (change === 'asset') await writeFile(join(f.root, 'bundle', 'fixture.js'), 'tampered');
    if (change === 'asset-removed') await unlink(join(f.root, 'bundle', 'fixture.js'));
    if (change === 'descriptor') await writeFile(join(f.root, 'bundle', 'descriptor.json'), '{}');
    if (change === 'ready-removed') await unlink(join(f.root, 'bundle-ready.json'));
    await assert.rejects(f.open());
    await assert.rejects(f.open(), /Faulted class owner/);
    assert.equal(f.count(), 1);
  });
}

test('partial failed first build is sticky even if no ready artifact was published', async t => {
  const f = await fixture(t);
  await assert.rejects(f.open({ build: async config => { await f.build(config); throw new Error('injected build failure'); } }));
  assert.equal(f.count(), 1);
  assert.ok(await readFile(join(f.root, 'build-attempt.json'), 'utf8'));
  await assert.rejects(f.open(), /Faulted class owner/);
  // Simulate a killed child that left only its durable attempt/partial outputs.
  await unlink(join(f.root, 'failure.json'));
  await assert.rejects(f.open());
  assert.equal(f.count(), 1);
});

test('a concurrent child cannot consume another child lease', async t => {
  const f = await fixture(t), bundle = await f.open();
  await assert.rejects(f.open(), { code: 'EEXIST' });
  await bundle.close();
  const next = await f.open(); await next.close();
  assert.equal(f.count(), 1);
});

test('a replaced child lease is preserved instead of unlinking foreign contents', async t => {
  const f = await fixture(t), bundle = await f.open();
  await writeFile(join(f.root, 'bundle-use.lock'), 'foreign-lease');
  await assert.rejects(bundle.close(), /Child lease was replaced/);
  assert.equal(await readFile(join(f.root, 'bundle-use.lock'), 'utf8'), 'foreign-lease');
  await assert.rejects(f.open(), { code: 'EEXIST' });
});

test('wrong owner or API address never starts a build', async t => {
  const f = await fixture(t);
  await assert.rejects(f.open({ owner: randomUUID().replaceAll('-', '') }));
  await assert.rejects(f.open({ api: new URL('http://127.0.0.1:55002/') }));
  assert.equal(f.count(), 0);
});

test('a missing emitted entry cannot be atomically published as ready', async t => {
  const f = await fixture(t);
  await assert.rejects(f.open({ build: async config => { const output = await f.build(config);
    await unlink(join(config.build.outDir, 'fixture.js')); return output; } }), /compiled chunk was not emitted/);
  await assert.rejects(f.open(), /Faulted class owner/);
  assert.equal(f.count(), 1);
});

for (const when of ['before-hit', 'after-hit']) test(`dependency drift ${when} invalidates the entire class at finalization`, async t => {
  const f = await fixture(t), cold = await f.open(); await cold.close();
  if (when === 'before-hit') await writeFile(join(f.repo, 'node_modules', 'dependency.js'), 'changed-before-hit');
  const hit = await f.open(); await hit.verify(); await hit.close();
  if (when === 'after-hit') await writeFile(join(f.repo, 'node_modules', 'dependency.js'), 'changed-after-hit');
  await assert.rejects(finalizeRetirementProductionBundle(f.root, f.owner), /Dependency\/tool drift/);
  await assert.rejects(f.open(), /Faulted class owner/);
  assert.equal(f.count(), 1);
});

test('source drift during the first build cannot publish a ready artifact', async t => {
  const f = await fixture(t);
  await assert.rejects(f.open({ build: async config => { const output = await f.build(config);
    await writeFile(join(f.repo, 'src', 'example.ts'), 'changed-during-build'); return output; } }), /Inputs changed during production build/);
  await assert.rejects(f.open(), /Faulted class owner/);
  assert.equal(f.count(), 1);
});

for (const change of ['dependency-before-hit', 'dependency-added', 'dependency-removed', 'tool-bytes']) {
  test(`full class boundary detects ${change} without timestamp shortcuts`, async t => {
    const f = await fixture(t), tool = join(f.repo, 'owned-node-stand-in');
    await writeFile(tool, 'tool-one');
    const admitted = await captureRetirementDependencyInputs(f.repo, tool);
    if (change === 'dependency-before-hit') await writeFile(join(f.repo, 'node_modules', 'dependency.js'), 'changed');
    if (change === 'dependency-added') await writeFile(join(f.repo, 'node_modules', 'new.js'), 'unexpected');
    if (change === 'dependency-removed') await unlink(join(f.repo, 'node_modules', 'dependency.js'));
    if (change === 'tool-bytes') await writeFile(tool, 'tool-two');
    await assert.rejects(verifyRetirementDependencyInputs(admitted, f.repo, tool), /Dependency\/tool drift/);
  });
}
