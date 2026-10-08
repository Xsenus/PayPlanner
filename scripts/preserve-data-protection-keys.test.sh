#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT
helper="$script_dir/preserve-data-protection-keys.sh"
target="$fixture_root/shared/data-protection-keys"
legacy="$fixture_root/shared/dataprotection"
fallback="$fixture_root/home/.aspnet/DataProtection-Keys"
mkdir -p "$target" "$legacy" "$fallback"
# Synthetic, non-cryptographic fixtures. Never read a developer's real key ring.
printf '<key id="new"/>\n' > "$target/key-new.xml"
printf '<key id="old"/>\n' > "$legacy/key-old.xml"
cp "$legacy/key-old.xml" "$fallback/key-old.xml"
printf '<revocation/>\n' > "$fallback/revocation-old.xml"
bash "$helper" "$target" "$fixture_root/backup/keys" existing "$legacy" "$fallback" "$fixture_root/missing"
for name in key-new.xml key-old.xml revocation-old.xml; do
  cmp "$target/$name" "$fixture_root/backup/keys/$name"
done
cmp "$legacy/key-old.xml" "$target/key-old.xml"
bash "$helper" "$target" "$fixture_root/backup2/keys" existing "$legacy" "$fallback"
cmp "$fixture_root/backup/keys/key-old.xml" "$fixture_root/backup2/keys/key-old.xml"

printf '<key id="conflict"/>\n' > "$fallback/key-old.xml"
if bash "$helper" "$target" "$fixture_root/conflict/keys" existing "$fallback" >"$fixture_root/failure.log" 2>&1; then
  echo "Conflicting key was accepted." >&2; exit 1
fi
test ! -e "$fixture_root/conflict/keys"
cmp "$legacy/key-old.xml" "$target/key-old.xml"
if grep -q '<key' "$fixture_root/failure.log"; then
  echo "Secret key contents appeared in diagnostics." >&2; exit 1
fi
if bash "$helper" "$fixture_root/empty" "$fixture_root/empty-backup" existing >/dev/null 2>&1; then
  echo "An existing installation accepted an empty key ring." >&2; exit 1
fi
bash "$helper" "$fixture_root/new" "$fixture_root/new-backup" new
if bash "$helper" "$target" "$target/unsafe-snapshot" existing >/dev/null 2>&1; then
  echo "A snapshot inside the active ring was accepted." >&2; exit 1
fi
if bash "$helper" "$target" "$fixture_root/backup/keys" existing >/dev/null 2>&1; then
  echo "An existing snapshot was replaced." >&2; exit 1
fi
if bash "$helper" /tmp/.. "$fixture_root/root-backup" new >/dev/null 2>&1; then
  echo "A path resolving to the filesystem root was accepted." >&2; exit 1
fi
if [[ "$(uname -s)" == Linux ]]; then
  test "$(stat -c %a "$target")" = 700
  test "$(stat -c %a "$target/key-old.xml")" = 600
  test "$(stat -c %a "$fixture_root/backup/keys")" = 700
  test "$(stat -c %a "$fixture_root/backup/keys/key-old.xml")" = 600
  ln -s "$legacy" "$fixture_root/linked-ring"
  if bash "$helper" "$target" "$fixture_root/link-backup" existing "$fixture_root/linked-ring" >/dev/null 2>&1; then
    echo "A symbolic-link key ring was accepted." >&2; exit 1
  fi
  mkdir -p "$fixture_root/linked-file-ring"
  ln -s "$legacy/key-old.xml" "$fixture_root/linked-file-ring/key-old.xml"
  if bash "$helper" "$target" "$fixture_root/link-file-backup" existing "$fixture_root/linked-file-ring" >/dev/null 2>&1; then
    echo "A symbolic-link key file was accepted." >&2; exit 1
  fi
fi
echo "Data Protection preservation tests passed."
