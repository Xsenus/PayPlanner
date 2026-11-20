import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, lstatSync, mkdtempSync, openSync, readFileSync, rmSync, statfsSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createPlan, fileHash, qualifiedContext } from './prepare-deployment-plan.mjs';
import { contextFromEnvironment, validateInspection, windowsAdapter } from './xsenus-artifact-store.mjs';

export const linuxFiles = Object.freeze([
  'prune-deploy-backups.sh', 'prune-deploy-backups.test.sh',
  'preserve-data-protection-keys.sh', 'preserve-data-protection-keys.test.sh',
  'deploy-history-archive.py', 'deploy-history-archive.test.py',
  'deploy-db-proof.py', 'deploy-db-proof.test.py', 'deployment-baseline.json', 'deployment-baseline-current.json',
  'deployment-baseline-act-annual.json',
  'deploy-payment-attempt-delta.json', 'deploy-payment-history-delta.json',
  'deploy-roadmap-candidate-review.json', 'deploy-roadmap-candidate-delta.json',
  'deploy-act-annual-review.json', 'deploy-act-annual-delta.json',
  'deploy-stopped-archive.py', 'deploy-stopped-archive.test.py',
  'deploy-history-source-proof.py', 'deploy-stopped-source-proof.py',
  'deploy-vps.sh', 'deploy-vps-admission.test.py',
]);
const sensitiveNames = ['VPS_SSH_KEY', 'PAYPLANNER_WEBPUSH_PUBLIC_KEY', 'PAYPLANNER_WEBPUSH_PRIVATE_KEY', 'PAYPLANNER_WEBPUSH_SUBJECT'];
const hash = value => createHash('sha256').update(value).digest('hex');
export const shellQuote = value => "'" + String(value).replaceAll("'", "'\\''") + "'";

export function redact(text, values) {
  const fragments = values.filter(Boolean).flatMap(value => [value, ...value.split(/\r?\n/).filter(line => line.length >= 12)]);
  return [...new Set(fragments)].sort((a, b) => b.length - a.length)
    .reduce((result, value) => result.replaceAll(value, '[redacted]'), String(text));
}

export function configuration(env = process.env, root = process.cwd()) {
  assert.equal(env.VPS_HOST, '185.46.10.202', 'Only the existing PayPlanner VPS is supported');
  assert.equal(env.VPS_USER, 'root', 'Use the existing deployment account');
  assert.equal(env.VPS_PORT, '22', 'Use the existing SSH port');
  assert.match(env.DEPLOY_SHA ?? '', /^[a-f0-9]{40}$/, 'A complete qualified revision is required');
  assert.equal(env.DEPLOY_ARCHIVE_NAME, `payplanner_${env.DEPLOY_SHA}.tar.gz`, 'Archive must belong to the qualified revision');
  assert.match(env.VPS_SSH_HOST_FINGERPRINT ?? '', /^SHA256:[A-Za-z0-9+/]{43}$/, 'A trusted SHA256 SSH host fingerprint is required');
  assert.ok(typeof env.VPS_SSH_KEY === 'string' && /-----BEGIN (?:OPENSSH|RSA|EC|PRIVATE) (?:PRIVATE )?KEY-----/.test(env.VPS_SSH_KEY), 'An unencrypted deployment SSH private key is required');
  assert.equal(env.PUBLIC_API_HEALTH_URL, 'https://api.elitpravo.com/api/health');
  assert.equal(env.PUBLIC_FRONTEND_URL, 'https://crm.elitpravo.com/');
  const remoteEnv = Object.fromEntries(['DEPLOY_SHA', 'DEPLOY_ARCHIVE_NAME', 'PUBLIC_API_HEALTH_URL', 'PUBLIC_FRONTEND_URL',
    'PAYPLANNER_WEBPUSH_ENABLED', 'PAYPLANNER_WEBPUSH_PUBLIC_KEY', 'PAYPLANNER_WEBPUSH_PRIVATE_KEY', 'PAYPLANNER_WEBPUSH_SUBJECT']
    .map(name => [name, env[name] ?? (name === 'PAYPLANNER_WEBPUSH_ENABLED' ? 'false' : '')]));
  assert.ok(Object.values(remoteEnv).every(value => !/[\0\r\n]/.test(value)), 'Remote environment values must be single-line text');
  const childEnv = Object.fromEntries(Object.entries(env).filter(([name]) => !sensitiveNames.some(secret => secret.toLowerCase() === name.toLowerCase())));
  return { root: resolve(root), host: env.VPS_HOST, user: env.VPS_USER, port: env.VPS_PORT, sha: env.DEPLOY_SHA,
    archive: resolve(root, env.DEPLOY_ARCHIVE_NAME), fingerprint: env.VPS_SSH_HOST_FINGERPRINT,
    key: env.VPS_SSH_KEY.replaceAll('\r\n', '\n').trim() + '\n', remoteEnv, childEnv,
    secrets: sensitiveNames.map(name => env[name] ?? ''), temporaryRoot: resolve(env.RUNNER_TEMP || tmpdir()),
    keyscanExecutable: 'C:/Program Files/Git/usr/bin/ssh-keyscan.exe',
    systemRoot: env.SystemRoot || env.WINDIR || 'C:/Windows' };
}

export function trustedHostLine(scan, config) {
  for (const line of scan.split(/\r?\n/)) {
    const parts = line.trim().split(/\s+/);
    if (parts.length !== 3 || !['ssh-ed25519', 'ssh-rsa'].includes(parts[1]) || !/^[A-Za-z0-9+/]+={0,2}$/.test(parts[2])) continue;
    const blob = Buffer.from(parts[2], 'base64');
    const fingerprint = 'SHA256:' + createHash('sha256').update(blob).digest('base64').replace(/=+$/, '');
    if (fingerprint === config.fingerprint) return `payplanner-vps ${parts[1]} ${parts[2]}\n`;
  }
  throw new Error('SSH host fingerprint mismatch; no authentication or upload attempted');
}

export function nativeRun(executable, args, options = {}) {
  return execFileSync(executable, args, { encoding: 'utf8', windowsHide: true, maxBuffer: 16 * 1024 * 1024,
    timeout: 180000, stdio: ['pipe', 'pipe', 'pipe'], ...options });
}

// stdout goes straight to a newly created file. Large backups never accumulate
// in Node memory and native failures never become a successful copy receipt.
export async function nativeStream(executable, args, destination, options = {}) {
  const fd = openSync(destination, 'wx');
  let child;
  try {
    await new Promise((accept, reject) => {
      child = spawn(executable, args, { env: options.env, windowsHide: true, stdio: ['ignore', fd, 'pipe'] });
      let errorText = '', timedOut = false;
      const timer = setTimeout(() => { timedOut = true; child.kill(); }, options.timeout ?? 1200000);
      child.stderr.on('data', bytes => { errorText = (errorText + bytes.toString('utf8')).slice(-65536); });
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.once('close', code => {
        clearTimeout(timer);
        if (code === 0 && !timedOut) accept();
        else reject(Object.assign(new Error(timedOut ? 'Native backup stream timed out' : 'Native backup stream failed'), { status: code, stderr: errorText }));
      });
    });
    fsyncSync(fd);
  } finally { closeSync(fd); }
}

function ancestors(path) {
  const paths = [];
  for (let current = resolve(path);; current = dirname(current)) {
    paths.push(current);
    if (dirname(current) === current) return paths;
  }
}

export function prepareDelivery(config) {
  const operatorPath = 'D:/PayPlannerRunner/deployment-history-current.json';
  const adapter = windowsAdapter(config.childEnv);
  const settingsPaths = ancestors(operatorPath);
  validatePrivateRecovery(adapter.inspect(settingsPaths), settingsPaths, dirname(resolve(operatorPath)));
  const operator = JSON.parse(readFileSync(operatorPath, 'utf8'));
  assert.equal(resolve(operator.offsiteRoot), resolve('D:/PayPlannerOffsiteBackups'));
  const history = JSON.parse(readFileSync(operator.historyReceiptPath, 'utf8'));
  const paths = [...new Set([operator.offsiteRoot, operator.historyReceiptPath, operator.historyManifestPath,
    ...history.archiveReferences.map(row => row.path)].flatMap(path => {
    const suffix = relative(resolve(operator.offsiteRoot), resolve(path));
    assert.ok(!isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..\\'), 'Recovery evidence escapes its private root');
    return ancestors(path);
  }))];
  validatePrivateRecovery(adapter.inspect(paths), paths, resolve(operator.offsiteRoot));
  return { ...createPlan({ root: config.root, archive: config.archive, revision: config.sha, operator, env: config.childEnv }),
    offsiteRoot: resolve(operator.offsiteRoot) };
}

export function validatePrivateRecovery(report, paths, root) {
  validateInspection(report, paths, root);
  const allowed = new Set([report.sid, 'S-1-5-18', 'S-1-5-32-544']);
  for (const item of report.items) {
    const suffix = relative(resolve(root), resolve(item.path));
    if (isAbsolute(suffix) || suffix === '..' || suffix.startsWith('..\\')) continue;
    for (const rule of item.rules) {
      assert.ok(!rule.allow || rule.inheritOnly || allowed.has(rule.sid) || rule.rights === 0,
        'Recovery data must not grant access to another Windows account');
    }
  }
}

export function haltCommand(record) {
  for (const key of ['releaseSha', 'previousRevision']) assert.match(record[key] ?? '', /^[a-f0-9]{40}$/);
  for (const key of ['expectedApiSha256', 'expectedPreviousApiSha256']) assert.match(record[key] ?? '', /^[a-f0-9]{64}$/);
  return `set -euo pipefail
# HALT_AFTER_READINESS_FAILURE: stop only this deployment; never restore a database.
exec 9>/run/lock/payplanner-manual-deploy.lock
flock -n 9
revision_is() {
  for p in /opt/payplanner/app/REVISION /opt/payplanner/app/.deploy-revision /opt/payplanner/app/frontend/deploy-revision.txt /var/www/crm.elitpravo.com/deploy-revision.txt; do
    [[ -f "$p" && ! -L "$p" && "$(tr -d '\\r\\n' < "$p")" == "$1" ]] || return 1
  done
}
actual="$(sha256sum /opt/payplanner/app/backend/PayPlanner.Api.dll | cut -d ' ' -f 1)"
if revision_is ${shellQuote(record.releaseSha)} && [[ "$actual" == ${shellQuote(record.expectedApiSha256)} ]]; then
  systemctl stop payplanner.service
  [[ "$(systemctl show payplanner.service -p ActiveState --value)" == inactive ]]
  [[ "$(systemctl show payplanner.service -p MainPID --value)" == 0 ]]
elif revision_is ${shellQuote(record.previousRevision)} && [[ "$actual" == ${shellQuote(record.expectedPreviousApiSha256)} ]]; then
  printf '%s\\n' 'Previous revision remains; no backward restart or restore attempted.'
else
  printf '%s\\n' 'Unexpected deployment identity: no unrelated service mutation; manual review required.' >&2
  exit 67
fi
`;
}

export function createPrivateDirectory(config, run = nativeRun) {
  const directory = mkdtempSync(join(config.temporaryRoot, 'payplanner-deploy-'));
  try {
    const identity = run(join(config.systemRoot, 'System32/whoami.exe'), ['/user', '/fo', 'csv', '/nh'], { env: config.childEnv });
    const sid = identity.match(/S-\d-\d+(?:-\d+)+/g)?.at(-1);
    assert.ok(sid, 'Cannot identify the current Windows account');
    run(join(config.systemRoot, 'System32/icacls.exe'), [directory, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F'], { env: config.childEnv });
    return directory;
  } catch (error) {
    rmSync(directory, { recursive: true, force: true });
    throw error;
  }
}

export function connectionArguments(config, directory, copy = false) {
  const knownHosts = join(directory, 'known_hosts').replaceAll('\\', '/');
  return ['-F', 'NUL', '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'PasswordAuthentication=no',
    '-o', 'KbdInteractiveAuthentication=no', '-o', 'StrictHostKeyChecking=yes', '-o', 'HostKeyAlias=payplanner-vps',
    '-o', `UserKnownHostsFile="${knownHosts}"`, '-o', 'GlobalKnownHostsFile=NUL', '-o', 'ConnectTimeout=30',
    '-o', 'LogLevel=ERROR', '-i', join(directory, 'identity'), copy ? '-P' : '-p', config.port];
}

async function withConnection(config, callback, { run = nativeRun, workspace = createPrivateDirectory, log = console.log, platform = process.platform } = {}) {
  assert.equal(platform, 'win32', 'This bridge requires the existing Windows runner');
  const openssh = join(config.systemRoot, 'System32/OpenSSH');
  const tools = Object.fromEntries(['ssh', 'scp'].map(name => [name, join(openssh, name + '.exe')]));
  // Windows 9.5 keyscan cannot negotiate this VPS's preferred hybrid KEX.
  // Git's existing scanner supports it. Keep Windows ssh/scp and their NUL
  // config semantics; the exact pinned host key remains mandatory for both.
  tools['ssh-keyscan'] = config.keyscanExecutable;
  for (const path of Object.values(tools)) assert.ok(existsSync(path), 'The existing Windows OpenSSH client is required: ' + basename(path));
  const execute = (tool, args, options = {}) => {
    try {
      const output = run(tools[tool], args, { env: config.childEnv, ...options });
      if (options.echo !== false && output?.trim()) log(redact(output, config.secrets));
      return output ?? '';
    } catch (error) {
      throw new Error(redact(`${tool} failed (native exit ${error.status ?? 'unavailable'}): ${error.stderr ?? error.message}`, config.secrets));
    }
  };
  let directory;
  let ownsDirectory = false;
  try {
    const knownHosts = trustedHostLine(execute('ssh-keyscan', ['-T', '15', '-p', config.port, '-t', 'ed25519,rsa', config.host], { echo: false }), config);
    directory = workspace(config, run);
    assert.ok(resolve(directory).startsWith(config.temporaryRoot + '\\') && basename(directory).startsWith('payplanner-deploy-'), 'Private workspace must belong to this invocation');
    ownsDirectory = true;
    writeFileSync(join(directory, 'known_hosts'), knownHosts, { flag: 'wx' });
    writeFileSync(join(directory, 'identity'), config.key, { flag: 'wx' });
    return await callback({ directory, execute, tools, destination: `${config.user}@${config.host}`,
      sshArgs: connectionArguments(config, directory), scpArgs: connectionArguments(config, directory, true) });
  } finally {
    if (ownsDirectory) rmSync(directory, { recursive: true, force: true });
  }
}

export async function haltDeployment(config, options = {}) {
  const record = JSON.parse(readFileSync(join(config.root, 'out/deployment-installed.json'), 'utf8'));
  const context = qualifiedContext(config.childEnv, config.sha, options.platform ?? process.platform);
  assert.equal(record.releaseSha, context.releaseSha);
  assert.equal(record.runId, context.runId);
  assert.equal(record.runAttempt, context.runAttempt);
  assert.equal(record.expectedApiSha256, fileHash(join(config.root, 'out/be/PayPlanner.Api.dll')));
  await withConnection(config, ({ execute, sshArgs, destination }) => {
    execute('ssh', [...sshArgs, destination, 'bash -s'], { input: haltCommand(record) });
  }, options);
}

export async function checkDeploymentAccess(config, options = {}) {
  const context = contextFromEnvironment(config.childEnv, options.platform ?? process.platform);
  assert.equal(context.producerJob, 'frontend', 'Read-only access is checked before release gates');
  assert.equal(context.headSha, config.sha);
  assert.equal(context.workflowSha, config.sha);
  await withConnection(config, ({ execute, sshArgs, destination }) => {
    // No archive transfer, backup, application inspection, restart or DB access.
    execute('ssh', [...sshArgs, destination, 'true'], { timeout: 60000 });
  }, options);
}

export async function deploy(config, options = {}) {
  const { run = nativeRun, stream = nativeStream, prepare = prepareDelivery,
    recoveryWorkspace = createPrivateDirectory, log = console.log } = options;
  const sourceFiles = [...new Set([...linuxFiles, 'deploy-vps.sh', 'windows-vps-deploy.mjs',
    'prepare-deployment-plan.mjs', 'prepare-deployment-package.mjs'].map(name => join(config.root, 'scripts', name)))];
  for (const path of [...sourceFiles, config.archive]) assert.ok(lstatSync(path).isFile() && !lstatSync(path).isSymbolicLink(), 'Deployment inputs must be ordinary files');
  assert.ok(lstatSync(config.archive).size > 0, 'The qualified archive is empty');
  const content = new Map(sourceFiles.map(path => [path, readFileSync(path)]));
  for (const path of sourceFiles.filter(path => path.endsWith('.sh'))) assert.ok(!content.get(path).includes(13), 'Deployment shell scripts require LF line endings');
  const stableSources = () => {
    for (const path of sourceFiles) assert.equal(fileHash(path), hash(content.get(path)), 'A qualified deployment input changed');
  };
  return withConnection(config, async ({ directory, execute, tools, destination, sshArgs, scpArgs }) => {
    let preflight, control, evidence, prepared;
    let backupAttempted = false, installAttempted = false;
    const uploadPlan = (phase, receipt = null) => {
      const plan = { ...prepared.plan, phase, offsiteReceipt: receipt };
      const raw = Buffer.from(JSON.stringify(plan, null, 2) + '\n');
      writeFileSync(join(directory, 'installer-plan.json'), raw);
      writeFileSync(join(evidence, `installer-plan.${phase}.json`), raw, { flag: 'wx' });
      execute('scp', [...scpArgs, join(directory, 'installer-plan.json'), `${destination}:${control}/`]);
      return hash(raw);
    };
    const phase = (name, receipt = null) => {
      const digest = uploadPlan(name, receipt);
      execute('ssh', [...sshArgs, destination,
        `bash ${shellQuote(control + '/deploy-vps.sh')} --execute ${name} ${digest}`], { timeout: 1200000 });
    };
    try {
      preflight = execute('ssh', [...sshArgs, destination, `umask 077; mktemp -d /tmp/payplanner-preflight-${config.sha}-XXXXXXXX`], { echo: false }).trim();
      assert.match(preflight, new RegExp(`^/tmp/payplanner-preflight-${config.sha}-[A-Za-z0-9]{8}$`));
      execute('scp', [...scpArgs, ...linuxFiles.map(name => join(config.root, 'scripts', name)), `${destination}:${preflight}/`]);
      const checksums = linuxFiles.map(name => `${hash(content.get(join(config.root, 'scripts', name)))}  ${name}`).join('\n') + '\n';
      const checks = `set -euo pipefail\ntest "$(uname -s)" = Linux\ncd ${shellQuote(preflight)}\nmkdir -m 700 fixtures\nexport TMPDIR="$PWD/fixtures"\nprintf %s ${shellQuote(checksums)} | sha256sum -c -\nbash prune-deploy-backups.test.sh\nbash preserve-data-protection-keys.test.sh\npython3 -B deploy-history-archive.test.py\npython3 -B deploy-db-proof.test.py\npython3 -B deploy-stopped-archive.test.py\nbash -n deploy-vps.sh\npython3 -B deploy-vps-admission.test.py\n`;
      execute('ssh', [...sshArgs, destination, 'bash -s'], { input: checks, timeout: 300000 });
      execute('ssh', [...sshArgs, destination, `rm -rf -- ${shellQuote(preflight)}`]);
      preflight = undefined;
      stableSources();
      prepared = prepare(config);
      const recoveryConfig = { ...config, temporaryRoot: prepared.offsiteRoot };
      evidence = recoveryWorkspace(recoveryConfig, run);
      assert.ok(resolve(evidence).startsWith(resolve(prepared.offsiteRoot) + '\\') && basename(evidence).startsWith('payplanner-deploy-'), 'Durable evidence must remain under its private recovery root');
      writeFileSync(join(evidence, 'source-pins.json'), JSON.stringify(Object.fromEntries(sourceFiles.map(path => [basename(path), hash(content.get(path))])), null, 2), { flag: 'wx' });
      log('Private recovery evidence: ' + evidence);
      control = execute('ssh', [...sshArgs, destination, `umask 077; mktemp -d /tmp/payplanner-control-${config.sha}-XXXXXXXX`], { echo: false }).trim();
      assert.match(control, new RegExp(`^/tmp/payplanner-control-${config.sha}-[A-Za-z0-9]{8}$`));
      const controlFiles = ['deploy-vps.sh', 'deploy-db-proof.py', 'deploy-history-source-proof.py', 'deploy-stopped-source-proof.py',
        'deploy-roadmap-candidate-review.json', 'deploy-roadmap-candidate-delta.json',
        'deploy-act-annual-review.json', 'deploy-act-annual-delta.json'];
      for (const name of controlFiles) writeFileSync(join(directory, name), content.get(join(config.root, 'scripts', name)), { flag: 'wx' });
      for (const [name, bytes] of [['ci-qualification.json', prepared.qualificationRaw], ['history-manifest.json', prepared.historyManifestRaw], ['history-offsite-verified.json', prepared.historyRaw]]) {
        writeFileSync(join(directory, name), bytes, { flag: 'wx' });
        writeFileSync(join(evidence, name), bytes, { flag: 'wx' });
        controlFiles.push(name);
      }
      execute('scp', [...scpArgs, ...controlFiles.map(name => join(directory, name)), `${destination}:${control}/`]);
      execute('scp', [...scpArgs, config.archive, `${destination}:/tmp/`], { timeout: 900000 });
      stableSources();
      backupAttempted = true;
      phase('backup');
      const backup = `/opt/payplanner/deploy-backups/manual-${config.sha}-${prepared.plan.backupUtc}`;
      const inventory = suffix => {
        const bytes = execute('ssh', [...sshArgs, destination, `python3 -B ${shellQuote(control + '/deploy-stopped-source-proof.py')} ${shellQuote(backup)}`], { echo: false, timeout: 300000 });
        writeFileSync(join(evidence, `stopped-${suffix}.json`), bytes, { flag: 'wx' });
      };
      inventory('before');
      const backupEntries = JSON.parse(readFileSync(join(evidence, 'stopped-before.json'), 'utf8')).entries;
      const requiredBytes = backupEntries.reduce((total, row) => total + BigInt(row.size ?? 0), 0n)
        + BigInt(backupEntries.find(row => row.path === 'database.dump')?.size ?? 0)
        + BigInt(backupEntries.length) * 4096n + 3n * 1024n ** 3n;
      const disk = statfsSync(evidence, { bigint: true });
      assert.ok(disk.bavail * disk.bsize > requiredBytes, 'Insufficient offsite disk reserve; abort before copying');
      try {
        await stream(tools.ssh, [...sshArgs, destination,
          `tar --format=pax --acls --xattrs --xattrs-include='*' --numeric-owner --hard-dereference -cpf - -C /opt/payplanner/deploy-backups ${shellQuote(basename(backup))}`],
        join(evidence, 'stopped.tar'), { env: config.childEnv, timeout: 1200000 });
      } catch (error) {
        throw new Error(redact(`Offsite stream failed (native exit ${error.status ?? 'unavailable'}): ${error.stderr ?? error.message}`, config.secrets));
      }
      inventory('after');
      const verification = run('python', ['-B', join(config.root, 'scripts/deploy-stopped-archive.py'),
        join(evidence, 'stopped-before.json'), join(evidence, 'stopped-after.json'), join(evidence, 'stopped.tar'),
        join(evidence, 'database.dump'), 'C:/Program Files/PostgreSQL/17/bin/pg_restore.exe', join(evidence, 'stopped-verified.json')],
      { env: config.childEnv, timeout: 1200000 });
      if (verification?.trim()) log(redact(verification, config.secrets));
      const verified = JSON.parse(readFileSync(join(evidence, 'stopped-verified.json'), 'utf8'));
      assert.ok(verified.schemaVersion === 1 && verified.rootAccepted === false && verified.verifiedOutsideVps === false);
      assert.equal(verified.backupServerPath, backup);
      assert.equal(verified.backupManifestSha256, backupEntries.find(row => row.path === 'BACKUP-SHA256SUMS')?.sha256);
      assert.ok(['allListedFilesCopied', 'allCopiedHashesMatched', 'allEntryMetadataMatched', 'sourceBeforeAfterExact'].every(name => verified[name] === true));
      assert.equal(verified.offsitePgRestoreListExitCode, 0);
      assert.equal(verified.offsitePgRestoreListSha256, fileHash(join(evidence, 'database.list')));
      assert.equal(verified.archive.bytes, lstatSync(join(evidence, 'stopped.tar')).size);
      const receipt = { ...verified, rootAccepted: true, verifiedOutsideVps: true,
        releaseSha: config.sha, historyVerifiedOutsideVps: true, historyReceiptSchemaVersion: 2,
        historyManifestSha256: prepared.plan.historyManifest.sha256,
        offsiteHistoryReceiptSha256: prepared.plan.offsiteHistoryReceipt.sha256 };
      const receiptRaw = Buffer.from(JSON.stringify(receipt, null, 2) + '\n');
      writeFileSync(join(evidence, 'offsite-verified.json'), receiptRaw, { flag: 'wx' });
      execute('scp', [...scpArgs, join(evidence, 'offsite-verified.json'), `${destination}:${control}/`]);
      stableSources();
      installAttempted = true;
      phase('install', { serverRelativePath: 'offsite-verified.json', sha256: hash(receiptRaw) });
      const record = { ...prepared.plan, runId: JSON.parse(prepared.qualificationRaw).runId,
        runAttempt: JSON.parse(prepared.qualificationRaw).runAttempt, evidence };
      writeFileSync(join(config.root, 'out/deployment-installed.json'), JSON.stringify(record, null, 2), { flag: 'wx' });
      writeFileSync(join(evidence, 'deployment-installed.json'), JSON.stringify(record, null, 2), { flag: 'wx' });
      execute('ssh', [...sshArgs, destination, `rm -rf -- ${shellQuote(control)}`]);
      control = undefined;
      log('Stopped offsite backup, native dump listing and installation passed; public readiness remains mandatory.');
    } catch (error) {
      try {
        if (evidence) writeFileSync(join(evidence, 'delivery-failure.json'), JSON.stringify({
          releaseSha: config.sha, backupAttempted, installAttempted, remoteControlDirectory: control,
          error: redact(error.message, config.secrets), automaticDatabaseRestore: false,
        }, null, 2), { flag: 'wx' });
      } catch (receiptError) {
        log(redact('Failure receipt could not be saved: ' + receiptError.message, config.secrets));
      }
      if (installAttempted) {
        try { execute('ssh', [...sshArgs, destination, 'bash -s'], { input: haltCommand(prepared.plan) }); }
        catch (haltError) { log(redact('HALT could not be confirmed: ' + haltError.message, config.secrets)); }
      } else if (backupAttempted) {
        try { phase('abort'); }
        catch (abortError) { log(redact('Safe pre-startup abort could not be confirmed: ' + abortError.message, config.secrets)); }
      }
      throw error;
    } finally {
      // Durable recovery archives and failed control directories are retained.
      if (preflight && /^\/tmp\/payplanner-preflight-[a-f0-9]{40}-[A-Za-z0-9]{8}$/.test(preflight)) {
        execute('ssh', [...sshArgs, destination, `rm -rf -- ${shellQuote(preflight)}`]);
      }
    }
  }, options);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  let config;
  try {
    assert.ok(process.argv.length === 2 || (process.argv.length === 3 && ['--halt', '--check-access'].includes(process.argv[2])), 'Only structured deployment, access preflight or guarded halt is supported');
    config = configuration();
    if (process.argv[2] === '--halt') await haltDeployment(config);
    else if (process.argv[2] === '--check-access') await checkDeploymentAccess(config);
    else await deploy(config);
  } catch (error) {
    console.error(redact(error.message, config?.secrets ?? sensitiveNames.map(name => process.env[name] ?? '')));
    process.exitCode = 1;
  }
}
