import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { lstat, readFile, readdir, readlink, realpath } from 'node:fs/promises';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { promisify } from 'node:util';

const execute = promisify(execFile);
const schema = 1;
const sha256 = value => createHash('sha256').update(value).digest('hex');
const autoEnvironment = /^(?:PAYPLANNER_|VITE_|DOTNET_|COMPlus_|ASPNETCORE_|BackgroundServices__|Email__|ConnectionStrings__|NODE_OPTIONS$|NODE_ENV$|TEMP$|TMP$|PATH$|TZ$|LANG$|LC_|CI$)/i;

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') {
    assert.equal(Object.getPrototypeOf(value), Object.prototype, 'Verification environment must contain JSON values.');
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  assert.ok(value === null || ['string', 'boolean'].includes(typeof value) || (typeof value === 'number' && Number.isFinite(value)), 'Verification environment must contain finite JSON values.');
  return value;
}

function repoPath(root, path) {
  const absolute = resolve(root, path);
  const normalized = relative(root, absolute).split(sep).join('/');
  assert.ok(normalized && normalized !== '..' && !normalized.startsWith('../') && !isAbsolute(normalized), 'Verification path must be inside its repository.');
  return normalized;
}

async function hashFiles(root, files) {
  const results = [];
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(12, files.length) }, async () => {
    while (next < files.length) {
      const { path, info: knownInfo } = files[next++];
      const info = knownInfo ?? await lstat(path);
      assert.ok(info.isFile() && !info.isSymbolicLink(), 'Verification inputs must remain ordinary files: ' + relative(root, path));
      const bytes = await readFile(path);
      results.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length,
        executable: Boolean(info.mode & 0o111), sha256: sha256(bytes) });
    }
  }));
  return results.sort((left, right) => left.path.localeCompare(right.path, 'en'));
}

async function snapshotTree(root, { dependencies = false } = {}) {
  const directories = [root];
  const files = [];
  const executableLinks = [];
  const excluded = dependencies ? new Set(['.cache', '.vite', '.vite-temp'].map(name => join(root, name))) : new Set();
  const rootInfo = await lstat(root);
  assert.ok(rootInfo.isDirectory() && !rootInfo.isSymbolicLink(), 'Verification artifact/dependency root must be an ordinary directory.');
  const resolvedRoot = dependencies ? await realpath(root) : root;
  for (let index = 0; index < directories.length;) {
    const batch = directories.slice(index, index + 12);
    index += batch.length;
    await Promise.all(batch.map(async directory => {
      for (const entry of await readdir(directory, { withFileTypes: true })) {
        const path = join(directory, entry.name);
        if (excluded.has(path)) continue;
        if (entry.isSymbolicLink() && dependencies && directory === join(root, '.bin')) {
          // npm uses regular executable shims on Windows and file symlinks on
          // Unix. Both are verification inputs; pin link identity and target bytes.
          const linkTarget = await readlink(path);
          const target = await realpath(path);
          const targetPath = relative(resolvedRoot, target);
          assert.ok(targetPath && targetPath !== '..' && !targetPath.startsWith('..' + sep) && !isAbsolute(targetPath),
            'Dependency executable link target must stay inside its qualified dependency root: ' + relative(root, path));
          const targetInfo = await lstat(target);
          assert.ok(targetInfo.isFile() && !targetInfo.isSymbolicLink(), 'Dependency executable link must target an ordinary file: ' + relative(root, path));
          const bytes = await readFile(target);
          executableLinks.push({ path: relative(root, path).split(sep).join('/'), linkTarget,
            resolvedTarget: targetPath.split(sep).join('/'), bytes: bytes.length,
            executable: Boolean(targetInfo.mode & 0o111), sha256: sha256(bytes) });
          continue;
        }
        assert.ok(!entry.isSymbolicLink(), 'Verification cannot follow an unqualified symbolic link: ' + relative(root, path));
        if (entry.isDirectory()) directories.push(path);
        // File stats and byte reads are bounded and parallel in hashFiles.
        else if (entry.isFile()) files.push({ path });
        else assert.fail('Unsupported verification input: ' + relative(root, path));
      }
    }));
  }
  return [...await hashFiles(root, files), ...executableLinks]
    .sort((left, right) => left.path.localeCompare(right.path, 'en'));
}

/**
 * Snapshot only evidence, never credentials. A revision is deliberately not an
 * influencing input: committing unchanged files must preserve verification.
 * Documentation is excluded ONLY by an explicit path allowlist. Unknown files,
 * including new documents, are included unless Git ignores them. The project's
 * top-level .env files are explicitly pinned even when ignored: Vite consumes
 * them. Only byte hashes are stored; no global secret store is scanned. Tested
 * docs always override the allowance and may name an ignored fixture.
 */
export async function snapshotVerificationInputs({ root, artifactRoots = [], environment = {}, testedDocs = [], untestedDocs = [], dependencyRoots } = {}) {
  root = resolve(root ?? process.cwd());
  const result = await execute('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'],
    { cwd: root, windowsHide: true, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  const tested = new Set(testedDocs.map(path => repoPath(root, path)));
  const allowed = new Set(untestedDocs.map(path => {
    const normalized = repoPath(root, path);
    assert.match(normalized, /\.(?:md|rst|txt)$/i, 'Only explicitly untested prose may be excluded.');
    return normalized;
  }));
  const projectEnvFiles = (await readdir(root)).filter(name => /^\.env(?:\.[A-Za-z0-9_-]+)*$/.test(name));
  const paths = [...new Set([...result.stdout.split('\0').filter(Boolean).map(path => repoPath(root, path)), ...projectEnvFiles, ...tested])].sort();
  const files = [];
  for (const path of paths) {
    if (allowed.has(path) && !tested.has(path)) continue;
    let info;
    try { info = await lstat(resolve(root, path)); }
    catch (error) {
      if (error.code === 'ENOENT' && !tested.has(path)) continue; // A tracked deletion has the same inputs before/after commit.
      throw error;
    }
    assert.ok(info.isFile() && !info.isSymbolicLink(), 'Repository verification inputs must be ordinary files: ' + path);
    files.push({ path: resolve(root, path), info });
  }
  const repositoryFiles = await hashFiles(root, files);
  const artifacts = [];
  for (const artifactRoot of [...new Set(artifactRoots.map(path => resolve(root, path)))].sort()) {
    const artifactFiles = await snapshotTree(artifactRoot);
    assert.ok(artifactFiles.length > 0, 'A qualified artifact directory must not be empty: ' + artifactRoot);
    artifacts.push({ root: artifactRoot, files: artifactFiles });
  }
  const dependencies = [];
  if (dependencyRoots === undefined) {
    dependencyRoots = [];
    try { if ((await lstat(join(root, 'node_modules'))).isDirectory()) dependencyRoots.push(join(root, 'node_modules')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  for (const dependencyRoot of [...new Set(dependencyRoots.map(path => resolve(root, path)))].sort()) {
    dependencies.push({ root: dependencyRoot, files: await snapshotTree(dependencyRoot, { dependencies: true }) });
  }
  // Providers re-read external identities/binaries at every qualification,
  // including final/resume checks; retaining a start-time object cannot prove
  // that a PostgreSQL service or installed browser stayed unchanged meanwhile.
  const suppliedEnvironment = typeof environment === 'function' ? await environment() : environment;
  assert.ok(suppliedEnvironment && Object.getPrototypeOf(suppliedEnvironment) === Object.prototype,
    'Verification environment provider must return a plain object.');
  const values = { ...Object.fromEntries(Object.entries(process.env).filter(([key]) => autoEnvironment.test(key))), ...suppliedEnvironment };
  const environmentHashes = Object.fromEntries(Object.entries(values).sort(([left], [right]) => left.localeCompare(right))
    .map(([key, value]) => [key, sha256(JSON.stringify(value === undefined ? { unset: true } : { value: canonical(value) }))]));
  const evidence = canonical({ schema, root, repository: { files: repositoryFiles }, artifacts, dependencies,
    environment: environmentHashes, runtime: { node: process.versions, platform: process.platform, arch: process.arch },
    documentationAllowance: [...allowed].filter(path => !tested.has(path)).sort(), testedDocs: [...tested].sort() });
  return { ...evidence, digest: sha256(JSON.stringify(evidence)) };
}

export async function assertUnchanged(before, options) {
  assert.ok(before && before.schema === schema && /^[a-f0-9]{64}$/.test(before.digest), 'A valid previous verification snapshot is required.');
  const { digest: previousDigest, ...previousEvidence } = before;
  assert.equal(sha256(JSON.stringify(canonical(previousEvidence))), previousDigest, 'The prior verification manifest was modified.');
  const after = await snapshotVerificationInputs(options);
  if (after.digest !== before.digest) {
    const changed = ['repository', 'artifacts', 'dependencies', 'environment', 'runtime', 'documentationAllowance', 'testedDocs', 'root']
      .filter(key => JSON.stringify(after[key]) !== JSON.stringify(before[key]));
    throw new Error('Verification inputs changed; previous full-gate evidence cannot be reused. Changed categories: ' + changed.join(', '));
  }
  return after;
}
