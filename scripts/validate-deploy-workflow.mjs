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
const matrixShard = '${{ matrix.shard }}';
const attempt = '${{ github.run_attempt }}';
const producerManifest = '${{ needs.backend-build.outputs.manifest-sha }}';
const producerSdk = '${{ needs.backend-build.outputs.sdk-version }}';
const runtimeArtifactId = '${{ needs.backend-build.outputs.runtime-artifact-id }}';
const frontendArtifactId = '${{ needs.frontend.outputs.frontend-artifact-id }}';
const backendArtifactId = '${{ needs.backend.outputs.backend-artifact-id }}';
const qualifiedShardName = shard => `qualified-backend-shard-${shard}-${sha}-${producerManifest}-${producerSdk}`;
const artifactStore = 'node scripts/xsenus-artifact-store.mjs';
const localIdPattern = '^xsenus-local-v1:[a-f0-9]{64}$';
const trustedNeeds = '${{ toJSON(needs) }}';
const localPut = (kind, path, days) => `${artifactStore} put --kind ${kind} --source ${path} --retention-days ${days}`;
const localGet = (kind, producer, path, shard, merge = false) => `${artifactStore} get --kind ${kind} --producer-job ${producer} --destination ${path}${shard === undefined ? '' : ` --shard ${shard}`}${merge ? ' --merge-disjoint' : ''}`;
const isLocalGet = step => typeof step.run === 'string' && step.run.split('\n').some(line => line.trim().startsWith(`${artifactStore} get `));
const isLocalPut = step => typeof step.run === 'string' && step.run.split('\n').some(line => line.trim().startsWith(`${artifactStore} put `));
const restoredFilter = 'FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource';
const restore = 'dotnet restore "${{ env.BE_SLN }}" --force --disable-parallel -p:PayPlannerReleaseAudit=true';
const formatCommands = [
  'dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal',
  'dotnet format whitespace backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'dotnet format style backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'dotnet format tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'dotnet format tests/PayPlanner.StorageStageWriterProbe/PayPlanner.StorageStageWriterProbe.csproj --verify-no-changes --no-restore --verbosity diagnostic',
  'dotnet format backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --verify-no-changes --no-restore --verbosity diagnostic',
];

export function validateDeployWorkflow(source) {
  const workflow = parseWorkflow(source);
  const jobs = workflow.jobs;
  requireValue(workflow.env?.PAYPLANNER_CI_ARTIFACT_ROOT === 'D:\\PayPlannerCIArtifacts\\main-store', 'Pin the separate private production artifact store');
  const dependencies = {
    frontend: [], 'backend-format': ['frontend'], 'backend-build': ['backend-format'],
    'backend-test': ['backend-build', 'backend-format', 'frontend'],
    'backend-browser': ['backend-build', 'backend-format', 'backend-test', 'frontend'],
    backend: ['backend-browser', 'backend-build', 'backend-test'],
    deploy: ['backend', 'backend-format', 'frontend'],
  };
  requireValue(jobs && Object.keys(jobs).sort().join(',') === Object.keys(dependencies).sort().join(','), 'Expected exactly seven release jobs');
  requireValue(workflow.defaults === undefined, 'Custom workflow shell/defaults require explicit gate review');
  requireValue(workflow.concurrency?.group === 'payplanner-deploy' && workflow.concurrency?.['cancel-in-progress'] === false,
    'A newer release must queue instead of interrupting an in-progress stopped-backup installation');
  requireValue(workflow.on?.push?.branches?.join(',') === 'main' && Object.hasOwn(workflow.on, 'workflow_dispatch'), 'Keep main push and manual release triggers');
  const documentationPaths = ['README.md', 'AGENTS.md', 'docs/**/*.md', 'docs/yougile-sources/**'];
  requireValue(Object.keys(workflow.on).sort().join(',') === 'push,workflow_dispatch'
    && Object.keys(workflow.on.push).sort().join(',') === 'branches,paths-ignore'
    && workflow.on.push['paths-ignore']?.join(',') === documentationPaths.join(','),
  'Exclude only documentation from automatic releases; application and CI changes must still run all gates');
  requireValue(workflow.env?.NODE_OPTIONS === '--max-old-space-size=1536'
    && workflow.env?.DOTNET_PROCESSOR_COUNT === '1'
    && workflow.env?.DOTNET_gcServer === '0' && workflow.env?.DOTNET_GCConserveMemory === '9'
    && workflow.env?.UseSharedCompilation === 'false'
    && workflow.env?.CompilerResponseFile === undefined
    && workflow.env?.PayPlannerSkipMigrationsBuild === undefined
    && workflow.env?.ArtifactsPath === undefined && workflow.env?.UseArtifactsOutput === undefined,
  'Keep the reviewed bounded coordinator/build resource profile');
  const heapLimits = { 'backend-format': '180000000', 'backend-build': '240000000',
    'backend-test': 'C0000000', 'backend-browser': 'C0000000', backend: 'C0000000' };
  for (const [id, heap] of Object.entries(heapLimits)) {
    requireValue(jobs[id]?.env?.DOTNET_GCHeapHardLimit === heap, `${id}: keep the bounded managed heap profile`);
  }
  requireValue(jobs['backend-build'].env?.CompilerResponseFile === '${{ github.workspace }}/scripts/compiler-serial.rsp',
    'Use the reviewed serial compiler response file for the fresh build graph');
  const timeouts = { frontend: 30, 'backend-format': 30, 'backend-build': 30, 'backend-test': 150, 'backend-browser': 15, backend: 10, deploy: 30 };
  for (const [id, job] of Object.entries(jobs)) {
    requireValue(String(job['timeout-minutes']) === String(timeouts[id]), `${id}: keep the explicit runner time budget`);
    requireValue(!Object.hasOwn(job, 'if'), `${id}: conditional job can bypass a release gate`);
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
    if (id === 'backend-test') {
      requireValue(job.strategy?.['fail-fast'] === false
        && Object.keys(job.strategy).sort().join(',') === 'fail-fast,matrix,max-parallel'
        && job.strategy['max-parallel'] === '1'
        && Object.keys(job.strategy.matrix ?? {}).join(',') === 'shard'
        && job.strategy.matrix.shard?.join(',') === '0,1,2,3', 'All four shards must run with fail-fast disabled');
    } else requireValue(job.strategy === undefined, `${id}: unexpected matrix/strategy`);
    for (const step of job.steps) {
      requireValue(step['continue-on-error'] === undefined || step['continue-on-error'] === false, `${id}: step continue-on-error bypass`);
      requireValue(!step.uses?.startsWith('actions/upload-artifact@') && !step.uses?.startsWith('actions/download-artifact@'), `${id}: cloud/fallback artifact action is forbidden`);
      const diagnostic = (id === 'backend-test' && step.run === `set -euo pipefail\n${localPut('shard-diagnostics', 'test-results/', 14)}`)
        || (id === 'backend-browser' && step.run === `set -euo pipefail\n${localPut('restored-diagnostics', 'test-results/restored-browser/', 14)}`);
      const qualifiedAlias = id === 'backend-test' && step.run === `set -euo pipefail\n${localPut('qualified-shard', 'test-results/', 14)}`;
      const deploymentHalt = id === 'deploy' && step.run === 'node scripts/windows-vps-deploy.mjs --halt';
      const haltCondition = "always() && steps.vps-install.outcome == 'success' && steps.production-readiness.outcome != 'success'";
      requireValue(step.if === undefined || (deploymentHalt && step.if === haltCondition) || (diagnostic && step.if === 'always()') || (qualifiedAlias && step.if === 'success()'), `${id}: conditional step can skip a release gate`);
      if (deploymentHalt) requireValue(step.if === haltCondition, 'Readiness failure must halt only the installed revision');
      if (diagnostic) requireValue(step.if === 'always()', `${id}: preserve immutable attempt evidence unconditionally`);
      if (qualifiedAlias) requireValue(step.if === 'success()', `${id}: only a successful native result may publish qualified evidence`);
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
  function localTransfer(job, expected, environment) {
    const found = jobs[job].steps.map((step, index) => ({ step, index })).filter(({ step }) => step.run === `set -euo pipefail\n${expected}`);
    requireValue(found.length === 1, `${job}: missing unique SHA-bound local artifact transfer ${expected}`);
    const result = found[0];
    requireValue(result.step.uses === undefined && result.step.with === undefined && result.step.shell === undefined,
      `${job}: local artifact CLI must use the failure-preserving job shell`);
    requireValue(Object.keys(result.step.env ?? {}).sort().join(',') === Object.keys(environment).sort().join(',')
      && Object.entries(environment).every(([key, value]) => result.step.env[key] === value), `${job}: exact trusted artifact identity/metadata required`);
    return result;
  }
  function artifact(job, direction, name, path) {
    let kind, shard;
    if (name === `frontend-${sha}-${attempt}`) kind = 'frontend';
    else if (name === `backend-${sha}-${attempt}`) kind = 'backend';
    else if (name === `test-runtime-${sha}-${attempt}`) kind = 'test-runtime';
    else if (name === `backend-shard-${matrixShard}-${sha}-${attempt}`) { kind = 'shard-diagnostics'; shard = matrixShard; }
    else if (name === `restored-browser-${sha}-${attempt}`) kind = 'restored-diagnostics';
    else if (name === qualifiedShardName(matrixShard)) { kind = 'qualified-shard'; shard = matrixShard; }
    else { shard = [0, 1, 2, 3].find(index => name === qualifiedShardName(index)); kind = 'qualified-shard'; }
    requireValue(kind && (kind !== 'qualified-shard' || shard !== undefined), `${job}: unknown artifact role`);
    const environment = { ARTIFACT_NEEDS_JSON: trustedNeeds };
    if (direction === 'upload') {
      if (kind !== 'frontend') {
        environment.EXPECTED_MANIFEST_SHA = job === 'backend-build' ? '${{ steps.plan.outputs.manifest-sha }}' : producerManifest;
        environment.EXPECTED_SDK_VERSION = job === 'backend-build' ? '${{ steps.plan.outputs.sdk-version }}' : producerSdk;
      }
      if (['shard-diagnostics', 'qualified-shard', 'restored-diagnostics'].includes(kind)) environment.EXPECTED_RUNTIME_ARTIFACT_ID = runtimeArtifactId;
      if (shard !== undefined) environment.ARTIFACT_SHARD = matrixShard;
      if (kind === 'qualified-shard') environment.EXPECTED_DIAGNOSTIC_ARTIFACT_ID = '${{ steps.shard-diagnostics.outputs.artifact-id }}';
      const retentionDays = ['frontend', 'backend', 'test-runtime'].includes(kind) ? 3 : 14;
      return { ...localTransfer(job, localPut(kind, path, retentionDays), environment), retentionDays };
    }
    requireValue(kind === 'qualified-shard' && Number.isInteger(shard), `${job}: only an exact shard role may be retrieved here`);
    Object.assign(environment, { EXPECTED_ARTIFACT_ID: `\${{ needs.backend-test.outputs.shard_${shard}_id }}`,
      EXPECTED_MANIFEST_SHA: producerManifest, EXPECTED_SDK_VERSION: producerSdk, EXPECTED_RUNTIME_ARTIFACT_ID: runtimeArtifactId });
    return localTransfer(job, localGet(kind, `backend-test-${shard}`, path, shard, true), environment);
  }
  function archiveUpload(job, name, path, id, output) {
    const uploaded = artifact(job, 'upload', name, path);
    requireValue(uploaded.step.id === id && uploaded.step.if === undefined
      && jobs[job].outputs?.[output] === `\${{ steps.${id}.outputs.artifact-id }}`
      && uploaded.retentionDays === 3, `${job}: immutable archive attempt and exact uploader output required`);
    return uploaded;
  }
  function downloadById(job, id, path) {
    const runtime = id === runtimeArtifactId;
    const backend = id === backendArtifactId;
    requireValue(runtime || backend || id === frontendArtifactId, `${job}: unknown trusted artifact ID`);
    const kind = runtime ? 'test-runtime' : backend ? 'backend' : 'frontend';
    const environment = { ARTIFACT_NEEDS_JSON: trustedNeeds, EXPECTED_ARTIFACT_ID: id };
    if (kind !== 'frontend') Object.assign(environment, {
      EXPECTED_MANIFEST_SHA: backend ? '${{ needs.backend.outputs.manifest-sha }}' : producerManifest,
      EXPECTED_SDK_VERSION: backend ? '${{ needs.backend.outputs.sdk-version }}' : producerSdk });
    if (job === 'backend-test') environment.ARTIFACT_SHARD = matrixShard;
    return localTransfer(job, localGet(kind, kind === 'frontend' ? 'frontend' : 'backend-build', path, undefined, kind === 'frontend'), environment);
  }
  for (const id of Object.keys(jobs)) {
    const checked = command(id, `${artifactStore} check`);
    const node = jobs[id].steps.findIndex(step => step.uses === 'actions/setup-node@v6');
    requireValue(checked.lines.join('\n') === `set -euo pipefail\n${artifactStore} check` && checked.step.if === undefined
      && node >= 0 && node < checked.index, `${id}: unconditional local disk/ACL check after Node is required`);
    const environment = id === 'backend-test' ? { ARTIFACT_SHARD: matrixShard } : {};
    requireValue(Object.keys(checked.step.env ?? {}).sort().join(',') === Object.keys(environment).sort().join(',')
      && Object.entries(environment).every(([key, value]) => checked.step.env[key] === value), `${id}: store preflight identity differs`);
    const firstGate = id === 'frontend' ? 'npm ci' : ['backend-format', 'backend-build'].includes(id) ? restore
      : id === 'deploy' ? 'node scripts/verify-xsenus-runner.mjs build' : 'node scripts/prepare-windows-ci-toolchain.mjs';
    requireValue(checked.index < command(id, firstGate).index, `${id}: local store preflight must precede gates`);
  }
  const feUpload = archiveUpload('frontend', `frontend-${sha}-${attempt}`, 'frontend.tar.gz', 'frontend-upload', 'frontend-artifact-id');
  const frontendCommands = [
    'npm ci', 'node scripts/validate-deploy-workflow.mjs',
    'bash scripts/prune-deploy-backups.test.sh', 'bash scripts/preserve-data-protection-keys.test.sh',
    'node --test scripts/storage-legacy-backfill.node-tests.mjs', 'npm run lint',
    'npm run typecheck', 'npm run audit:dates:test', 'npm run audit:encoding',
    'npm run audit:surface:test && npm run audit:surface', 'npm audit --audit-level=moderate',
    'npm run test:unit:serial', 'npm run test:integration',
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
  const formatRestore = command('backend-format', restore);
  const formats = formatCommands.map(value => command('backend-format', value));
  requireValue(formats.every((row, index) => row.index > (index === 0 ? formatRestore.index : formats[index - 1].index)
    && row.lines.length === 1), 'Strict restore must precede all seven complete semantic format processes');
  const formatSteps = jobs['backend-format'].steps.filter(step => typeof step.run === 'string'
    && step.run.split('\n').some(line => line.trim().startsWith('dotnet format ')));
  requireValue(formatSteps.length === 7, 'Cover all five projects with exactly seven complete format processes');
  requireValue(Object.keys(jobs['backend-format'].env ?? {}).join(',') === 'DOTNET_GCHeapHardLimit',
    'Fresh format workspace may not inherit local artifact paths or skipped project references');
  for (const [index, row] of formats.entries()) {
    if (index >= 1 && index <= 3) {
      requireValue(JSON.stringify(row.step.env) === JSON.stringify({ DOTNET_GCHeapHardLimit: '240000000',
        CompilerResponseFile: '${{ github.workspace }}/scripts/compiler-serial.rsp' }),
      'Each full Migrations category uses the measured nine-GiB serial compiler profile');
    } else requireValue(row.step.env === undefined, 'Other full project formats use the measured six-GiB profile without a response file');
  }
  requireValue(command('frontend', 'node scripts/verify-xsenus-runner.mjs build').index < fe[0].index,
    'Trusted host preflight must precede frontend tools');
  const accessTests = command('frontend', 'node --test scripts/windows-vps-access.node-tests.mjs');
  const access = command('frontend', 'node scripts/windows-vps-deploy.mjs --check-access');
  requireValue(command('frontend', 'node scripts/verify-xsenus-runner.mjs build').index < accessTests.index
    && accessTests.index < access.index && access.index < fe[0].index,
  'Read-only authenticated access and its regressions must fail before long release gates');
  requireValue(access.lines.join('\n') === 'node scripts/windows-vps-deploy.mjs --check-access'
    && JSON.stringify(access.step.env) === JSON.stringify({ DEPLOY_SHA: sha, DEPLOY_ARCHIVE_NAME: '${{ env.TAR_NAME }}',
      VPS_SSH_HOST_FINGERPRINT: '${{ vars.VPS_SSH_HOST_FINGERPRINT }}', VPS_SSH_KEY: '${{ secrets.VPS_SSH_KEY }}' }),
  'Early access must use the pinned SSH identity with no deployment command or custom context');

  for (const id of ['backend-format', 'backend-build', 'backend-test', 'backend-browser', 'backend']) {
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
  const build = command('backend-build', `dotnet build ${project} --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false`);
  const toolBuild = command('backend-build', `dotnet build ${toolProject} --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false`);
  const discover = command('backend-build', `${tool} discover --assembly ${assembly} --manifest test-plan.json --revision "${sha}" --shards 4 --execution-profile bounded`);
  requireValue(discover.lines[0] === 'set -euo pipefail' && discover.step.id === 'plan', 'Discovery must preserve failure status and produce job outputs');
  requireValue(String(discover.step.env?.BackgroundServices__Enabled) === 'false' && String(discover.step.env?.Email__Enabled) === 'false', 'Discovery mail/background isolation missing');
  requireValue(jobs['backend-build'].outputs?.['manifest-sha'] === '${{ steps.plan.outputs.manifest-sha }}'
    && jobs['backend-build'].outputs?.['sdk-version'] === '${{ steps.plan.outputs.sdk-version }}', 'Consumers must use producer manifest hash and SDK outputs');
  requireValue(discover.lines.includes(`printf 'manifest-sha=%s\\n' "$(node -p "require('./test-plan.summary.json').ManifestSha256.toLowerCase()")" >> "$GITHUB_OUTPUT"`)
    && discover.lines.includes(`printf 'sdk-version=%s\\n' "$(dotnet --version)" >> "$GITHUB_OUTPUT"`), 'Discovery outputs must come from the producer summary and SDK');
  const runtimePack = command('backend-build', `tar -czf test-runtime.tar.gz test-plan.json test-plan.summary.json backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/PayPlanner.TestShards/bin/Release/net9.0`);
  const runtimeUpload = archiveUpload('backend-build', `test-runtime-${sha}-${attempt}`, 'test-runtime.tar.gz', 'runtime-upload', 'runtime-artifact-id');
  const publish = command('backend-build', 'dotnet publish "${{ env.BE_CSPROJ }}" -c Release --no-build --no-restore -o "${{ env.OUT_DIR }}/be"');
  const bePack = command('backend-build', 'tar -czf backend.tar.gz -C "${{ env.OUT_DIR }}" be');
  const beUpload = archiveUpload('backend-build', `backend-${sha}-${attempt}`, 'backend.tar.gz', 'backend-upload', 'backend-artifact-id');
  const beOrder = [beRestore, build, toolBuild, discover, runtimePack, runtimeUpload, publish, bePack, beUpload].map(item => item.index);
  requireValue(beOrder.every((value, index) => index === 0 || value > beOrder[index - 1]), 'Backend strict restore/build/discover/artifact order');
  const shard = command('backend-test', `${tool} run --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --shard ${matrixShard} --results test-results/shard-${matrixShard} --workers auto --suite all 2>&1 | tee test-results/shard-${matrixShard}.log`);
  requireValue(shard.lines.length === 3 && shard.lines[1] === 'mkdir -p test-results', 'Shard result directory must be fresh and created by the runner');
  const restoredSdk = "vstest_console=\"$(node --input-type=module --eval \"import { captureVstestEntryPoint } from './scripts/run-backend-gate.mjs'; process.stdout.write(captureVstestEntryPoint('test-plan.summary.json').path)\")\"";
  const restoredCommand = `dotnet exec "$vstest_console" ${assembly} --Settings:test-results/restored-browser/gate.runsettings --Logger:'trx;LogFileName=results.trx' --ResultsDirectory:test-results/restored-browser --TestCaseFilter:'${restoredFilter}' > test-results/restored-browser/test.log 2>&1 || native_exit=$?`;
  const restored = command('backend-browser', restoredCommand, false, true);
  const restoredVerification = `${tool} verify-restored --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --results test-results/restored-browser --exit-code "$native_exit"`;
  requireValue(restored.lines.length === 8 && restored.lines[1] === 'mkdir -p test-results/restored-browser'
    && restored.lines[3] === restoredSdk && restored.lines[4] === 'native_exit=0' && restored.lines[5] === restoredCommand
    && restored.lines[6] === 'cat test-results/restored-browser/test.log' && restored.lines[7] === restoredVerification,
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
  for (const gate of [shard, restored, union]) requireValue(gate.step.env?.EXPECTED_MANIFEST_SHA === '${{ needs.backend-build.outputs.manifest-sha }}', 'Consumers must use the trusted producer manifest hash');
  for (const [id, gate] of [['backend-test', shard], ['backend-browser', restored], ['backend', union]]) {
    requireValue(gate.lines[0] === 'set -euo pipefail', `${id}: preserve failure exit status`);
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '${{ needs.backend-build.outputs.sdk-version }}', `${id}: use producer SDK version`);
    const actualSdk = command(id, 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"');
    requireValue(actualSdk.step.env?.EXPECTED_SDK_VERSION === producerSdk
      && actualSdk.step.env?.RUNTIME_ARTIFACT_ID === runtimeArtifactId
      && actualSdk.lines.join('\n') === 'set -euo pipefail\nactual_sdk="$(dotnet --version)"\ntest "$actual_sdk" = "$EXPECTED_SDK_VERSION"\n[[ "$RUNTIME_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', `${id}: verify actual SDK and one nonempty producer runtime ID`);
    const downloaded = downloadById(id, runtimeArtifactId, 'test-artifacts');
    const unpack = command(id, 'tar -xzf test-artifacts/test-runtime.tar.gz');
    requireValue(jobs[id].steps.indexOf(sdk[0]) < actualSdk.index && actualSdk.index < downloaded.index
      && downloaded.index < unpack.index && unpack.index < gate.index, `${id}: SDK check and downloaded runtime must precede execution`);
    requireValue(!jobs[id].steps.some(step => /dotnet (?:build|publish|restore)|\bMSBuild\b/.test(step.run ?? '')), `${id}: test runtime must not be rebuilt`);
    if (id !== 'backend') requireValue(jobs[id].steps.filter(isLocalGet).length === 1,
      `${id}: download only the exact producer runtime; no fallback artifacts`);
  }
  for (const [id, gate] of [['backend-test', shard], ['backend-browser', restored]]) {
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
  const shardDiagnostic = artifact('backend-test', 'upload', `backend-shard-${matrixShard}-${sha}-${attempt}`, 'test-results/');
  const restoredDiagnostic = artifact('backend-browser', 'upload', `restored-browser-${sha}-${attempt}`, 'test-results/restored-browser/');
  const qualifiedAlias = artifact('backend-test', 'upload', qualifiedShardName(matrixShard), 'test-results/');
  requireValue(shard.index < shardDiagnostic.index && shardDiagnostic.index < qualifiedAlias.index
    && restored.index < restoredDiagnostic.index, 'Qualified aliases require successful tests and preserved attempt evidence first');
  requireValue(shardDiagnostic.retentionDays === 14 && restoredDiagnostic.retentionDays === 14 && qualifiedAlias.retentionDays === 14,
    'Retain attempt diagnostics independently from qualified content-addressed evidence');
  requireValue(shardDiagnostic.step.id === 'shard-diagnostics' && qualifiedAlias.step.id === 'qualified-upload', 'Bind qualification to the actual diagnostic CLI output');
  requireValue(Object.keys(jobs['backend-test'].outputs ?? {}).sort().join(',') === 'shard_0_id,shard_1_id,shard_2_id,shard_3_id'
    && [0, 1, 2, 3].every(index => jobs['backend-test'].outputs[`shard_${index}_id`] === `\${{ steps.qualified-upload.outputs.shard_${index}_id }}`), 'Four distinct matrix output identities are mandatory');
  for (let index = 0; index < 4; index += 1) {
    const result = artifact('backend', 'download', qualifiedShardName(index), 'test-results');
    requireValue(result.index < union.index, 'All four result artifacts must precede exact union verification');
  }
  requireValue(jobs.backend.steps.filter(isLocalGet).length === 5,
    'Aggregate must download only the producer runtime and four exact qualified aliases; no fallback artifacts');
  for (const id of ['backend-format', 'backend-build']) {
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '9.0.x', `${id}: .NET 9 SDK required`);
  }

  const deploy = jobs.deploy.steps;
  requireValue(jobs.backend.outputs?.['backend-artifact-id'] === '${{ needs.backend-build.outputs.backend-artifact-id }}', 'Aggregate must forward the same producer backend artifact ID after successful qualification');
  requireValue(jobs.backend.outputs?.['manifest-sha'] === producerManifest && jobs.backend.outputs?.['sdk-version'] === producerSdk,
    'Aggregate must forward the same producer manifest/SDK metadata after successful qualification');
  const downloads = deploy.map((step, index) => ({ step, index })).filter(({ step }) => isLocalGet(step));
  requireValue(downloads.length === 2, 'Deploy must download exactly the two qualified artifacts');
  downloadById('deploy', backendArtifactId, 'deploy-artifacts');
  downloadById('deploy', frontendArtifactId, 'deploy-artifacts');
  const deployIds = command('deploy', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]');
  requireValue(deployIds.step.env?.BACKEND_ARTIFACT_ID === backendArtifactId && deployIds.step.env?.FRONTEND_ARTIFACT_ID === frontendArtifactId
    && deployIds.lines.join('\n') === 'set -euo pipefail\n[[ "$BACKEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]\n[[ "$FRONTEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]\n[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]'
    && downloads.every(({ index }) => deployIds.index < index), 'Deploy must reject empty, multiple or substituted artifact IDs before downloading');
  const tar = command('deploy', 'tar --null --verbatim-files-from --no-recursion -czf "${{ env.TAR_NAME }}" -C "${{ env.OUT_DIR }}" -T "${{ env.OUT_DIR }}/deployment-files.nul"');
  const packageManifest = command('deploy', 'node scripts/prepare-deployment-package.mjs "${{ env.OUT_DIR }}" "${{ github.sha }}"');
  const unpackBackend = command('deploy', 'tar -xzf deploy-artifacts/backend.tar.gz -C "${{ env.OUT_DIR }}"');
  const unpackFrontend = command('deploy', 'tar -xzf deploy-artifacts/frontend.tar.gz -C "${{ env.OUT_DIR }}"');
  const bridge = command('deploy', 'node scripts/windows-vps-deploy.mjs');
  const bridgeTests = command('deploy', 'node --test scripts/windows-vps-deploy.node-tests.mjs');
  const trustedDeploy = command('deploy', 'node scripts/verify-xsenus-runner.mjs build');
  const readiness = command('deploy', 'node scripts/run-production-deploy-readiness.mjs', true);
  const requiredBridgeEnv = {
    ARTIFACT_NEEDS_JSON: '${{ toJSON(needs) }}',
    DEPLOY_SHA: sha, DEPLOY_ARCHIVE_NAME: '${{ env.TAR_NAME }}',
    VPS_SSH_HOST_FINGERPRINT: '${{ vars.VPS_SSH_HOST_FINGERPRINT }}', VPS_SSH_KEY: '${{ secrets.VPS_SSH_KEY }}',
    PAYPLANNER_WEBPUSH_ENABLED: "${{ vars.PAYPLANNER_WEBPUSH_ENABLED || 'false' }}",
    PAYPLANNER_WEBPUSH_PUBLIC_KEY: '${{ secrets.PAYPLANNER_WEBPUSH_PUBLIC_KEY }}',
    PAYPLANNER_WEBPUSH_PRIVATE_KEY: '${{ secrets.PAYPLANNER_WEBPUSH_PRIVATE_KEY }}',
    PAYPLANNER_WEBPUSH_SUBJECT: '${{ secrets.PAYPLANNER_WEBPUSH_SUBJECT }}',
  };
  requireValue(bridge.lines.join('\n') === 'node scripts/windows-vps-deploy.mjs'
    && JSON.stringify(bridge.step.env) === JSON.stringify(requiredBridgeEnv), 'Deploy requires the pinned native SSH bridge and structured secret environment');
  requireValue(bridge.step.id === 'vps-install' && readiness.step.id === 'production-readiness', 'Exact install/readiness outcomes must bind failure handling');
  const halt = command('deploy', 'node scripts/windows-vps-deploy.mjs --halt');
  requireValue(halt.index > readiness.index && halt.step.if === "always() && steps.vps-install.outcome == 'success' && steps.production-readiness.outcome != 'success'",
    'After unsuccessful readiness, preserve the guarded HALT barrier');
  const haltEnv = Object.fromEntries(Object.entries(requiredBridgeEnv).filter(([name]) => !name.startsWith('PAYPLANNER_WEBPUSH')));
  requireValue(JSON.stringify(halt.step.env) === JSON.stringify(haltEnv), 'Guarded HALT must use the same pinned SSH target and actual CI needs');
  requireValue(bridgeTests.lines.includes('node --test scripts/prepare-deployment-package.node-tests.mjs scripts/prepare-deployment-plan.node-tests.mjs'), 'Package and CI admission regressions are mandatory');
  requireValue(!deploy.some(step => step.uses?.startsWith('appleboy/')), 'Windows deployment must use native OpenSSH with Linux filesystem gates');
  requireValue(trustedDeploy.index < bridgeTests.index && bridgeTests.index < bridge.index,
    'Trusted host and bridge regression tests must precede Linux filesystem gates and deployment');
  requireValue(readiness.lines.includes('--expected-sha="${{ github.sha }}" \\')
    && readiness.lines.includes('--token="" \\') && readiness.lines.includes('--require-websocket=true \\')
    && readiness.lines.includes('--allow-websocket-auth-challenge=true \\'), 'Preserve exact-revision public production readiness without a stored JWT');
  requireValue(downloads.every(({ index }) => index < unpackBackend.index && index < unpackFrontend.index)
    && unpackBackend.index < packageManifest.index && unpackFrontend.index < packageManifest.index && packageManifest.index < tar.index
    && tar.index < bridge.index && bridge.index < readiness.index, 'Deploy download/unpack/tar/Linux-gates/SSH/readiness order');
  requireValue(!deploy.some(step => /(?:dotnet (?:test|build|publish)|npm (?:ci|run build))/.test(step.run ?? '')), 'Deploy must use qualified artifacts without rebuilding');
  const transportCounts = { frontend: [1, 0], 'backend-format': [0, 0], 'backend-build': [2, 0],
    'backend-test': [2, 1], 'backend-browser': [1, 1], backend: [0, 5], deploy: [0, 2] };
  for (const [id, [puts, gets]] of Object.entries(transportCounts)) requireValue(jobs[id].steps.filter(isLocalPut).length === puts
    && jobs[id].steps.filter(isLocalGet).length === gets, `${id}: unexpected/fallback artifact transfer`);
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
