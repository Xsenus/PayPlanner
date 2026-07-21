import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { Activity, AlertTriangle, CalendarDays, CheckCircle2, ExternalLink, Filter, HelpCircle, Power, RefreshCw, Save, ShieldCheck, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { authService, type User } from '../../services/authService';
import {
  apiService,
  type CalendarSyncProviderDto,
  type CalendarSyncReadinessDto,
  type CalendarSyncLogEntryDto,
  type CalendarSyncLogPageDto,
  type CalendarSyncSettingsDto,
} from '../../services/api';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone, formatDateTimeWithSecondsInTimeZone, fromTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { TablePagination } from '../ui/TablePagination';

const LOG_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const MIN_CALENDAR_SYNC_INTERVAL_MINUTES = 1;
const MAX_CALENDAR_SYNC_INTERVAL_MINUTES = 1440;
const SHOW_INLINE_READINESS_PANEL = import.meta.env.VITE_ENABLE_CALENDAR_SYNC_READINESS_PANEL === 'true';
const completedTaskActionOptions = [
  { value: 'color', label: 'Выделять цветом' },
  { value: 'hide', label: 'Скрывать из календарей' },
];
const googleEventColorOptions = [
  { value: '8', label: 'Графитовый' },
  { value: '2', label: 'Зеленый' },
  { value: '5', label: 'Желтый' },
  { value: '6', label: 'Оранжевый' },
  { value: '11', label: 'Красный' },
  { value: '7', label: 'Бирюзовый' },
  { value: '9', label: 'Синий' },
  { value: '10', label: 'Фиолетовый' },
];

type CalendarLogFilters = {
  provider: string;
  status: string;
  operation: string;
  userId: string;
  createdFrom: string;
  createdTo: string;
  search: string;
};

const defaultLogFilters: CalendarLogFilters = {
  provider: '',
  status: '',
  operation: '',
  userId: '',
  createdFrom: '',
  createdTo: '',
  search: '',
};

export function CalendarSyncProviderSettings() {
  const timeZoneId = useUserTimeZone();
  const [providers, setProviders] = useState<CalendarSyncProviderDto[]>([]);
  const [readiness, setReadiness] = useState<CalendarSyncReadinessDto | null>(null);
  const [logs, setLogs] = useState<CalendarSyncLogPageDto | null>(null);
  const [settings, setSettings] = useState<CalendarSyncSettingsDto | null>(null);
  const [draft, setDraft] = useState<CalendarSyncSettingsDto | null>(null);
  const [users, setUsers] = useState<User[]>([]);
  const [logFilters, setLogFilters] = useState<CalendarLogFilters>(defaultLogFilters);
  const [loading, setLoading] = useState(false);
  const [logsLoading, setLogsLoading] = useState(false);
  const [logPage, setLogPage] = useState(1);
  const [logPageSize, setLogPageSize] = useState(25);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [readinessOpen, setReadinessOpen] = useState(false);
  const [activeSettingHelpKey, setActiveSettingHelpKey] = useState<CalendarSettingHelpKey | null>(null);

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

  const totalLogPages = useMemo(
    () => Math.max(1, Math.ceil((logs?.total ?? 0) / Math.max(1, logs?.pageSize ?? logPageSize))),
    [logs, logPageSize],
  );

  const applyLoadedSettings = useCallback((next: CalendarSyncSettingsDto) => {
    setSettings(next);
    setDraft({ ...next });
  }, []);

  const buildLogQuery = useCallback((page: number, pageSize: number) => ({
    provider: logFilters.provider || undefined,
    status: logFilters.status || undefined,
    operation: logFilters.operation || undefined,
    userId: logFilters.userId ? Number(logFilters.userId) : undefined,
    createdFrom: logFilters.createdFrom ? fromTimeZoneDateTimeInput(logFilters.createdFrom, timeZoneId) : undefined,
    createdTo: logFilters.createdTo ? fromTimeZoneDateTimeInput(logFilters.createdTo, timeZoneId) : undefined,
    search: logFilters.search.trim() || undefined,
    page,
    pageSize,
  }), [logFilters, timeZoneId]);

  const loadCalendarSyncLogs = useCallback(async (page = logPage, pageSize = logPageSize) => {
    setLogsLoading(true);
    try {
      const loadedLogs = await apiService.getCalendarSyncLogs(buildLogQuery(page, pageSize));
      setLogs(loadedLogs);
      setLogPage(loadedLogs.page);
      setLogPageSize(loadedLogs.pageSize);
    } finally {
      setLogsLoading(false);
    }
  }, [buildLogQuery, logPage, logPageSize]);

  const loadCalendarSyncState = useCallback(async () => {
    setLoading(true);
    setError(null);
    setSaveMessage(null);
    try {
      const [loadedProviders, loadedReadiness, loadedSettings, loadedUsers] = await Promise.all([
        apiService.getCalendarSyncProviders(),
        apiService.getCalendarSyncReadiness(),
        apiService.getCalendarSyncSettings(),
        authService.getUsers('active').catch(() => []),
      ]);
      const loadedLogs = await apiService.getCalendarSyncLogs({ page: 1, pageSize: logPageSize });
      setProviders(loadedProviders);
      setReadiness(loadedReadiness);
      setUsers(loadedUsers);
      setLogs(loadedLogs);
      setLogPage(loadedLogs.page);
      setLogPageSize(loadedLogs.pageSize);
      applyLoadedSettings(loadedSettings);
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить состояние внешних календарей.'));
    } finally {
      setLoading(false);
    }
  }, [applyLoadedSettings, logPageSize]);

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
        googleCalendarSyncEnabled: draft.googleCalendarSyncEnabled === true,
        yandexCalendarSyncEnabled: draft.yandexCalendarSyncEnabled === true,
        googleSyncIntervalMinutes: normalizeCalendarSyncInterval(draft.googleSyncIntervalMinutes),
        yandexSyncIntervalMinutes: normalizeCalendarSyncInterval(draft.yandexSyncIntervalMinutes),
        manualFullSyncCooldownMinutes: normalizeCalendarSyncInterval(draft.manualFullSyncCooldownMinutes),
        completedTaskCalendarAction: draft.completedTaskCalendarAction,
        completedTaskGoogleColorId: draft.completedTaskGoogleColorId,
        syncMode: draft.syncMode,
        conflictResolutionMode: draft.conflictResolutionMode,
        userControlMode: draft.userControlMode,
        defaultSyncFromDate: draft.defaultSyncFromDate || null,
        inactiveUserAction: draft.inactiveUserAction,
        requireManualConfirmation: draft.requireManualConfirmation,
        dryRunConfirmed: draft.dryRunConfirmed,
        notes: draft.notes,
      });
      const [loadedProviders, loadedReadiness] = await Promise.all([
        apiService.getCalendarSyncProviders(),
        apiService.getCalendarSyncReadiness(),
      ]);
      applyLoadedSettings(updated);
      setProviders(loadedProviders);
      setReadiness(loadedReadiness);
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
        Доступно сейчас: {availableCount} из {providers.length || 0}. Google и Яндекс запускаются только после включения внешнего календарного контура и настройки OAuth.
      </div>

      {draft ? (
        <div className="mt-5 rounded-2xl border border-gray-200 bg-white p-4">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <h5 className="text-sm font-semibold text-gray-900">Модель синхронизации и конфликты</h5>
                <button
                  type="button"
                  onClick={() => setReadinessOpen(true)}
                  className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-sky-200 bg-sky-50 text-sky-700 transition hover:bg-sky-100"
                  title="Показать готовность к подключению"
                  aria-label="Показать готовность к подключению календарей"
                >
                  <HelpCircle className="h-4 w-4" />
                </button>
              </div>
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

          <div className="mt-4 grid gap-3 md:grid-cols-2">
            <ProviderToggle
              checked={draft.googleCalendarSyncEnabled === true}
              label="Включить Google Calendar"
              description="Google Calendar перейдет из состояния «Выключен» в подготовку подключения. После этого нужно заполнить OAuth Google и закрыть чек-лист готовности."
              helpKey="googleCalendarSyncEnabled"
              onOpenHelp={setActiveSettingHelpKey}
              onChange={(checked) => updateDraft({ googleCalendarSyncEnabled: checked })}
            />
            <ProviderToggle
              checked={draft.yandexCalendarSyncEnabled === true}
              label="Включить Яндекс Календарь"
              description="Яндекс Календарь перейдет из состояния «Выключен» в подготовку подключения. После этого нужно заполнить OAuth Яндекса и закрыть чек-лист готовности."
              helpKey="yandexCalendarSyncEnabled"
              onOpenHelp={setActiveSettingHelpKey}
              onChange={(checked) => updateDraft({ yandexCalendarSyncEnabled: checked })}
            />
          </div>

          <div className="mt-4 grid gap-4 xl:grid-cols-2">
          <div className="rounded-2xl border border-gray-200 bg-gray-50 p-4">
            <div className="flex flex-col gap-1">
              <h6 className="text-sm font-semibold text-gray-900">Интервалы фоновой синхронизации</h6>
              <p className="text-xs leading-5 text-gray-600">
                Стандартный интервал - 10 минут. Минимум в CRM - {MIN_CALENDAR_SYNC_INTERVAL_MINUTES} минут, чтобы не создавать лишнюю нагрузку на API календарей.
              </p>
            </div>
            <div className="mt-3 grid gap-3 md:grid-cols-2">
              <CalendarIntervalInput
                label="Google Calendar"
                value={draft.googleSyncIntervalMinutes}
                onChange={(value) => updateDraft({ googleSyncIntervalMinutes: value })}
              />
              <CalendarIntervalInput
                label="Яндекс Календарь"
                value={draft.yandexSyncIntervalMinutes}
                onChange={(value) => updateDraft({ yandexSyncIntervalMinutes: value })}
              />
            </div>
          </div>

          <div className="rounded-2xl border border-amber-100 bg-amber-50/70 p-4">
            <div className="flex flex-col gap-1">
              <h6 className="text-sm font-semibold text-gray-900">Защита ручной полной синхронизации</h6>
              <p className="text-xs leading-5 text-gray-600">
                После ручного запуска пользователю нужно подождать этот интервал перед повторной полной синхронизацией. Фоновый обмен по расписанию продолжает работать.
              </p>
            </div>
            <div className="mt-3 max-w-md">
              <CalendarIntervalInput
                label="Повторный ручной запуск"
                value={draft.manualFullSyncCooldownMinutes}
                onChange={(value) => updateDraft({ manualFullSyncCooldownMinutes: value })}
              />
            </div>
          </div>
          </div>

          <div className="mt-4 rounded-2xl border border-emerald-100 bg-emerald-50/70 p-4">
            <div className="flex flex-col gap-1">
              <h6 className="text-sm font-semibold text-gray-900">Выполненные задачи в календарях</h6>
              <p className="text-xs leading-5 text-gray-600">
                Администратор выбирает, оставлять готовые задачи во внутреннем календаре и внешних календарях с отдельной отметкой или убирать их после выполнения.
              </p>
            </div>
            <div className="mt-3 grid gap-3 md:grid-cols-2">
              <label className="block">
                <span className="text-xs font-semibold text-gray-700">Действие после выполнения</span>
                <SystemSelect
                  value={draft.completedTaskCalendarAction}
                  onChange={(event) => updateDraft({ completedTaskCalendarAction: event.target.value })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                >
                  {completedTaskActionOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </SystemSelect>
              </label>
              <label className="block">
                <span className="text-xs font-semibold text-gray-700">Цвет готовых задач в Google</span>
                <SystemSelect
                  value={draft.completedTaskGoogleColorId}
                  onChange={(event) => updateDraft({ completedTaskGoogleColorId: event.target.value })}
                  disabled={draft.completedTaskCalendarAction !== 'color'}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100 disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-400"
                >
                  {googleEventColorOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </SystemSelect>
              </label>
            </div>
          </div>

          <div className="mt-4 grid gap-3 lg:grid-cols-2">
            <label className="block">
              <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
                Главный источник событий
                <SettingHelpButton helpKey="syncMode" onOpen={setActiveSettingHelpKey} />
              </span>
              <SystemSelect
                value={draft.syncMode}
                onChange={(event) => updateDraft({ syncMode: event.target.value })}
                className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                {syncModeOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
            </label>

            <label className="block">
              <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
                Правило спорных изменений
                <SettingHelpButton helpKey="conflictResolutionMode" onOpen={setActiveSettingHelpKey} />
              </span>
              <SystemSelect
                value={draft.conflictResolutionMode}
                onChange={(event) => updateDraft({ conflictResolutionMode: event.target.value })}
                className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                {conflictResolutionOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
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
                <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
                  Кто управляет
                  <SettingHelpButton helpKey="userControlMode" onOpen={setActiveSettingHelpKey} />
                </span>
                <SystemSelect
                  value={draft.userControlMode}
                  onChange={(event) => updateDraft({ userControlMode: event.target.value })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                >
                  {userControlModeOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </SystemSelect>
              </label>

              <label className="block">
                <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
                  Дата начала по умолчанию
                  <SettingHelpButton helpKey="defaultSyncFromDate" onOpen={setActiveSettingHelpKey} />
                </span>
                <SystemDateInput
                  type="date"
                  value={formatDateInputValue(draft.defaultSyncFromDate)}
                  onChange={(event) => updateDraft({ defaultSyncFromDate: event.target.value || null })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                />
              </label>

              <label className="block">
                <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
                  При потере доступа
                  <SettingHelpButton helpKey="inactiveUserAction" onOpen={setActiveSettingHelpKey} />
                </span>
                <SystemSelect
                  value={draft.inactiveUserAction}
                  onChange={(event) => updateDraft({ inactiveUserAction: event.target.value })}
                  className="mt-1 h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
                >
                  {inactiveUserActionOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </SystemSelect>
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
              <span className="inline-flex items-center gap-1">
                Требовать ручное подтверждение спорных изменений
                <SettingHelpButton helpKey="requireManualConfirmation" onOpen={setActiveSettingHelpKey} />
              </span>
            </label>
            <label className="flex items-start gap-2 rounded-xl border border-gray-200 bg-gray-50 px-3 py-3 text-xs text-gray-700">
              <input
                type="checkbox"
                checked={draft.dryRunConfirmed}
                onChange={(event) => updateDraft({ dryRunConfirmed: event.target.checked })}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-sky-600 focus:ring-sky-500"
              />
              <span className="inline-flex items-center gap-1">
                Dry-run на копии базы выполнен
                <SettingHelpButton helpKey="dryRunConfirmed" onOpen={setActiveSettingHelpKey} />
              </span>
            </label>
          </div>

          <label className="mt-4 block">
            <span className="inline-flex items-center gap-1 text-xs font-semibold text-gray-700">
              Комментарий к решению
              <SettingHelpButton helpKey="notes" onOpen={setActiveSettingHelpKey} />
            </span>
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
            <p className="mt-2 text-[11px] text-gray-500">Последнее сохранение: {formatDateTimeInTimeZone(settings.updatedAt, timeZoneId) || settings.updatedAt}</p>
          ) : null}
        </div>
      ) : null}

      {SHOW_INLINE_READINESS_PANEL && readiness ? (
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
            onClick={() => void loadCalendarSyncLogs(logPage, logPageSize)}
            disabled={logsLoading}
            className="inline-flex h-9 items-center justify-center gap-2 rounded-lg border border-gray-300 px-3 text-xs font-semibold text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${logsLoading ? 'animate-spin' : ''}`} />
            <span>{logsLoading ? 'Обновляем' : 'Обновить журнал'}</span>
          </button>
        </div>

        <div className="mt-4 rounded-2xl border border-slate-200 bg-slate-50 p-3">
          <div className="mb-3 flex items-center gap-2 text-xs font-semibold text-slate-700">
            <Filter className="h-4 w-4 text-sky-700" />
            Фильтры журнала
          </div>
          <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">Провайдер</span>
              <SystemSelect
                value={logFilters.provider}
                onChange={(event) => setLogFilters((current) => ({ ...current, provider: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                <option value="">Все</option>
                <option value="google-calendar">Google Calendar</option>
                <option value="yandex-calendar">Яндекс Календарь</option>
              </SystemSelect>
            </label>
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">Сотрудник</span>
              <SystemSelect
                value={logFilters.userId}
                onChange={(event) => setLogFilters((current) => ({ ...current, userId: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                <option value="">Все сотрудники</option>
                {users.map((user) => (
                  <option key={user.id} value={user.id}>{user.fullName || user.email}</option>
                ))}
              </SystemSelect>
            </label>
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">Действие</span>
              <SystemSelect
                value={logFilters.operation}
                onChange={(event) => setLogFilters((current) => ({ ...current, operation: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                <option value="">Все действия</option>
                {calendarLogOperationOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
            </label>
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">Статус</span>
              <SystemSelect
                value={logFilters.status}
                onChange={(event) => setLogFilters((current) => ({ ...current, status: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              >
                <option value="">Все статусы</option>
                <option value="success">Успешно</option>
                <option value="error">Ошибка</option>
                <option value="warning">Внимание</option>
                <option value="skipped">Пропущено</option>
              </SystemSelect>
            </label>
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">С даты и времени</span>
              <SystemDateInput
                type="datetime-local"
                value={logFilters.createdFrom}
                onChange={(event) => setLogFilters((current) => ({ ...current, createdFrom: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              />
            </label>
            <label className="block">
              <span className="text-[11px] font-semibold uppercase text-slate-500">По дату и время</span>
              <SystemDateInput
                type="datetime-local"
                value={logFilters.createdTo}
                onChange={(event) => setLogFilters((current) => ({ ...current, createdTo: event.target.value }))}
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              />
            </label>
            <label className="block xl:col-span-2">
              <span className="text-[11px] font-semibold uppercase text-slate-500">Поиск</span>
              <input
                type="search"
                value={logFilters.search}
                onChange={(event) => setLogFilters((current) => ({ ...current, search: event.target.value }))}
                placeholder="Задача, объект, сотрудник, сообщение"
                className="mt-1 h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-xs text-slate-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
              />
            </label>
          </div>
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void loadCalendarSyncLogs(1, logPageSize)}
              className="inline-flex h-8 items-center justify-center rounded-lg bg-sky-600 px-3 text-xs font-semibold text-white hover:bg-sky-700"
            >
              Применить фильтры
            </button>
            <button
              type="button"
              onClick={() => {
                setLogFilters(defaultLogFilters);
                void apiService.getCalendarSyncLogs({ page: 1, pageSize: logPageSize }).then((loadedLogs) => {
                  setLogs(loadedLogs);
                  setLogPage(loadedLogs.page);
                  setLogPageSize(loadedLogs.pageSize);
                });
              }}
              className="inline-flex h-8 items-center justify-center rounded-lg border border-slate-300 bg-white px-3 text-xs font-semibold text-slate-700 hover:bg-slate-50"
            >
              Сбросить
            </button>
          </div>
        </div>

        <div className="mt-4 overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200 text-left text-xs">
            <thead className="bg-gray-50 text-[11px] font-semibold uppercase text-gray-500">
              <tr>
                <th className="px-3 py-2">Когда и куда</th>
                <th className="px-3 py-2">Участники</th>
                <th className="px-3 py-2">Объект и изменения</th>
                <th className="px-3 py-2">Результат</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {logs?.items.length ? logs.items.map((item) => (
                <tr key={`${item.provider}-${item.id}`} className="align-top">
                  <td className="min-w-[180px] px-3 py-2 text-gray-700">
                    <div className="whitespace-nowrap font-medium">{formatCalendarSyncLogDateTime(item.createdAt, timeZoneId)}</div>
                    <div className="mt-1 font-semibold text-gray-900">{item.providerLabel}</div>
                    <div className="text-[11px] text-gray-500">{item.directionLabel || getCalendarLogDirectionLabel(item)}</div>
                    <div className="mt-1 text-[11px] font-semibold text-sky-700">{item.operationLabel || getCalendarLogOperationLabel(item.operation)}</div>
                  </td>
                  <td className="min-w-[230px] px-3 py-2 text-gray-700">
                    <div className="font-medium">{item.actorLabel || getCalendarLogEmployeeName(item)}</div>
                    {getCalendarLogEmployeeEmail(item) ? <div className="text-[11px] text-gray-500">{getCalendarLogEmployeeEmail(item)}</div> : null}
                    {item.calendarRoleLabel ? <div className="mt-1 text-[11px] text-gray-500">{item.calendarRoleLabel}</div> : null}
                    {item.taskAssigneeName ? <div className="text-[11px] text-gray-500">Исполнитель задачи: {item.taskAssigneeName}</div> : null}
                  </td>
                  <td className="min-w-[320px] px-3 py-2 text-gray-700">
                    <div className="font-medium">{getCalendarLogTargetLabel(item)}</div>
                    {getCalendarLogTargetDetails(item).map((detail) => (
                      <div key={detail} className="text-[11px] text-gray-500">{detail}</div>
                    ))}
                    {item.changeSummary ? <div className="mt-2 rounded-lg bg-sky-50 px-2 py-1 text-[11px] leading-4 text-sky-900">Изменения: {item.changeSummary}</div> : null}
                    {!item.changeSummary && item.details ? <div className="mt-2 rounded-lg bg-slate-50 px-2 py-1 text-[11px] leading-4 text-slate-600">Параметры: {item.details}</div> : null}
                  </td>
                  <td className="min-w-[240px] px-3 py-2 text-gray-600">
                    <span className={`inline-flex rounded-full px-2 py-0.5 font-semibold ${getCalendarLogStatusClass(item.status)}`}>
                      {item.statusLabel || getCalendarLogStatusLabel(item.status)}
                    </span>
                    <div className="mt-2 leading-5">{item.message || 'Без сообщения'}</div>
                  </td>
                </tr>
              )) : (
                <tr>
                  <td colSpan={4} className="px-3 py-6 text-center text-sm text-gray-500">Событий синхронизации не найдено.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        {logs ? (
          <div className="mt-3 overflow-hidden rounded-xl border border-slate-200">
            <TablePagination
              page={logs.page}
              totalPages={totalLogPages}
              totalItems={logs.total}
              pageSize={logs.pageSize}
              pageSizeOptions={LOG_PAGE_SIZE_OPTIONS}
              recordsLabel="Событий"
              pageSizeLabel="Событий на странице"
              onPageChange={(nextPage) => void loadCalendarSyncLogs(nextPage, logs.pageSize)}
              onPageSizeChange={(nextPageSize) => void loadCalendarSyncLogs(1, nextPageSize)}
            />
          </div>
        ) : null}
      </div>
      {readinessOpen ? (
        <CalendarSyncReadinessModal
          readiness={readiness}
          requiredReadyCount={requiredReadyCount}
          requiredTotalCount={requiredTotalCount}
          onClose={() => setReadinessOpen(false)}
        />
      ) : null}
      {activeSettingHelpKey ? (
        <CalendarSyncSettingHelpModal
          helpItem={getCalendarSettingHelpItem(activeSettingHelpKey)}
          onClose={() => setActiveSettingHelpKey(null)}
        />
      ) : null}
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

const calendarLogOperationOptions = [
  { value: 'create-event', label: 'Создание события' },
  { value: 'update-event', label: 'Обновление события' },
  { value: 'update-task', label: 'Обновление задачи в CRM' },
  { value: 'delete-event', label: 'Удаление события' },
  { value: 'create-calendar', label: 'Создание календаря сотрудника' },
  { value: 'delete-calendar', label: 'Удаление календаря сотрудника' },
  { value: 'settings-updated', label: 'Настройки изменены' },
  { value: 'sync-now', label: 'Ручной запуск синхронизации' },
  { value: 'import', label: 'Чтение изменений календаря' },
  { value: 'skip', label: 'Пропуск по правилу конфликтов' },
  { value: 'detach', label: 'Отвязка удаленного события' },
  { value: 'oauth-connected', label: 'Календарь подключен' },
];

function Capability({ enabled, label }: { enabled: boolean; label: string }) {
  return (
    <span className={`rounded-xl px-2 py-1 font-medium ${enabled ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
      {label}
    </span>
  );
}

function CalendarIntervalInput({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <label className="block">
      <span className="text-xs font-semibold text-gray-700">{label}</span>
      <div className="mt-1 flex items-center gap-2">
        <input
          type="number"
          min={MIN_CALENDAR_SYNC_INTERVAL_MINUTES}
          max={MAX_CALENDAR_SYNC_INTERVAL_MINUTES}
          step={1}
          value={Number.isFinite(value) ? value : 10}
          onChange={(event) => onChange(Number(event.target.value))}
          onBlur={(event) => onChange(normalizeCalendarSyncInterval(Number(event.target.value)))}
          className="h-10 w-full rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-800 focus:border-sky-500 focus:outline-none focus:ring-2 focus:ring-sky-100"
        />
        <span className="shrink-0 text-xs text-gray-500">мин.</span>
      </div>
    </label>
  );
}

function ProviderToggle({
  checked,
  label,
  description,
  helpKey,
  onOpenHelp,
  onChange,
}: {
  checked: boolean;
  label: string;
  description: string;
  helpKey?: CalendarSettingHelpKey;
  onOpenHelp?: (key: CalendarSettingHelpKey) => void;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className={`flex items-start gap-3 rounded-2xl border px-4 py-4 ${checked ? 'border-emerald-200 bg-emerald-50' : 'border-amber-200 bg-amber-50'}`}>
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-1 h-4 w-4 rounded border-gray-300 text-sky-600 focus:ring-sky-500"
      />
      <span className="min-w-0">
        <span className="flex items-center gap-2 text-sm font-semibold text-gray-900">
          <Power className="h-4 w-4 text-sky-700" />
          {label}
          {helpKey && onOpenHelp ? <SettingHelpButton helpKey={helpKey} onOpen={onOpenHelp} /> : null}
        </span>
        <span className="mt-1 block text-xs leading-5 text-gray-600">{description}</span>
      </span>
    </label>
  );
}

type CalendarSettingHelpKey =
  | 'googleCalendarSyncEnabled'
  | 'yandexCalendarSyncEnabled'
  | 'syncMode'
  | 'conflictResolutionMode'
  | 'userControlMode'
  | 'defaultSyncFromDate'
  | 'inactiveUserAction'
  | 'requireManualConfirmation'
  | 'dryRunConfirmed'
  | 'notes';

interface CalendarSettingHelpItem {
  key: CalendarSettingHelpKey;
  title: string;
  description: string;
  redirectUri?: string;
  steps?: string[];
  links?: Array<{
    label: string;
    url: string;
  }>;
  note?: string;
}

function SettingHelpButton({
  helpKey,
  onOpen,
}: {
  helpKey: CalendarSettingHelpKey;
  onOpen: (key: CalendarSettingHelpKey) => void;
}) {
  const item = getCalendarSettingHelpItem(helpKey);
  return (
    <button
      type="button"
      onClick={(event) => {
        event.preventDefault();
        event.stopPropagation();
        onOpen(helpKey);
      }}
      className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-sky-200 bg-sky-50 text-sky-700 transition hover:bg-sky-100"
      title={`Что делает настройка «${item.title}»`}
      aria-label={`Что делает настройка «${item.title}»`}
    >
      <HelpCircle className="h-3.5 w-3.5" />
    </button>
  );
}

function CalendarSyncReadinessModal({
  readiness,
  requiredReadyCount,
  requiredTotalCount,
  onClose,
}: {
  readiness: CalendarSyncReadinessDto | null;
  requiredReadyCount: number;
  requiredTotalCount: number;
  onClose: () => void;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4 py-6">
      <div className="max-h-[90vh] w-full max-w-5xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
          <div>
            <div className="inline-flex items-center gap-2 text-sm font-semibold text-amber-700">
              <AlertTriangle className="h-4 w-4" />
              Готовность к подключению
            </div>
            <h3 className="mt-1 text-lg font-semibold text-slate-900">Готовность к подключению Google/Яндекс</h3>
            {readiness ? (
              <p className="mt-1 text-sm leading-6 text-slate-600">
                Выполнено обязательных шагов: {requiredReadyCount} из {requiredTotalCount}. Пока все обязательные пункты не закрыты, внешняя синхронизация остается подготовительным контуром.
              </p>
            ) : (
              <p className="mt-1 text-sm leading-6 text-slate-600">Чек-лист готовности еще загружается.</p>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800"
            title="Закрыть"
            aria-label="Закрыть готовность к подключению"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="max-h-[calc(90vh-116px)] overflow-y-auto px-5 py-4">
          {readiness ? (
            <>
              <div className="mb-4 flex justify-end">
                <span className={`inline-flex w-fit items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-semibold ${readiness.readyToConnect ? 'border-emerald-200 bg-emerald-50 text-emerald-700' : 'border-amber-200 bg-white text-amber-700'}`}>
                  {readiness.readyToConnect ? <CheckCircle2 className="h-3.5 w-3.5" /> : <AlertTriangle className="h-3.5 w-3.5" />}
                  {readiness.readyToConnect ? 'Можно подключать' : 'Нужна подготовка'}
                </span>
              </div>
              <div className="grid gap-2 md:grid-cols-2">
                {readiness.items.map((item) => (
                  <div key={item.key} className="rounded-xl border border-slate-200 bg-slate-50 px-3 py-3">
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
            </>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function CalendarSyncSettingHelpModal({
  helpItem,
  onClose,
}: {
  helpItem: CalendarSettingHelpItem;
  onClose: () => void;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4 py-6">
      <div className="max-h-[90vh] w-full max-w-xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
          <div>
            <div className="inline-flex items-center gap-2 text-sm font-semibold text-sky-700">
              <HelpCircle className="h-4 w-4" />
              Пояснение настройки
            </div>
            <h3 className="mt-1 text-lg font-semibold text-slate-900">{helpItem.title}</h3>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800"
            title="Закрыть"
            aria-label="Закрыть пояснение"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="max-h-[calc(90vh-82px)] overflow-y-auto px-5 py-4">
          <p className="text-sm leading-6 text-slate-700">{helpItem.description}</p>
          {helpItem.redirectUri ? (
            <div className="mt-4 rounded-2xl border border-emerald-200 bg-emerald-50 px-4 py-3">
              <div className="text-sm font-semibold text-emerald-950">Redirect URI для кабинета провайдера</div>
              <div className="mt-2 rounded-xl border border-emerald-200 bg-white px-3 py-2 font-mono text-sm text-emerald-950 break-all">
                {helpItem.redirectUri}
              </div>
              <p className="mt-2 text-xs leading-5 text-emerald-900">
                Скопируйте этот адрес без изменений в настройки OAuth-приложения. Адрес должен совпадать полностью.
              </p>
            </div>
          ) : null}
          {helpItem.steps?.length ? (
            <div className="mt-4 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="text-sm font-semibold text-slate-900">Как настроить</div>
              <ol className="mt-2 list-decimal space-y-2 pl-5 text-sm leading-6 text-slate-700">
                {helpItem.steps.map((step) => (
                  <li key={step}>{step}</li>
                ))}
              </ol>
            </div>
          ) : null}
          {helpItem.links?.length ? (
            <div className="mt-4 grid gap-2">
              {helpItem.links.map((link) => (
                <a
                  key={link.url}
                  href={link.url}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center justify-between gap-3 rounded-xl border border-sky-200 bg-sky-50 px-3 py-2 text-sm font-semibold text-sky-800 transition hover:bg-sky-100"
                >
                  <span>{link.label}</span>
                  <ExternalLink className="h-4 w-4 shrink-0" />
                </a>
              ))}
            </div>
          ) : null}
          {helpItem.note ? (
            <div className="mt-4 rounded-2xl border border-sky-200 bg-sky-50 px-4 py-3 text-sm leading-6 text-sky-900">
              {helpItem.note}
            </div>
          ) : null}
          <div className="mt-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm leading-6 text-amber-900">
            Изменения сохраняются только после нажатия кнопки «Сохранить». Если настройка влияет на внешний календарь, действие будет записано в журнал синхронизации.
          </div>
        </div>
      </div>
    </div>
  );
}

const calendarSettingsHelpItems: CalendarSettingHelpItem[] = [
  {
    key: 'googleCalendarSyncEnabled',
    title: 'Включить Google Calendar',
    description: 'Разрешает подготовку и запуск синхронизации с Google Calendar. После включения нужно настроить OAuth Google и закрыть обязательные пункты готовности.',
    redirectUri: 'https://api.elitpravo.com/api/google-calendar/auth/callback',
    steps: [
      'В Google Cloud Console создайте или выберите проект для CRM.',
      'Включите Google Calendar API для выбранного проекта.',
      'Создайте OAuth Client ID типа Web application.',
      'В Authorized redirect URIs добавьте Redirect URI из блока выше.',
      'Скопируйте Client ID и Client Secret в раздел «Ключи интеграций» → вкладка «Календари».',
      'Если приложение находится в режиме тестирования, добавьте сотрудников в тестовые пользователи OAuth consent screen или переведите приложение в публикацию.',
    ],
    links: [
      { label: 'Google Cloud Console: Credentials', url: 'https://console.cloud.google.com/apis/credentials' },
      { label: 'Google Calendar API: авторизация и scopes', url: 'https://developers.google.com/calendar/api/auth' },
      { label: 'Google Cloud: настройка OAuth consent screen', url: 'https://support.google.com/cloud/answer/10311615' },
    ],
    note: 'Redirect URI должен совпадать полностью: протокол, домен, путь и слеши. Если Google показывает redirect_uri_mismatch, проверьте именно это поле.',
  },
  {
    key: 'yandexCalendarSyncEnabled',
    title: 'Включить Яндекс Календарь',
    description: 'Разрешает подготовку и запуск синхронизации с Яндекс Календарем. Провайдер можно включить независимо от Google Calendar.',
    redirectUri: 'https://api.elitpravo.com/api/yandex-calendar/auth/callback',
    steps: [
      'В кабинете OAuth Яндекс ID создайте приложение или откройте уже созданное.',
      'Добавьте Redirect URI из блока выше в настройки приложения.',
      'Выдайте приложению права для работы с календарем через CalDAV/Яндекс Календарь.',
      'Скопируйте Client ID и Client Secret в раздел «Ключи интеграций» → вкладка «Календари».',
      'Проверьте чек-лист готовности и только после этого разрешайте пользователям подключать календарь.',
    ],
    links: [
      { label: 'Яндекс OAuth: управление приложениями', url: 'https://oauth.yandex.ru/' },
      { label: 'Документация Яндекс ID: OAuth cabinet', url: 'https://yandex.ru/dev/id/doc/ru/oauth-cabinet' },
      { label: 'Документация Яндекс ID: Redirect URI и код авторизации', url: 'https://yandex.ru/dev/id/doc/ru/codes/code-url' },
    ],
    note: 'Если в Яндексе не указан redirect URI или не хватает прав календаря, подключение пользователя не завершится и ошибка попадет в журнал синхронизации.',
  },
  {
    key: 'syncMode',
    title: 'Главный источник событий',
    description: 'Определяет, что считать основным источником при обмене данными: CRM, внешний календарь или гибридную модель.',
  },
  {
    key: 'conflictResolutionMode',
    title: 'Правило спорных изменений',
    description: 'Задает, что делать, если одно и то же событие изменили в CRM и во внешнем календаре: оставить CRM, принять календарь, взять последнее изменение или отправить на ручное подтверждение.',
  },
  {
    key: 'userControlMode',
    title: 'Кто управляет',
    description: 'В режиме пользователя сотрудник сам управляет своим календарем. В режиме администратора правила задаются централизованно и применяются к подключениям сотрудников.',
  },
  {
    key: 'defaultSyncFromDate',
    title: 'Дата начала по умолчанию',
    description: 'Ограничивает, с какой даты задачи будут выгружаться во внешние календари. Очистка при отключении всё равно удаляет все ранее созданные CRM события.',
  },
  {
    key: 'inactiveUserAction',
    title: 'При потере доступа',
    description: 'Определяет действие при блокировке, увольнении или удалении сотрудника: удалить внешние события, только отключить синхронизацию или оставить подключение без изменений.',
  },
  {
    key: 'requireManualConfirmation',
    title: 'Ручное подтверждение',
    description: 'Включает дополнительную защиту для конфликтов, чтобы спорные изменения не применялись автоматически без проверки ответственным пользователем.',
  },
  {
    key: 'dryRunConfirmed',
    title: 'Dry-run на копии базы',
    description: 'Отметка, что тестовый прогон на копии базы выполнен. Это часть чек-листа перед запуском фоновой синхронизации.',
  },
  {
    key: 'notes',
    title: 'Комментарий к решению',
    description: 'Свободное поле для заметки администратора: кто согласовал модель, когда проверяли dry-run и какие ограничения приняли.',
  },
];

function getCalendarSettingHelpItem(key: CalendarSettingHelpKey) {
  return calendarSettingsHelpItems.find((item) => item.key === key) ?? calendarSettingsHelpItems[0];
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

function normalizeCalendarSyncInterval(value: number | undefined | null) {
  if (!Number.isFinite(value ?? Number.NaN)) return 10;
  return Math.min(MAX_CALENDAR_SYNC_INTERVAL_MINUTES, Math.max(MIN_CALENDAR_SYNC_INTERVAL_MINUTES, Math.round(value ?? 10)));
}

function formatCalendarSyncLogDateTime(value: string, timeZoneId?: string | null) {
  const normalizedValue = /(?:z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`;
  return formatDateTimeWithSecondsInTimeZone(normalizedValue, timeZoneId) || value;
}

function getCalendarLogEmployeeName(item: CalendarSyncLogEntryDto) {
  return firstDisplayValue(item.userName, item.calendarOwnerName, item.taskAssigneeName, 'Система');
}

function getCalendarLogEmployeeEmail(item: CalendarSyncLogEntryDto) {
  return firstDisplayValue(item.userEmail, item.calendarOwnerEmail);
}

function getCalendarLogTargetDetails(item: CalendarSyncLogEntryDto) {
  const details = [
    item.targetType === 'calendar' && item.calendarOwnerName ? `Владелец календаря: ${item.calendarOwnerName}` : '',
    item.targetType === 'task' && item.taskAssigneeName ? `Исполнитель задачи: ${item.taskAssigneeName}` : '',
    item.targetType === 'task' && item.calendarOwnerName && item.calendarOwnerName !== item.taskAssigneeName ? `Календарь сотрудника: ${item.calendarOwnerName}` : '',
    item.taskObjectName,
  ];

  return Array.from(new Set(details.map((detail) => detail.trim()).filter(Boolean)));
}

function firstDisplayValue(...values: Array<string | undefined | null>) {
  return values.find((value) => value?.trim())?.trim() ?? '';
}

function getCalendarLogTargetLabel(item: CalendarSyncLogEntryDto) {
  if (item.targetLabel) return item.targetLabel;
  if (item.taskTitle) return `Задача: ${item.taskTitle}`;
  if (item.calendarName) return `Календарь: ${item.calendarName}`;
  if (getCalendarLogEmployeeName(item) !== 'Система') return `Подключение: ${getCalendarLogEmployeeName(item)}`;
  if (item.taskJournalItemId) return `Задача #${item.taskJournalItemId}`;
  return 'Системное действие';
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
    'update-task': 'Обновление задачи в CRM',
    detach: 'Отвязка удаленного события',
    skip: 'Изменение пропущено по правилу конфликтов',
    upsert: 'Создание или обновление события',
    'create-calendar': 'Создание календаря',
    'delete-calendar': 'Удаление календаря',
    'delete-calendar-missing': 'Календарь уже отсутствует',
    'cleanup-user-calendars': 'Очистка календарей сотрудника',
    'settings-updated': 'Настройки изменены',
    'disable-sync': 'Синхронизация отключена',
    'sync-now': 'Ручной запуск',
    import: 'Импорт события',
  };
  return labels[operation] ?? operation;
}

function getCalendarLogDirectionLabel(item: CalendarSyncLogEntryDto) {
  if (item.direction === 'crm-to-google' || item.direction === 'crm-to-yandex') return `CRM → ${item.providerLabel}`;
  if (item.direction === 'google-to-crm' || item.direction === 'yandex-to-crm') return `${item.providerLabel} → CRM`;
  if (item.direction === 'system') return 'Системное действие';
  return item.direction || 'Направление не указано';
}
