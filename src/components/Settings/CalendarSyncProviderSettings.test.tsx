import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CalendarSyncProviderSettings } from './CalendarSyncProviderSettings';

const apiMock = vi.hoisted(() => ({
  getCalendarSyncProviders: vi.fn(),
  getCalendarSyncReadiness: vi.fn(),
  getCalendarSyncSettings: vi.fn(),
  getCalendarSyncLogs: vi.fn(),
  updateCalendarSyncSettings: vi.fn(),
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
          label: 'Внешние календари в админке',
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
    apiMock.getCalendarSyncSettings.mockResolvedValue({
      externalCalendarSyncEnabled: false,
      syncMode: 'notConfigured',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: null,
    });
    apiMock.getCalendarSyncLogs.mockResolvedValue({
      items: [
        {
          provider: 'google-calendar',
          providerLabel: 'Google Calendar',
          id: 1,
          connectionId: 10,
          userId: 5,
          userName: 'Тестовый пользователь',
          userEmail: 'test@example.com',
          taskJournalItemId: 42,
          direction: 'crm-to-google',
          operation: 'create-event',
          status: 'success',
          message: 'Событие создано.',
          createdAt: '2026-05-26T17:20:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 50,
    });
    apiMock.updateCalendarSyncSettings.mockImplementation(async (payload) => ({
      externalCalendarSyncEnabled: payload.externalCalendarSyncEnabled,
      syncMode: payload.syncMode,
      conflictResolutionMode: payload.conflictResolutionMode,
      userControlMode: payload.userControlMode,
      defaultSyncFromDate: payload.defaultSyncFromDate,
      inactiveUserAction: payload.inactiveUserAction,
      requireManualConfirmation: payload.requireManualConfirmation,
      dryRunConfirmed: payload.dryRunConfirmed,
      notes: payload.notes,
      updatedAt: '2026-05-26T17:10:00Z',
    }));
  });

  it('shows provider diagnostics and readiness checklist', async () => {
    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText('Провайдеры календарной синхронизации')).toBeTruthy();
    expect(screen.getByText('ICS-файлы')).toBeTruthy();
    expect(screen.getAllByText('Google Calendar').length).toBeGreaterThan(0);
    expect(screen.getByText('Модель синхронизации и конфликты')).toBeTruthy();
    expect(screen.getByText('Готовность к подключению Google/Яндекс')).toBeTruthy();
    expect(screen.getByText('Выполнено обязательных шагов: 1 из 2. Пока все обязательные пункты не закрыты, внешняя синхронизация остается подготовительным контуром.')).toBeTruthy();
    expect(screen.getByText('Правила конфликтов')).toBeTruthy();
    expect(screen.getByText('Нужна подготовка')).toBeTruthy();
  });

  it('saves calendar sync conflict settings', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Модель синхронизации и конфликты');
    fireEvent.click(screen.getByLabelText(/Включить Google Calendar и Яндекс Календарь/i));
    fireEvent.change(screen.getByLabelText('Главный источник событий'), { target: { value: 'hybrid' } });
    fireEvent.click(screen.getByLabelText('Dry-run на копии базы выполнен'));
    fireEvent.change(screen.getByLabelText('Комментарий к решению'), { target: { value: 'Согласовано с руководителем.' } });
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/i }));

    await waitFor(() => expect(apiMock.updateCalendarSyncSettings).toHaveBeenCalledWith({
      externalCalendarSyncEnabled: true,
      syncMode: 'hybrid',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: true,
      notes: 'Согласовано с руководителем.',
    }));
    await waitFor(() => expect(apiMock.getCalendarSyncProviders).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Настройки внешних календарей сохранены.')).toBeTruthy();
  });

  it('shows a readable loading error', async () => {
    apiMock.getCalendarSyncProviders.mockRejectedValue(new Error('calendar api down'));

    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText(/calendar api down/i)).toBeTruthy();
  });
});
