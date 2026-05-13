export type TaskJournalStatus =
  | 'new'
  | 'inProgress'
  | 'waiting'
  | 'onApproval'
  | 'revision'
  | 'overdueApproval'
  | 'done'
  | 'cancelled';
export type TaskJournalPriority = 'low' | 'normal' | 'high' | 'urgent';
export type TaskJournalType = 'task' | 'budget' | 'contact' | 'document' | 'reminder' | 'control';
export type TaskJournalReminder = 'atDue' | 'before15m' | 'before1h' | 'before1d' | 'dailyOverdue';
export type TaskJournalChannel = 'inApp' | 'email' | 'telegram';
export type TaskJournalParticipantRole = 'assignee' | 'observer' | 'approver';
export type TaskJournalApprovalStatus = 'pending' | 'approved' | 'revisionRequested';
export type TaskJournalCommentKind = 'comment' | 'system';

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

export interface TaskJournalItem {
  id: number;
  clientId?: number | null;
  clientName?: string;
  legalEntityId?: number | null;
  legalEntityName?: string;
  clientCaseId?: number | null;
  clientCaseTitle?: string;
  objectName: string;
  title: string;
  description: string;
  assigneeUserId?: number | null;
  assigneeName: string;
  authorUserId?: number | null;
  authorName: string;
  dueAt: string;
  plannedDurationMinutes: number;
  type: TaskJournalType;
  priority: TaskJournalPriority;
  status: TaskJournalStatus;
  result: string;
  reminders: TaskJournalReminder[];
  channels: TaskJournalChannel[];
  tags: string[];
  participants?: TaskJournalParticipant[];
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
}

export interface TaskJournalAssignee {
  id: number;
  name: string;
  email: string;
}

export interface TaskJournalCaseAutomationCandidate {
  clientId?: number | null;
  clientName: string;
  legalEntityId?: number | null;
  legalEntityName?: string;
  clientCaseId: number;
  clientCaseTitle: string;
  reason: string;
  activeTaskCount: number;
  completedTaskCount: number;
  daysWithoutActiveTask: number;
  lastTaskAt?: string | null;
  suggestedTitle: string;
  suggestedDescription: string;
  suggestedDueAt: string;
}

export interface TaskJournalAutomationCreateResult {
  createdTasks: TaskJournalItem[];
  skippedCount: number;
}

export type TaskJournalUpsertPayload = Omit<TaskJournalItem, 'id' | 'createdAt' | 'updatedAt' | 'authorUserId' | 'authorName'> & {
  changeComment?: string | null;
};

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
  return status !== 'done' && status !== 'cancelled';
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
