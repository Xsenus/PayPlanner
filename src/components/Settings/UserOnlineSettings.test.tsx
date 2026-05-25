import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { UserOnlineSettings } from './UserOnlineSettings';

vi.mock('../../services/api', () => ({
  apiService: {
    getUsersOnline: vi.fn(),
  },
}));

describe('UserOnlineSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getUsersOnline).mockReset();
  });

  it('shows online users and last login data', async () => {
    vi.mocked(apiService.getUsersOnline).mockResolvedValue({
      generatedAt: '2026-05-25T08:00:00Z',
      onlineThresholdMinutes: 5,
      totalUsers: 2,
      onlineUsers: 1,
      users: [
        {
          userId: 1,
          fullName: 'Иван Иванов',
          email: 'ivan@example.com',
          roleName: 'admin',
          department: 'Юридический отдел',
          isActive: true,
          isApproved: true,
          isOnline: true,
          lastActivityAt: new Date().toISOString(),
          lastLoginAt: '2026-05-25T07:40:00Z',
          lastLogoutAt: null,
          lastAction: 'heartbeat',
          lastSection: 'session',
          lastIpAddress: '127.0.0.1',
          lastUserAgent: null,
        },
        {
          userId: 2,
          fullName: 'Петр Петров',
          email: 'petr@example.com',
          roleName: 'user',
          department: null,
          isActive: true,
          isApproved: true,
          isOnline: false,
          lastActivityAt: '2026-05-25T06:00:00Z',
          lastLoginAt: '2026-05-25T05:30:00Z',
          lastLogoutAt: '2026-05-25T06:00:00Z',
          lastAction: 'logout',
          lastSection: 'auth',
          lastIpAddress: null,
          lastUserAgent: null,
        },
      ],
    });

    render(<UserOnlineSettings />);

    expect(await screen.findByText('Пользователи онлайн')).toBeTruthy();
    expect(screen.getByText('Иван Иванов')).toBeTruthy();
    expect(screen.getByText('Петр Петров')).toBeTruthy();
    expect(screen.getByText('Онлайн')).toBeTruthy();
    expect(screen.getByText('Офлайн')).toBeTruthy();
    expect(screen.getByText('127.0.0.1')).toBeTruthy();

    await waitFor(() => {
      expect(apiService.getUsersOnline).toHaveBeenCalledTimes(1);
    });
  });

  it('shows loading error', async () => {
    vi.mocked(apiService.getUsersOnline).mockRejectedValue(new Error('network down'));

    render(<UserOnlineSettings />);

    expect(await screen.findByText(/network down/i)).toBeTruthy();
  });
});
