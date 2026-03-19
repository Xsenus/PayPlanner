import React, { useEffect, useMemo, useState } from 'react';
import { Calculator as CalcIcon, Save, Loader2, Info, AlertTriangle, X } from 'lucide-react';
import { apiService } from '../../services/api';
import { useClients } from '../../hooks/useClients';
import { useAuth } from '../../contexts/AuthContext';
import { useTranslation } from '../../hooks/useTranslation';
import type { InstallmentRequest, InstallmentResponse, ClientCase } from '../../types';
import { toDateInputValue } from '../../utils/dateUtils';
import { consumeInstallmentDraft } from '../../utils/installmentBridge';
import { formatCurrencySmart } from '../../utils/formatters';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { TablePagination } from '../ui/TablePagination';

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
};

export function Calculator() {
  const { user } = useAuth();
  const { t, formatCurrency } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const { clients } = useClients();
  const schedulePageSizeStorageKey = `pp.calculator_schedule_page_size.${user?.id ?? 'guest'}.v1`;

  const [formData, setFormData] = useState<InstallmentRequest>({
    total: 100000,
    downPayment: 20000,
    annualRate: 5.5,
    months: 60,
    startDate: new Date().toISOString().split('T')[0],
    roundingMode: 'none',
    roundingStep: 100,
  });

  const [editableResults, setEditableResults] = useState<InstallmentResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [savingPayments, setSavingPayments] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [schedulePage, setSchedulePage] = useState(1);
  const [schedulePageSize, setSchedulePageSize] = useState<number>(() => {
    try {
      const raw = localStorage.getItem(schedulePageSizeStorageKey);
      const parsed = raw ? Number(raw) : NaN;
      return Number.isFinite(parsed) && parsed > 0 ? parsed : 24;
    } catch {
      return 24;
    }
  });

  const [prefillNotice, setPrefillNotice] = useState<string | null>(null);

  const [selectedClientId, setSelectedClientId] = useState<string>('');
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [selectedCaseId, setSelectedCaseId] = useState<string>('');

  const [selectedAccount, setSelectedAccount] = useState<string>('');
  const [selectedAccountDate, setSelectedAccountDate] = useState<string>('');

  const scheduleItems = editableResults?.items ?? [];
  const scheduleTotalPages = Math.max(1, Math.ceil(scheduleItems.length / schedulePageSize));
  const pagedScheduleItems = useMemo(() => {
    const start = (schedulePage - 1) * schedulePageSize;
    return scheduleItems.slice(start, start + schedulePageSize);
  }, [scheduleItems, schedulePage, schedulePageSize]);

  useEffect(() => {
    const draft = consumeInstallmentDraft();
    if (!draft) return;

    setFormData((prev) => {
      const next = { ...prev };
      if (typeof draft.total === 'number' && Number.isFinite(draft.total)) {
        next.total = draft.total;
      }
      if (draft.startDate) {
        next.startDate = draft.startDate;
      }
      if (draft.roundingMode === 'down' || draft.roundingMode === 'up' || draft.roundingMode === 'none') {
        next.roundingMode = draft.roundingMode;
      }
      if (typeof draft.roundingStep === 'number' && draft.roundingStep > 0) {
        next.roundingStep = draft.roundingStep;
      }
      return next;
    });

    if (draft.clientId) {
      setSelectedClientId(String(draft.clientId));
    }

    if (draft.clientName) {
      setPrefillNotice(
        `${t('calculatorPrefillFromInvoice') ?? 'Данные из счёта'}${
          draft.invoiceNumber ? ` №${draft.invoiceNumber}` : ''
        } — ${draft.clientName}`,
      );
    } else {
      setPrefillNotice(
        `${t('calculatorPrefillFromInvoice') ?? 'Данные из счёта'}${
          draft.invoiceNumber ? ` №${draft.invoiceNumber}` : ''
        }`,
      );
    }
  }, [t]);

  useEffect(() => {
    try {
      localStorage.setItem(schedulePageSizeStorageKey, String(schedulePageSize));
    } catch {
      /** */
    }
  }, [schedulePageSize, schedulePageSizeStorageKey]);

  useEffect(() => {
    setSchedulePage(1);
  }, [editableResults, schedulePageSize]);

  useEffect(() => {
    if (schedulePage > scheduleTotalPages) {
      setSchedulePage(scheduleTotalPages);
    }
  }, [schedulePage, scheduleTotalPages]);

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

  const handleNumber = (v: string) => (v === '' ? '' : Number.isNaN(Number(v)) ? 0 : parseFloat(v));

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const { name, value } = e.target;
    setFormData((prev) => {
      if (name === 'startDate') {
        return { ...prev, startDate: value };
      }

      if (name === 'roundingMode') {
        return { ...prev, roundingMode: value as InstallmentRequest['roundingMode'] };
      }

      if (name === 'roundingStep') {
        const trimmed = value.trim();
        if (!trimmed) {
          return { ...prev, roundingStep: null };
        }
        const parsed = Number.parseFloat(trimmed);
        return {
          ...prev,
          roundingStep: Number.isNaN(parsed) ? prev.roundingStep ?? null : parsed,
        };
      }

      return {
        ...prev,
        [name]: handleNumber(value) as number,
      };
    });
  };

  const handleCalculate = async (e?: React.FormEvent) => {
    e?.preventDefault?.();
    setLoading(true);
    setError(null);
    try {
      const roundingMode = formData.roundingMode ?? 'none';
      const roundingStepValue =
        typeof formData.roundingStep === 'number' && Number.isFinite(formData.roundingStep)
          ? formData.roundingStep
          : null;

      const payload: InstallmentRequest = {
        total: Number(formData.total) || 0,
        downPayment: Number(formData.downPayment) || 0,
        annualRate: Number(formData.annualRate) || 0,
        months: Number(formData.months) || 0,
        startDate: formData.startDate,
        roundingMode,
        roundingStep:
          roundingMode === 'none' || !roundingStepValue || roundingStepValue <= 0
            ? undefined
            : roundingStepValue,
      };

      const resp = await apiService.calculateInstallment(payload);
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
    const confirmed = await confirm({ title: 'Подтверждение', description: t('confirmSaveAllPayments') ?? 'Сохранить все платежи?', confirmLabel: t('confirm') ?? 'Подтвердить', cancelLabel: t('cancel') ?? 'Отмена' });
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
        };

        await apiService.createPayment(
          payload as unknown as Parameters<typeof apiService.createPayment>[0],
        );
      }
      toast.success(t('paymentsSaved') ?? 'Платежи сохранены');
    } catch (err) {
      console.error('Failed to save payments:', err);
      toast.error(t('saveError') ?? 'Ошибка сохранения платежей');
    } finally {
      setSavingPayments(false);
    }
  };

  const roundingMode = formData.roundingMode ?? 'none';
  const roundingStepValue =
    typeof formData.roundingStep === 'number' && Number.isFinite(formData.roundingStep)
      ? formData.roundingStep
      : 0;
  const roundingEnabled = roundingMode !== 'none' && roundingStepValue > 0;

  const formIsValid =
    formData.total > 0 &&
    formData.months >= 1 &&
    formData.annualRate >= 0 &&
    formData.downPayment >= 0 &&
    formData.downPayment <= formData.total &&
    (!roundingEnabled || roundingStepValue > 0);

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white border-b border-gray-100">
        <div className="max-w-[calc(100vw-2rem)] mx-auto px-4 sm:px-6 py-4 text-center md:text-left">
          <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 flex items-center gap-3 justify-center md:justify-start">
            <CalcIcon size={32} className="text-blue-600" />
            {t('installmentCalculator')}
          </h1>
          <p className="text-gray-600 mt-2 text-center md:text-left">
            {t('calculateLoanPayments')}
          </p>
        </div>
      </header>

      <main className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        {prefillNotice && (
          <div className="mb-4 sm:mb-5">
            <div className="flex items-start gap-3 rounded-xl border border-blue-200 bg-blue-50 px-3 py-2 text-blue-800">
              <Info className="mt-0.5 h-4 w-4 shrink-0" />
              <div className="flex-1 text-sm leading-snug">{prefillNotice}</div>
              <button
                type="button"
                onClick={() => setPrefillNotice(null)}
                className="rounded-full p-1 text-blue-700 transition-colors hover:bg-blue-100"
                aria-label={t('close') ?? 'Закрыть'}
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </div>
        )}

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
                  {t('roundingModeLabel') ?? 'Округление платежей'}
                </label>
                <select
                  name="roundingMode"
                  value={roundingMode}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                >
                  <option value="none">{t('roundingNone') ?? 'Без округления'}</option>
                  <option value="down">{t('roundingDown') ?? 'В меньшую сторону'}</option>
                  <option value="up">{t('roundingUp') ?? 'В большую сторону'}</option>
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('roundingStepLabel') ?? 'Шаг округления (₽)'}
                </label>
                <input
                  type="number"
                  name="roundingStep"
                  value={formData.roundingStep ?? ''}
                  onChange={handleChange}
                  min={0}
                  step="1"
                  disabled={roundingMode === 'none'}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:bg-gray-100 disabled:text-gray-500"
                  aria-label={t('roundingStepLabel') ?? 'Шаг округления'}
                />
                <p className="mt-1 text-xs text-gray-500">{t('roundingHint') ?? 'Например, 100 — округление до сотен.'}</p>
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
                {(() => {
                  const loanAmount =
                    editableResults.loanAmount ??
                    Math.max(0, Number(formData.total) - Number(formData.downPayment));
                  const baseMonthlyPayment =
                    editableResults.baseMonthlyPayment ??
                    (editableResults.items.length > 0 ? editableResults.items[0].payment : 0);
                  const roundedMonthlyPayment = editableResults.roundedMonthlyPayment ?? null;
                  const appliedRoundingMode =
                    (editableResults.roundingMode ?? roundingMode) as 'none' | 'down' | 'up';
                  const appliedRoundingStep =
                    typeof editableResults.roundingStep === 'number' && editableResults.roundingStep > 0
                      ? editableResults.roundingStep
                      : roundingEnabled
                      ? roundingStepValue
                      : null;

                  const roundingLabels: Record<'none' | 'down' | 'up', string> = {
                    none: t('roundingNone') ?? 'Без округления',
                    down: t('roundingDown') ?? 'В меньшую сторону',
                    up: t('roundingUp') ?? 'В большую сторону',
                  };

                  return (
                    <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
                      <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
                        <div className="text-sm text-gray-500">{t('loanPrincipal') ?? 'Сумма после аванса'}</div>
                        <div className="mt-1 text-lg font-semibold text-gray-900">
                          {formatCurrencySmart(loanAmount, { alwaysCents: true }).full}
                        </div>
                      </div>
                      <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
                        <div className="text-sm text-gray-500">{t('totalPayable') ?? 'К доплате'}</div>
                        <div className="mt-1 text-lg font-semibold text-gray-900">
                          {formatCurrencySmart(editableResults.toPay, { alwaysCents: true }).full}
                        </div>
                      </div>
                      <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
                        <div className="text-sm text-gray-500">{t('totalInterest') ?? 'Общий процент'}</div>
                        <div className="mt-1 text-lg font-semibold text-gray-900">
                          {formatCurrencySmart(editableResults.overpay, { alwaysCents: true }).full}
                        </div>
                      </div>
                      <div className="rounded-xl border border-gray-200 bg-gray-50 p-4">
                        <div className="text-sm text-gray-500">{t('monthlyPayment') ?? 'Ежемесячный платёж'}</div>
                        <div className="mt-1 text-lg font-semibold text-gray-900">
                          {formatCurrencySmart(baseMonthlyPayment, { alwaysCents: true }).full}
                        </div>
                        {roundedMonthlyPayment !== null && appliedRoundingMode !== 'none' && (
                          <div className="mt-2 text-xs text-gray-600">
                            <span className="font-medium text-gray-700">{t('roundedPaymentLabel') ?? 'С округлением'}:</span>{' '}
                            <span className="font-semibold text-blue-600">
                              {formatCurrencySmart(roundedMonthlyPayment, { alwaysCents: true }).full}
                            </span>
                            {appliedRoundingStep ? (
                              <span className="text-gray-500">
                                {' '}
                                {`(${roundingLabels[appliedRoundingMode]} · ${t('roundingStepShort') ?? 'шаг'} ${formatCurrencySmart(appliedRoundingStep, { alwaysCents: true }).full})`}
                              </span>
                            ) : (
                              <span className="text-gray-500"> {roundingLabels[appliedRoundingMode]}</span>
                            )}
                          </div>
                        )}
                      </div>
                    </div>
                  );
                })()}

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
                      {(t('account') ?? 'Счёт') + ' (legacy, только чтение)'}
                    </label>
                    <p className="mb-2 text-xs text-gray-500">
                      Поле больше не редактируется и не участвует в сохранении новых платежей.
                    </p>

                    <input
                      value={selectedAccount}
                      readOnly
                      tabIndex={-1}
                      placeholder={t('enterAccount') ?? 'Введите счёт'}
                      maxLength={120}
                      className="w-full cursor-not-allowed rounded-lg border border-gray-300 bg-gray-50 px-3 py-2 text-gray-600"
                    />
                  </div>

                  <div className="md:col-span-2">
                    <label className="block text-sm font-medium text-gray-700 mb-2">
                      {(t('accountDate') ?? 'Дата счёта') + ' (legacy, только чтение)'}
                    </label>
                    <input
                      type="date"
                      value={selectedAccountDate}
                      readOnly
                      disabled
                      className="w-full cursor-not-allowed rounded-lg border border-gray-300 bg-gray-100 px-3 py-2 text-center text-gray-600"
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
                  <div className="mb-3 flex flex-col gap-2 sm:mb-4 sm:flex-row sm:items-center sm:justify-between">
                    <h3 className="text-lg font-semibold text-gray-900">
                      {t('paymentSchedule')}
                    </h3>
                    {scheduleItems.length > 0 ? (
                      <div className="text-sm text-gray-500">
                        {(t('recordsFound') ?? 'Найдено записей:')} {scheduleItems.length}
                      </div>
                    ) : null}
                  </div>

                  <div className="overflow-x-auto border border-gray-200 rounded-xl">
                    <table className="min-w-[760px] w-full table-fixed text-sm">
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
                        {pagedScheduleItems.map((item, index) => {
                          const absoluteIndex = (schedulePage - 1) * schedulePageSize + index;
                          return (
                          <tr key={absoluteIndex} className={absoluteIndex % 2 ? 'bg-white' : 'bg-gray-50'}>
                            <td className="px-2 py-2 text-center text-gray-900">{absoluteIndex + 1}</td>

                            <td className="px-2 py-2 text-center">
                              <input
                                type="date"
                                value={toDateInputValue(item.date as unknown as string)}
                                onChange={(e) => handleResultChange(absoluteIndex, 'date', e.target.value)}
                                className="w-full text-xs border border-gray-200 rounded px-2 py-1 focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-center"
                              />
                            </td>

                            <td className="px-3 py-2 text-right">
                              <input
                                type="number"
                                inputMode="decimal"
                                value={item.payment}
                                onChange={(e) =>
                                  handleResultChange(absoluteIndex, 'payment', e.target.value)
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
                                  handleResultChange(absoluteIndex, 'principal', e.target.value)
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
                                  handleResultChange(absoluteIndex, 'interest', e.target.value)
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
                                  handleResultChange(absoluteIndex, 'balance', e.target.value)
                                }
                                className="w-full min-w-[110px] text-xs border border-gray-200 rounded px-2 py-1 text-right focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                step="0.01"
                              />
                            </td>
                          </tr>
                        )})}
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
                  {scheduleItems.length > 0 ? (
                    <TablePagination
                      page={schedulePage}
                      totalPages={scheduleTotalPages}
                      totalItems={scheduleItems.length}
                      pageSize={schedulePageSize}
                      pageSizeOptions={CALCULATOR_PAGE_SIZE_OPTIONS}
                      onPageChange={setSchedulePage}
                      onPageSizeChange={setSchedulePageSize}
                      pageSizeLabel="Строк на странице"
                    />
                  ) : null}
                </div>
              </div>
            )}
          </div>
        </section>
      </main>
    </div>
  );
}
