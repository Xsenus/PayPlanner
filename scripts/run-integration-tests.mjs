import { readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { spawnSync } from 'node:child_process';

const rootDir = process.cwd();
const srcDir = join(rootDir, 'src');
const integrationTestPattern = /\.integration\.test\.[cm]?[jt]sx?$/;

function collectIntegrationTests(dir) {
  const entries = readdirSync(dir, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectIntegrationTests(fullPath));
      continue;
    }

    if (entry.isFile() && integrationTestPattern.test(entry.name)) {
      files.push(relative(rootDir, fullPath).split(sep).join('/'));
    }
  }

  return files;
}

// Root-level Vite integration tests are also excluded from the unit gate.
// Inspect only root files here, without traversing dependencies or build artifacts.
const rootFiles = readdirSync(rootDir, { withFileTypes: true })
  .filter(entry => entry.isFile() && integrationTestPattern.test(entry.name))
  .map(entry => entry.name);
const files = [...collectIntegrationTests(srcDir), ...rootFiles].sort();

if (files.length === 0) {
  console.error('No integration tests found.');
  process.exit(1);
}

const vitestBin = join(rootDir, 'node_modules', 'vitest', 'vitest.mjs');
const result = spawnSync(process.execPath, [vitestBin, 'run', ...files, '--maxWorkers=1'], {
  cwd: rootDir,
  stdio: 'inherit',
  shell: false,
});

if (result.error) {
  console.error(result.error.message);
}

process.exit(result.status ?? 1);
