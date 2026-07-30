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
  Priority: 'Приоритет',
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
  ClientStatusId: 'Статус клиента',
  ClientStatusName: 'Статус клиента',
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
  SignedByClientAt: 'Дата подписи клиента',
  ConcludedAt: 'Дата заключения',
  TerminatedAt: 'Дата расторжения',
  TerminationReason: 'Причина расторжения',
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
  AdvanceReviewStatus: 'Контроль аванса',
  AdvanceReviewReason: 'Причина решения по авансу',
  AdvanceReviewedAt: 'Дата решения по авансу',
  AdvanceReviewedByUserId: 'Кто принял решение по авансу',
  AdvanceReviewedByUserName: 'Кто принял решение по авансу',
  DocumentStatusId: 'Статус документа',
  DocumentStatusName: 'Статус документа',
  DueDate: 'Срок оплаты',
  DueAt: 'Срок',
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
  LastActivityAction: 'Последнее действие',
  LastActivityAt: 'Время последней активности',
  LastActivitySection: 'Последний раздел',
  LastActivityDescription: 'Описание последней активности',
  LastLoginAt: 'Последний вход',
  LastViewedAt: 'Последний просмотр',
  LastViewedByUserId: 'Кто просматривал',
  LastViewedByUserName: 'Кто просматривал',
  LastViewedByResponsibleAt: 'Последний просмотр ответственным',
  LastViewedByResponsibleUserId: 'Ответственный, который просматривал',
  LastViewedByResponsibleUserName: 'Ответственный, который просматривал',
  ActivityConfirmedAt: 'Активность подтверждена',
  ActivityConfirmedByUserId: 'Кто подтвердил активность',
  ActivityConfirmedByUserName: 'Кто подтвердил активность',
  InactivityRemindersEnabled: 'Автоконтроль',
  IsUnderControl: 'На контроле',
  ControlMarkedAt: 'Дата постановки на контроль',
  ControlMarkedByUserId: 'Кто поставил на контроль',
  ControlMarkedByUserName: 'Кто поставил на контроль',
  IsHiddenFromExecutor: 'Скрыто от исполнителя',
  HiddenFromExecutorAt: 'Дата скрытия от исполнителя',
  HiddenFromExecutorByUserId: 'Кто изменил видимость для исполнителя',
  HiddenFromExecutorByUserName: 'Кто изменил видимость для исполнителя',
  HiddenFromExecutorReason: 'Причина скрытия от исполнителя',
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
    Priority: 'Приоритет дела',
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
  's3-storage': {
    Name: 'Название подключения',
    Provider: 'Провайдер',
    Endpoint: 'Endpoint',
    Region: 'Регион',
    Bucket: 'Bucket',
    RootPath: 'Рабочая папка',
    AccessKey: 'Access Key',
    SecretKey: 'Secret Key',
    IsEnabled: 'Подключение включено',
  },
  's3 storage': {
    Name: 'Название подключения',
    Provider: 'Провайдер',
    Endpoint: 'Endpoint',
    Region: 'Регион',
    Bucket: 'Bucket',
    RootPath: 'Рабочая папка',
    AccessKey: 'Access Key',
    SecretKey: 'Secret Key',
    IsEnabled: 'Подключение включено',
  },
  'yandex-disk': {
    RootPath: 'Рабочая папка',
    OAuthToken: 'OAuth-токен',
    IsConfigured: 'Подключение настроено',
  },
  'yandex disk': {
    RootPath: 'Рабочая папка',
    OAuthToken: 'OAuth-токен',
    IsConfigured: 'Подключение настроено',
  },
  'task-journal-item': {
    Title: 'Заголовок задачи',
    Description: 'Описание задачи',
    Status: 'Статус задачи',
    Type: 'Тип задачи',
    Result: 'Результат задачи',
    Priority: 'Приоритет',
    DueAt: 'Срок выполнения',
    ApprovalDueAt: 'Срок согласования',
    PlannedDurationMinutes: 'Плановая длительность',
    AssigneeUserId: 'Исполнитель',
    AssigneeName: 'Исполнитель',
    AuthorUserId: 'Автор',
    AuthorName: 'Автор',
    IsUnderControl: 'На контроле',
    ControlMarkedAt: 'Дата постановки на контроль',
    ControlMarkedByUserId: 'Кто поставил на контроль',
    ControlMarkedByUserName: 'Кто поставил на контроль',
    ChannelsJson: 'Каналы уведомлений',
    RemindersJson: 'Напоминания',
    ContractorPaymentId: 'Платеж подрядчику',
    ContractorPaymentEnabled: 'Платеж подрядчику включен',
    ContractorPaymentAmount: 'Сумма платежа подрядчику',
    ContractorPaymentDueDate: 'Срок платежа подрядчику',
    ContractorPaymentOnResult: 'Платеж подрядчику по результату',
    ContractorPaymentResultDelayDays: 'Отсрочка платежа подрядчику',
  },
  'calendar-sync': {
    ExternalCalendarSyncEnabled: 'Внешняя синхронизация календарей',
    GoogleCalendarSyncEnabled: 'Синхронизация Google Calendar',
    YandexCalendarSyncEnabled: 'Синхронизация Яндекс.Календаря',
    SyncMode: 'Главный источник событий',
    ConflictResolutionMode: 'Правило спорных изменений',
    RequireManualConfirmation: 'Ручное подтверждение конфликтов',
    DryRunConfirmed: 'Dry-run на копии базы',
    Notes: 'Комментарий к решению',
  },
  'notification-settings': {
    SmsEnabled: 'SMS-канал включён',
    SmsProvider: 'SMS-провайдер',
    SmsSenderName: 'Имя отправителя SMS',
    SmsApiToken: 'API-токен SMS',
    SmsDailyLimit: 'SMS за 24 часа',
    SmsRecipientDailyLimit: 'SMS получателю за 24 часа',
    SmsBurstLimitPerMinute: 'SMS в минуту',
    SmsMaxMessageLength: 'Максимальная длина SMS',
    SmsLowBalanceThreshold: 'Порог низкого баланса',
    SmsEmergencyStop: 'Аварийная остановка SMS',
    SmsDisabledEvents: 'Отключённые SMS-события',
    SmsQuietHoursEnabled: 'Тихие часы SMS',
    SmsQuietHoursStart: 'Начало тихих часов',
    SmsQuietHoursEnd: 'Окончание тихих часов',
    SmsQuietHoursTimeZone: 'Часовой пояс тихих часов',
    SmsTemplateEnabled: 'SMS-шаблон включён',
    SmsTemplateSubject: 'Тема SMS-шаблона',
    SmsTemplateBody: 'Текст SMS-шаблона',
  },
};

const OBJECT_TYPE_LABELS: Record<string, string> = {
  client: 'Клиент',
  'client-case': 'Дело',
  'client-case-document': 'Документ дела',
  'calendar-sync': 'Синхронизация календаря',
  'notification-settings': 'Настройки уведомлений',
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
  's3-storage': 'S3-хранилище',
  's3 storage': 'S3-хранилище',
  'yandex-disk': 'Я.Диск',
  'yandex disk': 'Я.Диск',
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
  none: 'Без контроля аванса',
  requiresdecision: 'Требует решения по авансу',
  acceptedasadvance: 'Принят как аванс',
  allocated: 'Распределен',
  ignored: 'Исключен из контроля',
  refunded: 'Возвращен клиенту',
  draft: 'Черновик',
  pendingapproval: 'На согласовании',
  revision: 'На доработке',
  invoicecreated: 'Счет создан',
  client: 'Физическое лицо',
  legalentity: 'Юридическое лицо',
  externalcontact: 'Контакт',
  signed: 'Подписан клиентом',
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
  concluded: 'Подписан клиентом',
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
  inprogress: 'В работе',
  done: 'Выполнена',
  new: 'Новая',
  waiting: 'Ожидает',
  task: 'Задача',
  courthearing: 'Судебное заседание',
  opentab: 'Открытие раздела',
  login: 'Вход в систему',
  settings: 'Настройки',
  auth: 'Авторизация',
};

const FIELD_VALUE_LABELS: Record<string, Record<string, string>> = {
  priority: {
    low: 'Низкий',
    normal: 'Обычный',
    high: 'Высокий',
    urgent: 'Срочно',
  },
  status: {
    inprogress: 'В работе',
    done: 'Выполнена',
    new: 'Новая',
    waiting: 'Ожидает',
    cancelled: 'Отменена',
    canceled: 'Отменена',
    onapproval: 'На согласовании',
  },
};

const BOOLEAN_FIELD_VALUE_LABELS: Record<string, { true: string; false: string }> = {
  isundercontrol: {
    true: 'На контроле',
    false: 'Без контроля',
  },
  inactivityremindersenabled: {
    true: 'Автоконтроль включен',
    false: 'Автоконтроль выключен',
  },
  ishiddenfromexecutor: {
    true: 'Скрыто от исполнителя',
    false: 'Открыто исполнителю',
  },
  isdeleted: {
    true: 'Удалено',
    false: 'Активно',
  },
  isactive: {
    true: 'Активно',
    false: 'Неактивно',
  },
};

const JSON_ARRAY_VALUE_LABELS: Record<string, Record<string, string>> = {
  channelsjson: {
    inapp: 'В системе',
    email: 'Email',
    telegram: 'Telegram',
  },
  remindersjson: {
    before7d: 'За 7 дней',
    before3d: 'За 3 дня',
    before1d: 'За день',
    before1h: 'За час',
    before15m: 'За 15 минут',
    atdue: 'В срок',
    dailyoverdue: 'Каждый день просрочки',
  },
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

function formatDateValue(value: string, timeZoneId?: string | null): string {
  const match = value.trim().match(/^(\d{4})-(\d{2})-(\d{2})(?:[T\s](\d{2}):(\d{2})(?::(\d{2}))?)?/u);
  if (!match) return value.trim();

  const [, year, month, day, hour, minute, second] = match;
  const hasExplicitOffset = /(?:Z|[+-]\d{2}:?\d{2})$/u.test(value.trim());
  if (hasExplicitOffset && hour && (hour !== '00' || minute !== '00' || (second && second !== '00'))) {
    return formatDateTimeInTimeZone(value, timeZoneId) || value.trim();
  }

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

function getFieldValueLabel(value: string, fieldName?: string | null): string | null {
  const fieldLookupKey = normalizeLookupKey(fieldName ?? '');
  if (!fieldLookupKey) return null;

  const valueLookupKey = normalizeLookupKey(value);
  const directLabels = FIELD_VALUE_LABELS[fieldLookupKey];
  if (directLabels?.[valueLookupKey]) {
    return directLabels[valueLookupKey];
  }

  return null;
}

function getBooleanFieldValueLabel(value: string, fieldName?: string | null): string | null {
  const fieldLookupKey = normalizeLookupKey(fieldName ?? '');
  const labels = BOOLEAN_FIELD_VALUE_LABELS[fieldLookupKey];
  if (!labels) return null;

  const valueLookupKey = normalizeLookupKey(value);
  if (valueLookupKey === 'true' || valueLookupKey === '1') return labels.true;
  if (valueLookupKey === 'false' || valueLookupKey === '0') return labels.false;

  return null;
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

function formatJsonValue(value: string, fieldName?: string | null): string | null {
  const trimmed = value.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return null;

  try {
    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) {
      if (parsed.length === 0) return 'Пустой список';

      const fieldLookupKey = normalizeLookupKey(fieldName ?? '');
      const arrayLabels = JSON_ARRAY_VALUE_LABELS[fieldLookupKey];
      if (arrayLabels && parsed.every((item) => typeof item === 'string')) {
        return parsed
          .map((item) => arrayLabels[normalizeLookupKey(item)] ?? translateTechnicalWords(item))
          .join(', ');
      }

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
    .replace(/\bConnectionCreated\b/gi, 'Подключение создано')
    .replace(/\bConnectionUpdated\b/gi, 'Подключение обновлено')
    .replace(/\bConnectionTested\b/gi, 'Подключение проверено')
    .replace(/\bObjectCreated\b/gi, 'Объект создан')
    .replace(/\bObjectUploaded\b/gi, 'Объект загружен')
    .replace(/\bObjectDeleted\b/gi, 'Объект удален')
    .replace(/\bObjectCopied\b/gi, 'Объект скопирован')
    .replace(/\bObjectMoved\b/gi, 'Объект перемещен')
    .replace(/\bObjectDownloaded\b/gi, 'Объект скачан')
    .replace(/\bTextFileUpdated\b/gi, 'Текстовый файл обновлен')
    .replace(/\bfor s3 storage\b/gi, 'для S3-хранилища')
    .replace(/\bfor s3-storage\b/gi, 'для S3-хранилища')
    .replace(/\bfor yandex disk\b/gi, 'для Я.Диска')
    .replace(/\bfor yandex-disk\b/gi, 'для Я.Диска')
    .replace(/\bfor\b/gi, 'для')
    .replace(/\bs3 storage\b/gi, 'S3-хранилище')
    .replace(/\bs3-storage\b/gi, 'S3-хранилище')
    .replace(/\byandex disk\b/gi, 'Я.Диск')
    .replace(/\byandex-disk\b/gi, 'Я.Диск')
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
    case 'connectioncreated':
      return 'Создание подключения';
    case 'connectionupdated':
      return 'Обновление подключения';
    case 'connectiontested':
      return 'Проверка подключения';
    case 'objectcreated':
      return 'Создание объекта';
    case 'objectuploaded':
      return 'Загрузка объекта';
    case 'objectdeleted':
      return 'Удаление объекта';
    case 'objectcopied':
      return 'Копирование объекта';
    case 'objectmoved':
      return 'Перемещение объекта';
    case 'objectdownloaded':
      return 'Скачивание объекта';
    case 'textfileupdated':
      return 'Редактирование файла';
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
    case 'objectdownloaded':
      return 'border-cyan-200 bg-cyan-50 text-cyan-700';
    case 'connectioncreated':
    case 'objectcreated':
    case 'objectuploaded':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'connectionupdated':
    case 'connectiontested':
    case 'objectcopied':
    case 'objectmoved':
    case 'textfileupdated':
      return 'border-blue-200 bg-blue-50 text-blue-700';
    case 'objectdeleted':
      return 'border-rose-200 bg-rose-50 text-rose-700';
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
  timeZoneId?: string | null,
): string {
  void objectType;
  const trimmed = value?.trim();
  if (!trimmed) return '—';

  const fieldValueLabel = getFieldValueLabel(trimmed, fieldName);
  if (fieldValueLabel) {
    return fieldValueLabel;
  }

  const booleanFieldValueLabel = getBooleanFieldValueLabel(trimmed, fieldName);
  if (booleanFieldValueLabel) {
    return booleanFieldValueLabel;
  }

  const normalizedValueKey = normalizeLookupKey(trimmed);
  if (VALUE_LABELS[normalizedValueKey]) {
    return VALUE_LABELS[normalizedValueKey];
  }

  if (shouldFormatAsBoolean(fieldName)) {
    if (trimmed === '1') return 'Да';
    if (trimmed === '0') return 'Нет';
  }

  if (looksLikeDate(trimmed)) {
    return formatDateValue(trimmed, timeZoneId);
  }

  if (shouldFormatAsMoney(fieldName)) {
    return formatMoneyLikeValue(trimmed) ?? trimmed;
  }

  if (shouldFormatAsIdentifier(fieldName) && /^-?\d+$/u.test(trimmed)) {
    return `ID ${trimmed}`;
  }

  return formatJsonValue(trimmed, fieldName) ?? translateTechnicalWords(trimmed);
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
import { formatDateTimeInTimeZone } from './timeZoneDate';
