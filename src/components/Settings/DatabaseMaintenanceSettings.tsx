import { useCallback, useEffect, useMemo, useState } from 'react';
import { Archive, Database, Download, Loader2, RefreshCw, Save, ShieldAlert, Trash2 } from 'lucide-react';
import { apiService } from '../../services/api';
import type {
  DatabaseBackupOverviewDto,
  DatabaseStatusDto,
  InvoiceCounterpartyCleanupResultDto,
  InvoiceOrphanCleanupResultDto,
  UserActivityCleanupResultDto,
  UserActivityCompactionResultDto,
  UserActivityMaintenanceDto,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { ObjectAuditDeletedObjectsSettings } from './ObjectAuditDeletedObjectsSettings';

type MaintenanceTab = 'overview' | 'activity' | 'operations' | 'backups' | 'deleted';

const TABS: Array<{ id: MaintenanceTab; label: string }> = [
  { id: 'overview', label: 'Обзор' },
  { id: 'activity', label: 'Журнал действий' },
  { id: 'operations', label: 'Сервисные операции' },
  { id: 'backups', label: 'Резервные копии' },
  { id: 'deleted', label: 'Удалённые объекты' },
];

function formatBytes(bytes: number) {
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
}

function formatDateTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

function getErrorMessage(error: unknown, fallback: string) {
  return extractDisplayErrorMessage(error, fallback);
}

function StatusBadge({ value, tone }: { value: string; tone: 'success' | 'warning' | 'muted' }) {
  const className =
    tone === 'success'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
      : tone === 'warning'
        ? 'border-amber-200 bg-amber-50 text-amber-700'
        : 'border-slate-200 bg-slate-50 text-slate-600';

  return (
    <span className={`inline-flex rounded-full border px-3 py-1 text-xs font-semibold ${className}`}>
      {value}
    </span>
  );
}

export function DatabaseMaintenanceSettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const [activeTab, setActiveTab] = useState<MaintenanceTab>('overview');
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const [databaseStatus, setDatabaseStatus] = useState<DatabaseStatusDto | null>(null);
  const [userActivityMaintenance, setUserActivityMaintenance] = useState<UserActivityMaintenanceDto | null>(null);
  const [userActivityLoggingEnabled, setUserActivityLoggingEnabled] = useState(true);
  const [userActivityRetentionDays, setUserActivityRetentionDays] = useState(0);
  const [userActivitySettingsSaving, setUserActivitySettingsSaving] = useState(false);
  const [userActivityCleanupLoading, setUserActivityCleanupLoading] = useState(false);
  const [userActivityCompactionLoading, setUserActivityCompactionLoading] = useState(false);
  const [userActivityMaintenanceMessage, setUserActivityMaintenanceMessage] = useState<string | null>(null);
  const [userActivityMaintenanceError, setUserActivityMaintenanceError] = useState<string | null>(null);
  const [userActivityCleanupResult, setUserActivityCleanupResult] = useState<UserActivityCleanupResultDto | null>(null);
  const [userActivityCompactionResult, setUserActivityCompactionResult] = useState<UserActivityCompactionResultDto | null>(null);

  const [invoiceCounterpartyCleanupLoading, setInvoiceCounterpartyCleanupLoading] = useState(false);
  const [invoiceCounterpartyCleanupMessage, setInvoiceCounterpartyCleanupMessage] = useState<string | null>(null);
  const [invoiceCounterpartyCleanupResult, setInvoiceCounterpartyCleanupResult] =
    useState<InvoiceCounterpartyCleanupResultDto | null>(null);
  const [invoiceOrphanCleanupLoading, setInvoiceOrphanCleanupLoading] = useState(false);
  const [invoiceOrphanCleanupMessage, setInvoiceOrphanCleanupMessage] = useState<string | null>(null);
  const [invoiceOrphanCleanupResult, setInvoiceOrphanCleanupResult] =
    useState<InvoiceOrphanCleanupResultDto | null>(null);

  const [databaseBackupOverview, setDatabaseBackupOverview] = useState<DatabaseBackupOverviewDto | null>(null);
  const [databaseBackupAutomaticEnabled, setDatabaseBackupAutomaticEnabled] = useState(true);
  const [databaseBackupScheduleLocal, setDatabaseBackupScheduleLocal] = useState('00:00,06:00,12:00,18:00');
  const [databaseBackupRetentionDays, setDatabaseBackupRetentionDays] = useState(8);
  const [databaseBackupFormat, setDatabaseBackupFormat] = useState('');
  const [databaseBackupSaving, setDatabaseBackupSaving] = useState(false);
  const [databaseBackupCreating, setDatabaseBackupCreating] = useState(false);
  const [databaseBackupDeleting, setDatabaseBackupDeleting] = useState<string | null>(null);
  const [databaseBackupClearing, setDatabaseBackupClearing] = useState(false);
  const [databaseBackupMessage, setDatabaseBackupMessage] = useState<string | null>(null);
  const [databaseBackupError, setDatabaseBackupError] = useState<string | null>(null);
  const canViewDeletedObjects = isAdmin() || permissions.audit.canView;
  const canRestoreDeletedObjects = isAdmin() || permissions.audit.canEdit;
  const availableTabs = useMemo(
    () => TABS.filter((tab) => tab.id !== 'deleted' || canViewDeletedObjects),
    [canViewDeletedObjects],
  );

  const applyDatabaseBackupOverview = (overview: DatabaseBackupOverviewDto | null) => {
    setDatabaseBackupOverview(overview);
    setDatabaseBackupAutomaticEnabled(overview?.automaticEnabled ?? true);
    setDatabaseBackupScheduleLocal(overview?.scheduleLocal ?? '00:00,06:00,12:00,18:00');
    setDatabaseBackupRetentionDays(Math.max(1, Number(overview?.retentionDays ?? 8) || 8));
    setDatabaseBackupFormat(overview?.selectedFormat ?? overview?.availableFormats[0]?.key ?? '');
  };

  const refreshAll = useCallback(async (silent = false) => {
    if (silent) {
      setRefreshing(true);
    } else {
      setLoading(true);
    }

    try {
      const [status, maintenance, backups] = await Promise.all([
        apiService.getDatabaseStatus(),
        apiService.getUserActivityMaintenance(),
        apiService.getDatabaseBackupOverview(),
      ]);

      setDatabaseStatus(status);
      setUserActivityMaintenance(maintenance);
      setUserActivityLoggingEnabled(maintenance.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(maintenance.retentionDays ?? 0) || 0));
      applyDatabaseBackupOverview(backups);
      setUserActivityMaintenanceError(null);
      setDatabaseBackupError(null);
    } catch (error) {
      toast.error(getErrorMessage(error, 'Не удалось загрузить обслуживание базы данных.'), 'База данных');
    } finally {
      if (silent) {
        setRefreshing(false);
      } else {
        setLoading(false);
      }
    }
  }, [toast]);

  useEffect(() => {
    void refreshAll();
  }, [refreshAll]);

  useEffect(() => {
    if (activeTab === 'deleted' && !canViewDeletedObjects) {
      setActiveTab('overview');
    }
  }, [activeTab, canViewDeletedObjects]);

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
        || databaseBackupFormat !== databaseBackupOverview.selectedFormat
      ),
    [
      databaseBackupAutomaticEnabled,
      databaseBackupFormat,
      databaseBackupOverview,
      databaseBackupRetentionDays,
      databaseBackupScheduleLocal,
    ],
  );

  const saveUserActivitySettings = async () => {
    setUserActivitySettingsSaving(true);
    setUserActivityMaintenanceError(null);
    setUserActivityMaintenanceMessage(null);
    try {
      const response = await apiService.updateUserActivitySettings({
        isEnabled: userActivityLoggingEnabled,
        retentionDays: userActivityRetentionDays,
      });
      setUserActivityMaintenance(response);
      setUserActivityLoggingEnabled(response.isEnabled);
      setUserActivityRetentionDays(Math.max(0, Number(response.retentionDays ?? 0) || 0));
      setUserActivityCleanupResult(null);
      setUserActivityCompactionResult(null);
      setUserActivityMaintenanceMessage('Настройки журнала действий сохранены.');
    } catch (error) {
      setUserActivityMaintenanceError(getErrorMessage(error, 'Не удалось сохранить настройки журнала действий.'));
    } finally {
      setUserActivitySettingsSaving(false);
    }
  };

  const runUserActivityCleanup = async () => {
    const approved = await confirm({
      title: 'Очистить журнал действий',
      description: 'Будут удалены все записи журнала действий пользователей.',
      confirmLabel: 'Очистить',
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
      setUserActivityMaintenance(response.maintenance);
      setUserActivityCompactionResult(null);
      setUserActivityMaintenanceMessage('Журнал действий очищен.');
    } catch (error) {
      setUserActivityMaintenanceError(getErrorMessage(error, 'Не удалось очистить журнал действий.'));
    } finally {
      setUserActivityCleanupLoading(false);
    }
  };

  const runUserActivityCompaction = async () => {
    const approved = await confirm({
      title: 'Уплотнить базу данных',
      description: 'Для SQLite будет выполнен checkpoint и VACUUM. Операция может занять время.',
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
      setUserActivityMaintenance(response.maintenance);
      setUserActivityMaintenanceMessage('Файловое обслуживание базы данных завершено.');
    } catch (error) {
      setUserActivityMaintenanceError(getErrorMessage(error, 'Не удалось выполнить файловое обслуживание базы данных.'));
    } finally {
      setUserActivityCompactionLoading(false);
    }
  };

  const runInvoiceCounterpartyCleanup = async () => {
    const approved = await confirm({
      title: 'Очистить legacy-поле счета',
      description: 'Будут удалены сохраненные значения поля "От кого счет" и старое legacy-требование.',
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
      setInvoiceCounterpartyCleanupMessage('Legacy-поле счета очищено.');
    } catch (error) {
      setInvoiceCounterpartyCleanupMessage(getErrorMessage(error, 'Не удалось очистить legacy-поле счета.'));
    } finally {
      setInvoiceCounterpartyCleanupLoading(false);
    }
  };

  const runInvoiceOrphanCleanup = async () => {
    const approved = await confirm({
      title: 'Очистить сиротские счета',
      description: 'Будут удалены записи счетов без единого живого платежа. Такие счета не видны в реестре, но могут мешать нумерации.',
      confirmLabel: 'Очистить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setInvoiceOrphanCleanupLoading(true);
    setInvoiceOrphanCleanupMessage(null);
    try {
      const response = await apiService.cleanupOrphanInvoices();
      setInvoiceOrphanCleanupResult(response);
      setInvoiceOrphanCleanupMessage(
        response.deleted > 0
          ? 'Сиротские счета удалены.'
          : 'Сиротских счетов не найдено.',
      );
    } catch (error) {
      setInvoiceOrphanCleanupMessage(getErrorMessage(error, 'Не удалось очистить сиротские счета.'));
    } finally {
      setInvoiceOrphanCleanupLoading(false);
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
        backupFormat: databaseBackupFormat,
      });
      applyDatabaseBackupOverview(overview);
      setDatabaseBackupMessage('Настройки резервного копирования сохранены.');
    } catch (error) {
      setDatabaseBackupError(getErrorMessage(error, 'Не удалось сохранить настройки резервного копирования.'));
    } finally {
      setDatabaseBackupSaving(false);
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
      await refreshAll(true);
      setDatabaseBackupMessage('Резервная копия создана, скачивание началось.');
    } catch (error) {
      setDatabaseBackupError(getErrorMessage(error, 'Не удалось создать резервную копию.'));
    } finally {
      setDatabaseBackupCreating(false);
    }
  };

  const deleteBackup = async (relativePath: string) => {
    const approved = await confirm({
      title: 'Удалить резервную копию',
      description: 'Файл резервной копии будет удален без возможности восстановления.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDatabaseBackupDeleting(relativePath);
    setDatabaseBackupError(null);
    setDatabaseBackupMessage(null);
    try {
      const result = await apiService.deleteDatabaseBackup(relativePath);
      applyDatabaseBackupOverview(result.overview);
      setDatabaseBackupMessage('Резервная копия удалена.');
    } catch (error) {
      setDatabaseBackupError(getErrorMessage(error, 'Не удалось удалить резервную копию.'));
    } finally {
      setDatabaseBackupDeleting(null);
    }
  };

  const clearAllBackups = async () => {
    const approved = await confirm({
      title: 'Очистить все резервные копии',
      description: 'Будут удалены все резервные копии текущего провайдера.',
      confirmLabel: 'Очистить все',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDatabaseBackupClearing(true);
    setDatabaseBackupError(null);
    setDatabaseBackupMessage(null);
    try {
      const result = await apiService.deleteAllDatabaseBackups();
      applyDatabaseBackupOverview(result.overview);
      setDatabaseBackupMessage(`Удалено резервных копий: ${result.deletedCount}.`);
    } catch (error) {
      setDatabaseBackupError(getErrorMessage(error, 'Не удалось очистить резервные копии.'));
    } finally {
      setDatabaseBackupClearing(false);
    }
  };

  const summary = useMemo(() => {
    const format = databaseBackupOverview?.availableFormats.find((item) => item.key === databaseBackupFormat)
      ?? databaseBackupOverview?.availableFormats.find((item) => item.key === databaseBackupOverview.selectedFormat)
      ?? null;

    return {
      provider: databaseStatus?.displayName ?? '—',
      backupFormat: format?.label ?? databaseBackupOverview?.backupFormat ?? '—',
      backupFormatDescription: format?.description ?? '',
      backupCount: databaseBackupOverview?.items.length ?? 0,
      supportsFileMaintenance: databaseStatus?.supportsSqliteMaintenance === true,
    };
  }, [databaseBackupFormat, databaseBackupOverview, databaseStatus]);

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200 bg-white p-6 text-sm text-gray-600 shadow-sm">
        Загружаем обслуживание базы данных...
      </div>
    );
  }

  return (
    <div className="grid gap-6 xl:grid-cols-[280px,minmax(0,1fr)]">
      <aside className="space-y-4">
        <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">Секция БД</div>
          <div className="mt-3 text-2xl font-semibold text-slate-900">Обслуживание базы данных</div>
          <p className="mt-3 text-sm leading-6 text-slate-600">
            Вкладки разделяют обзор, журнал действий, сервисные операции и резервные копии.
          </p>
          <div className="mt-4">
            <button
              type="button"
              onClick={() => void refreshAll(true)}
              className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50"
              disabled={refreshing}
            >
              {refreshing ? <Loader2 size={16} className="animate-spin" /> : <RefreshCw size={16} />}
              Обновить раздел
            </button>
          </div>
        </div>

        <div className="rounded-3xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="space-y-2">
            {availableTabs.map((tab) => (
              <button
                key={tab.id}
                type="button"
                onClick={() => setActiveTab(tab.id)}
                className={`w-full rounded-2xl border px-4 py-3 text-left text-sm font-semibold transition ${
                  activeTab === tab.id
                    ? 'border-emerald-300 bg-emerald-50 text-emerald-700 shadow-sm'
                    : 'border-transparent bg-white text-slate-700 hover:border-slate-200 hover:bg-slate-50'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </div>
        </div>
      </aside>

      <div className="space-y-6">
        {activeTab === 'overview' && (
          <>
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
              <div className="rounded-3xl border border-gray-200 bg-white p-5 shadow-sm">
                <div className="text-sm text-gray-500">Текущий провайдер</div>
                <div className="mt-2 text-2xl font-semibold text-gray-900">{summary.provider}</div>
              </div>
              <div className="rounded-3xl border border-gray-200 bg-white p-5 shadow-sm">
                <div className="text-sm text-gray-500">Формат backup</div>
                <div className="mt-2 text-2xl font-semibold text-gray-900">{summary.backupFormat}</div>
              </div>
              <div className="rounded-3xl border border-gray-200 bg-white p-5 shadow-sm">
                <div className="text-sm text-gray-500">Файлов в списке</div>
                <div className="mt-2 text-2xl font-semibold text-gray-900">{summary.backupCount}</div>
              </div>
              <div className="rounded-3xl border border-gray-200 bg-white p-5 shadow-sm">
                <div className="text-sm text-gray-500">Файловое обслуживание</div>
                <div className="mt-3">
                  {summary.supportsFileMaintenance ? (
                    <StatusBadge value="SQLite доступно" tone="success" />
                  ) : (
                    <StatusBadge value="Для PostgreSQL недоступно" tone="muted" />
                  )}
                </div>
              </div>
            </div>

            <div className="grid gap-6 lg:grid-cols-2">
              <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                <h4 className="text-lg font-semibold text-gray-900">Как работает обслуживание</h4>
                <div className="mt-4 space-y-3 text-sm leading-6 text-gray-700">
                  <p>
                    SQLite поддерживает checkpoint и VACUUM. Статистика размеров показывает основной файл,
                    WAL и оценку освобождаемого места.
                  </p>
                  <p>
                    PostgreSQL не использует файловые операции SQLite. Для него остаются журнал действий,
                    сервисные операции приложения и логические резервные копии через pg_dump.
                  </p>
                  <p>
                    Формат резервной копии задается отдельно в backup-вкладке. Доступные варианты зависят от
                    текущего провайдера базы данных.
                  </p>
                </div>
              </div>

              <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                <h4 className="text-lg font-semibold text-gray-900">Статус готовности</h4>
                <div className="mt-4 flex flex-wrap gap-2">
                  <StatusBadge
                    value={databaseBackupOverview?.automaticEnabled ? 'Автобэкапы включены' : 'Автобэкапы выключены'}
                    tone={databaseBackupOverview?.automaticEnabled ? 'success' : 'warning'}
                  />
                  <StatusBadge
                    value={summary.supportsFileMaintenance ? 'Обслуживание SQLite доступно' : 'Только логическое обслуживание'}
                    tone={summary.supportsFileMaintenance ? 'success' : 'muted'}
                  />
                  <StatusBadge
                    value={databaseBackupOverview?.toolCommand ? 'Команда backup настроена' : 'Используется путь по умолчанию'}
                    tone="muted"
                  />
                </div>
                <div className="mt-4 space-y-3 text-sm leading-6 text-gray-700">
                  <p>Провайдер: {databaseBackupOverview?.providerDisplayName ?? databaseStatus?.displayName ?? '—'}.</p>
                  <p>Формат: {summary.backupFormat}{summary.backupFormatDescription ? ` — ${summary.backupFormatDescription}` : ''}.</p>
                  <p>Каталог хранения: {databaseBackupOverview?.rootDirectory ?? '—'}.</p>
                  {databaseBackupOverview?.provider === 'postgresql' ? (
                    <p>Команда резервного копирования: {databaseBackupOverview.toolCommand || 'pg_dump'}.</p>
                  ) : (
                    <p>Для SQLite можно хранить zip-архив или прямую копию файла базы.</p>
                  )}
                </div>
              </div>
            </div>
          </>
        )}

        {activeTab === 'activity' && (
          <div className="space-y-6">
            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Журнал действий пользователей</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Этот блок отдельно управляет логированием и сроком хранения записей активности.
                  </p>
                </div>
                <StatusBadge
                  value={userActivityMaintenance?.isEnabled === false ? 'Логирование выключено' : 'Логирование включено'}
                  tone={userActivityMaintenance?.isEnabled === false ? 'warning' : 'success'}
                />
              </div>

              <div className="mt-5 space-y-4">
                <label className="flex items-start gap-3 rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                  <input
                    type="checkbox"
                    checked={userActivityLoggingEnabled}
                    onChange={(event) => setUserActivityLoggingEnabled(event.target.checked)}
                    className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    disabled={userActivitySettingsSaving}
                  />
                  <span>
                    <span className="block font-medium text-gray-900">Включить журналирование</span>
                    <span className="mt-1 block text-gray-600">
                      Когда настройка выключена, новые записи в UserActivityLogs не создаются.
                    </span>
                  </span>
                </label>

                <label className="block rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                  <span className="block font-medium text-gray-900">Срок хранения в днях</span>
                  <span className="mt-1 block text-gray-600">Значение 0 отключает автоочистку журнала.</span>
                  <input
                    type="number"
                    min={0}
                    step={1}
                    value={userActivityRetentionDays}
                    onChange={(event) => setUserActivityRetentionDays(Math.max(0, Math.floor(Number(event.target.value) || 0)))}
                    className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    disabled={userActivitySettingsSaving}
                  />
                </label>

                <div className="flex flex-wrap items-center gap-3">
                  <button
                    type="button"
                    onClick={saveUserActivitySettings}
                    disabled={!hasUserActivitySettingsChanges || userActivitySettingsSaving}
                    className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
                  >
                    <Save size={16} />
                    {userActivitySettingsSaving ? 'Сохраняем...' : 'Сохранить'}
                  </button>
                  <span className="text-sm text-gray-500">Обновлено: {formatDateTime(userActivityMaintenance?.updatedAt)}</span>
                </div>

                {(userActivityMaintenanceError || userActivityMaintenanceMessage) && (
                  <div
                    className={`rounded-2xl border px-4 py-3 text-sm ${
                      userActivityMaintenanceError
                        ? 'border-red-200 bg-red-50 text-red-700'
                        : 'border-emerald-200 bg-emerald-50 text-emerald-700'
                    }`}
                  >
                    {userActivityMaintenanceError ?? userActivityMaintenanceMessage}
                  </div>
                )}
              </div>
            </div>

            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Фактическое состояние</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Для SQLite видны реальные размеры файлов. Для PostgreSQL файловые метрики SQLite будут нулевыми.
                  </p>
                </div>
                {databaseStatus?.supportsSqliteMaintenance ? (
                  <StatusBadge value="Размеры SQLite доступны" tone="success" />
                ) : (
                  <StatusBadge value="Файловая статистика SQLite недоступна" tone="muted" />
                )}
              </div>

              <div className="mt-5 grid gap-4 md:grid-cols-2 xl:grid-cols-5">
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Записей</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{userActivityMaintenance?.logEntries ?? 0}</div>
                </div>
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Файл БД</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{formatBytes(userActivityMaintenance?.storage.databaseFileSizeBytes ?? 0)}</div>
                </div>
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">WAL</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{formatBytes(userActivityMaintenance?.storage.walFileSizeBytes ?? 0)}</div>
                </div>
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Всего</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{formatBytes(userActivityMaintenance?.storage.totalSizeBytes ?? 0)}</div>
                </div>
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Можно освободить</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{formatBytes(userActivityMaintenance?.storage.estimatedReclaimableBytes ?? 0)}</div>
                </div>
              </div>

              <div className="mt-4 grid gap-4 md:grid-cols-2">
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Самая старая запись</div>
                  <div className="mt-2 text-sm text-gray-700">{formatDateTime(userActivityMaintenance?.oldestLogAt)}</div>
                </div>
                <div className="rounded-2xl border border-gray-200 p-4">
                  <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Последняя запись</div>
                  <div className="mt-2 text-sm text-gray-700">{formatDateTime(userActivityMaintenance?.newestLogAt)}</div>
                </div>
              </div>
            </div>
          </div>
        )}

        {activeTab === 'operations' && (
          <div className="space-y-6">
            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Очистка сиротских счетов</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Удаляет записи счетов без единого живого платежа. Такие счета не видны в реестре, но могут блокировать номер счета.
                  </p>
                </div>
                <StatusBadge value="Операция доступна" tone="success" />
              </div>

              <div className="mt-4 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={runInvoiceOrphanCleanup}
                  disabled={invoiceOrphanCleanupLoading}
                  className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-60"
                >
                  <Trash2 size={16} />
                  {invoiceOrphanCleanupLoading ? 'Очищаем...' : 'Очистить сиротские счета'}
                </button>
                {invoiceOrphanCleanupMessage ? <span className="text-sm text-gray-600">{invoiceOrphanCleanupMessage}</span> : null}
              </div>

              {invoiceOrphanCleanupResult ? (
                <div className="mt-4 rounded-2xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                  Удалено сиротских счетов: {invoiceOrphanCleanupResult.deleted}.
                  {invoiceOrphanCleanupResult.items.length > 0 ? ` Номера: ${invoiceOrphanCleanupResult.items.map((item) => item.number).join(', ')}.` : ''}
                </div>
              ) : null}
            </div>

            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Очистка legacy-поля счета</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Блок отдельно очищает устаревшее поле "От кого счет" и удаляет старое обязательное требование.
                  </p>
                </div>
                <StatusBadge value="Операция доступна" tone="success" />
              </div>

              <div className="mt-4 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={runInvoiceCounterpartyCleanup}
                  disabled={invoiceCounterpartyCleanupLoading}
                  className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-60"
                >
                  <ShieldAlert size={16} />
                  {invoiceCounterpartyCleanupLoading ? 'Очищаем...' : 'Очистить поле'}
                </button>
                {invoiceCounterpartyCleanupMessage ? <span className="text-sm text-gray-600">{invoiceCounterpartyCleanupMessage}</span> : null}
              </div>

              {invoiceCounterpartyCleanupResult ? (
                <div className="mt-4 rounded-2xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                  Очищено счетов: {invoiceCounterpartyCleanupResult.cleared}. Удалено legacy-требований: {invoiceCounterpartyCleanupResult.removedRequirements}.
                </div>
              ) : null}
            </div>

            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Очистка и уплотнение журнала</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Очистка удаляет записи журнала. Уплотнение реально освобождает место только для SQLite.
                  </p>
                </div>
                {databaseStatus?.supportsSqliteMaintenance ? (
                  <StatusBadge value="SQLite-операции доступны" tone="success" />
                ) : (
                  <StatusBadge value="Для PostgreSQL только очистка журнала" tone="warning" />
                )}
              </div>

              {databaseStatus?.supportsSqliteMaintenance === false ? (
                <div className="mt-4 rounded-2xl border border-blue-200 bg-blue-50 p-4 text-sm text-blue-900">
                  Текущий провайдер — {databaseStatus?.displayName}. Для него отключены checkpoint и VACUUM, но очистка журнала действий продолжает работать.
                </div>
              ) : null}

              <div className="mt-4 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={runUserActivityCleanup}
                  disabled={userActivityCleanupLoading || userActivityCompactionLoading}
                  className="inline-flex items-center gap-2 rounded-2xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm font-semibold text-red-700 hover:bg-red-100 disabled:opacity-60"
                >
                  <Trash2 size={16} />
                  {userActivityCleanupLoading ? 'Удаляем...' : 'Очистить журнал'}
                </button>
                <button
                  type="button"
                  onClick={runUserActivityCompaction}
                  disabled={userActivityCleanupLoading || userActivityCompactionLoading || databaseStatus?.supportsSqliteMaintenance === false}
                  className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-60"
                >
                  <Database size={16} />
                  {userActivityCompactionLoading ? 'Уплотняем...' : 'Уплотнить БД'}
                </button>
              </div>

              {userActivityCleanupResult ? (
                <div className="mt-4 rounded-2xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                  Удалено записей: {userActivityCleanupResult.deletedLogs}. Текущий размер на диске: {formatBytes(userActivityCleanupResult.maintenance.storage.totalSizeBytes)}.
                </div>
              ) : null}

              {userActivityCompactionResult ? (
                <div className="mt-4 rounded-2xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                  До: {formatBytes(userActivityCompactionResult.before.totalSizeBytes)}. После: {formatBytes(userActivityCompactionResult.after.totalSizeBytes)}. Освобождено: {formatBytes(Math.max(0, userActivityCompactionResult.before.totalSizeBytes - userActivityCompactionResult.after.totalSizeBytes))}.
                </div>
              ) : null}
            </div>
          </div>
        )}

        {activeTab === 'backups' && (
          <div className="space-y-6">
            <div className="grid gap-6 xl:grid-cols-[minmax(0,1.4fr),minmax(320px,1fr)]">
              <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <h4 className="text-lg font-semibold text-gray-900">Параметры резервного копирования</h4>
                    <p className="mt-1 text-sm text-gray-600">
                      Этот блок отдельно хранит формат backup, расписание, срок хранения и режим автоматического запуска.
                    </p>
                  </div>
                  <StatusBadge value={databaseBackupOverview?.automaticEnabled ? 'Автобэкапы включены' : 'Автобэкапы выключены'} tone={databaseBackupOverview?.automaticEnabled ? 'success' : 'warning'} />
                </div>

                <div className="mt-5 grid gap-4 md:grid-cols-2">
                  <label className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                    <span className="block font-medium text-gray-900">Формат backup</span>
                    <span className="mt-1 block text-gray-600">
                      Для SQLite доступны архив или прямая копия файла. Для PostgreSQL — custom backup или plain SQL.
                    </span>
                    <select
                      value={databaseBackupFormat}
                      onChange={(event) => setDatabaseBackupFormat(event.target.value)}
                      className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    >
                      {(databaseBackupOverview?.availableFormats ?? []).map((item) => (
                        <option key={item.key} value={item.key}>{item.label}</option>
                      ))}
                    </select>
                  </label>

                  <label className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                    <span className="block font-medium text-gray-900">Расписание</span>
                    <span className="mt-1 block text-gray-600">Формат: `HH:mm,HH:mm`.</span>
                    <input
                      value={databaseBackupScheduleLocal}
                      onChange={(event) => setDatabaseBackupScheduleLocal(event.target.value)}
                      className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    />
                  </label>

                  <label className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                    <span className="block font-medium text-gray-900">Хранить дней</span>
                    <span className="mt-1 block text-gray-600">Старые копии удаляются автоматически по сроку хранения.</span>
                    <input
                      type="number"
                      min={1}
                      max={365}
                      step={1}
                      value={databaseBackupRetentionDays}
                      onChange={(event) => setDatabaseBackupRetentionDays(Math.max(1, Math.floor(Number(event.target.value) || 1)))}
                      className="mt-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                    />
                  </label>

                  <label className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
                    <span className="block font-medium text-gray-900">Автобэкапы</span>
                    <span className="mt-1 block text-gray-600">Фоновый сервис проверяет расписание и создает пропущенные копии.</span>
                    <div className="mt-4">
                      <input
                        type="checkbox"
                        checked={databaseBackupAutomaticEnabled}
                        onChange={(event) => setDatabaseBackupAutomaticEnabled(event.target.checked)}
                        className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                      />
                    </div>
                  </label>
                </div>

                <div className="mt-4 flex flex-wrap items-center gap-3">
                  <button
                    type="button"
                    onClick={saveDatabaseBackupSettings}
                    disabled={databaseBackupSaving || !hasDatabaseBackupSettingsChanges}
                    className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
                  >
                    <Save size={16} />
                    {databaseBackupSaving ? 'Сохраняем...' : 'Сохранить'}
                  </button>
                  <span className="text-sm text-gray-500">Текущий формат: {databaseBackupOverview?.backupFormat ?? '—'}</span>
                </div>

                {(databaseBackupError || databaseBackupMessage) ? (
                  <div className={`mt-4 rounded-2xl border px-4 py-3 text-sm ${databaseBackupError ? 'border-red-200 bg-red-50 text-red-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>
                    {databaseBackupError ?? databaseBackupMessage}
                  </div>
                ) : null}
              </div>

              <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
                <h4 className="text-lg font-semibold text-gray-900">Статус backup-механизма</h4>
                <div className="mt-4 flex flex-wrap gap-2">
                  <StatusBadge value={summary.provider} tone="muted" />
                  <StatusBadge value={summary.backupFormat} tone="success" />
                  <StatusBadge value={databaseBackupOverview?.items.length ? `Файлов: ${databaseBackupOverview.items.length}` : 'Список пока пуст'} tone={databaseBackupOverview?.items.length ? 'success' : 'warning'} />
                </div>
                <div className="mt-4 space-y-3 text-sm leading-6 text-gray-700">
                  <p>Каталог хранения: {databaseBackupOverview?.rootDirectory ?? '—'}.</p>
                  <p>Часовой пояс расписания: {databaseBackupOverview?.timeZone ?? '—'}.</p>
                  {databaseBackupOverview?.provider === 'postgresql' ? (
                    <p>Для PostgreSQL используется: {databaseBackupOverview.toolCommand || 'pg_dump'}.</p>
                  ) : (
                    <p>Для SQLite резервная копия строится из консистентной копии файла базы данных.</p>
                  )}
                </div>

                <div className="mt-6 flex flex-wrap gap-3">
                  <button
                    type="button"
                    onClick={createAndDownloadDatabaseBackup}
                    disabled={databaseBackupCreating}
                    className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-60"
                  >
                    <Archive size={16} />
                    {databaseBackupCreating ? 'Создаем...' : 'Создать и скачать'}
                  </button>
                  <button
                    type="button"
                    onClick={clearAllBackups}
                    disabled={databaseBackupClearing || !(databaseBackupOverview?.items.length)}
                    className="inline-flex items-center gap-2 rounded-2xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm font-semibold text-red-700 hover:bg-red-100 disabled:opacity-60"
                  >
                    <Trash2 size={16} />
                    {databaseBackupClearing ? 'Очищаем...' : 'Очистить все'}
                  </button>
                </div>
              </div>
            </div>

            <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-gray-900">Файлы резервных копий</h4>
                  <p className="mt-1 text-sm text-gray-600">
                    Каждый файл можно скачать отдельно. Администратор также может удалить отдельную копию или очистить весь список.
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => void refreshAll(true)}
                  className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50"
                  disabled={refreshing}
                >
                  {refreshing ? <Loader2 size={16} className="animate-spin" /> : <RefreshCw size={16} />}
                  Обновить список
                </button>
              </div>

              {databaseBackupOverview?.items.length ? (
                <div className="mt-5 overflow-hidden rounded-2xl border border-gray-200">
                  <div className="overflow-x-auto">
                    <table className="min-w-full text-sm">
                      <thead className="bg-slate-50 text-left text-slate-500">
                        <tr>
                          <th className="px-4 py-3 font-semibold">Файл</th>
                          <th className="px-4 py-3 font-semibold">Создан</th>
                          <th className="px-4 py-3 font-semibold">Размер</th>
                          <th className="px-4 py-3 font-semibold">Тип</th>
                          <th className="px-4 py-3 font-semibold text-right">Действия</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 bg-white">
                        {databaseBackupOverview.items.map((item) => (
                          <tr key={item.relativePath}>
                            <td className="px-4 py-3 text-gray-900">
                              <div className="font-medium">{item.fileName}</div>
                              <div className="mt-1 text-xs text-gray-500">{item.relativePath}</div>
                            </td>
                            <td className="px-4 py-3 text-gray-700">{formatDateTime(item.createdAt)}</td>
                            <td className="px-4 py-3 text-gray-700">{formatBytes(item.sizeBytes)}</td>
                            <td className="px-4 py-3 text-gray-700">{item.trigger === 'manual' ? 'Ручная' : item.trigger === 'auto' ? 'Автоматическая' : item.trigger}</td>
                            <td className="px-4 py-3">
                              <div className="flex justify-end gap-2">
                                <button
                                  type="button"
                                  onClick={() => void downloadDatabaseBackup(item.relativePath, item.fileName)}
                                  className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50"
                                >
                                  <Download size={14} />
                                  Скачать
                                </button>
                                <button
                                  type="button"
                                  onClick={() => void deleteBackup(item.relativePath)}
                                  disabled={databaseBackupDeleting === item.relativePath}
                                  className="inline-flex items-center gap-2 rounded-xl border border-red-200 bg-red-50 px-3 py-2 text-xs font-semibold text-red-700 hover:bg-red-100 disabled:opacity-60"
                                >
                                  {databaseBackupDeleting === item.relativePath ? <Loader2 size={14} className="animate-spin" /> : <Trash2 size={14} />}
                                  Удалить
                                </button>
                              </div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ) : (
                <div className="mt-5 rounded-2xl border border-dashed border-gray-300 bg-slate-50 px-5 py-8 text-sm text-gray-600">
                  Резервные копии пока не созданы. Сохраните настройки, затем создайте первую копию вручную.
                </div>
              )}
            </div>
          </div>
        )}

        {activeTab === 'deleted' && canViewDeletedObjects && (
          <ObjectAuditDeletedObjectsSettings canRestore={canRestoreDeletedObjects} />
        )}
      </div>
    </div>
  );
}
