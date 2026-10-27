import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { webcrypto } from 'node:crypto';
import { authService, getAuthSessionEpoch, type User } from './authService';

const token = (actor: number, nonce = 'old') => `header.${btoa(JSON.stringify({ sub: String(actor), nonce }))}.signature`;
const user = (id: number) => ({ id } as User);
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function json(value: unknown) { return new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } }); }
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.stubGlobal('crypto', webcrypto); localStorage.setItem('pp_token', token(1)); });
afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); sessionStorage.clear(); vi.unstubAllGlobals(); });

describe('shared auth-session epoch required by guarded payment creation', () => {
  it('lazily adopts an existing persisted login without logout or a profile/network request', () => {
    const request = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', request);
    const epoch = getAuthSessionEpoch();
    expect(epoch).toBeTruthy(); expect(getAuthSessionEpoch()).toBe(epoch);
    expect(localStorage.getItem('pp_token')).toBe(token(1)); expect(request).not.toHaveBeenCalled();
  });
  it('keeps the same epoch during an ordinary refresh of this login', async () => {
    const epoch = getAuthSessionEpoch();
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(json({ token: token(1, 'refreshed') })));
    expect(await authService.ensureFreshSession(true)).toBe(token(1, 'refreshed'));
    expect(getAuthSessionEpoch()).toBe(epoch); expect(localStorage.getItem('pp_token')).toBe(token(1, 'refreshed'));
  });
  it('does not restore an old token after logout while refresh waits', async () => {
    const response = deferred<Response>(); const request = vi.fn<typeof fetch>().mockReturnValue(response.promise); vi.stubGlobal('fetch', request);
    const refresh = authService.ensureFreshSession(true);
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    authService.logout(); response.resolve(json({ token: token(1, 'late') }));
    await expect(refresh).rejects.toThrow('Сессия изменилась');
    expect(localStorage.getItem('pp_token')).toBeNull(); expect(getAuthSessionEpoch()).toBeNull();
  });
  it('rejects an old refresh after A→B→A and preserves the newest login token/epoch', async () => {
    const response = deferred<Response>(); const request = vi.fn<typeof fetch>().mockReturnValue(response.promise); vi.stubGlobal('fetch', request);
    const refresh = authService.ensureFreshSession(true);
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    await authService.completeSignIn(token(2), user(2));
    await authService.completeSignIn(token(1, 'new-login'), user(1));
    const newest = getAuthSessionEpoch(); response.resolve(json({ token: token(1, 'late-old-refresh') }));
    await expect(refresh).rejects.toThrow('Сессия изменилась');
    expect(localStorage.getItem('pp_token')).toBe(token(1, 'new-login')); expect(getAuthSessionEpoch()).toBe(newest);
  });
  it('does not return its captured old token from a non-forced refresh after replacement', async () => {
    const expired = `header.${btoa(JSON.stringify({ sub: '1', exp: 1 }))}.signature`;
    localStorage.setItem('pp_token', expired);
    const response = deferred<Response>(); vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockReturnValueOnce(response.promise));
    const refresh = authService.ensureFreshSession(false);
    const rejected = expect(refresh).rejects.toThrow('Сессия изменилась');
    await authService.completeSignIn(token(2), user(2));
    response.resolve(json({ token: token(1, 'old-refresh') })); await rejected;
    expect(localStorage.getItem('pp_token')).toBe(token(2));
  });
  it('starts replacement-epoch refresh independently, and old cleanup cannot clear the new matching job', async () => {
    const first = deferred<Response>(); const second = deferred<Response>();
    const request = vi.fn<typeof fetch>().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    vi.stubGlobal('fetch', request);
    const old = authService.ensureFreshSession(true); const oldRejected = expect(old).rejects.toThrow('Сессия изменилась');
    await authService.completeSignIn(token(2), user(2));
    const current = authService.ensureFreshSession(true);
    first.resolve(json({ token: token(1, 'late') })); await oldRejected;
    const joined = authService.ensureFreshSession(true);
    expect(request).toHaveBeenCalledTimes(2);
    second.resolve(json({ token: token(2, 'refreshed') }));
    expect(await current).toBe(token(2, 'refreshed')); expect(await joined).toBe(token(2, 'refreshed'));
    expect(request).toHaveBeenCalledTimes(2);
  });
});
