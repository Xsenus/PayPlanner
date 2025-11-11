import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientStatus } from '../types';

export function useClientStatuses() {
  const [statuses, setStatuses] = useState<ClientStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchAll = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiService.getClientStatuses();
      setStatuses((data ?? []).filter((s) => s.isActive !== false));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить статусы клиентов');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchAll();
  }, [fetchAll]);

  const sorted = useMemo(
    () =>
      [...statuses].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [statuses],
  );

  return {
    statuses: sorted,
    loading,
    error,
    refresh: fetchAll,
  };
}
