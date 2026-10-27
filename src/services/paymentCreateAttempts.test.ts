import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { acknowledgeCreatedPayment, acknowledgePaymentCreate, fencePaymentCreateAdoption, bindPaymentCreateOwner, createPaymentGuarded, inspectPaymentCreate,
  openCommittedPaymentCreate, PAYMENT_CREATE_FIELDS, PaymentCreateRecoveryError, pendingPaymentCreates,
  serializePaymentCreate, type PaymentAttemptState, type PaymentCreatePorts } from './paymentCreateAttempts';
import { PAYMENT_ATTEMPT_DEADLINE_MS, PaymentCreateAdoptedError } from './paymentCreateAttempts';
import type { Payment, PaymentPayload } from '../types';

const firstId = '11111111-1111-4111-8111-111111111111';
const secondId = '22222222-2222-4222-8222-222222222222';
const token = (actor: number) => `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`;
const command = (): PaymentPayload => ({ date: '2026-10-26', plannedDate: '2026-10-26', amount: 123.456789,
  paidAmount: 0, type: 'Income', status: 'Pending', description: '  исходное описание  ', isPaid: false,
  notes: '', systemNotes: '', rescheduleCount: 0 });
const payment = (id: number): Payment => ({ ...command(), id, outstandingAmount: 123.456789, hasPartialPayment: false, createdAt: '2026-10-26T00:00:00Z' });
function state(overrides: Partial<PaymentAttemptState> = {}): PaymentAttemptState {
  return { attemptId: firstId, state: 'Draft', operation: 'primary', sellerCompanyId: null, sellerBranchId: null,
    primaryPaymentId: null, createdAtUtc: '2026-10-26T00:00:00Z', expiresAtUtc: '2026-10-27T00:00:00Z',
    canonicalPaymentId: null, identityVersion: 'payment-create-v1', ...overrides };
}
function ports(): PaymentCreatePorts {
  return { register: vi.fn().mockResolvedValue(state()), commit: vi.fn().mockResolvedValue(payment(91)),
    get: vi.fn().mockResolvedValue(state()), cancel: vi.fn().mockResolvedValue({ attemptId: firstId, state: 'Cancelled', canonicalPaymentId: null }),
    payment: vi.fn().mockResolvedValue(payment(91)) };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
function login(actor = 1, epoch = 'first-login', api = '/api') {
  localStorage.setItem('pp_token', token(actor));
  localStorage.setItem('pp.auth-session-epoch.v1', epoch);
  bindPaymentCreateOwner(actor, api);
}
function lockManager() {
  vi.stubGlobal('navigator', { locks: { request: vi.fn(async (_name: string, _options: LockOptions,
    callback: (lease: Lock | null) => Promise<unknown>) => callback({ name: 'owned', mode: 'exclusive' } as Lock)) } });
}
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); lockManager(); login(); });
afterEach(() => { vi.restoreAllMocks(); bindPaymentCreateOwner(null, '/api'); localStorage.clear(); vi.unstubAllGlobals(); });

describe('payment command and durable financial boundary', () => {
  it('serializes exactly the 38 allowed fields without response fields, rounding or new defaults', () => {
    expect(PAYMENT_CREATE_FIELDS).toHaveLength(38);
    const value = { ...command(), id: 700, clientName: 'private', sellerSnapshotJson: '{}', client: { id: 6 },
      paidDate: null, account: undefined } as unknown as PaymentPayload;
    expect(JSON.parse(serializePaymentCreate(value))).toEqual({ ...command(), paidDate: null });
    expect(serializePaymentCreate(value)).toContain('123.456789');
    expect(serializePaymentCreate(value)).not.toContain('private');
  });
  it.each([{ amount: NaN }, { clientId: 0 }, { advanceReviewStatus: null }, { description: [] }, { isPaid: null }])(
    'rejects noncanonical scalar values before registration: %j', async (bad) => {
      const io = ports();
      await expect(createPaymentGuarded({ ...command(), ...bad } as unknown as PaymentPayload, undefined, io)).rejects.toThrow();
      expect(io.register).not.toHaveBeenCalled(); expect(io.commit).not.toHaveBeenCalled();
    });
  it('keeps normal primary then remainder success, using separate server UUIDs and the confirmed primary ID', async () => {
    const io = ports();
    vi.mocked(io.register).mockResolvedValueOnce(state()).mockResolvedValueOnce(state({ attemptId: secondId,
      operation: 'remainder', primaryPaymentId: 91 }));
    vi.mocked(io.commit).mockResolvedValueOnce(payment(91)).mockResolvedValueOnce(payment(92));
    const primary = await createPaymentGuarded(command(), undefined, io);
    expect(await acknowledgeCreatedPayment(primary)).toBe(true);
    const remainder = await createPaymentGuarded(command(), { operation: 'remainder', primaryPaymentId: primary.id }, io);
    expect(remainder.id).toBe(92);
    expect(await acknowledgeCreatedPayment(remainder)).toBe(true);
    expect(vi.mocked(io.register).mock.calls[1][0]).toMatchObject({ operation: 'remainder', primaryPaymentId: 91 });
    expect(vi.mocked(io.commit).mock.calls.map(([id]) => id)).toEqual([firstId, secondId]);
    expect(pendingPaymentCreates()).toEqual([]);
  });
  it('does not classify a lost registration ACK as an unknown financial dispatch', async () => {
    const io = ports(); vi.mocked(io.register).mockRejectedValueOnce(new TypeError('network'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('network');
    expect(io.commit).not.toHaveBeenCalled(); expect(pendingPaymentCreates()).toEqual([]);
    await createPaymentGuarded(command(), undefined, io);
    expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('persists and reads back metadata before the first financial POST; stores no command, PII or token', async () => {
    const io = ports();
    vi.mocked(io.commit).mockImplementation(async () => {
      const records = pendingPaymentCreates(); expect(records).toHaveLength(1);
      const raw = JSON.stringify(records);
      expect(raw).toContain(firstId); expect(raw).not.toContain('исходное'); expect(raw).not.toContain('123.456789');
      expect(raw).not.toContain(token(1)); expect(raw).not.toContain('sessionEpoch');
      return payment(91);
    });
    await createPaymentGuarded(command(), undefined, io);
  });
  it('fails closed on storage quota before financial dispatch', async () => {
    const io = ports(); const original = Storage.prototype.setItem;
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, key: string, value: string) {
      if (key.startsWith('pp.payment-create.v1:')) throw new DOMException('quota', 'QuotaExceededError');
      return original.call(this, key, value);
    });
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('quota');
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('fails closed on incorrect readback before financial dispatch', async () => {
    const io = ports(); const original = Storage.prototype.getItem;
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      return key.startsWith('pp.payment-create.v1:') ? null : original.call(this, key);
    });
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('локальную');
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('requires an exclusive Web Lock, not just a localStorage readback', async () => {
    const io = ports(); vi.stubGlobal('navigator', {});
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('Браузер');
    expect(io.register).not.toHaveBeenCalled();
    vi.stubGlobal('navigator', { locks: { request: async (_name: string, _options: LockOptions,
      callback: (lease: Lock | null) => Promise<unknown>) => callback(null) } });
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('другой вкладке');
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('does not let a concurrent tab register while the first tab owns the financial interval', async () => {
    const held = new Set<string>();
    vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions,
      callback: (lease: Lock | null) => Promise<unknown>) => {
      if (held.has(name)) return callback(null); held.add(name);
      try { return await callback({ name, mode: 'exclusive' } as Lock); } finally { held.delete(name); }
    } } });
    const ack = deferred<Payment>(); const io = ports(); vi.mocked(io.commit).mockReturnValueOnce(ack.promise);
    const first = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('другой вкладке');
    expect(io.register).toHaveBeenCalledTimes(1); ack.resolve(payment(91)); await first;
  });
});

describe('unknown outcome, permission failures and owner isolation', () => {
  it('keeps a known committed receipt until the live caller explicitly adopts it', async () => {
    const io = ports(); const saved = await createPaymentGuarded(command(), undefined, io);
    expect(pendingPaymentCreates()[0]).toMatchObject({ phase: 'committed', canonicalPaymentId: saved.id });
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    expect(io.register).toHaveBeenCalledTimes(1);
    expect(await acknowledgeCreatedPayment(saved)).toBe(true); expect(pendingPaymentCreates()).toEqual([]);
  });
  it('recovers post-commit 500 by GET of the same UUID and canonical Payment, with no second financial POST', async () => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('HTTP 500 Notify'));
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    expect((await createPaymentGuarded(command(), undefined, io)).id).toBe(91);
    expect(io.commit).toHaveBeenCalledTimes(1); expect(io.get).toHaveBeenCalledWith(firstId, expect.anything(), expect.any(AbortSignal));
    expect(io.payment).toHaveBeenCalledWith(91, expect.anything(), expect.any(AbortSignal));
  });
  it.each([400, 403, 404, 409, 410, 500])('retains the UUID after commit HTTP %i and GET Draft; a new payload cannot unlock it', async (status) => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(Object.assign(new Error('blocked'), { status }));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    const entry = pendingPaymentCreates()[0];
    expect((await inspectPaymentCreate(entry, io)).state).toBe('Draft');
    await expect(createPaymentGuarded({ ...command(), amount: 999 }, undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    expect(io.register).toHaveBeenCalledTimes(1); expect(io.commit).toHaveBeenCalledTimes(1);
    vi.mocked(io.cancel).mockResolvedValue({ attemptId: firstId, state: 'Cancelled', canonicalPaymentId: null });
    await inspectPaymentCreate(entry, io, true); expect(pendingPaymentCreates()).toEqual([]);
  });
  it('does not clear the marker on Cancel 410 or on GET Draft after re-login; committed cancellation opens the canonical ID', async () => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('network'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow();
    const entry = pendingPaymentCreates()[0];
    login(2, 'B'); expect(pendingPaymentCreates()).toEqual([]);
    login(1, 'A-again'); expect(pendingPaymentCreates()[0].attemptId).toBe(firstId);
    vi.mocked(io.cancel).mockRejectedValueOnce(Object.assign(new Error('gone'), { status: 410 }));
    await expect(inspectPaymentCreate(entry, io, true)).rejects.toThrow('gone');
    expect(pendingPaymentCreates()).toHaveLength(1);
    vi.mocked(io.cancel).mockResolvedValueOnce({ attemptId: firstId, state: 'Committed', canonicalPaymentId: 91 });
    await inspectPaymentCreate(entry, io, true); expect(pendingPaymentCreates()).toHaveLength(1);
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    const saved = await openCommittedPaymentCreate(entry, io);
    expect(saved.id).toBe(91); expect(pendingPaymentCreates()).toHaveLength(1);
    await acknowledgePaymentCreate(entry, saved); expect(pendingPaymentCreates()).toEqual([]);
    expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('does not dispatch after A→B→A while registration waits, even before AuthContext can rebind', async () => {
    const io = ports(); const registration = deferred<PaymentAttemptState>();
    vi.mocked(io.register).mockReturnValueOnce(registration.promise);
    const first = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.register).toHaveBeenCalledTimes(1));
    localStorage.setItem('pp_token', token(2)); localStorage.setItem('pp.auth-session-epoch.v1', 'B');
    localStorage.setItem('pp_token', token(1)); localStorage.setItem('pp.auth-session-epoch.v1', 'A-new');
    registration.resolve(state()); await expect(first).rejects.toThrow('Пользователь изменился');
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('rejects the stale ACK after A→B→A and keeps the original UUID recoverable in a new A session', async () => {
    const io = ports(); const ack = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(ack.promise);
    const first = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    login(2, 'B'); login(1, 'A-new'); ack.resolve(payment(91));
    await expect(first).rejects.toThrow('Пользователь изменился');
    expect(pendingPaymentCreates()[0].attemptId).toBe(firstId);
    expect(io.get).not.toHaveBeenCalled();
  });
  it('separates the same actor in another API namespace', async () => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('network'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow();
    login(1, 'first-login', '/another/api'); expect(pendingPaymentCreates()).toEqual([]);
    login(); expect(pendingPaymentCreates()).toHaveLength(1);
  });
  it('adopts a positive ACK before refresh/cleanup failures, while retained metadata blocks a subsequent Create', async () => {
    const io = ports(); vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('storage denied'); });
    const saved = await createPaymentGuarded(command(), undefined, io);
    expect(saved.id).toBe(91); expect(await acknowledgeCreatedPayment(saved)).toBe(false);
    expect(pendingPaymentCreates()[0].canonicalPaymentId).toBe(91);
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('rejects a remainder ACK that points at its primary, retaining the server UUID for reconciliation', async () => {
    const io = ports();
    vi.mocked(io.register).mockResolvedValue(state({ operation: 'remainder', primaryPaymentId: 91 }));
    vi.mocked(io.get).mockResolvedValue(state({ operation: 'remainder', primaryPaymentId: 91 }));
    await expect(createPaymentGuarded(command(), { operation: 'remainder', primaryPaymentId: 91 }, io))
      .rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    expect(pendingPaymentCreates()[0]).toMatchObject({ attemptId: firstId, primaryPaymentId: 91 });
    expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('fails closed on a corrupt marker instead of deleting it or registering a replacement', async () => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('network'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow();
    const marker = Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index))
      .find((name) => name?.startsWith('pp.payment-create.v1:'))!;
    const raw = localStorage.getItem(marker)!;
    localStorage.setItem(marker, JSON.stringify({ ...JSON.parse(raw), unexpected: 'not a receipt' }));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('повреждена');
    expect(io.register).toHaveBeenCalledTimes(1); expect(localStorage.getItem(marker)).not.toBeNull();
  });
  it('aborts a local commit wait only after authoritative Cancelled, then protects a newer UUID from late old callbacks', async () => {
    const io = ports(); let oldResolve!: (value: Payment) => void; let oldReject!: (error: Error) => void;
    vi.mocked(io.register).mockResolvedValueOnce(state()).mockResolvedValueOnce(state({ attemptId: secondId }));
    vi.mocked(io.commit).mockImplementationOnce((_id, _command, _actor, signal) => new Promise<Payment>((resolve, reject) => {
      oldResolve = resolve; oldReject = reject;
      signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    }));
    const first = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const cancelled = expect(first).rejects.toThrow('попытка добавления отменена');
    await inspectPaymentCreate(pendingPaymentCreates()[0], io, true);
    await cancelled;
    expect(io.get).not.toHaveBeenCalled(); expect(pendingPaymentCreates()).toEqual([]);
    const next = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(next.promise);
    const second = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(2));
    oldResolve(payment(91)); oldReject(new Error('late network error')); await Promise.resolve();
    expect(pendingPaymentCreates().map(entry => entry.attemptId)).toEqual([secondId]);
    next.resolve(payment(92)); const adopted = await second; expect(adopted.id).toBe(92);
    expect(await acknowledgeCreatedPayment(adopted)).toBe(true); expect(pendingPaymentCreates()).toEqual([]);
  });
  it('does not recreate a cancelled marker even if an uncooperative old transport returns a late positive ACK', async () => {
    const io = ports(); const old = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(old.promise);
    const saving = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const cancelled = expect(saving).rejects.toThrow('попытка добавления отменена');
    await inspectPaymentCreate(pendingPaymentCreates()[0], io, true);
    old.resolve(payment(91)); await cancelled;
    expect(pendingPaymentCreates()).toEqual([]); expect(io.get).not.toHaveBeenCalled();
  });
  it('does not abort or treat the payment as cancelled when authoritative Cancel reports Committed', async () => {
    const io = ports(); const ack = deferred<Payment>(); let signal!: AbortSignal;
    vi.mocked(io.commit).mockImplementationOnce((_id, _command, _actor, value) => { signal = value; return ack.promise; });
    vi.mocked(io.cancel).mockResolvedValue({ attemptId: firstId, state: 'Committed', canonicalPaymentId: 91 });
    const saving = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const result = await inspectPaymentCreate(pendingPaymentCreates()[0], io, true);
    expect(result.state).toBe('Committed'); expect(signal.aborted).toBe(false);
    expect(pendingPaymentCreates()[0].canonicalPaymentId).toBe(91);
    ack.resolve(payment(91)); expect((await saving).id).toBe(91); expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('fences an externally adopted Committed flow, with no late success path or damage to a newer UUID', async () => {
    const io = ports(); const old = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(old.promise);
    vi.mocked(io.register).mockResolvedValueOnce(state()).mockResolvedValueOnce(state({ attemptId: secondId }));
    const first = createPaymentGuarded(command(), undefined, io);
    const adopted = expect(first).rejects.toBeInstanceOf(PaymentCreateAdoptedError);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const entry = pendingPaymentCreates()[0];
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    await inspectPaymentCreate(entry, io);
    const canonical = await openCommittedPaymentCreate(entry, io);
    await fencePaymentCreateAdoption(entry, canonical); await acknowledgePaymentCreate(entry, canonical); await adopted;
    const next = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(next.promise);
    const second = createPaymentGuarded(command(), undefined, io);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(2));
    old.resolve(payment(91)); await Promise.resolve(); await Promise.resolve();
    expect(pendingPaymentCreates().map(value => value.attemptId)).toEqual([secondId]);
    next.resolve(payment(92)); const saved = await second; expect(await acknowledgeCreatedPayment(saved)).toBe(true);
    expect(pendingPaymentCreates()).toEqual([]);
  });
  it('does not resurrect a receipt adopted in another tab when the original unfenced transport finally acknowledges', async () => {
    const io = ports(); const old = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(old.promise);
    const first = createPaymentGuarded(command(), undefined, io); const stopped = expect(first).rejects.toBeInstanceOf(PaymentCreateAdoptedError);
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const entry = pendingPaymentCreates()[0];
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    await inspectPaymentCreate(entry, io); const canonical = await openCommittedPaymentCreate(entry, io);
    // Another tab has no access to this tab's inFlight map. It only acknowledges its fresh canonical read.
    await acknowledgePaymentCreate(entry, canonical);
    old.resolve(payment(91)); await stopped;
    expect(pendingPaymentCreates()).toEqual([]); expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it.each(['sellerCompanyId', 'sellerBranchId', 'primaryPaymentId', 'canonicalPaymentId'] as const)(
    'requires explicit nullable StateDTO key %s rather than accepting omission', async key => {
      const io = ports(); const missing = { ...state() }; delete (missing as Partial<PaymentAttemptState>)[key];
      vi.mocked(io.register).mockResolvedValue(missing);
      await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow('состояние');
      expect(io.commit).not.toHaveBeenCalled();
    });
  it('accepts only the exact opaque authoritative CancelOutcome, not missing null or a protected full DTO', async () => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('unknown'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow();
    const entry = pendingPaymentCreates()[0];
    vi.mocked(io.cancel).mockResolvedValueOnce({ attemptId: firstId, state: 'Cancelled' } as never)
      .mockResolvedValueOnce(state({ state: 'Cancelled' }) as never)
      .mockResolvedValueOnce({ attemptId: firstId, state: 'Cancelled', canonicalPaymentId: null });
    await expect(inspectPaymentCreate(entry, io, true)).rejects.toThrow('Сервер не подтвердил');
    await expect(inspectPaymentCreate(entry, io, true)).rejects.toThrow('Сервер не подтвердил');
    expect(pendingPaymentCreates()).toHaveLength(1);
    expect((await inspectPaymentCreate(entry, io, true)).state).toBe('Cancelled');
    expect(pendingPaymentCreates()).toEqual([]);
  });
});

describe('specialised bounded attempt transports', () => {
  afterEach(() => vi.useRealTimers());
  it('bounds a hung registration and fences its late response before any financial dispatch', async () => {
    vi.useFakeTimers(); const io = ports(); const old = deferred<PaymentAttemptState>();
    vi.mocked(io.register).mockReturnValueOnce(old.promise).mockResolvedValueOnce(state({ attemptId: secondId }));
    const first = createPaymentGuarded(command(), undefined, io); const timedOut = expect(first).rejects.toThrow('слишком много времени');
    await vi.advanceTimersByTimeAsync(0); expect(io.register).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(PAYMENT_ATTEMPT_DEADLINE_MS); await timedOut;
    expect(vi.mocked(io.register).mock.calls[0][2].aborted).toBe(true);
    const second = createPaymentGuarded(command(), undefined, io); await vi.advanceTimersByTimeAsync(0);
    const saved = await second; expect(saved.id).toBe(91);
    old.resolve(state()); await vi.advanceTimersByTimeAsync(0);
    expect(io.commit).toHaveBeenCalledTimes(1); expect(vi.mocked(io.commit).mock.calls[0][0]).toBe(secondId);
    expect(pendingPaymentCreates()[0].attemptId).toBe(secondId); await acknowledgeCreatedPayment(saved);
  });
  it('bounds a hung financial dispatch but keeps its original UUID and fences late ACK', async () => {
    vi.useFakeTimers(); const io = ports(); const old = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(old.promise);
    const saving = createPaymentGuarded(command(), undefined, io); const timedOut = expect(saving).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    await vi.advanceTimersByTimeAsync(0); await vi.advanceTimersByTimeAsync(PAYMENT_ATTEMPT_DEADLINE_MS); await timedOut;
    expect(vi.mocked(io.commit).mock.calls[0][3].aborted).toBe(true);
    old.resolve(payment(91)); await vi.advanceTimersByTimeAsync(0);
    expect(pendingPaymentCreates()[0]).toMatchObject({ attemptId: firstId, phase: 'dispatched', canonicalPaymentId: null });
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
    expect(io.register).toHaveBeenCalledTimes(1);
  });
  it.each(['get', 'cancel', 'payment'] as const)('bounds hung recovery %s without unlocking or accepting a late result', async kind => {
    const io = ports(); vi.mocked(io.commit).mockRejectedValue(new Error('unknown'));
    await expect(createPaymentGuarded(command(), undefined, io)).rejects.toThrow();
    const entry = pendingPaymentCreates()[0]; vi.useFakeTimers();
    const late = deferred<never>();
    if (kind === 'payment') {
      vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
      vi.mocked(io.payment).mockReturnValueOnce(late.promise);
    } else vi.mocked(io[kind]).mockReturnValueOnce(late.promise);
    const pending = kind === 'payment' ? openCommittedPaymentCreate(entry, io) : inspectPaymentCreate(entry, io, kind === 'cancel');
    const rejected = expect(pending).rejects.toThrow('слишком много времени');
    await vi.advanceTimersByTimeAsync(PAYMENT_ATTEMPT_DEADLINE_MS); await rejected;
    expect(pendingPaymentCreates()).toHaveLength(1);
    const calls = kind === 'payment' ? vi.mocked(io.payment).mock.calls : kind === 'cancel' ? vi.mocked(io.cancel).mock.calls : vi.mocked(io.get).mock.calls;
    expect(calls[calls.length - 1][2].aborted).toBe(true);
    late.resolve((kind === 'payment' ? payment(91) : kind === 'cancel'
      ? { attemptId: firstId, state: 'Cancelled', canonicalPaymentId: null } : state()) as never);
    await vi.advanceTimersByTimeAsync(0); expect(pendingPaymentCreates()).toHaveLength(1);
  });
});

describe('cross-tab per-UUID metadata mutation boundary', () => {
  it.each(['inspect', 'cancelCommitted', 'canonicalOpen'] as const)(
    'never resurrects X after a delayed %s response when another tab adopted X and started Y', async action => {
      const io = ports(); vi.mocked(io.commit).mockRejectedValueOnce(new Error('unknown'));
      await expect(createPaymentGuarded(command(), undefined, io)).rejects.toBeInstanceOf(PaymentCreateRecoveryError);
      const entry = pendingPaymentCreates()[0];
      const lateState = deferred<PaymentAttemptState>();
      const lateCancel = deferred<Awaited<ReturnType<PaymentCreatePorts['cancel']>>>();
      const latePayment = deferred<Payment>();
      if (action === 'inspect') vi.mocked(io.get).mockReturnValueOnce(lateState.promise);
      else if (action === 'cancelCommitted') vi.mocked(io.cancel).mockReturnValueOnce(lateCancel.promise);
      else {
        vi.mocked(io.get).mockResolvedValueOnce(state({ state: 'Committed', canonicalPaymentId: 91 }));
        vi.mocked(io.payment).mockReturnValueOnce(latePayment.promise);
      }
      const old = action === 'canonicalOpen' ? openCommittedPaymentCreate(entry, io) : inspectPaymentCreate(entry, io, action === 'cancelCommitted');
      const rejected = expect(old).rejects.toThrow('уже разрешена');
      await vi.waitFor(() => expect(action === 'canonicalOpen' ? io.payment : action === 'cancelCommitted' ? io.cancel : io.get)
        .toHaveBeenCalledTimes(action === 'inspect' ? 2 : 1));
      const foreign = ports(); vi.mocked(foreign.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
      const canonical = await openCommittedPaymentCreate(entry, foreign);
      // Foreign tab has no access to this tab's original inFlight disposition.
      await acknowledgePaymentCreate(entry, canonical);
      const nextIo = ports(); vi.mocked(nextIo.register).mockResolvedValue(state({ attemptId: secondId }));
      vi.mocked(nextIo.commit).mockResolvedValue(payment(92));
      const next = await createPaymentGuarded(command(), undefined, nextIo);
      const yKey = Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index)).find(name => name?.endsWith(secondId))!;
      const yBytes = localStorage.getItem(yKey);
      if (action === 'inspect') lateState.resolve(state({ state: 'Committed', canonicalPaymentId: 91 }));
      else if (action === 'cancelCommitted') lateCancel.resolve({ attemptId: firstId, state: 'Committed', canonicalPaymentId: 91 });
      else latePayment.resolve(payment(91));
      await rejected;
      expect(pendingPaymentCreates().map(value => value.attemptId)).toEqual([secondId]);
      expect(localStorage.getItem(yKey)).toBe(yBytes);
      expect(io.register).toHaveBeenCalledTimes(1); expect(io.commit).toHaveBeenCalledTimes(1);
      expect(nextIo.register).toHaveBeenCalledTimes(1); expect(nextIo.commit).toHaveBeenCalledTimes(1);
      await expect(acknowledgePaymentCreate(entry, canonical)).rejects.toThrow('уже разрешена');
      expect(localStorage.getItem(yKey)).toBe(yBytes);
      expect(await acknowledgeCreatedPayment(next)).toBe(true);
    });

  it.each([state(), state({ state: 'Committed', canonicalPaymentId: 92 })])(
    'does not regress or replace an already committed receipt from a conflicting recovery response %j', async observed => {
      const io = ports(); const saved = await createPaymentGuarded(command(), undefined, io);
      const entry = pendingPaymentCreates()[0]; const before = JSON.stringify(pendingPaymentCreates());
      vi.mocked(io.get).mockResolvedValue(observed);
      await expect(inspectPaymentCreate(entry, io)).rejects.toThrow('уже разрешена');
      expect(JSON.stringify(pendingPaymentCreates())).toBe(before);
      expect(await acknowledgeCreatedPayment(saved)).toBe(true);
    });

  it('rereads the existing UUID inside a deferred metadata lease instead of trusting the pre-lock record', async () => {
    const io = ports(); const saved = await createPaymentGuarded(command(), undefined, io);
    const entry = pendingPaymentCreates()[0];
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    const grant = deferred<void>(); let delayNext = true; let requested = false;
    vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions,
      callback: (lease: Lock | null) => unknown) => {
      if (name.endsWith(':mutation') && delayNext) { delayNext = false; requested = true; await grant.promise; }
      return callback({ name, mode: 'exclusive' } as Lock);
    } } });
    const old = inspectPaymentCreate(entry, io); const stale = expect(old).rejects.toThrow('уже разрешена');
    await vi.waitFor(() => expect(requested).toBe(true));
    expect(await acknowledgeCreatedPayment(saved)).toBe(true);
    const nextIo = ports(); vi.mocked(nextIo.register).mockResolvedValue(state({ attemptId: secondId }));
    vi.mocked(nextIo.commit).mockResolvedValue(payment(92));
    const next = await createPaymentGuarded(command(), undefined, nextIo);
    const before = JSON.stringify(pendingPaymentCreates()); grant.resolve(); await stale;
    expect(JSON.stringify(pendingPaymentCreates())).toBe(before);
    expect(pendingPaymentCreates()[0].attemptId).toBe(secondId);
    expect(await acknowledgeCreatedPayment(next)).toBe(true);
  });

  it('fails closed when another tab holds the same UUID mutation lease without clearing or rewriting its receipt', async () => {
    const io = ports(); const saved = await createPaymentGuarded(command(), undefined, io);
    const entry = pendingPaymentCreates()[0]; const before = JSON.stringify(pendingPaymentCreates());
    vi.mocked(io.get).mockResolvedValue(state({ state: 'Committed', canonicalPaymentId: 91 }));
    vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions,
      callback: (lease: Lock | null) => unknown) => callback(name.endsWith(':mutation') ? null : { name, mode: 'exclusive' } as Lock) } });
    expect(await acknowledgeCreatedPayment(saved)).toBe(false);
    await expect(inspectPaymentCreate(entry, io)).rejects.toThrow('другой вкладке');
    expect(JSON.stringify(pendingPaymentCreates())).toBe(before);
    expect(io.register).toHaveBeenCalledTimes(1); expect(io.commit).toHaveBeenCalledTimes(1);
    lockManager(); expect(await acknowledgeCreatedPayment(saved)).toBe(true); expect(pendingPaymentCreates()).toEqual([]);
  });

  it('uses distinct short UUID leases so authoritative recovery can resolve a hung long Create lease', async () => {
    const held = new Set<string>(); const names: string[] = [];
    vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions,
      callback: (lease: Lock | null) => unknown) => {
      names.push(name); if (held.has(name)) return callback(null); held.add(name);
      try { return await callback({ name, mode: 'exclusive' } as Lock); } finally { held.delete(name); }
    } } });
    const io = ports(); const ack = deferred<Payment>(); vi.mocked(io.commit).mockReturnValueOnce(ack.promise);
    const old = createPaymentGuarded(command(), undefined, io); const stopped = expect(old).rejects.toThrow('попытка добавления отменена');
    await vi.waitFor(() => expect(io.commit).toHaveBeenCalledTimes(1));
    const entry = pendingPaymentCreates()[0]; await inspectPaymentCreate(entry, io, true); await stopped;
    expect(names.some(name => name.endsWith(`${firstId}:mutation`))).toBe(true);
    expect(new Set(names).size).toBe(2); expect(pendingPaymentCreates()).toEqual([]);
    ack.resolve(payment(91)); await Promise.resolve(); expect(pendingPaymentCreates()).toEqual([]);
  });
});
