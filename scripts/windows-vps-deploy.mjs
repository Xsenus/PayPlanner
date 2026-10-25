import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const linuxFiles = Object.freeze([
  'prune-deploy-backups.sh', 'prune-deploy-backups.test.sh',
  'preserve-data-protection-keys.sh', 'preserve-data-protection-keys.test.sh',
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

export async function deploy(config, { run = nativeRun, workspace = createPrivateDirectory, log = console.log, platform = process.platform } = {}) {
  assert.equal(platform, 'win32', 'This bridge requires the existing Windows runner');
  const openssh = join(config.systemRoot, 'System32/OpenSSH');
  const tools = Object.fromEntries(['ssh', 'scp', 'ssh-keyscan'].map(name => [name, join(openssh, name + '.exe')]));
  for (const path of Object.values(tools)) assert.ok(existsSync(path), 'The existing Windows OpenSSH client is required: ' + basename(path));
  const sourceFiles = [...linuxFiles.map(name => join(config.root, 'scripts', name)), join(config.root, 'scripts/deploy-vps.sh'), config.archive];
  for (const path of sourceFiles) assert.ok(lstatSync(path).isFile() && !lstatSync(path).isSymbolicLink(), 'Deployment inputs must be ordinary files');
  assert.ok(lstatSync(config.archive).size > 0, 'The qualified archive is empty');
  const content = new Map(sourceFiles.map(path => [path, readFileSync(path)]));
  for (const path of sourceFiles.filter(path => path.endsWith('.sh'))) assert.ok(!content.get(path).includes(13), 'Deployment shell scripts require LF line endings');
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
  let remote;
  let sshArgs;
  const destination = `${config.user}@${config.host}`;
  try {
    // Verify the public host key before materializing any private credential.
    const knownHosts = trustedHostLine(execute('ssh-keyscan', ['-T', '15', '-p', config.port, '-t', 'ed25519,rsa', config.host], { echo: false }), config);
    directory = workspace(config, run);
    assert.ok(resolve(directory).startsWith(config.temporaryRoot + '\\') && basename(directory).startsWith('payplanner-deploy-'), 'Private workspace must belong to this invocation');
    ownsDirectory = true;
    writeFileSync(join(directory, 'known_hosts'), knownHosts, { flag: 'wx' });
    writeFileSync(join(directory, 'identity'), config.key, { flag: 'wx' });
    sshArgs = connectionArguments(config, directory);
    const scpArgs = connectionArguments(config, directory, true);
    remote = execute('ssh', [...sshArgs, destination, `umask 077; mktemp -d /tmp/payplanner-preflight-${config.sha}-XXXXXXXX`], { echo: false }).trim();
    assert.match(remote, new RegExp(`^/tmp/payplanner-preflight-${config.sha}-[A-Za-z0-9]{8}$`), 'Unexpected remote temporary directory');
    execute('scp', [...scpArgs, ...sourceFiles.slice(0, linuxFiles.length), `${destination}:${remote}/`]);
    const checksums = sourceFiles.slice(0, linuxFiles.length).map(path => `${hash(content.get(path))}  ${basename(path)}`).join('\n') + '\n';
    const preflight = `set -euo pipefail\ntest "$(uname -s)" = Linux\ncd ${shellQuote(remote)}\nmkdir -m 700 fixtures\nexport TMPDIR="$PWD/fixtures"\nprintf %s ${shellQuote(checksums)} | sha256sum -c -\nbash prune-deploy-backups.test.sh\nbash preserve-data-protection-keys.test.sh\n`;
    execute('ssh', [...sshArgs, destination, 'bash -s'], { input: preflight, timeout: 300000 });
    execute('ssh', [...sshArgs, destination, `rm -rf -- ${shellQuote(remote)}`]);
    remote = undefined;
    // A failed Linux test or changed local input cannot reach the package upload.
    for (const path of sourceFiles) assert.equal(hash(readFileSync(path)), hash(content.get(path)), 'A qualified deployment input changed');
    execute('scp', [...scpArgs, config.archive, `${destination}:/tmp/`], { timeout: 900000 });
    const exports = Object.entries(config.remoteEnv).map(([name, value]) => `export ${name}=${shellQuote(value)}`).join('\n');
    const archiveCheck = `${hash(content.get(config.archive))}  /tmp/${basename(config.archive)}\n`;
    const payload = `set -euo pipefail\nset +x\n${exports}\nprintf %s ${shellQuote(archiveCheck)} | sha256sum -c -\n${content.get(join(config.root, 'scripts/deploy-vps.sh')).toString('utf8')}`;
    execute('ssh', [...sshArgs, destination, 'bash -s'], { input: payload, timeout: 1200000 });
    log('Linux filesystem gates and native VPS deployment passed.');
  } finally {
    try {
      if (remote && /^\/tmp\/payplanner-preflight-[a-f0-9]{40}-[A-Za-z0-9]{8}$/.test(remote) && sshArgs) {
        execute('ssh', [...sshArgs, destination, `rm -rf -- ${shellQuote(remote)}`]);
      }
    } finally {
      if (ownsDirectory) rmSync(directory, { recursive: true, force: true });
    }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  let config;
  try {
    assert.equal(process.argv.length, 2, 'Deployment configuration is accepted only through the structured job environment');
    config = configuration();
    await deploy(config);
  } catch (error) {
    console.error(redact(error.message, config?.secrets ?? sensitiveNames.map(name => process.env[name] ?? '')));
    process.exitCode = 1;
  }
}
