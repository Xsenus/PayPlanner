import { Activity, AlertTriangle, CalendarDays, CheckCircle2, RefreshCw, Save, ShieldCheck } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  apiService,
  type CalendarSyncProviderDto,
  type CalendarSyncReadinessDto,
  type CalendarSyncLogPageDto,
  type CalendarSyncSettingsDto,
} from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

export function CalendarSyncProviderSettings() {
  const [providers, setProviders] = useState<CalendarSyncProviderDto[]>([]);
  const [readiness, setReadiness] = useState<CalendarSyncReadinessDto | null>(null);
  const [logs, setLogs] = useState<CalendarSyncLogPageDto | null>(null);
  const [settings, setSettings] = useState<CalendarSyncSettingsDto | null>(null);
  const [draft, setDraft] = useState<CalendarSyncSettingsDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);

  const availableCount = useMemo(
    () => providers.filter((provider) => provider.isAvailable).length,
    [providers],
  );

  const requiredReadyCount = useMemo(
    () => readiness?.items.filter((item) => item.isRequired && item.isReady).length ?? 0,
    [readiness],
  );

  const requiredTotalCount = useMemo(
    () => readiness?.items.filter((item) => item.isRequired).length ?? 0,
    [readiness],
  );

  const applyLoadedSettings = useCallback((next: CalendarSyncSettingsDto) => {
    setSettings(next);
    setDraft({ ...next });
  }, []);

  const loadCalendarSyncState = useCallback(async () => {
    setLoading(true);
    setError(null);
    setSaveMessage(null);
    try {
      const [loadedProviders, loadedReadiness, loadedSettings] = await Promise.all([
        apiService.getCalendarSyncProviders(),
        apiService.getCalendarSyncReadiness(),
        apiService.getCalendarSyncSettings(),
      ]);
      const loadedLogs = await apiService.getCalendarSyncLogs({ pageSize: 50 });
      setProviders(loadedProviders);
      setReadiness(loadedReadiness);
      setLogs(loadedLogs);
      applyLoadedSettings(loadedSettings);
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить состояние внешних календарей.'));
    } finally {
      setLoading(false);
    }
  }, [applyLoadedSettings]);

  useEffect(() => {
    void loadCalendarSyncState();
  }, [loadCalendarSyncState]);

  const updateDraft = (patch: Partial<CalendarSyncSettingsDto>) => {
    setDraft((current) => (current ? { ...current, ...patch } : current));
    setSaveMessage(null);
  };

  const saveSettings = async () => {
    if (!draft) return;

    setSaving(true);
    setError(null);
    setSaveMessage(null);
    try {
      const updated = await apiService.updateCalendarSyncSettings({
        syncMode: draft.syncMode,
        conflictResolutionMode: draft.conflictResolutionMode,
        userControlMode: draft.userControlMode,
        defaultSyncFromDate: draft.defaultSyncFromDate || null,
        inactiveUserAction: draft.inactiveUserAction,
        requireManualConfirmation: draft.requireManualConfirmation,
        dryRunConfirmed: draft.dryRunConfirmed,
        notes: draft.notes,
      });
      applyLoadedSettings(updated);
      setReadiness(await apiService.getCalendarSyncReadiness());
      setSaveMessage('Настройки внешних календарей сохранены.');
    } catch (saveError) {
      setError(extractDisplayErrorMessage(saveError, 'Не удалось сохранить настройки внешних календарей.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full bg-sky-50 px-3 py-1 text-xs font-semibold text-sky-700">
            <CalendarDays className="h-3.5 w-3.5" />
            Внешние календари
          </div>
          <h4 className="mt-3 text-xl font-semibold text-gray-900">Провайдеры календарной синхронизации</h4>
          <p className="mt-1 text-sm text-gray-600">
            Здесь видно, какой контур календарей уже доступен, а какие подключения требуют отдельной настройки и приемки.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void loadCalendarSyncState()}
          disabled={loading}
          className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 px-4 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
          <span>{loading ? 'Обновляем' : 'Обновить'}</span>
        </button>
      </div>

      {error ? (
        <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
      ) : null}

      <div className="mt-5 grid gap-3 lg:grid-cols-3">
        {providers.map((provider) => (
          <div key={provider.key} className="rounded-2xl border border-gray-200 bg-gray-50 px-4 py-4">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-semibold text-gray-900">{provider.label}</span>
              <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${getProviderStatusClass(provider.status)}`}>
                {getProviderStatusLabel(provider.status)}
              </span>
            </div>
            <p className="mt-2 min-h-[40px] text-xs leading-5 text-gray-600">{provider.message}</p>
            <div className="mt-3 flex flex-wrap gap-1.5">
              {provider.supportedTargets.map((target) => (
                <span key={target} className="rounded-full bg-white px-2 py-0.5 text-xs font-medium text-gray-600">{target}</span>
              ))}
            </div>
            <div className="mt-4 grid grid-cols-2 gap-2 text-xs text-gray-600">
              <Capability enabled={provider.supportsManualExport} label="Ручной экспорт" />
              <Capability enabled={provider.supportsBackgroundSync} label="Фоновая синхронизация" />
              <Capability enabled={provider.supportsTwoWaySync} label="Двусторонний режим" />
              <Capability enabled={provider.requiresOAuth} label="OAuth" />
            </div>
          </div>
        ))}
      </div>

      <div className="mt-4 rounded-2xl border border-sky-100 bg-sky-50 px-4 py-3 text-sm text-sky-900">
        Доступно сейчас: {availableCount} из {providers.length || 0}. Google и Яндекс не запускаются автоматически, пока внешний календарный feature flag и OAuth-настройки не будут приняты отдельно.
      </div>

      {draft ? (
        <div className="mt-5 rounded-2xl border border-gray-200 bg-white p-4">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div>
              <h5 className="text-sm font-semibold text-gray-900">Модель синхронизации и конфликты</h5>
              <p className="mt-1 text-xs leading-5 text-gray-600">
                Эти настройки фиксируют правила перед подключением Google/Яндекс и обновляют чек-лист готовности.
              </p>
            </div>
            <button
              type="button"
              onClick={() => void saveSettings()}
              disabled={saving}
              className="inline-flex h-9 items-center justify-center gap-2 rounded-lg bg-sky-600 px-3 text-xs font-semibold text-white hover:bg-sky-700 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <Save className="h-4 w-4" />
              <span>{saving ? 'Сохраняем' : 'Сохранить'}</span>
            </button>
          </div>

          <div className="mt-4 grid gap-3 lg:grid-cols-2">
            <label className="block">
              <span className="text-xs font-semibold text-gray-700">Главный источник событий</span>
              <select
                value={draft.syncMode}
                onChange={(event) => updateDraft({ syncMode: event.target.value })}
                className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                {syncModeOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>

            <label className="block">
              <span className="text-xs font-semibold text-gray-700">Правило спорных изменений</span>
              <select
                value={draft.conflictResolutionMode}
                onChange={(event) => updateDraft({ conflictResolutionMode: event.target.value })}
                className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                {conflictResolutionOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
          </div>

          <div className="mt-4 rounded-2xl border border-sky-100 bg-sky-50/70 p-4">
            <div className="flex items-start gap-2">
              <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-sky-700" />
              <div>
                <h6 className="text-sm font-semibold text-gray-900">Административное управление</h6>
                <p className="mt-1 text-xs leading-5 text-gray-600">
                  Эти правила задают, кто управляет синхронизацией, с какой даты создавать события и что делать с календарями при блокировке сотрудника.
                </p>
              </div>
            </div>

            <div className="mt-4 grid gap-3 lg:grid-cols-3">
              <label className="block">
                <span className="text-xs font-semibold text-gray-700">Кто управляет</span>
                <select
                  value={draft.userControlMode}
                  onChange={(event) => updateDraft({ userControlMode: event.target.value })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                >
                  {userControlModeOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </select>
              </label>

              <label className="block">
                <span className="text-xs font-semibold text-gray-700">Дата начала по умолчанию</span>
                <input
                  type="date"
                  value={formatDateInputValue(draft.defaultSyncFromDate)}
                  onChange={(event) => updateDraft({ defaultSyncFromDate: event.target.value || null })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                />
              </label>

              <label className="block">
                <span className="text-xs font-semibold text-gray-700">При потере доступа</span>
                <select
                  value={draft.inactiveUserAction}
                  onChange={(event) => updateDraft({ inactiveUserAction: event.target.value })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                >
                  {inactiveUserActionOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </select>
              </label>
            </div>
          </div>

          <div className="mt-4 grid gap-2 md:grid-cols-2">
            <label className="flex items-start gap-2 rounded-xl border border-gray-200 bg-gray-50 px-3 py-3 text-xs text-gray-700">
              <input
                type="checkbox"
                checked={draft.requireManualConfirmation}
                onChange={(event) => updateDraft({ requireManualConfirmation: event.target.checked })}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-sky-600 focus:ring-sky-500"
              />
              <span>Требовать ручное подтверждение спорных изменений</span>
            </label>
            <label className="flex items-start gap-2 rounded-xl border border-gray-200 bg-gray-50 px-3 py-3 text-xs text-gray-700">
              <input
                type="checkbox"
                checked={draft.dryRunConfirmed}
                onChange={(event) => updateDraft({ dryRunConfirmed: event.target.checked })}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-sky-600 focus:ring-sky-500"
              />
              <span>Dry-run на копии базы выполнен</span>
            </label>
          </div>

          <label className="mt-4 block">
            <span className="text-xs font-semibold text-gray-700">Комментарий к решению</span>
            <textarea
              value={draft.notes}
              onChange={(event) => updateDraft({ notes: event.target.value })}
              maxLength={500}
              rows={3}
              className="mt-1 w-full resize-none rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              placeholder="Например: модель согласована с руководителем, dry-run проверен на копии базы."
            />
          </label>

          {saveMessage ? (
            <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-2 text-xs font-medium text-emerald-700">{saveMessage}</div>
          ) : null}
          {settings?.updatedAt ? (
            <p className="mt-2 text-[11px] text-gray-500">Последнее сохранение: {new Date(settings.updatedAt).toLocaleString('ru-RU')}</p>
          ) : null}
        </div>
      ) : null}

      {readiness ? (
        <div className="mt-5 rounded-2xl border border-amber-100 bg-amber-50/60 p-4">
          <div className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h5 className="text-sm font-semibold text-gray-900">Готовность к подключению Google/Яндекс</h5>
              <p className="mt-1 text-xs text-gray-600">
                Выполнено обязательных шагов: {requiredReadyCount} из {requiredTotalCount}. Пока все обязательные пункты не закрыты, внешняя синхронизация остается подготовительным контуром.
              </p>
            </div>
            <span className={`inline-flex w-fit items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-semibold ${readiness.readyToConnect ? 'border-emerald-200 bg-emerald-50 text-emerald-700' : 'border-amber-200 bg-white text-amber-700'}`}>
              {readiness.readyToConnect ? <CheckCircle2 className="h-3.5 w-3.5" /> : <AlertTriangle className="h-3.5 w-3.5" />}
              {readiness.readyToConnect ? 'Можно подключать' : 'Нужна подготовка'}
            </span>
          </div>
          <div className="mt-4 grid gap-2 md:grid-cols-2">
            {readiness.items.map((item) => (
              <div key={item.key} className="rounded-xl border border-white/80 bg-white px-3 py-3 shadow-sm">
                <div className="flex items-start gap-2">
                  {item.isReady ? (
                    <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" />
                  ) : (
                    <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${getReadinessIconClass(item.severity)}`} />
                  )}
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-xs font-semibold text-gray-900">{item.label}</span>
                      <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${getReadinessBadgeClass(item.severity, item.isReady)}`}>
                        {getReadinessStatusLabel(item.status, item.isReady)}
                      </span>
                    </div>
                    <p className="mt-1 text-xs leading-5 text-gray-600">{item.message}</p>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      ) : null}

      <div className="mt-5 rounded-2xl border border-gray-200 bg-white p-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <div className="inline-flex items-center gap-2 text-sm font-semibold text-gray-900">
              <Activity className="h-4 w-4 text-sky-700" />
              Полный журнал синхронизации
            </div>
            <p className="mt-1 text-xs leading-5 text-gray-600">
              Последние события по Google Calendar и Яндекс Календарю: создание, обновление, удаление, отключение и ошибки синхронизации.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void loadCalendarSyncState()}
            disabled={loading}
            className="inline-flex h-9 items-center justify-center gap-2 rounded-lg border border-gray-300 px-3 text-xs font-semibold text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            <span>Обновить журнал</span>
          </button>
        </div>

        <div className="mt-4 overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200 text-left text-xs">
            <thead className="bg-gray-50 text-[11px] font-semibold uppercase text-gray-500">
              <tr>
                <th className="px-3 py-2">Время</th>
                <th className="px-3 py-2">Провайдер</th>
                <th className="px-3 py-2">Сотрудник</th>
                <th className="px-3 py-2">Действие</th>
                <th className="px-3 py-2">Статус</th>
                <th className="px-3 py-2">Сообщение</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {logs?.items.length ? logs.items.map((item) => (
                <tr key={`${item.provider}-${item.id}`} className="align-top">
                  <td className="whitespace-nowrap px-3 py-2 text-gray-600">{new Date(item.createdAt).toLocaleString('ru-RU')}</td>
                  <td className="whitespace-nowrap px-3 py-2 font-medium text-gray-800">{item.providerLabel}</td>
                  <td className="px-3 py-2 text-gray-700">
                    <div className="font-medium">{item.userName || 'Система'}</div>
                    {item.userEmail ? <div className="text-[11px] text-gray-500">{item.userEmail}</div> : null}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-gray-700">{getCalendarLogOperationLabel(item.operation)}</td>
                  <td className="whitespace-nowrap px-3 py-2">
                    <span className={`rounded-full px-2 py-0.5 font-semibold ${getCalendarLogStatusClass(item.status)}`}>
                      {getCalendarLogStatusLabel(item.status)}
                    </span>
                  </td>
                  <td className="min-w-[260px] px-3 py-2 text-gray-600">{item.message || 'Без сообщения'}</td>
                </tr>
              )) : (
                <tr>
                  <td colSpan={6} className="px-3 py-6 text-center text-sm text-gray-500">Событий синхронизации пока нет.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        {logs ? (
          <p className="mt-3 text-[11px] text-gray-500">Показано {logs.items.length} из {logs.total} событий.</p>
        ) : null}
      </div>
    </div>
  );
}

const syncModeOptions = [
  { value: 'notConfigured', label: 'Не выбрано' },
  { value: 'crmMaster', label: 'CRM главная' },
  { value: 'externalMaster', label: 'Внешний календарь главный' },
  { value: 'hybrid', label: 'Гибридная модель' },
];

const conflictResolutionOptions = [
  { value: 'manualConfirmation', label: 'Ручное подтверждение' },
  { value: 'crmWins', label: 'Оставлять версию CRM' },
  { value: 'externalWins', label: 'Принимать внешний календарь' },
  { value: 'latestWins', label: 'Брать последнее изменение' },
];

const userControlModeOptions = [
  { value: 'userManaged', label: 'Пользователь управляет своим календарем' },
  { value: 'adminManaged', label: 'Только администратор управляет синхронизацией' },
];

const inactiveUserActionOptions = [
  { value: 'cleanupExternalData', label: 'Удалить созданные события и календари' },
  { value: 'disableSyncOnly', label: 'Отключить синхронизацию без удаления' },
  { value: 'keepConnection', label: 'Ничего не менять' },
];

function Capability({ enabled, label }: { enabled: boolean; label: string }) {
  return (
    <span className={`rounded-xl px-2 py-1 font-medium ${enabled ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
      {label}
    </span>
  );
}

function getProviderStatusLabel(status: string) {
  if (status === 'active') return 'Активен';
  if (status === 'requires-setup') return 'Нужна настройка';
  if (status === 'disabled') return 'Выключен';
  return status;
}

function getProviderStatusClass(status: string) {
  if (status === 'active') return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  if (status === 'requires-setup') return 'border-amber-200 bg-amber-50 text-amber-700';
  if (status === 'disabled') return 'border-slate-200 bg-white text-slate-600';
  return 'border-slate-200 bg-white text-slate-700';
}

function getReadinessStatusLabel(status: string, isReady: boolean) {
  if (isReady) return 'Готово';
  if (status === 'disabled') return 'Выключено';
  if (status === 'pending') return 'Ожидает';
  return status;
}

function getReadinessIconClass(severity: string) {
  if (severity === 'warning') return 'text-amber-600';
  if (severity === 'error') return 'text-rose-600';
  return 'text-slate-500';
}

function getReadinessBadgeClass(severity: string, isReady: boolean) {
  if (isReady) return 'bg-emerald-50 text-emerald-700';
  if (severity === 'warning') return 'bg-amber-100 text-amber-800';
  if (severity === 'error') return 'bg-rose-50 text-rose-700';
  return 'bg-slate-100 text-slate-600';
}

function formatDateInputValue(value?: string | null) {
  if (!value) return '';
  return value.slice(0, 10);
}

function getCalendarLogStatusLabel(status: string) {
  if (status === 'success') return 'Успешно';
  if (status === 'error') return 'Ошибка';
  if (status === 'warning') return 'Внимание';
  if (status === 'skipped') return 'Пропущено';
  return status || 'Неизвестно';
}

function getCalendarLogStatusClass(status: string) {
  if (status === 'success') return 'bg-emerald-50 text-emerald-700';
  if (status === 'error') return 'bg-rose-50 text-rose-700';
  if (status === 'warning') return 'bg-amber-100 text-amber-800';
  if (status === 'skipped') return 'bg-slate-100 text-slate-600';
  return 'bg-gray-100 text-gray-700';
}

function getCalendarLogOperationLabel(operation: string) {
  const labels: Record<string, string> = {
    'create-event': 'Создание события',
    'update-event': 'Обновление события',
    'delete-event': 'Удаление события',
    'delete-calendar': 'Удаление календаря',
    'settings-updated': 'Настройки изменены',
    'disable-sync': 'Синхронизация отключена',
    'sync-now': 'Ручной запуск',
    import: 'Импорт события',
  };
  return labels[operation] ?? operation;
}
