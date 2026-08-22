import type { UserActivityLogItem } from '../types/userActivity';
import { fromTimeZoneDateTimeInput } from './timeZoneDate';

const CATEGORY_LABELS: Record<string, string> = {
  Accounts: 'Счета',
  Acts: 'Акты',
  AdminUsers: 'Пользователи',
  AppVersion: 'Версия и обновления',
  Auth: 'Авторизация',
  AuthProvidersAdmin: 'Провайдеры входа',
  Banks: 'Банки',
  CalendarSync: 'Синхронизация календаря',
  Cases: 'Дела',
  CasesV1: 'Дела (старый интерфейс)',
  CasesV2: 'Дела (новый интерфейс)',
  CashOrders: 'Кассовые ордера',
  ClientBalances: 'Взаиморасчёты клиентов',
  ClientPassport: 'Паспортные данные клиентов',
  ClientPortal: 'Личный кабинет клиента',
  ClientPortalAdmin: 'Доступ клиентов',
  ClientReferenceData: 'Справочники клиентов',
  Clients: 'Клиенты',
  ClientsV1: 'Клиенты (старый интерфейс)',
  ClientsV2: 'Клиенты (новый интерфейс)',
  Contacts: 'Контакты',
  Contracts: 'Договоры',
  DadataIntegrationSettings: 'Подсказки адресов',
  DatabaseAdmin: 'Обслуживание базы данных',
  DictionariesAdmin: 'Справочники',
  DictionariesRead: 'Справочники',
  ElectronicSignatures: 'Электронная подпись',
  ExternalAuth: 'Внешняя авторизация',
  FeatureFlags: 'Функции системы',
  FormRequirements: 'Правила заполнения форм',
  GoogleCalendar: 'Google Календарь',
  Health: 'Состояние системы',
  Installments: 'Рассрочка',
  Invoices: 'Счета',
  LegalEntities: 'Юридические лица',
  LegalEntityBalances: 'Взаиморасчёты организаций',
  MaxMessenger: 'MAX',
  MessengerDocumentDeliveries: 'Доставка документов',
  MoneyAccounts: 'Денежные счета',
  MoneyOperations: 'Денежные операции',
  NavigationSettings: 'Настройка меню',
  NotificationInbox: 'Уведомления',
  Notifications: 'Настройки уведомлений',
  ObjectAudit: 'Аудит объектов',
  Payments: 'Платежи',
  PaymentsV1: 'Платежи (старый интерфейс)',
  PaymentsV2: 'Платежи (новый интерфейс)',
  PowerOfAttorney: 'Доверенности',
  Profile: 'Профиль',
  PublicWorkOrderApproval: 'Согласование заказ-наряда',
  PushSubscriptions: 'Push-уведомления',
  Reports: 'Отчёты',
  RolePermissions: 'Права ролей',
  Roles: 'Роли',
  S3Storage: 'S3-хранилища',
  Stats: 'Статистика',
  StatsV2: 'Статистика',
  StickyNotes: 'Заметки',
  SystemJobs: 'Фоновые сервисы',
  Tasks: 'Задачи',
  UserActivity: 'Контроль пользователей',
  Users: 'Пользователи',
  UserSettings: 'Пользовательские настройки',
  YandexCalendar: 'Яндекс Календарь',
  YandexDisk: 'Яндекс Диск',
  navigation: 'Навигация',
};

const ACTION_LABELS: Record<string, string> = {
  Get: 'Просмотр',
  GetAll: 'Просмотр списка',
  GetById: 'Просмотр карточки',
  GetPaged: 'Просмотр списка',
  GetSummary: 'Просмотр сводки',
  GetStats: 'Просмотр статистики',
  GetSettings: 'Просмотр настроек',
  GetNotifications: 'Просмотр уведомлений',
  BaseCurrencies: 'Базовые валюты',
  ClientStatuses: 'Статусы клиентов',
  Countries: 'Страны',
  DealTypes: 'Типы сделок',
  Departments: 'Отделы',
  DocumentStatuses: 'Статусы документов',
  IncomeTypes: 'Типы доходов',
  InvoiceItemNames: 'Наименования позиций счёта',
  InvoiceUnits: 'Единицы измерения',
  OrganizationForms: 'Организационно-правовые формы',
  PaymentSources: 'Источники платежей',
  PaymentStatuses: 'Статусы платежей',
  ServiceCategories: 'Категории услуг',
  ServiceLanguages: 'Языки услуг',
  Services: 'Услуги',
  TaxSystemModes: 'Режимы налогообложения',
  TaxSystems: 'Системы налогообложения',
  Timezones: 'Часовые пояса',
  Summary: 'Просмотр сводки',
  Create: 'Создание',
  Post: 'Создание',
  Update: 'Изменение',
  Put: 'Изменение',
  Upsert: 'Сохранение',
  Delete: 'Удаление',
  BulkDelete: 'Массовое удаление',
  Cancel: 'Отмена',
  Copy: 'Копирование',
  Print: 'Печать',
  Download: 'Скачивание',
  Upload: 'Загрузка',
  Import: 'Импорт',
  Export: 'Экспорт',
  Search: 'Поиск',
  Login: 'Вход',
  Logout: 'Выход',
  Register: 'Регистрация',
  RefreshSession: 'Обновление сеанса',
  Me: 'Проверка сеанса',
  open_tab: 'Открытие раздела',
  heartbeat: 'Активный сеанс',
};

const SECTION_LABELS: Record<string, string> = {
  userActivity: 'Контроль пользователей',
  settings: 'Настройки',
  session: 'Сеанс пользователя',
  navigation: 'Навигация',
};

const ACTION_PREFIX_LABELS: Array<[RegExp, string]> = [
  [/^Get|^List|^Preview|^Home$/u, 'Просмотр данных'],
  [/^Create|^Add/u, 'Создание'],
  [/^Update|^Save|^Upsert|^Change|^Toggle|^Mark/u, 'Изменение'],
  [/^Delete|^Remove|^Revoke/u, 'Удаление'],
  [/^Download/u, 'Скачивание'],
  [/^Upload/u, 'Загрузка'],
  [/^Import/u, 'Импорт'],
  [/^Export/u, 'Экспорт'],
  [/^Print/u, 'Печать'],
  [/^Send/u, 'Отправка'],
  [/^Sync/u, 'Синхронизация'],
  [/^Run|^Apply|^Retry/u, 'Запуск операции'],
  [/^Approve/u, 'Одобрение'],
  [/^Reject/u, 'Отклонение'],
  [/^Cancel|^Terminate/u, 'Отмена'],
  [/^Restore/u, 'Восстановление'],
];

export function getUserActivityCategoryLabel(value: string | null | undefined) {
  if (!value) return '—';
  return CATEGORY_LABELS[value] ?? value;
}

export function getUserActivityActionLabel(value: string | null | undefined) {
  if (!value) return '—';
  const exact = ACTION_LABELS[value];
  if (exact) return exact;
  return ACTION_PREFIX_LABELS.find(([pattern]) => pattern.test(value))?.[1] ?? value;
}

export function getUserActivitySectionLabel(value: string | null | undefined) {
  if (!value) return '—';
  return SECTION_LABELS[value] ?? CATEGORY_LABELS[value] ?? value;
}

export function formatUserActivityFilterOption(value: string, kind: 'category' | 'action' | 'section') {
  const label = kind === 'category'
    ? getUserActivityCategoryLabel(value)
    : kind === 'action'
      ? getUserActivityActionLabel(value)
      : getUserActivitySectionLabel(value);
  return label === value ? value : `${label} — ${value}`;
}

export function formatUserActivityDescription(log: Pick<
  UserActivityLogItem,
  'category' | 'action' | 'description' | 'objectId'
>) {
  const description = log.description?.trim();
  if (!description) return '—';

  const technicalPrefix = `${log.category}.${log.action}`;
  if (!description.startsWith(technicalPrefix)) return description;

  const objectSuffix = log.objectId ? ` · объект №${log.objectId}` : '';
  return `${getUserActivityActionLabel(log.action)}: ${getUserActivityCategoryLabel(log.category)}${objectSuffix}`;
}

export function buildUserActivityDateRange(
  from: string,
  to: string,
  timeZoneId?: string | null,
) {
  return {
    fromUtc: from ? fromTimeZoneDateTimeInput(from, timeZoneId) : undefined,
    toUtc: to ? fromTimeZoneDateTimeInput(to, timeZoneId) : undefined,
  };
}

export function getUserActivitySearchAliases(
  search: string,
  options: {
    categories?: readonly string[];
    actions?: readonly string[];
    sections?: readonly string[];
  },
) {
  const term = search.trim().toLocaleLowerCase('ru-RU');
  if (!term) return [];

  const matches = [
    ...(options.categories ?? []).map((value) => ({ value, label: getUserActivityCategoryLabel(value) })),
    ...(options.actions ?? []).map((value) => ({ value, label: getUserActivityActionLabel(value) })),
    ...(options.sections ?? []).map((value) => ({ value, label: getUserActivitySectionLabel(value) })),
  ]
    .filter(({ label }) => {
      const normalizedLabel = label.toLocaleLowerCase('ru-RU');
      return normalizedLabel.includes(term) || term.includes(normalizedLabel);
    })
    .map(({ value }) => value);

  return [...new Set(matches)].slice(0, 50);
}
