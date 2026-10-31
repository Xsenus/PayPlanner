import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService, getApiBaseUrl } from './api';
import { authService } from './authService';

const token = (actor: number) => `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`;
beforeEach(() => {
  localStorage.setItem('pp_token', token(1)); localStorage.setItem('pp.auth-session-epoch.v1', 'source-A');
  vi.spyOn(authService, 'ensureFreshSession').mockResolvedValue(token(1));
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); localStorage.clear(); });

describe('payment source refresh transport', () => {
  it('reads the existing payment without cache using the owned cancellation signal', async () => {
    const controller = new AbortController(); const source = { id: 91, remainderConfirmationToken: 'remainder-v2:' + 'A'.repeat(64) };
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json(source)); vi.stubGlobal('fetch', request);
    expect(await apiService.getPayment(91, controller.signal)).toEqual(source);
    expect(request).toHaveBeenCalledTimes(1); expect(String(request.mock.calls[0][0])).toBe(`${getApiBaseUrl()}/payments/91`);
    expect(request.mock.calls[0][1]?.cache).toBe('no-store'); expect(request.mock.calls[0][1]?.signal).toBe(controller.signal);
    expect(request.mock.calls[0][1]?.body).toBeUndefined();
  });

  it('aborts an obsolete read without retrying it or sending a financial command', async () => {
    const controller = new AbortController();
    const request = vi.fn<typeof fetch>().mockImplementation((_url, options) => new Promise((_resolve, reject) => {
      const fail = () => reject(new DOMException('Aborted', 'AbortError'));
      if (options?.signal?.aborted) fail(); else options?.signal?.addEventListener('abort', fail, { once: true });
    })); vi.stubGlobal('fetch', request);
    const rejected = expect(apiService.getPayment(91, controller.signal)).rejects.toThrow('Aborted');
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(1)); controller.abort(); await rejected;
    expect(request).toHaveBeenCalledTimes(1);
  });

  it('preserves a denied read as an error with no fallback request', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ message: 'Read denied' }, { status: 403 })); vi.stubGlobal('fetch', request);
    await expect(apiService.getPayment(91)).rejects.toThrow(); expect(request).toHaveBeenCalledTimes(1);
  });

  it('does not accept the response for a replacement login', async () => {
    let finish!: (value: Response) => void;
    const request = vi.fn<typeof fetch>().mockReturnValue(new Promise(resolve => { finish = resolve; })); vi.stubGlobal('fetch', request);
    const rejected = expect(apiService.getPayment(91)).rejects.toThrow('Сессия изменилась');
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    localStorage.setItem('pp_token', token(2)); localStorage.setItem('pp.auth-session-epoch.v1', 'source-B');
    finish(Response.json({ id: 91 })); await rejected; expect(request).toHaveBeenCalledTimes(1);
  });
});
