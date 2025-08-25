import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Payment } from '../types';

type UsePaymentsOptions = {
  pollInterval?: number;
  clientId?: number;
  caseId?: number;
};

type Mode = 'initial' | 'background';

function signature(arr: Payment[]): string {
  if (!arr?.length) return 'empty';
  return arr
    .map((p) => `${p.id}:${p.amount}:${p.status}:${p.type}:${p.date}`)
    .sort()
    .join('|');
}

export function usePayments(from?: string, to?: string, opts?: UsePaymentsOptions) {
  const [payments, setPayments] = useState<Payment[]>([]);
  const [loadingInitial, setLoadingInitial] = useState<boolean>(true);
  const [refreshing, setRefreshing] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);
  const timerRef = useRef<number | null>(null);
  const inflightRef = useRef(false);
  const hasLoadedRef = useRef(false);
  const sigRef = useRef<string>('init');

  const paramsKey = JSON.stringify({
    from: from ?? '',
    to: to ?? '',
    clientId: opts?.clientId ?? null,
    caseId: opts?.caseId ?? null,
  });

  const fetchOnce = useCallback(
    async (mode?: Mode) => {
      if (inflightRef.current) return;
      inflightRef.current = true;
      const effectiveMode: Mode = mode ?? (hasLoadedRef.current ? 'background' : 'initial');
      abortRef.current?.abort();
      const ac = new AbortController();
      abortRef.current = ac;

      setError(null);
      if (effectiveMode === 'initial') setLoadingInitial(true);
      else setRefreshing(true);

      try {
        const res = await apiService.getPayments({
          from,
          to,
          clientId: opts?.clientId,
          caseId: opts?.caseId,
        });

        if (!ac.signal.aborted) {
          const next = res ?? [];
          const nextSig = signature(next);
          if (sigRef.current !== nextSig) {
            sigRef.current = nextSig;
            setPayments(next);
          }
          hasLoadedRef.current = true;
        }
      } catch (e: unknown) {
        if (!ac.signal.aborted) {
          setError(e instanceof Error ? e.message : 'Failed to fetch payments');
        }
      } finally {
        if (!ac.signal.aborted) {
          if (effectiveMode === 'initial') setLoadingInitial(false);
          else setRefreshing(false);
        }
        inflightRef.current = false;
      }
    },
    [from, to, opts?.clientId, opts?.caseId],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    sigRef.current = 'init';

    abortRef.current?.abort();
    void fetchOnce('initial');

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
        void fetchOnce('background');
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
    await fetchOnce('background');
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

  const loading = loadingInitial && payments.length === 0;

  return {
    payments,
    loading,
    error,
    refresh,
    createPayment,
    updatePayment,
    deletePayment,
    refreshing,
  };
}
