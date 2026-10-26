import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

// These files are executable test inputs, despite their Markdown extension.
// Unknown documentation remains conservative unless explicitly reviewed prose
// is supplied through untestedDocs, just like verification-inputs.mjs.
export const testedDocumentation = Object.freeze({
  'README.md': ['documentation-contracts', 'frontend-process-contracts'],
  'docs/quality-gates.md': ['documentation-contracts'],
  'docs/release-note-policy.md': ['documentation-contracts'],
  'docs/deploy-runbook.md': ['documentation-contracts'],
  'docs/system-surface-inventory.generated.md': ['documentation-contracts', 'surface-audit'],
  'docs/batch-operations-audit-coverage-2026-06-10.md': ['documentation-contracts'],
  'docs/file-upload-limits-2026-06-10.md': ['documentation-contracts'],
  'docs/hosted-service-lifecycle-coverage-2026-06-10.md': ['documentation-contracts'],
});

const frontendChecks = ['frontend-guard', 'frontend-unit', 'frontend-lint', 'frontend-types', 'frontend-build'];
const backendChecks = ['backend-format', 'backend-build', 'backend-full'];
const commonChecks = ['source-encoding', 'diff-whitespace'];
const commands = Object.freeze({
  'source-encoding': ['npm run audit:encoding'],
  'diff-whitespace': ['git diff --check', 'git diff --cached --check'],
  'roadmap-progress': ['npm run roadmap:active-progress'],
  'documentation-contracts': ['dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~DocumentationProcessTests|FullyQualifiedName~BatchOperationAuditCoverageTests|FullyQualifiedName~FileUploadLimitsDocumentationTests|FullyQualifiedName~HostedServiceLifecycleCoverageTests|FullyQualifiedName~ProductionTlsResponsibilityTests"'],
  'frontend-process-contracts': ['npx vitest run scripts/test-performance-config.test.ts'],
  'frontend-guard': ['npm run test:guard'],
  'frontend-unit': ['npm run test:unit:after-guard'],
  'frontend-lint': ['npm run lint'],
  'frontend-types': ['npm run typecheck'],
  'frontend-build': ['npm run build'],
  'frontend-integration': ['npm run test:integration'],
  'frontend-audit': ['npm audit --audit-level=moderate'],
  'date-audit': ['npm run audit:dates:test'],
  'surface-audit': ['npm run audit:surface:test', 'npm run audit:surface'],
  'legal-flags-readiness': ['npm run test:legal-flags:strict -- --warn-on-feature-flags-summary-failure'],
  'backend-format': ['dotnet format backend/PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal'],
  'backend-build': ['dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release'],
  'backend-full': ['dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-build --no-restore -c Release'],
  'backend-dependency-audit': ['dotnet list backend/PayPlanner.Api.sln package --vulnerable --include-transitive'],
  'reference-cache-contracts': ['npm run audit:reference-cache'],
  'workflow-contracts': ['node scripts/validate-deploy-workflow.mjs', 'node --test scripts/xsenus-artifact-store.node-tests.mjs', 'npx vitest run scripts/validate-deploy-workflow.test.ts scripts/verify-xsenus-runner.test.ts scripts/prepare-windows-ci-toolchain.test.ts'],
  'deploy-shell-contracts': ['bash scripts/prune-deploy-backups.test.sh', 'bash scripts/preserve-data-protection-keys.test.sh'],
  'storage-backfill-contracts': ['node --test scripts/storage-legacy-backfill.node-tests.mjs'],
  'notification-observer-contracts': ['node --test scripts/task-notification-refresh-observer.node-tests.mjs'],
});
const instructions = Object.freeze({
  'backend-migrations': 'Preserve clean and upgrade migration cases in the complete backend catalog. Use only existing compatible database servers and isolated project test data.',
  'backend-restored-browser': 'Execute the dedicated backend-browser workflow gate with PAYPLANNER_TEST_RESTORED_BROWSER=true and the qualified producer runtime; require all five restored cases.',
  'security-regressions': 'Select the changed authentication/permission success, denial, boundary and regression cases; the complete backend gate remains mandatory for the final candidate.',
  'manual-scope-review': 'Review the unknown path and external effects before selecting specialized checks; this conservative plan already requires both application sides.',
  'deploy-shell-contracts': 'Linux CI must execute the filesystem permission and symlink assertions; a Windows Git Bash result does not substitute for that platform gate.',
});
const releaseChecks = [...commonChecks, 'documentation-contracts', ...frontendChecks, 'frontend-integration',
  'frontend-audit', 'date-audit', 'surface-audit', 'legal-flags-readiness', ...backendChecks,
  'backend-dependency-audit', 'reference-cache-contracts', 'backend-migrations', 'backend-restored-browser',
  'workflow-contracts', 'deploy-shell-contracts', 'storage-backfill-contracts', 'notification-observer-contracts'];
const testedPaths = new Map(Object.entries(testedDocumentation).map(([path, checks]) => [path.toLowerCase(), checks]));

export function normalizeChangedPath(value) {
  if (typeof value !== 'string' || !value || /[\r\n\0]/.test(value)) throw new Error('Expected a nonempty repository-relative path');
  const path = value.replaceAll('\\', '/').replace(/^\.\//, '');
  if (path.startsWith('/') || /^[a-z]:/i.test(path) || path.split('/').some(part => !part || part === '..' || part === '.')) {
    throw new Error('Changed paths must stay inside the repository: ' + value);
  }
  return path;
}

function classifyPath(path, prose) {
  const lower = path.toLowerCase();
  if (testedPaths.has(lower)) return { area: 'documentation', reason: 'Document is read by regression tests; run its guards early.', checks: [...testedPaths.get(lower)] };
  if (lower === 'agents.md' || prose.has(lower)) return { area: 'documentation', reason: 'Explicitly reviewed prose; encoding and documentation checks still apply.', checks: ['roadmap-progress'] };
  if (/^src\/(?:services|types|config)\//.test(lower) || /^src\/types\.tsx?$/.test(lower)
    || /^backend\/(?:controllers|models|data|hubs|filters|payplanner\.api\.migrations)\//.test(lower)
    || /^backend\/(?:program|startup)\.cs$/.test(lower)) {
    const checks = ['frontend-integration'];
    if (/\/(?:data|payplanner\.api\.migrations)\//.test(lower)) checks.push('backend-migrations', 'backend-restored-browser');
    if (/(?:auth|permission|role|security)/.test(lower)) checks.push('security-regressions');
    return { area: 'shared', reason: 'API, model, permissions or shared configuration can affect both application sides.', checks };
  }
  if (/^(?:package(?:-lock)?\.json|global\.json|(?:directory\.build|directory\.packages)\.(?:props|targets)|nuget\.config)$/i.test(path)
    || /\.(?:csproj|sln|props|targets)$/.test(lower) || /^(?:\.github|vendor|tools|scripts)\//.test(lower)
    || /^(?:vite|vitest|eslint|tailwind|postcss|tsconfig)[^.]*\./.test(lower)
    || /^(?:dockerfile|docker-compose|compose\.)/.test(lower) || /^backend\/appsettings[^/]*\.json$/.test(lower)) {
    return { area: 'shared', reason: 'Dependency, test tooling or infrastructure input invalidates narrow assumptions.',
      checks: ['frontend-integration', 'frontend-audit', 'backend-dependency-audit', 'workflow-contracts', 'backend-restored-browser'] };
  }
  if (/^src\/(?:components|hooks|contexts|utils|test|build)\/.+\.(?:[cm]?[jt]sx?|css|json)$/.test(lower)
    || /^src\/(?:app|main|index)\.(?:tsx?|css)$/.test(lower)
    || /^public\/.+\.(?:js|css|svg|png|jpe?g|webp|ico|woff2?|webmanifest|json|txt)$/.test(lower)
    || lower === 'index.html') {
    const checks = /\.integration\.test\./.test(lower) || /^public\/.*(?:worker|cache)/.test(lower) ? ['frontend-integration'] : [];
    return { area: 'frontend', reason: 'Known frontend implementation or test area.', checks };
  }
  if (/^backend\/(?:services|helpers|extensions|payplanner\.api\.tests)\/.+\.cs$/.test(lower)
    || /^backend\/appreleases\/releases\.json$/.test(lower)) {
    const checks = /(?:migration|restore|backup)/.test(lower) ? ['backend-migrations', 'backend-restored-browser'] : [];
    if (/(?:auth|permission|role|security)/.test(lower)) checks.push('security-regressions');
    return { area: 'backend', reason: 'Known backend implementation or test area; no shared contract path changed.', checks };
  }
  return { area: 'unknown', reason: 'Unclassified input, including unknown documentation or fixtures: expand conservatively.',
    checks: ['frontend-integration', 'backend-migrations', 'backend-restored-browser', 'workflow-contracts', 'manual-scope-review'] };
}

export function planVerificationScope(changes, { untestedDocs = [] } = {}) {
  if (!Array.isArray(changes)) throw new Error('Changes must be an array');
  const prose = new Set(untestedDocs.map(value => {
    const path = normalizeChangedPath(value);
    if (!/\.(?:md|rst|txt)$/i.test(path) || /[*?\[\]{}]/.test(path)) throw new Error('Only exact reviewed prose paths may be marked untested');
    return path.toLowerCase();
  }));
  const paths = [...new Set(changes.flatMap(change => {
    if (typeof change === 'string') return [normalizeChangedPath(change)];
    if (!change || typeof change !== 'object' || !change.path) throw new Error('Each change needs a path');
    return [normalizeChangedPath(change.path), ...(change.oldPath ? [normalizeChangedPath(change.oldPath)] : [])];
  }))].sort();
  const classified = paths.map(path => ({ path, ...classifyPath(path, prose) }));
  const areas = new Set(classified.map(entry => entry.area));
  const both = areas.has('unknown') || areas.has('shared') || (areas.has('frontend') && areas.has('backend'));
  const scope = !paths.length ? 'none' : both ? 'both' : areas.has('frontend') ? 'frontend' : areas.has('backend') ? 'backend' : 'documentation';
  const checks = new Set(paths.length ? commonChecks : []);
  if (both || scope === 'frontend') frontendChecks.forEach(value => checks.add(value));
  if (both || scope === 'backend') backendChecks.forEach(value => checks.add(value));
  for (const entry of classified) entry.checks.forEach(value => checks.add(value));
  const checkDetails = Object.fromEntries([...new Set([...checks, ...releaseChecks])].map(id => [id, {
    commands: [...(commands[id] ?? [])],
    ...(instructions[id] ? { instructions: instructions[id] } : {}),
  }]));
  return {
    schema: 1, scope, changedPaths: classified,
    checkDetails,
    execution: 'Planning only. Backend commands require prepared build/restore inputs, explicit BackgroundServices__Enabled=false and Email__Enabled=false, and isolated test data; no database server is provisioned by this planner.',
    development: { requiredChecks: [...checks], purpose: 'Select affected checks while implementing one release batch.' },
    release: {
      requiredChecks: [...releaseChecks],
      ciSkippingAllowed: false,
      reuse: 'No result is qualified by this path plan. Reuse needs a successful full gate with matching code, config, dependencies, catalog, data, environment and artifact fingerprints.',
      reason: 'The current mandatory CI barrier stays complete. Area selection alone cannot omit jobs or qualify an unchanged-side deployment artifact.',
    },
  };
}

export function parseNameStatus(output) {
  const tokens = output.split('\0');
  if (tokens.at(-1) === '') tokens.pop();
  const changes = [];
  for (let index = 0; index < tokens.length;) {
    const status = tokens[index++];
    if (!/^(?:[AMDTUXB]|[RC]\d{1,3})$/.test(status)) throw new Error('Unexpected git change status: ' + status);
    const first = tokens[index++];
    if (!first) throw new Error('Incomplete git change record');
    if (/^[RC]/.test(status)) {
      const path = tokens[index++];
      if (!path) throw new Error('Missing rename/copy destination');
      changes.push({ status, oldPath: normalizeChangedPath(first), path: normalizeChangedPath(path) });
    } else changes.push({ status, path: normalizeChangedPath(first) });
  }
  return changes;
}

export function discoverChangedPaths(root = process.cwd(), base) {
  root = resolve(root);
  const git = args => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, maxBuffer: 32 * 1024 * 1024 });
  if (base !== undefined && !/^[a-f0-9]{7,40}$/i.test(base)) throw new Error('--base must be a commit SHA');
  const baseRevision = git(['rev-parse', '--verify', `${base ?? 'HEAD'}^{commit}`]).trim();
  const revision = git(['rev-parse', '--verify', 'HEAD^{commit}']).trim();
  const options = ['--name-status', '-z', '--find-renames', '--no-ext-diff'];
  const committed = parseNameStatus(git(['diff', ...options, baseRevision, revision, '--']));
  const staged = parseNameStatus(git(['diff', '--cached', ...options, '--']));
  const unstaged = parseNameStatus(git(['diff', ...options, '--']));
  const untracked = git(['ls-files', '--others', '--exclude-standard', '-z']).split('\0').filter(Boolean)
    .map(path => ({ status: '?', path: normalizeChangedPath(path) }));
  return { root, baseRevision, revision, changes: [...committed, ...staged, ...unstaged, ...untracked] };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    let base;
    const untestedDocs = [];
    const paths = [];
    for (let index = 2; index < process.argv.length; index++) {
      const arg = process.argv[index];
      if (arg === '--base' || arg === '--untested-doc') {
        const value = process.argv[++index];
        if (!value || value.startsWith('--')) throw new Error('Missing value for ' + arg);
        if (arg === '--base') { if (base) throw new Error('Only one --base is allowed'); base = value; }
        else untestedDocs.push(value);
      } else if (arg === '--help') {
        console.log('Usage: node scripts/verification-scope.mjs [--base COMMIT_SHA] [--untested-doc EXACT_PATH] [changed/path ...]\nWith no paths, inspect committed, staged, unstaged and untracked Git changes. This prints a plan; it does not run checks, qualify evidence or skip CI.');
        process.exit(0);
      } else if (arg.startsWith('-')) throw new Error('Unknown option: ' + arg);
      else paths.push(arg);
    }
    if (base && paths.length) throw new Error('Use --base for Git discovery or explicit paths, not both');
    const source = paths.length ? { changes: paths } : discoverChangedPaths(process.cwd(), base);
    const { changes, ...git } = source;
    console.log(JSON.stringify({ ...planVerificationScope(changes, { untestedDocs }), git }, null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
