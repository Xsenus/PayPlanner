import { useEffect, useState } from 'react';

import { apiService } from '../../services/api';
import type { CourtRecoveryOverview } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';

interface CourtRecoveryPanelProps {
  caseId: number;
  canEdit: boolean;
}

const money = (amount: number | null) => amount == null ? '—' : formatCurrencySmart(amount).full;

export function CourtRecoveryPanel({ caseId, canEdit }: CourtRecoveryPanelProps) {
  const [overview, setOverview] = useState<CourtRecoveryOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [selectedPaymentId, setSelectedPaymentId] = useState('');
  const [amount, setAmount] = useState('');

  useEffect(() => {
    let active = true;
    setLoading(true);
    setOverview(null);
    setError(null);
    setSelectedPaymentId('');
    setAmount('');
    void apiService.getCourtRecoveries(caseId).then((result) => {
      if (active) setOverview(result);
    }).catch((loadError: unknown) => {
      if (active) setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить взыскания по делу.'));
    }).finally(() => {
      if (active) setLoading(false);
    });
    return () => { active = false; };
  }, [caseId]);

  const reload = async (query = search) => {
    const result = await apiService.getCourtRecoveries(caseId, query);
    setOverview(result);
    setSelectedPaymentId('');
    setAmount('');
  };

  const runSearch = async () => {
    setLoading(true);
    setError(null);
    try {
      await reload(search);
    } catch (searchError) {
      setError(extractDisplayErrorMessage(searchError, 'Не удалось найти платежи.'));
    } finally {
      setLoading(false);
    }
  };

  const choosePayment = (id: string) => {
    setSelectedPaymentId(id);
    const payment = [...(overview?.linkedPayments ?? []), ...(overview?.availablePayments ?? [])]
      .find((item) => item.paymentId === Number(id));
    setAmount(payment ? String(payment.allocatedAmount ?? payment.paidAmount) : '');
  };

  const save = async () => {
    const paymentId = Number(selectedPaymentId);
    const parsedAmount = Number(amount.replace(',', '.'));
    if (!paymentId || !/^\d+(?:[.,]\d{1,2})?$/.test(amount.trim()) ||
        !Number.isFinite(parsedAmount) || parsedAmount <= 0) {
      setError('Выберите платёж и укажите положительную сумму с точностью до копеек.');
      return;
    }
    const selectedPayment = [...(overview?.linkedPayments ?? []), ...(overview?.availablePayments ?? [])]
      .find((item) => item.paymentId === paymentId);
    if (!selectedPayment || parsedAmount > selectedPayment.paidAmount) {
      setError('Сумма взыскания не может превышать оплаченную часть выбранного платежа.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await apiService.setCourtRecoveryPayment(caseId, paymentId, parsedAmount);
      await reload();
    } catch (saveError) {
      setError(extractDisplayErrorMessage(saveError, 'Не удалось сохранить связь с платежом.'));
    } finally {
      setSaving(false);
    }
  };

  const remove = async (paymentId: number) => {
    setSaving(true);
    setError(null);
    try {
      await apiService.removeCourtRecoveryPayment(caseId, paymentId);
      await reload();
    } catch (removeError) {
      setError(extractDisplayErrorMessage(removeError, 'Не удалось убрать связь с платежом.'));
    } finally {
      setSaving(false);
    }
  };

  return <section className="rounded-xl border border-blue-200 bg-blue-50/30 p-3 text-sm text-slate-700">
    <h3 className="font-semibold text-slate-900">Фактически получено по решению суда</h3>
    <p className="mt-1 text-xs text-slate-500">Связывайте только подтверждённую часть уже оплаченного платежа. Новый платёж не создаётся, выручка и маржа не пересчитываются.</p>
    {loading ? <p className="mt-2 text-xs">Загружаем платежи…</p> : null}
    {overview ? <>
      <div className="mt-2 flex flex-wrap gap-3 text-xs">
        <span>Присуждено: <strong>{money(overview.awardedAmount)}</strong></span>
        <span>Подтверждено платежами: <strong>{money(overview.receivedAmount)}</strong></span>
        <span>Осталось: <strong>{money(overview.remainingAmount)}</strong></span>
      </div>
      {!overview.hasCompletePaymentVisibility ? <p className="mt-2 text-xs text-amber-700">Показаны только доступные вам платежи. Общая сумма поступлений и остаток скрыты, чтобы не показывать неполный итог.</p> : null}
      {overview.awardedAmount == null ? <p className="mt-2 text-amber-700">Сначала сохраните присуждённую сумму в карточке дела.</p> : null}
      {overview.linkedPayments.length > 0 ? <ul className="mt-3 space-y-2">{overview.linkedPayments.map((payment) =>
        <li key={payment.paymentId} className="rounded-lg border border-slate-200 bg-white p-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span><strong>Платёж #{payment.paymentId}</strong> · {payment.description || 'Без описания'} · {money(payment.effectiveAmount)}</span>
            {canEdit ? <button type="button" onClick={() => void remove(payment.paymentId)} disabled={saving} className="text-xs font-medium text-rose-700 disabled:opacity-50">Убрать связь</button> : null}
          </div>
          {payment.warning ? <p role="alert" className="mt-1 text-xs text-rose-700">{payment.warning}</p> : null}
        </li>)}
      </ul> : <p className="mt-2 text-xs text-slate-500">Платежи пока не отмечены как взыскание.</p>}
      {canEdit && overview.awardedAmount != null && overview.awardedAmount > 0 ? <div className="mt-3 space-y-2 border-t border-blue-100 pt-3">
        <div className="flex flex-wrap gap-2">
          <input aria-label="Поиск платежа для взыскания" value={search} onChange={(event) => setSearch(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); void runSearch(); } }} placeholder="Поиск по описанию или номеру" className="min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-2 py-1.5" />
          <button type="button" onClick={() => void runSearch()} disabled={saving || loading} className="rounded-lg border border-blue-200 bg-white px-2 py-1.5 font-medium text-blue-700">Найти</button>
        </div>
        {overview.hasMoreAvailablePayments ? <p className="text-xs text-slate-500">Показаны последние 100 платежей; найдите более старый через поиск.</p> : null}
        <div className="flex flex-wrap gap-2">
          <select aria-label="Платёж для взыскания" value={selectedPaymentId} onChange={(event) => choosePayment(event.target.value)} disabled={saving} className="min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-2 py-1.5">
            <option value="">Выберите оплаченный приходный платёж</option>
            {[...overview.linkedPayments, ...overview.availablePayments].map((payment) => <option key={payment.paymentId} value={payment.paymentId}>#{payment.paymentId} · {payment.description || 'Без описания'} · оплачено {money(payment.paidAmount)}</option>)}
          </select>
          <input aria-label="Сумма платежа в счёт взыскания" value={amount} onChange={(event) => setAmount(event.target.value)} inputMode="decimal" placeholder="Сумма, ₽" disabled={saving} className="w-36 rounded-lg border border-slate-200 bg-white px-2 py-1.5" />
          <button type="button" onClick={() => void save()} disabled={saving || !selectedPaymentId} className="rounded-lg bg-blue-700 px-3 py-1.5 font-medium text-white disabled:opacity-50">Сохранить связь</button>
        </div>
      </div> : null}
    </> : null}
    {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error}</p> : null}
  </section>;
}
