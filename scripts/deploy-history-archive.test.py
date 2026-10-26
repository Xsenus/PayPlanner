import base64
import copy
from decimal import Decimal
import hashlib
import importlib.util
import io
import os
import subprocess
import sys
from pathlib import Path
import tarfile
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('history_proof', Path(__file__).with_name('deploy-history-archive.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class HistoryArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='pp-history-proof-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.contents = {proof.KNOWN_HARDLINK: b'historical independent copy', 'same.backup': b'same', 'old.backup': b'old'}
        self.base = self.manifest(self.contents)
        self.current_contents = {k: v for k, v in self.contents.items() if k != 'old.backup'} | {'new.backup': b'new'}
        self.current = self.manifest(self.current_contents)
        self.current['.']['mtimeNs'] += 1
        self.base_raw, self.current_raw = self.raw(self.base), self.raw(self.current)
        self.plan = proof.plan_history(self.base_raw, self.current_raw)
        self.base_path, self.delta_path = self.root/'base.tar', self.root/'delta.tar'
        self.archive(self.base_path, self.base, self.contents)
        self.delta = {k: self.current[k] for k in self.plan['deltaPaths']}
        self.archive(self.delta_path, self.delta, self.current_contents)

    def manifest(self, contents):
        result = {}
        for name, data in contents.items():
            result[name] = dict(path=name, kind='file', mode=0o600, uid=123, gid=456,
                                mtimeNs=1700000000123456789, xattrs={}, size=len(data),
                                nlink=3 if name == proof.KNOWN_HARDLINK else 1,
                                sha256=hashlib.sha256(data).hexdigest())
            parent = name.rpartition('/')[0]
            while parent:
                result.setdefault(parent, dict(path=parent, kind='directory', mode=0o700, uid=123, gid=456,
                                               mtimeNs=1700000000123456789, xattrs={}))
                parent = parent.rpartition('/')[0]
        result['.'] = dict(path='.', kind='directory', mode=0o700, uid=123, gid=456, mtimeNs=1700000000123456789, xattrs={})
        return result

    def raw(self, entries):
        return proof.canonical(dict(schemaVersion=1, root=proof.ROOT, entries=[entries[k] for k in sorted(entries)]))

    def archive(self, path, entries, contents, change=None):
        with tarfile.open(path, 'w', format=tarfile.PAX_FORMAT, encoding='utf-8', errors='surrogateescape') as archive:
            for name, entry in sorted(entries.items()):
                member = tarfile.TarInfo('backups' if name == '.' else 'backups/'+name)
                member.uid, member.gid, member.mode = entry['uid'], entry['gid'], entry['mode']
                member.mtime = entry['mtimeNs']//1000000000
                member.pax_headers = {'mtime': str(Decimal(entry['mtimeNs'])/1000000000)}
                for key, value in entry['xattrs'].items():
                    member.pax_headers['SCHILY.xattr.'+key] = base64.b64decode(value).decode('utf-8', 'surrogateescape')
                if entry['kind'] == 'directory':
                    member.type = tarfile.DIRTYPE
                    data = None
                else:
                    member.size = len(contents[name])
                    data = io.BytesIO(contents[name])
                if change:
                    change(name, member)
                archive.addfile(member, data)

    def verify(self, **overrides):
        args = dict(base_raw=self.base_raw, current_raw=self.current_raw, after_raw=self.current_raw,
                    base_archive=self.base_path, delta_archive=self.delta_path,
                    expected_base_sha=hashlib.sha256(self.base_path.read_bytes()).hexdigest(),
                    expected_delta_sha=hashlib.sha256(self.delta_path.read_bytes()).hexdigest(),
                    reviewed_changes=self.plan['currentManifestSha256'])
        return proof.verify_complete_history(**(args | overrides))

    def test_complete_coverage_retains_removed_point_and_never_claims_ci_or_offsite_acceptance(self):
        result = self.verify()
        self.assertEqual(set(result['coverage']), set(self.current))
        self.assertEqual(result['coverage']['same.backup'], 0)
        self.assertEqual(result['coverage']['new.backup'], 1)
        self.assertEqual(result['coverage']['.'], 1)
        self.assertEqual(result['removedPathsRetainedInBaseArchive'], ['old.backup'])
        self.assertTrue(self.base_path.exists())
        self.assertFalse(result['rootAccepted'] or result['verifiedOutsideVps'] or result['qualifiesCI'] or result['automaticDeletion'])
        self.assertFalse(result['sourceAndArchiveNlinkEquivalent'])
        self.assertEqual(result['sourceHardlinkRecords'], [{'path': proof.KNOWN_HARDLINK, 'nlink': 3}])

    def test_only_identical_file_bytes_and_metadata_are_reused(self):
        for key, value in [('mtimeNs', 1700000000123456790), ('mode', 0o640), ('uid', 124), ('gid', 457),
                           ('xattrs', {'user.note': base64.b64encode(b'changed').decode()})]:
            with self.subTest(field=key):
                entries = copy.deepcopy(self.current)
                entries['same.backup'][key] = value
                plan = proof.plan_history(self.base_raw, self.raw(entries))
                self.assertNotIn('same.backup', plan['reusedPaths'])
                self.assertIn('same.backup', plan['deltaPaths'])

    def test_removed_or_changed_source_requires_exact_review(self):
        for review in [None, '', 'a'*64, hashlib.sha256(self.base_raw).hexdigest()]:
            with self.subTest(review=review):
                with self.assertRaisesRegex(ValueError, 'exact manifest review'):
                    self.verify(reviewed_changes=review)

    def test_source_change_during_transfer_fails_before_adoption(self):
        with self.assertRaisesRegex(ValueError, 'changed during transfer'):
            self.verify(after_raw=self.base_raw)

    def test_wrong_retained_or_delta_hash_is_rejected(self):
        for key in ['expected_base_sha', 'expected_delta_sha']:
            with self.subTest(archive=key):
                with self.assertRaisesRegex(ValueError, 'hash differs'):
                    self.verify(**{key: 'f'*64})

    def test_missing_delta_file_is_rejected(self):
        entries = {k: v for k, v in self.delta.items() if k != 'new.backup'}
        self.archive(self.delta_path, entries, self.current_contents)
        with self.assertRaisesRegex(ValueError, 'entry set differs'):
            self.verify()

    def test_changed_retained_file_is_rejected_even_when_callers_provide_its_new_tar_hash(self):
        self.archive(self.base_path, self.base, self.contents | {'same.backup': b'evil'})
        with self.assertRaisesRegex(ValueError, 'file content'):
            self.verify()

    def test_archive_metadata_and_text_only_acl_are_rejected(self):
        mutations = [lambda m: setattr(m, 'mode', 0o777), lambda m: setattr(m, 'uid', 999),
                     lambda m: m.pax_headers.update(mtime='1700000000.000000001'),
                     lambda m: m.pax_headers.update({'SCHILY.acl.access': 'user::rwx'})]
        for mutate in mutations:
            with self.subTest(mutation=mutations.index(mutate)):
                self.archive(self.delta_path, self.delta, self.current_contents,
                             lambda name, member: mutate(member) if name == 'new.backup' else None)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_binary_xattrs_round_trip_and_mismatch_fail_closed(self):
        entries = copy.deepcopy(self.delta)
        entries['new.backup']['xattrs'] = {'user.binary': base64.b64encode(b'\x00\xff\x01').decode()}
        self.archive(self.delta_path, entries, self.current_contents)
        proof.verify_archive(self.delta_path, entries)
        with self.assertRaisesRegex(ValueError, 'xattrs/ACL'):
            proof.verify_archive(self.delta_path, self.delta)

    def test_trailing_nonzero_data_is_rejected(self):
        with self.delta_path.open('ab') as output:
            output.write(b'not a second accepted archive')
        with self.assertRaisesRegex(ValueError, 'trailing TAR'):
            self.verify()

    def test_archive_symlink_and_extra_entry_are_rejected(self):
        self.archive(self.delta_path, self.delta | {'extra.backup': self.base['old.backup']}, self.current_contents | {'extra.backup': b'old'})
        with self.assertRaises(ValueError):
            self.verify()
        link = self.root/'linked.tar'
        os.symlink(self.base_path, link)
        with self.assertRaisesRegex(ValueError, 'ordinary independent'):
            self.verify(base_archive=link)

    def test_hardlinked_local_archive_is_not_an_independent_backup(self):
        os.link(self.base_path, self.root/'another-name.tar')
        with self.assertRaisesRegex(ValueError, 'ordinary independent'):
            self.verify()

    def test_manifest_rejects_unknown_links_path_traversal_missing_parent_and_noncanonical_json(self):
        for mode in ['unknown-link', 'traversal', 'missing-parent', 'noncanonical']:
            with self.subTest(mode=mode):
                entries = copy.deepcopy(self.base)
                if mode == 'unknown-link': entries['same.backup']['nlink'] = 2
                elif mode == 'traversal': entries['../escape'] = entries.pop('same.backup') | {'path': '../escape'}
                elif mode == 'missing-parent': entries.pop(proof.KNOWN_HARDLINK.rpartition('/')[0])
                raw = self.raw(entries) if mode != 'noncanonical' else self.base_raw + b' '
                with self.assertRaises(ValueError): proof.parse_manifest(raw)

    def test_unchanged_current_history_transfers_directories_without_retransferring_files(self):
        plan = proof.plan_history(self.base_raw, self.base_raw)
        self.assertFalse(plan['requiresSourceChangeReview'])
        self.assertEqual(plan['deltaFileBytes'], 0)
        self.assertTrue(all(self.base[k]['kind'] == 'directory' for k in plan['deltaPaths']))

    def cli(self, *arguments):
        return subprocess.run([sys.executable, '-B', str(Path(__file__).with_name('deploy-history-archive.py')), *arguments],
                              capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)

    def test_cli_requires_explicit_mode_and_complete_arguments(self):
        for arguments in [(), ('verify',), ('plan', '--base-manifest', str(self.base_path))]:
            with self.subTest(arguments=arguments):
                self.assertNotEqual(self.cli(*arguments).returncode, 0)

    def test_cli_plan_is_exclusive_and_does_not_overwrite_existing_proof(self):
        base, current, output = self.root/'base.json', self.root/'current.json', self.root/'proof.json'
        base.write_bytes(self.base_raw)
        current.write_bytes(self.current_raw)
        arguments = ('plan', '--base-manifest', str(base), '--current-manifest', str(current), '--output', str(output))
        first = self.cli(*arguments)
        self.assertEqual(first.returncode, 0, first.stderr)
        before = output.read_bytes()
        self.assertNotEqual(self.cli(*arguments).returncode, 0)
        self.assertEqual(output.read_bytes(), before)

    def test_failed_cli_verification_does_not_write_an_adoption_receipt(self):
        base, current, after, output = (self.root/name for name in ['base.json', 'current.json', 'after.json', 'failed.json'])
        base.write_bytes(self.base_raw)
        current.write_bytes(self.current_raw)
        after.write_bytes(self.base_raw)
        result = self.cli('verify', '--base-manifest', str(base), '--current-manifest', str(current),
                          '--after-manifest', str(after), '--base-archive', str(self.base_path),
                          '--delta-archive', str(self.delta_path), '--base-sha256', 'a'*64,
                          '--delta-sha256', 'b'*64, '--output', str(output))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
