import { useCallback, useEffect, useRef, useState } from 'react';
import { CalendarCheck, Loader2, Play, RefreshCw, Save, Unlink2 } from 'lucide-react';
import {
  apiService,
  type UpdateYandexCalendarSettingsRequest,
  type YandexCalendarStatusDto,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type Props = {
  isAdmin?: boolean;
};

const formatDateTime = (value?: string | null) => {
  if (!value) return 'нет данных';

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value));
};

const toDateInputValue = (value?: string | null) => (value ? value.slice(0, 10) : '');

export function YandexCalendarPanel({ isAdmin = false }: Props) {
  const toast = useToast();
  const { confirm } = useDialog();
  const popupRef = useRef<Window | null>(null);
  const [status, setStatus] = useState<YandexCalendarStatusDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [connecting, setConnecting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [disconnecting, setDisconnecting] = useState(false);
  const [form, setForm] = useState<UpdateYandexCalendarSettingsRequest>({
    isEnabled: true,
    syncEnabled: true,
    calendarMode: 'personal',
    conflictResolutionMode: 'latestWins',
    syncFromDate: null,
  });

  const loadStatus = useCallback(async () => {
    const next = await apiService.getYandexCalendarStatus();
    setStatus(next);
    if (next.connection) {
      setForm({
        isEnabled: next.connection.isEnabled,
        syncEnabled: next.connection.syncEnabled,
        calendarMode: next.connection.calendarMode,
        conflictResolutionMode: next.connection.conflictResolutionMode,
        syncFromDate: toDateInputValue(next.connection.syncFromDate) || null,
      });
    }
    return next;
  }, []);

  useEffect(() => {
    setLoading(true);
    void loadStatus()
      .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить статус Яндекс.Календаря.')))
      .finally(() => setLoading(false));
  }, [loadStatus, toast]);

  useEffect(() => () => {
    if (popupRef.current && !popupRef.current.closed) {
      popupRef.current.close();
    }
  }, []);

  const handleConnect = async () => {
    setConnecting(true);
    try {
      const started = await apiService.startYandexCalendarAuth('/?ppTab=profile');
      popupRef.current = window.open(started.authUrl, 'pp-yandex-calendar-link', 'popup=yes,width=560,height=720');
      if (!popupRef.current) {
        window.location.assign(started.authUrl);
        return;
      }

      const interval = window.setInterval(async () => {
        try {
          const authStatus = await apiService.getYandexCalendarAuthStatus(started.state);
          if (!authStatus.completed) return;

          window.clearInterval(interval);
          if (popupRef.current && !popupRef.current.closed) {
            popupRef.current.close();
          }
          popupRef.current = null;
          setConnecting(false);

          if (authStatus.status === 'completed') {
            await loadStatus();
            toast.success('Яндекс.Календарь подключен.');
            return;
          }

          toast.error(authStatus.detail || 'Не удалось завершить подключение Яндекс.Календаря.');
        } catch (error) {
          window.clearInterval(interval);
          setConnecting(false);
          toast.error(extractDisplayErrorMessage(error, 'Не удалось проверить подключение Яндекс.Календаря.'));
        }
      }, started.pollIntervalMs || 2000);
    } catch (error) {
      setConnecting(false);
      toast.error(extractDisplayErrorMessage(error, 'Не удалось начать подключение Яндекс.Календаря.'));
    }
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      const next = await apiService.updateYandexCalendarSettings(form);
      setStatus(next);
      toast.success('Настройки Яндекс.Календаря сохранены.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить настройки Яндекс.Календаря.'));
    } finally {
      setSaving(false);
    }
  };

  const handleSync = async () => {
    setSyncing(true);
    try {
      const result = await apiService.syncYandexCalendarNow();
      setStatus(result.status);
      toast.success(result.message);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выполнить синхронизацию Яндекс.Календаря.'));
    } finally {
      setSyncing(false);
    }
  };

  const handleDisconnect = async () => {
    const approved = await confirm({
      title: 'Отключить Яндекс.Календарь?',
      description: 'CRM сначала удалит созданные PayPlanner календари и события в Яндекс.Календаре. Если очистка не пройдёт, подключение останется активным.',
      confirmLabel: 'Отключить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDisconnecting(true);
    try {
      const next = await apiService.disconnectYandexCalendar();
      setStatus(next);
      toast.success('Яндекс.Календарь отключен.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось отключить Яндекс.Календарь.'));
    } finally {
      setDisconnecting(false);
    }
  };

  if (loading) {
    return (
      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex items-center gap-2 text-sm text-slate-500">
          <Loader2 className="h-4 w-4 animate-spin" />
          Загружаем Яндекс.Календарь...
        </div>
      </div>
    );
  }

  const connection = status?.connection;
  const busy = connecting || saving || syncing || disconnecting;

  return (
    <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2 text-lg font-semibold text-slate-900">
            <CalendarCheck className="h-5 w-5 text-amber-500" />
            Яндекс.Календарь
          </div>
          <div className="mt-1 text-sm text-slate-500">
            Задачи CRM синхронизируются с Яндекс.Календарем текущего пользователя. Изменения из Google, Яндекса и CRM приводятся к одному состоянию через задачи CRM.
          </div>
        </div>
        <button
          type="button"
          onClick={() => void loadStatus()}
          disabled={busy}
          className="inline-flex h-10 w-10 items-center justify-center rounded-2xl border border-slate-300 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          title="Обновить статус"
          aria-label="Обновить статус"
        >
          <RefreshCw className="h-4 w-4" />
        </button>
      </div>

      {!status?.yandexOAuthReady ? (
        <div className="mt-5 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          OAuth Яндекса пока не настроен администратором. Callback URL: <span className="font-mono">{status?.callbackUrl}</span>
        </div>
      ) : null}

      {!connection ? (
        <div className="mt-5 flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-4">
          <div>
            <div className="font-semibold text-slate-900">Календарь не подключен</div>
            <div className="mt-1 text-sm text-slate-500">Подключите аккаунт Яндекса и разрешите доступ к календарю.</div>
          </div>
          <button
            type="button"
            onClick={() => void handleConnect()}
            disabled={!status?.yandexOAuthReady || connecting}
            className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {connecting ? <Loader2 className="h-4 w-4 animate-spin" /> : <CalendarCheck className="h-4 w-4" />}
            Подключить
          </button>
        </div>
      ) : (
        <div className="mt-5 space-y-5">
          <div className="grid gap-3 md:grid-cols-3">
            <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Аккаунт</div>
              <div className="mt-1 truncate text-sm font-semibold text-slate-900">{connection.yandexEmail || connection.displayName || 'Яндекс'}</div>
            </div>
            <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Последняя синхронизация</div>
              <div className="mt-1 text-sm font-semibold text-slate-900">{formatDateTime(connection.lastIncrementalSyncAt)}</div>
            </div>
            <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Календари</div>
              <div className="mt-1 text-sm font-semibold text-slate-900">{connection.calendars.length}</div>
            </div>
          </div>

          {connection.lastError ? (
            <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-900">{connection.lastError}</div>
          ) : null}

          <div className="grid gap-4 md:grid-cols-2">
            <label className="rounded-2xl border border-slate-200 px-4 py-3 text-sm">
              <span className="font-semibold text-slate-700">Подключение</span>
              <select
                value={form.isEnabled ? 'enabled' : 'disabled'}
                onChange={(event) => setForm((current) => ({ ...current, isEnabled: event.target.value === 'enabled' }))}
                className="mt-2 w-full rounded-xl border border-slate-300 px-3 py-2"
              >
                <option value="enabled">Включено</option>
                <option value="disabled">Отключено</option>
              </select>
            </label>
            <label className="rounded-2xl border border-slate-200 px-4 py-3 text-sm">
              <span className="font-semibold text-slate-700">Синхронизация</span>
              <select
                value={form.syncEnabled ? 'enabled' : 'disabled'}
                onChange={(event) => setForm((current) => ({ ...current, syncEnabled: event.target.value === 'enabled' }))}
                className="mt-2 w-full rounded-xl border border-slate-300 px-3 py-2"
              >
                <option value="enabled">Включена</option>
                <option value="disabled">Остановлена</option>
              </select>
            </label>
            {isAdmin ? (
              <label className="rounded-2xl border border-slate-200 px-4 py-3 text-sm">
                <span className="font-semibold text-slate-700">Режим администратора</span>
                <select
                  value={form.calendarMode}
                  onChange={(event) => setForm((current) => ({ ...current, calendarMode: event.target.value }))}
                  className="mt-2 w-full rounded-xl border border-slate-300 px-3 py-2"
                >
                  <option value="adminByAssignee">Отдельные календари сотрудников</option>
                  <option value="personal">Один календарь задач</option>
                </select>
              </label>
            ) : null}
            <label className="rounded-2xl border border-slate-200 px-4 py-3 text-sm">
              <span className="font-semibold text-slate-700">Правило конфликтов</span>
              <select
                value={form.conflictResolutionMode}
                onChange={(event) => setForm((current) => ({ ...current, conflictResolutionMode: event.target.value }))}
                className="mt-2 w-full rounded-xl border border-slate-300 px-3 py-2"
              >
                <option value="latestWins">Последнее изменение главное</option>
                <option value="crmWins">CRM главная</option>
                <option value="yandexWins">Яндекс.Календарь главный</option>
              </select>
            </label>
            <label className="rounded-2xl border border-slate-200 px-4 py-3 text-sm">
              <span className="font-semibold text-slate-700">Синхронизировать задачи с даты</span>
              <input
                type="date"
                value={form.syncFromDate ?? ''}
                onChange={(event) => setForm((current) => ({ ...current, syncFromDate: event.target.value || null }))}
                className="mt-2 w-full rounded-xl border border-slate-300 px-3 py-2"
              />
              <span className="mt-1 block text-xs text-slate-500">Пустое поле оставляет стандартный диапазон синхронизации.</span>
            </label>
          </div>

          <div className="flex flex-wrap justify-end gap-2">
            <button
              type="button"
              onClick={() => void handleDisconnect()}
              disabled={busy}
              className="inline-flex h-10 w-10 items-center justify-center rounded-2xl border border-rose-200 text-rose-600 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"
              title="Отключить Яндекс.Календарь"
              aria-label="Отключить Яндекс.Календарь"
            >
              {disconnecting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Unlink2 className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={() => void handleSync()}
              disabled={busy}
              className="inline-flex h-10 w-10 items-center justify-center rounded-2xl border border-blue-200 text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
              title="Синхронизировать сейчас"
              aria-label="Синхронизировать сейчас"
            >
              {syncing ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={busy}
              className="inline-flex h-10 w-10 items-center justify-center rounded-2xl bg-blue-600 text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
              title="Сохранить настройки"
              aria-label="Сохранить настройки"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            </button>
          </div>

          {connection.calendars.length > 0 ? (
            <div className="overflow-hidden rounded-2xl border border-slate-200">
              <div className="border-b border-slate-200 bg-slate-50 px-4 py-3 text-sm font-semibold text-slate-900">Календари</div>
              <div className="divide-y divide-slate-100">
                {connection.calendars.map((calendar) => (
                  <div key={calendar.id} className="grid gap-2 px-4 py-3 text-sm md:grid-cols-[minmax(0,1fr)_180px]">
                    <div className="min-w-0">
                      <div className="truncate font-semibold text-slate-800">{calendar.name}</div>
                      <div className="truncate text-xs text-slate-500">{calendar.yandexCalendarId}</div>
                    </div>
                    <div className="text-xs text-slate-500">{formatDateTime(calendar.lastSyncedAt)}</div>
                  </div>
                ))}
              </div>
            </div>
          ) : null}

          {status?.recentLogs.length ? (
            <div className="overflow-hidden rounded-2xl border border-slate-200">
              <div className="border-b border-slate-200 bg-slate-50 px-4 py-3 text-sm font-semibold text-slate-900">Последние события</div>
              <div className="divide-y divide-slate-100">
                {status.recentLogs.slice(0, 8).map((log) => (
                  <div key={log.id} className="grid gap-2 px-4 py-3 text-sm md:grid-cols-[150px_minmax(0,1fr)_120px]">
                    <div className="text-xs text-slate-500">{formatDateTime(log.createdAt)}</div>
                    <div className="min-w-0 truncate text-slate-800">{log.message}</div>
                    <div className={log.status === 'error' ? 'text-rose-600' : 'text-emerald-600'}>{log.status === 'error' ? 'Ошибка' : 'Успешно'}</div>
                  </div>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
