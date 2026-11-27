import assert from 'node:assert/strict';

const expression = value => '${{ ' + value + ' }}';
const releaseCondition = expression("success() && github.ref == 'refs/heads/main' && (github.event_name == 'push' || (github.event_name == 'workflow_dispatch' && inputs.release_to_vps == true))");
const xsenus = ['self-hosted', 'Windows', 'X64', 'payplanner-xsenus'];
const shardJobs = [0, 1, 2, 3].map(index => `backend-test-${index}`);
const prerequisites = ['frontend', 'frontend-integration', 'backend-format', 'backend-build'];
const outputs = Object.freeze({
  'frontend-artifact-id': 'frontend',
  'frontend-sha': 'frontend',
  'backend-key': 'backend-build',
  'backend-sha': 'backend-build',
  'manifest-sha': 'backend-build',
  'sdk-version': 'backend-build',
  'package-manifest-sha': 'package',
});

function step(job, name) {
  const matches = job.steps.filter(candidate => candidate.name === name);
  assert.equal(matches.length, 1, `Expected one step named ${name}`);
  return matches[0];
}

function requireCommand(job, name, run) {
  assert.equal(step(job, name).run, run, `Expected the original command for ${name}`);
}

function requireDependency(jobs, dependencies) {
  assert.deepEqual(Object.keys(jobs).sort(), Object.keys(dependencies).sort(), 'Preserve all mandatory jobs');
  for (const [name, needs] of Object.entries(dependencies)) {
    assert.deepEqual(jobs[name].needs ?? [], needs, `Release dependencies differ for ${name}`);
  }
}

function verifyHostedJob(job, platform) {
  assert.equal(job['runs-on'], platform);
  assert.equal(job.if, undefined, 'Hosted checks cannot be optional');
  for (const candidate of job.steps) {
    assert.equal(candidate['continue-on-error'], undefined);
    assert.equal(candidate['working-directory'], undefined);
    if (candidate.if !== undefined) {
      assert.ok(candidate.name === 'Report bounded execution diagnostics' && candidate.if === 'always()'
        || ['Preserve exact shard results', 'Preserve restored browser results'].includes(candidate.name)
        && candidate.if === expression('!cancelled()'), 'Unexpected condition on required hosted step');
    }
    if (candidate.uses?.startsWith('actions/checkout@')) {
      assert.equal(candidate.with?.['persist-credentials'], false);
      assert.equal(candidate.with?.ref, undefined);
      assert.equal(candidate.with?.repository, undefined);
    }
  }
}

function ensureReadOnly(workflow) {
  assert.equal(workflow.permissions?.contents, 'read');
  assert.ok(Object.entries(workflow.permissions).every(([key, value]) => ['contents', 'actions'].includes(key) && value === 'read'));
}

function verifyHostedCall(workflow, jobs) {
  ensureReadOnly(workflow);
  assert.deepEqual(Object.keys(workflow.on), ['workflow_call']);
  assert.equal(workflow.env.BackgroundServices__Enabled, 'false');
  assert.equal(workflow.env.Email__Enabled, 'false');
  assert.equal(workflow.env.PAYPLANNER_CI_ARTIFACT_ROOT, undefined);
  const definition = JSON.stringify(workflow);
  assert.ok(!/secrets\.|secrets":|pull_request_target|continue-on-error/.test(definition), 'Hosted jobs require a secret-free, fail-closed workflow');
  for (const key of ['DOTNET_PROCESSOR_COUNT', 'NODE_OPTIONS', 'UseSharedCompilation', 'DOTNET_gcServer', 'DOTNET_GCConserveMemory']) {
    assert.equal(workflow.env[key], jobs.env[key], `Preserve the reviewed resource limit ${key}`);
  }
  assert.equal(workflow.env.PayPlannerSkipMigrationsBuild, undefined);
}

function checkCachedTransfer(job, name, path, key) {
  const receipt = step(job, name);
  assert.equal(receipt.uses, 'actions/cache/restore@v5');
  assert.deepEqual({ ...receipt.with }, { path, key, 'fail-on-cache-miss': true }, 'Restore only the exact producer key');
}

export function validateHostedRelease({ entry, checks, shard, legacy }) {
  const guarded = entry.jobs;
  const hosted = checks.jobs;
  ensureReadOnly(entry);
  assert.deepEqual(Object.keys(entry.on).sort(), ['push', 'workflow_dispatch']);
  assert.deepEqual(entry.on.push.branches, ['main', 'feature/ci-*']);
  assert.deepEqual(entry.on.push['paths-ignore'], legacy.on.push['paths-ignore']);
  assert.deepEqual(entry.on.workflow_dispatch, legacy.on.workflow_dispatch);
  assert.equal(entry.concurrency.group, expression("github.ref == 'refs/heads/main' && 'payplanner-deploy' || github.ref"));
  assert.equal(entry.concurrency['cancel-in-progress'], false);
  assert.deepEqual(entry.env, legacy.env);
  requireDependency(guarded, {
    'backend-format': [], 'deploy-access': [], frontend: ['backend-format', 'deploy-access'],
    'backend-build': ['backend-format', 'deploy-access'], backend: ['backend-format', 'backend-build'],
    deploy: ['frontend', 'backend-format', 'backend'],
  });
  assert.equal(guarded['backend-format'].uses, './.github/workflows/release-checks.yml');
  assert.deepEqual(Object.keys(guarded['backend-format']).sort(), ['name', 'uses']);
  for (const name of ['deploy-access', 'frontend', 'backend-build', 'deploy']) {
    assert.deepEqual(guarded[name]['runs-on'], xsenus);
    assert.equal(guarded[name].if, releaseCondition);
    assert.equal(guarded[name].defaults.run.shell, 'bash');
    requireCommand(guarded[name], 'Verify trusted Windows deployment host', 'node scripts/verify-xsenus-runner.mjs build');
    const checkout = step(guarded[name], 'Checkout');
    assert.deepEqual(checkout, step(legacy.jobs.deploy, 'Checkout'));
    for (const candidate of guarded[name].steps) assert.equal(candidate['continue-on-error'], undefined);
  }
  assert.equal(guarded.backend.if, releaseCondition);
  assert.equal(guarded.backend['runs-on'], 'ubuntu-24.04');
  assert.deepEqual(step(guarded['deploy-access'], 'Verify deployment SSH access before long gates'),
    step(legacy.jobs.frontend, 'Verify deployment SSH access before long gates'));
  const delivery = guarded.deploy;
  const packageCheck = step(delivery, 'Match the package manifest verified by hosted CI');
  assert.deepEqual(delivery.steps.filter(candidate => candidate !== packageCheck), legacy.jobs.deploy.steps,
    'Preserve the trusted delivery, Linux checks and production readiness');
  assert.equal(packageCheck.env.PACKAGE_MANIFEST_SHA, expression('needs.backend.outputs.package-manifest-sha'));
  assert.equal(packageCheck.run, 'node scripts/hosted-release/transfer.mjs verify out/deployment-manifest.json "$PACKAGE_MANIFEST_SHA"');
  assert.equal(delivery.steps.indexOf(packageCheck) + 1, delivery.steps.indexOf(step(delivery, 'Create tarball')));
  assert.equal(guarded.backend.outputs['package-manifest-sha'], expression('needs.backend-format.outputs.package-manifest-sha'));
  assert.equal(guarded.frontend.outputs['frontend-artifact-id'], expression('steps.frontend-upload.outputs.artifact-id'));
  assert.equal(guarded['backend-build'].outputs['backend-artifact-id'], expression('steps.backend-upload.outputs.artifact-id'));
  assert.equal(guarded.backend.outputs['backend-artifact-id'], expression('needs.backend-build.outputs.backend-artifact-id'));
  assert.equal(step(guarded.frontend, 'Import the frontend archive from this release run').with['artifact-ids'], expression('needs.backend-format.outputs.frontend-artifact-id'));
  requireCommand(guarded.frontend, 'Verify the producer SHA-256 before retaining the frontend archive',
    'node scripts/hosted-release/transfer.mjs verify frontend.tar.gz "$FRONTEND_SHA"');
  requireCommand(guarded.frontend, 'Retain frontend under the private typed artifact contract',
    'node scripts/xsenus-artifact-store.mjs put --kind frontend --source frontend.tar.gz --retention-days 3');
  checkCachedTransfer(guarded['backend-build'], 'Restore only the original backend archive for this run',
    'backend.tar.gz', expression('needs.backend-format.outputs.backend-key'));
  assert.match(step(guarded['backend-build'], 'Verify the producer SHA-256 before retaining the backend archive').run,
    /node scripts\/hosted-release\/transfer\.mjs verify backend\.tar\.gz "\$EXPECTED_SHA256"$/);
  requireCommand(guarded['backend-build'], 'Retain backend under the private typed artifact contract',
    'node scripts/xsenus-artifact-store.mjs put --kind backend --source backend.tar.gz --retention-days 3');

  verifyHostedCall(checks, legacy);
  verifyHostedCall(shard, legacy);
  assert.equal(checks.defaults.run.shell, 'bash');
  requireDependency(hosted, {
    ...Object.fromEntries(prerequisites.map(name => [name, []])),
    ...Object.fromEntries(shardJobs.map(name => [name, prerequisites])),
    'backend-union': ['backend-build', ...shardJobs], 'backend-browser': ['backend-build', ...shardJobs],
    'linux-filesystem': shardJobs,
    package: ['frontend', 'backend-build', 'backend-browser', 'backend-union', 'linux-filesystem'],
  });
  for (const [name, job] of Object.entries(hosted)) {
    if (shardJobs.includes(name)) {
      assert.deepEqual(Object.keys(job).sort(), ['needs', 'uses', 'with']);
      assert.equal(job.uses, './.github/workflows/release-shard.yml');
      assert.equal(job.with.shard, String(shardJobs.indexOf(name)));
      for (const field of ['runtime-key', 'runtime-sha', 'manifest-sha', 'sdk-version']) {
        assert.equal(job.with[field], expression(`needs.backend-build.outputs.${field}`));
      }
    } else {
      verifyHostedJob(job, name === 'linux-filesystem' ? 'ubuntu-24.04' : 'windows-2025');
    }
  }
  requireDependency(shard.jobs, { shard: [] });
  verifyHostedJob(shard.jobs.shard, 'windows-2025');
  assert.equal(shard.jobs.shard.defaults.run.shell, 'bash');
  assert.equal(shard.jobs.shard['timeout-minutes'], '300');
  requireCommand(shard.jobs.shard, 'Run exact backend test shard', 'bash scripts/hosted-release/run.sh shard "$SHARD"');
  checkCachedTransfer(shard.jobs.shard, 'Restore exact compiled test runtime', 'test-runtime.tar.gz', expression('inputs.runtime-key'));
  requireCommand(hosted['backend-browser'], 'Verify restored CRM through HTTP and browser', 'bash scripts/hosted-release/run.sh restored');
  requireCommand(hosted['linux-filesystem'], 'Run the exact Linux installer tests on isolated fixtures', 'bash scripts/hosted-release/run.sh linux');
  const shardIds = shardJobs.map(name => `needs.${name}.outputs.artifact-id`).join(', ');
  assert.equal(step(hosted['backend-union'], 'Download the four exact shard results').with['artifact-ids'],
    expression(`format('{0},{1},{2},{3}', ${shardIds})`));
  assert.match(step(hosted['backend-union'], 'Verify complete backend case union').run,
    /PayPlanner\.TestShards\.dll verify --manifest test-plan\.json --manifest-sha "\$EXPECTED_MANIFEST_SHA" --results-root test-results$/);

  const replaced = /^(Use the canonical|Verify trusted|Verify local artifact|Check private|Prepare Windows|Reuse qualified|Test current tooling|Verify deployment SSH|Upload|Verify portable backend package)/;
  for (const name of ['frontend', 'backend-format', 'backend-build']) {
    for (const former of legacy.jobs[name].steps) {
      if (!former.run || replaced.test(former.name)) continue;
      const job = ['Install Chromium for frontend integration', 'Test FE integration'].includes(former.name)
        ? hosted['frontend-integration'] : hosted[name];
      requireCommand(job, former.name, former.run);
      const environment = former.name === 'Discover complete backend test plan'
        ? { ...former.env, PAYPLANNER_TEST_TIMINGS: 'payplanner-test-timings.json' } : former.env;
      assert.deepEqual({ ...step(job, former.name).env }, { ...environment }, `Test inputs differ for ${former.name}`);
    }
  }
  assert.deepEqual(Object.keys(checks.on.workflow_call.outputs).sort(), Object.keys(outputs).sort());
  assert.equal(hosted['backend-build'].env.DOTNET_GCHeapHardLimit, '280000000');
  assert.equal(hosted['backend-build'].env.CompilerResponseFile, expression('github.workspace') + '/scripts/compiler-serial.rsp');
  for (const [name, producer] of Object.entries(outputs)) {
    assert.equal(checks.on.workflow_call.outputs[name].value, expression(`jobs.${producer}.outputs.${name}`));
  }
  assert.equal(hosted.package.outputs['package-manifest-sha'], expression('steps.package.outputs.sha256'));
  assert.ok(step(hosted.package, 'Verify the original release archives and assemble the package').run.includes(
    'node scripts/prepare-deployment-package.mjs out "$GITHUB_SHA"'));
  const artifacts = ['Download backend artifact', 'Download frontend artifact']
    .map(name => step(delivery, name).env.EXPECTED_ARTIFACT_ID);
  return { valid: true, gates: ['frontend', 'backend-format', 'backend'],
    deployNeeds: delivery.needs, artifacts, windowsWorkers: shardJobs.length };
}
