import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientRole, ClientStats } from '../types';

export function useClientStats(clientId: number, caseId?: number, clientRole?: ClientRole) {
  const [stats, setStats] = useState<ClientStats | null>(null);
  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<unknown>(null);

  const refresh = useCallback(async () => {
    if (!clientId) return;
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getClientStats(clientId, {
        caseId,
        clientRole,
      });
      setStats(data);
    } catch (e) {
      setError(e);
    } finally {
      setLoading(false);
    }
  }, [clientId, caseId, clientRole]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return { stats, loading, error, refresh };
}
