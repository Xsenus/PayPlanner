import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import test from 'node:test';
import { checkDeploymentAccess, configuration } from './windows-vps-deploy.mjs';

const sha = 'a'.repeat(40);
const hostBlob = Buffer.from('synthetic access preflight host key');
const fingerprint = 'SHA256:' + createHash('sha256').update(hostBlob).digest('base64').replace(/=+$/, '');
const scan = `185.46.10.202 ssh-ed25519 ${hostBlob.toString('base64')}\n`;

function accessFixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'payplanner-access-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const systemRoot = join(root, 'windows');
  mkdirSync(join(systemRoot, 'System32/OpenSSH'), { recursive: true });
  for (const name of ['ssh', 'scp']) writeFileSync(join(systemRoot, 'System32/OpenSSH', name + '.exe'), 'synthetic');
  const keyscanExecutable = join(root, 'git/usr/bin/ssh-keyscan.exe');
  mkdirSync(dirname(keyscanExecutable), { recursive: true });
  writeFileSync(keyscanExecutable, 'synthetic scanner');
  // Explicit synthetic function input, never a forged local CLI/runner context.
  const env = { VPS_HOST: '185.46.10.202', VPS_USER: 'root', VPS_PORT: '22', DEPLOY_SHA: sha,
    DEPLOY_ARCHIVE_NAME: `payplanner_${sha}.tar.gz`, VPS_SSH_HOST_FINGERPRINT: fingerprint,
    VPS_SSH_KEY: '-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-only\n-----END OPENSSH PRIVATE KEY-----',
    PUBLIC_API_HEALTH_URL: 'https://api.elitpravo.com/api/health', PUBLIC_FRONTEND_URL: 'https://crm.elitpravo.com/',
    RUNNER_TEMP: root, SystemRoot: systemRoot, COMPUTERNAME: 'XSENUS', GITHUB_ACTIONS: 'true',
    GITHUB_REPOSITORY: 'Xsenus/PayPlanner', GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
    GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main', GITHUB_JOB: 'frontend',
    GITHUB_SHA: sha, GITHUB_WORKFLOW_SHA: sha, GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1',
    ARTIFACT_NEEDS_JSON: '{}', PAYPLANNER_CI_ARTIFACT_ROOT: 'D:\\PayPlannerCIArtifacts\\main-store',
    RUNNER_NAME: 'PayPlanner-XSENUS', RUNNER_OS: 'Windows', RUNNER_ARCH: 'X64' };
  const config = configuration(env, root);
  config.keyscanExecutable = keyscanExecutable;
  return config;
}

function harness(config, fail) {
  const calls = [];
  let directory;
  return { calls, directory: () => directory, options: { platform: 'win32', log: () => {},
    workspace: () => { directory = mkdtempSync(join(config.temporaryRoot, 'payplanner-deploy-')); return directory; },
    run: (executable, args, options) => {
      const call = { tool: basename(executable), args, options }; calls.push(call);
      if (fail?.(call)) throw Object.assign(new Error('synthetic access failure'), { status: 23 });
      return call.tool === 'ssh-keyscan.exe' ? scan : '';
    },
  } };
}

test('deployment access preflight performs only authenticated true before any qualified package exists', async t => {
  const config = accessFixture(t);
  const state = harness(config);
  await checkDeploymentAccess(config, state.options);
  assert.deepEqual(state.calls.map(call => call.tool), ['ssh-keyscan.exe', 'ssh.exe']);
  assert.equal(state.calls.at(-1).args.at(-1), 'true');
  assert.ok(!existsSync(state.directory()));
});

test('deployment access preflight rejects a local, foreign, wrong-role or wrong-revision context before credentials', async t => {
  for (const [name, value] of [['GITHUB_ACTIONS', 'false'], ['GITHUB_JOB', 'deploy'], ['GITHUB_REF', 'refs/heads/other'],
    ['GITHUB_SHA', 'b'.repeat(40)], ['GITHUB_WORKFLOW_SHA', 'b'.repeat(40)], ['RUNNER_NAME', 'other']]) {
    await t.test(name, async child => {
      const config = accessFixture(child);
      config.childEnv[name] = value;
      const state = harness(config);
      await assert.rejects(checkDeploymentAccess(config, state.options));
      assert.equal(state.calls.length, 0);
      assert.equal(state.directory(), undefined);
    });
  }
});

test('deployment access preflight preserves native SSH failure and removes the temporary key', async t => {
  const config = accessFixture(t);
  const state = harness(config, call => call.tool === 'ssh.exe');
  await assert.rejects(checkDeploymentAccess(config, state.options), /native exit 23/);
  assert.ok(!existsSync(state.directory()));
});

