import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');
const modelsDirectory = join(repositoryRoot, 'backend', 'Models');
const inventoryPath = join(repositoryRoot, 'docs', 'field-encryption-data-inventory-2026-07-30.md');
const generatedInventoryPath = join(repositoryRoot, 'docs', 'field-encryption-candidates.generated.md');

const requiredFields = {
  Client: ['Name', 'Email', 'Phone', 'Address', 'Notes'],
  ClientPassport: ['Series', 'Number', 'IssuedBy', 'DepartmentCode', 'BirthPlacePassport', 'BirthPlaceFias'],
  AddressRecord: ['Value', 'UnrestrictedValue', 'PostalCode', 'House', 'Flat', 'GeoLat', 'GeoLon'],
  ExternalContact: ['FullName', 'Phone', 'Email', 'PhonesJson', 'EmailsJson', 'Notes'],
  User: ['Email', 'PasswordHash', 'FullName', 'DateOfBirth', 'PhoneNumber', 'LastActivityIpAddress', 'LastActivityUserAgent'],
  LegalEntity: [
    'Inn',
    'Kpp',
    'Ogrn',
    'Address',
    'Phone',
    'Email',
    'Director',
    'BankAccountsJson',
    'CardsJson',
    'TreasuryAccountNumber',
    'TreasuryCorrAccountNumber',
    'SealDataUrl',
    'DirectorSignatureDataUrl',
    'DocumentDirectorJson',
    'ChiefAccountantJson',
  ],
  MoneyAccount: ['BankAccountNumber'],
  ClientCase: [
    'CourtCaseNumber',
    'Claimant',
    'Defendant',
    'ThirdParties',
    'Representatives',
    'CourtSecretaryContacts',
    'DocumentsSummary',
    'EventHistory',
    'Strategy',
  ],
  TaskJournalComment: ['Text', 'MetadataJson'],
  StickyNote: ['Text'],
  ClientCaseDocument: ['OriginalFileName', 'StoragePath'],
  TaskJournalFile: ['OriginalFileName', 'StoragePath'],
  BankStatementImportRow: ['AccountName', 'CounterpartyName', 'Purpose', 'PaymentOrderNumber'],
  Payment: ['Description', 'Notes', 'SystemNotes'],
  NotificationDelivery: ['RecipientAddress', 'Subject', 'BodyPreview'],
  UserNotification: ['BodyPreview', 'MetadataJson'],
  UserActivityLog: ['UserEmail', 'IpAddress', 'UserAgent', 'QueryString', 'MetadataJson'],
  ObjectAuditEvent: ['Description', 'MetadataJson'],
  SpreadsheetImportBatchOperation: ['BeforeJson', 'AfterJson', 'MetadataJson'],
  GoogleCalendarConnection: ['GoogleEmail', 'AccessTokenProtected', 'RefreshTokenProtected'],
  YandexCalendarConnection: ['YandexEmail', 'AccessTokenProtected', 'RefreshTokenProtected'],
};
const modelSourceFiles = {
  BankStatementImportRow: 'BankStatementImportBatch.cs',
  SpreadsheetImportBatchOperation: 'SpreadsheetImportBatch.cs',
};

const candidateNamePattern = /(name|email|phone|address|passport|birth|inn|kpp|ogrn|account|card|signature|seal|comment|notes|text|description|metadata|query|string|token|secret|purpose|representative|claimant|defendant|party|contact|ipaddress|useragent)/i;
const propertyPattern = /public\s+[\w<>\[\],.?]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}/g;
const inventory = readFileSync(inventoryPath, 'utf8');
const failures = [];

function extractClasses(source) {
  const declarations = Array.from(source.matchAll(/public\s+(?:sealed\s+)?class\s+(\w+)[^{]*\{/g));
  return declarations.map((declaration, index) => {
    const bodyStart = declaration.index ?? 0;
    const bodyEnd = declarations[index + 1]?.index ?? source.length;
    const body = source.slice(bodyStart, bodyEnd);
    return {
      name: declaration[1],
      fields: new Set(Array.from(body.matchAll(propertyPattern), (match) => match[1])),
    };
  });
}

const modelFields = new Map();
for (const fileName of readdirSync(modelsDirectory).filter((name) => name.endsWith('.cs'))) {
  const source = readFileSync(join(modelsDirectory, fileName), 'utf8');
  for (const model of extractClasses(source)) {
    modelFields.set(model.name, model.fields);
  }
}

for (const [modelName, fieldNames] of Object.entries(requiredFields)) {
  const sourceFileName = modelSourceFiles[modelName] ?? `${modelName}.cs`;
  const actualFields = modelFields.get(modelName);
  if (!actualFields) {
    failures.push(`Не найден файл модели backend/Models/${sourceFileName}`);
    continue;
  }

  for (const fieldName of fieldNames) {
    const marker = `${modelName}.${fieldName}`;
    if (!actualFields.has(fieldName)) {
      failures.push(`В модели отсутствует поле ${marker}`);
    }
    if (!inventory.includes(`\`${marker}\``)) {
      failures.push(`В инвентаризации отсутствует маркер ${marker}`);
    }
  }
}

const reviewCandidates = [];
for (const [modelName, fieldNames] of modelFields) {
  for (const fieldName of fieldNames) {
    if (candidateNamePattern.test(fieldName) && !inventory.includes(`\`${modelName}.${fieldName}\``)) {
      reviewCandidates.push(`${modelName}.${fieldName}`);
    }
  }
}

function buildGeneratedInventory(candidates) {
  const grouped = new Map();
  for (const candidate of candidates.sort()) {
    const separator = candidate.indexOf('.');
    const modelName = candidate.slice(0, separator);
    const fieldName = candidate.slice(separator + 1);
    const fields = grouped.get(modelName) ?? [];
    fields.push(fieldName);
    grouped.set(modelName, fields);
  }

  const lines = [
    '# Автоматически найденные кандидаты для классификации',
    '',
    'Файл сформирован командой `npm run audit:encryption-inventory:update`.',
    'Он дополняет ручную карту и не является утверждённой бизнес-классификацией.',
    '',
    `Всего кандидатов вне ручной карты: ${candidates.length}.`,
    '',
  ];
  for (const [modelName, fields] of grouped) {
    lines.push(`- \`${modelName}\`: ${fields.map((field) => `\`${modelName}.${field}\``).join(', ')}`);
  }
  lines.push('');
  return lines.join('\n');
}

const generatedInventory = buildGeneratedInventory(reviewCandidates);
if (process.argv.includes('--write')) {
  writeFileSync(generatedInventoryPath, generatedInventory, 'utf8');
} else {
  let currentGeneratedInventory = '';
  try {
    currentGeneratedInventory = readFileSync(generatedInventoryPath, 'utf8');
  } catch {
    failures.push('Отсутствует docs/field-encryption-candidates.generated.md; выполните аудит с --write.');
  }
  if (currentGeneratedInventory !== generatedInventory) {
    failures.push('Автоматический список кандидатов устарел; выполните аудит с --write.');
  }
}

if (failures.length > 0) {
  console.error('Инвентаризация чувствительных данных устарела:');
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exitCode = 1;
} else {
  const requiredCount = Object.values(requiredFields).reduce((total, fields) => total + fields.length, 0);
  console.log(`Проверено критических маркеров: ${requiredCount}.`);
  console.log(`Кандидатов для следующей ручной классификации: ${reviewCandidates.length}.`);
  console.log(process.argv.includes('--write')
    ? 'Автоматический список кандидатов обновлён.'
    : 'Автоматический список кандидатов актуален.');
}
