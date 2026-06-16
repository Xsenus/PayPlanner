import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider } from '../../contexts/ToastContext';
import { StickyNotes } from './StickyNotes';
import type { StickyNoteBoard, StickyNoteItem } from '../../services/api';

vi.mock('../../services/api', () => ({
  apiService: {
    getStickyNoteBoards: vi.fn(),
    getStickyNotes: vi.fn(),
    createStickyNoteBoard: vi.fn(),
    createStickyNote: vi.fn(),
    updateStickyNote: vi.fn(),
    updateStickyNoteLayout: vi.fn(),
    deleteStickyNote: vi.fn(),
    deleteStickyNoteBoard: vi.fn(),
  },
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

import { apiService } from '../../services/api';

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
  createdAt: '2026-06-16T00:00:00Z',
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
    apiMock.createStickyNote.mockResolvedValue(createdNote);
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
});
