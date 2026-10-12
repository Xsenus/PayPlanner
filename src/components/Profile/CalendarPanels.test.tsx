import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { GoogleCalendarPanel } from './GoogleCalendarPanel';
import { YandexCalendarPanel } from './YandexCalendarPanel';

const apiMock = vi.hoisted(() => ({
  getGoogleCalendarStatus: vi.fn(),
  getYandexCalendarStatus: vi.fn(),
  syncGoogleCalendarNow: vi.fn(),
  syncYandexCalendarNow: vi.fn(),
}));
const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));
const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

describe('calendar profile panels', () => {
  it.each([
    ['Google', 0],
    ['Google', 1],
    ['Yandex', 0],
    ['Yandex', 1],
  ] as const)('shows the correct outcome for %s sync with %s errors', async (provider, errorCount) => {
    const status = {
      isAvailable: true,
      googleOAuthReady: true,
      yandexOAuthReady: true,
      callbackUrl: 'https://crm.example.local/api/calendar/auth/callback',
      connection: {
        id: 1,
        googleEmail: 'user@example.local',
        yandexEmail: 'user@example.local',
        displayName: 'Synthetic user',
        isEnabled: true,
        syncEnabled: true,
        calendarMode: 'personal',
        conflictResolutionMode: 'latestWins',
        manualFullSyncCooldownMinutes: 10,
        manualFullSyncAvailableAt: null,
        createdAt: '2026-10-01T09:00:00Z',
        updatedAt: '2026-10-01T09:00:00Z',
        calendars: [],
      },
      recentLogs: [],
    };
    const message = errorCount > 0 ? 'Синхронизация завершена с ошибками.' : 'Синхронизация выполнена.';
    const getStatus = provider === 'Google' ? apiMock.getGoogleCalendarStatus : apiMock.getYandexCalendarStatus;
    const syncNow = provider === 'Google' ? apiMock.syncGoogleCalendarNow : apiMock.syncYandexCalendarNow;
    getStatus.mockResolvedValue(status);
    syncNow.mockResolvedValue({ status, message, processedTasks: errorCount > 0 ? 0 : 1, importedEvents: 0, errorCount });
    const Panel = provider === 'Google' ? GoogleCalendarPanel : YandexCalendarPanel;
    render(<Panel />);

    fireEvent.click(await screen.findByRole('button', { name: 'Синхронизировать сейчас' }));

    await waitFor(() => expect(syncNow).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(errorCount > 0 ? toastMock.error : toastMock.success).toHaveBeenCalledWith(message));
    expect(errorCount > 0 ? toastMock.success : toastMock.error).not.toHaveBeenCalled();
    expect((screen.getByRole('button', { name: 'Синхронизировать сейчас' }) as HTMLButtonElement).disabled).toBe(false);
  });


  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('explains why Google Calendar is unavailable when disabled by admin', async () => {
    apiMock.getGoogleCalendarStatus.mockResolvedValue({
      isAvailable: false,
      googleOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/google-calendar/auth/callback',
      connection: null,
      recentLogs: [],
    });

    render(<GoogleCalendarPanel />);

    await waitFor(() => expect(apiMock.getGoogleCalendarStatus).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Интеграция отключена администратором и сейчас недоступна для подключения.')).toBeTruthy();
    expect(screen.getByText('Google Calendar')).toBeTruthy();
  });

  it('explains why Yandex Calendar is unavailable when disabled by admin', async () => {
    apiMock.getYandexCalendarStatus.mockResolvedValue({
      isAvailable: false,
      yandexOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/yandex-calendar/auth/callback',
      connection: null,
      recentLogs: [],
    });

    render(<YandexCalendarPanel />);

    await waitFor(() => expect(apiMock.getYandexCalendarStatus).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Интеграция отключена администратором и сейчас недоступна для подключения.')).toBeTruthy();
    expect(screen.getByText('Яндекс.Календарь')).toBeTruthy();
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
