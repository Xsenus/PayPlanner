import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../services/api';

export function useFormRequirements(section: string, enabled = true) {
  const [requiredKeys, setRequiredKeys] = useState<Set<string>>(new Set());

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;

    const load = async () => {
      try {
        const data = await apiService.getFormRequirements(section);
        if (cancelled) return;
        const keys = new Set((data ?? []).filter((x) => x.isRequired).map((x) => x.fieldKey));
        setRequiredKeys(keys);
      } catch {
        if (!cancelled) setRequiredKeys(new Set());
      }
    };

    void load();

    return () => {
      cancelled = true;
    };
  }, [section, enabled]);

  const isRequired = useMemo(() => {
    return (key: string) => requiredKeys.has(key);
  }, [requiredKeys]);

  return { isRequired, requiredKeys };
}
