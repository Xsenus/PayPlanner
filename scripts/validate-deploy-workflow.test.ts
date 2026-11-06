// @vitest-environment node

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { describe, expect, it } from 'vitest';
import { parseWorkflow, validateDeployWorkflow } from './validate-deploy-workflow.mjs';

const workflow = readFileSync(resolve('.github/workflows/deploy.yml'), 'utf8').replaceAll('\r\n', '\n');
const shardJobs = [0, 1, 2, 3].map(index => `backend-test-${index}`);
const allJobs = ['frontend', 'backend-format', 'backend-build', ...shardJobs, 'backend-test', 'backend-browser', 'backend', 'deploy'];
const browserNeeds = 'needs: [backend-build, backend-format, frontend, backend-test]';
const aggregateNeeds = 'needs: [backend-build, backend-test, backend-browser]';

function replaceOnce(before: string, after: string) {
  // A missing fixture must fail this test, not satisfy the surrounding
  // toThrow assertion before the workflow validator was even called.
  expect.soft(workflow).toContain(before);
  return workflow.replace(before, after);
}

function replaceInJob(id: string, change: (job: string) => string) {
  const header = `  ${id}:\n`;
  const begin = workflow.indexOf(header);
  expect.soft(begin).toBeGreaterThan(0);
  if (begin <= 0) return workflow;
  const following = begin + header.length;
  const next = workflow.slice(following).search(/^ {2}[a-z][a-z0-9-]*:\n/m);
  const end = next < 0 ? workflow.length : following + next;
  return workflow.slice(0, begin) + change(workflow.slice(begin, end)) + workflow.slice(end);
}

describe('merged production workflow protections', () => {
  it.each([...shardJobs, 'backend-browser'])('requires verified Chromium and Chrome preparation for %s', id => {
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      'node scripts/prepare-ci-browsers.mjs', 'echo browser ready',
    )))).toThrow();
  });

  it('retains every mandatory gate and four independent qualified workers', () => {
    const jobs = parseWorkflow(workflow).jobs;
    expect(Object.keys(jobs).sort()).toEqual(allJobs.toSorted());
    expect(jobs['backend-test'].needs.toSorted()).toEqual(['backend-build', 'backend-format', 'frontend', ...shardJobs].toSorted());
    expect(jobs['backend-format'].needs).toEqual(['frontend']);
    expect(jobs['backend-build'].needs).toEqual(['backend-format']);
    expect(jobs['backend-browser'].needs).toContain('backend-test');
    expect(jobs.backend.needs).toContain('backend-test');
  });

  it.each(shardJobs)('refuses bypassing or substituting the mandatory qualified gate output for %s', worker => {
    const index = shardJobs.indexOf(worker);
    const reference = `shard_${index}_id: \${{ needs.${worker}.outputs.qualified-artifact-id }}`;
    expect(() => validateDeployWorkflow(replaceOnce(reference, `shard_${index}_id: latest`))).toThrow();
    expect(() => validateDeployWorkflow(replaceInJob('backend-test', job => job.replace(worker + (index === 3 ? ']' : ', '), index === 3 ? ']' : '')))).toThrow();
  });

  it.each([
    ['unsafe workflow cancellation', '  cancel-in-progress: false', '  cancel-in-progress: true'],
    ['missing qualified gate verifier', 'run: node scripts/verify-qualified-shard-outputs.mjs', 'run: echo qualified'],
    ['untrusted qualified gate context', '          ARTIFACT_NEEDS_JSON: ${{ toJSON(needs) }}', '          ARTIFACT_NEEDS_JSON: {}'],
    ['compiler daemon', '  UseSharedCompilation: "false"', '  UseSharedCompilation: "true"'],
    ['unbounded node heap', '  NODE_OPTIONS: "--max-old-space-size=1536"', '  NODE_OPTIONS: "--max-old-space-size=8192"'],
    ['global migration bypass', 'env:\n  PAYPLANNER_CI_ARTIFACT_ROOT:', 'env:\n  PayPlannerSkipMigrationsBuild: "true"\n  PAYPLANNER_CI_ARTIFACT_ROOT:'],
    ['changed compiler response file', 'CompilerResponseFile: ${{ github.workspace }}/scripts/compiler-serial.rsp', 'CompilerResponseFile: C:/unreviewed.rsp'],
    ['early SSH preflight removed', 'run: node scripts/windows-vps-deploy.mjs --check-access', 'run: echo access'],
    ['early SSH preflight tests removed', 'run: node --test scripts/windows-vps-access.node-tests.mjs', 'run: echo tests'],
  ])('refuses %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it.each(['whitespace', 'style', 'analyzers'])('keeps the complete %s migration format gate and its resource profile', mode => {
    const command = `run: dotnet format ${mode} backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic`;
    expect(() => validateDeployWorkflow(replaceOnce(command, 'run: echo formatted'))).toThrow();
    const job = parseWorkflow(workflow).jobs['backend-format'];
    const step = job.steps.find((step: { run?: string }) => step.run === command.slice(5));
    expect(step.env).toEqual({ DOTNET_GCHeapHardLimit: mode === 'analyzers' ? '280000000' : '240000000', CompilerResponseFile: '${{ github.workspace }}/scripts/compiler-serial.rsp' });
  });

  it.each([
    ['analyzers', '240000000'],
    ['analyzers', '300000000'],
    ['style', '280000000'],
    ['whitespace', '280000000'],
  ])('rejects an unreviewed %s heap %s', (mode, heap) => {
    const phase = mode === 'analyzers' ? 'analyzer' : mode;
    const expected = mode === 'analyzers' ? '280000000' : '240000000';
    const proofCondition = "${{ steps.format-reuse.outputs.reused != 'true' }}";
    const before = `      - name: Verify all Migrations ${phase} formatting\n        if: ${proofCondition}\n        env:\n          DOTNET_GCHeapHardLimit: "${expected}"`;
    expect(() => validateDeployWorkflow(replaceOnce(before, before.replace(expected, heap)))).toThrow();
  });
});

describe('parallel deploy workflow release contract', () => {
  it.each([
    ['push', 'refs/heads/main', false, true, true],
    ['push', 'refs/heads/feature', true, true, false],
    ['workflow_dispatch', 'refs/heads/main', false, true, false],
    ['workflow_dispatch', 'refs/heads/main', true, true, true],
    ['workflow_dispatch', 'refs/heads/claude/practical-bardeen-pglcf3', true, true, false],
    ['workflow_dispatch', 'refs/heads/main', true, false, false],
    ['push', 'refs/heads/main', false, false, false],
  ])('gates deployment for %s on %s with explicit choice %s and success %s', (event, ref, choice, success, expected) => {
    const expression = parseWorkflow(workflow).jobs.deploy.if;
    expect(expression).toMatch(/^\$\{\{ .* \}\}$/);
    expect(runInNewContext(expression.slice(3, -2), {
      success: () => success,
      github: { event_name: event, ref },
      inputs: { release_to_vps: choice },
    })).toBe(expected);
  });

  it('requires explicit manual deployment on main after successful verification', () => {
    expect(() => validateDeployWorkflow(workflow)).not.toThrow();
    for (const [before, after] of [
      ['        default: false', '        default: true'],
      ['        type: boolean', '        type: string'],
      ['        required: true', '        required: false'],
      ["github.ref == 'refs/heads/main'", "github.ref != 'refs/heads/main'"],
      ['success() && ', ''],
      ['inputs.release_to_vps == true', 'inputs.release_to_vps != true'],
      ["github.event_name == 'workflow_dispatch' && inputs.release_to_vps == true", "github.event_name == 'workflow_dispatch'"],
    ]) expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it.each(allJobs)(
    'pins %s to the trusted Windows host and safe checkout', id => {
      for (const [before, after] of [
        ['runs-on: [self-hosted, Windows, X64, payplanner-xsenus]', 'runs-on: ubuntu-latest'],
        ['shell: bash', 'shell: powershell'],
        ['persist-credentials: false', 'persist-credentials: true'],
      ]) {
        const changed = replaceInJob(id, job => job.replace(before, after));
        expect(() => validateDeployWorkflow(changed)).toThrow();
      }
    },
  );

  it.each(['backend-format', 'backend-build', ...shardJobs, 'backend-browser', 'backend'])(
    'prepares a writable SDK directory and executable PowerShell before setup-dotnet in %s', id => {
      for (const [before, after] of [
        ['DOTNET_INSTALL_DIR: ${{ runner.tool_cache }}/payplanner-dotnet', 'DOTNET_INSTALL_DIR: C:/Program Files/dotnet'],
        ['run: node scripts/prepare-windows-ci-toolchain.mjs', 'run: echo prepared'],
        ['node-version: "22"', 'node-version: "18"'],
      ]) {
        expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(before, after)))).toThrow();
      }
      expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
        '        uses: actions/setup-dotnet@v5\n',
        '        uses: actions/setup-dotnet@v5\n        env:\n          DOTNET_INSTALL_DIR: C:/Program Files/dotnet\n',
      )))).toThrow(/installation directory/);
    },
  );

  it.each([
    ['bridge tests', 'node --test scripts/windows-vps-deploy.node-tests.mjs', 'echo skipped'],
    ['native bridge', 'node scripts/windows-vps-deploy.mjs', 'echo deployed'],
    ['host pin', '${{ vars.VPS_SSH_HOST_FINGERPRINT }}', 'SHA256:unreviewed'],
    ['private key source', '${{ secrets.VPS_SSH_KEY }}', 'literal-key'],
    ['expected revision', 'DEPLOY_SHA: ${{ github.sha }}', 'DEPLOY_SHA: main'],
    ['native bridge arguments', 'run: node scripts/windows-vps-deploy.mjs', 'run: node scripts/windows-vps-deploy.mjs --skip-linux-tests'],
    ['readiness revision', '--expected-sha="${{ github.sha }}"', '--expected-sha=old'],
  ])('preserves the reviewed Windows SSH deployment contract: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceInJob('deploy', job => job.replace(before, after)))).toThrow();
  });

  it('binds actual successful producer results to the deployment bridge itself', () => {
    const header = '      - name: Verify Linux filesystem gates and deploy qualified package\n        env:\n';
    const needs = '          ARTIFACT_NEEDS_JSON: ${{ toJSON(needs) }}\n';
    expect(workflow).toContain(header + needs);
    for (const replacement of ['', '          ARTIFACT_NEEDS_JSON: {}\n']) {
      expect(() => validateDeployWorkflow(replaceInJob('deploy', job => job.replace(header + needs, header + replacement)))).toThrow();
    }
  });

  it.each([
    ['missing inventory', 'node scripts/prepare-deployment-package.mjs "${{ env.OUT_DIR }}" "${{ github.sha }}"', 'echo inventoried'],
    ['stale inventory revision', 'node scripts/prepare-deployment-package.mjs "${{ env.OUT_DIR }}" "${{ github.sha }}"', 'node scripts/prepare-deployment-package.mjs "${{ env.OUT_DIR }}" main'],
    ['recursive archive root', 'tar --null --no-recursion -czf "${{ env.TAR_NAME }}" -C "${{ env.OUT_DIR }}" -T - < "${{ env.OUT_DIR }}/deployment-files.nul"', 'tar -czf "${{ env.TAR_NAME }}" -C "${{ env.OUT_DIR }}" .'],
    ['non-NUL file list', 'tar --null --no-recursion -czf', 'tar --no-recursion -czf'],
    ['unbound file list', '-T - < "${{ env.OUT_DIR }}/deployment-files.nul"', '-T other-files.txt'],
  ])('rejects incomplete deployment package preparation: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceInJob('deploy', job => job.replace(before, after)))).toThrow();
  });

  it('requires inventory after unpacking and before creating the archive', () => {
    const inventory = '      - name: Prepare qualified deployment package\n        run: node scripts/prepare-deployment-package.mjs "${{ env.OUT_DIR }}" "${{ github.sha }}"\n\n';
    expect(workflow).toContain(inventory);
    const without = workflow.replace(inventory, '');
    const early = without.replace('      - name: Unpack qualified artifacts\n', inventory + '      - name: Unpack qualified artifacts\n');
    const late = without.replace('      - name: Verify Linux filesystem gates and deploy qualified package\n', inventory + '      - name: Verify Linux filesystem gates and deploy qualified package\n');
    expect(() => validateDeployWorkflow(early)).toThrow(/order/);
    expect(() => validateDeployWorkflow(late)).toThrow(/order/);
  });

  it('requires reviewed deployment plan tests before frontend dependency installation', () => {
    const step = '      - name: Test reviewed deployment plan\n        run: node --test scripts/prepare-deployment-plan.node-tests.mjs\n\n';
    expect(workflow).toContain(step);
    const missing = workflow.replace(step, '');
    expect(() => validateDeployWorkflow(missing)).toThrow();
    const late = missing.replace('      - name: Build FE\n', step + '      - name: Build FE\n');
    expect(late).not.toBe(missing);
    expect(() => validateDeployWorkflow(late)).toThrow(/before long release gates/);
  });

  it('rejects a new database server and parallel memory-heavy shards', () => {
    expect(() => validateDeployWorkflow(replaceInJob('backend-test-0', job => job.replace(
      '    steps:', '    services:\n      postgres:\n        image: postgres:17-alpine\n    steps:',
    )))).toThrow(/new servers/);
    expect(() => validateDeployWorkflow(replaceInJob('backend-test-1', job => job.replace(
      'queue: max', 'queue: single',
    )))).toThrow(/queue/);
    expect(() => validateDeployWorkflow(replaceInJob('backend-test-1', job => job.replace(
      'cancel-in-progress: false', 'cancel-in-progress: true',
    )))).toThrow(/canceling/);
  });

  it('requires a verified portable package and dedicated PostgreSQL preflight', () => {
    for (const mode of ['package', 'postgres', 'build']) {
      expect(() => validateDeployWorkflow(replaceOnce(
        `run: node scripts/verify-xsenus-runner.mjs ${mode}`, `run: echo ${mode}`,
      ))).toThrow();
    }
  });

  it('rejects collecting timing estimates before full qualification', () => {
    const begin = workflow.indexOf('      - name: Produce timing estimates from the qualified full run\n');
    const end = workflow.indexOf('      - name: Cache qualified backend timing estimates\n', begin);
    expect(begin).toBeGreaterThan(0);
    expect(end).toBeGreaterThan(begin);
    const profile = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('      - name: Verify complete backend case union\n', profile + '      - name: Verify complete backend case union\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/Timing profile/);
  });

  it('rejects caching a profile before it has been qualified and produced', () => {
    const begin = workflow.indexOf('      - name: Cache qualified backend timing estimates\n');
    const end = workflow.indexOf('  deploy:\n', begin);
    const cache = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('      - name: Produce timing estimates from the qualified full run\n', cache + '      - name: Produce timing estimates from the qualified full run\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/Timing cache/);
  });

  it('qualifies the actual workflow and its same-revision artifacts', () => {
    expect(validateDeployWorkflow(workflow)).toEqual({
      valid: true,
      gates: ['frontend', 'backend-format', 'backend'],
      deployNeeds: ['frontend', 'backend-format', 'backend'],
      artifacts: ['${{ needs.backend.outputs.backend-artifact-id }}', '${{ needs.frontend.outputs.frontend-artifact-id }}'],
    });
  });

  it('uses job dependencies rather than textual order of independent jobs', () => {
    const first = workflow.indexOf('  frontend:\n');
    const second = workflow.indexOf('  backend-format:\n');
    const third = workflow.indexOf('  backend:\n');
    const reordered = workflow.slice(0, first) + workflow.slice(second, third)
      + workflow.slice(first, second) + workflow.slice(third);
    expect(validateDeployWorkflow(reordered).valid).toBe(true);
  });

  it.each([...shardJobs, 'backend-browser'])('keeps expensive %s behind all preflight gates', id => {
    const originalNeeds = id === 'backend-browser'
      ? browserNeeds
      : 'needs: [backend-build, backend-format, frontend]';
    for (const needs of ['backend-build, backend-format', 'backend-build, frontend', 'backend-format, frontend']) {
      const changed = replaceInJob(id, job => job.replace(
        originalNeeds, `needs: [${needs}]`,
      ));
      expect(() => validateDeployWorkflow(changed)).toThrow(/dependencies/);
    }
  });

  it('runs restored browser checks after every bounded shard completes', () => {
    expect(() => validateDeployWorkflow(replaceInJob('backend-browser', job => job.replace(
      browserNeeds,
      'needs: [backend-build, backend-format, frontend]',
    )))).toThrow(/dependencies/);
  });

  it.each([
    ['unprofiled discovery', ' --execution-profile bounded', ''],
    ['unknown discovery profile', '--execution-profile bounded', '--execution-profile unrestricted'],
    ['missing worker budget', ' --workers auto', ''],
    ['unbounded workers', '--workers auto', '--workers 0'],
    ['fixed workers bypass resource adaptation', '--workers auto', '--workers 4'],
    ['missing full-suite selection', ' --suite all', ''],
    ['fast-only release', '--suite all', '--suite fast'],
    ['heavy-only release', '--suite all', '--suite heavy'],
  ])('rejects a bounded execution bypass: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow(/missing.*gate/);
  });

  it.each([
    ['ignore application code', '      - "README.md"', '      - "backend/**"'],
    ['ignore release infrastructure', '      - "README.md"', '      - ".github/**"'],
    ['ignore arbitrary documents used by tools', '      - "docs/**/*.md"', '      - "docs/**"'],
    ['remove manual release', '  workflow_dispatch:\n', '  disabled_dispatch:\n'],
    ['remove main trigger', 'branches: ["main"]', 'branches: ["other"]'],
  ])('rejects trigger changes that %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it.each(allJobs)(
    'bounds runner time for %s', id => {
      const invalidTimeouts = ['0', '360', '${{ vars.UNBOUNDED_TIMEOUT }}', ...(id === 'frontend' ? ['30'] : []), ...(shardJobs.includes(id) ? ['150', '180', '181', '299', '301'] : [])];
      for (const timeout of invalidTimeouts) {
        const changed = replaceInJob(id, job => job.replace(/timeout-minutes: \d+/, `timeout-minutes: ${timeout}`));
        expect(() => validateDeployWorkflow(changed)).toThrow(/time budget/);
      }
    },
  );

  it.each([
    ['missing dependency', 'needs: [frontend, backend-format, backend]', 'needs: [frontend, backend]'],
    ['duplicate dependency', 'needs: [frontend, backend-format, backend]', 'needs: [frontend, backend, backend]'],
    ['deploy always', "    if: ${{ success() && github.ref == 'refs/heads/main' && (github.event_name == 'push' || (github.event_name == 'workflow_dispatch' && inputs.release_to_vps == true)) }}", '    if: always()'],
    ['skipped frontend', '  frontend:\n', '  frontend:\n    if: false\n'],
    ['job error bypass', '  backend:\n', '  backend:\n    continue-on-error: true\n'],
    ['step error bypass', '        run: npm run lint', '        continue-on-error: true\n        run: npm run lint'],
    ['conditional legal gate', '        run: npm run test:legal-flags:strict', '        if: false\n        run: npm run test:legal-flags:strict'],
    ['commented legal gate', '        run: npm run test:legal-flags:strict', '        run: echo npm run test:legal-flags:strict'],
    ['removed validator', '        run: node scripts/validate-deploy-workflow.mjs', '        run: echo validator'],
    ['failure swallowed', '        run: npm run lint', '        run: |\n          npm run lint\n          true || true'],
    ['conditional shell wrapper', '        run: npm run lint', '        run: |\n          if false; then\n            npm run lint\n          fi'],
    ['shard cases filtered', '--shard 0 --results', '--filter Tiny --shard 0 --results'],
    ['pipe failure lost', '          set -euo pipefail\n          mkdir -p test-results\n', '          set -eu\n          mkdir -p test-results\n'],
    ['restored browser disabled', 'PAYPLANNER_TEST_RESTORED_BROWSER: "true"', 'PAYPLANNER_TEST_RESTORED_BROWSER: "false"'],
    ['mail enabled', 'Email__Enabled: "false"', 'Email__Enabled: "true"'],
    ['non-strict restore', '--force --disable-parallel -p:PayPlannerReleaseAudit=true', '--force --disable-parallel'],
    ['format disabled', '--verify-no-changes --no-restore --verbosity minimal', '--no-restore --verbosity minimal'],
    ['analyzers disabled', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release -p:RunAnalyzers=false'],
    ['wrong artifact revision', 'env:\n  PAYPLANNER_CI_ARTIFACT_ROOT:', 'env:\n  GITHUB_SHA: main\n  PAYPLANNER_CI_ARTIFACT_ROOT:'],
    ['missing artifact tolerated', 'put --kind frontend --source frontend.tar.gz --retention-days 3', 'put --kind frontend --source frontend.tar.gz --retention-days 3 --allow-empty'],
    ['cross-run download', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend.outputs.backend-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend.outputs.backend-artifact-id }}\n          GITHUB_RUN_ID: 123'],
    ['conditional artifact upload', '      - name: Upload frontend build artifact\n', '      - name: Upload frontend build artifact\n        if: always()\n'],
    ['conditional readiness', '      - name: Check production deploy readiness\n', '      - name: Check production deploy readiness\n        if: false\n'],
    ['custom shell', '        run: npm run lint', '        shell: bash {0}\n        run: npm run lint'],
  ])('rejects %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('rejects moving a real legal gate behind its uploaded artifact', () => {
    const begin = workflow.indexOf('      - name: Test legal feature flags readiness\n');
    const end = workflow.indexOf('      - name: Build FE\n', begin);
    expect(begin).toBeGreaterThan(0);
    expect(end).toBeGreaterThan(begin);
    const legal = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('  backend-format:\n', legal + '  backend-format:\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/precede|order/);
  });

  it('rejects packaging a test runtime before discovery completes', () => {
    const begin = workflow.indexOf('      - name: Pack compiled test runtime\n');
    const end = workflow.indexOf('      - name: Upload compiled test runtime\n', begin);
    const pack = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('      - name: Discover complete backend test plan\n', pack + '      - name: Discover complete backend test plan\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/order/);
  });

  it.each([
    ['missing shard', '  backend-test-3:\n', '  backend-test-4:\n'],
    ['duplicated shard', '  backend-test-3:\n', '  backend-test-2:\n'],
    ['cancel remaining shards', '      queue: max', '      queue: single'],
    ['extra matrix dimension', '  backend-test-0:\n', '  backend-test-0:\n    strategy:\n      matrix:\n        variant: [a, b]\n'],
    ['matrix exclusion', '  backend-test-0:\n', '  backend-test-0:\n    strategy:\n      matrix:\n        exclude: []\n'],
    ['conditional aggregate', '  backend:\n', '  backend:\n    if: always()\n'],
    ['aggregate missing browser gate', aggregateNeeds, `needs: [backend-build, ${shardJobs.join(', ')}]`],
    ['aggregate missing shard gate', aggregateNeeds, 'needs: [backend-build, backend-browser]'],
    ['test missing build dependency', 'needs: [backend-build, backend-format, frontend]', 'needs: [backend-format, frontend]'],
    ['manifest producer output missing', 'manifest-sha: ${{ steps.plan.outputs.manifest-sha }}', 'manifest-sha: fake'],
    ['manifest hash recomputed downstream', 'EXPECTED_MANIFEST_SHA: ${{ needs.backend-build.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: fake'],
    ['untrusted manifest not checked', '--manifest-sha "$EXPECTED_MANIFEST_SHA" --shard', '--shard'],
    ['union check removed', ' verify --manifest test-plan.json', ' help --manifest test-plan.json'],
    ['union wrong root', '--results-root test-results', '--results-root other-results'],
    ['runtime missing manifest', 'tar -czf test-runtime.tar.gz test-plan.json ', 'tar -czf test-runtime.tar.gz '],
    ['runtime missing probe dependencies', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll tools/'],
    ['wrong shard result directory', '--destination test-results --merge-disjoint', '--destination test-results/shard-0 --merge-disjoint'],
    ['precreated shard directory', 'mkdir -p test-results\n', 'mkdir -p test-results/shard-0\n'],
    ['wrong shard artifact', 'get --kind qualified-shard --producer-job backend-test-3 --shard 3', 'get --kind qualified-shard --producer-job backend-test-2 --shard 3'],
    ['wrong attempt diagnostics', 'ARTIFACT_SHARD: "0"', 'ARTIFACT_SHARD: "0"\n          GITHUB_RUN_ATTEMPT: "1"'],
    ['results only on success', '        if: always()\n', ''],
    ['test SDK drift', 'dotnet-version: ${{ needs.backend-build.outputs.sdk-version }}', 'dotnet-version: "8.0.x"'],
    ['restored native exit replaced', '--exit-code "$native_exit"', '--exit-code 0'],
    ['restored verifier omitted', ' verify-restored --manifest', ' help --manifest'],
    ['restored exit overwritten', '          cat test-results/restored-browser/test.log', '          native_exit=0\n          cat test-results/restored-browser/test.log'],
    ['restored failure swallowed', ' || native_exit=$?', ' || true'],
    ['empty restored selection allowed', '<TreatNoTestsAsError>true</TreatNoTestsAsError>', '<TreatNoTestsAsError>false</TreatNoTestsAsError>'],
    ['restored method omitted', '|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource', ''],
    ['dedicated PostgreSQL replaced', 'secrets.PAYPLANNER_XSENUS_TEST_POSTGRES', 'secrets.PRODUCTION_POSTGRES'],
    ['tool analyzers disabled', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release -p:RunAnalyzers=false'],
  ])('rejects incomplete or unbound coverage: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('rejects recompiling a downloaded test runtime', () => {
    const begin = workflow.indexOf('  backend-test-0:\n');
    const changed = workflow.slice(0, begin) + workflow.slice(begin).replace('    steps:\n', '    steps:\n      - name: Rebuild\n        run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/rebuilt/);
  });

  it.each([
    ['qualified output on failure', '        if: success()\n', '        if: always()\n'],
    ['qualified output without success guard', '        if: success()\n', ''],
    ['qualified receipt overwritten', 'put --kind qualified-shard --source test-results/ --retention-days 14', 'put --kind qualified-shard --source test-results/ --retention-days 14 --overwrite'],
    ['qualified receipt missing manifest binding', 'EXPECTED_MANIFEST_SHA: ${{ needs.backend-build.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: stale'],
    ['qualified receipt missing SDK binding', 'EXPECTED_SDK_VERSION: ${{ needs.backend-build.outputs.sdk-version }}', 'EXPECTED_SDK_VERSION: stale'],
    ['qualified receipt tied to consumer attempt', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-test.outputs.shard_0_id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-test.outputs.shard_0_id }}-${{ github.run_attempt }}'],
    ['diagnostics overwritten', 'put --kind shard-diagnostics --source test-results/ --retention-days 14', 'put --kind shard-diagnostics --source test-results/ --retention-days 14 --overwrite'],
    ['diagnostics history shortened', 'put --kind shard-diagnostics --source test-results/ --retention-days 14', 'put --kind shard-diagnostics --source test-results/ --retention-days 1'],
    ['producer attempt forged', 'ARTIFACT_SHARD: "0"', 'ARTIFACT_SHARD: "0"\n          GITHUB_RUN_ATTEMPT: "1"'],
    ['archive overwritten', 'put --kind frontend --source frontend.tar.gz --retention-days 3', 'put --kind frontend --source frontend.tar.gz --retention-days 3 --overwrite'],
    ['runtime ID not uploader output', 'runtime-artifact-id: ${{ steps.runtime-upload.outputs.artifact-id }}', 'runtime-artifact-id: ${{ github.run_id }}'],
    ['frontend ID not uploader output', 'frontend-artifact-id: ${{ steps.frontend-upload.outputs.artifact-id }}', 'frontend-artifact-id: 123'],
    ['backend ID not uploader output', 'backend-artifact-id: ${{ steps.backend-upload.outputs.artifact-id }}', 'backend-artifact-id: 123'],
    ['aggregate substitutes backend ID', 'backend-artifact-id: ${{ needs.backend-build.outputs.backend-artifact-id }}', 'backend-artifact-id: 123'],
    ['runtime falls back to name', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_ARTIFACT_ID: test-runtime-latest'],
    ['runtime allows multiple IDs', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }},123'],
    ['runtime allows empty ID', '[[ "$RUNTIME_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['runtime compares wrong SDK', 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"', 'test "$actual_sdk" = "9.0.100"'],
    ['SDK check only echoes expected label', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$EXPECTED_SDK_VERSION"'],
    ['SDK check hides command failure', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$(dotnet --version || true)"'],
    ['deploy allows empty backend ID', '[[ "$BACKEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['deploy allows empty frontend ID', '[[ "$FRONTEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['deploy accepts same archive twice', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]', 'true'],
    ['deploy ID bypasses backend qualification', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend.outputs.backend-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.backend-artifact-id }}'],
    ['download arbitrary cross-run ID', 'get --kind test-runtime --producer-job backend-build --destination test-artifacts', 'get --kind test-runtime --producer-job backend-build --destination test-artifacts --run-id 123'],
    ['needs anchor omitted', '          ARTIFACT_NEEDS_JSON: ${{ toJSON(needs) }}\n', ''],
    ['diagnostic ID not successful preceding step', 'EXPECTED_DIAGNOSTIC_ARTIFACT_ID: ${{ steps.shard-diagnostics.outputs.artifact-id }}', 'EXPECTED_DIAGNOSTIC_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}'],
    ['diagnostic used as qualification', 'qualified-artifact-id: ${{ steps.qualified-upload.outputs.shard_0_id }}', 'qualified-artifact-id: ${{ steps.shard-diagnostics.outputs.artifact-id }}'],
    ['shared shard output key', 'qualified-artifact-id: ${{ steps.qualified-upload.outputs.shard_1_id }}', 'qualified-artifact-id: ${{ steps.qualified-upload.outputs.shard_0_id }}'],
    ['wrong shard metadata', 'ARTIFACT_SHARD: "0"', 'ARTIFACT_SHARD: "1"'],
    ['wrong runtime metadata', 'EXPECTED_RUNTIME_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_RUNTIME_ARTIFACT_ID: other-runtime'],
    ['archive retention shortened', 'put --kind frontend --source frontend.tar.gz --retention-days 3', 'put --kind frontend --source frontend.tar.gz --retention-days 1'],
    ['store variable fallback', "PAYPLANNER_CI_ARTIFACT_ROOT: 'D:\\PayPlannerCIArtifacts\\main-store'", "PAYPLANNER_CI_ARTIFACT_ROOT: ${{ vars.PAYPLANNER_CI_ARTIFACT_ROOT || 'temp' }}"],
    ['transport tests omitted', 'node --test scripts/xsenus-artifact-store.node-tests.mjs', 'echo transport-tested'],
    ['execution report tests omitted', 'node --test scripts/report-backend-execution.node-tests.mjs', 'echo report-tested'],
    ['execution report omitted', 'node scripts/report-backend-execution.mjs 0', 'echo report-complete'],
    ['store preflight omitted', 'run: node scripts/xsenus-artifact-store.mjs check', 'run: echo storage-ready'],
    ['diagnostic role substituted', 'put --kind qualified-shard', 'put --kind shard-diagnostics'],
    ['unsafe overlapping merge', 'get --kind frontend --producer-job frontend --destination deploy-artifacts --merge-disjoint', 'get --kind frontend --producer-job frontend --destination deploy-artifacts --merge'],
  ])('rejects unsafe rerun binding: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('requires completed shard diagnostics before publishing a qualified alias', () => {
    const begin = workflow.indexOf('      - name: Publish qualified shard alias\n');
    const end = workflow.indexOf('  backend-test-1:\n', begin);
    const alias = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('      - name: Preserve exact shard results\n', alias + '      - name: Preserve exact shard results\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/evidence|alias/);
  });

  it('rejects an extra fallback download even when all exact aliases remain', () => {
    const changed = workflow.replace('      - name: Verify complete backend case union\n', '      - name: Fallback\n        uses: actions/download-artifact@v7\n        with:\n          pattern: backend-shard-*\n          path: test-results\n\n      - name: Verify complete backend case union\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/fallback/);
  });

  it.each([...shardJobs, 'backend-browser'])('rejects a runtime fallback in %s', id => {
    const begin = workflow.indexOf(`  ${id}:\n`);
    const changed = workflow.slice(0, begin) + workflow.slice(begin).replace('    steps:\n', '    steps:\n      - name: Fallback runtime\n        uses: actions/download-artifact@v4\n        with:\n          pattern: test-runtime-*\n          path: test-artifacts\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/fallback/);
  });

  it.each(allJobs.filter(id => id !== 'backend-format'))('rejects extra local transport in %s', id => {
    const changed = replaceInJob(id, job => job +
      '\n      - name: Fallback local results\n        run: node scripts/xsenus-artifact-store.mjs get --kind qualified-shard --producer-job backend-test-0 --shard 0 --destination fallback --latest\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/transport|storage/);
  });

  it('requires storage validation before any producer or consumer operation', () => {
    const step = '      - name: Verify local artifact store\n        run: node scripts/xsenus-artifact-store.mjs check\n';
    const changed = replaceInJob('frontend', job => job.replace(step, '') + step);
    expect(() => validateDeployWorkflow(changed)).toThrow(/storage must precede/);
  });

  it.each(['manifest-sha', 'sdk-version'])('requires exact aggregate forwarding of producer %s', output => {
    const changed = replaceInJob('backend', job => job.replace(
      `${output}: \${{ needs.backend-build.outputs.${output} }}`, `${output}: unqualified`,
    ));
    expect(() => validateDeployWorkflow(changed)).toThrow(/forward/);
  });

  it.each([
    ['DOTNET_PROCESSOR_COUNT', '1', '4'], ['DOTNET_gcServer', '0', '1'], ['DOTNET_GCConserveMemory', '9', '0'],
  ])('preserves the existing %s resource limit', (name, before, after) => {
    const changed = replaceOnce(`${name}: "${before}"`, `${name}: "${after}"`);
    expect(() => validateDeployWorkflow(changed)).toThrow(/resource/);
  });

  it('keeps four distinct trusted producer outputs across shard-only, browser-only and aggregate-only retries', () => {
    const jobs = parseWorkflow(workflow).jobs;
    for (let index = 0; index < 4; index += 1) {
      const id = shardJobs[index];
      expect(jobs[id].needs).toEqual(['backend-build', 'backend-format', 'frontend']);
      expect(jobs[id].if).toBeUndefined();
      expect(jobs[id].strategy).toBeUndefined();
      expect(jobs[id].concurrency).toEqual({ group: 'payplanner-xsenus-backend-tests', queue: 'max', 'cancel-in-progress': false });
      expect(jobs[id].outputs).toEqual({ 'qualified-artifact-id': `\${{ steps.qualified-upload.outputs.shard_${index}_id }}` });
      const download = jobs.backend.steps.find((step: { run?: string }) => step.run?.includes(`get --kind qualified-shard --producer-job ${id} --shard ${index} `));
      expect(download.env.EXPECTED_ARTIFACT_ID).toBe(`\${{ needs.backend-test.outputs.shard_${index}_id }}`);
      expect(jobs['backend-test'].outputs[`shard_${index}_id`]).toBe(`\${{ needs.${id}.outputs.qualified-artifact-id }}`);
      expect(download.env.ARTIFACT_NEEDS_JSON).toBe('${{ toJSON(needs) }}');
      expect(download.run).not.toContain('run_attempt');
      expect(download.env.EXPECTED_ARTIFACT_ID).not.toContain('run_attempt');
    }
  });

  it('uses immutable typed producer IDs and exact runtime bindings for rerun consumers', () => {
    const jobs = parseWorkflow(workflow).jobs;
    for (const [job, id, kind] of [['frontend', 'frontend-upload', 'frontend'], ['backend-build', 'runtime-upload', 'test-runtime'], ['backend-build', 'backend-upload', 'backend']]) {
      const upload = jobs[job].steps.find((step: { id?: string }) => step.id === id);
      expect(upload.run).toContain(`put --kind ${kind} --source `);
      expect(upload.run).toContain('--retention-days 3');
      expect(upload.run).not.toMatch(/overwrite|latest|run-attempt|run-id/);
      expect(upload.with).toBeUndefined();
    }
    for (const id of [...shardJobs, 'backend-browser', 'backend']) {
      const download = jobs[id].steps.find((step: { name?: string }) => step.name === 'Download compiled test runtime');
      expect(download.run).toBe('node scripts/xsenus-artifact-store.mjs get --kind test-runtime --producer-job backend-build --destination test-artifacts');
      expect(download.env).toEqual({
        EXPECTED_ARTIFACT_ID: '${{ needs.backend-build.outputs.runtime-artifact-id }}', ARTIFACT_NEEDS_JSON: '${{ toJSON(needs) }}',
        EXPECTED_MANIFEST_SHA: '${{ needs.backend-build.outputs.manifest-sha }}', EXPECTED_SDK_VERSION: '${{ needs.backend-build.outputs.sdk-version }}',
      });
    }
  });

  it.each(shardJobs)('rejects divergent gates or resource settings in %s', id => {
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      '      DOTNET_GCHeapHardLimit: "C0000000"\n', '      DOTNET_GCHeapHardLimit: "C0000000"\n      DOTNET_PROCESSOR_COUNT: "4"\n',
    )))).toThrow(/resource/);
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      'needs: [backend-build, backend-format, frontend]', 'needs: [backend-build, backend-format, frontend, backend-test-0]',
    )))).toThrow(/dependencies/);
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace('queue: max\n', 'queue: max\n      extra: true\n')))).toThrow(/queue/);
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      'group: payplanner-xsenus-backend-tests', `group: ${id}`,
    )))).toThrow(/queue/);
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      '    steps:', '    if: always()\n    steps:',
    )))).toThrow(/conditional/);
    expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace(
      '    steps:', '    steps:\n      - name: Extra shard-only gate\n        run: echo extra\n',
    )))).toThrow(/identical/);
  });

});

describe('bounded workflow YAML reader', () => {
  it('retains scripts containing colon, comments and YAML-looking text as opaque blocks', () => {
    expect(parseWorkflow('steps:\n  - run: |\n      echo "jobs: fake"\n      # keep script comment\n      if true; then echo ok; fi\n    env:\n      SAFE: "false"\n').steps[0]).toEqual({
      run: 'echo "jobs: fake"\n# keep script comment\nif true; then echo ok; fi', env: { SAFE: 'false' },
    });
  });

  it.each([
    'jobs: {}\njobs: {}\n',
    'jobs: &shared\n',
    'jobs: *shared\n',
    'jobs: !custom foo\n',
    'jobs: {frontend: {}}\n',
    'jobs:\n\tfrontend: {}\n',
    'jobs: [frontend\n',
  ])('rejects ambiguous or unsupported YAML %j', source => {
    expect(() => parseWorkflow(source)).toThrow();
  });
});
