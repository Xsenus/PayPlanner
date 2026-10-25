import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';
import { configuration, connectionArguments, createPrivateDirectory, deploy, linuxFiles, nativeRun, redact, shellQuote, trustedHostLine } from './windows-vps-deploy.mjs';

const sha = 'a'.repeat(40);
const hostBlob = Buffer.from('synthetic host key for orchestration tests');
const fingerprint = 'SHA256:' + createHash('sha256').update(hostBlob).digest('base64').replace(/=+$/, '');
const key = '-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-private-material-only\n-----END OPENSSH PRIVATE KEY-----\n';
const scan = `185.46.10.202 ssh-ed25519 ${hostBlob.toString('base64')}\n`;

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-bridge-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, 'scripts'));
  for (const name of [...linuxFiles, 'deploy-vps.sh']) writeFileSync(join(root, 'scripts', name), readFileSync(resolve('scripts', name)));
  const systemRoot = join(root, 'windows');
  mkdirSync(join(systemRoot, 'System32/OpenSSH'), { recursive: true });
  for (const name of ['ssh', 'scp', 'ssh-keyscan']) writeFileSync(join(systemRoot, 'System32/OpenSSH', name + '.exe'), 'synthetic');
  writeFileSync(join(root, `payplanner_${sha}.tar.gz`), 'qualified archive placeholder');
  const env = { VPS_HOST: '185.46.10.202', VPS_USER: 'root', VPS_PORT: '22', DEPLOY_SHA: sha,
    DEPLOY_ARCHIVE_NAME: `payplanner_${sha}.tar.gz`, VPS_SSH_HOST_FINGERPRINT: fingerprint, VPS_SSH_KEY: key,
    PUBLIC_API_HEALTH_URL: 'https://api.elitpravo.com/api/health', PUBLIC_FRONTEND_URL: 'https://crm.elitpravo.com/',
    PAYPLANNER_WEBPUSH_PRIVATE_KEY: 'synthetic-vapid-private-value', PAYPLANNER_WEBPUSH_SUBJECT: "mailto:quote'example@example.invalid",
    RUNNER_TEMP: root, SystemRoot: systemRoot };
  return { root, env, config: configuration(env, root) };
}

function harness(config, fail) {
  const calls = [];
  const logs = [];
  let directory;
  return { calls, logs, directory: () => directory,
    options: { platform: 'win32', log: value => logs.push(value),
      workspace: () => { directory = mkdtempSync(join(config.temporaryRoot, 'payplanner-deploy-')); return directory; },
      run: (executable, args, options) => {
        const call = { tool: basename(executable), args, options }; calls.push(call);
        if (fail?.(call)) throw Object.assign(new Error('synthetic failure'), { status: 23, stderr: config.remoteEnv.PAYPLANNER_WEBPUSH_PRIVATE_KEY });
        if (call.tool === 'ssh-keyscan.exe') return scan;
        if (args.at(-1).startsWith('umask 077;')) return `/tmp/payplanner-preflight-${sha}-A1b2C3d4\n`;
        return '';
      },
    } };
}

test('requires the exact target, full revision, matching archive, host pin and structured configuration', t => {
  const { env, root } = fixture(t);
  for (const [name, value] of [['VPS_HOST', 'other.invalid'], ['VPS_USER', 'guest'], ['VPS_PORT', '2222'], ['DEPLOY_SHA', 'main'],
    ['DEPLOY_ARCHIVE_NAME', 'old.tar.gz'], ['VPS_SSH_HOST_FINGERPRINT', ''], ['VPS_SSH_KEY', ''], ['PAYPLANNER_WEBPUSH_SUBJECT', 'x\nInjected=1']]) {
    assert.throws(() => configuration({ ...env, [name]: value }, root));
  }
});

test('matches the actual host key fingerprint and refuses changed keys', t => {
  const { config } = fixture(t);
  assert.equal(trustedHostLine(scan, config), `payplanner-vps ssh-ed25519 ${hostBlob.toString('base64')}\n`);
  assert.throws(() => trustedHostLine(scan.replace(hostBlob.toString('base64'), Buffer.from('wrong host').toString('base64')), config), /fingerprint mismatch/);
});

test('a host-key mismatch creates no credential directory and never authenticates', async t => {
  const { config } = fixture(t);
  config.fingerprint = 'SHA256:' + 'B'.repeat(43);
  const state = harness(config);
  await assert.rejects(deploy(config, state.options), /fingerprint mismatch/);
  assert.equal(state.directory(), undefined);
  assert.deepEqual(state.calls.map(call => call.tool), ['ssh-keyscan.exe']);
});

test('schedules both Linux scripts and exact transferred-byte checks before package upload', async t => {
  const { config } = fixture(t);
  const state = harness(config);
  await deploy(config, state.options);
  const preflight = state.calls.findIndex(call => call.options.input?.includes('test "$(uname -s)" = Linux'));
  const upload = state.calls.findIndex(call => call.tool === 'scp.exe' && call.args.includes(config.archive));
  const installation = state.calls.findIndex(call => call.options.input?.includes('[11/14] Restart service'));
  assert.ok(preflight > 0 && upload > preflight && installation > upload);
  const script = state.calls[preflight].options.input;
  assert.match(script, /bash prune-deploy-backups\.test\.sh\nbash preserve-data-protection-keys\.test\.sh/);
  assert.match(script, /export TMPDIR="\$PWD\/fixtures"/);
  assert.match(script, /sha256sum -c -/);
  assert.equal(state.calls.filter(call => call.tool === 'scp.exe').length, 2);
  assert.ok(state.calls.some(call => call.args.at(-1).startsWith('rm -rf -- ') && state.calls.indexOf(call) < upload));
  assert.ok(!existsSync(state.directory()));
});

test('a failed Linux gate blocks archive transfer and installation, preserves native failure and cleans credentials', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.options.input?.includes('test "$(uname -s)" = Linux'));
  await assert.rejects(deploy(config, state.options), /native exit 23/);
  assert.ok(!state.calls.some(call => call.args.includes(config.archive)));
  assert.ok(!state.calls.some(call => call.options.input?.includes('[11/14] Restart service')));
  assert.ok(state.calls.some(call => call.args.at(-1).startsWith('rm -rf -- ')));
  assert.ok(!existsSync(state.directory()));
});

test('changing a local checked script during Linux preflight blocks the release upload', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => {
    if (call.options.input?.includes('test "$(uname -s)" = Linux')) writeFileSync(join(config.root, 'scripts', linuxFiles[0]), 'changed');
    return false;
  });
  await assert.rejects(deploy(config, state.options), /deployment input changed/);
  assert.ok(!state.calls.some(call => call.args.includes(config.archive)));
  assert.ok(!existsSync(state.directory()));
});

test('secrets never enter command arguments or child environment; remote values use quoted stdin', async t => {
  const { config } = fixture(t);
  const state = harness(config);
  await deploy(config, state.options);
  for (const call of state.calls) {
    const exposed = JSON.stringify({ args: call.args, env: call.options.env });
    for (const secret of config.secrets.filter(Boolean)) assert.ok(!exposed.includes(secret));
    assert.equal(call.options.env.VPS_SSH_KEY, undefined);
    assert.equal(call.options.env.PAYPLANNER_WEBPUSH_PRIVATE_KEY, undefined);
  }
  const payload = state.calls.find(call => call.options.input?.includes('[11/14] Restart service')).options.input;
  assert.ok(payload.includes('export PAYPLANNER_WEBPUSH_SUBJECT=' + shellQuote(config.remoteEnv.PAYPLANNER_WEBPUSH_SUBJECT)));
  assert.ok(payload.indexOf('set +x') < payload.indexOf('export PAYPLANNER_WEBPUSH_PRIVATE_KEY='));
  assert.ok(!payload.includes(config.key));
});

test('native failures redact secret output and still remove private credentials', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.options.input?.includes('[11/14] Restart service'));
  await assert.rejects(deploy(config, state.options), error => error.message.includes('[redacted]') && !error.message.includes(config.remoteEnv.PAYPLANNER_WEBPUSH_PRIVATE_KEY));
  assert.ok(!existsSync(state.directory()));
  assert.ok(!state.logs.join('\n').includes(config.remoteEnv.PAYPLANNER_WEBPUSH_PRIVATE_KEY));
});

test('connection ignores ambient SSH configuration and requires exactly the pinned identity', t => {
  const { config } = fixture(t);
  const args = connectionArguments(config, join(config.temporaryRoot, 'folder with spaces'));
  for (const value of ['NUL', 'StrictHostKeyChecking=yes', 'BatchMode=yes', 'IdentitiesOnly=yes', 'PasswordAuthentication=no', 'HostKeyAlias=payplanner-vps']) assert.ok(args.includes(value));
  assert.ok(args.some(value => value.startsWith('UserKnownHostsFile="') && value.endsWith('"')));
});

test('private directory ACL is applied before returning any location for secret writes', t => {
  const { config } = fixture(t);
  const calls = [];
  const directory = createPrivateDirectory(config, (executable, args) => {
    calls.push({ executable, args });
    assert.ok(!existsSync(join(args[0] ?? '', 'identity')));
    return executable.endsWith('whoami.exe') ? '"user","S-1-5-21-123-456-789-1001"' : '';
  });
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  assert.equal(calls.length, 2);
  assert.ok(calls[1].args.includes('/inheritance:r'));
  assert.ok(calls[1].args.includes('*S-1-5-21-123-456-789-1001:(OI)(CI)F'));
  assert.ok(calls[1].args.includes('*S-1-5-18:(OI)(CI)F'));
});

test('ACL failure removes its empty directory and fails closed', t => {
  const { config } = fixture(t);
  let directory;
  assert.throws(() => createPrivateDirectory(config, (executable, args) => {
    if (executable.endsWith('whoami.exe')) return '"user","S-1-5-21-123-456-789-1001"';
    directory = args[0]; throw new Error('ACL failure');
  }), /ACL failure/);
  assert.ok(directory && !existsSync(directory));
});

test('redaction covers complete and split private-key lines', () => {
  assert.ok(!redact(key + 'synthetic-private-material-only', [key]).includes('synthetic-private-material-only'));
});

test('native execution captures stderr without echoing it before redaction', () => {
  const moduleUrl = pathToFileURL(resolve('scripts/windows-vps-deploy.mjs')).href;
  const probe = `import { nativeRun } from ${JSON.stringify(moduleUrl)}; try { nativeRun(process.execPath, ['-e', 'process.stderr.write("synthetic-sensitive-stderr");process.exit(7)']); } catch { console.log('captured'); }`;
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', probe], { encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0);
  assert.equal(result.stderr, '');
  assert.equal(result.stdout.trim(), 'captured');
});

test('native Windows ACL setup succeeds on an isolated empty directory', { skip: process.platform !== 'win32' }, t => {
  const { config } = fixture(t);
  config.systemRoot = process.env.SystemRoot || 'C:/Windows';
  config.childEnv = { ...process.env };
  const directory = createPrivateDirectory(config, nativeRun);
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const acl = nativeRun(join(config.systemRoot, 'System32/icacls.exe'), [directory], { env: config.childEnv });
  assert.ok(!acl.includes('(I)'), 'The private directory must not retain inherited permissions');
});
