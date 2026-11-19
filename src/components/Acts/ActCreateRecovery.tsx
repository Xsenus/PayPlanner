import { useCallback, useEffect, useRef, useState } from 'react';
import type { Act, ActInput } from '../../types';
import { ACT_CREATE_ATTEMPTS_CHANGED, acknowledgeCreatedAct, actCreateCommand, newActCreateKey, pendingActCreates,
  type ActCreateOptions, type PendingActCreate } from '../../services/actCreateAttempts';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';
import { formatCurrencySmart } from '../../utils/formatters';

export type ActSubmit = (payload: ActInput, options?: ActCreateOptions) => Promise<Act | void>;

function attemptDetails(entry: PendingActCreate): string {
  const details: string[] = [];
  if (entry.invoiceNumber || entry.invoiceId) {
    const invoiceNumber = entry.invoiceNumber ? `№${entry.invoiceNumber}` : '';
    const invoiceId = entry.invoiceId ? `#${entry.invoiceId}` : '';
    details.push(`Счёт ${[invoiceNumber, invoiceId].filter(Boolean).join(' ')}`);
  }
  if (entry.clientId) details.push(`Клиент #${entry.clientId}`);
  if (entry.legalEntityId) details.push(`Организация #${entry.legalEntityId}`);
  if (typeof entry.amount === 'number' && Number.isFinite(entry.amount)) {
    details.push(entry.state !== 'confirmed' && entry.creationMode === 'Full' && entry.amount === 0 ? 'Сумма по счёту' : formatCurrencySmart(entry.amount).full);
  }
  return details.join(' · ');
}

export function useActCreateRecovery(enabled: boolean, scope: string, onSubmit: ActSubmit, onRestoreDraft?: (command: ActInput) => void) {
  const [listing, setListing] = useState<{ scope: string; entries: PendingActCreate[]; failed?: boolean }>({ scope: '', entries: [] });
  const [readVersion, reload] = useState(0);
  const entries = listing.scope === scope ? listing.entries : [];
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [fresh, setFresh] = useState(false);
  const attempt = useRef<string | null>(null);
  const scopeRef = useRef(scope);
  scopeRef.current = scope;
  const mounted = useRef(false);
  const generation = useRef(0);
  useEffect(() => {
    mounted.current = true;
    generation.current += 1;
    attempt.current = null;
    setFresh(false);
    setBusy(false);
    setError(null);
    const refresh = () => {
      if (!enabled) { setListing({ scope, entries: [] }); return; }
      try { setListing({ scope, entries: pendingActCreates() }); }
      catch (reason) { setListing({ scope, entries: [], failed: true }); setError(reason instanceof Error ? reason.message : 'Не удалось прочитать попытки создания акта.'); }
    };
    refresh();
    window.addEventListener(ACT_CREATE_ATTEMPTS_CHANGED, refresh);
    return () => { mounted.current = false; generation.current += 1; window.removeEventListener(ACT_CREATE_ATTEMPTS_CHANGED, refresh); };
  }, [enabled, readVersion, scope]);
  const submit = useCallback(async (payload: ActInput, options?: ActCreateOptions) => {
    const session = captureAuthSessionIdentity();
    const lifetime = generation.current;
    const current = () => mounted.current && generation.current === lifetime && scopeRef.current === scope && isAuthSessionIdentityCurrent(session);
    if (busy) return;
    const key = options?.attemptKey ?? attempt.current ?? newActCreateKey();
    attempt.current = key;
    setBusy(true);
    setError(null);
    try {
      const act = await onSubmit(payload, { ...options, attemptKey: key });
      if (act && current()) {
        acknowledgeCreatedAct(act);
        attempt.current = null;
      }
    } catch (reason) {
      if (current()) setError(reason instanceof Error ? reason.message : 'Не удалось сохранить акт.');
      throw reason;
    } finally { if (current()) setBusy(false); }
  }, [busy, onSubmit, scope]);
  const recover = async (entry: PendingActCreate, retry: boolean) => {
    const lifetime = generation.current;
    let session: ReturnType<typeof captureAuthSessionIdentity> | null = null;
    try {
      session = captureAuthSessionIdentity();
      const command = actCreateCommand(entry.key);
      await submit(command, { attemptKey: entry.key, retry, lookupOnly: !retry });
    } catch (reason) {
      if (mounted.current && generation.current === lifetime && scopeRef.current === scope && session && isAuthSessionIdentityCurrent(session)) {
        setError(reason instanceof Error ? reason.message : 'Не удалось прочитать сохранённую попытку.');
      }
    }
  };
  const selected = entries.find((entry) => entry.key === attempt.current);
  const currentError = listing.scope === scope ? error : null;
  const blocked = enabled && (listing.scope !== scope || listing.failed && !fresh || (selected ? selected.state !== 'editable' : entries.length > 0 && !fresh));
  const startAnother = () => { attempt.current = null; setFresh(true); setError(null); };
  const editDraft = (entry: PendingActCreate) => {
    try {
      onRestoreDraft?.(actCreateCommand(entry.key));
      attempt.current = entry.key;
      setFresh(true);
      setError(null);
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Не удалось восстановить черновик.'); }
  };
  const panel = enabled && (entries.length > 0 || currentError) ? (
    <div className="space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm">
      {entries.length > 0 && <p>Сохранённые попытки создания акта. Проверка получает результат, повтор отправляет прежнюю команду с тем же номером попытки.</p>}
      {entries.map((entry) => (
        <div key={entry.key} className="flex flex-wrap items-center gap-2">
          <span className="w-full min-w-0 break-words sm:w-auto sm:flex-1">
            <span className="block">{entry.date.slice(0, 10)}{entry.title ? ` · ${entry.title}` : ''} · {entry.state === 'confirmed' ? 'Акт ранее создан' : entry.state === 'editable' ? 'Исправьте данные и повторите сохранение' : 'Результат не подтверждён'}</span>
            <span className="block text-xs text-slate-600">{attemptDetails(entry)}</span>
          </span>
          <button type="button" disabled={busy} onClick={() => void recover(entry, false)} className="rounded border border-amber-300 px-2 py-1 disabled:opacity-50">Проверить результат</button>
          {entry.state === 'unknown' && <button type="button" disabled={busy} onClick={() => void recover(entry, true)} className="rounded border border-amber-300 px-2 py-1 disabled:opacity-50">Повторить сохранение</button>}
          {entry.state === 'editable' && <button type="button" disabled={busy} onClick={() => editDraft(entry)} className="rounded border border-amber-300 px-2 py-1 disabled:opacity-50">Исправить данные</button>}
        </div>
      ))}
      {listing.failed && <button type="button" disabled={busy} onClick={() => reload(value => value + 1)} className="mr-2 rounded border border-slate-300 bg-white px-2 py-1 disabled:opacity-50">Повторить чтение попыток</button>}
      {(entries.length > 0 || listing.failed) && <button type="button" disabled={busy} onClick={startAnother} className="rounded border border-slate-300 bg-white px-2 py-1 disabled:opacity-50">Создать другой акт</button>}
      {currentError && <p role="alert" className="text-rose-700">{currentError}</p>}
    </div>
  ) : null;
  return { submit, panel, blocked, busy };
}
