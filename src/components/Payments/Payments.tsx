import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { JSX } from 'react';
import { Ban, ChevronDown, Loader2, Lock, Pencil, Plus, RotateCcw, Search, SlidersHorizontal, Trash2, X } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useToast } from '../../hooks/useToast';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { usePaymentsPaged } from '../../hooks/usePaymentsPaged';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import type { ClientRole, Payment, PaymentPayload, PaymentStatus } from '../../types';
import { PaymentsTable, type PaymentTableColumnKey } from '../Calendar/PaymentsTable';
import { PaymentModal } from '../Calendar/PaymentModal';
import { formatCurrencySmart } from '../../utils/formatters';
import { formatLocalYMD, fromInputToApiDate } from '../../utils/dateUtils';
import { useClients } from '../../hooks/useClients';
import { useDictionaries } from '../../hooks/useDictionaries';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { apiService } from '../../services/api';
import { isInvoiceManagedPayment } from '../../utils/invoicePayments';
import { parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';
import { buildCancelledPaymentPayload } from '../../utils/paymentActions';

type TypeFilter = 'all' | Payment['type'];

type StatusFilter = 'all' | PaymentStatus;
type ClientRoleFilter = 'all' | ClientRole;

interface PaymentsProps {
  onOpenClient?: (clientId: number, caseId?: number) => void;
  routePaymentId?: number | null;
  onRoutePaymentChange?: (paymentId: number | null) => void;
  defaultType?: TypeFilter;
  lockType?: boolean;
  titleKey?: string;
  subtitleKey?: string;
  lockedTypeMessageKey?: string;
}

type SubmitDTO = PaymentPayload | ({ id: number } & PaymentPayload);

const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Processing', 'Overdue', 'Completed', 'Cancelled'];

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const PAYMENT_PREVIEW_STATUS_STYLES: Record<PaymentStatus, string> = {
  Completed: 'bg-emerald-100 text-emerald-700',
  Pending: 'bg-amber-100 text-amber-700',
  Overdue: 'bg-violet-100 text-violet-700',
  Processing: 'bg-sky-100 text-sky-700',
  Cancelled: 'bg-slate-200 text-slate-700',
};

const PAYMENT_COLUMN_CONFIGS = [
  { key: 'date', defaultVisible: true, defaultWidth: 140 },
  { key: 'plannedDate', defaultVisible: false, defaultWidth: 150 },
  { key: 'client', defaultVisible: true, defaultWidth: 260 },
  { key: 'description', defaultVisible: true, defaultWidth: 300 },
  { key: 'type', defaultVisible: true, defaultWidth: 130 },
  { key: 'status', defaultVisible: true, defaultWidth: 150 },
  { key: 'category', defaultVisible: true, defaultWidth: 220 },
  { key: 'amount', defaultVisible: true, defaultWidth: 150 },
  { key: 'paidDate', defaultVisible: false, defaultWidth: 150 },
  { key: 'createdAt', defaultVisible: false, defaultWidth: 150 },
] satisfies ReadonlyArray<{
  key: PaymentTableColumnKey;
  defaultVisible?: boolean;
  defaultWidth?: number;
}>;

interface MultiSelectOption {
  value: string;
  label: string;
}

interface MultiSelectProps {
  options: MultiSelectOption[];
  value: string[];
  label: string;
  placeholder: string;
  emptyLabel: string;
  clearLabel: string;
  summaryLabel: (count: number) => string;
  onChange: (next: string[]) => void;
}

function MultiSelect({
  options,
  value,
  label,
  placeholder,
  emptyLabel,
  clearLabel,
  summaryLabel,
  onChange,
}: MultiSelectProps) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const handleClick = (event: MouseEvent) => {
      if (!containerRef.current) return;
      if (!containerRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handleClick);
    return () => {
      document.removeEventListener('mousedown', handleClick);
    };
  }, [open]);

  const toggle = useCallback(
    (nextValue: string) => {
      onChange(
        value.includes(nextValue)
          ? value.filter((item) => item !== nextValue)
          : [...value, nextValue],
      );
    },
    [onChange, value],
  );

  const handleClear = useCallback(() => {
    onChange([]);
    setOpen(false);
  }, [onChange]);

  const summary = value.length ? summaryLabel(value.length) : placeholder;

  return (
    <div className="relative" ref={containerRef}>
      <button
        type="button"
        onClick={() => setOpen((prev) => !prev)}
        className={`flex w-full items-center justify-between rounded-lg border border-slate-200 bg-white px-3 py-2 text-left text-sm text-slate-700 shadow-sm transition-colors hover:border-slate-300 ${
          open ? 'ring-2 ring-slate-300 ring-offset-1' : ''
        }`}>
        <span className="truncate">{summary}</span>
        <ChevronDown className={`ml-2 h-4 w-4 shrink-0 text-slate-400 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open ? (
        <div className="absolute z-20 mt-2 w-full min-w-[220px] max-w-sm rounded-xl border border-slate-200 bg-white p-2 shadow-xl">
          <div className="flex items-center justify-between border-b border-slate-100 pb-2">
            <span className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</span>
            {value.length ? (
              <button
                type="button"
                onClick={handleClear}
                className="text-xs font-medium text-blue-600 hover:text-blue-700">
                {clearLabel}
              </button>
            ) : null}
          </div>
          <div className="mt-2 max-h-60 space-y-1 overflow-y-auto pr-1">
            {options.length === 0 ? (
              <p className="px-2 py-3 text-center text-xs text-slate-400">{emptyLabel}</p>
            ) : (
              options.map((option) => {
                const checked = value.includes(option.value);
                return (
                  <label
                    key={option.value}
                    className={`flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-sm transition-colors ${
                      checked ? 'bg-slate-100 text-slate-900' : 'hover:bg-slate-50 text-slate-600'
                    }`}>
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-slate-300 text-slate-700 focus:ring-slate-500"
                      checked={checked}
                      onChange={() => toggle(option.value)}
                    />
                    <span className="truncate">{option.label}</span>
                  </label>
                );
              })
            )}
          </div>
        </div>
      ) : null}
    </div>
  );
}

function parseAmountInput(raw: string): number | null {
  if (!raw) return null;
  const normalized = raw.replace(/[\s\u00a0]/g, '').replace(',', '.');
  const parsed = Number(normalized);
  return Number.isNaN(parsed) ? null : parsed;
}

function defaultFromDate() {
  const now = new Date();
  return formatLocalYMD(new Date(now.getFullYear(), now.getMonth(), 1));
}

function defaultToDate() {
  return formatLocalYMD(new Date());
}

function formatCountLabel(template: string, count: number) {
  return template.replace('{{count}}', count.toLocaleString('ru-RU'));
}

export function Payments({
  onOpenClient,
  routePaymentId,
  onRoutePaymentChange,
  defaultType,
  lockType = false,
  titleKey,
  subtitleKey,
  lockedTypeMessageKey,
}: PaymentsProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const paymentsPermissions = permissions.payments;
  const { t, formatDate } = useTranslation();
  const { clients } = useClients();
  const { dealTypes, incomeTypes, paymentSources } = useDictionaries();

  const [fromDate, setFromDate] = useState(() => defaultFromDate());
  const [toDate, setToDate] = useState(() => defaultToDate());
  const [selectedClients, setSelectedClients] = useState<string[]>([]);
  const [clientRoleFilter, setClientRoleFilter] = useState<ClientRoleFilter>('all');
  const [selectedCategories, setSelectedCategories] = useState<string[]>([]);
  const [minAmount, setMinAmount] = useState('');
  const [maxAmount, setMaxAmount] = useState('');
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const [typeFilter, setTypeFilter] = useState<TypeFilter>(defaultType ?? 'all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [page, setPage] = useState(1);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<Payment['type']>(
    defaultType === 'Expense' ? 'Expense' : 'Income',
  );
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [routePaymentPreviewOpen, setRoutePaymentPreviewOpen] = useState(false);
  const [routePaymentPreviewId, setRoutePaymentPreviewId] = useState<number | null>(null);
  const [routePaymentPreview, setRoutePaymentPreview] = useState<Payment | null>(null);
  const [routePaymentPreviewLoading, setRoutePaymentPreviewLoading] = useState(false);
  const [routePaymentPreviewError, setRoutePaymentPreviewError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [loadTimeoutReached, setLoadTimeoutReached] = useState(false);

  const initialFromRef = useRef(fromDate);
  const initialToRef = useRef(toDate);
  const routePaymentRequestRef = useRef(0);
  const [autoRetryScheduled, setAutoRetryScheduled] = useState(false);
  const autoRetryAttemptsRef = useRef(0);
  const autoRetryTimerRef = useRef<number | null>(null);
  const isMountedRef = useRef(true);
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
    resetTableSettings,
  } = usePersistedTableSettings<PaymentTableColumnKey>({
    storageKey: `pp.payments_table_settings.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'payments_table_settings',
    columns: PAYMENT_COLUMN_CONFIGS,
    defaultPageSize: 25,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 120,
  });

  const minAmountValue = useMemo(() => parseAmountInput(minAmount), [minAmount]);
  const maxAmountValue = useMemo(() => parseAmountInput(maxAmount), [maxAmount]);
  const appliedTypeFilter: TypeFilter = lockType ? (defaultType ?? 'all') : typeFilter;

  const {
    payments,
    total,
    summary,
    loading,
    refreshing,
    refresh,
    error,
  } = usePaymentsPaged({
    from: fromInputToApiDate(fromDate),
    to: fromInputToApiDate(toDate),
    clientIds: selectedClients,
    clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
    categoryTokens: selectedCategories,
    type: appliedTypeFilter === 'all' ? undefined : appliedTypeFilter,
    status: statusFilter === 'all' ? undefined : statusFilter,
    minAmount: minAmountValue,
    maxAmount: maxAmountValue,
    search: debouncedSearch || undefined,
    sortBy: 'date',
    sortDir: 'desc',
    page,
    pageSize,
  });

  const clientOptions = useMemo<MultiSelectOption[]>(
    () =>
      clients
        .filter((client) => client?.id && client?.name)
        .map((client) => ({
          value: String(client.id),
          label: client.name ?? `#${client.id}`,
        }))
        .sort((a, b) => a.label.localeCompare(b.label, 'ru')),
    [clients],
  );

  const categoryOptions = useMemo<MultiSelectOption[]>(() => {
    const options: MultiSelectOption[] = [];
    const dealPrefix = t('paymentsCategoryDealPrefix') ?? t('dealType') ?? 'Сделка';
    const incomePrefix = t('paymentsCategoryIncomePrefix') ?? t('incomeType') ?? 'Доход';
    const sourcePrefix = t('paymentsCategorySourcePrefix') ?? t('paymentSource') ?? 'Источник';

    dealTypes.forEach((deal) => {
      options.push({ value: `deal-${deal.id}`, label: `${dealPrefix}: ${deal.name}` });
    });
    incomeTypes.forEach((income) => {
      options.push({ value: `income-${income.id}`, label: `${incomePrefix}: ${income.name}` });
    });
    paymentSources.forEach((source) => {
      options.push({ value: `source-${source.id}`, label: `${sourcePrefix}: ${source.name}` });
    });

    return options.sort((a, b) => a.label.localeCompare(b.label, 'ru'));
  }, [dealTypes, incomeTypes, paymentSources, t]);

  useEffect(() => {
    if (typeof defaultType === 'undefined') {
      return;
    }
    setTypeFilter(defaultType);
    if (defaultType === 'Income' || defaultType === 'Expense') {
      setModalType(defaultType);
    }
  }, [defaultType]);

  useEffect(() => {
    setLastUpdated(new Date());
  }, [payments]);

  useEffect(() => {
    if (loading && payments.length === 0) {
      setLoadTimeoutReached(false);
      const timer = window.setTimeout(() => {
        setLoadTimeoutReached(true);
      }, 15000);
      return () => {
        window.clearTimeout(timer);
      };
    }
    setLoadTimeoutReached(false);
    return undefined;
  }, [loading, payments.length]);

  const resetRoutePaymentPreview = useCallback(() => {
    routePaymentRequestRef.current += 1;
    setRoutePaymentPreviewOpen(false);
    setRoutePaymentPreviewId(null);
    setRoutePaymentPreview(null);
    setRoutePaymentPreviewLoading(false);
    setRoutePaymentPreviewError(null);
  }, []);

  const closeRoutePaymentPreview = useCallback(() => {
    resetRoutePaymentPreview();
    onRoutePaymentChange?.(null);
  }, [onRoutePaymentChange, resetRoutePaymentPreview]);

  useEffect(() => {
    if (!routePaymentId && routePaymentPreviewOpen) {
      resetRoutePaymentPreview();
    }
  }, [resetRoutePaymentPreview, routePaymentId, routePaymentPreviewOpen]);

  useEffect(() => {
    if (!(routePaymentId && routePaymentId > 0)) {
      return;
    }

    const requestId = routePaymentRequestRef.current + 1;
    routePaymentRequestRef.current = requestId;
    setRoutePaymentPreviewOpen(true);
    setRoutePaymentPreviewId(routePaymentId);
    setRoutePaymentPreview(null);
    setRoutePaymentPreviewError(null);
    setRoutePaymentPreviewLoading(true);

    void (async () => {
      try {
        const payment = await apiService.getPayment(routePaymentId);
        if (routePaymentRequestRef.current !== requestId) {
          return;
        }
        setRoutePaymentPreview(payment);
      } catch (error) {
        if (routePaymentRequestRef.current !== requestId) {
          return;
        }
        setRoutePaymentPreviewError(
          extractDisplayErrorMessage(error, 'Не удалось загрузить платеж.'),
        );
      } finally {
        if (routePaymentRequestRef.current === requestId) {
          setRoutePaymentPreviewLoading(false);
        }
      }
    })();
  }, [routePaymentId]);

  const paymentColumns = useMemo(
    () => [
      { key: 'date' as const, label: t('date') ?? 'Дата' },
      { key: 'plannedDate' as const, label: 'Плановая дата' },
      { key: 'client' as const, label: t('clients') ?? 'Клиенты' },
      { key: 'description' as const, label: t('description') ?? 'Описание' },
      { key: 'type' as const, label: t('type') ?? 'Тип' },
      { key: 'status' as const, label: t('status') ?? t('statusFilter') ?? 'Статус' },
      { key: 'category' as const, label: t('category') ?? 'Категория' },
      { key: 'amount' as const, label: t('amount') ?? 'Сумма' },
      { key: 'paidDate' as const, label: 'Дата оплаты' },
      { key: 'createdAt' as const, label: t('createdAt') ?? 'Создан' },
    ],
    [t],
  );

  const paymentColumnsByKey = useMemo(
    () =>
      Object.fromEntries(paymentColumns.map((column) => [column.key, column])) as Record<
        PaymentTableColumnKey,
        (typeof paymentColumns)[number]
      >,
    [paymentColumns],
  );

  const visiblePaymentColumns = useMemo(
    () => orderedVisibleColumns.map((key) => paymentColumnsByKey[key]).filter(Boolean),
    [orderedVisibleColumns, paymentColumnsByKey],
  );

  const showPermissionNotice = useCallback(
    (message: string) => {
      toast.error(message, 'Недостаточно прав');
    },
    [toast],
  );

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'В обработке',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const selectedRoutePaymentInstallment = useMemo(
    () => (routePaymentPreview ? parseInstallmentPaymentMetadata(routePaymentPreview.systemNotes) : null),
    [routePaymentPreview],
  );

  const availableStatuses = useMemo(() => {
    return STATUS_ORDER;
  }, []);

  const effectiveDefaultType: TypeFilter = defaultType ?? 'all';
  const headingText =
    (titleKey ? t(titleKey) : t('payments')) ?? t('payments') ?? 'Платежи';
  const subtitleText =
    subtitleKey
      ? t(subtitleKey) ?? t('paymentsSubtitle') ?? 'Журнал всех платежей'
      : t('paymentsSubtitle') ?? 'Журнал всех платежей';
  const lockedTypeMessage = lockType
    ? (lockedTypeMessageKey ? t(lockedTypeMessageKey) : undefined) ??
      (effectiveDefaultType === 'Income'
        ? t('paymentsTypeLockedIncome') ?? 'Отображаются только доходные платежи'
        : effectiveDefaultType === 'Expense'
        ? t('paymentsTypeLockedExpense') ?? 'Отображаются только расходные платежи'
        : t('paymentsTypeLockedGeneric') ?? 'Фильтр по типу закреплён')
    : null;

  const searchQuery = debouncedSearch.toLowerCase();

  const filterSignature = useMemo(
    () =>
      [
        fromDate,
        toDate,
        selectedClients.join(','),
        clientRoleFilter,
        selectedCategories.join(','),
        minAmount || '',
        maxAmount || '',
        statusFilter,
        searchQuery,
        appliedTypeFilter,
      ].join('|'),
    [
      appliedTypeFilter,
      clientRoleFilter,
      fromDate,
      maxAmount,
      minAmount,
      searchQuery,
      selectedCategories,
      selectedClients,
      statusFilter,
      toDate,
    ],
  );

  const requestSignature = useMemo(
    () => `${filterSignature}|${page}|${pageSize}`,
    [filterSignature, page, pageSize],
  );

  useEffect(
    () => () => {
      isMountedRef.current = false;
      if (autoRetryTimerRef.current) {
        window.clearTimeout(autoRetryTimerRef.current);
        autoRetryTimerRef.current = null;
      }
    },
    [],
  );

  useEffect(() => {
    autoRetryAttemptsRef.current = 0;
    setAutoRetryScheduled(false);
    if (autoRetryTimerRef.current) {
      window.clearTimeout(autoRetryTimerRef.current);
      autoRetryTimerRef.current = null;
    }
  }, [requestSignature]);

  useEffect(() => {
    if (!error || payments.length > 0) {
      autoRetryAttemptsRef.current = 0;
      if (autoRetryTimerRef.current) {
        window.clearTimeout(autoRetryTimerRef.current);
        autoRetryTimerRef.current = null;
      }
      if (autoRetryScheduled) {
        setAutoRetryScheduled(false);
      }
      return;
    }

    if (autoRetryAttemptsRef.current >= 1 || autoRetryScheduled) {
      return;
    }

    autoRetryAttemptsRef.current += 1;
    setAutoRetryScheduled(true);
    autoRetryTimerRef.current = window.setTimeout(async () => {
      autoRetryTimerRef.current = null;
      await refresh();
      if (isMountedRef.current) {
        setAutoRetryScheduled(false);
      }
    }, 800);
  }, [autoRetryScheduled, error, payments.length, refresh]);

  useEffect(() => {
    setPage(1);
  }, [filterSignature, pageSize]);

  const totalPages = useMemo(
    () => (pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1),
    [pageSize, total],
  );

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const activeFiltersCount = useMemo(() => {
    let count = 0;
    if (fromDate !== initialFromRef.current || toDate !== initialToRef.current) {
      count += 1;
    }
    if (selectedClients.length) {
      count += 1;
    }
    if (clientRoleFilter !== 'all') {
      count += 1;
    }
    if (selectedCategories.length) {
      count += 1;
    }
    if (statusFilter !== 'all') {
      count += 1;
    }
    if (searchQuery) {
      count += 1;
    }
    if (minAmount.trim()) {
      count += 1;
    }
    if (maxAmount.trim()) {
      count += 1;
    }
    if (!lockType && typeFilter !== 'all') {
      count += 1;
    }
    return count;
  }, [
    clientRoleFilter,
    fromDate,
    lockType,
    maxAmount,
    minAmount,
    searchQuery,
    selectedCategories,
    selectedClients,
    statusFilter,
    toDate,
    typeFilter,
  ]);

  const summaryCards: JSX.Element[] = [];
  if (appliedTypeFilter === 'all' || appliedTypeFilter === 'Income') {
    summaryCards.push(
      <div key="income" className="rounded-xl border border-emerald-100 bg-emerald-50 p-4 shadow-sm">
        <div className="text-sm font-medium text-emerald-700">
          {t('paymentsSummaryIncome') ?? t('incomePlural') ?? 'Доходы'}
        </div>
        <div className="mt-2 text-2xl font-semibold text-emerald-800">
          {formatCurrencySmart(summary.income).full}
        </div>
        <div className="mt-1 text-xs text-emerald-700/80">
          {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.incomeCount)}
        </div>
      </div>,
    );
  }
  if (appliedTypeFilter === 'all' || appliedTypeFilter === 'Expense') {
    summaryCards.push(
      <div key="expense" className="rounded-xl border border-rose-100 bg-rose-50 p-4 shadow-sm">
        <div className="text-sm font-medium text-rose-700">
          {t('paymentsSummaryExpense') ?? t('expensePlural') ?? 'Расходы'}
        </div>
        <div className="mt-2 text-2xl font-semibold text-rose-700">
          {formatCurrencySmart(summary.expense).full}
        </div>
        <div className="mt-1 text-xs text-rose-700/80">
          {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.expenseCount)}
        </div>
      </div>,
    );
  }

  summaryCards.push(
    <div key="balance" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="text-sm font-medium text-slate-600">{t('paymentsSummaryBalance') ?? 'Сальдо'}</div>
      <div className={`mt-2 text-2xl font-semibold ${summary.balance >= 0 ? 'text-emerald-600' : 'text-rose-600'}`}>
        {formatCurrencySmart(summary.balance).full}
      </div>
      <div className="mt-1 text-xs text-slate-500">
        {t('paymentsTotalCount') ?? 'Всего платежей'}: {summary.total.toLocaleString('ru-RU')}
      </div>
    </div>,
  );

  summaryCards.push(
    <div key="total" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="text-sm font-medium text-slate-600">{t('paymentsTotalCount') ?? 'Всего платежей'}</div>
      <div className="mt-2 text-3xl font-semibold text-slate-900">
        {summary.total.toLocaleString('ru-RU')}
      </div>
      <div className="mt-1 text-xs text-slate-500">
        {t('recordsFound') ?? 'Найдено записей:'} {total.toLocaleString('ru-RU')}
      </div>
    </div>,
  );

  const summaryGridClass = [
    'grid w-full gap-4 grid-cols-1',
    summaryCards.length >= 3
      ? 'sm:grid-cols-2 xl:grid-cols-3'
      : summaryCards.length === 2
      ? 'sm:grid-cols-2 xl:grid-cols-2'
      : 'sm:grid-cols-1 xl:grid-cols-1',
    'xl:max-w-3xl xl:self-start xl:pl-4',
  ].join(' ');

  const handleFromDateChange = useCallback(
    (value: string) => {
      setFromDate(value);
      if (toDate && value && value > toDate) {
        setToDate(value);
      }
    },
    [toDate],
  );

  const handleToDateChange = useCallback(
    (value: string) => {
      setToDate(value);
      if (fromDate && value && value < fromDate) {
        setFromDate(value);
      }
    },
    [fromDate],
  );

  const handleResetFilters = useCallback(() => {
    setFromDate(defaultFromDate());
    setToDate(defaultToDate());
    setSelectedClients([]);
    setClientRoleFilter('all');
    setSelectedCategories([]);
    setMinAmount('');
    setMaxAmount('');
    setStatusFilter('all');
    setSearch('');
    setTypeFilter(defaultType ?? 'all');
  }, [defaultType]);

  const handleAddPayment = useCallback(
    (type: Payment['type']) => {
      if (!paymentsPermissions.canCreate) {
        showPermissionNotice(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
        return false;
      }
      setModalType(type);
      setEditingPayment(null);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canCreate, showPermissionNotice, t],
  );

  const handleEditPayment = useCallback(
    (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return false;
      }
      setEditingPayment(payment);
      setModalType(payment.type);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canEdit, showPermissionNotice, t],
  );

  const handleDeletePayment = useCallback(
    async (payment: Payment) => {
      if (isInvoiceManagedPayment(payment)) {
        toast.error('Счет и график рассрочки удаляются через раздел "Счета".', t('payments') ?? 'Платежи');
        return false;
      }
      if (!paymentsPermissions.canDelete) {
        showPermissionNotice(t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.');
        return false;
      }
      const confirmed = await confirm({ title: 'Подтверждение удаления', description: t('confirmDeletePayment') ?? 'Удалить платёж?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' });
      if (!confirmed) {
        return false;
      }
      try {
        await apiService.deletePayment(payment.id);
        toast.success(t('paymentDeleted') ?? 'Платёж удалён.', t('payments') ?? 'Платежи');
        if (payments.length === 1 && page > 1) {
          setPage((prev) => Math.max(1, prev - 1));
        } else {
          await refresh();
        }
        return true;
      } catch (error) {
        const message = extractDisplayErrorMessage(error, t('paymentsDeleteError') ?? 'Не удалось удалить платёж.');
        toast.error(message, t('payments') ?? 'Платежи');
        return false;
      }
    },
    [confirm, page, payments.length, paymentsPermissions.canDelete, refresh, showPermissionNotice, t, toast],
  );

  const handleCancelPayment = useCallback(
    async (payment: Payment) => {
      if (isInvoiceManagedPayment(payment)) {
        toast.error('Счет и график рассрочки управляют этим платежом. Измените статус через счет.', t('payments') ?? 'Платежи');
        return false;
      }
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return false;
      }
      if (payment.status === 'Cancelled') {
        return false;
      }

      const confirmed = await confirm({
        title: 'Аннулировать платеж',
        description: 'Платеж будет переведен в статус "Отменен", оплаченная сумма будет сброшена. Продолжить?',
        confirmLabel: 'Аннулировать',
        cancelLabel: 'Отмена',
        variant: 'danger',
      });
      if (!confirmed) return false;

      try {
        await apiService.updatePayment(payment.id, buildCancelledPaymentPayload(payment));
        toast.success('Платеж аннулирован.', t('payments') ?? 'Платежи');
        await refresh();
        return true;
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Не удалось аннулировать платеж.');
        toast.error(message, t('payments') ?? 'Платежи');
        return false;
      }
    },
    [confirm, paymentsPermissions.canEdit, refresh, showPermissionNotice, t, toast],
  );

  const handleSubmit = useCallback(
    async (payload: SubmitDTO) => {
      try {
        if ('id' in payload) {
          await apiService.updatePayment(payload.id, payload);
          await refresh();
          toast.success(t('paymentSaved') ?? 'Платёж обновлён.', t('payments') ?? 'Платежи');
        } else {
          await apiService.createPayment(payload);
          toast.success(t('paymentSaved') ?? 'Платёж создан.', t('payments') ?? 'Платежи');
          if (page !== 1) {
            setPage(1);
          } else {
            await refresh();
          }
        }

        setIsModalOpen(false);
        setEditingPayment(null);
      } catch (error) {
        const message = extractDisplayErrorMessage(error, t('paymentsSaveError') ?? 'Не удалось сохранить платёж.');
        toast.error(message, t('payments') ?? 'Платежи');
        throw new Error(message);
      }
    },
    [page, refresh, t, toast],
  );

  const handleCloseModal = useCallback(() => {
    setIsModalOpen(false);
    setEditingPayment(null);
  }, []);

  const handleRefresh = useCallback(async () => {
    await refresh();
  }, [refresh]);

  const lastUpdatedText = useMemo(() => {
    if (!lastUpdated) {
      return null;
    }
    const template = t('paymentsLastUpdated') ?? 'Обновлено {{time}}';
    const time = lastUpdated.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    return template.replace('{{time}}', time);
  }, [lastUpdated, t]);

  const typeButtonClass = useCallback(
    (target: TypeFilter) => {
      const common = 'px-4 py-2 text-sm font-medium rounded-full border transition-colors focus:outline-none';
      if (target === 'all') {
        const active = typeFilter === 'all';
        return [
          common,
          active
            ? 'bg-slate-900 text-white border-slate-900 shadow'
            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300',
        ].join(' ');
      }
      if (target === 'Income') {
        const active = typeFilter === 'Income';
        return [
          common,
          active
            ? 'bg-emerald-600 text-white border-emerald-600 shadow'
            : 'bg-emerald-50 text-emerald-700 border-emerald-200 hover:border-emerald-300',
        ].join(' ');
      }
      const active = typeFilter === 'Expense';
      return [
        common,
        active
          ? 'bg-rose-600 text-white border-rose-600 shadow'
          : 'bg-rose-50 text-rose-700 border-rose-200 hover:border-rose-300',
      ].join(' ');
    },
    [typeFilter],
  );

  const showErrorState = Boolean(error) && payments.length === 0 && !autoRetryScheduled;
  const showTimeoutPlaceholder =
    loadTimeoutReached && payments.length === 0 && !error && !autoRetryScheduled;
  const showAutoRetrySpinner = autoRetryScheduled && payments.length === 0;
  const initialLoading = loading && payments.length === 0 && !loadTimeoutReached && !error;
  const showIncomeButton =
    paymentsPermissions.canCreate &&
    (!lockType || effectiveDefaultType === 'Income' || effectiveDefaultType === 'all');
  const showExpenseButton =
    paymentsPermissions.canCreate &&
    (!lockType || effectiveDefaultType === 'Expense' || effectiveDefaultType === 'all');

  return (
    <div className="p-8">
      <div className="mx-auto max-w-none space-y-6">
        <div className="flex flex-col gap-6 xl:flex-row xl:items-start xl:justify-between">
          <div className="flex-1 space-y-4">
            <div>
              <h1 className="text-3xl font-bold text-slate-900">{headingText}</h1>
              {subtitleText ? (
                <p className="mt-2 max-w-2xl text-slate-600">{subtitleText}</p>
              ) : null}
              {lastUpdatedText ? (
                <p className="mt-2 text-xs text-slate-500">{lastUpdatedText}</p>
              ) : null}
            </div>

            <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
              <div className="flex flex-wrap items-center gap-2">
                {lockType ? (
                  <span className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-600">
                    <Lock className="h-3.5 w-3.5 text-slate-500" />
                    {lockedTypeMessage}
                  </span>
                ) : (
                  <>
                    <button type="button" className={typeButtonClass('all')} onClick={() => setTypeFilter('all')}>
                      {t('paymentsFilterAll') ?? 'Все'}
                    </button>
                    <button type="button" className={typeButtonClass('Income')} onClick={() => setTypeFilter('Income')}>
                      {t('paymentsFilterIncome') ?? t('incomePlural') ?? 'Доходы'}
                    </button>
                    <button type="button" className={typeButtonClass('Expense')} onClick={() => setTypeFilter('Expense')}>
                      {t('paymentsFilterExpense') ?? t('expensePlural') ?? 'Расходы'}
                    </button>
                  </>
                )}
              </div>

              <div className="flex flex-wrap items-center gap-3">
                <select
                  className="min-w-[180px] rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-slate-400 focus:outline-none"
                  value={statusFilter}
                  onChange={(event) => {
                    const value = event.target.value as StatusFilter;
                    setStatusFilter(value);
                  }}>
                  <option value="all">{t('paymentsStatusAll') ?? t('allStatuses') ?? 'Все статусы'}</option>
                  {availableStatuses.map((status) => (
                    <option key={status} value={status}>
                      {statusLabels[status] ?? status}
                    </option>
                  ))}
                </select>

                <div className="relative">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder={t('paymentsSearchPlaceholder') ?? t('search') ?? 'Поиск'}
                    className="w-64 rounded-lg border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none"
                  />
                </div>
              </div>
            </div>
          </div>

          <div className={summaryGridClass}>{summaryCards}</div>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={() => setFiltersOpen((prev) => !prev)}
            aria-expanded={filtersOpen}
            aria-controls="payments-filters-panel"
            className={`inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium transition-colors focus:outline-none ${
              filtersOpen
                ? 'border-slate-900 bg-slate-900 text-white shadow-sm'
                : 'border-slate-200 bg-white text-slate-700 hover:border-slate-300 hover:text-slate-900'
            }`}>
            <SlidersHorizontal className="h-4 w-4" />
            <span>
              {filtersOpen
                ? t('paymentsFiltersHide') ?? t('hideFilters') ?? 'Скрыть фильтры'
                : t('paymentsFiltersShow') ?? 'Показать фильтры'}
            </span>
            {activeFiltersCount ? (
              <span
                className={`inline-flex h-5 min-w-[1.5rem] items-center justify-center rounded-full px-2 text-xs font-semibold ${
                  filtersOpen ? 'bg-white text-slate-900' : 'bg-slate-900 text-white'
                }`}>
                {activeFiltersCount}
              </span>
            ) : null}
          </button>

          <button
            type="button"
            onClick={() => setSettingsOpen((prev) => !prev)}
            aria-expanded={settingsOpen}
            aria-controls="payments-settings-panel"
            className={`inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium transition-colors focus:outline-none ${
              settingsOpen
                ? 'border-slate-900 bg-slate-900 text-white shadow-sm'
                : 'border-slate-200 bg-white text-slate-700 hover:border-slate-300 hover:text-slate-900'
            }`}>
            <SlidersHorizontal className="h-4 w-4" />
            <span>{settingsOpen ? 'Скрыть настройки таблицы' : 'Настроить таблицу'}</span>
          </button>

          <button
            type="button"
            onClick={handleRefresh}
            className={`inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition-colors hover:border-slate-300 hover:text-slate-900 ${
              refreshing ? 'cursor-wait opacity-70' : ''
            }`}
            disabled={refreshing}>
            <RotateCcw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />
            {t('paymentsRefresh') ?? t('invoiceRefresh') ?? 'Обновить'}
          </button>

          {paymentsPermissions.canCreate && (showIncomeButton || showExpenseButton) ? (
            <div className="flex flex-wrap gap-3">
              {showIncomeButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Income')}
                  className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700 focus:outline-none">
                  <Plus className="h-4 w-4" />
                  {t('addIncome') ?? 'Добавить доход'}
                </button>
              ) : null}
              {showExpenseButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Expense')}
                  className="inline-flex items-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-rose-700 focus:outline-none">
                  <Plus className="h-4 w-4" />
                  {t('addExpense') ?? 'Добавить расход'}
                </button>
              ) : null}
            </div>
          ) : null}
        </div>

        {filtersOpen ? (
          <div
            id="payments-filters-panel"
            className="space-y-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm font-medium text-slate-600">
                {t('paymentsFiltersTitle') ?? 'Дополнительные фильтры'}
              </p>
              <button
                type="button"
                onClick={handleResetFilters}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-slate-600 transition-colors hover:border-slate-300 hover:text-slate-900">
                <RotateCcw className="h-3.5 w-3.5" />
                {t('paymentsFiltersReset') ?? 'Сбросить фильтры'}
              </button>
            </div>

            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
              <div className="flex flex-col gap-1">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterDateFrom') ?? t('dateFrom') ?? 'Дата от'}
                </label>
                <input
                  type="date"
                  value={fromDate}
                  max={toDate || undefined}
                  onChange={(event) => handleFromDateChange(event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-slate-400 focus:outline-none"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterDateTo') ?? t('dateTo') ?? 'Дата до'}
                </label>
                <input
                  type="date"
                  value={toDate}
                  min={fromDate || undefined}
                  onChange={(event) => handleToDateChange(event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-slate-400 focus:outline-none"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label htmlFor="payments-client-role-filter" className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  Роль клиента
                </label>
                <select
                  id="payments-client-role-filter"
                  value={clientRoleFilter}
                  onChange={(event) => setClientRoleFilter(event.target.value as ClientRoleFilter)}
                  className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-slate-400 focus:outline-none"
                >
                  <option value="all">Любая связь</option>
                  <option value="counterparty">Контрагент / поставщик</option>
                  <option value="expenseClient">Клиент расхода</option>
                </select>
              </div>

              <div className="flex flex-col gap-1 md:col-span-2">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterClientsLabel') ?? t('clients') ?? 'Клиенты'}
                </label>
                <MultiSelect
                  options={clientOptions}
                  value={selectedClients}
                  label={t('paymentsFilterClientsLabel') ?? t('clients') ?? 'Клиенты'}
                  placeholder={t('paymentsFilterClientsPlaceholder') ?? 'Все клиенты'}
                  emptyLabel={t('paymentsFilterClientsEmpty') ?? 'Клиенты не найдены'}
                  clearLabel={t('paymentsFiltersClear') ?? 'Очистить'}
                  summaryLabel={(count) =>
                    formatCountLabel(
                      t('paymentsFiltersSelectedCount') ?? 'Выбрано: {{count}}',
                      count,
                    )
                  }
                  onChange={setSelectedClients}
                />
              </div>

              <div className="flex flex-col gap-1 md:col-span-2">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterCategoriesLabel') ?? t('category') ?? 'Категории'}
                </label>
                <MultiSelect
                  options={categoryOptions}
                  value={selectedCategories}
                  label={t('paymentsFilterCategoriesLabel') ?? t('category') ?? 'Категории'}
                  placeholder={t('paymentsFilterCategoriesPlaceholder') ?? 'Все категории'}
                  emptyLabel={t('paymentsFilterCategoriesEmpty') ?? 'Категории не найдены'}
                  clearLabel={t('paymentsFiltersClear') ?? 'Очистить'}
                  summaryLabel={(count) =>
                    formatCountLabel(
                      t('paymentsFiltersSelectedCount') ?? 'Выбрано: {{count}}',
                      count,
                    )
                  }
                  onChange={setSelectedCategories}
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterAmountMin') ?? 'Сумма от'}
                </label>
                <input
                  type="number"
                  inputMode="decimal"
                  step="0.01"
                  value={minAmount}
                  onChange={(event) => setMinAmount(event.target.value)}
                  placeholder="0"
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-slate-400 focus:outline-none"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  {t('paymentsFilterAmountMax') ?? 'Сумма до'}
                </label>
                <input
                  type="number"
                  inputMode="decimal"
                  step="0.01"
                  value={maxAmount}
                  onChange={(event) => setMaxAmount(event.target.value)}
                  placeholder="0"
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm focus:border-slate-400 focus:outline-none"
                />
              </div>
            </div>
          </div>
        ) : null}

        {settingsOpen ? (
          <div id="payments-settings-panel">
            <TableColumnSettings
              columns={paymentColumns.map((column) => ({
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
                  <span>{t('rowsPerPage') ?? 'Строк на странице'}</span>
                  <select
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </select>
                </div>
              }
            />
          </div>
        ) : null}

        <div className="space-y-4">
          {initialLoading || showAutoRetrySpinner ? (
            <div className="flex justify-center py-12">
              <div className="flex flex-col items-center gap-3 text-slate-500">
                <div className="h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
                <span className="text-sm">
                  {showAutoRetrySpinner
                    ? t('paymentsRetrying') ?? 'Повторная попытка загрузки...'
                    : t('loading') ?? 'Загрузка...'}
                </span>
              </div>
            </div>
          ) : showErrorState ? (
            <div className="rounded-xl border border-red-200 bg-red-50 p-6 text-center text-sm text-red-700">
              <p>{t('paymentsLoadError') ?? 'Не удалось загрузить платежи.'}</p>
              <button
                type="button"
                onClick={handleRefresh}
                className="mt-3 inline-flex items-center justify-center rounded-md border border-red-300 bg-white/80 px-3 py-1.5 font-medium text-red-700 transition-colors hover:bg-red-100">
                {t('paymentsRetry') ?? 'Повторить попытку'}
              </button>
            </div>
          ) : showTimeoutPlaceholder ? (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-6 text-center text-sm text-amber-700">
              <p>
                {t('paymentsSlowLoadWarning') ??
                  'Загрузка занимает больше обычного. Попробуйте обновить список чуть позже.'}
              </p>
              <button
                type="button"
                onClick={handleRefresh}
                className="mt-3 inline-flex items-center justify-center rounded-md border border-amber-300 bg-white/80 px-3 py-1.5 font-medium text-amber-700 transition-colors hover:bg-amber-100">
                {t('paymentsRetry') ?? 'Повторить попытку'}
              </button>
            </div>
          ) : (
            <>
              <PaymentsTable
                payments={payments}
                onEditPayment={handleEditPayment}
                onOpenClient={onOpenClient}
                onAddPayment={handleAddPayment}
                onDeletePayment={handleDeletePayment}
                canAdd={paymentsPermissions.canCreate}
                canEdit={paymentsPermissions.canEdit}
                canDelete={paymentsPermissions.canDelete}
                canEditPayment={() => true}
                canDeletePayment={(payment) => !isInvoiceManagedPayment(payment)}
                columns={visiblePaymentColumns}
                draggingColumn={draggingColumn}
                onColumnDragStart={handleColumnDragStart}
                onColumnDragOver={handleColumnDragOver}
                onColumnDrop={handleColumnDrop}
                onColumnDragEnd={handleColumnDragEnd}
                onResizeStart={handleResizeStart}
                getColumnWidth={getColumnWidth}
              />
              <TablePagination
                page={page}
                totalPages={totalPages}
                totalItems={total}
                pageSize={pageSize}
                pageSizeOptions={PAGE_SIZE_OPTIONS}
                onPageChange={setPage}
                onPageSizeChange={setPageSize}
                recordsLabel={t('recordsFound') ?? 'Найдено записей'}
                pageSizeLabel={t('rowsPerPage') ?? 'Строк на странице'}
                previousLabel={t('previous') ?? 'Назад'}
                nextLabel={t('next') ?? 'Вперед'}
              />
            </>
          )}
        </div>

        {!initialLoading &&
        !showAutoRetrySpinner &&
        !showErrorState &&
        !showTimeoutPlaceholder &&
        payments.length === 0 ? (
          <div className="rounded-xl border border-slate-200 bg-white p-6 text-center text-slate-500">
            {t('paymentsEmpty') ?? t('noPaymentsForFilter') ?? 'Платежи не найдены'}
          </div>
        ) : null}
      </div>

      {routePaymentPreviewOpen ? (
        <div className="fixed inset-0 z-[1300] flex items-center justify-center bg-slate-900/60 p-4">
          <div
            data-testid="payments-route-payment-preview"
            className="w-full max-w-2xl rounded-2xl bg-white shadow-2xl"
          >
            <div className="flex items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 className="text-lg font-semibold text-slate-900">
                  {`Платеж #${routePaymentPreview?.id ?? routePaymentPreviewId ?? '—'}`}
                </h3>
                <p className="mt-1 text-sm text-slate-500">Просмотр платежа из журнала.</p>
              </div>
              <button
                type="button"
                onClick={closeRoutePaymentPreview}
                className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
                aria-label={t('close') ?? 'Закрыть'}
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-4 px-5 py-4">
              {routePaymentPreviewError ? (
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                  {routePaymentPreviewError}
                </div>
              ) : null}

              {routePaymentPreviewLoading ? (
                <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Обновляем данные платежа...
                </div>
              ) : null}

              {routePaymentPreview ? (
                <>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Сумма</div>
                      <div className="mt-2 text-xl font-semibold text-slate-900">
                        {formatCurrencySmart(routePaymentPreview.amount ?? 0).full}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Статус</div>
                      <div className="mt-2 flex flex-wrap items-center gap-2">
                        <span
                          className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                            PAYMENT_PREVIEW_STATUS_STYLES[routePaymentPreview.status] ?? PAYMENT_PREVIEW_STATUS_STYLES.Pending
                          }`}
                        >
                          {statusLabels[routePaymentPreview.status] ?? routePaymentPreview.status}
                        </span>
                        <span className="inline-flex items-center rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-700">
                          {routePaymentPreview.type === 'Expense' ? 'Расходный платеж' : 'Доходный платеж'}
                        </span>
                        {selectedRoutePaymentInstallment ? (
                          <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2.5 py-1 text-xs font-semibold text-violet-700">
                            {`Рассрочка ${selectedRoutePaymentInstallment.sequence}/${Math.max(
                              selectedRoutePaymentInstallment.months,
                              selectedRoutePaymentInstallment.sequence,
                            )}`}
                          </span>
                        ) : null}
                      </div>
                    </div>
                  </div>

                  <div className="rounded-xl border border-slate-200 px-4 py-3">
                    <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Описание</div>
                    <div className="mt-2 text-sm leading-6 text-slate-700">
                      {routePaymentPreview.description?.trim() || 'Без описания'}
                    </div>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-2">
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Плановая дата</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {formatDate(routePaymentPreview.plannedDate || routePaymentPreview.date) || '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Дата оплаты</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {(routePaymentPreview.paidDate || routePaymentPreview.lastPaymentDate)
                          ? formatDate(routePaymentPreview.paidDate || routePaymentPreview.lastPaymentDate || '')
                          : '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">
                        {routePaymentPreview.type === 'Expense' ? 'Поставщик' : 'Клиент'}
                      </div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {routePaymentPreview.client?.name || '—'}
                      </div>
                    </div>
                    {routePaymentPreview.type === 'Expense' ? (
                      <div className="rounded-xl border border-slate-200 px-4 py-3">
                        <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Клиент расхода</div>
                        <div className="mt-2 text-sm font-medium text-slate-900">
                          {routePaymentPreview.expenseClient?.name || '—'}
                        </div>
                      </div>
                    ) : null}
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">
                        {routePaymentPreview.type === 'Expense' ? 'Дело расхода' : 'Дело'}
                      </div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {routePaymentPreview.clientCase?.title || '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Категория</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {routePaymentPreview.dealType?.name
                          || routePaymentPreview.incomeType?.name
                          || routePaymentPreview.paymentSource?.name
                          || '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Платежный документ</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {routePaymentPreview.paymentOrderNumber?.trim()
                          ? routePaymentPreview.paymentDocumentDate
                            ? `${routePaymentPreview.paymentOrderNumber} от ${formatDate(routePaymentPreview.paymentDocumentDate)}`
                            : routePaymentPreview.paymentOrderNumber
                          : routePaymentPreview.paymentDocumentDate
                          ? formatDate(routePaymentPreview.paymentDocumentDate)
                          : '—'}
                      </div>
                    </div>
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Связанные документы</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">
                        {`Счет: ${
                          routePaymentPreview.account?.trim()
                          || (routePaymentPreview.invoiceId ? `ID ${routePaymentPreview.invoiceId}` : '—')
                        }, договор: ${
                          routePaymentPreview.contractId ? `ID ${routePaymentPreview.contractId}` : '—'
                        }`}
                      </div>
                    </div>
                  </div>

                  {routePaymentPreview.notes?.trim() ? (
                    <div className="rounded-xl border border-slate-200 px-4 py-3">
                      <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Комментарий</div>
                      <div className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">
                        {routePaymentPreview.notes}
                      </div>
                    </div>
                  ) : null}
                </>
              ) : null}
            </div>

            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 px-5 py-4">
              <div className="flex flex-wrap items-center gap-2">
                {routePaymentPreview && paymentsPermissions.canEdit ? (
                  <button
                    type="button"
                    onClick={() => {
                      handleEditPayment(routePaymentPreview);
                      closeRoutePaymentPreview();
                    }}
                    className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
                  >
                    <Pencil className="h-4 w-4" />
                    Редактировать
                  </button>
                ) : null}
                {routePaymentPreview && paymentsPermissions.canEdit && routePaymentPreview.status !== 'Cancelled' && !isInvoiceManagedPayment(routePaymentPreview) ? (
                  <button
                    type="button"
                    onClick={async () => {
                      const cancelled = await handleCancelPayment(routePaymentPreview);
                      if (cancelled) closeRoutePaymentPreview();
                    }}
                    className="inline-flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-2 text-sm font-medium text-amber-700 transition hover:bg-amber-100"
                  >
                    <Ban className="h-4 w-4" />
                    Аннулировать
                  </button>
                ) : null}
                {routePaymentPreview && paymentsPermissions.canDelete && !isInvoiceManagedPayment(routePaymentPreview) ? (
                  <button
                    type="button"
                    onClick={async () => {
                      const deleted = await handleDeletePayment(routePaymentPreview);
                      if (deleted) closeRoutePaymentPreview();
                    }}
                    className="inline-flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 transition hover:bg-red-100"
                  >
                    <Trash2 className="h-4 w-4" />
                    Удалить
                  </button>
                ) : null}
              </div>
              <button
                type="button"
                onClick={closeRoutePaymentPreview}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Закрыть
              </button>
            </div>
          </div>
        </div>
      ) : null}

      <PaymentModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSubmit={handleSubmit}
        onCreateRemainder={apiService.createPayment}
        payment={editingPayment}
        type={modalType}
        onDelete={
          paymentsPermissions.canDelete && (!editingPayment || !isInvoiceManagedPayment(editingPayment))
            ? async (id) => {
                try {
                  await apiService.deletePayment(id);
                  toast.success(t('paymentDeleted') ?? 'Платёж удалён.', t('payments') ?? 'Платежи');
                  setIsModalOpen(false);
                  setEditingPayment(null);
                  if (payments.length === 1 && page > 1) {
                    setPage((prev) => Math.max(1, prev - 1));
                  } else {
                    await refresh();
                  }
                } catch (error) {
                  const message = extractDisplayErrorMessage(error, t('paymentsDeleteError') ?? 'Не удалось удалить платёж.');
                  toast.error(message, t('payments') ?? 'Платежи');
                  throw new Error(message);
                }
              }
            : undefined
        }
      />
    </div>
  );
}
