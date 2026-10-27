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
        with self.assertRaisesRegex(AssertionError, 'Unreviewed migration'):
            self.compare()
        self.plan['expectedMigration'] = None
        self.after['baselineSha256'] = '0'*64
        with self.assertRaisesRegex(AssertionError, 'baseline bytes'):
            self.compare()

    def add_payment_attempt_migration(self):
        delta = json.loads(Path(__file__).with_name('deploy-payment-attempt-delta.json').read_text(encoding='utf-8'))
        owner = 'existing_owner'
        for key in delta:
            if key not in ('columns', 'migrations'):
                self.before['schemaObjects'].setdefault(key, [])
        self.before['schemaObjects']['relations'].append(dict(name='__EFMigrationsHistory', owner=owner))
        self.a_path.write_text(json.dumps(self.before))
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-10-26T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)
        for key, change in delta.items():
            self.assertEqual(change['removed'], [])
            rows = copy.deepcopy(change['added'])
            for row in rows:
                if row.get('owner') == '__migration_owner__':
                    row['owner'] = owner
            if key == 'columns': self.after['schemaColumns'].extend(rows)
            elif key == 'migrations': self.after['migrationRows'].extend(rows)
            else: self.after['schemaObjects'][key].extend(rows)
        self.plan['expectedMigration'] = proof.PAYMENT_ATTEMPT_MIGRATION
        self.after['controls']['migrations'] = sorted(self.before['controls']['migrations'] + [proof.PAYMENT_ATTEMPT_MIGRATION])

    def test_reviewed_additive_migration_is_exact_and_preserves_legacy_history(self):
        self.add_payment_attempt_migration()
        result = self.compare('startup')
        self.assertTrue(result['migrationHistory209Exact'])
        self.assertEqual(len(self.after['migrationRows']), 210)
        self.assertIn('20260521190030_AddTaskContractorPaymentsPostgres', self.after['controls']['migrations'])
        with self.assertRaises(AssertionError):
            self.compare('unchanged')

    def test_reviewed_migration_rejects_unexpected_changes(self):
        self.add_payment_attempt_migration()
        original = copy.deepcopy(self.after)
        mutations = [
            lambda a: a['schemaColumns'][-1].update(type='text'),
            lambda a: a['schemaObjects']['indexes'].pop(),
            lambda a: a['schemaObjects']['relations'][-1].update(owner='other_owner'),
            lambda a: a['migrationRows'][0].update(ProductVersion='tampered'),
            lambda a: a['migrationRows'][-1].update(ProductVersion='tampered'),
            lambda a: a['controls']['migrations'].append('unexpected'),
            lambda a: a['fingerprints'].pop(),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                self.after = copy.deepcopy(original)
                mutate(self.after)
                with self.assertRaises(AssertionError):
                    self.compare('startup')

    def add_payment_and_history_migrations(self):
        delta = json.loads(Path(__file__).with_name('deploy-payment-history-delta.json').read_text(encoding='utf-8'))
        owner = 'existing_owner'
        for key in delta:
            if key not in ('columns', 'migrations'):
                self.before['schemaObjects'].setdefault(key, [])
        self.before['schemaObjects']['relations'].append(dict(name='__EFMigrationsHistory', owner=owner))
        self.before['chatProjectionColumns'] = {table: ['Id', 'ExistingValue'] for table in proof.CHAT_TABLES}
        self.before['chatFingerprints'] = [dict(table=table, count=1, hash='b'*32) for table in proof.CHAT_TABLES]
        self.before['chatHistoryNonDefaults'] = None
        self.before['schemaColumns'].extend(dict(table=table, name=name) for table in proof.CHAT_TABLES for name in ['Id', 'ExistingValue'])
        self.a_path.write_text(json.dumps(self.before))
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-10-26T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)
        self.after['chatHistoryNonDefaults'] = 0
        for key, change in delta.items():
            self.assertEqual(change['removed'], [])
            rows = copy.deepcopy(change['added'])
            for row in rows:
                if row.get('owner') == '__migration_owner__':
                    row['owner'] = owner
            if key == 'columns': self.after['schemaColumns'].extend(rows)
            elif key == 'migrations': self.after['migrationRows'].extend(rows)
            else: self.after['schemaObjects'][key].extend(rows)
        self.plan['expectedMigration'] = proof.PAYMENT_AND_HISTORY_MIGRATIONS
        self.after['controls']['migrations'] = sorted(self.before['controls']['migrations'] + [proof.PAYMENT_ATTEMPT_MIGRATION, proof.CHAT_HISTORY_MIGRATION])

    def test_reviewed_pair_preserves_all_chat_and_financial_rows(self):
        self.add_payment_and_history_migrations()
        result = self.compare('startup')
        self.assertTrue(result['migrationHistory209Exact'])
        self.assertEqual(result['protectedChatTables'], 6)
        self.assertEqual(len(self.after['migrationRows']), 211)

    def test_reviewed_pair_checks_unchanged_stopped_baseline_before_startup(self):
        self.add_payment_and_history_migrations()
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-10-26T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)
        self.assertEqual(self.compare('unchanged')['phase'], 'unchanged')
        self.after['chatFingerprints'][1]['hash'] = 'different'
        with self.assertRaisesRegex(AssertionError, 'chat rows'):
            self.compare('unchanged')

    def test_reviewed_pair_rejects_chat_or_schema_changes(self):
        self.add_payment_and_history_migrations()
        original = copy.deepcopy(self.after)
        mutations = [
            lambda a: a['chatFingerprints'][0].update(hash='altered'),
            lambda a: a['chatFingerprints'][0].update(table=proof.CHAT_TABLES[1]),
            lambda a: a['chatProjectionColumns'][proof.CHAT_TABLES[0]].pop(),
            lambda a: a.update(chatHistoryNonDefaults=1),
            lambda a: a['schemaColumns'][-1].update(type='unreviewed-type'),
            lambda a: a['migrationRows'].pop(),
            lambda a: a['controls']['migrations'].append('unexpected'),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                self.after = copy.deepcopy(original)
                mutate(self.after)
                with self.assertRaises(AssertionError):
                    self.compare('startup')


if __name__ == '__main__':
    unittest.main()
