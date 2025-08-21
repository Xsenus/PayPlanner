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

  const [formData, setFormData] = useState({
    date: todayYMD(),
    amount: '',
    status: 'Pending' as PaymentStatus,
    description: '',
    isPaid: false,
    paidDate: '',
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
  });

  const [accountOpen, setAccountOpen] = useState(false);
  const [accountOpts, setAccountOpts] = useState<AccountOption[]>([]);
  const [accountLoading, setAccountLoading] = useState(false);

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
          amount: p.amount.toString(),
          status: p.status,
          description: p.description,
          isPaid: p.isPaid,
          paidDate: toDateInputValue(p.paidDate) || '',
          notes: p.notes || '',
          clientId: p.clientId?.toString() || '',
          clientCaseId: p.clientCaseId?.toString() || '',
          dealTypeId: p.dealTypeId?.toString() || '',
          incomeTypeId: p.incomeTypeId?.toString() || '',
          paymentSourceId: p.paymentSourceId?.toString() || '',
          paymentStatusId: p.paymentStatusId?.toString() || '',
          type: (p.type as PaymentKind) ?? type ?? 'Income',
          account: p.account ?? '',
          accountDate: toDateInputValue(p.accountDate) || '',
        });
      } else {
        setFormData({
          date: todayYMD(),
          amount: '',
          status: 'Pending',
          description: '',
          isPaid: false,
          paidDate: '',
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
        });
      }

      prevOpenRef.current = true;
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

  const clientIdRef = useRef(formData.clientId);
  useEffect(() => {
    clientIdRef.current = formData.clientId;
  }, [formData.clientId]);

  const caseIdRef = useRef(formData.clientCaseId);
  useEffect(() => {
    caseIdRef.current = formData.clientCaseId;
  }, [formData.clientCaseId]);

  useEffect(() => {
    let cancelled = false;

    const currentClientId = clientIdRef.current;
    const initialCaseId = caseIdRef.current;

    const load = async () => {
      const cid = Number(currentClientId);
      if (!cid) {
        if (!cancelled) {
          setCases([]);
          setFormData((s) => ({ ...s, clientCaseId: '' }));
        }
        return;
      }

      const list = await apiService.getCases(cid);
      if (cancelled) return;

      setCases(list || []);

      if (initialCaseId && !(list || []).some((c) => String(c.id) === initialCaseId)) {
        setFormData((s) => {
          if (s.clientId !== currentClientId) return s;
          if (s.clientCaseId !== initialCaseId) return s;
          return { ...s, clientCaseId: '' };
        });
      }
    };

    load();
    return () => {
      cancelled = true;
    };
  }, [formData.clientId]);

  const markPaid = () =>
    setFormData((s) => ({
      ...s,
      isPaid: true,
      status: 'Completed',
      paidDate: s.paidDate || todayYMD(),
    }));

  const markPending = () =>
    setFormData((s) => ({
      ...s,
      isPaid: false,
      status: 'Pending',
      paidDate: '',
    }));

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

    const amountNum = parseFloat(formData.amount.replace(',', '.'));
    if (!Number.isFinite(amountNum) || amountNum < 0) {
      console.error('Invalid amount value:', formData.amount);
      return;
    }

    setLoading(true);

    const normalizedDate = fromInputToApiDate(formData.date)!;
    const normalizedPaidDate =
      formData.isPaid && formData.paidDate ? fromInputToApiDate(formData.paidDate) : undefined;

    try {
      const paymentData = {
        date: normalizedDate,
        amount: amountNum,
        type: formData.type,
        status: formData.status,
        description: formData.description,
        isPaid: formData.isPaid,
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
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('amount')}</label>
              <input
                type="number"
                name="amount"
                value={formData.amount}
                onChange={handleChange}
                required
                min="0"
                step="0.01"
                inputMode="decimal"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
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
                key={formData.clientId || 'no-client'}
                name="clientCaseId"
                value={formData.clientCaseId}
                onChange={handleChange}
                disabled={!formData.clientId}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg disabled:bg-gray-100 focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectCase') ?? 'Без дела'}</option>
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
