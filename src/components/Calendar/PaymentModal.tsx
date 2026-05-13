import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { X, Trash2, Info, History, Search, UserRoundSearch, Ban, WalletCards } from 'lucide-react';
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
  ActResponsible,
  CashOrderType,
  ApplyClientBalanceResult,
  PaymentOverpaymentHandling,
} from '../../types';
import { toDateInputValue, fromInputToApiDate, todayYMD, toRuDate } from '../../utils/dateUtils';
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
import { buildCancelledPaymentPayload } from '../../utils/paymentActions';
import { CounterpartyPickerModal } from '../ui/CounterpartyPickerModal';

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
  type?: PaymentKind;
};

function isCashPaymentSourceName(name?: string | null): boolean {
  return Boolean(name && /кас|налич|cash/i.test(name));
}

function isCashOrderNumberForType(value: string, type: CashOrderType): boolean {
  const prefix = type === 'Incoming' ? 'ПКО' : 'РКО';
  return value.trim().toLocaleUpperCase('ru-RU').startsWith(prefix);
}

function parseDecimal(value: string): number {
  if (!value) return 0;
  const normalized = value.replace(',', '.').replace(/[^0-9.-]/g, '');
  const num = Number(normalized);
  return Number.isFinite(num) ? num : 0;
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
  return `${intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ' ')}.${fracPart}`;
}

function normalizeMoneyInputValue(value: string): string {
  if (!value.trim()) return '';
  const num = parseDecimal(value);
  return Number.isFinite(num) ? num.toFixed(2) : '';
}

function getLegalEntityDisplayName(entity: LegalEntitySummary): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юр. лицо #${entity.id}`;
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

  const [loading, setLoading] = useState(false);
  const [balanceApplyLoading, setBalanceApplyLoading] = useState(false);
  const [balanceApplyAmount, setBalanceApplyAmount] = useState('');
  const [appliedClientBalanceAmount, setAppliedClientBalanceAmount] = useState(0);
  const [localClientBalance, setLocalClientBalance] = useState<number | null>(null);
  const [fetchedClientBalance, setFetchedClientBalance] = useState(0);
  const [clientBalanceLoading, setClientBalanceLoading] = useState(false);
  const [overpaymentHandling, setOverpaymentHandling] = useState<PaymentOverpaymentHandling | ''>('');
  const [overpaymentComment, setOverpaymentComment] = useState('');
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [fallbackCase, setFallbackCase] = useState<ClientCase | null>(null);

  const [formData, setFormData] = useState({
    date: todayYMD(),
    plannedDate: todayYMD(),
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
    ? '№ приходника'
    : isOutgoingCashPayment
      ? '№ расходника'
      : '№ документа';
  const paymentDocumentDateLabel = isIncomingCashPayment
    ? 'Дата приходника'
    : isOutgoingCashPayment
      ? 'Дата расходника'
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
    const invoiceDate = formData.accountDate || formData.date || todayYMD();
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
        clientId,
        legalEntityId: formData.legalEntityId,
        expenseClientId: formData.type === 'Expense' ? formData.expenseClientId : '',
        expenseLegalEntityId: formData.type === 'Expense' ? formData.expenseLegalEntityId : '',
        contractId: formData.contractId || invoiceCreateSeedContractId,
        clientCaseReference: '',
        description,
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
        },
      ],
      selectedActs: [],
      selectedClientCaseIds: clientCaseId ? [clientCaseId] : [],
    };
  }, [defaultClientId, formData.account, formData.accountDate, formData.amount, formData.clientCaseId, formData.contractId, formData.date, formData.description, formData.expenseClientId, formData.expenseLegalEntityId, formData.legalEntityId, formData.paidDate, formData.status, formData.type, formData.clientId, invoiceCreateOpen, invoiceCreateSeedContractId]);

  useEffect(() => {
    if (!isOpen || !invoicePickerOpen || hasLockedInvoice) {
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
    hasLockedInvoice,
    invoicePickerOpen,
    invoiceSearch,
    isOpen,
  ]);

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
      todayYMD();
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
      setOverpaymentComment('');
      return;
    }

    if (!prevOpenRef.current) {
      setLocalClientBalance(null);
      setBalanceApplyAmount('');
      setOverpaymentHandling('');
      setOverpaymentComment('');
      const p = paymentRef.current;

      if (p) {
        const appliedBalanceAmount = getAppliedClientBalanceAmount(p.systemNotes);
        const resolvedLegalEntityId = linkedInvoice?.legalEntityId ?? p.legalEntityId ?? defaultLegalEntityId ?? null;
        const resolvedClientId = linkedInvoice?.clientId ?? p.clientId ?? defaultClientId ?? null;
        const resolvedExpenseLegalEntityId = linkedInvoice?.expenseLegalEntityId ?? p.expenseLegalEntityId ?? defaultExpenseLegalEntityId ?? null;
        const resolvedExpenseClientId = linkedInvoice?.expenseClientId ?? p.expenseClientId ?? defaultExpenseClientId ?? null;
        setAppliedClientBalanceAmount(appliedBalanceAmount);
        setFormData({
          date: toDateInputValue(p.date) || todayYMD(),
          plannedDate: toDateInputValue(p.plannedDate) || toDateInputValue(p.date) || todayYMD(),
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
          contractId: p.contractId?.toString() || '',
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
          date: todayYMD(),
          plannedDate: todayYMD(),
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
          contractId: '',
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
        (p ? toDateInputValue(p.plannedDate) || toDateInputValue(p.date) : null) || todayYMD();
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

      prevOpenRef.current = true;
    }
  }, [defaultClientCaseId, defaultClientId, defaultExpenseClientId, defaultExpenseLegalEntityId, defaultLegalEntityId, isOpen, linkedInvoice, type]);

  useEffect(() => {
    if (!isOpen || availableClientBalance !== undefined) return;
    const clientId = Number(formData.clientId || 0);
    if (!clientId || formData.type !== 'Income') {
      setFetchedClientBalance(0);
      return;
    }

    let cancelled = false;
    setClientBalanceLoading(true);
    apiService
      .getClientBalance(clientId)
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
  }, [availableClientBalance, formData.clientId, formData.type, isOpen]);

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
      return { ...prev, date: prev.lastPaymentDate };
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
      const effectivePaidDate = s.paidDate || todayYMD();
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
    setFormData((s) => ({
      ...s,
      isPaid: false,
      status: 'Pending',
      paidDate: '',
    }));

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

    setLoading(true);

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
        expenseClientId:
          formData.type === 'Expense' && formData.expenseClientId
            ? parseInt(formData.expenseClientId, 10)
            : undefined,
        expenseLegalEntityId:
          formData.type === 'Expense' && formData.expenseLegalEntityId
            ? parseInt(formData.expenseLegalEntityId, 10)
            : undefined,
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
        account: shouldPersistLegacyAccountFields ? formData.account?.trim() || undefined : undefined,
        accountDate:
          shouldPersistLegacyAccountFields && formData.accountDate
            ? fromInputToApiDate(formData.accountDate)
            : undefined,
        overpaymentHandling:
          formData.type === 'Income' && submitOverpaymentAmount > 0.005
            ? overpaymentHandling || undefined
            : undefined,
        overpaymentComment:
          formData.type === 'Income' && submitOverpaymentAmount > 0.005
            ? overpaymentComment.trim() || undefined
            : undefined,
      };

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
    if (hasLockedInvoice) {
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
    if (hasLockedInvoice || (!formData.clientId && !formData.legalEntityId)) {
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
    if (hasLockedInvoice) return;

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
    if (hasLockedInvoice) return;

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
        expenseClientId: value === 'Expense' ? prev.expenseClientId : '',
        expenseLegalEntityId: value === 'Expense' ? prev.expenseLegalEntityId : '',
        clientCaseId: '',
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
  const isRepresentativeInvoicePayment = payment?.documentKind === 'Invoice';
  const canDelete = !!payment && !!onDelete && !isRepresentativeInvoicePayment && !isProtectedTransferredRemainder && !isProtectedRemainderSource;
  const canCancelPayment = !!payment && !isProtectedTransferredRemainder && !isProtectedRemainderSource && payment.status !== 'Cancelled';
  const canMovePayment = !!payment && !isProtectedTransferredRemainder && !isProtectedRemainderSource && payment.status !== 'Cancelled';

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
      expenseClientId:
        formData.type === 'Expense' && formData.expenseClientId
          ? parseInt(formData.expenseClientId, 10)
          : undefined,
      expenseLegalEntityId:
        formData.type === 'Expense' && formData.expenseLegalEntityId
          ? parseInt(formData.expenseLegalEntityId, 10)
          : undefined,
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
      account: shouldPersistLegacyAccountFields ? formData.account?.trim() || undefined : undefined,
      accountDate:
        shouldPersistLegacyAccountFields && formData.accountDate
          ? fromInputToApiDate(formData.accountDate)
          : undefined,
      overpaymentHandling:
        formData.type === 'Income' && submitOverpaymentAmount > 0.005
          ? overpaymentHandling || undefined
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
    setPaymentMoveDate(paymentMoveDate || formData.plannedDate || formData.date || todayYMD());
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
        lastPaymentDate: prev.lastPaymentDate || todayYMD(),
        paidDate: nextPaid >= amount - 0.005 ? prev.paidDate || todayYMD() : prev.paidDate,
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
  if (!isOpen) return null;

  const amountNum = parseDecimal(formData.amount);
  const paidAmountNum = parseDecimal(formData.paidAmount);
  const remainingAmount = Math.max(amountNum - Math.min(paidAmountNum, amountNum), 0);
  const overpaymentAmount = Math.max(paidAmountNum - amountNum, 0);
  const overpaid = paidAmountNum > amountNum + 0.005;
  const hasPartial = paidAmountNum > 0 && remainingAmount > 0;
  const effectiveClientBalance =
    localClientBalance ?? availableClientBalance ?? fetchedClientBalance;
  const canApplyClientBalance = Boolean(payment && effectiveClientBalance > 0 && remainingAmount > 0 && formData.type === 'Income');
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
              aria-label="Close">
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
                        <select name="type" value={formData.type} onChange={handleChange} disabled={hasLockedInvoice} className={paymentInfoSelectClass}>
                          <option value="Income">{t('income') ?? 'Income'}</option><option value="Expense">{t('expense') ?? 'Expense'}</option>
                        </select>
                      </div>
                      <div>
                        <label className={centeredLabelClass}>{t('status')}</label>
                        <select name="status" value={formData.status} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="Pending">{t('pending')}</option><option value="Completed">{t('completedStatus')}</option><option value="Overdue">{t('overdue')}</option><option value="Cancelled">{t('cancelledStatus') ?? 'Отменен'}</option>
                        </select>
                      </div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className={centeredLabelClass}>{t('dealType')}</label>
                        <select name="dealTypeId" value={formData.dealTypeId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">{t('selectDealType')}</option>
                          {buildOptionsWithSelected(dealTypes, selDealTypeId, (d) => d.isActive).map((d) => (<option key={d.id} value={String(d.id)}>{d.name}{!d.isActive ? ' (inactive)' : ''}</option>))}
                        </select>
                      </div>
                      <div>
                        <label className={centeredLabelClass}>{formData.type === 'Income' ? t('incomeType') ?? 'Income type' : t('expenseType') ?? 'Expense type'}</label>
                        <select name="incomeTypeId" value={formData.incomeTypeId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">{formData.type === 'Income' ? t('selectIncomeType') ?? 'Select income type' : t('selectExpenseType') ?? 'Select expense type'}</option>
                          {buildOptionsWithSelected(incomeTypes, selIncomeTypeId, (it) => it.isActive && it.paymentType === formData.type).map((it) => (<option key={it.id} value={String(it.id)}>{it.name}{!it.isActive ? ' (inactive)' : ''}{it.paymentType !== formData.type ? ' (other direction)' : ''}</option>))}
                        </select>
                      </div>
                      <div className="sm:col-span-2">
                        <label className={centeredLabelClass}>Тип услуги</label>
                        <select name="serviceId" value={formData.serviceId} onChange={handleChange} className={paymentInfoSelectClass}>
                          <option value="">Выберите тип услуги</option>
                          {buildOptionsWithSelected(services, selServiceId, (service) => service.isActive).map((service) => (
                            <option key={service.id} value={String(service.id)}>
                              {service.name}
                              {!service.isActive ? ' (не активно)' : ''}
                            </option>
                          ))}
                        </select>
                      </div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div><label className={centeredLabelClass}>{t('expectedPaymentDate') ?? 'Expected date'}</label><input type="date" name="date" value={formData.date} onChange={handleChange} required className={`${centeredFieldClass} ${isRequired('Date') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : ''}`} /></div>
                      <div><label className={centeredLabelClass}>{t('plannedScheduleDate') ?? 'Planned date'}</label><input type="date" name="plannedDate" value={formData.plannedDate} onChange={handleChange} className={centeredFieldClass} /></div>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div><label className={centeredLabelClass}>{t('amount')}</label><input ref={amountRef} type="text" name="amount" value={formData.amount} onFocus={() => handleMoneyFieldFocus('amount')} onChange={(e) => handleAmountInput(e.target.value, e.currentTarget)} onBlur={formatAmountOnBlur} onWheel={(e) => (e.currentTarget as HTMLInputElement).blur()} inputMode="decimal" className={`${centeredFieldClass} ${isRequired('Amount') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : ''}`} placeholder="0.00" /></div>
                      <div><label className={centeredLabelClass}>{t('paidAmount') ?? 'Получено фактически'}</label><input type="text" name="paidAmount" value={formData.paidAmount} onFocus={() => handleMoneyFieldFocus('paidAmount')} onChange={(e) => handlePaidAmountInput(e.target.value)} onBlur={formatPaidAmountOnBlur} inputMode="decimal" className={centeredFieldClass} placeholder="0.00" /></div>
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
                              <input
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
                              <input
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
                                onChange={() => setOverpaymentHandling(option.value)}
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
                    {canApplyClientBalance && (
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
                    <div className="grid gap-3 sm:grid-cols-2">
                      <div>
                        <label className={centeredLabelClass}>{t('lastPaymentDate') ?? 'Дата фактического платежа'}</label>
                        <input type="date" name="lastPaymentDate" value={formData.lastPaymentDate} onChange={handleChange} className={centeredFieldClass} />
                      </div>
                      <div>
                        {formData.isPaid ? (
                          <>
                            <label className={centeredLabelClass}>{t('paidDate') ?? 'Дата оплаты'}</label>
                            <input type="date" name="paidDate" value={formData.paidDate} onChange={handleChange} className={centeredFieldClass} />
                          </>
                        ) : (
                          <div className="hidden h-full sm:block" aria-hidden="true" />
                        )}
                      </div>
                    </div>
                    <div className="space-y-3 rounded-lg border border-gray-100 bg-gray-50/70 p-3">
                      <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
                        <input type="checkbox" name="isPaid" checked={formData.isPaid} onChange={handleChange} className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500" />
                        <span>{t('paymentReceived')}</span>
                      </label>
                      <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
                        <input type="checkbox" checked={syncDateWithPayment} onChange={(e) => { void handleSyncDateWithPaymentChange(e.target.checked); }} className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500" />
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
                    <select
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
                      </select>
                    <select
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
                    </select>
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
                          disabled={hasLockedInvoice || clientsLoading || legalEntitiesLoading}
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
                      <select
                        name="expenseClientId"
                        value={formData.expenseClientId}
                        onChange={handleChange}
                        disabled={hasLockedInvoice}
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
                      </select>
                      <select
                        name="expenseLegalEntityId"
                        value={formData.expenseLegalEntityId}
                        onChange={handleChange}
                        disabled={hasLockedInvoice}
                        aria-hidden="true"
                        tabIndex={-1}
                        className="hidden">
                        <option value="">Без привязки к юрлицу</option>
                        {legalEntities.map((entity) => (
                          <option key={entity.id} value={entity.id}>
                            {getLegalEntityDisplayName(entity)}
                          </option>
                        ))}
                      </select>
                    </div>
                  )}

                  <div className="grid gap-3 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {formData.type === 'Expense' ? 'Дело клиента расхода' : (t('case') ?? 'Дело')}
                      </label>
                      <select
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
                      </select>
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('paymentSource')}
                      </label>
                      <select
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
                      </select>
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
                        <input
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
                      <input
                        type="date"
                        name="accountDate"
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
                          {!hasLockedInvoice && (
                            <div className="flex flex-wrap gap-2">
                              <button
                                type="button"
                                onClick={openInvoicePicker}
                                disabled={!formData.clientId && !formData.legalEntityId}
                                className="rounded-lg border border-blue-200 bg-white px-3 py-1.5 text-xs font-medium text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60">
                                {selectedInvoiceSummary ? 'Сменить счёт' : 'Выбрать счёт'}
                              </button>
                              <button
                                type="button"
                                onClick={() => void handleCreateInvoiceFromPayment()}
                                disabled={invoiceCreateSubmitting || (!formData.clientId && !formData.legalEntityId)}
                                className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60">
                                {invoiceCreateSubmitting ? '\u041e\u0442\u043a\u0440\u044b\u0432\u0430\u0435\u043c...' : '\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0441\u0447\u0451\u0442'}
                              </button>
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
                      <input
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
                    <input
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
          {invoicePickerOpen && !hasLockedInvoice && (
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
        {createInvoiceModal}
      </>
    );
  }

  return (
    <>
      {createPortal(modal, document.body)}
      {createPortal(createInvoiceModal, document.body)}
    </>
  );
}
