// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { discoverChangedPaths, normalizeChangedPath, parseNameStatus, planVerificationScope } from './verification-scope.mjs';

describe('fail-closed development verification scope', () => {
  it.each([
    ['src/components/Tasks/TaskJournal.tsx', 'frontend'],
    ['src/hooks/useToast.ts', 'frontend'],
    ['public/logo.svg', 'frontend'],
    ['backend/Services/TaskService.cs', 'backend'],
    ['backend/PayPlanner.Api.Tests/TaskTests.cs', 'backend'],
    ['AGENTS.md', 'documentation'],
  ])('selects the known area for %s', (path, expected) => {
    const plan = planVerificationScope([path]);
    expect(plan.scope).toBe(expected);
    const checks = plan.development.requiredChecks;
    if (expected === 'frontend') {
      expect(checks).toContain('frontend-unit');
      expect(checks).not.toContain('backend-full');
    }
    if (expected === 'backend') {
      expect(checks).toContain('backend-full');
      expect(checks).not.toContain('frontend-unit');
    }
    expect(checks).toContain('source-encoding');
  });

  it.each([
    'src/services/api.ts', 'src/types/index.ts', 'src/types.ts', 'src/config/features.ts',
    'backend/Controllers/TasksController.cs', 'backend/Models/Task.cs', 'backend/Data/AppDbContext.cs',
    'backend/Program.cs', 'backend/PayPlanner.Api.csproj', 'package-lock.json', 'package.json',
    '.github/workflows/deploy.yml', 'vitest.config.ts', 'tsconfig.app.json',
    'scripts/check-case-financial-live-browser.mjs', 'vendor/fixture.tgz',
    'new-feature/config.yaml', 'docs/new-test-fixture.md', 'backend/Assets/template.docx',
  ])('expands shared, dependency and unknown input %s to both sides', path => {
    const plan = planVerificationScope([path]);
    expect(plan.scope).toBe('both');
    expect(plan.development.requiredChecks).toEqual(expect.arrayContaining(['frontend-unit', 'backend-full', 'frontend-integration']));
    expect(plan.changedPaths[0].reason.length).toBeGreaterThan(20);
  });

  it('cannot discard the old side of a rename or copy', () => {
    const plan = planVerificationScope([{ status: 'R100', oldPath: 'backend/Services/Old.cs', path: 'src/utils/new.ts' }]);
    expect(plan.scope).toBe('both');
    expect(plan.changedPaths.map(entry => entry.path)).toEqual(['backend/Services/Old.cs', 'src/utils/new.ts']);
  });

  it('keeps deleted inputs in scope and combines frontend and backend changes', () => {
    expect(planVerificationScope([
      { status: 'D', path: 'backend/Services/Deleted.cs' }, 'src/components/Example.tsx',
    ]).scope).toBe('both');
  });

  it('runs documentation contracts even when tested Markdown was labelled untested prose', () => {
    const path = 'docs/batch-operations-audit-coverage-2026-06-10.md';
    const plan = planVerificationScope([path], { untestedDocs: [path] });
    expect(plan.scope).toBe('documentation');
    expect(plan.development.requiredChecks).toContain('documentation-contracts');
    expect(plan.development.requiredChecks).not.toContain('backend-full');
    expect(planVerificationScope(['README.md']).development.requiredChecks).toContain('frontend-process-contracts');
  });

  it('accepts only an exact reviewed prose allowance, not a wildcard or application file', () => {
    const path = 'docs/payplanner-handoff.md';
    expect(planVerificationScope([path], { untestedDocs: [path] }).scope).toBe('documentation');
    expect(planVerificationScope(['docs/other.md'], { untestedDocs: [path] }).scope).toBe('both');
    expect(() => planVerificationScope(['src/App.tsx'], { untestedDocs: ['src/App.tsx'] })).toThrow(/reviewed prose/);
    expect(() => planVerificationScope([path], { untestedDocs: ['docs/*.md'] })).toThrow(/exact reviewed prose/);
  });

  it('retains migration and restored-browser requirements for schema changes', () => {
    expect(planVerificationScope(['backend/PayPlanner.Api.Migrations/Migrations/New.cs']).development.requiredChecks)
      .toEqual(expect.arrayContaining(['backend-migrations', 'backend-restored-browser', 'frontend-integration']));
  });

  it('does not let a caller mutate the requirements of a later plan', () => {
    const first = planVerificationScope(['README.md']);
    first.release.requiredChecks.length = 0;
    first.changedPaths[0].checks.length = 0;
    first.checkDetails['frontend-unit'].commands.length = 0;
    const next = planVerificationScope(['README.md']);
    expect(next.release.requiredChecks).toContain('backend-full');
    expect(next.development.requiredChecks).toContain('documentation-contracts');
    expect(next.checkDetails['frontend-unit'].commands).toEqual(['npm run test:unit:after-guard']);
  });

  it('never uses a narrow local plan as permission to skip release gates', () => {
    const plans = [[], ['AGENTS.md'], ['src/components/Example.tsx'], ['backend/Services/Example.cs']]
      .map(paths => planVerificationScope(paths));
    for (const plan of plans) {
      expect(plan.release.ciSkippingAllowed).toBe(false);
      expect(plan.release.requiredChecks).toEqual(plans[0].release.requiredChecks);
      expect(plan.release.requiredChecks).toEqual(expect.arrayContaining(['frontend-unit', 'backend-full', 'backend-restored-browser', 'frontend-integration']));
      expect(plan.release.reuse).toContain('fingerprints');
      for (const id of plan.release.requiredChecks) {
        expect(plan.checkDetails[id].commands.length > 0 || Boolean(plan.checkDetails[id].instructions)).toBe(true);
      }
    }
    expect(plans[0].scope).toBe('none');
    expect(plans[0].development.requiredChecks).toEqual([]);
  });

  it.each(['../src/App.tsx', '/src/App.tsx', 'C:\\src\\App.tsx', 'docs/../src/App.tsx', 'src//App.tsx', '', 'src/a\nb.ts'])(
    'rejects ambiguous or escaping path %j', path => {
      expect(() => normalizeChangedPath(path)).toThrow();
    },
  );

  it('normalizes native separators while preserving exact filenames', () => {
    expect(normalizeChangedPath('.\\src\\components\\File with spaces.tsx')).toBe('src/components/File with spaces.tsx');
  });
});

describe('Git changes for the local plan', () => {
  it('parses NUL records including both rename and copy paths and deletions', () => {
    expect(parseNameStatus('R100\0old path.cs\0new path.ts\0C75\0source.cs\0copy.cs\0D\0deleted.cs\0'))
      .toEqual([
        { status: 'R100', oldPath: 'old path.cs', path: 'new path.ts' },
        { status: 'C75', oldPath: 'source.cs', path: 'copy.cs' },
        { status: 'D', path: 'deleted.cs' },
      ]);
    expect(() => parseNameStatus('R100\0old.cs\0')).toThrow(/destination/);
    expect(() => parseNameStatus('unexpected\0file.cs\0')).toThrow(/status/);
  });

  it('includes base-to-HEAD, staged renames, unstaged deletions and untracked files from a real repository', () => {
    const root = mkdtempSync(join(tmpdir(), 'payplanner-scope-'));
    const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true });
    const write = (path: string, content: string) => { mkdirSync(dirname(join(root, path)), { recursive: true }); writeFileSync(join(root, path), content); };
    try {
      git('init', '-q');
      write('backend/Services/Old.cs', 'baseline service');
      write('src/components/Button.tsx', 'baseline button');
      git('add', '.');
      git('-c', 'user.name=Scope Test', '-c', 'user.email=scope@example.invalid', 'commit', '-qm', 'baseline');
      const base = git('rev-parse', 'HEAD').trim();
      write('src/components/Button.tsx', 'committed button');
      git('add', '.');
      git('-c', 'user.name=Scope Test', '-c', 'user.email=scope@example.invalid', 'commit', '-qm', 'change');
      mkdirSync(join(root, 'src/utils'), { recursive: true });
      git('mv', 'backend/Services/Old.cs', 'src/utils/renamed.ts');
      rmSync(join(root, 'src/components/Button.tsx'));
      write('docs/untracked.md', 'untracked prose');
      const discovered = discoverChangedPaths(root, base);
      expect(discovered.baseRevision).toBe(base);
      expect(discovered.changes).toEqual(expect.arrayContaining([
        { status: 'M', path: 'src/components/Button.tsx' },
        { status: 'R100', oldPath: 'backend/Services/Old.cs', path: 'src/utils/renamed.ts' },
        { status: 'D', path: 'src/components/Button.tsx' },
        { status: '?', path: 'docs/untracked.md' },
      ]));
      expect(planVerificationScope(discovered.changes).scope).toBe('both');
      expect(() => discoverChangedPaths(root, 'HEAD;anything')).toThrow(/commit SHA/);
      expect(() => discoverChangedPaths(root, 'f'.repeat(40))).toThrow();
    } finally {
      // The only recursive removal is this exact newly-created test repository.
      expect(resolve(root).startsWith(resolve(tmpdir()) + '\\') || resolve(root).startsWith(resolve(tmpdir()) + '/')).toBe(true);
      rmSync(root, { recursive: true, force: true });
    }
  });
});
