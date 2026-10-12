import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

// Static preparation only: parses repository TSX, never loads app code or data.
const root = process.cwd();
const files = [
  'src/components/Tasks/TaskJournal.tsx',
  'src/components/Tasks/TaskJournalColumnSettings.tsx',
  'src/components/Clients/ClientDetail.tsx',
  'src/components/Clients/ClientModal.tsx',
  'src/components/Clients/ClientAddressEntriesForm.tsx',
  'src/components/Clients/ClientPassportEntriesForm.tsx',
  'src/components/Tasks/TaskTemplatesPanel.tsx',
  'src/components/Messenger/InternalMessenger.tsx',
];
const widgets = new Set(['button', 'input', 'select', 'textarea', 'a', 'SystemCheckbox', 'SystemDateInput',
  'SystemSelect', 'LookupSelect', 'TableColumnSettings', 'TaskJournalColumnSettings', 'TablePagination',
  'DatePeriodPicker', 'MonthRangePicker', 'ClientAddressEntriesForm', 'ClientPassportEntriesForm', 'TaskChatPanel', 'TaskTemplatesPanel']);
const rows = [];
const schemas = [];
const schemaFiles = [...files, 'src/utils/taskJournalStore.ts', 'src/components/Clients/clientRelativeUtils.ts'];
for (const file of schemaFiles) {
  const source = ts.createSourceFile(file, fs.readFileSync(path.join(root, file), 'utf8'), ts.ScriptTarget.Latest, true);
  function collect(node) {
    if (ts.isInterfaceDeclaration(node) && /^(TaskJournalItem|TaskJournalParticipant|TaskJournalRecurrence|ClientExtendedProfile|ClientEducationItem|ClientProfessionItem|ClientWorkplaceItem|ClientBirthCertificateItem|ClientMilitaryIdItem|ClientAddressItem|ClientAddressEntryFormValue|ClientPassportEntryFormValue|ClientRelativeItem)$/.test(node.name.text)) {
      for (const member of node.members) if (ts.isPropertySignature(member)) schemas.push({
        file, schema: node.name.text, field: member.name.getText(source),
        type: member.type?.getText(source), optional: Boolean(member.questionToken),
        line: source.getLineAndCharacterOfPosition(member.getStart(source)).line + 1,
      });
    }
    ts.forEachChild(node, collect);
  }
  collect(source);
}
function text(node, source) { return node ? node.getText(source).replace(/\s+/g, ' ').trim() : ''; }
function containingFunction(node) {
  for (let p = node.parent; p; p = p.parent) {
    if (ts.isFunctionDeclaration(p) && p.name) return p.name.text;
    if ((ts.isArrowFunction(p) || ts.isFunctionExpression(p)) && ts.isVariableDeclaration(p.parent)) return p.parent.name.getText();
  }
  return 'module';
}
for (const file of files) {
  const source = ts.createSourceFile(file, fs.readFileSync(path.join(root, file), 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  function visit(node) {
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      const tag = node.tagName.getText(source);
      const attributes = Object.fromEntries(node.attributes.properties.filter(ts.isJsxAttribute).map(a => [a.name.getText(source), text(a.initializer, source)]));
      if (widgets.has(tag) || Object.keys(attributes).some(a => /^on(Click|Change|Select|Save|Submit|Delete|Export)$/.test(a))) {
        const conditions = [];
        for (let p = node.parent; p; p = p.parent) {
          if (ts.isBinaryExpression(p) && p.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken && p.right.pos <= node.pos && p.right.end >= node.end) conditions.push(text(p.left, source));
          if (ts.isConditionalExpression(p)) {
            const branch = p.whenTrue.pos <= node.pos && p.whenTrue.end >= node.end ? 'true' : 'false';
            conditions.push(`${text(p.condition, source)} [${branch}]`);
          }
          if (ts.isIfStatement(p)) conditions.push(`${text(p.expression, source)} [if block; see branch]`);
        }
        let label = attributes['aria-label'] || attributes.title || attributes.placeholder || attributes.name || '';
        const texts = [];
        if (ts.isJsxOpeningElement(node)) {
          function childText(child) {
            if (ts.isJsxText(child)) { const t = child.text.replace(/\s+/g, ' ').trim(); if (t) texts.push(t); }
            else if (ts.isJsxExpression(child) && child.expression && !ts.isJsxElement(child.expression)) texts.push(`{${text(child.expression, source)}}`);
            else ts.forEachChild(child, childText);
          }
          for (const child of node.parent.children) childText(child);
        }
        label = label || texts.join(' ') || attributes.value || attributes.checked || attributes['data-testid'] || tag;
        // Field labels may be outside the input (Field/label wrapper).
        let fieldLabel = '';
        for (let p = node.parent; p && !ts.isFunctionDeclaration(p); p = p.parent) {
          if (ts.isJsxElement(p)) {
            const candidate = p.openingElement.attributes.properties.find(a => ts.isJsxAttribute(a) && a.name.getText(source) === 'label');
            if (candidate) { fieldLabel = text(candidate.initializer, source); break; }
          }
        }
        rows.push({ file, line: source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1,
          function: containingFunction(node), tag, label, fieldLabel, attributes, conditions });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
}
const directory = path.join(root, 'docs/yougile-sources/action-inventory');
fs.mkdirSync(directory, { recursive: true });
fs.writeFileSync(path.join(directory, '2026-10-12.json'), `${JSON.stringify({ revision: process.env.PREPARATION_SOURCE_REVISION || 'ee6eef4a', limitations: 'Static JSX inventory plus declared model fields; permission helpers require the manual tables; no role execution proof.', rows, schemas }, null, 2)}\n`);
function md(value, limit = 360) {
  const s = String(value ?? '').replaceAll('|', '\\|').replaceAll('`', '\\`').replace(/\s+/g, ' ');
  return s.length > limit ? `${s.slice(0, limit)}… (полный текст в JSON)` : s;
}
function inlineCode(value, limit = 360) {
  const escaped = md(value, limit).replaceAll('\\`', '`');
  const longest = Math.max(0, ...[...escaped.matchAll(/`+/g)].map(match => match[0].length));
  const delimiter = '`'.repeat(longest + 1);
  return `${delimiter} ${escaped} ${delimiter}`;
}
function candidate(row, map) {
  const s = `${row.label} ${row.fieldLabel} ${row.attributes.value ?? ''} ${row.attributes.onClick ?? ''}`;
  if (map === 19) return /filter|sort|search|period|view|column|Поиск|Фильтр|Сортир/i.test(s) ? 'Компактная панель над списком/доской' : /delete|export|bulk|выбран|Удал|Экспорт/i.test(s) ? 'Меню строки/массовая панель с прежним правом' : 'Прежний рабочий блок журнала; согласовать отсутствующее на макете';
  if (map === 20) return /comment|file|chat|result|description|Комментарии|Результат|Описание|Вложен/i.test(s) ? 'Основная область/вкладки обсуждения, файлов и результата' : /title|Название/i.test(s) ? 'Шапка задачи' : 'Правая панель параметров/меню действий; сохранить бизнес-условие';
  return /passport|snils|inn|document|Паспорт|СНИЛС|ИНН/i.test(s) ? 'Документы, сохранение защищённых полей' : /address|email|phone|Телефон|Email|Адрес/i.test(s) ? 'Контактная информация' : /section|activeSection|Case|Invoice|Payment|Contract/i.test(s) ? 'Связанные разделы/правое меню' : 'Основная или свёрнутая дополнительная информация; прежнее поле не удалять';
}
const definitions = [
  { id: 19, document: 'docs/action-map-pay-19-journal-2026-10-12.md', choose: r => r.file.includes('TaskJournalColumnSettings') || r.file.includes('TaskTemplatesPanel') || (r.file.endsWith('TaskJournal.tsx') && r.line < 10833) },
  { id: 20, document: 'docs/action-map-pay-20-task-cards-2026-10-12.md', choose: r => (r.file.endsWith('TaskJournal.tsx') && (r.line >= 10833 || (r.line >= 6898 && r.line < 7402) || (r.line >= 10184 && r.line < 10442) || (r.line >= 8546 && r.line < 8706))) || (r.file.includes('InternalMessenger') && ['ChatConversation', 'TaskChatPanel', 'renderInlineFormattedText', 'renderFormattedText', 'renderPlainFormattedText'].includes(r.function)) },
  { id: 32, document: 'docs/action-map-pay-32-individual-client-2026-10-12.md', choose: r => r.file.includes('/Clients/') },
];
for (const definition of definitions) {
  const selected = rows.filter(definition.choose);
  const document = path.join(root, definition.document);
  const current = fs.readFileSync(document, 'utf8').split('\n<!-- GENERATED INVENTORY -->')[0];
  const lines = ['\n<!-- GENERATED INVENTORY -->', '## Полная статическая опись JSX', '',
    `**${selected.length} деклараций элементов/полей** в перечисленных файлах. Повторные элементы двух форм намеренно сохранены. Условия и disabled/readOnly приведены как в коде; отсутствие прямого guard в JSX не означает отсутствие серверного права. Источник полной длины выражений: [JSON](yougile-sources/action-inventory/2026-10-12.json). Динамически создаваемые поля/команды и семантика прав описаны выше.`, '',
    '| Действие/поле (binding) | Условия показа | Право/блокировка элемента | Файл и функция | Кандидат на новое место |',
    '| --- | --- | --- | --- | --- |'];
  for (const row of selected) {
    const binding = row.attributes.value || row.attributes.checked || row.attributes.name || row.attributes.onClick || row.attributes.onChange || row.attributes.onSave || '';
    const guards = Object.entries(row.attributes).filter(([key]) => /^(disabled|readOnly|required|can|allow)/.test(key)).map(([key,value]) => `${key}=${value}`).join('; ');
    const roleConditions = row.conditions.filter(c => /can[A-Z]|permission|isAdmin|userIsAdmin|isReadOnly/i.test(c));
    lines.push(`| ${md(row.fieldLabel || row.label)}; ${inlineCode(row.tag)} / ${inlineCode(binding, 160)} | ${md(row.conditions.join(' && ') || 'Нет дополнительной JSX-ветки; см. контекст функции')} | ${md([...roleConditions, guards].filter(Boolean).join('; ') || 'Прямого guard здесь нет; наследуемое право в ручной матрице/handler')} | [${path.basename(row.file)}:${row.line}](../${row.file}#L${row.line}), ${inlineCode(row.function)} | ${candidate(row, definition.id)} |`);
  }
  const modelFields = schemas.filter(row => definition.id === 32 ? row.schema.startsWith('Client') : definition.id === 20 && row.schema.startsWith('Task'));
  if (modelFields.length) {
    lines.push('', '## Опись полей моделей, включая динамические коллекции', '',
      'Эти поля дополняют JSX-опись: одна декларация ввода может обслуживать много записей. Модель включает служебные/read-only значения: таблица не предлагает сделать каждое поле редактируемым. Наследованные типы раскрыты отдельными строками соответствующего интерфейса. Условия редактирования, валидация и место переноса определяются ручной матрицей выше и конкретным binding в JSX.', '',
      '| Модель/поле | Тип | Условия и право | Источник | Кандидат |',
      '| --- | --- | --- | --- | --- |');
    for (const row of modelFields) lines.push(`| \`${row.schema}.${row.field}${row.optional ? '?' : ''}\` | \`${md(row.type)}\` | Данные текущей карточки; прежний guard/валидация binding, служебные поля не открывать для ввода | [${path.basename(row.file)}:${row.line}](../${row.file}#L${row.line}) | ${definition.id === 32 ? 'Сохранить в основном/дополнительном блоке либо связанной коллекции; не удалять скрытое' : 'Общее состояние обеих форм; визуальное место по binding'} |`);
  }
  fs.writeFileSync(document, `${current.trimEnd()}\n${lines.join('\n')}\n`);
  console.log(`PAY-${definition.id}: ${selected.length} declarations`);
}
