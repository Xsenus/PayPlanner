#!/usr/bin/env bash
# CI delivery derived from the accepted stopped backup/offsite/install boundary.
# Package provenance comes from actual trusted Actions needs, not local R12 results.
# No SSH transport and no database restore are implemented here.
set -Eeuo pipefail
umask 077
if [[ "$#" -eq 0 ]]; then
  printf '%s\n' 'NOEXEC: draft only; no server inspection or mutation performed.'
  printf '%s\n' 'Entry: bash deploy-vps.sh --execute backup|install|abort <reviewed-plan-sha256>'
  exit 0
fi
[[ "$#" -eq 3 && "$1" == '--execute' ]] || exit 64
PHASE="$2"
[[ "$PHASE" == backup || "$PHASE" == install || "$PHASE" == abort ]] || exit 64
PLAN_SHA="$3"
HERE="$(cd -- "$(dirname -- "$0")" && pwd -P)"
SELF="$HERE/deploy-vps.sh"
PLAN="$HERE/installer-plan.json"
DB_PROOF="$HERE/deploy-db-proof.py"
HISTORY_PROOF="$HERE/deploy-history-source-proof.py"
STOPPED_PROOF="$HERE/deploy-stopped-source-proof.py"
[[ "$EUID" -eq 0 && "$PLAN_SHA" =~ ^[a-f0-9]{64}$ ]] || exit 65

# Only nonsecret, pinned data is read. Never source a shell config/production env.
CONFIG_VALUES="$(python3 - "$PLAN" "$PLAN_SHA" "$SELF" "$DB_PROOF" "$HISTORY_PROOF" "$PHASE" <<'PY'
import hashlib,json,pathlib,re,sys
p,expected,self_path,db_path,history_path,phase=sys.argv[1:]
def need(ok,msg):
    if not ok:raise SystemExit(msg)
def sha(p):
    with pathlib.Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def read(p):return json.loads(pathlib.Path(p).read_text(encoding='utf-8'))
def pin(row):
    n=row['serverRelativePath'];q=pathlib.PurePosixPath(n)
    need(not q.is_absolute() and q.as_posix()==n and '..' not in q.parts and '\\' not in n,'Unsafe evidence path')
    target=pathlib.Path(p).parent/n
    need(target.resolve().is_relative_to(pathlib.Path(p).parent.resolve()),'Evidence escapes the reviewed directory')
    need(not target.is_symlink() and target.is_file() and sha(target)==row['sha256'],'Pinned evidence mismatch')
    return target
need(sha(p)==expected,'Unreviewed plan bytes');c=read(p)
need(c['rootAccepted'] is True and c['runAuthorised'] is True and c['phase']==phase,'NOEXEC: phase not authorised')
need(c['status']=='ROOT_ADOPTED_FINAL_DELIVERY' and c['quietWindowAccepted'] is True,'Delivery/quiet window not adopted')
need(sha(self_path)==c['installerSha256'] and sha(db_path)==c['databaseProofSha256'],'Installer/database-proof changed')
need(re.fullmatch('[a-f0-9]{64}',c['historyProofSha256'] or '') and sha(history_path)==c['historyProofSha256'],'History proof changed')
need(sha(pathlib.Path(p).parent/'deploy-stopped-source-proof.py')==c['stoppedProofSha256'],'Stopped source proof changed')
rev,old=c['releaseSha'],c['previousRevision']
need(isinstance(rev,str) and re.fullmatch('[a-f0-9]{40}',rev) and rev!=old,'Exact new 40-hex commit required')
need(old=='e85d1f22d795b78ca8c3543eb3b14410c3a75747','Reassess changed production baseline')
need(re.fullmatch('[0-9]{8}T[0-9]{6}Z',c['backupUtc'] or ''),'Explicit backup identity required')
for v in [c['archive']['sha256'],c['expectedApiSha256'],c['expectedPreviousApiSha256'],c['expectedMigrationsSha256'],c['expectedPreviousMigrationsSha256']]:
    need(isinstance(v,str) and re.fullmatch('[a-f0-9]{64}',v),'Actual artifact/archive SHA missing')
need(type(c['archive']['bytes']) is int and c['archive']['bytes']>0,'Actual archive size missing')
need(type(c['minimumRemainingBytes']) is int and c['minimumRemainingBytes']>=3*1024**3,'Reserve below reviewed floor')
need(c['policy']==dict(localEvidenceIsCI=False,automaticDatabaseRestore=False,productionDataRepair=False,sendRealNotificationsForSmoke=False,sourceOrArtifactRebuild=False,credentialInThisPlan=False),'Policy changed')
need(c['expectedMigration'] is None,'No migration permitted after installed142')
need(c['expectedAddedProtectedTables']==[] and len(c['expectedProtectedTables'])==16 and all(t in c['expectedProtectedTables'] for t in ['Acts','Contracts','Payments','TaskJournalItems']),'All16 installed protected tables remain in scope')
need(c['rollbackAfterStartup']=='halt-for-manual-review','Unqualified backward restart forbidden')
need(c['offsiteHistoryReceipt']['serverRelativePath']=='history-offsite-verified.json' and c['historyManifest']['serverRelativePath']=='history-manifest.json','Only fixed reviewed history evidence paths are allowed')
h=read(pin(c['offsiteHistoryReceipt']));hm=read(pin(c['historyManifest']))
need(h['schemaVersion']==2 and h['rootAccepted'] is True and h['verifiedOutsideVps'] is True and h['historyRoot']=='/opt/payplanner/shared/backups','Complete offsite history-v2 adoption required')
need(h['currentManifestSha256']==c['historyManifest']['sha256'] and hm['schemaVersion']==1 and hm['root']=='/opt/payplanner/shared/backups','History manifest binding differs')
need(all(h[n] is True for n in ['sourceBeforeAfterExact','allCurrentEntriesCovered','allFileHashesMatched','allEntryMetadataMatched','fullDiskReadbackVerifiedInClosedClient','retainedForRecovery','previousRecoveryPointRetained']),'Incomplete history-v2 proof')
need(h['nativeDeltaTarExitCode']==0 and h['nativeDeltaTarAclXattrFlags'] is True and h['localEvidenceIsCI'] is False,'Native history delta not qualified')
need(len(h['archiveReferences'])==2 and all(re.fullmatch('[a-f0-9]{64}',a['sha256']) and type(a['bytes']) is int and a['bytes']>0 for a in h['archiveReferences']),'Both full base/delta archives required')
need(len(hm['entries'])==h['sourceEntryCount'] and sum(e['kind']=='file' for e in hm['entries'])==h['sourceFileCount'],'History source counts differ')
need(set(h['coverage'])=={e['path'] for e in hm['entries']} and all(type(v) is int and v in [0,1] for v in h['coverage'].values()),'History coverage differs')
coverage=(json.dumps(h['coverage'],ensure_ascii=True,sort_keys=True,separators=(',',':'))+'\n').encode('ascii')
need(hashlib.sha256(coverage).hexdigest()==h['coverageSha256'],'History coverage checksum differs')
q=read(pin(c['qualificationReceipt']))
need(q['schemaVersion']==1 and q['executionKind']=='github-actions-qualified-artifacts' and q['ciQualified'] is True and q['releaseSha']==rev,'Actual CI qualification required')
need(q['repository']=='Xsenus/PayPlanner' and q['ref']=='refs/heads/main' and q['job']=='deploy' and q['workflowSha']==rev,'Trusted main workflow identity required')
need(re.fullmatch('[1-9][0-9]*',q['runId']) and re.fullmatch('[1-9][0-9]*',q['runAttempt']),'Actual CI run identity required')
need(set(q['needs'])=={'frontend','backend-format','backend'} and all(v['result']=='success' for v in q['needs'].values()),'All required producer jobs must succeed')
need(re.fullmatch('xsenus-local-v1:[a-f0-9]{64}',q['backendArtifactId']) and re.fullmatch('xsenus-local-v1:[a-f0-9]{64}',q['frontendArtifactId']) and q['backendArtifactId']!=q['frontendArtifactId'],'Exact qualified artifact IDs required')
need(q['needs']['backend']['outputs']['backend-artifact-id']==q['backendArtifactId'] and q['needs']['frontend']['outputs']['frontend-artifact-id']==q['frontendArtifactId'],'Artifact producer binding differs')
need(q['needs']['backend']['outputs']['manifest-sha']==q['backendManifestSha256'] and re.fullmatch('[a-fA-F0-9]{64}',q['backendManifestSha256']),'Backend strict manifest union required')
need(q['backendPackageFiles'] and len(q['frontendRawFiles'])==c['expectedFrontendRawFileCount'] and all(n in q['frontendRawFiles'] for n in ['index.html','payplanner-app-cache.js','payplanner-push-worker.js','payplanner-worker.js']),'Complete qualified package maps required')
need('deploy-revision.txt' not in q['frontendRawFiles'],'Revision is bound separately')
need(c['expectedMigration'] is None and c['expectedAddedProtectedTables']==[],'This release permits no database migration')
need(q['expectedReleaseSeeds']==c['expectedReleaseSeeds'],'Only the reviewed new release entries may be inserted')
if phase=='install':pin(c['offsiteReceipt'])
print('|'.join([rev,old,c['backupUtc'],c['archive']['sha256'],str(c['archive']['bytes']),c['expectedApiSha256'],c['expectedPreviousApiSha256'],c['expectedMigrationsSha256'],str(c['minimumRemainingBytes']),c['expectedPreviousMigrationsSha256']]))
PY
)" || exit 65
IFS='|' read -r REV OLD BACKUP_UTC ARCHIVE_SHA ARCHIVE_BYTES API_SHA OLD_API_SHA MIGRATIONS_SHA RESERVE OLD_MIGRATIONS_SHA <<< "$CONFIG_VALUES"
SERVICE=payplanner.service
APP=/opt/payplanner/app
BE=/opt/payplanner/app/backend
FE=/opt/payplanner/app/frontend
PUBLIC=/var/www/crm.elitpravo.com
SHARED=/opt/payplanner/shared
HISTORY=/opt/payplanner/shared/backups
ENV_FILE=/opt/payplanner/shared/payplanner.env
KEYS=/opt/payplanner/shared/data-protection-keys
FILES=/opt/payplanner/shared/files
ARCHIVE="/tmp/payplanner_${REV}.tar.gz"
STAGE="/opt/payplanner/manual-deploy-staging/$REV-ci-01"
BACKUP="/opt/payplanner/deploy-backups/manual-$REV-$BACKUP_UTC"
exec 9>/run/lock/payplanner-manual-deploy.lock
flock -n 9 || exit 66
for path in "$APP" "$BE" "$FE" "$PUBLIC" "$SHARED" "$KEYS" "$FILES"; do
  [[ -d "$path" && ! -L "$path" ]] || { printf '%s\n' 'Reassess missing/redirected application/shared path.' >&2; exit 67; }
done
[[ -f "$ENV_FILE" && ! -L "$ENV_FILE" ]] || exit 67
# Do not silently delete a backend-local persistent store or follow nested links.
[[ -z "$(find "$BE" "$FE" "$PUBLIC" -type l -print -quit)" ]] || exit 67
[[ ! -d "$BE/App_Data" || -z "$(find "$BE/App_Data" -type f -print -quit)" ]] || { printf '%s\n' 'Backend-local App_Data needs explicit preservation review.' >&2; exit 67; }
for tool in python3 rsync tar sha256sum systemctl curl pg_restore sudo flock; do command -v "$tool" >/dev/null; done
revision_is() {
  for p in "$APP/REVISION" "$APP/.deploy-revision" "$FE/deploy-revision.txt" "$PUBLIC/deploy-revision.txt"; do
    [[ -f "$p" && ! -L "$p" && "$(tr -d '\r\n' < "$p")" == "$1" ]] || return 1
  done
}
space_at_least() { [[ "$(df --output=avail -B1 "$APP" | tail -n 1)" -ge "$1" ]]; }
file_sha() { sha256sum -- "$1" | cut -d ' ' -f 1; }
db_proof() {
  if [[ "$1" == "$BACKUP/database-before.json" ]]; then
    python3 "$DB_PROOF" "$1" > "$1.summary.private.json" || return "$?"
  else
    python3 "$DB_PROOF" "$1" "$BACKUP/database-before.json" > "$1.summary.private.json" || return "$?"
  fi
  python3 - "$PLAN" "$1" <<'PY'
import json,sys
c,p=(json.load(open(n,encoding='utf-8')) for n in sys.argv[1:])
assert sorted(r['table'] for r in p['fingerprints'])==sorted(c['expectedProtectedTables']+c['expectedAddedProtectedTables'])
if p['baselineSha256'] is None:
 import hashlib
 assert p['controls']['migrations']==c['expectedBaselineMigrations'],'Installed migration history differs; no rebaseline/startup permitted'
 assert hashlib.sha256(json.dumps(p['controls']['migrations'],sort_keys=True,separators=(',',':')).encode()).hexdigest()==c['expectedBaselineMigrationIdsSha256']
 assert len(p['migrationRows'])==209 and p['migrationRows']==c['expectedBaselineMigrationRows'],'Installed209 full history rows/ProductVersion differ; no rebaseline/startup permitted'
 assert all(type(v) is int and v>=0 for v in p['newPolicyNonNullCounts'].values()),'Expected installed nullable policy columns required'
assert all(p['controls'][n]==0 for n in ['legacyActive','pendingContractor','invalidIndexes','invalidConstraints'])
PY
}
same_db() {
  python3 "$DB_PROOF" --compare "$PLAN" "$1" "$2" "${3:-unchanged}"
}
preserved_tree() {
  python3 - "$ENV_FILE" "$KEYS" "$FILES" "$BE/appsettings.json" "$BE/appsettings.Production.json" /etc/systemd/system/payplanner.service /etc/systemd/system/payplanner.service.d > "$1" <<'PY'
import hashlib,json,os,pathlib,stat,sys
out={}
for root_name in sys.argv[1:]:
    root=pathlib.Path(root_name)
    paths=[root]+sorted(root.rglob('*')) if root.is_dir() and not root.is_symlink() else [root]
    for p in paths:
        try:s=p.lstat()
        except FileNotFoundError:out[str(p)]={'absent':True};continue
        r={'mode':stat.S_IMODE(s.st_mode),'uid':s.st_uid,'gid':s.st_gid}
        if stat.S_ISLNK(s.st_mode):r['link']=os.readlink(p)
        elif stat.S_ISREG(s.st_mode):
            with p.open('rb') as f:r['sha256']=hashlib.file_digest(f,'sha256').hexdigest()
        elif not stat.S_ISDIR(s.st_mode):raise SystemExit('Unexpected persistent object')
        out[str(p)]=r
print(json.dumps(out,sort_keys=True,indent=2))
PY
}
package_check() {
  python3 - "$PLAN" "$HERE" "$ARCHIVE" "$REV" <<'PY'
import hashlib,json,pathlib,re,sys,tarfile
plan,here,archive,rev=sys.argv[1:];c=json.load(open(plan,encoding='utf-8'))
with open(archive,'rb') as f:assert hashlib.file_digest(f,'sha256').hexdigest()==c['archive']['sha256']
assert pathlib.Path(archive).stat().st_size==c['archive']['bytes']
with tarfile.open(archive,'r:gz') as t:
    members=t.getmembers();names=[m.name for m in members];assert len(names)==len(set(names))
    for m in members:
        p=pathlib.PurePosixPath(m.name)
        assert m.isfile() and not p.is_absolute() and p.as_posix()==m.name and '..' not in p.parts and '\\' not in m.name
        assert not any(ord(ch)<32 for ch in m.name)
        assert m.name in ['deployment-manifest.json','SHA256SUMS'] or m.name.startswith(('be/','fe/'))
    manifest_bytes=t.extractfile('deployment-manifest.json').read();m=json.loads(manifest_bytes);assert m['revision']==rev
    entries=m['files'];assert len({r['path'] for r in entries})==len(entries)
    assert set(names)=={r['path'] for r in entries}|{'deployment-manifest.json','SHA256SUMS'}
    checks={}
    for r in entries:
        assert r['path'].startswith(('be/','fe/'))
        with t.extractfile(r['path']) as f:assert hashlib.file_digest(f,'sha256').hexdigest()==r['sha256']
        assert t.getmember(r['path']).size==r['bytes'];checks[r['path']]=r['sha256']
    checks['deployment-manifest.json']=hashlib.sha256(manifest_bytes).hexdigest()
    lines=t.extractfile('SHA256SUMS').read().decode('utf-8').splitlines();parsed={}
    for line in lines:
        h,sep,n=line.partition('  ');assert sep and re.fullmatch('[a-f0-9]{64}',h) and n not in parsed;parsed[n]=h
    assert parsed==checks
    q=json.load(open(pathlib.Path(here)/c['qualificationReceipt']['serverRelativePath'],encoding='utf-8'))
    assert {r['path'][3:]:r['sha256'] for r in entries if r['path'].startswith('be/')}==q['backendPackageFiles']
    fe={r['path'][3:]:r['sha256'] for r in entries if r['path'].startswith('fe/')}
    assert len(fe)==c['expectedFrontendRawFileCount']+1 and {n:h for n,h in fe.items() if n!='deploy-revision.txt'}==q['frontendRawFiles']
    assert t.extractfile('fe/deploy-revision.txt').read()==(rev+'\n').encode()
    assert checks['be/PayPlanner.Api.dll']==c['expectedApiSha256']
    assert checks['be/PayPlanner.Api.Migrations.dll']==c['expectedMigrationsSha256']
    assert checks['be/AppReleases/releases.json']==q['activeReleaseJsonSha256']
PY
}
installed_check() {
  python3 - "$STAGE/deployment-manifest.json" "$BE" "$FE" "$PUBLIC" "$BACKUP/backend.previous" <<'PY'
import hashlib,json,pathlib,sys
m=json.load(open(sys.argv[1],encoding='utf-8'));be,fe,public,old=map(pathlib.Path,sys.argv[2:])
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
for root,prefix in [(be,'be/'),(fe,'fe/'),(public,'fe/')]:
    expected={r['path'][3:]:r['sha256'] for r in m['files'] if r['path'].startswith(prefix)}
    if prefix=='be/':
        for n in ['appsettings.json','appsettings.Production.json']:
            if (old/n).is_file():expected[n]=sha(old/n)
    assert not any(p.is_symlink() for p in root.rglob('*'))
    actual={p.relative_to(root).as_posix():sha(p) for p in root.rglob('*') if p.is_file()}
    assert actual==expected,'Installed package differs, including unexpected files'
PY
}
history_check() {
  # The exact pinned helper owns canonical encoding/metadata semantics.
  python3 "$HISTORY_PROOF" "$1" && cmp -s "$HERE/history-manifest.json" "$1"
}
public_check() {
  # Reuse the existing verifier HTTP/hash checks while the existing startup ERR/HUP HALT stays armed.
  python3 - "$PLAN" "$HERE" "$REV" <<'PY'
import hashlib,json,pathlib,sys,urllib.request,urllib.parse
plan=json.load(open(sys.argv[1],encoding='utf-8'));here=pathlib.Path(sys.argv[2]);rev=sys.argv[3]
raw=(here/plan['qualificationReceipt']['serverRelativePath']).read_bytes()
assert hashlib.sha256(raw).hexdigest()==plan['qualificationReceipt']['sha256']
q=json.loads(raw);expected=q['frontendRawFiles'];assert len(expected)==plan['expectedFrontendRawFileCount']
def get(url):
 req=urllib.request.Request(url,headers={'Accept-Encoding':'identity','Cache-Control':'no-cache'})
 with urllib.request.urlopen(req,timeout=20) as response:
  assert response.status==200 and response.geturl()==url
  assert response.headers.get('Content-Encoding','identity')=='identity'
  return response.read(),response.headers
for url in ['http://127.0.0.1:5080/api/health','https://api.elitpravo.com/api/health']:
 body,_=get(url);assert json.loads(body)['status']=='ok'
for rel,digest in expected.items():
 path=pathlib.PurePosixPath(rel);assert not path.is_absolute() and '..' not in path.parts
 body,_=get('https://crm.elitpravo.com/'+urllib.parse.quote(rel,safe='/')+'?deployment='+rev)
 assert hashlib.sha256(body).hexdigest()==digest
body,headers=get('https://crm.elitpravo.com/')
assert hashlib.sha256(body).hexdigest()==expected['index.html'] and headers.get('Strict-Transport-Security')
body,_=get('https://crm.elitpravo.com/deploy-revision.txt?deployment='+rev);assert body==(rev+'\n').encode()
assert all(name in expected for name in ['payplanner-app-cache.js','payplanner-push-worker.js','payplanner-worker.js'])
print(json.dumps({'status':'passed-public-maps-health-readonly','publicRawAssetCount':len(expected),'workerCount':3,'releaseSha':rev}))
PY
}
STOPPED=false
SWITCHED=false
STARTUP_ATTEMPTED=false
service_inactive() {
  [[ "$(systemctl show "$SERVICE" -p ActiveState --value)" == inactive ]] &&
  [[ "$(systemctl show "$SERVICE" -p MainPID --value)" == 0 ]]
}
old_restart_if_safe() {
  local label="$1"
  [[ -f "$BACKUP/database-before.json" && -f "$BACKUP/preserved-before.private.json" ]] || return 1
  if service_inactive &&
     revision_is "$OLD" &&
     [[ "$(file_sha "$BE/PayPlanner.Api.dll")" == "$OLD_API_SHA" ]] &&
     db_proof "$BACKUP/database-$label-stopped.json" &&
     same_db "$BACKUP/database-before.json" "$BACKUP/database-$label-stopped.json" &&
     preserved_tree "$BACKUP/preserved-$label.private.json" &&
     cmp -s "$BACKUP/preserved-before.private.json" "$BACKUP/preserved-$label.private.json"; then
    systemctl start "$SERVICE"
  else
    printf '%s\n' 'Old service not restarted: inactive state, baseline or preserved configuration could not be confirmed.' >&2
    return 1
  fi
}
on_failure() {
  local result="$1"
  local reason="$2"
  # Do not recurse on cleanup errors or a second signal. Every critical action
  # below is explicitly conditional; failed preservation must leave manual work.
  trap - ERR
  trap '' INT TERM HUP
  set +e
  printf '%s\n' "FAILED ($reason): evidence retained; no database restore will run." >&2
  if [[ "$SWITCHED" == true && "$STARTUP_ATTEMPTED" == true ]]; then
    if systemctl stop "$SERVICE" && service_inactive; then
      printf '%s\n' 'Startup attempted: service stopped; retain current code/schema/evidence for manual recovery. No old-code restart or database restore.' >&2
    else
      printf '%s\n' 'Startup attempted: stop/inactive/MainPID=0 not confirmed; manual recovery required; no files or DB overwritten.' >&2
    fi
  elif [[ "$SWITCHED" == true ]]; then
    if systemctl stop "$SERVICE" && service_inactive; then
      if rsync -a --checksum --delete "$BACKUP/backend.previous/" "$BE/" &&
         rsync -a --checksum --delete "$BACKUP/frontend.previous/" "$FE/" &&
         rsync -a --checksum --delete "$BACKUP/public.previous/" "$PUBLIC/" &&
         cp -a "$BACKUP/REVISION.previous" "$APP/REVISION" &&
         cp -a "$BACKUP/dot-deploy-revision.previous" "$APP/.deploy-revision"; then
        old_restart_if_safe rollback || true
      else
        printf '%s\n' 'Rollback file restoration failed: service remains stopped for manual recovery.' >&2
      fi
    else
      printf '%s\n' 'Rollback not attempted: stop/inactive/MainPID=0 not confirmed; no live files overwritten.' >&2
    fi
    printf '%s\n' 'Manual service/data/rollback review required; changed data/configuration means no automatic restart.' >&2
  elif [[ "$STOPPED" == true ]]; then
    old_restart_if_safe aborted || true
  fi
  exit "$result"
}
trap 'on_failure "$?" ERR' ERR
trap 'on_failure 130 INT' INT
trap 'on_failure 143 TERM' TERM
trap 'on_failure 129 HUP' HUP

if [[ "$PHASE" == backup ]]; then
  revision_is "$OLD"
  [[ "$(file_sha "$BE/PayPlanner.Api.dll")" == "$OLD_API_SHA" ]]
  [[ "$(file_sha "$BE/PayPlanner.Api.Migrations.dll")" == "$OLD_MIGRATIONS_SHA" ]]
  systemctl is-active --quiet "$SERVICE"
  [[ ! -e "$BACKUP" && ! -e "$STAGE" && -f "$ARCHIVE" && ! -L "$ARCHIVE" ]]
  package_check
  space_at_least "$RESERVE"
  mkdir -m 700 "$BACKUP"
  cp -a "$PLAN" "$BACKUP/backup-phase-plan.json"
  cp -a "$SELF" "$DB_PROOF" "$HISTORY_PROOF" "$STOPPED_PROOF" "$HERE/history-manifest.json" "$HERE/history-offsite-verified.json" "$HERE/ci-qualification.json" "$BACKUP/"
  systemctl show "$SERVICE" -p FragmentPath -p DropInPaths -p EnvironmentFiles > "$BACKUP/unit-inputs.private.txt"
  python3 - "$BACKUP/unit-inputs.private.txt" <<'PY'
import sys
s=dict(line.rstrip('\n').split('=',1) for line in open(sys.argv[1],encoding='utf-8'))
assert s['FragmentPath']=='/etc/systemd/system/payplanner.service','Review the actual unit backup path'
assert all(n.startswith('/etc/systemd/system/payplanner.service.d/') for n in s['DropInPaths'].split()),'Review an unlisted drop-in backup path'
assert '/opt/payplanner/shared/payplanner.env' in s['EnvironmentFiles'],'Review the effective production env-file path'
PY
  history_check "$BACKUP/history-active.private.json"
  systemctl stop "$SERVICE"
  STOPPED=true
  service_inactive
  preserved_tree "$BACKUP/preserved-before.private.json"
  db_proof "$BACKUP/database-before.json"
  history_check "$BACKUP/history-stopped.private.json"
  db_bytes="$(sudo -n -u postgres psql -X -q -At -v ON_ERROR_STOP=1 -d payplanner -c "BEGIN READ ONLY; SELECT pg_database_size(current_database()); COMMIT;")"
  [[ "$db_bytes" =~ ^[0-9]+$ ]]
  tree_kib="$(du -sk "$BE" "$FE" "$PUBLIC" "$SHARED" | awk '{n += $1} END {printf "%.0f", n}')"
  [[ "$tree_kib" =~ ^[0-9]+$ ]]
  # Only the fully verified, separately retained off-VPS history is excluded.
  history_kib="$(du -sk "$HISTORY" | awk '{print $1}')"
  [[ "$history_kib" =~ ^[0-9]+$ && "$tree_kib" -ge "$history_kib" ]]
  tree_kib="$((tree_kib - history_kib))"
  unpacked_bytes="$(python3 - "$ARCHIVE" <<'PY'
import sys,tarfile
with tarfile.open(sys.argv[1],'r:gz') as t:print(sum(m.size for m in t.getmembers()))
PY
)"
  [[ "$unpacked_bytes" =~ ^[0-9]+$ ]]
  need="$((RESERVE + db_bytes + tree_kib * 1024 + unpacked_bytes * 2))"
  space_at_least "$need"
  cp -a "$BE" "$BACKUP/backend.previous"
  cp -a "$FE" "$BACKUP/frontend.previous"
  cp -a "$PUBLIC" "$BACKUP/public.previous"
  cp -a "$APP/REVISION" "$BACKUP/REVISION.previous"
  cp -a "$APP/.deploy-revision" "$BACKUP/dot-deploy-revision.previous"
  tar --acls --xattrs --anchored --no-wildcards --exclude=opt/payplanner/shared/backups -cpf "$BACKUP/preserved-instance.tar" -C / opt/payplanner/shared
  [[ ! -e /etc/systemd/system/payplanner.service ]] || cp -a /etc/systemd/system/payplanner.service "$BACKUP/systemd-unit.previous"
  [[ ! -e /etc/systemd/system/payplanner.service.d ]] || cp -a /etc/systemd/system/payplanner.service.d "$BACKUP/systemd-drop-in.previous"
  sudo -n -u postgres pg_dump --format=custom --no-owner --no-acl payplanner > "$BACKUP/database.dump"
  pg_restore --list "$BACKUP/database.dump" > "$BACKUP/database.dump.list"
  [[ -s "$BACKUP/database.dump" && -s "$BACKUP/database.dump.list" ]]
  db_proof "$BACKUP/database-after-dump.json"
  same_db "$BACKUP/database-before.json" "$BACKUP/database-after-dump.json"
  preserved_tree "$BACKUP/preserved-after-dump.private.json"
  cmp -s "$BACKUP/preserved-before.private.json" "$BACKUP/preserved-after-dump.private.json"
  history_check "$BACKUP/history-after-dump.private.json"
  (cd "$BACKUP" && find . -type f ! -name BACKUP-SHA256SUMS -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > BACKUP-SHA256SUMS)
  space_at_least "$RESERVE"
  trap - ERR INT TERM HUP
  printf '%s\n' 'BACKUP_READY_SERVICE_STOPPED: verify the complete backup off VPS before authorising install.'
  exit 0
fi

[[ -d "$BACKUP" && ! -L "$BACKUP" ]]
service_inactive
STOPPED=true
revision_is "$OLD"
[[ "$(file_sha "$BE/PayPlanner.Api.dll")" == "$OLD_API_SHA" ]]
[[ "$(file_sha "$BE/PayPlanner.Api.Migrations.dll")" == "$OLD_MIGRATIONS_SHA" ]]
python3 - "$PLAN" "$BACKUP/backup-phase-plan.json" <<'PY'
import json,sys
a,b=(json.load(open(n,encoding='utf-8')) for n in sys.argv[1:])
for p in [a,b]:
    for n in ['phase','offsiteReceipt']:p.pop(n)
assert a==b,'Only phase/offsite receipt may change after the stopped backup'
PY
(cd "$BACKUP" && sha256sum --check --strict --quiet BACKUP-SHA256SUMS)
db_proof "$BACKUP/database-before-switch.json"
same_db "$BACKUP/database-before.json" "$BACKUP/database-before-switch.json"
preserved_tree "$BACKUP/preserved-before-switch.private.json"
cmp -s "$BACKUP/preserved-before.private.json" "$BACKUP/preserved-before-switch.private.json"
if [[ "$PHASE" == abort ]]; then
  old_restart_if_safe explicit-abort
  STOPPED=false
  trap - ERR INT TERM HUP
  printf '%s\n' 'ABORTED_BEFORE_SWITCH: original revision restarted; backup retained.'
  exit 0
fi
python3 - "$PLAN" "$HERE" "$BACKUP" <<'PY'
import hashlib,json,pathlib,re,sys
c=json.load(open(sys.argv[1],encoding='utf-8'));here=pathlib.Path(sys.argv[2]);backup=pathlib.Path(sys.argv[3])
r=json.load(open(here/c['offsiteReceipt']['serverRelativePath'],encoding='utf-8'))
with (backup/'BACKUP-SHA256SUMS').open('rb') as f:h=hashlib.file_digest(f,'sha256').hexdigest()
assert r['rootAccepted'] is True and r['verifiedOutsideVps'] is True and r['releaseSha']==c['releaseSha']
assert r['backupServerPath']==str(backup) and r['backupManifestSha256']==h
assert r['allListedFilesCopied'] is True and r['allCopiedHashesMatched'] is True
assert r['offsitePgRestoreListExitCode']==0 and re.fullmatch('[a-f0-9]{64}',r['offsitePgRestoreListSha256'])
assert r['retainedForRecovery'] is True and r['localEvidenceIsCI'] is False
history=json.load(open(here/c['offsiteHistoryReceipt']['serverRelativePath'],encoding='utf-8'))
assert r['historyVerifiedOutsideVps'] is True
assert r['historyManifestSha256']==c['historyManifest']['sha256']==history['currentManifestSha256']
assert r['historyReceiptSchemaVersion']==2
assert r['offsiteHistoryReceiptSha256']==c['offsiteHistoryReceipt']['sha256']
PY
package_check
space_at_least "$RESERVE"
[[ ! -e "$STAGE" ]]
mkdir -m 700 -p "$(dirname "$STAGE")"
mkdir -m 700 "$STAGE"
tar --extract --gzip --file "$ARCHIVE" --directory "$STAGE" --no-same-owner --no-same-permissions
(cd "$STAGE" && sha256sum --check --strict --quiet SHA256SUMS)
history_check "$BACKUP/history-before-live-switch.private.json"
SWITCHED=true
rsync -a --checksum --delete "$STAGE/be/" "$BE/"
rsync -a --checksum --delete "$STAGE/fe/" "$FE/"
rsync -a --checksum --delete "$STAGE/fe/" "$PUBLIC/"
chown -R payplanner:payplanner "$BE" "$FE"
find "$BE" "$FE" -type d -exec chmod 750 {} +
find "$BE" "$FE" -type f -exec chmod 640 {} +
chown -R www-data:www-data "$PUBLIC"
find "$PUBLIC" -type d -exec chmod 755 {} +
find "$PUBLIC" -type f -exec chmod 644 {} +
# Instance settings keep their original bytes AND ownership/mode.
for name in appsettings.json appsettings.Production.json; do
  if [[ -f "$BACKUP/backend.previous/$name" ]]; then cp -a "$BACKUP/backend.previous/$name" "$BE/$name"; fi
done
printf '%s\n' "$REV" > "$APP/REVISION"
printf '%s\n' "$REV" > "$APP/.deploy-revision"
chown payplanner:payplanner "$APP/REVISION" "$APP/.deploy-revision"
chmod 640 "$APP/REVISION" "$APP/.deploy-revision"
installed_check
preserved_tree "$BACKUP/preserved-installed-stopped.private.json"
cmp -s "$BACKUP/preserved-before.private.json" "$BACKUP/preserved-installed-stopped.private.json"
db_proof "$BACKUP/database-installed-stopped.json"
same_db "$BACKUP/database-before.json" "$BACKUP/database-installed-stopped.json"
START_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
STARTUP_ATTEMPTED=true
systemctl start "$SERVICE"
STOPPED=false
healthy=false
for attempt in $(seq 1 60); do
  if curl -fsS --max-time 5 http://127.0.0.1:5080/api/health > "$BACKUP/health-local.private.json"; then
    if python3 - "$BACKUP/health-local.private.json" <<'PY'
import json,sys
assert json.load(open(sys.argv[1],encoding='utf-8'))['status']=='ok'
PY
    then healthy=true; break; fi
  fi
  sleep 2
done
[[ "$healthy" == true ]]
db_proof "$BACKUP/database-started.json"
same_db "$BACKUP/database-before.json" "$BACKUP/database-started.json" startup
preserved_tree "$BACKUP/preserved-started.private.json"
cmp -s "$BACKUP/preserved-before.private.json" "$BACKUP/preserved-started.private.json"
installed_check
revision_is "$REV"
systemctl show "$SERVICE" -p ActiveState -p SubState -p ExecMainStatus -p NRestarts -p MainPID > "$BACKUP/service.private.txt"
python3 - "$BACKUP/service.private.txt" "$BE/PayPlanner.Api.dll" <<'PY'
import pathlib,sys
s=dict(line.rstrip('\n').split('=',1) for line in open(sys.argv[1],encoding='utf-8'))
assert (s['ActiveState'],s['SubState'],s['ExecMainStatus'],s['NRestarts'])==('active','running','0','0')
assert sys.argv[2].encode() in (pathlib.Path('/proc')/s['MainPID']/'cmdline').read_bytes()
PY
public_check
journalctl -u "$SERVICE" --since "$START_UTC" --no-pager -o short-iso > "$BACKUP/startup-journal.private.log"
if grep -Eiq 'fail:|crit:|Unhandled exception' "$BACKUP/startup-journal.private.log"; then
  printf '%s\n' 'Startup error detected: halt for review; no database restore.' >&2
  false
fi
space_at_least "$RESERVE"
trap - ERR INT TERM HUP
printf '%s\n' 'INSTALLED_LOCAL_CHECKS_PASSED: existing public/assets/readiness and private startup review still required.'
printf '%s\n' 'Do not claim complete production delivery until those checks and offsite evidence are qualified.'
