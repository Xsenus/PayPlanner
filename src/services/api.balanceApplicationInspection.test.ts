import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { balanceApplicationInspectionPath, parseBalanceApplicationInspection } from './balanceApplicationInspection';

const requestId = '114a38a7-bf68-4ec7-bd34-7dd217101337';
const otherKey = '2a4faebd-0710-4e44-b861-6d2930dd3e78';
const result = { requestId, appliedAmount: 25, balance: 75, isReplay: true };
const committed = () => ({ requestId, paymentId: 19, state: 'Committed', result: { ...result } });
const unconfirmed = () => ({ requestId, paymentId: 19, state: 'Unconfirmed', result: null });

describe('balance application inspection transport', () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
  describe.each(['client', 'legalEntity'] as const)('%s', kind => {
    const read = (signal?: AbortSignal, id = requestId) => kind === 'client'
      ? apiService.inspectClientBalanceApplication(7, 19, id, signal)
      : apiService.inspectLegalEntityBalanceApplication(7, 19, id, signal);

    it.each([false, true])('reads the saved state without a financial command; committed=%s', async saved => {
      const expected = saved ? committed() : unconfirmed();
      const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(expected, { status: saved ? 200 : 202 }));
      vi.stubGlobal('fetch', fetch); const controller = new AbortController();
      await expect(read(controller.signal)).resolves.toEqual(expected);
      expect(fetch).toHaveBeenCalledOnce();
      const [url, init] = fetch.mock.calls[0];
      expect(String(url)).toMatch(new RegExp(`/${kind === 'client' ? 'clients' : 'legal-entities'}/7/balance/applications/${requestId}\\?paymentId=19$`));
      expect(init?.method ?? 'GET').toBe('GET'); expect(init?.body).toBeUndefined();
      expect(init?.cache).toBe('no-store'); expect(init?.signal).toBe(controller.signal);
    });
    it.each([403, 404, 409, 500])('never converts refusal %s into a successful empty result', async status => {
      const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'Результат недоступен' }, { status }));
      vi.stubGlobal('fetch', fetch);
      await expect(read()).rejects.toThrow('Результат недоступен'); expect(fetch).toHaveBeenCalledOnce();
    });
    it('rejects a malformed request key before the network', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
      await expect(read(undefined, `${requestId}\n`)).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
    });
    it('propagates a cancelled read without retrying or writing', async () => {
      const controller = new AbortController(); let start!: () => void;
      const started = new Promise<void>(resolve => { start = resolve; });
      const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, init) => new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true }); start();
      }));
      vi.stubGlobal('fetch', fetch); const pending = read(controller.signal);
      await started; controller.abort(); await expect(pending).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).toHaveBeenCalledOnce();
    });
    it('does not dispatch an already cancelled read', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
      const controller = new AbortController(); controller.abort();
      await expect(read(controller.signal)).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).not.toHaveBeenCalled();
    });
    it('rejects a wrong response identity without accepting a result', async () => {
      const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ ...committed(), paymentId: 99 }));
      vi.stubGlobal('fetch', fetch); await expect(read()).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
    });
  });
});

describe('balance application inspection contract', () => {
  it.each([0, -1, NaN, Infinity, 1.5, 2147483648, '19'])('rejects an invalid owner/payment ID %s', id => {
    expect(() => balanceApplicationInspectionPath('client', id as number, 19, requestId)).toThrow();
    expect(() => balanceApplicationInspectionPath('legalEntity', 7, id as number, requestId)).toThrow();
  });
  it.each(['', '00000000-0000-0000-0000-000000000000', `${requestId}\n`, ` ${requestId}`, `${requestId}?extra=1`, null])('rejects an invalid UUID %s', id => {
    expect(() => balanceApplicationInspectionPath('client', 7, 19, id as string)).toThrow();
  });
  it('normalizes UUID case and returns only the known contract fields', () => {
    expect(balanceApplicationInspectionPath('client', 7, 19, requestId.toUpperCase())).toContain(requestId);
    expect(parseBalanceApplicationInspection({ ...committed(), privateCommand: 'ignored', result: { ...result, privateField: 'ignored' } }, requestId.toUpperCase(), 19)).toEqual(committed());
  });
  it.each([
    null, [], {}, { ...committed(), requestId: otherKey }, { ...committed(), paymentId: 20 },
    { ...committed(), state: 'Cancelled' }, { ...committed(), state: 'committed' },
    { ...committed(), result: null }, { ...committed(), result: { ...result, requestId: otherKey } },
    { ...committed(), result: { ...result, isReplay: false } }, { ...unconfirmed(), result: undefined },
    { ...unconfirmed(), result: { ...result } },
  ])('rejects a mismatched or unknown outcome %j', value => {
    expect(() => parseBalanceApplicationInspection(value, requestId, 19)).toThrow();
  });
  it.each([0, -1, NaN, Infinity, '25', null])('rejects an invalid applied amount %s', amount => {
    expect(() => parseBalanceApplicationInspection({ ...committed(), result: { ...result, appliedAmount: amount } }, requestId, 19)).toThrow();
  });
  it.each([NaN, Infinity, '75', null])('rejects a nonnumeric balance %s', balance => {
    expect(() => parseBalanceApplicationInspection({ ...committed(), result: { ...result, balance } }, requestId, 19)).toThrow();
  });
  it('keeps an explicitly unconfirmed result distinct from a committed result', () => {
    expect(parseBalanceApplicationInspection(unconfirmed(), requestId, 19)).toEqual(unconfirmed());
    expect(parseBalanceApplicationInspection({ ...committed(), result: { ...result, balance: -5.12 } }, requestId, 19)).toMatchObject({ state: 'Committed', result: { balance: -5.12 } });
  });
});
