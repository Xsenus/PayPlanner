import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientRole, PayablesDashboard } from '../types';
import { extractDisplayErrorMessage } from '../utils/errorUtils';

type UsePayablesDashboardParams = {
  from?: string;
  to?: string;
  search?: string;
  clientId?: number;
  legalEntityId?: number;
  clientIds?: string;
  clientRole?: ClientRole;
  enabled?: boolean;
};

export function usePayablesDashboard(params: UsePayablesDashboardParams) {
  const [data, setData] = useState<PayablesDashboard | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const requestParams = useMemo(
    () => ({
      from: params.from,
      to: params.to,
      search: params.search,
      clientId: params.clientId,
      legalEntityId: params.legalEntityId,
      clientIds: params.clientIds,
      clientRole: params.clientRole,
    }),
    [params.clientId, params.legalEntityId, params.clientIds, params.clientRole, params.from, params.search, params.to],
  );

  const refresh = useCallback(async () => {
    if (params.enabled === false) {
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const response = await apiService.getPayablesDashboard(requestParams);
      setData(response);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить доску кредиторки'));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [params.enabled, requestParams]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return { data, loading, error, refresh };
}
