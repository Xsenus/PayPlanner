// @vitest-environment node

import { mkdtempSync, mkdirSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { postgresSettings, requireTrustedHost, verifyPortablePackage } from './verify-xsenus-runner.mjs';

const trusted = {
  COMPUTERNAME: 'XSENUS', GITHUB_REPOSITORY: 'Xsenus/PayPlanner', GITHUB_REF: 'refs/heads/main',
  GITHUB_EVENT_NAME: 'push', GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/main',
};
const connection = `Host=127.0.0.1;Port=5433;Database=postgres;Username=payplanner_ci;Password=${'a'.repeat(64)};Pooling=false`;

describe('trusted XSENUS runner boundary', () => {
  it.each(['push', 'workflow_dispatch'])('accepts a trusted main %s', event => {
    expect(() => requireTrustedHost({ ...trusted, GITHUB_EVENT_NAME: event }, 'win32')).not.toThrow();
  });
  it('allows only explicit manual dispatch of the current trusted release branch', () => {
    const branch = { ...trusted, GITHUB_REF: 'refs/heads/claude/practical-bardeen-pglcf3',
      GITHUB_WORKFLOW_REF: 'Xsenus/PayPlanner/.github/workflows/deploy.yml@refs/heads/claude/practical-bardeen-pglcf3' };
    expect(() => requireTrustedHost({ ...branch, GITHUB_EVENT_NAME: 'workflow_dispatch' }, 'win32')).not.toThrow();
    expect(() => requireTrustedHost(branch, 'win32')).toThrow();
  });
  it.each([
    ['COMPUTERNAME', 'ILYA'], ['GITHUB_REPOSITORY', 'external/PayPlanner'],
    ['GITHUB_REF', 'refs/pull/115/merge'], ['GITHUB_REF', 'refs/heads/feature'],
    ['GITHUB_EVENT_NAME', 'pull_request'], ['GITHUB_EVENT_NAME', 'pull_request_target'],
    ['GITHUB_WORKFLOW_REF', 'Xsenus/PayPlanner/.github/workflows/other.yml@refs/heads/main'],
  ])('rejects an untrusted %s=%s', (key, value) => {
    expect(() => requireTrustedHost({ ...trusted, [key]: value }, 'win32')).toThrow(/trusted/);
  });
  it('rejects another operating system or missing identity', () => {
    expect(() => requireTrustedHost(trusted, 'linux')).toThrow();
    expect(() => requireTrustedHost({}, 'win32')).toThrow();
  });
});

describe('dedicated existing PostgreSQL connection', () => {
  it('accepts only the dedicated loopback test role', () => {
    expect(postgresSettings(connection)).toEqual({ password: 'a'.repeat(64) });
  });
  it.each([
    ['127.0.0.1', 'server.example'], ['5433', '5432'], ['Database=postgres', 'Database=payplanner'],
    ['Username=payplanner_ci', 'Username=postgres'], ['Pooling=false', 'Pooling=true'],
    ['Password=' + 'a'.repeat(64), 'Password='],
  ])('rejects altered prerequisite %s without exposing credentials', (before, after) => {
    expect(() => postgresSettings(connection.replace(before, after))).toThrow();
  });
  it.each([';Host=127.0.0.1', ';Application Name=CI', ';Search Path=public'])('rejects extra or duplicate fields %s', extra => {
    expect(() => postgresSettings(connection + extra)).toThrow();
  });
  it('rejects missing connection and malformed fields', () => {
    expect(() => postgresSettings(undefined)).toThrow();
    expect(() => postgresSettings(connection + ';malformed')).toThrow();
  });
});

describe('portable Linux deployment package from Windows build', () => {
  it.each(['valid', 'windows-rid', 'missing-sqlite', 'missing-migrations'])('verifies %s before packaging', variant => {
    const root = mkdtempSync(join(tmpdir(), 'payplanner-portable-'));
    try {
      mkdirSync(join(root, 'runtimes/linux-x64/native'), { recursive: true });
      writeFileSync(join(root, 'PayPlanner.Api.deps.json'), JSON.stringify({ runtimeTarget: {
        name: variant === 'windows-rid' ? '.NETCoreApp,Version=v9.0/win-x64' : '.NETCoreApp,Version=v9.0',
      } }));
      writeFileSync(join(root, 'PayPlanner.Api.dll'), 'fixture');
      if (variant !== 'missing-migrations') writeFileSync(join(root, 'PayPlanner.Api.Migrations.dll'), 'fixture');
      if (variant !== 'missing-sqlite') writeFileSync(join(root, 'runtimes/linux-x64/native/libe_sqlite3.so'), 'fixture');
      writeFileSync(join(root, 'PayPlanner.Api.exe'), 'Windows apphost');
      if (variant === 'valid') {
        verifyPortablePackage(root);
        expect(existsSync(join(root, 'PayPlanner.Api.exe'))).toBe(false);
        expect(existsSync(join(root, 'PayPlanner.Api.dll'))).toBe(true);
      } else {
        expect(() => verifyPortablePackage(root)).toThrow(/portable/);
        expect(existsSync(join(root, 'PayPlanner.Api.exe'))).toBe(true);
      }
    } finally { rmSync(root, { recursive: true, force: true }); }
  });
});
