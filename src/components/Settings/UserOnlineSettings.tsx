import { useCallback, useEffect, useMemo, useState } from 'react';
import { RefreshCw, Users } from 'lucide-react';
import { apiService } from '../../services/api';
import type { UserOnlineResponse, UserOnlineStatus } from '../../types/userActivity';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

function formatDateTime(value?: string | null) {
  if (!value) return '—';

  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

function formatRelativeActivity(value?: string | null) {
  if (!value) return 'не было активности';

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  const diffMs = Date.now() - date.getTime();
  const diffMinutes = Math.max(0, Math.floor(diffMs / 60000));
  if (diffMinutes < 1) return 'только что';
  if (diffMinutes < 60) return `${diffMinutes} мин. назад`;

  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) return `${diffHours} ч. назад`;

  const diffDays = Math.floor(diffHours / 24);
  return `${diffDays} дн. назад`;
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
    const intervalId = window.setInterval(() => void load(true), 30000);
    return () => window.clearInterval(intervalId);
  }, [load]);

  const users = data?.users ?? [];
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

        <div className="mt-6 grid gap-4 md:grid-cols-3">
          <div className="rounded-xl border border-emerald-100 bg-emerald-50 p-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-emerald-700">Сейчас онлайн</div>
            <div className="mt-2 text-2xl font-semibold text-emerald-800">{data?.onlineUsers ?? 0}</div>
          </div>
          <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Всего пользователей</div>
            <div className="mt-2 text-2xl font-semibold text-gray-900">{data?.totalUsers ?? 0}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Отключены / не одобрены</div>
            <div className="mt-2 text-2xl font-semibold text-slate-800">{inactiveCount}</div>
          </div>
        </div>

        <div className="mt-4 text-xs text-gray-500">
          Обновлено: {formatDateTime(data?.generatedAt)}
        </div>
      </div>

      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      <div className="overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-sm">
        <div className="overflow-x-auto">
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
                    <div className="mt-1 text-xs text-gray-500">{formatDateTime(user.lastActivityAt)}</div>
                  </td>
                  <td className="px-4 py-3 text-sm text-gray-700">{formatDateTime(user.lastLoginAt)}</td>
                  <td className="px-4 py-3 text-sm text-gray-700">
                    <div>{user.lastSection || '—'}</div>
                    <div className="mt-1 text-xs text-gray-500">{user.lastAction || '—'}</div>
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
