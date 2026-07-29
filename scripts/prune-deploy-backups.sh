#!/usr/bin/env bash
set -euo pipefail

backup_root="${1:?Usage: prune-deploy-backups.sh BACKUP_ROOT [KEEP_COUNT] [PROTECTED_BACKUP]}"
keep_count="${2:-10}"
protected_backup="${3:-}"
dry_run="${DRY_RUN:-false}"

if ! [[ "$keep_count" =~ ^[0-9]+$ ]] || (( keep_count < 2 )); then
  echo "KEEP_COUNT must be an integer greater than or equal to 2." >&2
  exit 2
fi

if [[ ! -d "$backup_root" ]]; then
  echo "Backup root does not exist: $backup_root" >&2
  exit 2
fi

backup_root="$(readlink -f -- "$backup_root")"
if [[ -z "$backup_root" || "$backup_root" == "/" ]]; then
  echo "Refusing to prune an unsafe backup root: $backup_root" >&2
  exit 2
fi

if [[ -n "$protected_backup" ]]; then
  protected_backup="$(readlink -f -- "$protected_backup")"
  if [[ "$protected_backup" != "$backup_root/"* ]]; then
    echo "Protected backup is outside backup root: $protected_backup" >&2
    exit 2
  fi
fi

declare -a automatic_backups=()
while IFS= read -r -d '' entry; do
  candidate="${entry#* }"
  name="$(basename -- "$candidate")"
  if [[ "$name" =~ ^pre-[0-9a-f]{40}-[0-9]{8}-[0-9]{6}$ ]]; then
    automatic_backups+=("$candidate")
  fi
done < <(find "$backup_root" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\0' | sort -z -nr)

removed=0
for (( index=keep_count; index<${#automatic_backups[@]}; index++ )); do
  candidate="$(readlink -f -- "${automatic_backups[$index]}")"
  if [[ "$candidate" == "$protected_backup" ]]; then
    echo "Keeping protected backup: $candidate"
    continue
  fi

  if [[ "$(dirname -- "$candidate")" != "$backup_root" ]]; then
    echo "Refusing to remove a path outside backup root: $candidate" >&2
    exit 2
  fi

  if [[ "$dry_run" == "true" ]]; then
    echo "Would remove old automatic backup: $candidate"
  else
    echo "Removing old automatic backup: $candidate"
    rm -rf -- "$candidate"
  fi
  removed=$((removed + 1))
done

echo "Deploy backup retention complete: automatic=${#automatic_backups[@]} keep=$keep_count removed=$removed dry_run=$dry_run"
