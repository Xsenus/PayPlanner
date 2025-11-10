import {
  CalendarRange,
  FileSignature,
  Loader2,
  Pencil,
  Plus,
  RefreshCcw,
  Search,
  Trash2,
  Users as UsersIcon,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { useContracts, type ContractsSortKey } from '../../hooks/useContracts';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { ClientLookup, Contract, ContractInput } from '../../types';
import { toDateInputValue, toRuDate } from '../../utils/dateUtils';
import { ContractModal } from './ContractModal';

type SortState = {
  key: ContractsSortKey;
  direction: 'asc' | 'desc';
};

function formatMoney(value?: number | null): string {
  if (value === undefined || value === null) {
    return '—';
  }
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const formatted = toRuDate(value);
  return formatted || value;
}

export function Contracts() {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const contractsPermissions = permissions.contracts;
  const canCreate = contractsPermissions.canCreate;
  const canEdit = contractsPermissions.canEdit;
  const canDelete = contractsPermissions.canDelete;

  const { t } = useTranslation();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [clientFilter, setClientFilter] = useState<string>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const clientIdFilter = useMemo(() => {
    if (clientFilter === 'all') return undefined;
    const parsed = Number(clientFilter);
    return Number.isNaN(parsed) ? undefined : parsed;
  }, [clientFilter]);

  const {
    contracts,
    loading,
    refreshing,
    error,
    pagination,
    refresh,
    createContract,
    updateContract,
    deleteContract,
  } = useContracts({
    from: from || undefined,
    to: to || undefined,
    clientId: clientIdFilter,
    search: debouncedSearch || undefined,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  const [clients, setClients] = useState<ClientLookup[]>([]);
  const [clientsLoading, setClientsLoading] = useState(false);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [modalOpen, setModalOpen] = useState(false);
  const [modalMode, setModalMode] = useState<'create' | 'edit'>('create');
  const [selectedContract, setSelectedContract] = useState<Contract | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);

  const totalPages = useMemo(() => {
    if (!pagination.pageSize) return 1;
    return Math.max(1, Math.ceil((pagination.total ?? 0) / pagination.pageSize));
  }, [pagination.pageSize, pagination.total]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, from, to, clientIdFilter, sortState.key, sortState.direction]);

  const loadClients = useCallback(async () => {
    setClientsLoading(true);
    try {
      const data = await apiService.lookupClients({ includeInactive: true, limit: 200 });
      setClients(data ?? []);
      setClientsError(null);
    } catch (err) {
      setClientsError(
        err instanceof Error ? err.message : (t('contractsClientsLoadError') ?? 'Не удалось загрузить клиентов'),
      );
    } finally {
      setClientsLoading(false);
    }
  }, [t]);

  useEffect(() => {
    void loadClients();
  }, [loadClients]);

  const openCreateModal = () => {
    if (!canCreate) return;
    setModalMode('create');
    setSelectedContract(null);
    setModalOpen(true);
    setModalError(null);
  };

  const openEditModal = (contract: Contract) => {
    if (!canEdit) return;
    setModalMode('edit');
    setSelectedContract(contract);
    setModalOpen(true);
    setModalError(null);
  };

  const handleSubmit = async (payload: ContractInput) => {
    setSubmitting(true);
    setModalError(null);
    setActionError(null);
    try {
      if (modalMode === 'create') {
        await createContract(payload);
      } else if (selectedContract) {
        await updateContract(selectedContract.id, payload);
      }
      setModalOpen(false);
    } catch (err) {
      const message = err instanceof Error ? err.message : (t('failedToSave') ?? 'Не удалось сохранить');
      setModalError(message);
    } finally {
      setSubmitting(false);
    }
  };

  const handleDelete = async (contract: Contract) => {
    if (!canDelete) return;
    const confirmMessage =
      t('contractDeleteConfirm')?.replace('{{number}}', contract.number ?? '') ??
      `Удалить договор «${contract.number}»?`;
    if (!confirm(confirmMessage)) return;

    setDeletingId(contract.id);
    setActionError(null);
    try {
      await deleteContract(contract.id);
    } catch (err) {
      const message = err instanceof Error ? err.message : (t('failedToDelete') ?? 'Не удалось удалить');
      setActionError(message);
    } finally {
      setDeletingId(null);
    }
  };

  const changeSort = (key: ContractsSortKey) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' ? 'desc' : 'asc' };
    });
  };

  const renderSortIndicator = (key: ContractsSortKey) => {
    if (sortState.key !== key) return <span className="text-slate-300">↕</span>;
    return sortState.direction === 'asc' ? <span>↑</span> : <span>↓</span>;
  };

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
          <div>
            <div className="flex items-center gap-3 text-slate-900">
              <FileSignature className="h-9 w-9 text-emerald-500" />
              <div>
                <h1 className="text-2xl font-bold">
                  {t('contracts') ?? 'Договоры'}
                </h1>
                <p className="text-sm text-slate-600">
                  {t('contractsSubtitle') ?? 'Управляйте договорами и связями с клиентами.'}
                </p>
              </div>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => {
                setActionError(null);
                void refresh();
              }}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
              disabled={loading || refreshing}
            >
              {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCcw className="h-4 w-4" />}
              <span>{t('update') ?? 'Обновить'}</span>
            </button>
            {canCreate && (
              <button
                type="button"
                onClick={openCreateModal}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-emerald-500"
              >
                <Plus className="h-4 w-4" />
                <span>{t('contractsCreate') ?? 'Новый договор'}</span>
              </button>
            )}
          </div>
        </div>

        <div className="mt-6 grid gap-4 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm lg:p-6">
          <div className="grid grid-cols-1 gap-4 md:grid-cols-4">
            <label className="flex flex-col gap-2 text-sm text-slate-600">
              <span className="flex items-center gap-2 font-medium text-slate-700">
                <CalendarRange className="h-4 w-4" />
                {t('contractsFrom') ?? 'С даты'}
              </span>
              <input
                type="date"
                value={toDateInputValue(from)}
                onChange={(event) => setFrom(event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </label>
            <label className="flex flex-col gap-2 text-sm text-slate-600">
              <span className="flex items-center gap-2 font-medium text-slate-700">
                <CalendarRange className="h-4 w-4" />
                {t('contractsTo') ?? 'По дату'}
              </span>
              <input
                type="date"
                value={toDateInputValue(to)}
                onChange={(event) => setTo(event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </label>
            <label className="flex flex-col gap-2 text-sm text-slate-600">
              <span className="flex items-center gap-2 font-medium text-slate-700">
                <UsersIcon className="h-4 w-4" />
                {t('contractsClientFilter') ?? 'Клиент'}
              </span>
              <select
                value={clientFilter}
                onChange={(event) => setClientFilter(event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                disabled={clientsLoading && clients.length === 0}
              >
                <option value="all">{t('allClients') ?? 'Все клиенты'}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </select>
              {clientsError && (
                <span className="text-xs text-rose-600">{clientsError}</span>
              )}
            </label>
            <label className="flex flex-col gap-2 text-sm text-slate-600">
              <span className="font-medium text-slate-700">{t('contractsSortHint') ?? 'Сортировка'}</span>
              <div className="rounded-lg border border-dashed border-slate-200 px-3 py-2 text-xs text-slate-500">
                {t('contractsSortDescription') ?? 'Используйте заголовки таблицы для изменения сортировки.'}
              </div>
            </label>
          </div>
          <div className="flex flex-col gap-2">
            <div className="relative">
              <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t('contractsSearchPlaceholder') ?? 'Поиск по номеру или описанию'}
                className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-4 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </div>
            {actionError && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {actionError}
              </div>
            )}
            {error && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {t('contractsLoadError') ?? 'Не удалось загрузить договоры'}
                {': '}
                {error}
              </div>
            )}
          </div>
        </div>

        <div className="mt-6 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
          {loading ? (
            <div className="flex items-center justify-center gap-3 py-16 text-slate-500">
              <Loader2 className="h-6 w-6 animate-spin" />
              <span>{t('loading') ?? 'Загрузка...'}</span>
            </div>
          ) : contracts.length === 0 ? (
            <div className="py-16 text-center">
              <p className="text-lg font-semibold text-slate-900">
                {t('contractsEmptyTitle') ?? 'Договоры отсутствуют'}
              </p>
              <p className="text-sm text-slate-600">
                {t('contractsEmptyDescription') ?? 'Создайте первый договор, чтобы видеть его в списке.'}
              </p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200">
                <thead className="bg-slate-50">
                  <tr>
                    <th
                      scope="col"
                      className="whitespace-nowrap px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500"
                    >
                      <button
                        type="button"
                        onClick={() => changeSort('number')}
                        className="inline-flex items-center gap-1 text-slate-600"
                      >
                        {t('contractNumber') ?? 'Номер'}
                        {renderSortIndicator('number')}
                      </button>
                    </th>
                    <th
                      scope="col"
                      className="whitespace-nowrap px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500"
                    >
                      <button
                        type="button"
                        onClick={() => changeSort('date')}
                        className="inline-flex items-center gap-1 text-slate-600"
                      >
                        {t('contractDate') ?? 'Дата'}
                        {renderSortIndicator('date')}
                      </button>
                    </th>
                    <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      {t('contractClients') ?? 'Клиенты'}
                    </th>
                    <th
                      scope="col"
                      className="whitespace-nowrap px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500"
                    >
                      <button
                        type="button"
                        onClick={() => changeSort('amount')}
                        className="inline-flex items-center gap-1 text-slate-600"
                      >
                        {t('contractAmount') ?? 'Сумма'}
                        {renderSortIndicator('amount')}
                      </button>
                    </th>
                    <th className="whitespace-nowrap px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      {t('contractValidUntil') ?? 'Действует до'}
                    </th>
                    <th
                      scope="col"
                      className="whitespace-nowrap px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500"
                    >
                      <button
                        type="button"
                        onClick={() => changeSort('createdAt')}
                        className="inline-flex items-center gap-1 text-slate-600"
                      >
                        {t('contractCreatedAt') ?? 'Создано'}
                        {renderSortIndicator('createdAt')}
                      </button>
                    </th>
                    <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                      {t('contractDescription') ?? 'Описание'}
                    </th>
                    {(canEdit || canDelete) && (
                      <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wide text-slate-500">
                        {t('actions') ?? 'Действия'}
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {contracts.map((contract) => (
                    <tr key={contract.id} className="hover:bg-slate-50/70">
                      <td className="whitespace-nowrap px-4 py-3 text-sm font-medium text-slate-900">
                        {contract.number}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {formatDate(contract.date)}
                      </td>
                      <td className="px-4 py-3 text-sm text-slate-600">
                        {contract.clients && contract.clients.length > 0
                          ? contract.clients.map((client) => client.name).join(', ')
                          : '—'}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {formatMoney(contract.amount)}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {formatDate(contract.validUntil)}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
                        {formatDate(contract.createdAt)}
                      </td>
                      <td className="px-4 py-3 text-sm text-slate-500">
                        {contract.description ?? '—'}
                      </td>
                      {(canEdit || canDelete) && (
                        <td className="px-4 py-3">
                          <div className="flex justify-end gap-2">
                            {canEdit && (
                              <button
                                type="button"
                                onClick={() => openEditModal(contract)}
                                className="rounded-lg border border-slate-200 p-2 text-slate-600 hover:bg-slate-100"
                                title={t('edit') ?? 'Редактировать'}
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {canDelete && (
                              <button
                                type="button"
                                onClick={() => void handleDelete(contract)}
                                className="rounded-lg border border-slate-200 p-2 text-rose-600 hover:bg-rose-50"
                                title={t('delete') ?? 'Удалить'}
                                disabled={deletingId === contract.id}
                              >
                                {deletingId === contract.id ? (
                                  <Loader2 className="h-4 w-4 animate-spin" />
                                ) : (
                                  <Trash2 className="h-4 w-4" />
                                )}
                              </button>
                            )}
                          </div>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        {contracts.length > 0 && (
          <div className="flex flex-col items-center justify-between gap-3 rounded-2xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 shadow-sm md:flex-row">
            <div>
              {t('paginationSummary', {
                start: (pagination.page - 1) * pagination.pageSize + 1,
                end: Math.min(pagination.page * pagination.pageSize, pagination.total),
                total: pagination.total,
              }) ??
                `Показано ${(pagination.page - 1) * pagination.pageSize + 1}-${Math.min(
                  pagination.page * pagination.pageSize,
                  pagination.total,
                )} из ${pagination.total}`}
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setPage((prev) => Math.max(1, prev - 1))}
                disabled={page <= 1}
                className="rounded-lg border border-slate-200 px-3 py-1 text-sm font-medium text-slate-600 disabled:opacity-50"
              >
                {t('prev') ?? 'Назад'}
              </button>
              <span className="text-sm text-slate-500">
                {t('pageOf', { page, totalPages }) ?? `Страница ${page} из ${totalPages}`}
              </span>
              <button
                type="button"
                onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
                disabled={page >= totalPages}
                className="rounded-lg border border-slate-200 px-3 py-1 text-sm font-medium text-slate-600 disabled:opacity-50"
              >
                {t('next') ?? 'Вперёд'}
              </button>
            </div>
          </div>
        )}
      </div>
      <ContractModal
        open={modalOpen}
        mode={modalMode}
        contract={selectedContract}
        onClose={() => {
          if (submitting) return;
          setModalOpen(false);
        }}
        onSubmit={handleSubmit}
        submitting={submitting}
        errorMessage={modalError}
      />
    </div>
  );
}
