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
});
