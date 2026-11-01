import { useCallback, useMemo, useRef } from 'react';
import { apiService } from '../services/api';
import { useDictionarySnapshot } from './useDictionarySnapshot';

export function useFormRequirements(section: string, enabled = true, options?: { ownerScope?: string }) {
  const sectionKey = section.trim();
  const active = enabled && Boolean(sectionKey);
  const empty = useMemo(() => new Set<string>(), []);
  const load = useCallback(async (signal: AbortSignal) => {
    try {
      const data = await apiService.getFormRequirements(sectionKey, { signal });
      if (!Array.isArray(data) || !data.every(item => item && item.section === sectionKey
        && typeof item.fieldKey === 'string' && item.fieldKey.trim().length > 0 && typeof item.isRequired === 'boolean')
        || new Set(data.map(item => item.fieldKey.toLowerCase())).size !== data.length) {
        throw new Error('Не удалось подтвердить правила заполнения формы.');
      }
      return new Set(data.filter(item => item.isRequired).map(item => item.fieldKey));
    } catch {
      // Preserve the existing empty fallback and leave server-side validation unchanged.
      return empty;
    }
  }, [sectionKey, empty]);
  const { value: requiredKeys, capture, loading, refresh } = useDictionarySnapshot(load, empty, { enabled: active,
    ownerScope: JSON.stringify([sectionKey, options?.ownerScope ?? '']) }, false);
  const latestKeys = useRef(requiredKeys);
  const isRequired = useMemo(() => {
    latestKeys.current = requiredKeys;
    const owner = active ? capture() : null;
    return (key: string) => latestKeys.current.has(key) && Boolean(owner?.isCurrent());
  }, [active, capture, requiredKeys]);
  return { isRequired, requiredKeys, loading, refresh };
}
