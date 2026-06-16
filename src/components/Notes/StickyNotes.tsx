import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent } from 'react';
import {
  Lock,
  Palette,
  Pin,
  PinOff,
  Plus,
  RefreshCw,
  Save,
  StickyNote,
  Trash2,
  Users,
} from 'lucide-react';
import { apiService, type StickyNoteBoard, type StickyNoteColor, type StickyNoteItem } from '../../services/api';
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

function colorClass(color: StickyNoteColor): string {
  return COLORS.find((item) => item.value === color)?.className ?? COLORS[0].className;
}

function sortNotes(notes: StickyNoteItem[]) {
  return [...notes].sort((a, b) => a.zIndex - b.zIndex || a.id - b.id);
}

export function StickyNotes() {
  const toast = useToast();
  const connectionRef = useRef<HubConnection | null>(null);
  const [boards, setBoards] = useState<StickyNoteBoard[]>([]);
  const [activeBoardId, setActiveBoardId] = useState<number | null>(null);
  const [notes, setNotes] = useState<StickyNoteItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [notesLoading, setNotesLoading] = useState(false);
  const [newBoardTitle, setNewBoardTitle] = useState('');
  const [creatingBoard, setCreatingBoard] = useState(false);
  const [savingNoteIds, setSavingNoteIds] = useState<Set<number>>(new Set());

  const activeBoard = useMemo(
    () => boards.find((board) => board.id === activeBoardId) ?? null,
    [activeBoardId, boards],
  );

  const canEditActiveBoard = Boolean(activeBoard?.canEdit);

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

  useEffect(() => {
    void loadBoards();
  }, [loadBoards]);

  useEffect(() => {
    if (!activeBoardId) {
      setNotes([]);
      return;
    }

    void loadNotes(activeBoardId);
  }, [activeBoardId, loadNotes]);

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
      } else if ((event.type === 'noteCreated' || event.type === 'noteUpdated') && event.note) {
        setNotes((current) => {
          const exists = current.some((note) => note.id === event.note!.id);
          const next = exists
            ? current.map((note) => (note.id === event.note!.id ? event.note! : note))
            : [...current, event.note!];
          return sortNotes(next);
        });
      } else if (event.type === 'boardDeleted') {
        void loadBoards();
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
  }, [activeBoardId, loadBoards]);

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

  const createNote = async () => {
    if (!activeBoardId || !canEditActiveBoard) {
      return;
    }

    const offset = notes.length * 24;
    try {
      const note = await apiService.createStickyNote(activeBoardId, {
        text: '',
        color: 'yellow',
        x: 56 + (offset % 360),
        y: 56 + (offset % 240),
        ...DEFAULT_NOTE_SIZE,
      });
      setNotes((current) => sortNotes([...current, note]));
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
      toast.error('Заметка изменилась в другой вкладке. Доска обновлена.');
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

  const deleteNote = async (noteId: number) => {
    if (!canEditActiveBoard || !window.confirm('Удалить заметку?')) {
      return;
    }

    try {
      await apiService.deleteStickyNote(noteId);
      setNotes((current) => current.filter((note) => note.id !== noteId));
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

  const startDrag = (event: PointerEvent<HTMLDivElement>, note: StickyNoteItem) => {
    if (!canEditActiveBoard || note.isPinned) {
      return;
    }

    const target = event.target as HTMLElement;
    if (target.closest('textarea,button,select,input')) {
      return;
    }

    event.currentTarget.setPointerCapture(event.pointerId);
    const startX = event.clientX;
    const startY = event.clientY;
    const initial = { x: note.x, y: note.y };
    const topZIndex = Math.max(1, ...notes.map((item) => item.zIndex)) + 1;
    patchNote(note.id, { zIndex: topZIndex });

    const handleMove = (moveEvent: globalThis.PointerEvent) => {
      const nextX = Math.max(0, initial.x + moveEvent.clientX - startX);
      const nextY = Math.max(0, initial.y + moveEvent.clientY - startY);
      patchNote(note.id, { x: nextX, y: nextY, zIndex: topZIndex });
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

  const notesRef = useRef(notes);
  useEffect(() => {
    notesRef.current = notes;
  }, [notes]);

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
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center justify-between gap-3 border-b border-slate-200 bg-white px-3 py-2">
          <div className="min-w-0">
            <h1 className="truncate text-base font-semibold text-slate-950">{activeBoard?.title ?? 'Заметки'}</h1>
            <p className="text-xs text-slate-500">
              {activeBoard?.visibility === 'shared' ? 'Общая доска сотрудников' : 'Личная доска'}
            </p>
          </div>
          <div className="flex items-center gap-2">
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
              onClick={createNote}
              disabled={!canEditActiveBoard}
              className="inline-flex items-center gap-2 rounded-md bg-slate-900 px-3 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Plus className="h-4 w-4" />
              Заметка
            </button>
          </div>
        </div>

        <div
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

            {notes.map((note) => (
              <div
                key={note.id}
                onPointerDown={(event) => startDrag(event, note)}
                className={`absolute flex flex-col rounded-sm border p-2 shadow-md ${colorClass(note.color)} ${
                  note.isPinned ? 'cursor-default' : 'cursor-grab active:cursor-grabbing'
                }`}
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
                  className="min-h-0 flex-1 resize-none bg-transparent text-sm leading-snug outline-none placeholder:text-slate-500"
                  placeholder="Текст заметки"
                />
                <div className="mt-1 truncate text-[10px] text-slate-500">
                  {note.authorUserName || 'Автор не указан'}
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}

export default StickyNotes;
