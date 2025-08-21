import { useState, useEffect, useCallback } from 'react';
import { apiService } from '../services/api';
import type { DealType, IncomeType, PaymentSource, PaymentStatusEntity } from '../types';

const DICTS_CHANGED = 'dicts:changed';

export function useDictionaries() {
  const [dealTypes, setDealTypes] = useState<DealType[]>([]);
  const [incomeTypes, setIncomeTypes] = useState<IncomeType[]>([]);
  const [paymentSources, setPaymentSources] = useState<PaymentSource[]>([]);
  const [paymentStatuses, setPaymentStatuses] = useState<PaymentStatusEntity[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [deal, incIncome, incExpense, sources, statuses] = await Promise.all([
        apiService.getDict('deal-types'),
        apiService.getIncomeTypes('Income'),
        apiService.getIncomeTypes('Expense'),
        apiService.getDict('payment-sources'),
        apiService.getDict('payment-statuses'),
      ]);

      setDealTypes(deal ?? []);

      const merged = [...(incIncome ?? []), ...(incExpense ?? [])];
      const byId = new Map<number, IncomeType>();
      for (const it of merged) byId.set(it.id, it);
      setIncomeTypes(Array.from(byId.values()));

      setPaymentSources(sources ?? []);
      setPaymentStatuses(statuses ?? []);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load dictionaries');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handler = () => void refresh();
    window.addEventListener(DICTS_CHANGED, handler);
    return () => window.removeEventListener(DICTS_CHANGED, handler);
  }, [refresh]);

  return {
    dealTypes,
    incomeTypes,
    paymentSources,
    paymentStatuses,
    loading,
    error,
    refresh,
  };
}
