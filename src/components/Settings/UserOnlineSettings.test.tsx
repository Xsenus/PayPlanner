import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { UserOnlineSettings } from './UserOnlineSettings';

vi.mock('../../services/api', () => ({
  apiService: {
    getUsersOnline: vi.fn(),
    sendUserActivityHeartbeat: vi.fn(),
  },
}));

describe('UserOnlineSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getUsersOnline).mockReset();
    vi.mocked(apiService.sendUserActivityHeartbeat).mockReset();
    vi.mocked(apiService.sendUserActivityHeartbeat).mockResolvedValue(undefined);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows online users, translated activity and split refresh time', async () => {
    const nowWithoutTimezone = new Date().toISOString().replace(/Z$/, '');

    vi.mocked(apiService.getUsersOnline).mockResolvedValue({
      generatedAt: '2026-05-25T08:00:00',
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
          lastActivityAt: nowWithoutTimezone,
          lastLoginAt: '2026-05-25T07:40:00',
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
    expect(screen.getByText('только что')).toBeTruthy();
    expect(screen.getByText('Активность в системе')).toBeTruthy();
    expect(screen.getByText('Активность')).toBeTruthy();
    expect(screen.getByText('Обновлено')).toBeTruthy();
    expect(screen.getByText('25.05.2026')).toBeTruthy();
    expect(screen.getByText('127.0.0.1')).toBeTruthy();

    await waitFor(() => {
      expect(apiService.getUsersOnline).toHaveBeenCalledTimes(1);
      expect(apiService.sendUserActivityHeartbeat).toHaveBeenCalledTimes(1);
    });
  });

  it('auto-refreshes every 10 seconds while the section is visible', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    vi.mocked(apiService.getUsersOnline).mockResolvedValue({
      generatedAt: '2026-05-25T08:00:00',
      onlineThresholdMinutes: 5,
      totalUsers: 0,
      onlineUsers: 0,
      users: [],
    });

    const { unmount } = render(<UserOnlineSettings />);

    await waitFor(() => {
      expect(apiService.getUsersOnline).toHaveBeenCalledTimes(1);
    });

    expect(intervalSpy).toHaveBeenCalledWith(expect.any(Function), 10000);
    unmount();
    intervalSpy.mockRestore();
  });

  it('shows loading error', async () => {
    vi.mocked(apiService.getUsersOnline).mockRejectedValue(new Error('network down'));

    render(<UserOnlineSettings />);

    expect(await screen.findByText(/network down/i)).toBeTruthy();
  });
});
