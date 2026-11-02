import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { closeSync, fstatSync, lstatSync, openSync, readFileSync, readSync } from 'node:fs';
import { join } from 'node:path';
import { requireTrustedHost } from './verify-xsenus-runner.mjs';
import { sha256 } from './prepare-deployment-package.mjs';
import { canonicalBytes, safeRelativePath } from './xsenus-artifact-store.mjs';

const candidateReviewSha = 'e8123ac994faee7be91a20dc308cb14a7a5a0c06657ab621b160e9aa919d35e8';
const candidateDeltaFileSha = '4fc76c2c996af04d8cd8bcc298797be8f9a1ab59111ed316fd95703ff435cf9a';
const canonicalHash = value => sha256(canonicalBytes(value).subarray(0, -1));

export function fileHash(path) {
  const before = lstatSync(path);
  assert.ok(before.isFile() && !before.isSymbolicLink() && before.nlink === 1, 'Independent ordinary evidence file required');
  const signature = stat => [stat.dev, stat.ino, stat.size, stat.mtimeMs, stat.ctimeMs];
  const fd = openSync(path, 'r');
  const digest = createHash('sha256');
  try {
    assert.deepEqual(signature(fstatSync(fd)), signature(before));
    const block = Buffer.allocUnsafe(1024 * 1024);
    for (let length; (length = readSync(fd, block, 0, block.length, null)) !== 0;) digest.update(block.subarray(0, length));
    assert.deepEqual(signature(fstatSync(fd)), signature(before), 'Evidence changed while hashing');
  } finally { closeSync(fd); }
  assert.deepEqual(signature(lstatSync(path)), signature(before), 'Evidence path changed while hashing');
  return digest.digest('hex');
}

export function reviewedCandidate(root) {
  const path = join(root, 'scripts/deploy-roadmap-candidate-review.json');
  const raw = readFileSync(path);
  assert.equal(sha256(raw), fileHash(path), 'Reviewed candidate file changed while reading');
  const review = JSON.parse(raw.toString('utf8'));
  assert.equal(canonicalHash(review), candidateReviewSha, 'Reviewed candidate admission changed');
  return review;
}

export function validateReviewedBaseline(baseline, root) {
  const review = reviewedCandidate(root);
  const candidateId = review.migrationIds.join('+');
  assert.ok([null, '20261027005432_AddPaymentCreateAttempts', '20261027005432_AddPaymentCreateAttempts+20261027014154_AddPersonalChatHistory', candidateId].includes(baseline.expectedMigration), 'Only the reviewed additive migrations may be admitted');
  if (baseline.expectedMigration !== candidateId) return;
  assert.equal(baseline.previousRevision, review.previousRevision, 'Reviewed candidate previous revision differs');
  assert.equal(baseline.expectedReleaseCatalogSha256, review.releaseCatalogSha256, 'Reviewed candidate catalog differs');
  assert.ok(Array.isArray(baseline.expectedReleaseSeeds) && baseline.expectedReleaseSeeds.length === review.releaseSeedsCount, 'Exact reviewed candidate release seeds required');
  assert.equal(canonicalHash(baseline.expectedReleaseSeeds), review.releaseSeedsSha256, 'Exact reviewed candidate release seeds required');
  const path = join(root, 'scripts/deploy-roadmap-candidate-delta.json');
  const raw = readFileSync(path);
  assert.equal(sha256(raw), fileHash(path), 'Reviewed schema file changed while reading');
  // Schema JSON contains PostgreSQL int64 bounds; JSON.parse would round them.
  // Pin its exact LF-normalized bytes here; Python checks the canonical schema.
  assert.equal(sha256(Buffer.from(raw.toString('utf8').replaceAll('\r\n', '\n'))), candidateDeltaFileSha, 'Reviewed candidate schema fixture changed');
}

export function qualifiedContext(env, revision, platform = process.platform) {
  assert.match(revision ?? '', /^[a-f0-9]{40}$/);
  requireTrustedHost(env, platform);
  assert.equal(env.OUT_DIR, 'out');
  assert.equal(env.GITHUB_ACTIONS, 'true', 'Only actual GitHub Actions may qualify a deployment');
  assert.equal(env.GITHUB_REF, 'refs/heads/main');
  assert.equal(env.GITHUB_JOB, 'deploy');
  assert.equal(env.GITHUB_SHA, revision);
  assert.equal(env.GITHUB_WORKFLOW_SHA, revision);
  for (const name of ['GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT']) assert.match(env[name] ?? '', /^[1-9][0-9]*$/);
  const needs = JSON.parse(env.ARTIFACT_NEEDS_JSON ?? 'null');
  assert.ok(needs !== null && typeof needs === 'object' && !Array.isArray(needs), 'Actual mandatory producer needs are required');
  assert.deepEqual(Object.keys(needs).sort(), ['backend', 'backend-format', 'frontend']);
  assert.ok(Object.values(needs).every(job => job.result === 'success'), 'All mandatory producer gates must pass');
  const backendArtifactId = needs.backend.outputs['backend-artifact-id'];
  const frontendArtifactId = needs.frontend.outputs['frontend-artifact-id'];
  for (const id of [backendArtifactId, frontendArtifactId]) assert.match(id ?? '', /^xsenus-local-v1:[a-f0-9]{64}$/);
  assert.notEqual(backendArtifactId, frontendArtifactId);
  const backendManifestSha256 = needs.backend.outputs['manifest-sha'];
  assert.match(backendManifestSha256 ?? '', /^[a-fA-F0-9]{64}$/);
  return { schemaVersion: 1, executionKind: 'github-actions-qualified-artifacts', ciQualified: true,
    releaseSha: revision, repository: env.GITHUB_REPOSITORY, ref: env.GITHUB_REF, job: env.GITHUB_JOB,
    workflowSha: env.GITHUB_WORKFLOW_SHA, runId: env.GITHUB_RUN_ID, runAttempt: env.GITHUB_RUN_ATTEMPT,
    needs, backendArtifactId, frontendArtifactId, backendManifestSha256 };
}

export function createPlan({ root, archive, revision, operator, env = process.env, now = new Date(), platform = process.platform }) {
  const context = qualifiedContext(env, revision, platform);
  assert.equal(operator.authoriseAutomaticMainDelivery, true, 'Private operator authorisation is required');
  const baseline = JSON.parse(readFileSync(join(root, 'scripts/deployment-baseline.json'), 'utf8'));
  assert.equal(baseline.expectedProtectedTables.length, 16);
  assert.equal(baseline.expectedAddedProtectedTables.length, 0);
  validateReviewedBaseline(baseline, root);
  const packageRoot = join(root, env.OUT_DIR || 'out');
  const manifest = JSON.parse(readFileSync(join(packageRoot, 'deployment-manifest.json'), 'utf8'));
  assert.equal(manifest.revision, revision);
  const backendPackageFiles = {}, frontendRawFiles = {};
  const seen = new Set();
  for (const file of manifest.files) {
    safeRelativePath(file.path);
    assert.ok(!seen.has(file.path.toLowerCase()), 'Duplicate qualified package path');
    seen.add(file.path.toLowerCase());
    assert.match(file.sha256, /^[a-f0-9]{64}$/);
    assert.equal(fileHash(join(packageRoot, file.path)), file.sha256, 'Qualified package changed after preparation');
    if (file.path.startsWith('be/')) backendPackageFiles[file.path.slice(3)] = file.sha256;
    else {
      assert.ok(file.path.startsWith('fe/'));
      if (file.path !== 'fe/deploy-revision.txt') frontendRawFiles[file.path.slice(3)] = file.sha256;
    }
  }
  const activeReleaseJsonSha256 = backendPackageFiles['AppReleases/releases.json'];
  assert.equal(activeReleaseJsonSha256, baseline.expectedReleaseCatalogSha256, 'Review all release catalog changes before delivery');
  const qualification = { ...context, backendPackageFiles, frontendRawFiles, activeReleaseJsonSha256,
    expectedReleaseSeeds: baseline.expectedReleaseSeeds };
  const qualificationRaw = Buffer.from(JSON.stringify(qualification, null, 2) + '\n');
  const historyRaw = readFileSync(operator.historyReceiptPath);
  assert.equal(sha256(historyRaw), operator.historyReceiptSha256, 'Accepted history receipt changed');
  const historyManifestRaw = readFileSync(operator.historyManifestPath);
  assert.equal(sha256(historyManifestRaw), operator.historyManifestSha256, 'Accepted history manifest changed');
  const history = JSON.parse(historyRaw);
  assert.ok(history.schemaVersion === 2 && history.rootAccepted === true && history.verifiedOutsideVps === true);
  assert.equal(history.currentManifestSha256, operator.historyManifestSha256);
  // The retained recovery archives are checked for availability and exact bytes
  // before stopping the service. This is recovery evidence, not an application test.
  for (const reference of history.archiveReferences) {
    assert.equal(lstatSync(reference.path).size, reference.bytes, 'Recovery archive size differs');
    assert.equal(fileHash(reference.path), reference.sha256, 'Recovery archive content differs');
  }
  const backupUtc = now.toISOString().slice(0, 19).replaceAll(/[-:]/g, '') + 'Z';
  const plan = { ...baseline, rootAccepted: true, runAuthorised: true, phase: 'backup',
    status: 'ROOT_ADOPTED_FINAL_DELIVERY', quietWindowAccepted: true,
    releaseSha: revision, backupUtc,
    archive: { sha256: fileHash(archive), bytes: lstatSync(archive).size },
    expectedApiSha256: backendPackageFiles['PayPlanner.Api.dll'],
    expectedMigrationsSha256: backendPackageFiles['PayPlanner.Api.Migrations.dll'],
    expectedFrontendRawFileCount: Object.keys(frontendRawFiles).length,
    installerSha256: fileHash(join(root, 'scripts/deploy-vps.sh')),
    databaseProofSha256: fileHash(join(root, 'scripts/deploy-db-proof.py')),
    historyProofSha256: fileHash(join(root, 'scripts/deploy-history-source-proof.py')),
    stoppedProofSha256: fileHash(join(root, 'scripts/deploy-stopped-source-proof.py')),
    rollbackAfterStartup: 'halt-for-manual-review',
    qualificationReceipt: { serverRelativePath: 'ci-qualification.json', sha256: sha256(qualificationRaw) },
    historyManifest: { serverRelativePath: 'history-manifest.json', sha256: sha256(historyManifestRaw) },
    offsiteHistoryReceipt: { serverRelativePath: 'history-offsite-verified.json', sha256: sha256(historyRaw) },
    offsiteReceipt: null };
  return { plan, qualificationRaw, historyRaw, historyManifestRaw };
}
