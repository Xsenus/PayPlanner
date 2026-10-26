"""Read-only complete stopped-backup inventory. One exact authorised backup path argv."""
import base64
import hashlib
import json
import os
import re
import stat
import sys


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid,
            s.st_size, s.st_nlink, s.st_mtime_ns, s.st_ctime_ns)


def inventory(root):
    rows = []
    def visit(fd, path):
        before = os.fstat(fd)
        directory = stat.S_ISDIR(before.st_mode)
        if not directory and (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1):
            raise ValueError('Unsupported backup entry')
        attrs = {n: base64.b64encode(os.getxattr(fd, n)).decode('ascii') for n in sorted(os.listxattr(fd))}
        row = dict(path=path, kind='directory' if directory else 'file',
                   identity=list(identity(before)), xattrs=attrs)
        if directory:
            for name in sorted(os.listdir(fd)):
                s = os.stat(name, dir_fd=fd, follow_symlinks=False)
                if not (stat.S_ISDIR(s.st_mode) or stat.S_ISREG(s.st_mode)):
                    raise ValueError('Backup link or special entry')
                child = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK |
                                (os.O_DIRECTORY if stat.S_ISDIR(s.st_mode) else 0), dir_fd=fd)
                try:
                    if identity(os.fstat(child)) != identity(s):
                        raise ValueError('Backup entry changed before open')
                    visit(child, name if path == '.' else path + '/' + name)
                    if identity(os.stat(name, dir_fd=fd, follow_symlinks=False)) != identity(s):
                        raise ValueError('Backup path identity changed')
                finally:
                    os.close(child)
        else:
            digest, total = hashlib.sha256(), 0
            while True:
                block = os.read(fd, 1024 * 1024)
                if not block:
                    break
                digest.update(block)
                total += len(block)
            if total != before.st_size:
                raise ValueError('Backup file length changed')
            row.update(size=total, sha256=digest.hexdigest())
        after_attrs = {n: base64.b64encode(os.getxattr(fd, n)).decode('ascii') for n in sorted(os.listxattr(fd))}
        if identity(os.fstat(fd)) != identity(before) or attrs != after_attrs:
            raise ValueError('Backup metadata changed during read')
        rows.append(row)
    fd = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for part in root.strip('/').split('/'):
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd)
            fd = child
        original = identity(os.fstat(fd))
        visit(fd, '.')
        if identity(os.lstat(root)) != original:
            raise ValueError('Backup root changed')
    finally:
        os.close(fd)
    return sorted(rows, key=lambda r: r['path'])


def main():
    if len(sys.argv) != 2 or not re.fullmatch(r'/opt/payplanner/deploy-backups/manual-[a-f0-9]{40}-[0-9]{8}T[0-9]{6}Z', sys.argv[1]):
        raise ValueError('Exact stopped-backup root required')
    root = sys.argv[1]
    data = dict(schemaVersion=1, root=root, entries=inventory(root))
    # The second full proof after transfer detects additions/removals/metadata/content drift.
    sys.stdout.buffer.write((json.dumps(data, ensure_ascii=True, sort_keys=True,
                                      separators=(',', ':')) + '\n').encode('ascii'))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('stopped-proof failed: ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
