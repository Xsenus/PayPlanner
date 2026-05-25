import { act, render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RealtimeNotificationToasts } from './RealtimeNotificationToasts';
import { REALTIME_USER_NOTIFICATION_CREATED_EVENT, type RealtimeUserNotification } from '../../services/realtimeNotifications';

const toastMock = vi.hoisted(() => ({
  showToast: vi.fn(),
}));

const connectionState = vi.hoisted(() => ({
  handlers: new Map<string, (notification: RealtimeUserNotification) => void>(),
  start: vi.fn(),
  stop: vi.fn(),
  off: vi.fn(),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/realtimeNotifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../services/realtimeNotifications')>();
  return {
    ...actual,
    createNotificationsHubConnection: () => ({
      state: 'Disconnected',
      start: connectionState.start,
      stop: connectionState.stop,
      on: (eventName: string, handler: (notification: RealtimeUserNotification) => void) => {
        connectionState.handlers.set(eventName, handler);
      },
      off: connectionState.off,
    }),
  };
});

describe('RealtimeNotificationToasts', () => {
  beforeEach(() => {
    toastMock.showToast.mockReset();
    connectionState.handlers.clear();
    connectionState.start.mockReset();
    connectionState.start.mockResolvedValue(undefined);
    connectionState.stop.mockReset();
    connectionState.stop.mockResolvedValue(undefined);
    connectionState.off.mockReset();
  });

  it('shows task assignment toast and opens the task from action', async () => {
    const openTask = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenTask={openTask} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
    expect(handler).toBeTruthy();

    act(() => {
      handler?.({
        id: 42,
        recipientUserId: 5,
        type: 'taskAssigned',
        eventKey: 'task:123:assigned',
        title: 'Новая задача: Проверить документы',
        bodyPreview: 'Проверить документы по делу клиента.',
        tone: 'blue',
        objectType: 'task',
        objectId: 123,
        taskJournalItemId: 123,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Новая задача: Проверить документы',
      actionLabel: 'Открыть задачу',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openTask).toHaveBeenCalledWith(123);
  });

  it('does not show task assignment toast for another user', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 43,
        recipientUserId: 6,
        type: 'taskAssigned',
        eventKey: 'task:124:assigned',
        title: 'Новая задача',
        bodyPreview: 'Текст',
        tone: 'blue',
        objectType: 'task',
        objectId: 124,
        taskJournalItemId: 124,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
  });
});
