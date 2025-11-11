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

function normalizeClient(client: Client): Client {
  return {
    ...client,
    legalEntityId: client.legalEntityId ?? null,
    legalEntity: (client.legalEntity as LegalEntitySummary | null) ?? null,
    clientStatusId: client.clientStatusId ?? null,
    clientStatus: (client.clientStatus as ClientStatus | null) ?? null,
    cases: Array.isArray(client.cases) ? (client.cases as ClientCase[]) : [],
    contracts: Array.isArray(client.contracts) ? client.contracts : [],
  };
}

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
      const normalized = (base as Client[]).map((c) => normalizeClient(c));

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
        const normalized = normalizeClient(created);
        setClients((prev) => [normalized, ...prev]);
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
        const normalized = normalizeClient(updated);
        setClients((prev) =>
          prev.map((c) =>
            c.id === id
              ? {
                  ...c,
                  ...normalized,
                  legalEntity:
                    normalized.legalEntity ?? (c.legalEntity as LegalEntitySummary | null) ?? null,
                  clientStatus: normalized.clientStatus ?? (c.clientStatus as ClientStatus | null) ?? null,
                  cases:
                    normalized.cases.length === 0 && (c.cases?.length ?? 0) > 0
                      ? (c.cases as ClientCase[])
                      : normalized.cases,
                  contracts:
                    (normalized.contracts?.length ?? 0) === 0 && (c.contracts?.length ?? 0) > 0
                      ? c.contracts
                      : normalized.contracts,
                }
              : c,
          ),
        );
        void fetchClients();
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
