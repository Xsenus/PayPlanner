import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StorageCasePublicationQueueSettings } from './StorageCasePublicationQueueSettings';
import type { StorageCasePublicationPageDto } from '../../services/api';

const mocks = vi.hoisted(() => ({ read: vi.fn(), retry: vi.fn(), confirm: vi.fn(), success: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: { getStorageCasePublicationQueue: mocks.read, retryStorageCasePublication: mocks.retry } }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: mocks.confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: mocks.success }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'UTC' }));
const row = { id: 'owned-operation', storageObjectId: 'owned-object', fileName: 'owned.pdf', status: 'deadLetter' as const,
  attemptCount: 3, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', canRetry: true };
const page: StorageCasePublicationPageDto = { items: [row], nextCursor: null };
const loaded = () => screen.findByText('owned.pdf');
beforeEach(() => {
  vi.resetAllMocks(); mocks.read.mockResolvedValue(page); mocks.retry.mockResolvedValue({ id: row.id, status: 'pending' });
  mocks.confirm.mockResolvedValue(false);
});
afterEach(cleanup);

describe('StorageCasePublicationQueueSettings', () => {
  it('distinguishes a retired attempt and blocks retry even with stale canRetry metadata', async () => {
    mocks.read.mockResolvedValue({ ...page, items: [{ ...row, isRetired: true }] });
    render(<StorageCasePublicationQueueSettings />); await loaded();
    expect(screen.getByText('Попытка прекращена')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Повторить операцию' }).hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    expect(mocks.confirm).not.toHaveBeenCalled(); expect(mocks.retry).not.toHaveBeenCalled();
  });
  it('reads one dead-letter page and never retries on opening', async () => {
    render(<StorageCasePublicationQueueSettings />);
    await loaded();
    expect(mocks.read).toHaveBeenCalledExactlyOnceWith({ status: 'deadLetter', after: null, signal: expect.any(AbortSignal) });
    expect(screen.getByText(/Попыток: 3/)).toBeTruthy();
    expect(mocks.retry).not.toHaveBeenCalled();
  });

  it('requires approval, sends one POST and reloads after acceptance', async () => {
    render(<StorageCasePublicationQueueSettings />); await loaded();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledTimes(1));
    expect(mocks.retry).not.toHaveBeenCalled();
    mocks.confirm.mockResolvedValue(true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    await waitFor(() => expect(mocks.retry).toHaveBeenCalledExactlyOnceWith(row.id, expect.any(AbortSignal)));
    await waitFor(() => expect(mocks.read).toHaveBeenCalledTimes(2));
    expect(mocks.success).toHaveBeenCalledExactlyOnceWith(expect.stringContaining('поставлена в очередь'), 'Хранилище');
  });

  it('blocks duplicate clicks while confirmation is pending', async () => {
    let finish!: (approved: boolean) => void;
    mocks.confirm.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<StorageCasePublicationQueueSettings />); await loaded();
    const button = screen.getByRole('button', { name: 'Повторить операцию' });
    fireEvent.click(button); fireEvent.click(button);
    expect(mocks.confirm).toHaveBeenCalledTimes(1);
    await act(async () => finish(true));
    expect(mocks.retry).toHaveBeenCalledTimes(1);
  });

  it.each(['HTTP 409 Conflict', 'network lost', 'HTTP 403 Forbidden'])('clears stale rows after %s without replaying the POST', async (message) => {
    mocks.confirm.mockResolvedValue(true); mocks.retry.mockRejectedValue(new Error(message));
    render(<StorageCasePublicationQueueSettings />); await loaded();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    await screen.findByRole('alert');
    expect(screen.queryByText('owned.pdf')).toBeNull();
    expect(mocks.retry).toHaveBeenCalledTimes(1);
    expect(mocks.success).not.toHaveBeenCalled();
    expect(mocks.read).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить очередь' })); await loaded();
    expect(mocks.read).toHaveBeenCalledTimes(2);
    expect(mocks.retry).toHaveBeenCalledTimes(1);
  });

  it('pages only on demand and resets the cursor when the filter changes', async () => {
    mocks.read.mockResolvedValue({ ...page, nextCursor: 'cursor/next' });
    render(<StorageCasePublicationQueueSettings />); await loaded();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await waitFor(() => expect(mocks.read).toHaveBeenLastCalledWith({ status: 'deadLetter', after: 'cursor/next', signal: expect.any(AbortSignal) }));
    fireEvent.click(screen.getByRole('combobox', { name: /^Состояние операции\./ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Ошибка' }));
    await waitFor(() => expect(mocks.read).toHaveBeenLastCalledWith({ status: 'failed', after: null, signal: expect.any(AbortSignal) }));
    expect(screen.getByText('Страница 1')).toBeTruthy();
  });

  it('aborts an old read and ignores its names after unmounting', async () => {
    let finish!: (value: StorageCasePublicationPageDto) => void;
    mocks.read.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<StorageCasePublicationQueueSettings />);
    const signal = mocks.read.mock.calls[0][0].signal as AbortSignal;
    view.unmount(); await act(async () => finish(page));
    expect(signal.aborted).toBe(true);
    expect(screen.queryByText('owned.pdf')).toBeNull();
  });

  it('ignores a late page from the previous filter', async () => {
    let finish!: (value: StorageCasePublicationPageDto) => void;
    mocks.read.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    mocks.read.mockResolvedValue({ items: [{ ...row, fileName: 'current-filter.pdf', status: 'failed' }], nextCursor: null });
    render(<StorageCasePublicationQueueSettings />);
    const oldSignal = mocks.read.mock.calls[0][0].signal as AbortSignal;
    fireEvent.click(screen.getByRole('combobox', { name: /^Состояние операции\./ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Ошибка' }));
    await screen.findByText('current-filter.pdf');
    await act(async () => finish(page));
    expect(oldSignal.aborted).toBe(true);
    expect(screen.queryByText('owned.pdf')).toBeNull();
    expect(screen.getByText('current-filter.pdf')).toBeTruthy();
    expect(mocks.retry).not.toHaveBeenCalled();
  });

  it('aborts an in-flight retry when settings close and ignores its acceptance', async () => {
    let finish!: (value: { id: string; status: string }) => void;
    mocks.confirm.mockResolvedValue(true);
    mocks.retry.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<StorageCasePublicationQueueSettings />); await loaded();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    await waitFor(() => expect(mocks.retry).toHaveBeenCalledTimes(1));
    const signal = mocks.retry.mock.calls[0][1] as AbortSignal;
    view.unmount(); await act(async () => finish({ id: row.id, status: 'pending' }));
    expect(signal.aborted).toBe(true);
    expect(mocks.success).not.toHaveBeenCalled();
    expect(mocks.read).toHaveBeenCalledTimes(1);
  });

  it('does not send a POST if confirmation completes after closing settings', async () => {
    let finish!: (value: boolean) => void;
    mocks.confirm.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<StorageCasePublicationQueueSettings />); await loaded();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить операцию' }));
    view.unmount(); await act(async () => finish(true));
    expect(mocks.retry).not.toHaveBeenCalled(); expect(mocks.success).not.toHaveBeenCalled();
  });

  it('disables running and completed operations according to the server lease guard', async () => {
    mocks.read.mockResolvedValue({ items: [{ ...row, status: 'running', canRetry: false },
      { ...row, id: 'complete', status: 'succeeded', canRetry: false }], nextCursor: null });
    render(<StorageCasePublicationQueueSettings />); await screen.findByText('Выполняется');
    expect(screen.getAllByRole('button', { name: 'Повторить операцию' }).every((button) => button.hasAttribute('disabled'))).toBe(true);
    expect(mocks.retry).not.toHaveBeenCalled();
  });

  it('shows access failure without retaining names', async () => {
    mocks.read.mockRejectedValue(new Error('HTTP 403 Forbidden'));
    render(<StorageCasePublicationQueueSettings />);
    expect((await screen.findByRole('alert')).textContent).toContain('Нет доступа');
    expect(screen.queryByText('owned.pdf')).toBeNull();
  });
});
