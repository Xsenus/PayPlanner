import { useCallback, useEffect, useRef, useState } from 'react';
import { Loader2, X } from 'lucide-react';
import { SystemSelect } from '../ui/SystemSelect';
import { apiService } from '../../services/api';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { usePendingBalanceApplications } from '../../hooks/usePendingBalanceApplications';
import { useToast } from '../../hooks/useToast';
import { formatCurrencySmart } from '../../utils/formatters';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { BalanceApplicationRecovery } from './BalanceApplicationRecovery';
import type { ClientBalanceTransaction, Payment } from '../../types';
import type { PendingBalanceApplication, ReadyBalanceApplication } from '../../services/balanceApplicationAttempts';

type Props = { source: Payment | null; context: string; canEdit: boolean; canViewBalance: boolean;
  isContextCurrent: () => boolean;
  onClose: () => void; onResolved: (value: ReadyBalanceApplication, current: () => boolean) => Promise<void>; formatDate: (date: string) => string };
type Data = { scope: string; targets: Payment[]; selectedId: number | null; amount: string; balance: number; loading: boolean; ready: boolean; error: string | null };
const positive = (id: number) => Number.isInteger(id) && id > 0 && id <= 2147483647;
function advanceOwner(source: Payment | null) {
  if (!source) return null;
  const client = source.clientId ?? source.client?.id ?? 0; const legal = source.legalEntityId ?? 0;
  if (positive(client) && !legal) return { kind: 'client' as const, id: client, name: source.client?.name ?? source.counterpartyName ?? 'Клиент' };
  if (positive(legal) && !client) return { kind: 'legalEntity' as const, id: legal, name: source.legalEntityName ?? source.counterpartyName ?? 'Юридическое лицо' };
  return null;
}
function outstanding(payment: Payment) {
  if (!Number.isFinite(payment.amount) || !Number.isFinite(payment.paidAmount ?? 0)) return 0;
  return Math.max(payment.amount - Math.min(payment.paidAmount ?? 0, payment.amount), 0);
}
function sameOwner(row: { clientId?: number | null; legalEntityId?: number | null }, kind: 'client' | 'legalEntity', id: number) {
  return kind === 'client' ? row.clientId === id && row.legalEntityId == null : row.legalEntityId === id && row.clientId == null;
}
function sourceAvailable(rows: ClientBalanceTransaction[], sourceId: number, kind: 'client' | 'legalEntity', id: number) {
  let available = 0; const prefix = `payment-unallocated-advance-apply:${sourceId}:`;
  for (const row of rows) {
    if (!sameOwner(row, kind, id)) continue;
    const credit = row.paymentId === sourceId && row.type === 'UnallocatedAdvance' && row.direction === 'Credit';
    const debit = row.direction === 'Debit' && ((row.type === 'CreditApplied' && row.externalKey?.startsWith(prefix))
      || (row.type === 'Refund' && row.externalKey === `payment-unallocated-advance-refund:${sourceId}`));
    if (!credit && !debit) continue;
    if (typeof row.amount !== 'number' || !Number.isFinite(row.amount) || row.amount < 0) throw new Error('Остаток выбранного аванса не подтверждён.');
    available += credit ? row.amount : -row.amount;
    if (!Number.isFinite(available)) throw new Error('Остаток выбранного аванса не подтверждён.');
  }
  return Math.max(Number(available.toFixed(2)), 0);
}

export function AdvanceAllocationModal({ source, context, canEdit, canViewBalance, isContextCurrent, onClose, onResolved, formatDate }: Props) {
  const counterparty = advanceOwner(source); const kind = counterparty?.kind ?? 'client'; const id = counterparty?.id ?? 0;
  const enabled = !!source && positive(source.id) && source.advanceReviewStatus === 'AcceptedAsAdvance' && !!counterparty && canViewBalance && isContextCurrent();
  const owner = useDocumentDeliveryOwner(JSON.stringify([context, source?.id, kind, id, enabled, canEdit]));
  const { capture, scope, busy, begin, invalidate } = owner;
  const [data, setData] = useState<Data>({ scope: '', targets: [], selectedId: null, amount: '', balance: 0, loading: false, ready: false, error: null });
  const currentData = data.scope === scope ? data : { scope, targets: [], selectedId: null, amount: '', balance: 0, loading: enabled, ready: false, error: null };
  const pending = usePendingBalanceApplications(kind, id, enabled, scope);
  const controller = useRef<AbortController | null>(null);
  const toast = useToast();
  const sourceId = source?.id ?? 0;
  const reload = useCallback(async () => {
    const read = capture(); if (!enabled || !read) return;
    controller.current?.abort(); const next = new AbortController(); controller.current = next;
    const current = () => !next.signal.aborted && read.isCurrent() && isContextCurrent();
    const request = <T,>(work: () => Promise<T>) => { if (!current()) throw new Error('Контекст аванса изменился.'); return work(); };
    setData(prev => ({ ...(prev.scope === scope ? prev : { targets: [], selectedId: null, amount: '', balance: 0 }), scope, loading: true, ready: false, error: null }));
    try {
      const options = { signal: next.signal };
      const [balance, response, transactions] = await Promise.all([
        request(() => kind === 'client' ? apiService.getClientBalance(id, options) : apiService.getLegalEntityBalance(id, options)),
        request(() => apiService.getPaymentsV2({ ...(kind === 'client' ? { clientId: id } : { legalEntityId: id }), type: 'Income',
          page: 1, pageSize: 100, sortBy: 'date', sortDir: 'asc' }, options)),
        request(() => kind === 'client' ? apiService.getClientBalanceTransactions(id, options) : apiService.getLegalEntityBalanceTransactions(id, options)),
      ]);
      if (!current()) return;
      if (!sameOwner(balance, kind, id) || typeof balance.balance !== 'number' || !Number.isFinite(balance.balance)
        || !Array.isArray(response.items) || !Array.isArray(transactions)) throw new Error('Данные выбранного аванса не подтверждены.');
      const available = Math.min(Math.max(balance.balance, 0), sourceAvailable(transactions, sourceId, kind, id));
      const targets = response.items.filter(item => positive(item.id) && item.id !== sourceId && sameOwner(item, kind, id)
        && item.type === 'Income' && item.status !== 'Completed' && item.status !== 'Cancelled' && outstanding(item) > 0.005).slice(0, 100);
      setData(prev => {
        const previous = prev.scope === scope ? prev : null;
        const selected = targets.find(item => item.id === previous?.selectedId) ?? targets[0];
        return { scope, targets, selectedId: selected?.id ?? null, balance: available, loading: false, ready: true, error: null,
          amount: previous?.amount || (selected ? String(Number(Math.min(available, outstanding(selected)).toFixed(2))) : '') };
      });
    } catch (cause) {
      if (current()) setData(prev => ({ ...prev, scope, loading: false, ready: false, error: extractDisplayErrorMessage(cause, 'Не удалось загрузить долги для распределения аванса.') }));
    }
  }, [capture, enabled, scope, kind, id, sourceId, isContextCurrent]);
  useEffect(() => { void reload(); return () => controller.current?.abort(); }, [reload]);
  const close = () => { if (!capture() || !isContextCurrent()) return; controller.current?.abort(); invalidate(); onClose(); };
  const adopt = async (value: ReadyBalanceApplication, current: () => boolean) => {
    if (!current() || !apiService.canContinueBalanceApplication(value) || value.attempt.kind !== kind || value.attempt.ownerId !== id) return false;
    pending.adopted(value);
    if (value.state === 'Committed') setData(prev => prev.scope === scope ? { ...prev, ready: false,
      targets: prev.targets.map(item => item.id === value.canonicalPayment.id ? value.canonicalPayment : item)
        .filter(item => outstanding(item) > 0.005) } : prev);
    await onResolved(value, current);
    return current() && apiService.canContinueBalanceApplication(value);
  };
  const acknowledged = async (value: ReadyBalanceApplication, current: () => boolean) => {
    if (!current()) return;
    if (value.state === 'Cancelled') { await reload(); return; }
    toast.success(`Зачёт в платеж #${value.attempt.paymentId} на сумму ${formatCurrencySmart(value.appliedAmount).full} подтверждён.`, 'Платежи'); close();
  };
  const beginRecovery = (entry: PendingBalanceApplication) => {
    if (!enabled || !canEdit || currentData.loading || busy || !isContextCurrent() || entry.kind !== kind || entry.ownerId !== id) return null;
    const action = begin('advance-recovery'); return action ? { ...action, isCurrent: () => action.isCurrent() && isContextCurrent() } : null;
  };
  const apply = async () => {
    if (!enabled || !canEdit || busy || currentData.loading || !currentData.ready || pending.blocked || !isContextCurrent()) return;
    const target = currentData.targets.find(item => item.id === currentData.selectedId);
    const amount = Number(currentData.amount.replace(/\s/g, '').replace(',', '.'));
    const maximum = target ? Math.min(currentData.balance, outstanding(target)) : 0;
    if (!target || !Number.isFinite(amount) || amount <= 0.005 || amount > maximum + 0.005) {
      setData(prev => ({ ...prev, error: !target ? 'Выберите платёж для зачёта.' : `Укажите сумму зачёта не больше ${formatCurrencySmart(maximum).full}.` })); return;
    }
    const action = begin('advance-apply'); if (!action) return;
    const current = () => action.isCurrent() && isContextCurrent();
    setData(prev => ({ ...prev, error: null }));
    try {
      const command = { paymentId: target.id, amount, sourcePaymentId: sourceId, description: `Распределение аванса по платежу #${sourceId}` };
      const value = kind === 'client' ? await apiService.applyClientBalanceGuarded(id, command) : await apiService.applyLegalEntityBalanceGuarded(id, command);
      if (!await adopt(value, current) || !current()) return;
      if (!await apiService.acknowledgeBalanceApplication(value) || !current()) return;
      await acknowledged(value, current);
    } catch (cause) {
      if (current()) setData(prev => ({ ...prev, error: extractDisplayErrorMessage(cause, 'Не удалось распределить аванс.') }));
    } finally { action.finish(); }
  };
  if (!source || !isContextCurrent()) return null;
  const disabled = !enabled || !canEdit || busy || currentData.loading || !currentData.ready || pending.blocked;
  return (
    <div className="fixed inset-0 z-[1300] flex items-center justify-center bg-slate-900/60 p-3">
      <div role="dialog" aria-modal="true" aria-labelledby="advance-allocation-title" className="max-h-[calc(100dvh-1.5rem)] w-full max-w-2xl overflow-auto rounded-xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-3 border-b border-slate-200 p-3">
          <div><h3 id="advance-allocation-title" className="text-lg font-semibold">Распределить аванс</h3>
            <p className="mt-1 text-sm text-slate-500">Авансовый платёж #{source.id}. Выберите долг контрагента для зачёта.</p></div>
          <button type="button" aria-label="Закрыть" onClick={close} className="rounded p-2"><X className="h-5 w-5" /></button>
        </div>
        <div className="space-y-3 p-3">
          <BalanceApplicationRecovery pending={pending} context={scope} canResolve={enabled && canEdit} busy={busy || currentData.loading}
            begin={beginRecovery} adopt={adopt} onAcknowledged={acknowledged} />
          {!enabled && <p role="alert" className="text-sm text-red-700">Распределение недоступно для этого платежа или текущих прав доступа.</p>}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-3"><p className="text-xs text-emerald-700">Доступный остаток аванса</p>
              <p className="mt-1 text-xl font-semibold">{currentData.loading ? 'Проверяем...' : pending.blocked ? 'Требуется проверка зачёта' : formatCurrencySmart(currentData.balance).full}</p></div>
            <div className="rounded-lg border border-slate-200 p-3"><p className="text-xs text-slate-500">Контрагент</p><p className="mt-1 text-sm font-semibold">{counterparty?.name}</p></div>
          </div>
          {currentData.loading ? <p role="status" className="flex gap-2 text-sm"><Loader2 className="h-4 w-4 animate-spin" />Загружаем открытые долги контрагента...</p>
            : !currentData.targets.length ? <p className="text-sm text-amber-800">Открытых входящих платежей с остатком долга по этому контрагенту не найдено.</p> : (
              <fieldset disabled={disabled} className="space-y-3">
                <label className="block text-sm font-medium">Долг для зачета<SystemSelect value={currentData.selectedId ?? ''} onChange={event => {
                  const target = currentData.targets.find(item => item.id === Number(event.target.value));
                  setData(prev => ({ ...prev, selectedId: target?.id ?? null, amount: target ? String(Number(Math.min(prev.balance, outstanding(target)).toFixed(2))) : '', error: null }));
                }} className="mt-1 w-full rounded-lg border px-3 py-2 text-sm">
                  {currentData.targets.map(item => <option key={item.id} value={item.id}>#{item.id} · {item.description || 'Без описания'} · остаток {formatCurrencySmart(outstanding(item)).full} · {formatDate(item.plannedDate || item.date || '')}</option>)}
                </SystemSelect></label>
                <label className="block text-sm font-medium">Сумма зачета<input inputMode="decimal" value={currentData.amount}
                  onChange={event => setData(prev => ({ ...prev, amount: event.target.value, error: null }))} className="mt-1 w-full rounded-lg border px-3 py-2 text-sm" /></label>
              </fieldset>)}
          {currentData.error && <div role="alert" className="space-y-2 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
            <p>{currentData.error}</p><button type="button" disabled={busy || currentData.loading} onClick={() => void reload()} className="rounded border px-2 py-1">Обновить долги и остаток аванса</button>
          </div>}
        </div>
        <div className="flex justify-end gap-2 border-t p-3">
          <button type="button" onClick={close} className="rounded-lg border px-3 py-2 text-sm">Отмена</button>
          <button type="button" disabled={disabled || !currentData.targets.length} onClick={() => void apply()} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white disabled:opacity-60">
            {busy && <Loader2 className="h-4 w-4 animate-spin" />}Зачесть аванс</button>
        </div>
      </div>
    </div>
  );
}
