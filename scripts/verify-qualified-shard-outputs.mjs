import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { contextFromEnvironment, need } from './xsenus-artifact-store.mjs';

export function verifyQualifiedShardOutputs(context) {
  assert.equal(context.producerJob, 'backend-test', 'Only the mandatory backend-test gate binds worker outputs');
  const workers = [0, 1, 2, 3].map(index => `backend-test-${index}`);
  assert.deepEqual(Object.keys(context.needs).sort(), ['frontend', 'backend-format', 'backend-build', ...workers].sort(), 'Every producer and worker is required');
  for (const job of ['frontend', 'backend-format', 'backend-build']) need(context, job);
  const outputs = {};
  for (const [index, worker] of workers.entries()) {
    const id = context.needs[worker]?.outputs?.['qualified-artifact-id'];
    assert.match(id ?? '', /^xsenus-local-v1:[a-f0-9]{64}$/, 'Only an immutable qualified artifact ID may cross the gate');
    need(context, worker, 'qualified-artifact-id', id);
    outputs[`shard_${index}_id`] = id;
  }
  assert.equal(new Set(Object.values(outputs)).size, 4, 'Each worker needs a distinct qualified receipt');
  return outputs;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { verifyQualifiedShardOutputs(contextFromEnvironment(process.env)); console.log('All four qualified backend shards verified.'); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
