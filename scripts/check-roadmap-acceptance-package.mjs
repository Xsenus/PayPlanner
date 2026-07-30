import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');
const docsDirectory = join(repositoryRoot, 'docs');
const packagePath = join(
  docsDirectory,
  'tz-roadmap-acceptance-package-2026-07-30.md',
);
const acceptancePackage = readFileSync(packagePath, 'utf8');
const roadmapNames = readdirSync(docsDirectory)
  .filter((name) => /^tz-(?:gap|crm)-.+-roadmap-\d{4}-\d{2}-\d{2}\.md$/.test(name))
  .filter((name) => name !== 'tz-gap-roadmaps-index-2026-07-29.md')
  .sort();

const failures = [];
let roadmapAcceptanceTotal = 0;
let packageAcceptanceTotal = 0;

for (const roadmapName of roadmapNames) {
  const roadmap = readFileSync(join(docsDirectory, roadmapName), 'utf8');
  const acceptanceCount = Array.from(
    roadmap.matchAll(/^\s*-\s+`\[acceptance\]`/gm),
  ).length;
  roadmapAcceptanceTotal += acceptanceCount;

  const escapedName = roadmapName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const markerMatch = acceptancePackage.match(
    new RegExp(`<!--\\s*acceptance:\\s*${escapedName}\\s+count=(\\d+)\\s*-->`),
  );
  if (!markerMatch) {
    failures.push(`В пакете нет маркера ${roadmapName}.`);
    continue;
  }

  const declaredCount = Number(markerMatch[1]);
  packageAcceptanceTotal += declaredCount;
  if (declaredCount !== acceptanceCount) {
    failures.push(
      `${roadmapName}: в roadmap ${acceptanceCount} приёмок, в пакете указано ${declaredCount}.`,
    );
  }
}

const packageMarkers = Array.from(
  acceptancePackage.matchAll(
    /<!--\s*acceptance:\s*([^\s]+)\s+count=(\d+)\s*-->/g,
  ),
);
if (packageMarkers.length !== roadmapNames.length) {
  failures.push(
    `Ожидалось ${roadmapNames.length} маркеров roadmap, найдено ${packageMarkers.length}.`,
  );
}
if (roadmapAcceptanceTotal !== packageAcceptanceTotal) {
  failures.push(
    `Общий итог расходится: roadmap ${roadmapAcceptanceTotal}, пакет ${packageAcceptanceTotal}.`,
  );
}

if (failures.length > 0) {
  console.error('Пакет приёмки не соответствует активным roadmap:');
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exitCode = 1;
} else {
  console.log(
    `Пакет актуален: ${roadmapNames.length} roadmap, ${roadmapAcceptanceTotal} пунктов приёмки.`,
  );
}
