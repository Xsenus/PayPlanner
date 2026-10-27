import { StrictMode } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider, useAuth } from './AuthContext';
import { authService, type User } from '../services/authService';
import { webcrypto } from 'node:crypto';

const cache = vi.hoisted(() => ({ activate: vi.fn(), clear: vi.fn(), revoke: vi.fn() }));
vi.mock('../services/offlineCalendarStore', () => ({ activateOfflineCalendarUser: cache.activate, clearOfflineCalendarCache: cache.clear, revokeOfflineCalendarSnapshot: cache.revoke }));
vi.mock('../services/api', () => ({ AUTH_SESSION_EXPIRED_EVENT: 'owned-session-expired', apiService: { logUserActivity: vi.fn().mockResolvedValue(undefined) } }));
let context!: ReturnType<typeof useAuth>;
function Probe() { context = useAuth(); return <span data-testid="actor">{context.user?.id ?? 'none'}</span>; }
const user = (id: number) => ({ id, email: `actor${id}@example.local`, role: { id: 1, name: 'admin' } } as User);
const token = (id: number) => `header.${btoa(JSON.stringify({ sub: String(id) }))}.signature`;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
beforeEach(() => {
  vi.stubGlobal('crypto', webcrypto);
  localStorage.clear(); localStorage.setItem('pp_token', token(1)); localStorage.setItem('pp.auth-session-epoch.v1', 'original-A');
  cache.activate.mockReset().mockResolvedValue(undefined); cache.clear.mockReset().mockResolvedValue(undefined);
  cache.revoke.mockReset().mockResolvedValue(undefined);
});
afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); vi.unstubAllGlobals(); });

describe('AuthProvider applies only the current shared session and operation', () => {
  it('refreshes the user and blocks the old calendar snapshot before applying a changed role', async () => {
    const updated = { ...user(1), role: { id: 2, name: 'lawyer' } } as User;
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1)).mockResolvedValueOnce(updated);
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.role.id).toBe(1));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'user-role-changed' },
    })));
    await waitFor(() => expect(context.user?.role.id).toBe(2));
    expect(cache.revoke).toHaveBeenCalledWith(1);
    expect(cache.revoke.mock.invocationCallOrder[0]).toBeLessThan(cache.activate.mock.invocationCallOrder[1]);
  });

  it('keeps the current session when role refresh fails due to network', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1)).mockRejectedValueOnce(new Error('network unavailable'));
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'user-role-changed' },
    })));
    await waitFor(() => expect(authService.getCurrentUser).toHaveBeenCalledTimes(2));
    expect(context.user?.id).toBe(1);
    expect(logout).not.toHaveBeenCalled();
    expect(cache.clear).not.toHaveBeenCalled();
  });

  it.each([401, 403, 404])('clears the session when role refresh receives %s', async (status) => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1))
      .mockRejectedValueOnce(Object.assign(new Error('Unavailable'), { status }));
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'user-role-changed' },
    })));
    await waitFor(() => expect(context.user).toBeNull());
    expect(logout).toHaveBeenCalledOnce();
    expect(cache.clear).toHaveBeenCalledOnce();
  });

  it('rechecks the current user after reconnecting', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValue(user(1));
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'reconnected' },
    })));
    await waitFor(() => expect(authService.getCurrentUser).toHaveBeenCalledTimes(2));
  });

  it('retries a failed role refresh when the connection returns', async () => {
    const updated = { ...user(1), role: { id: 2, name: 'lawyer' } } as User;
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1))
      .mockRejectedValueOnce(new Error('network unavailable')).mockResolvedValueOnce(updated);
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    await act(async () => {
      window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
        detail: { source: 'realtime', reason: 'user-role-changed' },
      }));
      await Promise.resolve();
    });
    expect(context.user?.role.id).toBe(1);
    act(() => window.dispatchEvent(new Event('online')));
    await waitFor(() => expect(context.user?.role.id).toBe(2));
    expect(cache.revoke).toHaveBeenCalledWith(1);
  });

  it('ignores a late role refresh after another user signs in', async () => {
    const old = deferred<User>();
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1)).mockReturnValueOnce(old.promise);
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'user-role-changed' },
    })));
    await waitFor(() => expect(authService.getCurrentUser).toHaveBeenCalledTimes(2));
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    await act(async () => { old.resolve({ ...user(1), role: { id: 2, name: 'lawyer' } } as User); await old.promise; });
    expect(context.user?.id).toBe(2);
    expect(cache.revoke).not.toHaveBeenCalled();
  });

  it('does not let a role-change signal cancel an in-progress logout', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValue(user(1));
    const revoke = deferred<void>();
    vi.spyOn(authService, 'revokeSessions').mockReturnValueOnce(revoke.promise);
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(context.user?.id).toBe(1));
    let pending!: Promise<void>;
    act(() => { pending = context.signOut(); });
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'user-role-changed' },
    })));
    await act(async () => { revoke.resolve(); await pending; });
    expect(context.user).toBeNull();
    expect(logout).toHaveBeenCalledOnce();
  });
  it('ignores a stale profile response after StrictMode remount', async () => {
    const first = deferred<User>();
    const second = deferred<User>();
    const fetchProfile = vi.spyOn(authService, 'getCurrentUser')
      .mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const logout = vi.spyOn(authService, 'logout');
    render(<StrictMode><AuthProvider><Probe /></AuthProvider></StrictMode>);
    await waitFor(() => expect(fetchProfile).toHaveBeenCalledTimes(2));
    await act(async () => { second.resolve(user(1)); await second.promise; });
    await waitFor(() => expect(screen.getByTestId('actor').textContent).toBe('1'));
    await act(async () => { first.resolve(user(2)); await first.promise; });
    expect(screen.getByTestId('actor').textContent).toBe('1');
    expect(logout).not.toHaveBeenCalled();
  });
  it('does not reload the profile when its parent rerenders', async () => {
    const getCurrentUser = vi.spyOn(authService, 'getCurrentUser').mockResolvedValue(user(1));
    const view = render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(screen.getByTestId('actor').textContent).toBe('1'));
    view.rerender(<AuthProvider><Probe /></AuthProvider>);
    expect(getCurrentUser).toHaveBeenCalledTimes(1);
  });
  it.each(['success', 'failure'] as const)('ignores late initial-profile %s after B signed in', async (outcome) => {
    const old = deferred<User>(); vi.spyOn(authService, 'getCurrentUser').mockReturnValueOnce(old.promise);
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(authService.getCurrentUser).toHaveBeenCalledTimes(1));
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    expect(screen.getByTestId('actor').textContent).toBe('2');
    await act(async () => { if (outcome === 'success') old.resolve(user(1)); else old.reject(new Error('old profile failed')); });
    expect(screen.getByTestId('actor').textContent).toBe('2'); expect(logout).not.toHaveBeenCalled();
    expect(cache.clear).not.toHaveBeenCalled();
  });
  it('does not apply an old user after awaited offline activation yields to a newer login', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1));
    const old = deferred<void>(); let oldGuard!: () => boolean;
    cache.activate.mockImplementationOnce(async (_id: number, guard: () => boolean) => { oldGuard = guard; await old.promise; });
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(cache.activate).toHaveBeenCalledTimes(1));
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    expect(oldGuard()).toBe(false);
    await act(async () => { old.resolve(); });
    expect(screen.getByTestId('actor').textContent).toBe('2');
  });
  it('does not apply a regular signIn success after its awaited activation was superseded by B', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1));
    render(<AuthProvider><Probe /></AuthProvider>);
    await waitFor(() => expect(screen.getByTestId('actor').textContent).toBe('1'));
    const old = deferred<void>(); let guard!: () => boolean;
    cache.activate.mockImplementationOnce(async (_id: number, current: () => boolean) => { guard = current; await old.promise; });
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ token: token(1), user: user(1) }),
      { headers: { 'content-type': 'application/json' } })));
    let pending!: Promise<void>; act(() => { pending = context.signIn('actor1@example.local', 'fixture-password'); });
    await waitFor(() => expect(cache.activate).toHaveBeenCalledTimes(2));
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    expect(guard()).toBe(false); await act(async () => { old.resolve(); await pending; });
    expect(screen.getByTestId('actor').textContent).toBe('2');
  });
  it.each(['resolve', 'reject'] as const)('does not clear a newer login after an old signOut revoke %s', async (outcome) => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1));
    const revoke = deferred<void>(); vi.spyOn(authService, 'revokeSessions').mockReturnValueOnce(revoke.promise);
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>); await waitFor(() => expect(screen.getByTestId('actor').textContent).toBe('1'));
    let pending!: Promise<void>; act(() => { pending = context.signOut(); });
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    await act(async () => { if (outcome === 'resolve') revoke.resolve(); else revoke.reject(new Error('old revoke failed')); await pending; });
    expect(screen.getByTestId('actor').textContent).toBe('2'); expect(logout).not.toHaveBeenCalled(); expect(cache.clear).not.toHaveBeenCalled();
  });
  it('gates an already awaited destructive cache cleanup and logout after a replacement login', async () => {
    vi.spyOn(authService, 'getCurrentUser').mockResolvedValueOnce(user(1));
    vi.spyOn(authService, 'revokeSessions').mockResolvedValueOnce(undefined);
    const old = deferred<void>(); let clearGuard!: () => boolean;
    cache.clear.mockImplementationOnce(async (guard: () => boolean) => { clearGuard = guard; await old.promise; });
    const logout = vi.spyOn(authService, 'logout');
    render(<AuthProvider><Probe /></AuthProvider>); await waitFor(() => expect(screen.getByTestId('actor').textContent).toBe('1'));
    let pending!: Promise<void>; act(() => { pending = context.signOut(); });
    await waitFor(() => expect(cache.clear).toHaveBeenCalledTimes(1));
    await act(async () => { await context.signInWithToken(token(2), user(2)); });
    expect(clearGuard()).toBe(false); await act(async () => { old.resolve(); await pending; });
    expect(screen.getByTestId('actor').textContent).toBe('2'); expect(logout).not.toHaveBeenCalled();
  });
});
