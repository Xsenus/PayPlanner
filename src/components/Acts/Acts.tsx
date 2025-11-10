import {
  Ban,
  CalendarRange,
  CheckCircle2,
  FileCheck2,
  Loader2,
  Pencil,
  Plus,
  RotateCcw,
  Search,
  Send,
  Trash2,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useTranslation } from '../../hooks/useTranslation';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useActs, type ActsSortKey } from '../../hooks/useActs';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, ActStatus, Client } from '../../types';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { ActModal } from './ActModal';

const STATUS_ORDER: ActStatus[] = ['Terminated', 'Transferred', 'Signed'];

function formatMoney(value: number): string {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

type SortState = {
  key: ActsSortKey;
  direction: 'asc' | 'desc';
};

export function Acts() {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const actsPermissions = permissions.acts;
  const canCreate = actsPermissions.canCreate;
  const canEdit = actsPermissions.canEdit;
  const canDelete = actsPermissions.canDelete;

  const { t } = useTranslation();

  const today = useMemo(() => new Date(), []);
  const startOfYear = useMemo(
    () => formatLocalYMD(new Date(today.getFullYear(), 0, 1)),
    [today],
  );
  const todayYMD = useMemo(() => formatLocalYMD(today), [today]);

  const [from, setFrom] = useState<string>(startOfYear);
  const [to, setTo] = useState<string>(todayYMD);
  const [statusFilter, setStatusFilter] = useState<'all' | ActStatus>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const {
    acts,
    loading,
    refreshing,
    error,
    summary,
    summaryLoading,
    pagination,
    refresh,
    createAct,
    updateAct,
    deleteAct,
  } = useActs({
    from: from || undefined,
    to: to || undefined,
    status: statusFilter === 'all' ? undefined : statusFilter,
    search: debouncedSearch || undefined,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  const [modalOpen, setModalOpen] = useState(false);
  const [modalMode, setModalMode] = useState<'create' | 'edit'>('create');
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);

  const summaryBuckets = useMemo(
    () =>
      summary ?? {
        created: { amount: 0, count: 0 },
        transferred: { amount: 0, count: 0 },
        signed: { amount: 0, count: 0 },
        terminated: { amount: 0, count: 0 },
        totalAmount: 0,
        totalCount: 0,
      },
    [summary],
  );

  const statusLabels: Record<ActStatus, string> = {
    Created: t('actStatusCreated') ?? 'Создан',
    Transferred: t('actStatusTransferred') ?? 'Передано',
    Signed: t('actStatusSigned') ?? 'Подписано',
    Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
  };

  const statusClasses: Record<ActStatus, string> = {
    Created: 'bg-slate-100 text-slate-700 border border-slate-200',
    Transferred: 'bg-amber-100 text-amber-800 border border-amber-200',
    Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Terminated: 'bg-rose-100 text-rose-700 border border-rose-200',
  };

  const summaryCards = useMemo(
    () => [
      {
        key: 'terminated' as const,
        title: t('actSummaryTerminated') ?? 'Расторгнуто',
        bucket: summaryBuckets.terminated,
        icon: Ban,
        accent: 'text-rose-700 bg-rose-50 border border-rose-100',
      },
      {
        key: 'transferred' as const,
        title: t('actSummaryTransferred') ?? 'Передано',
        bucket: summaryBuckets.transferred,
        icon: Send,
        accent: 'text-amber-700 bg-amber-50 border border-amber-100',
      },
      {
        key: 'signed' as const,
        title: t('actSummarySigned') ?? 'Подписано',
        bucket: summaryBuckets.signed,
        icon: CheckCircle2,
        accent: 'text-emerald-700 bg-emerald-50 border border-emerald-100',
      },
    ],
    [summaryBuckets, t],
  );

  const totalPages = useMemo(() => {
    if (!pagination.pageSize) return 1;
    return Math.max(1, Math.ceil((pagination.total ?? 0) / pagination.pageSize));
  }, [pagination.pageSize, pagination.total]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, statusFilter, from, to]);

  const ensureLookups = useCallback(async () => {
    if (!modalOpen) return;
    if (clients.length > 0 && responsibles.length > 0 && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const [clientsResponse, responsibleResponse] = await Promise.all([
        apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      setClients(clientsResponse ?? []);
      setResponsibles(responsibleResponse ?? []);
      setLookupsError(null);
    } catch (err) {
      setLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить справочники');
    } finally {
      setLookupsLoading(false);
    }
  }, [clients.length, responsibles.length, modalOpen, lookupsError]);

  useEffect(() => {
    if (modalOpen) {
      void ensureLookups();
    }
  }, [modalOpen, ensureLookups]);

  const openCreateModal = () => {
    setModalMode('create');
    setSelectedAct(null);
    setModalOpen(true);
    setModalError(null);
  };

  const openEditModal = (act: Act) => {
    setModalMode('edit');
    setSelectedAct(act);
    setModalOpen(true);
    setModalError(null);
  };

  const closeModal = () => {
    if (submitting) return;
    setModalOpen(false);
    setSelectedAct(null);
    setModalError(null);
  };

  const handleSubmit = async (payload: ActInput) => {
    setSubmitting(true);
    setModalError(null);
    try {
      if (modalMode === 'create') {
        await createAct(payload);
      } else if (selectedAct) {
        await updateAct(selectedAct.id, payload);
      }
      setModalOpen(false);
      setSelectedAct(null);
    } catch (err) {
      setModalError(err instanceof Error ? err.message : 'Не удалось сохранить акт');
      throw err;
    } finally {
      setSubmitting(false);
    }
  };

  const handleDelete = async (act: Act) => {
    const confirmText = t('actDeleteConfirm') ?? 'Удалить акт?';
    if (!window.confirm(confirmText)) return;
    try {
      await deleteAct(act.id);
    } catch (err) {
      window.alert(err instanceof Error ? err.message : 'Не удалось удалить акт');
    }
  };

  const changeSort = (key: ActsSortKey) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' ? 'desc' : 'asc' };
    });
  };

  const handleResetFilters = () => {
    setFrom(startOfYear);
    setTo(todayYMD);
    setStatusFilter('all');
    setSearch('');
    setSortState({ key: 'date', direction: 'desc' });
    setPage(1);
  };

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="inline-flex h-12 w-12 items-center justify-center rounded-2xl bg-indigo-100 text-indigo-600">
                <FileCheck2 className="h-6 w-6" />
              </div>
              <div>
                <h1 className="text-2xl font-bold text-slate-900">{t('acts') ?? 'Акты'}</h1>
                <p className="text-sm text-slate-500">{t('actSectionDescription') ?? 'Управляйте актами и отслеживайте их статусы.'}</p>
              </div>
            </div>
          </div>
          {canCreate && (
            <button
              type="button"
              onClick={openCreateModal}
              className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-lg shadow-indigo-200 transition-colors hover:bg-indigo-500">
              <Plus className="h-4 w-4" />
              {t('addAct') ?? 'Добавить акт'}
            </button>
          )}
        </div>

        <div className="mb-6 grid gap-4 md:grid-cols-3">
          {summaryLoading ? (
            STATUS_ORDER.map((_, index) => (
              <div
                key={index}
                className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="h-4 w-28 animate-pulse rounded bg-slate-200" />
                <div className="mt-4 h-6 w-40 animate-pulse rounded bg-slate-200" />
                <div className="mt-3 h-4 w-24 animate-pulse rounded bg-slate-200" />
              </div>
            ))
          ) : (
            summaryCards.map((card) => {
              const Icon = card.icon;
              return (
                <div
                  key={card.key}
                  className={`rounded-2xl border bg-white p-5 shadow-sm transition hover:shadow-md ${card.accent}`}>
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium uppercase tracking-wide text-slate-500">
                      {card.title}
                    </span>
                    <Icon className="h-5 w-5" />
                  </div>
                  <div className="mt-3 text-2xl font-semibold">
                    {formatMoney(card.bucket.amount)}
                  </div>
                  <div className="mt-2 text-sm text-slate-600">
                    {t('actsSummaryCount') ?? 'Количество актов'}: {card.bucket.count}
                  </div>
                </div>
              );
            })
          )}
        </div>

        <div className="mb-6 flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm sm:flex-row sm:items-end sm:justify-between">
          <div className="grid w-full gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actFiltersPeriod') ?? 'Период с'}</span>
              <div className="relative">
                <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input
                  type="date"
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                  className="w-full rounded-lg border border-slate-300 px-3 py-2 pl-10 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                />
              </div>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actFiltersPeriodTo') ?? 'Период по'}</span>
              <div className="relative">
                <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input
                  type="date"
                  value={to}
                  onChange={(event) => setTo(event.target.value)}
                  className="w-full rounded-lg border border-slate-300 px-3 py-2 pl-10 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                />
              </div>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('status') ?? 'Статус'}</span>
              <select
                value={statusFilter}
                onChange={(event) => setStatusFilter(event.target.value === 'all' ? 'all' : (event.target.value as ActStatus))}
                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                {STATUS_ORDER.concat('Created' as ActStatus).map((status) => (
                  <option key={status} value={status}>
                    {statusLabels[status]}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="flex w-full flex-col gap-3 sm:w-auto sm:flex-1 sm:flex-row sm:items-end sm:justify-end">
            <div className="relative w-full sm:w-64">
              <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t('actSearchPlaceholder') ?? 'Номер акта, название или ИНН'}
                className="w-full rounded-lg border border-slate-300 px-3 py-2 pl-10 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleResetFilters}
                className="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-100">
                <RotateCcw className="h-4 w-4" />
                {t('actFiltersReset') ?? 'Сбросить'}
              </button>
              <button
                type="button"
                onClick={() => void refresh()}
                className="inline-flex items-center gap-1 rounded-lg border border-indigo-200 bg-indigo-50 px-3 py-2 text-sm font-medium text-indigo-600 transition hover:bg-indigo-100">
                {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <CalendarRange className="h-4 w-4" />}
                {t('actFiltersApply') ?? 'Обновить'}
              </button>
            </div>
          </div>
        </div>

        <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
          {error && (
            <div className="border-b border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          )}
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-200">
              <thead className="bg-slate-50">
                <tr>
                  {[
                    { key: 'date' as ActsSortKey, label: t('actDate') ?? 'Дата', sortable: true },
                    { key: 'number' as ActsSortKey, label: t('actNumber') ?? '№', sortable: true },
                    { key: 'amount' as ActsSortKey, label: t('actAmount') ?? 'Сумма', sortable: true },
                    { key: 'invoiceNumber' as ActsSortKey, label: t('actInvoice') ?? 'Счёт', sortable: true },
                    { key: 'client' as ActsSortKey, label: t('actClient') ?? 'Контрагент', sortable: true },
                    { key: 'counterpartyInn' as ActsSortKey, label: t('actInn') ?? 'ИНН', sortable: true },
                    { key: 'status' as ActsSortKey, label: t('actStatus') ?? 'Статус', sortable: true },
                    { key: 'responsible' as ActsSortKey, label: t('actResponsible') ?? 'Ответственный', sortable: true },
                    { key: 'actions' as ActsSortKey, label: t('actions') ?? 'Действия', sortable: false },
                  ]
                    .filter((column) =>
                      column.key !== 'actions' || canEdit || canDelete ? true : false,
                    )
                    .map((column) => {
                      const isSortable = column.sortable;
                      const isActive = sortState.key === column.key;
                      const direction = isActive ? sortState.direction : undefined;
                      return (
                        <th
                          key={column.key}
                          scope="col"
                          className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-600">
                          {isSortable ? (
                            <button
                              type="button"
                              onClick={() => changeSort(column.key)}
                              className="inline-flex items-center gap-1 text-slate-700">
                              <span>{column.label}</span>
                              {isActive && (
                                <span className="text-xs">
                                  {direction === 'asc' ? '▲' : '▼'}
                                </span>
                              )}
                            </button>
                          ) : (
                            <span>{column.label}</span>
                          )}
                        </th>
                      );
                    })}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 bg-white">
                {loading ? (
                  <tr>
                    <td colSpan={9} className="px-4 py-12 text-center text-slate-500">
                      <div className="flex flex-col items-center gap-3">
                        <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
                        <span>{t('loading') ?? 'Загрузка...'}...</span>
                      </div>
                    </td>
                  </tr>
                ) : acts.length === 0 ? (
                  <tr>
                    <td colSpan={9} className="px-4 py-12 text-center">
                      <div className="mx-auto max-w-md rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-6">
                        <h3 className="text-lg font-semibold text-slate-700">
                          {t('actsEmpty') ?? 'Акты не найдены'}
                        </h3>
                        <p className="mt-2 text-sm text-slate-500">
                          {t('actsEmptyHint') ?? 'Измените условия фильтра или добавьте новый акт.'}
                        </p>
                        {canCreate && (
                          <button
                            type="button"
                            onClick={openCreateModal}
                            className="mt-4 inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow hover:bg-indigo-500">
                            <Plus className="h-4 w-4" />
                            {t('addAct') ?? 'Добавить акт'}
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ) : (
                  acts.map((act) => (
                    <tr key={act.id} className="hover:bg-slate-50/80">
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">
                        {formatDate(act.date)}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm font-medium text-slate-900">
                        {act.number}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">
                        {formatMoney(act.amount || 0)}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {act.invoiceNumber || '—'}
                      </td>
                      <td className="px-4 py-3 text-sm text-slate-700">
                        <div className="flex flex-col">
                          <span className="font-medium text-slate-900">
                            {act.clientName || t('actClientUnknown') || 'Без контрагента'}
                          </span>
                          {act.title && (
                            <span className="text-xs text-slate-500">{act.title}</span>
                          )}
                        </div>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {act.counterpartyInn || '—'}
                      </td>
                      <td className="px-4 py-3">
                        <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${statusClasses[act.status]}`}>
                          {statusLabels[act.status]}
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">
                        {act.responsibleName || t('actResponsibleNotSelected') || '—'}
                      </td>
                      {(canEdit || canDelete) && (
                        <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-500">
                          <div className="flex items-center gap-2">
                            {canEdit && (
                              <button
                                type="button"
                                onClick={() => openEditModal(act)}
                                className="rounded-lg border border-slate-200 p-2 text-slate-500 transition hover:border-indigo-200 hover:text-indigo-600"
                                title={t('edit') ?? 'Редактировать'}>
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {canDelete && (
                              <button
                                type="button"
                                onClick={() => void handleDelete(act)}
                                className="rounded-lg border border-slate-200 p-2 text-slate-500 transition hover:border-rose-200 hover:text-rose-600"
                                title={t('delete') ?? 'Удалить'}>
                                <Trash2 className="h-4 w-4" />
                              </button>
                            )}
                          </div>
                        </td>
                      )}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          {acts.length > 0 && (
            <div className="flex flex-col items-center justify-between gap-4 border-t border-slate-200 px-4 py-4 text-sm text-slate-600 sm:flex-row">
              <div>
                {t('recordsFound') ?? 'Найдено записей:'} {pagination.total}
              </div>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((prev) => Math.max(1, prev - 1))}
                  disabled={page <= 1}
                  className="inline-flex items-center rounded-lg border border-slate-300 px-3 py-1 text-sm text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  ‹
                </button>
                <span>
                  {page} / {totalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
                  disabled={page >= totalPages}
                  className="inline-flex items-center rounded-lg border border-slate-300 px-3 py-1 text-sm text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                  ›
                </button>
              </div>
            </div>
          )}
        </div>
      </div>

      <ActModal
        open={modalOpen}
        mode={modalMode}
        act={selectedAct}
        onClose={closeModal}
        onSubmit={handleSubmit}
        submitting={submitting}
        errorMessage={modalError}
        clients={clients}
        responsibles={responsibles}
        lookupsLoading={lookupsLoading}
        lookupsError={lookupsError}
      />
    </div>
  );
}
