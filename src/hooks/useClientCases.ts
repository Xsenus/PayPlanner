import type { DocumentReadOwner } from './useDocumentDeliveryOwner';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientCase } from '../types';
import { awaitOwnedUiResult, StaleUiOperationError } from '../utils/StaleUiOperationError';

type CaseCreateUpdate = Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;

export function useClientCases(clientId?: number | string, options: { enabled?: boolean; legalEntityId?: number | string } = {}, readOwner?: DocumentReadOwner) {
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const abortRef = useRef<AbortController | null>(null);

  const cid = clientId != null ? Number(clientId) : undefined;
  const legalEntityId = options.legalEntityId != null ? Number(options.legalEntityId) : undefined;
  const enabled = options.enabled ?? true;
  const readScope = readOwner?.scope;
  const captureReadOwner = readOwner?.capture;
  const [dataScope, setDataScope] = useState(readScope);
  const resetReadScope = useRef<() => void>(() => undefined);
  resetReadScope.current = () => {
    abortRef.current?.abort(); abortRef.current = null;
    setCases([]); setError(null); setLoading(enabled);
    setDataScope(readScope);
  };
  useLayoutEffect(() => {
    if (readScope !== undefined) resetReadScope.current();
  }, [readScope]);
  const visible = readScope === undefined || dataScope === readScope;


  const refresh = useCallback(async () => {
    const owner = captureReadOwner?.();
    const isCurrent = () => !captureReadOwner || owner?.isCurrent() === true;
    if (!isCurrent()) return;
    abortRef.current?.abort();
    abortRef.current = null;
    if (!enabled || (!cid && !legalEntityId)) {
      setCases([]);
      setLoading(false);
      setError(null);
      return;
    }

    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getCasesV1(
        { clientId: cid, legalEntityId },
        { signal: controller.signal },
      );
      if (controller.signal.aborted || !isCurrent()) return;
      setCases(data);
    } catch (e) {
      if (controller.signal.aborted || !isCurrent()) return;
      setError(e);
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      if (!controller.signal.aborted && isCurrent()) {
        setLoading(false);
      }
    }
  }, [cid, enabled, legalEntityId, captureReadOwner]);

  useEffect(() => {
    void refresh();
    return () => {
      abortRef.current?.abort();
    };
  }, [refresh]);

  const createCase = useCallback(
    async (payload: CaseCreateUpdate, isCurrent: () => boolean = () => true) => {
      if (!cid && !legalEntityId) throw new Error('clientId or legalEntityId is required');
      const created = await awaitOwnedUiResult(() => apiService.createCase({
        ...payload,
        clientId: cid ?? null,
        legalEntityId: legalEntityId ?? null,
      } as CaseCreateUpdate), isCurrent);
      if (!isCurrent()) throw new StaleUiOperationError();
      abortRef.current?.abort();
      abortRef.current = null;
      setLoading(false);
      setError(null);
      setCases((prev) => [created, ...prev]);
      return created;
    },
    [cid, legalEntityId],
  );

  const updateCase = useCallback(async (id: number, payload: CaseCreateUpdate, isCurrent: () => boolean = () => true) => {
    const updated = await awaitOwnedUiResult(() => apiService.updateCase(id, payload), isCurrent);
    if (!isCurrent()) throw new StaleUiOperationError();
    abortRef.current?.abort();
    abortRef.current = null;
    setLoading(false);
    setError(null);
    setCases((prev) => prev.map((k) => (k.id === id ? updated : k)));
    return updated;
  }, []);

  const deleteCase = useCallback(async (id: number, isCurrent: () => boolean = () => true) => {
    await awaitOwnedUiResult(() => apiService.deleteCase(id), isCurrent);
    if (!isCurrent()) throw new StaleUiOperationError();
    abortRef.current?.abort();
    abortRef.current = null;
    setLoading(false);
    setError(null);
    setCases((prev) => prev.filter((k) => k.id !== id));
  }, []);

  return {
    cases: visible ? cases : [],
    loading: visible ? loading : enabled,
    error: visible ? error : null,
    refresh,
    createCase,
    updateCase,
    deleteCase,
    setCases,
  };
}
