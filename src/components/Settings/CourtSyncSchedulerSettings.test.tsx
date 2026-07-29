import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CourtSyncSchedulerSettings } from './CourtSyncSchedulerSettings';

const apiMock = vi.hoisted(() => ({
  getCourtSyncSettings: vi.fn(),
  getCourtSyncProviders: vi.fn(),
  updateCourtSyncSettings: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

describe('CourtSyncSchedulerSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getCourtSyncSettings.mockResolvedValue({
      isEnabled: true,
      featureFlagEnabled: true,
      intervalMinutes: 120,
      batchSize: 15,
      minIntervalMinutes: 15,
      maxIntervalMinutes: 1440,
      minBatchSize: 1,
      maxBatchSize: 100,
    });
    apiMock.getCourtSyncProviders.mockResolvedValue([
      {
        key: 'sudrf',
        label: 'ГАС Правосудие / sudrf.ru',
        sourceType: 'sudrf',
        schemaVersion: 1,
        capabilities: ['case-search', 'case-binding', 'case-reading', 'document-reading', 'change-detection', 'manual-sync', 'background-sync'],
        status: 'active',
        isAvailable: true,
        featureFlagEnabled: true,
        supportsManualRun: true,
        supportsBackgroundSync: true,
        supportedHosts: ['sudrf.ru'],
        message: 'Поддерживается ручное обновление карточки дела и фоновая синхронизация открытых дел.',
      },
      {
        key: 'kadArbitr',
        label: 'КАД Арбитр',
        sourceType: 'kadArbitr',
        schemaVersion: 1,
        capabilities: [],
        status: 'planned',
        isAvailable: false,
        featureFlagEnabled: true,
        supportsManualRun: false,
        supportsBackgroundSync: false,
        supportedHosts: ['kad.arbitr.ru'],
        message: 'Источник добавлен в дорожную карту.',
      },
    ]);
    apiMock.updateCourtSyncSettings.mockImplementation(async (payload) => ({
      ...payload,
      featureFlagEnabled: true,
      minIntervalMinutes: 15,
      maxIntervalMinutes: 1440,
      minBatchSize: 1,
      maxBatchSize: 100,
    }));
  });

  it('loads current court sync scheduler settings and providers', async () => {
    render(<CourtSyncSchedulerSettings />);

    expect(await screen.findByText('Автосинхронизация судов')).toBeTruthy();
    await waitFor(() => expect((screen.getByRole('checkbox', { name: /Включить фоновую проверку/i }) as HTMLInputElement).checked).toBe(true));
    expect((screen.getByLabelText('Интервал, минут') as HTMLInputElement).value).toBe('120');
    expect((screen.getByLabelText('Дел за запуск') as HTMLInputElement).value).toBe('15');
    expect(screen.getByText('ГАС Правосудие / sudrf.ru')).toBeTruthy();
    expect(screen.getByText('КАД Арбитр')).toBeTruthy();
    expect(screen.getByText('Запланирован')).toBeTruthy();
    expect(screen.getAllByText('Схема v1')).toHaveLength(2);
    expect(screen.getByText('Поиск дел')).toBeTruthy();
    expect(screen.getByText('Документы')).toBeTruthy();
  });

  it('saves normalized scheduler settings', async () => {
    render(<CourtSyncSchedulerSettings />);
    await screen.findByDisplayValue('120');

    fireEvent.click(screen.getByRole('checkbox', { name: /Включить фоновую проверку/i }));
    fireEvent.change(screen.getByLabelText('Интервал, минут'), { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText('Дел за запуск'), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/i }));

    await waitFor(() => expect(apiMock.updateCourtSyncSettings).toHaveBeenCalledWith({
      isEnabled: false,
      intervalMinutes: 15,
      batchSize: 100,
    }));
    expect(await screen.findByText('Настройки судебной синхронизации сохранены.')).toBeTruthy();
  });
});
