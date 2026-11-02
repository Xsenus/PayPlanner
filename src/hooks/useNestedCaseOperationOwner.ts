import { useCallback } from 'react';
import { useAuth } from '../contexts/AuthContext';
import { captureAuthSessionIdentity } from '../services/authService';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead, type DocumentDeliveryOperation } from './useDocumentDeliveryOwner';

export type CaseOperationParent = Pick<ReturnType<typeof useDocumentDeliveryOwner>, 'scope' | 'capture' | 'busy'> & { begin: (kind: string) => DocumentDeliveryOperation | null };

// A child keeps its own lifetime while sharing the parent's single pending operation.
export function useNestedCaseOperationOwner(scope: string, parent?: CaseOperationParent, enabled = true) {
  const { user } = useAuth();
  const parentCapture = parent?.capture;
  const parentBegin = parent?.begin;
  const available = enabled && captureAuthSessionIdentity().authenticated && (!parentCapture || !!parentCapture());
  const local = useDocumentDeliveryOwner(JSON.stringify([scope, parent?.scope, available, user?.id, user?.role?.id]));
  const { capture: localCapture, begin: localBegin } = local;
  const capture = useCallback((): DocumentDeliveryRead | null => {
    if (!available) return null;
    const own = localCapture(); const outer = parentCapture?.();
    if (!own || (parentCapture && !outer)) return null;
    return { isCurrent: () => own.isCurrent() && (!outer || outer.isCurrent()) };
  }, [available, localCapture, parentCapture]);
  const begin = useCallback((kind: string): DocumentDeliveryOperation | null => {
    const owner = capture(); if (!owner) return null;
    const outer = parentBegin?.(kind); if (parentBegin && !outer) return null;
    const own = localBegin(kind); if (!own) { outer?.finish(); return null; }
    return { kind, isCurrent: () => own.isCurrent() && owner.isCurrent() && (!outer || outer.isCurrent()),
      finish: () => { own.finish(); outer?.finish(); } };
  }, [capture, localBegin, parentBegin]);
  const active = !!capture();
  return { scope: local.scope, active, capture, begin, invalidate: local.invalidate, busy: active && (local.busy || !!parent?.busy), kind: active ? local.kind : null };
}
export type NestedCaseOperationOwner = ReturnType<typeof useNestedCaseOperationOwner>;
