import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function requireValue(condition, message) {
  if (!condition) throw new Error(message);
}

// Deliberately bounded YAML subset for this workflow, not a general YAML parser.
// Unsupported syntax fails closed; literal scripts are consumed as opaque scalars.
export function parseWorkflow(source) {
  requireValue(source.length <= 1_000_000, 'Workflow exceeds parser size bound');
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  let cursor = 0;
  function peek() {
    while (cursor < lines.length && /^\s*(?:#.*)?$/.test(lines[cursor])) cursor += 1;
    if (cursor === lines.length) return null;
    const line = lines[cursor];
    requireValue(!/^ *\t/.test(line), `Tabs are unsupported at line ${cursor + 1}`);
    return { indent: line.length - line.trimStart().length, text: line.trimStart(), line: cursor + 1 };
  }
  function scalar(value) {
    if (value === '{}') return {};
    if (value.startsWith('[')) {
      requireValue(value.endsWith(']'), 'Unclosed flow sequence');
      const body = value.slice(1, -1).trim();
      return body ? body.split(',').map(part => scalar(part.trim())) : [];
    }
    if (value.startsWith('"')) {
      try { return JSON.parse(value); } catch { throw new Error('Unsupported double-quoted YAML scalar'); }
    }
    if (value.startsWith("'")) {
      requireValue(/^'(?:[^']|'')*'$/.test(value), 'Unsupported single-quoted YAML scalar');
      return value.slice(1, -1).replaceAll("''", "'");
    }
    requireValue(!/^[&*!>{|\]}]/.test(value) && !/\s+#/.test(value), 'Unsupported YAML scalar syntax');
    if (value === 'true') return true;
    if (value === 'false') return false;
    return value;
  }
  function assign(map, item) {
    const match = /^([A-Za-z0-9_.-]+):(?:\s+(.*))?$/.exec(item.text);
    requireValue(match, `Unsupported mapping at line ${item.line}`);
    const [, key, raw = ''] = match;
    requireValue(!Object.hasOwn(map, key), `Duplicate YAML key: ${key}`);
    if (/^[|>]-?$/.test(raw)) {
      const body = [];
      let bodyIndent;
      while (cursor < lines.length) {
        const line = lines[cursor];
        if (!line.trim()) { body.push(''); cursor += 1; continue; }
        const indent = line.length - line.trimStart().length;
        if (indent <= item.indent) break;
        bodyIndent ??= indent;
        requireValue(indent >= bodyIndent, `Invalid block indentation at line ${cursor + 1}`);
        body.push(line.slice(bodyIndent));
        cursor += 1;
      }
      map[key] = body.join(raw[0] === '>' ? ' ' : '\n').trimEnd();
    } else if (raw) {
      map[key] = scalar(raw);
    } else {
      const next = peek();
      map[key] = next && next.indent > item.indent ? block(next.indent) : null;
    }
  }
  function mapping(indent, first) {
    const map = Object.create(null);
    if (first) assign(map, first);
    for (let item = peek(); item && item.indent >= indent; item = peek()) {
      requireValue(item.indent === indent && !item.text.startsWith('- '), `Invalid map indentation at line ${item.line}`);
      cursor += 1;
      assign(map, item);
    }
    return map;
  }
  function block(indent) {
    requireValue(indent <= 24, 'Workflow nesting exceeds parser bound');
    if (!peek().text.startsWith('- ')) return mapping(indent);
    const sequence = [];
    for (let item = peek(); item && item.indent >= indent; item = peek()) {
      requireValue(item.indent === indent && item.text.startsWith('- '), `Invalid sequence at line ${item.line}`);
      cursor += 1;
      const text = item.text.slice(2);
      sequence.push(/^[A-Za-z0-9_.-]+:(?:\s|$)/.test(text)
        ? mapping(indent + 2, { ...item, indent: indent + 2, text })
        : scalar(text));
    }
    return sequence;
  }
  requireValue(peek()?.indent === 0, 'Workflow must begin at root indentation');
  const document = block(0);
  requireValue(peek() === null, 'Unconsumed workflow content');
  return document;
}

const sha = '${{ github.sha }}';
const project = 'backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj';
const assembly = 'backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll';
const toolProject = 'tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj';
const tool = 'dotnet tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll';
const shardJobs = [0, 1, 2, 3].map(index => `backend-test-${index}`);
const store = 'node scripts/xsenus-artifact-store.mjs';
const producerManifest = '${{ needs.backend-build.outputs.manifest-sha }}';
const producerSdk = '${{ needs.backend-build.outputs.sdk-version }}';
const runtimeArtifactId = '${{ needs.backend-build.outputs.runtime-artifact-id }}';
const frontendArtifactId = '${{ needs.frontend.outputs.frontend-artifact-id }}';
const backendArtifactId = '${{ needs.backend.outputs.backend-artifact-id }}';
const needsJson = '${{ toJSON(needs) }}';
const storeRoot = '${{ vars.PAYPLANNER_CI_ARTIFACT_ROOT }}';
const restoredFilter = 'FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource';
const restore = 'dotnet restore "${{ env.BE_SLN }}" --force -p:PayPlannerReleaseAudit=true';
const format = 'dotnet format "${{ env.BE_SLN }}" --verify-no-changes --no-restore --verbosity minimal';

export function validateDeployWorkflow(source) {
  const workflow = parseWorkflow(source);
  const jobs = workflow.jobs;
  const dependencies = {
    frontend: [], 'backend-format': [], 'backend-build': [],
    ...Object.fromEntries(shardJobs.map(id => [id, ['backend-build', 'backend-format', 'frontend']])),
    'backend-browser': ['backend-build', 'backend-format', ...shardJobs, 'frontend'],
    backend: ['backend-browser', 'backend-build', ...shardJobs],
    deploy: ['backend', 'backend-format', 'frontend'],
  };
  requireValue(jobs && Object.keys(jobs).sort().join(',') === Object.keys(dependencies).sort().join(','), 'Expected exactly ten release jobs including four explicit shards');
  requireValue(workflow.defaults === undefined, 'Custom workflow shell/defaults require explicit gate review');
  requireValue(workflow.env?.PAYPLANNER_CI_ARTIFACT_ROOT === storeRoot,
    'Artifact storage must use the reviewed repository variable without a fallback path');
  const identityOverride = env => Object.keys(env ?? {}).some(key => /^(?:GITHUB_|RUNNER_)/.test(key));
  requireValue(!identityOverride(workflow.env), 'Do not replace authenticated artifact producer context');
  const resourceLimits = { DOTNET_PROCESSOR_COUNT: '2', DOTNET_gcServer: '0', DOTNET_GCConserveMemory: '9' };
  requireValue(Object.entries(resourceLimits).every(([key, value]) => workflow.env?.[key] === value),
    'Keep the reviewed CPU and garbage-collection resource limits');
  const preservesResources = env => Object.entries(resourceLimits).every(([key, value]) => env?.[key] === undefined || env[key] === value);
  requireValue(workflow.on?.push?.branches?.join(',') === 'main' && Object.hasOwn(workflow.on, 'workflow_dispatch'), 'Keep main push and manual release triggers');
  const manual = workflow.on.workflow_dispatch;
  const deployInput = manual?.inputs?.release_to_vps;
  requireValue(Object.keys(manual ?? {}).join(',') === 'inputs'
    && Object.keys(manual.inputs ?? {}).join(',') === 'release_to_vps'
    && deployInput?.type === 'boolean' && deployInput.required === true && deployInput.default === false
    && typeof deployInput.description === 'string' && deployInput.description.length > 0
    && Object.keys(deployInput).sort().join(',') === 'default,description,required,type',
  'Manual runs must default to verification only and require an explicit deployment choice');
  const documentationPaths = ['README.md', 'AGENTS.md', 'docs/**/*.md', 'docs/yougile-sources/**'];
  requireValue(Object.keys(workflow.on).sort().join(',') === 'push,workflow_dispatch'
    && Object.keys(workflow.on.push).sort().join(',') === 'branches,paths-ignore'
    && workflow.on.push['paths-ignore']?.join(',') === documentationPaths.join(','),
  'Exclude only documentation from automatic releases; application and CI changes must still run all gates');
  const timeouts = { frontend: 30, 'backend-format': 30, 'backend-build': 30, ...Object.fromEntries(shardJobs.map(id => [id, 150])), 'backend-browser': 15, backend: 10, deploy: 30 };
  for (const [id, job] of Object.entries(jobs)) {
    requireValue(String(job['timeout-minutes']) === String(timeouts[id]), `${id}: keep the explicit runner time budget`);
    const deployCondition = "${{ success() && github.ref == 'refs/heads/main' && (github.event_name == 'push' || (github.event_name == 'workflow_dispatch' && inputs.release_to_vps == true)) }}";
    requireValue(id === 'deploy' ? job.if === deployCondition : !Object.hasOwn(job, 'if'),
      `${id}: only the reviewed deployment condition is allowed; verification gates cannot be conditional`);
    if (shardJobs.includes(id)) requireValue(Object.keys(job.concurrency ?? {}).sort().join(',') === 'cancel-in-progress,group,queue'
      && job.concurrency.group === 'payplanner-xsenus-backend-tests'
      && job.concurrency.queue === 'max' && job.concurrency['cancel-in-progress'] === false,
    `${id}: serialize all shards with the shared queue without canceling pending or running checks`);
    else requireValue(job.concurrency === undefined, `${id}: unexpected concurrency group`);
    requireValue(!identityOverride(job.env) && job.env?.PAYPLANNER_CI_ARTIFACT_ROOT === undefined,
      `${id}: do not replace authenticated artifact context or storage root`);
    requireValue(preservesResources(job.env), `${id}: do not override reviewed resource limits`);
    requireValue(Array.isArray(job['runs-on']) && job['runs-on'].join(',') === 'self-hosted,Windows,X64,payplanner-xsenus'
      && JSON.stringify(job.defaults) === JSON.stringify({ run: { shell: 'bash' } }),
    `${id}: trusted runner labels and reviewed failure-preserving shell required`);
    {
      const checkout = job.steps.filter(step => step.uses === 'actions/checkout@v6');
      requireValue(checkout.length === 1 && checkout[0].with?.['persist-credentials'] === false,
        `${id}: do not retain repository credentials on the persistent host`);
    }
    requireValue(job.services === undefined, `${id}: use existing PostgreSQL; new servers are forbidden`);
    requireValue(job['continue-on-error'] === undefined || job['continue-on-error'] === false, `${id}: continue-on-error bypass`);
    requireValue(Array.isArray(job.steps) && job.steps.length > 0, `${id}: missing steps`);
    const stepIds = job.steps.filter(step => step.id !== undefined).map(step => step.id);
    requireValue(new Set(stepIds).size === stepIds.length, `${id}: duplicate step output identity`);
    const needs = job.needs ?? [];
    requireValue(Array.isArray(needs) && [...needs].sort().join(',') === dependencies[id].join(','), `${id}: exact successful dependencies required`);
    requireValue(job.strategy === undefined, `${id}: explicit jobs must not add a matrix/strategy`);
    for (const step of job.steps) {
      requireValue(!identityOverride(step.env) && step.env?.PAYPLANNER_CI_ARTIFACT_ROOT === undefined,
        `${id}: do not replace authenticated artifact context or storage root`);
      requireValue(preservesResources(step.env), `${id}: do not override reviewed resource limits`);
      requireValue(step['continue-on-error'] === undefined || step['continue-on-error'] === false, `${id}: step continue-on-error bypass`);
      const diagnostic = step.uses === undefined && (
        (shardJobs.includes(id) && step.run === `${store} put --kind shard-diagnostics --source test-results/ --retention-days 14`)
        || (id === 'backend-browser' && step.run === `${store} put --kind restored-diagnostics --source test-results/restored-browser/ --retention-days 14`));
      const qualifiedAlias = shardJobs.includes(id) && step.uses === undefined
        && step.run === `${store} put --kind qualified-shard --source test-results/ --retention-days 14`;
      requireValue(step.if === undefined || (diagnostic && step.if === 'always()') || (qualifiedAlias && step.if === 'success()'), `${id}: conditional step can skip a release gate`);
      if (diagnostic) requireValue(step.if === 'always()', `${id}: preserve immutable attempt evidence unconditionally`);
      if (qualifiedAlias) requireValue(step.if === 'success()', `${id}: only successful tests may publish a qualified receipt`);
      requireValue(!step.uses?.match(/^actions\/(?:upload|download)-artifact@/), `${id}: no hosted artifact or fallback transport`);
    }
  }

  function command(job, expected, prefix = false, capturedExit = false) {
    const found = jobs[job].steps.flatMap((step, index) => {
      const lines = typeof step.run === 'string' ? step.run.split('\n').map(line => line.trim()).filter(line => line && !line.startsWith('#')) : [];
      return lines.some(line => prefix ? line.startsWith(expected) : line === expected) ? [{ step, index, lines }] : [];
    });
    requireValue(found.length === 1, `${job}: missing or duplicate executable gate ${expected}`);
    const result = found[0];
    requireValue(result.step.shell === undefined || result.step.shell === 'bash', `${job}: gate shell must preserve failure status`);
    requireValue(!result.lines.some(line => !(capturedExit && line === expected) && /\|\||(?:^|[;\s])(?:exit\s+0|set\s+\+e)(?:\s|$)/.test(line)), `${job}: swallowed gate failure`);
    requireValue(!result.lines.some(line => /^(?:if\b|elif\b|else\b|for\b|while\b|until\b|case\b|function\b|\w+\(\)|trap\b|exit\b|return\b|set\s+\+)|<<|&&\s*\\$/.test(line)), `${job}: conditional/wrapped gate execution is unsupported`);
    return result;
  }
  const transportSteps = new Set();
  const sameEnvironment = (actual, expected) => Object.keys(actual ?? {}).sort().join(',') === Object.keys(expected).sort().join(',')
    && Object.entries(expected).every(([key, value]) => actual[key] === value);
  function transport(job, expected, env = {}) {
    const result = command(job, expected);
    requireValue(result.lines.length === 1 && result.step.uses === undefined && result.step.with === undefined
      && sameEnvironment(result.step.env, env), `${job}: exact typed artifact transport and trusted context required; no fallback`);
    transportSteps.add(`${job}:${result.index}`);
    return result;
  }
  const producerMetadata = { EXPECTED_MANIFEST_SHA: producerManifest, EXPECTED_SDK_VERSION: producerSdk };
  const resultMetadata = { ...producerMetadata, EXPECTED_RUNTIME_ARTIFACT_ID: runtimeArtifactId, ARTIFACT_NEEDS_JSON: needsJson };
  function archiveUpload(job, kind, path, id, output) {
    const env = kind === 'frontend' ? {} : {
      EXPECTED_MANIFEST_SHA: '${{ steps.plan.outputs.manifest-sha }}',
      EXPECTED_SDK_VERSION: '${{ steps.plan.outputs.sdk-version }}',
    };
    const uploaded = transport(job, `${store} put --kind ${kind} --source ${path} --retention-days 3`, env);
    requireValue(uploaded.step.id === id && uploaded.step.if === undefined
      && jobs[job].outputs?.[output] === `\${{ steps.${id}.outputs.artifact-id }}`,
    `${job}: immutable typed archive ID must come from the exact producer output`);
    return uploaded;
  }
  function downloadById(job, kind, producer, id, path, { shard, merge = false, metadata = {} } = {}) {
    return transport(job,
      `${store} get --kind ${kind} --producer-job ${producer}${shard === undefined ? '' : ` --shard ${shard}`} --destination ${path}${merge ? ' --merge-disjoint' : ''}`,
      { EXPECTED_ARTIFACT_ID: id, ARTIFACT_NEEDS_JSON: needsJson, ...metadata });
  }
  const feUpload = archiveUpload('frontend', 'frontend', 'frontend.tar.gz', 'frontend-upload', 'frontend-artifact-id');
  const frontendCommands = [
    'npm ci', 'node scripts/validate-deploy-workflow.mjs',
    'bash scripts/prune-deploy-backups.test.sh', 'bash scripts/preserve-data-protection-keys.test.sh',
    'node --test scripts/storage-legacy-backfill.node-tests.mjs', 'npm run lint',
    'npm run typecheck', 'npm run audit:dates:test', 'npm run audit:encoding',
    'npm run audit:surface:test && npm run audit:surface', 'npm audit --audit-level=moderate',
    'npm run test:ci', 'npm run test:integration',
    'npm run test:legal-flags:strict -- --warn-on-feature-flags-summary-failure', 'npm run build',
    'tar -czf frontend.tar.gz -C "${{ env.OUT_DIR }}" fe',
  ];
  const fe = frontendCommands.map(value => command('frontend', value));
  const feBrowser = command('frontend', 'npx playwright install chromium');
  requireValue(feBrowser.index > fe[0].index && feBrowser.index < fe[12].index,
    'Compatible Chromium must be installed after dependencies and before frontend integration');
  requireValue(fe.every(({ index }) => index < feUpload.index), 'Frontend gates/build must precede frontend upload');
  requireValue(fe.slice(1, 14).every(({ index }) => index > fe[0].index && index < fe[14].index), 'Frontend install/checks/build order');
  requireValue(fe[0].index < fe[1].index && fe[13].index < fe[14].index && fe[14].index < fe[15].index, 'Frontend install/legal/build/package order');
  requireValue(fe[13].step.env?.PAYPLANNER_READINESS_API_URL === '${{ env.FE_API_URL }}', 'Legal readiness API configuration missing');
  requireValue(fe[14].step.env?.VITE_API_URL === '${{ env.FE_API_URL }}', 'Frontend build API configuration missing');
  requireValue(command('backend-format', restore).index < command('backend-format', format).index, 'Strict restore must precede full format');
  requireValue(command('frontend', 'node scripts/verify-xsenus-runner.mjs build').index < fe[0].index,
    'Trusted host preflight must precede frontend tools');
  for (const check of ['node --test scripts/xsenus-artifact-store.node-tests.mjs', 'node --test scripts/task-notification-refresh-observer.node-tests.mjs']) {
    const gate = command('frontend', check);
    requireValue(command('frontend', 'node scripts/verify-xsenus-runner.mjs build').index < gate.index
      && gate.index < fe[14].index && gate.index < feUpload.index,
      'Artifact transport and notification regression gates must precede frontend packaging');
  }

  for (const id of ['backend-format', 'backend-build', ...shardJobs, 'backend-browser', 'backend']) {
    const job = jobs[id];
    const preparation = command(id, 'node scripts/prepare-windows-ci-toolchain.mjs');
    const sdkIndex = job.steps.findIndex(step => step.uses === 'actions/setup-dotnet@v5');
    const node = job.steps.findIndex(step => step.uses === 'actions/setup-node@v6' && step.with?.['node-version'] === '22');
    requireValue(preparation.step.env?.DOTNET_INSTALL_DIR === '${{ runner.tool_cache }}/payplanner-dotnet'
      && node >= 0 && node < preparation.index && preparation.index < sdkIndex,
    `${id}: prepare writable .NET installation and runnable PowerShell before setup-dotnet`);
    requireValue(job.steps[sdkIndex].env?.DOTNET_INSTALL_DIR === undefined,
      `${id}: do not override the verified .NET installation directory`);
  }

  const beRestore = command('backend-build', restore);
  const build = command('backend-build', `dotnet build ${project} --no-restore -c Release`);
  const toolBuild = command('backend-build', `dotnet build ${toolProject} --no-restore -c Release`);
  const discover = command('backend-build', `${tool} discover --assembly ${assembly} --manifest test-plan.json --revision "${sha}" --shards 4 --execution-profile bounded`);
  requireValue(discover.lines[0] === 'set -euo pipefail' && discover.step.id === 'plan', 'Discovery must preserve failure status and produce job outputs');
  requireValue(String(discover.step.env?.BackgroundServices__Enabled) === 'false' && String(discover.step.env?.Email__Enabled) === 'false', 'Discovery mail/background isolation missing');
  requireValue(jobs['backend-build'].outputs?.['manifest-sha'] === '${{ steps.plan.outputs.manifest-sha }}'
    && jobs['backend-build'].outputs?.['sdk-version'] === '${{ steps.plan.outputs.sdk-version }}', 'Consumers must use producer manifest hash and SDK outputs');
  requireValue(discover.lines.includes(`printf 'manifest-sha=%s\\n' "$(node -p "require('./test-plan.summary.json').ManifestSha256")" >> "$GITHUB_OUTPUT"`)
    && discover.lines.includes(`printf 'sdk-version=%s\\n' "$(dotnet --version)" >> "$GITHUB_OUTPUT"`), 'Discovery outputs must come from the producer summary and SDK');
  const runtimePack = command('backend-build', `tar -czf test-runtime.tar.gz test-plan.json test-plan.summary.json backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/PayPlanner.TestShards/bin/Release/net9.0`);
  const runtimeUpload = archiveUpload('backend-build', 'test-runtime', 'test-runtime.tar.gz', 'runtime-upload', 'runtime-artifact-id');
  const publish = command('backend-build', 'dotnet publish "${{ env.BE_CSPROJ }}" -c Release --no-build --no-restore -o "${{ env.OUT_DIR }}/be"');
  const bePack = command('backend-build', 'tar -czf backend.tar.gz -C "${{ env.OUT_DIR }}" be');
  const beUpload = archiveUpload('backend-build', 'backend', 'backend.tar.gz', 'backend-upload', 'backend-artifact-id');
  const beOrder = [beRestore, build, toolBuild, discover, runtimePack, runtimeUpload, publish, bePack, beUpload].map(item => item.index);
  requireValue(beOrder.every((value, index) => index === 0 || value > beOrder[index - 1]), 'Backend strict restore/build/discover/artifact order');
  const shards = shardJobs.map((id, index) => {
    const shard = command(id, `${tool} run --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --shard ${index} --results test-results/shard-${index} --workers auto --suite all 2>&1 | tee test-results/shard-${index}.log`);
    requireValue(shard.lines.length === 3 && shard.lines[1] === 'mkdir -p test-results', 'Shard result directory must be fresh and created by the runner');
    return shard;
  });
  const shardGates = shardJobs.map((id, index) => [id, shards[index]]);
  const restoredCommand = `dotnet vstest ${assembly} --Settings:test-results/restored-browser/gate.runsettings --Logger:'trx;LogFileName=results.trx' --ResultsDirectory:test-results/restored-browser --TestCaseFilter:'${restoredFilter}' > test-results/restored-browser/test.log 2>&1 || native_exit=$?`;
  const restored = command('backend-browser', restoredCommand, false, true);
  const restoredVerification = `${tool} verify-restored --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --results test-results/restored-browser --exit-code "$native_exit"`;
  requireValue(restored.lines.length === 7 && restored.lines[1] === 'mkdir -p test-results/restored-browser'
    && restored.lines[3] === 'native_exit=0' && restored.lines[4] === restoredCommand
    && restored.lines[5] === 'cat test-results/restored-browser/test.log' && restored.lines[6] === restoredVerification,
  'Restored gate must capture actual native status and verify exact five cases without masking failures');
  requireValue(restored.lines.includes(`printf '%s\\n' '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>' > test-results/restored-browser/gate.runsettings`), 'Restored browser gate must fail on empty selection');
  requireValue(String(restored.step.env?.PAYPLANNER_TEST_RESTORED_BROWSER) === 'true', 'Restored browser gate must execute');
  const union = command('backend', `${tool} verify --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --results-root test-results`);
  const timingProfile = command('backend', `${tool} timings --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --results-root test-results --output "$RUNNER_TEMP/payplanner-test-timings.json"`);
  requireValue(union.index < timingProfile.index && timingProfile.step.env?.EXPECTED_MANIFEST_SHA === '${{ needs.backend-build.outputs.manifest-sha }}',
    'Timing profile must follow complete union qualification with the producer hash');
  const timingCache = jobs.backend.steps.map((step, index) => ({ step, index }))
    .filter(({ step }) => step.uses === 'actions/cache/save@v5');
  requireValue(timingCache.length === 1 && timingCache[0].index > timingProfile.index
    && timingCache[0].step.with?.path === '${{ runner.temp }}/payplanner-test-timings.json'
    && timingCache[0].step.with?.key === 'backend-timings-v1-${{ runner.os }}-${{ github.run_id }}-${{ github.run_attempt }}',
  'Timing cache must publish only the qualified profile after its creation');
  for (const gate of [...shards, restored, union]) requireValue(gate.step.env?.EXPECTED_MANIFEST_SHA === '${{ needs.backend-build.outputs.manifest-sha }}', 'Consumers must use the trusted producer manifest hash');
  for (const [id, gate] of [...shardGates, ['backend-browser', restored], ['backend', union]]) {
    requireValue(gate.lines[0] === 'set -euo pipefail', `${id}: preserve failure exit status`);
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '${{ needs.backend-build.outputs.sdk-version }}', `${id}: use producer SDK version`);
    const actualSdk = command(id, 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"');
    requireValue(actualSdk.step.env?.EXPECTED_SDK_VERSION === producerSdk
      && actualSdk.step.env?.RUNTIME_ARTIFACT_ID === runtimeArtifactId
      && actualSdk.lines.join('\n') === 'set -euo pipefail\nactual_sdk="$(dotnet --version)"\ntest "$actual_sdk" = "$EXPECTED_SDK_VERSION"\n[[ "$RUNTIME_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', `${id}: verify actual SDK and one nonempty typed producer runtime ID`);
    const downloaded = downloadById(id, 'test-runtime', 'backend-build', runtimeArtifactId, 'test-artifacts', { metadata: producerMetadata });
    const unpack = command(id, 'tar -xzf test-artifacts/test-runtime.tar.gz');
    requireValue(jobs[id].steps.indexOf(sdk[0]) < actualSdk.index && actualSdk.index < downloaded.index
      && downloaded.index < unpack.index && unpack.index < gate.index, `${id}: SDK check and downloaded runtime must precede execution`);
    requireValue(!jobs[id].steps.some(step => /dotnet (?:build|publish|restore)|\bMSBuild\b/.test(step.run ?? '')), `${id}: test runtime must not be rebuilt`);
  }
  for (const [id, gate] of [...shardGates, ['backend-browser', restored]]) {
    requireValue(String(gate.step.env?.BackgroundServices__Enabled) === 'false' && String(gate.step.env?.Email__Enabled) === 'false', `${id}: test mail/background isolation missing`);
    requireValue(gate.step.env?.PAYPLANNER_TEST_POSTGRES === '${{ secrets.PAYPLANNER_XSENUS_TEST_POSTGRES }}'
      && gate.step.env?.PAYPLANNER_TEST_PG_DUMP === 'C:/Program Files/PostgreSQL/17/bin/pg_dump.exe'
      && gate.step.env?.PAYPLANNER_TEST_PSQL === 'C:/Program Files/PostgreSQL/17/bin/psql.exe', `${id}: dedicated existing PostgreSQL prerequisites missing`);
    const preflight = command(id, 'node scripts/verify-xsenus-runner.mjs postgres');
    requireValue(preflight.step.env?.PAYPLANNER_TEST_POSTGRES === '${{ secrets.PAYPLANNER_XSENUS_TEST_POSTGRES }}',
      `${id}: preflight must verify the same dedicated test connection`);
    for (const prereq of ['npm ci', 'npx playwright install chromium chrome', 'npm install --global @playwright/cli@0.1.22']) {
      requireValue(command(id, prereq).index < gate.index, `${id}: complete prerequisites must precede tests`);
    }
    requireValue(preflight.index < gate.index, `${id}: verify isolation before running tests`);
  }
  requireValue(command('backend-build', 'node scripts/verify-xsenus-runner.mjs build').index < beRestore.index,
    'Trusted host preflight must precede build');
  const portable = command('backend-build', 'node scripts/verify-xsenus-runner.mjs package');
  requireValue(publish.index < portable.index && portable.index < bePack.index,
    'Verify Linux-compatible portable package before archiving');
  const restoredDiagnostic = transport('backend-browser', `${store} put --kind restored-diagnostics --source test-results/restored-browser/ --retention-days 14`, resultMetadata);
  requireValue(restored.index < restoredDiagnostic.index, 'Restored results must precede unconditional diagnostic persistence');
  for (let index = 0; index < 4; index += 1) {
    const id = shardJobs[index];
    const metadata = { ...resultMetadata, ARTIFACT_SHARD: String(index) };
    const diagnostic = transport(id, `${store} put --kind shard-diagnostics --source test-results/ --retention-days 14`, metadata);
    const qualified = transport(id, `${store} put --kind qualified-shard --source test-results/ --retention-days 14`, {
      ...metadata, EXPECTED_DIAGNOSTIC_ARTIFACT_ID: '${{ steps.shard-diagnostics.outputs.artifact-id }}',
    });
    requireValue(diagnostic.step.id === 'shard-diagnostics' && qualified.step.id === 'qualified-upload'
      && shards[index].index < diagnostic.index && diagnostic.index < qualified.index,
    'Qualified receipts require successful tests and preserved attempt evidence first');
    requireValue(Object.keys(jobs[id].outputs ?? {}).join(',') === 'qualified-artifact-id'
      && jobs[id].outputs['qualified-artifact-id'] === `\${{ steps.qualified-upload.outputs.shard_${index}_id }}`,
    `${id}: distinct trusted shard output must come from its qualified publication`);
    const result = downloadById('backend', 'qualified-shard', id,
      `\${{ needs.${id}.outputs.qualified-artifact-id }}`, 'test-results',
      { shard: index, merge: true, metadata: { ...producerMetadata, EXPECTED_RUNTIME_ARTIFACT_ID: runtimeArtifactId } });
    requireValue(result.index < union.index, 'All four result artifacts must precede exact union verification');
  }
  for (const id of ['backend-format', 'backend-build']) {
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '9.0.x', `${id}: .NET 9 SDK required`);
  }

  const deploy = jobs.deploy.steps;
  requireValue(sameEnvironment(jobs.backend.outputs, {
    'backend-artifact-id': '${{ needs.backend-build.outputs.backend-artifact-id }}',
    'manifest-sha': producerManifest, 'sdk-version': producerSdk,
  }), 'Aggregate must forward the same producer backend artifact ID, manifest and SDK after successful qualification');
  const downloads = [
    downloadById('deploy', 'backend', 'backend-build', backendArtifactId, 'deploy-artifacts', {
      metadata: { EXPECTED_MANIFEST_SHA: '${{ needs.backend.outputs.manifest-sha }}', EXPECTED_SDK_VERSION: '${{ needs.backend.outputs.sdk-version }}' },
    }),
    downloadById('deploy', 'frontend', 'frontend', frontendArtifactId, 'deploy-artifacts', { merge: true }),
  ];
  const deployIds = command('deploy', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]');
  requireValue(deployIds.step.env?.BACKEND_ARTIFACT_ID === backendArtifactId && deployIds.step.env?.FRONTEND_ARTIFACT_ID === frontendArtifactId
    && deployIds.lines.join('\n') === 'set -euo pipefail\n[[ "$BACKEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]\n[[ "$FRONTEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]\n[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]'
    && downloads.every(({ index }) => deployIds.index < index), 'Deploy must reject empty, multiple or substituted artifact IDs before downloading');
  const tar = command('deploy', 'tar -czf "${{ env.TAR_NAME }}" -C "${{ env.OUT_DIR }}" .');
  const unpackBackend = command('deploy', 'tar -xzf deploy-artifacts/backend.tar.gz -C "${{ env.OUT_DIR }}"');
  const unpackFrontend = command('deploy', 'tar -xzf deploy-artifacts/frontend.tar.gz -C "${{ env.OUT_DIR }}"');
  const bridge = command('deploy', 'node scripts/windows-vps-deploy.mjs');
  const bridgeTests = command('deploy', 'node --test scripts/windows-vps-deploy.node-tests.mjs');
  const trustedDeploy = command('deploy', 'node scripts/verify-xsenus-runner.mjs build');
  const readiness = command('deploy', 'node scripts/run-production-deploy-readiness.mjs', true);
  const requiredBridgeEnv = {
    DEPLOY_SHA: sha, DEPLOY_ARCHIVE_NAME: '${{ env.TAR_NAME }}',
    VPS_SSH_HOST_FINGERPRINT: '${{ vars.VPS_SSH_HOST_FINGERPRINT }}', VPS_SSH_KEY: '${{ secrets.VPS_SSH_KEY }}',
    PAYPLANNER_WEBPUSH_ENABLED: "${{ vars.PAYPLANNER_WEBPUSH_ENABLED || 'false' }}",
    PAYPLANNER_WEBPUSH_PUBLIC_KEY: '${{ secrets.PAYPLANNER_WEBPUSH_PUBLIC_KEY }}',
    PAYPLANNER_WEBPUSH_PRIVATE_KEY: '${{ secrets.PAYPLANNER_WEBPUSH_PRIVATE_KEY }}',
    PAYPLANNER_WEBPUSH_SUBJECT: '${{ secrets.PAYPLANNER_WEBPUSH_SUBJECT }}',
  };
  requireValue(bridge.lines.join('\n') === 'node scripts/windows-vps-deploy.mjs'
    && JSON.stringify(bridge.step.env) === JSON.stringify(requiredBridgeEnv), 'Deploy requires the pinned native SSH bridge and structured secret environment');
  requireValue(!deploy.some(step => step.uses?.startsWith('appleboy/')), 'Windows deployment must use native OpenSSH with Linux filesystem gates');
  requireValue(trustedDeploy.index < bridgeTests.index && bridgeTests.index < bridge.index,
    'Trusted host and bridge regression tests must precede Linux filesystem gates and deployment');
  requireValue(readiness.lines.includes('--expected-sha="${{ github.sha }}" \\')
    && readiness.lines.includes('--token="" \\') && readiness.lines.includes('--require-websocket=true \\')
    && readiness.lines.includes('--allow-websocket-auth-challenge=true \\'), 'Preserve exact-revision public production readiness without a stored JWT');
  requireValue(downloads.every(({ index }) => index < unpackBackend.index && index < unpackFrontend.index)
    && unpackBackend.index < tar.index && unpackFrontend.index < tar.index
    && tar.index < bridge.index && bridge.index < readiness.index, 'Deploy download/unpack/tar/Linux-gates/SSH/readiness order');
  requireValue(!deploy.some(step => /(?:dotnet (?:test|build|publish)|npm (?:ci|run build))/.test(step.run ?? '')), 'Deploy must use qualified artifacts without rebuilding');
  for (const [id, job] of Object.entries(jobs)) {
    const storage = job.steps.map((step, index) => ({ step, index }))
      .filter(({ step }) => /xsenus-artifact-store\.mjs/.test(step.run ?? ''));
    if (!storage.length) continue;
    const check = transport(id, `${store} check`);
    requireValue(storage.every(({ index }) => index === check.index || index > check.index),
      `${id}: verified storage must precede every transport operation`);
    requireValue(storage.every(({ index }) => transportSteps.has(`${id}:${index}`)),
      `${id}: download only the exact producer IDs; no extra or fallback artifact transport`);
  }
  // The four independently rerunnable jobs differ only in their index. Their
  // shared queue serializes execution without canceling pending checks.
  const normalizedShard = (job, index) => {
    const normalized = JSON.parse(JSON.stringify(job));
    delete normalized.name;
    for (const step of normalized.steps) {
      if (step.env?.ARTIFACT_SHARD !== undefined) step.env.ARTIFACT_SHARD = '<shard>';
    }
    return JSON.stringify(normalized)
      .replaceAll(`shard_${index}_id`, 'shard_INDEX_id')
      .replaceAll(`--shard ${index}`, '--shard INDEX')
      .replaceAll(`shard-${index}`, 'shard-INDEX');
  };
  const firstShard = normalizedShard(jobs[shardJobs[0]], 0);
  for (let index = 1; index < 4; index += 1) requireValue(normalizedShard(jobs[shardJobs[index]], index) === firstShard,
    `${shardJobs[index]}: all four explicit shards must have identical gates, resource limits and transport contracts`);
  return { valid: true, gates: ['frontend', 'backend-format', 'backend'], deployNeeds: jobs.deploy.needs, artifacts: downloads.map(({ step }) => step.env.EXPECTED_ARTIFACT_ID) };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    requireValue(process.argv.length <= 3, 'Usage: node scripts/validate-deploy-workflow.mjs [workflow-path]');
    console.log(JSON.stringify(validateDeployWorkflow(readFileSync(resolve(process.argv[2] ?? '.github/workflows/deploy.yml'), 'utf8'))));
  } catch (error) {
    console.error(`Deploy workflow validation failed: ${error.message}`);
    process.exitCode = 1;
  }
}
