import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider } from '../../contexts/ToastContext';
import { copyTextToClipboard, getEffectiveBoardNotesCount, StickyNotes } from './StickyNotes';
import type { StickyNoteBoard, StickyNoteBoardMemberCandidate, StickyNoteItem } from '../../services/api';

const realtimeMock = vi.hoisted(() => {
  const handlers = new Map<string, (event: unknown) => void>();
  return {
    canStart: false,
    handlers,
    connection: {
      on: (eventName: string, handler: (event: unknown) => void) => {
        handlers.set(eventName, handler);
      },
      off: (eventName: string) => {
        handlers.delete(eventName);
      },
    },
  };
});

vi.mock('../../services/api', () => ({
  StickyNoteConflictError: class StickyNoteConflictError extends Error {
    constructor(public readonly serverNote: StickyNoteItem) {
      super('Sticky note version conflict');
      this.name = 'StickyNoteConflictError';
    }
  },
  apiService: {
    getStickyNoteBoards: vi.fn(),
    getStickyNotes: vi.fn(),
    getStickyNoteBoardMembers: vi.fn(),
    getStickyNoteMemberCandidates: vi.fn(),
    createStickyNoteBoard: vi.fn(),
    updateStickyNoteBoard: vi.fn(),
    createStickyNote: vi.fn(),
    updateStickyNote: vi.fn(),
    updateStickyNoteLayout: vi.fn(),
    deleteStickyNote: vi.fn(),
    deleteStickyNoteBoard: vi.fn(),
    restoreStickyNote: vi.fn(),
    upsertStickyNoteBoardMember: vi.fn(),
    deleteStickyNoteBoardMember: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, fullName: 'Пользователь', email: 'user@example.com' },
  }),
}));

vi.mock('../../services/stickyNotesRealtime', () => ({
  canStartNotesHubConnection: () => realtimeMock.canStart,
  createNotesHubConnection: vi.fn(() => realtimeMock.connection),
  joinNotesBoard: vi.fn(),
  leaveNotesBoard: vi.fn(),
  startNotesHubConnection: vi.fn(async () => true),
  stopNotesHubConnection: vi.fn(),
  STICKY_NOTE_BOARD_CHANGED_EVENT: 'stickyNoteBoardChanged',
}));

import { apiService, StickyNoteConflictError } from '../../services/api';

const apiMock = vi.mocked(apiService);

const board: StickyNoteBoard = {
  id: 7,
  title: 'Рабочая доска',
  visibility: 'shared',
  ownerUserId: null,
  ownerUserName: '',
  background: 'cork',
  sortOrder: 0,
  notesCount: 0,
  canEdit: true,
  canCreateNotes: true,
  canDelete: false,
  canDeleteNotes: true,
  canManageMembers: false,
  createdAt: '2026-06-16T00:00:00Z',
};

const personalBoard: StickyNoteBoard = {
  ...board,
  id: 9,
  title: 'Мои заметки',
  visibility: 'personal',
  ownerUserId: 1,
  ownerUserName: 'Пользователь',
  background: 'gray',
  canManageMembers: true,
};

const createdNote: StickyNoteItem = {
  id: 11,
  boardId: 7,
  text: '',
  color: 'yellow',
  x: 56,
  y: 56,
  width: 220,
  height: 180,
  zIndex: 1,
  version: 1,
  isPinned: false,
  authorUserId: 1,
  authorUserName: 'Пользователь',
  lastUpdatedByUserId: 1,
  lastUpdatedByUserName: 'Пользователь',
  createdAt: '2026-06-16T00:00:00Z',
};

function renderStickyNotes() {
  render(
    <ToastProvider>
      <StickyNotes />
    </ToastProvider>,
  );
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve;
  });

  return { promise, resolve };
}

describe('StickyNotes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    realtimeMock.canStart = false;
    realtimeMock.handlers.clear();
    window.localStorage.clear();
    if (!HTMLElement.prototype.setPointerCapture) {
      HTMLElement.prototype.setPointerCapture = vi.fn();
    }
    apiMock.getStickyNoteBoards.mockResolvedValue([board]);
    apiMock.getStickyNotes.mockResolvedValue([]);
    apiMock.getStickyNoteBoardMembers.mockResolvedValue([]);
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([]);
    apiMock.createStickyNote.mockResolvedValue(createdNote);
    apiMock.updateStickyNoteBoard.mockImplementation(async (_id, payload) => ({
      ...board,
      title: payload.title,
      description: payload.description,
      background: payload.background ?? board.background,
    }));
    apiMock.deleteStickyNote.mockResolvedValue(undefined);
    apiMock.restoreStickyNote.mockResolvedValue(createdNote);
    apiMock.upsertStickyNoteBoardMember.mockResolvedValue({
      id: 5,
      boardId: 9,
      userId: 3,
      displayName: 'Анна Юрист',
      email: 'anna@example.com',
      role: 'viewer',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('loads boards and notes', async () => {
    renderStickyNotes();

    expect(await screen.findAllByText('Рабочая доска')).toHaveLength(2);
    expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
  });

  it('restores the last selected board from local storage', async () => {
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);

    renderStickyNotes();

    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(9));
    expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBe('9');
  });

  it('clears the stored active board when no boards are available', async () => {
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([]);

    renderStickyNotes();

    expect(await screen.findByText('Досок пока нет.')).toBeTruthy();
    expect(apiMock.getStickyNotes).not.toHaveBeenCalled();
    expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBeNull();
  });

  it('ignores stale notes responses after switching boards', async () => {
    const personalNotes = createDeferred<StickyNoteItem[]>();
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => {
      if (boardId === 9) {
        return personalNotes.promise;
      }

      return [{ ...createdNote, id: 12, boardId: 7, text: 'Рабочая заметка после переключения' }];
    });

    const user = userEvent.setup();
    renderStickyNotes();

    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(9));
    await user.click(screen.getByRole('button', { name: /Рабочая доска/i }));

    expect(await screen.findByDisplayValue('Рабочая заметка после переключения')).toBeTruthy();

    await act(async () => {
      personalNotes.resolve([{ ...createdNote, boardId: 9, text: 'Старая личная заметка' }]);
      await personalNotes.promise;
    });

    expect(screen.queryByDisplayValue('Старая личная заметка')).toBeNull();
    expect(screen.getByDisplayValue('Рабочая заметка после переключения')).toBeTruthy();
  });

  it('creates a note on the active board', async () => {
    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: /Заметка/i }));

    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledWith(7, expect.objectContaining({
      color: 'yellow',
      width: 220,
      height: 180,
    })));
    expect(await screen.findByPlaceholderText('Текст заметки')).toBeTruthy();
  });

  it('does not duplicate a created note when realtime arrives before the API response', async () => {
    realtimeMock.canStart = true;
    const createdByRealtime = { ...createdNote, text: 'Создано почти одновременно' };
    const createdNoteResponse = createDeferred<StickyNoteItem>();
    apiMock.createStickyNote.mockImplementationOnce(() => createdNoteResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: /Заметка/i }));
    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledWith(7, expect.any(Object)));

    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({
      type: 'noteCreated',
      boardId: 7,
      note: createdByRealtime,
    });

    expect(await screen.findByDisplayValue('Создано почти одновременно')).toBeTruthy();

    await act(async () => {
      createdNoteResponse.resolve(createdByRealtime);
      await createdNoteResponse.promise;
    });

    expect(screen.getAllByDisplayValue('Создано почти одновременно')).toHaveLength(1);
  });

  it('ignores a stale created note response after switching boards', async () => {
    const createdNoteResponse = createDeferred<StickyNoteItem>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска открыта' }]
        : []
    ));
    apiMock.createStickyNote.mockImplementationOnce(() => createdNoteResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: /Заметка/i }));
    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledWith(7, expect.any(Object)));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная доска открыта')).toBeTruthy();

    await act(async () => {
      createdNoteResponse.resolve({ ...createdNote, text: 'Поздний ответ старой доски' });
      await createdNoteResponse.promise;
    });

    expect(screen.queryByDisplayValue('Поздний ответ старой доски')).toBeNull();
    expect(screen.getByDisplayValue('Личная доска открыта')).toBeTruthy();
  });

  it('switches away when the active board disappears during count refresh', async () => {
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards
      .mockResolvedValueOnce([board, personalBoard])
      .mockResolvedValue([board]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 7
        ? [{ ...createdNote, id: 12, boardId: 7, text: 'Рабочая доска после обновления' }]
        : []
    ));
    apiMock.createStickyNote.mockResolvedValue({
      ...createdNote,
      id: 13,
      boardId: 9,
      text: 'Личная заметка без доступа',
    });

    const user = userEvent.setup();
    renderStickyNotes();

    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(9));
    await user.click(screen.getByRole('button', { name: /Заметка/i }));

    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledWith(9, expect.any(Object)));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
    expect(await screen.findByDisplayValue('Рабочая доска после обновления')).toBeTruthy();
    expect(screen.queryByDisplayValue('Личная заметка без доступа')).toBeNull();
  });

  it('does not duplicate a board when the created board is already in the list', async () => {
    const user = userEvent.setup();
    apiMock.createStickyNoteBoard.mockResolvedValue({ ...board, title: 'Рабочая доска обновлена' });

    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.type(screen.getByPlaceholderText('Новая доска'), 'Рабочая доска обновлена');
    await user.click(screen.getByRole('button', { name: 'Общая' }));

    await waitFor(() => expect(apiMock.createStickyNoteBoard).toHaveBeenCalledWith({
      title: 'Рабочая доска обновлена',
      visibility: 'shared',
      background: 'cork',
    }));
    expect(screen.getAllByText('Рабочая доска обновлена')).toHaveLength(2);
    expect(screen.queryByText('Рабочая доска')).toBeNull();
  });

  it('disables note creation when the board does not allow creating notes', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([{ ...board, canCreateNotes: false }]);

    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');

    expect(screen.getByRole('button', { name: /Заметка/i })).toHaveProperty('disabled', true);
    expect(apiMock.createStickyNote).not.toHaveBeenCalled();
  });

  it('disables note creation when the active board reached the note limit', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([{ ...board, notesCount: 200 }]);

    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');

    const createButton = screen.getByRole('button', { name: /Заметка/i });
    expect(createButton).toHaveProperty('disabled', true);
    expect(createButton.getAttribute('title')).toBe('На одной доске можно хранить до 200 заметок.');
    expect(apiMock.createStickyNote).not.toHaveBeenCalled();
  });

  it('prefers loaded notes count when the board count is stale', () => {
    expect(getEffectiveBoardNotesCount(0, 200)).toBe(200);
    expect(getEffectiveBoardNotesCount(200, 0)).toBe(200);
    expect(getEffectiveBoardNotesCount(12, 8)).toBe(12);
  });

  it('updates board title and background from settings', async () => {
    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: 'Настройки доски' }));
    fireEvent.change(screen.getByPlaceholderText('Название доски'), { target: { value: 'Юридические заметки' } });
    fireEvent.change(screen.getByPlaceholderText('Короткое описание доски'), { target: { value: 'Напоминания по клиентским документам' } });
    await user.selectOptions(screen.getByLabelText('Фон доски'), 'gray');
    await user.click(screen.getByRole('button', { name: /Сохранить/i }));

    await waitFor(() => expect(apiMock.updateStickyNoteBoard).toHaveBeenCalledWith(7, {
      title: 'Юридические заметки',
      description: 'Напоминания по клиентским документам',
      background: 'gray',
    }));
  });

  it('filters notes by search text', async () => {
    apiMock.getStickyNotes.mockResolvedValue([
      { ...createdNote, id: 11, text: 'Заказать воду' },
      { ...createdNote, id: 12, text: 'Позвонить клиенту' },
    ]);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Заказать воду');
    await user.type(screen.getByPlaceholderText('Поиск'), 'клиент');

    expect(screen.queryByDisplayValue('Заказать воду')).toBeNull();
    expect(screen.getByDisplayValue('Позвонить клиенту')).toBeTruthy();
  });

  it('switches notes to the compact list view', async () => {
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Напомнить бухгалтерии' }]);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Напомнить бухгалтерии');
    await user.click(screen.getByRole('button', { name: 'Показать список' }));

    expect(screen.getByRole('button', { name: 'Показать холст' })).toBeTruthy();
    expect(screen.getByDisplayValue('Напомнить бухгалтерии')).toBeTruthy();
    expect(window.localStorage.getItem('payplanner.stickyNotes.viewMode')).toBe('list');
  });

  it('restores the compact list view from local storage', async () => {
    window.localStorage.setItem('payplanner.stickyNotes.viewMode', 'list');
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Открыть списком' }]);

    renderStickyNotes();

    expect(await screen.findByRole('button', { name: 'Показать холст' })).toBeTruthy();
    expect(screen.getByDisplayValue('Открыть списком')).toBeTruthy();
  });

  it('restores a deleted note from the undo action', async () => {
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Удаляемая заметка' }]);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Удаляемая заметка');
    await user.click(screen.getByTitle('Удалить'));
    await screen.findAllByText('Заметка удалена');
    await user.click(screen.getAllByRole('button', { name: /Восстановить/i })[0]);

    await waitFor(() => expect(apiMock.restoreStickyNote).toHaveBeenCalledWith(11));
  });

  it('does not show a restored note on another board after switching boards', async () => {
    const restoreResponse = createDeferred<StickyNoteItem>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная заметка' }]
        : [{ ...createdNote, text: 'Восстановить позже' }]
    ));
    apiMock.restoreStickyNote.mockImplementationOnce(() => restoreResponse.promise);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Восстановить позже');
    await user.click(screen.getByTitle('Удалить'));
    await screen.findAllByText('Заметка удалена');
    await user.click(screen.getAllByRole('button', { name: /Восстановить/i })[0]);
    await waitFor(() => expect(apiMock.restoreStickyNote).toHaveBeenCalledWith(11));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная заметка')).toBeTruthy();

    await act(async () => {
      restoreResponse.resolve({ ...createdNote, text: 'Позднее восстановление' });
      await restoreResponse.promise;
    });

    expect(screen.queryByDisplayValue('Позднее восстановление')).toBeNull();
    expect(screen.getByDisplayValue('Личная заметка')).toBeTruthy();
  });

  it('clears the local undo panel when a deleted note is restored through realtime', async () => {
    realtimeMock.canStart = true;
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Вернули в другой вкладке' }]);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Вернули в другой вкладке');
    await user.click(screen.getByTitle('Удалить'));

    await waitFor(() => expect(apiMock.deleteStickyNote).toHaveBeenCalledWith(11));
    expect(screen.getAllByText('Заметка удалена').length).toBeGreaterThan(1);

    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({
      type: 'noteRestored',
      boardId: 7,
      note: { ...createdNote, text: 'Вернули в другой вкладке' },
    });

    expect(await screen.findByDisplayValue('Вернули в другой вкладке')).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText('Заметка удалена').length).toBe(1));
  });

  it('does not show a stale delete undo after switching boards', async () => {
    const deleteResponse = createDeferred<void>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная заметка после удаления' }]
        : [{ ...createdNote, text: 'Удалить и уйти' }]
    ));
    apiMock.deleteStickyNote.mockImplementationOnce(() => deleteResponse.promise);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Удалить и уйти');
    await user.click(screen.getByTitle('Удалить'));
    await waitFor(() => expect(apiMock.deleteStickyNote).toHaveBeenCalledWith(11));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная заметка после удаления')).toBeTruthy();

    await act(async () => {
      deleteResponse.resolve();
      await deleteResponse.promise;
    });

    expect(screen.queryByText('Заметка удалена')).toBeNull();
    expect(screen.queryByRole('button', { name: /Восстановить/i })).toBeNull();
    expect(screen.getByDisplayValue('Личная заметка после удаления')).toBeTruthy();
  });

  it('disables note deletion when the board does not allow deleting notes', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([{ ...board, canDeleteNotes: false }]);
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Нельзя удалить' }]);

    renderStickyNotes();

    await screen.findByDisplayValue('Нельзя удалить');

    expect(screen.getByTitle('Удалить')).toHaveProperty('disabled', true);
    expect(apiMock.deleteStickyNote).not.toHaveBeenCalled();
  });

  it('clears deleted board notes and switches to the next available board', async () => {
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([board, { ...personalBoard, canDelete: true }]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, boardId: 9, text: 'Личная заметка перед удалением' }]
        : []
    ));
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Личная заметка перед удалением');
    await user.click(screen.getByTitle('Удалить доску'));

    await waitFor(() => expect(apiMock.deleteStickyNoteBoard).toHaveBeenCalledWith(9));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
    expect(screen.queryByDisplayValue('Личная заметка перед удалением')).toBeNull();
    expect(await screen.findByText('Доска заметок удалена.')).toBeTruthy();
  });

  it('clears active board state when the current board is deleted through realtime', async () => {
    realtimeMock.canStart = true;
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards
      .mockResolvedValueOnce([board, personalBoard])
      .mockResolvedValue([board]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, boardId: 9, text: 'Удалили в другой вкладке' }]
        : []
    ));

    renderStickyNotes();

    await screen.findByDisplayValue('Удалили в другой вкладке');
    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'boardDeleted', boardId: 9 });

    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
    expect(screen.queryByDisplayValue('Удалили в другой вкладке')).toBeNull();
  });

  it('clears active board notes when board access is revoked through realtime', async () => {
    realtimeMock.canStart = true;
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards
      .mockResolvedValueOnce([board, personalBoard])
      .mockResolvedValue([board]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, boardId: 9, text: 'Доступ уже отозвали' }]
        : []
    ));

    renderStickyNotes();

    await screen.findByDisplayValue('Доступ уже отозвали');
    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'boardMembersChanged', boardId: 9 });

    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
    await waitFor(() => expect(apiMock.getStickyNoteBoardMembers).toHaveBeenCalledWith(7));
    expect(apiMock.getStickyNoteBoardMembers.mock.calls.map(([boardId]) => boardId)).toEqual([9, 7]);
    expect(screen.queryByDisplayValue('Доступ уже отозвали')).toBeNull();
  });

  it('adds a board member when the user manages a personal board', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([personalBoard]);
    apiMock.getStickyNoteBoardMembers.mockResolvedValue([
      {
        id: 0,
        boardId: 9,
        userId: 1,
        displayName: 'Пользователь',
        email: 'user@example.com',
        role: 'owner',
      },
    ]);
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([
      { userId: 3, displayName: 'Анна Юрист', email: 'anna@example.com' },
    ]);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByText('Участники');
    await user.selectOptions(screen.getByLabelText('Сотрудник для доступа к доске'), '3');
    await user.click(screen.getByTitle('Добавить участника'));

    await waitFor(() => expect(apiMock.upsertStickyNoteBoardMember).toHaveBeenCalledWith(9, 3, { role: 'viewer' }));
  });

  it('requires selecting a board member candidate before adding access', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([personalBoard]);
    apiMock.getStickyNoteBoardMembers.mockResolvedValue([
      {
        id: 0,
        boardId: 9,
        userId: 1,
        displayName: 'Пользователь',
        email: 'user@example.com',
        role: 'owner',
      },
    ]);
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([
      { userId: 3, displayName: 'Анна Юрист', email: 'anna@example.com' },
    ]);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByRole('option', { name: 'Анна Юрист' });
    await user.click(screen.getByTitle('Добавить участника'));

    expect(apiMock.upsertStickyNoteBoardMember).not.toHaveBeenCalled();
    expect(await screen.findByText('Выберите сотрудника.')).toBeTruthy();
  });

  it('clears the selected member candidate when the user becomes a board member', async () => {
    realtimeMock.canStart = true;
    const ownerMember = {
      id: 0,
      boardId: 9,
      userId: 1,
      displayName: 'Пользователь',
      email: 'user@example.com',
      role: 'owner' as const,
    };
    const annaMember = {
      id: 5,
      boardId: 9,
      userId: 3,
      displayName: 'Анна Юрист',
      email: 'anna@example.com',
      role: 'viewer' as const,
    };
    apiMock.getStickyNoteBoards.mockResolvedValue([personalBoard]);
    apiMock.getStickyNoteBoardMembers
      .mockResolvedValueOnce([ownerMember])
      .mockResolvedValue([ownerMember, annaMember]);
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([
      { userId: 3, displayName: 'Анна Юрист', email: 'anna@example.com' },
    ]);

    const user = userEvent.setup();
    renderStickyNotes();

    const memberSelect = await screen.findByLabelText('Сотрудник для доступа к доске') as HTMLSelectElement;
    await screen.findByRole('option', { name: 'Анна Юрист' });
    await user.selectOptions(memberSelect, '3');
    expect(memberSelect.value).toBe('3');

    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'boardMembersChanged', boardId: 9 });

    await waitFor(() => expect(apiMock.getStickyNoteBoardMembers).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(memberSelect.value).toBe(''));
    expect(screen.queryByRole('option', { name: 'Анна Юрист' })).toBeNull();
  });

  it('ignores stale member candidate responses after switching boards', async () => {
    const staleCandidates = createDeferred<StickyNoteBoardMemberCandidate[]>();
    const freshCandidates = createDeferred<StickyNoteBoardMemberCandidate[]>();
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNoteMemberCandidates
      .mockImplementationOnce(() => staleCandidates.promise)
      .mockImplementationOnce(() => freshCandidates.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByText('Участники');
    await waitFor(() => expect(apiMock.getStickyNoteMemberCandidates).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole('button', { name: /Рабочая доска/i }));
    await waitFor(() => expect(screen.queryByText('Участники')).toBeNull());

    await act(async () => {
      staleCandidates.resolve([{ userId: 3, displayName: 'Анна Юрист', email: 'anna@example.com' }]);
      await staleCandidates.promise;
    });

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    await screen.findByText('Участники');
    await waitFor(() => expect(apiMock.getStickyNoteMemberCandidates).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole('option', { name: 'Анна Юрист' })).toBeNull();

    await act(async () => {
      freshCandidates.resolve([{ userId: 4, displayName: 'Олег Бухгалтер', email: 'oleg@example.com' }]);
      await freshCandidates.promise;
    });

    expect(await screen.findByRole('option', { name: 'Олег Бухгалтер' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Анна Юрист' })).toBeNull();
  });

  it('autosaves note text after typing pauses', async () => {
    const user = userEvent.setup();
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Черновик' }]);
    apiMock.updateStickyNote.mockResolvedValue({ ...createdNote, text: 'Черновик обновлен', version: 2 });

    renderStickyNotes();

    await screen.findByDisplayValue('Черновик');
    await user.type(screen.getByDisplayValue('Черновик'), ' обновлен');

    await waitFor(() => expect(apiMock.updateStickyNote).toHaveBeenCalledWith(11, expect.objectContaining({
      text: 'Черновик обновлен',
      version: 1,
    })), { timeout: 2500 });
  }, 4000);

  it('cancels pending autosave when switching boards', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 7
        ? [{ ...createdNote, text: 'Черновик перед уходом' }]
        : [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска открыта' }]
    ));

    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Черновик перед уходом');
    fireEvent.change(textarea, { target: { value: 'Черновик перед уходом обновлен' } });
    fireEvent.click(screen.getByRole('button', { name: /Мои заметки/i }));

    expect(await screen.findByDisplayValue('Личная доска открыта')).toBeTruthy();
    await new Promise((resolve) => window.setTimeout(resolve, 1300));

    expect(apiMock.updateStickyNote).not.toHaveBeenCalled();
  }, 4000);

  it('saves the current textarea value on blur', async () => {
    const user = userEvent.setup();
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Старый текст' }]);
    apiMock.updateStickyNote.mockResolvedValue({ ...createdNote, text: 'Новый текст', version: 2 });

    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Старый текст');
    await user.clear(textarea);
    await user.type(textarea, 'Новый текст');
    await user.tab();

    await waitFor(() => expect(apiMock.updateStickyNote).toHaveBeenCalledWith(11, expect.objectContaining({
      text: 'Новый текст',
      version: 1,
    })));
  });

  it('saves the current textarea value with Ctrl+Enter', async () => {
    const user = userEvent.setup();
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Быстрая заметка' }]);
    apiMock.updateStickyNote.mockResolvedValue({ ...createdNote, text: 'Быстрая заметка готова', version: 2 });

    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Быстрая заметка');
    await user.type(textarea, ' готова');
    await user.keyboard('{Control>}{Enter}{/Control}');

    await waitFor(() => expect(apiMock.updateStickyNote).toHaveBeenCalledWith(11, expect.objectContaining({
      text: 'Быстрая заметка готова',
      version: 1,
    })));
  });

  it('shows a conflict panel without losing the local text', async () => {
    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Моя версия', version: 1 }]);
    apiMock.updateStickyNote.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));

    expect(await screen.findByText('Заметка изменена другим пользователем')).toBeTruthy();
    expect(screen.getByText('Версия сервера')).toBeTruthy();
    expect(screen.getAllByText('Моя версия').length).toBeGreaterThan(1);
  });

  it('applies the server version from the conflict panel', async () => {
    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Моя версия', version: 1 }]);
    apiMock.updateStickyNote.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));
    await user.click(await screen.findByRole('button', { name: /Обновить с сервера/i }));

    expect(screen.getByDisplayValue('Версия сервера')).toBeTruthy();
    expect(screen.queryByText('Заметка изменена другим пользователем')).toBeNull();
  });

  it('copies conflict draft text through the clipboard helper', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);

    await copyTextToClipboard('Моя версия', { writeText });

    expect(writeText).toHaveBeenCalledWith('Моя версия');
  });

  it('overwrites the server conflict version with the local draft', async () => {
    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Моя версия', version: 1 }]);
    apiMock.updateStickyNote
      .mockRejectedValueOnce(new StickyNoteConflictError(serverNote))
      .mockResolvedValueOnce({ ...serverNote, text: 'Моя версия', version: 3 });

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));
    await user.click(await screen.findByRole('button', { name: /^Перезаписать$/i }));

    await waitFor(() => expect(apiMock.updateStickyNote).toHaveBeenLastCalledWith(11, expect.objectContaining({
      text: 'Моя версия',
      version: 2,
    })));
    expect(screen.queryByText('Заметка изменена другим пользователем')).toBeNull();
  });

  it('disables conflict overwrite when edit access is revoked', async () => {
    realtimeMock.canStart = true;
    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Моя версия', version: 1 }]);
    apiMock.updateStickyNote.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));
    expect(await screen.findByText('Заметка изменена другим пользователем')).toBeTruthy();

    apiMock.getStickyNoteBoards.mockResolvedValue([{ ...board, canEdit: false }]);
    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'boardUpdated', boardId: 7 });

    const overwriteButton = await screen.findByRole('button', { name: /^Перезаписать$/i });
    await waitFor(() => expect(overwriteButton).toHaveProperty('disabled', true));
    expect(overwriteButton.getAttribute('title')).toBe('Нет права редактировать эту доску');
  });

  it('shows a conflict panel when note layout save conflicts', async () => {
    const localNote = { ...createdNote, text: 'Перетащить меня', x: 56, y: 56, version: 1 };
    const serverNote = { ...localNote, x: 300, y: 220, version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([localNote]);
    apiMock.updateStickyNoteLayout.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Перетащить меня');
    const noteElement = textarea.parentElement as HTMLElement;
    expect(noteElement).toBeTruthy();

    fireEvent.pointerDown(noteElement, { pointerId: 1, clientX: 120, clientY: 120 });
    fireEvent.pointerMove(window, { pointerId: 1, clientX: 180, clientY: 170 });
    fireEvent.pointerUp(window, { pointerId: 1, clientX: 180, clientY: 170 });

    await waitFor(() => expect(apiMock.updateStickyNoteLayout).toHaveBeenCalledWith(11, expect.objectContaining({
      x: 116,
      y: 106,
      version: 1,
    })));
    expect(await screen.findByText('Заметка изменена другим пользователем')).toBeTruthy();
    expect(screen.getAllByText('Перетащить меня').length).toBeGreaterThan(1);
  });

  it('keeps a local draft when a realtime update arrives during pending autosave', async () => {
    realtimeMock.canStart = true;
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Мой черновик', version: 1 }]);
    const serverNote = { ...createdNote, text: 'Версия из другой вкладки', version: 2 };

    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Мой черновик');
    fireEvent.change(textarea, { target: { value: 'Мой черновик с правкой' } });
    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'noteUpdated', boardId: 7, note: serverNote });

    expect(await screen.findByText('Заметка изменена другим пользователем')).toBeTruthy();
    expect(screen.getByDisplayValue('Мой черновик с правкой')).toBeTruthy();
    expect(screen.getByText('Версия из другой вкладки')).toBeTruthy();

    await new Promise((resolve) => window.setTimeout(resolve, 1300));
    expect(apiMock.updateStickyNote).not.toHaveBeenCalled();
  }, 4000);

  it('clears note conflict when the conflicting note is deleted through realtime', async () => {
    realtimeMock.canStart = true;
    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNotes.mockResolvedValue([{ ...createdNote, text: 'Моя версия', version: 1 }]);
    apiMock.updateStickyNote.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));
    expect(await screen.findByText('Заметка изменена другим пользователем')).toBeTruthy();

    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'noteDeleted', boardId: 7, noteId: 11 });

    await waitFor(() => expect(screen.queryByText('Заметка изменена другим пользователем')).toBeNull());
    expect(screen.queryByDisplayValue('Моя версия')).toBeNull();
    expect(screen.queryByText('Версия сервера')).toBeNull();
  });
});
