import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider } from '../../contexts/ToastContext';
import { StickyNotes } from './StickyNotes';
import type { StickyNoteBoard, StickyNoteItem } from '../../services/api';

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
  canStartNotesHubConnection: () => false,
  createNotesHubConnection: vi.fn(),
  joinNotesBoard: vi.fn(),
  leaveNotesBoard: vi.fn(),
  startNotesHubConnection: vi.fn(),
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
  canDelete: false,
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

describe('StickyNotes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getStickyNoteBoards.mockResolvedValue([board]);
    apiMock.getStickyNotes.mockResolvedValue([]);
    apiMock.getStickyNoteBoardMembers.mockResolvedValue([]);
    apiMock.getStickyNoteMemberCandidates.mockResolvedValue([]);
    apiMock.createStickyNote.mockResolvedValue(createdNote);
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
  });

  it('loads boards and notes', async () => {
    renderStickyNotes();

    expect(await screen.findAllByText('Рабочая доска')).toHaveLength(2);
    expect(apiMock.getStickyNoteBoards).toHaveBeenCalledTimes(1);
    expect(apiMock.getStickyNotes).toHaveBeenCalledWith(7);
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
});
