import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyQualifiedShardOutputs } from './verify-qualified-shard-outputs.mjs';

const success = outputs => ({ result: 'success', outputs });
function context() {
  return { producerJob: 'backend-test', needs: { frontend: success({}), 'backend-format': success({}), 'backend-build': success({}),
    ...Object.fromEntries([0, 1, 2, 3].map(index => [`backend-test-${index}`, success({ 'qualified-artifact-id': `xsenus-local-v1:${String(index + 1).repeat(64)}` })])) } };
}
test('binds all four independent successful receipts without rerunning tests', () => {
  assert.equal(Object.keys(verifyQualifiedShardOutputs(context())).length, 4);
});
for (const worker of [0, 1, 2, 3]) {
  for (const state of ['failure', 'cancelled', 'skipped', 'missing', 'unqualified']) test(`rejects shard ${worker}: ${state}`, () => {
    const fixture = context(), name = `backend-test-${worker}`;
    if (state === 'missing') delete fixture.needs[name];
    else if (state === 'unqualified') fixture.needs[name].outputs['qualified-artifact-id'] = 'latest';
    else fixture.needs[name].result = state;
    assert.throws(() => verifyQualifiedShardOutputs(fixture));
  });
}
for (const producer of ['frontend', 'backend-format', 'backend-build']) test(`rejects failed mandatory producer ${producer}`, () => {
  const fixture = context(); fixture.needs[producer].result = 'failure';
  assert.throws(() => verifyQualifiedShardOutputs(fixture));
});
test('rejects reused receipt and a caller outside the mandatory gate', () => {
  const fixture = context(); fixture.needs['backend-test-3'].outputs = fixture.needs['backend-test-0'].outputs;
  assert.throws(() => verifyQualifiedShardOutputs(fixture));
  assert.throws(() => verifyQualifiedShardOutputs({ ...context(), producerJob: 'deploy' }));
});
