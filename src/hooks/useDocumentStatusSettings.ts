import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../services/api';
import type { DocumentStatusEntity } from '../types';

export function useDocumentStatusSettings() {
  const [statuses, setStatuses] = useState<DocumentStatusEntity[] | null>(null);

  const load = useCallback(async () => {
    try {
      setStatuses(await apiService.getDocumentStatuses());
    } catch {
      setStatuses(null);
    }
  }, []);

  useEffect(() => {
    void load();

    const handleChanged = () => {
      void load();
    };

    window.addEventListener('dicts:changed', handleChanged);
    return () => window.removeEventListener('dicts:changed', handleChanged);
  }, [load]);

  return statuses;
}
