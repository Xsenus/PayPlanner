import { PaymentCreateAdoptedError } from '../../services/paymentCreateAttempts';
import { acknowledgeCreatedAct, actCreateSuccessMessage, type ActCreateOptions } from '../../services/actCreateAttempts';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { SystemSelect } from '../ui/SystemSelect';
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  FilePlus2,
  GripVertical,
  Loader2,
  Pencil,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  Table2,
  Trash2,
  UserRound,
  WalletCards,
  X,
} from 'lucide-react';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useToast } from '../../hooks/useToast';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useAccountsRegistry } from '../../hooks/useAccountsRegistry';
import { usePayablesDashboard } from '../../hooks/usePayablesDashboard';
import { useTableSelection } from '../../hooks/useTableSelection';
import { useDocumentStatusSettings } from '../../hooks/useDocumentStatusSettings';
import type {
  AccountRegistryRow,
  Act,
  ActWorkflowStatus,
  ActStatus,
  ActInput,
  ActResponsible,
  Client,
  ClientRole,
  Contract,
  Invoice,
  InvoiceInput,
  InvoiceWorkflowStatus,
  LegalEntitySummary,
  Payment,
  PaymentKind,
  PaymentPayload,
} from '../../types';
import { apiService } from '../../services/api';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getDocumentStatusCodes, getDocumentStatusLabels } from '../../utils/documentStatusSettings';
import { formatCurrencySmart } from '../../utils/formatters';
import { formatPeriodLabel, normalizeStoredPeriod, resolvePeriod, type PeriodFilter } from '../../utils/periodUtils';
import { readSessionViewState, writeSessionViewState } from '../../utils/sessionViewState';
import {
  clearInvoiceEditorDraft,
  peekInvoiceEditorDraft,
  type InvoiceEditorDraftState,
} from '../../utils/invoiceEditorBridge';
import { INSTALLMENT_PAYMENT_MARKER, isGeneratedInstallmentPayment, isInvoiceManagedPayment, isPartialRemainderSourcePayment, isTransferredRemainderPayment } from '../../utils/invoicePayments';
import { parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';
import { InvoiceModal } from './InvoiceModal';
import { PaymentModal } from '../Calendar/PaymentModal';
import { ActModal } from '../Acts/ActModal';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { DatePeriodPicker } from '../DateRange/DatePeriodPicker';
import { FormPromptModal } from '../ui/FormPromptModal';
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { TableContextMenu, type TableContextMenuAction } from '../ui/TableContextMenu';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { TableSelectCheckbox } from '../ui/TableSelectCheckbox';
import { StatusChangeModal, type StatusChangeOption } from '../ui/StatusChangeModal';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import { TASK_JOURNAL_EVENT } from '../../utils/taskJournalStore';

interface AccountsProps {
  defaultType?: PaymentKind;
  lockType?: boolean;
  routeInvoiceId?: number | null;
  onRouteInvoiceChange?: (invoiceId: number | null) => void;
  onRouteInvoicePageBack?: () => void;
  initialCreateDraft?: { key: number; draft: InvoiceEditorDraftState } | null;
  onInitialCreateDraftHandled?: (key: number) => void;
  onOpenClient?: (clientId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenPayment?: (paymentId: number, paymentType?: PaymentKind) => void;
  onOpenTaskBilling?: () => void;
  onOpenActPage?: (actId: number, options?: { invoiceId?: number | null }) => void;
  onCreateActPage?: (
    initialValues?: Partial<ActInput> & { amount?: number | null },
    options?: { invoiceId?: number | null; invoiceSeed?: Invoice | null },
  ) => void;
}

type FilterType = PaymentKind | 'All';
type AccountStatus = 'created' | 'paid' | 'partial' | 'overdue' | 'cancelled';
const INVOICE_WORKFLOW_STATUS_ORDER: InvoiceWorkflowStatus[] = ['Draft', 'NotSigned', 'RequiresClientSignature', 'OnApproval', 'Approved', 'ApprovalRejected', 'NeedsCorrection', 'OnSigning', 'SignedByClient', 'Cancelled'];
type InvoiceSigningStatus =
  | 'notSigned'
  | 'requiresClientSignature'
  | 'onApproval'
  | 'approved'
  | 'approvalRejected'
  | 'needsCorrection'
  | 'onSigning'
  | 'signed'
  | 'cancelled';
type ClientRoleFilter = ClientRole | 'all';
type ColumnKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'counterparty'
  | 'status'
  | 'signingStatus'
  | 'dueDate'
  | 'paidDate'
  | 'act'
  | 'responsible'
  | 'type'
  | 'description';

type SortState = { key: 'date' | 'number' | 'amount' | 'counterparty' | 'dueDate' | 'responsible'; direction: 'asc' | 'desc' };

type AccountsViewState = {
  statusFilter: 'all' | AccountStatus;
  typeFilter: FilterType;
  selectedCounterparty: CounterpartySelection | null;
  contractId: number | null;
  clientRoleFilter: ClientRoleFilter;
  payablesOnly: boolean;
  search: string;
  sortState: SortState;
  page: number;
  expandedAccounts: Record<string, boolean>;
};

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
  legalEntityId?: number | null;
  expenseClientId?: number | null;
  expenseLegalEntityId?: number | null;
  clientCaseId?: number | null;
  contractId?: number | null;
  type?: PaymentKind;
};

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

function formatRussianCount(value: number, forms: [string, string, string]): string {
  const absolute = Math.abs(value);
  const lastTwo = absolute % 100;
  const last = absolute % 10;

  if (lastTwo >= 11 && lastTwo <= 14) return `${value} ${forms[2]}`;
  if (last === 1) return `${value} ${forms[0]}`;
  if (last >= 2 && last <= 4) return `${value} ${forms[1]}`;
  return `${value} ${forms[2]}`;
}

function formatPaymentCount(value: number): string {
  return formatRussianCount(value, ['платёж', 'платежа', 'платежей']);
}

function formatSupplierCount(value: number): string {
  return formatRussianCount(value, ['поставщик', 'поставщика', 'поставщиков']);
}

function formatSupplierWord(value: number): string {
  return formatSupplierCount(value).replace(/^\d+\s+/, '');
}

interface IconActionButtonProps {
  label: string;
  onClick: () => void;
  children: ReactNode;
  active?: boolean;
  disabled?: boolean;
  className?: string;
}

function IconActionButton({
  label,
  onClick,
  children,
  active = false,
  disabled = false,
  className = '',
}: IconActionButtonProps) {
  return (
    <span className="group relative inline-flex">
      <button
        type="button"
        onClick={onClick}
        disabled={disabled}
        title={label}
        aria-label={label}
        className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border text-sm font-medium shadow-sm transition focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:cursor-not-allowed disabled:opacity-60 ${
          active
            ? 'border-blue-200 bg-blue-50 text-blue-700 hover:bg-blue-100'
            : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-100'
        } ${className}`}
      >
        {children}
      </button>
      <span className="pointer-events-none absolute bottom-full left-1/2 z-20 mb-2 max-w-[13rem] -translate-x-1/2 whitespace-nowrap rounded-md bg-slate-900 px-2.5 py-1 text-xs font-medium text-white opacity-0 shadow-lg transition group-hover:opacity-100 group-focus-within:opacity-100">
        {label}
      </span>
    </span>
  );
}

function resolvePaymentOperationDate(payment: Payment): string | null {
  const hasActualPayment =
    payment.status === 'Completed'
    || payment.isPaid
    || payment.hasPartialPayment
    || (payment.paidAmount ?? 0) > 0;

  if (hasActualPayment) {
    return payment.paidDate ?? payment.lastPaymentDate ?? payment.date ?? payment.plannedDate ?? null;
  }

  return payment.plannedDate ?? payment.date ?? null;
}

function resolveInstallmentPaymentBadge(payment: Payment): string | null {
  if (!isGeneratedInstallmentPayment(payment)) {
    return null;
  }

  const metadata = parseInstallmentPaymentMetadata(payment.systemNotes);
  const fallbackMetadata = extractInstallmentMetadataFallback(payment.systemNotes);
  const sequence = metadata?.sequence ?? fallbackMetadata.sequence ?? extractLegacyInstallmentSequence(payment.systemNotes);
  const months = metadata?.months ?? fallbackMetadata.months ?? 0;

  if (sequence > 0 && months > 0) {
    return `Рассрочка ${sequence}/${months}`;
  }

  if (sequence > 0) {
    return `Рассрочка ${sequence}`;
  }

  return 'Рассрочка';
}

function extractInstallmentMetadataFallback(systemNotes?: string | null): { sequence?: number; months?: number } {
  const markerPayload = extractInstallmentMarkerPayload(systemNotes);
  if (!markerPayload?.startsWith('{')) {
    return {};
  }

  try {
    const parsed = JSON.parse(markerPayload) as Record<string, unknown>;
    return {
      sequence: readNumberProperty(parsed, 'sequence'),
      months: readNumberProperty(parsed, 'months'),
    };
  } catch {
    return {};
  }
}

function extractLegacyInstallmentSequence(systemNotes?: string | null): number {
  const markerPayload = extractInstallmentMarkerPayload(systemNotes);
  const match = markerPayload?.match(/^(\d+)/);
  return match ? Number.parseInt(match[1], 10) : 0;
}

function extractInstallmentMarkerPayload(systemNotes?: string | null): string | null {
  const markerIndex = systemNotes?.indexOf(INSTALLMENT_PAYMENT_MARKER) ?? -1;
  if (!systemNotes || markerIndex < 0) {
    return null;
  }

  return systemNotes.slice(markerIndex + INSTALLMENT_PAYMENT_MARKER.length).trim();
}

function readNumberProperty(value: Record<string, unknown>, propertyName: string): number | undefined {
  const entry = Object.entries(value).find(([key]) => key.toLowerCase() === propertyName.toLowerCase());
  const rawValue = entry?.[1];
  return typeof rawValue === 'number' && Number.isFinite(rawValue) ? rawValue : undefined;
}

function getInvoiceCaseSummary(invoice: Invoice | null | undefined): string[] {
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

function getPaymentCaseSummary(payment?: Payment | null): string[] {
  if (!payment) return [];
  const title = payment.clientCaseTitle?.trim() || payment.clientCase?.title?.trim();
  if (title) return [title];
  if (payment.clientCaseId) return [`Дело #${payment.clientCaseId}`];
  return [];
}

function resolveAccountCounterparty(invoice: Invoice | null | undefined, payments: Payment[], type: Payment['type']) {
  const firstPayment = payments[0] ?? null;
  const counterpartyName = invoice?.counterpartyName?.trim() || firstPayment?.counterpartyName?.trim() || '';
  const expenseClientName = invoice?.expenseClientName?.trim() || firstPayment?.expenseClient?.name?.trim() || '';
  const expenseLegalEntityName = invoice?.expenseLegalEntityName?.trim() || firstPayment?.expenseLegalEntityName?.trim() || '';
  const clientName = invoice?.clientName?.trim() || firstPayment?.client?.name?.trim() || '';
  const legalEntityName = invoice?.legalEntityName?.trim() || firstPayment?.legalEntityName?.trim() || '';

  if (type === 'Expense') {
    const primaryName = counterpartyName || expenseLegalEntityName || expenseClientName || legalEntityName || clientName || '—';
    const primaryIsExpenseClient = !counterpartyName && !expenseLegalEntityName && Boolean(expenseClientName);
    const expenseBasis = [
      expenseClientName && expenseClientName !== primaryName ? `Клиент расхода: ${expenseClientName}` : null,
      expenseLegalEntityName && expenseLegalEntityName !== primaryName ? `Организация расхода: ${expenseLegalEntityName}` : null,
    ].filter((item): item is string => Boolean(item));

    return {
      primaryName,
      clientId: primaryIsExpenseClient ? invoice?.expenseClientId ?? firstPayment?.expenseClientId ?? null : null,
      clientStatus: primaryIsExpenseClient ? invoice?.expenseClientStatus ?? null : null,
      company: primaryIsExpenseClient ? invoice?.expenseClientCompany ?? null : null,
      caseLines: getInvoiceCaseSummary(invoice).length > 0 ? getInvoiceCaseSummary(invoice) : getPaymentCaseSummary(firstPayment),
      expenseBasis,
    };
  }

  return {
    primaryName: clientName || legalEntityName || counterpartyName || '—',
    clientId: invoice?.clientId ?? firstPayment?.clientId ?? null,
    clientStatus: invoice?.clientStatus ?? null,
    company: invoice?.clientCompany ?? null,
    caseLines: getInvoiceCaseSummary(invoice).length > 0 ? getInvoiceCaseSummary(invoice) : getPaymentCaseSummary(firstPayment),
    expenseBasis: [] as string[],
  };
}

const PAGE_SIZE_OPTIONS = [10, 15, 25, 50, 100];
const ACCOUNT_COLUMNS = [
  { key: 'date', label: 'Дата', defaultVisible: true, defaultWidth: 150 },
  { key: 'number', label: 'Номер счёта', defaultVisible: true, defaultWidth: 180 },
  { key: 'amount', label: 'Сумма', defaultVisible: true, defaultWidth: 150 },
  { key: 'counterparty', label: 'Клиент / поставщик', defaultVisible: true, defaultWidth: 240 },
  { key: 'status', label: 'Статус', defaultVisible: true, defaultWidth: 170 },
  { key: 'signingStatus', label: 'Статус документа', defaultVisible: true, defaultWidth: 180 },
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
  if (invoice.status === 'Overdue') return 'overdue';
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

  const anyOverdue = payments.some((payment) => payment.status === 'Overdue');
  if (anyOverdue) return 'overdue';

  const anyProgress = payments.some(
    (payment) => payment.status === 'Completed' || payment.status === 'Processing' || payment.hasPartialPayment || payment.paidAmount > 0,
  );

  return anyProgress ? 'partial' : 'created';
}

function deriveInvoiceSigningStatus(invoice?: Invoice | null): InvoiceSigningStatus {
  if (!invoice) return 'notSigned';
  const workflowStatus = invoice.workflowStatus;
  if (invoice.cancelledAt || workflowStatus === 'Cancelled') return 'cancelled';
  if (workflowStatus === 'RequiresClientSignature') return 'requiresClientSignature';
  if (workflowStatus === 'OnApproval') return 'onApproval';
  if (workflowStatus === 'Approved') return 'approved';
  if (workflowStatus === 'ApprovalRejected') return 'approvalRejected';
  if (workflowStatus === 'NeedsCorrection') return 'needsCorrection';
  if (workflowStatus === 'NotSigned') return 'notSigned';
  if (workflowStatus === 'SignedByClient') return 'signed';
  if (workflowStatus === 'OnSigning') return 'onSigning';
  if (invoice.signedAt) return 'signed';
  if (invoice.sentForSigningAt) return 'onSigning';
  return 'notSigned';
}

function resolveEditableInvoiceWorkflowStatus(invoice?: Invoice | null): InvoiceWorkflowStatus {
  const workflowStatus = invoice?.workflowStatus;
  if (
    workflowStatus === 'Draft' ||
    workflowStatus === 'NotSigned' ||
    workflowStatus === 'RequiresClientSignature' ||
    workflowStatus === 'OnApproval' ||
    workflowStatus === 'Approved' ||
    workflowStatus === 'ApprovalRejected' ||
    workflowStatus === 'NeedsCorrection' ||
    workflowStatus === 'OnSigning' ||
    workflowStatus === 'SignedByClient' ||
    workflowStatus === 'Cancelled'
  ) {
    return workflowStatus;
  }

  switch (deriveInvoiceSigningStatus(invoice)) {
    case 'cancelled':
      return 'Cancelled';
    case 'signed':
      return 'SignedByClient';
    case 'onSigning':
      return 'OnSigning';
    case 'requiresClientSignature':
      return 'RequiresClientSignature';
    case 'notSigned':
    default:
      return 'NotSigned';
  }
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

function normalizeAccountsPeriod(raw: unknown, fallback: PeriodFilter): PeriodFilter {
  // An explicitly cleared range means all dates; retain it when reopening the registry.
  if (raw && typeof raw === 'object' && 'mode' in raw && raw.mode === 'custom'
    && !('from' in raw && raw.from) && !('to' in raw && raw.to)) {
    return { mode: 'custom' };
  }
  return normalizeStoredPeriod(raw, fallback);
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

export function Accounts({
  defaultType,
  lockType = false,
  routeInvoiceId,
  onRouteInvoiceChange,
  onRouteInvoicePageBack,
  initialCreateDraft,
  onInitialCreateDraftHandled,
  onOpenClient,
  onOpenContract,
  onOpenPayment,
  onOpenTaskBilling,
  onOpenActPage,
  onCreateActPage,
}: AccountsProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const { t } = useTranslation();
  const documentStatusSettings = useDocumentStatusSettings({ enabled: permissions.accounts.canView, ownerScope: JSON.stringify([user?.id, user?.role?.id, permissions.accounts]) });

  const canCreate = permissions.accounts.canCreate;
  const canEdit = permissions.accounts.canEdit;
  const canDelete = permissions.accounts.canDelete;
  const canViewPayments = permissions.payments.canView;
  const canCreatePayment = canViewPayments && permissions.payments.canCreate;
  const canEditPayment = canViewPayments && permissions.payments.canEdit;
  const canDeletePayment = canViewPayments && permissions.payments.canDelete;
  const canViewActs = permissions.acts.canView;
  const canCreateAct = canViewActs && permissions.acts.canCreate;
  const canEditAct = canViewActs && permissions.acts.canEdit;
  const canViewClients = permissions.clients.canView;
  const canViewLegalEntities = permissions.legalEntities.canView;
  const canViewContracts = permissions.contracts.canView;
  const allowedCounterpartyScopes = useMemo<CounterpartyKind[]>(() => {
    const scopes: CounterpartyKind[] = [];
    if (canViewClients) scopes.push('client');
    if (canViewLegalEntities) scopes.push('legalEntity');
    return scopes;
  }, [canViewClients, canViewLegalEntities]);
  const canSelectCounterpartyFilter = allowedCounterpartyScopes.length > 0;

  const defaultPeriod = useMemo<PeriodFilter>(() => ({ mode: 'preset', preset: 'year_to_date' }), []);
  const storagePrefix = useMemo(() => `pp.accounts.${user?.id ?? 'guest'}`, [user?.id]);
  const viewStateKey = useMemo(
    () => `${storagePrefix}.viewState.${lockType && defaultType ? defaultType : 'All'}.v1`,
    [defaultType, lockType, storagePrefix],
  );
  const savedViewState = useMemo(
    () => readSessionViewState<Partial<AccountsViewState> | null>(viewStateKey, null),
    [viewStateKey],
  );
  const savedPeriod = useMemo(() => safeRead<unknown>(`${storagePrefix}.period`, null), [storagePrefix]);
  const [period, setPeriod] = useState<PeriodFilter>(() => normalizeAccountsPeriod(savedPeriod, defaultPeriod));
  const periodRange = useMemo(() => resolvePeriod(period), [period]);
  const [statusFilter, setStatusFilter] = useState<'all' | AccountStatus>(() => savedViewState?.statusFilter ?? 'all');
  const [typeFilter, setTypeFilter] = useState<FilterType>(() => (
    lockType && defaultType ? defaultType : savedViewState?.typeFilter ?? defaultType ?? 'All'
  ));
  const [selectedCounterparty, setSelectedCounterparty] = useState<CounterpartySelection | null>(() => savedViewState?.selectedCounterparty ?? null);
  const [counterpartyPickerOpen, setCounterpartyPickerOpen] = useState(false);
  const [selectedContractId, setSelectedContractId] = useState<number | null>(() => savedViewState?.contractId ?? null);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [contractsLoading, setContractsLoading] = useState(false);
  const [clientRoleFilter, setClientRoleFilter] = useState<ClientRoleFilter>(() => savedViewState?.clientRoleFilter ?? 'all');
  const [payablesOnly, setPayablesOnly] = useState(() => savedViewState?.payablesOnly ?? false);
  const [payablesDashboardOpen, setPayablesDashboardOpen] = useState(false);
  const [search, setSearch] = useState(() => savedViewState?.search ?? '');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>(() => savedViewState?.sortState ?? { key: 'date', direction: 'desc' });
  const [page, setPage] = useState(() => savedViewState?.page ?? 1);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [expandedAccounts, setExpandedAccounts] = useState<Record<string, boolean>>(() => savedViewState?.expandedAccounts ?? {});
  const filtersResetReadyRef = useRef(false);
  const [paymentModalOpen, setPaymentModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [paymentModalType, setPaymentModalType] = useState<PaymentKind>('Income');
  const [paymentModalInvoiceContext, setPaymentModalInvoiceContext] = useState<PaymentModalInvoiceContext | null>(null);
  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actModalInvoice, setActModalInvoice] = useState<AccountRow | null>(null);
  const [actModalMode, setActModalMode] = useState<'create' | 'edit'>('create');
  const [activeAct, setActiveAct] = useState<Act | null>(null);
  const [expandedActListKey, setExpandedActListKey] = useState<string | null>(null);
  const [actInitialValues, setActInitialValues] = useState<Partial<ActInput> & { amount?: number | null } | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actLookupsLoaded, setActLookupsLoaded] = useState(false);
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
  const [tableMenuPosition, setTableMenuPosition] = useState<{ x: number; y: number } | null>(null);
  const [changeResponsibleOpen, setChangeResponsibleOpen] = useState(false);
  const [changeResponsibleSubmitting, setChangeResponsibleSubmitting] = useState(false);
  const [changeResponsibleError, setChangeResponsibleError] = useState<string | null>(null);
  const [nextResponsibleId, setNextResponsibleId] = useState('');
  const [responsibleChangeInvoiceIds, setResponsibleChangeInvoiceIds] = useState<number[]>([]);
  const [responsibleChangeSubjectInvoice, setResponsibleChangeSubjectInvoice] = useState<Invoice | null>(null);
  const [statusChangeInvoice, setStatusChangeInvoice] = useState<Invoice | null>(null);
  const [statusChangeValue, setStatusChangeValue] = useState<InvoiceWorkflowStatus>('NotSigned');
  const [statusChangeReason, setStatusChangeReason] = useState('');
  const [statusChangeSubmitting, setStatusChangeSubmitting] = useState(false);
  const [statusChangeError, setStatusChangeError] = useState<string | null>(null);
  const baseTypeFilter = useMemo<FilterType>(() => (lockType ? (defaultType ?? 'All') : 'All'), [defaultType, lockType]);
  const effectiveSelectedCounterparty = selectedCounterparty && allowedCounterpartyScopes.includes(selectedCounterparty.kind)
    ? selectedCounterparty
    : null;
  const selectedClientIdNumber = effectiveSelectedCounterparty?.kind === 'client' ? effectiveSelectedCounterparty.id : undefined;
  const selectedLegalEntityIdNumber = effectiveSelectedCounterparty?.kind === 'legalEntity' ? effectiveSelectedCounterparty.id : undefined;
  const effectiveSelectedContractId = canViewContracts ? selectedContractId : null;

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
    getTableWidth,
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
    from: periodRange.from || undefined,
    to: periodRange.to || undefined,
    status: statusFilter,
    search: debouncedSearch || undefined,
    type: typeFilter,
    clientId: selectedClientIdNumber,
    legalEntityId: selectedLegalEntityIdNumber,
    contractId: effectiveSelectedContractId ?? undefined,
    clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
    payablesOnly,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  const {
    data: payablesDashboard,
    loading: payablesDashboardLoading,
    error: payablesDashboardError,
    refresh: refreshPayablesDashboard,
  } = usePayablesDashboard({
    from: periodRange.from || undefined,
    to: periodRange.to || undefined,
    search: debouncedSearch || undefined,
    clientId: selectedClientIdNumber,
    legalEntityId: selectedLegalEntityIdNumber,
    clientRole: clientRoleFilter === 'all' ? undefined : clientRoleFilter,
    enabled: payablesOnly,
  });

  useEffect(() => {
    if (!canViewContracts) {
      setContracts([]);
      setContractsLoading(false);
      return;
    }

    let active = true;
    const ownerParams = {
      ...(selectedClientIdNumber ? { clientId: selectedClientIdNumber } : {}),
      ...(selectedLegalEntityIdNumber ? { legalEntityId: selectedLegalEntityIdNumber } : {}),
    };

    setContractsLoading(true);
    void apiService
      .getContracts({ ...ownerParams, sortBy: 'date', sortDir: 'desc', page: 1, pageSize: 200 })
      .then((response) => {
        if (active) {
          setContracts(response.items ?? []);
        }
      })
      .catch(() => {
        if (active) {
          setContracts([]);
        }
      })
      .finally(() => {
        if (active) {
          setContractsLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [canViewContracts, selectedClientIdNumber, selectedLegalEntityIdNumber]);

  useEffect(() => {
    if (!selectedContractId || contractsLoading) return;
    if (!canViewContracts) {
      setSelectedContractId(null);
      return;
    }
    if (contracts.some((contract) => contract.id === selectedContractId)) return;
    setSelectedContractId(null);
  }, [canViewContracts, contracts, contractsLoading, selectedContractId]);

  const payablesOverdueCount = useMemo(
    () => payablesDashboard?.dueBuckets.find((bucket) => bucket.key === 'overdue')?.count ?? 0,
    [payablesDashboard],
  );
  const payablesUpcomingCount = useMemo(
    () => (payablesDashboard?.dueBuckets ?? [])
      .filter((bucket) => bucket.key === 'today' || bucket.key === 'week')
      .reduce((sum, bucket) => sum + bucket.count, 0),
    [payablesDashboard],
  );

  const totalPages = useMemo(
    () => Math.max(1, Math.ceil((accountsTotal || 0) / Math.max(1, pageSize || 1))),
    [accountsTotal, pageSize],
  );

  useEffect(() => {
    if (!filtersResetReadyRef.current) {
      filtersResetReadyRef.current = true;
      return;
    }

    setPage(1);
  }, [
    periodRange.from,
    periodRange.to,
    statusFilter,
    debouncedSearch,
    typeFilter,
    selectedClientIdNumber,
    selectedLegalEntityIdNumber,
    selectedContractId,
    clientRoleFilter,
    payablesOnly,
    pageSize,
  ]);

  useEffect(() => {
    if (lockType) {
      setTypeFilter(baseTypeFilter);
    }
  }, [baseTypeFilter, lockType]);

  useEffect(() => {
    setPeriod(normalizeAccountsPeriod(savedPeriod, defaultPeriod));
  }, [defaultPeriod, savedPeriod]);

  useEffect(() => {
    if (!payablesDashboardOpen || typeof window === 'undefined') {
      return undefined;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setPayablesDashboardOpen(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [payablesDashboardOpen]);

  useEffect(() => {
    safeWrite(`${storagePrefix}.period`, period);
  }, [period, storagePrefix]);

  useEffect(() => {
    const counterparty = effectiveSelectedCounterparty
      ? {
        kind: effectiveSelectedCounterparty.kind,
        id: effectiveSelectedCounterparty.id,
        name: effectiveSelectedCounterparty.name,
        subtitle: effectiveSelectedCounterparty.subtitle ?? null,
      }
      : null;

    writeSessionViewState<AccountsViewState>(viewStateKey, {
      statusFilter,
      typeFilter,
      selectedCounterparty: counterparty,
      contractId: effectiveSelectedContractId,
      clientRoleFilter,
      payablesOnly,
      search,
      sortState,
      page,
      expandedAccounts,
    });
  }, [
    clientRoleFilter,
    expandedAccounts,
    page,
    payablesOnly,
    search,
    effectiveSelectedCounterparty,
    effectiveSelectedContractId,
    sortState,
    statusFilter,
    typeFilter,
    viewStateKey,
  ]);

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, totalPages]);

  const deferEditorClose = useCallback((callback: () => void) => {
    window.setTimeout(callback, 160);
  }, []);

  const statusLabels: Partial<Record<AccountStatus, string>> = {
    created: 'Создан',
    paid: 'Оплачен',
    partial: 'Частично оплачен',
    cancelled: t('cancelledStatus') ?? 'Отменён',
  };

  const actStatusLabels: Record<ActStatus, string> = {
    Created: t('actStatusCreated') ?? 'Создан',
    Transferred: t('actStatusTransferred') ?? 'Передано',
    Signed: t('actStatusSigned') ?? 'Подписан клиентом',
    Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
  };
  const actSigningStatusLabels: Record<ActStatus, string> = {
    Created: 'Не подписан',
    Transferred: 'На подписании',
    Signed: 'Подписан клиентом',
    Terminated: 'Аннулирован',
  };
  const actSigningStatusClasses: Record<ActStatus, string> = {
    Created: 'border border-slate-200 bg-slate-100 text-slate-700',
    Transferred: 'border border-violet-200 bg-violet-100 text-violet-700',
    Signed: 'border border-emerald-200 bg-emerald-100 text-emerald-700',
    Terminated: 'border border-rose-200 bg-rose-100 text-rose-700',
  };
  const defaultActWorkflowStatusLabels = useMemo<Record<ActWorkflowStatus, string>>(
    () => ({
      Draft: 'Черновик',
      NotSigned: 'Не подписан',
      RequiresClientSignature: 'Требуется подпись клиента',
      OnApproval: 'На согласовании',
      Approved: 'Согласован',
      ApprovalRejected: 'Отказ в согласовании',
      NeedsCorrection: 'Требует исправления',
      OnSigning: 'На подписании',
      Signed: 'Подписан клиентом',
      Cancelled: 'Аннулирован',
    }),
    [],
  );
  const actWorkflowStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'act', defaultActWorkflowStatusLabels),
    [defaultActWorkflowStatusLabels, documentStatusSettings],
  );
  const actWorkflowStatusClasses: Record<ActWorkflowStatus, string> = {
    Draft: 'border border-slate-200 bg-slate-100 text-slate-700',
    NotSigned: 'border border-slate-200 bg-slate-100 text-slate-700',
    RequiresClientSignature: 'border border-amber-200 bg-amber-100 text-amber-800',
    OnApproval: 'border border-cyan-200 bg-cyan-100 text-cyan-700',
    Approved: 'border border-emerald-200 bg-emerald-100 text-emerald-700',
    ApprovalRejected: 'border border-rose-200 bg-rose-100 text-rose-700',
    NeedsCorrection: 'border border-orange-200 bg-orange-100 text-orange-700',
    OnSigning: 'border border-violet-200 bg-violet-100 text-violet-700',
    Signed: 'border border-emerald-200 bg-emerald-100 text-emerald-700',
    Cancelled: 'border border-rose-200 bg-rose-100 text-rose-700',
  };
  const legacyActWorkflowStatusByStatus: Record<ActStatus, ActWorkflowStatus> = {
    Created: 'NotSigned',
    Transferred: 'OnSigning',
    Signed: 'Signed',
    Terminated: 'Cancelled',
  };
  const resolveActWorkflowStatus = (act: Act): ActWorkflowStatus =>
    act.workflowStatus ?? legacyActWorkflowStatusByStatus[act.status];

  const statusClasses: Partial<Record<AccountStatus, string>> = {
    created: 'bg-amber-100 text-amber-700 border border-amber-200',
    paid: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    partial: 'bg-blue-100 text-blue-700 border border-blue-200',
    cancelled: 'bg-slate-100 text-slate-700 border border-slate-200',
  };

  const accountStatusLabels = useMemo<Record<AccountStatus, string>>(
    () => ({
      created: 'Не оплачен',
      paid: 'Оплачен',
      partial: 'Частично оплачен',
      overdue: 'Просрочен',
      cancelled: t('cancelledStatus') ?? 'Отменён',
    }),
    [t],
  );

  const accountStatusClasses: Record<AccountStatus, string> = {
    created: 'bg-amber-100 text-amber-700 border border-amber-200',
    paid: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    partial: 'bg-blue-100 text-blue-700 border border-blue-200',
    overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
    cancelled: 'bg-slate-100 text-slate-700 border border-slate-200',
  };
  const signingStatusLabels: Record<InvoiceSigningStatus, string> = {
    notSigned: 'Не подписан',
    requiresClientSignature: 'Требуется подпись клиента',
    onApproval: 'На согласовании',
    approved: 'Согласован',
    approvalRejected: 'Отказ в согласовании',
    needsCorrection: 'Требует исправления',
    onSigning: 'На подписании',
    signed: 'Подписан клиентом',
    cancelled: 'Аннулирован',
  };
  const signingStatusClasses: Record<InvoiceSigningStatus, string> = {
    notSigned: 'bg-slate-100 text-slate-700 border border-slate-200',
    requiresClientSignature: 'bg-amber-100 text-amber-800 border border-amber-200',
    onApproval: 'bg-cyan-100 text-cyan-700 border border-cyan-200',
    approved: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    approvalRejected: 'bg-rose-100 text-rose-700 border border-rose-200',
    needsCorrection: 'bg-orange-100 text-orange-700 border border-orange-200',
    onSigning: 'bg-violet-100 text-violet-700 border border-violet-200',
    signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    cancelled: 'bg-rose-100 text-rose-700 border border-rose-200',
  };
  const defaultInvoiceWorkflowStatusLabels = useMemo<Record<InvoiceWorkflowStatus, string>>(
    () => ({
      Draft: 'Черновик',
      NotSigned: 'Не подписан',
      RequiresClientSignature: 'Требуется подпись клиента',
      OnApproval: 'На согласовании',
      Approved: 'Согласован',
      ApprovalRejected: 'Отказ в согласовании',
      NeedsCorrection: 'Требует исправления',
      OnSigning: 'На подписании',
      SignedByClient: 'Подписан клиентом',
      Paid: 'Оплачен',
      PartiallyPaid: 'Частично оплачен',
      Overdue: 'Просрочен',
      Cancelled: 'Аннулирован',
    }),
    [],
  );

  const invoiceWorkflowStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'invoice', defaultInvoiceWorkflowStatusLabels),
    [defaultInvoiceWorkflowStatusLabels, documentStatusSettings],
  );
  const invoiceWorkflowStatusClasses = useMemo<Record<InvoiceWorkflowStatus, string>>(
    () => ({
      Draft: 'bg-slate-100 text-slate-700 border border-slate-200',
      NotSigned: 'bg-slate-100 text-slate-700 border border-slate-200',
      RequiresClientSignature: 'bg-amber-100 text-amber-800 border border-amber-200',
      OnApproval: 'bg-cyan-100 text-cyan-700 border border-cyan-200',
      Approved: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
      ApprovalRejected: 'bg-rose-100 text-rose-700 border border-rose-200',
      NeedsCorrection: 'bg-orange-100 text-orange-700 border border-orange-200',
      OnSigning: 'bg-violet-100 text-violet-700 border border-violet-200',
      SignedByClient: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
      Paid: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
      PartiallyPaid: 'bg-blue-100 text-blue-700 border border-blue-200',
      Overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
      Cancelled: 'bg-rose-100 text-rose-700 border border-rose-200',
    }),
    [],
  );
  const invoiceWorkflowStatusDescriptions = useMemo<Record<InvoiceWorkflowStatus, string>>(
    () => ({
      Draft: 'Документ вернется в черновик, даты отправки и подписи будут очищены.',
      NotSigned: 'Документ будет отмечен как неподписанный.',
      RequiresClientSignature: 'Документ требует подпись клиента; статус оплаты счета не меняется.',
      OnApproval: 'Документ будет отправлен на внутреннее согласование.',
      Approved: 'Документ будет отмечен как согласованный.',
      ApprovalRejected: 'В журнал попадет отказ в согласовании документа.',
      NeedsCorrection: 'Документ будет возвращен на исправление.',
      OnSigning: 'Документ будет отмечен как отправленный на подписание.',
      SignedByClient: 'Документ будет отмечен как подписанный клиентом.',
      Paid: 'Статус оплаты ведется отдельно.',
      PartiallyPaid: 'Статус оплаты ведется отдельно.',
      Overdue: 'Статус оплаты ведется отдельно.',
      Cancelled: 'Изменится только статус документа; статус оплаты счета не меняется.',
    }),
    [],
  );

  const invoiceWorkflowStatusCodes = useMemo(
    () => getDocumentStatusCodes(documentStatusSettings, 'invoice', INVOICE_WORKFLOW_STATUS_ORDER),
    [documentStatusSettings],
  );

  const invoiceWorkflowStatusOptions: StatusChangeOption<InvoiceWorkflowStatus>[] = useMemo(
    () => invoiceWorkflowStatusCodes.map((status) => ({
      value: status,
      label: invoiceWorkflowStatusLabels[status],
      description: invoiceWorkflowStatusDescriptions[status],
      className: invoiceWorkflowStatusClasses[status],
    })),
    [invoiceWorkflowStatusClasses, invoiceWorkflowStatusCodes, invoiceWorkflowStatusDescriptions, invoiceWorkflowStatusLabels],
  );
  const statusBadgeClass =
    'inline-flex max-w-full items-center justify-center rounded-full px-3 py-1 text-center text-xs font-medium leading-tight whitespace-normal';
  void statusLabels;
  void statusClasses;

  const typeLabels: Record<PaymentKind, string> = {
    Income: t('invoiceTypeIncome') ?? 'Доходные счета',
    Expense: t('invoiceTypeExpense') ?? 'Расходные счета',
  };

  const typeFilterLabels = useMemo<Record<FilterType, string>>(
    () => ({
      All: t('invoiceTypeAll') ?? 'Все счета',
      Income: t('invoiceTypeIncome') ?? 'Доходные счета',
      Expense: t('invoiceTypeExpense') ?? 'Расходные счета',
    }),
    [t],
  );

  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };

  const canOpenClientFromAccounts = useMemo(() => canViewClients && typeof onOpenClient === 'function', [canViewClients, onOpenClient]);

  const headingTitle =
    payablesOnly
      ? 'Кредиторка'
      : typeFilter === 'Expense'
      ? t('invoiceExpenseTitle') ?? 'Расходные счета'
      : typeFilter === 'Income'
        ? t('invoiceIncomeTitle') ?? t('invoicesTitle') ?? 'Доходные счета'
        : t('invoiceAllTitle') ?? t('accounts') ?? 'Все счета';

  const headingDescription =
    payablesOnly
      ? 'Расходные счета, которые еще нужно оплатить поставщикам.'
      : typeFilter === 'Expense'
      ? t('invoiceSectionDescriptionExpense') ??
        'Учитывайте входящие счета от поставщиков и контролируйте их оплату.'
      : typeFilter === 'Income'
        ? t('invoiceSectionDescriptionIncome') ??
          t('invoiceSectionDescription') ??
          'Отслеживайте выставленные счета и контролируйте их оплату.'
        : t('invoiceSectionDescriptionAll') ??
          t('invoiceSectionDescription') ??
          'Просматривайте все входящие и исходящие счета компании.';

  const hasCustomPeriod = useMemo(
    () => JSON.stringify(period) !== JSON.stringify(defaultPeriod),
    [defaultPeriod, period],
  );
  const hasCustomStatusFilter = statusFilter !== 'all';
  const hasCustomSearch = search.trim().length > 0;
  const hasCustomTypeFilter = typeFilter !== baseTypeFilter;
  const hasCustomClientFilter = Boolean(effectiveSelectedCounterparty);
  const hasCustomContractFilter = effectiveSelectedContractId !== null;
  const hasCustomClientRoleFilter = clientRoleFilter !== 'all';
  const selectedContract = useMemo(
    () => contracts.find((contract) => contract.id === selectedContractId) ?? null,
    [contracts, selectedContractId],
  );
  const contractLookupOptions = useMemo<LookupSelectOption[]>(
    () =>
      contracts.map((contract) => {
        const clientNames = contract.clients
          ?.map((client) => client.name)
          .filter(Boolean)
          .join(', ');
        const label = `№${contract.number}${contract.title ? ` · ${contract.title}` : ''}`;
        const descriptionParts = [
          contract.date ? `от ${formatDate(contract.date)}` : null,
          clientNames || null,
        ].filter((item): item is string => Boolean(item));

        return {
          value: String(contract.id),
          label,
          description: descriptionParts.join(' · ') || undefined,
          keywords: [contract.number, contract.title ?? '', clientNames ?? ''],
        };
      }),
    [contracts],
  );
  const selectedContractLabel = selectedContract
    ? `№${selectedContract.number}${selectedContract.title ? ` · ${selectedContract.title}` : ''}`
    : selectedContractId
      ? `#${selectedContractId}`
      : '';
  const activeFilterBadges = useMemo(() => {
    const badges: string[] = [];

    if (hasCustomPeriod || hasCustomSearch) {
      badges.push(`Период: ${formatPeriodLabel(period, new Date(), 'Всё время')}`);
    }
    if (hasCustomStatusFilter) {
      badges.push(`Статус: ${accountStatusLabels[statusFilter]}`);
    }
    if (hasCustomTypeFilter) {
      badges.push(`Тип: ${typeFilterLabels[typeFilter]}`);
    }
    if (hasCustomClientFilter) {
      const prefix = effectiveSelectedCounterparty?.kind === 'legalEntity' ? 'Юрлицо' : 'Клиент';
      badges.push(`${prefix}: ${effectiveSelectedCounterparty?.name ?? 'контрагент'}`);
    }
    if (hasCustomContractFilter) {
      badges.push(`Договор: ${selectedContractLabel}`);
    }
    if (hasCustomClientRoleFilter) {
      badges.push(
        `Роль: ${clientRoleFilter === 'counterparty' ? 'Контрагент / поставщик' : 'Клиент расхода'}`,
      );
    }
    if (payablesOnly) {
      badges.push('Кредиторка');
    }
    if (hasCustomSearch) {
      badges.push(`Поиск: ${search.trim()}`);
    }

    return badges;
  }, [
    hasCustomPeriod,
    hasCustomSearch,
    hasCustomStatusFilter,
    hasCustomTypeFilter,
    hasCustomClientFilter,
    hasCustomContractFilter,
    hasCustomClientRoleFilter,
    period,
    accountStatusLabels,
    clientRoleFilter,
    payablesOnly,
    effectiveSelectedCounterparty,
    selectedContractLabel,
    statusFilter,
    search,
    typeFilter,
    typeFilterLabels,
  ]);
  const hasActiveFilters = activeFilterBadges.length > 0;

  const handleSort = (key: SortState['key']) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' ? 'desc' : 'asc' };
    });
  };

  const [clients, setClients] = useState<Client[]>([]);
  const [clientsLoaded, setClientsLoaded] = useState(false);
  const [legalEntities, setLegalEntities] = useState<LegalEntitySummary[]>([]);
  const [legalEntitiesLoaded, setLegalEntitiesLoaded] = useState(false);
  const [actResponsiblesLoaded, setActResponsiblesLoaded] = useState(false);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);

  const ensureInvoiceLookups = useCallback(async () => {
    if (!modalOpen && !pageEditingInvoiceId) return;
    const clientsReady = !canViewClients || clientsLoaded || clients.length > 0;
    const legalEntitiesReady = !canViewLegalEntities || legalEntitiesLoaded;
    if (clientsReady && actResponsiblesLoaded && legalEntitiesReady && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse, legalEntitiesResponse] = await Promise.all([
        !canViewClients || clientsLoaded || clients.length > 0 ? Promise.resolve(clients) : apiService.getClients(),
        actResponsiblesLoaded ? Promise.resolve(actResponsibles) : apiService.getActResponsibles(),
        !canViewLegalEntities ? Promise.resolve([]) : legalEntitiesLoaded ? Promise.resolve(legalEntities) : apiService.getLegalEntities(),
      ]);
      if (!clientsLoaded && canViewClients && !clients.length) {
        setClients(clientsResponse ?? []);
      }
      setClientsLoaded(true);
      setActResponsibles(responsiblesResponse ?? []);
      setActResponsiblesLoaded(true);
      if (!legalEntitiesLoaded) {
        setLegalEntities(legalEntitiesResponse ?? []);
        setLegalEntitiesLoaded(true);
      }
      setLookupsError(null);
    } catch (err) {
      setLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить справочники');
    } finally {
      setLookupsLoading(false);
    }
  }, [modalOpen, pageEditingInvoiceId, actResponsibles, actResponsiblesLoaded, canViewClients, canViewLegalEntities, clients, clientsLoaded, legalEntities, legalEntitiesLoaded, lookupsError]);

  const handleInvoiceClientCreated = useCallback((client: Client) => {
    setClients((prev) => {
      const exists = prev.some((item) => item.id === client.id);
      return exists
        ? prev.map((item) => (item.id === client.id ? client : item))
        : [client, ...prev];
    });
    setClientsLoaded(true);
  }, []);

  const ensureActLookups = useCallback(async () => {
    if (!actModalOpen) return;
    if (actLookupsLoaded && !actLookupsError) return;
    setActLookupsLoading(true);
    try {
      const [clientsResponse, responsibleResponse] = await Promise.all([
        !canViewClients ? Promise.resolve([]) : clients.length > 0 ? Promise.resolve(clients) : apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      if (!clients.length && clientsResponse) {
        setClients(clientsResponse);
      }
      setActClients(clientsResponse ?? clients);
      setActResponsibles(responsibleResponse ?? []);
      setActLookupsLoaded(true);
      setActLookupsError(null);
    } catch (err) {
      setActLookupsError(err instanceof Error ? err.message : 'Не удалось загрузить справочники');
    } finally {
      setActLookupsLoading(false);
    }
  }, [actLookupsError, actLookupsLoaded, actModalOpen, canViewClients, clients]);

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

  useEffect(() => {
    if (!initialCreateDraft || !canCreate) return;
    openCreateModal(initialCreateDraft.draft);
    onInitialCreateDraftHandled?.(initialCreateDraft.key);
  }, [canCreate, initialCreateDraft, onInitialCreateDraftHandled, openCreateModal]);

  const openEditPage = useCallback((invoiceId: number, draft: InvoiceEditorDraftState | null = null) => {
    clearInvoiceEditorDraft();
    if (pageEditingInvoiceId === invoiceId) {
      setResumeInvoiceDraft(draft);
      setModalError(null);
      return;
    }
    setPageEditingInvoiceId(invoiceId);
    setPageEditingInvoice(null);
    setPageInvoiceLoading(true);
    setResumeInvoiceDraft(draft);
    setModalError(null);
    onRouteInvoiceChange?.(invoiceId);
  }, [onRouteInvoiceChange, pageEditingInvoiceId]);

  useEffect(() => {
    if (!pageEditingInvoiceId) return;

    let active = true;
    const requestedInvoiceId = pageEditingInvoiceId;

    const loadInvoice = async () => {
      try {
        setPageInvoiceLoading(true);
        const invoice = await apiService.getInvoice(requestedInvoiceId);
        if (!active) return;
        setPageEditingInvoice(invoice);
      } catch (err) {
        if (!active) return;
        const persistedDraft = peekInvoiceEditorDraft();
        if (persistedDraft?.mode === 'edit' && persistedDraft.invoiceId === requestedInvoiceId) {
          clearInvoiceEditorDraft();
        }
        toast.error(extractDisplayErrorMessage(err, 'Не удалось загрузить счет.'));
        setPageEditingInvoiceId(null);
        setPageEditingInvoice(null);
        setResumeInvoiceDraft((prev) => (
          prev?.mode === 'edit' && prev.invoiceId === requestedInvoiceId ? null : prev
        ));
        onRouteInvoiceChange?.(null);
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
  }, [onRouteInvoiceChange, pageEditingInvoiceId, toast]);

  useEffect(() => {
    if (modalOpen || pageEditingInvoiceId) return;

    const draft = peekInvoiceEditorDraft();
    if (!draft) return;

    if (draft.mode === 'create') {
      openCreateModal(draft);
      return;
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

  const closeEditPage = (options?: { restoreClientContext?: boolean }) => {
    clearInvoiceEditorDraft();
    setPageEditingInvoiceId(null);
    setPageEditingInvoice(null);
    setPageInvoiceLoading(false);
    setResumeInvoiceDraft(null);
    setModalError(null);
    if (options?.restoreClientContext) {
      onRouteInvoicePageBack?.();
    }
    onRouteInvoiceChange?.(null);
  };

  useEffect(() => {
    if (!routeInvoiceId || routeInvoiceId <= 0 || routeInvoiceId === pageEditingInvoiceId) {
      return;
    }

    const draft = peekInvoiceEditorDraft();
    if (draft?.invoiceId === routeInvoiceId) {
      return;
    }

    openEditPage(routeInvoiceId);
  }, [openEditPage, pageEditingInvoiceId, routeInvoiceId]);

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
        deferEditorClose(closeEditPage);
      } else {
        await apiService.createInvoice(payload);
        if (payload.billingTaskIds?.length) {
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, { detail: { bulkBillingIds: payload.billingTaskIds } }));
        }
        if (page !== 1) {
          setPage(1);
        } else {
          await refreshAccounts();
        }
        toast.success('Счет успешно создан.', 'Счет сохранен');
        deferEditorClose(closeModal);
      }
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить счет.');
      setModalError(message);
    } finally {
      setSubmitting(false);
    }
  };

  const summaryBuckets = summary;
  const mergedTotal = accountsTotal;
  const pagedAccounts = accountRows;
  const visibleInvoiceIds = useMemo(
    () =>
      pagedAccounts
        .map((row) => row.invoice?.id ?? null)
        .filter((id): id is number => typeof id === 'number' && id > 0),
    [pagedAccounts],
  );
  const {
    selectedIds: selectedInvoiceIds,
    selectedIdSet: selectedInvoiceIdSet,
    selectedCount: selectedInvoiceCount,
    allVisibleSelected,
    someVisibleSelected,
    isSelected: isInvoiceSelected,
    clearSelection: clearInvoiceSelection,
    selectOnly: selectOnlyInvoice,
    toggleSelection: toggleInvoiceSelection,
    toggleAllVisible: toggleAllInvoices,
  } = useTableSelection(visibleInvoiceIds);
  const selectedInvoiceRows = useMemo(
    () =>
      pagedAccounts.filter(
        (row) => typeof row.invoice?.id === 'number' && selectedInvoiceIdSet.has(row.invoice.id),
      ),
    [pagedAccounts, selectedInvoiceIdSet],
  );
  const singleSelectedInvoice =
    selectedInvoiceRows.length === 1 ? selectedInvoiceRows[0].invoice ?? null : null;

  const ensureResponsiblesLoaded = useCallback(async () => {
    if (actResponsibles.length > 0) {
      return actResponsibles;
    }

    const responsibles = await apiService.getActResponsibles();
    setActResponsibles(responsibles ?? []);
    return responsibles ?? [];
  }, [actResponsibles]);

  const handleRefreshAccounts = useCallback(async () => {
    await refreshAccounts();
    toast.success('Список счетов обновлен.');
  }, [refreshAccounts, toast]);

  const handleDeleteSelectedInvoices = useCallback(async (ids: number[]) => {
    if (!canDelete) return;

    const normalizedIds = Array.from(new Set(ids.filter((id) => Number.isFinite(id) && id > 0)));
    if (!normalizedIds.length) return;

    const targetInvoices = pagedAccounts.filter(
      (row) => typeof row.invoice?.id === 'number' && normalizedIds.includes(row.invoice.id),
    );
    const description =
      normalizedIds.length === 1
        ? `Удалить счет "${targetInvoices[0]?.accountNumber ?? `#${normalizedIds[0]}`}"?`
        : `Удалить выбранные счета (${normalizedIds.length})?`;

    if (
      !(await confirm({
        title: normalizedIds.length === 1 ? 'Удаление счета' : 'Массовое удаление счетов',
        description,
        confirmLabel: 'Удалить',
        cancelLabel: 'Отмена',
        variant: 'danger',
      }))
    ) {
      return;
    }

    try {
      await apiService.deleteInvoices(normalizedIds);
      clearInvoiceSelection();
      if (pagedAccounts.length === normalizedIds.length && page > 1) {
        setPage((current) => Math.max(1, current - 1));
      } else {
        await refreshAccounts();
      }
      toast.success(
        normalizedIds.length === 1
          ? 'Счет удален.'
          : `Удалено счетов: ${normalizedIds.length}.`,
      );
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить счет.'));
    }
  }, [canDelete, clearInvoiceSelection, confirm, page, pagedAccounts, refreshAccounts, toast]);

  const openChangeResponsiblePrompt = useCallback(async (targetInvoice?: Invoice) => {
    if (!canEdit) return;

    const targetInvoiceIds = targetInvoice ? [targetInvoice.id] : selectedInvoiceIds;
    if (targetInvoiceIds.length === 0) return;
    const targetSingleInvoice = targetInvoice ?? (targetInvoiceIds.length === 1 ? singleSelectedInvoice : null);

    try {
      const responsibles = await ensureResponsiblesLoaded();
      if (!responsibles.length) {
        setChangeResponsibleError('Список ответственных пуст.');
        return;
      }

      const defaultResponsibleId =
        targetSingleInvoice?.responsibleId
          ? String(targetSingleInvoice.responsibleId)
          : String(responsibles[0].id);

      setNextResponsibleId(defaultResponsibleId);
      setResponsibleChangeInvoiceIds(targetInvoiceIds);
      setResponsibleChangeSubjectInvoice(targetSingleInvoice);
      setChangeResponsibleError(null);
      setChangeResponsibleOpen(true);
    } catch (err) {
      setChangeResponsibleError(
        extractDisplayErrorMessage(err, 'Не удалось загрузить список ответственных.'),
      );
    }
  }, [canEdit, ensureResponsiblesLoaded, selectedInvoiceIds, singleSelectedInvoice]);

  const submitResponsibleChange = useCallback(async () => {
    if (!canEdit) return;

    const targetInvoiceIds = responsibleChangeInvoiceIds.length > 0
      ? responsibleChangeInvoiceIds
      : selectedInvoiceIds;
    if (targetInvoiceIds.length === 0) {
      setChangeResponsibleError('Выберите счет.');
      return;
    }
    const targetSingleInvoice = responsibleChangeSubjectInvoice ?? singleSelectedInvoice;

    const responsibleUserId = Number(nextResponsibleId);
    if (!Number.isFinite(responsibleUserId) || responsibleUserId <= 0) {
      setChangeResponsibleError('Выберите ответственного.');
      return;
    }

    const selectedResponsibleLabel =
      actResponsibles.find((responsible) => String(responsible.id) === nextResponsibleId)?.fullName
      ?? (
        targetSingleInvoice?.responsibleId
        && String(targetSingleInvoice.responsibleId) === nextResponsibleId
          ? targetSingleInvoice.responsibleName
          : null
      )
      ?? 'выбранного ответственного';
    const approved = await confirm({
      title: 'Подтверждение сохранения',
      description:
        targetInvoiceIds.length === 1
          ? `Сохранить нового ответственного (${selectedResponsibleLabel}) для выбранного счета?`
          : `Сохранить нового ответственного (${selectedResponsibleLabel}) для выбранных счетов: ${targetInvoiceIds.length}?`,
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });

    if (!approved) {
      return;
    }

    try {
      setChangeResponsibleSubmitting(true);
      setChangeResponsibleError(null);
      await apiService.changeInvoicesResponsible(targetInvoiceIds, responsibleUserId);
      setChangeResponsibleOpen(false);
      setResponsibleChangeInvoiceIds([]);
      setResponsibleChangeSubjectInvoice(null);
      await refreshAccounts();
      toast.success('Ответственный обновлен.');
    } catch (err) {
      setChangeResponsibleError(
        extractDisplayErrorMessage(err, 'Не удалось сменить ответственного.'),
      );
    } finally {
      setChangeResponsibleSubmitting(false);
    }
  }, [
    actResponsibles,
    canEdit,
    confirm,
    nextResponsibleId,
    refreshAccounts,
    responsibleChangeInvoiceIds,
    responsibleChangeSubjectInvoice,
    selectedInvoiceIds,
    singleSelectedInvoice,
    toast,
  ]);

  const openInvoiceStatusChange = useCallback((invoice: Invoice) => {
    if (!canEdit) return;
    setStatusChangeInvoice(invoice);
    setStatusChangeValue(resolveEditableInvoiceWorkflowStatus(invoice));
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [canEdit]);

  const closeInvoiceStatusChange = useCallback(() => {
    if (statusChangeSubmitting) return;
    setStatusChangeInvoice(null);
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [statusChangeSubmitting]);

  const submitInvoiceStatusChange = useCallback(async () => {
    if (!canEdit || !statusChangeInvoice) return;

    try {
      setStatusChangeSubmitting(true);
      setStatusChangeError(null);
      await apiService.updateInvoiceWorkflowStatus(statusChangeInvoice.id, statusChangeValue, statusChangeReason.trim() || null);
      setStatusChangeInvoice(null);
      setStatusChangeReason('');
      await refreshAccounts();
      toast.success('Статус документа обновлен.');
    } catch (err) {
      setStatusChangeError(extractDisplayErrorMessage(err, 'Не удалось изменить статус документа.'));
    } finally {
      setStatusChangeSubmitting(false);
    }
  }, [canEdit, refreshAccounts, statusChangeInvoice, statusChangeReason, statusChangeValue, toast]);

  const closeTableMenu = useCallback(() => {
    setTableMenuPosition(null);
  }, []);

  const openTableMenu = useCallback(
    (event: ReactMouseEvent<HTMLTableRowElement>, invoiceId: number | null) => {
      event.preventDefault();
      event.stopPropagation();

      if (invoiceId && !isInvoiceSelected(invoiceId)) {
        selectOnlyInvoice(invoiceId);
      }

      setTableMenuPosition({ x: event.clientX, y: event.clientY });
    },
    [isInvoiceSelected, selectOnlyInvoice],
  );

  const removeInvoice = async (invoice: Invoice) => {
    if (!canDelete) return;
    await handleDeleteSelectedInvoices([invoice.id]);
  };

  const handleResetFilters = () => {
    setPeriod(defaultPeriod);
    setStatusFilter('all');
    setTypeFilter(baseTypeFilter);
    setSelectedCounterparty(null);
    setSelectedContractId(null);
    setClientRoleFilter('all');
    setPayablesOnly(false);
    setPayablesDashboardOpen(false);
    setSearch('');
    setPage(1);
  };

  const openPayablesDashboard = () => {
    if (!payablesOnly) {
      setPayablesOnly(true);
      setTypeFilter('Expense');
      setStatusFilter('all');
    }
    setPayablesDashboardOpen(true);
  };

  const typeOptions: FilterType[] = ['All', 'Income', 'Expense'];

  const columns: TableColumn[] = useMemo(
    () => [
      { key: 'date', label: t('invoiceDate') ?? t('actDate') ?? 'Дата', sortable: true },
      { key: 'number', label: t('invoiceNumber') ?? 'Номер счёта', sortable: true },
      { key: 'amount', label: t('invoiceAmount') ?? t('amount') ?? 'Сумма', sortable: true },
      { key: 'counterparty', label: t('invoiceClient') ?? 'Клиент / поставщик', sortable: true },
      { key: 'status', label: t('invoiceStatus') ?? t('status') ?? 'Статус', sortable: false },
      { key: 'signingStatus', label: 'Статус документа', sortable: false },
      { key: 'dueDate', label: t('invoiceDueDate') ?? 'Оплатить до', sortable: true },
      { key: 'paidDate', label: t('paidDate') ?? 'Дата оплаты', sortable: false },
      { key: 'act', label: 'Акт / статус', sortable: false },
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
      { key: 'counterparty', label: t('invoiceClient') ?? 'Клиент / поставщик', sortable: true },
      { key: 'status', label: t('invoiceStatus') ?? t('status') ?? 'Статус', sortable: false },
      { key: 'dueDate', label: t('invoiceDueDate') ?? 'Оплатить до', sortable: true },
      { key: 'paidDate', label: t('paidDate') ?? 'Дата оплаты', sortable: false },
      { key: 'act', label: 'Акт / статус', sortable: false },
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
        legalEntityId: invoice.legalEntityId ?? null,
        expenseClientId: invoice.expenseClientId ?? null,
        expenseLegalEntityId: invoice.expenseLegalEntityId ?? null,
        clientCaseId: invoice.clientCaseId ?? null,
        contractId: invoice.contractId ?? null,
        type: invoice.type ?? 'Income',
      };
    },
    [],
  );

  const openPaymentCreateModal = (invoice: Invoice | null | undefined) => {
    if (!canCreatePayment) return;
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

  const openLinkedPaymentEditModal = (payment: Payment, invoice?: Invoice | null) => {
    if (!canEditPayment) return;
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

  const refreshAfterPaymentSave = async () => {
    try {
      await refreshAccounts();
      if (pageEditingInvoiceId) {
        const refreshedInvoice = await apiService.getInvoice(pageEditingInvoiceId);
        setPageEditingInvoice(refreshedInvoice);
      }
    } catch {
      toast.error('Платёж сохранён, но не удалось обновить список счетов.', 'Платежи счета');
    }
  };

  const handleSubmitPayment = async (payload: PaymentPayload | ({ id: number } & PaymentPayload)): Promise<Payment> => {
    const isUpdate = 'id' in payload;
    if ((isUpdate && !canEditPayment) || (!isUpdate && !canCreatePayment)) {
      throw new Error('Недостаточно прав для сохранения платежа.');
    }
    const invoiceManagedEditingPayment =
      isUpdate
      && editingPayment
      && editingPayment.id === payload.id
      && isInvoiceManagedPayment(editingPayment);

    let savedPayment: Payment;
    try {
      if (isUpdate) {
        if (invoiceManagedEditingPayment) {
          savedPayment = await apiService.updateInvoiceManagedPayment(payload.id, payload);
        } else {
          savedPayment = await apiService.updatePayment(payload.id, payload);
        }
      } else {
        savedPayment = await apiService.createPayment(payload as PaymentPayload);
      }
    } catch (err) {
      if (err instanceof PaymentCreateAdoptedError) throw err;
      toast.error(extractDisplayErrorMessage(err, 'Не удалось сохранить платёж.'), 'Платежи счета');
      throw err;
    }
    void refreshAfterPaymentSave();
    return savedPayment;
  };

  const handleTransferPaymentRemainder = async (
    payload: { id: number } & PaymentPayload,
    remainderDate: string,
  ): Promise<Payment> => {
    if (!canEditPayment) {
      throw new Error('Недостаточно прав для сохранения платежа.');
    }
    let savedPayment: Payment;
    try {
      const result = await apiService.transferPaymentRemainder(payload.id, payload, remainderDate);
      savedPayment = result.payment;
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось перенести остаток платежа.'), 'Платежи счета');
      throw err;
    }
    void refreshAfterPaymentSave();
    toast.success('Остаток платежа перенесён.', 'Платежи счета');
    return savedPayment;
  };

  const handleRollbackPaymentRemainderTransfer = async (paymentId: number) => {
    try {
      await apiService.rollbackPaymentRemainderTransfer(paymentId);
      await refreshAccounts();
      if (pageEditingInvoiceId) {
        try {
          const refreshedInvoice = await apiService.getInvoice(pageEditingInvoiceId);
          setPageEditingInvoice(refreshedInvoice);
        } catch {
          // Keep the current page state if the invoice refresh fails after a successful payment change.
        }
      }
      handleClosePaymentModal();
      toast.success('Перенос остатка отменен.', 'Платежи счета');
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось откатить перенос остатка.'), 'Платежи счета');
      throw err;
    }
  };

  const handleAdjustPaymentRemainderTransfer = async (
    paymentId: number,
    paidAmount: number,
    remainderDate: string,
  ) => {
    try {
      await apiService.adjustPaymentRemainderTransfer(paymentId, paidAmount, remainderDate);
      await refreshAccounts();
      if (pageEditingInvoiceId) {
        try {
          const refreshedInvoice = await apiService.getInvoice(pageEditingInvoiceId);
          setPageEditingInvoice(refreshedInvoice);
        } catch {
          // Keep the current page state if the invoice refresh fails after a successful payment change.
        }
      }
      handleClosePaymentModal();
      toast.success('Разделение платежа обновлено.', 'Платежи счета');
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось изменить разделение платежа.'), 'Платежи счета');
      throw err;
    }
  };

  const handleDeletePayment = async (payment: Payment) => {
    const isTransferredRemainder = isTransferredRemainderPayment(payment);
    const isRemainderSource = isPartialRemainderSourcePayment(payment);
    if (payment.documentKind === 'Invoice' || isTransferredRemainder || isRemainderSource) {
      toast.error(
        isTransferredRemainder || isRemainderSource
          ? 'Перенесенный остаток нельзя удалить отдельно.'
          : 'Документ счета удаляется через раздел "Счета".',
        'Платежи счета',
      );
      return;
    }
    if (!canDeletePayment) return;
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
    if (!canCreateAct) return;
    setExpandedActListKey(null);
    const normalizedDate =
      toDateInputValue(row.invoice?.date ?? row.dueDate) || formatLocalYMD(new Date());
    const nextInitialValues: Partial<ActInput> & { amount?: number | null } = {
      creationMode: 'Full',
      invoiceId: row.invoice?.id ?? null,
      invoiceNumber: row.accountNumber,
      amount: row.totalAmount,
      title: row.invoice?.description ?? row.invoice?.actTitle ?? '',
      date: normalizedDate,
      clientId: row.invoice?.clientId ?? null,
      responsibleId: row.invoice?.responsibleId ?? null,
      comment: row.invoice?.actReference ?? '',
      status: 'Created',
    };

    if (onCreateActPage) {
      onCreateActPage(nextInitialValues, {
        invoiceId: null,
        invoiceSeed: row.invoice ?? null,
      });
      return;
    }

    setActModalMode('create');
    setActiveAct(null);
    setActModalInvoice(row);
    setActInitialValues(nextInitialValues);
    setActModalError(null);
    setActModalOpen(true);
  };

  const openActEditModal = (act: Act, row: AccountRow) => {
    if (!canEditAct) return;
    setExpandedActListKey(null);
    if (onOpenActPage) {
      onOpenActPage(act.id, {
        invoiceId: null,
      });
      return;
    }

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

  const actWriteOwner = useDocumentDeliveryOwner(JSON.stringify([user?.id, user?.role, user?.departmentId, permissions.acts,
    actModalOpen, actModalMode, activeAct?.id, actModalInvoice?.invoice?.id]));
  const actModalSubmitting = actWriteOwner.busy;
  const handleSubmitAct = async (payload: ActInput, options?: ActCreateOptions) => {
    if ((actModalMode === 'edit' && !canEditAct) || (actModalMode !== 'edit' && !canCreateAct)) {
      throw new StaleUiOperationError();
    }
    const operation = actWriteOwner.begin('save');
    if (!operation) throw new StaleUiOperationError();
    setActModalError(null);
    try {
      const saved = actModalMode === 'edit' && activeAct
        ? await apiService.updateAct(activeAct.id, {
          ...payload,
          invoiceId: payload.invoiceId ?? actModalInvoice?.invoice?.id,
          invoiceNumber: payload.invoiceNumber || actModalInvoice?.accountNumber || payload.number,
        })
        : await apiService.createAct({
          ...payload,
          invoiceId: payload.invoiceId ?? actModalInvoice?.invoice?.id,
          invoiceNumber: payload.invoiceNumber || actModalInvoice?.accountNumber || payload.number,
        }, options);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      await refreshAccounts();
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      if (actModalMode !== 'edit') {
        toast.success(actCreateSuccessMessage(saved));
        acknowledgeCreatedAct(saved);
      }
      closeActModal();
      return saved;
    } catch (err) {
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      setActModalError(err instanceof Error ? err.message : 'Не удалось создать акт');
      throw err;
    } finally {
      operation.finish();
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

  const responsibleOptions = useMemo(() => {
    const dictionaryItems = [...actResponsibles];
    const currentInvoice = responsibleChangeSubjectInvoice ?? singleSelectedInvoice;
    const currentResponsibleId = currentInvoice?.responsibleId ?? null;
    const currentResponsibleName = currentInvoice?.responsibleName ?? null;

    if (
      currentResponsibleId
      && currentResponsibleName
      && !dictionaryItems.some((responsible) => responsible.id === currentResponsibleId)
    ) {
      dictionaryItems.push({
        id: currentResponsibleId,
        fullName: currentResponsibleName,
      });
    }

    return [
      { value: '', label: 'Выберите ответственного' },
      ...dictionaryItems.map((responsible) => ({
        value: String(responsible.id),
        label: responsible.fullName,
      })),
    ];
  }, [actResponsibles, responsibleChangeSubjectInvoice, singleSelectedInvoice]);

  const tableMenuTitle =
    selectedInvoiceCount === 1
      ? singleSelectedInvoice?.number ?? 'Счет'
      : `Выбрано: ${selectedInvoiceCount}`;

  const tableMenuActions: TableContextMenuAction[] = [
    {
      key: 'add',
      label: 'Добавить',
      onSelect: openCreateModal,
      disabled: !canCreate,
    },
    {
      key: 'edit',
      label: 'Редактировать',
      onSelect: () => {
        if (singleSelectedInvoice) {
          openEditPage(singleSelectedInvoice.id);
        }
      },
      disabled: !canEdit || selectedInvoiceCount !== 1 || !singleSelectedInvoice,
    },
    {
      key: 'refresh',
      label: 'Обновить',
      onSelect: handleRefreshAccounts,
    },
    {
      key: 'changeResponsible',
      label: 'Смена ответственного',
      onSelect: openChangeResponsiblePrompt,
      disabled: !canEdit || selectedInvoiceCount === 0,
      separatorBefore: true,
    },
    {
      key: 'delete',
      label: 'Удалить',
      onSelect: () => void handleDeleteSelectedInvoices(selectedInvoiceIds),
      disabled: !canDelete || selectedInvoiceCount === 0,
      variant: 'danger',
      separatorBefore: true,
    },
  ];

  const statusChangeCurrentValue = resolveEditableInvoiceWorkflowStatus(statusChangeInvoice);
  const statusChangeTargetLabel = invoiceWorkflowStatusLabels[statusChangeValue] ?? statusChangeValue;
  const statusChangeSubject = statusChangeInvoice
    ? `Счет №${statusChangeInvoice.number} от ${formatDate(statusChangeInvoice.date)}`
    : 'Счет не выбран';
  const statusChangeConsequences = [
    `Статус документа изменится с «${invoiceWorkflowStatusLabels[statusChangeCurrentValue] ?? statusChangeCurrentValue}» на «${statusChangeTargetLabel}».`,
    'Статус оплаты, сумма, дата оплаты и связанные платежи не меняются.',
    'Изменение сохранится сразу после подтверждения и список счетов обновится.',
  ];
  const responsibleChangeCount = responsibleChangeInvoiceIds.length || selectedInvoiceCount;

  const paymentModalElement = (
    <PaymentModal
      isOpen={paymentModalOpen && canViewPayments}
      onClose={handleClosePaymentModal}
      onSubmit={handleSubmitPayment}
      canCreatePayment={canCreatePayment}
      canEditPayment={canEditPayment}
      onTransferRemainder={handleTransferPaymentRemainder}
      onRollbackRemainderTransfer={handleRollbackPaymentRemainderTransfer}
      onAdjustRemainderTransfer={handleAdjustPaymentRemainderTransfer}
      canViewLinkedInvoices={permissions.accounts.canView}
      canCreateLinkedInvoices={permissions.accounts.canView && permissions.accounts.canCreate}
      payment={editingPayment ?? undefined}
      type={paymentModalType}
      linkedInvoice={paymentModalInvoiceContext}
      onBalanceApplied={async () => {
        await refreshAccounts();
      }}
      onDelete={
        canDeletePayment
        && (!editingPayment || (editingPayment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(editingPayment) && !isPartialRemainderSourcePayment(editingPayment)))
          ? async (id) => {
              await apiService.deletePayment(id);
              toast.success('Платеж удален.');
              await refreshAccounts();
              handleClosePaymentModal();
            }
          : undefined
      }
      onCreateRemainder={
        canCreatePayment && (!editingPayment || !isInvoiceManagedPayment(editingPayment))
          ? async (payload, context) => {
              const created = await apiService.createPayment(payload, context);
              void refreshAfterPaymentSave();
              return created;
            }
          : undefined
      }
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
          onClose={() => closeEditPage()}
          onBack={() => closeEditPage({ restoreClientContext: true })}
          onOpenClient={canViewClients ? openClientFromEditor : undefined}
          onOpenContract={canViewContracts ? onOpenContract : undefined}
          onOpenActPage={canViewActs ? onOpenActPage : undefined}
          onOpenPayment={canViewPayments ? onOpenPayment : undefined}
          onSubmit={submitInvoice}
          submitting={submitting}
          errorMessage={modalError}
          clients={clients}
          legalEntities={legalEntities}
          allowedCounterpartyScopes={allowedCounterpartyScopes}
          legalEntitiesLoading={lookupsLoading && !legalEntitiesLoaded}
          legalEntitiesError={lookupsError}
          onClientCreated={handleInvoiceClientCreated}
          responsibles={actResponsibles}
          lookupsLoading={lookupsLoading}
          lookupsError={lookupsError}
          defaultClientId={undefined}
          defaultResponsibleId={user?.id}
          defaultType={typeFilter === 'All' ? undefined : typeFilter}
          lockType={lockType}
          relatedPayments={canViewPayments ? currentPageAccountRow?.payments ?? [] : []}
          paymentsLoading={canViewPayments && (isLoading || isRefreshing)}
          onRefreshRequested={refreshAccounts}
          onAddRelatedPayment={canCreatePayment ? () => openPaymentCreateModal(currentPageInvoice) : undefined}
          onEditRelatedPayment={canEditPayment ? (payment) => openLinkedPaymentEditModal(payment, currentPageInvoice) : undefined}
          onDeleteRelatedPayment={canDeletePayment ? handleDeletePayment : undefined}
          onCreateActPage={
            canCreateAct && onCreateActPage
              ? (initialValues, options) => onCreateActPage(initialValues, {
                invoiceId: options?.invoiceId ?? currentPageInvoice.id,
                invoiceSeed: options?.invoiceSeed ?? currentPageInvoice,
              })
              : undefined
          }
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

          <div className="flex flex-wrap gap-2">
          {onOpenTaskBilling && (
            <button type="button" onClick={onOpenTaskBilling}
              className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50">
              Анализ оплаты задач
            </button>
          )}
          {canCreate && (
            <button
              type="button"
              onClick={() => openCreateModal()}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow hover:bg-blue-500"
            >
              <Plus className="h-4 w-4" />
              {t('invoiceAdd') ?? 'Добавить счёт'}
            </button>
          )}
          </div>
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
                <div className="inline-flex w-fit max-w-full flex-wrap items-center gap-2 self-start rounded-full border border-gray-200 bg-slate-50 px-1 py-1 shadow-sm">
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
                        onClick={() => {
                          setTypeFilter(option);
                          setPayablesOnly(false);
                          setPayablesDashboardOpen(false);
                        }}
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
                <div className="flex flex-col gap-1 text-sm sm:col-span-2">
                  <DatePeriodPicker
                    value={period}
                    onChange={setPeriod}
                    label={t('invoiceFiltersPeriod') ?? 'Период'}
                    accent="blue"
                  />
                </div>
                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-gray-700">{t('invoiceFiltersStatus') ?? t('status') ?? 'Статус'}</span>
                  <SystemSelect
                    value={statusFilter}
                    onChange={(event) => setStatusFilter(event.target.value as 'all' | AccountStatus)}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  >
                    <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                    <option value="created">{accountStatusLabels.created}</option>
                    <option value="partial">{accountStatusLabels.partial}</option>
                    <option value="overdue">{accountStatusLabels.overdue}</option>
                    <option value="paid">{accountStatusLabels.paid}</option>
                    <option value="cancelled">{accountStatusLabels.cancelled}</option>
                  </SystemSelect>
                </label>
                <div className="sm:col-span-2 lg:col-span-2">
                  <div className="flex flex-col gap-1 text-sm">
                    <span className="flex min-h-7 items-center font-medium text-gray-700">Клиент / поставщик</span>
                    <button
                      type="button"
                      onClick={() => {
                        if (canSelectCounterpartyFilter) {
                          setCounterpartyPickerOpen(true);
                        }
                      }}
                      disabled={!canSelectCounterpartyFilter}
                      className="flex min-h-[42px] w-full items-center justify-between gap-3 rounded-lg border border-gray-300 bg-white px-3 py-2 text-left text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      <span className={effectiveSelectedCounterparty ? 'truncate text-slate-900' : 'truncate text-slate-500'}>
                        {effectiveSelectedCounterparty?.name ?? 'Все контрагенты'}
                      </span>
                      <span className="shrink-0 text-xs text-slate-500">
                        {effectiveSelectedCounterparty?.kind === 'legalEntity'
                          ? 'Юрлицо'
                          : effectiveSelectedCounterparty
                            ? 'Физлицо'
                            : canSelectCounterpartyFilter
                              ? 'Выбрать'
                              : 'Недоступно'}
                      </span>
                    </button>
                  </div>
                </div>
                <label className="flex flex-col gap-1 text-sm lg:col-start-3">
                  <span className="flex min-h-7 items-center font-medium text-gray-700">Роль клиента</span>
                  <SystemSelect
                    value={clientRoleFilter}
                    onChange={(event) => setClientRoleFilter(event.target.value as ClientRoleFilter)}
                    className="min-h-[42px] w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  >
                    <option value="all">Любая связь</option>
                    <option value="counterparty">Контрагент / поставщик</option>
                    <option value="expenseClient">Клиент расхода</option>
                  </SystemSelect>
                </label>
                <div className="sm:col-span-2 lg:col-span-3">
                  <LookupSelect
                    label="Договор"
                    value={effectiveSelectedContractId ? String(effectiveSelectedContractId) : ''}
                    options={contractLookupOptions}
                    onChange={(value) => {
                      const nextId = Number(value);
                      setSelectedContractId(value && !Number.isNaN(nextId) ? nextId : null);
                    }}
                    placeholder="Все договоры"
                    title="Выбор договора"
                    dictionaryLabel="Справочник"
                    searchPlaceholder="Номер, название или клиент"
                    emptyOptionLabel="Все договоры"
                    emptyOptionDescription="Очистить фильтр по договору"
                    helperText={contractsLoading ? 'Загрузка договоров...' : !canViewContracts ? 'Раздел договоров закрыт для вашей роли' : undefined}
                    disabled={!canViewContracts}
                    noOptionsText="Договоры не найдены"
                  />
                </div>
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
                  <IconActionButton
                    label="Кредиторка"
                    onClick={openPayablesDashboard}
                    active={payablesOnly}
                    className={payablesOnly ? 'border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100 focus:ring-rose-200' : ''}
                  >
                    <WalletCards className="h-4 w-4" />
                  </IconActionButton>
                  <IconActionButton label={t('invoiceFiltersReset') ?? 'Сбросить'} onClick={handleResetFilters}>
                    <RotateCcw className="h-4 w-4" />
                  </IconActionButton>
                  <IconActionButton
                    label={t('actFiltersApply') ?? 'Обновить'}
                    onClick={() => {
                      void refreshAccounts();
                      if (payablesOnly) {
                        void refreshPayablesDashboard();
                      }
                    }}
                    active
                    className="border-blue-200 bg-blue-50 text-blue-600 hover:bg-blue-100"
                  >
                    {isRefreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                  </IconActionButton>
                  <div className="relative">
                    <IconActionButton
                      label={t('tableSettings') ?? 'Таблица'}
                      onClick={() => setColumnsOpen((prev) => !prev)}
                      active={columnsOpen}
                    >
                      <Table2 className="h-4 w-4" />
                    </IconActionButton>
                  </div>
                </div>
              </div>

              {hasActiveFilters ? (
                <div className="rounded-xl border border-amber-200 bg-amber-50/80 px-3 py-3">
                  <div className="flex flex-wrap items-center gap-2 text-sm text-amber-900">
                    <span className="font-semibold">Применены фильтры:</span>
                    {activeFilterBadges.map((badge) => (
                      <span
                        key={badge}
                        className="inline-flex items-center rounded-full border border-amber-200 bg-white px-3 py-1 text-xs font-medium text-amber-800"
                      >
                        {badge}
                      </span>
                    ))}
                    {hasCustomSearch && (periodRange.from || periodRange.to) ? (
                      <button type="button" onClick={() => setPeriod({ mode: 'custom' })}
                        className="inline-flex items-center rounded-full border border-amber-300 bg-white px-3 py-1 text-xs font-semibold text-amber-800 transition hover:bg-amber-100">
                        Искать за всё время
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={handleResetFilters}
                      className="ml-auto inline-flex items-center rounded-full border border-amber-300 bg-white px-3 py-1 text-xs font-semibold text-amber-800 transition hover:bg-amber-100"
                    >
                      Сбросить все
                    </button>
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        </div>

        {payablesDashboardOpen && typeof document !== 'undefined' ? createPortal(
          <div
            className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/45 p-4"
            role="presentation"
            onMouseDown={(event) => {
              if (event.target === event.currentTarget) {
                setPayablesDashboardOpen(false);
              }
            }}
          >
            <section
              role="dialog"
              aria-modal="true"
              aria-labelledby="payables-dashboard-title"
              className="flex max-h-[calc(100vh-3rem)] w-full max-w-6xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl"
            >
              <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-5">
                <div>
                  <h2 id="payables-dashboard-title" className="text-lg font-semibold text-slate-900">
                    Доска кредиторки
                  </h2>
                  <p className="mt-1 text-sm text-slate-500">
                    Открытые расходные обязательства по поставщикам и срокам оплаты.
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  {payablesDashboardLoading ? (
                    <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 px-3 py-1 text-sm text-slate-500">
                      <Loader2 className="h-4 w-4 animate-spin" />
                      Загрузка
                    </span>
                  ) : null}
                  <IconActionButton
                    label="Обновить кредиторку"
                    onClick={() => void refreshPayablesDashboard()}
                    active
                    className="border-blue-200 bg-blue-50 text-blue-600 hover:bg-blue-100"
                  >
                    {payablesDashboardLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                  </IconActionButton>
                  <IconActionButton
                    label="Закрыть"
                    onClick={() => setPayablesDashboardOpen(false)}
                  >
                    <X className="h-4 w-4" />
                  </IconActionButton>
                </div>
              </div>

              <div className="overflow-y-auto px-6 py-5">
                {payablesDashboardError ? (
                  <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
                    {payablesDashboardError}
                  </div>
                ) : (
                  <>
                    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                      <div className="flex min-h-28 flex-col items-center justify-center rounded-xl border border-slate-100 bg-slate-50 px-4 py-4 text-center">
                        <p className="text-xs uppercase text-slate-500">Всего к оплате</p>
                        <p className="mt-2 text-xl font-semibold text-slate-900">
                          {formatCurrencySmart(payablesDashboard?.totalOutstanding ?? 0).full}
                        </p>
                        <p className="mt-1 text-xs text-slate-500">
                          {formatPaymentCount(payablesDashboard?.paymentCount ?? 0)}
                        </p>
                      </div>
                      <div className="flex min-h-28 flex-col items-center justify-center rounded-xl border border-rose-100 bg-rose-50 px-4 py-4 text-center">
                        <p className="text-xs uppercase text-rose-600">Просрочено</p>
                        <p className="mt-2 text-xl font-semibold text-rose-700">
                          {formatCurrencySmart(payablesDashboard?.overdueOutstanding ?? 0).full}
                        </p>
                        <p className="mt-1 text-xs text-rose-500">
                          {formatPaymentCount(payablesOverdueCount)}
                        </p>
                      </div>
                      <div className="flex min-h-28 flex-col items-center justify-center rounded-xl border border-amber-100 bg-amber-50 px-4 py-4 text-center">
                        <p className="text-xs uppercase text-amber-700">Ближайшие 7 дней</p>
                        <p className="mt-2 text-xl font-semibold text-amber-700">
                          {formatCurrencySmart(payablesDashboard?.upcomingOutstanding ?? 0).full}
                        </p>
                        <p className="mt-1 text-xs text-amber-600">
                          {formatPaymentCount(payablesUpcomingCount)}
                        </p>
                      </div>
                      <div className="flex min-h-28 flex-col items-center justify-center rounded-xl border border-blue-100 bg-blue-50 px-4 py-4 text-center">
                        <p className="text-xs uppercase text-blue-700">Поставщики</p>
                        <p className="mt-2 text-xl font-semibold text-blue-700">
                          {payablesDashboard?.supplierCount ?? 0}
                        </p>
                        <p className="mt-1 text-xs text-blue-600">
                          {formatSupplierWord(payablesDashboard?.supplierCount ?? 0)} с долгом
                        </p>
                      </div>
                    </div>

                    <div className="mt-5 grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(320px,0.9fr)]">
                      <div className="rounded-xl border border-slate-200">
                        <div className="border-b border-slate-100 px-4 py-3 text-sm font-semibold text-slate-800">
                          Сроки оплаты
                        </div>
                        <div className="divide-y divide-slate-100">
                          {(payablesDashboard?.dueBuckets ?? []).map((bucket) => (
                            <div
                              key={bucket.key}
                              className="flex items-center justify-between gap-3 px-4 py-3 text-sm"
                            >
                              <div>
                                <p className="font-medium text-slate-800">{bucket.label}</p>
                                <p className="text-xs text-slate-500">{formatPaymentCount(bucket.count)}</p>
                              </div>
                              <p className="font-semibold text-slate-900">
                                {formatCurrencySmart(bucket.amount).full}
                              </p>
                            </div>
                          ))}
                          {(payablesDashboard?.dueBuckets ?? []).length === 0 ? (
                            <div className="px-4 py-6 text-center text-sm text-slate-500">
                              Нет открытой кредиторки за выбранный период.
                            </div>
                          ) : null}
                        </div>
                      </div>

                      <div className="rounded-xl border border-slate-200">
                        <div className="border-b border-slate-100 px-4 py-3 text-sm font-semibold text-slate-800">
                          Поставщики с долгом
                        </div>
                        <div className="max-h-80 divide-y divide-slate-100 overflow-auto">
                          {(payablesDashboard?.suppliers ?? []).slice(0, 8).map((supplier) => (
                            <div key={`${supplier.supplierId ?? 'none'}:${supplier.supplierName}`} className="px-4 py-3 text-sm">
                              <div className="flex items-start justify-between gap-3">
                                <div className="min-w-0">
                                  <p className="truncate font-medium text-slate-800">
                                    {supplier.supplierName}
                                  </p>
                                  <p className="mt-1 text-xs text-slate-500">
                                    {formatPaymentCount(supplier.paymentCount)}
                                    {supplier.caseCount > 0 ? `, дел: ${supplier.caseCount}` : ''}
                                  </p>
                                  {supplier.nearestDueDate ? (
                                    <p className="mt-1 text-xs text-slate-500">
                                      Ближайший срок: {formatDate(supplier.nearestDueDate)}
                                    </p>
                                  ) : null}
                                </div>
                                <div className="text-right">
                                  <p className="font-semibold text-slate-900">
                                    {formatCurrencySmart(supplier.totalOutstanding).full}
                                  </p>
                                  {supplier.overdueOutstanding > 0 ? (
                                    <p className="mt-1 text-xs font-medium text-rose-700">
                                      Просрочено {formatCurrencySmart(supplier.overdueOutstanding).full}
                                    </p>
                                  ) : null}
                                </div>
                              </div>
                            </div>
                          ))}
                          {(payablesDashboard?.suppliers ?? []).length === 0 ? (
                            <div className="px-4 py-6 text-center text-sm text-slate-500">
                              Поставщики с открытым долгом не найдены.
                            </div>
                          ) : null}
                        </div>
                      </div>
                    </div>
                  </>
                )}
              </div>
            </section>
          </div>,
          document.body,
        ) : null}

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
                  <SystemSelect
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                  >
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

        {summaryBuckets.pending?.count > 0 && (
          <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {t('invoiceSummaryPending') ?? 'Ожидает оплаты'}: {summaryBuckets.pending.count}{' '}
            · {formatCurrencySmart(summaryBuckets.pending.amount).full}
          </div>
        )}

        <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y divide-gray-200"
              style={{ width: getTableWidth(52 + (canEdit || canDelete ? 148 : 0)), minWidth: '100%' }}
            >
              <colgroup>
                <col style={{ width: 52 }} />
                {orderedVisibleColumns
                  .map((column) => (
                    <col key={column.key} style={{ width: getColumnWidth(column.key) }} />
                  ))}
                {(canEdit || canDelete) && <col style={{ width: 148 }} />}
              </colgroup>
              <thead className="bg-gray-50">
                <tr className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-3 text-center align-middle">
                    <TableSelectCheckbox
                      checked={allVisibleSelected}
                      indeterminate={someVisibleSelected}
                      onChange={(event) => toggleAllInvoices(event.target.checked)}
                      aria-label="Выбрать все счета"
                    />
                  </th>
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
                    <td colSpan={orderedVisibleColumns.length + 1 + (canEdit || canDelete ? 1 : 0)} className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : pagedAccounts.length === 0 ? (
                  <tr>
                    <td colSpan={orderedVisibleColumns.length + 1 + (canEdit || canDelete ? 1 : 0)} className="py-10 text-center text-gray-500">
                      {t('invoiceNoData') ?? 'Счета не найдены'}
                    </td>
                  </tr>
                ) : (
                  pagedAccounts.map((row) => {
                    const invoice = row.invoice;
                    const invoiceId = typeof invoice?.id === 'number' ? invoice.id : null;
                    const accountStatus = row.status;
                    const signingStatus = deriveInvoiceSigningStatus(invoice);
                    const workflowStatus = invoice?.workflowStatus;
                    const documentStatusLabel = workflowStatus
                      ? (invoiceWorkflowStatusLabels[workflowStatus] ?? signingStatusLabels[signingStatus])
                      : signingStatusLabels[signingStatus];
                    const documentStatusClass = workflowStatus
                      ? (invoiceWorkflowStatusClasses[workflowStatus] ?? signingStatusClasses[signingStatus])
                      : signingStatusClasses[signingStatus];
                    const type = row.type;
                    const typeBadgeClass = typeBadgeClasses[type];
                    const typeLabel = typeLabels[type];
                    const actsForInvoice = row.acts ?? [];
                    const primaryAct = actsForInvoice[0] ?? null;
                    const extraActs = actsForInvoice.slice(1);
                    const primaryActWorkflowStatus = primaryAct ? resolveActWorkflowStatus(primaryAct) : null;
                    const primaryActStatusLabel = primaryAct && primaryActWorkflowStatus
                      ? (actWorkflowStatusLabels[primaryActWorkflowStatus] ?? actSigningStatusLabels[primaryAct.status] ?? actStatusLabels[primaryAct.status])
                      : null;
                    const primaryActStatusClass = primaryAct && primaryActWorkflowStatus
                      ? (actWorkflowStatusClasses[primaryActWorkflowStatus] ?? actSigningStatusClasses[primaryAct.status])
                      : null;
                    const actListKey = `${row.accountNumber}-${invoice?.id ?? 'no-invoice'}`;
                    const isActListOpen = expandedActListKey === actListKey;
                    const counterpartySummary = resolveAccountCounterparty(invoice, row.payments, row.type);
                    const primaryName = counterpartySummary.primaryName;
                    const caseLines = counterpartySummary.caseLines;
                    const clientId = typeof counterpartySummary.clientId === 'number' ? counterpartySummary.clientId : null;
                    const isExpanded = expandedAccounts[row.groupKey] ?? false;
                    const colSpan = orderedVisibleColumns.length + 1 + (canEdit || canDelete ? 1 : 0);
                    const responsibleLabel = invoice?.responsibles?.length
                      ? invoice.responsibles.map((item) => item.fullName).join(', ')
                      : invoice?.responsibleName
                        ? invoice.responsibleName
                        : '—';

                    return (
                      <Fragment key={`${row.accountNumber}-${invoice?.id ?? 'no-invoice'}`}>
                        <tr
                          onContextMenu={(event) => openTableMenu(event, invoiceId)}
                          className={`${
                            invoiceId && isInvoiceSelected(invoiceId)
                              ? 'bg-blue-50/70 hover:bg-blue-50'
                              : 'hover:bg-gray-50/70'
                          } ${canEdit && invoice ? 'cursor-pointer' : ''}`}
                          onClick={() => {
                            if (canEdit && invoice) {
                              openEditPage(invoice.id);
                            }
                          }}
                          title="ПКМ для действий"
                        >
                          <td className="px-4 py-3 text-center align-middle">
                            <TableSelectCheckbox
                              checked={invoiceId ? isInvoiceSelected(invoiceId) : false}
                              disabled={!invoiceId}
                              onChange={(event) => {
                                event.stopPropagation();
                                if (!invoiceId) return;
                                toggleInvoiceSelection(invoiceId, event.target.checked);
                              }}
                              onClick={(event) => event.stopPropagation()}
                              aria-label={invoice ? `Выбрать счет ${invoice.number}` : 'Выбрать счет'}
                            />
                          </td>
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
                                      onClick={(event) => { event.stopPropagation(); toggleExpanded(row.groupKey); }}
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
                                    <div className="flex min-w-0 flex-col items-start gap-1 text-left">
                                      <div className="w-full min-w-0">
                                        {clientId && canOpenClientFromAccounts ? (
                                          <button
                                            type="button"
                                            onClick={(event) => { event.stopPropagation(); onOpenClient?.(clientId); }}
                                            className="block w-full truncate text-left font-medium text-blue-700 transition-colors hover:text-blue-800 hover:underline"
                                            title={primaryName}
                                          >
                                            {primaryName}
                                          </button>
                                        ) : (
                                          <span className="block w-full truncate font-medium text-gray-900" title={primaryName}>{primaryName}</span>
                                        )}
                                      </div>
                                      <div className="flex flex-wrap items-center gap-2">
                                        <ClientStatusBadge status={counterpartySummary.clientStatus} />
                                        <span
                                          className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeBadgeClass}`}
                                        >
                                          {typeLabel}
                                        </span>
                                      </div>
                                      {counterpartySummary.company && <div className="w-full truncate text-xs text-gray-500" title={counterpartySummary.company}>{counterpartySummary.company}</div>}
                                      {counterpartySummary.expenseBasis.map((line) => (
                                        <div key={line} className="w-full truncate text-xs text-gray-500" title={line}>
                                          {line}
                                        </div>
                                      ))}
                                      {caseLines.length > 0 && (
                                        <div className="w-full truncate text-xs text-gray-500" title={`${(t('cases') ?? 'Дела')}: ${caseLines.join(', ')}`}>
                                          {(t('cases') ?? 'Дела')}: {caseLines.join(', ')}
                                        </div>
                                      )}
                                    </div>
                                  </td>
                                );
                              case 'status':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    <div className="flex flex-col items-center gap-1">
                                      <span className={`${statusBadgeClass} ${accountStatusClasses[accountStatus]}`}>
                                        {accountStatusLabels[accountStatus]}
                                      </span>
                                      {invoice?.paymentStatusName && (
                                        <div className="text-center text-xs text-gray-500">{invoice.paymentStatusName}</div>
                                      )}
                                    </div>
                                  </td>
                                );
                              case 'signingStatus':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    {invoice && canEdit ? (
                                      <button
                                        type="button"
                                        onClick={(event) => {
                                          event.stopPropagation();
                                          openInvoiceStatusChange(invoice);
                                        }}
                                        className="inline-flex max-w-full rounded-full transition hover:brightness-95 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                        title="Изменить статус документа"
                                        aria-label={`Изменить статус документа счета №${invoice.number}`}
                                      >
                                        <span className={`${statusBadgeClass} ${documentStatusClass}`}>
                                          {documentStatusLabel}
                                        </span>
                                      </button>
                                    ) : (
                                      <span className={`${statusBadgeClass} ${documentStatusClass}`}>
                                        {documentStatusLabel}
                                      </span>
                                    )}
                                  </td>
                                );
                              case 'dueDate':
                                return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.dueDate)}</td>;
                              case 'paidDate':
                                return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.paidDate)}</td>;
                              case 'act':
                                if (!canViewActs) {
                                  return (
                                    <td key={column.key} className="px-4 py-3 text-center align-middle">
                                      <span className="text-slate-400">—</span>
                                    </td>
                                  );
                                }
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    <div className="relative flex items-center justify-center gap-1.5">
                                      {primaryAct ? (
                                        <>
                                          {canCreateAct && (
                                            <button
                                              type="button"
                                              onClick={(event) => { event.stopPropagation(); openActCreateModal(row); }}
                                              className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-blue-200 bg-blue-50 text-blue-700 transition hover:bg-blue-100"
                                              aria-label={t('addAct') ?? 'Добавить акт'}
                                            >
                                              <FilePlus2 className="h-4 w-4" />
                                            </button>
                                          )}
                                          <button
                                            type="button"
                                            onClick={(event) => { event.stopPropagation(); openActEditModal(primaryAct, row); }}
                                            className="inline-flex w-[8.5rem] min-w-0 flex-col items-start gap-1 rounded-md border border-slate-200 bg-white px-2 py-1.5 text-left text-xs shadow-sm transition hover:border-blue-200 hover:bg-blue-50"
                                            title={`${primaryAct.number ? `Акт №${primaryAct.number}` : t('act') ?? 'Акт'} · ${formatDate(primaryAct.date)} · ${primaryActStatusLabel}`}
                                          >
                                            <span className="block w-full truncate font-semibold text-blue-700">
                                              {primaryAct.number ? `Акт №${primaryAct.number}` : t('act') ?? 'Акт'}
                                            </span>
                                            <span className="block w-full truncate text-[11px] font-medium text-slate-500">
                                              {formatDate(primaryAct.date)}
                                            </span>
                                            <span className={`inline-flex max-w-full items-center justify-center rounded-full px-2 py-0.5 text-center text-[11px] font-medium leading-tight ${primaryActStatusClass}`}>
                                              {primaryActStatusLabel}
                                            </span>
                                          </button>
                                          {extraActs.length > 0 && (
                                            <button
                                              type="button"
                                              onClick={(event) => {
                                                event.stopPropagation();
                                                setExpandedActListKey(isActListOpen ? null : actListKey);
                                              }}
                                              className={`inline-flex h-8 min-w-8 shrink-0 items-center justify-center rounded-md border px-2 text-xs font-semibold transition ${
                                                isActListOpen
                                                  ? 'border-blue-300 bg-blue-50 text-blue-700'
                                                  : 'border-slate-200 bg-white text-slate-600 hover:border-blue-200 hover:text-blue-700'
                                              }`}
                                              aria-expanded={isActListOpen}
                                              aria-label={`Показать все акты счета ${row.accountNumber}`}
                                            >
                                              +{extraActs.length}
                                            </button>
                                          )}
                                          {isActListOpen && (
                                            <div
                                              className="absolute left-1/2 top-full z-30 mt-2 w-64 -translate-x-1/2 rounded-lg border border-slate-200 bg-white p-2 text-left shadow-lg"
                                              onClick={(event) => event.stopPropagation()}
                                            >
                                              <div className="mb-1 px-2 text-[11px] font-semibold uppercase tracking-wide text-slate-500">
                                                Акты по счету
                                              </div>
                                              <div className="space-y-1">
                                                {actsForInvoice.map((act) => {
                                                  const actWorkflowStatus = resolveActWorkflowStatus(act);
                                                  const actStatusLabel =
                                                    actWorkflowStatusLabels[actWorkflowStatus] ??
                                                    actSigningStatusLabels[act.status] ??
                                                    actStatusLabels[act.status];
                                                  const actStatusClass =
                                                    actWorkflowStatusClasses[actWorkflowStatus] ??
                                                    actSigningStatusClasses[act.status];

                                                  return (
                                                    <button
                                                      key={act.id}
                                                      type="button"
                                                      onClick={(event) => { event.stopPropagation(); openActEditModal(act, row); }}
                                                      className="flex w-full items-center justify-between gap-3 rounded-md px-2 py-1.5 text-left transition hover:bg-blue-50"
                                                    >
                                                      <span className="min-w-0">
                                                        <span className="block truncate text-xs font-semibold text-blue-700">
                                                          {act.number ? `Акт №${act.number}` : t('act') ?? 'Акт'}
                                                        </span>
                                                        <span className="block text-[11px] font-medium text-slate-500">{formatDate(act.date)}</span>
                                                      </span>
                                                      <span className={`inline-flex shrink-0 items-center justify-center rounded-full px-2 py-0.5 text-center text-[11px] font-medium leading-tight ${actStatusClass}`}>
                                                        {actStatusLabel}
                                                      </span>
                                                    </button>
                                                  );
                                                })}
                                              </div>
                                            </div>
                                          )}
                                        </>
                                      ) : (
                                        canCreateAct ? (
                                          <button
                                            type="button"
                                            onClick={(event) => { event.stopPropagation(); openActCreateModal(row); }}
                                            className="inline-flex h-8 items-center justify-center gap-1.5 rounded-md border border-blue-200 bg-blue-50 px-2.5 text-xs font-semibold text-blue-700 transition hover:bg-blue-100"
                                            aria-label={t('actCreateFromInvoice') ?? 'Создать акт из счёта'}
                                          >
                                            <FilePlus2 className="h-4 w-4" />
                                            <span>Акт</span>
                                          </button>
                                        ) : (
                                          <span className="text-slate-400">—</span>
                                        )
                                      )}
                                    </div>
                                  </td>
                                );
                              case 'responsible':
                                return (
                                  <td key={column.key} className="px-4 py-3 text-center align-middle">
                                    {invoice && canEdit ? (
                                      <button
                                        type="button"
                                        onClick={(event) => {
                                          event.stopPropagation();
                                          void openChangeResponsiblePrompt(invoice);
                                        }}
                                        className="inline-flex max-w-full items-center justify-center gap-1.5 rounded-md px-2 py-1 text-sm font-medium text-blue-700 transition hover:bg-blue-50 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                        title="Сменить ответственного"
                                        aria-label={`Сменить ответственного счета №${invoice.number}`}
                                      >
                                        <span className="truncate">{responsibleLabel}</span>
                                        <Pencil className="h-3.5 w-3.5 shrink-0" />
                                      </button>
                                    ) : (
                                      responsibleLabel
                                    )}
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
                                    onClick={(event) => {
                                      event.stopPropagation();
                                      void openChangeResponsiblePrompt(invoice);
                                    }}
                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-blue-200 text-blue-700 hover:bg-blue-50"
                                    title="Сменить ответственного"
                                    aria-label={`Сменить ответственного счета №${invoice.number}`}
                                  >
                                    <UserRound className="h-4 w-4" />
                                  </button>
                                )}
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
                        {isExpanded && canViewPayments && (row.payments.length > 0 || canCreatePayment) && (
                          <tr>
                            <td colSpan={colSpan} className="bg-gray-50 px-4 py-3">
                              <div className="rounded-lg border border-gray-200 bg-white shadow-sm">
                                <div className="flex items-center justify-between gap-3 border-b border-gray-100 px-4 py-2">
                                  <div className="text-xs font-semibold uppercase text-gray-500">
                                    {t('paymentOperations') ?? 'Операции по счёту'}
                                  </div>
                                  <div className="flex items-center gap-2">
                                    {canCreatePayment && invoice && (
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
                                </div>
                                <div className="overflow-x-auto">
                                  <table className="min-w-full table-fixed divide-y divide-gray-100 text-sm">
                                    <colgroup>
                                      <col style={{ width: canEditPayment || canDeletePayment ? '18%' : '22%' }} />
                                      <col style={{ width: '18%' }} />
                                      <col style={{ width: '18%' }} />
                                      <col style={{ width: canEditPayment || canDeletePayment ? '30%' : '42%' }} />
                                      {(canEditPayment || canDeletePayment) ? <col style={{ width: '16%' }} /> : null}
                                    </colgroup>
                                    <thead className="bg-gray-50 text-left text-xs font-medium uppercase text-gray-500">
                                      <tr>
                                        <th className="px-4 py-2">{t('paymentDate') ?? 'Дата платежа'}</th>
                                        <th className="px-4 py-2">{t('amount') ?? 'Сумма'}</th>
                                        <th className="px-4 py-2">{t('status') ?? 'Статус'}</th>
                                        <th className="px-4 py-2">{t('description') ?? 'Описание'}</th>
                                        {(canEditPayment || canDeletePayment) && (
                                          <th className="px-4 py-2 text-right">{t('actions') ?? 'Действия'}</th>
                                        )}
                                      </tr>
                                    </thead>
                                    <tbody className="divide-y divide-gray-100">
                                      {row.payments.map((payment) => (
                                        <tr
                                          key={payment.id}
                                          className={canEditPayment ? 'cursor-pointer hover:bg-gray-50/80' : undefined}
                                          onClick={canEditPayment ? () => openLinkedPaymentEditModal(payment, invoice) : undefined}
                                        >
                                          <td className="px-4 py-2">{formatDate(resolvePaymentOperationDate(payment))}</td>
                                          <td className="px-4 py-2 font-semibold text-gray-900">
                                            {formatCurrencySmart(payment.amount).full}
                                          </td>
                                          <td className="px-4 py-2 text-center align-middle">
                                            <span className={`${statusBadgeClass} ${accountStatusClasses[deriveStatusFromPayments([payment], invoice)]}`}>
                                              {accountStatusLabels[deriveStatusFromPayments([payment], invoice)]}
                                            </span>
                                          </td>
                                          <td className="px-4 py-2 text-gray-600">
                                            <div className="flex flex-wrap items-center gap-2">
                                              {resolveInstallmentPaymentBadge(payment) ? (
                                                <span
                                                  className="inline-flex items-center rounded-full border border-sky-200 bg-sky-50 px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide text-sky-700"
                                                  title="Платеж по графику рассрочки"
                                                >
                                                  {resolveInstallmentPaymentBadge(payment)}
                                                </span>
                                              ) : null}
                                              <span>{payment.description || '—'}</span>
                                            </div>
                                          </td>
                                          {(canEditPayment || canDeletePayment) && (
                                            <td className="px-4 py-2">
                                              <div className="flex items-center justify-end gap-2">
                                                {canEditPayment && (
                                                  <button
                                                    type="button"
                                                    onClick={(event) => {
                                                      event.stopPropagation();
                                                      openLinkedPaymentEditModal(payment, invoice);
                                                    }}
                                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 transition hover:bg-gray-50"
                                                    title={t('edit') ?? 'Редактировать'}
                                                    aria-label={t('edit') ?? 'Редактировать'}
                                                  >
                                                    <Pencil className="h-4 w-4" />
                                                  </button>
                                                )}
                                                {canDeletePayment && payment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(payment) && !isPartialRemainderSourcePayment(payment) && (
                                                  <button
                                                    type="button"
                                                    onClick={(event) => {
                                                      event.stopPropagation();
                                                      void handleDeletePayment(payment);
                                                    }}
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

        <TableContextMenu
          open={tableMenuPosition !== null}
          position={tableMenuPosition}
          title={tableMenuTitle}
          actions={tableMenuActions}
          onClose={closeTableMenu}
        />

        <StatusChangeModal<InvoiceWorkflowStatus>
          open={statusChangeInvoice !== null}
          title="Смена статуса документа"
          subject={statusChangeSubject}
          currentLabel={invoiceWorkflowStatusLabels[statusChangeCurrentValue] ?? statusChangeCurrentValue}
          currentValue={statusChangeCurrentValue}
          value={statusChangeValue}
          options={invoiceWorkflowStatusOptions}
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
          onCancel={closeInvoiceStatusChange}
          onConfirm={submitInvoiceStatusChange}
          confirmLabel={`Изменить статус счета №${statusChangeInvoice?.number ?? ''} на «${statusChangeTargetLabel}»`}
          error={statusChangeError}
          loading={statusChangeSubmitting}
        />

        <FormPromptModal
          open={changeResponsibleOpen}
          title="Смена ответственного"
          description={
            responsibleChangeCount === 1
              ? 'Назначьте нового ответственного для выбранного счета.'
              : `Назначьте нового ответственного для счетов: ${responsibleChangeCount}.`
          }
          fields={[
            {
              key: 'responsibleUserId',
              label: 'Ответственный',
              value: nextResponsibleId,
              onChange: (value) => {
                setNextResponsibleId(value);
                if (changeResponsibleError) {
                  setChangeResponsibleError(null);
                }
              },
              type: 'lookup',
              options: responsibleOptions,
              required: true,
              lookupTitle: 'Выбор ответственного',
              searchPlaceholder: 'Найти ответственного',
              placeholder: 'Не выбрано',
              dictionaryLabel: 'Справочник',
              noOptionsText: changeResponsibleSubmitting ? 'Сохраняем...' : 'Ответственные не найдены',
              disabled: changeResponsibleSubmitting,
            },
          ]}
          onCancel={() => {
            setChangeResponsibleOpen(false);
            setResponsibleChangeInvoiceIds([]);
            setResponsibleChangeSubjectInvoice(null);
            setChangeResponsibleError(null);
          }}
          onConfirm={submitResponsibleChange}
          confirmLabel="Сохранить"
          cancelLabel="Отмена"
          error={changeResponsibleError ?? undefined}
          loading={changeResponsibleSubmitting}
        />

        <CounterpartyPickerModal
          open={counterpartyPickerOpen}
          title="Выбор клиента / поставщика"
          selected={effectiveSelectedCounterparty ? [effectiveSelectedCounterparty] : []}
          initialScope={effectiveSelectedCounterparty?.kind ?? 'client'}
          allowedScopes={allowedCounterpartyScopes}
          allowEmpty
          emptyLabel="Все контрагенты"
          overlayClassName="z-[1600]"
          onClose={() => setCounterpartyPickerOpen(false)}
          onSelect={(selection) => {
            setSelectedCounterparty(selection);
            setCounterpartyPickerOpen(false);
          }}
        />

        <InvoiceModal
          open={modalOpen}
          mode="create"
          invoice={selectedInvoice}
          resumeDraft={resumeInvoiceDraft}
          onClose={closeModal}
          onOpenClient={canViewClients ? openClientFromEditor : undefined}
          onOpenContract={canViewContracts ? onOpenContract : undefined}
          onOpenActPage={canViewActs ? onOpenActPage : undefined}
          onSubmit={submitInvoice}
          submitting={submitting}
          errorMessage={modalError}
          clients={clients}
          legalEntities={legalEntities}
          allowedCounterpartyScopes={allowedCounterpartyScopes}
          legalEntitiesLoading={lookupsLoading && !legalEntitiesLoaded}
          legalEntitiesError={lookupsError}
          onClientCreated={handleInvoiceClientCreated}
          responsibles={actResponsibles}
          lookupsLoading={lookupsLoading}
          lookupsError={lookupsError}
          defaultClientId={undefined}
          defaultResponsibleId={user?.id}
          defaultType={typeFilter === 'All' ? undefined : typeFilter}
          lockType={lockType}
          onRefreshRequested={refreshAccounts}
          onCreateActPage={
            canCreateAct && onCreateActPage
              ? (initialValues, options) => onCreateActPage(initialValues, {
                invoiceId: options?.invoiceId ?? selectedInvoice?.id ?? null,
                invoiceSeed: options?.invoiceSeed ?? selectedInvoice ?? null,
              })
              : undefined
          }
        />

        <ActModal
          open={actModalOpen && canViewActs}
          mode={actModalMode}
          act={activeAct}
          onClose={closeActModal}
          onSubmit={handleSubmitAct}
          accessScopeKey={actWriteOwner.scope}
          submitting={actModalSubmitting}
          errorMessage={actModalError}
          clients={actClients.length ? actClients : clients}
          responsibles={actResponsibles}
          lookupsLoading={actLookupsLoading}
          lookupsError={actLookupsError}
          defaultClientId={actModalInvoice?.invoice?.clientId ?? null}
          defaultResponsibleId={actModalInvoice?.invoice?.responsibleId ?? null}
          initialValues={actInitialValues ?? undefined}
          invoiceSeed={actModalInvoice?.invoice ?? undefined}
        />

        {paymentModalElement}
      </div>
    </div>
  );
}
