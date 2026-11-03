// Prepared diagnostic only. Importing this file is not an execution mode.
import { readFile, writeFile, lstat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { createReadStream, writeFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { join, dirname, resolve } from 'node:path';

const root = dirname(fileURLToPath(import.meta.url));
const load = async name => JSON.parse(await readFile(join(root, name), 'utf8'));
async function hash(path) {
  const digest = createHash('sha256');
  for await (const bytes of createReadStream(path)) digest.update(bytes);
  return digest.digest('hex');
}
async function emit(name, value) {
  await writeFile(join(root, name), JSON.stringify(value) + '\n', { flag: 'wx' });
}
const safeError = error => ({ name: /^[\w]+$/.test(error?.name) ? error.name : 'Error',
  code: /^[A-Z0-9_]+$/.test(error?.code) ? error.code : null });
const started = performance.now();
let heartbeat, deadline, captureCommitted = false;
try {
  const plan = await load('plan.json'), pins = await load('pins.json');
  const ticket = await load('launch-ticket.json');
  const auth = await load('authorization.json');
  if (ticket.scope !== plan.scope || ticket.nonce !== auth.nonce || auth.enabled !== true
      || ticket.authorizationSha256 !== await hash(join(root, 'authorization.json'))
      || ticket.manifestSha256 !== await hash(join(root, 'manifest.json'))
      || ticket.expiresUtc !== auth.expiresUtc || !Number.isFinite(Date.parse(ticket.expiresUtc))
      || Date.now() >= Date.parse(ticket.expiresUtc) || !Number.isInteger(ticket.workerBudgetMs)
      || ticket.workerBudgetMs < 1000 || ticket.workerBudgetMs > 870000)
    throw Object.assign(new Error(), { code: 'LAUNCH_TICKET_GUARD' });
  if (process.env.UV_THREADPOOL_SIZE !== '12') throw Object.assign(new Error(), { code: 'IO_POOL_GUARD' });
  if (process.version !== pins.node.expectedVersion
      || resolve(process.execPath).toLowerCase() !== resolve(pins.node.path).toLowerCase())
    throw Object.assign(new Error(), { code: 'NODE_IDENTITY_GUARD' });
  for (const [name, pin] of Object.entries(pins.files))
    if (await hash(join(pins.workspace, name)) !== pin.sha256)
      throw Object.assign(new Error(), { code: 'SOURCE_PIN_GUARD' });
  const nodeModules = await lstat(join(pins.workspace, 'node_modules'));
  if (!nodeModules.isDirectory() || nodeModules.isSymbolicLink())
    throw Object.assign(new Error(), { code: 'DEPENDENCY_ROOT_GUARD' });
  heartbeat = setInterval(() => {
    const memory = process.memoryUsage();
    console.log(JSON.stringify({ type: 'heartbeat', elapsedMs: performance.now() - started,
      rssBytes: memory.rss, heapUsedBytes: memory.heapUsed, externalBytes: memory.external }));
  }, 5000);
  deadline = setTimeout(() => {
    writeFileSync(join(root, 'own-deadline.json'), JSON.stringify({ deliberateOwnedStop: true,
      elapsedMs: performance.now() - started }) + '\n', { flag: 'wx' });
    console.log(JSON.stringify({ type: 'ownDeadline', deliberateOwnedStop: true }));
    process.exit(125);
  }, ticket.workerBudgetMs);
  // Only this exact module/export is called, once, after the controller's live guards.
  const module = await import(pathToFileURL(join(pins.workspace,
    'scripts/case-retirement-production-bundle.mjs')).href);
  if (typeof module.captureRetirementDependencyInputs !== 'function')
    throw Object.assign(new Error(), { code: 'EXPORT_GUARD' });
  captureCommitted = true;
  await emit('capture-attempt.json', { maximumInvocations: 1, invocationCommitted: true });
  const captureStarted = performance.now();
  const inputs = await module.captureRetirementDependencyInputs(pins.workspace, process.execPath);
  const captureElapsedMs = performance.now() - captureStarted;
  const encoded = JSON.stringify(inputs), fingerprintSha256 = createHash('sha256').update(encoded).digest('hex');
  if (inputs.nodeVersion !== pins.node.expectedVersion || inputs.nodeSha256 !== pins.node.sha256)
    throw Object.assign(new Error(), { code: 'CAPTURE_TOOL_GUARD' });
  await emit('dependency-fingerprint.json', inputs);
  if (await hash(plan.measurement.baselineCaptureResultPath) !== plan.measurement.baselineCaptureResultSha256)
    throw Object.assign(new Error(), { code: 'BASELINE_RECEIPT_GUARD' });
  const baseline = JSON.parse(await readFile(plan.measurement.baselineCaptureResultPath, 'utf8'));
  if (baseline.fingerprintSha256 !== fingerprintSha256 || baseline.fileCount !== Object.keys(inputs.files).length
      || baseline.nodeSha256 !== inputs.nodeSha256 || baseline.nodeVersion !== inputs.nodeVersion)
    throw Object.assign(new Error(), { code: 'BASELINE_INPUT_DRIFT' });

  const summary = { completed: true, maximumInvocations: 1, captureElapsedMs,
    elapsedMs: performance.now() - started, fileCount: Object.keys(inputs.files).length,
    linkCount: Object.values(inputs.files).filter(value => typeof value === 'object').length,
    fingerprintSha256, manifestFileSha256: await hash(join(root, 'dependency-fingerprint.json')),
    nodeSha256: inputs.nodeSha256, nodeVersion: inputs.nodeVersion, uvThreadpoolSize: 12,
    baselineFingerprintMatched: true, baselineCaptureElapsedMs: baseline.captureElapsedMs };
  await emit('capture-result.json', summary);
  console.log(JSON.stringify({ type: 'complete', ...summary }));
} catch (error) {
  await emit('capture-error.json', { captureCommitted, ...safeError(error) });
  process.exitCode = 2;
} finally {
  clearInterval(heartbeat); clearTimeout(deadline);
}
