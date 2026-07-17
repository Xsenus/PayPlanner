import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientRole, SummaryStats, PeriodKey, SummaryStatus } from '../types';

export function useSummaryStats(params: {
  clientId?: number;
  caseId?: number;
  from?: string;
  to?: string;
  period?: PeriodKey;
  type?: 'Income' | 'Expense';
  status?: SummaryStatus;
  clientRole?: ClientRole;
  q?: string;
  reloadToken?: number;
}) {
  const { clientId, caseId, from, to, period, type, status, clientRole, q, reloadToken } = params;

  const [data, setData] = useState<SummaryStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const fetch = useCallback(async () => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
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
        clientRole,
        q,
        r: typeof reloadToken === 'number' ? reloadToken : undefined, // ← cache-buster
      }, { signal: controller.signal });
      if (controller.signal.aborted) return;
      setData(res);
    } catch (e: unknown) {
      if (controller.signal.aborted) return;
      setError(e instanceof Error ? e.message : 'Failed to fetch summary');
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      if (!controller.signal.aborted) {
        setLoading(false);
      }
    }
  }, [clientId, caseId, from, to, period, type, status, clientRole, q, reloadToken]);

  useEffect(() => {
    void fetch();
    return () => {
      abortRef.current?.abort();
    };
  }, [fetch]);

  return { data, loading, error, refresh: fetch };
}
