import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseBackupAttempts } from './DatabaseBackupAttempts';
import type { DatabaseBackupAttemptDto } from '../../services/api';

const { getAttempts } = vi.hoisted(() => ({ getAttempts: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: { getDatabaseBackupAttempts: getAttempts } }));
const attempt = (id: string, status = 'failed'): DatabaseBackupAttemptDto => ({
  id, trigger: 'manual', scheduleSlot: null, status, stage: 'files', backupSetId: null,
  error: null, startedAt: '2026-09-28T00:00:00Z', updatedAt: '2026-09-28T00:01:00Z',
  completedAt: status === 'running' ? null : '2026-09-28T00:02:00Z',
});

describe('DatabaseBackupAttempts', () => {
  beforeEach(() => { getAttempts.mockReset(); getAttempts.mockResolvedValue({ items: [], nextCursor: null }); });

  it('shows an empty history without invented historical attempts', async () => {
    render(<DatabaseBackupAttempts />);
    await screen.findByText(/Сохранённых запусков пока нет/);
    expect(getAttempts).toHaveBeenCalledWith(undefined);
    expect(screen.queryByRole('button', { name: /восстанов/i })).toBeNull();
    expect((screen.getByRole('button', { name: 'Далее' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['running', 'Выполняется'], ['succeeded', 'Запуск завершён'], ['failed', 'Ошибка'],
    ['cancelled', 'Отменён'], ['interrupted', 'Прерван до подтверждения завершения'],
    ['degraded', 'Локальная копия создана, есть проблемы с отправкой'],
  ])('distinguishes %s from restore readiness', async (status, label) => {
    getAttempts.mockResolvedValue({ items: [attempt('one', status)], nextCursor: null });
    render(<DatabaseBackupAttempts timeZoneId="Europe/Samara" />);
    await screen.findByText(label);
    expect(screen.getByText('Последний этап: Сбор файлов')).toBeTruthy();
    expect(screen.getByText('Связь с готовым комплектом отсутствует.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /восстанов/i })).toBeNull();
    if (status === 'running') expect(screen.getByText(/Завершение не подтверждено/)).toBeTruthy();
  });

  it('paginates and resets to the first page on refresh', async () => {
    getAttempts.mockResolvedValueOnce({ items: [attempt('first')], nextCursor: 'first' })
      .mockResolvedValueOnce({ items: [attempt('last')], nextCursor: null })
      .mockResolvedValueOnce({ items: [attempt('refreshed')], nextCursor: null });
    render(<DatabaseBackupAttempts />);
    await screen.findByText('ID запуска: first');
    fireEvent.click(screen.getByRole('button', { name: 'Далее' }));
    await screen.findByText('ID запуска: last');
    expect(getAttempts).toHaveBeenLastCalledWith('first');
    expect(screen.queryByText('ID запуска: first')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить историю' }));
    await screen.findByText('ID запуска: refreshed');
    expect(getAttempts).toHaveBeenLastCalledWith(undefined);
    expect(screen.getByText('Страница 1')).toBeTruthy();
  });

  it('shows request errors without stale data and can retry', async () => {
    getAttempts.mockRejectedValueOnce(new Error('История недоступна'));
    render(<DatabaseBackupAttempts />);
    await screen.findByRole('alert');
    expect(screen.queryByText(/Сохранённых запусков/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить историю' }));
    await screen.findByText(/Сохранённых запусков пока нет/);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ignores responses from the unmounted history', async () => {
    let resolveOld!: (value: unknown) => void;
    getAttempts.mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve; }))
      .mockResolvedValueOnce({ items: [attempt('new')], nextCursor: null });
    const view = render(<DatabaseBackupAttempts key="old" />);
    await waitFor(() => expect(getAttempts).toHaveBeenCalledTimes(1));
    view.rerender(<DatabaseBackupAttempts key="new" />);
    await screen.findByText('ID запуска: new');
    resolveOld({ items: [attempt('old')], nextCursor: null });
    await waitFor(() => expect(screen.queryByText('ID запуска: old')).toBeNull());
  });
});
