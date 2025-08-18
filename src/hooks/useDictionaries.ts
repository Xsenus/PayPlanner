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
      const [dealTypesData, incomeTypesData, paymentSourcesData, paymentStatusesData] =
        await Promise.all([
          apiService.getDealTypes(),
          apiService.getIncomeTypes(),
          apiService.getPaymentSources(),
          apiService.getPaymentStatuses(),
        ]);
      setDealTypes(dealTypesData);
      setIncomeTypes(incomeTypesData);
      setPaymentSources(paymentSourcesData);
      setPaymentStatuses(paymentStatusesData);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch dictionaries');
      setDealTypes([]);
      setIncomeTypes([]);
      setPaymentSources([]);
      setPaymentStatuses([]);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    let alive = true;
    (async () => {
      if (!alive) return;
      await refresh();
    })();
    return () => {
      alive = false;
    };
  }, [refresh]);

  useEffect(() => {
    const handler = () => refresh();
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
