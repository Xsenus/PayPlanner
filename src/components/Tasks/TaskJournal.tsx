import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  AlarmClock,
  Bell,
  CalendarDays,
  CalendarClock,
  ChevronLeft,
  ChevronRight,
  CheckCircle2,
  ClipboardList,
  Clock3,
  Download,
  FileText,
  History,
  LayoutGrid,
  ListFilter,
  Mail,
  MessageSquare,
  Pencil,
  Phone,
  Plus,
  Search,
  Send,
  TimerReset,
  Trash2,
  WalletCards,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useClients } from '../../hooks/useClients';
import { useClientCases } from '../../hooks/useClientCases';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { Client, LegalEntitySummary } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import {
  isTaskJournalActive,
  TASK_JOURNAL_EVENT,
  type TaskJournalAssignee,
  type TaskJournalCaseAutomationCandidate,
  type TaskJournalChannel,
  type TaskJournalComment,
  type TaskJournalFile,
  type TaskJournalItem,
  type TaskJournalParticipant,
  type TaskJournalParticipantRole,
  type TaskJournalPriority,
  type TaskJournalReminder,
  type TaskJournalStatus,
  type TaskJournalType,
  type TaskJournalUpsertPayload,
} from '../../utils/taskJournalStore';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';

type ViewMode = 'list' | 'board' | 'calendar';
type PeriodMode = 'day' | 'week' | 'month';
type ModalState = { mode: 'create' | 'edit'; task?: TaskJournalItem; dueAt?: string } | null;
type TaskJournalProps = {
  initialView?: ViewMode;
  routeTaskId?: number | null;
  onRouteTaskChange?: (taskId: number | null) => void;
};

type TaskForm = TaskJournalUpsertPayload & { dueAt: string };

const STATUS_OPTIONS: Array<{ value: TaskJournalStatus; label: string }> = [
  { value: 'new', label: 'Новая' },
  { value: 'inProgress', label: 'В работе' },
  { value: 'waiting', label: 'Ожидает' },
  { value: 'onApproval', label: 'На согласовании' },
  { value: 'revision', label: 'На доработке' },
  { value: 'overdueApproval', label: 'Просрочено на согласовании' },
  { value: 'done', label: 'Готово' },
  { value: 'cancelled', label: 'Отменена' },
];

const TYPE_OPTIONS: Array<{ value: TaskJournalType; label: string; icon: typeof ClipboardList }> = [
  { value: 'task', label: 'Задача', icon: ClipboardList },
  { value: 'budget', label: 'Бюджет', icon: WalletCards },
  { value: 'contact', label: 'Связаться', icon: Phone },
  { value: 'document', label: 'Документ', icon: FileText },
  { value: 'reminder', label: 'Не забыть', icon: Bell },
  { value: 'control', label: 'Контроль', icon: AlarmClock },
];

const PRIORITY_OPTIONS: Array<{ value: TaskJournalPriority; label: string }> = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Срочно' },
];

const REMINDER_OPTIONS: Array<{ value: TaskJournalReminder; label: string }> = [
  { value: 'before1d', label: 'За день' },
  { value: 'before1h', label: 'За час' },
  { value: 'before15m', label: 'За 15 минут' },
  { value: 'atDue', label: 'В срок' },
  { value: 'dailyOverdue', label: 'Каждый день просрочки' },
];

const CHANNEL_OPTIONS: Array<{ value: TaskJournalChannel; label: string; icon: typeof Bell }> = [
  { value: 'inApp', label: 'В системе', icon: Bell },
  { value: 'email', label: 'Email', icon: Mail },
  { value: 'telegram', label: 'Telegram', icon: Send },
];

const PARTICIPANT_ROLE_LABELS: Record<TaskJournalParticipantRole, string> = {
  assignee: 'Исполнители',
  approver: 'Согласующие',
  observer: 'Наблюдатели',
};

const emptyForm = (assigneeName: string, assigneeUserId?: number | null): TaskForm => {
  const dueAt = new Date();
  dueAt.setHours(dueAt.getHours() + 2);

  return {
    clientId: null,
    legalEntityId: null,
    clientCaseId: null,
    objectName: '',
    title: '',
    description: '',
    assigneeUserId,
    assigneeName,
    dueAt: toLocalInputValue(dueAt.toISOString()),
    plannedDurationMinutes: 60,
    type: 'task',
    priority: 'normal',
    status: 'new',
    result: '',
    reminders: ['before1h', 'atDue'],
    channels: ['inApp'],
    tags: [],
    participants: assigneeName
      ? [{
          userId: assigneeUserId ?? null,
          role: 'assignee',
          displayName: assigneeName,
          sortOrder: 0,
          approvalStatus: 'pending',
        }]
      : [],
  };
};

function toLocalInputValue(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const offsetMs = date.getTimezoneOffset() * 60 * 1000;
  return new Date(date.getTime() - offsetMs).toISOString().slice(0, 16);
}

function fromLocalInputValue(value: string) {
  return value ? new Date(value).toISOString() : new Date().toISOString();
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function formatRelative(value: string) {
  const ms = new Date(value).getTime() - Date.now();
  const absHours = Math.ceil(Math.abs(ms) / 36e5);
  if (ms < 0) return `просрочено на ${absHours} ч.`;
  if (absHours < 24) return `через ${absHours} ч.`;
  return `через ${Math.ceil(absHours / 24)} дн.`;
}

function formatFileSize(bytes: number) {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 Б';
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
  return `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
}

function normalizeTaskDuration(value: number | null | undefined) {
  const minutes = Number.isFinite(value ?? NaN) ? Number(value) : 60;
  const clamped = Math.min(8 * 60, Math.max(15, minutes));
  return Math.round(clamped / 15) * 15;
}

function formatDuration(minutes: number | null | undefined) {
  const normalized = normalizeTaskDuration(minutes);
  const hours = Math.floor(normalized / 60);
  const rest = normalized % 60;
  if (hours > 0 && rest > 0) return `${hours} ч ${rest} мин`;
  if (hours > 0) return `${hours} ч`;
  return `${rest} мин`;
}

function intervalsOverlap(aStart: number, aEnd: number, bStart: number, bEnd: number) {
  return aStart < bEnd && bStart < aEnd;
}

function buildDefaultDueAtForDate(date: Date) {
  const next = startOfDay(date);
  next.setHours(9, 0, 0, 0);
  return next.toISOString();
}

function moveDueAtToDate(currentDueAt: string, targetDate: Date) {
  const current = new Date(currentDueAt);
  const next = startOfDay(targetDate);
  if (Number.isNaN(current.getTime())) {
    next.setHours(9, 0, 0, 0);
    return next.toISOString();
  }

  next.setHours(current.getHours(), current.getMinutes(), current.getSeconds(), current.getMilliseconds());
  return next.toISOString();
}

function taskToPayload(task: TaskJournalItem, overrides: Partial<TaskJournalUpsertPayload> = {}): TaskJournalUpsertPayload {
  return {
    clientId: task.clientId ?? null,
    legalEntityId: task.legalEntityId ?? null,
    clientCaseId: task.clientCaseId ?? null,
    objectName: task.objectName,
    title: task.title,
    description: task.description,
    assigneeUserId: task.assigneeUserId ?? null,
    assigneeName: task.assigneeName,
    dueAt: task.dueAt,
    plannedDurationMinutes: normalizeTaskDuration(task.plannedDurationMinutes),
    type: task.type,
    priority: task.priority,
    status: task.status,
    result: task.result,
    reminders: task.reminders,
    channels: task.channels,
    tags: task.tags,
    participants: task.participants ?? [],
    ...overrides,
  };
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
}

function isSameDay(a: Date, b: Date) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

function addDays(date: Date, days: number) {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

function addMonths(date: Date, months: number) {
  const next = new Date(date);
  next.setMonth(next.getMonth() + months);
  return next;
}

function startOfWeek(date: Date) {
  const next = new Date(date);
  const day = next.getDay() || 7;
  next.setDate(next.getDate() - day + 1);
  next.setHours(0, 0, 0, 0);
  return next;
}

function startOfDay(date: Date) {
  const next = new Date(date);
  next.setHours(0, 0, 0, 0);
  return next;
}

function startOfMonth(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function isSameMonth(a: Date, b: Date) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth();
}

function toDateKey(date: Date) {
  return [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0'),
  ].join('-');
}

function formatDateOnly(value: Date) {
  return value.toLocaleDateString('ru-RU', {
    day: '2-digit',
    month: 'long',
    year: 'numeric',
  });
}

function formatPeriodTitle(date: Date, mode: PeriodMode) {
  if (mode === 'day') {
    return formatDateOnly(date);
  }

  if (mode === 'week') {
    const start = startOfWeek(date);
    const end = endOfDay(addDays(start, 6));
    return `${start.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' })} — ${end.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short', year: 'numeric' })}`;
  }

  return date.toLocaleDateString('ru-RU', { month: 'long', year: 'numeric' });
}

function endOfDay(date: Date) {
  const next = new Date(date);
  next.setHours(23, 59, 59, 999);
  return next;
}

function getTypeMeta(type: TaskJournalType) {
  return TYPE_OPTIONS.find((item) => item.value === type) ?? TYPE_OPTIONS[0];
}

function getStatusLabel(status: TaskJournalStatus) {
  return STATUS_OPTIONS.find((item) => item.value === status)?.label ?? status;
}

function getPriorityLabel(priority: TaskJournalPriority) {
  return PRIORITY_OPTIONS.find((item) => item.value === priority)?.label ?? priority;
}

function getTaskClientName(task: TaskJournalItem) {
  return task.clientName?.trim() || task.legalEntityName?.trim() || task.objectName;
}

function getTaskCaseTitle(task: TaskJournalItem) {
  return task.clientCaseTitle?.trim() || '';
}

function getLegalEntityDisplayName(entity: Pick<LegalEntitySummary, 'id' | 'shortName' | 'fullName'> | null | undefined) {
  if (!entity) return '';
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`;
}

function getLegalEntityRoleLabels(entity: Pick<LegalEntitySummary, 'clientStatus' | 'isClientAndSupplier'> | null | undefined) {
  const statusName = entity?.clientStatus?.name?.trim();
  if (entity?.isClientAndSupplier) {
    return statusName && !/клиент|поставщик/i.test(statusName)
      ? ['Клиент', 'Поставщик', statusName]
      : ['Клиент', 'Поставщик'];
  }

  return statusName ? [statusName] : [];
}

function getAutomationCandidateOwnerName(candidate: TaskJournalCaseAutomationCandidate) {
  return candidate.clientName?.trim() || candidate.legalEntityName?.trim() || 'Контрагент';
}

function getTaskParticipants(task: TaskJournalItem, role: TaskJournalParticipantRole) {
  return (task.participants ?? []).filter((participant) => participant.role === role);
}

function getParticipantDisplayName(participant: TaskJournalParticipant) {
  return participant.displayName || participant.name || participant.email || '';
}

function hasAssigneeParticipant(task: TaskJournalItem, assigneeFilter: string) {
  return getTaskParticipants(task, 'assignee').some((participant) =>
    String(participant.userId ?? getParticipantDisplayName(participant)) === assigneeFilter);
}

function hasPendingApprovers(task: TaskJournalItem) {
  return getTaskParticipants(task, 'approver').some((participant) => participant.approvalStatus !== 'approved');
}

function priorityClass(priority: TaskJournalPriority) {
  switch (priority) {
    case 'urgent':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    case 'high':
      return 'border-amber-200 bg-amber-50 text-amber-700';
    case 'low':
      return 'border-slate-200 bg-slate-50 text-slate-600';
    default:
      return 'border-blue-200 bg-blue-50 text-blue-700';
  }
}

function statusClass(status: TaskJournalStatus) {
  switch (status) {
    case 'done':
      return 'bg-emerald-50 text-emerald-700';
    case 'cancelled':
      return 'bg-slate-100 text-slate-500';
    case 'waiting':
      return 'bg-amber-50 text-amber-700';
    case 'onApproval':
      return 'bg-violet-50 text-violet-700';
    case 'revision':
      return 'bg-orange-50 text-orange-700';
    case 'overdueApproval':
      return 'bg-rose-50 text-rose-700';
    case 'inProgress':
      return 'bg-blue-50 text-blue-700';
    default:
      return 'bg-slate-50 text-slate-700';
  }
}

function unique(values: string[]) {
  return Array.from(new Set(values.map((item) => item.trim()).filter(Boolean)));
}

function normalizeParticipants(form: TaskForm, assignees: TaskJournalAssignee[]): TaskJournalParticipant[] {
  const participants = [...(form.participants ?? [])];
  const selectedAssignee = form.assigneeUserId
    ? assignees.find((assignee) => assignee.id === form.assigneeUserId)
    : null;
  const primaryName = selectedAssignee?.name || form.assigneeName.trim();
  const primaryEmail = selectedAssignee?.email ?? '';

  if (primaryName) {
    const hasPrimary = participants.some((participant) =>
      participant.role === 'assignee' &&
      (form.assigneeUserId
        ? participant.userId === form.assigneeUserId
        : getParticipantDisplayName(participant).toLowerCase() === primaryName.toLowerCase()));

    if (!hasPrimary) {
      participants.unshift({
        userId: form.assigneeUserId ?? null,
        role: 'assignee',
        displayName: primaryName,
        email: primaryEmail,
        sortOrder: 0,
        approvalStatus: 'pending',
      });
    }
  }

  const keys = new Set<string>();
  return participants
    .map((participant, index) => ({
      ...participant,
      displayName: getParticipantDisplayName(participant).trim(),
      name: getParticipantDisplayName(participant).trim(),
      email: participant.email ?? '',
      sortOrder: participant.sortOrder ?? index,
      approvalStatus: participant.approvalStatus ?? 'pending',
      resolution: participant.resolution ?? '',
    }))
    .filter((participant) => participant.displayName)
    .filter((participant) => {
      const key = `${participant.role}:${participant.userId ?? participant.displayName.toLowerCase()}`;
      if (keys.has(key)) return false;
      keys.add(key);
      return true;
    });
}

function serializeCsvValue(value: string) {
  return `"${value.replaceAll('"', '""')}"`;
}

const WEEKDAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

export function TaskJournal({ initialView = 'list', routeTaskId = null, onRouteTaskChange }: TaskJournalProps) {
  const { user } = useAuth();
  const toast = useToast();
  const { confirm } = useDialog();
  const { clients } = useClients();
  const { legalEntities } = useLegalEntities();
  const [tasks, setTasks] = useState<TaskJournalItem[]>([]);
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [automationCandidates, setAutomationCandidates] = useState<TaskJournalCaseAutomationCandidate[]>([]);
  const [creatingAutomationCaseId, setCreatingAutomationCaseId] = useState<number | null>(null);
  const [creatingAutomationAll, setCreatingAutomationAll] = useState(false);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [viewMode, setViewMode] = useState<ViewMode>(initialView);
  const [periodMode, setPeriodMode] = useState<PeriodMode>('day');
  const [statusFilter, setStatusFilter] = useState<TaskJournalStatus | 'active' | 'all'>('active');
  const [assigneeFilter, setAssigneeFilter] = useState('all');
  const [typeFilter, setTypeFilter] = useState<TaskJournalType | 'all'>('all');
  const [modal, setModal] = useState<ModalState>(null);
  const [highlightedTaskId, setHighlightedTaskId] = useState<number | null>(routeTaskId);
  const [calendarAnchorDate, setCalendarAnchorDate] = useState(() => new Date());
  const [calendarSelectedDate, setCalendarSelectedDate] = useState(() => new Date());
  const [calendarMovingTaskId, setCalendarMovingTaskId] = useState<number | null>(null);
  const [calendarResizingTaskId, setCalendarResizingTaskId] = useState<number | null>(null);
  const routeTaskRequestRef = useRef(0);
  const lastOpenedRouteTaskIdRef = useRef<number | null>(null);

  const currentUserName = user?.fullName || user?.email || 'Текущий пользователь';
  const currentUserId = user?.id ?? null;

  useEffect(() => {
    setViewMode(initialView);
  }, [initialView]);

  const openTaskEditor = useCallback(
    (task: TaskJournalItem, options?: { syncRoute?: boolean }) => {
      setModal({ mode: 'edit', task });
      setHighlightedTaskId(task.id);

      if (options?.syncRoute ?? true) {
        onRouteTaskChange?.(task.id);
      }
    },
    [onRouteTaskChange],
  );

  const openTaskCreate = useCallback(() => {
    setModal({ mode: 'create' });
    setHighlightedTaskId(null);
    onRouteTaskChange?.(null);
  }, [onRouteTaskChange]);

  const openTaskCreateForDate = useCallback((date: Date) => {
    setCalendarSelectedDate(date);
    setModal({ mode: 'create', dueAt: buildDefaultDueAtForDate(date) });
    setHighlightedTaskId(null);
    onRouteTaskChange?.(null);
  }, [onRouteTaskChange]);

  const closeTaskModal = useCallback(() => {
    setModal(null);
    onRouteTaskChange?.(null);
  }, [onRouteTaskChange]);

  const emitTaskChange = useCallback(() => {
    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT));
  }, []);

  const refreshTasks = useCallback(async () => {
    setLoading(true);
    try {
      const [tasksResponse, assigneeResponse, automationResponse] = await Promise.all([
        apiService.getTaskJournalItems({ page: 1, pageSize: 200 }),
        apiService.getTaskJournalAssignees(),
        apiService.getTaskJournalCaseAutomationCandidates({ inactiveDays: 14, limit: 20 }),
      ]);
      setTasks(tasksResponse.items);
      setAssignees(assigneeResponse);
      setAutomationCandidates(automationResponse);
    } catch {
      toast.error('Не удалось загрузить журнал задач.', 'Журнал задач');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void refreshTasks();
    const refresh = () => void refreshTasks();
    window.addEventListener(TASK_JOURNAL_EVENT, refresh);
    return () => window.removeEventListener(TASK_JOURNAL_EVENT, refresh);
  }, [refreshTasks]);

  const createAutomationTask = useCallback(async (candidate: TaskJournalCaseAutomationCandidate) => {
    setCreatingAutomationCaseId(candidate.clientCaseId);
    try {
      const created = await apiService.createTaskJournalCaseAutomationTask(candidate.clientCaseId);
      toast.success('Контрольная задача создана.', 'Автоконтроль дел');
      await refreshTasks();
      openTaskEditor(created, { syncRoute: true });
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось создать контрольную задачу.'), 'Автоконтроль дел');
    } finally {
      setCreatingAutomationCaseId(null);
    }
  }, [emitTaskChange, openTaskEditor, refreshTasks, toast]);

  const createAllAutomationTasks = useCallback(async () => {
    if (automationCandidates.length === 0) {
      return;
    }

    setCreatingAutomationAll(true);
    try {
      const result = await apiService.createTaskJournalCaseAutomationTasks({ limit: Math.min(automationCandidates.length, 100) });
      toast.success(`Создано контрольных задач: ${result.createdTasks.length}.`, 'Автоконтроль дел');
      await refreshTasks();
      if (result.createdTasks[0]) {
        openTaskEditor(result.createdTasks[0], { syncRoute: true });
      }
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось создать контрольные задачи.'), 'Автоконтроль дел');
    } finally {
      setCreatingAutomationAll(false);
    }
  }, [automationCandidates.length, emitTaskChange, openTaskEditor, refreshTasks, toast]);

  useEffect(() => {
    if (!(routeTaskId && routeTaskId > 0)) {
      routeTaskRequestRef.current += 1;
      lastOpenedRouteTaskIdRef.current = null;
      return;
    }

    if (lastOpenedRouteTaskIdRef.current === routeTaskId) {
      return;
    }

    const showTask = (task: TaskJournalItem) => {
      setViewMode('list');
      setStatusFilter('all');
      setAssigneeFilter('all');
      setTypeFilter('all');
      setSearch('');
      openTaskEditor(task, { syncRoute: false });
      lastOpenedRouteTaskIdRef.current = routeTaskId;
    };

    const existingTask = tasks.find((task) => task.id === routeTaskId);
    if (existingTask) {
      showTask(existingTask);
      return;
    }

    const requestId = routeTaskRequestRef.current + 1;
    routeTaskRequestRef.current = requestId;

    void (async () => {
      try {
        const task = await apiService.getTaskJournalItem(routeTaskId);
        if (routeTaskRequestRef.current !== requestId) {
          return;
        }

        setTasks((previous) => (previous.some((item) => item.id === task.id) ? previous : [task, ...previous]));
        showTask(task);
      } catch {
        if (routeTaskRequestRef.current !== requestId) {
          return;
        }

        toast.error('Задача из уведомления не найдена или уже удалена.', 'Журнал задач');
        onRouteTaskChange?.(null);
      }
    })();
  }, [onRouteTaskChange, openTaskEditor, routeTaskId, tasks, toast]);

  const assigneeOptions = useMemo(() => {
    const byId = new Map<number, TaskJournalAssignee>();
    assignees.forEach((assignee) => byId.set(assignee.id, assignee));
    return Array.from(byId.values());
  }, [assignees]);

  const counterpartyOptions = useMemo<LookupSelectOption[]>(
    () =>
      clients
        .filter((client) => client?.id && client?.name)
        .map((client) => ({
          value: `client:${client.id}`,
          label: client.name,
          description: [client.phone, client.email].filter(Boolean).join(' · ') || undefined,
          keywords: [String(client.id), client.name, client.phone, client.email, client.company].filter(Boolean),
        }))
        .concat(legalEntities
          .filter((entity) => entity?.id && getLegalEntityDisplayName(entity))
          .map((entity) => {
            const name = getLegalEntityDisplayName(entity);
            const roleLabels = getLegalEntityRoleLabels(entity);
            return {
              value: `legal:${entity.id}`,
              label: name,
              description: ['Юрлицо', ...roleLabels, entity.inn ? `ИНН ${entity.inn}` : '', entity.kpp ? `КПП ${entity.kpp}` : '', entity.email].filter(Boolean).join(' · ') || undefined,
              keywords: [
                String(entity.id),
                name,
                entity.fullName,
                entity.inn,
                entity.kpp,
                entity.ogrn,
                entity.phone,
                entity.email,
                'юридическое лицо',
                'юрлицо',
                'контрагент',
                ...roleLabels,
              ].filter(Boolean),
            };
          }))
        .sort((a, b) => a.label.localeCompare(b.label, 'ru')),
    [clients, legalEntities],
  );

  const filteredTasks = useMemo(() => {
    const q = search.trim().toLowerCase();
    const nowDate = new Date();
    const weekStart = startOfWeek(nowDate);
    const monthStart = new Date(nowDate.getFullYear(), nowDate.getMonth(), 1);
    const periodEnd =
      periodMode === 'day'
        ? endOfDay(nowDate)
        : periodMode === 'week'
          ? endOfDay(addDays(weekStart, 6))
          : endOfDay(new Date(nowDate.getFullYear(), nowDate.getMonth() + 1, 0));
    const periodStart = periodMode === 'day' ? new Date(nowDate.getFullYear(), nowDate.getMonth(), nowDate.getDate()) : periodMode === 'week' ? weekStart : monthStart;

    return tasks
      .filter((task) => {
        const due = new Date(task.dueAt);
        const statusOk =
          statusFilter === 'all'
            ? true
            : statusFilter === 'active'
              ? isTaskJournalActive(task.status)
              : task.status === statusFilter;
        const assigneeOk =
          assigneeFilter === 'all' ||
          String(task.assigneeUserId ?? task.assigneeName) === assigneeFilter ||
          hasAssigneeParticipant(task, assigneeFilter);
        const typeOk = typeFilter === 'all' || task.type === typeFilter;
        const periodOk = viewMode !== 'board' || due < periodStart || (due >= periodStart && due <= periodEnd);
        const searchOk = !q || [
          task.objectName,
          task.clientName ?? '',
          task.legalEntityName ?? '',
          task.clientCaseTitle ?? '',
          task.title,
          task.description,
          task.assigneeName,
          (task.participants ?? []).map(getParticipantDisplayName).join(' '),
          task.authorName,
          task.result,
          task.tags.join(' '),
        ].some((value) => value.toLowerCase().includes(q));

        return statusOk && assigneeOk && typeOk && periodOk && searchOk;
      })
      .sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
  }, [assigneeFilter, periodMode, search, statusFilter, tasks, typeFilter, viewMode]);

  const shiftCalendarPeriod = (direction: -1 | 1) => {
    const next =
      periodMode === 'day'
        ? addDays(calendarAnchorDate, direction)
        : periodMode === 'week'
          ? addDays(calendarAnchorDate, direction * 7)
          : addMonths(calendarAnchorDate, direction);
    setCalendarAnchorDate(next);
    setCalendarSelectedDate(next);
  };

  const resetCalendarPeriod = () => {
    const today = new Date();
    setCalendarAnchorDate(today);
    setCalendarSelectedDate(today);
  };

  const stats = useMemo(() => {
    const active = tasks.filter((task) => isTaskJournalActive(task.status));
    const overdue = active.filter((task) => new Date(task.dueAt).getTime() < Date.now());
    const today = active.filter((task) => isSameDay(new Date(task.dueAt), new Date()));
    const high = active.filter((task) => task.priority === 'high' || task.priority === 'urgent');
    return { active: active.length, overdue: overdue.length, today: today.length, high: high.length };
  }, [tasks]);

  const boardColumns = useMemo(() => {
    const nowDate = new Date();
    const tomorrow = addDays(nowDate, 1);
    const active = filteredTasks.filter((task) => isTaskJournalActive(task.status));

    return [
      {
        key: 'overdue',
        title: 'Просроченные',
        tone: 'rose',
        items: active.filter((task) => new Date(task.dueAt).getTime() < Date.now()),
      },
      {
        key: 'today',
        title: 'На сегодня',
        tone: 'blue',
        items: active.filter((task) => isSameDay(new Date(task.dueAt), nowDate)),
      },
      {
        key: 'tomorrow',
        title: 'На завтра',
        tone: 'emerald',
        items: active.filter((task) => isSameDay(new Date(task.dueAt), tomorrow)),
      },
      {
        key: 'later',
        title: periodMode === 'month' ? 'Дальше в месяце' : 'Ближайшие',
        tone: 'slate',
        items: active.filter((task) => {
          const due = new Date(task.dueAt);
          return due.getTime() >= Date.now() && !isSameDay(due, nowDate) && !isSameDay(due, tomorrow);
        }),
      },
    ];
  }, [filteredTasks, periodMode]);

  const handleSave = async (form: TaskForm, taskId?: number) => {
    if (!form.clientId && !form.legalEntityId) {
      toast.error('Укажите контрагента задачи.', 'Журнал задач');
      return;
    }
    if (!form.title.trim()) {
      toast.error('Укажите наименование задачи.', 'Журнал задач');
      return;
    }
    if (form.status === 'done' && !form.result.trim()) {
      toast.error('Перед завершением задачи заполните результат.', 'Журнал задач');
      return;
    }

    const normalized: TaskForm = {
      ...form,
      objectName: form.objectName.trim(),
      title: form.title.trim(),
      description: form.description.trim(),
      result: form.result.trim(),
      dueAt: fromLocalInputValue(form.dueAt),
      plannedDurationMinutes: normalizeTaskDuration(form.plannedDurationMinutes),
      tags: unique(form.tags),
      reminders: form.reminders.length ? form.reminders : ['atDue'],
      channels: form.channels.length ? form.channels : ['inApp'],
      participants: normalizeParticipants(form, assignees),
    };

    try {
      if (taskId) {
        await apiService.updateTaskJournalItem(taskId, normalized);
        toast.success('Задача обновлена.', 'Журнал задач');
      } else {
        await apiService.createTaskJournalItem(normalized);
        toast.success('Задача создана.', 'Журнал задач');
      }
      closeTaskModal();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить задачу.'), 'Журнал задач');
    }
  };

  const handleDelete = async (task: TaskJournalItem) => {
    const accepted = await confirm({
      title: 'Удалить задачу?',
      description: `Задача "${task.title}" будет удалена из журнала.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;
    try {
      await apiService.deleteTaskJournalItem(task.id);
      toast.success('Задача удалена.', 'Журнал задач');
      if (routeTaskId === task.id) {
        onRouteTaskChange?.(null);
      }
      setHighlightedTaskId((previous) => (previous === task.id ? null : previous));
      await refreshTasks();
      emitTaskChange();
    } catch {
      toast.error('Не удалось удалить задачу.', 'Журнал задач');
    }
  };

  const setTaskStatus = async (task: TaskJournalItem, status: TaskJournalStatus) => {
    if (status === 'done' && !task.result.trim()) {
      toast.error('Перед завершением задачи заполните результат.', 'Журнал задач');
      openTaskEditor({ ...task, status: 'done' }, { syncRoute: true });
      return;
    }
    if (status === 'done' && hasPendingApprovers(task)) {
      toast.error('Задачу нельзя завершить, пока все согласующие не согласовали её.', 'Журнал задач');
      openTaskEditor({ ...task, status: 'onApproval' }, { syncRoute: true });
      return;
    }

    try {
      await apiService.updateTaskJournalStatus(task.id, status);
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось обновить статус задачи.'), 'Журнал задач');
    }
  };

  const handleApprovalDecision = async (
    taskId: number,
    decision: 'approved' | 'revisionRequested',
    resolution?: string,
    participantId?: number,
  ) => {
    try {
      await apiService.updateTaskJournalApproval(taskId, decision, resolution, participantId);
      toast.success(decision === 'approved' ? 'Решение по согласованию сохранено.' : 'Задача отправлена на доработку.', 'Журнал задач');
      closeTaskModal();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить решение по согласованию.'), 'Журнал задач');
    }
  };

  const handleCalendarTaskMove = async (task: TaskJournalItem, targetDate: Date) => {
    if (isSameDay(new Date(task.dueAt), targetDate)) {
      setCalendarSelectedDate(targetDate);
      return;
    }

    if (!task.clientId && !task.legalEntityId) {
      toast.error('У задачи не указан контрагент. Откройте задачу и заполните контрагента перед переносом.', 'Календарь задач');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

    const nextDueAt = moveDueAtToDate(task.dueAt, targetDate);
    setCalendarMovingTaskId(task.id);
    try {
      await apiService.updateTaskJournalItem(task.id, taskToPayload(task, { dueAt: nextDueAt }));
      setCalendarSelectedDate(targetDate);
      toast.success('Задача перенесена в календаре.', 'Календарь задач');
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось перенести задачу.'), 'Календарь задач');
    } finally {
      setCalendarMovingTaskId(null);
    }
  };

  const handleCalendarTaskResize = async (task: TaskJournalItem, deltaMinutes: -15 | 15) => {
    const nextDuration = normalizeTaskDuration((task.plannedDurationMinutes ?? 60) + deltaMinutes);
    if (nextDuration === normalizeTaskDuration(task.plannedDurationMinutes)) {
      return;
    }

    setCalendarResizingTaskId(task.id);
    try {
      await apiService.updateTaskJournalItem(task.id, taskToPayload(task, { plannedDurationMinutes: nextDuration }));
      toast.success('Длительность задачи обновлена.', 'Календарь задач');
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось изменить длительность задачи.'), 'Календарь задач');
    } finally {
      setCalendarResizingTaskId(null);
    }
  };

  const handleExport = () => {
    const header = ['Контрагент', 'Дело', 'Наименование задачи', 'Исполнитель', 'Срок', 'Длительность', 'Тип', 'Приоритет', 'Статус', 'Результат'];
    const rows = filteredTasks.map((task) => [
      getTaskClientName(task),
      getTaskCaseTitle(task),
      task.title,
      task.assigneeName,
      formatDateTime(task.dueAt),
      formatDuration(task.plannedDurationMinutes),
      getTypeMeta(task.type).label,
      getPriorityLabel(task.priority),
      getStatusLabel(task.status),
      task.result,
    ]);
    const csv = [header, ...rows].map((row) => row.map(serializeCsvValue).join(';')).join('\n');
    const blob = new Blob([`\ufeff${csv}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = 'task-journal.csv';
    link.click();
    URL.revokeObjectURL(url);
    toast.success('Журнал задач экспортирован в CSV.', 'Журнал задач');
  };

  const handleCalendarExport = async () => {
    try {
      const response = await apiService.downloadTaskJournalCalendar();
      downloadBlob(response.fileName, response.blob);
      toast.success('Календарь задач выгружен в ICS.', 'Календарь задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выгрузить календарь задач.'), 'Календарь задач');
    }
  };

  return (
    <div className="min-h-screen bg-slate-50">
      <div className="mx-auto max-w-[1720px] p-4 sm:p-6 lg:p-8">
        <section className="mb-6">
          <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Рабочий контроль</div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">Журнал задач</h1>
              <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
                Задачи по контрагентам, делам, платежам, звонкам и документам: исполнитель, срок, результат и напоминания в одном рабочем журнале.
              </p>
            </div>

            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={handleExport}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
              >
                <Download className="h-4 w-4" />
                CSV
              </button>
              <button
                type="button"
                onClick={() => void handleCalendarExport()}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
              >
                <CalendarDays className="h-4 w-4" />
                ICS
              </button>
              <button
                type="button"
                onClick={openTaskCreate}
                className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800"
              >
                <Plus className="h-4 w-4" />
                Добавить задачу
              </button>
            </div>
          </div>
        </section>

        <section className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <MetricCard label="Активные" value={stats.active} icon={ClipboardList} tone="blue" />
          <MetricCard label="Просрочены" value={stats.overdue} icon={TimerReset} tone="rose" />
          <MetricCard label="Сегодня" value={stats.today} icon={CalendarClock} tone="emerald" />
          <MetricCard label="Высокий приоритет" value={stats.high} icon={AlarmClock} tone="amber" />
        </section>

        <section className="mb-6 grid gap-3 xl:grid-cols-[minmax(0,1fr)_auto]">
          <div className="grid gap-3 md:grid-cols-[minmax(260px,1fr)_repeat(3,minmax(180px,220px))]">
            <label className="relative block">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Поиск по контрагенту, делу, наименованию, исполнителю или результату"
                className="h-11 w-full rounded-xl border border-slate-200 bg-white pl-9 pr-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
            </label>

            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value as TaskJournalStatus | 'active' | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Статус"
            >
              <option value="active">Активные</option>
              <option value="all">Все статусы</option>
              {STATUS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>

            <select
              value={assigneeFilter}
              onChange={(event) => setAssigneeFilter(event.target.value)}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Исполнитель"
            >
              <option value="all">Все исполнители</option>
              {assigneeOptions.map((assignee) => (
                <option key={assignee.id} value={String(assignee.id)}>{assignee.name}</option>
              ))}
            </select>

            <select
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value as TaskJournalType | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Тип задачи"
            >
              <option value="all">Все типы</option>
              {TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>

          <div className="flex flex-wrap justify-end gap-2">
            <div className="flex rounded-xl bg-slate-100 p-1">
              {(['day', 'week', 'month'] as PeriodMode[]).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => setPeriodMode(mode)}
                  className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${periodMode === mode ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                >
                  {mode === 'day' ? 'День' : mode === 'week' ? 'Неделя' : 'Месяц'}
                </button>
              ))}
            </div>

            <div className="flex rounded-xl bg-slate-100 p-1">
              <button
                type="button"
                onClick={() => setViewMode('list')}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'list' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <ListFilter className="h-4 w-4" />
                Список
              </button>
              <button
                type="button"
                onClick={() => setViewMode('board')}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'board' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <LayoutGrid className="h-4 w-4" />
                Доска
              </button>
              <button
                type="button"
                onClick={() => setViewMode('calendar')}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'calendar' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <CalendarDays className="h-4 w-4" />
                Календарь
              </button>
            </div>
          </div>
        </section>

        {automationCandidates.length > 0 ? (
          <TaskAutomationPanel
            candidates={automationCandidates}
            creatingCaseId={creatingAutomationCaseId}
            creatingAll={creatingAutomationAll}
            onCreate={createAutomationTask}
            onCreateAll={createAllAutomationTasks}
          />
        ) : null}

        {viewMode === 'list' ? (
          loading ? (
            <div className="rounded-2xl border border-slate-200 bg-white px-6 py-12 text-center text-sm text-slate-500 shadow-sm">
              Загружаем журнал задач...
            </div>
          ) : (
          <TaskTable
            tasks={filteredTasks}
            highlightedTaskId={highlightedTaskId}
            onEdit={openTaskEditor}
            onDelete={handleDelete}
            onStatusChange={setTaskStatus}
            onDone={(task) => setTaskStatus(task, 'done')}
          />
          )
        ) : viewMode === 'board' ? (
          <TaskBoard
            columns={boardColumns}
            highlightedTaskId={highlightedTaskId}
            onEdit={openTaskEditor}
            onDone={(task) => setTaskStatus(task, 'done')}
          />
        ) : (
          <TaskCalendar
            tasks={filteredTasks}
            mode={periodMode}
            anchorDate={calendarAnchorDate}
            selectedDate={calendarSelectedDate}
            highlightedTaskId={highlightedTaskId}
            loading={loading}
            onPrevious={() => shiftCalendarPeriod(-1)}
            onNext={() => shiftCalendarPeriod(1)}
            onToday={resetCalendarPeriod}
            onSelectDate={setCalendarSelectedDate}
            onCreateDate={openTaskCreateForDate}
            onMoveTask={handleCalendarTaskMove}
            onResizeTask={handleCalendarTaskResize}
            onEdit={openTaskEditor}
            onDone={(task) => setTaskStatus(task, 'done')}
            movingTaskId={calendarMovingTaskId}
            resizingTaskId={calendarResizingTaskId}
          />
        )}
      </div>

      {modal ? (
        <TaskModal
          key={modal.task ? `edit-${modal.task.id}` : 'create'}
          state={modal}
          assignees={assignees}
          clients={clients}
          legalEntities={legalEntities}
          counterpartyOptions={counterpartyOptions}
          currentUserId={currentUserId}
          currentUserName={currentUserName}
          onClose={closeTaskModal}
          onSave={handleSave}
          onApprovalDecision={handleApprovalDecision}
        />
      ) : null}
    </div>
  );
}

function TaskAutomationPanel({
  candidates,
  creatingCaseId,
  creatingAll,
  onCreate,
  onCreateAll,
}: {
  candidates: TaskJournalCaseAutomationCandidate[];
  creatingCaseId: number | null;
  creatingAll: boolean;
  onCreate: (candidate: TaskJournalCaseAutomationCandidate) => void;
  onCreateAll: () => void;
}) {
  const visibleCandidates = candidates.slice(0, 6);

  return (
    <section className="mb-6 rounded-2xl border border-amber-200 bg-amber-50/60 p-4 shadow-sm">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">Автоконтроль дел</div>
          <h2 className="mt-1 text-lg font-semibold text-slate-900">Дела без активных задач</h2>
        </div>
        <div className="inline-flex w-fit items-center gap-2 rounded-full border border-amber-200 bg-white px-3 py-1 text-sm font-semibold text-amber-700">
          <ClipboardList className="h-4 w-4" />
          {candidates.length}
        </div>
        <button
          type="button"
          onClick={onCreateAll}
          disabled={creatingAll}
          className="inline-flex w-fit items-center gap-2 rounded-xl bg-amber-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-amber-700 disabled:cursor-wait disabled:opacity-60"
        >
          <Plus className="h-4 w-4" />
          {creatingAll ? 'Создаем...' : 'Создать все'}
        </button>
      </div>

      <div className="mt-4 grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
        {visibleCandidates.map((candidate) => (
          <article key={candidate.clientCaseId} className="rounded-xl border border-amber-200 bg-white p-4 shadow-sm">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="truncate text-sm font-semibold text-slate-900">{getAutomationCandidateOwnerName(candidate)}</div>
                <div className="mt-1 truncate text-sm text-slate-600">{candidate.clientCaseTitle}</div>
              </div>
              <span className="shrink-0 rounded-full bg-amber-100 px-2 py-1 text-xs font-semibold text-amber-700">
                {candidate.daysWithoutActiveTask} дн.
              </span>
            </div>
            <div className="mt-3 text-sm text-slate-600">{candidate.reason}</div>
            <div className="mt-2 text-xs text-slate-500">
              {candidate.lastTaskAt ? `Последняя задача: ${formatDateTime(candidate.lastTaskAt)}` : 'Задач по делу еще не было'}
            </div>
            <div className="mt-4 flex items-center justify-between gap-3">
              <div className="text-xs text-slate-500">Закрытых задач: {candidate.completedTaskCount}</div>
              <button
                type="button"
                data-testid={`task-automation-create-${candidate.clientCaseId}`}
                disabled={creatingCaseId === candidate.clientCaseId}
                onClick={() => onCreate(candidate)}
                className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-amber-700 disabled:cursor-wait disabled:opacity-60"
              >
                <Plus className="h-4 w-4" />
                {creatingCaseId === candidate.clientCaseId ? 'Создаем...' : 'Создать задачу'}
              </button>
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

function MetricCard({ label, value, icon: Icon, tone }: { label: string; value: number; icon: typeof ClipboardList; tone: 'blue' | 'rose' | 'emerald' | 'amber' }) {
  const toneClass = {
    blue: 'bg-blue-50 text-blue-700',
    rose: 'bg-rose-50 text-rose-700',
    emerald: 'bg-emerald-50 text-emerald-700',
    amber: 'bg-amber-50 text-amber-700',
  }[tone];

  return (
    <div className="rounded-2xl border border-slate-200 bg-white px-5 py-4 shadow-sm">
      <div className="relative flex min-h-[68px] items-center justify-center text-center">
        <div className="min-w-0 px-12">
          <div className="text-sm text-slate-500">{label}</div>
          <div className="mt-2 text-3xl font-semibold text-slate-900">{value}</div>
        </div>
        <div className={`absolute right-0 top-1/2 -translate-y-1/2 rounded-xl p-3 ${toneClass}`}>
          <Icon className="h-5 w-5" />
        </div>
      </div>
    </div>
  );
}

function TaskTable({
  tasks,
  highlightedTaskId,
  onEdit,
  onDelete,
  onStatusChange,
  onDone,
}: {
  tasks: TaskJournalItem[];
  highlightedTaskId: number | null;
  onEdit: (task: TaskJournalItem) => void;
  onDelete: (task: TaskJournalItem) => void;
  onStatusChange: (task: TaskJournalItem, status: TaskJournalStatus) => void;
  onDone: (task: TaskJournalItem) => void;
}) {
  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="overflow-x-auto">
        <table className="min-w-[1180px] w-full">
          <thead className="border-b border-slate-200 bg-slate-50">
            <tr className="text-left text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">
              <th className="px-4 py-4">Контрагент / дело</th>
              <th className="px-4 py-4">Наименование задачи</th>
              <th className="px-4 py-4">Исполнитель</th>
              <th className="px-4 py-4">Дата исполнения</th>
              <th className="px-4 py-4">Тип</th>
              <th className="px-4 py-4">Результат</th>
              <th className="px-4 py-4">Статус</th>
              <th className="px-4 py-4 text-right">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {tasks.length === 0 ? (
              <tr>
                <td colSpan={8} className="px-4 py-12 text-center text-sm text-slate-500">
                  По текущим фильтрам задач нет.
                </td>
              </tr>
            ) : tasks.map((task) => {
              const TypeIcon = getTypeMeta(task.type).icon;
              const isOverdue = isTaskJournalActive(task.status) && new Date(task.dueAt).getTime() < Date.now();
              const isHighlighted = highlightedTaskId === task.id;
              const taskAssignees = getTaskParticipants(task, 'assignee');
              const taskApprovers = getTaskParticipants(task, 'approver');
              const taskObservers = getTaskParticipants(task, 'observer');

              return (
                <tr
                  key={task.id}
                  className={`align-top transition ${
                    isHighlighted ? 'bg-blue-50/70 ring-1 ring-inset ring-blue-200' : 'hover:bg-slate-50'
                  }`}
                >
                  <td className="max-w-[280px] px-4 py-4">
                    <div className="font-semibold text-slate-900">{getTaskClientName(task)}</div>
                    {getTaskCaseTitle(task) ? (
                      <div className="mt-1 text-sm text-slate-500">{getTaskCaseTitle(task)}</div>
                    ) : null}
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {task.tags.map((tag) => (
                        <span key={tag} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500">{tag}</span>
                      ))}
                    </div>
                  </td>
                  <td className="max-w-[360px] px-4 py-4">
                    <div className="font-medium text-slate-900">{task.title}</div>
                    <div className="mt-1 line-clamp-2 text-sm text-slate-500">{task.description || 'Описание не заполнено'}</div>
                    <span className={`mt-3 inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${priorityClass(task.priority)}`}>
                      {getPriorityLabel(task.priority)}
                    </span>
                  </td>
                  <td className="px-4 py-4 text-sm text-slate-600">
                    <div className="font-semibold text-slate-700">
                      {taskAssignees.map(getParticipantDisplayName).filter(Boolean).join(', ') || task.assigneeName}
                    </div>
                    {taskApprovers.length > 0 ? (
                      <div className="mt-2 text-xs text-violet-600">
                        Согласуют: {taskApprovers.map(getParticipantDisplayName).filter(Boolean).join(', ')}
                      </div>
                    ) : null}
                    {taskObservers.length > 0 ? (
                      <div className="mt-1 text-xs text-slate-400">
                        Наблюдают: {taskObservers.map(getParticipantDisplayName).filter(Boolean).join(', ')}
                      </div>
                    ) : null}
                  </td>
                  <td className="px-4 py-4">
                    <div className={isOverdue ? 'font-semibold text-rose-600' : 'font-medium text-slate-800'}>{formatDateTime(task.dueAt)}</div>
                    <div className="mt-1 text-xs text-slate-500">{formatRelative(task.dueAt)}</div>
                    <div className="mt-1 text-xs font-semibold text-slate-500">Длительность: {formatDuration(task.plannedDurationMinutes)}</div>
                  </td>
                  <td className="px-4 py-4">
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">
                      <TypeIcon className="h-3.5 w-3.5" />
                      {getTypeMeta(task.type).label}
                    </span>
                  </td>
                  <td className="max-w-[260px] px-4 py-4 text-sm text-slate-600">{task.result || '—'}</td>
                  <td className="px-4 py-4">
                    <select
                      value={task.status}
                      onChange={(event) => onStatusChange(task, event.target.value as TaskJournalStatus)}
                      className={`rounded-xl border-0 px-3 py-2 text-sm font-semibold outline-none ${statusClass(task.status)}`}
                    >
                      {STATUS_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>{option.label}</option>
                      ))}
                    </select>
                  </td>
                  <td className="px-4 py-4">
                    <div className="flex justify-end gap-2">
                      {isTaskJournalActive(task.status) ? (
                        <button type="button" onClick={() => onDone(task)} className="rounded-xl p-2 text-emerald-600 transition hover:bg-emerald-50 hover:text-emerald-700" title="Завершить">
                          <CheckCircle2 className="h-4 w-4" />
                        </button>
                      ) : null}
                      <button type="button" onClick={() => onEdit(task)} className="rounded-xl p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-900" title="Редактировать">
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button type="button" onClick={() => onDelete(task)} className="rounded-xl p-2 text-rose-500 transition hover:bg-rose-50 hover:text-rose-700" title="Удалить">
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TaskBoard({
  columns,
  highlightedTaskId,
  onEdit,
  onDone,
}: {
  columns: Array<{ key: string; title: string; tone: string; items: TaskJournalItem[] }>;
  highlightedTaskId: number | null;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
}) {
  return (
    <div className="grid gap-4 xl:grid-cols-4">
      {columns.map((column) => (
        <section key={column.key} className="min-h-[360px] rounded-2xl border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-100 px-4 py-4">
            <div className="flex items-center justify-between gap-3">
              <h2 className="font-semibold text-slate-900">{column.title}</h2>
              <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">{column.items.length}</span>
            </div>
          </div>
          <div className="space-y-3 p-3">
            {column.items.length === 0 ? (
              <div className="rounded-xl border border-dashed border-slate-200 px-3 py-8 text-center text-sm text-slate-400">
                Нет задач
              </div>
            ) : column.items.map((task) => {
              const TypeIcon = getTypeMeta(task.type).icon;
              const isHighlighted = highlightedTaskId === task.id;
              return (
                <article
                  key={task.id}
                  className={`rounded-xl border bg-white p-4 shadow-sm ${
                    isHighlighted ? 'border-blue-300 ring-2 ring-blue-100' : 'border-slate-200'
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${priorityClass(task.priority)}`}>
                      {getPriorityLabel(task.priority)}
                    </span>
                    <button type="button" onClick={() => onEdit(task)} className="rounded-lg p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" title="Редактировать">
                      <Pencil className="h-4 w-4" />
                    </button>
                  </div>
                  <div className="mt-3 text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{getTaskClientName(task)}</div>
                  {getTaskCaseTitle(task) ? (
                    <div className="mt-1 text-xs text-slate-500">{getTaskCaseTitle(task)}</div>
                  ) : null}
                  <h3 className="mt-2 text-sm font-semibold leading-5 text-slate-900">{task.title}</h3>
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <TypeIcon className="h-3.5 w-3.5" />
                    {getTypeMeta(task.type).label}
                    <span>•</span>
                    {task.assigneeName}
                  </div>
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <Clock3 className="h-3.5 w-3.5" />
                    {formatDateTime(task.dueAt)}
                    <span>•</span>
                    {formatDuration(task.plannedDurationMinutes)}
                  </div>
                  {task.status !== 'done' ? (
                    <button
                      type="button"
                      onClick={() => onDone(task)}
                      className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-50"
                    >
                      <CheckCircle2 className="h-4 w-4" />
                      Завершить
                    </button>
                  ) : null}
                </article>
              );
            })}
          </div>
        </section>
      ))}
    </div>
  );
}

function TaskCalendar({
  tasks,
  mode,
  anchorDate,
  selectedDate,
  highlightedTaskId,
  loading,
  onPrevious,
  onNext,
  onToday,
  onSelectDate,
  onCreateDate,
  onMoveTask,
  onResizeTask,
  onEdit,
  onDone,
  movingTaskId,
  resizingTaskId,
}: {
  tasks: TaskJournalItem[];
  mode: PeriodMode;
  anchorDate: Date;
  selectedDate: Date;
  highlightedTaskId: number | null;
  loading: boolean;
  onPrevious: () => void;
  onNext: () => void;
  onToday: () => void;
  onSelectDate: (date: Date) => void;
  onCreateDate: (date: Date) => void;
  onMoveTask: (task: TaskJournalItem, date: Date) => void;
  onResizeTask: (task: TaskJournalItem, deltaMinutes: -15 | 15) => void;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
  movingTaskId: number | null;
  resizingTaskId: number | null;
}) {
  const visibleDays = useMemo(() => {
    if (mode === 'day') {
      return [startOfDay(anchorDate)];
    }

    if (mode === 'week') {
      const start = startOfWeek(anchorDate);
      return Array.from({ length: 7 }, (_, index) => addDays(start, index));
    }

    const firstDay = startOfMonth(anchorDate);
    const start = startOfWeek(firstDay);
    return Array.from({ length: 42 }, (_, index) => addDays(start, index));
  }, [anchorDate, mode]);

  const tasksByDate = useMemo(() => {
    const map = new Map<string, TaskJournalItem[]>();
    tasks.forEach((task) => {
      const key = toDateKey(new Date(task.dueAt));
      const items = map.get(key) ?? [];
      items.push(task);
      map.set(key, items);
    });

    map.forEach((items) => {
      items.sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
    });

    return map;
  }, [tasks]);

  const selectedTasks = useMemo(
    () => tasksByDate.get(toDateKey(selectedDate)) ?? [],
    [selectedDate, tasksByDate],
  );
  const selectedTotalDuration = useMemo(
    () => selectedTasks.reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0),
    [selectedTasks],
  );
  const selectedWorkload = useMemo(() => {
    const byAssignee = new Map<string, {
      name: string;
      count: number;
      urgent: number;
      duration: number;
      intervals: Array<{ start: number; end: number }>;
    }>();
    selectedTasks.forEach((task) => {
      const participantNames = getTaskParticipants(task, 'assignee').map(getParticipantDisplayName).filter(Boolean);
      const names = unique(participantNames.length > 0 ? participantNames : [task.assigneeName || 'Без исполнителя']);
      const due = new Date(task.dueAt);
      const duration = normalizeTaskDuration(task.plannedDurationMinutes);
      const start = due.getTime();
      const end = Number.isNaN(start) ? Number.NaN : start + duration * 60 * 1000;

      names.forEach((name) => {
        const item = byAssignee.get(name) ?? { name, count: 0, urgent: 0, duration: 0, intervals: [] };
        item.count += 1;
        item.duration += duration;
        if (task.priority === 'high' || task.priority === 'urgent') {
          item.urgent += 1;
        }
        if (!Number.isNaN(start) && !Number.isNaN(end)) {
          item.intervals.push({ start, end });
        }
        byAssignee.set(name, item);
      });
    });

    return Array.from(byAssignee.values())
      .map((item) => {
        const sortedIntervals = item.intervals.slice().sort((a, b) => a.start - b.start || a.end - b.end);
        let overlaps = 0;
        for (let index = 1; index < sortedIntervals.length; index += 1) {
          if (intervalsOverlap(
            sortedIntervals[index - 1].start,
            sortedIntervals[index - 1].end,
            sortedIntervals[index].start,
            sortedIntervals[index].end,
          )) {
            overlaps += 1;
          }
        }

        return {
          name: item.name,
          count: item.count,
          urgent: item.urgent,
          duration: item.duration,
          overlaps,
          hasConflict: item.count >= 5 || item.duration >= 6 * 60 || overlaps > 0,
        };
      })
      .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name, 'ru'));
  }, [selectedTasks]);
  const periodTaskCount = visibleDays.reduce((count, day) => count + (tasksByDate.get(toDateKey(day))?.length ?? 0), 0);
  const calendarGridClass = mode === 'day' ? 'grid-cols-1' : 'grid-cols-7';
  const calendarMinWidthClass = mode === 'day' ? 'min-w-[320px]' : 'min-w-[860px]';

  return (
    <section className="rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-5 py-4 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">
            <CalendarDays className="h-4 w-4" />
            Календарь задач
          </div>
          <h2 className="mt-2 text-xl font-semibold text-slate-900">{formatPeriodTitle(anchorDate, mode)}</h2>
          <p className="mt-1 text-sm text-slate-500">
            {periodTaskCount > 0 ? `Найдено задач в периоде: ${periodTaskCount}` : 'На выбранный период задач нет'}
          </p>
        </div>

        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={onPrevious}
            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800"
            aria-label="Предыдущий период"
            title="Предыдущий период"
          >
            <ChevronLeft className="h-4 w-4" />
          </button>
          <button
            type="button"
            onClick={onToday}
            className="inline-flex h-10 items-center justify-center rounded-xl border border-slate-200 px-4 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
          >
            Сегодня
          </button>
          <button
            type="button"
            onClick={onNext}
            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800"
            aria-label="Следующий период"
            title="Следующий период"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
        </div>
      </div>

      {loading ? (
        <div className="px-6 py-12 text-center text-sm text-slate-500">Загружаем календарь задач...</div>
      ) : (
        <div className="grid gap-4 p-4 xl:grid-cols-[minmax(0,2fr)_minmax(320px,1fr)]">
          <div className="overflow-x-auto">
            {mode !== 'day' ? (
              <div className="mb-2 grid min-w-[860px] grid-cols-7 gap-2 text-center text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">
                {WEEKDAY_LABELS.map((label) => (
                  <div key={label}>{label}</div>
                ))}
              </div>
            ) : null}

            <div className={`grid gap-2 ${calendarMinWidthClass} ${calendarGridClass}`}>
              {visibleDays.map((day) => {
                const key = toDateKey(day);
                const dayTasks = tasksByDate.get(key) ?? [];
                const isSelected = isSameDay(day, selectedDate);
                const isCurrentMonth = mode !== 'month' || isSameMonth(day, anchorDate);
                const isCurrentDay = isSameDay(day, new Date());
                const activeDayTasks = dayTasks.filter((task) => isTaskJournalActive(task.status));
                const activeDayDuration = activeDayTasks.reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0);
                const dayLoadClass =
                  activeDayTasks.length >= 8 || activeDayDuration >= 8 * 60
                    ? 'border-rose-200 bg-rose-50/70'
                    : activeDayTasks.length >= 5 || activeDayDuration >= 5 * 60
                      ? 'border-amber-200 bg-amber-50/70'
                      : '';

                return (
                  <div
                    key={key}
                    data-testid={`task-calendar-day-${key}`}
                    onDragOver={(event) => {
                      if (event.dataTransfer.types.includes('application/x-task-id') || event.dataTransfer.types.includes('text/plain')) {
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                      }
                    }}
                    onDrop={(event) => {
                      event.preventDefault();
                      const taskId = Number(event.dataTransfer.getData('application/x-task-id') || event.dataTransfer.getData('text/plain'));
                      const task = tasks.find((item) => item.id === taskId);
                      if (task) {
                        onMoveTask(task, day);
                      }
                    }}
                    className={`min-h-[128px] rounded-xl border p-2 transition ${
                      isSelected
                        ? 'border-blue-300 bg-blue-50/60 ring-2 ring-blue-100'
                        : isCurrentMonth
                          ? dayLoadClass || 'border-slate-200 bg-white'
                          : 'border-slate-100 bg-slate-50/70'
                    }`}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <button
                        type="button"
                        onClick={() => onSelectDate(day)}
                        className="min-w-0 rounded-lg px-1 py-1 text-left transition hover:bg-white/70"
                      >
                        <span className={`block truncate text-sm font-semibold ${isCurrentMonth ? 'text-slate-900' : 'text-slate-400'}`}>
                          {mode === 'day' ? formatDateOnly(day) : day.getDate()}
                        </span>
                        {activeDayTasks.length >= 5 || activeDayDuration >= 5 * 60 ? (
                          <span className={`mt-0.5 block text-[11px] font-semibold ${activeDayTasks.length >= 8 || activeDayDuration >= 8 * 60 ? 'text-rose-600' : 'text-amber-600'}`}>
                            Высокая загрузка
                          </span>
                        ) : null}
                      </button>
                      <span className="flex shrink-0 items-center gap-1.5">
                        <button
                          type="button"
                          onClick={() => onCreateDate(day)}
                          className="inline-flex h-7 w-7 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-500 transition hover:border-blue-200 hover:text-blue-600"
                          title="Создать задачу на дату"
                          aria-label="Создать задачу на дату"
                        >
                          <Plus className="h-3.5 w-3.5" />
                        </button>
                        {isCurrentDay ? <span className="h-2 w-2 rounded-full bg-blue-500" /> : null}
                        {dayTasks.length > 0 ? (
                          <span className="rounded-full bg-slate-900 px-2 py-0.5 text-xs font-semibold text-white">{dayTasks.length}</span>
                        ) : null}
                      </span>
                    </div>

                    <div className="mt-2 space-y-1.5">
                      {dayTasks.slice(0, mode === 'day' ? 8 : 3).map((task) => (
                        <button
                          key={task.id}
                          type="button"
                          draggable={isTaskJournalActive(task.status)}
                          data-testid={`task-calendar-task-${task.id}`}
                          onDragStart={(event) => {
                            event.dataTransfer.setData('application/x-task-id', String(task.id));
                            event.dataTransfer.setData('text/plain', String(task.id));
                            event.dataTransfer.effectAllowed = 'move';
                          }}
                          onClick={() => onEdit(task)}
                          title={`${task.title}\n${getTaskClientName(task)}\nПеретащите карточку на другой день, чтобы перенести срок.`}
                          className={`block w-full rounded-lg border px-2 py-1.5 text-left text-xs transition hover:border-blue-200 hover:bg-blue-50 ${
                            movingTaskId === task.id
                              ? 'border-blue-300 bg-blue-100 opacity-60'
                              : highlightedTaskId === task.id
                                ? 'border-blue-300 bg-blue-50'
                                : 'border-slate-100 bg-slate-50'
                          }`}
                        >
                          <span className="block truncate font-semibold text-slate-900">{task.title}</span>
                          <span className="mt-0.5 block truncate text-slate-500">{getTaskClientName(task)}</span>
                          <span className="mt-1 block text-[11px] font-semibold text-slate-400">{formatDuration(task.plannedDurationMinutes)}</span>
                        </button>
                      ))}
                      {dayTasks.length > (mode === 'day' ? 8 : 3) ? (
                        <button
                          type="button"
                          onClick={() => onSelectDate(day)}
                          className="w-full rounded-lg px-2 py-1 text-left text-xs font-semibold text-blue-600 transition hover:bg-blue-50"
                        >
                          +{dayTasks.length - (mode === 'day' ? 8 : 3)} еще
                        </button>
                      ) : null}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>

          <aside className="rounded-xl border border-slate-200 bg-slate-50/70">
            <div className="border-b border-slate-200 px-4 py-4">
              <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">Загрузка за день</div>
              <div className="mt-1 flex flex-wrap items-center justify-between gap-2">
                <div>
                  <h3 className="font-semibold text-slate-900">{formatDateOnly(selectedDate)}</h3>
                  <p className="mt-1 text-sm text-slate-500">
                    {selectedTasks.length > 0 ? `${selectedTasks.length} задач` : 'Задач нет'}
                  </p>
                  {selectedTasks.length > 0 ? (
                    <p className="mt-0.5 text-xs text-slate-500">Плановая загрузка: {formatDuration(selectedTotalDuration)}</p>
                  ) : null}
                </div>
                <button
                  type="button"
                  onClick={() => onCreateDate(selectedDate)}
                  className="inline-flex items-center gap-2 rounded-xl border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-50"
                >
                  <Plus className="h-4 w-4" />
                  Создать
                </button>
              </div>
            </div>

            <div className="border-b border-slate-200 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">Исполнители</div>
              {selectedWorkload.length === 0 ? (
                <div className="mt-2 text-sm text-slate-400">Загрузки нет</div>
              ) : (
                <div className="mt-2 flex flex-wrap gap-2">
                  {selectedWorkload.map((item) => (
                    <span
                      key={item.name}
                      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ${
                        item.hasConflict
                          ? 'bg-rose-50 text-rose-700 ring-1 ring-rose-100'
                          : item.urgent > 0
                            ? 'bg-amber-50 text-amber-700 ring-1 ring-amber-100'
                            : 'bg-white text-slate-600 ring-1 ring-slate-200'
                      }`}
                      title={item.hasConflict ? 'Есть риск перегруза или пересечение по времени' : undefined}
                    >
                      {item.name}: {item.count} · {formatDuration(item.duration)}
                      {item.urgent > 0 ? ` · срочных ${item.urgent}` : ''}
                      {item.overlaps > 0 ? ` · пересечений ${item.overlaps}` : ''}
                    </span>
                  ))}
                </div>
              )}
              {selectedWorkload.some((item) => item.hasConflict) ? (
                <div className="mt-2 rounded-lg border border-rose-100 bg-rose-50 px-3 py-2 text-xs text-rose-700">
                  Есть риск конфликта: у исполнителя несколько задач на одно время или высокая дневная загрузка.
                </div>
              ) : null}
            </div>

            <div className="max-h-[460px] space-y-2 overflow-y-auto p-3">
              {selectedTasks.length === 0 ? (
                <div className="rounded-xl border border-dashed border-slate-200 bg-white px-3 py-8 text-center text-sm text-slate-400">
                  На этот день задач не найдено
                </div>
              ) : selectedTasks.map((task) => {
                const TypeIcon = getTypeMeta(task.type).icon;
                return (
                  <article key={task.id} className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
                    <div className="flex items-start justify-between gap-3">
                      <button type="button" onClick={() => onEdit(task)} className="min-w-0 text-left">
                        <div className="truncate text-sm font-semibold text-slate-900">{task.title}</div>
                        <div className="mt-1 truncate text-xs text-slate-500">{getTaskClientName(task)}</div>
                        {getTaskCaseTitle(task) ? <div className="mt-0.5 truncate text-xs text-slate-500">{getTaskCaseTitle(task)}</div> : null}
                      </button>
                      <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ${statusClass(task.status)}`}>
                        {getStatusLabel(task.status)}
                      </span>
                    </div>
                    <div className="mt-3 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                      <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2 py-1">
                        <Clock3 className="h-3.5 w-3.5" />
                        {formatDateTime(task.dueAt)}
                      </span>
                      <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2 py-1">
                        <TimerReset className="h-3.5 w-3.5" />
                        {formatDuration(task.plannedDurationMinutes)}
                      </span>
                      <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2 py-1">
                        <TypeIcon className="h-3.5 w-3.5" />
                        {getTypeMeta(task.type).label}
                      </span>
                    </div>
                    <div className="mt-3 flex items-center gap-2">
                      <button
                        type="button"
                        onClick={() => onResizeTask(task, -15)}
                        disabled={resizingTaskId === task.id || normalizeTaskDuration(task.plannedDurationMinutes) <= 15}
                        className="inline-flex h-9 flex-1 items-center justify-center rounded-xl border border-slate-200 bg-white text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                        title="Уменьшить длительность на 15 минут"
                      >
                        −15 мин
                      </button>
                      <button
                        type="button"
                        onClick={() => onResizeTask(task, 15)}
                        disabled={resizingTaskId === task.id || normalizeTaskDuration(task.plannedDurationMinutes) >= 480}
                        className="inline-flex h-9 flex-1 items-center justify-center rounded-xl border border-blue-200 bg-white text-sm font-semibold text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-50"
                        title="Увеличить длительность на 15 минут"
                      >
                        +15 мин
                      </button>
                    </div>
                    {isTaskJournalActive(task.status) ? (
                      <button
                        type="button"
                        onClick={() => onDone(task)}
                        className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-50"
                      >
                        <CheckCircle2 className="h-4 w-4" />
                        Завершить
                      </button>
                    ) : null}
                  </article>
                );
              })}
            </div>
          </aside>
        </div>
      )}
    </section>
  );
}

function TaskModal({
  state,
  assignees,
  clients,
  legalEntities,
  counterpartyOptions,
  currentUserId,
  currentUserName,
  onClose,
  onSave,
  onApprovalDecision,
}: {
  state: ModalState;
  assignees: TaskJournalAssignee[];
  clients: Client[];
  legalEntities: LegalEntitySummary[];
  counterpartyOptions: LookupSelectOption[];
  currentUserId: number | null;
  currentUserName: string;
  onClose: () => void;
  onSave: (form: TaskForm, taskId?: number) => void;
  onApprovalDecision: (
    taskId: number,
    decision: 'approved' | 'revisionRequested',
    resolution?: string,
    participantId?: number,
  ) => void;
}) {
  const toast = useToast();
  const { confirm } = useDialog();
  const taskId = state?.task?.id ?? null;
  const newFileInputRef = useRef<HTMLInputElement | null>(null);
  const versionInputRefs = useRef<Record<string, HTMLInputElement | null>>({});
  const [form, setForm] = useState<TaskForm>(() => {
    if (state?.task) {
      const rest = {
        clientId: state.task.clientId ?? null,
        legalEntityId: state.task.legalEntityId ?? null,
        clientCaseId: state.task.clientCaseId ?? null,
        objectName: state.task.objectName,
        title: state.task.title,
        description: state.task.description,
        assigneeUserId: state.task.assigneeUserId,
        assigneeName: state.task.assigneeName,
        dueAt: state.task.dueAt,
        type: state.task.type,
        priority: state.task.priority,
        status: state.task.status,
        result: state.task.result,
        plannedDurationMinutes: normalizeTaskDuration(state.task.plannedDurationMinutes),
        reminders: state.task.reminders,
        channels: state.task.channels,
        tags: state.task.tags,
        participants: state.task.participants ?? [],
      };
      return { ...rest, dueAt: toLocalInputValue(rest.dueAt) };
    }
    const next = emptyForm(currentUserName, currentUserId);
    return state?.dueAt ? { ...next, dueAt: toLocalInputValue(state.dueAt) } : next;
  });
  const [tagText, setTagText] = useState(() => form.tags.join(', '));
  const [comments, setComments] = useState<TaskJournalComment[]>([]);
  const [commentsLoading, setCommentsLoading] = useState(false);
  const [commentSaving, setCommentSaving] = useState(false);
  const [commentText, setCommentText] = useState('');
  const [files, setFiles] = useState<TaskJournalFile[]>([]);
  const [filesLoading, setFilesLoading] = useState(false);
  const [uploadingDocumentKey, setUploadingDocumentKey] = useState<string | null>(null);
  const selectedClient = useMemo(
    () => clients.find((client) => client.id === form.clientId) ?? null,
    [clients, form.clientId],
  );
  const selectedLegalEntity = useMemo(
    () => legalEntities.find((entity) => entity.id === form.legalEntityId) ?? null,
    [legalEntities, form.legalEntityId],
  );
  const selectedOwnerName = selectedClient?.name || getLegalEntityDisplayName(selectedLegalEntity);
  const approverParticipants = useMemo(
    () => (form.participants ?? []).filter((participant) => participant.role === 'approver'),
    [form.participants],
  );
  const fileGroups = useMemo(() => {
    const grouped = new Map<string, TaskJournalFile[]>();
    files.forEach((file) => {
      const current = grouped.get(file.documentKey) ?? [];
      current.push(file);
      grouped.set(file.documentKey, current);
    });

    return Array.from(grouped.entries())
      .map(([documentKeyValue, items]) => {
        const versions = items
          .slice()
          .sort((a, b) => b.version - a.version || new Date(b.uploadedAt).getTime() - new Date(a.uploadedAt).getTime());
        return {
          documentKey: documentKeyValue,
          latest: versions[0],
          versions,
        };
      })
      .sort((a, b) => new Date(b.latest.uploadedAt).getTime() - new Date(a.latest.uploadedAt).getTime());
  }, [files]);
  const { cases: clientCases, loading: casesLoading } = useClientCases(form.clientId ?? undefined, {
    enabled: Boolean(form.clientId || form.legalEntityId),
    legalEntityId: form.legalEntityId ?? undefined,
  });

  const update = <K extends keyof TaskForm>(key: K, value: TaskForm[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const selectedCounterpartyValue = form.clientId
    ? `client:${form.clientId}`
    : form.legalEntityId
      ? `legal:${form.legalEntityId}`
      : '';

  const handleCounterpartyChange = (value: string) => {
    const [kind, rawId] = value.split(':');
    const id = Number(rawId) || null;
    if (kind === 'legal' && id) {
      const entity = legalEntities.find((item) => item.id === id);
      setForm((prev) => ({
        ...prev,
        clientId: null,
        legalEntityId: id,
        clientCaseId: null,
        objectName: getLegalEntityDisplayName(entity),
      }));
      return;
    }

    const clientId = kind === 'client' ? id : null;
    const client = clients.find((item) => item.id === clientId);
    setForm((prev) => ({
      ...prev,
      clientId,
      legalEntityId: null,
      clientCaseId: null,
      objectName: client?.name ?? '',
    }));
  };

  const handleCaseChange = (value: string) => {
    const clientCaseId = Number(value) || null;
    const clientCase = clientCases.find((item) => item.id === clientCaseId);
    setForm((prev) => ({
      ...prev,
      clientCaseId,
      objectName: clientCase
        ? `${selectedOwnerName || prev.objectName} - ${clientCase.title}`
        : selectedOwnerName || prev.objectName,
    }));
  };

  const toggleArray = <T extends string>(key: 'reminders' | 'channels', value: T) => {
    setForm((prev) => {
      const current = prev[key] as T[];
      return {
        ...prev,
        [key]: current.includes(value) ? current.filter((item) => item !== value) : [...current, value],
      };
    });
  };

  const isParticipantSelected = (role: TaskJournalParticipantRole, userId: number) =>
    (form.participants ?? []).some((participant) => participant.role === role && participant.userId === userId);

  const toggleParticipant = (role: TaskJournalParticipantRole, assignee: TaskJournalAssignee) => {
    setForm((prev) => {
      const participants = prev.participants ?? [];
      const exists = participants.some((participant) => participant.role === role && participant.userId === assignee.id);

      return {
        ...prev,
        participants: exists
          ? participants.filter((participant) => !(participant.role === role && participant.userId === assignee.id))
          : [
              ...participants,
              {
                userId: assignee.id,
                role,
                displayName: assignee.name,
                name: assignee.name,
                email: assignee.email,
                sortOrder: participants.filter((participant) => participant.role === role).length,
                approvalStatus: 'pending',
                resolution: '',
              },
            ],
      };
    });
  };

  useEffect(() => {
    if (!taskId) {
      setComments([]);
      setCommentsLoading(false);
      return;
    }

    let active = true;
    setCommentsLoading(true);
    apiService.getTaskJournalComments(taskId)
      .then((items) => {
        if (active) {
          setComments(items);
        }
      })
      .catch((error) => {
        if (active) {
          toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить историю задачи.'), 'Журнал задач');
        }
      })
      .finally(() => {
        if (active) {
          setCommentsLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [taskId, toast]);

  useEffect(() => {
    if (!taskId) {
      setFiles([]);
      setFilesLoading(false);
      return;
    }

    let active = true;
    setFilesLoading(true);
    apiService.getTaskJournalFiles(taskId)
      .then((items) => {
        if (active) {
          setFiles(items);
        }
      })
      .catch((error) => {
        if (active) {
          toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить файлы задачи.'), 'Журнал задач');
        }
      })
      .finally(() => {
        if (active) {
          setFilesLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [taskId, toast]);

  const handleAddComment = async () => {
    const text = commentText.trim();
    if (!taskId || !text) return;

    setCommentSaving(true);
    try {
      const created = await apiService.createTaskJournalComment(taskId, text);
      setComments((previous) => [created, ...previous]);
      setCommentText('');
      toast.success('Комментарий добавлен.', 'Журнал задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось добавить комментарий.'), 'Журнал задач');
    } finally {
      setCommentSaving(false);
    }
  };

  const handleUploadFile = async (fileList: FileList | null | undefined, documentKey?: string) => {
    const file = fileList?.[0];
    if (!taskId || !file) return;

    const uploadKey = documentKey ?? '__new';
    setUploadingDocumentKey(uploadKey);
    try {
      const uploaded = await apiService.uploadTaskJournalFile(taskId, file, documentKey);
      setFiles((previous) => [uploaded, ...previous]);
      toast.success(documentKey ? 'Новая версия файла загружена.' : 'Файл добавлен к задаче.', 'Журнал задач');
      const historyItems = await apiService.getTaskJournalComments(taskId);
      setComments(historyItems);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить файл.'), 'Журнал задач');
    } finally {
      setUploadingDocumentKey(null);
    }
  };

  const handleDownloadFile = async (file: TaskJournalFile) => {
    if (!taskId) return;

    try {
      const response = await apiService.downloadTaskJournalFile(taskId, file.id);
      downloadBlob(response.fileName || file.originalFileName, response.blob);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось скачать файл.'), 'Журнал задач');
    }
  };

  const handleDeleteFile = async (file: TaskJournalFile) => {
    if (!taskId) return;

    const accepted = await confirm({
      title: 'Удалить файл?',
      description: `Версия v${file.version} файла "${file.originalFileName}" будет удалена из задачи.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;

    try {
      await apiService.deleteTaskJournalFile(taskId, file.id);
      setFiles((previous) => previous.filter((item) => item.id !== file.id));
      toast.success('Файл удален из задачи.', 'Журнал задач');
      const historyItems = await apiService.getTaskJournalComments(taskId);
      setComments(historyItems);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось удалить файл.'), 'Журнал задач');
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4 backdrop-blur-sm">
      <div className="flex max-h-[92vh] w-full max-w-5xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-6 py-5">
          <div>
            <h2 className="text-2xl font-semibold text-slate-900">{state?.mode === 'edit' ? 'Редактировать задачу' : 'Новая задача'}</h2>
            <p className="mt-1 text-sm text-slate-500">Заполните контрагента, исполнителя, срок и правила напоминаний.</p>
          </div>
          <button type="button" onClick={onClose} className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" aria-label="Закрыть">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 overflow-y-auto px-6 py-5">
          <div className="grid gap-5 lg:grid-cols-2">
            <Field label="Контрагент">
              <LookupSelect
                value={selectedCounterpartyValue}
                options={counterpartyOptions}
                onChange={handleCounterpartyChange}
                placeholder="Выберите контрагента"
                title="Выбор контрагента"
                dictionaryLabel="Открыть справочник"
                searchPlaceholder="Поиск по ФИО, названию, ИНН, телефону или email"
                emptyOptionLabel="Контрагент не выбран"
                emptyOptionDescription="Контрагент обязателен для задачи"
                buttonClassName="h-11 border-slate-200 focus:border-blue-400"
                noOptionsText="Контрагенты не найдены"
              />
            </Field>
            <Field label="Исполнитель">
              <select
                value={form.assigneeUserId ?? ''}
                onChange={(event) => {
                  const nextId = Number(event.target.value) || null;
                  const selected = assignees.find((assignee) => assignee.id === nextId);
                  update('assigneeUserId', nextId);
                  update('assigneeName', selected?.name ?? form.assigneeName);
                }}
                className="form-input"
              >
                <option value="">Без привязки к пользователю</option>
                {assignees.map((assignee) => (
                  <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                ))}
              </select>
            </Field>
            <Field label="Имя исполнителя">
              <input value={form.assigneeName} onChange={(event) => update('assigneeName', event.target.value)} className="form-input" />
            </Field>
            <Field label="Наименование задачи">
              <input value={form.title} onChange={(event) => update('title', event.target.value)} className="form-input" placeholder="Что нужно сделать" />
            </Field>
            <Field label="Дело">
              <select
                aria-label="Дело"
                value={form.clientCaseId ?? ''}
                onChange={(event) => handleCaseChange(event.target.value)}
                disabled={(!form.clientId && !form.legalEntityId) || casesLoading}
                className="form-input disabled:bg-slate-50 disabled:text-slate-400"
              >
                <option value="">Без дела</option>
                {clientCases.map((clientCase) => (
                  <option key={clientCase.id} value={clientCase.id}>{clientCase.title}</option>
                ))}
              </select>
            </Field>
            <Field label="Дата и время исполнения">
              <input type="datetime-local" value={form.dueAt} onChange={(event) => update('dueAt', event.target.value)} className="form-input" />
            </Field>
            <Field label="Длительность">
              <select
                value={normalizeTaskDuration(form.plannedDurationMinutes)}
                onChange={(event) => update('plannedDurationMinutes', Number(event.target.value))}
                className="form-input"
              >
                {[15, 30, 45, 60, 90, 120, 180, 240, 360, 480].map((minutes) => (
                  <option key={minutes} value={minutes}>{formatDuration(minutes)}</option>
                ))}
              </select>
            </Field>
            <Field label="Тип задачи">
              <select value={form.type} onChange={(event) => update('type', event.target.value as TaskJournalType)} className="form-input">
                {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Field label="Приоритет">
              <select value={form.priority} onChange={(event) => update('priority', event.target.value as TaskJournalPriority)} className="form-input">
                {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
            <Field label="Статус">
              <select value={form.status} onChange={(event) => update('status', event.target.value as TaskJournalStatus)} className="form-input">
                {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </Field>
          </div>

          <section className="mt-5 rounded-2xl border border-slate-200 bg-slate-50/70 p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Участники и согласование</h3>
                <p className="mt-1 text-sm text-slate-500">Выберите дополнительных исполнителей, наблюдателей и согласующих из пользователей системы.</p>
              </div>
              <span className="rounded-full bg-white px-3 py-1 text-xs font-semibold text-slate-500 ring-1 ring-slate-200">
                {(form.participants ?? []).length} участников
              </span>
            </div>
            <div className="mt-4 grid gap-4 lg:grid-cols-3">
              {(['assignee', 'approver', 'observer'] as TaskJournalParticipantRole[]).map((role) => (
                <Field key={role} label={PARTICIPANT_ROLE_LABELS[role]}>
                  <div className="max-h-52 space-y-2 overflow-y-auto rounded-xl border border-slate-200 bg-white p-2">
                    {assignees.length === 0 ? (
                      <div className="px-3 py-6 text-center text-sm text-slate-400">Пользователи не найдены</div>
                    ) : assignees.map((assignee) => (
                      <label key={`${role}-${assignee.id}`} className="flex cursor-pointer items-start gap-2 rounded-lg px-2 py-2 text-sm transition hover:bg-slate-50">
                        <input
                          type="checkbox"
                          checked={isParticipantSelected(role, assignee.id)}
                          onChange={() => toggleParticipant(role, assignee)}
                          className="mt-0.5 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                        />
                        <span className="min-w-0">
                          <span className="block truncate font-semibold text-slate-700">{assignee.name}</span>
                          {assignee.email ? <span className="block truncate text-xs text-slate-400">{assignee.email}</span> : null}
                        </span>
                      </label>
                    ))}
                  </div>
                </Field>
              ))}
            </div>

            {state?.task && approverParticipants.length > 0 ? (
              <div className="mt-4 rounded-xl border border-violet-100 bg-white p-3">
                <div className="text-sm font-semibold text-slate-800">Очередь согласования</div>
                <div className="mt-3 space-y-2">
                  {approverParticipants
                    .slice()
                    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))
                    .map((participant, index) => {
                      const isCurrentApprover = participant.userId === currentUserId;
                      const statusLabel =
                        participant.approvalStatus === 'approved'
                          ? 'Согласовано'
                          : participant.approvalStatus === 'revisionRequested'
                            ? 'На доработку'
                            : 'Ожидает решения';

                      return (
                        <div key={`${participant.userId ?? participant.displayName}-${index}`} className="flex flex-col gap-2 rounded-lg border border-slate-100 px-3 py-2 sm:flex-row sm:items-center sm:justify-between">
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-slate-700">{index + 1}. {getParticipantDisplayName(participant)}</div>
                            <div className="mt-0.5 text-xs text-slate-400">{statusLabel}</div>
                            {participant.resolution ? <div className="mt-1 text-xs text-slate-500">{participant.resolution}</div> : null}
                          </div>
                          {isCurrentApprover && participant.approvalStatus !== 'approved' ? (
                            <div className="flex shrink-0 gap-2">
                              <button
                                type="button"
                                onClick={() => onApprovalDecision(state.task!.id, 'approved', form.result.trim(), participant.id)}
                                className="rounded-lg border border-emerald-200 px-3 py-1.5 text-xs font-semibold text-emerald-700 transition hover:bg-emerald-50"
                              >
                                Согласовать
                              </button>
                              <button
                                type="button"
                                onClick={() => onApprovalDecision(state.task!.id, 'revisionRequested', form.result.trim(), participant.id)}
                                className="rounded-lg border border-orange-200 px-3 py-1.5 text-xs font-semibold text-orange-700 transition hover:bg-orange-50"
                              >
                                На доработку
                              </button>
                            </div>
                          ) : null}
                        </div>
                      );
                    })}
                </div>
              </div>
            ) : null}
          </section>

          <div className="mt-5 grid gap-5 lg:grid-cols-2">
            <Field label="Описание">
              <textarea value={form.description} onChange={(event) => update('description', event.target.value)} className="form-textarea" rows={5} placeholder="Контекст, телефоны, комментарии, откуда пришла задача" />
            </Field>
            <Field label="Результат">
              <textarea value={form.result} onChange={(event) => update('result', event.target.value)} className="form-textarea" rows={5} placeholder="Итог выполнения или причина ожидания" />
            </Field>
          </div>

          <div className="mt-5 grid gap-5 lg:grid-cols-2">
            <Field label="Напоминания">
              <div className="grid gap-2 sm:grid-cols-2">
                {REMINDER_OPTIONS.map((option) => (
                  <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700">
                    <input type="checkbox" checked={form.reminders.includes(option.value)} onChange={() => toggleArray('reminders', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
                    {option.label}
                  </label>
                ))}
              </div>
            </Field>
            <Field label="Каналы уведомлений">
              <div className="grid gap-2 sm:grid-cols-3">
                {CHANNEL_OPTIONS.map((option) => {
                  const Icon = option.icon;
                  return (
                    <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700">
                      <input type="checkbox" checked={form.channels.includes(option.value)} onChange={() => toggleArray('channels', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
                      <Icon className="h-4 w-4 text-slate-400" />
                      {option.label}
                    </label>
                  );
                })}
              </div>
            </Field>
          </div>

          <Field label="Теги" className="mt-5">
            <input
              value={tagText}
              onChange={(event) => {
                setTagText(event.target.value);
                update('tags', unique(event.target.value.split(',')));
              }}
              className="form-input"
              placeholder="контроль, платеж, звонок"
            />
          </Field>

          <section className="mt-5 rounded-2xl border border-slate-200 bg-white p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">
                  <FileText className="h-4 w-4 text-indigo-500" />
                  Файлы и версии
                </h3>
                <p className="mt-1 text-sm text-slate-500">Документы по задаче с версиями и историей загрузок.</p>
              </div>
              {taskId ? (
                <button
                  type="button"
                  onClick={() => newFileInputRef.current?.click()}
                  disabled={uploadingDocumentKey !== null}
                  className="inline-flex items-center justify-center gap-2 rounded-xl border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  <Plus className="h-4 w-4" />
                  {uploadingDocumentKey === '__new' ? 'Загружаем...' : 'Добавить файл'}
                </button>
              ) : null}
            </div>

            <input
              ref={newFileInputRef}
              type="file"
              className="hidden"
              onChange={(event) => {
                void handleUploadFile(event.currentTarget.files);
                event.currentTarget.value = '';
              }}
            />

            {!taskId ? (
              <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                Файлы можно будет добавить после сохранения задачи.
              </div>
            ) : filesLoading ? (
              <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">Загружаем файлы...</div>
            ) : fileGroups.length === 0 ? (
              <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">Файлы пока не добавлены.</div>
            ) : (
              <div className="mt-4 space-y-3">
                {fileGroups.map((group) => (
                  <article key={group.documentKey} className="rounded-xl border border-slate-200 bg-slate-50/70 p-3">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-white text-indigo-600 ring-1 ring-indigo-100">
                            <FileText className="h-4 w-4" />
                          </span>
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-slate-900">{group.latest.originalFileName}</div>
                            <div className="text-xs text-slate-500">
                              Последняя версия v{group.latest.version} · {formatFileSize(group.latest.sizeBytes)} · {formatDateTime(group.latest.uploadedAt)}
                            </div>
                          </div>
                        </div>
                      </div>
                      <div className="flex shrink-0 flex-wrap gap-2">
                        <input
                          ref={(node) => {
                            versionInputRefs.current[group.documentKey] = node;
                          }}
                          type="file"
                          className="hidden"
                          onChange={(event) => {
                            void handleUploadFile(event.currentTarget.files, group.documentKey);
                            event.currentTarget.value = '';
                          }}
                        />
                        <button
                          type="button"
                          onClick={() => versionInputRefs.current[group.documentKey]?.click()}
                          disabled={uploadingDocumentKey !== null}
                          className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-60"
                        >
                          <Plus className="h-4 w-4" />
                          {uploadingDocumentKey === group.documentKey ? 'Загружаем...' : 'Новая версия'}
                        </button>
                      </div>
                    </div>

                    <div className="mt-3 divide-y divide-slate-200 overflow-hidden rounded-lg border border-slate-200 bg-white">
                      {group.versions.map((file) => (
                        <div key={file.id} className="flex flex-col gap-3 px-3 py-2 sm:flex-row sm:items-center sm:justify-between">
                          <div className="min-w-0 text-sm">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${
                                file.id === group.latest.id ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'
                              }`}>
                                v{file.version}{file.id === group.latest.id ? ' · актуальная' : ''}
                              </span>
                              <span className="truncate font-medium text-slate-800">{file.originalFileName}</span>
                            </div>
                            <div className="mt-1 text-xs text-slate-500">
                              {formatFileSize(file.sizeBytes)} · {file.uploadedByName} · {formatDateTime(file.uploadedAt)}
                            </div>
                          </div>
                          <div className="flex shrink-0 gap-2">
                            <button
                              type="button"
                              onClick={() => void handleDownloadFile(file)}
                              className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                              title="Скачать"
                              aria-label="Скачать файл"
                            >
                              <Download className="h-4 w-4" />
                            </button>
                            <button
                              type="button"
                              onClick={() => void handleDeleteFile(file)}
                              className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-rose-100 text-rose-600 transition hover:bg-rose-50"
                              title="Удалить"
                              aria-label="Удалить файл"
                            >
                              <Trash2 className="h-4 w-4" />
                            </button>
                          </div>
                        </div>
                      ))}
                    </div>
                  </article>
                ))}
              </div>
            )}
          </section>

          <section className="mt-5 rounded-2xl border border-slate-200 bg-white p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">
                  <History className="h-4 w-4 text-blue-500" />
                  Комментарии и история
                </h3>
                <p className="mt-1 text-sm text-slate-500">Комментарии пользователей и системные изменения по задаче.</p>
              </div>
              {taskId ? (
                <span className="rounded-full bg-slate-50 px-3 py-1 text-xs font-semibold text-slate-500 ring-1 ring-slate-200">
                  {comments.length} записей
                </span>
              ) : null}
            </div>

            {!taskId ? (
              <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                Комментарии и история появятся после сохранения задачи.
              </div>
            ) : (
              <>
                <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50 p-3">
                  <textarea
                    value={commentText}
                    onChange={(event) => setCommentText(event.target.value)}
                    className="form-textarea min-h-[84px] bg-white"
                    rows={3}
                    placeholder="Введите комментарий"
                    maxLength={4000}
                  />
                  <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                    <span className="text-xs text-slate-400">{commentText.trim().length}/4000</span>
                    <button
                      type="button"
                      onClick={handleAddComment}
                      disabled={commentSaving || !commentText.trim()}
                      className="inline-flex items-center justify-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:bg-slate-300"
                    >
                      <MessageSquare className="h-4 w-4" />
                      {commentSaving ? 'Добавляем...' : 'Добавить комментарий'}
                    </button>
                  </div>
                </div>

                <div className="mt-4 space-y-3">
                  {commentsLoading ? (
                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">Загружаем историю...</div>
                  ) : comments.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">История пока пустая.</div>
                  ) : comments.map((comment) => {
                    const isSystem = comment.kind === 'system';
                    const Icon = isSystem ? History : MessageSquare;
                    return (
                      <article
                        key={comment.id}
                        className={`rounded-xl border px-4 py-3 ${
                          isSystem
                            ? 'border-blue-100 bg-blue-50/60'
                            : 'border-slate-200 bg-white'
                        }`}
                      >
                        <div className="flex flex-wrap items-center gap-2">
                          <span className={`inline-flex h-8 w-8 items-center justify-center rounded-full ${
                            isSystem ? 'bg-blue-100 text-blue-700' : 'bg-slate-100 text-slate-600'
                          }`}>
                            <Icon className="h-4 w-4" />
                          </span>
                          <div className="min-w-0">
                            <div className="flex flex-wrap items-center gap-2 text-sm">
                              <span className="font-semibold text-slate-800">{comment.authorName}</span>
                              <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${
                                isSystem ? 'bg-blue-100 text-blue-700' : 'bg-slate-100 text-slate-500'
                              }`}>
                                {isSystem ? 'История' : 'Комментарий'}
                              </span>
                            </div>
                            <div className="text-xs text-slate-400">{formatDateTime(comment.createdAt)}</div>
                          </div>
                        </div>
                        <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-700">{comment.text}</p>
                      </article>
                    );
                  })}
                </div>
              </>
            )}
          </section>
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 px-6 py-4 sm:flex-row sm:justify-end">
          <button type="button" onClick={onClose} className="rounded-xl border border-slate-300 px-5 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50">Отмена</button>
          <button type="button" onClick={() => onSave(form, state?.task?.id)} className="rounded-xl bg-slate-900 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800">
            {state?.mode === 'edit' ? 'Сохранить' : 'Создать задачу'}
          </button>
        </div>
      </div>
      <style>{`
        .form-input {
          height: 44px;
          width: 100%;
          border-radius: 12px;
          border: 1px solid rgb(226 232 240);
          background: white;
          padding: 0 12px;
          font-size: 14px;
          color: rgb(51 65 85);
          outline: none;
        }
        .form-input:focus,
        .form-textarea:focus {
          border-color: rgb(96 165 250);
          box-shadow: 0 0 0 2px rgb(219 234 254);
        }
        .form-textarea {
          width: 100%;
          border-radius: 12px;
          border: 1px solid rgb(226 232 240);
          background: white;
          padding: 10px 12px;
          font-size: 14px;
          color: rgb(51 65 85);
          outline: none;
          resize: vertical;
        }
      `}</style>
    </div>
  );
}

function Field({ label, children, className = '' }: { label: string; children: ReactNode; className?: string }) {
  return (
    <div className={`block ${className}`}>
      <span className="mb-2 block text-sm font-semibold text-slate-700">{label}</span>
      {children}
    </div>
  );
}
