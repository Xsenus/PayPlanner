#!/usr/bin/env bash
set -euo pipefail
umask 077

# Merge historical key rings before switching KeysPath or replacing a release.
# XML contents are secrets: never trace, print or replace conflicting files.
set +x
temporary=""
trap 'if [[ -n "$temporary" ]]; then rm -f -- "$temporary"; fi' EXIT
target="${1:?Usage: preserve-data-protection-keys.sh TARGET SNAPSHOT existing|new [SOURCE ...]}"
snapshot="${2:?A separate snapshot directory is required}"
mode="${3:?Installation mode must be existing or new}"
shift 3

validate_path() {
  local path="$1" probe
  if [[ "$path" != /* ]]; then
    echo "Key directories must use absolute paths." >&2
    return 2
  fi
  probe="$path"
  while [[ "$probe" != / ]]; do
    if [[ -L "$probe" ]]; then
      echo "Refusing a symbolic link in a key directory path." >&2
      return 2
    fi
    probe="$(dirname -- "$probe")"
  done
  if [[ "$(realpath -m -- "$path")" == / ]]; then
    echo "Refusing the filesystem root as a key directory." >&2
    return 2
  fi
}

if [[ "$mode" != existing && "$mode" != new ]]; then
  echo "Installation mode must be existing or new." >&2
  exit 2
fi
for path in "$target" "$snapshot"; do
  validate_path "$path"
done
target="$(realpath -m -- "$target")"
snapshot="$(realpath -m -- "$snapshot")"
if [[ "$snapshot" == "$target" || "$snapshot" == "$target/"* || "$target" == "$snapshot/"* ]]; then
  echo "Key snapshot and active ring must be separate directories." >&2
  exit 2
fi
if [[ -e "$snapshot" ]]; then
  echo "Key snapshot already exists; refusing to replace it." >&2
  exit 2
fi

declare -A files=()
key_count=0
shopt -s nullglob
for source in "$target" "$@"; do
  [[ -n "$source" ]] || continue
  validate_path "$source"
  [[ -d "$source" ]] || continue
  for file in "$source"/*.xml; do
    name="$(basename -- "$file")"
    if [[ -L "$file" || ! -f "$file" || ! "$name" =~ ^[a-zA-Z0-9._-]+\.xml$ ]]; then
      echo "Unsupported file in a Data Protection key directory." >&2
      exit 2
    fi
    if [[ -n "${files[$name]:-}" ]]; then
      if ! cmp -s -- "${files[$name]}" "$file"; then
        echo "Conflicting Data Protection files: $name. Nothing was replaced." >&2
        exit 1
      fi
    else
      files[$name]="$file"
      if [[ "$name" == key-*.xml ]]; then key_count=$((key_count + 1)); fi
    fi
  done
done

if [[ "$mode" == existing && "$key_count" -eq 0 ]]; then
  echo "Existing installation has no recoverable Data Protection keys; deployment stopped." >&2
  exit 1
fi

mkdir -p -- "$target" "$snapshot"
chmod 700 -- "$target" "$snapshot"
for name in "${!files[@]}"; do
  source="${files[$name]}"
  destination="$target/$name"
  if [[ ! -e "$destination" ]]; then
    # Publish a complete file atomically without replacing a concurrently created
    # key. The temporary file lives on the same filesystem as its hard link.
    temporary="$(mktemp "$target/.preserve-key.XXXXXX")"
    cp -p -- "$source" "$temporary"
    chmod 600 -- "$temporary"
    if ! ln -- "$temporary" "$destination" 2>/dev/null; then
      if [[ -L "$destination" ]] || ! cmp -s -- "$source" "$destination"; then
        echo "Data Protection key changed during preservation; deployment stopped." >&2
        exit 1
      fi
    fi
    rm -f -- "$temporary"
    temporary=""
  fi
  if [[ -L "$destination" ]] || ! cmp -s -- "$source" "$destination"; then
    echo "Data Protection key changed during preservation; deployment stopped." >&2
    exit 1
  fi
  chmod 600 -- "$destination"
  cp -p -- "$destination" "$snapshot/$name"
  cmp -s -- "$destination" "$snapshot/$name"
done
echo "Data Protection preserved: keys=$key_count files=${#files[@]} mode=$mode"
