import assert from 'node:assert/strict';
import { test } from 'node:test';
import { checkRoadmapProgress, isActiveRoadmapName } from './check-active-roadmap-progress.mjs';

const name = 'payplanner-example-roadmap-2026-10-14.md';
const source = '**Прогресс: 1/6 (16.7%)**: всего 6 пакетов, завершено 1, в работе 1, не начато 1, заблокировано 1, решений 1, отдельной приёмки 1.\n- [x] готово\n- [~] работа\n- [ ] новое\n- [!] блокер\n- [decision] решение\n- [acceptance] приёмка\n';

test('discovers both active families while excluding indexes and archive paths', () => {
  assert.equal(isActiveRoadmapName(name), true);
  assert.equal(isActiveRoadmapName('tz-crm-example-roadmap-2026-07-29.md'), true);
  assert.equal(isActiveRoadmapName('payplanner-unified-roadmap-2026-10-14.md'), false);
  assert.equal(isActiveRoadmapName('archive/roadmaps/' + name), false);
  assert.equal(isActiveRoadmapName('max-today-reply-drafts-2026-10-14.md'), false);
});
test('checks all six unified statuses and decimal progress', () => {
  const result = checkRoadmapProgress(name, source);
  assert.deepEqual(result.failures, []);
  assert.equal(result.total, 6);
  assert.equal(result.percent, 16.7);
});
test('accepts comma decimals and the usual acceptance label', () => {
  assert.deepEqual(checkRoadmapProgress(name, source.replace('16.7', '16,7').replace('отдельной приёмки', 'приёмки')).failures, []);
});
test('rejects stale status counts despite correct aggregate progress', () => {
  assert.ok(checkRoadmapProgress(name, source.replace('в работе 1', 'в работе 0')).failures.some(value => value.includes('inProgress')));
});
test('rejects incorrect completion percentage and total', () => {
  assert.ok(checkRoadmapProgress(name, source.replace('1/6 (16.7%)', '1/5 (20%)')).failures.some(value => value.includes('прогресс')));
  assert.ok(checkRoadmapProgress(name, source.replace('всего 6', 'всего 7')).failures.some(value => value.includes('итог')));
});
test('requires unified zero counts too', () => {
  const zero = '**Прогресс: 0/1 (0%)**: всего 1 пакет, завершено 0, в работе 0, не начато 1, заблокировано 0, решений 0, приёмки 0.\n- [ ] пункт';
  assert.deepEqual(checkRoadmapProgress(name, zero).failures, []);
  assert.ok(checkRoadmapProgress(name, zero.replace(', решений 0', '')).failures.some(value => value.includes('decision')));
});
test('retains legacy summaries and optional zero status fields', () => {
  const old = 'Прогресс: 1 из 2 рабочих пунктов закрыто, 50%.\n- Всего рабочих пунктов: 2.\n- Закрыто и проверено: 1.\n- В работе: 1.\n- [x] один\n- [~] два';
  assert.deepEqual(checkRoadmapProgress('tz-crm-example-roadmap-2026-07-29.md', old).failures, []);
  assert.ok(checkRoadmapProgress('tz-crm-example-roadmap-2026-07-29.md', old.replace('В работе: 1', 'В работе: 0')).failures.length);
});
test('does not read missing counts from explanatory text outside the summary', () => {
  const invalid = source.replace(', решений 1', '') + '\nОбъяснение: решений 1.';
  assert.ok(checkRoadmapProgress(name, invalid).failures.some(value => value.includes('decision')));
});
