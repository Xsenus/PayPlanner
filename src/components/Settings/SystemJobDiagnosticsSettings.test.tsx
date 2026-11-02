import { documentCopyTestSession } from '../../test/documentCopySession';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi, afterEach } from 'vitest';
let sharedTimezoneTestSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { sharedTimezoneTestSession = documentCopyTestSession(); });
afterEach(() => sharedTimezoneTestSession.restore());


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
          courtSyncMetrics: {
            startedAt: '2026-05-26T08:00:00Z',
            requestCount: 2,
            failedRequestCount: 1,
            averageLatencyMs: 200,
            maxLatencyMs: 280,
            duplicateSkipCount: 1,
            documentCount: 1,
            documentBytes: 4096,
            retained: {
              retentionDays: 90,
              periodStartedOn: '2026-05-01',
              periodEndedOn: '2026-05-26',
              requestCount: 120,
              failedRequestCount: 3,
              averageLatencyMs: 180,
              maxLatencyMs: 450,
              duplicateSkipCount: 8,
              documentCount: 12,
              documentBytes: 16384,
            },
          },
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
    expect(screen.getByText('Метрики судебных источников')).toBeTruthy();
    expect(screen.getAllByText('Запросы')).toHaveLength(2);
    expect(screen.getAllByText('Повторы исключены')).toHaveLength(2);
    expect(screen.getByText('200 мс / 280 мс')).toBeTruthy();
    expect(screen.getByText('4.0 КБ')).toBeTruthy();
    expect(screen.getByText('Сводка за 90 дней')).toBeTruthy();
    expect(screen.getByText('01.05.2026 — 26.05.2026')).toBeTruthy();
    expect(screen.getByText('16.0 КБ')).toBeTruthy();
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
