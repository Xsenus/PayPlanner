import { act, render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RealtimeNotificationToasts } from './RealtimeNotificationToasts';
import { REALTIME_USER_NOTIFICATION_CREATED_EVENT, type RealtimeUserNotification } from '../../services/realtimeNotifications';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../../utils/userNotifications';
import {
  getRealtimeNotificationDiagnostics,
  resetRealtimeNotificationDiagnostics,
} from '../../utils/realtimeNotificationDiagnostics';

const toastMock = vi.hoisted(() => ({
  showToast: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getUserNotifications: vi.fn(),
  markUserNotificationRead: vi.fn(),
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
    apiMock.markUserNotificationRead.mockReset();
    apiMock.markUserNotificationRead.mockResolvedValue({});
    window.localStorage.clear();
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
    resetRealtimeNotificationDiagnostics();
  });

  it('shows task assignment toast and opens the task from action', async () => {
    const openTask = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenTask={openTask} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    expect(getRealtimeNotificationDiagnostics()).toMatchObject({
      status: 'connected',
      fallbackPollingActive: false,
    });

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

  it('updates realtime state without toast when toast channel is disabled', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 45,
        recipientUserId: 5,
        type: 'taskAssigned',
        eventKey: 'task:126:assigned',
        title: 'Новая задача без всплывашки',
        bodyPreview: 'Центр должен обновиться, toast нет.',
        tone: 'blue',
        toastEnabled: false,
        objectType: 'task',
        objectId: 126,
        taskJournalItemId: 126,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 45,
      unreadDelta: 1,
    }));
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('marks a realtime toast as read from quick action', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 77,
        recipientUserId: 5,
        type: 'taskAssigned',
        eventKey: 'task:777:assigned',
        title: 'Новая задача',
        bodyPreview: 'Проверьте задачу.',
        tone: 'blue',
        objectType: 'task',
        objectId: 777,
        taskJournalItemId: 777,
        createdAt: '2026-06-18T10:00:00Z',
      });
    });

    const toastOptions = toastMock.showToast.mock.calls[0][0] as {
      actions: Array<{ label: string; onClick: () => Promise<void> | void }>;
    };
    const readAction = toastOptions.actions.find((item) => item.label === 'Прочитано');

    await act(async () => {
      await readAction?.onClick();
    });

    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(77);
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 77,
      reason: 'toast-read',
      unreadDelta: -1,
    }));
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('snoozes repeated realtime toasts for the same object', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);

    act(() => {
      handler?.({
        id: 78,
        recipientUserId: 5,
        type: 'taskAssigned',
        eventKey: 'task:778:assigned',
        title: 'Первое уведомление',
        bodyPreview: 'Проверьте задачу.',
        tone: 'blue',
        objectType: 'task',
        objectId: 778,
        taskJournalItemId: 778,
        createdAt: '2026-06-18T10:00:00Z',
      });
    });

    const toastOptions = toastMock.showToast.mock.calls[0][0] as {
      actions: Array<{ label: string; onClick: () => void }>;
    };
    const snoozeAction = toastOptions.actions.find((item) => item.label === 'Отложить');
    act(() => {
      snoozeAction?.onClick();
    });

    act(() => {
      handler?.({
        id: 79,
        recipientUserId: 5,
        type: 'taskChanged',
        eventKey: 'task:778:changed',
        title: 'Повторное уведомление',
        bodyPreview: 'По этой же задаче.',
        tone: 'blue',
        objectType: 'task',
        objectId: 778,
        taskJournalItemId: 778,
        createdAt: '2026-06-18T10:01:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledTimes(1);
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

  it('shows calendar toast and opens the calendar from action', async () => {
    const openCalendar = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenCalendar={openCalendar} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 48,
        recipientUserId: 5,
        type: 'crmCalendarEventChanged',
        eventKey: 'calendar:task:125:changed',
        title: 'Задача изменилась в календаре',
        bodyPreview: 'Срок задачи перенесен.',
        tone: 'amber',
        objectType: 'calendar',
        objectId: 125,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Задача изменилась в календаре',
      actionLabel: 'Открыть календарь',
      dedupeKey: 'calendar:125',
      domain: 'calendar',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openCalendar).toHaveBeenCalledTimes(1);
  });

  it('shows note toast and opens the notes board from action', async () => {
    const openNotes = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenNotes={openNotes} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 49,
        recipientUserId: 5,
        type: 'stickyNoteUpdated',
        eventKey: 'note:board:3:updated',
        title: 'Обновлена заметка',
        bodyPreview: 'На общей доске изменили заметку.',
        tone: 'blue',
        objectType: 'note',
        objectId: 3,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Обновлена заметка',
      actionLabel: 'Открыть доску',
      dedupeKey: 'note:3',
      domain: 'note',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openNotes).toHaveBeenCalledTimes(1);
  });

  it('recognizes sticky note toast by type when object type is not normalized', async () => {
    const openNotes = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenNotes={openNotes} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 50,
        recipientUserId: 5,
        type: 'stickyNoteCreated',
        eventKey: 'note:board:4:created',
        title: 'Новая заметка',
        bodyPreview: 'На общей доске появилась заметка.',
        tone: 'blue',
        objectType: 'sticky-note',
        objectId: 4,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Новая заметка',
      actionLabel: 'Открыть доску',
      dedupeKey: 'sticky-note:4',
      domain: 'note',
    }));
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

  it('replays unread notifications that arrived while this device was away', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    window.localStorage.setItem(
      'payplanner.realtimeToastLastSeenAt.v1.5',
      String(Date.now() - 120_000),
    );
    const createdAt = new Date(Date.now() - 60_000).toISOString();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [
        {
          id: 710,
          type: 'taskAssigned',
          typeLabel: 'Task',
          eventKey: 'task:710:assigned',
          title: 'Missed task from closed browser',
          bodyPreview: 'This notification should be replayed after return.',
          tone: 'blue',
          objectType: 'task',
          objectId: 710,
          taskId: 710,
          isRead: false,
          readAt: null,
          isArchived: false,
          archivedAt: null,
          createdAt,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 50,
    });

    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenCalledWith({
        status: 'unread',
        page: 1,
        pageSize: 50,
      }),
    );
    await waitFor(() =>
      expect(toastMock.showToast).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Missed task from closed browser',
          dedupeKey: 'task:710',
          domain: 'task',
        }),
      ),
    );
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 710,
      reason: 'missed-replay',
    }));
    expect(changedEvents.find((item) => item.notificationId === 710)?.unreadDelta).toBeUndefined();
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('spaces accumulated replay notifications so they can be read one by one', async () => {
    vi.useFakeTimers();
    window.localStorage.setItem(
      'payplanner.realtimeToastLastSeenAt.v1.5',
      String(Date.now() - 120_000),
    );
    const firstCreatedAt = new Date(Date.now() - 90_000).toISOString();
    const secondCreatedAt = new Date(Date.now() - 60_000).toISOString();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [
        {
          id: 712,
          type: 'taskAssigned',
          typeLabel: 'Task',
          eventKey: 'task:712:assigned',
          title: 'Second missed task',
          bodyPreview: 'Second replay item.',
          tone: 'blue',
          objectType: 'task',
          objectId: 712,
          taskId: 712,
          isRead: false,
          readAt: null,
          isArchived: false,
          archivedAt: null,
          createdAt: secondCreatedAt,
        },
        {
          id: 711,
          type: 'taskAssigned',
          typeLabel: 'Task',
          eventKey: 'task:711:assigned',
          title: 'First missed task',
          bodyPreview: 'First replay item.',
          tone: 'blue',
          objectType: 'task',
          objectId: 711,
          taskId: 711,
          isRead: false,
          readAt: null,
          isArchived: false,
          archivedAt: null,
          createdAt: firstCreatedAt,
        },
      ],
      total: 2,
      page: 1,
      pageSize: 50,
    });

    try {
      render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

      await act(async () => {
        await Promise.resolve();
        await connectionState.start.mock.results[0]?.value;
        await Promise.resolve();
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(toastMock.showToast).toHaveBeenCalledTimes(1);
      expect(toastMock.showToast).toHaveBeenNthCalledWith(1, expect.objectContaining({
        title: 'First missed task',
      }));

      act(() => {
        vi.advanceTimersByTime(2499);
      });
      expect(toastMock.showToast).toHaveBeenCalledTimes(1);

      await act(async () => {
        vi.advanceTimersByTime(1);
        await Promise.resolve();
      });
      expect(toastMock.showToast).toHaveBeenCalledTimes(2);
      expect(toastMock.showToast).toHaveBeenNthCalledWith(2, expect.objectContaining({
        title: 'Second missed task',
      }));
    } finally {
      vi.useRealTimers();
    }
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
    expect(getRealtimeNotificationDiagnostics()).toMatchObject({
      status: 'fallback',
      fallbackPollingActive: true,
    });
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

  it('starts fallback polling after realtime connection closes and stops it after reconnect', async () => {
    const changedEvents: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const handleChanged = (event: Event) => changedEvents.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(connectionState.onclose).toHaveBeenCalledTimes(1));

    act(() => {
      const closeCallback = connectionState.onclose.mock.calls[0][0] as () => void;
      closeCallback();
    });

    expect(getRealtimeNotificationDiagnostics()).toMatchObject({
      status: 'fallback',
      fallbackPollingActive: true,
    });
    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenCalledWith({
        status: 'unread',
        page: 1,
        pageSize: 15,
      }),
    );
    expect(toastMock.showToast).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Моментальные уведомления недоступны',
        dedupeKey: 'notifications:fallback-polling',
        domain: 'system',
      }),
    );

    act(() => {
      const reconnectCallback = connectionState.onreconnected.mock.calls[0][0] as () => void;
      reconnectCallback();
    });

    expect(getRealtimeNotificationDiagnostics()).toMatchObject({
      status: 'connected',
      fallbackPollingActive: false,
    });
    expect(changedEvents.at(-1)?.detail).toMatchObject({
      source: 'realtime',
      reason: 'reconnected',
    });

    window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
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
