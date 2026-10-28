import assert from 'node:assert/strict';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const batchPattern = /^(fast|heavy|exclusive)-[0-9]+$/;
const eventKinds = new Set(['initial-budget', 'lane-budget', 'launch-budget', 'budget-change', 'memory-floor', 'admission-wait', 'admission-ready', 'admission-timeout', 'started', 'native-exit']);
const errorKinds = new Set(['InvalidDataException', 'IOException', 'Win32Exception', 'InvalidOperationException',
  'UnauthorizedAccessException', 'OutOfMemoryException', 'TaskCanceledException', 'OperationCanceledException', 'JsonException']);
const numberFields = ['BatchCount', 'AdmittedWorkers', 'AvailableBytes', 'Processors', 'Workers', 'Active', 'Cases', 'ExitCode', 'VstestPeakWorkingSetBytes'];

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
  return { available: true, capturedOutcomes: counts, unsuccessfulIds: unsuccessfulIds.slice(0, 100),
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
