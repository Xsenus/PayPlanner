import {
  AlertTriangle,
  CalendarRange,
  CheckCircle2,
  Loader2,
  Pencil,
  Plus,
  RotateCcw,
  Search,
  Settings2,
  Trash2,
  WalletCards,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useInvoices, type InvoicesSortKey } from '../../hooks/useInvoices';
import type { Client, Invoice, InvoiceInput, PaymentKind } from '../../types';
import { apiService } from '../../services/api';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { InvoiceModal } from './InvoiceModal';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

interface AccountsProps {
  defaultType?: PaymentKind;
  lockType?: boolean;
}

type FilterType = PaymentKind | 'All';
type AccountStatus = 'created' | 'paid' | 'partial' | 'cancelled';
type ColumnKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'counterparty'
  | 'status'
  | 'dueDate'
  | 'paidDate'
  | 'act'
  | 'responsible'
  | 'type'
  | 'description';

type SortState = { key: InvoicesSortKey; direction: 'asc' | 'desc' };

type SummaryCard = {
  key: 'total' | 'overdue' | 'paid';
  title: string;
  amount: number;
  count: number;
  accent: string;
  icon: React.ComponentType<{ className?: string }>;
};

type TableColumn = {
  key: ColumnKey;
  label: string;
  sortable?: boolean;
};

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

const DEFAULT_VISIBLE_COLUMNS: ColumnKey[] = ['date', 'number', 'amount', 'counterparty', 'status', 'dueDate', 'act'];

function deriveAccountStatus(invoice: Invoice): AccountStatus {
  if (invoice.status === 'Completed') return 'paid';
  if (invoice.status === 'Cancelled') return 'cancelled';
  if (invoice.status === 'Pending') return 'created';
  return 'partial';
}

function safeRead<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as T;
    return parsed ?? fallback;
  } catch {
    return fallback;
  }
}

function safeWrite<T>(key: string, value: T) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /** */
  }
}

export function Accounts({ defaultType, lockType = false }: AccountsProps) {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const { t } = useTranslation();

  const canCreate = permissions.accounts.canCreate;
  const canEdit = permissions.accounts.canEdit;
  const canDelete = permissions.accounts.canDelete;

  const today = useMemo(() => new Date(), []);
  const startOfYear = useMemo(() => formatLocalYMD(new Date(today.getFullYear(), 0, 1)), [today]);
  const todayYMD = useMemo(() => formatLocalYMD(today), [today]);

  const storagePrefix = useMemo(() => `pp.accounts.${user?.id ?? 'guest'}`, [user?.id]);
  const savedPeriod = useMemo(
    () => safeRead<{ from?: string; to?: string }>(`${storagePrefix}.period`, {}),
    [storagePrefix],
  );
  const savedColumns = useMemo(
    () => safeRead<ColumnKey[]>(`${storagePrefix}.columns`, DEFAULT_VISIBLE_COLUMNS),
    [storagePrefix],
  );

  const [from, setFrom] = useState<string>(savedPeriod.from || startOfYear);
  const [to, setTo] = useState<string>(savedPeriod.to || todayYMD);
  const [statusFilter, setStatusFilter] = useState<'all' | AccountStatus>('all');
  const [typeFilter, setTypeFilter] = useState<FilterType>(defaultType ?? 'All');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const pageSize = 15;
  const [visibleColumns, setVisibleColumns] = useState<ColumnKey[]>(savedColumns);
  const [columnsOpen, setColumnsOpen] = useState(false);

  const {
    invoices,
    loading,
    refreshing,
    error,
    summary,
    pagination,
    refresh,
    createInvoice,
    updateInvoice,
    deleteInvoice,
  } = useInvoices({
    from: from || undefined,
    to: to || undefined,
    status:
      statusFilter === 'paid'
        ? 'Completed'
        : statusFilter === 'created'
          ? 'Pending'
          : statusFilter === 'partial'
            ? 'Processing'
            : undefined,
    search: debouncedSearch || undefined,
    type: typeFilter === 'All' ? undefined : typeFilter,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  useEffect(() => {
    setPage(1);
  }, [from, to, statusFilter, debouncedSearch, typeFilter]);

  useEffect(() => {
    if (!defaultType) {
      setTypeFilter('All');
      return;
    }
    setTypeFilter(defaultType);
  }, [defaultType]);

  useEffect(() => {
    safeWrite(`${storagePrefix}.period`, { from, to });
  }, [from, to, storagePrefix]);

  useEffect(() => {
    safeWrite(`${storagePrefix}.columns`, visibleColumns);
  }, [storagePrefix, visibleColumns]);

  const totalPages = useMemo(() => {
    if (!pagination.pageSize) return 1;
    return Math.max(1, Math.ceil((pagination.total ?? 0) / pagination.pageSize));
  }, [pagination.total, pagination.pageSize]);

  const summaryBuckets = useMemo(
    () =>
      summary ?? {
        total: { amount: 0, count: 0 },
        pending: { amount: 0, count: 0 },
        paid: { amount: 0, count: 0 },
        overdue: { amount: 0, count: 0 },
      },
    [summary],
  );

  const summaryCards: SummaryCard[] = useMemo(
    () => [
      {
        key: 'total',
        title: t('invoiceSummaryTotal') ?? 'Сумма счетов всего',
        amount: summaryBuckets.total.amount,
        count: summaryBuckets.total.count,
        accent: 'border border-blue-100 bg-blue-50 text-blue-700',
        icon: WalletCards,
      },
      {
        key: 'overdue',
        title: t('invoiceSummaryOverdue') ?? 'Просрочено',
        amount: summaryBuckets.overdue.amount,
        count: summaryBuckets.overdue.count,
        accent: 'border border-rose-100 bg-rose-50 text-rose-700',
        icon: AlertTriangle,
      },
      {
        key: 'paid',
        title: t('invoiceSummaryPaid') ?? 'Оплачено',
        amount: summaryBuckets.paid.amount,
        count: summaryBuckets.paid.count,
        accent: 'border border-emerald-100 bg-emerald-50 text-emerald-700',
        icon: CheckCircle2,
      },
    ],
    [summaryBuckets, t],
  );

  const statusLabels: Record<AccountStatus, string> = {
    created: 'Создан',
    paid: 'Оплачен',
    partial: 'Оплачен частично',
    cancelled: t('cancelledStatus') ?? 'Отменён',
  };

  const statusClasses: Record<AccountStatus, string> = {
    created: 'bg-amber-100 text-amber-700 border border-amber-200',
    paid: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    partial: 'bg-blue-100 text-blue-700 border border-blue-200',
    cancelled: 'bg-slate-100 text-slate-700 border border-slate-200',
  };

  const typeLabels: Record<PaymentKind, string> = {
    Income: t('invoiceTypeIncome') ?? 'Доходные счета',
    Expense: t('invoiceTypeExpense') ?? 'Расходные счета',
  };

  const typeFilterLabels: Record<FilterType, string> = {
    All: t('invoiceTypeAll') ?? 'Все счета',
    Income: t('invoiceTypeIncome') ?? 'Доходные счета',
    Expense: t('invoiceTypeExpense') ?? 'Расходные счета',
  };

  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };

  const headingTitle =
    typeFilter === 'Expense'
      ? t('invoiceExpenseTitle') ?? 'Расходные счета'
      : typeFilter === 'Income'
        ? t('invoiceIncomeTitle') ?? t('invoicesTitle') ?? 'Доходные счета'
        : t('invoiceAllTitle') ?? t('accounts') ?? 'Все счета';

  const headingDescription =
    typeFilter === 'Expense'
      ? t('invoiceSectionDescriptionExpense') ??
        'Учитывайте входящие счета от поставщиков и контролируйте их оплату.'
      : typeFilter === 'Income'
        ? t('invoiceSectionDescriptionIncome') ??
          t('invoiceSectionDescription') ??
          'Отслеживайте выставленные счета и контролируйте их оплату.'
        : t('invoiceSectionDescriptionAll') ??
          t('invoiceSectionDescription') ??
          'Просматривайте все входящие и исходящие счета компании.';

  const handleSort = (key: InvoicesSortKey) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
  };

  const [modalOpen, setModalOpen] = useState(false);
  const [modalMode, setModalMode] = useState<'create' | 'edit'>('create');
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);

  const [clients, setClients] = useState<Client[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);

  const ensureClients = useCallback(async () => {
    if (!modalOpen) return;
    if (clients.length > 0 && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const response = await apiService.getClients();
      setClients(response ?? []);
      setLookupsError(null);
    } catch (err) {
      setLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить клиентов');
    } finally {
      setLookupsLoading(false);
    }
  }, [modalOpen, clients.length, lookupsError]);

  useEffect(() => {
    if (modalOpen) {
      void ensureClients();
    }
  }, [modalOpen, ensureClients]);

  const openCreateModal = () => {
    setModalMode('create');
    setSelectedInvoice(null);
    setModalError(null);
    setModalOpen(true);
  };

  const openEditModal = (invoice: Invoice) => {
    setModalMode('edit');
    setSelectedInvoice(invoice);
    setModalError(null);
    setModalOpen(true);
  };

  const closeModal = () => {
    setModalOpen(false);
    setSelectedInvoice(null);
    setModalError(null);
  };

  const submitInvoice = async (payload: InvoiceInput) => {
    setSubmitting(true);
    setModalError(null);
    try {
      if (modalMode === 'edit' && selectedInvoice) {
        await updateInvoice(selectedInvoice.id, payload);
      } else {
        await createInvoice(payload);
      }
      closeModal();
    } catch (err) {
      setModalError(err instanceof Error ? err.message : 'Не удалось сохранить счёт');
    } finally {
      setSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!canDelete) return;
    if (!window.confirm(t('invoiceDeleteConfirm') ?? 'Удалить счёт?')) return;
    try {
      await deleteInvoice(invoice.id);
    } catch (err) {
      window.alert(err instanceof Error ? err.message : 'Не удалось удалить счёт');
    }
  };

  const handleResetFilters = () => {
    setFrom(startOfYear);
    setTo(todayYMD);
    setStatusFilter('all');
    setSearch('');
  };

  const typeOptions: FilterType[] = ['All', 'Income', 'Expense'];

  const normalizedSearch = debouncedSearch.toLowerCase();

  const filteredInvoices = useMemo(() => {
    const searchTerm = normalizedSearch;
    return invoices.filter((invoice) => {
      const derivedStatus = deriveAccountStatus(invoice);
      if (statusFilter !== 'all' && derivedStatus !== statusFilter) return false;

      if (!searchTerm) return true;
      const haystack = [
        invoice.number,
        invoice.clientName,
        invoice.counterpartyName,
        invoice.actNumber,
        invoice.actTitle,
        invoice.actReference,
        invoice.description,
        invoice.paymentStatusName,
      ]
        .map((value) => (value ? String(value).toLowerCase() : ''))
        .filter(Boolean);

      return haystack.some((value) => value.includes(searchTerm));
    });
  }, [invoices, normalizedSearch, statusFilter]);

  const columns: TableColumn[] = useMemo(
    () => [
      { key: 'date', label: t('invoiceDate') ?? t('actDate') ?? 'Дата', sortable: true },
      { key: 'number', label: t('invoiceNumber') ?? 'Номер счёта', sortable: true },
      { key: 'amount', label: t('invoiceAmount') ?? t('amount') ?? 'Сумма', sortable: true },
      { key: 'counterparty', label: t('invoiceClient') ?? 'Контрагент', sortable: true },
      { key: 'status', label: t('invoiceStatus') ?? t('status') ?? 'Статус', sortable: false },
      { key: 'dueDate', label: t('invoiceDueDate') ?? 'Оплатить до', sortable: true },
      { key: 'paidDate', label: t('paidDate') ?? 'Дата оплаты', sortable: false },
      { key: 'act', label: t('invoiceAct') ?? 'Акт', sortable: false },
      { key: 'responsible', label: t('invoiceResponsible') ?? 'Ответственный', sortable: true },
      { key: 'type', label: t('invoiceType') ?? 'Тип', sortable: false },
      { key: 'description', label: t('description') ?? 'Описание', sortable: false },
    ],
    [t],
  );

  const toggleColumn = (key: ColumnKey) => {
    setVisibleColumns((prev) => {
      if (prev.includes(key)) {
        const updated = prev.filter((c) => c !== key);
        return updated.length ? updated : prev;
      }
      return [...prev, key];
    });
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 py-8 sm:px-6 lg:px-8">
        <div className="mb-8 flex flex-wrap items-start justify-between gap-4">
          <div className="flex flex-col gap-3">
            <div>
              <h1 className="text-2xl font-bold text-gray-900">{headingTitle}</h1>
              <p className="text-sm text-gray-500">{headingDescription}</p>
            </div>
            {lockType ? (
              <span
                className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${
                  typeBadgeClasses[typeFilter === 'Expense' ? 'Expense' : 'Income']
                }`}
              >
                {typeFilterLabels[typeFilter === 'Expense' ? 'Expense' : 'Income']}
              </span>
            ) : (
              <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-gray-200 bg-white p-1 shadow-sm">
                {typeOptions.map((option) => {
                  const isActive = option === typeFilter;
                  const buttonLabel =
                    option === 'Income'
                      ? t('invoiceTypeIncomeShort') ?? typeFilterLabels[option]
                      : option === 'Expense'
                        ? t('invoiceTypeExpenseShort') ?? typeFilterLabels[option]
                        : t('invoiceTypeAllShort') ?? typeFilterLabels[option];
                  return (
                    <button
                      key={option}
                      type="button"
                      onClick={() => setTypeFilter(option)}
                      className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${
                        isActive ? 'bg-blue-600 text-white shadow' : 'bg-transparent text-gray-600 hover:bg-gray-100'
                      }`}
                    >
                      {buttonLabel}
                    </button>
                  );
                })}
              </div>
            )}

            <div className="flex flex-wrap gap-3">
              {summaryCards.map((card) => (
                <div
                  key={card.key}
                  className={`flex min-w-[12rem] flex-1 items-center gap-3 rounded-xl bg-white p-4 shadow-sm ${card.accent}`}
                >
                  <card.icon className="h-5 w-5" />
                  <div className="flex flex-col">
                    <span className="text-xs uppercase text-gray-500">{card.title}</span>
                    <span className="text-lg font-semibold text-gray-900">
                      {formatCurrencySmart(card.amount).full}
                    </span>
                    <span className="text-sm text-gray-600">{card.count} шт.</span>
                  </div>
                </div>
              ))}
            </div>
          </div>

          {canCreate && (
            <button
              type="button"
              onClick={openCreateModal}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-500"
            >
              <Plus className="h-4 w-4" />
              {t('addInvoice') ?? 'Добавить счёт'}
            </button>
          )}
        </div>

        <div className="mb-6 flex flex-col gap-3 rounded-2xl border border-gray-200 bg-white p-4 shadow-sm sm:flex-row sm:items-end sm:justify-between">
          <div className="grid w-full gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-gray-700">{t('invoiceFiltersPeriod') ?? 'Период с'}</span>
              <div className="relative">
                <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
                <input
                  type="date"
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                />
              </div>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-gray-700">{t('invoiceFiltersPeriodTo') ?? 'Период по'}</span>
              <div className="relative">
                <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
                <input
                  type="date"
                  value={to}
                  onChange={(event) => setTo(event.target.value)}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                />
              </div>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-gray-700">{t('status') ?? 'Статус'}</span>
              <select
                value={statusFilter}
                onChange={(event) => setStatusFilter(event.target.value as 'all' | AccountStatus)}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              >
                <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                <option value="created">{statusLabels.created}</option>
                <option value="partial">{statusLabels.partial}</option>
                <option value="paid">{statusLabels.paid}</option>
                <option value="cancelled">{statusLabels.cancelled}</option>
              </select>
            </label>
          </div>

          <div className="flex w-full flex-col gap-3 sm:w-auto sm:flex-1 sm:flex-row sm:items-end sm:justify-end">
            <div className="relative w-full sm:w-72">
              <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t('invoiceSearchPlaceholder') ?? 'Номер счёта, клиент или акт'}
                className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleResetFilters}
                className="inline-flex items-center gap-1 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-600 transition hover:bg-gray-100"
              >
                <RotateCcw className="h-4 w-4" />
                {t('invoiceFiltersReset') ?? 'Сбросить'}
              </button>
              <button
                type="button"
                onClick={() => void refresh()}
                className="inline-flex items-center gap-1 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-600 transition hover:bg-blue-100"
              >
                {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <CalendarRange className="h-4 w-4" />}
                {t('actFiltersApply') ?? 'Обновить'}
              </button>
              <div className="relative">
                <button
                  type="button"
                  onClick={() => setColumnsOpen((prev) => !prev)}
                  className="inline-flex items-center gap-1 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 transition hover:bg-gray-100"
                >
                  <Settings2 className="h-4 w-4" />
                  {t('tableSettings') ?? 'Настройка таблицы'}
                </button>
                {columnsOpen && (
                  <div className="absolute right-0 z-10 mt-2 w-64 rounded-lg border border-gray-200 bg-white p-3 shadow-lg">
                    <div className="mb-2 text-xs font-semibold uppercase text-gray-500">
                      {t('visibleColumns') ?? 'Отображаемые столбцы'}
                    </div>
                    <div className="flex max-h-64 flex-col gap-1 overflow-y-auto">
                      {columns.map((column) => (
                        <label key={column.key} className="flex items-center gap-2 text-sm text-gray-700">
                          <input
                            type="checkbox"
                            checked={visibleColumns.includes(column.key)}
                            onChange={() => toggleColumn(column.key)}
                            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                          />
                          <span>{column.label}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>

        {summaryBuckets.pending?.count > 0 && (
          <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {t('invoiceSummaryPending') ?? 'Ожидает оплаты'}: {summaryBuckets.pending.count}{' '}
            · {formatCurrencySmart(summaryBuckets.pending.amount).full}
          </div>
        )}

        <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                  {columns
                    .filter((column) => visibleColumns.includes(column.key))
                    .map((column) => {
                      const isSortable = column.sortable !== false && column.key !== 'status';
                      const isActive = sortState.key === column.key;
                      const direction = isActive ? sortState.direction : undefined;
                      const sortKey = (column.key as InvoicesSortKey) ?? 'date';

                      return (
                        <th key={column.key} scope="col" className="px-4 py-3">
                          {isSortable ? (
                            <button
                              type="button"
                              onClick={() => {
                                handleSort(sortKey);
                                setPage(1);
                              }}
                              className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                            >
                              {column.label}
                              <span className="text-gray-400">{isActive ? (direction === 'asc' ? '▲' : '▼') : ''}</span>
                            </button>
                          ) : (
                            <span>{column.label}</span>
                          )}
                        </th>
                      );
                    })}
                  {(canEdit || canDelete) && (
                    <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wide text-gray-500">
                      {t('actions') ?? 'Действия'}
                    </th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {loading ? (
                  <tr>
                    <td colSpan={visibleColumns.length + 1} className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : filteredInvoices.length === 0 ? (
                  <tr>
                    <td colSpan={visibleColumns.length + 1} className="py-10 text-center text-gray-500">
                      {t('invoiceNoData') ?? 'Счета не найдены'}
                    </td>
                  </tr>
                ) : (
                  filteredInvoices.map((invoice) => {
                    const accountStatus = deriveAccountStatus(invoice);
                    const type = (invoice.type ?? 'Income') as PaymentKind;
                    const typeBadgeClass = typeBadgeClasses[type];
                    const typeLabel = typeLabels[type];
                    const actPieces: string[] = [];
                    if (invoice.actNumber) actPieces.push(`№${invoice.actNumber}`);
                    if (invoice.actTitle) actPieces.push(invoice.actTitle);
                    const actLine = actPieces.join(' · ');
                    const primaryName = invoice.clientName || invoice.counterpartyName || '—';

                    return (
                      <tr key={invoice.id} className="hover:bg-gray-50/70">
                        {visibleColumns.includes('date') && (
                          <td className="px-4 py-3 font-medium text-gray-900">{formatDate(invoice.date)}</td>
                        )}
                        {visibleColumns.includes('number') && (
                          <td className="px-4 py-3 text-gray-900">{invoice.number}</td>
                        )}
                        {visibleColumns.includes('amount') && (
                          <td className="px-4 py-3">
                            <span className="font-semibold text-gray-900">
                              {formatCurrencySmart(invoice.amount).full}
                            </span>
                          </td>
                        )}
                        {visibleColumns.includes('counterparty') && (
                          <td className="px-4 py-3">
                            <div className="flex flex-col gap-1">
                              <div className="flex flex-wrap items-center gap-2">
                                <span className="font-medium text-gray-900">{primaryName}</span>
                                <ClientStatusBadge status={invoice.clientStatus} />
                                <span
                                  className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClass}`}
                                >
                                  {typeLabel}
                                </span>
                              </div>
                              {invoice.clientCompany && (
                                <div className="text-xs text-gray-500">{invoice.clientCompany}</div>
                              )}
                              {invoice.counterpartyName && invoice.counterpartyName !== invoice.clientName && (
                                <div className="text-xs text-gray-500">
                                  {t('invoiceCounterpartyLabel') ?? 'От поставщика'}: {invoice.counterpartyName}
                                </div>
                              )}
                            </div>
                          </td>
                        )}
                        {visibleColumns.includes('status') && (
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${statusClasses[accountStatus]}`}
                            >
                              {statusLabels[accountStatus]}
                            </span>
                            {invoice.paymentStatusName && (
                              <div className="mt-1 text-xs text-gray-500">{invoice.paymentStatusName}</div>
                            )}
                          </td>
                        )}
                        {visibleColumns.includes('dueDate') && (
                          <td className="px-4 py-3">{formatDate(invoice.dueDate)}</td>
                        )}
                        {visibleColumns.includes('paidDate') && (
                          <td className="px-4 py-3">{formatDate(invoice.paidDate)}</td>
                        )}
                        {visibleColumns.includes('act') && (
                          <td className="px-4 py-3">
                            {actLine ? (
                              <div className="font-medium text-gray-900">{actLine}</div>
                            ) : (
                              <div className="text-gray-500">—</div>
                            )}
                            {invoice.actReference && (
                              <div className="text-xs text-gray-500">{invoice.actReference}</div>
                            )}
                          </td>
                        )}
                        {visibleColumns.includes('responsible') && (
                          <td className="px-4 py-3">{invoice.responsibleName ? invoice.responsibleName : '—'}</td>
                        )}
                        {visibleColumns.includes('type') && (
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClass}`}
                            >
                              {typeLabel}
                            </span>
                          </td>
                        )}
                        {visibleColumns.includes('description') && (
                          <td className="px-4 py-3 text-gray-600">{invoice.description || '—'}</td>
                        )}
                        {(canEdit || canDelete) && (
                          <td className="px-4 py-3">
                            <div className="flex items-center justify-end gap-2">
                              {canEdit && (
                                <button
                                  type="button"
                                  onClick={() => openEditModal(invoice)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                  title={t('edit') ?? 'Редактировать'}
                                  aria-label={t('edit') ?? 'Редактировать'}
                                >
                                  <Pencil className="h-4 w-4" />
                                </button>
                              )}
                              {canDelete && (
                                <button
                                  type="button"
                                  onClick={() => removeInvoice(invoice)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                  title={t('delete') ?? 'Удалить'}
                                  aria-label={t('delete') ?? 'Удалить'}
                                >
                                  <Trash2 className="h-4 w-4" />
                                </button>
                              )}
                            </div>
                          </td>
                        )}
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>

          {error && (
            <div className="border-t border-gray-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{String(error)}</div>
          )}

          {!loading && filteredInvoices.length > 0 && (
            <div className="flex items-center justify-between border-t border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600">
              <div>
                {t('recordsFound') ?? 'Найдено записей'}: {pagination.total}
              </div>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1}
                  className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {t('previous') ?? 'Назад'}
                </button>
                <span>
                  {page} / {totalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  disabled={page >= totalPages}
                  className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {t('next') ?? 'Вперёд'}
                </button>
              </div>
            </div>
          )}
        </div>

        <InvoiceModal
          open={modalOpen}
          mode={modalMode}
          invoice={selectedInvoice}
          onClose={closeModal}
          onSubmit={submitInvoice}
          submitting={submitting}
          errorMessage={modalError}
          clients={clients}
          lookupsLoading={lookupsLoading}
          lookupsError={lookupsError}
          defaultClientId={undefined}
          defaultType={typeFilter === 'All' ? undefined : typeFilter}
          lockType={lockType}
        />
      </div>
    </div>
  );
}
