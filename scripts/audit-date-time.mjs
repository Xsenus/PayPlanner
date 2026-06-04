#!/usr/bin/env node

import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const repoRoot = process.cwd();
const defaultOutput = path.join(repoRoot, 'docs', 'date-time-inventory.md');

const ignoredDirectories = new Set([
  '.git',
  '.playwright-mcp',
  '.codex-build',
  'node_modules',
  'dist',
  'publish',
  'artifacts',
  'TestResults',
  'bin',
  'obj',
]);

const supportedExtensions = new Set([
  '.cs',
  '.cshtml',
  '.js',
  '.jsx',
  '.json',
  '.md',
  '.mjs',
  '.ts',
  '.tsx',
]);

const ignoredRelativeFiles = new Set([
  'docs/date-time-inventory.md',
  'docs/date-time-roadmap.md',
  'scripts/audit-date-time.mjs',
]);

const categories = [
  {
    id: 'backend-date-fields',
    title: 'Backend: поля и свойства дат',
    description: 'Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID.',
    patterns: [
      /\b(DateTime|DateTimeOffset|DateOnly|TimeOnly)\??\s+[A-ZА-ЯЁ_][\wА-Яа-яЁё_]*/gu,
      /\b(DateTime|DateTimeOffset|DateOnly|TimeOnly)\??\s+[a-zа-яё_][\wА-Яа-яЁё_]*\s*[),;]/gu,
    ],
  },
  {
    id: 'server-now',
    title: 'Backend: текущее время сервера',
    description: 'Вызовы Now/UtcNow, которые надо привести к единому источнику времени.',
    patterns: [
      /\bDateTime\.UtcNow\b/g,
      /\bDateTimeOffset\.UtcNow\b/g,
      /\bDateTime\.Now\b/g,
      /\bDateTimeOffset\.Now\b/g,
      /\bDateTimeOffset\.LocalNow\b/g,
    ],
  },
  {
    id: 'timezone-conversions',
    title: 'Backend: преобразования timezone',
    description: 'Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги.',
    patterns: [
      /\.ToLocalTime\(\)/g,
      /\.ToUniversalTime\(\)/g,
      /\bDateTime\.SpecifyKind\b/g,
      /\bTimeZoneInfo\./g,
      /\bCalendarTimeZoneHelper\b/g,
      /\bTimeZoneId\b/g,
      /\btimezone\b/gi,
      /\btimeZone\b/g,
    ],
  },
  {
    id: 'manual-offsets',
    title: 'Ручные смещения времени',
    description: 'Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов.',
    patterns: [
      /\+07:00/g,
      /\bUTC\s*\+?\s*7\b/gi,
      /\bAsia\/Novosibirsk\b/g,
      /\bAddHours\s*\(\s*7\s*\)/g,
      /\bTimeSpan\.FromHours\s*\(\s*7\s*\)/g,
    ],
  },
  {
    id: 'frontend-date-usage',
    title: 'Frontend: работа с датами',
    description: 'JS/TS места, где браузер может применить локальную timezone пользователя автоматически.',
    patterns: [
      /\bnew Date\s*\(/g,
      /\bDate\.parse\s*\(/g,
      /\.toISOString\s*\(/g,
      /\bIntl\.DateTimeFormat\b/g,
      /type=["']date["']/g,
      /type=["']datetime-local["']/g,
      /\bformatDate\b/g,
      /\bparseDate\b/g,
    ],
  },
  {
    id: 'period-filters',
    title: 'Фильтры и периоды',
    description: 'Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя.',
    patterns: [
      /\[FromQuery\]\s*(DateTime|DateOnly|DateTimeOffset)\??/g,
      /\b(DateTime|DateOnly|DateTimeOffset)\??\s+(from|to|start|end|dateFrom|dateTo)\b/gi,
      /\b(date_from|date_to|dateFrom|dateTo|startDate|endDate)\b/g,
      /\b(from|to|start|end):\s*(Date|string|null|undefined)/g,
    ],
  },
  {
    id: 'exports-imports',
    title: 'Экспорты, импорты и файлы',
    description: 'Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени.',
    patterns: [
      /\b(CSV|XLSX?|PDF|ICS)\b/g,
      /\bExport\w*/g,
      /\bImport\w*/g,
      /\bDownload\w*/g,
      /\bBuildIcs\b/g,
      /\bFormatIcs\b/g,
      /\bical\b/gi,
    ],
  },
  {
    id: 'calendar-integrations',
    title: 'Интеграции календарей',
    description: 'Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID.',
    patterns: [
      /\bGoogleCalendar\b/g,
      /\bYandexCalendar\b/g,
      /\bExternalCalendar\b/g,
      /\bCalendarSync\b/g,
      /\bGoogle Calendar\b/g,
      /\bЯндекс Календар[ьяею]?\b/g,
      /\bcalendarId\b/gi,
    ],
  },
];

function parseArgs(argv) {
  const args = new Set(argv);
  const outputIndex = argv.indexOf('--output');

  return {
    selfTest: args.has('--self-test'),
    output: outputIndex >= 0 && argv[outputIndex + 1]
      ? path.resolve(argv[outputIndex + 1])
      : defaultOutput,
  };
}

function isSupportedFile(filePath) {
  return supportedExtensions.has(path.extname(filePath));
}

function shouldIgnoreFile(filePath, root) {
  const relativePath = path.relative(root, filePath).replaceAll(path.sep, '/');

  return ignoredRelativeFiles.has(relativePath);
}

function shouldIgnoreDirectory(name) {
  return ignoredDirectories.has(name);
}

function walkFiles(root) {
  const result = [];
  const stack = [root];

  while (stack.length > 0) {
    const current = stack.pop();
    const entries = fs.readdirSync(current, { withFileTypes: true });

    for (const entry of entries) {
      const fullPath = path.join(current, entry.name);

      if (entry.isDirectory()) {
        if (!shouldIgnoreDirectory(entry.name)) {
          stack.push(fullPath);
        }
        continue;
      }

      if (entry.isFile() && isSupportedFile(fullPath) && !shouldIgnoreFile(fullPath, root)) {
        result.push(fullPath);
      }
    }
  }

  return result.sort((a, b) => a.localeCompare(b));
}

function getLineStarts(content) {
  const starts = [0];

  for (let index = 0; index < content.length; index += 1) {
    if (content[index] === '\n') {
      starts.push(index + 1);
    }
  }

  return starts;
}

function findLineNumber(lineStarts, offset) {
  let low = 0;
  let high = lineStarts.length - 1;

  while (low <= high) {
    const middle = Math.floor((low + high) / 2);

    if (lineStarts[middle] <= offset) {
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }

  return high + 1;
}

function lineSnippet(content, lineStarts, lineNumber) {
  const start = lineStarts[lineNumber - 1] ?? 0;
  const end = content.indexOf('\n', start);
  const raw = content.slice(start, end >= 0 ? end : undefined).trim();

  return raw.replace(/\s+/g, ' ').slice(0, 220);
}

function scanContent(filePath, content, root) {
  const normalizedRelativePath = path.relative(root, filePath).replaceAll(path.sep, '/');
  const lineStarts = getLineStarts(content);
  const matches = [];
  const seen = new Set();

  for (const category of categories) {
    for (const pattern of category.patterns) {
      pattern.lastIndex = 0;

      for (const match of content.matchAll(pattern)) {
        const line = findLineNumber(lineStarts, match.index ?? 0);
        const key = `${category.id}:${line}:${match[0]}`;

        if (seen.has(key)) {
          continue;
        }

        seen.add(key);
        matches.push({
          categoryId: category.id,
          file: normalizedRelativePath,
          line,
          match: match[0],
          snippet: lineSnippet(content, lineStarts, line),
        });
      }
    }
  }

  return matches;
}

function scanProject(root) {
  const files = walkFiles(root);
  const matches = [];

  for (const file of files) {
    const buffer = fs.readFileSync(file);

    if (buffer.includes(0)) {
      continue;
    }

    const content = buffer.toString('utf8');
    matches.push(...scanContent(file, content, root));
  }

  return { files, matches };
}

function summarize(matches) {
  const byCategory = new Map(categories.map((category) => [category.id, 0]));
  const byFile = new Map();

  for (const match of matches) {
    byCategory.set(match.categoryId, (byCategory.get(match.categoryId) ?? 0) + 1);
    byFile.set(match.file, (byFile.get(match.file) ?? 0) + 1);
  }

  return {
    byCategory,
    byFile: [...byFile.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])),
  };
}

function escapeMarkdownTable(value) {
  return String(value).replaceAll('|', '\\|').replace(/\r?\n/g, ' ');
}

function renderReport(root, files, matches) {
  const { byCategory, byFile } = summarize(matches);
  const generatedAt = new Date().toISOString();
  const lines = [
    '# Инвентаризация дат и времени',
    '',
    `Сгенерировано: ${generatedAt}`,
    '',
    'Этот отчет собирает места, где проект работает с датами, временем, периодами, экспортами и календарными интеграциями. Его задача - помочь переводить систему на единое правило: хранение в UTC плюс явный timezone ID пользователя или организации.',
    '',
    '## Как обновить',
    '',
    '```powershell',
    'npm run audit:dates',
    '```',
    '',
    '## Сводка',
    '',
    `- Проверено файлов: ${files.length}`,
    `- Найдено совпадений: ${matches.length}`,
    '',
    '| Категория | Совпадений | Что проверить |',
    '| --- | ---: | --- |',
  ];

  for (const category of categories) {
    lines.push(`| ${escapeMarkdownTable(category.title)} | ${byCategory.get(category.id) ?? 0} | ${escapeMarkdownTable(category.description)} |`);
  }

  lines.push('', '## Файлы с наибольшим количеством совпадений', '');
  lines.push('| Файл | Совпадений |');
  lines.push('| --- | ---: |');

  for (const [file, count] of byFile.slice(0, 30)) {
    lines.push(`| \`${escapeMarkdownTable(file)}\` | ${count} |`);
  }

  if (byFile.length === 0) {
    lines.push('| Нет данных | 0 |');
  }

  lines.push('', '## Детализация', '');

  for (const category of categories) {
    const categoryMatches = matches.filter((match) => match.categoryId === category.id);
    const omitted = Math.max(0, categoryMatches.length - 250);

    lines.push(`### ${category.title}`, '');
    lines.push(category.description, '');
    lines.push('| Файл | Строка | Совпадение | Контекст |');
    lines.push('| --- | ---: | --- | --- |');

    for (const match of categoryMatches.slice(0, 250)) {
      lines.push(`| \`${escapeMarkdownTable(match.file)}\` | ${match.line} | \`${escapeMarkdownTable(match.match)}\` | ${escapeMarkdownTable(match.snippet)} |`);
    }

    if (categoryMatches.length === 0) {
      lines.push('| Нет совпадений | 0 | - | - |');
    }

    if (omitted > 0) {
      lines.push(`| Еще ${omitted} совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |`);
    }

    lines.push('');
  }

  lines.push('## Правила разбора', '');
  lines.push('- Совпадение в отчете не всегда означает ошибку: это место, которое надо проверить при переводе на UTC + timezone ID.');
  lines.push('- Жесткие смещения вроде `+07:00`, `UTC+7`, `Asia/Novosibirsk` и `AddHours(7)` считаются приоритетными для исправления.');
  lines.push('- Для фильтров периода нужно отдельно проверить границы дня: начало и конец должны считаться в timezone пользователя, а храниться и сравниваться в UTC.');
  lines.push('- Для календарей Google/Yandex/ICS нужно передавать timezone ID события или календаря, а не полагаться на timezone сервера.');
  lines.push('');
  lines.push(`_Корень проверки: \`${path.relative(root, root) || '.'}\`_`);
  lines.push('');

  return lines.join('\n');
}

function writeReport(output, markdown) {
  fs.mkdirSync(path.dirname(output), { recursive: true });
  fs.writeFileSync(output, markdown, 'utf8');
}

function runSelfTest() {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'payplanner-date-audit-'));

  try {
    fs.mkdirSync(path.join(tempRoot, 'src'), { recursive: true });
    fs.mkdirSync(path.join(tempRoot, 'node_modules', 'ignored'), { recursive: true });

    fs.writeFileSync(path.join(tempRoot, 'src', 'Calendar.cs'), [
      'public DateTime DueDate { get; set; }',
      'var now = DateTime.UtcNow;',
      'var shifted = dueDate.AddHours(7);',
      'var local = dueDate.ToLocalTime();',
      'var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Novosibirsk");',
    ].join('\n'), 'utf8');

    fs.writeFileSync(path.join(tempRoot, 'src', 'Calendar.tsx'), [
      'const value = new Date(input).toISOString();',
      'const formatter = new Intl.DateTimeFormat("ru-RU");',
      '<input type="datetime-local" />;',
    ].join('\n'), 'utf8');

    fs.writeFileSync(path.join(tempRoot, 'node_modules', 'ignored', 'bad.cs'), 'DateTime.Now', 'utf8');

    const { files, matches } = scanProject(tempRoot);
    const categoryIds = new Set(matches.map((match) => match.categoryId));

    assert.equal(files.length, 2);
    assert.ok(categoryIds.has('backend-date-fields'));
    assert.ok(categoryIds.has('server-now'));
    assert.ok(categoryIds.has('manual-offsets'));
    assert.ok(categoryIds.has('timezone-conversions'));
    assert.ok(categoryIds.has('frontend-date-usage'));
    assert.equal(matches.some((match) => match.file.includes('node_modules')), false);

    const report = renderReport(tempRoot, files, matches);

    assert.match(report, /Инвентаризация дат и времени/);
    assert.match(report, /Backend: поля и свойства дат/);
    assert.match(report, /Frontend: работа с датами/);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
}

function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.selfTest) {
    runSelfTest();
    console.log('Date/time audit self-test passed.');
    return;
  }

  const { files, matches } = scanProject(repoRoot);
  const report = renderReport(repoRoot, files, matches);
  writeReport(args.output, report);

  console.log(`Date/time inventory written to ${path.relative(repoRoot, args.output)}`);
  console.log(`Scanned files: ${files.length}`);
  console.log(`Matches: ${matches.length}`);
}

main();
