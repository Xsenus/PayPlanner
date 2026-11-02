import { useCallback, useRef, useState } from 'react';

import { apiService } from '../../services/api';
import { useDictionarySnapshot } from '../../hooks/useDictionarySnapshot';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import type { ClientCase, CourtRecoveryOverview, CourtRecoveryPayment } from '../../types';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { exactMoneyCents } from '../../utils/lineItemMoney';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';

interface CourtRecoveryPanelProps {
  caseId: number;
  canEdit: boolean;
  canView?: boolean;
  caseItem?: Pick<ClientCase, 'id' | 'clientId' | 'legalEntityId' | 'courtAwardedAmount'>;
  actionOwner?: CaseOperationParent;
}
const money = (amount: number | null) => amount == null ? '—' : formatCurrencySmart(amount).full;

// Compare the decimal values serialized by JSON; never round a typed amount into a different amount.
function cents(value: string): bigint | null {
  const match = /^(-?)(\d{1,29})(?:\.(\d{1,2}))?$/.exec(value.replace(/^(-?)0+(?=\d)/, '$1'));
  if (!match) return null;
  const result = BigInt(match[2]) * 100n + BigInt((match[3] ?? '').padEnd(2, '0'));
  return match[1] ? -result : result;
}
const numericCents = (value: number) => typeof value === 'number' ? exactMoneyCents(value) : null;
type RecoverySnapshot = { overview: CourtRecoveryOverview; payments: Map<number, CourtRecoveryPayment>; linked: Set<number> };
function validateSnapshot(input: CourtRecoveryOverview): RecoverySnapshot {
  // MVC omits nullable properties (WhenWritingNull); keep the typed snapshot canonical.
  const value = input ? { ...input, awardedAmount: input.awardedAmount ?? null, receivedAmount: input.receivedAmount ?? null, remainingAmount: input.remainingAmount ?? null } : input;
  const fail = () => { throw new Error('Не удалось подтвердить данные взыскания. Обновите платежи.'); };
  if (!value || !Array.isArray(value.linkedPayments) || !Array.isArray(value.availablePayments) || value.availablePayments.length > 100
    || typeof value.hasCompletePaymentVisibility !== 'boolean' || typeof value.hasMoreAvailablePayments !== 'boolean'
    || [value.awardedAmount, value.receivedAmount, value.remainingAmount].some(amount => amount !== null && numericCents(amount) === null)) fail();
  const payments = new Map<number, CourtRecoveryPayment>(); const linked = new Set<number>();
  const normalizedOverview: CourtRecoveryOverview = { ...value, linkedPayments: [], availablePayments: [] };
  for (const [rows, isLinked] of [[value.linkedPayments, true], [value.availablePayments, false]] as const) {
    for (const inputRow of rows) {
      const row = inputRow ? { ...inputRow, allocatedAmount: inputRow.allocatedAmount ?? null, warning: inputRow.warning ?? null } : inputRow;
      if (!row || !Number.isSafeInteger(row.paymentId) || row.paymentId <= 0 || payments.has(row.paymentId)
        || typeof row.description !== 'string' || typeof row.date !== 'string' || !Number.isFinite(Date.parse(row.date))
        || numericCents(row.paidAmount) === null || numericCents(row.effectiveAmount) === null
        || (row.allocatedAmount !== null && numericCents(row.allocatedAmount) === null) || (row.warning !== null && typeof row.warning !== 'string')
        || (!isLinked && (row.paidAmount <= 0 || row.allocatedAmount !== null || row.effectiveAmount !== 0 || row.warning !== null))) fail();
      payments.set(row.paymentId, row);
      if (isLinked) { linked.add(row.paymentId); normalizedOverview.linkedPayments.push(row); }
      else normalizedOverview.availablePayments.push(row);
    }
  }
  return { overview: normalizedOverview, payments, linked };
}

export function CourtRecoveryPanel(props: CourtRecoveryPanelProps) {
  const { caseId, caseItem, canEdit, canView = true } = props;
  const available = Number.isSafeInteger(caseId) && caseId > 0 && canView && (!caseItem || caseItem.id === caseId);
  const owner = useNestedCaseOperationOwner(JSON.stringify(['court-recovery', caseId, caseItem?.clientId, caseItem?.legalEntityId, canEdit, canView]), props.actionOwner, available);
  return owner.active ? <OwnedCourtRecovery key={owner.scope} {...props} owner={owner} /> : null;
}
function OwnedCourtRecovery({ caseId, caseItem, canEdit, owner }: CourtRecoveryPanelProps & { owner: NestedCaseOperationOwner }) {
  const [search, setSearch] = useState('');
  const [selectedPaymentId, setSelectedPaymentId] = useState('');
  const [amount, setAmount] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [operationError, setOperationError] = useState<string | null>(null);
  const operationFailure = useRef<string | null>(null);
  const [confirmation, setConfirmation] = useState<string | null>(null);
  const appliedSearch = useRef('');
  const mutationRunning = useRef(false);
  const pendingRead = useRef<AbortSignal | null>(null);
  const readContext = JSON.stringify([owner.scope, caseItem?.courtAwardedAmount]);
  const currentContext = useRef(readContext); currentContext.current = readContext;
  const verified = useRef<{ context: string; value: RecoverySnapshot } | null>(null);
  const { capture } = owner;
  const load = useCallback(async (signal: AbortSignal) => {
    const read = capture(); if (!read) throw new StaleUiOperationError();
    pendingRead.current = signal; verified.current = null;
    const current = () => read.isCurrent() && !signal.aborted && pendingRead.current === signal && currentContext.current === readContext;
    try {
      const value = validateSnapshot(await awaitOwnedUiResult(() => apiService.getCourtRecoveries(caseId, appliedSearch.current, { signal }), current));
      if (current()) verified.current = { context: readContext, value };
      return value;
    } finally { if (pendingRead.current === signal) pendingRead.current = null; }
  }, [capture, caseId, readContext]);
  const snapshot = useDictionarySnapshot<RecoverySnapshot | null>(load, null, { ownerScope: readContext }, false);
  const currentRead = useRef(snapshot); currentRead.current = snapshot;
  const overview = snapshot.value?.overview;
  const loading = snapshot.loading;
  const error = operationError ?? snapshot.error ?? validationError;
  const readUncertain = !!snapshot.error || !!operationError || !snapshot.value;
  const saving = owner.busy;
  const fieldsDisabled = saving || loading || readUncertain;
  const choices = snapshot.value ? Array.from(snapshot.value.payments.values()) : [];
  const missingSelection = !!selectedPaymentId && !snapshot.value?.payments.has(Number(selectedPaymentId));
  const ready = () => {
    if (!canEdit || !owner.capture() || mutationRunning.current || operationFailure.current || (pendingRead.current && !pendingRead.current.aborted)
      || currentRead.current.loading || currentRead.current.error || verified.current?.context !== readContext) return null;
    return verified.current.value;
  };
  const readPayments = async (query: string) => {
    if (!owner.capture() || owner.busy || mutationRunning.current) return;
    const normalized = query.trim();
    if (normalized.length > 100) { setValidationError('Поисковый запрос не должен превышать 100 символов.'); return; }
    if (pendingRead.current && !pendingRead.current.aborted && appliedSearch.current === normalized) return;
    appliedSearch.current = normalized; operationFailure.current = null; setOperationError(null); setValidationError(null);
    await currentRead.current.refresh();
  };
  const runSearch = () => readPayments(search);
  const retry = () => readPayments(appliedSearch.current);
  const choosePayment = (raw: string) => {
    const data = ready(); if (!data) return;
    if (!raw) { setSelectedPaymentId(''); setAmount(''); setValidationError(null); return; }
    const id = Number(raw); const payment = data.payments.get(id);
    if (!payment || String(id) !== raw) { setSelectedPaymentId(''); setValidationError('Выберите платёж из актуального списка.'); return; }
    setSelectedPaymentId(raw); setAmount(String(payment.allocatedAmount ?? payment.paidAmount)); setValidationError(null);
  };
  const mutate = async (paymentId: number, parsedAmount?: number) => {
    const data = ready(); if (!data || !Number.isSafeInteger(paymentId) || paymentId <= 0 || (parsedAmount === undefined && !data.linked.has(paymentId))) return;
    const operation = owner.begin('recovery:write'); if (!operation) return;
    mutationRunning.current = true; operationFailure.current = null; setOperationError(null); setValidationError(null); setConfirmation(null);
    try {
      await awaitOwnedUiResult(() => parsedAmount === undefined ? apiService.removeCourtRecoveryPayment(caseId, paymentId) : apiService.setCourtRecoveryPayment(caseId, paymentId, parsedAmount), operation.isCurrent);
      setConfirmation(parsedAmount === undefined ? 'Связь с платежом удалена.' : 'Связь с платежом сохранена.');
      await awaitOwnedUiResult(() => currentRead.current.refresh(), operation.isCurrent);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) {
        operationFailure.current = extractDisplayErrorMessage(cause, 'Не удалось подтвердить изменение. Обновите платежи перед повтором.'); setOperationError(operationFailure.current);
        verified.current = null;
        if (isHttpErrorStatus(cause, 401) || isHttpErrorStatus(cause, 403)) { currentRead.current.updateValue(() => null); setSelectedPaymentId(''); setAmount(''); }
      }
    } finally { mutationRunning.current = false; operation.finish(); }
  };
  const save = async () => {
    const data = ready(); if (!data || data.overview.awardedAmount === null || data.overview.awardedAmount <= 0) return;
    const normalized = amount.trim().replace(',', '.'); const units = cents(normalized); const parsed = Number(normalized);
    if (units === null || units <= 0n || !/^\d+(?:\.\d{1,2})?$/.test(normalized) || numericCents(parsed) !== units) {
      setValidationError('Укажите положительную сумму с точностью до копеек без округления.'); return;
    }
    const payment = data.payments.get(Number(selectedPaymentId)); const paidUnits = payment ? numericCents(payment.paidAmount) : null;
    if (!payment || String(payment.paymentId) !== selectedPaymentId) { setValidationError('Выберите платёж из актуального списка.'); return; }
    if (paidUnits === null || units > paidUnits) { setValidationError('Сумма взыскания не может превышать оплаченную часть выбранного платежа.'); return; }
    await mutate(payment.paymentId, parsed);
  };
  const remove = (paymentId: number) => mutate(paymentId);
  return <section className="min-w-0 max-w-full rounded-xl border border-blue-200 bg-blue-50/30 p-3 text-sm text-slate-700">
    <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="font-semibold text-slate-900">Фактически получено по решению суда</h3><button type="button" disabled={saving} onClick={() => void retry()} className="text-xs font-semibold text-blue-700 disabled:opacity-50">Обновить платежи</button></div>
    <p className="mt-1 text-xs text-slate-500">Связывайте только подтверждённую часть уже оплаченного платежа. Новый платёж не создаётся, выручка и маржа не пересчитываются.</p>
    {loading ? <p className="mt-2 text-xs">Загружаем платежи…</p> : null}
    {overview ? <>
      <div className="mt-2 flex flex-wrap gap-3 text-xs">
        <span>Присуждено: <strong>{money(overview.awardedAmount)}</strong></span>
        <span>Подтверждено платежами: <strong>{money(overview.hasCompletePaymentVisibility ? overview.receivedAmount : null)}</strong></span>
        <span>Осталось: <strong>{money(overview.hasCompletePaymentVisibility ? overview.remainingAmount : null)}</strong></span>
      </div>
      {!overview.hasCompletePaymentVisibility ? <p className="mt-2 text-xs text-amber-700">Показаны только доступные вам платежи. Общая сумма поступлений и остаток скрыты, чтобы не показывать неполный итог.</p> : null}
      {overview.awardedAmount == null ? <p className="mt-2 text-amber-700">Сначала сохраните присуждённую сумму в карточке дела.</p> : null}
      {overview.linkedPayments.length > 0 ? <ul className="mt-3 space-y-2">{overview.linkedPayments.map((payment) =>
        <li key={payment.paymentId} className="rounded-lg border border-slate-200 bg-white p-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="min-w-0 flex-1 break-words"><strong>Платёж #{payment.paymentId}</strong> · {payment.description || 'Без описания'} · {money(payment.effectiveAmount)}</span>
            {canEdit ? <button type="button" onClick={() => void remove(payment.paymentId)} disabled={fieldsDisabled} className="text-xs font-medium text-rose-700 disabled:opacity-50">Убрать связь</button> : null}
          </div>
          {payment.warning ? <p role="alert" className="mt-1 text-xs text-rose-700">{payment.warning}</p> : null}
        </li>)}
      </ul> : <p className="mt-2 text-xs text-slate-500">Платежи пока не отмечены как взыскание.</p>}
      {canEdit && overview.awardedAmount != null && overview.awardedAmount > 0 ? <div className="mt-3 space-y-2 border-t border-blue-100 pt-3">
        <div className="flex flex-wrap gap-2">
          <input aria-label="Поиск платежа для взыскания" value={search} disabled={saving} maxLength={100} onChange={(event) => { if (owner.capture() && !mutationRunning.current) setSearch(event.target.value); }} onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); void runSearch(); } }} placeholder="Поиск по описанию или номеру" className="min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-2 py-1.5" />
          <button type="button" onClick={() => void runSearch()} disabled={saving} className="rounded-lg border border-blue-200 bg-white px-2 py-1.5 font-medium text-blue-700">Найти</button>
        </div>
        {overview.hasMoreAvailablePayments ? <p className="text-xs text-slate-500">Показаны последние 100 платежей; найдите более старый через поиск.</p> : null}
        <div className="flex flex-wrap gap-2">
          <select aria-label="Платёж для взыскания" value={selectedPaymentId} onChange={(event) => choosePayment(event.target.value)} disabled={fieldsDisabled} className="min-w-0 basis-full flex-1 rounded-lg sm:basis-0 border border-slate-200 bg-white px-2 py-1.5">
            <option value="">Выберите оплаченный приходный платёж</option>
            {missingSelection ? <option value={selectedPaymentId} disabled>#{selectedPaymentId} — отсутствует в текущем списке</option> : null}
            {choices.map((payment) => <option key={payment.paymentId} value={payment.paymentId}>#{payment.paymentId} · {payment.description || 'Без описания'} · оплачено {money(payment.paidAmount)}</option>)}
          </select>
          <input aria-label="Сумма платежа в счёт взыскания" value={amount} onChange={(event) => { if (ready()) { setAmount(event.target.value); setValidationError(null); } }} inputMode="decimal" placeholder="Сумма, ₽" disabled={fieldsDisabled} className="w-36 rounded-lg border border-slate-200 bg-white px-2 py-1.5" />
          <button type="button" onClick={() => void save()} disabled={fieldsDisabled || !selectedPaymentId || missingSelection} className="rounded-lg bg-blue-700 px-3 py-1.5 font-medium text-white disabled:opacity-50">Сохранить связь</button>
        </div>
        {missingSelection ? <p className="text-xs text-amber-800">Выбранный платёж отсутствует в актуальном списке. Обновите поиск или выберите доступный платёж; введённая сумма сохранена.</p> : null}
      </div> : null}
    </> : null}
    {confirmation ? <p role="status" className="mt-2 text-xs text-emerald-800">{confirmation}{snapshot.error ? " Обновите платежи, чтобы увидеть результат." : ""}</p> : null}
    {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{extractDisplayErrorMessage(error, 'Не удалось загрузить взыскания.')}{operationError || snapshot.error ? <> <button type="button" disabled={saving} onClick={() => void retry()} className="font-semibold underline">Повторить загрузку платежей</button></> : null}</p> : null}
  </section>;
}
