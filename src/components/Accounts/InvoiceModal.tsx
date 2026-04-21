import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowLeft, Ban, Check, ChevronDown, Copy, Download, Link2, List, Loader2, Pencil, Plus, Printer, Send, Trash2, UserRoundSearch, X } from 'lucide-react';
import type {
  Act,
  ActStatus,
  ActResponsible,
  Client,
  ClientCase,
  Contract,
  ContractClient,
  ContractInput,
  Invoice,
  InvoiceInput,
  InstallmentPlanInput,
  InvoiceLineItem,
  Payment,
  PaymentKind,
  PaymentStatus,
  ServiceDictionaryItem,
} from '../../types';
import { fromInputToApiDate, formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useDictionaries } from '../../hooks/useDictionaries';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import type { InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';
import { storeInvoiceEditorDraft } from '../../utils/invoiceEditorBridge';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { CaseModal } from '../Clients/CaseModal';
import { ContractModal } from '../Contracts/ContractModal';
import { InvoiceLineItemLookupDialog } from './InvoiceLineItemLookupDialog';
import { LookupSelect } from '../ui/LookupSelect';
import { InstallmentPlanner } from './InstallmentPlanner';
import { cloneInstallmentPlan, deriveInstallmentPlanFromPayments, parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';
import { getContractBillingModeLabel, getContractEffectiveAmount } from '../../utils/contracts';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { filterRepresentativeInvoiceWhenInstallmentsExist, isInvoiceManagedPayment } from '../../utils/invoicePayments';

type FormState = {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  type: PaymentKind;
  status: PaymentStatus;
  clientId: string;
  expenseClientId: string;
  contractId: string;
  clientCaseReference: string;
  description: string;
  isNotForAccounting: boolean;
  actReference: string;
  paidDate: string;
  responsibleIds: string[];
};

type LineItem = {
  id: string;
  name: string;
  quantity: string;
  unit: string;
  price: string;
  serviceCategoryId: string;
  serviceId: string;
};

type DraftActForm = {
  number: string;
  title: string;
  date: string;
  amount: string;
  comment: string;
};

type LineItemLookupState =
  | { kind: 'category'; lineItemId: string }
  | { kind: 'service'; lineItemId: string }
  | null;

const LINE_ITEM_GRID_TEMPLATE =
  'md:grid-cols-[minmax(14rem,1.1fr)_minmax(19rem,1.8fr)_minmax(5.5rem,0.55fr)_minmax(6.5rem,0.7fr)_minmax(8.5rem,0.8fr)_minmax(8.5rem,0.8fr)_2.75rem]';
const LINE_ITEM_GRID_MIN_WIDTH_CLASS = 'md:min-w-[68rem]';

function formatMoneyValue(value: string | number | null | undefined): string {
  const parsed = parseMoneyInput(value);
  if (!Number.isFinite(parsed)) return '0.00';
  const [integer, decimal] = parsed.toFixed(2).split('.');
  return `${groupThousands(integer)}.${decimal}`;
}

function normalizeMoneyInput(value: string | number | null | undefined): string {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? String(value) : '';
  }

  const raw = String(value ?? '')
    .replace(/\s+/g, '')
    .replace(',', '.')
    .replace(/[^\d.]/g, '');

  if (!raw) return '';

  const dotIndex = raw.indexOf('.');
  if (dotIndex < 0) return raw;

  return `${raw.slice(0, dotIndex + 1)}${raw.slice(dotIndex + 1).replace(/\./g, '')}`;
}

function groupThousands(value: string): string {
  return value.replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

function parseMoneyInput(value: string | number | null | undefined): number {
  const normalized = normalizeMoneyInput(value);
  if (!normalized || normalized === '.') return 0;

  const parsed = Number.parseFloat(normalized);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatMoneyInputValue(value: string | number | null | undefined): string {
  const normalized = normalizeMoneyInput(value);
  if (!normalized) return '';

  const hasDecimal = normalized.includes('.');
  const hasTrailingDot = normalized.endsWith('.');
  const [integerPartRaw, decimalPart = ''] = normalized.split('.', 2);
  const integerPart = groupThousands((integerPartRaw || '0').replace(/^0+(?=\d)/, '') || '0');

  if (hasTrailingDot) {
    return `${integerPart}.`;
  }

  if (hasDecimal) {
    return `${integerPart}.${decimalPart}`;
  }

  return integerPart;
}

function formatMoneyDisplayValue(value: string | number | null | undefined): string {
  return formatMoneyValue(value);
}

function calculateLineItemAmountValue(
  quantity: string | number | null | undefined,
  price: string | number | null | undefined,
): string {
  return formatMoneyDisplayValue(parseMoneyInput(quantity) * parseMoneyInput(price));
}

function normalizeLookupName(value: string | null | undefined): string {
  return String(value ?? '').trim().toLocaleLowerCase('ru');
}

function normalizeLineItems(source?: InvoiceLineItem[] | null, fallbackAmount?: number): LineItem[] {
  if (source && source.length > 0) {
    return source.map((item, index) => ({
      id: `${Date.now()}-${index}`,
      name: item.name ?? '',
      quantity: String(item.quantity ?? 0),
      unit: item.unit ?? 'шт',
      price: formatMoneyValue(item.price),
      serviceCategoryId: '',
      serviceId: item.serviceId ? String(item.serviceId) : '',
    }));
  }

  return [
    {
      id: `${Date.now()}-base`,
      name: '',
      quantity: '1',
      unit: 'шт',
      price: formatMoneyValue(fallbackAmount ?? 0),
      serviceCategoryId: '',
      serviceId: '',
    },
  ];
}

type ActOption = Pick<Act, 'id' | 'number' | 'title' | 'date' | 'amount' | 'invoiceNumber' | 'clientId' | 'clientName' | 'status'>;
type ClientCaseOption = Pick<ClientCase, 'id' | 'clientId' | 'title' | 'status' | 'createdAt'>;

interface InvoiceModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  invoice: Invoice | null;
  resumeDraft?: InvoiceEditorDraftState | null;
  onClose: () => void;
  onBack?: () => void;
  onOpenClient?: (clientId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenPayment?: (payment: Payment) => void;
  onSubmit: (payload: InvoiceInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  responsibles: ActResponsible[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  defaultClientId?: number;
  defaultResponsibleId?: number;
  defaultType?: PaymentKind;
  lockType?: boolean;
  presentation?: 'modal' | 'page';
  relatedPayments?: Payment[];
  paymentsLoading?: boolean;
  onRefreshRequested?: () => Promise<void> | void;
  onAddRelatedPayment?: () => void;
  onEditRelatedPayment?: (payment: Payment) => void;
  onDeleteRelatedPayment?: (payment: Payment) => Promise<void> | void;
  onClientCaseCreated?: (clientCase: ClientCase) => void;
}

function normalizeInvoiceToForm(
  invoice: Invoice | null,
  defaultClientId?: number,
  defaultResponsibleId?: number,
  defaultType?: PaymentKind,
): FormState {
  const today = formatLocalYMD(new Date());
  const resolvedType: PaymentKind = defaultType ?? 'Income';
  if (!invoice) {
    return {
      number: '',
      date: today,
      dueDate: '',
      amount: '',
      type: resolvedType,
      status: 'Pending',
      clientId: defaultClientId ? String(defaultClientId) : '',
      expenseClientId: '',
      contractId: '',
      clientCaseReference: '',
      description: '',
      isNotForAccounting: false,
      actReference: '',
      paidDate: '',
      responsibleIds: defaultResponsibleId ? [String(defaultResponsibleId)] : [],
    };
  }

  return {
    number: invoice.number ?? '',
    date: toDateInputValue(invoice.date) || today,
    dueDate: toDateInputValue(invoice.dueDate) || '',
    amount: invoice.amount !== undefined && invoice.amount !== null ? String(invoice.amount) : '',
    type: invoice.type ?? resolvedType,
    status: invoice.status ?? 'Pending',
    clientId: invoice.clientId ? String(invoice.clientId) : defaultClientId ? String(defaultClientId) : '',
    expenseClientId: invoice.expenseClientId ? String(invoice.expenseClientId) : '',
    contractId: invoice.contractId ? String(invoice.contractId) : '',
    clientCaseReference: invoice.clientCaseReference ?? '',
    description: invoice.description ?? '',
    isNotForAccounting: Boolean(invoice.isNotForAccounting),
    actReference: invoice.actReference ?? '',
    paidDate: toDateInputValue(invoice.paidDate) || '',
    responsibleIds: (invoice.responsibles ?? []).map((item) => String(item.id)).slice(0, 1),
  };
}

function normalizeInvoiceClientCaseIds(invoice: Invoice | null): number[] {
  const rawIds = invoice?.clientCaseIds?.length
    ? invoice.clientCaseIds
    : invoice?.clientCaseId
      ? [invoice.clientCaseId]
      : [];

  return Array.from(new Set(rawIds.filter((value): value is number => typeof value === 'number' && value > 0)));
}

function normalizeInvoiceClientCases(invoice: Invoice | null): ClientCaseOption[] {
  if (invoice?.clientCases?.length) {
    return invoice.clientCases.map((item) => ({
      id: item.id,
      clientId: item.clientId,
      title: item.title,
      status: item.status,
      createdAt: item.createdAt,
    }));
  }

  if (invoice?.clientCaseId) {
    return [
      {
        id: invoice.clientCaseId,
        clientId:
          invoice.type === 'Expense'
            ? (invoice.expenseClientId ?? invoice.clientId ?? 0)
            : (invoice.clientId ?? 0),
        title: invoice.clientCaseTitle ?? '',
        status: '',
        createdAt: invoice.createdAt,
      },
    ];
  }

  return [];
}

function normalizeInvoiceActs(invoice: Invoice | null): ActOption[] {
  if (invoice?.acts?.length) {
    return invoice.acts.map((item) => ({
      id: item.id,
      number: item.number,
      title: item.title ?? undefined,
      date: item.date,
      amount: item.amount,
      invoiceNumber: invoice.number,
      clientId: item.clientId ?? undefined,
      clientName: item.clientName ?? undefined,
      status: item.status,
    }));
  }

  if (invoice?.actId) {
    return [
      {
        id: invoice.actId,
        number: invoice.actNumber ?? invoice.number,
        title: invoice.actTitle ?? undefined,
        date: invoice.date,
        amount: invoice.amount,
        invoiceNumber: invoice.actNumber ?? invoice.number,
        clientId: invoice.clientId ?? undefined,
        clientName: invoice.clientName ?? undefined,
        status: (invoice.actStatus ?? 'Created') as ActStatus,
      },
    ];
  }

  return [];
}

function actLabel(act: ActOption | null): string {
  if (!act) return '';
  const parts: string[] = [];
  if (act.number) parts.push(`№${act.number}`);
  if (act.title) parts.push(act.title);
  return parts.join(' · ');
}

function clientCaseLabel(clientCase: ClientCaseOption | null): string {
  if (!clientCase) return '';
  return clientCase.title?.trim() || `Дело #${clientCase.id}`;
}

function clientCaseStatusLabel(statusRaw?: string | null): string {
  const status = (statusRaw ?? '').trim();
  const normalized = status.toLowerCase();

  if (!normalized) return 'Без статуса';
  if (normalized.includes('open')) return 'Открыто';
  if (normalized.includes('onhold') || normalized.includes('hold')) return 'Приостановлено';
  if (normalized.includes('closed')) return 'Закрыто';
  if (normalized.includes('в работе') || normalized.includes('актив')) return 'Открыто';
  if (normalized.includes('ожид')) return 'Приостановлено';
  if (normalized.includes('закры')) return 'Закрыто';

  return status;
}

function sortContractsByRecency(source: Contract[]): Contract[] {
  return [...source].sort((left, right) => {
    const leftDate = Date.parse(left.date ?? '');
    const rightDate = Date.parse(right.date ?? '');
    if (Number.isFinite(leftDate) && Number.isFinite(rightDate) && leftDate !== rightDate) {
      return rightDate - leftDate;
    }
    return right.id - left.id;
  });
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

function resolveRelatedPaymentDate(payment: Payment): string {
  return payment.paidDate ?? payment.lastPaymentDate ?? payment.plannedDate ?? payment.date ?? '';
}

function formatContractSidebarLabel(contract: Contract | null): string {
  if (!contract) return 'Без договора';
  const number = contract.number?.trim() || `#${contract.id}`;
  const dateLabel = formatDate(contract.date) || 'без даты';
  return `Договор №${number} от ${dateLabel}`;
}

function formatRelatedPaymentLinkLabel(payment: Payment): string {
  const dateLabel = formatDate(resolveRelatedPaymentDate(payment)) || 'без даты';
  const sourceLabel = payment.paymentSource?.name?.trim();
  const amountLabel = formatCurrencySmart(payment.amount ?? 0).short;
  return sourceLabel
    ? `от ${dateLabel} ${sourceLabel.toLowerCase()}: ${amountLabel}`
    : `от ${dateLabel}: ${amountLabel}`;
}

function getInstallmentPlanLastDate(plan: InstallmentPlanInput | null | undefined): string {
  if (!plan?.items?.length) return '';

  return plan.items.reduce<string>((latest, item) => {
    const normalized = toDateInputValue(item.date);
    if (!normalized) return latest;
    if (!latest || normalized > latest) return normalized;
    return latest;
  }, '');
}

type StatusTimelineEntry = {
  key: string;
  title: string;
  description: string;
  sourceLabel: string;
  timestampLabel: string;
  sortValue: number;
  isCurrent: boolean;
};

type InvoiceStatusSnapshot = {
  status: PaymentStatus;
  reason: string;
  totalAmount: number;
  paidAmount: number;
  outstandingAmount: number;
  sourcePayments: Payment[];
  timeline: StatusTimelineEntry[];
};

function getStatusSourcePayments(payments: Payment[]): Payment[] {
  const linkedPayments = payments.filter((payment) => payment.documentKind !== 'Invoice');
  return linkedPayments.length > 0 ? linkedPayments : payments;
}

function resolvePaidAmount(payment: Payment): number {
  if (Number.isFinite(payment.paidAmount) && payment.paidAmount > 0) {
    return payment.paidAmount;
  }

  return payment.status === 'Completed' ? Math.max(payment.amount ?? 0, 0) : 0;
}

function resolveOutstandingAmount(payment: Payment): number {
  if (payment.status === 'Completed' || payment.status === 'Cancelled') {
    return 0;
  }

  if (Number.isFinite(payment.outstandingAmount) && payment.outstandingAmount > 0) {
    return payment.outstandingAmount;
  }

  return Math.max((payment.amount ?? 0) - resolvePaidAmount(payment), 0);
}

function paymentSourceLabel(payment: Payment): string {
  const description = payment.description?.trim();
  if (description) return description;
  if (payment.documentKind === 'Invoice' && payment.account) return `Счет №${payment.account}`;
  if (payment.account) return `Платеж по счету №${payment.account}`;
  return `Платеж #${payment.id}`;
}

function deriveInvoiceStatusFromPayments(
  payments: Payment[],
  fallbackStatus: PaymentStatus,
  fallbackDueDate?: string | null,
): PaymentStatus {
  const sourcePayments = getStatusSourcePayments(payments);

  if (sourcePayments.length === 0) {
    if (fallbackStatus === 'Completed' || fallbackStatus === 'Cancelled' || fallbackStatus === 'Processing' || fallbackStatus === 'Overdue') {
      return fallbackStatus;
    }

    const normalizedDueDate = toDateInputValue(fallbackDueDate ?? null);
    if (normalizedDueDate && normalizedDueDate < formatLocalYMD(new Date())) {
      return 'Overdue';
    }

    return 'Pending';
  }

  if (sourcePayments.every((payment) => payment.status === 'Cancelled')) {
    return 'Cancelled';
  }

  if (sourcePayments.every((payment) => payment.status === 'Completed')) {
    return 'Completed';
  }

  if (sourcePayments.some((payment) => payment.status === 'Overdue')) {
    return 'Overdue';
  }

  const hasProgress = sourcePayments.some(
    (payment) =>
      payment.status === 'Completed'
      || payment.status === 'Processing'
      || payment.hasPartialPayment
      || resolvePaidAmount(payment) > 0,
  );

  return hasProgress ? 'Processing' : 'Pending';
}

function invoiceStatusLabel(status: PaymentStatus): string {
  switch (status) {
    case 'Completed':
      return 'Оплачен';
    case 'Processing':
      return 'Частично оплачен';
    case 'Overdue':
      return 'Просрочен';
    case 'Cancelled':
      return 'Отменен';
    case 'Pending':
    default:
      return 'Не оплачен';
  }
}

function parseStatusTimelineEntry(note: string, payment: Payment, index: number): StatusTimelineEntry | null {
  const trimmed = note.trim().replace(/^[•*-]\s*/u, '');
  if (!trimmed) return null;

  if (!/статус|оплач|остаток|срок|планов|дата платежа|перенес|перенос|частич/iu.test(trimmed)) {
    return null;
  }

  const match = trimmed.match(/^(\d{2})\.(\d{2})\.(\d{4}) \((\d{2}):(\d{2}):(\d{2})\)\s*[—-]\s*(.+?):\s*(.+)$/u);
  if (match) {
    const [, day, month, year, hour, minute, second, author, message] = match;
    const sortValue = Date.UTC(
      Number.parseInt(year, 10),
      Number.parseInt(month, 10) - 1,
      Number.parseInt(day, 10),
      Number.parseInt(hour, 10),
      Number.parseInt(minute, 10),
      Number.parseInt(second, 10),
    );

    return {
      key: `${payment.id}-${index}-${sortValue}`,
      title: message,
      description: author,
      sourceLabel: paymentSourceLabel(payment),
      timestampLabel: `${day}.${month}.${year} ${hour}:${minute}`,
      sortValue,
      isCurrent: false,
    };
  }

  const fallbackTimestamp = Date.parse(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt ?? '') || 0;
  return {
    key: `${payment.id}-${index}-${trimmed}`,
    title: trimmed,
    description: 'Системное изменение по связанному платежу',
    sourceLabel: paymentSourceLabel(payment),
    timestampLabel: formatDate(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt) || 'Без даты',
    sortValue: fallbackTimestamp,
    isCurrent: false,
  };
}

function buildInvoiceStatusSnapshot(
  payments: Payment[],
  fallbackStatus: PaymentStatus,
  fallbackDueDate?: string | null,
  fallbackAmount?: string | number | null,
): InvoiceStatusSnapshot {
  const sourcePayments = getStatusSourcePayments(payments);
  const status = deriveInvoiceStatusFromPayments(payments, fallbackStatus, fallbackDueDate);
  const fallbackAmountValue = parseMoneyInput(fallbackAmount);
  const aggregatedTotalFromPayments = sourcePayments.reduce((sum, payment) => {
    const paymentAmount = Math.max(payment.amount ?? 0, 0);
    const resolvedAmount = Math.max(paymentAmount, resolvePaidAmount(payment) + resolveOutstandingAmount(payment));
    return sum + resolvedAmount;
  }, 0);

  const totalAmount = sourcePayments.length > 0
    ? Math.max(fallbackAmountValue, aggregatedTotalFromPayments)
    : (fallbackAmountValue > 0 ? fallbackAmountValue : aggregatedTotalFromPayments);
  const paidAmount = sourcePayments.reduce((sum, payment) => sum + resolvePaidAmount(payment), 0);
  const rawOutstandingAmount = sourcePayments.reduce((sum, payment) => sum + resolveOutstandingAmount(payment), 0);
  const outstandingAmount = rawOutstandingAmount > 0
    ? rawOutstandingAmount
    : Math.max(totalAmount - paidAmount, 0);

  let reason = 'Связанные платежи по счету еще не созданы.';
  if (status === 'Completed') {
    reason = 'Все связанные платежи проведены, остатка к оплате нет.';
  } else if (status === 'Cancelled') {
    reason = 'Все связанные платежи по счету отменены.';
  } else if (status === 'Overdue') {
    reason = paidAmount > 0 && totalAmount > 0
      ? `По счету есть оплаты на ${formatCurrencySmart(paidAmount).short}, но остаток ${formatCurrencySmart(outstandingAmount).short} уже просрочен.`
      : 'Срок оплаты истек, а связанные платежи по счету еще не закрыты.';
  } else if (status === 'Processing') {
    reason = totalAmount > 0
      ? `По счету оплачено ${formatCurrencySmart(paidAmount).short} из ${formatCurrencySmart(totalAmount).short}. Остаток ${formatCurrencySmart(outstandingAmount).short} еще не закрыт.`
      : 'По счету есть частичная оплата, но он еще не закрыт полностью.';
  } else if (sourcePayments.length > 0) {
    reason = 'Связанные платежи созданы, но оплаты по счету пока не поступили.';
  }

  const historyEntries = payments
    .flatMap((payment) =>
      (payment.systemNotes ?? '')
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line, index) => parseStatusTimelineEntry(line, payment, index))
        .filter((entry): entry is StatusTimelineEntry => entry !== null),
    )
    .sort((left, right) => right.sortValue - left.sortValue);

  const currentEntry: StatusTimelineEntry = {
    key: `current-${fallbackStatus}-${paidAmount}-${outstandingAmount}`,
    title: `Текущий статус: ${invoiceStatusLabel(fallbackStatus)}`,
    description: reason,
    sourceLabel: sourcePayments.length > 0
      ? `Связанных платежей: ${sourcePayments.length}`
      : 'Статус пока определяется по самому счету',
    timestampLabel: historyEntries[0]?.timestampLabel ?? 'Сейчас',
    sortValue: historyEntries[0]?.sortValue ?? Date.now(),
    isCurrent: true,
  };

  return {
    status,
    reason,
    totalAmount,
    paidAmount,
    outstandingAmount,
    sourcePayments,
    timeline: [currentEntry, ...historyEntries],
  };
}


export function InvoiceModal({
  open,
  mode,
  invoice,
  resumeDraft,
  onClose,
  onBack,
  onOpenClient,
  onOpenContract,
  onOpenPayment,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  responsibles = [],
  lookupsLoading,
  lookupsError,
  defaultClientId,
  defaultResponsibleId,
  defaultType,
  lockType = false,
  presentation = 'modal',
  relatedPayments = [],
  paymentsLoading = false,
  onRefreshRequested,
  onAddRelatedPayment,
  onEditRelatedPayment,
  onDeleteRelatedPayment,
  onClientCaseCreated,
}: InvoiceModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const typeOptions: PaymentKind[] = ['Income', 'Expense'];
  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };
  const isTypeLocked = lockType && mode === 'create';
  const [form, setForm] = useState<FormState>(() =>
    normalizeInvoiceToForm(invoice, defaultClientId, defaultResponsibleId, defaultType),
  );
  const [lineItems, setLineItems] = useState<LineItem[]>([]);
  const [lineItemLookupState, setLineItemLookupState] = useState<LineItemLookupState>(null);
  const [lineItemLookupQuery, setLineItemLookupQuery] = useState('');
  const [localError, setLocalError] = useState<string | null>(null);
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientPickerSearch, setClientPickerSearch] = useState('');
  const [selectedActs, setSelectedActs] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [actOptions, setActOptions] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [clientCaseOptions, setClientCaseOptions] = useState<ClientCaseOption[]>(() => normalizeInvoiceClientCases(invoice));
  const [selectedClientCaseIds, setSelectedClientCaseIds] = useState<number[]>(() => normalizeInvoiceClientCaseIds(invoice));
  const [caseDropdownOpen, setCaseDropdownOpen] = useState(false);
  const [createCaseOpen, setCreateCaseOpen] = useState(false);
  const [createActOpen, setCreateActOpen] = useState(false);
  const [pageViewMode, setPageViewMode] = useState<boolean>(presentation === 'page' && mode === 'edit');
  const [creatingAct, setCreatingAct] = useState(false);
  const [createActError, setCreateActError] = useState<string | null>(null);
  const [draftAct, setDraftAct] = useState<DraftActForm>({
    number: '',
    title: '',
    date: formatLocalYMD(new Date()),
    amount: '',
    comment: '',
  });
  const [contractOptions, setContractOptions] = useState<Contract[]>([]);
  const [contractsLoading, setContractsLoading] = useState(false);
  const [contractsError, setContractsError] = useState<string | null>(null);
  const [contractsLoadedClientId, setContractsLoadedClientId] = useState('');
  const [createContractOpen, setCreateContractOpen] = useState(false);
  const [createContractSubmitting, setCreateContractSubmitting] = useState(false);
  const [createContractError, setCreateContractError] = useState<string | null>(null);
  const [contractPreviewOpen, setContractPreviewOpen] = useState(false);
  const [contractPreviewSeed, setContractPreviewSeed] = useState<Contract | null>(null);
  const [contractPreviewDetails, setContractPreviewDetails] = useState<Contract | null>(null);
  const [contractPreviewLoading, setContractPreviewLoading] = useState(false);
  const [contractPreviewError, setContractPreviewError] = useState<string | null>(null);
  const [paymentPreviewOpen, setPaymentPreviewOpen] = useState(false);
  const [paymentPreviewSeed, setPaymentPreviewSeed] = useState<Payment | null>(null);
  const [paymentPreviewDetails, setPaymentPreviewDetails] = useState<Payment | null>(null);
  const [paymentPreviewLoading, setPaymentPreviewLoading] = useState(false);
  const [paymentPreviewError, setPaymentPreviewError] = useState<string | null>(null);
  const [clientCasesLoading, setClientCasesLoading] = useState(false);
  const [clientCasesError, setClientCasesError] = useState<string | null>(null);
  const [actsLoading, setActsLoading] = useState(false);
  const [actsError, setActsError] = useState<string | null>(null);
  const [invoiceRules, setInvoiceRules] = useState({ maxClientCasesPerInvoice: 1, maxActsPerInvoice: 1 });
  const [numberEditedManually, setNumberEditedManually] = useState(false);
  const contractPreviewRequestRef = useRef(0);
  const paymentPreviewRequestRef = useRef(0);
  const visibleRelatedPayments = useMemo(
    () => filterRepresentativeInvoiceWhenInstallmentsExist(relatedPayments),
    [relatedPayments],
  );
  const [displayPayments, setDisplayPayments] = useState<Payment[]>(visibleRelatedPayments);
  const [installmentPlan, setInstallmentPlan] = useState<InstallmentPlanInput | null>(() => cloneInstallmentPlan(deriveInstallmentPlanFromPayments(visibleRelatedPayments)));
  const [installmentModalOpen, setInstallmentModalOpen] = useState(false);
  const [installmentPlanTouched, setInstallmentPlanTouched] = useState(false);
  const [documentPreviewHtml, setDocumentPreviewHtml] = useState('');
  const [documentPreviewLoading, setDocumentPreviewLoading] = useState(false);
  const [documentPreviewError, setDocumentPreviewError] = useState<string | null>(null);
  const [downloadMenuOpen, setDownloadMenuOpen] = useState(false);
  const lastErrorToastRef = useRef<string | null>(null);
  const lastLocalErrorToastRef = useRef<string | null>(null);
  const lastAutoResponsibleIdRef = useRef<string>('');
  const lastAutoContractClientIdRef = useRef<string>('');
  const contractOptionsRevisionRef = useRef(0);
  const downloadMenuRef = useRef<HTMLDivElement | null>(null);

  const calculateLineItemsTotal = useCallback(
    (items: LineItem[]) => {
      const total = items.reduce((sum, item) => sum + parseMoneyInput(item.quantity) * parseMoneyInput(item.price), 0);
      return formatMoneyDisplayValue(total);
    },
    [],
  );

  const { invoiceUnits, services, serviceCategories } = useDictionaries();
  const unitOptions = useMemo(() => invoiceUnits.map((item) => item.name).filter((name) => Boolean(name?.trim())), [invoiceUnits]);
  const sortedServiceCategories = useMemo(
    () => [...serviceCategories].sort((left, right) => left.name.localeCompare(right.name, 'ru')),
    [serviceCategories],
  );
  const sortedServices = useMemo(
    () => [...services].sort((left, right) => left.name.localeCompare(right.name, 'ru')),
    [services],
  );
  const serviceCategoryById = useMemo(() => {
    const map = new Map<number, string>();
    for (const category of sortedServiceCategories) {
      map.set(category.id, category.name);
    }
    return map;
  }, [sortedServiceCategories]);
  const servicesByCategoryId = useMemo(() => {
    const map = new Map<string, ServiceDictionaryItem[]>();
    for (const service of sortedServices) {
      const key = service.categoryId ? String(service.categoryId) : '';
      const bucket = map.get(key);
      if (bucket) {
        bucket.push(service);
      } else {
        map.set(key, [service]);
      }
    }
    return map;
  }, [sortedServices]);
  const activeLineItemLookup = useMemo(() => {
    if (!lineItemLookupState) {
      return null;
    }

    return lineItems.find((item) => item.id === lineItemLookupState.lineItemId) ?? null;
  }, [lineItemLookupState, lineItems]);
  const lineItemCategoryLookupOptions = useMemo(
    () =>
      sortedServiceCategories.map((category) => ({
        value: String(category.id),
        label: category.name,
        description: category.description?.trim() || undefined,
        keywords: [category.name, category.description ?? ''],
      })),
    [sortedServiceCategories],
  );
  const lineItemServiceLookupOptions = useMemo(() => {
    if (!activeLineItemLookup) {
      return [];
    }

    const source = activeLineItemLookup.serviceCategoryId
      ? servicesByCategoryId.get(activeLineItemLookup.serviceCategoryId) ?? []
      : sortedServices;

    return source.map((service) => {
      const categoryLabel = service.categoryId ? serviceCategoryById.get(service.categoryId) : null;
      const descriptionParts = [
        categoryLabel ? `Категория: ${categoryLabel}` : null,
        service.unit?.trim() ? `Ед.: ${service.unit.trim()}` : null,
        service.defaultPrice !== undefined && service.defaultPrice !== null
          ? `Цена по умолчанию: ${formatCurrencySmart(service.defaultPrice).full}`
          : null,
        service.description?.trim() || null,
      ].filter(Boolean);

      return {
        value: String(service.id),
        label: service.name,
        description: descriptionParts.join(' · ') || undefined,
        keywords: [
          service.name,
          categoryLabel ?? '',
          service.unit ?? '',
          service.description ?? '',
          service.defaultPrice !== undefined && service.defaultPrice !== null ? String(service.defaultPrice) : '',
        ],
      };
    });
  }, [activeLineItemLookup, serviceCategoryById, servicesByCategoryId, sortedServices]);

  const getServiceTemplateById = useCallback((serviceId: string | null | undefined) => {
    if (!serviceId) return null;
    return sortedServices.find((item) => String(item.id) === serviceId) ?? null;
  }, [sortedServices]);

  const findServiceTemplate = useCallback((name: string, serviceCategoryId?: string) => {
    const normalizedName = normalizeLookupName(name);
    if (!normalizedName) return null;

    const exactMatches = sortedServices.filter((item) => normalizeLookupName(item.name) === normalizedName);
    if (!exactMatches.length) return null;

    if (serviceCategoryId) {
      const categoryMatch = exactMatches.find((item) => String(item.categoryId ?? '') === serviceCategoryId);
      if (categoryMatch) return categoryMatch;
    }

    return exactMatches[0] ?? null;
  }, [sortedServices]);

  const resolveLineItemTemplate = useCallback((
    item: Pick<LineItem, 'name' | 'serviceCategoryId' | 'serviceId'>,
    nameOverride?: string,
    serviceCategoryIdOverride?: string,
  ) => {
    const effectiveName = nameOverride ?? item.name;
    const effectiveCategoryId = serviceCategoryIdOverride ?? item.serviceCategoryId;
    const selectedTemplate = getServiceTemplateById(item.serviceId);
    const selectedTemplateMatchesCategory = !effectiveCategoryId
      || String(selectedTemplate?.categoryId ?? '') === effectiveCategoryId;

    if (
      selectedTemplate
      && selectedTemplateMatchesCategory
      && normalizeLookupName(effectiveName)
      && normalizeLookupName(effectiveName) === normalizeLookupName(selectedTemplate.name)
    ) {
      return selectedTemplate;
    }

    const exactMatch = findServiceTemplate(effectiveName, effectiveCategoryId);
    if (exactMatch) {
      return exactMatch;
    }

    return selectedTemplate && selectedTemplateMatchesCategory ? selectedTemplate : null;
  }, [findServiceTemplate, getServiceTemplateById]);

  const handleLineItemPriceChange = (id: string, rawValue: string) => {
    updateLineItem(id, { price: formatMoneyInputValue(rawValue) });
  };

  const handleLineItemPriceBlur = (id: string, rawValue: string) => {
    const formatted = formatMoneyInputValue(rawValue);
    updateLineItem(id, { price: formatted || formatMoneyDisplayValue(0) });
  };

  const handleLineItemCategoryChange = (id: string, serviceCategoryId: string) => {
    const currentLineItem = lineItems.find((item) => item.id === id);
    const template = currentLineItem ? resolveLineItemTemplate(currentLineItem, currentLineItem.name, serviceCategoryId) : null;
    const patch: Partial<LineItem> = {
      serviceCategoryId,
      serviceId: template ? String(template.id) : '',
    };

    if (template?.unit?.trim()) {
      patch.unit = template.unit.trim();
    }

    if (template?.defaultPrice !== undefined && template?.defaultPrice !== null) {
      patch.price = formatMoneyDisplayValue(template.defaultPrice);
    }

    updateLineItem(id, patch);
  };

  const openLineItemLookup = (lookup: Exclude<LineItemLookupState, null>) => {
    const currentLineItem = lineItems.find((item) => item.id === lookup.lineItemId);
    setLineItemLookupQuery(lookup.kind === 'service' ? currentLineItem?.name ?? '' : '');
    setLineItemLookupState(lookup);
  };

  const closeLineItemLookup = () => {
    setLineItemLookupState(null);
    setLineItemLookupQuery('');
  };

  const handleLineItemServiceSelect = (serviceId: string) => {
    const currentLineItemId = lineItemLookupState?.lineItemId;
    if (!currentLineItemId) {
      return;
    }

    const selectedService = sortedServices.find((service) => String(service.id) === serviceId);
    if (!selectedService) {
      return;
    }

    const patch: Partial<LineItem> = {
      name: selectedService.name,
      serviceCategoryId: selectedService.categoryId ? String(selectedService.categoryId) : '',
      serviceId: String(selectedService.id),
    };

    if (selectedService.unit?.trim()) {
      patch.unit = selectedService.unit.trim();
    }

    if (selectedService.defaultPrice !== undefined && selectedService.defaultPrice !== null) {
      patch.price = formatMoneyDisplayValue(selectedService.defaultPrice);
    }

    updateLineItem(currentLineItemId, patch);
    closeLineItemLookup();
  };

  const applyCustomLineItemServiceName = () => {
    const currentLineItemId = lineItemLookupState?.lineItemId;
    const trimmedQuery = lineItemLookupQuery.trim();
    if (!currentLineItemId || !trimmedQuery) {
      return;
    }

    const currentLineItem = lineItems.find((item) => item.id === currentLineItemId);
    if (!currentLineItem) {
      return;
    }

    const template = resolveLineItemTemplate(currentLineItem, trimmedQuery);
    const patch: Partial<LineItem> = {
      name: trimmedQuery,
      serviceId: template ? String(template.id) : '',
    };

    if (template?.categoryId) {
      patch.serviceCategoryId = String(template.categoryId);
    }

    if (template?.unit?.trim()) {
      patch.unit = template.unit.trim();
    }

    if (template?.defaultPrice !== undefined && template?.defaultPrice !== null) {
      patch.price = formatMoneyDisplayValue(template.defaultPrice);
    }

    updateLineItem(currentLineItemId, patch);
    closeLineItemLookup();
  };

  const clearLineItemServiceName = () => {
    const currentLineItemId = lineItemLookupState?.lineItemId;
    if (!currentLineItemId) {
      return;
    }

    updateLineItem(currentLineItemId, { name: '', serviceId: '' });
    closeLineItemLookup();
  };

  const [actDropdownOpen, setActDropdownOpen] = useState(false);

  useEffect(() => {
    if (presentation === 'page' && mode === 'edit') {
      setPageViewMode(true);
      return;
    }
    setPageViewMode(false);
  }, [mode, presentation, invoice?.id]);

  useEffect(() => {
    if (presentation !== 'page' || !pageViewMode || !invoice?.id) {
      setDocumentPreviewHtml('');
      setDocumentPreviewError(null);
      setDocumentPreviewLoading(false);
      return;
    }

    let active = true;
    const loadPreview = async () => {
      setDocumentPreviewLoading(true);
      setDocumentPreviewError(null);
      try {
        const blob = await apiService.downloadInvoiceDocument(invoice.id, 'html', {
          includeSignatureAndSeal: false,
        });
        const html = await blob.text();
        if (!active) return;
        setDocumentPreviewHtml(html);
      } catch (err) {
        if (!active) return;
        setDocumentPreviewError(err instanceof Error ? err.message : 'Не удалось загрузить печатный шаблон счета.');
        setDocumentPreviewHtml('');
      } finally {
        if (active) {
          setDocumentPreviewLoading(false);
        }
      }
    };

    void loadPreview();

    return () => {
      active = false;
    };
  }, [invoice?.id, invoice?.auditUpdatedAt, pageViewMode, presentation]);

  useEffect(() => {
    if (!downloadMenuOpen) return undefined;

    const handlePointerDown = (event: MouseEvent) => {
      if (!downloadMenuRef.current) return;
      if (!downloadMenuRef.current.contains(event.target as Node)) {
        setDownloadMenuOpen(false);
      }
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setDownloadMenuOpen(false);
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [downloadMenuOpen]);

  const resolveCaseOwnerClientId = useCallback(
    (type: PaymentKind, clientId: string, expenseClientId: string): string =>
      type === 'Expense' ? expenseClientId : clientId,
    [],
  );

  const isIncome = form.type === 'Income';
  const isPageMode = presentation === 'page';
  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === form.clientId) ?? null,
    [clients, form.clientId],
  );
  const selectedExpenseClient = useMemo(
    () => clients.find((client) => String(client.id) === form.expenseClientId) ?? null,
    [clients, form.expenseClientId],
  );
  const caseOwnerClientId = resolveCaseOwnerClientId(form.type, form.clientId, form.expenseClientId);
  const selectedCaseOwnerClient = isIncome ? selectedClient : selectedExpenseClient;
  const selectedResponsibleSourceClient = isIncome
    ? selectedClient
    : (selectedExpenseClient ?? selectedClient);
  const counterpartyLabel = isIncome ? 'Клиент' : 'Поставщик';
  const counterpartyBlockTitle = isIncome ? 'Клиент и договор' : 'Поставщик и договор';
  const counterpartyPlaceholder = isIncome ? 'Выберите клиента' : 'Выберите поставщика';
  const counterpartyOpenCardLabel = isIncome ? 'Открыть карточку клиента' : 'Открыть карточку поставщика';
  const clientLookupOptions = useMemo(
    () =>
      clients.map((client) => ({
        value: String(client.id),
        label: client.name,
        description: [
          client.company,
          client.clientStatus?.name,
          client.responsibleUserName ? `Ответственный: ${client.responsibleUserName}` : null,
        ].filter(Boolean).join(' · '),
        keywords: [client.company ?? '', client.clientStatus?.name ?? '', client.phone ?? '', client.email ?? ''],
      })),
    [clients],
  );
  const relatedInstallmentPlan = useMemo(
    () => cloneInstallmentPlan(deriveInstallmentPlanFromPayments(visibleRelatedPayments)),
    [visibleRelatedPayments],
  );
  const selectedResponsibleId = form.responsibleIds[0] ?? '';
  const selectedContract = useMemo(
    () => contractOptions.find((contract) => String(contract.id) === form.contractId) ?? null,
    [contractOptions, form.contractId],
  );
  const selectedContractPreview = contractPreviewDetails ?? contractPreviewSeed;
  const selectedPaymentPreview = paymentPreviewDetails ?? paymentPreviewSeed;
  const selectedPaymentPreviewInstallment = selectedPaymentPreview
    ? parseInstallmentPaymentMetadata(selectedPaymentPreview.systemNotes)
    : null;
  const defaultContractClients = useMemo<ContractClient[]>(
    () =>
      selectedClient
        ? [
            {
              id: selectedClient.id,
              name: selectedClient.name,
              company: selectedClient.company,
              clientStatusId: selectedClient.clientStatusId ?? null,
              clientStatus: selectedClient.clientStatus ?? null,
            },
          ]
        : [],
    [selectedClient],
  );
  const responsibleOptions = useMemo(() => {
    const options = [...responsibles];
    const hasSelectedResponsible = options.some(
      (responsible) => String(responsible.id) === selectedResponsibleId,
    );

    if (!hasSelectedResponsible && selectedResponsibleId) {
      const fallbackLabel =
        invoice?.responsibles?.find((responsible) => String(responsible.id) === selectedResponsibleId)
          ?.fullName
        ?? (selectedResponsibleSourceClient?.responsibleUserId
          && String(selectedResponsibleSourceClient.responsibleUserId) === selectedResponsibleId
            ? selectedResponsibleSourceClient.responsibleUserName
            : null)
        ?? `#${selectedResponsibleId}`;

      options.push({
        id: Number(selectedResponsibleId),
        fullName: fallbackLabel,
      });
    }

    return options
      .sort((left, right) => left.fullName.localeCompare(right.fullName, 'ru', { sensitivity: 'base' }))
      .map((responsible) => ({
        value: String(responsible.id),
        label: responsible.fullName,
      }));
  }, [
    invoice?.responsibles,
    responsibles,
    selectedResponsibleSourceClient?.responsibleUserId,
    selectedResponsibleSourceClient?.responsibleUserName,
    selectedResponsibleId,
  ]);
  const selectedClientCasesSummary = useMemo(() => {
    if (!selectedClientCaseIds.length) return '';
    return selectedClientCaseIds
      .map((id) => {
        const match = clientCaseOptions.find((item) => item.id === id) ?? null;
        return clientCaseLabel(match ?? { id, clientId: selectedCaseOwnerClient?.id ?? 0, title: '', status: '', createdAt: '' });
      })
      .join(', ');
  }, [clientCaseOptions, selectedCaseOwnerClient?.id, selectedClientCaseIds]);
  const selectedActsSummary = useMemo(
    () => selectedActs.map((item) => actLabel(item)).filter(Boolean).join(', '),
    [selectedActs],
  );

  const filteredClients = useMemo(() => {
    const query = clientPickerSearch.trim().toLowerCase();
    if (!query) return clients;
    return clients.filter((client) => {
      const statusName = client.clientStatus?.name ?? '';
      return [client.name, client.company ?? '', statusName]
        .join(' ')
        .toLowerCase()
        .includes(query);
    });
  }, [clientPickerSearch, clients]);
  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;

    async function loadInvoiceRules() {
      try {
        const response = await apiService.getInvoiceRulesSettings();
        if (!cancelled) {
          setInvoiceRules({
            maxClientCasesPerInvoice: Math.max(1, response.maxClientCasesPerInvoice || 1),
            maxActsPerInvoice: Math.max(1, response.maxActsPerInvoice || 1),
          });
        }
      } catch {
        if (!cancelled) {
          setInvoiceRules({ maxClientCasesPerInvoice: 1, maxActsPerInvoice: 1 });
        }
      }
    }

    void loadInvoiceRules();
    return () => {
      cancelled = true;
    };
  }, [open]);

  useEffect(() => {
    let cancelled = false;

    async function loadContractsByClient() {
      if (!open || !form.clientId) {
        setContractOptions([]);
        setContractsError(null);
        setContractsLoadedClientId('');
        return;
      }

      const clientId = Number.parseInt(form.clientId, 10);
      if (!Number.isFinite(clientId) || clientId <= 0) {
        setContractOptions([]);
        setContractsError(null);
        setContractsLoadedClientId('');
        return;
      }

      const requestRevision = contractOptionsRevisionRef.current;
      const requestClientId = String(clientId);
      setContractsLoading(true);
      setContractsError(null);
      setContractsLoadedClientId('');
      try {
        const response = await apiService.getContracts({
          clientId,
          page: 1,
          pageSize: 200,
          sortBy: 'createdAt',
          sortDir: 'desc',
        });
        if (!cancelled && requestRevision === contractOptionsRevisionRef.current) {
          setContractOptions(sortContractsByRecency(response.items ?? []));
          setContractsLoadedClientId(requestClientId);
        }
      } catch (error) {
        if (!cancelled && requestRevision === contractOptionsRevisionRef.current) {
          setContractOptions([]);
          setContractsLoadedClientId(requestClientId);
          setContractsError(error instanceof Error ? error.message : 'Не удалось загрузить договоры');
        }
      } finally {
        if (!cancelled) setContractsLoading(false);
      }
    }

    void loadContractsByClient();
    return () => {
      cancelled = true;
    };
  }, [open, form.clientId]);

  useEffect(() => {
    if (!open || mode !== 'create' || !form.clientId || form.contractId || contractOptions.length === 0) {
      return;
    }

    if (lastAutoContractClientIdRef.current === form.clientId) {
      return;
    }

    const latestContract = contractOptions[0];
    if (!latestContract) {
      return;
    }

    setForm((prev) => (
      prev.clientId === form.clientId && !prev.contractId
        ? { ...prev, contractId: String(latestContract.id) }
        : prev
    ));
    lastAutoContractClientIdRef.current = form.clientId;
  }, [contractOptions, form.clientId, form.contractId, mode, open]);

  useEffect(() => {
    if (!open || !form.contractId) return;
    if (!form.clientId) {
      setForm((prev) => ({ ...prev, contractId: '' }));
      return;
    }
    if (contractsLoading || contractsLoadedClientId !== form.clientId) return;
    if (contractOptions.some((contract) => String(contract.id) === form.contractId)) return;
    setForm((prev) => ({ ...prev, contractId: '' }));
  }, [contractOptions, contractsLoadedClientId, contractsLoading, form.clientId, form.contractId, open]);

  const handleCreateContractFromInvoice = useCallback(async (payload: ContractInput) => {
    setCreateContractSubmitting(true);
    setCreateContractError(null);
    try {
      const created = await apiService.createContract(payload);
      contractOptionsRevisionRef.current += 1;
      setContractOptions((prev) => {
        const next = [created, ...prev.filter((contract) => contract.id !== created.id)];
        return sortContractsByRecency(next);
      });
      setContractsLoadedClientId(form.clientId);
      setForm((prev) => ({ ...prev, contractId: String(created.id) }));
      lastAutoContractClientIdRef.current = form.clientId;
      setCreateContractOpen(false);
      toast.success('Договор создан и выбран в счете.', 'Договоры');
    } catch (error) {
      setCreateContractError(extractDisplayErrorMessage(error, 'Не удалось создать договор'));
    } finally {
      setCreateContractSubmitting(false);
    }
  }, [form.clientId, toast]);

  useEffect(() => {
    let cancelled = false;

    async function loadClientCasesByClient() {
      if (!open || !caseOwnerClientId) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        return;
      }

      const clientId = Number.parseInt(caseOwnerClientId, 10);
      if (!Number.isFinite(clientId) || clientId <= 0) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        return;
      }

      setClientCasesLoading(true);
      setClientCasesError(null);
      try {
        const response = await apiService.getCases({ clientId });
        if (!cancelled) {
          const nextOptions = [...(response ?? [])].sort(
            (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(),
          );
          setClientCaseOptions(nextOptions);
        }
      } catch (error) {
        if (!cancelled) {
          setClientCaseOptions([]);
          setClientCasesError(error instanceof Error ? error.message : 'Не удалось загрузить дела');
        }
      } finally {
        if (!cancelled) setClientCasesLoading(false);
      }
    }

    void loadClientCasesByClient();
    return () => {
      cancelled = true;
    };
  }, [open, caseOwnerClientId]);

  useEffect(() => {
    if (open) {
      const nextForm = resumeDraft?.form ?? normalizeInvoiceToForm(invoice, defaultClientId, defaultResponsibleId, defaultType);
      setForm(nextForm);
      setLineItems(resumeDraft?.lineItems ?? normalizeLineItems(invoice?.lineItems, invoice?.amount));
      setSelectedActs(resumeDraft?.selectedActs ?? normalizeInvoiceActs(invoice));
      setActOptions(normalizeInvoiceActs(invoice));
      setClientCaseOptions(normalizeInvoiceClientCases(invoice));
      setSelectedClientCaseIds(resumeDraft?.selectedClientCaseIds ?? normalizeInvoiceClientCaseIds(invoice));
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
      setActDropdownOpen(false);
      setActsError(null);
      setClientCasesError(null);
      setCreateActError(null);
      setLocalError(null);
      setInstallmentPlan(cloneInstallmentPlan(resumeDraft?.installmentPlan ?? relatedInstallmentPlan));
      setInstallmentModalOpen(false);
      setInstallmentPlanTouched(Boolean(resumeDraft?.installmentPlan));
      setNumberEditedManually(Boolean(resumeDraft?.form?.number));
      if (presentation === 'page') {
        setPageViewMode(resumeDraft?.pageViewMode ?? true);
      }
      lastAutoContractClientIdRef.current = nextForm.contractId ? nextForm.clientId : '';
    }
  }, [defaultClientId, defaultResponsibleId, defaultType, invoice, open, presentation, relatedInstallmentPlan, resumeDraft]);

  useEffect(() => {
    if (!open || resumeDraft?.installmentPlan || installmentPlanTouched) {
      return;
    }

    setInstallmentPlan(cloneInstallmentPlan(relatedInstallmentPlan));
  }, [open, relatedInstallmentPlan, installmentPlanTouched, resumeDraft?.installmentPlan]);

  useEffect(() => {
    setDisplayPayments(visibleRelatedPayments);
  }, [visibleRelatedPayments]);

  useEffect(() => {
    if (form.type !== 'Income' && installmentPlan) {
      setInstallmentPlan(null);
      setInstallmentModalOpen(false);
      setInstallmentPlanTouched(true);
    }
  }, [form.type, installmentPlan]);

  useEffect(() => {
    if (!open || mode !== 'create') return;

    const nextAutoResponsibleId = selectedResponsibleSourceClient?.responsibleUserId
      ? String(selectedResponsibleSourceClient.responsibleUserId)
      : defaultResponsibleId
        ? String(defaultResponsibleId)
        : '';

    setForm((prev) => {
      const currentResponsibleId = prev.responsibleIds[0] ?? '';
      const shouldReplace =
        !currentResponsibleId || currentResponsibleId === lastAutoResponsibleIdRef.current;

      if (!shouldReplace) {
        lastAutoResponsibleIdRef.current = nextAutoResponsibleId;
        return prev;
      }

      lastAutoResponsibleIdRef.current = nextAutoResponsibleId;
      if (currentResponsibleId === nextAutoResponsibleId) {
        return prev;
      }

      return {
        ...prev,
        responsibleIds: nextAutoResponsibleId ? [nextAutoResponsibleId] : [],
      };
    });
  }, [defaultResponsibleId, mode, open, selectedResponsibleSourceClient?.id, selectedResponsibleSourceClient?.responsibleUserId]);

  useEffect(() => {
    if (!errorMessage) {
      lastErrorToastRef.current = null;
      return;
    }

    if (lastErrorToastRef.current === errorMessage) {
      return;
    }

    toast.error(
      errorMessage,
      mode === 'edit' ? 'Не удалось обновить счет' : 'Не удалось создать счет',
    );
    lastErrorToastRef.current = errorMessage;
  }, [errorMessage, mode, toast]);

  useEffect(() => {
    if (!localError) {
      lastLocalErrorToastRef.current = null;
      return;
    }

    if (lastLocalErrorToastRef.current === localError) {
      return;
    }

    toast.error(localError, 'Проверьте данные счета');
    lastLocalErrorToastRef.current = localError;
  }, [localError, toast]);

  useEffect(() => {
    if (!open || mode !== 'create' || numberEditedManually) return;

    let cancelled = false;

    async function loadNextNumber() {
      try {
        const next = await apiService.getNextInvoiceNumber(fromInputToApiDate(form.date) ?? form.date);
        if (!cancelled) {
          setForm((prev) => ({ ...prev, number: next.nextNumber || prev.number }));
        }
      } catch {
        // noop: оставляем номер как есть
      }
    }

    void loadNextNumber();
    return () => {
      cancelled = true;
    };
  }, [open, mode, form.date, numberEditedManually]);

  const closeContractPreview = useCallback(() => {
    contractPreviewRequestRef.current += 1;
    setContractPreviewOpen(false);
    setContractPreviewSeed(null);
    setContractPreviewDetails(null);
    setContractPreviewLoading(false);
    setContractPreviewError(null);
  }, []);

  const openContractPreview = useCallback(async (contractIdRaw: string | number | null | undefined, seed?: Contract | null) => {
    const contractId = typeof contractIdRaw === 'number'
      ? contractIdRaw
      : Number.parseInt(String(contractIdRaw ?? ''), 10);

    if (!Number.isFinite(contractId) || contractId <= 0) {
      return;
    }

    setContractPreviewOpen(true);
    setContractPreviewSeed(seed ?? null);
    setContractPreviewDetails(null);
    setContractPreviewError(null);

    const requestId = contractPreviewRequestRef.current + 1;
    contractPreviewRequestRef.current = requestId;
    setContractPreviewLoading(true);

    try {
      const freshContract = await apiService.getContract(contractId);
      if (contractPreviewRequestRef.current !== requestId) return;
      setContractPreviewDetails(freshContract);
    } catch (error) {
      if (contractPreviewRequestRef.current !== requestId) return;
      setContractPreviewError(extractDisplayErrorMessage(error, 'Не удалось загрузить договор.'));
    } finally {
      if (contractPreviewRequestRef.current === requestId) {
        setContractPreviewLoading(false);
      }
    }
  }, []);

  const closePaymentPreview = useCallback(() => {
    paymentPreviewRequestRef.current += 1;
    setPaymentPreviewOpen(false);
    setPaymentPreviewSeed(null);
    setPaymentPreviewDetails(null);
    setPaymentPreviewLoading(false);
    setPaymentPreviewError(null);
  }, []);

  const openPaymentPreview = useCallback(async (payment: Payment) => {
    setPaymentPreviewOpen(true);
    setPaymentPreviewSeed(payment);
    setPaymentPreviewDetails(null);
    setPaymentPreviewError(null);

    if (!(typeof payment.id === 'number' && payment.id > 0)) {
      setPaymentPreviewLoading(false);
      return;
    }

    const requestId = paymentPreviewRequestRef.current + 1;
    paymentPreviewRequestRef.current = requestId;
    setPaymentPreviewLoading(true);

    try {
      const freshPayment = await apiService.getPayment(payment.id);
      if (paymentPreviewRequestRef.current !== requestId) return;
      setPaymentPreviewDetails(freshPayment);
    } catch (error) {
      if (paymentPreviewRequestRef.current !== requestId) return;
      setPaymentPreviewError(extractDisplayErrorMessage(error, 'Не удалось загрузить платеж.'));
    } finally {
      if (paymentPreviewRequestRef.current === requestId) {
        setPaymentPreviewLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    if (!open) {
      closeContractPreview();
      closePaymentPreview();
    }
  }, [closeContractPreview, closePaymentPreview, open]);

  useEffect(() => {
    const total = calculateLineItemsTotal(lineItems);
    setForm((prev) => (prev.amount === total ? prev : { ...prev, amount: total }));
  }, [lineItems, calculateLineItemsTotal]);

  useEffect(() => {
    if (!unitOptions.length) return;
    setLineItems((prev) => prev.map((item) => (item.unit.trim() ? item : { ...item, unit: unitOptions[0] })));
  }, [unitOptions]);

  useEffect(() => {
    if (!sortedServices.length) return;

    setLineItems((prev) => {
      let changed = false;
      const next = prev.map((item) => {
        const template = resolveLineItemTemplate(item);
        const nextCategoryId = item.serviceCategoryId || (template?.categoryId ? String(template.categoryId) : '');
        const nextServiceId = item.serviceId || (template ? String(template.id) : '');
        if (item.serviceCategoryId === nextCategoryId && item.serviceId === nextServiceId) return item;
        changed = true;
        return {
          ...item,
          serviceCategoryId: nextCategoryId,
          serviceId: nextServiceId,
        };
      });
      return changed ? next : prev;
    });
  }, [resolveLineItemTemplate, sortedServices.length]);

  const updateLineItem = (id: string, patch: Partial<LineItem>) => {
    setLineItems((prev) => {
      const next = prev.map((item) => (item.id === id ? { ...item, ...patch } : item));
      setForm((current) => ({ ...current, amount: calculateLineItemsTotal(next) }));
      return next;
    });
  };

  const addLineItem = () => {
    setLineItems((prev) => [
      ...prev,
      {
        id: `${Date.now()}-${prev.length}`,
        name: '',
        quantity: '1',
        unit: unitOptions[0] || 'шт',
        price: formatMoneyDisplayValue(0),
        serviceCategoryId: '',
        serviceId: '',
      },
    ]);
  };

  const removeLineItem = async (id: string) => {
    const accepted = await confirm({
      title: 'Удалить позицию',
      description: 'Точно ли хотите удалить позицию?',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;

    setLineItems((prev) => {
      if (prev.length <= 1) return prev;
      const next = prev.filter((item) => item.id !== id);
      setForm((current) => ({ ...current, amount: calculateLineItemsTotal(next) }));
      return next;
    });
  };

  useEffect(() => {
    let cancelled = false;

    async function loadActsByClient() {
      if (!open || !form.clientId) {
        setActOptions([]);
        setActsError(null);
        return;
      }

      setActsLoading(true);
      try {
        const response = await apiService.getActs({
          page: 1,
          pageSize: 200,
          sortBy: 'date',
          sortDir: 'desc',
          clientId: Number(form.clientId),
        });
        if (cancelled) return;

        const items = response.items ?? [];
        setActOptions(items.map((act) => ({
          id: act.id,
          number: act.number,
          title: act.title ?? undefined,
          date: act.date,
          amount: act.amount,
          invoiceNumber: act.invoiceNumber ?? undefined,
          clientId: act.clientId ?? undefined,
          clientName: act.clientName ?? undefined,
          status: act.status,
        })));
        setActsError(null);
      } catch (err) {
        if (cancelled) return;
        setActsError(err instanceof Error ? err.message : 'Не удалось загрузить акты');
        setActOptions([]);
      } finally {
        if (!cancelled) {
          setActsLoading(false);
        }
      }
    }

    void loadActsByClient();
    return () => {
      cancelled = true;
    };
  }, [form.clientId, open]);

  const handleActSelect = (act: ActOption) => {
    if (!selectedActs.some((item) => item.id === act.id) && selectedActs.length >= invoiceRules.maxActsPerInvoice) {
      toast.error(`В счёте можно выбрать не более ${invoiceRules.maxActsPerInvoice} акта(ов).`, 'Ограничение счёта');
      return;
    }

    setSelectedActs((prev) => (prev.some((item) => item.id === act.id) ? prev : [...prev, act]));
  };

  const removeSelectedAct = (actId: number) => {
    setSelectedActs((prev) => prev.filter((item) => item.id !== actId));
  };

  const toggleAct = (act: ActOption) => {
    if (selectedActs.some((item) => item.id === act.id)) {
      removeSelectedAct(act.id);
      return;
    }

    handleActSelect(act);
  };

  const handleTypeSelect = (nextType: PaymentKind) => {
    if (isTypeLocked) return;
    const nextCaseOwnerClientId = resolveCaseOwnerClientId(nextType, form.clientId, form.expenseClientId);
    if (nextType !== form.type && nextCaseOwnerClientId !== caseOwnerClientId) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      type: nextType,
      expenseClientId: nextType === 'Income' ? '' : prev.expenseClientId,
    }));
  };

  const openClientPicker = () => {
    setClientPickerSearch('');
    setClientPickerOpen(true);
  };

  const handleClientPick = (client: Client) => {
    const nextClientId = String(client.id);
    const clientChanged = form.clientId !== nextClientId;
    const nextCaseOwnerClientId = resolveCaseOwnerClientId(form.type, nextClientId, form.expenseClientId);
    if (clientChanged) {
      lastAutoContractClientIdRef.current = '';
      setSelectedActs([]);
      setActOptions([]);
      if (nextCaseOwnerClientId !== caseOwnerClientId) {
        setSelectedClientCaseIds([]);
        setClientCaseOptions([]);
      }
      setCaseDropdownOpen(false);
      setActDropdownOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      clientId: nextClientId,
      contractId: clientChanged ? '' : prev.contractId,
    }));
    setClientPickerOpen(false);
  };

  const handleExpenseClientChange = (nextClientId: string) => {
    const nextCaseOwnerClientId = resolveCaseOwnerClientId(form.type, form.clientId, nextClientId);
    if (form.expenseClientId !== nextClientId && nextCaseOwnerClientId !== caseOwnerClientId) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }

    setForm((prev) => ({
      ...prev,
      expenseClientId: nextClientId,
    }));
  };

  const handleClientCaseSelect = (clientCase: ClientCaseOption) => {
    if (!selectedClientCaseIds.includes(clientCase.id) && selectedClientCaseIds.length >= invoiceRules.maxClientCasesPerInvoice) {
      toast.error(`В счёте можно выбрать не более ${invoiceRules.maxClientCasesPerInvoice} дела(дел).`, 'Ограничение счёта');
      return;
    }

    setSelectedClientCaseIds((prev) => (prev.includes(clientCase.id) ? prev : [...prev, clientCase.id]));
  };

  const removeSelectedClientCase = (clientCaseId: number) => {
    setSelectedClientCaseIds((prev) => prev.filter((id) => id !== clientCaseId));
  };

  const toggleClientCase = (clientCaseId: number) => {
    if (selectedClientCaseIds.includes(clientCaseId)) {
      removeSelectedClientCase(clientCaseId);
      return;
    }

    const clientCase = clientCaseOptions.find((item) => item.id === clientCaseId);
    if (clientCase) {
      handleClientCaseSelect(clientCase);
    }
  };

  const handleCreateClientCase = async (patch: Partial<ClientCase>) => {
    if (!caseOwnerClientId) {
      throw new Error(isIncome ? 'Сначала выберите клиента' : 'Сначала выберите клиента, к которому относится расход');
    }

    const created = await apiService.createCase({
      clientId: Number(caseOwnerClientId),
      title: patch.title?.trim() ?? '',
      description: patch.description?.trim(),
      status: patch.status ?? 'Open',
    });

    setClientCaseOptions((prev) => {
      const next = [created, ...prev.filter((item) => item.id !== created.id)];
      return next.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime());
    });
    onClientCaseCreated?.(created);
    setSelectedClientCaseIds((prev) => {
      if (prev.includes(created.id)) return prev;
      if (prev.length >= invoiceRules.maxClientCasesPerInvoice) {
        toast.error(`В счёте можно выбрать не более ${invoiceRules.maxClientCasesPerInvoice} дела(дел).`, 'Ограничение счёта');
        return prev;
      }

      return [...prev, created.id];
    });
    setCaseDropdownOpen(false);
    setCreateCaseOpen(false);
  };

  const handleCreateAct = async () => {
    if (!draftAct.number.trim()) {
      setCreateActError(t('actNumber') ?? 'Номер акта обязателен');
      return;
    }

    const actAmount = parseMoneyInput(draftAct.amount || form.amount);
    if (!Number.isFinite(actAmount) || actAmount < 0) {
      setCreateActError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    setCreatingAct(true);
    setCreateActError(null);
    try {
      const linkedInvoiceId = typeof invoice?.id === 'number' && invoice.id > 0 ? invoice.id : undefined;
      const shouldBuildFromInvoice = Boolean(linkedInvoiceId && (invoice?.lineItems?.length ?? 0) > 0);
      const created = await apiService.createAct({
        number: draftAct.number.trim(),
        title: draftAct.title.trim() || undefined,
        date: fromInputToApiDate(draftAct.date) ?? draftAct.date,
        amount: shouldBuildFromInvoice ? 0 : actAmount,
        creationMode: shouldBuildFromInvoice ? 'Full' : 'Manual',
        invoiceId: linkedInvoiceId,
        invoiceNumber: form.number.trim() || undefined,
        clientId: form.clientId ? Number(form.clientId) : undefined,
        contractId: form.contractId ? Number(form.contractId) : undefined,
        responsibleId: selectedResponsibleId ? Number(selectedResponsibleId) : undefined,
        counterpartyInn: invoice?.counterpartyInn?.trim() || undefined,
        status: 'Created',
        comment: draftAct.comment.trim() || undefined,
      });

      const option: ActOption = {
        id: created.id,
        number: created.number,
        title: created.title ?? undefined,
        date: created.date,
        amount: created.amount,
        invoiceNumber: created.invoiceNumber ?? undefined,
        clientId: created.clientId ?? undefined,
        clientName: created.clientName ?? undefined,
        status: created.status,
      };

      setActOptions((prev) => [option, ...prev.filter((item) => item.id !== option.id)]);
      setSelectedActs((prev) => {
        if (prev.some((item) => item.id === option.id)) return prev;
        if (prev.length >= invoiceRules.maxActsPerInvoice) {
          toast.error(`В счёте можно выбрать не более ${invoiceRules.maxActsPerInvoice} акта(ов).`, 'Ограничение счёта');
          return prev;
        }

        return [...prev, option];
      });
      setActDropdownOpen(false);
      setCreateActOpen(false);
      setDraftAct({
        number: '',
        title: '',
        date: form.date || formatLocalYMD(new Date()),
        amount: form.amount,
        comment: '',
      });
    } catch (err) {
      setCreateActError(err instanceof Error ? err.message : 'Не удалось создать акт');
    } finally {
      setCreatingAct(false);
    }
  };

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    if (field === 'number') {
      setNumberEditedManually(true);
    }
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleBooleanChange = (field: keyof Pick<FormState, 'isNotForAccounting'>) => (
    event: React.ChangeEvent<HTMLInputElement>,
  ) => {
    setForm((prev) => ({ ...prev, [field]: event.target.checked }));
  };

  const handleResponsibleChange = (value: string) => {
    setForm((prev) => ({
      ...prev,
      responsibleIds: value ? [value] : [],
    }));
  };

  const statusSnapshot = useMemo(
    () => buildInvoiceStatusSnapshot(displayPayments, form.status, form.dueDate, form.amount),
    [displayPayments, form.amount, form.dueDate, form.status],
  );

  const effectiveInvoiceStatus = form.status;
  const installmentPlanAmountMismatch = useMemo(() => {
    if (!installmentPlan) return false;
    return Math.abs(parseMoneyInput(form.amount) - installmentPlan.total) > 0.009;
  }, [form.amount, installmentPlan]);

  type BuildInvoicePayloadOptions = {
    statusOverride?: PaymentStatus;
    planOverride?: InstallmentPlanInput | null;
    dueDateOverride?: string | null;
    replaceInstallmentPlan?: boolean;
  };

  const buildInvoicePayload = useCallback((
    options: BuildInvoicePayloadOptions = {},
  ): InvoiceInput => {
    const { statusOverride, planOverride, dueDateOverride, replaceInstallmentPlan } = options;
    const safeAmount = parseMoneyInput(form.amount);
    const clientCaseIds = selectedClientCaseIds.filter((id) => Number.isFinite(id) && id > 0);
    const actIds = selectedActs.map((item) => item.id).filter((id) => Number.isFinite(id) && id > 0);
    const effectiveInstallmentPlan = planOverride === undefined ? installmentPlan : planOverride;
    const effectiveDueDate = dueDateOverride === undefined ? form.dueDate : (dueDateOverride ?? '');
    const shouldReplaceInstallmentPlan =
      replaceInstallmentPlan
      ?? (mode === 'create'
        ? Boolean(effectiveInstallmentPlan)
        : installmentPlanTouched);

    return {
      number: form.number.trim() || String(invoice?.number ?? ''),
      date: fromInputToApiDate(form.date) ?? form.date,
      dueDate: effectiveDueDate ? fromInputToApiDate(effectiveDueDate) ?? effectiveDueDate : undefined,
      amount: safeAmount,
      type: form.type,
      status: statusOverride ?? form.status,
      clientId: Number(form.clientId),
      expenseClientId: form.type === 'Expense' && form.expenseClientId ? Number(form.expenseClientId) : null,
      contractId: form.contractId ? Number(form.contractId) : undefined,
      clientCaseId: clientCaseIds[0] ?? null,
      clientCaseIds,
      clientCaseReference: form.clientCaseReference.trim() ? form.clientCaseReference.trim() : undefined,
      description: form.description.trim() ? form.description.trim() : undefined,
      isNotForAccounting: form.isNotForAccounting,
      actReference: form.actReference.trim() ? form.actReference.trim() : undefined,
      actId: actIds[0] ?? null,
      actIds,
      paidDate: form.paidDate ? fromInputToApiDate(form.paidDate) ?? form.paidDate : undefined,
      responsibleIds: form.responsibleIds.slice(0, 1).map((id) => Number(id)).filter((id) => Number.isFinite(id) && id > 0),
      lineItems: lineItems
        .map((item) => ({
          serviceId: item.serviceId ? Number(item.serviceId) : undefined,
          name: item.name.trim(),
          quantity: parseMoneyInput(item.quantity),
          unit: item.unit.trim() || 'шт',
          price: parseMoneyInput(item.price),
        }))
        .filter((item) => item.name && Number.isFinite(item.quantity) && Number.isFinite(item.price) && item.quantity >= 0 && item.price >= 0),
      replaceInstallmentPlan: shouldReplaceInstallmentPlan || undefined,
      installmentPlan: shouldReplaceInstallmentPlan
        ? (effectiveInstallmentPlan ? cloneInstallmentPlan(effectiveInstallmentPlan) : null)
        : undefined,
    };
  }, [form, installmentPlan, installmentPlanTouched, invoice?.number, lineItems, mode, selectedActs, selectedClientCaseIds]);

  const resolveInstallmentDueDateForSave = useCallback(async (
    planOverride?: InstallmentPlanInput | null,
  ): Promise<{ cancelled: boolean; dueDateOverride?: string }> => {
    const effectivePlan = planOverride === undefined ? installmentPlan : planOverride;
    const expectedDueDate = getInstallmentPlanLastDate(effectivePlan);

    if (!expectedDueDate) {
      return { cancelled: false };
    }

    const currentDueDate = toDateInputValue(form.dueDate);
    if (currentDueDate === expectedDueDate) {
      return { cancelled: false, dueDateOverride: expectedDueDate };
    }

    const currentDueDateLabel = formatDate(currentDueDate) || 'не указан';
    const expectedDueDateLabel = formatDate(expectedDueDate);
    const accepted = await confirm({
      title: 'Срок оплаты по рассрочке',
      description:
        `Срок оплаты счета (${currentDueDateLabel}) не совпадает с последней датой платежа по рассрочке (${expectedDueDateLabel}). `
        + `Установить срок оплаты ${expectedDueDateLabel} и продолжить сохранение?`,
      confirmLabel: 'Установить и сохранить',
      cancelLabel: 'Отмена',
    });

    if (!accepted) {
      return { cancelled: true };
    }

    setForm((prev) => (prev.dueDate === expectedDueDate ? prev : { ...prev, dueDate: expectedDueDate }));
    return { cancelled: false, dueDateOverride: expectedDueDate };
  }, [confirm, form.dueDate, installmentPlan]);

  const buildInvoiceEditorDraft = useCallback((): InvoiceEditorDraftState => ({
    invoiceId: invoice?.id ?? null,
    mode,
    presentation: isPageMode ? 'page' : 'modal',
    pageViewMode,
    form,
    lineItems,
    selectedActs,
    selectedClientCaseIds,
    installmentPlan,
  }), [form, installmentPlan, invoice?.id, isPageMode, lineItems, mode, pageViewMode, selectedActs, selectedClientCaseIds]);

  const handleOpenClientCard = (clientId: number) => {
    storeInvoiceEditorDraft(buildInvoiceEditorDraft());
    onOpenClient?.(clientId);
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    const number = form.number.trim();
    if (!number) {
      setLocalError(t('invoiceNumber') ?? 'Номер счёта обязателен');
      return;
    }
    if (!form.date) {
      setLocalError(t('invoiceDate') ?? 'Дата обязательна');
      return;
    }
    if (!form.clientId) {
      setLocalError(`Выберите ${counterpartyLabel.toLocaleLowerCase('ru')}`);
      return;
    }

    if (!isIncome && selectedClientCaseIds.length > 0 && !form.expenseClientId) {
      setLocalError('Выберите клиента, к делу которого относится расход');
      return;
    }

    const parsedAmount = parseMoneyInput(form.amount);
    if (Number.isNaN(parsedAmount) || parsedAmount < 0) {
      setLocalError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    if (installmentPlanAmountMismatch) {
      setLocalError('Сумма счета изменилась после настройки рассрочки. Пересчитайте график.');
      return;
    }

    if (selectedClientCaseIds.length > invoiceRules.maxClientCasesPerInvoice) {
      setLocalError(`В счёте можно указать не более ${invoiceRules.maxClientCasesPerInvoice} дела(дел).`);
      return;
    }

    if (selectedActs.length > invoiceRules.maxActsPerInvoice) {
      setLocalError(`В счёте можно указать не более ${invoiceRules.maxActsPerInvoice} акта(ов).`);
      return;
    }

    const dueDateResolution = await resolveInstallmentDueDateForSave();
    if (dueDateResolution.cancelled) {
      return;
    }

    const payload = buildInvoicePayload({ dueDateOverride: dueDateResolution.dueDateOverride });
    payload.number = number;

    try {
      await onSubmit(payload);
    } catch (err) {
      if (!errorMessage) {
        setLocalError(
          extractDisplayErrorMessage(
            err,
            mode === 'edit' ? 'Не удалось сохранить счет' : 'Не удалось создать счет',
          ),
        );
      }
    }
  };

  const handleOpenInstallmentCalculator = () => {
    if (form.type !== 'Income') return;
    setInstallmentModalOpen(true);
  };

  const handleApplyInstallmentPlan = async (plan: InstallmentPlanInput | null) => {
    const nextPlan = cloneInstallmentPlan(plan);
    const nextDueDate = getInstallmentPlanLastDate(nextPlan);

    setInstallmentPlan(nextPlan);
    setInstallmentPlanTouched(true);
    setLocalError(null);
    if (nextDueDate) {
      setForm((prev) => (prev.dueDate === nextDueDate ? prev : { ...prev, dueDate: nextDueDate }));
    }

    if (mode === 'edit' && invoice?.id) {
      try {
        const payload = buildInvoicePayload({
          planOverride: nextPlan,
          dueDateOverride: nextDueDate || undefined,
          replaceInstallmentPlan: true,
        });
        await apiService.updateInvoice(invoice.id, payload);
        await onRefreshRequested?.();
        setInstallmentModalOpen(false);
        if (nextPlan) {
          toast.success(`График рассрочки сохранен: ${nextPlan.items.length} платежей.`);
        } else {
          toast.success('График рассрочки удален.');
        }
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Не удалось сохранить график рассрочки.');
        setLocalError(message);
        toast.error(message);
      }
      return;
    }

    setInstallmentModalOpen(false);

    if (nextPlan) {
      toast.success(
        mode === 'create'
          ? `График сохранен в форме счета: ${nextPlan.items.length} платежей. Чтобы создать платежи, сохраните счет.`
          : `График рассрочки сохранен в счете: ${nextPlan.items.length} платежей.`,
      );
    } else {
      toast.success(mode === 'create' ? 'График удален из формы счета.' : 'График рассрочки обновлен.');
    }
  };

  const invoiceLink = useMemo(() => {
    if (typeof window === 'undefined') return '';
    const invoiceId = invoice?.id ? `/${invoice.id}` : '';
    return `${window.location.origin}/invoices${invoiceId}`;
  }, [invoice?.id]);

  const downloadBlob = (filename: string, blob: Blob) => {
    if (typeof window === 'undefined') return;
    const href = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = href;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(href);
  };

  const openPendingDocumentWindow = () => {
    if (typeof window === 'undefined') return;
    const previewWindow = window.open('', '_blank', 'width=1180,height=900');
    if (!previewWindow) {
      throw new Error('Браузер заблокировал окно PDF. Разрешите всплывающие окна и повторите.');
    }

    previewWindow.document.open();
    previewWindow.document.write(`
      <!doctype html>
      <html lang="ru">
        <head>
          <meta charset="utf-8" />
          <title>Подготавливаем PDF…</title>
          <style>
            body {
              margin: 0;
              font-family: Arial, sans-serif;
              background: #f8fafc;
              color: #0f172a;
              display: flex;
              align-items: center;
              justify-content: center;
              min-height: 100vh;
            }
            .box {
              padding: 24px 28px;
              border: 1px solid #cbd5e1;
              border-radius: 16px;
              background: #ffffff;
              box-shadow: 0 10px 25px rgba(15, 23, 42, 0.08);
              font-size: 15px;
            }
          </style>
        </head>
        <body>
          <div class="box">Подготавливаем PDF-документ…</div>
        </body>
      </html>
    `);
    previewWindow.document.close();

    return previewWindow;
  };

  const buildAutoPrintHtml = (html: string) => html;

  const openHtmlInWindow = (html: string, previewWindow?: Window | null) => {
    if (!previewWindow || previewWindow.closed) {
      throw new Error('Окно печати было закрыто до загрузки документа. Повторите ещё раз.');
    }

    previewWindow.document.open();
    previewWindow.document.write(buildAutoPrintHtml(html));
    previewWindow.document.close();
  };
  void openHtmlInWindow;

  const openBlobInWindow = (
    blob: Blob,
    previewWindow?: Window | null,
    options?: { autoPrint?: boolean },
  ) => {
    if (typeof window === 'undefined') return;
    const href = URL.createObjectURL(blob);
    let targetWindow = previewWindow && !previewWindow.closed
      ? previewWindow
      : null;

    if (!targetWindow) {
      const nextWindow = window.open('', '_blank', 'width=1180,height=900');
      if (!nextWindow) {
        URL.revokeObjectURL(href);
        throw new Error('Браузер заблокировал окно PDF. Разрешите всплывающие окна и повторите.');
      }

      targetWindow = nextWindow;
    }

    if (options?.autoPrint) {
      targetWindow.onload = () => {
        window.setTimeout(() => {
          try {
            targetWindow?.focus();
            targetWindow?.print();
          } catch (error) {
            console.error('pdf print failed', error);
          } finally {
            if (targetWindow) {
              targetWindow.onload = null;
            }
          }
        }, 250);
      };
    }

    targetWindow.location.href = href;
    window.setTimeout(() => URL.revokeObjectURL(href), 60_000);
  };

  const deferClose = useCallback((callback: () => void) => {
    window.setTimeout(callback, 160);
  }, []);

  const getDocumentActionErrorMessage = useCallback((error: unknown, fallback: string) => {
    if (!(error instanceof Error)) {
      return fallback;
    }

    const message = error.message.trim();
    if (!message) {
      return fallback;
    }

    const separatorIndex = message.lastIndexOf(' - ');
    if (separatorIndex >= 0) {
      const detail = message.slice(separatorIndex + 3).trim();
      if (detail) {
        return detail;
      }
    }

    return message;
  }, []);

  const handlePrintDocument = async () => {
    if (!invoice?.id) {
      toast.error('Сначала сохраните счёт, затем можно печатать документ.');
      return;
    }

    let previewWindow: Window | null | undefined;
    try {
      previewWindow = openPendingDocumentWindow();
      const blob = await apiService.downloadInvoiceDocument(invoice.id, 'pdf', {
        includeSignatureAndSeal: false,
      });
      openBlobInWindow(blob, previewWindow, { autoPrint: true });
    } catch (err) {
      if (previewWindow && !previewWindow.closed) {
        previewWindow.close();
      }
      toast.error(getDocumentActionErrorMessage(err, 'Не удалось сформировать PDF документа.'));
    }
  };

  const handleDownloadDocument = async (
    format: 'word' | 'pdf',
    includeSignatureAndSeal: boolean,
  ) => {
    if (!invoice?.id) {
      toast.error('Сначала сохраните счёт, затем можно скачать документ.');
      return;
    }

    try {
      const blob = await apiService.downloadInvoiceDocument(invoice.id, format, {
        includeSignatureAndSeal,
      });
      const extension = format === 'pdf' ? 'pdf' : 'doc';
      downloadBlob(`invoice-${form.number || 'export'}.${extension}`, blob);
      setDownloadMenuOpen(false);
    } catch (err) {
      toast.error(getDocumentActionErrorMessage(err, 'Не удалось скачать документ.'));
    }
  };


  const handlePageAction = async (action: 'send' | 'copy' | 'paid' | 'cancel' | 'delete') => {
    const actionMeta = {
      send: { title: 'Отправить счёт', message: 'Сформировать документ WORD и отправить на email клиента?', confirmLabel: 'Отправить' },
      copy: { title: 'Копия счёта', message: 'Создать копию текущего счёта?', confirmLabel: 'Копировать' },
      paid: { title: 'Отметить оплаченным', message: 'Отметить счёт как оплаченный?', confirmLabel: 'Оплачен' },
      cancel: { title: 'Отменить счёт', message: 'Подтвердите отмену счёта.', confirmLabel: 'Отменить' },
      delete: { title: 'Удалить счёт', message: 'Удалить счёт без возможности восстановления?', confirmLabel: 'Удалить' },
    }[action];

    const accepted = await confirm({
      title: actionMeta.title,
      description: actionMeta.message,
      confirmLabel: actionMeta.confirmLabel,
      cancelLabel: 'Отмена',
      variant: action === 'cancel' || action === 'delete' ? 'danger' : 'primary',
    });
    if (!accepted) return;

    try {
      if (action === 'send') {
        if (!invoice?.id) {
          throw new Error('Сначала сохраните счёт, затем можно отправить документ.');
        }
        await apiService.sendInvoiceByEmail(invoice.id, { format: 'word', email: selectedClient?.email || undefined });
        toast.success(
          selectedClient?.email
            ? `Документ WORD отправлен на ${selectedClient.email}.`
            : 'Документ WORD отправлен клиенту по email.',
          'Счет отправлен',
        );
        return;
      }

      if (action === 'copy') {
        const payload = buildInvoicePayload({
          replaceInstallmentPlan: Boolean(installmentPlan ?? relatedInstallmentPlan),
        });
        payload.number = `${form.number || 'Счёт'} (копия)`;
        await apiService.createInvoice(payload);
        await onRefreshRequested?.();
        toast.success('Создана копия текущего счета.', 'Копия создана');
        return;
      }

      if ((action === 'paid' || action === 'cancel') && invoice?.id) {
        const nextStatus: PaymentStatus = action === 'paid' ? 'Completed' : 'Cancelled';
        const payload = buildInvoicePayload({ statusOverride: nextStatus });
        await apiService.updateInvoice(invoice.id, payload);
        setForm((prev) => ({ ...prev, status: nextStatus }));
        setDisplayPayments((prev) => prev.map((payment) => (
          payment.documentKind === 'Invoice'
            ? {
                ...payment,
                status: nextStatus,
                paidDate: nextStatus === 'Completed'
                  ? (form.paidDate || payment.paidDate || formatLocalYMD(new Date()))
                  : undefined,
              }
            : payment
        )));
        await onRefreshRequested?.();
        toast.success(
          action === 'paid' ? 'Статус счета обновлен: оплачен.' : 'Статус счета обновлен: отменен.',
          'Счет обновлен',
        );
        return;
      }

      if (action === 'delete' && invoice?.id) {
        await apiService.deleteInvoice(invoice.id);
        await onRefreshRequested?.();
        toast.success('Счет удален.');
        deferClose(onClose);
        return;
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Не удалось выполнить действие по счету.');
    }
  };

  const amountValue = parseMoneyInput(form.amount);
  const lineItemsCount = lineItems.filter((item) => item.name.trim()).length;
  const printableAmount = formatCurrencySmart(amountValue).full;
  const statusMeta: Record<PaymentStatus, { label: string; className: string }> = {
    Pending: { label: 'Создан', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Processing: { label: 'В работе', className: 'bg-blue-100 text-blue-700 border border-blue-200' },
    Completed: { label: 'Оплачен', className: 'bg-emerald-100 text-emerald-700 border border-emerald-200' },
    Cancelled: { label: 'Отменен', className: 'bg-rose-100 text-rose-700 border border-rose-200' },
    Overdue: { label: 'Просрочен', className: 'bg-amber-100 text-amber-700 border border-amber-200' },
  };
  const relatedPaymentStatusMeta: Record<PaymentStatus, string> = {
    Pending: 'bg-slate-100 text-slate-700',
    Processing: 'bg-blue-100 text-blue-700',
    Completed: 'bg-emerald-100 text-emerald-700',
    Cancelled: 'bg-rose-100 text-rose-700',
    Overdue: 'bg-amber-100 text-amber-700',
  };
  const relatedPaymentStatusLabel: Record<PaymentStatus, string> = {
    Pending: t('pending') ?? 'Ожидается',
    Processing: t('processingStatus') ?? 'В обработке',
    Completed: t('completedStatus') ?? 'Оплачен',
    Cancelled: t('cancelledStatus') ?? 'Отменён',
    Overdue: t('overdue') ?? 'Просрочен',
  };
  const invoiceStatusMeta: Record<PaymentStatus, { label: string; className: string }> = {
    Pending: { label: 'Не оплачен', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Processing: { label: 'Частично оплачен', className: 'bg-blue-100 text-blue-700 border border-blue-200' },
    Completed: { label: 'Оплачен', className: 'bg-emerald-100 text-emerald-700 border border-emerald-200' },
    Cancelled: { label: 'Отменен', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Overdue: { label: 'Просрочен', className: 'bg-rose-100 text-rose-700 border border-rose-200' },
  };
  const invoicePaymentStatusLabel: Record<PaymentStatus, string> = {
    Pending: 'Не оплачен',
    Processing: 'Частично оплачен',
    Completed: 'Оплачен',
    Cancelled: t('cancelledStatus') ?? 'Отменён',
    Overdue: t('overdue') ?? 'Просрочен',
  };
  const currentStatusMeta = invoiceStatusMeta[effectiveInvoiceStatus] ?? invoiceStatusMeta.Pending;
  void statusMeta;
  void relatedPaymentStatusLabel;
  const canManageRelatedPayments = mode === 'edit' && !!invoice?.invoiceEntityId;
  const canShowRelatedPaymentActions = mode === 'edit' && !!onAddRelatedPayment;
  const createCaseStub = useMemo<ClientCase>(() => ({
    id: 0,
    clientId: caseOwnerClientId ? Number(caseOwnerClientId) : 0,
    title: '',
    description: '',
    status: 'Open',
    createdAt: new Date().toISOString(),
  }), [caseOwnerClientId]);
  const statusHistorySection = (
    <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50/70 p-4">
      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Статус счета</p>
          <span
            className="inline-flex h-6 min-w-6 items-center justify-center rounded-full bg-slate-200 px-2 text-xs font-semibold text-slate-700"
            title="Количество связанных платежей"
          >
            {statusSnapshot.sourcePayments.length}
          </span>
        </div>
        <div className="flex justify-center">
          <span className={`inline-flex min-w-[180px] items-center justify-center rounded-full px-4 py-2 text-sm font-semibold ${currentStatusMeta.className}`}>
            {currentStatusMeta.label}
          </span>
        </div>
        <p className="text-center text-sm text-slate-600">{statusSnapshot.reason}</p>
        <div className="grid grid-cols-2 gap-3">
          <div className="rounded-xl border border-slate-200 bg-white px-3 py-3 text-center">
            <div className="text-[11px] uppercase tracking-wide text-slate-500">Оплачено</div>
            <div className="mt-1 font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.paidAmount).full}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white px-3 py-3 text-center">
            <div className="text-[11px] uppercase tracking-wide text-slate-500">Остаток</div>
            <div className="mt-1 font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.outstandingAmount).full}</div>
          </div>
        </div>
      </div>

      <div className="mt-4 border-t border-slate-200 pt-4">
        <div className="mb-3 flex items-center justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Хроника статуса</p>
            <p className="mt-1 text-xs text-slate-500">Изменения собираются по связанным платежам и самому счету.</p>
          </div>
          <span
            className="inline-flex h-6 min-w-6 items-center justify-center rounded-full bg-slate-200 px-2 text-xs font-semibold text-slate-700"
            title="Количество записей в хронике"
          >
            {statusSnapshot.timeline.length}
          </span>
        </div>

        <div className="max-h-72 space-y-3 overflow-y-auto pr-1">
          {statusSnapshot.timeline.map((entry) => (
            <div
              key={entry.key}
              className={`rounded-xl border px-3 py-3 ${
                entry.isCurrent
                  ? 'border-blue-200 bg-blue-50/80'
                  : 'border-slate-200 bg-white'
              }`}
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-slate-900">{entry.title}</p>
                  <p className="mt-1 text-sm text-slate-600">{entry.description}</p>
                </div>
                <span className="whitespace-nowrap text-xs text-slate-400">{entry.timestampLabel}</span>
              </div>
              <p className="mt-2 text-xs text-slate-500">{entry.sourceLabel}</p>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
  const auditInfoSection = mode === 'edit' && invoice ? (
    <ObjectAuditLauncher
      objectType="payment"
      objectId={invoice.id}
      createdAt={invoice.createdAt}
      createdByUserName={invoice.auditCreatedByUserName}
      updatedAt={invoice.auditUpdatedAt}
      updatedByUserName={invoice.auditUpdatedByUserName}
      isDeleted={invoice.auditIsDeleted}
      deletedAt={invoice.auditDeletedAt}
      deletedByUserName={invoice.auditDeletedByUserName}
      triggerTitle="Аудит счета"
      triggerDescription="Паспорт счета и история изменений открываются в отдельном окне."
      title="История счета"
      subtitle="Здесь собраны базовые audit-данные и все изменения по выбранному счету."
    />
  ) : null;
  const documentsSection = (
    <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
      <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
        <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
          {t('invoiceDocumentsTitle') ?? 'Связанные документы'}
        </span>
      </header>
      <div className="mt-4 grid grid-cols-1 gap-5 lg:grid-cols-2">
        <div className="space-y-3 text-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <span className="font-medium text-slate-700">{t('cases') ?? 'Дела'}</span>
              <p className="mt-1 text-xs text-slate-500">
                Выберите дело клиента, к которому относится счёт.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                title="Выбрать дело"
                aria-label="Выбрать дело"
                onClick={() => {
                  setCaseDropdownOpen((prev) => !prev);
                  setActDropdownOpen(false);
                }}
                disabled={!caseOwnerClientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Создать дело"
                aria-label="Создать дело"
                onClick={() => setCreateCaseOpen(true)}
                disabled={!caseOwnerClientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!caseOwnerClientId ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                {isIncome ? 'Сначала выберите клиента.' : 'Сначала выберите клиента, к которому относится расход.'}
              </div>
            ) : selectedClientCaseIds.length === 0 ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Дела еще не добавлены в счет.
              </div>
            ) : (
              <div className="space-y-2">
                {selectedClientCaseIds.map((id) => {
                  const selectedCase = clientCaseOptions.find((item) => item.id === id) ?? null;
                  return (
                    <div
                      key={id}
                      className="flex items-start justify-between gap-3 rounded-xl border border-blue-200 bg-white px-3 py-2"
                    >
                      <div className="min-w-0">
                        <div className="truncate text-sm font-medium text-slate-900">
                          {clientCaseLabel(selectedCase ?? { id, clientId: selectedCaseOwnerClient?.id ?? 0, title: '', status: '', createdAt: '' })}
                        </div>
                        <div className="mt-1 text-xs text-slate-500">{clientCaseStatusLabel(selectedCase?.status)}</div>
                      </div>
                      <button
                        type="button"
                        onClick={() => removeSelectedClientCase(id)}
                        className="rounded-full p-1 text-slate-500 transition hover:bg-slate-100"
                        aria-label={t('clear') ?? 'Очистить'}
                      >
                        <X className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          {caseDropdownOpen && (
            <div className="rounded-xl border border-slate-200 bg-white shadow-sm">
              <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Выберите дело</span>
                <button
                  type="button"
                  onClick={() => setCaseDropdownOpen(false)}
                  className="rounded-full p-1 text-slate-500 hover:bg-slate-100"
                  aria-label={t('clear') ?? 'Очистить'}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="max-h-56 overflow-auto p-2">
                {clientCasesLoading ? (
                  <div className="flex items-center gap-2 px-2 py-3 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : clientCasesError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-600">{clientCasesError}</div>
                ) : clientCaseOptions.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center text-xs text-slate-500">
                    У выбранного клиента пока нет дел.
                  </div>
                ) : (
                  <div className="space-y-2">
                    {clientCaseOptions.map((clientCase) => {
                      const checked = selectedClientCaseIds.includes(clientCase.id);
                      return (
                        <button
                          key={clientCase.id}
                          type="button"
                          onClick={() => toggleClientCase(clientCase.id)}
                          className={`flex w-full items-start gap-3 rounded-xl border px-3 py-2 text-left transition ${
                            checked ? 'border-blue-200 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'
                          }`}
                        >
                          <span className={`mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-md border ${
                            checked ? 'border-blue-500 bg-blue-500 text-white' : 'border-slate-300 bg-white text-transparent'
                          }`}>
                            <Check className="h-3.5 w-3.5" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block text-sm font-medium text-slate-900">{clientCaseLabel(clientCase)}</span>
                            <span className="mt-1 block text-xs text-slate-500">{clientCaseStatusLabel(clientCase.status)}</span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">Комментарий по делу</span>
            <textarea
              value={form.clientCaseReference}
              onChange={handleChange('clientCaseReference')}
              rows={2}
              placeholder="Например: дело ожидает подтверждения документов"
              className="min-h-[96px] resize-y rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>
        </div>

        <div className="space-y-3 text-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <span className="font-medium text-slate-700">{t('acts') ?? 'Акты'}</span>
              <p className="mt-1 text-xs text-slate-500">
                Выберите акт {isIncome ? 'клиента' : 'поставщика'} из списка или создайте новый.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                title="Выбрать акт"
                aria-label="Выбрать акт"
                onClick={() => {
                  setActDropdownOpen((prev) => !prev);
                  setCaseDropdownOpen(false);
                }}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Создать акт"
                aria-label="Создать акт"
                onClick={() => {
                  setDraftAct((prev) => ({
                    ...prev,
                    date: form.date || prev.date,
                    amount: form.amount || prev.amount,
                  }));
                  setCreateActError(null);
                  setCreateActOpen(true);
                }}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!form.clientId ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Сначала выберите {counterpartyLabel.toLocaleLowerCase('ru')}.
              </div>
            ) : selectedActs.length === 0 ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Акты еще не добавлены в счет.
              </div>
            ) : (
              <div className="space-y-2">
                {selectedActs.map((act) => (
                  <div
                    key={act.id}
                    className="flex items-start justify-between gap-3 rounded-xl border border-emerald-200 bg-white px-3 py-2"
                  >
                    <div className="min-w-0">
                      <div className="truncate text-sm font-medium text-slate-900">{actLabel(act)}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => removeSelectedAct(act.id)}
                      className="rounded-full p-1 text-slate-500 transition hover:bg-slate-100"
                      aria-label={t('clear') ?? 'Очистить'}
                    >
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {actDropdownOpen && (
            <div className="rounded-xl border border-slate-200 bg-white shadow-sm">
              <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Выберите акт</span>
                <button
                  type="button"
                  onClick={() => setActDropdownOpen(false)}
                  className="rounded-full p-1 text-slate-500 hover:bg-slate-100"
                  aria-label={t('clear') ?? 'Очистить'}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="max-h-56 overflow-auto p-2">
                {actsLoading ? (
                  <div className="flex items-center gap-2 px-2 py-3 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : actsError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-600">{actsError}</div>
                ) : actOptions.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center text-xs text-slate-500">
                    У выбранного {counterpartyLabel.toLocaleLowerCase('ru')} пока нет актов.
                  </div>
                ) : (
                  <div className="space-y-2">
                    {actOptions.map((act) => {
                      const checked = selectedActs.some((item) => item.id === act.id);
                      return (
                        <button
                          key={act.id}
                          type="button"
                          onClick={() => toggleAct(act)}
                          className={`flex w-full items-start gap-3 rounded-xl border px-3 py-2 text-left transition ${
                            checked ? 'border-emerald-200 bg-emerald-50' : 'border-slate-200 bg-white hover:bg-slate-50'
                          }`}
                        >
                          <span className={`mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-md border ${
                            checked ? 'border-emerald-500 bg-emerald-500 text-white' : 'border-slate-300 bg-white text-transparent'
                          }`}>
                            <Check className="h-3.5 w-3.5" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block text-sm font-medium text-slate-900">{actLabel(act)}</span>
                            <span className="mt-1 block text-xs text-slate-500">
                              {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full}
                            </span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoiceManualActLabel') ?? 'Комментарий по акту'}</span>
            <textarea
              value={form.actReference}
              onChange={handleChange('actReference')}
              rows={2}
              placeholder={t('invoiceActManualPlaceholder') ?? 'Например: акт будет подписан позже'}
              className="min-h-[96px] resize-y rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>
        </div>
      </div>
    </section>
  );
  const lineItemLookupQueryTrimmed = lineItemLookupQuery.trim();
  const activeLineItemCategoryLabel = activeLineItemLookup?.serviceCategoryId
    ? serviceCategoryById.get(Number(activeLineItemLookup.serviceCategoryId)) ?? 'Все категории'
    : 'Все категории';
  const activeLineItemSelectedServiceId = activeLineItemLookup
    ? String(resolveLineItemTemplate(activeLineItemLookup)?.id ?? '')
    : '';
  const canApplyCustomServiceName = lineItemLookupState?.kind === 'service'
    && Boolean(lineItemLookupQueryTrimmed)
    && !lineItemServiceLookupOptions.some(
      (option) => normalizeLookupName(option.label) === normalizeLookupName(lineItemLookupQueryTrimmed),
    );

  if (!open) return null;

  const content = (
    <div className={`relative w-full overflow-hidden rounded-2xl bg-white ${isPageMode ? 'max-w-none border border-slate-200 shadow-sm' : 'max-w-[85rem] shadow-2xl'}`}>
      <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
        <div className="flex items-center gap-3">
          {isPageMode && (
            <button
              type="button"
              onClick={onBack ?? onClose}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Назад
            </button>
          )}
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isPageMode && pageViewMode
                ? `Счёт №${form.number || '—'} от ${formatDate(form.date) || '—'}`
                : mode === 'edit'
                ? t('invoiceEdit') ?? 'Редактировать счёт'
                : t('invoiceCreate') ?? 'Создать счёт'}
            </h2>
            <p className="text-sm text-slate-500">
              {isPageMode && pageViewMode
                ? 'Просмотр счёта с действиями и документом для печати/отправки'
                : t('invoiceModalSubtitle') ?? 'Укажите параметры счёта'}
            </p>
          </div>
        </div>
        {isPageMode && mode === 'edit' && (
          <div className="flex items-center gap-2">
            <span className={`rounded-full px-3 py-1 text-sm font-semibold ${currentStatusMeta.className}`}>{currentStatusMeta.label}</span>
            {pageViewMode ? (
              <button
                type="button"
                onClick={() => setPageViewMode(false)}
                className="inline-flex items-center rounded-lg border border-slate-200 bg-white p-2 text-slate-700 shadow-sm hover:bg-slate-50"
                aria-label="Редактировать счёт"
                title="Редактировать счёт"
              >
                <Pencil className="h-4 w-4" />
              </button>
            ) : (
              <>
                <button
                  type="submit"
                  form="invoice-edit-form"
                  disabled={submitting}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
                >
                  {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
                  {mode === 'edit' ? 'Сохранить' : t('invoiceCreate') ?? 'Создать счёт'}
                </button>
                <button
                  type="button"
                  onClick={() => setPageViewMode(true)}
                  className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
                >
                  {t('cancel') ?? 'Отмена'}
                </button>
              </>
            )}
          </div>
        )}
        {!isPageMode && (
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      {isPageMode && pageViewMode ? (
        <div className="space-y-4 bg-slate-100 px-4 py-4">
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
            <section className="rounded-xl border border-slate-300 bg-white p-3">
              <div className="mb-3 flex items-center justify-between gap-3 px-1">
                <div>
                  <h3 className="text-sm font-semibold text-slate-900">Печатный шаблон счета</h3>
                </div>
                {documentPreviewLoading ? (
                  <span className="inline-flex items-center gap-2 text-xs text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Загружаем шаблон…
                  </span>
                ) : null}
              </div>

              <div className="overflow-hidden rounded-xl border border-slate-200 bg-slate-50">
                {documentPreviewError ? (
                  <div className="flex min-h-[22rem] items-center justify-center px-6 text-center text-sm text-rose-600">
                    {documentPreviewError}
                  </div>
                ) : documentPreviewHtml ? (
                  <iframe
                    title={`Печатный шаблон счета ${form.number || ''}`.trim()}
                    srcDoc={documentPreviewHtml}
                    sandbox="allow-same-origin"
                    className="h-[72rem] w-full bg-white"
                  />
                ) : (
                  <div className="flex min-h-[22rem] items-center justify-center px-6 text-center text-sm text-slate-500">
                    Подготовка печатного шаблона…
                  </div>
                )}
              </div>
            </section>

            <aside className="space-y-3">
              <div className="rounded-lg border border-slate-300 bg-white p-3">
                <div className="flex items-center justify-between">
                  <span className="text-xl font-semibold">{printableAmount}</span>
                  <span className={`rounded px-3 py-1 text-sm font-semibold ${currentStatusMeta.className}`}>{currentStatusMeta.label}</span>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-2 flex items-center gap-2 font-medium"><Link2 className="h-4 w-4" />Ссылка на счет</div>
                <a href={invoiceLink} className="break-all text-blue-600 underline">{invoiceLink || '—'}</a>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-3 flex items-center gap-2 font-medium">
                  <List className="h-4 w-4" />
                  Договор и оплаты
                </div>
                <div className="space-y-3">
                  <div>
                    <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-500">Договор</div>
                    {form.contractId && contractsLoading && !selectedContract ? (
                      <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Загружаем договор...
                      </div>
                    ) : (selectedContract || form.contractId) ? (
                      <button
                        type="button"
                        onClick={() => { void openContractPreview(form.contractId, selectedContract); }}
                        data-testid="invoice-related-contract-link"
                        className="w-full rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-left transition hover:border-blue-200 hover:bg-blue-50/40"
                      >
                        <div className="font-medium text-blue-700 underline decoration-blue-300 underline-offset-2">
                          {formatContractSidebarLabel(selectedContract)}
                        </div>
                        {selectedContract?.title?.trim() ? (
                          <div className="mt-1 text-xs text-slate-600">{selectedContract.title}</div>
                        ) : (
                          <div className="mt-1 text-xs text-slate-500">Открыть карточку договора</div>
                        )}
                      </button>
                    ) : (
                      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-3 py-2 text-slate-500">
                        Договор не выбран.
                      </div>
                    )}
                  </div>

                  <div>
                    <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-500">Оплаты</div>
                    {paymentsLoading ? (
                      <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Загружаем оплаты...
                      </div>
                    ) : displayPayments.length === 0 ? (
                      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-3 py-2 text-slate-500">
                        Связанные оплаты пока не добавлены.
                      </div>
                    ) : (
                      <div className="space-y-2">
                        {displayPayments.map((payment) => {
                          const installmentMeta = parseInstallmentPaymentMetadata(payment.systemNotes);
                          const installmentSequenceLabel = installmentMeta
                            ? `${installmentMeta.sequence}/${Math.max(installmentMeta.months, installmentMeta.sequence)}`
                            : null;

                          return (
                            <button
                              key={payment.id}
                              type="button"
                              onClick={() => {
                                if (onEditRelatedPayment) {
                                  onEditRelatedPayment(payment);
                                  return;
                                }
                                void openPaymentPreview(payment);
                              }}
                              title={`Открыть платеж #${payment.id}`}
                              data-testid={`invoice-related-payment-link-${payment.id}`}
                              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-left transition hover:border-blue-200 hover:bg-blue-50/40"
                            >
                              <div className="text-sm font-medium text-blue-700 underline decoration-blue-300 underline-offset-2">
                                {formatRelatedPaymentLinkLabel(payment)}
                              </div>
                              <div className="mt-1 text-xs text-slate-600">
                                {payment.description?.trim() || 'Без описания'}
                              </div>
                              <div className="mt-2 flex flex-wrap items-center gap-2">
                                <span
                                  className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${
                                    relatedPaymentStatusMeta[payment.status] ?? relatedPaymentStatusMeta.Pending
                                  }`}
                                >
                                  {invoicePaymentStatusLabel[payment.status] ?? invoicePaymentStatusLabel.Pending}
                                </span>
                                {installmentMeta ? (
                                  <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] font-semibold text-violet-700">
                                    Рассрочка {installmentSequenceLabel}
                                  </span>
                                ) : null}
                              </div>
                            </button>
                          );
                        })}
                      </div>
                    )}
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm leading-6">
                <p>
                  <span className="text-slate-500">{counterpartyLabel}:</span>{' '}
                  {selectedClient && onOpenClient ? (
                    <button
                      type="button"
                      onClick={() => handleOpenClientCard(selectedClient.id)}
                      className="text-blue-600 underline hover:text-blue-700"
                    >
                      {selectedClient.name}
                    </button>
                  ) : (
                    selectedClient?.name || '—'
                  )}
                </p>
                {!isIncome && (
                  <p>
                    <span className="text-slate-500">Клиент расхода:</span>{' '}
                    {selectedExpenseClient && onOpenClient ? (
                      <button
                        type="button"
                        onClick={() => handleOpenClientCard(selectedExpenseClient.id)}
                        className="text-blue-600 underline hover:text-blue-700"
                      >
                        {selectedExpenseClient.name}
                      </button>
                    ) : (
                      selectedExpenseClient?.name || '—'
                    )}
                  </p>
                )}
                <p><span className="text-slate-500">{t('cases') ?? 'Дела'}:</span> {selectedClientCasesSummary || '—'}</p>
                <p><span className="text-slate-500">Комментарий по делу:</span> {form.clientCaseReference || '—'}</p>
                <p><span className="text-slate-500">{t('acts') ?? 'Акты'}:</span> {selectedActsSummary || '—'}</p>
                <p><span className="text-slate-500">{t('invoiceManualActLabel') ?? 'Комментарий по акту'}:</span> {form.actReference || '—'}</p>
                <p><span className="text-slate-500">Дата выставления:</span> {formatDate(form.date) || '—'}</p>
                <p><span className="text-slate-500">Срок оплаты:</span> {formatDate(form.dueDate) || '—'}</p>
                <p><span className="text-slate-500">Комментарий для клиента:</span> {form.description || '—'}</p>
                <p><span className="text-slate-500">Не для бухгалтерии:</span> {form.isNotForAccounting ? 'Да' : 'Нет'}</p>
                <p><span className="text-slate-500">Дата оплаты:</span> {formatDate(form.paidDate) || '—'}</p>
                <p><span className="text-slate-500">Позиции:</span> {lineItemsCount}</p>
              </div>

              {auditInfoSection}
            </aside>
          </div>

          <div className="grid grid-cols-2 gap-2 rounded-lg border border-slate-300 bg-white p-2 md:grid-cols-4 xl:grid-cols-7">
            <button type="button" onClick={() => void handlePageAction('send')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Send className="h-4 w-4" />Отправить</button>
            <button type="button" onClick={() => void handlePrintDocument()} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Printer className="h-4 w-4" />Печать</button>
            <div className="relative" ref={downloadMenuRef}>
              <button
                type="button"
                onClick={() => setDownloadMenuOpen((prev) => !prev)}
                className="inline-flex w-full items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"
              >
                <Download className="h-4 w-4" />Скачать<ChevronDown className="h-4 w-4" />
              </button>
              {downloadMenuOpen ? (
                <div className="absolute bottom-full left-0 z-20 mb-2 w-full min-w-[20rem] rounded-xl border border-slate-200 bg-white p-1 shadow-lg">
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('pdf', false)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>PDF без подписи и печати</span>
                    <span className="text-xs text-slate-400">.pdf</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('pdf', true)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>PDF с подписью и печатью</span>
                    <span className="text-xs text-slate-400">.pdf</span>
                  </button>
                  <div className="my-1 border-t border-slate-100" />
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('word', false)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>Word без подписи и печати</span>
                    <span className="text-xs text-slate-400">.doc</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('word', true)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>Word с подписью и печатью</span>
                    <span className="text-xs text-slate-400">.doc</span>
                  </button>
                </div>
              ) : null}
            </div>
            <button type="button" onClick={() => void handlePageAction('copy')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Copy className="h-4 w-4" />Копировать</button>
            <button type="button" onClick={() => void handlePageAction('paid')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Check className="h-4 w-4" />Оплачен</button>
            <button type="button" onClick={() => void handlePageAction('cancel')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Ban className="h-4 w-4" />Отменен</button>
            <button type="button" onClick={() => void handlePageAction('delete')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Trash2 className="h-4 w-4" />Удалить</button>
          </div>
        </div>
      ) : (
      <form id="invoice-edit-form" onSubmit={handleSubmit} className={`${isPageMode ? 'px-6 py-5' : 'max-h-[75vh] overflow-y-auto px-6 py-5'} space-y-6`}>
          <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_420px] xl:items-start">
            <div className="space-y-6">
              <section className="rounded-2xl border border-slate-200 bg-slate-50/50 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {t('invoiceMainDetailsTitle') ?? 'Основные параметры'}
                            </span>
              
                          </header>

                          <div className="mt-4 space-y-4">
                            <div className="flex flex-col gap-2">
                              <div className="flex flex-wrap items-center justify-between gap-3">
                                <span className="font-medium text-slate-700">{t('invoiceType') ?? 'Тип счёта'}</span>
                                {lockType && mode === 'edit' && (
                                  <span className="text-xs text-slate-500">
                                    {t('invoiceTypeEditHint') ?? 'Можно изменить тип, если счёт попал не в ту группу.'}
                                  </span>
                                )}
                              </div>
                              <div>
                                {isTypeLocked ? (
                                  <span
                                    className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${
                                      typeBadgeClasses[form.type]
                                    }`}
                                  >
                                    {form.type === 'Income'
                                      ? t('invoiceTypeIncome') ?? 'Доходные счета'
                                      : t('invoiceTypeExpense') ?? 'Расходные счета'}
                                  </span>
                                ) : (
                                  <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-slate-200 bg-white/80 p-1">
                                    {typeOptions.map((option) => {
                                      const active = option === form.type;
                                      return (
                                        <button
                                          key={option}
                                          type="button"
                                          onClick={() => handleTypeSelect(option)}
                                          className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${
                                            active
                                              ? 'bg-blue-600 text-white shadow'
                                              : 'bg-transparent text-slate-600 hover:bg-slate-100'
                                          }`}
                                        >
                                          {option === 'Income'
                                            ? t('invoiceTypeIncomeShort') ?? t('invoiceTypeIncome') ?? 'Доходные'
                                            : t('invoiceTypeExpenseShort') ?? t('invoiceTypeExpense') ?? 'Расходные'}
                                        </button>
                                      );
                                    })}
                                  </div>
                                )}
                              </div>
                            </div>

                            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center font-medium text-slate-700">{t('invoiceNumber') ?? 'Номер счёта'}</span>
                                <input
                                  type="text"
                                  value={form.number}
                                  onChange={handleChange('number')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  required
                                />
                              </label>
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center justify-between gap-2 font-medium text-slate-700">
                                  {t('invoiceAmount') ?? 'Сумма'}
                                  {isIncome && (
                                    <button
                                      type="button"
                                      onClick={handleOpenInstallmentCalculator}
                                      className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-200"
                                    >
                                      {installmentPlan ? 'Изменить рассрочку' : (t('invoiceInstallmentButton') ?? 'Рассрочка')}
                                    </button>
                                  )}
                                </span>
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  value={formatMoneyDisplayValue(form.amount)}
                                  readOnly
                                  className="rounded-lg border border-slate-300 bg-slate-50 px-3 py-2 text-right text-sm tabular-nums text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  placeholder="0.00"
                                />
                                {isIncome && installmentPlan ? (
                                  <span className={`text-xs ${installmentPlanAmountMismatch ? 'text-amber-600' : 'text-slate-500'}`}>
                                    {installmentPlanAmountMismatch
                                      ? 'Сумма изменилась, пересчитайте рассрочку.'
                                      : `Настроено платежей: ${installmentPlan.items.length}, к оплате ${formatCurrencySmart(installmentPlan.toPay).short}.`}
                                  </span>
                                ) : null}
                              </label>
                            </div>

                            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center font-medium text-slate-700">{t('invoiceStatus') ?? 'Статус'}</span>
                                <select
                                  value={form.status}
                                  onChange={handleChange('status')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                >
                                  <option value="Pending">{t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается'}</option>
                                  <option value="Completed">{t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачен'}</option>
                                  <option value="Overdue">{t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочен'}</option>
                                  <option value="Processing">{t('processingStatus') ?? 'В обработке'}</option>
                                  <option value="Cancelled">{t('cancelledStatus') ?? 'Отменён'}</option>
                                </select>
                              </label>

                              <LookupSelect
                                label={t('invoiceResponsible') ?? 'Ответственный'}
                                value={selectedResponsibleId}
                                onChange={handleResponsibleChange}
                                options={responsibleOptions}
                                placeholder={t('actResponsibleNotSelected') ?? 'Не указан'}
                                title="Выбор ответственного"
                                searchPlaceholder="Поиск ответственного"
                                emptyOptionLabel={t('actResponsibleNotSelected') ?? 'Не указан'}
                                helperText={
                                  selectedClient?.responsibleUserName
                                    ? `По клиенту: ${selectedClient.responsibleUserName}`
                                    : undefined
                                }
                                disabled={lookupsLoading}
                                buttonClassName="bg-white"
                              />
                            </div>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">Комментарий для клиента</span>
                              <textarea
                                value={form.description}
                                onChange={handleChange('description')}
                                rows={3}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                placeholder="Этот комментарий будет напечатан ниже таблицы счета."
                              />
                            </label>

                            <label className="inline-flex items-start gap-3 rounded-xl border border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-700">
                              <input
                                type="checkbox"
                                checked={form.isNotForAccounting}
                                onChange={handleBooleanChange('isNotForAccounting')}
                                className="mt-0.5 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                              />
                              <span>
                                <span className="block font-medium text-slate-800">Счет не для бухгалтерии</span>
                                <span className="mt-1 block text-xs text-slate-500">
                                  В печатной форме появится отдельная пометка для клиента.
                                </span>
                              </span>
                            </label>
                          </div>
                        </section>

              {documentsSection}

              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                          <header className="mb-3 flex items-center justify-between">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Позиции счета</span>
                            <button
                              type="button"
                              onClick={addLineItem}
                              className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50"
                            >
                              + Добавить
                            </button>
                          </header>
                          <div className="-mx-1 overflow-x-auto px-1">
                            <div className={`space-y-3 ${LINE_ITEM_GRID_MIN_WIDTH_CLASS}`}>
                              <div className={`hidden items-center gap-3 px-1 text-xs font-semibold uppercase tracking-wide text-slate-500 md:grid ${LINE_ITEM_GRID_TEMPLATE}`}>
                                <span>Категория</span>
                                <span>Наименование</span>
                                <span className="text-center">Кол-во</span>
                                <span>Ед.</span>
                                <span className="text-right">Цена</span>
                                <span className="text-right">Сумма</span>
                                <span className="sr-only">Действия</span>
                              </div>
                              {lineItems.map((item) => {
                                const lineItemTotal = calculateLineItemAmountValue(item.quantity, item.price);
                                const categoryLabel = item.serviceCategoryId
                                  ? serviceCategoryById.get(Number(item.serviceCategoryId)) ?? 'Категория не найдена'
                                  : 'Все категории';

                                return (
                                  <div
                                    key={item.id}
                                    className={`grid gap-3 rounded-2xl border border-slate-200 bg-slate-50/80 p-3 md:items-start md:rounded-none md:border-0 md:bg-transparent md:p-0 ${LINE_ITEM_GRID_TEMPLATE}`}
                                  >
                                    <div className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Категория</span>
                                      <button
                                        type="button"
                                        onClick={() => openLineItemLookup({ kind: 'category', lineItemId: item.id })}
                                        aria-label="Категория услуги"
                                        title={categoryLabel}
                                        className="flex min-h-10 w-full items-center justify-between gap-3 rounded-lg border border-slate-300 bg-white px-3 py-2 text-left text-sm text-slate-700 transition hover:border-blue-300 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-100"
                                      >
                                        <span className="min-w-0 flex-1 truncate">{categoryLabel}</span>
                                        <ChevronDown className="h-4 w-4 shrink-0 text-slate-400" />
                                      </button>
                                    </div>

                                    <div className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Наименование</span>
                                      <button
                                        type="button"
                                        data-testid="invoice-line-item-service-trigger"
                                        onClick={() => openLineItemLookup({ kind: 'service', lineItemId: item.id })}
                                        aria-label="Наименование услуги"
                                        title={item.name || 'Выберите или введите наименование'}
                                        className="flex min-h-10 w-full items-center justify-between gap-3 rounded-lg border border-slate-300 bg-white px-3 py-2 text-left text-sm transition hover:border-blue-300 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-100"
                                      >
                                        <span className={`min-w-0 flex-1 truncate ${item.name.trim() ? 'text-slate-700' : 'text-slate-400'}`}>
                                          {item.name.trim() || 'Наименование'}
                                        </span>
                                        <ChevronDown className="h-4 w-4 shrink-0 text-slate-400" />
                                      </button>
                                    </div>

                                    <label className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Кол-во</span>
                                      <input
                                        type="text"
                                        inputMode="decimal"
                                        value={item.quantity}
                                        onChange={(event) => updateLineItem(item.id, { quantity: event.target.value })}
                                        placeholder="Кол-во"
                                        aria-label="Количество"
                                        className="w-full rounded-lg border border-slate-300 px-2 py-2 text-center text-sm"
                                      />
                                    </label>

                                    <label className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Ед.</span>
                                      <input
                                        type="text"
                                        data-testid="invoice-line-item-unit-input"
                                        list="invoice-line-item-units"
                                        value={item.unit}
                                        onChange={(event) => updateLineItem(item.id, { unit: event.target.value })}
                                        placeholder="Ед."
                                        aria-label="Единица измерения"
                                        className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm"
                                      />
                                    </label>

                                    <label className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Цена</span>
                                      <input
                                        type="text"
                                        data-testid="invoice-line-item-price-input"
                                        inputMode="decimal"
                                        value={item.price}
                                        onChange={(event) => handleLineItemPriceChange(item.id, event.target.value)}
                                        onBlur={(event) => handleLineItemPriceBlur(item.id, event.target.value)}
                                        placeholder="0.00"
                                        aria-label="Цена"
                                        className="w-full rounded-lg border border-slate-300 px-3 py-2 text-right text-sm tabular-nums"
                                      />
                                    </label>

                                    <div className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Сумма</span>
                                      <output
                                        aria-label="Сумма позиции"
                                        className="inline-flex min-h-10 w-full items-center justify-end rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-right text-sm font-medium tabular-nums text-slate-700"
                                      >
                                        {lineItemTotal}
                                      </output>
                                    </div>

                                    <div className="flex items-end justify-end md:justify-start">
                                      <button
                                        type="button"
                                        onClick={() => { void removeLineItem(item.id); }}
                                        className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-rose-200 bg-white text-rose-500 transition hover:border-rose-300 hover:bg-rose-50 hover:text-rose-700"
                                        aria-label="Удалить"
                                        title="Удалить позицию"
                                      >
                                        <X className="h-4 w-4" />
                                      </button>
                                    </div>
                                  </div>
                                );
                              })}
                            </div>
                          </div>
                          <datalist id="invoice-line-item-units">
                            {unitOptions.map((unit) => (
                              <option key={unit} value={unit} />
                            ))}
                          </datalist>
                          <div className="mt-3 text-right text-sm font-semibold text-slate-700">Итого: {formatMoneyDisplayValue(form.amount)}</div>
                        </section>

              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                <header className="mb-3 flex items-center justify-between gap-3">
                  <div>
                    <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Платежи по счёту
                    </span>
                    <p className="mt-1 text-sm text-slate-500">
                      Здесь отображаются связанные календарные платежи и оплаты по текущему счёту.
                    </p>
                  </div>
                  {canShowRelatedPaymentActions && (
                    <button
                      type="button"
                      onClick={onAddRelatedPayment}
                      className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-sm font-semibold text-blue-700 transition hover:bg-blue-100"
                    >
                      <Plus className="h-4 w-4" />
                      Добавить платеж
                    </button>
                  )}
                </header>

                {!canManageRelatedPayments ? (
                  <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    Сначала сохраните счёт, после этого здесь можно будет создавать и редактировать связанные платежи.
                  </div>
                ) : paymentsLoading ? (
                  <div className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : displayPayments.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    Связанные платежи пока не добавлены.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {displayPayments.map((payment) => {
                      const installmentMeta = parseInstallmentPaymentMetadata(payment.systemNotes);
                      const installmentSequenceLabel = installmentMeta
                        ? `${installmentMeta.sequence}/${Math.max(installmentMeta.months, installmentMeta.sequence)}`
                        : null;

                      return (
                        <div
                          key={payment.id}
                          className="flex flex-col gap-3 rounded-xl border border-slate-200 px-4 py-3 sm:flex-row sm:items-start sm:justify-between"
                        >
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="text-sm font-semibold text-slate-900">
                              {formatCurrencySmart(payment.amount ?? 0).full}
                            </span>
                            <span
                              className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                                relatedPaymentStatusMeta[payment.status] ?? relatedPaymentStatusMeta.Pending
                              }`}
                            >
                              {invoicePaymentStatusLabel[payment.status] ?? invoicePaymentStatusLabel.Pending}
                            </span>
                            {installmentMeta ? (
                              <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2.5 py-1 text-xs font-semibold text-violet-700">
                                Рассрочка {installmentSequenceLabel}
                              </span>
                            ) : null}
                          </div>
                          <div className="mt-1 text-sm text-slate-600">
                            {payment.description?.trim() || 'Без описания'}
                          </div>
                          <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                            <span>План: {formatDate(payment.plannedDate || payment.date) || '—'}</span>
                            <span>Факт: {formatDate(payment.paidDate || payment.lastPaymentDate) || '—'}</span>
                            {payment.account && <span>Счёт: {payment.account}</span>}
                          </div>
                        </div>
                        {(onEditRelatedPayment || (onDeleteRelatedPayment && !isInvoiceManagedPayment(payment))) && (
                          <div className="flex items-center gap-2 self-end sm:self-start">
                            {onEditRelatedPayment && (
                              <button
                                type="button"
                                onClick={() => onEditRelatedPayment(payment)}
                                className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                                title={t('edit') ?? 'Редактировать'}
                                aria-label={t('edit') ?? 'Редактировать'}
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {onDeleteRelatedPayment && !isInvoiceManagedPayment(payment) && (
                              <button
                                type="button"
                                onClick={() => {
                                  void onDeleteRelatedPayment(payment);
                                }}
                                className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-slate-200 text-red-600 transition hover:bg-red-50"
                                title={t('delete') ?? 'Удалить'}
                                aria-label={t('delete') ?? 'Удалить'}
                              >
                                <Trash2 className="h-4 w-4" />
                              </button>
                            )}
                          </div>
                        )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </section>
            </div>

            <div className="space-y-6">
              <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {counterpartyBlockTitle}
                            </span>
              
                          </header>
                          <div className="mt-4 grid grid-cols-1 gap-4">
                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{counterpartyLabel}</span>
                              <div className="flex gap-2">
                                <input
                                  type="text"
                                  value={selectedClient?.name ?? ''}
                                  readOnly
                                  className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm bg-slate-50"
                                  placeholder={counterpartyPlaceholder}
                                />
                                <button
                                  type="button"
                                  onClick={openClientPicker}
                                  className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                                >
                                  <UserRoundSearch className="h-4 w-4" />
                                </button>
                              </div>
                              {lookupsLoading && (
                                <span className="text-xs text-slate-500">{t('loading') ?? 'Загрузка...'}</span>
                              )}
                              {lookupsError && (
                                <span className="text-xs text-rose-600">{lookupsError}</span>
                              )}
                              {(selectedClient?.clientStatus || selectedClient?.responsibleUserName) && (
                                <div className="mt-2 space-y-1 rounded-xl border border-slate-100 bg-slate-50 p-2">
                                  {selectedClient.clientStatus && (
                                    <ClientStatusBadge status={selectedClient.clientStatus} />
                                  )}
                                  {selectedClient.responsibleUserName && (
                                    <p className="text-xs text-slate-600">
                                      Ответственный: {selectedClient.responsibleUserName}
                                    </p>
                                  )}
                                  {selectedClient.clientStatus?.description && (
                                    <p className="text-xs text-slate-500">{selectedClient.clientStatus.description}</p>
                                  )}
                                </div>
                              )}
                              {selectedClient && onOpenClient && (
                                <button
                                  type="button"
                                  onClick={() => handleOpenClientCard(selectedClient.id)}
                                  className="inline-flex w-fit items-center text-xs font-medium text-blue-700 hover:text-blue-800 hover:underline"
                                >
                                  {counterpartyOpenCardLabel}
                                </button>
                              )}
                            </label>

                            {!isIncome && (
                              <div className="rounded-xl border border-amber-200 bg-amber-50/70 p-3">
                                <LookupSelect
                                  label="Клиент и дело расхода"
                                  value={form.expenseClientId}
                                  onChange={handleExpenseClientChange}
                                  options={clientLookupOptions}
                                  placeholder="Выберите клиента, к делу которого относится расход"
                                  title="Выбор клиента для расхода"
                                  dictionaryLabel="Справочник"
                                  searchPlaceholder="Поиск клиента"
                                  emptyOptionLabel="Без привязки к клиенту"
                                  emptyOptionDescription="Использовать для общих расходов без конкретного дела"
                                  helperText="Эта привязка нужна для аналитики расходов, долгов и рентабельности дела."
                                  disabled={lookupsLoading}
                                  buttonClassName="bg-white"
                                />
                                {(selectedExpenseClient?.clientStatus || selectedExpenseClient?.responsibleUserName) && (
                                  <div className="mt-2 space-y-1 rounded-xl border border-amber-100 bg-white/80 p-2">
                                    {selectedExpenseClient.clientStatus && (
                                      <ClientStatusBadge status={selectedExpenseClient.clientStatus} />
                                    )}
                                    {selectedExpenseClient.responsibleUserName && (
                                      <p className="text-xs text-slate-600">
                                        Ответственный: {selectedExpenseClient.responsibleUserName}
                                      </p>
                                    )}
                                    {selectedExpenseClient.clientStatus?.description && (
                                      <p className="text-xs text-slate-500">{selectedExpenseClient.clientStatus.description}</p>
                                    )}
                                  </div>
                                )}
                              </div>
                            )}

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">Договор</span>
                              <div className="flex items-stretch gap-2">
                                <select
                                  value={form.contractId}
                                  onChange={handleChange('contractId')}
                                  className="min-w-0 flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  disabled={!form.clientId || contractsLoading}
                                >
                                  <option value="">Без договора</option>
                                  {contractOptions.map((contract) => (
                                    <option key={contract.id} value={String(contract.id)}>
                                      {contract.number}{contract.title ? ` — ${contract.title}` : ''}
                                    </option>
                                  ))}
                                </select>
                                <button
                                  type="button"
                                  onClick={() => {
                                    setCreateContractError(null);
                                    setCreateContractOpen(true);
                                  }}
                                  disabled={!form.clientId}
                                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                                  aria-label="Добавить договор"
                                  title="Добавить договор"
                                >
                                  <Plus className="h-4 w-4" />
                                </button>
                              </div>
                              {contractsLoading && <span className="text-xs text-slate-500">Загрузка договоров...</span>}
                              {contractsError && <span className="text-xs text-rose-600">{contractsError}</span>}
                              {!form.clientId && (
                                <span className="text-xs text-slate-500">
                                  Сначала выберите {counterpartyLabel.toLocaleLowerCase('ru')}, затем можно будет быстро создать договор.
                                </span>
                              )}
                            </label>

                          </div>
                        </section>

              <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {t('invoiceScheduleTitle') ?? 'Сроки и оплата'}
                            </span>
              
                          </header>
                          <div className="mt-4 grid grid-cols-1 gap-4">
                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDate') ?? 'Дата выставления'}</span>
                              <input
                                type="date"
                                value={form.date}
                                onChange={handleChange('date')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                required
                              />
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDueDate') ?? 'Срок оплаты'}</span>
                              <input
                                type="date"
                                value={form.dueDate}
                                onChange={handleChange('dueDate')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                              />
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoicePaidDate') ?? 'Дата оплаты'}</span>
                              <input
                                type="date"
                                value={form.paidDate}
                                onChange={handleChange('paidDate')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                              />
                            </label>
                          </div>
                          {auditInfoSection}
                          {statusHistorySection}
                        </section>

            </div>
          </div>

          {!(isPageMode && mode === 'edit') && (
            <div className="flex items-center justify-end gap-3 pt-2">
              <button
                type="submit"
                disabled={submitting}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
              >
                {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
                {mode === 'edit' ? 'Сохранить' : t('invoiceCreate') ?? 'Создать счёт'}
              </button>
              <button
                type="button"
                onClick={() => {
                  if (isPageMode && mode === 'edit') {
                    setPageViewMode(true);
                    return;
                  }
                  onClose();
                }}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
              >
                {t('cancel') ?? 'Отмена'}
              </button>
            </div>
          )}
      </form>
      )}

      {contractPreviewOpen && selectedContractPreview && (
        <div className="fixed inset-0 z-[2240] flex items-center justify-center bg-slate-900/60 p-4">
          <div data-testid="invoice-related-contract-preview" className="w-full max-w-2xl rounded-2xl bg-white shadow-2xl">
            <div className="flex items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 className="text-lg font-semibold text-slate-900">
                  {formatContractSidebarLabel(selectedContractPreview)}
                </h3>
                <p className="mt-1 text-sm text-slate-500">Просмотр связанного договора по счету.</p>
              </div>
              <button
                type="button"
                onClick={closeContractPreview}
                className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
                aria-label={t('close') ?? 'Закрыть'}
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-4 px-5 py-4">
              {contractPreviewError && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                  {contractPreviewError}
                </div>
              )}

              {contractPreviewLoading && (
                <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Обновляем данные договора...
                </div>
              )}

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Тип расчетов</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {getContractBillingModeLabel(selectedContractPreview.billingMode)}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Сумма договора</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {getContractEffectiveAmount(selectedContractPreview) !== null
                      ? formatCurrencySmart(getContractEffectiveAmount(selectedContractPreview) ?? 0).full
                      : '—'}
                  </div>
                </div>
              </div>

              <div className="rounded-xl border border-slate-200 px-4 py-3">
                <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Название</div>
                <div className="mt-2 text-sm leading-6 text-slate-700">
                  {selectedContractPreview.title?.trim() || 'Без названия'}
                </div>
              </div>

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Дата договора</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {formatDate(selectedContractPreview.date) || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Срок действия</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedContractPreview.isOpenEnded
                      ? 'До исполнения обязательств'
                      : (formatDate(selectedContractPreview.validUntil) || '—')}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Клиенты</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedContractPreview.clients?.length
                      ? selectedContractPreview.clients.map((client) => client.name).join(', ')
                      : (selectedClient?.name || '—')}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Связанные документы</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {`Счетов: ${selectedContractPreview.invoiceIds?.length ?? 0}, актов: ${selectedContractPreview.actIds?.length ?? 0}, платежей: ${selectedContractPreview.paymentIds?.length ?? 0}`}
                  </div>
                </div>
              </div>

              {selectedContractPreview.description?.trim() ? (
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Описание</div>
                  <div className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">
                    {selectedContractPreview.description}
                  </div>
                </div>
              ) : null}
            </div>

            <div className="flex justify-end border-t border-slate-200 px-5 py-4">
              {onOpenContract && selectedContractPreview.id > 0 ? (
                <button
                  type="button"
                  onClick={() => {
                    closeContractPreview();
                    onOpenContract(selectedContractPreview.id);
                  }}
                  className="mr-auto rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
                >
                  Открыть полный договор
                </button>
              ) : null}
              <button
                type="button"
                onClick={closeContractPreview}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}

      {paymentPreviewOpen && selectedPaymentPreview && (
        <div className="fixed inset-0 z-[2250] flex items-center justify-center bg-slate-900/60 p-4">
          <div data-testid="invoice-related-payment-preview" className="w-full max-w-2xl rounded-2xl bg-white shadow-2xl">
            <div className="flex items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 className="text-lg font-semibold text-slate-900">Платеж #{selectedPaymentPreview.id}</h3>
                <p className="mt-1 text-sm text-slate-500">Просмотр связанного платежа по счету.</p>
              </div>
              <button
                type="button"
                onClick={closePaymentPreview}
                className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
                aria-label={t('close') ?? 'Закрыть'}
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-4 px-5 py-4">
              {paymentPreviewError && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                  {paymentPreviewError}
                </div>
              )}

              {paymentPreviewLoading && (
                <div className="inline-flex items-center gap-2 text-sm text-slate-500">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Обновляем данные платежа...
                </div>
              )}

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Сумма</div>
                  <div className="mt-2 text-xl font-semibold text-slate-900">
                    {formatCurrencySmart(selectedPaymentPreview.amount ?? 0).full}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Статус</div>
                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    <span
                      className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                        relatedPaymentStatusMeta[selectedPaymentPreview.status] ?? relatedPaymentStatusMeta.Pending
                      }`}
                    >
                      {invoicePaymentStatusLabel[selectedPaymentPreview.status] ?? invoicePaymentStatusLabel.Pending}
                    </span>
                    <span className="inline-flex items-center rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-700">
                      {selectedPaymentPreview.type === 'Expense' ? 'Расходный платеж' : 'Доходный платеж'}
                    </span>
                    {selectedPaymentPreviewInstallment ? (
                      <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2.5 py-1 text-xs font-semibold text-violet-700">
                        Рассрочка {selectedPaymentPreviewInstallment.sequence}/{Math.max(selectedPaymentPreviewInstallment.months, selectedPaymentPreviewInstallment.sequence)}
                      </span>
                    ) : null}
                  </div>
                </div>
              </div>

              <div className="rounded-xl border border-slate-200 px-4 py-3">
                <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Описание</div>
                <div className="mt-2 text-sm leading-6 text-slate-700">
                  {selectedPaymentPreview.description?.trim() || 'Без описания'}
                </div>
              </div>

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Плановая дата</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {formatDate(selectedPaymentPreview.plannedDate || selectedPaymentPreview.date) || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Дата оплаты</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {formatDate(selectedPaymentPreview.paidDate || selectedPaymentPreview.lastPaymentDate) || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Счет</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedPaymentPreview.account || form.number || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Клиент</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedPaymentPreview.client?.name
                      || (selectedPaymentPreview.type === 'Expense' ? selectedExpenseClient?.name : selectedClient?.name)
                      || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Дело</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedPaymentPreview.clientCase?.title || selectedClientCasesSummary || '—'}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Связь с договором</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {selectedPaymentPreview.contractId
                      ? `ID ${selectedPaymentPreview.contractId}`
                      : (selectedContract ? formatContractSidebarLabel(selectedContract) : '—')}
                  </div>
                </div>
              </div>

              {selectedPaymentPreview.notes?.trim() ? (
                <div className="rounded-xl border border-slate-200 px-4 py-3">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Комментарий</div>
                  <div className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">
                    {selectedPaymentPreview.notes}
                  </div>
                </div>
              ) : null}
            </div>

            <div className="flex justify-end border-t border-slate-200 px-5 py-4">
              {onOpenPayment && selectedPaymentPreview.id > 0 && !isInvoiceManagedPayment(selectedPaymentPreview) ? (
                <button
                  type="button"
                  onClick={() => {
                    closePaymentPreview();
                    onOpenPayment(selectedPaymentPreview);
                  }}
                  className="mr-auto rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100"
                >
                  Открыть полный платеж
                </button>
              ) : null}
              <button
                type="button"
                onClick={closePaymentPreview}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}

      <InvoiceLineItemLookupDialog
        open={lineItemLookupState?.kind === 'category'}
        title="Выбор категории услуги"
        description="Выберите категорию из справочника для текущей позиции счёта."
        query={lineItemLookupQuery}
        value={activeLineItemLookup?.serviceCategoryId ?? ''}
        options={lineItemCategoryLookupOptions}
        searchPlaceholder="Поиск категории"
        maxWidthClassName="max-w-2xl"
        clearActionLabel="Все категории"
        clearActionDescription="Оставить позицию без конкретной категории."
        noOptionsText="Категории не найдены"
        onQueryChange={setLineItemLookupQuery}
        onClose={closeLineItemLookup}
        onSelect={(value) => {
          if (!lineItemLookupState?.lineItemId) {
            return;
          }
          handleLineItemCategoryChange(lineItemLookupState.lineItemId, value);
          closeLineItemLookup();
        }}
        onClear={() => {
          if (!lineItemLookupState?.lineItemId) {
            return;
          }
          handleLineItemCategoryChange(lineItemLookupState.lineItemId, '');
          closeLineItemLookup();
        }}
      />

      <InvoiceLineItemLookupDialog
        open={lineItemLookupState?.kind === 'service'}
        title="Выбор наименования услуги"
        description={
          activeLineItemCategoryLabel === 'Все категории'
            ? 'Можно искать по всем услугам или ввести своё наименование.'
            : `Категория: ${activeLineItemCategoryLabel}. Можно выбрать из найденных услуг или ввести своё наименование.`
        }
        query={lineItemLookupQuery}
        value={activeLineItemSelectedServiceId}
        options={lineItemServiceLookupOptions}
        searchPlaceholder="Поиск наименования"
        maxWidthClassName="max-w-4xl"
        clearActionLabel="Очистить наименование"
        clearActionDescription="Оставить позицию без выбранного шаблона услуги."
        customActionLabel={canApplyCustomServiceName ? `Использовать: ${lineItemLookupQueryTrimmed}` : undefined}
        customActionDescription={canApplyCustomServiceName ? 'Сохранить введённый текст как наименование позиции.' : undefined}
        noOptionsText="Подходящие услуги не найдены"
        onQueryChange={setLineItemLookupQuery}
        onClose={closeLineItemLookup}
        onSelect={handleLineItemServiceSelect}
        onClear={clearLineItemServiceName}
        onSelectCustomQuery={canApplyCustomServiceName ? applyCustomLineItemServiceName : undefined}
      />

      {clientPickerOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">
                Выбор {counterpartyLabel.toLocaleLowerCase('ru')}
              </h3>
              <button type="button" onClick={() => setClientPickerOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <input
              type="text"
              value={clientPickerSearch}
              onChange={(event) => setClientPickerSearch(event.target.value)}
              placeholder={t('search') ?? 'Поиск'}
              className="mb-3 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm"
            />
            <div className="max-h-72 overflow-auto rounded-lg border border-slate-200">
              {filteredClients.map((client) => (
                <button
                  key={client.id}
                  type="button"
                  onClick={() => handleClientPick(client)}
                  className="flex w-full items-center justify-between border-b border-slate-100 px-3 py-2 text-left last:border-0 hover:bg-slate-50"
                >
                  <span className="font-medium text-slate-800">{client.name}</span>
                  {client.clientStatus?.name && <span className="text-xs text-slate-500">{client.clientStatus.name}</span>}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}

      {installmentModalOpen && (
        <div className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/60 p-4">
          <div className="max-h-[calc(100vh-2rem)] w-full max-w-6xl overflow-y-auto rounded-3xl bg-slate-50 p-4 shadow-2xl sm:p-6">
            <InstallmentPlanner
              mode="invoice"
              initialPlan={installmentPlan}
              initialTotal={parseMoneyInput(form.amount)}
              initialStartDate={form.date}
              initialClientId={form.clientId ? Number(form.clientId) : undefined}
              initialCaseId={selectedClientCaseIds[0]}
              initialClientName={selectedClient?.name ?? undefined}
              initialInvoiceNumber={form.number.trim() || invoice?.number || undefined}
              initialCaseName={selectedClientCasesSummary || undefined}
              onApplyPlan={handleApplyInstallmentPlan}
              onCancel={() => setInstallmentModalOpen(false)}
            />
          </div>
        </div>
      )}

      <CaseModal
        isOpen={createCaseOpen}
        onClose={() => setCreateCaseOpen(false)}
        caseData={createCaseStub}
        onSave={handleCreateClientCase}
        overlayClassName="z-[2300]"
      />

      {createActOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">{t('actCreate') ?? 'Создать акт'}</h3>
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actNumber') ?? 'Номер акта'}</span>
                <input value={draftAct.number} onChange={(e) => setDraftAct((prev) => ({ ...prev, number: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actDate') ?? 'Дата'}</span>
                <input type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
                <input value={draftAct.title} onChange={(e) => setDraftAct((prev) => ({ ...prev, title: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('invoiceAmount') ?? 'Сумма'}</span>
                <input value={draftAct.amount} onChange={(e) => setDraftAct((prev) => ({ ...prev, amount: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actComment') ?? 'Комментарий'}</span>
                <textarea value={draftAct.comment} onChange={(e) => setDraftAct((prev) => ({ ...prev, comment: e.target.value }))} rows={2} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
            </div>
            {createActError && <div className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{createActError}</div>}
            <div className="mt-4 flex items-center justify-end gap-2">
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">{t('cancel') ?? 'Отмена'}</button>
              <button type="button" onClick={() => void handleCreateAct()} disabled={creatingAct} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-60">
                {creatingAct && <Loader2 className="h-4 w-4 animate-spin" />}
                {t('actCreate') ?? 'Создать акт'}
              </button>
            </div>
          </div>
        </div>
      )}

      <ContractModal
        open={createContractOpen}
        mode="create"
        contract={null}
        onClose={() => {
          if (createContractSubmitting) return;
          setCreateContractOpen(false);
          setCreateContractError(null);
        }}
        onSubmit={handleCreateContractFromInvoice}
        submitting={createContractSubmitting}
        errorMessage={createContractError}
        defaultClients={defaultContractClients}
        overlayClassName="z-[2300]"
      />
    </div>
  );

  if (isPageMode) {
    return <div className="w-full px-4 py-6 sm:px-6 lg:px-8">{content}</div>;
  }

  return (
    <div className="fixed inset-0 z-[1400] flex items-center justify-center bg-slate-900/60 px-4 py-8">
      {content}
    </div>
  );
}
