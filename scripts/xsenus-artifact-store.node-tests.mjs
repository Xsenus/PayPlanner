import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { constants, copyFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync,
  renameSync, rmSync, symlinkSync, linkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, parse, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { artifactPrefix, canonicalBytes, contextFromEnvironment, createArtifactStore, parseArguments,
  reserveBytes, runCommand, safeRelativePath, validateInspection } from './xsenus-artifact-store.mjs';

const code = 'a'.repeat(40), manifest = 'b'.repeat(64), sdk = '9.0.319';
const runnerSid = 'S-1-5-21-101-202-303-1001';
const digest = data => createHash('sha256').update(data).digest('hex');
const success = outputs => ({ result: 'success', outputs });

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-artifact-store-'));
  t.after(() => {
    assert.equal(dirname(resolve(root)), resolve(tmpdir()));
    assert.ok(basename(root).startsWith('payplanner-artifact-store-'));
    rmSync(root, { recursive: true, force: true });
  });
  const storeRoot = join(root, 'store'), workspace = join(root, 'workspace'), runnerTemp = join(root, 'runner-temp');
  for (const path of [storeRoot, workspace, runnerTemp]) mkdirSync(path);
  const output = join(runnerTemp, 'github-output'); writeFileSync(output, '');
  const baseEnv = { GITHUB_ACTIONS: 'true', COMPUTERNAME: 'XSENUS', RUNNER_NAME: 'PayPlanner-XSENUS', RUNNER_OS: 'Windows', RUNNER_ARCH: 'X64',
    GITHUB_REPOSITORY: 'Xsenus/PayPlanner', GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
    GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main', GITHUB_WORKFLOW_SHA: code,
    GITHUB_RUN_ID: '123456789', GITHUB_RUN_ATTEMPT: '1', GITHUB_SHA: code, GITHUB_JOB: 'frontend',
    GITHUB_WORKSPACE: workspace, RUNNER_TEMP: runnerTemp, PAYPLANNER_CI_ARTIFACT_ROOT: storeRoot, GITHUB_OUTPUT: output,
    EXPECTED_MANIFEST_SHA: manifest, EXPECTED_SDK_VERSION: sdk, ARTIFACT_NEEDS_JSON: '{}' };
  const adapter = {
    inspect(paths) { return { sid: runnerSid, items: paths.map(path => ({ path, owner: runnerSid, reparse: lstatSync(path).isSymbolicLink(),
      directory: lstatSync(path).isDirectory(), format: 'NTFS', fixed: true,
      streams: lstatSync(path).isDirectory() ? [] : [':$DATA'],
      rules: [{ sid: runnerSid, rights: 2032127, allow: true, inheritOnly: false }] })) }; },
    freeBytes() { return reserveBytes + 1024n ** 3n; },
    copyFile(from, to) { copyFileSync(from, to, constants.COPYFILE_EXCL); },
    publish(from, to) { renameSync(from, to); },
    now() { return '2026-10-26T00:00:00.000Z'; },
  };
  const options = (overrides = {}, changes = {}) => ({ env: { ...baseEnv, ...overrides }, platform: 'win32', adapter: { ...adapter, ...changes } });
  const store = (overrides, changes) => createArtifactStore(options(overrides, changes));
  const write = (name, bytes = 'fixture payload') => { const path = join(workspace, name); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes); return path; };
  const object = id => join(storeRoot, 'v1/objects', id.slice(artifactPrefix.length, artifactPrefix.length + 2), id.slice(artifactPrefix.length));
  function publish(kind = 'frontend', overrides = {}, changes = {}) {
    const name = kind === 'test-runtime' ? 'test-runtime.tar.gz' : `${kind}.tar.gz`;
    if (!existsSync(join(workspace, name))) write(name);
    return store({ GITHUB_JOB: kind === 'frontend' ? 'frontend' : 'backend-build', ...overrides }, changes)
      .put({ kind, source: name, retentionDays: 3 });
  }
  const buildNeeds = runtime => ({ 'backend-build': success({ 'runtime-artifact-id': runtime.id, 'manifest-sha': manifest, 'sdk-version': sdk }),
    frontend: success({}), 'backend-format': success({}) });
  function shard(runtime, n = 0, overrides = {}, changes = {}) {
    write(`results-${n}/shard-${n}/result.trx`, `case ${n}: passed`);
    write(`results-${n}/shard-${n}.log`, '');
    mkdirSync(join(workspace, `results-${n}/shard-${n}/empty`));
    const env = { GITHUB_JOB: `backend-test-${n}`, ARTIFACT_SHARD: String(n), EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
      ARTIFACT_NEEDS_JSON: JSON.stringify(buildNeeds(runtime)), ...overrides };
    const diagnostic = store(env, changes).put({ kind: 'shard-diagnostics', source: `results-${n}`, retentionDays: 14 });
    const qualifiedEnv = { ...env, EXPECTED_DIAGNOSTIC_ARTIFACT_ID: diagnostic.id };
    const qualified = store(qualifiedEnv, changes).put({ kind: 'qualified-shard', source: `results-${n}`, retentionDays: 14 });
    return { diagnostic, qualified, env: qualifiedEnv, source: `results-${n}` };
  }
  const deployEnv = (kind, result, overrides = {}) => ({ GITHUB_JOB: 'deploy', EXPECTED_ARTIFACT_ID: result.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify({ [kind === 'backend' ? 'backend' : 'frontend']: success({ [`${kind}-artifact-id`]: result.id, 'manifest-sha': manifest, 'sdk-version': sdk }) }), ...overrides });
  return { root, storeRoot, workspace, runnerTemp, output, baseEnv, adapter, options, store, write, object, publish, buildNeeds, shard, deployEnv };
}

function forge(f, original, transform) {
  const old = f.object(original.id), receipt = JSON.parse(readFileSync(join(old, 'receipt.json'), 'utf8'));
  transform(receipt);
  const bytes = canonicalBytes(receipt), id = artifactPrefix + digest(bytes), next = f.object(id);
  mkdirSync(dirname(next), { recursive: true }); renameSync(old, next); writeFileSync(join(next, 'receipt.json'), bytes);
  return { id, receipt };
}

test('round-trips exact package bytes through a typed canonical receipt and trusted needs output', t => {
  const f = fixture(t); f.write('frontend.tar.gz', Buffer.from([0, 255, 10, 13, 7]));
  const published = f.publish(); assert.match(published.id, /^xsenus-local-v1:[a-f0-9]{64}$/);
  const bytes = readFileSync(join(f.object(published.id), 'receipt.json'));
  assert.equal(published.id, artifactPrefix + digest(bytes)); assert.ok(bytes.equals(canonicalBytes(published.receipt)));
  f.store(f.deployEnv('frontend', published)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'download' });
  assert.deepEqual(readFileSync(join(f.workspace, 'download/frontend.tar.gz')), Buffer.from([0, 255, 10, 13, 7]));
  assert.deepEqual(published.receipt.files, [{ path: 'frontend.tar.gz', bytes: 5, sha256: digest(Buffer.from([0, 255, 10, 13, 7])) }]);
  assert.equal(published.receipt.retentionDays, 3); assert.equal(published.receipt.producerJob, 'frontend');
});

test('publishes exact output only after readback; package output is artifact-id', t => {
  const f = fixture(t); f.write('frontend.tar.gz');
  const result = runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'], f.options());
  assert.equal(readFileSync(f.output, 'utf8'), `artifact-id=${result.id}\n`);
});

test('qualified shards bind runtime, manifest, SDK, diagnostics, job and only their one output key', t => {
  const f = fixture(t), runtime = f.publish('test-runtime'), shard = f.shard(runtime, 2);
  const result = runCommand(['put', '--kind', 'qualified-shard', '--source', shard.source, '--retention-days', '14'], f.options(shard.env));
  assert.equal(readFileSync(f.output, 'utf8'), `shard_2_id=${result.id}\n`);
  assert.equal(result.id, shard.qualified.id);
  const receipt = shard.qualified.receipt;
  assert.equal(receipt.runtimeArtifactId, runtime.id); assert.equal(receipt.diagnosticArtifactId, shard.diagnostic.id);
  assert.equal(receipt.manifestSha, manifest); assert.equal(receipt.sdkVersion, sdk); assert.equal(receipt.producerJob, 'backend-test-2');
  assert.ok(receipt.directories.includes('shard-2/empty'));
});

test('valid earlier producer attempt is reusable within the same run; future attempts fail', t => {
  const f = fixture(t), published = f.publish('backend', { GITHUB_RUN_ATTEMPT: '2' });
  const request = { kind: 'backend', producerJob: 'backend-build', destination: 'retry' };
  assert.throws(() => f.store(f.deployEnv('backend', published)).get(request), /Future/);
  f.store(f.deployEnv('backend', published, { GITHUB_RUN_ATTEMPT: '3' })).get(request);
  assert.ok(existsSync(join(f.workspace, 'retry/backend.tar.gz')));
});

test('readback is not a receipt-only check: missing, changed and extra payload files fail', t => {
  const f = fixture(t), result = f.publish(); const payload = join(f.object(result.id), 'payload/frontend.tar.gz');
  const original = readFileSync(payload), get = () => f.store(f.deployEnv('frontend', result)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' });
  writeFileSync(payload, 'tampered'); assert.throws(get, /inventory mismatch/); assert.ok(!existsSync(join(f.workspace, 'bad')));
  writeFileSync(payload, original); writeFileSync(join(dirname(payload), 'extra'), 'bad'); assert.throws(get, /inventory mismatch/);
  rmSync(join(dirname(payload), 'extra')); rmSync(payload); assert.throws(get, /inventory mismatch/);
});

test('a trusted typed ID detects receipt tampering and unexpected object files', t => {
  const f = fixture(t), result = f.publish(), object = f.object(result.id), receipt = join(object, 'receipt.json');
  const original = readFileSync(receipt); writeFileSync(receipt, Buffer.concat([original, Buffer.from(' ')]));
  const get = () => f.store(f.deployEnv('frontend', result)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' });
  assert.throws(get, /digest/); writeFileSync(receipt, original); writeFileSync(join(object, 'extra'), 'no'); assert.throws(get, /Unexpected object files/);
});

for (const [field, value] of [['runId', '999'], ['headSha', 'c'.repeat(40)], ['workflowSha', 'd'.repeat(40)],
  ['workflowRef', 'other/workflow@main'], ['repository', 'other/repository'], ['ref', 'refs/heads/other'],
  ['event', 'workflow_dispatch'], ['runnerName', 'other'], ['host', 'OTHER'], ['producerJob', 'backend-build'],
  ['kind', 'backend'], ['shard', 0], ['manifestSha', manifest], ['sdkVersion', sdk], ['runtimeArtifactId', artifactPrefix + 'e'.repeat(64)],
  ['producerAttempt', 0], ['retentionDays', 14]]) {
  test(`rejects a hash-anchored receipt with wrong ${field}`, t => {
    const f = fixture(t), original = f.publish(), forged = forge(f, original, receipt => { receipt[field] = value; });
    assert.throws(() => f.store(f.deployEnv('frontend', forged)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' }));
    assert.ok(!existsSync(join(f.workspace, 'bad')));
  });
}

test('needs JSON requires the exact successful producer/output, never a local name or claimed status', t => {
  const f = fixture(t), published = f.publish();
  const attempts = [{}, { frontend: { result: 'failure', outputs: { 'frontend-artifact-id': published.id } } },
    { frontend: success({ 'frontend-artifact-id': artifactPrefix + 'f'.repeat(64) }) },
    { 'backend-build': success({ 'frontend-artifact-id': published.id }) }];
  for (const needs of attempts) assert.throws(() => f.store(f.deployEnv('frontend', published, { ARTIFACT_NEEDS_JSON: JSON.stringify(needs) }))
    .get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' }), /needs/);
});

test('failed unrelated prior shard does not invalidate a successful runtime producer', t => {
  const f = fixture(t), runtime = f.publish('test-runtime');
  const needs = { ...f.buildNeeds(runtime), 'backend-test-0': { result: 'failure', outputs: {} } };
  f.store({ GITHUB_JOB: 'backend-test-1', EXPECTED_ARTIFACT_ID: runtime.id, ARTIFACT_NEEDS_JSON: JSON.stringify(needs) })
    .get({ kind: 'test-runtime', producerJob: 'backend-build', destination: 'runtime' });
});

test('wrong manifest, SDK or runtime bindings cannot publish shard qualification', t => {
  const f = fixture(t), runtime = f.publish('test-runtime'); f.write('results/shard-0/result.trx', 'passed');
  const env = { GITHUB_JOB: 'backend-test-0', ARTIFACT_SHARD: '0', EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify(f.buildNeeds(runtime)) };
  for (const change of [{ EXPECTED_MANIFEST_SHA: 'f'.repeat(64) }, { EXPECTED_SDK_VERSION: '9.0.111' },
    { EXPECTED_RUNTIME_ARTIFACT_ID: artifactPrefix + 'f'.repeat(64) }, { ARTIFACT_SHARD: '1' }]) {
    assert.throws(() => f.store({ ...env, ...change }).put({ kind: 'shard-diagnostics', source: 'results', retentionDays: 14 }));
  }
  assert.throws(() => f.store(env).put({ kind: 'qualified-shard', source: 'results', retentionDays: 14 }), /Persisted diagnostics/);
});

test('qualification requires unchanged same-attempt diagnostics; diagnostic kind cannot substitute', t => {
  const f = fixture(t), runtime = f.publish('test-runtime'), shard = f.shard(runtime);
  f.write('results-0/shard-0/result.trx', 'changed');
  assert.throws(() => f.store(shard.env).put({ kind: 'qualified-shard', source: shard.source, retentionDays: 14 }), /exactly match/);
  assert.throws(() => f.store({ ...shard.env, GITHUB_RUN_ATTEMPT: '2' }).put({ kind: 'qualified-shard', source: shard.source, retentionDays: 14 }), /this attempt/);
  const needs = { ...f.buildNeeds(runtime), 'backend-test-0': success({ 'qualified-artifact-id': shard.diagnostic.id }) };
  assert.throws(() => f.store({ GITHUB_JOB: 'backend', EXPECTED_ARTIFACT_ID: shard.diagnostic.id, EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify(needs) }).get({ kind: 'qualified-shard', producerJob: 'backend-test-0', shard: 0, destination: 'bad' }), /kind/);
});

test('qualified retry verifies the retained diagnostic bytes as well as the qualified payload', t => {
  const f = fixture(t), runtime = f.publish('test-runtime'), shard = f.shard(runtime);
  const env = { GITHUB_JOB: 'backend', GITHUB_RUN_ATTEMPT: '2', EXPECTED_ARTIFACT_ID: shard.qualified.id, EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify({ ...f.buildNeeds(runtime), 'backend-test-0': success({ 'qualified-artifact-id': shard.qualified.id }) }) };
  f.store(env).get({ kind: 'qualified-shard', producerJob: 'backend-test-0', shard: 0, destination: 'valid' });
  writeFileSync(join(f.object(shard.diagnostic.id), 'payload/shard-0/result.trx'), 'tampered diagnostic');
  assert.throws(() => f.store(env).get({ kind: 'qualified-shard', producerJob: 'backend-test-0', shard: 0, destination: 'bad' }), /inventory mismatch/);
});

test('disjoint retrieval combines exact shard trees and rejects even byte-identical file overlap', t => {
  const f = fixture(t), runtime = f.publish('test-runtime'), first = f.shard(runtime, 0), second = f.shard(runtime, 1);
  const get = (n, item) => f.store({ GITHUB_JOB: 'backend', EXPECTED_ARTIFACT_ID: item.qualified.id, EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify({ ...f.buildNeeds(runtime), [`backend-test-${n}`]: success({ 'qualified-artifact-id': item.qualified.id }) }) })
    .get({ kind: 'qualified-shard', producerJob: `backend-test-${n}`, shard: n, destination: 'all-results', mergeDisjoint: true });
  get(0, first); get(1, second);
  assert.equal(readFileSync(join(f.workspace, 'all-results/shard-0/result.trx'), 'utf8'), 'case 0: passed');
  assert.equal(readFileSync(join(f.workspace, 'all-results/shard-1/result.trx'), 'utf8'), 'case 1: passed');
  assert.throws(() => get(0, first), /overlaps/);
});

for (const name of ['../escape', '/absolute', 'C:/drive', 'file:stream', 'a\\b', 'a/../b', 'a//b', 'a/./b', 'bad\nname', 'a.', 'a ', 'NUL.txt', 'com1', 'a?']) {
  test(`rejects unsafe inventory path ${JSON.stringify(name)}`, () => assert.throws(() => safeRelativePath(name)));
}

test('a receipt with traversal or duplicate/case-colliding paths fails before writing destination', t => {
  for (const mutation of [receipt => { receipt.files[0].path = '../escape'; }, receipt => { receipt.files.push({ ...receipt.files[0] }); receipt.totalBytes *= 2; },
    receipt => { receipt.files.push({ ...receipt.files[0], path: 'FRONTEND.TAR.GZ' }); receipt.totalBytes *= 2; }]) {
    const f = fixture(t), result = forge(f, f.publish(), mutation);
    assert.throws(() => f.store(f.deployEnv('frontend', result)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' }));
    assert.ok(!existsSync(join(f.workspace, 'bad')));
  }
});

test('rejects source junctions, hard links and native reparse attributes', t => {
  const f = fixture(t); f.write('real/frontend.tar.gz', 'owned');
  symlinkSync(join(f.workspace, 'real'), join(f.workspace, 'junction'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => f.store().put({ kind: 'frontend', source: 'junction/frontend.tar.gz', retentionDays: 3 }), /Nonregular/);
  f.write('frontend.tar.gz'); linkSync(join(f.workspace, 'frontend.tar.gz'), join(f.workspace, 'linked-copy'));
  assert.throws(() => f.publish(), /Hard-linked/);
  rmSync(join(f.workspace, 'linked-copy'));
  const inspect = paths => { const report = f.adapter.inspect(paths); report.items.at(-1).reparse = true; return report; };
  assert.throws(() => f.store({}, { inspect }).check(), /Reparse/);
});

test('store and destination junctions cannot redirect artifact reads or writes', t => {
  const f = fixture(t), result = f.publish();
  const link = join(f.root, 'linked-store'); symlinkSync(f.storeRoot, link, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => f.store({ PAYPLANNER_CI_ARTIFACT_ROOT: link }).check(), /Nonregular/);
  f.write('outside/keep', 'original');
  symlinkSync(join(f.workspace, 'outside'), join(f.workspace, 'destination'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => f.store(f.deployEnv('frontend', result)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'destination', mergeDisjoint: true }), /Nonregular/);
  assert.deepEqual(readdirSync(join(f.workspace, 'outside')), ['keep']);
});

test('named or missing file streams fail before publication and cannot hide in stored payload or receipt', t => {
  const f = fixture(t); f.write('frontend.tar.gz');
  let badPath = join(f.workspace, 'frontend.tar.gz'), streams = [':$DATA', 'hidden:$DATA'];
  const inspect = paths => {
    const report = f.adapter.inspect(paths);
    for (const item of report.items) if (item.path === badPath) item.streams = streams;
    return report;
  };
  const put = () => runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'], f.options({}, { inspect }));
  assert.throws(put, /data stream/); assert.equal(readFileSync(f.output, 'utf8'), '');
  assert.ok(!existsSync(join(f.storeRoot, 'v1')));
  streams = undefined; assert.throws(put, /data stream/);
  const published = f.publish(); streams = [':$DATA', 'hidden:$DATA'];
  for (const relative of ['receipt.json', 'payload/frontend.tar.gz']) {
    badPath = join(f.object(published.id), relative);
    assert.throws(() => f.store(f.deployEnv('frontend', published), { inspect }).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad-stream' }), /data stream/);
    assert.ok(!existsSync(join(f.workspace, 'bad-stream')));
  }
});

test('a named stream introduced by a copy fails full readback and emits no trusted output', t => {
  const f = fixture(t); f.write('frontend.tar.gz'); let copiedPath;
  const changes = {
    copyFile(from, to) { f.adapter.copyFile(from, to); copiedPath = to; },
    inspect(paths) {
      const report = f.adapter.inspect(paths);
      for (const item of report.items) if (item.path === copiedPath) item.streams.push('introduced:$DATA');
      return report;
    },
  };
  assert.throws(() => runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'], f.options({}, changes)), /data stream/);
  assert.equal(readFileSync(f.output, 'utf8'), '');
});

test('receipt identity must use exact canonical bytes even when its digest is valid', t => {
  const f = fixture(t), result = f.publish(), old = f.object(result.id);
  const bytes = Buffer.from(JSON.stringify(result.receipt, null, 2)), id = artifactPrefix + digest(bytes), object = f.object(id);
  mkdirSync(dirname(object), { recursive: true }); renameSync(old, object); writeFileSync(join(object, 'receipt.json'), bytes);
  assert.throws(() => f.store(f.deployEnv('frontend', { id })).get({ kind: 'frontend', producerJob: 'frontend', destination: 'bad' }), /Noncanonical/);
});

test('partial disjoint merge preserves existing evidence and reports failure', t => {
  const f = fixture(t), frontend = f.publish(), backend = f.publish('backend');
  f.store(f.deployEnv('frontend', frontend)).get({ kind: 'frontend', producerJob: 'frontend', destination: 'packages', mergeDisjoint: true });
  assert.throws(() => f.store(f.deployEnv('backend', backend), {
    copyFile(from, to) { if (dirname(to) === join(f.workspace, 'packages')) { writeFileSync(to, 'partial', { flag: 'wx' }); throw new Error('merge copy failed'); } f.adapter.copyFile(from, to); },
  }).get({ kind: 'backend', producerJob: 'backend-build', destination: 'packages', mergeDisjoint: true }), /merge copy failed/);
  assert.equal(readFileSync(join(f.workspace, 'packages/frontend.tar.gz'), 'utf8'), 'fixture payload');
  assert.equal(readFileSync(join(f.workspace, 'packages/backend.tar.gz'), 'utf8'), 'partial');
});

test('native filesystem inspections are batched for large diagnostic file trees', t => {
  const f = fixture(t), runtime = f.publish('test-runtime');
  for (let n = 0; n < 200; n++) f.write(`many/shard-0/case-${n}.trx`, `passed ${n}`);
  let inspections = 0;
  const result = f.store({ GITHUB_JOB: 'backend-test-0', ARTIFACT_SHARD: '0', EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify(f.buildNeeds(runtime)) }, { inspect(paths) { inspections++; return f.adapter.inspect(paths); } })
    .put({ kind: 'shard-diagnostics', source: 'many', retentionDays: 14 });
  assert.equal(result.receipt.files.length, 200);
  assert.ok(inspections < 40, `Expected batched inspections, got ${inspections}`);
});

test('rejects missing/overlapping store roots and source/destination escapes', t => {
  const f = fixture(t);
  assert.throws(() => f.store({ PAYPLANNER_CI_ARTIFACT_ROOT: join(f.root, 'missing') }).check());
  for (const path of [f.workspace, join(f.workspace, 'store'), f.root, f.runnerTemp]) assert.throws(() => f.store({ PAYPLANNER_CI_ARTIFACT_ROOT: path }));
  assert.throws(() => f.store().put({ kind: 'frontend', source: '../outside.tar.gz', retentionDays: 3 }), /traversal/);
  const result = f.publish(); assert.throws(() => f.store(f.deployEnv('frontend', result)).get({ kind: 'frontend', producerJob: 'frontend', destination: f.root }), /inside checkout/);
});

test('enforces native owner, broad write/delete ACL, fixed local filesystem and complete inspection', t => {
  const f = fixture(t), paths = [f.root, f.storeRoot];
  validateInspection(f.adapter.inspect(paths), paths, f.storeRoot);
  for (const mutate of [r => { r.items[1].owner = 'S-1-1-0'; }, r => { r.items[1].rules.push({ sid: 'S-1-1-0', rights: 2, allow: true, inheritOnly: false }); },
    r => { r.items[0].rules.push({ sid: 'S-1-5-11', rights: 64, allow: true, inheritOnly: false }); }, r => { r.items[1].fixed = false; },
    r => { r.items[1].format = 'FAT32'; }, r => { r.items.pop(); }]) {
    const report = f.adapter.inspect(paths); mutate(report); assert.throws(() => validateInspection(report, paths, f.storeRoot));
  }
  const readOnly = f.adapter.inspect(paths); readOnly.items[1].rules.push({ sid: 'S-1-1-0', rights: 1179785, allow: true, inheritOnly: false });
  validateInspection(readOnly, paths, f.storeRoot);
});

test('actual XSENUS volume-root Modify ACL is compatible with a separately private store', t => {
  const f = fixture(t), volumeRoot = parse(f.storeRoot).root, paths = [volumeRoot, f.storeRoot];
  const report = f.adapter.inspect(paths);
  report.items[0].owner = 'S-1-5-18';
  report.items[0].rules.push({ sid: 'S-1-5-11', rights: 1245631, allow: true, inheritOnly: false });
  report.items[0].rules.push({ sid: 'S-1-5-11', rights: -536805376, allow: true, inheritOnly: true });
  assert.equal(1245631 & 65536, 65536); assert.equal(1245631 & 64, 0);
  validateInspection(report, paths, f.storeRoot);
  // The volume-root exception must not relax the private store's own DACL.
  report.items[1].rules.push({ sid: 'S-1-5-11', rights: 1245631, allow: true, inheritOnly: false });
  assert.throws(() => validateInspection(report, paths, f.storeRoot), /Broad write/);
});

test('DELETE-self on any ordinary store ancestor still permits replacement and is rejected', t => {
  const f = fixture(t), paths = [f.root, f.storeRoot], report = f.adapter.inspect(paths);
  assert.notEqual(f.root, parse(f.root).root);
  report.items[0].rules.push({ sid: 'S-1-5-11', rights: 65536, allow: true, inheritOnly: false });
  assert.throws(() => validateInspection(report, paths, f.storeRoot), /Broad write/);
});

test('volume roots still reject broad DELETE_CHILD, WRITE_DAC and WRITE_OWNER', t => {
  const f = fixture(t), volumeRoot = parse(f.storeRoot).root, paths = [volumeRoot, f.storeRoot];
  for (const rights of [64, 262144, 524288]) {
    const report = f.adapter.inspect(paths);
    report.items[0].rules.push({ sid: 'S-1-5-11', rights, allow: true, inheritOnly: false });
    assert.throws(() => validateInspection(report, paths, f.storeRoot), /Broad write/);
  }
});

test('requires actual copy budget plus the fixed20GiB reserve and never emits on low disk', t => {
  const f = fixture(t); f.write('frontend.tar.gz', Buffer.alloc(100));
  assert.throws(() => runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'],
    f.options({}, { freeBytes: () => reserveBytes + 99n })), /20GiB/);
  assert.equal(readFileSync(f.output, 'utf8'), '');
  assert.ok(!existsSync(join(f.storeRoot, 'v1')));
});

test('source mutation during copy fails before publication or GitHub output', t => {
  const f = fixture(t); f.write('frontend.tar.gz', 'original');
  assert.throws(() => runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'], f.options({}, {
    copyFile(from, to) { f.adapter.copyFile(from, to); writeFileSync(from, 'mutated'); },
  })), /Source changed/);
  assert.equal(readFileSync(f.output, 'utf8'), '');
  const prefixes = readdirSync(join(f.storeRoot, 'v1/objects'));
  assert.ok(prefixes.flatMap(prefix => readdirSync(join(f.storeRoot, 'v1/objects', prefix))).every(name => name.startsWith('.staging-')));
});

test('partial copy, failed atomic publication and committed readback tamper never emit output', t => {
  for (const phase of ['copy', 'publish', 'readback']) {
    const f = fixture(t); f.write('frontend.tar.gz');
    const changes = phase === 'copy' ? { copyFile(_from, to) { writeFileSync(to, 'partial', { flag: 'wx' }); throw new Error('copy failure'); } }
      : { publish(from, to) { if (phase === 'publish') throw new Error('publish failure'); f.adapter.publish(from, to); writeFileSync(join(to, 'payload/frontend.tar.gz'), 'tampered'); } };
    assert.throws(() => runCommand(['put', '--kind', 'frontend', '--source', 'frontend.tar.gz', '--retention-days', '3'], f.options({}, changes)));
    assert.equal(readFileSync(f.output, 'utf8'), '');
  }
});

test('duplicate content identity verifies existing object without replacement; corrupted collision fails', t => {
  const f = fixture(t), first = f.publish();
  const second = f.publish('frontend', {}, { publish() { assert.fail('A duplicate must not replace its object'); } });
  assert.equal(first.id, second.id);
  writeFileSync(join(f.object(first.id), 'payload/frontend.tar.gz'), 'corrupt');
  assert.throws(() => f.publish(), /inventory mismatch/);
});

test('mandatory archives and diagnostic payloads cannot be empty', t => {
  const f = fixture(t); f.write('frontend.tar.gz', ''); assert.throws(() => f.publish(), /empty payload/);
  rmSync(join(f.workspace, 'frontend.tar.gz')); const runtime = f.publish('test-runtime'); mkdirSync(join(f.workspace, 'empty'));
  assert.throws(() => f.store({ GITHUB_JOB: 'backend-test-0', ARTIFACT_SHARD: '0', EXPECTED_RUNTIME_ARTIFACT_ID: runtime.id,
    ARTIFACT_NEEDS_JSON: JSON.stringify(f.buildNeeds(runtime)) }).put({ kind: 'shard-diagnostics', source: 'empty', retentionDays: 14 }), /Empty/);
});

test('CLI has no host bypass, unknown flags, duplicate flags, wildcard ID or implicit latest fallback', t => {
  const f = fixture(t);
  for (const args of [['check', '--skip-verification'], ['put', '--kind', 'frontend', '--source', '*', '--retention-days', '3', '--kind', 'backend'],
    ['get', '--kind', 'frontend', '--producer-job', 'frontend', '--destination', 'd', '--pattern', 'latest']]) assert.throws(() => parseArguments(args));
  for (const override of [{ COMPUTERNAME: 'ILYA' }, { GITHUB_ACTIONS: 'false' }, { GITHUB_REF: 'refs/pull/1/merge' },
    { GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_WORKFLOW_REF: 'other' }, { GITHUB_RUN_ATTEMPT: '0' }, { GITHUB_JOB: 'untrusted' }]) {
    assert.throws(() => contextFromEnvironment({ ...f.baseEnv, ...override }, 'win32'));
  }
  const child = spawnSync(process.execPath, [resolve('scripts/xsenus-artifact-store.mjs'), 'check'], {
    env: { ...process.env, COMPUTERNAME: 'ILYA' }, encoding: 'utf8', windowsHide: true,
  });
  assert.equal(child.status, 1); assert.match(child.stderr, /Only trusted/);
});
