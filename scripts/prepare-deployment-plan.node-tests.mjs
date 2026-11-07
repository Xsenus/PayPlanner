import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { preparePackage, sha256 } from './prepare-deployment-package.mjs';
import { createPlan, qualifiedContext, reviewedCandidate, validateReviewedBaseline } from './prepare-deployment-plan.mjs';
import { parseWorkflow } from './validate-deploy-workflow.mjs';

const revision = 'a'.repeat(40);
const env = { COMPUTERNAME: 'XSENUS', GITHUB_ACTIONS: 'true', GITHUB_REPOSITORY: 'Xsenus/PayPlanner',
  GITHUB_REF: 'refs/heads/main', GITHUB_JOB: 'deploy', GITHUB_SHA: revision, GITHUB_WORKFLOW_SHA: revision,
  GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main', GITHUB_EVENT_NAME: 'push',
  GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', OUT_DIR: 'out', ARTIFACT_NEEDS_JSON: JSON.stringify({
    frontend: { result: 'success', outputs: { 'frontend-artifact-id': 'xsenus-local-v1:' + 'b'.repeat(64) } },
    'backend-format': { result: 'success', outputs: {} },
    backend: { result: 'success', outputs: { 'backend-artifact-id': 'xsenus-local-v1:' + 'c'.repeat(64), 'manifest-sha': 'd'.repeat(64) } },
  }) };

function fixture(t, candidate = false) {
  const root = mkdtempSync(join(tmpdir(), 'pp-ci-plan-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, 'scripts'));
  for (const name of ['deploy-vps.sh', 'deploy-db-proof.py', 'deploy-history-source-proof.py', 'deploy-stopped-source-proof.py', 'deploy-roadmap-candidate-review.json', 'deploy-roadmap-candidate-delta.json']) {
    writeFileSync(join(root, 'scripts', name), readFileSync(resolve('scripts', name)));
  }
  mkdirSync(join(root, 'out/be/AppReleases'), { recursive: true });
  mkdirSync(join(root, 'out/fe'), { recursive: true });
  const baseline = JSON.parse(readFileSync(resolve('scripts/deployment-baseline.json'), 'utf8'));
  let releases = Buffer.from('[{"releaseId":"published"}]');
  if (candidate) {
    releases = readFileSync(resolve('backend/AppReleases/releases.json'));
    assert.equal(sha256(releases), reviewedCandidate(root).releaseCatalogSha256);
  }
  if (!candidate) { baseline.expectedMigration = null; baseline.expectedReleaseSeeds = []; }
  baseline.expectedReleaseCatalogSha256 = sha256(releases);
  writeFileSync(join(root, 'scripts/deployment-baseline.json'), JSON.stringify(baseline));
  for (const name of ['PayPlanner.Api.dll', 'PayPlanner.Api.Migrations.dll']) writeFileSync(join(root, 'out/be', name), name);
  writeFileSync(join(root, 'out/be/AppReleases/releases.json'), releases);
  for (const name of ['index.html', 'payplanner-app-cache.js', 'payplanner-worker.js', 'payplanner-push-worker.js']) writeFileSync(join(root, 'out/fe', name), name);
  writeFileSync(join(root, 'out/fe/deploy-revision.txt'), revision + '\n');
  preparePackage(join(root, 'out'), revision);
  const archive = join(root, 'qualified.tar.gz');
  writeFileSync(archive, 'synthetic qualified package');
  const refs = ['base.tar', 'delta.tar'].map(name => {
    const path = join(root, name);
    writeFileSync(path, name);
    return { path, sha256: sha256(Buffer.from(name)), bytes: Buffer.byteLength(name) };
  });
  const historyManifestPath = join(root, 'history-manifest.json');
  writeFileSync(historyManifestPath, '{"schemaVersion":1}');
  const historyManifestSha256 = sha256(readFileSync(historyManifestPath));
  const historyReceiptPath = join(root, 'history-receipt.json');
  writeFileSync(historyReceiptPath, JSON.stringify({ schemaVersion: 2, rootAccepted: true, verifiedOutsideVps: true,
    currentManifestSha256: historyManifestSha256, archiveReferences: refs }));
  return { root, archive, revision, env: { ...env }, platform: 'win32', now: new Date('2026-10-26T09:00:00Z'),
    operator: { authoriseAutomaticMainDelivery: true, historyReceiptPath, historyReceiptSha256: sha256(readFileSync(historyReceiptPath)),
      historyManifestPath, historyManifestSha256 } };
}

test('binds qualified needs, exact package maps, current history, source pins and stopped phase', t => {
  const input = fixture(t);
  const { plan, qualificationRaw } = createPlan(input);
  const q = JSON.parse(qualificationRaw);
  assert.equal(plan.backupUtc, '20261026T090000Z');
  assert.equal(plan.phase, 'backup');
  assert.equal(plan.expectedProtectedTables.length, 16);
  assert.equal(plan.expectedFrontendRawFileCount, 4);
  assert.equal(q.ciQualified, true);
  assert.equal(q.backendArtifactId, 'xsenus-local-v1:' + 'c'.repeat(64));
  assert.equal(plan.qualificationReceipt.sha256, sha256(qualificationRaw));
  assert.deepEqual(plan.expectedReleaseSeeds, []);
  assert.equal(plan.rollbackAfterStartup, 'halt-for-manual-review');
});

test('qualifies exactly the reviewed eighteen-migration candidate and all 770 release insertions', t => {
  const input = fixture(t, true);
  const { plan, qualificationRaw } = createPlan(input);
  const review = reviewedCandidate(input.root);
  assert.equal(plan.expectedMigration, review.migrationIds.join('+'));
  assert.equal(plan.expectedReleaseSeeds.length, 770);
  assert.deepEqual(plan.expectedReleaseSeeds.slice(0, 5).map(entry => entry.version), ['1.14.912', '1.14.911', '1.14.910', '1.14.909', '1.14.908']);
  assert.ok(plan.expectedReleaseSeeds.slice(0, 5).every(entry => entry.isActive === true));
  assert.deepEqual(plan.expectedReleaseSeeds, JSON.parse(readFileSync(join(input.root, 'out/be/AppReleases/releases.json'), 'utf8')).slice(0, 770));
  assert.equal(plan.expectedReleaseCatalogSha256, review.releaseCatalogSha256);
  assert.deepEqual(JSON.parse(qualificationRaw).expectedReleaseSeeds, plan.expectedReleaseSeeds);
});

test('new catalog additions do not inherit the historical candidate admission', t => {
  const input = fixture(t, true);
  const path = join(input.root, 'scripts/deployment-baseline.json');
  const baseline = JSON.parse(readFileSync(path, 'utf8'));
  baseline.expectedReleaseCatalogSha256 = sha256(Buffer.concat([readFileSync(resolve('backend/AppReleases/releases.json')), Buffer.from('\n')]));
  assert.notEqual(baseline.expectedReleaseCatalogSha256, reviewedCandidate(input.root).releaseCatalogSha256);
  writeFileSync(path, JSON.stringify(baseline));
  assert.throws(() => createPlan(input), /Reviewed candidate catalog differs/);
});

test('candidate admission rejects partial/reordered migrations, catalog or seed substitutions', t => {
  const input = fixture(t, true);
  const original = JSON.parse(readFileSync(join(input.root, 'scripts/deployment-baseline.json'), 'utf8'));
  const ids = reviewedCandidate(input.root).migrationIds;
  for (const mutate of [
    value => { value.expectedMigration = ids.slice(0, -1).join('+'); },
    value => { value.expectedMigration = [...ids].reverse().join('+'); },
    value => { value.expectedMigration += '+unreviewed'; },
    value => { value.previousRevision = 'b'.repeat(40); },
    value => { value.expectedReleaseCatalogSha256 = 'c'.repeat(64); },
    value => { value.expectedReleaseSeeds.pop(); },
    value => { value.expectedReleaseSeeds.reverse(); },
    value => { value.expectedReleaseSeeds[0].items[0].text += ' changed'; },
    value => { value.expectedReleaseSeeds[0].isActive = !value.expectedReleaseSeeds[0].isActive; },
  ]) {
    const changed = structuredClone(original); mutate(changed);
    assert.throws(() => validateReviewedBaseline(changed, input.root));
  }
});

test('review and schema fixtures are immutable while LF and CRLF preserve approval', t => {
  const input = fixture(t, true);
  const baseline = JSON.parse(readFileSync(join(input.root, 'scripts/deployment-baseline.json'), 'utf8'));
  const reviewPath = join(input.root, 'scripts/deploy-roadmap-candidate-review.json');
  const raw = readFileSync(reviewPath, 'utf8');
  writeFileSync(reviewPath, raw.replaceAll('\n', '\r\n'));
  validateReviewedBaseline(baseline, input.root);
  const changed = JSON.parse(raw); changed.releaseSeedsCount++;
  writeFileSync(reviewPath, JSON.stringify(changed));
  assert.throws(() => validateReviewedBaseline(baseline, input.root), /admission changed/);
  writeFileSync(reviewPath, raw);
  const path = join(input.root, 'scripts/deploy-roadmap-candidate-delta.json');
  const delta = JSON.parse(readFileSync(path, 'utf8')); delta.columns.added[0].type = 'unreviewed';
  writeFileSync(path, JSON.stringify(delta));
  assert.throws(() => validateReviewedBaseline(baseline, input.root), /schema fixture changed/);
});

test('schema int64 boundaries are checked without JavaScript precision loss', t => {
  const input = fixture(t, true);
  const baseline = JSON.parse(readFileSync(join(input.root, 'scripts/deployment-baseline.json'), 'utf8'));
  const path = join(input.root, 'scripts/deploy-roadmap-candidate-delta.json');
  const original = readFileSync(path, 'utf8');
  assert.ok(original.includes('9223372036854775807'));
  assert.equal(Number('9223372036854775807'), Number('9223372036854775806'));
  writeFileSync(path, original.replace('9223372036854775807', '9223372036854775806'));
  assert.throws(() => validateReviewedBaseline(baseline, input.root), /schema fixture changed/);
});

test('admits only the specifically reviewed additive payment attempt migration', t => {
  const input = fixture(t);
  const path = join(input.root, 'scripts/deployment-baseline.json');
  const baseline = JSON.parse(readFileSync(path, 'utf8'));
  baseline.expectedMigration = '20261027005432_AddPaymentCreateAttempts';
  writeFileSync(path, JSON.stringify(baseline));
  assert.equal(createPlan(input).plan.expectedMigration, baseline.expectedMigration);
  baseline.expectedMigration = '20261027005432_AddPaymentCreateAttempts+20261027014154_AddPersonalChatHistory';
  writeFileSync(path, JSON.stringify(baseline));
  assert.equal(createPlan(input).plan.expectedMigration, baseline.expectedMigration);
  baseline.expectedMigration += '_unexpected';
  writeFileSync(path, JSON.stringify(baseline));
  assert.throws(() => createPlan(input), /Only the reviewed additive migration/);
  baseline.expectedMigration = 'another-migration';
  writeFileSync(path, JSON.stringify(baseline));
  assert.throws(() => createPlan(input), /Only the reviewed additive migration/);
});

test('rejects local context, wrong workflow identity, untrusted host, non-main and invalid run', () => {
  for (const [key, value] of [['GITHUB_ACTIONS', 'false'], ['COMPUTERNAME', 'OTHER'], ['GITHUB_SHA', 'b'.repeat(40)],
    ['GITHUB_WORKFLOW_SHA', 'b'.repeat(40)], ['GITHUB_JOB', 'backend'], ['GITHUB_REF', 'refs/heads/feature'], ['GITHUB_RUN_ID', '0'], ['OUT_DIR', '../other']]) {
    assert.throws(() => qualifiedContext({ ...env, [key]: value }, revision, 'win32'), undefined, key);
  }
  assert.throws(() => qualifiedContext(env, 'main', 'win32'));
});

test('missing or non-object producer needs fail closed with a fixed diagnostic', () => {
  for (const value of [undefined, 'null', '[]', '"not producer results"', 'false', '123']) {
    assert.throws(() => qualifiedContext({ ...env, ARTIFACT_NEEDS_JSON: value }, revision, 'win32'),
      /Actual mandatory producer needs are required/);
  }
});

test('the actual workflow inventory and archive commands satisfy the exact Linux package consumer', t => {
  const input = fixture(t);
  const output = join(input.root, 'out');
  for (const name of ['deployment-manifest.json', 'SHA256SUMS', 'deployment-files.nul']) rmSync(join(output, name));
  rmSync(input.archive);
  mkdirSync(join(output, 'fe/assets'));
  writeFileSync(join(output, 'fe/assets/русский файл.js'), 'qualified unicode asset');
  mkdirSync(join(output, 'deploy'));
  writeFileSync(join(output, 'deploy/unrelated.sh'), 'must stay outside the application archive');
  for (const name of ['prepare-deployment-package.mjs', 'xsenus-artifact-store.mjs', 'verify-xsenus-runner.mjs']) {
    writeFileSync(join(input.root, 'scripts', name), readFileSync(resolve('scripts', name)));
  }
  const steps = parseWorkflow(readFileSync(resolve('.github/workflows/deploy.yml'), 'utf8')).jobs.deploy.steps;
  const inventory = steps.find(step => step.name === 'Prepare qualified deployment package');
  const archive = steps.find(step => step.name === 'Create tarball');
  assert.ok(inventory, 'The real workflow must prepare its deployment manifest');
  const expand = script => script.replaceAll('${{ env.OUT_DIR }}', 'out')
    .replaceAll('${{ env.TAR_NAME }}', 'qualified.tar.gz').replaceAll('${{ github.sha }}', revision);
  const bash = process.platform === 'win32' ? 'C:/Program Files/Git/bin/bash.exe' : 'bash';
  const run = script => {
    const result = spawnSync(bash, ['--noprofile', '--norc', '-c', 'set -euo pipefail\n' + script], {
      cwd: input.root, encoding: 'utf8', timeout: 20000, windowsHide: true,
    });
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    return result;
  };
  run(expand(inventory.run) + '\n' + expand(archive.run));
  const prepared = createPlan(input);
  writeFileSync(join(input.root, 'installer-plan.json'), JSON.stringify(prepared.plan));
  writeFileSync(join(input.root, 'ci-qualification.json'), prepared.qualificationRaw);
  const installer = readFileSync(resolve('scripts/deploy-vps.sh'), 'utf8').replaceAll('\r\n', '\n');
  const consumer = /package_check\(\) \{\n[^\n]*<<'PY'\n([\s\S]*?)\nPY\n\}/.exec(installer);
  assert.ok(consumer, 'Exercise the real package admission body without service or database phases');
  const verify = () => spawnSync('python', ['-B', '-', join(input.root, 'installer-plan.json'),
    input.root, input.archive, revision], { input: consumer[1], encoding: 'utf8', timeout: 20000, windowsHide: true });
  const accepted = verify();
  assert.equal(accepted.status, 0, accepted.stderr || accepted.error?.message);
  const manifest = JSON.parse(readFileSync(join(output, 'deployment-manifest.json'), 'utf8'));
  assert.ok(manifest.files.some(file => file.path === 'fe/assets/русский файл.js'));
  assert.equal(prepared.plan.archive.sha256, sha256(readFileSync(input.archive)));
  assert.ok(!manifest.files.some(file => file.path.startsWith('deploy/')));
  // The former workflow's recursive root tar must remain rejected even when
  // the manifest exists and every individual application byte is qualified.
  run('tar -czf qualified.tar.gz -C out .');
  const legacyPlan = { ...prepared.plan, archive: { sha256: sha256(readFileSync(input.archive)), bytes: lstatSync(input.archive).size } };
  writeFileSync(join(input.root, 'installer-plan.json'), JSON.stringify(legacyPlan));
  const rejected = verify();
  assert.equal(rejected.status, 1, rejected.stderr || rejected.error?.message);
  assert.match(rejected.stderr, /AssertionError/);
  assert.ok(readdirSync(output).includes('deploy'));
});

test('failed or cancelled producer jobs cannot authorise deployment', () => {
  for (const name of ['frontend', 'backend-format', 'backend']) {
    const needs = JSON.parse(env.ARTIFACT_NEEDS_JSON);
    needs[name].result = 'failure';
    assert.throws(() => qualifiedContext({ ...env, ARTIFACT_NEEDS_JSON: JSON.stringify(needs) }, revision, 'win32'), /producer gates/);
  }
});

test('changed package bytes or unreviewed release catalog cannot qualify', t => {
  const input = fixture(t);
  writeFileSync(join(input.root, 'out/fe/index.html'), 'changed');
  assert.throws(() => createPlan(input), /changed after preparation/);
});

test('a missing private authorisation cannot stop production', t => {
  const input = fixture(t);
  input.operator.authoriseAutomaticMainDelivery = false;
  assert.throws(() => createPlan(input), /operator authorisation/);
});

test('changed recovery archive or history receipt fails before stopping production', t => {
  const input = fixture(t);
  writeFileSync(join(input.root, 'base.tar'), 'XXXXXXXX');
  assert.throws(() => createPlan(input), /Recovery archive content/);
  writeFileSync(input.operator.historyReceiptPath, '{}');
  assert.throws(() => createPlan(input), /history receipt changed/);
});
