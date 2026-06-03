import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { GoogleCalendarPanel } from './GoogleCalendarPanel';
import { YandexCalendarPanel } from './YandexCalendarPanel';

const apiMock = vi.hoisted(() => ({
  getGoogleCalendarStatus: vi.fn(),
  getYandexCalendarStatus: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    error: vi.fn(),
    success: vi.fn(),
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn(),
  }),
}));

describe('calendar profile panels', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('hides Google Calendar card when provider is disabled by admin', async () => {
    apiMock.getGoogleCalendarStatus.mockResolvedValue({
      isAvailable: false,
      googleOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/google-calendar/auth/callback',
      connection: null,
      recentLogs: [],
    });

    render(<GoogleCalendarPanel />);

    await waitFor(() => expect(apiMock.getGoogleCalendarStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByText('Google Calendar')).toBeNull());
  });

  it('hides Yandex Calendar card when provider is disabled by admin', async () => {
    apiMock.getYandexCalendarStatus.mockResolvedValue({
      isAvailable: false,
      yandexOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/yandex-calendar/auth/callback',
      connection: null,
      recentLogs: [],
    });

    render(<YandexCalendarPanel />);

    await waitFor(() => expect(apiMock.getYandexCalendarStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByText('Яндекс.Календарь')).toBeNull());
  });

  it('disables Google manual sync button while full sync cooldown is active', async () => {
    apiMock.getGoogleCalendarStatus.mockResolvedValue({
      isAvailable: true,
      googleOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/google-calendar/auth/callback',
      connection: {
        id: 1,
        googleEmail: 'user@example.com',
        displayName: 'User',
        isEnabled: true,
        syncEnabled: true,
        calendarMode: 'personal',
        conflictResolutionMode: 'latestWins',
        lastFullSyncAt: '2999-01-01T10:00:00Z',
        lastIncrementalSyncAt: '2999-01-01T10:00:00Z',
        manualFullSyncCooldownMinutes: 10,
        manualFullSyncAvailableAt: '2999-01-01T10:10:00Z',
        createdAt: '2999-01-01T09:00:00Z',
        updatedAt: '2999-01-01T10:00:00Z',
        calendars: [],
      },
      recentLogs: [],
    });

    render(<GoogleCalendarPanel />);

    const syncButton = await screen.findByRole('button', { name: /Повторную полную синхронизацию можно запустить после/i });

    expect((syncButton as HTMLButtonElement).disabled).toBe(true);
  });
});
