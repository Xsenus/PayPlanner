// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { canonicalBytes, contextFromEnvironment, need, parseArguments, productionStoreRoot,
  safeRelativePath, validateInspection } from './xsenus-artifact-store.mjs';

const id = 'xsenus-local-v1:' + 'a'.repeat(64);
const context = (change: Record<string, string | undefined> = {}) => ({
  COMPUTERNAME: 'XSENUS', GITHUB_REPOSITORY: 'Xsenus/PayPlanner',
  GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'push',
  GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main',
  GITHUB_ACTIONS: 'true', RUNNER_NAME: 'PayPlanner-XSENUS', RUNNER_OS: 'Windows', RUNNER_ARCH: 'X64',
  GITHUB_RUN_ID: '12345', GITHUB_RUN_ATTEMPT: '1', GITHUB_SHA: 'b'.repeat(40),
  GITHUB_WORKFLOW_SHA: 'c'.repeat(40), GITHUB_JOB: 'backend',
  PAYPLANNER_CI_ARTIFACT_ROOT: productionStoreRoot, ...change,
});

describe('main artifact transport caller and worker bindings', () => {
  it.each(['0', '1', '2', '3'])('binds canonical shard %s to the actual worker without mutating its environment', shard => {
    const env = context({ GITHUB_JOB: `backend-test-${shard}`, ARTIFACT_SHARD: shard });
    const result = contextFromEnvironment(env, 'win32');
    expect(result.producerJob).toBe(`backend-test-${shard}`);
    expect(env.GITHUB_JOB).toBe(`backend-test-${shard}`);
    expect(env.ARTIFACT_SHARD).toBe(shard);
  });
  it.each(['', '-1', '4', '01', '0 ', '1,2'])('rejects a noncanonical or mismatched worker shard %s', shard => {
    expect(() => contextFromEnvironment(context({ GITHUB_JOB: 'backend-test-0', ARTIFACT_SHARD: shard }), 'win32')).toThrow();
  });
  it('derives the shard from the actual worker when the optional shard hint is absent', () => {
    expect(contextFromEnvironment(context({ GITHUB_JOB: 'backend-test-0' }), 'win32').producerJob).toBe('backend-test-0');
    expect(() => contextFromEnvironment(context({ GITHUB_JOB: 'backend-test', ARTIFACT_SHARD: '0' }), 'win32')).toThrow();
  });
  it.each(['backend-test-4', 'backend-test-01', 'unknown'])('rejects a fabricated actual job %s', job => {
    expect(() => contextFromEnvironment(context({ GITHUB_JOB: job }), 'win32')).toThrow();
  });
  it('rejects a shard claimed by a nonmatrix job', () => {
    expect(() => contextFromEnvironment(context({ ARTIFACT_SHARD: '0' }), 'win32')).toThrow();
  });
  it('rejects the former prototype ref and root even when its old host guard permits dispatch', () => {
    expect(() => contextFromEnvironment(context({ GITHUB_REF: 'refs/heads/claude/practical-bardeen-pglcf3',
      GITHUB_EVENT_NAME: 'workflow_dispatch',
      GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/claude/practical-bardeen-pglcf3' }), 'win32')).toThrow();
    expect(() => contextFromEnvironment(context({ PAYPLANNER_CI_ARTIFACT_ROOT: 'D:\\PayPlannerCIArtifacts\\store' }), 'win32')).toThrow();
  });
  it.each([0, 1, 2, 3])('binds shard %s to the exact worker or aggregate output', shard => {
    const ctx = { needs: { 'backend-test': { result: 'success', outputs: { [`shard_${shard}_id`]: id } } } };
    expect(() => need(ctx, `backend-test-${shard}`, 'qualified-artifact-id', id)).not.toThrow();
    const worker = { needs: { [`backend-test-${shard}`]: { result: 'success', outputs: { 'qualified-artifact-id': id } } } };
    expect(() => need(worker, `backend-test-${shard}`, 'qualified-artifact-id', id)).not.toThrow();
    expect(() => need(worker, `backend-test-${shard}`, 'qualified-artifact-id', 'xsenus-local-v1:' + 'd'.repeat(64))).toThrow();
    expect(() => need({ needs: { [`backend-test-${shard}`]: { result: 'success', outputs: { 'frontend-artifact-id': id } } } },
      `backend-test-${shard}`, 'qualified-artifact-id', id)).toThrow();
    expect(() => need(ctx, `backend-test-${shard}`, 'qualified-artifact-id', 'xsenus-local-v1:' + 'd'.repeat(64))).toThrow();
  });
  it.each(['failure', 'cancelled', 'skipped'])('does not consume a %s matrix producer', result => {
    expect(() => need({ needs: { 'backend-test': { result, outputs: { shard_0_id: id } } } },
      'backend-test-0', 'qualified-artifact-id', id)).toThrow();
  });
  it('rejects unexported direct diagnostic matrix downloads', () => {
    expect(() => need({ needs: { 'backend-test': { result: 'success', outputs: { diagnostics_0_id: id } } } },
      'backend-test-0', 'diagnostics-artifact-id', id)).toThrow();
  });
  it('keeps exact nonmatrix needs output binding and rejects an init CLI mode', () => {
    expect(() => need({ needs: { frontend: { result: 'success', outputs: { 'frontend-artifact-id': id } } } },
      'frontend', 'frontend-artifact-id', id)).not.toThrow();
    expect(() => parseArguments(['init'])).toThrow();
  });
});

describe('preserved typed metadata, relative paths and native inspection contract', () => {
  it('uses stable canonical bytes without broadening a typed receipt', () => {
    expect(canonicalBytes({ z: 2, a: { b: 1 } }).toString()).toBe('{"a":{"b":1},"z":2}\n');
  });
  it.each(['../escape', '/absolute', 'C:ads', 'folder\\escape', 'NUL', 'trailing.', 'a//b'])('rejects unsafe inventory path %s', path => {
    expect(() => safeRelativePath(path)).toThrow();
  });
  it('rejects missing native inventory rather than treating it as a trusted ACL', () => {
    expect(() => validateInspection({ sid: 'S-1-5-18', items: [] }, [productionStoreRoot], productionStoreRoot)).toThrow();
  });
});
