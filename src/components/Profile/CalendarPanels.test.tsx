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
});
