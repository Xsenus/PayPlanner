import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { lstat, mkdir, open, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const schema = 1;
const digest = value => createHash('sha256').update(value).digest('hex');

function ownedPath(root, name) {
  const path = resolve(root, name);
  const rel = relative(resolve(root), path);
  assert.ok(rel && rel !== '..' && !rel.startsWith('..' + sep) && !isAbsolute(rel), 'Artifact path must stay inside its cache.');
  return path;
}

/** Content hashes, not mtimes: a restored timestamp must not qualify changed code. */
export async function hashFileTree(root, paths, { excludedDirectories = [] } = {}) {
  const excluded = new Set(excludedDirectories.map(path => resolve(root, path)));
  const files = [];
  const directories = [];
  function visit(path, info) {
    if (excluded.has(resolve(path))) return;
    // npm ci installs ordinary files here. Reject linked dependencies rather than
    // silently qualifying only a link while its external target can change.
    assert.ok(!info.isSymbolicLink(), 'Browser build input/output cannot be a symbolic link: ' + relative(root, path));
    if (info.isDirectory()) directories.push(path);
    else if (info.isFile()) files.push(path);
  }
  for (const path of [...new Set(paths.map(path => resolve(root, path)))].sort()) {
    let info;
    try { info = await lstat(path); } catch (error) { if (error.code === 'ENOENT') continue; throw error; }
    visit(path, info);
  }
  // Directory entries already carry file types. A separate stat per dependency
  // file makes validation slower than compilation on Windows/antivirus disks.
  for (let index = 0; index < directories.length;) {
    const batch = directories.slice(index, index + 12);
    index += batch.length;
    await Promise.all(batch.map(async directory => {
      for (const entry of await readdir(directory, { withFileTypes: true })) visit(join(directory, entry.name), entry);
    }));
  }
  const result = new Array(files.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(12, files.length) }, async () => {
    while (next < files.length) {
      const index = next++;
      const bytes = await readFile(files[index]);
      result[index] = { path: relative(root, files[index]).split(sep).join('/'), bytes: bytes.length, sha256: digest(bytes) };
    }
  }));
  return result.sort((left, right) => left.path.localeCompare(right.path, 'en'));
}

export async function fingerprintBrowserInputs(root, extra = {}) {
  const topLevel = await readdir(root);
  const paths = ['src', 'public', 'vendor', 'node_modules', 'scripts/browser-ui-artifact-cache.mjs',
    'scripts/synthetic-browser-ui.mjs', 'scripts/case-browser-fixture-entry.mjs',
    ...topLevel.filter(name => /^(?:index\.html|package(?:-lock)?\.json|\.npmrc|\.browserslistrc|\.env(?:\..*)?|(?:vite|postcss|tailwind)\.config\.[^.]+|tsconfig(?:\.[^.]+)?\.json)$/.test(name))];
  const files = await hashFileTree(root, paths, {
    // These are generated caches, never package implementation inputs. In
    // particular the artifact must not recursively fingerprint itself.
    excludedDirectories: ['node_modules/.cache', 'node_modules/.vite', 'node_modules/.vite-temp', 'node_modules/.bin'],
  });
  assert.ok(files.some(file => file.path === 'package-lock.json'), 'A locked dependency installation is required.');
  const environment = Object.fromEntries(Object.entries(process.env)
    .filter(([key]) => key.startsWith('VITE_') || /^(?:BROWSERSLIST|BROWSERSLIST_ENV|BROWSERSLIST_CONFIG|BABEL_ENV)$/.test(key))
    .sort(([left], [right]) => left.localeCompare(right)));
  // The sole scenario-dependent build value is late-bound by a local script.
  environment.VITE_API_URL = 'browser-runtime';
  environment.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
  const orderedEnvironment = Object.fromEntries(Object.entries(environment).sort(([left], [right]) => left.localeCompare(right)));
  const fingerprint = digest(JSON.stringify({ schema, files, environment: orderedEnvironment, extra, node: process.versions,
    platform: process.platform, arch: process.arch, nodeEnv: 'production' }));
  // No environment values or source contents are persisted in the manifest.
  return { fingerprint, fileCount: files.length };
}

async function inspectArtifact(directory, fingerprint) {
  try {
    const manifest = JSON.parse(await readFile(join(directory, 'manifest.json'), 'utf8'));
    if (manifest.schema !== schema || manifest.fingerprint !== fingerprint) return null;
    const files = await hashFileTree(directory, ['dist']);
    if (!files.some(file => file.path === 'dist/index.html') || JSON.stringify(files) !== JSON.stringify(manifest.files)) return null;
    if (digest(JSON.stringify(manifest.metadata)) !== manifest.metadataSha256) return null;
    return { directory, outDir: join(directory, 'dist'), manifest };
  } catch (error) {
    if (error.code === 'ENOENT' || error instanceof SyntaxError || error instanceof assert.AssertionError) return null;
    throw error;
  }
}

/** Immutable content-addressed artifacts; only a complete manifest is published. */
export async function getOrBuildBrowserArtifact({ cacheDir, qualify, build, lockTimeoutMs = 150_000, pollMs = 100 }) {
  cacheDir = resolve(cacheDir);
  await mkdir(cacheDir, { recursive: true });
  const inputs = await qualify();
  assert.match(inputs.fingerprint, /^[a-f0-9]{64}$/);
  const directory = ownedPath(cacheDir, inputs.fingerprint);
  const lockPath = ownedPath(cacheDir, inputs.fingerprint + '.lock');
  const started = Date.now();
  let lock;
  while (!lock) {
    const hit = await inspectArtifact(directory, inputs.fingerprint);
    if (hit) return { ...hit, cacheHit: true };
    try { lock = await open(lockPath, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      // Never steal by age: a busy compiler may legitimately take longer. A
      // terminated owner is reported explicitly instead of racing a new owner.
      try {
        const owner = JSON.parse(await readFile(lockPath, 'utf8'));
        if (Number.isSafeInteger(owner.pid) && owner.pid > 0) {
          try { process.kill(owner.pid, 0); }
          catch (cause) {
            if (cause.code === 'ESRCH') throw new Error('Browser artifact lock belongs to a terminated process; remove the indicated stale lock after verification: ' + lockPath);
          }
        }
      } catch (cause) { if (cause.code !== 'ENOENT' && !(cause instanceof SyntaxError)) throw cause; }
      assert.ok(Date.now() - started < lockTimeoutMs, 'Timed out waiting for the browser artifact compiler: ' + lockPath);
      await delay(pollMs);
    }
  }
  const staging = ownedPath(cacheDir, inputs.fingerprint + '.building-' + randomUUID());
  try {
    await lock.writeFile(JSON.stringify({ pid: process.pid }));
    const hit = await inspectArtifact(directory, inputs.fingerprint);
    if (hit) return { ...hit, cacheHit: true };
    await mkdir(join(staging, 'dist'), { recursive: true });
    const metadata = await build({ directory: staging, outDir: join(staging, 'dist') });
    const after = await qualify();
    assert.equal(after.fingerprint, inputs.fingerprint, 'Browser build inputs changed during compilation; run again on a stable candidate.');
    const files = await hashFileTree(staging, ['dist']);
    assert.ok(files.some(file => file.path === 'dist/index.html'), 'The production browser build must contain index.html.');
    await writeFile(join(staging, 'manifest.json'), JSON.stringify({ schema, fingerprint: inputs.fingerprint,
      inputFileCount: inputs.fileCount, files, metadata, metadataSha256: digest(JSON.stringify(metadata)) }, null, 2));
    // Any invalid prior output is replaced only while holding this key's lock.
    await rm(directory, { force: true, recursive: true });
    await rename(staging, directory);
    const artifact = await inspectArtifact(directory, inputs.fingerprint);
    assert.ok(artifact, 'The published production browser artifact failed validation.');
    return { ...artifact, cacheHit: false };
  } finally {
    await rm(staging, { force: true, recursive: true });
    await lock.close();
    await rm(lockPath, { force: true });
  }
}
