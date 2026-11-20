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
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('database_proof', Path(__file__).with_name('deploy-db-proof.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class DatabaseProofTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='pp-database-proof-')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.plan = json.loads(Path(__file__).with_name('deployment-baseline.json').read_text(encoding='utf-8'))
        self.plan['expectedMigration'] = None
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

    def configure_installed_catalog(self, annual=False):
        current = json.loads(Path(__file__).with_name('deployment-baseline-act-annual.json').read_text(encoding='utf-8'))
        if annual:
            delta = proof.read_json(Path(__file__).with_name('deploy-act-annual-delta.json'))
            migration = delta['migrations']['added'][0]
            current['expectedBaselineMigrations'] = sorted(current['expectedBaselineMigrations'] + [migration['MigrationId']])
            current['expectedBaselineMigrationRows'] = sorted(current['expectedBaselineMigrationRows'] + [migration], key=lambda row: row['MigrationId'])
            current['expectedBaselineMigrationIdsSha256'] = hashlib.sha256(proof.canonical(current['expectedBaselineMigrations']).encode()).hexdigest()
        for key in ['expectedBaselineMigrations', 'expectedBaselineMigrationRows', 'expectedBaselineMigrationIdsSha256']:
            self.plan[key] = current[key]
        self.before['controls']['migrations'] = self.plan['expectedBaselineMigrations']
        self.before['migrationRows'] = self.plan['expectedBaselineMigrationRows']
        self.before['chatHistoryNonDefaults'] = 2
        for prefix, tables in [('chat', proof.CHAT_TABLES), ('supplemental', proof.SUPPLEMENTAL_TABLES)]:
            self.before[prefix + 'ProjectionColumns'] = {table: ['Id'] for table in tables}
            self.before[prefix + 'Fingerprints'] = [dict(table=table, count=1, hash='b'*32) for table in tables]
            self.before['schemaColumns'].extend(dict(table=table, name='Id') for table in tables)
        policy_tables = set(proof.POLICY_TABLES)
        self.before['schemaColumns'].extend(dict(table=table, name='Id')
                                            for table in self.before['projectionColumns'] if table not in policy_tables)
        if annual:
            annual_columns = sorted(
                (column for column in delta['columns']['added'] if column['table'] in proof.ACT_ANNUAL_NEW_TABLES),
                key=lambda column: (column['table'], column['position']),
            )
            columns = {table: [column['name'] for column in annual_columns if column['table'] == table]
                       for table in proof.ACT_ANNUAL_NEW_TABLES}
            self.before['actAnnualProjectionColumns'] = columns
            self.before['actAnnualNewTableCounts'] = {table: 2 for table in columns}
            self.before['actAnnualFingerprints'] = [
                dict(table=table, count=2, hash='c'*32) for table in columns
            ]
            self.before['schemaColumns'].extend(annual_columns)
            self.before['schemaObjects']['constraints'].extend(
                row for row in delta['constraints']['added'] if row['table'] in proof.ACT_ANNUAL_NEW_TABLES
            )
        self.refresh_proof_pair()
        self.add_seed()

    def refresh_proof_pair(self):
        self.a_path.write_text(json.dumps(self.before))
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-10-26T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)

    def test_installed_227_release_preserves_migration_chat_and_cash_state(self):
        self.configure_installed_catalog()
        result = self.compare('startup')
        self.assertEqual(result['migrationBaselineCount'], 227)
        self.assertEqual(result['protectedChatTables'], len(proof.CHAT_TABLES))
        self.assertEqual(result['protectedSupplementalTables'], len(proof.SUPPLEMENTAL_TABLES))
        self.after['supplementalFingerprints'][0]['hash'] = 'changed'
        with self.assertRaisesRegex(AssertionError, 'cash and request rows'):
            self.compare('startup')

    def test_installed_228_release_preserves_nonempty_annual_act_chat_and_cash_tables(self):
        self.configure_installed_catalog(annual=True)
        result = self.compare('startup')
        self.assertEqual(result['migrationBaselineCount'], 228)
        self.assertEqual(result['protectedActAnnualTables'], 2)
        self.assertEqual(result['protectedChatTables'], 6)
        self.assertEqual(result['protectedSupplementalTables'], 4)
        version = self.after['migrationRows'][-1]['ProductVersion']
        self.after['migrationRows'][-1]['ProductVersion'] = 'changed'
        with self.assertRaisesRegex(AssertionError, 'ProductVersion'):
            self.compare('startup')
        self.after['migrationRows'][-1]['ProductVersion'] = version
        for key, error in [('chatFingerprints', 'Existing chat rows'),
                           ('supplementalFingerprints', 'Existing cash and request rows')]:
            with self.subTest(key=key):
                rows = self.after[key]
                original = rows[0]['hash']
                rows[0]['hash'] = 'd'*32
                with self.assertRaisesRegex(AssertionError, error):
                    self.compare('startup')
                rows[0]['hash'] = original

    def test_installed_228_rejects_changed_annual_act_contents_with_same_counts(self):
        self.configure_installed_catalog(annual=True)
        for row in self.after['actAnnualFingerprints']:
            with self.subTest(table=row['table']):
                row['hash'] = 'd'*32
                with self.assertRaisesRegex(AssertionError, 'Existing annual-act rows changed'):
                    self.compare('startup')
                row['hash'] = 'c'*32

    def test_installed_228_rejects_removing_a_postgresql18_not_null_constraint(self):
        self.configure_installed_catalog(annual=True)
        before = self.before['schemaObjects']['constraints']
        native = [row for row in before if isinstance(row, dict) and row['definition'].startswith('NOT NULL ')]
        self.assertEqual(len(native), 7)
        after = self.after['schemaObjects']['constraints']
        for row in native:
            with self.subTest(constraint=row['name']):
                self.after['schemaObjects']['constraints'] = [item for item in after if item != row]
                with self.assertRaisesRegex(AssertionError, 'Unexpected schema mutation'):
                    self.compare('startup')

    def test_installed_228_requires_every_annual_act_column_and_both_tables(self):
        self.configure_installed_catalog(annual=True)
        self.before['actAnnualProjectionColumns']['ActAnnualNumberCounters'] = ['Year']
        self.refresh_proof_pair()
        with self.assertRaisesRegex(AssertionError, 'must include every baseline column'):
            self.compare()
        self.before['actAnnualProjectionColumns']['ActAnnualNumberCounters'].append('LastNumber')
        self.refresh_proof_pair()
        self.after['actAnnualFingerprints'].pop()
        with self.assertRaisesRegex(AssertionError, 'fingerprint scope changed'):
            self.compare()

    def test_installed_228_cannot_collect_or_compare_without_a_pinned_annual_projection(self):
        self.configure_installed_catalog(annual=True)
        self.assertEqual(len(self.before['migrationRows']), 228)
        columns = self.after['schemaColumns']
        counts = self.after['actAnnualNewTableCounts']
        migrations = self.after['migrationRows']
        self.assertEqual(proof.act_annual_projection(columns, counts, migrations, self.before),
                         self.before['actAnnualProjectionColumns'])
        del self.before['actAnnualProjectionColumns']
        with self.assertRaisesRegex(AssertionError, 'Pinned annual-act table projection differs'):
            proof.act_annual_projection(columns, counts, migrations, self.before)
        self.refresh_proof_pair()
        with self.assertRaisesRegex(AssertionError, 'Installed annual-act projection scope changed'):
            self.compare()

    def test_installed_228_rejects_unreviewed_migration_even_with_updated_plan_hash(self):
        self.configure_installed_catalog(annual=True)
        migrations = self.plan['expectedBaselineMigrations']
        migrations[0] = '00000000000000_UnreviewedMigration'
        self.plan['expectedBaselineMigrationIdsSha256'] = hashlib.sha256(proof.canonical(migrations).encode()).hexdigest()
        with self.assertRaisesRegex(AssertionError, 'exact installed annual-act migration catalog'):
            self.compare('startup')

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


class CandidateRowProtectionTests(unittest.TestCase):
    def states(self, migrated=True):
        before = dict(schemaColumns=[dict(table=t, name=n) for t in ['Payments']+proof.SUPPLEMENTAL_TABLES for n in ['Id', 'ExistingValue']],
            projectionColumns={'Payments': ['Id', 'ExistingValue']},
            supplementalProjectionColumns={t: ['Id', 'ExistingValue'] for t in proof.SUPPLEMENTAL_TABLES},
            supplementalFingerprints=[dict(table=t, count=2, hash='a'*32) for t in proof.SUPPLEMENTAL_TABLES],
            candidateDefaultCounts={t: None for t in proof.CANDIDATE_DEFAULTS},
            candidateNewTableCounts={t: None for t in proof.CANDIDATE_NEW_TABLES})
        after = copy.deepcopy(before)
        if migrated:
            after['candidateDefaultCounts'] = {t: 0 for t in proof.CANDIDATE_DEFAULTS}
            after['candidateNewTableCounts'] = {t: 0 for t in proof.CANDIDATE_NEW_TABLES}
        return before, after

    def test_preserves_existing_cash_and_request_rows_and_checks_empty_additions(self):
        proof.compare_candidate_rows(*self.states(), True)
        proof.compare_candidate_rows(*self.states(False), False)

    def test_rejects_changed_cash_or_request_content_count_and_identity(self):
        for index in range(len(proof.SUPPLEMENTAL_TABLES)):
            for field, value in [('hash', 'altered'), ('count', 3), ('table', 'Other')]:
                with self.subTest(index=index, field=field):
                    before, after = self.states()
                    after['supplementalFingerprints'][index][field] = value
                    with self.assertRaisesRegex(AssertionError, 'cash and request rows'):
                        proof.compare_candidate_rows(before, after, True)

    def test_rejects_omitted_baseline_columns_even_if_both_projections_match(self):
        for key in ['projectionColumns', 'supplementalProjectionColumns']:
            before, after = self.states()
            table = next(iter(before[key]))
            before[key][table].pop()
            after[key][table].pop()
            with self.assertRaisesRegex(AssertionError, 'every baseline column'):
                proof.compare_candidate_rows(before, after, True)

    def test_rejects_hidden_scope_changes_and_incomplete_fingerprints(self):
        for mutate in [lambda a: a['supplementalProjectionColumns'].pop('CashOrders'),
                       lambda a: a['supplementalFingerprints'].pop(),
                       lambda a: a['candidateDefaultCounts'].pop('SalesRequests'),
                       lambda a: a['candidateNewTableCounts'].update(UnreviewedTable=0)]:
            before, after = self.states()
            mutate(after)
            with self.assertRaises(AssertionError):
                proof.compare_candidate_rows(before, after, True)

    def test_rejects_unexpected_assignments_and_preexisting_new_state(self):
        for key in ['candidateDefaultCounts', 'candidateNewTableCounts']:
            for table in self.states()[0][key]:
                for value in [1, -1, None, False]:
                    before, after = self.states()
                    after[key][table] = value
                    with self.assertRaisesRegex(AssertionError, 'unexpected data'):
                        proof.compare_candidate_rows(before, after, True)
                before, after = self.states()
                before[key][table] = 0
                with self.assertRaisesRegex(AssertionError, 'pre-migration state'):
                    proof.compare_candidate_rows(before, after, True)

    def test_collector_uses_bounded_readonly_counts_and_detects_partial_schema(self):
        columns = [dict(table=t, name=n) for t, (names, _) in proof.CANDIDATE_DEFAULTS.items() for n in names]
        objects = {'relations': [dict(name=t, kind='r') for t in proof.CANDIDATE_NEW_TABLES]}
        calls = []
        def query(sql):
            self.assertTrue(sql.startswith('SELECT to_json(count(*)) FROM "'))
            self.assertNotIn('UPDATE', sql)
            calls.append(sql)
            return [0]
        defaults, counts = proof.candidate_row_controls(columns, objects, query)
        self.assertEqual(set(defaults), {'Payments', 'CashInventories', 'CashOrders', 'SalesRequests', 'TaskJournalDeadlineChangeRequests', 'TaskJournalItems', 'CourtDocumentSources'})
        self.assertEqual(set(counts), {'BalanceApplicationReceipts', 'CompanyBankAccountBindings', 'PaymentCreateAttempts', 'PaymentOverdueNotificationOutboxes', 'TaskStatusNotificationOutboxes', 'TaskTimeCorrections', 'TaskTimeEntries', 'CaseCourtNotificationOutboxes'})
        self.assertEqual(len(calls), 15)
        self.assertIn('SELECT to_json(count(*)) FROM "Payments" WHERE "OverdueNotificationToken" IS NOT NULL;', calls)
        self.assertIn('SELECT to_json(count(*)) FROM "CourtDocumentSources" WHERE "AcceptedControlsJson" IS NOT NULL;', calls)
        self.assertTrue(all(value == 0 for value in [*defaults.values(), *counts.values()]))
        columns = [c for c in columns if not (c['table'] == 'SalesRequests' and c['name'] == 'ServiceInvoiceId')]
        with self.assertRaisesRegex(AssertionError, 'Incomplete reviewed candidate columns'):
            proof.candidate_row_controls(columns, objects, query)

    def test_collector_does_not_query_absent_new_columns_or_tables(self):
        def query(_):
            self.fail('The unchanged baseline must not query absent additions')
        defaults, counts = proof.candidate_row_controls([], {'relations': []}, query)
        self.assertTrue(all(value is None for value in [*defaults.values(), *counts.values()]))


class ForeignKeyTriggerIdentityTests(unittest.TestCase):
    def trigger(self, oid=123):
        name = 'RI_ConstraintTrigger_a_'+str(oid)
        return dict(table='Parent', name=name, definition='CREATE CONSTRAINT TRIGGER "'+name+'" AFTER DELETE ON "Parent" FROM "Child" NOT DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION "RI_FKey_noaction_del"()',
            enabled='O', internal=True, constraintName='FK_Child_Parent_ParentId', constraintType='f', constraintTable='Child',
            constraintReferencedTable='Parent', constraintDefinition='FOREIGN KEY ("ParentId") REFERENCES "Parent"("Id")')

    def test_only_cluster_local_oid_is_normalized(self):
        before = self.trigger()
        result = proof.stable_added_trigger(before)
        self.assertEqual(result, proof.stable_added_trigger(self.trigger(987654321)))
        self.assertEqual(before, self.trigger(), 'Normalization must not mutate the immutable observation')
        self.assertEqual(set(result), set(before))
        self.assertEqual({k: v for k, v in result.items() if k not in ['name', 'definition']},
                         {k: v for k, v in before.items() if k not in ['name', 'definition']})

    def test_different_binding_action_direction_or_enabled_state_never_compares_equal(self):
        original = self.trigger()
        expected = proof.stable_added_trigger(original)
        for key, value in [('table', 'OtherParent'), ('enabled', 'D'), ('internal', False), ('constraintName', 'OtherFK'),
                           ('constraintType', 'c'), ('constraintTable', 'OtherChild'), ('constraintReferencedTable', 'OtherParent'),
                           ('constraintDefinition', 'FOREIGN KEY ("OtherParentId") REFERENCES "Parent"("Id")'),
                           ('definition', original['definition'].replace('RI_FKey_noaction_del', 'RI_FKey_cascade_del'))]:
            with self.subTest(key=key):
                changed = {**original, key: value}
                self.assertNotEqual(expected, proof.stable_added_trigger(changed))

    def test_user_trigger_with_system_looking_name_is_not_normalized(self):
        user = {**self.trigger(), 'internal': False}
        self.assertEqual(proof.stable_added_trigger(user), user)
        self.assertNotEqual(proof.stable_added_trigger(user), proof.stable_added_trigger({**self.trigger(999), 'internal': False}))

    def test_missing_binding_malformed_name_or_mismatched_body_is_rejected(self):
        original = self.trigger()
        mutations = [('name', 'UserTrigger'), ('name', 'RI_ConstraintTrigger_a_0'),
                     ('definition', original['definition'].replace('123', '124'))]
        mutations += [(key, None) for key in ['constraintName', 'constraintTable', 'constraintReferencedTable', 'constraintDefinition']]
        for key, value in mutations:
            with self.subTest(key=key, value=value):
                with self.assertRaises(AssertionError):
                    proof.stable_added_trigger({**original, key: value})


class ReviewedCandidateAdmissionTests(unittest.TestCase):
    def test_complete_candidate_plan_is_pinned_before_database_proofs(self):
        plan = json.loads(Path(__file__).with_name('deployment-baseline.json').read_text(encoding='utf-8'))
        expected = proof.reviewed_candidate()['migrationIds']
        self.assertEqual(proof.validate_candidate_plan(plan), expected)
        for mutate in [lambda p: p.update(previousRevision='b'*40),
                       lambda p: p.update(expectedReleaseCatalogSha256='c'*64),
                       lambda p: p['expectedReleaseSeeds'].pop(),
                       lambda p: p['expectedReleaseSeeds'].reverse(),
                       lambda p: p['expectedReleaseSeeds'][0].update(isActive=False),
                       lambda p: p['expectedReleaseSeeds'][0]['items'][0].update(text='unreviewed')]:
            changed = copy.deepcopy(plan)
            mutate(changed)
            with self.assertRaises(AssertionError):
                proof.validate_candidate_plan(changed)

    def test_review_pins_contents_across_line_endings_and_rejects_changed_approval(self):
        review = proof.reviewed_candidate()
        with tempfile.TemporaryDirectory(prefix='pp-candidate-review-') as directory:
            path = Path(directory)/'deploy-roadmap-candidate-review.json'
            path.write_bytes((json.dumps(review, indent=2)+'\n').replace('\n', '\r\n').encode())
            with patch.object(proof, '__file__', str(Path(directory)/'deploy-db-proof.py')):
                self.assertEqual(proof.reviewed_candidate(), review)
                review['releaseSeedsCount'] += 1
                path.write_text(json.dumps(review), encoding='utf-8')
                with self.assertRaisesRegex(AssertionError, 'admission changed'):
                    proof.reviewed_candidate()

    def test_only_the_complete_ordered_reviewed_chain_is_admitted(self):
        ids = proof.reviewed_candidate()['migrationIds']
        self.assertEqual(proof.reviewed_migration_ids('+'.join(ids)), ids)
        self.assertEqual(len(ids), 18)
        for changed in [ids[:11], ids[:-1], ids+['unexpected'], list(reversed(ids)), [ids[0]+'_unexpected', *ids[1:]]]:
            with self.subTest(changed=changed):
                with self.assertRaisesRegex(AssertionError, 'Unreviewed migration'):
                    proof.reviewed_migration_ids('+'.join(changed))

    def test_reviewed_schema_is_exact_and_portable_across_generated_fk_oids(self):
        review = proof.reviewed_candidate()
        delta = json.loads(Path(__file__).with_name('deploy-roadmap-candidate-delta.json').read_text(encoding='utf-8'))
        self.assertEqual(hashlib.sha256(proof.canonical(delta).encode()).hexdigest(), review['schemaDeltaSha256'])
        self.assertTrue(all(not change['removed'] for change in delta.values()))
        self.assertEqual(sorted(row['MigrationId'] for row in delta['migrations']['added']), review['migrationIds'])
        before = dict(schemaColumns=[], migrationRows=[], schemaObjects={key: [] for key in delta if key not in ['columns', 'migrations']})
        before['schemaObjects']['relations'].append(dict(name='__EFMigrationsHistory', owner='fixture_owner'))
        after = copy.deepcopy(before)
        for key, change in delta.items():
            rows = copy.deepcopy(change['added'])
            for index, row in enumerate(rows):
                if 'owner' in row:
                    self.assertEqual(row['owner'], '__migration_owner__')
                    row['owner'] = 'fixture_owner'
                if key == 'triggers':
                    name = row['name']
                    direction = name.removeprefix('__internal_fk_trigger_').removesuffix('__')
                    row['name'] = 'RI_ConstraintTrigger_'+direction+'_'+str(1000+index)
                    row['definition'] = row['definition'].replace('"'+name+'"', '"'+row['name']+'"', 1)
            if key == 'columns': after['schemaColumns'] = rows
            elif key == 'migrations': after['migrationRows'] = rows
            else: after['schemaObjects'][key].extend(rows)
        proof.compare_payment_attempt_schema(before, after, review['schemaDeltaSha256'])
        bad = copy.deepcopy(after)
        bad['schemaObjects']['triggers'][0]['constraintName'] = 'unreviewed'
        with self.assertRaisesRegex(AssertionError, 'schema or history delta'):
            proof.compare_payment_attempt_schema(before, bad, review['schemaDeltaSha256'])



class AnnualActAdmissionTests(unittest.TestCase):
    compare = DatabaseProofTests.compare

    def setUp(self):
        DatabaseProofTests.setUp(self)
        self.plan = json.loads(Path(__file__).with_name('deployment-baseline-act-annual.json').read_text(encoding='utf-8'))
        self.assertEqual(self.plan['expectedMigration'], proof.ACT_ANNUAL_MIGRATION)
        self.assertEqual(len(self.plan['expectedReleaseSeeds']), 29)
        self.before['utc'] = '2026-11-13T00:00:00+00:00'
        self.before['controls']['migrations'] = self.plan['expectedBaselineMigrations']
        self.before['migrationRows'] = self.plan['expectedBaselineMigrationRows']
        self.before['chatHistoryNonDefaults'] = 2
        for prefix, tables in [('chat', proof.CHAT_TABLES), ('supplemental', proof.SUPPLEMENTAL_TABLES)]:
            self.before[prefix + 'ProjectionColumns'] = {table: ['Id'] for table in tables}
            self.before[prefix + 'Fingerprints'] = [dict(table=table, count=1, hash='b'*32) for table in tables]
            self.before['schemaColumns'].extend(dict(table=table, name='Id') for table in tables)
        self.before['schemaColumns'].extend(dict(table=table, name='Id')
            for table in self.before['projectionColumns'] if table not in proof.POLICY_TABLES)
        self.delta = json.loads(Path(__file__).with_name('deploy-act-annual-delta.json').read_text(encoding='utf-8'))
        self.before['schemaObjects'] = {name: [] for name in self.delta if name not in ['columns', 'migrations']}
        self.before['schemaObjects']['relations'] = [dict(name='__EFMigrationsHistory', owner='fixture_owner')]
        self.before['actAnnualNewTableCounts'] = {table: None for table in proof.ACT_ANNUAL_NEW_TABLES}
        self.a_path.write_text(json.dumps(self.before))
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-11-13T00:01:00+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)
        for name, change in self.delta.items():
            self.assertFalse(change['removed'])
            added = copy.deepcopy(change['added'])
            for row in added:
                if 'owner' in row:
                    self.assertEqual(row['owner'], '__migration_owner__')
                    row['owner'] = 'fixture_owner'
            if name == 'columns':
                self.after['schemaColumns'].extend(added)
            elif name == 'migrations':
                self.after['migrationRows'].extend(added)
            else:
                self.after['schemaObjects'][name].extend(added)
        self.after['controls']['migrations'] = sorted(self.plan['expectedBaselineMigrations'] + [proof.ACT_ANNUAL_MIGRATION])
        self.after['actAnnualNewTableCounts'] = {table: 0 for table in proof.ACT_ANNUAL_NEW_TABLES}
        next_item_id = 2
        for release_id, seed in enumerate(self.plan['expectedReleaseSeeds'], start=2):
            self.after['releaseRows'].append(dict(Id=release_id, ReleaseId=seed['releaseId'], Version=seed['version'],
                ReleasedAt=seed['releasedAt'], Title=seed['title'], Summary=seed['summary'], IsActive=True,
                Source=seed.get('source', 'agent'), CreatedAt='2026-11-13T00:00:30+00:00', UpdatedAt='2026-11-13T00:00:30+00:00',
                CreatedByUserId=None, CreatedByUserName=None, UpdatedByUserId=None, UpdatedByUserName=None))
            for order, item in enumerate(seed['items']):
                self.after['releaseItems'].append(dict(Id=next_item_id, AppReleaseId=release_id,
                    Type=item['type'], Text=item['text'], SortOrder=order))
                next_item_id += 1

    def test_exact_annual_startup_preserves_installed_227_and_all_protected_rows(self):
        result = self.compare('startup')
        self.assertEqual(result['migrationBaselineCount'], 227)
        self.assertEqual(result['protectedTables'], 16)
        self.assertEqual(result['protectedChatTables'], 6)
        self.assertEqual(result['protectedSupplementalTables'], 4)
        self.assertEqual(result['reviewedReleaseSeeds'], 29)
        self.assertTrue(result['allColumnsAndRowsExact'])

    def test_legacy_227_receipt_does_not_need_an_annual_projection(self):
        self.assertEqual(len(self.before['migrationRows']), 227)
        self.assertNotIn('actAnnualProjectionColumns', self.before)
        for current in (self.before, self.after):
            with self.subTest(migrations=len(current['migrationRows'])):
                self.assertEqual(proof.act_annual_projection(current['schemaColumns'],
                    current['actAnnualNewTableCounts'], current['migrationRows'], self.before), {})

    def test_stopped_boundary_keeps_schema_history_and_empty_absence_exact(self):
        self.after = copy.deepcopy(self.before)
        self.after['utc'] = '2026-11-13T00:00:30+00:00'
        self.after['baselineSha256'] = proof.digest(self.a_path)
        self.compare('unchanged')
        self.after['actAnnualNewTableCounts']['ActCreateReceipts'] = 0
        with self.assertRaises(AssertionError):
            self.compare('unchanged')

    def test_rejects_any_financial_chat_cash_history_or_schema_mutation(self):
        original = copy.deepcopy(self.after)
        mutations = [lambda state: state['fingerprints'][0].update(hash='changed'),
                     lambda state: state['chatFingerprints'][0].update(hash='changed'),
                     lambda state: state['supplementalFingerprints'][0].update(count=2),
                     lambda state: state.update(chatHistoryNonDefaults=3),
                     lambda state: state['schemaObjects']['indexes'][-1].update(definition='unreviewed'),
                     lambda state: state['migrationRows'][0].update(ProductVersion='unreviewed')]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                self.after = copy.deepcopy(original)
                mutate(self.after)
                with self.assertRaises(AssertionError):
                    self.compare('startup')

    def test_rejects_nonempty_missing_boolean_or_extra_new_table_controls(self):
        original = copy.deepcopy(self.after)
        for table in proof.ACT_ANNUAL_NEW_TABLES:
            for value in [1, True, None]:
                with self.subTest(table=table, value=value):
                    self.after = copy.deepcopy(original)
                    self.after['actAnnualNewTableCounts'][table] = value
                    with self.assertRaises(AssertionError):
                        self.compare('startup')
        for mutate in [lambda controls: controls.pop('ActCreateReceipts'),
                       lambda controls: controls.update(UnreviewedTable=0)]:
            self.after = copy.deepcopy(original)
            mutate(self.after['actAnnualNewTableCounts'])
            with self.assertRaises(AssertionError):
                self.compare('startup')

    def test_rejects_changed_annual_plan_release_seed_or_previous_baseline(self):
        original = copy.deepcopy(self.plan)
        mutations = [lambda plan: plan.update(previousRevision='b'*40),
                     lambda plan: plan.update(expectedMigration=None),
                     lambda plan: plan['expectedReleaseSeeds'].pop(),
                     lambda plan: plan['expectedReleaseSeeds'].reverse(),
                     lambda plan: plan['expectedReleaseSeeds'][0].update(isActive=False),
                     lambda plan: plan['expectedReleaseSeeds'][0]['items'][0].update(text='unreviewed')]
        for mutate in mutations:
            self.plan = copy.deepcopy(original)
            mutate(self.plan)
            with self.assertRaises(AssertionError):
                self.compare('startup')

    def test_postgresql18_native_not_null_scope_matches_all_seven_required_columns(self):
        required = {
            ('ActAnnualNumberCounters', 'Year'),
            ('ActAnnualNumberCounters', 'LastNumber'),
            ('ActCreateReceipts', 'ActorUserId'),
            ('ActCreateReceipts', 'UploadKey'),
            ('ActCreateReceipts', 'ActId'),
            ('ActCreateReceipts', 'CommandSha256'),
            ('ActCreateReceipts', 'Operation'),
        }
        columns = {(row['table'], row['name']) for row in self.delta['columns']['added']
                   if row['table'] in proof.ACT_ANNUAL_NEW_TABLES and not row['nullable']}
        self.assertEqual(columns, required)
        native = [row for row in self.delta['constraints']['added']
                  if row['definition'].startswith('NOT NULL ')]
        self.assertEqual(len(native), len(required))
        actual = set()
        for row in native:
            column = row['definition'].removeprefix('NOT NULL "').removesuffix('"')
            actual.add((row['table'], column))
            self.assertEqual(row['name'], row['table'] + '_' + column + '_not_null')
            self.assertIs(row['validated'], True)
            self.assertIs(row['deferrable'], False)
            self.assertIs(row['deferred'], False)
        self.assertEqual(actual, required)

    def test_rejects_every_missing_or_modified_native_not_null_constraint(self):
        original = copy.deepcopy(self.after)
        native = [row for row in self.delta['constraints']['added']
                  if row['definition'].startswith('NOT NULL ')]
        self.assertEqual(len(native), 7)
        mutations = [('name', 'unreviewed'), ('table', 'Invoices'),
                     ('definition', 'NOT NULL "Unreviewed"'), ('validated', False),
                     ('deferrable', True), ('deferred', True)]
        for expected in native:
            for field, value in [('remove', None)] + mutations:
                with self.subTest(constraint=expected['name'], mutation=field):
                    self.after = copy.deepcopy(original)
                    rows = self.after['schemaObjects']['constraints']
                    row = next(item for item in rows
                               if item['table'] == expected['table'] and item['name'] == expected['name'])
                    if field == 'remove':
                        rows.remove(row)
                    else:
                        row[field] = value
                    with self.assertRaisesRegex(AssertionError, 'Unexpected migration schema or history delta'):
                        self.compare('startup')
        self.after = copy.deepcopy(original)
        extra = copy.deepcopy(native[0])
        extra['name'] = 'unreviewed_extra_not_null'
        self.after['schemaObjects']['constraints'].append(extra)
        with self.assertRaisesRegex(AssertionError, 'Unexpected migration schema or history delta'):
            self.compare('startup')

    def test_new_table_collector_is_readonly_and_avoids_absent_relations(self):
        def absent_query(sql):
            self.fail('The collector queried an absent annual-act table.')
        self.assertEqual(proof.act_annual_row_controls({'relations': []}, absent_query),
                         {table: None for table in proof.ACT_ANNUAL_NEW_TABLES})
        calls = []
        def present_query(sql):
            calls.append(sql)
            return [0]
        relations = [dict(name=table, kind='r') for table in proof.ACT_ANNUAL_NEW_TABLES]
        self.assertEqual(proof.act_annual_row_controls({'relations': relations}, present_query),
                         {table: 0 for table in proof.ACT_ANNUAL_NEW_TABLES})
        self.assertEqual(len(calls), 2)
        self.assertTrue(all(sql.startswith('SELECT to_json(count(*)) FROM "') for sql in calls))


if __name__ == '__main__':
    unittest.main()
