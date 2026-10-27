"""Exercise the installer's exact admission body without invoking its shell phases."""
import copy
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


class DeliveryAdmissionTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='pp-ci-admission-fixture-')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.scripts = Path(__file__).parent
        self.sources = ['deploy-vps.sh', 'deploy-db-proof.py', 'deploy-history-source-proof.py', 'deploy-stopped-source-proof.py']
        for name in self.sources:
            (self.root/name).write_bytes((self.scripts/name).read_bytes())
        source = (self.scripts/'deploy-vps.sh').read_text()
        self.body = re.search(r'CONFIG_VALUES=.*?<<\'PY\'\n(.*?)\nPY', source, re.S).group(1)
        self.revision = 'a'*40
        self.history_manifest = dict(schemaVersion=1, root='/opt/payplanner/shared/backups',
                                     entries=[dict(path='.', kind='directory'), dict(path='fixture.backup', kind='file')])
        coverage = {'.': 1, 'fixture.backup': 0}
        coverage_raw = (json.dumps(coverage, ensure_ascii=True, sort_keys=True, separators=(',', ':'))+'\n').encode()
        self.history = dict(schemaVersion=2, rootAccepted=True, verifiedOutsideVps=True,
            historyRoot='/opt/payplanner/shared/backups', currentManifestSha256=self.write('history-manifest.json', self.history_manifest),
            sourceBeforeAfterExact=True, allCurrentEntriesCovered=True, allFileHashesMatched=True, allEntryMetadataMatched=True,
            fullDiskReadbackVerifiedInClosedClient=True, retainedForRecovery=True, previousRecoveryPointRetained=True,
            nativeDeltaTarExitCode=0, nativeDeltaTarAclXattrFlags=True, localEvidenceIsCI=False,
            archiveReferences=[dict(sha256='1'*64, bytes=1), dict(sha256='2'*64, bytes=1)],
            sourceEntryCount=2, sourceFileCount=1, coverage=coverage, coverageSha256=hashlib.sha256(coverage_raw).hexdigest())
        self.q = dict(schemaVersion=1, executionKind='github-actions-qualified-artifacts', ciQualified=True,
            releaseSha=self.revision, repository='Xsenus/PayPlanner', ref='refs/heads/main', job='deploy', workflowSha=self.revision,
            runId='123', runAttempt='1', backendArtifactId='xsenus-local-v1:'+'b'*64,
            frontendArtifactId='xsenus-local-v1:'+'c'*64, backendManifestSha256='d'*64,
            needs={'frontend': dict(result='success', outputs={'frontend-artifact-id': 'xsenus-local-v1:'+'c'*64}),
                   'backend-format': dict(result='success', outputs={}),
                   'backend': dict(result='success', outputs={'backend-artifact-id': 'xsenus-local-v1:'+'b'*64, 'manifest-sha': 'd'*64})},
            backendPackageFiles={'PayPlanner.Api.dll': 'e'*64},
            frontendRawFiles={n: 'f'*64 for n in ['index.html', 'payplanner-app-cache.js', 'payplanner-push-worker.js', 'payplanner-worker.js']},
            expectedReleaseSeeds=[])
        self.plan = json.loads((self.scripts/'deployment-baseline.json').read_text())
        self.plan.update(rootAccepted=True, runAuthorised=True, phase='backup', status='ROOT_ADOPTED_FINAL_DELIVERY',
            quietWindowAccepted=True, releaseSha=self.revision, backupUtc='20261026T120000Z', archive=dict(sha256='e'*64, bytes=1),
            expectedApiSha256='e'*64, expectedMigrationsSha256='f'*64, expectedFrontendRawFileCount=4,
            installerSha256=self.digest(self.root/'deploy-vps.sh'), databaseProofSha256=self.digest(self.root/'deploy-db-proof.py'),
            historyProofSha256=self.digest(self.root/'deploy-history-source-proof.py'), stoppedProofSha256=self.digest(self.root/'deploy-stopped-source-proof.py'),
            rollbackAfterStartup='halt-for-manual-review',
            historyManifest=dict(serverRelativePath='history-manifest.json', sha256=self.write('history-manifest.json', self.history_manifest)),
            offsiteReceipt=None)

    def digest(self, path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def write(self, name, value):
        path = self.root/name
        path.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8')
        return self.digest(path)

    def admission(self, expected=0):
        self.plan['offsiteHistoryReceipt'] = dict(serverRelativePath='history-offsite-verified.json', sha256=self.write('history-offsite-verified.json', self.history))
        self.plan['qualificationReceipt'] = dict(serverRelativePath='ci-qualification.json', sha256=self.write('ci-qualification.json', self.q))
        digest = self.write('installer-plan.json', self.plan)
        result = subprocess.run([sys.executable, '-B', '-', str(self.root/'installer-plan.json'), digest,
            str(self.root/'deploy-vps.sh'), str(self.root/'deploy-db-proof.py'), str(self.root/'deploy-history-source-proof.py'), 'backup'],
            input=self.body, text=True, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, expected, result.stderr)
        return result

    def test_actual_admission_body_accepts_a_complete_synthetic_ci_delivery(self):
        self.assertTrue(self.admission().stdout.startswith(self.revision+'|'))

    def test_reviewed_payment_migration_is_admitted_only_by_its_exact_id(self):
        self.plan['expectedMigration'] = '20261027005432_AddPaymentCreateAttempts'
        self.assertTrue(self.admission().stdout.startswith(self.revision+'|'))
        self.plan['expectedMigration'] += '_unexpected'
        self.admission(1)

    def test_reviewed_payment_and_history_pair_is_admitted_only_exactly(self):
        self.plan['expectedMigration'] = '20261027005432_AddPaymentCreateAttempts+20261027014154_AddPersonalChatHistory'
        self.assertTrue(self.admission().stdout.startswith(self.revision+'|'))
        self.plan['expectedMigration'] += '_unexpected'
        self.admission(1)

    def test_local_evidence_cannot_replace_ci_qualification(self):
        self.q['executionKind'] = 'local-passed-tests'
        self.assertIn('Actual CI qualification required', self.admission(1).stderr)

    def test_each_mandatory_producer_must_have_succeeded(self):
        for name in self.q['needs']:
            with self.subTest(name=name):
                self.q['needs'][name]['result'] = 'failure'
                self.admission(1)
                self.q['needs'][name]['result'] = 'success'

    def test_previous_production_revision_cannot_be_rebased_by_guess(self):
        self.plan['previousRevision'] = 'b'*40
        self.assertIn('Reassess changed production baseline', self.admission(1).stderr)

    def test_unsafe_backward_restart_or_migration_rejected(self):
        for key, value in [('rollbackAfterStartup', 'restart-old'), ('expectedMigration', 'new-migration')]:
            old = self.plan[key]
            self.plan[key] = value
            self.admission(1)
            self.plan[key] = old

    def test_changed_helper_bytes_fail_before_a_server_phase(self):
        (self.root/'deploy-stopped-source-proof.py').write_text('changed')
        self.assertIn('Stopped source proof changed', self.admission(1).stderr)

    def test_missing_offsite_coverage_or_stale_manifest_rejected(self):
        self.history['coverage'].pop('fixture.backup')
        self.admission(1)

    def test_producer_artifact_or_workflow_identity_substitution_rejected(self):
        for key, value in [('workflowSha', 'b'*40), ('backendArtifactId', self.q['frontendArtifactId'])]:
            old = self.q[key]
            self.q[key] = value
            self.admission(1)
            self.q[key] = old

    def test_protected_document_tables_cannot_be_omitted(self):
        self.plan['expectedProtectedTables'].remove('Acts')
        self.admission(1)


if __name__ == '__main__':
    unittest.main()
