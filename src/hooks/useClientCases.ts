import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientCase } from '../types';

type CaseCreateUpdate = Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;

export function useClientCases(clientId?: number | string, options: { enabled?: boolean; legalEntityId?: number | string } = {}) {
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const cid = clientId != null ? Number(clientId) : undefined;
  const legalEntityId = options.legalEntityId != null ? Number(options.legalEntityId) : undefined;
  const enabled = options.enabled ?? true;

  const refresh = useCallback(async () => {
    if (!enabled || (!cid && !legalEntityId)) {
      setCases([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getCasesV1({ clientId: cid, legalEntityId });
      setCases(data);
    } catch (e) {
      setError(e);
    } finally {
      setLoading(false);
    }
  }, [cid, enabled, legalEntityId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const createCase = useCallback(
    async (payload: CaseCreateUpdate) => {
      if (!cid && !legalEntityId) throw new Error('clientId or legalEntityId is required');
      const created = await apiService.createCase({
        ...payload,
        clientId: cid ?? null,
        legalEntityId: legalEntityId ?? null,
      } as CaseCreateUpdate);
      setCases((prev) => [created, ...prev]);
      return created;
    },
    [cid, legalEntityId],
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
