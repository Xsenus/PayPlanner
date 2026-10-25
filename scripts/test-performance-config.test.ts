// @vitest-environment node

import { readFileSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

type PackageJson = {
  scripts?: Record<string, string>;
};

const packageJson = JSON.parse(
  readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'),
) as PackageJson;
const readme = readFileSync(resolve(process.cwd(), 'README.md'), 'utf8');

function collectTestFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) return collectTestFiles(path);
    return /\.test\.[cm]?[jt]sx?$/.test(entry.name) ? [path] : [];
  });
}

describe('frontend test performance configuration', () => {
  it('runs the complete unit set with bounded file parallelism', () => {
    const command = packageJson.scripts?.['test:unit'];

    expect(command).toContain('--exclude "**/*.integration.test.*"');
    expect(command).toContain('--maxWorkers=3');
    expect(command).not.toContain('--no-file-parallelism');
  });

  it('keeps explicit low-impact development and serial diagnostic modes', () => {
    expect(packageJson.scripts?.['test:watch']).toContain('--maxWorkers=2');
    expect(packageJson.scripts?.['test:unit:serial']).toContain('--no-file-parallelism');
  });

  it('runs guards early and excludes exactly those files from the second CI pass', () => {
    const guard = packageJson.scripts?.['test:guard'] ?? '';
    const remaining = packageJson.scripts?.['test:unit:after-guard'] ?? '';
    const guardFiles = guard.split(/\s+/).filter(value => value.endsWith('.test.ts') || value.endsWith('.test.tsx'));
    const exclusions = [...remaining.matchAll(/--exclude ([^ ]+)/g)].map(match => match[1]);

    expect(packageJson.scripts?.['test:ci']).toBe('npm run test:guard && npm run test:unit:after-guard');
    expect(guardFiles).toHaveLength(2);
    expect(exclusions).toEqual(['"**/*.integration.test.*"', ...guardFiles]);
    expect(remaining).toContain('--maxWorkers=3');
  });

  it('documents the optimized and diagnostic commands for developers', () => {
    expect(readme).toContain('`npm test` — полный набор frontend unit-тестов');
    expect(readme).toContain('`npm run test:watch`');
    expect(readme).toContain('`npm run test:unit:serial`');
    expect(readme).toContain('`npm run test:integration`');
    expect(readme).toContain('выполняются в лёгком Node-окружении');
  });

  it('keeps pure tests in Node and the shared setup compatible with both environments', () => {
    const testFiles = [
      ...collectTestFiles(resolve(process.cwd(), 'src')),
      ...collectTestFiles(resolve(process.cwd(), 'scripts')),
    ];
    const nodeTestCount = testFiles.filter((file) =>
      readFileSync(file, 'utf8').includes('@vitest-environment node'),
    ).length;
    const setup = readFileSync(resolve(process.cwd(), 'src/test/setup.ts'), 'utf8');
    const domSetup = readFileSync(resolve(process.cwd(), 'src/test/setup.dom.ts'), 'utf8');

    expect(nodeTestCount).toBeGreaterThanOrEqual(25);
    expect(setup).toContain("typeof window !== 'undefined' && typeof document !== 'undefined'");
    expect(setup).toContain("if (hasDom) {\n  await import('./setup.dom');");
    expect(setup).not.toContain("from '@testing-library/react'");
    expect(domSetup).toContain('cleanup();');
    expect(typeof window).toBe('undefined');
    expect(typeof document).toBe('undefined');
  });
});

describe('load-check scenario catalog', () => {
  it('lists every selectable scenario in command help', () => {
    const help = execFileSync(
      process.execPath,
      [resolve(process.cwd(), 'scripts/run-legal-load-check.mjs'), '--help'],
      { encoding: 'utf8' },
    );

    expect(help).toContain('case-document-types');
    expect(help).toContain('case-document-templates');
    expect(help).toContain('case-document-template-tokens');
    expect(help).toContain('case-calendar-events');
    expect(help).toContain('case-calendar-summary');
  });
});
