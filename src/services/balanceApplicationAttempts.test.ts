import { createHash, webcrypto } from 'node:crypto';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { API_BASE_URL } from './apiBaseUrl';
import { bindPaymentCreateOwner, paymentCreateTokenMatches, PaymentCreateOwnerChangedError, type PaymentCreateOwner } from './paymentCreateAttempts';
import { acknowledgeBalanceApplication, applyBalanceGuarded, balanceApplicationInFlight, BALANCE_APPLICATION_CHANGED,
  BALANCE_APPLICATION_DEADLINE_MS, BalanceApplicationBusyError, BalanceApplicationRecoveryError, BalanceApplicationRecordChangedError,
  cancelBalanceApplication, canContinueBalanceApplication, inspectBalanceApplication, openAcknowledgedBalanceApplication, pendingBalanceApplication, pendingBalanceApplications,
  type BalanceApplicationPorts, type PendingBalanceApplication } from './balanceApplicationAttempts';
import type { BalanceApplicationCommand, Payment } from '../types';

const command = (): BalanceApplicationCommand => ({ paymentId: 19, amount: 25 });
const prefix = 'pp.balance-application.v1:';
function login(actor = 1, epoch = 'balance-first', scope = API_BASE_URL) {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch); bindPaymentCreateOwner(actor, scope);
}
function ownerGuard(actor: PaymentCreateOwner) {
  if (actor.apiScope !== API_BASE_URL || !paymentCreateTokenMatches(actor, localStorage.getItem('pp_token'))) throw new PaymentCreateOwnerChangedError();
}
function owned(entry: PendingBalanceApplication) {
  return entry.kind === 'client' ? { clientId: entry.ownerId, legalEntityId: null } : { clientId: null, legalEntityId: entry.ownerId };
}
function operation<T extends boolean = false>(entry: PendingBalanceApplication, isReplay: T = false as T) { return { requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay }; }
function io(): BalanceApplicationPorts {
  return {
    assertActor: ownerGuard,
    apply: vi.fn(async entry => operation(entry)),
    inspect: vi.fn(async entry => ({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Committed' as const, result: operation(entry, true) })),
    cancel: vi.fn(async entry => ({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Cancelled' as const, result: null })),
    payment: vi.fn(async entry => ({ id: entry.paymentId, ...owned(entry), amount: 100, paidAmount: 35, status: 'Pending', isPaid: false } as Payment)),
    balance: vi.fn(async entry => ({ ...owned(entry), balance: 65 })),
  };
}
function deferred<T>() {
  let resolve!: (value: T) => void; let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject };
}
function lockManager() {
  const held = new Set<string>();
  const request = vi.fn(async (name: string, _options: LockOptions, callback: (lock: Lock | null) => unknown) => {
    if (held.has(name)) return callback(null);
    held.add(name); try { return await callback({ name, mode: 'exclusive' } as Lock); } finally { held.delete(name); }
  });
  vi.stubGlobal('navigator', { locks: { request } }); return { held, request };
}
function storageKey() { const result = Object.keys(localStorage).find(key => key.startsWith(prefix)); if (!result) throw new Error('Expected metadata'); return result; }
async function lostResponse(ports: BalanceApplicationPorts, legalEntity = false) {
  vi.mocked(ports.apply).mockRejectedValueOnce(new TypeError('network lost'));
  await expect(applyBalanceGuarded(legalEntity ? 'legalEntity' : 'client', 7, command(), ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
  return pendingBalanceApplications(ports.assertActor)[0];
}
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.stubGlobal('crypto', webcrypto); lockManager(); login(); });
afterEach(() => { bindPaymentCreateOwner(null, API_BASE_URL); localStorage.clear(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('durable balance application lifecycle', () => {
  it.each(['client', 'legalEntity'] as const)('persists only bounded metadata before %s POST and uses canonical totals', async kind => {
    const ports = io(); const input = { ...command(), amount: 123.456789, description: 'private financial narrative', sourcePaymentId: 8 };
    vi.mocked(ports.apply).mockImplementation(async (entry, captured, actor, signal) => {
      expect(pendingBalanceApplications(ports.assertActor)).toEqual([entry]);
      expect(balanceApplicationInFlight(kind, 7, 19, ports.assertActor)).toBe(true);
      const raw = localStorage.getItem(storageKey())!;
      expect(Object.keys(JSON.parse(raw)).sort()).toEqual(['version', 'actorId', 'apiScope', 'kind', 'ownerId', 'paymentId', 'requestId', 'commandSha256', 'createdAtUtc'].sort());
      expect(raw).not.toContain(input.description); expect(raw).not.toContain('123.456789'); expect(raw).not.toContain(localStorage.getItem('pp_token'));
      expect(entry.commandSha256).toBe(createHash('sha256').update(JSON.stringify(captured)).digest('hex'));
      expect(Object.isFrozen(captured)).toBe(true); expect(actor.actorId).toBe(1); expect(signal.aborted).toBe(false);
      return operation(entry);
    });
    const value = await applyBalanceGuarded(kind, 7, input, ports);
    expect(value.appliedAmount).toBe(25); expect(value.operationBalance).toBe(75); expect(value.balance).toBe(65);
    expect(value.canonicalPayment.paidAmount).toBe(35); expect(canContinueBalanceApplication(value)).toBe(true);
    expect(ports.payment).toHaveBeenCalledOnce(); expect(ports.balance).toHaveBeenCalledOnce(); expect(ports.apply).toHaveBeenCalledOnce();
    expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
    expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(canContinueBalanceApplication(value)).toBe(true);
  });
  it('does not create a new UUID or POST when an earlier result is unknown', async () => {
    const ports = io(); const entry = await lostResponse(ports); const raw = localStorage.getItem(storageKey());
    await expect(applyBalanceGuarded('client', 7, { ...command(), amount: 50 }, ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(ports.apply).toHaveBeenCalledOnce(); expect(localStorage.getItem(storageKey())).toBe(raw);
    expect(pendingBalanceApplication('client', 7, 19, ports.assertActor)?.requestId).toBe(entry.requestId);
  });
  it.each([false, true])('recovers committed state with fresh canonical data; cancel=%s', async cancel => {
    const ports = io(); const entry = await lostResponse(ports);
    vi.mocked(ports.cancel).mockResolvedValue({ requestId: entry.requestId, paymentId: 19, state: 'Committed', result: operation(entry, true) });
    const value = await (cancel ? cancelBalanceApplication(entry, ports) : inspectBalanceApplication(entry, ports));
    expect(value.state).toBe('Committed'); if (value.state !== 'Committed') throw new Error('Expected commit');
    expect(value.canonicalPayment.paidAmount).toBe(35); expect(value.balance).toBe(65); expect(value.isReplay).toBe(true);
    expect(ports.apply).toHaveBeenCalledOnce(); expect(await acknowledgeBalanceApplication(value)).toBe(true);
  });
  it.each([false, true])('requires terminal cancellation before clearing metadata; inspect=%s', async inspect => {
    const ports = io(); const entry = await lostResponse(ports);
    vi.mocked(ports.inspect).mockResolvedValue({ requestId: entry.requestId, paymentId: 19, state: 'Cancelled', result: null });
    const value = await (inspect ? inspectBalanceApplication(entry, ports) : cancelBalanceApplication(entry, ports));
    expect(value.state).toBe('Cancelled'); expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1);
    expect(ports.payment).not.toHaveBeenCalled(); expect(ports.balance).not.toHaveBeenCalled(); expect(ports.apply).toHaveBeenCalledOnce();
    expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
    await applyBalanceGuarded('client', 7, command(), ports);
    expect(vi.mocked(ports.apply).mock.calls[1][0].requestId).not.toBe(entry.requestId);
  });
  it('keeps an unconfirmed result and does not repost money', async () => {
    const ports = io(); const entry = await lostResponse(ports);
    vi.mocked(ports.inspect).mockResolvedValue({ requestId: entry.requestId, paymentId: 19, state: 'Unconfirmed', result: null });
    await expect(inspectBalanceApplication(entry, ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(ports.apply).toHaveBeenCalledOnce(); expect(pendingBalanceApplications(ports.assertActor)).toEqual([entry]);
  });
  it('keeps a committed receipt when canonical reads fail', async () => {
    const ports = io(); vi.mocked(ports.payment).mockRejectedValue(new Error('read failed'));
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('подтверждён');
    expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1); expect(ports.apply).toHaveBeenCalledOnce();
  });
  it.each([false, true])('rechecks an externally removed record without recreating it or posting money; cancelled=%s', async cancel => {
    const ports = io(); const entry = await lostResponse(ports); localStorage.removeItem(storageKey());
    if (cancel) vi.mocked(ports.inspect).mockResolvedValue({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Cancelled', result: null });
    const writes = vi.spyOn(Storage.prototype, 'setItem'); const value = await openAcknowledgedBalanceApplication(entry, ports);
    expect(value.state).toBe(cancel ? 'Cancelled' : 'Committed'); expect(canContinueBalanceApplication(value)).toBe(true);
    expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(writes).not.toHaveBeenCalled();
    expect(ports.apply).toHaveBeenCalledOnce(); expect(ports.cancel).not.toHaveBeenCalled();
    expect(ports.payment).toHaveBeenCalledTimes(cancel ? 0 : 1); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
  });
  it('does not use the absent-record path while a current record is still stored', async () => {
    const ports = io(); const entry = await lostResponse(ports);
    await expect(openAcknowledgedBalanceApplication(entry, ports)).rejects.toBeInstanceOf(BalanceApplicationRecordChangedError);
    expect(ports.inspect).not.toHaveBeenCalled(); expect(pendingBalanceApplications(ports.assertActor)).toEqual([entry]);
  });
  it('retains an unconfirmed externally removed result as unresolved without recreating metadata', async () => {
    const ports = io(); const entry = await lostResponse(ports); localStorage.removeItem(storageKey());
    vi.mocked(ports.inspect).mockResolvedValue({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Unconfirmed', result: null });
    await expect(openAcknowledgedBalanceApplication(entry, ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(ports.apply).toHaveBeenCalledOnce(); expect(ports.cancel).not.toHaveBeenCalled(); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
  });
  it('cannot adopt an absent-record read after another window starts a newer attempt', async () => {
    const ports = io(); const entry = await lostResponse(ports); const name = storageKey(); localStorage.removeItem(name);
    const read = deferred<Awaited<ReturnType<BalanceApplicationPorts['inspect']>>>(); vi.mocked(ports.inspect).mockReturnValueOnce(read.promise);
    const action = openAcknowledgedBalanceApplication(entry, ports); await vi.waitFor(() => expect(ports.inspect).toHaveBeenCalledOnce());
    const newer = { ...entry, requestId: '22222222-2222-4222-8222-222222222222' }; localStorage.setItem(name, JSON.stringify(newer));
    read.resolve({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Committed', result: operation(entry, true) });
    await expect(action).rejects.toBeInstanceOf(BalanceApplicationRecordChangedError); expect(ports.payment).not.toHaveBeenCalled();
    expect(pendingBalanceApplications(ports.assertActor)).toEqual([newer]);
  });
  it.each(['payment', 'owner', 'amount', 'balance', 'status', 'paidFlag'] as const)('rejects invalid canonical %s without losing the receipt', async invalid => {
    const ports = io();
    if (invalid === 'payment') vi.mocked(ports.payment).mockImplementation(async entry => ({ id: 88, ...owned(entry), amount: 100, paidAmount: 25 } as Payment));
    if (invalid === 'owner') vi.mocked(ports.balance).mockResolvedValue({ clientId: 8, balance: 75 });
    if (invalid === 'amount') vi.mocked(ports.payment).mockImplementation(async entry => ({ id: 19, ...owned(entry), amount: NaN, paidAmount: 25 } as Payment));
    if (invalid === 'balance') vi.mocked(ports.balance).mockResolvedValue({ clientId: 7, balance: NaN });
    if (invalid === 'status') vi.mocked(ports.payment).mockImplementation(async entry => ({ id: 19, ...owned(entry), amount: 100, paidAmount: 25, status: 'unknown', isPaid: false } as unknown as Payment));
    if (invalid === 'paidFlag') vi.mocked(ports.payment).mockImplementation(async entry => ({ id: 19, ...owned(entry), amount: 100, paidAmount: 25, status: 'Processing', isPaid: 'false' } as unknown as Payment));
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
    expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1); expect(ports.apply).toHaveBeenCalledOnce();
  });
  it('does not send money when persistence cannot be verified', async () => {
    const ports = io(); const set = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota'); });
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow(); expect(ports.apply).not.toHaveBeenCalled(); set.mockRestore();
  });
  it('fails closed on corrupt metadata instead of replacing its key', async () => {
    const ports = io(); await lostResponse(ports); const name = storageKey(); localStorage.setItem(name, '{corrupt');
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('повреждена');
    expect(localStorage.getItem(name)).toBe('{corrupt'); expect(ports.apply).toHaveBeenCalledOnce();
  });
  it('keeps metadata under its actor and does not acknowledge a previous login', async () => {
    const ports = io(); const value = await applyBalanceGuarded('client', 7, command(), ports);
    login(2, 'balance-second'); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
    expect(canContinueBalanceApplication(value)).toBe(false); expect(await acknowledgeBalanceApplication(value)).toBe(false);
    login(1, 'balance-third'); expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1);
    expect(canContinueBalanceApplication(value)).toBe(false);
    const restored = await inspectBalanceApplication(value.attempt, ports); expect(await acknowledgeBalanceApplication(restored)).toBe(true);
  });
  it('rejects a token actor switch even before the owner binding changes', async () => {
    const ports = io(); localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '2' }))}.signature`);
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow(); expect(ports.apply).not.toHaveBeenCalled();
  });
  it('blocks parallel flows on the same target and releases the lease on timeout', async () => {
    vi.useFakeTimers(); const ports = io(); const entered = deferred<void>(); const late = deferred<Awaited<ReturnType<BalanceApplicationPorts['apply']>>>();
    vi.mocked(ports.apply).mockImplementation(async () => { entered.resolve(); return late.promise; });
    const first = applyBalanceGuarded('client', 7, command(), ports); const caught = first.catch(error => error);
    await entered.promise;
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toBeInstanceOf(BalanceApplicationBusyError);
    const entry = pendingBalanceApplications(ports.assertActor)[0];
    await vi.advanceTimersByTimeAsync(BALANCE_APPLICATION_DEADLINE_MS + 1);
    expect(await caught).toBeInstanceOf(BalanceApplicationRecoveryError); expect(balanceApplicationInFlight('client', 7, 19, ports.assertActor)).toBe(false);
    const closed = await cancelBalanceApplication(entry, ports); expect(closed.state).toBe('Cancelled'); expect(await acknowledgeBalanceApplication(closed)).toBe(true);
    late.resolve(operation(entry)); await Promise.resolve(); await Promise.resolve();
    expect(ports.payment).not.toHaveBeenCalled(); expect(pendingBalanceApplications(ports.assertActor)).toEqual([]);
  });
  it('invalidates a late response immediately when authenticated ownership changes', async () => {
    const ports = io(); const entered = deferred<void>(); const late = deferred<Awaited<ReturnType<BalanceApplicationPorts['apply']>>>();
    vi.mocked(ports.apply).mockImplementation(async () => { entered.resolve(); return late.promise; });
    const pending = applyBalanceGuarded('client', 7, command(), ports); const caught = pending.catch(error => error); await entered.promise;
    const entry = pendingBalanceApplications(ports.assertActor)[0]; login(2, 'other');
    expect(await caught).toBeInstanceOf(Error); late.resolve(operation(entry)); await Promise.resolve(); await Promise.resolve();
    expect(ports.payment).not.toHaveBeenCalled(); login(1, 'return'); expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1);
  });
  it('cannot delete a different metadata record during acknowledgement', async () => {
    const ports = io(); const value = await applyBalanceGuarded('client', 7, command(), ports);
    const name = storageKey(); const changed = { ...value.attempt, requestId: '2a4faebd-0710-4e44-b861-6d2930dd3e78' }; localStorage.setItem(name, JSON.stringify(changed));
    expect(canContinueBalanceApplication(value)).toBe(false); expect(await acknowledgeBalanceApplication(value)).toBe(false);
    expect(JSON.parse(localStorage.getItem(name)!).requestId).toBe(changed.requestId);
  });
  it('does not acknowledge a copied result or emit repeated adoption events', async () => {
    const ports = io(); const value = await applyBalanceGuarded('client', 7, command(), ports); const listener = vi.fn();
    window.addEventListener(BALANCE_APPLICATION_CHANGED, listener);
    try {
      expect(await acknowledgeBalanceApplication({ ...value })).toBe(false);
      expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(await acknowledgeBalanceApplication(value)).toBe(true); expect(listener).toHaveBeenCalledOnce();
    } finally { window.removeEventListener(BALANCE_APPLICATION_CHANGED, listener); }
  });
  it.each(['version', 'actor', 'scope', 'uuid', 'hash', 'date', 'target', 'extra'])('does not replace corrupt %s metadata', async invalid => {
    const ports = io(); await lostResponse(ports); const name = storageKey();
    const value = JSON.parse(localStorage.getItem(name)!);
    if (invalid === 'version') value.version = 'other';
    if (invalid === 'actor') value.actorId = 2;
    if (invalid === 'scope') value.apiScope = '/another-api';
    if (invalid === 'uuid') value.requestId = '00000000-0000-0000-0000-000000000000';
    if (invalid === 'hash') value.commandSha256 = 'bad';
    if (invalid === 'date') value.createdAtUtc = 'not-a-date';
    if (invalid === 'target') value.paymentId = 20;
    if (invalid === 'extra') value.amount = 25;
    const raw = JSON.stringify(value); localStorage.setItem(name, raw);
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('повреждена');
    expect(localStorage.getItem(name)).toBe(raw); expect(ports.apply).toHaveBeenCalledOnce();
  });
  it('captures recovery metadata before awaiting a mutable caller object', async () => {
    const ports = io(); const stored = await lostResponse(ports); const entry = { ...stored };
    const pending = inspectBalanceApplication(entry, ports); entry.requestId = '2a4faebd-0710-4e44-b861-6d2930dd3e78'; entry.ownerId = 88;
    const value = await pending; expect(value.requestId).toBe(stored.requestId); expect(value.attempt.ownerId).toBe(7);
    expect(vi.mocked(ports.inspect).mock.calls[0][0]).toEqual(stored);
  });
  it('captures authenticated ownership before reading command getters', async () => {
    const ports = io(); const payload = { ...command() };
    Object.defineProperty(payload, 'amount', { get: () => { login(2, 'getter-actor-switch'); return 25; } });
    await expect(applyBalanceGuarded('client', 7, payload, ports)).rejects.toThrow();
    expect(ports.apply).not.toHaveBeenCalled(); expect(Object.keys(localStorage).filter(key => key.startsWith(prefix))).toEqual([]);
  });
  it('does not invoke another canonical port after ownership changes synchronously', async () => {
    const ports = io(); vi.mocked(ports.payment).mockImplementation(async entry => {
      login(2, 'read-switch'); return { id: 19, ...owned(entry), amount: 100, paidAmount: 25 } as Payment;
    });
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow(); expect(ports.balance).not.toHaveBeenCalled();
  });
  it('does not send after its verified metadata changes before dispatch', async () => {
    const ports = io(); const other = '2a4faebd-0710-4e44-b861-6d2930dd3e78';
    const change = () => { const name = Object.keys(localStorage).find(key => key.startsWith(prefix)); if (name) localStorage.setItem(name, JSON.stringify({ ...JSON.parse(localStorage.getItem(name)!), requestId: other })); };
    window.addEventListener(BALANCE_APPLICATION_CHANGED, change);
    try {
      await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('изменилась');
      expect(ports.apply).not.toHaveBeenCalled(); expect(pendingBalanceApplications(ports.assertActor)[0].requestId).toBe(other);
    } finally { window.removeEventListener(BALANCE_APPLICATION_CHANGED, change); }
  });
  it('rejects a silently failed read-back before any financial POST', async () => {
    const ports = io(); const original = Storage.prototype.getItem;
    const read = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      return key.startsWith(prefix) ? null : original.call(this, key);
    });
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('недоступна'); expect(ports.apply).not.toHaveBeenCalled();
    read.mockRestore(); expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1);
  });
  it('does not clear metadata when removal is silently ignored', async () => {
    const ports = io(); const value = await applyBalanceGuarded('client', 7, command(), ports);
    const remove = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => undefined);
    await expect(acknowledgeBalanceApplication(value)).rejects.toThrow('недоступна'); remove.mockRestore();
    expect(canContinueBalanceApplication(value)).toBe(true); expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(1);
    expect(await acknowledgeBalanceApplication(value)).toBe(true);
  });
  it('bounds outstanding metadata without overwriting its existing keys', async () => {
    const ports = io(); const entry = await lostResponse(ports); const name = storageKey();
    for (let index = 1; index < 64; index += 1) {
      const value = { ...entry, paymentId: 19 + index, requestId: crypto.randomUUID() };
      localStorage.setItem(name.replace(/:19$/, `:${value.paymentId}`), JSON.stringify(value));
    }
    await expect(applyBalanceGuarded('client', 7, { ...command(), paymentId: 1000 }, ports)).rejects.toThrow('приостановлено');
    expect(pendingBalanceApplications(ports.assertActor)).toHaveLength(64); expect(ports.apply).toHaveBeenCalledOnce();
  });
  it('requires Web Locks before persisting or posting', async () => {
    const ports = io(); vi.stubGlobal('navigator', {});
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('Браузер'); expect(ports.apply).not.toHaveBeenCalled();
    expect(Object.keys(localStorage).filter(key => key.startsWith(prefix))).toEqual([]);
  });
  it('does not persist when cryptographic hashing is unavailable', async () => {
    const ports = io(); vi.stubGlobal('crypto', { randomUUID: () => '114a38a7-bf68-4ec7-bd34-7dd217101337' });
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('Браузер'); expect(ports.apply).not.toHaveBeenCalled();
    expect(Object.keys(localStorage).filter(key => key.startsWith(prefix))).toEqual([]);
  });
  it.each(['https://user:private@example.test/api', 'https://example.test/api?token=private', 'https://example.test/api#private'])('does not put credentials or URL secrets into metadata: %s', async scope => {
    login(1, 'secret-scope', scope); const ports = io(); ports.assertActor = actor => {
      if (!paymentCreateTokenMatches(actor, localStorage.getItem('pp_token'))) throw new PaymentCreateOwnerChangedError();
    };
    await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toThrow('подключении'); expect(ports.apply).not.toHaveBeenCalled();
    expect(Object.keys(localStorage).filter(key => key.startsWith(prefix))).toEqual([]);
  });
  it('cannot adopt an older confirmed result once a new operation has started', async () => {
    const ports = io(); const first = await applyBalanceGuarded('client', 7, command(), ports); expect(await acknowledgeBalanceApplication(first)).toBe(true);
    const second = await applyBalanceGuarded('client', 7, command(), ports);
    expect(canContinueBalanceApplication(first)).toBe(false); expect(await acknowledgeBalanceApplication(first)).toBe(false);
    expect(pendingBalanceApplications(ports.assertActor)[0].requestId).toBe(second.requestId); expect(await acknowledgeBalanceApplication(second)).toBe(true);
  });
  it('notifies passive windows after the actual financial lease has been released', async () => {
    const locks = lockManager(); const ports = io(); vi.mocked(ports.apply).mockRejectedValueOnce(new TypeError('lost'));
    const states: Array<{ active: boolean; locks: number }> = [];
    const listener = () => states.push({ active: balanceApplicationInFlight('client', 7, 19, ports.assertActor), locks: locks.held.size });
    window.addEventListener(BALANCE_APPLICATION_CHANGED, listener);
    try {
      await expect(applyBalanceGuarded('client', 7, command(), ports)).rejects.toBeInstanceOf(BalanceApplicationRecoveryError);
      expect(states[0].active).toBe(true); expect(states.at(-1)).toEqual({ active: false, locks: 0 });
    } finally { window.removeEventListener(BALANCE_APPLICATION_CHANGED, listener); }
  });
});
