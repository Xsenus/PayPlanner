import { useState, useEffect, useCallback, useRef } from 'react';
import { apiService } from '../services/api';
import type { Payment } from '../types';

type CreatePaymentDTO = Omit<Payment, 'id' | 'createdAt'>;
type UpdatePaymentDTO = { id: number } & Omit<Payment, 'id' | 'createdAt'>;

interface Options {
  pollInterval?: number;
  clientId?: number;
  caseId?: number;
}

export function usePayments(from?: string, to?: string, opts?: Options) {
  const [payments, setPayments] = useState<Payment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<number | null>(null);

  const fetchPayments = useCallback(
    async (silent = false) => {
      try {
        if (!silent) setLoading(true);
        setError(null);

        const data = await apiService.getPayments({
          from,
          to,
          clientId: opts?.clientId,
          caseId: opts?.caseId,
        });

        setPayments(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to fetch payments');
      } finally {
        if (!silent) setLoading(false);
      }
    },
    [from, to, opts?.clientId, opts?.caseId],
  );

  useEffect(() => {
    fetchPayments(false);
  }, [fetchPayments]);

  useEffect(() => {
    if (!opts?.pollInterval) return;
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = window.setInterval(() => {
      void fetchPayments(true);
    }, opts.pollInterval);
    return () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      timerRef.current = null;
    };
  }, [fetchPayments, opts?.pollInterval]);

  const createPayment = async (payment: CreatePaymentDTO) => {
    await apiService.createPayment(payment);
    await fetchPayments(true);
  };

  const updatePayment = async (paymentData: UpdatePaymentDTO) => {
    const { id, ...rest } = paymentData;
    await apiService.updatePayment(id, rest);
    await fetchPayments(true);
  };

  const deletePayment = async (id: number) => {
    await apiService.deletePayment(id);
    await fetchPayments(true);
  };

  return {
    payments,
    loading,
    error,
    createPayment,
    updatePayment,
    deletePayment,
    refresh: () => fetchPayments(false),
  };
}
