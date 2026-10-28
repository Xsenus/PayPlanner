import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TaskTimerPanel } from './TaskTimerPanel';

const api = vi.hoisted(() => ({ getCurrentTaskTimer: vi.fn(), getTaskTimerHistory: vi.fn(), getTaskTimerCorrections: vi.fn(), startTaskTimer: vi.fn(), stopTaskTimer: vi.fn(), correctTaskTimer: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: api }));
const running = { id: 'c89554b9-91b2-44e8-a8db-666ecdddcfa1', taskId: 101, startedAtUtc: '2026-10-27T10:00:00Z', stoppedAtUtc: null, correctedSeconds: null, revision: 0 };

describe('TaskTimerPanel', () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.clearAllMocks();
    api.getCurrentTaskTimer.mockResolvedValue(null);
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  it('updates the approximate elapsed time without polling and stops the clock on unmount', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-27T10:00:30Z'));
    api.getCurrentTaskTimer.mockResolvedValue(running);
    const view = render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Прошло примерно: 0 мин 30 с')).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByText('Прошло примерно: 0 мин 32 с')).toBeTruthy();
    expect(api.getCurrentTaskTimer).toHaveBeenCalledTimes(1);
    view.unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('starts and stops the current employee timer', async () => {
    api.startTaskTimer.mockResolvedValue(running);
    api.stopTaskTimer.mockResolvedValue({ ...running, stoppedAtUtc: '2026-10-27T10:10:00Z' });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Запустить таймер' }));
    await screen.findByText('Таймер этой задачи запущен.');
    expect(api.startTaskTimer).toHaveBeenCalledWith(101, expect.any(String));
    fireEvent.click(screen.getByRole('button', { name: 'Остановить таймер' }));
    await screen.findByText('Таймер не запущен.');
    expect(api.stopTaskTimer).toHaveBeenCalledWith(running.id);
  });

  it('retains and reuses the same request after an uncertain response and reopening', async () => {
    api.startTaskTimer.mockRejectedValueOnce(new Error('network')).mockResolvedValueOnce(running);
    const view = render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Запустить таймер' }));
    await screen.findByRole('alert');
    const first = api.startTaskTimer.mock.calls[0][1];
    view.unmount();
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Запустить таймер' }));
    await screen.findByText('Таймер этой задачи запущен.');
    expect(api.startTaskTimer.mock.calls[1][1]).toBe(first);
  });

  it('reuses the same pending start in two open panels for the same employee and task', async () => {
    const complete: Array<() => void> = [];
    api.startTaskTimer.mockImplementation((_taskId, id) => new Promise((resolve) => {
      complete.push(() => resolve({ ...running, id }));
    }));
    const first = render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    const second = render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    const firstPanel = within(first.container);
    const secondPanel = within(second.container);
    await waitFor(() => {
      expect(firstPanel.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true);
      expect(secondPanel.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true);
    });
    fireEvent.click(firstPanel.getByRole('button', { name: 'Запустить таймер' }));
    fireEvent.click(secondPanel.getByRole('button', { name: 'Запустить таймер' }));
    expect(api.startTaskTimer).toHaveBeenCalledTimes(2);
    expect(api.startTaskTimer.mock.calls[0][1]).toBe(api.startTaskTimer.mock.calls[1][1]);
    await act(async () => { complete.forEach((resolve) => resolve()); });
    expect(await firstPanel.findByText('Таймер этой задачи запущен.')).toBeTruthy();
    expect(await secondPanel.findByText('Таймер этой задачи запущен.')).toBeTruthy();
  });

  it('acknowledges a recovered start so the next timer receives a fresh request ID', async () => {
    api.startTaskTimer.mockRejectedValueOnce(new Error('network')).mockImplementationOnce(async (_taskId, id) => ({ ...running, id }));
    api.stopTaskTimer.mockResolvedValue({ ...running, stoppedAtUtc: '2026-10-27T10:10:00Z' });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Запустить таймер' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Запустить таймер' }));
    await screen.findByRole('alert');
    const first = api.startTaskTimer.mock.calls[0][1];
    api.getCurrentTaskTimer.mockResolvedValueOnce({ ...running, id: first });
    fireEvent.click(screen.getByRole('button', { name: 'Обновить состояние' }));
    await screen.findByText('Таймер этой задачи запущен.');
    fireEvent.click(screen.getByRole('button', { name: 'Остановить таймер' }));
    await screen.findByText('Таймер не запущен.');
    fireEvent.click(screen.getByRole('button', { name: 'Запустить таймер' }));
    await screen.findByText('Таймер этой задачи запущен.');
    expect(api.startTaskTimer.mock.calls[1][1]).not.toBe(first);
  });

  it('allows stopping another task without revealing its identity', async () => {
    api.getCurrentTaskTimer.mockResolvedValue({ ...running, taskId: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart={false} />);
    await screen.findByText('У вас уже запущен таймер другой задачи.');
    expect(screen.getByRole('button', { name: 'Остановить таймер' })).not.toHaveProperty('disabled', true);
  });

  it('reconciles an uncertain stop before offering another start', async () => {
    api.getCurrentTaskTimer.mockResolvedValueOnce(running).mockResolvedValueOnce(null);
    api.stopTaskTimer.mockRejectedValueOnce(new Error('response lost'));
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await screen.findByText('Таймер этой задачи запущен.');
    fireEvent.click(screen.getByRole('button', { name: 'Остановить таймер' }));
    await screen.findByRole('alert');
    expect(screen.getByText('Состояние таймера не подтверждено.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Запустить таймер' })).toBeNull();
    expect(api.startTaskTimer).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить состояние' }));
    await screen.findByText('Таймер не запущен.');
    expect((screen.getByRole('button', { name: 'Запустить таймер' }) as HTMLButtonElement).disabled).toBe(false);
    expect(api.stopTaskTimer).toHaveBeenCalledTimes(1);
  });

  it('does not offer a start when the current state cannot be read', async () => {
    api.getCurrentTaskTimer.mockRejectedValue(new Error('network'));
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await screen.findByRole('alert');
    expect(screen.getByRole('button', { name: 'Запустить таймер' })).toHaveProperty('disabled', true);
  });

  it('loads personal history only on request and reports a truncated list', async () => {
    const cursor = { beforeStartedAtUtc: running.startedAtUtc, beforeId: running.id };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [{ ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' }], hasMore: true, nextCursor: cursor })
      .mockResolvedValueOnce({ items: [{ ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' }], hasMore: false, nextCursor: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    expect(api.getTaskTimerHistory).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    expect((await screen.findByText(/Завершённые интервалы \(загруженные\)/)).textContent).toContain('37 мин');
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние записи' }));
    await waitFor(() => expect(api.getTaskTimerHistory).toHaveBeenLastCalledWith(101, cursor));
    expect((await screen.findAllByText(/37 мин/))).toHaveLength(2);
  });

  it('calculates a stopped interval across midnight and the year boundary', async () => {
    const overnight = { ...running, startedAtUtc: '2026-12-31T23:59:30Z', stoppedAtUtc: '2027-01-01T00:01:00Z' };
    api.getTaskTimerHistory.mockResolvedValue({ items: [overnight], hasMore: false, nextCursor: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    expect(await screen.findByText('Завершённые интервалы: 1 мин 30 с')).toBeTruthy();
    expect(api.getTaskTimerHistory).toHaveBeenCalledWith(101, undefined);
  });

  it('shows timer and correction timestamps in the profile time zone', async () => {
    const stopped = { ...running, startedAtUtc: '2026-12-31T23:59:30Z', stoppedAtUtc: '2027-01-01T00:01:00Z' };
    api.getCurrentTaskTimer.mockResolvedValue(running);
    api.getTaskTimerHistory.mockResolvedValue({ items: [stopped], hasMore: false, nextCursor: null });
    api.getTaskTimerCorrections.mockResolvedValue({ items: [{ id: '12', previousSeconds: null, newSeconds: 60, reason: 'Уточнение', createdAtUtc: '2027-01-01T00:02:00Z' }], nextBeforeId: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart timeZoneId="Europe/Moscow" />);
    expect(await screen.findByText(/Начало: 27\.10\.2026, 13:00:00/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    expect(await screen.findByText(/01\.01\.2027, 02:59:30.*01\.01\.2027, 03:01:00/)).toBeTruthy();
    expect(screen.getByText('Завершённые интервалы: 1 мин 30 с')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'История исправлений' }));
    expect(await screen.findByText(/01\.01\.2027, 03:02:00.*Уточнение/)).toBeTruthy();
  });

  it('treats an unsuffixed server UTC timestamp as UTC for elapsed time', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-27T10:00:30Z'));
    api.getCurrentTaskTimer.mockResolvedValue({ ...running, startedAtUtc: '2026-10-27T10:00:00' });
    render(<TaskTimerPanel taskId={101} userId={4} canStart timeZoneId="Asia/Novosibirsk" />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Прошло примерно: 0 мин 30 с')).toBeTruthy();
    expect(screen.getByText(/Начало: 27\.10\.2026, 17:00:00/)).toBeTruthy();
  });

  it('corrects a stopped interval with a reason and current revision', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    api.getTaskTimerHistory.mockResolvedValue({ items: [stopped], hasMore: false, nextCursor: null });
    api.correctTaskTimer.mockResolvedValue({ ...stopped, correctedSeconds: 1200, revision: 1 });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Исправить длительность' }));
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '20' } });
    fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Перерыв' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    expect(await screen.findByText(/20 мин \(исправлено\)/)).toBeTruthy();
    expect(screen.getByText('Завершённые интервалы: 20 мин')).toBeTruthy();
    expect(api.correctTaskTimer).toHaveBeenCalledWith(101, running.id, 0, 1200, 'Перерыв');
  });

  it('keeps loaded intervals when an older history page fails and retries the same cursor', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    const older = { ...stopped, id: 'c89554b9-91b2-44e8-a8db-666ecdddcfa2', startedAtUtc: '2026-10-26T10:00:00Z', stoppedAtUtc: '2026-10-26T10:10:00Z' };
    const cursor = { beforeStartedAtUtc: stopped.startedAtUtc, beforeId: stopped.id };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [stopped], hasMore: true, nextCursor: cursor })
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce({ items: [older], hasMore: false, nextCursor: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    await screen.findByText(/Завершённые интервалы \(загруженные\): 37 мин/);
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние записи' }));
    await screen.findByRole('alert');
    expect(screen.getByText(/Завершённые интервалы \(загруженные\): 37 мин/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние записи' }));
    await screen.findByText('Завершённые интервалы: 47 мин');
    expect(api.getTaskTimerHistory.mock.calls.slice(1)).toEqual([[101, cursor], [101, cursor]]);
  });

  it('keeps loaded corrections and the interval when an older correction page fails', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    const first = { id: '12', previousSeconds: null, newSeconds: 1200, reason: 'Перерыв', createdAtUtc: running.startedAtUtc };
    const older = { id: '11', previousSeconds: null, newSeconds: 900, reason: 'Уточнение', createdAtUtc: running.startedAtUtc };
    api.getTaskTimerHistory.mockResolvedValue({ items: [stopped], hasMore: false, nextCursor: null });
    api.getTaskTimerCorrections.mockResolvedValueOnce({ items: [first], nextBeforeId: '12' })
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce({ items: [older], nextBeforeId: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'История исправлений' }));
    await screen.findByText(/Перерыв/);
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние исправления' }));
    await screen.findByRole('alert');
    expect(screen.getByText(/Перерыв/)).toBeTruthy();
    expect(screen.getByText('Завершённые интервалы: 37 мин')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние исправления' }));
    await screen.findByText(/Уточнение/);
    expect(api.getTaskTimerCorrections.mock.calls.slice(1)).toEqual([[101, running.id, '12'], [101, running.id, '12']]);
  });

  it('shows correction reasons on demand and loads older changes without rounding identifiers', async () => {
    api.getTaskTimerHistory.mockResolvedValue({ items: [{ ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' }], hasMore: false, nextCursor: null });
    api.getTaskTimerCorrections.mockResolvedValueOnce({ items: [{ id: '9007199254740995', previousSeconds: null, newSeconds: 1200, reason: 'Перерыв', createdAtUtc: running.startedAtUtc }], nextBeforeId: '9007199254740995' })
      .mockResolvedValueOnce({ items: [{ id: '9007199254740994', previousSeconds: null, newSeconds: 900, reason: 'Уточнение', createdAtUtc: running.startedAtUtc }], nextBeforeId: null });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'История исправлений' }));
    await screen.findByText(/Перерыв/);
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние исправления' }));
    await screen.findByText(/Уточнение/);
    expect(api.getTaskTimerCorrections).toHaveBeenLastCalledWith(101, running.id, '9007199254740995');
  });

  it('retains correction input after a lost response and requires a fresh version before retry', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    const refreshed = { ...stopped, correctedSeconds: 900, revision: 1 };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [stopped], hasMore: false, nextCursor: null })
      .mockResolvedValueOnce({ items: [refreshed], hasMore: false, nextCursor: null });
    api.correctTaskTimer.mockRejectedValueOnce(new Error('response lost'))
      .mockResolvedValueOnce({ ...refreshed, correctedSeconds: 1200, revision: 2 });
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Исправить длительность' }));
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '20' } });
    fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Перерыв' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    await screen.findByRole('alert');
    expect((screen.getByLabelText('Длительность, целых минут') as HTMLInputElement).value).toBe('20');
    expect((screen.getByLabelText('Причина') as HTMLInputElement).value).toBe('Перерыв');
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    await screen.findByText(/Текущая длительность: 15 мин/);
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    await screen.findByText(/20 мин \(исправлено\)/);
    expect(api.correctTaskTimer.mock.calls).toEqual([[101, running.id, 0, 1200, 'Перерыв'], [101, running.id, 1, 1200, 'Перерыв']]);
  });

  it('does not repeat a correction when the refreshed result already matches the submitted duration', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    const alreadySaved = { ...stopped, correctedSeconds: 1200, revision: 1 };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [stopped], hasMore: false, nextCursor: null })
      .mockResolvedValueOnce({ items: [alreadySaved], hasMore: false, nextCursor: null });
    api.correctTaskTimer.mockRejectedValueOnce(new Error('response lost'));
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Исправить длительность' }));
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '20' } });
    fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Перерыв' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    expect(await screen.findByText(/Указанная длительность уже установлена/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(true);
    expect(api.correctTaskTimer).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '21' } });
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps the correction draft blocked when the refreshed history no longer contains the interval', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [stopped], hasMore: false, nextCursor: null })
      .mockResolvedValueOnce({ items: [], hasMore: false, nextCursor: null });
    api.correctTaskTimer.mockRejectedValueOnce(new Error('response lost'));
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Исправить длительность' }));
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '20' } });
    fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Перерыв' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    expect(await screen.findByText(/Запись не найдена в истории/)).toBeTruthy();
    expect((screen.getByLabelText('Причина') as HTMLInputElement).value).toBe('Перерыв');
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(true);
    expect(api.correctTaskTimer).toHaveBeenCalledTimes(1);
  });

  it('keeps a failed correction blocked until its interval is found on a later page', async () => {
    const stopped = { ...running, stoppedAtUtc: '2026-10-27T10:37:00Z' };
    const other = { ...stopped, id: 'c89554b9-91b2-44e8-a8db-666ecdddcfa2', startedAtUtc: '2026-10-28T10:00:00Z', stoppedAtUtc: '2026-10-28T10:10:00Z' };
    const cursor = { beforeStartedAtUtc: other.startedAtUtc, beforeId: other.id };
    const refreshed = { ...stopped, correctedSeconds: 900, revision: 1 };
    api.getTaskTimerHistory.mockResolvedValueOnce({ items: [stopped], hasMore: false, nextCursor: null })
      .mockResolvedValueOnce({ items: [other], hasMore: true, nextCursor: cursor })
      .mockResolvedValueOnce({ items: [refreshed], hasMore: false, nextCursor: null });
    api.correctTaskTimer.mockRejectedValueOnce(new Error('response lost'));
    render(<TaskTimerPanel taskId={101} userId={4} canStart />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Моя история времени' })).not.toHaveProperty('disabled', true));
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Исправить длительность' }));
    fireEvent.change(screen.getByLabelText('Длительность, целых минут'), { target: { value: '20' } });
    fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Перерыв' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить исправление' }));
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Моя история времени' }));
    await screen.findByText(/Запись не найдена в загруженной части истории/);
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByLabelText('Причина') as HTMLInputElement).value).toBe('Перерыв');
    fireEvent.click(screen.getByRole('button', { name: 'Показать более ранние записи' }));
    await screen.findByText('Текущая длительность: 15 мин');
    expect((screen.getByRole('button', { name: 'Сохранить исправление' }) as HTMLButtonElement).disabled).toBe(false);
    expect(api.getTaskTimerHistory).toHaveBeenLastCalledWith(101, cursor);
    expect(api.correctTaskTimer).toHaveBeenCalledTimes(1);
  });
});
