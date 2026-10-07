// @vitest-environment node
import { createHash } from 'node:crypto';
import { readFileSync, realpathSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

type BraceNode = { nodes?: BraceNode[]; [key: string]: unknown };
type BraceOptions = Record<string, unknown>;
type BracesApi = {
  parse: (input: string, options?: BraceOptions) => BraceNode;
  compile: (input: string | BraceNode, options?: BraceOptions) => string;
  expand: (input: string | BraceNode, options?: BraceOptions) => string[];
  stringify: (input: string | BraceNode, options?: BraceOptions) => string;
};
type Provenance = { upstreamIntegrity: string; files: { path: string; upstreamSha256: string | null; patchedSha256: string }[] };
type Artifact = { filename: string; integrity: string; provenanceSha256: string };

const loadModule = createRequire(import.meta.url);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const vendor = path.join(repo, 'vendor/braces-depth-guard');
const fixture = path.join(repo, 'src/build/fixtures/braces-3.0.3');
const upstream = loadModule(fixture) as BracesApi;
const sha256 = (file: string) => createHash('sha256').update(readFileSync(file)).digest('hex');

function actualImports(): { consumer: string; modulePath: string }[] {
  const tailwind = createRequire(loadModule.resolve('tailwindcss'));
  const fastGlob = createRequire(tailwind.resolve('fast-glob'));
  return [
    { consumer: 'Tailwind → chokidar', modulePath: createRequire(tailwind.resolve('chokidar')).resolve('braces') },
    { consumer: 'Tailwind → micromatch', modulePath: createRequire(tailwind.resolve('micromatch')).resolve('braces') },
    { consumer: 'Tailwind → fast-glob → micromatch', modulePath: createRequire(fastGlob.resolve('micromatch')).resolve('braces') },
  ];
}

function normalized(node: BraceNode): unknown {
  const fields = Object.fromEntries(Object.entries(node).filter(([key]) => !['parent', 'prev', 'queue', 'nodes'].includes(key)));
  return node.nodes ? { ...fields, nodes: node.nodes.map(normalized) } : fields;
}

describe('the actual installed braces security patch', () => {
  it('fails the same security probe on the unchanged upstream control', () => {
    const child = spawnSync(process.execPath, [path.join(repo, 'scripts/check-braces-depth-security.cjs'), loadModule.resolve(fixture)], { encoding: 'utf8', timeout: 10000, maxBuffer: 32768 });
    expect(child.error?.message).toBeUndefined();
    expect(child.status).toBe(1);
    expect(child.stderr).toContain('Missing expected exception');
  });

  it('preserves upstream license/source provenance and the locked archive integrity', () => {
    const provenance = JSON.parse(readFileSync(path.join(vendor, 'provenance.json'), 'utf8')) as Provenance;
    const artifact = JSON.parse(readFileSync(path.join(vendor, 'artifact.json'), 'utf8')) as Artifact;
    expect(provenance.upstreamIntegrity).toBe('sha512-yQbXgO/OSZVD2IsiLlro+7Hf6Q18EJrKSEsdoMzKePKXct3gvD8oLcOQdIzGupr5Fj+EDe8gO/lxc1BzfMpxvA==');
    expect(sha256(path.join(vendor, 'provenance.json'))).toBe(artifact.provenanceSha256);
    const integrity = `sha512-${createHash('sha512').update(readFileSync(path.join(vendor, artifact.filename))).digest('base64')}`;
    expect(integrity).toBe(artifact.integrity);
    const lock = JSON.parse(readFileSync(path.join(repo, 'package-lock.json'), 'utf8')) as { packages: Record<string, { integrity?: string }> };
    expect(lock.packages['node_modules/braces'].integrity).toBe(integrity);
    expect(readFileSync(path.join(vendor, 'LICENSE'))).toEqual(readFileSync(path.join(fixture, 'LICENSE')));
    for (const file of provenance.files) {
      expect(sha256(path.join(vendor, file.path)), file.path).toBe(file.patchedSha256);
      if (file.upstreamSha256) expect(sha256(path.join(fixture, file.path)), file.path).toBe(file.upstreamSha256);
    }
    for (const { modulePath } of actualImports()) {
      const installed = path.dirname(realpathSync(modulePath));
      for (const file of provenance.files) expect(sha256(path.join(installed, file.path)), file.path).toBe(file.patchedSha256);
    }
  });

  it.each(actualImports())('$consumer uses mandatory defaults for strings and caller ASTs', ({ modulePath }) => {
    const child = spawnSync(process.execPath, [path.join(repo, 'scripts/check-braces-depth-security.cjs'), modulePath], { encoding: 'utf8', timeout: 10000, maxBuffer: 32768 });
    expect(child.error?.message).toBeUndefined();
    expect(child.status, child.stderr).toBe(0);
    expect((JSON.parse(child.stdout) as { passed: number }).passed).toBe(78);
  });

  it('accepts the exact nesting boundary and retains escaped/quoted literal treatment', () => {
    for (const { modulePath } of actualImports()) {
      const braces = loadModule(modulePath) as BracesApi;
      for (const input of [
        '{'.repeat(100) + 'a' + '}'.repeat(100),
        '('.repeat(100) + 'a' + ')'.repeat(100),
        '"' + '{'.repeat(4000) + 'a' + '}'.repeat(4000) + '"',
        '\\{'.repeat(2000) + 'a' + '\\}'.repeat(2000),
      ]) {
        for (const operation of ['compile', 'expand', 'stringify'] as const) expect(braces[operation](input)).toEqual(upstream[operation](input));
      }
      expect(() => upstream.parse('{'.repeat(101) + 'a')).not.toThrow();
      expect(() => braces.parse('{'.repeat(101) + 'a')).toThrow(SyntaxError);
    }
  });

  it('preserves ordinary glob/range/invalid/dollar/string and direct AST results', () => {
    const braces = loadModule(actualImports()[0].modulePath) as BracesApi;
    const patterns = ['', 'a', '{}', '{a}', '{a,b}', '{a,,b}', '{a..z}', '{1..10}', '{01..05}', '{9..1..2}', '${a,b}', 'x/${a,{b,c}}/z', '{a,b', 'a,b}', '{', '}', '(a,{b,c})', '((a))', '\\{a,b\\}', '[{a,b}]', '"{a,b}"', "'{a,b}'", '`{a,b}`', '{a,{b,{c,d}}}', '{a...b}', '{a..b..c}', 'src/**/*.{js,ts,jsx,tsx}', './src/{a,b}/**/*.{js,ts}', '{a,b}/[!x]/**/*'];
    for (const pattern of patterns) {
      for (const options of [{}, { escapeInvalid: true }, { keepEscaping: true }, { keepQuotes: true }, { nodupes: true, noempty: true }]) {
        expect(normalized(braces.parse(pattern, options))).toEqual(normalized(upstream.parse(pattern, options)));
        for (const operation of ['compile', 'expand', 'stringify'] as const) {
          expect(braces[operation](pattern, options)).toEqual(upstream[operation](pattern, options));
          expect(braces[operation](upstream.parse(pattern, options), options)).toEqual(upstream[operation](upstream.parse(pattern, options), options));
        }
      }
    }
  });
});
