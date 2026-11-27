import gzip
import hashlib
import json
import sys
from pathlib import Path


HERE = Path(__file__).resolve().parent
REFERENCE = HERE / "baseline-catalogue.json"


def digest(cases, first, second):
    entries = sorted(f"{row[first]}\0{row[second]}" for row in cases)
    return hashlib.sha256("\n".join(entries).encode("utf-8")).hexdigest()


def describe(plan):
    cases = plan["Cases"]
    if len({row["Id"] for row in cases}) != len(cases):
        raise ValueError("Discovery returned duplicate case IDs")
    return {
        "revision": plan["Revision"],
        "count": len(cases),
        "identityDigest": digest(cases, "Id", "FullyQualifiedName"),
        "displayDigest": digest(cases, "FullyQualifiedName", "DisplayName"),
        "shards": [sum(row["Shard"] == shard for row in cases) for shard in range(4)],
    }


def read_plan(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def record(plan_path, hosted_path):
    plan = read_plan(plan_path)
    settings = describe(plan)
    hosted = describe(read_plan(hosted_path))
    identity_keys = ("revision", "count", "identityDigest")
    if any(settings[key] != hosted[key] for key in identity_keys):
        raise ValueError("The reference and hosted discovery contain different cases")
    settings["displayDigests"] = {
        "xsenus": settings.pop("displayDigest"),
        "githubWindows": hosted["displayDigest"],
    }
    REFERENCE.write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")
    profile = {"SchemaVersion": 1, "Revision": plan["Revision"],
               "ManifestSha256": hashlib.sha256(plan_path.read_bytes()).hexdigest(), "Methods": plan["Timings"]}
    (HERE / "reference-timings.json.gz").write_bytes(
        gzip.compress(json.dumps(profile, separators=(",", ":")).encode("utf-8"), mtime=0))


def verify(plan_path):
    expected = json.loads(REFERENCE.read_text(encoding="utf-8"))
    actual = describe(read_plan(plan_path))
    display_digests = expected.pop("displayDigests")
    if (any(actual[key] != value for key, value in expected.items())
            or actual["displayDigest"] not in display_digests.values()):
        raise ValueError(f"The hosted discovery changed the reference catalogue: {actual}")
    print(json.dumps(actual))


if __name__ == "__main__":
    {"record": record, "verify": verify}[sys.argv[1]](*(Path(arg) for arg in sys.argv[2:]))
