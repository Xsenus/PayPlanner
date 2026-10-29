import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { executionReport } from './report-backend-execution.mjs';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-execution-report-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const shard = join(root, 'shard-0'); mkdirSync(shard);
  const write = (name, value) => writeFileSync(join(shard, name), JSON.stringify(value));
  return { root, shard, write };
}

test('reports memory floor and native batch failure without leaking free text', t => {
  const f = fixture(t);
  f.write('execution.json', { secret: 'secret-config', Events: [
    { Kind: 'memory-floor', Lane: 'heavy', AvailableBytes: 123, Active: 2, Secret: 'secret-event' },
    { Kind: 'native-exit', Id: 'heavy-0', ExitCode: 137, Message: 'secret-error' },
    { Kind: 'secret-unknown', AvailableBytes: 100 },
  ] });
  mkdirSync(join(f.shard, 'batches/heavy-0'), { recursive: true });
  f.write('batches/heavy-0/run.json', { ExitCode: 137, ErrorKind: 'InvalidDataException', Message: 'secret-run' });
  const report = executionReport(f.root, 0);
  assert.equal(report.events.length, 2);
  assert.equal(report.events[0].AvailableBytes, 123);
  assert.equal(report.batches[0].exitCode, 137);
  assert.equal(report.batches[0].errorKind, 'InvalidDataException');
  assert.equal(JSON.stringify(report).includes('secret'), false);
  assert.equal(report.batches[0].qualified, false);
});

test('distinguishes qualification from native success and missing metadata', t => {
  const f = fixture(t);
  for (const name of ['fast-0', 'heavy-0']) mkdirSync(join(f.shard, 'batches', name), { recursive: true });
  f.write('batches/fast-0/run.json', { ExitCode: 0 });
  f.write('batches/fast-0/qualification.json', { Qualified: true });
  const report = executionReport(f.root, 0);
  assert.equal(report.batches[0].qualified, true);
  assert.equal(report.batches[1].metadataPresent, false);
  assert.equal(report.batches[1].exitCode, null);
});

test('reports bounded admission waiting and timeout without exposing arbitrary fields', t => {
  const f = fixture(t);
  f.write('execution.json', { Events: ['admission-wait', 'admission-ready', 'admission-timeout'].map(Kind =>
    ({ Kind, Lane: 'exclusive', Active: 0, AvailableBytes: 123, Message: 'secret-host-state' })) });
  const report = executionReport(f.root, 0);
  assert.deepEqual(report.events.map(row => row.Kind), ['admission-wait', 'admission-ready', 'admission-timeout']);
  assert.equal(report.events.every(row => row.AvailableBytes === 123 && row.Active === 0), true);
  assert.equal(JSON.stringify(report).includes('secret'), false);
});

test('missing shard is reported without claiming successful execution', t => {
  const f = fixture(t);
  assert.deepEqual(executionReport(f.root, 1), { shard: 1, available: false });
  assert.throws(() => executionReport(f.root, -1));
});

test('malformed JSON is rejected without reflecting its content', t => {
  const f = fixture(t);
  writeFileSync(join(f.shard, 'execution.json'), 'private configuration');
  assert.throws(() => executionReport(f.root, 0), /^Error: Execution metadata is unreadable or unsafe$/);
});

test('TRX diagnostics expose only outcomes and IDs, never test names or error text', t => {
  const f = fixture(t), id = '12345678-1234-1234-1234-123456789abc';
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'),
    `<TestRun><Results><UnitTestResult testId="${id}" outcome="Failed" testName="secret-name"><Output><ErrorInfo><Message>secret-message</Message></ErrorInfo></Output></UnitTestResult>`
    + `<UnitTestResult testId="${id}" outcome="Passed" /></Results></TestRun>`);
  const report = executionReport(f.root, 0);
  assert.deepEqual(report.batches[0].results.capturedOutcomes, { Failed: 1, Passed: 1 });
  assert.deepEqual(report.batches[0].results.unsuccessfulIds, [id]);
  assert.equal(report.batches[0].qualified, false);
  assert.equal(JSON.stringify(report).includes('secret'), false);
});
