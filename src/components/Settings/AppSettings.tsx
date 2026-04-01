import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { TablePagination } from '../ui/TablePagination';
import { AuthProviderSettings } from './AuthProviderSettings';
import type {
  DatabaseBackupOverviewDto,
  DatabaseImportResultDto,
  DatabaseStatusDto,
  FormRequirementSectionMetadataDto,
  InvoiceCounterpartyCleanupResultDto,
  InvoiceLegacyMigrationSummaryDto,
  InvoiceNumberingSettingsDto,
  InvoiceRulesSettingsDto,
  LegacyAddressMigrationResult,
  UserActivityCleanupResultDto,
  UserActivityCompactionResultDto,
  UserActivityMaintenanceDto,
} from '../../services/api';

type SettingsSection =
  | 'general'
  | 'integrations'
  | 'auth-providers'
  | 'db-maintenance'
  | 'db-import'
  | 'db-invoices'
  | 'db-addresses'
  | 'dictionary-merge'
  | 'form-requirements';
type MergeKind = 'countries' | 'base-currencies' | 'service-languages' | 'organization-forms' | 'timezones' | 'tax-systems' | 'tax-system-modes';
const DETAILS_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

export function AppSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const [activeSection, setActiveSection] = useState<SettingsSection>('integrations');
  const [loading, setLoading] = useState(false);
  const [validateWithDadata, setValidateWithDadata] = useState(true);
  const [result, setResult] = useState<LegacyAddressMigrationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dadataToken, setDadataToken] = useState('');
  const [dadataSecret, setDadataSecret] = useState('');
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
  const [invoiceCounterpartyCleanupLoading, setInvoiceCounterpartyCleanupLoading] = useState(false);
  const [invoiceCounterpartyCleanupMessage, setInvoiceCounterpartyCleanupMessage] = useState<string | null>(null);
  const [invoiceCounterpartyCleanupResult, setInvoiceCounterpartyCleanupResult] = useState<InvoiceCounterpartyCleanupResultDto | null>(null);
  const [invoiceMigrationLoading, setInvoiceMigrationLoading] = useState(false);
  const [invoiceMigrationError, setInvoiceMigrationError] = useState<string | null>(null);
  const [invoiceMigrationResult, setInvoiceMigrationResult] = useState<InvoiceLegacyMigrationSummaryDto | null>(null);
  const [userActivityMaintenance, setUserActivityMaintenance] = useState<UserActivityMaintenanceDto | null>(null);
  const [userActivityLoggingEnabled, setUserActivityLoggingEnabled] = useState(true);
  const [userActivityRetentionDays, setUserActivityRetentionDays] = useState(0);
  const [userActivityMaintenanceLoading, setUserActivityMaintenanceLoading] = useState(false);
  const [userActivitySettingsSaving, setUserActivitySettingsSaving] = useState(false);
  const [userActivityCleanupLoading, setUserActivityCleanupLoading] = useState(false);
  const [userActivityCompactionLoading, setUserActivityCompactionLoading] = useState(false);
  const [userActivityMaintenanceMessage, setUserActivityMaintenanceMessage] = useState<string | null>(null);
  const [userActivityMaintenanceError, setUserActivityMaintenanceError] = useState<string | null>(null);
  const [userActivityCleanupResult, setUserActivityCleanupResult] = useState<UserActivityCleanupResultDto | null>(null);
  const [userActivityCompactionResult, setUserActivityCompactionResult] = useState<UserActivityCompactionResultDto | null>(null);
  const [databaseStatus, setDatabaseStatus] = useState<DatabaseStatusDto | null>(null);
  const [databaseStatusLoading, setDatabaseStatusLoading] = useState(false);
  const [databaseBackupOverview, setDatabaseBackupOverview] = useState<DatabaseBackupOverviewDto | null>(null);
  const [databaseBackupAutomaticEnabled, setDatabaseBackupAutomaticEnabled] = useState(true);
  const [databaseBackupScheduleLocal, setDatabaseBackupScheduleLocal] = useState('00:00,06:00,12:00,18:00');
  const [databaseBackupRetentionDays, setDatabaseBackupRetentionDays] = useState(8);
  const [databaseBackupLoading, setDatabaseBackupLoading] = useState(false);
  const [databaseBackupSaving, setDatabaseBackupSaving] = useState(false);
  const [databaseBackupCreating, setDatabaseBackupCreating] = useState(false);
  const [databaseBackupMessage, setDatabaseBackupMessage] = useState<string | null>(null);
  const [databaseBackupError, setDatabaseBackupError] = useState<string | null>(null);
  const [databaseImportFile, setDatabaseImportFile] = useState<File | null>(null);
  const [databaseImportLoading, setDatabaseImportLoading] = useState(false);
  const [databaseImportError, setDatabaseImportError] = useState<string | null>(null);
  const [databaseImportResult, setDatabaseImportResult] = useState<DatabaseImportResultDto | null>(null);
  const [detailsPage, setDetailsPage] = useState(1);
  const [detailsPageSize, setDetailsPageSize] = useState(DETAILS_PAGE_SIZE_OPTIONS[1]);

  const activeSectionTitle = useMemo(() => {
    if (activeSection === 'general') return 'Основные';
    if (activeSection === 'integrations') return 'Ключи интеграций';
    if (activeSection === 'auth-providers') return 'Внешняя авторизация';
    if (activeSection === 'db-maintenance') return 'Работа с базой · Обслуживание';
    if (activeSection === 'db-import') return 'Работа с базой · Импорт SQLite';
    if (activeSection === 'dictionary-merge') return 'Слияние дублей справочников';
    if (activeSection === 'form-requirements') return 'Обязательные поля форм';
    if (activeSection === 'db-invoices') return 'Работа с базой · Счета';
    return 'Работа с базой · Адреса';
  }, [activeSection]);

  const migrationDetails = result?.details ?? [];
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

  const formatBytes = (bytes: number) => {
    if (!Number.isFinite(bytes) || bytes <= 0) {
      return '0 Б';
    }

    const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
    let value = bytes;
    let index = 0;

    while (value >= 1024 && index < units.length - 1) {
      value /= 1024;
      index += 1;
    }

    return `${value >= 100 || index === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[index]}`;
  };

  const formatDateTime = (value?: string | null) => {
    if (!value) {
      return '—';
    }

    try {
      return new Date(value).toLocaleString('ru-RU');
    } catch {
      return value;
    }
  };

  const applyDatabaseBackupOverview = (overview: DatabaseBackupOverviewDto | null) => {
    setDatabaseBackupOverview(overview);
    setDatabaseBackupAutomaticEnabled(overview?.automaticEnabled ?? true);
    setDatabaseBackupScheduleLocal(overview?.scheduleLocal ?? '00:00,06:00,12:00,18:00');
    setDatabaseBackupRetentionDays(Math.max(1, Number(overview?.retentionDays ?? 8) || 8));
  };

  const loadDatabaseStatus = async () => {
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
  };

  const loadDatabaseBackups = async (options?: { silent?: boolean; showSuccessToast?: boolean }) => {
    if (!options?.silent) {
      setDatabaseBackupLoading(true);
    }

    try {
      const overview = await apiService.getDatabaseBackupOverview();
      applyDatabaseBackupOverview(overview);
      setDatabaseBackupError(null);
      if (options?.showSuccessToast) {
        toast.success('Список резервных копий обновлён', 'База данных');
      }
      return overview;
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось загрузить список резервных копий');
      setDatabaseBackupError(message);
      if (options?.showSuccessToast) {
        toast.error(message, 'База данных');
      }
      return null;
    } finally {
      if (!options?.silent) {
        setDatabaseBackupLoading(false);
      }
    }
  };

  const downloadDatabaseBackup = async (relativePath: string, fallbackFileName?: string) => {
    const response = await apiService.downloadDatabaseBackup(relativePath);
    const blobUrl = window.URL.createObjectURL(response.blob);
    const link = document.createElement('a');
    link.href = blobUrl;
    link.download = response.fileName || fallbackFileName || `database-backup${databaseBackupOverview?.fileExtension ?? ''}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.URL.revokeObjectURL(blobUrl);
  };

  const createAndDownloadDatabaseBackup = async () => {
    setDatabaseBackupCreating(true);
    setDatabaseBackupError(null);
    setDatabaseBackupMessage(null);

    try {
      const result = await apiService.createDatabaseBackup();
      await downloadDatabaseBackup(result.backup.relativePath, result.backup.fileName);
      await loadDatabaseBackups({ silent: true });
      const message = 'Резервная копия создана, скачивание началось';
      setDatabaseBackupMessage(message);
      toast.success(message, 'База данных');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось создать резервную копию');
      setDatabaseBackupError(message);
      toast.error(message, 'База данных');
    } finally {
      setDatabaseBackupCreating(false);
    }
  };

  const saveDatabaseBackupSettings = async () => {
    setDatabaseBackupSaving(true);
    setDatabaseBackupError(null);
    setDatabaseBackupMessage(null);

    try {
      const overview = await apiService.updateDatabaseBackupSettings({
        automaticEnabled: databaseBackupAutomaticEnabled,
        scheduleLocal: databaseBackupScheduleLocal.trim(),
        retentionDays: Math.max(1, Math.floor(Number(databaseBackupRetentionDays) || 1)),
      });
      applyDatabaseBackupOverview(overview);
      const message = 'Настройки резервного копирования сохранены';
      setDatabaseBackupMessage(message);
      toast.success(message, 'База данных');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить настройки резервного копирования');
      setDatabaseBackupError(message);
      toast.error(message, 'База данных');
    } finally {
      setDatabaseBackupSaving(false);
    }
  };

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

  const loadDuplicates = async (
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
  };

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


  const loadDaDataCredentials = async () => {
    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      const [tokenSetting, secretSetting] = await Promise.all([
        apiService.getUserSetting('integrations.dadata.apiKey').catch(() => null),
        apiService.getUserSetting('integrations.dadata.secret').catch(() => null),
      ]);
      setDadataToken(tokenSetting?.value ?? '');
      setDadataSecret(secretSetting?.value ?? '');
    } catch (e) {
      setCredentialsMessage(e instanceof Error ? e.message : 'Не удалось загрузить ключи DaData');
    } finally {
      setCredentialsLoading(false);
    }
  };

  const saveDaDataCredentials = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: 'Сохранить ключи DaData в пользовательских настройках',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setCredentialsLoading(true);
    setCredentialsMessage(null);
    try {
      await Promise.all([
        apiService.upsertUserSetting('integrations.dadata.apiKey', dadataToken.trim()),
        apiService.upsertUserSetting('integrations.dadata.secret', dadataSecret.trim()),
      ]);
      const message = 'Ключи DaData сохранены';
      setCredentialsMessage(message);
      toast.success(message, 'Интеграции');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить ключи DaData');
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

  const runInvoiceCounterpartyCleanup = async () => {
    const approved = await confirm({
      title: 'Подтверждение очистки',
      description: 'Очистить содержимое поля "От кого счёт" во всех старых счетах канонической структуры.',
      confirmLabel: 'Очистить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setInvoiceCounterpartyCleanupLoading(true);
    setInvoiceCounterpartyCleanupMessage(null);
    try {
      const response = await apiService.cleanupInvoiceCounterpartyNames();
      setInvoiceCounterpartyCleanupResult(response);
      const message = 'Очистка поля "От кого счёт" выполнена';
      setInvoiceCounterpartyCleanupMessage(message);
      toast.success(message, 'Счета');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось очистить поле "От кого счёт"');
      setInvoiceCounterpartyCleanupResult(null);
      setInvoiceCounterpartyCleanupMessage(message);
      toast.error(message, 'Счета');
    } finally {
      setInvoiceCounterpartyCleanupLoading(false);
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

  const loadUserActivityMaintenance = async (options?: { showSuccessToast?: boolean }) => {
    setUserActivityMaintenanceLoading(true);
    setUserActivityMaintenanceError(null);
    try {
      const response = await apiService.getUserActivityMaintenance();
      setUserActivityMaintenance(response);
      setUserActivityLoggingEnabled(response.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(response.retentionDays ?? 0) || 0));
      if (options?.showSuccessToast) {
        toast.success('Статистика журнала и базы обновлена', 'База данных');
      }
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось загрузить состояние журнала и базы данных');
      setUserActivityMaintenanceError(message);
      if (options?.showSuccessToast) {
        toast.error(message, 'База данных');
      }
    } finally {
      setUserActivityMaintenanceLoading(false);
    }
  };

  const saveUserActivitySettings = async () => {
    const approved = await confirm({
      title: 'Подтверждение действия',
      description: userActivityLoggingEnabled
        ? 'Включить журналирование действий пользователей для всех пользователей системы.'
        : 'Отключить журналирование действий пользователей для всех пользователей системы.',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setUserActivitySettingsSaving(true);
    setUserActivityMaintenanceError(null);
    setUserActivityMaintenanceMessage(null);
    try {
      const normalizedRetentionDays = Math.max(0, Math.floor(Number(userActivityRetentionDays) || 0));
      const response = await apiService.updateUserActivitySettings({
        isEnabled: userActivityLoggingEnabled,
        retentionDays: normalizedRetentionDays > 0 ? normalizedRetentionDays : null,
      });
      setUserActivityMaintenance(response);
      setUserActivityLoggingEnabled(response.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(response.retentionDays ?? 0) || 0));
      const message = response.isEnabled
        ? normalizedRetentionDays > 0
          ? `Журналирование включено, автоочистка: ${normalizedRetentionDays} дн.`
          : 'Журналирование включено, автоочистка выключена'
        : normalizedRetentionDays > 0
          ? `Журналирование отключено, автоочистка: ${normalizedRetentionDays} дн.`
          : 'Журналирование отключено, автоочистка выключена';
      setUserActivityMaintenanceMessage(message);
      toast.success(message, 'База данных');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось сохранить настройку журналирования');
      setUserActivityMaintenanceError(message);
      toast.error(message, 'База данных');
    } finally {
      setUserActivitySettingsSaving(false);
    }
  };

  const runUserActivityCleanup = async () => {
    const approved = await confirm({
      title: 'Подтверждение очистки',
      description: 'Удалить все записи журнала активности. Это очистит таблицу логов, но для уменьшения файла SQLite может понадобиться отдельное уплотнение базы.',
      confirmLabel: 'Удалить записи',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setUserActivityCleanupLoading(true);
    setUserActivityMaintenanceError(null);
    setUserActivityMaintenanceMessage(null);
    try {
      const response = await apiService.deleteUserActivityLogs();
      setUserActivityCleanupResult(response);
      setUserActivityCompactionResult(null);
      setUserActivityMaintenance(response.maintenance);
      setUserActivityLoggingEnabled(response.maintenance.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(response.maintenance.retentionDays ?? 0) || 0));
      const message = `Журнал активности очищен. Удалено записей: ${response.deletedLogs}`;
      setUserActivityMaintenanceMessage(message);
      toast.success(message, 'База данных');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось очистить журнал активности');
      setUserActivityMaintenanceError(message);
      toast.error(message, 'База данных');
    } finally {
      setUserActivityCleanupLoading(false);
    }
  };

  const runUserActivityCompaction = async () => {
    const approved = await confirm({
      title: 'Подтверждение уплотнения',
      description: 'Выполнить `VACUUM` и очистить WAL-файл. Операция может занять время и временно увеличить нагрузку на SQLite.',
      confirmLabel: 'Уплотнить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setUserActivityCompactionLoading(true);
    setUserActivityMaintenanceError(null);
    setUserActivityMaintenanceMessage(null);
    try {
      const response = await apiService.compactUserActivityDatabase();
      setUserActivityCompactionResult(response);
      setUserActivityCleanupResult(null);
      setUserActivityMaintenance(response.maintenance);
      setUserActivityLoggingEnabled(response.maintenance.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(response.maintenance.retentionDays ?? 0) || 0));
      const savedBytes = Math.max(0, response.before.totalSizeBytes - response.after.totalSizeBytes);
      const message = `Уплотнение базы завершено. Освобождено: ${formatBytes(savedBytes)}`;
      setUserActivityMaintenanceMessage(message);
      toast.success(message, 'База данных');
    } catch (e) {
      const message = getErrorMessage(e, 'Не удалось уплотнить базу данных');
      setUserActivityMaintenanceError(message);
      toast.error(message, 'База данных');
    } finally {
      setUserActivityCompactionLoading(false);
    }
  };

  const hasUserActivitySettingsChanges = useMemo(
    () =>
      userActivityMaintenance !== null
      && (
        userActivityLoggingEnabled !== userActivityMaintenance.isEnabled
        || userActivityRetentionDays !== Math.max(0, Number(userActivityMaintenance.retentionDays ?? 0) || 0)
      ),
    [userActivityLoggingEnabled, userActivityMaintenance, userActivityRetentionDays],
  );

  const hasDatabaseBackupSettingsChanges = useMemo(
    () =>
      databaseBackupOverview !== null
      && (
        databaseBackupAutomaticEnabled !== databaseBackupOverview.automaticEnabled
        || databaseBackupScheduleLocal.trim() !== databaseBackupOverview.scheduleLocal.trim()
        || databaseBackupRetentionDays !== Math.max(1, Number(databaseBackupOverview.retentionDays ?? 0) || 1)
      ),
    [
      databaseBackupAutomaticEnabled,
      databaseBackupOverview,
      databaseBackupRetentionDays,
      databaseBackupScheduleLocal,
    ],
  );

  useEffect(() => {
    if (activeSection === 'integrations') {
      void loadDaDataCredentials();
    }
    if (activeSection === 'general') {
      void loadInvoiceNumbering();
      void loadInvoiceRules();
    }
    if (activeSection === 'db-maintenance') {
      void loadDatabaseStatus();
      void loadDatabaseBackups();
      void loadUserActivityMaintenance();
    }
    if (activeSection === 'db-import') {
      void loadDatabaseStatus();
    }
    if (activeSection === 'dictionary-merge') {
      void loadDuplicates(mergeKind);
    }
    if (activeSection === 'form-requirements') {
      void loadFormRequirements();
    }
  }, [activeSection, mergeKind]);

  return (
    <div className="p-6 lg:p-8">
      <div className="w-full">
        <div className="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-6">
          <aside className="bg-white border border-gray-200 rounded-2xl shadow-sm p-4 h-fit">
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Настройки</h2>

            <nav aria-label="Разделы настроек" className="space-y-2">
              <button
                type="button"
                onClick={() => setActiveSection('general')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'general'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Основные
              </button>

              <button
                type="button"
                onClick={() => setActiveSection('integrations')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'integrations'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Ключи интеграций
              </button>

              <button
                type="button"
                onClick={() => setActiveSection('auth-providers')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'auth-providers'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Внешняя авторизация
              </button>

              <button
                type="button"
                onClick={() => setActiveSection('form-requirements')}
                className={`w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                  activeSection === 'form-requirements'
                    ? 'bg-blue-50 text-blue-700 border border-blue-200'
                    : 'text-gray-700 hover:bg-gray-50 border border-transparent'
                }`}
              >
                Обязательные поля форм
              </button>

              <div className="rounded-xl border border-gray-200 bg-gray-50/60 p-2">
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Работа с базой</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-maintenance')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-maintenance'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Обслуживание
                </button>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-invoices')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-invoices'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Счета
                </button>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-addresses')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-addresses'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Адреса
                </button>
                <button
                  type="button"
                  onClick={() => setActiveSection('db-import')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'db-import'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Импорт SQLite
                </button>
              </div>

              <div className="rounded-xl border border-gray-200 bg-gray-50/60 p-2">
                <div className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-gray-500">Справочники</div>
                <button
                  type="button"
                  onClick={() => setActiveSection('dictionary-merge')}
                  className={`mt-1 w-full text-left rounded-lg px-3 py-2 text-sm font-medium transition ${
                    activeSection === 'dictionary-merge'
                      ? 'bg-blue-50 text-blue-700 border border-blue-200'
                      : 'text-gray-700 hover:bg-white border border-transparent'
                  }`}
                >
                  ↳ Объединение дублей
                </button>
              </div>
            </nav>
          </aside>

          <section className="space-y-6">
            <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6">
              <h3 className="text-lg font-semibold text-gray-900">{activeSectionTitle}</h3>
              <p className="text-sm text-gray-600 mt-1">
                {activeSection === 'general'
                  ? 'Базовые параметры системы. Раздел подготовлен для последующего расширения.'
                  : activeSection === 'integrations'
                    ? 'Хранение и управление ключами доступа к внешним сервисам.'
                    : activeSection === 'auth-providers'
                      ? 'Настройка входа через Google, VK, Yandex, Telegram Bot и Telegram Widget.'
                    : activeSection === 'db-maintenance'
                      ? 'Управление журналом действий пользователей и обслуживающие операции для SQLite: очистка, checkpoint и уплотнение базы.'
                    : activeSection === 'form-requirements'
                      ? 'Администратор определяет обязательные поля отдельно для каждого раздела.'
                    : activeSection === 'dictionary-merge'
                      ? 'Безопасное транзакционное объединение дублей в справочниках с заменой ссылок во всех карточках юрлиц.'
                    : activeSection === 'db-import'
                      ? 'Загрузка SQLite-файла в текущую базу с автоматическим объединением найденных записей и подробным отчётом по каждой таблице.'
                    : activeSection === 'db-invoices'
                      ? 'Операции по формированию канонических счетов и перепривязке платежей к актуальным сущностям счета.'
                      : 'Операции по переносу и выравниванию адресов. Позже здесь можно будет добавить другие сервисные процедуры.'}
              </p>
            </div>

            {activeSection === 'general' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Нумерация счетов</h4>
                  <p className="text-sm text-gray-600 mt-1">Номер сбрасывается каждый год. Можно выбрать стартовый номер и стратегию подбора следующего номера.</p>
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
                  <select
                    value={invoiceNumbering.allocationStrategy}
                    onChange={(e) => setInvoiceNumbering((prev) => ({ ...prev, allocationStrategy: e.target.value as InvoiceNumberingSettingsDto['allocationStrategy'] }))}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  >
                    <option value="max-plus-one">Максимальный номер + 1</option>
                    <option value="first-free">Первый свободный номер от стартового</option>
                  </select>
                </label>

                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={saveInvoiceNumbering}
                    disabled={invoiceNumberingLoading}
                    className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {invoiceNumberingLoading ? 'Сохранение…' : 'Сохранить'}
                  </button>
                  {invoiceNumberingMessage && <span className="text-sm text-gray-600">{invoiceNumberingMessage}</span>}
                </div>

                <div className="border-t border-gray-200 pt-4">
                  <h4 className="text-base font-semibold text-gray-900">Ограничения счёта</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Временно можно ограничить количество связанных дел и актов в одном счёте. Ограничение действует и в форме, и на сервере.
                  </p>

                  <div className="mt-4 grid gap-4 md:grid-cols-2">
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

                  <div className="mt-4 flex items-center gap-3">
                    <button
                      type="button"
                      onClick={saveInvoiceRules}
                      disabled={invoiceRulesLoading}
                      className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {invoiceRulesLoading ? 'Сохранение…' : 'Сохранить ограничения'}
                    </button>
                    {invoiceRulesMessage && <span className="text-sm text-gray-600">{invoiceRulesMessage}</span>}
                  </div>
                </div>

                <div className="border-t border-gray-200 pt-4">
                  <h4 className="text-base font-semibold text-gray-900">Очистка устаревшего поля счета</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Удаляет сохраненные значения поля "От кого счёт" и снимает старое требование заполнения.
                  </p>
                  <div className="mt-3 flex items-center gap-3">
                    <button
                      type="button"
                      onClick={runInvoiceCounterpartyCleanup}
                      disabled={invoiceCounterpartyCleanupLoading}
                      className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {invoiceCounterpartyCleanupLoading ? 'Очищаем…' : 'Очистить поле'}
                    </button>
                    {invoiceCounterpartyCleanupMessage && (
                      <span className="text-sm text-gray-600">{invoiceCounterpartyCleanupMessage}</span>
                    )}
                  </div>
                  {invoiceCounterpartyCleanupResult && (
                    <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p>Очищено счетов: {invoiceCounterpartyCleanupResult.cleared}</p>
                      <p>Удалено legacy-требований: {invoiceCounterpartyCleanupResult.removedRequirements}</p>
                    </div>
                  )}
                </div>
              </div>
            )}

            {activeSection === 'auth-providers' && <AuthProviderSettings />}

            {activeSection === 'db-maintenance' && (
              <div className="space-y-6">
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                  <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
                    <div>
                      <h4 className="text-base font-semibold text-gray-900">Журнал действий пользователей</h4>
                      <p className="mt-1 text-sm text-gray-600">
                        Глобальный переключатель для автоматического и ручного журналирования. При отключении новые записи в `UserActivityLogs` больше не создаются.
                      </p>
                    </div>
                    <button
                      type="button"
                      onClick={() => void loadUserActivityMaintenance({ showSuccessToast: true })}
                      disabled={userActivityMaintenanceLoading || userActivitySettingsSaving || userActivityCleanupLoading || userActivityCompactionLoading}
                      className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {userActivityMaintenanceLoading ? 'Обновляем…' : 'Обновить статистику'}
                    </button>
                  </div>

                  <label className="flex items-start gap-3 rounded-xl border border-gray-200 p-4 text-sm text-gray-700">
                    <input
                      type="checkbox"
                      checked={userActivityLoggingEnabled}
                      onChange={(event) => setUserActivityLoggingEnabled(event.target.checked)}
                      disabled={userActivityMaintenanceLoading || userActivitySettingsSaving}
                      className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span>
                      <span className="block font-medium text-gray-900">Включить журналирование действий пользователей</span>
                      <span className="mt-1 block text-gray-600">
                        Когда настройка выключена, система перестаёт писать автоматические API-логи и ручные события активности.
                      </span>
                    </span>
                  </label>

                  <label className="block rounded-xl border border-gray-200 p-4 text-sm text-gray-700">
                    <span className="block font-medium text-gray-900">Автоочистка журнала по сроку хранения</span>
                    <span className="mt-1 block text-gray-600">
                      Сервер каждые 12 часов удаляет записи старше указанного срока. Значение `0` отключает автоочистку.
                    </span>
                    <input
                      type="number"
                      min={0}
                      step={1}
                      value={userActivityRetentionDays}
                      onChange={(event) => setUserActivityRetentionDays(Math.max(0, Math.floor(Number(event.target.value) || 0)))}
                      disabled={userActivityMaintenanceLoading || userActivitySettingsSaving}
                      className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    />
                  </label>

                  <div className="flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={saveUserActivitySettings}
                      disabled={userActivityMaintenanceLoading || userActivitySettingsSaving || !hasUserActivitySettingsChanges}
                      className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {userActivitySettingsSaving ? 'Сохраняем…' : 'Сохранить настройку'}
                    </button>
                    <span className="text-sm text-gray-500">
                      Текущее состояние: {userActivityMaintenance?.isEnabled === false ? 'выключено' : 'включено'}
                    </span>
                    <span className="text-sm text-gray-500">
                      Автоочистка: {(userActivityMaintenance?.retentionDays ?? 0) > 0 ? `${userActivityMaintenance?.retentionDays} дн.` : 'выключена'}
                    </span>
                    {userActivityMaintenance?.updatedAt ? (
                      <span className="text-sm text-gray-500">
                        Обновлено: {formatDateTime(userActivityMaintenance.updatedAt)}
                      </span>
                    ) : null}
                  </div>

                  {userActivityMaintenanceError && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {userActivityMaintenanceError}
                    </div>
                  )}
                  {userActivityMaintenanceMessage && (
                    <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
                      {userActivityMaintenanceMessage}
                    </div>
                  )}

                  <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-5">
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Записей в журнале</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">
                        {userActivityMaintenanceLoading && !userActivityMaintenance ? '…' : userActivityMaintenance?.logEntries ?? 0}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Файл БД</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">
                        {formatBytes(userActivityMaintenance?.storage.databaseFileSizeBytes ?? 0)}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">WAL-файл</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">
                        {formatBytes(userActivityMaintenance?.storage.walFileSizeBytes ?? 0)}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Всего на диске</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">
                        {formatBytes(userActivityMaintenance?.storage.totalSizeBytes ?? 0)}
                      </div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Можно освободить</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">
                        {formatBytes(userActivityMaintenance?.storage.estimatedReclaimableBytes ?? 0)}
                      </div>
                    </div>
                  </div>

                  <div className="grid gap-4 md:grid-cols-2">
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Самая старая запись</div>
                      <div className="mt-2 text-sm text-gray-700">{formatDateTime(userActivityMaintenance?.oldestLogAt)}</div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Последняя запись</div>
                      <div className="mt-2 text-sm text-gray-700">{formatDateTime(userActivityMaintenance?.newestLogAt)}</div>
                    </div>
                  </div>
                </div>

                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                  <div>
                    <h4 className="text-base font-semibold text-gray-900">Очистка и уплотнение базы</h4>
                    <p className="mt-1 text-sm text-gray-600">
                      Очистка удаляет строки журнала. Уплотнение выполняет checkpoint и `VACUUM`, чтобы SQLite реально освободил место на диске.
                    </p>
                  </div>

                  {databaseStatus && !databaseStatus.supportsSqliteMaintenance && (
                    <div className="rounded-xl border border-blue-200 bg-blue-50 p-4 text-sm text-blue-900">
                      Текущее хранилище работает на {databaseStatus.displayName}. Очистка журнала доступна, а файловые операции SQLite вроде `VACUUM` и управления WAL здесь не применяются.
                    </div>
                  )}

                  <div className="flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={runUserActivityCleanup}
                      disabled={userActivityCleanupLoading || userActivityCompactionLoading}
                      className="rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 hover:bg-red-100 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {userActivityCleanupLoading ? 'Удаляем…' : 'Очистить журнал'}
                    </button>
                    <button
                      type="button"
                      onClick={runUserActivityCompaction}
                      disabled={userActivityCleanupLoading || userActivityCompactionLoading || databaseStatus?.supportsSqliteMaintenance === false}
                      className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {userActivityCompactionLoading ? 'Уплотняем…' : 'Уплотнить БД'}
                    </button>
                  </div>

                  <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
                    После удаления логов размер файла может не уменьшиться сразу. Это нормально для SQLite в режиме `WAL`: сначала освобождаются страницы, а затем их нужно собрать через уплотнение.
                  </div>

                  {userActivityCleanupResult && (
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium">Журнал очищен</p>
                      <p className="mt-1">Удалено записей: {userActivityCleanupResult.deletedLogs}</p>
                      <p className="mt-1">Текущий размер на диске: {formatBytes(userActivityCleanupResult.maintenance.storage.totalSizeBytes)}</p>
                    </div>
                  )}

                  {userActivityCompactionResult && (
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                      <p className="font-medium">Уплотнение завершено</p>
                      <p className="mt-1">До: {formatBytes(userActivityCompactionResult.before.totalSizeBytes)}</p>
                      <p className="mt-1">После: {formatBytes(userActivityCompactionResult.after.totalSizeBytes)}</p>
                      <p className="mt-1">
                        Освобождено: {formatBytes(Math.max(0, userActivityCompactionResult.before.totalSizeBytes - userActivityCompactionResult.after.totalSizeBytes))}
                      </p>
                    </div>
                  )}
                </div>

                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-5">
                  <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
                    <div>
                      <h4 className="text-base font-semibold text-gray-900">Резервные копии базы</h4>
                      <p className="mt-1 text-sm text-gray-600">
                        Для SQLite создаётся архив `.zip` с консистентной копией файла БД. Для PostgreSQL создаётся `.dump` через `pg_dump`, без дополнительной упаковки.
                      </p>
                    </div>
                    <div className="flex flex-wrap gap-3">
                      <button
                        type="button"
                        onClick={() => void loadDatabaseBackups({ showSuccessToast: true })}
                        disabled={databaseBackupLoading || databaseBackupCreating || databaseBackupSaving}
                        className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        {databaseBackupLoading ? 'Обновляем…' : 'Обновить список'}
                      </button>
                      <button
                        type="button"
                        onClick={createAndDownloadDatabaseBackup}
                        disabled={databaseBackupLoading || databaseBackupCreating}
                        className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        {databaseBackupCreating ? 'Создаём…' : 'Создать и скачать'}
                      </button>
                    </div>
                  </div>

                  <div className="grid gap-4 md:grid-cols-3">
                    <label className="block rounded-xl border border-gray-200 p-4 text-sm text-gray-700">
                      <span className="block font-medium text-gray-900">Автоматические бэкапы</span>
                      <span className="mt-1 block text-gray-600">
                        Сервис раз в минуту проверяет локальное расписание и создаёт пропущенные резервные копии.
                      </span>
                      <input
                        type="checkbox"
                        checked={databaseBackupAutomaticEnabled}
                        onChange={(event) => setDatabaseBackupAutomaticEnabled(event.target.checked)}
                        disabled={databaseBackupLoading || databaseBackupSaving}
                        className="mt-4 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                      />
                    </label>

                    <label className="block rounded-xl border border-gray-200 p-4 text-sm text-gray-700">
                      <span className="block font-medium text-gray-900">Расписание</span>
                      <span className="mt-1 block text-gray-600">
                        Формат: `HH:mm,HH:mm`. Пример: `03:00,15:00`.
                      </span>
                      <input
                        type="text"
                        value={databaseBackupScheduleLocal}
                        onChange={(event) => setDatabaseBackupScheduleLocal(event.target.value)}
                        disabled={databaseBackupLoading || databaseBackupSaving}
                        className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                      />
                    </label>

                    <label className="block rounded-xl border border-gray-200 p-4 text-sm text-gray-700">
                      <span className="block font-medium text-gray-900">Хранить дней</span>
                      <span className="mt-1 block text-gray-600">
                        Старые копии удаляются автоматически при следующей проверке расписания.
                      </span>
                      <input
                        type="number"
                        min={1}
                        max={365}
                        step={1}
                        value={databaseBackupRetentionDays}
                        onChange={(event) => setDatabaseBackupRetentionDays(Math.max(1, Math.floor(Number(event.target.value) || 1)))}
                        disabled={databaseBackupLoading || databaseBackupSaving}
                        className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                      />
                    </label>
                  </div>

                  <div className="flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={saveDatabaseBackupSettings}
                      disabled={databaseBackupLoading || databaseBackupSaving || !hasDatabaseBackupSettingsChanges}
                      className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {databaseBackupSaving ? 'Сохраняем…' : 'Сохранить настройки'}
                    </button>
                    <span className="text-sm text-gray-500">
                      Провайдер: {databaseBackupOverview?.providerDisplayName ?? databaseStatus?.displayName ?? '—'}
                    </span>
                    <span className="text-sm text-gray-500">
                      Формат: {databaseBackupOverview?.backupFormat ?? '—'}
                    </span>
                  </div>

                  {databaseBackupError && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                      {databaseBackupError}
                    </div>
                  )}
                  {databaseBackupMessage && (
                    <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
                      {databaseBackupMessage}
                    </div>
                  )}

                  <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Папка на сервере</div>
                      <div className="mt-2 break-all text-sm text-gray-700">{databaseBackupOverview?.rootDirectory ?? '—'}</div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Часовой пояс</div>
                      <div className="mt-2 text-sm text-gray-700">{databaseBackupOverview?.timeZone ?? '—'}</div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Файлов в списке</div>
                      <div className="mt-2 text-2xl font-semibold text-gray-900">{databaseBackupOverview?.items.length ?? 0}</div>
                    </div>
                    <div className="rounded-xl border border-gray-200 p-4">
                      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                        {databaseBackupOverview?.provider === 'postgresql' ? 'Команда pg_dump' : 'Формат файла'}
                      </div>
                      <div className="mt-2 break-all text-sm text-gray-700">
                        {databaseBackupOverview?.provider === 'postgresql'
                          ? databaseBackupOverview?.toolCommand || 'pg_dump'
                          : databaseBackupOverview?.fileExtension || '.zip'}
                      </div>
                    </div>
                  </div>

                  <div className="rounded-xl border border-gray-200 p-4">
                    <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
                      <div>
                        <h5 className="text-sm font-semibold text-gray-900">Последние резервные копии</h5>
                        <p className="mt-1 text-sm text-gray-600">
                          Показываем до 100 последних файлов текущего провайдера.
                        </p>
                      </div>
                    </div>

                    {databaseBackupOverview?.items.length ? (
                      <div className="mt-4 overflow-x-auto">
                        <table className="min-w-full text-sm">
                          <thead>
                            <tr className="border-b border-gray-200 text-left text-gray-500">
                              <th className="py-2 pr-4">Файл</th>
                              <th className="py-2 pr-4">Создан</th>
                              <th className="py-2 pr-4">Размер</th>
                              <th className="py-2 pr-4">Источник</th>
                              <th className="py-2 text-right">Действие</th>
                            </tr>
                          </thead>
                          <tbody>
                            {databaseBackupOverview.items.map((item) => (
                              <tr key={item.relativePath} className="border-b border-gray-100 align-top">
                                <td className="py-3 pr-4">
                                  <div className="font-medium text-gray-900">{item.fileName}</div>
                                  <div className="text-xs text-gray-500">{item.relativePath}</div>
                                </td>
                                <td className="py-3 pr-4 text-gray-700">{formatDateTime(item.createdAt)}</td>
                                <td className="py-3 pr-4 text-gray-700">{formatBytes(item.sizeBytes)}</td>
                                <td className="py-3 pr-4 text-gray-700">
                                  {item.trigger === 'manual' ? 'Вручную' : item.trigger === 'auto' ? 'Автоматически' : item.trigger}
                                </td>
                                <td className="py-3 text-right">
                                  <button
                                    type="button"
                                    onClick={() => {
                                      void downloadDatabaseBackup(item.relativePath, item.fileName).catch((error) => {
                                        const message = getErrorMessage(error, 'Не удалось скачать резервную копию');
                                        setDatabaseBackupError(message);
                                        toast.error(message, 'База данных');
                                      });
                                    }}
                                    className="rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium hover:bg-gray-50"
                                  >
                                    Скачать
                                  </button>
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    ) : (
                      <div className="mt-4 rounded-lg border border-dashed border-gray-300 px-4 py-6 text-sm text-gray-600">
                        Резервные копии пока не найдены. Можно создать первую копию вручную.
                      </div>
                    )}
                  </div>
                </div>
              </div>
            )}

            {activeSection === 'db-import' && (
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

            {activeSection === 'db-invoices' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div>
                  <h4 className="text-base font-semibold text-gray-900">Формирование канонических счетов из операций</h4>
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


            {activeSection === 'integrations' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">DaData API token</span>
                    <input
                      value={dadataToken}
                      onChange={(event) => setDadataToken(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                      placeholder="Введите токен"
                    />
                  </label>
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">DaData Secret key</span>
                    <input
                      value={dadataSecret}
                      onChange={(event) => setDadataSecret(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2"
                      placeholder="Введите секретный ключ"
                    />
                  </label>
                </div>
                <div className="flex items-center gap-3">
                  <button
                    type="button"
                    onClick={saveDaDataCredentials}
                    disabled={credentialsLoading}
                    className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
                  >
                    {credentialsLoading ? 'Сохраняем…' : 'Сохранить ключи'}
                  </button>
                </div>
                {credentialsMessage && (
                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
                    {credentialsMessage}
                  </div>
                )}
              </div>
            )}

            {activeSection === 'form-requirements' && (
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
                                      <input
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

            {activeSection === 'db-addresses' && (
              <>
                <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                  <label className="flex items-start gap-3 text-sm text-gray-700">
                    <input
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

            {activeSection === 'dictionary-merge' && (
              <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
                <div className="grid gap-4 md:grid-cols-3">
                  <label className="text-sm text-gray-700">
                    <span className="mb-1 block">Справочник</span>
                    <select
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
                    </select>
                  </label>

                  <label className="text-sm text-gray-700 md:col-span-2">
                    <span className="mb-1 block">Группа дублей</span>
                    <select
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
                    </select>
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
          </section>
        </div>
      </div>
    </div>
  );
}
