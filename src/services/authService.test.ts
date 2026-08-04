import { afterEach, describe, expect, it, vi } from 'vitest';

import { authService } from './authService';

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
});

describe('authService', () => {
  it('loads the current user from the profile endpoint', async () => {
    localStorage.setItem('pp_token', 'token');
    const user = {
      id: 1,
      email: 'admin@example.test',
      fullName: 'Admin',
      role: { id: 1, name: 'admin' },
      isEmployee: true,
      isActive: true,
      isApproved: true,
      createdAt: '2026-06-10T00:00:00Z',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(user), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(authService.getCurrentUser()).resolves.toMatchObject({ id: 1 });

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/profile');
    expect(String(url)).not.toContain('/auth/me');
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer token');
  });

  it('renews a session shortly before token expiration and shares one refresh request', async () => {
    const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
    const expiringToken = `${encode({ alg: 'none' })}.${encode({ exp: Math.floor(Date.now() / 1000) + 60 })}.signature`;
    const renewedToken = `${encode({ alg: 'none' })}.${encode({ exp: Math.floor(Date.now() / 1000) + 604800 })}.signature`;
    localStorage.setItem('pp_token', expiringToken);

    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ token: renewedToken }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(Promise.all([
      authService.ensureFreshSession(),
      authService.ensureFreshSession(),
    ])).resolves.toEqual([renewedToken, renewedToken]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain('/auth/refresh');
    expect((fetchMock.mock.calls[0][1]?.headers as Record<string, string>).Authorization).toBe(`Bearer ${expiringToken}`);
    expect(localStorage.getItem('pp_token')).toBe(renewedToken);
    expect(localStorage.getItem('auth_token')).toBe(renewedToken);
    expect(localStorage.getItem('pp.jwt')).toBe(renewedToken);
  });

  it('revokes server sessions with the current token before local logout', async () => {
    localStorage.setItem('pp_token', 'active-token');
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await authService.revokeSessions();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain('/auth/logout');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
    expect((fetchMock.mock.calls[0][1]?.headers as Record<string, string>).Authorization)
      .toBe('Bearer active-token');
    expect(localStorage.getItem('pp_token')).toBe('active-token');

    authService.logout();
    expect(localStorage.getItem('pp_token')).toBeNull();
  });

  it('does not call logout endpoint without a local session', async () => {
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);

    await authService.revokeSessions();

    expect(fetchMock).not.toHaveBeenCalled();
  });
});
