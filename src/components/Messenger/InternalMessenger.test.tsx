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
  getHistoryVisibility: vi.fn(),
  setHistoryVisibility: vi.fn(),
  getMessages: vi.fn(),
  markRead: vi.fn(),
  sendMessage: vi.fn(),
  getPendingSends: vi.fn(),
  getSendStatus: vi.fn(),
  resumeSend: vi.fn(),
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
const authState = vi.hoisted(() => ({ userId: 7 as number | null }));
const connectionMock = vi.hoisted(() => ({ on: vi.fn(), start: vi.fn(() => Promise.resolve()), stop: vi.fn(() => Promise.resolve()) }));

vi.mock('../../services/internalChatApi', async () => {
  const actual = await vi.importActual<typeof import('../../services/internalChatApi')>('../../services/internalChatApi');
  return { ...actual, internalChatApi: chatApiMock };
});
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: authState.userId === null ? null : { id: authState.userId, email: 'me@example.test', fullName: 'Я' }, isAdmin: () => false }) }));
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
    authState.userId = 7;
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
    chatApiMock.getHistoryVisibility.mockResolvedValue({ hiddenThroughMessageId: null, revision: 0, latestMessageId: 22 });
    chatApiMock.updateTaskGuests.mockResolvedValue(undefined);
    chatApiMock.sendMessage.mockResolvedValue({ ...message, id: 23, authorUserId: 7, authorName: 'Я', text: 'Готово' });
    chatApiMock.getPendingSends.mockResolvedValue({ status: 'pending', items: [] });
    chatApiMock.getSendStatus.mockRejectedValue(new Error('Отправка не найдена.'));
  });

  it.each(['confirmed', 'wrong-key', 'legacy'] as const)('keeps lost-response identity only after owned status confirmation: %s', async (outcome) => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Сохранённый ответ');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key, text: 'Сохранённый ответ',
      replyToId: null, files: [{ name: 'proof.txt', size: 8, lastModified: 1 }] }));
    if (outcome === 'legacy') chatApiMock.getSendStatus.mockRejectedValue(new Error('Отправка не найдена.'));
    else chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: outcome === 'confirmed' ? key.toUpperCase()
      : '6edb5c44-84dc-41aa-bde8-e14bdc1c7238', status: 'needsInput', automaticRecovery: false,
      description: 'Нужны исходные файлы.' });
    const { container } = render(<InternalMessenger />);
    await screen.findByText('документ');
    await act(async () => { await Promise.resolve(); });
    const stored = JSON.parse(sessionStorage.getItem('payplanner.chatSend.v1.7.12')!);
    expect(stored.managed).toBe(outcome === 'confirmed' ? true : undefined);
    fireEvent.change(container.querySelector('input[type=file]')!, { target: { files: [new File(['original'], 'proof.txt', { lastModified: 2 })] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1));
    if (outcome === 'confirmed') expect(chatApiMock.sendMessage.mock.calls[0][5]).toBe(key);
    else expect(chatApiMock.sendMessage.mock.calls[0][5]).not.toBe(key);
  });

  it('does not promote another pending key when an old status response arrives', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    const replacement = '6edb5c44-84dc-41aa-bde8-e14bdc1c7238';
    const pending = { id: key, text: 'Сохранённый ответ', replyToId: null, files: [] };
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify(pending));
    let finish: (value: unknown) => void = () => undefined;
    chatApiMock.getSendStatus.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<InternalMessenger />);
    await waitFor(() => expect(chatApiMock.getSendStatus).toHaveBeenCalled());
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ ...pending, id: replacement }));
    await act(async () => { finish({ clientRequestId: key, status: 'paused', automaticRecovery: false, description: 'Ожидание.' }); });
    expect(JSON.parse(sessionStorage.getItem('payplanner.chatSend.v1.7.12')!)).toEqual({ ...pending, id: replacement });
  });

  it('confirms a lost pending response before files are reselected in the same open chat', async () => {
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    chatApiMock.getSendStatus.mockImplementation(async (_thread, key) => ({ clientRequestId: key, status: 'needsInput',
      automaticRecovery: false, description: 'Нужны исходные файлы.' }));
    const { container } = render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Сохранённый ответ' } });
    const input = container.querySelector('input[type=file]')!;
    fireEvent.change(input, { target: { files: [new File(['original'], 'proof.txt', { lastModified: 1 })] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    const key = chatApiMock.sendMessage.mock.calls[0][5];
    await screen.findByText('Нужны исходные файлы.');
    fireEvent.change(input, { target: { files: [new File(['original'], 'proof.txt', { lastModified: 2 })] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(2));
    expect(chatApiMock.sendMessage.mock.calls[1][5]).toBe(key);
  });

  it('acknowledges a lost published response in the same open chat without another send', async () => {
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    chatApiMock.getSendStatus.mockImplementation(async (_thread, key) => ({ clientRequestId: key, status: 'published',
      automaticRecovery: false, description: 'Сообщение отправлено.',
      message: { ...message, id: 23, authorUserId: 7, text: 'Сохранённый ответ' } }));
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Сохранённый ответ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.getSendStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull());
    expect((screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement).value).toBe('');
    expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.resumeSend).not.toHaveBeenCalled();
  });

  it('keeps a newer draft when a failed send is acknowledged by a late status read', async () => {
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    let finish: (value: unknown) => void = () => undefined;
    chatApiMock.getSendStatus.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Сохранённый ответ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.getSendStatus).toHaveBeenCalledTimes(1));
    const key = chatApiMock.sendMessage.mock.calls[0][5];
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Новый черновик' } });
    await act(async () => { finish({ clientRequestId: key, status: 'published', automaticRecovery: false,
      description: 'Сообщение отправлено.', message: { ...message, id: 23, authorUserId: 7, text: 'Сохранённый ответ' } }); });
    expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull();
    expect((screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement).value).toBe('Новый черновик');
    expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.resumeSend).not.toHaveBeenCalled();
  });

  it('keeps confirmed managed identity in memory when updating the tab store fails', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Сохранённый ответ');
    const pending = { id: key, text: 'Сохранённый ответ', replyToId: null,
      files: [{ name: 'proof.txt', size: 8, lastModified: 1 }] };
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify(pending));
    chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: key, status: 'needsInput', automaticRecovery: false,
      description: 'Нужны исходные файлы.' });
    const originalSet = Storage.prototype.setItem;
    const failure = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name: string, value: string) {
      if (name === 'payplanner.chatSend.v1.7.12') throw new DOMException('quota', 'QuotaExceededError');
      originalSet.call(this, name, value);
    });
    try {
      const { container } = render(<InternalMessenger />);
      await screen.findByText('Нужны исходные файлы.');
      expect(JSON.parse(sessionStorage.getItem('payplanner.chatSend.v1.7.12')!)).toEqual(pending);
      fireEvent.change(container.querySelector('input[type=file]')!, { target: { files: [new File(['original'], 'proof.txt', { lastModified: 2 })] } });
      fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
      await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1));
      expect(chatApiMock.sendMessage.mock.calls[0][5]).toBe(key);
    } finally { failure.mockRestore(); }
  });

  it('shows threads, messages and sends a new message', async () => {
    render(<InternalMessenger />);

    expect((await screen.findAllByText('Судебная команда')).length).toBeGreaterThan(0);
    expect(await screen.findByText('документ')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Готово' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12, 'Готово', [], null, [], expect.any(String)));
    expect(await screen.findByText('Готово')).toBeTruthy();
  });

  it.each(['messenger', 'task'] as const)('hides and restores only personal history while preserving the draft: %s', async (host) => {
    const threadId = host === 'task' ? 44 : thread.id;
    if (host === 'task') chatApiMock.openTask.mockResolvedValue({ ...thread, id: threadId, kind: 'task', taskJournalItemId: 101 });
    let hidden: number | null = null;
    let revision = 0;
    chatApiMock.getMessages.mockImplementation(async () => ({ items: hidden === null ? [message] : [], hasMore: false,
      historyHiddenThroughMessageId: hidden, historyVisibilityRevision: revision }));
    chatApiMock.getHistoryVisibility.mockImplementation(async () => ({ hiddenThroughMessageId: hidden, revision, latestMessageId: 22 }));
    chatApiMock.setHistoryVisibility.mockImplementation(async (_id: number, cutoff: number | null, expected: number) => {
      expect(expected).toBe(revision); hidden = cutoff; revision += 1;
      return { hiddenThroughMessageId: hidden, revision, latestMessageId: 22 };
    });
    render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…');
    fireEvent.change(editor, { target: { value: 'Незавершённый ответ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть историю у себя' }));
    expect(await screen.findByText(/Другие участники сохранят доступ/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть у меня' }));
    expect(await screen.findByText('История скрыта только у вас. Её можно восстановить.')).toBeTruthy();
    expect((editor as HTMLTextAreaElement).value).toBe('Незавершённый ответ');
    expect(chatApiMock.setHistoryVisibility).toHaveBeenCalledWith(threadId, 22, 0);
    fireEvent.click(screen.getByRole('button', { name: 'Восстановить историю' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Восстановить' }));
    await waitFor(() => expect(chatApiMock.setHistoryVisibility).toHaveBeenCalledWith(threadId, null, 1));
    expect((editor as HTMLTextAreaElement).value).toBe('Незавершённый ответ');
  });

  it('retries an uncertain history action with the original watermark and revision', async () => {
    chatApiMock.setHistoryVisibility.mockRejectedValueOnce(new Error('Ответ потерян'))
      .mockResolvedValueOnce({ hiddenThroughMessageId: 22, revision: 1, latestMessageId: 23 });
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть историю у себя' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Скрыть у меня' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Повторить подтверждение' }));
    await waitFor(() => expect(chatApiMock.setHistoryVisibility).toHaveBeenCalledTimes(2));
    expect(chatApiMock.setHistoryVisibility).toHaveBeenNthCalledWith(1, 12, 22, 0);
    expect(chatApiMock.setHistoryVisibility).toHaveBeenNthCalledWith(2, 12, 22, 0);
    expect(chatApiMock.getHistoryVisibility).toHaveBeenCalledTimes(1);
  });

  it('reconciles an uncertain history action without issuing another write', async () => {
    chatApiMock.getHistoryVisibility.mockResolvedValueOnce({ hiddenThroughMessageId: null, revision: 0, latestMessageId: 22 })
      .mockResolvedValueOnce({ hiddenThroughMessageId: 22, revision: 1, latestMessageId: 23 });
    chatApiMock.setHistoryVisibility.mockRejectedValueOnce(new Error('Ответ потерян'));
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть историю у себя' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Скрыть у меня' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Проверить состояние' }));
    expect(await screen.findByText('История скрыта только у вас. Её можно восстановить.')).toBeTruthy();
    expect(chatApiMock.setHistoryVisibility).toHaveBeenCalledTimes(1);
    expect(chatApiMock.getHistoryVisibility).toHaveBeenCalledTimes(2);
  });

  it('drops previously visible messages after another tab hides history and keeps the draft', async () => {
    chatApiMock.getMessages.mockResolvedValueOnce({ items: [message], hasMore: false,
      historyHiddenThroughMessageId: null, historyVisibilityRevision: 0 });
    render(<InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…');
    await screen.findByText('документ');
    fireEvent.change(editor, { target: { value: 'Черновик остаётся' } });
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false,
      historyHiddenThroughMessageId: 22, historyVisibilityRevision: 1 });
    const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    await act(async () => { handlers.forEach((handler) => handler()); });
    expect(await screen.findByText('История скрыта только у вас. Её можно восстановить.')).toBeTruthy();
    expect(within(screen.getByLabelText('Сообщения чата')).queryByText('документ')).toBeNull();
    expect((editor as HTMLTextAreaElement).value).toBe('Черновик остаётся');
  });

  it('does not replace newer history state with a delayed action preparation response', async () => {
    chatApiMock.getMessages.mockResolvedValueOnce({ items: [message], hasMore: false,
      historyHiddenThroughMessageId: null, historyVisibilityRevision: 0 });
    let finishOldState: (state: { hiddenThroughMessageId: number | null; revision: number; latestMessageId: number }) => void = () => undefined;
    chatApiMock.getHistoryVisibility.mockImplementationOnce(() => new Promise((resolve) => { finishOldState = resolve; }));
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть историю у себя' }));
    await waitFor(() => expect(chatApiMock.getHistoryVisibility).toHaveBeenCalledTimes(1));
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false,
      historyHiddenThroughMessageId: 22, historyVisibilityRevision: 1 });
    const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    await act(async () => { handlers.forEach((handler) => handler()); });
    await screen.findByText('История скрыта только у вас. Её можно восстановить.');
    await act(async () => { finishOldState({ hiddenThroughMessageId: null, revision: 0, latestMessageId: 22 }); });
    expect(screen.queryByRole('button', { name: 'Скрыть у меня' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Восстановить историю' })).toBeTruthy();
    expect(toastMock.error).toHaveBeenCalledWith('История уже изменилась. Повторите проверку.');
    expect(chatApiMock.setHistoryVisibility).not.toHaveBeenCalled();
  });

  it('ignores a delayed history action after switching away and reopening the same chat', async () => {
    const second = { ...thread, id: 44, name: 'Второй канал', lastMessagePreview: 'Второе сообщение' };
    chatApiMock.getThreads.mockResolvedValue([thread, second]);
    chatApiMock.getMessages.mockImplementation(async (id: number) => ({
      items: [id === 44 ? { ...message, id: 44, threadId: 44, text: 'Второе сообщение' } : message],
      hasMore: false, historyHiddenThroughMessageId: null, historyVisibilityRevision: 0,
    }));
    let finishOldState: (state: { hiddenThroughMessageId: number | null; revision: number; latestMessageId: number }) => void = () => undefined;
    chatApiMock.getHistoryVisibility.mockImplementationOnce(() => new Promise((resolve) => { finishOldState = resolve; }));
    render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть историю у себя' }));
    await waitFor(() => expect(chatApiMock.getHistoryVisibility).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: /^Второй канал/ }));
    await within(screen.getByLabelText('Сообщения чата')).findByText('Второе сообщение');
    fireEvent.click(screen.getByRole('button', { name: /^Судебная команда/ }));
    await screen.findByText('документ');
    await act(async () => { finishOldState({ hiddenThroughMessageId: null, revision: 0, latestMessageId: 22 }); });
    expect(screen.queryByRole('button', { name: 'Скрыть у меня' })).toBeNull();
    expect(chatApiMock.setHistoryVisibility).not.toHaveBeenCalled();
  });

  it('preserves an unsaved edit and the separate draft when another tab hides the edited message', async () => {
    const mine = { ...message, authorUserId: 7, authorName: 'Я', text: 'Моё сообщение' };
    chatApiMock.getMessages.mockResolvedValueOnce({ items: [mine], hasMore: false,
      historyHiddenThroughMessageId: null, historyVisibilityRevision: 0 });
    render(<InternalMessenger />);
    await screen.findByText(mine.text);
    const editor = screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    fireEvent.change(editor, { target: { value: 'Обычный черновик' } });
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    fireEvent.change(editor, { target: { value: 'Несохранённая правка' } });
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false,
      historyHiddenThroughMessageId: 22, historyVisibilityRevision: 1 });
    const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    await act(async () => { handlers.forEach((handler) => handler()); });
    expect(await screen.findByText(/Редактируемое сообщение скрыто/)).toBeTruthy();
    expect(editor.value).toBe('Несохранённая правка');
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe('Обычный черновик');
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    expect(chatApiMock.editMessage).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Восстановить историю' })).not.toHaveProperty('disabled', true);
  });

  it('preserves an unresolved reply send identity when history changes in another tab', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Ответ, ожидающий подтверждения');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key,
      text: 'Ответ, ожидающий подтверждения', replyToId: 22, files: [] }));
    chatApiMock.getMessages.mockResolvedValueOnce({ items: [message], hasMore: false,
      historyHiddenThroughMessageId: null, historyVisibilityRevision: 0 });
    render(<InternalMessenger />);
    await screen.findByDisplayValue('Ответ, ожидающий подтверждения');
    chatApiMock.getMessages.mockResolvedValue({ items: [], hasMore: false,
      historyHiddenThroughMessageId: 22, historyVisibilityRevision: 1 });
    const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
    await act(async () => { handlers.forEach((handler) => handler()); });
    await screen.findByText('История скрыта только у вас. Её можно восстановить.');
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12,
      'Ответ, ожидающий подтверждения', [], 22, [], key));
  });

  it.each(['messenger', 'task'] as const)('keeps IME confirmation in the draft and sends only ordinary Enter: %s', async (host) => {
    const threadId = host === 'task' ? 44 : thread.id;
    if (host === 'task') chatApiMock.openTask.mockResolvedValue({ ...thread, id: threadId, kind: 'task', taskJournalItemId: 101 });
    render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…');
    const draft = 'Многострочный черновик\n入力中';
    fireEvent.change(editor, { target: { value: draft } });
    fireEvent.compositionStart(editor);
    fireEvent.keyDown(editor, { key: 'Enter', code: 'Enter', isComposing: false });
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    expect(chatApiMock.editMessage).not.toHaveBeenCalled();
    expect((editor as HTMLTextAreaElement).value).toBe(draft);
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);

    fireEvent.compositionEnd(editor);
    fireEvent.keyDown(editor, { key: 'Enter', code: 'Enter', isComposing: true });
    fireEvent.keyDown(editor, { key: 'Enter', code: 'Enter', shiftKey: true });
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    expect((editor as HTMLTextAreaElement).value).toBe(draft);
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);

    fireEvent.keyDown(editor, { key: 'Enter', code: 'Enter' });
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(threadId, draft, [], null, [], expect.any(String)));
    expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1);
    await waitFor(() => expect((editor as HTMLTextAreaElement).value).toBe(''));
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBeNull();
  });

  it('does not save a composing message edit or replace the separate chat draft', async () => {
    const mine = { ...message, authorUserId: 7, authorName: 'Я', text: 'Моё сообщение' };
    chatApiMock.getMessages.mockResolvedValue({ items: [mine], hasMore: false });
    chatApiMock.editMessage.mockImplementation(async (_id: number, text: string) => ({ ...mine, text }));
    render(<InternalMessenger />);
    await screen.findByText(mine.text);
    const editor = screen.getByPlaceholderText('Напишите сообщение…');
    const draft = 'Отдельный черновик\nНе отправлять';
    fireEvent.change(editor, { target: { value: draft } });
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    const edited = 'Исправленное сообщение\n入力中';
    fireEvent.change(editor, { target: { value: edited } });
    fireEvent.compositionStart(editor);
    fireEvent.keyDown(editor, { key: 'Enter', isComposing: true });
    expect(chatApiMock.editMessage).not.toHaveBeenCalled();
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    expect((editor as HTMLTextAreaElement).value).toBe(edited);
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe(draft);

    fireEvent.compositionEnd(editor);
    fireEvent.keyDown(editor, { key: 'Enter' });
    await waitFor(() => expect(chatApiMock.editMessage).toHaveBeenCalledWith(mine.id, edited));
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    await waitFor(() => expect((editor as HTMLTextAreaElement).value).toBe(draft));
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe(draft);
  });

  const renderOwnMessageEdit = async (host: 'messenger' | 'task' = 'messenger', withSecondMessage = false) => {
    const threadId = host === 'task' ? 44 : thread.id;
    const mine = { ...message, threadId, authorUserId: 7, authorName: 'Я', text: 'Моё редактируемое сообщение' };
    const second = { ...mine, id: mine.id + 1, text: 'Моё второе сообщение' };
    chatApiMock.getMessages.mockResolvedValue({ items: withSecondMessage ? [mine, second] : [mine], hasMore: false });
    if (host === 'task') chatApiMock.openTask.mockResolvedValue({ ...thread, id: threadId, kind: 'task', taskJournalItemId: 101 });
    const view = render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    await screen.findByText(mine.text);
    const editor = screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    const draft = 'Отдельный черновик перед правкой';
    fireEvent.change(editor, { target: { value: draft } });
    fireEvent.click(within(screen.getByText(mine.text).closest('article')!).getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    return { view, editor, mine, second, draft, threadId };
  };

  it.each(['messenger', 'task'] as const)('preserves the original draft when switching edit targets during a pending save: %s', async (host) => {
    const { editor, mine, second, draft, threadId } = await renderOwnMessageEdit(host, true);
    let finish: (value: typeof mine) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(() => new Promise<typeof mine>((resolve) => { finish = resolve; }));
    fireEvent.change(editor, { target: { value: 'Правка первого сообщения' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    fireEvent.click(within(screen.getByText(second.text).closest('article')!).getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    expect(editor.value).toBe(second.text);
    fireEvent.change(editor, { target: { value: 'Продолжение правки второго сообщения' } });
    await act(async () => { finish({ ...mine, text: 'Правка первого сообщения' }); });
    expect(editor.value).toBe('Продолжение правки второго сообщения');
    fireEvent.click(screen.getByRole('button', { name: 'Отменить ответ или редактирование' }));
    expect(editor.value).toBe(draft);
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);
    expect(chatApiMock.editMessage).toHaveBeenCalledExactlyOnceWith(mine.id, 'Правка первого сообщения');
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it.each(['messenger', 'task'] as const)('keeps later edit text and ignores repeated Enter while the edit is saving: %s', async (host) => {
    const { editor, mine, draft, threadId } = await renderOwnMessageEdit(host);
    let finish: (value: typeof mine) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(async (_id: number, text: string) => ({ ...mine, text }));
    chatApiMock.editMessage.mockImplementationOnce(() => new Promise<typeof mine>((resolve) => { finish = resolve; }));
    fireEvent.change(editor, { target: { value: 'Первая правка' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    fireEvent.keyDown(editor, { key: 'Enter' });
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.editMessage).toHaveBeenCalledWith(mine.id, 'Первая правка');
    fireEvent.change(editor, { target: { value: 'Более новая правка' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    await act(async () => { finish({ ...mine, text: 'Первая правка' }); });
    expect(screen.getByText('Первая правка')).toBeTruthy();
    expect(editor.value).toBe('Более новая правка');
    expect(screen.getByText('Редактирование сообщения')).toBeTruthy();
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);
    fireEvent.keyDown(editor, { key: 'Enter' });
    await waitFor(() => expect(chatApiMock.editMessage).toHaveBeenCalledTimes(2));
    expect(chatApiMock.editMessage).toHaveBeenLastCalledWith(mine.id, 'Более новая правка');
    await waitFor(() => expect(editor.value).toBe(draft));
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it('keeps a new composer draft after an edit was cancelled before its acknowledgement', async () => {
    const { editor, mine } = await renderOwnMessageEdit();
    let finish: (value: typeof mine) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(() => new Promise<typeof mine>((resolve) => { finish = resolve; }));
    fireEvent.change(editor, { target: { value: 'Сохранённая правка' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    fireEvent.click(screen.getByRole('button', { name: 'Отменить ответ или редактирование' }));
    fireEvent.change(editor, { target: { value: 'Новый отдельный черновик' } });
    await act(async () => { finish({ ...mine, text: 'Сохранённая правка' }); });
    expect(screen.getByText('Сохранённая правка')).toBeTruthy();
    expect(editor.value).toBe('Новый отдельный черновик');
    expect(screen.queryByText('Редактирование сообщения')).toBeNull();
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe('Новый отдельный черновик');
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it('keeps a reopened edit of the same message even when its text matches the old request', async () => {
    const { editor, mine, draft } = await renderOwnMessageEdit();
    let finish: (value: typeof mine) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(() => new Promise<typeof mine>((resolve) => { finish = resolve; }));
    fireEvent.change(editor, { target: { value: 'Совпадающий текст' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    fireEvent.click(screen.getByRole('button', { name: 'Отменить ответ или редактирование' }));
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    fireEvent.change(editor, { target: { value: 'Совпадающий текст' } });
    await act(async () => { finish({ ...mine, text: 'Совпадающий текст' }); });
    expect(editor.value).toBe('Совпадающий текст');
    expect(screen.getByText('Редактирование сообщения')).toBeTruthy();
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe(draft);
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it.each(['messenger', 'task'] as const)('ignores the previous actor edit acknowledgement after logout and another login: %s', async (host) => {
    const { view, editor, mine, draft, threadId } = await renderOwnMessageEdit(host);
    let finish: (value: typeof mine) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(() => new Promise<typeof mine>((resolve) => { finish = resolve; }));
    fireEvent.change(editor, { target: { value: 'Правка прежнего сотрудника' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    authState.userId = null;
    view.rerender(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    expect(screen.queryByPlaceholderText('Напишите сообщение…')).toBeNull();
    const next = { ...mine, authorUserId: 8, authorName: 'Анна', text: 'Сообщение нового сотрудника' };
    chatApiMock.getMessages.mockResolvedValue({ items: [next], hasMore: false });
    sessionStorage.setItem(`payplanner.chatDraft.v1.8.${threadId}`, 'Личный черновик Анны');
    authState.userId = 8;
    view.rerender(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    await screen.findByText(next.text);
    const nextEditor = screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    expect(nextEditor.value).toBe('Личный черновик Анны');
    fireEvent.change(nextEditor, { target: { value: 'Продолжение черновика Анны' } });
    await act(async () => { finish({ ...mine, text: 'Правка прежнего сотрудника' }); });
    expect(nextEditor.value).toBe('Продолжение черновика Анны');
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.8.${threadId}`)).toBe('Продолжение черновика Анны');
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe(draft);
    expect(screen.getByText(next.text)).toBeTruthy();
    expect(screen.queryByText('Правка прежнего сотрудника')).toBeNull();
    expect(toastMock.error).not.toHaveBeenCalled();
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it('keeps failed edit input and retries explicitly without creating another message', async () => {
    const { editor, mine, draft } = await renderOwnMessageEdit();
    let reject: (error: Error) => void = () => undefined;
    chatApiMock.editMessage.mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
    fireEvent.change(editor, { target: { value: 'Не терять после ошибки' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    await act(async () => { reject(new Error('Не удалось сохранить правку')); });
    expect(toastMock.error).toHaveBeenCalledWith('Не удалось сохранить правку');
    expect(editor.value).toBe('Не терять после ошибки');
    expect(screen.getByText('Редактирование сообщения')).toBeTruthy();
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe(draft);
    chatApiMock.editMessage.mockResolvedValue({ ...mine, text: 'Не терять после ошибки' });
    fireEvent.keyDown(editor, { key: 'Enter' });
    await waitFor(() => expect(chatApiMock.editMessage).toHaveBeenCalledTimes(2));
    expect(chatApiMock.editMessage).toHaveBeenLastCalledWith(mine.id, 'Не терять после ошибки');
    await waitFor(() => expect(editor.value).toBe(draft));
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
  });

  it('does not show the previous actor edit failure in the next actor conversation', async () => {
    const { view, editor } = await renderOwnMessageEdit();
    let reject: (error: Error) => void = () => undefined;
    chatApiMock.editMessage.mockImplementation(() => new Promise((_, fail) => { reject = fail; }));
    fireEvent.change(editor, { target: { value: 'Правка сотрудника 7' } });
    fireEvent.keyDown(editor, { key: 'Enter' });
    authState.userId = 8;
    sessionStorage.setItem('payplanner.chatDraft.v1.8.12', 'Черновик сотрудника 8');
    view.rerender(<InternalMessenger />);
    const nextEditor = await screen.findByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    expect(nextEditor.value).toBe('Черновик сотрудника 8');
    await act(async () => { reject(new Error('Ошибка прежнего сотрудника')); });
    expect(toastMock.error).not.toHaveBeenCalled();
    expect(nextEditor.value).toBe('Черновик сотрудника 8');
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.8.12')).toBe('Черновик сотрудника 8');
    expect(chatApiMock.editMessage).toHaveBeenCalledTimes(1);
  });

  it('keeps reply, text, files and identity while copies wait, including a failed exact retry', async () => {
    chatApiMock.sendMessage.mockImplementationOnce(async (...args) => ({ clientRequestId: args[5], status: 'waiting',
      automaticRecovery: true, description: 'Подготавливаются копии файлов.' }));
    const { container } = render(<InternalMessenger />);
    await screen.findByText('документ');
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Ответить' }));
    const editor = screen.getByPlaceholderText('Напишите сообщение…');
    fireEvent.change(editor, { target: { value: 'Проверено @[8]' } });
    const input = container.querySelector('input[type=file]')!;
    const first = new File(['original'], 'proof.txt', { type: 'text/plain', lastModified: 1 });
    fireEvent.change(input, { target: { files: [first] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    expect((await screen.findByRole('status')).textContent).toContain('Подготавливаются копии файлов.');
    expect(screen.getByDisplayValue('Проверено @[8]')).toBeTruthy();
    expect(screen.getByText('proof.txt')).toBeTruthy();
    const key = chatApiMock.sendMessage.mock.calls[0][5];
    expect(chatApiMock.sendMessage.mock.calls[0][3]).toBe(22);
    expect(JSON.parse(sessionStorage.getItem('payplanner.chatSend.v1.7.12')!).managed).toBe(true);
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    const reselected = new File(['original'], 'proof.txt', { type: 'text/plain', lastModified: 2 });
    fireEvent.change(input, { target: { files: [reselected] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(chatApiMock.sendMessage.mock.calls[1][5]).toBe(key);
    expect(JSON.parse(sessionStorage.getItem('payplanner.chatSend.v1.7.12')!).managed).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull());
    expect(chatApiMock.sendMessage.mock.calls[2][5]).toBe(key);
    expect((editor as HTMLTextAreaElement).value).toBe('');
    expect(screen.queryByText('proof.txt')).toBeNull();
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('does not erase a new draft or replacement file when the previous send completes late', async () => {
    let finish!: (value: typeof message) => void;
    chatApiMock.sendMessage.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const { container } = render(<InternalMessenger />);
    await screen.findByText('документ');
    const editor = screen.getByPlaceholderText('Напишите сообщение…');
    const input = container.querySelector('input[type=file]')!;
    fireEvent.change(editor, { target: { value: 'Первый текст' } });
    fireEvent.change(input, { target: { files: [new File(['first'], 'first.txt')] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1));
    fireEvent.change(editor, { target: { value: 'Новый текст' } });
    fireEvent.change(input, { target: { files: [new File(['next'], 'next.txt')] } });
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    fireEvent.click(screen.getByRole('button', { name: 'Ответить' }));
    await act(async () => { finish({ ...message, id: 23, text: 'Первый текст' }); });
    expect(screen.getByDisplayValue('Новый текст')).toBeTruthy();
    expect(screen.getByText('next.txt')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(2));
    expect(chatApiMock.sendMessage.mock.calls[1][3]).toBe(22);
    expect(chatApiMock.sendMessage.mock.calls[1][5]).not.toBe(chatApiMock.sendMessage.mock.calls[0][5]);
  });

  it.each(['messenger', 'task'] as const)('keeps an edited-back draft after a delayed send acknowledgement: %s', async (host) => {
    const threadId = host === 'task' ? 44 : 12;
    if (host === 'task') chatApiMock.openTask.mockResolvedValue({ ...thread, id: threadId, kind: 'task', taskJournalItemId: 101 });
    let finish!: (value: typeof message) => void;
    chatApiMock.sendMessage.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1));
    fireEvent.change(editor, { target: { value: 'Новый текст' } });
    fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
    await act(async () => { finish({ ...message, threadId, id: 23, text: 'Текст сообщения' }); });
    expect(editor.value).toBe('Текст сообщения');
    expect(sessionStorage.getItem(`payplanner.chatDraft.v1.7.${threadId}`)).toBe('Текст сообщения');
  });

  it('keeps an edited-back draft after a recovered published send', async () => {
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    let finish!: (value: unknown) => void;
    chatApiMock.getSendStatus.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.getSendStatus).toHaveBeenCalledTimes(1));
    const key = chatApiMock.sendMessage.mock.calls[0][5];
    fireEvent.change(editor, { target: { value: 'Новый текст' } });
    fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
    await act(async () => { finish({ clientRequestId: key, status: 'published', automaticRecovery: false,
      description: 'Сообщение отправлено.', message: { ...message, id: 23, authorUserId: 7, text: 'Текст сообщения' } }); });
    expect(editor.value).toBe('Текст сообщения');
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe('Текст сообщения');
    expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull();
  });

  it('keeps an edited-back draft after reopening a pending send', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Текст сообщения');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key, text: 'Текст сообщения', replyToId: null, files: [], draftChanged: true }));
    chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: key, status: 'published', automaticRecovery: false,
      description: 'Сообщение отправлено.', message: { ...message, id: 23, authorUserId: 7, text: 'Текст сообщения' } });
    render(<InternalMessenger />);
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull());
    expect((screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement).value).toBe('Текст сообщения');
    expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe('Текст сообщения');
  });

  it('keeps an edited-back draft if saving the pending marker fails', async () => {
    let finish!: (value: typeof message) => void;
    chatApiMock.sendMessage.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(<InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(1));
    const originalSet = Storage.prototype.setItem;
    const failure = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name: string, value: string) {
      if (name === 'payplanner.chatSend.v1.7.12') throw new DOMException('quota', 'QuotaExceededError');
      originalSet.call(this, name, value);
    });
    try {
      fireEvent.change(editor, { target: { value: 'Новый текст' } });
      fireEvent.change(editor, { target: { value: 'Текст сообщения' } });
      await act(async () => { finish({ ...message, id: 23, text: 'Текст сообщения' }); });
      expect(editor.value).toBe('Текст сообщения');
      expect(sessionStorage.getItem('payplanner.chatDraft.v1.7.12')).toBe('Текст сообщения');
    } finally { failure.mockRestore(); }
  });

  it('keeps the draft when the server returns another send identity', async () => {
    chatApiMock.sendMessage.mockResolvedValueOnce({ clientRequestId: '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c',
      status: 'published', message: { ...message, id: 23 }, description: 'Отправлено.' });
    render(<InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…');
    fireEvent.change(editor, { target: { value: 'Не терять' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining('идентификатор')));
    expect(screen.getByDisplayValue('Не терять')).toBeTruthy();
    expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).not.toBeNull();
  });

  it('continues a saved send after reopening without reselecting files', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Сохранённый текст');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key, text: 'Сохранённый текст',
      replyToId: null, files: [{ name: 'proof.txt', size: 8, lastModified: 1 }], managed: true }));
    chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: key, status: 'paused', automaticRecovery: false,
      description: 'Автоматическая отправка приостановлена.' });
    chatApiMock.resumeSend.mockResolvedValue({ clientRequestId: key, status: 'published', automaticRecovery: false,
      message: { ...message, id: 23, authorUserId: 7 }, description: 'Отправлено.' });
    render(<InternalMessenger />);
    expect(await screen.findByDisplayValue('Сохранённый текст')).toBeTruthy();
    fireEvent.click(await screen.findByText('Продолжить отправку'));
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull());
    expect((screen.getByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement).value).toBe('');
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    expect(chatApiMock.resumeSend).toHaveBeenCalledWith(12, key, expect.any(AbortSignal));
  });

  it('preserves a changed draft when a saved send publishes during recovery', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Новый текст');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key, text: 'Прежний текст',
      replyToId: null, files: [], managed: true }));
    chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: key, status: 'published', automaticRecovery: false,
      message: { ...message, id: 23, authorUserId: 7 }, description: 'Отправлено.' });
    render(<InternalMessenger />);
    await waitFor(() => expect(sessionStorage.getItem('payplanner.chatSend.v1.7.12')).toBeNull());
    expect(screen.getByDisplayValue('Новый текст')).toBeTruthy();
    expect(chatApiMock.sendMessage).not.toHaveBeenCalled();
    expect(chatApiMock.resumeSend).not.toHaveBeenCalled();
  });

  it('restores the frozen reply identity when missing originals must be reselected after reopening', async () => {
    const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
    sessionStorage.setItem('payplanner.chatDraft.v1.7.12', 'Сохранённый ответ');
    sessionStorage.setItem('payplanner.chatSend.v1.7.12', JSON.stringify({ id: key, text: 'Сохранённый ответ',
      replyToId: 15, files: [{ name: 'proof.txt', size: 8, lastModified: 1 }] }));
    chatApiMock.getSendStatus.mockResolvedValue({ clientRequestId: key, status: 'needsInput', automaticRecovery: false,
      description: 'Нужны исходные файлы.' });
    const { container } = render(<InternalMessenger />);
    expect(await screen.findByText('Ответ на сообщение №15 · сохранённый черновик')).toBeTruthy();
    await screen.findByText('Нужны исходные файлы.');
    fireEvent.change(container.querySelector('input[type=file]')!, { target: { files: [new File(['original'], 'proof.txt', { lastModified: 2 })] } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12, 'Сохранённый ответ', expect.any(Array), 15, [], key));
  });

  it('reuses the same send key after a failed response and starts a new key for another draft', async () => {
    chatApiMock.sendMessage.mockRejectedValueOnce(new Error('Связь прервана'));
    const view = render(<InternalMessenger />);
    await screen.findByText('документ');
    const editor = screen.getByPlaceholderText('Напишите сообщение…');
    fireEvent.change(editor, { target: { value: 'Готово' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    const firstKey = chatApiMock.sendMessage.mock.calls[0][5] as string;
    expect(firstKey).toMatch(/^[0-9a-f-]{36}$/i);
    view.unmount();
    render(<InternalMessenger />);
    expect(await screen.findByDisplayValue('Готово')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(2));
    expect(chatApiMock.sendMessage.mock.calls[1][5]).toBe(firstKey);
    await screen.findByText('Готово');

    fireEvent.change(screen.getByPlaceholderText('Напишите сообщение…'), { target: { value: 'Другое сообщение' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledTimes(3));
    expect(chatApiMock.sendMessage.mock.calls[2][5]).not.toBe(firstKey);
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
    fireEvent.click(screen.getAllByRole('button', { name: '@Анна' })[0]);
    const profile = screen.getByRole('dialog', { name: 'Профиль: Анна' });
    expect(within(profile).getByText('anna@example.test')).toBeTruthy();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog', { name: 'Профиль: Анна' })).toBeNull();
    fireEvent.click(screen.getAllByRole('button', { name: '@Анна' })[1]);
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Профиль: Анна' })).getByRole('button', { name: 'Закрыть профиль' }));
    expect(screen.queryByRole('dialog', { name: 'Профиль: Анна' })).toBeNull();
  });

  it('does not open a profile for a mention whose user is unavailable', async () => {
    chatApiMock.getMessages.mockResolvedValue({
      items: [{ ...message, text: 'Проверьте @[999]' }],
      hasMore: false,
    });
    render(<InternalMessenger />);

    expect(await screen.findByText('@участник #999')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '@участник #999' })).toBeNull();
    expect(screen.queryByRole('dialog', { name: /Профиль:/ })).toBeNull();
  });

  it('renders bulleted and numbered lists with inline formatting without interpreting HTML', async () => {
    chatApiMock.getMessages.mockResolvedValue({
      items: [{ ...message, text: 'План:\n- Проверить **иск**\n- Написать @[8]\n1. Подготовить *отзыв*\n2. Изучить `акт`\n<script>не код</script>' }],
      hasMore: false,
    });
    const { container } = render(<InternalMessenger />);

    expect(await screen.findByText('План:')).toBeTruthy();
    expect([...container.querySelectorAll('ul.list-disc li')].map((item) => item.textContent)).toEqual([
      'Проверить иск',
      'Написать @Анна',
    ]);
    expect([...container.querySelectorAll('ol.list-decimal li')].map((item) => item.textContent)).toEqual([
      'Подготовить отзыв',
      'Изучить акт',
    ]);
    expect(screen.getByText('иск').tagName).toBe('STRONG');
    expect(screen.getByText('отзыв').tagName).toBe('EM');
    expect(screen.getByText('акт').tagName).toBe('CODE');
    expect(container.querySelector('script')).toBeNull();
    expect(screen.getByText('<script>не код</script>')).toBeTruthy();
  });

  it('preserves an intentional gap in numbered list items', async () => {
    chatApiMock.getMessages.mockResolvedValue({
      items: [{ ...message, text: '1. Первый шаг\n3. Третий шаг' }],
      hasMore: false,
    });
    const { container } = render(<InternalMessenger />);

    expect(await screen.findByText('Первый шаг')).toBeTruthy();
    expect([...container.querySelectorAll<HTMLOListElement>('ol.list-decimal')].map((list) => list.start)).toEqual([1, 3]);
  });

  it('adds list markers at the current line and keeps toolbar edits in the chat draft', async () => {
    render(<InternalMessenger />);
    const editor = await screen.findByPlaceholderText('Напишите сообщение…') as HTMLTextAreaElement;
    fireEvent.change(editor, { target: { value: 'Первая строка\nВторая' } });
    editor.setSelectionRange('Первая строка\n'.length, 'Первая строка\n'.length);
    fireEvent.click(screen.getByTitle('Маркированный список'));
    expect(editor.value).toBe('Первая строка\n- Вторая');

    fireEvent.click(screen.getByTitle('Жирный'));
    expect(editor.value).toContain('**');
    editor.setSelectionRange(0, 0);
    fireEvent.click(screen.getByTitle('Нумерованный список'));
    expect(editor.value.startsWith('1. Первая строка')).toBe(true);
    const savedDrafts = Array.from({ length: sessionStorage.length }, (_, index) => sessionStorage.getItem(sessionStorage.key(index)!));
    expect(savedDrafts).toContain(editor.value);
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

  it.each(['messenger', 'task'] as const)('retries a failed read marker on the next scroll: %s', async (host) => {
    chatApiMock.markRead.mockRejectedValueOnce(new Error('Сеть недоступна'));
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    fireEvent.scroll(screen.getByLabelText('Сообщения чата'));
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledTimes(2));
    expect(chatApiMock.markRead).toHaveBeenLastCalledWith(host === 'task' ? 44 : 12, 22);
  });

  it.each(['messenger', 'task'] as const)('retries a failed read marker when network returns: %s', async (host) => {
    chatApiMock.markRead.mockRejectedValueOnce(new Error('Сеть недоступна'));
    chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
    render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    fireEvent(window, new Event('online'));
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledTimes(2));
    expect(chatApiMock.markRead).toHaveBeenLastCalledWith(host === 'task' ? 44 : 12, 22);
  });

  it('does not retry a read marker on reconnect while viewing older messages', async () => {
    chatApiMock.markRead.mockRejectedValueOnce(new Error('Сеть недоступна'));
    render(<InternalMessenger />);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    const conversation = screen.getByLabelText('Сообщения чата');
    Object.defineProperty(conversation, 'scrollHeight', { configurable: true, value: 1000 });
    Object.defineProperty(conversation, 'clientHeight', { configurable: true, value: 300 });
    conversation.scrollTop = 100;
    fireEvent(window, new Event('online'));
    expect(chatApiMock.markRead).toHaveBeenCalledTimes(1);
  });

  it('waits for the chat tab to become visible before retrying after reconnect', async () => {
    chatApiMock.markRead.mockRejectedValueOnce(new Error('Сеть недоступна'));
    render(<InternalMessenger />);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    try {
      visibility.mockReturnValue('hidden');
      fireEvent(window, new Event('online'));
      expect(chatApiMock.markRead).toHaveBeenCalledTimes(1);
      visibility.mockReturnValue('visible');
      fireEvent(document, new Event('visibilitychange'));
      await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledTimes(2));
    } finally {
      visibility.mockRestore();
    }
  });

  it.each(['messenger', 'task'] as const)('does not mark messages read while the chat tab is hidden: %s', async (host) => {
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    try {
      visibility.mockReturnValue('hidden');
      chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
      render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
      expect(await screen.findByText('документ')).toBeTruthy();
      expect(chatApiMock.markRead).not.toHaveBeenCalled();
      visibility.mockReturnValue('visible');
      fireEvent(document, new Event('visibilitychange'));
      await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledWith(host === 'task' ? 44 : 12, 22));
    } finally {
      visibility.mockRestore();
    }
  });

  it('keeps a new message unread when realtime refresh occurs in a hidden tab', async () => {
    render(<InternalMessenger />);
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledWith(12, 22));
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    try {
      visibility.mockReturnValue('hidden');
      chatApiMock.getMessages.mockResolvedValue({ items: [message, { ...message, id: 23, text: 'Новое фоновое сообщение' }], hasMore: false });
      const handlers = connectionMock.on.mock.calls.filter(([name]) => name === 'taskJournalChanged').map(([, handler]) => handler);
      await act(async () => { handlers.forEach((handler) => handler()); });
      expect(await screen.findByText('Новое фоновое сообщение')).toBeTruthy();
      expect(chatApiMock.markRead).toHaveBeenCalledTimes(1);
      visibility.mockReturnValue('visible');
      fireEvent(document, new Event('visibilitychange'));
      await waitFor(() => expect(chatApiMock.markRead).toHaveBeenLastCalledWith(12, 23));
    } finally {
      visibility.mockRestore();
    }
  });

  it.each(['messenger', 'task'] as const)('marks the last message only when it enters the viewport: %s', async (host) => {
    let notify!: IntersectionObserverCallback;
    const observed: Element[] = [];
    class Observer {
      constructor(callback: IntersectionObserverCallback) { notify = callback; }
      observe(target: Element) { observed.push(target); }
      disconnect() { /* Test observer has no external resources. */ }
      unobserve() { /* Not needed in this scenario. */ }
    }
    vi.stubGlobal('IntersectionObserver', Observer);
    try {
      chatApiMock.openTask.mockResolvedValue({ ...thread, id: 44, kind: 'task', taskJournalItemId: 101, name: 'Задача №101' });
      render(host === 'task' ? <TaskChatPanel taskId={101} /> : <InternalMessenger />);
      expect(await screen.findByText('документ')).toBeTruthy();
      await waitFor(() => expect(observed.length).toBeGreaterThan(0));
      expect(chatApiMock.markRead).not.toHaveBeenCalled();
      await act(async () => notify([{ target: observed.at(-1)!, isIntersecting: false, intersectionRatio: 0 } as IntersectionObserverEntry], {} as IntersectionObserver));
      expect(chatApiMock.markRead).not.toHaveBeenCalled();
      await act(async () => notify([{ target: observed.at(-1)!, isIntersecting: true, intersectionRatio: 1 } as IntersectionObserverEntry], {} as IntersectionObserver));
      await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledWith(host === 'task' ? 44 : 12, 22));
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('ignores a late read-marker failure after the account changes', async () => {
    let failOld!: (error: Error) => void;
    chatApiMock.markRead.mockImplementationOnce(() => new Promise((_resolve, reject) => { failOld = reject; }));
    const view = render(<InternalMessenger />);
    await waitFor(() => expect(chatApiMock.markRead).toHaveBeenCalledTimes(1));
    authState.userId = 8;
    view.rerender(<InternalMessenger />);
    await screen.findByPlaceholderText('Напишите сообщение…');
    await act(async () => failOld(new Error('Ошибка прошлого сотрудника')));
    expect(toastMock.error).not.toHaveBeenCalled();
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
    await waitFor(() => expect(chatApiMock.sendMessage).toHaveBeenCalledWith(12, 'Ответ из середины истории', [], null, [], expect.any(String)));
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
