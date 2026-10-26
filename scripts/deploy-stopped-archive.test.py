import copy
from decimal import Decimal
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('stopped', Path(__file__).with_name('deploy-stopped-archive.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class StoppedArchiveTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='pp-stopped-backup-')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.archive = self.root/'stopped.tar'
        self.server = '/opt/payplanner/deploy-backups/manual-'+'a'*40+'-20261026T120000Z'
        self.prefix = self.server.rpartition('/')[2]
        self.contents = {'database.dump': b'synthetic-pgdump-not-production', 'backend.previous/api.dll': b'old api',
                         'preserved-instance.tar': b'private settings and keys fixture'}
        sums = ''.join(hashlib.sha256(v).hexdigest()+'  ./'+k+'\n' for k, v in sorted(self.contents.items()))
        self.contents['BACKUP-SHA256SUMS'] = sums.encode()
        self.entries = {}
        for path, content in self.contents.items():
            self.entry(path, 'file', content)
            parent = path.rpartition('/')[0]
            if parent:
                self.entry(parent, 'directory')
        self.entry('.', 'directory')
        self.calls = []

    def entry(self, path, kind, content=b''):
        mode = (stat.S_IFDIR | 0o700) if kind == 'directory' else (stat.S_IFREG | 0o600)
        row = dict(path=path, kind=kind, identity=[1, len(self.entries)+100, mode, 0, 0, len(content),
                   2 if kind == 'directory' else 1, 1700000000123456789, 1700000000123456789], xattrs={})
        if kind == 'file':
            row.update(size=len(content), sha256=hashlib.sha256(content).hexdigest())
        self.entries[path] = row

    def raw(self):
        return json.dumps(dict(schemaVersion=1, root=self.server, entries=[self.entries[p] for p in sorted(self.entries)])).encode()

    def tar(self, mutate=None):
        with tarfile.open(self.archive, 'w', format=tarfile.PAX_FORMAT) as archive:
            for path, row in sorted(self.entries.items()):
                member = tarfile.TarInfo(self.prefix if path == '.' else self.prefix+'/'+path)
                member.mode = stat.S_IMODE(row['identity'][2])
                member.uid, member.gid = row['identity'][3:5]
                member.pax_headers['mtime'] = str(Decimal(row['identity'][7])/1000000000)
                member.type = tarfile.DIRTYPE if row['kind'] == 'directory' else tarfile.REGTYPE
                content = self.contents.get(path)
                member.size = len(content) if content is not None else 0
                if mutate:
                    mutate(member, path)
                archive.addfile(member, io.BytesIO(content) if member.isreg() else None)

    def native_list(self, argv, **options):
        self.calls.append((argv, options))
        return subprocess.CompletedProcess(argv, 0, b'; synthetic archive listing\n', b'')

    def verify(self, after=None, run=None):
        return proof.verify_backup(self.raw(), self.raw() if after is None else after,
            self.archive, self.root/'database.dump', 'synthetic-pg_restore', run=self.native_list if run is None else run)

    def test_complete_scope_and_read_only_native_command(self):
        self.tar()
        result = self.verify()
        self.assertEqual(result['archive']['entryCount'], len(self.entries))
        self.assertTrue(result['allEntryMetadataMatched'])
        self.assertFalse(result['rootAccepted'])
        self.assertFalse(result['verifiedOutsideVps'])
        self.assertEqual(result['backupServerPath'], self.server)
        self.assertEqual((self.root/'database.dump').read_bytes(), self.contents['database.dump'])
        self.assertEqual(self.calls[0][0], ['synthetic-pg_restore', '--list', str(self.root/'database.dump')])
        self.assertFalse(any(self.root.joinpath('backend.previous').glob('*')))

    def test_source_change_blocks_disk_readback(self):
        self.tar()
        with self.assertRaisesRegex(ValueError, 'changed during transfer'):
            self.verify(after=self.raw()+b' ')
        self.assertEqual(self.calls, [])

    def test_metadata_or_archive_path_change_rejected(self):
        for field, value in [('uid', 99), ('mode', 0o777), ('name', '../escape')]:
            with self.subTest(field=field):
                self.tar(lambda member, path: setattr(member, field, value) if path == 'database.dump' else None)
                with self.assertRaises(ValueError):
                    self.verify()
                self.assertEqual(self.calls, [])

    def test_link_rejected(self):
        def mutate(member, path):
            if path == 'database.dump':
                member.type, member.linkname, member.size = tarfile.SYMTYPE, '/etc/passwd', 0
        self.tar(mutate)
        with self.assertRaisesRegex(ValueError, 'linked TAR member'):
            self.verify()

    def test_corrupt_content_rejected(self):
        self.tar()
        raw = self.archive.read_bytes().replace(b'synthetic-pgdump-not-production', b'x' * len(b'synthetic-pgdump-not-production'))
        self.archive.write_bytes(raw)
        with self.assertRaisesRegex(ValueError, 'file content'):
            self.verify()

    def test_nonzero_trailing_data_rejected(self):
        self.tar()
        with self.archive.open('ab') as stream:
            stream.write(b'unverified payload')
        with self.assertRaisesRegex(ValueError, 'trailing TAR'):
            self.verify()

    def test_checksum_coverage_cannot_omit_keys_or_other_files(self):
        self.contents['BACKUP-SHA256SUMS'] = (hashlib.sha256(self.contents['database.dump']).hexdigest()+'  ./database.dump\n').encode()
        self.entry('BACKUP-SHA256SUMS', 'file', self.contents['BACKUP-SHA256SUMS'])
        self.tar()
        with self.assertRaisesRegex(ValueError, 'checksum coverage'):
            self.verify()

    def test_native_failure_or_empty_listing_blocks_receipt(self):
        self.tar()
        for code, listing in [(1, b'failure'), (0, b'')]:
            with self.subTest(code=code, listing=listing):
                (self.root/'database.dump').unlink(missing_ok=True)
                with self.assertRaisesRegex(ValueError, 'pg_restore --list failed'):
                    self.verify(run=lambda argv, **kwargs: subprocess.CompletedProcess(argv, code, listing, b'error'))

    def test_no_overwrite_of_existing_recovery_dump(self):
        self.tar()
        target = self.root/'database.dump'
        target.write_bytes(b'retained')
        with self.assertRaises(FileExistsError):
            self.verify()
        self.assertEqual(target.read_bytes(), b'retained')

    def test_wrong_source_root_or_hardlink_identity_rejected(self):
        for mutate in [lambda: setattr(self, 'server', '/another/company'),
                       lambda: self.entries['database.dump']['identity'].__setitem__(6, 2)]:
            old_server, old_entries = self.server, copy.deepcopy(self.entries)
            mutate()
            with self.assertRaises(ValueError):
                proof.parse_manifest(self.raw())
            self.server, self.entries = old_server, old_entries


if __name__ == '__main__':
    unittest.main()
