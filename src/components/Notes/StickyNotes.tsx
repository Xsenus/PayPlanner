import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent } from 'react';
import {
  Filter,
  Lock,
  Palette,
  Pin,
  PinOff,
  Plus,
  RefreshCw,
  Save,
  Search,
  StickyNote,
  Trash2,
  Undo2,
  UserPlus,
  Users,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { apiService, type StickyNoteBoard, type StickyNoteBoardMember, type StickyNoteBoardMemberCandidate, type StickyNoteBoardMemberRole, type StickyNoteColor, type StickyNoteItem } from '../../services/api';
import {
  canStartNotesHubConnection,
  createNotesHubConnection,
  joinNotesBoard,
  leaveNotesBoard,
  startNotesHubConnection,
  STICKY_NOTE_BOARD_CHANGED_EVENT,
  stopNotesHubConnection,
  type StickyNotesRealtimeHandler,
} from '../../services/stickyNotesRealtime';
import { useToast } from '../../hooks/useToast';
import type { HubConnection } from '@microsoft/signalr';

const COLORS: Array<{ value: StickyNoteColor; label: string; className: string }> = [
  { value: 'yellow', label: 'Желтая', className: 'bg-amber-100 border-amber-200 text-slate-950' },
  { value: 'blue', label: 'Синяя', className: 'bg-sky-100 border-sky-200 text-slate-950' },
  { value: 'green', label: 'Зеленая', className: 'bg-emerald-100 border-emerald-200 text-slate-950' },
  { value: 'pink', label: 'Розовая', className: 'bg-rose-100 border-rose-200 text-slate-950' },
  { value: 'cyan', label: 'Бирюзовая', className: 'bg-cyan-100 border-cyan-200 text-slate-950' },
  { value: 'white', label: 'Белая', className: 'bg-white border-slate-200 text-slate-950' },
];

const DEFAULT_NOTE_SIZE = { width: 220, height: 180 };
const FILTER_OPTIONS = [
  { value: 'all', label: 'Все' },
  { value: 'mine', label: 'Мои' },
  { value: 'recent', label: 'Недавние' },
  { value: 'pinned', label: 'Закрепленные' },
] as const;

type NoteFilter = typeof FILTER_OPTIONS[number]['value'] | StickyNoteColor;

function colorClass(color: StickyNoteColor): string {
  return COLORS.find((item) => item.value === color)?.className ?? COLORS[0].className;
}

function sortNotes(notes: StickyNoteItem[]) {
  return [...notes].sort((a, b) => a.zIndex - b.zIndex || a.id - b.id);
}

function isInteractiveTarget(target: EventTarget | null) {
  return target instanceof HTMLElement && Boolean(target.closest('textarea,button,select,input'));
}

export function StickyNotes() {
  const toast = useToast();
  const { user } = useAuth();
  const connectionRef = useRef<HubConnection | null>(null);
  const boardCanvasRef = useRef<HTMLDivElement | null>(null);
  const notesRef = useRef<StickyNoteItem[]>([]);
  const [boards, setBoards] = useState<StickyNoteBoard[]>([]);
  const [activeBoardId, setActiveBoardId] = useState<number | null>(null);
  const [notes, setNotes] = useState<StickyNoteItem[]>([]);
  const [members, setMembers] = useState<StickyNoteBoardMember[]>([]);
  const [memberCandidates, setMemberCandidates] = useState<StickyNoteBoardMemberCandidate[]>([]);
  const [loading, setLoading] = useState(true);
  const [notesLoading, setNotesLoading] = useState(false);
  const [newBoardTitle, setNewBoardTitle] = useState('');
  const [creatingBoard, setCreatingBoard] = useState(false);
  const [savingNoteIds, setSavingNoteIds] = useState<Set<number>>(new Set());
  const [selectedNoteId, setSelectedNoteId] = useState<number | null>(null);
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<NoteFilter>('all');
  const [deletedNoteId, setDeletedNoteId] = useState<number | null>(null);
  const [memberUserId, setMemberUserId] = useState('');
  const [memberRole, setMemberRole] = useState<Exclude<StickyNoteBoardMemberRole, 'owner'>>('viewer');

  const activeBoard = useMemo(
    () => boards.find((board) => board.id === activeBoardId) ?? null,
    [activeBoardId, boards],
  );

  const canEditActiveBoard = Boolean(activeBoard?.canEdit);
  const currentUserId = user?.id ?? null;

  const filteredNotes = useMemo(() => {
    const normalizedSearch = search.trim().toLowerCase();
    const recentThreshold = Date.now() - 7 * 24 * 60 * 60 * 1000;

    return sortNotes(notes).filter((note) => {
      const searchOk = !normalizedSearch
        || note.text.toLowerCase().includes(normalizedSearch)
        || note.authorUserName.toLowerCase().includes(normalizedSearch);
      if (!searchOk) {
        return false;
      }

      if (filter === 'mine') {
        return Boolean(currentUserId && note.authorUserId === currentUserId);
      }

      if (filter === 'recent') {
        return new Date(note.updatedAt ?? note.createdAt).getTime() >= recentThreshold;
      }

      if (filter === 'pinned') {
        return note.isPinned;
      }

      if (filter === 'all') {
        return true;
      }

      return note.color === filter;
    });
  }, [currentUserId, filter, notes, search]);

  const availableMemberCandidates = useMemo(() => {
    const blocked = new Set(members.map((member) => member.userId));
    if (activeBoard?.ownerUserId) {
      blocked.add(activeBoard.ownerUserId);
    }

    return memberCandidates.filter((candidate) => !blocked.has(candidate.userId));
  }, [activeBoard?.ownerUserId, memberCandidates, members]);

  const loadBoards = useCallback(async () => {
    setLoading(true);
    try {
      const nextBoards = await apiService.getStickyNoteBoards();
      setBoards(nextBoards);
      setActiveBoardId((current) => {
        if (current && nextBoards.some((board) => board.id === current)) {
          return current;
        }

        return nextBoards[0]?.id ?? null;
      });
    } catch (error) {
      console.error(error);
      toast.error('Не удалось загрузить доски заметок.');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  const loadNotes = useCallback(async (boardId: number) => {
    setNotesLoading(true);
    try {
      const nextNotes = await apiService.getStickyNotes(boardId);
      setNotes(sortNotes(nextNotes));
    } catch (error) {
      console.error(error);
      toast.error('Не удалось загрузить заметки на доске.');
    } finally {
      setNotesLoading(false);
    }
  }, [toast]);

  const loadMembers = useCallback(async (boardId: number) => {
    try {
      const nextMembers = await apiService.getStickyNoteBoardMembers(boardId);
      setMembers(nextMembers);
    } catch (error) {
      console.error(error);
      setMembers([]);
    }
  }, []);

  useEffect(() => {
    void loadBoards();
  }, [loadBoards]);

  useEffect(() => {
    notesRef.current = notes;
  }, [notes]);

  useEffect(() => {
    if (!activeBoardId) {
      setNotes([]);
      setMembers([]);
      return;
    }

    setSelectedNoteId(null);
    setDeletedNoteId(null);
    void loadNotes(activeBoardId);
    void loadMembers(activeBoardId);
  }, [activeBoardId, loadMembers, loadNotes]);

  useEffect(() => {
    if (!activeBoard?.canManageMembers) {
      return;
    }

    void apiService.getStickyNoteMemberCandidates()
      .then(setMemberCandidates)
      .catch((error) => {
        console.error(error);
        setMemberCandidates([]);
      });
  }, [activeBoard?.canManageMembers]);

  useEffect(() => {
    if (!canStartNotesHubConnection()) {
      return;
    }

    const connection = createNotesHubConnection();
    connectionRef.current = connection;

    const handler: StickyNotesRealtimeHandler = (event) => {
      if (event.boardId !== activeBoardId) {
        void loadBoards();
        return;
      }

      if (event.type === 'noteDeleted' && event.noteId) {
        setNotes((current) => current.filter((note) => note.id !== event.noteId));
      } else if ((event.type === 'noteCreated' || event.type === 'noteUpdated' || event.type === 'noteRestored') && event.note) {
        setNotes((current) => {
          const exists = current.some((note) => note.id === event.note!.id);
          const next = exists
            ? current.map((note) => (note.id === event.note!.id ? event.note! : note))
            : [...current, event.note!];
          return sortNotes(next);
        });
      } else if (event.type === 'boardDeleted') {
        void loadBoards();
      } else if (event.type === 'boardMembersChanged') {
        void loadBoards();
        if (activeBoardId) {
          void loadMembers(activeBoardId);
        }
      }
    };

    connection.on(STICKY_NOTE_BOARD_CHANGED_EVENT, handler);
    void startNotesHubConnection(connection).then((started) => {
      if (started && activeBoardId) {
        void joinNotesBoard(connection, activeBoardId);
      }
    });

    return () => {
      connection.off(STICKY_NOTE_BOARD_CHANGED_EVENT, handler);
      connectionRef.current = null;
      void stopNotesHubConnection(connection);
    };
  }, [activeBoardId, loadBoards, loadMembers]);

  useEffect(() => {
    const connection = connectionRef.current;
    if (!connection || !activeBoardId) {
      return;
    }

    void joinNotesBoard(connection, activeBoardId);
    return () => {
      void leaveNotesBoard(connection, activeBoardId);
    };
  }, [activeBoardId]);

  const refreshBoardCounts = useCallback(async () => {
    try {
      const nextBoards = await apiService.getStickyNoteBoards();
      setBoards(nextBoards);
    } catch {
      // The next manual refresh will recover counts.
    }
  }, []);

  const createBoard = async (visibility: 'personal' | 'shared') => {
    const title = newBoardTitle.trim();
    if (!title) {
      toast.error('Введите название доски.');
      return;
    }

    setCreatingBoard(true);
    try {
      const board = await apiService.createStickyNoteBoard({
        title,
        visibility,
        background: visibility === 'shared' ? 'cork' : 'gray',
      });
      setBoards((current) => [...current, board]);
      setActiveBoardId(board.id);
      setNewBoardTitle('');
    } catch (error) {
      console.error(error);
      toast.error('Не удалось создать доску.');
    } finally {
      setCreatingBoard(false);
    }
  };

  const createNoteAt = async (x?: number, y?: number) => {
    if (!activeBoardId || !canEditActiveBoard) {
      return;
    }

    const offset = notes.length * 24;
    try {
      const note = await apiService.createStickyNote(activeBoardId, {
        text: '',
        color: 'yellow',
        x: Math.max(0, x ?? 56 + (offset % 360)),
        y: Math.max(0, y ?? 56 + (offset % 240)),
        ...DEFAULT_NOTE_SIZE,
      });
      setNotes((current) => sortNotes([...current, note]));
      setSelectedNoteId(note.id);
      await refreshBoardCounts();
    } catch (error) {
      console.error(error);
      toast.error('Не удалось создать заметку.');
    }
  };

  const saveNote = async (note: StickyNoteItem) => {
    if (!canEditActiveBoard) {
      return;
    }

    setSavingNoteIds((current) => new Set(current).add(note.id));
    try {
      const saved = await apiService.updateStickyNote(note.id, {
        text: note.text,
        color: note.color,
        x: note.x,
        y: note.y,
        width: note.width,
        height: note.height,
        zIndex: note.zIndex,
        isPinned: note.isPinned,
        version: note.version,
      });
      setNotes((current) => sortNotes(current.map((item) => (item.id === saved.id ? saved : item))));
    } catch (error) {
      console.error(error);
      toast.error('Заметка уже изменена другим пользователем. Доска обновлена.');
      if (activeBoardId) {
        await loadNotes(activeBoardId);
      }
    } finally {
      setSavingNoteIds((current) => {
        const next = new Set(current);
        next.delete(note.id);
        return next;
      });
    }
  };

  const saveNoteLayout = async (note: StickyNoteItem) => {
    if (!canEditActiveBoard) {
      return;
    }

    try {
      const saved = await apiService.updateStickyNoteLayout(note.id, {
        x: note.x,
        y: note.y,
        width: note.width,
        height: note.height,
        zIndex: note.zIndex,
        version: note.version,
      });
      setNotes((current) => sortNotes(current.map((item) => (item.id === saved.id ? saved : item))));
    } catch (error) {
      console.error(error);
      if (activeBoardId) {
        await loadNotes(activeBoardId);
      }
    }
  };

  const patchNote = (noteId: number, patch: Partial<StickyNoteItem>) => {
    setNotes((current) => sortNotes(current.map((note) => (note.id === noteId ? { ...note, ...patch } : note))));
  };

  const restoreDeletedNote = async (noteId: number) => {
    try {
      const restored = await apiService.restoreStickyNote(noteId);
      setNotes((current) => sortNotes([...current.filter((note) => note.id !== restored.id), restored]));
      setDeletedNoteId(null);
      await refreshBoardCounts();
      toast.success('Заметка восстановлена.');
    } catch (error) {
      console.error(error);
      toast.error('Не удалось восстановить заметку.');
    }
  };

  const deleteNote = async (noteId: number) => {
    if (!canEditActiveBoard || !window.confirm('Удалить заметку?')) {
      return;
    }

    try {
      await apiService.deleteStickyNote(noteId);
      setNotes((current) => current.filter((note) => note.id !== noteId));
      setSelectedNoteId(null);
      setDeletedNoteId(noteId);
      toast.showToast({
        title: 'Заметка удалена',
        description: 'Удаление можно отменить, пока доска открыта.',
        variant: 'info',
        actionLabel: 'Восстановить',
        onAction: () => {
          void restoreDeletedNote(noteId);
        },
        durationMs: 8000,
      });
      await refreshBoardCounts();
    } catch (error) {
      console.error(error);
      toast.error('Не удалось удалить заметку.');
    }
  };

  const deleteBoard = async () => {
    if (!activeBoard?.canDelete || !window.confirm('Удалить доску вместе с заметками?')) {
      return;
    }

    try {
      await apiService.deleteStickyNoteBoard(activeBoard.id);
      const nextBoards = boards.filter((board) => board.id !== activeBoard.id);
      setBoards(nextBoards);
      setActiveBoardId(nextBoards[0]?.id ?? null);
    } catch (error) {
      console.error(error);
      toast.error('Не удалось удалить доску.');
    }
  };

  const upsertMember = async () => {
    if (!activeBoard?.canManageMembers) {
      return;
    }

    const parsedUserId = Number(memberUserId || availableMemberCandidates[0]?.userId);
    if (!parsedUserId) {
      toast.error('Выберите сотрудника.');
      return;
    }

    try {
      const member = await apiService.upsertStickyNoteBoardMember(activeBoard.id, parsedUserId, { role: memberRole });
      setMembers((current) => {
        const exists = current.some((item) => item.userId === member.userId);
        return exists
          ? current.map((item) => (item.userId === member.userId ? member : item))
          : [...current, member];
      });
      setMemberUserId('');
      await loadBoards();
    } catch (error) {
      console.error(error);
      toast.error('Не удалось обновить участника доски.');
    }
  };

  const deleteMember = async (member: StickyNoteBoardMember) => {
    if (!activeBoard?.canManageMembers || member.role === 'owner') {
      return;
    }

    try {
      await apiService.deleteStickyNoteBoardMember(activeBoard.id, member.userId);
      setMembers((current) => current.filter((item) => item.userId !== member.userId));
      await loadBoards();
    } catch (error) {
      console.error(error);
      toast.error('Не удалось убрать участника.');
    }
  };

  const handleCanvasDoubleClick = (event: ReactMouseEvent<HTMLDivElement>) => {
    if (!canEditActiveBoard || isInteractiveTarget(event.target)) {
      return;
    }

    const rect = boardCanvasRef.current?.getBoundingClientRect();
    if (!rect) {
      void createNoteAt();
      return;
    }

    void createNoteAt(
      event.clientX - rect.left + boardCanvasRef.current!.scrollLeft - DEFAULT_NOTE_SIZE.width / 2,
      event.clientY - rect.top + boardCanvasRef.current!.scrollTop - 28,
    );
  };

  const startDrag = (event: PointerEvent<HTMLDivElement>, note: StickyNoteItem) => {
    if (!canEditActiveBoard || note.isPinned || isInteractiveTarget(event.target)) {
      return;
    }

    setSelectedNoteId(note.id);
    event.currentTarget.setPointerCapture(event.pointerId);
    const startX = event.clientX;
    const startY = event.clientY;
    const initial = { x: note.x, y: note.y };
    const topZIndex = Math.max(1, ...notes.map((item) => item.zIndex)) + 1;
    patchNote(note.id, { zIndex: topZIndex });

    const handleMove = (moveEvent: globalThis.PointerEvent) => {
      patchNote(note.id, {
        x: Math.max(0, initial.x + moveEvent.clientX - startX),
        y: Math.max(0, initial.y + moveEvent.clientY - startY),
        zIndex: topZIndex,
      });
    };

    const handleUp = () => {
      window.removeEventListener('pointermove', handleMove);
      window.removeEventListener('pointerup', handleUp);
      const latest = notesRef.current.find((item) => item.id === note.id);
      if (latest) {
        void saveNoteLayout({ ...latest, zIndex: topZIndex });
      }
    };

    window.addEventListener('pointermove', handleMove);
    window.addEventListener('pointerup', handleUp, { once: true });
  };

  const startResize = (event: PointerEvent<HTMLButtonElement>, note: StickyNoteItem) => {
    if (!canEditActiveBoard || note.isPinned) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    setSelectedNoteId(note.id);
    event.currentTarget.setPointerCapture(event.pointerId);
    const startX = event.clientX;
    const startY = event.clientY;
    const initial = { width: note.width, height: note.height };

    const handleMove = (moveEvent: globalThis.PointerEvent) => {
      patchNote(note.id, {
        width: Math.min(640, Math.max(140, initial.width + moveEvent.clientX - startX)),
        height: Math.min(640, Math.max(120, initial.height + moveEvent.clientY - startY)),
      });
    };

    const handleUp = () => {
      window.removeEventListener('pointermove', handleMove);
      window.removeEventListener('pointerup', handleUp);
      const latest = notesRef.current.find((item) => item.id === note.id);
      if (latest) {
        void saveNoteLayout(latest);
      }
    };

    window.addEventListener('pointermove', handleMove);
    window.addEventListener('pointerup', handleUp, { once: true });
  };

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if ((event.key !== 'Delete' && event.key !== 'Backspace') || !selectedNoteId || isInteractiveTarget(event.target)) {
        return;
      }

      event.preventDefault();
      void deleteNote(selectedNoteId);
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedNoteId, canEditActiveBoard]);

  return (
    <div className="flex h-[calc(100vh-4rem)] min-h-[620px] bg-slate-100">
      <aside className="flex w-72 shrink-0 flex-col border-r border-slate-200 bg-white">
        <div className="border-b border-slate-200 p-3">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
            <StickyNote className="h-4 w-4" />
            Заметки
          </div>
          <div className="mt-3 flex gap-2">
            <input
              value={newBoardTitle}
              onChange={(event) => setNewBoardTitle(event.target.value)}
              className="min-w-0 flex-1 rounded-md border border-slate-300 px-2 py-1.5 text-sm"
              placeholder="Новая доска"
            />
          </div>
          <div className="mt-2 grid grid-cols-2 gap-2">
            <button
              type="button"
              onClick={() => createBoard('personal')}
              disabled={creatingBoard}
              className="inline-flex items-center justify-center gap-1 rounded-md border border-slate-300 px-2 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-60"
            >
              <Lock className="h-3.5 w-3.5" />
              Личная
            </button>
            <button
              type="button"
              onClick={() => createBoard('shared')}
              disabled={creatingBoard}
              className="inline-flex items-center justify-center gap-1 rounded-md border border-slate-300 px-2 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-60"
            >
              <Users className="h-3.5 w-3.5" />
              Общая
            </button>
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-2">
          {loading ? (
            <div className="p-3 text-sm text-slate-500">Загрузка досок...</div>
          ) : boards.length === 0 ? (
            <div className="p-3 text-sm text-slate-500">Досок пока нет.</div>
          ) : (
            boards.map((board) => (
              <button
                key={board.id}
                type="button"
                onClick={() => setActiveBoardId(board.id)}
                className={`mb-1 flex w-full items-center gap-2 rounded-md px-2 py-2 text-left text-sm transition ${
                  board.id === activeBoardId
                    ? 'bg-slate-900 text-white'
                    : 'text-slate-700 hover:bg-slate-100'
                }`}
              >
                {board.visibility === 'shared' ? <Users className="h-4 w-4" /> : <Lock className="h-4 w-4" />}
                <span className="min-w-0 flex-1 truncate">{board.title}</span>
                <span className="rounded-full bg-slate-200 px-1.5 py-0.5 text-xs text-slate-700">{board.notesCount}</span>
              </button>
            ))
          )}
        </div>

        {activeBoard?.visibility === 'personal' ? (
          <div className="border-t border-slate-200 p-3">
            <div className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
              <Users className="h-3.5 w-3.5" />
              Участники
            </div>
            <div className="space-y-1">
              {members.map((member) => (
                <div key={`${member.userId}-${member.role}`} className="flex items-center gap-2 rounded-md bg-slate-50 px-2 py-1.5 text-xs">
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-medium text-slate-800">{member.displayName}</div>
                    <div className="truncate text-slate-500">{member.role === 'owner' ? 'Владелец' : member.role === 'editor' ? 'Редактор' : 'Просмотр'}</div>
                  </div>
                  {activeBoard.canManageMembers && member.role !== 'owner' ? (
                    <button
                      type="button"
                      onClick={() => deleteMember(member)}
                      className="inline-flex h-6 w-6 items-center justify-center rounded border border-slate-200 text-slate-500 hover:bg-white"
                      title="Убрать участника"
                    >
                      <X className="h-3.5 w-3.5" />
                    </button>
                  ) : null}
                </div>
              ))}
            </div>
            {activeBoard.canManageMembers ? (
              <div className="mt-2 space-y-2">
                <select
                  aria-label="Сотрудник для доступа к доске"
                  value={memberUserId}
                  onChange={(event) => setMemberUserId(event.target.value)}
                  className="w-full rounded-md border border-slate-300 px-2 py-1.5 text-xs"
                >
                  <option value="">Выберите сотрудника</option>
                  {availableMemberCandidates.map((candidate) => (
                    <option key={candidate.userId} value={candidate.userId}>
                      {candidate.displayName}
                    </option>
                  ))}
                </select>
                <div className="grid grid-cols-[1fr_auto] gap-2">
                  <select
                    aria-label="Роль участника доски"
                    value={memberRole}
                    onChange={(event) => setMemberRole(event.target.value as Exclude<StickyNoteBoardMemberRole, 'owner'>)}
                    className="rounded-md border border-slate-300 px-2 py-1.5 text-xs"
                  >
                    <option value="viewer">Просмотр</option>
                    <option value="editor">Редактор</option>
                  </select>
                  <button
                    type="button"
                    onClick={upsertMember}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md bg-slate-900 text-white hover:bg-slate-800"
                    title="Добавить участника"
                  >
                    <UserPlus className="h-4 w-4" />
                  </button>
                </div>
              </div>
            ) : null}
          </div>
        ) : null}
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 bg-white px-3 py-2">
          <div className="min-w-0">
            <h1 className="truncate text-base font-semibold text-slate-950">{activeBoard?.title ?? 'Заметки'}</h1>
            <p className="text-xs text-slate-500">
              {activeBoard?.visibility === 'shared' ? 'Общая доска сотрудников' : 'Личная доска'}
            </p>
          </div>
          <div className="flex min-w-0 flex-1 items-center justify-end gap-2">
            <label className="relative min-w-[180px] max-w-xs flex-1">
              <Search className="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                className="w-full rounded-md border border-slate-300 py-2 pl-8 pr-2 text-sm"
                placeholder="Поиск"
              />
            </label>
            <label className="relative">
              <Filter className="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <select
                aria-label="Фильтр заметок"
                value={filter}
                onChange={(event) => setFilter(event.target.value as NoteFilter)}
                className="h-9 rounded-md border border-slate-300 bg-white pl-8 pr-2 text-sm"
                title="Фильтр"
              >
                {FILTER_OPTIONS.map((item) => (
                  <option key={item.value} value={item.value}>{item.label}</option>
                ))}
                {COLORS.map((item) => (
                  <option key={item.value} value={item.value}>{item.label}</option>
                ))}
              </select>
            </label>
            <button
              type="button"
              onClick={() => activeBoardId && loadNotes(activeBoardId)}
              className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-300 text-slate-700 hover:bg-slate-50"
              title="Обновить"
            >
              <RefreshCw className="h-4 w-4" />
            </button>
            {activeBoard?.canDelete ? (
              <button
                type="button"
                onClick={deleteBoard}
                className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-rose-200 text-rose-700 hover:bg-rose-50"
                title="Удалить доску"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => createNoteAt()}
              disabled={!canEditActiveBoard}
              className="inline-flex items-center gap-2 rounded-md bg-slate-900 px-3 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Plus className="h-4 w-4" />
              Заметка
            </button>
          </div>
        </div>

        <div
          ref={boardCanvasRef}
          onDoubleClick={handleCanvasDoubleClick}
          className={`relative min-h-0 flex-1 overflow-auto ${
            activeBoard?.background === 'gray'
              ? 'bg-[radial-gradient(circle_at_1px_1px,#cbd5e1_1px,transparent_0)] [background-size:18px_18px]'
              : 'bg-[radial-gradient(circle_at_1px_1px,#b45309_0.7px,transparent_0)] [background-color:#c48a4b] [background-size:12px_12px]'
          }`}
        >
          <div className="relative min-h-[900px] min-w-[1200px] p-3">
            {notesLoading ? (
              <div className="absolute left-4 top-4 rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                Загрузка заметок...
              </div>
            ) : null}

            {!notesLoading && notes.length === 0 ? (
              <div className="absolute left-4 top-4 rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                На доске пока нет заметок.
              </div>
            ) : null}

            {!notesLoading && notes.length > 0 && filteredNotes.length === 0 ? (
              <div className="absolute left-4 top-4 rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                По текущему поиску заметок нет.
              </div>
            ) : null}

            {filteredNotes.map((note) => (
              <div
                key={note.id}
                onPointerDown={(event) => startDrag(event, note)}
                onClick={() => setSelectedNoteId(note.id)}
                className={`absolute flex flex-col rounded-sm border p-2 shadow-md ${colorClass(note.color)} ${
                  selectedNoteId === note.id ? 'ring-2 ring-slate-900/40' : ''
                } ${note.isPinned ? 'cursor-default' : 'cursor-grab active:cursor-grabbing'}`}
                style={{
                  left: note.x,
                  top: note.y,
                  width: note.width,
                  height: note.height,
                  zIndex: note.zIndex,
                }}
              >
                <div className="mb-1 flex items-center gap-1">
                  <Palette className="h-3.5 w-3.5 text-slate-500" />
                  <select
                    value={note.color}
                    disabled={!canEditActiveBoard}
                    onChange={(event) => patchNote(note.id, { color: event.target.value as StickyNoteColor })}
                    onBlur={() => saveNote(note)}
                    className="min-w-0 flex-1 rounded border border-black/10 bg-white/60 px-1 py-0.5 text-xs"
                  >
                    {COLORS.map((item) => (
                      <option key={item.value} value={item.value}>{item.label}</option>
                    ))}
                  </select>
                  <button
                    type="button"
                    onClick={() => {
                      const next = { ...note, isPinned: !note.isPinned };
                      patchNote(note.id, { isPinned: next.isPinned });
                      void saveNote(next);
                    }}
                    disabled={!canEditActiveBoard}
                    className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-slate-700 hover:bg-white disabled:opacity-50"
                    title={note.isPinned ? 'Открепить' : 'Закрепить'}
                  >
                    {note.isPinned ? <Pin className="h-3.5 w-3.5" /> : <PinOff className="h-3.5 w-3.5" />}
                  </button>
                  <button
                    type="button"
                    onClick={() => saveNote(note)}
                    disabled={!canEditActiveBoard || savingNoteIds.has(note.id)}
                    className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-slate-700 hover:bg-white disabled:opacity-50"
                    title="Сохранить"
                  >
                    <Save className="h-3.5 w-3.5" />
                  </button>
                  <button
                    type="button"
                    onClick={() => deleteNote(note.id)}
                    disabled={!canEditActiveBoard}
                    className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-rose-700 hover:bg-white disabled:opacity-50"
                    title="Удалить"
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                </div>
                <textarea
                  value={note.text}
                  onChange={(event) => patchNote(note.id, { text: event.target.value })}
                  onBlur={() => saveNote(note)}
                  readOnly={!canEditActiveBoard}
                  maxLength={4000}
                  className="min-h-0 flex-1 resize-none bg-transparent text-sm leading-snug outline-none placeholder:text-slate-500"
                  placeholder="Текст заметки"
                />
                <div className="mt-1 flex items-center justify-between gap-2 text-[10px] text-slate-500">
                  <span className="truncate">{note.authorUserName || 'Автор не указан'}</span>
                  <span>{note.text.length}/4000</span>
                </div>
                {canEditActiveBoard ? (
                  <button
                    type="button"
                    onPointerDown={(event) => startResize(event, note)}
                    className="absolute bottom-1 right-1 h-4 w-4 cursor-se-resize rounded-sm border-b-2 border-r-2 border-slate-400/70"
                    title="Изменить размер"
                  />
                ) : null}
              </div>
            ))}
          </div>

          {deletedNoteId ? (
            <div className="sticky bottom-3 left-3 ml-3 inline-flex items-center gap-2 rounded-md border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 shadow-lg">
              <span>Заметка удалена</span>
              <button
                type="button"
                onClick={() => restoreDeletedNote(deletedNoteId)}
                className="inline-flex items-center gap-1 rounded-md bg-slate-900 px-2 py-1 text-xs font-medium text-white hover:bg-slate-800"
              >
                <Undo2 className="h-3.5 w-3.5" />
                Восстановить
              </button>
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}

export default StickyNotes;
