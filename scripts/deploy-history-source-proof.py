#!/usr/bin/env python3
"""Read-only immutable-history proof. OUTPUT is exclusive; --stdout writes only JSON."""
import base64
import hashlib
import json
import os
import stat
import sys

ROOT = '/opt/payplanner/shared/backups'
KNOWN_HARDLINK = 'manual-pre-09ae8b58-20260922-021158/current-release.tar.gz'
NOFOLLOW = os.O_NOFOLLOW | os.O_CLOEXEC


def allowed_links(relative, nlink):
    return nlink == (3 if relative == KNOWN_HARDLINK else 1)


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid,
            s.st_size, s.st_nlink, s.st_mtime_ns, s.st_ctime_ns)


def attrs(fd):
    return {n: base64.b64encode(os.getxattr(fd, n)).decode('ascii')
            for n in sorted(os.listxattr(fd))}


def root_fd():
    fd = os.open('/', os.O_RDONLY | os.O_DIRECTORY | NOFOLLOW)
    try:
        for part in ROOT.strip('/').split('/'):
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def inventory(hash_files):
    entries, identities = [], {}

    def visit(fd, relative):
        before = os.fstat(fd)
        directory = stat.S_ISDIR(before.st_mode)
        if not directory and (not stat.S_ISREG(before.st_mode) or not allowed_links(relative, before.st_nlink)):
            raise ValueError('Unsupported history entry')
        extended = attrs(fd)
        entry = dict(path=relative, kind='directory' if directory else 'file',
                     mode=stat.S_IMODE(before.st_mode), uid=before.st_uid,
                     gid=before.st_gid, mtimeNs=before.st_mtime_ns, xattrs=extended)
        if directory:
            for name in sorted(os.listdir(fd)):
                child_relative = name if relative == '.' else relative + '/' + name
                s = os.stat(name, dir_fd=fd, follow_symlinks=False)
                if not (stat.S_ISDIR(s.st_mode) or stat.S_ISREG(s.st_mode)):
                    raise ValueError('Unsupported history entry')
                if stat.S_ISREG(s.st_mode) and not allowed_links(child_relative, s.st_nlink):
                    raise ValueError('Hardlinked history file')
                flags = os.O_RDONLY | NOFOLLOW | os.O_NONBLOCK
                if stat.S_ISDIR(s.st_mode):
                    flags |= os.O_DIRECTORY
                child = os.open(name, flags, dir_fd=fd)
                try:
                    if identity(os.fstat(child)) != identity(s):
                        raise ValueError('History identity changed before read')
                    visit(child, child_relative)
                    if identity(os.stat(name, dir_fd=fd, follow_symlinks=False)) != identity(s):
                        raise ValueError('History path changed during read')
                finally:
                    os.close(child)
        else:
            entry.update(size=before.st_size, nlink=before.st_nlink)
            if hash_files:
                digest = hashlib.sha256()
                total = 0
                while True:
                    block = os.read(fd, 1024 * 1024)
                    if not block:
                        break
                    total += len(block)
                    digest.update(block)
                if total != before.st_size:
                    raise ValueError('History length changed during hash')
                entry['sha256'] = digest.hexdigest()
        if identity(os.fstat(fd)) != identity(before) or attrs(fd) != extended:
            raise ValueError('History metadata changed during read')
        identities[relative] = identity(before)
        entries.append(entry)

    fd = root_fd()
    try:
        root_identity = identity(os.fstat(fd))
        visit(fd, '.')
        if identity(os.lstat(ROOT)) != root_identity:
            raise ValueError('History root changed')
    finally:
        os.close(fd)
    return sorted(entries, key=lambda e: e['path']), identities


def manifest_bytes():
    first, first_ids = inventory(True)
    hardlinks = [(e['path'], e['nlink']) for e in first if e['kind'] == 'file' and e['nlink'] != 1]
    if hardlinks != [(KNOWN_HARDLINK, 3)]:
        raise ValueError('Exact known historical hardlink record required')
    second, second_ids = inventory(False)
    if [{k: v for k, v in e.items() if k != 'sha256'} for e in first] != second or first_ids != second_ids:
        raise ValueError('Complete history metadata inventory changed')
    return (json.dumps(dict(schemaVersion=1, root=ROOT, entries=first), ensure_ascii=True,
                       sort_keys=True, separators=(',', ':')) + '\n').encode('ascii')


def main():
    if len(sys.argv) != 2:
        raise ValueError('Expected OUTPUT.json or --stdout')
    output = sys.argv[1]
    data = manifest_bytes()
    if output == '--stdout':
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()
        return
    parent = os.path.realpath(os.path.dirname(os.path.abspath(output)))
    target = os.path.join(parent, os.path.basename(output))
    if os.path.commonpath([ROOT, target]) == ROOT:
        raise ValueError('Output must be outside immutable history')
    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | NOFOLLOW, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('history-proof failed: ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
