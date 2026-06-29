import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { ServiceCategoryDictionaryItem, ServiceDictionaryItem } from '../types';
import type { BaseDictItem } from '../types/dictionaries';

type InvoiceReferenceDictionariesState = {
  invoiceUnits: BaseDictItem[];
  services: ServiceDictionaryItem[];
  serviceCategories: ServiceCategoryDictionaryItem[];
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
};

const DICTS_CHANGED = 'dicts:changed';

export function useInvoiceReferenceDictionaries(): InvoiceReferenceDictionariesState {
  const [invoiceUnits, setInvoiceUnits] = useState<BaseDictItem[]>([]);
  const [services, setServices] = useState<ServiceDictionaryItem[]>([]);
  const [serviceCategories, setServiceCategories] = useState<ServiceCategoryDictionaryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.getInvoiceReferenceDictionaries();
      setInvoiceUnits((response.invoiceUnits ?? []).filter((item) => item.isActive !== false));
      setServices((response.services ?? []).filter((item) => item.isActive !== false));
      setServiceCategories((response.serviceCategories ?? []).filter((item) => item.isActive !== false));
    } catch (err) {
      setInvoiceUnits([]);
      setServices([]);
      setServiceCategories([]);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить справочники счета');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handler = () => void refresh();
    window.addEventListener(DICTS_CHANGED, handler);
    return () => window.removeEventListener(DICTS_CHANGED, handler);
  }, [refresh]);

  return {
    invoiceUnits,
    services,
    serviceCategories,
    loading,
    error,
    refresh,
  };
}
