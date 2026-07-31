import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import {
  AlarmClock,
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ArrowUpDown,
  Bell,
  Bookmark,
  CalendarDays,
  CalendarClock,
  ChevronLeft,
  ChevronRight,
  CheckCircle2,
  ClipboardList,
  Clock3,
  Copy,
  Download,
  Eye,
  FileText,
  History,
  LayoutGrid,
  ListFilter,
  Loader2,
  Mail,
  MessageSquare,
  MoreHorizontal,
  Paperclip,
  Palette,
  Pencil,
  Phone,
  Play,
  Plus,
  Search,
  Send,
  ShieldAlert,
  Scale,
  TimerReset,
  Trash2,
  UploadCloud,
  UsersRound,
  WalletCards,
  X,
  XCircle,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useClients } from '../../hooks/useClients';
import { useClientCases } from '../../hooks/useClientCases';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { apiService, type TaskJournalSettingsDto } from '../../services/api';
import {
  clearOfflineCalendarSnapshot,
  getOfflineCalendarTrustedDevice,
  loadOfflineCalendarSnapshot,
  saveOfflineCalendarSnapshot,
  setOfflineCalendarTrustedDevice,
  type OfflineCalendarSnapshot,
} from '../../services/offlineCalendarStore';
import {
  TaskStickerBadge,
  TaskStickerStylesProvider,
} from './TaskStickerBadge';
import {
  cloneDefaultTaskStickerStyles,
  normalizeTaskStickerStyles,
  taskPriorityStickerKey,
  type TaskStickerKey,
} from '../../utils/taskStickerStyles';
import type { CaseCalendarEvent, CaseCalendarSummary, Client, ClientCase, LegalEntitySummary } from '../../types';
import type { MenuSectionPermissions, TaskSectionPermissions } from '../../types/permissions';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import {
  formatDateTimeInTimeZone,
  fromTimeZoneDateTimeInput,
  getZonedParts,
  toTimeZoneDateKey,
  toTimeZoneDateTimeInput,
} from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import {
  isTaskJournalActive,
  TASK_JOURNAL_EVENT,
  type TaskJournalAssignee,
  type TaskJournalCaseAutomationCandidate,
  type TaskBillingContext,
  type TaskJournalChannel,
  type TaskJournalComment,
  type TaskJournalDeadlineChangeRequest,
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
import type { InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';
import {
  DEFAULT_TASK_NOTIFICATION_DEFAULTS,
  parseTaskNotificationDefaults,
  TASK_NOTIFICATION_DEFAULTS_SETTING_KEY,
  TASK_REMINDER_DEFAULT_OPTIONS,
  type TaskNotificationDefaults,
} from '../../utils/taskNotificationDefaults';
import { applyTaskTemplateDefaults, type TaskTemplateField } from '../../utils/taskTemplateApplication';
import {
  FORCED_TASK_SNOOZE_OPTIONS,
  FORCED_TASK_SNOOZE_STORAGE_KEY,
  FORCED_TASK_SNOOZES_EVENT,
  getForcedTaskApprovalSnoozeKey,
  getForcedTaskDeadlineChangeSnoozeKey,
  readForcedTaskSnoozes,
  snoozeForcedTaskActions,
} from '../../utils/forcedTaskActionSnoozes';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import { TaskTemplatesPanel } from './TaskTemplatesPanel';

type ViewMode = 'list' | 'board' | 'calendar' | 'timeline';
type PeriodMode = 'day' | 'week' | 'month' | 'all';
type QuickFilter = 'all' | 'assignedToMe' | 'authoredByMe';
type MetricFilter = 'none' | 'active' | 'overdue' | 'today' | 'highPriority';
type TaskStatusFilter = TaskJournalStatus | 'active' | 'all' | 'overdue';
type TaskCalendarColorMode = 'smart' | 'priority' | 'status';
type TaskCalendarConflictPolicy = 'warn' | 'block';
type TaskCalendarLoadWarningMode = 'strict' | 'standard' | 'relaxed';
type IcsReminderPreset = 'default' | 'hour' | 'twoHours' | 'day';
type TaskPlanningReportFormat = 'html' | 'word' | 'pdf';
type TaskJournalSortKey = 'counterparty' | 'title' | 'assignee' | 'author' | 'dueAt' | 'type' | 'result' | 'status';
type TaskTableColumnKey = TaskJournalSortKey | 'actions';
type TaskTableLayoutSettings = {
  getColumnWidth: (key: TaskTableColumnKey) => number;
  handleResizeStart: (key: TaskTableColumnKey, event: ReactMouseEvent<HTMLDivElement>) => void;
};
type TaskJournalContextFilter = {
  clientId?: number | null;
  legalEntityId?: number | null;
  objectName?: string | null;
  title?: string | null;
};
type ModalState = {
  mode: 'create' | 'edit' | 'view';
  task?: TaskJournalItem;
  dueAt?: string;
  defaults?: Partial<TaskJournalUpsertPayload>;
} | null;
type DeadlineChangeDecisionPromptState = {
  task: TaskJournalItem;
  request: TaskJournalDeadlineChangeRequest;
} | null;
type ApprovalDecisionPromptState = {
  task: TaskJournalItem;
  participant: TaskJournalParticipant;
} | null;
type TaskJournalMode = 'journal' | 'approvals';
type TaskApprovalFinalAction = 'complete' | 'returnToAssignee';
type TaskJournalProps = {
  initialView?: ViewMode;
  mode?: TaskJournalMode;
  embedded?: boolean;
  contextFilter?: TaskJournalContextFilter;
  underControlOnly?: boolean;
  routeTaskId?: number | null;
  onRouteTaskChange?: (taskId: number | null) => void;
  onOpenClient?: (clientId: number, caseId?: number | null) => void | Promise<void>;
  onOpenCase?: (caseId: number, clientId?: number | null) => void | Promise<void>;
  onOpenLegalEntity?: (legalEntityId: number) => void | Promise<void>;
  onCreateInvoiceDraft?: (draft: InvoiceEditorDraftState) => void;
  overdueFilterSignal?: number;
};

type TaskCreationWizardState = {
  defaults: Partial<TaskJournalUpsertPayload>;
  dueAt?: string;
} | null;

type TaskForm = TaskJournalUpsertPayload & { dueAt: string; approvalDueAt: string | null };

function isKeyword(value: string | null | undefined): value is string {
  return Boolean(value);
}

type TaskJournalFilterPreferences = {
  viewMode: ViewMode;
  calendarColorMode: TaskCalendarColorMode;
  calendarConflictPolicy: TaskCalendarConflictPolicy;
  calendarLoadWarningMode: TaskCalendarLoadWarningMode;
  icsReminderPreset: IcsReminderPreset;
  quickFilter: QuickFilter;
  metricFilter: MetricFilter;
  search: string;
  dateFrom: string;
  dateTo: string;
  statusFilter: TaskStatusFilter;
  counterpartyFilter: string;
  caseFilter: string;
  assigneeFilter: string;
  typeFilter: TaskJournalType | 'all';
  priorityFilter: TaskJournalPriority | 'all';
  periodMode: PeriodMode;
  sortBy: TaskJournalSortKey;
  sortDir: 'asc' | 'desc';
};
type TaskJournalSavedView = {
  id: string;
  name: string;
  filters: TaskJournalFilterPreferences;
  updatedAt: string;
};

const DEFAULT_TASK_JOURNAL_SETTINGS: TaskJournalSettingsDto = {
  restrictExecutorEditing: true,
  allowExecutorDeadlineChangeRequests: true,
  executorCompletionAllowedUserIds: [],
  requireCompletionResult: true,
  dailyOverdueReminderEnabled: true,
  allowAdminCompleteAnyTask: true,
  approvalDueDays: 5,
  taskCardWindowMode: 'legacy',
  approvalQuickActionMode: 'completionButton',
  caseInactivityAutomationEnabled: true,
  caseInactivityDays: 14,
  caseInactivityLimit: 25,
  caseInactivityDefaultAssigneeUserId: null,
  caseInactivityEscalationDays: 30,
  caseInactivityEscalationUserId: null,
  caseInactivityFirstRepeatAfterDays: 2,
  caseInactivityCriticalRepeatAfterDays: 4,
  caseInactivityRunLocalTime: '09:00',
  caseInactivityScheduleTimeZone: 'Europe/Moscow',
  stickerStyles: cloneDefaultTaskStickerStyles(),
};

const AUTOMATION_CANDIDATES_SEARCH_DELAY_MS = 300;
const TASK_JOURNAL_TABLE_SETTINGS_KEY = 'task_journal_table_settings_v1';
const TASK_JOURNAL_TABLE_COLUMNS: Array<{ key: TaskTableColumnKey; defaultWidth: number }> = [
  { key: 'counterparty', defaultWidth: 200 },
  { key: 'title', defaultWidth: 290 },
  { key: 'assignee', defaultWidth: 180 },
  { key: 'author', defaultWidth: 170 },
  { key: 'dueAt', defaultWidth: 180 },
  { key: 'type', defaultWidth: 130 },
  { key: 'result', defaultWidth: 260 },
  { key: 'status', defaultWidth: 160 },
  { key: 'actions', defaultWidth: 156 },
];

function buildAutomationCandidatesRequestKey(settings: TaskJournalSettingsDto, searchValue: string) {
  return [
    settings.caseInactivityDays,
    Math.min(settings.caseInactivityLimit, 20),
    settings.caseInactivityEscalationDays,
    searchValue.trim(),
  ].join('|');
}

const STATUS_OPTIONS: Array<{ value: TaskJournalStatus; label: string }> = [
  { value: 'new', label: 'Новая' },
  { value: 'inProgress', label: 'В работе' },
  { value: 'waiting', label: 'Ожидает' },
  { value: 'onApproval', label: 'На согласовании' },
  { value: 'approved', label: 'Согласовано' },
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
  { value: 'courtHearing', label: 'Суд. заседание', icon: Scale },
  { value: 'meeting', label: 'Встреча', icon: UsersRound },
];

const PRIORITY_OPTIONS: Array<{ value: TaskJournalPriority; label: string }> = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Срочно' },
];

const TASK_CALENDAR_COLOR_MODE_OPTIONS: Array<{ value: TaskCalendarColorMode; label: string }> = [
  { value: 'smart', label: 'Риски' },
  { value: 'priority', label: 'Приоритет' },
  { value: 'status', label: 'Статус' },
];

const TASK_CALENDAR_CONFLICT_POLICY_OPTIONS: Array<{ value: TaskCalendarConflictPolicy; label: string }> = [
  { value: 'warn', label: 'Предупреждать' },
  { value: 'block', label: 'Запрещать' },
];

const TASK_CALENDAR_LOAD_WARNING_MODE_OPTIONS: Array<{ value: TaskCalendarLoadWarningMode; label: string }> = [
  { value: 'strict', label: 'Строго' },
  { value: 'standard', label: 'Обычно' },
  { value: 'relaxed', label: 'Мягко' },
];

const ICS_REMINDER_PRESET_OPTIONS: Array<{ value: IcsReminderPreset; label: string; minutes: number[] }> = [
  { value: 'default', label: '1 ч + 15 мин', minutes: [60, 15] },
  { value: 'hour', label: '1 час', minutes: [60] },
  { value: 'twoHours', label: '2 ч + 30 мин', minutes: [120, 30] },
  { value: 'day', label: '1 день', minutes: [1440] },
];

const BULK_STATUS_OPTIONS = STATUS_OPTIONS.filter((option) => option.value !== 'approved');

const REMINDER_OPTIONS: Array<{ value: TaskJournalReminder; label: string }> = TASK_REMINDER_DEFAULT_OPTIONS;

const CHANNEL_OPTIONS: Array<{ value: TaskJournalChannel; label: string; icon: typeof Bell }> = [
  { value: 'inApp', label: 'В системе', icon: Bell },
  { value: 'email', label: 'Email', icon: Mail },
  { value: 'telegram', label: 'Telegram', icon: Send },
];

const TASK_JOURNAL_FILTERS_STORAGE_PREFIX = 'pp.task_journal.filters.v1';
const TASK_JOURNAL_SAVED_VIEWS_STORAGE_PREFIX = 'pp.task_journal.views.v1';

const QUICK_FILTER_OPTIONS: Array<{ value: QuickFilter; label: string; icon: typeof ClipboardList }> = [
  { value: 'all', label: 'Все', icon: ListFilter },
  { value: 'assignedToMe', label: 'Поручено мне', icon: CheckCircle2 },
  { value: 'authoredByMe', label: 'Я поручил', icon: Send },
];

const METRIC_FILTER_LABELS: Record<MetricFilter, string> = {
  none: 'Без быстрого фильтра',
  active: 'Активные',
  overdue: 'Просрочены',
  today: 'Сегодня',
  highPriority: 'Высокий приоритет',
};

const PARTICIPANT_ROLE_LABELS: Record<TaskJournalParticipantRole, string> = {
  assignee: 'Дополнительные исполнители',
  approver: 'Согласующие',
  observer: 'Наблюдатели',
};

function getTaskJournalFilterStorageKey(userId: number | null, mode: TaskJournalMode = 'journal') {
  const ownerKey = userId ? `user:${userId}` : 'guest';
  return mode === 'journal'
    ? `${TASK_JOURNAL_FILTERS_STORAGE_PREFIX}.${ownerKey}`
    : `${TASK_JOURNAL_FILTERS_STORAGE_PREFIX}.${mode}.${ownerKey}`;
}

function getTaskJournalSavedViewsStorageKey(userId: number | null, mode: TaskJournalMode = 'journal') {
  const ownerKey = userId ? `user:${userId}` : 'guest';
  return mode === 'journal'
    ? `${TASK_JOURNAL_SAVED_VIEWS_STORAGE_PREFIX}.${ownerKey}`
    : `${TASK_JOURNAL_SAVED_VIEWS_STORAGE_PREFIX}.${mode}.${ownerKey}`;
}

function getDefaultQuickFilter(_userId: number | null, mode: TaskJournalMode = 'journal'): QuickFilter {
  if (mode === 'approvals') {
    return 'all';
  }

  return 'all';
}

function createDefaultFilterPreferences(userId: number | null, mode: TaskJournalMode = 'journal', viewMode: ViewMode = 'list'): TaskJournalFilterPreferences {
  return {
    viewMode,
    calendarColorMode: 'smart',
    calendarConflictPolicy: 'warn',
    calendarLoadWarningMode: 'standard',
    icsReminderPreset: 'default',
    quickFilter: getDefaultQuickFilter(userId, mode),
    metricFilter: 'none',
    search: '',
    dateFrom: '',
    dateTo: '',
    statusFilter: 'active',
    counterpartyFilter: 'all',
    caseFilter: 'all',
    assigneeFilter: 'all',
    typeFilter: 'all',
    priorityFilter: 'all',
    periodMode: 'day',
    sortBy: 'dueAt',
    sortDir: 'asc',
  };
}

function isQuickFilter(value: unknown): value is QuickFilter {
  return value === 'all' || value === 'assignedToMe' || value === 'authoredByMe';
}

function isViewMode(value: unknown): value is ViewMode {
  return value === 'list' || value === 'board' || value === 'calendar' || value === 'timeline';
}

function isTaskCalendarColorMode(value: unknown): value is TaskCalendarColorMode {
  return value === 'smart' || value === 'priority' || value === 'status';
}

function isTaskCalendarConflictPolicy(value: unknown): value is TaskCalendarConflictPolicy {
  return value === 'warn' || value === 'block';
}

function isTaskCalendarLoadWarningMode(value: unknown): value is TaskCalendarLoadWarningMode {
  return value === 'strict' || value === 'standard' || value === 'relaxed';
}

function isIcsReminderPreset(value: unknown): value is IcsReminderPreset {
  return ICS_REMINDER_PRESET_OPTIONS.some((option) => option.value === value);
}

function isMetricFilter(value: unknown): value is MetricFilter {
  return value === 'none' || value === 'active' || value === 'overdue' || value === 'today' || value === 'highPriority';
}

function isStatusFilter(value: unknown): value is TaskStatusFilter {
  return value === 'active' || value === 'all' || value === 'overdue' || STATUS_OPTIONS.some((option) => option.value === value);
}

function isTypeFilter(value: unknown): value is TaskJournalType | 'all' {
  return value === 'all' || TYPE_OPTIONS.some((option) => option.value === value);
}

function isPriorityFilter(value: unknown): value is TaskJournalPriority | 'all' {
  return value === 'all' || PRIORITY_OPTIONS.some((option) => option.value === value);
}

function isPeriodMode(value: unknown): value is PeriodMode {
  return value === 'day' || value === 'week' || value === 'month' || value === 'all';
}

function isTaskJournalSortKey(value: unknown): value is TaskJournalSortKey {
  return value === 'counterparty'
    || value === 'title'
    || value === 'assignee'
    || value === 'author'
    || value === 'dueAt'
    || value === 'type'
    || value === 'result'
    || value === 'status';
}

function isDateFilterValue(value: unknown): value is string {
  return typeof value === 'string' && (value === '' || /^\d{4}-\d{2}-\d{2}$/.test(value));
}

function getNextDateFilterKey(value: string) {
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day + 1));
  return [
    date.getUTCFullYear(),
    String(date.getUTCMonth() + 1).padStart(2, '0'),
    String(date.getUTCDate()).padStart(2, '0'),
  ].join('-');
}

function parseDateFilterValue(value: string, boundary: 'start' | 'end') {
  if (!isDateFilterValue(value) || !value) {
    return null;
  }

  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
    return null;
  }

  if (boundary === 'start') {
    return new Date(fromTimeZoneDateTimeInput(`${value}T00:00`, activeTaskJournalTimeZoneId));
  }

  return new Date(new Date(fromTimeZoneDateTimeInput(`${getNextDateFilterKey(value)}T00:00`, activeTaskJournalTimeZoneId)).getTime() - 1);
}

function getTaskDateRangeBounds(dateFrom: string, dateTo: string) {
  const from = parseDateFilterValue(dateFrom, 'start');
  const to = parseDateFilterValue(dateTo, 'end');

  if (from && to && from.getTime() > to.getTime()) {
    return {
      from: parseDateFilterValue(dateTo, 'start'),
      to: parseDateFilterValue(dateFrom, 'end'),
    };
  }

  return { from, to };
}

function normalizeTaskJournalFilterPreferences(
  parsed: Partial<TaskJournalFilterPreferences> | null | undefined,
  defaults: TaskJournalFilterPreferences,
): TaskJournalFilterPreferences {
  return {
    viewMode: isViewMode(parsed?.viewMode) ? parsed.viewMode : defaults.viewMode,
    calendarColorMode: isTaskCalendarColorMode(parsed?.calendarColorMode) ? parsed.calendarColorMode : defaults.calendarColorMode,
    calendarConflictPolicy: isTaskCalendarConflictPolicy(parsed?.calendarConflictPolicy) ? parsed.calendarConflictPolicy : defaults.calendarConflictPolicy,
    calendarLoadWarningMode: isTaskCalendarLoadWarningMode(parsed?.calendarLoadWarningMode) ? parsed.calendarLoadWarningMode : defaults.calendarLoadWarningMode,
    icsReminderPreset: isIcsReminderPreset(parsed?.icsReminderPreset) ? parsed.icsReminderPreset : defaults.icsReminderPreset,
    quickFilter: isQuickFilter(parsed?.quickFilter) ? parsed.quickFilter : defaults.quickFilter,
    metricFilter: isMetricFilter(parsed?.metricFilter) ? parsed.metricFilter : defaults.metricFilter,
    search: typeof parsed?.search === 'string' ? parsed.search : defaults.search,
    dateFrom: isDateFilterValue(parsed?.dateFrom) ? parsed.dateFrom : defaults.dateFrom,
    dateTo: isDateFilterValue(parsed?.dateTo) ? parsed.dateTo : defaults.dateTo,
    statusFilter: isStatusFilter(parsed?.statusFilter) ? parsed.statusFilter : defaults.statusFilter,
    counterpartyFilter: typeof parsed?.counterpartyFilter === 'string'
      && (parsed.counterpartyFilter === 'all' || /^(client|legal):\d+$/.test(parsed.counterpartyFilter))
      ? parsed.counterpartyFilter
      : defaults.counterpartyFilter,
    caseFilter: typeof parsed?.caseFilter === 'string' && (parsed.caseFilter === 'all' || /^\d+$/.test(parsed.caseFilter))
      ? parsed.caseFilter
      : defaults.caseFilter,
    assigneeFilter: typeof parsed?.assigneeFilter === 'string' && parsed.assigneeFilter ? parsed.assigneeFilter : defaults.assigneeFilter,
    typeFilter: isTypeFilter(parsed?.typeFilter) ? parsed.typeFilter : defaults.typeFilter,
    priorityFilter: isPriorityFilter(parsed?.priorityFilter) ? parsed.priorityFilter : defaults.priorityFilter,
    periodMode: isPeriodMode(parsed?.periodMode) ? parsed.periodMode : defaults.periodMode,
    sortBy: isTaskJournalSortKey(parsed?.sortBy) ? parsed.sortBy : defaults.sortBy,
    sortDir: parsed?.sortDir === 'desc' ? 'desc' : defaults.sortDir,
  };
}

function readTaskJournalFilterPreferences(userId: number | null, mode: TaskJournalMode = 'journal', viewMode: ViewMode = 'list'): TaskJournalFilterPreferences {
  const defaults = createDefaultFilterPreferences(userId, mode, viewMode);
  if (typeof window === 'undefined') {
    return defaults;
  }

  try {
    const raw = window.localStorage.getItem(getTaskJournalFilterStorageKey(userId, mode));
    if (!raw) {
      return defaults;
    }

    const parsed = JSON.parse(raw) as Partial<TaskJournalFilterPreferences>;
    return normalizeTaskJournalFilterPreferences(parsed, defaults);
  } catch {
    return defaults;
  }
}

function writeTaskJournalFilterPreferences(userId: number | null, preferences: TaskJournalFilterPreferences, mode: TaskJournalMode = 'journal') {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(getTaskJournalFilterStorageKey(userId, mode), JSON.stringify(preferences));
  } catch {
    // Если браузер запретил localStorage, журнал продолжит работать без сохранения фильтров.
  }
}

function readTaskJournalSavedViews(userId: number | null, mode: TaskJournalMode = 'journal', viewMode: ViewMode = 'list'): TaskJournalSavedView[] {
  if (typeof window === 'undefined') {
    return [];
  }

  try {
    const raw = window.localStorage.getItem(getTaskJournalSavedViewsStorageKey(userId, mode));
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) {
      return [];
    }

    const defaults = createDefaultFilterPreferences(userId, mode, viewMode);
    return parsed
      .filter((item): item is TaskJournalSavedView => (
        Boolean(item)
        && typeof item === 'object'
        && typeof (item as TaskJournalSavedView).id === 'string'
        && typeof (item as TaskJournalSavedView).name === 'string'
        && Boolean((item as TaskJournalSavedView).filters)
      ))
      .map((item) => ({
        id: item.id,
        name: item.name,
        filters: normalizeTaskJournalFilterPreferences(item.filters, defaults),
        updatedAt: typeof item.updatedAt === 'string' ? item.updatedAt : '',
      }))
      .slice(0, 20);
  } catch {
    return [];
  }
}

function writeTaskJournalSavedViews(userId: number | null, mode: TaskJournalMode, views: TaskJournalSavedView[]) {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(getTaskJournalSavedViewsStorageKey(userId, mode), JSON.stringify(views.slice(0, 20)));
  } catch {
    // Если браузер запретил localStorage, журнал продолжит работать без сохраненных представлений.
  }
}

const INTERNAL_TASK_OBJECT_NAME = 'Внутренняя задача';
const TASK_COMPLETION_RESULT_HINT = 'Опишите подробно, что было сделано и какой результат был достигнут.';
const TASK_COMPLETION_RESULT_REQUIRED_MESSAGE = 'Добавьте результат по задаче перед завершением.';
const MAX_TASK_COMPLETION_FILE_BYTES = 25 * 1024 * 1024;
const TASK_APPROVAL_RESULT_REQUIRED_MESSAGE = 'Добавьте результат по задаче перед отправкой на согласование.';
let activeTaskJournalTimeZoneId = normalizeUserTimeZoneId();

function setActiveTaskJournalTimeZoneId(value?: string | null) {
  activeTaskJournalTimeZoneId = normalizeUserTimeZoneId(value);
}

const emptyForm = (
  assigneeName: string,
  assigneeUserId?: number | null,
  notificationDefaults: TaskNotificationDefaults = DEFAULT_TASK_NOTIFICATION_DEFAULTS,
): TaskForm => {
  const dueAt = new Date();
  dueAt.setHours(dueAt.getHours() + 2);
  const defaults = {
    reminders: [...notificationDefaults.reminders],
    channels: [...notificationDefaults.channels],
  };

  return {
    isInternal: false,
    clientId: null,
    legalEntityId: null,
    clientCaseId: null,
    objectName: '',
    title: '',
    description: '',
    assigneeUserId,
    assigneeName,
    authorUserId: assigneeUserId ?? null,
    dueAt: toLocalInputValue(dueAt.toISOString()),
    approvalDueAt: null,
    plannedDurationMinutes: 60,
    contractorPaymentEnabled: false,
    contractorPaymentAmount: null,
    contractorPaymentDueDate: null,
    contractorPaymentOnResult: false,
    contractorPaymentResultDelayDays: null,
    type: 'task',
    priority: 'normal',
    status: 'new',
    isUnderControl: false,
    result: '',
    reminders: defaults.reminders,
    channels: defaults.channels,
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
  return toTimeZoneDateTimeInput(value, activeTaskJournalTimeZoneId);
}

function fromLocalInputValue(value: string) {
  return value ? fromTimeZoneDateTimeInput(value, activeTaskJournalTimeZoneId) : new Date().toISOString();
}

function getDefaultRevisionDueAtInput(task: TaskJournalItem) {
  const currentDueAt = new Date(task.dueAt);
  const now = new Date();
  if (!Number.isNaN(currentDueAt.getTime()) && currentDueAt.getTime() > now.getTime()) {
    return toLocalInputValue(currentDueAt.toISOString());
  }

  const nextDueAt = new Date(now);
  nextDueAt.setDate(nextDueAt.getDate() + 1);
  nextDueAt.setMinutes(0, 0, 0);
  return toLocalInputValue(nextDueAt.toISOString());
}

function normalizeApprovalDueDays(value: number | null | undefined) {
  const days = Number.isFinite(value ?? NaN) ? Number(value) : 5;
  return Math.min(365, Math.max(1, Math.round(days)));
}

function getDefaultApprovalDueAtInput(settings: TaskJournalSettingsDto) {
  const approvalDueAt = new Date();
  approvalDueAt.setDate(approvalDueAt.getDate() + normalizeApprovalDueDays(settings.approvalDueDays));
  return toLocalInputValue(approvalDueAt.toISOString());
}

function isFutureDateTime(value: string) {
  return new Date(value).getTime() > Date.now();
}

function fromLocalDateInputValue(value: string) {
  return fromTimeZoneDateTimeInput(`${value}T00:00`, activeTaskJournalTimeZoneId);
}

function formatDateTime(value: string) {
  return formatDateTimeInTimeZone(value, activeTaskJournalTimeZoneId);
}

function getDeadlineChangeRequestStatusLabel(status: string) {
  switch (status) {
    case 'pending':
      return 'Ожидает решения';
    case 'approved':
      return 'Согласовано';
    case 'rejected':
      return 'Отклонено';
    case 'cancelled':
      return 'Отменено';
    case 'superseded':
      return 'Заменено';
    default:
      return status || 'Неизвестно';
  }
}

function getDeadlineChangeRequestStatusClass(status: string) {
  switch (status) {
    case 'pending':
      return 'border-amber-200 bg-amber-50 text-amber-800';
    case 'approved':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'rejected':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-600';
  }
}

function getDeadlineChangeRequestActionLabel(request: TaskJournalDeadlineChangeRequest) {
  if (request.status === 'pending') {
    return request.reviewerName
      ? `Ожидает решения: ${request.reviewerName}`
      : 'Ожидает решения постановщика';
  }

  if (request.decidedByName) {
    return `Обработал: ${request.decidedByName}`;
  }

  return 'Решение зафиксировано';
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

async function uploadTaskCompletionFiles(taskId: number, files: readonly File[]) {
  for (const file of files) {
    await apiService.uploadTaskJournalFile(taskId, file);
  }
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
  return fromTimeZoneDateTimeInput(`${toLocalDateKey(date)}T09:00`, activeTaskJournalTimeZoneId);
}

function moveDueAtToDate(currentDueAt: string, targetDate: Date) {
  const current = getZonedParts(currentDueAt, activeTaskJournalTimeZoneId);
  const time = current
    ? `${String(current.hour).padStart(2, '0')}:${String(current.minute).padStart(2, '0')}`
    : '09:00';
  return fromTimeZoneDateTimeInput(`${toLocalDateKey(targetDate)}T${time}`, activeTaskJournalTimeZoneId);
}

function taskToPayload(task: TaskJournalItem, overrides: Partial<TaskJournalUpsertPayload> = {}): TaskJournalUpsertPayload {
  return {
    isInternal: task.isInternal ?? (!task.clientId && !task.legalEntityId),
    clientId: task.clientId ?? null,
    legalEntityId: task.legalEntityId ?? null,
    clientCaseId: task.clientCaseId ?? null,
    objectName: task.objectName,
    title: task.title,
    description: task.description,
    assigneeUserId: task.assigneeUserId ?? null,
    assigneeName: task.assigneeName,
    authorUserId: task.authorUserId ?? null,
    dueAt: task.dueAt,
    approvalDueAt: task.approvalDueAt ?? null,
    plannedDurationMinutes: normalizeTaskDuration(task.plannedDurationMinutes),
    type: task.type,
    priority: task.priority,
    status: task.status,
    isUnderControl: Boolean(task.isUnderControl),
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

async function loadAllTaskJournalItems(
  contextFilter?: TaskJournalContextFilter,
  underControlOnly = false,
  calendarView = false,
  sortBy: TaskJournalSortKey = 'dueAt',
  sortDir: 'asc' | 'desc' = 'asc',
  includeDeadlineChangeRequests = false,
) {
  const pageSize = 200;
  let page = 1;
  let total = Number.POSITIVE_INFINITY;
  const items: TaskJournalItem[] = [];

  while (items.length < total) {
    const response = await apiService.getTaskJournalItems({
      page,
      pageSize,
      underControl: underControlOnly || undefined,
      clientId: contextFilter?.clientId ?? undefined,
      legalEntityId: contextFilter?.legalEntityId ?? undefined,
      calendarView,
      includeDeadlineChangeRequests,
      sortBy,
      sortDir,
    });
    items.push(...response.items);
    total = response.total;

    if (response.items.length === 0) {
      break;
    }

    page += 1;
  }

  return items;
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

function dateKeyToCalendarDate(value: string) {
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(year, month - 1, day, 12, 0, 0, 0);
  return Number.isNaN(date.getTime()) ? new Date() : date;
}

function toLocalDateKey(date: Date) {
  return toDateKey(date);
}

function getTaskDateKey(value: string | Date, timeZoneId = activeTaskJournalTimeZoneId) {
  return toTimeZoneDateKey(value, timeZoneId);
}

function getCurrentCalendarDate(now = new Date()) {
  const key = getTaskDateKey(now);
  return key ? dateKeyToCalendarDate(key) : new Date(now);
}

function getCalendarDateForTaskValue(value: string | Date) {
  const key = getTaskDateKey(value);
  return key ? dateKeyToCalendarDate(key) : (value instanceof Date ? new Date(value) : new Date(value));
}

function isSameTaskDay(value: string | Date, day: Date) {
  return getTaskDateKey(value) === toLocalDateKey(day);
}

function getCurrentTaskDayKey(now = new Date()) {
  return getTaskDateKey(now);
}

function getTaskTimeLabel(value: string | Date, timeZoneId = activeTaskJournalTimeZoneId) {
  const parts = getZonedParts(value, timeZoneId);
  if (!parts) {
    return '';
  }

  return `${String(parts.hour).padStart(2, '0')}:${String(parts.minute).padStart(2, '0')}`;
}

function getTaskDateLabel(value: string | Date, timeZoneId = activeTaskJournalTimeZoneId) {
  const parts = getZonedParts(value, timeZoneId);
  if (!parts) {
    return '';
  }

  return new Intl.DateTimeFormat('ru-RU', {
    timeZone: 'UTC',
    day: '2-digit',
    month: 'long',
    year: 'numeric',
  }).format(new Date(Date.UTC(parts.year, parts.month - 1, parts.day)));
}

function formatDateOnly(value: Date) {
  return value.toLocaleDateString('ru-RU', {
    day: '2-digit',
    month: 'long',
    year: 'numeric',
  });
}

function formatPeriodTitle(date: Date, mode: PeriodMode) {
  if (mode === 'all') {
    return 'Весь период';
  }

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

function getTaskPeriodBounds(date: Date, mode: PeriodMode) {
  if (mode === 'all') {
    return {
      periodStart: new Date(0),
      periodEnd: new Date(8640000000000000),
    };
  }

  const periodStart = mode === 'day'
    ? startOfDay(date)
    : mode === 'week'
      ? startOfWeek(date)
      : startOfMonth(date);
  const periodEndDay = mode === 'day'
    ? date
    : mode === 'week'
      ? addDays(periodStart, 6)
      : new Date(date.getFullYear(), date.getMonth() + 1, 0);
  const periodStartValue = parseDateFilterValue(toLocalDateKey(periodStart), 'start') ?? startOfDay(periodStart);
  const periodEndValue = parseDateFilterValue(toLocalDateKey(periodEndDay), 'end') ?? endOfDay(periodEndDay);

  return { periodStart: periodStartValue, periodEnd: periodEndValue };
}

function getCaseCalendarRange(anchorDate: Date, mode: PeriodMode) {
  if (mode === 'all') {
    const from = startOfMonth(anchorDate);
    return { from, to: endOfDay(addDays(addMonths(from, 1), -1)) };
  }

  if (mode === 'day') {
    return { from: startOfDay(anchorDate), to: endOfDay(anchorDate) };
  }

  if (mode === 'week') {
    const from = startOfWeek(anchorDate);
    return { from, to: endOfDay(addDays(from, 6)) };
  }

  const from = startOfMonth(anchorDate);
  return { from, to: endOfDay(addDays(addMonths(from, 1), -1)) };
}

function getTypeMeta(type: TaskJournalType) {
  return TYPE_OPTIONS.find((item) => item.value === type) ?? TYPE_OPTIONS[0];
}

function getStatusLabel(status: TaskJournalStatus) {
  return STATUS_OPTIONS.find((item) => item.value === status)?.label ?? status;
}

function isTaskOverdue(task: Pick<TaskJournalItem, 'status' | 'dueAt'>, nowDate = new Date()) {
  if (!isTaskJournalActive(task.status)) {
    return false;
  }

  const dueTime = new Date(task.dueAt).getTime();
  return Number.isFinite(dueTime) && dueTime < nowDate.getTime();
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

function getTaskAuthorName(task: Pick<TaskJournalItem, 'authorName'>) {
  return task.authorName?.trim() || 'Не указан';
}

function buildTaskTooltipLines(task: TaskJournalItem) {
  const due = new Date(task.dueAt);
  const duration = normalizeTaskDuration(task.plannedDurationMinutes);
  const end = new Date(due.getTime() + duration * 60 * 1000);
  const dueDateLabel = getTaskDateLabel(due);
  const dueTimeLabel = getTaskTimeLabel(due);
  const endTimeLabel = getTaskTimeLabel(end);
  const caseTitle = getTaskCaseTitle(task);
  const assignees = getTaskAssigneeNames(task).join(', ') || task.assigneeName || 'Не назначен';
  const lines = [
    task.title,
    `Контрагент: ${getTaskClientName(task) || 'не указан'}`,
  ];

  if (caseTitle) {
    lines.push(`Дело: ${caseTitle}`);
  }

  lines.push(
    `Время: ${dueDateLabel || formatDateOnly(due)} ${dueTimeLabel || formatTimeOnly(due)}-${endTimeLabel || formatTimeOnly(end)}`,
    `Исполнитель: ${assignees}`,
    `Поручил: ${getTaskAuthorName(task)}`,
    `Статус: ${getStatusLabel(task.status)}`,
    `Приоритет: ${getPriorityLabel(task.priority)}`,
    `Длительность: ${formatDuration(duration)}`,
  );

  return lines;
}

function isClosedClientCase(clientCase: ClientCase) {
  const status = String(clientCase.status ?? '').trim().toLowerCase();
  const normalized = status.replace(/ё/g, 'е');
  return normalized === '2'
    || normalized.includes('closed')
    || normalized.includes('lost')
    || normalized.includes('complete')
    || normalized.includes('archiv')
    || normalized.includes('cancel')
    || normalized.includes('done')
    || normalized.includes('закры')
    || normalized.includes('заверш')
    || normalized.includes('проигр')
    || normalized.includes('оконч')
    || normalized.includes('архив')
    || normalized.includes('отмен');
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

function getOrderedApprovers(task: TaskJournalItem) {
  return getTaskParticipants(task, 'approver')
    .slice()
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
}

function getCurrentPendingApprover(task: TaskJournalItem) {
  return getOrderedApprovers(task).find((participant) => participant.approvalStatus !== 'approved') ?? null;
}

function getApprovalSummary(task: TaskJournalItem) {
  const approvers = getOrderedApprovers(task);
  if (approvers.length === 0) {
    return null;
  }

  const allNames = approvers.map(getParticipantDisplayName).filter(Boolean).join(', ');
  const currentApprover = getCurrentPendingApprover(task);
  if (!currentApprover) {
    return `Согласовано: ${allNames}`;
  }

  const currentName = getParticipantDisplayName(currentApprover);
  return `Согласует: ${currentName || allNames}`;
}

function getPendingDeadlineChangeRequests(task: TaskJournalItem) {
  return (task.activeDeadlineChangeRequests ?? [])
    .filter((request) => request.status === 'pending');
}

function hasPendingDeadlineChangeRequestForApprovals(task: TaskJournalItem, userId: number | null, isAdminUser: boolean) {
  const requests = getPendingDeadlineChangeRequests(task);
  if (requests.length === 0) {
    return false;
  }

  if (isAdminUser) {
    return true;
  }

  return Boolean(userId && requests.some((request) => request.reviewerUserId === userId));
}

function getReviewableDeadlineChangeRequest(task: TaskJournalItem, userId: number | null, isAdminUser: boolean) {
  return getPendingDeadlineChangeRequests(task)
    .find((request) => isAdminUser || (userId !== null && request.reviewerUserId === userId)) ?? null;
}

function getReviewableApprovalParticipant(task: TaskJournalItem, userId: number | null) {
  if (task.status !== 'onApproval' && task.status !== 'overdueApproval') {
    return null;
  }

  const participant = getCurrentPendingApprover(task);
  return userId !== null && participant?.userId === userId ? participant : null;
}

function isTaskAwaitingApproval(task: Pick<TaskJournalItem, 'status'>) {
  return task.status === 'onApproval' || task.status === 'overdueApproval';
}

function isTaskInApprovalsScope(task: TaskJournalItem, userId: number | null, isAdminUser: boolean) {
  if (hasPendingDeadlineChangeRequestForApprovals(task, userId, isAdminUser)) {
    return true;
  }

  if (task.status !== 'onApproval' && task.status !== 'overdueApproval') {
    return false;
  }

  if (isAdminUser) {
    return true;
  }

  const currentApprover = getCurrentPendingApprover(task);
  return Boolean(userId && currentApprover?.userId === userId);
}

function isTaskExecutorForUser(task: TaskJournalItem, userId: number | null) {
  if (!userId) return false;
  return task.assigneeUserId === userId ||
    getTaskParticipants(task, 'assignee').some((participant) => participant.userId === userId);
}

function isTaskOwnedByUser(task: TaskJournalItem, userId: number | null) {
  return Boolean(userId && (task.authorUserId === userId || isTaskExecutorForUser(task, userId)));
}

function isTaskAuthoredByUser(task: TaskJournalItem, userId: number | null, userName: string) {
  if (!userId) return false;
  return task.authorUserId === userId || (!task.authorUserId && Boolean(userName) && task.authorName === userName);
}

function normalizeTaskCalendarViewScope(viewScope?: MenuSectionPermissions['viewScope']) {
  return viewScope === 'department' || viewScope === 'all' || viewScope === 'specific' ? viewScope : 'own';
}

function normalizeDepartmentName(value?: string | null) {
  return value?.trim().toLowerCase() || '';
}

function isAssigneeInDepartment(assignee: TaskJournalAssignee, departmentId?: number | null, department?: string | null) {
  if (departmentId && assignee.departmentId === departmentId) {
    return true;
  }

  const currentDepartment = normalizeDepartmentName(department);
  return Boolean(currentDepartment && normalizeDepartmentName(assignee.department) === currentDepartment);
}

function taskTouchesCalendarUsers(task: TaskJournalItem, visibleUserIds: Set<number>, currentUserId: number | null, currentUserName: string) {
  if (task.assigneeUserId && visibleUserIds.has(task.assigneeUserId)) return true;
  if (task.authorUserId && visibleUserIds.has(task.authorUserId)) return true;
  if ((task.participants ?? []).some((participant) => participant.userId && visibleUserIds.has(participant.userId))) return true;

  return Boolean(currentUserId && visibleUserIds.has(currentUserId) && isTaskAuthoredByUser(task, currentUserId, currentUserName));
}

function matchesQuickFilter(task: TaskJournalItem, quickFilter: QuickFilter, userId: number | null, userName: string) {
  if (quickFilter === 'assignedToMe') {
    return isTaskExecutorForUser(task, userId);
  }

  if (quickFilter === 'authoredByMe') {
    return isTaskAuthoredByUser(task, userId, userName);
  }

  return true;
}

function matchesMetricFilter(task: TaskJournalItem, metricFilter: MetricFilter, nowDate: Date) {
  const active = isTaskJournalActive(task.status);

  switch (metricFilter) {
    case 'active':
      return active;
    case 'overdue':
      return isTaskOverdue(task, nowDate);
    case 'today':
      return active && isSameTaskDay(task.dueAt, nowDate);
    case 'highPriority':
      return active && (task.priority === 'high' || task.priority === 'urgent');
    default:
      return true;
  }
}

function matchesStatusFilter(task: TaskJournalItem, statusFilter: TaskStatusFilter) {
  if (statusFilter === 'all') {
    return true;
  }

  if (statusFilter === 'active') {
    return isTaskJournalActive(task.status);
  }

  if (statusFilter === 'overdue') {
    return isTaskOverdue(task);
  }

  return task.status === statusFilter;
}

function isTaskReadOnlyForCurrentUser(
  task: TaskJournalItem,
  currentUserId: number | null,
  isAdminUser: boolean,
  settings: TaskJournalSettingsDto,
  taskPermissions: TaskSectionPermissions,
) {
  if (isAdminUser) {
    return false;
  }

  if (!currentUserId) {
    return true;
  }

  if (!isTaskOwnedByUser(task, currentUserId)) {
    return !taskPermissions.canEditOthers;
  }

  if (!taskPermissions.canEdit) {
    return true;
  }

  return settings.restrictExecutorEditing
    && isTaskExecutorForUser(task, currentUserId)
    && task.authorUserId !== currentUserId;
}

function canCompleteTaskForCurrentUser(
  task: TaskJournalItem,
  currentUserId: number | null,
  isAdminUser: boolean,
  settings: TaskJournalSettingsDto,
  taskPermissions: TaskSectionPermissions,
) {
  if (!isTaskJournalActive(task.status)) {
    return false;
  }

  if (!currentUserId) {
    return false;
  }

  if (!isAdminUser && !isTaskOwnedByUser(task, currentUserId)) {
    return taskPermissions.canCompleteOthers;
  }

  if (!isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, settings, taskPermissions)) {
    return true;
  }

  if (task.authorUserId === currentUserId) {
    return true;
  }

  return isTaskExecutorForUser(task, currentUserId)
    && settings.executorCompletionAllowedUserIds.includes(currentUserId);
}

function canDeleteTaskForCurrentUser(
  task: TaskJournalItem,
  currentUserId: number | null,
  isAdminUser: boolean,
  settings: TaskJournalSettingsDto,
  taskPermissions: TaskSectionPermissions,
) {
  if (isAdminUser) {
    return true;
  }

  if (!currentUserId) {
    return false;
  }

  if (!isTaskOwnedByUser(task, currentUserId)) {
    return taskPermissions.canDeleteOthers;
  }

  if (!taskPermissions.canDelete) {
    return false;
  }

  return !(settings.restrictExecutorEditing
    && isTaskExecutorForUser(task, currentUserId)
    && task.authorUserId !== currentUserId);
}

function canPostTaskCommentForCurrentUser(
  task: TaskJournalItem | null | undefined,
  currentUserId: number | null,
  canSwitchToEdit: boolean,
) {
  if (!task) {
    return false;
  }

  if (canSwitchToEdit) {
    return true;
  }

  if (!currentUserId) {
    return false;
  }

  if (task.authorUserId === currentUserId || task.assigneeUserId === currentUserId) {
    return true;
  }

  return (task.participants ?? []).some((participant) =>
    participant.userId === currentUserId &&
    participant.role.toLowerCase() !== 'observer',
  );
}

function TaskPriorityBadge({
  priority,
  compact = false,
}: {
  priority: TaskJournalPriority;
  compact?: boolean;
}) {
  return (
    <TaskStickerBadge
      stickerKey={taskPriorityStickerKey(priority)}
      compact={compact}
    />
  );
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
    case 'approved':
      return 'bg-emerald-50 text-emerald-700';
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

function taskCalendarPriorityTone(priority: TaskJournalPriority) {
  switch (priority) {
    case 'urgent':
      return 'border-rose-200 bg-rose-50 text-rose-900';
    case 'high':
      return 'border-amber-200 bg-amber-50 text-amber-900';
    case 'low':
      return 'border-slate-200 bg-slate-50 text-slate-700';
    default:
      return 'border-sky-200 bg-sky-50 text-sky-900';
  }
}

function taskCalendarStatusTone(status: TaskJournalStatus) {
  switch (status) {
    case 'done':
    case 'approved':
      return 'border-emerald-200 bg-emerald-50 text-emerald-900';
    case 'cancelled':
      return 'border-slate-200 bg-slate-100 text-slate-600';
    case 'waiting':
      return 'border-amber-200 bg-amber-50 text-amber-900';
    case 'onApproval':
      return 'border-violet-200 bg-violet-50 text-violet-900';
    case 'revision':
      return 'border-orange-200 bg-orange-50 text-orange-900';
    case 'overdueApproval':
      return 'border-rose-200 bg-rose-50 text-rose-900';
    case 'inProgress':
      return 'border-blue-200 bg-blue-50 text-blue-900';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-900';
  }
}

function getTaskCalendarCardTone(
  task: TaskJournalItem,
  colorMode: TaskCalendarColorMode,
  completedTaskCalendarAction: 'color' | 'hide' = 'color',
) {
  if (completedTaskCalendarAction === 'color' && task.status === 'done') {
    return 'border-slate-300 bg-slate-200 text-slate-900 ring-1 ring-slate-300';
  }

  if (colorMode === 'priority') {
    return taskCalendarPriorityTone(task.priority);
  }

  if (colorMode === 'status') {
    return taskCalendarStatusTone(task.status);
  }

  return 'border-slate-100 bg-slate-50 text-slate-900';
}

function getTaskCalendarLoadWarningThresholds(mode: TaskCalendarLoadWarningMode) {
  switch (mode) {
    case 'strict':
      return {
        warningTaskCount: 3,
        warningDurationMinutes: 3 * 60,
        criticalTaskCount: 5,
        criticalDurationMinutes: 5 * 60,
        assigneeWarningTaskCount: 3,
        assigneeWarningDurationMinutes: 4 * 60,
      };
    case 'relaxed':
      return {
        warningTaskCount: 8,
        warningDurationMinutes: 7 * 60,
        criticalTaskCount: 10,
        criticalDurationMinutes: 9 * 60,
        assigneeWarningTaskCount: 8,
        assigneeWarningDurationMinutes: 8 * 60,
      };
    default:
      return {
        warningTaskCount: 5,
        warningDurationMinutes: 5 * 60,
        criticalTaskCount: 8,
        criticalDurationMinutes: 8 * 60,
        assigneeWarningTaskCount: 5,
        assigneeWarningDurationMinutes: 6 * 60,
      };
  }
}

function formatTimeOnly(value: Date | string, timeZoneId = activeTaskJournalTimeZoneId) {
  const label = getTaskTimeLabel(value, timeZoneId);
  if (label) {
    return label;
  }

  const date = typeof value === 'string' ? new Date(value) : value;
  return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

function getTimelineHourLabels() {
  return Array.from(
    { length: TIMELINE_END_HOUR - TIMELINE_START_HOUR + 1 },
    (_, index) => TIMELINE_START_HOUR + index,
  );
}

function getTaskStartMinute(task: TaskJournalItem) {
  const parts = getZonedParts(task.dueAt, activeTaskJournalTimeZoneId);
  if (!parts) {
    return TIMELINE_START_MINUTE;
  }

  return parts.hour * 60 + parts.minute;
}

function getTaskEndMinute(task: TaskJournalItem) {
  return getTaskStartMinute(task) + normalizeTaskDuration(task.plannedDurationMinutes);
}

function getTaskAssigneeNames(task: TaskJournalItem) {
  const participantNames = getTaskParticipants(task, 'assignee').map(getParticipantDisplayName).filter(Boolean);
  return unique(participantNames.length > 0 ? participantNames : [task.assigneeName || 'Без исполнителя']);
}

function getTaskAssigneeConflictKeys(task: TaskJournalItem) {
  const keys = new Set<string>();
  if (task.assigneeUserId) {
    keys.add(`user:${task.assigneeUserId}`);
  }

  getTaskParticipants(task, 'assignee').forEach((participant) => {
    if (participant.userId) {
      keys.add(`user:${participant.userId}`);
      return;
    }

    const name = getParticipantDisplayName(participant).trim().toLowerCase();
    if (name) {
      keys.add(`name:${name}`);
    }
  });

  if (keys.size === 0 && task.assigneeName?.trim()) {
    keys.add(`name:${task.assigneeName.trim().toLowerCase()}`);
  }

  return Array.from(keys);
}

function buildTaskConflictTaskIds(tasks: TaskJournalItem[]) {
  const conflictIds = new Set<number>();
  const byAssignee = new Map<string, TaskJournalItem[]>();

  tasks
    .filter((task) => isTaskJournalActive(task.status))
    .forEach((task) => {
      getTaskAssigneeConflictKeys(task).forEach((key) => {
        const items = byAssignee.get(key) ?? [];
        items.push(task);
        byAssignee.set(key, items);
      });
    });

  byAssignee.forEach((items) => {
    const sorted = items.slice().sort((a, b) => getTaskStartMinute(a) - getTaskStartMinute(b) || a.id - b.id);
    for (let index = 0; index < sorted.length; index += 1) {
      const current = sorted[index];
      for (let nextIndex = index + 1; nextIndex < sorted.length; nextIndex += 1) {
        const candidate = sorted[nextIndex];
        if (getTaskStartMinute(candidate) >= getTaskEndMinute(current)) {
          break;
        }

        if (intervalsOverlap(
          getTaskStartMinute(current),
          getTaskEndMinute(current),
          getTaskStartMinute(candidate),
          getTaskEndMinute(candidate),
        )) {
          conflictIds.add(current.id);
          conflictIds.add(candidate.id);
        }
      }
    }
  });

  return conflictIds;
}

function findTaskScheduleConflict(task: TaskJournalItem, nextDueAt: Date, tasks: TaskJournalItem[]) {
  if (!isTaskJournalActive(task.status) || Number.isNaN(nextDueAt.getTime())) {
    return null;
  }

  const taskKeys = new Set(getTaskAssigneeConflictKeys(task));
  if (taskKeys.size === 0) {
    return null;
  }

  const nextStart = nextDueAt.getTime();
  const nextEnd = nextStart + normalizeTaskDuration(task.plannedDurationMinutes) * 60 * 1000;

  return tasks.find((candidate) => {
    if (candidate.id === task.id || !isTaskJournalActive(candidate.status)) {
      return false;
    }

    const candidateDueAt = new Date(candidate.dueAt);
    if (!isSameTaskDay(candidate.dueAt, nextDueAt)) {
      return false;
    }

    const hasSharedAssignee = getTaskAssigneeConflictKeys(candidate).some((key) => taskKeys.has(key));
    if (!hasSharedAssignee) {
      return false;
    }

    const candidateStart = candidateDueAt.getTime();
    const candidateEnd = candidateStart + normalizeTaskDuration(candidate.plannedDurationMinutes) * 60 * 1000;
    return intervalsOverlap(nextStart, nextEnd, candidateStart, candidateEnd);
  }) ?? null;
}

function getTimelineTaskTone(
  task: TaskJournalItem,
  currentUserId: number | null,
  assigneeFilter: string,
  colorMode: TaskCalendarColorMode,
  completedTaskCalendarAction: 'color' | 'hide' = 'color',
) {
  const isOverdue = isTaskOverdue(task);
  const isSelectedAssignee =
    assigneeFilter !== 'all' &&
    (String(task.assigneeUserId ?? '') === assigneeFilter || hasAssigneeParticipant(task, assigneeFilter));
  const isOwnTask = isSelectedAssignee || isTaskExecutorForUser(task, currentUserId);

  if (completedTaskCalendarAction === 'color' && task.status === 'done') {
    return { label: 'Выполнено', className: 'border-slate-700 bg-slate-600 text-white', dotClassName: 'bg-slate-600' };
  }

  if (colorMode === 'priority') {
    switch (task.priority) {
      case 'urgent':
        return { label: 'Срочный приоритет', className: 'border-rose-600 bg-rose-600 text-white', dotClassName: 'bg-rose-600' };
      case 'high':
        return { label: 'Высокий приоритет', className: 'border-orange-600 bg-orange-500 text-white', dotClassName: 'bg-orange-500' };
      case 'low':
        return { label: 'Низкий приоритет', className: 'border-slate-500 bg-slate-500 text-white', dotClassName: 'bg-slate-500' };
      default:
        return { label: 'Обычный приоритет', className: 'border-sky-600 bg-sky-600 text-white', dotClassName: 'bg-sky-600' };
    }
  }

  if (colorMode === 'status') {
    switch (task.status) {
      case 'done':
      case 'approved':
        return { label: 'Готово/согласовано', className: 'border-emerald-700 bg-emerald-600 text-white', dotClassName: 'bg-emerald-600' };
      case 'cancelled':
        return { label: 'Отменено', className: 'border-slate-500 bg-slate-500 text-white', dotClassName: 'bg-slate-500' };
      case 'waiting':
        return { label: 'Ожидание', className: 'border-amber-600 bg-amber-500 text-white', dotClassName: 'bg-amber-500' };
      case 'onApproval':
      case 'revision':
        return { label: 'Согласование/доработка', className: 'border-violet-600 bg-violet-600 text-white', dotClassName: 'bg-violet-600' };
      case 'overdueApproval':
        return { label: 'Просрочено на согласовании', className: 'border-rose-600 bg-rose-600 text-white', dotClassName: 'bg-rose-600' };
      case 'inProgress':
        return { label: 'В работе', className: 'border-blue-700 bg-blue-600 text-white', dotClassName: 'bg-blue-600' };
      default:
        return { label: 'Новые', className: 'border-sky-600 bg-sky-600 text-white', dotClassName: 'bg-sky-600' };
    }
  }

  if (task.status === 'done' || task.status === 'cancelled') {
    return {
      label: 'Выполненные/отмененные',
      className: 'border-slate-500 bg-slate-500 text-white',
      dotClassName: 'bg-slate-500',
    };
  }

  if (isOverdue || task.status === 'overdueApproval') {
    return {
      label: 'Просроченные',
      className: 'border-rose-600 bg-rose-600 text-white',
      dotClassName: 'bg-rose-600',
    };
  }

  if (isOwnTask) {
    return {
      label: 'Мои / выбранный исполнитель',
      className: 'border-emerald-700 bg-emerald-600 text-white',
      dotClassName: 'bg-emerald-600',
    };
  }

  if (task.priority === 'high' || task.priority === 'urgent') {
    return {
      label: 'Высокий приоритет',
      className: 'border-orange-600 bg-orange-500 text-white',
      dotClassName: 'bg-orange-500',
    };
  }

  if (task.status === 'onApproval' || task.status === 'approved' || task.status === 'revision') {
    return {
      label: 'Согласование/доработка',
      className: 'border-violet-600 bg-violet-600 text-white',
      dotClassName: 'bg-violet-600',
    };
  }

  if (task.status === 'waiting') {
    return {
      label: 'Ожидание',
      className: 'border-amber-600 bg-amber-500 text-white',
      dotClassName: 'bg-amber-500',
    };
  }

  return {
    label: 'Остальные задачи',
    className: 'border-sky-600 bg-sky-600 text-white',
    dotClassName: 'bg-sky-600',
  };
}

function buildTimelineTaskPlacements(tasks: TaskJournalItem[]) {
  const sorted = tasks
    .slice()
    .sort((a, b) => getTaskStartMinute(a) - getTaskStartMinute(b) || a.id - b.id);
  const laneEnds: number[] = [];
  const laneByTask = new Map<number, number>();

  sorted.forEach((task) => {
    const start = getTaskStartMinute(task);
    const end = getTaskEndMinute(task);
    const lane = laneEnds.findIndex((laneEnd) => laneEnd <= start);
    const nextLane = lane >= 0 ? lane : laneEnds.length;
    laneEnds[nextLane] = end;
    laneByTask.set(task.id, nextLane);
  });

  const laneCount = Math.max(1, laneEnds.length);
  return sorted.map((task) => {
    const rawStart = getTaskStartMinute(task);
    const rawEnd = getTaskEndMinute(task);
    const clampedStart = Math.min(TIMELINE_END_MINUTE - TIMELINE_SNAP_MINUTES, Math.max(TIMELINE_START_MINUTE, rawStart));
    const clampedEnd = Math.max(clampedStart + TIMELINE_SNAP_MINUTES, Math.min(TIMELINE_END_MINUTE, rawEnd));
    return {
      task,
      lane: laneByTask.get(task.id) ?? 0,
      laneCount,
      top: (clampedStart - TIMELINE_START_MINUTE) * TIMELINE_MINUTE_HEIGHT,
      height: Math.max(36, (clampedEnd - clampedStart) * TIMELINE_MINUTE_HEIGHT),
    };
  });
}

function getTimelineDayMinWidth(laneCount: number) {
  return Math.max(
    TIMELINE_DAY_MIN_WIDTH,
    Math.max(1, laneCount) * (TIMELINE_TASK_MIN_READABLE_WIDTH + TIMELINE_TASK_LANE_GAP),
  );
}

function buildTimelineDropDate(day: Date, relativeY: number) {
  const safeRelativeY = Number.isFinite(relativeY) ? relativeY : 0;
  const minutesFromStart = Math.round((safeRelativeY / TIMELINE_MINUTE_HEIGHT) / TIMELINE_SNAP_MINUTES) * TIMELINE_SNAP_MINUTES;
  const targetMinutes = Math.min(
    TIMELINE_END_MINUTE - TIMELINE_SNAP_MINUTES,
    Math.max(TIMELINE_START_MINUTE, TIMELINE_START_MINUTE + minutesFromStart),
  );
  return new Date(fromTimeZoneDateTimeInput(
    `${toLocalDateKey(day)}T${String(Math.floor(targetMinutes / 60)).padStart(2, '0')}:${String(targetMinutes % 60).padStart(2, '0')}`,
    activeTaskJournalTimeZoneId,
  ));
}

function parseLocalInputDate(value: string) {
  if (!value) {
    return null;
  }

  const date = new Date(fromTimeZoneDateTimeInput(value, activeTaskJournalTimeZoneId));
  return Number.isNaN(date.getTime()) ? null : date;
}

function getMinuteOfDay(date: Date) {
  const parts = getZonedParts(date, activeTaskJournalTimeZoneId);
  return parts ? parts.hour * 60 + parts.minute : date.getHours() * 60 + date.getMinutes();
}

function taskMatchesAssignee(task: TaskJournalItem, assigneeUserId?: number | null, assigneeName?: string) {
  if (assigneeUserId && (task.assigneeUserId === assigneeUserId || hasAssigneeParticipant(task, String(assigneeUserId)))) {
    return true;
  }

  const normalizedName = assigneeName?.trim().toLowerCase();
  if (!normalizedName) {
    return false;
  }

  return [task.assigneeName, ...getTaskAssigneeNames(task)]
    .map((value) => value.trim().toLowerCase())
    .some((value) => value === normalizedName);
}

function formatTimeRange(start: Date, durationMinutes: number) {
  const end = new Date(start.getTime() + normalizeTaskDuration(durationMinutes) * 60 * 1000);
  return `${formatTimeOnly(start)}-${formatTimeOnly(end)}`;
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
const TIMELINE_START_HOUR = 8;
const TIMELINE_END_HOUR = 21;
const TIMELINE_HOUR_HEIGHT = 64;
const TIMELINE_START_MINUTE = TIMELINE_START_HOUR * 60;
const TIMELINE_END_MINUTE = TIMELINE_END_HOUR * 60;
const TIMELINE_MINUTE_HEIGHT = TIMELINE_HOUR_HEIGHT / 60;
const TIMELINE_SNAP_MINUTES = 15;
const TIMELINE_TIME_COLUMN_WIDTH = 72;
const TIMELINE_DAY_MIN_WIDTH = 180;
const TIMELINE_TASK_MIN_READABLE_WIDTH = 116;
const TIMELINE_TASK_LANE_GAP = 8;

export function TaskJournal({
  initialView = 'list',
  mode = 'journal',
  embedded = false,
  contextFilter,
  underControlOnly = false,
  routeTaskId = null,
  onRouteTaskChange,
  onOpenClient,
  onOpenCase,
  onOpenLegalEntity,
  onCreateInvoiceDraft,
  overdueFilterSignal = 0,
}: TaskJournalProps) {
  const { user, isAdmin } = useAuth();
  const toast = useToast();
  const { confirm, prompt } = useDialog();
  const currentUserName = user?.fullName || user?.email || 'Текущий пользователь';
  const currentUserId = user?.id ?? null;
  const [offlineCalendarEnabled, setOfflineCalendarEnabled] = useState(false);
  const [offlineCalendarSnapshot, setOfflineCalendarSnapshot] = useState<OfflineCalendarSnapshot | null>(null);
  const [offlineCalendarTrustedDevice, setOfflineCalendarTrustedDeviceState] = useState(false);
  const taskTableLayoutSettings = usePersistedTableSettings<TaskTableColumnKey>({
    storageKey: `pp.task_journal.table_settings.v1.user:${currentUserId ?? 'guest'}`,
    settingsKey: TASK_JOURNAL_TABLE_SETTINGS_KEY,
    columns: TASK_JOURNAL_TABLE_COLUMNS,
    defaultPageSize: 25,
    minColumnWidth: 96,
    enabled: currentUserId !== null,
  });
  const [currentTimeZoneId, setCurrentTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const isAdminUser = isAdmin();
  const permissions = useRolePermissions(user?.role?.id);
  const canViewClients = isAdminUser || permissions.clients.canView;
  const canViewLegalEntities = isAdminUser || permissions.legalEntities.canView;
  const canViewCases = isAdminUser || permissions.cases.canView;
  const { clients } = useClients({ enabled: canViewClients });
  const { legalEntities } = useLegalEntities({ enabled: canViewLegalEntities });
  const canCreateTasks = !offlineCalendarSnapshot && (isAdminUser || permissions.tasks.canCreate);
  const canEditTasks = !offlineCalendarSnapshot && (isAdminUser || permissions.tasks.canEdit);
  const canDeleteTasks = !offlineCalendarSnapshot
    && (isAdminUser || permissions.tasks.canDelete || permissions.tasks.canDeleteOthers);
  const canExportTasks = isAdminUser || permissions.tasks.canExport;
  const canExportCases = isAdminUser || permissions.cases.canExport;
  const canSendTaskFilesToYandexDisk = isAdminUser || (canExportTasks && permissions.yandexDisk?.canCreate === true);
  const taskCalendarViewScope = normalizeTaskCalendarViewScope(permissions.calendar.viewScope);
  const calendarVisibleUserIds = useMemo(
    () => permissions.calendar.visibleUserIds ?? [],
    [permissions.calendar.visibleUserIds],
  );
  const canViewEmployeeTaskCalendars = isAdminUser
    || taskCalendarViewScope === 'department'
    || taskCalendarViewScope === 'all'
    || taskCalendarViewScope === 'specific';
  const isApprovalsMode = mode === 'approvals';
  const taskContextDefaults = useMemo<Partial<TaskJournalUpsertPayload>>(() => {
    if (!contextFilter) {
      return {};
    }

    const clientId = contextFilter.clientId ?? null;
    const legalEntityId = contextFilter.legalEntityId ?? null;
    const objectName = contextFilter.objectName?.trim() || contextFilter.title?.trim() || '';
    return {
      isInternal: false,
      clientId,
      legalEntityId,
      clientCaseId: null,
      objectName,
    };
  }, [contextFilter]);
  const initialPreferencesRef = useRef<TaskJournalFilterPreferences | null>(null);
  if (initialPreferencesRef.current === null) {
    initialPreferencesRef.current = readTaskJournalFilterPreferences(currentUserId, mode, initialView);
  }

  const initialPreferences = overdueFilterSignal > 0 && mode === 'journal'
    ? {
        ...initialPreferencesRef.current,
        viewMode: 'list' as const,
        quickFilter: 'assignedToMe' as const,
        metricFilter: 'none' as const,
        search: '',
        dateFrom: '',
        dateTo: '',
        statusFilter: 'overdue' as const,
        counterpartyFilter: 'all',
        caseFilter: 'all',
        assigneeFilter: 'all',
        typeFilter: 'all' as const,
        priorityFilter: 'all' as const,
        periodMode: 'all' as const,
        sortBy: 'dueAt' as const,
        sortDir: 'asc' as const,
      }
    : initialPreferencesRef.current;
  const [tasks, setTasks] = useState<TaskJournalItem[]>([]);
  const [caseCalendarEvents, setCaseCalendarEvents] = useState<CaseCalendarEvent[]>([]);
  const [caseCalendarSummary, setCaseCalendarSummary] = useState<CaseCalendarSummary | null>(null);
  const [caseCalendarLoading, setCaseCalendarLoading] = useState(false);
  const [caseCalendarExporting, setCaseCalendarExporting] = useState(false);
  const [taskSettings, setTaskSettings] = useState<TaskJournalSettingsDto>(DEFAULT_TASK_JOURNAL_SETTINGS);
  const requireCompletionResult = taskSettings.requireCompletionResult !== false;
  const [completedTaskCalendarAction, setCompletedTaskCalendarAction] = useState<'color' | 'hide'>('color');
  const [taskNotificationDefaults, setTaskNotificationDefaults] = useState<TaskNotificationDefaults>(DEFAULT_TASK_NOTIFICATION_DEFAULTS);
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [automationCandidates, setAutomationCandidates] = useState<TaskJournalCaseAutomationCandidate[]>([]);
  const [creatingAutomationCaseId, setCreatingAutomationCaseId] = useState<number | null>(null);
  const [creatingAutomationAll, setCreatingAutomationAll] = useState(false);
  const automationCandidatesRequestRef = useRef(0);
  const automationCandidatesInitializedRef = useRef(false);
  const automationCandidatesRequestKeyRef = useRef<string | null>(null);
  const taskJournalEventSourceRef = useRef<object>({});
  const [loading, setLoading] = useState(true);
  const [filterOwnerKey, setFilterOwnerKey] = useState(() => getTaskJournalFilterStorageKey(currentUserId, mode));
  const [savedViewOwnerKey, setSavedViewOwnerKey] = useState(() => getTaskJournalSavedViewsStorageKey(currentUserId, mode));

  const [quickFilter, setQuickFilter] = useState<QuickFilter>(initialPreferences.quickFilter);
  const [metricFilter, setMetricFilter] = useState<MetricFilter>(initialPreferences.metricFilter);
  const [search, setSearch] = useState(initialPreferences.search);
  const searchRef = useRef(initialPreferences.search);
  const [dateFromFilter, setDateFromFilter] = useState(initialPreferences.dateFrom);
  const [dateToFilter, setDateToFilter] = useState(initialPreferences.dateTo);
  const [viewMode, setViewMode] = useState<ViewMode>(initialPreferences.viewMode);
  const shouldUseCalendarVisibility = viewMode === 'calendar' || initialView === 'calendar';
  const shouldUseOfflineCalendar = shouldUseCalendarVisibility
    || viewMode === 'timeline'
    || initialView === 'timeline';
  const shouldUseOfflineCalendarRef = useRef(shouldUseOfflineCalendar);
  shouldUseOfflineCalendarRef.current = shouldUseOfflineCalendar;
  const [calendarColorMode, setCalendarColorMode] = useState<TaskCalendarColorMode>(initialPreferences.calendarColorMode);
  const [calendarConflictPolicy, setCalendarConflictPolicy] = useState<TaskCalendarConflictPolicy>(initialPreferences.calendarConflictPolicy);
  const [calendarLoadWarningMode, setCalendarLoadWarningMode] = useState<TaskCalendarLoadWarningMode>(initialPreferences.calendarLoadWarningMode);
  const [icsReminderPreset, setIcsReminderPreset] = useState<IcsReminderPreset>(initialPreferences.icsReminderPreset);
  const [periodMode, setPeriodMode] = useState<PeriodMode>(initialPreferences.periodMode);
  const [statusFilter, setStatusFilter] = useState<TaskStatusFilter>(initialPreferences.statusFilter);
  const [counterpartyFilter, setCounterpartyFilter] = useState(initialPreferences.counterpartyFilter);
  const [counterpartySearchOpen, setCounterpartySearchOpen] = useState(false);
  const [caseFilter, setCaseFilter] = useState(initialPreferences.caseFilter);
  const selectedFilterClientId = counterpartyFilter.startsWith('client:')
    ? Number(counterpartyFilter.slice('client:'.length))
    : undefined;
  const selectedFilterLegalEntityId = counterpartyFilter.startsWith('legal:')
    ? Number(counterpartyFilter.slice('legal:'.length))
    : undefined;
  const [assigneeFilter, setAssigneeFilter] = useState(initialPreferences.assigneeFilter);
  const [typeFilter, setTypeFilter] = useState<TaskJournalType | 'all'>(initialPreferences.typeFilter);
  const [priorityFilter, setPriorityFilter] = useState<TaskJournalPriority | 'all'>(initialPreferences.priorityFilter);
  const [sortBy, setSortBy] = useState<TaskJournalSortKey>(initialPreferences.sortBy);
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>(initialPreferences.sortDir);
  const [savedViews, setSavedViews] = useState<TaskJournalSavedView[]>(() => readTaskJournalSavedViews(currentUserId, mode, initialPreferences.viewMode));
  const [selectedSavedViewId, setSelectedSavedViewId] = useState('');
  const [modal, setModal] = useState<ModalState>(null);
  const [creationWizard, setCreationWizard] = useState<TaskCreationWizardState>(null);
  const [completionPromptTask, setCompletionPromptTask] = useState<TaskJournalItem | null>(null);
  const [completionPromptResult, setCompletionPromptResult] = useState('');
  const [completionPromptError, setCompletionPromptError] = useState<string | null>(null);
  const [completionPromptSaving, setCompletionPromptSaving] = useState(false);
  const [quickApprovalPromptTask, setQuickApprovalPromptTask] = useState<TaskJournalItem | null>(null);
  const [quickApprovalPromptResult, setQuickApprovalPromptResult] = useState('');
  const [quickApprovalPromptError, setQuickApprovalPromptError] = useState<string | null>(null);
  const [quickApprovalPromptSaving, setQuickApprovalPromptSaving] = useState(false);
  const [deadlineChangeDecisionPrompt, setDeadlineChangeDecisionPrompt] = useState<DeadlineChangeDecisionPromptState>(null);
  const [deadlineChangeDecisionDueAt, setDeadlineChangeDecisionDueAt] = useState('');
  const [deadlineChangeDecisionComment, setDeadlineChangeDecisionComment] = useState('');
  const [deadlineChangeDecisionError, setDeadlineChangeDecisionError] = useState<string | null>(null);
  const [deadlineChangeDecisionSaving, setDeadlineChangeDecisionSaving] = useState<'approve' | 'reject' | null>(null);
  const [approvalDecisionPrompt, setApprovalDecisionPrompt] = useState<ApprovalDecisionPromptState>(null);
  const [approvalDecisionRevisionText, setApprovalDecisionRevisionText] = useState('');
  const [approvalDecisionRevisionDueAt, setApprovalDecisionRevisionDueAt] = useState('');
  const [approvalDecisionError, setApprovalDecisionError] = useState<string | null>(null);
  const [approvalDecisionSaving, setApprovalDecisionSaving] = useState<'approve' | 'approveComplete' | 'approveReturn' | 'revision' | null>(null);
  const [forcedDecisionSnoozeRevision, setForcedDecisionSnoozeRevision] = useState(0);
  const forcedDecisionSnoozes = useMemo(() => ({
    revision: forcedDecisionSnoozeRevision,
    values: readForcedTaskSnoozes(),
  }), [forcedDecisionSnoozeRevision]);
  const [highlightedTaskId, setHighlightedTaskId] = useState<number | null>(routeTaskId);
  const [calendarAnchorDate, setCalendarAnchorDate] = useState(() => getCurrentCalendarDate());
  const [calendarSelectedDate, setCalendarSelectedDate] = useState(() => getCurrentCalendarDate());
  const [calendarMovingTaskId, setCalendarMovingTaskId] = useState<number | null>(null);
  const [calendarResizingTaskId, setCalendarResizingTaskId] = useState<number | null>(null);
  const [selectedTaskIds, setSelectedTaskIds] = useState<Set<number>>(() => new Set());
  const [bulkAssigneeUserId, setBulkAssigneeUserId] = useState('');
  const [bulkPriority, setBulkPriority] = useState<TaskJournalPriority | ''>('');
  const [bulkStatus, setBulkStatus] = useState<TaskJournalStatus | ''>('');
  const [bulkDueAt, setBulkDueAt] = useState('');
  const [bulkSaving, setBulkSaving] = useState(false);
  const [bulkDeleting, setBulkDeleting] = useState(false);
  const [templatesPanelOpen, setTemplatesPanelOpen] = useState(false);
  const routeTaskRequestRef = useRef(0);
  const lastOpenedRouteTaskIdRef = useRef<number | null>(null);
  const processedDeadlineChangeRequestIdsRef = useRef<Set<number>>(new Set());
  const previousCalendarTimeZoneRef = useRef(currentTimeZoneId);
  const skipNextFilterSaveRef = useRef(false);
  const previousInitialViewRef = useRef(initialView);
  const icsReminderMinutes = useMemo(
    () => ICS_REMINDER_PRESET_OPTIONS.find((option) => option.value === icsReminderPreset)?.minutes ?? ICS_REMINDER_PRESET_OPTIONS[0].minutes,
    [icsReminderPreset],
  );

  useEffect(() => {
    if (previousInitialViewRef.current === initialView) {
      return;
    }

    previousInitialViewRef.current = initialView;
    setViewMode(initialView);
  }, [initialView]);

  useEffect(() => {
    if (mode !== 'journal' || overdueFilterSignal <= 0) {
      return;
    }

    setViewMode('list');
    setQuickFilter('assignedToMe');
    setMetricFilter('none');
    setSearch('');
    setDateFromFilter('');
    setDateToFilter('');
    setStatusFilter('overdue');
    setCounterpartyFilter('all');
    setCaseFilter('all');
    setAssigneeFilter('all');
    setTypeFilter('all');
    setPriorityFilter('all');
    setPeriodMode('all');
    setSortBy('dueAt');
    setSortDir('asc');
  }, [mode, overdueFilterSignal]);

  useEffect(() => {
    const nextOwnerKey = getTaskJournalFilterStorageKey(currentUserId, mode);
    if (nextOwnerKey === filterOwnerKey) {
      return;
    }

    const nextPreferences = readTaskJournalFilterPreferences(currentUserId, mode, initialView);
    skipNextFilterSaveRef.current = true;
    setFilterOwnerKey(nextOwnerKey);
    setViewMode(nextPreferences.viewMode);
    setCalendarColorMode(nextPreferences.calendarColorMode);
    setCalendarConflictPolicy(nextPreferences.calendarConflictPolicy);
    setCalendarLoadWarningMode(nextPreferences.calendarLoadWarningMode);
    setIcsReminderPreset(nextPreferences.icsReminderPreset);
    setQuickFilter(nextPreferences.quickFilter);
    setMetricFilter(nextPreferences.metricFilter);
    setSearch(nextPreferences.search);
    setDateFromFilter(nextPreferences.dateFrom);
    setDateToFilter(nextPreferences.dateTo);
    setStatusFilter(nextPreferences.statusFilter);
    setCounterpartyFilter(nextPreferences.counterpartyFilter);
    setCaseFilter(nextPreferences.caseFilter);
    setAssigneeFilter(nextPreferences.assigneeFilter);
    setTypeFilter(nextPreferences.typeFilter);
    setPriorityFilter(nextPreferences.priorityFilter);
    setPeriodMode(nextPreferences.periodMode);
    setSortBy(nextPreferences.sortBy);
    setSortDir(nextPreferences.sortDir);
  }, [currentUserId, filterOwnerKey, initialView, mode]);

  useEffect(() => {
    const nextOwnerKey = getTaskJournalSavedViewsStorageKey(currentUserId, mode);
    if (nextOwnerKey === savedViewOwnerKey) {
      return;
    }

    setSavedViewOwnerKey(nextOwnerKey);
    setSavedViews(readTaskJournalSavedViews(currentUserId, mode, initialView));
    setSelectedSavedViewId('');
  }, [currentUserId, initialView, mode, savedViewOwnerKey]);

  useEffect(() => {
    if (skipNextFilterSaveRef.current) {
      skipNextFilterSaveRef.current = false;
      return;
    }

    writeTaskJournalFilterPreferences(currentUserId, {
      viewMode,
      calendarColorMode,
      calendarConflictPolicy,
      calendarLoadWarningMode,
      icsReminderPreset,
      quickFilter,
      metricFilter,
      search,
      dateFrom: dateFromFilter,
      dateTo: dateToFilter,
      statusFilter,
      counterpartyFilter,
      caseFilter,
      assigneeFilter,
      typeFilter,
      priorityFilter,
      periodMode,
      sortBy,
      sortDir,
    }, mode);
  }, [assigneeFilter, calendarColorMode, calendarConflictPolicy, calendarLoadWarningMode, caseFilter, counterpartyFilter, currentUserId, dateFromFilter, dateToFilter, icsReminderPreset, metricFilter, mode, periodMode, priorityFilter, quickFilter, search, sortBy, sortDir, statusFilter, typeFilter, viewMode]);

  const captureSavedViewFilters = (): TaskJournalFilterPreferences => ({
    viewMode,
    calendarColorMode,
    calendarConflictPolicy,
    calendarLoadWarningMode,
    icsReminderPreset,
    quickFilter,
    metricFilter,
    search,
    dateFrom: dateFromFilter,
    dateTo: dateToFilter,
    statusFilter,
    counterpartyFilter,
    caseFilter,
    assigneeFilter,
    typeFilter,
    priorityFilter,
    periodMode,
    sortBy,
    sortDir,
  });

  const applySavedViewFilters = (filters: TaskJournalFilterPreferences) => {
    setViewMode(filters.viewMode);
    setCalendarColorMode(filters.calendarColorMode);
    setCalendarConflictPolicy(filters.calendarConflictPolicy);
    setCalendarLoadWarningMode(filters.calendarLoadWarningMode);
    setIcsReminderPreset(filters.icsReminderPreset);
    setQuickFilter(filters.quickFilter);
    setMetricFilter(filters.metricFilter);
    setSearch(filters.search);
    setDateFromFilter(filters.dateFrom);
    setDateToFilter(filters.dateTo);
    setStatusFilter(filters.statusFilter);
    setCounterpartyFilter(filters.counterpartyFilter);
    setCaseFilter(filters.caseFilter);
    setAssigneeFilter(filters.assigneeFilter);
    setTypeFilter(filters.typeFilter);
    setPriorityFilter(filters.priorityFilter);
    setPeriodMode(filters.periodMode);
    setSortBy(filters.sortBy);
    setSortDir(filters.sortDir);
  };

  const handleSavedViewSelect = (viewId: string) => {
    setSelectedSavedViewId(viewId);
    const view = savedViews.find((item) => item.id === viewId);
    if (!view) {
      return;
    }

    applySavedViewFilters(view.filters);
  };

  const handleSaveCurrentView = async () => {
    const currentView = savedViews.find((item) => item.id === selectedSavedViewId);
    const name = (await prompt({
      title: 'Сохранить представление',
      label: 'Название представления',
      defaultValue: currentView?.name ?? '',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    }))?.trim();
    if (!name) {
      return;
    }

    const nextView: TaskJournalSavedView = {
      id: currentView?.id ?? `view-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      name,
      filters: captureSavedViewFilters(),
      updatedAt: new Date().toISOString(),
    };
    const nextViews = [nextView, ...savedViews.filter((item) => item.id !== nextView.id)].slice(0, 20);

    setSavedViews(nextViews);
    setSelectedSavedViewId(nextView.id);
    writeTaskJournalSavedViews(currentUserId, mode, nextViews);
    toast.success(`Представление «${name}» сохранено.`, 'Журнал задач');
  };

  const handleDeleteCurrentView = () => {
    if (!selectedSavedViewId) {
      return;
    }

    const deletedView = savedViews.find((item) => item.id === selectedSavedViewId);
    const nextViews = savedViews.filter((item) => item.id !== selectedSavedViewId);
    setSavedViews(nextViews);
    setSelectedSavedViewId('');
    writeTaskJournalSavedViews(currentUserId, mode, nextViews);
    toast.success(
      deletedView ? `Представление «${deletedView.name}» удалено.` : 'Представление удалено.',
      'Журнал задач',
    );
  };

  const openTaskEditor = useCallback(
    (task: TaskJournalItem, options?: { syncRoute?: boolean }) => {
      setModal({
        mode: 'view',
        task,
      });
      setHighlightedTaskId(task.id);

      if (options?.syncRoute ?? true) {
        lastOpenedRouteTaskIdRef.current = task.id;
        onRouteTaskChange?.(task.id);
      }
    },
    [onRouteTaskChange],
  );

  const openTaskCreate = useCallback(() => {
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Журнал задач');
      return;
    }

    setCreationWizard({ defaults: taskContextDefaults });
    setHighlightedTaskId(null);
    onRouteTaskChange?.(null);
  }, [canCreateTasks, onRouteTaskChange, taskContextDefaults, toast]);

  const openTaskCreateFromTemplate = useCallback((defaults: Partial<TaskJournalUpsertPayload>) => {
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Журнал задач');
      return;
    }

    setCreationWizard({ defaults: { ...taskContextDefaults, ...defaults } });
    setHighlightedTaskId(null);
    onRouteTaskChange?.(null);
  }, [canCreateTasks, onRouteTaskChange, taskContextDefaults, toast]);

  const openTaskCreateForDate = useCallback((date: Date) => {
    if (isApprovalsMode) {
      return;
    }
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Журнал задач');
      return;
    }

    setCalendarSelectedDate(date);
    setCreationWizard({ defaults: taskContextDefaults, dueAt: buildDefaultDueAtForDate(date) });
    setHighlightedTaskId(null);
    onRouteTaskChange?.(null);
  }, [canCreateTasks, isApprovalsMode, onRouteTaskChange, taskContextDefaults, toast]);

  const closeTaskModal = useCallback(() => {
    setModal(null);
    onRouteTaskChange?.(null);
  }, [onRouteTaskChange]);

  const emitTaskChange = useCallback(() => {
    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, {
      detail: { source: taskJournalEventSourceRef.current },
    }));
  }, []);

  const loadAutomationCandidates = useCallback(async (
    settings: TaskJournalSettingsDto,
    searchValue: string,
    signal?: AbortSignal,
  ) => {
    if (embedded || underControlOnly || isApprovalsMode || !canCreateTasks || !canViewCases || !settings.caseInactivityAutomationEnabled) {
      return [];
    }

    const params = {
      inactiveDays: settings.caseInactivityDays,
      limit: Math.min(settings.caseInactivityLimit, 20),
      escalationDays: settings.caseInactivityEscalationDays,
      search: searchValue.trim() || undefined,
    };
    return signal
      ? apiService.getTaskJournalCaseAutomationCandidates(params, { signal })
      : apiService.getTaskJournalCaseAutomationCandidates(params);
  }, [canCreateTasks, canViewCases, embedded, isApprovalsMode, underControlOnly]);

  const refreshTasks = useCallback(async () => {
    setLoading(true);
    try {
      const settingsPromise = apiService.getTaskJournalSettings();
      const featureFlagsPromise = apiService.getFeatureFlags().catch(() => null);
      const trustedDevicePromise = currentUserId
        ? getOfflineCalendarTrustedDevice(currentUserId).catch(() => false)
        : Promise.resolve(false);
      let automationSearchValue = searchRef.current;
      const automationPromise = settingsPromise.then((settingsResponse) => {
        automationSearchValue = searchRef.current;
        return loadAutomationCandidates(settingsResponse, automationSearchValue);
      });
      const [taskItems, assigneeResponse, settingsResponse, notificationDefaultsResponse, timeZoneResponse, calendarSyncSettingsResponse, automationResponse, featureFlagsResponse, trustedDevice] = await Promise.all([
        loadAllTaskJournalItems(contextFilter, underControlOnly, shouldUseCalendarVisibility, sortBy, sortDir, true),
        apiService.getTaskJournalAssignees({ calendarView: shouldUseCalendarVisibility }),
        settingsPromise,
        apiService.getUserSetting(TASK_NOTIFICATION_DEFAULTS_SETTING_KEY).catch(() => null),
        apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY).catch(() => null),
        apiService.getCalendarSyncSettings().catch(() => null),
        automationPromise,
        featureFlagsPromise,
        trustedDevicePromise,
      ]);
      setTasks(taskItems);
      setOfflineCalendarSnapshot(null);
      setOfflineCalendarTrustedDeviceState(trustedDevice);
      const offlineEnabled = featureFlagsResponse?.legal.offlineCalendarReadOnly === true;
      setOfflineCalendarEnabled(offlineEnabled);
      if (!offlineEnabled && currentUserId) {
        await clearOfflineCalendarSnapshot(currentUserId).catch(() => undefined);
      }
      setAssignees(assigneeResponse);
      setAutomationCandidates(automationResponse);
      automationCandidatesRequestKeyRef.current = buildAutomationCandidatesRequestKey(settingsResponse, automationSearchValue);
      automationCandidatesInitializedRef.current = true;
      setTaskSettings({
        ...settingsResponse,
        stickerStyles: normalizeTaskStickerStyles(settingsResponse.stickerStyles),
      });
      setTaskNotificationDefaults(parseTaskNotificationDefaults(notificationDefaultsResponse?.value));
      setCurrentTimeZoneId(normalizeUserTimeZoneId(timeZoneResponse?.value));
      setCompletedTaskCalendarAction(calendarSyncSettingsResponse?.completedTaskCalendarAction === 'hide' ? 'hide' : 'color');
    } catch {
      const snapshot = currentUserId && shouldUseOfflineCalendarRef.current
        ? await loadOfflineCalendarSnapshot(currentUserId).catch(() => null)
        : null;
      if (snapshot) {
        setTasks(snapshot.items);
        setAssignees([]);
        setAutomationCandidates([]);
        setOfflineCalendarEnabled(true);
        setOfflineCalendarSnapshot(snapshot);
        setOfflineCalendarTrustedDeviceState(snapshot.trustedDevice);
        toast.info('Показан сохранённый календарь. Изменения недоступны до восстановления связи.', 'Офлайн-календарь');
      } else {
        toast.error('Не удалось загрузить журнал задач.', 'Журнал задач');
      }
    } finally {
      setLoading(false);
    }
  }, [contextFilter, currentUserId, loadAutomationCandidates, shouldUseCalendarVisibility, sortBy, sortDir, toast, underControlOnly]);

  useEffect(() => {
    void refreshTasks();
    const refresh = (event: Event) => {
      const source = event instanceof CustomEvent
        ? (event.detail as { source?: object } | null)?.source
        : null;
      if (source === taskJournalEventSourceRef.current) {
        return;
      }

      void refreshTasks();
    };
    window.addEventListener(TASK_JOURNAL_EVENT, refresh);
    return () => window.removeEventListener(TASK_JOURNAL_EVENT, refresh);
  }, [refreshTasks]);

  useEffect(() => {
    if (!offlineCalendarEnabled
      || offlineCalendarSnapshot
      || !currentUserId
      || !shouldUseOfflineCalendar
      || tasks.length === 0) {
      return;
    }

    void saveOfflineCalendarSnapshot(currentUserId, tasks, offlineCalendarTrustedDevice).catch(() => null);
  }, [
    currentUserId,
    offlineCalendarEnabled,
    offlineCalendarSnapshot,
    offlineCalendarTrustedDevice,
    shouldUseOfflineCalendar,
    tasks,
  ]);

  useEffect(() => {
    if (!isApprovalsMode) {
      return;
    }

    const refreshSnoozes = () => setForcedDecisionSnoozeRevision(Date.now());
    const handleStorage = (event: StorageEvent) => {
      if (event.key === FORCED_TASK_SNOOZE_STORAGE_KEY) {
        refreshSnoozes();
      }
    };
    const intervalId = window.setInterval(refreshSnoozes, 30_000);
    window.addEventListener(FORCED_TASK_SNOOZES_EVENT, refreshSnoozes);
    window.addEventListener('storage', handleStorage);

    return () => {
      window.clearInterval(intervalId);
      window.removeEventListener(FORCED_TASK_SNOOZES_EVENT, refreshSnoozes);
      window.removeEventListener('storage', handleStorage);
    };
  }, [isApprovalsMode]);

  useEffect(() => {
    searchRef.current = search;
    if (!automationCandidatesInitializedRef.current) {
      return;
    }

    const requestKey = buildAutomationCandidatesRequestKey(taskSettings, search);
    if (automationCandidatesRequestKeyRef.current === requestKey) {
      return;
    }

    const requestId = automationCandidatesRequestRef.current + 1;
    automationCandidatesRequestRef.current = requestId;
    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => {
      void loadAutomationCandidates(taskSettings, search, controller.signal)
        .then((items) => {
          if (!controller.signal.aborted && automationCandidatesRequestRef.current === requestId) {
            automationCandidatesRequestKeyRef.current = requestKey;
            setAutomationCandidates(items);
          }
        })
        .catch(() => {
          if (!controller.signal.aborted && automationCandidatesRequestRef.current === requestId) {
            setAutomationCandidates([]);
          }
        });
    }, AUTOMATION_CANDIDATES_SEARCH_DELAY_MS);

    return () => {
      window.clearTimeout(timeoutId);
      controller.abort();
    };
  }, [loadAutomationCandidates, search, taskSettings]);

  const assigneeById = useMemo(() => {
    const byId = new Map<number, TaskJournalAssignee>();
    assignees.forEach((assignee) => byId.set(assignee.id, assignee));
    return byId;
  }, [assignees]);

  const assigneeOptions = useMemo(() => Array.from(assigneeById.values()), [assigneeById]);

  const counterpartyCaseFilterData = useMemo(() => {
    const counterparties = new Map<string, string>();
    const casesByCounterparty = new Map<string, Map<number, string>>();
    tasks.forEach((task) => {
      const counterpartyKey = task.clientId
        ? `client:${task.clientId}`
        : task.legalEntityId
          ? `legal:${task.legalEntityId}`
          : '';
      const counterpartyName = task.clientName?.trim() || task.legalEntityName?.trim() || '';
      if (!counterpartyKey || !counterpartyName) {
        return;
      }

      if (!counterparties.has(counterpartyKey)) {
        counterparties.set(counterpartyKey, counterpartyName);
      }
      if (!task.clientCaseId || !task.clientCaseTitle?.trim()) {
        return;
      }

      let counterpartyCases = casesByCounterparty.get(counterpartyKey);
      if (!counterpartyCases) {
        counterpartyCases = new Map<number, string>();
        casesByCounterparty.set(counterpartyKey, counterpartyCases);
      }
      if (!counterpartyCases.has(task.clientCaseId)) {
        counterpartyCases.set(task.clientCaseId, task.clientCaseTitle.trim());
      }
    });

    return {
      counterparties: Array.from(counterparties, ([value, label]) => ({ value, label }))
        .sort((left, right) => left.label.localeCompare(right.label, 'ru')),
      casesByCounterparty,
    };
  }, [tasks]);

  const caseFilterOptions = useMemo(() => {
    if (counterpartyFilter === 'all') {
      return [];
    }

    const counterpartyCases = counterpartyCaseFilterData.casesByCounterparty.get(counterpartyFilter);
    return counterpartyCases
      ? Array.from(counterpartyCases, ([id, label]) => ({ id, label }))
          .sort((left, right) => left.label.localeCompare(right.label, 'ru'))
      : [];
  }, [counterpartyCaseFilterData, counterpartyFilter]);

  const selectedFilterCounterparty = useMemo(
    () => counterpartyCaseFilterData.counterparties.find((counterparty) => counterparty.value === counterpartyFilter) ?? null,
    [counterpartyCaseFilterData.counterparties, counterpartyFilter],
  );

  const counterpartySearchOptions = useMemo(() => {
    const normalizedSearch = search.trim().toLocaleLowerCase('ru');
    if (normalizedSearch.length < 2) {
      return [];
    }

    return counterpartyCaseFilterData.counterparties
      .filter((counterparty) => counterparty.label.toLocaleLowerCase('ru').includes(normalizedSearch))
      .slice(0, 8);
  }, [counterpartyCaseFilterData.counterparties, search]);

  useEffect(() => {
    if (caseFilter === 'all' || counterpartyFilter !== 'all') {
      return;
    }

    const matchingCounterparty = Array.from(counterpartyCaseFilterData.casesByCounterparty)
      .find(([, cases]) => cases.has(Number(caseFilter)))?.[0];
    if (matchingCounterparty) {
      setCounterpartyFilter(matchingCounterparty);
    } else {
      setCaseFilter('all');
    }
  }, [caseFilter, counterpartyCaseFilterData, counterpartyFilter]);

  const visibleTaskCalendarUserIds = useMemo(() => {
    if (isAdminUser || taskCalendarViewScope === 'all') {
      return null;
    }

    const visibleUserIds = new Set<number>();
    if (currentUserId) {
      visibleUserIds.add(currentUserId);
    }

    if (taskCalendarViewScope === 'department') {
      assigneeOptions
        .filter((assignee) => isAssigneeInDepartment(assignee, user?.departmentId, user?.department))
        .forEach((assignee) => visibleUserIds.add(assignee.id));
    } else if (taskCalendarViewScope === 'specific') {
      calendarVisibleUserIds.forEach((userId) => visibleUserIds.add(userId));
    }

    return visibleUserIds;
  }, [assigneeOptions, calendarVisibleUserIds, currentUserId, isAdminUser, taskCalendarViewScope, user?.department, user?.departmentId]);

  const selectedAssigneeTimeZoneId = useMemo(() => {
    if (!canViewEmployeeTaskCalendars || assigneeFilter === 'all' || !/^\d+$/.test(assigneeFilter)) {
      return null;
    }

    const assigneeId = Number(assigneeFilter);
    if (visibleTaskCalendarUserIds && !visibleTaskCalendarUserIds.has(assigneeId)) {
      return null;
    }

    return assigneeById.get(assigneeId)?.timeZoneId ?? null;
  }, [assigneeById, assigneeFilter, canViewEmployeeTaskCalendars, visibleTaskCalendarUserIds]);
  const effectiveTaskJournalTimeZoneId = normalizeUserTimeZoneId(selectedAssigneeTimeZoneId ?? currentTimeZoneId);
  setActiveTaskJournalTimeZoneId(effectiveTaskJournalTimeZoneId);

  const calendarResponsibleUserId = useMemo(() => {
    if (canViewEmployeeTaskCalendars && assigneeFilter !== 'all' && /^\d+$/.test(assigneeFilter)) {
      const assigneeId = Number(assigneeFilter);
      return visibleTaskCalendarUserIds && !visibleTaskCalendarUserIds.has(assigneeId)
        ? currentUserId
        : assigneeId;
    }

    if (visibleTaskCalendarUserIds && currentUserId) {
      return currentUserId;
    }

    return null;
  }, [assigneeFilter, canViewEmployeeTaskCalendars, currentUserId, visibleTaskCalendarUserIds]);

  useEffect(() => {
    setActiveTaskJournalTimeZoneId(effectiveTaskJournalTimeZoneId);
  }, [effectiveTaskJournalTimeZoneId]);

  useEffect(() => {
    const previousTimeZoneId = previousCalendarTimeZoneRef.current;
    if (previousTimeZoneId === effectiveTaskJournalTimeZoneId) {
      return;
    }

    const previousTodayKey = toTimeZoneDateKey(new Date(), previousTimeZoneId);
    const nextToday = getCurrentCalendarDate();
    setCalendarAnchorDate((current) => (toLocalDateKey(current) === previousTodayKey ? nextToday : current));
    setCalendarSelectedDate((current) => (toLocalDateKey(current) === previousTodayKey ? nextToday : current));
    previousCalendarTimeZoneRef.current = effectiveTaskJournalTimeZoneId;
  }, [effectiveTaskJournalTimeZoneId]);

  const refreshCaseCalendarEvents = useCallback(async () => {
    if (viewMode !== 'calendar' && viewMode !== 'timeline') {
      return;
    }

    if (!canViewCases) {
      setCaseCalendarEvents([]);
      setCaseCalendarSummary(null);
      setCaseCalendarLoading(false);
      return;
    }

    const range = getCaseCalendarRange(calendarAnchorDate, viewMode === 'timeline' ? 'week' : periodMode);
    const responsibleUserId = calendarResponsibleUserId ?? undefined;
    const params = {
      from: toLocalDateKey(range.from),
      to: toLocalDateKey(range.to),
      clientCaseId: caseFilter !== 'all' ? Number(caseFilter) : undefined,
      responsibleUserId,
      includeTasks: false,
    };
    setCaseCalendarLoading(true);
    try {
      const [events, summary] = await Promise.all([
        apiService.getCaseCalendarEvents(params),
        apiService.getCaseCalendarSummary(params),
      ]);
      setCaseCalendarEvents(events.filter((event) => event.kind !== 'task'));
      setCaseCalendarSummary(summary);
    } catch {
      setCaseCalendarEvents([]);
      setCaseCalendarSummary(null);
      toast.error('Не удалось загрузить сроки и судебные события.', 'Календарь дел');
    } finally {
      setCaseCalendarLoading(false);
    }
  }, [calendarAnchorDate, calendarResponsibleUserId, canViewCases, caseFilter, periodMode, toast, viewMode]);

  const buildCaseCalendarParams = useCallback(() => {
    const range = getCaseCalendarRange(calendarAnchorDate, viewMode === 'timeline' ? 'week' : periodMode);
    const responsibleUserId = calendarResponsibleUserId ?? undefined;

    return {
      from: toLocalDateKey(range.from),
      to: toLocalDateKey(range.to),
      clientCaseId: caseFilter !== 'all' ? Number(caseFilter) : undefined,
      responsibleUserId,
      includeTasks: false,
    };
  }, [calendarAnchorDate, calendarResponsibleUserId, caseFilter, periodMode, viewMode]);

  useEffect(() => {
    void refreshCaseCalendarEvents();
  }, [refreshCaseCalendarEvents]);

  const createAutomationTask = useCallback(async (candidate: TaskJournalCaseAutomationCandidate) => {
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Автоконтроль дел');
      return;
    }

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
  }, [canCreateTasks, emitTaskChange, openTaskEditor, refreshTasks, toast]);

  const createAllAutomationTasks = useCallback(async () => {
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Автоконтроль дел');
      return;
    }

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
  }, [automationCandidates.length, canCreateTasks, emitTaskChange, openTaskEditor, refreshTasks, toast]);

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

  const counterpartyOptions = useMemo<LookupSelectOption[]>(
    () =>
      clients
        .filter((client) => client?.id && client?.name)
        .map((client) => ({
          value: `client:${client.id}`,
          label: client.name,
          description: [client.phone, client.email].filter(Boolean).join(' · ') || undefined,
          keywords: [String(client.id), client.name, client.phone, client.email, client.company].filter(isKeyword),
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
              ].filter(isKeyword),
            };
          }))
        .sort((a, b) => a.label.localeCompare(b.label, 'ru')),
    [clients, legalEntities],
  );

  const dateRangeBounds = useMemo(() => getTaskDateRangeBounds(dateFromFilter, dateToFilter), [dateFromFilter, dateToFilter]);

  const scopeFilteredTasks = useMemo(() => {
    const q = search.trim().toLowerCase();

    return tasks.filter((task) => {
      const due = new Date(task.dueAt);
      const approvalScopeOk = !isApprovalsMode || isTaskInApprovalsScope(task, currentUserId, isAdminUser);
      const quickOk = matchesQuickFilter(task, quickFilter, currentUserId, currentUserName);
      const assigneeOk =
        assigneeFilter === 'all' ||
        String(task.assigneeUserId ?? task.assigneeName) === assigneeFilter ||
        hasAssigneeParticipant(task, assigneeFilter);
      const taskCounterpartyKey = task.clientId
        ? `client:${task.clientId}`
        : task.legalEntityId
          ? `legal:${task.legalEntityId}`
          : '';
      const counterpartyOk = counterpartyFilter === 'all' || taskCounterpartyKey === counterpartyFilter;
      const caseOk = caseFilter === 'all' || String(task.clientCaseId ?? '') === caseFilter;
      const typeOk = typeFilter === 'all' || task.type === typeFilter;
      const priorityOk = priorityFilter === 'all' || task.priority === priorityFilter;
      const dateOk =
        !Number.isNaN(due.getTime()) &&
        (!dateRangeBounds.from || due >= dateRangeBounds.from) &&
        (!dateRangeBounds.to || due <= dateRangeBounds.to);
      const searchOk = !q || [
        task.objectName,
        task.clientName ?? '',
        task.legalEntityName ?? '',
        task.clientCaseTitle ?? '',
        task.title,
      ].some((value) => value.toLowerCase().includes(q));

      return approvalScopeOk && quickOk && assigneeOk && counterpartyOk && caseOk && typeOk && priorityOk && dateOk && searchOk;
    });
  }, [assigneeFilter, caseFilter, counterpartyFilter, currentUserId, currentUserName, dateRangeBounds, isAdminUser, isApprovalsMode, priorityFilter, quickFilter, search, tasks, typeFilter]);

  const statusAndPeriodFilteredTasks = useMemo(() => {
    const nowDate = getCurrentCalendarDate();
    const { periodStart, periodEnd } = getTaskPeriodBounds(nowDate, periodMode);
    const hasManualDateRange = Boolean(dateFromFilter || dateToFilter);
    const shouldApplyPeriodFilter = metricFilter === 'none' && !hasManualDateRange && periodMode !== 'all';

    const nextTasks = scopeFilteredTasks
      .filter((task) => {
        const due = new Date(task.dueAt);
        const statusOk = matchesStatusFilter(task, statusFilter);
        const periodOk = !shouldApplyPeriodFilter
          ? true
          : viewMode === 'list'
            ? due >= periodStart && due <= periodEnd
            : viewMode === 'board'
              ? due < periodStart || (due >= periodStart && due <= periodEnd)
              : true;

        return statusOk && periodOk;
      });

    return viewMode === 'list'
      ? nextTasks
      : nextTasks.sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
  }, [dateFromFilter, dateToFilter, metricFilter, periodMode, scopeFilteredTasks, statusFilter, viewMode]);

  const filteredTasks = useMemo(() => {
    const nowDate = getCurrentCalendarDate();
    return statusAndPeriodFilteredTasks.filter((task) => matchesMetricFilter(task, metricFilter, nowDate));
  }, [metricFilter, statusAndPeriodFilteredTasks]);

  const pendingDeadlineChangeDecision = useMemo<Exclude<DeadlineChangeDecisionPromptState, null> | null>(() => {
    if (!isApprovalsMode) {
      return null;
    }

    for (const task of tasks) {
      const request = getPendingDeadlineChangeRequests(task)
        .find((item) =>
          !processedDeadlineChangeRequestIdsRef.current.has(item.id) &&
          (forcedDecisionSnoozes.values[getForcedTaskDeadlineChangeSnoozeKey(task.id, item.id)] ?? 0) <= Date.now() &&
          (isAdminUser || (currentUserId !== null && item.reviewerUserId === currentUserId)));
      if (request) {
        return { task, request };
      }
    }

    return null;
  }, [currentUserId, forcedDecisionSnoozes, isAdminUser, isApprovalsMode, tasks]);

  const dismissUnavailableDeadlineChangePrompt = useCallback(async (taskId: number, requestId: number) => {
    processedDeadlineChangeRequestIdsRef.current.add(requestId);
    setForcedDecisionSnoozeRevision((value) => value + 1);
    setDeadlineChangeDecisionPrompt((current) => (
      current?.task.id === taskId && current.request.id === requestId ? null : current
    ));
    setDeadlineChangeDecisionDueAt('');
    setDeadlineChangeDecisionComment('');
    setDeadlineChangeDecisionError(null);
    setDeadlineChangeDecisionSaving(null);
    toast.info(
      'Задача уже удалена или больше недоступна. Запрос переноса срока закрыт и больше не будет показываться.',
      'Согласования',
    );
    await refreshTasks();
  }, [refreshTasks, toast]);

  const pendingApprovalDecision = useMemo<Exclude<ApprovalDecisionPromptState, null> | null>(() => {
    if (!isApprovalsMode) {
      return null;
    }

    for (const task of tasks) {
      if (task.status !== 'onApproval' && task.status !== 'overdueApproval') {
        continue;
      }

      if ((forcedDecisionSnoozes.values[getForcedTaskApprovalSnoozeKey(task.id)] ?? 0) > Date.now()) {
        continue;
      }

      const participant = getCurrentPendingApprover(task);
      if (!participant) {
        continue;
      }

      if (currentUserId !== null && participant.userId === currentUserId) {
        return { task, participant };
      }
    }

    return null;
  }, [currentUserId, forcedDecisionSnoozes, isApprovalsMode, tasks]);

  useEffect(() => {
    if (!isApprovalsMode) {
      return;
    }

    if (deadlineChangeDecisionPrompt) {
      const stillPending = tasks.some((task) =>
        task.id === deadlineChangeDecisionPrompt.task.id &&
        getPendingDeadlineChangeRequests(task).some((request) => request.id === deadlineChangeDecisionPrompt.request.id));
      if (!stillPending) {
        setDeadlineChangeDecisionPrompt(null);
        setDeadlineChangeDecisionDueAt('');
        setDeadlineChangeDecisionComment('');
        setDeadlineChangeDecisionError(null);
      }
      return;
    }

    if (!pendingDeadlineChangeDecision || modal || completionPromptTask || quickApprovalPromptTask || deadlineChangeDecisionSaving || approvalDecisionPrompt || approvalDecisionSaving) {
      return;
    }

    setDeadlineChangeDecisionPrompt(pendingDeadlineChangeDecision);
    setDeadlineChangeDecisionDueAt(toLocalInputValue(pendingDeadlineChangeDecision.request.requestedDueAt));
    setDeadlineChangeDecisionComment('');
    setDeadlineChangeDecisionError(null);
  }, [
    completionPromptTask,
    quickApprovalPromptTask,
    deadlineChangeDecisionPrompt,
    deadlineChangeDecisionSaving,
    isApprovalsMode,
    modal,
    pendingDeadlineChangeDecision,
    approvalDecisionPrompt,
    approvalDecisionSaving,
    tasks,
  ]);

  useEffect(() => {
    if (!deadlineChangeDecisionPrompt) {
      return;
    }

    const { task, request } = deadlineChangeDecisionPrompt;
    let cancelled = false;
    let dismissing = false;

    const verifyTaskStillExists = async () => {
      try {
        await apiService.getTaskJournalItem(task.id);
      } catch (error) {
        if (!cancelled && !dismissing && isHttpErrorStatus(error, 404)) {
          dismissing = true;
          await dismissUnavailableDeadlineChangePrompt(task.id, request.id);
        }
      }
    };

    void verifyTaskStillExists();
    const intervalId = window.setInterval(() => void verifyTaskStillExists(), 15_000);
    const handleWindowFocus = () => void verifyTaskStillExists();
    window.addEventListener('focus', handleWindowFocus);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
      window.removeEventListener('focus', handleWindowFocus);
    };
  }, [deadlineChangeDecisionPrompt, dismissUnavailableDeadlineChangePrompt]);

  useEffect(() => {
    if (!isApprovalsMode) {
      return;
    }

    if (approvalDecisionPrompt) {
      const stillPending = tasks.some((task) => {
        if (task.id !== approvalDecisionPrompt.task.id || (task.status !== 'onApproval' && task.status !== 'overdueApproval')) {
          return false;
        }
        const participant = getCurrentPendingApprover(task);
        return Boolean(
          participant
          && participant.id === approvalDecisionPrompt.participant.id
          && currentUserId !== null
          && participant.userId === currentUserId,
        );
      });
      if (!stillPending) {
        setApprovalDecisionPrompt(null);
        setApprovalDecisionRevisionText('');
        setApprovalDecisionRevisionDueAt('');
        setApprovalDecisionError(null);
      }
      return;
    }

    if (
      !pendingApprovalDecision
      || pendingDeadlineChangeDecision
      || modal
      || completionPromptTask
      || quickApprovalPromptTask
      || deadlineChangeDecisionPrompt
      || deadlineChangeDecisionSaving
      || approvalDecisionSaving
    ) {
      return;
    }

    setApprovalDecisionPrompt(pendingApprovalDecision);
    setApprovalDecisionRevisionText('');
    setApprovalDecisionRevisionDueAt('');
    setApprovalDecisionError(null);
  }, [
    approvalDecisionPrompt,
    approvalDecisionSaving,
    completionPromptTask,
    quickApprovalPromptTask,
    currentUserId,
    deadlineChangeDecisionPrompt,
    deadlineChangeDecisionSaving,
    isApprovalsMode,
    modal,
    pendingDeadlineChangeDecision,
    pendingApprovalDecision,
    tasks,
  ]);

  const calendarTasks = useMemo(
    () => {
      const scopeLimitedTasks = !visibleTaskCalendarUserIds
        ? filteredTasks
        : filteredTasks.filter((task) => taskTouchesCalendarUsers(task, visibleTaskCalendarUserIds, currentUserId, currentUserName));

      return completedTaskCalendarAction === 'hide'
        ? scopeLimitedTasks.filter((task) => task.status !== 'done')
        : scopeLimitedTasks;
    },
    [completedTaskCalendarAction, currentUserId, currentUserName, filteredTasks, visibleTaskCalendarUserIds],
  );

  useEffect(() => {
    setSelectedTaskIds((previous) => {
      const existingIds = new Set(tasks.map((task) => task.id));
      const next = new Set(Array.from(previous).filter((id) => existingIds.has(id)));
      return next.size === previous.size ? previous : next;
    });
  }, [tasks]);

  const selectableTasks = useMemo(
    () => filteredTasks.filter((task) => !isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, permissions.tasks)),
    [currentUserId, filteredTasks, isAdminUser, permissions.tasks, taskSettings],
  );
  const canEditModalTask = useMemo(
    () => Boolean(
      modal?.task &&
      !isTaskReadOnlyForCurrentUser(modal.task, currentUserId, isAdminUser, taskSettings, permissions.tasks),
    ),
    [currentUserId, isAdminUser, modal?.task, permissions.tasks, taskSettings],
  );
  const canDeleteModalTask = useMemo(
    () => Boolean(
      modal?.task
      && canDeleteTaskForCurrentUser(modal.task, currentUserId, isAdminUser, taskSettings, permissions.tasks),
    ),
    [currentUserId, isAdminUser, modal?.task, permissions.tasks, taskSettings],
  );
  const canCompleteModalTask = useMemo(
    () => Boolean(
      modal?.task
      && canCompleteTaskForCurrentUser(modal.task, currentUserId, isAdminUser, taskSettings, permissions.tasks),
    ),
    [currentUserId, isAdminUser, modal?.task, permissions.tasks, taskSettings],
  );
  const selectableTaskIds = useMemo(() => new Set(selectableTasks.map((task) => task.id)), [selectableTasks]);
  const selectedTasksForBulk = useMemo(
    () => selectableTasks.filter((task) => selectedTaskIds.has(task.id)),
    [selectableTasks, selectedTaskIds],
  );
  const unbilledClientTasks = useMemo(
    () => tasks.filter((task) => task.billingStatus === 'unbilled'),
    [tasks],
  );
  const allVisibleBulkSelected = selectableTasks.length > 0 && selectableTasks.every((task) => selectedTaskIds.has(task.id));

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

  const shiftTimelineWeek = (direction: -1 | 1) => {
    const next = addDays(calendarAnchorDate, direction * 7);
    setCalendarAnchorDate(next);
    setCalendarSelectedDate(next);
  };

  const resetCalendarPeriod = () => {
    const today = getCurrentCalendarDate();
    setCalendarAnchorDate(today);
    setCalendarSelectedDate(today);
  };

  const stats = useMemo(() => {
    const active = statusAndPeriodFilteredTasks.filter((task) => isTaskJournalActive(task.status));
    const overdue = active.filter((task) => new Date(task.dueAt).getTime() < Date.now());
    const today = active.filter((task) => isSameTaskDay(task.dueAt, getCurrentCalendarDate()));
    const high = active.filter((task) => task.priority === 'high' || task.priority === 'urgent');
    return { active: active.length, overdue: overdue.length, today: today.length, high: high.length };
  }, [statusAndPeriodFilteredTasks]);

  const boardColumns = useMemo(() => {
    const nowDate = getCurrentCalendarDate();
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
        items: active.filter((task) => isSameTaskDay(task.dueAt, nowDate)),
      },
      {
        key: 'tomorrow',
        title: 'На завтра',
        tone: 'emerald',
        items: active.filter((task) => isSameTaskDay(task.dueAt, tomorrow)),
      },
      {
        key: 'later',
        title: periodMode === 'month' ? 'Дальше в месяце' : 'Ближайшие',
        tone: 'slate',
        items: active.filter((task) => {
          const due = new Date(task.dueAt);
          return due.getTime() >= Date.now() && !isSameTaskDay(task.dueAt, nowDate) && !isSameTaskDay(task.dueAt, tomorrow);
        }),
      },
    ];
  }, [filteredTasks, periodMode]);

  const handleQuickFilterChange = (nextFilter: QuickFilter) => {
    setQuickFilter(nextFilter);
    setMetricFilter('none');

    if (nextFilter === 'assignedToMe') {
      setAssigneeFilter('all');
    }
  };

  const handleSortChange = (nextSortBy: TaskJournalSortKey) => {
    if (sortBy === nextSortBy) {
      setSortDir((current) => (current === 'asc' ? 'desc' : 'asc'));
      return;
    }

    setSortBy(nextSortBy);
    setSortDir('asc');
  };

  const handleMetricFilterClick = (nextFilter: MetricFilter) => {
    const resolvedFilter = metricFilter === nextFilter ? 'none' : nextFilter;
    setMetricFilter(resolvedFilter);

    if (resolvedFilter !== 'none') {
      setStatusFilter('active');
    }
  };

  const toggleTaskSelection = (taskId: number, checked: boolean) => {
    setSelectedTaskIds((previous) => {
      const next = new Set(previous);
      if (checked) {
        next.add(taskId);
      } else {
        next.delete(taskId);
      }
      return next;
    });
  };

  const toggleAllVisibleSelection = (checked: boolean) => {
    setSelectedTaskIds((previous) => {
      const next = new Set(previous);
      selectableTasks.forEach((task) => {
        if (checked) {
          next.add(task.id);
        } else {
          next.delete(task.id);
        }
      });
      return next;
    });
  };

  const clearBulkSelection = () => {
    setSelectedTaskIds(new Set());
  };

  const handleCreateInvoiceFromSelectedTasks = () => {
    if (!onCreateInvoiceDraft) {
      toast.error('У роли нет права на создание доходного счёта.', 'Платные задачи');
      return;
    }

    if (selectedTasksForBulk.length === 0 || selectedTasksForBulk.some((task) => task.billingStatus !== 'unbilled')) {
      toast.error('Выберите только платные задачи с меткой «Не выставлен счёт».', 'Платные задачи');
      return;
    }

    const first = selectedTasksForBulk[0];
    if (selectedTasksForBulk.some((task) => task.clientId !== first.clientId || task.legalEntityId !== first.legalEntityId)) {
      toast.error('В один счёт можно собрать задачи только одного клиента.', 'Платные задачи');
      return;
    }

    const contractIds = unique(selectedTasksForBulk
      .map((task) => task.billingContractId ? String(task.billingContractId) : '')
      .filter(Boolean));
    const caseIds = Array.from(new Set(selectedTasksForBulk
      .map((task) => task.clientCaseId)
      .filter((id): id is number => typeof id === 'number' && id > 0)));
    const today = toLocalDateKey(new Date());

    onCreateInvoiceDraft({
      mode: 'create',
      presentation: 'modal',
      pageViewMode: false,
      form: {
        number: '',
        date: today,
        dueDate: '',
        amount: '0',
        type: 'Income',
        status: 'Pending',
        workflowStatus: 'Draft',
        clientId: first.clientId ? String(first.clientId) : '',
        legalEntityId: first.legalEntityId ? String(first.legalEntityId) : '',
        expenseClientId: '',
        expenseLegalEntityId: '',
        contractId: contractIds.length === 1 ? contractIds[0] : '',
        clientCaseReference: caseIds.length === 1 ? `Дело: ${first.clientCaseTitle || first.objectName}` : `${caseIds.length} дел(а) по платным задачам`,
        description: `Счёт по платным задачам: ${selectedTasksForBulk.length}`,
        isNotForAccounting: false,
        actReference: '',
        paidDate: '',
        responsibleIds: [],
      },
      lineItems: selectedTasksForBulk.map((task) => ({
        id: `billing-task-${task.id}`,
        name: `${task.title} (задача №${task.id} от ${new Date(task.createdAt).toLocaleDateString('ru-RU')})`,
        quantity: '1',
        unit: 'шт',
        price: '0',
        serviceCategoryId: '',
        serviceId: '',
      })),
      selectedActs: [],
      selectedClientCaseIds: caseIds,
      billingTaskIds: selectedTasksForBulk.map((task) => task.id),
      installmentPlan: null,
    });
  };

  const resetBulkFields = () => {
    setBulkAssigneeUserId('');
    setBulkPriority('');
    setBulkStatus('');
    setBulkDueAt('');
  };

  const handleBulkUpdate = async () => {
    const ids = selectedTasksForBulk.map((task) => task.id);
    if (ids.length === 0) {
      toast.error('Выберите задачи для массового изменения.', 'Журнал задач');
      return;
    }

    const payload: {
      ids: number[];
      assigneeUserId?: number;
      priority?: TaskJournalPriority;
      status?: TaskJournalStatus;
      dueAt?: string;
    } = { ids };

    if (bulkAssigneeUserId) {
      payload.assigneeUserId = Number(bulkAssigneeUserId);
    }
    if (bulkPriority) {
      payload.priority = bulkPriority;
    }
    if (bulkStatus) {
      payload.status = bulkStatus;
    }
    if (bulkDueAt) {
      payload.dueAt = fromLocalInputValue(bulkDueAt);
    }

    if (!payload.assigneeUserId && !payload.priority && !payload.status && !payload.dueAt) {
      toast.error('Выберите хотя бы одно изменение для задач.', 'Журнал задач');
      return;
    }

    setBulkSaving(true);
    try {
      const result = await apiService.bulkUpdateTaskJournalItems(payload);
      const skippedText = result.skipped > 0 ? ` Пропущено: ${result.skipped}.` : '';
      toast.success(`Обновлено задач: ${result.updated}.${skippedText}`, 'Журнал задач');
      clearBulkSelection();
      resetBulkFields();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выполнить массовое изменение задач.'), 'Журнал задач');
    } finally {
      setBulkSaving(false);
    }
  };

  const handleBulkDelete = async () => {
    if (!canDeleteTasks) {
      toast.error('У роли нет права на удаление задач.', 'Журнал задач');
      return;
    }

    const ids = selectedTasksForBulk.map((task) => task.id);
    if (ids.length === 0) {
      toast.error('Выберите задачи для массового удаления.', 'Журнал задач');
      return;
    }

    const accepted = await confirm({
      title: 'Удалить выбранные задачи?',
      description: `Будет удалено задач: ${ids.length}. Действие нельзя отменить.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;

    setBulkDeleting(true);
    try {
      const result = await apiService.bulkDeleteTaskJournalItems(ids);
      const skippedText = result.skipped > 0 ? ` Пропущено: ${result.skipped}.` : '';
      toast.success(`Удалено задач: ${result.updated}.${skippedText}`, 'Журнал задач');
      clearBulkSelection();
      resetBulkFields();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось удалить выбранные задачи.'), 'Журнал задач');
    } finally {
      setBulkDeleting(false);
    }
  };

  const handleSave = async (form: TaskForm, taskId?: number) => {
    if (!taskId && !canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Журнал задач');
      return;
    }

    if (!form.isInternal && !form.clientId && !form.legalEntityId) {
      toast.error('Укажите контрагента задачи.', 'Журнал задач');
      return;
    }
    if (!form.title.trim()) {
      toast.error('Укажите наименование задачи.', 'Журнал задач');
      return;
    }
    if (form.contractorPaymentEnabled) {
      const selectedAssignee = assignees.find((assignee) => assignee.id === form.assigneeUserId);
      if (!selectedAssignee?.isExternalContractor) {
        toast.error('Для оплаты исполнителю выберите пользователя с признаком привлеченного исполнителя.', 'Журнал задач');
        return;
      }
      if (!form.contractorPaymentAmount || Number(form.contractorPaymentAmount) <= 0) {
        toast.error('Укажите сумму оплаты исполнителю.', 'Журнал задач');
        return;
      }
      if (form.contractorPaymentOnResult && form.contractorPaymentDueDate) {
        toast.error('Укажите либо точную дату оплаты, либо оплату по результату. Одновременно эти поля заполнять нельзя.', 'Журнал задач');
        return;
      }
      if (form.contractorPaymentOnResult) {
        const delayDays = Number(form.contractorPaymentResultDelayDays);
        if (form.contractorPaymentResultDelayDays === null || form.contractorPaymentResultDelayDays === undefined || !Number.isFinite(delayDays) || delayDays < 0 || delayDays > 365) {
          toast.error('Укажите количество дней для оплаты по результату от 0 до 365.', 'Журнал задач');
          return;
        }
      } else if (!form.contractorPaymentDueDate) {
        toast.error('Укажите дату оплаты исполнителю или включите оплату по результату.', 'Журнал задач');
        return;
      }
    }

    const normalized: TaskForm = {
      ...form,
      isInternal: Boolean(form.isInternal),
      clientId: form.isInternal ? null : form.clientId,
      legalEntityId: form.isInternal ? null : form.legalEntityId,
      clientCaseId: form.isInternal ? null : form.clientCaseId,
      objectName: form.isInternal ? INTERNAL_TASK_OBJECT_NAME : form.objectName.trim(),
      title: form.title.trim(),
      description: form.description.trim(),
      result: form.result.trim(),
      changeComment: form.changeComment?.trim() || null,
      dueAt: fromLocalInputValue(form.dueAt),
      approvalDueAt: form.approvalDueAt ? fromLocalInputValue(form.approvalDueAt) : null,
      plannedDurationMinutes: normalizeTaskDuration(form.plannedDurationMinutes),
      contractorPaymentEnabled: Boolean(form.contractorPaymentEnabled),
      contractorPaymentAmount: form.contractorPaymentEnabled ? Number(form.contractorPaymentAmount) : null,
      contractorPaymentDueDate: form.contractorPaymentEnabled && form.contractorPaymentDueDate
        ? fromLocalDateInputValue(form.contractorPaymentDueDate)
        : null,
      contractorPaymentOnResult: Boolean(form.contractorPaymentEnabled && form.contractorPaymentOnResult),
      contractorPaymentResultDelayDays: form.contractorPaymentEnabled && form.contractorPaymentOnResult
        ? Number(form.contractorPaymentResultDelayDays)
        : null,
      tags: unique(form.tags),
      reminders: form.reminders.length ? form.reminders : ['atDue'],
      channels: form.channels.length ? form.channels : ['inApp'],
      participants: normalizeParticipants(form, assignees),
    };

    if (normalized.status === 'revision' && !isFutureDateTime(normalized.dueAt)) {
      toast.error('Для статуса «На доработке» укажите новый срок позже текущего времени.', 'Журнал задач');
      return;
    }

    try {
      if (taskId) {
        const updated = await apiService.updateTaskJournalItem(taskId, normalized);
        toast.success('Задача обновлена.', 'Журнал задач');
        if (routeTaskId === taskId) {
          setModal({ mode: 'view', task: updated });
          setHighlightedTaskId(updated.id);
        } else {
          closeTaskModal();
        }
      } else {
        await apiService.createTaskJournalItem(normalized);
        toast.success('Задача создана.', 'Журнал задач');
        closeTaskModal();
      }
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить задачу.'), 'Журнал задач');
    }
  };

  const handleResultSave = async (task: TaskJournalItem, status: TaskJournalStatus, result: string) => {
    const normalizedResult = result.trim();
    if (status === 'done' && requireCompletionResult && !normalizedResult) {
      toast.error(TASK_COMPLETION_RESULT_REQUIRED_MESSAGE, 'Журнал задач');
      return;
    }

    try {
      await apiService.updateTaskJournalStatus(task.id, status, normalizedResult);
      toast.success('Результат по задаче сохранен.', 'Журнал задач');
      closeTaskModal();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить результат по задаче.'), 'Журнал задач');
    }
  };

  const closeCompletionPrompt = () => {
    if (completionPromptSaving) return;
    setCompletionPromptTask(null);
    setCompletionPromptResult('');
    setCompletionPromptError(null);
  };

  const openCompletionPrompt = (task: TaskJournalItem) => {
    setCompletionPromptTask(task);
    setCompletionPromptResult(task.result.trim());
    setCompletionPromptError(null);
  };

  const closeQuickApprovalPrompt = () => {
    if (quickApprovalPromptSaving) return;
    setQuickApprovalPromptTask(null);
    setQuickApprovalPromptResult('');
    setQuickApprovalPromptError(null);
  };

  const openQuickApprovalPrompt = (task: TaskJournalItem) => {
    setQuickApprovalPromptTask(task);
    setQuickApprovalPromptResult(task.result.trim());
    setQuickApprovalPromptError(null);
  };

  const submitCompletionPrompt = async (files: File[]) => {
    if (!completionPromptTask || completionPromptSaving) return;

    const normalizedResult = completionPromptResult.trim();
    if (requireCompletionResult && !normalizedResult) {
      setCompletionPromptError(TASK_COMPLETION_RESULT_REQUIRED_MESSAGE);
      return;
    }

    setCompletionPromptSaving(true);
    try {
      await uploadTaskCompletionFiles(completionPromptTask.id, files);
      await apiService.updateTaskJournalStatus(completionPromptTask.id, 'done', normalizedResult || undefined);
      toast.success('Задача завершена, результат сохранен.', 'Журнал задач');
      setCompletionPromptTask(null);
      setCompletionPromptResult('');
      setCompletionPromptError(null);
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      setCompletionPromptError(extractDisplayErrorMessage(error, 'Не удалось прикрепить файл или завершить задачу.'));
    } finally {
      setCompletionPromptSaving(false);
    }
  };

  const submitQuickApprovalPrompt = async (files: File[]) => {
    if (!quickApprovalPromptTask || quickApprovalPromptSaving) return;

    const normalizedResult = quickApprovalPromptResult.trim();
    if (requireCompletionResult && !normalizedResult) {
      setQuickApprovalPromptError(TASK_APPROVAL_RESULT_REQUIRED_MESSAGE);
      return;
    }

    setQuickApprovalPromptSaving(true);
    try {
      await uploadTaskCompletionFiles(quickApprovalPromptTask.id, files);
      await apiService.updateTaskJournalStatus(quickApprovalPromptTask.id, 'onApproval', normalizedResult || undefined);
      toast.success('Задача отправлена на согласование.', 'Журнал задач');
      setQuickApprovalPromptTask(null);
      setQuickApprovalPromptResult('');
      setQuickApprovalPromptError(null);
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      setQuickApprovalPromptError(extractDisplayErrorMessage(error, 'Не удалось прикрепить файл или отправить задачу на согласование.'));
    } finally {
      setQuickApprovalPromptSaving(false);
    }
  };

  const resetDeadlineChangeDecisionPrompt = () => {
    setDeadlineChangeDecisionPrompt(null);
    setDeadlineChangeDecisionDueAt('');
    setDeadlineChangeDecisionComment('');
    setDeadlineChangeDecisionError(null);
    setDeadlineChangeDecisionSaving(null);
  };

  const openDeadlineChangeDecisionPrompt = (task: TaskJournalItem, request: TaskJournalDeadlineChangeRequest) => {
    setDeadlineChangeDecisionPrompt({ task, request });
    setDeadlineChangeDecisionDueAt(toLocalInputValue(request.requestedDueAt));
    setDeadlineChangeDecisionComment('');
    setDeadlineChangeDecisionError(null);
    setDeadlineChangeDecisionSaving(null);
  };

  const openApprovalDecisionPrompt = (task: TaskJournalItem, participant: TaskJournalParticipant) => {
    setApprovalDecisionPrompt({ task, participant });
    setApprovalDecisionRevisionText('');
    setApprovalDecisionRevisionDueAt('');
    setApprovalDecisionError(null);
    setApprovalDecisionSaving(null);
  };

  const snoozeDeadlineChangeDecisionPrompt = (minutes: number) => {
    if (!deadlineChangeDecisionPrompt || deadlineChangeDecisionSaving) return;

    const { task, request } = deadlineChangeDecisionPrompt;
    snoozeForcedTaskActions([getForcedTaskDeadlineChangeSnoozeKey(task.id, request.id)], minutes);
    resetDeadlineChangeDecisionPrompt();
  };

  const approveDeadlineChangeDecision = async () => {
    if (!deadlineChangeDecisionPrompt || deadlineChangeDecisionSaving) return;

    const { task, request } = deadlineChangeDecisionPrompt;
    const comment = deadlineChangeDecisionComment.trim();
    if (!deadlineChangeDecisionDueAt) {
      setDeadlineChangeDecisionError('Укажите дату и время нового срока.');
      return;
    }

    const approvedDueAt = fromLocalInputValue(deadlineChangeDecisionDueAt);
    setDeadlineChangeDecisionSaving('approve');
    setDeadlineChangeDecisionError(null);
    try {
      await apiService.approveTaskJournalDeadlineChangeRequest(task.id, request.id, {
        approvedDueAt,
        comment: comment || null,
      });
      toast.success('Перенос срока согласован.', 'Согласования');
      processedDeadlineChangeRequestIdsRef.current.add(request.id);
      setForcedDecisionSnoozeRevision((value) => value + 1);
      resetDeadlineChangeDecisionPrompt();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      if (isHttpErrorStatus(error, 404)) {
        await dismissUnavailableDeadlineChangePrompt(task.id, request.id);
        return;
      }
      setDeadlineChangeDecisionError(extractDisplayErrorMessage(error, 'Не удалось согласовать перенос срока.'));
      setDeadlineChangeDecisionSaving(null);
    }
  };

  const rejectDeadlineChangeDecision = async () => {
    if (!deadlineChangeDecisionPrompt || deadlineChangeDecisionSaving) return;

    const comment = deadlineChangeDecisionComment.trim();
    if (!comment) {
      setDeadlineChangeDecisionError('Укажите причину отказа в переносе срока.');
      return;
    }

    const { task, request } = deadlineChangeDecisionPrompt;
    setDeadlineChangeDecisionSaving('reject');
    setDeadlineChangeDecisionError(null);
    try {
      await apiService.rejectTaskJournalDeadlineChangeRequest(task.id, request.id, { comment });
      toast.success('Перенос срока отклонен.', 'Согласования');
      processedDeadlineChangeRequestIdsRef.current.add(request.id);
      setForcedDecisionSnoozeRevision((value) => value + 1);
      resetDeadlineChangeDecisionPrompt();
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      if (isHttpErrorStatus(error, 404)) {
        await dismissUnavailableDeadlineChangePrompt(task.id, request.id);
        return;
      }
      setDeadlineChangeDecisionError(extractDisplayErrorMessage(error, 'Не удалось отклонить перенос срока.'));
      setDeadlineChangeDecisionSaving(null);
    }
  };

  const handleDelete = async (task: TaskJournalItem) => {
    if (!canDeleteTaskForCurrentUser(task, currentUserId, isAdminUser, taskSettings, permissions.tasks)) {
      toast.error('У роли нет права на удаление задач.', 'Журнал задач');
      return false;
    }

    const accepted = await confirm({
      title: 'Удалить задачу?',
      description: `Задача "${task.title}" будет удалена из журнала.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return false;
    try {
      await apiService.deleteTaskJournalItem(task.id);
      toast.success('Задача удалена.', 'Журнал задач');
      if (routeTaskId === task.id) {
        onRouteTaskChange?.(null);
      }
      setHighlightedTaskId((previous) => (previous === task.id ? null : previous));
      await refreshTasks();
      emitTaskChange();
      return true;
    } catch {
      toast.error('Не удалось удалить задачу.', 'Журнал задач');
      return false;
    }
  };

  const handleDuplicateTask = async (task: TaskJournalItem) => {
    if (!canCreateTasks) {
      toast.error('У роли нет права на создание задач.', 'Журнал задач');
      return false;
    }

    const participants = (task.participants ?? []).map((participant) => ({
      ...participant,
      id: undefined,
      approvalStatus: 'pending' as const,
      resolution: '',
      respondedAt: null,
    }));

    closeTaskModal();
    setCreationWizard({
      defaults: taskToPayload(task, {
        title: `${task.title} (копия)`,
        status: 'new',
        result: '',
        participants,
        clientBillingClassification: undefined,
        billingContractId: null,
        billingMode: '',
      }),
    });
    return true;
  };

  const setTaskStatus = async (task: TaskJournalItem, status: TaskJournalStatus) => {
    const shouldSendToApproval = status === 'done'
      && hasPendingApprovers(task)
      && !(isAdminUser && taskSettings.allowAdminCompleteAnyTask);
    const nextStatus = shouldSendToApproval ? 'onApproval' : status;

    if (nextStatus === 'done' && requireCompletionResult && !task.result.trim()) {
      openCompletionPrompt(task);
      return;
    }

    if ((nextStatus === 'onApproval' || nextStatus === 'overdueApproval') && requireCompletionResult && !task.result.trim()) {
      openQuickApprovalPrompt(task);
      return;
    }

    try {
      const revisionDueAt = nextStatus === 'revision' && !isFutureDateTime(task.dueAt)
        ? fromLocalInputValue(getDefaultRevisionDueAtInput(task))
        : undefined;
      if (revisionDueAt) {
        await apiService.updateTaskJournalStatus(task.id, nextStatus, undefined, revisionDueAt);
      } else {
        await apiService.updateTaskJournalStatus(task.id, nextStatus, undefined);
      }
      if (shouldSendToApproval) {
        toast.success('Задача отправлена на согласование.', 'Журнал задач');
      }
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось обновить статус задачи.'), 'Журнал задач');
    }
  };

  const handleQuickTaskCompletion = (task: TaskJournalItem) => {
    if (isTaskAwaitingApproval(task)) {
      const participant = getReviewableApprovalParticipant(task, currentUserId);
      if (taskSettings.approvalQuickActionMode === 'completionButton' && participant) {
        openApprovalDecisionPrompt(task, participant);
        return;
      }

      const currentApprover = getCurrentPendingApprover(task);
      const currentApproverName = currentApprover ? getParticipantDisplayName(currentApprover) : '';
      toast.error(
        currentApproverName
          ? `Задача находится на согласовании. Решение ожидается от: ${currentApproverName}.`
          : 'Задача находится на согласовании. Используйте действие согласования.',
        'Согласование задачи',
      );
      return;
    }

    void setTaskStatus(task, 'done');
  };

  const toggleTaskControl = async (task: TaskJournalItem) => {
    try {
      await apiService.updateTaskJournalControl(task.id, !task.isUnderControl);
      await refreshTasks();
      emitTaskChange();
      toast.success(task.isUnderControl ? 'Задача снята с контроля.' : 'Задача поставлена на контроль.', 'Журнал задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось обновить контроль задачи.'), 'Журнал задач');
    }
  };

  const handleApprovalDecision = async (
    taskId: number,
    decision: 'approved' | 'revisionRequested',
    resolution?: string,
    participantId?: number,
    finalAction?: TaskApprovalFinalAction,
    revisionDueAt?: string | null,
  ) => {
    try {
      if (decision === 'revisionRequested') {
        await apiService.updateTaskJournalApproval(taskId, decision, resolution, participantId, finalAction, revisionDueAt ?? undefined);
      } else {
        await apiService.updateTaskJournalApproval(taskId, decision, resolution, participantId, finalAction);
      }
      const successMessage = finalAction === 'complete'
        ? 'Задача согласована и завершена.'
        : decision === 'approved'
          ? 'Решение по согласованию сохранено.'
          : 'Задача отправлена на доработку.';
      toast.success(successMessage, 'Журнал задач');
      closeTaskModal();
      await refreshTasks();
      emitTaskChange();
      return true;
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить решение по согласованию.'), 'Журнал задач');
      return false;
    }
  };

  const resetApprovalDecisionPrompt = () => {
    setApprovalDecisionPrompt(null);
    setApprovalDecisionRevisionText('');
    setApprovalDecisionRevisionDueAt('');
    setApprovalDecisionError(null);
    setApprovalDecisionSaving(null);
  };

  const snoozeApprovalDecisionPrompt = (minutes: number) => {
    if (!approvalDecisionPrompt || approvalDecisionSaving) return;

    snoozeForcedTaskActions([getForcedTaskApprovalSnoozeKey(approvalDecisionPrompt.task.id)], minutes);
    resetApprovalDecisionPrompt();
  };

  const approvePromptDecision = async (finalAction?: TaskApprovalFinalAction) => {
    if (!approvalDecisionPrompt || approvalDecisionSaving) return;

    const savingState =
      finalAction === 'complete'
        ? 'approveComplete'
        : finalAction === 'returnToAssignee'
          ? 'approveReturn'
          : 'approve';

    setApprovalDecisionSaving(savingState);
    setApprovalDecisionError(null);
    const { task, participant } = approvalDecisionPrompt;
    const saved = await handleApprovalDecision(task.id, 'approved', task.result.trim(), participant.id, finalAction);
    if (saved) {
      resetApprovalDecisionPrompt();
    } else {
      setApprovalDecisionError('Не удалось сохранить решение по согласованию. Проверьте данные и попробуйте еще раз.');
      setApprovalDecisionSaving(null);
    }
  };

  const returnPromptDecisionToRevision = async () => {
    if (!approvalDecisionPrompt || approvalDecisionSaving) return;

    const text = approvalDecisionRevisionText.trim();
    if (!text) {
      setApprovalDecisionError('Напишите, что нужно исправить перед повторным согласованием.');
      return;
    }

    const revisionDueAt = approvalDecisionRevisionDueAt ? fromLocalInputValue(approvalDecisionRevisionDueAt) : null;
    const { task, participant } = approvalDecisionPrompt;

    setApprovalDecisionSaving('revision');
    setApprovalDecisionError(null);
    const saved = await handleApprovalDecision(task.id, 'revisionRequested', text, participant.id, undefined, revisionDueAt);
    if (saved) {
      resetApprovalDecisionPrompt();
    } else {
      setApprovalDecisionError('Не удалось вернуть задачу на доработку. Проверьте данные и попробуйте еще раз.');
      setApprovalDecisionSaving(null);
    }
  };

  const handleCalendarTaskMove = async (task: TaskJournalItem, targetDate: Date) => {
    if (isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, permissions.tasks)) {
      toast.error('Исполнитель может запросить перенос срока в карточке задачи.', 'Календарь задач');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

    if (isSameTaskDay(task.dueAt, targetDate)) {
      setCalendarSelectedDate(targetDate);
      return;
    }

    if (!task.clientId && !task.legalEntityId) {
      toast.error('У задачи не указан контрагент. Откройте задачу и заполните контрагента перед переносом.', 'Календарь задач');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

    const nextDueAt = moveDueAtToDate(task.dueAt, targetDate);
    const scheduleConflict = findTaskScheduleConflict(task, new Date(nextDueAt), tasks);
    if (calendarConflictPolicy === 'block' && scheduleConflict) {
      toast.error(`Перенос заблокирован: задача пересекается с "${scheduleConflict.title}".`, 'Календарь задач');
      return;
    }

    setCalendarMovingTaskId(task.id);
    try {
      await apiService.updateTaskJournalItem(task.id, taskToPayload(task, { dueAt: nextDueAt }));
      setCalendarSelectedDate(targetDate);
      toast.success(
        scheduleConflict
          ? `Задача перенесена, но пересекается с "${scheduleConflict.title}".`
          : 'Задача перенесена в календаре.',
        'Календарь задач',
      );
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось перенести задачу.'), 'Календарь задач');
    } finally {
      setCalendarMovingTaskId(null);
    }
  };

  const handleTimelineTaskMove = async (task: TaskJournalItem, targetDueAt: Date) => {
    if (isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, permissions.tasks)) {
      toast.error('Исполнитель может запросить перенос срока в карточке задачи.', 'Календарь нагрузки');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

    if (!task.clientId && !task.legalEntityId) {
      toast.error('У задачи не указан контрагент. Откройте задачу и заполните контрагента перед переносом.', 'Календарь нагрузки');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

    const currentDueAt = new Date(task.dueAt);
    if (!Number.isNaN(currentDueAt.getTime()) && currentDueAt.getTime() === targetDueAt.getTime()) {
      return;
    }

    const scheduleConflict = findTaskScheduleConflict(task, targetDueAt, tasks);
    if (calendarConflictPolicy === 'block' && scheduleConflict) {
      toast.error(`Перенос заблокирован: задача пересекается с "${scheduleConflict.title}".`, 'Календарь нагрузки');
      return;
    }

    setCalendarMovingTaskId(task.id);
    try {
      await apiService.updateTaskJournalItem(task.id, taskToPayload(task, { dueAt: targetDueAt.toISOString() }));
      setCalendarSelectedDate(getCalendarDateForTaskValue(targetDueAt));
      toast.success(
        scheduleConflict
          ? `Задача перенесена, но пересекается с "${scheduleConflict.title}".`
          : 'Задача перенесена на шкале нагрузки.',
        'Календарь нагрузки',
      );
      await refreshTasks();
      emitTaskChange();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось перенести задачу по шкале.'), 'Календарь нагрузки');
    } finally {
      setCalendarMovingTaskId(null);
    }
  };

  const handleCalendarTaskResize = async (task: TaskJournalItem, deltaMinutes: -15 | 15) => {
    if (isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, permissions.tasks)) {
      toast.error('Исполнитель не может менять длительность задачи напрямую.', 'Календарь задач');
      openTaskEditor(task, { syncRoute: true });
      return;
    }

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
    if (!canExportTasks) {
      toast.error('У роли нет права на выгрузку журнала задач.', 'Журнал задач');
      return;
    }

    const header = ['Контрагент', 'Дело', 'Наименование задачи', 'Исполнитель', 'Поручил', 'Срок', 'Длительность', 'Тип', 'Приоритет', 'Статус', 'Результат'];
    const rows = filteredTasks.map((task) => [
      getTaskClientName(task),
      getTaskCaseTitle(task),
      task.title,
      task.assigneeName,
      getTaskAuthorName(task),
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
    if (!canExportTasks) {
      toast.error('У роли нет права на выгрузку календаря задач.', 'Календарь задач');
      return;
    }

    try {
      const response = await apiService.downloadTaskJournalCalendar({
        clientCaseId: caseFilter !== 'all' ? Number(caseFilter) : undefined,
        clientId: contextFilter?.clientId ?? selectedFilterClientId,
        legalEntityId: contextFilter?.legalEntityId ?? selectedFilterLegalEntityId,
        reminderMinutes: icsReminderMinutes,
      });
      downloadBlob(response.fileName, response.blob);
      toast.success('Календарь задач выгружен в ICS.', 'Календарь задач');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выгрузить календарь задач.'), 'Календарь задач');
    }
  };

  const handleCaseCalendarExport = async () => {
    if (!canExportCases) {
      toast.error('У роли нет права на выгрузку календаря дел.', 'Календарь дел');
      return;
    }

    if (!canViewCases) {
      toast.error('У роли нет доступа к календарю дел.', 'Календарь дел');
      return;
    }

    setCaseCalendarExporting(true);
    try {
      const response = await apiService.downloadCaseCalendarIcs({
        ...buildCaseCalendarParams(),
        reminderMinutes: icsReminderMinutes,
      });
      downloadBlob(response.fileName, response.blob);
      toast.success('Календарь дел выгружен в ICS.', 'Календарь дел');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выгрузить календарь дел.'), 'Календарь дел');
    } finally {
      setCaseCalendarExporting(false);
    }
  };

  const buildPlanningReportParams = (format: TaskPlanningReportFormat) => {
    const nowDate = getCurrentCalendarDate();
    let from = dateRangeBounds.from?.toISOString();
    let to = dateRangeBounds.to?.toISOString();
    let reportStatus: TaskJournalStatus | 'active' | undefined =
      statusFilter === 'all' || statusFilter === 'overdue' ? undefined : statusFilter;
    let reportPriority: TaskJournalPriority | 'highPriority' | undefined = priorityFilter === 'all' ? undefined : priorityFilter;

    if (!from && !to && metricFilter === 'none' && periodMode !== 'all' && (viewMode === 'list' || viewMode === 'board')) {
      const { periodStart, periodEnd } = getTaskPeriodBounds(nowDate, periodMode);
      from = periodStart.toISOString();
      to = periodEnd.toISOString();
    }

    if (statusFilter === 'overdue') {
      reportStatus = 'active';
      to = nowDate.toISOString();
    }

    if (metricFilter === 'active') {
      reportStatus = 'active';
    } else if (metricFilter === 'overdue') {
      reportStatus = 'active';
      to = nowDate.toISOString();
    } else if (metricFilter === 'today') {
      reportStatus = 'active';
      from = parseDateFilterValue(toLocalDateKey(nowDate), 'start')?.toISOString() ?? startOfDay(nowDate).toISOString();
      to = parseDateFilterValue(toLocalDateKey(nowDate), 'end')?.toISOString() ?? endOfDay(nowDate).toISOString();
    } else if (metricFilter === 'highPriority') {
      reportStatus = 'active';
      reportPriority = 'highPriority';
    }

    const reportAssigneeUserId =
      quickFilter === 'assignedToMe' && currentUserId
        ? currentUserId
        : assigneeFilter !== 'all' && /^\d+$/.test(assigneeFilter)
          ? Number(assigneeFilter)
          : undefined;

    return {
      format,
      search: search.trim() || undefined,
      status: reportStatus,
      type: typeFilter === 'all' ? undefined : typeFilter,
      priority: reportPriority,
      assigneeUserId: reportAssigneeUserId,
      clientCaseId: caseFilter !== 'all' ? Number(caseFilter) : undefined,
      clientId: contextFilter?.clientId ?? selectedFilterClientId,
      legalEntityId: contextFilter?.legalEntityId ?? selectedFilterLegalEntityId,
      from,
      to,
    };
  };

  const handlePlanningReportExport = async (format: TaskPlanningReportFormat) => {
    if (!canExportTasks) {
      toast.error('У роли нет права на выгрузку отчета по задачам.', 'Отчет по задачам');
      return;
    }

    const label = format === 'word' ? 'Word' : format.toUpperCase();
    try {
      const response = await apiService.downloadTaskPlanningReport(buildPlanningReportParams(format));
      downloadBlob(response.fileName, response.blob);
      toast.success(`Отчет по задачам выгружен в ${label}.`, 'Отчет по задачам');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось выгрузить отчет по задачам.'), 'Отчет по задачам');
    }
  };

  const handleOfflineCalendarTrustedDeviceChange = async (trustedDevice: boolean) => {
    if (!currentUserId || offlineCalendarSnapshot) return;

    setOfflineCalendarTrustedDeviceState(trustedDevice);
    try {
      await setOfflineCalendarTrustedDevice(currentUserId, trustedDevice);
      await saveOfflineCalendarSnapshot(currentUserId, tasks, trustedDevice);
      toast.success(
        trustedDevice
          ? 'В офлайн-снимке будут видны названия задач и исполнителей.'
          : 'Офлайн-снимок заменён на обезличенный.',
        'Офлайн-календарь',
      );
    } catch {
      setOfflineCalendarTrustedDeviceState(!trustedDevice);
      toast.error('Не удалось изменить настройку локального календаря.', 'Офлайн-календарь');
    }
  };

  const contextTitle = contextFilter?.title?.trim();
  const pageTitle = isApprovalsMode ? 'Согласования' : underControlOnly ? 'На контроле' : contextTitle ? `Задачи: ${contextTitle}` : 'Журнал задач';
  const pageDescription = isApprovalsMode
    ? 'Задачи, которые находятся на согласовании и требуют решения текущего согласующего. Они остаются в общем журнале задач.'
    : underControlOnly
      ? 'Задачи, поставленные на контроль руководством и отслеживаемые через движение, комментарии и изменения статусов.'
    : contextTitle
      ? 'Задачи, связанные с этим клиентом: исполнитель, срок, результат и напоминания в одном списке.'
    : 'Задачи по контрагентам, делам, платежам, звонкам и документам: исполнитель, срок, результат и напоминания в одном рабочем журнале.';
  const isRouteTaskPage = !isApprovalsMode && Boolean(routeTaskId && routeTaskId > 0);
  const routeTaskState = isRouteTaskPage && modal?.task?.id === routeTaskId ? modal : null;
  const TaskCardComponent = taskSettings.taskCardWindowMode === 'modern' ? ModernTaskModal : TaskModal;

  if (isRouteTaskPage) {
    return (
      <TaskStickerStylesProvider styles={taskSettings.stickerStyles}>
        <div className="min-h-screen bg-slate-50">
        {routeTaskState ? (
          <TaskCardComponent
            key={`page-${routeTaskState.task!.id}-${routeTaskState.mode}`}
            presentation="page"
            state={routeTaskState}
            tasks={tasks}
            assignees={assignees}
            clients={clients}
            legalEntities={legalEntities}
            counterpartyOptions={counterpartyOptions}
            currentUserId={currentUserId}
            currentUserName={currentUserName}
            isAdminUser={isAdminUser}
            taskSettings={taskSettings}
            notificationDefaults={taskNotificationDefaults}
            canSwitchToEdit={canEditModalTask}
            canCompleteTask={canCompleteModalTask}
            canDeleteTask={canDeleteModalTask}
            canDuplicateTask={canCreateTasks}
            canSendFilesToYandexDisk={canSendTaskFilesToYandexDisk}
            onClose={closeTaskModal}
            onEnableEdit={() => setModal((current) => current?.task ? { ...current, mode: 'edit' } : current)}
            onSave={handleSave}
            onResultSave={handleResultSave}
            onApprovalDecision={handleApprovalDecision}
            onReviewDeadlineChange={(task, request) => {
              closeTaskModal();
              openDeadlineChangeDecisionPrompt(task, request);
            }}
            onReviewApproval={(task, participant) => {
              closeTaskModal();
              openApprovalDecisionPrompt(task, participant);
            }}
            onDelete={handleDelete}
            onDuplicate={handleDuplicateTask}
          />
        ) : (
          <div className="mx-auto max-w-5xl p-4 sm:p-6 lg:p-8">
            <div className="rounded-2xl border border-slate-200 bg-white p-8 text-center shadow-sm">
              <div className="mx-auto inline-block h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
              <p className="mt-4 text-sm text-slate-600">Загружаем карточку задачи...</p>
            </div>
          </div>
        )}
        </div>
      </TaskStickerStylesProvider>
    );
  }

  return (
    <TaskStickerStylesProvider styles={taskSettings.stickerStyles}>
      <div className={embedded ? 'bg-transparent' : 'min-h-screen bg-slate-50'}>
      <div data-testid="task-journal-shell" className={embedded ? 'w-full min-w-0' : 'w-full min-w-0 p-3 sm:p-4 lg:p-4'}>
        <section className="mb-6">
          <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                {contextTitle ? 'Задачи клиента' : 'Рабочий контроль'}
              </div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">{pageTitle}</h1>
              <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
                {pageDescription}
              </p>
            </div>

            <div className="flex min-w-0 max-w-full flex-wrap gap-2">
              {canExportTasks ? (
                <>
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
                    onClick={() => void handlePlanningReportExport('word')}
                    className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                  >
                    <FileText className="h-4 w-4" />
                    Word
                  </button>
                  <button
                    type="button"
                    onClick={() => void handlePlanningReportExport('pdf')}
                    className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                  >
                    <FileText className="h-4 w-4" />
                    PDF
                  </button>
                  <button
                    type="button"
                    onClick={() => void handlePlanningReportExport('html')}
                    className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                  >
                    <Download className="h-4 w-4" />
                    HTML
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleCalendarExport()}
                    className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                  >
                    <CalendarDays className="h-4 w-4" />
                    ICS
                  </button>
                  <label className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700">
                    <Bell className="h-4 w-4 text-slate-500" />
                    <SystemSelect
                      value={icsReminderPreset}
                      onChange={(event) => setIcsReminderPreset(event.target.value as IcsReminderPreset)}
                      data-testid="task-ics-reminder-preset"
                      className="bg-transparent text-sm font-semibold text-slate-700 outline-none"
                    >
                      {ICS_REMINDER_PRESET_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>{option.label}</option>
                      ))}
                    </SystemSelect>
                  </label>
                </>
              ) : null}
              {!isApprovalsMode && canCreateTasks ? (
                <>
                  <button
                    type="button"
                    onClick={() => setTemplatesPanelOpen((current) => !current)}
                    aria-pressed={templatesPanelOpen}
                    className={`inline-flex items-center gap-2 rounded-xl border px-4 py-2.5 text-sm font-semibold transition ${
                      templatesPanelOpen
                        ? 'border-blue-200 bg-blue-50 text-blue-700 hover:bg-blue-100'
                        : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                    }`}
                  >
                    <ClipboardList className="h-4 w-4" />
                    Шаблоны
                  </button>
                  <button
                    type="button"
                    onClick={openTaskCreate}
                    className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800"
                  >
                    <Plus className="h-4 w-4" />
                    Добавить задачу
                  </button>
                </>
              ) : null}
            </div>
          </div>
        </section>

        {offlineCalendarEnabled && shouldUseOfflineCalendar ? (
          <section className={`mb-3 flex flex-col gap-3 rounded-xl border px-3 py-3 sm:flex-row sm:items-center sm:justify-between ${
            offlineCalendarSnapshot
              ? 'border-amber-300 bg-amber-50 text-amber-950'
              : 'border-sky-200 bg-sky-50 text-sky-950'
          }`}>
            <div className="flex min-w-0 items-start gap-2">
              <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0" />
              <div>
                <div className="text-sm font-semibold">
                  {offlineCalendarSnapshot ? 'Офлайн-календарь · только просмотр' : 'Офлайн-календарь подготовлен'}
                </div>
                <div className="mt-0.5 text-xs leading-5 opacity-80">
                  {offlineCalendarSnapshot
                    ? `Данные сохранены ${new Date(offlineCalendarSnapshot.syncedAt).toLocaleString('ru-RU')}. Срок действия — до ${new Date(offlineCalendarSnapshot.expiresAt).toLocaleString('ru-RU')}.`
                    : 'Хранится не более 1000 событий на 7 дней. Документы, комментарии и результаты задач не сохраняются.'}
                </div>
              </div>
            </div>
            {!offlineCalendarSnapshot ? (
              <label className="inline-flex shrink-0 items-center gap-2 text-xs font-semibold">
                <SystemCheckbox
                  role="switch"
                  checked={offlineCalendarTrustedDevice}
                  onChange={(event) => void handleOfflineCalendarTrustedDeviceChange(event.target.checked)}
                  aria-label="Сохранять названия задач на доверенном устройстве"
                />
                Доверенное устройство: сохранять названия
              </label>
            ) : (
              <span className="shrink-0 rounded-full bg-white/70 px-2.5 py-1 text-xs font-semibold">
                {offlineCalendarSnapshot.trustedDevice ? 'Названия сохранены' : 'Данные обезличены'}
              </span>
            )}
          </section>
        ) : null}

        {templatesPanelOpen && !isApprovalsMode ? (
          <TaskTemplatesPanel
            assignees={assigneeOptions}
            canCreate={canCreateTasks}
            canEdit={canEditTasks}
            canDelete={canDeleteTasks}
            onUseTemplate={openTaskCreateFromTemplate}
          />
        ) : null}

        <section className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <MetricCard
            label="Активные"
            value={stats.active}
            icon={ClipboardList}
            tone="blue"
            active={metricFilter === 'active'}
            onClick={() => handleMetricFilterClick('active')}
          />
          <MetricCard
            label="Просрочены"
            value={stats.overdue}
            icon={TimerReset}
            tone="rose"
            active={metricFilter === 'overdue'}
            onClick={() => handleMetricFilterClick('overdue')}
          />
          <MetricCard
            label="Сегодня"
            value={stats.today}
            icon={CalendarClock}
            tone="emerald"
            active={metricFilter === 'today'}
            onClick={() => handleMetricFilterClick('today')}
          />
          <MetricCard
            label="Высокий приоритет"
            value={stats.high}
            icon={AlarmClock}
            tone="amber"
            active={metricFilter === 'highPriority'}
            onClick={() => handleMetricFilterClick('highPriority')}
          />
        </section>

        <section className="mb-4 flex min-w-0 max-w-full flex-wrap items-center justify-between gap-3">
          <div className="flex max-w-full flex-wrap overflow-x-auto rounded-xl bg-slate-100 p-1" data-testid="task-quick-filter-row">
            {QUICK_FILTER_OPTIONS.map(({ value, label, icon: Icon }) => (
              <button
                key={value}
                type="button"
                data-testid={`task-quick-filter-${value}`}
                aria-pressed={quickFilter === value}
                onClick={() => handleQuickFilterChange(value)}
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${
                  quickFilter === value
                    ? 'bg-white text-slate-900 shadow-sm'
                    : 'text-slate-500 hover:text-slate-800'
                }`}
              >
                <Icon className="h-4 w-4" />
                {label}
              </button>
            ))}
          </div>

          <div className="flex min-w-0 max-w-full flex-wrap items-center justify-end gap-2">
            <SystemSelect
              value={selectedSavedViewId}
              onChange={(event) => handleSavedViewSelect(event.target.value)}
              aria-label="Сохраненные представления задач"
              data-testid="task-saved-view-select"
              className="h-10 min-w-[220px] max-w-full rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-600 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            >
              <option value="">Сохраненные представления</option>
              {savedViews.map((view) => (
                <option key={view.id} value={view.id}>{view.name}</option>
              ))}
            </SystemSelect>

            <button
              type="button"
              onClick={handleSaveCurrentView}
              data-testid="task-save-view"
              className="inline-flex h-10 items-center gap-2 rounded-xl border border-blue-200 bg-blue-50 px-3 text-sm font-semibold text-blue-700 transition hover:bg-blue-100"
            >
              <Bookmark className="h-4 w-4" />
              Сохранить вид
            </button>

            <button
              type="button"
              onClick={handleDeleteCurrentView}
              disabled={!selectedSavedViewId}
              data-testid="task-delete-view"
              className="inline-flex h-10 items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Trash2 className="h-4 w-4" />
              Удалить
            </button>

            {metricFilter !== 'none' ? (
              <button
                type="button"
                onClick={() => setMetricFilter('none')}
                className="inline-flex h-10 items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
              >
                <X className="h-4 w-4" />
                {METRIC_FILTER_LABELS[metricFilter]}
              </button>
            ) : null}
          </div>
        </section>

        <section className="mb-6 min-w-0 max-w-full space-y-3" data-testid="task-filters-panel">
          <div
            className="grid gap-3 md:grid-cols-2 xl:grid-cols-[minmax(240px,1fr)_repeat(5,minmax(140px,190px))]"
            data-testid="task-main-filter-row"
          >
            <div
              className="relative"
              onBlur={(event) => {
                if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
                  setCounterpartySearchOpen(false);
                }
              }}
              data-testid="task-counterparty-search"
              data-selected-value={counterpartyFilter}
            >
              <div className="flex h-11 min-w-0 items-center gap-1.5 rounded-xl border border-slate-200 bg-white pl-3 pr-2 transition focus-within:border-blue-400 focus-within:ring-2 focus-within:ring-blue-100">
                <Search className="h-4 w-4 shrink-0 text-slate-400" />
                {selectedFilterCounterparty ? (
                  <span
                    className="inline-flex min-w-0 max-w-[55%] shrink-0 items-center gap-1 rounded-lg bg-blue-50 px-2 py-1 text-xs font-semibold text-blue-700"
                    title={selectedFilterCounterparty.label}
                  >
                    <span className="truncate">{selectedFilterCounterparty.label}</span>
                    <button
                      type="button"
                      onClick={() => {
                        setCounterpartyFilter('all');
                        setCaseFilter('all');
                      }}
                      className="shrink-0 rounded p-0.5 text-blue-400 transition hover:bg-blue-100 hover:text-blue-700"
                      aria-label={`Очистить контрагента ${selectedFilterCounterparty.label}`}
                    >
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </span>
                ) : null}
                <input
                  value={search}
                  onFocus={() => setCounterpartySearchOpen(true)}
                  onChange={(event) => {
                    setSearch(event.target.value);
                    setCounterpartySearchOpen(true);
                  }}
                  onKeyDown={(event) => {
                    if (event.key === 'Escape') {
                      setCounterpartySearchOpen(false);
                    } else if (event.key === 'Enter' && counterpartySearchOptions[0]) {
                      event.preventDefault();
                      setCounterpartyFilter(counterpartySearchOptions[0].value);
                      setCaseFilter('all');
                      setSearch('');
                      setCounterpartySearchOpen(false);
                    }
                  }}
                  placeholder={selectedFilterCounterparty
                    ? 'Поиск по делу или задаче'
                    : 'Поиск по контрагенту, делу или наименованию задачи'}
                  className="h-full min-w-0 flex-1 bg-transparent text-sm text-slate-700 outline-none placeholder:text-slate-400"
                  aria-label="Поиск по контрагенту, делу или наименованию задачи"
                  aria-autocomplete="list"
                  aria-expanded={counterpartySearchOpen && counterpartySearchOptions.length > 0}
                />
              </div>
              {counterpartySearchOpen && counterpartySearchOptions.length > 0 ? (
                <div
                  role="listbox"
                  aria-label="Найденные контрагенты"
                  className="absolute left-0 right-0 top-full z-40 mt-2 max-h-72 overflow-y-auto rounded-xl border border-slate-200 bg-white p-1.5 shadow-2xl"
                >
                  {counterpartySearchOptions.map((counterparty) => (
                    <button
                      key={counterparty.value}
                      type="button"
                      role="option"
                      aria-selected={counterparty.value === counterpartyFilter}
                      onClick={() => {
                        setCounterpartyFilter(counterparty.value);
                        setCaseFilter('all');
                        setSearch('');
                        setCounterpartySearchOpen(false);
                      }}
                      className="flex w-full items-center justify-between gap-3 rounded-lg px-3 py-2.5 text-left text-sm font-semibold text-slate-800 transition hover:bg-blue-50 focus:bg-blue-50 focus:outline-none"
                    >
                      <span className="truncate">{counterparty.label}</span>
                      {counterparty.value === counterpartyFilter ? <CheckCircle2 className="h-4 w-4 shrink-0 text-blue-600" /> : null}
                    </button>
                  ))}
                </div>
              ) : null}
            </div>

            <SystemSelect
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value as TaskStatusFilter)}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Статус"
            >
              <option value="active">Активные без закрытых</option>
              <option value="all">Все статусы</option>
              <option value="overdue">Просроченные</option>
              {STATUS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>

            <SystemSelect
              value={caseFilter}
              onChange={(event) => setCaseFilter(event.target.value)}
              disabled={counterpartyFilter === 'all'}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Фильтр по делу"
              data-testid="task-case-filter"
            >
              <option value="all">{counterpartyFilter === 'all' ? 'Сначала выберите контрагента' : 'Все дела контрагента'}</option>
              {caseFilterOptions.map((clientCase) => (
                <option key={clientCase.id} value={String(clientCase.id)}>{clientCase.label}</option>
              ))}
            </SystemSelect>

            <SystemSelect
              value={assigneeFilter}
              onChange={(event) => setAssigneeFilter(event.target.value)}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Исполнитель"
            >
              <option value="all">Все исполнители</option>
              {assigneeOptions.map((assignee) => (
                <option key={assignee.id} value={String(assignee.id)}>{assignee.name}</option>
              ))}
            </SystemSelect>

            <SystemSelect
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value as TaskJournalType | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Тип задачи"
            >
              <option value="all">Все типы</option>
              {TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>

            <SystemSelect
              value={priorityFilter}
              onChange={(event) => setPriorityFilter(event.target.value as TaskJournalPriority | 'all')}
              className="h-11 rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Приоритет"
              data-testid="task-priority-filter"
            >
              <option value="all">Все приоритеты</option>
              {PRIORITY_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>
          </div>

          <div className="flex flex-col gap-3 xl:flex-row xl:items-center xl:justify-between" data-testid="task-date-and-view-row">
            <div className="grid w-full gap-3 sm:grid-cols-2 xl:w-[380px] xl:shrink-0" data-testid="task-date-filter-row">
              <label className="relative block">
              <span className="pointer-events-none absolute left-3 top-1 text-[10px] font-semibold text-slate-400">Дата с</span>
              <SystemDateInput
                type="date"
                value={dateFromFilter}
                onChange={(event) => setDateFromFilter(event.target.value)}
                aria-label="Дата исполнения с"
                data-testid="task-date-from-filter"
                className="h-11 w-full rounded-xl border border-slate-200 bg-white px-3 pt-4 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
              </label>

              <label className="relative block">
              <span className="pointer-events-none absolute left-3 top-1 text-[10px] font-semibold text-slate-400">Дата по</span>
              <SystemDateInput
                type="date"
                value={dateToFilter}
                onChange={(event) => setDateToFilter(event.target.value)}
                aria-label="Дата исполнения по"
                data-testid="task-date-to-filter"
                className="h-11 w-full rounded-xl border border-slate-200 bg-white px-3 pt-4 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
              </label>
            </div>

            <div className="flex min-w-0 max-w-full flex-wrap justify-start gap-2 xl:justify-end" data-testid="task-view-controls">
            {viewMode !== 'timeline' ? (
              <div className="flex max-w-full overflow-x-auto rounded-xl bg-slate-100 p-1" data-testid="task-period-control">
                {(['day', 'week', 'month', 'all'] as PeriodMode[]).map((mode) => (
                  <button
                    key={mode}
                    type="button"
                    onClick={() => setPeriodMode(mode)}
                    aria-pressed={periodMode === mode}
                    data-testid={`task-period-${mode}`}
                    className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${periodMode === mode ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                  >
                    {mode === 'day' ? 'День' : mode === 'week' ? 'Неделя' : mode === 'month' ? 'Месяц' : 'Весь период'}
                  </button>
                ))}
              </div>
            ) : null}

            <div className="flex max-w-full overflow-x-auto rounded-xl bg-slate-100 p-1" aria-label="Цветовая схема календаря задач">
              <span className="inline-flex items-center gap-1 px-2 text-sm font-semibold text-slate-500">
                <Palette className="h-4 w-4" />
                Цвет
              </span>
              {TASK_CALENDAR_COLOR_MODE_OPTIONS.map((option) => (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => setCalendarColorMode(option.value)}
                  aria-pressed={calendarColorMode === option.value}
                  data-testid={`task-color-mode-${option.value}`}
                  className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${calendarColorMode === option.value ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                >
                  {option.label}
                </button>
              ))}
            </div>

            <div className="flex max-w-full overflow-x-auto rounded-xl bg-slate-100 p-1" aria-label="Стратегия конфликтов календаря задач">
              <span className="inline-flex items-center gap-1 px-2 text-sm font-semibold text-slate-500">
                <ShieldAlert className="h-4 w-4" />
                Конфликты
              </span>
              {TASK_CALENDAR_CONFLICT_POLICY_OPTIONS.map((option) => (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => setCalendarConflictPolicy(option.value)}
                  aria-pressed={calendarConflictPolicy === option.value}
                  data-testid={`task-conflict-policy-${option.value}`}
                  className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${calendarConflictPolicy === option.value ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                >
                  {option.label}
                </button>
              ))}
            </div>

            <div className="flex max-w-full overflow-x-auto rounded-xl bg-slate-100 p-1" aria-label="Порог предупреждений о загрузке календаря задач">
              <span className="inline-flex items-center gap-1 px-2 text-sm font-semibold text-slate-500">
                <AlarmClock className="h-4 w-4" />
                Загрузка
              </span>
              {TASK_CALENDAR_LOAD_WARNING_MODE_OPTIONS.map((option) => (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => setCalendarLoadWarningMode(option.value)}
                  aria-pressed={calendarLoadWarningMode === option.value}
                  data-testid={`task-load-warning-${option.value}`}
                  className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${calendarLoadWarningMode === option.value ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
                >
                  {option.label}
                </button>
              ))}
            </div>

            <div className="flex max-w-full overflow-x-auto rounded-xl bg-slate-100 p-1" data-testid="task-view-mode-control">
              <button
                type="button"
                onClick={() => setViewMode('list')}
                aria-pressed={viewMode === 'list'}
                data-testid="task-view-list"
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'list' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <ListFilter className="h-4 w-4" />
                Список
              </button>
              <button
                type="button"
                onClick={() => setViewMode('board')}
                aria-pressed={viewMode === 'board'}
                data-testid="task-view-board"
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'board' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <LayoutGrid className="h-4 w-4" />
                Доска
              </button>
              <button
                type="button"
                onClick={() => setViewMode('calendar')}
                aria-pressed={viewMode === 'calendar'}
                data-testid="task-view-calendar"
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'calendar' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <CalendarDays className="h-4 w-4" />
                Календарь
              </button>
              <button
                type="button"
                onClick={() => setViewMode('timeline')}
                aria-pressed={viewMode === 'timeline'}
                data-testid="task-view-timeline"
                className={`inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold transition ${viewMode === 'timeline' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-500 hover:text-slate-800'}`}
              >
                <CalendarClock className="h-4 w-4" />
                Нагрузка
              </button>
            </div>
            </div>
          </div>
        </section>

        {!isApprovalsMode && unbilledClientTasks.length > 0 ? (
          <section className="mb-3 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-rose-200 bg-gradient-to-r from-rose-50 to-amber-50 p-3" data-testid="task-unbilled-reminder">
            <div className="flex items-center gap-3">
              <span className="rounded-xl bg-rose-600 p-2 text-white"><WalletCards className="h-5 w-5" /></span>
              <div>
                <div className="font-semibold text-rose-950">Платные задачи без счёта: {unbilledClientTasks.length}</div>
                <div className="text-sm text-rose-700">Отметьте задачи одного клиента в таблице и подготовьте по ним единый счёт.</div>
              </div>
            </div>
            <button
              type="button"
              onClick={handleCreateInvoiceFromSelectedTasks}
              disabled={selectedTasksForBulk.length === 0 || !onCreateInvoiceDraft}
              className="inline-flex items-center gap-2 rounded-xl bg-rose-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <WalletCards className="h-4 w-4" />
              Подготовить счёт{selectedTasksForBulk.length > 0 ? ` (${selectedTasksForBulk.length})` : ''}
            </button>
          </section>
        ) : null}

        {viewMode === 'list' && !isApprovalsMode && selectedTasksForBulk.length > 0 ? (
          <TaskBulkActionsPanel
            selectedCount={selectedTasksForBulk.length}
            assignees={assigneeOptions}
            assigneeUserId={bulkAssigneeUserId}
            priority={bulkPriority}
            status={bulkStatus}
            dueAt={bulkDueAt}
            saving={bulkSaving}
            deleting={bulkDeleting}
            canDelete={canDeleteTasks}
            onAssigneeChange={setBulkAssigneeUserId}
            onPriorityChange={setBulkPriority}
            onStatusChange={setBulkStatus}
            onDueAtChange={setBulkDueAt}
            onApply={handleBulkUpdate}
            onDelete={handleBulkDelete}
            onClear={clearBulkSelection}
          />
        ) : null}

        {!embedded && !isApprovalsMode && automationCandidates.length > 0 ? (
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
            currentUserId={currentUserId}
            isAdminUser={isAdminUser}
            taskSettings={taskSettings}
            taskPermissions={permissions.tasks}
            selectedTaskIds={selectedTaskIds}
            selectableTaskIds={selectableTaskIds}
            allVisibleSelected={allVisibleBulkSelected}
            onOpenClient={onOpenClient}
            onOpenCase={onOpenCase}
            onOpenLegalEntity={onOpenLegalEntity}
            onToggleTaskSelection={toggleTaskSelection}
            onToggleAllVisibleSelection={toggleAllVisibleSelection}
            onEdit={openTaskEditor}
            onDelete={handleDelete}
            canDeleteTasks={canDeleteTasks}
            onStatusChange={setTaskStatus}
            onToggleControl={toggleTaskControl}
            onDone={handleQuickTaskCompletion}
            onReviewDeadlineChange={openDeadlineChangeDecisionPrompt}
            onReviewApproval={openApprovalDecisionPrompt}
            sortBy={sortBy}
            sortDir={sortDir}
            onSortChange={handleSortChange}
            layoutSettings={taskTableLayoutSettings}
          />
          )
        ) : viewMode === 'board' ? (
          <TaskBoard
            columns={boardColumns}
            highlightedTaskId={highlightedTaskId}
            currentUserId={currentUserId}
            isAdminUser={isAdminUser}
            onEdit={openTaskEditor}
            onDone={handleQuickTaskCompletion}
            onReviewDeadlineChange={openDeadlineChangeDecisionPrompt}
            onReviewApproval={openApprovalDecisionPrompt}
            approvalQuickActionMode={taskSettings.approvalQuickActionMode}
            canCompleteTask={(task) => canCompleteTaskForCurrentUser(
              task,
              currentUserId,
              isAdminUser,
              taskSettings,
              permissions.tasks,
            )}
          />
        ) : viewMode === 'calendar' ? (
          <>
            {!embedded ? (
            <CaseCalendarEventsPanel
              events={caseCalendarEvents}
              summary={caseCalendarSummary}
              timeZoneId={effectiveTaskJournalTimeZoneId}
              loading={caseCalendarLoading}
              canExport={canExportCases}
              exporting={caseCalendarExporting}
              reminderPreset={icsReminderPreset}
              onReminderPresetChange={setIcsReminderPreset}
              onExport={() => void handleCaseCalendarExport()}
            />
            ) : null}
            <TaskCalendar
              tasks={calendarTasks}
              timeZoneId={effectiveTaskJournalTimeZoneId}
              mode={periodMode}
              colorMode={calendarColorMode}
              completedTaskCalendarAction={completedTaskCalendarAction}
              loadWarningMode={calendarLoadWarningMode}
              anchorDate={calendarAnchorDate}
              selectedDate={calendarSelectedDate}
              highlightedTaskId={highlightedTaskId}
              loading={loading}
              onPrevious={() => shiftCalendarPeriod(-1)}
              onNext={() => shiftCalendarPeriod(1)}
              onToday={resetCalendarPeriod}
              onSelectDate={setCalendarSelectedDate}
              onCreateDate={canCreateTasks ? openTaskCreateForDate : undefined}
              onMoveTask={handleCalendarTaskMove}
              onResizeTask={handleCalendarTaskResize}
              onEdit={openTaskEditor}
              onDone={handleQuickTaskCompletion}
              onReviewApproval={openApprovalDecisionPrompt}
              currentUserId={currentUserId}
              isAdminUser={isAdminUser}
              taskSettings={taskSettings}
              taskPermissions={permissions.tasks}
              movingTaskId={calendarMovingTaskId}
              resizingTaskId={calendarResizingTaskId}
              readOnly={Boolean(offlineCalendarSnapshot)}
            />
          </>
        ) : (
          <>
            {!embedded ? (
            <CaseCalendarEventsPanel
              events={caseCalendarEvents}
              summary={caseCalendarSummary}
              timeZoneId={effectiveTaskJournalTimeZoneId}
              loading={caseCalendarLoading}
              canExport={canExportCases}
              exporting={caseCalendarExporting}
              reminderPreset={icsReminderPreset}
              onReminderPresetChange={setIcsReminderPreset}
              onExport={() => void handleCaseCalendarExport()}
            />
            ) : null}
            <TaskTimelineCalendar
              tasks={calendarTasks}
              timeZoneId={effectiveTaskJournalTimeZoneId}
              colorMode={calendarColorMode}
              completedTaskCalendarAction={completedTaskCalendarAction}
              anchorDate={calendarAnchorDate}
              highlightedTaskId={highlightedTaskId}
              loading={loading}
              assigneeFilter={assigneeFilter}
              onPrevious={() => shiftTimelineWeek(-1)}
              onNext={() => shiftTimelineWeek(1)}
              onToday={resetCalendarPeriod}
              onCreateDate={canCreateTasks ? openTaskCreateForDate : undefined}
              onMoveTask={handleTimelineTaskMove}
              onEdit={openTaskEditor}
              onDone={handleQuickTaskCompletion}
              onReviewApproval={openApprovalDecisionPrompt}
              currentUserId={currentUserId}
              isAdminUser={isAdminUser}
              taskSettings={taskSettings}
              taskPermissions={permissions.tasks}
              movingTaskId={calendarMovingTaskId}
              readOnly={Boolean(offlineCalendarSnapshot)}
            />
          </>
        )}
      </div>

      {modal ? (
        <TaskCardComponent
          key={modal.task ? `edit-${modal.task.id}` : 'create'}
          presentation="modal"
          state={modal}
          tasks={tasks}
          assignees={assignees}
          clients={clients}
          legalEntities={legalEntities}
          counterpartyOptions={counterpartyOptions}
          currentUserId={currentUserId}
          currentUserName={currentUserName}
          isAdminUser={isAdminUser}
          taskSettings={taskSettings}
          notificationDefaults={taskNotificationDefaults}
          canSwitchToEdit={canEditModalTask}
          canCompleteTask={canCompleteModalTask}
          canDeleteTask={canDeleteModalTask}
          canDuplicateTask={canCreateTasks}
          canSendFilesToYandexDisk={canSendTaskFilesToYandexDisk}
          onClose={closeTaskModal}
          onEnableEdit={() => setModal((current) => current?.task ? { ...current, mode: 'edit' } : current)}
          onSave={handleSave}
          onResultSave={handleResultSave}
          onApprovalDecision={handleApprovalDecision}
          onReviewDeadlineChange={(task, request) => {
            closeTaskModal();
            openDeadlineChangeDecisionPrompt(task, request);
          }}
          onReviewApproval={(task, participant) => {
            closeTaskModal();
            openApprovalDecisionPrompt(task, participant);
          }}
          onDelete={handleDelete}
          onDuplicate={handleDuplicateTask}
        />
      ) : null}

      {creationWizard ? (
        <TaskBillingCreateWizard
          counterpartyOptions={counterpartyOptions}
          initialDefaults={creationWizard.defaults}
          canViewCases={canViewCases}
          onCancel={() => setCreationWizard(null)}
          onContinue={(defaults) => {
            setCreationWizard(null);
            setModal({ mode: 'create', dueAt: creationWizard.dueAt, defaults });
            setHighlightedTaskId(null);
            onRouteTaskChange?.(null);
          }}
        />
      ) : null}
      {completionPromptTask ? (
        <TaskCompletionResultPrompt
          task={completionPromptTask}
          result={completionPromptResult}
          error={completionPromptError}
          saving={completionPromptSaving}
          onResultChange={(value) => {
            setCompletionPromptResult(value);
            if (completionPromptError) {
              setCompletionPromptError(null);
            }
          }}
          requireCompletionResult={requireCompletionResult}
          onSubmit={(files) => void submitCompletionPrompt(files)}
          onClose={closeCompletionPrompt}
        />
      ) : null}
      {quickApprovalPromptTask ? (
        <TaskCompletionResultPrompt
          task={quickApprovalPromptTask}
          eyebrow="Отправка на согласование"
          submitLabel="Отправить на согласование"
          result={quickApprovalPromptResult}
          error={quickApprovalPromptError}
          saving={quickApprovalPromptSaving}
          onResultChange={(value) => {
            setQuickApprovalPromptResult(value);
            if (quickApprovalPromptError) {
              setQuickApprovalPromptError(null);
            }
          }}
          requireCompletionResult={requireCompletionResult}
          onSubmit={(files) => void submitQuickApprovalPrompt(files)}
          onClose={closeQuickApprovalPrompt}
        />
      ) : null}
      {deadlineChangeDecisionPrompt ? (
        <TaskDeadlineChangeDecisionPrompt
          state={deadlineChangeDecisionPrompt}
          approvedDueAt={deadlineChangeDecisionDueAt}
          comment={deadlineChangeDecisionComment}
          error={deadlineChangeDecisionError}
          saving={deadlineChangeDecisionSaving}
          onApprovedDueAtChange={(value) => {
            setDeadlineChangeDecisionDueAt(value);
            if (deadlineChangeDecisionError) {
              setDeadlineChangeDecisionError(null);
            }
          }}
          onCommentChange={(value) => {
            setDeadlineChangeDecisionComment(value);
            if (deadlineChangeDecisionError) {
              setDeadlineChangeDecisionError(null);
            }
          }}
          onApprove={() => void approveDeadlineChangeDecision()}
          onReject={() => void rejectDeadlineChangeDecision()}
          onSnooze={snoozeDeadlineChangeDecisionPrompt}
        />
      ) : null}
      {approvalDecisionPrompt ? (
        <TaskApprovalDecisionPrompt
          state={approvalDecisionPrompt}
          revisionText={approvalDecisionRevisionText}
          revisionDueAt={approvalDecisionRevisionDueAt}
          error={approvalDecisionError}
          saving={approvalDecisionSaving}
          onRevisionTextChange={(value) => {
            setApprovalDecisionRevisionText(value);
            if (approvalDecisionError) {
              setApprovalDecisionError(null);
            }
          }}
          onRevisionDueAtChange={(value) => {
            setApprovalDecisionRevisionDueAt(value);
            if (approvalDecisionError) {
              setApprovalDecisionError(null);
            }
          }}
          onApprove={() => void approvePromptDecision()}
          onApproveComplete={() => void approvePromptDecision('complete')}
          onApproveReturnToAssignee={() => void approvePromptDecision('returnToAssignee')}
          onReturnToRevision={() => void returnPromptDecisionToRevision()}
          onSnooze={snoozeApprovalDecisionPrompt}
        />
      ) : null}
      </div>
    </TaskStickerStylesProvider>
  );
}

function TaskCompletionResultPrompt({
  task,
  taskTitle,
  eyebrow = 'Завершение задачи',
  submitLabel = 'Завершить задачу',
  result,
  error,
  saving,
  requireCompletionResult,
  onResultChange,
  onSubmit,
  onClose,
}: {
  task?: TaskJournalItem;
  taskTitle?: string;
  eyebrow?: string;
  submitLabel?: string;
  result: string;
  error: string | null;
  saving: boolean;
  requireCompletionResult: boolean;
  onResultChange: (value: string) => void;
  onSubmit: (files: File[]) => void;
  onClose: () => void;
}) {
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [fileError, setFileError] = useState<string | null>(null);

  const selectFiles = (files: FileList | null) => {
    if (!files?.length) return;

    const incoming = Array.from(files);
    const invalidFile = incoming.find((file) => file.size <= 0 || file.size > MAX_TASK_COMPLETION_FILE_BYTES);
    if (invalidFile) {
      setFileError(
        invalidFile.size <= 0
          ? `Файл «${invalidFile.name}» пуст.`
          : `Файл «${invalidFile.name}» превышает допустимый размер 25 МБ.`,
      );
      return;
    }

    setFileError(null);
    setSelectedFiles((current) => {
      const known = new Set(current.map((file) => `${file.name}:${file.size}:${file.lastModified}`));
      return [
        ...current,
        ...incoming.filter((file) => !known.has(`${file.name}:${file.size}:${file.lastModified}`)),
      ];
    });
  };

  return (
    <div className="fixed inset-0 z-[70] flex items-start justify-center overflow-y-auto bg-slate-900/40 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6">
      <div className="max-h-[calc(100vh-2rem)] w-full max-w-[min(42rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl bg-white p-4 shadow-2xl sm:p-6">
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0">
            <p className="text-xs font-semibold uppercase tracking-[0.16em] text-emerald-600">{eyebrow}</p>
            <h2 className="mt-2 text-xl font-semibold text-slate-900">Добавить результат по задаче</h2>
            <p className="mt-2 line-clamp-2 text-sm text-slate-500">{task?.title ?? taskTitle}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={saving}
            className="rounded-full border border-slate-200 p-2 text-slate-500 transition hover:bg-slate-50 disabled:cursor-wait disabled:opacity-60"
            aria-label="Закрыть окно результата"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <label className="mt-5 block text-sm font-semibold text-slate-700" htmlFor="task-completion-result">
          Результат
        </label>
        <textarea
          id="task-completion-result"
          value={result}
          onChange={(event) => onResultChange(event.target.value)}
          className="mt-2 block w-full max-h-[40vh] min-h-[120px] resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50 sm:min-h-[160px]"
          rows={7}
          placeholder={TASK_COMPLETION_RESULT_HINT}
          autoFocus
          disabled={saving}
        />
        <p className="mt-2 text-sm text-slate-500">
          {requireCompletionResult ? TASK_COMPLETION_RESULT_HINT : 'Можно оставить пустым, если результат не требуется.'}
        </p>
        {task?.id ? (
          <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              className="sr-only"
              data-testid="task-completion-file-input"
              onChange={(event) => {
                selectFiles(event.currentTarget.files);
                event.currentTarget.value = '';
              }}
              disabled={saving}
            />
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => fileInputRef.current?.click()}
                disabled={saving}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:border-blue-300 hover:text-blue-700 disabled:cursor-wait disabled:opacity-60"
              >
                <Paperclip className="h-4 w-4" />
                Прикрепить файл
              </button>
              <span className="text-xs text-slate-500">До 25 МБ на файл. Можно выбрать несколько файлов.</span>
            </div>
            {selectedFiles.length > 0 ? (
              <ul className="mt-3 space-y-2" aria-label="Файлы результата">
                {selectedFiles.map((file) => {
                  const key = `${file.name}:${file.size}:${file.lastModified}`;
                  return (
                    <li key={key} className="flex items-center justify-between gap-3 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
                      <span className="min-w-0 truncate text-slate-700" title={file.name}>
                        {file.name} <span className="text-xs text-slate-400">({formatFileSize(file.size)})</span>
                      </span>
                      <button
                        type="button"
                        onClick={() => setSelectedFiles((current) => current.filter((item) => `${item.name}:${item.size}:${item.lastModified}` !== key))}
                        disabled={saving}
                        className="shrink-0 rounded-md p-1 text-slate-400 transition hover:bg-rose-50 hover:text-rose-600 disabled:cursor-wait disabled:opacity-60"
                        aria-label={`Убрать файл ${file.name}`}
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </li>
                  );
                })}
              </ul>
            ) : null}
            {fileError ? <p className="mt-2 text-sm font-semibold text-rose-600">{fileError}</p> : null}
          </div>
        ) : null}
        {error ? <p className="mt-2 text-sm font-semibold text-rose-600">{error}</p> : null}

        <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={saving}
            className="w-full rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => onSubmit(selectedFiles)}
            disabled={saving}
            className="w-full rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
          >
            {saving ? 'Сохраняем...' : submitLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function TaskDecisionSnoozeMenu({
  disabled,
  onSnooze,
}: {
  disabled?: boolean;
  onSnooze: (minutes: number) => void;
}) {
  return (
    <details className="relative">
      <summary
        className={`inline-flex w-fit cursor-pointer list-none items-center gap-1.5 rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-semibold text-slate-700 shadow-sm transition hover:bg-slate-50 ${
          disabled ? 'pointer-events-none opacity-60' : ''
        }`}
      >
        <Clock3 className="h-3.5 w-3.5" />
        Отложить
      </summary>
      <div className="absolute right-0 z-10 mt-2 w-36 overflow-hidden rounded-xl border border-slate-200 bg-white p-1 shadow-xl">
        {FORCED_TASK_SNOOZE_OPTIONS.map((option) => (
          <button
            key={option.minutes}
            type="button"
            onClick={() => onSnooze(option.minutes)}
            disabled={disabled}
            className="block w-full rounded-lg px-3 py-2 text-left text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {option.label}
          </button>
        ))}
      </div>
    </details>
  );
}

function TaskApprovalDecisionPrompt({
  state,
  revisionText,
  revisionDueAt,
  error,
  saving,
  onRevisionTextChange,
  onRevisionDueAtChange,
  onApprove,
  onApproveComplete,
  onApproveReturnToAssignee,
  onReturnToRevision,
  onSnooze,
}: {
  state: Exclude<ApprovalDecisionPromptState, null>;
  revisionText: string;
  revisionDueAt: string;
  error: string | null;
  saving: 'approve' | 'approveComplete' | 'approveReturn' | 'revision' | null;
  onRevisionTextChange: (value: string) => void;
  onRevisionDueAtChange: (value: string) => void;
  onApprove: () => void;
  onApproveComplete: () => void;
  onApproveReturnToAssignee: () => void;
  onReturnToRevision: () => void;
  onSnooze: (minutes: number) => void;
}) {
  const { task, participant } = state;
  const approvers = getOrderedApprovers(task);
  const isFinalApprover = approvers.every((item) => item === participant || item.approvalStatus === 'approved');
  const revisionDisabled = Boolean(saving);
  const resultText = task.result.trim() || 'Результат по задаче не заполнен.';

  return (
    <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-950/55 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6">
      <div
        className="max-h-[calc(100vh-2rem)] w-full max-w-[min(48rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl border border-emerald-200 bg-white p-4 shadow-2xl sm:p-6"
        role="dialog"
        aria-modal="true"
        aria-labelledby="task-approval-decision-title"
        data-testid="task-approval-decision-prompt"
      >
        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0">
            <p className="text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">Согласование задачи</p>
            <h2 id="task-approval-decision-title" className="mt-2 text-xl font-semibold text-slate-950">
              У вас задача на согласовании
            </h2>
            <p className="mt-2 line-clamp-2 text-sm text-slate-600">{task.title}</p>
          </div>
          <div className="flex shrink-0 flex-col items-start gap-2 sm:items-end">
            <span className="inline-flex w-fit items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-800">
              <CheckCircle2 className="h-3.5 w-3.5" />
              Требует решения
            </span>
            <TaskDecisionSnoozeMenu disabled={Boolean(saving)} onSnooze={onSnooze} />
          </div>
        </div>

        <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 sm:grid-cols-2">
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Согласующий</div>
            <div className="mt-1 font-semibold text-slate-900">{getParticipantDisplayName(participant) || 'Не указано'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Исполнитель</div>
            <div className="mt-1 font-semibold text-slate-900">{task.assigneeName || 'Не указан'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Постановщик</div>
            <div className="mt-1 font-medium">{task.authorName || 'Не указан'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Срок задачи</div>
            <div className="mt-1 font-medium">{formatDateTime(task.dueAt)}</div>
          </div>
        </div>

        {task.description ? (
          <div className="mt-4 rounded-xl border border-slate-200 bg-white px-3 py-3 text-sm leading-6 text-slate-700">
            <div className="mb-1 text-xs font-semibold uppercase text-slate-400">Описание задачи</div>
            <div className="whitespace-pre-wrap">{task.description}</div>
          </div>
        ) : null}

        <div className="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-3 text-sm leading-6 text-emerald-950">
          <div className="mb-1 text-xs font-semibold uppercase text-emerald-700">Результат исполнителя</div>
          <div className="whitespace-pre-wrap">{resultText}</div>
        </div>

        <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="task-approval-revision-due-at">
          Срок доработки
        </label>
        <SystemDateInput
          id="task-approval-revision-due-at"
          type="datetime-local"
          value={revisionDueAt}
          onChange={(event) => onRevisionDueAtChange(event.target.value)}
          className="mt-2 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
          disabled={Boolean(saving)}
          aria-label="Срок доработки"
        />
        <p className="mt-2 text-sm text-slate-500">
          Заполняется только при возврате на доработку. Если оставить пустым, срок задачи не изменится.
        </p>

        <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="task-approval-revision-comment">
          Комментарий для возврата на доработку
        </label>
        <textarea
          id="task-approval-revision-comment"
          value={revisionText}
          onChange={(event) => onRevisionTextChange(event.target.value)}
          className="mt-2 block w-full min-h-[104px] resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
          rows={4}
          placeholder="Если задача требует правок, укажите что именно нужно исправить."
          disabled={Boolean(saving)}
        />
        {error ? <p className="mt-2 text-sm font-semibold text-rose-600">{error}</p> : null}

        <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onReturnToRevision}
            disabled={revisionDisabled}
            className="w-full rounded-xl border border-orange-200 bg-white px-4 py-2.5 text-sm font-semibold text-orange-700 transition hover:bg-orange-50 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto"
          >
            {saving === 'revision' ? 'Возвращаем...' : 'Вернуть на доработку'}
          </button>
          {isFinalApprover ? (
            <>
              <button
                type="button"
                onClick={onApproveReturnToAssignee}
                disabled={Boolean(saving)}
                className="w-full rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
              >
                {saving === 'approveReturn' ? 'Согласовываем...' : 'Согласовать и отправить исполнителю'}
              </button>
              <button
                type="button"
                onClick={onApproveComplete}
                disabled={Boolean(saving)}
                className="w-full rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
              >
                {saving === 'approveComplete' ? 'Согласовываем...' : 'Согласовать и завершить'}
              </button>
            </>
          ) : (
            <button
              type="button"
              onClick={onApprove}
              disabled={Boolean(saving)}
              className="w-full rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
            >
              {saving === 'approve' ? 'Согласовываем...' : 'Согласовать'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function TaskDeadlineChangeDecisionPrompt({
  state,
  approvedDueAt,
  comment,
  error,
  saving,
  onApprovedDueAtChange,
  onCommentChange,
  onApprove,
  onReject,
  onSnooze,
}: {
  state: Exclude<DeadlineChangeDecisionPromptState, null>;
  approvedDueAt: string;
  comment: string;
  error: string | null;
  saving: 'approve' | 'reject' | null;
  onApprovedDueAtChange: (value: string) => void;
  onCommentChange: (value: string) => void;
  onApprove: () => void;
  onReject: () => void;
  onSnooze: (minutes: number) => void;
}) {
  const { task, request } = state;
  const rejectDisabled = Boolean(saving) || !comment.trim();

  return (
    <div className="fixed inset-0 z-[80] flex items-start justify-center overflow-y-auto bg-slate-950/55 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6">
      <div
        className="max-h-[calc(100vh-2rem)] w-full max-w-[min(46rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl border border-amber-200 bg-white p-4 shadow-2xl sm:p-6"
        role="dialog"
        aria-modal="true"
        aria-labelledby="deadline-change-decision-title"
        data-testid="deadline-change-decision-prompt"
      >
        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0">
            <p className="text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">Запрос переноса срока</p>
            <h2 id="deadline-change-decision-title" className="mt-2 text-xl font-semibold text-slate-950">
              У вас запросили перенос срока по задаче
            </h2>
            <p className="mt-2 line-clamp-2 text-sm text-slate-600">{task.title}</p>
          </div>
          <div className="flex shrink-0 flex-col items-start gap-2 sm:items-end">
            <span className="inline-flex w-fit items-center gap-1.5 rounded-full border border-amber-200 bg-amber-50 px-3 py-1 text-xs font-semibold text-amber-800">
              <TimerReset className="h-3.5 w-3.5" />
              Требует решения
            </span>
            <TaskDecisionSnoozeMenu disabled={Boolean(saving)} onSnooze={onSnooze} />
          </div>
        </div>

        <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 sm:grid-cols-2">
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Запросил</div>
            <div className="mt-1 font-semibold text-slate-900">{request.requestedByName || 'Не указано'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Постановщик</div>
            <div className="mt-1 font-semibold text-slate-900">{request.reviewerName || task.authorName || 'Не указано'}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Текущий срок</div>
            <div className="mt-1 font-medium">{formatDateTime(request.currentDueAt)}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-400">Запрошенный исполнителем срок</div>
            <div className="mt-1 font-semibold text-blue-700">{formatDateTime(request.requestedDueAt)}</div>
          </div>
        </div>

        <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 px-3 py-3 text-sm leading-6 text-amber-950">
          <span className="font-semibold">Причина переноса:</span> {request.reason || 'Не указана'}
        </div>

        <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="deadline-change-approved-due-at">
          Новый срок задачи
        </label>
        <SystemDateInput
          id="deadline-change-approved-due-at"
          type="datetime-local"
          value={approvedDueAt}
          onChange={(event) => onApprovedDueAtChange(event.target.value)}
          className="mt-2 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
          disabled={Boolean(saving)}
          aria-label="Новый срок задачи"
          autoFocus
        />
        <p className="mt-2 text-sm text-slate-500">
          По умолчанию указана дата, которую запросил исполнитель. Ее можно изменить перед согласованием.
        </p>

        <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="deadline-change-decision-comment">
          Комментарий к решению
        </label>
        <textarea
          id="deadline-change-decision-comment"
          value={comment}
          onChange={(event) => onCommentChange(event.target.value)}
          className="mt-2 block w-full min-h-[96px] resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:cursor-wait disabled:bg-slate-50"
          rows={4}
          placeholder="Для отказа укажите причину. Для согласования комментарий можно оставить пустым."
          disabled={Boolean(saving)}
        />
        {error ? <p className="mt-2 text-sm font-semibold text-rose-600">{error}</p> : null}

        <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onReject}
            disabled={rejectDisabled}
            className="w-full rounded-xl border border-rose-200 bg-white px-4 py-2.5 text-sm font-semibold text-rose-700 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto"
          >
            {saving === 'reject' ? 'Отклоняем...' : 'Отказать'}
          </button>
          <button
            type="button"
            onClick={onApprove}
            disabled={Boolean(saving) || !approvedDueAt}
            className="w-full rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-wait disabled:opacity-60 sm:w-auto"
          >
            {saving === 'approve' ? 'Согласовываем...' : 'Согласовать перенос'}
          </button>
        </div>
      </div>
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
            {candidate.isEscalated ? (
              <div className="mt-3 inline-flex rounded-full border border-rose-200 bg-rose-50 px-2.5 py-1 text-xs font-semibold text-rose-700">
                Эскалация
              </div>
            ) : null}
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

function MetricCard({
  label,
  value,
  icon: Icon,
  tone,
  active,
  onClick,
}: {
  label: string;
  value: number;
  icon: typeof ClipboardList;
  tone: 'blue' | 'rose' | 'emerald' | 'amber';
  active?: boolean;
  onClick?: () => void;
}) {
  const toneClass = {
    blue: 'bg-blue-50 text-blue-700',
    rose: 'bg-rose-50 text-rose-700',
    emerald: 'bg-emerald-50 text-emerald-700',
    amber: 'bg-amber-50 text-amber-700',
  }[tone];

  return (
    <button
      type="button"
      data-testid={`task-metric-${label}`}
      aria-pressed={active}
      onClick={onClick}
      className={`w-full rounded-2xl border bg-white px-5 py-4 text-left shadow-sm transition hover:-translate-y-0.5 hover:border-blue-200 hover:shadow-md ${
        active ? 'border-blue-400 ring-2 ring-blue-100' : 'border-slate-200'
      }`}
    >
      <div className="relative flex min-h-[68px] items-center justify-center text-center">
        <div className="min-w-0 px-12">
          <div className="text-sm text-slate-500">{label}</div>
          <div className="mt-2 text-3xl font-semibold text-slate-900">{value}</div>
        </div>
        <div className={`absolute right-0 top-1/2 -translate-y-1/2 rounded-xl p-3 ${toneClass}`}>
          <Icon className="h-5 w-5" />
        </div>
      </div>
    </button>
  );
}

function TaskBulkActionsPanel({
  selectedCount,
  assignees,
  assigneeUserId,
  priority,
  status,
  dueAt,
  saving,
  deleting,
  canDelete,
  onAssigneeChange,
  onPriorityChange,
  onStatusChange,
  onDueAtChange,
  onApply,
  onDelete,
  onClear,
}: {
  selectedCount: number;
  assignees: TaskJournalAssignee[];
  assigneeUserId: string;
  priority: TaskJournalPriority | '';
  status: TaskJournalStatus | '';
  dueAt: string;
  saving: boolean;
  deleting: boolean;
  canDelete: boolean;
  onAssigneeChange: (value: string) => void;
  onPriorityChange: (value: TaskJournalPriority | '') => void;
  onStatusChange: (value: TaskJournalStatus | '') => void;
  onDueAtChange: (value: string) => void;
  onApply: () => void;
  onDelete: () => void;
  onClear: () => void;
}) {
  const busy = saving || deleting;

  return (
    <section className="mb-4 rounded-2xl border border-blue-100 bg-blue-50/70 px-4 py-3 shadow-sm" data-testid="task-bulk-actions">
      <div className="flex flex-col gap-3 xl:flex-row xl:items-end xl:justify-between">
        <div className="grid flex-1 gap-3 sm:grid-cols-2 xl:grid-cols-5">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.14em] text-blue-700">Выбрано</div>
            <div className="mt-2 text-lg font-semibold text-slate-900">{selectedCount}</div>
          </div>
          <label className="block">
            <span className="text-xs font-semibold text-slate-500">Исполнитель</span>
            <SystemSelect
              value={assigneeUserId}
              onChange={(event) => onAssigneeChange(event.target.value)}
              className="mt-1 h-10 w-full rounded-xl border border-blue-100 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Массово назначить исполнителя"
            >
              <option value="">Не менять</option>
              {assignees.map((assignee) => (
                <option key={assignee.id} value={String(assignee.id)}>{assignee.name}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="block">
            <span className="text-xs font-semibold text-slate-500">Приоритет</span>
            <SystemSelect
              value={priority}
              onChange={(event) => onPriorityChange(event.target.value as TaskJournalPriority | '')}
              className="mt-1 h-10 w-full rounded-xl border border-blue-100 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Массово изменить приоритет"
            >
              <option value="">Не менять</option>
              {PRIORITY_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="block">
            <span className="text-xs font-semibold text-slate-500">Статус</span>
            <SystemSelect
              value={status}
              onChange={(event) => onStatusChange(event.target.value as TaskJournalStatus | '')}
              className="mt-1 h-10 w-full rounded-xl border border-blue-100 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Массово изменить статус"
            >
              <option value="">Не менять</option>
              {BULK_STATUS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="block sm:col-span-2 xl:col-span-1">
            <span className="text-xs font-semibold text-slate-500">Срок</span>
            <SystemDateInput
              type="datetime-local"
              value={dueAt}
              onChange={(event) => onDueAtChange(event.target.value)}
              className="mt-1 h-10 w-full rounded-xl border border-blue-100 bg-white px-3 text-sm text-slate-700 outline-none transition focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              aria-label="Массово изменить срок"
            />
          </label>
        </div>
        <div className="flex shrink-0 flex-wrap justify-end gap-2">
          <button
            type="button"
            onClick={onClear}
            disabled={busy}
            className="inline-flex h-10 items-center justify-center rounded-xl border border-blue-100 bg-white px-4 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            Снять выбор
          </button>
          {canDelete ? (
            <button
              type="button"
              onClick={onDelete}
              disabled={busy}
              className="inline-flex h-10 items-center justify-center gap-2 rounded-xl border border-rose-200 bg-white px-4 text-sm font-semibold text-rose-600 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <Trash2 className="h-4 w-4" />
              {deleting ? 'Удаляем...' : 'Удалить выбранные'}
            </button>
          ) : null}
          <button
            type="button"
            onClick={onApply}
            disabled={busy}
            className="inline-flex h-10 items-center justify-center rounded-xl bg-blue-600 px-4 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {saving ? 'Сохраняем...' : 'Применить'}
          </button>
        </div>
      </div>
    </section>
  );
}

function TaskTable({
  tasks,
  highlightedTaskId,
  currentUserId,
  isAdminUser,
  taskSettings,
  taskPermissions,
  selectedTaskIds,
  selectableTaskIds,
  allVisibleSelected,
  onOpenClient,
  onOpenCase,
  onOpenLegalEntity,
  onToggleTaskSelection,
  onToggleAllVisibleSelection,
  onEdit,
  onDelete,
  canDeleteTasks,
  onStatusChange,
  onToggleControl,
  onDone,
  onReviewDeadlineChange,
  onReviewApproval,
  sortBy,
  sortDir,
  onSortChange,
  layoutSettings,
}: {
  tasks: TaskJournalItem[];
  highlightedTaskId: number | null;
  currentUserId: number | null;
  isAdminUser: boolean;
  taskSettings: TaskJournalSettingsDto;
  taskPermissions: TaskSectionPermissions;
  selectedTaskIds: Set<number>;
  selectableTaskIds: Set<number>;
  allVisibleSelected: boolean;
  onOpenClient?: (clientId: number, caseId?: number | null) => void | Promise<void>;
  onOpenCase?: (caseId: number, clientId?: number | null) => void | Promise<void>;
  onOpenLegalEntity?: (legalEntityId: number) => void | Promise<void>;
  onToggleTaskSelection: (taskId: number, checked: boolean) => void;
  onToggleAllVisibleSelection: (checked: boolean) => void;
  onEdit: (task: TaskJournalItem) => void;
  onDelete: (task: TaskJournalItem) => void;
  canDeleteTasks: boolean;
  onStatusChange: (task: TaskJournalItem, status: TaskJournalStatus) => void;
  onToggleControl: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
  onReviewDeadlineChange: (task: TaskJournalItem, request: TaskJournalDeadlineChangeRequest) => void;
  onReviewApproval: (task: TaskJournalItem, participant: TaskJournalParticipant) => void;
  sortBy: TaskJournalSortKey;
  sortDir: 'asc' | 'desc';
  onSortChange: (sortBy: TaskJournalSortKey) => void;
  layoutSettings: TaskTableLayoutSettings;
}) {
  const {
    handleResizeStart,
    getColumnWidth,
  } = layoutSettings;

  const getColumnStyle = (columnKey: TaskTableColumnKey) => {
    const width = getColumnWidth(columnKey);
    return { width, minWidth: width, maxWidth: width };
  };

  const renderResizeHandle = (columnKey: TaskTableColumnKey, label: string) => (
    <div
      role="separator"
      aria-label={`Изменить ширину колонки ${label}`}
      aria-orientation="vertical"
      onMouseDown={(event) => handleResizeStart(columnKey, event)}
      className="absolute right-0 top-2 z-30 h-[calc(100%-1rem)] w-1 cursor-col-resize touch-none rounded-full bg-transparent transition hover:bg-blue-300 active:bg-blue-500"
    />
  );

  const renderSortIcon = (columnKey: TaskJournalSortKey) => {
    if (sortBy !== columnKey) {
      return <ArrowUpDown className="h-3.5 w-3.5 text-slate-400" />;
    }

    return sortDir === 'desc'
      ? <ArrowDown className="h-3.5 w-3.5 text-blue-600" />
      : <ArrowUp className="h-3.5 w-3.5 text-blue-600" />;
  };

  const renderSortHeader = (columnKey: TaskJournalSortKey, label: string, className = '') => {
    const compactColumnClass = columnKey === 'author' || columnKey === 'result' ? 'hidden 2xl:table-cell' : '';

    return (
      <th
        data-testid={`task-column-header-${columnKey}`}
        className={`relative px-2 py-4 sm:px-3 xl:px-4 ${compactColumnClass} ${className}`}
        style={getColumnStyle(columnKey)}
      >
        <button
          type="button"
          onClick={() => onSortChange(columnKey)}
          className={`inline-flex min-w-0 items-center gap-1 rounded px-1 py-0.5 text-left uppercase tracking-[0.16em] transition hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-300 ${
            sortBy === columnKey ? 'text-blue-700' : 'text-slate-500'
          }`}
          aria-label={`Сортировать по колонке ${label}`}
          aria-sort={sortBy === columnKey ? (sortDir === 'desc' ? 'descending' : 'ascending') : 'none'}
        >
          <span>{label}</span>
          {renderSortIcon(columnKey)}
        </button>
        {renderResizeHandle(columnKey, label)}
      </th>
    );
  };

  return (
    <div data-testid="task-table-card" className="min-w-0 max-w-full overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div data-testid="task-table-scroll" className="max-w-full overflow-x-auto overscroll-x-contain">
        <table data-testid="task-table" className="pp-task-journal-table w-max min-w-full table-fixed">
          <thead className="border-b border-slate-200 bg-slate-50">
            <tr className="text-left text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">
              <th className="w-10 px-2 py-4 sm:px-3 xl:w-12 xl:px-4">
                <SystemCheckbox
                  type="checkbox"
                  checked={allVisibleSelected}
                  disabled={selectableTaskIds.size === 0}
                  onChange={(event) => onToggleAllVisibleSelection(event.target.checked)}
                  aria-label="Выбрать все задачи на странице"
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
              </th>
              {renderSortHeader('counterparty', 'Контрагент / дело')}
              {renderSortHeader('title', 'Наименование задачи')}
              {renderSortHeader('assignee', 'Исполнитель')}
              {renderSortHeader('author', 'Поручил')}
              {renderSortHeader('dueAt', 'Дата исполнения')}
              {renderSortHeader('type', 'Тип', 'hidden xl:table-cell')}
              {renderSortHeader('result', 'Результат')}
              {renderSortHeader('status', 'Статус')}
              <th
                data-testid="task-column-header-actions"
                className="sticky right-0 z-20 bg-slate-50 px-3 py-4 text-center shadow-[-10px_0_18px_-18px_rgba(15,23,42,0.55)] xl:px-4"
                style={getColumnStyle('actions')}
              >
                Действия
                {renderResizeHandle('actions', 'Действия')}
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {tasks.length === 0 ? (
              <tr>
                <td colSpan={10} className="px-4 py-12 text-center text-sm text-slate-500">
                  По текущим фильтрам задач нет.
                </td>
              </tr>
            ) : tasks.map((task) => {
              const TypeIcon = getTypeMeta(task.type).icon;
              const isOverdue = isTaskOverdue(task);
              const isHighlighted = highlightedTaskId === task.id;
              const taskAssignees = getTaskParticipants(task, 'assignee');
              const approvalSummary = getApprovalSummary(task);
              const pendingDeadlineRequests = getPendingDeadlineChangeRequests(task);
              const pendingDeadlineRequest = pendingDeadlineRequests[0] ?? null;
              const reviewableDeadlineRequest = getReviewableDeadlineChangeRequest(task, currentUserId, isAdminUser);
              const reviewableApprovalParticipant = getReviewableApprovalParticipant(task, currentUserId);
              const isAwaitingApproval = isTaskAwaitingApproval(task);
              const useSeparateApprovalAction = isAwaitingApproval
                && taskSettings.approvalQuickActionMode === 'separateButton';
              const approvalActionDisabled = isAwaitingApproval && !reviewableApprovalParticipant;
              const completionActionDisabled = isAwaitingApproval
                && (taskSettings.approvalQuickActionMode === 'separateButton' || approvalActionDisabled);
              const taskObservers = getTaskParticipants(task, 'observer');
              const isReadOnly = isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
              const canCompleteTask = canCompleteTaskForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
              const counterpartyName = getTaskClientName(task);
              const caseTitle = getTaskCaseTitle(task);
              const taskDescription = task.description || 'Описание не заполнено';
              const canSelectTask = selectableTaskIds.has(task.id);
              const canOpenCounterparty = Boolean(
                (task.clientId && onOpenClient) || (task.legalEntityId && onOpenLegalEntity),
              );
              const canOpenCase = Boolean(task.clientCaseId && onOpenCase);
              const openCounterparty = () => {
                if (task.clientId && onOpenClient) {
                  void onOpenClient(task.clientId, task.clientCaseId ?? null);
                  return;
                }

                if (task.legalEntityId && onOpenLegalEntity) {
                  void onOpenLegalEntity(task.legalEntityId);
                }
              };
              const openCase = () => {
                if (task.clientCaseId && onOpenCase) {
                  void onOpenCase(task.clientCaseId, task.clientId ?? null);
                }
              };

              return (
                <tr
                  key={task.id}
                  className={`group align-top transition ${
                    isHighlighted ? 'bg-blue-50/70 ring-1 ring-inset ring-blue-200' : 'hover:bg-slate-50'
                  }`}
                >
                  <td className="px-2 py-4 sm:px-3 xl:px-4">
                    <SystemCheckbox
                      type="checkbox"
                      checked={selectedTaskIds.has(task.id)}
                      disabled={!canSelectTask}
                      onChange={(event) => onToggleTaskSelection(task.id, event.target.checked)}
                      aria-label={`Выбрать задачу ${task.title}`}
                      className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-40"
                    />
                  </td>
                  <td className="px-2 py-4 sm:px-3 xl:px-4" style={getColumnStyle('counterparty')}>
                    {canOpenCounterparty ? (
                      <button
                        type="button"
                        onClick={openCounterparty}
                        className="block max-w-full text-left font-semibold text-blue-700 underline-offset-2 transition hover:text-blue-800 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        title="Открыть карточку контрагента"
                      >
                        {counterpartyName}
                      </button>
                    ) : (
                      <div className="font-semibold text-slate-900">{counterpartyName}</div>
                    )}
                    {caseTitle ? (
                      canOpenCase ? (
                        <button
                          type="button"
                          onClick={openCase}
                          className="mt-1 block max-w-full text-left text-sm text-slate-600 underline-offset-2 transition hover:text-blue-700 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                          title="Открыть карточку дела"
                        >
                          {caseTitle}
                        </button>
                      ) : (
                        <div className="mt-1 text-sm text-slate-500">{caseTitle}</div>
                      )
                    ) : null}
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {task.isUnderControl ? (
                        <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-700">
                          <ShieldAlert className="h-3 w-3" />
                          На контроле
                        </span>
                      ) : null}
                      {task.tags.map((tag) => (
                        <span key={tag} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500">{tag}</span>
                      ))}
                    </div>
                  </td>
                  <td className="px-2 py-4 sm:px-3 xl:px-4" style={getColumnStyle('title')}>
                    <button
                      type="button"
                      onClick={() => onEdit(task)}
                      title={task.title}
                      aria-label={`Открыть задачу ${task.title}`}
                      data-testid={`task-title-link-${task.id}`}
                      className="block max-w-full text-left font-semibold text-blue-700 underline-offset-2 transition hover:text-blue-800 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                      {task.title}
                    </button>
                    <div className="mt-1 line-clamp-2 text-sm text-slate-500" title={taskDescription}>{taskDescription}</div>
                    <div
                      className="mt-3 flex max-w-full flex-nowrap items-center gap-1.5"
                      data-testid={`task-meta-badges-${task.id}`}
                    >
                      <TaskPriorityBadge priority={task.priority} compact />
                      <TaskBillingBadge task={task} compact />
                    </div>
                    {pendingDeadlineRequest ? (
                      <div
                        className="mt-2 inline-flex max-w-full items-center gap-1.5 rounded-lg border border-amber-200 bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-800"
                        data-testid={`task-deadline-change-request-badge-${task.id}`}
                        title={pendingDeadlineRequest.reason || undefined}
                      >
                        <TimerReset className="h-3.5 w-3.5 shrink-0" />
                        <span className="truncate">
                          Запрос переноса срока
                          {pendingDeadlineRequests.length > 1 ? ` (${pendingDeadlineRequests.length})` : ''}
                        </span>
                      </div>
                    ) : null}
                  </td>
                  <td className="px-2 py-4 text-sm text-slate-600 sm:px-3 xl:px-4" style={getColumnStyle('assignee')}>
                    <div className="font-semibold text-slate-700">
                      {taskAssignees.map(getParticipantDisplayName).filter(Boolean).join(', ') || task.assigneeName}
                    </div>
                    {approvalSummary ? (
                      <div className="mt-2 text-xs text-violet-600">
                        {approvalSummary}
                      </div>
                    ) : null}
                    {taskObservers.length > 0 ? (
                      <div className="mt-1 text-xs text-slate-400">
                        Наблюдают: {taskObservers.map(getParticipantDisplayName).filter(Boolean).join(', ')}
                      </div>
                    ) : null}
                  </td>
                  <td className="hidden px-2 py-4 text-sm text-slate-600 sm:px-3 2xl:table-cell 2xl:px-4" style={getColumnStyle('author')}>
                    <div className="font-semibold text-slate-700">{getTaskAuthorName(task)}</div>
                  </td>
                  <td className="px-2 py-4 sm:px-3 xl:px-4" style={getColumnStyle('dueAt')}>
                    <div className={isOverdue ? 'font-semibold text-rose-600' : 'font-medium text-slate-800'}>{formatDateTime(task.dueAt)}</div>
                    <div className="mt-1 text-xs text-slate-500">{formatRelative(task.dueAt)}</div>
                    <div className="mt-1 text-xs font-semibold text-slate-500">Длительность: {formatDuration(task.plannedDurationMinutes)}</div>
                  </td>
                  <td className="hidden px-2 py-4 sm:px-3 xl:table-cell xl:px-4" style={getColumnStyle('type')}>
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">
                      <TypeIcon className="h-3.5 w-3.5" />
                      {getTypeMeta(task.type).label}
                    </span>
                  </td>
                  <td
                    className="hidden whitespace-normal break-words px-4 py-4 text-sm text-slate-600 2xl:table-cell"
                    style={getColumnStyle('result')}
                    title={task.result || undefined}
                  >
                    {task.result || '—'}
                  </td>
                  <td className="px-2 py-4 sm:px-3 xl:px-4" style={getColumnStyle('status')}>
                    {isReadOnly ? (
                      <div>
                        <span className={`inline-flex rounded-xl px-3 py-2 text-sm font-semibold ${statusClass(task.status)}`}>
                          {getStatusLabel(task.status)}
                        </span>
                        {isOverdue ? <div className="mt-1 text-xs font-semibold text-rose-600">Просрочена</div> : null}
                      </div>
                    ) : (
                      <div>
                        <SystemSelect
                          value={task.status}
                          onChange={(event) => onStatusChange(task, event.target.value as TaskJournalStatus)}
                          className={`rounded-xl border-0 px-3 py-2 text-sm font-semibold outline-none ${statusClass(task.status)}`}
                        >
                          {STATUS_OPTIONS.map((option) => (
                            <option key={option.value} value={option.value}>{option.label}</option>
                          ))}
                        </SystemSelect>
                        {isOverdue ? <div className="mt-1 text-xs font-semibold text-rose-600">Просрочена</div> : null}
                      </div>
                    )}
                  </td>
                  <td
                    className={`sticky right-0 z-10 px-2 py-4 shadow-[-10px_0_18px_-18px_rgba(15,23,42,0.55)] sm:px-3 xl:px-4 ${isHighlighted ? 'bg-blue-50' : 'bg-white group-hover:bg-slate-50'}`}
                    style={getColumnStyle('actions')}
                  >
                    <div
                      data-testid={`task-actions-${task.id}`}
                      className="grid grid-cols-[repeat(auto-fit,minmax(2.25rem,1fr))] place-items-center gap-1"
                    >
                      {reviewableDeadlineRequest ? (
                        <button
                          type="button"
                          onClick={() => onReviewDeadlineChange(task, reviewableDeadlineRequest)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-amber-600 transition hover:bg-amber-50 hover:text-amber-700"
                          title="Рассмотреть запрос переноса срока"
                          aria-label="Рассмотреть запрос переноса срока"
                        >
                          <TimerReset className="h-4 w-4" />
                        </button>
                      ) : null}
                      {useSeparateApprovalAction ? (
                        <button
                          type="button"
                          onClick={() => reviewableApprovalParticipant && onReviewApproval(task, reviewableApprovalParticipant)}
                          disabled={!reviewableApprovalParticipant}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-violet-600 transition hover:bg-violet-50 hover:text-violet-700 disabled:cursor-not-allowed disabled:text-slate-300 disabled:hover:bg-transparent"
                          title={reviewableApprovalParticipant ? 'Рассмотреть согласование' : 'Задача находится на согласовании у другого сотрудника'}
                          aria-label="Рассмотреть согласование"
                        >
                          <Scale className="h-4 w-4" />
                        </button>
                      ) : null}
                      {!isReadOnly && isTaskJournalActive(task.status) && task.status !== 'inProgress' ? (
                        <button type="button" onClick={() => onStatusChange(task, 'inProgress')} className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-blue-600 transition hover:bg-blue-50 hover:text-blue-700" title="Принять в работу">
                          <Play className="h-4 w-4" />
                        </button>
                      ) : null}
                      {!isReadOnly ? (
                        <button
                          type="button"
                          onClick={() => onToggleControl(task)}
                          className={`inline-flex h-9 w-9 items-center justify-center rounded-xl transition ${task.isUnderControl ? 'text-rose-600 hover:bg-rose-50 hover:text-rose-700' : 'text-slate-400 hover:bg-rose-50 hover:text-rose-600'}`}
                          title={task.isUnderControl ? 'Снять с контроля' : 'Поставить на контроль'}
                        >
                          <ShieldAlert className="h-4 w-4" />
                        </button>
                      ) : null}
                      {canCompleteTask || isAwaitingApproval ? (
                        <button
                          type="button"
                          onClick={() => onDone(task)}
                          disabled={completionActionDisabled}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-emerald-600 transition hover:bg-emerald-50 hover:text-emerald-700 disabled:cursor-not-allowed disabled:text-slate-300 disabled:hover:bg-transparent"
                          title={completionActionDisabled
                            ? taskSettings.approvalQuickActionMode === 'separateButton'
                              ? 'Задача находится на согласовании. Используйте отдельный значок согласования.'
                              : 'Задача находится на согласовании у другого сотрудника.'
                            : isAwaitingApproval
                              ? 'Открыть окно согласования'
                              : 'Завершить'}
                          aria-label={isAwaitingApproval ? 'Действие по согласованию' : 'Завершить'}
                        >
                          <CheckCircle2 className="h-4 w-4" />
                        </button>
                      ) : null}
                      <button type="button" onClick={() => onEdit(task)} className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-900" title="Просмотр">
                        <Eye className="h-4 w-4" />
                      </button>
                      {canDeleteTasks && canDeleteTaskForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions) ? (
                        <button type="button" onClick={() => onDelete(task)} className="inline-flex h-9 w-9 items-center justify-center rounded-xl text-rose-500 transition hover:bg-rose-50 hover:text-rose-700" title="Удалить">
                          <Trash2 className="h-4 w-4" />
                        </button>
                      ) : null}
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

function TaskBillingBadge({
  task,
  className = '',
  compact = false,
}: {
  task: TaskJournalItem;
  className?: string;
  compact?: boolean;
}) {
  const billingStatus = task.billingStatus;
  const stickerKey: TaskStickerKey = billingStatus === 'unbilled'
    ? 'billing.unbilled'
    : billingStatus === 'invoiced'
      ? 'billing.invoiced'
      : billingStatus === 'fixed'
        ? 'billing.fixed'
        : task.clientId || task.legalEntityId
          ? 'billing.unpaid'
          : 'billing.internal';
  if (!billingStatus || billingStatus === 'legacy') return null;

  const invoiceSuffix = billingStatus === 'invoiced' && task.billingInvoiceNumber
    ? ` №${task.billingInvoiceNumber}`
    : '';

  return (
    <TaskStickerBadge
      stickerKey={stickerKey}
      compact={compact}
      className={className}
      labelSuffix={invoiceSuffix}
      testId={`task-billing-badge-${task.id}`}
      icon={<WalletCards className={`${compact ? 'h-3 w-3' : 'h-3.5 w-3.5'} shrink-0`} />}
    />
  );
}

function TaskBoard({
  columns,
  highlightedTaskId,
  currentUserId,
  isAdminUser,
  onEdit,
  onDone,
  onReviewDeadlineChange,
  onReviewApproval,
  approvalQuickActionMode,
  canCompleteTask,
}: {
  columns: Array<{ key: string; title: string; tone: string; items: TaskJournalItem[] }>;
  highlightedTaskId: number | null;
  currentUserId: number | null;
  isAdminUser: boolean;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
  onReviewDeadlineChange: (task: TaskJournalItem, request: TaskJournalDeadlineChangeRequest) => void;
  onReviewApproval: (task: TaskJournalItem, participant: TaskJournalParticipant) => void;
  approvalQuickActionMode: TaskJournalSettingsDto['approvalQuickActionMode'];
  canCompleteTask: (task: TaskJournalItem) => boolean;
}) {
  return (
    <div data-testid="task-board" className="grid gap-4 xl:grid-cols-4">
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
              const reviewableDeadlineRequest = getReviewableDeadlineChangeRequest(task, currentUserId, isAdminUser);
              const reviewableApprovalParticipant = getReviewableApprovalParticipant(task, currentUserId);
              const isAwaitingApproval = isTaskAwaitingApproval(task);
              const useSeparateApprovalAction = isAwaitingApproval && approvalQuickActionMode === 'separateButton';
              const completionActionDisabled = isAwaitingApproval
                && (useSeparateApprovalAction || !reviewableApprovalParticipant);
              return (
                <article
                  key={task.id}
                  className={`rounded-xl border bg-white p-4 shadow-sm ${
                    isHighlighted ? 'border-blue-300 ring-2 ring-blue-100' : 'border-slate-200'
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <TaskPriorityBadge priority={task.priority} />
                    <button type="button" onClick={() => onEdit(task)} className="rounded-lg p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" title="Просмотр">
                      <Eye className="h-4 w-4" />
                    </button>
                  </div>
                  <div className="mt-3 text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{getTaskClientName(task)}</div>
                  {getTaskCaseTitle(task) ? (
                    <div className="mt-1 text-xs text-slate-500">{getTaskCaseTitle(task)}</div>
                  ) : null}
                  <h3 className="mt-2 text-sm font-semibold leading-5 text-slate-900">{task.title}</h3>
                  <TaskBillingBadge task={task} className="mt-2" />
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <TypeIcon className="h-3.5 w-3.5" />
                    {getTypeMeta(task.type).label}
                  </div>
                  <div className="mt-2 text-xs text-slate-500">
                    Исполнитель: {getTaskAssigneeNames(task).join(', ') || task.assigneeName}
                  </div>
                  <div className="mt-1 text-xs text-slate-500">
                    Поручил: {getTaskAuthorName(task)}
                  </div>
                  <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
                    <Clock3 className="h-3.5 w-3.5" />
                    {formatDateTime(task.dueAt)}
                    <span>•</span>
                    {formatDuration(task.plannedDurationMinutes)}
                  </div>
                  {reviewableDeadlineRequest ? (
                    <button
                      type="button"
                      onClick={() => onReviewDeadlineChange(task, reviewableDeadlineRequest)}
                      className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-amber-200 px-3 py-2 text-sm font-semibold text-amber-700 transition hover:bg-amber-50"
                    >
                      <TimerReset className="h-4 w-4" />
                      Рассмотреть перенос срока
                    </button>
                  ) : null}
                  {useSeparateApprovalAction ? (
                    <button
                      type="button"
                      onClick={() => reviewableApprovalParticipant && onReviewApproval(task, reviewableApprovalParticipant)}
                      disabled={!reviewableApprovalParticipant}
                      className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-violet-200 px-3 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-50 disabled:cursor-not-allowed disabled:border-slate-200 disabled:text-slate-400 disabled:hover:bg-transparent"
                      title={reviewableApprovalParticipant ? 'Рассмотреть согласование' : 'Задача находится на согласовании у другого сотрудника'}
                    >
                      <Scale className="h-4 w-4" />
                      Рассмотреть согласование
                    </button>
                  ) : null}
                  {canCompleteTask(task) || isAwaitingApproval ? (
                    <button
                      type="button"
                      onClick={() => onDone(task)}
                      disabled={completionActionDisabled}
                      className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-50 disabled:cursor-not-allowed disabled:border-slate-200 disabled:text-slate-400 disabled:hover:bg-transparent"
                      title={completionActionDisabled
                        ? useSeparateApprovalAction
                          ? 'Задача находится на согласовании. Используйте отдельный значок согласования.'
                          : 'Задача находится на согласовании у другого сотрудника.'
                        : isAwaitingApproval
                          ? 'Открыть окно согласования'
                          : 'Завершить'}
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

function CaseCalendarEventsPanel({
  events,
  summary,
  timeZoneId,
  loading,
  canExport,
  exporting,
  reminderPreset,
  onReminderPresetChange,
  onExport,
}: {
  events: CaseCalendarEvent[];
  summary: CaseCalendarSummary | null;
  timeZoneId: string;
  loading: boolean;
  canExport: boolean;
  exporting: boolean;
  reminderPreset: IcsReminderPreset;
  onReminderPresetChange: (value: IcsReminderPreset) => void;
  onExport: () => void;
}) {
  const visibleEvents = events.slice(0, 12);
  const totalEventCount = summary?.totalEventCount ?? events.length;
  const overdueCount = summary?.overdueCount ?? events.filter((event) => event.isOverdue).length;
  const warningCount = summary?.warningCount ?? events.filter((event) => event.isWarning).length;
  const courtEventCount = summary?.courtEventCount ?? events.filter((event) => event.kind === 'court').length;
  const proceduralDeadlineCount = summary?.proceduralDeadlineCount ?? events.filter((event) => event.kind === 'procedural-deadline').length;

  return (
    <section className="mb-4 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">
            <CalendarClock className="h-4 w-4" />
            Сроки и суд
          </div>
          <h2 className="mt-1 text-lg font-semibold text-slate-900">Календарь дел</h2>
        </div>
        <div className="flex flex-wrap items-center gap-2 text-xs font-semibold">
          {canExport ? (
            <>
              <label className="inline-flex items-center gap-1.5 rounded-full border border-slate-200 bg-white px-2.5 py-1 text-slate-700">
                <Bell className="h-3.5 w-3.5 text-slate-500" />
                <SystemSelect
                  value={reminderPreset}
                  onChange={(event) => onReminderPresetChange(event.target.value as IcsReminderPreset)}
                  data-testid="case-calendar-ics-reminder-preset"
                  className="bg-transparent text-xs font-semibold text-slate-700 outline-none"
                >
                  {ICS_REMINDER_PRESET_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </SystemSelect>
              </label>
              <button
                type="button"
                onClick={onExport}
                disabled={exporting || loading}
                className="inline-flex items-center justify-center gap-1.5 rounded-full border border-slate-200 bg-white px-3 py-1 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Download className={`h-3.5 w-3.5 ${exporting ? 'animate-pulse' : ''}`} />
                {exporting ? 'ICS...' : 'ICS'}
              </button>
            </>
          ) : null}
          <span className="rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-slate-600">{totalEventCount} событий</span>
          {proceduralDeadlineCount > 0 ? <span className="rounded-full border border-sky-200 bg-sky-50 px-2.5 py-1 text-sky-700">Сроков: {proceduralDeadlineCount}</span> : null}
          {courtEventCount > 0 ? <span className="rounded-full border border-indigo-200 bg-indigo-50 px-2.5 py-1 text-indigo-700">Судов: {courtEventCount}</span> : null}
          {warningCount > 0 ? <span className="rounded-full border border-amber-200 bg-amber-50 px-2.5 py-1 text-amber-700">Близко: {warningCount}</span> : null}
          {overdueCount > 0 ? <span className="rounded-full border border-rose-200 bg-rose-50 px-2.5 py-1 text-rose-700">Просрочено: {overdueCount}</span> : null}
        </div>
      </div>

      {loading ? (
        <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-center text-sm text-slate-500">
          Загружаем сроки и судебные события...
        </div>
      ) : visibleEvents.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-center text-sm text-slate-500">
          На выбранный период сроков и судебных событий нет.
        </div>
      ) : (
        <div className="mt-4 grid gap-2 lg:grid-cols-2 2xl:grid-cols-3">
          {visibleEvents.map((event) => (
            <article key={event.id} className={`rounded-xl border px-3 py-3 ${getCaseCalendarEventTone(event)}`}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate text-sm font-semibold">{event.title}</div>
                  <div className="mt-1 truncate text-xs opacity-80">{event.caseTitle}</div>
                </div>
                <span className="shrink-0 rounded-full bg-white/80 px-2 py-1 text-xs font-semibold text-slate-700">
                  {formatCaseCalendarEventDate(event, timeZoneId)}
                </span>
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs font-semibold">
                <span className="rounded-full bg-white/70 px-2 py-1 text-slate-700">{getCaseCalendarEventKindLabel(event.kind)}</span>
                {event.courtName ? <span className="rounded-full bg-white/70 px-2 py-1 text-slate-700">{event.courtName}</span> : null}
                {event.responsibleName ? <span className="rounded-full bg-white/70 px-2 py-1 text-slate-700">{event.responsibleName}</span> : null}
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  );
}

function getCaseCalendarEventKindLabel(kind: string) {
  switch (kind) {
    case 'court':
      return 'Суд';
    case 'procedural-deadline':
      return 'Процессуальный срок';
    case 'case-deadline':
      return 'Срок дела';
    default:
      return kind;
  }
}

function getCaseCalendarEventTone(event: CaseCalendarEvent) {
  if (event.isOverdue) {
    return 'border-rose-200 bg-rose-50 text-rose-900';
  }

  if (event.isWarning) {
    return 'border-amber-200 bg-amber-50 text-amber-900';
  }

  if (event.kind === 'court') {
    return 'border-indigo-200 bg-indigo-50 text-indigo-900';
  }

  return 'border-slate-200 bg-slate-50 text-slate-900';
}

function formatCaseCalendarEventDate(event: CaseCalendarEvent, timeZoneId: string) {
  const start = new Date(event.startAt);
  if (Number.isNaN(start.getTime())) {
    return 'Дата не задана';
  }

  if (event.allDay) {
    return getTaskDateLabel(start, timeZoneId) || start.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' });
  }

  return `${getTaskDateLabel(start, timeZoneId) || start.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' })} ${formatTimeOnly(start, timeZoneId)}`;
}

function TaskTimelineCalendar({
  tasks,
  timeZoneId,
  colorMode,
  completedTaskCalendarAction,
  anchorDate,
  highlightedTaskId,
  loading,
  assigneeFilter,
  onPrevious,
  onNext,
  onToday,
  onCreateDate,
  onMoveTask,
  onEdit,
  onDone,
  onReviewApproval,
  currentUserId,
  isAdminUser,
  taskSettings,
  taskPermissions,
  movingTaskId,
  readOnly,
}: {
  tasks: TaskJournalItem[];
  timeZoneId: string;
  colorMode: TaskCalendarColorMode;
  completedTaskCalendarAction: 'color' | 'hide';
  anchorDate: Date;
  highlightedTaskId: number | null;
  loading: boolean;
  assigneeFilter: string;
  onPrevious: () => void;
  onNext: () => void;
  onToday: () => void;
  onCreateDate?: (date: Date) => void;
  onMoveTask: (task: TaskJournalItem, dueAt: Date) => void;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
  onReviewApproval: (task: TaskJournalItem, participant: TaskJournalParticipant) => void;
  currentUserId: number | null;
  isAdminUser: boolean;
  taskSettings: TaskJournalSettingsDto;
  taskPermissions: TaskSectionPermissions;
  movingTaskId: number | null;
  readOnly?: boolean;
}) {
  const visibleDays = useMemo(() => {
    const start = startOfWeek(anchorDate);
    return Array.from({ length: 7 }, (_, index) => addDays(start, index));
  }, [anchorDate]);
  const visibleDayKeys = useMemo(() => new Set(visibleDays.map(toLocalDateKey)), [visibleDays]);
  const hourLabels = useMemo(getTimelineHourLabels, []);
  const timelineHeight = (TIMELINE_END_HOUR - TIMELINE_START_HOUR) * TIMELINE_HOUR_HEIGHT;

  const tasksByDate = useMemo(() => {
    const map = new Map<string, TaskJournalItem[]>();
    tasks.forEach((task) => {
      const key = getTaskDateKey(task.dueAt, timeZoneId);
      if (!visibleDayKeys.has(key)) {
        return;
      }

      const items = map.get(key) ?? [];
      items.push(task);
      map.set(key, items);
    });

    map.forEach((items) => {
      items.sort((a, b) => getTaskStartMinute(a) - getTaskStartMinute(b) || a.id - b.id);
    });

    return map;
  }, [tasks, timeZoneId, visibleDayKeys]);

  const weekTasks = useMemo(
    () => visibleDays.flatMap((day) => tasksByDate.get(toLocalDateKey(day)) ?? []),
    [tasksByDate, visibleDays],
  );

  const weekWorkload = useMemo(() => {
    const byAssignee = new Map<string, { name: string; count: number; duration: number; done: number }>();
    weekTasks.forEach((task) => {
      const names = getTaskAssigneeNames(task);
      names.forEach((name) => {
        const item = byAssignee.get(name) ?? { name, count: 0, duration: 0, done: 0 };
        item.count += 1;
        item.duration += normalizeTaskDuration(task.plannedDurationMinutes);
        if (task.status === 'done') {
          item.done += 1;
        }
        byAssignee.set(name, item);
      });
    });

    return Array.from(byAssignee.values())
      .sort((a, b) => b.duration - a.duration || b.count - a.count || a.name.localeCompare(b.name, 'ru'))
      .slice(0, 8);
  }, [weekTasks]);

  const timelineDayLayouts = useMemo(() => {
    const map = new Map<string, { placements: ReturnType<typeof buildTimelineTaskPlacements>; minWidth: number; laneCount: number }>();

    visibleDays.forEach((day) => {
      const key = toLocalDateKey(day);
      const placements = buildTimelineTaskPlacements(tasksByDate.get(key) ?? []);
      const laneCount = Math.max(1, ...placements.map((placement) => placement.laneCount));
      map.set(key, {
        placements,
        minWidth: getTimelineDayMinWidth(laneCount),
        laneCount,
      });
    });

    return map;
  }, [tasksByDate, visibleDays]);
  const timelineGridTemplateColumns = useMemo(
    () => `${TIMELINE_TIME_COLUMN_WIDTH}px ${visibleDays
      .map((day) => `minmax(${timelineDayLayouts.get(toLocalDateKey(day))?.minWidth ?? TIMELINE_DAY_MIN_WIDTH}px, 1fr)`)
      .join(' ')}`,
    [timelineDayLayouts, visibleDays],
  );
  const timelineMinWidth = useMemo(
    () => TIMELINE_TIME_COLUMN_WIDTH + visibleDays.reduce(
      (sum, day) => sum + (timelineDayLayouts.get(toLocalDateKey(day))?.minWidth ?? TIMELINE_DAY_MIN_WIDTH),
      0,
    ),
    [timelineDayLayouts, visibleDays],
  );

  const legend = colorMode === 'priority'
    ? [
      { label: 'Срочный приоритет', dotClassName: 'bg-rose-600' },
      { label: 'Высокий приоритет', dotClassName: 'bg-orange-500' },
      { label: 'Обычный приоритет', dotClassName: 'bg-sky-600' },
      { label: 'Низкий приоритет', dotClassName: 'bg-slate-500' },
    ]
    : colorMode === 'status'
      ? [
        { label: 'Новые', dotClassName: 'bg-sky-600' },
        { label: 'В работе', dotClassName: 'bg-blue-600' },
        { label: 'Ожидание', dotClassName: 'bg-amber-500' },
        { label: 'Согласование/доработка', dotClassName: 'bg-violet-600' },
        { label: 'Готово/согласовано', dotClassName: 'bg-emerald-600' },
        { label: 'Отменено', dotClassName: 'bg-slate-500' },
      ]
      : [
        { label: 'Выполненные/отмененные', dotClassName: 'bg-slate-500' },
        { label: 'Мои / выбранный исполнитель', dotClassName: 'bg-emerald-600' },
        { label: 'Высокий приоритет', dotClassName: 'bg-orange-500' },
        { label: 'Согласование/доработка', dotClassName: 'bg-violet-600' },
        { label: 'Просроченные', dotClassName: 'bg-rose-600' },
        { label: 'Остальные задачи', dotClassName: 'bg-sky-600' },
      ];

  const now = new Date();
  const nowParts = getZonedParts(now, activeTaskJournalTimeZoneId);
  const currentTaskDayKey = getCurrentTaskDayKey(now);
  const currentTimeTop =
    (((nowParts?.hour ?? now.getHours()) * 60 + (nowParts?.minute ?? now.getMinutes())) - TIMELINE_START_MINUTE) * TIMELINE_MINUTE_HEIGHT;
  const showCurrentTime = visibleDayKeys.has(currentTaskDayKey)
    && currentTimeTop >= 0
    && currentTimeTop <= timelineHeight;

  return (
    <section className="rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-5 py-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">
            <CalendarClock className="h-4 w-4" />
            Календарь нагрузки
          </div>
          <h2 className="mt-2 text-xl font-semibold text-slate-900">{formatPeriodTitle(anchorDate, 'week')}</h2>
          <p className="mt-1 text-sm text-slate-500">
            {weekTasks.length > 0 ? `В недельной шкале задач: ${weekTasks.length}` : 'На неделе задач нет'}
          </p>
          <div className="mt-3 flex flex-wrap gap-2">
            {legend.map((item) => (
              <span key={item.label} className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-600">
                <span className={`h-2.5 w-2.5 rounded-full ${item.dotClassName}`} />
                {item.label}
              </span>
            ))}
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={onPrevious}
            className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-500 transition hover:bg-slate-50 hover:text-slate-800"
            aria-label="Предыдущая неделя"
            title="Предыдущая неделя"
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
            aria-label="Следующая неделя"
            title="Следующая неделя"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
        </div>
      </div>

      {loading ? (
        <div className="px-6 py-12 text-center text-sm text-slate-500">Загружаем календарь нагрузки...</div>
      ) : (
        <div className="p-4">
          {weekWorkload.length > 0 ? (
            <div className="mb-4 flex flex-wrap gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-3">
              {weekWorkload.map((item) => (
                <span key={item.name} className="inline-flex items-center gap-1.5 rounded-full bg-white px-3 py-1.5 text-xs font-semibold text-slate-600 ring-1 ring-slate-200">
                  {item.name}: {item.count} · {formatDuration(item.duration)}
                  {item.done > 0 ? ` · готово ${item.done}` : ''}
                </span>
              ))}
            </div>
          ) : null}

          <div className="overflow-x-auto rounded-xl border border-slate-200">
            <div style={{ minWidth: timelineMinWidth }}>
              <div
                data-testid="task-timeline-header-grid"
                className="grid border-b border-slate-200 bg-slate-50"
                style={{ gridTemplateColumns: timelineGridTemplateColumns }}
              >
                <div className="px-3 py-3 text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">{timeZoneId}</div>
                {visibleDays.map((day) => {
                  const key = toLocalDateKey(day);
                  const dayTasks = tasksByDate.get(key) ?? [];
                  const dayDuration = dayTasks
                    .filter((task) => isTaskJournalActive(task.status))
                    .reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0);
                  return (
                    <div key={key} className="border-l border-slate-200 px-3 py-3 text-center">
                      <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">{WEEKDAY_LABELS[visibleDays.indexOf(day)]}</div>
                      <div className="mt-1 text-2xl font-semibold text-slate-900">{day.getDate()}</div>
                      <div className="mt-1 text-xs text-slate-500">{dayTasks.length} · {formatDuration(dayDuration)}</div>
                      {onCreateDate ? (
                        <button
                          type="button"
                          onClick={() => onCreateDate(day)}
                          className="mt-2 inline-flex h-7 items-center justify-center rounded-lg border border-blue-200 bg-white px-2 text-xs font-semibold text-blue-700 transition hover:bg-blue-50"
                        >
                          <Plus className="mr-1 h-3.5 w-3.5" />
                          Задача
                        </button>
                      ) : null}
                    </div>
                  );
                })}
              </div>

              <div
                data-testid="task-timeline-body-grid"
                className="grid"
                style={{ gridTemplateColumns: timelineGridTemplateColumns }}
              >
                <div className="relative bg-slate-50" style={{ height: timelineHeight }}>
                  {hourLabels.map((hour, index) => (
                    <div
                      key={hour}
                      className="absolute left-0 right-0 -translate-y-2 px-3 text-right text-xs font-semibold text-slate-500"
                      style={{ top: index * TIMELINE_HOUR_HEIGHT }}
                    >
                      {String(hour).padStart(2, '0')}:00
                    </div>
                  ))}
                </div>

                {visibleDays.map((day) => {
                  const key = toLocalDateKey(day);
                  const placements = timelineDayLayouts.get(key)?.placements ?? [];
                  const isToday = key === currentTaskDayKey;

                  return (
                    <div
                      key={key}
                      data-testid={`task-timeline-day-${key}`}
                      className={`relative border-l border-slate-200 ${isToday ? 'bg-blue-50/30' : 'bg-white'}`}
                      style={{ height: timelineHeight }}
                      onDragOver={(event) => {
                        if (!readOnly && (event.dataTransfer.types.includes('application/x-task-id') || event.dataTransfer.types.includes('text/plain'))) {
                          event.preventDefault();
                          event.dataTransfer.dropEffect = 'move';
                        }
                      }}
                      onDrop={(event) => {
                        event.preventDefault();
                        if (readOnly) return;
                        const taskId = Number(event.dataTransfer.getData('application/x-task-id') || event.dataTransfer.getData('text/plain'));
                        const task = tasks.find((item) => item.id === taskId);
                        if (!task) {
                          return;
                        }

                        const rect = event.currentTarget.getBoundingClientRect();
                        onMoveTask(task, buildTimelineDropDate(day, event.clientY - rect.top));
                      }}
                    >
                      {hourLabels.slice(0, -1).map((hour, index) => (
                        <div
                          key={hour}
                          className="absolute left-0 right-0 border-t border-slate-200"
                          style={{ top: index * TIMELINE_HOUR_HEIGHT }}
                        />
                      ))}
                      {showCurrentTime && isToday ? (
                        <div className="absolute left-0 right-0 z-20 border-t-2 border-rose-500" style={{ top: currentTimeTop }}>
                          <span className="absolute -top-2 left-1 rounded-full bg-rose-500 px-1.5 py-0.5 text-[10px] font-semibold text-white">сейчас</span>
                        </div>
                      ) : null}

                      {placements.map(({ task, top, height, lane, laneCount }) => {
                        const tone = getTimelineTaskTone(task, currentUserId, assigneeFilter, colorMode, completedTaskCalendarAction);
                        const isReadOnly = readOnly || isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
                        const canCompleteTask = canCompleteTaskForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
                        const isAwaitingApproval = isTaskAwaitingApproval(task);
                        const reviewableApprovalParticipant = getReviewableApprovalParticipant(task, currentUserId);
                        const useSeparateApprovalAction = isAwaitingApproval
                          && taskSettings.approvalQuickActionMode === 'separateButton';
                        const approvalCompletionDisabled = isAwaitingApproval
                          && (taskSettings.approvalQuickActionMode === 'separateButton'
                            || !reviewableApprovalParticipant);
                        const start = new Date(task.dueAt);
                        const end = new Date(start.getTime() + normalizeTaskDuration(task.plannedDurationMinutes) * 60 * 1000);
                        const width = `calc(${100 / laneCount}% - ${TIMELINE_TASK_LANE_GAP}px)`;
                        const left = `calc(${(lane * 100) / laneCount}% + ${TIMELINE_TASK_LANE_GAP / 2}px)`;
                        const isCompactTask = height < 52;

                        return (
                          <article
                            key={task.id}
                            draggable={isTaskJournalActive(task.status) && !isReadOnly}
                            data-testid={`task-timeline-task-${task.id}`}
                            onDragStart={(event) => {
                              if (isReadOnly) {
                                event.preventDefault();
                                return;
                              }
                              event.dataTransfer.setData('application/x-task-id', String(task.id));
                              event.dataTransfer.setData('text/plain', String(task.id));
                              event.dataTransfer.effectAllowed = 'move';
                            }}
                            className={`absolute z-10 min-w-[7.25rem] overflow-hidden rounded-lg border-l-4 px-2 text-left text-xs shadow-sm transition hover:z-30 hover:ring-2 hover:ring-blue-200 ${isCompactTask ? 'py-1' : 'py-1.5'} ${tone.className} ${
                              movingTaskId === task.id ? 'opacity-60' : ''
                            } ${highlightedTaskId === task.id ? 'ring-2 ring-blue-300' : ''}`}
                            style={{ top, height, left, width }}
                            title={`${task.title}\n${formatTimeOnly(start)}-${formatTimeOnly(end)}\nИсполнитель: ${getTaskAssigneeNames(task).join(', ') || task.assigneeName}\nПоручил: ${getTaskAuthorName(task)}`}
                          >
                            <div className="flex h-full items-start justify-between gap-1">
                              <button type="button" onClick={() => onEdit(task)} className="min-w-0 flex-1 text-left">
                                <span className="block truncate font-bold uppercase leading-4">{task.title}</span>
                                <span className={`block truncate font-semibold opacity-90 ${isCompactTask ? 'mt-0' : 'mt-0.5'}`}>{formatTimeOnly(start)}-{formatTimeOnly(end)}</span>
                                {!isCompactTask ? (
                                  <span className="mt-0.5 block truncate opacity-90">{getTaskAssigneeNames(task).join(', ')}</span>
                                ) : null}
                              </button>
                              {!isReadOnly && useSeparateApprovalAction ? (
                                <button
                                  type="button"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    if (reviewableApprovalParticipant) {
                                      onReviewApproval(task, reviewableApprovalParticipant);
                                    }
                                  }}
                                  disabled={!reviewableApprovalParticipant}
                                  className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-md bg-white/20 text-white transition hover:bg-white/30 disabled:cursor-not-allowed disabled:opacity-40"
                                  title={reviewableApprovalParticipant ? 'Рассмотреть согласование' : 'Задача находится на согласовании у другого сотрудника'}
                                  aria-label="Рассмотреть согласование"
                                >
                                  <Scale className="h-3.5 w-3.5" />
                                </button>
                              ) : null}
                              {!isReadOnly && (canCompleteTask || isAwaitingApproval) ? (
                                <button
                                  type="button"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    onDone(task);
                                  }}
                                  disabled={approvalCompletionDisabled}
                                  className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-md bg-white/20 text-white transition hover:bg-white/30 disabled:cursor-not-allowed disabled:opacity-40"
                                  title={approvalCompletionDisabled ? 'Задача находится на согласовании' : isAwaitingApproval ? 'Открыть окно согласования' : 'Завершить'}
                                  aria-label={isAwaitingApproval ? 'Действие по согласованию' : 'Завершить задачу'}
                                >
                                  <CheckCircle2 className="h-3.5 w-3.5" />
                                </button>
                              ) : null}
                            </div>
                          </article>
                        );
                      })}
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}

function CalendarTaskTooltip({ task }: { task: TaskJournalItem }) {
  const lines = buildTaskTooltipLines(task);
  return (
    <span
      data-testid={`task-calendar-tooltip-${task.id}`}
      className="pointer-events-none absolute left-2 right-2 top-full z-40 mt-1 hidden rounded-lg border border-slate-200 bg-white p-2 text-left text-xs font-normal text-slate-600 shadow-lg group-hover:block group-focus-visible:block"
    >
      <span className="block font-semibold text-slate-900">{lines[0]}</span>
      {lines.slice(1).map((line) => (
        <span key={line} className="mt-1 block leading-4">{line}</span>
      ))}
    </span>
  );
}

function TaskCalendar({
  tasks,
  timeZoneId,
  mode,
  colorMode,
  completedTaskCalendarAction,
  loadWarningMode,
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
  onReviewApproval,
  currentUserId,
  isAdminUser,
  taskSettings,
  taskPermissions,
  movingTaskId,
  resizingTaskId,
  readOnly,
}: {
  tasks: TaskJournalItem[];
  timeZoneId: string;
  mode: PeriodMode;
  colorMode: TaskCalendarColorMode;
  completedTaskCalendarAction: 'color' | 'hide';
  loadWarningMode: TaskCalendarLoadWarningMode;
  anchorDate: Date;
  selectedDate: Date;
  highlightedTaskId: number | null;
  loading: boolean;
  onPrevious: () => void;
  onNext: () => void;
  onToday: () => void;
  onSelectDate: (date: Date) => void;
  onCreateDate?: (date: Date) => void;
  onMoveTask: (task: TaskJournalItem, date: Date) => void;
  onResizeTask: (task: TaskJournalItem, deltaMinutes: -15 | 15) => void;
  onEdit: (task: TaskJournalItem) => void;
  onDone: (task: TaskJournalItem) => void;
  onReviewApproval: (task: TaskJournalItem, participant: TaskJournalParticipant) => void;
  currentUserId: number | null;
  isAdminUser: boolean;
  taskSettings: TaskJournalSettingsDto;
  taskPermissions: TaskSectionPermissions;
  movingTaskId: number | null;
  resizingTaskId: number | null;
  readOnly?: boolean;
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
      const key = getTaskDateKey(task.dueAt, timeZoneId);
      const items = map.get(key) ?? [];
      items.push(task);
      map.set(key, items);
    });

    map.forEach((items) => {
      items.sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
    });

    return map;
  }, [tasks, timeZoneId]);

  const selectedTasks = useMemo(
    () => tasksByDate.get(toLocalDateKey(selectedDate)) ?? [],
    [selectedDate, tasksByDate],
  );
  const loadWarningThresholds = useMemo(
    () => getTaskCalendarLoadWarningThresholds(loadWarningMode),
    [loadWarningMode],
  );
  const conflictTaskIds = useMemo(() => {
    const ids = new Set<number>();
    tasksByDate.forEach((items) => {
      buildTaskConflictTaskIds(items).forEach((id) => ids.add(id));
    });

    return ids;
  }, [tasksByDate]);
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
          hasConflict:
            item.count >= loadWarningThresholds.assigneeWarningTaskCount ||
            item.duration >= loadWarningThresholds.assigneeWarningDurationMinutes ||
            overlaps > 0,
        };
      })
      .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name, 'ru'));
  }, [loadWarningThresholds, selectedTasks]);
  const periodTaskCount = visibleDays.reduce((count, day) => count + (tasksByDate.get(toLocalDateKey(day))?.length ?? 0), 0);
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
            <div data-testid="task-calendar-mobile-strip" className="mb-3 md:hidden">
              <div className="flex gap-2 overflow-x-auto pb-1">
                {visibleDays.map((day) => {
                  const key = toLocalDateKey(day);
                  const dayTasks = tasksByDate.get(key) ?? [];
                  const activeDayTasks = dayTasks.filter((task) => isTaskJournalActive(task.status));
                  const activeDayDuration = activeDayTasks.reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0);
                  const hasConflict = dayTasks.some((task) => conflictTaskIds.has(task.id));
                  const hasHighLoad =
                    activeDayTasks.length >= loadWarningThresholds.warningTaskCount ||
                    activeDayDuration >= loadWarningThresholds.warningDurationMinutes;
                  const isSelected = isSameDay(day, selectedDate);
                  const isCurrentMonth = mode !== 'month' || isSameMonth(day, anchorDate);

                  return (
                    <button
                      key={key}
                      type="button"
                      onClick={() => onSelectDate(day)}
                      aria-label={`Выбрать ${formatDateOnly(day)}`}
                      className={`min-w-[5.75rem] rounded-xl border px-3 py-2 text-left shadow-sm transition ${
                        isSelected
                          ? 'border-blue-300 bg-blue-50 text-blue-800 ring-2 ring-blue-100'
                          : hasConflict
                            ? 'border-rose-200 bg-rose-50 text-rose-700'
                            : hasHighLoad
                              ? 'border-amber-200 bg-amber-50 text-amber-700'
                              : isCurrentMonth
                                ? 'border-slate-200 bg-white text-slate-700'
                                : 'border-slate-100 bg-slate-50 text-slate-400'
                      }`}
                    >
                      <span className="block text-xs font-semibold uppercase">{WEEKDAY_LABELS[day.getDay() === 0 ? 6 : day.getDay() - 1]}</span>
                      <span className="mt-1 block text-lg font-semibold">{day.getDate()}</span>
                      <span className="mt-1 block text-xs font-medium">
                        {hasConflict ? 'Конфликт' : dayTasks.length > 0 ? `${dayTasks.length} задач` : 'Свободно'}
                      </span>
                    </button>
                  );
                })}
              </div>
            </div>

            {mode !== 'day' ? (
              <div className="mb-2 grid min-w-[860px] grid-cols-7 gap-2 text-center text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">
                {WEEKDAY_LABELS.map((label) => (
                  <div key={label}>{label}</div>
                ))}
              </div>
            ) : null}

            <div className={`grid gap-2 ${calendarMinWidthClass} ${calendarGridClass}`}>
              {visibleDays.map((day) => {
                const key = toLocalDateKey(day);
                const dayTasks = tasksByDate.get(key) ?? [];
                const isSelected = isSameDay(day, selectedDate);
                const isCurrentMonth = mode !== 'month' || isSameMonth(day, anchorDate);
                const isCurrentDay = key === getCurrentTaskDayKey();
                const activeDayTasks = dayTasks.filter((task) => isTaskJournalActive(task.status));
                const activeDayDuration = activeDayTasks.reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0);
                const dayConflictCount = dayTasks.filter((task) => conflictTaskIds.has(task.id)).length;
                const hasCriticalLoad =
                  activeDayTasks.length >= loadWarningThresholds.criticalTaskCount ||
                  activeDayDuration >= loadWarningThresholds.criticalDurationMinutes;
                const hasHighLoad =
                  activeDayTasks.length >= loadWarningThresholds.warningTaskCount ||
                  activeDayDuration >= loadWarningThresholds.warningDurationMinutes;
                const dayLoadClass =
                  hasCriticalLoad
                    ? 'border-rose-200 bg-rose-50/70'
                    : hasHighLoad
                      ? 'border-amber-200 bg-amber-50/70'
                      : '';

                return (
                  <div
                    key={key}
                    data-testid={`task-calendar-day-${key}`}
                    onDragOver={(event) => {
                      if (!readOnly && (event.dataTransfer.types.includes('application/x-task-id') || event.dataTransfer.types.includes('text/plain'))) {
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                      }
                    }}
                    onDrop={(event) => {
                      event.preventDefault();
                      if (readOnly) return;
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
                        {dayConflictCount > 0 ? (
                          <span className="mt-0.5 block text-[11px] font-semibold text-rose-600">
                            Конфликт
                          </span>
                        ) : hasHighLoad ? (
                          <span className={`mt-0.5 block text-[11px] font-semibold ${hasCriticalLoad ? 'text-rose-600' : 'text-amber-600'}`}>
                            Высокая загрузка
                          </span>
                        ) : null}
                      </button>
                      <span className="flex shrink-0 items-center gap-1.5">
                        {onCreateDate ? (
                          <button
                            type="button"
                            onClick={() => onCreateDate(day)}
                            className="inline-flex h-7 w-7 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-500 transition hover:border-blue-200 hover:text-blue-600"
                            title="Создать задачу на дату"
                            aria-label="Создать задачу на дату"
                          >
                            <Plus className="h-3.5 w-3.5" />
                          </button>
                        ) : null}
                        {isCurrentDay ? <span className="h-2 w-2 rounded-full bg-blue-500" /> : null}
                        {dayTasks.length > 0 ? (
                          <span className="rounded-full bg-slate-900 px-2 py-0.5 text-xs font-semibold text-white">{dayTasks.length}</span>
                        ) : null}
                      </span>
                    </div>

                    <div className="mt-2 space-y-1.5">
                      {dayTasks.slice(0, mode === 'day' ? 8 : 3).map((task) => {
                        const isReadOnly = readOnly || isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
                        const hasTimeConflict = conflictTaskIds.has(task.id);
                        const calendarCardTone = getTaskCalendarCardTone(task, colorMode, completedTaskCalendarAction);
                        const tooltipTitle = [
                          ...buildTaskTooltipLines(task),
                          ...(hasTimeConflict ? ['Конфликт: задача пересекается по времени с другой задачей исполнителя.'] : []),
                          ...(isReadOnly ? [] : ['Перетащите карточку на другой день, чтобы перенести срок.']),
                        ].join('\n');
                        return (
                          <button
                            key={task.id}
                            type="button"
                            draggable={isTaskJournalActive(task.status) && !isReadOnly}
                            data-testid={`task-calendar-task-${task.id}`}
                            onDragStart={(event) => {
                              if (isReadOnly) {
                                event.preventDefault();
                                return;
                              }
                              event.dataTransfer.setData('application/x-task-id', String(task.id));
                              event.dataTransfer.setData('text/plain', String(task.id));
                              event.dataTransfer.effectAllowed = 'move';
                            }}
                            onClick={() => onEdit(task)}
                            title={tooltipTitle}
                            className={`group relative block w-full rounded-lg border px-2 py-1.5 text-left text-xs transition hover:border-blue-200 hover:bg-blue-50 ${
                              movingTaskId === task.id
                                ? 'border-blue-300 bg-blue-100 opacity-60'
                                : highlightedTaskId === task.id
                                  ? 'border-blue-300 bg-blue-50'
                                  : hasTimeConflict
                                    ? 'border-rose-200 bg-rose-50'
                                    : calendarCardTone
                            }`}
                          >
                            <span className="block truncate font-semibold text-slate-900">{task.title}</span>
                            <span className="mt-0.5 block truncate text-slate-500">{getTaskClientName(task)}</span>
                            <span className="mt-1 block text-[11px] font-semibold text-slate-400">{formatDuration(task.plannedDurationMinutes)}</span>
                            {hasTimeConflict ? (
                              <span className="mt-1 inline-flex rounded-full bg-rose-100 px-1.5 py-0.5 text-[10px] font-semibold text-rose-700">
                                Конфликт
                              </span>
                            ) : null}
                            <CalendarTaskTooltip task={task} />
                          </button>
                        );
                      })}
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
                {onCreateDate ? (
                  <button
                    type="button"
                    onClick={() => onCreateDate(selectedDate)}
                    className="inline-flex items-center gap-2 rounded-xl border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-50"
                  >
                    <Plus className="h-4 w-4" />
                    Создать
                  </button>
                ) : null}
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
                const isReadOnly = readOnly || isTaskReadOnlyForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
                const canCompleteTask = canCompleteTaskForCurrentUser(task, currentUserId, isAdminUser, taskSettings, taskPermissions);
                const isAwaitingApproval = isTaskAwaitingApproval(task);
                const reviewableApprovalParticipant = getReviewableApprovalParticipant(task, currentUserId);
                const useSeparateApprovalAction = isAwaitingApproval
                  && taskSettings.approvalQuickActionMode === 'separateButton';
                const approvalCompletionDisabled = isAwaitingApproval
                  && (taskSettings.approvalQuickActionMode === 'separateButton'
                    || !reviewableApprovalParticipant);
                const isOverdue = isTaskOverdue(task);
                return (
                  <article key={task.id} className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
                    <div className="flex items-start justify-between gap-3">
                      <button type="button" onClick={() => onEdit(task)} className="min-w-0 text-left">
                        <div className="truncate text-sm font-semibold text-slate-900">{task.title}</div>
                        <div className="mt-1 truncate text-xs text-slate-500">{getTaskClientName(task)}</div>
                        {getTaskCaseTitle(task) ? <div className="mt-0.5 truncate text-xs text-slate-500">{getTaskCaseTitle(task)}</div> : null}
                      </button>
                      <div className="shrink-0 text-right">
                        <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusClass(task.status)}`}>
                          {getStatusLabel(task.status)}
                        </span>
                        {isOverdue ? <div className="mt-1 text-[11px] font-semibold text-rose-600">Просрочена</div> : null}
                      </div>
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
                        disabled={isReadOnly || resizingTaskId === task.id || normalizeTaskDuration(task.plannedDurationMinutes) <= 15}
                        className="inline-flex h-9 flex-1 items-center justify-center rounded-xl border border-slate-200 bg-white text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                        title="Уменьшить длительность на 15 минут"
                      >
                        −15 мин
                      </button>
                      <button
                        type="button"
                        onClick={() => onResizeTask(task, 15)}
                        disabled={isReadOnly || resizingTaskId === task.id || normalizeTaskDuration(task.plannedDurationMinutes) >= 480}
                        className="inline-flex h-9 flex-1 items-center justify-center rounded-xl border border-blue-200 bg-white text-sm font-semibold text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-50"
                        title="Увеличить длительность на 15 минут"
                      >
                        +15 мин
                      </button>
                    </div>
                    {!isReadOnly && useSeparateApprovalAction ? (
                      <button
                        type="button"
                        onClick={() => reviewableApprovalParticipant && onReviewApproval(task, reviewableApprovalParticipant)}
                        disabled={!reviewableApprovalParticipant}
                        className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-violet-200 px-3 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-50 disabled:cursor-not-allowed disabled:border-slate-200 disabled:text-slate-400 disabled:hover:bg-transparent"
                        title={reviewableApprovalParticipant ? 'Рассмотреть согласование' : 'Задача находится на согласовании у другого сотрудника'}
                      >
                        <Scale className="h-4 w-4" />
                        Рассмотреть согласование
                      </button>
                    ) : null}
                    {!isReadOnly && (canCompleteTask || isAwaitingApproval) ? (
                      <button
                        type="button"
                        onClick={() => onDone(task)}
                        disabled={approvalCompletionDisabled}
                        className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-50 disabled:cursor-not-allowed disabled:border-slate-200 disabled:text-slate-400 disabled:hover:bg-transparent"
                        title={approvalCompletionDisabled ? 'Задача находится на согласовании' : isAwaitingApproval ? 'Открыть окно согласования' : 'Завершить'}
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

function TaskDaySchedulePanel({
  tasks,
  selectedDueAt,
  plannedDurationMinutes,
  assigneeUserId,
  assigneeName,
  currentTaskId,
}: {
  tasks: TaskJournalItem[];
  selectedDueAt: string;
  plannedDurationMinutes: number | null | undefined;
  assigneeUserId?: number | null;
  assigneeName?: string;
  currentTaskId: number | null;
}) {
  const selectedDate = useMemo(() => parseLocalInputDate(selectedDueAt), [selectedDueAt]);
  const plannedDuration = normalizeTaskDuration(plannedDurationMinutes);
  const assigneeLabel = assigneeName?.trim() || 'Исполнитель не выбран';
  const hasAssigneeFilter = Boolean(assigneeUserId || assigneeName?.trim());

  const dayTasks = useMemo(() => {
    if (!selectedDate) {
      return [];
    }

    return tasks
      .filter((task) => {
        const due = new Date(task.dueAt);
        return !Number.isNaN(due.getTime()) && isSameTaskDay(task.dueAt, selectedDate);
      })
      .sort((a, b) => getTaskStartMinute(a) - getTaskStartMinute(b) || a.id - b.id);
  }, [selectedDate, tasks]);

  const activeDayTasks = useMemo(() => dayTasks.filter((task) => isTaskJournalActive(task.status)), [dayTasks]);
  const assigneeDayTasks = useMemo(
    () => dayTasks.filter((task) => taskMatchesAssignee(task, assigneeUserId, assigneeName)),
    [assigneeName, assigneeUserId, dayTasks],
  );
  const activeAssigneeDayTasks = useMemo(
    () => assigneeDayTasks.filter((task) => isTaskJournalActive(task.status)),
    [assigneeDayTasks],
  );
  const visibleScheduleTasks = hasAssigneeFilter ? assigneeDayTasks : dayTasks;
  const activeVisibleScheduleTasks = hasAssigneeFilter ? activeAssigneeDayTasks : activeDayTasks;
  const scheduleOwnerLabel = hasAssigneeFilter ? assigneeLabel : 'Все исполнители';

  const selectedStartMinute = selectedDate ? getMinuteOfDay(selectedDate) : null;
  const selectedEndMinute = selectedStartMinute === null ? null : selectedStartMinute + plannedDuration;
  const conflictSource = hasAssigneeFilter ? activeAssigneeDayTasks : activeDayTasks;
  const conflicts = selectedStartMinute === null || selectedEndMinute === null
    ? []
    : conflictSource.filter((task) =>
      task.id !== currentTaskId &&
      intervalsOverlap(selectedStartMinute, selectedEndMinute, getTaskStartMinute(task), getTaskEndMinute(task)));
  const visibleScheduleLoadMinutes = activeVisibleScheduleTasks.reduce((sum, task) => sum + normalizeTaskDuration(task.plannedDurationMinutes), 0);

  if (!selectedDate) {
    return (
      <section data-testid="task-day-schedule-panel" className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        Выберите дату и время, чтобы увидеть расписание и загрузку на день.
      </section>
    );
  }

  return (
    <section data-testid="task-day-schedule-panel" className="rounded-2xl border border-blue-100 bg-blue-50/40 p-4">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div>
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.14em] text-blue-700">
            <CalendarClock className="h-4 w-4" />
            Расписание на день
          </div>
          <h3 className="mt-1 text-base font-semibold text-slate-900">
            {formatDateOnly(selectedDate)}, слот {formatTimeRange(selectedDate, plannedDuration)}
          </h3>
          <p className="mt-1 text-sm text-slate-500">
            {scheduleOwnerLabel}: {activeVisibleScheduleTasks.length} активных задач · {formatDuration(visibleScheduleLoadMinutes)}
          </p>
        </div>
        <div className="flex flex-wrap gap-2 text-xs font-semibold">
          <span className="rounded-full bg-white px-3 py-1 text-slate-600 ring-1 ring-slate-200">
            Всего: {visibleScheduleTasks.length}
          </span>
          <span className="rounded-full bg-white px-3 py-1 text-slate-600 ring-1 ring-slate-200">
            Активных: {activeVisibleScheduleTasks.length} · {formatDuration(visibleScheduleLoadMinutes)}
          </span>
          <span className={`rounded-full px-3 py-1 ring-1 ${conflicts.length > 0 ? 'bg-rose-50 text-rose-700 ring-rose-200' : 'bg-emerald-50 text-emerald-700 ring-emerald-200'}`}>
            {conflicts.length > 0 ? `Пересечения: ${conflicts.length}` : 'Свободно'}
          </span>
        </div>
      </div>

      {visibleScheduleTasks.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-blue-200 bg-white/70 px-4 py-5 text-sm text-slate-500">
          {hasAssigneeFilter ? 'На выбранный день у выбранного исполнителя задач пока нет.' : 'На выбранный день задач пока нет.'}
        </div>
      ) : (
        <div className="mt-4 max-h-64 space-y-2 overflow-y-auto pr-1">
          {visibleScheduleTasks.map((task) => {
            const start = new Date(task.dueAt);
            const duration = normalizeTaskDuration(task.plannedDurationMinutes);
            const isCurrent = task.id === currentTaskId;
            const sameAssignee = taskMatchesAssignee(task, assigneeUserId, assigneeName);
            const isConflict = conflicts.some((item) => item.id === task.id);
            const assigneeNames = getTaskAssigneeNames(task).join(', ');
            const isOverdue = isTaskOverdue(task);

            return (
              <article
                key={task.id}
                className={`rounded-xl border bg-white px-3 py-2 shadow-sm ${
                  isConflict
                    ? 'border-rose-200 ring-1 ring-rose-200'
                    : sameAssignee
                      ? 'border-blue-200'
                      : 'border-slate-200'
                }`}
              >
                <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-semibold text-slate-900">{formatTimeRange(start, duration)}</span>
                      <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusClass(task.status)}`}>
                        {getStatusLabel(task.status)}
                      </span>
                      {isOverdue ? (
                        <span className="rounded-full bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-700">
                          Просрочена
                        </span>
                      ) : null}
                      {isCurrent ? (
                        <span className="rounded-full bg-blue-100 px-2 py-0.5 text-xs font-semibold text-blue-700">
                          Текущая
                        </span>
                      ) : null}
                    </div>
                    <p className="mt-1 truncate text-sm font-semibold text-slate-800">{task.title}</p>
                    <p className="mt-0.5 truncate text-xs text-slate-500">{assigneeNames}</p>
                  </div>
                  <span className={`shrink-0 rounded-full px-2.5 py-1 text-xs font-semibold ${sameAssignee ? 'bg-blue-50 text-blue-700' : 'bg-slate-50 text-slate-500'}`}>
                    {formatDuration(duration)}
                  </span>
                </div>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}

type TaskCardChrome = 'legacy' | 'modern';

function TaskBillingCreateWizard({
  counterpartyOptions,
  initialDefaults,
  canViewCases,
  onCancel,
  onContinue,
}: {
  counterpartyOptions: LookupSelectOption[];
  initialDefaults: Partial<TaskJournalUpsertPayload>;
  canViewCases: boolean;
  onCancel: () => void;
  onContinue: (defaults: Partial<TaskJournalUpsertPayload>) => void;
}) {
  const initialCounterparty = initialDefaults.clientId
    ? `client:${initialDefaults.clientId}`
    : initialDefaults.legalEntityId
      ? `legal:${initialDefaults.legalEntityId}`
      : '';
  const [counterparty, setCounterparty] = useState(initialCounterparty);
  const [clientCaseId, setClientCaseId] = useState(initialDefaults.clientCaseId ? String(initialDefaults.clientCaseId) : '');
  const [step, setStep] = useState<'binding' | 'billing'>('binding');
  const [billingContext, setBillingContext] = useState<TaskBillingContext | null>(null);
  const [selectedContractId, setSelectedContractId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [kind, rawId] = counterparty.split(':');
  const ownerId = Number(rawId) || null;
  const clientId = kind === 'client' ? ownerId : null;
  const legalEntityId = kind === 'legal' ? ownerId : null;
  const { cases, loading: casesLoading } = useClientCases(clientId ?? undefined, {
    enabled: canViewCases && Boolean(ownerId),
    legalEntityId: legalEntityId ?? undefined,
  });
  const availableCases = cases.filter((item) => !isClosedClientCase(item));
  const selectedCase = availableCases.find((item) => String(item.id) === clientCaseId) ?? null;
  const selectedCounterparty = counterpartyOptions.find((item) => item.value === counterparty) ?? null;
  const selectedContract = billingContext?.contracts.find((item) => String(item.id) === selectedContractId)
    ?? billingContext?.contracts[0]
    ?? null;

  const loadBilling = async () => {
    if (!ownerId || !clientCaseId) {
      setError('Сначала выберите клиента и дело.');
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const context = await apiService.getTaskBillingContext({
        clientId: clientId ?? undefined,
        legalEntityId: legalEntityId ?? undefined,
        clientCaseId: Number(clientCaseId),
      });
      setBillingContext(context);
      setSelectedContractId(context.contracts[0] ? String(context.contracts[0].id) : '');
      setStep('billing');
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось проверить договор по делу.'));
    } finally {
      setLoading(false);
    }
  };

  const complete = (classification: 'billable' | 'internal') => {
    const contract = classification === 'billable' ? selectedContract : null;
    const objectName = [selectedCounterparty?.label, selectedCase?.title].filter(Boolean).join(' - ');
    onContinue({
      ...initialDefaults,
      isInternal: false,
      clientId,
      legalEntityId,
      clientCaseId: selectedCase?.id ?? null,
      objectName,
      clientBillingClassification: classification,
      billingContractId: contract?.id ?? null,
      billingMode: contract?.billingMode ?? '',
    });
  };

  const mode = selectedContract?.billingMode;
  const warningTitle = mode === 'Actual'
    ? 'Договор с оплатой по факту'
    : mode === 'Fixed'
      ? 'Договор с фиксированной оплатой'
      : 'По делу нет договора — задача платная';
  const warningText = mode === 'Actual'
    ? 'Клиент оплачивает фактически выполненные задачи. Клиентская задача получит яркую метку и будет ожидать включения в счёт.'
    : mode === 'Fixed'
      ? 'Работа выполняется в рамках фиксированной суммы. Внимание: следите за объёмом услуг по этому делу.'
      : 'Новая клиентская задача без договора учитывается как платная и попадёт в контроль выставления счёта. Если работа не должна оплачиваться клиентом, выберите внутреннюю задачу.';

  return (
    <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-950/45 p-3" role="dialog" aria-modal="true" aria-labelledby="task-create-wizard-title">
      <div className="w-full max-w-2xl rounded-2xl border border-slate-200 bg-white p-4 shadow-2xl sm:p-5">
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="text-xs font-bold uppercase tracking-[0.16em] text-blue-600">Новая задача · шаг {step === 'binding' ? '1 из 2' : '2 из 2'}</div>
            <h2 id="task-create-wizard-title" className="mt-1 text-xl font-bold text-slate-950">
              {step === 'binding' ? 'Выберите клиента и дело' : 'Определите учёт задачи'}
            </h2>
          </div>
          <button type="button" onClick={onCancel} className="rounded-xl p-2 text-slate-400 hover:bg-slate-100" aria-label="Закрыть"><X className="h-5 w-5" /></button>
        </div>

        {step === 'binding' ? (
          <div className="mt-5 space-y-4">
            <LookupSelect
              label="Клиент"
              value={counterparty}
              options={counterpartyOptions}
              onChange={(value) => {
                setCounterparty(value);
                setClientCaseId('');
                setError(null);
              }}
              placeholder="Выберите клиента или юридическое лицо"
              searchPlaceholder="Поиск по ФИО, названию, ИНН, телефону или email"
              helperText="Начните вводить ФИО, название, ИНН, телефон или email."
              presentation="inline-search"
              required
            />
            <label className="block text-sm font-semibold text-slate-700">
              Дело
              <SystemSelect
                value={clientCaseId}
                onChange={(event) => { setClientCaseId(event.target.value); setError(null); }}
                disabled={!ownerId || casesLoading}
                className="mt-1.5 h-11 w-full rounded-xl border border-slate-200 bg-white px-3 text-sm outline-none focus:border-blue-400 disabled:bg-slate-50"
                data-testid="task-billing-wizard-case"
              >
                <option value="">{casesLoading ? 'Загрузка дел…' : 'Выберите дело'}</option>
                {availableCases.map((item) => <option key={item.id} value={item.id}>{item.title}</option>)}
              </SystemSelect>
            </label>
            {!casesLoading && ownerId && availableCases.length === 0 ? <div className="rounded-xl bg-amber-50 p-3 text-sm text-amber-800">У контрагента нет активных дел. Платную задачу можно создать после добавления дела, а неоплачиваемую — прямо сейчас с привязкой к контрагенту.</div> : null}
            {error ? <div className="rounded-xl bg-rose-50 p-3 text-sm text-rose-700">{error}</div> : null}
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:items-center sm:justify-between">
              <button
                type="button"
                onClick={() => onContinue({
                  ...initialDefaults,
                  isInternal: true,
                  clientId: null,
                  legalEntityId: null,
                  clientCaseId: null,
                  objectName: 'Внутренняя задача',
                  clientBillingClassification: 'internal',
                  billingContractId: null,
                  billingMode: '',
                })}
                className="rounded-xl px-3 py-2.5 text-left text-sm font-semibold text-slate-500 hover:bg-slate-100 hover:text-slate-800"
              >
                Создать общую внутреннюю задачу без клиента
              </button>
              <div className="flex flex-wrap justify-end gap-2">
                <button type="button" onClick={onCancel} className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600">Отмена</button>
                <button
                  type="button"
                  onClick={() => complete('internal')}
                  disabled={!ownerId}
                  className="inline-flex items-center gap-2 rounded-xl border border-slate-300 bg-slate-50 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-100 disabled:opacity-50"
                >
                  <ShieldAlert className="h-4 w-4" /> Неоплачиваемая задача
                </button>
                <button type="button" onClick={() => void loadBilling()} disabled={loading || !ownerId || !clientCaseId} className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white disabled:opacity-50">
                  {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : null} Проверить договор
                </button>
              </div>
            </div>
          </div>
        ) : (
          <div className="mt-5">
            {billingContext && billingContext.contracts.length > 1 ? (
              <label className="mb-3 block text-sm font-semibold text-slate-700">
                Договор
                <SystemSelect value={selectedContractId} onChange={(event) => setSelectedContractId(event.target.value)} className="mt-1.5 h-11 w-full rounded-xl border border-slate-200 bg-white px-3 text-sm">
                  {billingContext.contracts.map((contract) => <option key={contract.id} value={contract.id}>№{contract.number} · {contract.billingMode === 'Actual' ? 'по факту' : 'фиксированная сумма'}</option>)}
                </SystemSelect>
              </label>
            ) : null}
            <div className={`rounded-xl border p-4 ${mode === 'Actual' ? 'border-rose-200 bg-rose-50' : mode === 'Fixed' ? 'border-violet-200 bg-violet-50' : 'border-amber-200 bg-amber-50'}`}>
              <div className="font-bold text-slate-950">{warningTitle}</div>
              {selectedContract ? <div className="mt-1 text-sm font-semibold text-slate-700">Договор №{selectedContract.number}{selectedContract.title ? ` · ${selectedContract.title}` : ''}</div> : null}
              <p className="mt-2 text-sm leading-6 text-slate-700">{warningText}</p>
            </div>
            <div className="mt-4 grid gap-3 sm:grid-cols-2">
              <button type="button" onClick={() => complete('billable')} className="min-h-36 rounded-2xl border-2 border-rose-300 bg-rose-50 p-5 text-left transition hover:bg-rose-100">
                <WalletCards className="h-7 w-7 text-rose-600" />
                <span className="mt-3 block text-base font-bold text-rose-950">{mode === 'Fixed' ? 'Клиентская задача' : mode === 'Actual' ? 'Платная задача' : 'Платная задача без договора'}</span>
                <span className="mt-1 block text-sm text-rose-700">{mode === 'Fixed' ? 'Учесть объём в рамках фиксированного договора' : 'Добавить в контроль будущего счёта'}</span>
              </button>
              <button type="button" onClick={() => complete('internal')} className="min-h-36 rounded-2xl border-2 border-slate-300 bg-slate-50 p-5 text-left transition hover:bg-slate-100">
                <ShieldAlert className="h-7 w-7 text-slate-600" />
                <span className="mt-3 block text-base font-bold text-slate-950">Внутренняя задача</span>
                <span className="mt-1 block text-sm text-slate-600">Не учитывать в системе оплаты с клиентом</span>
              </button>
            </div>
            <button type="button" onClick={() => setStep('binding')} className="mt-4 inline-flex items-center gap-2 text-sm font-semibold text-slate-600 hover:text-slate-900"><ArrowLeft className="h-4 w-4" /> Назад</button>
          </div>
        )}
      </div>
    </div>
  );
}

type TaskModalProps = {
  presentation?: 'modal' | 'page';
  chrome?: TaskCardChrome;
  state: ModalState;
  tasks: TaskJournalItem[];
  assignees: TaskJournalAssignee[];
  clients: Client[];
  legalEntities: LegalEntitySummary[];
  counterpartyOptions: LookupSelectOption[];
  currentUserId: number | null;
  currentUserName: string;
  isAdminUser: boolean;
  taskSettings: TaskJournalSettingsDto;
  notificationDefaults: TaskNotificationDefaults;
  canSwitchToEdit: boolean;
  canCompleteTask: boolean;
  canDeleteTask: boolean;
  canDuplicateTask: boolean;
  canSendFilesToYandexDisk: boolean;
  onClose: () => void;
  onEnableEdit: () => void;
  onSave: (form: TaskForm, taskId?: number) => void | Promise<void>;
  onResultSave: (task: TaskJournalItem, status: TaskJournalStatus, result: string) => void | Promise<void>;
  onApprovalDecision: (
    taskId: number,
    decision: 'approved' | 'revisionRequested',
    resolution?: string,
    participantId?: number,
    finalAction?: TaskApprovalFinalAction,
    revisionDueAt?: string | null,
  ) => boolean | void | Promise<boolean | void>;
  onReviewDeadlineChange: (task: TaskJournalItem, request: TaskJournalDeadlineChangeRequest) => void;
  onReviewApproval: (task: TaskJournalItem, participant: TaskJournalParticipant) => void;
  onDelete: (task: TaskJournalItem) => boolean | Promise<boolean>;
  onDuplicate: (task: TaskJournalItem) => boolean | Promise<boolean>;
};

function ModernTaskModal(props: TaskModalProps) {
  return <TaskModal {...props} chrome="modern" />;
}

function TaskModal({
  presentation = 'modal',
  chrome = 'legacy',
  state,
  tasks,
  assignees,
  clients,
  legalEntities,
  counterpartyOptions,
  currentUserId,
  currentUserName,
  isAdminUser,
  taskSettings,
  notificationDefaults,
  canSwitchToEdit,
  canCompleteTask,
  canDeleteTask,
  canDuplicateTask,
  canSendFilesToYandexDisk,
  onClose,
  onEnableEdit,
  onSave,
  onResultSave,
  onApprovalDecision,
  onReviewDeadlineChange,
  onReviewApproval,
  onDelete,
  onDuplicate,
}: TaskModalProps) {
  const toast = useToast();
  const { confirm } = useDialog();
  const isPage = presentation === 'page';
  const isModernChrome = chrome === 'modern';
  const taskId = state?.task?.id ?? null;
  const requireCompletionResult = taskSettings.requireCompletionResult !== false;
  const titleInputRef = useRef<HTMLInputElement | null>(null);
  const newFileInputRef = useRef<HTMLInputElement | null>(null);
  const versionInputRefs = useRef<Record<string, HTMLInputElement | null>>({});
  const [form, setForm] = useState<TaskForm>(() => {
    if (state?.task) {
      const rest = {
        isInternal: state.task.isInternal ?? (!state.task.clientId && !state.task.legalEntityId),
        clientId: state.task.clientId ?? null,
        legalEntityId: state.task.legalEntityId ?? null,
        clientCaseId: state.task.clientCaseId ?? null,
        objectName: state.task.objectName,
        title: state.task.title,
        description: state.task.description,
        assigneeUserId: state.task.assigneeUserId,
        assigneeName: state.task.assigneeName,
        authorUserId: state.task.authorUserId ?? null,
        dueAt: state.task.dueAt,
        approvalDueAt: state.task.approvalDueAt ?? null,
        contractorPaymentEnabled: Boolean(state.task.contractorPaymentEnabled),
        contractorPaymentAmount: state.task.contractorPaymentAmount ?? null,
        contractorPaymentDueDate: state.task.contractorPaymentDueDate ?? null,
        contractorPaymentOnResult: Boolean(state.task.contractorPaymentOnResult),
        contractorPaymentResultDelayDays: state.task.contractorPaymentResultDelayDays ?? null,
        contractorPaymentId: state.task.contractorPaymentId ?? null,
        contractorPaymentStatus: state.task.contractorPaymentStatus ?? null,
        type: state.task.type,
        priority: state.task.priority,
        status: state.task.status,
        isUnderControl: Boolean(state.task.isUnderControl),
        result: state.task.result,
        plannedDurationMinutes: normalizeTaskDuration(state.task.plannedDurationMinutes),
        reminders: state.task.reminders,
        channels: state.task.channels,
        tags: state.task.tags,
        participants: state.task.participants ?? [],
      };
      return {
        ...rest,
        dueAt: toLocalInputValue(rest.dueAt),
        approvalDueAt: rest.approvalDueAt ? toLocalInputValue(rest.approvalDueAt) : null,
        contractorPaymentDueDate: rest.contractorPaymentDueDate ? rest.contractorPaymentDueDate.split('T')[0] : null,
      };
    }
    const next = emptyForm(currentUserName, currentUserId, notificationDefaults);
    const protectedFields: TaskTemplateField[] = state?.dueAt ? ['dueAt'] : [];
    const withDefaults = applyTaskTemplateDefaults(next, state?.defaults, {
      protectedFields,
      replaceFields: [
        'isInternal',
        'clientId',
        'legalEntityId',
        'clientCaseId',
        'clientBillingClassification',
        'billingContractId',
        'billingMode',
        'objectName',
        'assigneeUserId',
        'assigneeName',
        'type',
        'priority',
        'status',
        'dueAt',
        'approvalDueAt',
        'plannedDurationMinutes',
        'isUnderControl',
      ],
    });
    return {
      ...withDefaults,
      dueAt: state?.dueAt
        ? toLocalInputValue(state.dueAt)
        : state?.defaults?.dueAt
          ? toLocalInputValue(withDefaults.dueAt)
          : withDefaults.dueAt,
      approvalDueAt: withDefaults.approvalDueAt && state?.defaults?.approvalDueAt
        ? toLocalInputValue(withDefaults.approvalDueAt)
        : withDefaults.approvalDueAt,
      contractorPaymentDueDate: withDefaults.contractorPaymentDueDate && state?.defaults?.contractorPaymentDueDate
        ? withDefaults.contractorPaymentDueDate.split('T')[0]
        : withDefaults.contractorPaymentDueDate,
    };
  });
  const [resultError, setResultError] = useState<string | null>(null);
  const [approvalResultPrompt, setApprovalResultPrompt] = useState<{ form: TaskForm; taskId?: number } | null>(null);
  const [approvalResultPromptResult, setApprovalResultPromptResult] = useState('');
  const [approvalResultPromptError, setApprovalResultPromptError] = useState<string | null>(null);
  const [approvalResultPromptSaving, setApprovalResultPromptSaving] = useState(false);
  const [tagText, setTagText] = useState(() => form.tags.join(', '));
  const [comments, setComments] = useState<TaskJournalComment[]>([]);
  const [commentsLoading, setCommentsLoading] = useState(false);
  const [commentSaving, setCommentSaving] = useState(false);
  const [commentText, setCommentText] = useState('');
  const [commentExternalCalendarVisible, setCommentExternalCalendarVisible] = useState(false);
  const [deadlineChangeRequests, setDeadlineChangeRequests] = useState<TaskJournalDeadlineChangeRequest[]>([]);
  const [deadlineChangeRequestsLoading, setDeadlineChangeRequestsLoading] = useState(false);
  const [changeComment, setChangeComment] = useState('');
  const [deadlineRequestDueAt, setDeadlineRequestDueAt] = useState(() => state?.task ? toLocalInputValue(state.task.dueAt) : '');
  const [deadlineRequestReasonDialogOpen, setDeadlineRequestReasonDialogOpen] = useState(false);
  const [deadlineRequestReasonDraft, setDeadlineRequestReasonDraft] = useState('');
  const [deadlineRequestReasonError, setDeadlineRequestReasonError] = useState<string | null>(null);
  const [deadlineRequestSaving, setDeadlineRequestSaving] = useState(false);
  const [files, setFiles] = useState<TaskJournalFile[]>([]);
  const [filesLoading, setFilesLoading] = useState(false);
  const [uploadingDocumentKey, setUploadingDocumentKey] = useState<string | null>(null);
  const [sendingYandexFileId, setSendingYandexFileId] = useState<number | null>(null);
  const [actionMenuOpen, setActionMenuOpen] = useState(false);
  const [quickPanel, setQuickPanel] = useState<'assignee' | 'observer' | null>(null);
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [approvalChoice, setApprovalChoice] = useState<{
    taskId: number;
    participantId?: number;
    resolution?: string;
  } | null>(null);
  const [revisionReview, setRevisionReview] = useState<{
    taskId: number;
    participantId?: number;
    approverName: string;
  } | null>(null);
  const [revisionReviewText, setRevisionReviewText] = useState('');
  const [revisionReviewDueAt, setRevisionReviewDueAt] = useState('');
  const [revisionReviewError, setRevisionReviewError] = useState<string | null>(null);
  const [revisionReviewSaving, setRevisionReviewSaving] = useState(false);
  const selectedClient = useMemo(
    () => clients.find((client) => client.id === form.clientId) ?? null,
    [clients, form.clientId],
  );
  const selectedLegalEntity = useMemo(
    () => legalEntities.find((entity) => entity.id === form.legalEntityId) ?? null,
    [legalEntities, form.legalEntityId],
  );
  const selectedOwnerName = selectedClient?.name || getLegalEntityDisplayName(selectedLegalEntity);
  const selectedAssignee = useMemo(
    () => assignees.find((assignee) => assignee.id === form.assigneeUserId) ?? null,
    [assignees, form.assigneeUserId],
  );
  const canUseContractorPayment = Boolean(selectedAssignee?.isExternalContractor);
  const approverParticipants = useMemo(
    () => (form.participants ?? []).filter((participant) => participant.role === 'approver'),
    [form.participants],
  );
  const orderedApproverParticipants = useMemo(
    () => approverParticipants.slice().sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0)),
    [approverParticipants],
  );
  const currentApprovalParticipant = useMemo(
    () => orderedApproverParticipants.find((participant) => participant.approvalStatus !== 'approved') ?? null,
    [orderedApproverParticipants],
  );
  const isCurrentApprovalFinal = useMemo(
    () => Boolean(currentApprovalParticipant)
      && orderedApproverParticipants.every((participant) =>
        participant === currentApprovalParticipant || participant.approvalStatus === 'approved'),
    [currentApprovalParticipant, orderedApproverParticipants],
  );
  const isImplicitPrimaryAssigneeParticipant = useCallback((participant: TaskJournalParticipant) => {
    if (participant.role !== 'assignee') return false;
    if (form.assigneeUserId) {
      return participant.userId === form.assigneeUserId;
    }

    const primaryName = form.assigneeName.trim().toLowerCase();
    return Boolean(primaryName) && getParticipantDisplayName(participant).trim().toLowerCase() === primaryName;
  }, [form.assigneeName, form.assigneeUserId]);
  const additionalParticipantsCount = useMemo(
    () => (form.participants ?? []).filter((participant) => participant.role !== 'assignee').length,
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
    enabled: !form.isInternal && Boolean(form.clientId || form.legalEntityId),
    legalEntityId: form.legalEntityId ?? undefined,
  });
  const selectableClientCases = useMemo(
    () => clientCases.filter((clientCase) => !isClosedClientCase(clientCase) || clientCase.id === form.clientCaseId),
    [clientCases, form.clientCaseId],
  );
  const isReadOnly = state?.mode === 'view';
  const isLockedViewWithEditAccess = Boolean(isReadOnly && canSwitchToEdit);
  const canRequestDeadlineChange = Boolean(taskId && isReadOnly && !canSwitchToEdit && taskSettings.allowExecutorDeadlineChangeRequests);
  const canPostComment = Boolean(taskId && canPostTaskCommentForCurrentUser(state?.task, currentUserId, canSwitchToEdit));
  const canCompleteReadOnlyTask = Boolean(
    taskId
      && isReadOnly
      && !canSwitchToEdit
      && state?.task
      && canCompleteTask,
  );
  const canManageFiles = Boolean(taskId && !isReadOnly);
  const isCreationBillingBindingLocked = !taskId
    && (form.clientBillingClassification === 'billable' || form.clientBillingClassification === 'internal');
  const selectedTaskAuthor = assignees.find((assignee) => assignee.id === form.authorUserId);
  const taskAuthorLabel = selectedTaskAuthor?.name
    || (state?.task ? getTaskAuthorName(state.task) : currentUserName)
    || 'Текущий пользователь';

  const update = <K extends keyof TaskForm>(key: K, value: TaskForm[K]) => {
    if (isReadOnly && key !== 'result') return;
    if (key === 'result' && form.status !== 'done') return;
    if (key === 'result' && resultError) {
      setResultError(null);
    }
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const selectedCounterpartyValue = form.isInternal
    ? ''
    : form.clientId
    ? `client:${form.clientId}`
    : form.legalEntityId
      ? `legal:${form.legalEntityId}`
      : '';

  const handleInternalChange = (checked: boolean) => {
    if (isReadOnly) return;
    setForm((prev) => ({
      ...prev,
      isInternal: checked,
      clientId: checked ? null : prev.clientId,
      legalEntityId: checked ? null : prev.legalEntityId,
      clientCaseId: checked ? null : prev.clientCaseId,
      objectName: checked ? INTERNAL_TASK_OBJECT_NAME : '',
    }));
  };

  const handleCounterpartyChange = (value: string) => {
    if (isReadOnly || form.isInternal) return;
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
    if (isReadOnly || form.isInternal) return;
    const clientCaseId = Number(value) || null;
    const clientCase = selectableClientCases.find((item) => item.id === clientCaseId);
    setForm((prev) => ({
      ...prev,
      clientCaseId,
      objectName: clientCase
        ? `${selectedOwnerName || prev.objectName} - ${clientCase.title}`
        : selectedOwnerName || prev.objectName,
    }));
  };

  const toggleArray = <T extends string>(key: 'reminders' | 'channels', value: T) => {
    if (isReadOnly) return;
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

  const addParticipant = (role: TaskJournalParticipantRole, assignee: TaskJournalAssignee) => {
    if (isReadOnly) return;

    setForm((prev) => {
      const participants = prev.participants ?? [];
      if (prev.assigneeUserId === assignee.id || participants.some((participant) => participant.role === role && participant.userId === assignee.id)) {
        return prev;
      }

      return {
        ...prev,
        participants: [
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

  const removeParticipant = (role: TaskJournalParticipantRole, participant: TaskJournalParticipant) => {
    if (isReadOnly || isImplicitPrimaryAssigneeParticipant(participant)) return;

    setForm((prev) => {
      const displayName = getParticipantDisplayName(participant).trim().toLowerCase();
      const nextParticipants = (prev.participants ?? []).filter((item) => {
        if (item.role !== role) return true;
        if (participant.id && item.id === participant.id) return false;
        if (participant.userId && item.userId === participant.userId) return false;
        return getParticipantDisplayName(item).trim().toLowerCase() !== displayName;
      });
      const approvalEnded = role === 'approver'
        && (prev.status === 'onApproval' || prev.status === 'overdueApproval')
        && !nextParticipants.some((item) => item.role === 'approver');

      return {
        ...prev,
        participants: nextParticipants,
        status: approvalEnded ? 'inProgress' : prev.status,
        approvalDueAt: approvalEnded ? null : prev.approvalDueAt,
      };
    });
  };

  const getVisibleParticipantsByRole = (role: TaskJournalParticipantRole) =>
    (form.participants ?? [])
      .filter((participant) => participant.role === role && !isImplicitPrimaryAssigneeParticipant(participant))
      .slice()
      .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));

  const getAvailableAssigneesForRole = (role: TaskJournalParticipantRole) =>
    assignees.filter((assignee) => assignee.id !== form.assigneeUserId && !isParticipantSelected(role, assignee.id));

  const setPrimaryAssignee = (assigneeId: number | null) => {
    if (isReadOnly) return;
    const selected = assignees.find((assignee) => assignee.id === assigneeId);
    const paymentAllowed = Boolean(selected?.isExternalContractor);
    setForm((prev) => {
      const previousAssigneeUserId = prev.assigneeUserId;
      const previousAssigneeName = prev.assigneeName.trim().toLowerCase();
      return {
        ...prev,
        assigneeUserId: assigneeId,
        assigneeName: selected?.name ?? (assigneeId ? prev.assigneeName : ''),
        contractorPaymentEnabled: paymentAllowed ? prev.contractorPaymentEnabled : false,
        participants: (prev.participants ?? []).filter((participant) => {
          if (participant.role !== 'assignee') return true;
          if (previousAssigneeUserId && participant.userId === previousAssigneeUserId) return false;
          if (assigneeId && participant.userId === assigneeId) return false;
          return !previousAssigneeName || getParticipantDisplayName(participant).trim().toLowerCase() !== previousAssigneeName;
        }),
      };
    });
  };

  const saveWithStatus = async (status: TaskJournalStatus) => {
    const task = state?.task;
    let nextForm = withChangeComment({ ...form, status });
    if ((status === 'onApproval' || status === 'overdueApproval') && !nextForm.approvalDueAt) {
      nextForm = { ...nextForm, approvalDueAt: getDefaultApprovalDueAtInput(taskSettings) };
    }

    if (status === 'done' && requireCompletionResult && !nextForm.result.trim()) {
      if (isReadOnly && task) {
        openApprovalResultPrompt(nextForm, task.id);
        return;
      }
      setForm(nextForm);
      setResultError(TASK_COMPLETION_RESULT_REQUIRED_MESSAGE);
      return;
    }

    if (isApprovalResultPromptRequired(nextForm)) {
      openApprovalResultPrompt(nextForm, task?.id);
      return;
    }

    setResultError(null);
    setForm(nextForm);
    if (isReadOnly && task) {
      await onResultSave(task, status, nextForm.result);
      return;
    }

    await onSave(nextForm, task?.id);
  };

  const withChangeComment = (nextForm: TaskForm = form): TaskForm => ({
    ...nextForm,
    changeComment: changeComment.trim() || null,
  });

  const isApprovalResultPromptRequired = (nextForm: TaskForm) =>
    requireCompletionResult
    && (nextForm.status === 'onApproval' || nextForm.status === 'overdueApproval')
    && !nextForm.result.trim();

  const openApprovalResultPrompt = (nextForm: TaskForm, taskId?: number) => {
    setForm(nextForm);
    setResultError(null);
    setApprovalResultPrompt({ form: nextForm, taskId });
    setApprovalResultPromptResult(nextForm.result.trim());
    setApprovalResultPromptError(null);
  };

  const closeApprovalResultPrompt = () => {
    if (approvalResultPromptSaving) return;
    setApprovalResultPrompt(null);
    setApprovalResultPromptResult('');
    setApprovalResultPromptError(null);
  };

  const submitApprovalResultPrompt = async (files: File[]) => {
    if (!approvalResultPrompt || approvalResultPromptSaving) return;

    const normalizedResult = approvalResultPromptResult.trim();
    if (requireCompletionResult && !normalizedResult) {
      setApprovalResultPromptError(
        approvalResultPrompt.form.status === 'done'
          ? TASK_COMPLETION_RESULT_REQUIRED_MESSAGE
          : TASK_APPROVAL_RESULT_REQUIRED_MESSAGE,
      );
      return;
    }

    const nextForm = {
      ...approvalResultPrompt.form,
      result: normalizedResult,
    };

    setApprovalResultPromptSaving(true);
    setResultError(null);
    setForm(nextForm);
    try {
      const existingTaskId = approvalResultPrompt.taskId ?? state?.task?.id;
      if (files.length > 0) {
        if (!existingTaskId) {
          setApprovalResultPromptError('Сначала сохраните задачу, затем прикрепите файл к результату.');
          return;
        }
        await uploadTaskCompletionFiles(existingTaskId, files);
      }
      if (isReadOnly && state?.task) {
        await onResultSave(state.task, nextForm.status, nextForm.result);
      } else {
        await onSave(nextForm, approvalResultPrompt.taskId);
      }
      setApprovalResultPrompt(null);
      setApprovalResultPromptResult('');
      setApprovalResultPromptError(null);
    } catch (error) {
      setApprovalResultPromptError(extractDisplayErrorMessage(error, 'Не удалось прикрепить файл или сохранить результат задачи.'));
    } finally {
      setApprovalResultPromptSaving(false);
    }
  };

  const handleSaveForm = async () => {
    let nextForm = withChangeComment();
    const removedAllApproversFromActiveApproval = Boolean(
      state?.task
      && isTaskAwaitingApproval(state.task)
      && (nextForm.status === 'onApproval' || nextForm.status === 'overdueApproval')
      && !(nextForm.participants ?? []).some((participant) => participant.role === 'approver'),
    );
    if (removedAllApproversFromActiveApproval) {
      nextForm = {
        ...nextForm,
        status: 'inProgress',
        approvalDueAt: null,
      };
    }
    if ((nextForm.status === 'onApproval' || nextForm.status === 'overdueApproval') && !nextForm.approvalDueAt) {
      nextForm = { ...nextForm, approvalDueAt: getDefaultApprovalDueAtInput(taskSettings) };
    }

    if (nextForm.status === 'done' && requireCompletionResult && !nextForm.result.trim()) {
      setForm(nextForm);
      setResultError(TASK_COMPLETION_RESULT_REQUIRED_MESSAGE);
      return;
    }

    if (isApprovalResultPromptRequired(nextForm)) {
      openApprovalResultPrompt(nextForm, state?.task?.id);
      return;
    }

    setResultError(null);
    await onSave(nextForm, state?.task?.id);
  };

  const handleApproveParticipant = (participant: TaskJournalParticipant) => {
    const task = state?.task;
    if (!task) return;

    const resolution = form.result.trim();
    if (participant === currentApprovalParticipant && isCurrentApprovalFinal) {
      setApprovalChoice({
        taskId: task.id,
        participantId: participant.id,
        resolution,
      });
      return;
    }

    void onApprovalDecision(task.id, 'approved', resolution, participant.id);
  };

  const handleCompletionAction = async () => {
    const task = state?.task;
    if (task && isTaskAwaitingApproval(task)) {
      if (taskSettings.approvalQuickActionMode === 'completionButton' && currentApprovalParticipant) {
        handleApproveParticipant(currentApprovalParticipant);
      }
      return;
    }

    const nextStatus: TaskJournalStatus = approverParticipants.some((participant) => participant.approvalStatus !== 'approved')
      ? 'onApproval'
      : 'done';
    await saveWithStatus(nextStatus);
  };

  const confirmApprovalChoice = async (finalAction: TaskApprovalFinalAction) => {
    const choice = approvalChoice;
    if (!choice) return;

    setApprovalChoice(null);
    await onApprovalDecision(choice.taskId, 'approved', choice.resolution, choice.participantId, finalAction);
  };

  const openRevisionReviewDialog = (participant?: TaskJournalParticipant | null) => {
    const task = state?.task;
    if (!task) return;

    setRevisionReview({
      taskId: task.id,
      participantId: participant?.id,
      approverName: participant ? getParticipantDisplayName(participant) : currentUserName,
    });
    setRevisionReviewText('');
    setRevisionReviewDueAt('');
    setRevisionReviewError(null);
  };

  const submitRevisionReview = async () => {
    const review = revisionReview;
    if (!review) return;

    const text = revisionReviewText.trim();
    if (!text) {
      setRevisionReviewError('Напишите рецензию: что именно нужно исправить перед повторным согласованием.');
      return;
    }

    const nextDueAt = revisionReviewDueAt ? fromLocalInputValue(revisionReviewDueAt) : null;

    setRevisionReviewSaving(true);
    setRevisionReviewError(null);
    try {
      await onApprovalDecision(review.taskId, 'revisionRequested', text, review.participantId, undefined, nextDueAt);
      setRevisionReview(null);
      setRevisionReviewText('');
      setRevisionReviewDueAt('');
    } finally {
      setRevisionReviewSaving(false);
    }
  };

  const handleDeleteTask = async () => {
    const task = state?.task;
    if (!task || !canDeleteTask) return;
    const deleted = await onDelete(task);
    if (deleted) {
      onClose();
    }
  };

  const handleDuplicateTask = async () => {
    const task = state?.task;
    if (!task || isReadOnly || !canDuplicateTask) return;
    await onDuplicate(task);
  };

  const handleCancelTask = async () => {
    const task = state?.task;
    if (!task || isReadOnly || !isTaskJournalActive(task.status)) return;

    const accepted = await confirm({
      title: 'Отменить задачу?',
      description: `Задача «${task.title}» перейдет в статус «Отменена» и останется в журнале и истории.`,
      confirmLabel: 'Отменить задачу',
      cancelLabel: 'Вернуться',
      variant: 'danger',
    });
    if (!accepted) return;

    setActionMenuOpen(false);
    await saveWithStatus('cancelled');
  };

  const canCancelTask = Boolean(state?.task && !isReadOnly && isTaskJournalActive(state.task.status));

  const openQuickPanel = (panel: 'assignee' | 'observer') => {
    if (isReadOnly) return;
    setQuickPanel((current) => (current === panel ? null : panel));
    setActionMenuOpen(false);
  };

  const availableObservers = getAvailableAssigneesForRole('observer');
  const isAwaitingApproval = Boolean(
    state?.task
    && isTaskAwaitingApproval(state.task)
    && approverParticipants.length > 0,
  );
  const approvalCompletionDisabled = isAwaitingApproval
    && (taskSettings.approvalQuickActionMode === 'separateButton' || !currentApprovalParticipant);
  const completionButtonLabel = isAwaitingApproval
    ? approvalCompletionDisabled
      ? 'Задача находится на согласовании'
      : 'Открыть окно согласования'
    : approverParticipants.some((participant) => participant.approvalStatus !== 'approved')
      ? 'На согласование'
      : 'Завершить';
  const compactCompletionButtonLabel = isAwaitingApproval ? 'Согласование' : completionButtonLabel;
  const canSendToAuthorApproval = Boolean(
    state?.task
      && !isAwaitingApproval
      && canCompleteTask
      && approverParticipants.length === 0
      && state.task.authorUserId
      && state.task.authorUserId !== currentUserId,
  );
  const sendToAuthorApprovalLabel = 'Отправить постановщику на согласование';

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
      setDeadlineChangeRequests([]);
      setDeadlineChangeRequestsLoading(false);
      return;
    }

    let active = true;
    setDeadlineChangeRequestsLoading(true);
    apiService.getTaskJournalDeadlineChangeRequests(taskId)
      .then((items) => {
        if (active) {
          setDeadlineChangeRequests(items);
        }
      })
      .catch((error) => {
        if (active) {
          toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить запросы переноса срока.'), 'Журнал задач');
        }
      })
      .finally(() => {
        if (active) {
          setDeadlineChangeRequestsLoading(false);
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
    if (!canPostComment) {
      toast.error('У вас нет права добавить комментарий к этой задаче.', 'Журнал задач');
      return;
    }

    setCommentSaving(true);
    try {
      const created = await apiService.createTaskJournalComment(taskId, text, commentExternalCalendarVisible);
      setComments((previous) => [created, ...previous]);
      setCommentText('');
      setCommentExternalCalendarVisible(false);
      toast.success(
        commentExternalCalendarVisible
          ? 'Комментарий добавлен и будет доступен во внешнем календаре.'
          : 'Комментарий добавлен.',
        'Журнал задач',
      );
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось добавить комментарий.'), 'Журнал задач');
    } finally {
      setCommentSaving(false);
    }
  };

  const openDeadlineRequestReasonDialog = () => {
    if (!deadlineRequestDueAt) {
      toast.error('Укажите новый срок для запроса переноса.', 'Журнал задач');
      return;
    }

    setDeadlineRequestReasonDraft('');
    setDeadlineRequestReasonError(null);
    setDeadlineRequestReasonDialogOpen(true);
  };

  const closeDeadlineRequestReasonDialog = () => {
    if (deadlineRequestSaving) return;
    setDeadlineRequestReasonDialogOpen(false);
    setDeadlineRequestReasonDraft('');
    setDeadlineRequestReasonError(null);
  };

  const handleDeadlineRequest = async () => {
    const requestedDueAt = fromLocalInputValue(deadlineRequestDueAt);
    if (!taskId || !deadlineRequestDueAt) return;
    const reason = deadlineRequestReasonDraft.trim();
    if (!reason) {
      setDeadlineRequestReasonError('Укажите причину переноса срока.');
      return;
    }

    setDeadlineRequestSaving(true);
    try {
      const created = await apiService.createTaskJournalDeadlineChangeRequest(taskId, {
        requestedDueAt,
        reason,
      });
      setComments((previous) => [created, ...previous]);
      const requests = await apiService.getTaskJournalDeadlineChangeRequests(taskId);
      setDeadlineChangeRequests(requests);
      setDeadlineRequestReasonDialogOpen(false);
      setDeadlineRequestReasonDraft('');
      setDeadlineRequestReasonError(null);
      toast.success('Запрос переноса срока добавлен в историю задачи.', 'Журнал задач');
    } catch (error) {
      setDeadlineRequestReasonError(extractDisplayErrorMessage(error, 'Не удалось отправить запрос переноса срока.'));
    } finally {
      setDeadlineRequestSaving(false);
    }
  };

  const handleUploadFile = async (fileList: FileList | null | undefined, documentKey?: string) => {
    const file = fileList?.[0];
    if (!taskId || !file || isReadOnly) return;

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

  const handleSendFileToYandexDisk = async (file: TaskJournalFile) => {
    if (!taskId) return;
    if (!canSendFilesToYandexDisk) {
      toast.error('У роли нет права отправлять файлы задач на Я.Диск.', 'Я.Диск');
      return;
    }

    setSendingYandexFileId(file.id);
    try {
      const result = await apiService.uploadTaskJournalFileToYandexDisk(taskId, file.id);
      toast.success(`Файл отправлен на Яндекс.Диск: ${result.path}.`, 'Я.Диск');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось отправить файл задачи на Яндекс.Диск.'), 'Я.Диск');
    } finally {
      setSendingYandexFileId(null);
    }
  };

  const handleDeleteFile = async (file: TaskJournalFile) => {
    if (!taskId || isReadOnly) return;

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

  const shellClassName = isPage
    ? isModernChrome
      ? 'w-full max-w-none p-3 sm:p-4 lg:p-5'
      : 'w-full max-w-none p-4 sm:p-6 lg:p-8'
    : isModernChrome
      ? 'fixed inset-0 z-50 flex items-center justify-center bg-slate-900/45 p-3 backdrop-blur-sm sm:p-4'
      : 'fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4 backdrop-blur-sm';
  const panelClassName = isPage
    ? isModernChrome
      ? 'flex w-full flex-col overflow-visible rounded-xl border border-slate-200 bg-white shadow-sm'
      : 'flex w-full flex-col overflow-visible rounded-2xl border border-slate-200 bg-white shadow-sm'
    : isModernChrome
      ? 'flex max-h-[94vh] w-full max-w-7xl flex-col overflow-hidden rounded-xl bg-white shadow-2xl'
      : 'flex max-h-[92vh] w-full max-w-5xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl';
  const headerClassName = isModernChrome
    ? 'flex flex-col gap-3 border-b border-slate-200 bg-slate-50/80 px-5 py-4 lg:flex-row lg:items-start lg:justify-between'
    : 'flex flex-col gap-4 border-b border-slate-200 px-6 py-5 lg:flex-row lg:items-start lg:justify-between';
  const bodyClassName = isPage
    ? isModernChrome ? 'px-5 py-4' : 'px-6 py-5'
    : isModernChrome ? 'min-h-0 overflow-y-auto px-5 py-4' : 'min-h-0 overflow-y-auto px-6 py-5';
  const canEditResult = form.status === 'done';
  const statusOption = STATUS_OPTIONS.find((option) => option.value === form.status) ?? STATUS_OPTIONS[0];
  const typeOption = TYPE_OPTIONS.find((option) => option.value === form.type) ?? TYPE_OPTIONS[0];
  const assigneesById = useMemo(
    () => new Map(assignees.map((assignee) => [assignee.id, assignee])),
    [assignees],
  );
  const selectedCaseTitle = selectableClientCases.find((item) => item.id === form.clientCaseId)?.title ?? state?.task?.clientCaseTitle ?? '';
  const visibleAdditionalAssignees = getVisibleParticipantsByRole('assignee');
  const availableAdditionalAssignees = getAvailableAssigneesForRole('assignee');
  const visibleObservers = getVisibleParticipantsByRole('observer');
  const visibleApprovers = getVisibleParticipantsByRole('approver');
  const renderAssigneeIdentity = (option: { value: string; label: ReactNode }) => {
    const assignee = assigneesById.get(Number(option.value));
    if (!assignee) {
      return option.label;
    }

    return (
      <span className="flex min-w-0 flex-col gap-0.5 leading-tight">
        <span className="whitespace-normal break-words font-semibold text-slate-800">{assignee.name}</span>
        {assignee.email ? <span className="break-all text-xs font-normal text-slate-500">{assignee.email}</span> : null}
      </span>
    );
  };
  const renderAssigneeControl = (compact = false) => (
    <div className="space-y-2" data-testid="task-assignees-control">
      <SystemSelect
        value={form.assigneeUserId ?? ''}
        onChange={(event) => setPrimaryAssignee(Number(event.target.value) || null)}
        disabled={isReadOnly}
        data-testid="task-assignee-select"
        aria-label="Основной исполнитель"
        renderOption={renderAssigneeIdentity}
        renderValue={renderAssigneeIdentity}
        className={compact
          ? 'w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white'
          : 'form-input'}
      >
        <option value="">Без привязки к пользователю</option>
        {assignees.map((assignee) => (
          <option key={assignee.id} value={assignee.id}>
            {assignee.email ? `${assignee.name} — ${assignee.email}` : assignee.name}
          </option>
        ))}
      </SystemSelect>

      {visibleAdditionalAssignees.length > 0 ? (
        <div className="space-y-1.5" data-testid="task-additional-assignees-list">
          {visibleAdditionalAssignees.map((participant, index) => {
            const participantName = getParticipantDisplayName(participant);
            const participantEmail = participant.email
              || (participant.userId ? assigneesById.get(participant.userId)?.email : '')
              || '';
            return (
              <div
                key={`assignee-direct-${participant.id ?? participant.userId ?? participantName}-${index}`}
                className="flex items-center justify-between gap-3 rounded-lg border border-blue-100 bg-blue-50/70 px-2.5 py-2 text-sm"
                data-testid="task-additional-assignee"
              >
                <span className="flex min-w-0 flex-1 flex-col gap-0.5 leading-tight">
                  <span className="whitespace-normal break-words font-semibold text-slate-800">{participantName}</span>
                  {participantEmail ? <span className="break-all text-xs font-normal text-slate-500">{participantEmail}</span> : null}
                </span>
                {!isReadOnly ? (
                  <button
                    type="button"
                    onClick={() => removeParticipant('assignee', participant)}
                    className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-slate-400 transition hover:bg-white hover:text-rose-600"
                    aria-label={`Удалить участника ${participantName}`}
                    title="Удалить исполнителя"
                  >
                    <X className="h-4 w-4" />
                  </button>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}

      {!isReadOnly ? (
        <SystemSelect
          aria-label="Добавить исполнителя"
          data-testid="task-assignee-add-select"
          renderOption={renderAssigneeIdentity}
          value=""
          onChange={(event) => {
            const selected = assignees.find((assignee) => assignee.id === Number(event.target.value));
            if (selected) addParticipant('assignee', selected);
          }}
          className={compact
            ? 'w-full rounded-lg border border-dashed border-blue-200 bg-white px-2 py-2 text-sm font-medium text-blue-700 outline-none focus:border-blue-400'
            : 'form-input border-dashed border-blue-200 bg-white text-blue-700'}
          disabled={!form.assigneeUserId || availableAdditionalAssignees.length === 0}
        >
          <option value="">
            {!form.assigneeUserId
              ? 'Сначала выберите основного исполнителя'
              : availableAdditionalAssignees.length === 0
                ? 'Все исполнители уже выбраны'
                : '+ Добавить ещё исполнителя'}
          </option>
          {availableAdditionalAssignees.map((assignee) => (
            <option key={assignee.id} value={assignee.id}>
              {assignee.email ? `${assignee.name} — ${assignee.email}` : assignee.name}
            </option>
          ))}
        </SystemSelect>
      ) : null}

      <div className="text-[11px] leading-4 text-slate-400">
        Основной и дополнительные исполнители видят задачу и могут работать с ней.
      </div>
    </div>
  );
  const renderModernSelectedParticipantList = (
    role: TaskJournalParticipantRole,
    participants: TaskJournalParticipant[],
    testId: string,
  ) => {
    if (participants.length === 0) {
      return (
        <div
          className="rounded-lg border border-dashed border-slate-200 bg-white/70 px-2.5 py-3 text-center text-xs text-slate-400"
          data-testid={testId}
        >
          Пока никто не добавлен
        </div>
      );
    }

    return (
      <div className="space-y-1.5" data-testid={testId}>
        {participants.map((participant, index) => {
          const participantName = getParticipantDisplayName(participant);
          const approvalStatus = role === 'approver'
            ? participant.approvalStatus === 'approved'
              ? { label: 'Согласовано', className: 'bg-emerald-50 text-emerald-700 ring-emerald-100' }
              : participant.approvalStatus === 'revisionRequested'
                ? { label: 'На доработку', className: 'bg-orange-50 text-orange-700 ring-orange-100' }
                : { label: 'Ожидает решения', className: 'bg-amber-50 text-amber-700 ring-amber-100' }
            : null;

          return (
            <div
              key={`${role}-modern-${participant.id ?? participant.userId ?? participantName}-${index}`}
              className="flex items-start justify-between gap-2 rounded-lg border border-slate-100 bg-slate-50 px-2.5 py-2 text-sm"
            >
              <span className="min-w-0">
                <span className="flex min-w-0 flex-wrap items-center gap-1.5">
                  <span className="min-w-0 break-words font-semibold text-slate-700">{participantName}</span>
                  {approvalStatus ? (
                    <span className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-semibold ring-1 ${approvalStatus.className}`}>
                      {approvalStatus.label}
                    </span>
                  ) : null}
                </span>
                {participant.email ? <span className="block truncate text-xs text-slate-400">{participant.email}</span> : null}
              </span>
              {!isReadOnly ? (
                <button
                  type="button"
                  onClick={() => removeParticipant(role, participant)}
                  className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-slate-400 transition hover:bg-white hover:text-rose-600"
                  aria-label={`Удалить участника ${participantName}`}
                  title="Удалить"
                >
                  <X className="h-4 w-4" />
                </button>
              ) : null}
            </div>
          );
        })}
      </div>
    );
  };
  const canShowApprovalActions = Boolean(
    state?.task
    && (form.status === 'onApproval' || form.status === 'overdueApproval')
    && currentApprovalParticipant
    && currentApprovalParticipant.userId === currentUserId
    && currentApprovalParticipant.approvalStatus !== 'approved',
  );
  const approvalChoiceDialog = approvalChoice ? (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/35 px-4 py-6">
      <div className="w-full max-w-xl rounded-2xl bg-white p-4 shadow-2xl">
        <div className="text-base font-semibold text-slate-900">Завершить согласование</div>
        <p className="mt-1 text-sm text-slate-500">
          Вы последний согласующий по задаче. Выберите, что сделать после согласования.
        </p>
        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <button
            type="button"
            onClick={() => void confirmApprovalChoice('complete')}
            className="rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-left text-sm font-semibold text-emerald-800 transition hover:bg-emerald-100"
          >
            Согласовать и завершить задачу
            <span className="mt-1 block text-xs font-normal text-emerald-700">
              Исполнитель получит уведомление о согласовании, а задача будет закрыта и перейдет в статус «Готово».
            </span>
          </button>
          <button
            type="button"
            onClick={() => void confirmApprovalChoice('returnToAssignee')}
            className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-3 text-left text-sm font-semibold text-blue-800 transition hover:bg-blue-100"
          >
            Согласовать и отправить исполнителю
            <span className="mt-1 block text-xs font-normal text-blue-700">Исполнитель получит уведомление, задача останется согласованной.</span>
          </button>
        </div>
        <div className="mt-4 flex justify-end">
          <button
            type="button"
            onClick={() => setApprovalChoice(null)}
            className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
          >
            Отмена
          </button>
        </div>
      </div>
    </div>
  ) : null;
  const revisionReviewDialog = revisionReview ? (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-slate-950/45 px-4 py-6 backdrop-blur-sm">
      <div className="w-full max-w-2xl rounded-2xl bg-white p-5 shadow-2xl">
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="text-lg font-semibold text-slate-950">Рецензия на доработку</div>
            <p className="mt-1 text-sm leading-6 text-slate-600">
              Укажите, почему задача возвращается исполнителю и какие поправки нужно внести перед повторным согласованием.
            </p>
          </div>
          <button
            type="button"
            onClick={() => {
              if (revisionReviewSaving) return;
              setRevisionReview(null);
              setRevisionReviewError(null);
              setRevisionReviewDueAt('');
            }}
            className="rounded-lg p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700"
            aria-label="Закрыть рецензию"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="mt-4 rounded-xl border border-orange-100 bg-orange-50 px-3 py-2 text-sm text-orange-800">
          Согласующий: {revisionReview.approverName}. После отправки задача перейдет в статус «На доработке», а исполнитель увидит обязательное сообщение с этой рецензией.
        </div>
        <label className="mt-4 block text-sm font-semibold text-slate-700">
          Срок доработки
          <div className="mt-2 flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 focus-within:border-orange-400 focus-within:ring-2 focus-within:ring-orange-100">
            <CalendarClock className="h-4 w-4 text-slate-400" />
            <SystemDateInput
              type="datetime-local"
              value={revisionReviewDueAt}
              onChange={(event) => {
                setRevisionReviewDueAt(event.target.value);
                if (revisionReviewError) setRevisionReviewError(null);
              }}
              className="min-w-0 flex-1 bg-transparent text-sm text-slate-900 outline-none"
              data-testid="task-revision-due-at-input"
            />
          </div>
          <span className="mt-1 block text-xs font-normal text-slate-500">
            Если указать дату, она станет новым сроком задачи в журнале, календаре и напоминаниях. Если оставить поле пустым, срок задачи не изменится.
          </span>
        </label>
        <label className="mt-4 block text-sm font-semibold text-slate-700">
          Комментарий для исполнителя
          <textarea
            value={revisionReviewText}
            onChange={(event) => {
              setRevisionReviewText(event.target.value);
              if (revisionReviewError) setRevisionReviewError(null);
            }}
            className="mt-2 min-h-[160px] w-full resize-y rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm leading-6 outline-none focus:border-orange-400 focus:ring-2 focus:ring-orange-100"
            maxLength={4000}
            autoFocus
            placeholder="Например: исправить дату, приложить документ, уточнить формулировку..."
          />
        </label>
        <div className="mt-2 flex items-center justify-between gap-3 text-xs">
          <span className={revisionReviewError ? 'font-semibold text-rose-600' : 'text-slate-400'}>
            {revisionReviewError ?? 'Рецензия сохранится в истории задачи и будет показана исполнителю.'}
          </span>
          <span className="shrink-0 text-slate-400">{revisionReviewText.trim().length}/4000</span>
        </div>
        <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={() => {
              if (revisionReviewSaving) return;
              setRevisionReview(null);
              setRevisionReviewError(null);
              setRevisionReviewDueAt('');
            }}
            className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:opacity-60"
            disabled={revisionReviewSaving}
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => void submitRevisionReview()}
            className="rounded-xl bg-orange-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-orange-700 disabled:cursor-not-allowed disabled:opacity-60"
            disabled={revisionReviewSaving}
          >
            {revisionReviewSaving ? 'Отправляем...' : 'Отправить на доработку с поправками'}
          </button>
        </div>
      </div>
    </div>
  ) : null;

  const deadlineRequestReasonDialog = deadlineRequestReasonDialogOpen ? (
    <div className="fixed inset-0 z-[70] flex items-start justify-center overflow-y-auto bg-slate-950/45 px-3 py-4 backdrop-blur-sm sm:items-center sm:px-4 sm:py-6">
      <div
        className="max-h-[calc(100vh-2rem)] w-full max-w-[min(40rem,calc(100vw-1.5rem))] overflow-y-auto rounded-2xl border border-amber-200 bg-white p-4 shadow-2xl sm:p-5"
        role="dialog"
        aria-modal="true"
        aria-labelledby="deadline-request-reason-title"
        data-testid="deadline-request-reason-dialog"
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">Запрос переноса срока</div>
            <h2 id="deadline-request-reason-title" className="mt-2 text-lg font-semibold text-slate-950">
              Укажите причину переноса срока
            </h2>
            <p className="mt-2 line-clamp-2 text-sm text-slate-600">{state?.task?.title}</p>
          </div>
          <button
            type="button"
            onClick={closeDeadlineRequestReasonDialog}
            disabled={deadlineRequestSaving}
            className="rounded-lg p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700 disabled:cursor-wait disabled:opacity-60"
            aria-label="Закрыть окно причины переноса срока"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          Новый срок: {deadlineRequestDueAt ? formatDateTime(fromLocalInputValue(deadlineRequestDueAt)) : 'не выбран'}
        </div>
        <label className="mt-4 block text-sm font-semibold text-slate-700" htmlFor="deadline-request-reason">
          Причина запроса
        </label>
        <textarea
          id="deadline-request-reason"
          value={deadlineRequestReasonDraft}
          onChange={(event) => {
            setDeadlineRequestReasonDraft(event.target.value);
            if (deadlineRequestReasonError) {
              setDeadlineRequestReasonError(null);
            }
          }}
          className="mt-2 min-h-[140px] w-full resize-y rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm leading-6 text-slate-700 outline-none transition focus:border-amber-400 focus:ring-2 focus:ring-amber-100 disabled:cursor-wait disabled:bg-slate-50"
          maxLength={1000}
          disabled={deadlineRequestSaving}
          placeholder="Например: жду документы от клиента, нужно дополнительное время на проверку."
          autoFocus
        />
        <div className="mt-2 flex items-center justify-between gap-3 text-xs">
          <span className={deadlineRequestReasonError ? 'font-semibold text-rose-600' : 'text-slate-500'}>
            {deadlineRequestReasonError ?? 'Без причины запрос не будет отправлен постановщику.'}
          </span>
          <span className="shrink-0 text-slate-400">{deadlineRequestReasonDraft.trim().length}/1000</span>
        </div>
        <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={closeDeadlineRequestReasonDialog}
            disabled={deadlineRequestSaving}
            className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:cursor-wait disabled:opacity-60"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => void handleDeadlineRequest()}
            disabled={deadlineRequestSaving || !deadlineRequestReasonDraft.trim()}
            className="rounded-xl bg-amber-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-amber-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {deadlineRequestSaving ? 'Отправляем...' : 'Отправить запрос'}
          </button>
        </div>
      </div>
    </div>
  ) : null;

  const approvalResultPromptDialog = approvalResultPrompt ? (
    <TaskCompletionResultPrompt
      task={state?.task}
      taskTitle={approvalResultPrompt.form.title}
      eyebrow={approvalResultPrompt.form.status === 'done' ? 'Завершение задачи' : 'Отправка на согласование'}
      submitLabel={approvalResultPrompt.form.status === 'done' ? 'Завершить задачу' : 'Отправить на согласование'}
      result={approvalResultPromptResult}
      error={approvalResultPromptError}
      saving={approvalResultPromptSaving}
      requireCompletionResult={requireCompletionResult}
      onResultChange={(value) => {
        setApprovalResultPromptResult(value);
        if (approvalResultPromptError) {
          setApprovalResultPromptError(null);
        }
      }}
      onSubmit={(files) => void submitApprovalResultPrompt(files)}
      onClose={closeApprovalResultPrompt}
    />
  ) : null;

  const deadlineChangeRequestsSection = taskId && (deadlineChangeRequestsLoading || deadlineChangeRequests.length > 0) ? (
    <section className="rounded-xl border border-amber-200 bg-amber-50/70 p-3" data-testid="task-deadline-change-requests">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h3 className="flex items-center gap-2 text-sm font-semibold text-amber-950">
            <TimerReset className="h-4 w-4 text-amber-700" />
            Запросы переноса срока
          </h3>
          <p className="mt-1 text-xs leading-5 text-amber-800">
            Здесь видно, кто запросил перенос, какой срок предложен и кто должен принять решение.
          </p>
        </div>
        <span className="rounded-full bg-white px-2.5 py-1 text-xs font-semibold text-amber-800 ring-1 ring-amber-200">
          {deadlineChangeRequestsLoading ? 'Загрузка' : `${deadlineChangeRequests.length} шт.`}
        </span>
      </div>

      {deadlineChangeRequestsLoading ? (
        <div className="mt-3 rounded-lg border border-amber-100 bg-white px-3 py-2 text-sm text-amber-700">Загружаем запросы...</div>
      ) : (
        <div className="mt-3 space-y-2">
          {deadlineChangeRequests.map((request) => (
            <article key={request.id} className="rounded-lg border border-amber-100 bg-white px-3 py-2 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className={`rounded-full border px-2.5 py-1 text-xs font-semibold ${getDeadlineChangeRequestStatusClass(request.status)}`}>
                  {getDeadlineChangeRequestStatusLabel(request.status)}
                </span>
                <span className="text-xs text-slate-500">{formatDateTime(request.createdAt)}</span>
              </div>
              <div className="mt-2 grid gap-2 text-slate-700 sm:grid-cols-2">
                <div>
                  <div className="text-xs font-semibold uppercase text-slate-400">Запросил</div>
                  <div className="font-medium">{request.requestedByName || 'Не указано'}</div>
                </div>
                <div>
                  <div className="text-xs font-semibold uppercase text-slate-400">Действие</div>
                  <div className="font-medium">{getDeadlineChangeRequestActionLabel(request)}</div>
                </div>
                <div>
                  <div className="text-xs font-semibold uppercase text-slate-400">Текущий срок</div>
                  <div>{formatDateTime(request.currentDueAt)}</div>
                </div>
                <div>
                  <div className="text-xs font-semibold uppercase text-slate-400">Запрошенный срок</div>
                  <div>{formatDateTime(request.requestedDueAt)}</div>
                </div>
              </div>
              <div className="mt-2 rounded-md bg-amber-50 px-2 py-2 text-sm leading-5 text-amber-900">
                <span className="font-semibold">Причина:</span> {request.reason || 'Не указана'}
              </div>
              {request.status === 'pending'
                && state?.task
                && (isAdminUser || (currentUserId !== null && request.reviewerUserId === currentUserId)) ? (
                  <button
                    type="button"
                    onClick={() => onReviewDeadlineChange(state.task!, request)}
                    className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-lg bg-amber-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-amber-700"
                  >
                    <TimerReset className="h-4 w-4" />
                    Рассмотреть запрос: согласовать или отказать
                  </button>
                ) : null}
              {request.decisionComment ? (
                <div className="mt-2 rounded-md bg-slate-50 px-2 py-2 text-sm leading-5 text-slate-700">
                  <span className="font-semibold">Решение:</span> {request.decisionComment}
                </div>
              ) : null}
            </article>
          ))}
        </div>
      )}
    </section>
  ) : null;

  if (isModernChrome) {
    const TypeIcon = typeOption.icon;
    const titleText = state?.mode === 'view'
      ? 'Просмотр задачи'
      : state?.mode === 'edit'
        ? 'Редактировать задачу'
        : 'Новая задача';

    return (
      <div className={shellClassName} data-testid="task-modern-card-window">
        <div className={panelClassName}>
          <div className="flex items-center justify-between gap-3 border-b border-slate-200 px-5 py-3">
            <div className="flex min-w-0 flex-wrap items-center gap-2 text-sm text-slate-500">
              <span className="truncate font-semibold text-slate-700">{form.objectName || 'Выберите проект'}</span>
              <ChevronRight className="h-4 w-4 shrink-0 text-slate-300" />
              <span className="truncate">{selectedCaseTitle || 'Без дела'}</span>
              {state?.task ? (
                <>
                  <ChevronRight className="h-4 w-4 shrink-0 text-slate-300" />
                  <span className="shrink-0">#{state.task.id}</span>
                </>
              ) : null}
            </div>
            <div className="flex shrink-0 items-center gap-1">
              {state?.task ? (
                <button
                  type="button"
                  onClick={() => void saveWithStatus('inProgress')}
                  disabled={isReadOnly && !canPostComment}
                  className="inline-flex h-9 items-center gap-2 rounded-lg bg-blue-50 px-3 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  <Play className="h-4 w-4" />
                  Запустить
                </button>
              ) : null}
              {isLockedViewWithEditAccess ? (
                <button
                  type="button"
                  onClick={onEnableEdit}
                  className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-600 transition hover:bg-slate-100 hover:text-slate-900"
                  title="Редактировать задачу"
                  aria-label="Редактировать задачу"
                >
                  <Pencil className="h-4 w-4" />
                </button>
              ) : (
                <button type="button" className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-500 transition hover:bg-slate-100" title="Просмотр" aria-label="Просмотр">
                  <Eye className="h-4 w-4" />
                </button>
              )}
              <button type="button" className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-amber-600 transition hover:bg-amber-50" title="Напоминание" aria-label="Напоминание">
                <Bell className="h-4 w-4" />
              </button>
              {state?.task && !isReadOnly && canDuplicateTask ? (
                <button
                  type="button"
                  onClick={() => void handleDuplicateTask()}
                  className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-500 transition hover:bg-blue-50 hover:text-blue-700"
                  title="Дублировать задачу — создать отдельную копию"
                  aria-label="Дублировать задачу"
                >
                  <Copy className="h-4 w-4" data-testid="task-duplicate-icon" />
                </button>
              ) : null}
              {canCancelTask ? (
                <button
                  type="button"
                  onClick={() => void handleCancelTask()}
                  className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-500 transition hover:bg-rose-50 hover:text-rose-700"
                  title="Отменить задачу — оставить в журнале со статусом «Отменена»"
                  aria-label="Отменить задачу"
                >
                  <XCircle className="h-4 w-4" data-testid="task-cancel-icon" />
                </button>
              ) : null}
              {state?.task && canDeleteTask ? (
                <div className="relative">
                  <button
                    type="button"
                    onClick={() => setActionMenuOpen((open) => !open)}
                    className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-500 transition hover:bg-slate-100"
                    title="Еще"
                    aria-label="Еще"
                  >
                    <MoreHorizontal className="h-4 w-4" />
                  </button>
                  {actionMenuOpen ? (
                    <div className="absolute right-0 z-20 mt-2 w-56 overflow-hidden rounded-xl border border-slate-200 bg-white py-1 text-sm shadow-xl">
                      <button
                        type="button"
                        onClick={() => void handleDeleteTask()}
                        className="flex w-full items-start gap-3 px-3 py-2.5 text-left text-rose-600 transition hover:bg-rose-50"
                        title="Удалить задачу безвозвратно"
                      >
                        <Trash2 className="mt-0.5 h-4 w-4 shrink-0" />
                        <span>
                          <span className="block font-semibold">Удалить задачу</span>
                          <span className="mt-0.5 block text-xs font-normal text-rose-400">Безвозвратно удалить запись из журнала</span>
                        </span>
                      </button>
                    </div>
                  ) : null}
                </div>
              ) : null}
              {!isPage ? (
                <button type="button" onClick={onClose} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" aria-label="Закрыть">
                  <X className="h-5 w-5" />
                </button>
              ) : (
                <button type="button" onClick={onClose} className="inline-flex h-9 items-center gap-2 rounded-lg border border-slate-200 px-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-50">
                  <ArrowLeft className="h-4 w-4" />
                  К журналу
                </button>
              )}
            </div>
          </div>

          <div className={isPage ? 'grid gap-4 p-4 xl:grid-cols-[minmax(0,1fr)_380px]' : 'grid min-h-0 gap-4 overflow-y-auto p-4 xl:grid-cols-[minmax(0,1fr)_380px]'}>
            <main className="min-w-0 space-y-4">
              <section className="space-y-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600">
                    <ClipboardList className="h-3.5 w-3.5" />
                    {titleText}
                  </span>
                  <span className="inline-flex items-center gap-2 rounded-full bg-orange-50 px-3 py-1 text-xs font-semibold text-orange-700">
                    <span className="h-2 w-2 rounded-full bg-orange-500" />
                    {statusOption.label}
                  </span>
                  {state?.task ? <TaskBillingBadge task={state.task} /> : form.clientBillingClassification ? (
                    <TaskStickerBadge
                      stickerKey={form.clientBillingClassification === 'billable'
                        ? form.billingMode === 'Fixed' ? 'billing.fixed' : 'billing.unbilled'
                        : form.clientId || form.legalEntityId
                          ? 'billing.unpaid'
                          : 'billing.internal'}
                      icon={<WalletCards className="h-3.5 w-3.5 shrink-0" />}
                    />
                  ) : null}
                </div>
                <h2 className="sr-only">{titleText}</h2>
                <input
                  ref={titleInputRef}
                  value={form.title}
                  onChange={(event) => update('title', event.target.value)}
                  disabled={isReadOnly}
                  data-testid="task-title-input"
                  className="w-full border-0 border-b border-slate-200 bg-transparent px-0 py-2 text-2xl font-semibold text-slate-950 outline-none transition placeholder:text-slate-300 focus:border-blue-400 focus:ring-0"
                  placeholder="Введите название задачи"
                />
              </section>

              <section className="rounded-xl border border-slate-200 bg-white p-3">
                <div className="mb-2 flex items-center gap-2 text-sm font-semibold text-slate-700">
                  <ListFilter className="h-4 w-4" />
                  Описание задачи
                </div>
                <textarea
                  value={form.description}
                  onChange={(event) => update('description', event.target.value)}
                  disabled={isReadOnly}
                  data-testid="task-description-field"
                  className="min-h-[126px] w-full resize-y rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-800 outline-none transition placeholder:text-slate-400 focus:border-blue-400 focus:bg-white focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:text-slate-500"
                  rows={5}
                  placeholder="Добавить описание"
                />
              </section>

              <section className={`rounded-xl border p-3 ${canEditResult ? 'border-emerald-200 bg-emerald-50/50' : 'border-dashed border-slate-200 bg-slate-50'}`}>
                <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
                  <div className="flex items-center gap-2 text-sm font-semibold text-slate-700">
                    <CheckCircle2 className={`h-4 w-4 ${canEditResult ? 'text-emerald-600' : 'text-slate-400'}`} />
                    Результат
                  </div>
                  {!canEditResult ? (
                    <span className="rounded-full bg-white px-2.5 py-1 text-xs font-semibold text-slate-500 ring-1 ring-slate-200">
                      Доступен при завершении
                    </span>
                  ) : null}
                </div>
                <textarea
                  value={form.result}
                  onChange={(event) => update('result', event.target.value)}
                  disabled={!canEditResult}
                  data-testid="task-result-field"
                  className="min-h-[92px] w-full resize-y rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 outline-none transition placeholder:text-slate-400 focus:border-emerald-400 focus:ring-2 focus:ring-emerald-100 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                  rows={4}
                  placeholder={canEditResult ? TASK_COMPLETION_RESULT_HINT : 'Результат появится при завершении задачи'}
                />
                {resultError ? <p className="mt-2 text-xs font-semibold text-rose-600">{resultError}</p> : null}
              </section>

              {deadlineChangeRequestsSection}

              <section className="rounded-xl border border-slate-200 bg-white">
                <div className="flex flex-wrap items-center gap-2 border-b border-slate-200 px-3 py-2 text-sm">
                  <span className="rounded-lg bg-blue-50 px-3 py-1.5 font-semibold text-blue-700">Комментарии {comments.length}</span>
                  <span className="rounded-lg px-3 py-1.5 font-semibold text-slate-500">Файлы {files.length}</span>
                  <span className="rounded-lg px-3 py-1.5 font-semibold text-slate-500">Таймер</span>
                  <span className="rounded-lg px-3 py-1.5 font-semibold text-slate-500">Активность</span>
                </div>
                <div className="min-h-[220px] space-y-3 p-3">
                  {!taskId ? (
                    <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                      Комментарии и файлы появятся после сохранения задачи.
                    </div>
                  ) : commentsLoading ? (
                    <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">Загружаем историю...</div>
                  ) : comments.length === 0 ? (
                    <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">Здесь пока нет сообщений.</div>
                  ) : (
                    <div className="max-h-[260px] space-y-2 overflow-y-auto pr-1">
                      {comments.map((comment) => (
                        <article key={comment.id} className={`max-w-[82%] rounded-2xl px-3 py-2 text-sm shadow-sm ${comment.kind === 'system' ? 'bg-slate-100 text-slate-600' : 'ml-auto bg-blue-500 text-white'}`}>
                          <div className="font-semibold">{comment.authorName}</div>
                          <div className="mt-1 whitespace-pre-wrap">{comment.text}</div>
                          <div className={`mt-1 text-[11px] ${comment.kind === 'system' ? 'text-slate-400' : 'text-blue-100'}`}>{formatDateTime(comment.createdAt)}</div>
                        </article>
                      ))}
                    </div>
                  )}
                  {taskId && canPostComment ? (
                    <div className="rounded-xl border border-slate-200 bg-slate-50 p-2">
                      <div className="flex items-end gap-2">
                        <button type="button" className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-slate-500 transition hover:bg-white" title="Упомянуть" aria-label="Упомянуть">
                          @
                        </button>
                        <textarea
                          value={commentText}
                          onChange={(event) => setCommentText(event.target.value)}
                          className="min-h-[40px] flex-1 resize-none border-0 bg-transparent px-2 py-2 text-sm outline-none placeholder:text-slate-400 focus:ring-0"
                          rows={1}
                          maxLength={4000}
                          placeholder="Напишите комментарий..."
                        />
                        {canManageFiles ? (
                          <button type="button" onClick={() => newFileInputRef.current?.click()} className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-slate-500 transition hover:bg-white" title="Прикрепить файл" aria-label="Прикрепить файл">
                            <UploadCloud className="h-4 w-4" />
                          </button>
                        ) : null}
                        <button
                          type="button"
                          onClick={handleAddComment}
                          disabled={commentSaving || !commentText.trim()}
                          className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-blue-500 text-white transition hover:bg-blue-600 disabled:cursor-not-allowed disabled:opacity-60"
                          aria-label="Отправить комментарий"
                        >
                          {commentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                        </button>
                      </div>
                      <label className="mt-2 inline-flex cursor-pointer items-center gap-2 px-2 text-xs font-medium text-slate-600">
                        <input
                          type="checkbox"
                          checked={commentExternalCalendarVisible}
                          onChange={(event) => setCommentExternalCalendarVisible(event.target.checked)}
                          className="form-checkbox"
                        />
                        Доступен во внешнем календаре
                      </label>
                    </div>
                  ) : null}
                </div>
              </section>

              {fileGroups.length > 0 ? (
                <section className="rounded-xl border border-slate-200 bg-white p-3">
                  <div className="mb-3 flex items-center justify-between gap-2">
                    <div className="flex items-center gap-2 text-sm font-semibold text-slate-700">
                      <FileText className="h-4 w-4" />
                      Файлы
                    </div>
                    {canManageFiles ? (
                      <button type="button" onClick={() => newFileInputRef.current?.click()} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-xs font-semibold text-blue-700 transition hover:bg-blue-100">
                        <Plus className="h-4 w-4" />
                        Добавить
                      </button>
                    ) : null}
                  </div>
                  <div className="space-y-2">
                    {fileGroups.map((group) => (
                      <div key={group.documentKey} className="flex items-center gap-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                        <span className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-white text-slate-600 ring-1 ring-slate-200">
                          <FileText className="h-4 w-4" />
                        </span>
                        <div className="min-w-0 flex-1">
                          <div className="truncate text-sm font-semibold text-slate-800">{group.latest.originalFileName}</div>
                          <div className="text-xs text-slate-500">v{group.latest.version} · {formatFileSize(group.latest.sizeBytes)}</div>
                        </div>
                        <button type="button" onClick={() => void handleDownloadFile(group.latest)} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-blue-600 transition hover:bg-white" title="Скачать" aria-label="Скачать файл">
                          <Download className="h-4 w-4" />
                        </button>
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}

              <input
                ref={newFileInputRef}
                type="file"
                className="hidden"
                onChange={(event) => {
                  void handleUploadFile(event.currentTarget.files);
                  event.currentTarget.value = '';
                }}
              />
            </main>

            <aside className="space-y-3 xl:sticky xl:top-0 xl:self-start">
              <div className="grid grid-cols-[minmax(0,1fr)_auto] gap-2">
                <SystemSelect
                  value={form.status}
                  onChange={(event) => update('status', event.target.value as TaskJournalStatus)}
                  disabled={isReadOnly}
                  data-testid="task-editor-status-select"
                  className="h-11 rounded-xl border border-orange-300 bg-white px-3 text-sm font-semibold text-slate-700 outline-none focus:border-orange-400 focus:ring-2 focus:ring-orange-100 disabled:bg-slate-50"
                >
                  {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </SystemSelect>
                {!isReadOnly || canCompleteReadOnlyTask || isAwaitingApproval ? (
                  <button
                    type="button"
                    onClick={() => void handleCompletionAction()}
                    disabled={approvalCompletionDisabled}
                    className="inline-flex h-11 items-center justify-center gap-2 whitespace-nowrap rounded-xl bg-emerald-50 px-3 text-sm font-semibold text-emerald-700 transition hover:bg-emerald-100 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-400"
                    title={completionButtonLabel}
                    aria-label={completionButtonLabel}
                  >
                    <CheckCircle2 className="h-5 w-5" />
                    <span>{compactCompletionButtonLabel}</span>
                  </button>
                ) : <span aria-hidden="true" />}
              </div>
              {canSendToAuthorApproval ? (
                <button
                  type="button"
                  onClick={() => void saveWithStatus('onApproval')}
                  className="inline-flex w-full items-center justify-center rounded-xl border border-violet-200 bg-violet-50 px-3 py-2 text-sm font-semibold text-violet-700 transition hover:border-violet-300 hover:bg-violet-100"
                >
                  {sendToAuthorApprovalLabel}
                </button>
              ) : null}

              <section className="space-y-2 rounded-xl border border-slate-200 bg-white p-2">
                <ModernTaskQuickField icon={<UsersRound className="h-4 w-4" />} label="Исполнители">
                  {renderAssigneeControl(true)}
                </ModernTaskQuickField>
                {state?.task ? (
                  <ModernTaskQuickField icon={<Pencil className="h-4 w-4" />} label="Постановщик">
                    <SystemSelect
                      value={form.authorUserId ?? ''}
                      onChange={(event) => update('authorUserId', Number(event.target.value) || null)}
                      disabled={isReadOnly || !isAdminUser}
                      data-testid="task-author-select"
                      className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white disabled:text-slate-500"
                    >
                      {form.authorUserId && !selectedTaskAuthor ? (
                        <option value={form.authorUserId}>{taskAuthorLabel}</option>
                      ) : null}
                      {assignees.map((assignee) => (
                        <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                      ))}
                    </SystemSelect>
                  </ModernTaskQuickField>
                ) : null}
                <div className="grid grid-cols-2 items-start gap-3 rounded-xl bg-slate-50 p-2.5" data-testid="task-schedule-fields-row">
                  <ModernTaskQuickField icon={<CalendarClock className="h-4 w-4" />} label="Дата" embedded>
                    <SystemDateInput
                      type="datetime-local"
                      value={form.dueAt}
                      onChange={(event) => update('dueAt', event.target.value)}
                      disabled={isReadOnly}
                      data-testid="task-due-at-input"
                      displayClassName="text-center"
                      showInlineClear={false}
                      className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white"
                    />
                  </ModernTaskQuickField>
                  <ModernTaskQuickField icon={<Clock3 className="h-4 w-4" />} label="Длительность" embedded>
                    <SystemSelect
                      value={normalizeTaskDuration(form.plannedDurationMinutes)}
                      onChange={(event) => update('plannedDurationMinutes', Number(event.target.value))}
                      disabled={isReadOnly}
                      className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-center text-sm outline-none focus:border-blue-400 focus:bg-white"
                    >
                      {[15, 30, 45, 60, 90, 120, 180, 240, 360, 480].map((minutes) => (
                        <option key={minutes} value={minutes}>{formatDuration(minutes)}</option>
                      ))}
                    </SystemSelect>
                  </ModernTaskQuickField>
                </div>
                <div className="grid grid-cols-2 items-start gap-3 rounded-xl bg-slate-50 p-2.5" data-testid="task-classification-fields-row">
                  <ModernTaskQuickField icon={<TypeIcon className="h-4 w-4" />} label="Тип задачи" embedded>
                    <SystemSelect value={form.type} onChange={(event) => update('type', event.target.value as TaskJournalType)} disabled={isReadOnly} className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white">
                      {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                    </SystemSelect>
                  </ModernTaskQuickField>
                  <ModernTaskQuickField icon={<Scale className="h-4 w-4" />} label="Приоритет" embedded>
                    <SystemSelect value={form.priority} onChange={(event) => update('priority', event.target.value as TaskJournalPriority)} disabled={isReadOnly} className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white">
                      {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                    </SystemSelect>
                  </ModernTaskQuickField>
                </div>
                <ModernTaskQuickField icon={<WalletCards className="h-4 w-4" />} label="Оплата исполнителю">
                  <label className="flex items-start gap-2 rounded-lg border border-emerald-100 bg-emerald-50/70 px-2 py-2 text-sm text-slate-700">
                    <SystemCheckbox
                      type="checkbox"
                      checked={Boolean(form.contractorPaymentEnabled)}
                      disabled={isReadOnly || !canUseContractorPayment}
                      onChange={(event) => update('contractorPaymentEnabled', event.target.checked)}
                      data-testid="task-contractor-payment-checkbox"
                      className="mt-0.5 h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500 disabled:opacity-50"
                    />
                    <span className="min-w-0">
                      <span className="block font-medium text-slate-800">
                        {form.contractorPaymentEnabled ? 'Оплата включена' : 'Указать оплату'}
                      </span>
                      <span className="mt-1 block text-xs leading-5 text-slate-500">
                        {canUseContractorPayment
                          ? 'После сохранения будет подготовлен расходный платеж.'
                          : 'Доступно для исполнителя с признаком привлеченного.'}
                      </span>
                    </span>
                  </label>
                  {form.contractorPaymentEnabled ? (
                    <div className="mt-2 space-y-2">
                      <label className="block text-xs font-medium text-slate-600">
                        Сумма
                        <input
                          type="number"
                          min="0"
                          step="0.01"
                          value={form.contractorPaymentAmount ?? ''}
                          onChange={(event) => update('contractorPaymentAmount', event.target.value ? Number(event.target.value) : null)}
                          disabled={isReadOnly}
                          data-testid="task-contractor-payment-amount"
                          className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-2 py-2 text-sm outline-none focus:border-emerald-400 focus:ring-2 focus:ring-emerald-100 disabled:bg-slate-50"
                        />
                      </label>
                      <label className="block text-xs font-medium text-slate-600">
                        Точная дата оплаты
                        <SystemDateInput
                          type="date"
                          value={form.contractorPaymentDueDate ? String(form.contractorPaymentDueDate).split('T')[0] : ''}
                          onChange={(event) => update('contractorPaymentDueDate', event.target.value || null)}
                          disabled={isReadOnly}
                          data-testid="task-contractor-payment-date"
                          className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-2 py-2 text-sm outline-none focus:border-emerald-400 focus:ring-2 focus:ring-emerald-100 disabled:bg-slate-50"
                        />
                      </label>
                      <label className="flex items-center gap-2 rounded-lg border border-emerald-100 bg-white px-2 py-2 text-xs font-medium text-slate-700">
                        <SystemCheckbox
                          type="checkbox"
                          checked={Boolean(form.contractorPaymentOnResult)}
                          onChange={(event) => {
                            const checked = event.target.checked;
                            setForm((prev) => ({
                              ...prev,
                              contractorPaymentOnResult: checked,
                              contractorPaymentResultDelayDays: checked
                                ? prev.contractorPaymentResultDelayDays ?? 5
                                : null,
                            }));
                          }}
                          disabled={isReadOnly}
                          data-testid="task-contractor-payment-on-result"
                          className="h-4 w-4 rounded border-slate-300 text-emerald-600"
                        />
                        Оплата по результату
                      </label>
                      {form.contractorPaymentOnResult ? (
                        <label className="block text-xs font-medium text-slate-600">
                          Через дней после результата
                          <input
                            type="number"
                            min="0"
                            max="365"
                            step="1"
                            value={form.contractorPaymentResultDelayDays ?? ''}
                            onChange={(event) => update('contractorPaymentResultDelayDays', event.target.value === '' ? null : Number(event.target.value))}
                            disabled={isReadOnly}
                            data-testid="task-contractor-payment-result-delay-days"
                            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-2 py-2 text-sm outline-none focus:border-emerald-400 focus:ring-2 focus:ring-emerald-100 disabled:bg-slate-50"
                          />
                        </label>
                      ) : null}
                      {form.contractorPaymentOnResult && form.contractorPaymentDueDate ? (
                        <div className="rounded-lg border border-rose-200 bg-rose-50 px-2 py-2 text-xs leading-5 text-rose-700">
                          Заполните только один вариант: точную дату оплаты или оплату по результату.
                        </div>
                      ) : form.contractorPaymentOnResult ? (
                        <div className="rounded-lg border border-emerald-100 bg-white px-2 py-2 text-xs leading-5 text-slate-600">
                          Платеж появится после принятия работы и завершения задачи.
                        </div>
                      ) : null}
                      {form.contractorPaymentId ? (
                        <div className="rounded-lg border border-emerald-100 bg-white px-2 py-2 text-xs leading-5 text-slate-600">
                          Платеж #{form.contractorPaymentId}
                          {form.contractorPaymentStatus ? ` · ${form.contractorPaymentStatus}` : ''}
                          {form.contractorPaymentAmount ? ` · ${formatCurrencySmart(Number(form.contractorPaymentAmount)).full}` : ''}
                        </div>
                      ) : null}
                    </div>
                  ) : null}
                </ModernTaskQuickField>
                <ModernTaskQuickField icon={<FileText className="h-4 w-4" />} label="Файлы">
                  <button
                    type="button"
                    onClick={() => newFileInputRef.current?.click()}
                    disabled={!canManageFiles || uploadingDocumentKey !== null}
                    className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-dashed border-slate-300 bg-slate-50 px-2 py-2 text-sm font-semibold text-slate-600 transition hover:bg-white disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <UploadCloud className="h-4 w-4" />
                    {files.length ? `${files.length} файлов` : 'Добавить файл'}
                  </button>
                </ModernTaskQuickField>
                <ModernTaskQuickField icon={<ClipboardList className="h-4 w-4" />} label="Подзадачи">
                  <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-2 py-2 text-sm text-slate-500">0/0</div>
                </ModernTaskQuickField>
              </section>

              <section className="rounded-xl border border-slate-200 bg-white p-3">
                <div className="mb-2 text-sm font-semibold text-slate-700">Связи</div>
                <label className="mb-2 flex items-center gap-2 text-sm text-slate-600">
                  <SystemCheckbox
                    type="checkbox"
                    checked={Boolean(form.isInternal)}
                    disabled={isReadOnly}
                    onChange={(event) => handleInternalChange(event.target.checked)}
                    data-testid="task-internal-checkbox"
                    className="h-4 w-4 rounded border-slate-300 text-blue-600"
                  />
                  Общая задача без контрагента
                </label>
                <LookupSelect
                  value={selectedCounterpartyValue}
                  options={counterpartyOptions}
                  onChange={handleCounterpartyChange}
                  placeholder="Контрагент"
                  title="Выбор контрагента"
                  dictionaryLabel="Открыть справочник"
                  searchPlaceholder="Поиск"
                  emptyOptionLabel="Контрагент не выбран"
                  buttonClassName="h-10 border-slate-200 text-sm"
                  disabled={isReadOnly || form.isInternal || isCreationBillingBindingLocked}
                />
                <SystemSelect
                  aria-label="Дело"
                  value={form.clientCaseId ?? ''}
                  onChange={(event) => handleCaseChange(event.target.value)}
                  disabled={isReadOnly || form.isInternal || isCreationBillingBindingLocked || (!form.clientId && !form.legalEntityId) || casesLoading}
                  className="mt-2 w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white disabled:text-slate-400"
                >
                  <option value="">Без дела</option>
                  {selectableClientCases.map((clientCase) => (
                    <option key={clientCase.id} value={clientCase.id}>{clientCase.title}</option>
                  ))}
                </SystemSelect>
              </section>

              <section className="rounded-xl border border-slate-200 bg-white p-3">
                <div className="mb-2 text-sm font-semibold text-slate-700">Участники</div>
                <div className="grid gap-2">
                  <div
                    className="space-y-2 rounded-xl border border-violet-100 bg-violet-50/40 p-2"
                    data-testid="task-modern-approver-group"
                  >
                    <ModernParticipantRow
                      label="Согласующие"
                      count={visibleApprovers.length}
                      description="Принимают решение по задаче по очереди"
                      tone="violet"
                    />
                    {renderModernSelectedParticipantList('approver', visibleApprovers, 'task-modern-approver-list')}
                    {!isReadOnly ? (
                      <SystemSelect
                        aria-label="Добавить согласующего"
                        value=""
                        onChange={(event) => {
                          const selected = assignees.find((assignee) => assignee.id === Number(event.target.value));
                          if (selected) addParticipant('approver', selected);
                        }}
                        className="w-full rounded-lg border border-violet-200 bg-white px-2 py-2 text-sm outline-none focus:border-violet-400"
                      >
                        <option value="">Добавить согласующего</option>
                        {getAvailableAssigneesForRole('approver').map((assignee) => (
                          <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                        ))}
                      </SystemSelect>
                    ) : null}
                  </div>
                  <div
                    className="space-y-2 rounded-xl border border-sky-100 bg-sky-50/40 p-2"
                    data-testid="task-modern-observer-group"
                  >
                    <ModernParticipantRow
                      label="Наблюдатели"
                      count={visibleObservers.length}
                      description="Следят за задачей, но не принимают решение"
                      tone="sky"
                    />
                    {renderModernSelectedParticipantList('observer', visibleObservers, 'task-modern-observer-list')}
                    {!isReadOnly ? (
                      <SystemSelect
                        aria-label="Добавить наблюдателя"
                        value=""
                        onChange={(event) => {
                          const selected = assignees.find((assignee) => assignee.id === Number(event.target.value));
                          if (selected) addParticipant('observer', selected);
                        }}
                        className="w-full rounded-lg border border-sky-200 bg-white px-2 py-2 text-sm outline-none focus:border-sky-400"
                      >
                        <option value="">Добавить наблюдателя</option>
                        {availableObservers.map((assignee) => (
                          <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                        ))}
                      </SystemSelect>
                    ) : null}
                  </div>
                </div>
                {isReadOnly && canDeleteTask ? (
                  <button
                    type="button"
                    onClick={() => void handleDeleteTask()}
                    className="mt-2 inline-flex h-10 w-10 items-center justify-center rounded-xl border border-rose-200 text-rose-600 transition hover:bg-rose-50"
                    aria-label="Удалить задачу"
                    title="Удалить задачу безвозвратно"
                  >
                    <Trash2 className="h-5 w-5" />
                  </button>
                ) : null}
              </section>

              {canShowApprovalActions ? (
                <section className="rounded-xl border border-emerald-200 bg-emerald-50 p-3">
                  <div className="text-sm font-semibold text-emerald-900">Согласование</div>
                  <div className="mt-1 text-xs text-emerald-700">
                    Сейчас решение ожидается от вас.
                  </div>
                  <button
                    type="button"
                    onClick={() => state?.task && currentApprovalParticipant && onReviewApproval(state.task, currentApprovalParticipant)}
                    className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-lg border border-violet-200 bg-white px-3 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-50"
                  >
                    <Scale className="h-4 w-4" />
                    Открыть окно согласования
                  </button>
                  <div className="mt-3 grid gap-2">
                    <button
                      type="button"
                      onClick={() => handleApproveParticipant(currentApprovalParticipant!)}
                      className="rounded-lg bg-emerald-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-emerald-700"
                    >
                      Согласовать
                    </button>
                    <button
                      type="button"
                      onClick={() => openRevisionReviewDialog(currentApprovalParticipant)}
                      className="rounded-lg border border-orange-200 bg-white px-3 py-2 text-sm font-semibold text-orange-700 transition hover:bg-orange-50"
                    >
                      На доработку
                    </button>
                  </div>
                </section>
              ) : null}

              <section className="rounded-xl border border-slate-200 bg-white p-3">
                <div className="mb-2 flex items-center justify-between gap-2">
                  <span className="text-sm font-semibold text-slate-700">Метки</span>
                  <Bookmark className="h-4 w-4 text-slate-400" />
                </div>
                <input
                  value={tagText}
                  disabled={isReadOnly}
                  onChange={(event) => {
                    if (isReadOnly) return;
                    setTagText(event.target.value);
                    update('tags', unique(event.target.value.split(',')));
                  }}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-2 py-2 text-sm outline-none focus:border-blue-400 focus:bg-white"
                  placeholder="метка, еще метка"
                />
                {form.tags.length ? (
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {form.tags.map((tag) => (
                      <span key={tag} className="rounded-md bg-emerald-100 px-2 py-1 text-xs font-semibold text-emerald-700">{tag}</span>
                    ))}
                  </div>
                ) : null}
              </section>

              {canRequestDeadlineChange ? (
                <section className="rounded-xl border border-amber-200 bg-amber-50 p-3">
                  <div className="mb-2 text-sm font-semibold text-amber-900">Запрос переноса срока</div>
                  <SystemDateInput type="datetime-local" aria-label="Новый срок для запроса переноса" value={deadlineRequestDueAt} onChange={(event) => setDeadlineRequestDueAt(event.target.value)} className="w-full rounded-lg border border-amber-200 bg-white px-2 py-2 text-sm" />
                  <button type="button" onClick={openDeadlineRequestReasonDialog} disabled={deadlineRequestSaving || !deadlineRequestDueAt} className="mt-2 w-full rounded-lg bg-amber-500 px-3 py-2 text-sm font-semibold text-white transition hover:bg-amber-600 disabled:opacity-60">
                    {deadlineRequestSaving ? 'Отправляем...' : 'Запросить перенос'}
                  </button>
                </section>
              ) : null}

              <section className="rounded-xl border border-slate-200 bg-white p-3 text-sm text-slate-500">
                <div>Создано: {state?.task ? formatDateTime(state.task.createdAt) : 'после сохранения'}</div>
                <div className="mt-1">Создатель: {taskAuthorLabel}</div>
                <div className="mt-1 truncate">Тип: {typeOption.label}</div>
              </section>
            </aside>
          </div>

          <div className="flex flex-col gap-2 border-t border-slate-200 bg-slate-50 px-5 py-3 sm:flex-row sm:items-center sm:justify-between">
            <input
              value={changeComment}
              onChange={(event) => setChangeComment(event.target.value)}
              disabled={!taskId || isReadOnly}
              className="h-10 min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 text-sm outline-none placeholder:text-slate-400 focus:border-blue-400 disabled:bg-slate-100"
              placeholder="Комментарий к изменению"
            />
            <div className="flex shrink-0 justify-end gap-2">
              <button type="button" onClick={onClose} className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50">
                {isPage ? 'К журналу задач' : 'Отмена'}
              </button>
              {isLockedViewWithEditAccess ? (
                <button type="button" onClick={onEnableEdit} className="rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white transition hover:bg-slate-800">
                  Редактировать задачу
                </button>
              ) : canCompleteReadOnlyTask || isAwaitingApproval ? (
                <>
                  {canSendToAuthorApproval ? (
                    <button type="button" onClick={() => void saveWithStatus('onApproval')} className="rounded-lg border border-violet-200 bg-violet-50 px-4 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-100">
                      {sendToAuthorApprovalLabel}
                    </button>
                  ) : null}
                  <button type="button" onClick={() => void handleCompletionAction()} disabled={approvalCompletionDisabled} title={completionButtonLabel} className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:bg-slate-200 disabled:text-slate-500">
                    {completionButtonLabel}
                  </button>
                </>
              ) : !isReadOnly ? (
                <button type="button" onClick={() => void handleSaveForm()} className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700">
                  {state?.task ? 'Сохранить' : 'Создать задачу'}
                </button>
              ) : null}
            </div>
          </div>
        </div>
        {approvalChoiceDialog}
        {revisionReviewDialog}
        {deadlineRequestReasonDialog}
        {approvalResultPromptDialog}
      </div>
    );
  }

  return (
    <div className={shellClassName} data-testid={isModernChrome ? 'task-modern-card-window' : 'task-legacy-card-window'}>
      <div className={panelClassName}>
        <div className={headerClassName}>
          <div className="min-w-0">
            {isPage ? (
              <button
                type="button"
                onClick={onClose}
                className="mb-4 inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
              >
                <ArrowLeft className="h-4 w-4" />
                Журнал задач
              </button>
            ) : null}
            <h2 className="text-2xl font-semibold text-slate-900">{state?.mode === 'view' ? 'Просмотр задачи' : state?.mode === 'edit' ? 'Редактировать задачу' : 'Новая задача'}</h2>
            <p className="mt-1 text-sm text-slate-500">Заполните контрагента, исполнителя, срок и правила напоминаний.</p>
          </div>
          <div className="flex shrink-0 flex-wrap items-start justify-end gap-2">
            {state?.task ? (
              <>
                {isLockedViewWithEditAccess ? (
                  <button
                    type="button"
                    onClick={onEnableEdit}
                    className="inline-flex items-center justify-center rounded-xl bg-slate-900 px-4 py-2 text-sm font-semibold text-white transition hover:bg-slate-800"
                  >
                    Редактировать задачу
                  </button>
                ) : (
                  <>
                    <button
                      type="button"
                      onClick={() => void saveWithStatus('inProgress')}
                      className="inline-flex items-center justify-center rounded-xl border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-100"
                    >
                      В работу
                    </button>
                    {!isReadOnly || canCompleteReadOnlyTask || isAwaitingApproval ? (
                      <>
                        {canSendToAuthorApproval ? (
                          <button
                            type="button"
                            onClick={() => void saveWithStatus('onApproval')}
                            className="inline-flex items-center justify-center rounded-xl border border-violet-200 bg-violet-50 px-4 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-100"
                          >
                            {sendToAuthorApprovalLabel}
                          </button>
                        ) : null}
                        <button
                          type="button"
                          onClick={() => void handleCompletionAction()}
                          disabled={approvalCompletionDisabled}
                          title={completionButtonLabel}
                          className="inline-flex items-center justify-center rounded-xl bg-emerald-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:bg-slate-200 disabled:text-slate-500"
                        >
                          {completionButtonLabel}
                        </button>
                      </>
                    ) : null}
                  </>
                )}
                {!isReadOnly ? (
                  <div className="relative">
                    <button
                      type="button"
                      onClick={() => setActionMenuOpen((open) => !open)}
                      className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-200 text-slate-600 transition hover:bg-slate-50 hover:text-slate-900"
                      aria-label="Действия с задачей"
                      title="Действия с задачей"
                    >
                      <MoreHorizontal className="h-5 w-5" />
                    </button>
                    {actionMenuOpen ? (
                      <div className="absolute right-0 z-20 mt-2 w-72 overflow-hidden rounded-xl border border-slate-200 bg-white py-2 text-sm shadow-xl">
                        <button type="button" onClick={() => openQuickPanel('observer')} className="block w-full px-4 py-2 text-left text-slate-700 transition hover:bg-slate-50">
                          Добавить подписчика-наблюдателя
                        </button>
                        <button type="button" onClick={() => openQuickPanel('assignee')} className="block w-full px-4 py-2 text-left text-slate-700 transition hover:bg-slate-50">
                          Сменить исполнителя
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setActionMenuOpen(false);
                            setQuickPanel(null);
                            titleInputRef.current?.focus();
                          }}
                          className="block w-full px-4 py-2 text-left text-slate-700 transition hover:bg-slate-50"
                        >
                          Редактировать
                        </button>
                        <button type="button" onClick={() => void saveWithStatus('waiting')} className="block w-full px-4 py-2 text-left text-slate-700 transition hover:bg-slate-50">
                          Поставить на паузу
                        </button>
                        <button type="button" onClick={() => void saveWithStatus('inProgress')} className="block w-full px-4 py-2 text-left text-slate-700 transition hover:bg-slate-50">
                          Вернуть в работу
                        </button>
                        {canDuplicateTask ? (
                          <button
                            type="button"
                            onClick={() => void handleDuplicateTask()}
                            className="flex w-full items-start gap-3 px-4 py-2.5 text-left text-slate-700 transition hover:bg-slate-50"
                            title="Дублировать задачу — создать отдельную копию"
                            aria-label="Дублировать задачу"
                          >
                            <Copy className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" data-testid="task-duplicate-icon" />
                            <span>
                              <span className="block font-semibold">Дублировать задачу</span>
                              <span className="mt-0.5 block text-xs font-normal text-slate-400">Создать отдельную копию с теми же данными</span>
                            </span>
                          </button>
                        ) : null}
                        {canCancelTask ? (
                          <button
                            type="button"
                            onClick={() => void handleCancelTask()}
                            className="flex w-full items-start gap-3 px-4 py-2.5 text-left text-slate-700 transition hover:bg-rose-50"
                            title="Отменить задачу — оставить в журнале со статусом «Отменена»"
                            aria-label="Отменить задачу"
                          >
                            <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-rose-600" data-testid="task-cancel-icon" />
                            <span>
                              <span className="block font-semibold">Отменить задачу</span>
                              <span className="mt-0.5 block text-xs font-normal text-slate-400">Оставить в журнале со статусом «Отменена»</span>
                            </span>
                          </button>
                        ) : null}
                        {canDeleteTask ? (
                          <button
                            type="button"
                            onClick={() => void handleDeleteTask()}
                            className="flex w-full items-start gap-3 px-4 py-2.5 text-left text-rose-600 transition hover:bg-rose-50"
                            title="Удалить задачу безвозвратно"
                            aria-label="Удалить задачу"
                          >
                            <Trash2 className="mt-0.5 h-4 w-4 shrink-0" />
                            <span>
                              <span className="block font-semibold">Удалить задачу</span>
                              <span className="mt-0.5 block text-xs font-normal text-rose-400">Безвозвратно удалить запись из журнала</span>
                            </span>
                          </button>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                ) : null}
              </>
            ) : null}
            {!isPage ? (
              <button type="button" onClick={onClose} className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700" aria-label="Закрыть">
                <X className="h-5 w-5" />
              </button>
            ) : null}
          </div>
        </div>

        {state?.task ? (
          <div className="px-6 pt-4"><TaskBillingBadge task={state.task} /></div>
        ) : form.clientBillingClassification ? (
          <div className={`mx-6 mt-4 rounded-xl border p-3 text-sm font-semibold ${form.clientBillingClassification === 'billable' ? 'border-rose-200 bg-rose-50 text-rose-800' : 'border-slate-200 bg-slate-50 text-slate-700'}`}>
            {form.clientBillingClassification === 'billable'
              ? form.billingMode === 'Fixed' ? 'Клиентская задача в рамках договора с фиксированной оплатой.' : 'Платная задача: после выполнения она останется на контроле до включения в счёт.'
              : form.clientId || form.legalEntityId
                ? 'Неоплачиваемая задача: привязана к контрагенту, но не будет учитываться при выставлении счёта.'
                : 'Внутренняя задача: не будет учитываться при выставлении счёта клиенту.'}
          </div>
        ) : null}

        <div className={bodyClassName}>
          {quickPanel === 'assignee' ? (
            <section className="mb-5 rounded-2xl border border-blue-100 bg-blue-50/70 p-4">
              <Field label="Новый исполнитель">
                <SystemSelect
                  aria-label="Новый исполнитель"
                  value={form.assigneeUserId ?? ''}
                  onChange={(event) => setPrimaryAssignee(Number(event.target.value) || null)}
                  className="form-input bg-white"
                >
                  <option value="">Без привязки к пользователю</option>
                  {assignees.map((assignee) => (
                    <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                  ))}
                </SystemSelect>
              </Field>
            </section>
          ) : null}

          {quickPanel === 'observer' ? (
            <section className="mb-5 rounded-2xl border border-blue-100 bg-blue-50/70 p-4">
              <Field label="Наблюдатель">
                <SystemSelect
                  aria-label="Наблюдатель"
                  value=""
                  onChange={(event) => {
                    const selected = assignees.find((assignee) => assignee.id === Number(event.target.value));
                    if (selected) {
                      addParticipant('observer', selected);
                    }
                  }}
                  className="form-input bg-white"
                >
                  <option value="">Выберите наблюдателя</option>
                  {availableObservers.map((assignee) => (
                    <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                  ))}
                </SystemSelect>
              </Field>
            </section>
          ) : null}

          <label className="mb-5 flex items-start gap-3 rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-3 text-sm text-slate-700">
            <SystemCheckbox
              type="checkbox"
              checked={Boolean(form.isInternal)}
              disabled={isReadOnly}
              onChange={(event) => handleInternalChange(event.target.checked)}
              data-testid="task-internal-checkbox"
              className="mt-0.5 h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
            />
            <span>
              <span className="font-semibold text-slate-900">Общая задача без контрагента</span>
              <span className="mt-1 block text-xs leading-5 text-slate-500">
                Включите только для общей задачи сотрудников. Неоплачиваемая задача клиента сохраняет выбранного контрагента и дело.
              </span>
            </span>
          </label>

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
                disabled={isReadOnly || form.isInternal || isCreationBillingBindingLocked}
              />
            </Field>
            <Field label="Исполнители">
              {renderAssigneeControl()}
            </Field>
            <Field label="Постановщик">
              {state?.task && isAdminUser ? (
                <SystemSelect
                  value={form.authorUserId ?? ''}
                  onChange={(event) => update('authorUserId', Number(event.target.value) || null)}
                  disabled={isReadOnly}
                  data-testid="task-author-select"
                  className="form-input disabled:bg-slate-50 disabled:text-slate-600"
                >
                  {form.authorUserId && !selectedTaskAuthor ? (
                    <option value={form.authorUserId}>{taskAuthorLabel}</option>
                  ) : null}
                  {assignees.map((assignee) => (
                    <option key={assignee.id} value={assignee.id}>{assignee.name}</option>
                  ))}
                </SystemSelect>
              ) : (
                <input
                  value={taskAuthorLabel}
                  disabled
                  data-testid="task-author-display"
                  className="form-input bg-slate-50 text-slate-600"
                />
              )}
            </Field>
            <Field label="Имя основного исполнителя (если нет пользователя)">
              <input
                value={form.assigneeName}
                onChange={(event) => update('assigneeName', event.target.value)}
                disabled={isReadOnly || Boolean(form.assigneeUserId)}
                className="form-input disabled:bg-slate-50 disabled:text-slate-500"
              />
            </Field>
            <Field label="Наименование задачи">
              <input ref={titleInputRef} value={form.title} onChange={(event) => update('title', event.target.value)} disabled={isReadOnly} data-testid="task-title-input" className="form-input" placeholder="Что нужно сделать" />
            </Field>
            <Field label="Дело">
              <SystemSelect
                aria-label="Дело"
                value={form.clientCaseId ?? ''}
                onChange={(event) => handleCaseChange(event.target.value)}
                disabled={isReadOnly || form.isInternal || isCreationBillingBindingLocked || (!form.clientId && !form.legalEntityId) || casesLoading}
                className="form-input disabled:bg-slate-50 disabled:text-slate-400"
              >
                <option value="">Без дела</option>
                {selectableClientCases.map((clientCase) => (
                  <option key={clientCase.id} value={clientCase.id}>{clientCase.title}</option>
                ))}
              </SystemSelect>
            </Field>
            <Field label="Дата и время исполнения">
              <div className="flex gap-2">
                <SystemDateInput
                  type="datetime-local"
                  value={form.dueAt}
                  onChange={(event) => update('dueAt', event.target.value)}
                  disabled={isReadOnly}
                  data-testid="task-due-at-input"
                  className="form-input min-w-0 flex-1"
                />
                <button
                  type="button"
                  onClick={() => setScheduleOpen((value) => !value)}
                  data-testid="task-day-schedule-toggle"
                  aria-expanded={scheduleOpen}
                  title="Расписание на выбранный день"
                  className={`inline-flex h-11 shrink-0 items-center justify-center gap-2 rounded-xl border px-3 text-sm font-semibold transition ${
                    scheduleOpen
                      ? 'border-blue-300 bg-blue-50 text-blue-700'
                      : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50 hover:text-slate-900'
                  }`}
                >
                  <CalendarClock className="h-4 w-4" />
                  <span className="hidden sm:inline">Расписание</span>
                </button>
              </div>
            </Field>
            <Field label="Срок согласования">
              <SystemDateInput
                type="datetime-local"
                value={form.approvalDueAt ?? ''}
                onChange={(event) => update('approvalDueAt', event.target.value || null)}
                disabled={isReadOnly}
                data-testid="task-approval-due-at-input"
                className="form-input"
              />
            </Field>
            {scheduleOpen ? (
              <div className="lg:col-span-2">
                <TaskDaySchedulePanel
                  tasks={tasks}
                  selectedDueAt={form.dueAt}
                  plannedDurationMinutes={form.plannedDurationMinutes}
                  assigneeUserId={form.assigneeUserId}
                  assigneeName={form.assigneeName}
                  currentTaskId={taskId}
                />
              </div>
            ) : null}
            <Field label="Длительность">
              <SystemSelect
                value={normalizeTaskDuration(form.plannedDurationMinutes)}
                onChange={(event) => update('plannedDurationMinutes', Number(event.target.value))}
                disabled={isReadOnly}
                className="form-input"
              >
                {[15, 30, 45, 60, 90, 120, 180, 240, 360, 480].map((minutes) => (
                  <option key={minutes} value={minutes}>{formatDuration(minutes)}</option>
                ))}
              </SystemSelect>
            </Field>
            <Field label="Тип задачи">
              <SystemSelect value={form.type} onChange={(event) => update('type', event.target.value as TaskJournalType)} disabled={isReadOnly} className="form-input">
                {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </Field>
            <Field label="Приоритет">
              <SystemSelect value={form.priority} onChange={(event) => update('priority', event.target.value as TaskJournalPriority)} disabled={isReadOnly} className="form-input">
                {PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </Field>
            <Field label="Статус">
              <SystemSelect data-testid="task-editor-status-select" value={form.status} onChange={(event) => update('status', event.target.value as TaskJournalStatus)} disabled={isReadOnly} className="form-input">
                {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </SystemSelect>
            </Field>
          </div>

          <section className="mt-5 rounded-2xl border border-emerald-100 bg-emerald-50/60 p-4">
            <label className="flex items-start gap-3 text-sm text-slate-700">
              <SystemCheckbox
                type="checkbox"
                checked={Boolean(form.contractorPaymentEnabled)}
                disabled={isReadOnly || !canUseContractorPayment}
                onChange={(event) => update('contractorPaymentEnabled', event.target.checked)}
                data-testid="task-contractor-payment-checkbox"
                className="mt-1 h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500 disabled:opacity-50"
              />
              <span>
                <span className="font-semibold text-slate-900">Оплата исполнителю</span>
                <span className="mt-1 block text-xs leading-5 text-slate-500">
                  {canUseContractorPayment
                    ? 'После сохранения задачи будет подготовлен ожидающий расходный платеж.'
                    : 'Доступно, когда у выбранного исполнителя в профиле включен признак привлеченного исполнителя.'}
                </span>
              </span>
            </label>
            {form.contractorPaymentEnabled ? (
              <div className="mt-4 grid gap-4 md:grid-cols-3">
                <Field label="Сумма">
                  <input
                    type="number"
                    min="0"
                    step="0.01"
                    value={form.contractorPaymentAmount ?? ''}
                    onChange={(event) => update('contractorPaymentAmount', event.target.value ? Number(event.target.value) : null)}
                    disabled={isReadOnly}
                    data-testid="task-contractor-payment-amount"
                    className="form-input bg-white"
                  />
                </Field>
                <Field label="Точная дата оплаты">
                  <SystemDateInput
                    type="date"
                    value={form.contractorPaymentDueDate ? String(form.contractorPaymentDueDate).split('T')[0] : ''}
                    onChange={(event) => update('contractorPaymentDueDate', event.target.value || null)}
                    disabled={isReadOnly}
                    data-testid="task-contractor-payment-date"
                    className="form-input bg-white"
                  />
                </Field>
                <div className="rounded-xl border border-emerald-100 bg-white px-3 py-3">
                  <label className="flex items-center gap-3 text-sm font-medium text-slate-700">
                    <SystemCheckbox
                      type="checkbox"
                      checked={Boolean(form.contractorPaymentOnResult)}
                      onChange={(event) => {
                        const checked = event.target.checked;
                        setForm((prev) => ({
                          ...prev,
                          contractorPaymentOnResult: checked,
                          contractorPaymentResultDelayDays: checked
                            ? prev.contractorPaymentResultDelayDays ?? 5
                            : null,
                        }));
                      }}
                      disabled={isReadOnly}
                      data-testid="task-contractor-payment-on-result"
                      className="h-4 w-4 rounded border-slate-300 text-emerald-600"
                    />
                    Оплата по результату
                  </label>
                  {form.contractorPaymentOnResult ? (
                    <label className="mt-3 block text-xs font-medium text-slate-600">
                      Через дней после результата
                      <input
                        type="number"
                        min="0"
                        max="365"
                        step="1"
                        value={form.contractorPaymentResultDelayDays ?? ''}
                        onChange={(event) => update('contractorPaymentResultDelayDays', event.target.value === '' ? null : Number(event.target.value))}
                        disabled={isReadOnly}
                        data-testid="task-contractor-payment-result-delay-days"
                        className="form-input mt-1 bg-white"
                      />
                    </label>
                  ) : null}
                </div>
                {form.contractorPaymentOnResult && form.contractorPaymentDueDate ? (
                  <div className="md:col-span-3 rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    Заполните только один вариант: точную дату оплаты или оплату по результату.
                  </div>
                ) : form.contractorPaymentOnResult ? (
                  <div className="md:col-span-3 rounded-xl border border-emerald-100 bg-white px-3 py-2 text-sm text-slate-600">
                    Если задача уже закрыта, платеж будет поставлен от даты закрытия. Если задача еще не закрыта, платеж появится только после принятия работы и завершения задачи.
                  </div>
                ) : null}
                {form.contractorPaymentId ? (
                  <div className="md:col-span-3 rounded-xl border border-emerald-100 bg-white px-3 py-2 text-sm text-slate-600">
                    Платеж #{form.contractorPaymentId}
                    {form.contractorPaymentStatus ? ` · ${form.contractorPaymentStatus}` : ''}
                    {form.contractorPaymentAmount ? ` · ${formatCurrencySmart(Number(form.contractorPaymentAmount)).full}` : ''}
                  </div>
                ) : null}
              </div>
            ) : null}
          </section>

          <section className="mt-5 rounded-2xl border border-slate-200 bg-slate-50/70 p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Наблюдатели и согласование</h3>
                <p className="mt-1 text-sm text-slate-500">Выберите наблюдателей и согласующих из пользователей системы.</p>
              </div>
              <span className="rounded-full bg-white px-3 py-1 text-xs font-semibold text-slate-500 ring-1 ring-slate-200">
                {additionalParticipantsCount} доп. участников
              </span>
            </div>
            <div className="mt-4 grid gap-4 md:grid-cols-2">
              {(['approver', 'observer'] as TaskJournalParticipantRole[]).map((role) => {
                const visibleParticipants = getVisibleParticipantsByRole(role);
                const availableAssignees = getAvailableAssigneesForRole(role);

                return (
                  <Field key={role} label={PARTICIPANT_ROLE_LABELS[role]}>
                    <div
                      className="space-y-3 rounded-xl border border-slate-200 bg-white p-3"
                      data-testid={`task-legacy-${role}-group`}
                    >
                      <p className="text-xs leading-4 text-slate-500">
                        {role === 'approver'
                          ? 'Принимают решение по задаче по очереди.'
                          : 'Следят за задачей, но не принимают решение.'}
                      </p>
                      <div className="min-h-24 space-y-2">
                        {visibleParticipants.length === 0 ? (
                          <div className="rounded-lg border border-dashed border-slate-200 px-3 py-5 text-center text-sm text-slate-400">
                            Пока никто не добавлен
                          </div>
                        ) : visibleParticipants.map((participant, index) => {
                          const participantName = getParticipantDisplayName(participant);
                          return (
                            <div
                              key={`${role}-${participant.id ?? participant.userId ?? participantName}-${index}`}
                              className="flex items-start justify-between gap-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-sm"
                            >
                              <span className="min-w-0">
                                <span className="block truncate font-semibold text-slate-700">{participantName}</span>
                                {participant.email ? <span className="block truncate text-xs text-slate-400">{participant.email}</span> : null}
                              </span>
                              {!isReadOnly ? (
                                <button
                                  type="button"
                                  onClick={() => removeParticipant(role, participant)}
                                  className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-slate-400 transition hover:bg-white hover:text-rose-600"
                                  aria-label={`Удалить участника ${participantName}`}
                                  title="Удалить"
                                >
                                  <X className="h-4 w-4" />
                                </button>
                              ) : null}
                            </div>
                          );
                        })}
                      </div>
                      {!isReadOnly ? (
                        <SystemSelect
                          aria-label={`Добавить: ${PARTICIPANT_ROLE_LABELS[role]}`}
                          value=""
                          onChange={(event) => {
                            const selected = assignees.find((assignee) => assignee.id === Number(event.target.value));
                            if (selected) {
                              addParticipant(role, selected);
                            }
                          }}
                          className="form-input bg-white"
                          disabled={availableAssignees.length === 0}
                        >
                          <option value="">
                            {availableAssignees.length === 0 ? 'Нет доступных пользователей' : '+ Добавить из справочника'}
                          </option>
                          {availableAssignees.map((assignee) => (
                            <option key={assignee.id} value={assignee.id}>
                              {assignee.email ? `${assignee.name} - ${assignee.email}` : assignee.name}
                            </option>
                          ))}
                        </SystemSelect>
                      ) : null}
                    </div>
                  </Field>
                );
              })}
            </div>

            {state?.task && approverParticipants.length > 0 ? (
              <div className="mt-4 rounded-xl border border-violet-100 bg-white p-3">
                <div className="text-sm font-semibold text-slate-800">Очередь согласования</div>
                {form.approvalDueAt ? (
                  <div className="mt-1 text-xs text-slate-500">Срок согласования: {formatDateTime(fromLocalInputValue(form.approvalDueAt))}</div>
                ) : null}
                <div className="mt-3 space-y-2">
                  {orderedApproverParticipants
                    .map((participant, index) => {
                      const isCurrentApprover = participant === currentApprovalParticipant && participant.userId === currentUserId;
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
                            <div className="flex shrink-0 flex-wrap justify-end gap-2">
                              <button
                                type="button"
                                onClick={() => state?.task && onReviewApproval(state.task, participant)}
                                className="rounded-lg border border-violet-200 bg-violet-50 px-3 py-1.5 text-xs font-semibold text-violet-700 transition hover:bg-violet-100"
                              >
                                Открыть окно решения
                              </button>
                              <button
                                type="button"
                                onClick={() => handleApproveParticipant(participant)}
                                className="rounded-lg border border-emerald-200 px-3 py-1.5 text-xs font-semibold text-emerald-700 transition hover:bg-emerald-50"
                              >
                                Согласовать
                              </button>
                              <button
                                type="button"
                                onClick={() => openRevisionReviewDialog(participant)}
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

          <div className="mt-5 grid gap-5" data-testid="task-text-fields">
            <Field label="Описание">
              <textarea value={form.description} onChange={(event) => update('description', event.target.value)} disabled={isReadOnly} data-testid="task-description-field" className="form-textarea min-h-[140px] resize-y" rows={6} placeholder="Контекст, телефоны, комментарии, откуда пришла задача" />
            </Field>
            <Field label="Результат">
              <textarea value={form.result} onChange={(event) => update('result', event.target.value)} disabled={!canEditResult} data-testid="task-result-field" className="form-textarea min-h-[120px] resize-y" rows={5} placeholder={canEditResult ? TASK_COMPLETION_RESULT_HINT : 'Результат доступен при завершении задачи'} />
              <p className="mt-1 text-xs text-slate-500">{canEditResult ? TASK_COMPLETION_RESULT_HINT : 'Поле станет доступным после выбора завершенного статуса или нажатия кнопки завершения.'}</p>
              {resultError ? <p className="mt-1 text-xs font-semibold text-rose-600">{resultError}</p> : null}
            </Field>
          </div>

          <section className="mt-5 rounded-2xl border border-slate-200 bg-white p-4">
            <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Изменения и уведомления</h3>
            {taskId && !isReadOnly ? (
              <Field label="Комментарий к изменению" className="mt-4">
                <textarea
                  value={changeComment}
                  onChange={(event) => setChangeComment(event.target.value)}
                  className="form-textarea min-h-[84px]"
                  rows={3}
                  maxLength={4000}
                  placeholder="Что изменили по задаче"
                />
              </Field>
            ) : null}

            <div className="mt-4 grid gap-5 lg:grid-cols-2">
              <Field label="Напоминания">
                <div className="grid gap-2 sm:grid-cols-2">
                  {REMINDER_OPTIONS.map((option) => (
                    <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700">
                      <SystemCheckbox type="checkbox" checked={form.reminders.includes(option.value)} disabled={isReadOnly} onChange={() => toggleArray('reminders', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
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
                        <SystemCheckbox type="checkbox" checked={form.channels.includes(option.value)} disabled={isReadOnly} onChange={() => toggleArray('channels', option.value)} className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500" />
                        <Icon className="h-4 w-4 text-slate-400" />
                        {option.label}
                      </label>
                    );
                  })}
                </div>
              </Field>
            </div>
          </section>

          <Field label="Теги" className="mt-5">
            <input
              value={tagText}
              disabled={isReadOnly}
              onChange={(event) => {
                if (isReadOnly) return;
                setTagText(event.target.value);
                update('tags', unique(event.target.value.split(',')));
              }}
              className="form-input"
              placeholder="контроль, платеж, звонок"
            />
          </Field>

          {canRequestDeadlineChange ? (
            <section className="mt-5 rounded-2xl border border-amber-200 bg-amber-50/60 p-4">
              <div className="grid gap-4 lg:grid-cols-[220px_auto] lg:items-end">
                <Field label="Новый срок">
                  <SystemDateInput
                    type="datetime-local"
                    aria-label="Новый срок для запроса переноса"
                    value={deadlineRequestDueAt}
                    onChange={(event) => setDeadlineRequestDueAt(event.target.value)}
                    className="form-input bg-white"
                  />
                </Field>
                <button
                  type="button"
                  onClick={openDeadlineRequestReasonDialog}
                  disabled={deadlineRequestSaving || !deadlineRequestDueAt}
                  className="inline-flex h-11 items-center justify-center rounded-xl border border-amber-300 bg-white px-4 text-sm font-semibold text-amber-800 transition hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {deadlineRequestSaving ? 'Отправляем...' : 'Запросить перенос'}
                </button>
              </div>
            </section>
          ) : null}

          {deadlineChangeRequestsSection ? (
            <div className="mt-5">
              {deadlineChangeRequestsSection}
            </div>
          ) : null}

          <section className="mt-5 rounded-2xl border border-slate-200 bg-white p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">
                  <FileText className="h-4 w-4 text-indigo-500" />
                  Файлы и версии
                </h3>
                <p className="mt-1 text-sm text-slate-500">Документы по задаче с версиями и историей загрузок.</p>
              </div>
              {canManageFiles ? (
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
                        {canManageFiles ? (
                        <button
                          type="button"
                          onClick={() => versionInputRefs.current[group.documentKey]?.click()}
                          disabled={uploadingDocumentKey !== null}
                          className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-60"
                        >
                          <Plus className="h-4 w-4" />
                          {uploadingDocumentKey === group.documentKey ? 'Загружаем...' : 'Новая версия'}
                        </button>
                        ) : null}
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
                              {file.clientCaseDocumentId ? ` · Документ дела #${file.clientCaseDocumentId}` : ''}
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
                            {canSendFilesToYandexDisk ? (
                              <button
                                type="button"
                                onClick={() => void handleSendFileToYandexDisk(file)}
                                disabled={sendingYandexFileId === file.id}
                                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-sky-200 text-sky-600 transition hover:bg-sky-50 disabled:cursor-not-allowed disabled:opacity-60"
                                title="Отправить на Я.Диск"
                                aria-label="Отправить файл на Я.Диск"
                              >
                                {sendingYandexFileId === file.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
                              </button>
                            ) : null}
                            {canManageFiles ? (
                            <button
                              type="button"
                              onClick={() => void handleDeleteFile(file)}
                              className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-rose-100 text-rose-600 transition hover:bg-rose-50"
                              title="Удалить"
                              aria-label="Удалить файл"
                            >
                              <Trash2 className="h-4 w-4" />
                            </button>
                            ) : null}
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
                {canPostComment ? (
                  <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50 p-3">
                    <textarea
                      value={commentText}
                      onChange={(event) => setCommentText(event.target.value)}
                      className="form-textarea min-h-[84px] bg-white"
                      rows={3}
                      placeholder="Введите комментарий"
                      maxLength={4000}
                    />
                    <label className="mt-3 inline-flex cursor-pointer items-center gap-2 text-xs font-medium text-slate-600">
                      <input
                        type="checkbox"
                        checked={commentExternalCalendarVisible}
                        onChange={(event) => setCommentExternalCalendarVisible(event.target.checked)}
                        className="form-checkbox"
                      />
                      Доступен во внешнем календаре
                    </label>
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
                ) : null}

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
                              {comment.isExternalCalendarVisible ? (
                                <span className="rounded-full bg-cyan-50 px-2 py-0.5 text-[11px] font-semibold text-cyan-700 ring-1 ring-cyan-200">
                                  Внешний календарь
                                </span>
                              ) : null}
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
          <button type="button" onClick={onClose} className="rounded-xl border border-slate-300 px-5 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50">{isPage ? 'К журналу задач' : 'Отмена'}</button>
          {state?.task && canDeleteTask ? (
            <button type="button" onClick={() => void handleDeleteTask()} className="rounded-xl border border-rose-200 px-5 py-2.5 text-sm font-semibold text-rose-600 transition hover:bg-rose-50">
              Удалить задачу
            </button>
          ) : null}
          {isLockedViewWithEditAccess ? (
            <button type="button" onClick={onEnableEdit} className="rounded-xl bg-slate-900 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800">
              Редактировать задачу
            </button>
          ) : canCompleteReadOnlyTask || isAwaitingApproval ? (
            <>
              {canSendToAuthorApproval ? (
                <button type="button" onClick={() => void saveWithStatus('onApproval')} className="rounded-xl border border-violet-200 bg-violet-50 px-5 py-2.5 text-sm font-semibold text-violet-700 transition hover:bg-violet-100">
                  {sendToAuthorApprovalLabel}
                </button>
              ) : null}
              <button type="button" onClick={() => void handleCompletionAction()} disabled={approvalCompletionDisabled} title={completionButtonLabel} className="rounded-xl bg-emerald-600 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:bg-slate-200 disabled:text-slate-500">
                {completionButtonLabel}
              </button>
            </>
          ) : (
            <button type="button" onClick={() => void handleSaveForm()} className="rounded-xl bg-slate-900 px-5 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800">
              {state?.mode === 'edit' ? 'Сохранить' : 'Создать задачу'}
            </button>
          )}
        </div>
      </div>
      {approvalChoiceDialog}
      {revisionReviewDialog}
      {deadlineRequestReasonDialog}
      {approvalResultPromptDialog}
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
        .form-input:disabled,
        .form-textarea:disabled {
          cursor: not-allowed;
          background: rgb(248 250 252);
          color: rgb(100 116 139);
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

function ModernTaskQuickField({
  icon,
  label,
  children,
  className = '',
  embedded = false,
}: {
  icon: ReactNode;
  label: string;
  children: ReactNode;
  className?: string;
  embedded?: boolean;
}) {
  return (
    <div className={`${embedded ? 'min-w-0' : 'rounded-xl bg-slate-50 p-2'} ${className}`}>
      <div className={`mb-1.5 flex items-center font-semibold text-slate-700 ${embedded ? 'justify-center gap-1.5 text-center text-xs' : 'gap-2 text-sm'}`}>
        <span className={`inline-flex shrink-0 items-center justify-center rounded-lg bg-white text-slate-500 ring-1 ring-slate-200 ${embedded ? 'h-6 w-6' : 'h-7 w-7'}`}>
          {icon}
        </span>
        {label}
      </div>
      {children}
    </div>
  );
}

function ModernParticipantRow({
  label,
  count,
  description,
  tone,
}: {
  label: string;
  count: number;
  description: string;
  tone: 'violet' | 'sky';
}) {
  const toneClass = tone === 'violet'
    ? 'bg-violet-100 text-violet-700 ring-violet-200'
    : 'bg-sky-100 text-sky-700 ring-sky-200';

  return (
    <div className="flex items-start justify-between gap-2 rounded-lg bg-white/80 px-2.5 py-2 text-sm">
      <span className="min-w-0">
        <span className="block font-semibold text-slate-800">{label}</span>
        <span className="mt-0.5 block text-[11px] leading-4 text-slate-500">{description}</span>
      </span>
      <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ring-1 ${toneClass}`}>{count}</span>
    </div>
  );
}
