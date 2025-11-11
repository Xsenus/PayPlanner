import { useState, useEffect, useCallback } from 'react';
import { apiService } from '../services/api';
import type {
  Client,
  ClientStats,
  ClientCase,
  LegalEntitySummary,
  ClientInput,
  ClientStatus,
  Contract,
} from '../types';

function isClient(x: unknown): x is Client {
  if (typeof x !== 'object' || x === null) return false;
  const o = x as Record<string, unknown>;
  return typeof o.id === 'number' && typeof o.createdAt === 'string';
}

function normalizeClient(raw: Client, previous?: Client): Client {
  const source = raw as Client & Record<string, unknown>;

  const legalEntityId = source.legalEntityId ?? null;
  const hasLegalEntity = Object.prototype.hasOwnProperty.call(source, 'legalEntity');
  const legalEntity = hasLegalEntity
    ? ((source.legalEntity as LegalEntitySummary | null | undefined) ?? null)
    : previous?.legalEntity ?? null;

  const clientStatusId = source.clientStatusId ?? null;
  const hasClientStatus = Object.prototype.hasOwnProperty.call(source, 'clientStatus');
  const clientStatus = hasClientStatus
    ? ((source.clientStatus as ClientStatus | null | undefined) ?? null)
    : clientStatusId
      ? previous?.clientStatus ?? null
      : null;

  const hasCases = Object.prototype.hasOwnProperty.call(source, 'cases');
  const cases = hasCases
    ? Array.isArray(source.cases)
      ? (source.cases as ClientCase[])
      : []
    : previous?.cases ?? [];

  const hasContracts = Object.prototype.hasOwnProperty.call(source, 'contracts');
  const contracts = hasContracts
    ? Array.isArray(source.contracts)
      ? (source.contracts as Contract[])
      : []
    : previous?.contracts ?? [];

  return {
    ...previous,
    ...raw,
    legalEntityId,
    legalEntity,
    clientStatusId,
    clientStatus,
    cases,
    contracts,
  };
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
        setClients((prev) => [normalizeClient(created), ...prev]);
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
          prev.map((c) => (c.id === id ? normalizeClient(updated, c) : c)),
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
