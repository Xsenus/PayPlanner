"""Pure fixtures only: importing the verifier must never invoke PostgreSQL."""
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('database_proof', Path(__file__).with_name('deploy-db-proof.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class DatabaseProofTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='pp-database-proof-')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.plan = json.loads(Path(__file__).with_name('deployment-baseline.json').read_text())
        self.plan['expectedReleaseSeeds'] = []
        tables = self.plan['expectedProtectedTables']
        self.assertEqual(len(tables), 16)
        columns = [dict(table=t, name=proof.POLICY_COLUMN, type='character varying(32)', nullable=True,
                        default=None, identity='', generated='', collation='pg_catalog.default')
                   for t in proof.POLICY_TABLES]
        self.before = dict(schemaVersion=2, utc='2026-10-26T00:00:00+00:00', baselineSha256=None,
                           fingerprints=[dict(table=t, count=1, hash='a'*32) for t in tables],
                           projectionColumns={t: [proof.POLICY_COLUMN] if t in proof.POLICY_TABLES else ['Id'] for t in tables},
                           schemaColumns=columns, schemaObjects={'constraints': ['existing']},
                           controls=dict(legacyActive=0, pendingContractor=0, invalidIndexes=0, invalidConstraints=0,
                                         migrations=self.plan['expectedBaselineMigrations']),
                           migrationRows=self.plan['expectedBaselineMigrationRows'],
                           newPolicyNonNullCounts={t: 0 for t in proof.POLICY_TABLES},
                           releaseRows=[dict(Id=1, ReleaseId='published', Title='user-edited title')],
                           releaseItems=[dict(Id=1, AppReleaseId=1, Text='published item')],
                           releaseSeen=dict(count=3, hash='seen'))
        self.a_path = self.root/'before.json'
        self.a_path.write_text(json.dumps(self.before))
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-10-26T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)

    def compare(self, phase='unchanged'):
        plan, after = self.root/'plan.json', self.root/'after.json'
        plan.write_text(json.dumps(self.plan))
        after.write_text(json.dumps(self.after))
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            proof.compare(str(plan), str(self.a_path), str(after), phase)
        return json.loads(output.getvalue())

    def add_seed(self):
        seed = dict(releaseId='new-reviewed', version='1.14.143', releasedAt='2026-10-26T00:00:00Z',
                    title='New', summary='Available', isActive=True, source='agent',
                    items=[dict(type='fixed', text='Visible fix')])
        self.plan['expectedReleaseSeeds'] = [seed]
        self.after['releaseRows'].append(dict(Id=2, ReleaseId=seed['releaseId'], Version=seed['version'],
            ReleasedAt=seed['releasedAt'], Title=seed['title'], Summary=seed['summary'], IsActive=True,
            Source='agent', CreatedAt='2026-10-26T00:00:30Z', UpdatedAt='2026-10-26T00:00:30Z',
            CreatedByUserId=None, CreatedByUserName=None, UpdatedByUserId=None, UpdatedByUserName=None))
        self.after['releaseItems'].append(dict(Id=2, AppReleaseId=2, Type='fixed', Text='Visible fix', SortOrder=0))

    def test_unchanged_complete_scope(self):
        result = self.compare()
        self.assertEqual(result['protectedTables'], 16)
        self.assertTrue(result['releaseCatalogUnchanged'])

    def test_reviewed_insert_only_at_startup_and_receipt_is_truthful(self):
        self.add_seed()
        with self.assertRaisesRegex(AssertionError, 'before startup'):
            self.compare()
        result = self.compare('startup')
        self.assertEqual(result['phase'], 'startup')
        self.assertFalse(result['releaseCatalogUnchanged'])
        self.assertEqual(result['reviewedReleaseSeeds'], 1)

    def test_protected_row_or_full_column_change_rejected(self):
        for key, value in [('fingerprints', []), ('projectionColumns', {}), ('schemaColumns', []), ('schemaObjects', {})]:
            with self.subTest(key=key):
                old = self.after[key]
                self.after[key] = value
                with self.assertRaises(AssertionError):
                    self.compare('startup')
                self.after[key] = old

    def test_migration_product_version_change_rejected(self):
        self.after['migrationRows'][0]['ProductVersion'] = 'changed'
        with self.assertRaisesRegex(AssertionError, 'ProductVersion'):
            self.compare()

    def test_published_releases_and_items_never_rewritten(self):
        self.add_seed()
        for kind, field in [('releaseRows', 'Title'), ('releaseItems', 'Text')]:
            with self.subTest(kind=kind):
                old = self.after[kind][0][field]
                self.after[kind][0][field] = 'unapproved'
                with self.assertRaisesRegex(AssertionError, 'published/manual'):
                    self.compare('startup')
                self.after[kind][0][field] = old

    def test_unexpected_release_or_changed_item_rejected(self):
        self.add_seed()
        self.after['releaseItems'][-1]['Text'] = 'different'
        with self.assertRaisesRegex(AssertionError, 'content/order'):
            self.compare('startup')

    def test_release_read_markers_and_policy_values_protected(self):
        for field, value in [('releaseSeen', {}), ('newPolicyNonNullCounts', {})]:
            with self.subTest(field=field):
                old = self.after[field]
                self.after[field] = value
                with self.assertRaises(AssertionError):
                    self.compare('startup')
                self.after[field] = old

    def test_no_migration_permission_or_unbound_baseline(self):
        self.plan['expectedMigration'] = 'unapproved'
        with self.assertRaisesRegex(AssertionError, 'No migration'):
            self.compare()
        self.plan['expectedMigration'] = None
        self.after['baselineSha256'] = '0'*64
        with self.assertRaisesRegex(AssertionError, 'baseline bytes'):
            self.compare()


if __name__ == '__main__':
    unittest.main()
