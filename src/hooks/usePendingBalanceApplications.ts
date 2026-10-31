import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { apiService } from '../services/api';
import { BALANCE_APPLICATION_CHANGED, type PendingBalanceApplication, type ReadyBalanceApplication } from '../services/balanceApplicationAttempts';
import { captureAuthSessionIdentity } from '../services/authService';
import type { BalanceApplicationOwnerKind } from '../services/balanceApplicationInspection';
import { PAYMENT_CREATE_CHANGED } from '../services/paymentCreateAttempts';

export type PendingBalanceApplicationsView = { entries: PendingBalanceApplication[]; missing: ReadonlySet<string>; error: string | null; blocked: boolean;
  adopted: (value: ReadyBalanceApplication) => void };

// Metadata changes update the view, but never change the financial operation's owner.
export function usePendingBalanceApplications(kind: BalanceApplicationOwnerKind, ownerId: number, enabled: boolean, context: string, allOwners = false): PendingBalanceApplicationsView {
  const key = JSON.stringify([kind, ownerId, enabled, context, allOwners]);
  const history = useRef({ session: '', records: new Map<string, PendingBalanceApplication>(), accepted: new Set<string>() });
  const [view, setView] = useState<{ key: string; entries: PendingBalanceApplication[]; missing: Set<string>; error: string | null }>({ key: '', entries: [], missing: new Set(), error: null });
  const cache = useCallback(() => {
    const session = JSON.stringify(captureAuthSessionIdentity());
    if (history.current.session !== session) history.current = { session, records: new Map(), accepted: new Set() };
    return history.current;
  }, []);
  const read = useCallback(() => {
    const saved = cache();
    if (!enabled) { setView({ key, entries: [], missing: new Set(), error: null }); return; }
    try {
      const actual = apiService.pendingBalanceApplications(); const ids = new Set(actual.map(entry => entry.requestId));
      for (const id of saved.accepted) if (!ids.has(id)) { saved.records.delete(id); saved.accepted.delete(id); }
      let error: string | null = null;
      for (const entry of actual) {
        if (saved.records.has(entry.requestId) || saved.records.size < 64) saved.records.set(entry.requestId, entry);
        else error = 'Сначала проверьте ранее открытые зачёты, чтобы загрузить остальные.';
      }
      const entries = [...saved.records.values()].filter(entry => allOwners || (entry.kind === kind && entry.ownerId === ownerId));
      const missing = new Set(entries.filter(entry => !ids.has(entry.requestId)).map(entry => entry.requestId));
      setView({ key, entries, missing, error });
    } catch (cause) { setView({ key, entries: [], missing: new Set(), error: cause instanceof Error ? cause.message : 'Не удалось прочитать незавершённые зачёты.' }); }
  }, [key, enabled, kind, ownerId, allOwners, cache]);
  useLayoutEffect(() => {
    read();
    window.addEventListener(BALANCE_APPLICATION_CHANGED, read);
    window.addEventListener(PAYMENT_CREATE_CHANGED, read);
    window.addEventListener('storage', read);
    return () => {
      window.removeEventListener(BALANCE_APPLICATION_CHANGED, read);
      window.removeEventListener(PAYMENT_CREATE_CHANGED, read);
      window.removeEventListener('storage', read);
    };
  }, [read]);
  const current = enabled && view.key === key ? view : { entries: [], missing: new Set<string>(), error: null };
  return { ...current, blocked: enabled && (view.key !== key || current.error !== null || current.entries.length > 0),
    adopted: value => {
      if (!apiService.canContinueBalanceApplication(value)) return;
      cache().accepted.add(value.requestId); read();
    } };
}
