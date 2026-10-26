"""Verify complete history coverage from immutable base and native delta TARs.

Pure local archive operations. Never connects to a server, extracts files,
deletes backups, or treats this proof as application/CI qualification.
PAX/hash checks preserve the accepted history-offsite validator's semantics.
"""
import base64
import argparse
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import re
import stat
import tarfile

ROOT = '/opt/payplanner/shared/backups'
KNOWN_HARDLINK = 'manual-pre-09ae8b58-20260922-021158/current-release.tar.gz'
CHUNK = 1024 * 1024


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def canonical(value):
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':')) + '\n').encode('ascii')


def parse_manifest(raw):
    manifest = json.loads(raw)
    require(canonical(manifest) == raw and set(manifest) == {'schemaVersion', 'root', 'entries'}, 'Noncanonical history manifest')
    require(type(manifest['schemaVersion']) is int and manifest['schemaVersion'] == 1 and manifest['root'] == ROOT, 'Wrong history root/schema')
    entries = manifest['entries']
    require(isinstance(entries, list) and entries and [e['path'] for e in entries] == sorted(e['path'] for e in entries), 'History manifest order')
    found = {}
    for entry in entries:
        name, kind = entry['path'], entry['kind']
        require(isinstance(name, str) and (name == '.' or (not name.startswith('/') and all(p not in ('', '.', '..') for p in name.split('/')))), 'Unsafe history path')
        require('\\' not in name and not any(ord(c) < 32 for c in name) and name not in found, 'Invalid/duplicate history path')
        require(kind in ('file', 'directory'), 'Unsupported history kind')
        fields = {'path', 'kind', 'mode', 'uid', 'gid', 'mtimeNs', 'xattrs'} | ({'size', 'nlink', 'sha256'} if kind == 'file' else set())
        require(set(entry) == fields and all(type(entry[k]) is int for k in ('mode', 'uid', 'gid', 'mtimeNs')), 'History metadata shape')
        require(0 <= entry['mode'] <= 0o7777 and entry['uid'] >= 0 and entry['gid'] >= 0 and isinstance(entry['xattrs'], dict), 'History metadata values')
        for key, value in entry['xattrs'].items():
            require(isinstance(key, str) and isinstance(value, str), 'History xattr shape')
            base64.b64decode(value, validate=True)
        if kind == 'file':
            require(type(entry['size']) is int and entry['size'] >= 0, 'History size')
            require(type(entry['nlink']) is int and entry['nlink'] == (3 if name == KNOWN_HARDLINK else 1), 'Unknown history hardlink')
            require(isinstance(entry['sha256'], str) and re.fullmatch('[a-f0-9]{64}', entry['sha256']), 'History file hash')
        found[name] = entry
    require(found.get('.', {}).get('kind') == 'directory', 'Missing history root')
    require(found.get(KNOWN_HARDLINK, {}).get('nlink') == 3, 'Missing known historical hardlink record')
    for name in found:
        parent = name.rpartition('/')[0] or '.'
        require(name == '.' or found.get(parent, {}).get('kind') == 'directory', 'Missing history parent directory')
    return found


def plan_history(base_raw, current_raw):
    base, current = parse_manifest(base_raw), parse_manifest(current_raw)
    # Every current directory is transferred again, including changed timestamps
    # and ACLs. Only byte- AND metadata-identical ordinary files may be reused.
    reused = {name for name, entry in current.items() if entry['kind'] == 'file' and base.get(name) == entry}
    delta = [name for name in current if name not in reused]
    removed = sorted(set(base) - set(current))
    changed = sorted(name for name in set(base) & set(current) if base[name] != current[name])
    return {
        'schemaVersion': 1,
        'baseManifestSha256': hashlib.sha256(base_raw).hexdigest(),
        'currentManifestSha256': hashlib.sha256(current_raw).hexdigest(),
        'reusedPaths': sorted(reused), 'deltaPaths': sorted(delta),
        'removedPathsRetainedInBaseArchive': removed, 'changedPaths': changed,
        'requiresSourceChangeReview': bool(removed or changed),
        'deltaFileBytes': sum(current[name].get('size', 0) for name in delta),
        'currentEntryCount': len(current),
        'currentFileCount': sum(e['kind'] == 'file' for e in current.values()),
        'productionMutation': False, 'automaticDeletion': False,
    }


def ordinary(path):
    path = Path(path)
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink()
            and not (getattr(info, 'st_file_attributes', 0) & 0x400)
            and info.st_nlink == 1 and path.resolve() == path.absolute(), 'Archive must be an ordinary independent file')
    return path, info


def signature(info):
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


class HashingReader:
    def __init__(self, stream):
        self.stream = stream
        self.digest = hashlib.sha256()
        self.count = 0

    def read(self, length=CHUNK):
        block = self.stream.read(length)
        self.digest.update(block)
        self.count += len(block)
        return block


def verify_archive(archive, expected, *, archive_root='backups'):
    require(isinstance(archive_root, str) and archive_root and '/' not in archive_root
            and '\\' not in archive_root and archive_root not in ('.', '..')
            and not any(ord(c) < 32 for c in archive_root), 'Unsafe archive root')
    path, before = ordinary(archive)
    seen, end = set(), 0
    with path.open('rb') as source:
        reader = HashingReader(source)
        with tarfile.open(fileobj=reader, mode='r|', encoding='utf-8', errors='surrogateescape') as stream:
            for member in stream:
                name = member.name.rstrip('/')
                require(name == archive_root or name.startswith(archive_root + '/'), 'TAR path escapes history')
                key = '.' if name == archive_root else name[len(archive_root) + 1:]
                require(key in expected and key not in seen and not member.linkname and member.sparse is None, 'Unexpected/duplicate/linked TAR member')
                entry = expected[key]
                require((member.isdir() and entry['kind'] == 'directory') or (member.isreg() and entry['kind'] == 'file'), 'TAR kind')
                require((member.mode, member.uid, member.gid) == (entry['mode'], entry['uid'], entry['gid']), 'TAR ownership/mode')
                nanos = Decimal(str(member.pax_headers.get('mtime', member.mtime))) * 1000000000
                require(nanos.is_finite() and nanos == nanos.to_integral_value() and int(nanos) == entry['mtimeNs'], 'TAR mtime')
                attrs = {k[len('SCHILY.xattr.'):]: v.encode('utf-8', 'surrogateescape') for k, v in member.pax_headers.items() if k.startswith('SCHILY.xattr.')}
                require(attrs == {n: base64.b64decode(v, validate=True) for n, v in entry['xattrs'].items()}, 'TAR raw xattrs/ACL')
                for suffix, attribute in (('access', 'system.posix_acl_access'), ('default', 'system.posix_acl_default')):
                    require('SCHILY.acl.' + suffix not in member.pax_headers or attribute in attrs, 'Unverified text ACL')
                require(not any(k.startswith('GNU.sparse') for k in member.pax_headers), 'Sparse TAR extension')
                if member.isreg():
                    require(member.size == entry['size'], 'TAR file size')
                    digest, count = hashlib.sha256(), 0
                    with stream.extractfile(member) as data:
                        while block := data.read(CHUNK):
                            digest.update(block)
                            count += len(block)
                    require(count == entry['size'] and digest.hexdigest() == entry['sha256'], 'TAR file content')
                else:
                    require(member.size == 0, 'TAR directory payload')
                end = member.offset_data + ((member.size + 511) // 512) * 512
                seen.add(key)
        require(seen == set(expected), 'TAR entry set differs')
        while block := reader.read(CHUNK):
            require(not block.strip(b'\0'), 'Nonzero trailing TAR data')
        require(reader.count == before.st_size and reader.count - end >= 1024, 'TAR length/end blocks')
        source.seek(end)
        while block := source.read(CHUNK):
            require(not block.strip(b'\0'), 'Unverified trailing TAR payload')
    _, after = ordinary(path)
    require(signature(before) == signature(after), 'Archive changed during disk readback')
    return {'sha256': reader.digest.hexdigest(), 'bytes': reader.count, 'entryCount': len(seen)}


def verify_complete_history(base_raw, current_raw, after_raw, base_archive, delta_archive,
                            expected_base_sha, expected_delta_sha, reviewed_changes=None):
    require(current_raw == after_raw, 'Source history changed during transfer')
    plan = plan_history(base_raw, current_raw)
    # A vanished or modified source path is a review boundary. Passing a review
    # of some other manifest cannot approve the current change implicitly.
    if plan['requiresSourceChangeReview']:
        require(reviewed_changes == plan['currentManifestSha256'], 'Source history changes require an exact manifest review')
    base, current = parse_manifest(base_raw), parse_manifest(current_raw)
    first = verify_archive(base_archive, base)
    require(first['sha256'] == expected_base_sha, 'Retained base archive hash differs')
    delta = {name: current[name] for name in plan['deltaPaths']}
    second = verify_archive(delta_archive, delta)
    require(second['sha256'] == expected_delta_sha, 'Native delta archive hash differs')
    coverage = {name: 0 for name in plan['reusedPaths']}
    for name in plan['deltaPaths']:
        require(name not in coverage, 'Overlapping history coverage')
        coverage[name] = 1
    require(set(coverage) == set(current), 'Incomplete current history coverage')
    return {
        'schemaVersion': 2, 'status': 'verified-complete-base-plus-delta-history',
        'currentManifestSha256': plan['currentManifestSha256'],
        'baseManifestSha256': plan['baseManifestSha256'],
        'archives': [first, second], 'coverage': coverage,
        'coverageSha256': hashlib.sha256(canonical(coverage)).hexdigest(),
        'sourceEntryCount': plan['currentEntryCount'], 'sourceFileCount': plan['currentFileCount'],
        'allCurrentEntriesCovered': True, 'allFileHashesMatched': True,
        'allEntryMetadataMatched': True, 'sourceBeforeAfterExact': True,
        'sourceAndArchiveNlinkEquivalent': False,
        'sourceHardlinkRecords': [{'path': KNOWN_HARDLINK, 'nlink': 3}],
        'flattenedArchiveRepresentation': True,
        'removedPathsRetainedInBaseArchive': plan['removedPathsRetainedInBaseArchive'],
        'rootAccepted': False, 'verifiedOutsideVps': False,
        'productionMutation': False, 'automaticDeletion': False, 'qualifiesCI': False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='mode', required=True)
    for mode in ['plan', 'verify']:
        command = commands.add_parser(mode)
        command.add_argument('--base-manifest', required=True, type=Path)
        command.add_argument('--current-manifest', required=True, type=Path)
        command.add_argument('--output', required=True, type=Path)
        if mode == 'verify':
            command.add_argument('--after-manifest', required=True, type=Path)
            command.add_argument('--base-archive', required=True, type=Path)
            command.add_argument('--delta-archive', required=True, type=Path)
            command.add_argument('--base-sha256', required=True)
            command.add_argument('--delta-sha256', required=True)
            command.add_argument('--reviewed-changes')
    options = parser.parse_args()
    base_raw = ordinary(options.base_manifest)[0].read_bytes()
    current_raw = ordinary(options.current_manifest)[0].read_bytes()
    if options.mode == 'plan':
        result = plan_history(base_raw, current_raw)
    else:
        result = verify_complete_history(base_raw, current_raw, ordinary(options.after_manifest)[0].read_bytes(),
                                         options.base_archive, options.delta_archive,
                                         options.base_sha256, options.delta_sha256, options.reviewed_changes)
    # Never overwrite an earlier proof, backup or control file, including after
    # a failed attempt. The calling bridge owns the private output directory.
    with options.output.open('xb') as output:
        output.write(canonical(result))
    print(json.dumps({'status': result.get('status', 'history-transfer-plan-only'),
                      'outputSha256': hashlib.sha256(canonical(result)).hexdigest(),
                      'qualifiesCI': False, 'rootAccepted': False}))


if __name__ == '__main__':
    main()
