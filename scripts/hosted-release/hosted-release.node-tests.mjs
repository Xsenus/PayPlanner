import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { parseWorkflow } from '../validate-deploy-workflow.mjs';
import { transferKey, verifyFile, verifyTransferKey } from './transfer.mjs';
import { validateHostedRelease } from './validate.mjs';

function workflows() {
  return Object.fromEntries(Object.entries({
    entry: '.github/workflows/deploy.yml',
    checks: '.github/workflows/release-checks.yml',
    shard: '.github/workflows/release-shard.yml',
    legacy: 'scripts/fixtures/deploy-xsenus.yml',
  }).map(([role, path]) => [role, parseWorkflow(readFileSync(path, 'utf8'))]));
}

test('the release entry, hosted checks and hosted shard preserve the deployment contract', () => {
  assert.equal(validateHostedRelease(workflows()).windowsWorkers, 4);
});

const weakenings = [
  ['add an untrusted PR trigger', data => { data.entry.on.pull_request = {}; }],
  ['enable access checks on a feature branch', data => { data.entry.jobs['deploy-access'].if = 'success()'; }],
  ['run the hosted worker on XSENUS', data => { data.shard.jobs.shard['runs-on'] = ['self-hosted']; }],
  ['export secrets to a reusable workflow', data => { data.entry.jobs['backend-format'].secrets = 'inherit'; }],
  ['allow a partial or unchecked test runtime', data => { delete data.shard.jobs.shard.steps.find(step => step.id === 'runtime').with['fail-on-cache-miss']; }],
  ['allow deployment before hosted checks finish', data => { data.entry.jobs.deploy.needs.splice(1, 1); }],
  ['skip a mandatory compile or formatting command', data => { data.checks.jobs['backend-format'].steps.pop(); }],
  ['package a partial backend test union', data => { data.checks.jobs['backend-union'].needs.pop(); }],
  ['skip package qualification for the exact delivered files', data => { data.entry.jobs.deploy.steps = data.entry.jobs.deploy.steps.filter(step => !step.name.startsWith('Match the package manifest')); }],
  ['allow an error to turn into a passing job', data => { data.shard.jobs.shard.steps[0]['continue-on-error'] = true; }],
];
for (const [name, modify] of weakenings) test(`reject ${name}`, () => {
  const data = workflows();
  modify(data);
  assert.throws(() => validateHostedRelease(data));
});

test('the exact transfer key accepts earlier attempts in the same run and excludes alternate revisions and caches', () => {
  const env = { GITHUB_RUN_ID: '918', GITHUB_RUN_ATTEMPT: '1', GITHUB_SHA: 'a'.repeat(40) };
  const digest = 'b'.repeat(64);
  const key = transferKey('backend', digest, env);
  assert.doesNotThrow(() => verifyTransferKey('backend', digest, key, { ...env, GITHUB_RUN_ATTEMPT: '2' }));
  for (const [kind, content, candidate, context] of [
    ['runtime', digest, key, env],
    ['backend', 'c'.repeat(64), key, env],
    ['backend', digest, key + '-prefix-hit', env],
    ['backend', digest, key, { ...env, GITHUB_RUN_ID: '919' }],
    ['backend', digest, key, { ...env, GITHUB_SHA: 'e'.repeat(40) }],
    ['backend', digest, transferKey('backend', digest, { ...env, GITHUB_RUN_ATTEMPT: '2' }), env],
  ]) assert.throws(() => verifyTransferKey(kind, content, candidate, context));
});

test('transport rejects changed bytes before unpacking or retaining them', t => {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-hosted-release-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const path = join(root, 'package.tar.gz');
  const digest = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';
  writeFileSync(path, 'abc');
  assert.doesNotThrow(() => verifyFile(path, digest));
  writeFileSync(path, 'abd');
  assert.throws(() => verifyFile(path, digest), /differs/);
});
