import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { lstat, mkdir, open, readFile, readdir, readlink, realpath, rename, unlink, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

const sha = value => createHash('sha256').update(value).digest('hex');
const json = value => JSON.stringify(value);
const exists = path => lstat(path).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; });
const inside = (root, path) => path === root || path.startsWith(root + sep);
const caches = new Set(['.vite', '.vite-temp', '.cache']);

async function fileHash(path) {
  const info = await lstat(path);
  assert.ok(info.isFile() && !info.isSymbolicLink(), 'Input or artifact must be an ordinary file');
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

// Sorted content manifests, not timestamps. Only dependency-internal file links are allowed.
async function tree(root, dependencies = false) {
  const rootInfo = await lstat(root);
  assert.ok(rootInfo.isDirectory() && !rootInfo.isSymbolicLink(), 'Input or artifact root must be an ordinary directory');
  const directories = [root], files = [];
  // Keep the complete file/link boundary; hide Windows metadata latency with bounded I/O.
  for (let index = 0; index < directories.length;) {
    const batch = directories.slice(index, index + 12);
    index += batch.length;
    const scanned = await Promise.allSettled(batch.map(async directory => {
      const info = await lstat(directory);
      assert.ok(info.isDirectory() && !info.isSymbolicLink(), 'Input or artifact root must be an ordinary directory');
      for (const entry of await readdir(directory, { withFileTypes: true })) {
        if (dependencies && caches.has(entry.name)) continue;
        const path = join(directory, entry.name), key = relative(root, path).replaceAll('\\', '/');
        if (entry.isSymbolicLink()) {
          const target = await realpath(path);
          assert.ok(dependencies && inside(root, target) && (await lstat(target)).isFile(), 'Unowned input link');
          files.push({ path, key, target, link: await readlink(path) });
        } else if (entry.isDirectory()) directories.push(path);
        else { assert.ok(entry.isFile(), 'Non-ordinary input'); files.push({ path, key }); }
      }
    }));
    const rejected = scanned.find(result => result.status === 'rejected');
    if (rejected) throw rejected.reason;
  }
  files.sort((left, right) => left.key < right.key ? -1 : left.key > right.key ? 1 : 0);
  const records = new Array(files.length);
  let next = 0, failed = false, failure;
  await Promise.all(Array.from({ length: Math.min(12, files.length) }, async () => {
    while (!failed) {
      const index = next++;
      if (index >= files.length) return;
      const entry = files[index];
      try {
        const digest = await fileHash(entry.target ?? entry.path);
        records[index] = [entry.key, entry.target ? { link: entry.link, target: entry.target, sha256: digest } : digest];
      } catch (error) {
        if (!failed) failure = error;
        failed = true;
      }
    }
  }));
  // Every started read settles before a failed owner can be released.
  if (failed) throw failure;
  return Object.fromEntries(records);
}

async function ownedRoot(root, owner) {
  assert.ok(isAbsolute(root) && resolve(root) === root);
  assert.match(owner, /^[a-f0-9]{32}$/);
  const temporaryRoot = resolve(tmpdir());
  if (dirname(root) !== temporaryRoot) console.log('CASE_RETIREMENT_BUNDLE_TEMP_ROOT_MISMATCH');
  assert.equal(dirname(root), temporaryRoot);
  assert.equal(basename(root), 'pp_case_retirement_bundle_' + owner);
  assert.ok((await lstat(root)).isDirectory() && !(await lstat(root)).isSymbolicLink());
}

async function readJson(path) {
  await fileHash(path);
  return JSON.parse(await readFile(path, 'utf8'));
}

async function ownerRecord(root, owner, api) {
  await ownedRoot(root, owner);
  assert.ok((await lstat(join(root, 'owner.json'))).isFile() && !(await lstat(join(root, 'owner.json'))).isSymbolicLink());
  const record = await readJson(join(root, 'owner.json'));
  assert.deepEqual(record, { schema: 1, id: owner, root, apiOrigin: api.origin });
  assert.equal(api.hostname, '127.0.0.1'); assert.equal(api.protocol, 'http:');
  assert.ok(api.port && api.pathname === '/');
  return record;
}

export async function retirementBundlePhase(name, action) {
  const started = performance.now();
  let completed = false;
  try { const result = await action(); completed = true; return result; }
  finally { console.log('CASE_RETIREMENT_BUNDLE_PHASE ' + json({ phase: name, elapsedMs: performance.now() - started, completed })); }
}
const phase = retirementBundlePhase;

const stableEnvironment = () => Object.fromEntries(Object.entries(process.env)
  .filter(([key]) => !key.startsWith('CASE_RETIREMENT_')).sort(([a], [b]) => a.localeCompare(b))
  .map(([key, value]) => [key, sha(value)]));

export async function withRetirementBuildEnvironment(action) {
  const scenario = Object.entries(process.env).filter(([key]) => key.startsWith('CASE_RETIREMENT_'));
  for (const [key] of scenario) delete process.env[key];
  try { return await action(); }
  finally { for (const [key, value] of scenario) process.env[key] = value; }
}

async function sourceInputs(repo, root, environment = stableEnvironment()) {
  const files = {};
  for (const name of ['src', 'public', 'scripts', 'vendor']) files[name] = await tree(join(repo, name));
  for (const name of (await readdir(repo)).sort()) {
    const info = await lstat(join(repo, name));
    if (name === '.git' || name === 'node_modules' || info.isDirectory()) continue;
    assert.ok(info.isFile() && !info.isSymbolicLink(), 'Non-ordinary root configuration');
    files[name] = await fileHash(join(repo, name));
  }
  return { repo, environment, files, entry: await fileHash(join(root, 'fixture.mjs')),
    envFiles: await tree(join(root, 'env')), envDir: join(root, 'env'), cacheDir: join(root, 'vite-cache'),
    outDir: join(root, 'building'), input: { index: join(repo, 'index.html'), 'case-retirement-fixture': join(root, 'fixture.mjs') },
    preserveEntrySignatures: 'strict', nodeEnv: 'production' };
}

export async function captureRetirementDependencyInputs(repo, executable = process.execPath) {
  const runtime = await realpath(join(repo, 'node_modules'));
  return { runtime, files: await tree(runtime, true), node: await realpath(executable), nodeSha256: await fileHash(executable), nodeVersion: process.version };
}

export async function verifyRetirementDependencyInputs(admitted, repo, executable = process.execPath) {
  assert.deepEqual(await captureRetirementDependencyInputs(repo, executable), admitted,
    'Dependency/tool drift invalidates the entire class, including previously passed cases');
}

async function failOwner(root, error) {
  if (!await exists(join(root, 'failure.json')))
    await writeFile(join(root, 'failure.json'), json({ status: 'faulted-no-retry', error: error.name }), { flag: 'wx' });
}

async function verifyAssets(bundle, descriptor) {
  const actual = await tree(bundle);
  delete actual['descriptor.json'];
  assert.deepEqual(actual, descriptor.assets, 'Production artifact was replaced, extended or truncated');
  for (const name of ['index.html', descriptor.fixtureFile, ...descriptor.disconnectFiles, ...descriptor.pollingFiles])
    assert.ok(Object.hasOwn(actual, name), 'A required entry or notification chunk is missing');
}

export async function openRetirementProductionBundle({ root, owner, api, repo = resolve('.'), build }) {
  await ownerRecord(root, owner, api);
  assert.equal(process.env.NODE_ENV, 'production');
  assert.equal(process.env.VITE_API_URL, new URL('/api', api).href);
  assert.equal(process.env.VITE_RESTORED_RUNTIME_ISOLATION, 'false');
  assert.ok(!await exists(join(root, 'failure.json')), 'Faulted class owner cannot build or qualify another case');
  const lockPath = join(root, 'bundle-use.lock'), lock = await open(lockPath, 'wx');
  const lockText = json({ owner, token: randomUUID() });
  await lock.writeFile(lockText);
  const identity = await lock.stat();
  const bundle = join(root, 'bundle');
  let descriptor, admission, closed = false;
  const close = async () => {
    if (closed) return;
    closed = true;
    try {
      const current = await lstat(lockPath);
      assert.ok(current.isFile() && !current.isSymbolicLink() && current.dev === identity.dev && current.ino === identity.ino);
      assert.equal(await fileHash(lockPath), sha(lockText), 'Child lease was replaced; do not unlink it');
      await unlink(lockPath);
    } finally { await lock.close(); }
  };
  try {
    const ready = await exists(bundle);
    if (!ready) {
      // This is deliberately never removed: timeout/partial failure cannot trigger another build.
      await writeFile(join(root, 'build-attempt.json'), json({ owner, apiOrigin: api.origin }), { flag: 'wx' });
      assert.ok(!await exists(join(root, 'building')) && !await exists(join(root, 'class-inputs.json')), 'Partial build cannot be retried');
      await mkdir(join(root, 'env'));
      await writeFile(join(root, 'fixture.mjs'),
        `export { beginCaseDocumentUpload } from ${json(join(repo, 'src/services/caseDocumentUploadAttempts.ts').replaceAll('\\', '/'))};\n`
        + `export { getAuthToken } from ${json(join(repo, 'src/services/api.ts').replaceAll('\\', '/'))};\n`, { flag: 'wx' });
      admission = { owner, apiOrigin: api.origin,
        dependencies: await phase('class-dependency-admission', () => captureRetirementDependencyInputs(repo)) };
      await writeFile(join(root, 'class-inputs.json'), json(admission), { flag: 'wx' });
      const inputs = await phase('source-key-cold', () => sourceInputs(repo, root));
      await mkdir(join(root, 'building'));
      const config = configuration(inputs, join(root, 'building'));
      const built = await phase('production-build', () => withRetirementBuildEnvironment(() => build(config)));
      const chunks = (Array.isArray(built) ? built : [built]).flatMap(output => output.output ?? []).filter(chunk => chunk.type === 'chunk');
      assert.ok(chunks.some(chunk => chunk.isEntry && chunk.name === 'index' && chunk.code.length > 0));
      const entries = chunks.filter(chunk => chunk.isEntry && chunk.name === 'case-retirement-fixture');
      assert.equal(entries.length, 1);
      assert.ok(['beginCaseDocumentUpload', 'getAuthToken'].every(name => entries[0].exports.includes(name)));
      const modules = source => chunks.filter(chunk => chunk.moduleIds.some(id => id.replaceAll('\\', '/').endsWith(source))).map(chunk => chunk.fileName);
      descriptor = { owner, apiOrigin: api.origin, inputs, classInputsSha256: sha(json(admission)), fixtureFile: entries[0].fileName,
        disconnectFiles: modules('/src/services/realtimeNotifications.ts'), pollingFiles: modules('/src/components/Notifications/RealtimeNotificationToasts.tsx'),
        assets: await phase('asset-publish-hash', () => tree(join(root, 'building'))) };
      assert.ok(descriptor.disconnectFiles.length && descriptor.pollingFiles.length);
      assert.ok(chunks.every(chunk => Object.hasOwn(descriptor.assets, chunk.fileName)), 'A compiled chunk was not emitted');
      assert.deepEqual(await phase('source-key-after-build', () => sourceInputs(repo, root)), inputs, 'Inputs changed during production build');
      await writeFile(join(root, 'building', 'descriptor.json'), json(descriptor), { flag: 'wx' });
      await rename(join(root, 'building'), bundle);
      await writeFile(join(root, 'bundle-ready.pending'), json({ owner, descriptorSha256: await fileHash(join(bundle, 'descriptor.json')) }), { flag: 'wx' });
      await rename(join(root, 'bundle-ready.pending'), join(root, 'bundle-ready.json'));
    } else {
      assert.ok(await exists(join(root, 'build-attempt.json')), 'Unclaimed ready artifact');
      admission = await readJson(join(root, 'class-inputs.json'));
      descriptor = await readJson(join(bundle, 'descriptor.json'));
      assert.equal(descriptor.classInputsSha256, sha(json(admission)));
      assert.deepEqual(await phase('source-key-hit', () => sourceInputs(repo, root)), descriptor.inputs, 'Source/config/environment changed between cases');
    }
    assert.equal(admission.owner, owner); assert.equal(descriptor.owner, owner);
    const readyRecord = await readJson(join(root, 'bundle-ready.json'));
    assert.equal(readyRecord.owner, owner);
    assert.equal(readyRecord.descriptorSha256, await fileHash(join(bundle, 'descriptor.json')));
    assert.equal(admission.apiOrigin, api.origin); assert.equal(descriptor.apiOrigin, api.origin);
    assert.equal(admission.dependencies.node, await realpath(process.execPath));
    assert.equal(admission.dependencies.nodeVersion, process.version);
    assert.equal(admission.dependencies.runtime, await realpath(join(repo, 'node_modules')));
    await phase('asset-before-case', () => verifyAssets(bundle, descriptor));
    return { ...descriptor, config: configuration(descriptor.inputs, bundle), close,
      async verify() {
        try {
          await ownerRecord(root, owner, api);
          assert.deepEqual(await readJson(join(root, 'bundle-ready.json')), readyRecord);
          assert.equal(await fileHash(join(bundle, 'descriptor.json')), readyRecord.descriptorSha256);
          assert.equal(sha(await readFile(join(root, 'class-inputs.json'))), descriptor.classInputsSha256);
          assert.deepEqual(await phase('source-key-after-case', () => sourceInputs(repo, root)), descriptor.inputs);
          await phase('asset-after-case', () => verifyAssets(bundle, descriptor));
        } catch (error) { await failOwner(root, error); throw error; }
      } };
  } catch (error) { await failOwner(root, error); await close(); throw error; }
}

function configuration(inputs, outDir) {
  return { envDir: inputs.envDir, cacheDir: inputs.cacheDir, logLevel: 'error',
    build: { outDir, emptyOutDir: false, rollupOptions: { input: inputs.input, preserveEntrySignatures: 'strict' } } };
}

export async function finalizeRetirementProductionBundle(root, owner) {
  await ownedRoot(root, owner);
  const admission = await readJson(join(root, 'class-inputs.json'));
  const api = new URL(admission.apiOrigin);
  await ownerRecord(root, owner, api);
  assert.equal(admission.owner, owner);
  assert.ok(!await exists(join(root, 'bundle-use.lock')) && !await exists(join(root, 'failure.json')), 'Class owner is busy or faulted');
  try {
    const descriptor = await readJson(join(root, 'bundle', 'descriptor.json'));
    const ready = await readJson(join(root, 'bundle-ready.json'));
    assert.equal(ready.owner, owner);
    assert.equal(ready.descriptorSha256, await fileHash(join(root, 'bundle', 'descriptor.json')));
    assert.equal(descriptor.classInputsSha256, sha(json(admission)));
    await phase('class-dependency-finalization', () => verifyRetirementDependencyInputs(admission.dependencies, descriptor.inputs.repo));
    assert.deepEqual(await phase('class-source-finalization', () => sourceInputs(descriptor.inputs.repo, root, descriptor.inputs.environment)), descriptor.inputs);
    await phase('class-asset-finalization', () => verifyAssets(join(root, 'bundle'), descriptor));
    console.log('CASE_RETIREMENT_BUNDLE_CLASS_INPUTS_UNCHANGED ' + json({ owner, dependencyFiles: Object.keys(admission.dependencies.files).length }));
  } catch (error) { await failOwner(root, error); throw error; }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  assert.equal(process.argv[2], '--finalize');
  await finalizeRetirementProductionBundle(process.argv[3], process.argv[4]);
}
