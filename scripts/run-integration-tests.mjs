import { readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const integrationTestPattern = /\.integration\.test\.[cm]?[jt]sx?$/;
const reporters = new Set(['default', 'verbose', 'dot', 'json', 'tap', 'tap-flat', 'junit', 'tree']);
const help = `Usage: npm run test:integration -- [exact integration file ...] [--reporter NAME]

Without files, runs every integration test under src/ and at the repository root.
Files must be exact repository-relative paths, not directories or glob/substring filters.
Runs once with maxWorkers=1. --reporter can be repeated (default, verbose, dot, json,
tap, tap-flat, junit, tree). --help prints this message without running tests.
Test-name filters (-t/--testNamePattern), coverage overrides, exclusions and other
Vitest options are not accepted here. Selected files are a development check;
the unfiltered command and mandatory full CI remain the release gates.`;

function collectIntegrationTests(rootDir, dir) {
  const entries = readdirSync(dir, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectIntegrationTests(rootDir, fullPath));
      continue;
    }

    if (entry.isFile() && integrationTestPattern.test(entry.name)) {
      files.push(relative(rootDir, fullPath).split(sep).join('/'));
    }
  }

  return files;
}

export function discoverIntegrationTests(rootDir) {
  // Root-level Vite integration tests are also excluded from the unit gate.
  // Do not traverse dependencies or build artifacts.
  const rootFiles = readdirSync(rootDir, { withFileTypes: true })
    .filter(entry => entry.isFile() && integrationTestPattern.test(entry.name))
    .map(entry => entry.name);
  return [...collectIntegrationTests(rootDir, join(rootDir, 'src')), ...rootFiles].sort();
}

export function selectIntegrationTests(args, availableFiles) {
  const selected = new Set();
  const options = [];

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--reporter' || arg.startsWith('--reporter=')) {
      const value = arg === '--reporter' ? args[++index] : arg.slice('--reporter='.length);
      if (!reporters.has(value)) {
        throw new Error('Expected a supported --reporter name. See --help.');
      }
      options.push(`--reporter=${value}`);
      continue;
    }
    if (arg.startsWith('-')) {
      throw new Error(`Unsupported integration option: ${arg}. Select exact files; see --help.`);
    }

    const file = arg.replaceAll('\\', '/').replace(/^\.\//, '');
    if (!file || file.split('/').includes('..') || !availableFiles.includes(file)) {
      throw new Error(`Unknown integration file: ${arg || '(empty)'}. Use an exact repository-relative path.`);
    }
    selected.add(file);
  }

  if (availableFiles.length === 0) throw new Error('No integration tests found.');
  const files = selected.size ? availableFiles.filter(file => selected.has(file)) : availableFiles;
  // Vitest positional arguments are case-insensitive substring filters. Refuse
  // an ambiguous selection rather than silently running a different catalog file.
  for (const file of files) {
    const unexpected = availableFiles.find(candidate =>
      !files.includes(candidate) && candidate.toLowerCase().includes(file.toLowerCase()));
    if (unexpected) {
      throw new Error(`Ambiguous Vitest file filter: ${file} also matches ${unexpected}. Select both files explicitly.`);
    }
  }
  return { files, options };
}

export function runIntegrationTests(rootDir, args) {
  if (args.length === 1 && (args[0] === '--help' || args[0] === '-h')) {
    console.log(help);
    return 0;
  }

  const { files, options } = selectIntegrationTests(args, discoverIntegrationTests(rootDir));
  const vitestBin = join(rootDir, 'node_modules', 'vitest', 'vitest.mjs');
  const result = spawnSync(process.execPath, [vitestBin, 'run', ...files, '--maxWorkers=1', ...options], {
    cwd: rootDir,
    stdio: 'inherit',
    shell: false,
  });
  if (result.error) console.error(result.error.message);
  return result.status ?? 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = runIntegrationTests(process.cwd(), process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
