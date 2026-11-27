import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { allowedReuseDiff, boundedText, cacheSteps, compatibleRecipes, formatChecks, frontendChecks, parsedTools,
  previousQualification, producerRecipe, qualifiesProducer, reuseCondition, reviewedBrowserFixtureRevision, reviewedFormatOnlyPaths, trustedLogRedirect, validateReusePlumbing } from './qualified-check-reuse.mjs';
import { parseWorkflow } from './validate-deploy-workflow.mjs';

const currentSha = 'a'.repeat(40), oldSha = 'b'.repeat(40);
const now = Date.UTC(2026, 10, 6);
const tools = { node: '22.23.4', npm: '10.9.9', sdk: '9.0.320' };
function originalWorkflow() {
  const steps = names => names.map(name => ({ name, run: name.startsWith('Test FE guard') ? 'npm run test:ci'
    : name === 'Test FE integration' ? 'npm run test:integration' : name }));
  return { permissions: { contents: 'read' }, env: { NODE_OPTIONS: '--max-old-space-size=1536' }, jobs: {
    frontend: { 'runs-on': ['self-hosted', 'Windows', 'X64', 'payplanner-xsenus'], steps: [
      { name: 'Checkout', uses: 'actions/checkout@v6', with: { 'persist-credentials': false } },
      ...steps(['Lint FE', 'Typecheck FE', 'Audit FE packages', 'Verify source encoding', 'Verify system surface inventory', ...frontendChecks, 'Build FE'])] },
    'backend-format': { 'runs-on': ['self-hosted', 'Windows', 'X64', 'payplanner-xsenus'], steps: [
      { name: 'Checkout', uses: 'actions/checkout@v6', with: { 'persist-credentials': false } },
      { name: 'Verify full API semantic formatting', run: 'dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal' },
      { name: 'Verify all Migrations whitespace formatting', run: 'dotnet format whitespace backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic' },
      { name: 'Verify all Migrations style formatting', run: 'dotnet format style backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic' },
      { name: 'Verify all Migrations analyzer formatting', run: 'dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic' },
      { name: 'Verify full test shard tool semantic formatting', run: 'dotnet format tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --verify-no-changes --no-restore --verbosity diagnostic' },
      { name: 'Verify full storage probe semantic formatting', run: 'dotnet format tests/PayPlanner.StorageStageWriterProbe/PayPlanner.StorageStageWriterProbe.csproj --verify-no-changes --no-restore --verbosity diagnostic' },
      { name: 'Verify full Tests semantic formatting', run: 'dotnet format backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --verify-no-changes --no-restore --verbosity diagnostic' }] },
  } };
}
function withReuse(base = originalWorkflow()) {
  const result = structuredClone(base);
  result.permissions.actions = 'read';
  for (const [mode, id] of [['frontend', 'frontend'], ['format', 'backend-format']]) {
    result.jobs[id].steps[0].with['fetch-depth'] = 32;
    const first = result.jobs[id].steps.findIndex(step => (mode === 'frontend' ? frontendChecks : formatChecks).includes(step.name));
    result.jobs[id].steps.splice(first, 0, ...structuredClone(cacheSteps[mode]));
    for (const step of result.jobs[id].steps) if ((mode === 'frontend' ? frontendChecks : formatChecks).includes(step.name)) step.if = reuseCondition(id, step);
  }
  return result;
}
function yaml(value, indent = 0) {
  const pad = ' '.repeat(indent);
  return Object.entries(value).map(([key, item]) => {
    if (Array.isArray(item)) return pad + key + ':\n' + item.map(entry => {
      if (typeof entry !== 'object') return pad + '  - ' + JSON.stringify(entry) + '\n';
      const body = yaml(entry, indent + 4).split('\n');
      return pad + '  - ' + body[0].trimStart() + '\n' + body.slice(1).join('\n');
    }).join('');
    if (item && typeof item === 'object') return pad + key + ':\n' + yaml(item, indent + 2);
    return pad + key + ': ' + JSON.stringify(item) + '\n';
  }).join('');
}
function producer(mode = 'frontend') {
  const run = { id: 2, head_sha: oldSha, head_branch: 'main', event: 'push', path: '.github/workflows/deploy.yml', name: 'Deploy PayPlanner', run_attempt: 1 };
  const checks = mode === 'frontend' ? [...frontendChecks, 'Build FE', 'Lint FE', 'Typecheck FE', 'Audit FE packages']
    : [...formatChecks, 'Verify full Tests semantic formatting'];
  const job = { id: 21, run_id: 2, head_sha: oldSha, name: mode === 'frontend' ? 'frontend' : 'backend-format',
    status: 'completed', conclusion: 'success', runner_name: 'PayPlanner-XSENUS',
    started_at: new Date(now - 4_000_000).toISOString(), completed_at: new Date(now - 3_600_000).toISOString(),
    steps: checks.map(name => ({ name, status: 'completed', conclusion: 'success' })) };
  return { run, job, sha: oldSha, mode, runner: 'PayPlanner-XSENUS', tools: { ...tools }, log: { ...tools }, now, bootTime: now - 86_400_000 };
}

test('actual workflow has complete proof plumbing and fresh affected gates', () => {
  const workflow = parseWorkflow(readFileSync('scripts/fixtures/deploy-xsenus.yml', 'utf8'));
  validateReusePlumbing(workflow);
  assert.equal(workflow.jobs['backend-format'].steps.find(step => step.name === 'Verify full Tests semantic formatting').if, undefined);
  assert.equal(workflow.jobs.frontend.steps.find(step => step.name === 'Build FE').if, undefined);
});
test('only a bounded test/documentation/infrastructure diff is eligible', () => {
  assert.equal(allowedReuseDiff([]), true);
  assert.equal(allowedReuseDiff(['backend/PayPlanner.Api.Tests/New.cs', 'docs/checkpoint.md', 'AGENTS.md', '.github/workflows/deploy.yml']), true);
  assert.equal(allowedReuseDiff(['scripts/check-task-upload-live-browser.mjs', 'scripts/check-case-retirement-live-browser.mjs']), true);
  for (const path of ['src/App.tsx', 'backend/Controllers/ClientsController.cs', 'backend/AppReleases/releases.json', 'package-lock.json', 'global.json',
    '.editorconfig', 'scripts/prepare-deployment-plan.mjs', 'scripts/check-case-court-live-browser.mjs', 'backend/PayPlanner.Api.Tests/../Controllers/Bad.cs', 'docs/a.md\n', 'docs\\a.md', '/docs/a.md']) {
    assert.equal(allowedReuseDiff([path]), false, path);
  }
  assert.equal(allowedReuseDiff(Array(1001).fill('docs/a.md')), false);
});
for (const mode of ['frontend', 'format']) {
  test(mode + ': exact original recipe survives only reviewed additions', () => {
    const old = originalWorkflow(), current = withReuse(old);
    validateReusePlumbing(current);
    assert.equal(compatibleRecipes(old, current, mode), true);
    assert.deepEqual(producerRecipe(old, mode), producerRecipe(current, mode));
  });
  for (const mutation of ['command', 'env', 'action', 'condition', 'partial-proof', 'proof-command', 'proof-order', 'fresh-gate']) {
    test(mode + ': rejects recipe/proof tampering ' + mutation, () => {
      const old = originalWorkflow(), current = withReuse(old);
      const job = current.jobs[mode === 'frontend' ? 'frontend' : 'backend-format'];
      const name = (mode === 'frontend' ? frontendChecks : formatChecks)[0];
      if (mutation === 'command') job.steps.find(step => step.name === name).run = 'echo passed';
      if (mutation === 'env') current.env.NODE_OPTIONS = '--max-old-space-size=4096';
      if (mutation === 'action') job.steps[0].uses = 'actions/checkout@v5';
      if (mutation === 'condition') job.steps.find(step => step.name === name).if = 'true';
      if (mutation === 'partial-proof') job.steps.splice(job.steps.findIndex(step => step.id?.endsWith('-reuse')), 1);
      if (mutation === 'proof-command') job.steps.find(step => step.id?.endsWith('-reuse')).run += ' || true';
      if (mutation === 'proof-order') {
        const proof = job.steps.splice(job.steps.findIndex(step => step.id?.endsWith('-reuse')), 1)[0];
        job.steps.push(proof);
      }
      if (mutation === 'fresh-gate') job.steps.find(step => step.name === (mode === 'frontend' ? 'Build FE' : 'Verify full Tests semantic formatting')).if = 'false';
      let rejected = !compatibleRecipes(old, current, mode);
      try { validateReusePlumbing(current); } catch { rejected = true; }
      assert.equal(rejected, true);
    });
  }
}
test('parses exact official Node/npm/SDK versions and rejects ambiguity', () => {
  assert.deepEqual(parsedTools('2026-11-05Z node: v22.23.4\n2026-11-05Z npm: 10.9.9\nD:\\sdk\\9.0.320\\Sdks'), tools);
  assert.equal(parsedTools('sdk/9.0.319/Sdks sdk/9.0.320/Sdks').sdk, undefined);
  assert.equal(parsedTools('node: v22.14.0\nnode: v22.23.4\n').node, undefined);
  assert.equal(parsedTools('npm: 10.9.6\nnpm: 10.9.9\n').npm, undefined);
  assert.equal(parsedTools('missing').node, undefined);
});
test('HTTP proofs are bounded and a failed response is never success evidence', async () => {
  assert.equal(await boundedText(new Response('small proof')), 'small proof');
  await assert.rejects(boundedText(new Response('denied', { status: 403 })), /unavailable/);
  await assert.rejects(boundedText(new Response('x'.repeat(2 * 1024 * 1024 + 1))), /exceeds bound/);
});
test('missing Actions access selects the ordinary gate without a network request', { skip: process.platform !== 'win32' }, () => {
  const temporary = mkdtempSync(join(tmpdir(), 'payplanner-reuse-'));
  try {
    const output = join(temporary, 'output.txt');
    const env = { ...process.env, GITHUB_JOB: 'frontend', GITHUB_OUTPUT: output };
    delete env.GH_TOKEN;
    const stdout = execFileSync(process.execPath, ['scripts/qualified-check-reuse.mjs', 'frontend'],
      { env, encoding: 'utf8', timeout: 10_000, windowsHide: true });
    assert.equal(readFileSync(output, 'utf8'), 'reused=false\n');
    assert.match(stdout, /Reuse proof unavailable; ordinary gate required/);
  } finally { rmSync(temporary, { recursive: true, force: true }); }
});
for (const mode of ['frontend', 'format']) {
  test(mode + ': CLI with a token and invalid Actions context settles on the ordinary gate', { skip: process.platform !== 'win32' }, () => {
    const temporary = mkdtempSync(join(tmpdir(), 'payplanner-reuse-cli-'));
    try {
      const output = join(temporary, 'output.txt');
      // Enter the actual qualification path without any authenticated network
      // request. Import-only tests cannot detect an entry-point await cycle.
      const env = { ...process.env, GH_TOKEN: 'synthetic-invalid-context', GITHUB_ACTIONS: 'false',
        GITHUB_JOB: mode === 'frontend' ? 'frontend' : 'backend-format', GITHUB_OUTPUT: output };
      const stdout = execFileSync(process.execPath, ['scripts/qualified-check-reuse.mjs', mode],
        { env, encoding: 'utf8', timeout: 10_000, windowsHide: true });
      assert.equal(readFileSync(output, 'utf8'), 'reused=false\n');
      assert.match(stdout, /Reuse proof unavailable; ordinary gate required/);
    } finally { rmSync(temporary, { recursive: true, force: true }); }
  });
}
for (const mode of ['frontend', 'format']) {
  test(mode + ': requires an actual complete producer with equivalent tools', () => assert.equal(qualifiesProducer(producer(mode)), true));
  for (const mutation of ['branch', 'event', 'head', 'path', 'job-run', 'job-head', 'name', 'runner', 'status', 'outcome', 'skipped-check',
    'missing-check', 'duplicate-check', 'stale', 'future', 'reboot', 'node', 'tool', 'missing-tools']) {
    test(mode + ': rejects unqualified producer ' + mutation, () => {
      const row = structuredClone(producer(mode));
      if (mutation === 'branch') row.run.head_branch = 'feature/untrusted';
      if (mutation === 'event') row.run.event = 'pull_request';
      if (mutation === 'head') row.run.head_sha = currentSha;
      if (mutation === 'path') row.run.path = '.github/workflows/other.yml';
      if (mutation === 'job-run') row.job.run_id++;
      if (mutation === 'job-head') row.job.head_sha = currentSha;
      if (mutation === 'name') row.job.name = 'other';
      if (mutation === 'runner') row.job.runner_name = 'other';
      if (mutation === 'status') row.job.status = 'in_progress';
      if (mutation === 'outcome') row.job.conclusion = 'failure';
      if (mutation === 'skipped-check') row.job.steps[0].conclusion = 'skipped';
      if (mutation === 'missing-check') row.job.steps.pop();
      if (mutation === 'duplicate-check') row.job.steps.push({ ...row.job.steps[0], conclusion: 'skipped' });
      if (mutation === 'stale') row.job.completed_at = new Date(now - 86_400_001).toISOString();
      if (mutation === 'future') row.job.completed_at = new Date(now + 60_001).toISOString();
      if (mutation === 'reboot') row.bootTime = now;
      if (mutation === 'node') row.log.node = '22.14.0';
      if (mutation === 'tool') row.log[mode === 'frontend' ? 'npm' : 'sdk'] = '0.0.0';
      if (mutation === 'missing-tools') row.log = {};
      assert.equal(qualifiesProducer(row), false);
    });
  }
}
test('private log redirects never forward credentials to arbitrary origins', () => {
  assert.equal(trustedLogRedirect('https://results.blob.core.windows.net/log?sig=opaque'), 'https://results.blob.core.windows.net/log?sig=opaque');
  for (const url of ['http://results.blob.core.windows.net/log', 'https://evil.test/log', 'https://u:p@results.blob.core.windows.net/log',
    'https://results.blob.core.windows.net.evil.test/log', 'https://results.blob.core.windows.net:444/log']) assert.throws(() => trustedLogRedirect(url));
});
function contextFixture(mode = 'frontend', alter = () => {}) {
  const sample = producer(mode);
  const env = { GITHUB_ACTIONS: 'true', COMPUTERNAME: 'XSENUS', GITHUB_REPOSITORY: 'Xsenus/PayPlanner', GITHUB_REF: 'refs/heads/main',
    GITHUB_SHA: currentSha, GITHUB_WORKFLOW_SHA: currentSha, GITHUB_RUN_ID: '3', GITHUB_RUN_ATTEMPT: '1', GITHUB_EVENT_NAME: 'push',
    GITHUB_JOB: sample.job.name, RUNNER_NAME: sample.runner };
  const fixture = { sample, env, paths: ['backend/PayPlanner.Api.Tests/Changed.cs'], old: originalWorkflow(), current: withReuse(),
    dirty: false, ignoredEnvironment: false, ancestor: true, currentRunStatus: 'in_progress', currentJobStatus: 'in_progress', logs: 'node: v22.23.4\nnpm: 10.9.9\nsdk/9.0.320/Sdks\n' };
  alter(fixture);
  const git = args => {
    if (args[0] === 'rev-parse') return currentSha;
    if (args[0] === 'status') {
      assert.ok(args.includes('--untracked-files=normal'));
      return fixture.dirty ? ' M src/App.tsx' : '';
    }
    if (args[0] === 'ls-files') {
      assert.deepEqual(args, ['ls-files', '--others', '--ignored', '--exclude-standard', '--', '.env', '.env.*']);
      return fixture.ignoredEnvironment ? '.env.local' : '';
    }
    if (args[0] === 'merge-base') { if (!fixture.ancestor) throw new Error('not ancestor'); return ''; }
    if (args[0] === 'diff') {
      assert.ok(args.includes('--no-renames') && args.includes('--no-ext-diff') && args.includes('--no-textconv'));
      return fixture.paths.join('\0') + '\0';
    }
    if (args[0] === 'show') return yaml(args[1].startsWith(currentSha) ? fixture.current : fixture.old);
    throw new Error('Unexpected git operation');
  };
  const getJson = async path => {
    if (path === '/actions/runs/3') return { ...sample.run, id: 3, run_attempt: 1, head_sha: currentSha, status: fixture.currentRunStatus };
    if (path.startsWith('/actions/runs/3/')) return { total_count: 1, jobs: [{ ...sample.job, run_id: 3, head_sha: currentSha, status: fixture.currentJobStatus }] };
    if (path.startsWith('/actions/runs?')) return { workflow_runs: [sample.run] };
    if (path.startsWith('/actions/runs/2/')) return { total_count: 1, jobs: [sample.job] };
    throw new Error('Unexpected proof request');
  };
  return { env, git, getJson, getLog: async () => fixture.logs, tools, now, bootTime: now - 86_400_000 };
}
for (const path of reviewedFormatOnlyPaths) {
  for (const mode of ['frontend', 'format']) for (const reviewed of [false, true]) {
    test(mode + ': format-independent path requires its exact reviewed input graph ' + path + ' ' + reviewed, async () => {
      const fixture = contextFixture(mode, value => {
        value.paths = [path];
        if (reviewed) {
          value.sample.run.head_sha = reviewedBrowserFixtureRevision;
          value.sample.job.head_sha = reviewedBrowserFixtureRevision;
        }
      });
      assert.equal((await previousQualification(fixture)).reused, mode === 'format' && reviewed);
    });
  }
}
for (const path of ['backend/PayPlanner.Api.csproj', 'backend/Controllers/CasesV2Controller.cs', 'Directory.Build.props',
  '.editorconfig', 'src/Other.tsx', 'scripts/Other.mjs']) {
  test('format: unreviewed input still rejects reuse ' + path, async () => {
    const fixture = contextFixture('format', value => {
      value.paths = [path];
      value.sample.run.head_sha = reviewedBrowserFixtureRevision;
      value.sample.job.head_sha = reviewedBrowserFixtureRevision;
    });
    assert.equal((await previousQualification(fixture)).reused, false);
  });
}
test('an unknown reuse mode never receives a diff admission', () => assert.equal(allowedReuseDiff([], 'other'), false));
for (const mode of ['frontend', 'format']) {
  for (const reviewed of [true, false]) {
    test(mode + ': isolated browser drivers require the reviewed consumer revision ' + reviewed, async () => {
      const fixture = contextFixture(mode, value => {
        value.paths = ['scripts/check-task-upload-live-browser.mjs', 'scripts/check-case-retirement-live-browser.mjs'];
        if (reviewed) {
          value.sample.run.head_sha = reviewedBrowserFixtureRevision;
          value.sample.job.head_sha = reviewedBrowserFixtureRevision;
        }
      });
      const result = await previousQualification(fixture);
      assert.equal(result.reused, reviewed);
      if (reviewed) assert.equal(result.sourceRevision, reviewedBrowserFixtureRevision);
    });
  }
  test(mode + ': official producer and source proof qualify together', async () => {
    const result = await previousQualification(contextFixture(mode));
    assert.equal(result.reused, true);
    assert.equal(result.sourceRevision, oldSha);
    assert.equal(result.currentRevision, currentSha);
    assert.equal(result.sourceJobId, 21);
    assert.match(result.decodedLogSha256, /^[a-f0-9]{64}$/);
  });
  test(mode + ': pending run requires its actual active producer', async () => {
    const fixture = contextFixture(mode, value => { value.currentRunStatus = 'pending'; });
    assert.equal((await previousQualification(fixture)).reused, true);
    const waiting = contextFixture(mode, value => { value.currentRunStatus = 'pending'; value.currentJobStatus = 'pending'; });
    await assert.rejects(previousQualification(waiting), /Actual current producer/);
  });
  for (const mutation of ['dirty', 'ignored-environment-file', 'ancestry', 'production-source', 'renamed-production-source', 'untracked-environment', 'tools', 'job-failed', 'workflow-command', 'current-run', 'current-job']) {
    test(mode + ': whole proof fails closed ' + mutation, async () => {
      const fixture = contextFixture(mode, value => {
        if (mutation === 'dirty') value.dirty = true;
        if (mutation === 'ignored-environment-file') value.ignoredEnvironment = true;
        if (mutation === 'ancestry') value.ancestor = false;
        if (mutation === 'production-source') value.paths.push('backend/Controllers/ClientsController.cs');
        if (mutation === 'renamed-production-source') value.paths = ['src/Removed.tsx', 'backend/PayPlanner.Api.Tests/Removed.tsx'];
        if (mutation === 'untracked-environment') value.env.VITE_OTHER_API = 'https://wrong.invalid/';
        if (mutation === 'tools') value.logs = 'node: v0.0.0\n';
        if (mutation === 'job-failed') value.sample.job.conclusion = 'failure';
        if (mutation === 'workflow-command') value.current.jobs[value.env.GITHUB_JOB].steps.at(-1).run += ' || true';
        if (mutation === 'current-run') value.currentRunStatus = 'completed';
        if (mutation === 'current-job') value.currentJobStatus = 'completed';
      });
      let reused = false;
      try { reused = (await previousQualification(fixture)).reused; } catch { /* Refusal is the safe outcome. */ }
      assert.equal(reused, false);
    });
  }
}
