import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CalendarSyncProviderSettings } from './CalendarSyncProviderSettings';

const apiMock = vi.hoisted(() => ({
  getCalendarSyncProviders: vi.fn(),
  getCalendarSyncReadiness: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

describe('CalendarSyncProviderSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getCalendarSyncProviders.mockResolvedValue([
      {
        key: 'ics-export',
        label: 'ICS-файлы',
        kind: 'ics',
        status: 'active',
        isAvailable: true,
        featureFlagEnabled: true,
        requiresOAuth: false,
        supportsManualExport: true,
        supportsBackgroundSync: false,
        supportsTwoWaySync: false,
        supportedTargets: ['Календарь задач', 'Календарь дел'],
        message: 'ICS доступен.',
      },
      {
        key: 'google-calendar',
        label: 'Google Calendar',
        kind: 'google',
        status: 'requires-setup',
        isAvailable: false,
        featureFlagEnabled: true,
        requiresOAuth: true,
        supportsManualExport: false,
        supportsBackgroundSync: true,
        supportsTwoWaySync: true,
        supportedTargets: ['Календарь задач'],
        message: 'Нужна настройка OAuth.',
      },
    ]);
    apiMock.getCalendarSyncReadiness.mockResolvedValue({
      generatedAt: '2026-05-26T16:00:00Z',
      externalCalendarSyncEnabled: true,
      readyToConnect: false,
      items: [
        {
          key: 'ics-fallback',
          label: 'ICS-выгрузка',
          status: 'ready',
          isReady: true,
          isRequired: false,
          severity: 'success',
          message: 'ICS остается доступен.',
        },
        {
          key: 'feature-flag',
          label: 'Feature flag внешних календарей',
          status: 'ready',
          isReady: true,
          isRequired: true,
          severity: 'success',
          message: 'Включено.',
        },
        {
          key: 'conflict-policy',
          label: 'Правила конфликтов',
          status: 'pending',
          isReady: false,
          isRequired: true,
          severity: 'warning',
          message: 'Нужно утвердить модель конфликтов.',
        },
      ],
    });
  });

  it('shows provider diagnostics and readiness checklist', async () => {
    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText('Провайдеры календарной синхронизации')).toBeTruthy();
    expect(screen.getByText('ICS-файлы')).toBeTruthy();
    expect(screen.getByText('Google Calendar')).toBeTruthy();
    expect(screen.getByText('Готовность к подключению Google/Яндекс')).toBeTruthy();
    expect(screen.getByText('Выполнено обязательных шагов: 1 из 2. Пока все обязательные пункты не закрыты, внешняя синхронизация остается подготовительным контуром.')).toBeTruthy();
    expect(screen.getByText('Правила конфликтов')).toBeTruthy();
    expect(screen.getByText('Нужна подготовка')).toBeTruthy();
  });

  it('shows a readable loading error', async () => {
    apiMock.getCalendarSyncProviders.mockRejectedValue(new Error('calendar api down'));

    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText(/calendar api down/i)).toBeTruthy();
  });
});
