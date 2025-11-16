import {
  AlertTriangle,
  CalendarRange,
  CheckCircle2,
  Loader2,
  Pencil,
  Plus,
  RotateCcw,
  Search,
  Trash2,
  WalletCards,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useInvoices, type InvoicesSortKey } from '../../hooks/useInvoices';
import type { Client, Invoice, InvoiceInput, PaymentKind, PaymentStatus } from '../../types';
import { apiService } from '../../services/api';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { InvoiceModal } from './InvoiceModal';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

type SortState = { key: InvoicesSortKey; direction: 'asc' | 'desc' };

type SummaryCard = {
  key: 'total' | 'overdue' | 'paid';
  title: string;
  amount: number;
  count: number;
  accent: string;
  icon: React.ComponentType<{ className?: string }>;
};

interface AccountsProps {
  defaultType?: PaymentKind;
  lockType?: boolean;
}

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Overdue', 'Completed', 'Processing', 'Cancelled'];

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

  const [from, setFrom] = useState<string>(startOfYear);
  const [to, setTo] = useState<string>(todayYMD);
  const [statusFilter, setStatusFilter] = useState<'all' | PaymentStatus>('all');
  const [typeFilter, setTypeFilter] = useState<PaymentKind>(defaultType ?? 'Income');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const pageSize = 15;

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
    status: statusFilter === 'all' ? undefined : statusFilter,
    search: debouncedSearch || undefined,
    type: typeFilter,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  useEffect(() => {
    setPage(1);
  }, [from, to, statusFilter, debouncedSearch, typeFilter]);

  useEffect(() => {
    if (!defaultType) return;
    setTypeFilter(defaultType);
  }, [defaultType]);

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

  const statusLabels: Record<PaymentStatus, string> = {
    Pending: t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается',
    Completed: t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачено',
    Overdue: t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочено',
    Processing: t('processingStatus') ?? 'В обработке',
    Cancelled: t('cancelledStatus') ?? 'Отменено',
  };

  const statusClasses: Record<PaymentStatus, string> = {
    Pending: 'bg-amber-100 text-amber-700 border border-amber-200',
    Completed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
    Processing: 'bg-blue-100 text-blue-700 border border-blue-200',
    Cancelled: 'bg-slate-100 text-slate-600 border border-slate-200',
  };

  const typeLabels: Record<PaymentKind, string> = {
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
      : t('invoiceIncomeTitle') ?? t('invoicesTitle') ?? 'Доходные счета';

  const headingDescription =
    typeFilter === 'Expense'
      ? t('invoiceSectionDescriptionExpense') ??
        'Учитывайте входящие счета от поставщиков и контролируйте их оплату.'
      : t('invoiceSectionDescriptionIncome') ??
        t('invoiceSectionDescription') ??
        'Отслеживайте выставленные счета и контролируйте их оплату.';

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

  const typeOptions: PaymentKind[] = ['Income', 'Expense'];

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
                  typeBadgeClasses[typeFilter]
                }`}
              >
                {typeLabels[typeFilter]}
              </span>
            ) : (
              <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-gray-200 bg-white p-1 shadow-sm">
                {typeOptions.map((option) => {
                  const isActive = option === typeFilter;
                  const buttonLabel =
                    option === 'Income'
                      ? t('invoiceTypeIncomeShort') ?? typeLabels[option]
                      : t('invoiceTypeExpenseShort') ?? typeLabels[option];
                  return (
                    <button
                      key={option}
                      type="button"
                      onClick={() => setTypeFilter(option)}
                      className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${
                        isActive
                          ? 'bg-blue-600 text-white shadow'
                          : 'bg-transparent text-gray-600 hover:bg-gray-100'
                      }`}
                    >
                      {buttonLabel}
                    </button>
                  );
                })}
              </div>
            )}
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={() => refresh()}
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
            >
              <RotateCcw className={refreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
              {t('invoiceRefresh') ?? 'Обновить'}
            </button>
            {canCreate && (
              <button
                type="button"
                onClick={openCreateModal}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700"
              >
                <Plus className="h-4 w-4" /> {t('invoiceAdd') ?? 'Добавить счёт'}
              </button>
            )}
          </div>
        </div>

        <div className="mb-6 grid gap-4 md:grid-cols-3">
          {summaryCards.map((card) => {
            const Icon = card.icon;
            return (
              <div
                key={card.key}
                className={`rounded-2xl p-5 shadow-sm ${card.accent}`}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">{card.title}</p>
                    <p className="mt-2 text-2xl font-semibold">
                      {formatCurrencySmart(card.amount).full}
                    </p>
                  </div>
                  <Icon className="h-10 w-10 opacity-80" />
                </div>
                <p className="mt-4 text-xs">
                  {t('invoiceSummaryCount') ?? 'Количество счетов'}: {card.count}
                </p>
              </div>
            );
          })}
        </div>

        <div className="mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
          <div className="flex items-center gap-2">
            <CalendarRange className="h-4 w-4 text-gray-500" />
            <input
              type="date"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
              className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
            <span className="text-gray-500">—</span>
            <input
              type="date"
              value={to}
              onChange={(e) => setTo(e.target.value)}
              className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </div>

          <div>
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value as 'all' | PaymentStatus)}
              className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            >
              <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
              {STATUS_ORDER.map((status) => (
                <option key={status} value={status}>
                  {statusLabels[status]}
                </option>
              ))}
            </select>
          </div>

          <div className="relative flex-1 min-w-[220px]">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('invoiceSearchPlaceholder') ?? 'Номер счёта, клиент или акт'}
              className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </div>

          <button
            type="button"
            onClick={handleResetFilters}
            className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
          >
            {t('invoiceFiltersReset') ?? 'Сбросить'}
          </button>
        </div>

        {summaryBuckets.pending.count > 0 && (
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
                  {[
                    { key: 'date' as InvoicesSortKey, label: t('invoiceDate') ?? t('actDate') ?? 'Дата' },
                    { key: 'number' as InvoicesSortKey, label: t('invoiceNumber') ?? 'Номер счёта' },
                    { key: 'client' as InvoicesSortKey, label: t('invoiceClient') ?? 'Контрагент' },
                    { key: 'amount' as InvoicesSortKey, label: t('invoiceAmount') ?? t('amount') ?? 'Сумма' },
                    { key: 'status' as InvoicesSortKey, label: t('invoiceStatus') ?? t('status') ?? 'Статус' },
                    { key: 'responsible' as InvoicesSortKey, label: t('invoiceResponsible') ?? 'Ответственный' },
                    { key: 'dueDate' as InvoicesSortKey, label: t('invoiceDueDate') ?? 'Срок оплаты' },
                    { key: 'number' as InvoicesSortKey, label: t('invoiceAct') ?? 'Акт', sortable: false },
                    { key: 'createdAt' as InvoicesSortKey, label: t('actions') ?? 'Действия', sortable: false },
                  ].map((column) => {
                    const isSortable = column.sortable !== false;
                    const isActive = sortState.key === column.key;
                    const direction = isActive ? sortState.direction : undefined;
                    return (
                      <th key={column.label} scope="col" className="px-4 py-3">
                        {isSortable ? (
                          <button
                            type="button"
                            onClick={() => {
                              handleSort(column.key);
                              setPage(1);
                            }}
                            className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                          >
                            {column.label}
                            <span className="text-gray-400">
                              {isActive ? (direction === 'asc' ? '▲' : '▼') : ''}
                            </span>
                          </button>
                        ) : (
                          <span>{column.label}</span>
                        )}
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {loading ? (
                  <tr>
                    <td colSpan={9} className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : invoices.length === 0 ? (
                  <tr>
                    <td colSpan={9} className="py-10 text-center text-gray-500">
                      {t('invoiceNoData') ?? 'Счета не найдены'}
                    </td>
                  </tr>
                ) : (
                  invoices.map((invoice) => {
                    const status = invoice.status ?? 'Pending';
                    const type = (invoice.type ?? 'Income') as PaymentKind;
                    const actPieces: string[] = [];
                    if (invoice.actNumber) actPieces.push(`№${invoice.actNumber}`);
                    if (invoice.actTitle) actPieces.push(invoice.actTitle);
                    const actLine = actPieces.join(' · ');
                    const typeBadgeClass = typeBadgeClasses[type];
                    const typeLabel = typeLabels[type];
                    const primaryName =
                      type === 'Expense'
                        ? invoice.counterpartyName ?? invoice.clientName ?? '—'
                        : invoice.clientName ?? invoice.counterpartyName ?? '—';
                    const extraLines: string[] = [];
                    if (type === 'Expense') {
                      if (invoice.clientName) {
                        extraLines.push(
                          `${t('invoiceLinkedClientLabel') ?? 'Связан с клиентом'}: ${invoice.clientName}`,
                        );
                      }
                      if (invoice.clientCompany) {
                        extraLines.push(invoice.clientCompany);
                      }
                    } else {
                      if (invoice.clientCompany) {
                        extraLines.push(invoice.clientCompany);
                      }
                      if (invoice.counterpartyName) {
                        extraLines.push(
                          `${t('invoiceCounterpartyLabel') ?? 'От поставщика'}: ${invoice.counterpartyName}`,
                        );
                      }
                    }

                    return (
                      <tr key={invoice.id}>
                        <td className="px-4 py-3 font-medium text-gray-900">{formatDate(invoice.date)}</td>
                        <td className="px-4 py-3 text-gray-900">{invoice.number}</td>
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
                            {extraLines.map((line, index) => (
                              <div key={`${line}-${index}`} className="text-xs text-gray-500">
                                {line}
                              </div>
                            ))}
                          </div>
                        </td>
                        <td className="px-4 py-3">
                          <span className="font-semibold text-gray-900">
                            {formatCurrencySmart(invoice.amount).full}
                          </span>
                        </td>
                        <td className="px-4 py-3">
                          <span
                            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${statusClasses[status]}`}
                          >
                            {statusLabels[status]}
                          </span>
                          {invoice.paymentStatusName && (
                            <div className="mt-1 text-xs text-gray-500">{invoice.paymentStatusName}</div>
                          )}
                        </td>
                        <td className="px-4 py-3">
                          {invoice.responsibleName ? invoice.responsibleName : '—'}
                        </td>
                        <td className="px-4 py-3">{formatDate(invoice.dueDate)}</td>
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
                        <td className="px-4 py-3">
                          <div className="flex items-center gap-2">
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
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>

          {error && (
            <div className="border-t border-gray-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {String(error)}
            </div>
          )}

          {!loading && invoices.length > 0 && (
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
          defaultType={typeFilter}
          lockType={lockType}
        />
      </div>
    </div>
  );
}
