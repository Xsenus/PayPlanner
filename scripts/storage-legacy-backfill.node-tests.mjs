import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm, mkdir, writeFile, unlink, link } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { parseOptions, runBackfill } from './storage-legacy-backfill.mjs';

const token = 'synthetic-token';
function report(afterId, ids, hasMore = false, apply = false) {
  const items = ids.map(sourceId => ({ sourceId, ownerType: 'task-file', ownerId: String(sourceId),
    logicalName: `synthetic-${sourceId}.txt`, status: apply ? 'registered' : 'ready', sizeBytes: 4,
    sha256: 'a'.repeat(64), objectId: '12345678-1234-1234-1234-123456789abc', locator: 'synthetic' }));
  return { source: 'task-files', applied: apply, afterId, nextAfterId: ids.at(-1) ?? null,
    hasMore, scanned: items.length, registered: apply ? items.length : 0, alreadyRegistered: 0,
    missing: 0, invalid: 0, bytesVerified: items.length * 4, items };
}
async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'pp-backfill-cli-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return { root, options: { url: 'http://127.0.0.1:12345', source: 'task-files', output: join(root, 'run'),
    batchSize: 2, intervalMs: 0 } };
}
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });

test('default options are dry-run and bounded; unsafe or unknown inputs are rejected', () => {
  const args = ['--url', 'https://example.invalid', '--source', 'task-files', '--output', 'synthetic'];
  const parsed = parseOptions(args);
  assert.equal(parsed.apply, false);
  assert.equal(parsed.maxBatches, 100);
  assert.equal(parsed.intervalMs, 1000);
  for (const suffix of [['--tenant', '1'], ['--token', token], ['--batch-size', '501'],
    ['--max-batches', '0'], ['--after-id', '-1'], ['--interval-ms', '60001'], ['--source', 'other']])
    assert.throws(() => parseOptions([...args, ...suffix]));
  for (const url of ['http://external.invalid', 'https://user:secret@example.invalid',
    'https://example.invalid?token=secret', 'https://example.invalid#secret', 'file:///example'])
    assert.throws(() => parseOptions(['--url', url, ...args.slice(2)]));
});

test('bounded batches persist journal and checkpoint, with no credential in artifacts', async t => {
  const { options } = await fixture(t);
  const requests = [];
  const result = await runBackfill(options, { token, fetchImpl: async (url, init) => {
    assert.equal(url, `${options.url}/api/storage/legacy-backfill`);
    assert.equal(init.redirect, 'manual');
    assert.equal(init.headers.Authorization, `Bearer ${token}`);
    const request = JSON.parse(init.body);
    requests.push(request);
    return json(request.afterId === 0 ? report(0, [1, 2], true) : report(2, [3]));
  } });
  assert.equal(result.status, 'catalogComplete');
  assert.equal(result.checkpoint.replicaMigrationComplete, false);
  assert.deepEqual(requests.map(r => r.afterId), [0, 2]);
  assert(requests.every(r => r.apply === false));
  const saved = await readFile(join(options.output, 'checkpoint.json'), 'utf8');
  const lines = await readFile(join(options.output, 'batches.jsonl'), 'utf8');
  assert(!saved.includes(token) && !lines.includes(token));
  assert.equal(lines.trim().split('\n').length, 2);
  assert.deepEqual((await readdir(options.output)).sort(), ['batches.jsonl', 'checkpoint.json']);
  let calls = 0;
  const replay = await runBackfill({ ...options, resume: true }, { token, fetchImpl: () => { calls++; } });
  assert.equal(replay.status, 'catalogComplete');
  assert.equal(calls, 0);
});

test('batch limit resumes from persisted cursor without skipping records', async t => {
  const { options } = await fixture(t);
  const limited = await runBackfill({ ...options, maxBatches: 1 }, { token, fetchImpl: async () => json(report(0, [1, 2], true)) });
  assert.equal(limited.status, 'batchLimit');
  const result = await runBackfill({ ...options, resume: true }, { token, fetchImpl: async (url, init) => {
    assert.equal(JSON.parse(init.body).afterId, 2);
    return json(report(2, [3]));
  } });
  assert.equal(result.checkpoint.afterId, 3);
});

test('missing/changed source is recorded but cursor stays on failed page for retry', async t => {
  const { options } = await fixture(t);
  const bad = report(0, [1, 2], true);
  bad.items[1].status = 'invalid'; bad.invalid = 1;
  const result = await runBackfill(options, { token, fetchImpl: async () => json(bad) });
  assert.equal(result.status, 'conflicts');
  assert.equal(result.checkpoint.afterId, 0);
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
  const retry = await runBackfill({ ...options, resume: true }, { token, fetchImpl: async (url, init) => {
    assert.equal(JSON.parse(init.body).afterId, 0);
    return json(report(0, [1, 2]));
  } });
  assert.equal(retry.status, 'catalogComplete');
  assert.equal((await readFile(join(options.output, 'batches.jsonl'), 'utf8')).trim().split('\n').length, 2);
});

test('ambiguous HTTP failure leaves the cursor and allows idempotent replay', async t => {
  const { options } = await fixture(t);
  const inventory = `${options.output}-inventory`;
  await runBackfill({ ...options, output: inventory }, { token, fetchImpl: async () => json(report(0, [1])) });
  await assert.rejects(runBackfill({ ...options, apply: true, inventory }, { token, fetchImpl: async () => {
    throw new Error(`transport contains ${token}`);
  } }), error => !error.message.includes(token) && /cursor was not advanced/u.test(error.message));
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
  const page = report(0, [1], false, true);
  page.items[0].status = 'alreadyRegistered'; page.registered = 0; page.alreadyRegistered = 1;
  const result = await runBackfill({ ...options, apply: true, inventory, resume: true }, { token, fetchImpl: async () => json(page) });
  assert.equal(result.status, 'catalogComplete');
});

for (const status of [302, 401, 403, 429, 500]) test(`HTTP ${status} is not retried or redirected`, async t => {
  const { options } = await fixture(t);
  let calls = 0;
  await assert.rejects(runBackfill(options, { token, fetchImpl: async () => {
    calls++; return new Response('sensitive response', { status });
  } }), new RegExp(`HTTP ${status}`, 'u'));
  assert.equal(calls, 1);
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});

for (const mutation of ['source', 'applied', 'afterId', 'nextAfterId', 'scanned', 'registered', 'bytesVerified',
  'status', 'sha', 'duplicate', 'tooMany', 'emptyMore']) test(`malformed report ${mutation} cannot advance cursor`, async t => {
  const { options } = await fixture(t);
  const page = report(0, [1, 2]);
  if (mutation === 'source') page.source = 'case-documents';
  else if (mutation === 'applied') page.applied = true;
  else if (mutation === 'status') page.items[0].status = 'available';
  else if (mutation === 'sha') page.items[0].sha256 = null;
  else if (mutation === 'duplicate') page.items[1].sourceId = 1;
  else if (mutation === 'tooMany') page.items.push(page.items[1]);
  else if (mutation === 'emptyMore') Object.assign(page, report(0, [], true));
  else page[mutation] = 777;
  await assert.rejects(runBackfill(options, { token, fetchImpl: async () => json(page) }));
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});

test('oversized API response is bounded and cannot change checkpoint', async t => {
  const { options } = await fixture(t);
  await assert.rejects(runBackfill(options, { token, fetchImpl: async () => new Response('x'.repeat(8 * 1024 * 1024 + 1)) }), /allowed size/u);
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});

test('malformed JSON response cannot expose reflected credentials in errors', async t => {
  const { options } = await fixture(t);
  await assert.rejects(runBackfill(options, { token, fetchImpl: async () => new Response(`secret=${token}`) }),
    error => error.message === 'API response is not valid JSON.' && !error.message.includes(token));
});

test('resume rejects changed endpoint/source/mode/batch/initial cursor before API I/O', async t => {
  const { options } = await fixture(t);
  await runBackfill(options, { token, fetchImpl: async () => json(report(0, [])) });
  for (const change of [{ url: 'https://other.invalid' }, { source: 'case-documents' }, { batchSize: 3 }, { afterId: 1 }]) {
    await assert.rejects(runBackfill({ ...options, ...change, resume: true }, { token, fetchImpl: () => assert.fail('must not request API') }), /Checkpoint/u);
  }
});

test('apply requires completed dry-run with matching scope before any API request or output creation', async t => {
  const { options } = await fixture(t);
  const inventory = `${options.output}-inventory`;
  await assert.rejects(runBackfill({ ...options, apply: true }, { token }), /inventory/u);
  await runBackfill({ ...options, output: inventory, maxBatches: 1 }, { token, fetchImpl: async () => json(report(0, [1, 2], true)) });
  await assert.rejects(runBackfill({ ...options, apply: true, inventory }, { token,
    fetchImpl: () => assert.fail('not allowed') }), /completed matching/u);
  await runBackfill({ ...options, output: inventory, resume: true }, { token, fetchImpl: async () => json(report(2, [3])) });
  await assert.rejects(runBackfill({ ...options, url: 'https://other.invalid', apply: true, inventory }, { token,
    fetchImpl: () => assert.fail('not allowed') }), /completed matching/u);
  const result = await runBackfill({ ...options, apply: true, inventory }, { token, fetchImpl: async (url, init) => {
    assert.equal(JSON.parse(init.body).apply, true);
    return json(report(0, [1], false, true));
  } });
  assert.equal(result.status, 'catalogComplete');
  assert.equal(result.checkpoint.replicaMigrationComplete, false);
});

test('resume refuses missing journal and oversized or multiply-linked checkpoint', async t => {
  const { options, root } = await fixture(t);
  await runBackfill(options, { token, fetchImpl: async () => json(report(0, [])) });
  const checkpoint = join(options.output, 'checkpoint.json');
  const saved = await readFile(checkpoint);
  await unlink(join(options.output, 'batches.jsonl'));
  await assert.rejects(runBackfill({ ...options, resume: true }, { token }), { code: 'ENOENT' });
  await writeFile(checkpoint, ' '.repeat(65537));
  await assert.rejects(runBackfill({ ...options, resume: true }, { token }), /checkpoint size/u);
  await writeFile(checkpoint, saved);
  await link(checkpoint, join(root, 'linked.json'));
  await assert.rejects(runBackfill({ ...options, resume: true }, { token }), /linked elsewhere/u);
});

test('resume drops a partial uncommitted journal tail but refuses loss of committed bytes', async t => {
  const { options } = await fixture(t);
  await runBackfill({ ...options, maxBatches: 1 }, { token, fetchImpl: async () => json(report(0, [1, 2], true)) });
  const journal = join(options.output, 'batches.jsonl');
  const saved = await readFile(journal);
  await writeFile(journal, Buffer.concat([saved, Buffer.from('{partial killed append')]));
  await runBackfill({ ...options, resume: true }, { token, fetchImpl: async () => json(report(2, [3])) });
  const lines = (await readFile(journal, 'utf8')).trim().split('\n');
  assert.equal(lines.length, 2);
  assert(lines.every(line => JSON.parse(line).report));
  await writeFile(journal, saved.subarray(0, saved.length - 1));
  await assert.rejects(runBackfill({ ...options, resume: true }, { token }), /shorter than/u);
});

test('cancellation after a committed page retains it and cleans the lock', async t => {
  const { options } = await fixture(t);
  const controller = new AbortController();
  await assert.rejects(runBackfill(options, { token, signal: controller.signal,
    fetchImpl: async () => json(report(0, [1, 2], true)), onProgress: () => controller.abort() }), { name: 'AbortError' });
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 2);
  assert(!(await readdir(options.output)).includes('run.lock'));
});

test('existing output and concurrent lock are never overwritten', async t => {
  const { options } = await fixture(t);
  await mkdir(options.output);
  await writeFile(join(options.output, 'run.lock'), 'synthetic existing lock');
  await assert.rejects(runBackfill(options, { token }), { code: 'EEXIST' });
  await assert.rejects(runBackfill({ ...options, resume: true }, { token }), { code: 'EEXIST' });
  assert.equal(await readFile(join(options.output, 'run.lock'), 'utf8'), 'synthetic existing lock');
});

test('real loopback HTTP uses Admin bearer contract and POST only', async t => {
  const { options } = await fixture(t);
  const server = createServer(async (request, response) => {
    assert.equal(request.url, '/api/storage/legacy-backfill');
    assert.equal(request.method, 'POST');
    assert.equal(request.headers.authorization, `Bearer ${token}`);
    let body = '';
    for await (const chunk of request) body += chunk;
    assert.deepEqual(JSON.parse(body), { source: 'task-files', afterId: 0, batchSize: 2, apply: false });
    response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(report(0, [1])));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => new Promise(resolve => server.close(resolve)));
  const result = await runBackfill({ ...options, url: `http://127.0.0.1:${server.address().port}` }, { token });
  assert.equal(result.status, 'catalogComplete');
});

function replicaReport(request, ids = [1], { hasMore = false, verified = false, fingerprint = 'b'.repeat(64) } = {}) {
  const sourceVerification = report(request.afterId, ids, hasMore);
  if (request.endId !== undefined) sourceVerification.endId = request.endId;
  sourceVerification.items.forEach(i => Object.assign(i, { status: 'alreadyRegistered', catalogVersion: 1, contentType: 'text/plain' }));
  sourceVerification.alreadyRegistered = ids.length;
  return { source: request.source, runId: request.runId, applied: request.apply, policyVersion: 'synthetic',
    policyFingerprint: fingerprint, requiredCopies: 1, batchVerified: verified, queued: request.apply && !verified ? ids.length * 2 : 0,
    sourceVerification, errors: [], items: sourceVerification.items.map(i => ({ sourceId: i.sourceId, objectId: i.objectId,
      providerKey: 'local', connectionId: null, locator: `migration/${i.sourceId}/data.bin`,
      status: verified ? 'verified' : request.apply ? 'pending' : 'planned', error: null })) };
}

async function replicaFixture(t) {
  const { options, root } = await fixture(t);
  options.phase = 'replicas';
  const inventory = join(root, 'inventory');
  await runBackfill({ ...options, output: inventory }, { token,
    fetchImpl: async (url, init) => json(replicaReport(JSON.parse(init.body))) });
  return { root, options: { ...options, inventory, apply: true } };
}

test('replica phase restricts page size and rejects unknown phases', () => {
  const args = ['--url', 'https://example.invalid', '--source', 'task-files', '--output', 'synthetic', '--phase', 'replicas'];
  assert.equal(parseOptions(args).phase, 'replicas');
  assert.throws(() => parseOptions([...args, '--batch-size', '101']));
  assert.throws(() => parseOptions([...args, '--phase', 'cutover']));
});

test('replica apply pins inventory identity, waits without cursor advancement, and resumes same run', async t => {
  const { options } = await replicaFixture(t);
  const requests = [];
  const pending = await runBackfill(options, { token, fetchImpl: async (url, init) => {
    assert.equal(url, `${options.url}/api/storage/legacy-replicas`);
    const request = JSON.parse(init.body); requests.push(request);
    return json(replicaReport(request));
  } });
  assert.equal(pending.status, 'pendingCopies');
  assert.equal(pending.checkpoint.afterId, 0);
  assert.deepEqual(requests.map(r => r.apply), [false, true]);
  assert(requests.every(r => r.expectedPolicyFingerprint === 'b'.repeat(64)));
  const verified = await runBackfill({ ...options, resume: true }, { token, fetchImpl: async (url, init) => {
    const request = JSON.parse(init.body);
    assert.equal(request.runId, pending.checkpoint.runId);
    assert.equal(request.afterId, 0);
    return json(replicaReport(request, [1], { verified: true }));
  } });
  assert.equal(verified.status, 'replicaScopeVerified');
  assert.equal(verified.checkpoint.afterId, 1);
  assert.equal(verified.checkpoint.replicaMigrationComplete, false);
  assert(!(await readFile(join(options.output, 'batches.jsonl'), 'utf8')).includes(token));
  assert(!(await readdir(options.output)).includes('run.lock'));
});

for (const mutation of ['source', 'target', 'policy', 'newRow', 'removedRow']) test(`replica apply refuses ${mutation} drift before writes`, async t => {
  const { options } = await replicaFixture(t);
  let writes = 0;
  await assert.rejects(runBackfill(options, { token, fetchImpl: async (url, init) => {
    const request = JSON.parse(init.body); if (request.apply) writes++;
    const value = replicaReport(request, mutation === 'newRow' ? [1, 2] : mutation === 'removedRow' ? [] : [1]);
    if (mutation === 'source') value.sourceVerification.items[0].sha256 = 'c'.repeat(64);
    if (mutation === 'target') value.items[0].locator = 'different/target';
    if (mutation === 'policy') value.policyFingerprint = 'c'.repeat(64);
    return json(value);
  } }));
  assert.equal(writes, 0);
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});

for (const mutation of ['missingTarget', 'duplicateTarget', 'wrongRun', 'falseProof']) test(`replica response rejects ${mutation} without accepting proof`, async t => {
  const { options } = await fixture(t);
  await assert.rejects(runBackfill({ ...options, phase: 'replicas' }, { token, fetchImpl: async (url, init) => {
    const value = replicaReport(JSON.parse(init.body), [1], { verified: true });
    if (mutation === 'missingTarget') value.items = [];
    if (mutation === 'duplicateTarget') value.items.push(value.items[0]);
    if (mutation === 'wrongRun') value.runId = '12345678-1234-1234-1234-123456789abc';
    if (mutation === 'falseProof') value.items[0].status = 'planned';
    return json(value);
  } }));
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});

test('replica HTTP unknown outcome resumes stable run without skipping the page', async t => {
  const { options } = await replicaFixture(t);
  let runId;
  await assert.rejects(runBackfill(options, { token, fetchImpl: async (url, init) => {
    const request = JSON.parse(init.body); runId = request.runId;
    if (request.apply) throw new Error(`transport ${token}`);
    return json(replicaReport(request));
  } }), error => !error.message.includes(token));
  const result = await runBackfill({ ...options, resume: true }, { token, fetchImpl: async (url, init) => {
    const request = JSON.parse(init.body); assert.equal(request.runId, runId); assert.equal(request.afterId, 0);
    return json(replicaReport(request, [1], { verified: true }));
  } });
  assert.equal(result.status, 'replicaScopeVerified');
});

test('replica inventory resumes sequential pages and does not accept a policy change', async t => {
  const { options } = await fixture(t);
  const input = { ...options, phase: 'replicas', maxBatches: 1 };
  const limited = await runBackfill(input, { token, fetchImpl: async (url, init) => json(replicaReport(JSON.parse(init.body), [1, 2], { hasMore: true })) });
  assert.equal(limited.status, 'batchLimit');
  await assert.rejects(runBackfill({ ...input, resume: true }, { token,
    fetchImpl: async (url, init) => json(replicaReport(JSON.parse(init.body), [3], { fingerprint: 'c'.repeat(64) })) }));
  const completed = await runBackfill({ ...input, resume: true }, { token,
    fetchImpl: async (url, init) => json(replicaReport(JSON.parse(init.body), [3])) });
  assert.equal(completed.status, 'replicaPlanComplete');
  const apply = { ...input, output: `${options.output}-apply`, inventory: options.output, apply: true };
  const fetchImpl = async (url, init) => {
    const request = JSON.parse(init.body);
    return json(replicaReport(request, request.afterId === 0 ? [1, 2] : [3], { hasMore: request.afterId === 0, verified: true }));
  };
  assert.equal((await runBackfill(apply, { token, fetchImpl })).status, 'batchLimit');
  const finished = await runBackfill({ ...apply, resume: true }, { token, fetchImpl });
  assert.equal(finished.status, 'replicaScopeVerified');
  assert.equal(finished.checkpoint.afterId, 3);
});

test('cancelling after replica preview cannot queue apply and releases its report lock', async t => {
  const { options } = await replicaFixture(t);
  const controller = new AbortController();
  let calls = 0;
  await assert.rejects(runBackfill(options, { token, signal: controller.signal, fetchImpl: async (url, init) => {
    calls++; const request = JSON.parse(init.body); assert.equal(request.apply, false);
    controller.abort(); return json(replicaReport(request));
  } }), { name: 'AbortError' });
  assert.equal(calls, 1);
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
  assert(!(await readdir(options.output)).includes('run.lock'));
});

test('replica apply rejects corrupted committed inventory without making any request', async t => {
  const { options } = await replicaFixture(t);
  const path = join(options.inventory, 'batches.jsonl');
  const bytes = await readFile(path);
  bytes[0] = 33; await writeFile(path, bytes);
  await assert.rejects(runBackfill(options, { token, fetchImpl: () => assert.fail('must not request') }), /valid JSON/u);
});

test('replica errors and incomplete plans stop without completing an inventory', async t => {
  const { options } = await fixture(t);
  const result = await runBackfill({ ...options, phase: 'replicas' }, { token, fetchImpl: async (url, init) => {
    const value = replicaReport(JSON.parse(init.body)); value.items = []; value.errors = ['unavailable writer'];
    return json(value);
  } });
  assert.equal(result.status, 'conflicts');
  assert.equal(result.checkpoint.afterId, 0);
  assert.equal(result.checkpoint.completed, false);
});

test('actual API omission of null connection IDs does not reject local replica destinations', async t => {
  const { options } = await fixture(t);
  const result = await runBackfill({ ...options, phase: 'replicas' }, { token, fetchImpl: async (url, init) => {
    const value = replicaReport(JSON.parse(init.body)); delete value.items[0].connectionId; delete value.items[0].error;
    return json(value);
  } });
  assert.equal(result.status, 'replicaPlanComplete');
});

test('actual API omission of an empty next cursor accepts only an empty terminal page', async t => {
  const { options } = await fixture(t);
  const result = await runBackfill(options, { token, fetchImpl: async () => {
    const value = report(0, []); delete value.nextAfterId; return json(value);
  } });
  assert.equal(result.status, 'catalogComplete');
});

test('end ID is pinned across replica preview/apply/resume and rejects unsafe bounds', async t => {
  const { options } = await fixture(t);
  const inventory = `${options.output}-inventory`;
  const args = ['--url', options.url, '--source', options.source, '--output', options.output, '--after-id', '2', '--end-id'];
  assert.throws(() => parseOptions([...args, '1']));
  assert.throws(() => parseOptions([...args, '-1']));
  const preview = { ...options, phase: 'replicas', endId: 1, output: inventory };
  const fetchImpl = async (url, init) => {
    const request = JSON.parse(init.body); assert.equal(request.endId, 1);
    return json(replicaReport(request, [1], { verified: request.apply }));
  };
  await runBackfill(preview, { token, fetchImpl });
  await assert.rejects(runBackfill({ ...preview, output: options.output, endId: 2, apply: true, inventory },
    { token, fetchImpl: () => assert.fail('changed scope must not call API') }));
  const apply = { ...preview, output: options.output, apply: true, inventory };
  assert.equal((await runBackfill(apply, { token, fetchImpl })).status, 'replicaScopeVerified');
  await assert.rejects(runBackfill({ ...apply, endId: 2, resume: true }, { token, fetchImpl: () => assert.fail('changed scope') }));
});

for (const mutation of ['missingEcho', 'outsideBound', 'moreAtBound']) test(`end ID rejects ${mutation} response before advancing`, async t => {
  const { options } = await fixture(t);
  await assert.rejects(runBackfill({ ...options, endId: 1 }, { token, fetchImpl: async () => {
    const value = report(0, mutation === 'outsideBound' ? [2] : [1], mutation === 'moreAtBound');
    if (mutation !== 'missingEcho') value.endId = 1;
    return json(value);
  } }));
  assert.equal(JSON.parse(await readFile(join(options.output, 'checkpoint.json'), 'utf8')).afterId, 0);
});
