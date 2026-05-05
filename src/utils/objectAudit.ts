const COMMON_FIELD_LABELS: Record<string, string> = {
  Id: 'ID',
  Name: 'Название',
  FullName: 'Полное имя',
  Description: 'Описание',
  Comment: 'Комментарий',
  Type: 'Тип',
  Status: 'Статус',
  Amount: 'Сумма',
  Number: 'Номер',
  Date: 'Дата',
  Email: 'Email',
  Phone: 'Телефон',
  Phones: 'Телефоны',
  FirstName: 'Имя',
  LastName: 'Фамилия',
  MiddleName: 'Отчество',
  DateOfBirth: 'Дата рождения',
  PhoneNumber: 'Телефон',
  WhatsApp: 'WhatsApp',
  Telegram: 'Telegram',
  Instagram: 'Instagram',
  Messenger: 'Messenger',
  Viber: 'Viber',
  Address: 'Адрес',
  AddressLine: 'Адрес',
  ResponsibleUserId: 'Ответственный',
  ResponsibleId: 'Ответственный',
  ResponsibleName: 'Ответственный',
  Responsibles: 'Ответственные',
  CounterpartyName: 'Контрагент',
  ClientId: 'Клиент',
  ClientName: 'Клиент',
  ExpenseClientId: 'Клиент расхода',
  ExpenseClientName: 'Клиент расхода',
  LegalEntityId: 'Юридическое лицо',
  LegalEntityName: 'Юридическое лицо',
  ExpenseLegalEntityId: 'Юрлицо расхода',
  ExpenseLegalEntityName: 'Юрлицо расхода',
  ClientCaseId: 'Дело',
  ClientCaseReference: 'Дело',
  ClientCaseName: 'Дело',
  InvoiceClientCases: 'Дела счета',
  ContractId: 'Договор',
  ContractNumber: 'Договор',
  ActId: 'Акт',
  ActReference: 'Акт',
  InvoiceId: 'Счет',
  InvoiceNumber: 'Счет',
  Account: 'Счет / касса',
  AccountId: 'Счет / касса',
  AccountName: 'Счет / касса',
  AccountDate: 'Дата операции',
  PaymentSourceId: 'Источник платежа',
  PaymentSourceName: 'Источник платежа',
  ServiceId: 'Услуга',
  ServiceName: 'Услуга',
  IncomeTypeId: 'Тип дохода',
  IncomeTypeName: 'Тип дохода',
  ExpenseTypeId: 'Тип расхода',
  ExpenseTypeName: 'Тип расхода',
  DealTypeId: 'Тип дела',
  DealTypeName: 'Тип дела',
  PaymentStatusId: 'Статус оплаты',
  PaymentStatusName: 'Статус оплаты',
  DueDate: 'Срок оплаты',
  IssueDate: 'Дата выставления',
  PaymentDate: 'Дата оплаты',
  PaidDate: 'Дата оплаты',
  LastPaymentDate: 'Дата последней оплаты',
  PlannedDate: 'Плановая дата',
  IsPaid: 'Оплачен',
  PaidAmount: 'Оплачено',
  OutstandingAmount: 'Долг',
  RescheduleCount: 'Количество переносов',
  StartDate: 'Дата начала',
  EndDate: 'Дата окончания',
  CreatedAt: 'Дата создания',
  CreatedByUserId: 'Создал',
  CreatedByUserName: 'Создал',
  UpdatedAt: 'Дата изменения',
  UpdatedByUserId: 'Изменил',
  UpdatedByUserName: 'Изменил',
  RoleId: 'Роль',
  IsEmployee: 'Сотрудник',
  IsActive: 'Активность',
  IsApproved: 'Одобрение',
  EmploymentStartDate: 'Дата начала работы',
  EmploymentEndDate: 'Дата окончания работы',
  PhotoUrl: 'Фото',
  PermissionsUpdatedAt: 'Изменение прав',
  PermissionsUpdatedByUserName: 'Изменил права',
  IsDeleted: 'Состояние удаления',
};

const OBJECT_TYPE_FIELD_OVERRIDES: Record<string, Record<string, string>> = {
  client: {
    FullName: 'ФИО клиента',
    Name: 'Имя клиента',
  },
  'client-case': {
    Name: 'Название дела',
    Description: 'Описание дела',
    Status: 'Статус дела',
  },
  payment: {
    CounterpartyName: 'Клиент / поставщик',
    Description: 'Описание платежа',
    Status: 'Статус платежа',
    Type: 'Тип платежа',
  },
  invoice: {
    CounterpartyName: 'Клиент / поставщик',
    Description: 'Описание счета',
    Status: 'Статус счета',
    Type: 'Тип счета',
  },
  act: {
    Description: 'Описание акта',
    Status: 'Статус акта',
  },
  contract: {
    Description: 'Описание договора',
    Status: 'Статус договора',
    Number: 'Номер договора',
  },
  'legal-entity': {
    Name: 'Наименование организации',
  },
  contact: {
    FullName: 'ФИО контакта',
  },
  user: {
    FullName: 'ФИО пользователя',
    Name: 'Пользователь',
  },
  role: {
    Name: 'Название роли',
    Description: 'Описание роли',
  },
  'task-journal-item': {
    Title: 'Заголовок задачи',
    Description: 'Описание задачи',
    Status: 'Статус задачи',
    Type: 'Тип задачи',
    Priority: 'Приоритет',
    DueAt: 'Срок выполнения',
    AssigneeUserId: 'Исполнитель',
    AuthorUserId: 'Автор',
  },
};

const OBJECT_TYPE_LABELS: Record<string, string> = {
  client: 'Клиент',
  'client-case': 'Дело',
  payment: 'Платеж',
  invoice: 'Счет',
  act: 'Акт',
  contract: 'Договор',
  'cash-order': 'Кассовый ордер',
  'legal-entity': 'Юрлицо',
  contact: 'Контакт',
  user: 'Пользователь',
  role: 'Роль',
  'task-journal-item': 'Задача',
};

const AUDIT_ACTOR_LABELS: Record<string, string> = {
  'codex data repair': 'Системное исправление',
};

const VALUE_LABELS: Record<string, string> = {
  true: 'Да',
  false: 'Нет',
  income: 'Доходный',
  expense: 'Расходный',
  pending: 'Ожидает оплаты',
  completed: 'Оплачено',
  overdue: 'Просрочено',
  processing: 'Частично оплачено',
  cancelled: 'Отменено',
  canceled: 'Отменено',
  paid: 'Оплачено',
  draft: 'Черновик',
  signed: 'Подписан',
  notsigned: 'Не подписан',
  onsigning: 'На подписании',
  open: 'Открыто',
  closed: 'Закрыто',
  active: 'Активен',
  inactive: 'Неактивен',
};

export interface ParsedAuditFieldChange {
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

interface ParsedAuditMetadataObject {
  changedFields?: unknown;
  changes?: unknown;
}

function prettifyFieldName(fieldName: string): string {
  return fieldName
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();
}

function normalizeLookupKey(value: string): string {
  return value.replace(/[^a-zA-Z0-9]/g, '').toLowerCase();
}

function findLabel(labels: Record<string, string> | undefined, fieldName: string): string | null {
  if (!labels) return null;
  if (labels[fieldName]) return labels[fieldName];

  const lookupKey = normalizeLookupKey(fieldName);
  const matchedKey = Object.keys(labels).find((key) => normalizeLookupKey(key) === lookupKey);
  return matchedKey ? labels[matchedKey] : null;
}

function looksLikeDate(value: string): boolean {
  return /^\d{4}-\d{2}-\d{2}(?:[T\s]\d{2}:\d{2}(?::\d{2})?)?/u.test(value.trim());
}

function formatDateValue(value: string): string {
  const match = value.trim().match(/^(\d{4})-(\d{2})-(\d{2})(?:[T\s](\d{2}):(\d{2})(?::(\d{2}))?)?/u);
  if (!match) return value.trim();

  const [, year, month, day, hour, minute, second] = match;
  const datePart = `${day}.${month}.${year}`;
  if (!hour || (hour === '00' && minute === '00' && (!second || second === '00'))) {
    return datePart;
  }

  return `${datePart} ${hour}:${minute}`;
}

function shouldFormatAsMoney(fieldName?: string | null): boolean {
  const lookupKey = normalizeLookupKey(fieldName ?? '');
  return Boolean(lookupKey) && (
    lookupKey.includes('amount')
    || lookupKey.includes('sum')
    || lookupKey.includes('price')
    || lookupKey.includes('total')
    || lookupKey.includes('paid')
    || lookupKey.includes('outstanding')
    || lookupKey.includes('debt')
  );
}

function shouldFormatAsBoolean(fieldName?: string | null): boolean {
  const lookupKey = normalizeLookupKey(fieldName ?? '');
  return lookupKey.startsWith('is') || lookupKey.startsWith('has') || lookupKey.startsWith('can');
}

function shouldFormatAsIdentifier(fieldName?: string | null): boolean {
  const lookupKey = normalizeLookupKey(fieldName ?? '');
  return lookupKey.endsWith('id') && lookupKey !== 'id';
}

function formatMoneyLikeValue(value: string): string | null {
  const normalized = value.trim().replace(/\s+/g, '').replace(',', '.');
  if (!/^-?\d+(?:\.\d+)?$/u.test(normalized)) return null;
  const amount = Number(normalized);
  if (!Number.isFinite(amount)) return null;

  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    maximumFractionDigits: Number.isInteger(amount) ? 0 : 2,
  }).format(amount);
}

function formatJsonValue(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return null;

  try {
    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) {
      if (parsed.length === 0) return 'Пустой список';
      return `Список: ${parsed.length}`;
    }

    if (parsed && typeof parsed === 'object') {
      return 'Структурированные данные';
    }
  } catch {
    return null;
  }

  return null;
}

function translateTechnicalWords(value: string): string {
  return value
    .replace(/\bCodex data repair\b/gi, 'Системное исправление')
    .replace(/\bCodex\b/gi, 'Система')
    .replace(/\bDataRepair\b/gi, 'Исправление данных')
    .replace(/\bTask Journal Item\b/gi, 'Задача')
    .replace(/\btask journal item\b/gi, 'Задача');
}

export function formatObjectAuditText(value: string | null | undefined): string {
  return translateTechnicalWords(value?.trim() ?? '');
}

export function getObjectAuditTypeLabel(objectType: string | null | undefined): string {
  const normalizedObjectType = objectType?.trim().toLowerCase() ?? '';
  return OBJECT_TYPE_LABELS[normalizedObjectType] ?? translateTechnicalWords(prettifyFieldName(normalizedObjectType || 'object'));
}

export function getObjectAuditActionLabel(action?: string | null): string {
  switch (action?.toLowerCase()) {
    case 'created':
      return 'Создание';
    case 'updated':
      return 'Изменение';
    case 'deleted':
      return 'Удаление';
    case 'restored':
      return 'Восстановление';
    case 'datarepair':
    case 'data-repair':
      return 'Исправление данных';
    default:
      return action ? translateTechnicalWords(action) : 'Событие';
  }
}

export function getObjectAuditActionTone(action?: string | null): string {
  switch (action?.toLowerCase()) {
    case 'created':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'updated':
      return 'border-blue-200 bg-blue-50 text-blue-700';
    case 'deleted':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    case 'restored':
      return 'border-teal-200 bg-teal-50 text-teal-700';
    case 'datarepair':
    case 'data-repair':
      return 'border-amber-200 bg-amber-50 text-amber-700';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-700';
  }
}

export function getObjectAuditActorLabel(actorUserName?: string | null): string {
  const normalizedActor = actorUserName?.trim();
  if (!normalizedActor) {
    return 'Система';
  }

  return AUDIT_ACTOR_LABELS[normalizedActor.toLowerCase()] ?? translateTechnicalWords(normalizedActor);
}

export function getObjectAuditFieldLabel(objectType: string | null | undefined, fieldName: string): string {
  const normalizedObjectType = objectType?.trim().toLowerCase() ?? '';
  const objectOverrides = OBJECT_TYPE_FIELD_OVERRIDES[normalizedObjectType];
  const objectLabel = findLabel(objectOverrides, fieldName);
  if (objectLabel) return objectLabel;

  const commonLabel = findLabel(COMMON_FIELD_LABELS, fieldName);
  if (commonLabel) return commonLabel;

  return prettifyFieldName(fieldName);
}

export function parseAuditChangedFields(metadataJson: string | null | undefined): string[] {
  if (!metadataJson?.trim()) {
    return [];
  }

  try {
    const parsed = JSON.parse(metadataJson);

    if (Array.isArray(parsed)) {
      return parsed
        .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
        .map((item) => item.trim());
    }

    if (parsed && typeof parsed === 'object') {
      const objectMetadata = parsed as ParsedAuditMetadataObject;
      if (Array.isArray(objectMetadata.changedFields)) {
        return objectMetadata.changedFields
          .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
          .map((item) => item.trim());
      }
    }

    return [];
  } catch {
    return [];
  }
}

export function parseAuditFieldChanges(metadataJson: string | null | undefined): ParsedAuditFieldChange[] {
  if (!metadataJson?.trim()) {
    return [];
  }

  try {
    const parsed = JSON.parse(metadataJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return [];
    }

    const objectMetadata = parsed as ParsedAuditMetadataObject;
    if (!Array.isArray(objectMetadata.changes)) {
      return [];
    }

    return objectMetadata.changes
      .filter((item): item is { field?: unknown; oldValue?: unknown; newValue?: unknown } => Boolean(item) && typeof item === 'object')
      .map((item) => {
        const field = typeof item.field === 'string' ? item.field.trim() : '';
        if (!field) {
          return null;
        }

        const oldValue = typeof item.oldValue === 'string' && item.oldValue.trim().length > 0
          ? item.oldValue.trim()
          : null;
        const newValue = typeof item.newValue === 'string' && item.newValue.trim().length > 0
          ? item.newValue.trim()
          : null;

        return { field, oldValue, newValue };
      })
      .filter((item): item is ParsedAuditFieldChange => item !== null);
  } catch {
    return [];
  }
}

export function formatAuditValue(
  value: string | null | undefined,
  objectType?: string | null,
  fieldName?: string | null,
): string {
  void objectType;
  const trimmed = value?.trim();
  if (!trimmed) return '—';

  const normalizedValueKey = normalizeLookupKey(trimmed);
  if (VALUE_LABELS[normalizedValueKey]) {
    return VALUE_LABELS[normalizedValueKey];
  }

  if (shouldFormatAsBoolean(fieldName)) {
    if (trimmed === '1') return 'Да';
    if (trimmed === '0') return 'Нет';
  }

  if (looksLikeDate(trimmed)) {
    return formatDateValue(trimmed);
  }

  if (shouldFormatAsMoney(fieldName)) {
    return formatMoneyLikeValue(trimmed) ?? trimmed;
  }

  if (shouldFormatAsIdentifier(fieldName) && /^-?\d+$/u.test(trimmed)) {
    return `ID ${trimmed}`;
  }

  return formatJsonValue(trimmed) ?? translateTechnicalWords(trimmed);
}

export function formatAuditChangeSummary(objectType: string | null | undefined, metadataJson: string | null | undefined): string | null {
  const changedFields = parseAuditChangedFields(metadataJson).map((fieldName) =>
    getObjectAuditFieldLabel(objectType, fieldName),
  );

  if (changedFields.length === 0) {
    return null;
  }

  if (changedFields.length === 1) {
    return `Изменено поле: ${changedFields[0]}`;
  }

  const visibleFields = changedFields.slice(0, 3).join(', ');
  const remainingCount = changedFields.length - 3;

  return remainingCount > 0
    ? `Изменены поля: ${visibleFields} и ещё ${remainingCount}`
    : `Изменены поля: ${visibleFields}`;
}
