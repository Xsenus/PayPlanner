import assert from 'node:assert/strict';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const batchPattern = /^(fast|heavy|exclusive)-[0-9]+$/;
const eventKinds = new Set(['initial-budget', 'lane-budget', 'launch-budget', 'budget-change', 'memory-floor', 'admission-wait', 'admission-ready', 'admission-timeout', 'started', 'native-exit']);
const errorKinds = new Set(['InvalidDataException', 'IOException', 'Win32Exception', 'InvalidOperationException',
  'UnauthorizedAccessException', 'OutOfMemoryException', 'TaskCanceledException', 'OperationCanceledException', 'JsonException']);
const numberFields = ['BatchCount', 'AdmittedWorkers', 'AvailableBytes', 'Processors', 'Workers', 'Active', 'Cases', 'ExitCode', 'VstestPeakWorkingSetBytes'];
const uploadFailureStages = new Set(['separate-version-response', 'separate-writ-response',
  'separate-document-response', 'separate-document-version-response']);
const retirementPhases = new Set(['class-dependency-admission', 'source-key-cold', 'production-build',
  'asset-publish-hash', 'source-key-after-build', 'source-key-hit', 'asset-before-case',
  'preview-listen', 'browser-open', 'browser-goto', 'browser-snapshot', 'scenario',
  'source-key-after-case', 'asset-after-case', 'browser-close', 'preview-close',
  'class-dependency-finalization', 'class-source-finalization', 'class-asset-finalization']);
const retirementFailureCodes = [
  ['Input or artifact must be an ordinary file', 'non-ordinary-file'],
  ['Input or artifact root must be an ordinary directory', 'non-ordinary-directory'],
  ['Unowned input link', 'unowned-input-link'],
  ['Non-ordinary root configuration', 'non-ordinary-root-configuration'],
  ['Dependency/tool drift invalidates the entire class', 'dependency-tool-drift'],
  ['Production artifact was replaced, extended or truncated', 'asset-drift'],
  ['A required entry or notification chunk is missing', 'required-asset-missing'],
  ['Inputs changed during production build', 'build-input-drift'],
  ['Source/config/environment changed between cases', 'case-input-drift'],
  ['Partial build cannot be retried', 'partial-build'],
  ['Faulted class owner cannot build or qualify another case', 'faulted-node-owner'],
  ['Invalid bundle root.', 'invalid-bundle-root'],
  ['Bundle ownership changed.', 'bundle-owner-changed'],
  ['Bundle ownership cannot follow a reparse point.', 'bundle-reparse-point'],
  ['The class-owned API address is busy or unproven.', 'bundle-address-unproven'],
  ['A failed or interrupted class build cannot admit another runtime.', 'failed-build-admission'],
  ['Bundle address was not safely returned.', 'bundle-address-not-returned'],
  ['A bundle child has not released its lease.', 'bundle-lease-not-released'],
  ['Failed build owner is preserved for inspection:', 'failed-build-preserved'],
  ['Class bundle inputs changed or could not be verified.', 'class-finalization-failed'],
  ['Project root is missing.', 'project-root-missing'],
  ['Synthetic writable backend failed before listening:', 'backend-startup-failed'],
  ['Owned restarted backend exited before listening', 'backend-restart-failed'],
  ['Browser fixture could not start.', 'browser-process-not-started'],
  ['AssertionError [ERR_ASSERTION]', 'node-assertion-failed'],
  ["code: 'MODULE_NOT_FOUND'", 'node-module-not-found'],
  ["code: 'ERR_MODULE_NOT_FOUND'", 'node-module-not-found'],
  ["code: 'ERR_DLOPEN_FAILED'", 'node-native-module-load-failed'],
  ['The browser never reached the owned boundary.', 'browser-boundary-not-reached'],
  ['Unavailable proof released the identity', 'unavailable-proof-identity-released'],
  ['Unavailable proof lost unsaved fields', 'unavailable-proof-draft-lost'],
  ['Ordinary polling released a retired identity', 'polling-released-identity'],
  ['The forced outage did not exercise explicit unresolved-proof recovery', 'outage-recovery-not-exercised'],
  ['A refused proof was silently accepted', 'refused-proof-accepted'],
  ['Confirmed retirement retained its browser identity', 'confirmed-identity-retained'],
];
const retirementExceptionKinds = ['System.IO.IOException', 'System.IO.FileNotFoundException',
  'System.IO.DirectoryNotFoundException', 'System.UnauthorizedAccessException', 'System.TimeoutException',
  'System.OperationCanceledException', 'System.Threading.Tasks.TaskCanceledException',
  'System.OutOfMemoryException', 'System.ComponentModel.Win32Exception', 'System.Net.Sockets.SocketException',
  'Microsoft.Data.Sqlite.SqliteException', 'Xunit.Sdk.TrueException', 'Xunit.Sdk.EqualException'];

function retirementDiagnostics(text) {
  // Observed signals only, not a root-cause verdict or a qualification gate.
  // Never reflect exception messages, paths, names, payloads or arbitrary JSON.
  const phases = new Map();
  const marker = 'CASE_RETIREMENT_BUNDLE_PHASE ';
  let offset = -marker.length;
  let scanned = 0;
  for (; scanned < 256; scanned++) {
    offset = text.indexOf(marker, offset + marker.length);
    if (offset < 0) break;
    const prefix = text.slice(offset + marker.length, offset + marker.length + 1024).replaceAll('&quot;', '"');
    try {
      const value = JSON.parse(prefix.slice(0, prefix.indexOf('}') + 1));
      if (!retirementPhases.has(value.phase) || typeof value.completed !== 'boolean'
        || !Number.isFinite(value.elapsedMs) || value.elapsedMs < 0 || value.elapsedMs > 3_600_000) continue;
      // TRX can repeat a marker in ErrorInfo and stdout. These are marker
      // occurrences, never a count of distinct cases or canonical outcomes.
      const row = phases.get(value.phase) ?? { phase: value.phase, completedMarkers: 0, failedMarkers: 0, maximumElapsedMs: 0 };
      row[value.completed ? 'completedMarkers' : 'failedMarkers']++;
      row.maximumElapsedMs = Math.max(row.maximumElapsedMs, Math.ceil(value.elapsedMs));
      phases.set(value.phase, row);
    } catch { /* Malformed diagnostic text never becomes public output or success evidence. */ }
  }
  return {
    observedFailureCodes: [...new Set(retirementFailureCodes.filter(([message]) => text.includes(message)).map(([, code]) => code))].sort(),
    observedExceptionKinds: retirementExceptionKinds.filter(kind => text.includes(kind)),
    phases: [...phases.values()].sort((a, b) => a.phase < b.phase ? -1 : a.phase > b.phase ? 1 : 0),
    phaseMarkersScanned: scanned,
    phaseMarkersTruncated: scanned === 256 && text.indexOf(marker, offset + marker.length) >= 0,
  };
}

function readJson(path) {
  try {
    const info = lstatSync(path);
    assert.ok(info.isFile() && !info.isSymbolicLink() && info.size <= 8 * 1024 * 1024);
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw new Error('Execution metadata is unreadable or unsafe');
  }
}

function directory(path) {
  const info = lstatSync(path);
  assert.ok(info.isDirectory() && !info.isSymbolicLink(), 'Execution directory must be ordinary');
}

function trxSummary(path) {
  let text;
  try {
    const info = lstatSync(path);
    assert.ok(info.isFile() && !info.isSymbolicLink() && info.size <= 64 * 1024 * 1024);
    text = readFileSync(path, 'utf8');
  } catch (error) {
    if (error.code === 'ENOENT') return { available: false };
    throw new Error('Test result metadata is unreadable or unsafe');
  }
  const counts = {}, unsuccessfulIds = [];
  // Diagnostic extraction only; the existing native verifier still qualifies
  // the complete XML and exact case union. Never reflect names or error text.
  for (const match of text.matchAll(/<UnitTestResult\b([^>]{0,65536})>/g)) {
    const outcome = /\boutcome="([A-Za-z]+)"/.exec(match[1])?.[1];
    const id = /\btestId="([a-fA-F0-9-]{36})"/.exec(match[1])?.[1];
    if (!['Passed', 'Failed', 'NotExecuted', 'Timeout', 'Aborted', 'Inconclusive', 'Error', 'NotRunnable', 'Warning', 'Pending'].includes(outcome)) continue;
    counts[outcome] = (counts[outcome] ?? 0) + 1;
    if (outcome !== 'Passed' && id) unsuccessfulIds.push(id);
  }
  const browserFailureStages = new Set();
  if (text.includes('CASE_RETIREMENT_BUNDLE_TEMP_ROOT_MISMATCH')) browserFailureStages.add('temporary-root-mismatch');
  const marker = 'CASE_RETIREMENT_UPLOAD_WAIT ';
  let offset = -marker.length;
  // Emit only fixed stage codes, never the diagnostic object, paths or errors.
  for (let captured = 0; captured < 100; captured++) {
    offset = text.indexOf(marker, offset + marker.length);
    if (offset < 0) break;
    const prefix = text.slice(offset + marker.length, offset + marker.length + 200).replaceAll('&quot;', '"');
    const stage = /^\{"phase":"([^"\r\n]{1,64})"/.exec(prefix)?.[1];
    if (uploadFailureStages.has(stage)) browserFailureStages.add(stage);
  }
  return { available: true, capturedOutcomes: counts, unsuccessfulIds: unsuccessfulIds.slice(0, 100),
    browserFailureStages: [...browserFailureStages].sort(),
    retirementDiagnostics: retirementDiagnostics(text),
    omittedIds: Math.max(0, unsuccessfulIds.length - 100) };
}

// Report only typed scheduler metadata. Never print test stdout/stderr, exception
// messages, configuration, environment variables, or arbitrary JSON properties.
export function executionReport(resultsRoot, shard) {
  assert.ok(Number.isInteger(shard) && shard >= 0 && shard <= 3, 'Invalid shard');
  directory(resultsRoot);
  const root = join(resultsRoot, `shard-${shard}`);
  try { directory(root); } catch (error) {
    if (error.code === 'ENOENT') return { shard, available: false };
    throw error;
  }
  const execution = readJson(join(root, 'execution.json'));
  const events = (Array.isArray(execution?.Events) ? execution.Events : []).flatMap(event => {
    if (!eventKinds.has(event?.Kind)) return [];
    const row = { Kind: event.Kind };
    if (batchPattern.test(event.Id ?? '')) row.Id = event.Id;
    if (['fast', 'heavy', 'exclusive'].includes(event.Lane)) row.Lane = event.Lane;
    for (const field of numberFields) if (Number.isSafeInteger(event[field])) row[field] = event[field];
    return [row];
  });
  const batches = [];
  const batchRoot = join(root, 'batches');
  try {
    directory(batchRoot);
    for (const name of readdirSync(batchRoot).sort()) {
      assert.match(name, batchPattern);
      directory(join(batchRoot, name));
      const run = readJson(join(batchRoot, name, 'run.json'));
      batches.push({ batch: name, metadataPresent: run !== null,
        exitCode: Number.isSafeInteger(run?.ExitCode) ? run.ExitCode : null,
        errorKind: errorKinds.has(run?.ErrorKind) ? run.ErrorKind : run?.ErrorKind ? 'other' : null,
        results: trxSummary(join(batchRoot, name, 'results.trx')),
        qualified: readJson(join(batchRoot, name, 'qualification.json'))?.Qualified === true });
    }
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  return { shard, available: true, events, batches };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    assert.match(process.argv[2] ?? '', /^[0-3]$/);
    assert.equal(process.argv.length, 3);
    console.log(JSON.stringify(executionReport(resolve('test-results'), Number(process.argv[2]))));
  } catch {
    console.error('Backend execution report unavailable; retained artifacts remain authoritative');
    process.exitCode = 1;
  }
}
