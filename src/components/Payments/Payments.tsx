import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { JSX } from 'react';
import { AlertTriangle, Ban, CalendarPlus, ChevronDown, Download, Loader2, Lock, Pencil, Plus, RotateCcw, Search, SlidersHorizontal, Trash2, X } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useToast } from '../../hooks/useToast';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { usePaymentsPaged } from '../../hooks/usePaymentsPaged';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import type { ClientBalanceTransaction, ClientCase, ClientRole, PartialPaymentRemainderCandidate, Payment, PaymentAdvanceReviewDiagnosticsResponse, PaymentAdvanceReviewStatus, PaymentPayload, PaymentStatus } from '../../types';
import { PaymentsTable, type PaymentTableColumnKey } from '../Calendar/PaymentsTable';
import { PaymentModal } from '../Calendar/PaymentModal';
import { formatCurrencySmart } from '../../utils/formatters';
import { todayYMDInTimeZone, fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { useClients } from '../../hooks/useClients';
import { useDictionaries } from '../../hooks/useDictionaries';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import { StatusChangeModal, type StatusChangeOption } from '../ui/StatusChangeModal';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { apiService } from '../../services/api';
import { isInvoiceManagedPayment, isPartialRemainderSourcePayment, isTransferredRemainderPayment } from '../../utils/invoicePayments';
import { parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';
import { buildCancelledPaymentPayload } from '../../utils/paymentActions';
import {
  notifyPaymentAdvanceReviewChanged,
  PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY,
} from '../../utils/paymentAdvanceReview';

type TypeFilter = 'all' | Payment['type'];

type StatusFilter = 'all' | PaymentStatus;
type AdvanceReviewFilter = 'all' | PaymentAdvanceReviewStatus;
type ClientRoleFilter = 'all' | ClientRole;

const ADVANCE_REVIEW_FILTER_INTENT_KEY = PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY;

interface PaymentsProps {
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenPayment?: (paymentId: number, paymentType: Payment['type']) => void;
  routePaymentId?: number | null;
  onRoutePaymentChange?: (paymentId: number | null) => void;
  onRoutePaymentExit?: () => void;
  initialCreatePayment?: PaymentCreatePrefill | null;
  onInitialCreatePaymentHandled?: (key: number) => void;
  defaultType?: TypeFilter;
  lockType?: boolean;
  titleKey?: string;
  subtitleKey?: string;
  lockedTypeMessageKey?: string;
}

type SubmitDTO = PaymentPayload | ({ id: number } & PaymentPayload);

type AdvanceAllocationState = {
  source: Payment;
  targets: Payment[];
  selectedTargetId: number | null;
  amount: string;
  balance: number;
  loading: boolean;
  submitting: boolean;
  error: string | null;
  ownerKind: 'client' | 'legalEntity';
  ownerId: number;
  ownerName: string;
};

const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Processing', 'Overdue', 'Completed', 'Cancelled'];

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const PAYMENT_PREVIEW_STATUS_STYLES: Record<PaymentStatus, string> = {
  Completed: 'bg-emerald-100 text-emerald-700',
  Pending: 'bg-amber-100 text-amber-700',
  Overdue: 'bg-violet-100 text-violet-700',
  Processing: 'bg-sky-100 text-sky-700',
  Cancelled: 'bg-slate-200 text-slate-700',
};

function resolvePaymentOutstandingAmount(payment: Pick<Payment, 'amount' | 'paidAmount' | 'outstandingAmount'>) {
  if (typeof payment.outstandingAmount === 'number' && Number.isFinite(payment.outstandingAmount)) {
    return Math.max(payment.outstandingAmount, 0);
  }
  const amount = Number(payment.amount ?? 0);
  const paid = Number(payment.paidAmount ?? 0);
  return Math.max(amount - Math.min(paid, amount), 0);
}

function parseMoneyInput(value: string) {
  const normalized = value.replace(/\s/g, '').replace(',', '.');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatMoneyInput(value: number) {
  return value > 0 ? String(Number(value.toFixed(2))) : '';
}

function resolveAdvanceSourceAvailableAmount(transactions: ClientBalanceTransaction[], sourcePaymentId: number) {
  const appliedPrefix = `payment-unallocated-advance-apply:${sourcePaymentId}:`;
  const credited = transactions
    .filter((item) =>
      item.paymentId === sourcePaymentId &&
      item.type === 'UnallocatedAdvance' &&
      item.direction === 'Credit')
    .reduce((sum, item) => sum + Number(item.amount ?? 0), 0);
  const applied = transactions
    .filter((item) =>
      item.type === 'CreditApplied' &&
      item.direction === 'Debit' &&
      Boolean(item.externalKey?.startsWith(appliedPrefix)))
    .reduce((sum, item) => sum + Number(item.amount ?? 0), 0);
  return Math.max(Number((credited - applied).toFixed(2)), 0);
}

function resolveAdvanceOwner(payment: Payment) {
  const clientId = payment.clientId ?? payment.client?.id ?? 0;
  if (clientId > 0) {
    return {
      kind: 'client' as const,
      id: clientId,
      name: payment.client?.name ?? payment.counterpartyName ?? 'Клиент не указан',
    };
  }

  const legalEntityId = payment.legalEntityId ?? 0;
  if (legalEntityId > 0) {
    return {
      kind: 'legalEntity' as const,
      id: legalEntityId,
      name: payment.legalEntityName ?? payment.counterpartyName ?? 'Юридическое лицо не указано',
    };
  }

  return null;
}

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
                    <SystemCheckbox
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

interface PaymentCreatePrefill {
  key: number;
  type: Payment['type'];
  clientId?: number | null;
  legalEntityId?: number | null;
  clientCaseId?: number | null;
  casesPrefetch?: ClientCase[];
}

interface ToolbarIconButtonProps {
  label: string;
  icon: JSX.Element;
  active?: boolean;
  disabled?: boolean;
  badge?: number;
  warning?: boolean;
  onClick: () => void;
}

function ToolbarIconButton({
  label,
  icon,
  active = false,
  disabled = false,
  badge,
  warning = false,
  onClick,
}: ToolbarIconButtonProps) {
  const toneClass = active
    ? warning
      ? 'border-amber-300 bg-amber-100 text-amber-800 shadow-sm'
      : 'border-blue-300 bg-blue-50 text-blue-700 shadow-sm'
    : warning
      ? 'border-amber-300 bg-amber-50 text-amber-700 hover:border-amber-400 hover:bg-amber-100'
      : 'border-slate-200 bg-white text-slate-600 hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900';

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={label}
      className={`relative inline-flex h-10 w-10 items-center justify-center rounded-lg border text-sm font-medium transition-colors focus:outline-none disabled:cursor-not-allowed disabled:opacity-60 ${toneClass}`}
    >
      {icon}
      <span className="sr-only">{label}</span>
      {badge ? (
        <span className={`absolute -right-1 -top-1 inline-flex h-5 min-w-5 items-center justify-center rounded-full px-1.5 text-[10px] font-semibold text-white ${warning ? 'bg-amber-600' : 'bg-slate-900'}`}>
          {badge}
        </span>
      ) : null}
    </button>
  );
}

function parseAmountInput(raw: string): number | null {
  if (!raw) return null;
  const normalized = raw.replace(/[\s\u00a0]/g, '').replace(',', '.');
  const parsed = Number(normalized);
  return Number.isNaN(parsed) ? null : parsed;
}

function defaultFromDate(timeZoneId?: string | null) {
  const today = todayYMDInTimeZone(timeZoneId);
  return `${today.slice(0, 8)}01`;
}

function defaultToDate(timeZoneId?: string | null) {
  return todayYMDInTimeZone(timeZoneId);
}

function formatCountLabel(template: string, count: number) {
  return template.replace('{{count}}', count.toLocaleString('ru-RU'));
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

export function Payments({
  onOpenClient,
  onOpenPayment,
  routePaymentId,
  onRoutePaymentChange,
  onRoutePaymentExit,
  initialCreatePayment,
  onInitialCreatePaymentHandled,
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
  const canOpenLinkedClients = permissions.clients?.canView === true;
  const canOpenLinkedCases = permissions.cases?.canView === true;
  const canViewLinkedInvoices = permissions.accounts?.canView === true;
  const canCreateLinkedInvoices = canViewLinkedInvoices && permissions.accounts?.canCreate === true;
  const { t, formatDate } = useTranslation();
  const { clients } = useClients();
  const { dealTypes, incomeTypes, paymentSources } = useDictionaries();
  const profileTimeZoneId = useUserTimeZone();
  const defaultFrom = defaultFromDate(profileTimeZoneId);
  const defaultTo = defaultToDate(profileTimeZoneId);

  const [fromDate, setFromDate] = useState(() => defaultFrom);
  const [toDate, setToDate] = useState(() => defaultTo);
  const [selectedClientId, setSelectedClientId] = useState('');
  const [clientRoleFilter, setClientRoleFilter] = useState<ClientRoleFilter>('all');
  const [selectedCategories, setSelectedCategories] = useState<string[]>([]);
  const [minAmount, setMinAmount] = useState('');
  const [maxAmount, setMaxAmount] = useState('');
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [paymentsCsvExporting, setPaymentsCsvExporting] = useState(false);
  const [paymentsExcelExporting, setPaymentsExcelExporting] = useState(false);

  const [typeFilter, setTypeFilter] = useState<TypeFilter>(defaultType ?? 'all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [advanceReviewFilter, setAdvanceReviewFilter] = useState<AdvanceReviewFilter>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [page, setPage] = useState(1);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<Payment['type']>(
    defaultType === 'Expense' ? 'Expense' : 'Income',
  );
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [paymentCreatePrefill, setPaymentCreatePrefill] = useState<PaymentCreatePrefill | null>(null);
  const [routePaymentPreviewOpen, setRoutePaymentPreviewOpen] = useState(false);
  const [routePaymentPreviewId, setRoutePaymentPreviewId] = useState<number | null>(null);
  const [routePaymentPreview, setRoutePaymentPreview] = useState<Payment | null>(null);
  const [routePaymentPreviewLoading, setRoutePaymentPreviewLoading] = useState(false);
  const [routePaymentPreviewError, setRoutePaymentPreviewError] = useState<string | null>(null);
  const [returnAfterRoutePaymentEdit, setReturnAfterRoutePaymentEdit] = useState(false);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [loadTimeoutReached, setLoadTimeoutReached] = useState(false);
  const [remainderCandidatesOpen, setRemainderCandidatesOpen] = useState(false);
  const [remainderCandidatesLoading, setRemainderCandidatesLoading] = useState(false);
  const [remainderCandidates, setRemainderCandidates] = useState<PartialPaymentRemainderCandidate[]>([]);
  const [remainderCandidatesTotal, setRemainderCandidatesTotal] = useState(0);
  const [remainderCandidatesTotalAmount, setRemainderCandidatesTotalAmount] = useState(0);
  const [remainderCandidateDates, setRemainderCandidateDates] = useState<Record<number, string>>({});
  const [statusChangePayment, setStatusChangePayment] = useState<Payment | null>(null);
  const [statusChangeValue, setStatusChangeValue] = useState<PaymentStatus>('Pending');
  const [statusChangeReason, setStatusChangeReason] = useState('');
  const [statusChangeSubmitting, setStatusChangeSubmitting] = useState(false);
  const [statusChangeError, setStatusChangeError] = useState<string | null>(null);
  const [advanceAllocation, setAdvanceAllocation] = useState<AdvanceAllocationState | null>(null);
  const [advanceDiagnosticsQuery, setAdvanceDiagnosticsQuery] = useState('');
  const [advanceDiagnosticsDate, setAdvanceDiagnosticsDate] = useState('');
  const [advanceDiagnosticsAmount, setAdvanceDiagnosticsAmount] = useState('');
  const [advanceDiagnosticsResult, setAdvanceDiagnosticsResult] = useState<PaymentAdvanceReviewDiagnosticsResponse | null>(null);
  const [advanceDiagnosticsLoading, setAdvanceDiagnosticsLoading] = useState(false);
  const [advanceDiagnosticsError, setAdvanceDiagnosticsError] = useState<string | null>(null);

  useEffect(() => {
    const pendingFilter = window.sessionStorage.getItem(ADVANCE_REVIEW_FILTER_INTENT_KEY);
    if (
      pendingFilter === 'RequiresDecision' ||
      pendingFilter === 'AcceptedAsAdvance' ||
      pendingFilter === 'Allocated' ||
      pendingFilter === 'Ignored' ||
      pendingFilter === 'Refunded'
    ) {
      setAdvanceReviewFilter(pendingFilter);
      setStatusFilter('all');
      setPage(1);
      window.sessionStorage.removeItem(ADVANCE_REVIEW_FILTER_INTENT_KEY);
    }
  }, []);

  const initialFromRef = useRef(fromDate);
  const initialToRef = useRef(toDate);
  const defaultPeriodRef = useRef({ from: defaultFrom, to: defaultTo });
  const routePaymentRequestRef = useRef(0);
  const toastRef = useRef(toast);
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
    getTableWidth,
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

  useEffect(() => {
    toastRef.current = toast;
  }, [toast]);

  useEffect(() => {
    const previous = defaultPeriodRef.current;
    setFromDate((current) => (current === previous.from ? defaultFrom : current));
    setToDate((current) => (current === previous.to ? defaultTo : current));
    if (initialFromRef.current === previous.from) {
      initialFromRef.current = defaultFrom;
    }
    if (initialToRef.current === previous.to) {
      initialToRef.current = defaultTo;
    }
    defaultPeriodRef.current = { from: defaultFrom, to: defaultTo };
  }, [defaultFrom, defaultTo]);

  const minAmountValue = useMemo(() => parseAmountInput(minAmount), [minAmount]);
  const maxAmountValue = useMemo(() => parseAmountInput(maxAmount), [maxAmount]);
  const appliedTypeFilter: TypeFilter = lockType ? (defaultType ?? 'all') : typeFilter;
  const selectedClientIds = useMemo(() => (selectedClientId ? [selectedClientId] : []), [selectedClientId]);

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
    clientIds: selectedClientIds,
    clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
    categoryTokens: selectedCategories,
    type: appliedTypeFilter === 'all' ? undefined : appliedTypeFilter,
    status: statusFilter === 'all' ? undefined : statusFilter,
    advanceReviewStatus: advanceReviewFilter === 'all' ? undefined : advanceReviewFilter,
    minAmount: minAmountValue,
    maxAmount: maxAmountValue,
    search: debouncedSearch || undefined,
    sortBy: 'date',
    sortDir: 'desc',
    page,
    pageSize,
  });

  const paymentExportParams = useMemo(
    () => ({
      from: fromInputToApiDate(fromDate),
      to: fromInputToApiDate(toDate),
      clientIds: selectedClientIds.join(',') || undefined,
      clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
      categoryTokens: selectedCategories.join(',') || undefined,
      type: appliedTypeFilter === 'all' ? undefined : appliedTypeFilter,
      status: statusFilter === 'all' ? undefined : statusFilter,
      advanceReviewStatus: advanceReviewFilter === 'all' ? undefined : advanceReviewFilter,
      minAmount: minAmountValue ?? undefined,
      maxAmount: maxAmountValue ?? undefined,
      search: debouncedSearch || undefined,
      sortBy: 'date' as const,
      sortDir: 'desc' as const,
    }),
    [
      appliedTypeFilter,
      advanceReviewFilter,
      clientRoleFilter,
      debouncedSearch,
      fromDate,
      maxAmountValue,
      minAmountValue,
      selectedCategories,
      selectedClientIds,
      statusFilter,
      toDate,
    ],
  );

  const clientLookupOptions = useMemo<LookupSelectOption[]>(
    () =>
      clients
        .filter((client) => client?.id && client?.name)
        .map((client) => ({
          value: String(client.id),
          label: client.name ?? `#${client.id}`,
          keywords: [String(client.id), client.name ?? ''],
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
  }, [defaultFrom, defaultTo, defaultType]);

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

  const dismissRoutePaymentPreview = useCallback(() => {
    resetRoutePaymentPreview();
    onRoutePaymentChange?.(null);
  }, [onRoutePaymentChange, resetRoutePaymentPreview]);

  const closeRoutePaymentPreview = useCallback(() => {
    dismissRoutePaymentPreview();
    onRoutePaymentExit?.();
  }, [dismissRoutePaymentPreview, onRoutePaymentExit]);

  const finishRoutePaymentEdit = useCallback(() => {
    if (!returnAfterRoutePaymentEdit) {
      return;
    }

    setReturnAfterRoutePaymentEdit(false);
    onRoutePaymentExit?.();
  }, [onRoutePaymentExit, returnAfterRoutePaymentEdit]);

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
      Completed: 'Оплачено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: 'Частично оплачено',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const advanceReviewLabels: Record<PaymentAdvanceReviewStatus, string> = useMemo(
    () => ({
      None: 'Без контроля аванса',
      RequiresDecision: 'Требуют решения',
      AcceptedAsAdvance: 'Приняты как аванс',
      Allocated: 'Распределены',
      Ignored: 'Игнорируются',
      Refunded: 'Возвращены',
    }),
    [],
  );

  const selectedRoutePaymentInstallment = useMemo(
    () => (routePaymentPreview ? parseInstallmentPaymentMetadata(routePaymentPreview.systemNotes) : null),
    [routePaymentPreview],
  );

  const availableStatuses = useMemo(() => {
    return STATUS_ORDER;
  }, []);

  const paymentStatusOptions = useMemo<StatusChangeOption<PaymentStatus>[]>(
    () => availableStatuses.map((status) => ({
      value: status,
      label: statusLabels[status] ?? status,
      className: PAYMENT_PREVIEW_STATUS_STYLES[status],
      description:
        status === 'Completed'
          ? 'Платеж станет оплаченным, оплаченная сумма будет приравнена к полной сумме.'
          : status === 'Cancelled'
            ? 'Платеж будет отменен, оплаченная сумма и дата оплаты будут очищены.'
            : status === 'Overdue'
              ? 'Платеж будет отмечен как просроченный.'
              : status === 'Processing'
                ? 'Платеж будет отмечен как частично оплаченный.'
                : 'Платеж будет отмечен как ожидающий оплаты.',
    })),
    [availableStatuses, statusLabels],
  );

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
        selectedClientId,
        clientRoleFilter,
        selectedCategories.join(','),
        minAmount || '',
        maxAmount || '',
        statusFilter,
        advanceReviewFilter,
        searchQuery,
        appliedTypeFilter,
      ].join('|'),
    [
      appliedTypeFilter,
      advanceReviewFilter,
      clientRoleFilter,
      fromDate,
      maxAmount,
      minAmount,
      searchQuery,
      selectedCategories,
      selectedClientId,
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

  const advanceReviewPageSummary = useMemo(() => {
    const requiresDecision = payments.filter((payment) => payment.advanceReviewStatus === 'RequiresDecision');
    const accepted = payments.filter((payment) => payment.advanceReviewStatus === 'AcceptedAsAdvance');
    const requiresDecisionAmount = requiresDecision.reduce(
      (sum, payment) => sum + Math.max(Number(payment.paidAmount || payment.amount || 0), 0),
      0,
    );
    const acceptedAmount = accepted.reduce(
      (sum, payment) => sum + Math.max(Number(payment.paidAmount || payment.amount || 0), 0),
      0,
    );

    return {
      requiresDecisionCount: requiresDecision.length,
      requiresDecisionAmount,
      acceptedCount: accepted.length,
      acceptedAmount,
    };
  }, [payments]);

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
    if (selectedClientId) {
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
    if (advanceReviewFilter !== 'all') {
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
    advanceReviewFilter,
    fromDate,
    lockType,
    maxAmount,
    minAmount,
    searchQuery,
    selectedCategories,
    selectedClientId,
    statusFilter,
    toDate,
    typeFilter,
  ]);

  const showAdvanceReviewMonitoring = useCallback((status: PaymentAdvanceReviewStatus) => {
    setAdvanceReviewFilter(status);
    setStatusFilter('all');
    setPage(1);
  }, []);

  const openPaymentPreviewById = useCallback(async (paymentId: number) => {
    const requestId = routePaymentRequestRef.current + 1;
    routePaymentRequestRef.current = requestId;
    setRoutePaymentPreviewOpen(true);
    setRoutePaymentPreviewId(paymentId);
    setRoutePaymentPreview(null);
    setRoutePaymentPreviewError(null);
    setRoutePaymentPreviewLoading(true);

    try {
      const payment = await apiService.getPayment(paymentId);
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
  }, []);

  const handleRunAdvanceDiagnostics = useCallback(async () => {
    const query = advanceDiagnosticsQuery.trim();
    const date = advanceDiagnosticsDate.trim();
    const rawAmount = advanceDiagnosticsAmount.trim();
    const amount = rawAmount
      ? Number(rawAmount.replace(/\s+/g, '').replace(',', '.'))
      : undefined;

    if (amount !== undefined && (!Number.isFinite(amount) || amount < 0)) {
      setAdvanceDiagnosticsError('Укажите корректную сумму платежа.');
      return;
    }

    if (!query && !date && amount === undefined) {
      setAdvanceDiagnosticsError('Укажите клиента, дату или сумму спорного платежа.');
      return;
    }

    setAdvanceDiagnosticsLoading(true);
    setAdvanceDiagnosticsError(null);

    try {
      const result = await apiService.getPaymentAdvanceDiagnostics({
        q: query || undefined,
        from: date || undefined,
        to: date || undefined,
        amount,
        amountTolerance: amount === undefined ? undefined : 0.01,
        limit: 20,
      });
      setAdvanceDiagnosticsResult(result);
    } catch (error) {
      setAdvanceDiagnosticsError(
        extractDisplayErrorMessage(error, 'Не удалось проверить спорный платеж.'),
      );
    } finally {
      setAdvanceDiagnosticsLoading(false);
    }
  }, [advanceDiagnosticsAmount, advanceDiagnosticsDate, advanceDiagnosticsQuery]);

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
    'grid w-full gap-3 grid-cols-1',
    summaryCards.length >= 3
      ? 'sm:grid-cols-2 xl:grid-cols-3'
      : summaryCards.length === 2
      ? 'sm:grid-cols-2 xl:grid-cols-2'
      : 'sm:grid-cols-1 xl:grid-cols-1',
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
    setFromDate(defaultFrom);
    setToDate(defaultTo);
    initialFromRef.current = defaultFrom;
    initialToRef.current = defaultTo;
    setSelectedClientId('');
    setClientRoleFilter('all');
    setSelectedCategories([]);
    setMinAmount('');
    setMaxAmount('');
    setStatusFilter('all');
    setAdvanceReviewFilter('all');
    setSearch('');
    setTypeFilter(defaultType ?? 'all');
  }, [defaultFrom, defaultTo, defaultType]);

  const handleAddPayment = useCallback(
    (type: Payment['type']) => {
      if (!paymentsPermissions.canCreate) {
        showPermissionNotice(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
        return false;
      }
      setModalType(type);
      setEditingPayment(null);
      setPaymentCreatePrefill(null);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canCreate, showPermissionNotice, t],
  );

  useEffect(() => {
    if (!initialCreatePayment) return;
    if (!paymentsPermissions.canCreate) {
      showPermissionNotice(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
      onInitialCreatePaymentHandled?.(initialCreatePayment.key);
      return;
    }

    setPaymentCreatePrefill(initialCreatePayment);
    setModalType(initialCreatePayment.type);
    setEditingPayment(null);
    setIsModalOpen(true);
    onInitialCreatePaymentHandled?.(initialCreatePayment.key);
  }, [
    initialCreatePayment,
    onInitialCreatePaymentHandled,
    paymentsPermissions.canCreate,
    showPermissionNotice,
    t,
  ]);

  const handleEditPayment = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return false;
      }
      try {
        const detailedPayment = await apiService.getPayment(payment.id);
        setEditingPayment(detailedPayment);
        setPaymentCreatePrefill(null);
        setModalType(detailedPayment.type);
        setIsModalOpen(true);
      } catch (error) {
        toast.error(
          extractDisplayErrorMessage(error, 'Не удалось загрузить платеж для редактирования.'),
          t('payments') ?? 'Платежи',
        );
        return false;
      }
      return true;
    },
    [paymentsPermissions.canEdit, showPermissionNotice, t, toast],
  );

  const handleDeletePayment = useCallback(
    async (payment: Payment) => {
      const isTransferredRemainder = isTransferredRemainderPayment(payment);
      const isRemainderSource = isPartialRemainderSourcePayment(payment);
      if (payment.documentKind === 'Invoice' || isTransferredRemainder || isRemainderSource) {
        toast.error(
          isTransferredRemainder || isRemainderSource
            ? 'Перенос остатка нельзя удалить обычным удалением.'
            : 'Документ счета удаляется через раздел "Счета".',
          t('payments') ?? 'Платежи',
        );
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
      const isTransferredRemainder = isTransferredRemainderPayment(payment);
      const isRemainderSource = isPartialRemainderSourcePayment(payment);
      const invoiceManagedPayment = isInvoiceManagedPayment(payment);
      if (isTransferredRemainder || isRemainderSource) {
        toast.error(
          'Перенос остатка нельзя аннулировать обычным действием.',
          t('payments') ?? 'Платежи',
        );
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
        if (invoiceManagedPayment) {
          await apiService.updateInvoiceManagedPayment(payment.id, buildCancelledPaymentPayload(payment));
        } else {
          await apiService.updatePayment(payment.id, buildCancelledPaymentPayload(payment));
        }
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

  const handleAcceptAdvance = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }

      const confirmed = await confirm({
        title: 'Принять платеж как аванс',
        description:
          'Платеж будет добавлен на баланс клиента как нераспределенный аванс. Позже эту сумму можно будет зачесть по долгу клиента.',
        confirmLabel: 'Принять как аванс',
        cancelLabel: 'Отмена',
        variant: 'primary',
      });
      if (!confirmed) return;

      try {
        await apiService.acceptPaymentAdvance(payment.id, 'Принято как аванс из журнала платежей');
        toast.success('Платеж принят как аванс и добавлен на баланс клиента.', 'Платежи');
        await refresh();
        notifyPaymentAdvanceReviewChanged();
      } catch (error) {
        toast.error(
          extractDisplayErrorMessage(error, 'Не удалось принять платеж как аванс.'),
          'Платежи',
        );
      }
    },
    [confirm, paymentsPermissions.canEdit, refresh, showPermissionNotice, t, toast],
  );

  const handleIgnoreAdvance = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }

      const confirmed = await confirm({
        title: 'Исключить из контроля авансов',
        description:
          'Платеж больше не будет попадать в список нераспределенных авансов. Баланс клиента по этому платежу не изменится.',
        confirmLabel: 'Игнорировать',
        cancelLabel: 'Отмена',
        variant: 'danger',
      });
      if (!confirmed) return;

      try {
        await apiService.ignorePaymentAdvance(payment.id, 'Исключено из контроля авансов из журнала платежей');
        toast.success('Платеж исключен из контроля авансов.', 'Платежи');
        await refresh();
        notifyPaymentAdvanceReviewChanged();
      } catch (error) {
        toast.error(
          extractDisplayErrorMessage(error, 'Не удалось исключить платеж из контроля авансов.'),
          'Платежи',
        );
      }
    },
    [confirm, paymentsPermissions.canEdit, refresh, showPermissionNotice, t, toast],
  );

  const handleRefundAdvance = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }

      if (payment.advanceReviewStatus !== 'AcceptedAsAdvance') {
        toast.error('Вернуть можно только платеж, принятый как аванс.', 'Платежи');
        return;
      }

      const confirmed = await confirm({
        title: 'Вернуть аванс клиенту',
        description:
          'Доступный остаток аванса будет списан с баланса клиента, в денежном журнале появится расходная операция возврата, а платеж получит статус контроля «Возвращен».',
        confirmLabel: 'Вернуть аванс',
        cancelLabel: 'Отмена',
        variant: 'danger',
      });
      if (!confirmed) return;

      try {
        await apiService.refundPaymentAdvance(payment.id, 'Возврат аванса из журнала платежей');
        toast.success('Аванс возвращен клиенту и списан с баланса.', 'Платежи');
        await refresh();
        notifyPaymentAdvanceReviewChanged();
      } catch (error) {
        toast.error(
          extractDisplayErrorMessage(error, 'Не удалось вернуть аванс клиенту.'),
          'Платежи',
        );
      }
    },
    [confirm, paymentsPermissions.canEdit, refresh, showPermissionNotice, t, toast],
  );

  const closeAdvanceAllocation = useCallback(() => {
    setAdvanceAllocation(null);
  }, []);

  const handleAllocateAdvance = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }

      if (payment.advanceReviewStatus !== 'AcceptedAsAdvance') {
        toast.error('Распределить можно только платеж, принятый как аванс.', 'Платежи');
        return;
      }

      const owner = resolveAdvanceOwner(payment);
      if (!owner) {
        toast.error('Автоматическое распределение доступно только для авансов клиента или юридического лица.', 'Платежи');
        return;
      }

      setAdvanceAllocation({
        source: payment,
        targets: [],
        selectedTargetId: null,
        amount: '',
        balance: 0,
        loading: true,
        submitting: false,
        error: null,
        ownerKind: owner.kind,
        ownerId: owner.id,
        ownerName: owner.name,
      });

      try {
        const [balanceResponse, paymentsResponse, balanceTransactions] = await Promise.all([
          owner.kind === 'client'
            ? apiService.getClientBalance(owner.id)
            : apiService.getLegalEntityBalance(owner.id),
          apiService.getPaymentsV2({
            ...(owner.kind === 'client' ? { clientId: owner.id } : { legalEntityId: owner.id }),
            type: 'Income',
            page: 1,
            pageSize: 100,
            sortBy: 'date',
            sortDir: 'asc',
          }),
          owner.kind === 'client'
            ? apiService.getClientBalanceTransactions(owner.id)
            : apiService.getLegalEntityBalanceTransactions(owner.id),
        ]);

        const totalBalance = Math.max(Number(balanceResponse.balance ?? 0), 0);
        const sourceBalance = resolveAdvanceSourceAvailableAmount(balanceTransactions, payment.id);
        const balance = Math.min(totalBalance, sourceBalance);
        const targets = (paymentsResponse.items ?? [])
          .filter((item) =>
            item.id !== payment.id &&
            item.status !== 'Completed' &&
            item.status !== 'Cancelled' &&
            item.type === 'Income' &&
            resolvePaymentOutstandingAmount(item) > 0.005)
          .slice(0, 100);
        const firstTarget = targets[0] ?? null;
        const initialAmount = firstTarget
          ? Math.min(balance, resolvePaymentOutstandingAmount(firstTarget))
          : 0;

        setAdvanceAllocation({
          source: payment,
          targets,
          selectedTargetId: firstTarget?.id ?? null,
          amount: formatMoneyInput(initialAmount),
          balance,
          loading: false,
          submitting: false,
          error: null,
          ownerKind: owner.kind,
          ownerId: owner.id,
          ownerName: owner.name,
        });
      } catch (error) {
        setAdvanceAllocation((current) =>
          current?.source.id === payment.id
            ? {
                ...current,
                loading: false,
                error: extractDisplayErrorMessage(error, 'Не удалось загрузить долги клиента для распределения аванса.'),
              }
            : current,
        );
      }
    },
    [paymentsPermissions.canEdit, showPermissionNotice, t, toast],
  );

  const handleAdvanceAllocationTargetChange = useCallback((targetId: number) => {
    setAdvanceAllocation((current) => {
      if (!current) return current;
      const target = current.targets.find((item) => item.id === targetId) ?? null;
      const nextAmount = target
        ? Math.min(current.balance, resolvePaymentOutstandingAmount(target))
        : 0;
      return {
        ...current,
        selectedTargetId: targetId,
        amount: formatMoneyInput(nextAmount),
        error: null,
      };
    });
  }, []);

  const submitAdvanceAllocation = useCallback(async () => {
    if (!advanceAllocation || advanceAllocation.submitting) return;

    const target = advanceAllocation.targets.find((item) => item.id === advanceAllocation.selectedTargetId) ?? null;
    if (!target) {
      setAdvanceAllocation((current) => current ? { ...current, error: 'Выберите платеж, в который нужно зачесть аванс.' } : current);
      return;
    }

    const amount = parseMoneyInput(advanceAllocation.amount);
    const outstanding = resolvePaymentOutstandingAmount(target);
    const maxAmount = Math.min(advanceAllocation.balance, outstanding);
    if (amount <= 0.005) {
      setAdvanceAllocation((current) => current ? { ...current, error: 'Укажите сумму зачета.' } : current);
      return;
    }
    if (amount > maxAmount + 0.005) {
      setAdvanceAllocation((current) =>
        current ? { ...current, error: `Сумма зачета не может быть больше ${formatCurrencySmart(maxAmount).full}.` } : current,
      );
      return;
    }

    try {
      setAdvanceAllocation((current) => current ? { ...current, submitting: true, error: null } : current);
      const applyPayload = {
        paymentId: target.id,
        amount,
        description: `Распределение аванса по платежу #${advanceAllocation.source.id}`,
        sourcePaymentId: advanceAllocation.source.id,
      };
      const result = advanceAllocation.ownerKind === 'client'
        ? await apiService.applyClientBalance(advanceAllocation.ownerId, applyPayload)
        : await apiService.applyLegalEntityBalance(advanceAllocation.ownerId, applyPayload);
      toast.success(
        `Аванс зачтен в платеж #${target.id} на сумму ${formatCurrencySmart(result.appliedAmount ?? amount).full}.`,
        'Платежи',
      );
      setAdvanceAllocation(null);
      await refresh();
    } catch (error) {
      setAdvanceAllocation((current) =>
        current
          ? {
              ...current,
              submitting: false,
              error: extractDisplayErrorMessage(error, 'Не удалось распределить аванс.'),
            }
          : current,
      );
    }
  }, [advanceAllocation, refresh, toast]);

  const openPaymentStatusChange = useCallback((payment: Payment) => {
    if (!paymentsPermissions.canEdit) {
      showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
      return;
    }
    setStatusChangePayment(payment);
    setStatusChangeValue(payment.status);
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [paymentsPermissions.canEdit, showPermissionNotice, t]);

  const closePaymentStatusChange = useCallback(() => {
    if (statusChangeSubmitting) return;
    setStatusChangePayment(null);
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [statusChangeSubmitting]);

  const submitPaymentStatusChange = useCallback(async () => {
    if (!statusChangePayment || !paymentsPermissions.canEdit) return;

    try {
      setStatusChangeSubmitting(true);
      setStatusChangeError(null);
      await apiService.updatePaymentStatus(
        statusChangePayment.id,
        statusChangeValue,
        statusChangeValue === 'Completed' ? defaultTo : null,
        statusChangeReason.trim() || null,
      );
      setStatusChangePayment(null);
      setStatusChangeReason('');
      await refresh();
      toast.success('Статус платежа обновлен.', t('payments') ?? 'Платежи');
    } catch (error) {
      setStatusChangeError(extractDisplayErrorMessage(error, 'Не удалось изменить статус платежа.'));
    } finally {
      setStatusChangeSubmitting(false);
    }
  }, [defaultTo, paymentsPermissions.canEdit, refresh, statusChangePayment, statusChangeReason, statusChangeValue, t, toast]);

  const handleSubmit = useCallback(
    async (payload: SubmitDTO) => {
      try {
        if ('id' in payload) {
          if (editingPayment && isInvoiceManagedPayment(editingPayment)) {
            await apiService.updateInvoiceManagedPayment(payload.id, payload);
          } else {
            await apiService.updatePayment(payload.id, payload);
          }
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
        setPaymentCreatePrefill(null);
        finishRoutePaymentEdit();
      } catch (error) {
        const message = extractDisplayErrorMessage(error, t('paymentsSaveError') ?? 'Не удалось сохранить платёж.');
        toast.error(message, t('payments') ?? 'Платежи');
        throw new Error(message);
      }
    },
    [editingPayment, finishRoutePaymentEdit, page, refresh, t, toast],
  );

  const handleTransferRemainder = useCallback(
    async (payload: SubmitDTO & { id: number }, remainderDate: string) => {
      try {
        await apiService.transferPaymentRemainder(payload.id, payload, remainderDate);
        await refresh();
        toast.success('Остаток платежа перенесён.', t('payments') ?? 'Платежи');
        setIsModalOpen(false);
        setEditingPayment(null);
        finishRoutePaymentEdit();
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Не удалось перенести остаток платежа.');
        toast.error(message, t('payments') ?? 'Платежи');
        throw new Error(message);
      }
    },
    [finishRoutePaymentEdit, refresh, t, toast],
  );

  const handleRollbackRemainderTransfer = useCallback(
    async (paymentId: number) => {
      try {
        await apiService.rollbackPaymentRemainderTransfer(paymentId);
        await refresh();
        toast.success('Перенос остатка отменен.', t('payments') ?? 'Платежи');
        setIsModalOpen(false);
        setEditingPayment(null);
        finishRoutePaymentEdit();
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Не удалось откатить перенос остатка.');
        toast.error(message, t('payments') ?? 'Платежи');
        throw new Error(message);
      }
    },
    [finishRoutePaymentEdit, refresh, t, toast],
  );

  const handleAdjustRemainderTransfer = useCallback(
    async (paymentId: number, paidAmount: number, remainderDate: string) => {
      try {
        await apiService.adjustPaymentRemainderTransfer(paymentId, paidAmount, remainderDate);
        await refresh();
        toast.success('Разделение платежа обновлено.', t('payments') ?? 'Платежи');
        setIsModalOpen(false);
        setEditingPayment(null);
        finishRoutePaymentEdit();
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Не удалось изменить разделение платежа.');
        toast.error(message, t('payments') ?? 'Платежи');
        throw new Error(message);
      }
    },
    [finishRoutePaymentEdit, refresh, t, toast],
  );

  const loadRemainderCandidates = useCallback(async (options: { openPanel?: boolean; showError?: boolean } = {}) => {
    const { openPanel = true, showError = true } = options;
    if (openPanel) {
      setRemainderCandidatesOpen(true);
    }
    setRemainderCandidatesLoading(true);
    try {
      const response = await apiService.getPartialPaymentRemainderCandidates({
        clientId: selectedClientId ? Number(selectedClientId) : undefined,
        clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
        type: appliedTypeFilter === 'all' ? undefined : appliedTypeFilter,
        includeFuture: false,
        page: 1,
        pageSize: 100,
      });
      setRemainderCandidates(response.items ?? []);
      setRemainderCandidatesTotal(response.total ?? 0);
      setRemainderCandidatesTotalAmount(response.totalRemainderAmount ?? 0);
      setRemainderCandidateDates((current) => {
        const next: Record<number, string> = {};
        (response.items ?? []).forEach((item) => {
          next[item.payment.id] = current[item.payment.id] || toDateInputValue(item.suggestedRemainderDate);
        });
        return next;
      });
    } catch (error) {
      if (showError) {
        toastRef.current.error(
          extractDisplayErrorMessage(error, 'Не удалось проверить частичные оплаты без переноса.'),
          'Платежи',
        );
      }
    } finally {
      setRemainderCandidatesLoading(false);
    }
  }, [appliedTypeFilter, clientRoleFilter, selectedClientId]);

  useEffect(() => {
    void loadRemainderCandidates({ openPanel: false, showError: false });
  }, [loadRemainderCandidates]);

  const handleTransferExistingRemainder = useCallback(
    async (candidate: PartialPaymentRemainderCandidate) => {
      const remainderDate = remainderCandidateDates[candidate.payment.id];
      if (!remainderDate) {
        toast.error('Укажите дату, на которую нужно перенести остаток.', 'Платежи');
        return;
      }

      const normalizedDate = fromInputToApiDate(remainderDate);
      if (!normalizedDate) {
        toast.error('Дата переноса остатка указана неверно.', 'Платежи');
        return;
      }

      const confirmed = await confirm({
        title: 'Создать платеж на остаток',
        description: `Исходный платеж будет закрыт на оплаченную сумму ${formatCurrencySmart(candidate.paidAmount).full}, а остаток ${formatCurrencySmart(candidate.remainderAmount).full} появится отдельным платежом на выбранную дату.`,
        confirmLabel: 'Создать остаток',
        cancelLabel: 'Отмена',
      });
      if (!confirmed) return;

      try {
        await apiService.transferExistingPaymentRemainder(candidate.payment.id, normalizedDate);
        toast.success('Остаток создан отдельным платежом.', 'Платежи');
        await refresh();
        await loadRemainderCandidates();
      } catch (error) {
        toast.error(
          extractDisplayErrorMessage(error, 'Не удалось создать платеж на остаток.'),
          'Платежи',
        );
      }
    },
    [confirm, loadRemainderCandidates, refresh, remainderCandidateDates, toast],
  );

  const handleCloseModal = useCallback(() => {
    setIsModalOpen(false);
    setEditingPayment(null);
    setPaymentCreatePrefill(null);
    finishRoutePaymentEdit();
  }, [finishRoutePaymentEdit]);

  const handleRefresh = useCallback(async () => {
    await refresh();
  }, [refresh]);

  const exportPaymentsCsv = useCallback(async () => {
    if (!paymentsPermissions.canExport) {
      showPermissionNotice('У роли нет права на выгрузку платежей.');
      return;
    }

    setPaymentsCsvExporting(true);
    try {
      const { blob, fileName } = await apiService.exportPaymentsCsv(paymentExportParams);
      downloadBlob(fileName || 'payments-export.csv', blob);
      toast.success('Платежи выгружены в CSV.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось выгрузить платежи в CSV.'),
        'Платежи',
      );
    } finally {
      setPaymentsCsvExporting(false);
    }
  }, [paymentExportParams, paymentsPermissions.canExport, showPermissionNotice, toast]);

  const exportPaymentsExcel = useCallback(async () => {
    if (!paymentsPermissions.canExport) {
      showPermissionNotice('У роли нет права на выгрузку платежей.');
      return;
    }

    setPaymentsExcelExporting(true);
    try {
      const { blob, fileName } = await apiService.exportPaymentsExcel(paymentExportParams);
      downloadBlob(fileName || 'payments-export.xls', blob);
      toast.success('Платежи выгружены в Excel.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось выгрузить платежи в Excel.'),
        'Платежи',
      );
    } finally {
      setPaymentsExcelExporting(false);
    }
  }, [paymentExportParams, paymentsPermissions.canExport, showPermissionNotice, toast]);

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
      const active = typeFilter === target;
      return [
        'inline-flex h-8 min-w-[4rem] items-center justify-center rounded-full px-4 text-sm font-semibold transition-colors focus:outline-none',
        active
          ? 'bg-blue-600 text-white shadow-sm'
          : 'text-slate-700 hover:bg-white hover:text-slate-900',
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
  const fieldLabelClass = 'text-sm font-semibold text-slate-700';
  const centeredFieldLabelClass = `${fieldLabelClass} text-center`;
  const inputClass =
    'h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-700 shadow-sm transition-colors focus:border-blue-400 focus:outline-none focus:ring-2 focus:ring-blue-100';
  const centeredDateInputClass = `${inputClass} text-center`;
  const statusChangeCurrentValue = statusChangePayment?.status ?? 'Pending';
  const statusChangeTargetLabel = statusLabels[statusChangeValue] ?? statusChangeValue;
  const statusChangeSubject = statusChangePayment
    ? `Платеж #${statusChangePayment.id}: ${statusChangePayment.description?.trim() || 'без описания'}`
    : 'Платеж не выбран';
  const statusChangeConsequences = [
    `Статус платежа изменится с «${statusLabels[statusChangeCurrentValue] ?? statusChangeCurrentValue}» на «${statusChangeTargetLabel}».`,
    statusChangeValue === 'Completed'
      ? 'Оплаченная сумма станет равна полной сумме платежа, дата оплаты будет сегодняшней.'
      : statusChangeValue === 'Cancelled'
        ? 'Оплаченная сумма и дата оплаты будут очищены.'
        : 'Связанные поля оплаты будут приведены к выбранному статусу.',
    'После сохранения список платежей обновится.',
  ];

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] space-y-6 px-4 py-8 sm:px-6 lg:px-8">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <h1 className="text-2xl font-bold text-slate-900">{headingText}</h1>
            {subtitleText ? (
              <p className="mt-2 max-w-2xl text-sm text-slate-600">{subtitleText}</p>
            ) : null}
            {lastUpdatedText ? (
              <p className="mt-2 text-xs text-slate-500">{lastUpdatedText}</p>
            ) : null}
          </div>

          {paymentsPermissions.canCreate && (showIncomeButton || showExpenseButton) ? (
            <div className="flex flex-wrap gap-3 lg:justify-end">
              {showIncomeButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Income')}
                  className="inline-flex h-10 items-center gap-2 rounded-lg bg-emerald-600 px-4 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-emerald-700 focus:outline-none focus:ring-2 focus:ring-emerald-200">
                  <Plus className="h-4 w-4" />
                  Доход
                </button>
              ) : null}
              {showExpenseButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Expense')}
                  className="inline-flex h-10 items-center gap-2 rounded-lg bg-rose-600 px-4 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-rose-700 focus:outline-none focus:ring-2 focus:ring-rose-200">
                  <Plus className="h-4 w-4" />
                  Расход
                </button>
              ) : null}
            </div>
          ) : null}
        </div>

        <div className="rounded-2xl border border-gray-200 bg-white p-4 shadow-sm">
          <div className="grid gap-4 xl:grid-cols-[minmax(420px,1fr)_minmax(520px,0.95fr)]">
            <div className="space-y-3">
              {lockType ? (
                <span className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1.5 text-sm font-medium text-slate-600">
                  <Lock className="h-3.5 w-3.5 text-slate-500" />
                  {lockedTypeMessage}
                </span>
              ) : (
                <div className="inline-flex w-fit items-center gap-1 rounded-full border border-slate-200 bg-slate-50 p-1 shadow-sm">
                  <button type="button" className={typeButtonClass('all')} onClick={() => setTypeFilter('all')}>
                    {t('paymentsFilterAll') ?? 'Все'}
                  </button>
                  <button type="button" className={typeButtonClass('Income')} onClick={() => setTypeFilter('Income')}>
                    {t('paymentsFilterIncome') ?? t('incomePlural') ?? 'Доходные'}
                  </button>
                  <button type="button" className={typeButtonClass('Expense')} onClick={() => setTypeFilter('Expense')}>
                    {t('paymentsFilterExpense') ?? t('expensePlural') ?? 'Расходные'}
                  </button>
                </div>
              )}

              <div className={summaryGridClass}>{summaryCards}</div>
            </div>

            <div className="rounded-xl bg-slate-50/80 p-3">
              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                <div className="flex flex-col gap-1">
                  <label className={centeredFieldLabelClass}>
                    {t('paymentsFilterDateFrom') ?? t('dateFrom') ?? 'Дата от'}
                  </label>
                  <SystemDateInput
                    type="date"
                    value={fromDate}
                    max={toDate || undefined}
                    onChange={(event) => handleFromDateChange(event.target.value)}
                    className={centeredDateInputClass}
                  />
                </div>

                <div className="flex flex-col gap-1">
                  <label className={centeredFieldLabelClass}>
                    {t('paymentsFilterDateTo') ?? t('dateTo') ?? 'Дата до'}
                  </label>
                  <SystemDateInput
                    type="date"
                    value={toDate}
                    min={fromDate || undefined}
                    onChange={(event) => handleToDateChange(event.target.value)}
                    className={centeredDateInputClass}
                  />
                </div>

                <div className="flex flex-col gap-1">
                  <label className={fieldLabelClass}>{t('status') ?? 'Статус'}</label>
                  <SystemSelect
                    className={inputClass}
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
                  </SystemSelect>
                </div>

                <div className="flex flex-col gap-1">
                  <label className={fieldLabelClass}>Контроль аванса</label>
                  <SystemSelect
                    className={inputClass}
                    value={advanceReviewFilter}
                    onChange={(event) => setAdvanceReviewFilter(event.target.value as AdvanceReviewFilter)}
                  >
                    <option value="all">Все платежи</option>
                    <option value="RequiresDecision">{advanceReviewLabels.RequiresDecision}</option>
                    <option value="AcceptedAsAdvance">{advanceReviewLabels.AcceptedAsAdvance}</option>
                    <option value="Allocated">{advanceReviewLabels.Allocated}</option>
                    <option value="Ignored">{advanceReviewLabels.Ignored}</option>
                    <option value="Refunded">{advanceReviewLabels.Refunded}</option>
                  </SystemSelect>
                </div>

                <div className="flex flex-col gap-1 md:col-span-2">
                  <LookupSelect
                    label="Клиент / поставщик"
                    value={selectedClientId}
                    options={clientLookupOptions}
                    onChange={setSelectedClientId}
                    placeholder="Все контрагенты"
                    title="Выбор клиента / поставщика"
                    dictionaryLabel="Открыть справочник"
                    searchPlaceholder="Поиск по ФИО, компании, телефону или email"
                    emptyOptionLabel="Все контрагенты"
                    emptyOptionDescription="Показать платежи всех клиентов и поставщиков"
                    buttonClassName="border-slate-200 focus:border-blue-400"
                    noOptionsText="Клиенты не найдены"
                  />
                </div>

                <div className="flex flex-col gap-1">
                  <label htmlFor="payments-client-role-filter" className={fieldLabelClass}>
                    Роль клиента
                  </label>
                  <SystemSelect
                    id="payments-client-role-filter"
                    value={clientRoleFilter}
                    onChange={(event) => setClientRoleFilter(event.target.value as ClientRoleFilter)}
                    className={inputClass}
                  >
                    <option value="all">Любая связь</option>
                    <option value="counterparty">Контрагент / поставщик</option>
                    <option value="expenseClient">Клиент расхода</option>
                  </SystemSelect>
                </div>

                <div className="relative flex flex-col gap-1 md:col-span-2">
                  <label className={fieldLabelClass}>{t('search') ?? 'Поиск'}</label>
                  <Search className="pointer-events-none absolute bottom-3 left-3 h-4 w-4 text-slate-400" />
                  <input
                    type="search"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder={t('paymentsSearchPlaceholder') ?? 'Номер, клиент или описание'}
                    className={`${inputClass} pl-10 placeholder:text-slate-400`}
                  />
                </div>

                <div className="flex flex-col gap-1">
                  <label className={fieldLabelClass}>
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
                  <div className="mt-2 grid grid-cols-2 gap-2">
                    <div className="flex flex-col gap-1">
                      <label className="text-center text-xs font-semibold text-slate-500">
                        {t('paymentsFilterAmountMin') ?? 'Сумма от'}
                      </label>
                      <input
                        type="number"
                        inputMode="decimal"
                        step="0.01"
                        value={minAmount}
                        onChange={(event) => setMinAmount(event.target.value)}
                        placeholder="0"
                        className={`${inputClass} text-center`}
                      />
                    </div>

                    <div className="flex flex-col gap-1">
                      <label className="text-center text-xs font-semibold text-slate-500">
                        {t('paymentsFilterAmountMax') ?? 'Сумма до'}
                      </label>
                      <input
                        type="number"
                        inputMode="decimal"
                        step="0.01"
                        value={maxAmount}
                        onChange={(event) => setMaxAmount(event.target.value)}
                        placeholder="0"
                        className={`${inputClass} text-center`}
                      />
                    </div>
                  </div>
                </div>
              </div>

              <div className="mt-3 flex flex-wrap items-center justify-end gap-2" role="toolbar" aria-label="Действия с платежами">
                <ToolbarIconButton
                  label={settingsOpen ? 'Скрыть настройки таблицы' : 'Настроить таблицу'}
                  icon={<SlidersHorizontal className="h-4 w-4" />}
                  active={settingsOpen}
                  onClick={() => setSettingsOpen((prev) => !prev)}
                />
                <ToolbarIconButton
                  label={t('paymentsRefresh') ?? t('invoiceRefresh') ?? 'Обновить'}
                  icon={<RotateCcw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />}
                  disabled={refreshing}
                  onClick={() => {
                    void handleRefresh();
                  }}
                />
                {paymentsPermissions.canExport ? (
                  <>
                    <ToolbarIconButton
                      label="Выгрузить платежи в CSV"
                      icon={paymentsCsvExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                      disabled={paymentsCsvExporting}
                      onClick={() => {
                        void exportPaymentsCsv();
                      }}
                    />
                    <ToolbarIconButton
                      label="Выгрузить платежи в Excel"
                      icon={paymentsExcelExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                      disabled={paymentsExcelExporting}
                      onClick={() => {
                        void exportPaymentsExcel();
                      }}
                    />
                  </>
                ) : null}
                <ToolbarIconButton
                  label="Частичные без остатка"
                  icon={
                    remainderCandidatesLoading ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <AlertTriangle className="h-4 w-4" />
                    )
                  }
                  active={remainderCandidatesOpen}
                  disabled={remainderCandidatesLoading}
                  badge={remainderCandidatesTotal || undefined}
                  warning={remainderCandidatesTotal > 0}
                  onClick={() => {
                    if (remainderCandidatesOpen) {
                      setRemainderCandidatesOpen(false);
                    } else {
                      void loadRemainderCandidates({ openPanel: true });
                    }
                  }}
                />
                {activeFiltersCount ? (
                  <button
                    type="button"
                    onClick={handleResetFilters}
                    className="ml-1 inline-flex h-10 items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 text-sm font-semibold text-amber-700 transition-colors hover:bg-amber-100">
                    <RotateCcw className="h-4 w-4" />
                    Сбросить все
                  </button>
                ) : null}
              </div>
            </div>
          </div>
        </div>
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
          </div>
        ) : null}

        {remainderCandidatesOpen ? (
          <div className="rounded-xl border border-amber-200 bg-amber-50/70 p-4 shadow-sm">
            <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
              <div>
                <div className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-amber-700">
                  <AlertTriangle className="h-4 w-4" />
                  Частичные оплаты без переноса остатка
                </div>
                <p className="mt-1 text-sm text-amber-900">
                  Найдены платежи, где часть уже оплачена, но неоплаченный остаток еще не создан отдельным платежом.
                </p>
                <div className="mt-2 flex flex-wrap gap-2 text-xs font-medium text-amber-800">
                  <span className="rounded-full bg-white px-2.5 py-1 ring-1 ring-amber-200">
                    {`Записей: ${remainderCandidatesTotal.toLocaleString('ru-RU')}`}
                  </span>
                  <span className="rounded-full bg-white px-2.5 py-1 ring-1 ring-amber-200">
                    {`Остаток: ${formatCurrencySmart(remainderCandidatesTotalAmount).full}`}
                  </span>
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  onClick={() => void loadRemainderCandidates()}
                  disabled={remainderCandidatesLoading}
                  aria-label="Проверить снова"
                  title="Проверить снова"
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-amber-200 bg-white text-amber-700 transition hover:bg-amber-50 disabled:opacity-60">
                  {remainderCandidatesLoading ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : (
                    <RotateCcw className="h-4 w-4" />
                  )}
                </button>
                <button
                  type="button"
                  onClick={() => setRemainderCandidatesOpen(false)}
                  aria-label="Закрыть"
                  title="Закрыть"
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition hover:bg-slate-50">
                  <X className="h-4 w-4" />
                </button>
              </div>
            </div>

            <div className="mt-4 overflow-hidden rounded-lg border border-amber-200 bg-white">
              {remainderCandidatesLoading && remainderCandidates.length === 0 ? (
                <div className="flex items-center justify-center gap-2 px-4 py-8 text-sm text-amber-700">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Проверяем платежи...
                </div>
              ) : remainderCandidates.length === 0 ? (
                <div className="px-4 py-8 text-center text-sm text-slate-500">
                  Неразнесенных частичных оплат за прошедшие даты не найдено.
                </div>
              ) : (
                <div className="max-h-[440px] overflow-auto">
                  <table className="min-w-full divide-y divide-amber-100 text-sm">
                    <thead className="sticky top-0 bg-amber-50 text-xs uppercase tracking-wide text-amber-700">
                      <tr>
                        <th className="px-4 py-3 text-left font-semibold">Платеж</th>
                        <th className="px-4 py-3 text-left font-semibold">Оплата</th>
                        <th className="px-4 py-3 text-left font-semibold">Остаток</th>
                        <th className="px-4 py-3 text-left font-semibold">Новая дата</th>
                        <th className="px-4 py-3 text-right font-semibold">Действия</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100">
                      {remainderCandidates.map((candidate) => {
                        const payment = candidate.payment;
                        const clientName =
                          payment.client?.name
                          || payment.expenseClient?.name
                          || payment.legalEntityName
                          || payment.expenseLegalEntityName
                          || 'Контрагент не указан';
                        return (
                          <tr key={payment.id} className="align-top">
                            <td className="px-4 py-3">
                              <div className="font-medium text-slate-900">
                                {payment.description || 'Без описания'}
                              </div>
                              <div className="mt-1 text-xs text-slate-500">
                                {`${clientName} · ${formatDate(payment.date)} · #${payment.id}`}
                              </div>
                              {candidate.daysPastDue > 0 ? (
                                <div className="mt-1 text-xs font-medium text-amber-700">
                                  {`Просрочено дней: ${candidate.daysPastDue}`}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-3 text-slate-700">
                              <div>{`Всего: ${formatCurrencySmart(candidate.originalAmount).full}`}</div>
                              <div className="font-semibold text-emerald-700">
                                {`Оплачено: ${formatCurrencySmart(candidate.paidAmount).full}`}
                              </div>
                            </td>
                            <td className="px-4 py-3 font-semibold text-amber-700">
                              {formatCurrencySmart(candidate.remainderAmount).full}
                            </td>
                            <td className="px-4 py-3 align-middle">
                              <SystemDateInput
                                type="date"
                                value={remainderCandidateDates[payment.id] ?? ''}
                                onChange={(event) =>
                                  setRemainderCandidateDates((current) => ({
                                    ...current,
                                    [payment.id]: event.target.value,
                                  }))
                                }
                                className="h-10 w-[160px] rounded-lg border border-slate-200 px-3 text-center text-sm text-slate-700 shadow-sm focus:border-amber-400 focus:outline-none"
                              />
                            </td>
                            <td className="px-4 py-3 align-middle">
                              <div className="flex items-center justify-end gap-2">
                                <button
                                  type="button"
                                  onClick={() => handleEditPayment(payment)}
                                  aria-label="Открыть"
                                  title="Открыть"
                                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition hover:bg-slate-50">
                                  <Pencil className="h-3.5 w-3.5" />
                                </button>
                                <button
                                  type="button"
                                  onClick={() => void handleTransferExistingRemainder(candidate)}
                                  disabled={remainderCandidatesLoading}
                                  className="inline-flex h-10 items-center gap-1.5 rounded-lg bg-amber-600 px-3 text-xs font-medium text-white transition hover:bg-amber-700 disabled:opacity-60">
                                  <CalendarPlus className="h-3.5 w-3.5" />
                                  Создать остаток
                                </button>
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
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
              <section
                className="rounded-xl border border-rose-100 bg-white p-3 shadow-sm"
                aria-label="Мониторинг нераспределенных авансов"
              >
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex min-w-0 flex-col gap-1">
                    <div className="inline-flex items-center gap-2 text-sm font-semibold text-slate-900">
                      <AlertTriangle className="h-4 w-4 text-rose-600" />
                      Нераспределенные авансы
                    </div>
                    <div className="text-xs text-slate-500">
                      Платежи без закрывающих документов нужно принять как аванс, распределить, привязать или исключить с причиной.
                    </div>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      onClick={() => showAdvanceReviewMonitoring('RequiresDecision')}
                      className={[
                        'inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-semibold transition-colors',
                        advanceReviewFilter === 'RequiresDecision'
                          ? 'border-rose-200 bg-rose-600 text-white'
                          : 'border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100',
                      ].join(' ')}
                    >
                      На разбор
                      <span className="rounded-full bg-white/80 px-2 py-0.5 text-xs font-bold text-rose-700">
                        {advanceReviewPageSummary.requiresDecisionCount}
                      </span>
                    </button>
                    <button
                      type="button"
                      onClick={() => showAdvanceReviewMonitoring('AcceptedAsAdvance')}
                      className={[
                        'inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-semibold transition-colors',
                        advanceReviewFilter === 'AcceptedAsAdvance'
                          ? 'border-emerald-200 bg-emerald-600 text-white'
                          : 'border-emerald-200 bg-emerald-50 text-emerald-700 hover:bg-emerald-100',
                      ].join(' ')}
                    >
                      Принятые авансы
                      <span className="rounded-full bg-white/80 px-2 py-0.5 text-xs font-bold text-emerald-700">
                        {advanceReviewPageSummary.acceptedCount}
                      </span>
                    </button>
                  </div>
                </div>
                <div className="mt-3 grid gap-2 sm:grid-cols-2">
                  <div className="rounded-lg border border-rose-100 bg-rose-50 px-3 py-2">
                    <div className="text-xs font-medium uppercase text-rose-700/70">Требуют решения на странице</div>
                    <div className="mt-1 text-base font-semibold text-rose-700">
                      {advanceReviewPageSummary.requiresDecisionCount} на {formatCurrencySmart(advanceReviewPageSummary.requiresDecisionAmount).full}
                    </div>
                  </div>
                  <div className="rounded-lg border border-emerald-100 bg-emerald-50 px-3 py-2">
                    <div className="text-xs font-medium uppercase text-emerald-700/70">Приняты как аванс на странице</div>
                    <div className="mt-1 text-base font-semibold text-emerald-700">
                      {advanceReviewPageSummary.acceptedCount} на {formatCurrencySmart(advanceReviewPageSummary.acceptedAmount).full}
                    </div>
                  </div>
                </div>
                <div className="mt-3 rounded-lg border border-amber-200 bg-amber-50/70 p-3">
                  <div className="flex flex-wrap items-end gap-2">
                    <label className="min-w-[220px] flex-1 text-xs font-medium text-amber-900">
                      Клиент, описание или номер
                      <input
                        aria-label="Клиент, описание или номер для диагностики аванса"
                        type="search"
                        value={advanceDiagnosticsQuery}
                        onChange={(event) => setAdvanceDiagnosticsQuery(event.target.value)}
                        className="mt-1 w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-amber-400 focus:ring-2 focus:ring-amber-100"
                        placeholder="Например: Винокуров"
                      />
                    </label>
                    <label className="w-full text-xs font-medium text-amber-900 sm:w-40">
                      Дата платежа
                      <SystemDateInput
                        aria-label="Дата платежа для диагностики аванса"
                        type="date"
                        value={advanceDiagnosticsDate}
                        onChange={(event) => setAdvanceDiagnosticsDate(event.target.value)}
                        className="mt-1 w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-amber-400 focus:ring-2 focus:ring-amber-100"
                      />
                    </label>
                    <label className="w-full text-xs font-medium text-amber-900 sm:w-40">
                      Сумма
                      <input
                        aria-label="Сумма платежа для диагностики аванса"
                        type="text"
                        inputMode="decimal"
                        value={advanceDiagnosticsAmount}
                        onChange={(event) => setAdvanceDiagnosticsAmount(event.target.value)}
                        className="mt-1 w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-amber-400 focus:ring-2 focus:ring-amber-100"
                        placeholder="7100"
                      />
                    </label>
                    <button
                      type="button"
                      onClick={() => void handleRunAdvanceDiagnostics()}
                      disabled={advanceDiagnosticsLoading}
                      className="inline-flex min-h-[38px] items-center justify-center gap-2 rounded-md border border-amber-300 bg-white px-3 py-2 text-sm font-semibold text-amber-800 transition hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-70"
                    >
                      {advanceDiagnosticsLoading ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <Search className="h-4 w-4" />
                      )}
                      Проверить платеж
                    </button>
                  </div>
                  {advanceDiagnosticsError ? (
                    <div className="mt-2 rounded-md border border-rose-200 bg-white px-3 py-2 text-sm text-rose-700">
                      {advanceDiagnosticsError}
                    </div>
                  ) : null}
                  {advanceDiagnosticsResult ? (
                    <div className="mt-3 space-y-2">
                      <div className="text-xs font-medium text-amber-900">
                        Найдено: {advanceDiagnosticsResult.total} на {formatCurrencySmart(advanceDiagnosticsResult.totalAmount).full}
                      </div>
                      {advanceDiagnosticsResult.items.length === 0 ? (
                        <div className="rounded-md border border-amber-200 bg-white px-3 py-2 text-sm text-slate-600">
                          Похожих платежей не найдено.
                        </div>
                      ) : (
                        advanceDiagnosticsResult.items.map((item) => (
                          <div key={item.id} className="rounded-md border border-amber-200 bg-white px-3 py-2">
                            <div className="flex flex-wrap items-start justify-between gap-2">
                              <div className="min-w-0">
                                <div className="text-sm font-semibold text-slate-900">
                                  {item.counterpartyName?.trim() || 'Контрагент не указан'} · #{item.id}
                                </div>
                                <div className="mt-1 text-xs text-slate-600">
                                  {formatDate(item.date)} · {formatCurrencySmart(item.advanceAmount || item.paidAmount || item.amount).full} · {item.description?.trim() || 'Без описания'}
                                </div>
                              </div>
                              <button
                                type="button"
                                onClick={() => void openPaymentPreviewById(item.id)}
                                className="inline-flex items-center justify-center rounded-md border border-slate-200 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50"
                              >
                                Открыть
                              </button>
                            </div>
                            <div className="mt-2 flex flex-wrap gap-1.5">
                              <span className={[
                                'rounded-full px-2 py-0.5 text-xs font-medium',
                                item.isUnallocatedAdvanceCandidate
                                  ? 'bg-rose-100 text-rose-700'
                                  : 'bg-slate-100 text-slate-600',
                              ].join(' ')}
                              >
                                {item.isUnallocatedAdvanceCandidate ? 'Кандидат в аванс' : 'Не требует решения'}
                              </span>
                              <span className="rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
                                {advanceReviewLabels[item.advanceReviewStatus] ?? item.advanceReviewStatus}
                              </span>
                              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">
                                {statusLabels[item.status] ?? item.status}
                              </span>
                              {item.hasInvoiceLink ? (
                                <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Есть счет</span>
                              ) : null}
                              {item.hasActLink ? (
                                <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Есть акт</span>
                              ) : null}
                              {item.hasAccountNumber ? (
                                <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">Есть номер счета</span>
                              ) : null}
                              {item.hasUnallocatedAdvanceBalanceTransaction ? (
                                <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">
                                  Баланс: {formatCurrencySmart(item.unallocatedAdvanceBalanceAmount).full}
                                </span>
                              ) : (
                                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-800">
                                  Нет проводки баланса
                                </span>
                              )}
                            </div>
                          </div>
                        ))
                      )}
                    </div>
                  ) : null}
                </div>
              </section>
              <PaymentsTable
                payments={payments}
                onEditPayment={handleEditPayment}
                onOpenPayment={onOpenPayment}
                onOpenClient={onOpenClient}
                onAddPayment={handleAddPayment}
                onDeletePayment={async (payment) => {
                  await handleDeletePayment(payment);
                }}
                onStatusChange={openPaymentStatusChange}
                onAcceptAdvance={handleAcceptAdvance}
                onIgnoreAdvance={handleIgnoreAdvance}
                onAllocateAdvance={handleAllocateAdvance}
                onRefundAdvance={handleRefundAdvance}
                canAdd={paymentsPermissions.canCreate}
                canEdit={paymentsPermissions.canEdit}
                canDelete={paymentsPermissions.canDelete}
                canOpenClients={canOpenLinkedClients}
                canOpenCases={canOpenLinkedCases}
                canEditPayment={() => true}
                canDeletePayment={(payment) => payment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(payment) && !isPartialRemainderSourcePayment(payment)}
                columns={visiblePaymentColumns}
                draggingColumn={draggingColumn}
                onColumnDragStart={handleColumnDragStart}
                onColumnDragOver={handleColumnDragOver}
                onColumnDrop={handleColumnDrop}
                onColumnDragEnd={handleColumnDragEnd}
                onResizeStart={handleResizeStart}
                getColumnWidth={getColumnWidth}
                getTableWidth={getTableWidth}
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

      {advanceAllocation ? (
        <div className="fixed inset-0 z-[1300] flex items-center justify-center bg-slate-900/60 p-4">
          <div
            className="w-full max-w-2xl rounded-xl bg-white shadow-2xl"
            role="dialog"
            aria-modal="true"
            aria-labelledby="advance-allocation-title"
          >
            <div className="flex items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 id="advance-allocation-title" className="text-lg font-semibold text-slate-900">
                  Распределить аванс
                </h3>
                <p className="mt-1 text-sm text-slate-500">
                  {`Авансовый платеж #${advanceAllocation.source.id}. Выберите долг клиента, куда зачесть доступный баланс.`}
                </p>
              </div>
              <button
                type="button"
                onClick={closeAdvanceAllocation}
                className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
                aria-label="Закрыть"
                disabled={advanceAllocation.submitting}
              >
                <X className="h-5 w-5" />
              </button>
            </div>

            <div className="space-y-4 px-5 py-4">
              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-3">
                  <div className="text-xs font-semibold uppercase tracking-wide text-emerald-700">
                    Доступный баланс
                  </div>
                  <div className="mt-1 text-xl font-semibold text-emerald-900">
                    {formatCurrencySmart(advanceAllocation.balance).full}
                  </div>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                  <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                    Контрагент
                  </div>
                  <div className="mt-1 text-sm font-semibold text-slate-900">
                    {advanceAllocation.ownerName}
                  </div>
                </div>
              </div>

              {advanceAllocation.loading ? (
                <div className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-600">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Загружаем открытые долги клиента...
                </div>
              ) : advanceAllocation.targets.length === 0 ? (
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-3 text-sm text-amber-800">
                  Открытых входящих платежей с остатком долга по этому клиенту не найдено.
                </div>
              ) : (
                <>
                  <label className="block text-sm font-medium text-slate-700">
                    Долг для зачета
                    <SystemSelect
                      value={advanceAllocation.selectedTargetId ?? ''}
                      onChange={(event) => handleAdvanceAllocationTargetChange(Number(event.target.value))}
                      disabled={advanceAllocation.submitting}
                      className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
                    >
                      {advanceAllocation.targets.map((target) => {
                        const outstanding = resolvePaymentOutstandingAmount(target);
                        const labelParts = [
                          `#${target.id}`,
                          target.description || 'Без описания',
                          `остаток ${formatCurrencySmart(outstanding).full}`,
                          target.plannedDate || target.date ? `дата ${formatDate(target.plannedDate || target.date || '')}` : null,
                        ].filter(Boolean);
                        return (
                          <option key={target.id} value={target.id}>
                            {labelParts.join(' · ')}
                          </option>
                        );
                      })}
                    </SystemSelect>
                  </label>

                  <label className="block text-sm font-medium text-slate-700">
                    Сумма зачета
                    <input
                      value={advanceAllocation.amount}
                      onChange={(event) =>
                        setAdvanceAllocation((current) =>
                          current ? { ...current, amount: event.target.value, error: null } : current,
                        )
                      }
                      inputMode="decimal"
                      disabled={advanceAllocation.submitting}
                      className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
                    />
                  </label>
                </>
              )}

              {advanceAllocation.error ? (
                <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                  {advanceAllocation.error}
                </div>
              ) : null}
            </div>

            <div className="flex justify-end gap-2 border-t border-slate-200 px-5 py-4">
              <button
                type="button"
                onClick={closeAdvanceAllocation}
                disabled={advanceAllocation.submitting}
                className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Отмена
              </button>
              <button
                type="button"
                onClick={submitAdvanceAllocation}
                disabled={
                  advanceAllocation.loading ||
                  advanceAllocation.submitting ||
                  advanceAllocation.targets.length === 0
                }
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {advanceAllocation.submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                Зачесть аванс
              </button>
            </div>
          </div>
        </div>
      ) : null}

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
                    onClick={async () => {
                      const opened = await handleEditPayment(routePaymentPreview);
                      if (!opened) {
                        return;
                      }
                      setReturnAfterRoutePaymentEdit(Boolean(onRoutePaymentExit));
                      dismissRoutePaymentPreview();
                    }}
                    className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
                  >
                    <Pencil className="h-4 w-4" />
                    Редактировать
                  </button>
                ) : null}
                {routePaymentPreview && paymentsPermissions.canEdit && routePaymentPreview.status !== 'Cancelled' && !isTransferredRemainderPayment(routePaymentPreview) && !isPartialRemainderSourcePayment(routePaymentPreview) ? (
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
                {routePaymentPreview && paymentsPermissions.canDelete && routePaymentPreview.documentKind !== 'Invoice' && !isTransferredRemainderPayment(routePaymentPreview) && !isPartialRemainderSourcePayment(routePaymentPreview) ? (
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

      <StatusChangeModal<PaymentStatus>
        open={statusChangePayment !== null}
        title="Смена статуса платежа"
        subject={statusChangeSubject}
        currentLabel={statusLabels[statusChangeCurrentValue] ?? statusChangeCurrentValue}
        currentValue={statusChangeCurrentValue}
        value={statusChangeValue}
        options={paymentStatusOptions}
        onChange={(value) => {
          setStatusChangeValue(value);
          if (statusChangeError) {
            setStatusChangeError(null);
          }
        }}
        reason={statusChangeReason}
        reasonRequired={Boolean(user?.requiresFinancialActionReason)}
        onReasonChange={(value) => {
          setStatusChangeReason(value);
          if (statusChangeError) {
            setStatusChangeError(null);
          }
        }}
        consequences={statusChangeConsequences}
        onCancel={closePaymentStatusChange}
        onConfirm={submitPaymentStatusChange}
        confirmLabel={`Изменить статус платежа #${statusChangePayment?.id ?? ''} на «${statusChangeTargetLabel}»`}
        error={statusChangeError}
        loading={statusChangeSubmitting}
      />

      <PaymentModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSubmit={handleSubmit}
        onCreateRemainder={apiService.createPayment}
        onTransferRemainder={handleTransferRemainder}
        onRollbackRemainderTransfer={handleRollbackRemainderTransfer}
        onAdjustRemainderTransfer={handleAdjustRemainderTransfer}
        canViewLinkedInvoices={canViewLinkedInvoices}
        canCreateLinkedInvoices={canCreateLinkedInvoices}
        onBalanceApplied={async () => {
          await refresh();
        }}
        payment={editingPayment}
        type={modalType}
        defaultClientId={!editingPayment && !paymentCreatePrefill?.legalEntityId ? paymentCreatePrefill?.clientId ?? undefined : undefined}
        defaultLegalEntityId={!editingPayment ? paymentCreatePrefill?.legalEntityId ?? undefined : undefined}
        defaultClientCaseId={!editingPayment ? paymentCreatePrefill?.clientCaseId ?? undefined : undefined}
        casesPrefetch={!editingPayment ? paymentCreatePrefill?.casesPrefetch : undefined}
        onDelete={
          paymentsPermissions.canDelete
          && (!editingPayment || (editingPayment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(editingPayment) && !isPartialRemainderSourcePayment(editingPayment)))
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
                  finishRoutePaymentEdit();
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
