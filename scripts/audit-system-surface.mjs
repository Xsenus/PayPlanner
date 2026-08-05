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

function parseControllerBlock(source, file, classMatch, blockEnd, previousBlockEnd) {
  const controller = classMatch?.[1] ?? path.basename(file, '.cs');
  const rawHeader = source.slice(previousBlockEnd, classMatch?.index ?? 0);
  const headerBoundary = Math.max(rawHeader.lastIndexOf('}'), rawHeader.lastIndexOf(';'));
  const classHeader = rawHeader.slice(headerBoundary + 1);
  const classRoute = classHeader.match(/\[Route\("([^"]+)"\)\]/)?.[1] ?? '';
  const classAuthorized = /\[Authorize(?:\([^\]]*\))?\]/.test(classHeader);
  const classAnonymous = /\[AllowAnonymous\]/.test(classHeader);
  const classPermissions = extractAccessAttributes(classHeader);
  const endpoints = [];
  const httpAttribute = /\[Http(Get|Post|Put|Patch|Delete)(?:\("([^"]*)"\))?\]/g;
  const blockStart = classMatch?.index ?? 0;
  const block = source.slice(blockStart, blockEnd);
  for (const match of block.matchAll(httpAttribute)) {
    const localIndex = match.index ?? 0;
    const index = blockStart + localIndex;
    const tail = block.slice(localIndex + match[0].length, localIndex + match[0].length + 900);
    const method = tail.match(/public\s+(?:async\s+)?[\w<>,.?\[\]\s]+?\s+(\w+)\s*\(/)?.[1] ?? 'не распознано';
    const prefix = block.slice(Math.max(0, localIndex - 1_200), localIndex);
    const memberBoundary = Math.max(prefix.lastIndexOf('{'), prefix.lastIndexOf('}'), prefix.lastIndexOf(';'));
    const nextDeclaration = tail.search(/\bpublic\s/);
    const attributesAfterHttp = nextDeclaration >= 0 ? tail.slice(0, nextDeclaration) : '';
    const methodHeader = `${prefix.slice(memberBoundary + 1)}\n${attributesAfterHttp}`;
    const allowAnonymous = classAnonymous || /\[AllowAnonymous\]/.test(methodHeader);
    const methodAuthorized = /\[Authorize(?:\([^\]]*\))?\]/.test(methodHeader);
    const access = allowAnonymous
      ? 'публичный ([AllowAnonymous])'
      : [...classPermissions, ...extractAccessAttributes(methodHeader)].join('; ') || (classAuthorized || methodAuthorized ? '[Authorize]' : 'не задано');
    const methodRoute = match[2] ?? '';
    const route = methodRoute.startsWith('/')
      ? methodRoute.slice(1)
      : [classRoute, methodRoute].filter(Boolean).join('/');
    endpoints.push({
      controller,
      method,
      verb: match[1].toUpperCase(),
      route,
      line: lineAt(source, index),
      authorized: !allowAnonymous && (classAuthorized || methodAuthorized),
      access,
    });
  }
  return {
    controller,
    classRoute,
    authorized: classAuthorized && !classAnonymous,
    access: classAnonymous ? 'публичный ([AllowAnonymous])' : classPermissions.join('; ') || (classAuthorized ? '[Authorize]' : 'не задано'),
    endpoints,
  };
}

export function parseControllers(source, file) {
  const matches = [...source.matchAll(/public\s+(?:(?:sealed|partial|abstract)\s+)*class\s+(\w+Controller)\b/g)];
  if (matches.length === 0) return [parseControllerBlock(source, file, null, source.length, 0)];
  return matches.map((classMatch, index) => parseControllerBlock(
    source,
    file,
    classMatch,
    matches[index + 1]?.index ?? source.length,
    index === 0 ? 0 : matches[index - 1]?.index ?? 0,
  ));
}

export function parseController(source, file) {
  return parseControllers(source, file)[0];
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
  return parseTabAccessRules(source).map(item => item.name);
}

export function parseTabAccessRules(source) {
  const block = source.match(/export const TAB_ACCESS_RULES\s*=\s*\{([\s\S]*?)\}\s*satisfies/)?.[1] ?? '';
  return [...block.matchAll(/^\s*(\w+):\s*([^\n]+),?$/gm)].map(match => ({
    name: match[1],
    access: match[2].replace(/,$/, '').trim(),
  }));
}

export function parseBackgroundServices(source, file) {
  return [...source.matchAll(/(?:public\s+)?(?:sealed\s+)?class\s+(\w+)(?:\s*\([^)]*\))?\s*:\s*BackgroundService\b/g)]
    .map(match => ({ name: match[1], line: lineAt(source, match.index ?? 0), file: relative(file) }));
}

export function parseHttpClientRegistrations(source) {
  return [...source.matchAll(/AddHttpClient<([^>]+)>/g)].map(match => {
    const types = match[1].split(',').map(value => value.trim());
    return {
      name: types.at(-1),
      registration: match[0],
      line: lineAt(source, match.index ?? 0),
    };
  });
}

export function parseHttpClientFactoryConsumers(source) {
  const matches = [...source.matchAll(/public\s+(?:(?:sealed|partial|abstract)\s+)*class\s+(\w+)\b/g)];
  return matches.flatMap((match, index) => {
    const block = source.slice(match.index ?? 0, matches[index + 1]?.index ?? source.length);
    return block.includes('IHttpClientFactory')
      ? [{ name: match[1], line: lineAt(source, match.index ?? 0), registration: 'IHttpClientFactory' }]
      : [];
  });
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
  const programFile = path.join(root, 'backend', 'Program.cs');
  const documentationFiles = [
    ...(await fs.readdir(path.join(root, 'docs'), { withFileTypes: true }))
      .filter(entry => entry.isFile() && entry.name.endsWith('.md') && entry.name !== path.basename(outputPath))
      .map(entry => path.join(root, 'docs', entry.name)),
    path.join(root, 'README.md'),
  ];
  const backendTestSources = await Promise.all(backendTestFiles.map(async file => ({
    file: relative(file),
    source: await fs.readFile(file, 'utf8'),
  })));
  const backendTests = backendTestSources.map(item => item.source).join('\n');
  const frontendTests = (await Promise.all(frontendTestFiles.map(file => fs.readFile(file, 'utf8')))).join('\n');
  const documentationSources = await Promise.all(documentationFiles.map(async file => ({
    file: relative(file),
    source: await fs.readFile(file, 'utf8').catch(() => ''),
  })));

  const controllers = [];
  for (const file of controllerFiles) {
    const parsedControllers = parseControllers(await fs.readFile(file, 'utf8'), file);
    for (const parsed of parsedControllers) {
      const relatedTests = backendTestSources.filter(item => item.source.includes(parsed.controller));
      controllers.push({
        ...parsed,
        file: relative(file),
        tested: relatedTests.length > 0,
        relatedTests,
      });
    }
  }
  const endpoints = controllers.flatMap(controller => controller.endpoints.map(endpoint => ({
    ...endpoint,
    file: controller.file,
    tested: controller.relatedTests.some(test => hasDirectMethodEvidence(test.source, controller.controller, endpoint.method)),
    documentation: findEndpointDocumentation(documentationSources, controller.controller, endpoint),
  })));

  const forms = [];
  for (const file of frontendFiles) {
    const parsed = parseForms(await fs.readFile(file, 'utf8'), file);
    forms.push(...parsed.map(form => ({
      ...form,
      file: relative(file),
      tested: frontendTests.includes(form.component),
      documentation: findNamedDocumentation(documentationSources, form.component),
    })));
  }

  const backgroundServices = [];
  const outboundHttpIntegrations = new Map();
  for (const file of serviceFiles) {
    const source = await fs.readFile(file, 'utf8');
    backgroundServices.push(...parseBackgroundServices(source, file).map(service => ({
      ...service,
      tested: backendTests.includes(service.name),
      documentation: findNamedDocumentation(documentationSources, service.name),
    })));
    for (const integration of parseHttpClientFactoryConsumers(source)) {
      outboundHttpIntegrations.set(integration.name, {
        ...integration,
        file: relative(file),
      });
    }
  }
  const programSource = await fs.readFile(programFile, 'utf8');
  for (const integration of parseHttpClientRegistrations(programSource)) {
    outboundHttpIntegrations.set(integration.name, {
      ...integration,
      file: relative(programFile),
    });
  }
  const integrations = [...outboundHttpIntegrations.values()]
    .map(integration => ({
      ...integration,
      tested: backendTests.includes(integration.name),
      documentation: findNamedDocumentation(documentationSources, integration.name),
    }))
    .sort((left, right) => left.name.localeCompare(right.name, 'en'));
  const tabs = parseTabAccessRules(await fs.readFile(path.join(root, 'src', 'appRouting.ts'), 'utf8'))
    .map(tab => ({
      ...tab,
      tested: new RegExp(`['\"]${escapeRegex(tab.name)}['\"]`).test(frontendTests),
      documentation: findTabDocumentation(documentationSources, tab.name),
    }));
  return { controllers, endpoints, forms, backgroundServices, integrations, tabs };
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Простое упоминание контроллера не доказывает проверку каждого его действия.
 * Прямая связь засчитывается, только если тот же тестовый файл вызывает метод
 * контроллера или ссылается на него через nameof для проверки контракта.
 */
function hasDirectMethodEvidence(source, controller, method) {
  if (method === 'не распознано') return false;
  const escapedController = escapeRegex(controller);
  const escapedMethod = escapeRegex(method);
  return new RegExp(`nameof\\s*\\(\\s*${escapedController}\\s*\\.\\s*${escapedMethod}\\s*\\)`).test(source)
    || new RegExp(`\\.\\s*${escapedMethod}\\s*\\(`).test(source);
}

/**
 * Возвращает только точные ссылки на маршрут или на пару «контроллер.Метод».
 * Обычное упоминание контроллера не считается документацией конкретного API.
 */
function findEndpointDocumentation(sources, controller, endpoint) {
  const methodMarker = `${controller}.${endpoint.method}`;
  const routeWithoutConstraints = endpoint.route.replace(/\{([^}:]+):[^}]+\}/g, '{$1}');
  const routeMarkers = [endpoint.route, routeWithoutConstraints].filter(Boolean);
  return sources
    .filter(item => item.source.includes(methodMarker)
      || routeMarkers.some(marker => item.source.includes(marker)))
    .map(item => item.file);
}

function findNamedDocumentation(sources, name) {
  return sources.filter(item => item.source.includes(name)).map(item => item.file);
}

function findTabDocumentation(sources, tab) {
  const markers = [`ppTab=${tab}`, `\`${tab}\``, `/${tab}`];
  return sources.filter(item => markers.some(marker => item.source.includes(marker))).map(item => item.file);
}

function documentationCell(files) {
  return files.length > 0 ? files.join(', ') : 'нет';
}

function classifyCriticalEndpoint(endpoint) {
  const reasons = [];
  if (endpoint.access.startsWith('публичный')) reasons.push('публичный доступ');
  if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(endpoint.verb)) reasons.push('изменение данных');
  if (/(auth|user|role|permission|backup|setting|payment|invoice|contract|document|file|import|export|signature)/i.test(endpoint.route)) {
    reasons.push('чувствительные данные или операция');
  }
  return reasons;
}

function render(inventory) {
  const testedEndpoints = inventory.endpoints.filter(item => item.tested).length;
  const testedForms = inventory.forms.filter(item => item.tested).length;
  const testedServices = inventory.backgroundServices.filter(item => item.tested).length;
  const testedIntegrations = inventory.integrations.filter(item => item.tested).length;
  const documentedEndpoints = inventory.endpoints.filter(item => item.documentation.length > 0).length;
  const undocumentedCritical = inventory.endpoints.filter(item => classifyCriticalEndpoint(item).length > 0 && item.documentation.length === 0).length;
  const uncoveredForms = inventory.forms.filter(item => !item.tested);
  const uncoveredServices = inventory.backgroundServices.filter(item => !item.tested);
  const criticalUncovered = inventory.endpoints
    .map(endpoint => ({ ...endpoint, riskReasons: classifyCriticalEndpoint(endpoint) }))
    .filter(endpoint => !endpoint.tested && endpoint.riskReasons.length > 0)
    .sort((left, right) => right.riskReasons.length - left.riskReasons.length
      || left.route.localeCompare(right.route, 'en'));
  return `# Автоматическая инвентаризация поверхностей PayPlanner

Файл формируется командой \`npm run audit:surface:update\`. Ручное редактирование не допускается.
Для endpoint тестовая связь считается прямой, только если тестовый файл одновременно относится к контроллеру и вызывает конкретный метод либо проверяет его контракт через \`nameof\`. Для формы и фонового сервиса пока используется связь по имени компонента или класса; детальная полнота сценариев проверяется последующими этапами roadmap.

## Сводка

- Разделов frontend: ${inventory.tabs.length}.
- Backend-контроллеров: ${inventory.controllers.length}.
- HTTP endpoints: ${inventory.endpoints.length}; с прямой тестовой связью: ${testedEndpoints}.
- HTTP endpoints с точной ссылкой из документации: ${documentedEndpoints}; критичных без такой ссылки: ${undocumentedCritical}.
- Критичных endpoints без прямой тестовой связи: ${criticalUncovered.length}.
- Реальных HTML-форм: ${inventory.forms.length}; с найденной тестовой связью: ${testedForms}.
- Фоновых сервисов: ${inventory.backgroundServices.length}; с найденной тестовой связью: ${testedServices}.
- Исходящих HTTP-интеграций: ${inventory.integrations.length}; с найденной тестовой связью: ${testedIntegrations}.

## Разделы frontend

${table(['Раздел', 'Правило доступа', 'Тестовая связь', 'Документация'], inventory.tabs.map(tab => [tab.name, tab.access, tab.tested ? 'да' : 'нет', documentationCell(tab.documentation)]))}

## Формы

${table(['Компонент', 'Файл', 'Строка', 'Тест найден', 'Документация'], inventory.forms.map(item => [item.component, item.file, item.line, item.tested ? 'да' : 'нет', documentationCell(item.documentation)]))}

### Формы без тестовой связи

${uncoveredForms.length > 0
    ? table(['Компонент', 'Файл:строка', 'Документация'], uncoveredForms.map(item => [item.component, `${item.file}:${item.line}`, documentationCell(item.documentation)]))
    : 'Все найденные HTML-формы имеют тестовую связь.'}

## Фоновые сервисы

${table(['Сервис', 'Файл', 'Строка', 'Тест найден', 'Документация'], inventory.backgroundServices.map(item => [item.name, item.file, item.line, item.tested ? 'да' : 'нет', documentationCell(item.documentation)]))}

### Фоновые сервисы без тестовой связи

${uncoveredServices.length > 0
    ? table(['Сервис', 'Файл:строка', 'Документация'], uncoveredServices.map(item => [item.name, `${item.file}:${item.line}`, documentationCell(item.documentation)]))
    : 'Все найденные фоновые сервисы имеют тестовую связь.'}

## Контроллеры

${table(['Контроллер', 'Файл', 'Endpoints', 'Правило доступа', 'Тест найден'], inventory.controllers.map(item => [item.controller, item.file, item.endpoints.length, item.access, item.tested ? 'да' : 'нет']))}

## Исходящие HTTP-интеграции

${table(['Клиент или сервис', 'Регистрация', 'Файл:строка', 'Тест найден', 'Документация'], inventory.integrations.map(item => [item.name, item.registration, `${item.file}:${item.line}`, item.tested ? 'да' : 'нет', documentationCell(item.documentation)]))}

## Критичные endpoints без прямой тестовой связи

В список попадают публичные маршруты, операции изменения данных и маршруты с авторизацией, пользователями, правами, настройками, резервными копиями, финансами, документами, файлами, импортом, экспортом или электронной подписью.

${criticalUncovered.length > 0
    ? table(['Метод', 'Маршрут', 'Контроллер.действие', 'Причина приоритета', 'Файл:строка'], criticalUncovered.map(item => [item.verb, item.route, `${item.controller}.${item.method}`, item.riskReasons.join(', '), `${item.file}:${item.line}`]))
    : 'Непокрытых критичных endpoints не найдено.'}

## Все HTTP endpoints

${table(['Метод', 'Маршрут', 'Контроллер', 'Действие', 'Файл:строка', 'Правило доступа', 'Прямой тест действия', 'Документация'], inventory.endpoints.map(item => [item.verb, item.route, item.controller, item.method, `${item.file}:${item.line}`, item.access, item.tested ? 'да' : 'нет', documentationCell(item.documentation)]))}
`;
}

function selfTest() {
  const controller = parseController('[Authorize]\n[Route("api/items")]\npublic class ItemsController {\n[HttpGet("{id}")]\npublic async Task<IActionResult> Get(int id) => null;\n}', 'ItemsController.cs');
  if (controller.endpoints.length !== 1 || controller.endpoints[0].method !== 'Get' || controller.endpoints[0].route !== 'api/items/{id}') throw new Error('Не удалось разобрать endpoint.');
  if (!controller.endpoints[0].authorized || controller.endpoints[0].access !== '[Authorize]') throw new Error('Не удалось разобрать авторизацию endpoint.');
  const publicController = parseController('[Authorize]\n[Route("api/public")]\npublic class PublicController {\n[AllowAnonymous]\n[HttpGet]\npublic IActionResult Get() => null;\n}', 'PublicController.cs');
  if (publicController.endpoints[0].authorized || !publicController.endpoints[0].access.startsWith('публичный')) throw new Error('Не удалось распознать AllowAnonymous.');
  const permissionController = parseController('[Authorize]\n[Route("api/secure")]\npublic class SecureController {\n[HttpPut]\n[RequireSectionPermission(Permissions.Clients, Operations.Edit)]\npublic IActionResult Put() => null;\n}', 'SecureController.cs');
  if (!permissionController.endpoints[0].access.includes('Operations.Edit')) throw new Error('Не удалось распознать атрибут доступа после Http-атрибута.');
  const multiController = parseControllers('[Authorize]\n[Route("api/first")]\npublic class FirstController {\n[HttpGet("/api/shared/{id}")]\npublic IActionResult Shared(int id) => null;\n}\n[AllowAnonymous]\n[Route("api/second")]\npublic class SecondController {\n[HttpPost]\npublic IActionResult Receive() => null;\n}', 'CombinedController.cs');
  if (multiController.length !== 2 || multiController[1].controller !== 'SecondController') throw new Error('Не удалось разобрать несколько контроллеров в одном файле.');
  if (multiController[0].endpoints[0].route !== 'api/shared/{id}') throw new Error('Абсолютный маршрут ошибочно объединён с route контроллера.');
  if (!multiController[1].endpoints[0].access.startsWith('публичный')) throw new Error('Атрибут второго контроллера определён неверно.');
  if (!hasDirectMethodEvidence('var controller = new SecureController(); await controller.Put();', 'SecureController', 'Put')) throw new Error('Не найдена прямая тестовая связь endpoint.');
  if (hasDirectMethodEvidence('var controller = new SecureController();', 'SecureController', 'Put')) throw new Error('Упоминание контроллера ошибочно принято за тест endpoint.');
  const documentationSources = [{ file: 'docs/example.md', source: 'Документ без ссылки на API.' }];
  if (findEndpointDocumentation(documentationSources, 'ItemsController', { method: 'Get', route: '' }).length !== 0) throw new Error('Пустой маршрут ошибочно принят за ссылку из документации.');
  if (parseForms('a\n<form onSubmit={save}>', 'Editor.tsx')[0]?.line !== 2) throw new Error('Не удалось разобрать форму.');
  if (parseTabs('export const TAB_ACCESS_RULES = {\n clients: () => true,\n} satisfies Record<string, unknown>;').join() !== 'clients') throw new Error('Не удалось разобрать разделы.');
  const tabRules = parseTabAccessRules('export const TAB_ACCESS_RULES = {\n clients: (permissions) => permissions.clients.canView,\n} satisfies Record<string, unknown>;');
  if (tabRules[0]?.access !== '(permissions) => permissions.clients.canView') throw new Error('Не удалось сохранить правило доступа раздела.');
  const backgroundServices = [
    ...parseBackgroundServices('public sealed class RegularWorker : BackgroundService {}', 'RegularWorker.cs'),
    ...parseBackgroundServices('public sealed class PrimaryWorker(ILogger logger) : BackgroundService {}', 'PrimaryWorker.cs'),
  ];
  if (backgroundServices.map(item => item.name).join() !== 'RegularWorker,PrimaryWorker') throw new Error('Не удалось разобрать фоновые сервисы.');
  const httpClients = parseHttpClientRegistrations('services.AddHttpClient<IOneClient, OneClient>();\nservices.AddHttpClient<SecondClient>();');
  if (httpClients.map(item => item.name).join() !== 'OneClient,SecondClient') throw new Error('Не удалось разобрать регистрации HTTP-интеграций.');
  const factoryConsumers = parseHttpClientFactoryConsumers('public class Dto {}\npublic sealed class OAuthService(IHttpClientFactory factory) {}');
  if (factoryConsumers.map(item => item.name).join() !== 'OAuthService') throw new Error('Не удалось разобрать потребителей IHttpClientFactory.');
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
