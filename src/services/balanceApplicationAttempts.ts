import { assertAuthSessionIdentity, captureAuthSessionIdentity, type AuthSessionIdentity } from './authService';
import { assertPaymentCreateOwner, paymentCreateOwner, PAYMENT_CREATE_CHANGED, type PaymentCreateOwner } from './paymentCreateAttempts';
import { createClientRequestId } from '../utils/clientRequestId';
import { parseBalanceApplicationInspection, parseManagedBalanceApplicationResult, prepareBalanceApplicationCommand, type BalanceApplicationOwnerKind } from './balanceApplicationInspection';
import type { BalanceApplicationCommand, BalanceApplicationInspection, ClientBalanceSummary, ManagedBalanceApplicationResult, Payment } from '../types';

export type PendingBalanceApplication = Readonly<{
  version: 'balance-application-v1'; actorId: number; apiScope: string;
  kind: BalanceApplicationOwnerKind; ownerId: number; paymentId: number;
  requestId: string; commandSha256: string; createdAtUtc: string;
}>;
export type BalanceApplicationPorts = {
  assertActor: (actor: PaymentCreateOwner) => void;
  apply: (entry: PendingBalanceApplication, command: Readonly<Required<BalanceApplicationCommand>>, actor: PaymentCreateOwner, signal: AbortSignal) => Promise<ManagedBalanceApplicationResult>;
  inspect: (entry: PendingBalanceApplication, actor: PaymentCreateOwner, signal: AbortSignal) => Promise<BalanceApplicationInspection>;
  cancel: (entry: PendingBalanceApplication, actor: PaymentCreateOwner, signal: AbortSignal) => Promise<BalanceApplicationInspection>;
  payment: (entry: PendingBalanceApplication, actor: PaymentCreateOwner, signal: AbortSignal) => Promise<Payment>;
  balance: (entry: PendingBalanceApplication, actor: PaymentCreateOwner, signal: AbortSignal) => Promise<ClientBalanceSummary>;
};
export type CommittedBalanceApplication = Readonly<ManagedBalanceApplicationResult & {
  state: 'Committed'; attempt: PendingBalanceApplication; canonicalPayment: Payment; operationBalance: number;
}>;
export type CancelledBalanceApplication = Readonly<{ state: 'Cancelled'; requestId: string; attempt: PendingBalanceApplication }>;
export type ReadyBalanceApplication = CommittedBalanceApplication | CancelledBalanceApplication;

export const BALANCE_APPLICATION_CHANGED = 'payplanner:balance-application-changed';
export const BALANCE_APPLICATION_DEADLINE_MS = 45_000;
const prefix = 'pp.balance-application.v1:';
const uuid = /^(?!00000000-0000-0000-0000-000000000000$)[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
const fields = ['version', 'actorId', 'apiScope', 'kind', 'ownerId', 'paymentId', 'requestId', 'commandSha256', 'createdAtUtc'] as const;
type Guard = BalanceApplicationPorts['assertActor'];
type Receipt = { actor: PaymentCreateOwner; session: AuthSessionIdentity; entry: PendingBalanceApplication; guard: Guard; acknowledged: boolean };
type Flow = { actor: PaymentCreateOwner; session: AuthSessionIdentity; ports: BalanceApplicationPorts; controller: AbortController; expectAbsent?: boolean;
  entry?: PendingBalanceApplication; live: boolean; financialStarted: boolean; committed: boolean; assert: () => void; fail: (error: unknown) => void };
const receipts = new WeakMap<ReadyBalanceApplication, Receipt>();
const inFlight = new Map<string, Flow>();
let listening = false;

export class BalanceApplicationRecoveryError extends Error {
  constructor(public readonly attempt: PendingBalanceApplication, public readonly originalError?: unknown, committed = false) {
    super(committed ? 'Зачёт подтверждён, но актуальные данные ещё не загружены. Повторите проверку.'
      : 'Результат зачёта ещё не подтверждён. Проверьте его или закройте попытку перед новым зачётом.');
  }
}
export class BalanceApplicationBusyError extends Error {
  constructor() { super('Эта операция сейчас выполняется или проверяется в другой вкладке.'); }
}
export class BalanceApplicationRecordChangedError extends Error {
  constructor() { super('Запись зачёта уже закрыта или изменилась в другой вкладке. Обновите проверку.'); }
}
function storageError() { return new Error('Локальная запись зачёта недоступна или повреждена. Новое списание приостановлено.'); }
function positive(value: unknown): value is number { return typeof value === 'number' && Number.isInteger(value) && value > 0 && value <= 2147483647; }
function scope(actor: Pick<PaymentCreateOwner, 'actorId' | 'apiScope'>) {
  const url = new URL(actor.apiScope, window.location.href);
  if (actor.apiScope.length > 2048 || url.username || url.password || url.search || url.hash || !['http:', 'https:'].includes(url.protocol))
    throw new Error('Защищённый зачёт недоступен при текущем подключении.');
  return `${prefix}${encodeURIComponent(actor.apiScope)}:${actor.actorId}:`;
}
function targetKey(actor: Pick<PaymentCreateOwner, 'actorId' | 'apiScope'>, kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number) {
  if (!['client', 'legalEntity'].includes(kind) || !positive(ownerId) || !positive(paymentId)) throw new Error('Не выбран сохранённый контрагент или платёж.');
  return `${scope(actor)}${kind}:${ownerId}:${paymentId}`;
}
function entryKey(entry: PendingBalanceApplication) { return targetKey(entry, entry.kind, entry.ownerId, entry.paymentId); }
function assertActor(actor: PaymentCreateOwner, guard: Guard, session?: AuthSessionIdentity) {
  assertPaymentCreateOwner(actor); guard(actor); if (session) assertAuthSessionIdentity(session);
}
function parseEntry(raw: string, name: string, actor: PaymentCreateOwner): PendingBalanceApplication {
  try {
    if (raw.length > 4096) throw storageError();
    const value = JSON.parse(raw) as PendingBalanceApplication;
    if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).length !== fields.length
      || Object.keys(value).some(field => !fields.includes(field as typeof fields[number]))
      || value.version !== 'balance-application-v1' || value.actorId !== actor.actorId || value.apiScope !== actor.apiScope
      || !positive(value.actorId) || value.apiScope.length > 2048 || typeof value.requestId !== 'string' || value.requestId.length !== 36 || !uuid.test(value.requestId)
      || typeof value.commandSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(value.commandSha256)
      || typeof value.createdAtUtc !== 'string' || !Number.isFinite(Date.parse(value.createdAtUtc))
      || new Date(value.createdAtUtc).toISOString() !== value.createdAtUtc || entryKey(value) !== name) throw storageError();
    return Object.freeze({ ...value, requestId: value.requestId.toLowerCase() });
  } catch { throw storageError(); }
}
function readEntry(name: string, actor: PaymentCreateOwner): PendingBalanceApplication | null {
  try { const raw = localStorage.getItem(name); return raw === null ? null : parseEntry(raw, name, actor); }
  catch { throw storageError(); }
}
function sameEntry(a: PendingBalanceApplication, b: PendingBalanceApplication) { return fields.every(field => a[field] === b[field]); }
function requireEntry(entry: PendingBalanceApplication, actor: PaymentCreateOwner) {
  if (entry.actorId !== actor.actorId || entry.apiScope !== actor.apiScope) throw new BalanceApplicationRecordChangedError();
  const current = readEntry(entryKey(entry), actor);
  if (!current || !sameEntry(current, entry)) throw new BalanceApplicationRecordChangedError();
  return current;
}
function changed() { window.dispatchEvent(new Event(BALANCE_APPLICATION_CHANGED)); }

export function pendingBalanceApplications(guard: Guard): PendingBalanceApplication[] {
  const actor = paymentCreateOwner(); assertActor(actor, guard);
  try {
    if (localStorage.length > 4096) throw storageError();
    const keyPrefix = scope(actor);
    const entries: PendingBalanceApplication[] = [];
    for (let index = 0; index < localStorage.length; index += 1) {
      const name = localStorage.key(index); if (!name?.startsWith(keyPrefix)) continue;
      const value = readEntry(name, actor); if (!value || entries.length >= 64) throw storageError(); entries.push(value);
    }
    return entries;
  } catch {
    assertActor(actor, guard); throw storageError();
  }
}
export function pendingBalanceApplication(kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number, guard: Guard): PendingBalanceApplication | null {
  const actor = paymentCreateOwner(); assertActor(actor, guard);
  return readEntry(targetKey(actor, kind, ownerId, paymentId), actor);
}
export function balanceApplicationInFlight(kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number, guard: Guard): boolean {
  const actor = paymentCreateOwner(); assertActor(actor, guard);
  return inFlight.has(targetKey(actor, kind, ownerId, paymentId));
}
async function mutation<T>(actor: PaymentCreateOwner, guard: Guard, action: () => T): Promise<T> {
  if (!navigator.locks?.request) throw new Error('Браузер не поддерживает безопасную проверку зачёта.');
  return navigator.locks.request(`${scope(actor)}mutation`, { mode: 'exclusive', ifAvailable: true }, lock => {
    if (!lock) throw new BalanceApplicationBusyError(); assertActor(actor, guard); return action();
  });
}
function ownerChanged() {
  for (const flow of inFlight.values()) {
    try { assertActor(flow.actor, flow.ports.assertActor, flow.session); }
    catch (error) { flow.fail(error); }
  }
}
function syncListener() {
  if (inFlight.size && !listening) { window.addEventListener(PAYMENT_CREATE_CHANGED, ownerChanged); listening = true; }
  else if (!inFlight.size && listening) { window.removeEventListener(PAYMENT_CREATE_CHANGED, ownerChanged); listening = false; }
}
function bounded<T>(name: string, actor: PaymentCreateOwner, ports: BalanceApplicationPorts, work: (flow: Flow) => Promise<T>): Promise<T> {
  const controller = new AbortController(); const session = captureAuthSessionIdentity();
  const deadline = Date.now() + BALANCE_APPLICATION_DEADLINE_MS;
  let rejectBound!: (error: unknown) => void;
  const timeout = () => new Error('Проверка зачёта заняла слишком много времени. Проверьте результат перед новым действием.');
  const flow: Flow = { actor, session, ports, controller, live: true, financialStarted: false, committed: false,
    assert: () => {
      assertActor(actor, ports.assertActor, session);
      if (!flow.live || controller.signal.aborted || Date.now() >= deadline) throw timeout();
      if (flow.entry) {
        if (flow.expectAbsent) { if (readEntry(entryKey(flow.entry), actor)) throw new BalanceApplicationRecordChangedError(); }
        else requireEntry(flow.entry, actor);
      }
    },
    fail: error => { if (flow.live) { flow.live = false; controller.abort(); rejectBound(error); } },
  };
  let timer: ReturnType<typeof setTimeout>;
  const pending = new Promise<T>((resolve, reject) => {
    rejectBound = reject; inFlight.set(name, flow); syncListener();
    timer = setTimeout(() => flow.fail(timeout()), BALANCE_APPLICATION_DEADLINE_MS);
    changed();
    void Promise.resolve().then(() => { flow.assert(); return work(flow); }).then(value => {
      try { flow.assert(); resolve(value); } catch (error) { reject(error); }
    }, reject);
  });
  return pending.catch(error => {
    assertActor(actor, ports.assertActor, session);
    if (error instanceof BalanceApplicationRecoveryError || error instanceof BalanceApplicationRecordChangedError) throw error;
    if (flow.entry && flow.financialStarted) throw new BalanceApplicationRecoveryError(flow.entry, error, flow.committed);
    throw error;
  }).finally(() => {
    flow.live = false; controller.abort(); clearTimeout(timer);
    if (inFlight.get(name) === flow) inFlight.delete(name); syncListener();
  });
}
async function ownedFlow<T>(kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number, ports: BalanceApplicationPorts,
  work: (flow: Flow) => Promise<T>, actor = paymentCreateOwner()): Promise<T> {
  assertActor(actor, ports.assertActor);
  if (!navigator.locks?.request) throw new Error('Браузер не поддерживает безопасный зачёт.');
  const name = targetKey(actor, kind, ownerId, paymentId);
  let started = false;
  const result = navigator.locks.request(`${name}:flow`, { mode: 'exclusive', ifAvailable: true }, lock => {
    if (!lock || inFlight.has(name) || inFlight.size >= 64) throw new BalanceApplicationBusyError();
    assertActor(actor, ports.assertActor); started = true; return bounded(name, actor, ports, work);
  });
  return result.finally(() => { if (started) changed(); });
}
function ownerMatches(value: { clientId?: number | null; legalEntityId?: number | null }, entry: PendingBalanceApplication) {
  return entry.kind === 'client' ? value.clientId === entry.ownerId && value.legalEntityId == null
    : value.legalEntityId === entry.ownerId && value.clientId == null;
}
async function committed(flow: Flow, entry: PendingBalanceApplication, operation: ManagedBalanceApplicationResult): Promise<CommittedBalanceApplication> {
  flow.committed = true; flow.assert();
  const [paymentResponse, balanceResponse] = await Promise.all([
    Promise.resolve().then(() => { flow.assert(); return flow.ports.payment(entry, flow.actor, flow.controller.signal); }),
    Promise.resolve().then(() => { flow.assert(); return flow.ports.balance(entry, flow.actor, flow.controller.signal); }),
  ]);
  flow.assert();
  const payment = paymentResponse && Object.freeze({ ...paymentResponse });
  const balance = balanceResponse && { clientId: balanceResponse.clientId, legalEntityId: balanceResponse.legalEntityId, balance: balanceResponse.balance };
  if (!payment || payment.id !== entry.paymentId || !ownerMatches(payment, entry)
    || typeof payment.amount !== 'number' || !Number.isFinite(payment.amount) || payment.amount < 0
    || typeof payment.paidAmount !== 'number' || !Number.isFinite(payment.paidAmount) || payment.paidAmount < 0
    || typeof payment.isPaid !== 'boolean' || !['Pending', 'Completed', 'Overdue', 'Cancelled', 'Processing'].includes(payment.status)
    || !balance || !ownerMatches(balance, entry) || typeof balance.balance !== 'number' || !Number.isFinite(balance.balance))
    throw new Error('Актуальные данные зачёта не подтверждены. Повторите проверку.');
  flow.assert();
  const value: CommittedBalanceApplication = Object.freeze({ ...operation, state: 'Committed', attempt: entry,
    operationBalance: operation.balance, balance: balance.balance, canonicalPayment: payment });
  receipts.set(value, { actor: flow.actor, session: flow.session, entry, guard: flow.ports.assertActor, acknowledged: flow.expectAbsent === true });
  return value;
}
function cancelled(flow: Flow, entry: PendingBalanceApplication): CancelledBalanceApplication {
  flow.assert();
  const value: CancelledBalanceApplication = Object.freeze({ state: 'Cancelled', requestId: entry.requestId, attempt: entry });
  receipts.set(value, { actor: flow.actor, session: flow.session, entry, guard: flow.ports.assertActor, acknowledged: flow.expectAbsent === true });
  return value;
}

export async function applyBalanceGuarded(kind: BalanceApplicationOwnerKind, ownerId: number, payload: BalanceApplicationCommand,
  ports: BalanceApplicationPorts): Promise<CommittedBalanceApplication> {
  const actor = paymentCreateOwner(); assertActor(actor, ports.assertActor);
  const command = prepareBalanceApplicationCommand(payload);
  return ownedFlow(kind, ownerId, command.paymentId, ports, async flow => {
    const name = targetKey(flow.actor, kind, ownerId, command.paymentId);
    const existing = readEntry(name, flow.actor);
    if (existing) throw new BalanceApplicationRecoveryError(existing);
    if (!globalThis.crypto?.subtle?.digest) throw new Error('Браузер не поддерживает безопасное подтверждение зачёта.');
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(command)));
    flow.assert();
    const candidate: PendingBalanceApplication = Object.freeze({ version: 'balance-application-v1', actorId: flow.actor.actorId,
      apiScope: flow.actor.apiScope, kind, ownerId, paymentId: command.paymentId, requestId: createClientRequestId(),
      commandSha256: Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join(''), createdAtUtc: new Date().toISOString() });
    const entry = parseEntry(JSON.stringify(candidate), name, flow.actor);
    await mutation(flow.actor, ports.assertActor, () => {
      flow.assert(); if (readEntry(name, flow.actor)) throw new BalanceApplicationRecordChangedError();
      if (pendingBalanceApplications(ports.assertActor).length >= 64) throw storageError();
      try { localStorage.setItem(name, JSON.stringify(entry)); } catch { throw storageError(); }
      const saved = readEntry(name, flow.actor); if (!saved || !sameEntry(saved, entry)) throw storageError();
      flow.entry = entry;
    });
    changed(); flow.assert(); flow.financialStarted = true;
    const result = parseManagedBalanceApplicationResult(await ports.apply(entry, command, flow.actor, flow.controller.signal), entry.requestId);
    flow.assert(); return committed(flow, entry, result);
  }, actor);
}
async function resolveBalance(entry: PendingBalanceApplication, ports: BalanceApplicationPorts, cancel: boolean, absent = false): Promise<ReadyBalanceApplication> {
  const actor = paymentCreateOwner(); assertActor(actor, ports.assertActor);
  const captured = parseEntry(JSON.stringify(entry), entryKey(entry), actor);
  return ownedFlow(captured.kind, captured.ownerId, captured.paymentId, ports, async flow => {
    flow.entry = captured; flow.expectAbsent = absent; flow.assert(); flow.financialStarted = true;
    const raw = await (cancel ? ports.cancel(captured, flow.actor, flow.controller.signal) : ports.inspect(captured, flow.actor, flow.controller.signal));
    flow.assert();
    const status = parseBalanceApplicationInspection(raw, captured.requestId, captured.paymentId);
    if (status.state === 'Unconfirmed') throw new BalanceApplicationRecoveryError(captured);
    return status.state === 'Cancelled' ? cancelled(flow, captured) : committed(flow, captured, status.result);
  }, actor);
}
export function inspectBalanceApplication(entry: PendingBalanceApplication, ports: BalanceApplicationPorts) { return resolveBalance(entry, ports, false); }
export function cancelBalanceApplication(entry: PendingBalanceApplication, ports: BalanceApplicationPorts) { return resolveBalance(entry, ports, true); }
// An already open editor must re-read money after another window removes its receipt.
// This path is GET-only, requires the exact record to remain absent and never creates a new key.
export function openAcknowledgedBalanceApplication(entry: PendingBalanceApplication, ports: BalanceApplicationPorts) { return resolveBalance(entry, ports, false, true); }

export function canContinueBalanceApplication(value: ReadyBalanceApplication): boolean {
  const receipt = receipts.get(value); if (!receipt) return false;
  try {
    assertActor(receipt.actor, receipt.guard, receipt.session);
    const current = readEntry(entryKey(receipt.entry), receipt.actor);
    return receipt.acknowledged ? current === null && !inFlight.has(entryKey(receipt.entry)) : current !== null && sameEntry(current, receipt.entry);
  } catch { return false; }
}
export async function acknowledgeBalanceApplication(value: ReadyBalanceApplication): Promise<boolean> {
  const receipt = receipts.get(value); if (!receipt || !canContinueBalanceApplication(value)) return false;
  if (receipt.acknowledged) return true;
  const removed = await mutation(receipt.actor, receipt.guard, () => {
    assertActor(receipt.actor, receipt.guard, receipt.session);
    if (!canContinueBalanceApplication(value)) return false;
    if (receipt.acknowledged) return true;
    try { localStorage.removeItem(entryKey(receipt.entry)); } catch { throw storageError(); }
    if (readEntry(entryKey(receipt.entry), receipt.actor) !== null) throw storageError();
    receipt.acknowledged = true; return true;
  });
  if (removed) changed(); return removed;
}
