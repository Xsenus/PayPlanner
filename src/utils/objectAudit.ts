const COMMON_FIELD_LABELS: Record<string, string> = {
  Id: 'ID',
  Name: 'Название',
  FullName: 'Полное имя',
  Title: 'Заголовок',
  Description: 'Описание',
  Comment: 'Комментарий',
  Notes: 'Заметки',
  SystemNotes: 'Служебные заметки',
  ChannelsJson: 'Каналы уведомлений',
  Type: 'Тип',
  Status: 'Статус',
  Result: 'Результат',
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
  InvoiceWorkflowStatus: 'Статус документа',
  WorkflowStatus: 'Статус документа',
  InvoiceSentForSigningAt: 'Дата отправки счета на подписание',
  InvoiceSignedAt: 'Дата подписания счета',
  InvoiceCancelledAt: 'Дата отмены счета',
  InvoiceCancelledByUserId: 'Кто отменил счет',
  InvoiceCancelledReason: 'Причина отмены счета',
  SentForSigningAt: 'Дата отправки на подписание',
  SignedAt: 'Дата подписания',
  CancelledAt: 'Дата отмены',
  CancelledByUserId: 'Отменил',
  CancelledReason: 'Причина отмены',
  PaymentId: 'Счет',
  Account: 'Счет / касса',
  AccountId: 'Счет / касса',
  AccountName: 'Счет / касса',
  AccountDate: 'Дата операции',
  PaymentSourceId: 'Источник платежа',
  PaymentSourceName: 'Источник платежа',
  PaymentOrderNumber: 'Номер платежного поручения',
  PaymentDocumentDate: 'Дата платежного документа',
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
  HasPartialPayment: 'Есть частичная оплата',
  PaidAmount: 'Оплачено',
  OutstandingAmount: 'Долг',
  DocumentKind: 'Тип документа',
  LegacyAccountNumber: 'Старый номер счета',
  LegacyAccountDate: 'Старая дата счета',
  IsNotForAccounting: 'Не для бухгалтерии',
  SortOrder: 'Порядок',
  Quantity: 'Количество',
  Unit: 'Ед. изм.',
  Price: 'Цена',
  Total: 'Итого',
  SourceKey: 'Источник строки',
  SourceSortOrder: 'Порядок в источнике',
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
  RequiresFinancialActionReason: 'Требовать причину финансовых действий',
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
    EventHistory: 'История событий',
  },
  'client-case-document': {
    DocumentKey: 'Ключ документа',
    Version: 'Версия',
    DocumentType: 'Тип документа',
    Status: 'Статус документа',
    OriginalFileName: 'Файл',
    StoredFileName: 'Файл в хранилище',
    StoragePath: 'Путь хранения',
    ContentType: 'Тип файла',
    SizeBytes: 'Размер',
    UploadedByUserId: 'Кто загрузил',
    UploadedByName: 'Кто загрузил',
    UploadedAt: 'Дата загрузки',
  },
  'client-case-work-order': {
    Title: 'Заказ-наряд',
    Status: 'Статус заказ-наряда',
    Currency: 'Валюта',
    Notes: 'Комментарий',
    TotalAmount: 'Итого',
    ApprovalRequestedAt: 'Отправлено на согласование',
    ApprovalRequestedByUserId: 'Отправил на согласование',
    ApprovalRequestedByUserName: 'Отправил на согласование',
    ApprovalDecisionAt: 'Дата решения',
    ApprovalDecisionByUserId: 'Согласующий',
    ApprovalDecisionByUserName: 'Согласующий',
    ApprovalComment: 'Комментарий согласования',
  },
  'client-case-work-order-item': {
    ServiceName: 'Услуга',
    Description: 'Описание услуги',
    ExecutorUserId: 'Исполнитель',
    ExecutorName: 'Исполнитель',
    DueDate: 'Срок',
    Quantity: 'Количество',
    UnitPrice: 'Цена',
    TotalAmount: 'Сумма',
    SortOrder: 'Порядок',
  },
  'client-case-related-party': {
    DisplayName: 'Участник',
    Role: 'Роль в деле',
    SourceType: 'Тип участника',
    ClientId: 'Физическое лицо',
    LegalEntityId: 'Юридическое лицо',
    ExternalContactId: 'Контакт',
    Phone: 'Телефон',
    Email: 'Email',
    Notes: 'Комментарий',
    IsArchived: 'Архивная связь',
    ArchivedAt: 'Дата архивирования',
    ArchivedByUserId: 'Кто архивировал',
    ArchivedByUserName: 'Кто архивировал',
    ArchiveReason: 'Причина архивирования',
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
    InvoiceWorkflowStatus: 'Статус документа',
    WorkflowStatus: 'Статус документа',
    Type: 'Тип счета',
  },
  'invoice-line-item': {
    Name: 'Наименование позиции',
    Description: 'Описание позиции',
    PaymentId: 'Счет',
    ServiceId: 'Услуга',
    ServiceName: 'Услуга',
    SortOrder: 'Порядок строки',
    SourceKey: 'Источник строки',
    SourceSortOrder: 'Порядок в источнике',
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
    Result: 'Результат задачи',
    Priority: 'Приоритет',
    DueAt: 'Срок выполнения',
    AssigneeUserId: 'Исполнитель',
    AssigneeName: 'Исполнитель',
    AuthorUserId: 'Автор',
    ChannelsJson: 'Каналы уведомлений',
  },
  'calendar-sync': {
    SyncMode: 'Главный источник событий',
    ConflictResolutionMode: 'Правило спорных изменений',
    RequireManualConfirmation: 'Ручное подтверждение конфликтов',
    DryRunConfirmed: 'Dry-run на копии базы',
    Notes: 'Комментарий к решению',
  },
};

const OBJECT_TYPE_LABELS: Record<string, string> = {
  client: 'Клиент',
  'client-case': 'Дело',
  'client-case-document': 'Документ дела',
  'calendar-sync': 'Синхронизация календаря',
  'client-case-work-order': 'Заказ-наряд',
  'client-case-work-order-item': 'Услуга заказ-наряда',
  'client-case-related-party': 'Связанное лицо дела',
  payment: 'Платеж',
  invoice: 'Счет',
  'invoice-line-item': 'Позиция счета',
  act: 'Акт',
  contract: 'Договор',
  'cash-order': 'Кассовый ордер',
  'cash-inventory': 'Инвентаризация кассы',
  'legal-entity': 'Юрлицо',
  contact: 'Контакт',
  address: 'Адрес',
  'client-passport': 'Паспорт клиента',
  'power-of-attorney': 'Доверенность',
  'money-operation': 'Денежная операция',
  user: 'Пользователь',
  role: 'Роль',
  'task-journal-item': 'Задача',
};

const AUDIT_ACTOR_LABELS: Record<string, string> = {
  'codex data repair': 'Администратор',
  'codex maintenance': 'Администратор',
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
  partiallypaid: 'Частично оплачен',
  draft: 'Черновик',
  pendingapproval: 'На согласовании',
  revision: 'На доработке',
  invoicecreated: 'Счет создан',
  client: 'Физическое лицо',
  legalentity: 'Юридическое лицо',
  externalcontact: 'Контакт',
  signed: 'Подписан',
  signedbyclient: 'Подписан клиентом',
  notsigned: 'Не подписан',
  requiresclientsignature: 'Требуется подпись клиента',
  onapproval: 'На согласовании',
  approved: 'Согласован',
  approvalrejected: 'Отказ в согласовании',
  needscorrection: 'Требует исправления',
  onsigning: 'На подписании',
  created: 'Создан',
  transferred: 'Передан',
  concluded: 'Подписан',
  terminated: 'Расторгнут',
  open: 'Открыто',
  closed: 'Закрыто',
  active: 'Активен',
  inactive: 'Неактивен',
  notconfigured: 'Не выбрано',
  crmmaster: 'CRM главная',
  externalmaster: 'Внешний календарь главный',
  hybrid: 'Гибридная модель',
  manualconfirmation: 'Ручное подтверждение',
  crmwins: 'Оставлять версию CRM',
  externalwins: 'Принимать внешний календарь',
  latestwins: 'Брать последнее изменение',
};

export interface ParsedAuditFieldChange {
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

interface ParsedAuditMetadataObject {
  changedFields?: unknown;
  changes?: unknown;
  reason?: unknown;
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
    .replace(/\bCodex data repair\b/gi, 'Администратор')
    .replace(/\bCodex maintenance\b/gi, 'Администратор')
    .replace(/\bCodex\b/gi, 'Администратор')
    .replace(/\bSystem Notes\b/gi, 'Служебные заметки')
    .replace(/\bSystemNotes\b/gi, 'Служебные заметки')
    .replace(/\bInvoice Workflow Status\b/gi, 'Статус документа')
    .replace(/\bPayment Status\b/gi, 'Статус платежа')
    .replace(/\bClient Case Reference\b/gi, 'Дело')
    .replace(/\bDataRepair\b/gi, 'Исправление данных')
    .replace(/\bTask Journal Item\b/gi, 'Задача')
    .replace(/\btask journal item\b/gi, 'Задача')
    .replace(/\bupdated\b/gi, 'изменил')
    .replace(/\bcreated\b/gi, 'создал')
    .replace(/\bdeleted\b/gi, 'удалил')
    .replace(/\brestored\b/gi, 'восстановил');
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
    case 'downloaded':
      return 'Скачивание';
    case 'exported':
      return 'Экспорт';
    case 'restored':
      return 'Восстановление';
    case 'automation':
      return 'Автоконтроль';
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
    case 'downloaded':
      return 'border-cyan-200 bg-cyan-50 text-cyan-700';
    case 'exported':
      return 'border-indigo-200 bg-indigo-50 text-indigo-700';
    case 'restored':
      return 'border-teal-200 bg-teal-50 text-teal-700';
    case 'automation':
      return 'border-amber-200 bg-amber-50 text-amber-700';
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

  if (/\bcodex\b/iu.test(normalizedActor)) {
    return 'Администратор';
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

  return translateTechnicalWords(prettifyFieldName(fieldName));
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

export function parseAuditReason(metadataJson: string | null | undefined): string | null {
  if (!metadataJson?.trim()) {
    return null;
  }

  try {
    const parsed = JSON.parse(metadataJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return null;
    }

    const reason = (parsed as ParsedAuditMetadataObject).reason;
    return typeof reason === 'string' && reason.trim().length > 0 ? reason.trim() : null;
  } catch {
    return null;
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
