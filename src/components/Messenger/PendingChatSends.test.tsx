import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PendingChatSends } from './PendingChatSends';

const api = vi.hoisted(() => ({ getPendingSends: vi.fn(), getSendStatus: vi.fn(), resumeSend: vi.fn() }));
vi.mock('../../services/internalChatApi', () => ({ internalChatApi: api }));
const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
const second = '6edb5c44-84dc-41aa-bde8-e14bdc1c7238';
const item = { attemptId: 'saved-attempt', clientRequestId: key, createdAt: '2026-10-03T10:00:00Z', fileCount: 2, totalSizeBytes: 6 };
const waiting = { clientRequestId: key, status: 'waiting' as const, automaticRecovery: true, description: 'Подготавливаются копии файлов.' };
const message = { id: 23, threadId: 12, authorUserId: 7, text: 'Первый текст' };

describe('PendingChatSends', () => {
  beforeEach(() => {
    Object.values(api).forEach((mock) => mock.mockReset());
    api.getPendingSends.mockResolvedValue({ status: 'pending', items: [item] });
    api.getSendStatus.mockResolvedValue(waiting);
    api.resumeSend.mockResolvedValue({ ...waiting, status: 'published', message });
  });
  afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

  it('discovers bounded metadata and checks only the selected send', async () => {
    api.getPendingSends.mockResolvedValueOnce({ status: 'pending', items: [item], nextAfterAttemptId: item.attemptId })
      .mockResolvedValueOnce({ status: 'pending', items: [{ ...item, attemptId: 'older-attempt', clientRequestId: second }] });
    render(<PendingChatSends threadId={12} actorId={7} initialKey={null} incoming={null} onPublished={vi.fn()} />);
    expect(await screen.findByText('Незавершённые отправки: 1')).toBeTruthy();
    expect(api.getPendingSends).toHaveBeenCalledWith(12, expect.objectContaining({ limit: 20, signal: expect.any(AbortSignal) }));
    expect(api.getSendStatus).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Показать ещё'));
    await screen.findByText('Незавершённые отправки: 2');
    expect(api.getPendingSends.mock.calls[1][1].afterAttemptId).toBe(item.attemptId);
    expect(api.getSendStatus).not.toHaveBeenCalled();
    fireEvent.click(screen.getAllByText('Проверить отправку')[0]);
    expect((await screen.findByRole('status')).textContent).toContain(waiting.description);
    expect(api.getSendStatus).toHaveBeenCalledTimes(1);
    expect(api.resumeSend).not.toHaveBeenCalled();
  });

  it('resumes the saved send without files and acknowledges publication once', async () => {
    const onPublished = vi.fn();
    render(<PendingChatSends threadId={12} actorId={7} initialKey={key} incoming={null} onPublished={onPublished} />);
    await screen.findByRole('status');
    fireEvent.click(screen.getByText('Продолжить отправку'));
    await waitFor(() => expect(onPublished).toHaveBeenCalledWith(key, message));
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(api.resumeSend).toHaveBeenCalledWith(12, key, expect.any(AbortSignal));
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText('Незавершённые отправки: 1')).toBeNull();
  });

  it('resolves a lost published response after reopening, even when discovery has no pending rows', async () => {
    api.getPendingSends.mockResolvedValue({ status: 'pending', items: [] });
    api.getSendStatus.mockResolvedValue({ ...waiting, status: 'published', message });
    const onPublished = vi.fn();
    render(<PendingChatSends threadId={12} actorId={7} initialKey={key} incoming={null} onPublished={onPublished} />);
    await waitFor(() => expect(onPublished).toHaveBeenCalledWith(key, message));
    expect(api.resumeSend).not.toHaveBeenCalled();
  });

  it('rejects mismatched identity or published scope without acknowledging a draft', async () => {
    const onPublished = vi.fn();
    render(<PendingChatSends threadId={12} actorId={7} initialKey={null} incoming={waiting} onPublished={onPublished} />);
    await screen.findByRole('status');
    api.getSendStatus.mockResolvedValueOnce({ ...waiting, clientRequestId: second, status: 'published', message });
    fireEvent.click(screen.getByText('Обновить состояние'));
    expect((await screen.findByRole('alert')).textContent).toContain('идентификатор');
    expect(onPublished).not.toHaveBeenCalled();
    api.resumeSend.mockResolvedValueOnce({ ...waiting, status: 'published', message: { ...message, authorUserId: 8 } });
    fireEvent.click(screen.getByText('Продолжить отправку'));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('сообщение'));
    expect(onPublished).not.toHaveBeenCalled();
  });

  it('keeps missing-input and terminal attempts visible without automatic writes', async () => {
    const view = render(<PendingChatSends threadId={12} actorId={7} initialKey={null}
      incoming={{ ...waiting, status: 'needsInput', automaticRecovery: false, description: 'Нужны исходные файлы.' }} onPublished={vi.fn()} />);
    expect(await screen.findByText('Выберите исходные файлы снова и отправьте прежний текст сообщения.')).toBeTruthy();
    expect(screen.queryByText('Продолжить отправку')).toBeNull();
    view.rerender(<PendingChatSends threadId={12} actorId={7} initialKey={null}
      incoming={{ ...waiting, status: 'deleted', automaticRecovery: false, description: 'Сообщение удалено.' }} onPublished={vi.fn()} />);
    expect(await screen.findByText('Сообщение удалено.')).toBeTruthy();
    expect(screen.queryByText('Продолжить отправку')).toBeNull();
    expect(api.resumeSend).not.toHaveBeenCalled();
  });

  it('polls only a visible selected automatic send, with no automatic continuation', async () => {
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    vi.useFakeTimers();
    await act(async () => { render(<PendingChatSends threadId={12} actorId={7} initialKey={null} incoming={waiting} onPublished={vi.fn()} />); });
    expect(screen.getByRole('status')).toBeTruthy();
    expect(api.getSendStatus).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getSendStatus).toHaveBeenCalledTimes(1);
    visibility.mockReturnValue('hidden');
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(api.getSendStatus).toHaveBeenCalledTimes(1);
    visibility.mockReturnValue('visible');
    await act(async () => { document.dispatchEvent(new Event('visibilitychange')); });
    expect(api.getSendStatus).toHaveBeenCalledTimes(2);
    expect(api.resumeSend).not.toHaveBeenCalled();
  });

  it('cancels discovery and status requests on unmount and ignores their late results', async () => {
    let finish!: (value: object) => void;
    api.getSendStatus.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const onPublished = vi.fn();
    const view = render(<PendingChatSends threadId={12} actorId={7} initialKey={key} incoming={null} onPublished={onPublished} />);
    await waitFor(() => expect(api.getSendStatus).toHaveBeenCalled());
    const signal = api.getSendStatus.mock.calls[0][2] as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
    await act(async () => { finish({ ...waiting, status: 'published', message }); });
    expect(onPublished).not.toHaveBeenCalled();
  });
});
