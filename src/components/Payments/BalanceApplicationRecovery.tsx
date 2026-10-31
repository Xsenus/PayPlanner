import { useRef, useState } from 'react';
import type { PendingBalanceApplicationsView } from '../../hooks/usePendingBalanceApplications';
import { useDocumentDeliveryOwner, type DocumentDeliveryOperation } from '../../hooks/useDocumentDeliveryOwner';
import { apiService } from '../../services/api';
import type { PendingBalanceApplication, ReadyBalanceApplication } from '../../services/balanceApplicationAttempts';

type Props = {
  pending: PendingBalanceApplicationsView;
  context: string;
  canResolve: boolean;
  busy: boolean;
  begin: (entry: PendingBalanceApplication) => DocumentDeliveryOperation | null;
  adopt: (value: ReadyBalanceApplication, current: () => boolean) => Promise<boolean>;
};

export function BalanceApplicationRecovery({ pending, context, canResolve, busy, begin, adopt }: Props) {
  const owner = useDocumentDeliveryOwner(JSON.stringify([context, canResolve]));
  const running = useRef<DocumentDeliveryOperation | null>(null);
  const [message, setMessage] = useState<{ scope: string; requestId: string; error: boolean; text: string } | null>(null);
  const run = async (entry: PendingBalanceApplication, cancel: boolean) => {
    if (!canResolve || busy || running.current?.isCurrent()) return;
    const action = owner.begin('balance-recovery'); if (!action) return;
    const gate = begin(entry); if (!gate) { action.finish(); return; }
    running.current = action;
    const current = () => action.isCurrent() && gate.isCurrent();
    setMessage(null);
    try {
      const missing = pending.missing.has(entry.requestId);
      if (cancel && missing) return;
      const value = await (missing ? apiService.openAcknowledgedBalanceApplication(entry)
        : cancel ? apiService.cancelPendingBalanceApplication(entry) : apiService.inspectPendingBalanceApplication(entry));
      if (!current() || !apiService.canContinueBalanceApplication(value)) return;
      if (!await adopt(value, current) || !current() || !apiService.canContinueBalanceApplication(value)) return;
      if (!await apiService.acknowledgeBalanceApplication(value) || !current()) return;
      setMessage({ scope: owner.scope, requestId: entry.requestId, error: false, text: value.state === 'Cancelled'
        ? 'Попытка закрыта без зачёта. Теперь можно выполнить новый зачёт.'
        : 'Зачёт подтверждён. Оплата платежа и доступный баланс обновлены.' });
    } catch (cause) {
      if (current()) setMessage({ scope: owner.scope, requestId: entry.requestId, error: true, text: cause instanceof Error ? cause.message : 'Не удалось проверить зачёт.' });
    } finally {
      if (running.current === action) running.current = null;
      gate.finish(); action.finish();
    }
  };
  const notice = message?.scope === owner.scope && (message.error
    ? pending.entries.some(entry => entry.requestId === message.requestId) : pending.entries.length === 0) ? message : null;
  if (!pending.entries.length && !pending.error && !notice) return null;
  return (
    <div className="mb-3 space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm" role="status">
      {pending.entries.length > 0 && <p>Результат предыдущего зачёта ещё не принят. Сначала проверьте его или закройте попытку. Закрытие уже выполненного зачёта не возвращает деньги.</p>}
      {pending.entries.map(entry => (
        <div key={entry.requestId} className="flex flex-wrap items-center gap-2">
          <span>Зачёт в платёж №{entry.paymentId}</span>
          {pending.missing.has(entry.requestId) && <span>Запись закрыта в другом окне. Обновите финансовые данные проверкой.</span>}
          <button type="button" disabled={!canResolve || busy || owner.busy} onClick={() => void run(entry, false)} className="rounded border px-2 py-1">Проверить зачёт</button>
          {!pending.missing.has(entry.requestId) && <button type="button" disabled={!canResolve || busy || owner.busy} onClick={() => void run(entry, true)} className="rounded border px-2 py-1">Закрыть попытку зачёта</button>}
        </div>
      ))}
      {pending.error && <p className="text-rose-700" role="alert">{pending.error}</p>}
      {notice && <p className={notice.error ? 'text-rose-700' : undefined} role={notice.error ? 'alert' : undefined}>{notice.text}</p>}
    </div>
  );
}
