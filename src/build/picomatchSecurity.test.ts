// @vitest-environment node
import { createHash } from 'node:crypto';
import { readFileSync, realpathSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

type Picomatch = {
  makeRe: (pattern: string, options?: { maxExtglobRecursion?: number | false }) => RegExp;
  isMatch: (input: string, pattern: string) => boolean;
};
type PackageProvenance = {
  upstreamVersion: string;
  sourceDirectory: string;
  archive: string;
  integrity: string;
  archiveSha256: string;
  files: { path: string; upstreamSha256: string; patchedSha256: string }[];
};

const root = fileURLToPath(new URL('../../', import.meta.url));
const vendor = join(root, 'vendor/picomatch-repeat-guard');
const load = createRequire(import.meta.url);
const provenance = JSON.parse(readFileSync(join(vendor, 'provenance.json'), 'utf8')) as { packages: PackageProvenance[] };
const requireFrom = (consumer: string) => createRequire(load.resolve(consumer));
const consumers = [
  { name: 'anymatch', major: 2, require: requireFrom('anymatch') },
  { name: 'micromatch', major: 2, require: requireFrom('micromatch') },
  { name: 'readdirp', major: 2, require: requireFrom('readdirp') },
  { name: 'tinyglobby', major: 4, require: requireFrom('tinyglobby') },
  { name: 'Vite', major: 4, require: requireFrom('vite') },
  { name: 'Vitest', major: 4, require: requireFrom('vitest') },
];

describe('the installed picomatch security patch', () => {
  it('pins the complete archive and preserves upstream source and licenses', () => {
    const lock = JSON.parse(readFileSync(join(root, 'package-lock.json'), 'utf8')) as {
      packages: Record<string, { version: string; resolved?: string; integrity?: string }>;
    };
    const locked = Object.entries(lock.packages).filter(([path]) => path.endsWith('/picomatch'));
    expect(locked).toHaveLength(consumers.length);
    expect(provenance.packages.map(pkg => pkg.upstreamVersion)).toEqual(['2.3.2', '4.0.7']);
    for (const pkg of provenance.packages) {
      const archive = readFileSync(join(vendor, pkg.archive));
      expect(createHash('sha256').update(archive).digest('hex')).toBe(pkg.archiveSha256);
      expect(`sha512-${createHash('sha512').update(archive).digest('base64')}`).toBe(pkg.integrity);
      const version = `${pkg.upstreamVersion}-payplanner.1`;
      const copies = locked.filter(([, entry]) => entry.version === version);
      expect(copies).toHaveLength(3);
      for (const [, entry] of copies) {
        expect(entry.resolved).toBe(`file:vendor/picomatch-repeat-guard/${pkg.archive}`);
        expect(entry.integrity).toBe(pkg.integrity);
      }
      expect(pkg.files.filter(file => file.patchedSha256 !== file.upstreamSha256).map(file => file.path))
        .toEqual(['lib/parse.js', 'package.json']);
    }
  });

  it.each(consumers)('$name uses the reviewed archive for major $major', (consumer) => {
    const pkg = provenance.packages.find(item => item.upstreamVersion.startsWith(`${consumer.major}.`));
    expect(pkg).toBeDefined();
    if (!pkg) throw new Error('Missing picomatch provenance.');
    const installed = dirname(realpathSync(consumer.require.resolve('picomatch/package.json')));
    for (const file of pkg.files) {
      const source = readFileSync(join(vendor, pkg.sourceDirectory, file.path));
      const loaded = readFileSync(join(installed, file.path));
      expect(createHash('sha256').update(source).digest('hex'), file.path).toBe(file.patchedSha256);
      expect(loaded, file.path).toEqual(source);
    }
  });

  it.each(consumers)('$name guards repeated variable-length branches and preserves ordinary globs', (consumer) => {
    const picomatch = consumer.require('picomatch') as Picomatch;
    const variableWidth = '*' + '.txt';
    for (const repeat of ['+', '*']) {
      const pattern = `${repeat}(${variableWidth})`;
      const literal = pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      for (const options of [{}, { maxExtglobRecursion: false as const }, { maxExtglobRecursion: 100 }]) {
        expect(picomatch.makeRe(pattern, options).source).toBe(`^(?:${literal})$`);
      }
      expect(picomatch.isMatch(pattern, pattern)).toBe(true);
      expect(picomatch.isMatch('notes.txt', pattern)).toBe(false);
    }
    expect(picomatch.isMatch('reports/november.csv', '**/*.csv')).toBe(true);
    expect(picomatch.isMatch('reports/november.txt', '**/*.csv')).toBe(false);
    expect(picomatch.isMatch('readyready', '+(ready)')).toBe(true);
    expect(picomatch.isMatch('aab', '+(a|b)')).toBe(true);
    expect(picomatch.isMatch('aaaa', '+(*(a))')).toBe(true);
    expect(picomatch.isMatch('a1a2', '+(a?)')).toBe(true);
    expect(picomatch.isMatch('*note*note', '+("*note")')).toBe(true);
  });
});
