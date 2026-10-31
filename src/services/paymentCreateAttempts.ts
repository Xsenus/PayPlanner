import { getAuthSessionEpoch } from './authService';
import type { Payment, PaymentPayload } from '../types';

export type PaymentCreateContext = { operation: 'primary'; primaryPaymentId?: never; sourceConfirmationToken?: never }
  | { operation: 'remainder'; primaryPaymentId: number; sourceConfirmationToken: string };
export function isPaymentSourceConfirmationToken(value: unknown): value is string {
  return typeof value === 'string' && /^remainder-v2:[0-9A-F]{64}$/.test(value);
}
export type PaymentCreateOwner = Readonly<{ actorId: number; apiScope: string; generation: number; sessionEpoch: string }>;
export type PaymentAttemptState = {
  attemptId: string; state: 'Draft' | 'Committed' | 'Cancelled';
  operation: 'primary' | 'remainder'; sellerCompanyId: number | null; sellerBranchId: number | null;
  primaryPaymentId: number | null; createdAtUtc: string; expiresAtUtc: string;
  canonicalPaymentId: number | null; identityVersion: 'payment-create-v1';
};
export type PendingPaymentCreate = Omit<PaymentAttemptState, 'state'> & {
  actorId: number; apiScope: string; phase: 'dispatched' | 'committed';
};
export type PaymentCancelOutcome = { attemptId: string; state: 'Cancelled' | 'Committed'; canonicalPaymentId: number | null };
export type PaymentCreatePorts = {
  register: (body: Pick<PaymentAttemptState, 'operation' | 'sellerCompanyId' | 'sellerBranchId' | 'primaryPaymentId'>, owner: PaymentCreateOwner, signal: AbortSignal) => Promise<PaymentAttemptState>;
  commit: (id: string, command: string, owner: PaymentCreateOwner, signal: AbortSignal) => Promise<Payment>;
  get: (id: string, owner: PaymentCreateOwner, signal: AbortSignal) => Promise<PaymentAttemptState>;
  cancel: (id: string, owner: PaymentCreateOwner, signal: AbortSignal) => Promise<PaymentCancelOutcome>;
  payment: (id: number, owner: PaymentCreateOwner, signal: AbortSignal) => Promise<Payment>;
};

export const PAYMENT_CREATE_CHANGED = 'payplanner:payment-create-changed';
const prefix = 'pp.payment-create.v1:';
const uuid = /^(?!00000000-0000-0000-0000-000000000000$)[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
let owner: PaymentCreateOwner | null = null;
let generation = 0;
export const PAYMENT_ATTEMPT_DEADLINE_MS = 45_000;
type AttemptFlow = { controller: AbortController; live: boolean; disposition: 'active' | 'cancelled' | 'adopted';
  adoptedPayment?: Payment; entry?: PendingPaymentCreate; assert: () => void; fail: (error: Error) => void };
type Receipt = { entry: PendingPaymentCreate; owner: PaymentCreateOwner; payment: Payment; adopted: boolean };
const inFlight = new Map<string, AttemptFlow>();
const receipts = new WeakMap<Payment, Receipt>();
const receiptsById = new Map<string, Receipt>();

export class PaymentCreateOwnerChangedError extends Error {
  constructor() { super('Пользователь изменился. Проверьте сохранение в своей учётной записи.'); }
}
export class PaymentCreateCancelledError extends Error {
  constructor() { super('Незавершённая попытка добавления отменена.'); }
}
export class PaymentCreateAdoptedError extends Error {
  constructor(public readonly payment: Payment) { super('Подтверждённый платёж уже открыт.'); }
}
export class PaymentCreateRecoveryError extends Error {
  constructor(public readonly attempt: PendingPaymentCreate, public readonly startedHere: boolean,
    public readonly originalError?: unknown) {
    super('Результат создания платежа ещё не подтверждён. Проверьте сохранение перед новым добавлением.');
  }
}
function changed() { window.dispatchEvent(new Event(PAYMENT_CREATE_CHANGED)); }
function positive(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 && value <= 2147483647;
}
function scope(value: Pick<PaymentCreateOwner, 'actorId' | 'apiScope'>) {
  return `${prefix}${encodeURIComponent(value.apiScope)}:${value.actorId}:`;
}
function key(value: PendingPaymentCreate) { return `${scope(value)}${value.attemptId}`; }

// Bound by AuthProvider from the already loaded authenticated user, never GET /me per Create.
// Re-entering A after B has a new generation, even though A's durable records remain recoverable.
export function bindPaymentCreateOwner(actorId: number | null, apiScope: string): void {
  const nextId = positive(actorId) ? actorId : null;
  let sessionEpoch: string | null = null;
  try { sessionEpoch = getAuthSessionEpoch(); } catch { /* Other app functions remain available; financial Create fails closed. */ }
  if (owner?.actorId === nextId && owner.apiScope === apiScope && owner.sessionEpoch === sessionEpoch) return;
  owner = nextId === null || sessionEpoch === null ? null : Object.freeze({ actorId: nextId, apiScope, generation: ++generation, sessionEpoch });
  if (nextId === null) generation += 1;
  for (const [id, receipt] of receiptsById) {
    if (!owner || receipt.owner.generation !== owner.generation) receiptsById.delete(id);
  }
  changed();
}
export function paymentCreateSessionVersion(): string {
  try { return `${generation}:${getAuthSessionEpoch() ?? 'signed-out'}`; }
  catch { return `${generation}:unavailable`; }
}
export function paymentCreateOwner(): PaymentCreateOwner {
  if (!owner || !isPaymentCreateOwnerCurrent(owner)) throw new PaymentCreateOwnerChangedError();
  return owner;
}
export function isPaymentCreateOwnerCurrent(expected: PaymentCreateOwner): boolean {
  try {
    return owner?.generation === expected.generation && owner.actorId === expected.actorId
      && owner.apiScope === expected.apiScope && expected.sessionEpoch === getAuthSessionEpoch();
  } catch { return false; }
}
export function assertPaymentCreateOwner(expected: PaymentCreateOwner): void {
  if (!isPaymentCreateOwnerCurrent(expected)) throw new PaymentCreateOwnerChangedError();
}
export function paymentCreateTokenMatches(expected: PaymentCreateOwner, token: string | null): boolean {
  if (!isPaymentCreateOwnerCurrent(expected)) return false;
  try {
    const part = token?.split('.')[1];
    if (!part) return false;
    const base64 = part.replace(/-/g, '+').replace(/_/g, '/');
    const claims = JSON.parse(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))) as Record<string, unknown>;
    const id = claims['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] ?? claims.nameid ?? claims.sub;
    return String(id) === String(expected.actorId);
  } catch { return false; }
}

// PaymentPayload is deliberately wider than the public creation command. Pick only these 38
// scalar fields; do not serialize response snapshots, navigation properties or derived totals.
export const PAYMENT_CREATE_FIELDS = [
  'date', 'plannedDate', 'amount', 'paidAmount', 'type', 'status', 'description', 'isPaid', 'notes',
  'systemNotes', 'rescheduleCount', 'paidDate', 'lastPaymentDate', 'advanceReviewStatus',
  'advanceReviewReason', 'clientId', 'legalEntityId', 'expenseClientId', 'expenseLegalEntityId',
  'contractId', 'clientCaseId', 'contractorUserId', 'dealTypeId', 'incomeTypeId', 'serviceId',
  'paymentSourceId', 'paymentStatusId', 'invoiceId', 'paymentOrderNumber', 'paymentDocumentDate',
  'account', 'accountDate', 'overpaymentHandling', 'overpaymentTargetPaymentId', 'overpaymentComment',
  'sellerCompanyId', 'sellerBranchId', 'documentKind',
] as const;
export function serializePaymentCreate(payment: PaymentPayload): string {
  const input = payment as unknown as Record<string, unknown>;
  const result: Record<string, string | number | boolean | null> = {};
  for (const name of PAYMENT_CREATE_FIELDS) {
    const value = input[name];
    if (value === undefined) {
      if (PAYMENT_CREATE_FIELDS.indexOf(name) < 11) throw new Error(`Не заполнено поле платежа: ${name}.`);
      continue;
    }
    const required = PAYMENT_CREATE_FIELDS.indexOf(name) < 11;
    const id = name.endsWith('Id');
    const number = name === 'amount' || name === 'paidAmount' || name === 'rescheduleCount';
    const valid = value === null ? !required && name !== 'advanceReviewStatus' && name !== 'documentKind'
      : id ? positive(value)
      : number ? typeof value === 'number' && Number.isFinite(value)
        && (name !== 'rescheduleCount' || (Number.isInteger(value) && value >= -2147483648 && value <= 2147483647))
      : name === 'isPaid' ? typeof value === 'boolean'
      : typeof value === 'string';
    if (!valid || (name === 'documentKind' && value !== 'Payment')) throw new Error(`Недопустимое поле платежа: ${name}.`);
    result[name] = value as string | number | boolean | null;
  }
  // No monetary/date/default normalization. The exact serialized command is sent once.
  return JSON.stringify(result);
}

function validateState(value: PaymentAttemptState, relation?: Pick<PaymentAttemptState,
  'attemptId' | 'operation' | 'primaryPaymentId' | 'sellerCompanyId' | 'sellerBranchId'>): PaymentAttemptState {
  if (!value || !uuid.test(value.attemptId) || !['Draft', 'Committed', 'Cancelled'].includes(value.state)
    || !['primary', 'remainder'].includes(value.operation) || value.identityVersion !== 'payment-create-v1'
    || ![value.sellerCompanyId, value.sellerBranchId].every((id) => id === null || positive(id))
    || (value.operation === 'primary' ? value.primaryPaymentId !== null : !positive(value.primaryPaymentId))
    || !Number.isFinite(Date.parse(value.createdAtUtc)) || !Number.isFinite(Date.parse(value.expiresAtUtc))
    || (value.state === 'Committed' ? !positive(value.canonicalPaymentId) : value.canonicalPaymentId !== null)
    || (value.state === 'Committed' && value.operation === 'remainder' && value.canonicalPaymentId === value.primaryPaymentId)
    || (relation && (value.attemptId !== relation.attemptId || value.operation !== relation.operation
      || value.primaryPaymentId !== relation.primaryPaymentId || value.sellerCompanyId !== relation.sellerCompanyId
      || value.sellerBranchId !== relation.sellerBranchId))) throw new Error('Сервер не подтвердил состояние сохранения.');
  return value;
}
function requirePayment(value: Payment, id?: number): Payment {
  if (!value || !positive(value.id) || (id !== undefined && value.id !== id)) throw new Error('Сервер не подтвердил сохранённый платёж.');
  return value;
}
function persist(value: PendingPaymentCreate) {
  const raw = JSON.stringify(value);
  localStorage.setItem(key(value), raw);
  if (localStorage.getItem(key(value)) !== raw) throw new Error('Не удалось подтвердить локальную запись сохранения. Платёж не отправлен.');
  changed();
}
function remove(value: PendingPaymentCreate) {
  localStorage.removeItem(key(value));
  if (localStorage.getItem(key(value)) !== null) throw new Error('Не удалось закрыть локальную запись сохранения.');
  changed();
}
export function pendingPaymentCreates(): PendingPaymentCreate[] {
  if (!owner || !isPaymentCreateOwnerCurrent(owner)) return [];
  const current = owner;
  // Another tab may have adopted a receipt. Retain the disposition on its WeakMap object,
  // but do not retain an unbounded strong Payment response after the durable record is gone.
  for (const [id, receipt] of receiptsById) {
    if (localStorage.getItem(key(receipt.entry)) === null) { receipt.adopted = true; receiptsById.delete(id); }
  }
  const entries: PendingPaymentCreate[] = [];
  // Per-UUID keys cannot overwrite another tab's unresolved financial operation.
  if (localStorage.length > 4096) throw new Error('Не удалось прочитать локальные сохранения. Новое добавление приостановлено.');
  for (let index = 0; index < localStorage.length; index += 1) {
    const name = localStorage.key(index);
    if (!name?.startsWith(scope(current))) continue;
    const raw = localStorage.getItem(name);
    if (!raw || raw.length > 2048) throw new Error('Локальная запись сохранения повреждена. Новое добавление приостановлено.');
    const entry = JSON.parse(raw) as PendingPaymentCreate;
    const fields = ['attemptId', 'operation', 'sellerCompanyId', 'sellerBranchId', 'primaryPaymentId',
      'createdAtUtc', 'expiresAtUtc', 'canonicalPaymentId', 'identityVersion', 'actorId', 'apiScope', 'phase'];
    if (!entry || typeof entry !== 'object' || Array.isArray(entry) || Object.keys(entry).length !== fields.length
      || Object.keys(entry).some((field) => !fields.includes(field)))
      throw new Error('Локальная запись сохранения повреждена. Новое добавление приостановлено.');
    validateState({ ...entry, state: entry.phase === 'committed' ? 'Committed' : 'Draft' });
    if (entry.actorId !== current.actorId || entry.apiScope !== current.apiScope || key(entry) !== name
      || !['dispatched', 'committed'].includes(entry.phase) || entries.length >= 64)
      throw new Error('Локальная запись сохранения повреждена. Новое добавление приостановлено.');
    entries.push(entry);
  }
  return entries;
}
class PaymentCreateReceiptChangedError extends Error {
  constructor(public readonly missing = false) { super('Эта попытка уже разрешена или изменена в другой вкладке. Обновите проверку.'); }
}
function durableAttempt(expected: PendingPaymentCreate, actor: PaymentCreateOwner) {
  assertPaymentCreateOwner(actor);
  const value = pendingPaymentCreates().find((entry) => entry.attemptId === expected.attemptId);
  if (!value) throw new PaymentCreateReceiptChangedError(true);
  const immutable = ['attemptId', 'operation', 'sellerCompanyId', 'sellerBranchId', 'primaryPaymentId',
    'createdAtUtc', 'expiresAtUtc', 'identityVersion', 'actorId', 'apiScope'] as const;
  if (immutable.some(field => value[field] !== expected[field])
    || (expected.phase === 'committed' && (value.phase !== 'committed' || value.canonicalPaymentId !== expected.canonicalPaymentId)))
    throw new PaymentCreateReceiptChangedError();
  return value;
}
// A separate SHORT per-UUID lease, never the actor/API financial interval. Every durable write
// and removal cooperates here, with synchronous re-read + mutation inside the same callback.
// Recovery can therefore resolve a hung Create without waiting for its long lease. No queued
// wait/await is allowed inside this callback; contended/unavailable metadata leases fail closed.
function metadataLease<T>(entry: PendingPaymentCreate, actor: PaymentCreateOwner, action: () => T): Promise<T> {
  if (!navigator.locks?.request) throw new Error('Браузер не поддерживает безопасную проверку сохранения.');
  return navigator.locks.request(`${key(entry)}:mutation`, { mode: 'exclusive', ifAvailable: true }, (lease) => {
    if (!lease) throw new Error('Сохранение сейчас проверяется в другой вкладке. Обновите проверку.');
    assertPaymentCreateOwner(actor);
    return action();
  });
}
function mutateExisting<T>(entry: PendingPaymentCreate, actor: PaymentCreateOwner,
  action: (current: PendingPaymentCreate) => T, assertCurrent: () => void = () => undefined): Promise<T> {
  return metadataLease(entry, actor, () => {
    assertCurrent();
    return action(durableAttempt(entry, actor));
  });
}
function committed(entry: PendingPaymentCreate, id: number): PendingPaymentCreate {
  if (entry.phase === 'committed' && entry.canonicalPaymentId !== id) throw new PaymentCreateReceiptChangedError();
  return { ...entry, phase: 'committed', canonicalPaymentId: id };
}

// The timer aborts the actual transports AND fences every awaited continuation. Even a transport
// that ignores AbortSignal cannot commit after its pre-financial deadline, revive a receipt or
// hold the Web Lock callback forever. Timeout is never cancellation or permission to retry money.
function withAttemptDeadline<T>(actor: PaymentCreateOwner, financial: boolean,
  work: (flow: AttemptFlow) => Promise<T>): Promise<T> {
  const controller = new AbortController();
  let rejectBound!: (error: Error) => void;
  const timeoutError = () => {
    const error = new Error('Проверка сохранения заняла слишком много времени. Результат нужно проверить перед новым добавлением.');
    return financial && flow.entry ? new PaymentCreateRecoveryError(flow.entry, true, error) : error;
  };
  const flow: AttemptFlow = { controller, live: true, disposition: 'active',
    assert: () => {
      assertPaymentCreateOwner(actor);
      if (flow.disposition === 'adopted' && flow.adoptedPayment) throw new PaymentCreateAdoptedError(flow.adoptedPayment);
      if (flow.disposition === 'cancelled') throw new PaymentCreateCancelledError();
      if (!flow.live || controller.signal.aborted) throw timeoutError();
    },
    fail: (error) => { if (flow.live) { flow.live = false; controller.abort(); rejectBound(error); } },
  };
  let timer: ReturnType<typeof setTimeout>;
  const bounded = new Promise<T>((resolve, reject) => {
    rejectBound = reject;
    timer = setTimeout(() => flow.fail(timeoutError()), PAYMENT_ATTEMPT_DEADLINE_MS);
    void Promise.resolve().then(() => { flow.assert(); return work(flow); }).then(
      (result) => { try { flow.assert(); resolve(result); } catch (error) { reject(error); } },
      (error) => reject(error),
    );
  });
  return bounded.finally(() => {
    flow.live = false;
    clearTimeout(timer);
    if (flow.entry && inFlight.get(flow.entry.attemptId) === flow) inFlight.delete(flow.entry.attemptId);
  });
}

export async function createPaymentGuarded(payment: PaymentPayload, context: PaymentCreateContext | undefined,
  ports: PaymentCreatePorts): Promise<Payment> {
  const actor = paymentCreateOwner();
  if (!navigator.locks?.request) throw new Error('Браузер не поддерживает безопасное добавление платежа. Используйте защищённое соединение и поддерживаемый браузер.');
  return navigator.locks.request(scope(actor), { mode: 'exclusive', ifAvailable: true }, async (lease) => {
    if (!lease) throw new Error('Создание платежа выполняется в другой вкладке. Дождитесь ответа.');
    assertPaymentCreateOwner(actor);
    return withAttemptDeadline(actor, true, async (flow) => {
      const serializedPayment = serializePaymentCreate(payment);
      const pending = pendingPaymentCreates();
      if (pending.length) throw new PaymentCreateRecoveryError(pending[0], false);
      const operation = context?.operation ?? 'primary';
      const primaryPaymentId = operation === 'remainder' ? context?.primaryPaymentId : null;
      if (operation === 'remainder' && !positive(primaryPaymentId)) throw new Error('Не подтверждён исходный платёж остатка.');
      if (operation === 'primary' && context?.primaryPaymentId !== undefined) throw new Error('Некорректная связь платежа.');
      const sourceConfirmationToken = context?.sourceConfirmationToken;
      if (operation === 'remainder' && !isPaymentSourceConfirmationToken(sourceConfirmationToken))
        throw new Error('Снимок исходного платежа не подтверждён. Откройте сохранённый платёж заново и проверьте суммы.');
      if (operation === 'primary' && sourceConfirmationToken !== undefined) throw new Error('Снимок исходного платежа допустим только для остатка.');
      const command = operation === 'remainder'
        ? JSON.stringify({ ...JSON.parse(serializedPayment), sourceConfirmationToken }) : serializedPayment;
      const selection = payment as PaymentPayload & { sellerCompanyId?: number | null; sellerBranchId?: number | null };
      const body = { operation, primaryPaymentId: primaryPaymentId ?? null,
        sellerCompanyId: selection.sellerCompanyId ?? null, sellerBranchId: selection.sellerBranchId ?? null };
      const registered = await ports.register(body, actor, flow.controller.signal);
      flow.assert();
      const state = validateState(registered);
      if (state.state !== 'Draft' || state.operation !== body.operation || state.primaryPaymentId !== body.primaryPaymentId
        || state.sellerCompanyId !== body.sellerCompanyId || state.sellerBranchId !== body.sellerBranchId)
        throw new Error('Сервер не подтвердил новую попытку сохранения.');
      const entry: PendingPaymentCreate = { attemptId: state.attemptId, operation: state.operation, primaryPaymentId: state.primaryPaymentId,
        sellerCompanyId: state.sellerCompanyId, sellerBranchId: state.sellerBranchId,
        createdAtUtc: state.createdAtUtc, expiresAtUtc: state.expiresAtUtc, canonicalPaymentId: null,
        identityVersion: state.identityVersion, actorId: actor.actorId, apiScope: actor.apiScope, phase: 'dispatched' };
      await metadataLease(entry, actor, () => {
        flow.assert();
        // This is the ONLY missing-to-present transition, immediately after a new server register.
        if (localStorage.getItem(key(entry)) !== null) throw new PaymentCreateReceiptChangedError();
        persist(entry);
      });
      flow.assert();
      flow.entry = entry;
      inFlight.set(entry.attemptId, flow);
      let saved: Payment;
      try {
        const result = await ports.commit(entry.attemptId, command, actor, flow.controller.signal);
        flow.assert();
        saved = requirePayment(result);
        if (operation === 'remainder' && saved.id === primaryPaymentId) throw new Error('Сервер вернул исходный платёж вместо остатка.');
      } catch (cause) {
        flow.assert();
        try {
          const result = await ports.get(entry.attemptId, actor, flow.controller.signal);
          flow.assert();
          const observed = validateState(result, entry);
          if (observed.state !== 'Committed' || !positive(observed.canonicalPaymentId)) throw cause;
          const canonical = await ports.payment(observed.canonicalPaymentId, actor, flow.controller.signal);
          flow.assert();
          saved = requirePayment(canonical, observed.canonicalPaymentId);
        } catch {
          flow.assert();
          throw new PaymentCreateRecoveryError(entry, true, cause);
        }
      }
      flow.assert();
      let confirmed = committed(entry, saved.id);
      try {
        await mutateExisting(entry, actor, current => {
          confirmed = committed(current, saved.id);
          // Storage failure keeps the prior unresolved record. A live caller may still adopt
          // the positive ID, but a missing/changed receipt is never recreated from this ACK.
          try { persist(confirmed); } catch { changed(); }
        }, flow.assert);
      } catch (error) {
        flow.assert();
        if (error instanceof PaymentCreateReceiptChangedError && error.missing) throw new PaymentCreateAdoptedError(saved);
        throw new PaymentCreateRecoveryError(entry, true, error);
      }
      flow.assert();
      const receipt: Receipt = { entry: confirmed, owner: actor, payment: saved, adopted: false };
      receipts.set(saved, receipt);
      receiptsById.set(entry.attemptId, receipt);
      return saved;
    });
  });
}

export function canContinueCreatedPayment(saved: Payment): boolean {
  const receipt = receipts.get(saved);
  if (!receipt) return true;
  if (receipt.adopted || !isPaymentCreateOwnerCurrent(receipt.owner)) return false;
  try { return pendingPaymentCreates().some(value => value.attemptId === receipt.entry.attemptId); }
  catch { return false; }
}
export async function acknowledgeCreatedPayment(saved: Payment): Promise<boolean> {
  const receipt = receipts.get(saved);
  if (!receipt) return true; // Existing edit responses and test doubles have no creation receipt.
  if (receipt.adopted || !isPaymentCreateOwnerCurrent(receipt.owner)) return false;
  try {
    return await mutateExisting(receipt.entry, receipt.owner, current => {
      if (receipt.adopted) return false;
      persist(committed(current, saved.id));
      remove(current);
      receipts.delete(saved);
      if (receiptsById.get(current.attemptId) === receipt) receiptsById.delete(current.attemptId);
      return true;
    });
  } catch { return false; }
}

function validateCancel(value: PaymentCancelOutcome, entry: PendingPaymentCreate): PaymentCancelOutcome {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).length !== 3 || !['attemptId', 'state', 'canonicalPaymentId'].every(key => Object.hasOwn(value, key))
    || value.attemptId !== entry.attemptId || !['Cancelled', 'Committed'].includes(value.state)
    || (value.state === 'Cancelled' ? value.canonicalPaymentId !== null : !positive(value.canonicalPaymentId))
    || (value.state === 'Committed' && entry.operation === 'remainder' && value.canonicalPaymentId === entry.primaryPaymentId))
    throw new Error('Сервер не подтвердил отмену или сохранение. Новое добавление приостановлено.');
  return value;
}
export async function inspectPaymentCreate(entry: PendingPaymentCreate, ports: PaymentCreatePorts,
  cancel = false): Promise<PaymentAttemptState | PaymentCancelOutcome> {
  const actor = paymentCreateOwner();
  return withAttemptDeadline(actor, false, async (flow) => {
    const current = durableAttempt(entry, actor);
    const result = await (cancel ? ports.cancel(current.attemptId, actor, flow.controller.signal)
      : ports.get(current.attemptId, actor, flow.controller.signal));
    flow.assert();
    const state = cancel ? validateCancel(result as PaymentCancelOutcome, current) : validateState(result as PaymentAttemptState, current);
    await mutateExisting(current, actor, latest => {
      if (latest.phase === 'committed' && state.state !== 'Committed') throw new PaymentCreateReceiptChangedError();
      if (state.state === 'Committed' && positive(state.canonicalPaymentId)) persist(committed(latest, state.canonicalPaymentId));
      if (cancel && state.state === 'Cancelled') {
        if (latest.phase === 'committed') throw new PaymentCreateReceiptChangedError();
        const active = inFlight.get(latest.attemptId);
        if (active) { active.disposition = 'cancelled'; active.fail(new PaymentCreateCancelledError()); }
        remove(latest);
      }
    }, flow.assert);
    flow.assert();
    return state;
  });
}
export async function openCommittedPaymentCreate(entry: PendingPaymentCreate, ports: PaymentCreatePorts): Promise<Payment> {
  const actor = paymentCreateOwner();
  return withAttemptDeadline(actor, false, async (flow) => {
    const current = durableAttempt(entry, actor);
    const result = await ports.get(current.attemptId, actor, flow.controller.signal);
    flow.assert();
    const state = validateState(result, current);
    if (state.state !== 'Committed' || !positive(state.canonicalPaymentId)) throw new PaymentCreateRecoveryError(current, false);
    const canonical = await ports.payment(state.canonicalPaymentId, actor, flow.controller.signal);
    flow.assert();
    const saved = requirePayment(canonical, state.canonicalPaymentId);
    await mutateExisting(current, actor, latest => persist(committed(latest, saved.id)), flow.assert);
    flow.assert();
    return saved;
  });
}
// Fencing happens after fresh canonical GET and current UI scope check, before an async adoption
// callback can yield. It never labels a committed payment Cancelled and never removes its receipt.
export async function fencePaymentCreateAdoption(entry: PendingPaymentCreate, saved: Payment): Promise<void> {
  const actor = paymentCreateOwner();
  await mutateExisting(entry, actor, current => {
    if (current.phase !== 'committed' || current.canonicalPaymentId !== requirePayment(saved).id) throw new Error('Платёж не подтверждён.');
    const receipt = receiptsById.get(current.attemptId);
    if (receipt) receipt.adopted = true;
    const active = inFlight.get(current.attemptId);
    if (active) {
      active.disposition = 'adopted'; active.adoptedPayment = saved;
      active.fail(new PaymentCreateAdoptedError(saved));
    }
  });
}
export async function acknowledgePaymentCreate(entry: PendingPaymentCreate, saved: Payment): Promise<void> {
  const actor = paymentCreateOwner();
  await mutateExisting(entry, actor, current => {
    if (current.phase !== 'committed' || current.canonicalPaymentId !== requirePayment(saved).id) throw new Error('Платёж не подтверждён.');
    remove(current);
    receiptsById.delete(current.attemptId);
  });
}
