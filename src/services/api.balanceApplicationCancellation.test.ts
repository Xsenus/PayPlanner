import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

const requestId = '114a38a7-bf68-4ec7-bd34-7dd217101337';
const cancelled = { requestId, paymentId: 19, state: 'Cancelled', result: null };
const committed = { requestId, paymentId: 19, state: 'Committed', result: { requestId, appliedAmount: 25, balance: 75, isReplay: true } };

describe.each(['client', 'legalEntity'] as const)('%s balance application cancellation transport', kind => {
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
  const cancel = (signal?: AbortSignal, id = requestId) => kind === 'client'
    ? apiService.cancelClientBalanceApplication(7, 19, id, signal)
    : apiService.cancelLegalEntityBalanceApplication(7, 19, id, signal);

  it.each([cancelled, committed])('confirms %j without a financial command or an extra money request', async value => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(value));
    vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await expect(cancel(controller.signal)).resolves.toEqual(value);
    expect(fetch).toHaveBeenCalledOnce();
    const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toMatch(new RegExp(`/${kind === 'client' ? 'clients' : 'legal-entities'}/7/balance/applications/${requestId}/cancel$`));
    expect(init?.method).toBe('POST'); expect(JSON.parse(String(init?.body))).toEqual({ paymentId: 19 });
    expect(init?.cache).toBe('no-store'); expect(init?.signal).toBe(controller.signal);
  });
  it('rejects an unconfirmed cancellation even with HTTP 200', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ ...cancelled, state: 'Unconfirmed' }));
    vi.stubGlobal('fetch', fetch); await expect(cancel()).rejects.toThrow('ещё не подтверждена'); expect(fetch).toHaveBeenCalledOnce();
  });
  it.each([202, 204, 403, 404, 409, 500])('never treats HTTP %s as confirmation or retries with another key', async status => {
    const response = status === 204 ? new Response(null, { status }) : Response.json(cancelled, { status });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response); vi.stubGlobal('fetch', fetch);
    await expect(cancel()).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('rejects another target in the response', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ ...cancelled, paymentId: 88 }));
    vi.stubGlobal('fetch', fetch); await expect(cancel()).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
  it('validates the key before any cancellation request', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    await expect(cancel(undefined, `${requestId}\n`)).rejects.toThrow(); expect(fetch).not.toHaveBeenCalled();
  });
  it('does not dispatch when already aborted', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); controller.abort();
    await expect(cancel(controller.signal)).rejects.toHaveProperty('name', 'AbortError'); expect(fetch).not.toHaveBeenCalled();
  });
  it('retains an uncertain outcome when the transport is lost', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError('network lost'));
    vi.stubGlobal('fetch', fetch); await expect(cancel()).rejects.toThrow('network lost'); expect(fetch).toHaveBeenCalledOnce();
  });
});
