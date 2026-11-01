import { useCallback, useEffect, useRef, useState } from 'react';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from './useDocumentDeliveryOwner';
import { isHttpErrorStatus } from '../utils/errorUtils';

export type DictionaryReadOptions = { enabled?: boolean; ownerScope?: string };

export function assertDictionaryRows(data: unknown): asserts data is { id: number; name: string }[] {
  if (!Array.isArray(data) || !data.every(item => item && Number.isSafeInteger(item.id) && item.id > 0 && typeof item.name === 'string')
    || new Set(data.map(item => item.id)).size !== data.length) {
    throw new Error('Не удалось подтвердить данные справочника. Обновите список.');
  }
}

// Readonly reference snapshots share one request lifetime; form rules do not listen to dictionary edits.
export function useDictionarySnapshot<T>(load: (signal: AbortSignal) => Promise<T>, empty: T, options?: DictionaryReadOptions, listenForChanges = true) {
  const enabled = options?.enabled ?? true;
  const { capture, scope } = useDocumentDeliveryOwner(JSON.stringify([enabled, options?.ownerScope ?? '']));
  const [view, setView] = useState<{ scope: string; owner: DocumentDeliveryRead | null; value: T; loading: boolean; error: string | null }>({ scope, owner: null, value: empty, loading: enabled, error: null });
  const read = useRef<{ sequence: number; controller: AbortController | null }>({ sequence: 0, controller: null });
  const cancel = useCallback(() => {
    read.current.sequence++;
    read.current.controller?.abort();
    read.current.controller = null;
  }, []);
  const refresh = useCallback(async () => {
    const owner = enabled ? capture() : null;
    if (!owner) return;
    cancel();
    const sequence = read.current.sequence;
    const controller = new AbortController();
    read.current.controller = controller;
    const current = () => owner.isCurrent() && sequence === read.current.sequence;
    setView(previous => current() ? { scope, owner, value: previous.scope === scope && previous.owner?.isCurrent() ? previous.value : empty, loading: true, error: null } : previous);
    try {
      const value = await load(controller.signal);
      if (current()) setView(previous => current() ? { scope, owner, value, loading: false, error: null } : previous);
    } catch (error) {
      if (current()) {
        // A failed member of the batch makes its remaining HTTP reads unnecessary.
        controller.abort();
        const denied = isHttpErrorStatus(error, 401) || isHttpErrorStatus(error, 403);
        setView(previous => current() ? { scope, owner, value: denied || previous.scope !== scope || !previous.owner?.isCurrent() ? empty : previous.value, loading: false,
          error: error instanceof Error ? error.message : 'Не удалось загрузить справочники.' } : previous);
      }
    } finally {
      if (read.current.controller === controller) read.current.controller = null;
    }
  }, [enabled, capture, scope, cancel, load, empty]);
  useEffect(() => { void refresh(); return cancel; }, [refresh, cancel]);
  useEffect(() => {
    if (!enabled || !listenForChanges) return;
    const changed = () => { void refresh(); };
    window.addEventListener('dicts:changed', changed);
    return () => window.removeEventListener('dicts:changed', changed);
  }, [enabled, refresh, listenForChanges]);
  const visible = enabled && view.scope === scope && view.owner?.isCurrent() ? view : null;
  return { value: visible?.value ?? empty, loading: enabled && (visible?.loading ?? true), error: visible?.error ?? null, refresh, capture };
}
