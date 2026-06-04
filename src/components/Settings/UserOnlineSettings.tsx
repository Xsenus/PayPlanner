import { useCallback, useEffect, useMemo, useState } from 'react';
import { RefreshCw, Users } from 'lucide-react';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { apiService } from '../../services/api';
import type { UserOnlineResponse, UserOnlineStatus } from '../../types/userActivity';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateInTimeZone, formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

const AUTO_REFRESH_MS = 10000;

const sectionLabels: Record<string, string> = {
  session: 'Активность в системе',
  auth: 'Авторизация',
  navigation: 'Навигация',
  settings: 'Настройки',
  'users-online': 'Пользователи онлайн',
  users: 'Пользователи',
  roles: 'Роли',
  clients: 'Клиенты',
  clientDetail: 'Карточка клиента',
  cases: 'Дела',
  caseDetail: 'Карточка дела',
  tasks: 'Журнал задач',
  tasksCalendar: 'Календарь задач',
  tasksApprovals: 'Согласования задач',
  calendar: 'Календарь',
  accounts: 'Счета',
  accountsIncome: 'Доходные счета',
  accountsExpense: 'Расходные счета',
  acts: 'Акты',
  contracts: 'Договоры',
  payments: 'Платежи',
  paymentsIncome: 'Доходные платежи',
  paymentsExpense: 'Расходные платежи',
  reports: 'Отчеты',
  dictionaries: 'Справочники',
  audit: 'Аудит объектов',
  userActivity: 'Контроль пользователей',
};

const actionLabels: Record<string, string> = {
  heartbeat: 'Активность',
  login: 'Вход',
  logout: 'Выход',
  open_tab: 'Открыт раздел',
  CreateAsync: 'Создание',
  GetAsync: 'Просмотр',
  UpdateAsync: 'Изменение',
  DeleteAsync: 'Удаление',
  CleanupAsync: 'Очистка',
};

function normalizeServerInstant(value?: string | null) {
  if (!value) return null;

  return /(?:z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`;
}

function parseServerDate(value?: string | null) {
  const normalized = normalizeServerInstant(value);
  if (!normalized) return null;

  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatDateTime(value?: string | null, timeZoneId?: string | null) {
  const date = parseServerDate(value);
  if (!date) return '—';

  return formatDateTimeInTimeZone(date, timeZoneId) || value || 'вЂ”';
}

function formatDate(value?: string | null, timeZoneId?: string | null) {
  const date = parseServerDate(value);
  if (!date) return '—';

  return formatDateInTimeZone(date, timeZoneId) || value || 'вЂ”';
}

function formatTime(value?: string | null, timeZoneId?: string | null) {
  const date = parseServerDate(value);
  if (!date) return '—';

  return formatDateTimeInTimeZone(date, timeZoneId).split(',').at(-1)?.trim() || value || 'вЂ”';
}

function formatRelativeActivity(value?: string | null) {
  const date = parseServerDate(value);
  if (!date) return 'не было активности';

  const diffMs = Date.now() - date.getTime();
  const diffMinutes = Math.max(0, Math.floor(diffMs / 60000));
  if (diffMinutes < 1) return 'только что';
  if (diffMinutes < 60) return `${diffMinutes} мин. назад`;

  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) return `${diffHours} ч. назад`;

  const diffDays = Math.floor(diffHours / 24);
  return `${diffDays} дн. назад`;
}

function translateValue(value?: string | null, labels: Record<string, string> = {}) {
  if (!value) return '—';

  const trimmed = value.trim();
  if (!trimmed) return '—';

  return labels[trimmed] ?? labels[trimmed.toLowerCase()] ?? trimmed;
}

function getUserStatusLabel(user: UserOnlineStatus) {
  if (user.isOnline) return 'Онлайн';
  if (!user.isActive) return 'Отключен';
  if (!user.isApproved) return 'Не одобрен';
  return 'Офлайн';
}

function getUserStatusClass(user: UserOnlineStatus) {
  if (user.isOnline) return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  if (!user.isActive || !user.isApproved) return 'border-slate-200 bg-slate-100 text-slate-600';
  return 'border-gray-200 bg-white text-gray-600';
}

export function UserOnlineSettings() {
  const timeZoneId = useUserTimeZone();
  const [data, setData] = useState<UserOnlineResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (silent = false) => {
    if (silent) {
      setRefreshing(true);
    } else {
      setLoading(true);
    }
    setError(null);

    try {
      await apiService.sendUserActivityHeartbeat();
      const response = await apiService.getUsersOnline();
      setData(response);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить пользователей онлайн.'));
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void load();
    const intervalId = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        void load(true);
      }
    }, AUTO_REFRESH_MS);
    return () => window.clearInterval(intervalId);
  }, [load]);

  const users = useMemo(() => data?.users ?? [], [data?.users]);
  const inactiveCount = useMemo(
    () => users.filter((user) => !user.isActive || !user.isApproved).length,
    [users],
  );

  return (
    <div className="space-y-6">
      <div className="rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-emerald-50 text-emerald-700">
                <Users className="h-5 w-5" />
              </div>
              <div>
                <h4 className="text-base font-semibold text-gray-900">Пользователи онлайн</h4>
                <p className="mt-1 text-sm text-gray-600">
                  Пользователь считается онлайн, если за последние {data?.onlineThresholdMinutes ?? 5} минут была активность и последним действием не был выход.
                </p>
              </div>
            </div>
          </div>
          <button
            type="button"
            onClick={() => void load(true)}
            disabled={loading || refreshing}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>

        <div className="mt-6 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          <div className="flex min-h-[112px] flex-col items-center justify-center rounded-xl border border-emerald-100 bg-emerald-50 p-4 text-center">
            <div className="text-xs font-semibold uppercase tracking-wide text-emerald-700">Сейчас онлайн</div>
            <div className="mt-2 text-2xl font-semibold text-emerald-800">{data?.onlineUsers ?? 0}</div>
          </div>
          <div className="flex min-h-[112px] flex-col items-center justify-center rounded-xl border border-gray-200 bg-gray-50 p-4 text-center">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Всего пользователей</div>
            <div className="mt-2 text-2xl font-semibold text-gray-900">{data?.totalUsers ?? 0}</div>
          </div>
          <div className="flex min-h-[112px] flex-col items-center justify-center rounded-xl border border-slate-200 bg-white p-4 text-center">
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Отключены / не одобрены</div>
            <div className="mt-2 text-2xl font-semibold text-slate-800">{inactiveCount}</div>
          </div>
          <div className="flex min-h-[112px] flex-col items-center justify-center rounded-xl border border-blue-100 bg-blue-50 p-4 text-center">
            <div className="text-xs font-semibold uppercase tracking-wide text-blue-700">Обновлено</div>
            <div className="mt-2 text-lg font-semibold text-blue-900">{formatDate(data?.generatedAt, timeZoneId)}</div>
            <div className="mt-1 text-sm font-medium text-blue-700">{formatTime(data?.generatedAt, timeZoneId)}</div>
          </div>
        </div>
      </div>

      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      <div className="overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-sm">
        <div className={`overflow-x-auto transition-opacity duration-300 ${refreshing ? 'opacity-70' : 'opacity-100'}`}>
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">Пользователь</th>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">Статус</th>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">Последняя активность</th>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">Последний вход</th>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">Раздел / действие</th>
                <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">IP</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {loading && (
                <tr>
                  <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">
                    Загружаем данные...
                  </td>
                </tr>
              )}

              {!loading && users.length === 0 && (
                <tr>
                  <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">
                    Пользователи не найдены.
                  </td>
                </tr>
              )}

              {!loading && users.map((user) => (
                <tr key={user.userId} className={user.isOnline ? 'bg-emerald-50/30' : undefined}>
                  <td className="px-4 py-3 text-sm">
                    <div className="font-semibold text-gray-900">{user.fullName || user.email}</div>
                    <div className="mt-1 text-xs text-gray-500">{user.email}</div>
                    <div className="mt-1 text-xs text-gray-500">
                      {[user.roleName, user.department].filter(Boolean).join(' · ') || 'Роль не указана'}
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm">
                    <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${getUserStatusClass(user)}`}>
                      {getUserStatusLabel(user)}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-sm text-gray-700">
                    <div>{formatRelativeActivity(user.lastActivityAt)}</div>
                    <div className="mt-1 text-xs text-gray-500">{formatDateTime(user.lastActivityAt, timeZoneId)}</div>
                  </td>
                  <td className="px-4 py-3 text-sm text-gray-700">{formatDateTime(user.lastLoginAt, timeZoneId)}</td>
                  <td className="px-4 py-3 text-sm text-gray-700">
                    <div>{translateValue(user.lastSection, sectionLabels)}</div>
                    <div className="mt-1 text-xs text-gray-500">{translateValue(user.lastAction, actionLabels)}</div>
                  </td>
                  <td className="px-4 py-3 text-sm text-gray-700">{user.lastIpAddress || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
