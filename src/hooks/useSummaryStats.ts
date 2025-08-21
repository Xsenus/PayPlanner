import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { SummaryStats, PeriodKey, SummaryStatus } from '../types';

export function useSummaryStats(params: {
  clientId?: number;
  caseId?: number;
  from?: string;
  to?: string;
  period?: PeriodKey;
  type?: 'Income' | 'Expense';
  status?: SummaryStatus;
  q?: string;
  reloadToken?: number;
}) {
  const { clientId, caseId, from, to, period, type, status, q, reloadToken } = params;

  const [data, setData] = useState<SummaryStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetch = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await apiService.getSummaryStats({
        clientId,
        caseId,
        from,
        to,
        period,
        type,
        status,
        q,
      });
      setData(res);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to fetch summary');
    } finally {
      setLoading(false);
    }
  }, [clientId, caseId, from, to, period, type, status, q]);

  useEffect(() => {
    void fetch();
  }, [fetch, reloadToken]);

  return { data, loading, error, refresh: fetch };
}
