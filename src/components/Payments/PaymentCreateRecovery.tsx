import { useEffect, useRef, useState } from 'react';
import { apiService } from '../../services/api';
import { PAYMENT_CREATE_CHANGED, isPaymentCreateOwnerCurrent, paymentCreateOwner,
  pendingPaymentCreates, paymentCreateSessionVersion, type PaymentCreateOwner, type PendingPaymentCreate } from '../../services/paymentCreateAttempts';
import type { Payment } from '../../types';

type Props = {
  onOpen?: (payment: Payment, attempt: PendingPaymentCreate) => Promise<boolean> | boolean;
  onAcknowledged?: (payment: Payment, attempt: PendingPaymentCreate) => void;
  onCancelled?: (attempt: PendingPaymentCreate) => void;
  scopeVersion?: () => string | number;
  beforeOpen?: (attempt: PendingPaymentCreate) => Promise<boolean>;
};

// One compact recovery surface for the common editor and the direct Calculator consumer.
// Metadata has no form values. A recovered payment is opened only by an explicit user action.
export function PaymentCreateRecovery({ onOpen, onAcknowledged, onCancelled, beforeOpen, scopeVersion }: Props) {
  const live = useRef(0);
  const busyRef = useRef(false);
  const session = useRef(paymentCreateSessionVersion());
  const [entries, setEntries] = useState<PendingPaymentCreate[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const read = () => {
      if (session.current !== paymentCreateSessionVersion()) {
        session.current = paymentCreateSessionVersion();
        live.current += 1;
        busyRef.current = false;
        setBusy(false);
        setEntries([]);
      }
      try { setEntries(pendingPaymentCreates()); setError(null); }
      catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось прочитать сохранения.'); }
      setNotice(null);
    };
    read();
    window.addEventListener(PAYMENT_CREATE_CHANGED, read);
    window.addEventListener('storage', read);
    return () => {
      live.current += 1;
      window.removeEventListener(PAYMENT_CREATE_CHANGED, read);
      window.removeEventListener('storage', read);
    };
  }, []);
  const run = async (entry: PendingPaymentCreate, action: 'check' | 'cancel' | 'open') => {
    if (busyRef.current) return;
    let actor: PaymentCreateOwner;
    try { actor = paymentCreateOwner(); } catch (cause) { setError(cause instanceof Error ? cause.message : 'Обновите страницу.'); return; }
    const actionRevision = ++live.current;
    const view = scopeVersion?.();
    const current = () => live.current === actionRevision && isPaymentCreateOwnerCurrent(actor) && scopeVersion?.() === view;
    busyRef.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      if (action === 'open') {
        if (beforeOpen && !await beforeOpen(entry)) return;
        if (!current()) return;
        const saved = await apiService.openPaymentCreate(entry);
        if (!current()) return;
        await apiService.fencePaymentCreateAdoption(entry, saved);
        if (!current()) return;
        const adopted = await onOpen?.(saved, entry);
        if (adopted !== true) return;
        // Opening the canonical payment may navigate away from this recovery surface.
        // A changed actor still forbids acknowledgement; navigation after successful adoption does not.
        if (!isPaymentCreateOwnerCurrent(actor)) return;
        await apiService.acknowledgePaymentCreate(entry, saved);
        if (current()) {
          onAcknowledged?.(saved, entry);
          setNotice(`Сохранён платёж №${saved.id}.`);
        }
      } else {
        const state = await apiService.inspectPaymentCreate(entry, action === 'cancel');
        if (!current()) return;
        if (action === 'cancel' && state.state === 'Cancelled') {
          onCancelled?.(entry);
          setNotice('Незавершённая попытка отменена. Можно добавить платёж заново.');
        } else if (state.state === 'Committed') {
          setNotice(`Сохранён платёж №${state.canonicalPaymentId}. Откройте его перед продолжением.`);
        } else {
          setNotice('Сохранение не подтверждено. Для нового добавления сначала отмените эту попытку.');
        }
      }
    } catch (cause) {
      if (current()) setError(cause instanceof Error ? cause.message : 'Проверка недоступна. Новое добавление приостановлено.');
    } finally { if (live.current === actionRevision && isPaymentCreateOwnerCurrent(actor)) { busyRef.current = false; setBusy(false); } }
  };
  if (!entries.length && !error && !notice) return null;
  return (
    <div className="space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm" role="status">
      {entries.length > 0 && <p>Сначала проверьте предыдущее добавление платежа. Повторное добавление приостановлено.</p>}
      {entries.map((entry) => (
        <div key={entry.attemptId} className="flex flex-wrap items-center gap-2">
          <span>{entry.operation === 'remainder' ? `Остаток платежа №${entry.primaryPaymentId}` : 'Новый платёж'}</span>
          <button type="button" disabled={busy} onClick={() => void run(entry, 'check')} className="rounded border px-2 py-1">Проверить сохранение</button>
          <button type="button" disabled={busy} onClick={() => void run(entry, 'cancel')} className="rounded border px-2 py-1">Отменить попытку</button>
          {entry.canonicalPaymentId && <button type="button" disabled={busy} onClick={() => void run(entry, 'open')} className="rounded border px-2 py-1">Открыть платёж №{entry.canonicalPaymentId}</button>}
        </div>
      ))}
      {error && <p className="text-rose-700" role="alert">{error}</p>}
      {notice && <p>{notice}</p>}
    </div>
  );
}
