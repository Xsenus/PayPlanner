import { useState, useEffect, useCallback } from 'react';
import { apiService } from '../services/api';
import type {
  Client,
  ClientStats,
  ClientCase,
  LegalEntitySummary,
  ClientInput,
  ClientStatus,
} from '../types';

function isClient(x: unknown): x is Client {
  if (typeof x !== 'object' || x === null) return false;
  const o = x as Record<string, unknown>;
  return typeof o.id === 'number' && typeof o.createdAt === 'string';
}

export function useClients() {
  const [clients, setClients] = useState<Client[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchClients = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const base = await apiService.getClients();
      const normalized: Client[] = (base as Client[]).map((c) => ({
        ...c,
        legalEntityId: c.legalEntityId ?? null,
        legalEntity: (c.legalEntity as LegalEntitySummary | null) ?? null,
        cases: (c.cases ?? []) as ClientCase[],
        clientStatusId: c.clientStatusId ?? null,
        clientStatus: (c.clientStatus as ClientStatus | null) ?? null,
      }));

      setClients(normalized);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch clients');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchClients();
  }, [fetchClients]);

  const createClient = async (client: ClientInput) => {
    try {
      const created = await apiService.createClient(client);
      if (isClient(created)) {
        setClients((prev) => [
          {
            ...created,
            legalEntityId: created.legalEntityId ?? null,
            legalEntity: (created.legalEntity as LegalEntitySummary | null) ?? null,
            cases: (created.cases ?? []) as ClientCase[],
            clientStatusId: created.clientStatusId ?? null,
            clientStatus: (created.clientStatus as ClientStatus | null) ?? null,
          },
          ...prev,
        ]);
      } else {
        await fetchClients();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create client');
      throw err;
    }
  };

  const updateClient = async (id: number, patch: ClientInput) => {
    try {
      const updated = await apiService.updateClient(id, patch);
      if (isClient(updated)) {
        setClients((prev) =>
          prev.map((c) =>
            c.id === id
              ? {
                  ...updated,
                  legalEntityId: updated.legalEntityId ?? null,
                  legalEntity: (updated.legalEntity as LegalEntitySummary | null) ?? null,
                  cases: (updated.cases ?? c.cases ?? []) as ClientCase[],
                  clientStatusId: updated.clientStatusId ?? null,
                  clientStatus: (updated.clientStatus as ClientStatus | null) ?? null,
                }
              : c,
          ),
        );
      } else {
        await fetchClients();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update client');
      throw err;
    }
  };

  const deleteClient = async (id: number) => {
    try {
      setClients((prev) => prev.filter((c) => c.id !== id));
      await apiService.deleteClient(id);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete client');
      await fetchClients();
      throw err;
    }
  };

  return {
    clients,
    loading,
    error,
    createClient,
    updateClient,
    deleteClient,
    setClients,
    refresh: fetchClients,
  };
}

export function useClientStats(clientId: number, caseId?: number) {
  const [stats, setStats] = useState<ClientStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchStats = async () => {
      try {
        setLoading(true);
        const data = await apiService.getClientStats(clientId, caseId ? { caseId } : undefined);
        setStats(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to fetch client statistics');
      } finally {
        setLoading(false);
      }
    };

    if (clientId) void fetchStats();
  }, [clientId, caseId]);

  return {
    stats,
    loading,
    error,
    refresh: async () => {
      const data = await apiService.getClientStats(clientId, caseId ? { caseId } : undefined);
      setStats(data);
    },
  };
}
