import { afterEach, describe, expect, it, vi } from 'vitest';

const API = 'http://127.0.0.1:5080/api';
const ID = 'cefe9b7e3bb64c51b3c80a9e96bb57ea';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.resetModules();
  localStorage.clear();
  sessionStorage.clear();
});

function isolated() {
  vi.resetModules();
  vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'true');
  vi.stubEnv('VITE_API_URL', API);
  vi.stubEnv('VITE_RESTORED_RUNTIME_INSTANCE_ID', ID);
}

describe('shared API transport', () => {
  it('leaves ordinary fetch arguments and response untouched', async () => {
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'false');
    const response = new Response('fixture');
    const native = vi.fn<typeof fetch>().mockResolvedValue(response);
    vi.stubGlobal('fetch', native);
    const { apiFetch } = await import('./apiTransport');
    const init = { method: 'POST', body: 'fixture' };
    expect(await apiFetch('/ordinary', init)).toBe(response);
    expect(native).toHaveBeenCalledExactlyOnceWith('/ordinary', init);
  });

  it('shares verification across login, task APIs, uploads and attachment downloads', async () => {
    isolated();
    const native = vi.fn<typeof fetch>().mockImplementation(async input => {
      const url = String(input);
      if (url.endsWith('/restored-runtime/identity')) return Response.json({ isolated: true, instanceId: ID });
      if (url.endsWith('/auth/login')) return Response.json({ token: 'fixture-token', user: { id: 1 } });
      return Response.json({ items: [], total: 0 });
    });
    vi.stubGlobal('fetch', native);
    const { authService } = await import('./authService');
    const { ApiService } = await import('./api');
    const { internalChatApi } = await import('./internalChatApi');
    await authService.login('fixture@fixture.invalid', 'fixture-password');
    const api = new ApiService();
    await Promise.all([
      api.getTaskJournalItems(),
      api.uploadTaskJournalFile(1, new File(['fixture'], 'fixture.txt')),
      api.downloadTaskJournalFile(1, 2),
      internalChatApi.getThreads(),
      internalChatApi.getAttachmentBlob(3),
    ]);
    expect(native.mock.calls.filter(([url]) => String(url).endsWith('/restored-runtime/identity'))).toHaveLength(1);
    for (const [url, options] of native.mock.calls.slice(1)) {
      expect(String(url)).toContain(API);
      expect(new Headers(options?.headers).get('X-PayPlanner-Restored-Instance')).toBe(ID);
    }
    const upload = native.mock.calls.find(([url, options]) => String(url).endsWith('/tasks/1/files') && options?.method === 'POST');
    expect(upload?.[1]?.body).toBeInstanceOf(FormData);
    expect(new Headers(upload?.[1]?.headers).has('Content-Type')).toBe(false);
  });

  it('never sends a login or a chat request to an ordinary local backend', async () => {
    isolated();
    const native = vi.fn<typeof fetch>().mockResolvedValue(new Response('', { status: 404 }));
    vi.stubGlobal('fetch', native);
    const { authService } = await import('./authService');
    const { internalChatApi } = await import('./internalChatApi');
    await expect(authService.login('fixture@fixture.invalid', 'fixture-password')).rejects.toThrow();
    await expect(internalChatApi.getThreads()).rejects.toThrow();
    expect(native).toHaveBeenCalledTimes(1);
    expect(native.mock.calls[0][1]?.body).toBeUndefined();
    expect(native.mock.calls[0][1]?.headers).toBeUndefined();
    expect(localStorage.getItem('pp_token')).toBeNull();
  });
});
