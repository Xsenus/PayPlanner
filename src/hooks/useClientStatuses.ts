import { useCallback, useMemo } from 'react';
import { apiService } from '../services/api';
import { assertDictionaryRows, useDictionarySnapshot, type DictionaryReadOptions } from './useDictionarySnapshot';
import type { ClientStatus } from '../types';

const empty: ClientStatus[] = [];
export function useClientStatuses(options?: DictionaryReadOptions) {
  const load = useCallback(async (signal: AbortSignal) => {
    const rows = await apiService.getClientStatuses({ signal });
    assertDictionaryRows(rows);
    return rows.filter(item => item.isActive !== false);
  }, []);
  const state = useDictionarySnapshot(load, empty, options);
  const sorted = useMemo(() => [...state.value].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })), [state.value]);
  return { statuses: sorted, loading: state.loading, error: state.error, refresh: state.refresh };
}
