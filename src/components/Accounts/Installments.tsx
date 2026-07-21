import { SystemSelect } from '../ui/SystemSelect';
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown, CheckCircle2, GripVertical, Loader2, Pencil, Plus, RotateCcw, Search, SlidersHorizontal, Trash2, WalletCards } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useInvoices, type InvoicesSortKey } from '../../hooks/useInvoices';
import type { ActResponsible, Client, Invoice, InvoiceInput, PaymentKind, PaymentStatus } from '../../types';
import { apiService } from '../../services/api';
import { toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { normalizeStoredPeriod, resolvePeriod, type PeriodFilter } from '../../utils/periodUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { DatePeriodPicker } from '../DateRange/DatePeriodPicker';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { InvoiceModal } from './InvoiceModal';

type SortState = { key: InvoicesSortKey; direction: 'asc' | 'desc' };
type SummaryCard = { key: 'total' | 'overdue' | 'paid'; title: string; amount: number; count: number; accent: string; icon: React.ComponentType<{ className?: string }> };
interface AccountsProps { defaultType?: PaymentKind; lockType?: boolean }
type FilterType = PaymentKind | 'All';
const PAGE_SIZE_OPTIONS = [10, 15, 25, 50, 100];
const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Overdue', 'Completed', 'Processing', 'Cancelled'];
const INSTALLMENT_COLUMNS = [
  { key: 'date', label: 'Дата', defaultVisible: true, defaultWidth: 140, sortKey: 'date' as const },
  { key: 'number', label: 'Номер счета', defaultVisible: true, defaultWidth: 170, sortKey: 'number' as const },
  { key: 'counterparty', label: 'Клиент / поставщик', defaultVisible: true, defaultWidth: 320, sortKey: 'client' as const },
  { key: 'amount', label: 'Сумма', defaultVisible: true, defaultWidth: 150, sortKey: 'amount' as const },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 180, sortKey: 'status' as const },
  { key: 'responsible', label: 'Ответственный', defaultVisible: true, defaultWidth: 190, sortKey: 'responsible' as const },
  { key: 'dueDate', label: 'Срок оплаты', defaultVisible: true, defaultWidth: 150, sortKey: 'dueDate' as const },
  { key: 'act', label: 'Акт', defaultVisible: true, defaultWidth: 210, sortKey: undefined },
  { key: 'type', label: 'Тип', defaultVisible: false, defaultWidth: 130, sortKey: undefined },
  { key: 'description', label: 'Описание', defaultVisible: false, defaultWidth: 280, sortKey: undefined },
  { key: 'createdAt', label: 'Создан', defaultVisible: false, defaultWidth: 150, sortKey: 'createdAt' as const },
] as const;
type InstallmentColumnKey = (typeof INSTALLMENT_COLUMNS)[number]['key'];
const INSTALLMENT_COLUMN_LABELS: Record<InstallmentColumnKey, string> = INSTALLMENT_COLUMNS.reduce((acc, column) => ({ ...acc, [column.key]: column.label }), {} as Record<InstallmentColumnKey, string>);

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

function getInvoiceCaseSummary(invoice: Invoice): string[] {
  if (invoice.clientCases?.length) return invoice.clientCases.map((item) => item.title?.trim() || `Дело #${item.id}`);
  if (invoice.clientCaseTitle?.trim()) return [invoice.clientCaseTitle];
  if (invoice.clientCaseId) return [`Дело #${invoice.clientCaseId}`];
  return [];
}

function getInvoiceActSummary(invoice: Invoice): string[] {
  if (invoice.acts?.length) {
    return invoice.acts.map((item) => {
      const parts: string[] = [];
      if (item.number) parts.push(`№${item.number}`);
      if (item.title) parts.push(item.title);
      return parts.join(' · ') || `Акт #${item.id}`;
    });
  }
  const legacy: string[] = [];
  if (invoice.actNumber) legacy.push(`№${invoice.actNumber}`);
  if (invoice.actTitle) legacy.push(invoice.actTitle);
  return legacy.length > 0 ? [legacy.join(' · ')] : [];
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

export function Installments({ defaultType, lockType = false }: AccountsProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const { t } = useTranslation();
  const canCreate = permissions.accounts.canCreate;
  const canEdit = permissions.accounts.canEdit;
  const canDelete = permissions.accounts.canDelete;
  const defaultPeriod = useMemo<PeriodFilter>(() => ({ mode: 'preset', preset: 'year_to_date' }), []);
  const storagePrefix = useMemo(() => `pp.installments.${user?.id ?? 'guest'}`, [user?.id]);
  const savedPeriod = useMemo(() => safeRead<unknown>(`${storagePrefix}.period`, null), [storagePrefix]);
  const [period, setPeriod] = useState<PeriodFilter>(() => normalizeStoredPeriod(savedPeriod, defaultPeriod));
  const periodRange = useMemo(() => resolvePeriod(period), [period]);
  const [statusFilter, setStatusFilter] = useState<'all' | PaymentStatus>('all');
  const [typeFilter, setTypeFilter] = useState<FilterType>(defaultType ?? 'All');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [modalMode, setModalMode] = useState<'create' | 'edit'>('create');
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);
  const { columnOrder, orderedVisibleColumns, visibleColumns, pageSize, setPageSize, draggingColumn, toggleColumn, moveColumn, handleColumnDragStart, handleColumnDragOver, handleColumnDrop, handleColumnDragEnd, handleResizeStart, getColumnWidth, getTableWidth, resetTableSettings } = usePersistedTableSettings<InstallmentColumnKey>({
    storageKey: `${storagePrefix}.tableSettings.v1`,
    settingsKey: 'installments_table_settings',
    columns: INSTALLMENT_COLUMNS.map((column) => ({ key: column.key, defaultVisible: column.defaultVisible, defaultWidth: column.defaultWidth })),
    defaultPageSize: 15,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 110,
  });
  const { invoices, loading, refreshing, error, summary, pagination, refresh } = useInvoices({
    from: periodRange.from || undefined,
    to: periodRange.to || undefined,
    status: statusFilter === 'all' ? undefined : statusFilter,
    search: debouncedSearch || undefined,
    type: typeFilter === 'All' ? undefined : typeFilter,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  useEffect(() => { setPage(1); }, [periodRange.from, periodRange.to, statusFilter, debouncedSearch, typeFilter, pageSize]);
  useEffect(() => { if (!defaultType) { setTypeFilter('All'); return; } setTypeFilter(defaultType); }, [defaultType]);
  useEffect(() => { setPeriod(normalizeStoredPeriod(savedPeriod, defaultPeriod)); }, [defaultPeriod, savedPeriod]);
  useEffect(() => { safeWrite(`${storagePrefix}.period`, period); }, [period, storagePrefix]);
  useEffect(() => { if (error) toast.error(error); }, [error, toast]);

  const totalPages = useMemo(() => !pagination.pageSize ? 1 : Math.max(1, Math.ceil((pagination.total ?? 0) / pagination.pageSize)), [pagination.total, pagination.pageSize]);
  useEffect(() => { if (page > totalPages) setPage(totalPages); }, [page, totalPages]);

  const summaryBuckets = useMemo(() => summary ?? { total: { amount: 0, count: 0 }, pending: { amount: 0, count: 0 }, paid: { amount: 0, count: 0 }, overdue: { amount: 0, count: 0 } }, [summary]);
  const summaryCards: SummaryCard[] = useMemo(() => [
    { key: 'total', title: t('invoiceSummaryTotal') ?? 'Сумма счетов всего', amount: summaryBuckets.total.amount, count: summaryBuckets.total.count, accent: 'border border-blue-100 bg-blue-50 text-blue-700', icon: WalletCards },
    { key: 'overdue', title: t('invoiceSummaryOverdue') ?? 'Просрочено', amount: summaryBuckets.overdue.amount, count: summaryBuckets.overdue.count, accent: 'border border-rose-100 bg-rose-50 text-rose-700', icon: AlertTriangle },
    { key: 'paid', title: t('invoiceSummaryPaid') ?? 'Оплачено', amount: summaryBuckets.paid.amount, count: summaryBuckets.paid.count, accent: 'border border-emerald-100 bg-emerald-50 text-emerald-700', icon: CheckCircle2 },
  ], [summaryBuckets, t]);
  const statusLabels: Record<PaymentStatus, string> = { Pending: t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается', Completed: t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачено', Overdue: t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочено', Processing: t('processingStatus') ?? 'Частично оплачено', Cancelled: t('cancelledStatus') ?? 'Отменено' };
  const statusClasses: Record<PaymentStatus, string> = { Pending: 'border border-amber-200 bg-amber-100 text-amber-700', Completed: 'border border-emerald-200 bg-emerald-100 text-emerald-700', Overdue: 'border border-rose-200 bg-rose-100 text-rose-700', Processing: 'border border-blue-200 bg-blue-100 text-blue-700', Cancelled: 'border border-slate-200 bg-slate-100 text-slate-600' };
  const typeLabels: Record<PaymentKind, string> = { Income: t('invoiceTypeIncome') ?? 'Доходные счета', Expense: t('invoiceTypeExpense') ?? 'Расходные счета' };
  const typeFilterLabels: Record<FilterType, string> = { All: t('invoiceTypeAll') ?? 'Все счета', Income: t('invoiceTypeIncome') ?? 'Доходные счета', Expense: t('invoiceTypeExpense') ?? 'Расходные счета' };
  const typeBadgeClasses: Record<PaymentKind, string> = { Income: 'border border-emerald-200 bg-emerald-100 text-emerald-700', Expense: 'border border-rose-200 bg-rose-100 text-rose-700' };
  const headingTitle = typeFilter === 'Expense' ? t('invoiceExpenseTitle') ?? 'Расходные счета' : typeFilter === 'Income' ? t('invoiceIncomeTitle') ?? t('invoicesTitle') ?? 'Доходные счета' : t('invoiceAllTitle') ?? t('accounts') ?? 'Все счета';
  const headingDescription = typeFilter === 'Expense' ? t('invoiceSectionDescriptionExpense') ?? 'Учитывайте входящие счета от поставщиков и контролируйте их оплату.' : typeFilter === 'Income' ? t('invoiceSectionDescriptionIncome') ?? t('invoiceSectionDescription') ?? 'Отслеживайте выставленные счета и контролируйте их оплату.' : t('invoiceSectionDescriptionAll') ?? t('invoiceSectionDescription') ?? 'Просматривайте все входящие и исходящие счета компании.';
  const columnsByKey = useMemo(() => Object.fromEntries(INSTALLMENT_COLUMNS.map((column) => [column.key, column])) as Record<InstallmentColumnKey, (typeof INSTALLMENT_COLUMNS)[number]>, []);
  const visibleColumnsOrdered = useMemo(() => orderedVisibleColumns.map((key) => columnsByKey[key]).filter(Boolean), [columnsByKey, orderedVisibleColumns]);
  const handleSort = (key: InvoicesSortKey) => setSortState((prev) => prev.key === key ? { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' } : { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' });
  const renderSortIcon = (sortKey?: InvoicesSortKey) => !sortKey ? null : sortState.key !== sortKey ? <ArrowUpDown className="h-4 w-4 text-slate-400" /> : sortState.direction === 'asc' ? <ArrowUp className="h-4 w-4 text-blue-600" /> : <ArrowDown className="h-4 w-4 text-blue-600" />;

  const ensureLookups = useCallback(async () => {
    if (!modalOpen) return;
    if (clients.length > 0 && actResponsibles.length > 0 && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse] = await Promise.all([clients.length > 0 ? Promise.resolve(clients) : apiService.getClients(), apiService.getActResponsibles()]);
      if (!clients.length) setClients(clientsResponse ?? []);
      setActResponsibles(responsiblesResponse ?? []);
      setLookupsError(null);
    } catch (err) {
      setLookupsError(extractDisplayErrorMessage(err, 'Не удалось загрузить справочники.'));
    } finally {
      setLookupsLoading(false);
    }
  }, [actResponsibles.length, clients, lookupsError, modalOpen]);

  useEffect(() => { if (modalOpen) void ensureLookups(); }, [ensureLookups, modalOpen]);
  const openCreateModal = () => { setModalMode('create'); setSelectedInvoice(null); setModalError(null); setModalOpen(true); };
  const openEditModal = (invoice: Invoice) => { setModalMode('edit'); setSelectedInvoice(invoice); setModalError(null); setModalOpen(true); };
  const closeModal = () => { setModalOpen(false); setSelectedInvoice(null); setModalError(null); };

  const submitInvoice = async (payload: InvoiceInput) => {
    setSubmitting(true);
    setModalError(null);
    try {
      if (modalMode === 'edit' && selectedInvoice) { await apiService.updateInvoice(selectedInvoice.id, payload); toast.success('Изменения по счету сохранены.', 'Счет обновлен'); }
      else { await apiService.createInvoice(payload); toast.success('Счет успешно создан.', 'Счет сохранен'); }
      await refresh();
      closeModal();
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить счет.');
      setModalError(message);
    } finally {
      setSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!canDelete) return;
    const approved = await confirm({ title: 'Подтверждение удаления', description: t('invoiceDeleteConfirm') ?? 'Удалить счёт?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' });
    if (!approved) return;
    setDeletingId(invoice.id);
    try {
      await apiService.deleteInvoice(invoice.id);
      toast.success('Счет удален.');
      if (invoices.length === 1 && page > 1) setPage((current) => Math.max(1, current - 1));
      else await refresh();
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить счет.'));
    } finally {
      setDeletingId(null);
    }
  };

  const handleResetFilters = () => { setPeriod(defaultPeriod); setStatusFilter('all'); setSearch(''); };
  const typeOptions: FilterType[] = ['All', 'Income', 'Expense'];

  const renderCell = (invoice: Invoice, columnKey: InstallmentColumnKey) => {
    const status = invoice.status ?? 'Pending';
    const type = (invoice.type ?? 'Income') as PaymentKind;
    const caseLines = getInvoiceCaseSummary(invoice);
    const actLines = getInvoiceActSummary(invoice);
    const primaryName = invoice.clientName ?? invoice.counterpartyName ?? '—';
    const extraLines: string[] = [];
    if (invoice.clientCompany) extraLines.push(invoice.clientCompany);
    if (caseLines.length > 0) extraLines.push(`${t('cases') ?? 'Дела'}: ${caseLines.join(', ')}`);
    switch (columnKey) {
      case 'date': return <span className="font-medium text-slate-900">{formatDate(invoice.date)}</span>;
      case 'number': return <span className="font-medium text-slate-900">{invoice.number}</span>;
      case 'counterparty':
        return (
          <div className="flex min-w-0 flex-col items-start gap-1 text-left">
            <div className="w-full truncate font-medium text-slate-900" title={primaryName}>
              {primaryName}
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <ClientStatusBadge status={invoice.clientStatus} />
              <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClasses[type]}`}>{typeLabels[type]}</span>
            </div>
            {extraLines.map((line, index) => (
              <div key={`${line}-${index}`} className="w-full truncate text-xs text-slate-500" title={line}>
                {line}
              </div>
            ))}
          </div>
        );
      case 'amount': return <span className="font-semibold text-slate-900">{formatCurrencySmart(invoice.amount).full}</span>;
      case 'status': return <div className="space-y-1"><span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${statusClasses[status]}`}>{statusLabels[status]}</span>{invoice.paymentStatusName ? <div className="text-xs text-slate-500">{invoice.paymentStatusName}</div> : null}</div>;
      case 'responsible': return invoice.responsibles?.length ? invoice.responsibles.map((item) => item.fullName).join(', ') : invoice.responsibleName || '—';
      case 'dueDate': return formatDate(invoice.dueDate);
      case 'act': return actLines.length > 0 ? <div className="space-y-1">{actLines.map((line) => <div key={line} className="font-medium text-slate-900">{line}</div>)}{invoice.actReference ? <div className="text-xs text-slate-500">{invoice.actReference}</div> : null}</div> : invoice.actReference ? <div className="text-xs text-slate-500">{invoice.actReference}</div> : '—';
      case 'type': return <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClasses[type]}`}>{typeLabels[type]}</span>;
      case 'description': return invoice.description?.trim() ? <div className="truncate text-slate-700" title={invoice.description}>{invoice.description}</div> : '—';
      case 'createdAt': return formatDate(invoice.createdAt);
      default: return '—';
    }
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
              <span className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${typeBadgeClasses[typeFilter === 'Expense' ? 'Expense' : 'Income']}`}>
                {typeFilterLabels[typeFilter === 'Expense' ? 'Expense' : 'Income']}
              </span>
            ) : (
              <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-gray-200 bg-white p-1 shadow-sm">
                {typeOptions.map((option) => {
                  const isActive = option === typeFilter;
                  const buttonLabel = option === 'Income' ? t('invoiceTypeIncomeShort') ?? typeFilterLabels[option] : option === 'Expense' ? t('invoiceTypeExpenseShort') ?? typeFilterLabels[option] : t('invoiceTypeAllShort') ?? typeFilterLabels[option];
                  return (
                    <button key={option} type="button" onClick={() => setTypeFilter(option)} className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${isActive ? 'bg-blue-600 text-white shadow' : 'bg-transparent text-gray-600 hover:bg-gray-100'}`}>
                      {buttonLabel}
                    </button>
                  );
                })}
              </div>
            )}
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <button type="button" onClick={() => setSettingsOpen((prev) => !prev)} className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium shadow-sm transition-colors ${settingsOpen ? 'border-blue-200 bg-blue-50 text-blue-700' : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'}`}>
              <SlidersHorizontal className="h-4 w-4" />
              {t('tableSettings') ?? 'Колонки'}
            </button>
            <button type="button" onClick={() => void refresh()} className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50">
              <RotateCcw className={refreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
              {t('invoiceRefresh') ?? 'Обновить'}
            </button>
            {canCreate ? (
              <button type="button" onClick={openCreateModal} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700">
                <Plus className="h-4 w-4" />
                {t('invoiceAdd') ?? 'Добавить счёт'}
              </button>
            ) : null}
          </div>
        </div>

        <div className="mb-6 grid gap-4 md:grid-cols-3">
          {summaryCards.map((card) => {
            const Icon = card.icon;
            return (
              <div key={card.key} className={`rounded-2xl p-5 shadow-sm ${card.accent}`}>
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">{card.title}</p>
                    <p className="mt-2 text-2xl font-semibold">{formatCurrencySmart(card.amount).full}</p>
                  </div>
                  <Icon className="h-10 w-10 opacity-80" />
                </div>
                <p className="mt-4 text-xs">{t('invoiceSummaryCount') ?? 'Количество счетов'}: {card.count}</p>
              </div>
            );
          })}
        </div>

        <div className="mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
          <div className="min-w-[240px] flex-1">
            <DatePeriodPicker
              value={period}
              onChange={setPeriod}
              label={t('invoiceFiltersPeriod') ?? 'Период'}
              accent="blue"
            />
          </div>

          <div>
            <SystemSelect value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as 'all' | PaymentStatus)} className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200">
              <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
              {STATUS_ORDER.map((status) => (
                <option key={status} value={status}>{statusLabels[status]}</option>
              ))}
            </SystemSelect>
          </div>

          <div className="relative min-w-[220px] flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
            <input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t('invoiceSearchPlaceholder') ?? 'Номер счёта, клиент или акт'} className="w-full rounded-lg border border-gray-300 py-2 pl-9 pr-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200" />
          </div>

          <button type="button" onClick={handleResetFilters} className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50">
            <RotateCcw className="h-4 w-4" />
            {t('invoiceFiltersReset') ?? 'Сбросить'}
          </button>
        </div>

        {settingsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={INSTALLMENT_COLUMNS.map((column) => ({ key: column.key, label: column.label }))}
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
                  <span>{t('rowsPerPage') ?? 'Строк на странице'}</span>
                  <SystemSelect value={pageSize} onChange={(event) => setPageSize(Number(event.target.value) || pageSize)} className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {PAGE_SIZE_OPTIONS.map((size) => <option key={size} value={size}>{size}</option>)}
                  </SystemSelect>
                </div>
              }
            />
          </div>
        ) : null}

        {summaryBuckets.pending.count > 0 ? (
          <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {t('invoiceSummaryPending') ?? 'Ожидает оплаты'}: {summaryBuckets.pending.count} · {formatCurrencySmart(summaryBuckets.pending.amount).full}
          </div>
        ) : null}

        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table
              className="w-full min-w-[1220px] table-fixed"
              style={{ width: getTableWidth(120, 1220), minWidth: '100%' }}
            >
              <colgroup>
                {orderedVisibleColumns.map((columnKey) => <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />)}
                <col style={{ width: 120 }} />
              </colgroup>
              <thead className="border-b border-slate-200 bg-slate-50">
                <tr>
                  {visibleColumnsOrdered.map((column) => (
                    <th key={column.key} style={{ width: getColumnWidth(column.key) }} className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wider ${draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : 'text-slate-700'}`} onDragOver={handleColumnDragOver} onDrop={(event) => handleColumnDrop(column.key, event)}>
                      <div className="flex items-center justify-between gap-2">
                        <button
                          type="button"
                          draggable
                          onDragStart={(event) => handleColumnDragStart(column.key, event)}
                          onDragEnd={handleColumnDragEnd}
                          className="inline-flex min-w-0 items-center gap-2 rounded p-1 text-left text-slate-700 transition-colors cursor-grab active:cursor-grabbing"
                          title="Перетащите, чтобы изменить порядок"
                          aria-label={`Перетащите колонку ${INSTALLMENT_COLUMN_LABELS[column.key]}, чтобы изменить порядок`}
                        >
                          <GripVertical className="h-4 w-4 shrink-0 text-slate-400" />
                          <span className="truncate">{INSTALLMENT_COLUMN_LABELS[column.key]}</span>
                        </button>
                        {column.sortKey ? (
                          <button
                            type="button"
                            onClick={() => handleSort(column.sortKey)}
                            className="inline-flex shrink-0 items-center gap-1 rounded p-1 text-slate-500 transition-colors hover:text-blue-700"
                            aria-label={`Сортировать по колонке ${INSTALLMENT_COLUMN_LABELS[column.key]}`}
                          >
                            {renderSortIcon(column.sortKey)}
                          </button>
                        ) : null}
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${INSTALLMENT_COLUMN_LABELS[column.key]}`}
                        className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                        onMouseDown={(event) => handleResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wider text-slate-700">{t('actions') ?? 'Действия'}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {loading ? (
                  <tr>
                    <td colSpan={visibleColumnsOrdered.length + 1} className="px-4 py-12 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin" />{t('loading') ?? 'Загрузка...'}</div>
                    </td>
                  </tr>
                ) : invoices.length === 0 ? (
                  <tr>
                    <td colSpan={visibleColumnsOrdered.length + 1} className="px-4 py-12 text-center text-gray-500">{t('invoiceNoData') ?? 'Счета не найдены'}</td>
                  </tr>
                ) : (
                  invoices.map((invoice) => (
                    <tr key={invoice.id} className="transition-colors hover:bg-slate-50">
                      {visibleColumnsOrdered.map((column) => (
                        <td key={column.key} style={{ width: getColumnWidth(column.key) }} className="max-w-0 overflow-hidden px-4 py-4 align-top">
                          {renderCell(invoice, column.key)}
                        </td>
                      ))}
                      <td className="px-4 py-4">
                        <div className="flex items-center justify-end gap-2">
                          {canEdit ? (
                            <button type="button" onClick={() => openEditModal(invoice)} className="rounded-lg p-2 text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900" title={t('edit') ?? 'Редактировать'} aria-label={t('edit') ?? 'Редактировать'}>
                              <Pencil className="h-4 w-4" />
                            </button>
                          ) : null}
                          {canDelete ? (
                            <button type="button" onClick={() => void removeInvoice(invoice)} className="rounded-lg p-2 text-rose-600 transition-colors hover:bg-rose-50 hover:text-rose-700" title={t('delete') ?? 'Удалить'} aria-label={t('delete') ?? 'Удалить'}>
                              {deletingId === invoice.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                            </button>
                          ) : null}
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <TablePagination page={page} totalPages={totalPages} totalItems={pagination.total ?? 0} pageSize={pageSize} pageSizeOptions={PAGE_SIZE_OPTIONS} onPageChange={setPage} onPageSizeChange={setPageSize} recordsLabel={t('recordsFound') ?? 'Найдено записей'} pageSizeLabel={t('rowsPerPage') ?? 'Строк на странице'} previousLabel={t('previous') ?? 'Назад'} nextLabel={t('next') ?? 'Вперед'} />
        </div>

        <InvoiceModal open={modalOpen} mode={modalMode} invoice={selectedInvoice} onClose={closeModal} onSubmit={submitInvoice} submitting={submitting} errorMessage={modalError} clients={clients} responsibles={actResponsibles} lookupsLoading={lookupsLoading} lookupsError={lookupsError} defaultClientId={undefined} defaultResponsibleId={user?.id} defaultType={typeFilter === 'All' ? undefined : typeFilter} lockType={lockType} />
      </div>
    </div>
  );
}
