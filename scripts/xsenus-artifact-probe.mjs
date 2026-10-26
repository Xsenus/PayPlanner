import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { gzipSync, gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { requireTrustedHost } from './verify-xsenus-runner.mjs';

const helperSha = '7ee39a8d49b12b0247c4a9b484262b58458d8b063af69e3533fe925f4053aa6c';
const guardSha = 'f2fd90458ac5060901273aacf974e85c92e2f1a7e09e237a5a1da5c02c91820b';
const expectedSid = 'S-1-5-21-944213604-1016845418-2036792573-1001';
const storeRoot = 'D:\\PayPlannerCIArtifacts\\store';
const helper = fileURLToPath(new URL('./xsenus-artifact-store.mjs', import.meta.url));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');

function preflight() {
  requireTrustedHost();
  assert.equal(process.env.GITHUB_ACTIONS, 'true');
  assert.equal(process.env.RUNNER_NAME, 'PayPlanner-XSENUS');
  assert.equal(process.env.RUNNER_OS, 'Windows');
  assert.equal(process.env.RUNNER_ARCH, 'X64');
  assert.ok(['frontend', 'deploy'].includes(process.env.GITHUB_JOB));
  assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch');
  assert.equal(process.env.GITHUB_REF, 'refs/heads/claude/practical-bardeen-pglcf3');
  assert.equal(process.env.PAYPLANNER_CI_ARTIFACT_ROOT, storeRoot);
  assert.match(process.env.GITHUB_RUN_ID ?? '', /^[1-9][0-9]+$/);
  assert.match(process.env.GITHUB_RUN_ATTEMPT ?? '', /^[1-9][0-9]*$/);
  assert.match(process.env.GITHUB_SHA ?? '', /^[a-f0-9]{40}$/);
  assert.match(process.env.GITHUB_WORKFLOW_SHA ?? '', /^[a-f0-9]{40}$/);
  assert.equal(sha(readFileSync(helper)), helperSha, 'Frozen native helper differs');
  assert.equal(sha(readFileSync(new URL('./verify-xsenus-runner.mjs', import.meta.url))), guardSha, 'Existing trust guard differs');
  assert.equal(resolve(process.cwd()).toLowerCase(), resolve(process.env.GITHUB_WORKSPACE).toLowerCase());
  const token = spawnSync(join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe'),
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'],
    { encoding: 'utf8', windowsHide: true, timeout: 15000, maxBuffer: 4096 });
  assert.equal(token.status, 0, 'Native SID query failed');
  assert.equal(token.signal, null);
  assert.equal(token.stdout.trim().replace(/^\uFEFF/, ''), expectedSid, 'Actual runner SID differs');
}

function entries() {
  return [
    { name: 'fixture.txt', data: Buffer.from('PayPlanner artifact-store transport fixture. No application or production content.\n') },
    { name: 'binary.dat', data: Buffer.from(Array.from({ length: 256 }, (_, index) => index)) },
    { name: 'probe.json', data: Buffer.from(JSON.stringify({ schema: 1, runId: process.env.GITHUB_RUN_ID,
      headSha: process.env.GITHUB_SHA, workflowSha: process.env.GITHUB_WORKFLOW_SHA, producerAttempt: 1,
      qualifiesCI: false, qualifiesDeployment: false }) + '\n') },
  ];
}

function archive() {
  const chunks = [];
  for (const entry of entries()) {
    const header = Buffer.alloc(512);
    const octal = (value, length) => value.toString(8).padStart(length - 1, '0') + '\0';
    header.write(entry.name, 0, 100, 'ascii');
    header.write(octal(0o600, 8), 100, 8, 'ascii');
    header.write(octal(0, 8), 108, 8, 'ascii'); header.write(octal(0, 8), 116, 8, 'ascii');
    header.write(octal(entry.data.length, 12), 124, 12, 'ascii'); header.write(octal(0, 12), 136, 12, 'ascii');
    header.fill(32, 148, 156); header[156] = 48;
    header.write('ustar\0', 257, 6, 'ascii'); header.write('00', 263, 2, 'ascii');
    const checksum = header.reduce((sum, byte) => sum + byte, 0);
    header.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'ascii');
    chunks.push(header, entry.data, Buffer.alloc((512 - entry.data.length % 512) % 512));
  }
  chunks.push(Buffer.alloc(1024));
  return gzipSync(Buffer.concat(chunks), { level: 9 });
}

function inspectArchive(bytes) {
  assert.ok(bytes.length > 0 && bytes.length < 16384, 'Unexpected fixture archive bound');
  const tar = gunzipSync(bytes, { maxOutputLength: 1024 * 1024 });
  let offset = 0;
  const files = [];
  for (const expected of entries()) {
    const header = tar.subarray(offset, offset + 512);
    assert.equal(header.length, 512);
    const field = (start, length) => header.subarray(start, start + length).toString('ascii').replace(/\0.*$/, '').trim();
    assert.equal(field(0, 100), expected.name);
    assert.equal(header[156], 48, 'Only ordinary file members are allowed');
    assert.equal(field(157, 100), ''); assert.equal(field(345, 155), '');
    assert.equal(field(257, 6), 'ustar'); assert.equal(field(263, 2), '00');
    const checksumHeader = Buffer.from(header); checksumHeader.fill(32, 148, 156);
    assert.equal(parseInt(field(148, 8), 8), checksumHeader.reduce((sum, byte) => sum + byte, 0));
    const length = parseInt(field(124, 12), 8);
    assert.equal(length, expected.data.length);
    offset += 512;
    const actual = tar.subarray(offset, offset + length);
    assert.ok(actual.equals(expected.data), `Fixture bytes differ: ${expected.name}`);
    files.push({ path: expected.name, bytes: length, sha256: sha(actual) });
    offset += length;
    const padding = (512 - length % 512) % 512;
    assert.ok(tar.subarray(offset, offset + padding).every(byte => byte === 0)); offset += padding;
  }
  assert.equal(tar.length - offset, 1024, 'Unexpected extra tar members or trailing payload');
  assert.ok(tar.subarray(offset).every(byte => byte === 0));
  assert.ok(bytes.equals(archive()), 'Whole deterministic archive bytes differ');
  return { archiveBytes: bytes.length, archiveSha256: sha(bytes), files };
}

function nativeGet(destination) {
  const command = [helper, 'get', '--kind', 'frontend', '--producer-job', 'frontend', '--destination', destination];
  const child = spawnSync(process.execPath, command, { encoding: 'utf8', windowsHide: true,
    timeout: 120000, maxBuffer: 1024 * 1024 });
  const evidence = { nativeExitCode: child.status, signal: child.signal, stdout: child.stdout ?? '', stderr: child.stderr ?? '' };
  writeFileSync(`probe-get-native-attempt-${process.env.GITHUB_RUN_ATTEMPT}.json`, JSON.stringify(evidence, null, 2) + '\n', { flag: 'wx' });
  assert.equal(child.status, 0, 'Actual helper CLI get failed; see native receipt');
  assert.equal(child.signal, null);
  return JSON.parse(child.stdout.trim().replace(/^\uFEFF/, ''));
}

try {
  assert.equal(process.argv.length, 3);
  const mode = process.argv[2];
  assert.ok(['preflight', 'create', 'consume'].includes(mode));
  preflight();
  let result;
  if (mode === 'create') {
    assert.equal(process.env.GITHUB_JOB, 'frontend');
    assert.equal(process.env.GITHUB_RUN_ATTEMPT, '1', 'Producer init/put must never rerun');
    const bytes = archive();
    result = inspectArchive(bytes);
    writeFileSync('frontend.tar.gz', bytes, { flag: 'wx' });
    assert.ok(readFileSync('frontend.tar.gz').equals(bytes));
  } else if (mode === 'consume') {
    assert.equal(process.env.GITHUB_JOB, 'deploy');
    assert.ok(['1', '2'].includes(process.env.GITHUB_RUN_ATTEMPT), 'Only initial consumer and one explicit rerun are allowed');
    const needs = JSON.parse(process.env.ARTIFACT_NEEDS_JSON);
    assert.deepEqual(Object.keys(needs), ['frontend']);
    assert.equal(needs.frontend.result, 'success');
    assert.equal(needs.frontend.outputs['frontend-artifact-id'], process.env.EXPECTED_ARTIFACT_ID);
    assert.match(process.env.EXPECTED_ARTIFACT_ID, /^xsenus-local-v1:[a-f0-9]{64}$/);
    const destination = `probe-retrieved-attempt-${process.env.GITHUB_RUN_ATTEMPT}`;
    const retrieved = nativeGet(destination);
    assert.equal(retrieved.id, process.env.EXPECTED_ARTIFACT_ID);
    assert.equal(retrieved.receipt.producerAttempt, 1);
    assert.equal(retrieved.receipt.producerJob, 'frontend');
    assert.equal(retrieved.receipt.runId, process.env.GITHUB_RUN_ID);
    assert.equal(retrieved.receipt.headSha, process.env.GITHUB_SHA);
    assert.equal(retrieved.receipt.workflowSha, process.env.GITHUB_WORKFLOW_SHA);
    assert.deepEqual(readdirSync(destination), ['frontend.tar.gz']);
    const path = join(destination, 'frontend.tar.gz'), info = lstatSync(path);
    assert.ok(info.isFile() && !info.isSymbolicLink() && info.nlink === 1);
    result = { ...inspectArchive(readFileSync(path)), artifactId: retrieved.id,
      actualConsumerAttempt: Number(process.env.GITHUB_RUN_ATTEMPT), actualProducerAttempt: retrieved.receipt.producerAttempt,
      getNativeExitCode: 0, getSignal: null, helperReceipt: retrieved.receipt };
  } else result = { helperSha256: helperSha, guardSha256: guardSha, actualSid: expectedSid };
  const receipt = { status: 'passed', scope: 'Real local fixture transport only; no application/release qualification', mode,
    runId: process.env.GITHUB_RUN_ID, runAttempt: process.env.GITHUB_RUN_ATTEMPT, headSha: process.env.GITHUB_SHA,
    job: process.env.GITHUB_JOB, qualifiesCI: false, qualifiesDeployment: false, priorSymlinkFailureUnchanged: 1314, ...result };
  if (mode !== 'preflight') writeFileSync(`probe-${mode}-receipt-attempt-${process.env.GITHUB_RUN_ATTEMPT}.json`, JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx' });
  console.log(JSON.stringify(receipt, null, 2));
} catch (error) {
  console.error(JSON.stringify({ status: 'failed', scope: 'Local fixture probe', error: error.message,
    qualifiesCI: false, qualifiesDeployment: false }));
  process.exitCode = 1;
}
