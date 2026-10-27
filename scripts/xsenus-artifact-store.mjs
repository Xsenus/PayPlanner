import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { constants, appendFileSync, closeSync, copyFileSync, existsSync, fstatSync, lstatSync,
  mkdirSync, openSync, readFileSync, readdirSync, readSync, renameSync, statfsSync, writeFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, parse, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { requireTrustedHost } from './verify-xsenus-runner.mjs';

export const artifactPrefix = 'xsenus-local-v1:';
export const reserveBytes = 20n * 1024n ** 3n;
const idPattern = /^xsenus-local-v1:[a-f0-9]{64}$/;
const shaPattern = /^[a-f0-9]{64}$/;
const maxEntries = 100_000;
const maxReceiptBytes = 64 * 1024 ** 2;
const packages = { frontend: 'frontend.tar.gz', backend: 'backend.tar.gz', 'test-runtime': 'test-runtime.tar.gz' };
const kinds = [...Object.keys(packages), 'shard-diagnostics', 'qualified-shard', 'restored-diagnostics'];
const jobs = ['frontend', 'backend-build', 'backend-format', 'backend-browser', 'backend', 'deploy', ...[0, 1, 2, 3].map(n => `backend-test-${n}`)];
const receiptKeys = ['schema', 'repository', 'workflowRef', 'workflowSha', 'runId', 'producerAttempt', 'headSha', 'ref', 'event',
  'producerJob', 'runnerName', 'host', 'kind', 'shard', 'manifestSha', 'sdkVersion', 'runtimeArtifactId', 'diagnosticArtifactId',
  'retentionDays', 'publishedUtc', 'directories', 'files', 'totalBytes'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;

export function canonicalBytes(value) {
  const sorted = item => Array.isArray(item) ? item.map(sorted)
    : item !== null && typeof item === 'object' ? Object.fromEntries(Object.keys(item).sort(compare).map(key => [key, sorted(item[key])])) : item;
  return Buffer.from(JSON.stringify(sorted(value)) + '\n');
}

function exactKeys(value, keys, label) {
  assert.ok(value && !Array.isArray(value) && typeof value === 'object', `${label} must be an object`);
  assert.deepEqual(Object.keys(value).sort(compare), [...keys].sort(compare), `${label} has unexpected fields`);
}

export function safeRelativePath(name) {
  assert.ok(typeof name === 'string' && name.length > 0 && name.length <= 4096, 'Invalid inventory path');
  assert.ok(!/[\\:\x00-\x1f\x7f]/.test(name) && !name.startsWith('/'), 'Absolute, ADS or control-character path');
  for (const part of name.split('/')) {
    assert.ok(part && part !== '.' && part !== '..' && !/[. ]$/.test(part), 'Unsafe path component');
    assert.ok(!/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part), 'Windows device path');
    assert.ok(!/[<>"|?*]/.test(part), 'Invalid Windows path component');
  }
  return name;
}

function inside(root, target, equal = false) {
  const rel = relative(resolve(root), resolve(target));
  return (equal && !rel) || Boolean(rel && rel !== '..' && !rel.startsWith('..' + sep) && !isAbsolute(rel));
}

function plainPath(path) {
  assert.ok(typeof path === 'string' && isAbsolute(path) && !/[\x00-\x1f\x7f]/.test(path), 'An ordinary absolute path is required');
  assert.ok(!path.startsWith('\\\\') && !path.startsWith('//'), 'UNC/device paths are not supported');
  const suffix = path.slice(parse(path).root.length);
  assert.ok(!suffix.includes(':') && suffix.split(/[\\/]/).every(part => part !== '..' && part !== '.' && !/[. ]$/.test(part)), 'Unsafe absolute path');
  return resolve(path);
}

function ancestors(path) {
  const paths = [];
  for (let current = resolve(path);; current = dirname(current)) {
    paths.push(current);
    if (dirname(current) === current) return paths.reverse();
  }
}

function ordinary(path, directory) {
  const info = lstatSync(path, { bigint: true });
  assert.ok(!info.isSymbolicLink() && (directory ? info.isDirectory() : info.isFile()), 'Nonregular/reparse artifact path');
  if (!directory) assert.equal(info.nlink, 1n, 'Hard-linked artifact file');
  return info;
}

function fileDigest(path) {
  const before = ordinary(path, false);
  const fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  const hashState = createHash('sha256');
  try {
    const opened = fstatSync(fd, { bigint: true });
    assert.equal(opened.ino, before.ino, 'File replaced while opening');
    assert.equal(opened.dev, before.dev, 'File device changed');
    const buffer = Buffer.allocUnsafe(1024 * 1024);
    for (let length; (length = readSync(fd, buffer)) > 0;) hashState.update(buffer.subarray(0, length));
    const after = fstatSync(fd, { bigint: true });
    assert.deepEqual([after.size, after.mtimeNs, after.ctimeNs, after.nlink], [before.size, before.mtimeNs, before.ctimeNs, 1n], 'File changed while hashing');
  } finally { closeSync(fd); }
  const final = ordinary(path, false);
  assert.deepEqual([final.dev, final.ino, final.size, final.mtimeNs, final.ctimeNs], [before.dev, before.ino, before.size, before.mtimeNs, before.ctimeNs], 'File replaced while hashing');
  assert.ok(before.size <= BigInt(Number.MAX_SAFE_INTEGER), 'Artifact file is too large');
  return { bytes: Number(before.size), sha256: hashState.digest('hex') };
}

// The CLI uses this read-only native adapter. Tests supply their own explicit
// platform adapter to exported functions; no command-line switch bypasses it.
export function windowsAdapter(env = process.env) {
  return {
    inspect(paths) {
      const script = String.raw`$ErrorActionPreference='Stop'
$inputPaths = ConvertFrom-Json ([Console]::In.ReadToEnd())
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$items = @($inputPaths | ForEach-Object {
  $path = [string]$_
  $attributes = [IO.File]::GetAttributes($path)
  $directory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
  $entry = if ($directory) { New-Object IO.DirectoryInfo($path) } else { New-Object IO.FileInfo($path) }
  $acl = $entry.GetAccessControl()
  $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | ForEach-Object {
    [pscustomobject]@{sid=$_.IdentityReference.Value;rights=[long]$_.FileSystemRights;allow=($_.AccessControlType -eq 0);inheritOnly=(($_.PropagationFlags -band 2) -ne 0)}
  })
  $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($path))
  $streams = @()
  if (-not $directory) { $streams = @(Get-Item -LiteralPath $path -Stream * | ForEach-Object { [string]$_.Stream }) }
  [pscustomobject]@{path=$path;owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value;reparse=(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0);directory=$directory;format=$drive.DriveFormat;fixed=($drive.DriveType -eq 3);streams=$streams;rules=$rules}
})
[pscustomobject]@{sid=$sid;items=$items} | ConvertTo-Json -Depth 6 -Compress`;
      const result = spawnSync(join(env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe'),
        ['-NoProfile', '-NonInteractive', '-Command', script], { input: JSON.stringify(paths), encoding: 'utf8', windowsHide: true, timeout: 60_000, maxBuffer: maxReceiptBytes });
      assert.equal(result.status, 0, 'Native Windows filesystem/ACL inspection failed');
      return JSON.parse(result.stdout.replace(/^\uFEFF/, ''));
    },
    freeBytes(path) { const info = statfsSync(path, { bigint: true }); return info.bavail * info.bsize; },
    copyFile(source, destination) { copyFileSync(source, destination, constants.COPYFILE_EXCL); },
    publish(source, destination) { renameSync(source, destination); },
    now() { return new Date().toISOString(); },
  };
}

export function validateInspection(report, paths, storeRoot) {
  assert.match(report?.sid ?? '', /^S-1-\d+(?:-\d+)+$/, 'Missing native runner SID');
  assert.ok(Array.isArray(report.items) && report.items.length === paths.length, 'Incomplete native path inspection');
  const allowed = new Set([report.sid, 'S-1-5-18', 'S-1-5-32-544']);
  const writeMask = 2 | 4 | 16 | 64 | 256 | 65536 | 262144 | 524288;
  const replaceMask = 64 | 65536 | 262144 | 524288;
  const seen = new Set();
  for (const item of report.items) {
    assert.ok(paths.includes(item.path) && !seen.has(item.path), 'Native inspection path differs'); seen.add(item.path);
    assert.equal(item.reparse, false, 'Reparse point is forbidden');
    assert.ok(item.fixed && ['NTFS', 'ReFS'].includes(item.format), 'A fixed local NTFS/ReFS volume is required');
    assert.equal(typeof item.directory, 'boolean', 'Missing native file type');
    // CopyFile preserves named streams; they are outside the ordinary file SHA.
    // Directories are created anew, never copied with their alternate streams.
    assert.deepEqual(item.streams, item.directory ? [] : [':$DATA'], 'Named/missing NTFS data stream inventory');
    const protectedPath = inside(storeRoot, item.path, true);
    const storeAncestor = inside(item.path, storeRoot);
    if (!protectedPath && !storeAncestor) continue;
    assert.ok(allowed.has(item.owner), 'Store/ancestor owner is not the runner, SYSTEM or Administrators');
    assert.ok(Array.isArray(item.rules) && item.rules.length > 0, 'Missing native ACL');
    // DELETE applies to the object itself; FILE_DELETE_CHILD applies to its
    // children. A fixed volume root cannot be renamed/deleted as a directory.
    // Keep every other replacement permission, and DELETE on ordinary ancestors.
    const ancestorMask = item.directory && item.path === parse(item.path).root ? replaceMask & ~65536 : replaceMask;
    for (const rule of item.rules) {
      assert.ok(typeof rule.allow === 'boolean' && typeof rule.inheritOnly === 'boolean' && Number.isSafeInteger(rule.rights), 'Malformed native ACL rule');
      if (rule.allow && !rule.inheritOnly && !allowed.has(rule.sid)) {
        assert.equal(rule.rights & (protectedPath ? writeMask : ancestorMask), 0, 'Broad write/delete/ownership access to artifact store');
      }
    }
  }
}

export function contextFromEnvironment(env, platform = process.platform) {
  requireTrustedHost(env, platform);
  assert.equal(env.GITHUB_ACTIONS, 'true', 'A GitHub Actions context is required');
  assert.equal(env.RUNNER_NAME, 'PayPlanner-XSENUS', 'Unexpected runner identity');
  assert.equal(env.RUNNER_OS, 'Windows'); assert.equal(env.RUNNER_ARCH, 'X64');
  assert.match(env.GITHUB_RUN_ID ?? '', /^[1-9][0-9]{0,19}$/);
  assert.match(env.GITHUB_RUN_ATTEMPT ?? '', /^[1-9][0-9]{0,8}$/);
  assert.match(env.GITHUB_SHA ?? '', /^[a-f0-9]{40}$/);
  assert.match(env.GITHUB_WORKFLOW_SHA ?? '', /^[a-f0-9]{40}$/);
  assert.ok(jobs.includes(env.GITHUB_JOB), 'Unknown producer/consumer job');
  const rawNeeds = env.ARTIFACT_NEEDS_JSON || '{}';
  assert.ok(Buffer.byteLength(rawNeeds) <= 256 * 1024, 'Oversized needs context');
  const needs = JSON.parse(rawNeeds);
  assert.ok(needs && !Array.isArray(needs) && typeof needs === 'object', 'Invalid needs context');
  return { repository: env.GITHUB_REPOSITORY, workflowRef: env.GITHUB_WORKFLOW_REF, workflowSha: env.GITHUB_WORKFLOW_SHA,
    runId: env.GITHUB_RUN_ID, producerAttempt: Number(env.GITHUB_RUN_ATTEMPT), headSha: env.GITHUB_SHA,
    ref: env.GITHUB_REF, event: env.GITHUB_EVENT_NAME, producerJob: env.GITHUB_JOB, runnerName: env.RUNNER_NAME,
    host: env.COMPUTERNAME.toUpperCase(), needs };
}

function need(context, job, output, expected) {
  const entry = context.needs[job];
  assert.ok(entry && entry.result === 'success' && entry.outputs && typeof entry.outputs === 'object', `Missing successful needs producer: ${job}`);
  if (output) assert.equal(entry.outputs[output], expected, `Trusted needs output differs: ${job}.${output}`);
}

function metadata(kind, env, shard) {
  const result = { manifestSha: null, sdkVersion: null, runtimeArtifactId: null, diagnosticArtifactId: null };
  if (kind !== 'frontend') {
    assert.match(env.EXPECTED_MANIFEST_SHA ?? '', /^[a-fA-F0-9]{64}$/, 'Missing expected manifest SHA');
    assert.match(env.EXPECTED_SDK_VERSION ?? '', /^9\.0\.[1-9][0-9]*$/, 'Missing exact .NET9 SDK version');
    result.manifestSha = env.EXPECTED_MANIFEST_SHA; result.sdkVersion = env.EXPECTED_SDK_VERSION;
  }
  if (['shard-diagnostics', 'qualified-shard', 'restored-diagnostics'].includes(kind)) {
    assert.match(env.EXPECTED_RUNTIME_ARTIFACT_ID ?? '', idPattern, 'Missing exact runtime artifact ID');
    result.runtimeArtifactId = env.EXPECTED_RUNTIME_ARTIFACT_ID;
  }
  if (kind === 'qualified-shard' && env.EXPECTED_DIAGNOSTIC_ARTIFACT_ID) {
    assert.match(env.EXPECTED_DIAGNOSTIC_ARTIFACT_ID, idPattern); result.diagnosticArtifactId = env.EXPECTED_DIAGNOSTIC_ARTIFACT_ID;
  }
  const sharded = kind === 'shard-diagnostics' || kind === 'qualified-shard';
  assert.ok(sharded ? Number.isInteger(shard) && shard >= 0 && shard <= 3 : shard === null, 'Kind/shard mismatch');
  return result;
}

function producer(kind, shard) {
  if (kind === 'frontend') return 'frontend';
  if (kind === 'backend' || kind === 'test-runtime') return 'backend-build';
  if (kind === 'restored-diagnostics') return 'backend-browser';
  return `backend-test-${shard}`;
}

function anchorMetadata(context, meta, job = 'backend-build') {
  need(context, job, 'manifest-sha', meta.manifestSha);
  need(context, job, 'sdk-version', meta.sdkVersion);
  if (meta.runtimeArtifactId) need(context, job, 'runtime-artifact-id', meta.runtimeArtifactId);
}

function validateInventory(receipt) {
  assert.ok(Array.isArray(receipt.files) && receipt.files.length > 0 && receipt.files.length <= maxEntries, 'Empty/oversized payload inventory');
  assert.ok(Array.isArray(receipt.directories) && receipt.directories.length <= maxEntries, 'Invalid directory inventory');
  const names = new Set();
  let total = 0;
  for (const path of receipt.directories) {
    safeRelativePath(path); assert.ok(!names.has(path.toLowerCase()), 'Case-colliding inventory'); names.add(path.toLowerCase());
  }
  for (const row of receipt.files) {
    exactKeys(row, ['path', 'bytes', 'sha256'], 'Inventory row'); safeRelativePath(row.path);
    assert.ok(!names.has(row.path.toLowerCase()), 'Duplicate/case-colliding inventory'); names.add(row.path.toLowerCase());
    assert.ok(Number.isSafeInteger(row.bytes) && row.bytes >= 0, 'Invalid file size'); assert.match(row.sha256, shaPattern);
    total += row.bytes;
  }
  assert.ok(Number.isSafeInteger(total) && total > 0 && total === receipt.totalBytes, 'Invalid/empty payload total');
  assert.deepEqual(receipt.directories, [...receipt.directories].sort(compare), 'Unsorted directories');
  assert.deepEqual(receipt.files.map(row => row.path), receipt.files.map(row => row.path).sort(compare), 'Unsorted files');
  for (const name of [...receipt.directories, ...receipt.files.map(row => row.path)]) {
    const parent = name.split('/').slice(0, -1).join('/');
    if (parent) assert.ok(receipt.directories.includes(parent), 'Missing inventory parent');
  }
  if (packages[receipt.kind]) {
    assert.deepEqual(receipt.directories, []);
    assert.deepEqual(receipt.files.map(row => row.path), [packages[receipt.kind]], 'Package must contain its one exact archive');
  }
}

export function createArtifactStore({ env = process.env, platform = process.platform, adapter = windowsAdapter(env) } = {}) {
  const context = contextFromEnvironment(env, platform);
  const root = plainPath(env.PAYPLANNER_CI_ARTIFACT_ROOT);
  const workspace = plainPath(env.GITHUB_WORKSPACE);
  const runnerTemp = plainPath(env.RUNNER_TEMP);
  assert.ok(!inside(root, workspace, true) && !inside(workspace, root, true)
    && !inside(root, runnerTemp, true) && !inside(runnerTemp, root, true), 'Store must be external to checkout and runner temporary directory');
  const inspect = paths => {
    paths = [...new Set(paths.map(path => resolve(path)))];
    for (const path of paths) {
      const info = lstatSync(path); assert.ok(!info.isSymbolicLink() && (info.isDirectory() || info.isFile()), 'Nonregular artifact path');
    }
    validateInspection(adapter.inspect(paths), paths, root);
  };
  const pathChain = path => inspect(ancestors(path));
  function disk(path, extra = 0) {
    assert.ok(adapter.freeBytes(path) >= reserveBytes + BigInt(extra), 'Artifact operation must retain 20GiB free disk space');
  }
  function check() { ordinary(root, true); inspect([...ancestors(root), ...ancestors(workspace), ...ancestors(runnerTemp)]); disk(root); return { root, reserveBytes: reserveBytes.toString() }; }
  function ensureDirectory(path) {
    if (!existsSync(path)) { pathChain(dirname(path)); mkdirSync(path); }
    ordinary(path, true); pathChain(path);
  }
  function tree(directory) {
    ordinary(directory, true); pathChain(directory);
    const files = [], directories = [], paths = [directory];
    const visit = current => {
      for (const name of readdirSync(current)) {
        const path = join(current, name), rel = relative(directory, path).split(sep).join('/'); safeRelativePath(rel);
        const info = lstatSync(path); assert.ok(!info.isSymbolicLink(), 'Linked payload entry'); paths.push(path);
        if (info.isDirectory()) { directories.push(rel); visit(path); }
        else { ordinary(path, false); files.push({ path: rel, ...fileDigest(path) }); }
        assert.ok(files.length + directories.length <= maxEntries, 'Oversized payload inventory');
      }
    };
    visit(directory); inspect(paths);
    files.sort((a, b) => compare(a.path, b.path)); directories.sort(compare);
    return { files, directories, totalBytes: files.reduce((total, row) => total + row.bytes, 0) };
  }
  function sourceInventory(source, kind) {
    if (!packages[kind]) return tree(source);
    pathChain(source); assert.equal(basename(source), packages[kind], 'Unexpected package source name');
    const file = { path: basename(source), ...fileDigest(source) };
    return { directories: [], files: [file], totalBytes: file.bytes };
  }
  function workPath(value) {
    assert.ok(typeof value === 'string' && value && !/[\x00-\x1f\x7f]/.test(value), 'Missing workspace path');
    assert.ok(!value.split(/[\\/]/).includes('..'), 'Parent traversal in workspace path');
    const path = plainPath(isAbsolute(value) ? value : resolve(workspace, value));
    assert.ok(inside(workspace, path), 'Artifact input/output must stay inside checkout');
    return path;
  }
  function copy(source, destination, inventory, single = false) {
    ordinary(destination, true);
    const directories = inventory.directories.map(name => join(destination, ...name.split('/')));
    for (const path of directories) { ordinary(dirname(path), true); mkdirSync(path); ordinary(path, true); }
    // Batch native ACL/reparse inspection: one PowerShell process per tree,
    // rather than per file. Exclusive copies and final full readback remain.
    inspect([...ancestors(source), ...ancestors(destination), ...directories,
      ...inventory.files.map(row => single ? source : join(source, ...row.path.split('/')))]);
    for (const row of inventory.files) {
      disk(destination, row.bytes);
      const from = single ? source : join(source, ...row.path.split('/'));
      const to = join(destination, ...row.path.split('/'));
      ordinary(from, false); ordinary(dirname(to), true);
      assert.ok(!existsSync(to), 'Copy destination already exists'); adapter.copyFile(from, to);
      assert.deepEqual(fileDigest(to), { bytes: row.bytes, sha256: row.sha256 }, 'Copied payload differs');
    }
    assert.deepEqual(tree(destination), inventory, 'Copied tree differs');
  }
  function objectPath(id) {
    assert.match(id, idPattern, 'Expected a typed receipt SHA artifact ID');
    const digest = id.slice(artifactPrefix.length);
    return join(root, 'v1/objects', digest.slice(0, 2), digest);
  }
  function readObject(id, kind, producerJob, shard, meta, currentAttempt = false) {
    const object = objectPath(id); ordinary(object, true); pathChain(object);
    assert.deepEqual(readdirSync(object).sort(compare), ['payload', 'receipt.json'], 'Unexpected object files');
    const receiptPath = join(object, 'receipt.json'); pathChain(receiptPath);
    assert.ok(ordinary(receiptPath, false).size <= BigInt(maxReceiptBytes), 'Oversized receipt');
    const bytes = readFileSync(receiptPath);
    assert.equal(hash(bytes), id.slice(artifactPrefix.length), 'Receipt digest differs from trusted ID');
    const receipt = JSON.parse(bytes.toString('utf8')); exactKeys(receipt, receiptKeys, 'Receipt');
    assert.ok(canonicalBytes(receipt).equals(bytes), 'Noncanonical receipt'); assert.equal(receipt.schema, 1);
    for (const key of ['repository', 'workflowRef', 'workflowSha', 'runId', 'headSha', 'ref', 'event', 'runnerName', 'host']) assert.equal(receipt[key], context[key], `Wrong artifact ${key}`);
    assert.ok(Number.isInteger(receipt.producerAttempt) && receipt.producerAttempt > 0 && receipt.producerAttempt <= context.producerAttempt, 'Future/invalid producer attempt');
    if (currentAttempt) assert.equal(receipt.producerAttempt, context.producerAttempt, 'Diagnostics must come from this attempt');
    assert.equal(receipt.kind, kind, 'Wrong artifact kind'); assert.equal(receipt.producerJob, producerJob, 'Wrong artifact producer');
    assert.equal(producerJob, producer(kind, shard), 'Kind/producer role differs'); assert.equal(receipt.shard, shard, 'Wrong artifact shard');
    for (const key of ['manifestSha', 'sdkVersion', 'runtimeArtifactId']) assert.equal(receipt[key], meta[key], `Wrong artifact ${key}`);
    if (kind === 'qualified-shard') assert.match(receipt.diagnosticArtifactId ?? '', idPattern);
    else assert.equal(receipt.diagnosticArtifactId, null);
    assert.equal(receipt.retentionDays, packages[kind] ? 3 : 14, 'Wrong retention class');
    assert.ok(typeof receipt.publishedUtc === 'string' && new Date(receipt.publishedUtc).toISOString() === receipt.publishedUtc, 'Invalid publication timestamp');
    validateInventory(receipt);
    const inventory = { files: receipt.files, directories: receipt.directories, totalBytes: receipt.totalBytes };
    assert.deepEqual(tree(join(object, 'payload')), inventory, 'Stored payload/inventory mismatch');
    if (kind === 'qualified-shard') {
      const diagnostics = readObject(receipt.diagnosticArtifactId, 'shard-diagnostics', producerJob, shard, meta);
      assert.equal(diagnostics.receipt.producerAttempt, receipt.producerAttempt, 'Qualified/diagnostic attempts differ');
      assert.deepEqual(diagnostics.inventory, inventory, 'Qualified/diagnostic inventories differ');
    }
    return { receipt, inventory, payload: join(object, 'payload') };
  }
  function put({ kind, source: rawSource, retentionDays }) {
    check(); assert.ok(kinds.includes(kind), 'Unknown artifact kind');
    const shard = ['shard-diagnostics', 'qualified-shard'].includes(kind) ? Number(env.ARTIFACT_SHARD) : null;
    if (shard !== null) assert.match(env.ARTIFACT_SHARD ?? '', /^[0-3]$/);
    assert.equal(context.producerJob, producer(kind, shard), 'Invalid producer role');
    assert.equal(retentionDays, packages[kind] ? 3 : 14, 'Retention must match the exact artifact class');
    const meta = metadata(kind, env, shard);
    if (meta.runtimeArtifactId) {
      anchorMetadata(context, meta); need(context, 'frontend'); need(context, 'backend-format');
      readObject(meta.runtimeArtifactId, 'test-runtime', 'backend-build', null, { ...meta, runtimeArtifactId: null });
    }
    const source = workPath(rawSource), inventory = sourceInventory(source, kind);
    validateInventory({ ...inventory, kind }); disk(root, inventory.totalBytes + maxReceiptBytes);
    if (kind === 'qualified-shard') {
      assert.match(meta.diagnosticArtifactId ?? '', idPattern, 'Persisted diagnostics are required before qualification');
      const diagnostics = readObject(meta.diagnosticArtifactId, 'shard-diagnostics', context.producerJob, shard, meta, true);
      assert.deepEqual(diagnostics.inventory, inventory, 'Qualified payload must exactly match persisted diagnostics');
    }
    const identity = Object.fromEntries(Object.entries(context).filter(([key]) => key !== 'needs'));
    const receipt = { schema: 1, ...identity, kind, shard, ...meta, retentionDays, publishedUtc: adapter.now(), ...inventory };
    const bytes = canonicalBytes(receipt); assert.ok(bytes.length <= maxReceiptBytes, 'Oversized receipt');
    const id = artifactPrefix + hash(bytes), target = objectPath(id);
    for (const path of [join(root, 'v1'), join(root, 'v1/objects'), dirname(target)]) ensureDirectory(path);
    if (existsSync(target)) {
      const existing = readObject(id, kind, context.producerJob, shard, meta, true);
      assert.deepEqual(existing.receipt, receipt, 'Existing object differs');
      return { id, outputKey: kind === 'qualified-shard' ? `shard_${shard}_id` : 'artifact-id', receipt };
    }
    const staging = join(dirname(target), '.staging-' + randomUUID()); mkdirSync(staging); pathChain(staging);
    mkdirSync(join(staging, 'payload')); copy(source, join(staging, 'payload'), inventory, Boolean(packages[kind]));
    assert.deepEqual(sourceInventory(source, kind), inventory, 'Source changed during publication');
    writeFileSync(join(staging, 'receipt.json'), bytes, { flag: 'wx' });
    assert.ok(readFileSync(join(staging, 'receipt.json')).equals(bytes), 'Receipt write/readback failed');
    pathChain(staging); pathChain(dirname(target)); disk(root);
    try { adapter.publish(staging, target); }
    catch (error) { if (!existsSync(target)) throw error; readObject(id, kind, context.producerJob, shard, meta, true); }
    const committed = readObject(id, kind, context.producerJob, shard, meta, true);
    assert.deepEqual(committed.receipt, receipt, 'Published receipt changed');
    return { id, outputKey: kind === 'qualified-shard' ? `shard_${shard}_id` : 'artifact-id', receipt };
  }
  function get({ kind, producerJob, shard = null, destination: rawDestination, mergeDisjoint = false }) {
    check(); assert.ok(kinds.includes(kind), 'Unknown artifact kind');
    assert.equal(producerJob, producer(kind, shard), 'Unexpected producer job');
    const id = env.EXPECTED_ARTIFACT_ID; assert.match(id ?? '', idPattern);
    const meta = metadata(kind, env, shard);
    if (kind === 'frontend' || kind === 'backend') {
      assert.equal(context.producerJob, 'deploy', 'Packages may only be consumed by deploy');
      need(context, kind === 'backend' ? 'backend' : 'frontend', `${kind}-artifact-id`, id);
      if (kind === 'backend') anchorMetadata(context, meta, 'backend');
    } else if (kind === 'test-runtime') {
      assert.ok(['backend', 'backend-browser', ...[0, 1, 2, 3].map(n => `backend-test-${n}`)].includes(context.producerJob), 'Invalid runtime consumer');
      need(context, 'backend-build', 'runtime-artifact-id', id); anchorMetadata(context, meta);
    } else {
      assert.equal(context.producerJob, 'backend', 'Result evidence may only be consumed by aggregator');
      need(context, producerJob, kind === 'qualified-shard' ? 'qualified-artifact-id' : 'diagnostics-artifact-id', id);
      anchorMetadata(context, meta);
    }
    const object = readObject(id, kind, producerJob, shard, meta);
    const destination = workPath(rawDestination); pathChain(dirname(destination));
    const before = existsSync(destination) ? tree(destination) : null;
    assert.ok(!before || mergeDisjoint, 'Destination already exists');
    if (before) {
      const occupied = new Set([...before.directories, ...before.files.map(row => row.path)].map(name => name.toLowerCase()));
      for (const row of object.inventory.files) assert.ok(!occupied.has(row.path.toLowerCase()), 'Merged file overlaps existing destination');
      for (const name of object.inventory.directories) {
        assert.ok(!before.files.some(row => row.path.toLowerCase() === name.toLowerCase()), 'Merged directory overlaps existing file');
        assert.ok(!before.directories.some(prior => prior.toLowerCase() === name.toLowerCase() && prior !== name), 'Merged directories collide by case');
      }
    }
    disk(dirname(destination), object.inventory.totalBytes * (before ? 2 : 1));
    const staging = join(dirname(destination), '.artifact-get-' + randomUUID()); mkdirSync(staging);
    copy(object.payload, staging, object.inventory);
    readObject(id, kind, producerJob, shard, meta); // Verify persisted source again after copying.
    if (!before) {
      assert.ok(!existsSync(destination), 'Destination appeared during retrieval'); adapter.publish(staging, destination);
      assert.deepEqual(tree(destination), object.inventory, 'Promoted retrieval differs');
    } else {
      assert.deepEqual(tree(destination), before, 'Merge destination changed during retrieval');
      const directories = object.inventory.directories.map(name => join(destination, ...name.split('/')));
      for (const path of directories) { ordinary(dirname(path), true); if (!existsSync(path)) mkdirSync(path); ordinary(path, true); }
      inspect([...ancestors(destination), ...directories]);
      // Each file is exclusive-create. Failed merges never qualify; existing
      // evidence is never overwritten or removed, including on partial failure.
      for (const row of object.inventory.files) {
        const target = join(destination, ...row.path.split('/')); ordinary(dirname(target), true);
        disk(destination, row.bytes);
        assert.ok(!existsSync(target), 'Merge destination file appeared'); adapter.copyFile(join(staging, ...row.path.split('/')), target);
      }
      const expected = { directories: [...new Set([...before.directories, ...object.inventory.directories])].sort(compare),
        files: [...before.files, ...object.inventory.files].sort((a, b) => compare(a.path, b.path)), totalBytes: before.totalBytes + object.inventory.totalBytes };
      assert.deepEqual(tree(destination), expected, 'Merged retrieval differs');
    }
    disk(dirname(destination)); return { id, destination, receipt: object.receipt };
  }
  return { check, put, get };
}

export function parseArguments(args) {
  const [mode, ...rest] = args; assert.ok(['check', 'put', 'get'].includes(mode), 'Use check, put or get');
  const options = {};
  for (let index = 0; index < rest.length; index++) {
    const flag = rest[index]; assert.ok(flag.startsWith('--') && !Object.hasOwn(options, flag), 'Unknown/duplicate argument');
    if (flag === '--merge-disjoint') options[flag] = true;
    else { assert.ok(rest[index + 1] && !rest[index + 1].startsWith('--'), 'Missing argument value'); options[flag] = rest[++index]; }
  }
  const required = mode === 'put' ? ['--kind', '--source', '--retention-days'] : mode === 'get' ? ['--kind', '--producer-job', '--destination'] : [];
  const allowed = mode === 'get' ? [...required, '--shard', '--merge-disjoint'] : required;
  assert.ok(required.every(key => Object.hasOwn(options, key)) && Object.keys(options).every(key => allowed.includes(key)), 'Unexpected/missing command arguments');
  if (options['--retention-days']) assert.match(options['--retention-days'], /^(3|14)$/);
  if (options['--shard']) assert.match(options['--shard'], /^[0-3]$/);
  return { mode, options };
}

export function runCommand(args, settings = {}) {
  const { mode, options } = parseArguments(args), env = settings.env ?? process.env;
  const adapter = settings.adapter ?? windowsAdapter(env);
  const store = createArtifactStore({ ...settings, env, adapter });
  if (mode === 'check') return store.check();
  if (mode === 'get') return store.get({ kind: options['--kind'], producerJob: options['--producer-job'],
    shard: options['--shard'] === undefined ? null : Number(options['--shard']), destination: options['--destination'], mergeDisjoint: options['--merge-disjoint'] === true });
  const output = plainPath(env.GITHUB_OUTPUT);
  assert.ok(inside(plainPath(env.RUNNER_TEMP), output), 'GitHub output must be inside runner temporary directory');
  const outputPaths = ancestors(output);
  validateInspection(adapter.inspect(outputPaths), outputPaths, plainPath(env.PAYPLANNER_CI_ARTIFACT_ROOT));
  const info = ordinary(output, false);
  const result = store.put({ kind: options['--kind'], source: options['--source'], retentionDays: Number(options['--retention-days']) });
  const after = ordinary(output, false); assert.equal(after.dev, info.dev); assert.equal(after.ino, info.ino);
  validateInspection(adapter.inspect(outputPaths), outputPaths, plainPath(env.PAYPLANNER_CI_ARTIFACT_ROOT));
  appendFileSync(output, `${result.outputKey}=${result.id}\n`);
  return { id: result.id, outputKey: result.outputKey };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { console.log(JSON.stringify(runCommand(process.argv.slice(2)))); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
