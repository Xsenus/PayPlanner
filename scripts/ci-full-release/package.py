import hashlib
import json
import shutil
import sys
from pathlib import Path, PurePosixPath


PUBLISHED = Path("out/be")
RUNTIME = Path("backend/PayPlanner.Api.Tests/bin/Release/net9.0")
EXTRA = Path("portable-extra")
MANIFEST = Path("portable-map.json")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def files(directory):
    return sorted(path for path in directory.rglob("*") if path.is_file())


def pack():
    # The same API and dependencies often exist in both publish and test outputs.
    # Store identical bytes once while retaining the exact published file layout.
    EXTRA.mkdir()
    known = {digest(path): path for path in files(RUNTIME)}
    rows = []
    for path in files(PUBLISHED):
        name = path.relative_to(PUBLISHED)
        sha = digest(path)
        source = known.get(sha)
        if source is None:
            source = EXTRA / name
            source.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, source)
            known[sha] = source
        rows.append({"name": name.as_posix(), "source": source.as_posix(), "sha256": sha})
    if not rows:
        raise ValueError("Empty backend publish")
    MANIFEST.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")


def safe_path(value):
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or ":" in value or "\\" in value or not path.parts:
        raise ValueError("Invalid package path")
    return Path(*path.parts)


def unpack():
    PUBLISHED.mkdir(parents=True)
    rows = json.loads(MANIFEST.read_text(encoding="utf-8"))
    seen = set()
    for row in rows:
        source = safe_path(row["source"])
        if not any(source.is_relative_to(parent) for parent in (EXTRA, RUNTIME)) or source.is_symlink():
            raise ValueError("Unexpected package source")
        name = safe_path(row["name"])
        if name.as_posix().lower() in seen or digest(source) != row["sha256"]:
            raise ValueError("Duplicate package path or hash mismatch")
        seen.add(name.as_posix().lower())
        target = PUBLISHED / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    if not seen:
        raise ValueError("Empty portable package")


if __name__ == "__main__":
    {"pack": pack, "unpack": unpack}[sys.argv[1]]()
