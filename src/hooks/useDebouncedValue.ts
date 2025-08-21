import { useEffect, useState } from 'react';

export function useDebouncedValue<T>(value: T, delayMs = 600): T {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(t);
  }, [value, delayMs]);

  return debounced;
}
