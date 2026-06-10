import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SystemJobDiagnosticsSettings } from './SystemJobDiagnosticsSettings';

const apiMock = vi.hoisted(() => ({
  getUserSetting: vi.fn(),
  getSystemJobDiagnostics: vi.fn(),
  runSystemJob: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

describe('SystemJobDiagnosticsSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    apiMock.getSystemJobDiagnostics.mockResolvedValue({
      generatedAt: '2026-05-26T12:00:00Z',
      items: [
        {
          key: 'database-backup',
          name: 'Резервное копирование базы данных',
          description: 'Проверяет расписание.',
          enabled: true,
          status: 'active',
          statusText: 'Включено',
          intervalText: 'Расписание: 02:00,14:00',
          pendingCount: null,
          lastProcessedCount: null,
          lastActivityAt: '2026-05-26T10:00:00Z',
          lastActivityDescription: 'Создана автоматическая копия.',
          nextRunHint: 'Следующая проверка расписания выполняется фоновым сервисом.',
          lastRunStatus: 'success',
          lastRunStatusText: 'Успешно',
          lastRunStartedAt: '2026-05-26T09:59:00Z',
          lastRunFinishedAt: '2026-05-26T10:00:00Z',
          lastSuccessAt: '2026-05-26T10:00:00Z',
          lastRunDurationMs: 60000,
          lastRunError: null,
          manualRunEnabled: true,
          manualRunHint: 'Можно вручную создать резервную копию базы данных.',
        },
        {
          key: 'court-sync',
          name: 'Синхронизация судебных источников',
          description: 'Проверяет дела с привязкой.',
          enabled: false,
          status: 'blocked',
          statusText: 'Ожидает feature flag',
          intervalText: 'Каждые 90 мин.',
          pendingCount: 0,
          lastProcessedCount: 12,
          lastActivityAt: null,
          lastActivityDescription: null,
          nextRunHint: 'Фоновая судебная синхронизация не запускается автоматически.',
          lastRunStatus: 'error',
          lastRunStatusText: 'Ошибка',
          lastRunStartedAt: '2026-05-26T11:00:00Z',
          lastRunFinishedAt: '2026-05-26T11:00:10Z',
          lastSuccessAt: '2026-05-26T10:30:00Z',
          lastRunDurationMs: 10000,
          lastRunError: 'feature flag disabled',
          manualRunEnabled: false,
          manualRunHint: 'Ручной запуск доступен только после включения feature flag.',
        },
      ],
    });
    apiMock.runSystemJob.mockResolvedValue({
      jobKey: 'database-backup',
      startedAt: '2026-05-26T12:05:00Z',
      finishedAt: '2026-05-26T12:05:02Z',
      processedCount: 1,
      skippedCount: null,
      summary: 'Создана ручная резервная копия базы данных.',
      diagnostics: {
        generatedAt: '2026-05-26T12:05:02Z',
        items: [],
      },
    });
  });

  it('shows background job diagnostics and summary', async () => {
    render(<SystemJobDiagnosticsSettings />);

    expect(await screen.findByText('Фоновые сервисы')).toBeTruthy();
    expect(await screen.findByText('Резервное копирование базы данных')).toBeTruthy();
    expect(screen.getByText('Синхронизация судебных источников')).toBeTruthy();
    expect(screen.getByText('Расписание: 02:00,14:00')).toBeTruthy();
    expect(screen.getByText('Ожидает feature flag')).toBeTruthy();
    expect(screen.getByText('Успешно')).toBeTruthy();
    expect(screen.getByText('feature flag disabled')).toBeTruthy();
    expect(screen.getAllByText('Последний успех').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Длительность').length).toBeGreaterThan(0);
    expect(screen.getByText('1 мин. 00 сек.')).toBeTruthy();
    expect(screen.getByText('10 сек.')).toBeTruthy();
    expect(screen.getByText('Всего сервисов')).toBeTruthy();
    expect(screen.getByText('Ожидает условий')).toBeTruthy();
  });

  it('formats diagnostic timestamps in profile time zone', async () => {
    render(<SystemJobDiagnosticsSettings />);

    expect(await screen.findByText(/Обновлено: 26\.05\.2026, 16:00/)).toBeTruthy();
    expect(screen.getAllByText(/26\.05\.2026, 14:00/).length).toBeGreaterThan(0);
  });

  it('refreshes diagnostics by button', async () => {
    render(<SystemJobDiagnosticsSettings />);

    await screen.findByText('Резервное копирование базы данных');
    fireEvent.click(screen.getByRole('button', { name: /Обновить/i }));

    await waitFor(() => expect(apiMock.getSystemJobDiagnostics).toHaveBeenCalledTimes(2));
  });

  it('runs enabled job manually and refreshes diagnostics from response', async () => {
    render(<SystemJobDiagnosticsSettings />);

    fireEvent.click(await screen.findByRole('button', { name: /Запустить/i }));

    await waitFor(() => expect(apiMock.runSystemJob).toHaveBeenCalledWith('database-backup'));
  });

  it('shows readable loading error', async () => {
    apiMock.getSystemJobDiagnostics.mockRejectedValue(new Error('diagnostics unavailable'));

    render(<SystemJobDiagnosticsSettings />);

    expect(await screen.findByText(/diagnostics unavailable/i)).toBeTruthy();
  });
});
