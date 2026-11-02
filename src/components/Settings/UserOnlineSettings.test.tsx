import { documentCopyTestSession } from '../../test/documentCopySession';
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
let sharedTimezoneTestSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { sharedTimezoneTestSession = documentCopyTestSession(); });
afterEach(() => sharedTimezoneTestSession.restore());


import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { UserOnlineSettings } from './UserOnlineSettings';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('UserOnlineSettings', () => {
  beforeEach(() => {
    resetApiServiceMock('getUsersOnline', 'sendUserActivityHeartbeat', 'getUserSetting');
    apiServiceMock.sendUserActivityHeartbeat.mockResolvedValue(undefined);
    apiServiceMock.getUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows online users, translated activity and split refresh time', async () => {
    const nowWithoutTimezone = new Date().toISOString().replace(/Z$/, '');

    apiServiceMock.getUsersOnline.mockResolvedValue({
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
    expect(await screen.findByText('12:00')).toBeTruthy();
    expect(screen.getByText('127.0.0.1')).toBeTruthy();
    expect(screen.getByTestId('user-online-settings').className).toContain('space-y-3');
    expect(screen.getByTestId('user-online-summary-card').className).toContain('p-3');
    expect(screen.getByTestId('user-online-summary-grid').className).toContain('gap-2');
    expect(screen.getByTestId('user-online-table-card')).toBeTruthy();

    await waitFor(() => {
      expect(apiServiceMock.getUsersOnline).toHaveBeenCalledTimes(1);
      expect(apiServiceMock.sendUserActivityHeartbeat).toHaveBeenCalledTimes(1);
    });
  });

  it('auto-refreshes every 10 seconds while the section is visible', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    apiServiceMock.getUsersOnline.mockResolvedValue({
      generatedAt: '2026-05-25T08:00:00',
      onlineThresholdMinutes: 5,
      totalUsers: 0,
      onlineUsers: 0,
      users: [],
    });

    const { unmount } = render(<UserOnlineSettings />);

    await waitFor(() => {
      expect(apiServiceMock.getUsersOnline).toHaveBeenCalledTimes(1);
    });

    expect(intervalSpy).toHaveBeenCalledWith(expect.any(Function), 10000);
    unmount();
    intervalSpy.mockRestore();
  });

  it('shows loading error', async () => {
    apiServiceMock.getUsersOnline.mockRejectedValue(new Error('network down'));

    render(<UserOnlineSettings />);

    expect(await screen.findByText(/network down/i)).toBeTruthy();
  });
});
