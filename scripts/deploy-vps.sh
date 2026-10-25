#!/usr/bin/env bash
set -euo pipefail
: "${DEPLOY_SHA:?Expected revision is required}"
: "${DEPLOY_ARCHIVE_NAME:?Qualified archive is required}"
: "${PUBLIC_API_HEALTH_URL:?Public API health URL is required}"
: "${PUBLIC_FRONTEND_URL:?Public frontend URL is required}"
set -x

TAR="/tmp/${DEPLOY_ARCHIVE_NAME}"
STAGE="/tmp/payplanner-${DEPLOY_SHA}"
APP_DIR="/opt/payplanner/app"
BE_DIR="/opt/payplanner/app/backend"
FE_APP_DIR="/opt/payplanner/app/frontend"
FE_PUBLIC_DIR="/var/www/crm.elitpravo.com"
PRIMARY_HEALTH_URL="http://127.0.0.1:5080/api/health"
PUBLIC_API_HEALTH_URL="${PUBLIC_API_HEALTH_URL}"
PUBLIC_FRONTEND_URL="${PUBLIC_FRONTEND_URL}"
RUNTIME_ENV_FILE="/opt/payplanner/shared/payplanner.env"
SYSTEMD_ENV_DROPIN="/etc/systemd/system/payplanner.service.d/20-payplanner-runtime-env.conf"
PERSISTENT_FILE_ROOT="/opt/payplanner/shared/files"
DATA_PROTECTION_KEYS="/opt/payplanner/shared/data-protection-keys"
LEGACY_STORAGE_MIGRATION_MARKER="$PERSISTENT_FILE_ROOT/.legacy-app-data-migrated-v1"
MIN_FREE_KB=262144
BACKUP_ROOT="/opt/payplanner/deploy-backups"
BACKUP_DIR="$BACKUP_ROOT/pre-${DEPLOY_SHA}-$(date +%Y%m%d-%H%M%S)"
DEPLOY_BACKUP_KEEP_COUNT=10

cleanup() {
  rm -rf "$STAGE" "$TAR" "/tmp/${DEPLOY_ARCHIVE_NAME}"
}

resolve_package() {
  if [ -f "$TAR" ]; then
    return 0
  fi

  local nested_tar="/tmp/${DEPLOY_ARCHIVE_NAME}/${DEPLOY_ARCHIVE_NAME}"
  if [ -f "$nested_tar" ]; then
    TAR="$nested_tar"
    return 0
  fi

  echo "Deploy package was not found at expected paths:"
  echo "  /tmp/${DEPLOY_ARCHIVE_NAME}"
  echo "  $nested_tar"
  echo "Recent /tmp PayPlanner artifacts:"
  ls -la /tmp/payplanner* 2>/dev/null || true
  return 1
}

check_space() {
  local path="$1"
  local min_kb="$2"
  local avail_kb
  avail_kb="$(df -Pk "$path" | awk 'NR==2 {print $4}')"
  if [ "${avail_kb:-0}" -lt "$min_kb" ]; then
    echo "Low disk space on $(df -Pk "$path" | awk 'NR==2 {print $6}'): ${avail_kb} KB available, need at least ${min_kb} KB"
    df -h "$path" || true
    return 1
  fi
}

configure_runtime_env() {
  set +x
  local webpush_enabled="${PAYPLANNER_WEBPUSH_ENABLED:-false}"
  local webpush_public_key="${PAYPLANNER_WEBPUSH_PUBLIC_KEY:-}"
  local webpush_private_key="${PAYPLANNER_WEBPUSH_PRIVATE_KEY:-}"
  local webpush_subject="${PAYPLANNER_WEBPUSH_SUBJECT:-}"

  case "$(printf '%s' "$webpush_enabled" | tr '[:upper:]' '[:lower:]')" in
    true|1|yes|on) webpush_enabled="true" ;;
    false|0|no|off) webpush_enabled="false" ;;
    *)
      echo "PAYPLANNER_WEBPUSH_ENABLED must be true or false."
      set -x
      return 1
      ;;
  esac

  if [ "$webpush_enabled" = "true" ]; then
    if [ -z "$webpush_public_key" ] || [ -z "$webpush_private_key" ] || [ -z "$webpush_subject" ]; then
      echo "Web Push is enabled, but PAYPLANNER_WEBPUSH_PUBLIC_KEY, PAYPLANNER_WEBPUSH_PRIVATE_KEY or PAYPLANNER_WEBPUSH_SUBJECT is missing."
      echo "Add GitHub repository secrets or set repository variable PAYPLANNER_WEBPUSH_ENABLED=false."
      set -x
      return 1
    fi
  fi

  local tmp_env
  tmp_env="$(mktemp)"
  local existing_env
  existing_env="$(mktemp)"

  if sudo test -f "$RUNTIME_ENV_FILE"; then
    sudo sed \
      -e '/^WebPush__/d' \
      -e '/^FileStorage__RootPath=/d' \
      -e '/^ProfilePhotos__StorageRoot=/d' \
      -e '/^ElectronicSignature__StorageRoot=/d' \
      -e '/^DataProtection__KeysPath=/d' \
      -e '/^DataProtection__KeysDirectory=/d' \
      "$RUNTIME_ENV_FILE" > "$existing_env"
  fi

  {
    cat "$existing_env"
    printf 'FileStorage__RootPath=%s\n' "$PERSISTENT_FILE_ROOT"
    printf 'ProfilePhotos__StorageRoot=%s\n' "$PERSISTENT_FILE_ROOT/user-photos"
    printf 'ElectronicSignature__StorageRoot=%s\n' "$PERSISTENT_FILE_ROOT/electronic-signatures"
    printf 'DataProtection__KeysPath=%s\n' "$DATA_PROTECTION_KEYS"
    # Preserve the existing ApplicationName, including its absence.
    # Changing it invalidates ciphertext even when every key is present.
    printf 'WebPush__Enabled=%s\n' "$webpush_enabled"
    if [ -n "$webpush_public_key" ]; then printf 'WebPush__PublicKey=%s\n' "$webpush_public_key"; fi
    if [ -n "$webpush_private_key" ]; then printf 'WebPush__PrivateKey=%s\n' "$webpush_private_key"; fi
    if [ -n "$webpush_subject" ]; then printf 'WebPush__Subject=%s\n' "$webpush_subject"; fi
  } > "$tmp_env"

  if ! grep -q '^ConnectionStrings__Default=' "$tmp_env" || ! grep -q '^Jwt__Secret=' "$tmp_env"; then
    echo "Runtime environment file must already contain ConnectionStrings__Default and Jwt__Secret on VPS."
    echo "Refusing to overwrite $RUNTIME_ENV_FILE with an incomplete runtime configuration."
    rm -f "$tmp_env" "$existing_env"
    set -x
    return 1
  fi

  sudo install -o root -g payplanner -m 0640 "$tmp_env" "$RUNTIME_ENV_FILE"
  rm -f "$tmp_env" "$existing_env"
  sudo mkdir -p "$(dirname "$SYSTEMD_ENV_DROPIN")"
  printf '[Service]\nEnvironmentFile=-%s\n' "$RUNTIME_ENV_FILE" | sudo tee "$SYSTEMD_ENV_DROPIN" >/dev/null
  set -x
}

preserve_data_protection_keys() {
  set +x
  local previous_keys_path="" previous_keys_directory="" service_home
  sudo mkdir -p "$BACKUP_DIR"
  sudo chmod 700 "$BACKUP_DIR"
  if sudo test -f "$RUNTIME_ENV_FILE"; then
    sudo cp -a "$RUNTIME_ENV_FILE" "$BACKUP_DIR/payplanner.env.previous"
    previous_keys_path="$(sudo sed -n 's/^DataProtection__KeysPath=//p' "$RUNTIME_ENV_FILE" | tail -n 1)"
    previous_keys_directory="$(sudo sed -n 's/^DataProtection__KeysDirectory=//p' "$RUNTIME_ENV_FILE" | tail -n 1)"
    previous_keys_path="${previous_keys_path#\"}"; previous_keys_path="${previous_keys_path%\"}"
    previous_keys_directory="${previous_keys_directory#\"}"; previous_keys_directory="${previous_keys_directory%\"}"
  fi
  service_home="$(getent passwd payplanner | cut -d: -f6)"
  # Merge both configuration aliases before KeysPath takes precedence.
  # Older versions also used the service account's default ASP.NET ring.
  sudo bash "$STAGE/deploy/preserve-data-protection-keys.sh" \
    "$DATA_PROTECTION_KEYS" "$BACKUP_DIR/data-protection-keys.previous" existing \
    "$previous_keys_path" "$previous_keys_directory" \
    "$service_home/.aspnet/DataProtection-Keys" \
    "/opt/payplanner/shared/dataprotection" \
    "$BE_DIR/.aspnet/DataProtection-Keys"
  set -x
}

migrate_legacy_file_storage() {
  local before_count
  local after_count

  sudo mkdir -p \
    "$PERSISTENT_FILE_ROOT/task-files" \
    "$PERSISTENT_FILE_ROOT/case-documents" \
    "$PERSISTENT_FILE_ROOT/user-photos" \
    "$PERSISTENT_FILE_ROOT/electronic-signatures" \
    "/opt/payplanner/shared/data-protection-keys"
  if sudo test -d "$BE_DIR/wwwroot/user-photos"; then
    sudo rsync -a --ignore-existing "$BE_DIR/wwwroot/user-photos/" "$PERSISTENT_FILE_ROOT/user-photos/"
  fi
  if sudo test -d "$BE_DIR/storage/electronic-signatures"; then
    sudo rsync -a --ignore-existing "$BE_DIR/storage/electronic-signatures/" "$PERSISTENT_FILE_ROOT/electronic-signatures/"
  fi
  before_count="$(sudo find "$PERSISTENT_FILE_ROOT" -type f ! -name '.legacy-app-data-migrated-v1' | wc -l)"
  if sudo test -f "$LEGACY_STORAGE_MIGRATION_MARKER"; then
    echo "Persistent file storage is ready: files=$before_count"
    return 0
  fi

  if sudo test -d "$BE_DIR/App_Data"; then
    sudo rsync -a --ignore-existing "$BE_DIR/App_Data/" "$PERSISTENT_FILE_ROOT/"
  fi

  while IFS= read -r -d '' legacy_app_data; do
    sudo rsync -a --ignore-existing "$legacy_app_data/" "$PERSISTENT_FILE_ROOT/"
  done < <(sudo find "$BACKUP_ROOT" -mindepth 3 -maxdepth 3 -type d -path '*/backend.previous/App_Data' -print0 | sort -z)

  printf '%s\n' "${DEPLOY_SHA}" | sudo tee "$LEGACY_STORAGE_MIGRATION_MARKER" >/dev/null
  after_count="$(sudo find "$PERSISTENT_FILE_ROOT" -type f ! -name '.legacy-app-data-migrated-v1' | wc -l)"
  echo "Persistent file storage restored: before=$before_count after=$after_count restored=$((after_count - before_count))"
  sudo du -sh "$PERSISTENT_FILE_ROOT"
}

trap cleanup EXIT

echo "[1/14] Preflight disk check"
check_space "/" "$MIN_FREE_KB"

echo "[2/14] Prepare staging"
resolve_package
rm -rf "$STAGE"
mkdir -p "$STAGE"
tar -xzf "$TAR" -C "$STAGE"

echo "[3/14] Ensure target dirs"
sudo mkdir -p "$BE_DIR" "$FE_APP_DIR" "$FE_PUBLIC_DIR" "$BACKUP_ROOT" /opt/payplanner/shared/backups "$PERSISTENT_FILE_ROOT" /opt/payplanner/shared/data-protection-keys

echo "[4/14] Preserve Data Protection keys and configure runtime environment"
preserve_data_protection_keys
configure_runtime_env

echo "[5/14] Backup current release"
sudo mkdir -p "$BACKUP_DIR"
if [ -d "$BE_DIR" ]; then sudo cp -a "$BE_DIR" "$BACKUP_DIR/backend.previous"; fi
if [ -d "$FE_APP_DIR" ]; then sudo cp -a "$FE_APP_DIR" "$BACKUP_DIR/frontend.previous"; fi
if [ -f "$APP_DIR/REVISION" ]; then sudo cp -a "$APP_DIR/REVISION" "$BACKUP_DIR/REVISION.previous"; fi
if [ -f "$APP_DIR/.deploy-revision" ]; then sudo cp -a "$APP_DIR/.deploy-revision" "$BACKUP_DIR/.deploy-revision.previous"; fi

echo "[6/14] Restore persistent file storage"
migrate_legacy_file_storage

echo "[7/14] Rsync backend"
sudo rsync -a --delete "$STAGE/be/" "$BE_DIR/"

echo "[8/14] Rsync frontend"
# Remove stale hashed chunks: the frontend reloads itself if a browser tries to load an old lazy chunk.
sudo rsync -a --delete "$STAGE/fe/" "$FE_APP_DIR/"
sudo rsync -a --delete "$STAGE/fe/" "$FE_PUBLIC_DIR/"

echo "[9/14] Write revision marker"
printf '%s\n' "${DEPLOY_SHA}" | sudo tee "$APP_DIR/REVISION" >/dev/null
printf '%s\n' "${DEPLOY_SHA}" | sudo tee "$APP_DIR/.deploy-revision" >/dev/null

echo "[10/14] Permissions"
sudo chown -R payplanner:payplanner /opt/payplanner
sudo chown -R www-data:www-data "$FE_PUBLIC_DIR"

echo "[11/14] Restart service"
check_space "/" "$MIN_FREE_KB"
sudo systemctl daemon-reload || true
sudo systemctl enable payplanner.service
sudo systemctl restart payplanner.service

echo "[12/14] Wait for health"
ok=0
for i in $(seq 1 30); do
  if curl -fsS "$PRIMARY_HEALTH_URL" | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"'; then
    ok=1
    echo "Health OK on attempt $i via $PRIMARY_HEALTH_URL"
    break
  fi
  sleep 2
done
if [ "$ok" -ne 1 ]; then
  echo "Health check failed after retries: $PRIMARY_HEALTH_URL"
  sudo systemctl status payplanner.service --no-pager || true
  sudo systemctl show payplanner.service -p ExecStart -p FragmentPath -p DropInPaths || true
  sudo df -h "$BE_DIR" || true
  sudo du -sh /opt/payplanner/shared/backups /tmp 2>/dev/null || true
  sudo ss -ltnp | grep dotnet || true
  sudo journalctl -u payplanner.service --no-pager -n 200 || true
  exit 1
fi

echo "[13/14] Check public endpoints"
curl -fsS "$PUBLIC_API_HEALTH_URL" | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"'
curl -fsS -o /dev/null "$PUBLIC_FRONTEND_URL"

echo "[14/14] Prune old automatic deploy backups"
sudo bash "$STAGE/deploy/prune-deploy-backups.sh" \
  "$BACKUP_ROOT" \
  "$DEPLOY_BACKUP_KEEP_COUNT" \
  "$BACKUP_DIR"
cleanup
trap - EXIT
