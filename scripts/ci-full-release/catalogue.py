import gzip
import hashlib
import json
import sys
from pathlib import Path


HERE = Path(__file__).resolve().parent
REFERENCE = HERE / "baseline-catalogue.json"


def catalogue(cases):
    names = [f"{row['FullyQualifiedName']}\0{row['DisplayName']}" for row in cases]
    return hashlib.sha256("\n".join(sorted(names)).encode("utf-8")).hexdigest()


def record(plan_path):
    plan = json.loads(plan_path.read_text(encoding="utf-8-sig"))
    cases = plan["Cases"]
    settings = {"revision": plan["Revision"], "count": len(cases), "digest": catalogue(cases),
                "shards": [sum(row["Shard"] == shard for row in cases) for shard in range(4)]}
    REFERENCE.write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")
    profile = {"SchemaVersion": 1, "Revision": plan["Revision"],
               "ManifestSha256": hashlib.sha256(plan_path.read_bytes()).hexdigest(), "Methods": plan["Timings"]}
    (HERE / "reference-timings.json.gz").write_bytes(
        gzip.compress(json.dumps(profile, separators=(",", ":")).encode("utf-8"), mtime=0))


def verify(plan_path):
    plan = json.loads(plan_path.read_text(encoding="utf-8-sig"))
    expected = json.loads(REFERENCE.read_text(encoding="utf-8"))
    cases = plan["Cases"]
    actual = {"revision": plan["Revision"], "count": len(cases), "digest": catalogue(cases),
              "shards": [sum(row["Shard"] == shard for row in cases) for shard in range(4)]}
    if actual != expected or len({row["Id"] for row in cases}) != len(cases):
        raise ValueError(f"The hosted discovery changed the reference catalogue: {actual}")
    print(json.dumps(actual))


if __name__ == "__main__":
    {"record": record, "verify": verify}[sys.argv[1]](Path(sys.argv[2]))
