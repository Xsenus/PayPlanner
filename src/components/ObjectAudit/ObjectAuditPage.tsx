import { Filter, Loader2, RefreshCw, Search, ShieldCheck } from 'lucide-react';
import { Fragment, useEffect, useMemo, useState } from 'react';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import {
  apiService,
  type ObjectAuditEventDto,
} from '../../services/api';
import {
  formatAuditChangeSummary,
  formatAuditValue,
  formatObjectAuditText,
  getObjectAuditActorLabel,
  getObjectAuditActionLabel,
  getObjectAuditActionTone,
  getObjectAuditFieldLabel,
  getObjectAuditTypeLabel,
  parseAuditChangedFields,
  parseAuditFieldChanges,
} from '../../utils/objectAudit';
import { TablePagination } from '../ui/TablePagination';

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const OBJECT_TYPE_OPTIONS = [
  'all',
  'client',
  'client-case',
  'payment',
  'invoice',
  'invoice-line-item',
  'act',
  'contract',
  'legal-entity',
  'contact',
  'address',
  'client-passport',
  'power-of-attorney',
  'money-operation',
  'cash-order',
  'cash-inventory',
  'user',
  'role',
  'task-journal-item',
] as const;

const ACTION_OPTIONS = ['all', 'created', 'updated', 'deleted', 'restored', 'datarepair'] as const;

function formatDateTime(value?: string | null) {
  if (!value) {
    return 'Неизвестно';
  }

  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

function getEventDescription(item: ObjectAuditEventDto) {
  if (item.action?.toLowerCase() === 'updated') {
    const summary = formatAuditChangeSummary(item.objectType, item.metadataJson);
    if (summary) {
      return summary;
    }
  }

  return formatObjectAuditText(item.description) || 'Служебное событие без подробного описания.';
}

export function ObjectAuditPage() {
  const [items, setItems] = useState<ObjectAuditEventDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchInput, setSearchInput] = useState('');
  const [objectType, setObjectType] = useState<(typeof OBJECT_TYPE_OPTIONS)[number]>('all');
  const [action, setAction] = useState<(typeof ACTION_OPTIONS)[number]>('all');
  const [actor, setActor] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [total, setTotal] = useState(0);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const debouncedSearch = useDebouncedValue(searchInput, 400);
  const debouncedActor = useDebouncedValue(actor, 400);

  useEffect(() => {
    setPage(1);
  }, [action, debouncedActor, debouncedSearch, from, objectType, pageSize, to]);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      setLoading(true);
      setError(null);

      try {
        const response = await apiService.getObjectAuditEvents({
          search: debouncedSearch || undefined,
          objectType: objectType === 'all' ? undefined : objectType,
          action: action === 'all' ? undefined : action,
          actor: debouncedActor || undefined,
          from: from || undefined,
          to: to || undefined,
          page,
          pageSize,
        });

        if (cancelled) {
          return;
        }

        setItems(response.items ?? []);
        setTotal(response.total ?? 0);
        if (page > 1 && (response.items?.length ?? 0) === 0 && (response.total ?? 0) > 0) {
          setPage(1);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Не удалось загрузить журнал аудита.');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void load();

    return () => {
      cancelled = true;
    };
  }, [action, debouncedActor, debouncedSearch, from, objectType, page, pageSize, to]);

  const totalPages = useMemo(
    () => (pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1),
    [pageSize, total],
  );

  const hasActiveFilters = Boolean(
    searchInput.trim() ||
      objectType !== 'all' ||
      action !== 'all' ||
      actor.trim() ||
      from ||
      to,
  );

  const activeFilters = useMemo(() => {
    const result: string[] = [];
    if (searchInput.trim()) {
      result.push(`Поиск: ${searchInput.trim()}`);
    }
    if (objectType !== 'all') {
      result.push(`Объект: ${getObjectAuditTypeLabel(objectType)}`);
    }
    if (action !== 'all') {
      result.push(`Действие: ${getObjectAuditActionLabel(action)}`);
    }
    if (actor.trim()) {
      result.push(`Пользователь: ${actor.trim()}`);
    }
    if (from) {
      result.push(`С: ${from}`);
    }
    if (to) {
      result.push(`По: ${to}`);
    }
    return result;
  }, [action, actor, from, objectType, searchInput, to]);

  const resetFilters = () => {
    setSearchInput('');
    setObjectType('all');
    setAction('all');
    setActor('');
    setFrom('');
    setTo('');
  };

  const handleRefresh = async () => {
    setLoading(true);
    setError(null);

    try {
      const response = await apiService.getObjectAuditEvents({
        search: debouncedSearch || undefined,
        objectType: objectType === 'all' ? undefined : objectType,
        action: action === 'all' ? undefined : action,
        actor: debouncedActor || undefined,
        from: from || undefined,
        to: to || undefined,
        page,
        pageSize,
      });

      setItems(response.items ?? []);
      setTotal(response.total ?? 0);
    } catch (refreshError) {
      setError(refreshError instanceof Error ? refreshError.message : 'Не удалось обновить журнал аудита.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="p-6 lg:p-8">
      <div className="space-y-6">
        <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">
                Отдельная вкладка аудита
              </div>
              <h1 className="mt-3 text-3xl font-semibold text-slate-900">Журнал всех действий</h1>
              <p className="mt-2 max-w-3xl text-sm text-slate-600">
                Здесь собраны все зафиксированные изменения по объектам системы: создание, правки,
                удаление и восстановление.
              </p>
            </div>

            <div className="grid min-w-[280px] gap-3 sm:grid-cols-2">
              <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                  Событий в выборке
                </div>
                <div className="mt-2 text-2xl font-semibold text-slate-900">{total}</div>
              </div>
              <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                  Страница
                </div>
                <div className="mt-2 text-2xl font-semibold text-slate-900">
                  {page} / {totalPages}
                </div>
              </div>
            </div>
          </div>
        </section>

        <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <div className="inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                <Filter className="h-4 w-4" />
                Фильтры
              </div>
              <p className="mt-2 text-sm text-slate-600">
                Можно отобрать действия по типу объекта, пользователю, периоду и содержимому события.
              </p>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => void handleRefresh()}
                disabled={loading}
                className="inline-flex items-center gap-2 rounded-2xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60"
              >
                {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Обновить
              </button>

              {hasActiveFilters ? (
                <button
                  type="button"
                  onClick={resetFilters}
                  className="rounded-2xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
                >
                  Сбросить фильтры
                </button>
              ) : null}
            </div>
          </div>

          <div className="mt-5 grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Поиск</span>
              <div className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
                <Search className="h-4 w-4 text-slate-400" />
                <input
                  value={searchInput}
                  onChange={(event) => setSearchInput(event.target.value)}
                  placeholder="Описание, тип объекта, ID или текст события"
                  className="w-full bg-transparent text-sm text-slate-700 outline-none placeholder:text-slate-400"
                />
              </div>
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Объект</span>
              <select
                value={objectType}
                onChange={(event) => setObjectType(event.target.value as (typeof OBJECT_TYPE_OPTIONS)[number])}
                className="w-full rounded-2xl border border-slate-300 px-3 py-3 text-sm text-slate-700"
              >
                <option value="all">Все объекты</option>
                {OBJECT_TYPE_OPTIONS.filter((item) => item !== 'all').map((item) => (
                  <option key={item} value={item}>
                    {getObjectAuditTypeLabel(item)}
                  </option>
                ))}
              </select>
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Действие</span>
              <select
                value={action}
                onChange={(event) => setAction(event.target.value as (typeof ACTION_OPTIONS)[number])}
                className="w-full rounded-2xl border border-slate-300 px-3 py-3 text-sm text-slate-700"
              >
                <option value="all">Все действия</option>
                {ACTION_OPTIONS.filter((item) => item !== 'all').map((item) => (
                  <option key={item} value={item}>
                    {getObjectAuditActionLabel(item)}
                  </option>
                ))}
              </select>
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Пользователь</span>
              <input
                value={actor}
                onChange={(event) => setActor(event.target.value)}
                placeholder="Имя пользователя"
                className="w-full rounded-2xl border border-slate-300 px-3 py-3 text-sm text-slate-700"
              />
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Дата с</span>
              <input
                type="date"
                value={from}
                onChange={(event) => setFrom(event.target.value)}
                className="w-full rounded-2xl border border-slate-300 px-3 py-3 text-sm text-slate-700"
              />
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Дата по</span>
              <input
                type="date"
                value={to}
                onChange={(event) => setTo(event.target.value)}
                className="w-full rounded-2xl border border-slate-300 px-3 py-3 text-sm text-slate-700"
              />
            </label>
          </div>

          {activeFilters.length > 0 ? (
            <div className="mt-4 flex flex-wrap gap-2">
              {activeFilters.map((item) => (
                <span
                  key={item}
                  className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600"
                >
                  {item}
                </span>
              ))}
            </div>
          ) : null}
        </section>

        <section className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-200 px-6 py-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h2 className="text-lg font-semibold text-slate-900">События аудита</h2>
                <p className="mt-1 text-sm text-slate-500">
                  События показаны в таблице: основные сведения видны сразу, а изменения по полям раскрываются отдельной строкой.
                </p>
              </div>
              <div className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600">
                <ShieldCheck className="h-4 w-4" />
                Все действия в одном месте
              </div>
            </div>
          </div>

          {error ? (
            <div className="px-6 py-6">
              <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
                {error}
              </div>
            </div>
          ) : loading ? (
            <div className="px-6 py-10 text-center text-sm text-slate-500">
              <Loader2 className="mx-auto h-6 w-6 animate-spin text-slate-400" />
              <div className="mt-3">Загружаем журнал аудита...</div>
            </div>
          ) : items.length === 0 ? (
            <div className="px-6 py-10 text-center text-sm text-slate-500">
              По текущим фильтрам событий не найдено.
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full table-fixed divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50">
                  <tr>
                    <th className="w-[140px] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Действие
                    </th>
                    <th className="w-[150px] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Объект
                    </th>
                    <th className="w-[180px] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Пользователь
                    </th>
                    <th className="w-[160px] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Дата
                    </th>
                    <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Событие
                    </th>
                    <th className="w-[130px] px-4 py-3 text-right text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Детали
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                  {items.map((item) => {
                    const detailedChanges = parseAuditFieldChanges(item.metadataJson);
                    const changedFields = parseAuditChangedFields(item.metadataJson).map((fieldName) =>
                      getObjectAuditFieldLabel(item.objectType, fieldName),
                    );
                    const isExpanded = expandedId === item.id;
                    const canExpand = detailedChanges.length > 0;

                    return (
                      <Fragment key={item.id}>
                        <tr className="align-top transition hover:bg-slate-50/80">
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-semibold ${getObjectAuditActionTone(item.action)}`}
                            >
                              {getObjectAuditActionLabel(item.action)}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-slate-700">
                            <span className="font-medium text-slate-900">{getObjectAuditTypeLabel(item.objectType)}</span>
                            <span className="ml-1 text-slate-400">#{item.objectId}</span>
                          </td>
                          <td className="px-4 py-3 font-medium text-slate-900">
                            {getObjectAuditActorLabel(item.actorUserName)}
                          </td>
                          <td className="px-4 py-3 text-slate-500">{formatDateTime(item.createdAt)}</td>
                          <td className="px-4 py-3 text-slate-700">
                            <div className="line-clamp-2">{getEventDescription(item)}</div>
                            {changedFields.length > 0 && !canExpand ? (
                              <div className="mt-2 flex flex-wrap gap-1.5">
                                {changedFields.map((field) => (
                                  <span
                                    key={`${item.id}-${field}`}
                                    className="inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs font-medium text-slate-600"
                                  >
                                    {field}
                                  </span>
                                ))}
                              </div>
                            ) : null}
                          </td>
                          <td className="px-4 py-3 text-right">
                            {canExpand ? (
                              <button
                                type="button"
                                onClick={() => setExpandedId((current) => (current === item.id ? null : item.id))}
                                className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100"
                              >
                                {isExpanded ? 'Скрыть' : 'Показать'}
                              </button>
                            ) : (
                              <span className="text-xs text-slate-400">—</span>
                            )}
                          </td>
                        </tr>
                        {isExpanded ? (
                          <tr className="bg-slate-50/70">
                            <td colSpan={6} className="px-4 py-4">
                              <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
                                <table className="min-w-full divide-y divide-slate-100 text-sm">
                                  <thead className="bg-slate-50">
                                    <tr>
                                      <th className="w-[240px] px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                                        Поле
                                      </th>
                                      <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                                        Было
                                      </th>
                                      <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                                        Стало
                                      </th>
                                    </tr>
                                  </thead>
                                  <tbody className="divide-y divide-slate-100">
                                    {detailedChanges.map((change) => (
                                      <tr key={`${item.id}-${change.field}`}>
                                        <td className="px-4 py-3 font-medium text-slate-900">
                                          {getObjectAuditFieldLabel(item.objectType, change.field)}
                                        </td>
                                        <td className="px-4 py-3 text-slate-600">
                                          {formatAuditValue(change.oldValue, item.objectType, change.field)}
                                        </td>
                                        <td className="px-4 py-3 text-slate-900">
                                          {formatAuditValue(change.newValue, item.objectType, change.field)}
                                        </td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              </div>
                            </td>
                          </tr>
                        ) : null}
                      </Fragment>
                    );
                  })}
                </tbody>
              </table>
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
            className="border-t border-slate-200"
            recordsLabel="Событий найдено"
            pageSizeLabel="Событий на странице"
          />
        </section>
      </div>
    </div>
  );
}

export default ObjectAuditPage;
