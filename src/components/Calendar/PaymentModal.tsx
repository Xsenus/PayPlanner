import React, { useEffect, useRef, useState } from 'react';
import { X, Trash2 } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useDictionaries } from '../../hooks/useDictionaries';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Payment, PaymentStatus, ClientCase } from '../../types';
import { toDateInputValue, fromInputToApiDate, todayYMD, toRuDate } from '../../utils/dateUtils';
import { buildOptionsWithSelected } from '../../utils/formOptions';

type PaymentKind = 'Income' | 'Expense';
type AccountOption = { account: string; accountDate?: string | null };

interface PaymentModalProps {
  defaultClientId?: number;
  defaultClientCaseId?: number;
  casesPrefetch?: ClientCase[];
  type?: PaymentKind;
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (
    payment:
      | Omit<Payment, 'id' | 'createdAt'>
      | ({ id: number } & Omit<Payment, 'id' | 'createdAt'>),
  ) => Promise<void>;
  payment?: Payment | null;
  onDelete?: (id: number) => Promise<void>;
}

export function PaymentModal({
  isOpen,
  onClose,
  onSubmit,
  payment,
  onDelete,
  defaultClientId,
  defaultClientCaseId,
  casesPrefetch,
  type,
}: PaymentModalProps) {
  const accountInputRef = useRef<HTMLInputElement>(null);
  const formRef = useRef<HTMLFormElement>(null);

  function suppressBrowserAutocomplete() {
    const el = accountInputRef.current;
    if (!el) return;
    el.setAttribute('readonly', 'true');
    setTimeout(() => el.removeAttribute('readonly'), 80);
  }

  const { clients } = useClients();
  const { dealTypes, incomeTypes, paymentSources } = useDictionaries();
  const { t } = useTranslation();

  const [loading, setLoading] = useState(false);
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [fallbackCase, setFallbackCase] = useState<ClientCase | null>(null);

  const [formData, setFormData] = useState({
    date: todayYMD(),
    amount: '',
    status: 'Pending' as PaymentStatus,
    description: '',
    isPaid: false,
    paidDate: '',
    paidAmount: '',
    notes: '',
    clientId: '',
    clientCaseId: '',
    dealTypeId: '',
    incomeTypeId: '',
    paymentSourceId: '',
    paymentStatusId: '',
    type: (type ?? 'Income') as PaymentKind,
    account: '',
    accountDate: '',
    initialDate: '',
    systemNotes: '',
    rescheduleCount: 0,
  });

  const [accountOpen, setAccountOpen] = useState(false);
  const [accountOpts, setAccountOpts] = useState<AccountOption[]>([]);
  const [accountLoading, setAccountLoading] = useState(false);
  const [autoAlignExpectedDate, setAutoAlignExpectedDate] = useState(false);

  /* -------------------- Подсказки по счёту -------------------- */
  useEffect(() => {
    let stop = false;
    const timer = setTimeout(async () => {
      try {
        setAccountLoading(true);
        const cid = formData.clientId ? parseInt(formData.clientId, 10) : undefined;
        const caseId = formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined;

        const params = {
          clientId: cid,
          caseId,
          q: formData.account,
          take: 20,
          withDate: true,
          dedupe: true,
        };

        const dataRaw = (await apiService.getAccounts(
          params as unknown as Parameters<typeof apiService.getAccounts>[0],
        )) as unknown;

        let normalized: AccountOption[] = [];
        if (Array.isArray(dataRaw)) {
          normalized = dataRaw
            .map((x): AccountOption | null => {
              if (typeof x === 'string') {
                const acc = x.trim();
                return acc ? { account: acc } : null;
              }
              if (x && typeof x === 'object') {
                const obj = x as { account?: unknown; accountDate?: unknown };
                const acc = typeof obj.account === 'string' ? obj.account.trim() : '';
                if (!acc) return null;
                const accDate =
                  typeof obj.accountDate === 'string' && obj.accountDate ? obj.accountDate : null;
                return { account: acc, accountDate: accDate };
              }
              return null;
            })
            .filter((v): v is AccountOption => v !== null);
        }

        const uniq = new Map<string, AccountOption>();
        for (const it of normalized) {
          const key = `${it.account}__${it.accountDate ?? ''}`;
          if (!uniq.has(key)) uniq.set(key, it);
        }

        if (!stop) setAccountOpts(Array.from(uniq.values()));
      } finally {
        if (!stop) setAccountLoading(false);
      }
    }, 250);

    return () => {
      stop = true;
      clearTimeout(timer);
    };
  }, [formData.account, formData.clientId, formData.clientCaseId]);

  const prevOpenRef = useRef(false);
  const paymentRef = useRef<Payment | null>(null);

  useEffect(() => {
    if (paymentRef.current?.id !== payment?.id) {
      paymentRef.current = payment ?? null;
    }
  }, [payment]);

  useEffect(() => {
    if (!isOpen) {
      prevOpenRef.current = false;
      return;
    }

    if (!prevOpenRef.current) {
      const p = paymentRef.current;

      if (p) {
        setFormData({
          date: toDateInputValue(p.date) || todayYMD(),
          amount: Number.isFinite(p.amount) ? p.amount.toFixed(2) : '',
          status: p.status,
          description: p.description,
          isPaid: p.isPaid,
          paidDate: toDateInputValue(p.paidDate) || '',
          paidAmount: Number.isFinite(p.paidAmount) ? p.paidAmount.toFixed(2) : '',
          notes: p.notes || '',
          clientId: (p.clientId ?? defaultClientId)?.toString() || '',
          clientCaseId: (p.clientCaseId ?? defaultClientCaseId)?.toString() || '',
          dealTypeId: p.dealTypeId?.toString() || '',
          incomeTypeId: p.incomeTypeId?.toString() || '',
          paymentSourceId: p.paymentSourceId?.toString() || '',
          paymentStatusId: p.paymentStatusId?.toString() || '',
          type: (p.type as PaymentKind) ?? type ?? 'Income',
          account: p.account ?? '',
          accountDate: toDateInputValue(p.accountDate) || '',
          initialDate: toDateInputValue(p.initialDate ?? p.date) || toDateInputValue(p.date) || todayYMD(),
          systemNotes: p.systemNotes ?? '',
          rescheduleCount: p.rescheduleCount ?? 0,
        });
      } else {
        setFormData({
          date: todayYMD(),
          amount: '',
          status: 'Pending',
          description: '',
          isPaid: false,
          paidDate: '',
          paidAmount: '',
          notes: '',
          clientId: defaultClientId?.toString() || '',
          clientCaseId: defaultClientCaseId?.toString() || '',
          dealTypeId: '',
          incomeTypeId: '',
          paymentSourceId: '',
          paymentStatusId: '',
          type: (type ?? 'Income') as PaymentKind,
          account: '',
          accountDate: '',
          initialDate: todayYMD(),
          systemNotes: '',
          rescheduleCount: 0,
        });
      }

      prevOpenRef.current = true;
      setAutoAlignExpectedDate(false);
    }
  }, [isOpen, defaultClientId, defaultClientCaseId, type]);

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
    if (!autoAlignExpectedDate) return;
    if (!formData.paidDate) return;
    setFormData((s) => ({ ...s, date: s.paidDate }));
  }, [autoAlignExpectedDate, formData.paidDate, isOpen]);

  useEffect(() => {
    if (!hasPartialPayment && autoAlignExpectedDate) {
      setAutoAlignExpectedDate(false);
    }
  }, [autoAlignExpectedDate, hasPartialPayment]);

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

  const clientIdRef = useRef(formData.clientId);
  useEffect(() => {
    clientIdRef.current = formData.clientId;
  }, [formData.clientId]);

  const caseIdRef = useRef(formData.clientCaseId);
  useEffect(() => {
    caseIdRef.current = formData.clientCaseId;
  }, [formData.clientCaseId]);

  const parseMoneyValue = (value: string): number => {
    if (!value) return 0;
    const normalized = value.replace(/\s+/g, '').replace(',', '.');
    const parsed = Number(normalized);
    return Number.isFinite(parsed) ? parsed : 0;
  };

  const clampPaidAmount = (paid: number, amount: number): number => {
    if (!Number.isFinite(paid)) return 0;
    const safePaid = paid < 0 ? 0 : paid;
    if (!Number.isFinite(amount) || amount <= 0) return safePaid;
    return safePaid > amount ? amount : safePaid;
  };

  const formatMoneyString = (value: number): string => value.toFixed(2);

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

  useEffect(() => {
    if (!isOpen) return;
    setFormData((s) => {
      if (!s.amount?.trim()) {
        return s.paidAmount ? { ...s, paidAmount: '' } : s;
      }
      const amountValue = parseMoneyValue(s.amount);
      if (!Number.isFinite(amountValue)) return s;
      const paidValue = parseMoneyValue(s.paidAmount);
      const clamped = clampPaidAmount(paidValue, amountValue);
      const normalized = formatMoneyString(clamped);
      if (paidValue !== clamped) {
        return { ...s, paidAmount: clamped === 0 ? '' : normalized };
      }
      return s;
    });
  }, [formData.amount, isOpen]);

  const markPaid = () =>
    setFormData((s) => {
      const amountValue = parseMoneyValue(s.amount);
      const nextPaid = amountValue > 0 ? formatMoneyString(amountValue) : s.paidAmount || '';
      return {
        ...s,
        isPaid: true,
        status: 'Completed',
        paidDate: s.paidDate || todayYMD(),
        paidAmount: nextPaid,
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
  const paidAmountRef = useRef<HTMLInputElement>(null);

  const handleCurrencyInput = (
    raw: string,
    field: 'amount' | 'paidAmount',
    ref: React.RefObject<HTMLInputElement>,
    el?: HTMLInputElement,
  ) => {
    let v = raw.replace(/[^\d.,]/g, '');
    const caret = el?.selectionStart ?? v.length;
    if (!v) {
      setFormData((s) => ({ ...s, [field]: '' }));
      return;
    }

    const firstSepIdx = v.search(/[.,]/);
    if (firstSepIdx === -1) {
      const intPart = v.replace(/^0+(?=\d)/, '') || '0';
      const newVal = `${intPart}.00`;
      const nextCaret = Math.min(caret, intPart.length);

      setFormData((s) => ({ ...s, [field]: newVal }));
      requestAnimationFrame(() => {
        const node = ref.current;
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

    setFormData((s) => {
      const next = { ...s, [field]: newVal } as typeof s;
      if (field === 'paidAmount') {
        const amountValue = parseMoneyValue(next.amount);
        const paidValue = parseMoneyValue(newVal);
        const clamped = clampPaidAmount(paidValue, amountValue);
        const normalized = formatMoneyString(clamped);
        if (clamped !== paidValue) {
          next.paidAmount = normalized;
          requestAnimationFrame(() => {
            const node = ref.current;
            if (node) node.setSelectionRange(normalized.length, normalized.length);
          });
        }
      }
      return next;
    });
    requestAnimationFrame(() => {
      const node = ref.current;
      if (node) node.setSelectionRange(nextCaret, nextCaret);
    });
  };

  const formatCurrencyOnBlur = (field: 'amount' | 'paidAmount') => {
    const raw = formData[field].trim();
    if (!raw) return;

    const cleaned = raw.replace(',', '.').replace(/[^\d.]/g, '');
    const num = Number(cleaned);
    if (!Number.isFinite(num)) {
      setFormData((s) => ({ ...s, [field]: '' }));
      return;
    }
    const fixed = num.toFixed(2);
    setFormData((s) => {
      if (field === 'paidAmount') {
        const amountValue = parseMoneyValue(s.amount);
        const clamped = clampPaidAmount(num, amountValue);
        return { ...s, paidAmount: formatMoneyString(clamped) };
      }
      return { ...s, amount: fixed };
    });
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const pickedCat = incomeTypes.find((it) => String(it.id) === formData.incomeTypeId);
    if (pickedCat && pickedCat.paymentType !== formData.type) {
      alert(
        t('invalidCategoryForDirection') ?? 'Тип категории не соответствует направлению платежа',
      );
      return;
    }

    if (loading) return;

    const amountNum = parseMoneyValue(formData.amount);
    if (!Number.isFinite(amountNum) || amountNum < 0) {
      alert(t('invalidAmount') ?? 'Укажите корректную сумму платежа.');
      return;
    }

    let paidAmountNum = clampPaidAmount(parseMoneyValue(formData.paidAmount), amountNum);
    if (formData.isPaid) {
      paidAmountNum = amountNum;
    }

    const remaining = amountNum - paidAmountNum;
    const hasPartialPayment = paidAmountNum > 0 && remaining > 0.009;

    if (hasPartialPayment) {
      const confirmMessage =
        t('confirmPartialPayment') ??
        `Внесено ${paidAmountNum.toFixed(2)} из ${amountNum.toFixed(2)}. Остаток ${remaining.toFixed(
          2,
        )}. Продолжить?`;
      const confirmed = window.confirm(confirmMessage);
      if (!confirmed) return;
    }

    setLoading(true);

    const normalizedDate = fromInputToApiDate(formData.date)!;
    const normalizedPaidDate =
      (formData.isPaid || paidAmountNum === amountNum) && formData.paidDate
        ? fromInputToApiDate(formData.paidDate)
        : undefined;
    const normalizedInitialDate = formData.initialDate
      ? fromInputToApiDate(formData.initialDate)
      : undefined;

    try {
      const isCompleted =
        formData.status === 'Cancelled' || formData.status === 'Processing'
          ? false
          : formData.isPaid || Math.abs(paidAmountNum - amountNum) < 0.01;

      const statusToSend =
        formData.status === 'Cancelled' || formData.status === 'Processing'
          ? formData.status
          : isCompleted
          ? 'Completed'
          : formData.status === 'Completed'
          ? 'Pending'
          : formData.status;

      const paymentData = {
        date: normalizedDate,
        amount: amountNum,
        type: formData.type,
        status: statusToSend,
        description: formData.description,
        isPaid: isCompleted,
        paidDate: normalizedPaidDate,
        notes: formData.notes,
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
        account: formData.account?.trim() || undefined,
        accountDate: formData.accountDate ? fromInputToApiDate(formData.accountDate) : undefined,
        paidAmount: Number(paidAmountNum.toFixed(2)),
        initialDate: normalizedInitialDate,
      } as Omit<Payment, 'id' | 'createdAt'>;

      if (payment) {
        await onSubmit({ id: payment.id, ...paymentData });
      } else {
        await onSubmit(paymentData);
      }

      onClose();
    } finally {
      setLoading(false);
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
      setFormData((prev) => ({ ...prev, clientId: value, clientCaseId: '' }));
      return;
    }

    if (name === 'type') {
      setFormData((prev) => ({
        ...prev,
        type: value as PaymentKind,
        incomeTypeId: '',
      }));
      return;
    }

    setFormData((prev) => ({
      ...prev,
      [name]: inputType === 'checkbox' ? (e.target as HTMLInputElement).checked : value,
    }));
  };

  const amountNumber = parseMoneyValue(formData.amount);
  const paidAmountNumber = parseMoneyValue(formData.paidAmount);
  const remainingAmount = Math.max(amountNumber - paidAmountNumber, 0);
  const hasPartialPayment = paidAmountNumber > 0 && remainingAmount > 0.009;

  const canDelete = !!payment && !!onDelete;
  if (!isOpen) return null;

  const selDealTypeId = formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined;
  const selIncomeTypeId = formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined;
  const selSourceId = formData.paymentSourceId ? parseInt(formData.paymentSourceId, 10) : undefined;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center p-4 z-50">
      <div className="bg-white rounded-xl max-w-md w-full max-h-[90vh] overflow-y-auto">
        <div className="p-6">
          <div className="flex items-center justify-between mb-6">
            <h2 className="text-xl font-semibold text-gray-900">
              {payment ? t('editPayment') : t('addPayment')}
            </h2>
            <button
              onClick={onClose}
              type="button"
              className="p-2 hover:bg-gray-100 rounded-lg transition-colors"
              aria-label="Close">
              <X size={20} />
            </button>
          </div>

          <form ref={formRef} onSubmit={handleSubmit} autoComplete="off" className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('type') ?? 'Тип'}
              </label>
              <select
                name="type"
                value={formData.type}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="Income">{t('income') ?? 'Доход'}</option>
                <option value="Expense">{t('expense') ?? 'Расход'}</option>
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('date')}</label>
              <input
                type="date"
                name="date"
                value={formData.date}
                onChange={handleChange}
                required
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('initialPlannedDate') ?? 'Изначально запланированная дата'}
              </label>
              <input
                type="date"
                name="initialDate"
                value={formData.initialDate}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
              {formData.rescheduleCount > 0 && (
                <div className="mt-1 text-xs text-gray-600">
                  {(t('rescheduleCount') ?? 'Количество переносов:') + ' '}
                  <span className="font-medium">{formData.rescheduleCount}</span>
                </div>
              )}
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('amount')}</label>
              <input
                ref={amountRef}
                type="text"
                name="amount"
                value={formData.amount}
                onChange={(e) => handleCurrencyInput(e.target.value, 'amount', amountRef, e.currentTarget)}
                onBlur={() => formatCurrencyOnBlur('amount')}
                onWheel={(e) => (e.currentTarget as HTMLInputElement).blur()}
                inputMode="decimal"
                pattern="^\d+([.,]\d{0,2})?$"
                placeholder="0.00"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('paidAmount') ?? 'Оплачено фактически'}
              </label>
              <input
                ref={paidAmountRef}
                type="text"
                name="paidAmount"
                value={formData.paidAmount}
                onChange={(e) =>
                  handleCurrencyInput(e.target.value, 'paidAmount', paidAmountRef, e.currentTarget)
                }
                onBlur={() => formatCurrencyOnBlur('paidAmount')}
                onWheel={(e) => (e.currentTarget as HTMLInputElement).blur()}
                inputMode="decimal"
                pattern="^\d+([.,]\d{0,2})?$"
                placeholder="0.00"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
              <div className="mt-1 text-xs text-gray-600 space-y-1">
                <div>
                  {(t('remainingToPay') ?? 'Остаток к оплате:')}{' '}
                  <span className={remainingAmount > 0 ? 'text-amber-600 font-medium' : ''}>
                    {formatMoneyString(remainingAmount)} ₽
                  </span>
                </div>
                {hasPartialPayment && (
                  <div className="text-amber-600">
                    {t('partialPaymentHint') ??
                      'Зафиксирован частичный платёж — укажите дату и условия следующего платежа.'}
                  </div>
                )}
              </div>
              {hasPartialPayment && (
                <label className="mt-3 flex items-center gap-2 text-sm text-gray-700">
                  <input
                    type="checkbox"
                    checked={autoAlignExpectedDate}
                    onChange={(e) => setAutoAlignExpectedDate(e.target.checked)}
                  />
                  <span>
                    {t('alignExpectedDateWithPayment') ??
                      'Перенести дату ожидания на фактическую дату оплаты'}
                  </span>
                </label>
              )}
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('client')}</label>
              <select
                name="clientId"
                value={formData.clientId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectClient')}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name} {client.company ? `— ${client.company}` : ''}
                  </option>
                ))}
              </select>
            </div>

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

            {/* СЧЁТ */}
            <div className="relative">
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('account') ?? 'Счёт'}
              </label>

              <input
                ref={accountInputRef}
                name="account"
                value={formData.account}
                onChange={handleChange}
                onFocus={() => {
                  suppressBrowserAutocomplete();
                  setAccountOpen(true);
                }}
                onClick={suppressBrowserAutocomplete}
                onBlur={() => setTimeout(() => setAccountOpen(false), 150)}
                placeholder="Например: Счет № 12345"
                maxLength={120}
                autoComplete="new-password"
                autoCorrect="off"
                autoCapitalize="none"
                spellCheck={false}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />

              {accountOpen &&
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
            </div>

            {/* ДАТА СЧЁТА */}
            <div className="mt-3">
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('accountDate') ?? 'Дата счёта'}
              </label>
              <input
                type="date"
                name="accountDate"
                value={formData.accountDate}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

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
                      Внимание: выбранный тип сделки деактивирован. Вы можете сохранить платёж как
                      есть или выбрать актуальный тип.
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
                      {picked.paymentType !== formData.type
                        ? ' и относится к другому направлению'
                        : ''}
                      . Вы можете сохранить платёж как есть или выбрать актуальную категорию.
                    </div>
                  );
                }
                return null;
              })()}
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
                {buildOptionsWithSelected(paymentSources, selSourceId, (s) => s.isActive).map(
                  (s) => (
                    <option key={s.id} value={String(s.id)}>
                      {s.name}
                      {!s.isActive ? ' (не активно)' : ''}
                    </option>
                  ),
                )}
              </select>
              {(() => {
                const picked = paymentSources.find((s) => s.id === selSourceId);
                if (picked && !picked.isActive) {
                  return (
                    <div className="mt-1 text-xs text-amber-600">
                      Внимание: выбранный источник деактивирован. Вы можете сохранить платёж как
                      есть или выбрать актуальный источник.
                    </div>
                  );
                }
                return null;
              })()}
            </div>

            <div>
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

            <div className="flex items-center">
              <input
                type="checkbox"
                name="isPaid"
                checked={formData.isPaid}
                onChange={handleChange}
                className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
              />
              <label className="ml-2 block text-sm text-gray-700">{t('paymentReceived')}</label>
            </div>

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

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('description')}
              </label>
              <textarea
                name="description"
                value={formData.description}
                onChange={handleChange}
                rows={3}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('paymentDescription')}
              />
            </div>

            {formData.systemNotes && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('systemNotes') ?? 'Системные комментарии'}
                </label>
                <textarea
                  value={formData.systemNotes}
                  readOnly
                  rows={Math.min(6, Math.max(3, formData.systemNotes.split('\n').length))}
                  className="w-full px-3 py-2 border border-gray-200 bg-gray-50 rounded-lg text-sm text-gray-700"
                />
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
              <textarea
                name="notes"
                value={formData.notes}
                onChange={handleChange}
                rows={2}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('additionalNotes')}
              />
            </div>

            <div className="flex flex-col sm:flex-row gap-3 pt-4">
              {canDelete && (
                <button
                  type="button"
                  onClick={async () => {
                    if (!payment) return;
                    const ok = confirm(t('confirmDeletePayment') ?? 'Удалить платеж?');
                    if (!ok) return;
                    await onDelete!(payment.id);
                    onClose();
                  }}
                  className="sm:w-auto w-full inline-flex items-center justify-center gap-2 py-2 px-4 rounded-lg font-medium bg-red-50 text-red-700 hover:bg-red-100 transition-colors">
                  <Trash2 className="h-4 w-4" />
                  {t('delete')}
                </button>
              )}

              <div className="flex-1 flex gap-3">
                <button
                  type="submit"
                  disabled={loading}
                  className={`flex-1 py-2 px-4 rounded-lg font-medium transition-colors ${
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
                  className="flex-1 py-2 px-4 bg-gray-200 text-gray-800 rounded-lg font-medium hover:bg-gray-300 transition-colors">
                  {t('cancel')}
                </button>
              </div>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}
