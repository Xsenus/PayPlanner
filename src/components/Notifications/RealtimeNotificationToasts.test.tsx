import { act, render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RealtimeNotificationToasts } from './RealtimeNotificationToasts';
import { REALTIME_USER_NOTIFICATION_CREATED_EVENT, type RealtimeUserNotification } from '../../services/realtimeNotifications';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../../utils/userNotifications';

const toastMock = vi.hoisted(() => ({
  showToast: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getUserNotifications: vi.fn(),
}));

const connectionState = vi.hoisted(() => ({
  handlers: new Map<string, (notification: RealtimeUserNotification) => void>(),
  canStart: true,
  create: vi.fn(),
  start: vi.fn(),
  stop: vi.fn(),
  off: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../services/realtimeNotifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../services/realtimeNotifications')>();
  return {
    ...actual,
    canStartNotificationsHubConnection: () => connectionState.canStart,
    createNotificationsHubConnection: () => {
      connectionState.create();
      return {
        state: 'Disconnected',
        start: connectionState.start,
        stop: connectionState.stop,
        on: (eventName: string, handler: (notification: RealtimeUserNotification) => void) => {
          connectionState.handlers.set(eventName, handler);
        },
        off: connectionState.off,
        onreconnected: connectionState.onreconnected,
        onclose: connectionState.onclose,
      };
    },
  };
});

async function flushRealtimeStart() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe('RealtimeNotificationToasts', () => {
  beforeEach(() => {
    toastMock.showToast.mockReset();
    apiMock.getUserNotifications.mockReset();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 15,
    });
    connectionState.handlers.clear();
    connectionState.canStart = true;
    connectionState.create.mockReset();
    connectionState.start.mockReset();
    connectionState.start.mockResolvedValue(undefined);
    connectionState.stop.mockReset();
    connectionState.stop.mockResolvedValue(undefined);
    connectionState.off.mockReset();
    connectionState.onreconnected.mockReset();
    connectionState.onclose.mockReset();
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
      dedupeKey: 'task:123',
      domain: 'task',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openTask).toHaveBeenCalledWith(123);
  });

  it('shows task change toast and opens the task from action', async () => {
    const openTask = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenTask={openTask} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 44,
        recipientUserId: 5,
        type: 'taskChanged',
        eventKey: 'task:125:changed',
        title: 'Изменена задача',
        bodyPreview: 'Срок задачи перенесен.',
        tone: 'blue',
        objectType: 'task',
        objectId: 125,
        taskJournalItemId: 125,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Изменена задача',
      variant: 'info',
      durationMs: 12000,
      actionLabel: 'Открыть задачу',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openTask).toHaveBeenCalledWith(125);
  });

  it('shows payment review toast and opens the payment from action', async () => {
    const openPayment = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenPayment={openPayment} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 45,
        recipientUserId: 5,
        type: 'paymentAdvanceReviewRequired',
        eventKey: 'payment:321:advance-review',
        title: 'Проверьте платеж',
        bodyPreview: 'По платежу требуется проверка аванса.',
        tone: 'rose',
        objectType: 'payment',
        objectId: 321,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Проверьте платеж',
      variant: 'error',
      durationMs: 16000,
      actionLabel: 'Открыть платеж',
      dedupeKey: 'payment:321',
      domain: 'payment',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openPayment).toHaveBeenCalledWith(321);
  });

  it('falls back to notification center when a supported toast has no linked handler', async () => {
    const openNotifications = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenNotifications={openNotifications} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 46,
        recipientUserId: 5,
        type: 'systemAdminWarning',
        eventKey: 'system:warning',
        title: 'Важное системное уведомление',
        bodyPreview: 'Проверьте настройки интеграции.',
        tone: 'rose',
        objectType: 'system',
        objectId: null,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Важное системное уведомление',
      variant: 'error',
      durationMs: 16000,
      actionLabel: 'Открыть уведомления',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openNotifications).toHaveBeenCalledTimes(1);
  });

  it('does not show toast for notification types that are not realtime-worthy', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 47,
        recipientUserId: 5,
        type: 'registrationApproved',
        eventKey: 'registration:approved',
        title: 'Регистрация подтверждена',
        bodyPreview: 'Аккаунт активирован.',
        tone: 'emerald',
        objectType: 'user',
        objectId: 5,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
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

  it('does not create realtime connection when auth token is missing', () => {
    connectionState.canStart = false;

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    expect(connectionState.create).not.toHaveBeenCalled();
    expect(connectionState.start).not.toHaveBeenCalled();
  });

  it('keeps expected offline startup failures out of console.error', async () => {
    const debugSpy = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await flushRealtimeStart();
    expect(errorSpy).not.toHaveBeenCalled();
    expect(debugSpy).toHaveBeenCalledWith(
      expect.stringContaining('Realtime notifications are unavailable'),
      expect.any(TypeError),
    );

    debugSpy.mockRestore();
    errorSpy.mockRestore();
  });

  it('keeps real realtime startup failures visible in console.error', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const startupError = new Error("Response status code does not indicate success: 401 (Unauthorized).");
    connectionState.start.mockRejectedValue(startupError);

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await flushRealtimeStart();
    expect(errorSpy).toHaveBeenCalledWith(
      expect.stringContaining('Realtime notifications failed to start'),
      startupError,
    );

    errorSpy.mockRestore();
  });

  it('polls unread notifications and shows fallback toasts when realtime startup is unavailable', async () => {
    const createdAt = new Date(Date.now() + 1000).toISOString();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [
        {
          id: 701,
          type: 'taskAssigned',
          typeLabel: 'Задача',
          eventKey: 'task:701:assigned',
          title: 'Новая задача через fallback',
          bodyPreview: 'Проверьте задачу.',
          tone: 'blue',
          objectType: 'task',
          objectId: 701,
          taskId: 701,
          isRead: false,
          readAt: null,
          isArchived: false,
          archivedAt: null,
          createdAt,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 15,
    });
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await flushRealtimeStart();
    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenCalledWith({
        status: 'unread',
        page: 1,
        pageSize: 15,
      }),
    );
    await waitFor(() =>
      expect(toastMock.showToast).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Новая задача через fallback',
          dedupeKey: 'task:701',
          domain: 'task',
        }),
      ),
    );

    unmount();
  });

  it('does not show accumulated old unread notifications as fallback toasts', async () => {
    const createdAt = new Date(Date.now() - 60_000).toISOString();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [
        {
          id: 702,
          type: 'taskAssigned',
          typeLabel: 'Задача',
          eventKey: 'task:702:assigned',
          title: 'Старая задача',
          bodyPreview: 'Это уведомление уже было в центре.',
          tone: 'blue',
          objectType: 'task',
          objectId: 702,
          taskId: 702,
          isRead: false,
          readAt: null,
          isArchived: false,
          archivedAt: null,
          createdAt,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 15,
    });
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await flushRealtimeStart();
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(1));
    expect(toastMock.showToast).not.toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Старая задача',
      }),
    );

    unmount();
  });

  it('emits user notification change details for realtime notifications', async () => {
    const changedEvents: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const handleChanged = (event: Event) => changedEvents.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 501,
        recipientUserId: 5,
        type: 'taskAssigned',
        eventKey: 'task:501:assigned',
        title: 'Новая задача',
        bodyPreview: 'Проверьте задачу.',
        tone: 'blue',
        objectType: 'task',
        objectId: 501,
        taskJournalItemId: 501,
        createdAt: '2026-06-17T10:00:00Z',
      });
    });

    expect(changedEvents.at(-1)?.detail).toMatchObject({
      source: 'realtime',
      reason: 'created',
      unreadDelta: 1,
      notificationId: 501,
    });

    window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
  });

  it('requests notification refresh after realtime reconnect', async () => {
    const changedEvents: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const handleChanged = (event: Event) => changedEvents.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.onreconnected).toHaveBeenCalledTimes(1));

    act(() => {
      const callback = connectionState.onreconnected.mock.calls[0][0] as () => void;
      callback();
    });

    expect(changedEvents.at(-1)?.detail).toMatchObject({
      source: 'realtime',
      reason: 'reconnected',
    });

    window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
  });
});
