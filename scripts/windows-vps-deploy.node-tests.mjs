import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';
import { configuration, connectionArguments, createPrivateDirectory, deploy, haltCommand, haltDeployment, linuxFiles, nativeRun, nativeStream, redact, shellQuote, trustedHostLine, validatePrivateRecovery } from './windows-vps-deploy.mjs';

const sha = 'a'.repeat(40);
const hostBlob = Buffer.from('synthetic host key for orchestration tests');
const fingerprint = 'SHA256:' + createHash('sha256').update(hostBlob).digest('base64').replace(/=+$/, '');
const key = '-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-private-material-only\n-----END OPENSSH PRIVATE KEY-----\n';
const scan = `185.46.10.202 ssh-ed25519 ${hostBlob.toString('base64')}\n`;

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-bridge-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, 'scripts'));
  for (const name of [...linuxFiles, 'deploy-vps.sh', 'windows-vps-deploy.mjs', 'prepare-deployment-plan.mjs', 'prepare-deployment-package.mjs']) writeFileSync(join(root, 'scripts', name), readFileSync(resolve('scripts', name)));
  mkdirSync(join(root, 'out'));
  mkdirSync(join(root, 'recovery'));
  const systemRoot = join(root, 'windows');
  mkdirSync(join(systemRoot, 'System32/OpenSSH'), { recursive: true });
  for (const name of ['ssh', 'scp']) writeFileSync(join(systemRoot, 'System32/OpenSSH', name + '.exe'), 'synthetic');
  const keyscanExecutable = join(root, 'git/usr/bin/ssh-keyscan.exe');
  mkdirSync(dirname(keyscanExecutable), { recursive: true });
  writeFileSync(keyscanExecutable, 'synthetic compatible scanner');
  writeFileSync(join(root, `payplanner_${sha}.tar.gz`), 'qualified archive placeholder');
  const env = { VPS_HOST: '185.46.10.202', VPS_USER: 'root', VPS_PORT: '22', DEPLOY_SHA: sha,
    DEPLOY_ARCHIVE_NAME: `payplanner_${sha}.tar.gz`, VPS_SSH_HOST_FINGERPRINT: fingerprint, VPS_SSH_KEY: key,
    PUBLIC_API_HEALTH_URL: 'https://api.elitpravo.com/api/health', PUBLIC_FRONTEND_URL: 'https://crm.elitpravo.com/',
    PAYPLANNER_WEBPUSH_PRIVATE_KEY: 'synthetic-vapid-private-value', PAYPLANNER_WEBPUSH_SUBJECT: "mailto:quote'example@example.invalid",
    RUNNER_TEMP: root, SystemRoot: systemRoot };
  const config = configuration(env, root);
  config.keyscanExecutable = keyscanExecutable;
  return { root, env, config };
}

function harness(config, fail) {
  const calls = [];
  const logs = [];
  let directory;
  return { calls, logs, directory: () => directory,
    options: { platform: 'win32', log: value => logs.push(value),
      prepare: () => ({ plan: { releaseSha: sha, previousRevision: 'b'.repeat(40), expectedApiSha256: 'c'.repeat(64),
        expectedPreviousApiSha256: 'd'.repeat(64), backupUtc: '20261026T120000Z', historyManifest: { sha256: 'e'.repeat(64) },
        offsiteHistoryReceipt: { sha256: 'f'.repeat(64) } },
        qualificationRaw: Buffer.from('{"runId":"123","runAttempt":"1"}'), historyRaw: Buffer.from('{}'), historyManifestRaw: Buffer.from('{}'),
        offsiteRoot: join(config.root, 'recovery') }),
      recoveryWorkspace: recovery => mkdtempSync(join(recovery.temporaryRoot, 'payplanner-deploy-')),
      stream: async (executable, args, destination, options) => {
        const call = { tool: 'stream', args, options }; calls.push(call);
        if (fail?.(call)) throw Object.assign(new Error('synthetic stream failure'), { status: 24 });
        writeFileSync(destination, 'synthetic stopped backup');
      },
      workspace: () => { directory = mkdtempSync(join(config.temporaryRoot, 'payplanner-deploy-')); return directory; },
      run: (executable, args, options) => {
        const call = { executable, tool: basename(executable), args, options }; calls.push(call);
        if (fail?.(call)) throw Object.assign(new Error('synthetic failure'), { status: 23, stderr: config.remoteEnv.PAYPLANNER_WEBPUSH_PRIVATE_KEY });
        if (call.tool === 'ssh-keyscan.exe') return scan;
        if (call.tool === 'python') {
          const list = Buffer.from('synthetic native listing');
          writeFileSync(join(dirname(args.at(-1)), 'database.list'), list);
          writeFileSync(args.at(-1), JSON.stringify({ schemaVersion: 1, rootAccepted: false, verifiedOutsideVps: false,
            backupServerPath: `/opt/payplanner/deploy-backups/manual-${sha}-20261026T120000Z`, backupManifestSha256: '1'.repeat(64),
            allListedFilesCopied: true, allCopiedHashesMatched: true, allEntryMetadataMatched: true, sourceBeforeAfterExact: true,
            offsitePgRestoreListExitCode: 0, offsitePgRestoreListSha256: createHash('sha256').update(list).digest('hex'),
            archive: { bytes: Buffer.byteLength('synthetic stopped backup') } })); return '';
        }
        if (args.at(-1).startsWith('umask 077;')) return `/tmp/payplanner-${args.at(-1).includes('control-') ? 'control' : 'preflight'}-${sha}-A1b2C3d4\n`;
        if (args.at(-1).startsWith('python3 -B')) return JSON.stringify({ entries: [{ path: 'BACKUP-SHA256SUMS', sha256: '1'.repeat(64) }] }) + '\n';
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
  assert.equal(state.calls[0].executable, config.keyscanExecutable);
});

test('missing compatible scanner fails before materialising a credential without an unpinned fallback', async t => {
  const { config } = fixture(t);
  rmSync(config.keyscanExecutable);
  const state = harness(config);
  await assert.rejects(deploy(config, state.options), /OpenSSH client is required/);
  assert.equal(state.calls.length, 0);
  assert.equal(state.directory(), undefined);
});

test('schedules both Linux scripts and exact transferred-byte checks before package upload', async t => {
  const { config } = fixture(t);
  const state = harness(config);
  await deploy(config, state.options);
  const preflight = state.calls.findIndex(call => call.options.input?.includes('test "$(uname -s)" = Linux'));
  const upload = state.calls.findIndex(call => call.tool === 'scp.exe' && call.args.includes(config.archive));
  const installation = state.calls.findIndex(call => call.args.at(-1).includes('--execute install '));
  for (const name of ['deploy-roadmap-candidate-review.json', 'deploy-roadmap-candidate-delta.json',
    'deploy-act-annual-review.json', 'deploy-act-annual-delta.json']) {
    assert.ok(linuxFiles.includes(name), 'Reviewed candidate data must reach Linux preflight');
    const transfer = state.calls.find(call => call.tool === 'scp.exe' && call.args.at(-1).includes('/tmp/payplanner-control-') && call.args.some(arg => basename(arg) === name));
    assert.ok(transfer, 'Reviewed candidate data must also reach the installer control directory');
    const path = transfer.args.find(arg => basename(arg) === name);
    assert.ok(path.startsWith(state.directory()), 'Installer data must come from the immutable invocation snapshot');
  }
  assert.ok(preflight > 0 && upload > preflight && installation > upload);
  const script = state.calls[preflight].options.input;
  const preflightTransfer = state.calls.find(call => call.tool === 'scp.exe' && call.args.at(-1).includes('/tmp/payplanner-preflight-'));
  assert.ok(preflightTransfer, 'Linux checks must receive their files before they run');
  for (const name of ['deployment-baseline.json', 'deployment-baseline-current.json']) {
    assert.ok(preflightTransfer.args.some(arg => basename(arg) === name), name + ' is required by the Linux database proof tests');
    assert.ok(script.includes('  ' + name + '\n'), name + ' must be verified after transfer');
  }
  assert.match(script, /bash prune-deploy-backups\.test\.sh\nbash preserve-data-protection-keys\.test\.sh/);
  assert.match(script, /python3 -B deploy-history-archive\.test\.py\n/);
  assert.match(script, /export TMPDIR="\$PWD\/fixtures"/);
  assert.match(script, /sha256sum -c -/);
  const backup = state.calls.findIndex(call => call.args.at(-1).includes('--execute backup '));
  const copy = state.calls.findIndex(call => call.tool === 'stream');
  const verified = state.calls.findIndex(call => call.tool === 'python');
  assert.ok(upload < backup && backup < copy && copy < verified && verified < installation);
  assert.match(state.calls[verified].args.join(' '), /pg_restore\.exe/);
  const evidence = state.logs.find(line => line.startsWith('Private recovery evidence: ')).slice('Private recovery evidence: '.length);
  assert.ok(existsSync(join(evidence, 'stopped.tar')));
  assert.ok(existsSync(join(evidence, 'offsite-verified.json')));
  assert.ok(state.calls.some(call => call.args.at(-1).startsWith('rm -rf -- ') && state.calls.indexOf(call) < upload));
  assert.ok(!existsSync(state.directory()));
});

test('a failed Linux gate blocks archive transfer and installation, preserves native failure and cleans credentials', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.options.input?.includes('test "$(uname -s)" = Linux'));
  await assert.rejects(deploy(config, state.options), /native exit 23/);
  assert.ok(!state.calls.some(call => call.args.includes(config.archive)));
  assert.ok(!state.calls.some(call => call.args.at(-1).includes('--execute install ')));
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

test('secrets never enter command arguments, child environment or remote stdin', async t => {
  const { config } = fixture(t);
  const state = harness(config);
  await deploy(config, state.options);
  for (const call of state.calls) {
    const exposed = JSON.stringify({ args: call.args, env: call.options.env });
    for (const secret of config.secrets.filter(Boolean)) assert.ok(!exposed.includes(secret));
    assert.equal(call.options.env.VPS_SSH_KEY, undefined);
    assert.equal(call.options.env.PAYPLANNER_WEBPUSH_PRIVATE_KEY, undefined);
  }
  for (const call of state.calls) {
    for (const secret of config.secrets.filter(Boolean)) assert.ok(!(call.options.input ?? '').includes(secret));
  }
  assert.equal(shellQuote("value'quoted"), "'value'\\''quoted'");
});

test('a failed stopped-backup stream aborts before installation and retains the recovery point', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.tool === 'stream');
  await assert.rejects(deploy(config, state.options), /native exit 24/);
  assert.ok(state.calls.some(call => call.args.at(-1).includes('--execute abort ')));
  assert.ok(!state.calls.some(call => call.args.at(-1).includes('--execute install ')));
  assert.ok(!state.calls.some(call => call.options.input?.includes('HALT_AFTER_READINESS_FAILURE')));
  assert.ok(!existsSync(state.directory()));
  const evidence = state.logs.find(line => line.startsWith('Private recovery evidence: ')).slice('Private recovery evidence: '.length);
  assert.ok(existsSync(join(evidence, 'stopped-before.json')));
});

test('native dump-list or local archive verification failure cannot authorise installation', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.tool === 'python');
  await assert.rejects(deploy(config, state.options), /synthetic failure/);
  assert.ok(state.calls.some(call => call.args.at(-1).includes('--execute abort ')));
  assert.ok(!state.calls.some(call => call.args.at(-1).includes('--execute install ')));
  assert.ok(!existsSync(state.directory()));
});

test('an unbound verifier adoption cannot substitute for the trusted offsite caller', async t => {
  const { config } = fixture(t);
  const state = harness(config);
  const original = state.options.run;
  state.options.run = (executable, args, options) => {
    const output = original(executable, args, options);
    if (executable === 'python') {
      const result = JSON.parse(readFileSync(args.at(-1), 'utf8'));
      result.rootAccepted = true;
      writeFileSync(args.at(-1), JSON.stringify(result));
    }
    return output;
  };
  await assert.rejects(deploy(config, state.options));
  assert.ok(state.calls.some(call => call.args.at(-1).includes('--execute abort ')));
  assert.ok(!state.calls.some(call => call.args.at(-1).includes('--execute install ')));
});

test('after an attempted installation, failure halts the exact new revision instead of starting old code', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.args.at(-1).includes('--execute install '));
  await assert.rejects(deploy(config, state.options));
  const halt = state.calls.find(call => call.options.input?.includes('HALT_AFTER_READINESS_FAILURE'));
  assert.ok(halt);
  assert.match(halt.options.input, /systemctl stop payplanner\.service/);
  assert.ok(!halt.options.input.includes('systemctl start'));
  assert.ok(!halt.options.input.includes('pg_restore'));
  assert.ok(!state.calls.some(call => call.args.at(-1).includes('--execute abort ')));
});

test('guarded halt refuses invalid revision or digest identities', () => {
  const record = { releaseSha: sha, previousRevision: 'b'.repeat(40), expectedApiSha256: 'c'.repeat(64), expectedPreviousApiSha256: 'd'.repeat(64) };
  assert.match(haltCommand(record), /flock -n 9/);
  assert.match(haltCommand(record), /Unexpected deployment identity/);
  for (const name of Object.keys(record)) assert.throws(() => haltCommand({ ...record, [name]: 'main;exit 0' }));
});

function haltFixture(t) {
  const { config } = fixture(t);
  mkdirSync(join(config.root, 'out/be'));
  const api = Buffer.from('qualified api fixture');
  writeFileSync(join(config.root, 'out/be/PayPlanner.Api.dll'), api);
  // Explicit synthetic function input, never a locally forged CLI/runner context.
  config.childEnv = { ...config.childEnv, COMPUTERNAME: 'XSENUS', GITHUB_ACTIONS: 'true',
    GITHUB_REPOSITORY: 'Xsenus/PayPlanner', GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
    GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main',
    GITHUB_JOB: 'deploy', GITHUB_SHA: sha, GITHUB_WORKFLOW_SHA: sha, GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', OUT_DIR: 'out',
    ARTIFACT_NEEDS_JSON: JSON.stringify({ frontend: { result: 'success', outputs: { 'frontend-artifact-id': 'xsenus-local-v1:'+'1'.repeat(64) } },
      'backend-format': { result: 'success', outputs: {} },
      backend: { result: 'success', outputs: { 'backend-artifact-id': 'xsenus-local-v1:'+'2'.repeat(64), 'manifest-sha': '3'.repeat(64) } } }) };
  const record = { releaseSha: sha, previousRevision: 'b'.repeat(40), runId: '123', runAttempt: '1',
    expectedApiSha256: createHash('sha256').update(api).digest('hex'), expectedPreviousApiSha256: 'd'.repeat(64) };
  const path = join(config.root, 'out/deployment-installed.json');
  writeFileSync(path, JSON.stringify(record));
  return { config, record, path };
}

test('readiness HALT rejects another run, attempt, revision or API before authentication', async t => {
  for (const [name, value] of [['runId', '999'], ['runAttempt', '2'], ['releaseSha', 'b'.repeat(40)], ['expectedApiSha256', 'e'.repeat(64)]]) {
    await t.test(name, async child => {
      const { config, record, path } = haltFixture(child);
      writeFileSync(path, JSON.stringify({ ...record, [name]: value }));
      const state = harness(config);
      await assert.rejects(haltDeployment(config, state.options));
      assert.equal(state.calls.length, 0);
      assert.equal(state.directory(), undefined);
    });
  }
});

test('readiness HALT requires a matching pinned host and cleans its temporary credential', async t => {
  const { config } = haltFixture(t);
  const state = harness(config);
  await haltDeployment(config, state.options);
  const halt = state.calls.find(call => call.options.input?.includes('HALT_AFTER_READINESS_FAILURE'));
  assert.ok(halt);
  assert.equal(state.calls[0].tool, 'ssh-keyscan.exe');
  assert.ok(!state.calls.some(call => call.tool === 'scp.exe'));
  assert.ok(!halt.options.input.includes('systemctl start'));
  assert.ok(!halt.options.input.includes('pg_restore'));
  assert.ok(!existsSync(state.directory()));
});

test('readiness HALT propagates a native SSH failure without retaining the credential', async t => {
  const { config } = haltFixture(t);
  const state = harness(config, call => call.options.input?.includes('HALT_AFTER_READINESS_FAILURE'));
  await assert.rejects(haltDeployment(config, state.options), /native exit 23/);
  assert.ok(!existsSync(state.directory()));
});

test('offsite recovery data denies broad read access as well as broad write access', () => {
  const path = resolve('D:/PayPlannerOffsiteBackups');
  const sid = 'S-1-5-21-123-456-789-1001';
  const report = { sid, items: [{ path, owner: sid, reparse: false, directory: true, fixed: true, format: 'NTFS', streams: [],
    rules: [{ sid, rights: 2032127, allow: true, inheritOnly: false }] }] };
  validatePrivateRecovery(report, [path], path);
  report.items[0].rules.push({ sid: 'S-1-1-0', rights: 1, allow: true, inheritOnly: false });
  assert.throws(() => validatePrivateRecovery(report, [path], path), /another Windows account/);
});

test('native backup streaming writes exact bytes with bounded memory and propagates failure', async t => {
  const { root } = fixture(t);
  const path = join(root, 'native-stream.bin');
  await nativeStream(process.execPath, ['-e', 'process.stdout.write(Buffer.alloc(2*1024*1024, 42))'], path);
  assert.deepEqual(readFileSync(path), Buffer.alloc(2*1024*1024, 42));
  const partial = join(root, 'native-partial.bin');
  await assert.rejects(nativeStream(process.execPath, ['-e', 'process.stdout.write("partial");process.stderr.write("private failure");process.exit(7)'], partial),
    error => error.status === 7 && error.stderr === 'private failure');
  assert.equal(readFileSync(partial, 'utf8'), 'partial');
  await assert.rejects(nativeStream(process.execPath, ['-e', 'process.exit(0)'], partial), { code: 'EEXIST' });
});

test('changed history proof or its tests after Linux preflight cannot reach archive upload', async t => {
  for (const name of ['deploy-history-archive.py', 'deploy-history-archive.test.py']) {
    await t.test(name, async child => {
      const { config } = fixture(child);
      const state = harness(config, call => {
        if (call.options.input?.includes('test "$(uname -s)" = Linux')) writeFileSync(join(config.root, 'scripts', name), 'changed');
        return false;
      });
      await assert.rejects(deploy(config, state.options), /deployment input changed/);
      assert.ok(!state.calls.some(call => call.args.includes(config.archive)));
      assert.ok(!existsSync(state.directory()));
    });
  }
});

test('native failures redact secret output and still remove private credentials', async t => {
  const { config } = fixture(t);
  const state = harness(config, call => call.args.at(-1).includes('--execute install '));
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
