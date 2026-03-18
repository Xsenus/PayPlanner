import {
  AlertTriangle,
  CalendarRange,
  CheckCircle2,
  ChevronDown,
  FilePlus2,
  GripVertical,
  Loader2,
  Pencil,
  Plus,
  RotateCcw,
  Search,
  SlidersHorizontal,
  Trash2,
  WalletCards,
} from 'lucide-react';
import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useToast } from '../../hooks/useToast';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useAccountsRegistry } from '../../hooks/useAccountsRegistry';
import type {
  AccountRegistryRow,
  Act,
  ActStatus,
  ActInput,
  ActResponsible,
  Client,
  Invoice,
  InvoiceInput,
  Payment,
  PaymentKind,
  PaymentPayload,
} from '../../types';
import { apiService } from '../../services/api';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import {
  clearInvoiceEditorDraft,
  peekInvoiceEditorDraft,
  type InvoiceEditorDraftState,
} from '../../utils/invoiceEditorBridge';
import { InvoiceModal } from './InvoiceModal';
import { PaymentModal } from '../Calendar/PaymentModal';
import { ActModal } from '../Acts/ActModal';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';

interface AccountsProps {
  defaultType?: PaymentKind;
  lockType?: boolean;
  onOpenClient?: (clientId: number) => void;
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

type SortState = { key: 'date' | 'number' | 'amount' | 'counterparty' | 'dueDate' | 'responsible'; direction: 'asc' | 'desc' };

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

type AccountRow = AccountRegistryRow;

type PaymentModalInvoiceContext = {
  invoiceEntityId: number;
  invoiceRecordId?: number | null;
  number: string;
  date: string;
  clientId?: number | null;
  clientCaseId?: number | null;
  type?: PaymentKind;
};

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

function getInvoiceCaseSummary(invoice: Invoice | null): string[] {
  if (!invoice) return [];
  if (invoice.clientCases?.length) {
    return invoice.clientCases.map((item) => item.title?.trim() || `Дело #${item.id}`);
  }
  if (invoice.clientCaseTitle?.trim()) {
    return [invoice.clientCaseTitle];
  }
  if (invoice.clientCaseId) {
    return [`Дело #${invoice.clientCaseId}`];
  }
  return [];
}

const PAGE_SIZE_OPTIONS = [10, 15, 25, 50, 100];
const ACCOUNT_COLUMNS = [
  { key: 'date', label: 'Дата', defaultVisible: true, defaultWidth: 150 },
  { key: 'number', label: 'Номер счёта', defaultVisible: true, defaultWidth: 180 },
  { key: 'amount', label: 'Сумма', defaultVisible: true, defaultWidth: 150 },
  { key: 'counterparty', label: 'Поставщик услуг', defaultVisible: true, defaultWidth: 240 },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 170 },
  { key: 'dueDate', label: 'Оплатить до', defaultVisible: true, defaultWidth: 150 },
  { key: 'paidDate', label: 'Дата оплаты', defaultVisible: false, defaultWidth: 150 },
  { key: 'act', label: 'Акт', defaultVisible: true, defaultWidth: 210 },
  { key: 'responsible', label: 'Ответственный', defaultVisible: false, defaultWidth: 210 },
  { key: 'type', label: 'Тип', defaultVisible: false, defaultWidth: 130 },
  { key: 'description', label: 'Описание', defaultVisible: false, defaultWidth: 260 },
] as const;

function deriveAccountStatus(invoice: Invoice): AccountStatus {
  if (invoice.status === 'Completed') return 'paid';
  if (invoice.status === 'Cancelled') return 'cancelled';
  if (invoice.status === 'Pending') return 'created';
  return 'partial';
}

function deriveStatusFromPayments(payments: Payment[], fallback?: Invoice | null): AccountStatus {
  if (!payments.length) {
    return fallback ? deriveAccountStatus(fallback) : 'created';
  }

  const allCancelled = payments.every((payment) => payment.status === 'Cancelled');
  if (allCancelled) return 'cancelled';

  const allPaid = payments.every((payment) => payment.status === 'Completed');
  if (allPaid) return 'paid';

  const anyProgress = payments.some(
    (payment) => payment.status === 'Completed' || payment.status === 'Processing' || payment.status === 'Overdue',
  );

  return anyProgress ? 'partial' : 'created';
}

function accountKey(value?: string | null): string | null {
  if (!value) return null;
  const normalized = value.trim().toLowerCase();
  if (!normalized) return null;

  const numeric = normalized.replace(/\D+/g, '');
  if (numeric) return numeric;

  const stripped = normalized.replace(/[^a-z0-9а-яё]+/gi, '');
  if (stripped) return stripped;

  return normalized;
}

function invoiceEntityId(value?: Invoice | null): number | null {
  return typeof value?.invoiceEntityId === 'number' && value.invoiceEntityId > 0
    ? value.invoiceEntityId
    : null;
}

function invoiceLegacyKey(number?: string | null, date?: string | null): string | null {
  const normalizedNumber = accountKey(number);
  if (!normalizedNumber) return null;

  const normalizedDate = toDateInputValue(date ?? null);
  const year = normalizedDate?.slice(0, 4) ?? 'na';
  return `${normalizedNumber}::${year}`;
}

function paymentLegacyKey(payment: Payment): string | null {
  return invoiceLegacyKey(payment.account, payment.accountDate ?? payment.date);
}

function mergePaymentsUnique(...groups: Payment[][]): Payment[] {
  const map = new Map<number, Payment>();
  groups.flat().forEach((payment) => {
    map.set(payment.id, payment);
  });

  return Array.from(map.values()).sort((a, b) => {
    const left = toDateInputValue(a.plannedDate) || toDateInputValue(a.date) || '';
    const right = toDateInputValue(b.plannedDate) || toDateInputValue(b.date) || '';
    return left.localeCompare(right) || a.id - b.id;
  });
}

void [accountKey, invoiceLegacyKey, paymentLegacyKey, mergePaymentsUnique];

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

export function Accounts({ defaultType, lockType = false, onOpenClient }: AccountsProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
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

  const [from, setFrom] = useState<string>(savedPeriod.from || startOfYear);
  const [to, setTo] = useState<string>(savedPeriod.to || todayYMD);
  const [statusFilter, setStatusFilter] = useState<'all' | AccountStatus>('all');
  const [typeFilter, setTypeFilter] = useState<FilterType>(defaultType ?? 'All');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [expandedAccounts, setExpandedAccounts] = useState<Record<string, boolean>>({});
  const [paymentModalOpen, setPaymentModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [paymentModalType, setPaymentModalType] = useState<PaymentKind>('Income');
  const [paymentModalInvoiceContext, setPaymentModalInvoiceContext] = useState<PaymentModalInvoiceContext | null>(null);
  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalSubmitting, setActModalSubmitting] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actModalInvoice, setActModalInvoice] = useState<AccountRow | null>(null);
  const [actModalMode, setActModalMode] = useState<'create' | 'edit'>('create');
  const [activeAct, setActiveAct] = useState<Act | null>(null);
  const [actInitialValues, setActInitialValues] = useState<Partial<ActInput> & { amount?: number | null } | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actLookupsLoading, setActLookupsLoading] = useState(false);
  const [actLookupsError, setActLookupsError] = useState<string | null>(null);
  const [modalOpen, setModalOpen] = useState(false);
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [pageEditingInvoiceId, setPageEditingInvoiceId] = useState<number | null>(null);
  const [pageEditingInvoice, setPageEditingInvoice] = useState<Invoice | null>(null);
  const [pageInvoiceLoading, setPageInvoiceLoading] = useState(false);
  const [resumeInvoiceDraft, setResumeInvoiceDraft] = useState<InvoiceEditorDraftState | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);

  const {
    columnOrder,
    orderedVisibleColumns: orderedVisibleColumnKeys,
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
  } = usePersistedTableSettings<ColumnKey>({
    storageKey: `${storagePrefix}.tableSettings.v2`,
    settingsKey: 'accounts_table_settings',
    columns: ACCOUNT_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 15,
    minPageSize: 5,
    maxPageSize: 100,
    minColumnWidth: 96,
  });

  const {
    items: accountRows,
    total: accountsTotal,
    summary,
    loading: isLoading,
    refreshing: isRefreshing,
    error,
    refresh: refreshAccounts,
  } = useAccountsRegistry({
    from: from || undefined,
    to: to || undefined,
    status: statusFilter,
    search: debouncedSearch || undefined,
    type: typeFilter,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((accountsTotal || 0) / Math.max(1, pageSize || 1))),
    [accountsTotal, pageSize],
  );

  useEffect(() => {
    setPage(1);
  }, [from, to, statusFilter, debouncedSearch, typeFilter, pageSize]);

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
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const statusLabels: Record<AccountStatus, string> = {
    created: 'Создан',
    paid: 'Оплачен',
    partial: 'Оплачен частично',
    cancelled: t('cancelledStatus') ?? 'Отменён',
  };

  const actStatusLabels: Record<ActStatus, string> = {
    Created: t('actStatusCreated') ?? 'Создан',
    Transferred: t('actStatusTransferred') ?? 'Передано',
    Signed: t('actStatusSigned') ?? 'Подписано',
    Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
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

  const canOpenClientFromAccounts = useMemo(() => typeof onOpenClient === 'function', [onOpenClient]);

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

  const handleSort = (key: SortState['key']) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' ? 'desc' : 'asc' };
    });
  };

  const [clients, setClients] = useState<Client[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);

  const ensureInvoiceLookups = useCallback(async () => {
    if (!modalOpen && !pageEditingInvoiceId) return;
    if (clients.length > 0 && actResponsibles.length > 0 && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse] = await Promise.all([
        clients.length > 0 ? Promise.resolve(clients) : apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      if (!clients.length) {
        setClients(clientsResponse ?? []);
      }
      setActResponsibles(responsiblesResponse ?? []);
      setLookupsError(null);
    } catch (err) {
      setLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить справочники');
    } finally {
      setLookupsLoading(false);
    }
  }, [modalOpen, pageEditingInvoiceId, clients, actResponsibles.length, lookupsError]);

  const ensureActLookups = useCallback(async () => {
    if (!actModalOpen) return;
    if (clients.length > 0 && actResponsibles.length > 0 && !actLookupsError && actClients.length > 0) return;
    setActLookupsLoading(true);
    try {
      const [clientsResponse, responsibleResponse] = await Promise.all([
        clients.length > 0 ? Promise.resolve(clients) : apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      if (!clients.length && clientsResponse) {
        setClients(clientsResponse);
      }
      setActClients(clientsResponse ?? clients);
      setActResponsibles(responsibleResponse ?? []);
      setActLookupsError(null);
    } catch (err) {
      setActLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить справочники');
    } finally {
      setActLookupsLoading(false);
    }
  }, [actClients.length, actLookupsError, actModalOpen, actResponsibles.length, clients]);

  useEffect(() => {
    if (modalOpen || pageEditingInvoiceId) {
      void ensureInvoiceLookups();
    }
  }, [modalOpen, pageEditingInvoiceId, ensureInvoiceLookups]);

  useEffect(() => {
    if (actModalOpen) {
      void ensureActLookups();
    }
  }, [actModalOpen, ensureActLookups]);

  const openCreateModal = useCallback((draft: InvoiceEditorDraftState | null = null) => {
    clearInvoiceEditorDraft();
    setSelectedInvoice(null);
    setResumeInvoiceDraft(draft);
    setModalError(null);
    setModalOpen(true);
  }, []);

  const openEditPage = useCallback((invoiceId: number, draft: InvoiceEditorDraftState | null = null) => {
    clearInvoiceEditorDraft();
    setPageEditingInvoiceId(invoiceId);
    setPageEditingInvoice(null);
    setPageInvoiceLoading(true);
    setResumeInvoiceDraft(draft);
    setModalError(null);
  }, []);

  useEffect(() => {
    if (!pageEditingInvoiceId) return;

    let active = true;

    const loadInvoice = async () => {
      try {
        setPageInvoiceLoading(true);
        const invoice = await apiService.getInvoice(pageEditingInvoiceId);
        if (!active) return;
        setPageEditingInvoice(invoice);
      } catch (err) {
        if (!active) return;
        toast.error(extractDisplayErrorMessage(err, 'Не удалось загрузить счет.'));
        setPageEditingInvoiceId(null);
        setPageEditingInvoice(null);
      } finally {
        if (active) {
          setPageInvoiceLoading(false);
        }
      }
    };

    void loadInvoice();

    return () => {
      active = false;
    };
  }, [pageEditingInvoiceId, toast]);

  useEffect(() => {
    if (modalOpen || pageEditingInvoiceId) return;

    const draft = peekInvoiceEditorDraft();
    if (!draft) return;

    if (draft.mode === 'create') {
      openCreateModal(draft);
      toast.error(extractDisplayErrorMessage(err, 'Не удалось сохранить счет.'));
    }

    if (!draft.invoiceId) {
      clearInvoiceEditorDraft();
      return;
    }

    openEditPage(draft.invoiceId, draft);
  }, [modalOpen, openCreateModal, openEditPage, pageEditingInvoiceId]);

  useEffect(() => {
    if (!canEdit) return;
    if (peekInvoiceEditorDraft()) return;
    let raw: string | null = null;
    try {
      raw = localStorage.getItem('pp.openInvoiceId');
    } catch {
      raw = null;
    }
    if (!raw) return;
    const targetId = Number(raw);
    if (Number.isNaN(targetId)) {
      try { localStorage.removeItem('pp.openInvoiceId'); } catch { /** */ }
      return;
    }
    openEditPage(targetId);
    try { localStorage.removeItem('pp.openInvoiceId'); } catch { /** */ }
  }, [canEdit, openEditPage]);

  const closeModal = () => {
    clearInvoiceEditorDraft();
    setModalOpen(false);
    setSelectedInvoice(null);
    setResumeInvoiceDraft(null);
    setModalError(null);
  };

  const closeEditPage = () => {
    clearInvoiceEditorDraft();
    setPageEditingInvoiceId(null);
    setPageEditingInvoice(null);
    setPageInvoiceLoading(false);
    setResumeInvoiceDraft(null);
    setModalError(null);
  };

  const openClientFromEditor = (clientId: number) => {
    onOpenClient?.(clientId);
  };

  const submitInvoice = async (payload: InvoiceInput) => {
    setSubmitting(true);
    setModalError(null);
    try {
      if (pageEditingInvoiceId) {
        await apiService.updateInvoice(pageEditingInvoiceId, payload);
        toast.success('Изменения по счету сохранены.', 'Счет обновлен');
        await refreshAccounts();
        closeEditPage();
      } else {
        await apiService.createInvoice(payload);
        if (page !== 1) {
          setPage(1);
        } else {
          await refreshAccounts();
        }
        toast.success('Счет успешно создан.', 'Счет сохранен');
        closeModal();
      }
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить счет.');
      setModalError(message);
      toast.error(message);
    } finally {
      setSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!canDelete) return;
    if (!(await confirm({ title: 'Подтверждение удаления', description: t('invoiceDeleteConfirm') ?? 'Удалить счёт?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    try {
      await apiService.deleteInvoice(invoice.id);
      toast.success('Счет удален.');
      if (accountRows.length === 1 && page > 1) {
        setPage((current) => Math.max(1, current - 1));
      } else {
        await refreshAccounts();
      }
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить счет.'));
    }
  };

  const handleResetFilters = () => {
    setFrom(startOfYear);
    setTo(todayYMD);
    setStatusFilter('all');
    setSearch('');
  };

  const typeOptions: FilterType[] = ['All', 'Income', 'Expense'];

  const summaryBuckets = summary;
  const mergedTotal = accountsTotal;
  const pagedAccounts = accountRows;

  const columns: TableColumn[] = useMemo(
    () => [
      { key: 'date', label: t('invoiceDate') ?? t('actDate') ?? 'Дата', sortable: true },
      { key: 'number', label: t('invoiceNumber') ?? 'Номер счёта', sortable: true },
      { key: 'amount', label: t('invoiceAmount') ?? t('amount') ?? 'Сумма', sortable: true },
      { key: 'counterparty', label: t('invoiceClient') ?? 'Поставщик услуг', sortable: true },
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

  const columnsByKey = useMemo(
    () => Object.fromEntries(columns.map((column) => [column.key, column])) as Record<ColumnKey, TableColumn>,
    [columns],
  );

  const orderedVisibleColumns = useMemo(
    () => orderedVisibleColumnKeys.map((key) => columnsByKey[key]).filter(Boolean),
    [columnsByKey, orderedVisibleColumnKeys],
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

  const toggleExpanded = (key: string) => {
    setExpandedAccounts((prev) => ({ ...prev, [key]: !prev[key] }));
  };

  /*

  const paymentsByInvoiceEntityId = useMemo(() => {
    const map = new Map<number, Payment[]>();
    payments.forEach((payment) => {
      if (payment.documentKind === 'Invoice') return;
      if (typeof payment.invoiceId !== 'number' || payment.invoiceId <= 0) return;
      const existing = map.get(payment.invoiceId) ?? [];
      map.set(payment.invoiceId, [...existing, payment]);
    });
    return map;
  }, [payments]);

  const legacyPaymentsByInvoiceKey = useMemo(() => {
    const map = new Map<string, Payment[]>();
    payments.forEach((payment) => {
      if (payment.documentKind === 'Invoice') return;
      if (payment.invoiceId) return;
      const key = paymentLegacyKey(payment);
      if (!key) return;
      const existing = map.get(key) ?? [];
      map.set(key, [...existing, payment]);
    });
    return map;
  }, [payments]);

  const paymentsById = useMemo(() => {
    return new Map(payments.map((payment) => [payment.id, payment]));
  }, [payments]);

  const accountRows = useMemo(() => {
    const grouped = new Map<string, Invoice[]>();

    invoices.forEach((invoice) => {
      const canonicalId = invoiceEntityId(invoice);
      const legacyKey = invoiceLegacyKey(invoice.number, invoice.date);
      const groupKey = canonicalId ? `entity:${canonicalId}` : `legacy:${legacyKey ?? invoice.id}`;
      const existing = grouped.get(groupKey) ?? [];
      grouped.set(groupKey, [...existing, invoice]);
    });

    return Array.from(grouped.values()).map<AccountRow>((groupInvoices) => {
      const representative =
        groupInvoices.find((item) => item.documentKind === 'Invoice') ?? groupInvoices[0];
      const canonicalId =
        invoiceEntityId(representative) ??
        groupInvoices.map((item) => invoiceEntityId(item)).find((value): value is number => Boolean(value)) ??
        null;
      const legacyKey = invoiceLegacyKey(representative.number, representative.date);
      const canonicalPayments = canonicalId ? paymentsByInvoiceEntityId.get(canonicalId) ?? [] : [];
      const legacyPayments = legacyKey ? legacyPaymentsByInvoiceKey.get(legacyKey) ?? [] : [];
      const invoiceRowIds = new Set(groupInvoices.map((item) => item.id));
      const paymentsFromInvoiceRows = groupInvoices
        .filter((item) => item.id !== representative.id)
        .map((item) => paymentsById.get(item.id))
        .filter((payment): payment is Payment => Boolean(payment));
      const paymentsForInvoice = mergePaymentsUnique(
        paymentsFromInvoiceRows,
        canonicalPayments.filter((payment) => !invoiceRowIds.has(payment.id)),
        legacyPayments.filter((payment) => !invoiceRowIds.has(payment.id)),
      );

      const dueDate = paymentsForInvoice.reduce<string | null>((acc, payment) => {
        if (!payment.plannedDate) return acc;
        if (!acc) return payment.plannedDate;
        return new Date(payment.plannedDate) > new Date(acc) ? payment.plannedDate : acc;
      }, representative.dueDate ?? null);

      const paidDate = paymentsForInvoice.reduce<string | null>((acc, payment) => {
        if (!payment.paidDate) return acc;
        if (!acc) return payment.paidDate;
        return new Date(payment.paidDate) > new Date(acc) ? payment.paidDate : acc;
      }, representative.paidDate ?? null);

      const summedFromPayments = paymentsForInvoice.reduce((sum, payment) => sum + (payment.amount ?? 0), 0);
      const fallbackSum = groupInvoices.reduce((sum, invoice) => sum + (invoice.amount ?? 0), 0);

      return {
        accountNumber: representative.number,
        invoice: representative,
        payments: paymentsForInvoice,
        totalAmount: summedFromPayments > 0 ? summedFromPayments : fallbackSum,
        dueDate,
        paidDate,
        status: deriveStatusFromPayments(paymentsForInvoice, representative),
        type: representative.type ?? 'Income',
      };
    });
  }, [invoices, legacyPaymentsByInvoiceKey, paymentsById, paymentsByInvoiceEntityId]);

  const loadActsForInvoices = useCallback(async () => {
    const invoiceNumbers = Array.from(
      new Set(
        accountRows
          .map((row) => accountKey(row.accountNumber))
          .filter((value): value is string => Boolean(value)),
      ),
    );

    if (!invoiceNumbers.length) {
      setActsByInvoice({});
      return;
    }

    setActsLoading(true);
    setActsError(null);
    try {
      const entries = await Promise.all(
        invoiceNumbers.map(async (number) => {
          const response = await apiService.getActs({ search: number, pageSize: 50 });
          const relatedActs = (response.items ?? []).filter(
            (act) => accountKey(act.invoiceNumber ?? act.number) === number,
          );
          return [number, relatedActs] as const;
        }),
      );
      setActsByInvoice(Object.fromEntries(entries));
    } catch (err) {
      setActsError(err instanceof Error ? err.message : 'Не удалось загрузить акты');
    } finally {
      setActsLoading(false);
    }
  }, [accountRows]);

  const summaryBuckets = useMemo(() => {
    const base = {
      total: { amount: 0, count: 0 },
      pending: { amount: 0, count: 0 },
      paid: { amount: 0, count: 0 },
      overdue: { amount: 0, count: 0 },
    };

    accountRows.forEach((row) => {
      base.total.amount += row.totalAmount;
      base.total.count += 1;

      if (row.status === 'paid') {
        base.paid.amount += row.totalAmount;
        base.paid.count += 1;
      } else if (row.status === 'partial') {
        base.pending.amount += row.totalAmount;
        base.pending.count += 1;
      }

      const isOverdue = row.payments.some((payment) => payment.status === 'Overdue');
      if (isOverdue) {
        base.overdue.amount += row.totalAmount;
        base.overdue.count += 1;
      }
    });

    return summary ?? base;
  }, [accountRows, summary]);

  useEffect(() => {
    void loadActsForInvoices();
  }, [loadActsForInvoices]);

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

  const filteredAccounts = useMemo(() => accountRows, [accountRows]);

  const mergedTotal = filteredAccounts.length;

  const totalPages = useMemo(() => {
    if (!effectivePageSize) return 1;
    return Math.max(1, Math.ceil(mergedTotal / effectivePageSize));
  }, [mergedTotal, effectivePageSize]);

  const pagedAccounts = useMemo(() => {
    const start = (page - 1) * effectivePageSize;
    return filteredAccounts.slice(start, start + effectivePageSize);
  }, [filteredAccounts, page, effectivePageSize]);

  const columns: TableColumn[] = useMemo(
    () => [
      { key: 'date', label: t('invoiceDate') ?? t('actDate') ?? 'Дата', sortable: true },
      { key: 'number', label: t('invoiceNumber') ?? 'Номер счёта', sortable: true },
      { key: 'amount', label: t('invoiceAmount') ?? t('amount') ?? 'Сумма', sortable: true },
      { key: 'counterparty', label: t('invoiceClient') ?? 'Поставщик услуг', sortable: true },
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
  const columnsByKey = useMemo(() => Object.fromEntries(columns.map((column) => [column.key, column])) as Record<ColumnKey, TableColumn>, [columns]);

  const orderedVisibleColumns = useMemo(
    () => columnOrder.filter((key) => visibleColumns.includes(key)).map((key) => columnsByKey[key]).filter(Boolean),
    [columnOrder, columnsByKey, visibleColumns],
  );

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const toggleColumn = (key: ColumnKey) => {
    setVisibleColumns((prev) => {
      if (prev.includes(key)) {
        const updated = prev.filter((c) => c !== key);
        return updated.length ? updated : prev;
      }
      return [...prev, key];
    });
  };

  const reorderColumns = (source: ColumnKey, target: ColumnKey) => {
    if (source === target) return;
    setColumnOrder((prev) => {
      const fromIndex = prev.indexOf(source);
      const toIndex = prev.indexOf(target);
      if (fromIndex < 0 || toIndex < 0) return prev;
      const next = [...prev];
      next.splice(fromIndex, 1);
      next.splice(toIndex, 0, source);
      return next;
    });
  };

  const handleColumnDragStart = (key: ColumnKey, event: React.DragEvent<HTMLDivElement>) => {
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', key);
    setDraggingColumn(key);
  };

  const handleColumnDrop = (target: ColumnKey, event: React.DragEvent<HTMLElement>) => {
    event.preventDefault();
    const source = event.dataTransfer.getData('text/plain') as ColumnKey;
    if (!source) return;
    reorderColumns(source, target);
    setDraggingColumn(null);
  };

  const handleColumnDragOver = (event: React.DragEvent<HTMLElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  };

  const toggleExpanded = (key: string) => {
    setExpandedAccounts((prev) => ({ ...prev, [key]: !prev[key] }));
  };

  const getColumnWidth = (key: ColumnKey) => columnWidths[key] ?? DEFAULT_COLUMN_WIDTHS[key];

  const handleResizeStart = (key: ColumnKey, event: React.MouseEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();

    const previousCursor = document.body.style.cursor;
    const previousSelect = document.body.style.userSelect;
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';

    resizingRef.current = {
      columnKey: key,
      startX: event.clientX,
      startWidth: getColumnWidth(key),
    };

    const handleMouseMove = (moveEvent: MouseEvent) => {
      if (!resizingRef.current) return;
      const delta = moveEvent.clientX - resizingRef.current.startX;
      const nextWidth = Math.max(96, resizingRef.current.startWidth + delta);
      setColumnWidths((prev) => ({
        ...prev,
        [resizingRef.current?.columnKey ?? key]: nextWidth,
      }));
    };

    const handleMouseUp = () => {
      resizingRef.current = null;
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousSelect;
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
  };

  */

  const buildPaymentModalInvoiceContext = useCallback(
    (invoice: Invoice | null | undefined): PaymentModalInvoiceContext | null => {
      const canonicalId = invoiceEntityId(invoice);
      if (!invoice || !canonicalId) return null;

      return {
        invoiceEntityId: canonicalId,
        invoiceRecordId: invoice.id,
        number: invoice.number,
        date: invoice.date,
        clientId: invoice.clientId ?? null,
        clientCaseId: invoice.clientCaseId ?? null,
        type: invoice.type ?? 'Income',
      };
    },
    [],
  );

  const openPaymentCreateModal = (invoice: Invoice | null | undefined) => {
    const context = buildPaymentModalInvoiceContext(invoice);
    if (!context) {
      toast.error('Сначала сохраните счет, затем можно добавлять связанные платежи.', 'Платежи счета');
      return;
    }

    setEditingPayment(null);
    setPaymentModalInvoiceContext(context);
    setPaymentModalType(context.type ?? 'Income');
    setPaymentModalOpen(true);
  };

  const openPaymentEditModal = (payment: Payment, invoice?: Invoice | null) => {
    setEditingPayment(payment);
    setPaymentModalInvoiceContext(buildPaymentModalInvoiceContext(invoice));
    setPaymentModalType(payment.type ?? 'Income');
    setPaymentModalOpen(true);
  };

  const handleClosePaymentModal = () => {
    setPaymentModalOpen(false);
    setEditingPayment(null);
    setPaymentModalInvoiceContext(null);
  };

  const handleSubmitPayment = async (payload: PaymentPayload | ({ id: number } & PaymentPayload)) => {
    const isUpdate = 'id' in payload;

    if (isUpdate) {
      await apiService.updatePayment(payload.id, payload);
    } else {
      await apiService.createPayment(payload as PaymentPayload);
    }

    await refreshAccounts();
    handleClosePaymentModal();
  };

  const handleDeletePayment = async (payment: Payment) => {
    if (!canDelete) return;
    const approved = await confirm({
      title: 'Удаление платежа',
      description: 'Удалить связанный платеж?',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    await apiService.deletePayment(payment.id);
    toast.success('Платеж удален.');
    await refreshAccounts();
  };

  const openActCreateModal = (row: AccountRow) => {
    setActModalMode('create');
    setActiveAct(null);
    const normalizedDate =
      toDateInputValue(row.invoice?.date ?? row.dueDate) || formatLocalYMD(new Date());
    setActModalInvoice(row);
    setActInitialValues({
      invoiceId: row.invoice?.id ?? null,
      invoiceNumber: row.accountNumber,
      number: row.invoice?.actNumber ?? row.accountNumber,
      amount: row.totalAmount,
      title: row.invoice?.description ?? row.invoice?.actTitle ?? '',
      date: normalizedDate,
      clientId: row.invoice?.clientId ?? null,
      responsibleId: row.invoice?.responsibleId ?? null,
      comment: row.invoice?.actReference ?? '',
      status: 'Created',
    });
    setActModalError(null);
    setActModalOpen(true);
  };

  const openActEditModal = (act: Act, row: AccountRow) => {
    setActModalMode('edit');
    setActiveAct(act);
    setActModalInvoice(row);
    setActInitialValues({
      invoiceId: act.invoiceId ?? row.invoice?.id ?? null,
      invoiceNumber: act.invoiceNumber ?? row.accountNumber,
      number: act.number,
      title: act.title ?? '',
      date: act.date,
      amount: act.amount,
      clientId: act.clientId ?? row.invoice?.clientId ?? null,
      responsibleId: act.responsibleId ?? row.invoice?.responsibleId ?? null,
      comment: act.comment ?? '',
      status: act.status,
      counterpartyInn: act.counterpartyInn ?? '',
    });
    setActModalError(null);
    setActModalOpen(true);
  };

  const closeActModal = () => {
    if (actModalSubmitting) return;
    setActModalOpen(false);
    setActModalInvoice(null);
    setActiveAct(null);
    setActModalMode('create');
    setActInitialValues(null);
    setActModalError(null);
  };

  const handleSubmitAct = async (payload: ActInput) => {
    setActModalSubmitting(true);
    setActModalError(null);
    try {
      if (actModalMode === 'edit' && activeAct) {
        await apiService.updateAct(activeAct.id, {
          ...payload,
          invoiceId: payload.invoiceId ?? actModalInvoice?.invoice?.id,
          invoiceNumber: payload.invoiceNumber || actModalInvoice?.accountNumber || payload.number,
        });
      } else {
        await apiService.createAct({
          ...payload,
          invoiceId: payload.invoiceId ?? actModalInvoice?.invoice?.id,
          invoiceNumber: payload.invoiceNumber || actModalInvoice?.accountNumber || payload.number,
        });
      }
      await refreshAccounts();
      closeActModal();
    } catch (err) {
      setActModalError(err instanceof Error ? err.message : 'Не удалось создать акт');
    } finally {
      setActModalSubmitting(false);
    }
  };

  const currentPageInvoice = pageEditingInvoice;
  const currentPageAccountRow = currentPageInvoice
    ? accountRows.find((row) => {
        const rowCanonicalId = invoiceEntityId(row.invoice);
        const currentCanonicalId = invoiceEntityId(currentPageInvoice);
        if (rowCanonicalId && currentCanonicalId) {
          return rowCanonicalId === currentCanonicalId;
        }
        return row.invoice?.id === currentPageInvoice.id;
      }) ?? null
    : null;

  const paymentModalElement = (
    <PaymentModal
      isOpen={paymentModalOpen}
      onClose={handleClosePaymentModal}
      onSubmit={handleSubmitPayment}
      payment={editingPayment ?? undefined}
      type={paymentModalType}
      linkedInvoice={paymentModalInvoiceContext}
      onDelete={
        canDelete
          ? async (id) => {
              await apiService.deletePayment(id);
              toast.success('Платеж удален.');
              await refreshAccounts();
              handleClosePaymentModal();
            }
          : undefined
      }
      onCreateRemainder={async (payload) => {
        const created = await apiService.createPayment(payload);
        await refreshAccounts();
        return created;
      }}
    />
  );

  if (pageEditingInvoiceId && pageInvoiceLoading && !currentPageInvoice) {
    return (
      <div className="min-h-screen bg-gray-50">
        <div className="mx-auto flex min-h-screen max-w-[calc(100vw-2rem)] items-center justify-center px-4 py-8 sm:px-6 lg:px-8">
          <div className="inline-flex items-center gap-3 rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 shadow-sm">
            <Loader2 className="h-4 w-4 animate-spin" />
            {t('loading') ?? 'Загрузка...'}
          </div>
        </div>
        {paymentModalElement}
      </div>
    );
  }

  if (currentPageInvoice) {
    return (
      <div className="min-h-screen bg-gray-50">
        <InvoiceModal
          open
          mode="edit"
          presentation="page"
          invoice={currentPageInvoice}
          resumeDraft={resumeInvoiceDraft}
          onClose={closeEditPage}
          onBack={closeEditPage}
          onOpenClient={openClientFromEditor}
          onSubmit={submitInvoice}
          submitting={submitting}
          errorMessage={modalError}
          clients={clients}
          responsibles={actResponsibles}
          lookupsLoading={lookupsLoading}
          lookupsError={lookupsError}
          defaultClientId={undefined}
          defaultResponsibleId={user?.id}
          defaultType={typeFilter === 'All' ? undefined : typeFilter}
          lockType={lockType}
          relatedPayments={currentPageAccountRow?.payments ?? []}
          paymentsLoading={isLoading || isRefreshing}
          onAddRelatedPayment={canEdit ? () => openPaymentCreateModal(currentPageInvoice) : undefined}
          onEditRelatedPayment={canEdit ? (payment) => openPaymentEditModal(payment, currentPageInvoice) : undefined}
          onDeleteRelatedPayment={canDelete ? handleDeletePayment : undefined}
        />
        {paymentModalElement}
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 py-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex flex-wrap items-start justify-between gap-4">
          <div className="flex flex-col gap-2">
            <h1 className="text-2xl font-bold text-gray-900">{headingTitle}</h1>
            <p className="text-sm text-gray-500">{headingDescription}</p>
          </div>

          {canCreate && (
            <button
              type="button"
              onClick={openCreateModal}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-500"
            >
              <Plus className="h-4 w-4" />
              {t('invoiceAdd') ?? 'Добавить счёт'}
            </button>
          )}
        </div>

        <div className="mb-6 rounded-2xl border border-gray-200 bg-white p-4 shadow-sm">
          <div className="flex flex-wrap items-stretch gap-4">
            <div className="flex min-w-[260px] flex-1 flex-col gap-3">
              {lockType ? (
                <span
                  className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${
                    typeBadgeClasses[typeFilter === 'Expense' ? 'Expense' : 'Income']
                  }`}
                >
                  {typeFilterLabels[typeFilter === 'Expense' ? 'Expense' : 'Income']}
                </span>
              ) : (
                <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-gray-200 bg-slate-50 px-1 py-1 shadow-sm">
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
                    className={`flex min-w-[11rem] flex-1 items-center gap-3 rounded-xl bg-white p-4 shadow-sm ${card.accent}`}
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

            <div className="flex min-w-[320px] flex-1 flex-col gap-3 rounded-xl bg-slate-50/80 p-3">
              <div className="grid w-full gap-3 sm:grid-cols-2 lg:grid-cols-3">
                <label className="flex flex-col gap-1 text-sm">
                  <span className="text-center font-medium text-gray-700">{t('invoiceFiltersPeriod') ?? 'Период с'}</span>
                  <div className="relative">
                    <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
                    <input
                      type="date"
                      value={from}
                      onChange={(event) => setFrom(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                </label>
                <label className="flex flex-col gap-1 text-sm">
                  <span className="text-center font-medium text-gray-700">{t('invoiceFiltersPeriodTo') ?? 'Период по'}</span>
                  <div className="relative">
                    <CalendarRange className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
                    <input
                      type="date"
                      value={to}
                      onChange={(event) => setTo(event.target.value)}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                </label>
                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-gray-700">{t('invoiceFiltersStatus') ?? t('status') ?? 'Статус'}</span>
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

              <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
                <div className="relative w-full md:flex-1">
                  <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-gray-400" />
                  <input
                    type="search"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder={t('invoiceSearchPlaceholder') ?? 'Номер счёта, клиент или акт'}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-10 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  />
                </div>

                <div className="flex flex-wrap items-center gap-2 md:flex-nowrap md:justify-end">
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
                    onClick={() => void refreshAccounts()}
                    className="inline-flex items-center gap-1 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-600 transition hover:bg-blue-100"
                  >
                    {isRefreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <CalendarRange className="h-4 w-4" />}
                    {t('actFiltersApply') ?? 'Обновить'}
                  </button>
                  <div className="relative">
                    <button
                      type="button"
                      onClick={() => setColumnsOpen((prev) => !prev)}
                      className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition-colors ${
                        columnsOpen
                          ? 'border-blue-200 bg-blue-50 text-blue-700'
                          : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-100'
                      }`}
                    >
                      <SlidersHorizontal className="h-4 w-4" />
                      {t('tableSettings') ?? 'Таблица'}
                    </button>
                    {false && columnsOpen && (
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
          </div>
        </div>

        {columnsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={columns.map((column) => ({
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
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                  >
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

        {summaryBuckets.pending?.count > 0 && (
          <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {t('invoiceSummaryPending') ?? 'Ожидает оплаты'}: {summaryBuckets.pending.count}{' '}
            · {formatCurrencySmart(summaryBuckets.pending.amount).full}
          </div>
        )}

        <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-full table-fixed divide-y divide-gray-200">
              <colgroup>
                {orderedVisibleColumns
                  .map((column) => (
                    <col key={column.key} style={{ width: getColumnWidth(column.key) }} />
                  ))}
                {(canEdit || canDelete) && <col style={{ width: 148 }} />}
              </colgroup>
              <thead className="bg-gray-50">
                <tr className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                  {orderedVisibleColumns
                    .map((column) => {
                      const isSortable = column.sortable !== false && column.key !== 'status';
                      const isActive = sortState.key === column.key;
                      const direction = isActive ? sortState.direction : undefined;
                      const sortKey = (column.key as SortState['key']) ?? 'date';

                      return (
                        <th
                          key={column.key}
                          scope="col"
                          style={{ width: getColumnWidth(column.key) }}
                          className={`relative px-4 py-3 text-center align-middle ${draggingColumn === column.key ? 'bg-blue-50' : ''}`}
                          onDragOver={handleColumnDragOver}
                          onDrop={(event) => handleColumnDrop(column.key, event)}
                        >
                          <button
                            type="button"
                            className="absolute left-1 top-1/2 z-10 inline-flex -translate-y-1/2 cursor-grab rounded p-1 text-gray-400 active:cursor-grabbing"
                            draggable
                            onDragStart={(event) => handleColumnDragStart(column.key, event)}
                            onDragEnd={handleColumnDragEnd}
                            title="Перетащите для изменения порядка"
                          >
                            <GripVertical className="h-3.5 w-3.5" />
                          </button>
                          {isSortable ? (
                            <button
                              type="button"
                              onClick={() => {
                                handleSort(sortKey);
                                setPage(1);
                              }}
                              className="inline-flex items-center justify-center gap-1 pl-6 text-xs font-semibold uppercase tracking-wide text-gray-500"
                            >
                              {column.label}
                              <span className="text-gray-400">{isActive ? (direction === 'asc' ? '▲' : '▼') : ''}</span>
                            </button>
                          ) : (
                            <span className="inline-flex items-center justify-center pl-6">{column.label}</span>
                          )}
                          <div
                            role="separator"
                            aria-label={`Изменить ширину колонки ${column.label}`}
                            className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                            onMouseDown={(event) => handleResizeStart(column.key, event)}
                            onDragStart={(dragEvent) => dragEvent.preventDefault()}
                          />
                        </th>
                      );
                    })}
                  {(canEdit || canDelete) && (
                    <th className="px-4 py-3 text-center align-middle text-xs font-semibold uppercase tracking-wide text-gray-500">
                      {t('actions') ?? 'Действия'}
                    </th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {isLoading ? (
                  <tr>
                    <td colSpan={orderedVisibleColumns.length + (canEdit || canDelete ? 1 : 0)} className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : pagedAccounts.length === 0 ? (
                  <tr>
                    <td colSpan={orderedVisibleColumns.length + (canEdit || canDelete ? 1 : 0)} className="py-10 text-center text-gray-500">
                      {t('invoiceNoData') ?? 'Счета не найдены'}
                    </td>
                  </tr>
                ) : (
                  pagedAccounts.map((row) => {
                    const invoice = row.invoice;
                    const accountStatus = row.status;
                    const type = row.type;
                    const typeBadgeClass = typeBadgeClasses[type];
                    const typeLabel = typeLabels[type];
                    const actsForInvoice = row.acts ?? [];
                    const primaryName = invoice?.clientName || invoice?.counterpartyName || row.payments[0]?.client?.name || '—';
                    const caseLines = getInvoiceCaseSummary(invoice);
                    const clientId = typeof invoice?.clientId === 'number' ? invoice.clientId : null;
                    const isExpanded = expandedAccounts[row.accountNumber] ?? false;
                    const colSpan = orderedVisibleColumns.length + (canEdit || canDelete ? 1 : 0);

                    return (
                      <Fragment key={`${row.accountNumber}-${invoice?.id ?? 'no-invoice'}`}>
                        <tr
                          className={`hover:bg-gray-50/70 ${canEdit && invoice ? 'cursor-pointer' : ''}`}
                          onClick={() => {
                            if (canEdit && invoice) {
                              openEditPage(invoice.id);
                            }
                          }}
                        >
                          {orderedVisibleColumns.map((column) => {
                            switch (column.key) {
                              case 'date':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle font-medium text-gray-900">
                                    {formatDate(invoice?.date)}
                                  </td>
                                );
                              case 'number':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-gray-900">
                                    <button
                                      type="button"
                                      onClick={(event) => { event.stopPropagation(); toggleExpanded(row.accountNumber); }}
                                      className="inline-flex items-center gap-2 font-semibold text-blue-700"
                                    >
                                      <ChevronDown
                                        className={`h-4 w-4 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                                      />
                                      {row.accountNumber}
                                    </button>
                                  </td>
                                );
                              case 'amount':
                                return (
                                  <td key={column.key} className="px-4 py-3">
                                    <span className="font-semibold text-gray-900">{formatCurrencySmart(row.totalAmount).full}</span>
                                  </td>
                                );
                              case 'counterparty':
                                return (
                                  <td key={column.key} className="px-4 py-3">
                                    <div className="flex flex-col gap-1">
                                      <div className="flex flex-wrap items-center gap-2">
                                        {clientId && canOpenClientFromAccounts ? (
                                          <button
                                            type="button"
                                            onClick={(event) => { event.stopPropagation(); onOpenClient?.(clientId); }}
                                            className="font-medium text-blue-700 transition-colors hover:text-blue-800 hover:underline"
                                            title="Открыть карточку клиента"
                                          >
                                            {primaryName}
                                          </button>
                                        ) : (
                                          <span className="font-medium text-gray-900">{primaryName}</span>
                                        )}
                                        <ClientStatusBadge status={invoice?.clientStatus} />
                                        <span
                                          className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClass}`}
                                        >
                                          {typeLabel}
                                        </span>
                                      </div>
                                      {invoice?.clientCompany && <div className="text-xs text-gray-500">{invoice.clientCompany}</div>}
                                      {caseLines.length > 0 && (
                                        <div className="text-xs text-gray-500">
                                          {(t('cases') ?? 'Дела')}: {caseLines.join(', ')}
                                        </div>
                                      )}
                                    </div>
                                  </td>
                                );
                              case 'status':
                                return (
                                  <td key={column.key} className="px-4 py-3">
                                    <span
                                      className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${statusClasses[accountStatus]}`}
                                    >
                                      {statusLabels[accountStatus]}
                                    </span>
                                    {invoice?.paymentStatusName && <div className="mt-1 text-xs text-gray-500">{invoice.paymentStatusName}</div>}
                                  </td>
                                );
                              case 'dueDate':
                                return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.dueDate)}</td>;
                              case 'paidDate':
                                return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.paidDate)}</td>;
                              case 'act':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    <div className="flex flex-col items-center gap-2">
                                      {false && actsForInvoice.length === 0 && (
                                        <div className="text-xs text-gray-400">{t('loading') ?? 'Загрузка...'}</div>
                                      )}
                                      {actsForInvoice.length > 0 ? (
                                        <>
                                          <div className="flex items-center justify-center gap-2">
                                            {canCreate && (
                                              <button
                                                type="button"
                                                onClick={() => openActCreateModal(row)}
                                                className="rounded-full border border-blue-200 bg-blue-50 p-2 text-blue-700 transition hover:bg-blue-100"
                                                aria-label={t('addAct') ?? 'Добавить акт'}
                                              >
                                                <FilePlus2 className="h-4 w-4" />
                                              </button>
                                            )}
                                          </div>
                                          <div className="flex flex-col items-center gap-2">
                                            {actsForInvoice.map((act) => (
                                              <button
                                                key={act.id}
                                                type="button"
                                                onClick={() => openActEditModal(act, row)}
                                                className="group inline-flex flex-wrap items-center justify-center gap-2 rounded-full border border-gray-200 bg-gray-50 px-3 py-1.5 text-xs font-semibold text-blue-700 shadow-sm transition hover:border-blue-200 hover:bg-blue-50"
                                              >
                                                <span>{act.number ? `Акт №${act.number}` : t('act') ?? 'Акт'}</span>
                                                <span className="text-[11px] font-medium text-gray-500">{formatDate(act.date)}</span>
                                                <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] font-semibold text-slate-700">{actStatusLabels[act.status]}</span>
                                              </button>
                                            ))}
                                          </div>
                                        </>
                                      ) : (
                                        canCreate && (
                                          <button
                                            type="button"
                                            onClick={() => openActCreateModal(row)}
                                            className="rounded-full border border-blue-200 bg-blue-50 p-2 text-blue-700 transition hover:bg-blue-100"
                                            aria-label={t('actCreateFromInvoice') ?? 'Создать акт из счёта'}
                                          >
                                            <FilePlus2 className="h-5 w-5" />
                                          </button>
                                        )
                                      )}
                                    </div>
                                  </td>
                                );
                              case 'responsible':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    {invoice?.responsibles?.length
                                      ? invoice.responsibles.map((item) => item.fullName).join(', ')
                                      : invoice?.responsibleName
                                        ? invoice.responsibleName
                                        : '—'}
                                  </td>
                                );
                              case 'type':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    <span
                                      className={`inline-flex items-center justify-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClass}`}
                                    >
                                      {typeLabel}
                                    </span>
                                  </td>
                                );
                              case 'description':
                                return <td key={column.key} className="px-4 py-3 text-gray-600">{invoice?.description || '—'}</td>;
                              default:
                                return null;
                            }
                          })}
                          {(canEdit || canDelete) && (
                            <td className="px-4 py-3">
                              <div className="flex items-center justify-end gap-2">
                                {canEdit && invoice && (
                                  <button
                                    type="button"
                                    onClick={(event) => { event.stopPropagation(); openEditPage(invoice.id); }}
                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                    title={t('edit') ?? 'Редактировать'}
                                    aria-label={t('edit') ?? 'Редактировать'}
                                  >
                                    <Pencil className="h-4 w-4" />
                                  </button>
                                )}
                                {canDelete && invoice && (
                                  <button
                                    type="button"
                                    onClick={(event) => { event.stopPropagation(); void removeInvoice(invoice); }}
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
                        {isExpanded && (row.payments.length > 0 || canEdit) && (
                          <tr>
                            <td colSpan={colSpan} className="bg-gray-50 px-4 py-3">
                              <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
                                <div className="flex items-center justify-between gap-3 border-b border-gray-100 px-4 py-2">
                                  <div className="text-xs font-semibold uppercase text-gray-500">
                                    {t('paymentOperations') ?? 'Операции по счёту'}
                                  </div>
                                  {canEdit && invoice && (
                                    <button
                                      type="button"
                                      onClick={() => openPaymentCreateModal(invoice)}
                                      className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-xs font-semibold text-blue-700 transition hover:bg-blue-100"
                                    >
                                      <Plus className="h-3.5 w-3.5" />
                                      {t('addPayment') ?? 'Добавить платеж'}
                                    </button>
                                  )}
                                </div>
                                <div className="overflow-x-auto">
                                  <table className="min-w-full table-fixed divide-y divide-gray-100 text-sm">
                                    <colgroup>
                                      <col style={{ width: canEdit || canDelete ? '18%' : '22%' }} />
                                      <col style={{ width: '18%' }} />
                                      <col style={{ width: '18%' }} />
                                      <col style={{ width: canEdit || canDelete ? '30%' : '42%' }} />
                                      {(canEdit || canDelete) ? <col style={{ width: '16%' }} /> : null}
                                    </colgroup>
                                    <thead className="bg-gray-50 text-left text-xs font-medium uppercase text-gray-500">
                                      <tr>
                                        <th className="px-4 py-2">{t('paymentDate') ?? 'Дата платежа'}</th>
                                        <th className="px-4 py-2">{t('amount') ?? 'Сумма'}</th>
                                        <th className="px-4 py-2">{t('status') ?? 'Статус'}</th>
                                        <th className="px-4 py-2">{t('description') ?? 'Описание'}</th>
                                        {(canEdit || canDelete) && (
                                          <th className="px-4 py-2 text-right">{t('actions') ?? 'Действия'}</th>
                                        )}
                                      </tr>
                                    </thead>
                                    <tbody className="divide-y divide-gray-100">
                                      {row.payments.map((payment) => (
                                        <tr key={payment.id}>
                                          <td className="px-4 py-2">{formatDate(payment.plannedDate)}</td>
                                          <td className="px-4 py-2 font-semibold text-gray-900">
                                            {formatCurrencySmart(payment.amount).full}
                                          </td>
                                          <td className="px-4 py-2">
                                            <span className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">
                                              {statusLabels[deriveStatusFromPayments([payment], invoice)]}
                                            </span>
                                          </td>
                                          <td className="px-4 py-2 text-gray-600">{payment.description || '—'}</td>
                                          {(canEdit || canDelete) && (
                                            <td className="px-4 py-2">
                                              <div className="flex items-center justify-end gap-2">
                                                {canEdit && (
                                                  <button
                                                    type="button"
                                                    onClick={() => openPaymentEditModal(payment, invoice)}
                                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 transition hover:bg-gray-50"
                                                    title={t('edit') ?? 'Редактировать'}
                                                    aria-label={t('edit') ?? 'Редактировать'}
                                                  >
                                                    <Pencil className="h-4 w-4" />
                                                  </button>
                                                )}
                                                {canDelete && (
                                                  <button
                                                    type="button"
                                                    onClick={() => void handleDeletePayment(payment)}
                                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 transition hover:bg-red-50"
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
                                      ))}
                                    </tbody>
                                  </table>
                                </div>
                              </div>
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>

          {error && (
            <div className="border-t border-gray-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{String(error)}</div>
          )}

          {false && !isLoading && mergedTotal > 0 && (
            <div className="flex flex-col gap-3 border-t border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex flex-wrap items-center gap-4">
                <div>
                  {t('recordsFound') ?? 'Найдено записей'}: {mergedTotal}
                </div>
                <label className="flex items-center gap-2 text-sm text-gray-700">
                  <span>{t('rowsPerPage') ?? 'Строк на странице'}:</span>
                  <select
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-gray-200 px-2 py-1 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  >
                    {[10, 15, 25, 50, 100].map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </select>
                </label>
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
          {!isLoading && mergedTotal > 0 && (
            <TablePagination
              page={page}
              totalPages={totalPages}
              totalItems={mergedTotal}
              pageSize={pageSize}
              pageSizeOptions={PAGE_SIZE_OPTIONS}
              onPageChange={setPage}
              onPageSizeChange={setPageSize}
              recordsLabel={t('recordsFound') ?? 'Найдено записей'}
              pageSizeLabel={t('rowsPerPage') ?? 'Строк на странице'}
              previousLabel={t('previous') ?? 'Назад'}
              nextLabel={t('next') ?? 'Вперёд'}
            />
          )}
        </div>

        <InvoiceModal
          open={modalOpen}
          mode="create"
          invoice={selectedInvoice}
          resumeDraft={resumeInvoiceDraft}
          onClose={closeModal}
          onOpenClient={openClientFromEditor}
          onSubmit={submitInvoice}
          submitting={submitting}
          errorMessage={modalError}
          clients={clients}
          responsibles={actResponsibles}
          lookupsLoading={lookupsLoading}
          lookupsError={lookupsError}
          defaultClientId={undefined}
          defaultResponsibleId={user?.id}
          defaultType={typeFilter === 'All' ? undefined : typeFilter}
          lockType={lockType}
        />

        <ActModal
          open={actModalOpen}
          mode={actModalMode}
          act={activeAct}
          onClose={closeActModal}
          onSubmit={handleSubmitAct}
          submitting={actModalSubmitting}
          errorMessage={actModalError}
          clients={actClients.length ? actClients : clients}
          responsibles={actResponsibles}
          lookupsLoading={actLookupsLoading}
          lookupsError={actLookupsError}
          defaultClientId={actModalInvoice?.invoice?.clientId ?? null}
          defaultResponsibleId={actModalInvoice?.invoice?.responsibleId ?? null}
          initialValues={actInitialValues ?? undefined}
        />

        {paymentModalElement}
      </div>
    </div>
  );
}
