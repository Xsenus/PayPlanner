import { describe, expect, it, vi } from 'vitest';
import { createRestoredRuntimeFetch } from './restoredRuntimeFetch';

const API = 'http://127.0.0.1:5080/api';
const ID = 'cefe9b7e3bb64c51b3c80a9e96bb57ea';

describe('restored runtime transport', () => {
  it('shares one credential-free handshake before concurrent business requests', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValueOnce(Response.json({ isolated: true, instanceId: ID }))
      .mockResolvedValue(Response.json({ ok: true }));
    const request = createRestoredRuntimeFetch(API, ID, transport);
    await Promise.all([request(`${API}/tasks`), request(`${API}/auth/login`, { method: 'POST', body: 'fixture' })]);
    expect(transport).toHaveBeenCalledTimes(3);
    expect(transport.mock.calls[0]).toEqual([`${API}/restored-runtime/identity`, expect.objectContaining({
      method: 'GET', credentials: 'omit', cache: 'no-store', redirect: 'error',
    })]);
    for (const [, options] of transport.mock.calls.slice(1)) {
      expect(new Headers(options?.headers).get('X-PayPlanner-Restored-Instance')).toBe(ID);
      expect(options?.redirect).toBe('error');
    }
    expect(transport.mock.calls[2][1]?.body).toBe('fixture');
  });

  it.each([
    { isolated: false, instanceId: ID },
    { isolated: true, instanceId: '00000000000000000000000000000000' },
    {}, null,
  ])('never sends a business request after incompatible identity %j', async identity => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(Response.json(identity));
    const request = createRestoredRuntimeFetch(API, ID, transport);
    await expect(request(`${API}/tasks`, { method: 'POST' })).rejects.toThrow('Рабочий запрос не отправлен');
    await expect(request(`${API}/tasks`)).rejects.toThrow('Рабочий запрос не отправлен');
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it.each([404, 500])('rejects ordinary or failed backend with status %s', async status => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(new Response('', { status }));
    await expect(createRestoredRuntimeFetch(API, ID, transport)(`${API}/auth/login`)).rejects.toThrow();
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it('does not disclose transport errors or make fallback requests', async () => {
    const transport = vi.fn<typeof fetch>().mockRejectedValue(new Error('fixture-secret'));
    await expect(createRestoredRuntimeFetch(API, ID, transport)(`${API}/tasks`)).rejects.not.toThrow('fixture-secret');
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it.each(['https://api.elitpravo.com/api/tasks', 'http://127.0.0.1:5081/api/tasks', '/api/tasks'])(
    'rejects another or ambiguous destination before handshake: %s', async url => {
      const transport = vi.fn<typeof fetch>();
      await expect(createRestoredRuntimeFetch(API, ID, transport)(url)).rejects.toThrow();
      expect(transport).not.toHaveBeenCalled();
    });

  it('preserves existing authentication headers but replaces a supplied wrong probe marker', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ isolated: true, instanceId: ID }));
    const request = createRestoredRuntimeFetch(API, ID, transport);
    await request(new Request(`${API}/tasks`, { headers: {
      Authorization: 'Bearer fixture-token', 'X-PayPlanner-Restored-Instance': 'wrong',
    } }));
    const headers = new Headers(transport.mock.calls[1][1]?.headers);
    expect(headers.get('Authorization')).toBe('Bearer fixture-token');
    expect(headers.get('X-PayPlanner-Restored-Instance')).toBe(ID);
  });

  it('does not start a handshake for an already cancelled request', async () => {
    const transport = vi.fn<typeof fetch>();
    const controller = new AbortController();
    controller.abort();
    await expect(createRestoredRuntimeFetch(API, ID, transport)(`${API}/tasks`, { signal: controller.signal }))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(transport).not.toHaveBeenCalled();
  });
});

describe('restored handshake without AbortSignal.timeout', () => {
  it.each([true, false])('releases the timer after success=%s without using unsupported API', async succeeds => {
    vi.useFakeTimers();
    const timeout = vi.spyOn(AbortSignal, 'timeout').mockImplementation(() => { throw new Error('unsupported'); });
    try {
      const transport = succeeds
        ? vi.fn<typeof fetch>().mockResolvedValue(Response.json({ isolated: true, instanceId: ID }))
        : vi.fn<typeof fetch>().mockRejectedValue(new Error('fixture-secret'));
      const request = createRestoredRuntimeFetch(API, ID, transport);
      if (succeeds) await request.verify();
      else await expect(request.verify()).rejects.toThrow('Рабочий запрос не отправлен');
      expect(timeout).not.toHaveBeenCalled();
      expect(vi.getTimerCount()).toBe(0);
      const signal = transport.mock.calls[0][1]?.signal;
      expect(signal?.aborted).toBe(false);
    } finally { timeout.mockRestore(); vi.useRealTimers(); }
  });
  it('aborts a stalled handshake at ten seconds and sends no business request', async () => {
    vi.useFakeTimers();
    try {
      const transport = vi.fn<typeof fetch>().mockImplementation((_input, options) => new Promise((_resolve, reject) => {
        options?.signal?.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true });
      }));
      const request = createRestoredRuntimeFetch(API, ID, transport);
      const result = expect(request(`${API}/tasks`, { method: 'POST' })).rejects.toThrow('Рабочий запрос не отправлен');
      await vi.advanceTimersByTimeAsync(9_999);
      expect(transport.mock.calls[0][1]?.signal?.aborted).toBe(false);
      await vi.advanceTimersByTimeAsync(1);
      await result;
      await expect(request(`${API}/tasks`)).rejects.toThrow('Рабочий запрос не отправлен');
      expect(transport).toHaveBeenCalledTimes(1);
      expect(transport.mock.calls[0][1]?.signal?.aborted).toBe(true);
      expect(vi.getTimerCount()).toBe(0);
    } finally { vi.useRealTimers(); }
  });
});
