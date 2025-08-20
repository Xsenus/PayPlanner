import React, { useEffect, useRef, useState } from 'react';
import { Calculator as CalcIcon, Save, Loader2, Info, AlertTriangle } from 'lucide-react';
import { apiService } from '../../services/api';
import { useClients } from '../../hooks/useClients';
import { useTranslation } from '../../hooks/useTranslation';
import type { InstallmentRequest, InstallmentResponse, ClientCase } from '../../types';
import { toDateInputValue, fromInputToApiDate } from '../../utils/dateUtils';

type AccountOption = { account: string; accountDate?: string | null };

type PaymentCreate = {
  date: string;
  amount: number;
  type: 'Income' | 'Expense';
  status?: string;
  description?: string;
  isPaid?: boolean;
  notes?: string;
  clientId: number;
  clientCaseId?: number;
  account?: string;
  accountDate?: string | null;
};

type AccountApiItem = string | { account?: unknown; accountDate?: unknown };

function toAccountOption(x: unknown): AccountOption | null {
  if (typeof x === 'string') {
    const acc = x.trim();
    return acc ? { account: acc } : null;
  }
  if (x && typeof x === 'object') {
    const obj = x as { account?: unknown; accountDate?: unknown };
    const acc = typeof obj.account === 'string' ? obj.account.trim() : '';
    if (!acc) return null;
    const accDate = typeof obj.accountDate === 'string' && obj.accountDate ? obj.accountDate : null;
    return { account: acc, accountDate: accDate };
  }
  return null;
}

function isAccountWithDate(x: unknown, acc: string): x is { account: string; accountDate: string } {
  if (typeof x !== 'object' || x === null) return false;
  const obj = x as { account?: unknown; accountDate?: unknown };
  return (
    typeof obj.account === 'string' &&
    obj.account === acc &&
    typeof obj.accountDate === 'string' &&
    !!obj.accountDate
  );
}

export function Calculator() {
  const { t, formatCurrency } = useTranslation();
  const { clients } = useClients();

  const [formData, setFormData] = useState<InstallmentRequest>({
    total: 100000,
    downPayment: 20000,
    annualRate: 5.5,
    months: 60,
    startDate: new Date().toISOString().split('T')[0],
  });

  const [editableResults, setEditableResults] = useState<InstallmentResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [savingPayments, setSavingPayments] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedClientId, setSelectedClientId] = useState<string>('');
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [selectedCaseId, setSelectedCaseId] = useState<string>('');

  const [selectedAccount, setSelectedAccount] = useState<string>('');
  const [selectedAccountDate, setSelectedAccountDate] = useState<string>('');
  const [accountOpen, setAccountOpen] = useState(false);
  const [accountOpts, setAccountOpts] = useState<AccountOption[]>([]);
  const [accountLoading, setAccountLoading] = useState(false);
  const accountInputRef = useRef<HTMLInputElement>(null);

  function suppressBrowserAutocomplete() {
    const el = accountInputRef.current;
    if (!el) return;
    el.readOnly = true;
    requestAnimationFrame(() => {
      el.readOnly = false;
    });
  }

  useEffect(() => {
    let ignore = false;
    setCases([]);

    if (!selectedClientId) {
      setSelectedCaseId('');
      return;
    }

    (async () => {
      try {
        const rows = await apiService.getCases?.(Number(selectedClientId));
        if (ignore) return;
        const list = rows || [];
        setCases(list);

        if (selectedCaseId && !list.some((c) => String(c.id) === selectedCaseId)) {
          setSelectedCaseId('');
        }
      } catch (e) {
        console.warn('getCases failed', e);
      }
    })();

    return () => {
      ignore = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedClientId]);

  useEffect(() => {
    let stop = false;
    const timer = setTimeout(async () => {
      try {
        setAccountLoading(true);
        const cid = selectedClientId ? parseInt(selectedClientId, 10) : undefined;
        const caseId = selectedCaseId ? parseInt(selectedCaseId, 10) : undefined;

        const params: Parameters<typeof apiService.getAccounts>[0] = {
          clientId: cid,
          caseId,
          q: selectedAccount,
          take: 20,
          withDate: true,
          dedupe: true,
        };

        const dataRaw = await apiService.getAccounts(params);
        let normalized: AccountOption[] = [];

        if (Array.isArray(dataRaw)) {
          const arr: AccountApiItem[] = dataRaw as AccountApiItem[];
          normalized = arr.map(toAccountOption).filter((v): v is AccountOption => v !== null);
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
  }, [selectedAccount, selectedClientId, selectedCaseId]);

  const handleNumber = (v: string) => (v === '' ? '' : Number.isNaN(Number(v)) ? 0 : parseFloat(v));

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const { name, value } = e.target;
    setFormData((prev) => ({
      ...prev,
      [name]: name === 'startDate' ? value : (handleNumber(value) as number),
    }));
  };

  const handleCalculate = async (e?: React.FormEvent) => {
    e?.preventDefault?.();
    setLoading(true);
    setError(null);
    try {
      const resp = await apiService.calculateInstallment(formData);
      setEditableResults(JSON.parse(JSON.stringify(resp)) as InstallmentResponse);
    } catch {
      setError(t('apiUnavailable'));
    } finally {
      setLoading(false);
    }
  };

  const handleResultChange = (
    index: number,
    field: keyof InstallmentResponse['items'][number],
    value: string,
  ) => {
    if (!editableResults) return;
    const updated: InstallmentResponse = { ...editableResults, items: [...editableResults.items] };
    const num = parseFloat(value);

    updated.items[index] = {
      ...updated.items[index],
      [field]: field === 'date' ? value : Number.isNaN(num) ? 0 : num,
    } as InstallmentResponse['items'][number];

    if (field !== 'date') {
      const totalPayments = updated.items.reduce((s, it) => s + (Number(it.payment) || 0), 0);
      updated.toPay = totalPayments + (Number(formData.downPayment) || 0);
      updated.overpay = totalPayments - (Number(formData.total) - Number(formData.downPayment));
    }
    setEditableResults(updated);
  };

  const handleSavePayments = async () => {
    if (!editableResults || !selectedClientId) return;
    const confirmed = window.confirm(t('confirmSaveAllPayments') ?? 'Сохранить все платежи?');
    if (!confirmed) return;

    setSavingPayments(true);
    try {
      for (const item of editableResults.items) {
        const payload: PaymentCreate = {
          date: toDateInputValue(item.date as unknown as string),
          amount: item.payment,
          type: 'Income',
          status: 'Pending',
          description: `Рассрочка — платёж ${formatCurrency(item.payment)}`,
          isPaid: false,
          notes: `Основной долг: ${formatCurrency(item.principal)}, Проценты: ${formatCurrency(
            item.interest,
          )}`,
          clientId: parseInt(selectedClientId, 10),
          clientCaseId: selectedCaseId ? parseInt(selectedCaseId, 10) : undefined,
          account: selectedAccount || undefined,
          accountDate: selectedAccountDate ? fromInputToApiDate(selectedAccountDate) : undefined,
        };

        await apiService.createPayment(
          payload as unknown as Parameters<typeof apiService.createPayment>[0],
        );
      }
      alert(t('paymentsSaved'));
    } catch (err) {
      console.error('Failed to save payments:', err);
      alert(t('saveError') ?? 'Ошибка сохранения платежей');
    } finally {
      setSavingPayments(false);
    }
  };

  const formIsValid =
    formData.total > 0 &&
    formData.months >= 1 &&
    formData.annualRate >= 0 &&
    formData.downPayment >= 0 &&
    formData.downPayment <= formData.total;

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white border-b border-gray-100">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 py-4 text-center md:text-left">
          <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 flex items-center gap-3 justify-center md:justify-start">
            <CalcIcon size={32} className="text-blue-600" />
            {t('installmentCalculator')}
          </h1>
          <p className="text-gray-600 mt-2 text-center md:text-left">
            {t('calculateLoanPayments')}
          </p>
        </div>
      </header>

      <main className="max-w-7xl mx-auto p-4 sm:p-6">
        <section className="bg-white rounded-2xl p-4 sm:p-6 shadow-sm border border-gray-100">
          <form onSubmit={handleCalculate} className="space-y-5" aria-describedby="calc-help">
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('totalAmount')}
                </label>
                <div className="relative">
                  <input
                    type="number"
                    inputMode="decimal"
                    name="total"
                    value={formData.total}
                    onChange={handleChange}
                    required
                    min={0}
                    step="0.01"
                    className="w-full pl-3 pr-10 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    aria-label={t('totalAmount') ?? 'Total amount'}
                  />
                  <span className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-400">₽</span>
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('downPayment')}
                </label>
                <div className="relative">
                  <input
                    type="number"
                    inputMode="decimal"
                    name="downPayment"
                    value={formData.downPayment}
                    onChange={handleChange}
                    required
                    min={0}
                    step="0.01"
                    className="w-full pl-3 pr-10 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    aria-label={t('downPayment') ?? 'Down payment'}
                  />
                  <span className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-400">₽</span>
                </div>
                {formData.downPayment > formData.total && (
                  <p className="mt-1 text-xs text-red-600 flex items-center gap-1">
                    <AlertTriangle size={14} />
                    {t('downPaymentTooLarge') ?? 'Первоначальный взнос больше суммы'}
                  </p>
                )}
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('annualInterestRate')}
                </label>
                <div className="relative">
                  <input
                    type="number"
                    inputMode="decimal"
                    name="annualRate"
                    value={formData.annualRate}
                    onChange={handleChange}
                    required
                    min={0}
                    step="0.01"
                    className="w-full pl-3 pr-10 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    aria-label={t('annualInterestRate') ?? 'Annual interest rate'}
                  />
                  <span className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-400">%</span>
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('termMonths')}
                </label>
                <input
                  type="number"
                  name="months"
                  value={formData.months}
                  onChange={handleChange}
                  required
                  min={1}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  aria-label={t('termMonths') ?? 'Term (months)'}
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('startDate')}
                </label>
                <input
                  type="date"
                  name="startDate"
                  value={toDateInputValue(formData.startDate as unknown as string)}
                  onChange={handleChange}
                  required
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-center"
                />
                <p id="calc-help" className="mt-2 text-xs text-gray-500 flex items-center gap-1">
                  <Info size={14} /> {t('calcHint') ?? 'Параметры можно изменить в любой момент.'}
                </p>
              </div>

              <div className="flex flex-col">
                <label className="block text-sm mb-2 opacity-0 select-none">{t('startDate')}</label>
                <button
                  type="submit"
                  disabled={!formIsValid || loading}
                  aria-disabled={!formIsValid || loading}
                  aria-busy={loading || undefined}
                  className="
                      w-full md:w-auto md:min-w-[220px] h-[42px]
                      px-4 rounded-lg font-medium whitespace-nowrap
                      bg-blue-600 text-white shadow-sm
                      hover:bg-blue-700
                      focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
                      disabled:opacity-50 disabled:cursor-not-allowed
                      flex items-center justify-center gap-2
                    ">
                  {loading ? (
                    <Loader2 className="animate-spin shrink-0" size={18} />
                  ) : (
                    <CalcIcon className="shrink-0" size={18} />
                  )}
                  <span>{loading ? t('calculating') : t('calculate')}</span>
                </button>
              </div>
            </div>

            {error && (
              <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-red-700">
                {error}
              </div>
            )}
          </form>
        </section>

        <section className="mt-6 space-y-6">
          <div className="bg-white rounded-2xl p-4 sm:p-6 shadow-sm border border-gray-100">
            <div className="flex items-center justify-between mb-4 sm:mb-6">
              <h2 className="text-lg sm:text-xl font-semibold text-gray-900">{t('results')}</h2>
              {editableResults && (
                <span className="text-sm text-gray-500">
                  {t('editableSchedule') ?? 'Редактируемый график'}
                </span>
              )}
            </div>

            {!editableResults ? (
              <div className="text-center py-12 text-gray-500">
                <CalcIcon size={48} className="mx-auto mb-4 text-gray-300" />
                <p>{t('enterLoanParameters')}</p>
              </div>
            ) : (
              <div className="space-y-6">
                <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                  <div className="md:col-span-2">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('client')}
                    </label>
                    <select
                      value={selectedClientId}
                      onChange={(e) => {
                        setSelectedClientId(e.target.value);
                        setSelectedCaseId('');
                        setSelectedAccount('');
                        setSelectedAccountDate('');
                      }}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                      <option value="">{t('selectClient')}</option>
                      {clients.map((c) => (
                        <option key={c.id} value={c.id}>
                          {c.name} {c.company ? `— ${c.company}` : ''}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="md:col-span-2">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('case')}
                    </label>
                    <select
                      value={selectedCaseId}
                      onChange={(e) => {
                        setSelectedCaseId(e.target.value);
                        setSelectedAccountDate('');
                      }}
                      disabled={!selectedClientId}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:bg-gray-100 disabled:text-gray-500">
                      <option value="">{t('selectCase')}</option>
                      {cases.map((c) => (
                        <option key={c.id} value={c.id}>
                          {c.title} — {new Date(c.createdAt).toLocaleDateString()}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="relative md:col-span-2">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('account') ?? 'Счёт'}
                    </label>

                    <input
                      ref={accountInputRef}
                      value={selectedAccount}
                      onChange={(e) => {
                        setSelectedAccount(e.target.value);
                        setAccountOpen(true);
                      }}
                      onFocus={() => setAccountOpen(true)}
                      onMouseDown={suppressBrowserAutocomplete}
                      onBlur={() => setTimeout(() => setAccountOpen(false), 150)}
                      placeholder={t('enterAccount') ?? 'Введите счёт'}
                      maxLength={120}
                      autoComplete="off"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />

                    {accountOpen &&
                      (accountOpts.length > 0 ||
                        (selectedAccount ?? '').toString().trim() !== '') && (
                        <div className="absolute z-20 mt-1 w-full max-h-56 overflow-auto rounded-lg border bg-white shadow">
                          {accountLoading && (
                            <div className="px-3 py-2 text-sm text-gray-500">
                              {t('loading') ?? 'Загрузка…'}
                            </div>
                          )}

                          {(() => {
                            const sel = (selectedAccount ?? '').toString();
                            const selTrim = sel.trim();
                            const selLower = selTrim.toLowerCase();
                            return selTrim !== '' &&
                              !accountOpts.some((x) => x.account.toLowerCase() === selLower) ? (
                              <button
                                type="button"
                                className="w-full text-left px-3 py-2 text-sm hover:bg-gray-100"
                                onMouseDown={(e) => e.preventDefault()}
                                onClick={() => setAccountOpen(false)}>
                                {(t('create') ?? 'Создать') + ` «${selTrim}»`}
                              </button>
                            ) : null;
                          })()}

                          {accountOpts.map((opt) => {
                            const key = `${opt.account}__${opt.accountDate ?? ''}`;
                            const human =
                              opt.account +
                              (opt.accountDate
                                ? ` (${new Date(opt.accountDate).toLocaleDateString()})`
                                : '');

                            return (
                              <button
                                key={key}
                                type="button"
                                className="w-full text-left px-3 py-2 text-sm hover:bg-gray-100"
                                onMouseDown={(e) => e.preventDefault()}
                                onClick={async () => {
                                  setSelectedAccount(opt.account);
                                  setAccountOpen(false);

                                  if (opt.accountDate) {
                                    setSelectedAccountDate(toDateInputValue(opt.accountDate));
                                    return;
                                  }

                                  try {
                                    const cid = selectedClientId
                                      ? parseInt(selectedClientId, 10)
                                      : undefined;
                                    const caseId = selectedCaseId
                                      ? parseInt(selectedCaseId, 10)
                                      : undefined;

                                    const resp = await apiService.getAccounts({
                                      clientId: cid,
                                      caseId,
                                      q: opt.account,
                                      take: 50,
                                      withDate: true as const,
                                      dedupe: false,
                                    } as Parameters<typeof apiService.getAccounts>[0]);

                                    const arr = Array.isArray(resp)
                                      ? (resp as AccountApiItem[])
                                      : [];
                                    const found = arr.find((x) =>
                                      isAccountWithDate(x, opt.account),
                                    );
                                    if (found) {
                                      setSelectedAccountDate(toDateInputValue(found.accountDate));
                                    }
                                  } catch {
                                    /*  */
                                  }
                                }}>
                                {human}
                              </button>
                            );
                          })}
                        </div>
                      )}
                  </div>

                  <div className="md:col-span-2">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {t('accountDate') ?? 'Дата счёта'}
                    </label>
                    <input
                      type="date"
                      value={selectedAccountDate}
                      onChange={(e) => setSelectedAccountDate(e.target.value)}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-center"
                    />
                  </div>

                  <div className="md:col-span-4 flex items-end">
                    <button
                      onClick={handleSavePayments}
                      disabled={!selectedClientId || savingPayments}
                      className="w-full flex items-center justify-center gap-2 bg-emerald-600 text-white px-4 py-2.5 rounded-lg hover:bg-emerald-700 transition-colors disabled:opacity-50">
                      {savingPayments ? (
                        <Loader2 className="animate-spin" size={16} />
                      ) : (
                        <Save size={16} />
                      )}
                      {savingPayments ? t('saving') : t('saveToCalendar')}
                    </button>
                  </div>
                </div>

                <div>
                  <h3 className="text-lg font-semibold text-gray-900 mb-3 sm:mb-4">
                    {t('paymentSchedule')}
                  </h3>

                  <div className="overflow-x-auto border border-gray-200 rounded-xl">
                    <table className="min-w-[760px] w-full text-sm">
                      <colgroup>
                        <col style={{ width: 56 }} />
                        <col style={{ width: 160 }} />
                        <col />
                        <col />
                        <col />
                        <col />
                      </colgroup>

                      <thead className="bg-gray-50 sticky top-0 z-10">
                        <tr>
                          <th className="px-2 py-2 text-center text-gray-600 font-medium">#</th>
                          <th className="px-2 py-2 text-center text-gray-600 font-medium">
                            {t('date')}
                          </th>
                          <th className="px-3 py-2 text-center text-gray-600 font-medium">
                            {t('payment')}
                          </th>
                          <th className="px-3 py-2 text-center text-gray-600 font-medium">
                            {t('principal')}
                          </th>
                          <th className="px-3 py-2 text-center text-gray-600 font-medium">
                            {t('interest')}
                          </th>
                          <th className="px-3 py-2 text-center text-gray-600 font-medium">
                            {t('balance')}
                          </th>
                        </tr>
                      </thead>

                      <tbody className="divide-y divide-gray-100">
                        {editableResults.items.map((item, index) => (
                          <tr key={index} className={index % 2 ? 'bg-white' : 'bg-gray-50'}>
                            <td className="px-2 py-2 text-center text-gray-900">{index + 1}</td>

                            <td className="px-2 py-2 text-center">
                              <input
                                type="date"
                                value={toDateInputValue(item.date as unknown as string)}
                                onChange={(e) => handleResultChange(index, 'date', e.target.value)}
                                className="w-full text-xs border border-gray-200 rounded px-2 py-1 focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-center"
                              />
                            </td>

                            <td className="px-3 py-2 text-right">
                              <input
                                type="number"
                                inputMode="decimal"
                                value={item.payment}
                                onChange={(e) =>
                                  handleResultChange(index, 'payment', e.target.value)
                                }
                                className="w-full min-w-[110px] text-xs border border-gray-200 rounded px-2 py-1 text-right focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                step="0.01"
                              />
                            </td>
                            <td className="px-3 py-2 text-right">
                              <input
                                type="number"
                                inputMode="decimal"
                                value={item.principal}
                                onChange={(e) =>
                                  handleResultChange(index, 'principal', e.target.value)
                                }
                                className="w-full min-w-[110px] text-xs border border-gray-200 rounded px-2 py-1 text-right text-emerald-700 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                                step="0.01"
                              />
                            </td>
                            <td className="px-3 py-2 text-right">
                              <input
                                type="number"
                                inputMode="decimal"
                                value={item.interest}
                                onChange={(e) =>
                                  handleResultChange(index, 'interest', e.target.value)
                                }
                                className="w-full min-w-[110px] text-xs border border-gray-200 rounded px-2 py-1 text-right text-red-600 focus:ring-2 focus:ring-red-500 focus:border-red-500"
                                step="0.01"
                              />
                            </td>
                            <td className="px-3 py-2 text-right">
                              <input
                                type="number"
                                inputMode="decimal"
                                value={item.balance}
                                onChange={(e) =>
                                  handleResultChange(index, 'balance', e.target.value)
                                }
                                className="w-full min-w-[110px] text-xs border border-gray-200 rounded px-2 py-1 text-right focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                step="0.01"
                              />
                            </td>
                          </tr>
                        ))}
                      </tbody>

                      <tfoot>
                        <tr className="bg-gray-100 border-t border-gray-200">
                          <td className="px-2 py-2 text-gray-600 text-center" colSpan={2}>
                            {t('totals')}
                          </td>
                          <td className="px-3 py-2 text-right font-semibold">
                            {formatCurrency(
                              editableResults.items.reduce(
                                (s, it) => s + (Number(it.payment) || 0),
                                0,
                              ),
                            )}
                          </td>
                          <td className="px-3 py-2 text-right font-semibold text-emerald-700">
                            {formatCurrency(
                              editableResults.items.reduce(
                                (s, it) => s + (Number(it.principal) || 0),
                                0,
                              ),
                            )}
                          </td>
                          <td className="px-3 py-2 text-right font-semibold text-red-700">
                            {formatCurrency(
                              editableResults.items.reduce(
                                (s, it) => s + (Number(it.interest) || 0),
                                0,
                              ),
                            )}
                          </td>
                          <td className="px-3 py-2" />
                        </tr>
                      </tfoot>
                    </table>
                  </div>
                </div>
              </div>
            )}
          </div>
        </section>
      </main>
    </div>
  );
}
