import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');
const docsDirectory = join(repositoryRoot, 'docs');
const packagePath = join(docsDirectory, 'tz-gap-decision-package-2026-07-30.md');
const decisionPackage = readFileSync(packagePath, 'utf8');
const roadmapNames = readdirSync(docsDirectory)
  .filter((name) => /^tz-gap-.+-roadmap-2026-07-29\.md$/.test(name))
  .filter((name) => name !== 'tz-gap-roadmaps-index-2026-07-29.md')
  .sort();

const failures = [];
let roadmapDecisionTotal = 0;
let packageDecisionTotal = 0;

for (const roadmapName of roadmapNames) {
  const roadmap = readFileSync(join(docsDirectory, roadmapName), 'utf8');
  const decisionCount = Array.from(
    roadmap.matchAll(/^\s*-\s+`\[decision\]`/gm),
  ).length;
  roadmapDecisionTotal += decisionCount;

  const escapedName = roadmapName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const markerMatch = decisionPackage.match(
    new RegExp(`<!--\\s*decisions:\\s*${escapedName}\\s+count=(\\d+)\\s*-->`),
  );
  if (!markerMatch) {
    failures.push(`В пакете нет маркера ${roadmapName}.`);
    continue;
  }

  const declaredCount = Number(markerMatch[1]);
  packageDecisionTotal += declaredCount;
  if (declaredCount !== decisionCount) {
    failures.push(
      `${roadmapName}: в roadmap ${decisionCount} решений, в пакете указано ${declaredCount}.`,
    );
  }
}

const packageMarkers = Array.from(
  decisionPackage.matchAll(/<!--\s*decisions:\s*([^\s]+)\s+count=(\d+)\s*-->/g),
);
if (packageMarkers.length !== roadmapNames.length) {
  failures.push(
    `Ожидалось ${roadmapNames.length} маркеров roadmap, найдено ${packageMarkers.length}.`,
  );
}
if (roadmapDecisionTotal !== packageDecisionTotal) {
  failures.push(
    `Общий итог расходится: roadmap ${roadmapDecisionTotal}, пакет ${packageDecisionTotal}.`,
  );
}

if (failures.length > 0) {
  console.error('Пакет решений не соответствует активным roadmap:');
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exitCode = 1;
} else {
  console.log(
    `Пакет актуален: ${roadmapNames.length} roadmap, ${roadmapDecisionTotal} решений.`,
  );
}
