import { hasStorableLineItemPrecision, lineItemPrecisionError, roundedLineTotal } from '../../utils/lineItemMoney';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { DocumentSellerPicker } from '../Documents/DocumentSellerPicker';
import { DocumentImagePrintDialog } from '../Documents/DocumentImagePrintDialog';
import type { DocumentImagePrintOptions } from '../../types/documentImagePrint';
import { VatRateSelect } from '../Documents/VatRateSelect';
import { SystemSelect } from '../ui/SystemSelect';
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { ArrowLeft, Ban, Check, ChevronDown, Copy, Download, FileText, Link2, List, Loader2, Pencil, Plus, Printer, Send, Trash2, UploadCloud, UserRoundSearch, X } from 'lucide-react';
import type {
  Act,
  ActInput,
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
  InvoiceWorkflowStatus,
  LegalEntitySummary,
  MoneyOperation,
  Payment,
  PaymentKind,
  PaymentStatus,
  ServiceDictionaryItem,
} from '../../types';
import { fromInputToApiDate, todayYMDInTimeZone, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useDocumentMenuLayout } from '../../hooks/useDocumentMenuLayout';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from '../../hooks/useDocumentDeliveryOwner';
import { useAsyncOperationOwner } from '../../hooks/useAsyncOperationOwner';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useInvoiceReferenceDictionaries } from '../../hooks/useInvoiceReferenceDictionaries';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { downloadBlob } from '../../utils/downloadBlob';
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
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { MessengerDocumentDeliveryModal } from '../Cases/MessengerDocumentDeliveryModal';
import { ElectronicSignatureModal } from '../Documents/ElectronicSignatureModal';
import { useDocumentStatusSettings } from '../../hooks/useDocumentStatusSettings';
import { getDocumentStatusLabels } from '../../utils/documentStatusSettings';
import {
  filterRepresentativeInvoiceWhenInstallmentsExist,
  getStatusSourcePaymentsForInvoice,
  isGeneratedInstallmentPayment,
  isInvoiceManagedPayment,
} from '../../utils/invoicePayments';
import {
  buildInvoiceAmountReconciliation,
  type InvoiceAmountReconciliation,
} from '../../utils/invoiceAmountReconciliation';
import { getAppliedClientBalanceAmount } from '../../utils/systemNotes';
import { buildInvoiceDocumentFileName } from '../../utils/invoiceDocumentFileName';

const EMPTY_RELATED_PAYMENTS: Payment[] = [];

type FormState = {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  type: PaymentKind;
  status: PaymentStatus;
  workflowStatus: InvoiceWorkflowStatus;
  clientId: string;
  legalEntityId: string;
  expenseClientId: string;
  expenseLegalEntityId: string;
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
  vatRate?: string;
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

const DEFAULT_INVOICE_WORKFLOW_STATUS_LABELS: Record<InvoiceWorkflowStatus, string> = {
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
};

const INVOICE_OFFER_TEMPLATE_KEY = 'invoice-offer';
const INVOICE_ORDER_TEMPLATE_KEY = 'invoice-order';

const INVOICE_WORKFLOW_STATUS_CLASSES: Record<InvoiceWorkflowStatus, string> = {
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
};

type InvoiceCounterpartyPickerMode = 'main' | 'expense';

type LineItemLookupState = { lineItemId: string; ownerScope: string } | null;
type InvoiceCancellationScope = 'All' | 'UnpaidInstallmentsOnly';
type InvoiceCancellationMoneyAction = 'Reject' | 'LeaveAsAdvance' | 'Refund' | 'Reallocate';
type InvoiceCancellationRefundSource = 'Bank' | 'Cash' | 'Card' | 'EMoney';
type InvoiceAmountReconciliationDecision = 'save' | 'manage' | 'cancel';

const LINE_ITEM_GRID_TEMPLATE =
  'md:grid-cols-[minmax(24rem,2.7fr)_minmax(5.5rem,0.55fr)_minmax(6.5rem,0.7fr)_minmax(8.5rem,0.85fr)_minmax(6.5rem,0.7fr)_minmax(8.5rem,0.85fr)_2.75rem]';
const LINE_ITEM_GRID_MIN_WIDTH_CLASS = 'md:min-w-[64rem]';

function formatMoneyValue(value: string | number | null | undefined): string {
  const parsed = parseMoneyInput(value);
  if (!Number.isFinite(parsed)) return '0,00';
  const [integer, decimal] = parsed.toFixed(2).split('.');
  return `${groupThousands(integer)},${decimal}`;
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
  roundLine = false,
): string {
  const parsedQuantity = parseMoneyInput(quantity);
  const parsedPrice = parseMoneyInput(price);
  return formatMoneyDisplayValue(roundLine ? roundedLineTotal(parsedQuantity, parsedPrice) : parsedQuantity * parsedPrice);
}

function normalizeLookupName(value: string | null | undefined): string {
  return String(value ?? '').trim().toLocaleLowerCase('ru');
}

function getLegalEntityDisplayName(entity: LegalEntitySummary): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юр. лицо #${entity.id}`;
}

function normalizeLineItems(source?: InvoiceLineItem[] | null, fallbackAmount?: number): LineItem[] {
  if (source && source.length > 0) {
    return source.map((item, index) => ({
      id: `${Date.now()}-${index}`,
      name: item.name ?? '',
      quantity: String(item.quantity ?? 0),
      unit: item.unit ?? 'шт',
      price: formatMoneyValue(item.price),
      vatRate: item.vatRate ?? '',
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
      vatRate: '',
      serviceCategoryId: '',
      serviceId: '',
    },
  ];
}

type ActOption = Pick<Act, 'id' | 'number' | 'title' | 'date' | 'amount' | 'invoiceNumber' | 'clientId' | 'clientName' | 'legalEntityId' | 'legalEntityName' | 'status'>;
type ClientCaseOption = Pick<ClientCase, 'id' | 'clientId' | 'legalEntityId' | 'title' | 'status' | 'createdAt'>;

interface InvoiceModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  invoice: Invoice | null;
  resumeDraft?: InvoiceEditorDraftState | null;
  onClose: () => void;
  onBack?: () => void;
  onOpenClient?: (clientId: number) => void;
  onOpenContract?: (contractId: number) => void;
  onOpenActPage?: (actId: number, options?: { invoiceId?: number | null }) => void;
  onOpenPayment?: (paymentId: number, paymentType?: PaymentKind) => void;
  onSubmit: (payload: InvoiceInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  legalEntities?: LegalEntitySummary[];
  allowedCounterpartyScopes?: CounterpartyKind[];
  legalEntitiesLoading?: boolean;
  legalEntitiesError?: string | null;
  onClientCreated?: (client: Client) => void;
  responsibles: ActResponsible[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  onRetryLookups?: () => void;
  defaultClientId?: number;
  defaultLegalEntityId?: number;
  defaultExpenseClientId?: number;
  defaultExpenseLegalEntityId?: number;
  defaultClientCaseId?: number;
  defaultResponsibleId?: number;
  defaultType?: PaymentKind;
  lockType?: boolean;
  presentation?: 'modal' | 'page';
  relatedPayments?: Payment[];
  paymentsLoading?: boolean;
  paymentsError?: string | null;
  onRetryPayments?: () => Promise<void> | void;
  onRefreshRequested?: () => Promise<void> | void;
  onAddRelatedPayment?: () => void;
  onEditRelatedPayment?: (payment: Payment) => void;
  onDeleteRelatedPayment?: (payment: Payment) => Promise<void> | void;
  onClientCaseCreated?: (clientCase: ClientCase) => void;
  onCreateActPage?: (
    initialValues?: Partial<ActInput> & { amount?: number | null },
    options?: { invoiceId?: number | null; invoiceSeed?: Invoice | null },
  ) => void;
}

function normalizeInvoiceToForm(
  invoice: Invoice | null,
  defaultClientId?: number,
  defaultLegalEntityId?: number,
  defaultResponsibleId?: number,
  defaultType?: PaymentKind,
  defaultExpenseClientId?: number,
  defaultExpenseLegalEntityId?: number,
  timeZoneId?: string | null,
): FormState {
  const today = todayYMDInTimeZone(timeZoneId);
  const resolvedType: PaymentKind = defaultType ?? 'Income';
  if (!invoice) {
    return {
      number: '',
      date: today,
      dueDate: '',
      amount: '',
      type: resolvedType,
      status: 'Pending',
      workflowStatus: 'Draft',
      clientId: defaultLegalEntityId ? '' : defaultClientId ? String(defaultClientId) : '',
      legalEntityId: defaultLegalEntityId ? String(defaultLegalEntityId) : '',
      expenseClientId: resolvedType === 'Expense' && defaultExpenseClientId ? String(defaultExpenseClientId) : '',
      expenseLegalEntityId: resolvedType === 'Expense' && defaultExpenseLegalEntityId ? String(defaultExpenseLegalEntityId) : '',
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
    workflowStatus: invoice.workflowStatus ?? 'Draft',
    clientId: invoice.legalEntityId || defaultLegalEntityId ? '' : invoice.clientId ? String(invoice.clientId) : defaultClientId ? String(defaultClientId) : '',
    legalEntityId: invoice.legalEntityId ? String(invoice.legalEntityId) : defaultLegalEntityId ? String(defaultLegalEntityId) : '',
    expenseClientId: invoice.expenseClientId
      ? String(invoice.expenseClientId)
      : invoice.expenseLegalEntityId || defaultExpenseLegalEntityId
        ? ''
        : (invoice.type ?? resolvedType) === 'Expense' && defaultExpenseClientId
          ? String(defaultExpenseClientId)
          : '',
    expenseLegalEntityId: invoice.expenseLegalEntityId ? String(invoice.expenseLegalEntityId) : (invoice.type ?? resolvedType) === 'Expense' && defaultExpenseLegalEntityId ? String(defaultExpenseLegalEntityId) : '',
    contractId: invoice.contractId ? String(invoice.contractId) : '',
    clientCaseReference: invoice.clientCaseReference ?? '',
    description: invoice.description ?? '',
    isNotForAccounting: Boolean(invoice.isNotForAccounting),
    actReference: invoice.actReference ?? '',
    paidDate: toDateInputValue(invoice.paidDate) || '',
    responsibleIds: (invoice.responsibles ?? []).map((item) => String(item.id)).slice(0, 1),
  };
}

function normalizeInvoiceClientCaseIds(invoice: Invoice | null, defaultClientCaseId?: number): number[] {
  const rawIds = invoice?.clientCaseIds?.length
    ? invoice.clientCaseIds
    : invoice?.clientCaseId
      ? [invoice.clientCaseId]
      : defaultClientCaseId
        ? [defaultClientCaseId]
        : [];

  return Array.from(new Set(rawIds.filter((value): value is number => typeof value === 'number' && value > 0)));
}

function normalizeInvoiceClientCases(invoice: Invoice | null): ClientCaseOption[] {
  if (invoice?.clientCases?.length) {
    return invoice.clientCases.map((item) => ({
      id: item.id,
      clientId: item.clientId,
      legalEntityId: item.legalEntityId,
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
        legalEntityId:
          invoice.type === 'Expense'
            ? (invoice.expenseLegalEntityId ?? invoice.legalEntityId ?? null)
            : (invoice.legalEntityId ?? null),
        title: invoice.clientCaseTitle ?? '',
        status: '',
        createdAt: invoice.createdAt,
      },
    ];
  }

  return [];
}

function parsePositiveId(value?: string | null): number | null {
  const parsed = Number.parseInt(value ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function isClientCaseLinkedToOwner(
  clientCase: Pick<ClientCaseOption, 'clientId' | 'legalEntityId'>,
  ownerClientId: number | null,
  ownerLegalEntityId: number | null,
): boolean {
  return (clientCase.clientId ?? null) === ownerClientId
    && (clientCase.legalEntityId ?? null) === ownerLegalEntityId;
}

function filterClientCaseIdsForOwner(
  ids: number[],
  options: ClientCaseOption[],
  ownerClientId: number | null,
  ownerLegalEntityId: number | null,
): number[] {
  if (!ownerClientId && !ownerLegalEntityId) {
    return [];
  }

  const optionsById = new Map(options.map((option) => [option.id, option]));
  const result: number[] = [];
  ids.forEach((id) => {
    if (!Number.isFinite(id) || id <= 0 || result.includes(id)) {
      return;
    }

    const option = optionsById.get(id);
    if (option && isClientCaseLinkedToOwner(option, ownerClientId, ownerLegalEntityId)) {
      result.push(id);
    } else if (!option) {
      result.push(id);
    }
  });

  return result;
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
      legalEntityId: item.legalEntityId ?? undefined,
      legalEntityName: item.legalEntityName ?? undefined,
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
        legalEntityId: invoice.legalEntityId ?? undefined,
        legalEntityName: invoice.legalEntityName ?? undefined,
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
  if (normalized.includes('lost') || normalized.includes('проигр')) return 'Проиграно';
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

function moneyOperationSourceLabel(source?: MoneyOperation['source'] | null): string {
  switch (source) {
    case 'Cash':
      return 'Касса';
    case 'Card':
      return 'Карты';
    case 'EMoney':
      return 'Электронные деньги';
    case 'Bank':
    default:
      return 'Банк';
  }
}

function moneyOperationDirectionLabel(direction?: MoneyOperation['direction'] | null): string {
  switch (direction) {
    case 'Expense':
      return 'Расход';
    case 'Transfer':
      return 'Перевод';
    case 'Income':
    default:
      return 'Приход';
  }
}

function moneyOperationMatchStatusLabel(status?: MoneyOperation['matchStatus']): string | null {
  switch (status) {
    case 'Matched':
      return 'Сверено';
    case 'ClarificationRequired':
      return 'Уточнить';
    case 'Ignored':
      return 'Не сверять';
    default:
      return null;
  }
}

function resolveRelatedPaymentExpectedDate(payment: Payment): string {
  return payment.date ?? payment.plannedDate ?? '';
}

function resolveRelatedPaymentActualDate(payment: Payment): string {
  return payment.paidDate ?? payment.lastPaymentDate ?? '';
}

function formatContractSidebarLabel(contract: Contract | null): string {
  if (!contract) return 'Без договора';
  const number = contract.number?.trim() || `#${contract.id}`;
  const dateLabel = formatDate(contract.date) || 'без даты';
  return `Договор №${number} от ${dateLabel}`;
}

function formatRelatedPaymentLinkLabel(payment: Payment): string {
  const sourceLabel = payment.paymentSource?.name?.trim();
  const amountLabel = formatCurrencySmart(payment.amount ?? 0).short;
  return sourceLabel
    ? `${amountLabel} · ${sourceLabel}`
    : amountLabel;
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

function paymentHasReceivedMoney(payment: Payment): boolean {
  return (
    resolvePaidAmount(payment) > 0
    || Boolean(payment.isPaid)
    || Boolean(payment.paidDate)
    || Boolean(payment.lastPaymentDate)
    || payment.status === 'Completed'
  );
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
  fallbackAmount?: string | number | null,
  fallbackPaidAmount?: string | number | null,
  todayKey: string = todayYMDInTimeZone(),
): PaymentStatus {
  const sourcePayments = getStatusSourcePaymentsForInvoice(payments);

  if (sourcePayments.length === 0) {
    if (fallbackStatus === 'Completed' || fallbackStatus === 'Cancelled' || fallbackStatus === 'Overdue') {
      return fallbackStatus;
    }

    const fallbackAmountValue = parseMoneyInput(fallbackAmount);
    const fallbackPaidAmountValue = Math.min(parseMoneyInput(fallbackPaidAmount), Math.max(fallbackAmountValue, 0));
    if (fallbackPaidAmountValue > 0 && fallbackPaidAmountValue < fallbackAmountValue) {
      return 'Processing';
    }

    const normalizedDueDate = toDateInputValue(fallbackDueDate ?? null);
    if (normalizedDueDate && normalizedDueDate < todayKey) {
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

function deriveInvoiceWorkflowStatus(status: PaymentStatus, amount: number, fallback: InvoiceWorkflowStatus): InvoiceWorkflowStatus {
  void amount;
  switch (status) {
    case 'Cancelled':
      return 'Cancelled';
    default:
      return fallback;
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
  fallbackPaidAmount?: string | number | null,
  fallbackOutstandingAmount?: string | number | null,
  todayKey?: string,
): InvoiceStatusSnapshot {
  const sourcePayments = getStatusSourcePaymentsForInvoice(payments);
  const status = deriveInvoiceStatusFromPayments(payments, fallbackStatus, fallbackDueDate, fallbackAmount, fallbackPaidAmount, todayKey);
  const fallbackAmountValue = parseMoneyInput(fallbackAmount);
  const fallbackPaidAmountValue = Math.min(parseMoneyInput(fallbackPaidAmount), Math.max(fallbackAmountValue, 0));
  const fallbackOutstandingAmountValue = parseMoneyInput(fallbackOutstandingAmount);
  const aggregatedTotalFromPayments = sourcePayments.reduce((sum, payment) => {
    const paymentAmount = Math.max(payment.amount ?? 0, 0);
    const resolvedAmount = Math.max(paymentAmount, resolvePaidAmount(payment) + resolveOutstandingAmount(payment));
    return sum + resolvedAmount;
  }, 0);

  const totalAmount = sourcePayments.length > 0
    ? Math.max(fallbackAmountValue, aggregatedTotalFromPayments)
    : (fallbackAmountValue > 0 ? fallbackAmountValue : aggregatedTotalFromPayments);
  const paidAmount = sourcePayments.length > 0
    ? sourcePayments.reduce((sum, payment) => sum + resolvePaidAmount(payment), 0)
    : status === 'Completed'
      ? totalAmount
      : fallbackPaidAmountValue;
  const sourceOutstandingAmount = sourcePayments.reduce((sum, payment) => sum + resolveOutstandingAmount(payment), 0);
  const hasCancelledRelatedPayments = payments.some((payment) => payment.status === 'Cancelled');
  const amountBasedOutstandingAmount = Math.max(totalAmount - paidAmount, 0);
  const outstandingAmount = status === 'Completed' || status === 'Cancelled'
    ? 0
    : hasCancelledRelatedPayments && sourceOutstandingAmount <= 0
      ? 0
      : sourceOutstandingAmount > 0
        ? sourceOutstandingAmount
      : sourcePayments.length > 0
        ? amountBasedOutstandingAmount
        : fallbackOutstandingAmountValue > 0
          ? fallbackOutstandingAmountValue
          : amountBasedOutstandingAmount;

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
    key: `current-${status}-${paidAmount}-${outstandingAmount}`,
    title: `Текущий статус: ${invoiceStatusLabel(status)}`,
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
  onOpenActPage,
  onOpenPayment,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  legalEntities = [],
  allowedCounterpartyScopes,
  responsibles = [],
  lookupsLoading,
  lookupsError,
  onRetryLookups,
  defaultClientId,
  defaultLegalEntityId,
  defaultExpenseClientId,
  defaultExpenseLegalEntityId,
  defaultClientCaseId,
  defaultResponsibleId,
  defaultType,
  lockType = false,
  presentation = 'modal',
  relatedPayments: suppliedRelatedPayments = EMPTY_RELATED_PAYMENTS,
  paymentsLoading = false,
  paymentsError = null,
  onRetryPayments,
  onRefreshRequested,
  onAddRelatedPayment,
  onEditRelatedPayment,
  onDeleteRelatedPayment,
  onClientCaseCreated,
  onCreateActPage,
}: InvoiceModalProps) {
  const { user: currentUser } = useAuth();
  const isAdmin = (currentUser?.role?.name ?? '').toLowerCase() === 'admin';
  const permissions = useRolePermissions(currentUser?.role?.id);
  const canViewPayments = isAdmin || permissions.payments.canView;
  const relatedPayments = canViewPayments ? suppliedRelatedPayments : EMPTY_RELATED_PAYMENTS;
  const canExport = isAdmin || permissions.accounts.canExport;
  const { t } = useTranslation();
  const { confirm, prompt } = useDialog();
  const toast = useToast();
  const profileTimeZoneId = useUserTimeZone();
  const profileTimeZoneIdRef = useRef(profileTimeZoneId);
  profileTimeZoneIdRef.current = profileTimeZoneId;
  const defaultDate = todayYMDInTimeZone(profileTimeZoneId);
  const defaultDateRef = useRef(defaultDate);
  const typeOptions: PaymentKind[] = ['Income', 'Expense'];
  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };
  const isTypeLocked = lockType && mode === 'create';
  const [form, setForm] = useState<FormState>(() =>
    normalizeInvoiceToForm(invoice, defaultClientId, defaultLegalEntityId, defaultResponsibleId, defaultType, defaultExpenseClientId, defaultExpenseLegalEntityId, profileTimeZoneId),
  );
  const [lineItems, setLineItems] = useState<LineItem[]>([]);
  const roundLineAmounts = useMemo(() => lineItems.some((item) => Boolean(item.vatRate)), [lineItems]);
  const [lineItemLookupState, setLineItemLookupState] = useState<LineItemLookupState>(null);
  const [lineItemLookupQuery, setLineItemLookupQuery] = useState('');
  const [localError, setLocalError] = useState<string | null>(null);
  const [sellerSelection, setSellerSelection] = useState<{ companyId: number | null; branchId: number | null }>({ companyId: null, branchId: null });
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientPickerMode, setClientPickerMode] = useState<InvoiceCounterpartyPickerMode>('main');
  const canSelectCounterparty = allowedCounterpartyScopes === undefined || allowedCounterpartyScopes.length > 0;
  const [selectedActs, setSelectedActs] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [actOptions, setActOptions] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [clientCaseOptions, setClientCaseOptions] = useState<ClientCaseOption[]>(() => normalizeInvoiceClientCases(invoice));
  const [selectedClientCaseIds, setSelectedClientCaseIds] = useState<number[]>(() => normalizeInvoiceClientCaseIds(invoice, defaultClientCaseId));
  const [caseDropdownOpen, setCaseDropdownOpen] = useState(false);
  const [createCaseOpen, setCreateCaseOpen] = useState(false);
  const [createActOpen, setCreateActOpen] = useState(false);
  const [pageViewMode, setPageViewMode] = useState<boolean>(presentation === 'page' && mode === 'edit');
  const [creatingAct, setCreatingAct] = useState(false);
  const [createActError, setCreateActError] = useState<string | null>(null);
  const [draftAct, setDraftAct] = useState<DraftActForm>({
    number: '',
    title: '',
    date: defaultDate,
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
  const [clientCasesLoadedOwnerKey, setClientCasesLoadedOwnerKey] = useState('');
  const clientCasesLoadGeneration = useRef(0);
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
  const [storedDisplayPayments, setDisplayPayments] = useState<Payment[]>(visibleRelatedPayments);
  const displayPayments = canViewPayments ? storedDisplayPayments : EMPTY_RELATED_PAYMENTS;
  const [amountReconciliationDialog, setAmountReconciliationDialog] = useState<InvoiceAmountReconciliation | null>(null);
  const [linkedMoneyOperations, setLinkedMoneyOperations] = useState<MoneyOperation[]>([]);
  const [linkedMoneyOperationsLoading, setLinkedMoneyOperationsLoading] = useState(false);
  const [linkedMoneyOperationsError, setLinkedMoneyOperationsError] = useState<string | null>(null);
  const [counterpartyBalance, setCounterpartyBalance] = useState<number | null>(null);
  const [counterpartyBalanceLoading, setCounterpartyBalanceLoading] = useState(false);
  const [installmentPlan, setInstallmentPlan] = useState<InstallmentPlanInput | null>(() => cloneInstallmentPlan(deriveInstallmentPlanFromPayments(visibleRelatedPayments)));
  const [installmentModalOpen, setInstallmentModalOpen] = useState(false);
  const [installmentPlanTouched, setInstallmentPlanTouched] = useState(false);
  const [documentPreviewHtml, setDocumentPreviewHtml] = useState('');
  const [documentPreviewLoading, setDocumentPreviewLoading] = useState(false);
  const [documentPreviewError, setDocumentPreviewError] = useState<string | null>(null);
  const [printMenuOpen, setPrintMenuOpen] = useState(false);
  const printButtonRef = useRef<HTMLButtonElement>(null);
  const printRequestGeneration = useRef(0);
  const canReadPrint = isAdmin || permissions.accounts.canView;
  const printOwner = useDocumentDeliveryOwner(JSON.stringify([open, invoice?.id, currentUser?.id, currentUser?.role?.id,
    canReadPrint, canExport, permissions.accounts.viewScope]));
  const printScope = printOwner.scope;
  const printScopeRef = useRef(printScope);
  printScopeRef.current = printScope;
  const [imagePrintRequest, setImagePrintRequest] = useState<{
    documentId: number; scope: string; generation: number; format: 'print' | 'word' | 'pdf'; templateKey?: string; useDefaults: boolean;
    initialOptions: DocumentImagePrintOptions; owner: DocumentDeliveryRead;
  } | null>(null);
  const [downloadMenuOpen, setDownloadMenuOpen] = useState(false);
  const printMenuLayout = useDocumentMenuLayout(printMenuOpen && open && canReadPrint && canExport, 24);
  const downloadMenuLayout = useDocumentMenuLayout(downloadMenuOpen && open && canReadPrint && canExport, 28);
  const [messengerDeliveryOpen, setMessengerDeliveryOpen] = useState<string | null>(null);
  const [electronicSignatureOpen, setElectronicSignatureOpen] = useState<string | null>(null);
  useEffect(() => { setImagePrintRequest(null); setPrintMenuOpen(false); setDownloadMenuOpen(false); }, [printScope]);
  const [cancelDialogOpen, setCancelDialogOpen] = useState(false);
  const [cancelReason, setCancelReason] = useState('');
  const [cancelScope, setCancelScope] = useState<InvoiceCancellationScope>('All');
  const [cancelMoneyAction, setCancelMoneyAction] = useState<InvoiceCancellationMoneyAction>('LeaveAsAdvance');
  const [cancelTargetPaymentId, setCancelTargetPaymentId] = useState('');
  const [cancelRefundSource, setCancelRefundSource] = useState<InvoiceCancellationRefundSource>('Bank');
  const [cancelRefundDate, setCancelRefundDate] = useState(defaultDate);
  const [cancelSubmitting, setCancelSubmitting] = useState(false);
  const [cancelError, setCancelError] = useState<string | null>(null);
  const lastErrorToastRef = useRef<string | null>(null);
  const lastLocalErrorToastRef = useRef<string | null>(null);
  const lastAutoResponsibleIdRef = useRef<string>('');
  const lastAutoContractClientIdRef = useRef<string>('');
  const contractOptionsRevisionRef = useRef(0);
  const downloadMenuRef = useRef<HTMLDivElement | null>(null);
  const relatedPaymentsSectionRef = useRef<HTMLElement | null>(null);
  const amountReconciliationResolverRef = useRef<((decision: InvoiceAmountReconciliationDecision) => void) | null>(null);

  useEffect(() => () => {
    amountReconciliationResolverRef.current?.('cancel');
    amountReconciliationResolverRef.current = null;
  }, []);

  const calculateLineItemsTotal = useCallback(
    (items: LineItem[]) => {
      const roundLines = items.some((item) => Boolean(item.vatRate));
      const total = items.reduce((sum, item) => {
        const value = parseMoneyInput(item.quantity) * parseMoneyInput(item.price);
        return sum + (roundLines ? roundedLineTotal(parseMoneyInput(item.quantity), parseMoneyInput(item.price)) : value);
      }, 0);
      return formatMoneyDisplayValue(total);
    },
    [],
  );

  const referenceScope = JSON.stringify([invoice?.id, mode, presentation, currentUser?.id, currentUser?.role?.id, isAdmin, permissions.accounts]);
  const { invoiceUnits, services, serviceCategories, loading: referencesLoading, error: referencesError,
    refresh: refreshReferences, capture: captureReferenceOwner } = useInvoiceReferenceDictionaries({ enabled: open && canReadPrint, ownerScope: referenceScope });
  const referencesReady = open && canReadPrint && !referencesLoading && !referencesError;
  const canEditInvoiceItems = canReadPrint && (isAdmin || (mode === 'edit' ? permissions.accounts.canEdit : permissions.accounts.canCreate));
  const referenceView = useRef({ scope: referenceScope, ready: referencesReady, services, loading: referencesLoading, error: referencesError, editable: canEditInvoiceItems && !pageViewMode && !submitting });
  referenceView.current = { scope: referenceScope, ready: referencesReady, services, loading: referencesLoading, error: referencesError, editable: canEditInvoiceItems && !pageViewMode && !submitting };
  const capturePrintOwner = printOwner.capture;
  const canChangeCurrentInvoiceItems = useCallback(() => open && referenceView.current.scope === referenceScope && referenceView.current.editable
    && Boolean(capturePrintOwner()) && Boolean(captureReferenceOwner()), [capturePrintOwner, captureReferenceOwner, open, referenceScope]);
  const canUseCurrentReferences = useCallback(() => canChangeCurrentInvoiceItems() && referenceView.current.ready && referenceView.current.services === services,
    [canChangeCurrentInvoiceItems, services]);
  const currentLineItemLookup = useRef(lineItemLookupState);
  currentLineItemLookup.current = lineItemLookupState;
  const isCurrentLineItemLookup = () => Boolean(lineItemLookupState && currentLineItemLookup.current === lineItemLookupState
    && lineItemLookupState.ownerScope === referenceScope && referenceView.current.scope === referenceScope && open && capturePrintOwner() && captureReferenceOwner());
  useEffect(() => { setLineItemLookupState(null); setLineItemLookupQuery(''); }, [referenceScope]);
  const referenceRetry = useRef<{ scope: string } | null>(null);
  const retryReferences = async () => {
    if (!open || referenceView.current.scope !== referenceScope || !captureReferenceOwner() || !capturePrintOwner()
      || referenceView.current.loading || !referenceView.current.error || referenceRetry.current?.scope === referenceScope) return;
    const attempt = { scope: referenceScope }; referenceRetry.current = attempt;
    try { await refreshReferences(); }
    finally { if (referenceRetry.current === attempt) referenceRetry.current = null; }
  };
  const documentStatusSettings = useDocumentStatusSettings();
  const invoiceWorkflowStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'invoice', DEFAULT_INVOICE_WORKFLOW_STATUS_LABELS),
    [documentStatusSettings],
  );
  const unitOptions = useMemo(() => referencesReady ? invoiceUnits.map((item) => item.name).filter((name) => Boolean(name?.trim())) : [], [invoiceUnits, referencesReady]);
  const sortedServiceCategories = useMemo(
    () => referencesReady ? [...serviceCategories].sort((left, right) => left.name.localeCompare(right.name, 'ru')) : [],
    [serviceCategories, referencesReady],
  );
  const sortedServices = useMemo(
    () => referencesReady ? [...services].sort((left, right) => left.name.localeCompare(right.name, 'ru')) : [],
    [services, referencesReady],
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
    if (!lineItemLookupState || lineItemLookupState.ownerScope !== referenceScope) {
      return null;
    }

    return lineItems.find((item) => item.id === lineItemLookupState.lineItemId) ?? null;
  }, [lineItemLookupState, lineItems, referenceScope]);
  const lineItemCategoryFilterOptions = useMemo(
    () =>
      sortedServiceCategories.map((category) => ({
        value: String(category.id),
        label: category.name,
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
    const formatted = rawValue.trim() ? formatMoneyDisplayValue(rawValue) : '';
    updateLineItem(id, { price: formatted || formatMoneyDisplayValue(0) });
  };

  const handleLineItemCategoryChange = (id: string, serviceCategoryId: string) => {
    if (!isCurrentLineItemLookup() || !canUseCurrentReferences() || (serviceCategoryId && !sortedServiceCategories.some(category => String(category.id) === serviceCategoryId))) return;
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

  const openLineItemLookup = (lineItemId: string) => {
    if (!canChangeCurrentInvoiceItems()) return;
    const currentLineItem = lineItems.find((item) => item.id === lineItemId);
    setLineItemLookupQuery(currentLineItem?.name ?? '');
    setLineItemLookupState({ lineItemId, ownerScope: referenceScope });
  };

  const closeLineItemLookup = () => {
    if (!isCurrentLineItemLookup()) return;
    setLineItemLookupState(null);
    setLineItemLookupQuery('');
  };

  const handleLineItemServiceSelect = (serviceId: string) => {
    if (!isCurrentLineItemLookup() || !canUseCurrentReferences()) return;
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
    if (!isCurrentLineItemLookup() || !canChangeCurrentInvoiceItems()) return;
    const currentLineItemId = lineItemLookupState?.lineItemId;
    const trimmedQuery = lineItemLookupQuery.trim();
    if (!currentLineItemId || !trimmedQuery) {
      return;
    }

    const currentLineItem = lineItems.find((item) => item.id === currentLineItemId);
    if (!currentLineItem) {
      return;
    }

    const currentCatalogue = canUseCurrentReferences();
    const template = currentCatalogue ? resolveLineItemTemplate(currentLineItem, trimmedQuery) : null;
    const patch: Partial<LineItem> = {
      name: trimmedQuery,
      serviceId: template ? String(template.id) : currentCatalogue ? '' : currentLineItem.serviceId,
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
    if (!isCurrentLineItemLookup() || !canChangeCurrentInvoiceItems()) return;
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
        const blob = await apiService.getInvoicePreview(invoice.id);
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
      type === 'Expense' ? (expenseClientId || clientId) : clientId,
    [],
  );

  const isIncome = form.type === 'Income';
  const isPageMode = presentation === 'page';
  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === form.clientId) ?? null,
    [clients, form.clientId],
  );
  const canSendToDisk = canReadPrint && canExport && (isAdmin || Boolean(permissions.yandexDisk.canView && permissions.yandexDisk.canCreate));
  const deliveryScope = JSON.stringify([printScope, invoice?.auditUpdatedAt,
    invoice?.clientId, invoice?.expenseClientId, selectedClient?.email, isAdmin, canSendToDisk, permissions.yandexDisk.viewScope]);
  const delivery = useDocumentDeliveryOwner(deliveryScope);
  const leaveInvoiceCard = (leave: () => void) => {
    printOwner.invalidate();
    delivery.invalidate();
    caseCreation.invalidate();
    leave();
  };
  const yandexDiskUploading = delivery.kind === 'disk';
  useEffect(() => { setMessengerDeliveryOpen(null); setElectronicSignatureOpen(null); }, [delivery.scope]);
  const selectedLegalEntity = useMemo(
    () => legalEntities.find((entity) => String(entity.id) === form.legalEntityId) ?? null,
    [legalEntities, form.legalEntityId],
  );
  const selectedExpenseClient = useMemo(
    () => clients.find((client) => String(client.id) === form.expenseClientId) ?? null,
    [clients, form.expenseClientId],
  );
  const selectedExpenseLegalEntity = useMemo(
    () => legalEntities.find((entity) => String(entity.id) === form.expenseLegalEntityId) ?? null,
    [legalEntities, form.expenseLegalEntityId],
  );
  const caseOwnerClientId = resolveCaseOwnerClientId(form.type, form.clientId, form.expenseClientId);
  const rawCaseOwnerLegalEntityId = form.type === 'Expense'
    ? (form.expenseLegalEntityId || form.legalEntityId)
    : form.legalEntityId;
  const caseOwnerClientIdNumber = parsePositiveId(caseOwnerClientId);
  const caseOwnerLegalEntityIdNumber = caseOwnerClientIdNumber ? null : parsePositiveId(rawCaseOwnerLegalEntityId);
  const caseOwnerKey = caseOwnerClientIdNumber
    ? `client:${caseOwnerClientIdNumber}`
    : caseOwnerLegalEntityIdNumber
      ? `legal:${caseOwnerLegalEntityIdNumber}`
      : '';
  const canReadCaseOptions = (isAdmin || permissions.cases.canView)
    && (isAdmin || permissions.accounts.canView || mode === 'create' && permissions.accounts.canCreate);
  const { epoch: caseReadEpoch, actor: caseReadActor, authenticated: caseReadAuthenticated } = captureAuthSessionIdentity();
  const caseReadSession = useMemo(() => ({ epoch: caseReadEpoch, actor: caseReadActor, authenticated: caseReadAuthenticated }), [caseReadEpoch, caseReadActor, caseReadAuthenticated]);
  const caseReadScope = JSON.stringify([open, mode, invoice?.id, presentation, caseOwnerKey, form.type,
    currentUser?.id, currentUser?.role?.id, currentUser?.role?.name, canReadCaseOptions,
    permissions.cases.viewScope, permissions.accounts.viewScope, caseReadSession]);
  const { capture: captureCaseReadOwner, isCurrent: isCaseReadOwnerCurrent } = useAsyncOperationOwner(caseReadScope);
  const caseReadOwnerAtRender = captureCaseReadOwner();
  // Clear fetched names/errors before painting a different permission or login context.
  // Selected IDs and the invoice's saved links are not rewritten here.
  useLayoutEffect(() => {
    setClientCaseOptions([]);
    setClientCasesLoading(false);
    setClientCasesError(null);
    setClientCasesLoadedOwnerKey('');
    setCaseDropdownOpen(false);
  }, [caseReadScope]);
  const canCreateRelatedCase = (isAdmin || permissions.cases.canCreate)
    && (isAdmin || (mode === 'edit' ? permissions.accounts.canEdit : permissions.accounts.canCreate));
  const caseCreation = useDocumentDeliveryOwner(JSON.stringify([open, mode, invoice?.id, presentation, createCaseOpen,
    caseOwnerKey, form.type, currentUser?.id, currentUser?.role?.id, currentUser?.role?.name,
    canCreateRelatedCase, permissions.cases.viewScope, permissions.accounts.viewScope]));
  const selectedCaseOwnerClient = isIncome ? selectedClient : (selectedExpenseClient ?? selectedClient);
  const selectedCaseOwnerLegalEntity = caseOwnerClientIdNumber
    ? null
    : isIncome
      ? selectedLegalEntity
      : (selectedExpenseLegalEntity ?? selectedLegalEntity);
  const selectedResponsibleSourceClient = isIncome
    ? selectedClient
    : (selectedExpenseClient ?? selectedClient);
  const positiveCounterpartyBalance = Math.max(Number(counterpartyBalance ?? 0), 0);
  const showCounterpartyBalanceNotice = isIncome
    && Boolean(form.clientId || form.legalEntityId)
    && (counterpartyBalanceLoading || positiveCounterpartyBalance > 0);
  const counterpartyBalanceOwnerLabel = form.legalEntityId ? 'юрлица' : 'клиента';
  const selectedCounterpartyName = selectedClient?.name ?? (selectedLegalEntity ? getLegalEntityDisplayName(selectedLegalEntity) : '');
  const selectedExpenseCounterpartyName = selectedExpenseClient?.name
    ?? (selectedExpenseLegalEntity ? getLegalEntityDisplayName(selectedExpenseLegalEntity) : '');

  useEffect(() => {
    if (!open || !canViewPayments || pageViewMode || !isIncome || (!form.clientId && !form.legalEntityId)) {
      setCounterpartyBalance(null);
      setCounterpartyBalanceLoading(false);
      return;
    }

    const clientId = Number(form.clientId || 0);
    const legalEntityId = Number(form.legalEntityId || 0);
    if (!clientId && !legalEntityId) {
      setCounterpartyBalance(null);
      setCounterpartyBalanceLoading(false);
      return;
    }

    let cancelled = false;
    setCounterpartyBalanceLoading(true);
    const request = clientId > 0
      ? apiService.getClientBalance(clientId)
      : apiService.getLegalEntityBalance(legalEntityId);

    request
      .then((summary) => {
        if (!cancelled) {
          setCounterpartyBalance(summary.balance ?? 0);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setCounterpartyBalance(null);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setCounterpartyBalanceLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [form.clientId, form.legalEntityId, isIncome, open, canViewPayments, pageViewMode]);

  const selectedCounterpartyPickerValue = useMemo<CounterpartySelection[]>(() => {
    if (clientPickerMode === 'expense') {
      if (selectedExpenseClient) {
        return [{
          kind: 'client',
          id: selectedExpenseClient.id,
          name: selectedExpenseClient.name,
          subtitle: selectedExpenseClient.company ?? null,
          client: selectedExpenseClient,
        }];
      }

      if (selectedExpenseLegalEntity) {
        return [{
          kind: 'legalEntity',
          id: selectedExpenseLegalEntity.id,
          name: getLegalEntityDisplayName(selectedExpenseLegalEntity),
          subtitle: [selectedExpenseLegalEntity.inn ? `ИНН ${selectedExpenseLegalEntity.inn}` : '', selectedExpenseLegalEntity.kpp ? `КПП ${selectedExpenseLegalEntity.kpp}` : '']
            .filter(Boolean)
            .join(' · ') || selectedExpenseLegalEntity.fullName || null,
          legalEntity: selectedExpenseLegalEntity,
        }];
      }

      return [];
    }

    if (selectedClient) {
      return [{
        kind: 'client',
        id: selectedClient.id,
        name: selectedClient.name,
        subtitle: selectedClient.company ?? null,
        client: selectedClient,
      }];
    }

    if (selectedLegalEntity) {
      return [{
        kind: 'legalEntity',
        id: selectedLegalEntity.id,
        name: getLegalEntityDisplayName(selectedLegalEntity),
        subtitle: [selectedLegalEntity.inn ? `ИНН ${selectedLegalEntity.inn}` : '', selectedLegalEntity.kpp ? `КПП ${selectedLegalEntity.kpp}` : '']
          .filter(Boolean)
          .join(' · ') || selectedLegalEntity.fullName || null,
        legalEntity: selectedLegalEntity,
      }];
    }

    return [];
  }, [clientPickerMode, selectedClient, selectedExpenseClient, selectedExpenseLegalEntity, selectedLegalEntity]);
  const counterpartyLabel = isIncome ? 'Клиент' : 'Поставщик';
  const counterpartyObjectLabel = isIncome ? 'клиента' : 'поставщика';
  const counterpartyBlockTitle = isIncome ? 'Клиент и договор' : 'Поставщик и договор';
  const counterpartyPlaceholder = isIncome ? 'Выберите клиента' : 'Выберите поставщика';
  const counterpartyOpenCardLabel = isIncome ? 'Открыть карточку клиента' : 'Открыть карточку поставщика';
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
  const selectedPaymentPreviewExpectedDate = selectedPaymentPreview
    ? resolveRelatedPaymentExpectedDate(selectedPaymentPreview)
    : '';
  const selectedPaymentPreviewActualDate = selectedPaymentPreview
    ? resolveRelatedPaymentActualDate(selectedPaymentPreview)
    : '';
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
        return clientCaseLabel(match ?? {
          id,
          clientId: selectedCaseOwnerClient?.id ?? null,
          legalEntityId: selectedCaseOwnerLegalEntity?.id ?? null,
          title: '',
          status: '',
          createdAt: '',
        });
      })
      .join(', ');
  }, [clientCaseOptions, selectedCaseOwnerClient?.id, selectedCaseOwnerLegalEntity?.id, selectedClientCaseIds]);
  const selectedActsSummary = useMemo(
    () => selectedActs.map((item) => actLabel(item)).filter(Boolean).join(', '),
    [selectedActs],
  );

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

    async function loadContractsByOwner() {
      if (!open || (!form.clientId && !form.legalEntityId)) {
        setContractOptions([]);
        setContractsError(null);
        setContractsLoadedClientId('');
        return;
      }

      const clientId = Number.parseInt(form.clientId, 10);
      const legalEntityId = Number.parseInt(form.legalEntityId, 10);
      const hasClientId = Number.isFinite(clientId) && clientId > 0;
      const hasLegalEntityId = Number.isFinite(legalEntityId) && legalEntityId > 0;
      if (!hasClientId && !hasLegalEntityId) {
        setContractOptions([]);
        setContractsError(null);
        setContractsLoadedClientId('');
        return;
      }

      const requestRevision = contractOptionsRevisionRef.current;
      const requestOwnerKey = hasClientId ? `client:${clientId}` : `legal:${legalEntityId}`;
      setContractsLoading(true);
      setContractsError(null);
      setContractsLoadedClientId('');
      try {
        const response = await apiService.getContracts({
          clientId: hasClientId ? clientId : undefined,
          legalEntityId: !hasClientId && hasLegalEntityId ? legalEntityId : undefined,
          page: 1,
          pageSize: 200,
          sortBy: 'createdAt',
          sortDir: 'desc',
        });
        if (!cancelled && requestRevision === contractOptionsRevisionRef.current) {
          setContractOptions(sortContractsByRecency(response.items ?? []));
          setContractsLoadedClientId(requestOwnerKey);
        }
      } catch (error) {
        if (!cancelled && requestRevision === contractOptionsRevisionRef.current) {
          setContractOptions([]);
          setContractsLoadedClientId(requestOwnerKey);
          setContractsError(error instanceof Error ? error.message : 'Не удалось загрузить договоры');
        }
      } finally {
        if (!cancelled) setContractsLoading(false);
      }
    }

    void loadContractsByOwner();
    return () => {
      cancelled = true;
    };
  }, [open, form.clientId, form.legalEntityId]);

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
    if (!form.clientId && !form.legalEntityId) {
      setForm((prev) => ({ ...prev, contractId: '' }));
      return;
    }
    const ownerKey = form.clientId ? `client:${form.clientId}` : `legal:${form.legalEntityId}`;
    if (contractsLoading || contractsLoadedClientId !== ownerKey) return;
    if (contractOptions.some((contract) => String(contract.id) === form.contractId)) return;
    setForm((prev) => ({ ...prev, contractId: '' }));
  }, [contractOptions, contractsLoadedClientId, contractsLoading, form.clientId, form.contractId, form.legalEntityId, open]);

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
      const generation = ++clientCasesLoadGeneration.current;
      const owner = captureCaseReadOwner();
      const current = () => !cancelled && generation === clientCasesLoadGeneration.current
        && isCaseReadOwnerCurrent(owner) && isAuthSessionIdentityCurrent(caseReadSession);
      if (!open || !caseOwnerKey || !canReadCaseOptions || !caseReadSession.authenticated) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        setClientCasesLoadedOwnerKey('');
        return;
      }

      const clientId = caseOwnerClientIdNumber ?? undefined;
      const legalEntityId = caseOwnerLegalEntityIdNumber ?? undefined;
      if ((!clientId || !Number.isFinite(clientId) || clientId <= 0)
        && (!legalEntityId || !Number.isFinite(legalEntityId) || legalEntityId <= 0)) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        setClientCasesLoadedOwnerKey('');
        return;
      }

      setClientCasesLoading(true);
      setClientCasesError(null);
      setClientCasesLoadedOwnerKey('');
      try {
        const response = await apiService.getCases({
          clientId: clientId && clientId > 0 ? clientId : undefined,
          legalEntityId: legalEntityId && legalEntityId > 0 ? legalEntityId : undefined,
        });
        if (current()) {
          if (!Array.isArray(response) || !response.every(item => item && Number.isInteger(item.id) && item.id > 0
            && (clientId ? item.clientId === clientId : item.legalEntityId === legalEntityId)))
            throw new Error('Не удалось подтвердить принадлежность списка дел. Откройте счёт повторно.');
          const nextOptions = [...(response ?? [])].sort(
            (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(),
          );
          setClientCaseOptions(nextOptions);
          setClientCasesLoadedOwnerKey(caseOwnerKey);
        }
      } catch (error) {
        if (current()) {
          setClientCaseOptions([]);
          setClientCasesLoadedOwnerKey('');
          setClientCasesError(error instanceof Error ? error.message : 'Не удалось загрузить дела');
        }
      } finally {
        if (current()) setClientCasesLoading(false);
      }
    }

    void loadClientCasesByClient();
    return () => {
      cancelled = true;
    };
  }, [open, caseOwnerClientIdNumber, caseOwnerLegalEntityIdNumber, caseOwnerKey, caseReadScope,
    captureCaseReadOwner, isCaseReadOwnerCurrent, caseReadSession, canReadCaseOptions]);

  useEffect(() => {
    if (!open) {
      return;
    }

    setSelectedClientCaseIds((prev) => {
      const next = filterClientCaseIdsForOwner(
        prev,
        clientCaseOptions,
        caseOwnerClientIdNumber,
        caseOwnerLegalEntityIdNumber,
      );

      return next.length === prev.length && next.every((id, index) => id === prev[index])
        ? prev
        : next;
    });
  }, [
    caseOwnerClientIdNumber,
    caseOwnerKey,
    caseOwnerLegalEntityIdNumber,
    clientCaseOptions,
    clientCasesLoadedOwnerKey,
    open,
  ]);

  useEffect(() => {
    if (open) {
      const nextForm: FormState = resumeDraft?.form
        ? { ...resumeDraft.form, workflowStatus: resumeDraft.form.workflowStatus ?? 'Draft' }
        : normalizeInvoiceToForm(
        invoice,
        defaultClientId,
        defaultLegalEntityId,
        defaultResponsibleId,
        defaultType,
        defaultExpenseClientId,
        defaultExpenseLegalEntityId,
        profileTimeZoneIdRef.current,
      );
      setForm(nextForm);
      setSellerSelection(resumeDraft?.sellerSelection ?? { companyId: null, branchId: null });
      setLineItems(resumeDraft?.lineItems ?? normalizeLineItems(invoice?.lineItems, invoice?.amount));
      setSelectedActs(resumeDraft?.selectedActs ?? normalizeInvoiceActs(invoice));
      setActOptions(normalizeInvoiceActs(invoice));
      setClientCaseOptions(normalizeInvoiceClientCases(invoice));
      setSelectedClientCaseIds(resumeDraft?.selectedClientCaseIds ?? normalizeInvoiceClientCaseIds(invoice, defaultClientCaseId));
      setClientCasesLoadedOwnerKey('');
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
  }, [defaultClientId, defaultLegalEntityId, defaultExpenseClientId, defaultExpenseLegalEntityId, defaultClientCaseId, defaultResponsibleId, defaultType, invoice, open, presentation, relatedInstallmentPlan, resumeDraft]);

  useEffect(() => {
    if (!open || invoice || resumeDraft) {
      defaultDateRef.current = defaultDate;
      return;
    }

    setForm((current) => (
      current.date === defaultDateRef.current ? { ...current, date: defaultDate } : current
    ));
    setDraftAct((current) => (
      current.date === defaultDateRef.current ? { ...current, date: defaultDate } : current
    ));
    setCancelRefundDate((current) => (
      current === defaultDateRef.current ? defaultDate : current
    ));
    defaultDateRef.current = defaultDate;
  }, [defaultDate, invoice, open, resumeDraft]);

  useEffect(() => {
    if (!open || resumeDraft?.installmentPlan || installmentPlanTouched) {
      return;
    }

    setInstallmentPlan(cloneInstallmentPlan(relatedInstallmentPlan));
  }, [open, relatedInstallmentPlan, installmentPlanTouched, resumeDraft?.installmentPlan]);

  useEffect(() => {
    setDisplayPayments((prev) => (
      prev.length === visibleRelatedPayments.length
        && prev.every((payment, index) => payment === visibleRelatedPayments[index])
        ? prev
        : visibleRelatedPayments
    ));
  }, [visibleRelatedPayments]);

  useEffect(() => {
    const linkedInvoiceId = invoice?.invoiceEntityId ?? invoice?.id ?? null;
    if (!open || !canViewPayments || mode !== 'edit' || !linkedInvoiceId) {
      setLinkedMoneyOperations([]);
      setLinkedMoneyOperationsError(null);
      setLinkedMoneyOperationsLoading(false);
      return;
    }

    let cancelled = false;
    setLinkedMoneyOperationsLoading(true);
    setLinkedMoneyOperationsError(null);

    apiService
      .getMoneyOperationsPaged({ linkedInvoiceId, page: 1, pageSize: 20 })
      .then((response) => {
        if (cancelled) return;
        setLinkedMoneyOperations(response.items ?? []);
      })
      .catch((error) => {
        if (cancelled) return;
        setLinkedMoneyOperations([]);
        setLinkedMoneyOperationsError(extractDisplayErrorMessage(error, 'Не удалось загрузить движения денег по счёту.'));
      })
      .finally(() => {
        if (!cancelled) {
          setLinkedMoneyOperationsLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [invoice?.id, invoice?.invoiceEntityId, mode, open, canViewPayments]);

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

  const openInvoiceManagedPaymentEditor = useCallback((payment: Payment) => {
    if (onEditRelatedPayment) {
      onEditRelatedPayment(payment);
      return;
    }
    void openPaymentPreview(payment);
  }, [onEditRelatedPayment, openPaymentPreview]);

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
    if (!unitOptions.length || !canUseCurrentReferences()) return;
    setLineItems((prev) => {
      let changed = false;
      const next = prev.map((item) => {
        if (item.unit.trim()) return item;
        changed = true;
        return { ...item, unit: unitOptions[0] };
      });
      return changed ? next : prev;
    });
  }, [unitOptions, canUseCurrentReferences]);

  useEffect(() => {
    if (!sortedServices.length || !canUseCurrentReferences()) return;

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
  }, [resolveLineItemTemplate, sortedServices.length, canUseCurrentReferences]);

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
        vatRate: '',
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
      if (!open || (!form.clientId && !form.legalEntityId)) {
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
          clientId: form.clientId ? Number(form.clientId) : undefined,
          legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : undefined,
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
          legalEntityId: act.legalEntityId ?? undefined,
          legalEntityName: act.legalEntityName ?? undefined,
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
  }, [form.clientId, form.legalEntityId, open]);

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
    const nextCaseOwnerLegalEntityId = nextType === 'Expense'
      ? (form.expenseLegalEntityId || form.legalEntityId)
      : form.legalEntityId;
    const nextCaseOwnerKey = nextCaseOwnerClientId ? `client:${nextCaseOwnerClientId}` : nextCaseOwnerLegalEntityId ? `legal:${nextCaseOwnerLegalEntityId}` : '';
    if (nextType !== form.type && nextCaseOwnerKey !== caseOwnerKey) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setClientCasesLoadedOwnerKey('');
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      type: nextType,
      expenseClientId: nextType === 'Income' ? '' : prev.expenseClientId,
      expenseLegalEntityId: nextType === 'Income' ? '' : prev.expenseLegalEntityId,
    }));
  };

  const openClientPicker = (mode: InvoiceCounterpartyPickerMode = 'main') => {
    if (!canSelectCounterparty) return;
    setClientPickerMode(mode);
    setClientPickerOpen(true);
  };

  const handleClientPick = (client: Client) => {
    const nextClientId = String(client.id);
    const clientChanged = form.clientId !== nextClientId;
    const nextCaseOwnerClientId = resolveCaseOwnerClientId(form.type, nextClientId, form.expenseClientId);
    const nextCaseOwnerKey = nextCaseOwnerClientId ? `client:${nextCaseOwnerClientId}` : form.legalEntityId ? `legal:${form.legalEntityId}` : '';
    if (clientChanged || form.legalEntityId) {
      lastAutoContractClientIdRef.current = '';
      setSelectedActs([]);
      setActOptions([]);
      if (nextCaseOwnerKey !== caseOwnerKey) {
        setSelectedClientCaseIds([]);
        setClientCaseOptions([]);
        setClientCasesLoadedOwnerKey('');
      }
      setCaseDropdownOpen(false);
      setActDropdownOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      clientId: nextClientId,
      legalEntityId: '',
      contractId: clientChanged || prev.legalEntityId ? '' : prev.contractId,
    }));
    setClientPickerOpen(false);
  };

  const handleLegalEntityPick = (entity: LegalEntitySummary) => {
    const nextLegalEntityId = String(entity.id);
    const legalEntityChanged = form.legalEntityId !== nextLegalEntityId;
    setLocalError(null);
    if (legalEntityChanged || form.clientId) {
      lastAutoContractClientIdRef.current = '';
      setSelectedActs([]);
      setActOptions([]);
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setClientCasesLoadedOwnerKey('');
      setCaseDropdownOpen(false);
      setActDropdownOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      clientId: '',
      legalEntityId: nextLegalEntityId,
      contractId: '',
    }));
    setClientPickerOpen(false);
  };

  const handleExpenseClientChange = (nextClientId: string) => {
    const nextCaseOwnerClientId = resolveCaseOwnerClientId(form.type, form.clientId, nextClientId);
    if (form.expenseClientId !== nextClientId && nextCaseOwnerClientId !== caseOwnerClientId) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setClientCasesLoadedOwnerKey('');
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }

    setForm((prev) => ({
      ...prev,
      expenseClientId: nextClientId,
      expenseLegalEntityId: nextClientId ? '' : prev.expenseLegalEntityId,
    }));
  };

  const handleExpenseLegalEntityPick = (entity: LegalEntitySummary | null) => {
    const nextLegalEntityId = entity ? String(entity.id) : '';
    const nextCaseOwnerKey = nextLegalEntityId ? `legal:${nextLegalEntityId}` : '';

    if ((form.expenseLegalEntityId !== nextLegalEntityId || form.expenseClientId) && nextCaseOwnerKey !== caseOwnerKey) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setClientCasesLoadedOwnerKey('');
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }

    setForm((prev) => ({
      ...prev,
      expenseClientId: '',
      expenseLegalEntityId: nextLegalEntityId,
    }));
    setClientPickerOpen(false);
  };

  const clearExpenseCounterparty = () => {
    if (form.expenseClientId || form.expenseLegalEntityId) {
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setClientCasesLoadedOwnerKey('');
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
    }

    setForm((prev) => ({
      ...prev,
      expenseClientId: '',
      expenseLegalEntityId: '',
    }));
    setClientPickerOpen(false);
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
    const operation = open && createCaseOpen && canCreateRelatedCase ? caseCreation.begin('case') : null;
    if (!operation) throw new StaleUiOperationError();
    try {
      if (!caseOwnerClientIdNumber && !caseOwnerLegalEntityIdNumber) {
        throw new Error(isIncome ? 'Сначала выберите клиента' : 'Сначала выберите клиента, к которому относится расход');
      }

      const created = await awaitOwnedUiResult(() => apiService.createCase({
        clientId: caseOwnerClientIdNumber,
        legalEntityId: caseOwnerLegalEntityIdNumber,
        title: patch.title?.trim() ?? '',
        description: patch.description?.trim(),
        status: patch.status ?? 'Open',
        caseNumber: patch.caseNumber?.trim() ?? '',
        caseType: patch.caseType?.trim() ?? '',
        stage: patch.stage?.trim() ?? '',
        responsibleUserId: patch.responsibleUserId ?? null,
        startDate: patch.startDate ?? null,
        dueDate: patch.dueDate ?? null,
        feeAmount: patch.feeAmount ?? 0,
        priority: patch.priority ?? 'normal',
        courtName: patch.courtName?.trim() ?? '',
        courtCaseNumber: patch.courtCaseNumber?.trim() ?? '',
        courtUrl: patch.courtUrl?.trim() ?? '',
        nextHearingAt: patch.nextHearingAt ?? null,
        claimant: patch.claimant?.trim() ?? '',
        defendant: patch.defendant?.trim() ?? '',
        thirdParties: patch.thirdParties?.trim() ?? '',
        representatives: patch.representatives?.trim() ?? '',
        judgeName: patch.judgeName?.trim() ?? '',
        courtroom: patch.courtroom?.trim() ?? '',
        courtSecretaryContacts: patch.courtSecretaryContacts?.trim() ?? '',
        claimFiledAt: patch.claimFiledAt ?? null,
        appealDeadlineAt: patch.appealDeadlineAt ?? null,
        motionDeadlineAt: patch.motionDeadlineAt ?? null,
        objectionDeadlineAt: patch.objectionDeadlineAt ?? null,
        documentsSummary: patch.documentsSummary?.trim() ?? '',
        eventHistory: patch.eventHistory?.trim() ?? '',
        strategy: patch.strategy?.trim() ?? '',
      }), operation.isCurrent);
      if (!created || !Number.isInteger(created.id) || created.id <= 0
        || !isClientCaseLinkedToOwner(created, caseOwnerClientIdNumber, caseOwnerLegalEntityIdNumber))
        throw new Error('Не удалось подтвердить принадлежность созданного дела. Откройте список дел повторно.');

      // A list requested before this successful creation must not erase the new case.
      ++clientCasesLoadGeneration.current;
      setClientCasesLoading(false);
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
    } finally { operation.finish(); }
  };

  const handleCreateAct = async () => {
    if (openActCreatePage()) {
      return;
    }

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
        clientId: form.clientId ? Number(form.clientId) : null,
        legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
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
        legalEntityId: created.legalEntityId ?? undefined,
        legalEntityName: created.legalEntityName ?? undefined,
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
        date: form.date || defaultDate,
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
    setForm((prev) => {
      if (
        field === 'date'
        && !installmentPlan
        && !hasGeneratedRelatedPayments
        && (!prev.dueDate || prev.dueDate === prev.date)
      ) {
        return { ...prev, date: value, dueDate: value };
      }

      return { ...prev, [field]: value };
    });
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
    () => buildInvoiceStatusSnapshot(
      relatedPayments,
      form.status,
      form.dueDate,
      form.amount,
      invoice?.paidAmount,
      invoice?.outstandingAmount,
      defaultDate,
    ),
    [defaultDate, form.amount, form.dueDate, form.status, invoice?.outstandingAmount, invoice?.paidAmount, relatedPayments],
  );
  const hasGeneratedRelatedPayments = useMemo(
    () => relatedPayments.some((payment) => isGeneratedInstallmentPayment(payment)),
    [relatedPayments],
  );
  const dueDateLockedByInstallmentPlan = Boolean(installmentPlan) || hasGeneratedRelatedPayments;
  const cancellationInstallmentStats = useMemo(() => {
    const generated = relatedPayments.filter((payment) => isGeneratedInstallmentPayment(payment));
    const active = generated.filter((payment) => payment.status !== 'Cancelled');
    const paid = active.filter(paymentHasReceivedMoney);
    const unpaid = active.filter((payment) => !paymentHasReceivedMoney(payment));

    return {
      generatedCount: generated.length,
      paidCount: paid.length,
      unpaidCount: unpaid.length,
      paidAmount: paid.reduce((sum, payment) => sum + resolvePaidAmount(payment), 0),
      unpaidAmount: unpaid.reduce((sum, payment) => sum + resolveOutstandingAmount(payment), 0),
      canPreservePaidInstallments: paid.length > 0 && unpaid.length > 0,
    };
  }, [relatedPayments]);
  const cancelDialogHasReceivedMoney = statusSnapshot.paidAmount > 0 || cancellationInstallmentStats.paidCount > 0;

  const effectiveInvoiceStatus = statusSnapshot.status;
  const installmentPlanAmountMismatch = useMemo(() => {
    if (!installmentPlan) return false;
    return Math.abs(parseMoneyInput(form.amount) - installmentPlan.total) > 0.009;
  }, [form.amount, installmentPlan]);
  const requiresFinancialActionReason = Boolean(currentUser?.requiresFinancialActionReason);

  const requestFinancialActionReason = useCallback(async (actionName: string): Promise<string | null | undefined> => {
    if (!requiresFinancialActionReason) {
      return null;
    }

    const reason = await prompt({
      title: 'Причина изменения',
      description: 'Причина будет сохранена в журнале.',
      label: `Укажите причину ${actionName}`,
      multiline: true,
      confirmLabel: 'Продолжить',
      cancelLabel: 'Отмена',
    });
    if (reason === null) {
      return undefined;
    }

    const normalizedReason = reason.trim();
    if (!normalizedReason) {
      setLocalError('Причина обязательна для записи в журнал.');
      return undefined;
    }

    return normalizedReason;
  }, [prompt, requiresFinancialActionReason]);

  const requestAmountReconciliationDecision = useCallback((
    details: InvoiceAmountReconciliation,
  ): Promise<InvoiceAmountReconciliationDecision> => {
    setAmountReconciliationDialog(details);
    return new Promise((resolve) => {
      amountReconciliationResolverRef.current = resolve;
    });
  }, []);

  const resolveAmountReconciliationDecision = useCallback((decision: InvoiceAmountReconciliationDecision) => {
    amountReconciliationResolverRef.current?.(decision);
    amountReconciliationResolverRef.current = null;
    setAmountReconciliationDialog(null);
  }, []);

  const focusRelatedPaymentsAfterAmountMismatch = useCallback((details: InvoiceAmountReconciliation) => {
    setLocalError(
      details.direction === 'increase'
        ? `Сумма счета больше выставленных платежей на ${formatCurrencySmart(details.difference).full}. Добавьте недостающий платеж или сохраните счет без изменения платежей.`
        : `Сумма счета меньше выставленных платежей на ${formatCurrencySmart(details.difference).full}. Удалите или отредактируйте лишние платежи вручную.`,
    );
    relatedPaymentsSectionRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });

    if (details.direction === 'increase' && onAddRelatedPayment) {
      onAddRelatedPayment();
    }
  }, [onAddRelatedPayment]);

  type BuildInvoicePayloadOptions = {
    statusOverride?: PaymentStatus;
    planOverride?: InstallmentPlanInput | null;
    dueDateOverride?: string | null;
    replaceInstallmentPlan?: boolean;
    includeActLinks?: boolean;
  };

  const buildInvoicePayload = useCallback((
    options: BuildInvoicePayloadOptions = {},
  ): InvoiceInput => {
    const { statusOverride, planOverride, dueDateOverride, replaceInstallmentPlan, includeActLinks = true } = options;
    const safeAmount = parseMoneyInput(form.amount);
    const clientCaseIds = filterClientCaseIdsForOwner(
      selectedClientCaseIds,
      clientCaseOptions,
      caseOwnerClientIdNumber,
      caseOwnerLegalEntityIdNumber,
    );
    const actIds = includeActLinks
      ? selectedActs.map((item) => item.id).filter((id) => Number.isFinite(id) && id > 0)
      : [];
    const effectiveInstallmentPlan = planOverride === undefined ? installmentPlan : planOverride;
    const effectiveDueDate = dueDateOverride === undefined ? form.dueDate : (dueDateOverride ?? '');
    const shouldReplaceInstallmentPlan =
      replaceInstallmentPlan
      ?? (mode === 'create'
        ? Boolean(effectiveInstallmentPlan)
        : installmentPlanTouched);

    return {
      ...(mode === 'create' && form.type !== 'Expense'
        ? { sellerCompanyId: sellerSelection.companyId, sellerBranchId: sellerSelection.branchId } : {}),
      number: form.number.trim() || String(invoice?.number ?? ''),
      date: fromInputToApiDate(form.date) ?? form.date,
      dueDate: effectiveDueDate ? fromInputToApiDate(effectiveDueDate) ?? effectiveDueDate : undefined,
      amount: safeAmount,
      type: form.type,
      status: statusOverride ?? form.status,
      workflowStatus: statusOverride ? deriveInvoiceWorkflowStatus(statusOverride, safeAmount, form.workflowStatus) : form.workflowStatus,
      clientId: form.clientId ? Number(form.clientId) : null,
      legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
      expenseClientId: form.type === 'Expense' && form.expenseClientId ? Number(form.expenseClientId) : null,
      expenseLegalEntityId: form.type === 'Expense' && form.expenseLegalEntityId ? Number(form.expenseLegalEntityId) : null,
      contractId: form.contractId ? Number(form.contractId) : undefined,
      clientCaseId: clientCaseIds[0] ?? null,
      clientCaseIds,
      billingTaskIds: resumeDraft?.billingTaskIds,
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
          vatRate: item.vatRate ? item.vatRate : null,
        }))
        .filter((item) => item.name && Number.isFinite(item.quantity) && Number.isFinite(item.price) && item.quantity >= 0 && item.price >= 0),
      replaceInstallmentPlan: shouldReplaceInstallmentPlan || undefined,
      installmentPlan: shouldReplaceInstallmentPlan
        ? (effectiveInstallmentPlan ? cloneInstallmentPlan(effectiveInstallmentPlan) : null)
        : undefined,
    };
  }, [
    caseOwnerClientIdNumber,
    caseOwnerLegalEntityIdNumber,
    clientCaseOptions,
    form,
    installmentPlan,
    installmentPlanTouched,
    invoice?.number,
    lineItems,
    mode,
    selectedActs,
    selectedClientCaseIds,
    sellerSelection,
    resumeDraft?.billingTaskIds,
  ]);

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
    sellerSelection,
    invoiceId: invoice?.id ?? null,
    mode,
    presentation: isPageMode ? 'page' : 'modal',
    pageViewMode,
    form,
    lineItems,
    selectedActs,
    selectedClientCaseIds,
    billingTaskIds: resumeDraft?.billingTaskIds,
    installmentPlan,
  }), [form, installmentPlan, invoice?.id, isPageMode, lineItems, mode, pageViewMode, resumeDraft?.billingTaskIds, selectedActs, selectedClientCaseIds, sellerSelection]);

  const openActCreatePage = useCallback(() => {
    if (!onCreateActPage) {
      return false;
    }

    const linkedInvoiceId = typeof invoice?.id === 'number' && invoice.id > 0 ? invoice.id : null;
    const resolvedClientId =
      form.type === 'Expense'
        ? (form.expenseClientId ? Number(form.expenseClientId) : form.clientId ? Number(form.clientId) : null)
        : (form.clientId ? Number(form.clientId) : form.expenseClientId ? Number(form.expenseClientId) : null);
    const resolvedLegalEntityId =
      form.type === 'Expense'
        ? (form.expenseLegalEntityId ? Number(form.expenseLegalEntityId) : form.legalEntityId ? Number(form.legalEntityId) : null)
        : (form.legalEntityId ? Number(form.legalEntityId) : form.expenseLegalEntityId ? Number(form.expenseLegalEntityId) : null);

    if (isPageMode) {
      storeInvoiceEditorDraft(buildInvoiceEditorDraft());
    }
    onCreateActPage(
      {
        number: draftAct.number.trim() || form.number.trim(),
        title: draftAct.title.trim() || form.description.trim(),
        date: draftAct.date || form.date || defaultDate,
        amount: parseMoneyInput(draftAct.amount || form.amount),
        creationMode: linkedInvoiceId ? 'Full' : 'Manual',
        invoiceId: linkedInvoiceId,
        invoiceNumber: form.number.trim() || undefined,
        clientId: resolvedClientId,
        legalEntityId: resolvedLegalEntityId,
        contractId: form.contractId ? Number(form.contractId) : null,
        responsibleId: selectedResponsibleId ? Number(selectedResponsibleId) : null,
        status: 'Created',
        comment: draftAct.comment.trim() || undefined,
      },
      {
        invoiceId: linkedInvoiceId,
        invoiceSeed: invoice ?? null,
      },
    );

    return true;
  }, [
    buildInvoiceEditorDraft,
    draftAct.amount,
    draftAct.comment,
    draftAct.date,
    draftAct.number,
    draftAct.title,
    form.amount,
    form.clientId,
    form.contractId,
    form.date,
    form.description,
    form.expenseClientId,
    form.expenseLegalEntityId,
    form.legalEntityId,
    form.number,
    form.type,
    invoice,
    isPageMode,
    onCreateActPage,
    selectedResponsibleId,
    defaultDate,
  ]);

  const handleOpenClientCard = (clientId: number) => {
    storeInvoiceEditorDraft(buildInvoiceEditorDraft());
    onOpenClient?.(clientId);
  };

  const handleOpenActPage = useCallback((act: ActOption) => {
    if (!onOpenActPage || !(act.id > 0)) {
      return;
    }

    storeInvoiceEditorDraft(buildInvoiceEditorDraft());
    onOpenActPage(act.id, {
      invoiceId: invoice?.id ?? null,
    });
  }, [buildInvoiceEditorDraft, invoice?.id, onOpenActPage]);

  const paymentsReadError = paymentsError || (mode === 'edit' && !canViewPayments ? 'Недостаточно прав для просмотра платежей.' : null);
  const paymentsReadBlocked = mode === 'edit' && canViewPayments && (paymentsLoading || Boolean(paymentsError));
  const renderPaymentsError = () => <div role="alert" className="space-y-2 text-sm text-red-700">
    <p>{paymentsReadError}</p>
    {onRetryPayments ? <button type="button" disabled={paymentsLoading}
      onClick={() => { void Promise.resolve(onRetryPayments()).catch(() => undefined); }}
      className="rounded-lg border border-red-200 px-2 py-1 text-xs font-semibold disabled:opacity-60">Повторить загрузку платежей</button> : null}
  </div>;
  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);
    if (paymentsReadBlocked) {
      setLocalError('Сначала дождитесь загрузки платежей по счёту или повторите её после ошибки.');
      return;
    }

    const number = form.number.trim();
    if (!number) {
      setLocalError(t('invoiceNumber') ?? 'Номер счёта обязателен');
      return;
    }
    if (!form.date) {
      setLocalError(t('invoiceDate') ?? 'Дата обязательна');
      return;
    }
    if (!form.clientId && !form.legalEntityId) {
      setLocalError(`Выберите ${counterpartyObjectLabel}`);
      return;
    }

    const parsedAmount = parseMoneyInput(form.amount);
    if (Number.isNaN(parsedAmount) || parsedAmount < 0) {
      setLocalError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    if (lineItems.some((item) => item.name.trim() && !hasStorableLineItemPrecision(parseMoneyInput(item.quantity), parseMoneyInput(item.price)))) {
      setLocalError(lineItemPrecisionError);
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

    const amountReconciliation = buildInvoiceAmountReconciliation(relatedPayments, invoice?.amount, parsedAmount);
    if (amountReconciliation) {
      const decision = await requestAmountReconciliationDecision(amountReconciliation);
      if (decision === 'cancel') {
        return;
      }

      if (decision === 'manage') {
        focusRelatedPaymentsAfterAmountMismatch(amountReconciliation);
        return;
      }
    }

    const currentOwnerKey = form.clientId
      ? `client:${form.clientId}`
      : form.legalEntityId
        ? `legal:${form.legalEntityId}`
        : '';
    const originalOwnerKey = invoice?.clientId
      ? `client:${invoice.clientId}`
      : invoice?.legalEntityId
        ? `legal:${invoice.legalEntityId}`
        : '';
    if (
      mode === 'edit'
      && invoice
      && currentOwnerKey
      && originalOwnerKey
      && currentOwnerKey !== originalOwnerKey
      && hasGeneratedRelatedPayments
    ) {
      const accepted = await confirm({
        title: 'Смена контрагента счета',
        description:
          'У счета есть сформированные платежи. При смене контрагента они тоже будут перепривязаны. Продолжить?',
        confirmLabel: 'Да, изменить',
        cancelLabel: 'Отмена',
      });

      if (!accepted) {
        return;
      }
    }

    const dueDateResolution = await resolveInstallmentDueDateForSave();
    if (dueDateResolution.cancelled) {
      return;
    }

    const payload = buildInvoicePayload({ dueDateOverride: dueDateResolution.dueDateOverride });
    payload.number = number;

    if (
      mode === 'edit'
      && invoice
      && (
        payload.status !== invoice.status
        || payload.workflowStatus !== (invoice.workflowStatus ?? form.workflowStatus)
      )
    ) {
      const reason = await requestFinancialActionReason('изменения статуса счета');
      if (reason === undefined) {
        return;
      }
      payload.actionReason = reason;
    }

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

  const openPendingDocumentWindow = () => {
    if (typeof window === 'undefined') return;
    const previewWindow = window.open('', '_blank', 'width=1180,height=900');
    if (!previewWindow) {
      throw new Error('Браузер заблокировал окно печати. Разрешите всплывающие окна и повторите.');
    }

    previewWindow.document.open();
    previewWindow.document.write(`
      <!doctype html>
      <html lang="ru">
        <head>
          <meta charset="utf-8" />
          <title>Подготавливаем документ…</title>
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
          <div class="box">Подготавливаем печатную форму…</div>
        </body>
      </html>
    `);
    previewWindow.document.close();

    return previewWindow;
  };

  const printHtmlInWindow = (html: string, previewWindow: Window | null | undefined, owner: DocumentDeliveryRead) => {
    if (!previewWindow || previewWindow.closed) {
      throw new Error('Окно печати было закрыто до загрузки документа. Повторите ещё раз.');
    }

    previewWindow.document.open();
    previewWindow.document.write(html);
    previewWindow.document.close();
    window.setTimeout(() => {
      if (!owner.isCurrent()) { if (!previewWindow.closed) previewWindow.close(); return; }
      if (previewWindow.closed) return;
      try {
        previewWindow.focus();
        previewWindow.print();
      } catch (error) {
        console.error('html print failed', error);
      }
    }, 250);
  };

  const deferClose = useCallback((callback: () => void) => {
    window.setTimeout(callback, 160);
  }, []);

  const requestDocumentCopy = (format: 'print' | 'word' | 'pdf', combined?: boolean, templateKey?: string) => {
    if (!invoice?.id) {
      toast.error('Сначала сохраните счёт, затем можно подготовить документ.');
      return;
    }
    if (!canExport || !canReadPrint) return;
    const owner = printOwner.capture();
    if (!owner) return;
    (format === 'print' ? printButtonRef.current : downloadMenuRef.current?.querySelector('button'))?.focus();
    setPrintMenuOpen(false);
    setDownloadMenuOpen(false);
    setImagePrintRequest({ documentId: invoice.id, scope: printScope, owner, generation: ++printRequestGeneration.current, format, templateKey, useDefaults: combined === undefined,
      initialOptions: { includeLogo: true, includeSeal: combined ?? false, includeSignature: combined ?? false } });
  };

  const handlePrintDocument = (templateKey?: string, combined = true) => requestDocumentCopy('print', combined, templateKey);

  const confirmDocumentCopy = async (images: DocumentImagePrintOptions, signal: AbortSignal) => {
    const request = imagePrintRequest;
    if (!request || !request.owner.isCurrent() || request.documentId !== invoice?.id || !canReadPrint || !canExport || request.scope !== printScopeRef.current) throw new Error('Документ или права изменились. Откройте карточку повторно.');
    const requireCurrentCopy = () => {
      signal.throwIfAborted();
      if (!request.owner.isCurrent() || request.scope !== printScopeRef.current) throw new Error('Документ или права изменились. Откройте карточку повторно.');
    };
    let previewWindow: Window | null | undefined;
    const closePendingWindow = () => { if (previewWindow && !previewWindow.closed) previewWindow.close(); };
    try {
      requireCurrentCopy();
      if (request.format === 'print') previewWindow = openPendingDocumentWindow();
      signal.addEventListener('abort', closePendingWindow, { once: true });
      const options = { ...images, ...(request.templateKey ? { templateKey: request.templateKey } : {}), signal };
      const blob = request.format === 'print'
        ? await apiService.prepareDocumentPrint('invoices', request.documentId, options)
        : await apiService.downloadInvoiceDocument(request.documentId, request.format, options);
      requireCurrentCopy();
      if (request.format === 'print') {
        const html = await blob.text();
        requireCurrentCopy();
        printHtmlInWindow(html, previewWindow, request.owner);
      } else {
        downloadBlob(blob, buildInvoiceDocumentFileName(invoice.number, invoice.date, request.format, request.templateKey));
      }
    } catch (cause) {
      closePendingWindow();
      throw cause;
    } finally {
      signal.removeEventListener('abort', closePendingWindow);
    }
  };

  const handleUploadDocumentToYandexDisk = async () => {
    if (!open || !canSendToDisk) return;
    if (!invoice?.id) {
      toast.error('Сначала сохраните счёт, затем можно отправить документ на Яндекс.Диск.');
      return;
    }

    const operation = delivery.begin('disk');
    if (!operation) return;
    try {
      const result = await apiService.uploadInvoiceDocumentToYandexDisk(invoice.id, 'pdf');
      if (operation.isCurrent()) toast.success(`Счёт отправлен на Яндекс.Диск: ${result.path}.`, 'Я.Диск');
    } catch (err) {
      if (operation.isCurrent()) toast.error(extractDisplayErrorMessage(err, 'Не удалось отправить счёт на Яндекс.Диск.'), 'Я.Диск');
    } finally {
      operation.finish();
    }
  };


  const openCancelInvoiceDialog = () => {
    if (!invoice?.id) {
      toast.error('Сначала сохраните счёт, затем можно аннулировать его.');
      return;
    }

    setCancelReason('');
    setCancelScope(cancellationInstallmentStats.canPreservePaidInstallments ? 'UnpaidInstallmentsOnly' : 'All');
    setCancelMoneyAction(cancelDialogHasReceivedMoney ? 'LeaveAsAdvance' : 'Reject');
    setCancelTargetPaymentId('');
    setCancelRefundSource('Bank');
    setCancelRefundDate(defaultDate);
    setCancelError(null);
    setCancelDialogOpen(true);
  };

  const handleCancelInvoiceSubmit = async () => {
    if (!invoice?.id) {
      setCancelError('Сначала сохраните счёт, затем можно аннулировать его.');
      return;
    }

    const reason = cancelReason.trim();
    if (!reason) {
      setCancelError('Укажите причину отмены.');
      return;
    }

    const isUnpaidOnly = cancelScope === 'UnpaidInstallmentsOnly';
    let receivedMoneyAction: InvoiceCancellationMoneyAction = 'Reject';
    let targetPaymentId: number | null = null;
    let refundSource: InvoiceCancellationRefundSource | null = null;
    let refundDate: string | null = null;

    if (!isUnpaidOnly && cancelDialogHasReceivedMoney) {
      receivedMoneyAction = cancelMoneyAction;
      if (receivedMoneyAction === 'Reallocate') {
        const parsedTargetPaymentId = Number(cancelTargetPaymentId);
        if (!Number.isInteger(parsedTargetPaymentId) || parsedTargetPaymentId <= 0) {
          setCancelError('Для зачёта укажите положительный ID счёта.');
          return;
        }
        targetPaymentId = parsedTargetPaymentId;
      }

      if (receivedMoneyAction === 'Refund') {
        refundSource = cancelRefundSource;
        refundDate = cancelRefundDate || defaultDate;
      }
    }

    setCancelSubmitting(true);
    setCancelError(null);
    try {
      const updated = await apiService.cancelInvoice(
        invoice.id,
        reason,
        receivedMoneyAction,
        targetPaymentId,
        refundSource,
        refundDate,
        cancelScope,
      );
      setForm((prev) => ({
        ...prev,
        status: updated.status,
        workflowStatus: updated.workflowStatus ?? prev.workflowStatus,
      }));
      await onRefreshRequested?.();
      setCancelDialogOpen(false);
      toast.success(
        isUnpaidOnly
          ? 'Неоплаченные платежи графика отменены, оплаченные оставлены.'
          : 'Счёт аннулирован.',
        'Счёт обновлён',
      );
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось отменить счёт.');
      setCancelError(message);
      toast.error(message);
    } finally {
      setCancelSubmitting(false);
    }
  };

  const handlePageAction = async (action: 'send' | 'markNotSigned' | 'sendToSigning' | 'markSigned' | 'copy' | 'paid' | 'cancel' | 'delete') => {
    if (action === 'send') {
      if (!open || !canReadPrint || !canExport || !invoice?.id) return;
      const operation = delivery.begin('email');
      if (!operation) return;
      try {
        const accepted = await confirm({ title: 'Отправить счёт', description: 'Сформировать документ WORD и отправить на email клиента?', confirmLabel: 'Отправить', cancelLabel: 'Отмена', variant: 'primary' });
        if (!accepted || !operation.isCurrent()) return;
        await apiService.sendInvoiceByEmail(invoice.id, { format: 'word', email: selectedClient?.email || undefined });
        if (operation.isCurrent()) toast.success(selectedClient?.email ? `Документ WORD отправлен на ${selectedClient.email}.` : 'Документ WORD отправлен клиенту по email.', 'Счет отправлен');
      } catch (err) {
        if (operation.isCurrent()) toast.error(extractDisplayErrorMessage(err, 'Не удалось выполнить отправку счёта.'));
      } finally { operation.finish(); }
      return;
    }
    if (action === 'cancel') {
      openCancelInvoiceDialog();
      return;
    }

    const actionMeta = {
      send: { title: 'Отправить счёт', message: 'Сформировать документ WORD и отправить на email клиента?', confirmLabel: 'Отправить' },
      copy: { title: 'Копия счёта', message: 'Создать копию текущего счёта?', confirmLabel: 'Копировать' },
      paid: { title: 'Отметить оплаченным', message: 'Отметить счёт как оплаченный?', confirmLabel: 'Оплачен' },
      delete: { title: 'Удалить счёт', message: 'Удалить счёт без возможности восстановления?', confirmLabel: 'Удалить' },
      markNotSigned: { title: 'Отметить неподписанным', message: 'Перевести счёт в статус “не подписан”?', confirmLabel: 'Не подписан' },
      sendToSigning: { title: 'Отправить на подписание', message: 'Перевести счёт в статус “на подписании”?', confirmLabel: 'На подписание' },
      markSigned: { title: 'Отметить подписанным', message: 'Отметить счёт как подписанный клиентом?', confirmLabel: 'Подписан' },
    }[action];

    const accepted = await confirm({
      title: actionMeta.title,
      description: actionMeta.message,
      confirmLabel: actionMeta.confirmLabel,
      cancelLabel: 'Отмена',
      variant: action === 'delete' ? 'danger' : 'primary',
    });
    if (!accepted) return;

    try {
      if (action === 'markNotSigned' || action === 'sendToSigning' || action === 'markSigned') {
        if (!invoice?.id) {
          throw new Error('Сначала сохраните счёт, затем можно менять статус подписания.');
        }

        const workflowStatus: InvoiceWorkflowStatus =
          action === 'sendToSigning'
            ? 'OnSigning'
            : action === 'markSigned'
              ? 'SignedByClient'
              : 'NotSigned';
        const reason = await requestFinancialActionReason('смены статуса документа');
        if (reason === undefined) return;

        const updated = await apiService.updateInvoiceWorkflowStatus(
          invoice.id,
          workflowStatus,
          reason,
        );

        setForm((prev) => ({
          ...prev,
          workflowStatus: updated.workflowStatus ?? prev.workflowStatus,
        }));
        await onRefreshRequested?.();
        toast.success(
          action === 'sendToSigning'
            ? 'Счёт отправлен на подписание.'
            : action === 'markSigned'
              ? 'Счёт отмечен подписанным.'
              : 'Счёт отмечен неподписанным.',
          'Статус счёта обновлён',
        );
        return;
      }

      if (action === 'copy') {
        const payload = buildInvoicePayload({
          replaceInstallmentPlan: Boolean(installmentPlan ?? relatedInstallmentPlan),
          includeActLinks: false,
        });
        payload.number = `${form.number || 'Счёт'} (копия)`;
        await apiService.createInvoice(payload);
        await onRefreshRequested?.();
        toast.success('Создана копия текущего счета.', 'Копия создана');
        return;
      }

      if (action === 'paid' && invoice?.id) {
        const nextStatus: PaymentStatus = 'Completed';
        const payload = buildInvoicePayload({ statusOverride: nextStatus });
        const reason = await requestFinancialActionReason('изменения статуса оплаты');
        if (reason === undefined) {
          return;
        }
        payload.actionReason = reason;
        await apiService.updateInvoice(invoice.id, payload);
        setForm((prev) => ({ ...prev, status: nextStatus }));
        setDisplayPayments((prev) => prev.map((payment) => (
          payment.documentKind === 'Invoice'
            ? {
                ...payment,
                status: nextStatus,
                paidDate: nextStatus === 'Completed'
                  ? (form.paidDate || payment.paidDate || defaultDate)
                  : undefined,
              }
            : payment
        )));
        await onRefreshRequested?.();
        toast.success('Статус счета обновлен: оплачен.', 'Счет обновлен');
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
    Processing: { label: 'Частично оплачен', className: 'bg-blue-100 text-blue-700 border border-blue-200' },
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
    Processing: t('processingStatus') ?? 'Частично оплачено',
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
  const currentWorkflowStatus = form.workflowStatus;
  const currentWorkflowStatusMeta = {
    label: invoiceWorkflowStatusLabels[currentWorkflowStatus] ?? DEFAULT_INVOICE_WORKFLOW_STATUS_LABELS[currentWorkflowStatus],
    className: INVOICE_WORKFLOW_STATUS_CLASSES[currentWorkflowStatus] ?? INVOICE_WORKFLOW_STATUS_CLASSES.Draft,
  };
  void statusMeta;
  void relatedPaymentStatusLabel;
  const canManageRelatedPayments = mode === 'edit' && !!invoice?.invoiceEntityId;
  const canShowRelatedPaymentActions = mode === 'edit' && !!onAddRelatedPayment;
  const createCaseStub = useMemo<ClientCase>(() => ({
    id: 0,
    clientId: caseOwnerClientIdNumber,
    legalEntityId: caseOwnerLegalEntityIdNumber,
    title: '',
    description: '',
    status: 'Open',
    createdAt: new Date().toISOString(),
  }), [caseOwnerClientIdNumber, caseOwnerLegalEntityIdNumber]);
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
                  if (!canReadCaseOptions || !caseReadSession.authenticated || !isCaseReadOwnerCurrent(caseReadOwnerAtRender)
                    || !isAuthSessionIdentityCurrent(caseReadSession)) return;
                  setCaseDropdownOpen((prev) => !prev);
                  setActDropdownOpen(false);
                }}
                disabled={!caseOwnerKey || !canReadCaseOptions || !caseReadSession.authenticated}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Создать дело"
                aria-label="Создать дело"
                onClick={() => { if (canCreateRelatedCase && !caseCreation.busy) setCreateCaseOpen(true); }}
                disabled={!caseOwnerKey || !canCreateRelatedCase || caseCreation.busy}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!caseOwnerKey ? (
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
                          {clientCaseLabel(selectedCase ?? {
                            id,
                            clientId: selectedCaseOwnerClient?.id ?? null,
                            legalEntityId: selectedCaseOwnerLegalEntity?.id ?? null,
                            title: '',
                            status: '',
                            createdAt: '',
                          })}
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
                disabled={!form.clientId && !form.legalEntityId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Создать акт"
                aria-label="Создать акт"
                onClick={() => {
                  if (openActCreatePage()) {
                    return;
                  }
                  setDraftAct((prev) => ({
                    ...prev,
                    date: form.date || prev.date,
                    amount: form.amount || prev.amount,
                  }));
                  setCreateActError(null);
                  setCreateActOpen(true);
                }}
                disabled={!form.clientId && !form.legalEntityId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!form.clientId && !form.legalEntityId ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Сначала выберите {counterpartyObjectLabel}.
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
                    У выбранного {counterpartyObjectLabel} пока нет актов.
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
    ? serviceCategoryById.get(Number(activeLineItemLookup.serviceCategoryId)) ?? `Выбранная категория #${activeLineItemLookup.serviceCategoryId}`
    : 'Все категории';
  const activeLineItemSelectedServiceId = activeLineItemLookup
    ? String(resolveLineItemTemplate(activeLineItemLookup)?.id ?? '')
    : '';
  const canApplyCustomServiceName = Boolean(lineItemLookupState)
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
              onClick={() => leaveInvoiceCard(onBack ?? onClose)}
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
            <span
              data-testid="invoice-header-workflow-status-badge"
              title="Статус документа"
              className={`rounded-full px-3 py-1 text-sm font-semibold ${currentWorkflowStatusMeta.className}`}
            >
              {currentWorkflowStatusMeta.label}
            </span>
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
                  disabled={submitting || paymentsReadBlocked}
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
            onClick={() => leaveInvoiceCard(onClose)}
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
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="text-xl font-semibold">{printableAmount}</span>
                  <div className="flex flex-wrap items-center justify-end gap-2">
                    <span data-testid="invoice-page-status-badge" title="Статус оплаты" className={`rounded px-3 py-1 text-sm font-semibold ${currentStatusMeta.className}`}>{currentStatusMeta.label}</span>
                    <span data-testid="invoice-page-workflow-status-badge" title="Статус документа" className={`rounded px-3 py-1 text-sm font-semibold ${currentWorkflowStatusMeta.className}`}>{currentWorkflowStatusMeta.label}</span>
                  </div>
                </div>
                <div data-testid="invoice-page-payment-summary" className="mt-3 grid grid-cols-2 gap-2">
                  <div className={`rounded-lg border px-3 py-2 text-center ${statusSnapshot.outstandingAmount > 0 ? 'border-rose-200 bg-rose-50' : 'border-slate-200 bg-slate-50'}`}>
                    <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Долг</div>
                    <div data-testid="invoice-page-debt-amount" className={`mt-1 text-sm font-semibold tabular-nums ${statusSnapshot.outstandingAmount > 0 ? 'text-rose-700' : 'text-slate-700'}`}>
                      {formatCurrencySmart(statusSnapshot.outstandingAmount).short}
                    </div>
                  </div>
                  <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-center">
                    <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Выплачено</div>
                    <div data-testid="invoice-page-paid-amount" className="mt-1 text-sm font-semibold tabular-nums text-emerald-700">
                      {formatCurrencySmart(statusSnapshot.paidAmount).short}
                    </div>
                  </div>
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
                    {paymentsReadError ? renderPaymentsError() : paymentsLoading ? (
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
                          const appliedClientBalanceAmount = getAppliedClientBalanceAmount(payment.systemNotes);
                          const expectedPaymentDate = resolveRelatedPaymentExpectedDate(payment);
                          const actualPaymentDate = resolveRelatedPaymentActualDate(payment);
                          const installmentSequenceLabel = installmentMeta
                            ? `${installmentMeta.sequence}/${Math.max(installmentMeta.months, installmentMeta.sequence)}`
                            : null;

                          return (
                            <button
                              key={payment.id}
                              type="button"
                              onClick={() => {
                                if (onEditRelatedPayment) {
                                  closePaymentPreview();
                                  setLocalError(null);
                                  onEditRelatedPayment(payment);
                                  return;
                                }
                                void openPaymentPreview(payment);
                              }}
                              title={`Открыть платеж #${payment.id}`}
                              data-testid={`invoice-related-payment-link-${payment.id}`}
                              className="w-full rounded-lg border border-slate-200 px-2.5 py-2 text-left transition hover:border-blue-200 hover:bg-blue-50/40"
                            >
                              <div className="flex items-start justify-between gap-2">
                                <div className="min-w-0 flex-1">
                                  <div className="truncate text-sm font-semibold text-blue-700 underline decoration-blue-300 underline-offset-2">
                                    {formatRelatedPaymentLinkLabel(payment)}
                                  </div>
                                  <div className="mt-0.5 truncate text-xs text-slate-600">
                                    {payment.description?.trim() || 'Без описания'}
                                  </div>
                                  <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-slate-500">
                                    <span>Ожидаемая дата: {formatDate(expectedPaymentDate) || '—'}</span>
                                    {actualPaymentDate ? (
                                      <span>Фактическая дата: {formatDate(actualPaymentDate) || actualPaymentDate}</span>
                                    ) : null}
                                    {payment.account ? <span>Счёт: {payment.account}</span> : null}
                                  </div>
                                </div>
                                <span
                                  className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${
                                    relatedPaymentStatusMeta[payment.status] ?? relatedPaymentStatusMeta.Pending
                                  }`}
                                >
                                  {invoicePaymentStatusLabel[payment.status] ?? invoicePaymentStatusLabel.Pending}
                                </span>
                              </div>
                              {(installmentMeta || appliedClientBalanceAmount > 0) ? (
                              <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                                {installmentMeta ? (
                                  <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] font-semibold text-violet-700">
                                    Рассрочка {installmentSequenceLabel}
                                  </span>
                                ) : null}
                                {appliedClientBalanceAmount > 0 ? (
                                  <span className="inline-flex items-center rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700">
                                    Зачтено с баланса: {formatCurrencySmart(appliedClientBalanceAmount).full}
                                  </span>
                                ) : null}
                              </div>
                              ) : null}
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
                <p>
                  <span className="text-slate-500">{t('acts') ?? 'Акты'}:</span>{' '}
                  {selectedActs.length === 0 ? '—' : onOpenActPage ? (
                    <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1 align-middle">
                      {selectedActs.map((act, index) => (
                        <Fragment key={act.id}>
                          {index > 0 ? <span className="text-slate-400">,</span> : null}
                          <button
                            type="button"
                            onClick={() => handleOpenActPage(act)}
                            data-testid={`invoice-related-act-link-${act.id}`}
                            className="text-blue-600 underline hover:text-blue-700"
                          >
                            {actLabel(act) || `Акт #${act.id}`}
                          </button>
                        </Fragment>
                      ))}
                    </span>
                  ) : (
                    selectedActsSummary || '—'
                  )}
                </p>
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

          <div className="flex flex-wrap items-start gap-2 rounded-lg border border-slate-300 bg-white p-2 [&_button]:h-10 [&_button]:whitespace-nowrap">
            <div className="flex flex-wrap gap-1 rounded-md border border-slate-200 bg-slate-50 p-1">
            <button type="button" onClick={() => void handlePageAction('send')} disabled={delivery.busy || !canReadPrint || !canExport} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm disabled:opacity-60"><Send className="h-4 w-4" />Отправить</button>
            {isAdmin && invoice && (invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId) ? (
              <button type="button" onClick={() => { if (canReadPrint && canExport && delivery.capture()) setMessengerDeliveryOpen(delivery.scope); }} className="inline-flex items-center justify-center gap-2 rounded border border-violet-200 bg-white px-3 py-2 text-sm text-violet-700"><Send className="h-4 w-4" />Мессенджер</button>
            ) : null}
            {isAdmin && invoice && (invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId) ? (
              <button type="button" onClick={() => { if (canReadPrint && canExport && delivery.capture()) setElectronicSignatureOpen(delivery.scope); }} className="inline-flex items-center justify-center gap-2 rounded border border-blue-200 bg-white px-3 py-2 text-sm text-blue-700"><FileText className="h-4 w-4" />Э-подпись</button>
            ) : null}
            </div>
            <div className="flex flex-wrap gap-1 rounded-md border border-slate-200 bg-slate-50 p-1">
            <div className="relative">
              <button
                type="button"
                onClick={() => {
                  setPrintMenuOpen((prev) => !prev);
                  setDownloadMenuOpen(false);
                }}
                aria-expanded={printMenuOpen}
                ref={printButtonRef}
                disabled={!canReadPrint || !canExport}
                className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"
              >
                <Printer className="h-4 w-4" />Печать<ChevronDown className="h-4 w-4" />
              </button>
              {printMenuOpen ? (
                <div ref={printMenuLayout} className="absolute bottom-full left-0 z-30 mb-2 max-h-[70dvh] overflow-y-auto w-[24rem] max-w-[calc(100vw-2rem)] rounded-xl border border-slate-200 bg-white p-1 shadow-lg">
                  {[
                    { label: 'Счет', templateKey: undefined },
                    { label: 'Счет-договор', templateKey: INVOICE_OFFER_TEMPLATE_KEY },
                    { label: 'Заказ', templateKey: INVOICE_ORDER_TEMPLATE_KEY },
                  ].map((document) => (
                    <div key={document.label} className="rounded-lg px-1 py-1">
                      <div className="px-2 pb-1 pt-1 text-xs font-semibold uppercase tracking-wide text-slate-400">{document.label}</div>
                      <div className="grid grid-cols-2 gap-1">
                        <button type="button" onClick={() => requestDocumentCopy('print', undefined, document.templateKey)} className="col-span-2 rounded-md px-2 py-2 text-left text-sm font-medium text-blue-700 hover:bg-blue-50">
                          По настройкам документа
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setPrintMenuOpen(false);
                            void handlePrintDocument(document.templateKey, false);
                          }}
                          className="rounded-md px-2 py-2 text-left text-sm text-slate-700 hover:bg-slate-50"
                        >
                          Без подписи и печати
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setPrintMenuOpen(false);
                            void handlePrintDocument(document.templateKey, true);
                          }}
                          className="rounded-md px-2 py-2 text-left text-sm font-medium text-emerald-700 hover:bg-emerald-50"
                        >
                          С подписью и печатью
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
            <button
              type="button"
              onClick={() => void handleUploadDocumentToYandexDisk()}
              disabled={delivery.busy || !canSendToDisk}
              className="inline-flex items-center justify-center gap-2 rounded border border-sky-200 bg-white px-3 py-2 text-sm text-sky-700 disabled:opacity-60"
            >
              {yandexDiskUploading ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
              Я.Диск
            </button>
            <div className="relative" ref={downloadMenuRef}>
              <button
                type="button"
                onClick={() => {
                  setDownloadMenuOpen((prev) => !prev);
                  setPrintMenuOpen(false);
                }}
                disabled={!canReadPrint || !canExport}
                className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"
              >
                <Download className="h-4 w-4" />Скачать<ChevronDown className="h-4 w-4" />
              </button>
              {downloadMenuOpen ? (
                <div ref={downloadMenuLayout} className="absolute bottom-full left-0 z-20 mb-2 max-h-[70dvh] overflow-y-auto w-[28rem] max-w-[calc(100vw-2rem)] rounded-xl border border-slate-200 bg-white p-1 shadow-lg">
                  {[
                    { label: 'Счет', templateKey: undefined, tone: 'text-slate-700' },
                    { label: 'Счет-договор', templateKey: INVOICE_OFFER_TEMPLATE_KEY, tone: 'text-emerald-700' },
                    { label: 'Заказ', templateKey: INVOICE_ORDER_TEMPLATE_KEY, tone: 'text-amber-800' },
                  ].map((document) => (
                    <div key={document.label} className="rounded-lg px-1 py-1">
                      <div className="px-2 pb-1 pt-1 text-xs font-semibold uppercase tracking-wide text-slate-400">{document.label}</div>
                      <div className="grid grid-cols-2 gap-1">
                        {[
                          ...(['pdf', 'word'] as const).map(format => ({ signed: undefined, label: `${format === 'pdf' ? 'PDF' : 'Word'} по настройкам` })),
                          ...(['pdf', 'word'] as const).flatMap((format) => ([
                          { signed: false, label: `${format === 'pdf' ? 'PDF' : 'Word'} без подписи и печати` },
                          { signed: true, label: `${format === 'pdf' ? 'PDF' : 'Word'} с подписью и печатью` },
                          ])),
                        ].map((variant) => (
                          <button
                            key={variant.label}
                            type="button"
                            onClick={() => void requestDocumentCopy(
                              variant.label.startsWith('PDF') ? 'pdf' : 'word',
                              variant.signed,
                              document.templateKey,
                            )}
                            aria-label={`${document.label}: ${variant.label}`}
                            className={`rounded-md px-2 py-2 text-left text-sm hover:bg-slate-50 ${variant.signed === undefined ? 'font-medium text-blue-700 hover:bg-blue-50' : variant.signed ? 'font-medium text-emerald-700 hover:bg-emerald-50' : document.tone}`}
                          >
                            {variant.label}
                          </button>
                        ))}
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
            </div>
            <div className="flex flex-wrap gap-1 rounded-md border border-slate-200 bg-slate-50 p-1">
              <button type="button" onClick={() => void handlePageAction('markNotSigned')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Ban className="h-4 w-4" />Не подписан</button>
              <button type="button" onClick={() => void handlePageAction('sendToSigning')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Send className="h-4 w-4" />На подписание</button>
              <button type="button" onClick={() => void handlePageAction('markSigned')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Check className="h-4 w-4" />Подписан</button>
            </div>
            <div className="flex flex-wrap gap-1 rounded-md border border-slate-200 bg-slate-50 p-1">
              <button type="button" onClick={() => void handlePageAction('copy')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Copy className="h-4 w-4" />Копировать</button>
              <button type="button" onClick={() => void handlePageAction('paid')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Check className="h-4 w-4" />Оплачен</button>
              <button type="button" onClick={() => void handlePageAction('cancel')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Ban className="h-4 w-4" />Отменен</button>
              <button type="button" onClick={() => void handlePageAction('delete')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 bg-white px-3 py-2 text-sm"><Trash2 className="h-4 w-4" />Удалить</button>
            </div>
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
                                  <button
                                    type="button"
                                    onClick={handleOpenInstallmentCalculator}
                                    className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-200"
                                  >
                                    {installmentPlan ? 'Изменить рассрочку' : (t('invoiceInstallmentButton') ?? 'Рассрочка')}
                                  </button>
                                </span>
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  value={formatMoneyDisplayValue(form.amount)}
                                  readOnly
                                  className="rounded-lg border border-slate-300 bg-slate-50 px-3 py-2 text-right text-sm tabular-nums text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  placeholder="0,00"
                                />
                                {installmentPlan ? (
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
                                <SystemSelect
                                  value={form.status}
                                  onChange={handleChange('status')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                >
                                  <option value="Pending">{t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается'}</option>
                                  <option value="Completed">{t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачен'}</option>
                                  <option value="Overdue">{t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочен'}</option>
                                  <option value="Processing">Частично оплачен</option>
                                  {form.status === 'Cancelled' && <option value="Cancelled">{t('cancelledStatus') ?? 'Отменён'}</option>}
                                </SystemSelect>
                              </label>

                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center font-medium text-slate-700">Статус документа</span>
                                <SystemSelect
                                  value={form.workflowStatus}
                                  onChange={handleChange('workflowStatus')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                >
                                  <option value="Draft">Черновик</option>
                                  <option value="NotSigned">Не подписан</option>
                                  <option value="RequiresClientSignature">Требуется подпись клиента</option>
                                  <option value="OnApproval">На согласовании</option>
                                  <option value="Approved">Согласован</option>
                                  <option value="ApprovalRejected">Отказ в согласовании</option>
                                  <option value="NeedsCorrection">Требует исправления</option>
                                  <option value="OnSigning">На подписании</option>
                                  <option value="SignedByClient">Подписан клиентом</option>
                                  {form.workflowStatus === 'Cancelled' && <option value="Cancelled">Аннулирован</option>}
                                </SystemSelect>
                                <span className="text-xs font-normal text-slate-500">Для аннулирования используйте действие «Отмена счёта» с указанием причины.</span>
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

                            {mode === 'edit' && invoice?.id && isIncome ? (
                              <div className="text-sm">
                                <DocumentSellerPicker
                                  kind="invoice"
                                  documentId={invoice.id}
                                  companyId={invoice.sellerCompanyId}
                                  branchId={invoice.sellerBranchId}
                                  frozen={invoice.sellerFrozen}
                                  snapshotInfo={invoice.sellerSnapshotInfo}
                                  timeZoneId={profileTimeZoneId}
                                  canEdit
                                />
                              </div>
                            ) : null}

                            {mode === 'create' && isIncome ? (
                              <div className="text-sm">
                                <DocumentSellerPicker kind="invoice" documentId={0} draft canEdit={!submitting}
                                  companyId={sellerSelection.companyId} branchId={sellerSelection.branchId} onChanged={setSellerSelection} />
                              </div>
                            ) : null}

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
                              <SystemCheckbox
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

              <section ref={relatedPaymentsSectionRef} className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
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
                          {referencesLoading ? <p className="mb-2 text-xs text-slate-500">Загрузка справочников позиций...</p> : null}
                          {referencesError ? <div role="alert" className="mb-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
                            {extractDisplayErrorMessage(new Error(referencesError), 'Не удалось загрузить справочники позиций.')}
                            <button type="button" onClick={() => { void retryReferences(); }} disabled={referencesLoading} className="ml-2 underline">Повторить загрузку услуг</button>
                          </div> : null}
                          <div className="-mx-1 overflow-x-auto px-1">
                            <div className={`space-y-3 ${LINE_ITEM_GRID_MIN_WIDTH_CLASS}`}>
                              <div className={`hidden items-center gap-3 px-1 text-xs font-semibold uppercase tracking-wide text-slate-500 md:grid ${LINE_ITEM_GRID_TEMPLATE}`}>
                                <span>Наименование</span>
                                <span className="text-center">Кол-во</span>
                                <span>Ед.</span>
                                <span className="text-right">Цена</span>
                                <span>НДС</span>
                                <span className="text-right">Сумма</span>
                                <span className="sr-only">Действия</span>
                              </div>
                              {lineItems.map((item) => {
                                const lineItemTotal = calculateLineItemAmountValue(item.quantity, item.price, roundLineAmounts);

                                return (
                                  <div
                                    key={item.id}
                                    className={`grid gap-3 rounded-2xl border border-slate-200 bg-slate-50/80 p-3 md:items-start md:rounded-none md:border-0 md:bg-transparent md:p-0 ${LINE_ITEM_GRID_TEMPLATE}`}
                                  >
                                    <div className="space-y-1">
                                      <button
                                        type="button"
                                        data-testid="invoice-line-item-service-trigger"
                                        onClick={() => openLineItemLookup(item.id)}
                                        disabled={!canEditInvoiceItems || submitting}
                                        aria-label="Наименование услуги"
                                        title={item.name || 'Выберите наименование услуги'}
                                        className="flex min-h-[3.25rem] w-full items-center justify-between gap-3 rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-left text-sm transition hover:border-blue-300 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-100 md:h-10 md:min-h-10 md:py-2"
                                      >
                                        <span className="min-w-0 flex-1">
                                          <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Услуга</span>
                                          <span className={`mt-1 block truncate font-medium md:mt-0 ${item.name.trim() ? 'text-slate-700' : 'text-slate-400'}`}>
                                            {item.name.trim() || 'Выберите наименование услуги'}
                                          </span>
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
                                        placeholder="0,00"
                                        aria-label="Цена"
                                        className="w-full rounded-lg border border-slate-300 px-3 py-2 text-right text-sm tabular-nums"
                                      />
                                    </label>

                                    <label className="space-y-1">
                                      <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">НДС</span>
                                      <VatRateSelect value={item.vatRate ?? ''} onChange={(value) => updateLineItem(item.id, { vatRate: value })} />
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
                ) : paymentsReadError ? renderPaymentsError() : paymentsLoading ? (
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
                      const invoiceManagedPayment = isInvoiceManagedPayment(payment);
                      const installmentMeta = parseInstallmentPaymentMetadata(payment.systemNotes);
                      const appliedClientBalanceAmount = getAppliedClientBalanceAmount(payment.systemNotes);
                      const canEditInvoiceManagedPayment = false;
                      const canEditRelatedPayment = Boolean(onEditRelatedPayment);
                      const canDeleteRelatedPayment = Boolean(onDeleteRelatedPayment) && !invoiceManagedPayment;
                      const expectedPaymentDate = resolveRelatedPaymentExpectedDate(payment);
                      const actualPaymentDate = resolveRelatedPaymentActualDate(payment);
                      const installmentSequenceLabel = installmentMeta
                        ? `${installmentMeta.sequence}/${Math.max(installmentMeta.months, installmentMeta.sequence)}`
                        : null;

                      return (
                        <div
                          key={payment.id}
                          className="flex items-start gap-3 rounded-lg border border-slate-200 px-3 py-2"
                        >
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="text-sm font-semibold text-slate-900">
                              {formatCurrencySmart(payment.amount ?? 0).full}
                            </span>
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
                            {appliedClientBalanceAmount > 0 ? (
                              <span className="inline-flex items-center rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700">
                                Зачтено с баланса: {formatCurrencySmart(appliedClientBalanceAmount).full}
                              </span>
                            ) : null}
                          </div>
                          <div className="mt-0.5 truncate text-xs text-slate-600">
                            {payment.description?.trim() || 'Без описания'}
                          </div>
                          <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-slate-500">
                            <span>Ожидаемая дата: {formatDate(expectedPaymentDate) || '—'}</span>
                            {actualPaymentDate ? (
                              <span>Фактическая дата: {formatDate(actualPaymentDate) || actualPaymentDate}</span>
                            ) : null}
                            {payment.account && <span>Счёт: {payment.account}</span>}
                          </div>
                        </div>
                        {(canEditRelatedPayment || canDeleteRelatedPayment) && (
                          <div className="flex shrink-0 items-center gap-1">
                            {canEditInvoiceManagedPayment && (
                              <button
                                type="button"
                                onClick={() => openInvoiceManagedPaymentEditor(payment)}
                                data-testid={`invoice-related-payment-edit-${payment.id}`}
                                className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                                title={t('edit') ?? 'Редактировать'}
                                aria-label={t('edit') ?? 'Редактировать'}
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {canEditRelatedPayment && (
                              <button
                                type="button"
                                onClick={() => onEditRelatedPayment?.(payment)}
                                data-testid={`invoice-related-payment-edit-${payment.id}`}
                                className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                                title={t('edit') ?? 'Редактировать'}
                                aria-label={t('edit') ?? 'Редактировать'}
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {canDeleteRelatedPayment && (
                              <button
                                type="button"
                                onClick={() => {
                                  void onDeleteRelatedPayment?.(payment);
                                }}
                                className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 text-red-600 transition hover:bg-red-50"
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

              {canViewPayments && !linkedMoneyOperationsError && (linkedMoneyOperationsLoading || linkedMoneyOperations.length > 0) && (
              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                <header className="mb-3 flex flex-col gap-1">
                  <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                    Движения денег
                  </span>
                  <p className="text-sm text-slate-500">
                    Банк, касса, карты и электронные кошельки, связанные с этим счётом.
                  </p>
                </header>

                {linkedMoneyOperationsLoading ? (
                  <div className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : (
                  <div className="space-y-3">
                    {linkedMoneyOperations.map((operation) => {
                      const amount = formatCurrencySmart(operation.amount ?? 0).full;
                      const statusLabel = moneyOperationMatchStatusLabel(operation.matchStatus);
                      const sourceLabel = moneyOperationSourceLabel(operation.source);
                      const directionLabel = moneyOperationDirectionLabel(operation.direction);
                      const purpose = operation.purpose?.trim()
                        || operation.counterpartyName?.trim()
                        || operation.paidInvoiceNumber?.trim()
                        || 'Без назначения';

                      return (
                        <div
                          key={`${operation.isReadonly ? 'payment' : 'operation'}-${operation.id}`}
                          className="rounded-xl border border-slate-200 px-4 py-3"
                        >
                          <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                            <div className="min-w-0">
                              <div className="flex flex-wrap items-center gap-2">
                                <span className="text-sm font-semibold text-slate-900">{amount}</span>
                                <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                                  operation.direction === 'Expense'
                                    ? 'bg-rose-50 text-rose-700'
                                    : operation.direction === 'Transfer'
                                      ? 'bg-violet-50 text-violet-700'
                                      : 'bg-emerald-50 text-emerald-700'
                                }`}>
                                  {directionLabel}
                                </span>
                                <span className="inline-flex rounded-full border border-slate-200 px-2.5 py-1 text-xs font-semibold text-slate-600">
                                  {sourceLabel}
                                </span>
                                {statusLabel ? (
                                  <span className="inline-flex rounded-full border border-blue-200 bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">
                                    {statusLabel}
                                  </span>
                                ) : null}
                                {operation.isReadonly ? (
                                  <span className="inline-flex rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-semibold text-slate-600">
                                    Платёж
                                  </span>
                                ) : null}
                              </div>
                              <div className="mt-1 line-clamp-2 text-sm text-slate-600">{purpose}</div>
                            </div>
                            <div className="shrink-0 text-left text-xs text-slate-500 sm:text-right">
                              <div>{formatDate(operation.date) || 'Без даты'}</div>
                              {operation.accountName?.trim() ? <div>{operation.accountName.trim()}</div> : null}
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </section>
              )}
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
                                  value={selectedCounterpartyName}
                                  readOnly
                                  className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm bg-slate-50"
                                  placeholder={counterpartyPlaceholder}
                                />
                                <button
                                  type="button"
                                  aria-label={`Открыть выбор: ${counterpartyObjectLabel}`}
                                  onClick={() => openClientPicker('main')}
                                  disabled={!canSelectCounterparty}
                                  className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                                >
                                  <UserRoundSearch className="h-4 w-4" />
                                </button>
                              </div>
                              {lookupsLoading && (
                                <span className="text-xs text-slate-500">{t('loading') ?? 'Загрузка...'}</span>
                              )}
                              {lookupsError && (
                                <span className="text-xs text-rose-600">{lookupsError}
                                  {onRetryLookups ? <button type="button" onClick={() => { if (open && printOwner.capture() && !lookupsLoading) onRetryLookups(); }} disabled={lookupsLoading} className="ml-2 underline">Повторить загрузку справочников</button> : null}
                                </span>
                              )}
                              {(selectedClient?.clientStatus || selectedClient?.responsibleUserName || selectedLegalEntity) && (
                                <div className="mt-2 space-y-1 rounded-xl border border-slate-100 bg-slate-50 p-2">
                                  {selectedLegalEntity && (
                                    <p className="text-xs text-slate-600">
                                      Юрлицо: {getLegalEntityDisplayName(selectedLegalEntity)}
                                      {selectedLegalEntity.inn ? `, ИНН ${selectedLegalEntity.inn}` : ''}
                                    </p>
                                  )}
                                  {selectedClient?.clientStatus && (
                                    <ClientStatusBadge status={selectedClient.clientStatus} />
                                  )}
                                  {selectedClient?.responsibleUserName && (
                                    <p className="text-xs text-slate-600">
                                      Ответственный: {selectedClient.responsibleUserName}
                                    </p>
                                  )}
                                  {selectedClient?.clientStatus?.description && (
                                    <p className="text-xs text-slate-500">{selectedClient.clientStatus.description}</p>
                                  )}
                                </div>
                              )}
                              {showCounterpartyBalanceNotice && (
                                <div className="mt-2 rounded-xl border border-emerald-100 bg-emerald-50 p-3">
                                  <div className="flex flex-wrap items-center justify-between gap-2">
                                    <div>
                                      <div className="text-sm font-medium text-emerald-900">Доступно на балансе {counterpartyBalanceOwnerLabel}</div>
                                      <div className="mt-0.5 text-xs text-emerald-700">
                                        Эти деньги можно зачесть в счет оплаты после создания связанного платежа.
                                      </div>
                                    </div>
                                    <div className="text-base font-semibold tabular-nums text-emerald-800">
                                      {counterpartyBalanceLoading ? 'проверяем...' : formatMoneyDisplayValue(positiveCounterpartyBalance)}
                                    </div>
                                  </div>
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
                                <label className="flex flex-col gap-1 text-sm">
                                  <span className="font-medium text-slate-700">Контрагент и дело расхода (опционально)</span>
                                  <div className="flex gap-2">
                                    <input
                                      type="text"
                                      value={selectedExpenseCounterpartyName}
                                      readOnly
                                      className="flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                                      placeholder="Не выбрано - расход только по поставщику"
                                    />
                                    <button
                                      type="button"
                                      onClick={() => openClientPicker('expense')}
                                      disabled={lookupsLoading || !canSelectCounterparty}
                                      className="inline-flex items-center rounded-lg border border-slate-200 bg-white px-3 py-2 text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                                      aria-label="Открыть выбор контрагента расхода"
                                    >
                                      <UserRoundSearch className="h-4 w-4" />
                                    </button>
                                  </div>
                                  <span className="text-xs text-slate-500">
                                    Аналитика расходов, долгов и рентабельности дела.
                                  </span>
                                </label>
                                {(selectedExpenseClient?.clientStatus || selectedExpenseClient?.responsibleUserName || selectedExpenseLegalEntity) && (
                                  <div className="mt-2 space-y-1 rounded-xl border border-amber-100 bg-white/80 p-2">
                                    {selectedExpenseLegalEntity && (
                                      <p className="text-xs text-slate-600">
                                        Юрлицо: {getLegalEntityDisplayName(selectedExpenseLegalEntity)}
                                        {selectedExpenseLegalEntity.inn ? `, ИНН ${selectedExpenseLegalEntity.inn}` : ''}
                                      </p>
                                    )}
                                    {selectedExpenseClient?.clientStatus && (
                                      <ClientStatusBadge status={selectedExpenseClient.clientStatus} />
                                    )}
                                    {selectedExpenseClient?.responsibleUserName && (
                                      <p className="text-xs text-slate-600">
                                        Ответственный: {selectedExpenseClient?.responsibleUserName}
                                      </p>
                                    )}
                                    {selectedExpenseClient?.clientStatus?.description && (
                                      <p className="text-xs text-slate-500">{selectedExpenseClient?.clientStatus?.description}</p>
                                    )}
                                  </div>
                                )}
                              </div>
                            )}

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">Договор</span>
                              <div className="flex items-stretch gap-2">
                                <SystemSelect
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
                                </SystemSelect>
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
                                  Сначала выберите {counterpartyObjectLabel}, затем можно будет быстро создать договор.
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
                              <SystemDateInput
                                type="date"
                                value={form.date}
                                onChange={handleChange('date')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                required
                              />
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDueDate') ?? 'Срок оплаты'}</span>
                              <SystemDateInput
                                type="date"
                                value={form.dueDate}
                                onChange={handleChange('dueDate')}
                                disabled={dueDateLockedByInstallmentPlan}
                                className={`rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 ${dueDateLockedByInstallmentPlan ? 'bg-slate-100 text-slate-500' : ''}`}
                              />
                              {dueDateLockedByInstallmentPlan ? (
                                <span className="text-xs leading-5 text-amber-600">
                                  Действует рассрочка - корректировка срока оплаты только вручную по каждому платежу.
                                </span>
                              ) : null}
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoicePaidDate') ?? 'Дата оплаты'}</span>
                              <SystemDateInput
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
                disabled={submitting || paymentsReadBlocked}
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
                  leaveInvoiceCard(onClose);
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
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Ожидаемая дата</div>
                  <div className="mt-2 text-sm font-medium text-slate-900">
                    {formatDate(selectedPaymentPreviewExpectedDate) || '—'}
                  </div>
                </div>
                {selectedPaymentPreviewActualDate ? (
                  <div className="rounded-xl border border-slate-200 px-4 py-3">
                    <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">Фактическая дата</div>
                    <div className="mt-2 text-sm font-medium text-slate-900">
                      {formatDate(selectedPaymentPreviewActualDate) || selectedPaymentPreviewActualDate}
                    </div>
                  </div>
                ) : null}
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
                    onOpenPayment(selectedPaymentPreview.id, selectedPaymentPreview.type);
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
        open={!!lineItemLookupState && lineItemLookupState.ownerScope === referenceScope && canEditInvoiceItems && !pageViewMode}
        title="Выбор наименования услуги"
        description={
          activeLineItemCategoryLabel === 'Все категории'
            ? 'Выберите категорию и подходящую услугу из справочника или введите своё наименование вручную.'
            : `Категория: ${activeLineItemCategoryLabel}. Можно выбрать из найденных услуг или ввести своё наименование.`
        }
        query={lineItemLookupQuery}
        value={activeLineItemSelectedServiceId}
        options={lineItemServiceLookupOptions}
        searchPlaceholder="Поиск наименования"
        maxWidthClassName="max-w-4xl"
        filterLabel="Категория услуги"
        filterValue={activeLineItemLookup?.serviceCategoryId ?? ''}
        filterOptions={lineItemCategoryFilterOptions}
        filterAllLabel="Все категории"
        filterHelperText="Категория сохраняется у позиции счёта и сразу фильтрует подходящие шаблоны услуги."
        clearActionLabel="Очистить наименование"
        clearActionDescription="Оставить позицию без выбранного шаблона услуги."
        customActionLabel={canApplyCustomServiceName ? `Использовать: ${lineItemLookupQueryTrimmed}` : undefined}
        customActionDescription={canApplyCustomServiceName ? 'Сохранить введённый текст как наименование позиции.' : undefined}
        noOptionsText="Подходящие услуги не найдены"
        loading={referencesLoading}
        error={referencesError ? extractDisplayErrorMessage(new Error(referencesError), 'Не удалось загрузить услуги.') : null}
        onRetry={() => { void retryReferences(); }}
        onQueryChange={(value) => { if (isCurrentLineItemLookup()) setLineItemLookupQuery(value); }}
        onClose={closeLineItemLookup}
        onSelect={handleLineItemServiceSelect}
        onClear={clearLineItemServiceName}
        onSelectCustomQuery={canApplyCustomServiceName ? applyCustomLineItemServiceName : undefined}
        onFilterChange={(value) => {
          if (!lineItemLookupState?.lineItemId) {
            return;
          }
          handleLineItemCategoryChange(lineItemLookupState.lineItemId, value);
        }}
      />

      <CounterpartyPickerModal
        open={clientPickerOpen}
        title={clientPickerMode === 'expense' ? 'Выбор контрагента расхода' : `Выбор ${counterpartyObjectLabel}`}
        initialScope={
          clientPickerMode === 'expense'
            ? (form.expenseLegalEntityId ? 'legalEntity' : 'client')
            : (form.legalEntityId ? 'legalEntity' : 'client')
        }
        selected={selectedCounterpartyPickerValue}
        allowedScopes={allowedCounterpartyScopes}
        counterpartyRole={
          clientPickerMode === 'expense'
            ? 'client'
            : form.type === 'Expense'
              ? 'supplier'
              : 'client'
        }
        allowEmpty={clientPickerMode === 'expense'}
        emptyLabel="Без контрагента расхода"
        overlayClassName="z-[1600]"
        onClose={() => setClientPickerOpen(false)}
        onSelect={(selection) => {
          if (!selection) {
            if (clientPickerMode === 'expense') {
              clearExpenseCounterparty();
            }
            return;
          }
          if (clientPickerMode === 'expense') {
            if (selection.kind === 'client') {
              handleExpenseClientChange(String(selection.id));
              setClientPickerOpen(false);
            } else if (selection.kind === 'legalEntity' && selection.legalEntity) {
              handleExpenseLegalEntityPick(selection.legalEntity);
            }
          } else if (selection.kind === 'client' && selection.client) {
            handleClientPick(selection.client);
          } else if (selection.kind === 'legalEntity' && selection.legalEntity) {
            handleLegalEntityPick(selection.legalEntity);
          }
        }}
      />
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

      {cancelDialogOpen && (
        <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-900/60 p-4">
          <div
            role="dialog"
            aria-modal="true"
            aria-labelledby="invoice-cancel-dialog-title"
            data-testid="invoice-cancel-dialog"
            className="max-h-[calc(100vh-2rem)] w-full max-w-2xl overflow-y-auto rounded-2xl bg-white shadow-2xl"
          >
            <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 id="invoice-cancel-dialog-title" className="text-lg font-semibold text-slate-900">Отмена счёта</h3>
                <p className="mt-1 text-sm text-slate-500">Выберите, что сделать с платежами и уже полученными деньгами.</p>
              </div>
              <button
                type="button"
                onClick={() => {
                  if (!cancelSubmitting) setCancelDialogOpen(false);
                }}
                disabled={cancelSubmitting}
                className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700 disabled:cursor-not-allowed disabled:opacity-60"
                aria-label="Закрыть окно отмены счёта"
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-5 px-5 py-4">
              <div className="grid gap-3 sm:grid-cols-3">
                <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-emerald-700">Оплачено</div>
                  <div className="mt-1 text-sm font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.paidAmount).short}</div>
                </div>
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-amber-700">Не оплачено</div>
                  <div className="mt-1 text-sm font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.outstandingAmount).short}</div>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <div className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">График</div>
                  <div className="mt-1 text-sm font-semibold text-slate-900">
                    {cancellationInstallmentStats.generatedCount > 0
                      ? `${cancellationInstallmentStats.paidCount} оплач., ${cancellationInstallmentStats.unpaidCount} не оплач.`
                      : 'Нет рассрочки'}
                  </div>
                </div>
              </div>

              <label className="block text-sm">
                <span className="mb-1 block font-medium text-slate-700">Причина отмены</span>
                <textarea
                  data-testid="invoice-cancel-reason"
                  value={cancelReason}
                  onChange={(event) => setCancelReason(event.target.value)}
                  rows={3}
                  className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  placeholder="Например: клиент отказался от оставшихся платежей"
                />
              </label>

              <fieldset className="space-y-3">
                <legend className="text-sm font-semibold text-slate-900">Что отменить</legend>
                {cancellationInstallmentStats.canPreservePaidInstallments ? (
                  <>
                    <label className={`flex cursor-pointer gap-3 rounded-lg border px-3 py-3 text-sm transition ${cancelScope === 'UnpaidInstallmentsOnly' ? 'border-blue-300 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}>
                      <input
                        type="radio"
                        name="invoice-cancel-scope"
                        value="UnpaidInstallmentsOnly"
                        checked={cancelScope === 'UnpaidInstallmentsOnly'}
                        onChange={() => setCancelScope('UnpaidInstallmentsOnly')}
                        className="mt-1"
                      />
                      <span>
                        <span className="block font-medium text-slate-900">Только неоплаченные платежи графика</span>
                        <span className="mt-1 block text-slate-600">
                          Оплаченные платежи останутся оплаченными. Счёт не будет полностью аннулирован.
                        </span>
                      </span>
                    </label>
                    <label className={`flex cursor-pointer gap-3 rounded-lg border px-3 py-3 text-sm transition ${cancelScope === 'All' ? 'border-rose-300 bg-rose-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}>
                      <input
                        type="radio"
                        name="invoice-cancel-scope"
                        value="All"
                        checked={cancelScope === 'All'}
                        onChange={() => setCancelScope('All')}
                        className="mt-1"
                      />
                      <span>
                        <span className="block font-medium text-slate-900">Весь счёт и все платежи</span>
                        <span className="mt-1 block text-slate-600">
                          Счёт будет аннулирован, а полученную оплату нужно перенести, зачесть или вернуть.
                        </span>
                      </span>
                    </label>
                  </>
                ) : (
                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-700">
                    Будет аннулирован весь счёт. Вариант с сохранением оплаченных платежей появляется, когда в графике есть и оплаченные, и неоплаченные платежи.
                  </div>
                )}
              </fieldset>

              {cancelScope === 'All' && cancelDialogHasReceivedMoney && (
                <fieldset className="space-y-3">
                  <legend className="text-sm font-semibold text-slate-900">Что сделать с полученной оплатой</legend>
                  <label className={`flex cursor-pointer gap-3 rounded-lg border px-3 py-3 text-sm transition ${cancelMoneyAction === 'LeaveAsAdvance' ? 'border-blue-300 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}>
                    <input
                      type="radio"
                      name="invoice-cancel-money-action"
                      data-testid="invoice-cancel-money-advance"
                      checked={cancelMoneyAction === 'LeaveAsAdvance'}
                      onChange={() => setCancelMoneyAction('LeaveAsAdvance')}
                      className="mt-1"
                    />
                    <span>
                      <span className="block font-medium text-slate-900">Оставить авансом контрагента</span>
                      <span className="mt-1 block text-slate-600">Деньги останутся на балансе контрагента для будущих счетов.</span>
                    </span>
                  </label>
                  <label className={`flex cursor-pointer gap-3 rounded-lg border px-3 py-3 text-sm transition ${cancelMoneyAction === 'Reallocate' ? 'border-blue-300 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}>
                    <input
                      type="radio"
                      name="invoice-cancel-money-action"
                      data-testid="invoice-cancel-money-reallocate"
                      checked={cancelMoneyAction === 'Reallocate'}
                      onChange={() => setCancelMoneyAction('Reallocate')}
                      className="mt-1"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block font-medium text-slate-900">Зачесть на другой счёт</span>
                      <span className="mt-1 block text-slate-600">Укажите ID счёта этого же контрагента.</span>
                      {cancelMoneyAction === 'Reallocate' && (
                        <input
                          data-testid="invoice-cancel-target-payment-id"
                          value={cancelTargetPaymentId}
                          onChange={(event) => setCancelTargetPaymentId(event.target.value.replace(/[^\d]/g, ''))}
                          className="mt-2 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                          placeholder="ID счёта"
                          inputMode="numeric"
                        />
                      )}
                    </span>
                  </label>
                  <label className={`flex cursor-pointer gap-3 rounded-lg border px-3 py-3 text-sm transition ${cancelMoneyAction === 'Refund' ? 'border-blue-300 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'}`}>
                    <input
                      type="radio"
                      name="invoice-cancel-money-action"
                      data-testid="invoice-cancel-money-refund"
                      checked={cancelMoneyAction === 'Refund'}
                      onChange={() => setCancelMoneyAction('Refund')}
                      className="mt-1"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block font-medium text-slate-900">Оформить возврат денег</span>
                      <span className="mt-1 block text-slate-600">Будет создан расход денег на сумму полученной оплаты.</span>
                      {cancelMoneyAction === 'Refund' && (
                        <span className="mt-2 grid gap-2 sm:grid-cols-2">
                          <SystemSelect
                            data-testid="invoice-cancel-refund-source"
                            value={cancelRefundSource}
                            onChange={(event) => setCancelRefundSource(event.target.value as InvoiceCancellationRefundSource)}
                            className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                          >
                            {(['Bank', 'Cash', 'Card', 'EMoney'] as InvoiceCancellationRefundSource[]).map((source) => (
                              <option key={source} value={source}>{moneyOperationSourceLabel(source)}</option>
                            ))}
                          </SystemSelect>
                          <SystemDateInput
                            data-testid="invoice-cancel-refund-date"
                            type="date"
                            value={cancelRefundDate}
                            onChange={(event) => setCancelRefundDate(event.target.value)}
                            className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                          />
                        </span>
                      )}
                    </span>
                  </label>
                </fieldset>
              )}

              {cancelError && (
                <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                  {cancelError}
                </div>
              )}
            </div>

            <div className="flex flex-col-reverse gap-2 border-t border-slate-200 px-5 py-4 sm:flex-row sm:justify-end">
              <button
                type="button"
                onClick={() => setCancelDialogOpen(false)}
                disabled={cancelSubmitting}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Не отменять
              </button>
              <button
                type="button"
                onClick={() => void handleCancelInvoiceSubmit()}
                disabled={cancelSubmitting}
                data-testid="invoice-cancel-submit"
                className="inline-flex items-center justify-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-semibold text-white hover:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {cancelSubmitting && <Loader2 className="h-4 w-4 animate-spin" />}
                Продолжить
              </button>
            </div>
          </div>
        </div>
      )}

      {amountReconciliationDialog && (
        <div className="fixed inset-0 z-[2450] flex items-center justify-center bg-slate-900/50 px-4 py-6">
          <div className="w-full max-w-xl overflow-hidden rounded-2xl bg-white shadow-2xl">
            <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
              <div>
                <h3 className="text-base font-semibold text-slate-900">Сверка суммы счета и платежей</h3>
                <p className="mt-1 text-sm text-slate-500">
                  Найдено расхождение между суммой счета, выставленными платежами и оплатами.
                </p>
              </div>
              <button
                type="button"
                onClick={() => resolveAmountReconciliationDecision('cancel')}
                className="rounded-full p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
                aria-label="Закрыть"
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-4 px-5 py-4 text-sm text-slate-700">
              <div className="grid gap-2 sm:grid-cols-2">
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Сумма счета</div>
                  <div className="mt-1 text-base font-semibold text-slate-900">
                    {formatCurrencySmart(amountReconciliationDialog.invoiceAmount).full}
                  </div>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Выставлено платежей</div>
                  <div className="mt-1 text-base font-semibold text-slate-900">
                    {formatCurrencySmart(amountReconciliationDialog.plannedAmount).full}
                  </div>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Получено оплат</div>
                  <div className="mt-1 text-base font-semibold text-slate-900">
                    {formatCurrencySmart(amountReconciliationDialog.paidAmount).full}
                  </div>
                </div>
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-amber-700">Расхождение</div>
                  <div className="mt-1 text-base font-semibold text-amber-900">
                    {formatCurrencySmart(amountReconciliationDialog.difference).full}
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-blue-800">
                {amountReconciliationDialog.direction === 'increase'
                  ? 'Сумма счета стала больше суммы выставленных платежей. Можно добавить платежи на недостающую сумму или сохранить только счет.'
                  : 'Сумма счета стала меньше суммы выставленных платежей или оплат. Лишние платежи нужно удалить или отредактировать вручную.'}
              </div>
              <p className="text-xs leading-5 text-slate-500">
                При сохранении платеж из строки счета повторно не создается. Связанные платежи остаются как есть, пока вы не измените их отдельно.
              </p>
            </div>

            <div className="flex flex-col-reverse gap-2 border-t border-slate-200 px-5 py-4 sm:flex-row sm:justify-end">
              <button
                type="button"
                onClick={() => resolveAmountReconciliationDecision('cancel')}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Вернуться
              </button>
              <button
                type="button"
                onClick={() => resolveAmountReconciliationDecision('manage')}
                className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-100"
              >
                {amountReconciliationDialog.direction === 'increase' ? 'Добавить платежи' : 'Редактировать платежи'}
              </button>
              <button
                type="button"
                onClick={() => resolveAmountReconciliationDecision('save')}
                className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white transition hover:bg-slate-800"
              >
                Сохранить только счет
              </button>
            </div>
          </div>
        </div>
      )}

      <CaseModal
        key={caseCreation.scope}
        isOpen={createCaseOpen && canCreateRelatedCase}
        onClose={() => { if (caseCreation.capture()) { caseCreation.invalidate(); setCreateCaseOpen(false); } }}
        caseData={createCaseStub}
        onSave={handleCreateClientCase}
        overlayClassName="z-[2300]"
        responsibles={responsibles}
      />

      {imagePrintRequest && invoice && open && canReadPrint && canExport && imagePrintRequest.scope === printScope && imagePrintRequest.documentId === invoice.id ? (
        <DocumentImagePrintDialog key={`${invoice.id}:${imagePrintRequest.generation}`} owner={imagePrintRequest.owner}
          documentName={`${imagePrintRequest.templateKey === INVOICE_OFFER_TEMPLATE_KEY ? 'Счёт-договор' : imagePrintRequest.templateKey === INVOICE_ORDER_TEMPLATE_KEY ? 'Заказ' : 'Счёт'} № ${invoice.number || invoice.id}`}
          initialOptions={imagePrintRequest.initialOptions}
          defaultsSource={imagePrintRequest.useDefaults ? { section: 'invoices', documentId: invoice.id } : undefined}
          notice={imagePrintRequest.format === 'print' ? 'Подготовка печатной копии закрепляет реквизиты по выбранному для документа правилу. Отмена в системном окне печати не отменяет подготовку копии.' : undefined}
          confirmLabel={imagePrintRequest.format === 'print' ? 'Печатать' : `Скачать ${imagePrintRequest.format === 'word' ? 'Word' : 'PDF'}`}
          onConfirm={confirmDocumentCopy} onClose={() => setImagePrintRequest(null)} />
      ) : null}

      {messengerDeliveryOpen === delivery.scope && open && canReadPrint && canExport && isAdmin && invoice && (invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId) ? (
        <MessengerDocumentDeliveryModal
          key={delivery.scope} ownerScope={delivery.scope}
          clientId={(invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId)!}
          document={{
            id: invoice.id,
            originalFileName: `invoice-${invoice.number || invoice.id}.pdf`,
            documentType: 'Счёт',
            version: 1,
            sizeBytes: 0,
            sourceType: 'invoice',
          }}
          preparePdf={() => apiService.downloadInvoiceDocument(invoice.id, 'pdf')}
          onClose={() => setMessengerDeliveryOpen(null)}
        />
      ) : null}
      {electronicSignatureOpen === delivery.scope && open && canReadPrint && canExport && isAdmin && invoice && (invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId) ? (
        <ElectronicSignatureModal
          key={delivery.scope} ownerScope={delivery.scope}
          clientId={(invoice.type === 'Expense' ? invoice.expenseClientId : invoice.clientId)!}
          documentId={invoice.id}
          documentName={`Счёт ${invoice.number || invoice.id}`}
          documentType="invoice"
          preparePdf={() => apiService.downloadInvoiceDocument(invoice.id, 'pdf')}
          onClose={() => setElectronicSignatureOpen(null)}
        />
      ) : null}

      {createActOpen && !onCreateActPage && (
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
                <SystemDateInput type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
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
