import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { X, Trash2, Info, History } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useDictionaries } from '../../hooks/useDictionaries';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Payment, PaymentStatus, ClientCase, PaymentPayload, Invoice, InvoiceInput, ActResponsible } from '../../types';
import { toDateInputValue, fromInputToApiDate, todayYMD, toRuDate } from '../../utils/dateUtils';
import { buildOptionsWithSelected } from '../../utils/formOptions';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { useDialog } from '../../hooks/useDialog';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { useToast } from '../../hooks/useToast';
import { InvoiceModal } from '../Accounts/InvoiceModal';
import type { InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';

type PaymentKind = 'Income' | 'Expense';
type AccountOption = { account: string; accountDate?: string | null };
type LinkedInvoiceContext = {
  invoiceEntityId: number;
  invoiceRecordId?: number | null;
  number: string;
  date: string;
  clientId?: number | null;
  clientCaseId?: number | null;
  type?: PaymentKind;
};

function parseDecimal(value: string): number {
  if (!value) return 0;
  const normalized = value.replace(',', '.').replace(/[^0-9.-]/g, '');
  const num = Number(normalized);
  return Number.isFinite(num) ? num : 0;
}

interface PaymentModalProps {
  defaultClientId?: number;
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
}

export function PaymentModal({
  isOpen,
  onClose,
  onSubmit,
  payment,
  onDelete,
  onCreateRemainder,
  defaultClientId,
  defaultClientCaseId,
  casesPrefetch,
  type,
  linkedInvoice,
}: PaymentModalProps) {
  const formRef = useRef<HTMLFormElement>(null);

  const { clients, loading: clientsLoading } = useClients();
  const { dealTypes, incomeTypes, paymentSources } = useDictionaries();
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const { isRequired } = useFormRequirements('payments', isOpen);

  const [loading, setLoading] = useState(false);
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
    clientCaseId: '',
    dealTypeId: '',
    incomeTypeId: '',
    paymentSourceId: '',
    paymentStatusId: '',
    invoiceId: '',
    type: (type ?? 'Income') as PaymentKind,
    account: '',
    accountDate: '',
    systemNotes: '',
    rescheduleCount: 0,
  });
  const systemNotesEntries = useMemo(() => {
    if (!formData.systemNotes) return [] as string[];
    return formData.systemNotes
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line) => (line.startsWith('• ') ? line.slice(2) : line));
  }, [formData.systemNotes]);
  const transferTrail = useMemo(() => {
    if (!systemNotesEntries.length) return [] as string[];
    return systemNotesEntries.filter((note) => /остаток|перенес|источник платежа/i.test(note));
  }, [systemNotesEntries]);
  const selectedClient = useMemo(
    () => clients.find((c) => String(c.id) === formData.clientId) ?? null,
    [clients, formData.clientId],
  );

  const [syncDateWithPayment, setSyncDateWithPayment] = useState(false);

  const [accountOpen, setAccountOpen] = useState(false);
  const [accountOpts, setAccountOpts] = useState<AccountOption[]>([]);
  const [accountLoading, setAccountLoading] = useState(false);
  const [invoicePickerOpen, setInvoicePickerOpen] = useState(false);
  const [invoicePickerLoading, setInvoicePickerLoading] = useState(false);
  const [invoicePickerError, setInvoicePickerError] = useState<string | null>(null);
  const [invoiceOptions, setInvoiceOptions] = useState<Invoice[]>([]);
  const [invoiceSearch, setInvoiceSearch] = useState('');
  const [invoiceCreateOpen, setInvoiceCreateOpen] = useState(false);
  const [invoiceCreateSubmitting, setInvoiceCreateSubmitting] = useState(false);
  const [invoiceCreateError, setInvoiceCreateError] = useState<string | null>(null);
  const [invoiceCreateResponsibles, setInvoiceCreateResponsibles] = useState<ActResponsible[]>([]);
  const [invoiceCreateLookupsLoading, setInvoiceCreateLookupsLoading] = useState(false);
  const [invoiceCreateLookupsError, setInvoiceCreateLookupsError] = useState<string | null>(null);
  const [rescheduleRemainder, setRescheduleRemainder] = useState(false);
  const [rescheduleDate, setRescheduleDate] = useState<string>('');
  const [showProcessHelp, setShowProcessHelp] = useState(false);
  const hasLockedInvoice = Boolean(linkedInvoice?.invoiceEntityId);
  const hasSelectedInvoice = hasLockedInvoice || Boolean(formData.invoiceId);
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

    const clientId = formData.clientId || (defaultClientId ? String(defaultClientId) : '');
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
        contractId: '',
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
  }, [defaultClientId, formData.account, formData.accountDate, formData.amount, formData.clientCaseId, formData.date, formData.description, formData.paidDate, formData.status, formData.type, formData.clientId, invoiceCreateOpen]);

  /* -------------------- Legacy account fields stay read-only -------------------- */
  useEffect(() => {
    setAccountOpts([]);
    setAccountLoading(false);
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen || !invoicePickerOpen || hasLockedInvoice) {
      setInvoicePickerLoading(false);
      setInvoicePickerError(null);
      return;
    }

    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    if (!clientId) {
      setInvoiceOptions([]);
      setInvoicePickerLoading(false);
      setInvoicePickerError('Сначала выберите клиента.');
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
          caseId,
          type: formData.type,
          search: invoiceSearch.trim() || formData.account.trim() || undefined,
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
          setInvoicePickerError(e instanceof Error ? e.message : 'Не удалось загрузить счета клиента.');
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
    formData.type,
    hasLockedInvoice,
    invoicePickerOpen,
    invoiceSearch,
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

  const formatMoneyString = (value: number | null | undefined) =>
    typeof value === 'number' && Number.isFinite(value) ? value.toFixed(2) : '';

  useEffect(() => {
    if (!isOpen) {
      prevOpenRef.current = false;
      setSyncDateWithPayment(false);
      return;
    }

    if (!prevOpenRef.current) {
      const p = paymentRef.current;

      if (p) {
        setFormData({
          date: toDateInputValue(p.date) || todayYMD(),
          plannedDate: toDateInputValue(p.plannedDate) || toDateInputValue(p.date) || todayYMD(),
          amount: formatMoneyString(p.amount),
          paidAmount: formatMoneyString(p.paidAmount),
          status: p.status,
          description: p.description,
          isPaid: p.isPaid,
          paidDate: toDateInputValue(p.paidDate) || '',
          lastPaymentDate: toDateInputValue(p.lastPaymentDate) || '',
          notes: p.notes || '',
          clientId: (linkedInvoice?.clientId ?? p.clientId ?? defaultClientId)?.toString() || '',
          clientCaseId:
            (p.clientCaseId ?? linkedInvoice?.clientCaseId ?? defaultClientCaseId)?.toString() || '',
          dealTypeId: p.dealTypeId?.toString() || '',
          incomeTypeId: p.incomeTypeId?.toString() || '',
          paymentSourceId: p.paymentSourceId?.toString() || '',
          paymentStatusId: p.paymentStatusId?.toString() || '',
          invoiceId: (p.invoiceId ?? linkedInvoice?.invoiceEntityId)?.toString() || '',
          type: (p.type as PaymentKind) ?? linkedInvoice?.type ?? type ?? 'Income',
          account: linkedInvoice?.number ?? p.account ?? '',
          accountDate: toDateInputValue(linkedInvoice?.date) || toDateInputValue(p.accountDate) || '',
          systemNotes: p.systemNotes ?? '',
          rescheduleCount: p.rescheduleCount ?? 0,
        });
        setSyncDateWithPayment(Boolean(p.hasPartialPayment && p.lastPaymentDate));
        setInvoicePickerOpen(false);
        setInvoiceSearch('');
        setInvoicePickerError(null);
      } else {
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
          clientId: (linkedInvoice?.clientId ?? defaultClientId)?.toString() || '',
          clientCaseId: (linkedInvoice?.clientCaseId ?? defaultClientCaseId)?.toString() || '',
          dealTypeId: '',
          incomeTypeId: '',
          paymentSourceId: '',
          paymentStatusId: '',
          invoiceId: linkedInvoice?.invoiceEntityId?.toString() || '',
          type: (linkedInvoice?.type ?? type ?? 'Income') as PaymentKind,
          account: linkedInvoice?.number ?? '',
          accountDate: toDateInputValue(linkedInvoice?.date) || '',
          systemNotes: '',
          rescheduleCount: 0,
        });
        setSyncDateWithPayment(false);
        setInvoicePickerOpen(false);
        setInvoiceSearch('');
        setInvoicePickerError(null);
      }

      const fallbackDate =
        (p ? toDateInputValue(p.plannedDate) || toDateInputValue(p.date) : null) || todayYMD();
      setRescheduleDate(fallbackDate);
      setRescheduleRemainder(false);

      prevOpenRef.current = true;
    }
  }, [defaultClientCaseId, defaultClientId, isOpen, linkedInvoice, type]);

  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'enter') {
        formRef.current?.requestSubmit();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) return;
    setFormData((s) => {
      if (!s.amount?.trim()) return s;
      const n = Number(String(s.amount).replace(',', '.'));
      if (!Number.isFinite(n)) return s;
      const fixed = n.toFixed(2);
      return s.amount === fixed ? s : { ...s, amount: fixed };
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

    const currentClientId = formData.clientId || (defaultClientId ? String(defaultClientId) : '');
    const initialCaseId =
      formData.clientCaseId || (defaultClientCaseId ? String(defaultClientCaseId) : '');
    const cidNum = Number(currentClientId);

    const filterPrefetchForClient = (all?: ClientCase[]) =>
      (all ?? []).filter((c) => String(c.clientId) === String(currentClientId));

    const load = async () => {
      if (!cidNum) {
        if (!cancelled) {
          setCases([]);
          setFallbackCase(null);
        }
        return;
      }

      let list: ClientCase[] = filterPrefetchForClient(casesPrefetch);

      if (!list.length) {
        try {
          list = await apiService.getCases(cidNum);
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

            if (one && String(one.clientId) === String(currentClientId)) {
              list = [...list, one];
              setFallbackCase(one);
            } else {
              setFallbackCase(null);
              setFormData((s) => {
                if (s.clientId !== currentClientId) return s;
                if (s.clientCaseId !== initialCaseId) return s;
                return { ...s, clientCaseId: '' };
              });
            }
          } catch {
            if (!cancelled) {
              setFallbackCase(null);
              setFormData((s) => {
                if (s.clientId !== currentClientId) return s;
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
    formData.clientId,
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
    const fixed = num.toFixed(2);
    setFormData((s) => ({ ...s, amount: fixed }));
  };

  const formatPaidAmountOnBlur = () => {
    const raw = formData.paidAmount.trim();
    if (!raw) return;
    const num = parseDecimal(raw);
    if (!Number.isFinite(num)) {
      setFormData((s) => ({ ...s, paidAmount: '' }));
      return;
    }
    if (num <= 0) {
      setFormData((s) => ({ ...s, paidAmount: '' }));
      return;
    }
    setFormData((s) => ({ ...s, paidAmount: num.toFixed(2) }));
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
    const safePaidAmount = Math.min(Math.max(rawPaidAmount, 0), amountNum);
    const remainder = Math.max(Number((amountNum - safePaidAmount).toFixed(2)), 0);

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

    try {
      const baseAmount = shouldCreateRemainder ? Number(safePaidAmount.toFixed(2)) : amountNum;
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
        ? `${t('partialCloseNote') ?? 'Частичная оплата'}: ${safePaidAmount.toFixed(2)} / ${amountNum.toFixed(
            2,
          )} • ${t('remainderNoteLabel') ?? 'Остаток'} ${remainder.toFixed(2)} → ${
            toRuDate(rescheduleDate) || rescheduleDate
          }`
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
        clientCaseId: formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined,
        dealTypeId: formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined,
        incomeTypeId: formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined,
        paymentSourceId: formData.paymentSourceId
          ? parseInt(formData.paymentSourceId, 10)
          : undefined,
        paymentStatusId: formData.paymentStatusId
          ? parseInt(formData.paymentStatusId, 10)
          : undefined,
        invoiceId: formData.invoiceId ? parseInt(formData.invoiceId, 10) : undefined,
        account: shouldPersistLegacyAccountFields ? formData.account?.trim() || undefined : undefined,
        accountDate:
          shouldPersistLegacyAccountFields && formData.accountDate
            ? fromInputToApiDate(formData.accountDate)
            : undefined,
      };

      if (payment) {
        await onSubmit({ id: payment.id, ...paymentData });
      } else {
        await onSubmit(paymentData);
      }

      if (shouldCreateRemainder && onCreateRemainder && normalizedRescheduleDate) {
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
            `${t('remainderNotePrefix') ?? 'Остаток после частичной оплаты'} (${toRuDate(
              formData.date,
            ) || formData.date})`,
            `${
              t('remainderOriginNote') ?? 'Источник платежа'
            }${payment?.id ? ` #${payment.id}` : ''}: ${amountNum.toFixed(2)} • ${t('paidLabel') ?? 'Оплачено'} ${
              safePaidAmount.toFixed(2)
            } • ${t('remainderNoteLabel') ?? 'Остаток'} ${remainder.toFixed(2)} → ${
              toRuDate(rescheduleDate) || rescheduleDate
            }`,
          ].join('\n'),
          rescheduleCount: (formData.rescheduleCount ?? 0) + 1,
        };
        await onCreateRemainder(nextPayment);
      }

      onClose();
    } finally {
      setLoading(false);
    }
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
      type: invoice.type ?? prev.type,
      clientId: invoice.clientId ? String(invoice.clientId) : prev.clientId,
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
      invoiceId: '',
      account: '',
      accountDate: '',
    }));
  };

  const handleCreateInvoiceFromPayment = async () => {
    if (hasLockedInvoice) return;

    const clientId = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
    if (!clientId) {
      toast.error('Сначала выберите клиента, затем можно создать счет из платежа.', 'Создание счета');
      return;
    }

    setInvoiceCreateError(null);
    setInvoicePickerOpen(false);
    setInvoiceCreateOpen(true);
  };

  const handleSubmitCreatedInvoice = async (payload: InvoiceInput) => {
    try {
      setInvoiceCreateSubmitting(true);
      setInvoiceCreateError(null);

      const created = await apiService.createInvoice(payload);
      const normalizedCreated: Invoice = created.invoiceEntityId
        ? created
        : {
            ...created,
            invoiceEntityId: created.id,
          };

      setInvoiceOptions((prev) => {
        const next = prev.filter((item) => item.invoiceEntityId !== normalizedCreated.invoiceEntityId);
        return [normalizedCreated, ...next];
      });

      applyInvoiceSelection(normalizedCreated);
      setInvoiceCreateOpen(false);
      toast.success('Счет создан и привязан к платежу.', 'Создание счета');
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
      return;
    }

    if (name === 'clientId') {
      setFormData((prev) => ({
        ...prev,
        clientId: value,
        clientCaseId: '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
      return;
    }

    if (name === 'type') {
      setFormData((prev) => ({
        ...prev,
        type: value as PaymentKind,
        incomeTypeId: '',
        invoiceId: hasLockedInvoice ? prev.invoiceId : '',
        account: hasLockedInvoice ? prev.account : '',
        accountDate: hasLockedInvoice ? prev.accountDate : '',
      }));
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

  const canDelete = !!payment && !!onDelete;
  if (!isOpen) return null;

  const amountNum = parseDecimal(formData.amount);
  const paidAmountNum = parseDecimal(formData.paidAmount);
  const remainingAmount = Math.max(amountNum - paidAmountNum, 0);
  const overpaid = paidAmountNum > amountNum + 0.005;
  const hasPartial = paidAmountNum > 0 && remainingAmount > 0;

  const selDealTypeId = formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined;
  const selIncomeTypeId = formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined;
  const selSourceId = formData.paymentSourceId ? parseInt(formData.paymentSourceId, 10) : undefined;

  const modal = (
    <div className="fixed inset-0 z-[1300] flex items-center justify-center bg-black/50 p-4">
      <div className="bg-white rounded-xl max-w-6xl w-full max-h-[92vh] shadow-2xl flex flex-col">
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
          <form ref={formRef} onSubmit={handleSubmit} autoComplete="off" className="space-y-6 py-4">
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

            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              <div className="rounded-xl border border-gray-200 bg-gray-50/70 px-4 py-3 shadow-sm">
                <p className="text-xs uppercase tracking-[0.08em] text-gray-500">{t('amount')} {isRequired('Amount') ? '*' : ''}</p>
                <div className="flex items-baseline gap-2">
                  <span className="text-2xl font-semibold text-gray-900">{amountNum.toFixed(2)}</span>
                  <span className="text-xs text-gray-500" title={t('plannedScheduleDate') ?? 'Плановая дата по графику'}>
                    {toRuDate(formData.plannedDate) || t('notSpecified') || 'Не указана'}
                  </span>
                </div>
                <p className="mt-1 text-xs text-gray-500">{t('plannedScheduleDate') ?? 'Плановая дата по графику'}</p>
              </div>
              <div className="rounded-xl border border-gray-200 bg-white px-4 py-3 shadow-sm">
                <p className="text-xs uppercase tracking-[0.08em] text-gray-500">{t('paidAmount') ?? 'Получено'}</p>
                <div className="flex items-baseline gap-2">
                  <span className="text-2xl font-semibold text-emerald-600">{paidAmountNum.toFixed(2)}</span>
                  <span className="text-xs text-gray-500">{t('lastPaymentDate') ?? 'Факт'}</span>
                </div>
              </div>
              <div
                className={`rounded-xl px-4 py-3 shadow-sm border ${
                  remainingAmount > 0
                    ? 'border-amber-200 bg-amber-50'
                    : 'border-emerald-200 bg-emerald-50'
                }`}>
                <p className="text-xs uppercase tracking-[0.08em] text-gray-600">
                  {remainingAmount > 0
                    ? t('partialPaidInfoRemaining') ?? 'Остаток'
                    : t('paymentReceived') ?? 'Закрыт'}
                </p>
                <div className="flex items-baseline gap-2">
                  <span
                    className={`text-2xl font-semibold ${
                      remainingAmount > 0 ? 'text-amber-700' : 'text-emerald-700'
                    }`}>
                    {remainingAmount > 0 ? remainingAmount.toFixed(2) : t('completedStatus')}
                  </span>
                  {hasPartial && (
                    <span className="inline-flex items-center rounded-full bg-white/70 px-2 py-0.5 text-[11px] font-medium text-amber-800 ring-1 ring-amber-200">
                      {t('rescheduleRemainderOption') ?? 'Перенос остатка'}
                    </span>
                  )}
                </div>
              </div>
            </div>

            {(transferTrail.length > 0 || (formData.rescheduleCount ?? 0) > 0 || hasPartial) && (
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

            <div className="grid gap-6 lg:grid-cols-[0.95fr,1.05fr]">
              <div className="space-y-4 rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                <div className="flex items-center gap-2 text-sm font-semibold text-gray-800">
                  <span className="h-6 w-6 rounded-full bg-blue-50 text-blue-600 grid place-items-center text-xs font-bold">1</span>
                  <span>{t('expectedPaymentDate') ?? 'Планирование платежа'}</span>
                </div>

                <div className="grid gap-3 lg:grid-cols-12 lg:items-end">
                  <div className="lg:col-span-4">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('type') ?? 'Тип'}
                    </label>
                    <select
                      name="type"
                      value={formData.type}
                      onChange={handleChange}
                      disabled={hasLockedInvoice}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                      <option value="Income">{t('income') ?? 'Доход'}</option>
                      <option value="Expense">{t('expense') ?? 'Расход'}</option>
                    </select>
                  </div>

                  <div className="lg:col-span-4">
                    <label className="block text-sm font-medium text-gray-700 mb-2">{t('status')}</label>
                    <select
                      name="status"
                      value={formData.status}
                      onChange={handleChange}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                      <option value="Pending">{t('pending')}</option>
                      <option value="Completed">{t('completedStatus')}</option>
                      <option value="Overdue">{t('overdue')}</option>
                    </select>
                  </div>

                </div>

                <div className="grid gap-3 lg:grid-cols-12 lg:items-end">
                  <div className="lg:col-span-6">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('expectedPaymentDate') ?? 'Ожидаемая дата платежа'}
                    </label>
                    <input
                      type="date"
                      name="date"
                      value={formData.date}
                      onChange={handleChange}
                      required
                      className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${isRequired('Date') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
                    />
                  </div>
                  <div className="lg:col-span-6">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('plannedScheduleDate') ?? 'Плановая дата по графику'}
                    </label>
                    <input
                      type="date"
                      name="plannedDate"
                      value={formData.plannedDate}
                      onChange={handleChange}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>
                </div>

                <div className="grid gap-3 lg:grid-cols-12 lg:items-end">
                  <div className="lg:col-span-4">
                    <label className="block text-sm font-medium text-gray-700 mb-2">{t('amount')}</label>
                    <input
                      ref={amountRef}
                      type="text"
                      name="amount"
                      value={formData.amount}
                      onChange={(e) => handleAmountInput(e.target.value, e.currentTarget)}
                      onBlur={formatAmountOnBlur}
                      onWheel={(e) => (e.currentTarget as HTMLInputElement).blur()}
                      inputMode="decimal"
                      className={`w-full px-3 py-2 border rounded-lg focus:ring-2 ${isRequired('Amount') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
                      placeholder="0.00"
                    />
                  </div>

                  <div className="lg:col-span-4">
                    <label className="block text-sm font-medium text-gray-700 mb-2">{t('paidAmount')}</label>
                    <input
                      type="text"
                      name="paidAmount"
                      value={formData.paidAmount}
                      onChange={(e) =>
                        setFormData((s) => ({
                          ...s,
                          paidAmount: e.target.value,
                        }))
                      }
                      onBlur={formatPaidAmountOnBlur}
                      inputMode="decimal"
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder="0.00"
                    />
                  </div>

                  <div className="lg:col-span-4 space-y-2">
                    <label className="block text-sm font-medium text-gray-700">{t('paidDate')}</label>
                    <input
                      type="date"
                      name="paidDate"
                      value={formData.paidDate}
                      onChange={handleChange}
                      disabled={!formData.isPaid}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:bg-gray-100"
                    />
                  </div>
                </div>

                {hasPartial && (
                  <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 shadow-inner">
                    <div className="flex flex-wrap items-center justify-between gap-2 font-semibold">
                      <span className="flex items-center gap-1">
                        <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-white text-xs font-bold text-amber-700 ring-1 ring-amber-200">
                          {t('remaining') ?? 'Остаток'}
                        </span>
                        {t('remainingAmount', { amount: remainingAmount.toFixed(2) }) ??
                          `Остаток: ${remainingAmount.toFixed(2)}`}
                      </span>
                      <span className="rounded-full bg-white/80 px-2 py-1 text-[11px] uppercase tracking-wide text-amber-700 ring-1 ring-amber-200">
                        {t('partialPaidBadge') ?? 'частичный платёж'}
                      </span>
                    </div>
                    <p className="mt-2 text-xs text-amber-700">
                      {t('partialPaidReminder') ??
                        'Обновите ожидаемую дату поступления оставшейся суммы.'}
                    </p>
                    <div className="mt-3 space-y-2 rounded-md border border-amber-200 bg-white/80 p-3 text-amber-900">
                      <label className="flex items-start gap-2 text-sm font-medium text-amber-900">
                        <input
                          type="checkbox"
                          checked={rescheduleRemainder}
                          onChange={(e) => setRescheduleRemainder(e.target.checked)}
                          className="mt-1 h-4 w-4 rounded border-amber-300 text-amber-600 focus:ring-amber-500"
                        />
                        <span>{t('rescheduleRemainderOption') ?? 'Перенести остаток на новую дату'}</span>
                      </label>
                      <div className="grid grid-cols-1 gap-2 sm:grid-cols-[auto,1fr] sm:items-center">
                        <label className="text-sm text-gray-700" htmlFor="reschedule-date">
                          {t('rescheduleRemainderDate') ?? 'Новая дата остатка'}
                        </label>
                        <input
                          id="reschedule-date"
                          type="date"
                          value={rescheduleDate}
                          onChange={(e) => setRescheduleDate(e.target.value)}
                          disabled={!rescheduleRemainder}
                          className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-amber-500 focus:ring-2 focus:ring-amber-500 disabled:bg-gray-100"
                        />
                      </div>
                      <p className="text-xs text-amber-700">
                        {t('rescheduleRemainderHint') ??
                          'Текущий платёж будет закрыт на внесённую сумму, остаток появится в календаре новой записью.'}
                      </p>
                    </div>
                  </div>
                )}

                {overpaid && (
                  <div className="text-sm text-rose-600">
                    {t('partialPaidOverpaid') ??
                      'Внесённая сумма превышает запланированную. Она будет ограничена суммой платежа.'}{' '}
                    {amountNum.toFixed(2)}
                  </div>
                )}

                {formData.rescheduleCount > 0 && (
                  <div className="text-sm text-gray-600">
                    {(t('rescheduleCountLabel') ?? 'Количество переносов:')}{' '}
                    {formData.rescheduleCount}
                  </div>
                )}

                <div className="grid gap-3 lg:grid-cols-12 lg:items-end">
                  <div className="lg:col-span-4">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('lastPaymentDate') ?? 'Дата фактического платежа'}
                    </label>
                    <input
                      type="date"
                      name="lastPaymentDate"
                      value={formData.lastPaymentDate}
                      onChange={handleChange}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>

                  <div className="lg:col-span-4">
                    <label className="flex h-full items-center gap-2 text-sm font-medium text-gray-700">
                      <input
                        type="checkbox"
                        name="isPaid"
                        checked={formData.isPaid}
                        onChange={handleChange}
                        className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                      />
                      <span>{t('paymentReceived')}</span>
                    </label>
                  </div>

                  <div className="lg:col-span-4">
                    <label className="flex h-full items-center gap-2 text-sm text-gray-700">
                      <input
                        type="checkbox"
                        checked={syncDateWithPayment}
                        onChange={(e) => setSyncDateWithPayment(e.target.checked)}
                        className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                      />
                      <span>{t('syncExpectedWithFact') ?? 'Синхронизировать ожидаемую дату с фактической'}</span>
                    </label>
                  </div>
                </div>
              </div>

              <div className="space-y-4 rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                <div className="flex items-center gap-2 text-sm font-semibold text-gray-800">
                  <span className="h-6 w-6 rounded-full bg-indigo-50 text-indigo-600 grid place-items-center text-xs font-bold">2</span>
                  <span>{t('client')}</span>
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
                      <label className="block text-sm font-medium text-gray-700">{t('client')} {isRequired('ClientId') ? '*' : ''}</label>
                      {selectedClient?.clientStatus ? (
                        <ClientStatusBadge status={selectedClient.clientStatus} className="shrink-0" />
                      ) : null}
                    </div>
                    <select
                      name="clientId"
                      value={formData.clientId}
                      onChange={handleChange}
                      disabled={hasLockedInvoice}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                      <option value="">{t('selectClient')}</option>
                      {clients.map((client) => (
                        <option key={client.id} value={client.id}>
                          {client.name}
                          {client.company ? ` — ${client.company}` : ''}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('case') ?? 'Дело'}
                      </label>
                      <select
                        key={(formData.clientId || 'no-client') + ':' + cases.length}
                        name="clientCaseId"
                        value={formData.clientCaseId}
                        onChange={handleChange}
                        disabled={!formData.clientId}
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

                  <div className="grid gap-3 sm:grid-cols-[2fr,1fr] sm:items-end">
                    <div className="relative">
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {(t('account') ?? 'Счёт') + ' (legacy, только чтение)'}
                      </label>
                      <p className="mb-2 text-xs text-gray-500">
                        Поле больше не редактируется вручную. Для привязки используйте блок «Связанный счёт» ниже.
                      </p>

                      <input
                        name="account"
                        value={formData.account}
                        placeholder="Например: Счёт № 12345"
                        maxLength={120}
                        readOnly
                        tabIndex={-1}
                        className="w-full cursor-not-allowed rounded-lg border border-gray-300 bg-gray-50 px-3 py-2 text-gray-600"
                      />

                      {false &&
                        !hasSelectedInvoice &&
                        accountOpen &&
                        (accountOpts.length > 0 || (formData.account ?? '').trim() !== '') && (
                          <div className="absolute z-20 mt-1 w-full max-h-56 overflow-auto rounded-lg border bg-white shadow">
                            {accountLoading && (
                              <div className="px-3 py-2 text-sm text-gray-500">Загрузка…</div>
                            )}

                            {formData.account &&
                              !accountOpts.some(
                                (x) => x.account.toLowerCase() === formData.account.trim().toLowerCase(),
                              ) && (
                                <button
                                  type="button"
                                  className="w-full text-left px-3 py-2 text-sm hover:bg-gray-100"
                                  onMouseDown={(e) => e.preventDefault()}
                                  onClick={() => setAccountOpen(false)}>
                                  Создать «{formData.account.trim()}»
                                </button>
                              )}

                            {accountOpts.map((opt) => {
                              const key = `${opt.account}__${opt.accountDate ?? ''}`;
                              const human =
                                opt.account + (opt.accountDate ? ` от ${toRuDate(opt.accountDate)}` : '');

                              return (
                                <button
                                  key={key}
                                  type="button"
                                  className="w-full text-left px-3 py-2 text-sm hover:bg-gray-100"
                                  onMouseDown={(e) => e.preventDefault()}
                                  onClick={async () => {
                                    setFormData((s) => ({ ...s, account: opt.account }));
                                    setAccountOpen(false);

                                    if (opt.accountDate) {
                                      setFormData((s) => ({
                                        ...s,
                                        accountDate: toDateInputValue(opt.accountDate),
                                      }));
                                      return;
                                    }

                                    try {
                                      const cid = formData.clientId
                                        ? parseInt(formData.clientId, 10)
                                        : undefined;
                                      const caseId = formData.clientCaseId
                                        ? parseInt(formData.clientCaseId, 10)
                                        : undefined;

                                      const resp = await apiService.getAccounts({
                                        clientId: cid,
                                        caseId,
                                        q: opt.account,
                                        take: 50,
                                        withDate: true as const,
                                        dedupe: false,
                                      });

                                      const found = resp.find(
                                        (x) => x.account === opt.account && x.accountDate,
                                      );
                                      if (found?.accountDate) {
                                        setFormData((s) => ({
                                          ...s,
                                          accountDate: toDateInputValue(found.accountDate),
                                        }));
                                      }
                                    } catch {
                                      /* no-op */
                                    }
                                  }}>
                                  {human}
                                </button>
                              );
                            })}
                          </div>
                        )}

                      {false && !hasLockedInvoice && (
                        <div className="mt-3 rounded-lg border border-gray-200 bg-gray-50/60 p-3">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <div>
                              <div className="text-sm font-medium text-gray-800">Связанный счет</div>
                              <div className="text-xs text-gray-500">
                                Выберите существующий счет клиента или создайте новый счет из текущего платежа.
                              </div>
                            </div>
                            <div className="flex flex-wrap gap-2">
                              <button
                                type="button"
                                onClick={() => setInvoicePickerOpen((prev) => !prev)}
                                disabled={!formData.clientId}
                                className="rounded-lg border border-blue-200 bg-white px-3 py-1.5 text-xs font-medium text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                              >
                                {invoicePickerOpen ? 'Скрыть список' : 'Выбрать счет'}
                              </button>
                              <button
                                type="button"
                                onClick={() => void handleCreateInvoiceFromPayment()}
                                disabled={invoiceCreateSubmitting || !formData.clientId}
                                className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60"
                              >
                                {invoiceCreateSubmitting ? 'Открываем…' : 'Создать счет'}
                              </button>
                              {selectedInvoiceSummary && (
                                <button
                                  type="button"
                                  onClick={clearInvoiceSelection}
                                  className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-xs font-medium text-gray-600 transition hover:bg-gray-100"
                                >
                                  Убрать связь
                                </button>
                              )}
                            </div>
                          </div>

                          {selectedInvoiceSummary ? (
                            <div className="mt-3 rounded-lg border border-emerald-200 bg-white px-3 py-2">
                              <div className="text-sm font-semibold text-gray-900">{selectedInvoiceSummary.title}</div>
                              <div className="mt-1 text-xs text-gray-500">{selectedInvoiceSummary.meta}</div>
                            </div>
                          ) : (
                            <div className="mt-3 rounded-lg border border-dashed border-gray-300 bg-white px-3 py-3 text-sm text-gray-500">
                              Связанный счет пока не выбран.
                            </div>
                          )}

                          {invoicePickerOpen && (
                            <div className="mt-3 space-y-3 rounded-lg border border-gray-200 bg-white p-3">
                              <input
                                type="text"
                                value={invoiceSearch}
                                onChange={(event) => setInvoiceSearch(event.target.value)}
                                placeholder="Поиск счета по номеру, описанию или делу"
                                className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                              />

                              {invoicePickerLoading && (
                                <div className="text-sm text-gray-500">Загрузка счетов…</div>
                              )}

                              {invoicePickerError && (
                                <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                                  {invoicePickerError}
                                </div>
                              )}

                              {!invoicePickerLoading && !invoicePickerError && invoiceOptions.length === 0 && (
                                <div className="rounded-lg border border-dashed border-gray-300 bg-gray-50 px-3 py-3 text-sm text-gray-500">
                                  Подходящие счета не найдены. Можно создать новый счет из этого платежа.
                                </div>
                              )}

                              {!invoicePickerLoading && invoiceOptions.length > 0 && (
                                <div className="max-h-56 space-y-2 overflow-y-auto">
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
                                        className={`w-full rounded-lg border px-3 py-2 text-left transition ${
                                          selected
                                            ? 'border-emerald-300 bg-emerald-50'
                                            : 'border-gray-200 bg-white hover:border-blue-200 hover:bg-blue-50/50'
                                        }`}
                                      >
                                        <div className="text-sm font-semibold text-gray-900">
                                          Счет № {invoiceOption.number}
                                        </div>
                                        <div className="mt-1 text-xs text-gray-500">
                                          {toRuDate(invoiceOption.date) || invoiceOption.date}
                                          {caseLabel ? ` · ${caseLabel}` : ''}
                                          {invoiceOption.description ? ` · ${invoiceOption.description}` : ''}
                                        </div>
                                      </button>
                                    );
                                  })}
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                      )}
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {(t('accountDate') ?? 'Дата счёта') + ' (legacy, только чтение)'}
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
                            <div className="text-sm font-medium text-gray-800">Связанный счет</div>
                            <div className="text-xs text-gray-500">
                              Выберите существующий счет клиента или создайте новый счет из текущего платежа.
                            </div>
                          </div>
                          {!hasLockedInvoice && (
                            <div className="flex flex-wrap gap-2">
                              <button
                                type="button"
                                onClick={() => setInvoicePickerOpen((prev) => !prev)}
                                disabled={!formData.clientId}
                                className="rounded-lg border border-blue-200 bg-white px-3 py-1.5 text-xs font-medium text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                              >
                                {invoicePickerOpen ? 'Скрыть список' : 'Выбрать счет'}
                              </button>
                              <button
                                type="button"
                                onClick={() => void handleCreateInvoiceFromPayment()}
                                disabled={invoiceCreateSubmitting || !formData.clientId}
                                className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-medium text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60"
                              >
                                {invoiceCreateSubmitting ? 'Открываем…' : 'Создать счет'}
                              </button>
                              {selectedInvoiceSummary && (
                                <button
                                  type="button"
                                  onClick={clearInvoiceSelection}
                                  className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-xs font-medium text-gray-600 transition hover:bg-gray-100"
                                >
                                  Убрать связь
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
                            Связанный счет пока не выбран.
                          </div>
                        )}

                        {!hasLockedInvoice && invoicePickerOpen && (
                          <div className="mt-3 space-y-3 rounded-lg border border-gray-200 bg-white p-3">
                            <input
                              type="text"
                              value={invoiceSearch}
                              onChange={(event) => setInvoiceSearch(event.target.value)}
                              placeholder="Поиск счета по номеру, описанию или делу"
                              className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:ring-2 focus:ring-blue-500"
                            />

                            {invoicePickerLoading && (
                              <div className="text-sm text-gray-500">Загрузка счетов…</div>
                            )}

                            {invoicePickerError && (
                              <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                                {invoicePickerError}
                              </div>
                            )}

                            {!invoicePickerLoading && !invoicePickerError && invoiceOptions.length === 0 && (
                              <div className="rounded-lg border border-dashed border-gray-300 bg-gray-50 px-3 py-3 text-sm text-gray-500">
                                Подходящие счета не найдены. Можно создать новый счет из этого платежа.
                              </div>
                            )}

                            {!invoicePickerLoading && invoiceOptions.length > 0 && (
                              <div className="max-h-56 space-y-2 overflow-y-auto">
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
                                      className={`w-full rounded-lg border px-3 py-2 text-left transition ${
                                        selected
                                          ? 'border-emerald-300 bg-emerald-50'
                                          : 'border-gray-200 bg-white hover:border-blue-200 hover:bg-blue-50/50'
                                      }`}
                                    >
                                      <div className="text-sm font-semibold text-gray-900">
                                        Счет № {invoiceOption.number}
                                      </div>
                                      <div className="mt-1 text-xs text-gray-500">
                                        {toRuDate(invoiceOption.date) || invoiceOption.date}
                                        {caseLabel ? ` · ${caseLabel}` : ''}
                                        {invoiceOption.description ? ` · ${invoiceOption.description}` : ''}
                                      </div>
                                    </button>
                                  );
                                })}
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    </div>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('dealType')}
                      </label>
                      <select
                        name="dealTypeId"
                        value={formData.dealTypeId}
                        onChange={handleChange}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">{t('selectDealType')}</option>
                        {buildOptionsWithSelected(dealTypes, selDealTypeId, (d) => d.isActive).map((d) => (
                          <option key={d.id} value={String(d.id)}>
                            {d.name}
                            {!d.isActive ? ' (не активно)' : ''}
                          </option>
                        ))}
                      </select>{' '}
                      {(() => {
                        const picked = dealTypes.find((d) => d.id === selDealTypeId);
                        if (picked && !picked.isActive) {
                          return (
                            <div className="mt-1 text-xs text-amber-600">
                              Внимание: выбранный тип сделки деактивирован. Вы можете сохранить платёж как есть или выбрать
                              актуальный тип.
                            </div>
                          );
                        }
                        return null;
                      })()}
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {formData.type === 'Income'
                          ? t('incomeType') ?? 'Тип дохода'
                          : t('expenseType') ?? 'Тип расхода'}
                      </label>
                      <select
                        name="incomeTypeId"
                        value={formData.incomeTypeId}
                        onChange={handleChange}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">
                          {formData.type === 'Income'
                            ? t('selectIncomeType') ?? 'Выберите тип дохода'
                            : t('selectExpenseType') ?? 'Выберите тип расхода'}
                        </option>
                        {buildOptionsWithSelected(
                          incomeTypes,
                          selIncomeTypeId,
                          (it) => it.isActive && it.paymentType === formData.type,
                        ).map((it) => (
                          <option key={it.id} value={String(it.id)}>
                            {it.name}
                            {!it.isActive ? ' (не активно)' : ''}
                            {it.paymentType !== formData.type ? ' (другое направление)' : ''}
                          </option>
                        ))}
                      </select>
                      {(() => {
                        const picked = incomeTypes.find((it) => it.id === selIncomeTypeId);
                        if (!picked) return null;
                        if (!picked.isActive || picked.paymentType !== formData.type) {
                          return (
                            <div className="mt-1 text-xs text-amber-600">
                              Внимание: выбранная категория
                              {!picked.isActive ? ' деактивирована' : ''}
                              {picked.paymentType !== formData.type ? ' и относится к другому направлению' : ''}.
                              Вы можете сохранить платёж как есть или выбрать актуальную категорию.
                            </div>
                          );
                        }
                        return null;
                      })()}
                    </div>
                  </div>

                  <div className="grid gap-3 sm:grid-cols-[auto,1fr] sm:items-center">
                    <label className="flex items-center gap-2 text-sm text-gray-700">
                      <input
                        type="checkbox"
                        name="isPaid"
                        checked={formData.isPaid}
                        onChange={handleChange}
                        className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                      />
                      <span>{t('paymentReceived')}</span>
                    </label>

                    {formData.isPaid && (
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-2">
                          {t('paidDate')}
                        </label>
                        <input
                          type="date"
                          name="paidDate"
                          value={formData.paidDate}
                          onChange={handleChange}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                        />
                      </div>
                    )}
                  </div>
                </div>
              </div>
            </div>

            <div className="grid gap-4 lg:grid-cols-2">
              <div className="rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('description')} {isRequired('Description') ? '*' : ''}
                </label>
                <textarea
                  name="description"
                  value={formData.description}
                  onChange={handleChange}
                  rows={3}
                  className={`w-full rounded-lg border px-3 py-2 focus:ring-2 ${isRequired('Description') ? 'border-red-400 focus:ring-red-200 focus:border-red-500' : 'border-gray-300 focus:ring-blue-500 focus:border-blue-500'}`}
                  placeholder={t('paymentDescription')}
                />
              </div>

              <div className="rounded-xl border border-gray-100 bg-white px-4 py-3 shadow-sm">
                <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
                <textarea
                  name="notes"
                  value={formData.notes}
                  onChange={handleChange}
                  rows={3}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  placeholder={t('additionalNotes')}
                />
              </div>
            </div>

            {systemNotesEntries.length > 0 && (
              <div className="rounded-xl border border-gray-100 bg-gray-50 px-4 py-3 shadow-sm">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('systemNotesLabel') ?? 'Служебные заметки'}
                </label>
                <div className="max-h-40 overflow-y-auto rounded-lg border border-gray-200 bg-white p-3 text-sm text-gray-700">
                  <ul className="space-y-2">
                    {systemNotesEntries.map((note, idx) => (
                      <li key={`${note}-${idx}`} className="flex gap-2">
                        <span className="text-gray-400 leading-6">•</span>
                        <span className="flex-1 whitespace-pre-wrap">{note}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            <div className="sticky bottom-0 flex flex-col gap-3 border-t border-gray-200 bg-white/95 py-3 backdrop-blur sm:flex-row sm:items-center sm:justify-between">
              {canDelete && (
                <button
                  type="button"
                  onClick={async () => {
                    if (!payment) return;
                    const ok = await confirm({ title: 'Подтверждение удаления', description: t('confirmDeletePayment') ?? 'Удалить платеж?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' });
                    if (!ok) return;
                    await onDelete!(payment.id);
                    onClose();
                  }}
                  className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-red-50 px-4 py-2 font-medium text-red-700 transition-colors hover:bg-red-100 sm:w-auto sm:min-w-40">
                  <Trash2 className="h-4 w-4" />
                  {t('delete')}
                </button>
              )}

              <div className="flex w-full gap-3 sm:w-auto sm:justify-end">
                <button
                  type="submit"
                  disabled={loading}
                  className={`flex-1 rounded-lg px-4 py-2 font-medium transition-colors sm:min-w-40 sm:flex-none ${
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

                <button
                  type="button"
                  onClick={onClose}
                  className="flex-1 rounded-lg bg-gray-200 px-4 py-2 font-medium text-gray-800 transition-colors hover:bg-gray-300 sm:min-w-40 sm:flex-none">
                  {t('cancel')}
                </button>
              </div>
            </div>
          </form>
        </div>
      </div>
    </div>
  );

  const createInvoiceModal = (
    <InvoiceModal
      open={invoiceCreateOpen}
      mode="create"
      invoice={null}
      resumeDraft={invoiceCreateDraft}
      onClose={() => {
        setInvoiceCreateOpen(false);
        setInvoiceCreateError(null);
      }}
      onSubmit={handleSubmitCreatedInvoice}
      submitting={invoiceCreateSubmitting}
      errorMessage={invoiceCreateError}
      clients={clients}
      responsibles={invoiceCreateResponsibles}
      lookupsLoading={clientsLoading || invoiceCreateLookupsLoading}
      lookupsError={invoiceCreateLookupsError}
      defaultClientId={formData.clientId ? Number(formData.clientId) : defaultClientId}
      defaultType={formData.type}
    />
  );

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
      {createInvoiceModal}
    </>
  );
}
