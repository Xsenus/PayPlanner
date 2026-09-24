import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InternalMessenger, TaskChatPanel } from './InternalMessenger';

const chatApiMock = vi.hoisted(() => ({
  getThreads: vi.fn(),
  getPeople: vi.fn(),
  createChannel: vi.fn(),
  openDirect: vi.fn(),
  openTask: vi.fn(),
  getMessages: vi.fn(),
  markRead: vi.fn(),
  sendMessage: vi.fn(),
  editMessage: vi.fn(),
  deleteMessage: vi.fn(),
  toggleReaction: vi.fn(),
  setPinned: vi.fn(),
  setNotificationMode: vi.fn(),
  createTask: vi.fn(),
  downloadAttachment: vi.fn(),
  getAttachmentBlob: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }));
const permissionState = vi.hoisted(() => ({ canCreateTasks: true }));
const connectionMock = vi.hoisted(() => ({ on: vi.fn(), start: vi.fn(() => Promise.resolve()), stop: vi.fn(() => Promise.resolve()) }));

vi.mock('../../services/internalChatApi', async () => {
  const actual = await vi.importActual<typeof import('../../services/internalChatApi')>('../../services/internalChatApi');
  return { ...actual, internalChatApi: chatApiMock };
});
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, email: 'me@example.test', fullName: 'Я' }, isAdmin: () => false }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ tasks: { canCreate: permissionState.canCreateTasks } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => toastMock }));
vi.mock('../../services/realtimeNotifications', () => ({
  REALTIME_TASK_JOURNAL_CHANGED_EVENT: 'taskJournalChanged',
  createNotificationsHubConnection: () => connectionMock,
  stopNotificationsHubConnection: (connection: { stop: () => Promise<void> }) => connection.stop(),
}));

const thread = {
  id: 12,
  kind: 'channel' as const,
  name: 'Судебная команда',
  role: 'owner',
  notificationMode: 'all' as const,
  memberUserIds: [7, 8],
  unreadCount: 1,
  lastMessagePreview: 'Проверьте документ',
  lastMessageAt: '2026-09-21T10:01:00Z',
  createdAt: '2026-09-21T10:00:00Z',
};

const message = {
  id: 22,
  threadId: 12,
  authorUserId: 8,
  authorName: 'Анна',
  kind: 'user' as const,
  text: 'Проверьте **документ**',
  isPinned: false,
  createdAt: '2026-09-21T10:01:00Z',
  attachments: [],
  reactions: [],
  mentionUserIds: [],
};

describe('InternalMessenger', () => {
  beforeEach(() => {
    permissionState.canCreateTasks = true;
    Object.values(chatApiMock).forEach((mock) => mock.mockReset());
    toastMock.error.mockReset();
    toastMock.success.mockReset();
    connectionMock.on.mockReset();
    connectionMock.start.mockReset().mockResolvedValue(undefined);
    connectionMock.stop.mockReset().mockResolvedValue(undefined);
    chatApiMock.getThreads.mockResolvedValue([thread]);
    chatApiMock.getPeople.mockResolvedValue([{ id: 8, name: 'Анна', email: 'anna@example.test' }]);
    chatApiMock.getMessages.mockResolvedValue({ items: [message], hasMore: false, nextBeforeId: 22 });
    chatApiMock.markRead.mockResolvedValue(undefined);
    chatApiMock.sendMessage.mockResolvedValue({ ...message, id: 23, authorUserId: 7, authorName: 'Я', text: 'Готово' });
  });

  it('shows threads, messages and sends a new message', async () => {
    render(<InternalMessenger />);

    expect((await screen.findAllByText('Судебная команда')).length).toBeGreaterThan(0);
    expect(await screen.findByText('документ')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Готово' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12, 'Готово', [], undefined));
    expect(await screen.findByText('Готово')).toBeTruthy();
  });

  it('hides task creation from messages when the user cannot create tasks', async () => {
    permissionState.canCreateTasks = false;
    render(<InternalMessenger />);

    expect(await screen.findByText('документ')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    expect(screen.queryByRole('button', { name: 'Создать задачу' })).toBeNull();
  });

  it('opens the task chat through the same protected API', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false, nextBeforeId: null });

    render(<TaskChatPanel taskId={101} />);

    expect(await screen.findByText('Задача №101')).toBeTruthy();
    expect(chatApiMock.openTask).toHaveBeenCalledWith(101);
  });

  it('shows task chat participants and resolves stored mention IDs to names', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    chatApiMock.getMessages.mockResolvedValue({
      items: [{ ...message, threadId: 44, text: 'Посмотрите @[8] и @8, пожалуйста', mentionUserIds: [8] }],
      hasMore: false,
      nextBeforeId: 22,
    });

    render(<TaskChatPanel taskId={101} />);

    expect(await screen.findByText('Чат задачи · Участники: Анна')).toBeTruthy();
    expect((await screen.findAllByText('@Анна')).length).toBe(2);
    expect(screen.queryByText('@[8]')).toBeNull();
  });

  it('does not mark filtered search results as read and restores the conversation on closing search', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    render(<TaskChatPanel taskId={101} />);

    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledWith(44, 22));
    chatApiMock.markRead.mockClear();
    chatApiMock.getMessages.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Поиск' }));
    fireEvent.change(screen.getByPlaceholderText('Текст сообщения'), { target: { value: 'документ' } });
    expect(chatApiMock.getMessages).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
    await waitFor(() => expect(chatApiMock.getMessages).toHaveBeenCalledWith(44, expect.objectContaining({ search: 'документ' })));
    expect(chatApiMock.markRead).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Поиск' }));
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledWith(44, 22));
    expect(screen.queryByPlaceholderText('Текст сообщения')).toBeNull();
  });

  it('ignores an older conversation response after a search result arrives', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    let resolveInitial: (value: unknown) => void = () => undefined;
    chatApiMock.getMessages.mockImplementationOnce(() => new Promise((resolve) => { resolveInitial = resolve; }));
    chatApiMock.getMessages.mockResolvedValue({
      items: [{ ...message, id: 23, text: 'Найдено' }], hasMore: false, nextBeforeId: 23,
    });
    render(<TaskChatPanel taskId={101} />);

    await waitFor(() => expect(chatApiMock.getMessages).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'Поиск' }));
    fireEvent.change(screen.getByPlaceholderText('Текст сообщения'), { target: { value: 'Найдено' } });
    fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
    expect(await screen.findByText('Найдено')).toBeTruthy();
    await act(async () => { resolveInitial({ items: [{ ...message, text: 'Старое' }], hasMore: false, nextBeforeId: 22 }); });
    expect(screen.queryByText('Старое')).toBeNull();
    expect(chatApiMock.markRead).not.toHaveBeenCalled();
  });
});
