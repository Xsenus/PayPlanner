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
const restoredFilter = 'FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource';
const restore = 'dotnet restore "${{ env.BE_SLN }}" --force -p:PayPlannerReleaseAudit=true';
const format = 'dotnet format "${{ env.BE_SLN }}" --verify-no-changes --no-restore --verbosity minimal';

export function validateDeployWorkflow(source) {
  const workflow = parseWorkflow(source);
  const jobs = workflow.jobs;
  const dependencies = {
    frontend: [], 'backend-format': [], 'backend-build': [],
    'backend-test': ['backend-build', 'backend-format', 'frontend'],
    'backend-browser': ['backend-build', 'backend-format', 'frontend'],
    backend: ['backend-browser', 'backend-build', 'backend-test'],
    deploy: ['backend', 'backend-format', 'frontend'],
  };
  requireValue(jobs && Object.keys(jobs).sort().join(',') === Object.keys(dependencies).sort().join(','), 'Expected exactly seven release jobs');
  requireValue(workflow.defaults === undefined, 'Custom workflow shell/defaults require explicit gate review');
  requireValue(workflow.on?.push?.branches?.join(',') === 'main' && Object.hasOwn(workflow.on, 'workflow_dispatch'), 'Keep main push and manual release triggers');
  const documentationPaths = ['README.md', 'AGENTS.md', 'docs/**/*.md', 'docs/yougile-sources/**'];
  requireValue(Object.keys(workflow.on).sort().join(',') === 'push,workflow_dispatch'
    && Object.keys(workflow.on.push).sort().join(',') === 'branches,paths-ignore'
    && workflow.on.push['paths-ignore']?.join(',') === documentationPaths.join(','),
  'Exclude only documentation from automatic releases; application and CI changes must still run all gates');
  const timeouts = { frontend: 30, 'backend-format': 30, 'backend-build': 30, 'backend-test': 150, 'backend-browser': 15, backend: 10, deploy: 30 };
  for (const [id, job] of Object.entries(jobs)) {
    requireValue(String(job['timeout-minutes']) === String(timeouts[id]), `${id}: keep the explicit runner time budget`);
    requireValue(!Object.hasOwn(job, 'if'), `${id}: conditional job can bypass a release gate`);
    const localRunner = ['backend-build', 'backend-test', 'backend-browser', 'backend'].includes(id);
    requireValue(localRunner
      ? job['runs-on']?.join(',') === 'self-hosted,Windows,X64,payplanner-xsenus'
        && JSON.stringify(job.defaults) === JSON.stringify({ run: { shell: 'bash' } })
      : job['runs-on'] === 'ubuntu-latest' && job.defaults === undefined,
    `${id}: trusted runner labels and reviewed failure-preserving shell required`);
    if (localRunner) {
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
      const diagnostic = step.uses === 'actions/upload-artifact@v6' && step.run === undefined && (
        (id === 'backend-test' && step.with?.name === `backend-shard-${matrixShard}-${sha}-${attempt}` && step.with?.path === 'test-results/')
        || (id === 'backend-browser' && step.with?.name === `restored-browser-${sha}-${attempt}` && step.with?.path === 'test-results/restored-browser/'));
      const qualifiedAlias = id === 'backend-test' && step.uses === 'actions/upload-artifact@v6' && step.run === undefined
        && step.with?.name === qualifiedShardName(matrixShard) && step.with?.path === 'test-results/';
      requireValue(step.if === undefined || (diagnostic && step.if === 'always()') || (qualifiedAlias && step.if === 'success()'), `${id}: conditional step can skip a release gate`);
      if (diagnostic) requireValue(step.if === 'always()' && step.with?.overwrite === undefined, `${id}: preserve immutable attempt evidence unconditionally`);
      if (qualifiedAlias) requireValue(step.if === 'success()' && step.with?.overwrite === true, `${id}: only a successful qualified alias may be replaced`);
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
  function artifact(job, direction, name, path) {
    const action = direction === 'upload' ? 'actions/upload-artifact@v6' : 'actions/download-artifact@v7';
    const found = jobs[job].steps.map((step, index) => ({ step, index })).filter(({ step }) => step.uses === action && step.with?.name === name);
    requireValue(found.length === 1, `${job}: missing unique SHA-bound ${direction} artifact ${name}`);
    const result = found[0];
    requireValue(result.step.with.path === path, `${job}: artifact path mismatch`);
    if (direction === 'upload') requireValue(result.step.with['if-no-files-found'] === 'error', `${job}: artifact must exist`);
    else requireValue(Object.keys(result.step.with).every(key => ['name', 'path'].includes(key)), `${job}: download must belong to this workflow run`);
    return result;
  }
  function archiveUpload(job, name, path, id, output) {
    const uploaded = artifact(job, 'upload', name, path);
    requireValue(uploaded.step.id === id && uploaded.step.if === undefined
      && jobs[job].outputs?.[output] === `\${{ steps.${id}.outputs.artifact-id }}`
      && Object.keys(uploaded.step.with).sort().join(',') === 'compression-level,if-no-files-found,name,path,retention-days'
      && uploaded.step.with['retention-days'] === '3' && uploaded.step.with['compression-level'] === '0', `${job}: immutable archive attempt and exact uploader output required`);
    return uploaded;
  }
  function downloadById(job, id, path) {
    const found = jobs[job].steps.map((step, index) => ({ step, index })).filter(({ step }) => step.uses === 'actions/download-artifact@v7' && step.with?.['artifact-ids'] === id);
    requireValue(found.length === 1 && found[0].step.with.path === path
      && Object.keys(found[0].step.with).sort().join(',') === 'artifact-ids,path', `${job}: download one trusted same-run artifact ID without fallback`);
    return found[0];
  }
  const feUpload = archiveUpload('frontend', `frontend-${sha}-${attempt}`, 'frontend.tar.gz', 'frontend-upload', 'frontend-artifact-id');
  const frontendCommands = [
    'npm ci', 'node scripts/validate-deploy-workflow.mjs',
    'bash scripts/prune-deploy-backups.test.sh', 'bash scripts/preserve-data-protection-keys.test.sh',
    'node --test scripts/storage-legacy-backfill.node-tests.mjs', 'npm run lint',
    'npx tsc -p tsconfig.app.json --noEmit', 'npm run audit:dates:test', 'npm run audit:encoding',
    'npm run audit:surface:test && npm run audit:surface', 'npm audit --audit-level=moderate',
    'npm run test:ci', 'npm run test:integration',
    'npm run test:legal-flags:strict -- --warn-on-feature-flags-summary-failure', 'npm run build',
    'tar -czf frontend.tar.gz -C "${{ env.OUT_DIR }}" fe',
  ];
  const fe = frontendCommands.map(value => command('frontend', value));
  const feBrowser = command('frontend', 'npx playwright install --with-deps chromium');
  requireValue(feBrowser.index > fe[0].index && feBrowser.index < fe[12].index,
    'Compatible Chromium must be installed after dependencies and before frontend integration');
  requireValue(fe.every(({ index }) => index < feUpload.index), 'Frontend gates/build must precede frontend upload');
  requireValue(fe.slice(1, 14).every(({ index }) => index > fe[0].index && index < fe[14].index), 'Frontend install/checks/build order');
  requireValue(fe[0].index < fe[1].index && fe[13].index < fe[14].index && fe[14].index < fe[15].index, 'Frontend install/legal/build/package order');
  requireValue(fe[13].step.env?.PAYPLANNER_READINESS_API_URL === '${{ env.FE_API_URL }}', 'Legal readiness API configuration missing');
  requireValue(fe[14].step.env?.VITE_API_URL === '${{ env.FE_API_URL }}', 'Frontend build API configuration missing');
  requireValue(command('backend-format', restore).index < command('backend-format', format).index, 'Strict restore must precede full format');

  const beRestore = command('backend-build', restore);
  const build = command('backend-build', `dotnet build ${project} --no-restore -c Release`);
  const toolBuild = command('backend-build', `dotnet build ${toolProject} --no-restore -c Release`);
  const discover = command('backend-build', `${tool} discover --assembly ${assembly} --manifest test-plan.json --revision "${sha}" --shards 4`);
  requireValue(discover.lines[0] === 'set -euo pipefail' && discover.step.id === 'plan', 'Discovery must preserve failure status and produce job outputs');
  requireValue(String(discover.step.env?.BackgroundServices__Enabled) === 'false' && String(discover.step.env?.Email__Enabled) === 'false', 'Discovery mail/background isolation missing');
  requireValue(jobs['backend-build'].outputs?.['manifest-sha'] === '${{ steps.plan.outputs.manifest-sha }}'
    && jobs['backend-build'].outputs?.['sdk-version'] === '${{ steps.plan.outputs.sdk-version }}', 'Consumers must use producer manifest hash and SDK outputs');
  requireValue(discover.lines.includes(`printf 'manifest-sha=%s\\n' "$(node -p "require('./test-plan.summary.json').ManifestSha256")" >> "$GITHUB_OUTPUT"`)
    && discover.lines.includes(`printf 'sdk-version=%s\\n' "$(dotnet --version)" >> "$GITHUB_OUTPUT"`), 'Discovery outputs must come from the producer summary and SDK');
  const runtimePack = command('backend-build', `tar -czf test-runtime.tar.gz test-plan.json test-plan.summary.json backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/PayPlanner.TestShards/bin/Release/net9.0`);
  const runtimeUpload = archiveUpload('backend-build', `test-runtime-${sha}-${attempt}`, 'test-runtime.tar.gz', 'runtime-upload', 'runtime-artifact-id');
  const publish = command('backend-build', 'dotnet publish "${{ env.BE_CSPROJ }}" -c Release --no-build --no-restore -o "${{ env.OUT_DIR }}/be"');
  const bePack = command('backend-build', 'tar -czf backend.tar.gz -C "${{ env.OUT_DIR }}" be');
  const beUpload = archiveUpload('backend-build', `backend-${sha}-${attempt}`, 'backend.tar.gz', 'backend-upload', 'backend-artifact-id');
  const beOrder = [beRestore, build, toolBuild, discover, runtimePack, runtimeUpload, publish, bePack, beUpload].map(item => item.index);
  requireValue(beOrder.every((value, index) => index === 0 || value > beOrder[index - 1]), 'Backend strict restore/build/discover/artifact order');
  const shard = command('backend-test', `${tool} run --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" --shard ${matrixShard} --results test-results/shard-${matrixShard} 2>&1 | tee test-results/shard-${matrixShard}.log`);
  requireValue(shard.lines.length === 3 && shard.lines[1] === 'mkdir -p test-results', 'Shard result directory must be fresh and created by the runner');
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
  for (const gate of [shard, restored, union]) requireValue(gate.step.env?.EXPECTED_MANIFEST_SHA === '${{ needs.backend-build.outputs.manifest-sha }}', 'Consumers must use the trusted producer manifest hash');
  for (const [id, gate] of [['backend-test', shard], ['backend-browser', restored], ['backend', union]]) {
    requireValue(gate.lines[0] === 'set -euo pipefail', `${id}: preserve failure exit status`);
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '${{ needs.backend-build.outputs.sdk-version }}', `${id}: use producer SDK version`);
    const actualSdk = command(id, 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"');
    requireValue(actualSdk.step.env?.EXPECTED_SDK_VERSION === producerSdk
      && actualSdk.step.env?.RUNTIME_ARTIFACT_ID === runtimeArtifactId
      && actualSdk.lines.join('\n') === 'set -euo pipefail\nactual_sdk="$(dotnet --version)"\ntest "$actual_sdk" = "$EXPECTED_SDK_VERSION"\n[[ "$RUNTIME_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]', `${id}: verify actual SDK and one nonempty producer runtime ID`);
    const downloaded = downloadById(id, runtimeArtifactId, 'test-artifacts');
    const unpack = command(id, 'tar -xzf test-artifacts/test-runtime.tar.gz');
    requireValue(jobs[id].steps.indexOf(sdk[0]) < actualSdk.index && actualSdk.index < downloaded.index
      && downloaded.index < unpack.index && unpack.index < gate.index, `${id}: SDK check and downloaded runtime must precede execution`);
    requireValue(!jobs[id].steps.some(step => /dotnet (?:build|publish|restore)|\bMSBuild\b/.test(step.run ?? '')), `${id}: test runtime must not be rebuilt`);
    if (id !== 'backend') requireValue(jobs[id].steps.filter(step => step.uses?.startsWith('actions/download-artifact@')).length === 1,
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
  requireValue(shardDiagnostic.step.with['retention-days'] === '14' && restoredDiagnostic.step.with['retention-days'] === '14', 'Retain attempt diagnostics independently from qualified aliases');
  requireValue(qualifiedAlias.step.with['retention-days'] === '14'
    && Object.keys(qualifiedAlias.step.with).sort().join(',') === 'if-no-files-found,name,overwrite,path,retention-days', 'Qualified alias options must retain exact identity, mandatory contents and bounded retention');
  for (let index = 0; index < 4; index += 1) {
    const result = artifact('backend', 'download', qualifiedShardName(index), 'test-results');
    requireValue(result.index < union.index, 'All four result artifacts must precede exact union verification');
  }
  requireValue(jobs.backend.steps.filter(step => step.uses?.startsWith('actions/download-artifact@')).length === 5,
    'Aggregate must download only the producer runtime and four exact qualified aliases; no fallback artifacts');
  for (const id of ['backend-format', 'backend-build']) {
    const sdk = jobs[id].steps.filter(step => step.uses === 'actions/setup-dotnet@v5');
    requireValue(sdk.length === 1 && sdk[0].with?.['dotnet-version'] === '9.0.x', `${id}: .NET 9 SDK required`);
  }

  const deploy = jobs.deploy.steps;
  requireValue(jobs.backend.outputs?.['backend-artifact-id'] === '${{ needs.backend-build.outputs.backend-artifact-id }}', 'Aggregate must forward the same producer backend artifact ID after successful qualification');
  const downloads = deploy.map((step, index) => ({ step, index })).filter(({ step }) => step.uses?.startsWith('actions/download-artifact@'));
  requireValue(downloads.length === 2, 'Deploy must download exactly the two qualified artifacts');
  downloadById('deploy', backendArtifactId, 'deploy-artifacts');
  downloadById('deploy', frontendArtifactId, 'deploy-artifacts');
  const deployIds = command('deploy', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]');
  requireValue(deployIds.step.env?.BACKEND_ARTIFACT_ID === backendArtifactId && deployIds.step.env?.FRONTEND_ARTIFACT_ID === frontendArtifactId
    && deployIds.lines.join('\n') === 'set -euo pipefail\n[[ "$BACKEND_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]\n[[ "$FRONTEND_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]\n[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]'
    && downloads.every(({ index }) => deployIds.index < index), 'Deploy must reject empty, multiple or substituted artifact IDs before downloading');
  const tar = command('deploy', 'tar -czf "${{ env.TAR_NAME }}" -C "${{ env.OUT_DIR }}" .');
  const unpackBackend = command('deploy', 'tar -xzf deploy-artifacts/backend.tar.gz -C "${{ env.OUT_DIR }}"');
  const unpackFrontend = command('deploy', 'tar -xzf deploy-artifacts/frontend.tar.gz -C "${{ env.OUT_DIR }}"');
  const scp = deploy.findIndex(step => step.uses?.startsWith('appleboy/scp-action@'));
  const ssh = deploy.findIndex(step => step.uses?.startsWith('appleboy/ssh-action@'));
  const readiness = command('deploy', 'node scripts/run-production-deploy-readiness.mjs', true);
  requireValue(downloads.every(({ index }) => index < unpackBackend.index && index < unpackFrontend.index)
    && unpackBackend.index < tar.index && unpackFrontend.index < tar.index
    && tar.index < scp && scp < ssh && ssh < readiness.index, 'Deploy download/unpack/tar/scp/ssh/readiness order');
  requireValue(!deploy.some(step => /(?:dotnet (?:test|build|publish)|npm (?:ci|run build))/.test(step.run ?? '')), 'Deploy must use qualified artifacts without rebuilding');
  return { valid: true, gates: ['frontend', 'backend-format', 'backend'], deployNeeds: jobs.deploy.needs, artifacts: downloads.map(({ step }) => step.with['artifact-ids']) };
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
