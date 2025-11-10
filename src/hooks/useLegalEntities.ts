import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiService } from '../services/api';
import type {
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
  LegalEntitySummary,
} from '../types';

function mapDetailToSummary(detail: LegalEntityDetail): LegalEntitySummary {
  return { ...detail };
}

export function useLegalEntities() {
  const [legalEntities, setLegalEntities] = useState<LegalEntitySummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchAll = useCallback(async (search?: string) => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiService.getLegalEntities(search ? { search } : undefined);
      setLegalEntities(data ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить юридические лица');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchAll();
  }, [fetchAll]);

  const createLegalEntity = useCallback(async (payload: LegalEntityInput) => {
    const created = await apiService.createLegalEntity(payload);
    setLegalEntities((prev) => [mapDetailToSummary(created), ...prev]);
    return created;
  }, []);

  const updateLegalEntity = useCallback(async (id: number, payload: LegalEntityInput) => {
    const updated = await apiService.updateLegalEntity(id, payload);
    setLegalEntities((prev) =>
      prev.map((item) => (item.id === id ? mapDetailToSummary(updated) : item)),
    );
    return updated;
  }, []);

  const deleteLegalEntity = useCallback(async (id: number) => {
    await apiService.deleteLegalEntity(id);
    setLegalEntities((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const getLegalEntity = useCallback(async (id: number) => {
    return apiService.getLegalEntity(id);
  }, []);

  const suggestLegalEntities = useCallback(
    async (payload: { query?: string; inn?: string; limit?: number }): Promise<LegalEntitySuggestion[]> =>
      apiService.suggestLegalEntities(payload),
    [],
  );

  const sorted = useMemo(
    () =>
      [...legalEntities].sort((a, b) =>
        a.shortName.localeCompare(b.shortName, undefined, { sensitivity: 'base' }),
      ),
    [legalEntities],
  );

  return {
    legalEntities: sorted,
    loading,
    error,
    refresh: fetchAll,
    createLegalEntity,
    updateLegalEntity,
    deleteLegalEntity,
    getLegalEntity,
    suggestLegalEntities,
  };
}
