import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Act, ActInput } from '../types';
import { acknowledgeCreatedAct, actCreateCommand, actCreateSuccessMessage, createActGuarded, pendingActCreates } from './actCreateAttempts';

const key = '11111111-1111-4111-8111-111111111111';
const otherKey = '22222222-2222-4222-8222-222222222222';
const command = (): ActInput => ({ number: 'INVOICE-99', date: '2027-01-01', amount: 125,
  status: 'Created', creationMode: 'Manual', clientId: 7, title: 'Услуги',
  lineItems: [{ sourceKey: 'manual:1', name: 'Консультация', quantity: 1, price: 125 }] });
const saved = (): Act => ({ id: 91, number: '1', date: '2027-01-01', amount: 125, status: 'Created', title: 'Услуги', clientId: 7 } as Act);
const failure = (status: number) => Object.assign(new Error(`HTTP ${status}`), { status });
const ports = () => ({ commit: vi.fn().mockResolvedValue(saved()), lookup: vi.fn().mockRejectedValue(failure(404)) });
function login(actor = 1, epoch = 'first-session') {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch);
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
beforeEach(() => { sessionStorage.clear(); localStorage.clear(); login(); });
afterEach(() => { vi.restoreAllMocks(); sessionStorage.clear(); localStorage.clear(); });

describe('ordinary act durable create attempts', () => {
  it('persists the UUID and exact command before dispatch and accepts only the canonical number', async () => {
    const io = ports();
    io.commit.mockImplementation(async (body: ActInput, uploadKey: string) => {
      expect(uploadKey).toBe(key);
      expect(body.number).toBe('');
      expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
      expect(actCreateCommand(key)).toEqual({ ...command(), number: '' });
      return saved();
    });
    const act = await createActGuarded(command(), { attemptKey: key }, io);
    expect(act.number).toBe('1');
    expect(pendingActCreates()).toMatchObject([{ state: 'confirmed', actId: 91 }]);
    expect(actCreateSuccessMessage(act)).toBe('Акт создан.');
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toEqual([]);
  });

  it.each(['quota', 'meta-quota', 'verification'])('refuses transport before an untracked %s write', async (kind) => {
    const io = ports();
    const original = Storage.prototype.setItem;
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name, value) {
      if (this === sessionStorage) {
        if (kind === 'quota' || kind === 'meta-quota' && name.endsWith(':meta')) throw new DOMException('Quota exceeded', 'QuotaExceededError');
        if (kind === 'verification') return;
      }
      original.call(this, name, value);
    });
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toThrow('Не удалось сохранить попытку');
    expect(io.commit).not.toHaveBeenCalled();
    expect(sessionStorage.length).toBe(0);
  });

  it('recovers a lost acknowledgement without a second POST and keeps the receipt until adoption', async () => {
    const io = ports(); io.commit.mockRejectedValue(new TypeError('Network failed')); io.lookup.mockResolvedValue(saved());
    const act = await createActGuarded(command(), { attemptKey: key }, io);
    expect(io.commit).toHaveBeenCalledOnce();
    expect(actCreateSuccessMessage(act)).toContain('ранее сохранённый');
    expect(pendingActCreates()).toHaveLength(1);
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toHaveLength(0);
  });
  it('a receipt write failure after server commit retains recovery without another creation', async () => {
    let committed = false;
    const original = Storage.prototype.setItem;
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name, value) {
      if (this === sessionStorage && committed && name.endsWith(':meta')) throw new DOMException('Quota exceeded', 'QuotaExceededError');
      original.call(this, name, value);
    });
    const io = ports(); io.commit.mockImplementation(async () => { committed = true; return saved(); }); io.lookup.mockResolvedValue(saved());
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toThrow('Не удалось сохранить попытку');
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
    committed = false;
    const act = await createActGuarded(command(), { attemptKey: key, lookupOnly: true }, io);
    expect(io.commit).toHaveBeenCalledOnce();
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toEqual([]);
  });

  it.each([403, 404, 410, 500])('retains an unknown attempt after POST %s and a missing lookup', async (status) => {
    const io = ports(); io.commit.mockRejectedValue(failure(status));
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toMatchObject({ status });
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
    expect(actCreateCommand(key).number).toBe('');
  });

  it('lookup404 after reload keeps the UUID and retry sends the saved command', async () => {
    const first = ports(); first.commit.mockRejectedValue(new TypeError('Lost response'));
    await expect(createActGuarded(command(), { attemptKey: key }, first)).rejects.toThrow();
    const retry = ports();
    await expect(createActGuarded({ ...command(), amount: 999 }, { attemptKey: key, lookupOnly: true }, retry)).rejects.toThrow('Результат пока недоступен');
    expect(retry.commit).not.toHaveBeenCalled();
    const act = await createActGuarded({ ...command(), amount: 999 }, { attemptKey: key, retry: true }, retry);
    expect(retry.commit).toHaveBeenCalledWith({ ...command(), number: '' }, key);
    expect(act.number).toBe('1');
  });

  it.each([400, 422])('allows corrected data after validation%s with the same UUID', async (status) => {
    const io = ports(); io.commit.mockRejectedValueOnce(failure(status));
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toMatchObject({ status });
    expect(pendingActCreates()).toMatchObject([{ key, state: 'editable' }]);
    const corrected = { ...command(), amount: 250 };
    await createActGuarded(corrected, { attemptKey: key }, io);
    expect(io.commit).toHaveBeenLastCalledWith({ ...corrected, number: '' }, key);
    expect(io.lookup).not.toHaveBeenCalled();
  });

  it('a changed command conflict recovers the earlier canonical act explicitly', async () => {
    const io = ports(); io.commit.mockRejectedValueOnce(failure(400));
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toThrow();
    io.commit.mockRejectedValueOnce(failure(409)); io.lookup.mockResolvedValue(saved());
    const act = await createActGuarded({ ...command(), amount: 999 }, { attemptKey: key }, io);
    expect(act.amount).toBe(125);
    expect(actCreateSuccessMessage(act)).toContain('его сохранённые данные');
  });

  it('confirmed but currently hidden/deleted records stay retained without reissuing a POST', async () => {
    const io = ports(); await createActGuarded(command(), { attemptKey: key }, io);
    const next = ports();
    await expect(createActGuarded(command(), { attemptKey: key, retry: true }, next)).rejects.toThrow('Результат пока недоступен');
    expect(next.commit).not.toHaveBeenCalled();
    expect(pendingActCreates()).toMatchObject([{ key, state: 'confirmed' }]);
  });

  it('an explicit separate draft can succeed while an unrelated attempt remains unknown', async () => {
    const first = ports(); first.commit.mockRejectedValue(failure(403));
    await expect(createActGuarded(command(), { attemptKey: key }, first)).rejects.toThrow();
    const second = ports(); const act = await createActGuarded({ ...command(), clientId: 8 }, { attemptKey: otherKey }, second);
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
    expect(second.commit).toHaveBeenCalledOnce();
  });

  it('coalesces simultaneous retries of one UUID', async () => {
    const io = ports(); const reply = deferred<Act>(); io.commit.mockReturnValue(reply.promise);
    const first = createActGuarded(command(), { attemptKey: key }, io);
    const second = createActGuarded(command(), { attemptKey: key }, io);
    reply.resolve(saved());
    expect((await first).id).toBe((await second).id);
    expect(io.commit).toHaveBeenCalledOnce();
  });
  it('a concurrent changed command reports the earlier canonical result explicitly', async () => {
    const io = ports(); const reply = deferred<Act>(); io.commit.mockReturnValue(reply.promise);
    const first = createActGuarded(command(), { attemptKey: key }, io);
    const changed = createActGuarded({ ...command(), amount: 999 }, { attemptKey: key }, io);
    reply.resolve(saved());
    await first;
    const act = await changed;
    expect(act.amount).toBe(125);
    expect(actCreateSuccessMessage(act)).toContain('ранее сохранённый');
    expect(io.commit).toHaveBeenCalledOnce();
  });
  it('the same actor can recover after a new login through a fresh ACL-checked lookup', async () => {
    const first = ports(); first.commit.mockRejectedValue(failure(500));
    await expect(createActGuarded(command(), { attemptKey: key }, first)).rejects.toThrow();
    login(1, 'new-login');
    const recovery = ports(); recovery.lookup.mockResolvedValue(saved());
    const act = await createActGuarded(command(), { attemptKey: key, lookupOnly: true }, recovery);
    expect(recovery.lookup).toHaveBeenCalledWith(key);
    expect(recovery.commit).not.toHaveBeenCalled();
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toEqual([]);
  });
  it('a fresh login lookup does not wait for the previous session pending POST', async () => {
    const old = ports(); const reply = deferred<Act>(); old.commit.mockReturnValue(reply.promise);
    const pending = createActGuarded(command(), { attemptKey: key }, old);
    const rejected = expect(pending).rejects.toThrow('Сессия изменилась');
    login(1, 'new-login');
    const recovery = ports(); recovery.lookup.mockResolvedValue(saved());
    const act = await createActGuarded(command(), { attemptKey: key, lookupOnly: true }, recovery);
    expect(recovery.lookup).toHaveBeenCalledOnce();
    expect(recovery.commit).not.toHaveBeenCalled();
    reply.resolve(saved()); await rejected;
    expect(pendingActCreates()).toMatchObject([{ key, state: 'confirmed', actId: 91 }]);
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toEqual([]);
  });

  it.each(['actor', 'session'])('rejects late results after %s replacement and preserves the old attempt', async (change) => {
    const io = ports(); const reply = deferred<Act>(); io.commit.mockReturnValue(reply.promise);
    const job = createActGuarded(command(), { attemptKey: key }, io);
    login(change === 'actor' ? 2 : 1, 'second-session'); reply.resolve(saved());
    await expect(job).rejects.toThrow('Сессия изменилась');
    if (change === 'actor') expect(pendingActCreates()).toEqual([]);
    login(); expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
  });

  it('lists small metadata without reading stored line item commands', async () => {
    const io = ports(); await createActGuarded(command(), { attemptKey: key }, io);
    const read = vi.spyOn(Storage.prototype, 'getItem');
    expect(pendingActCreates()).toHaveLength(1);
    expect(read.mock.calls.some(([name]) => name.endsWith(':command'))).toBe(false);
  });
  it('keeps source identifiers and amount in small metadata without copying line items', async () => {
    const io = ports(); io.commit.mockRejectedValue(failure(500));
    const body = { ...command(), title: '', invoiceId: 618, invoiceNumber: 'INV-24', legalEntityId: null };
    await expect(createActGuarded(body, { attemptKey: key }, io)).rejects.toThrow();
    const [entry] = pendingActCreates();
    expect(entry).toMatchObject({ key, title: '', amount: 125, invoiceId: 618, invoiceNumber: 'INV-24', clientId: 7, legalEntityId: null });
    expect(entry).not.toHaveProperty('lineItems');
    expect(actCreateCommand(key)).toEqual({ ...body, number: '' });
  });
  it('reads and recovers v1 metadata created before source identifiers were added', async () => {
    const io = ports(); io.commit.mockRejectedValue(failure(500));
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toThrow();
    const metaKey = Object.keys(sessionStorage).find(name => name.endsWith(':meta'))!;
    const legacy = JSON.parse(sessionStorage.getItem(metaKey)!);
    for (const field of ['amount', 'creationMode', 'invoiceNumber', 'invoiceId', 'clientId', 'legalEntityId']) delete legacy[field];
    sessionStorage.setItem(metaKey, JSON.stringify(legacy));
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown', date: command().date }]);
    expect(pendingActCreates()[0]).not.toHaveProperty('amount');
    io.lookup.mockResolvedValue(saved());
    const act = await createActGuarded(command(), { attemptKey: key, lookupOnly: true }, io);
    expect(io.commit).toHaveBeenCalledOnce();
    acknowledgeCreatedAct(act);
    expect(pendingActCreates()).toEqual([]);
  });
  it('confirmed metadata uses canonical current data while the original command stays unchanged', async () => {
    const io = ports(); io.commit.mockRejectedValue(failure(500));
    await expect(createActGuarded(command(), { attemptKey: key }, io)).rejects.toThrow();
    const canonical = { ...saved(), date: '2027-01-02', title: 'Актуальный акт', amount: 250, invoiceId: 619, invoiceNumber: 'INV-B', clientId: null, legalEntityId: 9 };
    io.lookup.mockResolvedValue(canonical);
    await createActGuarded(command(), { attemptKey: key, lookupOnly: true }, io);
    expect(pendingActCreates()).toMatchObject([{ key, state: 'confirmed', date: canonical.date, title: canonical.title,
      amount: 250, invoiceId: 619, invoiceNumber: 'INV-B', clientId: null, legalEntityId: 9 }]);
    expect(actCreateCommand(key)).toEqual({ ...command(), number: '' });
  });
});
