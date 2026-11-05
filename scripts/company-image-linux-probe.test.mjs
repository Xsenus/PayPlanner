// @vitest-environment node
import assert from 'node:assert/strict';
import { it as test } from 'vitest';
import { mkdtemp, mkdir, readFile, readdir, rmdir, unlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { sha256 } from './prepare-company-image-linux-probe.mjs';
import { createArguments, executeProbe, memoryBytes, parseProbeOutput, resolveResultPath, safeRelative, validateImage, verifyArtifact } from './run-company-image-linux-probe.mjs';

const image = `mcr.microsoft.com/dotnet/runtime@sha256:${'a'.repeat(64)}`;
const runId = '11111111-1111-4111-8111-111111111111';
const app = '/isolated/company-probe/app';
const id = 'b'.repeat(64);
function container() {
  return { Image: 'test-image-id', Config: { Entrypoint: ['/usr/bin/timeout'], Cmd: ['--signal=KILL', '40s', 'dotnet', '/probe/PayPlanner.CompanyImageProbe.dll'], User: '65532:65532', Labels: { 'payplanner.probe': 'company-image', 'payplanner.probe.run': runId },
    Env: ['PAYPLANNER_IMAGE_PROBE=isolated-linux', 'Email__Enabled=false', 'BackgroundServices__Enabled=false'] },
  HostConfig: { ReadonlyRootfs: true, Privileged: false, NetworkMode: 'none', Memory: memoryBytes, MemorySwap: memoryBytes, NanoCpus: 1_000_000_000,
    PidsLimit: 64, CapDrop: ['ALL'], SecurityOpt: ['no-new-privileges=true'], IpcMode: 'private', RestartPolicy: { Name: 'no' }, LogConfig: { Type: 'none' }, Tmpfs: { '/tmp': 'rw,noexec,nosuid,nodev,size=16m' },
    Ulimits: [{ Name: 'core', Soft: 0, Hard: 0 }, { Name: 'nofile', Soft: 128, Hard: 128 }] },
  Mounts: [{ Type: 'bind', Source: app, Destination: '/probe', RW: false }], State: { ExitCode: 0, OOMKilled: false, Running: false } };
}
function fakeDocker({ alter, alterEngine, startError, oom = false } = {}) {
  const calls = [];
  const state = container();
  state.State.OOMKilled = oom;
  if (alter) alter(state);
  const engine = { MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] };
  if (alterEngine) alterEngine(engine);
  return { calls, docker(args) {
    calls.push(args);
    if (args[0] === 'info') return { stdout: JSON.stringify(engine) };
    if (args[0] === 'image') return { stdout: JSON.stringify([{ Id: 'test-image-id', Os: 'linux', Architecture: 'amd64', RepoDigests: [image], Config: { Env: ['DOTNET_VERSION=9.0.22'] } }]) };
    if (args[0] === 'create') return { stdout: id };
    if (args[0] === 'inspect') return { stdout: JSON.stringify([state]) };
    if (args[0] === 'start') { state.State.Running = Boolean(startError); return { status: startError ? null : 0, error: startError,
      stdout: JSON.stringify({ status: 'passed', cases: Array.from({ length: 16 }, () => ({ passed: true })) }) }; }
    if (args[0] === 'kill') state.State.Running = false;
    return { stdout: '', status: 0 };
  } };
}

test('plan has one readonly bind, no network/pull/ports/root and bounded resources', () => {
  const args = createArguments({ app, image, runId });
  for (const flag of ['--pull=never', '--network=none', '--read-only', '--memory=512m', '--memory-swap=512m', '--cpus=1', '--user=65532:65532', '--pids-limit=64', '--ulimit=core=0:0', '--ulimit=nofile=128:128']) assert.ok(args.includes(flag));
  assert.equal(args.filter(arg => arg.startsWith('--mount=')).length, 1);
  assert.ok(args.find(arg => arg.startsWith('--mount=')).endsWith(',readonly'));
  assert.ok(args.includes('--entrypoint=/usr/bin/timeout'));
  assert.deepEqual(args.slice(-4), ['--signal=KILL', '40s', 'dotnet', '/probe/PayPlanner.CompanyImageProbe.dll']);
  assert.ok(!args.some(arg => /privileged|^--publish|^--volume|^--env-file/.test(arg)));
});
test('reject mutable/foreign image refs and container path injection', () => {
  for (const candidate of ['mcr.microsoft.com/dotnet/runtime:9.0', 'other/runtime@sha256:' + 'a'.repeat(64), '']) assert.throws(() => validateImage(candidate));
  assert.throws(() => createArguments({ app: '/tmp/app,dst=/etc', image, runId }));
  for (const candidate of ['../secret', '/etc/passwd', 'C:/secret', 'a/../b', 'a\\b']) assert.equal(safeRelative(candidate), false);
  assert.equal(safeRelative('runtimes/linux-x64/native/libSkiaSharp.so'), true);
});
test('successful fake run removes only its own container', () => {
  const fake = fakeDocker();
  const result = executeProbe({ app, image, runId, docker: fake.docker });
  assert.equal(result.status, 'passed');
  assert.equal(result.ownedContainerRemoved, true);
  assert.deepEqual(fake.calls.find(args => args[0] === 'rm'), ['rm', id]);
});

test('requires Docker API CpuCfsQuota and refuses missing, disabled or incorrectly cased capability', () => {
  for (const alterEngine of [
    engine => { delete engine.CpuCfsQuota; },
    engine => { engine.CpuCfsQuota = false; },
    engine => { delete engine.CpuCfsQuota; engine.CPUCfsQuota = true; },
  ]) {
    const fake = fakeDocker({ alterEngine });
    assert.throws(() => executeProbe({ app, image, runId, docker: fake.docker }), /must support.*CPU/);
    assert.ok(!fake.calls.some(args => ['create', 'start', 'kill', 'rm'].includes(args[0])));
  }
});
test('OOM is failure, never success even when JSON says passed', () => {
  const fake = fakeDocker({ oom: true });
  const result = executeProbe({ app, image, runId, docker: fake.docker });
  assert.equal(result.status, 'failed');
  assert.equal(result.oomKilled, true);
  assert.equal(result.ownedContainerRemoved, true);
});
test('timeout kills and removes only the labelled owned process container', () => {
  const fake = fakeDocker({ startError: new Error('timeout') });
  const result = executeProbe({ app, image, runId, docker: fake.docker });
  assert.equal(result.status, 'failed');
  assert.ok(fake.calls.some(args => args[0] === 'kill' && args[1] === id));
  assert.equal(result.ownedContainerRemoved, true);
});
test('changed isolation is rejected before starting, with owned cleanup', () => {
  for (const alter of [state => { state.HostConfig.NetworkMode = 'host'; }, state => { state.Mounts[0].RW = true; }, state => { state.HostConfig.Memory = 0; }]) {
    const fake = fakeDocker({ alter });
    const result = executeProbe({ app, image, runId, docker: fake.docker });
    assert.equal(result.status, 'failed');
    assert.ok(!fake.calls.some(args => args[0] === 'start'));
    assert.equal(result.ownedContainerRemoved, true);
  }
});
test('foreign ownership prevents both start and cleanup', () => {
  const fake = fakeDocker({ alter: state => { state.Config.Labels['payplanner.probe.run'] = 'other'; } });
  const result = executeProbe({ app, image, runId, docker: fake.docker });
  assert.equal(result.status, 'failed');
  assert.ok(result.cleanupError.includes('ownership'));
  assert.ok(!fake.calls.some(args => ['start', 'kill', 'rm'].includes(args[0])));
});

test('missing or changed file/core limits refuse native start and clean only the owned container', () => {
  for (const alter of [
    state => { delete state.HostConfig.Ulimits; },
    state => { state.HostConfig.Ulimits = []; },
    state => { state.HostConfig.Ulimits[1].Soft = 64; },
    state => { state.HostConfig.Ulimits[1].Hard = 256; },
    state => { state.HostConfig.Ulimits[0].Hard = -1; },
    state => { state.HostConfig.Ulimits.push({ Name: 'nofile', Soft: 128, Hard: 128 }); },
  ]) {
    const fake = fakeDocker({ alter });
    const result = executeProbe({ app, image, runId, docker: fake.docker });
    assert.equal(result.status, 'failed');
    assert.ok(!fake.calls.some(args => args[0] === 'start'));
    assert.equal(result.ownedContainerRemoved, true);
  }
});

async function artifactFixture(t) {
  const directory = await mkdtemp(path.join(tmpdir(), 'payplanner-image-probe-test-'));
  const app = path.join(directory, 'app');
  await mkdir(app);
  t.onTestFinished(async () => {
    assert.equal(path.dirname(directory), tmpdir());
    assert.ok(path.basename(directory).startsWith('payplanner-image-probe-test-'));
    for (const file of await readdir(app)) await unlink(path.join(app, file));
    await rmdir(app);
    await unlink(path.join(directory, 'probe-manifest.json'));
    await rmdir(directory);
  });
  const files = [];
  for (const name of ['PayPlanner.CompanyImageProbe.dll', 'PayPlanner.CompanyImageProbe.deps.json', 'PayPlanner.CompanyImageProbe.runtimeconfig.json', 'libSkiaSharp.so']) {
    await writeFile(path.join(app, name), 'synthetic manifest fixture only');
    files.push({ path: name, sha256: await sha256(path.join(app, name)) });
  }
  await writeFile(path.join(directory, 'probe-manifest.json'), JSON.stringify({ schema: 1, kind: 'payplanner-company-image-linux-probe', rid: 'linux-x64', expectedCases: 16, files }));
  return { directory, app, hash: await sha256(path.join(directory, 'probe-manifest.json')) };
}
test('artifact integrity: verifies exact catalog without executing any code', async t => {
  const fixture = await artifactFixture(t);
  const verified = await verifyArtifact(fixture.directory, fixture.hash);
  assert.equal(verified.manifestSha256, fixture.hash);
  assert.equal(verified.manifest.files.length, 4);
});
test('artifact integrity: refuses changed native file or an extra file', async t => {
  const fixture = await artifactFixture(t);
  await writeFile(path.join(fixture.app, 'libSkiaSharp.so'), 'changed');
  await assert.rejects(verifyArtifact(fixture.directory, fixture.hash), /hash changed/);
  await writeFile(path.join(fixture.app, 'extra.txt'), 'extra');
  await assert.rejects(verifyArtifact(fixture.directory, fixture.hash), /catalog changed/);
});
test('artifact integrity: refuses replacement manifest even when its file hashes look valid', async t => {
  const fixture = await artifactFixture(t);
  const manifest = JSON.parse(await readFile(path.join(fixture.directory, 'probe-manifest.json'), 'utf8'));
  manifest.files[0].sha256 = 'c'.repeat(64);
  await writeFile(path.join(fixture.directory, 'probe-manifest.json'), JSON.stringify(manifest));
  await assert.rejects(verifyArtifact(fixture.directory, fixture.hash), /manifest hash changed/);
});
test('artifact integrity: rejects traversal before reading outside the mount', async t => {
  const fixture = await artifactFixture(t);
  const file = path.join(fixture.directory, 'probe-manifest.json');
  const manifest = JSON.parse(await readFile(file, 'utf8'));
  manifest.files[0].path = '../secret';
  await writeFile(file, JSON.stringify(manifest));
  await assert.rejects(verifyArtifact(fixture.directory, await sha256(file)), /Invalid artifact file entry/);
});
test('result preflight: refuses existing evidence or a result inside the app before Docker', async t => {
  const fixture = await artifactFixture(t);
  await assert.rejects(resolveResultPath(path.join(fixture.directory, 'probe-manifest.json'), fixture.app), /already exists/);
  await assert.rejects(resolveResultPath(path.join(fixture.app, 'result.json'), fixture.app), /outside/);
});
test('result preflight: resolves a new sibling result without creating it', async t => {
  const fixture = await artifactFixture(t);
  const result = path.join(fixture.directory, 'new-result.json');
  assert.equal(await resolveResultPath(result, fixture.app), result);
  await assert.rejects(readFile(result), error => error.code === 'ENOENT');
});
test('native output: saves completed case evidence on interrupted summary', () => {
  const parsed = parseProbeOutput('{"type":"case","result":{"name":"png","passed":true}}\n{\n  "status":');
  assert.deepEqual(parsed.partialCases, [{ name: 'png', passed: true }]);
  assert.equal(parsed.output, undefined);
});
test('native output: reads case events followed by a complete formatted summary', () => {
  const parsed = parseProbeOutput('{"type":"case","result":{"name":"png","passed":true}}\n' + JSON.stringify({ status: 'passed' }, null, 2));
  assert.equal(parsed.partialCases.length, 1);
  assert.equal(parsed.output.status, 'passed');
});
test('native output: accepts a single complete summary without inventing case events', () => {
  const parsed = parseProbeOutput('{"status":"failed"}');
  assert.equal(parsed.output.status, 'failed');
  assert.deepEqual(parsed.partialCases, []);
});
test('engine resources: no container is created without enforceable memory/cpu/pid limits', () => {
  const fake = fakeDocker();
  const docker = args => args[0] === 'info' ? { stdout: JSON.stringify({ MemoryLimit: false }) } : fake.docker(args);
  assert.throws(() => executeProbe({ app, image, runId, docker }), /must support/);
  assert.equal(fake.calls.length, 0);
});
