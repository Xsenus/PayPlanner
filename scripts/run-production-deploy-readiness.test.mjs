import { describe, expect, it } from 'vitest';

import {
  DEFAULT_REVISION_PATH,
  buildDeployConfig,
  checkDeployRevision,
  compareSha,
  normalizeSha,
} from './run-production-deploy-readiness.mjs';

describe('run-production-deploy-readiness configuration', () => {
  it('normalizes and compares full or short git SHAs', () => {
    expect(normalizeSha('ABCDEF1234567')).toBe('abcdef1234567');
    expect(compareSha('abcdef1', 'abcdef1234567890')).toBe(true);
    expect(compareSha('abcdef1234567890', 'abcdef1')).toBe(true);
    expect(compareSha('abcdef1', '1234567')).toBe(false);
    expect(() => normalizeSha('not-a-sha')).toThrow(/Expected a git SHA/);
  });

  it('uses GITHUB_SHA as expected revision and requires marker by default', () => {
    const previousSha = process.env.GITHUB_SHA;
    process.env.GITHUB_SHA = '4c99d82a1bffa610d7e8d18ca6e4141bf53fe18e';

    try {
      expect(buildDeployConfig({})).toMatchObject({
        expectedSha: '4c99d82a1bffa610d7e8d18ca6e4141bf53fe18e',
        requireRevision: true,
        revisionPath: DEFAULT_REVISION_PATH,
      });
    } finally {
      if (previousSha === undefined) {
        delete process.env.GITHUB_SHA;
      } else {
        process.env.GITHUB_SHA = previousSha;
      }
    }
  });

  it('allows local dry runs without an expected revision marker', () => {
    const previousSha = process.env.GITHUB_SHA;
    delete process.env.GITHUB_SHA;

    try {
      expect(buildDeployConfig({
        apiUrl: 'http://localhost:5080',
        frontendUrl: 'http://localhost:5173',
        allowHttp: 'true',
      })).toMatchObject({
        expectedSha: '',
        requireRevision: false,
        revisionPath: DEFAULT_REVISION_PATH,
        allowHttp: true,
      });
    } finally {
      if (previousSha !== undefined) {
        process.env.GITHUB_SHA = previousSha;
      }
    }
  });

  it('normalizes custom revision path and explicit flags', () => {
    expect(buildDeployConfig({
      expectedSha: '4c99d82',
      revisionPath: 'revision.txt',
      requireRevision: 'false',
    })).toMatchObject({
      expectedSha: '4c99d82',
      revisionPath: '/revision.txt',
      requireRevision: false,
    });
  });

  it('skips SPA fallback revision responses when revision is optional', async () => {
    const previousFetch = globalThis.fetch;
    globalThis.fetch = async () => new Response('<!doctype html><title>CRM</title>', { status: 200 });

    try {
      await expect(checkDeployRevision({
        frontendUrl: 'https://crm.example',
        timeoutMs: 1000,
        expectedSha: '4c99d82',
        requireRevision: false,
        revisionPath: DEFAULT_REVISION_PATH,
      })).resolves.toMatch(/skipped/);
    } finally {
      globalThis.fetch = previousFetch;
    }
  });
});
