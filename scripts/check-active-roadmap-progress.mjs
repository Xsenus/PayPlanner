import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const markerPatterns = {
  completed: /^\s*-\s+`?\[x\]`?/gim,
  inProgress: /^\s*-\s+`?\[~\]`?/gim,
  notStarted: /^\s*-\s+`?\[ \]`?/gim,
  blocked: /^\s*-\s+`?\[!\]`?/gim,
  decision: /^\s*-\s+`?\[decision\]`?/gim,
  acceptance: /^\s*-\s+`?\[acceptance\]`?/gim,
};
const legacySummaryPatterns = {
  completed: /^-\s+Закрыто и проверено:\s*(\d+)\./m,
  inProgress: /^-\s+В работе:\s*(\d+)\./m,
  notStarted: /^-\s+Не начато:\s*(\d+)\./m,
  blocked: /^-\s+Заблокировано[^:]*:\s*(\d+)\./m,
  decision: /^-\s+Требует решения заказчика:\s*(\d+)\./m,
  acceptance: /^-\s+Требует ручной при[её]мки:\s*(\d+)\./mi,
};
const unifiedSummaryPatterns = {
  completed: /завершено\s+(\d+)/i,
  inProgress: /в работе\s+(\d+)/i,
  notStarted: /не начато\s+(\d+)/i,
  blocked: /заблокировано\s+(\d+)/i,
  decision: /решений\s+(\d+)/i,
  acceptance: /при[её]мки\s+(\d+)/i,
};
export function isActiveRoadmapName(name) {
  return /^tz-(?:gap|crm)-.+-roadmap-\d{4}-\d{2}-\d{2}\.md$/.test(name)
    || /^payplanner-(?!unified-).+-roadmap-\d{4}-\d{2}-\d{2}\.md$/.test(name);
}
export function checkRoadmapProgress(name, roadmap) {
  const failures = [];
  const counts = Object.fromEntries(Object.entries(markerPatterns).map(([key, pattern]) => [key, Array.from(roadmap.matchAll(pattern)).length]));
  const total = Object.values(counts).reduce((sum, count) => sum + count, 0);
  const unified = name.startsWith('payplanner-');
  const percent = total === 0 ? 0 : unified ? Number((counts.completed / total * 100).toFixed(1)) : Math.round(counts.completed / total * 100);
  const summaryText = unified ? roadmap.split('\n').find(line => /\*\*Прогресс:/.test(line)) ?? '' : roadmap;
  const progressMatch = summaryText.match(unified
    ? /Прогресс:\s*(\d+)\s*\/\s*(\d+)\s*\(([\d.,]+)%\)/i
    : /Прогресс:\s*(\d+)\s+из\s+(\d+)\s+рабоч\S*\s+пункт\S*\s+закрыто,\s*(\d+)%\./i);
  if (!progressMatch) failures.push(`${name}: не найдена строка общего прогресса.`);
  else {
    const [reportedCompleted, reportedTotal, reportedPercent] = progressMatch.slice(1).map(value => Number(value.replace(',', '.')));
    if (reportedCompleted !== counts.completed || reportedTotal !== total || reportedPercent !== percent) {
      failures.push(`${name}: прогресс ${reportedCompleted}/${reportedTotal} (${reportedPercent}%), фактически ${counts.completed}/${total} (${percent}%).`);
    }
  }
  const totalMatch = summaryText.match(unified ? /всего\s+(\d+)\s+пакет/i : /^-\s+Всего рабочих пунктов:\s*(\d+)\./m);
  if (!totalMatch || Number(totalMatch[1]) !== total) failures.push(`${name}: итог в сводке ${totalMatch?.[1] ?? 'не указан'}, фактически ${total}.`);
  const summaryPatterns = unified ? unifiedSummaryPatterns : legacySummaryPatterns;
  for (const [key, pattern] of Object.entries(summaryPatterns)) {
    const match = summaryText.match(pattern);
    if (match && Number(match[1]) !== counts[key]) failures.push(`${name}: ${key} в сводке ${match[1]}, фактически ${counts[key]}.`);
    else if (!match && (unified || counts[key] > 0)) failures.push(`${name}: в сводке отсутствует ${key}, фактически ${counts[key]}.`);
  }
  return { failures, counts, total, percent };
}
function run() {
  const docs = join(resolve(import.meta.dirname, '..'), 'docs');
  const names = readdirSync(docs).filter(isActiveRoadmapName).sort();
  const failures = [];
  let total = 0;
  let completed = 0;
  if (names.length === 0) failures.push('Не найдено ни одного активного roadmap непосредственно в docs/.');
  for (const name of names) {
    const result = checkRoadmapProgress(name, readFileSync(join(docs, name), 'utf8'));
    failures.push(...result.failures);
    total += result.total;
    completed += result.counts.completed;
  }
  if (failures.length) {
    console.error('Сводки активных roadmap не соответствуют рабочим пунктам:');
    failures.forEach(failure => console.error(`- ${failure}`));
    process.exitCode = 1;
  } else {
    const percent = total === 0 ? 0 : Number((completed / total * 100).toFixed(1));
    console.log(`Сводки актуальны: ${names.length} roadmap, выполнено ${completed} из ${total} (${percent}%).`);
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) run();
