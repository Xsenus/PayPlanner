import { act, render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { ForcedTaskRevisionModal } from './ForcedTaskRevisionModal';
import { RealtimeNotificationToasts } from './RealtimeNotificationToasts';
import {
  REALTIME_TASK_JOURNAL_CHANGED_EVENT,
  REALTIME_USER_NOTIFICATION_CREATED_EVENT,
  TASK_JOURNAL_REALTIME_EVENT,
  type RealtimeUserNotification,
} from '../../services/realtimeNotifications';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../../utils/userNotifications';
import {
  getRealtimeNotificationDiagnostics,
  resetRealtimeNotificationDiagnostics,
} from '../../utils/realtimeNotificationDiagnostics';

const toastMock = vi.hoisted(() => ({
  showToast: vi.fn(),
  success: vi.fn(),
  error: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getUserNotifications: vi.fn(),
  getUserSetting: vi.fn(),
  markUserNotificationRead: vi.fn(),
  markCaseActivity: vi.fn(),
  snoozeUserNotification: vi.fn(),
}));

const connectionState = vi.hoisted(() => ({
  handlers: new Map<string, (notification: RealtimeUserNotification) => void>(),
  canStart: true,
  create: vi.fn(),
  start: vi.fn(),
  stop: vi.fn(),
  off: vi.fn(),
  onreconnected: vi.fn(),
  onreconnecting: vi.fn(),
  onclose: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 5 } }) }));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
  AUTH_SESSION_EXPIRED_EVENT: 'payplanner:auth-session-expired',
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
        onreconnecting: connectionState.onreconnecting,
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
    toastMock.success.mockReset();
    toastMock.error.mockReset();
    apiMock.getUserSetting.mockReset();
    apiMock.getUserSetting.mockResolvedValue(null);
    apiMock.getUserNotifications.mockReset();
    apiMock.getUserNotifications.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 15,
    });
    apiMock.markUserNotificationRead.mockReset();
    apiMock.markUserNotificationRead.mockResolvedValue({});
    apiMock.markCaseActivity.mockReset();
    apiMock.markCaseActivity.mockResolvedValue({});
    apiMock.snoozeUserNotification.mockReset();
    apiMock.snoozeUserNotification.mockResolvedValue({});
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
    connectionState.onreconnecting.mockReset();
    connectionState.onclose.mockReset();
    resetRealtimeNotificationDiagnostics();
  });

  it.each(['live', 'replay', 'fallback'] as const)('refreshes the central inbox and respects server toast suppression for ordinary completion through %s', async delivery => {
    const notification: RealtimeUserNotification = {
      id: 811, recipientUserId: 5, type: 'taskCompleted', eventKey: 'task:505:completed',
      title: 'Задача завершена в центральном окне', bodyPreview: 'Документы подготовлены.', tone: 'emerald',
      toastEnabled: false, objectType: 'task', objectId: 505, taskJournalItemId: 505,
      metadataJson: '{"status":"done","completionOrigin":"ordinary"}',
      createdAt: new Date(Date.now() + (delivery === 'fallback' ? 1000 : -60_000)).toISOString(),
    };
    const changes = vi.fn();
    window.addEventListener(USER_NOTIFICATIONS_EVENT, changes);
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    try {
      if (delivery !== 'live') {
        window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
        apiMock.getUserNotifications.mockResolvedValue({ items: [{ ...notification, taskId: 505, isRead: false }], total: 1, page: 1, pageSize: 50 });
      }
      if (delivery === 'fallback') connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));
      render(<RealtimeNotificationToasts userId={5} />);
      await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
      if (delivery === 'live') act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.(notification));
      await waitFor(() => expect(changes).toHaveBeenCalledWith(expect.objectContaining({ detail: expect.objectContaining({ notificationId: 811, notificationType: 'taskCompleted' }) })));
      expect(toastMock.showToast.mock.calls.some(([options]) => options.title === notification.title)).toBe(false);
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, changes);
      debug.mockRestore();
    }
  });

  it.each(['ordinary', 'approval', 'legacy'])('retains the completion toast when server delivery permits it for %s origin', async completionOrigin => {
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 812, recipientUserId: 5, type: 'taskCompleted', eventKey: 'task:505:completed',
      title: 'Завершение через нижнее сообщение', bodyPreview: 'Документы подготовлены.', tone: 'emerald',
      toastEnabled: true, objectType: 'task', objectId: 505, taskJournalItemId: 505,
      metadataJson: JSON.stringify({ status: 'done', completionOrigin }), createdAt: new Date().toISOString(),
    }));
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Завершение через нижнее сообщение', durationMs: 0, actionLabel: 'К задаче' }));
  });

  it('shows one ordinary completion window across inbox and realtime and routes its task after acknowledgement', async () => {
    const notification = {
      id: 813, type: 'taskCompleted', typeLabel: 'Задача завершена', eventKey: 'task:505:completed',
      title: 'Задача выполнена: Подготовить документы', bodyPreview: 'Документы подготовлены.', tone: 'emerald',
      toastEnabled: false, forcedModalEnabled: true, requiresAction: true,
      objectType: 'task', objectId: 505, taskId: 505, isRead: false,
      metadataJson: '{"status":"done","completionOrigin":"ordinary"}', createdAt: new Date().toISOString(),
    };
    let items = [notification];
    apiMock.getUserNotifications.mockImplementation(async params => ({
      items: params?.type === 'taskCompleted' ? items : [], total: params?.type === 'taskCompleted' ? items.length : 0, page: 1, pageSize: 25,
    }));
    apiMock.markUserNotificationRead.mockImplementation(async () => { items = []; return { ...notification, isRead: true }; });
    const onOpenTask = vi.fn();
    render(<><ForcedTaskRevisionModal onOpenTask={onOpenTask} /><RealtimeNotificationToasts userId={5} onOpenTask={onOpenTask} /></>);
    await screen.findByRole('button', { name: 'Понятно' });
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({ ...notification, recipientUserId: 5, taskJournalItemId: 505 }));
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    expect(toastMock.showToast).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'К задаче' }));
    await waitFor(() => expect(onOpenTask).toHaveBeenCalledWith(505));
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(813);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(toastMock.showToast).not.toHaveBeenCalled();
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
      dedupeKey: 'task:123:notification:42',
      domain: 'task',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openTask).toHaveBeenCalledWith(123);
  });

  it('waits for resolved permissions before replaying a notice with its task action', async () => {
    const openTask = vi.fn();
    window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
    apiMock.getUserNotifications.mockResolvedValue({
      items: [{ id: 712, type: 'taskChanged', title: 'Файл прикреплён к задаче',
        eventKey: 'task-file:712:result', objectType: 'task', objectId: 712, taskId: 712,
        isRead: false, createdAt: new Date(Date.now() - 60_000).toISOString(),
      }], total: 1, page: 1, pageSize: 50,
    });
    const { rerender } = render(<RealtimeNotificationToasts userId={5} ready={false} />);
    await flushRealtimeStart();
    expect(connectionState.start).not.toHaveBeenCalled();
    expect(apiMock.getUserNotifications).not.toHaveBeenCalled();
    rerender(<RealtimeNotificationToasts userId={5} ready onOpenTask={openTask} />);
    await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Файл прикреплён к задаче', actionLabel: 'Открыть задачу' }),
    ));
    const options = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => options.onAction());
    expect(openTask).toHaveBeenCalledWith(712);
  });

  it('shows a background upload result with the task card closed and ignores repeat delivery', async () => {
    const openTask = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenTask={openTask} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const result = {
      id: 143,
      recipientUserId: 5,
      type: 'taskChanged',
      eventKey: 'task-file-publication-result:00000000000000000000000000000143',
      title: 'Файл прикреплён к задаче',
      bodyPreview: 'Загрузка завершена. Файл доступен в карточке задачи.',
      tone: 'emerald',
      objectType: 'task',
      objectId: 223,
      taskJournalItemId: 223,
      metadataJson: '{"uploadKey":"00000000-0000-0000-0000-000000000143","result":"uploaded"}',
      createdAt: '2026-09-29T10:00:00Z',
    };
    act(() => {
      const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
      handler?.(result);
      handler?.(result);
    });
    expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Файл прикреплён к задаче', actionLabel: 'Открыть задачу', domain: 'task',
    }));
    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());
    expect(openTask).toHaveBeenCalledWith(223);
  });

  it('keeps task completion visible until the author acknowledges it or opens the task', async () => {
    const openTask = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenTask={openTask} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 142,
        recipientUserId: 5,
        type: 'taskCompleted',
        eventKey: 'task:223:completed',
        title: 'Задача выполнена',
        bodyPreview: 'Исполнитель отметил задачу выполненной.',
        tone: 'blue',
        objectType: 'task',
        objectId: 223,
        taskJournalItemId: 223,
        createdAt: '2026-09-21T10:00:00Z',
      });
    });

    const toastOptions = toastMock.showToast.mock.calls[0][0] as {
      durationMs: number;
      actionLabel: string;
      actions: Array<{ label: string; onClick: () => void | Promise<void>; variant?: string }>;
    };
    expect(toastOptions.durationMs).toBe(0);
    expect(toastOptions.actionLabel).toBe('К задаче');
    expect(toastOptions.actions.map((item) => item.label)).toEqual(['Всё понятно', 'К задаче']);

    await act(async () => {
      await toastOptions.actions[0].onClick();
    });
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(142);

    act(() => {
      toastOptions.actions[1].onClick();
    });
    expect(openTask).toHaveBeenCalledWith(223);
  });

  it('opens case document toast actions on the documents tab', async () => {
    const openCase = vi.fn();
    const openCaseDocument = vi.fn();
    render(
      <RealtimeNotificationToasts
        userId={5}
        onOpenCase={openCase}
        onOpenCaseDocument={openCaseDocument}
      />,
    );

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);

    act(() => {
      handler?.({
        id: 43,
        recipientUserId: 5,
        type: 'caseDocument',
        eventKey: 'case:77:document:901',
        title: 'Новый документ дела',
        bodyPreview: 'Загружено заявление.',
        tone: 'emerald',
        objectType: 'case',
        objectId: 77,
        taskJournalItemId: null,
        metadataJson: '{"clientCaseDocumentId":901,"action":"uploaded"}',
        createdAt: '2026-05-25T10:05:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Новый документ дела',
      actionLabel: 'Открыть документы',
      domain: 'case',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openCaseDocument).toHaveBeenCalledWith(77, 901);
    expect(openCase).not.toHaveBeenCalled();
  });

  it('shows a recorded court decision without claiming publication', async () => {
    const openCase = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenCase={openCase} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 44,
        recipientUserId: 5,
        type: 'courtDecisionRecorded',
        eventKey: 'case:77:decision:901',
        title: 'По делу зафиксировано решение суда',
        bodyPreview: 'Текст судебного акта пока не опубликован.',
        tone: 'rose',
        objectType: 'case',
        objectId: 77,
        taskJournalItemId: null,
        metadataJson: null,
        createdAt: '2026-09-25T10:05:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'По делу зафиксировано решение суда',
      actionLabel: 'Открыть дело',
      domain: 'case',
    }));
    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());
    expect(openCase).toHaveBeenCalledWith(77);
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

  it('does not show a regular toast for completed approval decisions', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 46,
        recipientUserId: 5,
        type: 'taskApproved',
        eventKey: 'task:127:approval-complete',
        title: 'Задача согласована и завершена',
        bodyPreview: 'Задача согласована и завершена.',
        tone: 'emerald',
        objectType: 'task',
        objectId: 127,
        taskJournalItemId: 127,
        metadataJson: '{"decision":"approved","resultingStatus":"done","completed":true}',
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 46,
      unreadDelta: 1,
    }));
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('does not show a regular toast for revision requests handled by forced modals', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 49,
        recipientUserId: 5,
        type: 'taskRevisionRequested',
        eventKey: 'task:130:approval-decision:701:revisionRequested:20260630120000000',
        title: 'Задача возвращена на доработку',
        bodyPreview: 'Нужно исправить расчет и приложить документ.',
        tone: 'orange',
        objectType: 'task',
        objectId: 130,
        taskJournalItemId: 130,
        metadataJson: '{"decision":"revisionRequested","resolution":"Нужно исправить расчет и приложить документ."}',
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 49,
      unreadDelta: 1,
    }));
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('does not show a regular toast for deadline change requests handled by forced modals', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 47,
        recipientUserId: 5,
        type: 'taskDeadlineChangeRequested',
        eventKey: 'task:128:deadline-change-request:901:requested',
        title: 'Запрос переноса срока',
        bodyPreview: 'Исполнитель запросил перенос срока задачи.',
        tone: 'violet',
        objectType: 'task',
        objectId: 128,
        taskJournalItemId: 128,
        metadataJson: '{"deadlineChangeRequestId":901,"requestedDueAt":"2026-06-30T09:00:00Z"}',
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 47,
      unreadDelta: 1,
    }));
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('does not show a regular toast for approval requests handled by forced modals', async () => {
    const changedEvents: UserNotificationsChangedDetail[] = [];
    const listener = (event: Event) => {
      changedEvents.push((event as CustomEvent<UserNotificationsChangedDetail>).detail);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 48,
        recipientUserId: 5,
        type: 'taskApprovalRequested',
        eventKey: 'task:129:approval-request:701:20260630120000000',
        title: 'Задача ожидает согласования',
        bodyPreview: 'Исполнитель отправил результат на согласование.',
        tone: 'violet',
        objectType: 'task',
        objectId: 129,
        taskJournalItemId: 129,
        metadataJson: '{"currentApproverId":701}',
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).not.toHaveBeenCalled();
    expect(changedEvents).toContainEqual(expect.objectContaining({
      notificationId: 48,
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

  it('keeps separate task changes from replacing each other or a completion toast', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
    const first = {
      recipientUserId: 5,
      tone: 'blue',
      objectType: 'task',
      objectId: 778,
      taskJournalItemId: 778,
      createdAt: '2026-09-27T10:00:00Z',
    };

    act(() => {
      handler?.({ ...first, id: 80, type: 'taskCompleted', eventKey: 'task:778:completed', title: 'Задача выполнена', bodyPreview: 'Готово.' });
      handler?.({ ...first, id: 81, type: 'taskChanged', eventKey: 'task:778:changed:1', title: 'Сменился постановщик', bodyPreview: 'Новый постановщик.' });
      handler?.({ ...first, id: 82, type: 'taskChanged', eventKey: 'task:778:due-at:2', title: 'Изменён срок задачи', bodyPreview: 'Новый срок.' });
    });

    expect(toastMock.showToast.mock.calls.map(([options]) => options.dedupeKey)).toEqual([
      'task:778:notification:80',
      'task:778:notification:81',
      'task:778:notification:82',
    ]);
    expect(toastMock.showToast.mock.calls[0][0].durationMs).toBe(0);
  });

  it.each([
    { body: 'Задача: Сохранённое название\nАвтор изменения: Анна\n\nИзменения:\n- Срок: вчера -> завтра' },
    { body: 'Изменения:\r\n- Срок: вчера -> завтра\r\n\r\nЗадача: Сохранённое название\r\nАвтор изменения: Анна' },
  ])('keeps a task identity from the persisted body header for $body', async ({ body }) => {
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 820, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:778:changed:legacy',
      title: 'Задача изменена', bodyPreview: body, tone: 'blue', objectType: 'task', objectId: 778,
      taskJournalItemId: 778, createdAt: new Date().toISOString(),
    }));
    const description = toastMock.showToast.mock.calls[0][0].description as string;
    expect(description).toBe('Задача №778 — Сохранённое название\nСрок: вчера -> завтра');
  });

  it('keeps metadata task identity ahead of a truncated long result and preserves priority changes', async () => {
    const taskTitle = 'Очень длинное название задачи '.repeat(20).slice(0, 500);
    const body = ['Изменения:', '- Срок: 10.11 -> 12.11', '- Постановщик: Анна -> Андрей', `- Результат: ${'готово '.repeat(250)}`].join('\n').slice(0, 1000);
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 821, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:778:changed:long',
      title: 'Задача изменена', bodyPreview: body, metadataJson: JSON.stringify({ taskTitle }),
      tone: 'blue', objectType: 'task', objectId: 778, taskJournalItemId: 778, createdAt: new Date().toISOString(),
    }));
    const description = toastMock.showToast.mock.calls[0][0].description as string;
    const [identity, ...changes] = description.split('\n');
    expect(identity).toBe(`Задача №778 — ${Array.from(taskTitle).slice(0, 157).join('')}...`);
    expect(changes.join('\n').length).toBeLessThanOrEqual(220);
    expect(changes[0]).toBe('Срок: 10.11 -> 12.11');
    expect(changes[1]).toBe('Постановщик: Анна -> Андрей');
    expect(body).not.toContain('Задача:');
  });

  it.each([null, 17, {}, '   '])('falls back to the body when taskTitle metadata is %j', async taskTitle => {
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 822, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:778:changed:invalid-meta',
      title: 'Задача изменена', bodyPreview: 'Изменения:\n- Исполнитель: Анна -> Мария\n\nЗадача: Имя из истории',
      metadataJson: JSON.stringify({ taskTitle }), tone: 'blue', objectType: 'task', objectId: 778,
      taskJournalItemId: 778, createdAt: new Date().toISOString(),
    }));
    expect(toastMock.showToast.mock.calls[0][0].description).toBe('Задача №778 — Имя из истории\nИсполнитель: Анна -> Мария');
  });

  it('uses the object task ID for a custom template without a task header and preserves its subject', async () => {
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 823, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:915:changed:custom',
      title: 'Тема пользовательского шаблона', bodyPreview: 'Исполнитель обновил документы.', metadataJson: '{',
      tone: 'blue', objectType: 'task', objectId: 915, taskJournalItemId: null, createdAt: new Date().toISOString(),
    }));
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Тема пользовательского шаблона', description: 'Задача №915\nИсполнитель обновил документы.',
    }));
  });

  it('prefers trimmed authoritative task metadata over a stale body header', async () => {
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledOnce());
    act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
      id: 824, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:778:changed:metadata',
      title: 'Задача изменена', bodyPreview: 'Задача: Старый шаблон\nКомментарий сохранён.', metadataJson: '{"taskTitle":"  Актуальное название  "}',
      tone: 'blue', objectType: 'task', objectId: 778, taskJournalItemId: 778, createdAt: new Date().toISOString(),
    }));
    expect(toastMock.showToast.mock.calls[0][0].description).toBe('Задача №778 — Актуальное название\nКомментарий сохранён.');
  });

  it('uses task identity metadata when a stored task change is replayed', async () => {
    window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
    apiMock.getUserNotifications.mockResolvedValue({ items: [{
      id: 825, type: 'taskChanged', eventKey: 'task:778:changed:replay', title: 'Задача изменена',
      bodyPreview: 'Пользовательский текст.', metadataJson: '{"taskTitle":"Название из сохранённого события"}',
      objectType: 'task', objectId: 778, taskId: 778, isRead: false, createdAt: new Date(Date.now() - 60_000).toISOString(),
    }], total: 1, page: 1, pageSize: 50 });
    render(<RealtimeNotificationToasts userId={5} />);
    await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      description: 'Задача №778 — Название из сохранённого события\nПользовательский текст.',
    })));
  });

  it('shows changed deadline and author before a long task description in the toast', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
    act(() => {
      handler?.({
        id: 83,
        recipientUserId: 5,
        type: 'taskChanged',
        eventKey: 'task:778:due-at:3',
        title: 'Изменён срок задачи',
        bodyPreview: [
          `Задача: ${'Подготовить документы по делу '.repeat(10)}`,
          'Автор изменения: Андрей',
          `Клиент/дело: ${'Длинное наименование '.repeat(10)}`,
          'Текущий срок: 22.09.2026 10:00',
          '',
          'Изменения:',
          '- Описание: старое -> новое',
          '- Срок: 21.09.2026 10:00 -> 22.09.2026 10:00',
          '- Постановщик: Анна -> Андрей',
        ].join('\n'),
        tone: 'amber',
        objectType: 'task',
        objectId: 778,
        taskJournalItemId: 778,
        createdAt: '2026-09-27T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Изменён срок задачи',
      description: expect.stringContaining('Срок: 21.09.2026 10:00 -> 22.09.2026 10:00'),
    }));
    const description = toastMock.showToast.mock.calls[0][0].description as string;
    expect(description).toContain('Постановщик: Анна -> Андрей');
    expect(description).toMatch(/^Задача №778 — Подготовить документы по делу/);
    expect(description.indexOf('Срок:')).toBeLessThan(description.indexOf('Описание:'));
    expect(description).not.toContain('Длинное наименование');
  });

  it('offers persistent snooze and activity confirmation for case inactivity', async () => {
    render(<RealtimeNotificationToasts userId={5} onOpenCase={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
    const handler = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
    act(() => {
      handler?.({
        id: 80,
        recipientUserId: 5,
        type: 'caseInactivityControl',
        eventKey: 'case:56:inactivity-control:88',
        title: 'Автоконтроль дела',
        bodyPreview: 'Проверьте движение.',
        tone: 'amber',
        objectType: 'case',
        objectId: 56,
        taskJournalItemId: 88,
        metadataJson: '{"clientCaseId":56}',
        createdAt: '2026-06-18T10:00:00Z',
      });
    });

    const toastOptions = toastMock.showToast.mock.calls[0][0] as {
      actions: Array<{ label: string; onClick: () => void | Promise<void> }>;
    };
    const confirmAction = toastOptions.actions.find((item) => item.label === 'Подтвердить активность');
    await act(async () => {
      await confirmAction?.onClick();
    });
    expect(apiMock.markCaseActivity).toHaveBeenCalledWith(56, { confirmed: true });
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(80);

    const snoozeAction = toastOptions.actions.find((item) => item.label === 'Отложить');
    await act(async () => {
      await snoozeAction?.onClick();
    });
    expect(apiMock.snoozeUserNotification).toHaveBeenCalledWith(80, 30);
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

  it('opens the payment from toast using metadata payment type', async () => {
    const openPayment = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenPayment={openPayment} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 46,
        recipientUserId: 5,
        type: 'paymentAdvanceReviewRequired',
        eventKey: 'payment:323:advance-review',
        title: 'Проверьте платеж',
        bodyPreview: 'По платежу требуется проверка аванса.',
        tone: 'rose',
        objectType: 'payment',
        objectId: 323,
        taskJournalItemId: null,
        metadataJson: JSON.stringify({ paymentType: 'Income' }),
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Проверьте платеж',
      variant: 'error',
      durationMs: 16000,
      actionLabel: 'Открыть платеж',
      dedupeKey: 'payment:323',
      domain: 'payment',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openPayment).toHaveBeenCalledWith(323, 'Income');
  });

  it('opens notification center for payment toast when the payment action is unavailable', async () => {
    const openNotifications = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenNotifications={openNotifications} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 52,
        recipientUserId: 5,
        type: 'paymentAdvanceReviewRequired',
        eventKey: 'payment:322:advance-review',
        title: 'Проверьте платеж',
        bodyPreview: 'По платежу требуется проверка аванса.',
        tone: 'rose',
        objectType: 'payment',
        objectId: 322,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Проверьте платеж',
      actionLabel: 'Открыть уведомления',
      dedupeKey: 'payment:322',
      domain: 'payment',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openNotifications).toHaveBeenCalledTimes(1);
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

  it('opens notification center for note toast when the notes board action is unavailable', async () => {
    const openNotifications = vi.fn();
    render(<RealtimeNotificationToasts userId={5} onOpenNotifications={openNotifications} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 51,
        recipientUserId: 5,
        type: 'stickyNoteUpdated',
        eventKey: 'note:board:5:updated',
        title: 'Обновлена заметка',
        bodyPreview: 'На общей доске изменили заметку.',
        tone: 'blue',
        objectType: 'note',
        objectId: 5,
        taskJournalItemId: null,
        createdAt: '2026-05-25T10:00:00Z',
      });
    });

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Обновлена заметка',
      actionLabel: 'Открыть уведомления',
      dedupeKey: 'note:5',
      domain: 'note',
    }));

    const toastOptions = toastMock.showToast.mock.calls[0][0] as { onAction: () => void };
    act(() => toastOptions.onAction());

    expect(openNotifications).toHaveBeenCalledTimes(1);
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

  it('delivers notifications through polling when the realtime endpoint returns a server error', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    connectionState.start.mockRejectedValue(new Error('Response status code: 502 (Bad Gateway).'));
    apiMock.getUserNotifications.mockResolvedValue({
      items: [{
        id: 902,
        type: 'taskAssigned',
        eventKey: 'task:902:assigned',
        title: 'Задача при недоступном канале',
        bodyPreview: 'Подготовить документы',
        tone: 'blue',
        objectType: 'task',
        objectId: 902,
        taskId: 902,
        createdAt: new Date().toISOString(),
      }],
    });

    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();

    expect(apiMock.getUserNotifications).toHaveBeenCalled();
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Задача при недоступном канале' }));
    expect(getRealtimeNotificationDiagnostics()).toMatchObject({ fallbackPollingActive: true });
    unmount();
    errorSpy.mockRestore();
  });

  it('checks notifications during automatic reconnect instead of waiting until reconnect is exhausted', async () => {
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();
    apiMock.getUserNotifications.mockClear();

    expect(connectionState.onreconnecting).toHaveBeenCalledTimes(1);
    await act(async () => {
      connectionState.onreconnecting.mock.calls[0][0](new Error('Connection lost'));
    });

    expect(apiMock.getUserNotifications).toHaveBeenCalledWith({ status: 'unread', page: 1, pageSize: 15 });
    expect(getRealtimeNotificationDiagnostics()).toMatchObject({ fallbackPollingActive: true });
    unmount();
  });

  it('keeps hidden-tab notifications for display when the employee returns', async () => {
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();
    visibility.mockReturnValue('hidden');
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 903,
        recipientUserId: 5,
        type: 'taskChanged',
        eventKey: 'task:903:changed',
        title: 'Изменён срок задачи',
        bodyPreview: 'Завтра в 12:00',
        tone: 'amber',
        objectType: 'task',
        taskJournalItemId: 903,
        createdAt: new Date().toISOString(),
      });
    });
    expect(toastMock.showToast).not.toHaveBeenCalled();

    visibility.mockReturnValue('visible');
    await act(async () => document.dispatchEvent(new Event('visibilitychange')));
    expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Изменён срок задачи' }));
    unmount();
    visibility.mockRestore();
  });

  it('does not increment unread notifications twice when the same event is delivered again', async () => {
    const listener = vi.fn();
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();
    const notification: RealtimeUserNotification = {
      id: 904, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:904:changed',
      title: 'Изменён исполнитель', bodyPreview: 'Задача передана коллеге', tone: 'amber',
      objectType: 'task', taskJournalItemId: 904, createdAt: new Date().toISOString(),
    };
    act(() => {
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.(notification);
      connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.(notification);
    });
    expect(listener.mock.calls.filter(([event]) => event.detail.unreadDelta === 1)).toHaveLength(1);
    expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    unmount();
    window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
  });

  it('replays the remaining hidden-tab queue after a reload before the queue has drained', async () => {
    vi.useFakeTimers();
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    const first = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();
    visibility.mockReturnValue('hidden');
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    const queuedNotification: RealtimeUserNotification = {
      id: 907, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:907:changed',
      title: 'Изменён срок второй задачи', bodyPreview: 'Новый срок', tone: 'amber',
      objectType: 'task', taskJournalItemId: 907, createdAt: new Date().toISOString(),
    };
    act(() => {
      const receive = connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT);
      receive?.({ ...queuedNotification, id: 906, title: 'Первая задача', taskJournalItemId: 906 });
      receive?.(queuedNotification);
    });
    await act(async () => vi.advanceTimersByTimeAsync(10_000));
    visibility.mockReturnValue('visible');
    await act(async () => document.dispatchEvent(new Event('visibilitychange')));
    expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    first.unmount();

    expect(Number(window.localStorage.getItem('payplanner.realtimeToastLastSeenAt.v1.5')))
      .toBeLessThanOrEqual(Date.parse(queuedNotification.createdAt));
    apiMock.getUserNotifications.mockResolvedValue({ items: [{ ...queuedNotification, taskId: 907 }] });
    toastMock.showToast.mockClear();
    const second = render(<RealtimeNotificationToasts userId={5} />);
    await flushRealtimeStart();
    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: queuedNotification.title }));

    second.unmount();
    visibility.mockRestore();
    vi.useRealTimers();
  });

  it.each(['replay', 'fallback'] as const)('recovers a notification committed while an older empty %s response is pending', async (delivery) => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-01T00:00:00Z'));
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const emptySnapshot = { items: [], total: 0, page: 1, pageSize: 50 };
    let finishSnapshot: (() => void) | undefined;
    apiMock.getUserNotifications.mockReturnValueOnce(new Promise<typeof emptySnapshot>((resolve) => {
      finishSnapshot = () => resolve(emptySnapshot);
    }));
    if (delivery === 'fallback') connectionState.start.mockRejectedValueOnce(new Error('Owned fallback boundary'));
    const first = render(<RealtimeNotificationToasts userId={5} />);
    let second: ReturnType<typeof render> | undefined;
    try {
      await flushRealtimeStart();
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(1);
      // The server snapshot is empty, but its response is delayed. A new row
      // commits after that snapshot and before the response reaches this tab.
      vi.setSystemTime(new Date('2026-10-01T00:00:10Z'));
      const notification = {
        id: 908, type: 'taskChanged', eventKey: 'task-file:908:result',
        title: 'Файл прикреплён во время чтения прежнего снимка', objectType: 'task',
        objectId: 908, taskId: 908, isRead: false, toastEnabled: true,
        createdAt: new Date().toISOString(),
      };
      vi.setSystemTime(new Date('2026-10-01T00:00:20Z'));
      await act(async () => finishSnapshot?.());
      first.unmount();
      toastMock.showToast.mockClear();
      apiMock.getUserNotifications.mockResolvedValue({ items: [notification], total: 1, page: 1, pageSize: 50 });
      second = render(<RealtimeNotificationToasts userId={5} />);
      await flushRealtimeStart();
      expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: notification.title }));
    } finally {
      first.unmount();
      second?.unmount();
      errorSpy.mockRestore();
      vi.useRealTimers();
    }
  });

  it.each([false, true])('keeps the live replay boundary at confirmed time rather than later wall time (future timestamp: %s)', async (future) => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-01T00:00:00Z'));
    const first = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await flushRealtimeStart();
      vi.setSystemTime(new Date('2026-10-01T00:00:10Z'));
      act(() => connectionState.handlers.get(REALTIME_USER_NOTIFICATION_CREATED_EVENT)?.({
        id: 909, recipientUserId: 5, type: 'taskChanged', eventKey: 'task:909:changed',
        title: 'Подтверждённое уведомление', objectType: 'task', taskJournalItemId: 909,
        bodyPreview: '', tone: 'info',
        createdAt: future ? '2026-10-01T00:01:00Z' : new Date().toISOString(),
      }));
      expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Подтверждённое уведомление' }));
      vi.setSystemTime(new Date('2026-10-01T00:00:20Z'));
      first.unmount();
      expect(Number(window.localStorage.getItem('payplanner.realtimeToastLastSeenAt.v1.5')))
        .toBe(Date.parse('2026-10-01T00:00:10Z'));
    } finally {
      first.unmount();
      vi.useRealTimers();
    }
  });

  it('covers the handshake gap after an earlier empty recovery read without overlapping inbox reads', async () => {
    window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
    let finishConnection: (() => void) | undefined;
    let finishInitialRead: (() => void) | undefined;
    connectionState.start.mockImplementation(() => new Promise<void>((resolve) => { finishConnection = resolve; }));
    apiMock.getUserNotifications.mockImplementationOnce(() => new Promise((resolve) => {
      finishInitialRead = () => resolve({ items: [], total: 0, page: 1, pageSize: 50 });
    })).mockResolvedValue({
      items: [{ id: 907, type: 'taskAssigned', eventKey: 'task:907:assigned',
        title: 'Задача во время подключения', bodyPreview: 'Новая задача', tone: 'blue',
        objectType: 'task', taskId: 907, createdAt: new Date().toISOString(),
      }], total: 1, page: 1, pageSize: 50,
    });
    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
    try {
      await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(1));
      await act(async () => finishConnection?.());
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(1);
      await act(async () => finishInitialRead?.());
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Задача во время подключения' })));
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
      expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    } finally { unmount(); }
  });
  it('replays a published file while the initial realtime connection is still pending', async () => {
    window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
    let finishConnection: (() => void) | undefined;
    connectionState.start.mockImplementation(() => new Promise<void>((resolve) => { finishConnection = resolve; }));
    apiMock.getUserNotifications.mockResolvedValue({
      items: [{
        id: 906, type: 'taskChanged', eventKey: 'task:906:published',
        title: 'Файл прикреплён к задаче', bodyPreview: 'Файл доступен в карточке задачи.', tone: 'emerald',
        objectType: 'task', taskId: 906, metadataJson: JSON.stringify({ result: 'uploaded' }), createdAt: new Date(Date.now() - 30_000).toISOString(),
      }], total: 1, page: 1, pageSize: 50,
    });
    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
    try {
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Файл прикреплён к задаче', actionLabel: 'Открыть задачу',
      })));
      expect(getRealtimeNotificationDiagnostics().status).toBe('connecting');
      await act(async () => finishConnection?.());
      expect(toastMock.showToast).toHaveBeenCalledTimes(1);
    } finally { unmount(); }
  });
  it('recovers an assignment created during the initial connection handshake on a new device', async () => {
    let finishConnection: (() => void) | undefined;
    connectionState.start.mockImplementation(() => new Promise<void>((resolve) => { finishConnection = resolve; }));
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    apiMock.getUserNotifications.mockResolvedValue({
      items: [{
        id: 905, type: 'taskAssigned', eventKey: 'task:905:assigned',
        title: 'Новая задача во время подключения', bodyPreview: 'Проверить документы', tone: 'blue',
        objectType: 'task', taskId: 905, createdAt: new Date().toISOString(),
      }],
    });
    await act(async () => finishConnection?.());

    expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Новая задача во время подключения' }));
    unmount();
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
          dedupeKey: 'task:710:notification:710',
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

  it('replays UTC database timestamps without a suffix in a non-UTC browser', async () => {
    const nativeParse = Date.parse;
    const parse = vi.spyOn(Date, 'parse').mockImplementation((value) =>
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+$/.test(value)
        ? nativeParse(`${value}Z`) - 7 * 60 * 60 * 1000
        : nativeParse(value));
    try {
      window.localStorage.setItem('payplanner.realtimeToastLastSeenAt.v1.5', String(Date.now() - 120_000));
      apiMock.getUserNotifications.mockResolvedValue({
        items: [{
          id: 711, type: 'taskChanged', title: 'Файл прикреплён к задаче',
          bodyPreview: 'Загрузка завершена.', eventKey: 'task-file:711:result',
          objectType: 'task', objectId: 711, taskId: 711, isRead: false,
          createdAt: new Date(Date.now() - 60_000).toISOString().replace(/Z$/, ''),
        }], total: 1, page: 1, pageSize: 50,
      });
      render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Файл прикреплён к задаче' }),
      ));
    } finally {
      parse.mockRestore();
    }
  });

  it('shows a link to the inbox when more missed notifications exist than can be replayed', async () => {
    const createdAt = new Date(Date.now() + 1000).toISOString();
    apiMock.getUserNotifications.mockResolvedValue({
      items: Array.from({ length: 26 }, (_, index) => ({
        id: index + 1,
        type: 'taskChanged',
        eventKey: `task:${index + 1}:changed`,
        title: `Задача ${index + 1}`,
        bodyPreview: 'Изменение задачи.',
        tone: 'blue',
        objectType: 'task',
        objectId: index + 1,
        taskId: index + 1,
        createdAt,
      })),
      total: 26,
      page: 1,
      pageSize: 50,
    });
    const openNotifications = vi.fn();

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenNotifications={openNotifications} />);
    try {
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Есть другие новые уведомления',
        actionLabel: 'К уведомлениям',
      })));
      const overflow = toastMock.showToast.mock.calls
        .map(([options]) => options)
        .find((options) => options.title === 'Есть другие новые уведомления');
      act(() => overflow.onAction());
      expect(openNotifications).toHaveBeenCalledTimes(1);
    } finally {
      unmount();
    }
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
          dedupeKey: 'task:701:notification:701',
          domain: 'task',
        }),
      ),
    );

    unmount();
  });

  it('continues fallback polling past the first page of recent notifications', async () => {
    const createdAt = new Date(Date.now() + 1000).toISOString();
    const makeNotification = (id: number) => ({
      id,
      type: 'taskChanged',
      eventKey: `task:${id}:changed`,
      title: `Изменена задача ${id}`,
      bodyPreview: 'Изменён срок задачи.',
      tone: 'amber',
      objectType: 'task',
      objectId: id,
      taskId: id,
      isRead: false,
      isArchived: false,
      createdAt,
    });
    apiMock.getUserNotifications.mockImplementation(async ({ page }: { page: number }) => ({
      items: page === 1
        ? Array.from({ length: 15 }, (_, index) => makeNotification(index + 2))
        : [makeNotification(1)],
      total: 16,
      page,
      pageSize: 15,
    }));
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));
    const changedEvents: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const handleChanged = (event: Event) => changedEvents.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
    try {
      await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledWith({
        status: 'unread', page: 2, pageSize: 15,
      }));
      await waitFor(() => expect(changedEvents.filter((event) => event.detail.reason === 'fallback-poll')).toHaveLength(16));
      expect(changedEvents.map((event) => event.detail.notificationId)).toContain(1);
      expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Изменена задача 1' }));
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
      unmount();
    }
  });

  it('points to the notification center when a fallback burst exceeds the toast queue', async () => {
    const createdAt = new Date(Date.now() + 1000).toISOString();
    apiMock.getUserNotifications.mockImplementation(async ({ page }: { page: number }) => ({
      items: Array.from({ length: 15 }, (_, index) => {
        const id = 31 - ((page - 1) * 15 + index + 1);
        return {
          id,
          type: 'taskChanged',
          eventKey: `task:${id}:changed`,
          title: `Задача ${id}`,
          bodyPreview: 'Изменение задачи.',
          tone: 'blue',
          objectType: 'task',
          objectId: id,
          taskId: id,
          createdAt,
        };
      }),
      total: 30,
      page,
      pageSize: 15,
    }));
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));
    const openNotifications = vi.fn();
    const changedEvents: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const handleChanged = (event: Event) => changedEvents.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);

    const { unmount } = render(<RealtimeNotificationToasts userId={5} onOpenNotifications={openNotifications} />);
    try {
      await waitFor(() => expect(changedEvents.filter((event) => event.detail.reason === 'fallback-poll')).toHaveLength(25));
      const overflow = toastMock.showToast.mock.calls
        .map(([options]) => options)
        .find((options) => options.title === 'Есть другие новые уведомления');
      expect(overflow).toMatchObject({ actionLabel: 'К уведомлениям' });
      act(() => overflow.onAction());
      expect(openNotifications).toHaveBeenCalledTimes(1);
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
      unmount();
    }
  });

  it('bounds fallback requests when the recent unread backlog spans many pages', async () => {
    const createdAt = new Date(Date.now() + 1000).toISOString();
    apiMock.getUserNotifications.mockImplementation(async ({ page }: { page: number }) => ({
      items: Array.from({ length: 15 }, (_, index) => {
        const id = 120 - ((page - 1) * 15 + index);
        return {
          id,
          type: 'taskChanged',
          eventKey: `task:${id}:changed`,
          title: `Задача ${id}`,
          bodyPreview: 'Изменение задачи.',
          tone: 'blue',
          objectType: 'task',
          objectId: id,
          taskId: id,
          createdAt,
        };
      }),
      total: 120,
      page,
      pageSize: 15,
    }));
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));

    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledWith({
        status: 'unread', page: 7, pageSize: 15,
      }));
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(7);
      expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Есть другие новые уведомления',
      }));
    } finally {
      unmount();
    }
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
      notificationType: 'taskAssigned',
      objectType: 'task',
      objectId: 501,
      taskJournalItemId: 501,
    });

    window.removeEventListener(USER_NOTIFICATIONS_EVENT, handleChanged);
  });

  it('forwards the global task journal signal without notification data', async () => {
    const listener = vi.fn();
    window.addEventListener(TASK_JOURNAL_REALTIME_EVENT, listener);
    render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);

    await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));

    act(() => {
      connectionState.handlers.get(REALTIME_TASK_JOURNAL_CHANGED_EVENT)?.({} as RealtimeUserNotification);
    });

    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener(TASK_JOURNAL_REALTIME_EVENT, listener);
  });

  it('forwards the targeted role-permission signal without displaying a notification', async () => {
    const events: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const listener = (event: Event) => events.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    try {
      render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
      await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
      act(() => connectionState.handlers.get('rolePermissionsChanged')?.({} as RealtimeUserNotification));
      expect(events.some((event) => event.detail.source === 'realtime' && event.detail.reason === 'role-permissions-changed')).toBe(true);
      expect(toastMock.showToast).not.toHaveBeenCalled();
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
    }
  });

  it('forwards the targeted user-role signal without displaying a notification', async () => {
    const events: CustomEvent<UserNotificationsChangedDetail>[] = [];
    const listener = (event: Event) => events.push(event as CustomEvent<UserNotificationsChangedDetail>);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, listener);
    try {
      render(<RealtimeNotificationToasts userId={5} onOpenTask={vi.fn()} />);
      await waitFor(() => expect(connectionState.start).toHaveBeenCalledTimes(1));
      act(() => connectionState.handlers.get('userRoleChanged')?.({} as RealtimeUserNotification));
      expect(events.some((event) => event.detail.source === 'realtime' && event.detail.reason === 'user-role-changed')).toBe(true);
      expect(toastMock.showToast).not.toHaveBeenCalled();
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, listener);
    }
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
  it('retains fallback polling after network loss and restores notification delivery on visibility change', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const debugSpy = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));
    apiMock.getUserNotifications.mockRejectedValue(new TypeError('Failed to fetch'));
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await flushRealtimeStart();
      await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
      await waitFor(() => expect(debugSpy).toHaveBeenCalledWith('Notifications are unavailable; polling will retry.', expect.any(TypeError)));
      expect(errorSpy).not.toHaveBeenCalled();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
      expect(getRealtimeNotificationDiagnostics()).toMatchObject({ fallbackPollingActive: true });
      apiMock.getUserNotifications.mockResolvedValue({ items: [{
        id: 1902, type: 'taskAssigned', eventKey: 'task:1902:assigned', title: 'Восстановленное уведомление',
        bodyPreview: 'Сохранённое сообщение', tone: 'blue', objectType: 'task', objectId: 1902,
        taskId: 1902, createdAt: new Date().toISOString(),
      }] });
      await act(async () => document.dispatchEvent(new Event('visibilitychange')));
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Восстановленное уведомление' })));
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally {
      unmount(); visibility.mockRestore(); errorSpy.mockRestore(); debugSpy.mockRestore();
    }
  });

  it.each(['HTTP 403 Forbidden', 'HTTP 500 Failed to fetch', 'HTTP 507 Failed to fetch', 'Invalid notification response'])('keeps a genuine fallback read failure visible: %s', async (message) => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    connectionState.start.mockRejectedValue(new TypeError('Failed to fetch'));
    apiMock.getUserNotifications.mockRejectedValue(new Error(message));
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await flushRealtimeStart();
      await waitFor(() => expect(errorSpy).toHaveBeenCalledWith('Fallback notifications polling failed.', expect.any(Error)));
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { unmount(); errorSpy.mockRestore(); }
  });

  it('restores missed notification reads after network loss without a false error or acknowledgement', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const debugSpy = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    apiMock.getUserNotifications.mockRejectedValue(new TypeError('Failed to fetch'));
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await flushRealtimeStart();
      await waitFor(() => expect(debugSpy).toHaveBeenCalledWith('Notifications are unavailable; replay will retry.', expect.any(TypeError)));
      expect(errorSpy).not.toHaveBeenCalled();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
      apiMock.getUserNotifications.mockResolvedValue({ items: [{
        id: 1903, type: 'taskAssigned', eventKey: 'task:1903:assigned', title: 'Сообщение после восстановления',
        bodyPreview: 'Исходное уведомление', tone: 'blue', objectType: 'task', objectId: 1903,
        taskId: 1903, createdAt: new Date().toISOString(),
      }] });
      await act(async () => document.dispatchEvent(new Event('visibilitychange')));
      await waitFor(() => expect(toastMock.showToast).toHaveBeenCalledWith(expect.objectContaining({ title: 'Сообщение после восстановления' })));
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { unmount(); visibility.mockRestore(); errorSpy.mockRestore(); debugSpy.mockRestore(); }
  });

  it.each(['HTTP 403 Forbidden', 'HTTP 500 Failed to fetch', 'Invalid notification response'])('keeps a genuine missed-notification read failure visible: %s', async (message) => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    apiMock.getUserNotifications.mockRejectedValue(new Error(message));
    const { unmount } = render(<RealtimeNotificationToasts userId={5} />);
    try {
      await flushRealtimeStart();
      await waitFor(() => expect(errorSpy).toHaveBeenCalledWith('Missed notifications replay failed.', expect.any(Error)));
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { unmount(); errorSpy.mockRestore(); }
  });

});
