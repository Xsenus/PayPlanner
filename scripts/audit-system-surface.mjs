import { promises as fs } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const root = process.cwd();
const outputPath = path.join(root, 'docs', 'system-surface-inventory.generated.md');

async function listFiles(directory, predicate) {
  const result = [];
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules', 'dist'].includes(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(fullPath, predicate));
    else if (predicate(fullPath)) result.push(fullPath);
  }
  return result.sort((left, right) => left.localeCompare(right, 'en'));
}

function relative(file) {
  return path.relative(root, file).replaceAll('\\', '/');
}

function lineAt(source, index) {
  return source.slice(0, index).split('\n').length;
}

export function parseController(source, file) {
  const classMatch = source.match(/public\s+(?:sealed\s+)?class\s+(\w+Controller)\b/);
  const controller = classMatch?.[1]
    ?? path.basename(file, '.cs');
  const classRoute = source.match(/\[Route\("([^"]+)"\)\]/)?.[1] ?? '';
  const classHeader = source.slice(0, classMatch?.index ?? 0);
  const classAuthorized = /\[Authorize(?:\([^\]]*\))?\]/.test(classHeader);
  const classPermissions = extractAccessAttributes(classHeader);
  const endpoints = [];
  const httpAttribute = /\[Http(Get|Post|Put|Patch|Delete)(?:\("([^"]*)"\))?\]/g;
  for (const match of source.matchAll(httpAttribute)) {
    const index = match.index ?? 0;
    const tail = source.slice(index + match[0].length, index + match[0].length + 900);
    const method = tail.match(/public\s+(?:async\s+)?[\w<>,.?\[\]\s]+?\s+(\w+)\s*\(/)?.[1] ?? 'не распознано';
    const prefix = source.slice(Math.max(0, index - 1_200), index);
    const memberBoundary = Math.max(prefix.lastIndexOf('{'), prefix.lastIndexOf('}'), prefix.lastIndexOf(';'));
    const methodHeader = prefix.slice(memberBoundary + 1);
    const allowAnonymous = /\[AllowAnonymous\]/.test(methodHeader);
    const methodAuthorized = /\[Authorize(?:\([^\]]*\))?\]/.test(methodHeader);
    const access = allowAnonymous
      ? 'публичный ([AllowAnonymous])'
      : [...classPermissions, ...extractAccessAttributes(methodHeader)].join('; ') || (classAuthorized || methodAuthorized ? '[Authorize]' : 'не задано');
    endpoints.push({
      controller,
      method,
      verb: match[1].toUpperCase(),
      route: [classRoute, match[2] ?? ''].filter(Boolean).join('/'),
      line: lineAt(source, index),
      authorized: !allowAnonymous && (classAuthorized || methodAuthorized),
      access,
    });
  }
  return { controller, classRoute, authorized: classAuthorized, access: classPermissions.join('; ') || (classAuthorized ? '[Authorize]' : 'не задано'), endpoints };
}

/**
 * Возвращает только атрибуты, влияющие на доступ к контроллеру или действию.
 * Полный текст аргументов сохраняется, чтобы отчёт позволял проверить секцию,
 * уровень операции, административную политику и роли без ручного поиска в коде.
 */
function extractAccessAttributes(source) {
  return [...source.matchAll(/\[((?:Authorize|RequireSectionPermission)(?:\([^\]]*\))?)\]/g)]
    .map(match => `[${match[1].replaceAll(/\s+/g, ' ').trim()}]`);
}

export function parseForms(source, file) {
  return [...source.matchAll(/<form\b/g)].map(match => ({
    component: path.basename(file, path.extname(file)),
    line: lineAt(source, match.index ?? 0),
  }));
}

export function parseTabs(source) {
  const block = source.match(/export const TAB_ACCESS_RULES\s*=\s*\{([\s\S]*?)\}\s*satisfies/)?.[1] ?? '';
  return [...block.matchAll(/^\s*(\w+):/gm)].map(match => match[1]);
}

export function parseBackgroundServices(source, file) {
  return [...source.matchAll(/(?:public\s+)?(?:sealed\s+)?class\s+(\w+)(?:\s*\([^)]*\))?\s*:\s*BackgroundService\b/g)]
    .map(match => ({ name: match[1], line: lineAt(source, match.index ?? 0), file: relative(file) }));
}

function escapeCell(value) {
  return String(value ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ');
}

function table(headers, rows) {
  return [
    `| ${headers.join(' | ')} |`,
    `| ${headers.map(() => '---').join(' | ')} |`,
    ...rows.map(row => `| ${row.map(escapeCell).join(' | ')} |`),
  ].join('\n');
}

async function buildInventory() {
  const controllerFiles = await listFiles(path.join(root, 'backend', 'Controllers'), file => file.endsWith('.cs'));
  const backendTestFiles = await listFiles(path.join(root, 'backend', 'PayPlanner.Api.Tests'), file => file.endsWith('.cs'));
  const frontendFiles = await listFiles(path.join(root, 'src'), file => file.endsWith('.tsx') && !file.includes('.test.'));
  const frontendTestFiles = await listFiles(path.join(root, 'src'), file => /\.test\.tsx?$/.test(file));
  const serviceFiles = await listFiles(path.join(root, 'backend', 'Services'), file => file.endsWith('.cs'));
  const backendTests = (await Promise.all(backendTestFiles.map(file => fs.readFile(file, 'utf8')))).join('\n');
  const frontendTests = (await Promise.all(frontendTestFiles.map(file => fs.readFile(file, 'utf8')))).join('\n');

  const controllers = [];
  for (const file of controllerFiles) {
    const parsed = parseController(await fs.readFile(file, 'utf8'), file);
    controllers.push({
      ...parsed,
      file: relative(file),
      tested: backendTests.includes(parsed.controller),
    });
  }
  const endpoints = controllers.flatMap(controller => controller.endpoints.map(endpoint => ({
    ...endpoint,
    file: controller.file,
    tested: controller.tested,
  })));

  const forms = [];
  for (const file of frontendFiles) {
    const parsed = parseForms(await fs.readFile(file, 'utf8'), file);
    forms.push(...parsed.map(form => ({
      ...form,
      file: relative(file),
      tested: frontendTests.includes(form.component),
    })));
  }

  const backgroundServices = [];
  for (const file of serviceFiles) {
    const source = await fs.readFile(file, 'utf8');
    backgroundServices.push(...parseBackgroundServices(source, file).map(service => ({
      ...service,
      tested: backendTests.includes(service.name),
    })));
  }
  const tabs = parseTabs(await fs.readFile(path.join(root, 'src', 'appRouting.ts'), 'utf8'));
  return { controllers, endpoints, forms, backgroundServices, tabs };
}

function render(inventory) {
  const testedEndpoints = inventory.endpoints.filter(item => item.tested).length;
  const testedForms = inventory.forms.filter(item => item.tested).length;
  const testedServices = inventory.backgroundServices.filter(item => item.tested).length;
  return `# Автоматическая инвентаризация поверхностей PayPlanner

Файл формируется командой \`npm run audit:surface:update\`. Ручное редактирование не допускается.
Признак теста означает, что имя контроллера, компонента или фонового сервиса найдено в тестовом контуре; детальная полнота сценариев проверяется последующими этапами roadmap.

## Сводка

- Разделов frontend: ${inventory.tabs.length}.
- Backend-контроллеров: ${inventory.controllers.length}.
- HTTP endpoints: ${inventory.endpoints.length}; с найденной тестовой связью: ${testedEndpoints}.
- Реальных HTML-форм: ${inventory.forms.length}; с найденной тестовой связью: ${testedForms}.
- Фоновых сервисов: ${inventory.backgroundServices.length}; с найденной тестовой связью: ${testedServices}.

## Разделы frontend

${table(['Раздел', 'Правило доступа'], inventory.tabs.map(tab => [tab, 'TAB_ACCESS_RULES']))}

## Формы

${table(['Компонент', 'Файл', 'Строка', 'Тест найден'], inventory.forms.map(item => [item.component, item.file, item.line, item.tested ? 'да' : 'нет']))}

## Фоновые сервисы

${table(['Сервис', 'Файл', 'Строка', 'Тест найден'], inventory.backgroundServices.map(item => [item.name, item.file, item.line, item.tested ? 'да' : 'нет']))}

## Контроллеры

${table(['Контроллер', 'Файл', 'Endpoints', 'Правило доступа', 'Тест найден'], inventory.controllers.map(item => [item.controller, item.file, item.endpoints.length, item.access, item.tested ? 'да' : 'нет']))}

## Все HTTP endpoints

${table(['Метод', 'Маршрут', 'Контроллер', 'Действие', 'Файл:строка', 'Правило доступа', 'Тест контроллера'], inventory.endpoints.map(item => [item.verb, item.route, item.controller, item.method, `${item.file}:${item.line}`, item.access, item.tested ? 'да' : 'нет']))}
`;
}

function selfTest() {
  const controller = parseController('[Authorize]\n[Route("api/items")]\npublic class ItemsController {\n[HttpGet("{id}")]\npublic async Task<IActionResult> Get(int id) => null;\n}', 'ItemsController.cs');
  if (controller.endpoints.length !== 1 || controller.endpoints[0].method !== 'Get' || controller.endpoints[0].route !== 'api/items/{id}') throw new Error('Не удалось разобрать endpoint.');
  if (!controller.endpoints[0].authorized || controller.endpoints[0].access !== '[Authorize]') throw new Error('Не удалось разобрать авторизацию endpoint.');
  const publicController = parseController('[Authorize]\n[Route("api/public")]\npublic class PublicController {\n[AllowAnonymous]\n[HttpGet]\npublic IActionResult Get() => null;\n}', 'PublicController.cs');
  if (publicController.endpoints[0].authorized || !publicController.endpoints[0].access.startsWith('публичный')) throw new Error('Не удалось распознать AllowAnonymous.');
  if (parseForms('a\n<form onSubmit={save}>', 'Editor.tsx')[0]?.line !== 2) throw new Error('Не удалось разобрать форму.');
  if (parseTabs('export const TAB_ACCESS_RULES = {\n clients: () => true,\n} satisfies Record<string, unknown>;').join() !== 'clients') throw new Error('Не удалось разобрать разделы.');
  const backgroundServices = [
    ...parseBackgroundServices('public sealed class RegularWorker : BackgroundService {}', 'RegularWorker.cs'),
    ...parseBackgroundServices('public sealed class PrimaryWorker(ILogger logger) : BackgroundService {}', 'PrimaryWorker.cs'),
  ];
  if (backgroundServices.map(item => item.name).join() !== 'RegularWorker,PrimaryWorker') throw new Error('Не удалось разобрать фоновые сервисы.');
  console.log('Самопроверка инвентаризации пройдена.');
}

if (process.argv.includes('--self-test')) {
  selfTest();
} else {
  const content = render(await buildInventory());
  if (process.argv.includes('--write')) {
    await fs.writeFile(outputPath, content, 'utf8');
    console.log(`Инвентаризация обновлена: ${relative(outputPath)}`);
  } else {
    const current = await fs.readFile(outputPath, 'utf8').catch(() => '');
    if (current !== content) {
      console.error('Инвентаризация устарела. Выполните npm run audit:surface:update.');
      process.exitCode = 1;
    } else {
      console.log('Инвентаризация актуальна.');
    }
  }
}
