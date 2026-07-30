import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');
const docsDirectory = join(repositoryRoot, 'docs');
const roadmapNames = readdirSync(docsDirectory)
  .filter((name) => /^tz-(?:gap|crm)-.+-roadmap-\d{4}-\d{2}-\d{2}\.md$/.test(name))
  .filter((name) => name !== 'tz-gap-roadmaps-index-2026-07-29.md')
  .sort();

const markerPatterns = {
  completed: /^\s*-\s+`?\[x\]`?/gim,
  inProgress: /^\s*-\s+`?\[~\]`?/gim,
  notStarted: /^\s*-\s+`?\[ \]`?/gim,
  blocked: /^\s*-\s+`?\[!\]`?/gim,
  decision: /^\s*-\s+`?\[decision\]`?/gim,
  acceptance: /^\s*-\s+`?\[acceptance\]`?/gim,
};

const summaryPatterns = {
  completed: /^-\s+Закрыто и проверено:\s*(\d+)\./m,
  inProgress: /^-\s+В работе:\s*(\d+)\./m,
  notStarted: /^-\s+Не начато:\s*(\d+)\./m,
  blocked: /^-\s+Заблокировано[^:]*:\s*(\d+)\./m,
  decision: /^-\s+Требует решения заказчика:\s*(\d+)\./m,
  acceptance: /^-\s+Требует ручной при[её]мки:\s*(\d+)\./mi,
};

const failures = [];
let totalItems = 0;
let totalCompleted = 0;

for (const roadmapName of roadmapNames) {
  const roadmap = readFileSync(join(docsDirectory, roadmapName), 'utf8');
  const counts = Object.fromEntries(
    Object.entries(markerPatterns).map(([key, pattern]) => [
      key,
      Array.from(roadmap.matchAll(pattern)).length,
    ]),
  );
  const itemCount = Object.values(counts).reduce((sum, count) => sum + count, 0);
  const progressPercent =
    itemCount === 0 ? 0 : Math.round((counts.completed / itemCount) * 100);
  totalItems += itemCount;
  totalCompleted += counts.completed;

  const progressMatch = roadmap.match(
    /Прогресс:\s*(\d+)\s+из\s+(\d+)\s+рабоч\S*\s+пункт\S*\s+закрыто,\s*(\d+)%\./i,
  );
  if (!progressMatch) {
    failures.push(`${roadmapName}: не найдена строка общего прогресса.`);
  } else {
    const [reportedCompleted, reportedTotal, reportedPercent] = progressMatch
      .slice(1)
      .map(Number);
    if (
      reportedCompleted !== counts.completed ||
      reportedTotal !== itemCount ||
      reportedPercent !== progressPercent
    ) {
      failures.push(
        `${roadmapName}: прогресс ${reportedCompleted}/${reportedTotal} (${reportedPercent}%), фактически ${counts.completed}/${itemCount} (${progressPercent}%).`,
      );
    }
  }

  const totalMatch = roadmap.match(/^-\s+Всего рабочих пунктов:\s*(\d+)\./m);
  if (!totalMatch || Number(totalMatch[1]) !== itemCount) {
    failures.push(
      `${roadmapName}: итог в сводке ${totalMatch?.[1] ?? 'не указан'}, фактически ${itemCount}.`,
    );
  }

  for (const [key, pattern] of Object.entries(summaryPatterns)) {
    const summaryMatch = roadmap.match(pattern);
    if (summaryMatch && Number(summaryMatch[1]) !== counts[key]) {
      failures.push(
        `${roadmapName}: ${key} в сводке ${summaryMatch[1]}, фактически ${counts[key]}.`,
      );
    } else if (!summaryMatch && counts[key] > 0) {
      failures.push(
        `${roadmapName}: в сводке отсутствует ${key}, фактически ${counts[key]}.`,
      );
    }
  }
}

if (failures.length > 0) {
  console.error('Сводки активных roadmap не соответствуют рабочим пунктам:');
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exitCode = 1;
} else {
  const completionPercent =
    totalItems === 0 ? 0 : Math.round((totalCompleted / totalItems) * 100);
  console.log(
    `Сводки актуальны: ${roadmapNames.length} roadmap, выполнено ${totalCompleted} из ${totalItems} (${completionPercent}%).`,
  );
}
