import { mkdir, open, lstat, rename, unlink } from 'node:fs/promises';
import { constants } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomUUID, createHash } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

const sources = new Set(['task-files', 'case-documents', 'internal-chat', 'messenger-snapshots',
  'electronic-signatures', 'electronic-signature-originals', 'profile-photos']);
const states = new Set(['ready', 'registered', 'alreadyRegistered', 'missing', 'invalid']);
const maximumResponseBytes = 8 * 1024 * 1024;

function integer(value, minimum, maximum, name) {
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < minimum || number > maximum)
    throw new Error(`Invalid ${name}.`);
  return number;
}

export function parseOptions(args) {
  const options = { apply: false, resume: false, batchSize: 100, afterId: 0, maxBatches: 100, intervalMs: 1000 };
  const values = { '--url': 'url', '--source': 'source', '--output': 'output', '--batch-size': 'batchSize',
    '--after-id': 'afterId', '--end-id': 'endId', '--max-batches': 'maxBatches', '--interval-ms': 'intervalMs', '--inventory': 'inventory', '--phase': 'phase' };
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === '--apply') options.apply = true;
    else if (arg === '--resume') options.resume = true;
    else if (values[arg] && args[index + 1] && !args[index + 1].startsWith('--')) options[values[arg]] = args[++index];
    else throw new Error('Unknown or incomplete option. Use --help; credentials must not be passed as arguments.');
  }
  return normalizeOptions(options);
}

function normalizeOptions(options) {
  const phase = options.phase ?? 'catalog-registration';
  if (!['catalog-registration', 'replicas'].includes(phase)) throw new Error('Unknown migration phase.');
  const url = new URL(options.url);
  const loopback = ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname);
  if (url.username || url.password || url.search || url.hash || (url.protocol !== 'https:' && !(url.protocol === 'http:' && loopback)))
    throw new Error('Use HTTPS, or HTTP on loopback, without credentials, query or fragment.');
  if (!sources.has(options.source)) throw new Error('Unknown legacy source.');
  if (typeof options.output !== 'string' || !options.output.trim()) throw new Error('An output directory is required.');
  if (options.apply && (typeof options.inventory !== 'string' || !options.inventory.trim()))
    throw new Error('Apply requires --inventory pointing to a completed dry-run directory.');
  const afterId = integer(options.afterId ?? 0, 0, Number.MAX_SAFE_INTEGER, 'after ID');
  const endId = options.endId === null || options.endId === undefined ? null : integer(options.endId, afterId, Number.MAX_SAFE_INTEGER, 'end ID');
  return { ...options, phase, url: url.href.replace(/\/$/u, ''), output: resolve(options.output),
    apply: options.apply === true, resume: options.resume === true,
    batchSize: integer(options.batchSize ?? 100, 1, phase === 'replicas' ? 100 : 500, 'batch size'),
    afterId, endId,
    maxBatches: integer(options.maxBatches ?? 100, 1, 100000, 'batch limit'),
    intervalMs: integer(options.intervalMs ?? 1000, 0, 60000, 'request interval') };
}

async function existingRegularFile(path, flags) {
  const info = await lstat(path);
  if (!info.isFile() || info.isSymbolicLink()) throw new Error('Report artifact is not a regular file.');
  const handle = await open(path, flags | (constants.O_NOFOLLOW ?? 0));
  const actual = await handle.stat();
  if (actual.dev !== info.dev || actual.ino !== info.ino || actual.nlink !== 1 || !actual.isFile()) {
    await handle.close(); throw new Error('Report artifact changed or is linked elsewhere.');
  }
  return handle;
}

async function loadCheckpoint(path) {
  const file = await existingRegularFile(path, constants.O_RDONLY);
  try {
    // Read a bounded buffer even if the file grows after stat.
    const buffer = Buffer.alloc(65537);
    const { bytesRead } = await file.read(buffer, 0, buffer.length, 0);
    if (bytesRead > 65536) throw new Error('Invalid checkpoint size.');
    try { return JSON.parse(buffer.subarray(0, bytesRead).toString('utf8')); }
    catch { throw new Error('Checkpoint is not valid JSON.'); }
  } finally { await file.close(); }
}

async function boundedJson(response) {
  if (!response.body) throw new Error('Empty API response.');
  const reader = response.body.getReader();
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maximumResponseBytes) throw new Error('API response exceeds the allowed size.');
      chunks.push(value);
    }
    try { return JSON.parse(Buffer.concat(chunks, size).toString('utf8')); }
    catch { throw new Error('API response is not valid JSON.'); }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

function validateReport(report, scope, afterId) {
  if (!report || report.source !== scope.source || report.applied !== scope.apply || report.afterId !== afterId
    || (report.endId ?? null) !== (scope.endId ?? null)
    || typeof report.hasMore !== 'boolean' || !Array.isArray(report.items) || report.items.length > scope.batchSize
    || report.scanned !== report.items.length) throw new Error('API response does not match the requested batch.');
  let previous = afterId;
  let bytes = 0;
  const counts = { ready: 0, registered: 0, alreadyRegistered: 0, missing: 0, invalid: 0 };
  for (const item of report.items) {
    integer(item.sourceId, previous + 1, Number.MAX_SAFE_INTEGER, 'reported source ID');
    if (scope.endId != null && item.sourceId > scope.endId) throw new Error('API returned an out-of-scope document.');
    integer(item.sizeBytes, 0, Number.MAX_SAFE_INTEGER, 'reported size');
    if (!states.has(item.status) || (!scope.apply && item.status === 'registered') || (scope.apply && item.status === 'ready'))
      throw new Error('Unexpected API item status.');
    if (['ready', 'registered', 'alreadyRegistered'].includes(item.status)
      && (!/^[a-f0-9]{64}$/iu.test(item.sha256 ?? '') || !/^[a-f0-9-]{36}$/iu.test(item.objectId ?? '')))
      throw new Error('API item is missing its verified identity.');
    previous = item.sourceId;
    counts[item.status]++;
    if (item.sha256) bytes += item.sizeBytes;
  }
  for (const name of ['registered', 'alreadyRegistered', 'missing', 'invalid'])
    if (report[name] !== counts[name]) throw new Error('API summary does not match its items.');
  if (!Number.isSafeInteger(bytes) || report.bytesVerified !== bytes
    || (report.nextAfterId ?? null) !== (report.items.length ? previous : null)
    || (report.hasMore && (report.items.length === 0 || previous === scope.endId))) throw new Error('API cursor or byte count is inconsistent.');
  return report;
}

async function saveCheckpoint(path, checkpoint) {
  const temporary = `${path}.${randomUUID()}.tmp`;
  try {
    const file = await open(temporary, 'wx', 0o600);
    try { await file.writeFile(JSON.stringify(checkpoint, null, 2) + '\n'); await file.sync(); }
    finally { await file.close(); }
    await rename(temporary, path);
  } finally { await unlink(temporary).catch(error => { if (error.code !== 'ENOENT') throw error; }); }
}

function validateReplicaReport(report, scope, afterId, runId, fingerprint) {
  if (!report || report.source !== scope.source || report.runId !== runId || report.applied !== scope.apply
    || !/^[a-f0-9]{64}$/u.test(report.policyFingerprint ?? '') || (fingerprint && report.policyFingerprint !== fingerprint)
    || typeof report.batchVerified !== 'boolean' || !Array.isArray(report.errors) || !Array.isArray(report.items)
    || report.items.length > scope.batchSize * 20) throw new Error('Replica response does not match the pinned operation.');
  integer(report.queued, 0, scope.batchSize * 40, 'queued operations');
  integer(report.requiredCopies, 1, 20, 'required copies');
  if (!scope.apply && report.queued !== 0) throw new Error('Preview unexpectedly queued writes.');
  validateReport(report.sourceVerification, { ...scope, apply: false }, afterId);
  const sourcesById = new Map(report.sourceVerification.items.map(item => [item.sourceId, item]));
  const identities = new Set();
  const targetCounts = new Map();
  for (const item of report.items) {
    const source = sourcesById.get(item.sourceId);
    const identity = JSON.stringify([item.sourceId, item.providerKey, item.connectionId]);
    if (!source || item.objectId !== source.objectId || identities.has(identity)
      || !['planned', 'pending', 'verified', 'conflict', 'sourceConflict', 'registrationRequired'].includes(item.status)
      || (!scope.apply && item.status === 'pending') || (scope.apply && item.status === 'planned'))
      throw new Error('Replica response has invalid or duplicate identities.');
    identities.add(identity);
    targetCounts.set(item.sourceId, (targetCounts.get(item.sourceId) ?? 0) + 1);
    if (['planned', 'pending', 'verified'].includes(item.status)
      && (typeof item.providerKey !== 'string' || !/^[a-z0-9-]+$/u.test(item.providerKey)
        || typeof item.locator !== 'string' || !item.locator.length
        || !(item.connectionId === null || item.connectionId === undefined || typeof item.connectionId === 'string')))
      throw new Error('Replica destination identity is incomplete.');
  }
  if (report.batchVerified && (report.errors.length || report.sourceVerification.items.some(i => i.status !== 'alreadyRegistered')
    || report.items.some(i => i.status !== 'verified')
    || report.sourceVerification.items.some(source => (targetCounts.get(source.sourceId) ?? 0) < report.requiredCopies)))
    throw new Error('Replica proof is incomplete.');
  return report;
}

function replicaPageIdentity(report) {
  const source = report.sourceVerification;
  const identity = { afterId: source.afterId, nextAfterId: source.nextAfterId, hasMore: source.hasMore,
    sources: source.items.map(i => [i.sourceId, i.objectId, i.ownerType, i.ownerId, i.logicalName,
      i.contentType, i.catalogVersion, i.locator, i.sizeBytes, i.sha256]),
    targets: report.items.map(i => [i.sourceId, i.objectId, i.providerKey, i.connectionId, i.locator])
      .sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b))) };
  return createHash('sha256').update(JSON.stringify(identity)).digest('hex');
}

// Bounded sequential reader: no whole-inventory load or per-page rescanning.
async function* inventoryPages(path, committedBytes) {
  const file = await existingRegularFile(path, constants.O_RDONLY);
  let position = 0, pieces = [], length = 0;
  try {
    while (position < committedBytes) {
      const buffer = Buffer.alloc(Math.min(65536, committedBytes - position));
      const { bytesRead } = await file.read(buffer, 0, buffer.length, position);
      if (!bytesRead) throw new Error('Inventory journal is incomplete.');
      position += bytesRead;
      let start = 0;
      for (let index = 0; index < bytesRead; index++) {
        if (buffer[index] !== 10) continue;
        pieces.push(buffer.subarray(start, index)); length += index - start;
        if (length > maximumResponseBytes + 1024) throw new Error('Inventory report exceeds the allowed size.');
        let entry;
        try { entry = JSON.parse(Buffer.concat(pieces, length).toString('utf8')); }
        catch { throw new Error('Inventory journal is not valid JSON.'); }
        yield entry.report;
        pieces = []; length = 0; start = index + 1;
      }
      pieces.push(buffer.subarray(start, bytesRead)); length += bytesRead - start;
      if (length > maximumResponseBytes + 1024) throw new Error('Inventory report exceeds the allowed size.');
    }
    if (length) throw new Error('Inventory journal has an incomplete committed report.');
  } finally { await file.close(); }
}

function replicaConflict(report) {
  const counts = new Map();
  for (const item of report.items) counts.set(item.sourceId, (counts.get(item.sourceId) ?? 0) + 1);
  return report.errors.length > 0 || report.sourceVerification.items.some(i => i.status !== 'alreadyRegistered')
    || report.items.some(i => !['planned', 'pending', 'verified'].includes(i.status))
    || report.sourceVerification.items.some(source => (counts.get(source.sourceId) ?? 0) < report.requiredCopies);
}

// This phase inventories and registers only. It never declares replica quorum,
// changes product pointers or deletes legacy bytes. Copy/verify is a separate gate.
export async function runBackfill(input, { token, signal, fetchImpl = fetch, onProgress = () => {} } = {}) {
  const options = normalizeOptions(input);
  if (typeof token !== 'string' || !/^[A-Za-z0-9._~-]+$/u.test(token)) throw new Error('Set PAYPLANNER_STORAGE_ADMIN_TOKEN securely in the environment.');
  const replicas = options.phase === 'replicas';
  const scope = { endpoint: `${options.url}/api/storage/${replicas ? 'legacy-replicas' : 'legacy-backfill'}`, source: options.source,
    apply: options.apply, batchSize: options.batchSize, initialAfterId: options.afterId,
    ...(options.endId !== null ? { endId: options.endId } : {}) };
  let inventory;
  let pages;
  if (options.apply) {
    inventory = await loadCheckpoint(join(resolve(options.inventory), 'checkpoint.json'));
    if (inventory.version !== 1 || inventory.phase !== options.phase || inventory.completed !== true
      || inventory.replicaMigrationComplete !== false
      || JSON.stringify(inventory.scope) !== JSON.stringify({ ...scope, apply: false }))
      throw new Error('Apply requires a completed matching dry-run inventory.');
    integer(inventory.afterId, options.afterId, options.endId ?? Number.MAX_SAFE_INTEGER, 'inventory cursor');
    integer(inventory.journalBytes, 0, Number.MAX_SAFE_INTEGER, 'inventory journal size');
    const inventoryJournal = await existingRegularFile(join(resolve(options.inventory), 'batches.jsonl'), constants.O_RDONLY);
    try {
      if ((await inventoryJournal.stat()).size < inventory.journalBytes) throw new Error('Inventory journal is incomplete.');
    } finally { await inventoryJournal.close(); }
    if (replicas && (!/^[a-f0-9]{64}$/u.test(inventory.policyFingerprint ?? '')
      || !/^[a-f0-9-]{36}$/u.test(inventory.runId ?? ''))) throw new Error('Inventory is missing its pinned replica plan.');
  }
  if (!options.resume) await mkdir(options.output, { mode: 0o700 });
  const directory = await lstat(options.output);
  if (!directory.isDirectory() || directory.isSymbolicLink()) throw new Error('Output must be a real directory.');
  const checkpointPath = join(options.output, 'checkpoint.json');
  const lockPath = join(options.output, 'run.lock');
  const lock = await open(lockPath, 'wx', 0o600);
  let journal;
  let checkpoint;
  try {
    await lock.writeFile(JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }));
    if (options.resume) {
      checkpoint = await loadCheckpoint(checkpointPath);
      if (checkpoint.version !== 1 || checkpoint.phase !== options.phase
        || JSON.stringify(checkpoint.scope) !== JSON.stringify(scope) || typeof checkpoint.completed !== 'boolean'
        || typeof checkpoint.replicaMigrationComplete !== 'boolean' || checkpoint.replicaMigrationComplete)
        throw new Error('Checkpoint does not match this operation.');
      integer(checkpoint.afterId, options.afterId, options.endId ?? Number.MAX_SAFE_INTEGER, 'checkpoint cursor');
      integer(checkpoint.journalBytes, 0, Number.MAX_SAFE_INTEGER, 'checkpoint journal size');
      if (replicas && (!/^[a-f0-9-]{36}$/u.test(checkpoint.runId ?? '')
        || (checkpoint.policyFingerprint !== null && !/^[a-f0-9]{64}$/u.test(checkpoint.policyFingerprint ?? ''))
        || (options.apply && (checkpoint.runId !== inventory.runId || checkpoint.policyFingerprint !== inventory.policyFingerprint))))
        throw new Error('Checkpoint lost or changed its pinned replica plan.');
    } else {
      checkpoint = { version: 1, phase: options.phase, scope, afterId: options.afterId,
        completed: false, replicaMigrationComplete: false, journalBytes: 0 };
      if (replicas) Object.assign(checkpoint, { runId: inventory?.runId ?? randomUUID(), policyFingerprint: inventory?.policyFingerprint ?? null });
      await saveCheckpoint(checkpointPath, checkpoint);
    }
    // A fresh run never overwrites an existing journal. Resume may replay its last
    // page if the process stopped after journal fsync but before checkpoint rename.
    journal = options.resume
      ? await existingRegularFile(join(options.output, 'batches.jsonl'), constants.O_RDWR)
      : await open(join(options.output, 'batches.jsonl'), 'wx', 0o600);
    if (options.resume) {
      if ((await journal.stat()).size < checkpoint.journalBytes) throw new Error('Journal is shorter than its checkpoint.');
      // Discard only the uncommitted tail: a killed append may have left partial JSON.
      await journal.truncate(checkpoint.journalBytes);
      await journal.sync();
    }
    if (replicas && options.apply) pages = inventoryPages(join(resolve(options.inventory), 'batches.jsonl'), inventory.journalBytes);
    async function requestReport(apply) {
      signal?.throwIfAborted();
      const requestSignal = signal ? AbortSignal.any([signal, AbortSignal.timeout(120000)]) : AbortSignal.timeout(120000);
      let response;
      try {
        response = await fetchImpl(scope.endpoint, { method: 'POST', redirect: 'manual', signal: requestSignal,
          headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
          body: JSON.stringify({ source: scope.source, afterId: checkpoint.afterId, batchSize: scope.batchSize, apply,
            ...(options.endId !== null ? { endId: options.endId } : {}),
            ...(replicas ? { runId: checkpoint.runId, expectedPolicyFingerprint: checkpoint.policyFingerprint } : {}) }) });
      } catch {
        if (signal?.aborted) signal.throwIfAborted();
        throw new Error('API request failed. The cursor was not advanced; resume the same operation.');
      }
      if (!response.ok) {
        await response.body?.cancel();
        throw new Error(`API returned HTTP ${response.status}. The cursor was not advanced.`);
      }
      const body = await boundedJson(response);
      return replicas ? validateReplicaReport(body, { ...scope, apply }, checkpoint.afterId, checkpoint.runId, checkpoint.policyFingerprint)
        : validateReport(body, scope, checkpoint.afterId);
    }
    for (let batch = 0; !checkpoint.completed && batch < options.maxBatches; batch++) {
      signal?.throwIfAborted();
      if (batch > 0) await delay(options.intervalMs, undefined, { signal });
      let report;
      if (pages) {
        let saved;
        do { saved = await pages.next(); } while (!saved.done && saved.value?.sourceVerification?.afterId < checkpoint.afterId);
        if (saved.done) throw new Error('Inventory has no proof for the requested page.');
        validateReplicaReport(saved.value, { ...scope, apply: false }, checkpoint.afterId, checkpoint.runId, checkpoint.policyFingerprint);
        const preview = await requestReport(false);
        if (replicaConflict(preview) || replicaPageIdentity(saved.value) !== replicaPageIdentity(preview))
          throw new Error('Source or destination changed since inventory; prepare a new preview.');
        report = await requestReport(true);
        if (replicaPageIdentity(preview) !== replicaPageIdentity(report))
          throw new Error('Source or destination changed while applying; the cursor was not advanced.');
      } else report = await requestReport(scope.apply);
      if (replicas) checkpoint = { ...checkpoint, policyFingerprint: report.policyFingerprint };
      const line = JSON.stringify({ attemptId: randomUUID(), at: new Date().toISOString(), report }) + '\n';
      const encoded = Buffer.from(line);
      let written = 0;
      while (written < encoded.byteLength) {
        const { bytesWritten } = await journal.write(encoded, written, encoded.byteLength - written, checkpoint.journalBytes + written);
        if (bytesWritten === 0) throw new Error('Could not persist the report journal.');
        written += bytesWritten;
      }
      await journal.sync();
      checkpoint = { ...checkpoint, journalBytes: checkpoint.journalBytes + Buffer.byteLength(line) };
      const sourceReport = replicas ? report.sourceVerification : report;
      if (sourceReport.missing > 0 || sourceReport.invalid > 0 || (replicas && replicaConflict(report))) {
        await saveCheckpoint(checkpointPath, checkpoint);
        onProgress({ status: 'conflicts', scanned: sourceReport.scanned, afterId: checkpoint.afterId });
        return { status: 'conflicts', checkpoint };
      }
      if (replicas && options.apply && !report.batchVerified) {
        await saveCheckpoint(checkpointPath, checkpoint);
        onProgress({ status: 'pendingCopies', scanned: sourceReport.scanned, afterId: checkpoint.afterId });
        return { status: 'pendingCopies', checkpoint };
      }
      checkpoint = { ...checkpoint, afterId: sourceReport.nextAfterId ?? checkpoint.afterId,
        completed: !sourceReport.hasMore };
      await saveCheckpoint(checkpointPath, checkpoint);
      onProgress({ status: checkpoint.completed ? (replicas ? (options.apply ? 'replicaScopeVerified' : 'replicaPlanComplete') : 'catalogComplete') : 'running', scanned: sourceReport.scanned, afterId: checkpoint.afterId });
    }
    return { status: checkpoint.completed ? (replicas ? (options.apply ? 'replicaScopeVerified' : 'replicaPlanComplete') : 'catalogComplete') : 'batchLimit', checkpoint };
  } finally {
    try { await pages?.return(); }
    finally {
      try { await journal?.close(); }
      finally { try { await lock.close(); } finally { await unlink(lockPath); } }
    }
  }
}

const help = `Legacy storage inventory/catalog registration (not full replica migration).
node scripts/storage-legacy-backfill.mjs --url <CRM origin> --source <source> --output <new directory>
  [--apply --inventory <completed dry-run directory>] [--resume]
  [--batch-size 100] [--after-id 0] [--end-id <inclusive upper ID>] [--max-batches 100] [--interval-ms 1000]
  [--phase catalog-registration|replicas]
Default: dry-run. Apply requires a matching completed inventory and an administrator token in PAYPLANNER_STORAGE_ADMIN_TOKEN.
Conflicts stop before cursor advancement. Reports contain document metadata; restrict directory access.
Resume uses the identical scope and output. End ID is pinned for preview/apply/resume; no tenant filter is supported yet.
Replica mode requires registered sources, uses batches <=100, and pins the preview policy and page identities.
Apply stops with pendingCopies until workers finish; --resume rechecks the same page without advancing early.
Replica scope verification is point-in-time, NOT global failback, product cutover or safe source deletion.`;

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  if (process.argv.slice(2).includes('--help')) console.log(help);
  else {
    const controller = new AbortController();
    process.once('SIGINT', () => controller.abort());
    process.once('SIGTERM', () => controller.abort());
    try {
      const result = await runBackfill(parseOptions(process.argv.slice(2)), {
        token: process.env.PAYPLANNER_STORAGE_ADMIN_TOKEN, signal: controller.signal,
        onProgress: progress => console.log(JSON.stringify(progress)),
      });
      console.log(`Result: ${result.status}. Mandatory replica migration is NOT complete.`);
      process.exitCode = ['catalogComplete', 'replicaPlanComplete', 'replicaScopeVerified'].includes(result.status) ? 0 : 2;
    } catch (error) {
      console.error(controller.signal.aborted ? 'Stopped. Resume from the saved cursor.' : error.message);
      process.exitCode = controller.signal.aborted ? 130 : 1;
    }
  }
}
