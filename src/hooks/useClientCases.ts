import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientCase } from '../types';

type CaseCreateUpdate = Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;

export function useClientCases(clientId?: number | string) {
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const cid = clientId != null ? Number(clientId) : undefined;

  const refresh = useCallback(async () => {
    if (!cid) {
      setCases([]);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getCasesV1({ clientId: cid });
      setCases(data);
    } catch (e) {
      setError(e);
    } finally {
      setLoading(false);
    }
  }, [cid]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const createCase = useCallback(
    async (payload: CaseCreateUpdate) => {
      if (!cid) throw new Error('clientId is required');
      const created = await apiService.createCase({
        ...payload,
        clientId: cid,
      } as CaseCreateUpdate);
      setCases((prev) => [created, ...prev]);
      return created;
    },
    [cid],
  );

  const updateCase = useCallback(async (id: number, payload: CaseCreateUpdate) => {
    const updated = await apiService.updateCase(id, payload);
    setCases((prev) => prev.map((k) => (k.id === id ? updated : k)));
    return updated;
  }, []);

  const deleteCase = useCallback(async (id: number) => {
    await apiService.deleteCase(id);
    setCases((prev) => prev.filter((k) => k.id !== id));
  }, []);

  return {
    cases,
    loading,
    error,
    refresh,
    createCase,
    updateCase,
    deleteCase,
    setCases,
  };
}
