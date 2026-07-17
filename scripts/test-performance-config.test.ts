import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

type PackageJson = {
  scripts?: Record<string, string>;
};

const packageJson = JSON.parse(
  readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'),
) as PackageJson;
const readme = readFileSync(resolve(process.cwd(), 'README.md'), 'utf8');

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

  it('documents the optimized and diagnostic commands for developers', () => {
    expect(readme).toContain('`npm test` — полный набор frontend unit-тестов');
    expect(readme).toContain('`npm run test:watch`');
    expect(readme).toContain('`npm run test:unit:serial`');
    expect(readme).toContain('`npm run test:integration`');
  });
});
