import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { Bell, CalendarDays, ClipboardList, Cloud, Database, Flag, History, KeyRound, Palette, Save, Scale, ShieldCheck, SlidersHorizontal, Users } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { TablePagination } from '../ui/TablePagination';
import { BankDirectorySettings } from './BankDirectorySettings';
import { DatabaseMaintenanceSettings } from './DatabaseMaintenanceSettings';
import { CaseDocumentTemplateSettings } from './CaseDocumentTemplateSettings';
import { CalendarSyncProviderSettings } from './CalendarSyncProviderSettings';
import { ContractDocumentTemplateSettings } from './ContractDocumentTemplateSettings';
import { ActDocumentTemplateSettings } from './ActDocumentTemplateSettings';
import { CourtSyncJournalSettings } from './CourtSyncJournalSettings';
import { CourtSyncSchedulerSettings } from './CourtSyncSchedulerSettings';
import { FeatureFlagSettings } from './FeatureFlagSettings';
import { AppVersionSettings } from './AppVersionSettings';
import { MainCompanySettings } from './MainCompanySettings';
import { MoneyAccountSettings } from './MoneyAccountSettings';
import { ProceduralDeadlineCalendarSettings } from './ProceduralDeadlineCalendarSettings';
import { TaskJournalSettings } from './TaskJournalSettings';
import { UserOnlineSettings } from './UserOnlineSettings';
import { UserMergeSettings } from './UserMergeSettings';
import { AppearanceSettings } from './AppearanceSettings';
import { S3Storage } from '../S3Storage/S3Storage';
import { YandexDisk } from '../YandexDisk/YandexDisk';
import type {
  ContractNumberingSettingsDto,
  ContractRulesSettingsDto,
  DadataIntegrationSettingsDto,
  DatabaseImportResultDto,
  DatabaseStatusDto,
  FormRequirementSectionMetadataDto,
  InvoiceLegacyMigrationSummaryDto,
  InvoiceNumberingSettingsDto,
  InvoiceRulesSettingsDto,
  LegacyAddressMigrationResult,
} from '../../services/api';

const AuthProviderSettings = lazy(() => import('./AuthProviderSettings')
  .then((module) => ({ default: module.AuthProviderSettings })));
const InvoiceDocumentTemplateSettings = lazy(() => import('./InvoiceDocumentTemplateSettings')
  .then((module) => ({ default: module.InvoiceDocumentTemplateSettings })));
const NotificationSettings = lazy(() => import('./NotificationSettings')
  .then((module) => ({ default: module.NotificationSettings })));
const SpreadsheetImportSettings = lazy(() => import('./SpreadsheetImportSettings')
  .then((module) => ({ default: module.SpreadsheetImportSettings })));

function SettingsPanelLoading() {
  return <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-500">Загрузка настроек…</div>;
}

type SettingsSection =
  | 'companies'
  | 'tasks'
  | 'appearance'
  | 'general'
  | 'integrations'
  | 'notifications'
  | 'yandex-disk'
  | 's3-storage'
  | 'users-online'
  | 'app-releases'
  | 'auth-providers'
  | 'db-maintenance'
  | 'db-import'
  | 'db-operations'
  | 'form-requirements';
type FinancialRulesSubsection = 'invoices' | 'acts' | 'contracts' | 'case-documents';
type DatabaseOperationsSubsection = 'invoices' | 'addresses' | 'dictionary-merge' | 'users-merge';
type IntegrationSettingsTab = 'dadata' | 'feature-flags' | 'calendars' | 'courts';
type MergeKind = 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes';
const DETAILS_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const SHOW_LEGACY_SQLITE_IMPORT = false;
const SETTINGS_SECTION_PARAM = 'settingsSection';
const NOTIFICATION_CHANNEL_PARAM = 'notificationChannel';
const NOTIFICATION_SENDER_PARAM = 'notificationSender';
const SETTINGS_NAV_ITEM_CLASS = 'pp-settings-nav-item w-full rounded-xl border px-3 py-3 text-left text-sm font-semibold transition';
const settingsNavItemClass = (isActive: boolean) =>
  `${SETTINGS_NAV_ITEM_CLASS} ${isActive ? 'pp-settings-nav-item-active shadow-sm' : 'border-transparent'}`;
const SETTINGS_SECTIONS: SettingsSection[] = [
  'companies',
  'tasks',
  'appearance',
  'general',
  'integrations',
  'notifications',
  'yandex-disk',
  's3-storage',
  'users-online',
  'app-releases',
  'auth-providers',
  'db-maintenance',
  'db-import',
  'db-operations',
  'form-requirements',
];

const INTEGRATION_SETTINGS_TABS: Array<{
  id: IntegrationSettingsTab;
  label: string;
  description: string;
  icon: LucideIcon;
}> = [
  {
    id: 'dadata',
    label: 'DaData',
    description: 'Ключи подсказок, нормализации и проверок реквизитов.',
    icon: KeyRound,
  },
  {
    id: 'feature-flags',
    label: 'Юридический контур',
    description: 'Включатели доступности внешних юридических механизмов.',
    icon: Flag,
  },
  {
    id: 'calendars',
    label: 'Календари',
    description: 'Google Calendar, Яндекс Календарь и правила синхронизации.',
    icon: CalendarDays,
  },
  {
    id: 'courts',
    label: 'Суды',
    description: 'Расписание судебной синхронизации и журнал проверок.',
    icon: Scale,
  },
];

function isSettingsSection(value: string | null): value is SettingsSection {
  return SETTINGS_SECTIONS.includes(value as SettingsSection);
}

function readSettingsSectionFromUrl(): SettingsSection {
  if (typeof window === 'undefined') {
    return 'general';
  }

  const section = new URLSearchParams(window.location.search).get(SETTINGS_SECTION_PARAM);
  return isSettingsSection(section) ? section : 'general';
}

function pushSettingsSectionToUrl(section: SettingsSection) {
  if (typeof window === 'undefined') return;

  const url = new URL(window.location.href);
  url.searchParams.set(SETTINGS_SECTION_PARAM, section);
  url.searchParams.delete(NOTIFICATION_CHANNEL_PARAM);
  url.searchParams.delete(NOTIFICATION_SENDER_PARAM);

  window.history.pushState(null, '', `${url.pathname}${url.search}${url.hash}`);
}

type AppSettingsProps = {
  initialSection?: SettingsSection;
};

export function AppSettings({ initialSection }: AppSettingsProps = {}) {
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const { confirm } = useDialog();
  const toast = useToast();
  const timeZoneId = useUserTimeZone();
  const [activeSection, setActiveSection] = useState<SettingsSection>(() => initialSection ?? readSettingsSectionFromUrl());
  const [activeFinancialRulesSubsection, setActiveFinancialRulesSubsection] = useState<FinancialRulesSubsection>('invoices');
  const [activeDatabaseOperationsSubsection, setActiveDatabaseOperationsSubsection] = useState<DatabaseOperationsSubsection>('invoices');
  const [activeIntegrationTab, setActiveIntegrationTab] = useState<IntegrationSettingsTab>('dadata');
  const [loading, setLoading] = useState(false);
  const [validateWithDadata, setValidateWithDadata] = useState(true);
  const [result, setResult] = useState<LegacyAddressMigrationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dadataToken, setDadataToken] = useState('');
  const [dadataSecret, setDadataSecret] = useState('');
  const [dadataSettings, setDadataSettings] = useState<DadataIntegrationSettingsDto | null>(null);
  const [credentialsLoading, setCredentialsLoading] = useState(false);
  const [credentialsMessage, setCredentialsMessage] = useState<string | null>(null);
  const [mergeKind, setMergeKind] = useState<MergeKind>('organization-forms');
  const [duplicates, setDuplicates] = useState<Array<{ canonical: string; items: Array<{ id: number; name: string; description: string; colorHex: string; isActive: boolean }> }>>([]);
  const [mergeLoading, setMergeLoading] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [mergeMessage, setMergeMessage] = useState<string | null>(null);
  const [selectedGroupKey, setSelectedGroupKey] = useState<string>('');
  const [preferredId, setPreferredId] = useState<number | null>(null);
  const [requiredFieldKeys, setRequiredFieldKeys] = useState<Set<string>>(new Set());
  const [formRequirementSections, setFormRequirementSections] = useState<FormRequirementSectionMetadataDto[]>([]);
  const [selectedRequirementsSection, setSelectedRequirementsSection] = useState<string>('');
  const [requirementsLoading, setRequirementsLoading] = useState(false);
  const [requirementsMessage, setRequirementsMessage] = useState<string | null>(null);
  const [invoiceNumbering, setInvoiceNumbering] = useState<InvoiceNumberingSettingsDto>({ startNumber: 1, allocationStrategy: 'max-plus-one' });
  const [invoiceNumberingLoading, setInvoiceNumberingLoading] = useState(false);
  const [invoiceNumberingMessage, setInvoiceNumberingMessage] = useState<string | null>(null);
  const [invoiceRules, setInvoiceRules] = useState<InvoiceRulesSettingsDto>({ maxClientCasesPerInvoice: 1, maxActsPerInvoice: 1 });
  const [invoiceRulesLoading, setInvoiceRulesLoading] = useState(false);
  const [invoiceRulesMessage, setInvoiceRulesMessage] = useState<string | null>(null);
  const [contractNumbering, setContractNumbering] = useState<ContractNumberingSettingsDto>({
    startNumber: 1,
    allocationStrategy: 'max-plus-one',
    template: '{N}-{DD}/{MM}-{YY}',
  });
  const [contractNumberingLoading, setContractNumberingLoading] = useState(false);
  const [contractNumberingMessage, setContractNumberingMessage] = useState<string | null>(null);
  const [contractRules, setContractRules] = useState<ContractRulesSettingsDto>({
    autoGenerateWhenEmpty: true,
    allowManualNumberEdit: true,
  });
  const [contractRulesLoading, setContractRulesLoading] = useState(false);
  const [contractRulesMessage, setContractRulesMessage] = useState<string | null>(null);
  const [invoiceMigrationLoading, setInvoiceMigrationLoading] = useState(false);
  const [invoiceMigrationError, setInvoiceMigrationError] = useState<string | null>(null);
  const [invoiceMigrationResult, setInvoiceMigrationResult] = useState<InvoiceLegacyMigrationSummaryDto | null>(null);
  const [databaseStatus, setDatabaseStatus] = useState<DatabaseStatusDto | null>(null);
  const [databaseStatusLoading, setDatabaseStatusLoading] = useState(false);
  const [databaseImportFile, setDatabaseImportFile] = useState<File | null>(null);
  const [databaseImportLoading, setDatabaseImportLoading] = useState(false);
  const [databaseImportError, setDatabaseImportError] = useState<string | null>(null);
  const [databaseImportResult, setDatabaseImportResult] = useState<DatabaseImportResultDto | null>(null);
  const [detailsPage, setDetailsPage] = useState(1);
  const [detailsPageSize, setDetailsPageSize] = useState(DETAILS_PAGE_SIZE_OPTIONS[1]);
  const isAdminRole = isAdmin();
  const canViewNotificationSettings = isAdminRole || permissions.notifications.canView;
  const canEditNotificationSettings = isAdminRole || permissions.notifications.canEdit;
  const canViewIntegrationSettings = isAdminRole || Boolean(permissions.integrations?.canView);
  const canEditIntegrationSettings = isAdminRole || Boolean(permissions.integrations?.canEdit);
  const canViewMaintenanceSettings = isAdminRole || permissions.audit.canView;
  const canViewYandexDiskSettings = isAdminRole || Boolean(permissions.yandexDisk?.canView);
  const canViewS3StorageSettings = isAdminRole || Boolean(permissions.s3Storage?.canView);
  const restrictedSettingsMode = !isAdminRole;

  const selectSection = useCallback((section: SettingsSection) => {
    setActiveSection(section);
    pushSettingsSectionToUrl(section);
  }, []);

  useEffect(() => {
    const handlePopState = () => setActiveSection(readSettingsSectionFromUrl());
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  const activeSectionTitle = useMemo(() => {
    if (activeSection === 'companies') return 'Компании сервиса';
    if (activeSection === 'tasks') return 'Журнал задач';
    if (activeSection === 'appearance') return 'Оформление CRM';
    if (activeSection === 'general') return 'Финансовые правила';
    if (activeSection === 'integrations') return 'Ключи и внешние сервисы';
    if (activeSection === 'notifications') return 'Уведомления';
    if (activeSection === 'yandex-disk') return 'Я.Диск';
    if (activeSection === 's3-storage') return 'S3-хранилища';
    if (activeSection === 'users-online') return 'Пользователи онлайн';
    if (activeSection === 'app-releases') return 'Версия приложения и обновления';
    if (activeSection === 'auth-providers') return 'Внешняя авторизация';
    if (activeSection === 'db-maintenance') return 'Обслуживание базы данных';
    if (activeSection === 'db-import') return 'Импорт данных';
    if (activeSection === 'db-operations') return 'Операции по разделам БД';
    if (activeSection === 'form-requirements') return 'Правила заполнения форм';
    return 'Финансовые правила';
  }, [activeSection]);

  const restrictedAllowedSections = useMemo<SettingsSection[]>(() => {
    if (!restrictedSettingsMode) return SETTINGS_SECTIONS;

    const allowed: SettingsSection[] = [];
    if (canViewIntegrationSettings) allowed.push('integrations');
    if (canViewNotificationSettings) allowed.push('notifications');
    if (canViewMaintenanceSettings) allowed.push('db-maintenance');
    if (canViewYandexDiskSettings) allowed.push('yandex-disk');
    if (canViewS3StorageSettings) allowed.push('s3-storage');
    return allowed;
  }, [
    canViewMaintenanceSettings,
    canViewIntegrationSettings,
    canViewNotificationSettings,
    canViewS3StorageSettings,
    canViewYandexDiskSettings,
    restrictedSettingsMode,
  ]);

  useEffect(() => {
    if (!restrictedSettingsMode || restrictedAllowedSections.length === 0) return;
    if (!restrictedAllowedSections.includes(activeSection)) {
      selectSection(restrictedAllowedSections[0]);
    }
  }, [activeSection, restrictedAllowedSections, restrictedSettingsMode, selectSection]);

  const migrationDetails = useMemo(() => result?.details ?? [], [result]);
  const migrationDetailsTotalPages = useMemo(
    () => Math.max(1, Math.ceil(migrationDetails.length / detailsPageSize || 1)),
    [detailsPageSize, migrationDetails.length],
  );
  const pagedMigrationDetails = useMemo(() => {
    const offset = (detailsPage - 1) * detailsPageSize;
    return migrationDetails.slice(offset, offset + detailsPageSize);
  }, [detailsPage, detailsPageSize, migrationDetails]);

  const getErrorMessage = (error: unknown, fallback: string) =>
    extractDisplayErrorMessage(error, fallback);

  const formatDateTime = (value?: string | null) => {
    if (!value) {
      return '—';
    }

    try {
      return formatDateTimeInTimeZone(value, timeZoneId) || value;
    } catch {
      return value;
    }
  };

  const loadDatabaseStatus = useCallback(async () => {
    setDatabaseStatusLoading(true);
    try {
      const status = await apiService.getDatabaseStatus();
      setDatabaseStatus(status);
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось определить текущий провайдер базы данных');
      setDatabaseImportError((prev) => prev ?? message);
    } finally {
      setDatabaseStatusLoading(false);
    }
  }, []);

  const runDatabaseImport = async () => {
    if (!databaseImportFile) {
      const message = 'Сначала выберите SQLite-файл для импорта';
      setDatabaseImportError(message);
      toast.error(message, 'Импорт БД');
      return;
    }

    const approved = await confirm({
      title: 'Подтверждение импорта',
      description: 'Импорт объединит данные из SQLite-файла с текущей базой и покажет отчёт по каждой таблице.',
      confirmLabel: 'Запустить импорт',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setDatabaseImportLoading(true);
    setDatabaseImportError(null);
    setDatabaseImportResult(null);

    try {
      const result = await apiService.importSqliteDatabase(databaseImportFile);
      setDatabaseImportResult(result);
      const message = result.success
        ? 'Импорт SQLite завершён'
        : 'Импорт завершён с замечаниями, проверьте отчёт по таблицам';
      toast.success(message, 'Импорт БД');
      await loadDatabaseStatus();
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось выполнить импорт SQLite-базы');
      setDatabaseImportError(message);
      toast.error(message, 'Импорт БД');
    } finally {
      setDatabaseImportLoading(false);
    }
  };

  const handleReloadDuplicates = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Обновить список групп для объединения справочников',
      confirmLabel: 'Обновить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    await loadDuplicates(mergeKind, { showSuccessToast: true, showErrorToast: true });
  };

  useEffect(() => {
    setDetailsPage(1);
  }, [detailsPageSize, result]);

  useEffect(() => {
    setDetailsPage((prev) => Math.min(prev, migrationDetailsTotalPages));
  }, [migrationDetailsTotalPages]);

  const loadFormRequirements = async () => {
    setRequirementsLoading(true);
    setRequirementsMessage(null);
    try {
      const [data, sections] = await Promise.all([
        apiService.getFormRequirements(),
        apiService.getFormRequirementSections(),
      ]);
      setRequiredFieldKeys(new Set((data ?? []).filter((x) => x.isRequired).map((x) => `${x.section}:${x.fieldKey}`)));
      setFormRequirementSections(sections ?? []);
      if ((sections ?? []).length > 0) {
        setSelectedRequirementsSection((prev) => prev || sections[0].key);
      }
    } catch (e) {
      setRequirementsMessage(e instanceof Error ? e.message : 'Не удалось загрузить обязательные поля');
    } finally {
      setRequirementsLoading(false);
    }
  };

  const toggleRequiredField = async (section: string, fieldKey: string, isRequired: boolean) => {
    setRequirementsLoading(true);
    setRequirementsMessage(null);
    try {
      await apiService.upsertFormRequirement({ section, fieldKey, isRequired });
      await loadFormRequirements();
      const message = 'Настройки обязательных полей сохранены';
      setRequirementsMessage(message);
      toast.success(message, 'Настройки');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить настройку');
      setRequirementsMessage(message);
      toast.error(message, 'Настройки');
      setRequirementsLoading(false);
    }
  };

  const loadDuplicates = useCallback(async (
    kind: MergeKind,
    options?: { showSuccessToast?: boolean; showErrorToast?: boolean },
  ) => {
    setMergeLoading(true);
    setMergeError(null);
    setMergeMessage(null);
    try {
      const data = await apiService.getSimpleDictionaryDuplicates(kind);
      setDuplicates(data ?? []);
      const first = (data ?? [])[0];
      setSelectedGroupKey(first?.canonical ?? '');
      setPreferredId(first?.items?.[0]?.id ?? null);
      if (options?.showSuccessToast) {
        toast.success(
          (data?.length ?? 0) > 0
            ? `Найдено групп дублей: ${data?.length ?? 0}`
            : 'Группы с доступными дублями не найдены',
          'Справочники',
        );
      }
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось загрузить дубли');
      setMergeError(message);
      setDuplicates([]);
      setSelectedGroupKey('');
      setPreferredId(null);
      if (options?.showErrorToast) {
        toast.error(message, 'Справочники');
      }
    } finally {
      setMergeLoading(false);
    }
  }, [toast]);

  const runMerge = async () => {
    const group = duplicates.find((item) => item.canonical === selectedGroupKey);
    if (!group || group.items.length < 2 || !preferredId) {
      const message = 'Выберите группу дублей и базовое значение';
      setMergeError(message);
      toast.error(message, 'Справочники');
      return;
    }

    const approved = await confirm({
      title: 'Подтверждение объединения',
      description: 'Объединить выбранные записи в одно значение и заменить ссылки во всех карточках юрлиц.',
      confirmLabel: 'Объединить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setMergeLoading(true);
    setMergeError(null);
    setMergeMessage(null);
    try {
      const primary = group.items.find((item) => item.id === preferredId) ?? group.items[0];
      const result = await apiService.mergeSimpleDictionaryDuplicates(mergeKind, {
        sourceIds: group.items.map((item) => item.id),
        preferredId: primary.id,
        name: primary.name,
        description: primary.description,
        colorHex: primary.colorHex,
        isActive: primary.isActive,
      });
      const message = `Объединение выполнено: создано "${result.name}", обновлено юрлиц: ${result.replacedInLegalEntities}`;
      setMergeMessage(message);
      toast.success(message, 'Справочники');
      await loadDuplicates(mergeKind);
      window.dispatchEvent(new CustomEvent('dicts:changed'));
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось выполнить объединение');
      setMergeError(message);
      toast.error(message, 'Справочники');
    } finally {
      setMergeLoading(false);
    }
  };


  const loadDaDataCredentials = useCallback(async () => {
    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      const settings = await apiService.getDadataIntegrationSettings();
      setDadataSettings(settings);
      setDadataToken('');
      setDadataSecret('');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось загрузить серверные ключи DaData');
      setCredentialsMessage(message);
      setDadataSettings(null);
    } finally {
      setCredentialsLoading(false);
    }
  }, []);

  const saveDaDataCredentials = async () => {
    if (!canEditIntegrationSettings || dadataSettings?.canEdit === false) {
      const message = 'Недостаточно прав для изменения ключей DaData';
      setCredentialsMessage(message);
      toast.error(message, 'Интеграции');
      return;
    }

    const payload: { apiKey?: string; secret?: string } = {};
    const nextToken = dadataToken.trim();
    const nextSecret = dadataSecret.trim();
    if (nextToken) payload.apiKey = nextToken;
    if (nextSecret) payload.secret = nextSecret;

    if (!payload.apiKey && !payload.secret) {
      const message = 'Введите новый токен или секретный ключ DaData';
      setCredentialsMessage(message);
      toast.error(message, 'Интеграции');
      return;
    }

    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить серверные ключи DaData для всей CRM',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      const settings = await apiService.updateDadataIntegrationSettings(payload);
      setDadataSettings(settings);
      setDadataToken('');
      setDadataSecret('');
      const message = 'Серверные ключи DaData сохранены';
      setCredentialsMessage(message);
      toast.success(message, 'Интеграции');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить серверные ключи DaData');
      setCredentialsMessage(message);
      toast.error(message, 'Интеграции');
    } finally {
      setCredentialsLoading(false);
    }
  };

  const loadInvoiceNumbering = async () => {
    setInvoiceNumberingLoading(true);
    setInvoiceNumberingMessage(null);
    try {
      const response = await apiService.getInvoiceNumberingSettings();
      setInvoiceNumbering(response);
    } catch (e) {
      setInvoiceNumberingMessage(e instanceof Error ? e.message : 'Не удалось загрузить настройки нумерации счетов');
    } finally {
      setInvoiceNumberingLoading(false);
    }
  };

  const saveInvoiceNumbering = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить настройки нумерации счетов',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setInvoiceNumberingLoading(true);
    setInvoiceNumberingMessage(null);
    try {
      const response = await apiService.updateInvoiceNumberingSettings(invoiceNumbering);
      setInvoiceNumbering(response);
      const message = 'Настройки нумерации счетов сохранены';
      setInvoiceNumberingMessage(message);
      toast.success(message, 'Настройки');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить настройки нумерации счетов');
      setInvoiceNumberingMessage(message);
      toast.error(message, 'Настройки');
    } finally {
      setInvoiceNumberingLoading(false);
    }
  };

  const loadInvoiceRules = async () => {
    setInvoiceRulesLoading(true);
    setInvoiceRulesMessage(null);
    try {
      const response = await apiService.getInvoiceRulesSettings();
      setInvoiceRules(response);
    } catch (e) {
      setInvoiceRulesMessage(e instanceof Error ? e.message : 'Не удалось загрузить ограничения счёта');
    } finally {
      setInvoiceRulesLoading(false);
    }
  };

  const saveInvoiceRules = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить ограничения по количеству дел и актов в счёте',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setInvoiceRulesLoading(true);
    setInvoiceRulesMessage(null);
    try {
      const response = await apiService.updateInvoiceRulesSettings({
        maxClientCasesPerInvoice: Math.max(1, Number(invoiceRules.maxClientCasesPerInvoice) || 1),
        maxActsPerInvoice: Math.max(1, Number(invoiceRules.maxActsPerInvoice) || 1),
      });
      setInvoiceRules(response);
      const message = 'Ограничения счёта сохранены';
      setInvoiceRulesMessage(message);
      toast.success(message, 'Настройки');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить ограничения счёта');
      setInvoiceRulesMessage(message);
      toast.error(message, 'Настройки');
    } finally {
      setInvoiceRulesLoading(false);
    }
  };

  const loadContractNumbering = async () => {
    setContractNumberingLoading(true);
    setContractNumberingMessage(null);
    try {
      const response = await apiService.getContractNumberingSettings();
      setContractNumbering(response);
    } catch (e) {
      setContractNumberingMessage(e instanceof Error ? e.message : 'Не удалось загрузить настройки нумерации договоров');
    } finally {
      setContractNumberingLoading(false);
    }
  };

  const saveContractNumbering = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить настройки нумерации договоров',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setContractNumberingLoading(true);
    setContractNumberingMessage(null);
    try {
      const response = await apiService.updateContractNumberingSettings({
        startNumber: Math.max(1, Number(contractNumbering.startNumber) || 1),
        allocationStrategy: contractNumbering.allocationStrategy,
        template: contractNumbering.template.trim(),
      });
      setContractNumbering(response);
      const message = 'Настройки нумерации договоров сохранены';
      setContractNumberingMessage(message);
      toast.success(message, 'Настройки');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить настройки нумерации договоров');
      setContractNumberingMessage(message);
      toast.error(message, 'Настройки');
    } finally {
      setContractNumberingLoading(false);
    }
  };

  const loadContractRules = async () => {
    setContractRulesLoading(true);
    setContractRulesMessage(null);
    try {
      const response = await apiService.getContractRulesSettings();
      setContractRules(response);
    } catch (e) {
      setContractRulesMessage(e instanceof Error ? e.message : 'Не удалось загрузить правила договоров');
    } finally {
      setContractRulesLoading(false);
    }
  };

  const saveContractRules = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить правила генерации номера договора',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setContractRulesLoading(true);
    setContractRulesMessage(null);
    try {
      const response = await apiService.updateContractRulesSettings(contractRules);
      setContractRules(response);
      const message = 'Правила договоров сохранены';
      setContractRulesMessage(message);
      toast.success(message, 'Настройки');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить правила договоров');
      setContractRulesMessage(message);
      toast.error(message, 'Настройки');
    } finally {
      setContractRulesLoading(false);
    }
  };

  const runMigration = async () => {
    const approved = await confirm({
      title: 'Подтверждение миграции',
      description: 'Перенести старые адреса карточек юрлиц и физлиц в отдельные структурированные сущности.',
      confirmLabel: 'Перенести',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setLoading(true);
    setError(null);
    try {
      const response = await apiService.migrateLegacyLegalEntityAddresses(validateWithDadata);
      setResult(response);
      toast.success(
        `Перенос адресов завершён. Добавлено: ${response.added}, обновлено: ${response.updated}, ошибок: ${response.failed}`,
        'База данных',
      );
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось выполнить перенос адресов');
      setError(message);
      toast.error(message, 'База данных');
    } finally {
      setLoading(false);
    }
  };

  const runInvoiceMigration = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Создать счета и связи из legacy-структуры старых платежей канонического реестра.',
      confirmLabel: 'Запустить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setInvoiceMigrationLoading(true);
    setInvoiceMigrationError(null);
    try {
      const response = await apiService.migrateLegacyInvoiceLinks();
      setInvoiceMigrationResult(response);
      toast.success(
        `Миграция счетов завершена. Создано счетов: ${response.createdInvoices}, связей платежей: ${response.linkedPayments}`,
        'Счета',
      );
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось выполнить перенос счетов');
      setInvoiceMigrationError(message);
      toast.error(message, 'Счета');
    } finally {
      setInvoiceMigrationLoading(false);
    }
  };

  useEffect(() => {
    if (activeSection === 'integrations' && canViewIntegrationSettings) {
      void loadDaDataCredentials();
    }
    if (activeSection === 'general') {
      void loadInvoiceNumbering();
      void loadInvoiceRules();
      void loadContractNumbering();
      void loadContractRules();
    }
    if (activeSection === 'db-import') {
      void loadDatabaseStatus();
    }
    if (activeSection === 'db-operations' && activeDatabaseOperationsSubsection === 'dictionary-merge') {
      void loadDuplicates(mergeKind);
    }
    if (activeSection === 'form-requirements') {
      void loadFormRequirements();
    }
  }, [activeDatabaseOperationsSubsection, activeSection, canViewIntegrationSettings, loadDaDataCredentials, loadDatabaseStatus, loadDuplicates, mergeKind]);

  return (
    <div className="p-3 lg:p-4">
      <div className="w-full">
        <div data-testid="settings-workspace" className="grid min-w-0 grid-cols-1 gap-3 lg:grid-cols-[280px_minmax(0,1fr)]">
          <aside
            data-testid="settings-sidebar"
            className="pp-settings-sidebar flex flex-col rounded-3xl border p-3 shadow-sm lg:sticky lg:top-4 lg:max-h-[calc(100vh-8rem)] lg:self-start lg:overflow-hidden"
          >
            <div className="pp-settings-sidebar-heading rounded-2xl border px-3 py-3">
              <div className="pp-settings-sidebar-kicker text-xs font-semibold uppercase tracking-[0.18em]">Панель настроек</div>
              <h2 className="pp-settings-sidebar-title mt-2 text-xl font-semibold">Структура разделов</h2>
            </div>

            <nav
              aria-label="Разделы настроек"
              data-testid="settings-sidebar-scroll"
              className="mt-3 min-h-0 flex-1 space-y-2 overflow-y-auto overscroll-contain pr-1"
            >
              {!restrictedSettingsMode && (
              <div className="pp-settings-group rounded-2xl border p-3 shadow-sm">
                <div className="pp-settings-group-header flex items-center gap-3 rounded-xl border px-3 py-3 shadow-sm">
                  <div className="pp-settings-group-icon flex h-10 w-10 items-center justify-center rounded-xl">
                    <SlidersHorizontal className="h-5 w-5" />
                  </div>
                  <div className="min-w-0">
                    <div className="pp-settings-group-kicker text-[11px] font-semibold uppercase tracking-[0.18em]">Раздел</div>
                    <div className="pp-settings-group-title text-sm font-semibold">Операционная модель</div>
                  </div>
                </div>
                <div className="mt-3 space-y-2">
                  <button
                    type="button"
                    onClick={() => selectSection('companies')}
                    className={settingsNavItemClass(activeSection === 'companies')}
                  >
                    Основные компании
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('tasks')}
                    className={settingsNavItemClass(activeSection === 'tasks')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <ClipboardList className="h-4 w-4" />
                      Журнал задач
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('appearance')}
                    className={settingsNavItemClass(activeSection === 'appearance')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <Palette className="h-4 w-4" />
                      Оформление CRM
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('general')}
                    className={settingsNavItemClass(activeSection === 'general')}
                  >
                    Финансовые правила
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('form-requirements')}
                    className={settingsNavItemClass(activeSection === 'form-requirements')}
                  >
                    Правила заполнения форм
                  </button>
                </div>
              </div>
              )}

              {(!restrictedSettingsMode || canViewIntegrationSettings || canViewNotificationSettings || canViewYandexDiskSettings || canViewS3StorageSettings) && (
              <div className="pp-settings-group rounded-2xl border p-3 shadow-sm">
                <div className="pp-settings-group-header flex items-center gap-3 rounded-xl border px-3 py-3 shadow-sm">
                  <div className="pp-settings-group-icon flex h-10 w-10 items-center justify-center rounded-xl">
                    <ShieldCheck className="h-5 w-5" />
                  </div>
                  <div className="min-w-0">
                    <div className="pp-settings-group-kicker text-[11px] font-semibold uppercase tracking-[0.18em]">Раздел</div>
                    <div className="pp-settings-group-title text-sm font-semibold">Интеграции и доступ</div>
                  </div>
                </div>
                <div className="mt-3 space-y-2">
                  {(canViewIntegrationSettings || !restrictedSettingsMode) && (
                  <button
                    type="button"
                    onClick={() => selectSection('integrations')}
                    className={settingsNavItemClass(activeSection === 'integrations')}
                  >
                    Ключи интеграций
                  </button>
                  )}
                  {(canViewNotificationSettings || !restrictedSettingsMode) && (
                  <button
                    type="button"
                    onClick={() => selectSection('notifications')}
                    className={settingsNavItemClass(activeSection === 'notifications')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <Bell className="h-4 w-4" />
                      Уведомления
                    </span>
                  </button>
                  )}
                  {(canViewYandexDiskSettings || !restrictedSettingsMode) && (
                  <button
                    type="button"
                    onClick={() => selectSection('yandex-disk')}
                    className={settingsNavItemClass(activeSection === 'yandex-disk')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <Cloud className="h-4 w-4" />
                      Я.Диск
                    </span>
                  </button>
                  )}
                  {(canViewS3StorageSettings || !restrictedSettingsMode) && (
                  <button
                    type="button"
                    onClick={() => selectSection('s3-storage')}
                    className={settingsNavItemClass(activeSection === 's3-storage')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <Cloud className="h-4 w-4" />
                      S3-хранилища
                    </span>
                  </button>
                  )}
                  {!restrictedSettingsMode && (
                  <>
                  <button
                    type="button"
                    onClick={() => selectSection('app-releases')}
                    className={settingsNavItemClass(activeSection === 'app-releases')}
                  >
                    <span className="inline-flex items-center gap-2">
                      <History className="h-4 w-4" />
                      Версия и обновления
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('auth-providers')}
                    className={settingsNavItemClass(activeSection === 'auth-providers')}
                  >
                    Авторизация и вход
                  </button>
                  </>
                  )}
                  {!restrictedSettingsMode && (
                    <button
                      type="button"
                      onClick={() => selectSection('users-online')}
                      className={settingsNavItemClass(activeSection === 'users-online')}
                    >
                      <span className="inline-flex items-center gap-2">
                        <Users className="h-4 w-4" />
                        Пользователи онлайн
                      </span>
                    </button>
                  )}
                </div>
              </div>
              )}

              {(!restrictedSettingsMode || canViewMaintenanceSettings) && (
              <div className="pp-settings-group rounded-2xl border p-3 shadow-sm">
                <div className="pp-settings-group-header flex items-center gap-3 rounded-xl border px-3 py-3 shadow-sm">
                  <div className="pp-settings-group-icon flex h-10 w-10 items-center justify-center rounded-xl">
                    <Database className="h-5 w-5" />
                  </div>
                  <div className="min-w-0">
                    <div className="pp-settings-group-kicker text-[11px] font-semibold uppercase tracking-[0.18em]">Раздел</div>
                    <div className="pp-settings-group-title text-sm font-semibold">База данных и обслуживание</div>
                  </div>
                </div>
                <div className="mt-3 space-y-2">
                  <button
                    type="button"
                    onClick={() => selectSection('db-maintenance')}
                    className={settingsNavItemClass(activeSection === 'db-maintenance')}
                  >
                    Обслуживание
                  </button>
                  {!restrictedSettingsMode && (
                    <>
                  <button
                    type="button"
                    onClick={() => selectSection('db-operations')}
                    className={settingsNavItemClass(activeSection === 'db-operations')}
                  >
                    Операции по разделам
                  </button>
                  <button
                    type="button"
                    onClick={() => selectSection('db-import')}
                    className={settingsNavItemClass(activeSection === 'db-import')}
                  >
                    Импорт данных
                  </button>
                    </>
                  )}
                </div>
              </div>
              )}
            </nav>
          </aside>

          <section data-testid="settings-content" className="min-w-0 space-y-3">
            <div className="rounded-3xl border border-gray-200 bg-white p-3 shadow-sm">
              <h3 className="text-2xl font-semibold text-gray-900">{activeSectionTitle}</h3>
            </div>

            {activeSection === 'companies' && (
              <div className="space-y-6">
                <MainCompanySettings />
                <MoneyAccountSettings />
                <BankDirectorySettings />
              </div>
            )}

            {activeSection === 'tasks' && (
              <div className="space-y-6">
                <TaskJournalSettings />
              </div>
            )}

            {activeSection === 'appearance' && !restrictedSettingsMode && <AppearanceSettings />}

            {activeSection === 'general' && (
              <div className="space-y-6">
                <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => setActiveFinancialRulesSubsection('invoices')}
                      className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                        activeFinancialRulesSubsection === 'invoices'
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      Счета
                    </button>
                    <button
                      type="button"
                      onClick={() => setActiveFinancialRulesSubsection('acts')}
                      className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                        activeFinancialRulesSubsection === 'acts'
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      Акты
                    </button>
                    <button
                      type="button"
                      onClick={() => setActiveFinancialRulesSubsection('contracts')}
                      className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                        activeFinancialRulesSubsection === 'contracts'
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      Договоры
                    </button>
                    <button
                      type="button"
                      onClick={() => setActiveFinancialRulesSubsection('case-documents')}
                      className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                        activeFinancialRulesSubsection === 'case-documents'
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      Документы дел
                    </button>
                  </div>
                </div>

                {activeFinancialRulesSubsection === 'invoices' && (
                  <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                    <div className="mb-6">
                      <h4 className="text-xl font-semibold text-gray-900">Счета</h4>
                    </div>

                    <div className="grid gap-6 xl:grid-cols-2">
                      <div className="flex h-full flex-col rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
                        <div className="space-y-4">
                          <div>
                            <h5 className="text-base font-semibold text-gray-900">Нумерация счетов</h5>
                            <p className="mt-1 text-sm text-gray-600">
                              Номер сбрасывается каждый год. Можно выбрать стартовый номер и стратегию подбора следующего номера.
                            </p>
                          </div>

                          <label className="block">
                            <span className="text-sm font-medium text-gray-700">Стартовый номер года</span>
                            <input
                              type="number"
                              min={1}
                              value={invoiceNumbering.startNumber}
                              onChange={(e) => setInvoiceNumbering((prev) => ({ ...prev, startNumber: Math.max(1, Number(e.target.value) || 1) }))}
                              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                            />
                          </label>

                          <label className="block">
                            <span className="text-sm font-medium text-gray-700">Стратегия следующего номера</span>
                            <SystemSelect
                              value={invoiceNumbering.allocationStrategy}
                              onChange={(e) => setInvoiceNumbering((prev) => ({ ...prev, allocationStrategy: e.target.value as InvoiceNumberingSettingsDto['allocationStrategy'] }))}
                              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                            >
                              <option value="max-plus-one">Максимальный номер + 1</option>
                              <option value="first-free">Первый свободный номер от стартового</option>
                            </SystemSelect>
                          </label>
                        </div>

                        <div className="mt-auto flex min-h-10 items-end justify-between gap-3 pt-6">
                          {invoiceNumberingMessage ? (
                            <span className="text-sm text-gray-600">{invoiceNumberingMessage}</span>
                          ) : (
                            <span />
                          )}
                          <button
                            type="button"
                            onClick={saveInvoiceNumbering}
                            disabled={invoiceNumberingLoading}
                            className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            <Save className="h-4 w-4" />
                            <span>{invoiceNumberingLoading ? 'Сохранение…' : 'Сохранить'}</span>
                          </button>
                        </div>
                      </div>

                      <div className="flex h-full flex-col rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
                        <div className="space-y-4">
                          <div>
                            <h5 className="text-base font-semibold text-gray-900">Ограничения счёта</h5>
                            <p className="mt-1 text-sm text-gray-600">
                              Временно можно ограничить количество связанных дел и актов в одном счёте. Ограничение действует и в форме, и на сервере.
                            </p>
                          </div>

                          <div className="grid gap-4 md:grid-cols-2">
                            <label className="block">
                              <span className="text-sm font-medium text-gray-700">Максимум дел в счёте</span>
                              <input
                                type="number"
                                min={1}
                                value={invoiceRules.maxClientCasesPerInvoice}
                                onChange={(e) => setInvoiceRules((prev) => ({ ...prev, maxClientCasesPerInvoice: Math.max(1, Number(e.target.value) || 1) }))}
                                className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                              />
                            </label>

                            <label className="block">
                              <span className="text-sm font-medium text-gray-700">Максимум актов в счёте</span>
                              <input
                                type="number"
                                min={1}
                                value={invoiceRules.maxActsPerInvoice}
                                onChange={(e) => setInvoiceRules((prev) => ({ ...prev, maxActsPerInvoice: Math.max(1, Number(e.target.value) || 1) }))}
                                className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                              />
                            </label>
                          </div>
                        </div>

                        <div className="mt-auto flex min-h-10 items-end justify-between gap-3 pt-6">
                          {invoiceRulesMessage ? (
                            <span className="text-sm text-gray-600">{invoiceRulesMessage}</span>
                          ) : (
                            <span />
                          )}
                          <button
                            type="button"
                            onClick={saveInvoiceRules}
                            disabled={invoiceRulesLoading}
                            className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            <Save className="h-4 w-4" />
                            <span>{invoiceRulesLoading ? 'Сохранение…' : 'Сохранить'}</span>
                          </button>
                        </div>
                      </div>
                    </div>

                  </div>
                )}

                {activeFinancialRulesSubsection === 'invoices' && (
                  <Suspense fallback={<SettingsPanelLoading />}>
                    <InvoiceDocumentTemplateSettings />
                  </Suspense>
                )}

                {activeFinancialRulesSubsection === 'acts' && (
                  <ActDocumentTemplateSettings />
                )}

                {activeFinancialRulesSubsection === 'case-documents' && (
                  <div className="space-y-6">
                    <ProceduralDeadlineCalendarSettings />
                    <CaseDocumentTemplateSettings />
                  </div>
                )}

                {activeFinancialRulesSubsection === 'contracts' && (
                  <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                    <div className="mb-6">
                      <h4 className="text-xl font-semibold text-gray-900">Договоры</h4>
                    </div>

                    <ContractDocumentTemplateSettings />

                    <div className="grid gap-6 xl:grid-cols-2">
                      <div className="flex h-full flex-col rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
                        <div className="space-y-4">
                          <div>
                            <h5 className="text-base font-semibold text-gray-900">Нумерация договоров</h5>
                            <p className="mt-1 text-sm text-gray-600">
                              Здесь задаются стартовый номер, стратегия подбора и шаблон генерации номера договора.
                            </p>
                          </div>

                          <label className="block">
                            <span className="text-sm font-medium text-gray-700">Стартовый номер</span>
                            <input
                              type="number"
                              min={1}
                              value={contractNumbering.startNumber}
                              onChange={(e) =>
                                setContractNumbering((prev) => ({
                                  ...prev,
                                  startNumber: Math.max(1, Number(e.target.value) || 1),
                                }))
                              }
                              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                            />
                          </label>

                          <label className="block">
                            <span className="text-sm font-medium text-gray-700">Стратегия следующего номера</span>
                            <SystemSelect
                              value={contractNumbering.allocationStrategy}
                              onChange={(e) =>
                                setContractNumbering((prev) => ({
                                  ...prev,
                                  allocationStrategy: e.target.value as ContractNumberingSettingsDto['allocationStrategy'],
                                }))
                              }
                              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                            >
                              <option value="max-plus-one">Максимальный номер + 1</option>
                              <option value="first-free">Первый свободный номер</option>
                            </SystemSelect>
                          </label>

                          <label className="block">
                            <span className="text-sm font-medium text-gray-700">Шаблон номера</span>
                            <input
                              type="text"
                              value={contractNumbering.template}
                              onChange={(e) =>
                                setContractNumbering((prev) => ({
                                  ...prev,
                                  template: e.target.value,
                                }))
                              }
                              placeholder="{N}-{DD}/{MM}-{YY}"
                              className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                            />
                            <p className="mt-2 text-xs text-gray-500">
                              Поддерживаются маркеры номера <code>{'{N}'}</code>, <code>{'{NN}'}</code>, <code>{'{NNN}'}</code>, <code>{'{NNNN}'}</code> и даты <code>{'{DD}'}</code>, <code>{'{MM}'}</code>, <code>{'{YY}'}</code>, <code>{'{YYYY}'}</code>.
                            </p>
                          </label>
                        </div>

                        <div className="mt-auto flex min-h-10 items-end justify-between gap-3 pt-6">
                          {contractNumberingMessage ? (
                            <span className="text-sm text-gray-600">{contractNumberingMessage}</span>
                          ) : (
                            <span />
                          )}
                          <button
                            type="button"
                            onClick={saveContractNumbering}
                            disabled={contractNumberingLoading}
                            className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            <Save className="h-4 w-4" />
                            <span>{contractNumberingLoading ? 'Сохранение…' : 'Сохранить'}</span>
                          </button>
                        </div>
                      </div>

                      <div className="flex h-full flex-col rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
                        <div className="space-y-4">
                          <div>
                            <h5 className="text-base font-semibold text-gray-900">Правила договоров</h5>
                            <p className="mt-1 text-sm text-gray-600">
                              Здесь настраивается автогенерация номера и возможность ручного редактирования.
                            </p>
                          </div>

                          <label className="flex items-start gap-3 rounded-xl border border-gray-200 px-4 py-3 text-sm text-gray-700">
                            <SystemCheckbox
                              type="checkbox"
                              checked={contractRules.autoGenerateWhenEmpty}
                              onChange={(e) =>
                                setContractRules((prev) => ({
                                  ...prev,
                                  autoGenerateWhenEmpty: e.target.checked,
                                }))
                              }
                              className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                            />
                            <span>
                              <span className="block font-medium text-gray-900">Автоматически заполнять номер, если поле пустое</span>
                              <span className="mt-1 block text-gray-500">Подойдёт, если договор обычно создаётся без ручного ввода номера.</span>
                            </span>
                          </label>

                          <label className="flex items-start gap-3 rounded-xl border border-gray-200 px-4 py-3 text-sm text-gray-700">
                            <SystemCheckbox
                              type="checkbox"
                              checked={contractRules.allowManualNumberEdit}
                              onChange={(e) =>
                                setContractRules((prev) => ({
                                  ...prev,
                                  allowManualNumberEdit: e.target.checked,
                                }))
                              }
                              className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                            />
                            <span>
                              <span className="block font-medium text-gray-900">Разрешить ручное изменение номера договора</span>
                              <span className="mt-1 block text-gray-500">Если опция выключена, система будет удерживать номер по правилам и не даст изменить его вручную.</span>
                            </span>
                          </label>
                        </div>

                        <div className="mt-auto flex min-h-10 items-end justify-between gap-3 pt-6">
                          {contractRulesMessage ? (
                            <span className="text-sm text-gray-600">{contractRulesMessage}</span>
                          ) : (
                            <span />
                          )}
                          <button
                            type="button"
                            onClick={saveContractRules}
                            disabled={contractRulesLoading}
                            className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            <Save className="h-4 w-4" />
                            <span>{contractRulesLoading ? 'Сохранение…' : 'Сохранить'}</span>
                          </button>
                        </div>
                      </div>
                    </div>
                  </div>
                )}
              </div>
            )}

            {activeSection === 'notifications' && canViewNotificationSettings && (
              <Suspense fallback={<SettingsPanelLoading />}>
                <NotificationSettings canEdit={canEditNotificationSettings} />
              </Suspense>
            )}

            {activeSection === 'users-online' && !restrictedSettingsMode && <UserOnlineSettings />}

            {activeSection === 'app-releases' && !restrictedSettingsMode && <AppVersionSettings />}

            {activeSection === 'auth-providers' && !restrictedSettingsMode && (
              <Suspense fallback={<SettingsPanelLoading />}>
                <AuthProviderSettings />
              </Suspense>
            )}

            {activeSection === 'yandex-disk' && canViewYandexDiskSettings && <YandexDisk embedded />}

            {activeSection === 's3-storage' && canViewS3StorageSettings && <S3Storage embedded />}

            {activeSection === 'db-maintenance' && canViewMaintenanceSettings && <DatabaseMaintenanceSettings />}

            {activeSection === 'db-import' && !restrictedSettingsMode && (
              <Suspense fallback={<SettingsPanelLoading />}>
                <SpreadsheetImportSettings />
              </Suspense>
            )}

            {SHOW_LEGACY_SQLITE_IMPORT && activeSection === 'db-import' && (
              <div className="space-y-6">
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                  <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
                    <div>
                      <h4 className="text-base font-semibold text-gray-900">Импорт старой SQLite-базы</h4>
                      <p className="mt-1 text-sm text-gray-600">
                        Раздел рассчитан на перенос данных из существующего SQLite-файла в новую базу. Импорт проходит по таблицам, пытается объединять совпадающие записи и возвращает отдельный статус по каждой сущности.
                      </p>
                    </div>
                    <button
                      type="button"
                      onClick={() => void loadDatabaseStatus()}
                      disabled={databaseStatusLoading || databaseImportLoading}
                      className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {databaseStatusLoading ? 'Обновляем…' : 'Обновить статус'}
                    </button>
                  </div>

                  <div className="grid gap-4 md:grid-cols-3">
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Текущая база</div>
                      <div className="mt-2 text-lg font-semibold text-gray-900">
                        {databaseStatusLoading && !databaseStatus ? '…' : databaseStatus?.displayName ?? '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Импорт SQLite</div>
                      <div className="mt-2 text-lg font-semibold text-gray-900">
                        {databaseStatus?.supportsSqliteFileImport === false ? 'Недоступен' : 'Доступен'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Выбранный файл</div>
                      <div className="mt-2 text-sm text-gray-700 break-all">
                        {databaseImportFile?.name ?? 'Файл не выбран'}
                      </div>
                    </div>
                  </div>

                  <label className="block text-sm text-gray-700">
                    <span className="mb-2 block font-medium text-gray-900">SQLite-файл</span>
                    <input
                      type="file"
                      accept=".db,.sqlite,.sqlite3,application/octet-stream"
                      onChange={(event) => {
                        setDatabaseImportError(null);
                        setDatabaseImportResult(null);
                        setDatabaseImportFile(event.target.files?.[0] ?? null);
                      }}
                      className="block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-blue-700"
                    />
                  </label>

                  <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
                    Перед запуском импорта лучше использовать новую базу без рабочих данных. Если часть записей уже существует, система попытается объединить их по ключевым полям и покажет это в отчёте.
                  </div>

                  <div className="flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={runDatabaseImport}
                      disabled={databaseImportLoading || !databaseImportFile}
                      className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {databaseImportLoading ? 'Импортируем…' : 'Запустить импорт'}
                    </button>
                    <span className="text-sm text-gray-500">
                      Источник: SQLite → приёмник: {databaseStatus?.displayName ?? 'текущая база'}
                    </span>
                  </div>

                  {databaseImportError && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {databaseImportError}
                    </div>
                  )}
                </div>

                {databaseImportResult && (
                  <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium">
                        {databaseImportResult.success ? 'Импорт завершён' : 'Импорт завершён с замечаниями'}
                      </p>
                      <div className="mt-2 grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                        <div>Таблиц обработано: <b>{databaseImportResult.sourceTablesProcessed}</b></div>
                        <div>Таблиц с ошибками: <b>{databaseImportResult.tablesWithErrors}</b></div>
                        <div>Старт: <b>{formatDateTime(databaseImportResult.startedAt)}</b></div>
                        <div>Финиш: <b>{formatDateTime(databaseImportResult.finishedAt)}</b></div>
                      </div>
                    </div>

                    <div className="overflow-x-auto">
                      <table className="min-w-full text-sm">
                        <thead>
                          <tr className="border-b border-gray-200 text-left text-gray-500">
                            <th className="py-2 pr-4">Таблица</th>
                            <th className="py-2 pr-4">Источник</th>
                            <th className="py-2 pr-4">Добавлено</th>
                            <th className="py-2 pr-4">Обновлено</th>
                            <th className="py-2 pr-4">Пропущено</th>
                            <th className="py-2 pr-4">Конфликты</th>
                            <th className="py-2 pr-4">Ошибки</th>
                            <th className="py-2 pr-4">Статус</th>
                            <th className="py-2">Комментарий</th>
                          </tr>
                        </thead>
                        <tbody>
                          {databaseImportResult.tables.map((item) => (
                            <tr key={item.table} className="border-b border-gray-100 align-top">
                              <td className="py-2 pr-4 font-medium text-gray-900">{item.displayName}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.sourceRows}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.inserted}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.updated}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.skipped}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.conflicts}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.errors}</td>
                              <td className="py-2 pr-4">
                                <span className={`inline-flex rounded-md px-2 py-1 text-xs font-medium ${
                                  item.completed && item.errors === 0
                                    ? 'bg-emerald-100 text-emerald-700'
                                    : item.errors > 0
                                      ? 'bg-red-100 text-red-700'
                                      : 'bg-amber-100 text-amber-700'
                                }`}>
                                  {item.completed && item.errors === 0 ? 'Готово' : item.errors > 0 ? 'Ошибка' : 'Проверить'}
                                </span>
                              </td>
                              <td className="py-2 text-gray-700">{item.message || '—'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )}
              </div>
            )}

            {activeSection === 'db-operations' && !restrictedSettingsMode && (
              <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => setActiveDatabaseOperationsSubsection('invoices')}
                    className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                      activeDatabaseOperationsSubsection === 'invoices'
                        ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                        : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Счета
                  </button>
                  <button
                    type="button"
                    onClick={() => setActiveDatabaseOperationsSubsection('addresses')}
                    className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                      activeDatabaseOperationsSubsection === 'addresses'
                        ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                        : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Адреса
                  </button>
                  <button
                    type="button"
                    onClick={() => setActiveDatabaseOperationsSubsection('dictionary-merge')}
                    className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                      activeDatabaseOperationsSubsection === 'dictionary-merge'
                        ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                        : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Объединение дублей
                  </button>
                  <button
                    type="button"
                    onClick={() => setActiveDatabaseOperationsSubsection('users-merge')}
                    className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                      activeDatabaseOperationsSubsection === 'users-merge'
                        ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                        : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Пользователи
                  </button>
                </div>
              </div>
            )}

            {activeSection === 'db-operations' && !restrictedSettingsMode && activeDatabaseOperationsSubsection === 'invoices' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Счета</h4>
                  <p className="text-sm text-gray-600 mt-1">
                    Операция анализирует платежи, у которых заполнен legacy-счет, и формирует канонические счета с привязкой по клиенту, номеру счета, году и делу.
                    Платежи с одинаковым счетом и одним делом объединяются в один счет; одинаковый номер счета, но разные дела, разделяются на разные счета.
                    После этого платежи перепривязываются к актуальному счету через `invoiceId`.
                  </p>
                </div>

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runInvoiceMigration}
                    disabled={invoiceMigrationLoading}
                    className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {invoiceMigrationLoading ? 'Обрабатываем…' : 'Обновить счета и связи'}
                  </button>
                </div>

                {invoiceMigrationError && (
                  <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{invoiceMigrationError}</div>
                )}

                {invoiceMigrationResult && (
                  <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                    <p className="font-medium mb-2">Сводка обновления счетов</p>
                    <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                      <li>Проверено платежей: <b>{invoiceMigrationResult.scannedPayments}</b></li>
                      <li>Из них legacy invoice-строк: <b>{invoiceMigrationResult.invoicePayments}</b></li>
                      <li>Уже были связаны правильно: <b>{invoiceMigrationResult.alreadyLinked}</b></li>
                      <li>Пропущено без номера: <b>{invoiceMigrationResult.missingNumberSkipped}</b></li>
                      <li>Пропущено без клиента: <b>{invoiceMigrationResult.missingClientSkipped}</b></li>
                      <li>Групп к обработке: <b>{invoiceMigrationResult.distinctLegacyGroups}</b></li>
                      <li>Групп с делом: <b>{invoiceMigrationResult.groupsWithCase}</b></li>
                      <li>Групп без дела: <b>{invoiceMigrationResult.groupsWithoutCase}</b></li>
                      <li>Связано с существующими счетами: <b>{invoiceMigrationResult.linkedToExistingInvoice}</b></li>
                      <li>Создано новых счетов: <b>{invoiceMigrationResult.createdInvoices}</b></li>
                      <li>Новых привязок платежей: <b>{invoiceMigrationResult.linkedPayments}</b></li>
                      <li>Перепривязано к новым счетам: <b>{invoiceMigrationResult.relinkedPayments}</b></li>
                      <li>Обновлено карточек счета/дела: <b>{invoiceMigrationResult.representativePaymentsUpdated}</b></li>
                      <li>Legacy-групп по ключу: <b>{invoiceMigrationResult.distinctLegacyInvoiceKeys}</b></li>
                      <li>Дубликатов ключей в счетах: <b>{invoiceMigrationResult.existingDuplicateInvoiceKeys}</b></li>
                      <li>Общих счетов на несколько ключей: <b>{invoiceMigrationResult.sharedInvoiceEntities}</b></li>
                      <li>Групп, которые пришлось разделить: <b>{invoiceMigrationResult.splitSharedInvoiceGroups}</b></li>
                    </ul>
                  </div>
                )}
              </div>
            )}


            {activeSection === 'integrations' && canViewIntegrationSettings && (
              <div className="space-y-6">
                <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                  <div className="flex flex-col gap-4">
                    <div>
                      <h4 className="text-xl font-semibold text-gray-900">Разделы интеграций</h4>
                      <p className="mt-1 text-sm text-gray-600">
                        Настройки разнесены по вкладкам, чтобы ключи, календарная синхронизация и судебные проверки не смешивались на одном экране.
                      </p>
                    </div>
                    <div className="grid gap-3 lg:grid-cols-4" role="tablist" aria-label="Разделы ключей интеграций">
                      {INTEGRATION_SETTINGS_TABS.map((tab) => {
                        const Icon = tab.icon;
                        const isActive = activeIntegrationTab === tab.id;
                        return (
                          <button
                            key={tab.id}
                            type="button"
                            role="tab"
                            aria-selected={isActive}
                            onClick={() => setActiveIntegrationTab(tab.id)}
                            className={`flex min-h-[104px] items-start gap-3 rounded-2xl border px-4 py-3 text-left transition ${
                              isActive
                                ? 'border-blue-200 bg-blue-50 text-blue-950 shadow-sm'
                                : 'border-gray-200 bg-white text-gray-700 hover:border-blue-100 hover:bg-blue-50/50'
                            }`}
                          >
                            <span className={`mt-0.5 inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-xl ${
                              isActive ? 'bg-blue-100 text-blue-700' : 'bg-slate-100 text-slate-500'
                            }`}>
                              <Icon className="h-5 w-5" />
                            </span>
                            <span className="min-w-0">
                              <span className="block text-sm font-semibold">{tab.label}</span>
                              <span className="mt-1 block text-xs leading-5 text-gray-600">{tab.description}</span>
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  </div>
                </div>

                {activeIntegrationTab === 'feature-flags' && (
                  <div role="tabpanel">
                    <FeatureFlagSettings />
                  </div>
                )}

                {activeIntegrationTab === 'calendars' && (
                  <div role="tabpanel">
                    <CalendarSyncProviderSettings />
                  </div>
                )}

                {activeIntegrationTab === 'courts' && (
                  <div className="space-y-6" role="tabpanel">
                    <CourtSyncSchedulerSettings />
                    <CourtSyncJournalSettings />
                  </div>
                )}

                {activeIntegrationTab === 'dadata' && (
                  <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm" role="tabpanel">
                  <div className="mb-6">
                    <h4 className="text-xl font-semibold text-gray-900">DaData</h4>
                  </div>

                  <div className="rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
                    <div>
                      <h5 className="text-base font-semibold text-gray-900">Ключи доступа</h5>
                      <p className="mt-1 text-sm text-gray-600">
                        Токен и секретный ключ для подсказок, нормализации и проверок через DaData.
                      </p>
                    </div>

                    <div className="mt-4 grid gap-4 md:grid-cols-2">
                      <label className="block">
                        <span className="text-sm font-medium text-gray-700">DaData API token</span>
                        <input
                          type="password"
                          autoComplete="new-password"
                          value={dadataToken}
                          onChange={(event) => setDadataToken(event.target.value)}
                          disabled={credentialsLoading || !canEditIntegrationSettings || dadataSettings?.canEdit === false}
                          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                          placeholder={dadataSettings?.apiKeyConfigured ? `Сохранён: ${dadataSettings.apiKeyMasked}` : 'Введите токен'}
                        />
                      </label>
                      <label className="block">
                        <span className="text-sm font-medium text-gray-700">DaData Secret key</span>
                        <input
                          type="password"
                          autoComplete="new-password"
                          value={dadataSecret}
                          onChange={(event) => setDadataSecret(event.target.value)}
                          disabled={credentialsLoading || !canEditIntegrationSettings || dadataSettings?.canEdit === false}
                          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                          placeholder={dadataSettings?.secretConfigured ? `Сохранён: ${dadataSettings.secretMasked}` : 'Введите секретный ключ'}
                        />
                      </label>
                    </div>

                    <p className="mt-3 text-xs text-gray-500">
                      Сохранённые значения не показываются в браузере. Чтобы заменить ключ, введите новое значение; пустое поле сохранит текущий ключ без изменений.
                    </p>
                    {!canEditIntegrationSettings || dadataSettings?.canEdit === false ? (
                      <p className="mt-2 text-xs text-amber-700">
                        У вас есть доступ только к просмотру: ключи скрыты и недоступны для изменения.
                      </p>
                    ) : null}

                    <div className="mt-6 flex min-h-10 items-end justify-between gap-3">
                      {credentialsMessage ? (
                        <span className="text-sm text-gray-600">{credentialsMessage}</span>
                      ) : (
                        <span />
                      )}
                      <button
                        type="button"
                        onClick={saveDaDataCredentials}
                        disabled={credentialsLoading || !canEditIntegrationSettings || dadataSettings?.canEdit === false}
                        className="ml-auto inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        <Save className="h-4 w-4" />
                        <span>{credentialsLoading ? 'Сохраняем…' : 'Сохранить'}</span>
                      </button>
                    </div>
                  </div>
                </div>
                )}
              </div>
            )}

            {activeSection === 'form-requirements' && !restrictedSettingsMode && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                {formRequirementSections.length === 0 && !requirementsLoading && (
                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
                    Нет доступных полей для настройки.
                  </div>
                )}
                {formRequirementSections.length > 0 && (
                  <div className="space-y-4">
                    <div className="flex flex-wrap gap-2 border-b border-gray-200 pb-3">
                      {formRequirementSections.map((section) => (
                        <button
                          key={section.key}
                          type="button"
                          onClick={() => setSelectedRequirementsSection(section.key)}
                          className={`rounded-lg px-3 py-2 text-sm font-medium transition ${
                            selectedRequirementsSection === section.key
                              ? 'bg-blue-50 text-blue-700 border border-blue-200'
                              : 'text-gray-700 hover:bg-gray-50 border border-gray-200'
                          }`}
                        >
                          {section.title}
                        </button>
                      ))}
                    </div>

                    {formRequirementSections
                      .filter((section) => section.key === selectedRequirementsSection)
                      .map((section) => (
                        <div key={section.key} className="rounded-xl border border-gray-200 p-4">
                          <h4 className="text-sm font-semibold text-gray-900 mb-3">{section.title}</h4>
                          {Object.entries(section.fields.reduce<Record<string, typeof section.fields>>((acc, field) => {
                            const category = field.category || 'Основные';
                            if (!acc[category]) acc[category] = [];
                            acc[category].push(field);
                            return acc;
                          }, {})).map(([category, fields]) => (
                            <div key={category} className="mb-4 last:mb-0">
                              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">{category}</div>
                              <div className="grid gap-2 md:grid-cols-2">
                                {fields.map((field) => {
                                  const id = `${section.key}:${field.key}`;
                                  const checked = requiredFieldKeys.has(id);
                                  return (
                                    <label key={id} className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm">
                                      <SystemCheckbox
                                        type="checkbox"
                                        checked={checked}
                                        disabled={requirementsLoading}
                                        onChange={(event) => void toggleRequiredField(section.key, field.key, event.target.checked)}
                                        className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                      />
                                      <span>{field.label}</span>
                                    </label>
                                  );
                                })}
                              </div>
                            </div>
                          ))}
                        </div>
                      ))}
                  </div>
                )}
                {requirementsMessage && <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{requirementsMessage}</div>}
              </div>
            )}

            {activeSection === 'db-operations' && !restrictedSettingsMode && activeDatabaseOperationsSubsection === 'addresses' && (
              <>
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                  <label className="flex items-start gap-3 text-sm text-gray-700">
                    <SystemCheckbox
                      type="checkbox"
                      checked={validateWithDadata}
                      onChange={(event) => setValidateWithDadata(event.target.checked)}
                      className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span>
                      Переносить <b>с проверкой через DaData</b> (если выключить — перенос будет без DaData)
                    </span>
                  </label>

                  <div className="flex items-center gap-3">
                    <button
                      onClick={runMigration}
                      disabled={loading}
                      className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60 disabled:cursor-not-allowed"
                    >
                      {loading ? 'Переносим…' : 'Перенести старые адреса'}
                    </button>
                  </div>

                  {error && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {error}
                    </div>
                  )}

                  {result && (
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium mb-2">Отчёт по переносу</p>
                      <ul className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
                        <li>
                          Проверено всего карточек: <b>{result.scanned}</b>
                        </li>
                        <li>
                          Из них юр. лиц: <b>{result.scannedLegalEntities ?? 0}</b>
                        </li>
                        <li>
                          Из них физ. лиц: <b>{result.scannedClients ?? 0}</b>
                        </li>
                        <li>
                          Добавлено новых адресов: <b>{result.added}</b>
                        </li>
                        <li>
                          Обновлено существующих записей: <b>{result.updated}</b>
                        </li>
                        <li>
                          Пропущено дубликатов: <b>{result.duplicateSkipped}</b>
                        </li>
                        <li>
                          Пропущено пустых: <b>{result.emptySkipped}</b>
                        </li>
                        <li>
                          Ошибок: <b>{result.failed}</b>
                        </li>
                        <li>
                          Совпадений DaData: <b>{result.dadataMatched}</b>
                        </li>
                        <li>
                          Без совпадения DaData: <b>{result.dadataNotMatched}</b>
                        </li>
                        <li>
                          Режим: <b>{result.validateWithDadata ? 'с DaData' : 'без DaData'}</b>
                        </li>
                      </ul>
                    </div>
                  )}
                </div>

                {result && result.details.length > 0 && (
                  <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
                    <h3 className="text-lg font-semibold text-gray-900 mb-3">Детальный отчёт</h3>
                    <div className="overflow-x-auto">
                      <table className="min-w-full table-fixed text-sm">
                        <colgroup>
                          <col style={{ width: '18%' }} />
                          <col style={{ width: '22%' }} />
                          <col style={{ width: '22%' }} />
                          <col style={{ width: '12%' }} />
                          <col style={{ width: '10%' }} />
                          <col style={{ width: '16%' }} />
                        </colgroup>
                        <thead>
                          <tr className="text-left text-gray-500 border-b border-gray-200">
                            <th className="py-2 pr-4">Карточка</th>
                            <th className="py-2 pr-4">Legacy адрес</th>
                            <th className="py-2 pr-4">Нормализованный адрес</th>
                            <th className="py-2 pr-4">Действие</th>
                            <th className="py-2 pr-4">DaData</th>
                            <th className="py-2">Комментарий</th>
                          </tr>
                        </thead>
                        <tbody>
                          {pagedMigrationDetails.map((item, idx) => (
                            <tr
                              key={`${item.ownerType ?? 'legal'}-${item.ownerId ?? item.legalEntityId ?? item.clientId ?? idx}-${idx}`}
                              className="border-b border-gray-100 align-top"
                            >
                              <td className="py-2 pr-4">
                                <div className="font-medium text-gray-900">{item.ownerName ?? item.legalEntityName ?? item.clientName ?? '—'}</div>
                                <div className="text-xs text-gray-500">
                                  {item.ownerType === 'client' ? 'Физ. лицо' : 'Юр. лицо'} · ID:{' '}
                                  {item.ownerId ?? item.legalEntityId ?? item.clientId ?? '—'}
                                </div>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">{item.legacyAddress || '—'}</td>
                              <td className="py-2 pr-4 text-gray-700">{item.normalizedAddress || '—'}</td>
                              <td className="py-2 pr-4">
                                <span className="inline-flex rounded-md bg-slate-100 px-2 py-1 text-xs font-medium text-slate-700">
                                  {item.action}
                                </span>
                              </td>
                              <td className="py-2 pr-4 text-gray-700">
                                {item.dadataMatched === null || item.dadataMatched === undefined
                                  ? '—'
                                  : item.dadataMatched
                                    ? 'Найдено'
                                    : 'Не найдено'}
                              </td>
                              <td className="py-2 text-gray-700">{item.message || '—'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    <TablePagination
                      page={detailsPage}
                      totalPages={migrationDetailsTotalPages}
                      totalItems={migrationDetails.length}
                      pageSize={detailsPageSize}
                      pageSizeOptions={DETAILS_PAGE_SIZE_OPTIONS}
                      onPageChange={setDetailsPage}
                      onPageSizeChange={setDetailsPageSize}
                    />
                  </div>
                )}
              </>
            )}

            {activeSection === 'db-operations' && !restrictedSettingsMode && activeDatabaseOperationsSubsection === 'dictionary-merge' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div className="grid gap-4 md:grid-cols-3">
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">Справочник</span>
                    <SystemSelect
                      value={mergeKind}
                      onChange={(event) => setMergeKind(event.target.value as MergeKind)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                    >
                      <option value="organization-forms">Формы организации</option>
                      <option value="countries">Страны</option>
                      <option value="base-currencies">Базовые валюты</option>
                      <option value="service-languages">Языки сервиса</option>
                      <option value="timezones">Часовые пояса</option>
                      <option value="tax-systems">Системы налогообложения</option>
                      <option value="tax-system-modes">Режимы налогообложения</option>
                    </SystemSelect>
                  </label>

                  <label className="text-sm text-gray-700 md:col-span-2">
                    <span className="mb-1 block">Группа дублей</span>
                    <SystemSelect
                      value={selectedGroupKey}
                      onChange={(event) => {
                        const canonical = event.target.value;
                        setSelectedGroupKey(canonical);
                        const group = duplicates.find((item) => item.canonical === canonical);
                        setPreferredId(group?.items?.[0]?.id ?? null);
                      }}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                    >
                      <option value="">Выберите группу</option>
                      {duplicates.map((group) => (
                        <option key={group.canonical} value={group.canonical}>
                          {group.items.map((item) => item.name).join(' / ')}
                        </option>
                      ))}
                    </SystemSelect>
                  </label>
                </div>

                {selectedGroupKey && (
                  <div className="rounded-xl border border-gray-200 p-4">
                    <p className="mb-2 text-sm font-medium text-gray-800">Выберите базовое значение</p>
                    <div className="space-y-2">
                      {(duplicates.find((item) => item.canonical === selectedGroupKey)?.items ?? []).map((item) => (
                        <label key={item.id} className="flex items-start gap-2 text-sm text-gray-700">
                          <input
                            type="radio"
                            checked={preferredId === item.id}
                            onChange={() => setPreferredId(item.id)}
                            className="mt-1"
                          />
                          <span>
                            <b>{item.name}</b>
                            <span className="text-gray-500"> · {item.description || '—'}</span>
                          </span>
                        </label>
                      ))}
                    </div>
                  </div>
                )}

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runMerge}
                    disabled={mergeLoading || !selectedGroupKey}
                    className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
                  >
                    {mergeLoading ? 'Объединяем…' : 'Объединить выбранные дубли'}
                  </button>
                  <button
                    type="button"
                    onClick={handleReloadDuplicates}
                    disabled={mergeLoading}
                    className="px-4 py-2 rounded-lg border border-gray-300 text-sm font-medium hover:bg-gray-50 disabled:opacity-60"
                  >
                    Обновить список
                  </button>
                </div>

                {mergeError && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{mergeError}</div>}
                {mergeMessage && <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">{mergeMessage}</div>}

                {!mergeLoading && duplicates.length === 0 && (
                  <div className="rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                    Дубли в выбранном справочнике не найдены.
                  </div>
                )}
              </div>
            )}

            {activeSection === 'db-operations' && !restrictedSettingsMode && activeDatabaseOperationsSubsection === 'users-merge' && (
              <UserMergeSettings />
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
