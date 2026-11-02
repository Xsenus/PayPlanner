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

test('TRX diagnostics expose outcomes, IDs and fixed stages, never test names or error text', t => {
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

test('browser failure diagnostics expose only allowlisted stage codes', t => {
  const f = fixture(t), id = '12345678-1234-1234-1234-123456789abc';
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'),
    `<TestRun><Results><UnitTestResult testId="${id}" outcome="Failed" testName="secret-name"><Output><ErrorInfo><Message>`
    + 'secret-error CASE_RETIREMENT_BUNDLE_TEMP_ROOT_MISMATCH\n'
    + 'CASE_RETIREMENT_UPLOAD_WAIT {"phase":"separate-writ-response","password":"secret-password","filePosts":123}\n'
    + 'CASE_RETIREMENT_UPLOAD_WAIT {&quot;phase&quot;:&quot;separate-version-response&quot;,&quot;secret&quot;:true}\n'
    + 'CASE_RETIREMENT_UPLOAD_WAIT {"phase":"secret-unknown-stage"}\n'
    + 'CASE_RETIREMENT_UPLOAD_WAIT {"phase":"separate-writ-response"}\n'
    + '</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>');
  const report = executionReport(f.root, 0);
  assert.deepEqual(report.batches[0].results.browserFailureStages,
    ['separate-version-response', 'separate-writ-response', 'temporary-root-mismatch']);
  assert.deepEqual(report.batches[0].results.capturedOutcomes, { Failed: 1 });
  assert.deepEqual(report.batches[0].results.unsuccessfulIds, [id]);
  assert.equal(report.batches[0].qualified, false);
  assert.equal(JSON.stringify(report).includes('secret'), false);
  assert.equal(JSON.stringify(report).includes('password'), false);
  assert.equal(JSON.stringify(report).includes('filePosts'), false);
});

function retirementFixture(t, text) {
  const f = fixture(t);
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'), `<TestRun><Output>${text}</Output></TestRun>`);
  return executionReport(f.root, 0).batches[0];
}

test('retirement diagnostics retain fixed failure codes and aggregate completed and failed phases privately', t => {
  const phase = value => 'CASE_RETIREMENT_BUNDLE_PHASE ' + JSON.stringify(value) + '\n';
  const batch = retirementFixture(t, 'private-error C:\\private-path\\secret\n'
    + 'Unavailable proof released the identity\nUnavailable proof released the identity\n'
    + 'Class bundle inputs changed or could not be verified. private-finalization-detail\n'
    + phase({ phase: 'scenario', elapsedMs: 10000.2, completed: false, secret: 'private-password' })
    + phase({ phase: 'scenario', elapsedMs: 125, completed: true })
    + phase({ phase: 'production-build', elapsedMs: 45.1, completed: true }));
  assert.deepEqual(batch.results.retirementDiagnostics, {
    observedFailureCodes: ['class-finalization-failed', 'unavailable-proof-identity-released'],
    observedExceptionKinds: [],
    phases: [
      { phase: 'production-build', completedMarkers: 1, failedMarkers: 0, maximumElapsedMs: 46 },
      { phase: 'scenario', completedMarkers: 1, failedMarkers: 1, maximumElapsedMs: 10001 },
    ],
    phaseMarkersScanned: 3,
    phaseMarkersTruncated: false,
  });
  assert.equal(batch.qualified, false);
  const serialized = JSON.stringify(batch);
  for (const secret of ['private', 'password', 'secret', 'Class bundle inputs', 'Unavailable proof released'])
    assert.equal(serialized.includes(secret), false, secret);
});

test('retirement diagnostics accept XML-escaped markers but reject arbitrary phases, invalid values and malformed text', t => {
  const phase = value => 'CASE_RETIREMENT_BUNDLE_PHASE ' + JSON.stringify(value).replaceAll('"', '&quot;') + '\n';
  const batch = retirementFixture(t,
    phase({ phase: 'class-dependency-finalization', elapsedMs: 3_600_000, completed: false })
    + phase({ phase: 'private-unknown-phase', elapsedMs: 1, completed: true })
    + phase({ phase: 'scenario', elapsedMs: -1, completed: true })
    + phase({ phase: 'scenario', elapsedMs: 3_600_001, completed: true })
    + phase({ phase: 'scenario', elapsedMs: 'private-time', completed: true })
    + phase({ phase: 'scenario', elapsedMs: 1, completed: 'private-state' })
    + 'CASE_RETIREMENT_BUNDLE_PHASE {"phase":"scenario","elapsedMs":1e309,"completed":true}\n'
    + 'CASE_RETIREMENT_BUNDLE_PHASE private-malformed-json\n'
    + 'CASE_RETIREMENT_BUNDLE_PHASE null\n');
  assert.deepEqual(batch.results.retirementDiagnostics.phases, [
    { phase: 'class-dependency-finalization', completedMarkers: 0, failedMarkers: 1, maximumElapsedMs: 3_600_000 },
  ]);
  assert.equal(batch.results.retirementDiagnostics.phaseMarkersScanned, 9);
  assert.equal(JSON.stringify(batch).includes('private'), false);
  assert.equal(batch.qualified, false);
});

test('retirement diagnostic scanning has a fixed bound and cannot promote a batch to qualified', t => {
  const marker = 'CASE_RETIREMENT_BUNDLE_PHASE {"phase":"scenario","elapsedMs":1,"completed":true}\n';
  const batch = retirementFixture(t, marker.repeat(300));
  assert.deepEqual(batch.results.retirementDiagnostics, {
    observedFailureCodes: [],
    observedExceptionKinds: [],
    phases: [{ phase: 'scenario', completedMarkers: 256, failedMarkers: 0, maximumElapsedMs: 1 }],
    phaseMarkersScanned: 256,
    phaseMarkersTruncated: true,
  });
  assert.equal(batch.qualified, false);
  assert.deepEqual(batch.results.capturedOutcomes, {});
});

test('fixed failure signals distinguish ownership, input drift and missing browser boundary without reflecting suffixes', t => {
  const batch = retirementFixture(t,
    'Bundle ownership changed. private-owner\n'
    + 'Source/config/environment changed between cases private-environment\n'
    + 'Dependency/tool drift invalidates the entire class private-dependency\n'
    + 'The browser never reached the owned boundary. private-process\n'
    + 'UnknownException: private-stack\n');
  assert.deepEqual(batch.results.retirementDiagnostics.observedFailureCodes,
    ['browser-boundary-not-reached', 'bundle-owner-changed', 'case-input-drift', 'dependency-tool-drift']);
  assert.equal(JSON.stringify(batch).includes('private'), false);
  assert.equal(JSON.stringify(batch).includes('UnknownException'), false);
});

test('startup signals and known exception types remain fixed codes without error details', t => {
  const batch = retirementFixture(t, 'Synthetic writable backend failed before listening: private-server-log\n'
    + 'System.IO.FileNotFoundException: private-runtime-path\n'
    + 'Microsoft.Data.Sqlite.SqliteException: private-database-content\n'
    + 'Private.CustomException: private-secret\n');
  assert.deepEqual(batch.results.retirementDiagnostics.observedFailureCodes, ['backend-startup-failed']);
  assert.deepEqual(batch.results.retirementDiagnostics.observedExceptionKinds,
    ['System.IO.FileNotFoundException', 'Microsoft.Data.Sqlite.SqliteException']);
  assert.equal(JSON.stringify(batch).includes('private'), false);
  assert.equal(JSON.stringify(batch).includes('CustomException'), false);
  assert.equal(batch.qualified, false);
});

test('Node runtime and assertion codes are deduplicated without publishing module names or diffs', t => {
  const batch = retirementFixture(t, "AssertionError [ERR_ASSERTION]: private-input-diff\n"
    + "code: 'MODULE_NOT_FOUND', path: 'private-module-name'\n"
    + "code: 'ERR_MODULE_NOT_FOUND', url: 'private-module-url'\n"
    + "code: 'ERR_DLOPEN_FAILED', details: 'private-native-path'\n");
  assert.deepEqual(batch.results.retirementDiagnostics.observedFailureCodes,
    ['node-assertion-failed', 'node-module-not-found', 'node-native-module-load-failed']);
  assert.equal(JSON.stringify(batch).includes('private'), false);
  assert.equal(batch.qualified, false);
});

test('failed case entries retain bounded duration and fixed kinds without names, SQL or messages', t => {
  const f = fixture(t), id = '12345678-1234-1234-1234-123456789abc';
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'), '<TestRun><Results>'
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="00:03:41.5807146" testName="private-name">`
    + '<Output><ErrorInfo><Message>Npgsql.PostgresException: private-sql Checkpoint writer readiness timed out.</Message>'
    + '</ErrorInfo></Output></UnitTestResult>'
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="00:00:00.0010000">`
    + '<Output><ErrorInfo><Message>System.IO.IOException: A bundle child has not released its lease. private-path</Message>'
    + '</ErrorInfo></Output></UnitTestResult></Results></TestRun>');
  const batch = executionReport(f.root, 0).batches[0];
  assert.deepEqual(batch.results.unsuccessfulCaseDetails, [
    { id, outcome: 'Failed', durationMs: 221581, boundedBodyAvailable: true,
      observedExceptionKinds: ['Npgsql.PostgresException'], observedFailureCodes: ['storage-checkpoint-readiness-timeout'] },
    { id, outcome: 'Failed', durationMs: 1, boundedBodyAvailable: true,
      observedExceptionKinds: ['System.IO.IOException'], observedFailureCodes: ['bundle-lease-not-released'] },
  ]);
  assert.deepEqual(batch.results.capturedOutcomes, { Failed: 2 });
  assert.equal(batch.qualified, false);
  assert.equal(JSON.stringify(batch).includes('private'), false);
});

test('failed case details bound entries and reject invalid durations and arbitrary exception kinds', t => {
  const f = fixture(t), id = '12345678-1234-1234-1234-123456789abc';
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'), '<TestRun><Results>'
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="99:00:00" testName="private-name">`
      .concat('<Output>Private.Exception: private-message</Output></UnitTestResult>').repeat(150)
    + '</Results></TestRun>');
  const batch = executionReport(f.root, 0).batches[0];
  assert.equal(batch.results.unsuccessfulCaseDetails.length, 100);
  assert.equal(batch.results.omittedIds, 50);
  assert.equal(batch.results.unsuccessfulCaseDetails.every(row => row.durationMs === null
    && row.observedExceptionKinds.length === 0 && row.observedFailureCodes.length === 0), true);
  assert.equal(batch.qualified, false);
  assert.equal(JSON.stringify(batch).includes('private'), false);
});

test('missing or oversized case bodies never borrow diagnostics from the next failed case', t => {
  const f = fixture(t), id = '12345678-1234-1234-1234-123456789abc';
  mkdirSync(join(f.shard, 'batches/exclusive-0'), { recursive: true });
  writeFileSync(join(f.shard, 'batches/exclusive-0/results.trx'), '<TestRun><Results>'
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="private-time" />`
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="00:00:01">`
    + 'private'.repeat(11_000) + 'Npgsql.NpgsqlException</UnitTestResult>'
    + `<UnitTestResult testId="${id}" outcome="Failed" duration="00:00:02">`
    + 'System.TimeoutException: private-details</UnitTestResult></Results></TestRun>');
  const batch = executionReport(f.root, 0).batches[0];
  assert.deepEqual(batch.results.unsuccessfulCaseDetails.map(row => ({ durationMs: row.durationMs,
    boundedBodyAvailable: row.boundedBodyAvailable, observedExceptionKinds: row.observedExceptionKinds })), [
    { durationMs: null, boundedBodyAvailable: true, observedExceptionKinds: [] },
    { durationMs: 1000, boundedBodyAvailable: false, observedExceptionKinds: [] },
    { durationMs: 2000, boundedBodyAvailable: true, observedExceptionKinds: ['System.TimeoutException'] },
  ]);
  assert.equal(batch.qualified, false);
  assert.equal(JSON.stringify(batch).includes('private'), false);
});
