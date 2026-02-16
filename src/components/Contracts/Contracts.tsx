import { FileSignature, Loader2, Pencil, RefreshCcw, Search, Trash2 } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useDialog } from '../../hooks/useDialog';
import { useTranslation } from '../../hooks/useTranslation';
import { useContracts, type ContractsSortKey } from '../../hooks/useContracts';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { Client, Contract } from '../../types';
import { toDateInputValue, toRuDate } from '../../utils/dateUtils';

type SortState = { key: ContractsSortKey; direction: 'asc' | 'desc' };

interface ContractsProps {
  onOpenContract?: (id: number) => void;
  onOpenClient?: (id: number) => void;
}

const formatMoney = (value?: number | null) =>
  value == null
    ? '—'
    : new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB' }).format(value);

const formatDate = (value?: string | null) => (value ? (toRuDate(value) || value) : '—');

const SORT_LABELS: Record<ContractsSortKey, string> = {
  number: 'Номер договора',
  date: 'Дата договора',
  amount: 'Сумма договора',
  createdAt: 'Дата создания',
};

export function Contracts({ onOpenContract, onOpenClient }: ContractsProps) {
  const { user } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const permissions = useRolePermissions(user?.role?.id).contracts;
  const { t } = useTranslation();
  const { confirm } = useDialog();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [clientFilter, setClientFilter] = useState<string>('all');
  const [search, setSearch] = useState('');
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [clients, setClients] = useState<Client[]>([]);

  const debouncedSearch = useDebouncedValue(search.trim(), 350);
  const clientIdFilter = clientFilter === 'all' ? undefined : Number(clientFilter);

  const { contracts, loading, refreshing, error, pagination, refresh, deleteContract } = useContracts({
    from: from || undefined,
    to: to || undefined,
    clientId: Number.isNaN(clientIdFilter) ? undefined : clientIdFilter,
    search: debouncedSearch || undefined,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize: 20,
  });

  useEffect(() => {
    setPage(1);
  }, [from, to, clientFilter, debouncedSearch, sortState]);

  useEffect(() => {
    void apiService
      .getClientsV1({ sortBy: 'name', sortDir: 'asc' })
      .then((data) => setClients(data ?? []))
      .catch(() => setClients([]));
  }, []);

  const totalPages = Math.max(1, Math.ceil((pagination.total || 0) / (pagination.pageSize || 20)));

  const handleDelete = useCallback(
    async (contract: Contract) => {
      if (!permissions.canDelete) return;
      const ok = await confirm({
        title: 'Подтверждение удаления',
        description: `Удалить договор «${contract.number}»?`,
        confirmLabel: 'Удалить',
        cancelLabel: 'Отмена',
        variant: 'danger',
      });
      if (!ok) return;
      setDeletingId(contract.id);
      try {
        await deleteContract(contract.id);
      } catch (e) {
        setActionError(e instanceof Error ? e.message : 'Не удалось удалить договор');
      } finally {
        setDeletingId(null);
      }
    },
    [confirm, deleteContract, permissions.canDelete],
  );

  const renderSort = (key: ContractsSortKey) =>
    sortState.key !== key ? '↕' : sortState.direction === 'asc' ? '↑' : '↓';

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <FileSignature className="h-9 w-9 text-emerald-500" />
            <h1 className="text-2xl font-bold text-slate-900">{t('contracts') ?? 'Договоры'}</h1>
          </div>
          <div className="flex gap-2">
            {permissions.canCreate && (
              <button
                className="rounded-lg bg-emerald-600 px-4 py-2 text-sm text-white"
                onClick={() => setActiveTab('contractCreate')}
              >
                Создать договор
              </button>
            )}
            <button
              className="inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm"
              onClick={() => void refresh()}
            >
              {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCcw className="h-4 w-4" />}
              Обновить
            </button>
          </div>
        </div>

        <div className="mb-4 grid gap-4 rounded-2xl border bg-white p-4 md:grid-cols-4">
          <label className="text-sm text-slate-700">
            С даты
            <input
              type="date"
              value={toDateInputValue(from)}
              onChange={(e) => setFrom(e.target.value)}
              className="mt-1 w-full rounded-lg border px-3 py-2"
            />
          </label>
          <label className="text-sm text-slate-700">
            По дату
            <input
              type="date"
              value={toDateInputValue(to)}
              onChange={(e) => setTo(e.target.value)}
              className="mt-1 w-full rounded-lg border px-3 py-2"
            />
          </label>
          <label className="text-sm text-slate-700">
            Клиент
            <select
              value={clientFilter}
              onChange={(e) => setClientFilter(e.target.value)}
              className="mt-1 w-full rounded-lg border px-3 py-2"
            >
              <option value="all">Все клиенты</option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
          <label className="text-sm text-slate-700">
            Поиск
            <div className="relative mt-1">
              <Search className="absolute left-2 top-2.5 h-4 w-4 text-slate-400" />
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                className="w-full rounded-lg border py-2 pl-8 pr-3"
                placeholder="Поиск по номеру, названию или описанию"
              />
            </div>
          </label>
        </div>

        {actionError && (
          <div className="mb-3 rounded-lg border border-rose-200 bg-rose-50 p-2 text-sm text-rose-700">
            {actionError}
          </div>
        )}
        {error && (
          <div className="mb-3 rounded-lg border border-rose-200 bg-rose-50 p-2 text-sm text-rose-700">
            {error}
          </div>
        )}

        <div className="overflow-hidden rounded-2xl border bg-white">
          {loading ? (
            <div className="flex justify-center py-10">
              <Loader2 className="h-6 w-6 animate-spin" />
            </div>
          ) : (
            <table className="min-w-full divide-y">
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  {(['number', 'date', 'amount', 'createdAt'] as ContractsSortKey[]).map((k) => (
                    <th key={k} className="px-3 py-2 text-left">
                      <button
                        onClick={() =>
                          setSortState((s) => ({
                            key: k,
                            direction: s.key === k && s.direction === 'asc' ? 'desc' : 'asc',
                          }))
                        }
                      >
                        {SORT_LABELS[k]} {renderSort(k)}
                      </button>
                    </th>
                  ))}
                  <th className="px-3 py-2 text-left">Клиенты</th>
                  <th className="px-3 py-2 text-left">Описание</th>
                  <th className="px-3 py-2 text-right">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {contracts.map((contract) => (
                  <tr key={contract.id} className="hover:bg-slate-50">
                    <td className="px-3 py-2">
                      <button
                        className="text-left text-emerald-700 hover:underline"
                        onClick={() => onOpenContract?.(contract.id)}
                      >
                        {contract.number}
                      </button>
                    </td>
                    <td className="px-3 py-2">{formatDate(contract.date)}</td>
                    <td className="px-3 py-2">{formatMoney(contract.amount)}</td>
                    <td className="px-3 py-2">{formatDate(contract.createdAt)}</td>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap gap-2">
                        {contract.clients.map((c) => (
                          <button
                            key={c.id}
                            className="rounded-full border border-slate-200 bg-white px-2 py-1 text-xs text-blue-700 hover:bg-blue-50"
                            onClick={() => onOpenClient?.(c.id)}
                          >
                            {c.name}
                          </button>
                        ))}
                      </div>
                    </td>
                    <td className="px-3 py-2">{contract.description || '—'}</td>
                    <td className="px-3 py-2 text-right">
                      <div className="flex justify-end gap-1">
                        <button
                          onClick={() => onOpenContract?.(contract.id)}
                          className="rounded border p-1 text-slate-700 hover:bg-slate-100"
                          title="Редактировать договор"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        {permissions.canDelete && (
                          <button
                            onClick={() => void handleDelete(contract)}
                            className="rounded border p-1 text-rose-600"
                            title="Удалить"
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
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="mt-3 flex items-center justify-between text-sm">
          <span>{pagination.total || 0} записей</span>
          <div className="flex items-center gap-2">
            <button
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
              className="rounded border px-2 py-1"
            >
              Назад
            </button>
            <span>
              {page}/{totalPages}
            </span>
            <button
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
              className="rounded border px-2 py-1"
            >
              Вперёд
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
