import { useEffect, useMemo, useState } from 'react';
import { Shield, RefreshCw, ChevronLeft, ChevronRight, Search } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { apiService } from '../../services/api';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import type {
  UserActivityActor,
  UserActivityFiltersResponse,
  UserActivityLogItem,
  UserActivityStatus,
} from '../../types/userActivity';

const statusStyles: Record<UserActivityStatus, string> = {
  Info: 'bg-slate-100 text-slate-700 border border-slate-200',
  Success: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  Warning: 'bg-amber-100 text-amber-700 border border-amber-200',
  Failure: 'bg-red-100 text-red-700 border border-red-200',
};

const statusLabels: Record<UserActivityStatus, string> = {
  Info: 'Инфо',
  Success: 'Успех',
  Warning: 'Предупреждение',
  Failure: 'Ошибка',
};

function formatDate(value: string) {
  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

function formatMetadata(metadata: unknown): string {
  if (metadata === null || metadata === undefined) {
    return '';
  }
  if (typeof metadata === 'string') {
    return metadata;
  }
  try {
    return JSON.stringify(metadata, null, 2);
  } catch (error) {
    console.warn('Не удалось преобразовать metadata', error);
    return String(metadata);
  }
}

export const UserActivity = () => {
  const { isAdmin } = useAuth();
  const [logs, setLogs] = useState<UserActivityLogItem[]>([]);
  const [filters, setFilters] = useState<UserActivityFiltersResponse | null>(null);
  const [actors, setActors] = useState<UserActivityActor[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<UserActivityStatus | 'all'>('all');
  const [category, setCategory] = useState<string>('all');
  const [action, setAction] = useState<string>('all');
  const [section, setSection] = useState<string>('all');
  const [method, setMethod] = useState<string>('all');
  const [actor, setActor] = useState<number | 'all'>('all');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [refreshToken, setRefreshToken] = useState(0);
  const [expandedId, setExpandedId] = useState<number | null>(null);

  const debouncedSearch = useDebouncedValue(search, 500);

  useEffect(() => {
    if (!isAdmin()) {
      return;
    }

    const loadFilters = async () => {
      try {
        const data = await apiService.getUserActivityFilters();
        setFilters(data);
        setActors(data.actors);
      } catch (err) {
        console.error('Не удалось загрузить фильтры активности', err);
      }
    };

    void loadFilters();
  }, [isAdmin]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, status, category, action, section, method, actor, from, to]);

  useEffect(() => {
    if (!isAdmin()) {
      return;
    }

    const load = async () => {
      try {
        setLoading(true);
        setError(null);
        const response = await apiService.getUserActivityLogs({
          page,
          pageSize,
          search: debouncedSearch || undefined,
          status: status === 'all' ? undefined : status,
          category: category === 'all' ? undefined : category,
          action: action === 'all' ? undefined : action,
          section: section === 'all' ? undefined : section,
          httpMethod: method === 'all' ? undefined : method,
          userId: actor === 'all' ? undefined : actor,
          from: from || undefined,
          to: to || undefined,
        });
        setLogs(response.items);
        setTotal(response.total);
        if (page > 1 && response.items.length === 0 && response.total > 0) {
          setPage(1);
        }
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Не удалось загрузить активность пользователей';
        setError(message);
      } finally {
        setLoading(false);
      }
    };

    void load();
  }, [actor, action, category, debouncedSearch, from, isAdmin, method, page, pageSize, refreshToken, section, status, to]);

  const totalPages = useMemo(() => (pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1), [total, pageSize]);

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="max-w-4xl mx-auto">
          <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
            <Shield className="w-12 h-12 text-red-600 mx-auto mb-4" />
            <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
            <p className="text-red-700">Только администраторы могут просматривать журнал пользовательской активности.</p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-8">
      <div className="max-w-[calc(100vw-2rem)] mx-auto">
        <div className="flex flex-col gap-6">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div>
              <h1 className="text-3xl font-bold text-slate-900">Пользовательский контроль</h1>
              <p className="text-slate-600 mt-2">
                Анализируйте действия пользователей, входы в разделы и системные события в режиме реального времени.
              </p>
            </div>
            <div className="flex items-center gap-3">
              <div className="text-sm text-slate-500">
                Найдено записей: <span className="font-semibold text-slate-700">{total}</span>
              </div>
              <button
                type="button"
                onClick={() => setRefreshToken((token) => token + 1)}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-slate-200 bg-white text-slate-700 hover:bg-slate-50 hover:border-slate-300 transition-colors"
              >
                <RefreshCw className="w-4 h-4" />
                Обновить
              </button>
            </div>
          </div>

          <div className="bg-white border border-slate-200 rounded-xl shadow-sm p-4">
            <div className="grid grid-cols-1 lg:grid-cols-4 gap-4">
              <label className="col-span-1 lg:col-span-2">
                <span className="text-sm font-medium text-slate-600 flex items-center gap-2">
                  <Search className="w-4 h-4" /> Поиск
                </span>
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder="Описание, объект, раздел"
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                />
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Статус</span>
                <select
                  value={status}
                  onChange={(event) => setStatus(event.target.value as UserActivityStatus | 'all')}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {filters?.statuses.map((item) => (
                    <option key={item} value={item}>
                      {statusLabels[item]}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Категория</span>
                <select
                  value={category}
                  onChange={(event) => setCategory(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {filters?.categories.map((item) => (
                    <option key={item} value={item}>
                      {item}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Действие</span>
                <select
                  value={action}
                  onChange={(event) => setAction(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {filters?.actions.map((item) => (
                    <option key={item} value={item}>
                      {item}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Раздел</span>
                <select
                  value={section}
                  onChange={(event) => setSection(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {filters?.sections.map((item) => (
                    <option key={item} value={item}>
                      {item}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">HTTP-метод</span>
                <select
                  value={method}
                  onChange={(event) => setMethod(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {filters?.httpMethods.map((item) => (
                    <option key={item} value={item}>
                      {item}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Пользователь</span>
                <select
                  value={actor}
                  onChange={(event) =>
                    setActor(event.target.value === 'all' ? 'all' : Number.parseInt(event.target.value, 10) || 'all')
                  }
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  <option value="all">Все</option>
                  {actors.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.fullName || item.email || `ID ${item.id}`}
                    </option>
                  ))}
                </select>
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Дата с</span>
                <input
                  type="datetime-local"
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                />
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">Дата по</span>
                <input
                  type="datetime-local"
                  value={to}
                  onChange={(event) => setTo(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                />
              </label>

              <label>
                <span className="text-sm font-medium text-slate-600">На странице</span>
                <select
                  value={pageSize}
                  onChange={(event) => setPageSize(Number.parseInt(event.target.value, 10) || 25)}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-slate-500/60 focus:border-slate-400"
                >
                  {[10, 25, 50, 100, 150, 200].map((value) => (
                    <option key={value} value={value}>
                      {value}
                    </option>
                  ))}
                </select>
              </label>
            </div>
          </div>

          {error && (
            <div className="p-4 bg-red-50 border border-red-200 text-red-700 rounded-lg">{error}</div>
          )}

          <div className="bg-white border border-slate-200 rounded-xl shadow-sm overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-slate-50 text-left text-slate-600 uppercase tracking-wide text-xs">
                  <tr>
                    <th className="px-4 py-3 font-semibold">Когда</th>
                    <th className="px-4 py-3 font-semibold">Пользователь</th>
                    <th className="px-4 py-3 font-semibold">Категория / действие</th>
                    <th className="px-4 py-3 font-semibold">Описание</th>
                    <th className="px-4 py-3 font-semibold">Статус</th>
                    <th className="px-4 py-3 font-semibold">HTTP</th>
                    <th className="px-4 py-3 font-semibold">Раздел</th>
                    <th className="px-4 py-3 font-semibold">IP</th>
                    <th className="px-4 py-3 font-semibold">Длит.</th>
                    <th className="px-4 py-3 font-semibold text-right">Детали</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200">
                  {loading ? (
                    <tr>
                      <td colSpan={10} className="px-4 py-6 text-center text-slate-500">
                        Загружаем действия пользователей…
                      </td>
                    </tr>
                  ) : logs.length === 0 ? (
                    <tr>
                      <td colSpan={10} className="px-4 py-8 text-center text-slate-500">
                        Пока нет записей, удовлетворяющих условиям фильтра.
                      </td>
                    </tr>
                  ) : (
                    logs.map((log) => {
                      const actorName = log.userFullName || log.userEmail || 'Система';
                      const statusClass = statusStyles[log.status];
                      const isExpanded = expandedId === log.id;

                      return (
                        <tr key={log.id} className="hover:bg-slate-50 transition-colors">
                          <td className="px-4 py-3 align-top text-slate-700 whitespace-nowrap">{formatDate(log.createdAt)}</td>
                          <td className="px-4 py-3 align-top">
                            <div className="font-medium text-slate-900">{actorName}</div>
                            {log.userEmail && <div className="text-xs text-slate-500">{log.userEmail}</div>}
                          </td>
                          <td className="px-4 py-3 align-top">
                            <div className="font-medium text-slate-800">{log.category}</div>
                            <div className="text-xs text-slate-500">{log.action}</div>
                          </td>
                          <td className="px-4 py-3 align-top max-w-xs">
                            <div className="text-sm text-slate-700 line-clamp-3">{log.description || '—'}</div>
                            {log.objectType && (
                              <div className="text-xs text-slate-500 mt-1">
                                {log.objectType} {log.objectId ? `#${log.objectId}` : ''}
                              </div>
                            )}
                          </td>
                          <td className="px-4 py-3 align-top">
                            <span className={`inline-flex items-center gap-1 px-2 py-1 rounded-full text-xs font-semibold ${statusClass}`}>
                              {statusLabels[log.status]}
                            </span>
                          </td>
                          <td className="px-4 py-3 align-top">
                            <div className="font-mono text-xs text-slate-700">
                              {log.httpMethod || '—'} {log.httpStatusCode ?? ''}
                            </div>
                            {log.path && <div className="text-xs text-slate-500 mt-1 line-clamp-2">{log.path}</div>}
                          </td>
                          <td className="px-4 py-3 align-top text-xs text-slate-600">
                            {log.section || '—'}
                          </td>
                          <td className="px-4 py-3 align-top text-xs text-slate-600">
                            {log.ipAddress || '—'}
                          </td>
                          <td className="px-4 py-3 align-top text-xs text-slate-600">
                            {typeof log.durationMs === 'number' ? `${log.durationMs} мс` : '—'}
                          </td>
                          <td className="px-4 py-3 align-top text-right">
                            <button
                              type="button"
                              onClick={() => setExpandedId(isExpanded ? null : log.id)}
                              className="text-slate-600 hover:text-slate-900 text-xs font-medium"
                            >
                              {isExpanded ? 'Скрыть' : 'Подробнее'}
                            </button>
                          </td>
                        </tr>
                      );
                    })
                  )}
                </tbody>
              </table>
            </div>

            {logs.length > 0 && (
              <div className="border-t border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600 flex flex-col gap-3">
                {expandedId !== null && (
                  <div className="bg-white border border-slate-200 rounded-lg p-4 shadow-sm">
                    {(() => {
                      const current = logs.find((log) => log.id === expandedId);
                      if (!current) return null;
                      const metadataText = formatMetadata(current.metadata);
                      return (
                        <div className="space-y-3">
                          <div>
                            <div className="text-xs uppercase text-slate-500 font-semibold">Маршрут</div>
                            <div className="font-mono text-xs text-slate-700 break-all">
                              {current.httpMethod || '—'} {current.path || ''}
                              {current.queryString}
                            </div>
                          </div>
                          <div>
                            <div className="text-xs uppercase text-slate-500 font-semibold">Описание</div>
                            <div className="text-sm text-slate-700 whitespace-pre-wrap">
                              {current.description || '—'}
                            </div>
                          </div>
                          <div>
                            <div className="text-xs uppercase text-slate-500 font-semibold">Метаданные</div>
                            {metadataText ? (
                              <pre className="mt-1 bg-slate-900 text-slate-100 rounded-lg p-3 text-xs overflow-x-auto whitespace-pre-wrap">
                                {metadataText}
                              </pre>
                            ) : (
                              <div className="text-sm text-slate-500">Метаданные отсутствуют</div>
                            )}
                          </div>
                        </div>
                      );
                    })()}
                  </div>
                )}

                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    Страница {page} из {totalPages}
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => setPage((prev) => Math.max(1, prev - 1))}
                      disabled={page <= 1}
                      className="inline-flex items-center justify-center rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-100 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      <ChevronLeft className="w-4 h-4" />
                      Назад
                    </button>
                    <button
                      type="button"
                      onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
                      disabled={page >= totalPages}
                      className="inline-flex items-center justify-center rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-100 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      Вперёд
                      <ChevronRight className="w-4 h-4" />
                    </button>
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
