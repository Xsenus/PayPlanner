#!/usr/bin/env node
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const DEFAULT_ROADMAP = 'docs/archive/roadmaps/unallocated-advance-monitoring-roadmap-2026-06-12.md';

if (isCliEntryPoint()) {
  runCli();
}

function runCli() {
  const args = process.argv.slice(2);

  if (args.includes('--help')) {
    printHelp();
    process.exit(0);
  }

  if (args.includes('--self-test')) {
    runSelfTest();
    process.exit(0);
  }

  const roadmapArg = args.find((arg) => !arg.startsWith('--')) ?? DEFAULT_ROADMAP;
  const roadmapPath = path.resolve(process.cwd(), roadmapArg);
  const failOnOpen = args.includes('--fail-on-open');
  const failOnBlocked = args.includes('--fail-on-blocked');
  const failOnMissingChecklist = args.includes('--fail-on-missing-checklist');
  const report = buildRoadmapReport(readFileSync(roadmapPath, 'utf8'), roadmapPath);

  printReport(report);

  if (failOnOpen && report.implementationOpenCount > 0) {
    console.error(`Open implementation roadmap items found: ${report.implementationOpenCount}`);
    process.exitCode = 1;
  }

  if (failOnBlocked && report.blockedCount > 0) {
    console.error(`Blocked roadmap items found: ${report.blockedCount}`);
    process.exitCode = 1;
  }

  if (failOnMissingChecklist && !report.acceptanceChecklist.exists) {
    console.error('Acceptance checklist is missing or not linked.');
    process.exitCode = 1;
  }
}

function isCliEntryPoint() {
  return Boolean(process.argv[1]) && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
}

export function buildRoadmapReport(content, source = DEFAULT_ROADMAP) {
  const items = parseRoadmapItems(content);
  const acceptanceChecklist = parseAcceptanceChecklist(content, source);
  const counts = {
    done: 0,
    notStarted: 0,
    inProgress: 0,
    blocked: 0,
    decision: 0,
    acceptance: 0,
  };

  for (const item of items) {
    counts[item.status] += 1;
  }

  const implementationTotal = items.length - counts.acceptance - counts.blocked;
  const implementationDone = counts.done;
  const implementationOpenCount = counts.notStarted + counts.inProgress + counts.decision;
  const implementationPercent = implementationTotal > 0
    ? Math.round((implementationDone / implementationTotal) * 1000) / 10
    : 100;

  return {
    source,
    total: items.length,
    ...counts,
    blockedCount: counts.blocked,
    implementationTotal,
    implementationDone,
    implementationOpenCount,
    implementationPercent,
    blockedItems: items.filter((item) => item.status === 'blocked'),
    acceptanceItems: items.filter((item) => item.status === 'acceptance'),
    acceptanceChecklist,
    openImplementationItems: items.filter((item) => (
      item.status === 'notStarted' ||
      item.status === 'inProgress' ||
      item.status === 'decision'
    )),
  };
}

function parseAcceptanceChecklist(content, source) {
  const match = content.match(/Чек-лист ручного показа:\s+`([^`]+)`/);
  const checklistPath = match?.[1] ?? null;

  if (!checklistPath) {
    return {
      path: null,
      exists: false,
    };
  }

  const baseDir = path.dirname(path.resolve(source));
  const rootRelativePath = path.resolve(process.cwd(), checklistPath);
  const roadmapRelativePath = path.resolve(baseDir, checklistPath);
  const absolutePath = existsSync(rootRelativePath) ? rootRelativePath : roadmapRelativePath;

  return {
    path: checklistPath,
    absolutePath,
    exists: existsSync(absolutePath),
  };
}

function parseRoadmapItems(content) {
  const statusMap = {
    x: 'done',
    ' ': 'notStarted',
    '~': 'inProgress',
    '!': 'blocked',
    decision: 'decision',
    acceptance: 'acceptance',
  };

  return content
    .split(/\r?\n/)
    .map((line, index) => {
      const match = line.match(/^- \[(x| |~|!|decision|acceptance)\]\s+(.+)$/);
      if (!match) {
        return null;
      }

      return {
        line: index + 1,
        status: statusMap[match[1]],
        text: match[2].trim(),
      };
    })
    .filter(Boolean);
}

function printReport(report) {
  console.log(`Advance roadmap: ${report.source}`);
  console.log(`Implementation: ${report.implementationPercent}% (${report.implementationDone}/${report.implementationTotal})`);
  console.log(`Items total: ${report.total}`);
  console.log(`Done: ${report.done}`);
  console.log(`Open implementation: ${report.implementationOpenCount}`);
  console.log(`Blocked: ${report.blockedCount}`);
  console.log(`Manual acceptance: ${report.acceptance}`);
  console.log(`Acceptance checklist: ${report.acceptanceChecklist.path ?? 'not linked'} (${report.acceptanceChecklist.exists ? 'found' : 'missing'})`);

  if (report.openImplementationItems.length > 0) {
    console.log('\nOpen implementation items:');
    for (const item of report.openImplementationItems) {
      console.log(`- line ${item.line}: ${item.text}`);
    }
  }

  if (report.blockedItems.length > 0) {
    console.log('\nBlocked items:');
    for (const item of report.blockedItems) {
      console.log(`- line ${item.line}: ${item.text}`);
    }
  }

  if (report.acceptanceItems.length > 0) {
    console.log('\nManual acceptance items:');
    for (const item of report.acceptanceItems) {
      console.log(`- line ${item.line}: ${item.text}`);
    }
  }
}

function runSelfTest() {
  const report = buildRoadmapReport([
    '- [x] Done',
    '- [ ] Not started',
    '- [~] In progress',
    '- [!] Blocked',
    '- [decision] Decision',
    '- [acceptance] Acceptance',
    '',
    'Чек-лист ручного показа: `package.json`.',
  ].join('\n'));

  assertEqual(report.total, 6, 'total');
  assertEqual(report.done, 1, 'done');
  assertEqual(report.notStarted, 1, 'notStarted');
  assertEqual(report.inProgress, 1, 'inProgress');
  assertEqual(report.blockedCount, 1, 'blockedCount');
  assertEqual(report.decision, 1, 'decision');
  assertEqual(report.acceptance, 1, 'acceptance');
  assertEqual(report.implementationTotal, 4, 'implementationTotal');
  assertEqual(report.implementationOpenCount, 3, 'implementationOpenCount');
  assertEqual(report.implementationPercent, 25, 'implementationPercent');
  assertEqual(report.acceptanceChecklist.path, 'package.json', 'acceptanceChecklist.path');
  assertEqual(report.acceptanceChecklist.exists, true, 'acceptanceChecklist.exists');

  console.log('Roadmap status self-test passed.');
}

function assertEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${expected}, got ${actual}`);
  }
}

function printHelp() {
  console.log(`Usage: node scripts/check-advance-roadmap-status.mjs [roadmap.md] [options]

Options:
  --fail-on-open     Exit with code 1 when [ ], [~], or [decision] implementation items exist.
  --fail-on-blocked  Exit with code 1 when [!] blocked items exist.
  --fail-on-missing-checklist
                     Exit with code 1 when the manual acceptance checklist is missing.
  --self-test        Run parser self-test.
  --help             Show this help.
`);
}
