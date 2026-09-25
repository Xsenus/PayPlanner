import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InternalMessenger, TaskChatPanel } from './InternalMessenger';
import { localChatDayKey } from '../../utils/chatDateTime';

const chatApiMock = vi.hoisted(() => ({
  getThreads: vi.fn(),
  getPeople: vi.fn(),
  createChannel: vi.fn(),
  openDirect: vi.fn(),
  openTask: vi.fn(),
  updateTaskGuests: vi.fn(),
  setPersonalState: vi.fn(),
  getMessages: vi.fn(),
  markRead: vi.fn(),
  sendMessage: vi.fn(),
  editMessage: vi.fn(),
  deleteMessage: vi.fn(),
  toggleReaction: vi.fn(),
  setPinned: vi.fn(),
  setNoReplyReminder: vi.fn(),
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
  REALTIME_USER_NOTIFICATION_CREATED_EVENT: 'userNotificationCreated',
  createNotificationsHubConnection: () => connectionMock,
  stopNotificationsHubConnection: (connection: { stop: () => Promise<void> }) => connection.stop(),
}));

const thread = {
  id: 12,
  kind: 'channel' as const,
  name: 'Судебная команда',
  role: 'owner',
  notificationMode: 'all' as const,
  isArchived: false,
  memberUserIds: [7, 8],
  guestUserIds: [],
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
  it('groups messages by the viewer’s local date, not by the UTC date', () => {
    expect(localChatDayKey('2026-09-23T16:30:00Z', 'Asia/Novosibirsk')).toBe('2026-09-23');
    expect(localChatDayKey('2026-09-23T17:30:00Z', 'Asia/Novosibirsk')).toBe('2026-09-24');
  });

  beforeEach(() => {
    sessionStorage.clear();
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
    chatApiMock.setPersonalState.mockResolvedValue(undefined);
    chatApiMock.updateTaskGuests.mockResolvedValue(undefined);
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

  it('shows a newly created conversation when a colleague sends its first message', async () => {
    chatApiMock.getThreads.mockResolvedValueOnce([]).mockResolvedValueOnce([thread]);
    render(<InternalMessenger />);
    expect(await screen.findByText('Чатов здесь пока нет.')).toBeTruthy();

    const refresh = connectionMock.on.mock.calls.find(([event]) => event === 'taskJournalChanged')?.[1];
    expect(refresh).toBeTypeOf('function');
    act(() => { refresh(); });

    expect((await screen.findAllByText('Судебная команда')).length).toBeGreaterThan(0);
    expect(chatApiMock.getThreads.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('does not duplicate a sent message when realtime loads it before the send request returns', async () => {
    const created = { ...message, id: 23, authorUserId: 7, authorName: 'Я', text: 'Готово' };
    let finishSend: (value: typeof created) => void = () => undefined;
    chatApiMock.sendMessage.mockImplementation(() => new Promise((resolve) => { finishSend = resolve; }));
    render(<InternalMessenger />);
    await screen.findByText('документ');

    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Готово' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalled());
    chatApiMock.getMessages.mockResolvedValue({ items: [message, created], hasMore: false });
    const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    await act(async () => { handlers.forEach((handler) => handler()); });
    expect(await within(screen.getByLabelText('Сообщения чата')).findByText('Готово')).toBeTruthy();

    await act(async () => { finishSend(created); });
    expect(within(screen.getByLabelText('Сообщения чата')).getAllByText('Готово')).toHaveLength(1);
  });

  it('lets an author enable and disable a daily no-reply reminder in the shared chat', async () => {
    const ownMessage = { ...message, authorUserId: 7, authorName: 'Я', noReplyReminderEnabled: false };
    chatApiMock.getMessages.mockResolvedValue({ items: [ownMessage], hasMore: false });
    chatApiMock.setNoReplyReminder.mockResolvedValueOnce({ ...ownMessage, noReplyReminderEnabled: true })
      .mockResolvedValueOnce(ownMessage);
    render(<InternalMessenger />);
    await screen.findByText('документ');
    expect(screen.getByRole('button', { name: 'Действия' }).className).not.toContain('hidden');

    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Напомнить, если нет ответа' }));
    await waitFor(() => expect(chatApiMock.setNoReplyReminder).toHaveBeenCalledWith(22, true));
    expect(await screen.findByText('Напомнить, если нет ответа')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отключить напоминание' }));
    await waitFor(() => expect(chatApiMock.setNoReplyReminder).toHaveBeenCalledWith(22, false));
  });

  it('keeps an unfinished message in this tab across a chat remount and clears it after sending', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101 });
    const first = render(<TaskChatPanel taskId={101} />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…');
    fireEvent.change(editor, { target: { value: 'Важный незаконченный текст' } });
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.44')).toBe('Важный незаконченный текст');
    first.unmount();

    render(<TaskChatPanel taskId={101} />);
    expect(await screen.findByDisplayValue('Важный незаконченный текст')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.44')).toBeNull());
  });

  it('keeps the reading position and draft when a realtime message arrives', async () => {
    render(<InternalMessenger />);
    expect(await screen.findByText('документ')).toBeTruthy();
    const conversation = screen.getByLabelText('Сообщения чата');
    let scrollHeight = 1000;
    Object.defineProperty(conversation, 'scrollHeight', { configurable: true, get: () => scrollHeight });
    Object.defineProperty(conversation, 'clientHeight', { configurable: true, get: () => 400 });
    conversation.scrollTop = 300;
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Пишу ответ' } });
    scrollHeight = 1200;
    chatApiMock.getMessages.mockResolvedValue({ items: [message, { ...message, id: 23, text: 'Новое сообщение' }], hasMore: false });
    const realtimeHandlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    expect(realtimeHandlers.length).toBeGreaterThanOrEqual(2);
    await act(async () => { realtimeHandlers.forEach((handler) => handler()); });
    expect(await screen.findByText('Новое сообщение')).toBeTruthy();
    expect(conversation.scrollTop).toBe(300);
    expect(screen.getByDisplayValue('Пишу ответ')).toBeTruthy();
  });

  it('copies the source message into task description and lets the author enter a short title', async () => {
    chatApiMock.createTask.mockResolvedValue({ ...message, id: 24, kind: 'taskLink', text: 'Создана задача' });
    render(<InternalMessenger />);
    expect(await screen.findByText('документ')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Создать задачу' }));
    expect((screen.getByLabelText('Название') as HTMLInputElement).value).toBe('');
    expect((screen.getByLabelText('Описание') as HTMLTextAreaElement).value).toBe(message.text);
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'Проверить документ' } });
    fireEvent.change(screen.getByLabelText('Срок'), { target: { value: '2027-01-01T11:00' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать задачу' }));
    await waitFor(() => expect(chatApiMock.createTask).toHaveBeenCalledWith(22, expect.objectContaining({
      title: 'Проверить документ', description: message.text,
    })));
  });

  it('hides task creation from messages when the user cannot create tasks', async () => {
    permissionState.canCreateTasks = false;
    render(<InternalMessenger />);

    expect(await screen.findByText('документ')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    expect(screen.queryByRole('button', { name: 'Создать задачу' })).toBeNull();
  });

  it('opens the task chat through the same protected API', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101', lastReadMessageId: 20, firstUnreadMessageId: 22 });
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false, nextBeforeId: null });

    render(<TaskChatPanel taskId={101} />);

    expect(await screen.findByText('Задача №101')).toBeTruthy();
    expect(chatApiMock.openTask).toHaveBeenCalledWith(101);
    await waitFor(() => expect(chatApiMock.getMessages).toHaveBeenCalledWith(44, expect.objectContaining({ afterId: 21 })));
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

  it('links task references and the task chat title to the task card', async () => {
    const onOpenTask = vi.fn();
    chatApiMock.getThreads.mockResolvedValue([{ ...thread, kind: 'task', taskJournalItemId: 101, name: 'Сверка по делу' }]);
    chatApiMock.getMessages.mockResolvedValue({ items: [{ ...message, text: 'Проверьте задачу №102 и #103' }], hasMore: false });
    render(<InternalMessenger onOpenTask={onOpenTask} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' }));
    fireEvent.click(screen.getByRole('button', { name: '№102' }));
    fireEvent.click(screen.getByRole('button', { name: '#103' }));
    expect(onOpenTask.mock.calls).toEqual([[101], [102], [103]]);
  });

  it('shows unread markers by chat type and archives without deleting the colleague conversation', async () => {
    chatApiMock.getThreads.mockResolvedValue([
      thread,
      { ...thread, id: 13, kind: 'task', name: 'Задача №13', unreadCount: 2 },
      { ...thread, id: 14, kind: 'direct', name: 'Личный диалог', isArchived: true, unreadCount: 3 },
    ]);
    render(<InternalMessenger />);

    expect(await screen.findByRole('button', { name: 'Задачи2 непрочитанных' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Архив3 непрочитанных' }));
    expect(screen.getByText('Личный диалог')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Все3 непрочитанных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Действия с чатом Судебная команда' }));
    fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    await waitFor(() => expect(chatApiMock.setPersonalState).toHaveBeenCalledWith(12, 'archived'));
  });

  it('allows the task owner to invite a chat guest without changing task participants', async () => {
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    chatApiMock.getPeople.mockResolvedValue([
      { id: 7, name: 'Постановщик', email: 'author@example.test' },
      { id: 8, name: 'Участник задачи', email: 'member@example.test' },
      { id: 9, name: 'Бухгалтер', email: 'accountant@example.test' },
    ]);
    render(<TaskChatPanel taskId={101} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Участники чата' }));
    const memberCheckbox = screen.getByRole('checkbox', { name: /Участник задачи/ });
    expect((memberCheckbox as HTMLInputElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('checkbox', { name: /Бухгалтер/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить участников' }));
    await waitFor(() => expect(chatApiMock.updateTaskGuests).toHaveBeenCalledWith(44, [9]));
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
    await waitFor(() => expect(chatApiMock.getMessages).toHaveBeenCalledWith(44, expect.not.objectContaining({ search: 'документ' })));
    expect(chatApiMock.markRead).not.toHaveBeenCalled();
    expect(screen.queryByPlaceholderText('Текст сообщения')).toBeNull();
  });

  it('opens a long unread conversation at the first unread page without marking the latest page read', async () => {
    chatApiMock.getThreads.mockResolvedValue([{ ...thread, lastReadMessageId: 4, firstUnreadMessageId: 5, unreadCount: 56 }]);
    chatApiMock.getMessages.mockResolvedValueOnce({ items: [{ ...message, id: 5, text: 'Первое непрочитанное' }], hasMore: true, hasNewer: true });
    render(<InternalMessenger />);

    expect(await screen.findByText('Первое непрочитанное')).toBeTruthy();
    expect(chatApiMock.getMessages).toHaveBeenCalledWith(12, expect.objectContaining({ afterId: 4 }));
    expect(await screen.findByRole('button', { name: 'Показать более новые' })).toBeTruthy();
    expect(chatApiMock.markRead).not.toHaveBeenCalled();
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Ответ из середины истории' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12, 'Ответ из середины истории', [], undefined));
    expect(screen.queryByText('Готово')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Показать более новые' }));
    await waitFor(() => expect(chatApiMock.getMessages).toHaveBeenCalledWith(12, expect.objectContaining({ afterId: 5 })));
  });

  it('offers a jump to the latest messages while reading earlier ones', async () => {
    render(<InternalMessenger />);
    expect(await screen.findByText('документ')).toBeTruthy();
    const conversation = screen.getByLabelText('Сообщения чата');
    Object.defineProperty(conversation, 'scrollHeight', { configurable: true, get: () => 1000 });
    Object.defineProperty(conversation, 'clientHeight', { configurable: true, get: () => 300 });
    conversation.scrollTop = 100;
    fireEvent.scroll(conversation);
    fireEvent.click(screen.getByRole('button', { name: 'К последним сообщениям' }));
    expect(conversation.scrollTop).toBe(1000);
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
