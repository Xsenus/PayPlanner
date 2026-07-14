import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent } from 'react';
import {
  Bold,
  Check,
  CalendarClock,
  Filter,
  Eye,
  Italic,
  LayoutGrid,
  List,
  ListChecks,
  Lock,
  Maximize2,
  Palette,
  Pin,
  PinOff,
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings2,
  StickyNote,
  Trash2,
  Underline,
  Undo2,
  UserCheck,
  UserPlus,
  Users,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { apiService, StickyNoteConflictError, type StickyNoteBoard, type StickyNoteBoardBackground, type StickyNoteBoardMember, type StickyNoteBoardMemberCandidate, type StickyNoteBoardMemberRole, type StickyNoteColor, type StickyNoteItem } from '../../services/api';
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
import { useDialog } from '../../hooks/useDialog';
import type { HubConnection } from '@microsoft/signalr';
import { copyTextToClipboard, getEffectiveBoardNotesCount } from './stickyNotesUtils';

const COLORS: Array<{ value: StickyNoteColor; label: string; className: string; swatchClassName: string; swatchTextClassName: string }> = [
  { value: 'yellow', label: 'Желтая', className: 'bg-yellow-300 border-yellow-500 text-slate-950', swatchClassName: 'bg-yellow-400 border-yellow-600', swatchTextClassName: 'text-yellow-950' },
  { value: 'orange', label: 'Оранжевая', className: 'bg-orange-300 border-orange-500 text-slate-950', swatchClassName: 'bg-orange-400 border-orange-600', swatchTextClassName: 'text-orange-950' },
  { value: 'blue', label: 'Синяя', className: 'bg-blue-300 border-blue-500 text-slate-950', swatchClassName: 'bg-blue-400 border-blue-600', swatchTextClassName: 'text-blue-950' },
  { value: 'cyan', label: 'Бирюзовая', className: 'bg-cyan-300 border-cyan-500 text-slate-950', swatchClassName: 'bg-cyan-400 border-cyan-600', swatchTextClassName: 'text-cyan-950' },
  { value: 'green', label: 'Зеленая', className: 'bg-emerald-300 border-emerald-500 text-slate-950', swatchClassName: 'bg-emerald-400 border-emerald-600', swatchTextClassName: 'text-emerald-950' },
  { value: 'purple', label: 'Фиолетовая', className: 'bg-violet-300 border-violet-500 text-slate-950', swatchClassName: 'bg-violet-400 border-violet-600', swatchTextClassName: 'text-violet-950' },
  { value: 'pink', label: 'Розовая', className: 'bg-rose-300 border-rose-500 text-slate-950', swatchClassName: 'bg-rose-400 border-rose-600', swatchTextClassName: 'text-rose-950' },
  { value: 'white', label: 'Белая', className: 'bg-white border-slate-200 text-slate-950', swatchClassName: 'bg-white border-slate-400', swatchTextClassName: 'text-slate-700' },
];

const DEFAULT_NOTE_SIZE = { width: 220, height: 180 };
const NOTE_SIZE_PRESETS = [
  { width: 220, height: 180, label: 'Компактный' },
  { width: 300, height: 240, label: 'Средний' },
  { width: 400, height: 320, label: 'Большой' },
] as const;
const MIN_NOTE_FONT_SIZE = 10;
const MAX_NOTE_FONT_SIZE = 28;
const NOTE_OPACITY_OPTIONS = [70, 85, 100] as const;
const MAX_NOTES_PER_BOARD = 200;
const FILTER_OPTIONS = [
  { value: 'all', label: 'Все' },
  { value: 'mine', label: 'Мои' },
  { value: 'recent', label: 'Недавние' },
  { value: 'pinned', label: 'Закрепленные' },
] as const;

type NoteFilter = typeof FILTER_OPTIONS[number]['value'] | StickyNoteColor;
type NoteConflict = {
  local: StickyNoteItem;
  server: StickyNoteItem;
};
type NotesViewMode = 'canvas' | 'list';

const NOTES_VIEW_MODE_STORAGE_KEY = 'payplanner.stickyNotes.viewMode';
const NOTES_ACTIVE_BOARD_STORAGE_KEY = 'payplanner.stickyNotes.activeBoardId';

function colorMeta(color: StickyNoteColor) {
  return COLORS.find((item) => item.value === color) ?? COLORS[0];
}

function colorClass(color: StickyNoteColor): string {
  return colorMeta(color).className;
}

function colorSwatchClass(color: StickyNoteColor): string {
  return colorMeta(color).swatchClassName;
}

function sortNotes(notes: StickyNoteItem[]) {
  return [...notes].sort((a, b) => a.zIndex - b.zIndex || a.id - b.id);
}

function formatDateTimeLocal(value?: string | null) {
  if (!value) {
    return '';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const pad = (part: number) => part.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toUtcIsoFromLocalInput(value: string) {
  if (!value) {
    return null;
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function isInteractiveTarget(target: EventTarget | null) {
  return target instanceof Element && Boolean(target.closest('textarea,button,select,input'));
}

function readStoredViewMode(): NotesViewMode {
  if (typeof window === 'undefined') {
    return 'canvas';
  }

  try {
    const stored = window.localStorage.getItem(NOTES_VIEW_MODE_STORAGE_KEY);
    return stored === 'list' || stored === 'canvas' ? stored : 'canvas';
  } catch {
    return 'canvas';
  }
}

function readStoredActiveBoardId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    const stored = window.localStorage.getItem(NOTES_ACTIVE_BOARD_STORAGE_KEY);
    const parsed = Number(stored);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
  } catch {
    return null;
  }
}

function writeStoredValue(key: string, value: string) {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Local storage is a convenience; the notes themselves stay server-backed.
  }
}

function removeStoredValue(key: string) {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.removeItem(key);
  } catch {
    // Local storage is a convenience; the notes themselves stay server-backed.
  }
}

export function StickyNotes() {
  const toast = useToast();
  const { confirm } = useDialog();
  const { user } = useAuth();
  const connectionRef = useRef<HubConnection | null>(null);
  const boardCanvasRef = useRef<HTMLDivElement | null>(null);
  const notesRef = useRef<StickyNoteItem[]>([]);
  const newBoardTitleRef = useRef('');
  const activeBoardIdRef = useRef<number | null>(null);
  const autosaveTimersRef = useRef<Map<number, number>>(new Map());
  const noteEditRevisionsRef = useRef<Map<number, number>>(new Map());
  const noteSavesInFlightRef = useRef<Set<number>>(new Set());
  const pendingNoteSavesRef = useRef<Map<number, StickyNoteItem>>(new Map());
  const textareaRefsRef = useRef<Map<number, HTMLTextAreaElement>>(new Map());
  const boardLoadRequestIdRef = useRef(0);
  const notesLoadRequestIdRef = useRef(0);
  const membersLoadRequestIdRef = useRef(0);
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
  const [colorPaletteNoteId, setColorPaletteNoteId] = useState<number | null>(null);
  const [formatPanelNoteId, setFormatPanelNoteId] = useState<number | null>(null);
  const [memberUserId, setMemberUserId] = useState('');
  const [memberRole, setMemberRole] = useState<Exclude<StickyNoteBoardMemberRole, 'owner'>>('viewer');
  const [noteConflict, setNoteConflict] = useState<NoteConflict | null>(null);
  const [viewMode, setViewMode] = useState<NotesViewMode>(() => readStoredViewMode());
  const [boardSettingsOpen, setBoardSettingsOpen] = useState(false);
  const [boardTitle, setBoardTitle] = useState('');
  const [boardDescription, setBoardDescription] = useState('');
  const [boardBackground, setBoardBackground] = useState<StickyNoteBoardBackground>('cork');
  const [savingBoardSettings, setSavingBoardSettings] = useState(false);

  const activeBoard = useMemo(
    () => boards.find((board) => board.id === activeBoardId) ?? null,
    [activeBoardId, boards],
  );

  const canEditActiveBoard = Boolean(activeBoard?.canEdit);
  const canCreateNotesOnActiveBoard = Boolean(activeBoard?.canCreateNotes);
  const canUpdateActiveBoardSettings = Boolean(activeBoard?.canUpdateSettings);
  const canDeleteNotesOnActiveBoard = Boolean(activeBoard?.canDeleteNotes);
  const effectiveActiveBoardNotesCount = activeBoard ? getEffectiveBoardNotesCount(activeBoard.notesCount, notes.length) : notes.length;
  const isActiveBoardAtNoteLimit = effectiveActiveBoardNotesCount >= MAX_NOTES_PER_BOARD;
  const canAddNoteToActiveBoard = canCreateNotesOnActiveBoard && !isActiveBoardAtNoteLimit;
  const currentUserId = user?.id ?? null;

  const getDisplayedBoardNotesCount = (board: StickyNoteBoard) => {
    if (board.id !== activeBoardId) {
      return board.notesCount;
    }

    return getEffectiveBoardNotesCount(board.notesCount, notes.length);
  };

  const clearAutosaveTimer = useCallback((noteId: number) => {
    const timer = autosaveTimersRef.current.get(noteId);
    if (timer) {
      window.clearTimeout(timer);
      autosaveTimersRef.current.delete(noteId);
    }
  }, []);

  const clearNoteTransientState = useCallback((noteId: number) => {
    clearAutosaveTimer(noteId);
    noteEditRevisionsRef.current.delete(noteId);
    noteSavesInFlightRef.current.delete(noteId);
    pendingNoteSavesRef.current.delete(noteId);
    setSelectedNoteId((current) => (current === noteId ? null : current));
    setDeletedNoteId((current) => (current === noteId ? null : current));
    setColorPaletteNoteId((current) => (current === noteId ? null : current));
    setFormatPanelNoteId((current) => (current === noteId ? null : current));
    textareaRefsRef.current.delete(noteId);
    setNoteConflict((current) => (
      current?.local.id === noteId || current?.server.id === noteId ? null : current
    ));
    setSavingNoteIds((current) => {
      if (!current.has(noteId)) {
        return current;
      }

      const next = new Set(current);
      next.delete(noteId);
      return next;
    });
  }, [clearAutosaveTimer]);

  const clearActiveBoardState = useCallback(() => {
    for (const timer of autosaveTimersRef.current.values()) {
      window.clearTimeout(timer);
    }
    autosaveTimersRef.current.clear();
    noteEditRevisionsRef.current.clear();
    noteSavesInFlightRef.current.clear();
    pendingNoteSavesRef.current.clear();
    setSavingNoteIds(new Set());
    setNotes([]);
    setMembers([]);
    setSelectedNoteId(null);
    setDeletedNoteId(null);
    setColorPaletteNoteId(null);
    setFormatPanelNoteId(null);
    textareaRefsRef.current.clear();
    setNoteConflict(null);
    setBoardSettingsOpen(false);
  }, []);

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

  useEffect(() => {
    if (!memberUserId) {
      return;
    }

    const selectedCandidateStillAvailable = availableMemberCandidates.some(
      (candidate) => String(candidate.userId) === memberUserId,
    );
    if (!selectedCandidateStillAvailable) {
      setMemberUserId('');
    }
  }, [availableMemberCandidates, memberUserId]);

  const loadBoards = useCallback(async () => {
    const requestId = boardLoadRequestIdRef.current + 1;
    boardLoadRequestIdRef.current = requestId;
    setLoading(true);
    try {
      const nextBoards = await apiService.getStickyNoteBoards();
      if (boardLoadRequestIdRef.current !== requestId) {
        return activeBoardIdRef.current;
      }

      const currentBoardId = activeBoardIdRef.current;
      if (currentBoardId && !nextBoards.some((board) => board.id === currentBoardId)) {
        clearActiveBoardState();
      }

      const storedBoardId = readStoredActiveBoardId();
      const nextActiveBoardId = currentBoardId && nextBoards.some((board) => board.id === currentBoardId)
        ? currentBoardId
        : storedBoardId && nextBoards.some((board) => board.id === storedBoardId)
          ? storedBoardId
          : nextBoards[0]?.id ?? null;

      setBoards(nextBoards);
      setActiveBoardId(nextActiveBoardId);
      return nextActiveBoardId;
    } catch (error) {
      console.error(error);
      if (boardLoadRequestIdRef.current !== requestId) {
        return activeBoardIdRef.current;
      }

      toast.error('Не удалось загрузить доски заметок.');
      return activeBoardIdRef.current;
    } finally {
      if (boardLoadRequestIdRef.current === requestId) {
        setLoading(false);
      }
    }
  }, [clearActiveBoardState, toast]);

  const loadNotes = useCallback(async (boardId: number) => {
    const requestId = notesLoadRequestIdRef.current + 1;
    notesLoadRequestIdRef.current = requestId;
    setNotesLoading(true);
    try {
      const nextNotes = await apiService.getStickyNotes(boardId);
      if (notesLoadRequestIdRef.current !== requestId || activeBoardIdRef.current !== boardId) {
        return;
      }

      setNotes(sortNotes(nextNotes));
    } catch (error) {
      console.error(error);
      if (notesLoadRequestIdRef.current === requestId && activeBoardIdRef.current === boardId) {
        toast.error('Не удалось загрузить заметки на доске.');
      }
    } finally {
      if (notesLoadRequestIdRef.current === requestId && activeBoardIdRef.current === boardId) {
        setNotesLoading(false);
      }
    }
  }, [toast]);

  const loadMembers = useCallback(async (boardId: number) => {
    const requestId = membersLoadRequestIdRef.current + 1;
    membersLoadRequestIdRef.current = requestId;
    try {
      const nextMembers = await apiService.getStickyNoteBoardMembers(boardId);
      if (membersLoadRequestIdRef.current !== requestId || activeBoardIdRef.current !== boardId) {
        return;
      }

      setMembers(nextMembers);
    } catch (error) {
      console.error(error);
      if (membersLoadRequestIdRef.current === requestId && activeBoardIdRef.current === boardId) {
        setMembers([]);
      }
    }
  }, []);

  useEffect(() => {
    void loadBoards();
  }, [loadBoards]);

  useEffect(() => {
    notesRef.current = notes;
  }, [notes]);

  useEffect(() => {
    newBoardTitleRef.current = newBoardTitle;
  }, [newBoardTitle]);

  useEffect(() => {
    setBoardTitle(activeBoard?.title ?? '');
    setBoardDescription(activeBoard?.description ?? '');
    setBoardBackground(activeBoard?.background ?? 'cork');
  }, [activeBoard?.background, activeBoard?.description, activeBoard?.id, activeBoard?.title]);

  useEffect(() => {
    writeStoredValue(NOTES_VIEW_MODE_STORAGE_KEY, viewMode);
  }, [viewMode]);

  useEffect(() => {
    if (activeBoardId) {
      writeStoredValue(NOTES_ACTIVE_BOARD_STORAGE_KEY, String(activeBoardId));
    } else if (!loading) {
      removeStoredValue(NOTES_ACTIVE_BOARD_STORAGE_KEY);
    }
  }, [activeBoardId, loading]);

  useEffect(() => {
    activeBoardIdRef.current = activeBoardId;
  }, [activeBoardId]);

  useEffect(() => {
    const timers = autosaveTimersRef.current;
    return () => {
      for (const timer of timers.values()) {
        window.clearTimeout(timer);
      }
      timers.clear();
    };
  }, []);

  useEffect(() => {
    if (!activeBoardId) {
      clearActiveBoardState();
      setNotesLoading(false);
      return;
    }

    clearActiveBoardState();
    void loadNotes(activeBoardId);
    void loadMembers(activeBoardId);
  }, [activeBoardId, clearActiveBoardState, loadMembers, loadNotes]);

  useEffect(() => {
    if (!activeBoard?.canManageMembers) {
      setMemberCandidates([]);
      setMemberUserId('');
      return;
    }

    let cancelled = false;
    const boardId = activeBoard.id;

    void apiService.getStickyNoteMemberCandidates()
      .then((candidates) => {
        if (!cancelled && activeBoardIdRef.current === boardId) {
          setMemberCandidates(candidates);
        }
      })
      .catch((error) => {
        console.error(error);
        if (!cancelled && activeBoardIdRef.current === boardId) {
          setMemberCandidates([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [activeBoard?.canManageMembers, activeBoard?.id]);

  useEffect(() => {
    if (!canStartNotesHubConnection()) {
      return;
    }

    let cancelled = false;
    const connection = createNotesHubConnection();
    connectionRef.current = connection;

    const handler: StickyNotesRealtimeHandler = (event) => {
      if (event.boardId !== activeBoardId) {
        void loadBoards();
        return;
      }

      if (event.type === 'noteDeleted' && event.noteId) {
        clearNoteTransientState(event.noteId);
        setNotes((current) => current.filter((note) => note.id !== event.noteId));
      } else if ((event.type === 'noteCreated' || event.type === 'noteUpdated' || event.type === 'noteRestored') && event.note) {
        if (event.note.boardId !== activeBoardId) {
          void loadBoards();
          return;
        }

        if (event.type === 'noteUpdated' && event.actorUserId === currentUserId) {
          return;
        }

        if (event.type === 'noteUpdated' && (autosaveTimersRef.current.has(event.note.id) || noteSavesInFlightRef.current.has(event.note.id))) {
          const localNote = notesRef.current.find((note) => note.id === event.note!.id);
          if (localNote) {
            clearAutosaveTimer(event.note.id);
            setNoteConflict({ local: localNote, server: event.note });
            toast.error('Заметка уже изменена другим пользователем. Проверьте конфликт перед сохранением.');
            return;
          }
        }

        if (event.type === 'noteRestored') {
          setDeletedNoteId((current) => (current === event.note!.id ? null : current));
        }

        setNotes((current) => {
          const exists = current.some((note) => note.id === event.note!.id);
          const next = exists
            ? current.map((note) => (note.id === event.note!.id ? event.note! : note))
            : [...current, event.note!];
          return sortNotes(next);
        });
      } else if (event.type === 'boardDeleted') {
        clearActiveBoardState();
        void loadBoards();
      } else if (event.type === 'boardMembersChanged' || event.type === 'boardUpdated') {
        void loadBoards().then((nextActiveBoardId) => {
          if (nextActiveBoardId === event.boardId) {
            void loadMembers(nextActiveBoardId);
          }
        });
      }
    };

    connection.on(STICKY_NOTE_BOARD_CHANGED_EVENT, handler);
    void startNotesHubConnection(connection).then((started) => {
      if (!cancelled && started && activeBoardId) {
        void joinNotesBoard(connection, activeBoardId);
      }
    });

    return () => {
      cancelled = true;
      connection.off(STICKY_NOTE_BOARD_CHANGED_EVENT, handler);
      connectionRef.current = null;
      void stopNotesHubConnection(connection);
    };
  }, [activeBoardId, clearActiveBoardState, clearAutosaveTimer, clearNoteTransientState, currentUserId, loadBoards, loadMembers, toast]);

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
    const requestId = boardLoadRequestIdRef.current + 1;
    boardLoadRequestIdRef.current = requestId;
    try {
      const nextBoards = await apiService.getStickyNoteBoards();
      if (boardLoadRequestIdRef.current !== requestId) {
        return;
      }

      const currentBoardId = activeBoardIdRef.current;
      if (currentBoardId && !nextBoards.some((board) => board.id === currentBoardId)) {
        clearActiveBoardState();
      }

      setBoards(nextBoards);
      setActiveBoardId((current) => (
        current && nextBoards.some((board) => board.id === current)
          ? current
          : nextBoards[0]?.id ?? null
      ));
    } catch {
      // The next manual refresh will recover counts.
    } finally {
      if (boardLoadRequestIdRef.current === requestId) {
        setLoading(false);
      }
    }
  }, [clearActiveBoardState]);

  const createBoard = async (visibility: 'personal' | 'shared') => {
    const title = newBoardTitle.trim();
    if (!title) {
      toast.error('Введите название доски.');
      return;
    }

    const requestedActiveBoardId = activeBoardIdRef.current;

    setCreatingBoard(true);
    try {
      const board = await apiService.createStickyNoteBoard({
        title,
        visibility,
        background: visibility === 'shared' ? 'cork' : 'gray',
      });
      setBoards((current) => (
        current.some((item) => item.id === board.id)
          ? current.map((item) => (item.id === board.id ? board : item))
          : [...current, board]
      ));
      if (activeBoardIdRef.current === requestedActiveBoardId) {
        setActiveBoardId(board.id);
      }
      setNewBoardTitle((current) => (current.trim() === title ? '' : current));
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current === requestedActiveBoardId && newBoardTitleRef.current.trim() === title) {
        toast.error('Не удалось создать доску.');
      }
    } finally {
      setCreatingBoard(false);
    }
  };

  const createNoteAt = async (x?: number, y?: number) => {
    if (!activeBoardId || !canCreateNotesOnActiveBoard) {
      return;
    }

    const requestedBoardId = activeBoardId;

    if (isActiveBoardAtNoteLimit) {
      toast.error(`На одной доске можно хранить до ${MAX_NOTES_PER_BOARD} заметок.`);
      return;
    }

    const offset = notes.length * 24;
    try {
      const note = await apiService.createStickyNote(requestedBoardId, {
        text: '',
        color: 'yellow',
        x: Math.max(0, x ?? 56 + (offset % 360)),
        y: Math.max(0, y ?? 56 + (offset % 240)),
        ...DEFAULT_NOTE_SIZE,
        fontSize: 14,
        isBold: false,
        isItalic: false,
        isUnderline: false,
        opacity: 100,
        assignedUserId: null,
        reminderAt: null,
      });
      if (activeBoardIdRef.current !== requestedBoardId) {
        await refreshBoardCounts();
        return;
      }
      setNotes((current) => {
        const exists = current.some((item) => item.id === note.id);
        const next = exists
          ? current.map((item) => (item.id === note.id ? note : item))
          : [...current, note];
        return sortNotes(next);
      });
      setSelectedNoteId(note.id);
      await refreshBoardCounts();
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось создать заметку.');
      }
    }
  };

  const saveNote = async (note: StickyNoteItem) => {
    if (!canEditActiveBoard) {
      return;
    }

    const requestedBoardId = note.boardId;

    const timer = autosaveTimersRef.current.get(note.id);
    if (timer) {
      window.clearTimeout(timer);
      autosaveTimersRef.current.delete(note.id);
    }

    if (noteSavesInFlightRef.current.has(note.id)) {
      pendingNoteSavesRef.current.set(note.id, note);
      return;
    }

    noteSavesInFlightRef.current.add(note.id);
    const savedRevision = noteEditRevisionsRef.current.get(note.id) ?? 0;

    setSavingNoteIds((current) => new Set(current).add(note.id));
    try {
      const saved = await apiService.updateStickyNote(note.id, {
        text: note.text,
        color: note.color,
        x: note.x,
        y: note.y,
        width: note.width,
        height: note.height,
        fontSize: note.fontSize,
        isBold: note.isBold,
        isItalic: note.isItalic,
        isUnderline: note.isUnderline,
        opacity: note.opacity,
        zIndex: note.zIndex,
        isPinned: note.isPinned,
        assignedUserId: note.assignedUserId ?? null,
        reminderAt: note.reminderAt ?? null,
        version: note.version,
      });
      if (activeBoardIdRef.current !== requestedBoardId || saved.boardId !== requestedBoardId) {
        return;
      }

      const currentNote = notesRef.current.find((item) => item.id === saved.id);
      const currentRevision = noteEditRevisionsRef.current.get(saved.id) ?? 0;
      const nextNote = currentNote && currentRevision !== savedRevision
        ? {
          ...saved,
          text: currentNote.text,
          color: currentNote.color,
          x: currentNote.x,
          y: currentNote.y,
          width: currentNote.width,
          height: currentNote.height,
          fontSize: currentNote.fontSize,
          isBold: currentNote.isBold,
          isItalic: currentNote.isItalic,
          isUnderline: currentNote.isUnderline,
          opacity: currentNote.opacity,
          zIndex: currentNote.zIndex,
          isPinned: currentNote.isPinned,
          assignedUserId: currentNote.assignedUserId,
          assignedUserName: currentNote.assignedUserName,
          reminderAt: currentNote.reminderAt,
          reminderSentAt: currentNote.reminderSentAt,
        }
        : saved;
      const nextNotes = sortNotes(notesRef.current.map((item) => (item.id === saved.id ? nextNote : item)));
      notesRef.current = nextNotes;
      setNotes(nextNotes);
      setNoteConflict((current) => current?.local.id === saved.id ? null : current);
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current !== requestedBoardId) {
        return;
      }
      if (error instanceof StickyNoteConflictError) {
        if (error.serverNote.boardId !== requestedBoardId) {
          return;
        }
        setNoteConflict({ local: note, server: error.serverNote });
        setNotes((current) => sortNotes(current.map((item) => (item.id === note.id ? note : item))));
        toast.error('Заметка уже изменена другим пользователем. Проверьте конфликт перед сохранением.');
      } else {
        toast.error('Не удалось сохранить заметку.');
        if (activeBoardId) {
          await loadNotes(activeBoardId);
        }
      }
    } finally {
      noteSavesInFlightRef.current.delete(note.id);
      setSavingNoteIds((current) => {
        const next = new Set(current);
        next.delete(note.id);
        return next;
      });

      const pendingNote = pendingNoteSavesRef.current.get(note.id);
      pendingNoteSavesRef.current.delete(note.id);
      if (pendingNote && activeBoardIdRef.current === requestedBoardId) {
        const latest = notesRef.current.find((item) => item.id === note.id) ?? pendingNote;
        void saveNote(latest);
      }
    }
  };

  const scheduleNoteAutosave = (noteId: number) => {
    if (!canEditActiveBoard) {
      return;
    }

    const existing = autosaveTimersRef.current.get(noteId);
    if (existing) {
      window.clearTimeout(existing);
    }

    const timer = window.setTimeout(() => {
      autosaveTimersRef.current.delete(noteId);
      const latest = notesRef.current.find((note) => note.id === noteId);
      if (latest) {
        void saveNote(latest);
      }
    }, 1200);
    autosaveTimersRef.current.set(noteId, timer);
  };

  const saveNoteLayout = async (note: StickyNoteItem) => {
    if (!canEditActiveBoard) {
      return;
    }

    const requestedBoardId = note.boardId;

    try {
      const saved = await apiService.updateStickyNoteLayout(note.id, {
        x: note.x,
        y: note.y,
        width: note.width,
        height: note.height,
        zIndex: note.zIndex,
        version: note.version,
      });
      if (activeBoardIdRef.current !== requestedBoardId || saved.boardId !== requestedBoardId) {
        return;
      }
      setNotes((current) => sortNotes(current.map((item) => (item.id === saved.id ? saved : item))));
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current !== requestedBoardId) {
        return;
      }
      if (error instanceof StickyNoteConflictError) {
        if (error.serverNote.boardId !== requestedBoardId) {
          return;
        }
        setNoteConflict({ local: note, server: error.serverNote });
        setNotes((current) => sortNotes(current.map((item) => (item.id === note.id ? note : item))));
        toast.error('Расположение заметки уже изменено другим пользователем. Проверьте конфликт перед сохранением.');
      } else if (activeBoardId) {
        await loadNotes(activeBoardId);
      }
    }
  };

  const applyServerConflictVersion = () => {
    if (!noteConflict) {
      return;
    }

    setNotes((current) => sortNotes(current.map((note) => (note.id === noteConflict.server.id ? noteConflict.server : note))));
    setNoteConflict(null);
  };

  const copyConflictDraft = async () => {
    if (!noteConflict) {
      return;
    }

    const requestedBoardId = noteConflict.local.boardId;

    try {
      await copyTextToClipboard(noteConflict.local.text);
      if (activeBoardIdRef.current !== requestedBoardId) {
        return;
      }
      toast.success('Текст вашей версии скопирован.');
    } catch {
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось скопировать текст автоматически.');
      }
    }
  };

  const overwriteConflictVersion = async () => {
    if (!noteConflict || !canEditActiveBoard) {
      return;
    }

    const note = {
      ...noteConflict.local,
      version: noteConflict.server.version,
      x: noteConflict.server.x,
      y: noteConflict.server.y,
      width: noteConflict.server.width,
      height: noteConflict.server.height,
      zIndex: noteConflict.server.zIndex,
    };

    setNoteConflict(null);
    await saveNote(note);
  };

  const patchNote = (noteId: number, patch: Partial<StickyNoteItem>) => {
    noteEditRevisionsRef.current.set(noteId, (noteEditRevisionsRef.current.get(noteId) ?? 0) + 1);
    const nextNotes = sortNotes(notesRef.current.map((note) => (note.id === noteId ? { ...note, ...patch } : note)));
    notesRef.current = nextNotes;
    setNotes(nextNotes);
  };

  const updateNoteAppearance = (note: StickyNoteItem, patch: Partial<StickyNoteItem>) => {
    if (!canEditActiveBoard) {
      return;
    }

    patchNote(note.id, patch);
    const latest = notesRef.current.find((item) => item.id === note.id);
    if (latest) {
      void saveNote(latest);
    }
  };

  const setNoteFontSize = (note: StickyNoteItem, value: string) => {
    const fontSize = Number(value);
    if (Number.isInteger(fontSize) && fontSize >= MIN_NOTE_FONT_SIZE && fontSize <= MAX_NOTE_FONT_SIZE && fontSize !== note.fontSize) {
      updateNoteAppearance(note, { fontSize });
    }
  };

  const toggleChecklist = (note: StickyNoteItem) => {
    if (!canEditActiveBoard) {
      return;
    }

    const textarea = textareaRefsRef.current.get(note.id);
    const selectionStart = textarea?.selectionStart ?? 0;
    const selectionEnd = textarea?.selectionEnd ?? selectionStart;
    const lineStart = note.text.lastIndexOf('\n', Math.max(0, selectionStart - 1)) + 1;
    const nextBreak = note.text.indexOf('\n', selectionEnd);
    const lineEnd = nextBreak === -1 ? note.text.length : nextBreak;
    const selectedLines = note.text.slice(lineStart, lineEnd).split('\n');
    const shouldRemove = selectedLines.every((line) => /^[☐☑]\s?/.test(line));
    const transformedLines = selectedLines.map((line) => (
      shouldRemove ? line.replace(/^[☐☑]\s?/, '') : `☐ ${line}`
    ));
    const transformed = transformedLines.join('\n');
    const text = `${note.text.slice(0, lineStart)}${transformed}${note.text.slice(lineEnd)}`;
    patchNote(note.id, { text });
    scheduleNoteAutosave(note.id);

    window.requestAnimationFrame(() => {
      const current = textareaRefsRef.current.get(note.id);
      if (!current) {
        return;
      }
      current.focus();
      current.setSelectionRange(lineStart, lineStart + transformed.length);
    });
  };

  const cycleNoteSize = (note: StickyNoteItem) => {
    const currentIndex = NOTE_SIZE_PRESETS.findIndex((preset) => preset.width === note.width && preset.height === note.height);
    const nextPreset = NOTE_SIZE_PRESETS[(currentIndex + 1 + NOTE_SIZE_PRESETS.length) % NOTE_SIZE_PRESETS.length];
    updateNoteAppearance(note, { width: nextPreset.width, height: nextPreset.height });
  };

  const selectNoteColor = (note: StickyNoteItem, color: StickyNoteColor) => {
    setColorPaletteNoteId(null);
    if (!canEditActiveBoard || note.color === color) {
      return;
    }

    const next = { ...note, color };
    patchNote(note.id, { color });
    void saveNote(next);
  };

  const assignNote = (note: StickyNoteItem, rawUserId: string) => {
    if (!canEditActiveBoard) {
      return;
    }

    const assignedUserId = rawUserId ? Number(rawUserId) : null;
    const assignee = assignedUserId
      ? memberCandidates.find((candidate) => candidate.userId === assignedUserId)
      : null;
    const next = {
      ...note,
      assignedUserId,
      assignedUserName: assignee?.displayName ?? '',
      reminderAt: assignedUserId ? note.reminderAt ?? null : null,
      reminderSentAt: assignedUserId ? note.reminderSentAt ?? null : null,
    };
    patchNote(note.id, {
      assignedUserId: next.assignedUserId,
      assignedUserName: next.assignedUserName,
      reminderAt: next.reminderAt,
      reminderSentAt: next.reminderSentAt,
    });
    void saveNote(next);
  };

  const setNoteReminder = (note: StickyNoteItem, rawValue: string) => {
    if (!canEditActiveBoard || !note.assignedUserId) {
      return;
    }

    const reminderAt = toUtcIsoFromLocalInput(rawValue);
    const next = { ...note, reminderAt, reminderSentAt: null };
    patchNote(note.id, { reminderAt, reminderSentAt: null });
    void saveNote(next);
  };

  const restoreDeletedNote = useCallback(async (noteId: number) => {
    const requestedBoardId = activeBoardId;

    try {
      const restored = await apiService.restoreStickyNote(noteId);
      if (!requestedBoardId || activeBoardIdRef.current !== requestedBoardId || restored.boardId !== requestedBoardId) {
        await refreshBoardCounts();
        return;
      }
      setNotes((current) => sortNotes([...current.filter((note) => note.id !== restored.id), restored]));
      setDeletedNoteId(null);
      await refreshBoardCounts();
      toast.success('Заметка восстановлена.');
    } catch (error) {
      console.error(error);
      if (!requestedBoardId || activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось восстановить заметку.');
      }
    }
  }, [activeBoardId, refreshBoardCounts, toast]);

  const deleteNote = useCallback(async (noteId: number) => {
    if (!canDeleteNotesOnActiveBoard) {
      return;
    }

    const approved = await confirm({
      title: 'Удалить заметку?',
      description: 'Заметка будет удалена с текущей доски. После удаления ее можно будет быстро восстановить через уведомление.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    const requestedBoardId = activeBoardId;

    try {
      await apiService.deleteStickyNote(noteId);
      if (!requestedBoardId || activeBoardIdRef.current !== requestedBoardId) {
        await refreshBoardCounts();
        return;
      }
      clearNoteTransientState(noteId);
      setNotes((current) => current.filter((note) => note.id !== noteId));
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
      if (!requestedBoardId || activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось удалить заметку.');
      }
    }
  }, [activeBoardId, canDeleteNotesOnActiveBoard, clearNoteTransientState, confirm, refreshBoardCounts, restoreDeletedNote, toast]);

  const deleteBoard = async () => {
    if (!activeBoard?.canDelete) {
      return;
    }

    const approved = await confirm({
      title: 'Удалить доску?',
      description: 'Доска будет удалена вместе со всеми заметками на ней. Это действие нельзя отменить.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    const requestedBoardId = activeBoard.id;
    const fallbackBoardId = boards.filter((board) => board.id !== requestedBoardId)[0]?.id ?? null;

    try {
      await apiService.deleteStickyNoteBoard(requestedBoardId);
      setBoards((current) => current.filter((board) => board.id !== requestedBoardId));
      if (activeBoardIdRef.current === requestedBoardId) {
        clearActiveBoardState();
        setActiveBoardId(fallbackBoardId);
        toast.success('Доска заметок удалена.');
      }
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось удалить доску.');
      }
    }
  };

  const saveBoardSettings = async () => {
    if (!activeBoard || !canUpdateActiveBoardSettings) {
      return;
    }

    const title = boardTitle.trim();
    if (!title) {
      toast.error('Введите название доски.');
      return;
    }

    const requestedBoardId = activeBoard.id;

    setSavingBoardSettings(true);
    try {
      const updated = await apiService.updateStickyNoteBoard(requestedBoardId, {
        title,
        description: boardDescription.trim() || null,
        background: boardBackground,
      });
      setBoards((current) => current.map((board) => (board.id === updated.id ? updated : board)));
      if (activeBoardIdRef.current !== requestedBoardId) {
        return;
      }
      setBoardSettingsOpen(false);
      toast.success('Настройки доски сохранены.');
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось сохранить настройки доски.');
      }
    } finally {
      setSavingBoardSettings(false);
    }
  };

  const upsertMember = async () => {
    if (!activeBoard?.canManageMembers) {
      return;
    }

    const parsedUserId = Number(memberUserId);
    if (!Number.isInteger(parsedUserId) || parsedUserId <= 0) {
      toast.error('Выберите сотрудника.');
      return;
    }

    const requestedBoardId = activeBoard.id;

    try {
      const member = await apiService.upsertStickyNoteBoardMember(requestedBoardId, parsedUserId, { role: memberRole });
      if (activeBoardIdRef.current !== requestedBoardId) {
        await loadBoards();
        return;
      }
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
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось обновить участника доски.');
      }
    }
  };

  const deleteMember = async (member: StickyNoteBoardMember) => {
    if (!activeBoard?.canManageMembers || member.role === 'owner') {
      return;
    }

    const requestedBoardId = activeBoard.id;

    try {
      await apiService.deleteStickyNoteBoardMember(requestedBoardId, member.userId);
      if (activeBoardIdRef.current !== requestedBoardId) {
        await loadBoards();
        return;
      }
      setMembers((current) => current.filter((item) => item.userId !== member.userId));
      await loadBoards();
    } catch (error) {
      console.error(error);
      if (activeBoardIdRef.current === requestedBoardId) {
        toast.error('Не удалось убрать участника.');
      }
    }
  };

  const handleCanvasDoubleClick = (event: ReactMouseEvent<HTMLDivElement>) => {
    if (!canAddNoteToActiveBoard || isInteractiveTarget(event.target)) {
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
    if (!canEditActiveBoard) {
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

  const renderNote = (note: StickyNoteItem, mode: NotesViewMode) => {
    const isCanvas = mode === 'canvas';
    const currentColor = colorMeta(note.color);
    const isColorPaletteOpen = colorPaletteNoteId === note.id;
    const isFormatPanelOpen = formatPanelNoteId === note.id;
    const currentFontSize = note.fontSize ?? 14;
    const currentOpacity = note.opacity ?? 100;
    const currentSizeLabel = NOTE_SIZE_PRESETS.find((preset) => preset.width === note.width && preset.height === note.height)?.label
      ?? `${Math.round(note.width)}×${Math.round(note.height)}`;

    return (
      <div
        key={note.id}
        onPointerDown={isCanvas ? (event) => startDrag(event, note) : undefined}
        onDoubleClick={(event) => event.stopPropagation()}
        onClick={() => setSelectedNoteId(note.id)}
        className={`${isCanvas ? 'absolute' : 'relative min-h-[160px] w-full'} flex flex-col rounded-sm border p-2 shadow-md ${colorClass(note.color)} ${
          selectedNoteId === note.id ? 'ring-2 ring-slate-900/40' : ''
        } ${isCanvas && !note.isPinned ? 'cursor-grab active:cursor-grabbing' : 'cursor-default'}`}
        style={isCanvas
          ? {
            left: note.x,
            top: note.y,
            width: note.width,
            height: note.height,
            zIndex: note.zIndex,
            opacity: currentOpacity / 100,
          }
          : { opacity: currentOpacity / 100 }}
      >
        <div className="mb-1 flex items-center gap-1">
          <div className="relative shrink-0">
            <button
              type="button"
              onPointerDown={(event) => event.stopPropagation()}
              onClick={(event) => {
                event.stopPropagation();
                setFormatPanelNoteId(null);
                setColorPaletteNoteId((current) => (current === note.id ? null : note.id));
              }}
              disabled={!canEditActiveBoard}
              className="inline-flex h-6 w-9 items-center justify-center gap-1 rounded border border-black/10 bg-white/50 text-slate-700 hover:bg-white disabled:opacity-50"
              title={`Цвет заметки: ${currentColor.label}`}
              aria-label={`Выбрать цвет заметки: ${currentColor.label}`}
              aria-expanded={isColorPaletteOpen}
            >
              <Palette className="h-3 w-3" />
              <span className={`h-3 w-3 rounded-sm border ${colorSwatchClass(note.color)}`} />
            </button>
            {isColorPaletteOpen ? (
              <div
                className="absolute left-0 top-8 z-[90] w-[236px] rounded-lg border border-slate-200 bg-white p-2 shadow-xl"
                onPointerDown={(event) => event.stopPropagation()}
                onClick={(event) => event.stopPropagation()}
                role="group"
                aria-label="Палитра цветов заметки"
              >
                <div className="grid grid-cols-4 gap-2">
                  {COLORS.map((item) => {
                    const selected = note.color === item.value;
                    return (
                      <button
                        key={item.value}
                        type="button"
                        onClick={() => selectNoteColor(note, item.value)}
                        className={`flex h-12 flex-col items-center justify-center gap-1 rounded-md border text-[10px] font-medium transition ${item.swatchClassName} ${item.swatchTextClassName} ${
                          selected ? 'ring-2 ring-slate-900 ring-offset-1' : 'hover:ring-2 hover:ring-slate-400 hover:ring-offset-1'
                        }`}
                        title={item.label}
                        aria-label={`Цвет заметки: ${item.label}`}
                        aria-pressed={selected}
                      >
                        {selected ? <Check className="h-3.5 w-3.5" /> : <span className="h-3.5 w-3.5" />}
                        <span className="max-w-full truncate px-0.5">{item.label}</span>
                      </button>
                    );
                  })}
                </div>
              </div>
            ) : null}
          </div>
          <div className="relative shrink-0">
            <button
              type="button"
              onPointerDown={(event) => event.stopPropagation()}
              onClick={(event) => {
                event.stopPropagation();
                setColorPaletteNoteId(null);
                setFormatPanelNoteId((current) => (current === note.id ? null : note.id));
              }}
              disabled={!canEditActiveBoard}
              className={`inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 text-[9px] font-semibold transition hover:bg-white disabled:opacity-50 ${isFormatPanelOpen ? 'bg-slate-900 text-white' : 'bg-white/50 text-slate-700'}`}
              title="Оформление текста"
              aria-label="Открыть оформление текста"
              aria-expanded={isFormatPanelOpen}
            >
              Aa
            </button>
            {isFormatPanelOpen ? (
              <div
                className="absolute left-0 top-8 z-[90] w-[248px] rounded-lg border border-slate-200 bg-white p-2 text-slate-700 shadow-xl"
                onPointerDown={(event) => event.stopPropagation()}
                onClick={(event) => event.stopPropagation()}
                role="group"
                aria-label="Оформление текста заметки"
              >
                <div className="mb-2 flex items-center gap-1">
                  <button
                    type="button"
                    onClick={() => updateNoteAppearance(note, { isBold: !note.isBold })}
                    className={`inline-flex h-8 w-8 items-center justify-center rounded border border-slate-200 ${note.isBold ? 'bg-slate-900 text-white' : 'bg-white hover:bg-slate-50'}`}
                    title={note.isBold ? 'Обычный текст' : 'Жирный текст'}
                    aria-label={note.isBold ? 'Отключить жирный текст' : 'Включить жирный текст'}
                    aria-pressed={Boolean(note.isBold)}
                  >
                    <Bold className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    onClick={() => updateNoteAppearance(note, { isItalic: !note.isItalic })}
                    className={`inline-flex h-8 w-8 items-center justify-center rounded border border-slate-200 ${note.isItalic ? 'bg-slate-900 text-white' : 'bg-white hover:bg-slate-50'}`}
                    title={note.isItalic ? 'Обычное начертание' : 'Курсив'}
                    aria-label={note.isItalic ? 'Отключить курсив' : 'Включить курсив'}
                    aria-pressed={Boolean(note.isItalic)}
                  >
                    <Italic className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    onClick={() => updateNoteAppearance(note, { isUnderline: !note.isUnderline })}
                    className={`inline-flex h-8 w-8 items-center justify-center rounded border border-slate-200 ${note.isUnderline ? 'bg-slate-900 text-white' : 'bg-white hover:bg-slate-50'}`}
                    title={note.isUnderline ? 'Убрать подчёркивание' : 'Подчеркнуть текст'}
                    aria-label={note.isUnderline ? 'Убрать подчёркивание' : 'Подчеркнуть текст'}
                    aria-pressed={Boolean(note.isUnderline)}
                  >
                    <Underline className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    onPointerDown={(event) => event.preventDefault()}
                    onClick={() => toggleChecklist(note)}
                    className="inline-flex h-8 w-8 items-center justify-center rounded border border-slate-200 bg-white hover:bg-slate-50"
                    title="Добавить или убрать чек-лист для выбранных строк"
                    aria-label="Переключить чек-лист выбранных строк"
                  >
                    <ListChecks className="h-4 w-4" />
                  </button>
                  <select
                    value={currentFontSize}
                    onChange={(event) => setNoteFontSize(note, event.target.value)}
                    className="ml-auto h-8 rounded border border-slate-200 bg-white px-1 text-xs font-medium"
                    title="Размер текста"
                    aria-label="Размер текста заметки"
                  >
                    {[10, 12, 14, 16, 18, 20, 24, 28].map((size) => (
                      <option key={size} value={size}>{size} пт</option>
                    ))}
                  </select>
                </div>
                <div className="flex items-center gap-2 border-t border-slate-100 pt-2">
                  <Eye className="h-4 w-4 shrink-0 text-slate-500" />
                  <span className="text-[11px] font-medium">Прозрачность</span>
                  <div className="ml-auto inline-flex rounded border border-slate-200 p-0.5">
                    {NOTE_OPACITY_OPTIONS.map((opacity) => (
                      <button
                        key={opacity}
                        type="button"
                        onClick={() => updateNoteAppearance(note, { opacity })}
                        className={`h-6 rounded px-1.5 text-[10px] font-semibold ${currentOpacity === opacity ? 'bg-slate-900 text-white' : 'hover:bg-slate-100'}`}
                        aria-label={`Прозрачность ${opacity}%`}
                        aria-pressed={currentOpacity === opacity}
                      >
                        {opacity}%
                      </button>
                    ))}
                  </div>
                </div>
              </div>
            ) : null}
          </div>
          {isCanvas ? (
            <button
              type="button"
              onPointerDown={(event) => event.stopPropagation()}
              onClick={() => cycleNoteSize(note)}
              disabled={!canEditActiveBoard}
              className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-slate-700 transition hover:bg-white disabled:opacity-50"
              title={`Размер стикера: ${currentSizeLabel}. Нажмите, чтобы выбрать следующий.`}
              aria-label={`Изменить размер стикера. Сейчас: ${currentSizeLabel}`}
            >
              <Maximize2 className="h-3.5 w-3.5" />
            </button>
          ) : null}
          <button
            type="button"
            onPointerDown={(event) => event.stopPropagation()}
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
            onPointerDown={(event) => event.stopPropagation()}
            onClick={(event) => {
              event.stopPropagation();
              void saveNote(note);
            }}
            disabled={!canEditActiveBoard || savingNoteIds.has(note.id)}
            className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-slate-700 hover:bg-white disabled:opacity-50"
            title="Сохранить"
          >
            <Save className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            onPointerDown={(event) => event.stopPropagation()}
            onClick={(event) => {
              event.stopPropagation();
              void deleteNote(note.id);
            }}
            disabled={!canDeleteNotesOnActiveBoard}
            className="inline-flex h-6 w-6 items-center justify-center rounded border border-black/10 bg-white/50 text-rose-700 hover:bg-white disabled:opacity-50"
            title="Удалить"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        </div>
        <div className="mb-1 grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-1">
          <label className="flex min-w-0 items-center gap-1 rounded border border-black/10 bg-white/35 px-1.5 py-1 text-[10px] text-slate-600">
            <UserCheck className="h-3 w-3 shrink-0" />
            <select
              value={note.assignedUserId ?? ''}
              onPointerDown={(event) => event.stopPropagation()}
              onChange={(event) => assignNote(note, event.target.value)}
              disabled={!canEditActiveBoard}
              className="min-w-0 flex-1 bg-transparent text-[11px] text-slate-700 outline-none disabled:opacity-60"
              title={note.assignedUserName ? `Ответственный: ${note.assignedUserName}` : 'Ответственный не выбран'}
              aria-label="Ответственный за заметку"
            >
              <option value="">Без ответственного</option>
              {memberCandidates.map((candidate) => (
                <option key={candidate.userId} value={candidate.userId}>
                  {candidate.displayName}
                </option>
              ))}
            </select>
          </label>
          <label className="flex min-w-0 items-center gap-1 rounded border border-black/10 bg-white/35 px-1.5 py-1 text-[10px] text-slate-600">
            <CalendarClock className="h-3 w-3 shrink-0" />
            <input
              type="datetime-local"
              value={formatDateTimeLocal(note.reminderAt)}
              onPointerDown={(event) => event.stopPropagation()}
              onChange={(event) => setNoteReminder(note, event.target.value)}
              disabled={!canEditActiveBoard || !note.assignedUserId}
              className="min-w-0 flex-1 bg-transparent text-[11px] text-slate-700 outline-none disabled:opacity-60"
              title={note.reminderAt ? 'Срок напоминания' : 'Срок не указан'}
              aria-label="Срок напоминания"
            />
          </label>
        </div>
        <textarea
          ref={(element) => {
            if (element) {
              textareaRefsRef.current.set(note.id, element);
            } else {
              textareaRefsRef.current.delete(note.id);
            }
          }}
          value={note.text}
          onChange={(event) => {
            patchNote(note.id, { text: event.target.value });
            scheduleNoteAutosave(note.id);
          }}
          onKeyDown={(event) => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
              event.preventDefault();
              void saveNote({ ...note, text: event.currentTarget.value });
            }
          }}
          onBlur={(event) => saveNote({ ...note, text: event.currentTarget.value })}
          readOnly={!canEditActiveBoard}
          maxLength={4000}
          className={`min-h-0 flex-1 resize-none bg-transparent leading-snug outline-none placeholder:text-slate-500 ${note.isBold ? 'font-bold' : 'font-normal'} ${note.isItalic ? 'italic' : 'not-italic'} ${note.isUnderline ? 'underline underline-offset-2' : 'no-underline'}`}
          style={{ fontSize: `${currentFontSize}px` }}
          placeholder="Текст заметки"
        />
        <div className="mt-1 flex items-center justify-between gap-2 text-[10px] text-slate-500">
          <span className="truncate" title={`Создал: ${note.authorUserName || 'не указан'}`}>
            {note.lastUpdatedByUserName ? `Изменил: ${note.lastUpdatedByUserName}` : note.authorUserName || 'Автор не указан'}
          </span>
          <span>{note.text.length}/4000</span>
        </div>
        {canEditActiveBoard && isCanvas ? (
          <button
            type="button"
            onPointerDown={(event) => startResize(event, note)}
            className="absolute bottom-1 right-1 h-5 w-5 cursor-se-resize rounded-sm border-b-[3px] border-r-[3px] border-slate-600/70 bg-white/20 hover:bg-white/50"
            title="Изменить размер"
          />
        ) : null}
      </div>
    );
  };

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if ((event.key !== 'Delete' && event.key !== 'Backspace') || !selectedNoteId || !canDeleteNotesOnActiveBoard || isInteractiveTarget(event.target)) {
        return;
      }

      event.preventDefault();
      void deleteNote(selectedNoteId);
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedNoteId, canDeleteNotesOnActiveBoard, deleteNote]);

  return (
    <div className="flex h-[calc(100vh-4rem)] min-h-[620px] flex-col bg-slate-100 lg:flex-row">
      <aside className="flex w-full shrink-0 flex-col border-b border-slate-200 bg-white lg:w-72 lg:border-b-0 lg:border-r">
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
                <span className="rounded-full bg-slate-200 px-1.5 py-0.5 text-xs text-slate-700">{getDisplayedBoardNotesCount(board)}</span>
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
            <p className="truncate text-xs text-slate-500">
              {activeBoard?.description
                ? `${activeBoard.visibility === 'shared' ? 'Общая доска сотрудников' : 'Личная доска'} · ${activeBoard.description}`
                : activeBoard?.visibility === 'shared' ? 'Общая доска сотрудников' : 'Личная доска'}
            </p>
          </div>
          <div className="flex min-w-0 flex-1 items-center justify-end gap-2">
            <div className="inline-flex h-9 shrink-0 rounded-md border border-slate-300 bg-white p-0.5">
              <button
                type="button"
                onClick={() => setViewMode('canvas')}
                className={`inline-flex h-8 w-8 items-center justify-center rounded text-slate-700 ${
                  viewMode === 'canvas' ? 'bg-slate-900 text-white' : 'hover:bg-slate-50'
                }`}
                title="Холст"
                aria-label="Показать холст"
              >
                <LayoutGrid className="h-4 w-4" />
              </button>
              <button
                type="button"
                onClick={() => setViewMode('list')}
                className={`inline-flex h-8 w-8 items-center justify-center rounded text-slate-700 ${
                  viewMode === 'list' ? 'bg-slate-900 text-white' : 'hover:bg-slate-50'
                }`}
                title="Список"
                aria-label="Показать список"
              >
                <List className="h-4 w-4" />
              </button>
            </div>
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
            {canUpdateActiveBoardSettings || activeBoard?.canManageMembers ? (
              <button
                type="button"
                onClick={() => setBoardSettingsOpen((current) => !current)}
                className={`inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-300 text-slate-700 hover:bg-slate-50 ${
                  boardSettingsOpen ? 'bg-slate-900 text-white hover:bg-slate-800' : ''
                }`}
                title="Настройки доски"
                aria-label="Настройки доски"
              >
                <Settings2 className="h-4 w-4" />
              </button>
            ) : null}
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
              disabled={!canAddNoteToActiveBoard}
              className="inline-flex items-center gap-2 rounded-md bg-slate-900 px-3 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
              title={isActiveBoardAtNoteLimit ? `На одной доске можно хранить до ${MAX_NOTES_PER_BOARD} заметок.` : 'Создать заметку'}
            >
              <Plus className="h-4 w-4" />
              Заметка
            </button>
          </div>
        </div>

        {boardSettingsOpen && activeBoard && (canUpdateActiveBoardSettings || activeBoard.canManageMembers) ? (
          <div className="grid gap-2 border-b border-slate-200 bg-white px-3 py-2 text-sm lg:grid-cols-[minmax(180px,1fr)_180px_auto]">
            <label className="min-w-0">
              <span className="sr-only">Название доски</span>
              <input
                value={boardTitle}
                onChange={(event) => setBoardTitle(event.target.value)}
                disabled={!canUpdateActiveBoardSettings}
                className="h-9 w-full rounded-md border border-slate-300 px-2 text-sm"
                maxLength={120}
                placeholder="Название доски"
              />
            </label>
            <label className="min-w-0 lg:col-span-3">
              <span className="sr-only">Описание доски</span>
              <textarea
                value={boardDescription}
                onChange={(event) => setBoardDescription(event.target.value)}
                disabled={!canUpdateActiveBoardSettings}
                className="min-h-16 w-full resize-y rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                maxLength={500}
                placeholder="Короткое описание доски"
              />
            </label>
            <label className="min-w-0">
              <span className="sr-only">Фон доски</span>
              <select
                aria-label="Фон доски"
                value={boardBackground}
                onChange={(event) => setBoardBackground(event.target.value as StickyNoteBoardBackground)}
                disabled={!canUpdateActiveBoardSettings}
                className="h-9 w-full rounded-md border border-slate-300 bg-white px-2 text-sm"
              >
                <option value="cork">Пробковая доска</option>
                <option value="gray">Серый фон</option>
              </select>
            </label>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={saveBoardSettings}
                disabled={savingBoardSettings || !canUpdateActiveBoardSettings}
                className="inline-flex h-9 items-center gap-1 rounded-md bg-slate-900 px-3 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-60"
              >
                <Save className="h-4 w-4" />
                Сохранить
              </button>
              <button
                type="button"
                onClick={() => setBoardSettingsOpen(false)}
                className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-300 text-slate-700 hover:bg-slate-50"
                title="Закрыть настройки"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </div>
        ) : null}

        <div
          ref={boardCanvasRef}
          onDoubleClick={viewMode === 'canvas' ? handleCanvasDoubleClick : undefined}
          className={`relative min-h-0 flex-1 overflow-auto ${
            activeBoard?.background === 'gray'
              ? 'bg-[radial-gradient(circle_at_1px_1px,#cbd5e1_1px,transparent_0)] [background-size:18px_18px]'
              : 'bg-[radial-gradient(circle_at_1px_1px,#b45309_0.7px,transparent_0)] [background-color:#c48a4b] [background-size:12px_12px]'
          }`}
        >
          {viewMode === 'canvas' ? (
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

              {filteredNotes.map((note) => renderNote(note, 'canvas'))}
            </div>
          ) : (
            <div className="p-3">
              {notesLoading ? (
                <div className="rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                  Загрузка заметок...
                </div>
              ) : null}

              {!notesLoading && notes.length === 0 ? (
                <div className="rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                  На доске пока нет заметок.
                </div>
              ) : null}

              {!notesLoading && notes.length > 0 && filteredNotes.length === 0 ? (
                <div className="rounded-md bg-white/90 px-3 py-2 text-sm text-slate-600 shadow-sm">
                  По текущему поиску заметок нет.
                </div>
              ) : null}

              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
                {filteredNotes.map((note) => renderNote(note, 'list'))}
              </div>
            </div>
          )}

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

          {noteConflict ? (
            <div className="sticky bottom-3 left-3 ml-3 mt-2 max-w-xl rounded-md border border-amber-200 bg-white px-3 py-2 text-sm text-slate-800 shadow-lg">
              <div className="font-semibold text-amber-800">Заметка изменена другим пользователем</div>
              <div className="mt-1 grid gap-2 text-xs sm:grid-cols-2">
                <div className="min-w-0 rounded border border-slate-200 bg-slate-50 p-2">
                  <div className="mb-1 font-medium text-slate-600">На сервере</div>
                  <div className="max-h-14 overflow-hidden whitespace-pre-wrap">{noteConflict.server.text || 'Пустая заметка'}</div>
                </div>
                <div className="min-w-0 rounded border border-slate-200 bg-slate-50 p-2">
                  <div className="mb-1 font-medium text-slate-600">Ваша версия</div>
                  <div className="max-h-14 overflow-hidden whitespace-pre-wrap">{noteConflict.local.text || 'Пустая заметка'}</div>
                </div>
              </div>
              <div className="mt-2 flex flex-wrap gap-2">
                <button
                  type="button"
                  onClick={applyServerConflictVersion}
                  className="rounded-md border border-slate-300 px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50"
                >
                  Обновить с сервера
                </button>
                <button
                  type="button"
                  onClick={copyConflictDraft}
                  className="rounded-md border border-slate-300 px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50"
                >
                  Скопировать мой текст
                </button>
                <button
                  type="button"
                  onClick={overwriteConflictVersion}
                  disabled={!canEditActiveBoard}
                  title={canEditActiveBoard ? 'Перезаписать серверную версию' : 'Нет права редактировать эту доску'}
                  className="rounded-md bg-slate-900 px-2 py-1 text-xs font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Перезаписать
                </button>
              </div>
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}

export default StickyNotes;
