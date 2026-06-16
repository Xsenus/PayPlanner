import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider } from '../../contexts/ToastContext';
import { copyTextToClipboard, getEffectiveBoardNotesCount, StickyNotes } from './StickyNotes';
import type { StickyNoteBoard, StickyNoteBoardMember, StickyNoteBoardMemberCandidate, StickyNoteItem } from '../../services/api';

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
import { joinNotesBoard, startNotesHubConnection } from '../../services/stickyNotesRealtime';

const apiMock = vi.mocked(apiService);
const joinNotesBoardMock = vi.mocked(joinNotesBoard);
const startNotesHubConnectionMock = vi.mocked(startNotesHubConnection);

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
  canUpdateSettings: true,
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
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((nextResolve, nextReject) => {
    resolve = nextResolve;
    reject = nextReject;
  });

  return { promise, resolve, reject };
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

  it('ignores stale notes responses when a newer refresh has already applied', async () => {
    const staleNotes = createDeferred<StickyNoteItem[]>();
    const freshNotes = createDeferred<StickyNoteItem[]>();
    apiMock.getStickyNotes
      .mockImplementationOnce(() => staleNotes.promise)
      .mockImplementationOnce(() => freshNotes.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7));
    await user.click(screen.getByTitle('Обновить'));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledTimes(2));

    await act(async () => {
      freshNotes.resolve([{ ...createdNote, text: 'Свежая заметка' }]);
      await freshNotes.promise;
    });
    expect(await screen.findByDisplayValue('Свежая заметка')).toBeTruthy();

    await act(async () => {
      staleNotes.resolve([{ ...createdNote, text: 'Устаревшая заметка' }]);
      await staleNotes.promise;
    });

    expect(screen.getByDisplayValue('Свежая заметка')).toBeTruthy();
    expect(screen.queryByDisplayValue('Устаревшая заметка')).toBeNull();
  });

  it('does not join a stale realtime board when hub start finishes after switching boards', async () => {
    realtimeMock.canStart = true;
    const oldBoardStart = createDeferred<boolean>();
    const newBoardStart = createDeferred<boolean>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    startNotesHubConnectionMock
      .mockResolvedValueOnce(true)
      .mockImplementationOnce(() => oldBoardStart.promise)
      .mockImplementationOnce(() => newBoardStart.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByRole('button', { name: /Рабочая доска/i });
    await waitFor(() => expect(startNotesHubConnectionMock).toHaveBeenCalledTimes(2));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    await waitFor(() => expect(startNotesHubConnectionMock).toHaveBeenCalledTimes(3));
    const oldBoardJoinCount = joinNotesBoardMock.mock.calls.filter(([, boardId]) => boardId === 7).length;

    await act(async () => {
      oldBoardStart.resolve(true);
      await oldBoardStart.promise;
    });
    expect(joinNotesBoardMock.mock.calls.filter(([, boardId]) => boardId === 7)).toHaveLength(oldBoardJoinCount);

    await act(async () => {
      newBoardStart.resolve(true);
      await newBoardStart.promise;
    });
    await waitFor(() => expect(joinNotesBoardMock).toHaveBeenCalledWith(realtimeMock.connection, 9));
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

  it('ignores realtime note payloads that belong to another board', async () => {
    realtimeMock.canStart = true;
    renderStickyNotes();

    await screen.findByRole('button', { name: /Рабочая доска/i });
    await waitFor(() => expect(realtimeMock.handlers.has('stickyNoteBoardChanged')).toBe(true));

    act(() => {
      realtimeMock.handlers.get('stickyNoteBoardChanged')?.({
        type: 'noteCreated',
        boardId: 7,
        note: { ...createdNote, id: 17, boardId: 9, text: 'Чужая доска' },
      });
    });

    expect(screen.queryByDisplayValue('Чужая доска')).toBeNull();
    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(2));
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

  it('does not show a stale create note error after switching boards', async () => {
    const createdNoteResponse = createDeferred<StickyNoteItem>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска после ошибки создания' }]
        : []
    ));
    apiMock.createStickyNote.mockImplementationOnce(() => createdNoteResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: /Заметка/i }));
    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledWith(7, expect.any(Object)));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная доска после ошибки создания')).toBeTruthy();

    await act(async () => {
      createdNoteResponse.reject(new Error('create failed'));
      await Promise.resolve();
    });

    expect(screen.queryByText('Не удалось создать заметку.')).toBeNull();
    expect(screen.getByDisplayValue('Личная доска после ошибки создания')).toBeTruthy();
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

  it('does not switch boards or clear a new draft after a late board creation response', async () => {
    const createBoardResponse = createDeferred<StickyNoteBoard>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.createStickyNoteBoard.mockImplementationOnce(() => createBoardResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    const titleInput = screen.getByPlaceholderText('Новая доска');
    await user.type(titleInput, 'Новая общая доска');
    await user.click(screen.getByRole('button', { name: 'Общая' }));
    await waitFor(() => expect(apiMock.createStickyNoteBoard).toHaveBeenCalledWith({
      title: 'Новая общая доска',
      visibility: 'shared',
      background: 'cork',
    }));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    await user.clear(titleInput);
    await user.type(titleInput, 'Следующая доска');
    await waitFor(() => expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBe('9'));

    await act(async () => {
      createBoardResponse.resolve({
        ...board,
        id: 13,
        title: 'Новая общая доска',
        sortOrder: 2,
      });
      await createBoardResponse.promise;
    });

    expect(screen.getByRole('button', { name: /Новая общая доска/i })).toBeTruthy();
    expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBe('9');
    expect(titleInput).toHaveProperty('value', 'Следующая доска');
    expect(apiMock.getStickyNotes).not.toHaveBeenCalledWith(13);
  });

  it('does not show a stale board creation error after switching boards and typing a new draft', async () => {
    const createBoardResponse = createDeferred<StickyNoteBoard>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.createStickyNoteBoard.mockImplementationOnce(() => createBoardResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    const titleInput = screen.getByPlaceholderText('Новая доска');
    await user.type(titleInput, 'Ошибочная доска');
    await user.click(screen.getByRole('button', { name: 'Общая' }));
    await waitFor(() => expect(apiMock.createStickyNoteBoard).toHaveBeenCalledWith({
      title: 'Ошибочная доска',
      visibility: 'shared',
      background: 'cork',
    }));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    await user.clear(titleInput);
    await user.type(titleInput, 'Новая попытка');
    await waitFor(() => expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBe('9'));

    await act(async () => {
      createBoardResponse.reject(new Error('board creation failed'));
      await Promise.resolve();
    });

    expect(screen.queryByText('Не удалось создать доску.')).toBeNull();
    expect(window.localStorage.getItem('payplanner.stickyNotes.activeBoardId')).toBe('9');
    expect(titleInput).toHaveProperty('value', 'Новая попытка');
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

  it('hides board settings when the user can only edit notes on another personal board', async () => {
    apiMock.getStickyNoteBoards.mockResolvedValue([
      {
        ...personalBoard,
        canEdit: true,
        canUpdateSettings: false,
        canManageMembers: false,
      },
    ]);

    renderStickyNotes();

    await screen.findAllByText('Мои заметки');
    expect(screen.queryByRole('button', { name: 'Настройки доски' })).toBeNull();
  });

  it('does not show stale board settings toast after switching boards', async () => {
    const updateBoardResponse = createDeferred<StickyNoteBoard>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.updateStickyNoteBoard.mockImplementationOnce(() => updateBoardResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findAllByText('Рабочая доска');
    await user.click(screen.getByRole('button', { name: 'Настройки доски' }));
    fireEvent.change(screen.getByPlaceholderText('Название доски'), { target: { value: 'Юридические заметки' } });
    await user.selectOptions(screen.getByLabelText('Фон доски'), 'gray');
    await user.click(screen.getByRole('button', { name: /Сохранить/i }));
    await waitFor(() => expect(apiMock.updateStickyNoteBoard).toHaveBeenCalledWith(7, {
      title: 'Юридические заметки',
      description: null,
      background: 'gray',
    }));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    await waitFor(() => expect(apiMock.getStickyNotes).toHaveBeenCalledWith(9));

    await act(async () => {
      updateBoardResponse.resolve({
        ...board,
        title: 'Юридические заметки',
        background: 'gray',
      });
      await updateBoardResponse.promise;
    });

    expect(screen.queryByText('Настройки доски сохранены.')).toBeNull();
    expect(screen.getByRole('button', { name: /Мои заметки/i })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Юридические заметки/i })).toBeTruthy();
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

  it('does not show a stale restore note error after switching boards', async () => {
    const restoreResponse = createDeferred<StickyNoteItem>();
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска после ошибки восстановления' }]
        : [{ ...createdNote, text: 'Восстановить с ошибкой' }]
    ));
    apiMock.restoreStickyNote.mockImplementationOnce(() => restoreResponse.promise);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Восстановить с ошибкой');
    await user.click(screen.getByTitle('Удалить'));
    await screen.findAllByText('Заметка удалена');
    await user.click(screen.getAllByRole('button', { name: /Восстановить/i })[0]);
    await waitFor(() => expect(apiMock.restoreStickyNote).toHaveBeenCalledWith(11));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная доска после ошибки восстановления')).toBeTruthy();

    await act(async () => {
      restoreResponse.reject(new Error('restore failed'));
      await Promise.resolve();
    });

    expect(screen.queryByText('Не удалось восстановить заметку.')).toBeNull();
    expect(screen.getByDisplayValue('Личная доска после ошибки восстановления')).toBeTruthy();
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

  it('keeps the current board when a board deletion finishes after switching boards', async () => {
    const archiveBoard: StickyNoteBoard = {
      ...board,
      id: 13,
      title: 'Архив заметок',
      sortOrder: 2,
    };
    const deleteBoardResponse = createDeferred<void>();
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([{ ...personalBoard, canDelete: true }, board, archiveBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, boardId: 9, text: 'Доска удаляется' }]
        : boardId === 13
          ? [{ ...createdNote, id: 13, boardId: 13, text: 'Архив открыт' }]
          : [{ ...createdNote, id: 7, text: 'Рабочая открылась' }]
    ));
    apiMock.deleteStickyNoteBoard.mockImplementationOnce(() => deleteBoardResponse.promise);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Доска удаляется');
    await user.click(screen.getByTitle('Удалить доску'));
    await waitFor(() => expect(apiMock.deleteStickyNoteBoard).toHaveBeenCalledWith(9));

    await user.click(screen.getByRole('button', { name: /Архив заметок/i }));
    expect(await screen.findByDisplayValue('Архив открыт')).toBeTruthy();

    await act(async () => {
      deleteBoardResponse.resolve();
      await deleteBoardResponse.promise;
    });

    expect(screen.getByDisplayValue('Архив открыт')).toBeTruthy();
    expect(screen.queryByDisplayValue('Рабочая открылась')).toBeNull();
    expect(screen.queryByText('Доска заметок удалена.')).toBeNull();
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

  it('ignores stale board list refreshes when a newer refresh has already applied', async () => {
    realtimeMock.canStart = true;
    const staleBoards = createDeferred<StickyNoteBoard[]>();
    const freshBoards = createDeferred<StickyNoteBoard[]>();
    apiMock.getStickyNoteBoards
      .mockResolvedValueOnce([board, personalBoard])
      .mockImplementationOnce(() => staleBoards.promise)
      .mockImplementationOnce(() => freshBoards.promise);

    renderStickyNotes();

    await screen.findByRole('button', { name: /Рабочая доска/i });
    await waitFor(() => expect(realtimeMock.handlers.has('stickyNoteBoardChanged')).toBe(true));
    const handler = realtimeMock.handlers.get('stickyNoteBoardChanged');

    act(() => handler?.({ type: 'boardMembersChanged', boardId: 9 }));
    act(() => handler?.({ type: 'boardUpdated', boardId: 9 }));
    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(3));

    await act(async () => {
      freshBoards.resolve([{ ...board, title: 'Актуальная доска' }, personalBoard]);
      await freshBoards.promise;
    });
    expect(await screen.findByRole('button', { name: /Актуальная доска/i })).toBeTruthy();

    await act(async () => {
      staleBoards.resolve([{ ...board, title: 'Устаревшая доска' }, personalBoard]);
      await staleBoards.promise;
    });

    expect(screen.getByRole('button', { name: /Актуальная доска/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Устаревшая доска/i })).toBeNull();
  });

  it('ignores stale board count refreshes when a newer refresh has already applied', async () => {
    const firstCreateResponse = createDeferred<StickyNoteItem>();
    const secondCreateResponse = createDeferred<StickyNoteItem>();
    const staleBoards = createDeferred<StickyNoteBoard[]>();
    const freshBoards = createDeferred<StickyNoteBoard[]>();
    apiMock.getStickyNoteBoards
      .mockResolvedValueOnce([board, personalBoard])
      .mockImplementationOnce(() => staleBoards.promise)
      .mockImplementationOnce(() => freshBoards.promise);
    apiMock.createStickyNote
      .mockImplementationOnce(() => firstCreateResponse.promise)
      .mockImplementationOnce(() => secondCreateResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByRole('button', { name: /Рабочая доска/i });
    const addNoteButton = screen.getByRole('button', { name: /Заметка/i });
    await user.click(addNoteButton);
    await user.click(addNoteButton);
    await waitFor(() => expect(apiMock.createStickyNote).toHaveBeenCalledTimes(2));

    await act(async () => {
      firstCreateResponse.resolve({ ...createdNote, id: 21, text: 'Первый стикер' });
      await firstCreateResponse.promise;
    });
    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(2));

    await act(async () => {
      secondCreateResponse.resolve({ ...createdNote, id: 22, text: 'Второй стикер' });
      await secondCreateResponse.promise;
    });
    await waitFor(() => expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(3));

    await act(async () => {
      freshBoards.resolve([{ ...board, title: 'Актуальные счетчики', notesCount: 2 }, personalBoard]);
      await freshBoards.promise;
    });
    expect(await screen.findByRole('button', { name: /Актуальные счетчики/i })).toBeTruthy();

    await act(async () => {
      staleBoards.resolve([{ ...board, title: 'Устаревшие счетчики', notesCount: 1 }, personalBoard]);
      await staleBoards.promise;
    });

    expect(screen.getByRole('button', { name: /Актуальные счетчики/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Устаревшие счетчики/i })).toBeNull();
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

  it('does not add a stale board member after switching boards', async () => {
    const archiveBoard: StickyNoteBoard = {
      ...personalBoard,
      id: 13,
      title: 'Личные напоминания',
      sortOrder: 2,
    };
    const ownerMember = {
      id: 0,
      boardId: 9,
      userId: 1,
      displayName: 'Пользователь',
      email: 'user@example.com',
      role: 'owner' as const,
    };
    const archiveOwnerMember = {
      ...ownerMember,
      boardId: 13,
    };
    const upsertResponse = createDeferred<StickyNoteBoardMember>();
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([personalBoard, archiveBoard]);
    apiMock.getStickyNoteBoardMembers.mockImplementation(async (boardId) => (
      boardId === 13 ? [archiveOwnerMember] : [ownerMember]
    ));
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([
      { userId: 3, displayName: 'Анна Юрист', email: 'anna@example.com' },
    ]);
    apiMock.upsertStickyNoteBoardMember.mockImplementationOnce(() => upsertResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByText('Участники');
    await user.selectOptions(screen.getByLabelText('Сотрудник для доступа к доске'), '3');
    await user.click(screen.getByTitle('Добавить участника'));
    await waitFor(() => expect(apiMock.upsertStickyNoteBoardMember).toHaveBeenCalledWith(9, 3, { role: 'viewer' }));

    await user.click(screen.getByRole('button', { name: /Личные напоминания/i }));
    await waitFor(() => expect(apiMock.getStickyNoteBoardMembers).toHaveBeenCalledWith(13));
    expect(screen.queryAllByText('Анна Юрист').filter((element) => element.tagName !== 'OPTION')).toHaveLength(0);

    await act(async () => {
      upsertResponse.resolve({
        id: 5,
        boardId: 9,
        userId: 3,
        displayName: 'Анна Юрист',
        email: 'anna@example.com',
        role: 'viewer',
      });
      await upsertResponse.promise;
    });

    expect(screen.queryAllByText('Анна Юрист').filter((element) => element.tagName !== 'OPTION')).toHaveLength(0);
    expect(screen.getByRole('button', { name: /Личные напоминания/i })).toBeTruthy();
  });

  it('does not remove a member from another board after switching boards', async () => {
    const archiveBoard: StickyNoteBoard = {
      ...personalBoard,
      id: 13,
      title: 'Личные напоминания',
      sortOrder: 2,
    };
    const ownerMember = {
      id: 0,
      boardId: 9,
      userId: 1,
      displayName: 'Пользователь',
      email: 'user@example.com',
      role: 'owner' as const,
    };
    const annaOnPersonal = {
      id: 5,
      boardId: 9,
      userId: 3,
      displayName: 'Анна Юрист',
      email: 'anna@example.com',
      role: 'viewer' as const,
    };
    const annaOnArchive = {
      ...annaOnPersonal,
      id: 6,
      boardId: 13,
    };
    const deleteMemberResponse = createDeferred<void>();
    window.localStorage.setItem('payplanner.stickyNotes.activeBoardId', '9');
    apiMock.getStickyNoteBoards.mockResolvedValue([personalBoard, archiveBoard]);
    apiMock.getStickyNoteBoardMembers.mockImplementation(async (boardId) => (
      boardId === 13
        ? [{ ...ownerMember, boardId: 13 }, annaOnArchive]
        : [ownerMember, annaOnPersonal]
    ));
    apiMock.deleteStickyNoteBoardMember.mockImplementationOnce(() => deleteMemberResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByText('Анна Юрист');
    await user.click(screen.getByTitle('Убрать участника'));
    await waitFor(() => expect(apiMock.deleteStickyNoteBoardMember).toHaveBeenCalledWith(9, 3));

    await user.click(screen.getByRole('button', { name: /Личные напоминания/i }));
    await waitFor(() => expect(apiMock.getStickyNoteBoardMembers).toHaveBeenCalledWith(13));
    expect(screen.queryAllByText('Анна Юрист').filter((element) => element.tagName !== 'OPTION')).toHaveLength(1);

    await act(async () => {
      deleteMemberResponse.resolve();
      await deleteMemberResponse.promise;
    });

    expect(screen.queryAllByText('Анна Юрист').filter((element) => element.tagName !== 'OPTION')).toHaveLength(1);
    expect(screen.getByRole('button', { name: /Личные напоминания/i })).toBeTruthy();
  });

  it('ignores stale member responses when a newer refresh has already applied', async () => {
    realtimeMock.canStart = true;
    const staleMembers = createDeferred<StickyNoteBoardMember[]>();
    const freshMembers = createDeferred<StickyNoteBoardMember[]>();
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
      .mockImplementationOnce(() => staleMembers.promise)
      .mockImplementationOnce(() => freshMembers.promise);

    renderStickyNotes();

    await screen.findByText('Участники');
    await waitFor(() => expect(realtimeMock.handlers.has('stickyNoteBoardChanged')).toBe(true));
    realtimeMock.handlers.get('stickyNoteBoardChanged')?.({ type: 'boardMembersChanged', boardId: 9 });
    await waitFor(() => expect(apiMock.getStickyNoteBoardMembers).toHaveBeenCalledTimes(2));

    await act(async () => {
      freshMembers.resolve([ownerMember, annaMember]);
      await freshMembers.promise;
    });
    expect(await screen.findByText('Анна Юрист')).toBeTruthy();

    await act(async () => {
      staleMembers.resolve([ownerMember]);
      await staleMembers.promise;
    });

    expect(screen.getByText('Анна Юрист')).toBeTruthy();
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

  it('does not show a stale note conflict after switching boards', async () => {
    const saveResponse = createDeferred<StickyNoteItem>();
    const serverNote = { ...createdNote, text: 'Версия старой доски', version: 2 };
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная заметка после сохранения' }]
        : [{ ...createdNote, text: 'Моя версия', version: 1 }]
    ));
    apiMock.updateStickyNote.mockImplementationOnce(() => saveResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    await screen.findByDisplayValue('Моя версия');
    await user.click(screen.getByTitle('Сохранить'));
    await waitFor(() => expect(apiMock.updateStickyNote).toHaveBeenCalledWith(11, expect.objectContaining({
      text: 'Моя версия',
      version: 1,
    })));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная заметка после сохранения')).toBeTruthy();

    await act(async () => {
      saveResponse.reject(new StickyNoteConflictError(serverNote));
      await Promise.resolve();
    });

    expect(screen.queryByText('Заметка изменена другим пользователем')).toBeNull();
    expect(screen.queryByText('Версия старой доски')).toBeNull();
    expect(screen.getByDisplayValue('Личная заметка после сохранения')).toBeTruthy();
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
    const globalWriteText = vi.fn().mockResolvedValue(undefined);

    await copyTextToClipboard('Моя версия', { writeText });
    vi.stubGlobal('navigator', { clipboard: { writeText: globalWriteText } });
    await copyTextToClipboard('Глобальная версия');

    expect(writeText).toHaveBeenCalledWith('Моя версия');
    expect(globalWriteText).toHaveBeenCalledWith('Глобальная версия');
  });

  it('does not show a stale conflict copy toast after switching boards', async () => {
    const copyResponse = createDeferred<void>();
    let copiedText = '';
    const writeText = vi.fn((text: string) => {
      copiedText = text;
      return copyResponse.promise;
    });

    const user = userEvent.setup();
    const clipboardDescriptor = Object.getOwnPropertyDescriptor(window.navigator, 'clipboard');
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    expect(window.navigator.clipboard.writeText).toBe(writeText);

    const serverNote = { ...createdNote, text: 'Версия сервера', version: 2 };
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска после копирования' }]
        : [{ ...createdNote, text: 'Моя версия', version: 1 }]
    ));
    apiMock.updateStickyNote.mockRejectedValueOnce(new StickyNoteConflictError(serverNote));

    try {
      renderStickyNotes();

      await screen.findByDisplayValue('Моя версия');
      await user.click(screen.getByTitle('Сохранить'));
      await user.click(await screen.findByRole('button', { name: /Скопировать мой текст/i }));
      await waitFor(() => expect(copiedText).toBe('Моя версия'));

      await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
      expect(await screen.findByDisplayValue('Личная доска после копирования')).toBeTruthy();

      await act(async () => {
        copyResponse.resolve();
        await copyResponse.promise;
      });

      expect(screen.queryByText('Текст вашей версии скопирован.')).toBeNull();
      expect(screen.getByDisplayValue('Личная доска после копирования')).toBeTruthy();
    } finally {
      if (clipboardDescriptor) {
        Object.defineProperty(window.navigator, 'clipboard', clipboardDescriptor);
      } else {
        delete (window.navigator as Navigator & { clipboard?: Clipboard }).clipboard;
      }
    }
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

  it('does not show a stale layout conflict after switching boards', async () => {
    const layoutResponse = createDeferred<StickyNoteItem>();
    const localNote = { ...createdNote, text: 'Перетащить и уйти', x: 56, y: 56, version: 1 };
    const serverNote = { ...localNote, x: 300, y: 220, version: 2 };
    apiMock.getStickyNoteBoards.mockResolvedValue([board, personalBoard]);
    apiMock.getStickyNotes.mockImplementation(async (boardId) => (
      boardId === 9
        ? [{ ...createdNote, id: 12, boardId: 9, text: 'Личная доска после перемещения' }]
        : [localNote]
    ));
    apiMock.updateStickyNoteLayout.mockImplementationOnce(() => layoutResponse.promise);

    const user = userEvent.setup();
    renderStickyNotes();

    const textarea = await screen.findByDisplayValue('Перетащить и уйти');
    const noteElement = textarea.parentElement as HTMLElement;
    fireEvent.pointerDown(noteElement, { pointerId: 1, clientX: 120, clientY: 120 });
    fireEvent.pointerMove(window, { pointerId: 1, clientX: 180, clientY: 170 });
    fireEvent.pointerUp(window, { pointerId: 1, clientX: 180, clientY: 170 });

    await waitFor(() => expect(apiMock.updateStickyNoteLayout).toHaveBeenCalledWith(11, expect.objectContaining({
      x: 116,
      y: 106,
      version: 1,
    })));

    await user.click(screen.getByRole('button', { name: /Мои заметки/i }));
    expect(await screen.findByDisplayValue('Личная доска после перемещения')).toBeTruthy();

    await act(async () => {
      layoutResponse.reject(new StickyNoteConflictError(serverNote));
      await Promise.resolve();
    });

    expect(screen.queryByText('Заметка изменена другим пользователем')).toBeNull();
    expect(screen.queryByDisplayValue('Перетащить и уйти')).toBeNull();
    expect(screen.getByDisplayValue('Личная доска после перемещения')).toBeTruthy();
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
