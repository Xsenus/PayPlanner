import { useCallback } from 'react';
import { apiService } from '../services/api';
import { captureAuthSessionIdentity } from '../services/authService';
import type { DocumentStatusEntity } from '../types';
import { assertDictionaryRows, useDictionarySnapshot, type DictionaryReadOptions } from './useDictionarySnapshot';

export function useDocumentStatusSettings(options?: DictionaryReadOptions) {
  const load = useCallback(async (signal: AbortSignal) => {
    const rows = await apiService.getDocumentStatuses({ signal });
    assertDictionaryRows(rows);
    if (!rows.every(row => typeof row.code === 'string' && row.code.trim().length > 0
      && typeof row.isActive === 'boolean' && typeof row.useForInvoices === 'boolean'
      && typeof row.useForActs === 'boolean' && typeof row.useForContracts === 'boolean'
      && Number.isFinite(row.sortOrder))) throw new Error('Не удалось подтвердить статусы документов.');
    return rows;
  }, []);
  const state = useDictionarySnapshot<DocumentStatusEntity[] | null>(load, null, { ...options, enabled: captureAuthSessionIdentity().authenticated && (options?.enabled ?? true) });
  return state.error ? null : state.value;
}
