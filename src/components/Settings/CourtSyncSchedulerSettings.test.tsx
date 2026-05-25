import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CourtSyncSchedulerSettings } from './CourtSyncSchedulerSettings';

const apiMock = vi.hoisted(() => ({
  getCourtSyncSettings: vi.fn(),
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
    apiMock.updateCourtSyncSettings.mockImplementation(async (payload) => ({
      ...payload,
      featureFlagEnabled: true,
      minIntervalMinutes: 15,
      maxIntervalMinutes: 1440,
      minBatchSize: 1,
      maxBatchSize: 100,
    }));
  });

  it('loads current court sync scheduler settings', async () => {
    render(<CourtSyncSchedulerSettings />);

    expect(await screen.findByText('Автосинхронизация судов')).toBeTruthy();
    expect((screen.getByRole('checkbox', { name: /Включить фоновую проверку/i }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText('Интервал, минут') as HTMLInputElement).value).toBe('120');
    expect((screen.getByLabelText('Дел за запуск') as HTMLInputElement).value).toBe('15');
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
