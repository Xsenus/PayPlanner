import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ForcedTaskRevisionModal } from './ForcedTaskRevisionModal';
import type { PagedResult, UserNotificationDto } from '../../services/api';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import {
  FORCED_TASK_SNOOZE_STORAGE_KEY,
  getForcedTaskApprovalSnoozeKey,
} from '../../utils/forcedTaskActionSnoozes';

const apiMock = vi.hoisted(() => ({
  approveTaskJournalDeadlineChangeRequest: vi.fn(),
  getTaskJournalItem: vi.fn(),
  getUserSetting: vi.fn(),
  getUserNotifications: vi.fn(),
  markUserNotificationRead: vi.fn(),
  rejectTaskJournalDeadlineChangeRequest: vi.fn(),
  updateTaskJournalApproval: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

function createNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return {
    id: 31,
    type: 'taskRevisionRequested',
    typeLabel: 'Возврат на доработку',
    eventKey: 'task:101:approval-decision',
    title: 'Задача возвращена на доработку: Подготовить иск',
    bodyPreview: 'Исправить дату и приложить документ.',
    tone: 'orange',
    toastEnabled: false,
    objectType: 'task',
    objectId: 101,
    taskId: 101,
    metadataJson: '{"resolution":"Уточнить дату заседания."}',
    requiresAction: true,
    isRead: false,
    readAt: null,
    isArchived: false,
    archivedAt: null,
    createdAt: '2026-06-29T12:00:00Z',
    ...overrides,
  };
}

function createPage(items: UserNotificationDto[]): PagedResult<UserNotificationDto> {
  return {
    items,
    total: items.length,
    page: 1,
    pageSize: 10,
  };
}

function createCompletedApprovalNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return createNotification({
    id: 41,
    type: 'taskApproved',
    typeLabel: 'Задача согласована',
    eventKey: 'task:202:approval-decision',
    title: 'Задача согласована и завершена: Проверить договор',
    bodyPreview: 'Согласующий согласовал и завершил задачу.',
    tone: 'emerald',
    objectId: 202,
    taskId: 202,
    metadataJson: '{"decision":"approved","resultingStatus":"done","completed":true}',
    ...overrides,
  });
}

function createDeadlineChangeNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return createNotification({
    id: 51,
    type: 'taskDeadlineChangeRequested',
    typeLabel: 'Запрос переноса срока',
    eventKey: 'task:303:deadline-change-request:901:requested',
    title: 'Запрос переноса срока: Подготовить претензию',
    bodyPreview: 'Исполнитель запросил перенос срока задачи.',
    tone: 'violet',
    objectId: 303,
    taskId: 303,
    metadataJson: JSON.stringify({
      deadlineChangeRequestId: 901,
      requestedByName: 'Иван Петров',
      reviewerName: 'Анна Смирнова',
      requestedDueAt: '2026-06-30T09:00:00Z',
      reason: 'Нужно дождаться ответа клиента.',
    }),
    ...overrides,
  });
}

function createApprovalRequestNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return createNotification({
    id: 61,
    type: 'taskApprovalRequested',
    typeLabel: 'Ожидает согласования',
    eventKey: 'task:404:approval-request:701:20260630120000000',
    title: 'Задача ожидает согласования: Проверить иск',
    bodyPreview: 'Юрист отправил результат задачи на согласование.',
    tone: 'violet',
    objectId: 404,
    taskId: 404,
    metadataJson: JSON.stringify({
      actorUserId: 8,
      actorName: 'Мария Юрист',
      currentApproverId: 701,
    }),
    ...overrides,
  });
}

function createApprovalTask(overrides: Record<string, unknown> = {}) {
  return {
    id: 404,
    title: 'Проверить иск',
    description: 'Проверить правовую позицию и документы.',
    assigneeName: 'Мария Юрист',
    authorName: 'Андрей Руководитель',
    dueAt: '2026-07-01T09:00:00Z',
    result: 'Иск проверен, правки внесены, документы готовы.',
    participants: [
      {
        id: 701,
        userId: 17,
        role: 'approver',
        displayName: 'Анна Согласующая',
        sortOrder: 0,
        approvalStatus: 'pending',
      },
    ],
    ...overrides,
  };
}

function mockNotificationPages(
  revisions: UserNotificationDto[] = [],
  approvals: UserNotificationDto[] = [],
  deadlineChanges: UserNotificationDto[] = [],
  approvalRequests: UserNotificationDto[] = [],
) {
  apiMock.getUserNotifications.mockImplementation(async (params?: { type?: string }) => {
    if (params?.type === 'taskApprovalRequested') {
      return createPage(approvalRequests);
    }

    if (params?.type === 'taskDeadlineChangeRequested') {
      return createPage(deadlineChanges);
    }

    if (params?.type === 'taskApproved') {
      return createPage(approvals);
    }

    if (params?.type === 'taskRevisionRequested') {
      return createPage(revisions);
    }

    return createPage([]);
  });
}

describe('ForcedTaskRevisionModal', () => {
  beforeEach(() => {
    apiMock.approveTaskJournalDeadlineChangeRequest.mockReset();
    apiMock.getTaskJournalItem.mockReset();
    apiMock.getUserSetting.mockReset();
    apiMock.getUserNotifications.mockReset();
    apiMock.markUserNotificationRead.mockReset();
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockReset();
    apiMock.updateTaskJournalApproval.mockReset();
    window.localStorage.clear();
    apiMock.approveTaskJournalDeadlineChangeRequest.mockResolvedValue({});
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask());
    apiMock.getUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    apiMock.markUserNotificationRead.mockImplementation(async (id: number) => createNotification({ id, isRead: true }));
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockResolvedValue({});
    apiMock.updateTaskJournalApproval.mockResolvedValue({});
  });

  it('shows unread revision requests as a mandatory modal and acknowledges them', async () => {
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Задача на доработку')).toBeTruthy();
    expect(screen.getByText('Задача вернулась на доработку')).toBeTruthy();
    expect(screen.getByText('Подготовить иск')).toBeTruthy();
    expect(screen.getByText('Уточнить дату заседания.')).toBeTruthy();
    expect(screen.getByTestId('forced-task-message-box').className).toContain('min-h-[120px]');

    fireEvent.click(screen.getByRole('button', { name: 'Информация прочитана' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(31));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('snoozes a mandatory modal without marking the notification read', async () => {
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    fireEvent.click(screen.getByText('Отложить'));
    fireEvent.click(screen.getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem('pp.forcedTaskModalSnoozes.v1') || '{}') as Record<string, number>;
    expect(snoozes['31']).toBeGreaterThan(Date.now());

    window.dispatchEvent(new Event(USER_NOTIFICATIONS_EVENT));
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('shares an approval snooze with the approvals journal', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal />);

    const prompt = await screen.findByTestId('forced-task-approval-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    const snoozes = JSON.parse(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY) || '{}') as Record<string, number>;
    expect(snoozes['61']).toBeGreaterThan(Date.now());
    expect(snoozes[getForcedTaskApprovalSnoozeKey(404)]).toBeGreaterThan(Date.now());
  });

  it('migrates an active notification-only approval snooze to the shared task key', async () => {
    const snoozedUntil = Date.now() + 30 * 60_000;
    window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, JSON.stringify({ '61': snoozedUntil }));
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => {
      const snoozes = JSON.parse(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY) || '{}') as Record<string, number>;
      expect(snoozes[getForcedTaskApprovalSnoozeKey(404)]).toBe(snoozedUntil);
    });
    expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull();
  });

  it('marks the message read before opening the task', async () => {
    const onOpenTask = vi.fn();
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(31));
    expect(onOpenTask).toHaveBeenCalledWith(101);
  });

  it('shows completed approvals as a mandatory read confirmation', async () => {
    mockNotificationPages([], [createCompletedApprovalNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Согласование завершено')).toBeTruthy();
    expect(screen.getByText('Задача согласована и завершена')).toBeTruthy();
    expect(screen.getByText('Проверить договор')).toBeTruthy();
    expect(screen.getByText('Согласующий согласовал и завершил задачу.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Ознакомлен' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(41));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('loads configured forced notifications into one queue and keeps the oldest item first', async () => {
    mockNotificationPages(
      [createNotification({ id: 32, createdAt: '2026-06-29T12:05:00Z' })],
      [createCompletedApprovalNotification({ id: 41, createdAt: '2026-06-29T12:00:00Z' })],
    );

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-action-queue')).toBeTruthy();
    expect(screen.getByText('Еще обязательных уведомлений: 1')).toBeTruthy();
    expect(screen.getByText('Согласование завершено')).toBeTruthy();
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskApprovalRequested',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskDeadlineChangeRequested',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskRevisionRequested',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskApproved',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));

    fireEvent.click(screen.getByRole('button', { name: 'Ознакомлен' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(41));
    expect(await screen.findByText('Задача на доработку')).toBeTruthy();
  });

  it('lets the reviewer approve a deadline change request from the forced queue', async () => {
    mockNotificationPages([], [], [createDeadlineChangeNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();
    expect(screen.getByText('У вас запросили перенос срока по задаче')).toBeTruthy();
    expect(screen.getByText('Подготовить претензию')).toBeTruthy();
    expect(screen.getByText(/Нужно дождаться ответа клиента/)).toBeTruthy();

    const dueAtInput = screen.getByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(dueAtInput.value).toBe('2026-06-30T13:00'));

    fireEvent.change(dueAtInput, { target: { value: '2026-07-01T10:30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Согласовать перенос' }));

    await waitFor(() => expect(apiMock.approveTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(303, 901, {
      approvedDueAt: '2026-07-01T06:30:00.000Z',
      comment: null,
    }));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('requires a comment before rejecting a deadline change request from the forced queue', async () => {
    mockNotificationPages([], [], [createDeadlineChangeNotification({ id: 52 })]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отказать' }));

    expect(await screen.findByText('Укажите причину отказа в переносе срока.')).toBeTruthy();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Комментарий к решению'), {
      target: { value: 'Клиент уже подтвердил прежний срок.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отказать' }));

    await waitFor(() => expect(apiMock.rejectTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(303, 901, {
      comment: 'Клиент уже подтвердил прежний срок.',
    }));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(52));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('lets the approver approve and complete a task from the forced approval prompt', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-approval-prompt')).toBeTruthy();
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(404);
    expect(screen.getByText('У вас задача на согласовании')).toBeTruthy();
    expect(screen.getByText('Проверить иск')).toBeTruthy();
    expect(screen.getByText('Анна Согласующая')).toBeTruthy();
    expect(screen.getByText('Иск проверен, правки внесены, документы готовы.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать и завершить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      404,
      'approved',
      'Иск проверен, правки внесены, документы готовы.',
      701,
      'complete',
    ));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(61));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('requires a revision comment before returning an approval task to revision', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification({ id: 62 })]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-approval-prompt')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Вернуть на доработку' }));

    expect(await screen.findByText('Укажите, что нужно исправить перед возвратом задачи на доработку.')).toBeTruthy();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Комментарий для возврата на доработку'), {
      target: { value: 'Нужно добавить расчет госпошлины.' },
    });
    fireEvent.change(screen.getByLabelText('Срок доработки'), {
      target: { value: '2026-07-02T11:00' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Вернуть на доработку' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      404,
      'revisionRequested',
      'Нужно добавить расчет госпошлины.',
      701,
      undefined,
      '2026-07-02T07:00:00.000Z',
    ));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(62));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('restores a forced notification from the unread inbox after the notification event', async () => {
    const revisions: UserNotificationDto[] = [];
    mockNotificationPages(revisions, []);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();

    revisions.splice(0, revisions.length, createNotification({ id: 51 }));
    window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT));

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Задача на доработку')).toBeTruthy();
  });
  it('does not show the mandatory modal when the forced modal channel is disabled', async () => {
    mockNotificationPages([createNotification({ forcedModalEnabled: false })]);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('does not restore an acknowledged notification when the inbox reloads before the server hides it', async () => {
    const revision = createNotification({ id: 71 });
    mockNotificationPages([revision]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Информация прочитана' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(71));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT));

    await waitFor(() => expect(apiMock.getUserNotifications.mock.calls.length).toBeGreaterThanOrEqual(8));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledTimes(1);
  });
});
