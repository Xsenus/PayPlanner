import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync } from 'node:fs';
import { uptime } from 'node:os';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseWorkflow } from './validate-deploy-workflow.mjs';

const expression = value => '$' + '{{ ' + value + ' }}';
export const frontendChecks = ['Test FE guard and unit', 'Test FE integration'];
export const formatChecks = ['Verify full API semantic formatting', 'Verify all Migrations whitespace formatting',
  'Verify all Migrations style formatting', 'Verify all Migrations analyzer formatting',
  'Verify full test shard tool semantic formatting', 'Verify full storage probe semantic formatting'];
export const reusableCommands = Object.freeze({
  'Test FE guard and unit': 'npm run test:ci',
  'Test FE integration': 'npm run test:integration',
  'Verify full API semantic formatting': 'dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal',
  'Verify all Migrations whitespace formatting': 'dotnet format whitespace backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'Verify all Migrations style formatting': 'dotnet format style backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'Verify all Migrations analyzer formatting': 'dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'Verify full test shard tool semantic formatting': 'dotnet format tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'Verify full storage probe semantic formatting': 'dotnet format tests/PayPlanner.StorageStageWriterProbe/PayPlanner.StorageStageWriterProbe.csproj --verify-no-changes --no-restore --verbosity diagnostic',
});
export const cacheSteps = Object.freeze({
  frontend: [
    { name: 'Test qualified check reuse contract', run: 'node --test scripts/qualified-check-reuse.node-tests.mjs' },
    { name: 'Reuse qualified frontend test evidence', id: 'frontend-reuse', env: { GH_TOKEN: expression('github.token') },
      run: 'node scripts/qualified-check-reuse.mjs frontend' },
    { name: 'Test current tooling with reused UI evidence', if: expression("steps.frontend-reuse.outputs.reused == 'true'"),
      run: 'npx vitest run scripts --exclude "**/*.integration.test.*" --maxWorkers=1' },
  ],
  format: [
    { name: 'Reuse qualified unchanged formatting evidence', id: 'format-reuse', env: { GH_TOKEN: expression('github.token') },
      run: 'node scripts/qualified-check-reuse.mjs format' },
  ],
});
const allowedInfrastructure = new Set(['.github/workflows/deploy.yml', 'scripts/qualified-check-reuse.mjs',
  'scripts/qualified-check-reuse.node-tests.mjs', 'scripts/validate-deploy-workflow.mjs', 'scripts/validate-deploy-workflow.test.ts']);
// These are backend browser drivers, not application inputs in this reviewed
// consumer revision. Never transfer that assumption to a different producer.
export const reviewedBrowserFixtureRevision = '84d7664d925db697942db71c779c99fe4fea4d4c';
const isolatedBrowserFixtures = new Set(['scripts/check-task-upload-live-browser.mjs', 'scripts/check-case-retirement-live-browser.mjs']);
// Reviewed against that same producer's C# project graph: none of these files
// is a source, analyzer input or build setting for the six reusable format checks.
// The changed test project still receives its complete fresh formatting gate.
export const reviewedFormatOnlyPaths = Object.freeze(['src/App.tsx', 'src/App.calendar-navigation.test.tsx',
  'src/components/Cases/CaseDetailPage.tsx', 'src/components/Cases/CaseDetailPage.test.tsx',
  'backend/AppReleases/releases.json', 'scripts/deploy-db-proof.py', 'scripts/deploy-roadmap-candidate-review.json',
  'scripts/deploy-vps-admission.test.py', 'scripts/deployment-baseline.json', 'scripts/prepare-deployment-plan.mjs',
  'scripts/prepare-deployment-plan.node-tests.mjs']);
const formatOnlyPaths = new Set(reviewedFormatOnlyPaths);
const hash = value => createHash('sha256').update(value).digest('hex');

export function allowedReuseDiff(paths, mode = 'frontend') {
  return ['frontend', 'format'].includes(mode) && Array.isArray(paths) && paths.length <= 1000 && paths.every(path => typeof path === 'string'
    && path.length <= 400 && !/[\x00-\x1f\x7f\\:]/.test(path)
    && !path.split('/').some(part => !part || part === '.' || part === '..')
    && (path.startsWith('backend/PayPlanner.Api.Tests/') || path === 'README.md' || path === 'AGENTS.md'
      || path.startsWith('docs/') && path.endsWith('.md') || allowedInfrastructure.has(path) || isolatedBrowserFixtures.has(path)
      || mode === 'format' && formatOnlyPaths.has(path)));
}

export function reuseCondition(job, step) {
  if (job === 'frontend' && frontendChecks.includes(step.name)) return expression("steps.frontend-reuse.outputs.reused != 'true'");
  if (job === 'backend-format' && formatChecks.includes(step.name)) return expression("steps.format-reuse.outputs.reused != 'true'");
  return undefined;
}

export function validateReuseStep(job, step) {
  const mode = job === 'frontend' ? 'frontend' : job === 'backend-format' ? 'format' : null;
  if (!mode) return false;
  return cacheSteps[mode].some(expected => JSON.stringify(step) === JSON.stringify(expected))
    || step.if === reuseCondition(job, step) && reuseCondition(job, step) !== undefined;
}

export function validateReusePlumbing(workflow) {
  for (const mode of ['frontend', 'format']) {
    const jobId = mode === 'frontend' ? 'frontend' : 'backend-format';
    const job = workflow.jobs[jobId];
    const expected = cacheSteps[mode];
    const additions = job.steps.filter(step => expected.some(value => value.name === step.name || value.id && value.id === step.id));
    const conditions = job.steps.filter(step => step.if !== undefined && reuseCondition(jobId, step) !== undefined);
    if (!additions.length && !conditions.length) continue;
    assert.equal(workflow.permissions?.actions, 'read', 'Read-only official Actions evidence required');
    assert.equal(additions.length, expected.length, 'All reuse proof steps required');
    for (const step of expected) assert.equal(additions.filter(value => JSON.stringify(value) === JSON.stringify(step)).length, 1, 'Exact reuse proof step required');
    const positions = expected.map(value => job.steps.findIndex(step => step.name === value.name));
    assert.ok(positions.every((value, index) => index === 0 || value > positions[index - 1]), 'Proof and fresh tooling must run in order');
    const proofPosition = job.steps.findIndex(step => step.id === (mode === 'frontend' ? 'frontend-reuse' : 'format-reuse'));
    for (const name of mode === 'frontend' ? frontendChecks : formatChecks) {
      const matches = job.steps.filter(step => step.name === name);
      assert.equal(matches.length, 1, 'Exactly one original verification step required');
      assert.equal(matches[0].run, reusableCommands[name], 'Original unfiltered check command required');
      assert.equal(matches[0].if, reuseCondition(jobId, matches[0]), 'Proof-bound reuse condition required');
      assert.ok(job.steps.indexOf(matches[0]) > proofPosition, 'Proof must precede reused checks');
    }
    const fresh = mode === 'frontend' ? ['Lint FE', 'Typecheck FE', 'Audit FE packages', 'Verify source encoding', 'Verify system surface inventory', 'Build FE']
      : ['Verify full Tests semantic formatting'];
    for (const name of fresh) {
      const step = job.steps.find(value => value.name === name);
      assert.ok(step && step.if === undefined, 'Affected and fresh gates cannot be conditional');
    }
  }
}

// Compare the complete original producer recipe after removing only reviewed
// cache plumbing. Unknown conditions, commands, environment or actions fail.
export function producerRecipe(workflow, mode) {
  const jobId = mode === 'frontend' ? 'frontend' : 'backend-format';
  const job = structuredClone(workflow.jobs[jobId]);
  const additions = cacheSteps[mode];
  const observed = new Set();
  job.steps = job.steps.filter(step => {
    const expected = additions.find(value => value.name === step.name);
    if (!expected) return true;
    assert.deepEqual(step, expected, 'Unreviewed reuse step');
    assert.ok(!observed.has(step.name), 'Duplicate reuse step');
    observed.add(step.name);
    return false;
  }).map(step => {
    const condition = reuseCondition(jobId, step);
    if (step.if !== undefined && condition !== undefined) {
      assert.equal(step.if, condition, 'Unreviewed reuse condition');
      delete step.if;
    }
    if (step.uses === 'actions/checkout@v6' && step.with?.['fetch-depth'] !== undefined) {
      assert.equal(String(step.with['fetch-depth']), '32', 'Bounded ancestor checkout required');
      delete step.with['fetch-depth'];
    }
    return step;
  });
  assert.ok(observed.size === 0 || observed.size === additions.length, 'Incomplete reuse proof plumbing');
  return { env: workflow.env, job };
}

export function compatibleRecipes(oldWorkflow, currentWorkflow, mode) {
  try {
    assert.deepEqual(producerRecipe(currentWorkflow, mode), producerRecipe(oldWorkflow, mode));
    return true;
  } catch { return false; }
}

export function parsedTools(log) {
  const text = log.replace(/\u001b\[[0-9;]*m/g, '').replace(/^\uFEFF/g, '');
  const uniqueVersion = pattern => {
    const values = [...new Set([...text.matchAll(pattern)].map(match => match[1]))];
    return values.length === 1 ? values[0] : undefined;
  };
  const node = uniqueVersion(/(?:^|\n)[^\n]*?\bnode: v([0-9]+\.[0-9]+\.[0-9]+)[ \t]*(?=\r?\n|$)/g);
  const npm = uniqueVersion(/(?:^|\n)[^\n]*?\bnpm: ([0-9]+\.[0-9]+\.[0-9]+)[ \t]*(?=\r?\n|$)/g);
  const sdk = [...new Set([...text.matchAll(/\bsdk[/\\](9\.[0-9]+\.[0-9]+)[/\\]/g)].map(match => match[1]))];
  return { node, npm, sdk: sdk.length === 1 ? sdk[0] : undefined };
}

export function qualifiesProducer({ run, job, sha, mode, runner, tools, log, now, bootTime }) {
  if (!run || !job || !tools || !log || !Array.isArray(job.steps)) return false;
  const checks = mode === 'frontend' ? [...frontendChecks, 'Build FE', 'Lint FE', 'Typecheck FE', 'Audit FE packages']
    : [...formatChecks, 'Verify full Tests semantic formatting'];
  const completed = Date.parse(job.completed_at);
  return run.head_sha === sha && run.head_branch === 'main' && run.event === 'push'
    && run.path === '.github/workflows/deploy.yml' && run.name === 'Deploy PayPlanner'
    && job.run_id === run.id && job.head_sha === sha && job.name === (mode === 'frontend' ? 'frontend' : 'backend-format')
    && job.status === 'completed' && job.conclusion === 'success' && job.runner_name === runner
    && Number.isFinite(completed) && completed <= now + 60_000 && now - completed <= 24 * 60 * 60 * 1000
    && Number.isFinite(Date.parse(job.started_at)) && bootTime <= Date.parse(job.started_at) + 60_000
    && checks.every(name => {
      const matching = job.steps.filter(step => step.name === name);
      return matching.length === 1 && matching[0].status === 'completed' && matching[0].conclusion === 'success';
    })
    && /^[0-9]+\.[0-9]+\.[0-9]+$/.test(log.node ?? '') && tools.node === log.node
    && (mode === 'frontend' ? /^[0-9]+\.[0-9]+\.[0-9]+$/.test(log.npm ?? '') && tools.npm === log.npm
      : /^9\.[0-9]+\.[0-9]+$/.test(log.sdk ?? '') && tools.sdk === log.sdk);
}

export function trustedLogRedirect(location) {
  const url = new URL(location);
  assert.equal(url.protocol, 'https:');
  assert.equal(url.port, '');
  assert.ok(url.hostname.endsWith('.blob.core.windows.net') || url.hostname.endsWith('.actions.githubusercontent.com'));
  assert.ok(!url.username && !url.password);
  return url.href;
}

export async function boundedText(response) {
  assert.ok(response.ok, 'GitHub proof unavailable');
  const reader = response.body.getReader();
  const chunks = []; let bytes = 0;
  for (;;) {
    const part = await reader.read();
    if (part.done) break;
    bytes += part.value.byteLength;
    if (bytes > 2 * 1024 * 1024) {
      await reader.cancel();
      throw new Error('GitHub proof exceeds bound');
    }
    chunks.push(Buffer.from(part.value));
  }
  return Buffer.concat(chunks).toString('utf8');
}

export async function previousQualification({ env = process.env, git, getJson, getLog, tools, now = Date.now(), bootTime = now - uptime() * 1000 }) {
  const mode = env.GITHUB_JOB === 'frontend' ? 'frontend' : env.GITHUB_JOB === 'backend-format' ? 'format' : null;
  assert.ok(mode && env.GITHUB_ACTIONS === 'true' && env.COMPUTERNAME?.toUpperCase() === 'XSENUS'
    && env.GITHUB_REPOSITORY === 'Xsenus/PayPlanner' && env.GITHUB_REF === 'refs/heads/main'
    && env.GITHUB_SHA === env.GITHUB_WORKFLOW_SHA && /^[a-f0-9]{40}$/.test(env.GITHUB_SHA)
    && /^[1-9][0-9]{0,14}$/.test(env.GITHUB_RUN_ID ?? '') && /^[1-9][0-9]{0,3}$/.test(env.GITHUB_RUN_ATTEMPT ?? '')
    && /^(push|workflow_dispatch)$/.test(env.GITHUB_EVENT_NAME), 'Authenticated main context required');
  assert.equal(git(['rev-parse', 'HEAD']).trim(), env.GITHUB_SHA);
  assert.equal(git(['status', '--porcelain=v1', '--untracked-files=normal']).trim(), '', 'Working inputs changed');
  assert.equal(git(['ls-files', '--others', '--ignored', '--exclude-standard', '--', '.env', '.env.*']).trim(), '', 'Unpinned local environment file');
  const activeRun = await getJson('/actions/runs/' + env.GITHUB_RUN_ID);
  assert.ok(activeRun.id === Number(env.GITHUB_RUN_ID) && activeRun.run_attempt === Number(env.GITHUB_RUN_ATTEMPT)
    && activeRun.head_sha === env.GITHUB_SHA && activeRun.head_branch === 'main'
    && activeRun.path === '.github/workflows/deploy.yml' && activeRun.name === 'Deploy PayPlanner'
    && activeRun.event === env.GITHUB_EVENT_NAME && ['in_progress', 'pending'].includes(activeRun.status), 'Actual current Actions run required');
  const activeJobs = await getJson('/actions/runs/' + env.GITHUB_RUN_ID + '/attempts/' + env.GITHUB_RUN_ATTEMPT + '/jobs?per_page=100');
  assert.ok(activeJobs.total_count <= 100 && activeJobs.jobs.filter(job => job.name === env.GITHUB_JOB
    && job.head_sha === env.GITHUB_SHA && job.run_id === activeRun.id && job.status === 'in_progress'
    && job.runner_name === env.RUNNER_NAME).length === 1, 'Actual current producer job required');
  const current = parseWorkflow(git(['show', env.GITHUB_SHA + ':.github/workflows/deploy.yml']));
  validateReusePlumbing(current);
  for (const key of Object.keys(env).filter(key => /^(?:VITE_|VITEST_|NODE_ENV$|NODE_PATH$|TZ$|BABEL_ENV$|BROWSERSLIST_ENV$)/.test(key))) {
    assert.equal(env[key], current.jobs[env.GITHUB_JOB].env?.[key] ?? current.env?.[key], 'Unpinned test environment input');
  }
  const runs = await getJson('/actions/runs?event=push&branch=main&per_page=30');
  assert.ok(Array.isArray(runs.workflow_runs) && runs.workflow_runs.length <= 30);
  for (const run of runs.workflow_runs) {
    if (run.head_sha === env.GITHUB_SHA || !/^[a-f0-9]{40}$/.test(run.head_sha ?? '')
      || !Number.isSafeInteger(run.id) || run.id <= 0 || !Number.isSafeInteger(run.run_attempt) || run.run_attempt < 1 || run.run_attempt > 1000) continue;
    try {
      git(['merge-base', '--is-ancestor', run.head_sha, env.GITHUB_SHA]);
      const paths = git(['diff', '--no-renames', '--no-ext-diff', '--no-textconv', '--name-only', '-z', run.head_sha, env.GITHUB_SHA]).split('\0').filter(Boolean);
      if (!allowedReuseDiff(paths, mode)) continue;
      if (paths.some(path => isolatedBrowserFixtures.has(path)) && run.head_sha !== reviewedBrowserFixtureRevision) continue;
      if (paths.some(path => formatOnlyPaths.has(path)) && run.head_sha !== reviewedBrowserFixtureRevision) continue;
      const old = parseWorkflow(git(['show', run.head_sha + ':.github/workflows/deploy.yml']));
      if (!compatibleRecipes(old, current, mode)) continue;
      for (let attempt = run.run_attempt; attempt >= Math.max(1, run.run_attempt - 4); attempt--) {
        const result = await getJson('/actions/runs/' + run.id + '/attempts/' + attempt + '/jobs?per_page=100');
        if (!Array.isArray(result.jobs) || result.jobs.length > 100 || result.total_count > 100) continue;
        const job = result.jobs.find(value => value.name === env.GITHUB_JOB && value.conclusion === 'success');
        if (!job || !Number.isSafeInteger(job.id) || job.id <= 0) continue;
        const raw = await getLog(job.id);
        if (!qualifiesProducer({ run, job, sha: run.head_sha, mode, runner: env.RUNNER_NAME, tools, log: parsedTools(raw), now, bootTime })) continue;
        return { reused: true, mode, sourceRevision: run.head_sha, sourceRunId: run.id, sourceAttempt: attempt, sourceJobId: job.id,
          currentRevision: env.GITHUB_SHA, currentRunId: activeRun.id, currentAttempt: activeRun.run_attempt,
          changedPaths: paths, decodedLogSha256: hash(raw), recipeSha256: hash(JSON.stringify(producerRecipe(current, mode))),
          tools, evidence: 'Actual prior GitHub success with equivalent influencing inputs; no local check is labeled CI.' };
      }
    } catch { /* An unavailable or incompatible ancestor is never qualification. */ }
  }
  return { reused: false, mode, reason: 'No verified equivalent prior producer' };
}

async function main() {
  const mode = process.argv[2];
  assert.ok(process.argv.length === 3 && ['frontend', 'format'].includes(mode));
  assert.equal(process.platform, 'win32');
  assert.equal(process.env.GITHUB_JOB, mode === 'frontend' ? 'frontend' : 'backend-format');
  const token = process.env.GH_TOKEN;
  const api = 'https://api.github.com/repos/Xsenus/PayPlanner';
  const headers = { Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' };
  const request = path => fetch(api + path, { headers, redirect: 'manual', signal: AbortSignal.timeout(20_000) });
  const getJson = async path => JSON.parse(await boundedText(await request(path)));
  const getLog = async id => {
    const response = await request('/actions/jobs/' + id + '/logs');
    if (response.status === 200) return boundedText(response);
    assert.equal(response.status, 302);
    return boundedText(await fetch(trustedLogRedirect(response.headers.get('location')), { redirect: 'error', signal: AbortSignal.timeout(20_000) }));
  };
  const childEnv = { ...process.env };
  delete childEnv.GH_TOKEN;
  const git = args => execFileSync('git', args, { env: childEnv, encoding: 'utf8', windowsHide: true, timeout: 20_000, maxBuffer: 4 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
  let result;
  try {
    assert.ok(token, 'Read-only Actions access required');
    const npm = execFileSync(process.execPath, [resolve(dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js'), '--version'],
      { env: childEnv, encoding: 'utf8', windowsHide: true, timeout: 20_000 }).trim();
    const tools = { node: process.versions.node, npm, sdk: mode === 'format' ? execFileSync('dotnet', ['--version'], { env: childEnv, encoding: 'utf8', windowsHide: true, timeout: 20_000 }).trim() : undefined };
    result = await previousQualification({ git, getJson, getLog, tools });
  }
  catch { result = { reused: false, mode, reason: 'Reuse proof unavailable; ordinary gate required' }; }
  assert.ok(process.env.GITHUB_OUTPUT);
  appendFileSync(process.env.GITHUB_OUTPUT, 'reused=' + String(result.reused) + '\n');
  console.log('QUALIFIED_CHECK_REUSE ' + JSON.stringify(result));
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) await main();
