import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { StrictMode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ForcedTaskRevisionModal } from './ForcedTaskRevisionModal';
import { AUTH_SESSION_EXPIRED_EVENT, type PagedResult, type UserNotificationDto } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';
import { TASK_JOURNAL_REALTIME_EVENT } from '../../services/realtimeNotifications';
import {
  FORCED_TASK_SNOOZE_STORAGE_KEY,
  getForcedTaskApprovalSnoozeKey,
  getForcedTaskSnoozeStorageKey,
  snoozeForcedTaskActions,
} from '../../utils/forcedTaskActionSnoozes';

const expectedForcedNotificationTypes = [
  'courtHearing', 'taskApprovalRequested', 'taskDeadlineChangeRequested',
  'taskRevisionRequested', 'taskApproved', 'taskCompleted',
] as const;

const authState = vi.hoisted((): { user: { id: number; role?: { id: number }; departmentId?: number | null; department?: string | null } | null } => ({ user: { id: 7 } }));

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => authState }));
const permissionsState = vi.hoisted(() => ({
  tasks: { canView: true, viewScope: 'all' },
  cases: { canView: true, viewScope: 'all' },
}));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => permissionsState }));

const apiMock = vi.hoisted(() => ({
  approveTaskJournalDeadlineChangeRequest: vi.fn(),
  downloadTaskJournalFile: vi.fn(),
  getTaskJournalDeadlineChangeRequest: vi.fn(),
  getTaskJournalFiles: vi.fn(),
  getTaskJournalItem: vi.fn(),
  getUserSetting: vi.fn(),
  getUserNotifications: vi.fn(),
  markUserNotificationRead: vi.fn(),
  rejectTaskJournalDeadlineChangeRequest: vi.fn(),
  updateTaskJournalApproval: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
  AUTH_SESSION_EXPIRED_EVENT: 'payplanner:auth-session-expired',
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

function createCompletedTaskNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return createNotification({
    id: 81, type: 'taskCompleted', typeLabel: 'Задача завершена',
    eventKey: 'task:505:completed', title: 'Задача выполнена: Подготовить документы',
    bodyPreview: 'Мария Юрист завершила задачу. Документы подготовлены.',
    taskId: 505, objectId: 505, tone: 'emerald', forcedModalEnabled: true,
    metadataJson: '{"status":"done","completionOrigin":"ordinary"}', ...overrides,
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
      currentDueAt: '2026-06-28T09:00:00Z',
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
  courtHearings: UserNotificationDto[] = [],
  completions: UserNotificationDto[] = [],
) {
  apiMock.getUserNotifications.mockImplementation(async (params?: { type?: string }) => {
    if (params?.type === 'taskCompleted') return createPage(completions);

    if (params?.type === 'courtHearing') {
      return createPage(courtHearings);
    }

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
    authState.user = { id: 7 };
    permissionsState.tasks = { canView: true, viewScope: 'all' };
    permissionsState.cases = { canView: true, viewScope: 'all' };
    apiMock.approveTaskJournalDeadlineChangeRequest.mockReset();
    apiMock.downloadTaskJournalFile.mockReset();
    apiMock.getTaskJournalDeadlineChangeRequest.mockReset();
    apiMock.getTaskJournalFiles.mockReset();
    apiMock.getTaskJournalItem.mockReset();
    apiMock.getUserSetting.mockReset();
    apiMock.getUserNotifications.mockReset();
    apiMock.markUserNotificationRead.mockReset();
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockReset();
    apiMock.updateTaskJournalApproval.mockReset();
    window.localStorage.clear();
    apiMock.approveTaskJournalDeadlineChangeRequest.mockResolvedValue({});
    apiMock.getTaskJournalFiles.mockResolvedValue([]);
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValue({
      id: 901,
      taskJournalItemId: 303,
      requestedByUserId: 7,
      requestedByName: 'Иван Петров',
      reviewerUserId: 8,
      reviewerName: 'Анна Смирнова',
      currentDueAt: '2026-06-28T09:00:00Z',
      requestedDueAt: '2026-06-30T09:00:00Z',
      reason: 'Нужно дождаться ответа клиента.',
      status: 'pending',
      createdAt: '2026-06-27T09:00:00Z',
    });
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask());
    apiMock.getUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    apiMock.markUserNotificationRead.mockImplementation(async (id: number) => createNotification({ id, isRead: true }));
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockResolvedValue({});
    apiMock.updateTaskJournalApproval.mockResolvedValue({});
  });

  it('shows an ordinary completion in the central queue and acknowledges it with Понятно', async () => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
    const onOpenTask = vi.fn();
    render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);
    expect(await screen.findByRole('heading', { name: 'Задача завершена' })).toBeTruthy();
    expect(screen.getByText('Подготовить документы')).toBeTruthy();
    expect(screen.getByText('Мария Юрист завершила задачу. Документы подготовлены.')).toBeTruthy();
    expect(apiMock.getTaskJournalItem).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Понятно' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(81);
    expect(onOpenTask).not.toHaveBeenCalled();
  });

  it('acknowledges the ordinary completion before opening its exact task', async () => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
    let finish!: (notification: UserNotificationDto) => void;
    apiMock.markUserNotificationRead.mockReturnValue(new Promise<UserNotificationDto>(resolve => { finish = resolve; }));
    const onOpenTask = vi.fn();
    render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);
    fireEvent.click(await screen.findByRole('button', { name: 'К задаче' }));
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(81);
    expect(onOpenTask).not.toHaveBeenCalled();
    await act(async () => finish(createCompletedTaskNotification({ isRead: true })));
    expect(onOpenTask).toHaveBeenCalledWith(505);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it.each(['Понятно', 'К задаче'])('keeps the ordinary completion retryable when %s acknowledgement fails', async button => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
      apiMock.markUserNotificationRead.mockRejectedValueOnce(new Error('Нет связи с сервером'));
      const onOpenTask = vi.fn();
      render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);
      fireEvent.click(await screen.findByRole('button', { name: button }));
      expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Нет связи с сервером');
      expect(onOpenTask).not.toHaveBeenCalled();
      fireEvent.click(screen.getByRole('button', { name: button }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      expect(apiMock.markUserNotificationRead).toHaveBeenCalledTimes(2);
      expect(onOpenTask).toHaveBeenCalledTimes(button === 'К задаче' ? 1 : 0);
    } finally { error.mockRestore(); }
  });

  it.each([
    { metadataJson: '{}' }, { metadataJson: '{' }, { metadataJson: '{"completionOrigin":"approval"}' },
    { metadataJson: '{"completionOrigin":"legacy"}' }, { metadataJson: '{"completionOrigin":"Ordinary"}' },
    { forcedModalEnabled: false }, { forcedModalEnabled: undefined }, { taskId: null, forcedModalEnabled: true },
  ])('keeps historical or disabled completion outside the central queue for %j', async overrides => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification({ forcedModalEnabled: false, ...overrides })]);
    render(<ForcedTaskRevisionModal />);
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(6));
    await act(async () => { await Promise.resolve(); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it.each(['{}', '{', '{"completionOrigin":"legacy"}'])('trusts the authoritative completion window flag when metadata is %s', async metadataJson => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification({ metadataJson })]);
    render(<ForcedTaskRevisionModal />);
    expect(await screen.findByRole('button', { name: 'Понятно' })).toBeTruthy();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('keeps one completed-approval modal when its durable completion also exists', async () => {
    mockNotificationPages([], [createCompletedApprovalNotification()], [], [], [], [createCompletedTaskNotification({ forcedModalEnabled: false, metadataJson: '{"completionOrigin":"approval"}' })]);
    render(<ForcedTaskRevisionModal />);
    expect(await screen.findByText('Согласование завершено')).toBeTruthy();
    expect(screen.queryByText(/Еще обязательных уведомлений/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Ознакомлен' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(41);
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledTimes(1);
  });

  it('retains an ordinary completion snooze across reload and restores it after expiry', async () => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
    const first = render(<ForcedTaskRevisionModal />);
    fireEvent.click(await screen.findByText('Отложить'));
    fireEvent.click(screen.getByRole('button', { name: '30 мин' }));
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    first.unmount();
    render(<ForcedTaskRevisionModal />);
    await waitFor(() => expect(apiMock.getUserNotifications.mock.calls.filter(([params]) => params.type === 'taskCompleted')).toHaveLength(2));
    expect(screen.queryByRole('dialog')).toBeNull();
    const now = vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 31 * 60_000);
    try {
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      expect(await screen.findByRole('button', { name: 'Понятно' })).toBeTruthy();
    } finally { now.mockRestore(); }
  });

  it('removes the ordinary completion after another inbox entry marks it read', async () => {
    const completions = [createCompletedTaskNotification()];
    mockNotificationPages([], [], [], [], [], completions);
    render(<ForcedTaskRevisionModal />);
    await screen.findByRole('button', { name: 'Понятно' });
    completions[0] = { ...completions[0], isRead: true };
    fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT, { detail: { source: 'inbox', reason: 'read', notificationId: 81 } }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it.each(['actor', 'login', 'logout'] as const)('discards a late ordinary inbox response after auth %s before the context rerenders', async replacement => {
    const session = documentCopyTestSession();
    try {
      let finish!: (page: PagedResult<UserNotificationDto>) => void;
      apiMock.getUserNotifications.mockImplementation(async params => params?.type === 'taskCompleted'
        ? new Promise<PagedResult<UserNotificationDto>>(resolve => { finish = resolve; })
        : createPage([]));
      render(<ForcedTaskRevisionModal />);
      await waitFor(() => expect(finish).toBeTruthy());
      session.replace(replacement);
      await act(async () => finish(createPage([createCompletedTaskNotification()])));
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it.each(['account', 'logout', 'session', 'role', 'scope', 'department', 'legacyDepartment', 'permission'].flatMap(owner => ['success', 'error'].map(outcome => ({ owner, outcome }))))('discards a late ordinary acknowledgement $outcome after $owner changes', async ({ owner, outcome }) => {
    const session = documentCopyTestSession();
    const notifications = vi.fn();
    window.addEventListener(USER_NOTIFICATIONS_EVENT, notifications);
    try {
      authState.user = { id: 7, role: { id: 3 }, departmentId: owner === 'legacyDepartment' ? null : 1, department: 'Первый отдел' };
      mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
      let finish!: (notification: UserNotificationDto) => void;
      let fail!: (error: Error) => void;
      apiMock.markUserNotificationRead.mockReturnValue(new Promise<UserNotificationDto>((resolve, reject) => { finish = resolve; fail = reject; }));
      const onOpenTask = vi.fn();
      const view = render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);
      fireEvent.click(await screen.findByRole('button', { name: 'К задаче' }));
      mockNotificationPages();
      if (owner === 'account') authState.user = { id: 8, role: { id: 3 } };
      if (owner === 'logout') { authState.user = null; session.replace('logout'); }
      if (owner === 'session') session.replace('login');
      if (owner === 'role') authState.user = { id: 7, role: { id: 4 } };
      if (owner === 'scope') permissionsState.tasks = { canView: true, viewScope: 'own' };
      if (owner === 'department') authState.user = { ...authState.user!, departmentId: 2 };
      if (owner === 'legacyDepartment') authState.user = { ...authState.user!, department: 'Другой отдел' };
      view.rerender(<ForcedTaskRevisionModal onOpenTask={owner === 'permission' ? undefined : onOpenTask} />);
      notifications.mockClear();
      await act(async () => {
        if (outcome === 'success') finish(createCompletedTaskNotification({ isRead: true }));
        else fail(new Error('Ответ предыдущего владельца'));
      });
      expect(onOpenTask).not.toHaveBeenCalled();
      expect(notifications).not.toHaveBeenCalled();
      expect(screen.queryByRole('alert')).toBeNull();
      if (owner !== 'permission') expect(screen.queryByText('Мария Юрист завершила задачу. Документы подготовлены.')).toBeNull();
    } finally {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, notifications);
      session.restore();
    }
  });

  it('loads an ordinary completion under StrictMode through one finite startup read', async () => {
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
    render(<StrictMode><ForcedTaskRevisionModal /></StrictMode>);
    expect(await screen.findByRole('button', { name: 'Понятно' })).toBeTruthy();
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(6);
  });

  it('discards the ordinary window on an explicit auth signal while storage is unreadable', async () => {
    const session = documentCopyTestSession();
    mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
    const view = render(<ForcedTaskRevisionModal />);
    await screen.findByRole('button', { name: 'Понятно' });
    const storage = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage unavailable'); });
    try {
      view.rerender(<ForcedTaskRevisionModal />);
      expect(screen.getByRole('dialog')).toBeTruthy();
      fireEvent(window, new Event(AUTH_SESSION_EXPIRED_EVENT));
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { storage.mockRestore(); session.restore(); }
  });

  it('keeps the ordinary window across a same-session token refresh signal', async () => {
    const session = documentCopyTestSession();
    try {
      mockNotificationPages([], [], [], [], [], [createCompletedTaskNotification()]);
      const view = render(<ForcedTaskRevisionModal />);
      await screen.findByRole('button', { name: 'Понятно' });
      const reads = apiMock.getUserNotifications.mock.calls.length;
      session.refresh();
      fireEvent(window, new StorageEvent('storage', { key: 'pp_token' }));
      view.rerender(<ForcedTaskRevisionModal />);
      expect(screen.getByRole('button', { name: 'Понятно' })).toBeTruthy();
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(reads);
    } finally { session.restore(); }
  });

  it('coalesces mandatory notification startup reads under StrictMode', async () => {
    const pending: Array<() => void> = [];
    apiMock.getUserNotifications.mockImplementation(() => new Promise<PagedResult<UserNotificationDto>>((resolve) => {
      pending.push(() => resolve(createPage([])));
    }));
    const view = render(<StrictMode><ForcedTaskRevisionModal /></StrictMode>);
    try {
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
    } finally {
      // Settle all original loads before the next test, including a failed baseline.
      for (let round = 0; round < 6; round++) {
        await act(async () => { pending.splice(0).forEach(finish => finish()); });
      }
      view.unmount();
    }
  });

  it('coalesces overlapping mandatory notification events into the active load', async () => {
    const pending: Array<() => void> = [];
    apiMock.getUserNotifications.mockImplementation(() => new Promise<PagedResult<UserNotificationDto>>((resolve) => {
      pending.push(() => resolve(createPage([])));
    }));
    const view = render(<ForcedTaskRevisionModal />);
    try {
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
    } finally {
      for (let round = 0; round < 6; round++) {
        await act(async () => { pending.splice(0).forEach(finish => finish()); });
      }
      view.unmount();
    }
  });

  it('refreshes fresh mandatory notifications once after overlapping events', async () => {
    const pending: Array<() => void> = [];
    let calls = 0;
    let maximumPending = 0;
    apiMock.getUserNotifications.mockImplementation((params) => {
      const refreshed = ++calls > 6;
      return new Promise<PagedResult<UserNotificationDto>>((resolve) => {
        pending.push(() => resolve(createPage(refreshed && params.type === 'taskRevisionRequested'
          ? [createNotification({ title: 'Задача возвращена на доработку: Свежий результат' })] : [])));
        maximumPending = Math.max(maximumPending, pending.length);
      });
    });
    const view = render(<ForcedTaskRevisionModal />);
    try {
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      expect(calls).toBe(2);
      for (let round = 0; round < 6; round++) {
        await act(async () => { pending.splice(0).forEach(finish => finish()); });
      }
      expect(await screen.findByText('Свежий результат')).toBeTruthy();
      expect(calls).toBe(12);
      expect(maximumPending).toBe(2);
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { view.unmount(); }
  });

  it('stops later mandatory notification batches after unmount', async () => {
    const pending: Array<() => void> = [];
    apiMock.getUserNotifications.mockImplementation(() => new Promise<PagedResult<UserNotificationDto>>((resolve) => {
      pending.push(() => resolve(createPage([createNotification()])));
    }));
    const view = render(<ForcedTaskRevisionModal />);
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
    view.unmount();
    await act(async () => { pending.splice(0).forEach(finish => finish()); });
    fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('keeps mandatory notification loads separate across keyed employee changes', async () => {
    let employee = 'previous';
    const previous: Array<() => void> = [];
    const current: Array<() => void> = [];
    apiMock.getUserNotifications.mockImplementation((params) => {
      const owner = employee;
      return new Promise<PagedResult<UserNotificationDto>>((resolve) => {
        (owner === 'previous' ? previous : current).push(() => resolve(createPage(
          params.type === 'taskRevisionRequested'
            ? [createNotification({ title: `Задача возвращена на доработку: ${owner}` })] : [],
        )));
      });
    });
    const view = render(<ForcedTaskRevisionModal key={7} />);
    try {
      employee = 'current';
      authState.user = { id: 8 };
      view.rerender(<ForcedTaskRevisionModal key={8} />);
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(4);
      await act(async () => { previous.splice(0).forEach(finish => finish()); });
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(4);
      for (let round = 0; round < 3; round++) {
        await act(async () => { current.splice(0).forEach(finish => finish()); });
      }
      expect(await screen.findByText('current')).toBeTruthy();
      expect(screen.queryByText('previous')).toBeNull();
      expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(8);
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { view.unmount(); }
  });

  it('finishes a failed mandatory notification batch before refreshing a queued event', async () => {
    let finishPending!: () => void;
    let calls = 0;
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    apiMock.getUserNotifications.mockImplementation((params) => {
      calls++;
      if (calls === 1) return Promise.reject(new TypeError('Failed to fetch'));
      if (calls === 2) return new Promise<PagedResult<UserNotificationDto>>((resolve) => {
        finishPending = () => resolve(createPage([]));
      });
      return Promise.resolve(createPage(params.type === 'taskRevisionRequested'
        ? [createNotification({ title: 'Задача возвращена на доработку: После восстановления' })] : []));
    });
    const view = render(<ForcedTaskRevisionModal />);
    try {
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      await act(async () => { await Promise.resolve(); });
      expect(calls).toBe(2);
      await act(async () => { finishPending(); });
      expect(await screen.findByText('После восстановления')).toBeTruthy();
      expect(calls).toBe(8);
      expect(debug).toHaveBeenCalled();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { view.unmount(); debug.mockRestore(); }
  });

  it('bounds simultaneous reads while retaining every forced notification type', async () => {
    const pending: Array<() => void> = [];
    let maximumPending = 0;
    apiMock.getUserNotifications.mockImplementation(() => new Promise<PagedResult<UserNotificationDto>>((resolve) => {
      pending.push(() => resolve(createPage([])));
      maximumPending = Math.max(maximumPending, pending.length);
    }));
    const { unmount } = render(<ForcedTaskRevisionModal onOpenTask={vi.fn()} />);
    try {
      for (let completed = 0; completed < 6;) {
        const batchSize = Math.min(2, 6 - completed);
        await waitFor(() => expect(pending).toHaveLength(batchSize));
        expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(completed + batchSize);
        await act(async () => { pending.splice(0).forEach(finish => finish()); });
        completed += batchSize;
      }
      expect(maximumPending).toBe(2);
      expect(apiMock.getUserNotifications.mock.calls.map(([params]) => params.type)).toEqual([
        'courtHearing', 'taskApprovalRequested', 'taskDeadlineChangeRequested', 'taskRevisionRequested', 'taskApproved', 'taskCompleted',
      ]);
      for (const [params] of apiMock.getUserNotifications.mock.calls)
        expect(params).toEqual(expect.objectContaining({ status: 'unread', requiresAction: true, page: 1, pageSize: 25 }));
    } finally { unmount(); }
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

  it('shows a mandatory hearing warning and opens the case task section', async () => {
    const onOpenCaseTasks = vi.fn();
    const hearing = createNotification({
      id: 71,
      type: 'courtHearing',
      typeLabel: 'Судебное заседание',
      eventKey: 'case:55:court-event:91:courtHearing',
      title: 'Судебное заседание: №2-100/2026 · Иванов - иск',
      bodyPreview: 'Заседание назначено на 15.09.2026 10:00. Суд: Районный суд.',
      tone: 'cyan',
      forcedModalEnabled: true,
      objectType: 'case',
      objectId: 55,
      taskId: null,
      metadataJson: '{"clientCaseId":55,"courtEventId":91}',
    });
    mockNotificationPages([], [], [], [], [hearing]);

    render(<ForcedTaskRevisionModal onOpenCaseTasks={onOpenCaseTasks} />);

    const prompt = await screen.findByTestId('forced-court-hearing-prompt');
    expect(within(prompt).getByText('Внимание — назначено судебное заседание по делу')).toBeTruthy();
    expect(within(prompt).getByText('Проверьте задачи для юриста')).toBeTruthy();

    fireEvent.click(within(prompt).getByRole('button', { name: 'Открыть задачи дела' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(71));
    expect(onOpenCaseTasks).toHaveBeenCalledWith(55);
  });

  it('snoozes a mandatory modal without marking the notification read', async () => {
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    fireEvent.click(screen.getByText('Отложить'));
    fireEvent.click(screen.getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!) || '{}') as Record<string, number>;
    expect(snoozes['31']).toBeGreaterThan(Date.now());

    window.dispatchEvent(new Event(USER_NOTIFICATIONS_EVENT));
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it.each(['read', 'write'] as const)('keeps the mandatory modal open when snooze storage cannot %s', async (failure) => {
    mockNotificationPages([createNotification()]);
    const view = render(<ForcedTaskRevisionModal />);
    const prompt = await screen.findByRole('dialog');
    fireEvent.click(within(prompt).getByText('Отложить'));
    const storage = failure === 'read'
      ? vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage unavailable'); })
      : vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('storage unavailable'); });
    try {
      fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));
      view.rerender(<ForcedTaskRevisionModal />);
      expect(screen.getByRole('dialog')).toBe(prompt);
      expect(within(prompt).getByRole('alert').textContent).toContain('Не удалось отложить');
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { storage.mockRestore(); }
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it.each(['approval', 'deadline'] as const)('keeps the %s decision open when snooze storage fails', async (kind) => {
    if (kind === 'approval') mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    else mockNotificationPages([], [], [createDeadlineChangeNotification()]);
    render(<ForcedTaskRevisionModal />);
    const prompt = await screen.findByRole('dialog');
    const menu = await within(prompt).findByText('Отложить');
    fireEvent.click(menu);
    await waitFor(() => expect((within(prompt).getByRole('button', { name: '10 мин' }) as HTMLButtonElement).disabled).toBe(false));
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('storage unavailable'); });
    try {
      fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));
      expect(within(prompt).getByRole('alert').textContent).toContain('Не удалось отложить');
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally { write.mockRestore(); }
  });

  it('shares an approval snooze with the approvals journal', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal />);

    const prompt = await screen.findByTestId('forced-task-approval-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!) || '{}') as Record<string, number>;
    expect(snoozes['61']).toBeGreaterThan(Date.now());
    expect(snoozes[getForcedTaskApprovalSnoozeKey(404)]).toBeGreaterThan(Date.now());
  });

  it('migrates an active notification-only approval snooze to the shared task key', async () => {
    const snoozedUntil = Date.now() + 30 * 60_000;
    window.localStorage.setItem(getForcedTaskSnoozeStorageKey(7)!, JSON.stringify({ '61': snoozedUntil }));
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => {
      const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!) || '{}') as Record<string, number>;
      expect(snoozes[getForcedTaskApprovalSnoozeKey(404)]).toBe(snoozedUntil);
    });
    expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull();
  });

  it('retains a snooze on reload but shows the same pending task after logout and another employee signs in', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({
      participants: [{ id: 701, userId: 7, role: 'approver', sortOrder: 0, approvalStatus: 'pending' }],
    }));
    const view = render(<ForcedTaskRevisionModal />);
    const prompt = await screen.findByTestId('forced-task-approval-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    const original = window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!);
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2 * expectedForcedNotificationTypes.length));

    view.rerender(<ForcedTaskRevisionModal key="reload" />);
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(3 * expectedForcedNotificationTypes.length));
    expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull();
    authState.user = null;
    view.rerender(<ForcedTaskRevisionModal key="reload" />);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(3 * expectedForcedNotificationTypes.length);

    authState.user = { id: 8 };
    mockNotificationPages([], [], [], [createApprovalRequestNotification({ id: 62, metadataJson: '{"currentApproverId":702}' })]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({
      participants: [{ id: 702, userId: 8, role: 'approver', sortOrder: 0, approvalStatus: 'pending' }],
    }));
    view.rerender(<ForcedTaskRevisionModal key="reload" />);
    const nextPrompt = await screen.findByTestId('forced-task-approval-prompt');
    fireEvent.click(within(nextPrompt).getByText('Отложить'));
    fireEvent.click(within(nextPrompt).getByRole('button', { name: '30 мин' }));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!)).toBe(original);
    const next = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(8)!) || '{}') as Record<string, number>;
    expect(next[getForcedTaskApprovalSnoozeKey(404)]).toBeGreaterThan(Date.now());
    expect(next['62']).toBeGreaterThan(Date.now());
    expect(next['61']).toBeUndefined();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it('does not adopt ownerless v1 notification or shared task snoozes', async () => {
    const legacy = JSON.stringify({ '61': Date.now() + 60_000, [getForcedTaskApprovalSnoozeKey(404)]: Date.now() + 60_000 });
    window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, legacy);
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-approval-prompt')).toBeTruthy();
    expect(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY)).toBe(legacy);
    expect(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!)).toBeNull();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('preserves an open approval draft when another employee snoozes and the queue refreshes', async () => {
    let notification = createApprovalRequestNotification();
    apiMock.getUserNotifications.mockImplementation(async (params) => createPage(params.type === 'taskApprovalRequested'
      ? [{ ...notification }] : []));
    render(<ForcedTaskRevisionModal />);
    const prompt = await screen.findByTestId('forced-task-approval-prompt');
    const comment = within(prompt).getByLabelText('Комментарий для возврата на доработку');
    await waitFor(() => expect((comment as HTMLTextAreaElement).disabled).toBe(false));
    fireEvent.change(comment, { target: { value: 'Сохранить мой комментарий' } });
    fireEvent.change(within(prompt).getByLabelText('Срок доработки'), { target: { value: '2099-01-10T10:30' } });
    const calls = apiMock.getUserNotifications.mock.calls.length;
    const taskReads = apiMock.getTaskJournalItem.mock.calls.length;
    act(() => {
      snoozeForcedTaskActions(8, ['61', getForcedTaskApprovalSnoozeKey(404)], 30);
      window.dispatchEvent(new StorageEvent('storage', { key: getForcedTaskSnoozeStorageKey(8) }));
      window.dispatchEvent(new Event(USER_NOTIFICATIONS_EVENT));
    });
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(calls + expectedForcedNotificationTypes.length));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(taskReads + 1));
    await waitFor(() => expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).disabled).toBe(false));
    expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).value)
      .toBe('Сохранить мой комментарий');
    expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Срок доработки') as HTMLInputElement).value).toBe('2099-01-10T10:30');

    notification = createApprovalRequestNotification({ eventKey: 'task:404:approval-request:702:next', metadataJson: '{"currentApproverId":702}' });
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({
      participants: [{ id: 702, userId: 7, role: 'approver', sortOrder: 0, approvalStatus: 'pending' }],
    }));
    act(() => window.dispatchEvent(new Event(USER_NOTIFICATIONS_EVENT)));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(taskReads + 2));
    await waitFor(() => expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).disabled).toBe(false));
    expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).value).toBe('');
    expect((within(screen.getByTestId('forced-task-approval-prompt')).getByLabelText('Срок доработки') as HTMLInputElement).value).not.toBe('2099-01-10T10:30');
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('discards an old employee load when logout and login occur without a parent key', async () => {
    const previous: Array<() => void> = [];
    apiMock.getUserNotifications.mockImplementation((params) => new Promise<PagedResult<UserNotificationDto>>((resolve) => {
      previous.push(() => resolve(createPage(params.type === 'taskRevisionRequested'
        ? [createNotification({ title: 'Задача возвращена на доработку: Прежний сотрудник' })] : [])));
    }));
    const view = render(<ForcedTaskRevisionModal />);
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
    authState.user = null;
    view.rerender(<ForcedTaskRevisionModal />);
    authState.user = { id: 8 };
    mockNotificationPages([createNotification({ title: 'Задача возвращена на доработку: Текущий сотрудник' })]);
    view.rerender(<ForcedTaskRevisionModal />);
    expect(await screen.findByText('Текущий сотрудник')).toBeTruthy();
    await act(async () => { previous.forEach(finish => finish()); });
    expect(screen.queryByText('Прежний сотрудник')).toBeNull();
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2 + expectedForcedNotificationTypes.length);
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
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
    expect(screen.getByText('28.06.2026, 13:00')).toBeTruthy();
    expect(screen.getAllByText('30.06.2026, 13:00').length).toBeGreaterThan(0);

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

  it('uses the persisted requested deadline instead of stale notification metadata', async () => {
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({
        deadlineChangeRequestId: 901,
        requestedByName: 'Иван Петров',
        reviewerName: 'Анна Смирнова',
        currentDueAt: '2026-06-28T09:00:00Z',
        requestedDueAt: '2026-06-28T09:00:00Z',
        reason: 'Нужно дождаться ответа клиента.',
      }),
    })]);

    render(<ForcedTaskRevisionModal />);

    const dueAtInput = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(303, 901));
    await waitFor(() => expect(dueAtInput.value).toBe('2026-06-30T13:00'));
    expect(screen.getByText('28.06.2026, 13:00')).toBeTruthy();
    expect(screen.getAllByText('30.06.2026, 13:00').length).toBeGreaterThan(0);
  });

  it('closes a former reviewer’s notification when the request was reassigned', async () => {
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it('closes an open former reviewer’s prompt after a reassignment causes a forbidden decision', async () => {
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, reviewerName: 'Прежний постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    }).mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 8, reviewerName: 'Новый постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    });
    apiMock.approveTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('HTTP 403 Forbidden'));
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);

    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    fireEvent.click(screen.getByRole('button', { name: 'Согласовать перенос' }));

    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('keeps an observer’s notification when another reviewer was reassigned', async () => {
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 8 }),
    })]);
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 9, reviewerName: 'Новый постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    });

    render(<ForcedTaskRevisionModal />);
    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/Новый постановщик/)).toBeTruthy());
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it.each(['same reviewer', 'network error'])('keeps the draft after forbidden response when reassignment is unconfirmed: %s', async (caseName) => {
    const original = {
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, reviewerName: 'Постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    };
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce(original);
    if (caseName === 'same reviewer') apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce(original);
    else apiMock.getTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('Нет сети'));
    apiMock.approveTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('HTTP 403 Forbidden'));
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);

    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    fireEvent.change(input, { target: { value: '2026-07-01T10:30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Согласовать перенос' }));
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Недостаточно прав для выполнения действия.')).toBeTruthy();
    expect(input.value).toBe('2026-07-01T10:30');
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('closes an open former reviewer’s prompt after a forbidden rejection', async () => {
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, reviewerName: 'Прежний постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    }).mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 8, reviewerName: 'Новый постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    });
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('HTTP 403 Forbidden'));
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);

    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    fireEvent.change(screen.getByLabelText('Комментарий к решению'), { target: { value: 'Причина отказа' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отказать' }));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('closes an open former reviewer’s prompt when a task change arrives', async () => {
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, reviewerName: 'Прежний постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    }).mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 8, reviewerName: 'Новый постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    });
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);
    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it.each(['same reviewer', 'network error'])('preserves the open draft on realtime refresh: %s', async (caseName) => {
    const original = {
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, reviewerName: 'Постановщик',
      requestedDueAt: '2026-06-30T09:00:00Z', reason: 'Причина', status: 'pending',
    };
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce(original);
    if (caseName === 'same reviewer') apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce(original);
    else apiMock.getTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('Нет сети'));
    mockNotificationPages([], [], [createDeadlineChangeNotification({
      metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }),
    })]);
    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    fireEvent.change(input, { target: { value: '2026-07-01T10:30' } });
    fireEvent.change(screen.getByLabelText('Комментарий к решению'), { target: { value: 'Важный текст' } });
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2));
    expect(input.value).toBe('2026-07-01T10:30');
    expect((screen.getByLabelText('Комментарий к решению') as HTMLTextAreaElement).value).toBe('Важный текст');
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it.each(['reconnected', 'fallback-poll'])('refreshes the open deadline request on %s', async (reason) => {
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, requestedDueAt: '2026-06-30T09:00:00Z', status: 'pending',
    }).mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 8, requestedDueAt: '2026-06-30T09:00:00Z', status: 'pending',
    });
    mockNotificationPages([], [], [createDeadlineChangeNotification({ metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }) })]);
    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    act(() => window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT, { detail: { source: 'realtime', reason, taskJournalItemId: 303 } })));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('coalesces task changes while an open deadline request refresh is running', async () => {
    const original = { id: 901, taskJournalItemId: 303, reviewerUserId: 7, requestedDueAt: '2026-06-30T09:00:00Z', status: 'pending' };
    let finishRefresh: ((value: typeof original) => void) | undefined;
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce(original)
      .mockImplementationOnce(() => new Promise((resolve) => { finishRefresh = resolve; }))
      .mockResolvedValueOnce(original);
    mockNotificationPages([], [], [createDeadlineChangeNotification({ metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }) })]);
    render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    act(() => {
      window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT));
      window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT));
      window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT));
    });
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2));
    act(() => {
      window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT));
      window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT));
    });
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2);
    await act(async () => { finishRefresh?.(original); });
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(3));
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('does not reopen a stale prompt when the initial read finishes after realtime reassignment', async () => {
    let finishInitial: ((value: Record<string, unknown>) => void) | undefined;
    apiMock.getTaskJournalDeadlineChangeRequest.mockImplementationOnce(() => new Promise((resolve) => { finishInitial = resolve; }))
      .mockResolvedValueOnce({ id: 901, taskJournalItemId: 303, reviewerUserId: 8, status: 'pending' });
    mockNotificationPages([], [], [createDeadlineChangeNotification({ metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }) })]);
    render(<ForcedTaskRevisionModal />);
    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await act(async () => { finishInitial?.({ id: 901, reviewerUserId: 7, status: 'pending', requestedDueAt: '2026-06-30T09:00:00Z' }); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledTimes(1);
  });

  it('ignores a deadline refresh that finishes after the window is unmounted', async () => {
    let finishRefresh: ((value: Record<string, unknown>) => void) | undefined;
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValueOnce({
      id: 901, taskJournalItemId: 303, reviewerUserId: 7, requestedDueAt: '2026-06-30T09:00:00Z', status: 'pending',
    }).mockImplementationOnce(() => new Promise((resolve) => { finishRefresh = resolve; }));
    mockNotificationPages([], [], [createDeadlineChangeNotification({ metadataJson: JSON.stringify({ deadlineChangeRequestId: 901, reviewerUserId: 7 }) })]);
    const view = render(<ForcedTaskRevisionModal />);
    const input = await screen.findByLabelText('Новый срок задачи') as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe('2026-06-30T13:00'));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.getTaskJournalDeadlineChangeRequest).toHaveBeenCalledTimes(2));
    view.unmount();
    await act(async () => { finishRefresh?.({ id: 901, reviewerUserId: 8, status: 'pending' }); });
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('closes a stale deadline change notification when the request no longer exists', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    apiMock.getTaskJournalDeadlineChangeRequest.mockRejectedValue(new Error('HTTP 404 Not Found'));
    mockNotificationPages([], [], [createDeadlineChangeNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(consoleError).not.toHaveBeenCalled();

    consoleError.mockRestore();
  });

  it('closes a deadline change notification when the request was already processed', async () => {
    apiMock.getTaskJournalDeadlineChangeRequest.mockResolvedValue({
      id: 901,
      taskJournalItemId: 303,
      requestedByUserId: 7,
      requestedByName: 'Иван Петров',
      reviewerUserId: 8,
      reviewerName: 'Анна Смирнова',
      currentDueAt: '2026-06-28T09:00:00Z',
      requestedDueAt: '2026-06-30T09:00:00Z',
      reason: 'Нужно дождаться ответа клиента.',
      status: 'approved',
      createdAt: '2026-06-27T09:00:00Z',
    });
    mockNotificationPages([], [], [createDeadlineChangeNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-deadline-change-prompt')).toBeTruthy();
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(51));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
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

  it.each(['done', 'approved', 'revision', 'cancelled'] as const)('closes an already resolved approval when task status is %s', async (status) => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status }));
    apiMock.getTaskJournalFiles.mockImplementation(() => new Promise(() => {}));
    render(<ForcedTaskRevisionModal />);
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(61));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it.each(['onApproval', 'overdueApproval'] as const)('retains an active approval while status is %s', async (status) => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status }));
    render(<ForcedTaskRevisionModal />);
    await screen.findByTestId('forced-task-approval-prompt');
    await waitFor(() => expect((screen.getByRole('button', { name: 'Согласовать и завершить' }) as HTMLButtonElement).disabled).toBe(false));
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('waits for attachment loading before allowing an active approval decision', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    let finishFiles: (files: never[]) => void = () => {};
    apiMock.getTaskJournalFiles.mockImplementation(() => new Promise((resolve) => { finishFiles = resolve; }));
    render(<ForcedTaskRevisionModal />);
    await screen.findByText('Иск проверен, правки внесены, документы готовы.');
    const button = screen.getByRole('button', { name: 'Согласовать и завершить' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    await act(async () => { finishFiles([]); });
    await waitFor(() => expect(button.disabled).toBe(false));
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it('closes an unavailable approval task even while its files are still loading', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('HTTP 404 Not Found'));
    apiMock.getTaskJournalFiles.mockImplementation(() => new Promise(() => {}));
    render(<ForcedTaskRevisionModal />);
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(61));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it.each(['HTTP 403 Forbidden', 'HTTP 500 Failed'])('keeps the approval notification when task read fails with %s', async (reason) => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockRejectedValue(new Error(reason));
    render(<ForcedTaskRevisionModal />);
    await screen.findByTestId('forced-task-approval-prompt');
    await screen.findByText(reason.includes('403') ? 'Недостаточно прав для выполнения действия.' : 'Не удалось загрузить задачу для согласования.');
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('preserves an approval draft and disables decisions when a refreshed task fails to load', async () => {
    apiMock.getUserNotifications.mockImplementation(async (params) => createPage(params.type === 'taskApprovalRequested'
      ? [{ ...createApprovalRequestNotification() }] : []));
    render(<ForcedTaskRevisionModal />);
    const comment = await screen.findByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement;
    await waitFor(() => expect(comment.disabled).toBe(false));
    fireEvent.change(comment, { target: { value: 'Несохранённый комментарий' } });
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('HTTP 500 Failed'));
    act(() => window.dispatchEvent(new Event(USER_NOTIFICATIONS_EVENT)));
    await screen.findByText('Не удалось загрузить задачу для согласования.');
    expect((screen.getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).value).toBe('Несохранённый комментарий');
    expect((screen.getByRole('button', { name: 'Согласовать' }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('ignores a late unavailable-task response after the window unmounts', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    let rejectRead: (reason: Error) => void = () => {};
    apiMock.getTaskJournalItem.mockImplementation(() => new Promise((_, reject) => { rejectRead = reject; }));
    const view = render(<ForcedTaskRevisionModal />);
    await screen.findByTestId('forced-task-approval-prompt');
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalled());
    view.unmount();
    await act(async () => { rejectRead(new Error('HTTP 404 Not Found')); });
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('ignores an older initial approval read after a newer realtime read', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    let finishOld: (task: ReturnType<typeof createApprovalTask>) => void = () => {};
    apiMock.getTaskJournalItem.mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve; }));
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
    render(<ForcedTaskRevisionModal />);
    await screen.findByTestId('forced-task-approval-prompt');
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await act(async () => { finishOld(createApprovalTask({ status: 'done' })); });
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(2));
    expect(screen.getByTestId('forced-task-approval-prompt')).toBeTruthy();
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    await waitFor(() => expect((screen.getByRole('button', { name: 'Согласовать и завершить' }) as HTMLButtonElement).disabled).toBe(false));
  });

  it('closes an open approval after a realtime task update confirms it is resolved', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
    render(<ForcedTaskRevisionModal />);
    const comment = await screen.findByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement;
    await waitFor(() => expect(comment.disabled).toBe(false));
    fireEvent.change(comment, { target: { value: 'Мой текст' } });
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'done' }));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(61));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it('disables a resolved open approval until its notification can be closed', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
    render(<ForcedTaskRevisionModal />);
    const button = await screen.findByRole('button', { name: 'Согласовать и завершить' }) as HTMLButtonElement;
    await waitFor(() => expect(button.disabled).toBe(false));
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'done' }));
    apiMock.markUserNotificationRead.mockRejectedValueOnce(new Error('Нет связи'));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await screen.findByText(/Не удалось закрыть уведомление/);
    expect(button.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить закрытие уведомления' }));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
  });

  it('keeps an open approval draft on a failed realtime refresh', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
    render(<ForcedTaskRevisionModal />);
    const comment = await screen.findByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement;
    await waitFor(() => expect(comment.disabled).toBe(false));
    fireEvent.change(comment, { target: { value: 'Мой сохранённый текст' } });
    const reads = apiMock.getTaskJournalItem.mock.calls.length;
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('HTTP 500 Failed'));
    act(() => window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(reads + 1));
    expect((screen.getByLabelText('Комментарий для возврата на доработку') as HTMLTextAreaElement).value).toBe('Мой сохранённый текст');
    expect((screen.getByRole('button', { name: 'Согласовать' }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
  });

  it('retains an unavailable approval when acknowledgement fails', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('HTTP 404 Not Found'));
    apiMock.markUserNotificationRead.mockRejectedValueOnce(new Error('Нет связи'));
    render(<ForcedTaskRevisionModal />);
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(61));
    expect(screen.getByTestId('forced-task-approval-prompt')).toBeTruthy();
    expect(await screen.findByText(/Не удалось закрыть уведомление/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Согласовать' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить закрытие уведомления' }));
    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(apiMock.markUserNotificationRead).toHaveBeenCalledTimes(2);
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

  it('shows the latest attached files in the forced approval prompt', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
    apiMock.getTaskJournalFiles.mockResolvedValue([
      {
        id: 81,
        taskJournalItemId: 404,
        documentKey: 'claim',
        version: 1,
        originalFileName: 'Иск черновик.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        sizeBytes: 1024,
        uploadedByName: 'Мария Юрист',
        uploadedAt: '2026-06-30T09:00:00Z',
        isLatestVersion: false,
      },
      {
        id: 82,
        taskJournalItemId: 404,
        documentKey: 'claim',
        version: 2,
        originalFileName: 'Иск готовый.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        sizeBytes: 2048,
        uploadedByName: 'Мария Юрист',
        uploadedAt: '2026-06-30T10:00:00Z',
        isLatestVersion: true,
      },
    ]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByText('Иск готовый.docx')).toBeTruthy();
    expect(screen.getByText('v2 · 2.0 КБ')).toBeTruthy();
    expect(screen.queryByText('Иск черновик.docx')).toBeNull();
    expect(screen.getByRole('button', { name: 'Скачать файл Иск готовый.docx' })).toBeTruthy();
    expect(apiMock.getTaskJournalFiles).toHaveBeenCalledWith(404, { latestOnly: true });
  });

  it.each(['session', 'scope'].flatMap(owner => ['success', 'error'].map(outcome => ({ owner, outcome }))))('discards an approval attachment late $outcome after $owner changes', async ({ owner, outcome }) => {
    const session = documentCopyTestSession();
    const createUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:owned-attachment');
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const log = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
      apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
      apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 12, documentKey: 'claim', version: 1, originalFileName: 'Иск.docx', sizeBytes: 128, uploadedAt: '2026-06-30T10:00:00Z' }]);
      let finish!: (value: { blob: Blob; fileName: string }) => void;
      let fail!: (error: Error) => void;
      apiMock.downloadTaskJournalFile.mockReturnValue(new Promise<{ blob: Blob; fileName: string }>((resolve, reject) => { finish = resolve; fail = reject; }));
      const view = render(<ForcedTaskRevisionModal />);
      fireEvent.click(await screen.findByRole('button', { name: 'Скачать файл Иск.docx' }));
      expect(apiMock.downloadTaskJournalFile).toHaveBeenCalledWith(404, 12);
      mockNotificationPages();
      if (owner === 'session') session.replace('login');
      else permissionsState.tasks = { canView: true, viewScope: 'own' };
      view.rerender(<ForcedTaskRevisionModal />);
      await act(async () => {
        if (outcome === 'success') finish({ blob: new Blob(['owned']), fileName: 'Иск.docx' });
        else fail(new Error('Ошибка прошлого владельца'));
      });
      expect(createUrl).not.toHaveBeenCalled();
      expect(click).not.toHaveBeenCalled();
      expect(log).not.toHaveBeenCalled();
      expect(screen.queryByText('Ошибка прошлого владельца')).toBeNull();
    } finally { session.restore(); createUrl.mockRestore(); click.mockRestore(); log.mockRestore(); }
  });

  it('downloads the current approval attachment after its owned response', async () => {
    const createUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:owned-attachment');
    const revokeUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    try {
      mockNotificationPages([], [], [], [createApprovalRequestNotification()]);
      apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({ status: 'onApproval' }));
      apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 12, documentKey: 'claim', version: 1, originalFileName: 'Иск.docx', sizeBytes: 128, uploadedAt: '2026-06-30T10:00:00Z' }]);
      apiMock.downloadTaskJournalFile.mockResolvedValue({ blob: new Blob(['owned']), fileName: 'Иск.docx' });
      render(<ForcedTaskRevisionModal />);
      fireEvent.click(await screen.findByRole('button', { name: 'Скачать файл Иск.docx' }));
      await waitFor(() => expect(click).toHaveBeenCalledOnce());
      expect(createUrl).toHaveBeenCalledOnce();
      expect(revokeUrl).toHaveBeenCalledWith('blob:owned-attachment');
    } finally { createUrl.mockRestore(); revokeUrl.mockRestore(); click.mockRestore(); }
  });

  it('temporarily hides a pending approval before opening its task', async () => {
    const onOpenTask = vi.fn();
    mockNotificationPages([], [], [], [createApprovalRequestNotification()]);

    render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);

    const prompt = await screen.findByTestId('forced-task-approval-prompt');
    fireEvent.click(within(prompt).getByRole('button', { name: 'Открыть задачу' }));

    await waitFor(() => expect(screen.queryByTestId('forced-task-approval-prompt')).toBeNull());
    expect(onOpenTask).toHaveBeenCalledWith(404);
    expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!) || '{}') as Record<string, number>;
    expect(snoozes['61']).toBeGreaterThan(Date.now());
    expect(snoozes[getForcedTaskApprovalSnoozeKey(404)]).toBeGreaterThan(Date.now());
  });

  it('uses the current pending approver when the notification participant is stale', async () => {
    mockNotificationPages([], [], [], [createApprovalRequestNotification({
      metadataJson: JSON.stringify({
        actorUserId: 8,
        actorName: 'Мария Юрист',
        currentApproverId: 1643,
      }),
    })]);
    apiMock.getTaskJournalItem.mockResolvedValue(createApprovalTask({
      participants: [
        {
          id: 1699,
          userId: 3,
          role: 'approver',
          displayName: 'Кожеваткин Андрей Владимирович',
          sortOrder: 0,
          approvalStatus: 'pending',
        },
      ],
    }));

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-approval-prompt')).toBeTruthy();
    expect(screen.getByText('Кожеваткин Андрей Владимирович')).toBeTruthy();
    expect(screen.queryByText('Не указан')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать и завершить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      404,
      'approved',
      'Иск проверен, правки внесены, документы готовы.',
      1699,
      'complete',
    ));
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
  it('keeps the mandatory inbox during a network outage and restores reads on the notification event', async () => {
    const errorLog = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const debugLog = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    try {
      mockNotificationPages([createNotification({ id: 91 })]);
      render(<ForcedTaskRevisionModal />);
      expect(await screen.findByRole('dialog')).toBeTruthy();
      apiMock.getUserNotifications.mockRejectedValue(new TypeError('Failed to fetch'));
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      await waitFor(() => expect(debugLog).toHaveBeenCalled());
      expect(errorLog).not.toHaveBeenCalled();
      expect(screen.getByRole('dialog')).toBeTruthy();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
      mockNotificationPages([createNotification({ id: 92, title: 'Задача возвращена на доработку: Новое сообщение' })]);
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      expect(await screen.findByText('Новое сообщение')).toBeTruthy();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally {
      errorLog.mockRestore();
      debugLog.mockRestore();
    }
  });

  it('continues loading mandatory messages after an initial network failure without a false error', async () => {
    const errorLog = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const debugLog = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    try {
      apiMock.getUserNotifications.mockRejectedValue(new TypeError('Failed to fetch'));
      render(<ForcedTaskRevisionModal />);
      await waitFor(() => expect(debugLog).toHaveBeenCalled());
      expect(errorLog).not.toHaveBeenCalled();
      expect(screen.queryByRole('dialog')).toBeNull();
      mockNotificationPages([createNotification({ id: 93 })]);
      fireEvent(window, new CustomEvent(USER_NOTIFICATIONS_EVENT));
      expect(await screen.findByRole('dialog')).toBeTruthy();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally {
      errorLog.mockRestore();
      debugLog.mockRestore();
    }
  });

  it.each(['HTTP 500 Failed to fetch', 'HTTP 403 Forbidden', 'HTTP 400 Failed to fetch', 'HTTP 409 Failed to fetch', 'HTTP 422 Failed to fetch', 'HTTP 501 Failed to fetch', 'HTTP 507 Failed to fetch', 'Invalid notification response'])('keeps a genuine inbox failure visible: %s', async (message) => {
    const errorLog = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const debugLog = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    try {
      apiMock.getUserNotifications.mockRejectedValue(new Error(message));
      render(<ForcedTaskRevisionModal />);
      await waitFor(() => expect(errorLog).toHaveBeenCalledWith('Failed to load forced task action messages.', expect.any(Error)));
      expect(debugLog).not.toHaveBeenCalled();
      expect(apiMock.markUserNotificationRead).not.toHaveBeenCalled();
    } finally {
      errorLog.mockRestore();
      debugLog.mockRestore();
    }
  });

});
