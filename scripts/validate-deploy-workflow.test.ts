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
      artifacts: ['backend-${{ github.sha }}', 'frontend-${{ github.sha }}'],
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
    ['cross-run download', '          name: backend-${{ github.sha }}\n          path: deploy-artifacts', '          name: backend-${{ github.sha }}\n          run-id: 123\n          path: deploy-artifacts'],
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
    ['wrong shard artifact', 'name: backend-shard-3-${{ github.sha }}', 'name: backend-shard-2-${{ github.sha }}'],
    ['wrong attempt results', 'name: backend-shard-0-${{ github.sha }}-${{ github.run_attempt }}', 'name: backend-shard-0-${{ github.sha }}-1'],
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
