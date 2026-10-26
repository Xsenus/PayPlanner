"""Verify an off-VPS stopped backup. Never restore a database or extract a tree."""
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import tarfile

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('archive_proof', Path(__file__).with_name('deploy-history-archive.py'))
archive_proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(archive_proof)
need = archive_proof.require


def parse_manifest(raw):
    manifest = json.loads(raw)
    need(manifest['schemaVersion'] == 1 and type(manifest['schemaVersion']) is int, 'Stopped manifest schema')
    need(re.fullmatch(r'/opt/payplanner/deploy-backups/manual-[a-f0-9]{40}-[0-9]{8}T[0-9]{6}Z', manifest['root']), 'Stopped root')
    rows = manifest['entries']
    need(isinstance(rows, list) and rows and [r['path'] for r in rows] == sorted(r['path'] for r in rows), 'Stopped manifest ordering')
    expected = {}
    for row in rows:
        path, kind, ident = row['path'], row['kind'], row['identity']
        need(isinstance(path, str) and '\\' not in path and not any(ord(c) < 32 for c in path), 'Unsafe stopped path')
        need(path == '.' or (not path.startswith('/') and all(p not in ('', '.', '..') for p in path.split('/'))), 'Unsafe stopped path')
        need(path not in expected and kind in ('file', 'directory'), 'Duplicate/unsupported stopped entry')
        need(isinstance(ident, list) and len(ident) == 9 and all(type(v) is int and v >= 0 for v in ident), 'Stopped identity')
        dev, ino, mode, uid, gid, size, nlink, mtime, ctime = ident
        need((stat.S_ISREG(mode) and kind == 'file' and nlink == 1) or (stat.S_ISDIR(mode) and kind == 'directory'), 'Stopped entry mode/link')
        attrs = row['xattrs']
        need(isinstance(attrs, dict), 'Stopped xattrs')
        for name, value in attrs.items():
            need(isinstance(name, str) and isinstance(value, str), 'Stopped xattr shape')
            base64.b64decode(value, validate=True)
        entry = dict(path=path, kind=kind, mode=stat.S_IMODE(mode), uid=uid, gid=gid, mtimeNs=mtime, xattrs=attrs)
        if kind == 'file':
            need(type(row['size']) is int and size == row['size'] and re.fullmatch('[a-f0-9]{64}', row['sha256']), 'Stopped length/hash')
            entry.update(size=size, nlink=1, sha256=row['sha256'])
        expected[path] = entry
    need(expected.get('.', {}).get('kind') == 'directory', 'Missing stopped root')
    for path in expected:
        parent = path.rpartition('/')[0] or '.'
        need(path == '.' or expected.get(parent, {}).get('kind') == 'directory', 'Stopped parent missing')
    return manifest['root'], expected


def verify_backup(before_raw, after_raw, archive, dump_target, pg_restore, *, run=subprocess.run):
    need(before_raw == after_raw, 'Stopped backup changed during transfer')
    root, expected = parse_manifest(before_raw)
    archive_root = PurePosixPath(root).name
    result = archive_proof.verify_archive(archive, expected, archive_root=archive_root)
    need(expected.get('database.dump', {}).get('size', 0) > 0, 'Stopped database dump missing')
    need(expected.get('BACKUP-SHA256SUMS', {}).get('size', 0) > 0, 'Stopped checksum manifest missing')
    # Read only two already verified ordinary members, never extractall(). The
    # fixed dump destination must be a fresh file in the protected offsite folder.
    with tarfile.open(archive, 'r:') as stream:
        checksums = stream.extractfile(archive_root + '/BACKUP-SHA256SUMS').read()
        parsed = {}
        for line in checksums.decode('utf-8').splitlines():
            digest, separator, name = line.partition('  ./')
            need(separator and re.fullmatch('[a-f0-9]{64}', digest) and name not in parsed, 'Invalid stopped checksum record')
            parsed[name] = digest
        need(parsed == {p: r['sha256'] for p, r in expected.items() if r['kind'] == 'file' and p != 'BACKUP-SHA256SUMS'}, 'Incomplete stopped checksum coverage')
        with stream.extractfile(archive_root + '/database.dump') as source, Path(dump_target).open('xb') as target:
            digest, count = hashlib.sha256(), 0
            while block := source.read(1024 * 1024):
                target.write(block)
                digest.update(block)
                count += len(block)
            target.flush()
            os.fsync(target.fileno())
        need(count == expected['database.dump']['size'] and digest.hexdigest() == expected['database.dump']['sha256'], 'Offsite database dump copy differs')
    # Listing is intentionally read-only. No database connection/restore exists.
    native = run([str(pg_restore), '--list', str(dump_target)], capture_output=True, timeout=120, check=False)
    need(native.returncode == 0 and bool(native.stdout.strip()), 'Offsite native pg_restore --list failed')
    list_path = Path(dump_target).with_suffix('.list')
    with list_path.open('xb') as output:
        output.write(native.stdout)
    # Byte verification alone cannot attest where the program ran. The trusted
    # Windows caller adopts this proof only after checking its protected NTFS path.
    return dict(schemaVersion=1, rootAccepted=False, verifiedOutsideVps=False, backupServerPath=root,
                backupManifestSha256=hashlib.sha256(checksums).hexdigest(), allListedFilesCopied=True,
                allCopiedHashesMatched=True, allEntryMetadataMatched=True, sourceBeforeAfterExact=True,
                offsitePgRestoreListExitCode=0, offsitePgRestoreListSha256=hashlib.sha256(native.stdout).hexdigest(),
                retainedForRecovery=True, localEvidenceIsCI=False, archive=result,
                dumpSha256=digest.hexdigest(), dumpBytes=count)


def main():
    need(len(sys.argv) == 7, 'Expected before/after/backup.tar/dump/pg_restore/output')
    before, after, archive, dump, pg_restore, output = sys.argv[1:]
    result = verify_backup(Path(before).read_bytes(), Path(after).read_bytes(), archive, dump, pg_restore)
    with Path(output).open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    print(json.dumps(dict(status='verified-stopped-offsite', entries=result['archive']['entryCount'])))


if __name__ == '__main__':
    main()
