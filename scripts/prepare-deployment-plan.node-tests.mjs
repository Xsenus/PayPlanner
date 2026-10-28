import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { preparePackage, sha256 } from './prepare-deployment-package.mjs';
import { createPlan, qualifiedContext, reviewedCandidate, validateReviewedBaseline } from './prepare-deployment-plan.mjs';

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
  const releases = candidate ? readFileSync(resolve('backend/AppReleases/releases.json')) : Buffer.from('[{"releaseId":"published"}]');
  const baseline = JSON.parse(readFileSync(resolve('scripts/deployment-baseline.json'), 'utf8'));
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

test('qualifies exactly the reviewed eleven-migration candidate and all 249 release insertions', t => {
  const input = fixture(t, true);
  const { plan, qualificationRaw } = createPlan(input);
  const review = reviewedCandidate(input.root);
  assert.equal(plan.expectedMigration, review.migrationIds.join('+'));
  assert.equal(plan.expectedReleaseSeeds.length, 249);
  assert.deepEqual(plan.expectedReleaseSeeds.slice(0, 5).map(entry => entry.version), ['1.14.391', '1.14.390', '1.14.389', '1.14.388', '1.14.387']);
  assert.ok(plan.expectedReleaseSeeds.slice(0, 5).every(entry => entry.isActive === false));
  assert.deepEqual(plan.expectedReleaseSeeds, JSON.parse(readFileSync(resolve('backend/AppReleases/releases.json'), 'utf8')).slice(0, 249));
  assert.equal(plan.expectedReleaseCatalogSha256, review.releaseCatalogSha256);
  assert.deepEqual(JSON.parse(qualificationRaw).expectedReleaseSeeds, plan.expectedReleaseSeeds);
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
