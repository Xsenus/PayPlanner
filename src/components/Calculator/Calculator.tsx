import React, { useEffect, useMemo, useState } from 'react';
import { Calculator as CalcIcon, Save, Loader2, Info, AlertTriangle } from 'lucide-react';
import { apiService } from '../../services/api';
import { useClients } from '../../hooks/useClients';
import { useTranslation } from '../../hooks/useTranslation';
import type { InstallmentRequest, InstallmentResponse, ClientCase } from '../../types';
import { toDateInputValue } from '../../utils/dateUtils';

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

  const mockResult: InstallmentResponse = {
    overpay: 11686,
    toPay: 111686,
    items: [
      {
        date: '2025-09-16',
        principal: 1161.42,
        interest: 366.67,
        payment: 1528.09,
        balance: 88500,
      },
      {
        date: '2025-10-16',
        principal: 1166.75,
        interest: 361.34,
        payment: 1528.09,
        balance: 87333.25,
      },
      {
        date: '2025-11-16',
        principal: 1172.09,
        interest: 356.0,
        payment: 1528.09,
        balance: 86161.16,
      },
      {
        date: '2025-12-16',
        principal: 1177.47,
        interest: 350.62,
        payment: 1528.09,
        balance: 84983.69,
      },
    ],
  };

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
      setEditableResults(JSON.parse(JSON.stringify(mockResult)) as InstallmentResponse);
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
        await apiService.createPayment({
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
        });
      }
      alert(t('paymentsSaved'));
    } catch (err) {
      console.error('Failed to save payments:', err);
      alert(t('saveError') ?? 'Ошибка сохранения платежей');
    } finally {
      setSavingPayments(false);
    }
  };

  useEffect(() => {
    let ignore = false;
    setCases([]);
    setSelectedCaseId('');
    if (!selectedClientId) return;

    (async () => {
      try {
        const rows = await apiService.getCases?.(Number(selectedClientId));
        if (!ignore && rows) setCases(rows);
      } catch (e) {
        console.warn('getCases failed', e);
      }
    })();

    return () => {
      ignore = true;
    };
  }, [selectedClientId]);

  const monthlyPayment = useMemo(
    () =>
      editableResults?.items?.[0]?.payment
        ? formatCurrency(editableResults.items[0].payment)
        : '₽0',
    [editableResults, formatCurrency],
  );

  const formIsValid =
    formData.total > 0 &&
    formData.months >= 1 &&
    formData.annualRate >= 0 &&
    formData.downPayment >= 0 &&
    formData.downPayment <= formData.total;

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white border-b border-gray-100">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 py-4">
          <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 flex items-center gap-3">
            <CalcIcon size={32} className="text-blue-600" />
            {t('installmentCalculator')}
          </h1>
          <p className="text-gray-600 mt-1">{t('calculateLoanPayments')}</p>
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

              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('startDate')}
                </label>
                <input
                  type="date"
                  name="startDate"
                  value={toDateInputValue(formData.startDate as unknown as string)}
                  onChange={handleChange}
                  required
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                />
                <p id="calc-help" className="mt-2 text-xs text-gray-500 flex items-center gap-1">
                  <Info size={14} /> {t('calcHint') ?? 'Параметры можно изменить в любой момент.'}
                </p>
              </div>
            </div>

            {error && (
              <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-red-700">
                {error}
              </div>
            )}

            <div className="sticky bottom-3 md:static">
              <button
                type="submit"
                disabled={!formIsValid || loading}
                className="w-full md:w-auto md:min-w-[220px] bg-blue-600 text-white py-2.5 px-4 rounded-lg font-medium hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center justify-center gap-2 shadow-sm"
                aria-disabled={!formIsValid || loading}>
                {loading ? <Loader2 className="animate-spin" size={18} /> : <CalcIcon size={18} />}
                {loading ? t('calculating') : t('calculate')}
              </button>
            </div>
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
                <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-center">
                  <div className="p-4 bg-blue-50 rounded-xl border border-blue-100">
                    <p className="text-sm text-gray-600 mb-1">{t('totalPayable')}</p>
                    <p className="text-xl font-bold text-blue-700">
                      {formatCurrency(editableResults.toPay)}
                    </p>
                  </div>
                  <div className="p-4 bg-red-50 rounded-xl border border-red-100">
                    <p className="text-sm text-gray-600 mb-1">{t('totalInterest')}</p>
                    <p className="text-xl font-bold text-red-700">
                      {formatCurrency(editableResults.overpay)}
                    </p>
                  </div>
                  <div className="p-4 bg-emerald-50 rounded-xl border border-emerald-100">
                    <p className="text-sm text-gray-600 mb-1">{t('monthlyPayment')}</p>
                    <p className="text-xl font-bold text-emerald-700">{monthlyPayment}</p>
                  </div>
                </div>

                <div className="p-4 rounded-xl bg-gray-50 border border-gray-200">
                  <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('client')}
                      </label>
                      <select
                        value={selectedClientId}
                        onChange={(e) => setSelectedClientId(e.target.value)}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                        <option value="">{t('selectClient')}</option>
                        {clients.map((c) => (
                          <option key={c.id} value={c.id}>
                            {c.name} {c.company ? `— ${c.company}` : ''}
                          </option>
                        ))}
                      </select>
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-2">
                        {t('case')}
                      </label>
                      <select
                        value={selectedCaseId}
                        onChange={(e) => setSelectedCaseId(e.target.value)}
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

                    <div className="flex items-end">
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
                </div>

                <div>
                  <h3 className="text-lg font-semibold text-gray-900 mb-3 sm:mb-4">
                    {t('paymentSchedule')}
                  </h3>

                  <div className="overflow-x-auto border border-gray-200 rounded-xl">
                    <table className="min-w-[760px] w-full text-sm">
                      <thead className="bg-gray-50 sticky top-0 z-10">
                        <tr>
                          <th className="px-3 py-2 text-left text-gray-600 font-medium">#</th>
                          <th className="px-3 py-2 text-left text-gray-600 font-medium">
                            {t('date')}
                          </th>
                          <th className="px-3 py-2 text-right text-gray-600 font-medium">
                            {t('payment')}
                          </th>
                          <th className="px-3 py-2 text-right text-gray-600 font-medium">
                            {t('principal')}
                          </th>
                          <th className="px-3 py-2 text-right text-gray-600 font-medium">
                            {t('interest')}
                          </th>
                          <th className="px-3 py-2 text-right text-gray-600 font-medium">
                            {t('balance')}
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-100">
                        {editableResults.items.map((item, index) => (
                          <tr key={index} className={index % 2 ? 'bg-white' : 'bg-gray-50'}>
                            <td className="px-3 py-2 text-gray-900">{index + 1}</td>
                            <td className="px-3 py-2">
                              <input
                                type="date"
                                value={toDateInputValue(item.date as unknown as string)}
                                onChange={(e) => handleResultChange(index, 'date', e.target.value)}
                                className="w-full text-xs border border-gray-200 rounded px-2 py-1 focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
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
                                className="w-28 text-xs border border-gray-200 rounded px-2 py-1 text-right focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
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
                                className="w-28 text-xs border border-gray-200 rounded px-2 py-1 text-right text-emerald-700 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
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
                                className="w-28 text-xs border border-gray-200 rounded px-2 py-1 text-right text-red-600 focus:ring-2 focus:ring-red-500 focus:border-red-500"
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
                                className="w-28 text-xs border border-gray-200 rounded px-2 py-1 text-right focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                step="0.01"
                              />
                            </td>
                          </tr>
                        ))}
                      </tbody>
                      <tfoot>
                        <tr className="bg-gray-100 border-t border-gray-200">
                          <td className="px-3 py-2 text-gray-600" colSpan={2}>
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
