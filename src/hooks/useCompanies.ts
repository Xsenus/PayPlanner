import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { Company, CompanyPayload } from '../types';

function isCompany(x: unknown): x is Company {
  if (typeof x !== 'object' || x === null) return false;
  const o = x as Record<string, unknown>;
  return typeof o.id === 'number' && typeof o.createdAt === 'string';
}

export function useCompanies() {
  const [companies, setCompanies] = useState<Company[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchCompanies = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const list = await apiService.getCompanies();
      const normalized = (list ?? []).map((company) => ({
        ...company,
        members: (company.members ?? []).map((m) => ({ ...m })),
      }));
      setCompanies(normalized);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch companies');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchCompanies();
  }, [fetchCompanies]);

  const createCompany = async (payload: CompanyPayload) => {
    try {
      const created = await apiService.createCompany(payload);
      if (isCompany(created)) {
        setCompanies((prev) => [{ ...created, members: created.members ?? [] }, ...prev]);
      } else {
        await fetchCompanies();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create company');
      throw err;
    }
  };

  const updateCompany = async (id: number, payload: CompanyPayload) => {
    try {
      const updated = await apiService.updateCompany(id, payload);
      if (isCompany(updated)) {
        setCompanies((prev) =>
          prev.map((company) =>
            company.id === id ? { ...updated, members: updated.members ?? [] } : company,
          ),
        );
      } else {
        await fetchCompanies();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update company');
      throw err;
    }
  };

  const deleteCompany = async (id: number) => {
    try {
      setCompanies((prev) => prev.filter((company) => company.id !== id));
      await apiService.deleteCompany(id);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete company');
      await fetchCompanies();
      throw err;
    }
  };

  return {
    companies,
    loading,
    error,
    createCompany,
    updateCompany,
    deleteCompany,
    setCompanies,
    refresh: fetchCompanies,
  };
}
