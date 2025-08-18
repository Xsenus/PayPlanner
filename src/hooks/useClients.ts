import { useState, useEffect, useCallback } from 'react';
import { apiService } from '../services/api';
import type { Client, ClientStats, ClientCase } from '../types';

export function useClients() {
  const [clients, setClients] = useState<Client[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const hydrateCases = async (list: Client[]) => {
    const withCases = await Promise.all(
      list.map(async (c) => {
        try {
          const cases = await apiService.getCasesV1({ clientId: c.id });
          return { ...c, cases: cases as ClientCase[] };
        } catch {
          return { ...c, cases: [] as ClientCase[] };
        }
      }),
    );
    return withCases;
  };

  const fetchClients = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const base = await apiService.getClients();
      const enriched = await hydrateCases(base);
      setClients(enriched);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch clients');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchClients();
  }, [fetchClients]);

  const createClient = async (client: Omit<Client, 'id' | 'createdAt'>) => {
    try {
      await apiService.createClient(client);
      await fetchClients();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create client');
      throw err;
    }
  };

  const updateClient = async (id: number, client: Omit<Client, 'id' | 'createdAt'>) => {
    try {
      await apiService.updateClient(id, client);
      await fetchClients();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update client');
      throw err;
    }
  };

  const deleteClient = async (id: number) => {
    try {
      await apiService.deleteClient(id);
      await fetchClients();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete client');
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

    if (clientId) fetchStats();
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
