import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  clearOfflineCalendarCache: vi.fn(),
  logout: vi.fn(),
  revokeSessions: vi.fn(),
}));

vi.mock('../services/authService', async (importOriginal) => ({
  ...await importOriginal<typeof import('../services/authService')>(),
  authService: {
    isAuthenticated: () => false,
    revokeSessions: mocks.revokeSessions,
    logout: mocks.logout,
  },
}));

vi.mock('../services/api', () => ({
  AUTH_SESSION_EXPIRED_EVENT: 'payplanner:auth-session-expired',
  apiService: { logUserActivity: vi.fn().mockResolvedValue(undefined) },
}));

vi.mock('../services/offlineCalendarStore', () => ({
  activateOfflineCalendarUser: vi.fn(),
  clearOfflineCalendarCache: mocks.clearOfflineCalendarCache,
  revokeOfflineCalendarSnapshot: vi.fn(),
}));

import { AuthProvider, useAuth } from './AuthContext';

const LogoutButton = () => {
  const { signOut } = useAuth();
  return <button type="button" onClick={() => void signOut()}>Выйти</button>;
};

describe('AuthContext server logout', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.clearOfflineCalendarCache.mockResolvedValue(undefined);
  });

  it('clears local data even when server revocation is temporarily unavailable', async () => {
    mocks.revokeSessions.mockRejectedValue(new Error('network unavailable'));
    render(<AuthProvider><LogoutButton /></AuthProvider>);

    fireEvent.click(await screen.findByRole('button', { name: 'Выйти' }));

    await waitFor(() => expect(mocks.revokeSessions).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(mocks.clearOfflineCalendarCache).toHaveBeenCalledTimes(1));
    expect(mocks.logout).toHaveBeenCalledTimes(1);
  });

  it('revokes server sessions before removing the local token', async () => {
    const order: string[] = [];
    mocks.revokeSessions.mockImplementation(async () => { order.push('server'); });
    mocks.clearOfflineCalendarCache.mockImplementation(async () => { order.push('cache'); });
    mocks.logout.mockImplementation(() => { order.push('token'); });
    render(<AuthProvider><LogoutButton /></AuthProvider>);

    fireEvent.click(await screen.findByRole('button', { name: 'Выйти' }));

    await waitFor(() => expect(mocks.logout).toHaveBeenCalledTimes(1));
    expect(order).toEqual(['server', 'cache', 'token']);
  });
});
