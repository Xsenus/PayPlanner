// @vitest-environment node
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { afterEach, describe, it } from 'vitest';
import { assertUnchanged, snapshotVerificationInputs } from './verification-inputs.mjs';

const execute = promisify(execFile);
const roots = [];
const envName = 'PAYPLANNER_VERIFICATION_TEST_PRIVATE_VALUE';
const previousEnv = process.env[envName];
afterEach(async () => {
  if (previousEnv === undefined) delete process.env[envName]; else process.env[envName] = previousEnv;
  for (const root of roots.splice(0)) await rm(root, { recursive: true, force: true });
});

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), 'pp-verification-inputs-'));
  roots.push(root);
  const git = async (...args) => execute('git', args, { cwd: root, windowsHide: true });
  await git('init', '-q');
  await git('config', 'user.name', 'Verification test');
  await git('config', 'user.email', 'verification@example.invalid');
  await git('config', 'commit.gpgsign', 'false');
  await writeFile(join(root, '.gitignore'), 'node_modules/\nartifacts/\n.env.local\nprivate-secret.json\n');
  await mkdir(join(root, 'src'));
  await mkdir(join(root, 'docs'));
  await writeFile(join(root, 'src/main.ts'), 'export const value = 1;');
  await writeFile(join(root, 'docs/notes.md'), 'Notes');
  await writeFile(join(root, 'docs/tested.md'), 'Contract');
  await git('add', '.');
  await git('commit', '-qm', 'Initial verification fixture');
  return { root, git };
}

describe('durable full-gate input qualification', () => {
  it('is canonical across repeat snapshots, index changes and commits with identical inputs', async () => {
    const { root, git } = await fixture();
    await writeFile(join(root, 'src/new.ts'), 'export const next = 2;');
    const before = await snapshotVerificationInputs({ root, environment: { toolchain: { sdk: '9', browser: 'stable' } } });
    assert.equal((await snapshotVerificationInputs({ root, environment: { toolchain: { browser: 'stable', sdk: '9' } } })).digest, before.digest);
    await git('add', 'src/new.ts');
    await git('commit', '-qm', 'Commit already verified files');
    await assertUnchanged(before, { root, environment: { toolchain: { sdk: '9', browser: 'stable' } } });
    await rm(join(root, 'src/new.ts'));
    const deletion = await snapshotVerificationInputs({ root });
    await git('add', '-u');
    await git('commit', '-qm', 'Commit already verified deletion');
    await assertUnchanged(deletion, { root });
  });

  it('fails closed for new unknown files, source changes, and tested docs, but allows only explicitly untested prose', async () => {
    const { root, git } = await fixture();
    const options = { root, untestedDocs: ['docs/notes.md', 'docs/tested.md'], testedDocs: ['docs/tested.md'] };
    const before = await snapshotVerificationInputs(options);
    await writeFile(join(root, 'docs/notes.md'), 'Updated unrelated notes');
    await git('add', 'docs/notes.md');
    await git('commit', '-qm', 'Documentation only');
    await assertUnchanged(before, options);
    await writeFile(join(root, 'new-tool.cfg'), 'unknown influencing input');
    await assert.rejects(assertUnchanged(before, options), /repository/);
    await rm(join(root, 'new-tool.cfg'));
    await writeFile(join(root, 'docs/new.md'), 'Unknown documentation may be a tested input.');
    await assert.rejects(assertUnchanged(before, options), /repository/);
    await rm(join(root, 'docs/new.md'));
    await writeFile(join(root, 'docs/tested.md'), 'Changed contract');
    await assert.rejects(assertUnchanged(before, options), /repository/);
    await writeFile(join(root, 'docs/tested.md'), 'Contract');
    await writeFile(join(root, 'src/main.ts'), 'export const value = 2;');
    await assert.rejects(assertUnchanged(before, options), /repository/);
    await assert.rejects(snapshotVerificationInputs({ root, untestedDocs: ['src/main.ts'] }), /untested prose/);
  });

  it('ignores ordinary ignored secrets while explicitly qualified artifacts and runtime dependencies remain byte-sensitive', async () => {
    const { root } = await fixture();
    const artifactRoot = join(root, 'artifacts/runtime');
    await mkdir(artifactRoot, { recursive: true });
    await writeFile(join(artifactRoot, 'app.dll'), 'compiled application');
    await mkdir(join(root, 'node_modules/library'), { recursive: true });
    await writeFile(join(root, 'node_modules/library/index.js'), 'runtime dependency');
    const options = { root, artifactRoots: [artifactRoot] };
    const before = await snapshotVerificationInputs(options);
    await writeFile(join(root, 'private-secret.json'), 'never read or persist this secret');
    await assertUnchanged(before, options);
    assert.ok(!JSON.stringify(before).includes('never read'));
    await writeFile(join(root, 'node_modules/library/index.js'), 'changed dependency');
    await assert.rejects(assertUnchanged(before, options), /dependencies/);
    await writeFile(join(root, 'node_modules/library/index.js'), 'runtime dependency');
    await mkdir(join(root, 'node_modules/.cache'), { recursive: true });
    await writeFile(join(root, 'node_modules/.cache/tsbuildinfo'), 'generated');
    await assertUnchanged(before, options);
    await writeFile(join(artifactRoot, 'app.dll'), 'changed executable');
    await assert.rejects(assertUnchanged(before, options), /artifacts/);
    await rm(artifactRoot, { recursive: true });
    await assert.rejects(snapshotVerificationInputs(options), /ENOENT/);
  });

  it('explicitly hashes ignored project dotenv inputs without recording their contents', async () => {
    const { root } = await fixture();
    const before = await snapshotVerificationInputs({ root });
    await writeFile(join(root, '.env.local'), 'VITE_API_URL=http://127.0.0.1:12345/api\nPRIVATE_CONFIG=do-not-persist-this\n');
    await assert.rejects(assertUnchanged(before, { root }), /repository/);
    const configured = await snapshotVerificationInputs({ root });
    expectHash(configured.repository.files.find(file => file.path === '.env.local')?.sha256);
    assert.ok(!JSON.stringify(configured).includes('do-not-persist-this'));
    await writeFile(join(root, '.env.local'), 'VITE_API_URL=http://127.0.0.1:12346/api\n');
    await assert.rejects(assertUnchanged(configured, { root }), /repository/);
  });

  it('qualifies executable shims and detects a local node override, changed wrapper, or missing wrapper', async () => {
    const { root } = await fixture();
    const bin = join(root, 'node_modules/.bin');
    await mkdir(bin, { recursive: true });
    const wrappers = ['tsc.cmd', 'eslint.ps1', 'vite'];
    for (const wrapper of wrappers) await writeFile(join(bin, wrapper), 'original executable shim');
    const before = await snapshotVerificationInputs({ root });
    assert.deepEqual(before.dependencies[0].files.map(file => file.path), ['.bin/eslint.ps1', '.bin/tsc.cmd', '.bin/vite']);
    for (const wrapper of wrappers) {
      await writeFile(join(bin, wrapper), 'changed executable shim');
      await assert.rejects(assertUnchanged(before, { root }), /dependencies/);
      await writeFile(join(bin, wrapper), 'original executable shim');
    }
    await writeFile(join(bin, 'node.exe'), 'unqualified local node override');
    await assert.rejects(assertUnchanged(before, { root }), /dependencies/);
    await rm(join(bin, 'node.exe'));
    await assertUnchanged(before, { root });
    await rm(join(bin, wrappers[0]));
    await assert.rejects(assertUnchanged(before, { root }), /dependencies/);
  });

  it('qualifies npm executable symlink identity and target bytes within the dependency root', async () => {
    const { root } = await fixture();
    const dependencies = join(root, 'node_modules');
    await mkdir(join(dependencies, '.bin'), { recursive: true });
    await mkdir(join(dependencies, 'tool'));
    for (const name of ['first.js', 'second.js']) await writeFile(join(dependencies, 'tool', name), 'same executable bytes');
    const link = join(dependencies, '.bin/tool');
    await symlink('../tool/first.js', link, 'file');
    const before = await snapshotVerificationInputs({ root });
    const executable = before.dependencies[0].files.find(file => file.path === '.bin/tool');
    assert.equal(executable.linkTarget.replaceAll('\\', '/'), '../tool/first.js');
    assert.equal(executable.resolvedTarget, 'tool/first.js');
    assert.equal(executable.sha256, before.dependencies[0].files.find(file => file.path === 'tool/first.js').sha256);
    await writeFile(join(dependencies, 'tool/first.js'), 'changed target');
    await assert.rejects(assertUnchanged(before, { root }), /dependencies/);
    await writeFile(join(dependencies, 'tool/first.js'), 'same executable bytes');
    await assertUnchanged(before, { root });
    await rm(link);
    await symlink('../tool/second.js', link, 'file');
    await assert.rejects(assertUnchanged(before, { root }), /dependencies/);
  });

  it('rejects external, dangling, directory, and non-bin dependency symlinks', async () => {
    const { root } = await fixture();
    const dependencies = join(root, 'node_modules');
    await mkdir(join(dependencies, '.bin'), { recursive: true });
    await mkdir(join(dependencies, 'tool'));
    await writeFile(join(dependencies, 'tool/entry.js'), 'qualified executable');
    await writeFile(join(root, 'outside.js'), 'outside dependency root');
    const link = join(dependencies, '.bin/tool');
    await symlink('../../outside.js', link, 'file');
    await assert.rejects(snapshotVerificationInputs({ root }), /inside its qualified dependency root/);
    await rm(link);
    await symlink('../tool/missing.js', link, 'file');
    await assert.rejects(snapshotVerificationInputs({ root }), /ENOENT/);
    await rm(link);
    await symlink(join(dependencies, 'tool'), link, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(snapshotVerificationInputs({ root }), /ordinary file/);
    await rm(link);
    await symlink('tool/entry.js', join(dependencies, 'unqualified-link'), 'file');
    await assert.rejects(snapshotVerificationInputs({ root }), /unqualified symbolic link/);
  });

  it('supports artifact roots outside the checkout and rejects empty or linked artifact roots', async () => {
    const { root } = await fixture();
    const external = await mkdtemp(join(tmpdir(), 'pp-verification-artifact-'));
    roots.push(external);
    await assert.rejects(snapshotVerificationInputs({ root, artifactRoots: [external] }), /empty/);
    await writeFile(join(external, 'runtime.exe'), 'outside checkout');
    const evidence = await snapshotVerificationInputs({ root, artifactRoots: [external] });
    assert.equal(evidence.artifacts[0].root, external);
    assert.equal(evidence.artifacts[0].files[0].path, 'runtime.exe');
    const linked = join(root, 'artifact-link');
    await symlink(external, linked, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(snapshotVerificationInputs({ root, artifactRoots: [linked] }), /ordinary/);
  });

  it('stores only hashes of automatic and explicitly supplied environment values and detects changes', async () => {
    const { root } = await fixture();
    process.env[envName] = 'secret-auto-value';
    const options = { root, environment: { pgConnection: 'secret-pg-connection', sdk: '9.0.319', emailEnabled: false } };
    const before = await snapshotVerificationInputs(options);
    const serialized = JSON.stringify(before);
    for (const value of ['secret-auto-value', 'secret-pg-connection', '9.0.319']) assert.ok(!serialized.includes(value));
    assert.match(before.environment[envName], /^[a-f0-9]{64}$/);
    process.env[envName] = 'changed-secret';
    await assert.rejects(assertUnchanged(before, options), /environment/);
    process.env[envName] = 'secret-auto-value';
    await assert.rejects(assertUnchanged(before, { ...options, environment: { ...options.environment, emailEnabled: true } }), /environment/);
    await assertUnchanged(before, options);
  });

  it('rejects a modified previous manifest and missing explicitly tested documents', async () => {
    const { root } = await fixture();
    const before = await snapshotVerificationInputs({ root });
    before.repository.files[0].sha256 = '0'.repeat(64);
    await assert.rejects(assertUnchanged(before, { root }), /manifest was modified/);
    await assert.rejects(snapshotVerificationInputs({ root, testedDocs: ['docs/missing.md'] }), /ENOENT/);
    await assert.rejects(snapshotVerificationInputs({ root, testedDocs: ['../outside.md'] }), /inside its repository/);
    assert.equal(await readFile(join(root, 'docs/tested.md'), 'utf8'), 'Contract');
  });

  it('refreshes async external environment providers on every reuse check and propagates failed qualification', async () => {
    const { root } = await fixture();
    let identity = 'database-instance-and-browser-hash-1';
    let calls = 0;
    const options = { root, environment: async () => { calls++; return { identity }; } };
    const before = await snapshotVerificationInputs(options);
    await assertUnchanged(before, options);
    assert.equal(calls, 2);
    identity = 'database-instance-and-browser-hash-2';
    await assert.rejects(assertUnchanged(before, options), /environment/);
    assert.equal(calls, 3);
    assert.ok(!JSON.stringify(before).includes('database-instance-and-browser-hash-1'));
    await assert.rejects(snapshotVerificationInputs({ root, environment: async () => { throw new Error('database unavailable'); } }), /database unavailable/);
    await assert.rejects(snapshotVerificationInputs({ root, environment: async () => undefined }), /plain object/);
  });
});

function expectHash(value) { assert.match(value ?? '', /^[a-f0-9]{64}$/); }
