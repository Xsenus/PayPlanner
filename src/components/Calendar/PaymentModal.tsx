import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { X, Trash2, Info, History, Search, UserRoundSearch, Ban, WalletCards, Printer, ChevronDown } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useDictionaries } from '../../hooks/useDictionaries';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type {
  Payment,
  PaymentStatus,
  Client,
  ClientCase,
  LegalEntitySummary,
  PaymentPayload,
  Invoice,
  InvoiceInput,
  Contract,
  ActResponsible,
  CashOrderType,
  ApplyClientBalanceResult,
  PaymentOverpaymentHandling,
} from '../../types';
import { toDateInputValue, fromInputToApiDate, todayYMDInTimeZone, toRuDate } from '../../utils/dateUtils';
import { buildOptionsWithSelected } from '../../utils/formOptions';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { useDialog } from '../../hooks/useDialog';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { useToast } from '../../hooks/useToast';
import { InvoiceModal } from '../Accounts/InvoiceModal';
import type { InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';
import { getAppliedClientBalanceAmount, getVisibleSystemNoteLines } from '../../utils/systemNotes';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import {
  isPartialRemainderSourcePayment,
  isTransferredRemainderPayment,
  parsePartialRemainderTransferMetadata,
} from '../../utils/invoicePayments';
import { parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';
import { buildCancelledPaymentPayload } from '../../utils/paymentActions';
import { CounterpartyPickerModal } from '../ui/CounterpartyPickerModal';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';

type PaymentKind = 'Income' | 'Expense';
type LinkedInvoiceContext = {
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

function isCashPaymentSourceName(name?: string | null): boolean {
  return Boolean(name && /кас|налич|cash/i.test(name));
}

function isBankPaymentSourceName(name?: string | null): boolean {
  return Boolean(name && /банк|банков|безнал|перевод|плат[её]жн|payment|bank/i.test(name));
}

function isCashOrderNumberForType(value: string, type: CashOrderType): boolean {
  const prefix = type === 'Incoming' ? 'ПКО' : 'РКО';
  return value.trim().toLocaleUpperCase('ru-RU').startsWith(prefix);
}

function resolvePaymentPrintTitle(type: PaymentKind, sourceName?: string | null, documentNumber?: string | null): string {
  const normalizedNumber = (documentNumber ?? '').trim().toLocaleUpperCase('ru-RU');

  if (isCashPaymentSourceName(sourceName) || normalizedNumber.startsWith('ПКО') || normalizedNumber.startsWith('РКО')) {
    return type === 'Expense' ? 'Расходный кассовый ордер' : 'Приходный кассовый ордер';
  }

  if (
    isBankPaymentSourceName(sourceName)
    || normalizedNumber.startsWith('П/П')
    || normalizedNumber.startsWith('ПП')
  ) {
    return 'Платежное поручение';
  }

  return type === 'Expense' ? 'Расходный кассовый ордер' : 'Приходный кассовый ордер';
}

function escapeHtml(value: string | number | null | undefined): string {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatPrintMoney(value: number): string {
  return new Intl.NumberFormat('ru-RU', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(Number.isFinite(value) ? value : 0);
}

function formatPrintDate(value?: string | null): string {
  return value ? toRuDate(value) || value : '—';
}

function createPaymentPrintWindow(): Window {
  const printWindow = window.open('', '_blank', 'width=980,height=900');
  if (!printWindow) {
    throw new Error('Браузер заблокировал окно печати. Разрешите всплывающие окна и повторите.');
  }

  return printWindow;
}

function renderPaymentPrintWindow(printWindow: Window, html: string): void {
  printWindow.document.open();
  printWindow.document.write(html);
  printWindow.document.close();
  window.setTimeout(() => {
    try {
      printWindow.focus();
      printWindow.print();
    } catch (error) {
      console.error('payment print failed', error);
    }
  }, 250);
}

function parseDecimal(value: string): number {
  if (!value) return 0;
  const normalized = value.replace(',', '.').replace(/[^0-9.-]/g, '');
  const num = Number(normalized);
  return Number.isFinite(num) ? num : 0;
}

function optionalPositiveId(value: number | null | undefined): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : undefined;
}

function parseOptionalId(value: string | null | undefined): number | undefined {
  if (!value) return undefined;
  const id = parseInt(value, 10);
  return Number.isFinite(id) && id > 0 ? id : undefined;
}

function normalizeInvoiceNumber(value: string | null | undefined): string {
  return (value ?? '')
    .trim()
    .replace(/^№\s*/i, '')
    .replace(/\s+/g, '')
    .toLowerCase();
}

function matchesInvoiceReference(
  invoice: Pick<Invoice, 'number' | 'date'>,
  account: string,
  accountDate: string,
): boolean {
  if (normalizeInvoiceNumber(invoice.number) !== normalizeInvoiceNumber(account)) {
    return false;
  }

  const normalizedAccountDate = toDateInputValue(accountDate);
  if (!normalizedAccountDate) {
    return true;
  }

  return toDateInputValue(invoice.date) === normalizedAccountDate;
}

function paymentMatchesInvoiceReference(
  payment: Pick<Payment, 'account' | 'accountDate'>,
  account: string,
  accountDate: string,
): boolean {
  const normalizedAccount = normalizeInvoiceNumber(account);
  if (!normalizedAccount) {
    return true;
  }

  const paymentAccount = normalizeInvoiceNumber(payment.account);
  if (paymentAccount && paymentAccount !== normalizedAccount) {
    return false;
  }

  const normalizedAccountDate = toDateInputValue(accountDate);
  const paymentAccountDate = toDateInputValue(payment.accountDate);
  return !normalizedAccountDate || !paymentAccountDate || paymentAccountDate === normalizedAccountDate;
}

function formatMoneyDisplayValue(value: number | string | null | undefined): string {
  const num =
    typeof value === 'number'
      ? value
      : typeof value === 'string' && value.trim()
      ? parseDecimal(value)
      : NaN;

  if (!Number.isFinite(num)) return '';

  const [intPart, fracPart] = num.toFixed(2).split('.');
  return `${intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ' ')},${fracPart}`;
}

function normalizeMoneyInputValue(value: string): string {
  if (!value.trim()) return '';
  const num = parseDecimal(value);
  return Number.isFinite(num) ? num.toFixed(2) : '';
}

function resolvePaymentOutstandingAmount(payment: Pick<Payment, 'amount' | 'paidAmount' | 'outstandingAmount'>): number {
  if (typeof payment.outstandingAmount === 'number' && Number.isFinite(payment.outstandingAmount)) {
    return Math.max(payment.outstandingAmount, 0);
  }

  const amount = typeof payment.amount === 'number' && Number.isFinite(payment.amount) ? payment.amount : 0;
  const paid = typeof payment.paidAmount === 'number' && Number.isFinite(payment.paidAmount) ? payment.paidAmount : 0;
  return Math.max(Number((amount - Math.min(paid, amount)).toFixed(2)), 0);
}

const LEGACY_INSTALLMENT_DESCRIPTION_RE = /Рассрочка\s+\d+\s*\/\s*\d+/i;

function isInstallmentPaymentCandidate(
  payment: Pick<Payment, 'id' | 'amount' | 'documentKind' | 'systemNotes' | 'description'>,
  invoicePayments?: Array<Pick<Payment, 'id' | 'amount' | 'documentKind' | 'systemNotes'>>,
): boolean {
  if (parseInstallmentPaymentMetadata(payment.systemNotes) !== null || payment.systemNotes?.includes('[pp:installment-payment]')) {
    return true;
  }

  if (payment.description && LEGACY_INSTALLMENT_DESCRIPTION_RE.test(payment.description)) {
    return true;
  }

  return isLegacyInstallmentSibling(payment, invoicePayments);
}

function isLegacyInstallmentSibling(
  payment: Pick<Payment, 'id' | 'amount' | 'documentKind' | 'systemNotes'>,
  invoicePayments?: Array<Pick<Payment, 'id' | 'amount' | 'documentKind' | 'systemNotes'>>,
): boolean {
  if (!invoicePayments || invoicePayments.length < 3 || invoicePayments.some((item) => item.systemNotes?.includes('[pp:installment-payment]'))) {
    return false;
  }

  const representativeInvoice = invoicePayments
    .filter((item) => item.documentKind === 'Invoice')
    .sort((a, b) => (b.amount ?? 0) - (a.amount ?? 0) || a.id - b.id)[0];
  if (!representativeInvoice || representativeInvoice.id === payment.id) {
    return false;
  }

  const maxSiblingAmount = Math.max(
    0,
    ...invoicePayments
      .filter((item) => item.id !== representativeInvoice.id)
      .map((item) => (typeof item.amount === 'number' && Number.isFinite(item.amount) ? item.amount : 0)),
  );

  const amount = typeof payment.amount === 'number' && Number.isFinite(payment.amount) ? payment.amount : 0;
  return (representativeInvoice.amount ?? 0) > maxSiblingAmount + 0.005 && amount <= maxSiblingAmount + 0.005;
}

function getLegalEntityDisplayName(entity: LegalEntitySummary): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юр. лицо #${entity.id}`;
}

function getContractDisplayName(contract: Pick<Contract, 'id' | 'number' | 'date' | 'title'>): string {
  const number = contract.number?.trim() || `#${contract.id}`;
  const date = contract.date ? toRuDate(contract.date) || contract.date : '';
  const title = contract.title?.trim();
  return `Договор №${number}${date ? ` от ${date}` : ''}${title ? ` · ${title}` : ''}`;
}

interface PaymentModalProps {
  defaultClientId?: number;
  defaultLegalEntityId?: number;
  defaultExpenseClientId?: number;
  defaultExpenseLegalEntityId?: number;
  defaultClientCaseId?: number;
  casesPrefetch?: ClientCase[];
  type?: PaymentKind;
  linkedInvoice?: LinkedInvoiceContext | null;
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (payment: PaymentPayload | ({ id: number } & PaymentPayload)) => Promise<unknown>;
  payment?: Payment | null;
  onDelete?: (id: number) => Promise<void>;
  onCreateRemainder?: (payment: PaymentPayload) => Promise<unknown>;
  onTransferRemainder?: (
    payment: { id: number } & PaymentPayload,
    remainderDate: string,
  ) => Promise<unknown>;
  onRollbackRemainderTransfer?: (paymentId: number) => Promise<unknown>;
  onAdjustRemainderTransfer?: (
    paymentId: number,
    paidAmount: number,
    remainderDate: string,
  ) => Promise<unknown>;
  availableClientBalance?: number;
  onApplyClientBalance?: (amount: number) => Promise<ApplyClientBalanceResult | void>;
  onBalanceApplied?: (paymentId: number) => Promise<unknown> | unknown;
  canViewLinkedInvoices?: boolean;
  canCreateLinkedInvoices?: boolean;
}

export function PaymentModal({
  isOpen,
  onClose,
  onSubmit,
  payment,
  onDelete,
  onCreateRemainder,
  onTransferRemainder,
  onRollbackRemainderTransfer,
  onAdjustRemainderTransfer,
  availableClientBalance,
  onApplyClientBalance,
  onBalanceApplied,
  defaultClientId,
  defaultLegalEntityId,
  defaultExpenseClientId,
  defaultExpenseLegalEntityId,
  defaultClientCaseId,
  casesPrefetch,
  type,
  linkedInvoice,
  canViewLinkedInvoices = true,
  canCreateLinkedInvoices = true,
}: PaymentModalProps) {
  const formRef = useRef<HTMLFormElement>(null);
  const autoCashOrderDraftRef = useRef<{ number: string; date: string; type: CashOrderType } | null>(null);

  const { clients, loading: clientsLoading } = useClients();
  const { legalEntities, loading: legalEntitiesLoading, error: legalEntitiesError } = useLegalEntities();
  const { dealTypes, incomeTypes, paymentSources, services = [] } = useDictionaries();
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const { isRequired } = useFormRequirements('payments', isOpen);
  const profileTimeZoneId = useUserTimeZone();
  const defaultDate = todayYMDInTimeZone(profileTimeZoneId);
  const defaultDateRef = useRef(defaultDate);

  const [loading, setLoading] = useState(false);
  const [paymentPrinting, setPaymentPrinting] = useState(false);
  const [printMenuOpen, setPrintMenuOpen] = useState(false);
  const [balanceApplyLoading, setBalanceApplyLoading] = useState(false);
  const [balanceApplyAmount, setBalanceApplyAmount] = useState('');
  const [appliedClientBalanceAmount, setAppliedClientBalanceAmount] = useState(0);
  const [localClientBalance, setLocalClientBalance] = useState<number | null>(null);
  const [fetchedClientBalance, setFetchedClientBalance] = useState(0);
  const [clientBalanceLoading, setClientBalanceLoading] = useState(false);
  const [overpaymentHandling, setOverpaymentHandling] = useState<PaymentOverpaymentHandling | ''>('');
  const [overpaymentTargetPaymentId, setOverpaymentTargetPaymentId] = useState<number | null>(null);
  const [overpaymentComment, setOverpaymentComment] = useState('');
  const [installmentPayments, setInstallmentPayments] = useState<Payment[]>([]);
  const [installmentPaymentsLoading, setInstallmentPaymentsLoading] = useState(false);
  const [installmentPaymentsError, setInstallmentPaymentsError] = useState<string | null>(null);
  const [installmentTargetDialogMode, setInstallmentTargetDialogMode] = useState<'overpayment' | 'balance' | null>(null);
  const [installmentTargetDraftId, setInstallmentTargetDraftId] = useState<number | null>(null);
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [fallbackCase, setFallbackCase] = useState<ClientCase | null>(null);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [contractsLoading, setContractsLoading] = useState(false);
  const [contractsError, setContractsError] = useState<string | null>(null);

  const [formData, setFormData] = useState({
    date: defaultDate,
    plannedDate: defaultDate,
    amount: '',
    paidAmount: '',
    status: 'Pending' as PaymentStatus,
    description: '',
    isPaid: false,
    paidDate: '',
    lastPaymentDate: '',
    notes: '',
    clientId: '',
    legalEntityId: '',
    expenseClientId: '',
    expenseLegalEntityId: '',
    clientCaseId: '',
    dealTypeId: '',
    incomeTypeId: '',
    serviceId: '',
    paymentSourceId: '',
    paymentStatusId: '',
    invoiceId: '',
    contractId: '',
    type: (type ?? 'Income') as PaymentKind,
    paymentOrderNumber: '',
    paymentDocumentDate: '',
    account: '',
    accountDate: '',
    systemNotes: '',
    rescheduleCount: 0,
  });
  const systemNotesEntries = useMemo(() => {
    return getVisibleSystemNoteLines(formData.systemNotes);
  }, [formData.systemNotes]);
  const transferTrail = useMemo(() => {
    if (!systemNotesEntries.length) return [] as string[];
    return systemNotesEntries.filter((note) => /остаток|перенес|источник платежа/i.test(note));
  }, [systemNotesEntries]);
  const selectedClient = useMemo(
    () => clients.find((c) => String(c.id) === formData.clientId) ?? null,
    [clients, formData.clientId],
  );
  const selectedLegalEntity = useMemo(
    () => legalEntities.find((entity) => String(entity.id) === formData.legalEntityId) ?? null,
    [legalEntities, formData.legalEntityId],
  );
  const selectedExpenseClient = useMemo(
    () => clients.find((c) => String(c.id) === formData.expenseClientId) ?? null,
    [clients, formData.expenseClientId],
  );
  const selectedExpenseLegalEntity = useMemo(
    () => legalEntities.find((entity) => String(entity.id) === formData.expenseLegalEntityId) ?? null,
    [legalEntities, formData.expenseLegalEntityId],
  );
  const selectedContract = useMemo(
    () => contracts.find((contract) => String(contract.id) === formData.contractId) ?? null,
    [contracts, formData.contractId],
  );
  const contractOptions = useMemo(() => {
    if (!formData.contractId || selectedContract) {
      return contracts;
    }

    return [
      {
        id: Number(formData.contractId),
        number: formData.contractId,
        title: null,
        date: '',
        billingMode: 'Fixed',
        isOpenEnded: true,
        createdAt: '',
        invoiceAmountTotal: 0,
        actAmountTotal: 0,
        paymentAmountTotal: 0,
        clients: [],
      } as Contract,
      ...contracts,
    ];
  }, [contracts, formData.contractId, selectedContract]);
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientPickerMode, setClientPickerMode] = useState<'client' | 'expense'>('client');

  const [syncDateWithPayment, setSyncDateWithPayment] = useState(false);

  const [invoicePickerOpen, setInvoicePickerOpen] = useState(false);
  const [invoicePickerLoading, setInvoicePickerLoading] = useState(false);
  const [invoicePickerError, setInvoicePickerError] = useState<string | null>(null);
  const [invoiceOptions, setInvoiceOptions] = useState<Invoice[]>([]);
  const [invoiceSearch, setInvoiceSearch] = useState('');
  const [invoiceCreateOpen, setInvoiceCreateOpen] = useState(false);
  const [invoiceCreateSubmitting, setInvoiceCreateSubmitting] = useState(false);
  const [invoiceCreateError, setInvoiceCreateError] = useState<string | null>(null);
  const [invoiceCreateSeedContractId, setInvoiceCreateSeedContractId] = useState('');
  const [invoiceCreateResponsibles, setInvoiceCreateResponsibles] = useState<ActResponsible[]>([]);
  const [invoiceCreateLookupsLoading, setInvoiceCreateLookupsLoading] = useState(false);
  const [invoiceCreateLookupsError, setInvoiceCreateLookupsError] = useState<string | null>(null);
  const [rescheduleRemainder, setRescheduleRemainder] = useState(false);
  const [rescheduleDate, setRescheduleDate] = useState<string>('');
  const [paymentMoveOpen, setPaymentMoveOpen] = useState(false);
  const [paymentMoveDate, setPaymentMoveDate] = useState('');
  const [remainderToolsOpen, setRemainderToolsOpen] = useState(false);
  const [adjustPaidAmount, setAdjustPaidAmount] = useState('');
  const [adjustRemainderDate, setAdjustRemainderDate] = useState('');
  const [showProcessHelp, setShowProcessHelp] = useState(false);
  const [detailsTab, setDetailsTab] = useState<'details' | 'system'>('details');
  const hasLockedInvoice = Boolean(linkedInvoice?.invoiceEntityId);
  const canChangePaymentType = !hasLockedInvoice || !payment;
  const canEditExpenseAttribution = !hasLockedInvoice || (!payment && formData.type === 'Expense');
  const canEditContract = !hasLockedInvoice || !payment;
  const caseOwnerClientId = formData.type === 'Expense'
    ? formData.expenseClientId
    : formData.clientId;
  const caseOwnerLegalEntityId = formData.type === 'Expense'
    ? formData.expenseLegalEntityId
    : formData.legalEntityId;
  const caseOwnerKey = caseOwnerClientId
    ? `client:${caseOwnerClientId}`
    : caseOwnerLegalEntityId
      ? `legal:${caseOwnerLegalEntityId}`
      : '';
  const selectedPaymentSource = useMemo(
    () => paymentSources.find((source) => String(source.id) === formData.paymentSourceId) ?? null,
    [formData.paymentSourceId, paymentSources],
  );
  const isCashPaymentSource = isCashPaymentSourceName(selectedPaymentSource?.name);
  const isIncomingCashPayment = formData.type === 'Income' && isCashPaymentSource;
  const isOutgoingCashPayment = formData.type === 'Expense' && isCashPaymentSource;
  const cashOrderType: CashOrderType = formData.type === 'Expense' ? 'Outgoing' : 'Incoming';
  const showPaymentDocumentFields = Boolean(
    formData.paymentOrderNumber
    || formData.paymentDocumentDate
    || selectedPaymentSource,
  );
  const paymentDocumentNumberLabel = isIncomingCashPayment
    ? '№ приходного кассового ордера'
    : isOutgoingCashPayment
      ? '№ расходного кассового ордера'
      : '№ документа';
  const paymentDocumentDateLabel = isIncomingCashPayment
    ? 'Дата приходного кассового ордера'
    : isOutgoingCashPayment
      ? 'Дата расходного кассового ордера'
      : 'Дата документа';
  const paymentDocumentNumberPlaceholder = isIncomingCashPayment
    ? 'Например: ПКО-2026-0001'
    : isOutgoingCashPayment
      ? 'Например: РКО-2026-0001'
      : 'Например: п/п 458 или ордер 17';
  const selectedInvoiceSummary = useMemo(() => {
    if (!formData.invoiceId) return null;

    const selectedId = parseInt(formData.invoiceId, 10);
    const matched = invoiceOptions.find((item) => item.invoiceEntityId === selectedId) ?? null;
    if (matched) {
      return {
        id: selectedId,
        title: `Счет № ${matched.number}`,
        meta: `${toRuDate(matched.date) || matched.date || '—'}${matched.clientCaseTitle ? ` · ${matched.clientCaseTitle}` : ''}`,
      };
    }

    return {
      id: selectedId,
      title: formData.account?.trim() ? `Счет № ${formData.account.trim()}` : `Счет #${selectedId}`,
      meta: formData.accountDate ? toRuDate(formData.accountDate) || formData.accountDate : 'Связь со счетом установлена',
    };
  }, [formData.account, formData.accountDate, formData.invoiceId, invoiceOptions]);
  const invoiceCreateDraft = useMemo<InvoiceEditorDraftState | null>(() => {
    if (!invoiceCreateOpen) return null;

    const clientId = formData.legalEntityId ? '' : formData.clientId || (defaultClientId ? String(defaultClientId) : '');
    const clientCaseId = formData.clientCaseId ? Number(formData.clientCaseId) : null;
    const invoiceDate = formData.accountDate || formData.date || defaultDate;
    const dueDate = formData.date || invoiceDate;
    const amount = formData.amount.trim();
    const description = formData.description.trim();
    const lineItemName = description || 'Платеж';

    return {
      invoiceId: null,
      mode: 'create',
      presentation: 'modal',
      pageViewMode: false,
      form: {
        number: formData.account.trim(),
        date: invoiceDate,
        dueDate,
        amount,
        type: formData.type,
        status: formData.status,
        workflowStatus: 'Draft',
        clientId,
        legalEntityId: formData.legalEntityId,
        expenseClientId: formData.type === 'Expense' ? formData.expenseClientId : '',
        expenseLegalEntityId: formData.type === 'Expense' ? formData.expenseLegalEntityId : '',
        contractId: formData.contractId || invoiceCreateSeedContractId,
        clientCaseReference: '',
        description,
        isNotForAccounting: false,
        actReference: '',
        paidDate: formData.paidDate || '',
        responsibleIds: [],
      },
      lineItems: [
        {
          id: `${Date.now()}-linked-invoice`,
          name: lineItemName,
          quantity: '1',
          unit: 'усл. ед.',
          price: amount,
          serviceCategoryId: '',
          serviceId: '',
        },
      ],
      selectedActs: [],
      selectedClientCaseIds: clientCaseId ? [clientCaseId] : [],
    };
  }, [defaultClientId, defaultDate, formData.account, formData.accountDate, formData.amount, formData.clientCaseId, formData.contractId, formData.date, formData.description, formData.expenseClientId, formData.expenseLegalEntityId, formData.legalEntityId, formData.paidDate, formData.status, formData.type, formData.clientId, invoiceCreateOpen, invoiceCreateSeedContractId]);

  useEffect(() => {
    if (!isOpen || !invoicePickerOpen || hasLockedInvoice || !canViewLinkedInvoices) {
      setInvoicePickerLoading(false);
      setInvoicePickerError(null);
      return;
    }

    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    const legalEntityId = formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined;
    if (!clientId && !legalEntityId) {
      setInvoiceOptions([]);
      setInvoicePickerLoading(false);
      setInvoicePickerError('Сначала выберите контрагента.');
      return;
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        setInvoicePickerLoading(true);
        setInvoicePickerError(null);
        const caseId = formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined;
        const response = await apiService.getInvoices({
          clientId,
          legalEntityId,
          caseId,
          type: formData.type,
          search:
            invoiceSearch.trim() ||
            (!formData.invoiceId && formData.account.trim() ? formData.account.trim() : undefined),
          page: 1,
          pageSize: 20,
        });

        if (cancelled) return;

        const canonicalItems = (response.items ?? []).filter(
          (item) => typeof item.invoiceEntityId === 'number' && item.invoiceEntityId > 0,
        );
        setInvoiceOptions(canonicalItems);
      } catch (e) {
        if (!cancelled) {
          setInvoiceOptions([]);
          setInvoicePickerError(e instanceof Error ? e.message : 'Не удалось загрузить счета контрагента.');
        }
      } finally {
        if (!cancelled) {
          setInvoicePickerLoading(false);
        }
      }
    }, 200);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [
    formData.account,
    formData.clientCaseId,
    formData.clientId,
    formData.legalEntityId,
    formData.invoiceId,
    formData.type,
    canViewLinkedInvoices,
    hasLockedInvoice,
    invoicePickerOpen,
    invoiceSearch,
    isOpen,
  ]);

  useEffect(() => {
    if (!isOpen) {
      setContracts([]);
      setContractsLoading(false);
      setContractsError(null);
      return;
    }

    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    const legalEntityId = formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined;

    if (!clientId && !legalEntityId) {
      setContracts([]);
      setContractsLoading(false);
      setContractsError(null);
      return;
    }

    let cancelled = false;
    const loadContracts = async () => {
      try {
        setContractsLoading(true);
        setContractsError(null);
        const response = await apiService.getContracts({
          clientId,
          legalEntityId,
          sortBy: 'date',
          sortDir: 'desc',
          page: 1,
          pageSize: 100,
        });
        if (!cancelled) {
          setContracts(response.items ?? []);
        }
      } catch (error) {
        if (!cancelled) {
          setContracts([]);
          setContractsError(error instanceof Error ? error.message : 'Не удалось загрузить договоры контрагента.');
        }
      } finally {
        if (!cancelled) {
          setContractsLoading(false);
        }
      }
    };

    void loadContracts();

    return () => {
      cancelled = true;
    };
  }, [formData.clientId, formData.legalEntityId, isOpen]);

  useEffect(() => {
    if (!isOpen || !isCashPaymentSource) {
      autoCashOrderDraftRef.current = null;
      return;
    }

    const documentDate =
      formData.paymentDocumentDate ||
      formData.lastPaymentDate ||
      formData.paidDate ||
      formData.date ||
      defaultDate;
    const currentNumber = formData.paymentOrderNumber.trim();
    const currentDraft = autoCashOrderDraftRef.current;

    if (
      currentNumber &&
      currentDraft &&
      currentNumber === currentDraft.number &&
      currentDraft.date === documentDate &&
      currentDraft.type === cashOrderType
    ) {
      return;
    }

    if (currentNumber && currentNumber !== currentDraft?.number) {
      const canReplaceNonCashNumber = !currentDraft && !isCashOrderNumberForType(currentNumber, cashOrderType);
      if (!canReplaceNonCashNumber) {
        return;
      }
    }

    let cancelled = false;
    const previousDraftNumber = currentDraft?.number ?? '';
    const numberBeforeRequest = currentNumber;
    const shouldReplaceNonCashNumber = Boolean(
      currentNumber &&
      !currentDraft &&
      !isCashOrderNumberForType(currentNumber, cashOrderType),
    );
    void apiService
      .getNextCashOrderNumber(cashOrderType, documentDate)
      .then((result) => {
        if (cancelled || !result?.number) return;

        const resultDate = toDateInputValue(result.date) || documentDate;
        setFormData((prev) => {
          const prevNumber = prev.paymentOrderNumber.trim();
          if (
            prevNumber &&
            prevNumber !== previousDraftNumber &&
            (!shouldReplaceNonCashNumber || prevNumber !== numberBeforeRequest)
          ) {
            return prev;
          }

          autoCashOrderDraftRef.current = {
            number: result.number,
            date: resultDate,
            type: result.type ?? cashOrderType,
          };

          if (prev.paymentOrderNumber === result.number && prev.paymentDocumentDate === resultDate) {
            return prev;
          }

          return {
            ...prev,
            paymentOrderNumber: result.number,
            paymentDocumentDate: prev.paymentDocumentDate || resultDate,
          };
        });
      })
      .catch(() => {
        // Если номер заранее получить не удалось, сервер присвоит его при сохранении платежа.
      });

    return () => {
      cancelled = true;
    };
  }, [
    cashOrderType,
    formData.date,
    formData.lastPaymentDate,
    formData.paidDate,
    formData.paymentDocumentDate,
    formData.paymentOrderNumber,
    defaultDate,
    isCashPaymentSource,
    isOpen,
  ]);

  useEffect(() => {
    if (!invoiceCreateOpen) return;
    if (invoiceCreateResponsibles.length > 0 || invoiceCreateLookupsLoading) return;

    let cancelled = false;
    void (async () => {
      try {
        setInvoiceCreateLookupsLoading(true);
        setInvoiceCreateLookupsError(null);
        const responsibles = await apiService.getActResponsibles();
        if (!cancelled) {
          setInvoiceCreateResponsibles(responsibles ?? []);
        }
      } catch (e) {
        if (!cancelled) {
          setInvoiceCreateLookupsError(e instanceof Error ? e.message : 'Не удалось загрузить ответственных.');
        }
      } finally {
        if (!cancelled) {
          setInvoiceCreateLookupsLoading(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [invoiceCreateLookupsLoading, invoiceCreateOpen, invoiceCreateResponsibles.length]);

  const prevOpenRef = useRef(false);
  const paymentRef = useRef<Payment | null>(null);

  useEffect(() => {
    if (paymentRef.current?.id !== payment?.id) {
      paymentRef.current = payment ?? null;
    }
  }, [payment]);

  const formatMoneyString = (value: number | null | undefined) => formatMoneyDisplayValue(value);

  useEffect(() => {
    if (!isOpen) {
      prevOpenRef.current = false;
      defaultDateRef.current = defaultDate;
      setSyncDateWithPayment(false);
      setDetailsTab('details');
      setClientPickerOpen(false);
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      setInvoiceCreateSeedContractId('');
      setPaymentMoveOpen(false);
      setPaymentMoveDate('');
      setFetchedClientBalance(0);
      setBalanceApplyAmount('');
      setAppliedClientBalanceAmount(0);
      setLocalClientBalance(null);
      setOverpaymentHandling('');
      setOverpaymentTargetPaymentId(null);
      setOverpaymentComment('');
      setInstallmentPayments([]);
      setInstallmentPaymentsError(null);
      setInstallmentTargetDialogMode(null);
      setInstallmentTargetDraftId(null);
      return;
    }

    if (!prevOpenRef.current) {
      setLocalClientBalance(null);
      setBalanceApplyAmount('');
      setOverpaymentHandling('');
      setOverpaymentTargetPaymentId(null);
      setOverpaymentComment('');
      setInstallmentPaymentsError(null);
      setInstallmentTargetDialogMode(null);
      setInstallmentTargetDraftId(null);
      const p = paymentRef.current;

      if (p) {
        const appliedBalanceAmount = getAppliedClientBalanceAmount(p.systemNotes);
        const resolvedLegalEntityId = linkedInvoice?.legalEntityId ?? p.legalEntityId ?? defaultLegalEntityId ?? null;
        const resolvedClientId = linkedInvoice?.clientId ?? p.clientId ?? defaultClientId ?? null;
        let resolvedExpenseLegalEntityId = linkedInvoice?.expenseLegalEntityId ?? p.expenseLegalEntityId ?? defaultExpenseLegalEntityId ?? null;
        let resolvedExpenseClientId = linkedInvoice?.expenseClientId ?? p.expenseClientId ?? defaultExpenseClientId ?? null;
        if (
          p.type === 'Expense'
          && p.clientCaseId
          && !resolvedExpenseClientId
          && !resolvedExpenseLegalEntityId
        ) {
          resolvedExpenseClientId = p.clientId ?? null;
          resolvedExpenseLegalEntityId = resolvedExpenseClientId ? null : p.legalEntityId ?? null;
        }
        setAppliedClientBalanceAmount(appliedBalanceAmount);
        setFormData({
          date: toDateInputValue(p.date) || defaultDate,
          plannedDate: toDateInputValue(p.plannedDate) || toDateInputValue(p.date) || defaultDate,
          amount: formatMoneyString(p.amount),
          paidAmount: formatMoneyString(Math.max(p.paidAmount ?? 0, appliedBalanceAmount)),
          status: p.status,
          description: p.description,
          isPaid: p.isPaid,
          paidDate: toDateInputValue(p.paidDate) || '',
          lastPaymentDate: toDateInputValue(p.lastPaymentDate) || '',
          notes: p.notes || '',
          clientId: resolvedLegalEntityId ? '' : resolvedClientId?.toString() || '',
          legalEntityId: resolvedLegalEntityId?.toString() || '',
          expenseClientId: resolvedExpenseLegalEntityId ? '' : resolvedExpenseClientId?.toString() || '',
          expenseLegalEntityId: resolvedExpenseLegalEntityId?.toString() || '',
          clientCaseId:
            (p.clientCaseId ?? linkedInvoice?.clientCaseId ?? defaultClientCaseId)?.toString() || '',
          dealTypeId: p.dealTypeId?.toString() || '',
          incomeTypeId: p.incomeTypeId?.toString() || '',
          serviceId: p.serviceId?.toString() || '',
          paymentSourceId: p.paymentSourceId?.toString() || '',
          paymentStatusId: p.paymentStatusId?.toString() || '',
          invoiceId: (p.invoiceId ?? linkedInvoice?.invoiceEntityId)?.toString() || '',
          contractId: (p.contractId ?? linkedInvoice?.contractId)?.toString() || '',
          type: (p.type as PaymentKind) ?? linkedInvoice?.type ?? type ?? 'Income',
          paymentOrderNumber: p.paymentOrderNumber ?? '',
          paymentDocumentDate: toDateInputValue(p.paymentDocumentDate) || '',
          account: linkedInvoice?.number ?? p.account ?? '',
          accountDate: toDateInputValue(linkedInvoice?.date) || toDateInputValue(p.accountDate) || '',
          systemNotes: p.systemNotes ?? '',
          rescheduleCount: p.rescheduleCount ?? 0,
        });
        setSyncDateWithPayment(Boolean(p.hasPartialPayment && p.lastPaymentDate));
        setClientPickerOpen(false);
        setInvoicePickerOpen(false);
        setInvoiceSearch('');
        setInvoicePickerError(null);
      } else {
        const resolvedLegalEntityId = linkedInvoice?.legalEntityId ?? defaultLegalEntityId ?? null;
        const resolvedClientId = linkedInvoice?.clientId ?? defaultClientId ?? null;
        const resolvedExpenseLegalEntityId = linkedInvoice?.expenseLegalEntityId ?? defaultExpenseLegalEntityId ?? null;
        const resolvedExpenseClientId = linkedInvoice?.expenseClientId ?? defaultExpenseClientId ?? null;
        setAppliedClientBalanceAmount(0);
        setFormData({
          date: defaultDate,
          plannedDate: defaultDate,
          amount: '',
          paidAmount: '',
          status: 'Pending',
          description: '',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: '',
          notes: '',
          clientId: resolvedLegalEntityId ? '' : resolvedClientId?.toString() || '',
          legalEntityId: resolvedLegalEntityId?.toString() || '',
          expenseClientId: resolvedExpenseLegalEntityId ? '' : resolvedExpenseClientId?.toString() || '',
          expenseLegalEntityId: resolvedExpenseLegalEntityId?.toString() || '',
          clientCaseId: (linkedInvoice?.clientCaseId ?? defaultClientCaseId)?.toString() || '',
          dealTypeId: '',
          incomeTypeId: '',
          serviceId: '',
          paymentSourceId: '',
          paymentStatusId: '',
          invoiceId: linkedInvoice?.invoiceEntityId?.toString() || '',
          contractId: linkedInvoice?.contractId?.toString() || '',
          type: (linkedInvoice?.type ?? type ?? 'Income') as PaymentKind,
          paymentOrderNumber: '',
          paymentDocumentDate: '',
          account: linkedInvoice?.number ?? '',
          accountDate: toDateInputValue(linkedInvoice?.date) || '',
          systemNotes: '',
          rescheduleCount: 0,
        });
        setSyncDateWithPayment(false);
        setClientPickerOpen(false);
        setInvoicePickerOpen(false);
        setInvoiceSearch('');
        setInvoicePickerError(null);
      }

      const fallbackDate =
        (p ? toDateInputValue(p.plannedDate) || toDateInputValue(p.date) : null) || defaultDate;
      const splitMetadata = p ? parsePartialRemainderTransferMetadata(p) : null;
      setRescheduleDate(fallbackDate);
      setPaymentMoveDate(fallbackDate);
      setPaymentMoveOpen(false);
      setRescheduleRemainder(false);
      setRemainderToolsOpen(false);
      setAdjustPaidAmount(
        splitMetadata?.paidAmount !== undefined
          ? formatMoneyString(splitMetadata.paidAmount)
          : '',
      );
      setAdjustRemainderDate(toDateInputValue(splitMetadata?.remainderDate) || fallbackDate);

      defaultDateRef.current = defaultDate;
      prevOpenRef.current = true;
    }
  // defaultDate is synchronized by the next effect while the modal stays open;
  // adding it here would reset user-entered fields on date changes.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [defaultClientCaseId, defaultClientId, defaultExpenseClientId, defaultExpenseLegalEntityId, defaultLegalEntityId, isOpen, linkedInvoice, type]);

  useEffect(() => {
    if (!isOpen || paymentRef.current) {
      defaultDateRef.current = defaultDate;
      return;
    }

    const previousDefaultDate = defaultDateRef.current;
    setFormData((current) => ({
      ...current,
      date: current.date === previousDefaultDate ? defaultDate : current.date,
      plannedDate: current.plannedDate === previousDefaultDate ? defaultDate : current.plannedDate,
    }));
    setRescheduleDate((current) => (current === previousDefaultDate ? defaultDate : current));
    setPaymentMoveDate((current) => (current === previousDefaultDate ? defaultDate : current));
    setAdjustRemainderDate((current) => (current === previousDefaultDate ? defaultDate : current));
    defaultDateRef.current = defaultDate;
  }, [defaultDate, isOpen]);

  const getSelectedClientCase = () => (
    formData.clientCaseId
      ? cases.find((item) => String(item.id) === formData.clientCaseId) ?? fallbackCase
      : null
  );

  const resolveExpenseClientIdForSubmit = () => {
    if (formData.type !== 'Expense' || formData.expenseLegalEntityId) return undefined;

    const selectedCase = getSelectedClientCase();
    if (selectedCase?.legalEntityId) return undefined;

    return parseOptionalId(formData.expenseClientId)
      ?? optionalPositiveId(selectedCase?.clientId)
      ?? (
        payment?.type === 'Expense' && String(payment.clientCaseId ?? '') === formData.clientCaseId
          ? optionalPositiveId(payment.clientId)
          : undefined
      );
  };

  const resolveExpenseLegalEntityIdForSubmit = () => {
    if (formData.type !== 'Expense' || formData.expenseClientId) return undefined;

    const selectedCase = getSelectedClientCase();
    if (selectedCase?.clientId) return undefined;

    return parseOptionalId(formData.expenseLegalEntityId)
      ?? optionalPositiveId(selectedCase?.legalEntityId)
      ?? (
        payment?.type === 'Expense' && String(payment.clientCaseId ?? '') === formData.clientCaseId
          ? optionalPositiveId(payment.legalEntityId)
          : undefined
      );
  };

  useEffect(() => {
    if (!isOpen || availableClientBalance !== undefined) return;
    const clientId = Number(formData.clientId || 0);
    const legalEntityId = Number(formData.legalEntityId || 0);
    if (formData.type !== 'Income' || (!clientId && !legalEntityId)) {
      setFetchedClientBalance(0);
      return;
    }

    let cancelled = false;
    setClientBalanceLoading(true);
    const request = clientId > 0
      ? apiService.getClientBalance(clientId)
      : apiService.getLegalEntityBalance(legalEntityId);

    request
      .then((summary) => {
        if (!cancelled) setFetchedClientBalance(summary.balance ?? 0);
      })
      .catch(() => {
        if (!cancelled) setFetchedClientBalance(0);
      })
      .finally(() => {
        if (!cancelled) setClientBalanceLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [availableClientBalance, formData.clientId, formData.legalEntityId, formData.type, isOpen]);

  useEffect(() => {
    if (!isOpen || !formData.invoiceId) {
      setInstallmentPayments([]);
      setInstallmentPaymentsError(null);
      setInstallmentPaymentsLoading(false);
      setOverpaymentTargetPaymentId(null);
      return;
    }

    const invoiceId = Number(formData.invoiceId);
    if (!Number.isFinite(invoiceId) || invoiceId <= 0) {
      setInstallmentPayments([]);
      setInstallmentPaymentsError(null);
      setInstallmentPaymentsLoading(false);
      setOverpaymentTargetPaymentId(null);
      return;
    }

    let cancelled = false;
    const account = formData.account.trim();
    const accountDate = formData.accountDate;
    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    const legalEntityId = formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined;
    const caseId = formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined;
    setInstallmentPaymentsLoading(true);
    setInstallmentPaymentsError(null);

    const resolveInstallmentInvoiceId = async () => {
      if (!account || (!clientId && !legalEntityId)) {
        return invoiceId;
      }

      try {
        const response = await apiService.getInvoices({
          clientId,
          legalEntityId,
          caseId,
          type: formData.type,
          search: account,
          page: 1,
          pageSize: 20,
        });
        if (cancelled) {
          return invoiceId;
        }

        const match = (response.items ?? [])
          .filter((item) => typeof item.invoiceEntityId === 'number' && item.invoiceEntityId > 0)
          .find((item) => matchesInvoiceReference(item, account, accountDate));
        const canonicalId = match?.invoiceEntityId;
        if (!match || !canonicalId || canonicalId <= 0) {
          return invoiceId;
        }

        setInvoiceOptions((prev) => {
          if (prev.some((item) => item.invoiceEntityId === canonicalId)) {
            return prev;
          }

          return [match, ...prev];
        });

        if (canonicalId !== invoiceId) {
          setFormData((prev) => {
            if (prev.invoiceId !== String(invoiceId)) {
              return prev;
            }

            if (!matchesInvoiceReference({ number: account, date: accountDate }, prev.account, prev.accountDate)) {
              return prev;
            }

            return {
              ...prev,
              invoiceId: String(canonicalId),
              account: match.number,
              accountDate: toDateInputValue(match.date) || prev.accountDate,
            };
          });
        }

        return canonicalId;
      } catch {
        return invoiceId;
      }
    };

    resolveInstallmentInvoiceId()
      .then((resolvedInvoiceId) =>
        apiService.getPaymentsV2({ invoiceId: resolvedInvoiceId, type: 'Income', pageSize: 100, sortBy: 'date', sortDir: 'asc' }),
      )
      .then((result) => {
        if (cancelled) return;

        const activeItems = (result.items ?? []).filter((item) => item.status !== 'Cancelled');
        const items = activeItems
          .filter((item) => paymentMatchesInvoiceReference(item, account, accountDate))
          .filter((item) => isInstallmentPaymentCandidate(item, activeItems))
          .filter((item) => resolvePaymentOutstandingAmount(item) > 0.005)
          .sort((a, b) => {
            const metaA = parseInstallmentPaymentMetadata(a.systemNotes);
            const metaB = parseInstallmentPaymentMetadata(b.systemNotes);
            const sequenceCompare = (metaA?.sequence ?? Number.MAX_SAFE_INTEGER) - (metaB?.sequence ?? Number.MAX_SAFE_INTEGER);
            if (sequenceCompare !== 0) return sequenceCompare;
            const dateCompare = String(a.plannedDate || a.date).localeCompare(String(b.plannedDate || b.date));
            return dateCompare !== 0 ? dateCompare : a.id - b.id;
          });

        setInstallmentPayments(items);
        setOverpaymentTargetPaymentId((current) =>
          current && items.some((item) => item.id === current && item.id !== payment?.id) ? current : null,
        );
      })
      .catch((error) => {
        if (!cancelled) {
          setInstallmentPayments([]);
          setInstallmentPaymentsError(error instanceof Error ? error.message : 'Не удалось загрузить платежи рассрочки.');
          setOverpaymentTargetPaymentId(null);
        }
      })
      .finally(() => {
        if (!cancelled) setInstallmentPaymentsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [formData.account, formData.accountDate, formData.clientCaseId, formData.clientId, formData.invoiceId, formData.legalEntityId, formData.type, isOpen, payment?.id]);

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (clientPickerOpen) {
          setClientPickerOpen(false);
          return;
        }
        if (invoicePickerOpen) {
          setInvoicePickerOpen(false);
          return;
        }
        onClose();
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'enter') {
        formRef.current?.requestSubmit();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [clientPickerOpen, invoicePickerOpen, isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) return;
    setFormData((s) => {
      if (!s.amount?.trim()) return s;
      const formatted = formatMoneyDisplayValue(s.amount);
      return !formatted || s.amount === formatted ? s : { ...s, amount: formatted };
    });
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen || !rescheduleRemainder) return;
    const amount = parseDecimal(formData.amount);
    const paid = parseDecimal(formData.paidAmount);
    const hasPartialPayment = paid > 0 && amount - paid > 0;
    if (!hasPartialPayment) setRescheduleRemainder(false);
  }, [formData.amount, formData.paidAmount, isOpen, rescheduleRemainder]);

  const clientIdRef = useRef(formData.clientId);
  useEffect(() => {
    clientIdRef.current = formData.clientId;
  }, [formData.clientId]);

  const caseIdRef = useRef(formData.clientCaseId);
  useEffect(() => {
    caseIdRef.current = formData.clientCaseId;
  }, [formData.clientCaseId]);

  useEffect(() => {
    if (!isOpen || !syncDateWithPayment) return;
    setFormData((prev) => {
      if (!prev.lastPaymentDate) return prev;
      if (prev.date === prev.lastPaymentDate) return prev;
      return { ...prev, date: prev.lastPaymentDate, plannedDate: prev.lastPaymentDate };
    });
  }, [formData.lastPaymentDate, syncDateWithPayment, isOpen]);

  useEffect(() => {
    if (!isOpen) return;

    let cancelled = false;

    const currentClientId = caseOwnerClientId
      || (caseOwnerLegalEntityId ? '' : (formData.type === 'Expense' ? '' : (defaultClientId ? String(defaultClientId) : '')));
    const currentLegalEntityId = caseOwnerLegalEntityId;
    const initialCaseId =
      formData.clientCaseId || (defaultClientCaseId ? String(defaultClientCaseId) : '');
    const cidNum = Number(currentClientId);
    const legalEntityIdNum = Number(currentLegalEntityId);

    const filterPrefetchForOwner = (all?: ClientCase[]) =>
      (all ?? []).filter((c) =>
        currentClientId
          ? String(c.clientId) === String(currentClientId)
          : String(c.legalEntityId) === String(currentLegalEntityId),
      );

    const load = async () => {
      if (!cidNum && !legalEntityIdNum) {
        if (!cancelled) {
          setCases([]);
          setFallbackCase(null);
        }
        return;
      }

      let list: ClientCase[] = filterPrefetchForOwner(casesPrefetch);

      if (!list.length) {
        try {
          list = await apiService.getCases({
            clientId: cidNum || undefined,
            legalEntityId: legalEntityIdNum || undefined,
          });
        } catch {
          list = [];
        }
        if (cancelled) return;
      }

      if (initialCaseId) {
        const present = list.some((c) => String(c.id) === String(initialCaseId));
        if (!present) {
          try {
            const one = await apiService.getCase(Number(initialCaseId));
            if (cancelled) return;

            const caseMatchesOwner = one
              ? currentClientId
                ? String(one.clientId) === String(currentClientId)
                : String(one.legalEntityId) === String(currentLegalEntityId)
              : false;

            if (one && caseMatchesOwner) {
              list = [...list, one];
              setFallbackCase(one);
            } else {
              setFallbackCase(null);
              setFormData((s) => {
                if (s.clientId !== currentClientId || s.legalEntityId !== currentLegalEntityId) return s;
                if (s.clientCaseId !== initialCaseId) return s;
                return { ...s, clientCaseId: '' };
              });
            }
          } catch {
            if (!cancelled) {
              setFallbackCase(null);
              setFormData((s) => {
                if (s.clientId !== currentClientId || s.legalEntityId !== currentLegalEntityId) return s;
                if (s.clientCaseId !== initialCaseId) return s;
                return { ...s, clientCaseId: '' };
              });
            }
          }
        } else {
          setFallbackCase(null);
        }
      } else {
        setFallbackCase(null);
      }

      if (!cancelled) setCases(list);
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [
    isOpen,
    caseOwnerClientId,
    caseOwnerLegalEntityId,
    formData.type,
    casesPrefetch,
    formData.clientCaseId,
    defaultClientId,
    defaultClientCaseId,
  ]);

  const markPaid = () =>
    setFormData((s) => {
      const effectivePaidDate = s.paidDate || defaultDate;
      const paidAmount = s.amount && s.amount.trim() ? s.amount : s.paidAmount || s.amount;
      return {
        ...s,
        isPaid: true,
        status: 'Completed',
        paidDate: effectivePaidDate,
        lastPaymentDate: s.lastPaymentDate || effectivePaidDate,
        paidAmount: paidAmount ?? '',
      };
    });

  const markPending = () =>
    setFormData((s) => {
      const amount = parseDecimal(s.amount);
      const paidAmount = parseDecimal(s.paidAmount);
      const shouldClearPaidAmount =
        !Number.isFinite(paidAmount) ||
        amount <= 0.005 ||
        paidAmount >= amount - 0.005;

      return {
        ...s,
        isPaid: false,
        status: 'Pending',
        paidAmount: shouldClearPaidAmount ? '' : s.paidAmount,
        paidDate: '',
        lastPaymentDate: shouldClearPaidAmount ? '' : s.lastPaymentDate,
      };
    });

  const amountRef = useRef<HTMLInputElement>(null);
  const handleMoneyFieldFocus = (field: 'amount' | 'paidAmount') => {
    setFormData((prev) => {
      const raw = normalizeMoneyInputValue(prev[field]);
      return raw === prev[field] ? prev : { ...prev, [field]: raw };
    });
  };

  const handleAmountInput = (raw: string, el?: HTMLInputElement) => {
    let v = raw.replace(/[^\d.,]/g, '');
    const caret = el?.selectionStart ?? v.length;
    if (!v) {
      setFormData((s) => ({ ...s, amount: '' }));
      return;
    }

    const firstSepIdx = v.search(/[.,]/);
    if (firstSepIdx === -1) {
      const intPart = v.replace(/^0+(?=\d)/, '') || '0';
      const newVal = `${intPart}.00`;
      const nextCaret = Math.min(caret, intPart.length);

      setFormData((s) => ({ ...s, amount: newVal }));
      requestAnimationFrame(() => {
        const node = amountRef.current;
        if (node) node.setSelectionRange(nextCaret, nextCaret);
      });
      return;
    }

    const sep = v[firstSepIdx];
    v = v.slice(0, firstSepIdx + 1) + v.slice(firstSepIdx + 1).replace(/[.,]/g, '');
    const [intRaw, fracRaw = ''] = v.split(sep);

    const intPart = (intRaw || '0').replace(/^0+(?=\d)/, '') || '0';
    const fracPart = fracRaw.slice(0, 2);
    const newVal = fracPart ? `${intPart}${sep}${fracPart}` : `${intPart}${sep}`;

    let nextCaret: number;
    if (caret <= firstSepIdx) {
      nextCaret = Math.min(caret, intPart.length);
    } else {
      const offsetInFrac = Math.max(0, caret - (firstSepIdx + 1));
      nextCaret = intPart.length + 1 + Math.min(offsetInFrac, fracPart.length);
    }

    setFormData((s) => ({ ...s, amount: newVal }));
    requestAnimationFrame(() => {
      const node = amountRef.current;
      if (node) node.setSelectionRange(nextCaret, nextCaret);
    });
  };

  const formatAmountOnBlur = () => {
    const raw = formData.amount.trim();
    if (!raw) return;

    const cleaned = raw.replace(',', '.').replace(/[^\d.]/g, '');
    const num = Number(cleaned);
    if (!Number.isFinite(num)) {
      setFormData((s) => ({ ...s, amount: '' }));
      return;
    }
    setFormData((s) => ({ ...s, amount: formatMoneyDisplayValue(num) }));
  };

  const formatPaidAmountOnBlur = () => {
    const raw = formData.paidAmount.trim();
    if (!raw) {
      if (appliedClientBalanceAmount > 0) {
        toast.error(
          `Оплата не может быть ниже зачтенной суммы: ${formatMoneyDisplayValue(appliedClientBalanceAmount)}.`,
        );
        setFormData((s) => ({
          ...s,
          paidAmount: formatMoneyDisplayValue(appliedClientBalanceAmount),
        }));
      }
      return;
    }
    const num = parseDecimal(raw);
    if (!Number.isFinite(num)) {
      setFormData((s) => ({ ...s, paidAmount: '' }));
      return;
    }
    if (appliedClientBalanceAmount > 0 && num < appliedClientBalanceAmount) {
      toast.error(
        `Оплата не может быть ниже зачтенной суммы: ${formatMoneyDisplayValue(appliedClientBalanceAmount)}.`,
      );
      setFormData((s) => ({
        ...s,
        paidAmount: formatMoneyDisplayValue(appliedClientBalanceAmount),
      }));
      return;
    }
    if (num <= 0) {
      setFormData((s) => ({ ...s, paidAmount: '' }));
      return;
    }

    setFormData((s) => ({ ...s, paidAmount: formatMoneyDisplayValue(num) }));
  };

  const handlePaidAmountInput = (value: string) => {
    setFormData((s) => ({ ...s, paidAmount: value }));
  };

  const resolvePaymentOrderNumberForSubmit = () => {
    const value = formData.paymentOrderNumber.trim();
    if (!value) return undefined;

    const draft = autoCashOrderDraftRef.current;
    if (isCashPaymentSource && draft?.number === value) {
      return undefined;
    }

    return value;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const pickedCat = incomeTypes.find((it) => String(it.id) === formData.incomeTypeId);
    if (pickedCat && pickedCat.paymentType !== formData.type) {
      toast.error(
        t('invalidCategoryForDirection') ?? 'Тип категории не соответствует направлению платежа',
      );
      return;
    }

    if (loading) return;

    const amountNum = parseDecimal(formData.amount);
    if (!Number.isFinite(amountNum) || amountNum < 0) {
      console.error('Invalid amount value:', formData.amount);
      return;
    }

    const rawPaidAmount = parseDecimal(formData.paidAmount);
    if (
      appliedClientBalanceAmount > 0 &&
      (!Number.isFinite(rawPaidAmount) || rawPaidAmount < appliedClientBalanceAmount)
    ) {
      toast.error(
        `Нельзя сохранить оплату ниже зачтенной суммы: ${formatMoneyDisplayValue(appliedClientBalanceAmount)}.`,
      );
      setFormData((s) => ({
        ...s,
        paidAmount: formatMoneyDisplayValue(appliedClientBalanceAmount),
      }));
      return;
    }
    const safePaidAmount = Math.max(rawPaidAmount, 0);
    const paidForRemainder = Math.min(safePaidAmount, amountNum);
    const remainder = Math.max(Number((amountNum - paidForRemainder).toFixed(2)), 0);
    const submitOverpaymentAmount = Math.max(Number((safePaidAmount - amountNum).toFixed(2)), 0);
    if (formData.type === 'Income' && submitOverpaymentAmount > 0.005 && !overpaymentHandling) {
      toast.error('Выберите сценарий обработки переплаты.');
      return;
    }

    if (
      formData.type === 'Income' &&
      submitOverpaymentAmount > 0.005 &&
      overpaymentHandling === 'NextInstallment' &&
      !overpaymentTargetPaymentId
    ) {
      openInstallmentTargetDialog('overpayment');
      toast.error('Выберите платеж рассрочки для зачета переплаты.');
      return;
    }

    const normalizedDate = fromInputToApiDate(formData.date)!;
    const normalizedPaidDate =
      formData.isPaid && formData.paidDate ? fromInputToApiDate(formData.paidDate) : undefined;
    const normalizedPlannedDate =
      formData.plannedDate && fromInputToApiDate(formData.plannedDate)
        ? fromInputToApiDate(formData.plannedDate)
        : normalizedDate;
    const normalizedLastPaymentDate = formData.lastPaymentDate
      ? fromInputToApiDate(formData.lastPaymentDate)
      : safePaidAmount > 0
      ? normalizedPaidDate ?? normalizedDate
      : undefined;
    const normalizedRescheduleDate = rescheduleDate ? fromInputToApiDate(rescheduleDate) : undefined;

    if (rescheduleRemainder && remainder > 0 && !normalizedRescheduleDate) {
      toast.error(t('rescheduleDateRequired') ?? 'Укажите новую дату для остатка платежа.');
      setLoading(false);
      return;
    }

    const shouldCreateRemainder =
      rescheduleRemainder &&
      remainder > 0 &&
      safePaidAmount > 0 &&
      Number.isFinite(remainder) &&
      Boolean(normalizedRescheduleDate);

    if (!payment) {
      const approved = await confirm({
        title: 'Подтверждение добавления платежа',
        description: 'Добавить новый платеж с текущими данными?',
        confirmLabel: 'Добавить платеж',
        cancelLabel: 'Отмена',
        variant: 'primary',
      });
      if (!approved) {
        return;
      }
    }

    setLoading(true);

    try {
      const baseAmount = shouldCreateRemainder ? Number(paidForRemainder.toFixed(2)) : amountNum;
      const primaryStatus = shouldCreateRemainder ? 'Completed' : formData.status;
      const primaryPaidDate = shouldCreateRemainder
        ? normalizedPaidDate ?? normalizedDate
        : normalizedPaidDate;
      const primaryLastPaymentDate = shouldCreateRemainder
        ? normalizedLastPaymentDate ?? normalizedDate
        : normalizedLastPaymentDate;

      const baseSystemNotes = (formData.systemNotes ?? '').trim();
      const baseSystemNotesLines = baseSystemNotes
        ? baseSystemNotes
            .split('\n')
            .map((line) => line.trim())
            .filter(Boolean)
        : [];

      const payableBreakdownNote = shouldCreateRemainder
        ? `Частичная оплата: ${formatMoneyDisplayValue(paidForRemainder)} / ${formatMoneyDisplayValue(
            amountNum,
          )} • Остаток ${formatMoneyDisplayValue(remainder)} → ${toRuDate(rescheduleDate) || rescheduleDate}`
        : undefined;

      const shouldPersistLegacyAccountFields = Boolean(payment && !formData.invoiceId);

      const paymentData: PaymentPayload = {
        date: normalizedDate,
        plannedDate: normalizedPlannedDate ?? normalizedDate,
        amount: baseAmount,
        paidAmount: Number(safePaidAmount.toFixed(2)),
        lastPaymentDate: primaryLastPaymentDate,
        type: formData.type,
        status: primaryStatus as PaymentStatus,
        description: formData.description,
        isPaid: shouldCreateRemainder ? true : formData.isPaid,
        paidDate: primaryPaidDate,
        notes: formData.notes,
        systemNotes: [...baseSystemNotesLines, payableBreakdownNote]
          .filter(Boolean)
          .join('\n'),
        rescheduleCount: formData.rescheduleCount ?? 0,
        clientId: formData.clientId ? parseInt(formData.clientId, 10) : undefined,
        legalEntityId: formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined,
        expenseClientId: resolveExpenseClientIdForSubmit(),
        expenseLegalEntityId: resolveExpenseLegalEntityIdForSubmit(),
        clientCaseId: formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined,
        dealTypeId: formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined,
        incomeTypeId: formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined,
        serviceId: formData.serviceId ? parseInt(formData.serviceId, 10) : undefined,
        paymentSourceId: formData.paymentSourceId
          ? parseInt(formData.paymentSourceId, 10)
          : undefined,
        paymentStatusId: formData.paymentStatusId
          ? parseInt(formData.paymentStatusId, 10)
          : undefined,
        paymentOrderNumber: resolvePaymentOrderNumberForSubmit(),
        paymentDocumentDate: formData.paymentDocumentDate
          ? fromInputToApiDate(formData.paymentDocumentDate)
          : undefined,
        invoiceId: formData.invoiceId ? parseInt(formData.invoiceId, 10) : undefined,
        contractId: formData.contractId ? parseInt(formData.contractId, 10) : undefined,
        account: shouldPersistLegacyAccountFields ? formData.account?.trim() || undefined : undefined,
        accountDate:
          shouldPersistLegacyAccountFields && formData.accountDate
            ? fromInputToApiDate(formData.accountDate)
            : undefined,
        overpaymentHandling:
          formData.type === 'Income' && submitOverpaymentAmount > 0.005
            ? overpaymentHandling || undefined
            : undefined,
        overpaymentTargetPaymentId:
          formData.type === 'Income' && submitOverpaymentAmount > 0.005 && overpaymentHandling === 'NextInstallment'
            ? overpaymentTargetPaymentId ?? undefined
            : undefined,
        overpaymentComment:
          formData.type === 'Income' && submitOverpaymentAmount > 0.005
            ? overpaymentComment.trim() || undefined
            : undefined,
        advanceReviewStatus: payment?.advanceReviewStatus ?? 'None',
        advanceReviewReason: payment?.advanceReviewReason ?? undefined,
      };

      const paidAmountForAdvance = Number(paymentData.paidAmount ?? 0);
      const hasContractForAdvance = Boolean(paymentData.contractId);
      const isUnallocatedAdvanceCandidate =
        !shouldCreateRemainder &&
        paymentData.type === 'Income' &&
        paymentData.status !== 'Cancelled' &&
        paidAmountForAdvance > 0.005 &&
        Boolean(paymentData.clientId || paymentData.legalEntityId) &&
        !paymentData.invoiceId &&
        !paymentData.account &&
        payment?.advanceReviewStatus !== 'Ignored' &&
        paymentData.advanceReviewStatus !== 'AcceptedAsAdvance';

      if (isUnallocatedAdvanceCandidate) {
        const approved = await confirm({
          title: 'Нераспределенный аванс',
          description: hasContractForAdvance
            ? 'Платеж привязан к договору, но не связан со счетом или актом. Если сохранить его сейчас, деньги будут приняты как авансовый платеж и добавлены на баланс клиента для дальнейшего распределения.'
            : 'Платеж не привязан к счету, договору или акту. Если сохранить его сейчас, деньги будут приняты как авансовый платеж и добавлены на баланс клиента для дальнейшего распределения.',
          confirmLabel: 'Принять как аванс',
          cancelLabel: 'Вернуться к привязке',
          variant: 'danger',
        });
        if (!approved) {
          return;
        }

        paymentData.advanceReviewStatus = 'AcceptedAsAdvance';
        paymentData.advanceReviewReason = 'Пользователь подтвердил авансовый платеж при сохранении';
      }

      if (shouldCreateRemainder && payment && onTransferRemainder && normalizedRescheduleDate) {
        await onTransferRemainder({ id: payment.id, ...paymentData }, normalizedRescheduleDate);
      } else if (payment) {
        await onSubmit({ id: payment.id, ...paymentData });
      } else {
        await onSubmit(paymentData);
      }

      const shouldCreateRemainderViaFallback =
        shouldCreateRemainder
        && normalizedRescheduleDate
        && onCreateRemainder
        && (!payment || !onTransferRemainder);

      if (shouldCreateRemainderViaFallback) {
        const nextPayment: PaymentPayload = {
          ...paymentData,
          date: normalizedRescheduleDate,
          plannedDate: normalizedRescheduleDate,
          amount: remainder,
          paidAmount: 0,
          lastPaymentDate: undefined,
          status: normalizedRescheduleDate < normalizedDate ? 'Overdue' : 'Pending',
          isPaid: false,
          paidDate: undefined,
          systemNotes: [
            `Остаток после частичной оплаты (${toRuDate(formData.date) || formData.date})`,
            `Источник платежа${payment?.id ? ` #${payment.id}` : ''}: ${formatMoneyDisplayValue(amountNum)} • Оплачено ${formatMoneyDisplayValue(
              paidForRemainder,
            )} • Остаток ${formatMoneyDisplayValue(remainder)} → ${toRuDate(rescheduleDate) || rescheduleDate}`,
          ].join('\n'),
          rescheduleCount: (formData.rescheduleCount ?? 0) + 1,
        };
        await onCreateRemainder(nextPayment);
      }

      onClose();
    } catch {
      return;
    } finally {
      setLoading(false);
    }
  };

  const openClientPicker = (mode: 'client' | 'expense' = 'client') => {
    if (hasLockedInvoice && (mode !== 'expense' || !canEditExpenseAttribution)) {
      return;
    }

    setClientPickerMode(mode);
    setClientPickerOpen(true);
  };

  const handleClientPick = (client: Client) => {
    setFormData((prev) => {
      if (clientPickerMode === 'expense') {
        return {
          ...prev,
          expenseClientId: String(client.id),
          expenseLegalEntityId: '',
          clientCaseId: '',
          invoiceId: hasLockedInvoice ? prev.invoiceId : '',
          account: hasLockedInvoice ? prev.account : '',
          accountDate: hasLockedInvoice ? prev.accountDate : '',
        };
      }

      return {
        ...prev,
        clientId: String(client.id),
        legalEntityId: '',
        clientCaseId: prev.type === 'Expense' ? prev.clientCaseId : '',
        contractId: hasLockedInvoice ? prev.contractId : '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      };
    });
    setClientPickerOpen(false);
    setInvoicePickerOpen(false);
    setInvoiceSearch('');
    setInvoicePickerError(null);
  };

  const handleLegalEntityPick = (entity: LegalEntitySummary) => {
    setFormData((prev) => {
      if (clientPickerMode === 'expense') {
        return {
          ...prev,
          expenseClientId: '',
          expenseLegalEntityId: String(entity.id),
          clientCaseId: '',
          invoiceId: hasLockedInvoice ? prev.invoiceId : '',
          account: hasLockedInvoice ? prev.account : '',
          accountDate: hasLockedInvoice ? prev.accountDate : '',
        };
      }

      return {
        ...prev,
        clientId: '',
        legalEntityId: String(entity.id),
        clientCaseId: prev.type === 'Expense' ? prev.clientCaseId : '',
        contractId: hasLockedInvoice ? prev.contractId : '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      };
    });
    setClientPickerOpen(false);
    setInvoicePickerOpen(false);
    setInvoiceSearch('');
    setInvoicePickerError(null);
  };

  const openInvoicePicker = () => {
    if (hasLockedInvoice || !canViewLinkedInvoices || (!formData.clientId && !formData.legalEntityId)) {
      return;
    }

    setInvoiceSearch('');
    setInvoicePickerError(null);
    setInvoicePickerOpen(true);
  };

  const applyInvoiceSelection = (invoice: Invoice) => {
    const canonicalId = invoice.invoiceEntityId;
    if (!canonicalId || canonicalId <= 0) {
      toast.error(
        'У выбранного счета еще нет канонической связи. Сначала обновите счета в настройках.',
        'Связанный счет',
      );
      return;
    }

    setFormData((prev) => ({
      ...prev,
      invoiceId: String(canonicalId),
      account: invoice.number,
      accountDate: toDateInputValue(invoice.date) || prev.accountDate,
      contractId: invoice.contractId ? String(invoice.contractId) : prev.contractId,
      type: invoice.type ?? prev.type,
      clientId: invoice.legalEntityId ? '' : invoice.clientId ? String(invoice.clientId) : prev.clientId,
      legalEntityId: invoice.legalEntityId ? String(invoice.legalEntityId) : invoice.clientId ? '' : prev.legalEntityId,
      expenseClientId: invoice.expenseClientId
        && !invoice.expenseLegalEntityId
        ? String(invoice.expenseClientId)
        : prev.expenseClientId,
      expenseLegalEntityId: invoice.expenseLegalEntityId
        ? String(invoice.expenseLegalEntityId)
        : invoice.expenseClientId
          ? ''
          : prev.expenseLegalEntityId,
      clientCaseId: invoice.clientCaseId ? String(invoice.clientCaseId) : prev.clientCaseId,
    }));
    setInvoicePickerOpen(false);
    setInvoiceSearch('');
  };

  const clearInvoiceSelection = async () => {
    if (hasLockedInvoice || !canViewLinkedInvoices) return;

    const approved = await confirm({
      title: 'Убрать связь со счетом',
      description: 'Удалить привязку текущего платежа к выбранному счету?',
      confirmLabel: 'Убрать связь',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setFormData((prev) => ({
      ...prev,
      contractId: '',
      invoiceId: '',
      account: '',
      accountDate: '',
    }));
  };

  const handleCreateInvoiceFromPayment = async () => {
    if (hasLockedInvoice || !canViewLinkedInvoices || !canCreateLinkedInvoices) return;

    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    const legalEntityId = formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined;
    if (!clientId && !legalEntityId) {
      toast.error('Сначала выберите контрагента, затем можно создать счет из платежа.', 'Создание счета');
      return;
    }

    let seedContractId = formData.contractId;
    if (!seedContractId && clientId) {
      try {
        const response = await apiService.getContracts({
          clientId,
          page: 1,
          pageSize: 25,
          sortBy: 'createdAt',
          sortDir: 'desc',
        });

        const latestContract = response.items?.[0];
        if (latestContract?.id) {
          seedContractId = String(latestContract.id);
        }
      } catch {
        seedContractId = '';
      }
    }

    setInvoiceCreateSeedContractId(seedContractId);
    setInvoiceCreateError(null);
    setInvoicePickerOpen(false);
    setInvoiceCreateOpen(true);
  };

  const handleSubmitCreatedInvoice = async (payload: InvoiceInput) => {
    try {
      setInvoiceCreateSubmitting(true);
      setInvoiceCreateError(null);

      const created = await apiService.createInvoice(payload);
      const canonicalInvoiceId = created.invoiceEntityId;
      if (!canonicalInvoiceId || canonicalInvoiceId <= 0) {
        throw new Error('Backend не вернул идентификатор созданного счета. Обновите страницу и попробуйте еще раз.');
      }

      const normalizedCreated: Invoice = {
        ...created,
        invoiceEntityId: canonicalInvoiceId,
      };

      setInvoiceOptions((prev) => {
        const next = prev.filter((item) => item.invoiceEntityId !== normalizedCreated.invoiceEntityId);
        return [normalizedCreated, ...next];
      });

      applyInvoiceSelection(normalizedCreated);
      setInvoiceCreateOpen(false);
      setInvoiceCreateError(null);
      setInvoicePickerOpen(false);
      setInvoiceCreateSeedContractId('');
      toast.success('Счет создан и привязан к платежу.', 'Создание счета');

      return;
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Не удалось создать счет.';
      setInvoiceCreateError(message);
    } finally {
      setInvoiceCreateSubmitting(false);
    }
  };

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>,
  ) => {
    const { name, value, type: inputType } = e.target;

    if (name === 'isPaid') {
      const checked = (e.target as HTMLInputElement).checked;
      if (checked) markPaid();
      else markPending();
      return;
    }

    if (name === 'status') {
      const newStatus = value as PaymentStatus;
      if (newStatus === 'Completed') markPaid();
      else if (newStatus === 'Pending') markPending();
      else if (newStatus === 'Overdue')
        setFormData((s) => ({ ...s, status: 'Overdue', isPaid: false, paidDate: '' }));
      else if (newStatus === 'Cancelled')
        setFormData((s) => ({
          ...s,
          status: 'Cancelled',
          isPaid: false,
          paidAmount: '',
          paidDate: '',
          lastPaymentDate: '',
        }));
      return;
    }

    if (name === 'date') {
      setFormData((prev) => ({ ...prev, date: value, plannedDate: value }));
      return;
    }

    if (name === 'clientId') {
      setFormData((prev) => ({
        ...prev,
        clientId: value,
        legalEntityId: value ? '' : prev.legalEntityId,
        clientCaseId: prev.type === 'Expense' ? prev.clientCaseId : '',
        contractId: hasLockedInvoice ? prev.contractId : '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setClientPickerOpen(false);
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'legalEntityId') {
      setFormData((prev) => ({
        ...prev,
        clientId: value ? '' : prev.clientId,
        legalEntityId: value,
        clientCaseId: prev.type === 'Expense' ? prev.clientCaseId : '',
        contractId: hasLockedInvoice ? prev.contractId : '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setClientPickerOpen(false);
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'expenseClientId') {
      setFormData((prev) => ({
        ...prev,
        expenseClientId: value,
        expenseLegalEntityId: value ? '' : prev.expenseLegalEntityId,
        clientCaseId: '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setClientPickerOpen(false);
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'expenseLegalEntityId') {
      setFormData((prev) => ({
        ...prev,
        expenseClientId: value ? '' : prev.expenseClientId,
        expenseLegalEntityId: value,
        clientCaseId: '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setClientPickerOpen(false);
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'type') {
      setFormData((prev) => ({
        ...prev,
        type: value as PaymentKind,
        expenseClientId: value === 'Expense'
          ? prev.expenseClientId || (hasLockedInvoice && !prev.expenseLegalEntityId ? prev.clientId : '')
          : '',
        expenseLegalEntityId: value === 'Expense'
          ? prev.expenseLegalEntityId || (hasLockedInvoice && !prev.expenseClientId && !prev.clientId ? prev.legalEntityId : '')
          : '',
        clientCaseId: hasLockedInvoice ? prev.clientCaseId : '',
        incomeTypeId: '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'clientCaseId') {
      setFormData((prev) => ({
        ...prev,
        clientCaseId: value,
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      setInvoicePickerOpen(false);
      setInvoiceSearch('');
      setInvoicePickerError(null);
      return;
    }

    if (name === 'plannedDate' || name === 'lastPaymentDate') {
      setFormData((prev) => ({ ...prev, [name]: value }));
      return;
    }

    if (name === 'paidAmount') {
      setFormData((prev) => ({ ...prev, paidAmount: value }));
      return;
    }

    setFormData((prev) => ({
      ...prev,
      [name]: inputType === 'checkbox' ? (e.target as HTMLInputElement).checked : value,
    }));
  };

  const handleSyncDateWithPaymentChange = async (checked: boolean) => {
    if (!checked) {
      setSyncDateWithPayment(false);
      return;
    }

    const approved = await confirm({
      title: 'Включить синхронизацию дат?',
      description:
        'При изменении даты фактического платежа ожидаемая дата платежа будет автоматически становиться такой же.',
      confirmLabel: 'Включить',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });

    if (!approved) return;
    setSyncDateWithPayment(true);
  };

  const isProtectedTransferredRemainder = !!payment && isTransferredRemainderPayment(payment);
  const isProtectedRemainderSource = !!payment && isPartialRemainderSourcePayment(payment);
  const remainderTransferMetadata = payment ? parsePartialRemainderTransferMetadata(payment) : null;
  const hasRemainderServiceActions = Boolean(
    payment
    && (isProtectedTransferredRemainder || isProtectedRemainderSource)
    && (onRollbackRemainderTransfer || onAdjustRemainderTransfer),
  );
  const isRepresentativeInvoicePayment = payment?.documentKind === 'Invoice' && Boolean(payment.invoiceId && payment.invoiceId > 0);
  const canDelete = !!payment && !!onDelete && !isRepresentativeInvoicePayment && !isProtectedTransferredRemainder && !isProtectedRemainderSource;
  const canCancelPayment = !!payment && !isProtectedTransferredRemainder && !isProtectedRemainderSource && payment.status !== 'Cancelled';
  const canMovePayment = !!payment && !isProtectedTransferredRemainder && !isProtectedRemainderSource && payment.status !== 'Cancelled';
  const canPrintPayment = !!payment;
  const paymentPrintTitle = resolvePaymentPrintTitle(
    formData.type,
    selectedPaymentSource?.name,
    formData.paymentOrderNumber,
  );
  const hasCashOrderPrintChoice = paymentPrintTitle === 'Приходный кассовый ордер'
    || paymentPrintTitle === 'Расходный кассовый ордер';
  const cashOrderPrintOptionLabel = formData.type === 'Expense'
    ? 'РКО (форма КО-2)'
    : 'ПКО (форма КО-1)';

  useEffect(() => {
    if (!isOpen || !hasCashOrderPrintChoice) {
      setPrintMenuOpen(false);
    }
  }, [hasCashOrderPrintChoice, isOpen]);

  const buildPaymentPrintHtml = () => {
    const documentNumber = formData.paymentOrderNumber.trim() || (payment ? `#${payment.id}` : 'Без номера');
    const documentDate =
      formData.paymentDocumentDate ||
      formData.paidDate ||
      formData.lastPaymentDate ||
      formData.date;
    const selectedCase = cases.find((item) => String(item.id) === formData.clientCaseId) ?? fallbackCase;
    const selectedDealType = dealTypes.find((item) => String(item.id) === formData.dealTypeId) ?? null;
    const selectedIncomeType = incomeTypes.find((item) => String(item.id) === formData.incomeTypeId) ?? null;
    const selectedService = services.find((item) => String(item.id) === formData.serviceId) ?? null;
    const counterpartyName = formData.type === 'Expense'
      ? selectedExpenseClient?.name
        || (selectedExpenseLegalEntity ? getLegalEntityDisplayName(selectedExpenseLegalEntity) : '')
        || selectedClient?.name
        || (selectedLegalEntity ? getLegalEntityDisplayName(selectedLegalEntity) : '')
      : selectedClient?.name
        || (selectedLegalEntity ? getLegalEntityDisplayName(selectedLegalEntity) : '')
        || selectedExpenseClient?.name
        || (selectedExpenseLegalEntity ? getLegalEntityDisplayName(selectedExpenseLegalEntity) : '');
    const amount = parseDecimal(formData.isPaid && formData.paidAmount ? formData.paidAmount : formData.amount);
    const invoiceLabel = formData.account.trim()
      ? `Счет № ${formData.account.trim()}${formData.accountDate ? ` от ${formatPrintDate(formData.accountDate)}` : ''}`
      : '—';
    const rows = [
      ['Операция', formData.type === 'Expense' ? 'Расход' : 'Приход'],
      ['Источник платежа', selectedPaymentSource?.name || '—'],
      ['Контрагент', counterpartyName || '—'],
      ['Дело', selectedCase?.title || '—'],
      ['Счет', invoiceLabel],
      ['Тип сделки', selectedDealType?.name || '—'],
      ['Тип дохода', selectedIncomeType?.name || '—'],
      ['Тип услуги', selectedService?.name || '—'],
      ['Описание', formData.description || '—'],
      ['Комментарий', formData.notes || '—'],
    ];
    const rowHtml = rows
      .map(([label, value]) => `
        <tr>
          <th>${escapeHtml(label)}</th>
          <td>${escapeHtml(value)}</td>
        </tr>`)
      .join('');

    return `<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>${escapeHtml(paymentPrintTitle)} ${escapeHtml(documentNumber)}</title>
  <style>
    * { box-sizing: border-box; }
    body { margin: 0; background: #f8fafc; color: #0f172a; font-family: Arial, sans-serif; }
    .page { width: 210mm; min-height: 297mm; margin: 0 auto; padding: 22mm; background: #fff; }
    .top { display: flex; justify-content: space-between; gap: 24px; border-bottom: 2px solid #0f172a; padding-bottom: 14px; }
    .label { color: #64748b; font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    h1 { margin: 6px 0 0; font-size: 24px; line-height: 1.2; }
    .number { min-width: 210px; text-align: right; font-size: 15px; line-height: 1.7; }
    .amount { margin: 24px 0; border: 1px solid #cbd5e1; padding: 16px; }
    .amount strong { display: block; margin-top: 4px; font-size: 28px; }
    table { width: 100%; border-collapse: collapse; margin-top: 16px; font-size: 13px; }
    th, td { border: 1px solid #cbd5e1; padding: 9px 10px; vertical-align: top; }
    th { width: 34%; background: #f8fafc; text-align: left; color: #475569; }
    .signatures { display: grid; grid-template-columns: 1fr 1fr; gap: 36px; margin-top: 42px; }
    .signature { border-top: 1px solid #0f172a; padding-top: 8px; color: #475569; font-size: 12px; }
    @media print {
      body { background: #fff; }
      .page { width: auto; min-height: auto; margin: 0; padding: 12mm; }
    }
  </style>
</head>
<body>
  <main class="page">
    <section class="top">
      <div>
        <div class="label">Печатная форма платежа</div>
        <h1>${escapeHtml(paymentPrintTitle)}</h1>
      </div>
      <div class="number">
        <div><strong>№ ${escapeHtml(documentNumber)}</strong></div>
        <div>от ${escapeHtml(formatPrintDate(documentDate))}</div>
      </div>
    </section>
    <section class="amount">
      <div class="label">Сумма документа</div>
      <strong>${escapeHtml(formatPrintMoney(amount))} ₽</strong>
    </section>
    <table>
      <tbody>${rowHtml}</tbody>
    </table>
    <section class="signatures">
      <div class="signature">Ответственный</div>
      <div class="signature">Кассир / бухгалтер</div>
    </section>
  </main>
</body>
</html>`;
  };

  const handlePrintPayment = async (format: 'legacy' | 'cashOrder' = 'legacy') => {
    let printWindow: Window | null = null;
    try {
      setPrintMenuOpen(false);
      printWindow = createPaymentPrintWindow();
      setPaymentPrinting(true);
      const html = format === 'cashOrder' && payment
        ? await apiService.getCashOrderPrintHtmlForPayment(payment.id)
        : buildPaymentPrintHtml();
      renderPaymentPrintWindow(printWindow, html);
    } catch (error) {
      printWindow?.close();
      toast.error(error instanceof Error ? error.message : 'Не удалось открыть печать платежа.', 'Платежи');
    } finally {
      setPaymentPrinting(false);
    }
  };

  const buildActionPaymentPayload = (
    overrides: { date?: string; plannedDate?: string } = {},
  ): PaymentPayload | null => {
    const normalizedDate = fromInputToApiDate(overrides.date ?? formData.date);
    if (!normalizedDate) {
      return null;
    }

    const normalizedPlannedDate =
      fromInputToApiDate(overrides.plannedDate ?? formData.plannedDate) ?? normalizedDate;
    const normalizedPaidDate =
      formData.isPaid && formData.paidDate ? fromInputToApiDate(formData.paidDate) : undefined;
    const safePaidAmount = Math.max(parseDecimal(formData.paidAmount), 0);
    const normalizedLastPaymentDate = formData.lastPaymentDate
      ? fromInputToApiDate(formData.lastPaymentDate)
      : safePaidAmount > 0
        ? normalizedPaidDate ?? normalizedDate
        : undefined;
    const amount = parseDecimal(formData.amount);
    const shouldPersistLegacyAccountFields = Boolean(payment && !formData.invoiceId);
    const submitOverpaymentAmount = Math.max(Number((safePaidAmount - amount).toFixed(2)), 0);

    return {
      date: normalizedDate,
      plannedDate: normalizedPlannedDate,
      amount,
      paidAmount: Number(safePaidAmount.toFixed(2)),
      lastPaymentDate: normalizedLastPaymentDate,
      type: formData.type,
      status: formData.status,
      description: formData.description,
      isPaid: formData.isPaid,
      paidDate: normalizedPaidDate,
      notes: formData.notes,
      systemNotes: (formData.systemNotes ?? '').trim(),
      rescheduleCount: formData.rescheduleCount ?? 0,
      clientId: formData.clientId ? parseInt(formData.clientId, 10) : undefined,
      legalEntityId: formData.legalEntityId ? parseInt(formData.legalEntityId, 10) : undefined,
      expenseClientId: resolveExpenseClientIdForSubmit(),
      expenseLegalEntityId: resolveExpenseLegalEntityIdForSubmit(),
      clientCaseId: formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined,
      dealTypeId: formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined,
      incomeTypeId: formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined,
      serviceId: formData.serviceId ? parseInt(formData.serviceId, 10) : undefined,
      paymentSourceId: formData.paymentSourceId ? parseInt(formData.paymentSourceId, 10) : undefined,
      paymentStatusId: formData.paymentStatusId ? parseInt(formData.paymentStatusId, 10) : undefined,
      paymentOrderNumber: resolvePaymentOrderNumberForSubmit(),
      paymentDocumentDate: formData.paymentDocumentDate
        ? fromInputToApiDate(formData.paymentDocumentDate)
        : undefined,
      invoiceId: formData.invoiceId ? parseInt(formData.invoiceId, 10) : undefined,
      contractId: formData.contractId ? parseInt(formData.contractId, 10) : undefined,
      account: shouldPersistLegacyAccountFields ? formData.account?.trim() || undefined : undefined,
      accountDate:
        shouldPersistLegacyAccountFields && formData.accountDate
          ? fromInputToApiDate(formData.accountDate)
          : undefined,
      overpaymentHandling:
        formData.type === 'Income' && submitOverpaymentAmount > 0.005
          ? overpaymentHandling || undefined
          : undefined,
      overpaymentTargetPaymentId:
        formData.type === 'Income' && submitOverpaymentAmount > 0.005 && overpaymentHandling === 'NextInstallment'
          ? overpaymentTargetPaymentId ?? undefined
          : undefined,
      overpaymentComment:
        formData.type === 'Income' && submitOverpaymentAmount > 0.005
          ? overpaymentComment.trim() || undefined
          : undefined,
    };
  };

  const openRemainderTools = () => {
    if (!payment) return;
    setAdjustPaidAmount(
      remainderTransferMetadata?.paidAmount !== undefined
        ? formatMoneyString(remainderTransferMetadata.paidAmount)
        : formData.paidAmount,
    );
    setAdjustRemainderDate(
      toDateInputValue(remainderTransferMetadata?.remainderDate)
      || rescheduleDate
      || formData.plannedDate
      || formData.date,
    );
    setRemainderToolsOpen(true);
  };

  const openPaymentMoveTools = () => {
    setPaymentMoveDate(paymentMoveDate || formData.plannedDate || formData.date || defaultDate);
    setPaymentMoveOpen(true);
  };

  const handleMovePayment = async () => {
    if (!payment || loading) return;

    const normalizedMoveDate = fromInputToApiDate(paymentMoveDate);
    if (!normalizedMoveDate) {
      toast.error('Укажите новую дату платежа.');
      return;
    }

    const approved = await confirm({
      title: 'Перенести платеж',
      description: `Дата платежа будет изменена на ${toRuDate(normalizedMoveDate) || normalizedMoveDate}. Продолжить?`,
      confirmLabel: 'Перенести',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });
    if (!approved) return;

    const paymentData = buildActionPaymentPayload({
      date: normalizedMoveDate,
      plannedDate: normalizedMoveDate,
    });
    if (!paymentData) {
      toast.error('Не удалось подготовить данные платежа.');
      return;
    }

    try {
      setLoading(true);
      await onSubmit({ id: payment.id, ...paymentData });
      onClose();
    } catch {
      return;
    } finally {
      setLoading(false);
    }
  };

  const handleRollbackRemainderTransfer = async () => {
    if (!payment || !onRollbackRemainderTransfer || loading) return;

    const approved = await confirm({
      title: 'Откатить перенос остатка',
      description: 'Перенесенный остаток будет удален, а исходная сумма вернется в платеж. Операция доступна только пока остаток не оплачен.',
      confirmLabel: 'Откатить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    try {
      setLoading(true);
      await onRollbackRemainderTransfer(payment.id);
      onClose();
    } catch {
      return;
    } finally {
      setLoading(false);
    }
  };

  const handleAdjustRemainderTransfer = async () => {
    if (!payment || !onAdjustRemainderTransfer || loading) return;

    const paidAmount = parseDecimal(adjustPaidAmount);
    if (!Number.isFinite(paidAmount) || paidAmount <= 0) {
      toast.error('Укажите фактически оплаченную сумму.');
      return;
    }
    if (!adjustRemainderDate) {
      toast.error('Укажите дату переноса остатка.');
      return;
    }

    try {
      setLoading(true);
      await onAdjustRemainderTransfer(payment.id, Number(paidAmount.toFixed(2)), adjustRemainderDate);
      onClose();
    } catch {
      return;
    } finally {
      setLoading(false);
    }
  };

  const handleCancelPayment = async () => {
    if (!payment || loading) return;

    const approved = await confirm({
      title: 'Аннулировать платеж',
      description: 'Платеж будет переведен в статус "Отменен", оплаченная сумма будет сброшена. Продолжить?',
      confirmLabel: 'Аннулировать',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    try {
      setLoading(true);
      await onSubmit({ id: payment.id, ...buildCancelledPaymentPayload(payment) });
      onClose();
    } catch {
      return;
    } finally {
      setLoading(false);
    }
  };

  const handleApplyClientBalance = async () => {
    if (!payment || balanceApplyLoading) return;

    const requestedAmount = parseDecimal(balanceApplyAmount);
    if (!Number.isFinite(requestedAmount) || requestedAmount <= 0) {
      toast.error('Укажите сумму зачета с баланса.');
      return;
    }

    try {
      setBalanceApplyLoading(true);
      const clientId = Number(formData.clientId || 0);
      const amount = parseDecimal(formData.amount);
      const paid = parseDecimal(formData.paidAmount);
      const currentRemaining = Math.max(amount - Math.min(paid, amount), 0);
      const currentBalance = Math.max(effectiveClientBalance, 0);
      const expectedApplied = Math.min(requestedAmount, currentBalance, currentRemaining);
      if (expectedApplied <= 0.005) {
        toast.error('Нет суммы для зачета.');
        return;
      }

      let result: ApplyClientBalanceResult | void;
      if (onApplyClientBalance) {
        result = await onApplyClientBalance(requestedAmount);
      } else {
        if (!clientId) {
          throw new Error('Клиент не выбран.');
        }
        result = await apiService.applyClientBalance(clientId, {
          paymentId: payment.id,
          amount: requestedAmount,
        });
      }

      const appliedAmount = Math.max(
        0,
        Math.min(result?.appliedAmount ?? expectedApplied, currentRemaining),
      );
      const nextPaid = Math.min(amount, paid + appliedAmount);
      const nextBalance =
        result?.balance ?? Math.max(Number((currentBalance - appliedAmount).toFixed(2)), 0);

      setFormData((prev) => ({
        ...prev,
        paidAmount: formatMoneyDisplayValue(nextPaid),
        isPaid: nextPaid >= amount - 0.005,
        status: nextPaid >= amount - 0.005 ? 'Completed' : prev.status,
        lastPaymentDate: prev.lastPaymentDate || defaultDate,
        paidDate: nextPaid >= amount - 0.005 ? prev.paidDate || defaultDate : prev.paidDate,
      }));
      setAppliedClientBalanceAmount((prev) =>
        Number((prev + appliedAmount).toFixed(2)),
      );
      setLocalClientBalance(nextBalance);
      setFetchedClientBalance(nextBalance);
      await onBalanceApplied?.(payment.id);
      toast.success('Баланс зачтен в платеж.');
      setBalanceApplyAmount('');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось зачесть баланс.');
    } finally {
      setBalanceApplyLoading(false);
    }
  };

  const getInstallmentPaymentTitle = (item: Payment) => {
    const meta = parseInstallmentPaymentMetadata(item.systemNotes);
    const sequence = meta ? `Рассрочка ${meta.sequence}/${Math.max(meta.months, meta.sequence)}` : 'Платеж рассрочки';
    return `${sequence} · ${item.description || `#${item.id}`}`;
  };

  const openInstallmentTargetDialog = (mode: 'overpayment' | 'balance') => {
    const currentPaymentId = payment?.id ?? 0;
    const candidates = mode === 'overpayment'
      ? installmentPayments.filter((item) => item.id !== currentPaymentId)
      : installmentPayments;

    if (installmentPaymentsLoading) {
      toast.info('Платежи рассрочки еще загружаются.');
      return;
    }

    if (candidates.length === 0) {
      toast.error('Нет открытых платежей рассрочки для выбора.');
      return;
    }

    setInstallmentTargetDialogMode(mode);
    setInstallmentTargetDraftId(
      mode === 'overpayment'
        ? overpaymentTargetPaymentId ?? candidates[0].id
        : candidates.find((item) => item.id === currentPaymentId)?.id ?? candidates[0].id,
    );
  };

  const handleOverpaymentHandlingChange = (value: PaymentOverpaymentHandling) => {
    setOverpaymentHandling(value);
    if (value === 'NextInstallment') {
      openInstallmentTargetDialog('overpayment');
    } else {
      setOverpaymentTargetPaymentId(null);
    }
  };

  const confirmInstallmentTargetDialog = async () => {
    if (!installmentTargetDialogMode || !installmentTargetDraftId) return;

    const selected = installmentPayments.find((item) => item.id === installmentTargetDraftId);
    if (!selected) {
      toast.error('Выбранный платеж рассрочки не найден.');
      return;
    }

    if (installmentTargetDialogMode === 'overpayment') {
      setOverpaymentTargetPaymentId(selected.id);
      setInstallmentTargetDialogMode(null);
      setInstallmentTargetDraftId(null);
      return;
    }

    if (balanceApplyLoading) return;

    const clientId = Number(formData.clientId || 0);
    if (!clientId) {
      toast.error('Клиент не выбран.');
      return;
    }

    const currentBalance = Math.max(effectiveClientBalance, 0);
    const selectedOutstanding = resolvePaymentOutstandingAmount(selected);
    const requestedAmount = parseDecimal(balanceApplyAmount);
    const amountToApply = Math.min(
      Number.isFinite(requestedAmount) && requestedAmount > 0 ? requestedAmount : selectedOutstanding,
      currentBalance,
      selectedOutstanding,
    );

    if (amountToApply <= 0.005) {
      toast.error('Нет суммы для зачета.');
      return;
    }

    try {
      setBalanceApplyLoading(true);
      const result = await apiService.applyClientBalance(clientId, {
        paymentId: selected.id,
        amount: amountToApply,
      });
      const appliedAmount = Math.max(0, result.appliedAmount ?? amountToApply);
      const nextBalance = result.balance ?? Math.max(Number((currentBalance - appliedAmount).toFixed(2)), 0);

      setLocalClientBalance(nextBalance);
      setFetchedClientBalance(nextBalance);
      if (selected.id === payment?.id) {
        const amount = parseDecimal(formData.amount);
        const paid = parseDecimal(formData.paidAmount);
        const nextPaid = Math.min(amount, paid + appliedAmount);
        setFormData((prev) => ({
          ...prev,
          paidAmount: formatMoneyDisplayValue(nextPaid),
          isPaid: nextPaid >= amount - 0.005,
          status: nextPaid >= amount - 0.005 ? 'Completed' : prev.status,
          lastPaymentDate: prev.lastPaymentDate || defaultDate,
          paidDate: nextPaid >= amount - 0.005 ? prev.paidDate || defaultDate : prev.paidDate,
        }));
        setAppliedClientBalanceAmount((prev) => Number((prev + appliedAmount).toFixed(2)));
      }

      setInstallmentPayments((prev) =>
        prev
          .map((item) =>
            item.id === selected.id
              ? {
                  ...item,
                  paidAmount: Math.min(item.amount, (item.paidAmount ?? 0) + appliedAmount),
                  outstandingAmount: Math.max(resolvePaymentOutstandingAmount(item) - appliedAmount, 0),
                  status: (resolvePaymentOutstandingAmount(item) - appliedAmount <= 0.005 ? 'Completed' : 'Processing') as PaymentStatus,
                }
              : item,
          )
          .filter((item) => resolvePaymentOutstandingAmount(item) > 0.005),
      );
      await onBalanceApplied?.(selected.id);
      toast.success('Баланс зачтен в выбранный платеж.');
      setBalanceApplyAmount('');
      setInstallmentTargetDialogMode(null);
      setInstallmentTargetDraftId(null);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось зачесть баланс.');
    } finally {
      setBalanceApplyLoading(false);
    }
  };

  if (!isOpen) return null;

  const amountNum = parseDecimal(formData.amount);
  const paidAmountNum = parseDecimal(formData.paidAmount);
  const remainingAmount = Math.max(amountNum - Math.min(paidAmountNum, amountNum), 0);
  const overpaymentAmount = Math.max(paidAmountNum - amountNum, 0);
  const overpaid = paidAmountNum > amountNum + 0.005;
  const hasPartial = paidAmountNum > 0 && remainingAmount > 0;
  const effectiveClientBalance =
    localClientBalance ?? availableClientBalance ?? fetchedClientBalance;
  const visibleAvailableBalance = Math.max(Number(effectiveClientBalance || 0), 0);
  const selectedBalanceOwnerLabel = formData.legalEntityId ? 'юрлица' : 'клиента';
  const showAvailableBalanceNotice = formData.type === 'Income'
    && Boolean(formData.clientId || formData.legalEntityId)
    && (clientBalanceLoading || visibleAvailableBalance > 0);
  const canApplyClientBalanceToCurrent = Boolean(payment && effectiveClientBalance > 0 && remainingAmount > 0 && formData.type === 'Income');
  const canApplyClientBalance = Boolean(
    payment &&
    effectiveClientBalance > 0 &&
    formData.type === 'Income' &&
    (remainingAmount > 0 || installmentPayments.length > 0),
  );
  const selectedOverpaymentTarget = overpaymentTargetPaymentId
    ? installmentPayments.find((item) => item.id === overpaymentTargetPaymentId) ?? null
    : null;
  const overpaymentOptions: Array<{
    value: PaymentOverpaymentHandling;
    title: string;
    description: string;
  }> = [
    {
      value: 'Advance',
      title: 'Основной платёж + аванс',
      description: 'Закрыть текущий платёж, переплату записать на баланс клиента.',
    },
    {
      value: 'NextInstallment',
      title: 'Основной платёж + следующий платёж',
      description: 'Закрыть текущий платёж, переплату зачесть в следующий платёж рассрочки.',
    },
    {
      value: 'Refund',
      title: 'Только основной платёж',
      description: 'Закрыть текущий платёж, переплату оформить к возврату клиенту.',
    },
  ];

  const selDealTypeId = formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined;
  const selIncomeTypeId = formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined;
  const selServiceId = formData.serviceId ? parseInt(formData.serviceId, 10) : undefined;
  const selSourceId = formData.paymentSourceId ? parseInt(formData.paymentSourceId, 10) : undefined;
  const formId = payment ? `payment-form-${payment.id}` : 'payment-form-new';
  const centeredLabelClass = 'mb-2 block text-center text-sm font-medium text-gray-700';
  const centeredFieldClass =
    'w-full rounded-lg border border-gray-300 px-3 py-2 text-center focus:border-blue-500 focus:ring-2 focus:ring-blue-500';
  const paymentInfoSelectClass =
    'w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500';
  const statCardBaseClass = 'flex min-h-[110px] flex-col rounded-xl border px-4 py-3 shadow-sm';
  const statLabelClass = 'text-xs uppercase tracking-[0.08em] text-gray-500';
  const statValueClass = 'mt-2 whitespace-nowrap text-[1.9rem] font-semibold leading-none tracking-tight tabular-nums';
  const serviceTabHasContent =
    Boolean(formData.notes.trim()) || systemNotesEntries.length > 0 || transferTrail.length > 0;
  const auditInfoSection = payment ? (
    <ObjectAuditLauncher
      objectType="payment"
      objectId={payment.id}
      createdAt={payment.createdAt}
      createdByUserName={payment.auditCreatedByUserName}
      updatedAt={payment.auditUpdatedAt}
      updatedByUserName={payment.auditUpdatedByUserName}
      isDeleted={payment.auditIsDeleted}
      deletedAt={payment.auditDeletedAt}
      deletedByUserName={payment.auditDeletedByUserName}
      triggerTitle="Аудит платежа"
      triggerDescription="Паспорт платежа и история изменений открываются в отдельном окне."
      title="История платежа"
      subtitle="Здесь собраны базовые audit-данные и все изменения по выбранному платежу."
    />
  ) : null;

  const installmentTargetCandidates = installmentTargetDialogMode === 'overpayment'
    ? installmentPayments.filter((item) => item.id !== payment?.id)
    : installmentPayments;
  const installmentTargetDialog = installmentTargetDialogMode ? (
    <div className="fixed inset-0 z-[1400] flex items-center justify-center bg-black/45 p-4">
      <div className="flex max-h-[86vh] w-full max-w-2xl flex-col rounded-xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-5 py-4">
          <div>
            <h3 className="text-lg font-semibold text-slate-900">
              {installmentTargetDialogMode === 'overpayment'
                ? 'Выберите платеж для зачета переплаты'
                : 'Выберите платеж для зачета аванса'}
            </h3>
            <p className="mt-1 text-sm text-slate-500">
              Деньги будут распределены в выбранную строку рассрочки.
            </p>
          </div>
          <button
            type="button"
            onClick={() => {
              setInstallmentTargetDialogMode(null);
              setInstallmentTargetDraftId(null);
            }}
            className="rounded-lg p-2 text-slate-500 transition hover:bg-slate-100"
            aria-label="Закрыть выбор платежа"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {installmentPaymentsLoading ? (
            <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
              Загружаем платежи рассрочки...
            </div>
          ) : installmentTargetCandidates.length === 0 ? (
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              Открытые платежи рассрочки для выбора не найдены.
            </div>
          ) : (
            <div className="space-y-2">
              {installmentTargetCandidates.map((item) => {
                const outstanding = resolvePaymentOutstandingAmount(item);
                return (
                  <label
                    key={item.id}
                    className={`flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-3 transition ${
                      installmentTargetDraftId === item.id
                        ? 'border-blue-400 bg-blue-50 ring-2 ring-blue-100'
                        : 'border-slate-200 bg-white hover:border-blue-200'
                    }`}
                  >
                    <input
                      type="radio"
                      name="installmentTarget"
                      value={item.id}
                      checked={installmentTargetDraftId === item.id}
                      onChange={() => setInstallmentTargetDraftId(item.id)}
                      className="mt-1 h-4 w-4 border-blue-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block font-medium text-slate-900">{getInstallmentPaymentTitle(item)}</span>
                      <span className="mt-1 block text-xs text-slate-500">
                        Дата: {toRuDate(item.plannedDate || item.date) || item.plannedDate || item.date} · Остаток: {formatMoneyDisplayValue(outstanding)}
                      </span>
                    </span>
                    <span className="shrink-0 rounded-full bg-slate-100 px-2 py-1 text-xs font-semibold text-slate-700">
                      #{item.id}
                    </span>
                  </label>
                );
              })}
            </div>
          )}
        </div>
        <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-5 py-4">
          <button
            type="button"
            onClick={() => {
              setInstallmentTargetDialogMode(null);
              setInstallmentTargetDraftId(null);
            }}
            className="inline-flex h-10 items-center rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => void confirmInstallmentTargetDialog()}
            disabled={!installmentTargetDraftId || balanceApplyLoading}
            className="inline-flex h-10 items-center rounded-lg bg-blue-600 px-4 text-sm font-medium text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {installmentTargetDialogMode === 'overpayment' ? 'Выбрать' : 'Зачесть'}
          </button>
        </div>
      </div>
    </div>
  ) : null;

  const modal = (
    <div className="fixed inset-0 z-[1300] flex items-center justify-center bg-black/50 p-4">
      <div className="relative flex max-h-[92vh] w-full max-w-6xl flex-col rounded-xl bg-white shadow-2xl">
        <div className="sticky top-0 z-10 flex items-center justify-between border-b border-gray-200 bg-white/95 px-6 py-4 backdrop-blur">
          <div>
            <p className="text-xs uppercase tracking-[0.08em] text-gray-500">
              {payment ? t('editPayment') : t('addPayment')}
            </p>
            <h2 className="text-xl font-semibold text-gray-900">{payment?.description || t('payment')}</h2>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setShowProcessHelp((prev) => !prev)}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100 transition-colors">
              <Info className="h-4 w-4" />
              {showProcessHelp
                ? t('hidePaymentProcessHelp') ?? 'Скрыть логику'
                : t('showPaymentProcessHelp') ?? 'Как работает платеж'}
            </button>

            <button
              onClick={onClose}
              type="button"
              className="p-2 hover:bg-gray-100 rounded-lg transition-colors"
              aria-label="Закрыть">
              <X size={20} />
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-6 pb-6">
          <form id={formId} ref={formRef} onSubmit={handleSubmit} autoComplete="off" className="space-y-6 py-4">
            {showProcessHelp && (
              <div className="rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-900 shadow-sm">
                <div className="flex items-center gap-2 font-semibold">
                  <Info className="h-4 w-4" />
                  <span>{t('paymentFlowTitle') ?? 'Как работает разнесение платежей'}</span>
                </div>
                <ul className="mt-2 space-y-1 list-disc list-inside">
                  <li>
                    {t('paymentFlowStepPlan') ??
                      'Запланируйте сумму и дату платежа, привяжите клиента и источник.'}
                  </li>
                  <li>
                    {t('paymentFlowStepFact') ??
                      'Когда получили деньги, введите фактически оплачено и дату оплаты.'}
                  </li>
                  <li>
                    {t('paymentFlowStepRemainder') ??
                      'Если оплатили частично, включите перенос остатка — система создаст новый платеж на остаток и отметит связь.'}
                  </li>
                  <li>
                    {t('paymentFlowStepHistory') ??
                      'История переносов и расчёт остатка фиксируются в служебных заметках.'}
                  </li>
                </ul>
              </div>
            )}

            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-[220px,repeat(3,minmax(0,1fr))]">
              <div className="flex min-h-[110px] items-center">
                <div className="inline-flex w-full rounded-2xl border border-gray-200 bg-white p-1.5 shadow-sm">
                  <button
                    type="button"
                    onClick={() => setDetailsTab('details')}
                    className={`flex-1 rounded-xl px-3 py-2 text-sm font-medium transition ${
                      detailsTab === 'details'
                        ? 'bg-gray-900 text-white shadow-sm'
                        : 'text-gray-600 hover:text-gray-900'
                    }`}>
                    Основное
                  </button>
                  <button
                    type="button"
                    onClick={() => setDetailsTab('system')}
                    className={`flex flex-1 items-center justify-center gap-1 rounded-xl px-3 py-2 text-sm font-medium transition ${
                      detailsTab === 'system'
                        ? 'bg-gray-900 text-white shadow-sm'
                        : 'text-gray-600 hover:text-gray-900'
                    }`}>
                    <span>Заметки</span>
                    {serviceTabHasContent ? (
                      <span
                        className={`inline-block h-2 w-2 rounded-full ${
                          detailsTab === 'system' ? 'bg-amber-300' : 'bg-amber-500'
                        }`}
                        aria-hidden="true"
                      />
                    ) : null}
                  </button>
                </div>
              </div>

              <div className={`${statCardBaseClass} items-center border-gray-200 bg-gray-50/70 text-center`}>
                <p className={`${statLabelClass} text-center`}>
                  {t('amount')} {isRequired('Amount') ? '*' : ''}
                </p>
                <div className={`${statValueClass} w-full text-center text-gray-900`}>
                  {formatMoneyDisplayValue(amountNum)}
                </div>
                <div className="mt-3 w-full space-y-1 text-center text-xs text-gray-500">
                  <div>{t('plannedScheduleDate') ?? 'Плановая дата по графику'}</div>
                  <div className="font-medium text-gray-700">
                    {toRuDate(formData.plannedDate) || t('notSpecified') || 'Не указана'}
                  </div>
                </div>
              </div>

              <div className={`${statCardBaseClass} items-center border-gray-200 bg-white text-center`}>
                <p className={`${statLabelClass} text-center`}>{t('paidAmount') ?? 'Получено фактически'}</p>
                <div className={`${statValueClass} w-full text-center text-emerald-600`}>
                  {formatMoneyDisplayValue(paidAmountNum)}
                </div>
                <div className="mt-3 w-full space-y-1 text-center text-xs text-gray-500">
                  <div>{t('lastPaymentDate') ?? 'Дата фактического платежа'}</div>
                  <div className="font-medium text-gray-700">
                    {toRuDate(formData.lastPaymentDate) || 'Не указана'}
                  </div>
                </div>
              </div>

              <div
                className={`${statCardBaseClass} items-center text-center ${
                  remainingAmount > 0
                    ? 'border-amber-200 bg-amber-50'
                    : 'border-emerald-200 bg-emerald-50'
                }`}>
                <p className={`text-center text-xs uppercase tracking-[0.08em] ${remainingAmount > 0 ? 'text-amber-700' : 'text-emerald-700'}`}>
                  {remainingAmount > 0 ? 'Остаток' : t('paymentReceived') ?? 'Закрыт'}
                </p>
                <div
                  className={`${statValueClass} w-full text-center ${
                    remainingAmount > 0 ? 'text-amber-700' : 'text-emerald-700'
                  }`}>
                  {remainingAmount > 0 ? formatMoneyDisplayValue(remainingAmount) : 'Выполнено'}
                </div>
                <div className="mt-3 flex min-h-[20px] w-full justify-center">
                  {hasPartial ? (
                    <span className="inline-flex items-center rounded-full bg-white/80 px-2.5 py-1 text-[11px] font-medium text-amber-800 ring-1 ring-amber-200">
                      Перенос остатка
                    </span>
                  ) : null}
                </div>
              </div>
            </div>

            {detailsTab === 'system' && (transferTrail.length > 0 || (formData.rescheduleCount ?? 0) > 0 || hasPartial) && (
              <div className="rounded-xl border border-amber-100 bg-amber-50 px-4 py-3 shadow-sm">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="flex items-center gap-2 text-amber-800">
                    <History className="h-5 w-5" />
                    <div>
                      <p className="text-sm font-semibold">{t('transferHistoryTitle') ?? 'Цепочка переносов'}</p>
                      <p className="text-xs text-amber-700">
                        {t('transferHistorySubtitle') ?? 'Как образовался текущий остаток и связанные платежи'}
                      </p>
                    </div>
                  </div>
                  <div className="flex items-center gap-2 text-xs font-semibold text-amber-800">
                    <span className="rounded-full bg-white/80 px-2 py-1 ring-1 ring-amber-200">
                      {t('transferCountLabel') ?? 'Переносов'}: {formData.rescheduleCount ?? 0}
                    </span>
                    {formData.lastPaymentDate && (
                      <span className="rounded-full bg-white/80 px-2 py-1 ring-1 ring-amber-200">
                        {t('lastPaymentDate') ?? 'Факт'}: {toRuDate(formData.lastPaymentDate) || formData.lastPaymentDate}
                      </span>
                    )}
                  </div>
                </div>

                {transferTrail.length > 0 ? (
                  <ul className="mt-3 space-y-2 text-sm text-amber-900">
                    {transferTrail.map((note, idx) => (
                      <li key={`${note}-${idx}`} className="flex gap-2">
                        <span className="text-amber-400 leading-6">•</span>
                        <span className="flex-1 whitespace-pre-wrap">{note}</span>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <div className="mt-3 text-sm text-amber-800">
                    {t('transferHistoryEmpty') ?? 'Пока нет служебных отметок о переносах.'}
                  </div>
                )}
              </div>
            )}

            {detailsTab === 'details' && (
              <>
                <div className="grid gap-6 lg:grid-cols-[0.95fr,1.05fr]">
                  <div className="space-y-4 rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                    <div className="flex items-center gap-2 text-sm font-semibold text-gray-800">
                      <span className="grid h-6 w-6 place-items-center rounded-full bg-blue-50 text-xs font-bold text-blue-600">1</span>
                      <span>{'Информация о платеже'}</span>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className={centeredLabelClass}>{t('type') ?? 'Type'}</label>
                        <SystemSelect name="type" value={formData.type} onChange={handleChange} disabled={!canChangePaymentType} className={paymentInfoSelectClass}>
                          <option value="Income">{t('income') ?? 'Income'}</option><option value="Expense">{t('expense') ?? 'Expense'}</option>
                        </SystemSelect>
                      </div>
                      <div>
                        <label className={centeredLabelClass}>{t('status')}</label>
                        <SystemSelect name="status" value={formData.status} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="Pending">{t('pending')}</option><option value="Completed">{t('completedStatus')}</option><option value="Overdue">{t('overdue')}</option><option value="Cancelled">{t('cancelledStatus') ?? 'Отменен'}</option>
                        </SystemSelect>
                      </div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className={centeredLabelClass}>{t('dealType')}</label>
                        <SystemSelect name="dealTypeId" value={formData.dealTypeId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">{t('selectDealType')}</option>
                          {buildOptionsWithSelected(dealTypes, selDealTypeId, (d) => d.isActive).map((d) => (<option key={d.id} value={String(d.id)}>{d.name}{!d.isActive ? ' (неактивен)' : ''}</option>))}
                        </SystemSelect>
                      </div>
                      <div>
                        <label className={centeredLabelClass}>{formData.type === 'Income' ? t('incomeType') ?? 'Income type' : t('expenseType') ?? 'Expense type'}</label>
                        <SystemSelect name="incomeTypeId" value={formData.incomeTypeId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">{formData.type === 'Income' ? t('selectIncomeType') ?? 'Select income type' : t('selectExpenseType') ?? 'Select expense type'}</option>
                          {buildOptionsWithSelected(incomeTypes, selIncomeTypeId, (it) => it.isActive && it.paymentType === formData.type).map((it) => (<option key={it.id} value={String(it.id)}>{it.name}{!it.isActive ? ' (неактивен)' : ''}{it.paymentType !== formData.type ? ' (другое направление)' : ''}</option>))}
                        </SystemSelect>
                      </div>
                      <div className="sm:col-span-2">
                        <label className={centeredLabelClass}>Тип услуги</label>
                        <SystemSelect name="serviceId" value={formData.serviceId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">Выберите тип услуги</option>
                          {buildOptionsWithSelected(services, selServiceId, (service) => service.isActive).map((service) => (
                            <option key={service.id} value={String(service.id)}>
                              {service.name}
                              {!service.isActive ? ' (не активно)' : ''}
                            </option>
                          ))}
                        </SystemSelect>
                      </div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div><label className={centeredLabelClass}>{t('expectedPaymentDate') ?? 'Ожидаемая дата платежа'}</label><SystemDateInput type="date" name="date" aria-label="Ожидаемая дата платежа" value={formData.date} onChange={handleChange} required className={`${centeredFieldClass} ${isRequired('Date') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : ''}`} /></div>
                      <div><label className={centeredLabelClass}>{t('plannedScheduleDate') ?? 'Плановая дата по графику'}</label><SystemDateInput type="date" name="plannedDate" aria-label="Плановая дата по графику" value={formData.plannedDate} onChange={handleChange} className={centeredFieldClass} /></div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div><label className={centeredLabelClass}>{t('amount')}</label><input ref={amountRef} type="text" name="amount" aria-label={t('amount') ?? 'Сумма (₽)'} value={formData.amount} onFocus={() => handleMoneyFieldFocus('amount')} onChange={(e) => handleAmountInput(e.target.value, e.currentTarget)} onBlur={formatAmountOnBlur} onWheel={(e) => (e.currentTarget as HTMLInputElement).blur()} inputMode="decimal" className={`${centeredFieldClass} ${isRequired('Amount') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : ''}`} placeholder="0,00" /></div>
                      <div><label className={centeredLabelClass}>{t('paidAmount') ?? 'Получено фактически'}</label><input type="text" name="paidAmount" aria-label={t('paidAmount') ?? 'Получено фактически'} value={formData.paidAmount} onFocus={() => handleMoneyFieldFocus('paidAmount')} onChange={(e) => handlePaidAmountInput(e.target.value)} onBlur={formatPaidAmountOnBlur} inputMode="decimal" className={centeredFieldClass} placeholder="0,00" /></div>
                    </div>
                    {hasPartial && (
                      <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 shadow-inner">
                        <div className="grid gap-4 md:grid-cols-2 md:items-stretch">
                          <div className="space-y-3 rounded-md border border-amber-200/80 bg-white/70 p-3">
                            <div>
                              <div className="text-center text-xs font-medium uppercase tracking-[0.08em] text-amber-700">
                                Остаток
                              </div>
                              <div className="mt-1 text-center text-2xl font-semibold text-amber-900">
                                {formatMoneyDisplayValue(remainingAmount)}
                              </div>
                            </div>
                            <p className="text-xs leading-5 text-amber-700">
                              Обновите ожидаемую дату поступления оставшейся суммы.
                            </p>
                            <div className="flex justify-center">
                              <span className="inline-flex items-center rounded-full bg-white px-3 py-1 text-[11px] font-semibold uppercase tracking-wide text-amber-700 ring-1 ring-amber-200">
                                Частичный платёж
                              </span>
                            </div>
                          </div>
                          <div className="space-y-3 rounded-md border border-amber-200 bg-white/80 p-3 text-amber-900">
                            <label className="flex items-center gap-2 text-sm font-medium text-amber-900">
                              <SystemCheckbox
                                type="checkbox"
                                checked={rescheduleRemainder}
                                onChange={(e) => setRescheduleRemainder(e.target.checked)}
                                className="h-4 w-4 rounded border-amber-300 text-amber-600 focus:ring-amber-500"
                              />
                              <span>Перенести остаток</span>
                            </label>
                            <div className="space-y-2">
                              <label className="block text-center text-sm font-medium text-gray-700" htmlFor="reschedule-date">
                                Новая дата остатка
                              </label>
                              <SystemDateInput
                                id="reschedule-date"
                                type="date"
                                value={rescheduleDate}
                                onChange={(e) => setRescheduleDate(e.target.value)}
                                disabled={!rescheduleRemainder}
                                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-center focus:border-amber-500 focus:ring-2 focus:ring-amber-500 disabled:bg-gray-100"
                              />
                              {formData.rescheduleCount > 0 && (
                                <div className="text-center text-xs text-gray-600">
                                  {`Количество переносов: ${formData.rescheduleCount}`}
                                </div>
                              )}
                            </div>
                          </div>
                        </div>
                      </div>
                    )}
                    {overpaid && (
                      <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
                        <div className="flex flex-wrap items-start justify-between gap-3">
                          <div>
                            <div className="font-semibold">Обнаружена переплата</div>
                            <div className="mt-1 text-emerald-800">
                              Платеж {formatMoneyDisplayValue(amountNum)} · внесено {formatMoneyDisplayValue(paidAmountNum)} · переплата {formatMoneyDisplayValue(overpaymentAmount)}
                            </div>
                          </div>
                          <span className="rounded-full bg-white px-3 py-1 text-xs font-semibold text-emerald-700 ring-1 ring-emerald-200">
                            Требуется выбор
                          </span>
                        </div>
                        <div className="mt-3 grid gap-2">
                          {overpaymentOptions.map((option) => (
                            <label
                              key={option.value}
                              className={`flex cursor-pointer gap-3 rounded-lg border bg-white px-3 py-2 transition ${
                                overpaymentHandling === option.value
                                  ? 'border-emerald-400 ring-2 ring-emerald-100'
                                  : 'border-emerald-100 hover:border-emerald-300'
                              }`}>
                              <input
                                type="radio"
                                name="overpaymentHandling"
                                value={option.value}
                                checked={overpaymentHandling === option.value}
                                onChange={() => handleOverpaymentHandlingChange(option.value)}
                                className="mt-1 h-4 w-4 border-emerald-300 text-emerald-600 focus:ring-emerald-500"
                              />
                              <span>
                                <span className="block font-medium text-slate-900">{option.title}</span>
                                <span className="mt-0.5 block text-xs text-slate-600">{option.description}</span>
                              </span>
                            </label>
                          ))}
                        </div>
                        <textarea
                          value={overpaymentComment}
                          onChange={(event) => setOverpaymentComment(event.target.value)}
                          className="mt-3 min-h-[72px] w-full rounded-lg border border-emerald-200 bg-white px-3 py-2 text-sm text-slate-800 focus:border-emerald-500 focus:ring-2 focus:ring-emerald-500"
                          placeholder="Комментарий к обработке переплаты"
                        />
                      </div>
                    )}
                    {overpaid && overpaymentHandling === 'NextInstallment' && (
                      <div className="rounded-lg border border-emerald-200 bg-white px-3 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <div className="min-w-0 text-sm">
                            <div className="font-medium text-slate-900">Платеж для зачета</div>
                            <div className="mt-0.5 truncate text-xs text-slate-600">
                              {overpaymentTargetPaymentId
                                ? selectedOverpaymentTarget
                                  ? getInstallmentPaymentTitle(selectedOverpaymentTarget)
                                  : 'Выбранный платеж больше не доступен'
                                : installmentPaymentsLoading
                                  ? 'Загрузка платежей рассрочки...'
                                  : 'Выберите платеж рассрочки'}
                            </div>
                          </div>
                          <button
                            type="button"
                            onClick={() => openInstallmentTargetDialog('overpayment')}
                            className="inline-flex h-9 items-center rounded-lg border border-emerald-200 px-3 text-sm font-medium text-emerald-700 hover:bg-emerald-50"
                          >
                            Выбрать
                          </button>
                        </div>
                        {installmentPaymentsError && (
                          <div className="mt-2 text-xs text-rose-600">{installmentPaymentsError}</div>
                        )}
                      </div>
                    )}
                    {showAvailableBalanceNotice && !canApplyClientBalanceToCurrent && (
                      <div className="rounded-lg border border-emerald-100 bg-emerald-50 px-3 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                          <div>
                            <div className="font-medium text-emerald-900">Доступно на балансе {selectedBalanceOwnerLabel}</div>
                            <div className="mt-0.5 text-xs text-emerald-700">
                              {payment
                                ? formData.legalEntityId
                                  ? 'Аванс можно зачесть через баланс юридического лица.'
                                  : 'Аванс можно зачесть в этот платеж, когда по нему есть остаток к оплате.'
                                : 'После сохранения платежа эту сумму можно будет зачесть в оплату.'}
                            </div>
                          </div>
                          <div className="text-base font-semibold tabular-nums text-emerald-800">
                            {clientBalanceLoading ? 'проверяем...' : formatMoneyDisplayValue(visibleAvailableBalance)}
                          </div>
                        </div>
                      </div>
                    )}
                    {canApplyClientBalanceToCurrent && (
                      <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2">
                        <div className="flex flex-wrap items-center gap-2">
                          <div className="min-w-[150px] flex-1 text-sm text-blue-900">
                            <span className="font-medium">Баланс:</span>{' '}
                            <span className="font-semibold">
                              {clientBalanceLoading
                                ? 'проверяем...'
                                : formatMoneyDisplayValue(effectiveClientBalance)}
                            </span>
                          </div>
                          <input
                            type="text"
                            aria-label="Сумма зачета"
                            value={balanceApplyAmount}
                            onChange={(event) => setBalanceApplyAmount(event.target.value)}
                            onBlur={(event) => setBalanceApplyAmount(normalizeMoneyInputValue(event.target.value))}
                            className="h-9 w-28 rounded-lg border border-blue-200 px-2 text-center text-sm focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                            placeholder={formatMoneyDisplayValue(Math.min(effectiveClientBalance, remainingAmount))}
                          />
                          <button
                            type="button"
                            onClick={handleApplyClientBalance}
                            disabled={balanceApplyLoading}
                            className="inline-flex h-9 items-center gap-2 rounded-lg bg-blue-600 px-3 text-sm font-medium text-white transition-colors hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            <WalletCards className="h-4 w-4" />
                            Зачесть
                          </button>
                        </div>
                      </div>
                    )}
                    {canApplyClientBalance && installmentPayments.length > 0 && (
                      <div className="rounded-lg border border-blue-100 bg-white px-3 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                          <span className="text-slate-700">Зачесть аванс в другой платеж рассрочки</span>
                          <button
                            type="button"
                            onClick={() => openInstallmentTargetDialog('balance')}
                            disabled={balanceApplyLoading}
                            className="inline-flex h-9 items-center rounded-lg border border-blue-200 px-3 font-medium text-blue-700 transition-colors hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            Выбрать платеж
                          </button>
                        </div>
                      </div>
                    )}
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className={centeredLabelClass}>{t('lastPaymentDate') ?? 'Дата фактического платежа'}</label>
                        <SystemDateInput type="date" name="lastPaymentDate" aria-label="Дата фактического платежа" value={formData.lastPaymentDate} onChange={handleChange} className={centeredFieldClass} />
                      </div>
                      <div>
                        {formData.isPaid ? (
                          <>
                            <label className={centeredLabelClass}>{t('paidDate') ?? 'Дата оплаты'}</label>
                            <SystemDateInput type="date" name="paidDate" aria-label="Дата оплаты" value={formData.paidDate} onChange={handleChange} className={centeredFieldClass} />
                          </>
                        ) : (
                          <div className="hidden h-full sm:block" aria-hidden="true" />
                        )}
                      </div>
                    </div>
                    <div className="space-y-3 rounded-lg border border-gray-100 bg-gray-50/70 p-3">
                      <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
                        <SystemCheckbox type="checkbox" name="isPaid" checked={formData.isPaid} onChange={handleChange} className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500" />
                        <span>{t('paymentReceived')}</span>
                      </label>
                      <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
                        <SystemCheckbox type="checkbox" checked={syncDateWithPayment} onChange={(e) => { void handleSyncDateWithPaymentChange(e.target.checked); }} className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500" />
                        <span>{t('syncExpectedWithFact') ?? 'Синхронизировать ожидаемую дату с фактической'}</span>
                      </label>
                    </div>
              </div>

              <div className="space-y-4 rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                <div className="flex items-center gap-2 text-sm font-semibold text-gray-800">
                  <span className="h-6 w-6 rounded-full bg-indigo-50 text-indigo-600 grid place-items-center text-xs font-bold">2</span>
                  <span>{formData.type === 'Expense' ? 'Контрагент и привязка расхода' : t('client')}</span>
                </div>
                <div className="space-y-4">
                  {hasLockedInvoice && (
                    <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-sm text-blue-900">
                      Платеж привязан к счету № {linkedInvoice?.number || '—'} от{' '}
                      {toRuDate(linkedInvoice?.date) || linkedInvoice?.date || '—'}.
                    </div>
                  )}
                  <div>
                    <div className="mb-2 flex items-center justify-between gap-2">
                      <label className="block text-sm font-medium text-gray-700">
                        {formData.type === 'Expense' ? 'Поставщик / контрагент' : t('client')} {isRequired('ClientId') ? '*' : ''}
                      </label>
                      {selectedClient?.clientStatus ? (
                        <ClientStatusBadge status={selectedClient.clientStatus} className="shrink-0" />
                      ) : null}
                    </div>
                    <div className="mb-2 flex gap-2">
                      <input
                        type="text"
                        value={selectedClient?.name ?? (selectedLegalEntity ? getLegalEntityDisplayName(selectedLegalEntity) : '')}
                        readOnly
                        placeholder={formData.type === 'Expense' ? 'Выберите поставщика или юрлицо' : 'Выберите клиента или юрлицо'}
                        className="w-full rounded-lg border border-gray-300 bg-gray-50 px-3 py-2 text-gray-900"
                      />
                      <button
                        type="button"
                        onClick={() => openClientPicker('client')}
                        disabled={hasLockedInvoice || clientsLoading || legalEntitiesLoading}
                        className="inline-flex items-center rounded-lg border border-gray-300 px-3 py-2 text-gray-700 transition hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                        aria-label="Выбрать контрагента">
                        <UserRoundSearch className="h-4 w-4" />
                      </button>
                    </div>
                    {clientsLoading || legalEntitiesLoading ? (
                      <div className="mb-2 text-xs text-gray-500">{t('loading') ?? 'Загрузка...'}</div>
                    ) : null}
                    {legalEntitiesError ? (
                      <div className="mb-2 text-xs text-rose-600">{legalEntitiesError}</div>
                    ) : null}
                    {(selectedClient?.company || selectedClient?.responsibleUserName) ? (
                      <div className="mb-2 space-y-1 rounded-xl border border-gray-100 bg-gray-50 p-2">
                        {selectedClient?.company ? (
                          <div className="text-xs text-gray-600">{selectedClient.company}</div>
                        ) : null}
                        {selectedClient?.responsibleUserName ? (
                          <div className="text-xs text-gray-500">Ответственный: {selectedClient.responsibleUserName}</div>
                        ) : null}
                      </div>
                    ) : null}
                    {selectedLegalEntity ? (
                      <div className="mb-2 space-y-1 rounded-xl border border-indigo-100 bg-indigo-50/60 p-2">
                        <div className="text-xs text-gray-600">Юрлицо: {getLegalEntityDisplayName(selectedLegalEntity)}</div>
                        {selectedLegalEntity.inn ? (
                          <div className="text-xs text-gray-500">
                            ИНН {selectedLegalEntity.inn}{selectedLegalEntity.kpp ? `, КПП ${selectedLegalEntity.kpp}` : ''}
                          </div>
                        ) : null}
                        {selectedLegalEntity.responsibleUserName ? (
                          <div className="text-xs text-gray-500">Ответственный: {selectedLegalEntity.responsibleUserName}</div>
                        ) : null}
                      </div>
                    ) : null}
                    <SystemSelect
                      name="clientId"
                      value={formData.clientId}
                      onChange={handleChange}
                      disabled={hasLockedInvoice}
                      aria-hidden="true"
                      tabIndex={-1}
                      className="hidden">
                      <option value="">{t('selectClient')}</option>
                      {clients.map((client) => (
                        <option key={client.id} value={client.id}>
                          {client.name}
                          {client.company ? ` — ${client.company}` : ''}
                        </option>
                      ))}
                      </SystemSelect>
                    <SystemSelect
                      name="legalEntityId"
                      value={formData.legalEntityId}
                      onChange={handleChange}
                      disabled={hasLockedInvoice}
                      aria-hidden="true"
                      tabIndex={-1}
                      className="hidden">
                      <option value="">Без юрлица</option>
                      {legalEntities.map((entity) => (
                        <option key={entity.id} value={entity.id}>
                          {getLegalEntityDisplayName(entity)}
                        </option>
                      ))}
                    </SystemSelect>
                  </div>

                  {formData.type === 'Expense' && (
                    <div>
                      <div className="mb-2 flex items-center justify-between gap-2">
                        <label className="block text-sm font-medium text-gray-700">Клиент расхода</label>
                        {selectedExpenseClient?.clientStatus ? (
                          <ClientStatusBadge status={selectedExpenseClient.clientStatus} className="shrink-0" />
                        ) : null}
                      </div>
                      <div className="mb-2 flex gap-2">
                        <input
                          type="text"
                          value={selectedExpenseClient?.name ?? (selectedExpenseLegalEntity ? getLegalEntityDisplayName(selectedExpenseLegalEntity) : '')}
                          readOnly
                          placeholder="Выберите клиента или юрлицо, на которое ложится расход"
                          className="w-full rounded-lg border border-gray-300 bg-gray-50 px-3 py-2 text-gray-900"
                        />
                        <button
                          type="button"
                          onClick={() => openClientPicker('expense')}
                          disabled={!canEditExpenseAttribution || clientsLoading || legalEntitiesLoading}
                          className="inline-flex items-center rounded-lg border border-gray-300 px-3 py-2 text-gray-700 transition hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                          aria-label="Выбрать контрагента расхода">
                          <UserRoundSearch className="h-4 w-4" />
                        </button>
                      </div>
                      {(selectedExpenseClient?.company || selectedExpenseClient?.responsibleUserName) ? (
                        <div className="mb-2 space-y-1 rounded-xl border border-amber-100 bg-amber-50/60 p-2">
                          {selectedExpenseClient?.company ? (
                            <div className="text-xs text-gray-600">{selectedExpenseClient.company}</div>
                          ) : null}
                          {selectedExpenseClient?.responsibleUserName ? (
                            <div className="text-xs text-gray-500">Ответственный: {selectedExpenseClient.responsibleUserName}</div>
                          ) : null}
                        </div>
                      ) : null}
                      {selectedExpenseLegalEntity ? (
                        <div className="mb-2 space-y-1 rounded-xl border border-amber-100 bg-amber-50/60 p-2">
                          <div className="text-xs text-gray-600">Юрлицо: {getLegalEntityDisplayName(selectedExpenseLegalEntity)}</div>
                          {selectedExpenseLegalEntity.inn ? (
                            <div className="text-xs text-gray-500">
                              ИНН {selectedExpenseLegalEntity.inn}{selectedExpenseLegalEntity.kpp ? `, КПП ${selectedExpenseLegalEntity.kpp}` : ''}
                            </div>
                          ) : null}
                        </div>
                      ) : null}
                      {!formData.expenseClientId && !formData.expenseLegalEntityId ? (
                        <div className="text-xs text-amber-700">
                          Для привязки расхода к делу выберите клиента или юрлицо расхода и затем дело.
                        </div>
                      ) : null}
                      <SystemSelect
                        name="expenseClientId"
                        value={formData.expenseClientId}
                        onChange={handleChange}
                        disabled={!canEditExpenseAttribution}
                        aria-hidden="true"
                        tabIndex={-1}
                        className="hidden">
                        <option value="">Без привязки к клиенту</option>
                        {clients.map((client) => (
                          <option key={client.id} value={client.id}>
                            {client.name}
                            {client.company ? ` — ${client.company}` : ''}
                          </option>
                        ))}
                      </SystemSelect>
                      <SystemSelect
                        name="expenseLegalEntityId"
                        value={formData.expenseLegalEntityId}
                        onChange={handleChange}
                        disabled={!canEditExpenseAttribution}
                        aria-hidden="true"
                        tabIndex={-1}
                        className="hidden">
                        <option value="">Без привязки к юрлицу</option>
                        {legalEntities.map((entity) => (
                          <option key={entity.id} value={entity.id}>
                            {getLegalEntityDisplayName(entity)}
                          </option>
                        ))}
                      </SystemSelect>
                    </div>
                  )}

                  <div className="grid gap-3 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {formData.type === 'Expense' ? 'Дело клиента расхода' : (t('case') ?? 'Дело')}
                      </label>
                      <SystemSelect
                        key={(caseOwnerKey || 'no-owner') + ':' + cases.length}
                        name="clientCaseId"
                        value={formData.clientCaseId}
                        onChange={handleChange}
                        disabled={!caseOwnerKey}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg disabled:bg-gray-100 focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">{t('selectCase') ?? 'Без дела'}</option>

                        {formData.clientCaseId &&
                          !cases.some((c) => String(c.id) === String(formData.clientCaseId)) &&
                          (fallbackCase ? (
                            <option value={fallbackCase.id}>{fallbackCase.title}</option>
                          ) : (
                            <option value={formData.clientCaseId}>
                              {t('case') ?? 'Дело'} #{formData.clientCaseId}
                            </option>
                          ))}

                        {cases.map((c) => (
                          <option key={c.id} value={c.id}>
                            {c.title}
                          </option>
                        ))}
                      </SystemSelect>
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        Договор
                      </label>
                      <SystemSelect
                        name="contractId"
                        value={formData.contractId}
                        onChange={handleChange}
                        aria-label="Договор"
                        disabled={!canEditContract || contractsLoading || (!formData.clientId && !formData.legalEntityId)}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg disabled:bg-gray-100 focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">
                          {!formData.clientId && !formData.legalEntityId
                            ? 'Выберите контрагента'
                            : contractsLoading
                              ? 'Загрузка договоров...'
                              : 'Без договора'}
                        </option>
                        {contractOptions.map((contract) => (
                          <option key={contract.id} value={String(contract.id)}>
                            {getContractDisplayName(contract)}
                          </option>
                        ))}
                      </SystemSelect>
                      {contractsError && (
                        <div className="mt-1 text-xs text-rose-600">
                          {contractsError}
                        </div>
                      )}
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('paymentSource')}
                      </label>
                      <SystemSelect
                        name="paymentSourceId"
                        value={formData.paymentSourceId}
                        onChange={handleChange}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">{t('selectPaymentSource')}</option>
                        {buildOptionsWithSelected(paymentSources, selSourceId, (s) => s.isActive).map((s) => (
                          <option key={s.id} value={String(s.id)}>
                            {s.name}
                            {!s.isActive ? ' (не активно)' : ''}
                          </option>
                        ))}
                      </SystemSelect>
                      {(() => {
                        const picked = paymentSources.find((s) => s.id === selSourceId);
                        if (picked && !picked.isActive) {
                          return (
                            <div className="mt-1 text-xs text-amber-600">
                              Внимание: выбранный источник деактивирован. Вы можете сохранить платёж как есть или
                              выбрать актуальный источник.
                            </div>
                          );
                        }
                        return null;
                      })()}
                    </div>
                  </div>

                  {showPaymentDocumentFields && (
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-2">
                          {paymentDocumentNumberLabel}
                        </label>
                        <input
                          type="text"
                          name="paymentOrderNumber"
                          value={formData.paymentOrderNumber}
                          onChange={handleChange}
                          maxLength={100}
                          placeholder={paymentDocumentNumberPlaceholder}
                          className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-2">
                          {paymentDocumentDateLabel}
                        </label>
                        <SystemDateInput
                          type="date"
                          name="paymentDocumentDate"
                          value={formData.paymentDocumentDate}
                          onChange={handleChange}
                          className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                        />
                      </div>
                    </div>
                  )}

                  <div className="grid gap-3 sm:grid-cols-[2fr,1fr] sm:items-end">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('account') ?? '\u0421\u0447\u0451\u0442'}
                      </label>
                      <input
                        name="account"
                        value={formData.account}
                        placeholder={'\u041d\u0430\u043f\u0440\u0438\u043c\u0435\u0440: \u0421\u0447\u0451\u0442 \u2116 12345'}
                        maxLength={120}
                        readOnly
                        tabIndex={-1}
                        className="w-full cursor-not-allowed rounded-lg border border-gray-300 bg-gray-50 px-3 py-2 text-gray-600"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('accountDate') ?? '\u0414\u0430\u0442\u0430 \u0441\u0447\u0451\u0442\u0430'}
                      </label>
                      <SystemDateInput
                        type="date"
                        name="accountDate"
                        aria-label="Дата счёта"
                        value={formData.accountDate}
                        readOnly
                        disabled
                        className="w-full cursor-not-allowed rounded-lg border border-gray-300 bg-gray-100 px-3 py-2 text-gray-600"
                      />
                    </div>

                    <div className="sm:col-span-2">
                      <div className="rounded-lg border border-gray-200 bg-gray-50/60 p-3">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <div>
                            <div className="text-sm font-medium text-gray-800">{'\u0421\u0432\u044f\u0437\u0430\u043d\u043d\u044b\u0439 \u0441\u0447\u0451\u0442'}</div>
                            <div className="text-xs text-gray-500">
                              {'Выберите существующий счёт контрагента или создайте новый счёт из текущего платежа.'}
                            </div>
                          </div>
                          {!hasLockedInvoice && canViewLinkedInvoices && (
                            <div className="flex flex-wrap gap-2">
                              <button
                                type="button"
                                onClick={openInvoicePicker}
                                disabled={!formData.clientId && !formData.legalEntityId}
                                className="rounded-lg border border-blue-200 bg-white px-3 py-1.5 text-xs font-medium text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60">
                                {selectedInvoiceSummary ? 'Сменить счёт' : 'Выбрать счёт'}
                              </button>
                              {canCreateLinkedInvoices ? (
                                <button
                                  type="button"
                                  onClick={() => void handleCreateInvoiceFromPayment()}
                                  disabled={invoiceCreateSubmitting || (!formData.clientId && !formData.legalEntityId)}
                                  className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60">
                                  {invoiceCreateSubmitting ? '\u041e\u0442\u043a\u0440\u044b\u0432\u0430\u0435\u043c...' : '\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0441\u0447\u0451\u0442'}
                                </button>
                              ) : null}
                              {selectedInvoiceSummary && (
                                <button
                                  type="button"
                                  onClick={clearInvoiceSelection}
                                  className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-xs font-medium text-gray-600 transition hover:bg-gray-100">
                                  {'\u0423\u0431\u0440\u0430\u0442\u044c \u0441\u0432\u044f\u0437\u044c'}
                                </button>
                              )}
                            </div>
                          )}
                        </div>

                        {selectedInvoiceSummary ? (
                          <div className="mt-3 rounded-lg border border-emerald-200 bg-white px-3 py-2">
                            <div className="text-sm font-semibold text-gray-900">{selectedInvoiceSummary.title}</div>
                            <div className="mt-1 text-xs text-gray-500">{selectedInvoiceSummary.meta}</div>
                          </div>
                        ) : (
                          <div className="mt-3 rounded-lg border border-dashed border-gray-300 bg-white px-3 py-3 text-sm text-gray-500">
                            {'\u0421\u0432\u044f\u0437\u0430\u043d\u043d\u044b\u0439 \u0441\u0447\u0451\u0442 \u043f\u043e\u043a\u0430 \u043d\u0435 \u0432\u044b\u0431\u0440\u0430\u043d.'}
                          </div>
                        )}
                        {appliedClientBalanceAmount > 0 && (
                          <div className="mt-3 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
                            Зачтено с баланса: {formatMoneyDisplayValue(appliedClientBalanceAmount)}.
                            Оплату ниже этой суммы поставить нельзя.
                          </div>
                        )}

                      </div>
                    </div>
                  </div>

                </div>
              </div>
            </div>

                <div className="rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                  <label className="block text-sm font-medium text-gray-700 mb-2">
                    {t('description')} {isRequired('Description') ? '*' : ''}
                  </label>
                  <textarea
                    name="description"
                    value={formData.description}
                    onChange={handleChange}
                    rows={4}
                    className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${isRequired('Description') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
                    placeholder={t('paymentDescription')}
                  />
                </div>
              </>
            )}

            {detailsTab === 'system' && (
              <div className="space-y-4">
                {auditInfoSection}

                <div className="rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                  <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
                  <textarea
                    name="notes"
                    value={formData.notes}
                    onChange={handleChange}
                    rows={8}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    placeholder={t('additionalNotes')}
                  />
                </div>

                {systemNotesEntries.length > 0 ? (
                  <div className="rounded-xl border border-gray-100 bg-gray-50 px-4 py-3 shadow-sm">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('systemNotesLabel') ?? '\u0421\u043b\u0443\u0436\u0435\u0431\u043d\u044b\u0435 \u0437\u0430\u043c\u0435\u0442\u043a\u0438'}
                    </label>
                    <div className="max-h-56 overflow-y-auto rounded-lg border border-gray-200 bg-white p-3 text-sm text-gray-700">
                      <ul className="space-y-2">
                        {systemNotesEntries.map((note, idx) => (
                          <li key={`${note}-${idx}`} className="flex gap-2">
                            <span className="text-gray-400 leading-6">{'•'}</span>
                            <span className="flex-1 whitespace-pre-wrap">{note}</span>
                          </li>
                        ))}
                      </ul>
                    </div>
                  </div>
                ) : !formData.notes.trim() && !(transferTrail.length > 0 || (formData.rescheduleCount ?? 0) > 0 || hasPartial) ? (
                  <div className="rounded-xl border border-dashed border-gray-300 bg-gray-50 px-4 py-6 text-sm text-gray-500 shadow-sm">
                    {'\u0421\u043b\u0443\u0436\u0435\u0431\u043d\u044b\u0445 \u0437\u0430\u043c\u0435\u0442\u043e\u043a \u043f\u043e\u043a\u0430 \u043d\u0435\u0442.'}
                  </div>
                ) : null}
              </div>
            )}

            {hasRemainderServiceActions && (
              <div className="rounded-xl border border-blue-100 bg-blue-50/70 px-4 py-3 shadow-sm">
                <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                  <div>
                    <div className="text-sm font-semibold text-blue-950">Перенос остатка</div>
                    <div className="mt-1 text-sm text-blue-800">
                      Остаток связан с исходным частично оплаченным платежом, поэтому удаление и изменение суммы выполняются отдельными действиями.
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    {onAdjustRemainderTransfer ? (
                      <button
                        type="button"
                        onClick={openRemainderTools}
                        disabled={loading}
                        className="rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-50 disabled:opacity-60">
                        Изменить разделение
                      </button>
                    ) : null}
                    {onRollbackRemainderTransfer ? (
                      <button
                        type="button"
                        onClick={() => void handleRollbackRemainderTransfer()}
                        disabled={loading}
                        className="rounded-lg border border-rose-200 bg-white px-3 py-2 text-sm font-medium text-rose-700 transition hover:bg-rose-50 disabled:opacity-60">
                        Откатить перенос
                      </button>
                    ) : null}
                  </div>
                </div>

                {remainderToolsOpen && onAdjustRemainderTransfer ? (
                  <div className="mt-4 grid gap-3 rounded-lg border border-blue-100 bg-white p-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end">
                    <label className="block text-sm font-medium text-gray-700">
                      Фактически оплачено
                      <input
                        type="text"
                        value={adjustPaidAmount}
                        onChange={(event) => setAdjustPaidAmount(event.target.value)}
                        onBlur={() => {
                          const value = parseDecimal(adjustPaidAmount);
                          setAdjustPaidAmount(Number.isFinite(value) && value > 0 ? formatMoneyDisplayValue(value) : '');
                        }}
                        className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-200"
                      />
                    </label>
                    <label className="block text-sm font-medium text-gray-700">
                      Новая дата остатка
                      <SystemDateInput
                        type="date"
                        value={adjustRemainderDate}
                        onChange={(event) => setAdjustRemainderDate(event.target.value)}
                        className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:ring-2 focus:ring-blue-200"
                      />
                    </label>
                    <div className="flex gap-2">
                      <button
                        type="button"
                        onClick={() => setRemainderToolsOpen(false)}
                        className="rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-600 transition hover:bg-gray-50">
                        Отмена
                      </button>
                      <button
                        type="button"
                        onClick={() => void handleAdjustRemainderTransfer()}
                        disabled={loading}
                        className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white transition hover:bg-blue-700 disabled:opacity-60">
                        Сохранить
                      </button>
                    </div>
                  </div>
                ) : null}
              </div>
            )}

            {paymentMoveOpen && (
              <div className="rounded-xl border border-blue-100 bg-blue-50/70 p-4">
                <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
                  <label className="block text-sm font-medium text-gray-700">
                    Новая дата платежа
                    <SystemDateInput
                      type="date"
                      value={paymentMoveDate}
                      onChange={(event) => setPaymentMoveDate(event.target.value)}
                      className="mt-1 w-full rounded-lg border border-blue-200 bg-white px-3 py-2 text-center focus:border-blue-500 focus:ring-2 focus:ring-blue-200"
                    />
                  </label>
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => setPaymentMoveOpen(false)}
                      className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-600 transition hover:bg-gray-50">
                      Отмена
                    </button>
                    <button
                      type="button"
                      onClick={() => void handleMovePayment()}
                      disabled={loading}
                      className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white transition hover:bg-blue-700 disabled:opacity-60">
                      Сохранить перенос
                    </button>
                  </div>
                </div>
              </div>
            )}

            <div className="border-t border-gray-200 py-4">
              <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="flex items-center">
                  <div className="flex flex-wrap items-center gap-2">
                  {canPrintPayment && (
                    <div className="relative">
                      <div className="inline-flex">
                        <button
                          type="button"
                          onClick={() => void handlePrintPayment(hasCashOrderPrintChoice ? 'cashOrder' : 'legacy')}
                          disabled={loading || paymentPrinting}
                          title={`Печать: ${hasCashOrderPrintChoice ? cashOrderPrintOptionLabel : paymentPrintTitle}`}
                          className={`inline-flex min-w-40 items-center justify-center gap-2 bg-slate-50 px-4 py-2 font-medium text-slate-700 transition-colors hover:bg-slate-100 disabled:opacity-60 ${hasCashOrderPrintChoice ? 'rounded-l-lg' : 'rounded-lg'}`}>
                          <Printer className="h-4 w-4" />
                          {paymentPrinting ? 'Подготовка печати...' : 'Печать'}
                        </button>
                        {hasCashOrderPrintChoice && (
                          <button
                            type="button"
                            onClick={() => setPrintMenuOpen((value) => !value)}
                            disabled={loading || paymentPrinting}
                            title="Выбрать другую печатную форму"
                            aria-label="Выбрать другую печатную форму"
                            aria-haspopup="menu"
                            aria-expanded={printMenuOpen}
                            aria-controls="payment-print-menu"
                            className="inline-flex items-center justify-center rounded-r-lg border-l border-slate-200 bg-slate-50 px-2.5 text-slate-700 transition-colors hover:bg-slate-100 disabled:opacity-60">
                            <ChevronDown className="h-4 w-4" />
                          </button>
                        )}
                      </div>
                      {hasCashOrderPrintChoice && printMenuOpen && (
                        <div
                          id="payment-print-menu"
                          role="menu"
                          aria-label="Выбор печатной формы"
                          className="absolute bottom-full left-0 z-30 mb-2 w-64 overflow-hidden rounded-xl border border-slate-200 bg-white p-1.5 shadow-xl">
                          <button
                            type="button"
                            role="menuitem"
                            onClick={() => void handlePrintPayment('cashOrder')}
                            className="w-full rounded-lg px-3 py-2 text-left transition hover:bg-blue-50">
                            <span className="block text-sm font-semibold text-blue-700">{cashOrderPrintOptionLabel}</span>
                            <span className="mt-0.5 block text-xs text-slate-500">Официальная кассовая форма с квитанцией · по умолчанию</span>
                          </button>
                          <button
                            type="button"
                            role="menuitem"
                            onClick={() => void handlePrintPayment('legacy')}
                            className="w-full rounded-lg px-3 py-2 text-left transition hover:bg-slate-50">
                            <span className="block text-sm font-semibold text-slate-800">Старая форма платежа</span>
                            <span className="mt-0.5 block text-xs text-slate-500">Краткая форма платежного ордера</span>
                          </button>
                        </div>
                      )}
                    </div>
                  )}
                  {canMovePayment && (
                    <button
                      type="button"
                      onClick={openPaymentMoveTools}
                      disabled={loading}
                      className="inline-flex min-w-40 items-center justify-center gap-2 rounded-lg bg-blue-50 px-4 py-2 font-medium text-blue-700 transition-colors hover:bg-blue-100 disabled:opacity-60">
                      <History className="h-4 w-4" />
                      Перенести
                    </button>
                  )}
                  {canCancelPayment && (
                    <button
                      type="button"
                      onClick={() => void handleCancelPayment()}
                      disabled={loading}
                      className="inline-flex min-w-40 items-center justify-center gap-2 rounded-lg bg-amber-50 px-4 py-2 font-medium text-amber-700 transition-colors hover:bg-amber-100 disabled:opacity-60">
                      <Ban className="h-4 w-4" />
                      Аннулировать
                    </button>
                  )}
                  {canDelete && (
                    <button
                      type="button"
                      onClick={async () => {
                        if (!payment) return;
                        const ok = await confirm({
                          title: '\u041f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d\u0438\u0435 \u0443\u0434\u0430\u043b\u0435\u043d\u0438\u044f',
                          description: t('confirmDeletePayment') ?? '\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u043f\u043b\u0430\u0442\u0451\u0436\u003f',
                          confirmLabel: '\u0423\u0434\u0430\u043b\u0438\u0442\u044c',
                          cancelLabel: '\u041e\u0442\u043c\u0435\u043d\u0430',
                          variant: 'danger',
                        });
                        if (!ok) return;
                        try {
                          await onDelete!(payment.id);
                          onClose();
                        } catch {
                          return;
                        }
                      }}
                      className="inline-flex min-w-40 items-center justify-center gap-2 rounded-lg bg-red-50 px-4 py-2 font-medium text-red-700 transition-colors hover:bg-red-100">
                      <Trash2 className="h-4 w-4" />
                      {t('delete')}
                    </button>
                  )}
                  </div>
                </div>

                <div className="flex w-full justify-end gap-3 sm:w-auto">
                  <button
                    type="button"
                    onClick={onClose}
                    className="rounded-lg bg-gray-200 px-4 py-2 font-medium text-gray-800 transition-colors hover:bg-gray-300 sm:min-w-40">
                    {t('cancel')}
                  </button>

                  <button
                    type="submit"
                    disabled={loading}
                    className={`rounded-lg px-4 py-2 font-medium transition-colors sm:min-w-40 ${
                      formData.type === 'Income'
                        ? 'bg-emerald-600 text-white hover:bg-emerald-700'
                        : 'bg-red-600 text-white hover:bg-red-700'
                    } disabled:opacity-50`}>
                    {loading
                      ? payment
                        ? t('updating')
                        : t('adding')
                      : payment
                      ? t('save')
                      : t('addPayment')}
                  </button>
                </div>
              </div>
            </div>
          </form>

          <CounterpartyPickerModal
            open={clientPickerOpen}
            title={clientPickerMode === 'expense' ? 'Выбор контрагента расхода' : 'Выбор контрагента'}
            initialScope={clientPickerMode === 'expense' && formData.expenseLegalEntityId ? 'legalEntity' : formData.legalEntityId ? 'legalEntity' : 'client'}
            counterpartyRole={
              clientPickerMode === 'expense'
                ? 'client'
                : formData.type === 'Expense'
                  ? 'supplier'
                  : 'client'
            }
            overlayClassName="z-[1600]"
            onClose={() => setClientPickerOpen(false)}
            onSelect={(selection) => {
              if (!selection) return;
              if (selection.kind === 'client' && selection.client) {
                handleClientPick(selection.client);
              } else if (selection.kind === 'legalEntity' && selection.legalEntity) {
                handleLegalEntityPick(selection.legalEntity);
              }
            }}
          />
          {invoicePickerOpen && !hasLockedInvoice && canViewLinkedInvoices && (
            <div className="absolute inset-0 z-20 flex items-center justify-center bg-slate-900/35 p-4">
              <div className="w-full max-w-3xl rounded-2xl bg-white p-4 shadow-2xl">
                <div className="mb-3 flex items-center justify-between gap-2">
                  <h3 className="text-base font-semibold text-slate-900">Выбор счёта</h3>
                  <button
                    type="button"
                    onClick={() => setInvoicePickerOpen(false)}
                    className="rounded-full p-1 text-slate-500 transition hover:bg-slate-100">
                    <X className="h-4 w-4" />
                  </button>
                </div>

                <div className="relative mb-3">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="text"
                    value={invoiceSearch}
                    onChange={(event) => setInvoiceSearch(event.target.value)}
                    placeholder="Поиск счёта по номеру, описанию или делу"
                    className="w-full rounded-lg border border-slate-300 py-2 pl-9 pr-3 text-sm text-slate-800 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  />
                </div>

                {invoicePickerLoading ? (
                  <div className="rounded-lg border border-slate-200 px-3 py-4 text-sm text-slate-500">Загрузка счетов...</div>
                ) : invoicePickerError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-4 text-sm text-rose-700">
                    {invoicePickerError}
                  </div>
                ) : invoiceOptions.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-slate-300 bg-slate-50 px-3 py-4 text-sm text-slate-500">
                    Подходящие счета не найдены. Можно создать новый счёт из текущего платежа.
                  </div>
                ) : (
                  <div className="max-h-80 overflow-auto rounded-lg border border-slate-200">
                    {invoiceOptions.map((invoiceOption) => {
                      const canonicalId = invoiceOption.invoiceEntityId;
                      const selected = canonicalId && String(canonicalId) === formData.invoiceId;
                      const caseLabel = invoiceOption.clientCases?.length
                        ? invoiceOption.clientCases.map((item) => item.title).filter(Boolean).join(', ')
                        : invoiceOption.clientCaseTitle;

                      return (
                        <button
                          key={`${canonicalId ?? 'legacy'}-${invoiceOption.id}`}
                          type="button"
                          onClick={() => applyInvoiceSelection(invoiceOption)}
                          className={`flex w-full items-start justify-between gap-3 border-b border-slate-100 px-3 py-3 text-left last:border-0 ${
                            selected ? 'bg-emerald-50' : 'hover:bg-slate-50'
                          }`}>
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-slate-900">Счёт № {invoiceOption.number}</div>
                            <div className="mt-1 text-xs text-slate-500">
                              {toRuDate(invoiceOption.date) || invoiceOption.date}
                              {caseLabel ? ` • ${caseLabel}` : ''}
                            </div>
                            {invoiceOption.description ? (
                              <div className="mt-1 truncate text-xs text-slate-400">{invoiceOption.description}</div>
                            ) : null}
                          </div>
                          <div className="shrink-0 text-right">
                            {(invoiceOption.legalEntityName || invoiceOption.clientName) ? (
                              <div className="max-w-44 truncate text-xs text-slate-500">
                                {invoiceOption.legalEntityName || invoiceOption.clientName}
                              </div>
                            ) : null}
                          </div>
                        </button>
                      );
                    })}
                  </div>
                )}

                <div className="mt-3 flex flex-wrap justify-end gap-2">
                  <button
                    type="button"
                    onClick={() => setInvoicePickerOpen(false)}
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50">
                    Закрыть
                  </button>
                  <button
                    hidden={!canCreateLinkedInvoices}
                    type="button"
                    onClick={() => void handleCreateInvoiceFromPayment()}
                    disabled={invoiceCreateSubmitting || (!formData.clientId && !formData.legalEntityId)}
                    className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60">
                    {invoiceCreateSubmitting ? 'Открываем...' : 'Создать счёт'}
                  </button>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );

  const createInvoiceModal = invoiceCreateOpen ? (
    <InvoiceModal
      open={invoiceCreateOpen}
      mode="create"
      invoice={null}
      resumeDraft={invoiceCreateDraft}
      onClose={() => {
        setInvoiceCreateOpen(false);
        setInvoiceCreateError(null);
        setInvoiceCreateSeedContractId('');
      }}
      onSubmit={handleSubmitCreatedInvoice}
      submitting={invoiceCreateSubmitting}
      errorMessage={invoiceCreateError}
      clients={clients}
      legalEntities={legalEntities}
      legalEntitiesLoading={legalEntitiesLoading}
      legalEntitiesError={legalEntitiesError}
      responsibles={invoiceCreateResponsibles}
      lookupsLoading={clientsLoading || legalEntitiesLoading || invoiceCreateLookupsLoading}
      lookupsError={invoiceCreateLookupsError}
      defaultClientId={formData.legalEntityId ? undefined : formData.clientId ? Number(formData.clientId) : defaultClientId}
      defaultType={formData.type}
    />
  ) : null;

  if (typeof document === 'undefined') {
    return (
      <>
        {modal}
        {installmentTargetDialog}
        {createInvoiceModal}
      </>
    );
  }

  return (
    <>
      {createPortal(modal, document.body)}
      {createPortal(installmentTargetDialog, document.body)}
      {createPortal(createInvoiceModal, document.body)}
    </>
  );
}
