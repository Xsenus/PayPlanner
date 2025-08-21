import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Payment } from '../types';

type UsePaymentsOptions = {
  pollInterval?: number;
  clientId?: number;
  caseId?: number;
};

export function usePayments(from?: string, to?: string, opts?: UsePaymentsOptions) {
  const [payments, setPayments] = useState<Payment[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);
  const timerRef = useRef<number | null>(null);
  const inflightRef = useRef(false);

  const paramsKey = JSON.stringify({
    from: from ?? '',
    to: to ?? '',
    clientId: opts?.clientId ?? null,
    caseId: opts?.caseId ?? null,
  });

  const fetchOnce = useCallback(async () => {
    if (inflightRef.current) return;
    inflightRef.current = true;

    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;

    setLoading(true);
    setError(null);
    try {
      const res = await apiService.getPayments({
        from,
        to,
        clientId: opts?.clientId,
        caseId: opts?.caseId,
      });
      if (!ac.signal.aborted) setPayments(res);
    } catch (e: unknown) {
      if (!ac.signal.aborted) {
        setError(e instanceof Error ? e.message : 'Failed to fetch payments');
      }
    } finally {
      if (!ac.signal.aborted) setLoading(false);
      inflightRef.current = false;
    }
  }, [from, to, opts?.clientId, opts?.caseId]);

  useEffect(() => {
    void fetchOnce();
    return () => {
      abortRef.current?.abort();
    };
  }, [fetchOnce, paramsKey]);

  useEffect(() => {
    if (timerRef.current) {
      clearInterval(timerRef.current);
      timerRef.current = null;
    }
    const iv = opts?.pollInterval ?? 0;
    if (iv > 0) {
      timerRef.current = window.setInterval(() => {
        void fetchOnce();
      }, iv);
    }
    return () => {
      if (timerRef.current) {
        clearInterval(timerRef.current);
        timerRef.current = null;
      }
    };
  }, [fetchOnce, opts?.pollInterval, paramsKey]);

  const refresh = useCallback(async () => {
    await fetchOnce();
  }, [fetchOnce]);

  const createPayment = useCallback(
    async (p: Omit<Payment, 'id' | 'createdAt'>) => apiService.createPayment(p),
    [],
  );
  const updatePayment = useCallback(
    async (payload: { id: number } & Omit<Payment, 'id' | 'createdAt'>) =>
      apiService.updatePayment(payload.id, payload),
    [],
  );
  const deletePayment = useCallback(async (id: number) => apiService.deletePayment(id), []);

  return { payments, loading, error, refresh, createPayment, updatePayment, deletePayment };
}
