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

describe('parallel deploy workflow release contract', () => {
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
    ['non-strict restore', '--force -p:PayPlannerReleaseAudit=true', '--force'],
    ['format disabled', '--verify-no-changes --no-restore --verbosity minimal', '--no-restore --verbosity minimal'],
    ['analyzers disabled', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release', 'run: dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release -p:RunAnalyzers=false'],
    ['wrong artifact revision', 'name: frontend-${{ github.sha }}', 'name: frontend-main'],
    ['missing artifact tolerated', 'if-no-files-found: error', 'if-no-files-found: warn'],
    ['cross-run download', '          artifact-ids: ${{ needs.backend.outputs.backend-artifact-id }}\n          path: deploy-artifacts', '          artifact-ids: ${{ needs.backend.outputs.backend-artifact-id }}\n          run-id: 123\n          path: deploy-artifacts'],
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
    ['test missing build dependency', 'needs: [backend-build]', 'needs: []'],
    ['manifest producer output missing', 'manifest-sha: ${{ steps.plan.outputs.manifest-sha }}', 'manifest-sha: fake'],
    ['manifest hash recomputed downstream', 'EXPECTED_MANIFEST_SHA: ${{ needs.backend-build.outputs.manifest-sha }}', 'EXPECTED_MANIFEST_SHA: fake'],
    ['untrusted manifest not checked', '--manifest-sha "$EXPECTED_MANIFEST_SHA" --shard', '--shard'],
    ['union check removed', ' verify --manifest test-plan.json', ' help --manifest test-plan.json'],
    ['union wrong root', '--results-root test-results', '--results-root other-results'],
    ['runtime missing manifest', 'tar -czf test-runtime.tar.gz test-plan.json ', 'tar -czf test-runtime.tar.gz '],
    ['runtime missing probe dependencies', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0 tools/', ' backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll tools/'],
    ['wrong shard result directory', 'path: test-results\n', 'path: test-results/shard-0\n'],
    ['precreated shard directory', 'mkdir -p test-results\n', 'mkdir -p test-results/shard-${{ matrix.shard }}\n'],
    ['wrong shard artifact', 'name: qualified-backend-shard-3-${{ github.sha }}', 'name: qualified-backend-shard-2-${{ github.sha }}'],
    ['wrong attempt diagnostics', 'name: backend-shard-${{ matrix.shard }}-${{ github.sha }}-${{ github.run_attempt }}', 'name: backend-shard-${{ matrix.shard }}-${{ github.sha }}-1'],
    ['results only on success', '        if: always()\n        uses: actions/upload-artifact@v6', '        uses: actions/upload-artifact@v6'],
    ['test SDK drift', 'dotnet-version: ${{ needs.backend-build.outputs.sdk-version }}', 'dotnet-version: "8.0.x"'],
    ['restored native exit replaced', '--exit-code "$native_exit"', '--exit-code 0'],
    ['restored verifier omitted', ' verify-restored --manifest', ' help --manifest'],
    ['restored exit overwritten', '          cat test-results/restored-browser/test.log', '          native_exit=0\n          cat test-results/restored-browser/test.log'],
    ['restored failure swallowed', ' || native_exit=$?', ' || true'],
    ['empty restored selection allowed', '<TreatNoTestsAsError>true</TreatNoTestsAsError>', '<TreatNoTestsAsError>false</TreatNoTestsAsError>'],
    ['restored method omitted', '|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource', ''],
    ['PostgreSQL service omitted', 'image: postgres:17-alpine', 'image: redis:7-alpine'],
    ['tool analyzers disabled', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release', 'run: dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release -p:RunAnalyzers=false'],
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
    ['alias cannot replace prior success', '          overwrite: true\n', '          overwrite: false\n'],
    ['alias missing manifest binding', '-${{ needs.backend-build.outputs.manifest-sha }}-${{ needs.backend-build.outputs.sdk-version }}', '-${{ needs.backend-build.outputs.sdk-version }}'],
    ['alias missing SDK binding', '-${{ needs.backend-build.outputs.manifest-sha }}-${{ needs.backend-build.outputs.sdk-version }}', '-${{ needs.backend-build.outputs.manifest-sha }}'],
    ['alias tied to current attempt', 'name: qualified-backend-shard-0-${{ github.sha }}', 'name: qualified-backend-shard-0-${{ github.sha }}-${{ github.run_attempt }}'],
    ['diagnostics overwritten', '          name: backend-shard-${{ matrix.shard }}-${{ github.sha }}-${{ github.run_attempt }}\n', '          name: backend-shard-${{ matrix.shard }}-${{ github.sha }}-${{ github.run_attempt }}\n          overwrite: true\n'],
    ['diagnostics history shortened', '          retention-days: 14\n', '          retention-days: 1\n'],
    ['archive attempt omitted', 'name: test-runtime-${{ github.sha }}-${{ github.run_attempt }}', 'name: test-runtime-${{ github.sha }}'],
    ['archive overwritten', '          name: frontend-${{ github.sha }}-${{ github.run_attempt }}\n', '          name: frontend-${{ github.sha }}-${{ github.run_attempt }}\n          overwrite: true\n'],
    ['runtime ID not uploader output', 'runtime-artifact-id: ${{ steps.runtime-upload.outputs.artifact-id }}', 'runtime-artifact-id: ${{ github.run_id }}'],
    ['frontend ID not uploader output', 'frontend-artifact-id: ${{ steps.frontend-upload.outputs.artifact-id }}', 'frontend-artifact-id: 123'],
    ['backend ID not uploader output', 'backend-artifact-id: ${{ steps.backend-upload.outputs.artifact-id }}', 'backend-artifact-id: 123'],
    ['aggregate substitutes backend ID', 'backend-artifact-id: ${{ needs.backend-build.outputs.backend-artifact-id }}', 'backend-artifact-id: 123'],
    ['runtime falls back to name', 'artifact-ids: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'name: test-runtime-${{ github.sha }}'],
    ['runtime allows multiple IDs', 'artifact-ids: ${{ needs.backend-build.outputs.runtime-artifact-id }}', 'artifact-ids: ${{ needs.backend-build.outputs.runtime-artifact-id }},123'],
    ['runtime allows empty ID', '[[ "$RUNTIME_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]', 'true'],
    ['runtime compares wrong SDK', 'test "$actual_sdk" = "$EXPECTED_SDK_VERSION"', 'test "$actual_sdk" = "9.0.100"'],
    ['SDK check only echoes expected label', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$EXPECTED_SDK_VERSION"'],
    ['SDK check hides command failure', 'actual_sdk="$(dotnet --version)"', 'actual_sdk="$(dotnet --version || true)"'],
    ['deploy allows empty backend ID', '[[ "$BACKEND_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]', 'true'],
    ['deploy allows empty frontend ID', '[[ "$FRONTEND_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]', 'true'],
    ['deploy accepts same archive twice', '[[ "$BACKEND_ARTIFACT_ID" != "$FRONTEND_ARTIFACT_ID" ]]', 'true'],
    ['deploy ID bypasses backend qualification', 'artifact-ids: ${{ needs.backend.outputs.backend-artifact-id }}', 'artifact-ids: ${{ needs.backend-build.outputs.backend-artifact-id }}'],
    ['download arbitrary cross-run ID', '          artifact-ids: ${{ needs.backend-build.outputs.runtime-artifact-id }}\n', '          artifact-ids: ${{ needs.backend-build.outputs.runtime-artifact-id }}\n          github-token: ${{ github.token }}\n'],
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

  it('keeps qualified shard keys stable for shard-only, browser-only and aggregate-only retries', () => {
    const jobs = parseWorkflow(workflow).jobs;
    const upload = jobs['backend-test'].steps.find(step => step.name === 'Publish qualified shard alias').with.name;
    const downloads = jobs.backend.steps.filter(step => step.with?.name?.startsWith('qualified-backend-shard-'));
    const context = { 'github.sha': 'a'.repeat(40), 'needs.backend-build.outputs.manifest-sha': 'b'.repeat(64), 'needs.backend-build.outputs.sdk-version': '9.0.318' };
    function evaluate(template: string, attempt: number, shard: number, changes: Record<string, string> = {}) {
      const values: Record<string, string> = { ...context, ...changes, 'github.run_attempt': String(attempt), 'matrix.shard': String(shard) };
      return template.replace(/\$\{\{ (.*?) \}\}/g, (_match, key) => values[key]);
    }
    expect(downloads.length).toBe(4);
    for (let index = 0; index < 4; index += 1) {
      expect(evaluate(downloads[index].with.name, 2, index)).toBe(evaluate(upload, index === 1 ? 2 : 1, index));
      expect(evaluate(downloads[index].with.name, 3, index)).toBe(evaluate(upload, 1, index));
    }
    expect(evaluate(upload, 1, 0) === evaluate(upload, 2, 0, { 'needs.backend-build.outputs.manifest-sha': 'c'.repeat(64) })).toBe(false);
    expect(evaluate(upload, 1, 0) === evaluate(upload, 2, 0, { 'needs.backend-build.outputs.sdk-version': '9.0.319' })).toBe(false);
    expect(evaluate(upload, 1, 0) === evaluate(upload, 2, 0, { 'github.sha': 'd'.repeat(40) })).toBe(false);
  });

  it('uses distinct immutable archive attempt names and exact producer IDs for rerun consumers', () => {
    const jobs = parseWorkflow(workflow).jobs;
    for (const [job, id] of [['frontend', 'frontend-upload'], ['backend-build', 'runtime-upload'], ['backend-build', 'backend-upload']]) {
      const upload = jobs[job].steps.find(step => step.id === id);
      expect(upload.with.name.includes('${{ github.sha }}-${{ github.run_attempt }}')).toBe(true);
      expect(upload.with.name.replace('${{ github.run_attempt }}', '1') === upload.with.name.replace('${{ github.run_attempt }}', '2')).toBe(false);
      expect(upload.with.overwrite).toBe(undefined);
    }
    for (const id of ['backend-test', 'backend-browser', 'backend']) {
      const download = jobs[id].steps.find(step => step.name === 'Download compiled test runtime');
      expect(download.with).toEqual({ 'artifact-ids': '${{ needs.backend-build.outputs.runtime-artifact-id }}', path: 'test-artifacts' });
    }
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
