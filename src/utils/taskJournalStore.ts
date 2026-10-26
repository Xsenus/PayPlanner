export type TaskJournalStatus =
  | 'new'
  | 'inProgress'
  | 'waiting'
  | 'onApproval'
  | 'approved'
  | 'revision'
  | 'overdueApproval'
  | 'done'
  | 'cancelled';
export type TaskJournalPriority = 'low' | 'normal' | 'high' | 'urgent';
export type TaskJournalType =
  | 'task'
  | 'budget'
  | 'contact'
  | 'document'
  | 'reminder'
  | 'control'
  | 'courtHearing'
  | 'meeting';
export type TaskJournalReminder = 'atDue' | 'before15m' | 'before1h' | 'before1d' | 'before3d' | 'before7d' | 'dailyOverdue';
export type TaskJournalChannel = 'inApp' | 'email' | 'telegram';
export type TaskJournalParticipantRole = 'assignee' | 'observer' | 'approver';
export type TaskJournalApprovalStatus = 'pending' | 'approved' | 'revisionRequested';
export type TaskJournalCommentKind = 'comment' | 'system';
export type TaskClientBillingClassification = 'legacy' | 'billable' | 'internal';
export type TaskClientBillingStatus = 'legacy' | 'internal' | 'fixed' | 'unbilled' | 'invoiced';
export type TaskRecurrenceFrequency = 'daily' | 'weekly' | 'monthly' | 'yearly';

export interface TaskJournalParticipant {
  id?: number;
  userId?: number | null;
  role: TaskJournalParticipantRole;
  displayName: string;
  name?: string;
  email?: string;
  sortOrder?: number;
  approvalStatus?: TaskJournalApprovalStatus;
  resolution?: string;
  respondedAt?: string | null;
}

export type TaskJournalTemplateBindingType = 'none' | 'caseStage' | 'caseType';

export interface TaskJournalTemplateParticipant {
  id?: number;
  userId?: number | null;
  role: TaskJournalParticipantRole;
  displayName: string;
  email?: string;
  sortOrder?: number;
}

export interface TaskJournalTemplateParticipantUpsertPayload {
  userId?: number | null;
  name?: string | null;
  role: TaskJournalParticipantRole;
  sortOrder?: number | null;
}

export interface TaskJournalTemplate {
  id: number;
  name: string;
  description?: string | null;
  title: string;
  taskDescription?: string | null;
  type: TaskJournalType;
  priority: TaskJournalPriority;
  plannedDurationMinutes: number;
  dueOffsetMinutes: number;
  assigneeUserId?: number | null;
  assigneeName: string;
  bindingType: TaskJournalTemplateBindingType;
  bindingKey: string;
  isActive: boolean;
  reminders: TaskJournalReminder[];
  channels: TaskJournalChannel[];
  tags: string[];
  participants: TaskJournalTemplateParticipant[];
  createdByUserId?: number | null;
  createdByName: string;
  createdAt: string;
  updatedAt?: string | null;
}

export type TaskJournalTemplateUpsertPayload = Omit<TaskJournalTemplate, 'id' | 'participants' | 'createdByUserId' | 'createdByName' | 'createdAt' | 'updatedAt'> & {
  participants?: TaskJournalTemplateParticipantUpsertPayload[];
};

export interface TaskBillingInvoiceOption {
  id: number;
  number: string;
  date: string;
  amount: number;
  workflowStatus: string;
}

export interface TaskJournalItem {
  id: number;
  clientId?: number | null;
  clientName?: string;
  legalEntityId?: number | null;
  legalEntityName?: string;
  isInternal?: boolean;
  isUntriaged?: boolean;
  salesRequestId?: number | null;
  clientCaseId?: number | null;
  clientCaseTitle?: string;
  clientBillingClassification?: TaskClientBillingClassification;
  billingContractId?: number | null;
  billingContractNumber?: string | null;
  billingMode?: 'Fixed' | 'Actual' | '' | null;
  billingInvoiceId?: number | null;
  billingInvoiceNumber?: string | null;
  billingStatus?: TaskClientBillingStatus;
  objectName: string;
  title: string;
  description: string;
  assigneeUserId?: number | null;
  assigneeName: string;
  authorUserId?: number | null;
  authorName: string;
  dueAt: string;
  approvalDueAt?: string | null;
  plannedDurationMinutes: number;
  contractorPaymentEnabled?: boolean;
  contractorPaymentAmount?: number | null;
  contractorPaymentDueDate?: string | null;
  contractorPaymentOnResult?: boolean;
  contractorPaymentResultDelayDays?: number | null;
  contractorPaymentId?: number | null;
  contractorPaymentStatus?: string | null;
  type: TaskJournalType;
  priority: TaskJournalPriority;
  status: TaskJournalStatus;
  isUnderControl?: boolean;
  controlMarkedAt?: string | null;
  controlMarkedByUserId?: number | null;
  controlMarkedByUserName?: string;
  result: string;
  reminders: TaskJournalReminder[];
  channels: TaskJournalChannel[];
  tags: string[];
  recurrenceEnabled?: boolean;
  recurrenceFrequency?: TaskRecurrenceFrequency | null;
  recurrenceInterval?: number;
  recurrenceDayOfWeek?: number | null;
  recurrenceDayOfMonth?: number | null;
  recurrenceMonth?: number | null;
  recurrenceTimeMinutes?: number | null;
  recurrenceSkipWeekends?: boolean;
  recurrenceTimeZoneId?: string | null;
  recurrenceNextAt?: string | null;
  recurrencePreviousTaskId?: number | null;
  recurrenceSequence?: number;
  participants?: TaskJournalParticipant[];
  activeDeadlineChangeRequests?: TaskJournalDeadlineChangeRequest[];
  createdAt: string;
  updatedAt: string;
}

export interface TaskJournalNotification {
  id: string;
  taskId: number;
  title: string;
  description: string;
  tone: 'rose' | 'amber' | 'blue';
  dueAt: string;
}

export interface TaskJournalComment {
  id: number;
  taskJournalItemId: number;
  authorUserId?: number | null;
  authorName: string;
  kind: TaskJournalCommentKind;
  text: string;
  isExternalCalendarVisible: boolean;
  externalSource?: string | null;
  externalAuthorTrust?: string | null;
  metadataJson?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

export type TaskJournalDeadlineChangeRequestStatus = 'pending' | 'approved' | 'rejected' | 'cancelled' | 'superseded';

export interface TaskJournalDeadlineChangeRequest {
  id: number;
  taskJournalItemId: number;
  requestedByUserId?: number | null;
  requestedByName: string;
  reviewerUserId?: number | null;
  reviewerName: string;
  currentDueAt: string;
  requestedDueAt: string;
  reason: string;
  status: TaskJournalDeadlineChangeRequestStatus | string;
  decisionDueAt?: string | null;
  decisionComment?: string | null;
  decidedByUserId?: number | null;
  decidedByName?: string | null;
  decidedAt?: string | null;
  createdCommentId?: number | null;
  decisionCommentId?: number | null;
  createdAt: string;
  updatedAt?: string | null;
}

export interface TaskJournalFile {
  id: number;
  taskJournalItemId: number;
  documentKey: string;
  version: number;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedByUserId?: number | null;
  uploadedByName: string;
  uploadedAt: string;
  isLatestVersion: boolean;
  clientCaseDocumentId?: number | null;
}

export interface TaskJournalAssignee {
  id: number;
  name: string;
  email: string;
  isExternalContractor?: boolean;
  departmentId?: number | null;
  department?: string | null;
  timeZoneId?: string | null;
}

export interface TaskJournalCaseAutomationCandidate {
  clientId?: number | null;
  clientName: string;
  legalEntityId?: number | null;
  legalEntityName?: string;
  responsibleUserId?: number | null;
  responsibleName?: string | null;
  clientCaseId: number;
  clientCaseTitle: string;
  reason: string;
  activeTaskCount: number;
  completedTaskCount: number;
  daysWithoutActiveTask: number;
  isEscalated?: boolean;
  isReadyForControl?: boolean;
  automationState?: 'ready' | 'waiting' | 'paused';
  lastActivityAt?: string | null;
  lastActivitySource?: 'caseCreated' | 'task' | 'taskComment' | 'document' | 'courtEvent' | 'responsibleView' | 'activityConfirmation' | string;
  lastTaskAt?: string | null;
  nextControlAt?: string | null;
  daysUntilControl?: number;
  suggestedTitle: string;
  suggestedDescription: string;
  suggestedDueAt: string;
  suggestedPriority?: TaskJournalPriority;
  suggestedAssigneeUserId?: number | null;
  suggestedAssigneeName?: string | null;
  suggestedAssigneeSource?: 'responsible' | 'configuredDefault' | 'currentUser';
  suggestedAssigneeTimeZoneId?: string | null;
  responsibleVacationUntilDate?: string | null;
}

export interface TaskJournalCaseAutomationSummary {
  totalCount: number;
  readyCount: number;
  /** Подмножество readyCount, уже достигшее критического порога. */
  criticalCount?: number;
  waitingCount: number;
  pausedCount: number;
  /** Ближайший момент перехода дела в другое состояние автоконтроля. */
  nextStateChangeAt?: string | null;
  nextStateChangeKind?: 'ready' | 'critical' | 'vacationResume' | null;
}

export interface TaskJournalAutomationCreateResult {
  createdTasks: TaskJournalItem[];
  skippedCount: number;
}

export interface TaskBillingContractOption {
  id: number;
  number: string;
  title: string;
  billingMode: 'Fixed' | 'Actual';
  status: string;
  amount?: number | null;
  date: string;
}

export interface TaskBillingContext {
  clientId?: number | null;
  legalEntityId?: number | null;
  clientCaseId: number;
  clientCaseTitle: string;
  contracts: TaskBillingContractOption[];
}

export type TaskJournalUpsertPayload = Omit<TaskJournalItem,
  'id' | 'createdAt' | 'updatedAt' | 'authorUserId' | 'authorName'
  | 'recurrenceNextAt' | 'recurrencePreviousTaskId' | 'recurrenceSequence'> & {
  changeComment?: string | null;
  isInternal?: boolean;
  authorUserId?: number | null;
};

export interface TaskJournalBulkUpdatePayload {
  ids: number[];
  assigneeUserId?: number;
  priority?: TaskJournalPriority;
  status?: TaskJournalStatus;
  dueAt?: string;
  billingClassification?: 'billable' | 'internal';
  includeCompleted?: boolean;
  allowContractClassification?: boolean;
  expectedUpdatedAtById?: Record<string, string | null>;
}

export type TaskBillingBulkReason = 'notAvailable' | 'forbidden' | 'invoiced' | 'paid'
  | 'contractorPaymentProtected' | 'contractual' | 'unsupportedStatus' | 'cancelled'
  | 'completedExcluded' | 'unknownClassification' | 'clientCaseRequired' | 'versionConflict';

export interface TaskBillingBulkItemResult {
  id: number;
  outcome: 'updated' | 'unchanged' | 'skipped' | 'conflict';
  reason: TaskBillingBulkReason | null;
  updatedAt: string | null;
}

export interface TaskBillingBulkResult {
  requested: number;
  updated: number;
  unchanged: number;
  skipped: number;
  conflicted: number;
  items: TaskBillingBulkItemResult[];
}

export interface TaskJournalBulkUpdateResult {
  requested: number;
  updated: number;
  skipped: number;
  // Legacy status/assignee bulk responses do not contain billing outcomes.
  unchanged?: number;
  conflicted?: number;
  items?: TaskBillingBulkItemResult[];
}

const STORAGE_KEY = 'pp.task_journal.items.v1';
export const TASK_JOURNAL_EVENT = 'pp.task_journal.changed';

const now = () => new Date().toISOString();

function addHours(date: Date, hours: number) {
  const next = new Date(date);
  next.setHours(next.getHours() + hours);
  return next.toISOString();
}

function seedTasks(): TaskJournalItem[] {
  const base = new Date();

  return [
    {
      id: -1,
      objectName: 'ШАРОВ - ОФОРМЛЕНИЕ ЗЕМ.УЧАСТКА',
      title: 'Произвести расчет услуг и передать в работу ответственному',
      description: 'Проверить стоимость услуг, закрепить ответственного и зафиксировать результат в карточке объекта.',
      assigneeName: 'Андрей Кожеваткин',
      authorName: 'Робот PayPlanner',
      dueAt: addHours(base, -5),
      plannedDurationMinutes: 60,
      type: 'task',
      priority: 'urgent',
      status: 'inProgress',
      result: '',
      reminders: ['atDue', 'dailyOverdue'],
      channels: ['inApp', 'telegram'],
      tags: ['расчет', 'контроль'],
      createdAt: addHours(base, -30),
      updatedAt: addHours(base, -2),
    },
    {
      id: -2,
      objectName: 'ГОРБАТЮК В.Б. - БАНКРОТСТВО',
      title: 'Проверить ожидаемый платеж 151 000 ₽',
      description: 'Платеж ожидается по календарю. Если поступления нет, связаться с клиентом и поставить комментарий.',
      assigneeName: 'Финансовый отдел',
      authorName: 'Календарь платежей',
      dueAt: addHours(base, 3),
      plannedDurationMinutes: 45,
      type: 'budget',
      priority: 'high',
      status: 'new',
      result: '',
      reminders: ['before1h', 'atDue'],
      channels: ['inApp', 'email'],
      tags: ['платеж', 'бюджет'],
      createdAt: addHours(base, -12),
      updatedAt: addHours(base, -12),
    },
    {
      id: -3,
      objectName: 'Мороз Татьяна Евгеньевна',
      title: 'Перезвонить клиенту по документам',
      description: 'Уточнить готовность документов, отметить итог звонка и следующий шаг.',
      assigneeName: 'Секретарь',
      authorName: 'Робот PayPlanner',
      dueAt: addHours(base, 26),
      plannedDurationMinutes: 30,
      type: 'contact',
      priority: 'normal',
      status: 'new',
      result: '',
      reminders: ['before15m', 'atDue'],
      channels: ['inApp'],
      tags: ['звонок'],
      createdAt: addHours(base, -8),
      updatedAt: addHours(base, -8),
    },
    {
      id: -4,
      objectName: 'ЖИТАРЧУК К.В. - ВЗЫСКАНИЕ ДЕНЕЖНЫХ СРЕДСТВ',
      title: 'Поставить актуальную задачу по делу',
      description: 'Проверить текущее состояние, назначить исполнителя и срок следующего действия.',
      assigneeName: 'Руководитель практики',
      authorName: 'Контроль качества',
      dueAt: addHours(base, 76),
      plannedDurationMinutes: 60,
      type: 'control',
      priority: 'normal',
      status: 'waiting',
      result: 'Ожидаем вводные от юриста.',
      reminders: ['before1d'],
      channels: ['inApp'],
      tags: ['контроль'],
      createdAt: addHours(base, -48),
      updatedAt: addHours(base, -10),
    },
  ];
}

function emitChange() {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT));
}

export function loadTaskJournalItems(): TaskJournalItem[] {
  if (typeof window === 'undefined') {
    return seedTasks();
  }

  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      const seeded = seedTasks();
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(seeded));
      return seeded;
    }

    const parsed = JSON.parse(raw) as TaskJournalItem[];
    return Array.isArray(parsed)
      ? parsed.map((task) => ({
          ...task,
          plannedDurationMinutes: task.plannedDurationMinutes ?? 60,
        }))
      : seedTasks();
  } catch {
    return seedTasks();
  }
}

export function saveTaskJournalItems(items: TaskJournalItem[]) {
  if (typeof window === 'undefined') return;
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
  emitChange();
}

export function createTaskJournalItem(input: Omit<TaskJournalItem, 'id' | 'createdAt' | 'updatedAt'>): TaskJournalItem {
  const timestamp = now();
  return {
    ...input,
    id: -Math.floor(Date.now() + Math.random() * 1000),
    createdAt: timestamp,
    updatedAt: timestamp,
  };
}

export function isTaskJournalActive(status: TaskJournalStatus) {
  const normalized = status?.trim().toLowerCase();
  return normalized !== 'done' && normalized !== 'cancelled';
}

export function getTaskJournalNotifications(items = loadTaskJournalItems()): TaskJournalNotification[] {
  const current = Date.now();
  const soonBoundary = current + 60 * 60 * 1000;

  return items
    .filter((task) => isTaskJournalActive(task.status))
    .flatMap((task): TaskJournalNotification[] => {
      const dueMs = new Date(task.dueAt).getTime();
      if (Number.isNaN(dueMs)) return [];

      if (dueMs < current) {
        return [{
          id: `${task.id}-overdue`,
          taskId: task.id,
          title: 'Просрочена задача',
          description: `${task.objectName}: ${task.title}`,
          tone: 'rose',
          dueAt: task.dueAt,
        }];
      }

      if (dueMs <= soonBoundary) {
        return [{
          id: `${task.id}-soon`,
          taskId: task.id,
          title: 'Скоро срок',
          description: `${task.objectName}: ${task.title}`,
          tone: 'amber',
          dueAt: task.dueAt,
        }];
      }

      return [];
    })
    .sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
}
