import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useAsyncOperationOwner } from './useAsyncOperationOwner';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../services/authService';

export type DocumentDeliveryRead = { isCurrent: () => boolean };
export type DocumentReadOwner = { scope: string; capture: () => DocumentDeliveryRead | null };
export type DocumentDeliveryOperation = DocumentDeliveryRead & { finish: () => void; kind: string };

// Owns UI acceptance and the next step of a delivery; an already sent request is not undone.
export function useDocumentDeliveryOwner(scope: string) {
  const { epoch, actor, authenticated } = captureAuthSessionIdentity();
  const session = useMemo(() => ({ epoch, actor, authenticated }), [epoch, actor, authenticated]);
  const key = JSON.stringify([scope, session]);
  const { capture: captureEpoch, isCurrent: currentEpoch, invalidate: invalidateEpoch } = useAsyncOperationOwner(key);
  const renderedEpoch = captureEpoch();
  // React StrictMode replays effect cleanup; recapture that new lifetime once.
  const [, syncLifetime] = useState(renderedEpoch);
  useEffect(() => { syncLifetime(captureEpoch()); }, [captureEpoch]);
  const closed = useRef({ key, value: false });
  if (closed.current.key !== key) closed.current = { key, value: false };
  const pending = useRef<DocumentDeliveryOperation | null>(null);
  const [operation, setOperation] = useState<DocumentDeliveryOperation | null>(null);
  const capture = useCallback((): DocumentDeliveryRead | null => {
    const isCurrent = () => !closed.current.value && currentEpoch(renderedEpoch) && isAuthSessionIdentityCurrent(session);
    return isCurrent() ? { isCurrent } : null;
  }, [currentEpoch, renderedEpoch, session]);
  const begin = useCallback((kind = 'send'): DocumentDeliveryOperation | null => {
    const owner = capture();
    if (!owner || pending.current?.isCurrent()) return null;
    const next: DocumentDeliveryOperation = {
      kind,
      isCurrent: () => pending.current === next && owner.isCurrent(),
      finish: () => {
        if (pending.current !== next) return;
        pending.current = null;
        if (owner.isCurrent()) setOperation(null);
      },
    };
    pending.current = next;
    setOperation(next);
    return next;
  }, [capture]);
  const invalidate = useCallback(() => { closed.current.value = true; invalidateEpoch(); pending.current = null; setOperation(null); }, [invalidateEpoch]);
  const busy = operation?.isCurrent() ?? false;
  return { capture, begin, invalidate, scope: key, busy, kind: busy ? operation!.kind : null };
}
