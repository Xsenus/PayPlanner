// @vitest-environment node

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseWorkflow, validateDeployWorkflow } from './validate-deploy-workflow.mjs';

const workflow = readFileSync(resolve('.github/workflows/deploy.yml'), 'utf8').replaceAll('\r\n', '\n');

function replaceOnce(before: string, after: string) {
  expect(workflow).toContain(before);
  return workflow.replace(before, after);
}

function replaceInJob(id: string, change: (job: string) => string) {
  const header = `  ${id}:\n`;
  const begin = workflow.indexOf(header);
  expect(begin).toBeGreaterThan(0);
  const following = begin + header.length;
  const next = workflow.slice(following).search(/^ {2}[a-z][a-z-]*:\n/m);
  const end = next < 0 ? workflow.length : following + next;
  return workflow.slice(0, begin) + change(workflow.slice(begin, end)) + workflow.slice(end);
}

describe('parallel deploy workflow release contract', () => {
  it.each(['frontend', 'backend-format', 'backend-build', 'backend-test', 'backend-browser', 'backend', 'deploy'])(
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

  it.each(['backend-format', 'backend-build', 'backend-test', 'backend-browser', 'backend'])(
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

  it.each([
    ['format overlaps frontend', 'needs: [frontend]', 'needs: []'],
    ['build overlaps format', 'needs: [backend-format]', 'needs: []'],
    ['unbounded Node heap', '--max-old-space-size=1536', '--max-old-space-size=3072'],
    ['different coordinator CPU budget', 'DOTNET_PROCESSOR_COUNT: "1"', 'DOTNET_PROCESSOR_COUNT: "2"'],
    ['compiler server enabled', 'UseSharedCompilation: "false"', 'UseSharedCompilation: "true"'],
    ['unreviewed compiler response file', 'scripts/compiler-serial.rsp', 'scripts/other.rsp'],
    ['format managed cap omitted', '      DOTNET_GCHeapHardLimit: "180000000"\n', ''],
    ['parallel unit invocation', 'run: npm run test:unit:serial', 'run: npm run test:ci'],
    ['parallel restore', '--force --disable-parallel', '--force'],
    ['build server reuse', '-m:1 -nr:false -p:UseSharedCompilation=false', '-m:1 -p:UseSharedCompilation=false'],
    ["missing full Tests format", "dotnet format backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --verify-no-changes --no-restore --verbosity diagnostic", "echo tests-format-omitted"],
    ["missing Migrations analyzer category", "dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic", "dotnet format whitespace backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic"],
    ["partial Migrations diagnostic filter", "dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic", "dotnet format analyzers backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --verify-no-changes --no-restore --verbosity diagnostic --diagnostics IDE0005"],
    ["narrow full API include", "dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal", "dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal --include backend/Program.cs"],
    ["local workspace skip property", "  UseSharedCompilation: \"false\"", "  UseSharedCompilation: \"false\"\n  PayPlannerSkipMigrationsBuild: \"true\""],
  ])('rejects a widened or overlapping resource profile: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('rejects a new database server and parallel memory-heavy shards', () => {
    expect(() => validateDeployWorkflow(replaceInJob('backend-test', job => job.replace(
      '    steps:', '    services:\n      postgres:\n        image: postgres:17-alpine\n    steps:',
    )))).toThrow(/new servers/);
    expect(() => validateDeployWorkflow(replaceOnce('max-parallel: 1', 'max-parallel: 4'))).toThrow();
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

  it.each(['backend-test', 'backend-browser'])('keeps expensive %s behind all preflight gates', id => {
    const originalNeeds = id === 'backend-browser'
      ? 'needs: [backend-build, backend-format, frontend, backend-test]'
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
      'needs: [backend-build, backend-format, frontend, backend-test]',
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
    ['remove manual release', '  workflow_dispatch: {}\n', ''],
    ['remove main trigger', 'branches: ["main"]', 'branches: ["other"]'],
  ])('rejects trigger changes that %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it.each(['frontend', 'backend-format', 'backend-build', 'backend-test', 'backend-browser', 'backend', 'deploy'])(
    'bounds runner time for %s', id => {
      for (const timeout of ['0', '360', '${{ vars.UNBOUNDED_TIMEOUT }}']) {
        const changed = replaceInJob(id, job => job.replace(/timeout-minutes: \d+/, `timeout-minutes: ${timeout}`));
        expect(() => validateDeployWorkflow(changed)).toThrow(/time budget/);
      }
    },
  );

  it.each([
    ['missing dependency', 'needs: [frontend, backend-format, backend]', 'needs: [frontend, backend]'],
    ['duplicate dependency', 'needs: [frontend, backend-format, backend]', 'needs: [frontend, backend, backend]'],
    ['deploy always', '  deploy:\n', '  deploy:\n    if: always()\n'],
    ['skipped frontend', '  frontend:\n', '  frontend:\n    if: false\n'],
    ['job error bypass', '  backend:\n', '  backend:\n    continue-on-error: true\n'],
    ['step error bypass', '        run: npm run lint', '        continue-on-error: true\n        run: npm run lint'],
    ['conditional legal gate', '        run: npm run test:legal-flags:strict', '        if: false\n        run: npm run test:legal-flags:strict'],
    ['commented legal gate', '        run: npm run test:legal-flags:strict', '        run: echo npm run test:legal-flags:strict'],
    ['removed validator', '        run: node scripts/validate-deploy-workflow.mjs', '        run: echo validator'],
    ['failure swallowed', '        run: npm run lint', '        run: |\n          npm run lint\n          true || true'],
    ['conditional shell wrapper', '        run: npm run lint', '        run: |\n          if false; then\n            npm run lint\n          fi'],
    ['shard cases filtered', '--shard ${{ matrix.shard }} --results', '--filter Tiny --shard ${{ matrix.shard }} --results'],
    ['pipe failure lost', '          set -euo pipefail\n          mkdir -p test-results\n', '          set -eu\n          mkdir -p test-results\n'],
    ['restored browser disabled', 'PAYPLANNER_TEST_RESTORED_BROWSER: "true"', 'PAYPLANNER_TEST_RESTORED_BROWSER: "false"'],
    ['mail enabled', 'Email__Enabled: "false"', 'Email__Enabled: "true"'],
    ['non-strict restore', '--force --disable-parallel -p:PayPlannerReleaseAudit=true', '--force'],
    ['format disabled', '--verify-no-changes --no-restore --verbosity minimal', '--no-restore --verbosity minimal'],
    ['analyzers disabled', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:RunAnalyzers=false'],
    ['wrong artifact revision context', 'ARTIFACT_NEEDS_JSON: ${{ toJSON(needs) }}', 'ARTIFACT_NEEDS_JSON: ${{ toJSON(needs) }}\n          GITHUB_SHA: main'],
    ['missing artifact tolerated', '--kind frontend --source frontend.tar.gz --retention-days 3', '--kind frontend --source frontend.tar.gz --retention-days 3 || true'],
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
    ['missing shard', 'shard: [0, 1, 2, 3]', 'shard: [0, 1, 2]'],
    ['duplicated shard', 'shard: [0, 1, 2, 3]', 'shard: [0, 1, 2, 2]'],
    ['cancel remaining shards', 'fail-fast: false', 'fail-fast: true'],
    ['extra matrix dimension', 'shard: [0, 1, 2, 3]', 'shard: [0, 1, 2, 3]\n        variant: [a, b]'],
    ['matrix exclusion', 'shard: [0, 1, 2, 3]', 'shard: [0, 1, 2, 3]\n        exclude: []'],
    ['conditional aggregate', '  backend:\n', '  backend:\n    if: always()\n'],
    ['aggregate missing browser gate', 'needs: [backend-build, backend-test, backend-browser]', 'needs: [backend-build, backend-test]'],
    ['aggregate missing shard gate', 'needs: [backend-build, backend-test, backend-browser]', 'needs: [backend-build, backend-browser]'],
    ['test missing build dependency', 'needs: [backend-build, backend-format, frontend]', 'needs: [backend-format, frontend]'],
    ['manifest producer output missing', 'manifest-sha: ${{ steps.plan.outputs.manifest-sha }}', 'manifest-sha: fake'],
    ['manifest hash recomputed downstream', 'EXPECTED_MANIFEST_SHA: ${{ needs.backend-build.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: fake'],
    ['untrusted manifest not checked', '--manifest-sha "$EXPECTED_MANIFEST_SHA" --shard', '--shard'],
    ['union check removed', ' verify --manifest test-plan.json', ' help --manifest test-plan.json'],
    ['union wrong root', '--results-root test-results', '--results-root other-results'],
    ['runtime missing manifest', 'tar -czf test-runtime.tar.gz test-plan.json ', 'tar -czf test-runtime.tar.gz '],
    ['runtime missing probe dependencies', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll tools/'],
    ['wrong shard result directory', '--producer-job backend-test-0 --destination test-results --shard 0', '--producer-job backend-test-0 --destination test-results/shard-0 --shard 0'],
    ['precreated shard directory', 'mkdir -p test-results\n', 'mkdir -p test-results/shard-${{ matrix.shard }}\n'],
    ['wrong shard artifact', '--producer-job backend-test-3 --destination test-results --shard 3', '--producer-job backend-test-2 --destination test-results --shard 3'],
    ['wrong attempt diagnostics', '--kind shard-diagnostics --source test-results/ --retention-days 14', '--kind shard-diagnostics --source test-results/ --retention-days 14 --attempt 1'],
    ['results only on success', '        if: always()\n        env:', '        env:'],
    ['test SDK drift', 'dotnet-version: ${{ needs.backend-build.outputs.sdk-version }}', 'dotnet-version: "8.0.x"'],
    ['restored native exit replaced', '--exit-code "$native_exit"', '--exit-code 0'],
    ['restored verifier omitted', ' verify-restored --manifest', ' help --manifest'],
    ['restored exit overwritten', '          cat test-results/restored-browser/test.log', '          native_exit=0\n          cat test-results/restored-browser/test.log'],
    ['restored failure swallowed', ' || native_exit=$?', ' || true'],
    ['empty restored selection allowed', '<TreatNoTestsAsError>true</TreatNoTestsAsError>', '<TreatNoTestsAsError>false</TreatNoTestsAsError>'],
    ['restored method omitted', '|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource', ''],
    ['dedicated PostgreSQL replaced', 'secrets.PAYPLANNER_XSENUS_TEST_POSTGRES', 'secrets.PRODUCTION_POSTGRES'],
    ['tool analyzers disabled', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:RunAnalyzers=false'],
  ])('rejects incomplete or unbound coverage: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('rejects recompiling a downloaded test runtime', () => {
    const begin = workflow.indexOf('  backend-test:\n');
    const changed = workflow.slice(0, begin) + workflow.slice(begin).replace('    steps:\n', '    steps:\n      - name: Rebuild\n        run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/rebuilt/);
  });

  it.each([
    ['alias on failure', '        if: success()\n', '        if: always()\n'],
    ['alias without explicit success guard', '        if: success()\n', ''],
    ['qualified object must remain immutable', '--kind qualified-shard --source test-results/ --retention-days 14', '--kind qualified-shard --source test-results/ --retention-days 14 --overwrite'],
    ['alias missing manifest binding', 'EXPECTED_MANIFEST_SHA: ${{ needs.backend-build.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: unbound'],
    ['alias missing SDK binding', 'EXPECTED_SDK_VERSION: ${{ needs.backend-build.outputs.sdk-version }}', 'EXPECTED_SDK_VERSION: unbound'],
    ['qualified ID replaced by current attempt label', 'shard_0_id: ${{ steps.qualified-upload.outputs.shard_0_id }}', 'shard_0_id: ${{ github.run_attempt }}'],
    ['diagnostics overwritten', '--kind shard-diagnostics --source test-results/ --retention-days 14', '--kind shard-diagnostics --source test-results/ --retention-days 14 --overwrite'],
    ['diagnostics retention class shortened', '--retention-days 14', '--retention-days 1'],
    ['archive attempt overridden', 'EXPECTED_MANIFEST_SHA: ${{ steps.plan.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: ${{ steps.plan.outputs.manifest-sha }}\n          GITHUB_RUN_ATTEMPT: 1'],
    ['archive overwritten', '--kind frontend --source frontend.tar.gz --retention-days 3', '--kind frontend --source frontend.tar.gz --retention-days 3 --overwrite'],
    ['runtime ID not uploader output', 'runtime-artifact-id: ${{ steps.runtime-upload.outputs.artifact-id }}', 'runtime-artifact-id: ${{ github.run_id }}'],
    ['frontend ID not uploader output', 'frontend-artifact-id: ${{ steps.frontend-upload.outputs.artifact-id }}', 'frontend-artifact-id: 123'],
    ['backend ID not uploader output', 'backend-artifact-id: ${{ steps.backend-upload.outputs.artifact-id }}', 'backend-artifact-id: 123'],
    ['aggregate substitutes backend ID', 'backend-artifact-id: ${{ needs.backend-build.outputs.backend-artifact-id }}', 'backend-artifact-id: 123'],
    ['runtime falls back to name', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_ARTIFACT_ID: test-runtime-${{ github.sha }}'],
    ['runtime allows multiple IDs', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }},123'],
    ['runtime allows empty ID', '[[ "$RUNTIME_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['runtime compares wrong SDK', 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"', 'test "$actual_sdk" = "9.0.100"'],
    ['SDK check only echoes expected label', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$EXPECTED_SDK_VERSION"'],
    ['SDK check hides command failure', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$(dotnet --version || true)"'],
    ['deploy allows empty backend ID', '[[ "$BACKEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['deploy allows empty frontend ID', '[[ "$FRONTEND_ARTIFACT_ID" =~ ^xsenus-local-v1:[a-f0-9]{64}$ ]]', 'true'],
    ['deploy accepts same archive twice', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]', 'true'],
    ['deploy ID bypasses backend qualification', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend.outputs.backend-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.backend-artifact-id }}'],
    ['download arbitrary cross-run ID', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'EXPECTED_ARTIFACT_ID: ${{ needs.backend-build.outputs.runtime-artifact-id }}\n          GITHUB_TOKEN: ${{ github.token }}'],
  ])('rejects unsafe rerun binding: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('requires completed shard diagnostics before publishing a qualified alias', () => {
    const begin = workflow.indexOf('      - name: Publish qualified shard alias\n');
    const end = workflow.indexOf('  backend-browser:\n', begin);
    const alias = workflow.slice(begin, end);
    const moved = (workflow.slice(0, begin) + workflow.slice(end)).replace('      - name: Preserve exact shard results\n', alias + '      - name: Preserve exact shard results\n');
    expect(() => validateDeployWorkflow(moved)).toThrow(/evidence|alias/);
  });

  it('rejects an extra fallback download even when all exact aliases remain', () => {
    const changed = workflow.replace('      - name: Verify complete backend case union\n', '      - name: Fallback\n        uses: actions/download-artifact@v7\n        with:\n          pattern: backend-shard-*\n          path: test-results\n\n      - name: Verify complete backend case union\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/fallback/);
  });

  it.each(['backend-test', 'backend-browser'])('rejects a runtime fallback in %s', id => {
    const begin = workflow.indexOf(`  ${id}:\n`);
    const changed = workflow.slice(0, begin) + workflow.slice(begin).replace('    steps:\n', '    steps:\n      - name: Fallback runtime\n        uses: actions/download-artifact@v4\n        with:\n          pattern: test-runtime-*\n          path: test-artifacts\n');
    expect(() => validateDeployWorkflow(changed)).toThrow(/fallback/);
  });

  it('forwards each qualified shard output independently for shard-only, browser-only and aggregate-only retries', () => {
    const jobs = parseWorkflow(workflow).jobs;
    const outputs = jobs['backend-test'].outputs;
    const upload = jobs['backend-test'].steps.find(step => step.name === 'Publish qualified shard alias');
    const downloads = jobs.backend.steps.filter(step => step.run?.includes(' get --kind qualified-shard '));
    expect(Object.keys(outputs).sort()).toEqual(['shard_0_id', 'shard_1_id', 'shard_2_id', 'shard_3_id']);
    expect(upload.if).toBe('success()');
    expect(upload.env.EXPECTED_DIAGNOSTIC_ARTIFACT_ID).toBe('${{ steps.shard-diagnostics.outputs.artifact-id }}');
    expect(downloads.length).toBe(4);
    for (let index = 0; index < 4; index += 1) {
      expect(outputs[`shard_${index}_id`]).toBe(`\${{ steps.qualified-upload.outputs.shard_${index}_id }}`);
      expect(downloads[index].env.EXPECTED_ARTIFACT_ID).toBe(`\${{ needs.backend-test.outputs.shard_${index}_id }}`);
      expect(downloads[index].env.EXPECTED_MANIFEST_SHA).toBe('${{ needs.backend-build.outputs.manifest-sha }}');
      expect(downloads[index].env.EXPECTED_SDK_VERSION).toBe('${{ needs.backend-build.outputs.sdk-version }}');
      expect(downloads[index].env.EXPECTED_RUNTIME_ARTIFACT_ID).toBe('${{ needs.backend-build.outputs.runtime-artifact-id }}');
      expect(downloads[index].run).toContain(`--producer-job backend-test-${index} --destination test-results --shard ${index} --merge-disjoint`);
    }
    expect(jobs.backend.outputs['manifest-sha']).toBe('${{ needs.backend-build.outputs.manifest-sha }}');
    expect(jobs.backend.outputs['sdk-version']).toBe('${{ needs.backend-build.outputs.sdk-version }}');
  });

  it('uses immutable content-addressed archive receipts and exact producer IDs for rerun consumers', () => {
    const jobs = parseWorkflow(workflow).jobs;
    for (const [job, id, kind, source] of [['frontend', 'frontend-upload', 'frontend', 'frontend.tar.gz'],
      ['backend-build', 'runtime-upload', 'test-runtime', 'test-runtime.tar.gz'], ['backend-build', 'backend-upload', 'backend', 'backend.tar.gz']]) {
      const upload = jobs[job].steps.find(step => step.id === id);
      expect(upload.run).toBe(`set -euo pipefail\nnode scripts/xsenus-artifact-store.mjs put --kind ${kind} --source ${source} --retention-days 3`);
      expect(upload.with).toBe(undefined);
      expect(upload.if).toBe(undefined);
      expect(upload.env.ARTIFACT_NEEDS_JSON).toBe('${{ toJSON(needs) }}');
    }
    for (const id of ['backend-test', 'backend-browser', 'backend']) {
      const download = jobs[id].steps.find(step => step.name === 'Download compiled test runtime');
      expect(download.run).toBe('set -euo pipefail\nnode scripts/xsenus-artifact-store.mjs get --kind test-runtime --producer-job backend-build --destination test-artifacts');
      expect(download.env.EXPECTED_ARTIFACT_ID).toBe('${{ needs.backend-build.outputs.runtime-artifact-id }}');
      expect(download.env.EXPECTED_MANIFEST_SHA).toBe('${{ needs.backend-build.outputs.manifest-sha }}');
      expect(download.env.EXPECTED_SDK_VERSION).toBe('${{ needs.backend-build.outputs.sdk-version }}');
    }
  });
  it.each(['frontend', 'backend-format', 'backend-build', 'backend-test', 'backend-browser', 'backend', 'deploy'])(
    'requires ordinary private-store preflight before any gates in %s', id => {
      expect(() => validateDeployWorkflow(replaceInJob(id, job => job.replace('node scripts/xsenus-artifact-store.mjs check', 'echo checked')))).toThrow(/store|gate/);
    },
  );

  it.each([
    ['production root is not probe root', "'D:\\PayPlannerCIArtifacts\\main-store'", "'D:\\PayPlannerCIArtifacts\\store'"],
    ['qualified shard output cannot collide', 'shard_3_id: ${{ steps.qualified-upload.outputs.shard_3_id }}', 'shard_3_id: ${{ steps.qualified-upload.outputs.shard_2_id }}'],
    ['native matrix identity is not forged', 'ARTIFACT_SHARD: ${{ matrix.shard }}', 'ARTIFACT_SHARD: ${{ matrix.shard }}\n          GITHUB_JOB: backend-test-0'],
    ['diagnostic receipt is required', 'EXPECTED_DIAGNOSTIC_ARTIFACT_ID: ${{ steps.shard-diagnostics.outputs.artifact-id }}', 'EXPECTED_DIAGNOSTIC_ARTIFACT_ID: unverified'],
    ['deploy manifest is forwarded', 'manifest-sha: ${{ needs.backend-build.outputs.manifest-sha }}', 'manifest-sha: fake'],
    ['deploy SDK is forwarded', 'sdk-version: ${{ needs.backend-build.outputs.sdk-version }}', 'sdk-version: fake'],
    ['digest canonicalization remains exact', '.ManifestSha256.toLowerCase()', '.ManifestSha256.slice(0, 8)'],
    ['canonical producer digest output is required', '.ManifestSha256.toLowerCase()', '.ManifestSha256'],
    ['no overlapping result overwrite', '--shard 3 --merge-disjoint', '--shard 3 --overwrite'],
    ['all four shard outputs are consumed', 'needs.backend-test.outputs.shard_3_id', 'needs.backend-test.outputs.shard_2_id'],
    ['frontend package merge is disjoint', '--producer-job frontend --destination deploy-artifacts --merge-disjoint', '--producer-job frontend --destination deploy-artifacts'],
  ])('rejects local artifact protocol drift: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
  });

  it('resolves restored VSTest from the transported discovery summary through the existing SDK input library', () => {
    const jobs = parseWorkflow(workflow).jobs;
    const packaged = jobs['backend-build'].steps.find(step => step.run?.startsWith('tar -czf test-runtime.tar.gz'));
    expect(packaged.run).toContain('test-plan.json test-plan.summary.json');
    const restored = jobs['backend-browser'].steps.find(step => step.name === 'Verify restored CRM through HTTP and browser');
    expect(restored.run).toContain("vstest_console=\"$(node --input-type=module --eval \"import { captureVstestEntryPoint } from './scripts/run-backend-gate.mjs'; process.stdout.write(captureVstestEntryPoint('test-plan.summary.json').path)\")\"");
    expect(restored.run).toContain('dotnet exec "$vstest_console" backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll');
    expect(restored.run).not.toContain('dotnet vstest ');
    expect(restored.run).toContain(' || native_exit=$?');
    expect(restored.run).toContain(' verify-restored --manifest test-plan.json');
  });
  it.each([
    ['SDK dispatch wrapper', 'dotnet exec "$vstest_console" backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll', 'dotnet vstest backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll'],
    ['hardcoded SDK path', "vstest_console=\"$(node --input-type=module --eval \"import { captureVstestEntryPoint } from './scripts/run-backend-gate.mjs'; process.stdout.write(captureVstestEntryPoint('test-plan.summary.json').path)\")\"", 'vstest_console="C:/Program Files/dotnet/sdk/9.0.100/vstest.console.dll"'],
    ['summary missing from transported runtime', 'test-plan.json test-plan.summary.json backend/PayPlanner.Api.Tests/bin/Release/net9.0', 'test-plan.json backend/PayPlanner.Api.Tests/bin/Release/net9.0'],
  ])('rejects restored SDK entry-point drift: %s', (_name, before, after) => {
    expect(() => validateDeployWorkflow(replaceOnce(before, after))).toThrow();
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
