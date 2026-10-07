import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TaskUploadStatusPanel } from './TaskUploadStatusPanel';
import type { TaskFileUploadState } from '../../services/api';

const api = vi.hoisted(() => ({ getTaskFileUploadStates: vi.fn(), getTaskFileUploadPage: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: api }));
const waiting: TaskFileUploadState = { uploadKey: 'key-1', fileName: 'document.pdf', status: 'waiting', fileId: null, message: 'Ожидаем копии.' };
const flush = async () => { await act(async () => { await Promise.resolve(); }); };

describe('TaskUploadStatusPanel', () => {
  beforeEach(() => {
    vi.useFakeTimers(); api.getTaskFileUploadStates.mockReset(); api.getTaskFileUploadPage.mockReset();
    // Existing scenarios supply the same discovery data; polling still uses exact-key states.
    api.getTaskFileUploadPage.mockImplementation(async () => ({ items: await api.getTaskFileUploadStates(), nextCursor: null }));
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  it('restores pending metadata and confirms publication by exact key before refreshing files', async () => {
    api.getTaskFileUploadStates.mockResolvedValueOnce([waiting])
      .mockResolvedValueOnce([{ ...waiting, status: 'uploaded', fileId: 601, message: 'Файл добавлен.' }]).mockResolvedValue([]);
    const onPublished = vi.fn().mockResolvedValue(undefined);
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.getByText('document.pdf')).toBeTruthy();
    expect(onPublished).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getTaskFileUploadStates).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), { uploadKeys: ['key-1'] });
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Добавлен')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить загрузки' }));
    await flush();
    expect(onPublished).toHaveBeenCalledTimes(1);
  });

  it('does not treat missing metadata as successful publication', async () => {
    api.getTaskFileUploadStates.mockResolvedValueOnce([waiting]).mockResolvedValue([]);
    const onPublished = vi.fn();
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={onPublished} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(screen.getByText(/Не удалось подтвердить состояние/)).toBeTruthy();
    expect(onPublished).not.toHaveBeenCalled();
  });

  it('removes names and stops polling when permission is revoked', async () => {
    api.getTaskFileUploadStates.mockResolvedValueOnce([waiting]).mockRejectedValue(new Error('HTTP 403 Forbidden'));
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(screen.queryByText('document.pdf')).toBeNull();
    expect(screen.getByRole('alert').textContent).toContain('Нет доступа');
    const calls = api.getTaskFileUploadStates.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(api.getTaskFileUploadStates).toHaveBeenCalledTimes(calls);
  });

  it('aborts the request and ignores a late response after closing the card', async () => {
    let finish!: (states: TaskFileUploadState[]) => void;
    api.getTaskFileUploadStates.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const onPublished = vi.fn();
    const view = render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={onPublished} />);
    const signal = api.getTaskFileUploadPage.mock.calls[0][1] as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
    await act(async () => { finish([waiting]); });
    expect(onPublished).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not poll or display a block when there are no pending uploads', async () => {
    api.getTaskFileUploadStates.mockResolvedValue([]);
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    expect(screen.queryByRole('region', { name: 'Состояние ваших загрузок' })).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(api.getTaskFileUploadStates).toHaveBeenCalledTimes(1);
  });

  it('fetches older pending pages only on demand', async () => {
    api.getTaskFileUploadPage.mockResolvedValueOnce({ items: [], nextCursor: 'raw-window-cursor' })
      .mockResolvedValueOnce({ items: [waiting], nextCursor: null });
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), 'raw-window-cursor');
    expect(screen.getByText('document.pdf')).toBeTruthy();
  });

  it('resets cursor navigation on refresh, revision and task changes', async () => {
    api.getTaskFileUploadPage.mockResolvedValue({ items: [], nextCursor: 'older-cursor' });
    const view = render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), 'older-cursor');
    fireEvent.click(screen.getByRole('button', { name: 'Обновить загрузки' }));
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), undefined);
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    view.rerender(<TaskUploadStatusPanel taskId={101} revision={1} onPublished={vi.fn()} />);
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), undefined);
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    view.rerender(<TaskUploadStatusPanel taskId={202} revision={1} onPublished={vi.fn()} />);
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(202, expect.any(AbortSignal), undefined);
    expect(screen.getByText('Страница 1')).toBeTruthy();
  });

  it('ignores an old page response after returning to the first page', async () => {
    let finish!: (page: { items: TaskFileUploadState[]; nextCursor: string | null }) => void;
    api.getTaskFileUploadPage.mockResolvedValueOnce({ items: [], nextCursor: 'older-cursor' })
      .mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }))
      .mockResolvedValueOnce({ items: [], nextCursor: 'fresh-cursor' });
    const onPublished = vi.fn();
    render(<TaskUploadStatusPanel taskId={101} revision={0} onPublished={onPublished} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    const oldSignal = api.getTaskFileUploadPage.mock.calls[1][1] as AbortSignal;
    fireEvent.click(screen.getByRole('button', { name: 'Предыдущие загрузки' }));
    await flush();
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { finish({ items: [{ ...waiting, status: 'uploaded', fileId: 601 }], nextCursor: null }); });
    expect(screen.queryByText('document.pdf')).toBeNull();
    expect(onPublished).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Более ранние загрузки' }));
    await flush();
    expect(api.getTaskFileUploadPage).toHaveBeenLastCalledWith(101, expect.any(AbortSignal), 'fresh-cursor');
  });
});
