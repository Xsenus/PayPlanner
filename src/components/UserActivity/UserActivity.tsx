import { SystemSelect } from '../ui/SystemSelect';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Shield, RefreshCw, Search, Trash2, Filter, GripVertical } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { apiService } from '../../services/api';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import type {
  UserActivityActor,
  UserActivityFiltersResponse,
  UserActivityLogItem,
  UserActivityStatus,
} from '../../types/userActivity';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

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

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const USER_ACTIVITY_COLUMNS = [
  { key: 'createdAt', label: 'Когда', defaultVisible: true, defaultWidth: 190 },
  { key: 'user', label: 'Пользователь', defaultVisible: true, defaultWidth: 240 },
  { key: 'categoryAction', label: 'Категория / действие', defaultVisible: true, defaultWidth: 220 },
  { key: 'description', label: 'Описание', defaultVisible: true, defaultWidth: 320 },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 140 },
  { key: 'http', label: 'HTTP', defaultVisible: true, defaultWidth: 220 },
  { key: 'section', label: 'Раздел', defaultVisible: true, defaultWidth: 160 },
  { key: 'ipAddress', label: 'IP', defaultVisible: false, defaultWidth: 150 },
  { key: 'duration', label: 'Длит.', defaultVisible: true, defaultWidth: 110 },
] as const;

type UserActivityColumnKey = (typeof USER_ACTIVITY_COLUMNS)[number]['key'];

function formatDate(value: string, timeZoneId?: string | null) {
  return formatDateTimeInTimeZone(value, timeZoneId) || value;
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
  const { user, isAdmin } = useAuth();
  const timeZoneId = useUserTimeZone();
  const [logs, setLogs] = useState<UserActivityLogItem[]>([]);
  const [filters, setFilters] = useState<UserActivityFiltersResponse | null>(null);
  const [actors, setActors] = useState<UserActivityActor[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
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
  const [clearing, setClearing] = useState(false);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const { confirm } = useDialog();
  const toast = useToast();
  const {
    columnOrder,
    orderedVisibleColumns,
    visibleColumns,
    pageSize,
    setPageSize,
    draggingColumn,
    toggleColumn,
    moveColumn,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<UserActivityColumnKey>({
    storageKey: `pp.user_activity_table_settings.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'user_activity_table_settings',
    columns: USER_ACTIVITY_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 25,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 110,
  });

  const debouncedSearch = useDebouncedValue(search, 500);
  const activityQueryScopeKey = [
    debouncedSearch,
    status,
    category,
    action,
    section,
    method,
    actor,
    from,
    to,
    pageSize,
  ].join('\u001f');
  const previousActivityQueryScopeRef = useRef(activityQueryScopeKey);

  const hasActiveFilters = useMemo(
    () =>
      Boolean(
        search ||
          status !== 'all' ||
          category !== 'all' ||
          action !== 'all' ||
          section !== 'all' ||
          method !== 'all' ||
          actor !== 'all' ||
          from ||
          to,
      ),
    [action, actor, category, from, method, search, section, status, to],
  );

  const activeFiltersSummary = useMemo(() => {
    const items: string[] = [];
    if (search) items.push(`Поиск: "${search}"`);
    if (status !== 'all') items.push(`Статус: ${statusLabels[status as UserActivityStatus] ?? status}`);
    if (category !== 'all') items.push(`Категория: ${category}`);
    if (action !== 'all') items.push(`Действие: ${action}`);
    if (section !== 'all') items.push(`Раздел: ${section}`);
    if (method !== 'all') items.push(`Метод: ${method}`);
    if (actor !== 'all') items.push(`Пользователь: ${actor}`);
    if (from) items.push(`С: ${from}`);
    if (to) items.push(`По: ${to}`);
    return items;
  }, [action, actor, category, from, method, search, section, status, to]);

  const resetFilters = () => {
    setSearch('');
    setStatus('all');
    setCategory('all');
    setAction('all');
    setSection('all');
    setMethod('all');
    setActor('all');
    setFrom('');
    setTo('');
  };

  const handleClearLogs = async () => {
    if (!isAdmin()) return;

    const confirmDelete = await confirm({
      title: 'Подтверждение удаления',
      description: 'Удалить все записи пользовательского контроля? Для уменьшения файла SQLite после этого выполните уплотнение БД в настройках.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!confirmDelete) return;

    try {
      setClearing(true);
      const result = await apiService.deleteUserActivityLogs();
      setRefreshToken((token) => token + 1);
      toast.success(`История контроля очищена. Удалено записей: ${result.deletedLogs}.`, 'Журнал активности');
    } catch (error) {
      console.error('Не удалось очистить журнал контроля', error);
      const message = extractDisplayErrorMessage(
        error,
        'Не удалось очистить историю. Попробуйте позже или проверьте подключение.',
      );
      toast.error(message, 'Журнал активности');
    } finally {
      setClearing(false);
    }
  };

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
  }, [debouncedSearch, status, category, action, section, method, actor, from, to, pageSize]);

  useEffect(() => {
    if (!isAdmin()) {
      return undefined;
    }
    if (page !== 1 && previousActivityQueryScopeRef.current !== activityQueryScopeKey) {
      return undefined;
    }
    previousActivityQueryScopeRef.current = activityQueryScopeKey;
    const controller = new AbortController();

    const load = async () => {
      try {
        setLoading(true);
        setError(null);
        const response = await apiService.getUserActivityLogs(
          {
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
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;
        setLogs(response.items);
        setTotal(response.total);
        if (page > 1 && response.items.length === 0 && response.total > 0) {
          setPage(1);
        }
      } catch (err) {
        if (controller.signal.aborted) return;
        const message = err instanceof Error ? err.message : 'Не удалось загрузить активность пользователей';
        setError(message);
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => {
      controller.abort();
    };
  }, [actor, action, activityQueryScopeKey, category, debouncedSearch, from, isAdmin, method, page, pageSize, refreshToken, section, status, to]);

  const totalPages = useMemo(() => (pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1), [total, pageSize]);

  const activityColumnsByKey = useMemo(
    () =>
      Object.fromEntries(USER_ACTIVITY_COLUMNS.map((column) => [column.key, column])) as Record<
        UserActivityColumnKey,
        (typeof USER_ACTIVITY_COLUMNS)[number]
      >,
    [],
  );

  const visibleActivityColumns = useMemo(
    () => orderedVisibleColumns.map((key) => activityColumnsByKey[key]).filter(Boolean),
    [activityColumnsByKey, orderedVisibleColumns],
  );

  const renderCell = (log: UserActivityLogItem, columnKey: UserActivityColumnKey) => {
    const actorName = log.userFullName || log.userEmail || 'Система';
    const statusClass = statusStyles[log.status];

    switch (columnKey) {
      case 'createdAt':
        return <span className="whitespace-nowrap text-slate-700">{formatDate(log.createdAt, timeZoneId)}</span>;
      case 'user':
        return (
          <>
            <div className="font-medium text-slate-900">{actorName}</div>
            {log.userEmail ? <div className="text-xs text-slate-500">{log.userEmail}</div> : null}
          </>
        );
      case 'categoryAction':
        return (
          <>
            <div className="font-medium text-slate-800">{log.category}</div>
            <div className="text-xs text-slate-500">{log.action}</div>
          </>
        );
      case 'description':
        return (
          <>
            <div className="line-clamp-3 text-sm text-slate-700">{log.description || '—'}</div>
            {log.objectType ? (
              <div className="mt-1 text-xs text-slate-500">
                {log.objectType} {log.objectId ? `#${log.objectId}` : ''}
              </div>
            ) : null}
          </>
        );
      case 'status':
        return (
          <span className={`inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-semibold ${statusClass}`}>
            {statusLabels[log.status]}
          </span>
        );
      case 'http':
        return (
          <>
            <div className="font-mono text-xs text-slate-700">
              {log.httpMethod || '—'} {log.httpStatusCode ?? ''}
            </div>
            {log.path ? <div className="mt-1 line-clamp-2 text-xs text-slate-500">{log.path}</div> : null}
          </>
        );
      case 'section':
        return <span className="text-xs text-slate-600">{log.section || '—'}</span>;
      case 'ipAddress':
        return <span className="text-xs text-slate-600">{log.ipAddress || '—'}</span>;
      case 'duration':
        return (
          <span className="text-xs text-slate-600">
            {typeof log.durationMs === 'number' ? `${log.durationMs} мс` : '—'}
          </span>
        );
      default:
        return '—';
    }
  };

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
            <div className="flex items-center gap-3 flex-wrap justify-end">
              <div className="text-sm text-slate-500">
                Найдено записей: <span className="font-semibold text-slate-700">{total}</span>
              </div>
              <button
                type="button"
                onClick={() => setFiltersOpen((open) => !open)}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-slate-200 bg-white text-slate-700 hover:bg-slate-50 hover:border-slate-300 transition-colors"
              >
                <Filter className="w-4 h-4" />
                {filtersOpen ? 'Скрыть фильтры' : 'Показать фильтры'}
                {hasActiveFilters && (
                  <span className="ml-1 rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600">
                    {activeFiltersSummary.length}
                  </span>
                )}
              </button>
              <button
                type="button"
                onClick={() => setSettingsOpen((open) => !open)}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-slate-700 transition-colors hover:border-slate-300 hover:bg-slate-50"
              >
                <Filter className="w-4 h-4" />
                {settingsOpen ? 'Скрыть настройки таблицы' : 'Настроить таблицу'}
              </button>
              <button
                type="button"
                onClick={() => setRefreshToken((token) => token + 1)}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-slate-200 bg-white text-slate-700 hover:bg-slate-50 hover:border-slate-300 transition-colors"
              >
                <RefreshCw className="w-4 h-4" />
                Обновить
              </button>
              <button
                type="button"
                onClick={handleClearLogs}
                disabled={clearing}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-red-200 bg-red-50 text-red-700 hover:bg-red-100 hover:border-red-300 transition-colors disabled:opacity-60"
              >
                <Trash2 className="w-4 h-4" />
                {clearing ? 'Очищаем…' : 'Очистить историю'}
              </button>
            </div>
          </div>

          {filtersOpen && (
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
                  <SystemSelect
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
                  </SystemSelect>
                </label>

                <label>
                  <span className="text-sm font-medium text-slate-600">Категория</span>
                  <SystemSelect
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
                  </SystemSelect>
                </label>

                <label>
                  <span className="text-sm font-medium text-slate-600">Действие</span>
                  <SystemSelect
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
                  </SystemSelect>
                </label>

                <label>
                  <span className="text-sm font-medium text-slate-600">Раздел</span>
                  <SystemSelect
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
                  </SystemSelect>
                </label>

                <label>
                  <span className="text-sm font-medium text-slate-600">HTTP-метод</span>
                  <SystemSelect
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
                  </SystemSelect>
                </label>

                <label>
                  <span className="text-sm font-medium text-slate-600">Пользователь</span>
                  <SystemSelect
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
                  </SystemSelect>
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

              </div>

              <div className="mt-4 flex justify-end gap-2 text-sm">
                <button
                  type="button"
                  onClick={resetFilters}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                >
                  Сбросить фильтры
                </button>
                <button
                  type="button"
                  onClick={() => setFiltersOpen(false)}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                >
                  Свернуть
                </button>
              </div>
            </div>
          )}

          {!filtersOpen && hasActiveFilters && (
            <div className="bg-white border border-slate-200 rounded-xl shadow-sm p-4 flex flex-wrap items-center gap-2 text-sm text-slate-700">
              <span className="font-semibold text-slate-900">Активные фильтры:</span>
              {activeFiltersSummary.map((item) => (
                <span key={item} className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1">
                  {item}
                </span>
              ))}
              <button
                type="button"
                onClick={resetFilters}
                className="ml-auto inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-1.5 text-slate-700 hover:bg-slate-50"
              >
                Сбросить
              </button>
            </div>
          )}

          {settingsOpen && (
            <TableColumnSettings
              columns={USER_ACTIVITY_COLUMNS.map((column) => ({
                key: column.key,
                label: column.label,
              }))}
              columnOrder={columnOrder}
              visibleColumns={visibleColumns}
              draggingColumn={draggingColumn}
              onToggleColumn={toggleColumn}
              onMoveColumn={moveColumn}
              onDragStart={handleColumnDragStart}
              onDragOver={handleColumnDragOver}
              onDrop={handleColumnDrop}
              onDragEnd={handleColumnDragEnd}
              onReset={resetTableSettings}
              footer={
                <div className="flex items-center gap-2 text-sm text-slate-600">
                  <span>Строк на странице</span>
                  <SystemSelect
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
              }
            />
          )}

          {error && (
            <div className="p-4 bg-red-50 border border-red-200 text-red-700 rounded-lg">{error}</div>
          )}

          <div className="bg-white border border-slate-200 rounded-xl shadow-sm overflow-hidden">
            <div className="overflow-x-auto">
              <table
                className="min-w-[1280px] w-full table-fixed text-sm"
                style={{ width: getTableWidth(120, 1280), minWidth: '100%' }}
              >
                <colgroup>
                  {orderedVisibleColumns.map((columnKey) => (
                    <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                  ))}
                  <col style={{ width: 120 }} />
                </colgroup>
                <thead className="bg-slate-50 text-left text-slate-600 uppercase tracking-wide text-xs">
                  <tr>
                    {visibleActivityColumns.map((column) => (
                      <th
                        key={column.key}
                        style={{ width: getColumnWidth(column.key) }}
                        className={`relative px-4 py-3 font-semibold ${
                          draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : ''
                        }`}
                        onDragOver={handleColumnDragOver}
                        onDrop={(event) => handleColumnDrop(column.key, event)}>
                        <div
                          className="inline-flex min-w-0 select-none items-center gap-2 cursor-grab active:cursor-grabbing"
                          draggable
                          onDragStart={(event) => handleColumnDragStart(column.key, event)}
                          onDragEnd={handleColumnDragEnd}
                          title="Перетащите, чтобы изменить порядок">
                          <GripVertical className="h-4 w-4 text-slate-400" />
                          <span className="truncate">{column.label}</span>
                        </div>
                        <div
                          role="separator"
                          aria-label={`Изменить ширину колонки ${column.label}`}
                          className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                          onMouseDown={(event) => handleResizeStart(column.key, event)}
                          onDragStart={(event) => event.preventDefault()}
                        />
                      </th>
                    ))}
                    <th className="px-4 py-3 font-semibold text-right">Детали</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200">
                  {loading ? (
                    <tr>
                      <td
                        colSpan={visibleActivityColumns.length + 1}
                        className="px-4 py-6 text-center text-slate-500">
                        Загружаем действия пользователей…
                      </td>
                    </tr>
                  ) : logs.length === 0 ? (
                    <tr>
                      <td
                        colSpan={visibleActivityColumns.length + 1}
                        className="px-4 py-8 text-center text-slate-500">
                        Пока нет записей, удовлетворяющих условиям фильтра.
                      </td>
                    </tr>
                  ) : (
                    logs.map((log) => {
                      const isExpanded = expandedId === log.id;

                      return (
                        <tr key={log.id} className="hover:bg-slate-50 transition-colors">
                          {visibleActivityColumns.map((column) => (
                            <td
                              key={column.key}
                              style={{ width: getColumnWidth(column.key) }}
                              className="max-w-0 overflow-hidden px-4 py-3 align-top">
                              {renderCell(log, column.key)}
                            </td>
                          ))}
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

                <TablePagination
                  page={page}
                  totalPages={totalPages}
                  totalItems={total}
                  pageSize={pageSize}
                  pageSizeOptions={PAGE_SIZE_OPTIONS}
                  onPageChange={setPage}
                  onPageSizeChange={setPageSize}
                  recordsLabel="Найдено записей"
                  pageSizeLabel="Строк на странице"
                  previousLabel="Назад"
                  nextLabel="Вперед"
                />
              </div>
            )}
          </div>
        </div>
      </div>

    </div>
  );
};
