#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT

backup_root="$fixture_root/deploy-backups"
mkdir -p "$backup_root"

for index in $(seq 1 6); do
  sha="$(printf '%040x' "$index")"
  backup="$backup_root/pre-${sha}-2026072${index}-010101"
  mkdir -p "$backup"
  touch -d "2026-07-2${index} 01:01:01 UTC" "$backup"
done

manual_backup="$backup_root/pre-manual-keep-me-20260701-010101"
protected_backup="$backup_root/pre-0000000000000000000000000000000000000001-20260721-010101"
mkdir -p "$manual_backup"

DRY_RUN=true bash "$script_dir/prune-deploy-backups.sh" "$backup_root" 3 "$protected_backup"
test "$(find "$backup_root" -mindepth 1 -maxdepth 1 -type d | wc -l)" -eq 7

bash "$script_dir/prune-deploy-backups.sh" "$backup_root" 3 "$protected_backup"

test -d "$manual_backup"
test -d "$protected_backup"
test "$(find "$backup_root" -mindepth 1 -maxdepth 1 -type d -name 'pre-????????????????????????????????????????-*' | wc -l)" -eq 4

if bash "$script_dir/prune-deploy-backups.sh" "/" 3 >/dev/null 2>&1; then
  echo "Unsafe root validation did not fail." >&2
  exit 1
fi

echo "Deploy backup pruning tests passed."
