import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TaskJournal } from './TaskJournal';
import type { TaskJournalSettingsDto } from '../../services/api';
import type { Client, ClientCase, LegalEntitySummary } from '../../types';
import { fromTimeZoneDateTimeInput, getZonedParts, toTimeZoneDateKey } from '../../utils/timeZoneDate';
import { TASK_JOURNAL_EVENT, type TaskJournalAssignee, type TaskJournalItem } from '../../utils/taskJournalStore';
import { cloneDefaultTaskStickerStyles } from '../../utils/taskStickerStyles';
import {
  FORCED_TASK_SNOOZE_STORAGE_KEY,
  getForcedTaskApprovalSnoozeKey,
  getForcedTaskDeadlineChangeSnoozeKey,
} from '../../utils/forcedTaskActionSnoozes';

const apiMock = vi.hoisted(() => ({
  getTaskJournalItems: vi.fn(),
  getTaskJournalSummary: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getTaskJournalSettings: vi.fn(),
  getCalendarSyncSettings: vi.fn(),
  getFeatureFlags: vi.fn(),
  getUserSetting: vi.fn(),
  upsertUserSetting: vi.fn(),
  updateTaskJournalSettings: vi.fn(),
  getTaskJournalCaseAutomationCandidates: vi.fn(),
  createTaskJournalCaseAutomationTask: vi.fn(),
  createTaskJournalCaseAutomationTasks: vi.fn(),
  downloadTaskJournalCalendar: vi.fn(),
  downloadCaseCalendarIcs: vi.fn(),
  downloadTaskPlanningReport: vi.fn(),
  getCaseCalendarEvents: vi.fn(),
  getCaseCalendarSummary: vi.fn(),
  getTaskJournalItem: vi.fn(),
  getTaskBillingContext: vi.fn(),
  createTaskJournalItem: vi.fn(),
  updateTaskJournalItem: vi.fn(),
  updateTaskJournalStatus: vi.fn(),
  updateTaskJournalControl: vi.fn(),
  bulkUpdateTaskJournalItems: vi.fn(),
  bulkDeleteTaskJournalItems: vi.fn(),
  updateTaskJournalApproval: vi.fn(),
  getTaskJournalComments: vi.fn(),
  createTaskJournalComment: vi.fn(),
  createTaskJournalDeadlineChangeRequest: vi.fn(),
  getTaskJournalDeadlineChangeRequests: vi.fn(),
  approveTaskJournalDeadlineChangeRequest: vi.fn(),
  rejectTaskJournalDeadlineChangeRequest: vi.fn(),
  getTaskJournalFiles: vi.fn(),
  uploadTaskJournalFile: vi.fn(),
  downloadTaskJournalFile: vi.fn(),
  uploadTaskJournalFileToYandexDisk: vi.fn(),
  deleteTaskJournalFile: vi.fn(),
  deleteTaskJournalItem: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  info: vi.fn(),
  success: vi.fn(),
}));

const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
  prompt: vi.fn(),
}));

const offlineCalendarMock = vi.hoisted(() => ({
  clearOfflineCalendarSnapshot: vi.fn(),
  getOfflineCalendarTrustedDevice: vi.fn(),
  loadOfflineCalendarSnapshot: vi.fn(),
  saveOfflineCalendarSnapshot: vi.fn(),
  setOfflineCalendarTrustedDevice: vi.fn(),
}));

const clientsState = vi.hoisted(() => ({
  clients: [] as Client[],
}));

const casesState = vi.hoisted(() => ({
  cases: [] as ClientCase[],
}));

const legalEntitiesState = vi.hoisted(() => ({
  legalEntities: [] as LegalEntitySummary[],
}));

const authState = vi.hoisted((): { user: { id: number; fullName: string; email: string; departmentId?: number | null; department?: string | null }; isAdmin: boolean } => ({
  user: { id: 7, fullName: 'Администратор', email: 'admin@example.com' },
  isAdmin: true,
}));

const permissionsState = vi.hoisted(() => ({
  taskCanCreate: true,
  taskCanEdit: true,
  taskCanDelete: true,
  taskCanEditOthers: true,
  taskCanCompleteOthers: true,
  taskCanDeleteOthers: true,
  taskCanExport: true,
  caseCanExport: true,
  clientCanView: true,
  caseCanView: true,
  legalEntityCanView: true,
  yandexDiskCanCreate: true,
  taskViewScope: 'all' as 'own' | 'department' | 'all',
  calendarViewScope: 'all' as 'own' | 'department' | 'all' | 'specific',
  calendarVisibleUserIds: [] as number[],
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../services/offlineCalendarStore', () => offlineCalendarMock);

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

vi.mock('../../hooks/useClients', () => ({
  useClients: (options?: { enabled?: boolean }) => ({
    clients: options?.enabled === false ? [] : clientsState.clients,
    loading: false,
  }),
}));

vi.mock('../../hooks/useClientCases', () => ({
  useClientCases: (clientId?: number | string, options?: { legalEntityId?: number | string }) => ({
    cases: casesState.cases.filter((clientCase) => {
      const normalizedClientId = clientId == null ? undefined : Number(clientId);
      const normalizedLegalEntityId = options?.legalEntityId == null ? undefined : Number(options.legalEntityId);
      return normalizedClientId
        ? clientCase.clientId === normalizedClientId
        : normalizedLegalEntityId
          ? clientCase.legalEntityId === normalizedLegalEntityId
          : true;
    }),
    loading: false,
  }),
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: (options?: { enabled?: boolean }) => ({
    legalEntities: options?.enabled === false ? [] : legalEntitiesState.legalEntities,
    loading: false,
  }),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: authState.user,
    isAdmin: () => authState.isAdmin,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => {
    const section = {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: true,
      viewScope: 'all',
    };

    return {
      calendar: {
        ...section,
        canViewAnalytics: true,
        viewScope: permissionsState.calendarViewScope,
        visibleUserIds: permissionsState.calendarVisibleUserIds,
      },
      tasks: {
        ...section,
        canCreate: permissionsState.taskCanCreate,
        canEdit: permissionsState.taskCanEdit,
        canDelete: permissionsState.taskCanDelete,
        canEditOthers: permissionsState.taskCanEditOthers,
        canCompleteOthers: permissionsState.taskCanCompleteOthers,
        canDeleteOthers: permissionsState.taskCanDeleteOthers,
        canExport: permissionsState.taskCanExport,
        viewScope: permissionsState.taskViewScope,
      },
      reports: { ...section },
      calculator: { ...section },
      legalEntities: { ...section, canView: permissionsState.legalEntityCanView },
      clients: { ...section, canView: permissionsState.clientCanView },
      cases: { ...section, canView: permissionsState.caseCanView, canExport: permissionsState.caseCanExport },
      accounts: { ...section },
      acts: { ...section },
      payments: { ...section },
      contracts: { ...section },
      dictionaries: { ...section },
      roles: { ...section },
      audit: { ...section },
      yandexDisk: { ...section, canCreate: permissionsState.yandexDiskCanCreate },
    };
  },
}));

function todayAt(hours: number, minutes = 0) {
  const value = new Date();
  value.setHours(hours, minutes, 0, 0);
  return value.toISOString();
}

function toDatetimeLocalInputValue(date: Date) {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function dateKey(date: Date) {
  return [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0'),
  ].join('-');
}

function dateAt(date: Date, hours = 11) {
  const value = new Date(date);
  value.setHours(hours, 0, 0, 0);
  return value.toISOString();
}

function getSameWeekDateOutsideTodayForTest(date: Date) {
  const value = new Date(date);
  const day = value.getDay() || 7;
  value.setDate(value.getDate() + (day === 1 ? 1 : -1));
  return value;
}

function createDataTransfer() {
  const values = new Map<string, string>();
  return {
    types: [] as string[],
    effectAllowed: '',
    dropEffect: '',
    setData(type: string, value: string) {
      values.set(type, value);
      if (!this.types.includes(type)) {
        this.types.push(type);
      }
    },
    getData(type: string) {
      return values.get(type) ?? '';
    },
  };
}

function createTask(overrides: Partial<TaskJournalItem> = {}): TaskJournalItem {
  return {
    id: 101,
    clientId: 10,
    clientName: 'Иванов Иван Иванович',
    clientCaseId: 100,
    clientCaseTitle: 'БАНКРОТСТВО',
    objectName: 'Иванов Иван Иванович - БАНКРОТСТВО',
    title: 'Тестовая задача',
    description: 'Описание задачи',
    assigneeUserId: 7,
    assigneeName: 'Администратор',
    authorUserId: 8,
    authorName: 'Постановщик',
    dueAt: todayAt(11),
    approvalDueAt: null,
    plannedDurationMinutes: 60,
    type: 'task',
    priority: 'normal',
    status: 'new',
    result: '',
    reminders: ['atDue'],
    channels: ['inApp'],
    tags: ['тест'],
    createdAt: todayAt(9),
    updatedAt: todayAt(9),
    ...overrides,
  };
}

function filterTaskJournalMockItems(tasks: TaskJournalItem[], params: Record<string, unknown> = {}) {
  const now = Date.now();
  const from = typeof params.from === 'string' ? new Date(params.from).getTime() : null;
  const to = typeof params.to === 'string' ? new Date(params.to).getTime() : null;
  const todayFrom = typeof params.todayFrom === 'string' ? new Date(params.todayFrom).getTime() : null;
  const todayTo = typeof params.todayTo === 'string' ? new Date(params.todayTo).getTime() : null;
  const search = typeof params.search === 'string' ? params.search.trim().toLocaleLowerCase('ru') : '';

  return tasks.filter((task) => {
    const dueAt = new Date(task.dueAt).getTime();
    const active = task.status !== 'done' && task.status !== 'cancelled';
    const statusMatches = params.status === 'active'
      ? active
      : params.status === 'overdue'
        ? active && dueAt < now
        : typeof params.status === 'string' && params.status !== 'all'
          ? task.status === params.status
          : true;
    const metricMatches = params.metric === 'active'
      ? active
      : params.metric === 'overdue'
        ? active && dueAt < now
        : params.metric === 'today'
          ? active && todayFrom !== null && todayTo !== null && dueAt >= todayFrom && dueAt < todayTo
          : params.metric === 'highPriority'
            ? active && (task.priority === 'high' || task.priority === 'urgent')
            : true;
    const assigneeMatches = typeof params.assigneeUserId !== 'number'
      || task.assigneeUserId === params.assigneeUserId
      || task.participants?.some((participant) => participant.role === 'assignee' && participant.userId === params.assigneeUserId);
    const searchMatches = !search || [task.objectName, task.clientName, task.legalEntityName, task.clientCaseTitle, task.title]
      .some((value) => value?.toLocaleLowerCase('ru').includes(search));
    const billingMatches = typeof params.billingStatus !== 'string'
      || (active && task.billingStatus === params.billingStatus);

    return statusMatches
      && metricMatches
      && assigneeMatches
      && (typeof params.authorUserId !== 'number' || task.authorUserId === params.authorUserId)
      && (typeof params.clientId !== 'number' || task.clientId === params.clientId)
      && (typeof params.legalEntityId !== 'number' || task.legalEntityId === params.legalEntityId)
      && (typeof params.clientCaseId !== 'number' || task.clientCaseId === params.clientCaseId)
      && (typeof params.type !== 'string' || task.type === params.type)
      && (typeof params.priority !== 'string' || task.priority === params.priority)
      && (params.underControl !== true || task.isUnderControl === true)
      && (from === null || dueAt >= from)
      && (to === null || dueAt <= to)
      && searchMatches
      && billingMatches;
  });
}

async function renderJournal(
  tasks: TaskJournalItem[] = [createTask()],
  options: {
    initialView?: 'list' | 'board' | 'calendar' | 'timeline';
    mode?: 'journal' | 'approvals';
    assignees?: TaskJournalAssignee[];
    withRoute?: boolean;
    waitForTitle?: string | null;
    periodMode?: 'day' | 'week' | 'month' | 'all';
    embedded?: boolean;
    contextFilter?: {
      clientId?: number | null;
      legalEntityId?: number | null;
      objectName?: string | null;
      title?: string | null;
    };
    onOpenClient?: (clientId: number, caseId?: number | null) => void;
    onOpenCase?: (caseId: number, clientId?: number | null) => void;
    onOpenLegalEntity?: (legalEntityId: number) => void;
    onCreateInvoiceDraft?: (draft: unknown) => void;
    settings?: Partial<TaskJournalSettingsDto>;
    underControlOnly?: boolean;
    overdueFilterSignal?: number;
  } = {},
) {
  apiMock.getTaskJournalItems.mockReset();
  apiMock.getTaskJournalItems.mockImplementation(async (params: Record<string, unknown> = {}) => {
    const filtered = filterTaskJournalMockItems(tasks, params);
    const page = typeof params.page === 'number' ? params.page : 1;
    const pageSize = typeof params.pageSize === 'number' ? params.pageSize : 100;
    const offset = (page - 1) * pageSize;
    return {
      items: filtered.slice(offset, offset + pageSize),
      total: filtered.length,
      page,
      pageSize,
    };
  });
  apiMock.getTaskJournalSummary.mockReset();
  apiMock.getTaskJournalSummary.mockImplementation(async (params: Record<string, unknown> = {}) => {
    const filtered = filterTaskJournalMockItems(tasks, params);
    const active = filtered.filter((task) => task.status !== 'done' && task.status !== 'cancelled');
    const unbilled = active.filter((task) => task.billingStatus === 'unbilled');
    return {
      active: active.length,
      overdue: filtered.filter((task) => task.status !== 'done' && task.status !== 'cancelled' && new Date(task.dueAt).getTime() < Date.now()).length,
      today: filtered.filter((task) => task.status !== 'done' && task.status !== 'cancelled' && toTimeZoneDateKey(task.dueAt) === toTimeZoneDateKey(new Date())).length,
      highPriority: filtered.filter((task) => task.status !== 'done' && task.status !== 'cancelled' && (task.priority === 'high' || task.priority === 'urgent')).length,
      unbilled: unbilled.length,
      invoicesToPrepare: new Set(unbilled.map((task) => task.clientId ? `client:${task.clientId}` : task.legalEntityId ? `legal:${task.legalEntityId}` : `task:${task.id}`)).size,
      fixed: active.filter((task) => task.billingStatus === 'fixed').length,
      invoiced: active.filter((task) => task.billingStatus === 'invoiced').length,
      nonBillable: active.filter((task) => task.billingStatus === 'internal').length,
      unclassified: active.filter((task) => task.billingStatus === 'legacy').length,
    };
  });
  apiMock.getTaskJournalAssignees.mockResolvedValue(options.assignees ?? [
    { id: 7, name: 'Администратор', email: 'admin@example.com' },
  ]);
  apiMock.getTaskJournalSettings.mockResolvedValue({
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
    stickerStyles: cloneDefaultTaskStickerStyles(),
    ...options.settings,
  });
  const currentUserId = authState.user.id;
  const filterStorageKey = options.mode === 'approvals'
    ? `pp.task_journal.filters.v1.approvals.user:${currentUserId}`
    : `pp.task_journal.filters.v1.user:${currentUserId}`;
  if (!window.localStorage.getItem(filterStorageKey)) {
    window.localStorage.setItem(filterStorageKey, JSON.stringify({
      quickFilter: 'all',
      viewMode: options.initialView ?? 'list',
      periodMode: options.periodMode ?? 'all',
      statusFilter: 'active',
    }));
  }

  function RoutedJournal() {
    const [routeTaskId, setRouteTaskId] = useState<number | null>(null);
    return (
      <TaskJournal
        initialView={options.initialView}
        mode={options.mode}
        embedded={options.embedded}
        contextFilter={options.contextFilter}
        routeTaskId={routeTaskId}
        onRouteTaskChange={setRouteTaskId}
        onOpenClient={options.onOpenClient}
        onOpenCase={options.onOpenCase}
        onOpenLegalEntity={options.onOpenLegalEntity}
        onCreateInvoiceDraft={options.onCreateInvoiceDraft}
        underControlOnly={options.underControlOnly}
        overdueFilterSignal={options.overdueFilterSignal}
      />
    );
  }

  render(options.withRoute ? (
    <RoutedJournal />
  ) : (
    <TaskJournal
      initialView={options.initialView}
      mode={options.mode}
      embedded={options.embedded}
      contextFilter={options.contextFilter}
      onOpenClient={options.onOpenClient}
      onOpenCase={options.onOpenCase}
      onOpenLegalEntity={options.onOpenLegalEntity}
      onCreateInvoiceDraft={options.onCreateInvoiceDraft}
      underControlOnly={options.underControlOnly}
      overdueFilterSignal={options.overdueFilterSignal}
    />
  ));
  if (options.waitForTitle === null) {
    await screen.findByTestId('task-view-controls');
  } else {
    await screen.findAllByText(
      options.waitForTitle ?? tasks[0]?.title ?? 'Тестовая задача',
      {},
      { timeout: 10000 },
    );
  }
}

async function openFirstTaskForEdit() {
  fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
  await screen.findByRole('heading', { name: 'Просмотр задачи' });
  fireEvent.click(screen.getAllByRole('button', { name: 'Редактировать задачу' })[0]);
  await screen.findByRole('heading', { name: 'Редактировать задачу' });
}

async function openNewTaskFormAsGlobalInternal() {
  fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
  await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
  fireEvent.click(screen.getByRole('button', { name: 'Создать общую внутреннюю задачу без клиента' }));
  await screen.findByRole('heading', { name: 'Новая задача' });
}

async function continueTaskWizardForClient(options: { legalEntity?: boolean; classification?: 'billable' | 'internal' } = {}) {
  const counterpartyName = options.legalEntity ? /ООО Ромашка/ : /Иванов Иван Иванович/;
  const caseId = options.legalEntity ? '200' : '100';
  fireEvent.focus(screen.getByRole('combobox', { name: 'Клиент' }));
  const listbox = await screen.findByRole('listbox', { name: 'Клиент' });
  fireEvent.click(within(listbox).getByRole('option', { name: counterpartyName }));
  fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: caseId } });
  fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));
  await screen.findByRole('heading', { name: 'Определите учёт задачи' });
  fireEvent.click(screen.getByRole('button', { name: new RegExp(options.classification === 'internal' ? 'Внутренняя задача' : 'Платная задача') }));
  await screen.findByRole('heading', { name: 'Новая задача' });
}

describe('TaskJournal', () => {
  beforeEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
    dialogMock.confirm.mockResolvedValue(true);
    dialogMock.prompt.mockResolvedValue('Срочные задачи');
    window.localStorage.clear();
    authState.user = { id: 7, fullName: 'Администратор', email: 'admin@example.com' };
    authState.isAdmin = true;
    permissionsState.taskCanCreate = true;
    permissionsState.taskCanEdit = true;
    permissionsState.taskCanDelete = true;
    permissionsState.taskCanEditOthers = true;
    permissionsState.taskCanCompleteOthers = true;
    permissionsState.taskCanDeleteOthers = true;
    permissionsState.taskCanExport = true;
    permissionsState.caseCanExport = true;
    permissionsState.clientCanView = true;
    permissionsState.caseCanView = true;
    permissionsState.legalEntityCanView = true;
    permissionsState.yandexDiskCanCreate = true;
    permissionsState.taskViewScope = 'all';
    permissionsState.calendarViewScope = 'all';
    permissionsState.calendarVisibleUserIds = [];
    clientsState.clients = [
      { id: 10, name: 'Иванов Иван Иванович', phone: '+7 900 000-00-00', email: 'client@example.com' } as Client,
    ];
    casesState.cases = [
      { id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 200, legalEntityId: 20, title: 'КОРПОРАТИВНЫЙ СПОР', status: 'Open', createdAt: todayAt(8) } as ClientCase,
    ];
    legalEntitiesState.legalEntities = [
      { id: 20, shortName: 'ООО Ромашка', inn: '5400000000', email: 'office@example.com' } as LegalEntitySummary,
    ];
    apiMock.getUserSetting.mockReset();
    apiMock.getUserSetting.mockRejectedValue(new Error('not found'));
    apiMock.upsertUserSetting.mockResolvedValue(undefined);
    apiMock.getCalendarSyncSettings.mockReset();
    apiMock.getCalendarSyncSettings.mockResolvedValue({
      externalCalendarSyncEnabled: false,
      googleCalendarSyncEnabled: false,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      manualFullSyncCooldownMinutes: 10,
      completedTaskCalendarAction: 'color',
      completedTaskGoogleColorId: '8',
      syncMode: 'notConfigured',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: null,
    });
    apiMock.getFeatureFlags.mockResolvedValue({
      legal: {
        externalCourtSync: false,
        externalCalendarSync: false,
        courtEvents: false,
        documentTemplateLibrary: false,
        externalWorkOrderApproval: false,
        clientPortal: false,
        offlineCalendarReadOnly: false,
      },
    });
    offlineCalendarMock.clearOfflineCalendarSnapshot.mockResolvedValue(undefined);
    offlineCalendarMock.getOfflineCalendarTrustedDevice.mockResolvedValue(false);
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue(null);
    offlineCalendarMock.saveOfflineCalendarSnapshot.mockResolvedValue(null);
    offlineCalendarMock.setOfflineCalendarTrustedDevice.mockResolvedValue(undefined);
    apiMock.getTaskBillingContext.mockResolvedValue({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      contracts: [{ id: 501, number: '15/26', title: 'Юридические услуги', billingMode: 'Actual', status: 'Concluded' }],
    });
    apiMock.getTaskJournalItem.mockReset();
    apiMock.getTaskJournalItem.mockResolvedValue(createTask());
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([]);
    apiMock.createTaskJournalCaseAutomationTask.mockResolvedValue(createTask({
      type: 'control',
      priority: 'high',
      title: 'Проверить движение по делу',
      description: 'Автоконтроль: по делу нет активной задачи.',
      tags: ['автоконтроль'],
    }));
    apiMock.createTaskJournalCaseAutomationTasks.mockResolvedValue({
      createdTasks: [createTask({ id: 202, title: 'Проверить движение по делу', type: 'control' })],
      skippedCount: 0,
    });
    apiMock.downloadTaskJournalCalendar.mockResolvedValue({ blob: new Blob(['ics']), fileName: 'task-calendar.ics' });
    apiMock.downloadCaseCalendarIcs.mockResolvedValue({ blob: new Blob(['case-ics']), fileName: 'case-calendar.ics' });
    apiMock.downloadTaskPlanningReport.mockResolvedValue({ blob: new Blob(['report']), fileName: 'task-planning-report.docx' });
    apiMock.getCaseCalendarEvents.mockResolvedValue([]);
    apiMock.getCaseCalendarSummary.mockResolvedValue({
      from: todayAt(0),
      to: todayAt(23),
      totalEventCount: 0,
      overdueCount: 0,
      warningCount: 0,
      courtEventCount: 0,
      proceduralDeadlineCount: 0,
      caseDeadlineCount: 0,
      taskCount: 0,
      nearestEvent: null,
    });
    apiMock.createTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalStatus.mockResolvedValue(createTask({ status: 'done', result: 'Готово' }));
    apiMock.updateTaskJournalControl.mockResolvedValue(createTask({ isUnderControl: true }));
    apiMock.bulkUpdateTaskJournalItems.mockResolvedValue({ requested: 1, updated: 1, skipped: 0 });
    apiMock.bulkDeleteTaskJournalItems.mockResolvedValue({ requested: 1, updated: 1, skipped: 0 });
    apiMock.updateTaskJournalApproval.mockResolvedValue(createTask({ status: 'approved' }));
    apiMock.getTaskJournalComments.mockResolvedValue([]);
    apiMock.createTaskJournalComment.mockResolvedValue({
      id: 501,
      taskJournalItemId: 101,
      authorUserId: 7,
      authorName: 'Администратор',
      kind: 'comment',
      text: 'Комментарий по задаче',
      createdAt: todayAt(12),
      updatedAt: null,
    });
    apiMock.createTaskJournalDeadlineChangeRequest.mockResolvedValue({
      id: 502,
      taskJournalItemId: 101,
      authorUserId: 7,
      authorName: 'Администратор',
      kind: 'system',
      text: 'Администратор запросил перенос срока задачи.',
      createdAt: todayAt(12),
      updatedAt: null,
    });
    apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([]);
    apiMock.approveTaskJournalDeadlineChangeRequest.mockResolvedValue(createTask());
    apiMock.rejectTaskJournalDeadlineChangeRequest.mockResolvedValue(createTask());
    apiMock.getTaskJournalFiles.mockResolvedValue([]);
    apiMock.uploadTaskJournalFile.mockResolvedValue({
      id: 601,
      taskJournalItemId: 101,
      documentKey: 'doc-1',
      version: 1,
      originalFileName: 'document.pdf',
      contentType: 'application/pdf',
      sizeBytes: 12,
      uploadedByUserId: 7,
      uploadedByName: 'Администратор',
      uploadedAt: todayAt(12),
      isLatestVersion: true,
    });
    apiMock.downloadTaskJournalFile.mockResolvedValue({ blob: new Blob(['file']), fileName: 'document.pdf' });
    apiMock.uploadTaskJournalFileToYandexDisk.mockResolvedValue({ success: true, path: '/CRM/document.pdf' });
    apiMock.deleteTaskJournalFile.mockResolvedValue(undefined);
    apiMock.deleteTaskJournalItem.mockResolvedValue(undefined);
  });

  it('shows client, case and renamed task title field in the journal', async () => {
    await renderJournal();

    expect(screen.getByText('Контрагент / дело')).toBeTruthy();
    expect(screen.getByText('Наименование задачи')).toBeTruthy();
    expect(screen.getAllByText('Иванов Иван Иванович').length).toBeGreaterThan(0);
    expect(screen.getByText('БАНКРОТСТВО')).toBeTruthy();
  });

  it('shows a valid cached calendar as read-only when the task API is unavailable', async () => {
    const cachedTask = createTask({ title: 'Сохранённая задача' });
    apiMock.getFeatureFlags.mockResolvedValue({
      legal: {
        externalCourtSync: false,
        externalCalendarSync: false,
        courtEvents: false,
        documentTemplateLibrary: false,
        externalWorkOrderApproval: false,
        clientPortal: false,
        offlineCalendarReadOnly: true,
      },
    });
    apiMock.getTaskJournalItems.mockRejectedValue(new Error('offline'));
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue({
      schemaVersion: 1,
      userId: 7,
      trustedDevice: false,
      syncedAt: '2026-07-31T08:00:00.000Z',
      expiresAt: '2026-08-07T08:00:00.000Z',
      items: [cachedTask],
    });

    render(<TaskJournal initialView="calendar" />);

    expect(await screen.findByText('Офлайн-календарь · только просмотр')).toBeTruthy();
    expect(offlineCalendarMock.loadOfflineCalendarSnapshot).toHaveBeenCalledWith(7);
    expect(screen.queryByRole('button', { name: 'Добавить задачу' })).toBeNull();
    expect(toastMock.info).toHaveBeenCalledWith(
      expect.stringContaining('Изменения недоступны'),
      'Офлайн-календарь',
    );
  });

  it('opens a task from the list title and exposes full hover text for clipped fields', async () => {
    const taskTitle = 'ИГНАТЬЕВ - ДОВЕРЕННОСТЬ';
    const taskDescription = 'Проект доверенности, отправить нотариусу, договориться с клиентом о подписании.';
    await renderJournal([createTask({ title: taskTitle, description: taskDescription })]);

    const titleLink = screen.getByTestId('task-title-link-101');
    expect(titleLink.getAttribute('title')).toBe(taskTitle);
    expect(titleLink.getAttribute('aria-label')).toBe(`Открыть задачу ${taskTitle}`);
    expect(screen.getByText(taskDescription).getAttribute('title')).toBe(taskDescription);

    fireEvent.click(titleLink);

    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
  });

  it('loads the manual control section with the under-control filter', async () => {
    await renderJournal([createTask({ isUnderControl: true })], { underControlOnly: true });

    expect(screen.getByRole('heading', { name: 'На контроле' })).toBeTruthy();
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      underControl: true,
    }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
  });

  it('loads assignees and tasks with calendar visibility in calendar view', async () => {
    authState.isAdmin = false;
    permissionsState.calendarViewScope = 'specific';
    permissionsState.calendarVisibleUserIds = [11];

    await renderJournal([createTask({ assigneeUserId: 11, assigneeName: 'Выбранный сотрудник' })], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 11, name: 'Выбранный сотрудник', email: 'selected@example.com' },
      ],
    });

    await waitFor(() => {
      expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
        calendarView: true,
      }));
      expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledWith({ calendarView: true });
    });
  });

  it('does not request case-only task extras when the role cannot view cases', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanCreate = true;
    permissionsState.caseCanView = false;
    permissionsState.caseCanExport = false;

    await renderJournal([createTask()], { initialView: 'calendar' });

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalled());
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
    expect(apiMock.getCaseCalendarEvents).not.toHaveBeenCalled();
    expect(apiMock.getCaseCalendarSummary).not.toHaveBeenCalled();
  });

  it('filters active overdue tasks from the status selector', async () => {
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);

    await renderJournal([
      createTask({ id: 101, title: 'Просроченная задача', status: 'new', dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 102, title: 'Будущая задача', status: 'new', dueAt: dateAt(tomorrow, 10) }),
      createTask({ id: 103, title: 'Закрытая просроченная задача', status: 'done', dueAt: dateAt(yesterday, 10) }),
    ]);

    const statusSelect = screen.getByLabelText('Статус');
    expect(within(statusSelect).getByText('Просроченные', { selector: 'option' })).toBeTruthy();

    fireEvent.change(statusSelect, { target: { value: 'overdue' } });

    expect(await screen.findByText('Просроченная задача')).toBeTruthy();
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(screen.queryByText('Закрытая просроченная задача')).toBeNull();
  });

  it('keeps the task table compact and row actions pinned on smaller workspaces', async () => {
    await renderJournal([createTask()]);

    expect(screen.getByTestId('task-table').className).toContain('w-max');
    expect(screen.getByTestId('task-table').className).toContain('min-w-full');
    expect(screen.getByTestId('task-table').className).toContain('table-fixed');
    expect(screen.getByText('Действия').closest('th')?.className).toContain('sticky');
    expect(screen.getByTitle('Просмотр').closest('td')?.className).toContain('sticky');
    expect(screen.getByTestId('task-actions-101').className).toContain('grid-cols-[repeat(auto-fit,minmax(2.25rem,1fr))]');
    expect(screen.getByTestId('task-column-header-result').className).toContain('hidden');
    expect(screen.getByTestId('task-column-header-result').className).toContain('2xl:table-cell');
  });

  it('separates tasks by execution date when the journal is ordered by date', async () => {
    await renderJournal([
      createTask({ id: 301, title: 'Первая задача на 7 июня', dueAt: '2026-06-07T10:00:00Z' }),
      createTask({ id: 302, title: 'Вторая задача на 7 июня', dueAt: '2026-06-07T12:00:00Z' }),
      createTask({ id: 303, title: 'Задача на 8 июня', dueAt: '2026-06-08T10:00:00Z' }),
    ]);

    expect(screen.getByRole('separator', { name: 'Задачи на 7 июня 2026 года' })).toBeTruthy();
    expect(screen.getByRole('separator', { name: 'Задачи на 8 июня 2026 года' })).toBeTruthy();
    expect(screen.getAllByText('7 июня 2026 года')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Наименование задачи' }));

    await waitFor(() => {
      expect(screen.queryByRole('separator', { name: 'Задачи на 7 июня 2026 года' })).toBeNull();
    });
  });

  it('allows users to resize task columns and keeps the selected width', async () => {
    await renderJournal([createTask()]);

    const titleHeader = screen.getByTestId('task-column-header-title');
    expect(titleHeader.style.width).toBe('290px');

    fireEvent.mouseDown(screen.getByRole('separator', { name: 'Изменить ширину колонки Наименование задачи' }), {
      clientX: 290,
    });
    fireEvent.mouseMove(window, { clientX: 350 });
    fireEvent.mouseUp(window);

    expect(titleHeader.style.width).toBe('350px');
    expect(
      JSON.parse(window.localStorage.getItem('pp.task_journal.table_settings.v1.user:7') ?? '{}').columnWidths.title,
    ).toBe(350);
  });

  it('opens the daily reminder target as current employees overdue task list', async () => {
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);

    await renderJournal([
      createTask({ id: 201, title: 'Моя просроченная задача', assigneeUserId: 7, dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 202, title: 'Чужая просроченная задача', assigneeUserId: 8, dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 203, title: 'Моя будущая задача', assigneeUserId: 7, dueAt: dateAt(tomorrow, 10) }),
    ], { overdueFilterSignal: 1 });

    expect(await screen.findByText('Моя просроченная задача')).toBeTruthy();
    await waitFor(() => {
      expect(screen.queryByText('Чужая просроченная задача')).toBeNull();
      expect(screen.queryByText('Моя будущая задача')).toBeNull();
      expect(screen.getByTestId('task-quick-filter-assignedToMe').getAttribute('aria-pressed')).toBe('true');
      expect((screen.getByLabelText('Статус') as HTMLSelectElement).value).toBe('overdue');
    });
  });

  it('passes selected table sorting to the backend task journal endpoint', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Первая задача', assigneeName: 'Анна' }),
      createTask({ id: 102, title: 'Вторая задача', assigneeName: 'Сергей' }),
    ]);

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      sortBy: 'dueAt',
      sortDir: 'asc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Исполнитель' }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({
      sortBy: 'assignee',
      sortDir: 'asc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Исполнитель' }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({
      sortBy: 'assignee',
      sortDir: 'desc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));
  });

  it('loads one server page and moves to the next page without reloading metadata', async () => {
    const tasks = Array.from({ length: 60 }, (_, index) => createTask({
      id: index + 1,
      title: `Задача страницы ${index + 1}`,
    }));
    await renderJournal(tasks, { waitForTitle: 'Задача страницы 1' });

    const totalLabel = screen.getByText('Задач найдено:');
    expect(within(totalLabel.parentElement as HTMLElement).getByText('60')).toBeTruthy();
    expect(screen.queryByText('Задача страницы 26')).toBeNull();
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ page: 1, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );

    apiMock.getTaskJournalAssignees.mockClear();
    apiMock.getTaskJournalSettings.mockClear();
    apiMock.getCalendarSyncSettings.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Вперёд' }));

    expect(await screen.findByText('Задача страницы 26')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ));
    expect(apiMock.getTaskJournalAssignees).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSettings).not.toHaveBeenCalled();
    expect(apiMock.getCalendarSyncSettings).not.toHaveBeenCalled();
  });

  it('debounces server search and keeps global unbilled counter independent of the page', async () => {
    const tasks = Array.from({ length: 30 }, (_, index) => createTask({
      id: index + 1,
      title: index === 29 ? 'Наследство найдено' : `Обычная задача ${index + 1}`,
      billingStatus: 'unbilled',
    }));
    await renderJournal(tasks, { waitForTitle: 'Обычная задача 1' });

    expect(screen.getByText('Платные задачи без счёта: 30')).toBeTruthy();
    apiMock.getTaskJournalItems.mockClear();
    fireEvent.change(screen.getByRole('textbox', {
      name: 'Поиск по контрагенту, делу или наименованию задачи',
    }), { target: { value: 'Наследство' } });

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'Наследство', page: 1, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ), { timeout: 2000 });
    expect(await screen.findByText('Наследство найдено')).toBeTruthy();
    expect(screen.queryByText('Обычная задача 1')).toBeNull();
  });

  it('marks a task for manual control from the row actions', async () => {
    await renderJournal([createTask({ isUnderControl: false })]);

    fireEvent.click(screen.getByTitle('Поставить на контроль'));

    await waitFor(() => {
      expect(apiMock.updateTaskJournalControl).toHaveBeenCalledWith(101, true);
    });
    expect(toastMock.success).toHaveBeenCalledWith('Задача поставлена на контроль.', 'Журнал задач');
  });

  it('accepts a task into work from the row actions', async () => {
    await renderJournal([createTask({ status: 'new' })]);

    fireEvent.click(screen.getByTitle('Принять в работу'));

    await waitFor(() => {
      expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'inProgress', undefined);
    });
  });

  it('sets a new due date when a stale row is returned to revision', async () => {
    await renderJournal([createTask({
      status: 'new',
      dueAt: new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString(),
    })]);

    const statusSelect = Array.from(document.querySelectorAll('select'))
      .find((element) => element.value === 'new') as HTMLSelectElement | undefined;
    expect(statusSelect).toBeTruthy();

    fireEvent.change(statusSelect!, { target: { value: 'revision' } });

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'revision',
      undefined,
      expect.any(String),
    ));
  });

  it('opens the modern task card when the global setting is enabled', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    expect(await screen.findByTestId('task-modern-card-window')).toBeTruthy();
    expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
  });

  it('separates comments, files, timer and system activity in the modern task card', async () => {
    apiMock.getTaskJournalComments.mockResolvedValueOnce([
      {
        id: 501,
        taskJournalItemId: 101,
        authorUserId: 7,
        authorName: 'Администратор',
        kind: 'comment',
        text: 'Комментарий пользователя',
        createdAt: todayAt(9),
      },
      {
        id: 502,
        taskJournalItemId: 101,
        authorUserId: null,
        authorName: 'Система',
        kind: 'system',
        text: 'Изменён срок задачи.',
        createdAt: todayAt(10),
      },
    ]);
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([
      {
        id: 601,
        taskJournalItemId: 101,
        documentKey: 'doc-1',
        version: 1,
        originalFileName: 'доверенность.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Администратор',
        uploadedAt: todayAt(10),
        isLatestVersion: true,
      },
    ]);

    await renderJournal([createTask({ plannedDurationMinutes: 75 })], {
      settings: { taskCardWindowMode: 'modern' },
    });
    await openFirstTaskForEdit();

    const card = await screen.findByTestId('task-modern-card-window');
    expect(await within(card).findByRole('tab', { name: 'Комментарии 1' })).toBeTruthy();
    expect(within(card).getByRole('tab', { name: 'Файлы 1' })).toBeTruthy();
    expect(within(card).getByRole('tab', { name: 'Активность 1' })).toBeTruthy();
    expect(within(card).getByText('Комментарий пользователя')).toBeTruthy();
    expect(within(card).queryByText('Изменён срок задачи.')).toBeNull();

    fireEvent.click(within(card).getByRole('tab', { name: 'Активность 1' }));
    expect(await within(card).findByText('Изменён срок задачи.')).toBeTruthy();
    expect(within(card).queryByText('Комментарий пользователя')).toBeNull();

    fireEvent.click(within(card).getByRole('tab', { name: 'Файлы 1' }));
    expect(await within(card).findByText('доверенность.pdf')).toBeTruthy();

    fireEvent.click(within(card).getByRole('tab', { name: 'Таймер' }));
    expect(await within(card).findByText('Плановая длительность')).toBeTruthy();
    expect(within(card).getByText('1 ч 15 мин')).toBeTruthy();
  });

  it('keeps result disabled in the modern task card until completion', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    const resultInput = await screen.findByTestId('task-result-field') as HTMLTextAreaElement;
    expect(resultInput.disabled).toBe(true);

    fireEvent.change(screen.getByTestId('task-editor-status-select'), { target: { value: 'done' } });
    expect(resultInput.disabled).toBe(false);
  });

  it('shows contractor payment controls in the modern task card for an external assignee', async () => {
    await renderJournal([createTask()], {
      waitForTitle: null,
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    await continueTaskWizardForClient();

    fireEvent.change(screen.getByTestId('task-assignee-select'), { target: { value: '9' } });
    const contractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;

    expect(contractorPaymentCheckbox.disabled).toBe(false);
    fireEvent.click(contractorPaymentCheckbox);
    expect(screen.getByTestId('task-contractor-payment-amount')).toBeTruthy();
    expect(screen.getByTestId('task-contractor-payment-date')).toBeTruthy();
  });

  it('shows contractor payment controls in an existing modern task card and enables them after edit mode', async () => {
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Привлеченный исполнитель',
      }),
    ], {
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    const readonlyContractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;
    expect(readonlyContractorPaymentCheckbox.disabled).toBe(true);

    fireEvent.click(screen.getAllByRole('button', { name: 'Редактировать задачу' })[0]);
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    const contractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;
    expect(contractorPaymentCheckbox.disabled).toBe(false);
    fireEvent.click(contractorPaymentCheckbox);
    expect(screen.getByTestId('task-contractor-payment-amount')).toBeTruthy();
    expect(screen.getByTestId('task-contractor-payment-date')).toBeTruthy();
  });

  it('loads and creates tasks inside a client context', async () => {
    await renderJournal([createTask({ clientId: 10, clientCaseId: null })], {
      embedded: true,
      contextFilter: {
        clientId: 10,
        objectName: 'Иванов Иван Иванович',
        title: 'Иванов Иван Иванович',
      },
    });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      page: 1,
      pageSize: 25,
    }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(screen.getByText('Задачи: Иванов Иван Иванович')).toBeTruthy();
    expect(screen.queryByText('Проверка дел без задач')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));
    await screen.findByRole('heading', { name: 'Определите учёт задачи' });
    fireEvent.click(screen.getByRole('button', { name: /Платная задача/ }));
    await screen.findByRole('heading', { name: 'Новая задача' });
    fireEvent.change(screen.getByTestId('task-title-input'), { target: { value: 'Связаться с клиентом' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      clientBillingClassification: 'billable',
      title: 'Связаться с клиентом',
    }));
  });

  it('shows court hearing and meeting types in filters and the task form', async () => {
    await renderJournal([
      createTask({ title: 'Заседание по делу', type: 'courtHearing' }),
      createTask({ id: 102, title: 'Встреча с клиентом', type: 'meeting' }),
    ], { waitForTitle: 'Заседание по делу' });

    expect(screen.getAllByText('Суд. заседание').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Встреча').length).toBeGreaterThan(0);

    const typeFilterSelect = screen.getByLabelText('Тип задачи') as HTMLSelectElement;
    expect(Array.from(typeFilterSelect.options).map((option) => option.textContent)).toEqual(expect.arrayContaining([
      'Суд. заседание',
      'Встреча',
    ]));

    await openNewTaskFormAsGlobalInternal();

    const formTypeSelect = Array.from(document.querySelectorAll('select'))
      .find((select) => (
        Array.from(select.options).some((option) => option.value === 'courtHearing') &&
        !Array.from(select.options).some((option) => option.value === 'all')
      ));

    expect(formTypeSelect).toBeTruthy();
    expect(Array.from(formTypeSelect!.options).map((option) => option.textContent)).toEqual(expect.arrayContaining([
      'Суд. заседание',
      'Встреча',
    ]));

    fireEvent.change(screen.getByTestId('task-title-input'), { target: { value: 'Подготовиться к заседанию' } });
    fireEvent.change(formTypeSelect!, { target: { value: 'courtHearing' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Подготовиться к заседанию',
      type: 'courtHearing',
    }));
  });

  it('warns about a fixed-price contract before creating a client task', async () => {
    apiMock.getTaskBillingContext.mockResolvedValueOnce({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      contracts: [{ id: 601, number: 'FIX-26', title: 'Абонентское обслуживание', billingMode: 'Fixed', status: 'Concluded' }],
    });
    await renderJournal([], { waitForTitle: null });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.focus(screen.getByRole('combobox', { name: 'Клиент' }));
    fireEvent.click(within(await screen.findByRole('listbox', { name: 'Клиент' })).getByRole('option', { name: /Иванов Иван Иванович/ }));
    fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));

    expect(await screen.findByText('Договор с фиксированной оплатой')).toBeTruthy();
    expect(screen.getByText(/следите за объёмом услуг/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Клиентская задача/ }));

    expect(await screen.findByText('Клиентская задача в рамках договора с фиксированной оплатой.')).toBeTruthy();
  });

  it('treats a new client task without a contract as billable', async () => {
    apiMock.getTaskBillingContext.mockResolvedValueOnce({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      contracts: [],
    });
    await renderJournal([], { waitForTitle: null });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.focus(screen.getByRole('combobox', { name: 'Клиент' }));
    fireEvent.click(within(await screen.findByRole('listbox', { name: 'Клиент' })).getByRole('option', { name: /Иванов Иван Иванович/ }));
    fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));

    expect(await screen.findByText('По делу нет договора — задача платная')).toBeTruthy();
    expect(screen.getByText(/попадёт в контроль выставления счёта/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Платная задача без договора/ }));

    await screen.findByRole('heading', { name: 'Новая задача' });
    fireEvent.change(screen.getByTestId('task-title-input'), { target: { value: 'Платная работа без договора' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      clientCaseId: 100,
      clientBillingClassification: 'billable',
      title: 'Платная работа без договора',
    }));
    expect(apiMock.createTaskJournalItem.mock.calls[0][0]).not.toHaveProperty('billingContractId');
  });

  it.each([
    { taskCardWindowMode: 'legacy' as const, legalEntity: false, caseId: null },
    { taskCardWindowMode: 'modern' as const, legalEntity: false, caseId: '100' },
    { taskCardWindowMode: 'legacy' as const, legalEntity: true, caseId: null },
    { taskCardWindowMode: 'modern' as const, legalEntity: true, caseId: '200' },
  ])('creates an unpaid counterparty task without a contract check in the $taskCardWindowMode card', async ({ taskCardWindowMode, legalEntity, caseId }) => {
    await renderJournal([], {
      waitForTitle: null,
      settings: { taskCardWindowMode },
    });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });

    const unpaidButton = screen.getByRole('button', { name: 'Неоплачиваемая задача' }) as HTMLButtonElement;
    expect(unpaidButton.disabled).toBe(true);

    fireEvent.focus(screen.getByRole('combobox', { name: 'Клиент' }));
    fireEvent.click(within(await screen.findByRole('listbox', { name: 'Клиент' })).getByRole('option', {
      name: legalEntity ? /ООО Ромашка/ : /Иванов Иван Иванович/,
    }));
    if (caseId) {
      fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: caseId } });
    }

    expect(unpaidButton.disabled).toBe(false);
    fireEvent.click(unpaidButton);

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).getByText('Общая задача без контрагента')).toBeTruthy();
    expect(screen.getByText(taskCardWindowMode === 'modern'
      ? 'НЕОПЛАЧИВАЕМАЯ · НЕ ДЛЯ СЧЁТА'
      : 'Неоплачиваемая задача: привязана к контрагенту, но не будет учитываться при выставлении счёта.')).toBeTruthy();
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Бесплатная работа по клиенту' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.getTaskBillingContext).not.toHaveBeenCalled();
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      isInternal: false,
      clientId: legalEntity ? null : 10,
      legalEntityId: legalEntity ? 20 : null,
      clientCaseId: caseId ? Number(caseId) : null,
      clientBillingClassification: 'internal',
      title: 'Бесплатная работа по клиенту',
    }));
    expect(apiMock.createTaskJournalItem.mock.calls[0][0]).not.toHaveProperty('billingContractId');
  });

  it('shows how many invoices are needed and distributes active tasks by billing category', async () => {
    await renderJournal([
      createTask({ id: 301, clientId: 10, billingStatus: 'unbilled' }),
      createTask({ id: 302, clientId: 10, billingStatus: 'unbilled' }),
      createTask({ id: 303, clientId: null, legalEntityId: 20, billingStatus: 'unbilled' }),
      createTask({ id: 304, billingStatus: 'fixed' }),
      createTask({ id: 305, billingStatus: 'internal' }),
      createTask({ id: 306, billingStatus: 'invoiced' }),
      createTask({ id: 307, billingStatus: 'legacy' }),
    ]);

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    expect(within(analysis).getByText('Счетов подготовить: 2')).toBeTruthy();
    expect(within(analysis).getByText('Платные задачи без счёта: 3')).toBeTruthy();
    expect(within(analysis).getByText('По фиксированному договору: 1')).toBeTruthy();
    expect(within(analysis).getByText('Неоплачиваемые: 1')).toBeTruthy();
    expect(within(analysis).getByText('Уже включены в счёт: 1')).toBeTruthy();
    expect(within(analysis).getByText('Нужно распределить: 1')).toBeTruthy();

    fireEvent.click(within(analysis).getByRole('button', { name: 'Платные задачи без счёта: 3' }));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ billingStatus: 'unbilled', sortBy: 'counterparty', sortDir: 'asc' }),
      expect.anything(),
    ));
    expect(within(analysis).getByText(/Категория открыта в таблице ниже/i)).toBeTruthy();
  });

  it('prepares one invoice draft from selected unbilled client tasks', async () => {
    const onCreateInvoiceDraft = vi.fn();
    const unbilledTask = createTask({
      id: 321,
      title: 'Подготовить иск',
      clientBillingClassification: 'billable',
      billingContractId: 501,
      billingContractNumber: '15/26',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });
    await renderJournal([unbilledTask], {
      onCreateInvoiceDraft,
      settings: {
        financialDocumentsResponsibleUserId: 7,
        financialDocumentsResponsibleUserName: 'Администратор',
      },
    });

    expect(screen.getByTestId('task-unbilled-reminder').textContent).toContain('Платные задачи без счёта: 1');
    const unbilledBadge = screen.getByText('БЕЗ СЧЁТА').closest('[data-testid="task-billing-badge-321"]');
    expect(unbilledBadge).toBeTruthy();
    expect(unbilledBadge?.getAttribute('title')).toBe('ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Подготовить иск' }));
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить счёт (1)' }));

    expect(onCreateInvoiceDraft).toHaveBeenCalledWith(expect.objectContaining({
      billingTaskIds: [321],
      selectedClientCaseIds: [100],
      form: expect.objectContaining({ responsibleIds: ['7'] }),
      lineItems: [expect.objectContaining({ name: expect.stringMatching(/Подготовить иск \(задача №321 от/) })],
    }));
  });

  it('keeps priority and billing badges in one compact metadata row', async () => {
    await renderJournal([
      createTask({
        clientBillingClassification: 'internal',
        billingStatus: 'internal',
      }),
    ]);

    const badges = screen.getByTestId('task-meta-badges-101');
    const billingBadge = within(badges).getByTestId('task-billing-badge-101');

    expect(badges.className).toContain('flex');
    expect(badges.className).toContain('flex-nowrap');
    expect(badges.className).toContain('items-center');
    expect(within(badges).getByText('Обычный')).toBeTruthy();
    expect(within(billingBadge).getByText('НЕ ДЛЯ СЧЁТА')).toBeTruthy();
    expect(billingBadge.className).toContain('text-[10px]');
    expect(billingBadge.getAttribute('title')).toBe('НЕОПЛАЧИВАЕМАЯ · НЕ ДЛЯ СЧЁТА');
  });

  it('applies administrator sticker labels and colors in the journal', async () => {
    const stickerStyles = cloneDefaultTaskStickerStyles();
    stickerStyles['priority.normal'] = {
      ...stickerStyles['priority.normal'],
      shortLabel: 'СРЕДНИЙ',
      backgroundColor: '#fef3c7',
      textColor: '#92400e',
      borderColor: '#f59e0b',
    };

    await renderJournal([createTask()], { settings: { stickerStyles } });

    const badges = screen.getByTestId('task-meta-badges-101');
    const priorityBadge = within(badges).getByText('СРЕДНИЙ').closest('span[title]');
    expect(priorityBadge).toBeTruthy();
    expect(priorityBadge?.getAttribute('style')).toContain('background-color: rgb(254, 243, 199)');
    expect(priorityBadge?.getAttribute('style')).toContain('color: rgb(146, 64, 14)');
  });

  it('shows who assigned the task in the journal and task card', async () => {
    await renderJournal([
      createTask({
        assigneeName: 'Кожеваткин Андрей Владимирович',
        authorName: 'Диденко Анна Владимировна',
      }),
    ]);

    expect(screen.getByText('Поручил')).toBeTruthy();
    expect(screen.getByText('Диденко Анна Владимировна')).toBeTruthy();

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect((screen.getByTestId('task-author-select') as HTMLSelectElement).selectedOptions[0]?.textContent).toBe('Диденко Анна Владимировна');
  });

  it.each(['legacy', 'modern'] as const)('allows an administrator to change the task author in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        authorUserId: 8,
        authorName: 'Старый постановщик',
      }),
    ], {
      settings: { taskCardWindowMode },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Старый постановщик', email: 'old-author@example.com' },
        { id: 9, name: 'Новый постановщик', email: 'new-author@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const card = screen.getByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-author-select'), { target: { value: '9' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      authorUserId: 9,
    })));
  });

  it('opens an existing task as a full page when the route is synced', async () => {
    await renderJournal(undefined, { withRoute: true });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);

    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect(screen.queryByTestId('task-table')).toBeNull();
    expect(screen.getByRole('button', { name: 'Журнал задач' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Журнал задач' }));

    await screen.findByTestId('task-table');
  });

  it('hides task export buttons when export permission is denied', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanExport = false;

    await renderJournal();

    expect(screen.queryByRole('button', { name: 'CSV' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Word' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'PDF' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'HTML' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'ICS' })).toBeNull();
  });

  it('hides task creation controls when create permission is denied', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanCreate = false;

    await renderJournal();

    expect(screen.queryByRole('button', { name: /Добавить задачу/i })).toBeNull();

    cleanup();
    window.localStorage.clear();
    await renderJournal(undefined, { initialView: 'calendar' });

    expect(screen.queryByTitle('Создать задачу на дату')).toBeNull();
    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('hides task delete controls when delete permission is denied', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanDeleteOthers = false;

    await renderJournal([createTask({
      assigneeUserId: 8,
      assigneeName: 'Постановщик',
      authorUserId: 8,
      authorName: 'Постановщик',
      title: 'Задача без удаления',
    })], { waitForTitle: null });

    expect(screen.queryByTitle('Удалить')).toBeNull();

    const taskCheckbox = await screen.findByRole(
      'checkbox',
      { name: 'Выбрать задачу Задача без удаления' },
      { timeout: 15000 },
    );
    fireEvent.click(taskCheckbox);

    expect(screen.getByTestId('task-bulk-actions')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Удалить выбранные' })).toBeNull();
    expect(apiMock.deleteTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.bulkDeleteTaskJournalItems).not.toHaveBeenCalled();
  });

  it('exports client planning report in Word format', async () => {
    await renderJournal(undefined, {
      contextFilter: {
        clientId: 10,
        objectName: 'Иванов Иван Иванович',
        title: 'Иванов Иван Иванович',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Word' }));

    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      format: 'word',
      status: 'active',
      clientId: 10,
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в Word.', 'Отчет по задачам');
  });

  it('exports task journal CSV and planning reports with current filters', async () => {
    let exportedBlob: Blob | null = null;
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      exportedBlob = blob as Blob;
      return 'blob:task-export';
    });
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    try {
      await renderJournal([
        createTask({
          title: 'Подготовить документы',
          clientId: 10,
          clientName: 'Иванов Иван Иванович',
          clientCaseId: 100,
          clientCaseTitle: 'БАНКРОТСТВО',
        }),
      ], {
        contextFilter: {
          clientId: 10,
          objectName: 'Иванов Иван Иванович',
          title: 'Иванов Иван Иванович',
        },
      });

      fireEvent.click(screen.getByRole('button', { name: 'CSV' }));

      expect(anchorClick).toHaveBeenCalledTimes(1);
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:task-export');
      expect(exportedBlob).not.toBeNull();
      const csv = await exportedBlob!.text();
      expect(csv).toContain('"Контрагент";"Дело";"Наименование задачи"');
      expect(csv).toContain('"Иванов Иван Иванович";"БАНКРОТСТВО";"Подготовить документы"');
      expect(toastMock.success).toHaveBeenCalledWith('Журнал задач экспортирован в CSV.', 'Журнал задач');

      fireEvent.click(screen.getByRole('button', { name: 'PDF' }));

      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
        format: 'pdf',
        status: 'active',
        clientId: 10,
      })));

      fireEvent.click(screen.getByRole('button', { name: 'HTML' }));

      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
        format: 'html',
        status: 'active',
        clientId: 10,
      })));
      expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в PDF.', 'Отчет по задачам');
      expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в HTML.', 'Отчет по задачам');
    } finally {
      createObjectUrl.mockRestore();
      revokeObjectUrl.mockRestore();
      anchorClick.mockRestore();
    }
  });

  it('shows case deadlines and court events in calendar mode', async () => {
    apiMock.getCaseCalendarSummary.mockResolvedValue({
      from: todayAt(0),
      to: todayAt(23),
      totalEventCount: 2,
      overdueCount: 0,
      warningCount: 1,
      courtEventCount: 1,
      proceduralDeadlineCount: 1,
      caseDeadlineCount: 0,
      taskCount: 0,
      nearestEvent: null,
    });
    apiMock.getCaseCalendarEvents.mockResolvedValue([
      {
        id: 'procedural-deadline:appeal:100',
        kind: 'procedural-deadline',
        sourceType: 'appeal',
        sourceId: null,
        title: 'Срок апелляции',
        startAt: todayAt(9),
        endAt: todayAt(10),
        allDay: true,
        clientCaseId: 100,
        caseTitle: 'БАНКРОТСТВО',
        clientName: 'Иванов Иван Иванович',
        legalEntityName: null,
        responsibleUserId: 7,
        responsibleName: 'Администратор',
        status: 'Open',
        priority: 'high',
        deadlineType: 'appeal',
        isOverdue: false,
        isWarning: true,
        courtName: 'Арбитражный суд',
        courtCaseNumber: 'А55-100/2026',
      },
    ]);

    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });

    expect(await screen.findByText('Сроки и суд')).toBeTruthy();
    expect(await screen.findByText('Срок апелляции')).toBeTruthy();
    expect(screen.getByText('Процессуальный срок')).toBeTruthy();
    expect(screen.getByText('2 событий')).toBeTruthy();
    expect(screen.getByText('Сроков: 1')).toBeTruthy();
    expect(screen.getByText('Судов: 1')).toBeTruthy();
    expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({ includeTasks: false }));
    expect(apiMock.getCaseCalendarSummary).toHaveBeenCalledWith(expect.objectContaining({ includeTasks: false }));
    const calendarParams = apiMock.getCaseCalendarEvents.mock.calls.at(-1)?.[0];
    expect(calendarParams?.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(calendarParams?.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('formats case calendar timed events with the profile time zone', async () => {
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Samara' }
        : null
    ));
    apiMock.getCaseCalendarSummary.mockResolvedValue({
      from: '2026-06-05T00:00:00Z',
      to: '2026-06-05T23:59:59Z',
      totalEventCount: 1,
      overdueCount: 0,
      warningCount: 0,
      courtEventCount: 1,
      proceduralDeadlineCount: 0,
      caseDeadlineCount: 0,
      taskCount: 0,
      nearestEvent: null,
    });
    apiMock.getCaseCalendarEvents.mockResolvedValue([
      {
        id: 'court:hearing:100',
        kind: 'court',
        sourceType: 'hearing',
        sourceId: null,
        title: 'Судебное заседание',
        startAt: '2026-06-05T12:00:00Z',
        endAt: '2026-06-05T13:00:00Z',
        allDay: false,
        clientCaseId: 100,
        caseTitle: 'Чудайкин - С/З',
        clientName: 'Иванов Иван Иванович',
        legalEntityName: null,
        responsibleUserId: 7,
        responsibleName: 'Администратор',
        status: 'Open',
        priority: 'normal',
        deadlineType: 'hearing',
        isOverdue: false,
        isWarning: false,
        courtName: 'Арбитражный суд',
        courtCaseNumber: 'А55-100/2026',
      },
    ]);

    await renderJournal([createTask()], { initialView: 'calendar' });

    expect(await screen.findByText('Судебное заседание')).toBeTruthy();
    expect(screen.getByText('05 июня 2026 г. 16:00')).toBeTruthy();
  });

  it('exports case calendar events to ICS with current calendar filters', async () => {
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });

    const panel = screen.getByText('Календарь дел').closest('section');
    expect(panel).not.toBeNull();
    fireEvent.change(within(panel as HTMLElement).getByTestId('case-calendar-ics-reminder-preset'), { target: { value: 'twoHours' } });
    fireEvent.click(within(panel as HTMLElement).getByRole('button', { name: 'ICS' }));

    await waitFor(() => expect(apiMock.downloadCaseCalendarIcs).toHaveBeenCalledWith(expect.objectContaining({
      includeTasks: false,
      reminderMinutes: [120, 30],
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Календарь дел выгружен в ICS.', 'Календарь дел');
  });

  it('filters case calendar events by selected assignee', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.change(screen.getByLabelText('Исполнитель'), { target: { value: '7' } });

    await waitFor(() => {
      expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({
        includeTasks: false,
        responsibleUserId: 7,
      }));
      expect(apiMock.getCaseCalendarSummary).toHaveBeenLastCalledWith(expect.objectContaining({
        includeTasks: false,
        responsibleUserId: 7,
      }));
    });
  });

  it('updates selected tasks through the bulk actions panel', async () => {
    const firstTask = createTask({ id: 101, title: 'Первая задача' });
    const secondTask = createTask({ id: 102, title: 'Вторая задача' });

    await renderJournal([firstTask, secondTask], {
      waitForTitle: 'Первая задача',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Юрист отдела', email: 'lawyer@example.com' },
      ],
    });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Первая задача' }));

    expect(screen.getByTestId('task-bulk-actions')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Массово назначить исполнителя'), { target: { value: '9' } });
    fireEvent.change(screen.getByLabelText('Массово изменить приоритет'), { target: { value: 'urgent' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledWith({
      ids: [101],
      assigneeUserId: 9,
      priority: 'urgent',
    });
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Обновлено задач: 1'), 'Журнал задач');
  });

  it('sends selected tasks to done through the bulk actions panel', async () => {
    const task = createTask({ id: 101, title: 'Закрыть массово', result: '' });

    await renderJournal([task], { waitForTitle: 'Закрыть массово' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Закрыть массово' }));
    fireEvent.change(screen.getByLabelText('Массово изменить статус'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledWith({
      ids: [101],
      status: 'done',
    });
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Обновлено задач: 1'), 'Журнал задач');
  });

  it('asks for task result before quick completion', async () => {
    const task = createTask({ id: 101, title: 'Закрыть с результатом', result: '' });

    await renderJournal([task], { waitForTitle: 'Закрыть с результатом' });

    fireEvent.click(screen.getByTitle('Завершить'));

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    const resultInput = screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.') as HTMLTextAreaElement;
    expect(resultInput.className).toContain('w-full');
    expect(resultInput.className).toContain('min-h-[120px]');
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));
    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.change(resultInput, { target: { value: 'Связались со следователем, документы переданы клиенту.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'done',
      'Связались со следователем, документы переданы клиенту.',
    ));
  });

  it('keeps completion prompt open when result file upload fails', async () => {
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('Файл не загрузился'));
    await renderJournal([createTask({ id: 101, title: 'Задача с файлом', result: '' })], { waitForTitle: 'Задача с файлом' });

    fireEvent.click(screen.getByTitle('Завершить'));
    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();

    const resultFile = new File(['Результат'], 'итог.txt', { type: 'text/plain' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), { target: { files: [resultFile] } });
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Работа выполнена.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    expect(await screen.findByText('Файл не загрузился')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
  });

  it('completes a task without result when completion result is optional', async () => {
    const task = createTask({ id: 101, title: 'Закрыть без результата', result: '' });

    await renderJournal([task], {
      waitForTitle: 'Закрыть без результата',
      settings: { requireCompletionResult: false },
    });

    fireEvent.click(screen.getByTitle('Завершить'));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', undefined));
    expect(screen.queryByRole('heading', { name: 'Добавить результат по задаче' })).toBeNull();
  });

  it('shows a neutral skipped count after bulk actions', async () => {
    apiMock.bulkUpdateTaskJournalItems.mockResolvedValueOnce({ requested: 2, updated: 1, skipped: 1 });
    const firstTask = createTask({ id: 101, title: 'Обновить одну' });
    const secondTask = createTask({ id: 102, title: 'Пропустить одну' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Обновить одну' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Обновить одну' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Пропустить одну' }));
    fireEvent.change(screen.getByLabelText('Массово изменить статус'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(toastMock.success).toHaveBeenCalledWith('Обновлено задач: 1. Пропущено: 1.', 'Журнал задач');
  });

  it('deletes selected tasks through the bulk actions panel', async () => {
    const firstTask = createTask({ id: 101, title: 'Первая задача' });
    const secondTask = createTask({ id: 102, title: 'Вторая задача' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Первая задача' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Первая задача' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить выбранные' }));

    await waitFor(() => expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledWith([101]);
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Удалено задач: 1'), 'Журнал задач');
  });

  it('shows a neutral skipped count after bulk delete', async () => {
    apiMock.bulkDeleteTaskJournalItems.mockResolvedValueOnce({ requested: 2, updated: 1, skipped: 1 });
    const firstTask = createTask({ id: 101, title: 'Удалить одну' });
    const secondTask = createTask({ id: 102, title: 'Оставить одну' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Удалить одну' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Удалить одну' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Оставить одну' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить выбранные' }));

    await waitFor(() => expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(toastMock.success).toHaveBeenCalledWith('Удалено задач: 1. Пропущено: 1.', 'Журнал задач');
  });

  it('opens client card from the counterparty link in the journal', async () => {
    const onOpenClient = vi.fn();
    const task = createTask({
      clientId: 42,
      clientCaseId: 314,
      clientName: 'Solomanin Oleg',
      objectName: 'Solomanin Oleg - Bankruptcy',
    });

    await renderJournal([task], { onOpenClient });

    fireEvent.click(screen.getByRole('button', { name: 'Solomanin Oleg' }));

    expect(onOpenClient).toHaveBeenCalledWith(42, 314);
  });

  it('opens case card from the case title in the journal', async () => {
    const onOpenCase = vi.fn();
    const task = createTask({
      clientId: 42,
      clientCaseId: 314,
      clientName: 'Solomanin Oleg',
      clientCaseTitle: 'Bankruptcy',
      objectName: 'Solomanin Oleg - Bankruptcy',
    });

    await renderJournal([task], { onOpenCase });

    fireEvent.click(screen.getByRole('button', { name: 'Bankruptcy' }));

    expect(onOpenCase).toHaveBeenCalledWith(314, 42);
  });

  it('opens legal entity card from the counterparty link in the journal', async () => {
    const onOpenLegalEntity = vi.fn();
    const task = createTask({
      clientId: null,
      clientName: '',
      legalEntityId: 20,
      legalEntityName: 'OOO Romashka',
      objectName: 'OOO Romashka',
    });

    await renderJournal([task], { onOpenLegalEntity });

    fireEvent.click(screen.getByRole('button', { name: 'OOO Romashka' }));

    expect(onOpenLegalEntity).toHaveBeenCalledWith(20);
  });

  it('shows only tasks that reached the current approver in approvals mode', async () => {
    authState.isAdmin = false;

    const approvalTask = createTask({
      id: 201,
      title: 'Согласовать проект',
      status: 'onApproval',
      assigneeUserId: 8,
      participants: [
        { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 0, approvalStatus: 'pending' },
      ],
    });
    const laterApprovalTask = createTask({
      id: 202,
      title: 'Ждет предыдущего согласующего',
      status: 'onApproval',
      assigneeUserId: 8,
      participants: [
        { userId: 9, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'pending' },
        { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 1, approvalStatus: 'pending' },
      ],
    });
    const regularTask = createTask({
      id: 203,
      title: 'Обычная задача',
      status: 'new',
      assigneeUserId: 7,
    });

    await renderJournal([approvalTask, laterApprovalTask, regularTask], {
      mode: 'approvals',
      waitForTitle: approvalTask.title,
    });

    expect(screen.getByRole('heading', { name: 'Согласования' })).toBeTruthy();
    expect(screen.getAllByText(approvalTask.title).length).toBeGreaterThan(0);
    expect(screen.queryByText(laterApprovalTask.title)).toBeNull();
    expect(screen.queryByText(regularTask.title)).toBeNull();
  });

  it('shows pending deadline change requests in approvals mode for the reviewer', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };

    const deadlineRequestTask = createTask({
      id: 204,
      title: 'Перенести срок подготовки документов',
      status: 'new',
      authorUserId: 8,
      authorName: 'Постановщик',
      assigneeUserId: 7,
      assigneeName: 'Исполнитель',
      activeDeadlineChangeRequests: [
        {
          id: 901,
          taskJournalItemId: 204,
          requestedByUserId: 7,
          requestedByName: 'Исполнитель',
          reviewerUserId: 8,
          reviewerName: 'Постановщик',
          currentDueAt: todayAt(18),
          requestedDueAt: todayAt(20),
          reason: 'Нужно дождаться документы от клиента',
          status: 'pending',
          decisionDueAt: null,
          decisionComment: null,
          decidedByUserId: null,
          decidedByName: null,
          decidedAt: null,
          createdCommentId: 501,
          decisionCommentId: null,
          createdAt: todayAt(12),
          updatedAt: todayAt(12),
        },
      ],
    });
    const regularTask = createTask({
      id: 205,
      title: 'Обычная задача без согласования',
      status: 'new',
      authorUserId: 8,
      assigneeUserId: 7,
    });

    await renderJournal([deadlineRequestTask, regularTask], {
      mode: 'approvals',
      waitForTitle: deadlineRequestTask.title,
    });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ includeDeadlineChangeRequests: true }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(screen.getAllByText(deadlineRequestTask.title).length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-deadline-change-request-badge-204')).toBeTruthy();
    expect(screen.getAllByText('Запрос переноса срока').length).toBeGreaterThan(0);
    expect(screen.queryByText(regularTask.title)).toBeNull();
  });

  it('shows a forced decision prompt for deadline change requests in approvals mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const requestedDueAt = todayAt(20);

    const deadlineRequestTask = createTask({
      id: 206,
      title: 'Согласовать перенос срока документов',
      status: 'new',
      authorUserId: 8,
      authorName: 'Постановщик',
      assigneeUserId: 7,
      assigneeName: 'Исполнитель',
      activeDeadlineChangeRequests: [
        {
          id: 902,
          taskJournalItemId: 206,
          requestedByUserId: 7,
          requestedByName: 'Исполнитель',
          reviewerUserId: 8,
          reviewerName: 'Постановщик',
          currentDueAt: todayAt(18),
          requestedDueAt,
          reason: 'Документы будут готовы завтра',
          status: 'pending',
          decisionDueAt: null,
          decisionComment: null,
          decidedByUserId: null,
          decidedByName: null,
          decidedAt: null,
          createdCommentId: 501,
          decisionCommentId: null,
          createdAt: todayAt(12),
          updatedAt: todayAt(12),
        },
      ],
    });

    await renderJournal([deadlineRequestTask], {
      mode: 'approvals',
      waitForTitle: deadlineRequestTask.title,
    });

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('У вас запросили перенос срока по задаче')).toBeTruthy();
    expect(within(prompt).getByText('Документы будут готовы завтра')).toBeTruthy();

    const approvedDueAtInput = within(prompt).getByLabelText('Новый срок задачи') as HTMLInputElement;
    expect(approvedDueAtInput.value).toBe(toDatetimeLocalInputValue(new Date(requestedDueAt)));
    const changedDueAt = new Date(requestedDueAt);
    changedDueAt.setHours(changedDueAt.getHours() + 2);
    fireEvent.change(approvedDueAtInput, {
      target: { value: toDatetimeLocalInputValue(changedDueAt) },
    });

    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать перенос' }));

    await waitFor(() => expect(apiMock.approveTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(206, 902, {
      approvedDueAt: expect.not.stringMatching(requestedDueAt),
      comment: null,
    }));
  });

  it('closes a forced deadline prompt when its task was deleted elsewhere', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const deletedTask = createTask({
      id: 208,
      title: 'Удалённая задача с переносом срока',
      authorUserId: 8,
      assigneeUserId: 7,
      activeDeadlineChangeRequests: [{
        id: 904,
        taskJournalItemId: 208,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Ожидаем документы',
        status: 'pending',
        decisionDueAt: null,
        decisionComment: null,
        decidedByUserId: null,
        decidedByName: null,
        decidedAt: null,
        createdCommentId: 501,
        decisionCommentId: null,
        createdAt: todayAt(12),
        updatedAt: todayAt(12),
      }],
    });
    apiMock.getTaskJournalItem.mockRejectedValueOnce(new Error('HTTP 404 Not Found at /api/tasks/208'));

    await renderJournal([deletedTask], { mode: 'approvals' });

    await screen.findByTestId('deadline-change-decision-prompt');
    await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
    expect(toastMock.info).toHaveBeenCalledWith(
      expect.stringContaining('Задача уже удалена'),
      'Согласования',
    );
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it('opens the deadline change decision from a journal row without entering the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const task = createTask({
      id: 207,
      title: 'Быстро рассмотреть перенос',
      authorUserId: 8,
      assigneeUserId: 7,
      activeDeadlineChangeRequests: [{
        id: 903,
        taskJournalItemId: 207,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Ожидаем документы',
        status: 'pending',
        decisionDueAt: null,
        decisionComment: null,
        decidedByUserId: null,
        decidedByName: null,
        decidedAt: null,
        createdCommentId: 501,
        decisionCommentId: null,
        createdAt: todayAt(12),
        updatedAt: todayAt(12),
      }],
    });

    await renderJournal([task], { waitForTitle: task.title });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ includeDeadlineChangeRequests: true }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Рассмотреть запрос переноса срока' }));

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('Ожидаем документы')).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Согласовать перенос' })).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Отказать' })).toBeTruthy();
  });

  it('lets the reviewer snooze a deadline change decision in the approvals journal', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const deadlineRequestTask = createTask({
      id: 206,
      title: 'Согласовать перенос срока документов',
      status: 'new',
      authorUserId: 8,
      activeDeadlineChangeRequests: [
        {
          id: 902,
          taskJournalItemId: 206,
          requestedByUserId: 7,
          requestedByName: 'Исполнитель',
          reviewerUserId: 8,
          reviewerName: 'Постановщик',
          currentDueAt: todayAt(18),
          requestedDueAt: todayAt(20),
          reason: 'Документы будут готовы завтра',
          status: 'pending',
          decisionDueAt: null,
          decisionComment: null,
          decidedByUserId: null,
          decidedByName: null,
          decidedAt: null,
          createdCommentId: 501,
          decisionCommentId: null,
          createdAt: todayAt(12),
          updatedAt: todayAt(12),
        },
      ],
    });

    await renderJournal([deadlineRequestTask], { mode: 'approvals' });

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '30 мин' }));

    await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY) || '{}') as Record<string, number>;
    expect(snoozes[getForcedTaskDeadlineChangeSnoozeKey(206, 902)]).toBeGreaterThan(Date.now());
  });

  it('shows approval action buttons only for the current approver in queue', async () => {
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { userId: 9, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'pending' },
          { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 1, approvalStatus: 'pending' },
        ],
      }),
    ]);

    await openFirstTaskForEdit();

    expect(screen.queryByRole('button', { name: 'Согласовать' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'На доработку' })).toBeNull();
  });

  it('opens the work approval window from a journal row without entering the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    const task = createTask({
      status: 'onApproval',
      result: 'Работа сдана исполнителем.',
      participants: [
        { id: 78, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
      ],
    });

    await renderJournal([task], { settings: { approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByRole('button', { name: 'Рассмотреть согласование' }));
    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByText('Работа сдана исполнителем.')).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Согласовать и завершить' })).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Вернуть на доработку' })).toBeTruthy();
  });

  it('shows a forced approval decision prompt for the current approver in approvals mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Исполнитель подготовил документы и приложил результат.',
        participants: [
          { id: 76, userId: 6, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'approved' },
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 1, approvalStatus: 'pending' },
          { id: 78, userId: 8, role: 'approver', displayName: 'Следующий согласующий', sortOrder: 2, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByRole('heading', { name: 'У вас задача на согласовании' })).toBeTruthy();
    expect(within(prompt).getByText('Исполнитель подготовил документы и приложил результат.')).toBeTruthy();

    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      'Исполнитель подготовил документы и приложил результат.',
      77,
      undefined,
    ));
  });

  it('keeps the approvals journal accessible when the same task was snoozed in the global prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, JSON.stringify({
      [getForcedTaskApprovalSnoozeKey(101)]: Date.now() + 10 * 60_000,
    }));

    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull();
    expect(screen.getByTestId('task-view-controls')).toBeTruthy();
  });

  it('lets the approver snooze the journal decision prompt without resolving the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY) || '{}') as Record<string, number>;
    expect(snoozes[getForcedTaskApprovalSnoozeKey(101)]).toBeGreaterThan(Date.now());
  });

  it('does not show the forced approval decision prompt to admin when admin is not the current approver', async () => {
    authState.isAdmin = true;
    authState.user = { id: 99, fullName: 'Admin viewer', email: 'admin-viewer@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        title: 'Task for another approver',
        status: 'onApproval',
        result: 'Work result is ready.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Current approver', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull();
  });

  it('requires a revision comment in the forced approval decision prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Результат на проверку.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByRole('button', { name: 'Вернуть на доработку' }));

    expect(within(prompt).getByText('Напишите, что нужно исправить перед повторным согласованием.')).toBeTruthy();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();

    fireEvent.change(within(prompt).getByLabelText('Комментарий для возврата на доработку'), {
      target: { value: 'Нужно приложить акт и уточнить дату.' },
    });
    fireEvent.change(within(prompt).getByLabelText('Срок доработки'), {
      target: { value: '2099-01-10T10:30' },
    });
    fireEvent.click(within(prompt).getByRole('button', { name: 'Вернуть на доработку' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Нужно приложить акт и уточнить дату.',
      77,
      undefined,
      expect.any(String),
    ));
  });

  it('lets the final approver complete the task from the forced approval decision prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Все работы выполнены.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать и завершить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      'Все работы выполнены.',
      77,
      'complete',
    ));
  });

  it.each(['legacy', 'modern'] as const)('opens the shared approval window from the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Итог работы передан на проверку.',
        participants: [
          { id: 79, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const buttonName = taskCardWindowMode === 'modern' ? 'Открыть окно согласования' : 'Открыть окно решения';
    fireEvent.click(within(card).getByRole('button', { name: buttonName }));

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByText('Итог работы передан на проверку.')).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('keeps save available while an editable task awaits approval in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = true;
    await renderJournal([
      createTask({
        status: 'onApproval',
        assigneeUserId: 7,
        assigneeName: 'Согласующий',
        result: 'Готово к согласованию.',
        participants: [
          { id: 79, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'completionButton', restrictExecutorEditing: false } });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const description = within(card).getByTestId('task-description-field');
    fireEvent.change(description, { target: { value: 'Исправленное описание перед решением.' } });

    expect(within(card).getByRole('button', { name: 'Сохранить' })).toBeTruthy();
    expect(within(card).getAllByRole('button', { name: 'Открыть окно согласования' }).length).toBeGreaterThan(0);
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem.mock.calls[0][1]).toEqual(expect.objectContaining({
      description: 'Исправленное описание перед решением.',
      status: 'onApproval',
    }));
  });

  it('opens approval from the green action in completion-button mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { approvalQuickActionMode: 'completionButton' } });

    expect(screen.queryByRole('button', { name: 'Рассмотреть согласование' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Действие по согласованию' }));

    expect(await screen.findByTestId('task-approval-decision-prompt')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it('disables completion and shows a separate approval action in separate-button mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { approvalQuickActionMode: 'separateButton' } });

    const completion = screen.getByRole('button', { name: 'Действие по согласованию' });
    expect((completion as HTMLButtonElement).disabled).toBe(true);
    const approval = screen.getByRole('button', { name: 'Рассмотреть согласование' });
    fireEvent.click(approval);

    expect(await screen.findByTestId('task-approval-decision-prompt')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('routes the completion action to approval in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'completionButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getAllByRole('button', { name: 'Открыть окно согласования' })[0]);

    expect(screen.getByText('Завершить согласование')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('disables completion in the %s task card when a separate approval action is configured', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const completionButtons = within(card).getAllByRole('button', { name: 'Задача находится на согласовании' });
    expect(completionButtons.length).toBeGreaterThan(0);
    completionButtons.forEach((button) => expect((button as HTMLButtonElement).disabled).toBe(true));
  });

  it('lets the current final approver choose whether to complete the task from view mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));
    expect(screen.getByText('Завершить согласование')).toBeTruthy();
    expect(screen.getByText(
      'Исполнитель получит уведомление о согласовании, а задача будет закрыта и перейдет в статус «Готово».',
    )).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Согласовать и завершить задачу/ }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      '',
      77,
      'complete',
    ));
  });

  it('lets the current final approver approve and return the task to the assignee', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));
    fireEvent.click(screen.getByRole('button', { name: /Согласовать и отправить исполнителю/ }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      '',
      77,
      'returnToAssignee',
    ));
  });

  it('requires a review before returning an approval task to revision', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'На доработку' }));
    expect(screen.getByText('Рецензия на доработку')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));
    expect(screen.getByText('Напишите рецензию: что именно нужно исправить перед повторным согласованием.')).toBeTruthy();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Комментарий для исполнителя'), {
      target: { value: 'Исправить дату и приложить документ.' },
    });
    fireEvent.change(screen.getByTestId('task-revision-due-at-input'), {
      target: { value: '' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Исправить дату и приложить документ.',
      77,
      undefined,
      undefined,
    ));
  });

  it('passes a revision due date when the approver fills it in', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'На доработку' }));
    fireEvent.change(screen.getByLabelText('Комментарий для исполнителя'), {
      target: { value: 'Исправить дату.' },
    });
    fireEvent.change(screen.getByTestId('task-revision-due-at-input'), {
      target: { value: '2099-01-10T10:30' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Исправить дату.',
      77,
      undefined,
      expect.any(String),
    ));
  });

  it('shows agreed tasks as approved for the executor after approval', async () => {
    await renderJournal([
      createTask({
        status: 'approved',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'approved' },
        ],
      }),
    ]);

    expect(screen.getByText('Согласовано: Согласующий')).toBeTruthy();
    expect(screen.getByDisplayValue('Согласовано')).toBeTruthy();
  });

  it('opens with all tasks quick filter and remembers switching to current user tasks', async () => {
    await renderJournal([
      createTask(),
      createTask({
        id: 102,
        title: 'Чужая задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    expect(screen.getByText('Тестовая задача')).toBeTruthy();
    expect(screen.getByText('Чужая задача')).toBeTruthy();
    expect(screen.getByTestId('task-quick-filter-all').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-quick-filter-assignedToMe'));

    await waitFor(() => expect(screen.queryByText('Чужая задача')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        quickFilter: 'assignedToMe',
      });
    });
  });

  it('keeps the search filter after opening a task from the filtered journal', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'Egor task',
        clientName: 'Egorov Sergey',
        objectName: 'EGOROV - BANKRUPTCY',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Other task',
        clientName: 'Petrov Petr',
        objectName: 'PETROV - BANKRUPTCY',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], { withRoute: true });

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'EGOROV' } });

    expect(await screen.findByText('Egor task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Other task')).toBeNull());

    const row = screen.getByText('Egor task').closest('tr');
    expect(row).toBeTruthy();
    fireEvent.click(within(row as HTMLTableRowElement).getByTitle('Просмотр'));

    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect(searchInput.value).toBe('EGOROV');
    expect(screen.queryByTestId('task-table')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Журнал задач' }));

    const restoredSearchInput = await screen.findByRole('textbox') as HTMLInputElement;
    expect(restoredSearchInput.value).toBe('EGOROV');
    expect(screen.getByText('Egor task')).toBeTruthy();
    expect(screen.queryByText('Other task')).toBeNull();

    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        search: 'EGOROV',
      });
    });
  });

  it('does not match another client by hidden task fields when searching by surname', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'ЕГОРОВА - счет 94',
        clientName: 'Егорова Татьяна Евгеньевна',
        clientCaseTitle: 'ОСПАРИВАНИЕ СДЕЛКИ ДОМ+ЗЕМЛЯ',
        objectName: 'Егорова Татьяна Евгеньевна - ОСПАРИВАНИЕ СДЕЛКИ',
      }),
      createTask({
        id: 102,
        title: 'ВОЯКИНА - решить вопрос по заключению договора',
        clientName: 'Воякина Елена Михайловна',
        clientCaseTitle: 'ВОЯКИНА - оформление дома и земли',
        objectName: 'Воякина Елена Михайловна - ОФОРМЛЕНИЕ ДОМА',
        assigneeName: 'Егорова Татьяна',
        authorName: 'Егорова Татьяна',
        result: 'Егорова упомянута во внутреннем результате',
        tags: ['Егорова'],
        participants: [
          { userId: 8, role: 'observer', displayName: 'Егорова Татьяна', approvalStatus: 'pending' },
        ],
      }),
    ]);

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'ЕГОРОВА' } });

    expect(await screen.findByText('ЕГОРОВА - счет 94')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('ВОЯКИНА - решить вопрос по заключению договора')).toBeNull());
  });

  it('filters tasks by due date range and remembers the range', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'May task',
        dueAt: new Date(2026, 4, 10, 10, 0, 0, 0).toISOString(),
      }),
      createTask({
        id: 102,
        title: 'June task',
        dueAt: new Date(2026, 5, 10, 10, 0, 0, 0).toISOString(),
      }),
    ], { waitForTitle: null });

    fireEvent.change(screen.getByTestId('task-date-from-filter'), { target: { value: '2026-05-01' } });
    fireEvent.change(screen.getByTestId('task-date-to-filter'), { target: { value: '2026-05-31' } });

    expect(await screen.findByText('May task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('June task')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        dateFrom: '2026-05-01',
        dateTo: '2026-05-31',
      });
    });
  });

  it('filters tasks by priority and remembers the selected priority', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'Обычная задача',
        priority: 'normal',
      }),
      createTask({
        id: 102,
        title: 'Срочная задача',
        priority: 'urgent',
      }),
    ]);

    fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'urgent' } });

    expect(await screen.findByText('Срочная задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Обычная задача')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        priorityFilter: 'urgent',
      });
    });
  });

  it('filters tasks by case and remembers the selected case', async () => {
    clientsState.clients.push({ id: 11, name: 'Петров Петр Петрович' } as Client);
    casesState.cases = [
      { id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 200, clientId: 11, title: 'НАСЛЕДСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 300, legalEntityId: 20, title: 'КОРПОРАТИВНЫЙ СПОР', status: 'Open', createdAt: todayAt(8) } as ClientCase,
    ];
    await renderJournal([
      createTask({
        id: 101,
        title: 'Задача по банкротству',
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
      }),
      createTask({
        id: 102,
        title: 'Задача по наследству',
        clientId: 11,
        clientName: 'Петров Петр Петрович',
        clientCaseId: 200,
        clientCaseTitle: 'НАСЛЕДСТВО',
      }),
      createTask({
        id: 103,
        title: 'Задача по корпоративному спору',
        clientId: null,
        clientName: undefined,
        legalEntityId: 20,
        legalEntityName: 'ООО Ромашка',
        clientCaseId: 300,
        clientCaseTitle: 'КОРПОРАТИВНЫЙ СПОР',
      }),
    ]);

    const caseFilter = screen.getByTestId('task-case-filter') as HTMLSelectElement;
    expect(caseFilter.disabled).toBe(true);
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
    expect(within(caseFilter).queryByText('НАСЛЕДСТВО', { selector: 'option' })).toBeNull();

    const counterpartySearch = screen.getByRole('textbox', {
      name: 'Поиск по контрагенту, делу или наименованию задачи',
    }) as HTMLInputElement;
    fireEvent.change(counterpartySearch, { target: { value: 'Петров' } });
    fireEvent.click(screen.getByRole('option', { name: 'Петров Петр Петрович' }));

    expect(caseFilter.disabled).toBe(false);
    expect(screen.getByTestId('task-counterparty-search').getAttribute('data-selected-value')).toBe('client:11');
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
    expect(within(caseFilter).getByText('НАСЛЕДСТВО', { selector: 'option' })).toBeTruthy();

    fireEvent.change(caseFilter, { target: { value: '200' } });

    expect(await screen.findByText('Задача по наследству')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Задача по банкротству')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        counterpartyFilter: 'client:11',
        caseFilter: '200',
      });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      format: 'word',
      clientCaseId: 200,
    })));

    fireEvent.click(screen.getByTestId('task-view-calendar'));
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      clientCaseId: 200,
      includeTasks: false,
    })));

    fireEvent.change(counterpartySearch, { target: { value: 'Иванов' } });
    fireEvent.click(screen.getByRole('option', { name: 'Иванов Иван Иванович' }));
    expect(caseFilter.value).toBe('all');
    expect(within(caseFilter).getByText('БАНКРОТСТВО', { selector: 'option' })).toBeTruthy();
    expect(within(caseFilter).queryByText('НАСЛЕДСТВО', { selector: 'option' })).toBeNull();

    apiMock.downloadTaskPlanningReport.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      legalEntityId: undefined,
      clientCaseId: undefined,
    })));

    fireEvent.change(counterpartySearch, { target: { value: 'Ромашка' } });
    fireEvent.click(screen.getByRole('option', { name: 'ООО Ромашка' }));
    expect(within(caseFilter).getByText('КОРПОРАТИВНЫЙ СПОР', { selector: 'option' })).toBeTruthy();
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
  });

  it('saves, applies and deletes custom task journal views', async () => {
    dialogMock.prompt.mockResolvedValueOnce('Срочные задачи');

    try {
      await renderJournal([
        createTask({
          id: 101,
          title: 'Обычная задача',
          priority: 'normal',
        }),
        createTask({
          id: 102,
          title: 'Срочная задача',
          priority: 'urgent',
        }),
      ], { waitForTitle: null });

      const searchInput = screen.getByPlaceholderText('Поиск по контрагенту, делу или наименованию задачи') as HTMLInputElement;
      fireEvent.change(searchInput, { target: { value: 'Иванов' } });
      fireEvent.click(screen.getByRole('option', { name: 'Иванов Иван Иванович' }));
      fireEvent.change(screen.getByTestId('task-case-filter'), { target: { value: '100' } });
      fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'urgent' } });
      fireEvent.change(searchInput, { target: { value: 'Срочная' } });
      fireEvent.click(screen.getByTestId('task-save-view'));

      await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith(
        'Представление «Срочные задачи» сохранено.',
        'Журнал задач',
      ));

      const rawSavedViews = window.localStorage.getItem('pp.task_journal.views.v1.user:7');
      expect(rawSavedViews).toBeTruthy();
      const savedViews = JSON.parse(rawSavedViews ?? '[]') as Array<{
        id: string;
        name: string;
        filters: { counterpartyFilter?: string; caseFilter?: string; priorityFilter?: string; search?: string };
      }>;
      expect(savedViews).toHaveLength(1);
      expect(savedViews[0]).toMatchObject({
        name: 'Срочные задачи',
        filters: {
          counterpartyFilter: 'client:10',
          caseFilter: '100',
          priorityFilter: 'urgent',
          search: 'Срочная',
        },
      });

      fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'all' } });
      fireEvent.click(screen.getByRole('button', { name: 'Очистить контрагента Иванов Иван Иванович' }));
      fireEvent.change(searchInput, { target: { value: '' } });

      fireEvent.change(screen.getByTestId('task-saved-view-select'), { target: { value: savedViews[0].id } });

      await waitFor(() => {
        expect((screen.getByTestId('task-priority-filter') as HTMLSelectElement).value).toBe('urgent');
        expect(screen.getByTestId('task-counterparty-search').getAttribute('data-selected-value')).toBe('client:10');
        expect((screen.getByTestId('task-case-filter') as HTMLSelectElement).value).toBe('100');
        expect(searchInput.value).toBe('Срочная');
      });

      fireEvent.click(screen.getByTestId('task-delete-view'));

      await waitFor(() => {
        expect(JSON.parse(window.localStorage.getItem('pp.task_journal.views.v1.user:7') ?? '[]')).toHaveLength(0);
      });
    } finally {
      dialogMock.prompt.mockResolvedValue('Срочные задачи');
    }
  });

  it('keeps date filters in a separate responsive row from view controls', async () => {
    await renderJournal();

    const mainFilterRow = screen.getByTestId('task-main-filter-row');
    const dateAndViewRow = screen.getByTestId('task-date-and-view-row');
    const dateFilterRow = screen.getByTestId('task-date-filter-row');
    const viewControls = screen.getByTestId('task-view-controls');
    const dateFrom = screen.getByTestId('task-date-from-filter');
    const dateTo = screen.getByTestId('task-date-to-filter');

    expect(mainFilterRow.className).toContain('xl:grid-cols-3');
    expect(mainFilterRow.className).toContain('2xl:grid-cols-[minmax(240px,1fr)_repeat(5,minmax(140px,190px))]');
    expect(screen.queryByTestId('task-counterparty-filter')).toBeNull();
    expect(screen.getByTestId('task-counterparty-search')).toBeTruthy();
    expect(mainFilterRow.contains(dateFrom)).toBe(false);
    expect(dateFilterRow.contains(dateFrom)).toBe(true);
    expect(dateFilterRow.contains(dateTo)).toBe(true);
    expect(dateAndViewRow.contains(viewControls)).toBe(true);
    expect(dateFilterRow.contains(viewControls)).toBe(false);
    expect(dateAndViewRow.className).toContain('flex-col');
  });

  it('applies day, week, month and full period buttons to the list view', async () => {
    await renderJournal([createTask()], { periodMode: 'day', waitForTitle: null });

    const expectPeriodMode = async (periodMode: 'day' | 'week' | 'month' | 'all') => {
      await waitFor(() => {
        expect(screen.getByTestId(`task-period-${periodMode}`).getAttribute('aria-pressed')).toBe('true');
        expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
          periodMode,
        });
      });
    };

    await expectPeriodMode('day');

    fireEvent.click(screen.getByTestId('task-period-week'));
    await expectPeriodMode('week');

    fireEvent.click(screen.getByTestId('task-period-month'));
    await expectPeriodMode('month');

    fireEvent.click(screen.getByTestId('task-period-all'));
    await expectPeriodMode('all');
  });

  it('shows open unresolved tasks across all assignees for the full period', async () => {
    const oldDate = new Date();
    oldDate.setFullYear(oldDate.getFullYear() - 1);
    oldDate.setHours(11, 0, 0, 0);

    await renderJournal([
      createTask({
        id: 101,
        title: 'Старая открытая задача',
        dueAt: oldDate.toISOString(),
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
      createTask({
        id: 102,
        title: 'Ожидает другого исполнителя',
        dueAt: oldDate.toISOString(),
        status: 'waiting',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 103,
        title: 'Закрытая старая задача',
        dueAt: oldDate.toISOString(),
        status: 'done',
        result: 'Готово',
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Другой исполнитель', email: 'executor@example.com' },
      ],
      waitForTitle: null,
    });

    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.click(screen.getByTestId('task-period-all'));

    await waitFor(() => expect(screen.getByText('Старая открытая задача')).toBeTruthy());
    expect(screen.getByText('Ожидает другого исполнителя')).toBeTruthy();
    expect(screen.queryByText('Закрытая старая задача')).toBeNull();
    expect((screen.getByLabelText('Статус') as HTMLSelectElement).value).toBe('active');
    expect((screen.getByLabelText('Исполнитель') as HTMLSelectElement).value).toBe('all');
    expect(screen.getByTestId('task-period-all').getAttribute('aria-pressed')).toBe('true');
  });

  it('switches journal presentation modes from the view controls', async () => {
    await renderJournal();

    expect(screen.getByTestId('task-table')).toBeTruthy();
    expect(screen.getByTestId('task-view-list').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-board'));

    await waitFor(() => expect(screen.getByTestId('task-board')).toBeTruthy());
    expect(screen.queryByTestId('task-table')).toBeNull();
    expect(screen.getByTestId('task-view-board').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-calendar'));

    await waitFor(() => expect(screen.getByTestId(`task-calendar-task-101`)).toBeTruthy());
    expect(screen.getByTestId('task-view-calendar').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-timeline'));

    await waitFor(() => expect(screen.getByTestId('task-timeline-body-grid')).toBeTruthy());
    expect(screen.getByTestId('task-view-timeline').getAttribute('aria-pressed')).toBe('true');
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        viewMode: 'timeline',
      });
    });

    fireEvent.click(screen.getByTestId('task-view-list'));

    await waitFor(() => expect(screen.getByTestId('task-table')).toBeTruthy());
    expect(screen.getByTestId('task-view-list').getAttribute('aria-pressed')).toBe('true');
  });

  it('keeps task filter and view controls constrained on mobile widths', async () => {
    await renderJournal();

    expect(screen.getByTestId('task-quick-filter-row').className).toContain('max-w-full');
    expect(screen.getByTestId('task-view-controls').className).toContain('max-w-full');
    expect(screen.getByTestId('task-period-control').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-view-mode-control').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-journal-shell').className).toContain('w-full');
    expect(screen.getByTestId('task-journal-shell').className).toContain('min-w-0');
    expect(screen.getByTestId('task-journal-shell').className).not.toContain('max-w-[');
    expect(screen.getByTestId('task-journal-shell').className).not.toContain('mx-auto');
    expect(screen.getByTestId('task-table-card').className).toContain('min-w-0');
    expect(screen.getByTestId('task-table-card').className).toContain('max-w-full');
    expect(screen.getByTestId('task-table-scroll').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-table-scroll').className).toContain('overscroll-x-contain');
    expect(screen.getByTestId('task-table').className).toContain('pp-task-journal-table');
    expect(screen.getByTestId('task-table').className).toContain('w-max');
    expect(screen.getByTestId('task-table').className).toContain('min-w-full');
    expect(screen.getByTestId('task-table').className).toContain('table-fixed');
    expect(screen.getByText('Действия').closest('th')?.className).toContain('sticky');
    expect(screen.getByTitle('Просмотр').closest('td')?.className).toContain('sticky');
    expect(screen.getByTestId('task-table').className).not.toContain('min-w-[920px]');
    expect(screen.getByTestId('task-column-header-author').className).toContain('hidden');
    expect(screen.getByTestId('task-column-header-author').className).toContain('2xl:table-cell');
  });

  it('restores the saved journal presentation mode for the current user', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'timeline',
      periodMode: 'week',
    }));

    await renderJournal([createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60 })], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-timeline-body-grid')).toBeTruthy());
    expect(screen.getByTestId('task-view-timeline').getAttribute('aria-pressed')).toBe('true');
  });

  it('saves and applies the selected task calendar color mode', async () => {
    await renderJournal([createTask({ priority: 'high' })], { initialView: 'calendar' });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    fireEvent.click(screen.getByTestId('task-color-mode-priority'));

    await waitFor(() => {
      expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-amber-200');
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarColorMode: 'priority',
      });
    });
  });

  it('restores the saved task calendar color mode', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarColorMode: 'status',
    }));

    await renderJournal([createTask({ status: 'inProgress' })], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-blue-200'));
    expect(screen.getByTestId('task-color-mode-status').getAttribute('aria-pressed')).toBe('true');
  });

  it('filters tasks authored by the current user from the quick controls', async () => {
    await renderJournal([
      createTask(),
      createTask({
        id: 102,
        title: 'Поставленная мной задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        authorUserId: 7,
        authorName: 'Администратор',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTestId('task-quick-filter-authoredByMe'));

    expect(await screen.findByText('Поставленная мной задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Тестовая задача')).toBeNull());
  });

  it('uses metric cards as task filters', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      periodMode: 'all',
    }));

    const overdue = new Date();
    overdue.setDate(overdue.getDate() - 1);
    overdue.setHours(10, 0, 0, 0);

    const future = new Date();
    future.setDate(future.getDate() + 1);
    future.setHours(23, 30, 0, 0);

    await renderJournal([
      createTask({ title: 'Overdue task', dueAt: overdue.toISOString() }),
      createTask({
        id: 102,
        title: 'Future task',
        dueAt: future.toISOString(),
        assigneeUserId: 7,
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], { waitForTitle: 'Future task' });

    expect(screen.getByText('Future task')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Просрочены/i }));

    expect(screen.getByText('Overdue task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Future task')).toBeNull());
  });

  it('recalculates metric counters when the status filter changes', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Открытая задача', status: 'new', dueAt: todayAt(11) }),
      createTask({
        id: 102,
        title: 'Закрытая задача',
        status: 'done',
        result: 'Готово',
        dueAt: todayAt(12),
      }),
    ]);

    await waitFor(() => {
      expect(within(screen.getByTestId('task-metric-Активные')).getByText('1')).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'done' } });

    await waitFor(() => {
      expect(within(screen.getByTestId('task-metric-Активные')).getByText('0')).toBeTruthy();
    });
    expect(screen.getByText('Закрытая задача')).toBeTruthy();
    expect(screen.queryByText('Открытая задача')).toBeNull();
  });

  it('shows overdue note under the current task status', async () => {
    const overdue = new Date();
    overdue.setDate(overdue.getDate() - 1);
    overdue.setHours(10, 0, 0, 0);

    await renderJournal([
      createTask({
        id: 101,
        title: 'Ожидающая просроченная задача',
        status: 'waiting',
        dueAt: overdue.toISOString(),
      }),
    ], { waitForTitle: null });

    fireEvent.click(screen.getByTestId('task-period-all'));

    const row = (await screen.findByText('Ожидающая просроченная задача')).closest('tr');
    expect(row).not.toBeNull();
    expect(within(row as HTMLTableRowElement).getByText('Ожидает', { selector: ':not(option)' })).toBeTruthy();
    expect(within(row as HTMLTableRowElement).getByText('Просрочена')).toBeTruthy();
  });

  it('does not save a new task without a required counterparty', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });

    expect((screen.getByRole('button', { name: 'Проверить договор' }) as HTMLButtonElement).disabled).toBe(true);

    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('finds and selects a client directly in the new task wizard', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    const search = screen.getByRole('combobox', { name: 'Клиент' });
    fireEvent.focus(search);
    fireEvent.change(search, { target: { value: 'Иванов' } });
    const listbox = await screen.findByRole('listbox', { name: 'Клиент' });
    fireEvent.click(within(listbox).getByRole('option', { name: /Иванов Иван Иванович/ }));

    expect((search as HTMLInputElement).value).toBe('Иванов Иван Иванович');
    expect(screen.queryByRole('listbox', { name: 'Клиент' })).toBeNull();
  });

  it('creates an internal task without a counterparty', async () => {
    await renderJournal();

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByTestId('task-title-input'), { target: { value: 'Internal process' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      isInternal: true,
      clientId: null,
      legalEntityId: null,
      clientCaseId: null,
      objectName: 'Внутренняя задача',
      title: 'Internal process',
    }));
  });

  it('shows the selected day schedule from the task date field', async () => {
    const busySlot = new Date();
    busySlot.setDate(busySlot.getDate() + 3);
    busySlot.setHours(14, 0, 0, 0);

    const selectedSlot = new Date(busySlot);
    selectedSlot.setHours(14, 30, 0, 0);

    const otherDay = new Date(busySlot);
    otherDay.setDate(otherDay.getDate() + 1);

    await renderJournal([
      createTask({
        title: 'Busy slot',
        dueAt: busySlot.toISOString(),
        plannedDurationMinutes: 60,
        assigneeUserId: 7,
        assigneeName: 'Администратор',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Other day task',
        dueAt: otherDay.toISOString(),
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
      createTask({
        id: 103,
        title: 'Other assignee same day task',
        dueAt: busySlot.toISOString(),
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { waitForTitle: null });

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByTestId('task-due-at-input'), {
      target: { value: toDatetimeLocalInputValue(selectedSlot) },
    });
    fireEvent.click(screen.getByTestId('task-day-schedule-toggle'));

    const panel = screen.getByTestId('task-day-schedule-panel');
    expect(within(panel).getByText('Busy slot')).toBeTruthy();
    expect(within(panel).queryByText('Other assignee same day task')).toBeNull();
    expect(within(panel).queryByText('Other day task')).toBeNull();
    expect(within(panel).getByText('Всего: 1')).toBeTruthy();
    expect(within(panel).getByText(/Пересечения: 1/)).toBeTruthy();
  });

  it('stacks description and result fields vertically in the task form', async () => {
    await renderJournal();

    await openNewTaskFormAsGlobalInternal();

    const textFields = screen.getByTestId('task-text-fields');
    const description = screen.getByTestId('task-description-field');
    const result = screen.getByTestId('task-result-field');

    expect(textFields.contains(description)).toBe(true);
    expect(textFields.contains(result)).toBe(true);
    expect(textFields.className).toContain('grid');
    expect(textFields.className).not.toContain('lg:grid-cols-2');
    expect(description.className).toContain('resize-y');
    expect(result.className).toContain('resize-y');
  });

  it('saves selected client and case from the task form', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    await continueTaskWizardForClient();
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Подготовить документы' } });
    const submitButtons = screen.getAllByRole('button', { name: 'Создать задачу' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      clientCaseId: 100,
      title: 'Подготовить документы',
    }));
  });

  it('sends contractor payment fields for an external assignee', async () => {
    await renderJournal([createTask()], {
      waitForTitle: null,
      assignees: [
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });
    await waitFor(() => expect(apiMock.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId'));

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByTestId('task-assignee-select'), { target: { value: '9' } });
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Оплатить работу исполнителя' } });

    fireEvent.click(screen.getByTestId('task-contractor-payment-checkbox'));
    fireEvent.change(screen.getByTestId('task-contractor-payment-amount'), { target: { value: '7500' } });
    fireEvent.click(screen.getByTestId('task-contractor-payment-on-result'));
    fireEvent.change(screen.getByTestId('task-contractor-payment-result-delay-days'), { target: { value: '5' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      assigneeUserId: 9,
      contractorPaymentEnabled: true,
      contractorPaymentAmount: 7500,
      contractorPaymentDueDate: null,
      contractorPaymentOnResult: true,
      contractorPaymentResultDelayDays: 5,
    }));
  });

  it('saves contractor payment exact date using the profile time zone', async () => {
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Samara' }
        : null
    ));

    await renderJournal([createTask()], {
      waitForTitle: null,
      assignees: [
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByTestId('task-assignee-select'), { target: { value: '9' } });
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Оплатить работу исполнителя' } });

    fireEvent.click(screen.getByTestId('task-contractor-payment-checkbox'));
    fireEvent.change(screen.getByTestId('task-contractor-payment-amount'), { target: { value: '7500' } });
    fireEvent.change(screen.getByTestId('task-contractor-payment-date'), { target: { value: '2026-05-26' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      assigneeUserId: 9,
      contractorPaymentEnabled: true,
      contractorPaymentAmount: 7500,
      contractorPaymentDueDate: fromTimeZoneDateTimeInput('2026-05-26T00:00', 'Europe/Samara'),
      contractorPaymentOnResult: false,
      contractorPaymentResultDelayDays: null,
    }));
  });

  it('uses user notification defaults when a new task is created', async () => {
    apiMock.getUserSetting.mockResolvedValue({
      key: 'tasks.notificationDefaults.v1',
      value: JSON.stringify({
        reminders: ['before1d', 'before15m', 'dailyOverdue'],
        channels: ['inApp', 'email', 'telegram'],
      }),
      updatedAt: todayAt(8),
    });

    await renderJournal();

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Созвониться с клиентом' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      reminders: ['before1d', 'before15m', 'dailyOverdue'],
      channels: ['inApp', 'email', 'telegram'],
    }));
  });

  it('shows only non-closed cases in the task case selector', async () => {
    casesState.cases = [
      { id: 100, clientId: 10, title: 'Открытое дело', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 101, clientId: 10, title: 'Приостановленное дело', status: 'OnHold', createdAt: todayAt(8) } as ClientCase,
      { id: 102, clientId: 10, title: 'Закрытое дело', status: 'Closed', createdAt: todayAt(8) } as ClientCase,
      { id: 107, clientId: 10, title: 'Проигранное дело', status: 'Lost', createdAt: todayAt(8) } as ClientCase,
      { id: 103, clientId: 10, title: 'Закрытое импортом', status: 'Закрытое', createdAt: todayAt(8) } as ClientCase,
      { id: 104, clientId: 10, title: 'Завершенное дело', status: 'Завершено', createdAt: todayAt(8) } as ClientCase,
      { id: 105, clientId: 10, title: 'Архивное дело', status: 'Archived', createdAt: todayAt(8) } as ClientCase,
      { id: 106, clientId: 10, title: 'Числовой закрытый статус', status: '2', createdAt: todayAt(8) } as ClientCase,
    ];

    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.focus(screen.getByRole('combobox', { name: 'Клиент' }));
    fireEvent.click(within(await screen.findByRole('listbox', { name: 'Клиент' })).getByRole('option', { name: /Иванов Иван Иванович/ }));

    const caseSelect = screen.getByLabelText('Дело');
    const options = Array.from(caseSelect.querySelectorAll('option')).map((option) => option.textContent);

    expect(options).toContain('Открытое дело');
    expect(options).toContain('Приостановленное дело');
    expect(options).not.toContain('Закрытое дело');
    expect(options).not.toContain('Проигранное дело');
    expect(options).not.toContain('Закрытое импортом');
    expect(options).not.toContain('Завершенное дело');
    expect(options).not.toContain('Архивное дело');
    expect(options).not.toContain('Числовой закрытый статус');
  });

  it('saves selected legal entity and legal entity case from the task form', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    apiMock.getTaskBillingContext.mockResolvedValueOnce({
      clientId: null,
      legalEntityId: 20,
      clientCaseId: 200,
      contracts: [{ id: 502, number: '16/26', title: 'Фиксированные усулги', billingMode: 'Actual', status: 'Concluded' }],
    });
    await continueTaskWizardForClient({ legalEntity: true });
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Проверить документы юрлица' } });
    const submitButtons = screen.getAllByRole('button', { name: 'Создать задачу' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: null,
      legalEntityId: 20,
      clientCaseId: 200,
      title: 'Проверить документы юрлица',
    }));
  });

  it('finds legal entities in counterparty lookup by INN and latin legal form', async () => {
    legalEntitiesState.legalEntities = [
      { id: 20, shortName: 'ООО Ромашка', inn: '5400000000', email: 'office@example.com' } as LegalEntitySummary,
      { id: 21, shortName: 'АО Василек', inn: '7700000000', email: 'vasilek@example.com' } as LegalEntitySummary,
    ];

    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    const searchInput = screen.getByRole('combobox', { name: 'Клиент' });
    fireEvent.focus(searchInput);
    const listbox = await screen.findByRole('listbox', { name: 'Клиент' });

    fireEvent.change(searchInput, { target: { value: '5400000000' } });

    expect(await within(listbox).findByRole('option', { name: /ООО Ромашка/ })).toBeTruthy();
    expect(within(listbox).queryByRole('option', { name: /АО Василек/ })).toBeNull();

    fireEvent.change(searchInput, { target: { value: 'ooo' } });

    expect(await within(listbox).findByRole('option', { name: /ООО Ромашка/ })).toBeTruthy();
    expect(within(listbox).queryByRole('option', { name: /АО Василек/ })).toBeNull();
  });

  it('creates a control task from case automation candidate', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Нет активной задачи уже 20 дн.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 20,
        isEscalated: true,
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
        suggestedDueAt: todayAt(9),
        suggestedPriority: 'urgent',
      },
    ]);
    await renderJournal();

    expect(screen.getByText('Автоконтроль дел')).toBeTruthy();
    expect(screen.getByText('Эскалация')).toBeTruthy();
    const createButton = screen.getByTestId('task-automation-create-100');
    expect(createButton.className).toContain('w-full');
    expect(createButton.closest('article')?.className).toContain('min-w-0');
    fireEvent.click(createButton);

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTask).toHaveBeenCalledWith(100));
    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith('Контрольная задача создана.', 'Автоконтроль дел');
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
  });

  it('keeps the complete case automation overview when the task search changes', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Кошелева Елена Юрьевна',
        clientCaseId: 100,
        clientCaseTitle: 'КОШЕЛЕВА - уголовное дело',
        reason: 'Нет активной задачи уже 21 дн.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 21,
        isEscalated: false,
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
        suggestedDueAt: todayAt(9),
        suggestedPriority: 'normal',
      },
    ]);
    await renderJournal([], { waitForTitle: null });
    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'ДРУГОЙ КЛИЕНТ' } });

    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
    expect(await screen.findByText('Кошелева Елена Юрьевна')).toBeTruthy();
    expect(screen.getByText('КОШЕЛЕВА - уголовное дело')).toBeTruthy();
  });

  it('creates control tasks for all automation candidates', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Нет активной задачи',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 1,
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: 'Создать наступившие (1)' }));

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTasks).toHaveBeenCalledWith({ limit: 1 }));
    expect(toastMock.success).toHaveBeenCalledWith('Создано контрольных задач: 1.', 'Автоконтроль дел');
  });

  it('shows all cases without active tasks by automation state', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'ГОТОВО К КОНТРОЛЮ',
        reason: 'Нет активной задачи уже 21 дн.',
        activeTaskCount: 0,
        completedTaskCount: 1,
        daysWithoutActiveTask: 21,
        isReadyForControl: true,
        automationState: 'ready',
        lastActivityAt: todayAt(8),
        lastActivitySource: 'task',
        lastTaskAt: null,
        nextControlAt: todayAt(9),
        daysUntilControl: 0,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
      {
        clientId: 11,
        clientName: 'Петров Петр Петрович',
        clientCaseId: 101,
        clientCaseTitle: 'ОЖИДАЕТ ПОРОГА',
        reason: 'Первый контроль через 11 дн.',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 3,
        automationState: 'waiting',
        lastActivityAt: todayAt(8),
        lastActivitySource: 'document',
        lastTaskAt: null,
        nextControlAt: todayAt(9),
        daysUntilControl: 11,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);

    await renderJournal();

    expect(screen.getByRole('heading', { name: 'Все дела без активных задач' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Пора создать: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Ожидают срока: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Создать наступившие (1)' })).toBeTruthy();
    expect(screen.getAllByText('Нет активных задач')).toHaveLength(2);
    expect(screen.getByText(/Последнее движение:.*задача/)).toBeTruthy();
    expect(screen.getByText(/Последнее движение:.*документ/)).toBeTruthy();
    expect(screen.getByText(/Следующий контроль:/)).toBeTruthy();

    const waitingTaskButton = screen.getByTestId('task-automation-create-101') as HTMLButtonElement;
    expect(waitingTaskButton.disabled).toBe(false);
    expect(waitingTaskButton.textContent).toContain('Поставить задачу');

    fireEvent.click(waitingTaskButton);

    expect(await screen.findByRole('heading', { name: 'Выберите клиента и дело' })).toBeTruthy();
    expect(apiMock.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
  });

  it('completes an active task with an existing result from the quick action', async () => {
    await renderJournal([createTask({ result: 'Результат уже заполнен.' })]);

    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalAssignees.mockClear();
    apiMock.getTaskJournalSettings.mockClear();
    apiMock.getCalendarSyncSettings.mockClear();
    apiMock.getUserSetting.mockClear();

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', undefined);
    expect(apiMock.getTaskJournalAssignees).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSettings).not.toHaveBeenCalled();
    expect(apiMock.getCalendarSyncSettings).not.toHaveBeenCalled();
    expect(apiMock.getUserSetting).not.toHaveBeenCalled();
    expect(toastMock.error).not.toHaveBeenCalledWith('Добавьте результат по задаче перед завершением.', 'Журнал задач');
  });

  it('refreshes once when another task journal instance reports a change', async () => {
    await renderJournal();
    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();

    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, {
      detail: { source: {} },
    }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
  });

  it('asks for task result before quick completion sends a task to approval', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { executorCompletionAllowedUserIds: [7] } });

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(screen.getByText('Отправка на согласование')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Работа выполнена, результат готов к проверке.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Работа выполнена, результат готов к проверке.',
    ));
    expect(toastMock.success).toHaveBeenCalledWith('Задача отправлена на согласование.', 'Журнал задач');
    expect(toastMock.error).not.toHaveBeenCalledWith(
      'Задачу нельзя завершить, пока все согласующие не согласовали её.',
      'Журнал задач',
    );
  });

  it('sends an edited task to approval when approvers are configured', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: { approvalDueDays: 9 },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Согласующий', email: 'approver@example.com' },
      ],
    });

    await openFirstTaskForEdit();

    fireEvent.click(screen.getByRole('button', { name: 'На согласование' }));

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Документы подготовлены, проверены и приложены к задаче.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const payload = apiMock.updateTaskJournalItem.mock.calls[0][1];
    const approvalDueAt = new Date(payload.approvalDueAt).getTime();
    expect(approvalDueAt).toBeGreaterThan(Date.now() + 8 * 24 * 60 * 60 * 1000);
    expect(approvalDueAt).toBeLessThan(Date.now() + 10 * 24 * 60 * 60 * 1000);
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'Документы подготовлены, проверены и приложены к задаче.',
      approvalDueAt: expect.any(String),
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'approver', userId: 8, approvalStatus: 'pending' }),
      ]),
    }));
  });

  it.each(['legacy', 'modern'] as const)('allows a fully editable task without approvers to enter approval in the %s card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        status: 'inProgress',
        result: '',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: {
        approvalDueDays: 5,
        taskCardWindowMode,
      },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Постановщик', email: 'author@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const sendButtons = within(card).getAllByRole('button', { name: 'Отправить постановщику на согласование' });
    expect(sendButtons.length).toBeGreaterThan(0);
    fireEvent.click(sendButtons[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Результат отправлен постановщику.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'Результат отправлен постановщику.',
      approvalDueAt: expect.any(String),
      participants: expect.not.arrayContaining([
        expect.objectContaining({ role: 'approver' }),
      ]),
    }));
  });

  it('asks for a result before sending a task to approval in the modern task card', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Согласующий', email: 'approver@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const modernCard = await screen.findByTestId('task-modern-card-window');

    fireEvent.click(within(modernCard).getByRole('button', { name: 'На согласование' }));

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'В новом окне результат заполнен перед согласованием.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'В новом окне результат заполнен перед согласованием.',
      approvalDueAt: expect.any(String),
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'approver', userId: 8, approvalStatus: 'pending' }),
      ]),
    }));
  });

  it('requires a result when completing an approved task from the editor', async () => {
    await renderJournal([
      createTask({
        status: 'approved',
        result: '',
        assigneeUserId: 8,
        assigneeName: 'Юрист',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Юрист', approvalStatus: 'pending' },
          { userId: 7, role: 'approver', displayName: 'Администратор', approvalStatus: 'approved' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Юрист', email: 'lawyer@example.com' },
      ],
      waitForTitle: null,
    });
    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');

    await openFirstTaskForEdit();

    const completeButtons = screen.getAllByRole('button', { name: 'Завершить' });
    fireEvent.click(completeButtons[completeButtons.length - 1]);

    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Согласованная задача выполнена, результат передан клиенту.' },
    });
    fireEvent.click(completeButtons[completeButtons.length - 1]);

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'done',
      result: 'Согласованная задача выполнена, результат передан клиенту.',
    }));
  });

  it('requires a result when a task is saved as done manually', async () => {
    await renderJournal([createTask({ status: 'approved', result: '' })], { waitForTitle: null });
    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');

    await openFirstTaskForEdit();

    fireEvent.change(screen.getByTestId('task-editor-status-select'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Задача закрыта после проверки документов.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'done',
      result: 'Задача закрыта после проверки документов.',
    }));
  });

  it('shows and saves approval due date in the task editor', async () => {
    const approvalDueAt = todayAt(16, 30);
    await renderJournal([createTask({ approvalDueAt })]);

    await openFirstTaskForEdit();

    const approvalDueAtInput = screen.getByTestId('task-approval-due-at-input') as HTMLInputElement;
    expect(approvalDueAtInput.value).toBe(toDatetimeLocalInputValue(new Date(approvalDueAt)));

    const nextApprovalDueAt = new Date();
    nextApprovalDueAt.setHours(18, 45, 0, 0);
    fireEvent.change(approvalDueAtInput, {
      target: { value: toDatetimeLocalInputValue(nextApprovalDueAt) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      approvalDueAt: nextApprovalDueAt.toISOString(),
    }));
  });

  it('adds an observer from the task actions menu', async () => {
    await renderJournal([createTask()], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Наблюдатель', email: 'observer@example.com' },
      ],
    });

    await openFirstTaskForEdit();

    fireEvent.click(screen.getByRole('button', { name: 'Действия с задачей' }));
    fireEvent.click(screen.getByRole('button', { name: 'Добавить подписчика-наблюдателя' }));
    fireEvent.change(screen.getByLabelText('Наблюдатель'), { target: { value: '8' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'observer', userId: 8, displayName: 'Наблюдатель' }),
      ]),
    }));
  });

  it('adds extra participants from dropdowns without duplicating the primary executor', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Дополнительный исполнитель', email: 'helper@example.com' },
      ],
    });

    await openFirstTaskForEdit();

    const extraExecutorSelect = screen.getByLabelText('Добавить исполнителя');
    expect(within(extraExecutorSelect).queryByRole('option', { name: /Администратор/ })).toBeNull();

    fireEvent.change(extraExecutorSelect, { target: { value: '8' } });
    expect(screen.getByRole('button', { name: 'Удалить участника Дополнительный исполнитель' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.participants).toEqual(expect.arrayContaining([
      expect.objectContaining({ role: 'assignee', userId: 7, displayName: 'Администратор' }),
      expect.objectContaining({ role: 'assignee', userId: 8, displayName: 'Дополнительный исполнитель' }),
    ]));
    expect(payload.participants.filter((participant: { role: string; userId?: number | null }) => participant.role === 'assignee' && participant.userId === 7)).toHaveLength(1);
  });

  it.each(['legacy', 'modern'] as const)('adds and removes several executors directly in the %s executor field', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Второй исполнитель', email: 'second@example.com' },
        { id: 9, name: 'Третий исполнитель', email: 'third@example.com' },
      ],
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const addExecutor = within(card).getByLabelText('Добавить исполнителя');

    fireEvent.change(addExecutor, { target: { value: '8' } });
    fireEvent.change(addExecutor, { target: { value: '9' } });

    const selected = within(card).getByTestId('task-additional-assignees-list');
    expect(within(selected).getByText('Второй исполнитель')).toBeTruthy();
    expect(within(selected).getByText('Третий исполнитель')).toBeTruthy();
    expect(within(selected).getByText('second@example.com')).toBeTruthy();
    expect(within(selected).getByText('third@example.com')).toBeTruthy();

    if (taskCardWindowMode === 'modern') {
      expect(within(card).getByTestId('task-schedule-fields-row')).toBeTruthy();
      expect(within(card).getByTestId('task-classification-fields-row')).toBeTruthy();
    }

    fireEvent.click(within(selected).getByRole('button', { name: 'Удалить участника Второй исполнитель' }));
    expect(within(selected).queryByText('Второй исполнитель')).toBeNull();
    expect(within(selected).getByText('Третий исполнитель')).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const executorIds = payload.participants
      .filter((participant: { role: string }) => participant.role === 'assignee')
      .map((participant: { userId?: number | null }) => participant.userId);

    expect(executorIds).toEqual(expect.arrayContaining([7, 9]));
    expect(executorIds).not.toContain(8);
  });

  it.each(['legacy', 'modern'] as const)('creates a task with several executors from the %s executor field', async (taskCardWindowMode) => {
    await renderJournal([], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Второй исполнитель', email: 'second@example.com' },
        { id: 9, name: 'Третий исполнитель', email: 'third@example.com' },
      ],
      settings: { taskCardWindowMode },
      waitForTitle: null,
    });

    await openNewTaskFormAsGlobalInternal();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Совместная задача' } });

    const addExecutor = within(card).getByLabelText('Добавить исполнителя');
    fireEvent.change(addExecutor, { target: { value: '8' } });
    fireEvent.change(addExecutor, { target: { value: '9' } });
    fireEvent.click(within(card).getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    const [payload] = apiMock.createTaskJournalItem.mock.calls[0];
    const executorIds = payload.participants
      .filter((participant: { role: string }) => participant.role === 'assignee')
      .map((participant: { userId?: number | null }) => participant.userId);

    expect(executorIds).toEqual(expect.arrayContaining([7, 8, 9]));
  });

  it.each(['legacy', 'modern'] as const)('keeps multiple approvers selected in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Первый согласующий', email: 'first-approver@example.com' },
        { id: 9, name: 'Второй согласующий', email: 'second-approver@example.com' },
      ],
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const addApproverLabel = taskCardWindowMode === 'modern' ? 'Добавить согласующего' : 'Добавить: Согласующие';

    fireEvent.change(within(card).getByLabelText(addApproverLabel), { target: { value: '8' } });
    fireEvent.change(within(card).getByLabelText(addApproverLabel), { target: { value: '9' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const approvers = payload.participants.filter((participant: { role: string }) => participant.role === 'approver');

    expect(approvers).toHaveLength(2);
    expect(approvers).toEqual(expect.arrayContaining([
      expect.objectContaining({ userId: 8, displayName: 'Первый согласующий', role: 'approver' }),
      expect.objectContaining({ userId: 9, displayName: 'Второй согласующий', role: 'approver' }),
    ]));
  });

  it('shows selected approvers in the modern task card and removes them from the list', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Первый согласующий', email: 'first-approver@example.com' },
        { id: 9, name: 'Второй согласующий', email: 'second-approver@example.com' },
      ],
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-modern-card-window');
    const addApproverSelect = within(card).getByLabelText('Добавить согласующего');

    fireEvent.change(addApproverSelect, { target: { value: '8' } });
    fireEvent.change(addApproverSelect, { target: { value: '9' } });

    const approverList = await within(card).findByTestId('task-modern-approver-list');
    expect(within(approverList).getByText('Первый согласующий')).toBeTruthy();
    expect(within(approverList).getByText('first-approver@example.com')).toBeTruthy();
    expect(within(approverList).getByText('Второй согласующий')).toBeTruthy();

    fireEvent.click(within(approverList).getByRole('button', { name: 'Удалить участника Первый согласующий' }));

    expect(within(approverList).queryByText('Первый согласующий')).toBeNull();
    expect(within(approverList).getByText('Второй согласующий')).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const approvers = payload.participants.filter((participant: { role: string }) => participant.role === 'approver');

    expect(approvers).toEqual([
      expect.objectContaining({ userId: 9, displayName: 'Второй согласующий', role: 'approver' }),
    ]);
  });

  it.each(['legacy', 'modern'] as const)('keeps multiple observers selected in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 10, name: 'Первый наблюдатель', email: 'first-observer@example.com' },
        { id: 11, name: 'Второй наблюдатель', email: 'second-observer@example.com' },
      ],
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const addObserverLabel = taskCardWindowMode === 'modern' ? 'Добавить наблюдателя' : 'Добавить: Наблюдатели';

    fireEvent.change(within(card).getByLabelText(addObserverLabel), { target: { value: '10' } });
    fireEvent.change(within(card).getByLabelText(addObserverLabel), { target: { value: '11' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const observers = payload.participants.filter((participant: { role: string }) => participant.role === 'observer');

    expect(observers).toHaveLength(2);
    expect(observers).toEqual(expect.arrayContaining([
      expect.objectContaining({ userId: 10, displayName: 'Первый наблюдатель', role: 'observer' }),
      expect.objectContaining({ userId: 11, displayName: 'Второй наблюдатель', role: 'observer' }),
    ]));
  });

  it('shows selected observers in the modern task card and removes them from the list', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 10, name: 'Первый наблюдатель', email: 'first-observer@example.com' },
        { id: 11, name: 'Второй наблюдатель', email: 'second-observer@example.com' },
      ],
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-modern-card-window');
    const addObserverSelect = within(card).getByLabelText('Добавить наблюдателя');

    fireEvent.change(addObserverSelect, { target: { value: '10' } });
    fireEvent.change(addObserverSelect, { target: { value: '11' } });

    const observerList = await within(card).findByTestId('task-modern-observer-list');
    expect(within(observerList).getByText('Первый наблюдатель')).toBeTruthy();
    expect(within(observerList).getByText('first-observer@example.com')).toBeTruthy();
    expect(within(observerList).getByText('Второй наблюдатель')).toBeTruthy();

    fireEvent.click(within(observerList).getByRole('button', { name: 'Удалить участника Первый наблюдатель' }));

    expect(within(observerList).queryByText('Первый наблюдатель')).toBeNull();
    expect(within(observerList).getByText('Второй наблюдатель')).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const observers = payload.participants.filter((participant: { role: string }) => participant.role === 'observer');

    expect(observers).toEqual([
      expect.objectContaining({ userId: 11, displayName: 'Второй наблюдатель', role: 'observer' }),
    ]);
  });

  it.each(['legacy', 'modern'] as const)('keeps existing participants visible and saved in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', email: 'admin@example.com', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Текущий согласующий', email: 'current-approver@example.com', approvalStatus: 'pending' },
          { userId: 10, role: 'observer', displayName: 'Текущий наблюдатель', email: 'current-observer@example.com', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Текущий согласующий', email: 'current-approver@example.com' },
        { id: 10, name: 'Текущий наблюдатель', email: 'current-observer@example.com' },
      ],
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    if (taskCardWindowMode === 'modern') {
      const approverGroup = await within(card).findByTestId('task-modern-approver-group');
      const observerGroup = await within(card).findByTestId('task-modern-observer-group');
      const approverList = within(approverGroup).getByTestId('task-modern-approver-list');
      const observerList = within(observerGroup).getByTestId('task-modern-observer-list');
      expect(within(approverList).getByText('Текущий согласующий')).toBeTruthy();
      expect(within(approverList).getByText('Ожидает решения')).toBeTruthy();
      expect(within(approverGroup).getByText('Принимают решение по задаче по очереди')).toBeTruthy();
      expect(within(approverList).queryByText('Текущий наблюдатель')).toBeNull();
      expect(within(observerList).getByText('Текущий наблюдатель')).toBeTruthy();
      expect(within(observerGroup).getByText('Следят за задачей, но не принимают решение')).toBeTruthy();
      expect(within(observerList).queryByText('Текущий согласующий')).toBeNull();
    } else {
      const approverGroup = await within(card).findByTestId('task-legacy-approver-group');
      const observerGroup = await within(card).findByTestId('task-legacy-observer-group');
      expect(within(approverGroup).getByRole('button', { name: 'Удалить участника Текущий согласующий' })).toBeTruthy();
      expect(within(approverGroup).getByText('Принимают решение по задаче по очереди.')).toBeTruthy();
      expect(within(approverGroup).queryByText('Текущий наблюдатель')).toBeNull();
      expect(within(observerGroup).getByRole('button', { name: 'Удалить участника Текущий наблюдатель' })).toBeTruthy();
      expect(within(observerGroup).getByText('Следят за задачей, но не принимают решение.')).toBeTruthy();
      expect(within(observerGroup).queryByText('Текущий согласующий')).toBeNull();
    }

    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];

    expect(payload.participants).toEqual(expect.arrayContaining([
      expect.objectContaining({ userId: 8, displayName: 'Текущий согласующий', role: 'approver' }),
      expect.objectContaining({ userId: 10, displayName: 'Текущий наблюдатель', role: 'observer' }),
    ]));
  });

  it.each(['legacy', 'modern'] as const)('returns an actively approved task to work after removing its last approver in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Документ отправлен на согласование.',
        approvalDueAt: todayAt(16, 30),
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', email: 'admin@example.com', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Ошибочный согласующий', email: 'removed-approver@example.com', approvalStatus: 'pending' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Ошибочный согласующий', email: 'removed-approver@example.com' },
      ],
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    fireEvent.click(within(card).getByRole('button', { name: 'Удалить участника Ошибочный согласующий' }));

    expect((within(card).getByTestId('task-editor-status-select') as HTMLSelectElement).value).toBe('inProgress');
    expect(within(card).queryByTitle('Задача находится на согласовании')).toBeNull();
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.status).toBe('inProgress');
    expect(payload.approvalDueAt).toBeNull();
    expect(payload.participants.some((participant: { role: string }) => participant.role === 'approver')).toBe(false);
  });

  it.each(['legacy', 'modern'] as const)('covers participant create edit remove reopen and save in the %s task card', async (taskCardWindowMode) => {
    const assignees: TaskJournalAssignee[] = [
      { id: 7, name: 'Администратор', email: 'admin@example.com' },
      { id: 8, name: 'Первый согласующий', email: 'first-approver@example.com' },
      { id: 10, name: 'Первый наблюдатель', email: 'first-observer@example.com' },
    ];
    const addApproverLabel = taskCardWindowMode === 'modern' ? 'Добавить согласующего' : 'Добавить: Согласующие';
    const addObserverLabel = taskCardWindowMode === 'modern' ? 'Добавить наблюдателя' : 'Добавить: Наблюдатели';
    const createdTaskTitle = `Проверка участников ${taskCardWindowMode}`;

    await renderJournal([], {
      assignees,
      settings: { taskCardWindowMode },
      waitForTitle: null,
    });

    await openNewTaskFormAsGlobalInternal();
    const createCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    fireEvent.change(within(createCard).getByTestId('task-title-input'), { target: { value: createdTaskTitle } });
    fireEvent.change(within(createCard).getByLabelText(addApproverLabel), { target: { value: '8' } });
    fireEvent.change(within(createCard).getByLabelText(addObserverLabel), { target: { value: '10' } });
    fireEvent.click(within(createCard).getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    const [createPayload] = apiMock.createTaskJournalItem.mock.calls[0];
    expect(createPayload.participants).toEqual(expect.arrayContaining([
      expect.objectContaining({ userId: 8, displayName: 'Первый согласующий', role: 'approver' }),
      expect.objectContaining({ userId: 10, displayName: 'Первый наблюдатель', role: 'observer' }),
    ]));

    const createdTask = createTask({
      id: 202,
      title: createdTaskTitle,
      isInternal: true,
      clientId: null,
      legalEntityId: null,
      clientCaseId: null,
      objectName: 'Внутренняя задача',
      participants: createPayload.participants,
    });

    cleanup();
    apiMock.createTaskJournalItem.mockClear();
    apiMock.updateTaskJournalItem.mockClear();

    await renderJournal([createdTask], {
      assignees,
      settings: { taskCardWindowMode },
      waitForTitle: createdTaskTitle,
    });
    await openFirstTaskForEdit();
    const editCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    if (taskCardWindowMode === 'modern') {
      expect(within(await within(editCard).findByTestId('task-modern-approver-list')).getByText('Первый согласующий')).toBeTruthy();
      expect(within(await within(editCard).findByTestId('task-modern-observer-list')).getByText('Первый наблюдатель')).toBeTruthy();
    } else {
      expect(within(editCard).getByRole('button', { name: 'Удалить участника Первый согласующий' })).toBeTruthy();
      expect(within(editCard).getByRole('button', { name: 'Удалить участника Первый наблюдатель' })).toBeTruthy();
    }
    fireEvent.click(within(editCard).getByRole('button', { name: 'Удалить участника Первый согласующий' }));
    expect(within(editCard).queryByRole('button', { name: 'Удалить участника Первый согласующий' })).toBeNull();
    expect(within(editCard).getByRole('button', { name: 'Удалить участника Первый наблюдатель' })).toBeTruthy();
    fireEvent.click(within(editCard).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, updatePayload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(updatePayload.participants).toEqual(expect.arrayContaining([
      expect.objectContaining({ userId: 10, displayName: 'Первый наблюдатель', role: 'observer' }),
    ]));
    expect(updatePayload.participants.some((participant: { role: string; userId?: number | null }) => (
      participant.role === 'approver' && participant.userId === 8
    ))).toBe(false);

    cleanup();
    apiMock.updateTaskJournalItem.mockClear();

    await renderJournal([{ ...createdTask, participants: updatePayload.participants }], {
      assignees,
      settings: { taskCardWindowMode },
      waitForTitle: createdTaskTitle,
    });
    await openFirstTaskForEdit();
    const reopenedCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(reopenedCard).queryByRole('button', { name: 'Удалить участника Первый согласующий' })).toBeNull();
    expect(within(reopenedCard).getByRole('button', { name: 'Удалить участника Первый наблюдатель' })).toBeTruthy();
  }, 30_000);

  it('sends a change comment with task edits', async () => {
    await renderJournal();

    await openFirstTaskForEdit();

    fireEvent.change(screen.getByPlaceholderText('Что изменили по задаче'), { target: { value: 'Поменяли уведомления и срок.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      changeComment: 'Поменяли уведомления и срок.',
    }));
  });

  it('duplicates a task from the task actions menu', async () => {
    apiMock.createTaskJournalItem.mockResolvedValue(createTask({ id: 202, title: 'Тестовая задача (копия)' }));
    await renderJournal();

    await openFirstTaskForEdit();

    fireEvent.click(screen.getByRole('button', { name: 'Действия с задачей' }));
    expect(screen.getByTestId('task-duplicate-icon')).toBeTruthy();
    expect(screen.getByText('Создать отдельную копию с теми же данными')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Дублировать задачу' }));

    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));
    await screen.findByRole('heading', { name: 'Определите учёт задачи' });
    fireEvent.click(screen.getByRole('button', { name: /Платная задача/ }));
    await screen.findByRole('heading', { name: 'Новая задача' });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Тестовая задача (копия)',
      status: 'new',
      result: '',
    }));
  });

  it('cancels a task from the legacy actions menu without deleting it', async () => {
    await renderJournal([createTask({ status: 'new' })]);

    await openFirstTaskForEdit();

    fireEvent.click(screen.getByRole('button', { name: 'Действия с задачей' }));
    expect(screen.getByTestId('task-cancel-icon')).toBeTruthy();
    expect(screen.getByText('Оставить в журнале со статусом «Отменена»')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отменить задачу' }));

    expect(dialogMock.confirm).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Отменить задачу?',
      confirmLabel: 'Отменить задачу',
    }));
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'cancelled',
    })));
    expect(apiMock.deleteTaskJournalItem).not.toHaveBeenCalled();
  });

  it('shows duplicate and cancel as direct icon actions in the modern task card', async () => {
    await renderJournal([createTask({ status: 'new' })], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    const duplicateButton = screen.getByRole('button', { name: 'Дублировать задачу' });
    const cancelButton = screen.getByRole('button', { name: 'Отменить задачу' });
    expect(duplicateButton.getAttribute('title')).toContain('создать отдельную копию');
    expect(cancelButton.getAttribute('title')).toContain('статусом «Отменена»');
    expect(within(duplicateButton).getByTestId('task-duplicate-icon')).toBeTruthy();
    expect(within(cancelButton).getByTestId('task-cancel-icon')).toBeTruthy();

    fireEvent.click(cancelButton);
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'cancelled',
    })));
  });

  it('opens executor task in view mode and saves result only after completion starts', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { executorCompletionAllowedUserIds: [7] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    const titleInput = screen.getByDisplayValue('Тестовая задача') as HTMLInputElement;
    expect(titleInput.disabled).toBe(true);

    const resultInput = screen.getByTestId('task-result-field') as HTMLTextAreaElement;
    expect(resultInput.disabled).toBe(true);
    fireEvent.click(screen.getAllByRole('button', { name: 'Завершить' }).at(-1)!);
    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Результат исполнителя' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', 'Результат исполнителя'));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to send a task without approvers to its author in the %s card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: {
        executorCompletionAllowedUserIds: [7],
        taskCardWindowMode,
      },
    });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const sendButtons = within(card).getAllByRole('button', { name: 'Отправить постановщику на согласование' });
    expect(sendButtons.length).toBeGreaterThan(0);
    fireEvent.click(sendButtons[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(screen.getByText('Отправка на согласование')).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Заявление подготовлено и приложено к задаче.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Заявление подготовлено и приложено к задаче.',
    ));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to resubmit a returned task in the %s card without general completion access', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 9,
        authorName: 'Постановщик',
        result: '',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
          {
            userId: 8,
            role: 'approver',
            displayName: 'Согласующий',
            approvalStatus: 'revisionRequested',
            resolution: 'Исправьте результат задачи.',
          },
        ],
      }),
    ], {
      settings: {
        taskCardWindowMode,
        executorCompletionAllowedUserIds: [],
      },
    });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    if (taskCardWindowMode === 'modern') {
      expect(within(card).queryByRole('button', { name: 'Запустить' })).toBeNull();
    }

    const submitButtons = within(card).getAllByRole('button', { name: 'На согласование' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Замечания устранены, исправленный результат приложен.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Замечания устранены, исправленный результат приложен.',
    ));
    expect(toastMock.success).toHaveBeenCalledWith('Задача отправлена на согласование.', 'Журнал задач');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('opens observed own-scope task in view mode even when role can edit tasks', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Observer', email: 'observer@example.com' };
    permissionsState.taskCanEdit = true;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskViewScope = 'own';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ quickFilter: 'all', periodMode: 'all' }));
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Assignee',
        authorUserId: 8,
        authorName: 'Author',
        participants: [
          { userId: 7, role: 'observer', displayName: 'Observer', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
    expect((screen.getByDisplayValue('Тестовая задача') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByPlaceholderText('Введите комментарий')).toBeNull();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('allows executor to comment on a read-only task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText('Введите комментарий'), { target: { value: 'Комментарий исполнителя' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' }));
    fireEvent.click(screen.getByRole('button', { name: /Добавить комментарий/i }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Комментарий исполнителя', true));
  });

  it.each(['legacy', 'modern'] as const)('explains external calendar comment visibility in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' })).toBeTruthy();
    expect(within(card).getByText(
      'Если календарь подключён, этот комментарий появится в описании события Google или Яндекс Календаря. Без отметки он останется только в CRM.',
    )).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to complete a read-only task from the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        result: '',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [7] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const completeButtons = within(card).getAllByRole('button', { name: 'Завершить' });
    fireEvent.click(completeButtons[completeButtons.length - 1]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    const resultFile = new File(['Итоговый документ'], 'результат.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), {
      target: { files: [resultFile] },
    });
    expect(screen.getByText('результат.pdf')).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Работа выполнена, результат передан постановщику.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, resultFile));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'done',
      'Работа выполнена, результат передан постановщику.',
    ));
    expect(apiMock.uploadTaskJournalFile.mock.invocationCallOrder[0])
      .toBeLessThan(apiMock.updateTaskJournalStatus.mock.invocationCallOrder[0]);
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.createTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('hides completion from an executor without personal access in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    expect(within(card).getAllByPlaceholderText(/комментарий/i).length).toBeGreaterThan(0);
  });

  it.each(['legacy', 'modern'] as const)('separately allows editing another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = true;
    permissionsState.taskCanCompleteOthers = false;
    permissionsState.taskCanDeleteOthers = false;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getAllByRole('button', { name: 'Редактировать задачу' }).length).toBeGreaterThan(0);
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
  });

  it.each(['legacy', 'modern'] as const)('separately allows completing another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskCanCompleteOthers = true;
    permissionsState.taskCanDeleteOthers = false;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    expect(screen.queryByTitle('Принять в работу')).toBeNull();
    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Редактировать задачу' })).toBeNull();
    expect(within(card).getAllByRole('button', { name: 'Завершить' }).length).toBeGreaterThan(0);
  });

  it.each(['legacy', 'modern'] as const)('separately allows deleting another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskCanCompleteOthers = false;
    permissionsState.taskCanDeleteOthers = true;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Редактировать задачу' })).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    const actionMenuButton = within(card).queryByRole('button', { name: 'Еще' });
    if (actionMenuButton) {
      fireEvent.click(actionMenuButton);
    }
    expect(within(card).getByRole('button', { name: 'Удалить задачу' })).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to request deadline change from the %s task card without moving task directly', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    fireEvent.change(within(card).getByLabelText('Новый срок для запроса переноса'), { target: { value: '2026-05-15T10:30' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Запросить перенос' }));

    const reasonDialog = await screen.findByTestId('deadline-request-reason-dialog');
    expect(apiMock.createTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    fireEvent.change(within(reasonDialog).getByLabelText('Причина запроса'), { target: { value: 'Нужны документы от клиента' } });
    fireEvent.click(within(reasonDialog).getByRole('button', { name: 'Отправить запрос' }));

    await waitFor(() => expect(apiMock.createTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(101, {
      requestedDueAt: expect.any(String),
      reason: 'Нужны документы от клиента',
    }));
    const requestPayload = apiMock.createTaskJournalDeadlineChangeRequest.mock.calls.at(-1)?.[1];
    expect(toDatetimeLocalInputValue(new Date(requestPayload.requestedDueAt))).toBe('2026-05-15T10:30');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('shows deadline change requests in the task card', async () => {
    apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([
      {
        id: 901,
        taskJournalItemId: 101,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Нужно дождаться документы от клиента',
        status: 'pending',
        decisionDueAt: null,
        decisionComment: null,
        decidedByUserId: null,
        decidedByName: null,
        decidedAt: null,
        createdCommentId: 501,
        decisionCommentId: null,
        createdAt: todayAt(12),
        updatedAt: todayAt(12),
      },
    ]);

    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));

    const requestsSection = await screen.findByTestId('task-deadline-change-requests');
    expect(within(requestsSection).getByText('Запросы переноса срока')).toBeTruthy();
    expect(within(requestsSection).getByText('Ожидает решения')).toBeTruthy();
    expect(within(requestsSection).getByText('Исполнитель')).toBeTruthy();
    expect(within(requestsSection).getByText('Ожидает решения: Постановщик')).toBeTruthy();
    expect(within(requestsSection).getByText(/Нужно дождаться документы от клиента/)).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('opens the deadline decision window from the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([{
      id: 904,
      taskJournalItemId: 101,
      requestedByUserId: 7,
      requestedByName: 'Исполнитель',
      reviewerUserId: 8,
      reviewerName: 'Постановщик',
      currentDueAt: todayAt(18),
      requestedDueAt: todayAt(20),
      reason: 'Нужно получить ответ суда',
      status: 'pending',
      decisionDueAt: null,
      decisionComment: null,
      decidedByUserId: null,
      decidedByName: null,
      decidedAt: null,
      createdCommentId: 501,
      decisionCommentId: null,
      createdAt: todayAt(12),
      updatedAt: todayAt(12),
    }]);

    await renderJournal([createTask({ authorUserId: 8, authorName: 'Постановщик' })], {
      settings: { taskCardWindowMode },
    });
    fireEvent.click(screen.getByTitle('Просмотр'));
    const section = await screen.findByTestId('task-deadline-change-requests');
    fireEvent.click(within(section).getByRole('button', { name: 'Рассмотреть запрос: согласовать или отказать' }));

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('Нужно получить ответ суда')).toBeTruthy();
    const approvedDueAtInput = within(prompt).getByLabelText('Новый срок задачи') as HTMLInputElement;
    expect(approvedDueAtInput.value).toBe(toDatetimeLocalInputValue(new Date(todayAt(20))));
  });

  it('renders task calendar as a journal subgroup', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Календарь/i }));

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    const calendarTask = await screen.findByTestId('task-calendar-task-101');
    expect(screen.getAllByText('Тестовая задача').length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-calendar-mobile-strip')).toBeTruthy();
    expect(screen.getByTestId('task-calendar-layout').className).toContain('grid-cols-[minmax(0,1fr)]');
    expect(screen.getByTestId('task-calendar-layout').className).toContain('min-w-0');
    expect(screen.getByTestId('task-calendar-grid-scroll').className).toContain('max-w-full');
    expect(screen.getByTestId('task-calendar-grid-scroll').className).toContain('overflow-x-auto');
    expect(calendarTask.getAttribute('title')).toContain('Статус: Новая');
    expect(calendarTask.getAttribute('title')).toContain('Исполнитель: Администратор');
    expect(calendarTask.getAttribute('title')).toContain('Поручил: Постановщик');
    expect(screen.getByTestId('task-calendar-tooltip-101').textContent).toContain('Дело: БАНКРОТСТВО');
    expect(screen.getByTestId('task-calendar-tooltip-101').textContent).toContain('Поручил: Постановщик');
  });

  it('can open directly in calendar mode from the journal submenu', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    expect(screen.getByText('Загрузка за день')).toBeTruthy();
  });

  it('opens quick create form from a calendar date', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getAllByTitle('Создать задачу на дату')[0]);

    expect(await screen.findByRole('heading', { name: 'Выберите клиента и дело' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Создать общую внутреннюю задачу без клиента' }));
    expect(await screen.findByRole('heading', { name: 'Новая задача' })).toBeTruthy();
    const dueInput = document.querySelector('input[type="datetime-local"]') as HTMLInputElement | null;
    expect(dueInput?.value).toMatch(/T09:00$/);
  });

  it('moves a task to another calendar day by drag and drop', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getByRole('button', { name: 'Неделя' }));
    const currentKey = dateKey(new Date());
    const targetDay = Array.from(document.querySelectorAll<HTMLElement>('[data-testid^="task-calendar-day-"]'))
      .find((element) => element.dataset.testid !== `task-calendar-day-${currentKey}`);
    expect(targetDay).toBeTruthy();
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay!, { dataTransfer });
    fireEvent.drop(targetDay!, { dataTransfer });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      clientId: 10,
      title: 'Тестовая задача',
      dueAt: expect.any(String),
    }));
    expect(apiMock.updateTaskJournalItem.mock.calls[0][1].dueAt).not.toEqual(createTask().dueAt);
  });

  it('keeps the local task time when calendar drag-and-drop uses the profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T23:30', 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        dueAt,
      }),
    ], { initialView: 'calendar', waitForTitle: null });

    const targetDay = await screen.findByTestId('task-calendar-day-2026-06-05');
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer });
    fireEvent.drop(targetDay, { dataTransfer });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.dueAt).toBe(fromTimeZoneDateTimeInput('2026-06-05T23:30', 'Asia/Kamchatka'));
    expect(getZonedParts(payload.dueAt, 'Asia/Kamchatka')).toMatchObject({
      year: 2026,
      month: 6,
      day: 5,
      hour: 23,
      minute: 30,
    });
  });

  it('groups calendar tasks by the user profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const profileDateKey = toTimeZoneDateKey(new Date(), 'Europe/Moscow');
    const dueAt = fromTimeZoneDateTimeInput(`${profileDateKey}T23:30`, 'Europe/Moscow');

    await renderJournal([
      createTask({
        title: 'Поздняя задача профиля',
        dueAt,
      }),
    ], { initialView: 'calendar', waitForTitle: null });

    const profileDay = await screen.findByTestId(`task-calendar-day-${profileDateKey}`);
    await waitFor(
      () => expect(within(profileDay).getByTestId('task-calendar-task-101')).toBeTruthy(),
      { timeout: 5000 },
    );
  });

  it('opens the task calendar on today from the user profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const profileDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${profileDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        title: 'Profile timezone task',
        dueAt,
      }),
    ], { initialView: 'calendar', periodMode: 'day' });

    const profileDay = await screen.findByTestId(`task-calendar-day-${profileDateKey}`);
    expect(profileDay.className).toContain('border-blue-300');
    expect(within(profileDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: profileDateKey,
      to: profileDateKey,
    })));
  });

  it('uses the selected assignee profile time zone when an admin views the task calendar', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const assigneeDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${assigneeDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        assigneeUserId: 8,
        assigneeName: 'Исполнитель Камчатка',
        dueAt,
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Исполнитель Камчатка', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com', timeZoneId: 'Europe/Moscow' },
        { id: 8, name: 'Исполнитель Камчатка', email: 'kamchatka@example.com', timeZoneId: 'Asia/Kamchatka' },
      ],
      waitForTitle: null,
    });

    const assigneeDay = await screen.findByTestId(`task-calendar-day-${assigneeDateKey}`);
    expect(assigneeDay.className).toContain('border-blue-300');
    expect(within(assigneeDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: assigneeDateKey,
      to: assigneeDateKey,
      responsibleUserId: 8,
    })));
  });

  it('uses the selected assignee profile time zone when the role can view employee task calendars', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    authState.isAdmin = false;
    permissionsState.calendarViewScope = 'all';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const assigneeDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${assigneeDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        assigneeUserId: 8,
        assigneeName: 'Исполнитель Камчатка',
        dueAt,
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Исполнитель Камчатка', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Секретарь', email: 'secretary@example.com', timeZoneId: 'Europe/Moscow' },
        { id: 8, name: 'Исполнитель Камчатка', email: 'kamchatka@example.com', timeZoneId: 'Asia/Kamchatka' },
      ],
      waitForTitle: null,
    });

    const assigneeDay = await screen.findByTestId(`task-calendar-day-${assigneeDateKey}`);
    expect(assigneeDay.className).toContain('border-blue-300');
    expect(within(assigneeDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: assigneeDateKey,
      to: assigneeDateKey,
      responsibleUserId: 8,
    })));
  });

  it('limits task calendar rows to the current user when the calendar role scope is own', async () => {
    authState.isAdmin = false;
    permissionsState.taskViewScope = 'all';
    permissionsState.calendarViewScope = 'own';

    await renderJournal([
      createTask({
        id: 101,
        title: 'Своя задача',
        assigneeUserId: 7,
        assigneeName: 'Секретарь',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Секретарь', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Чужая задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      waitForTitle: null,
    });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      responsibleUserId: 7,
    })));
  });

  it('limits task calendar rows to the current department when the calendar role scope is department', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Секретарь', email: 'secretary@example.com', departmentId: 1, department: 'Офис' };
    permissionsState.taskViewScope = 'all';
    permissionsState.calendarViewScope = 'department';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));

    await renderJournal([
      createTask({
        id: 101,
        title: 'Задача отдела',
        assigneeUserId: 8,
        assigneeName: 'Сотрудник отдела',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Сотрудник отдела', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Задача другого отдела',
        assigneeUserId: 9,
        assigneeName: 'Другой отдел',
        participants: [
          { userId: 9, role: 'assignee', displayName: 'Другой отдел', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Секретарь', email: 'secretary@example.com', departmentId: 1, department: 'Офис' },
        { id: 8, name: 'Сотрудник отдела', email: 'team@example.com', departmentId: 1, department: 'Офис' },
        { id: 9, name: 'Другой отдел', email: 'other@example.com', departmentId: 2, department: 'Юристы' },
      ],
      waitForTitle: null,
    });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      responsibleUserId: 8,
    })));
  });

  it('hides completed tasks from the task calendar when the calendar setting says to hide them', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));
    apiMock.getCalendarSyncSettings.mockResolvedValueOnce({
      externalCalendarSyncEnabled: false,
      googleCalendarSyncEnabled: false,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      manualFullSyncCooldownMinutes: 10,
      completedTaskCalendarAction: 'hide',
      completedTaskGoogleColorId: '8',
      syncMode: 'notConfigured',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: null,
    });

    await renderJournal([
      createTask({ status: 'done', result: 'Готово', dueAt: todayAt(11) }),
    ], { initialView: 'calendar', waitForTitle: null });

    await waitFor(() => expect(screen.queryByTestId('task-calendar-task-101')).toBeNull());
  });

  it('colors completed tasks in the task calendar when the calendar setting says to color them', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));

    await renderJournal([
      createTask({ status: 'done', result: 'Готово', dueAt: todayAt(11) }),
    ], { initialView: 'calendar', waitForTitle: null });

    const taskCard = await screen.findByTestId('task-calendar-task-101');
    expect(taskCard.className).toContain('border-slate-300');
    expect(taskCard.className).toContain('bg-slate-200');
  });

  it('updates task duration from the calendar load panel', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // Полдень UTC сохраняет один календарный день и в UTC окружения CI,
    // и в часовом поясе пользователя, поэтому тест проверяет изменение
    // длительности, а не зависит от локального часового пояса runner.
    vi.setSystemTime(new Date('2026-06-04T08:00:00.000Z'));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T11:00', 'Asia/Kamchatka');
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));

    await renderJournal([createTask({ dueAt, plannedDurationMinutes: 60 })], { initialView: 'calendar', periodMode: 'day' });

    fireEvent.click(screen.getByRole('button', { name: '+15 мин' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      dueAt,
      plannedDurationMinutes: 75,
    }));
  });

  it('highlights overlapping tasks in the task calendar', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-04T04:00:00.000Z'));
    await renderJournal([
      createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(11, 30), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
    ], { initialView: 'calendar' });

    expect(screen.getByText(/Есть риск конфликта/i)).toBeTruthy();
    expect(screen.getAllByText('Конфликт').length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-rose-200');
    expect(screen.getByTestId('task-calendar-task-101').getAttribute('title')).toContain('Конфликт: задача пересекается');
  });

  it('saves the selected calendar conflict policy', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getByTestId('task-conflict-policy-block'));

    await waitFor(() => {
      expect(screen.getByTestId('task-conflict-policy-block').getAttribute('aria-pressed')).toBe('true');
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarConflictPolicy: 'block',
      });
    });
  });

  it('blocks calendar drag-and-drop when the selected conflict policy forbids overlaps', async () => {
    const targetDate = getSameWeekDateOutsideTodayForTest(new Date());
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarConflictPolicy: 'block',
    }));

    await renderJournal([
      createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({
        id: 102,
        title: 'Занятый слот',
        dueAt: dateAt(targetDate, 11),
        plannedDurationMinutes: 60,
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
    ], { waitForTitle: null });

    const targetDay = await screen.findByTestId(`task-calendar-day-${dateKey(targetDate)}`);
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer });
    fireEvent.drop(targetDay, { dataTransfer });

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      'Перенос заблокирован: задача пересекается с "Занятый слот".',
      'Календарь задач',
    ));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('saves and applies the selected calendar load warning mode', async () => {
    await renderJournal([
      createTask({ dueAt: todayAt(9), plannedDurationMinutes: 60 }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(10), plannedDurationMinutes: 60 }),
      createTask({ id: 103, title: 'Третья задача', dueAt: todayAt(11), plannedDurationMinutes: 60 }),
    ], { initialView: 'calendar' });

    expect(screen.queryByText('Высокая загрузка')).toBeNull();

    fireEvent.click(screen.getByTestId('task-load-warning-strict'));

    await waitFor(() => {
      expect(screen.getByText('Высокая загрузка')).toBeTruthy();
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarLoadWarningMode: 'strict',
      });
    });
  });

  it('restores the saved relaxed calendar load warning mode', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarLoadWarningMode: 'relaxed',
    }));

    await renderJournal([
      createTask({ dueAt: todayAt(9), plannedDurationMinutes: 60 }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(10), plannedDurationMinutes: 60 }),
      createTask({ id: 103, title: 'Третья задача', dueAt: todayAt(11), plannedDurationMinutes: 60 }),
      createTask({ id: 104, title: 'Четвертая задача', dueAt: todayAt(12), plannedDurationMinutes: 60 }),
      createTask({ id: 105, title: 'Пятая задача', dueAt: todayAt(13), plannedDurationMinutes: 60 }),
    ], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-load-warning-relaxed').getAttribute('aria-pressed')).toBe('true'));
    expect(screen.queryByText('Высокая загрузка')).toBeNull();
    expect(screen.queryByText(/Есть риск конфликта/i)).toBeNull();
  });

  it('renders a weekly workload timeline with color legend', async () => {
    await renderJournal([
      createTask({ assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({ id: 102, title: 'Закрытая задача', status: 'done', result: 'Готово', assigneeUserId: 7, assigneeName: 'Администратор' }),
    ]);

    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    fireEvent.click(screen.getByRole('button', { name: /Нагрузка/i }));

    expect(screen.getByText('Календарь нагрузки')).toBeTruthy();
    expect(screen.getByText('Мои / выбранный исполнитель')).toBeTruthy();
    expect(screen.getByText('Выполненные/отмененные')).toBeTruthy();
    expect(await screen.findByTestId('task-timeline-task-101')).toBeTruthy();
    expect(await screen.findByTestId('task-timeline-task-102')).toBeTruthy();
  });

  it('keeps overlapping workload timeline tasks wide enough to show text', async () => {
    const overlappingTasks = Array.from({ length: 6 }, (_, index) => createTask({
      id: 200 + index,
      title: `Пересекающаяся задача ${index + 1}`,
      dueAt: todayAt(13),
      plannedDurationMinutes: 60,
      assigneeUserId: 7,
      assigneeName: 'Администратор',
    }));

    await renderJournal(overlappingTasks, {
      initialView: 'timeline',
    });

    const bodyGrid = screen.getByTestId('task-timeline-body-grid') as HTMLElement;

    expect(bodyGrid.style.gridTemplateColumns).toContain('minmax(744px, 1fr)');
    expect(screen.getByTestId('task-timeline-task-200').className).toContain('min-w-[7.25rem]');
    expect(screen.getByText('Пересекающаяся задача 1')).toBeTruthy();
  });

  it('moves a task to another time slot in the workload timeline', async () => {
    await renderJournal([createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60 })], { initialView: 'timeline' });

    const targetDay = screen.getByTestId(`task-timeline-day-${dateKey(new Date())}`);
    targetDay.getBoundingClientRect = vi.fn(() => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      bottom: 832,
      right: 180,
      width: 180,
      height: 832,
      toJSON: () => ({}),
    }));
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-timeline-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer, clientY: 96 });
    fireEvent.drop(targetDay, { dataTransfer, clientY: 96 });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const movedDueAt = new Date(payload.dueAt);
    expect(movedDueAt.getHours()).toBe(8);
    expect(movedDueAt.getMinutes()).toBe(0);
  });

  it('moves a timeline task to the selected local slot in the profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T11:00', 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        dueAt,
        plannedDurationMinutes: 60,
      }),
    ], { initialView: 'timeline' });

    const targetDay = await screen.findByTestId('task-timeline-day-2026-06-04');
    targetDay.getBoundingClientRect = vi.fn(() => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      bottom: 832,
      right: 180,
      width: 180,
      height: 832,
      toJSON: () => ({}),
    }));
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-timeline-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer, clientY: 96 });
    fireEvent.drop(targetDay, { dataTransfer, clientY: 96 });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.dueAt).toBe(fromTimeZoneDateTimeInput('2026-06-04T08:00', 'Asia/Kamchatka'));
    expect(getZonedParts(payload.dueAt, 'Asia/Kamchatka')).toMatchObject({
      year: 2026,
      month: 6,
      day: 4,
      hour: 8,
      minute: 0,
    });
  });

  it('loads task history and adds a comment from the edit form', async () => {
    apiMock.getTaskJournalComments.mockResolvedValueOnce([
      {
        id: 500,
        taskJournalItemId: 101,
        authorUserId: null,
        authorName: 'Система',
        kind: 'system',
        text: 'Задача создана.',
        createdAt: todayAt(9),
        updatedAt: null,
      },
    ]);

    await renderJournal();

    await openFirstTaskForEdit();

    expect(await screen.findByText('Задача создана.')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Введите комментарий'), { target: { value: 'Комментарий по задаче' } });
    fireEvent.click(screen.getByRole('button', { name: /Добавить комментарий/i }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Комментарий по задаче', false));
    expect(await screen.findAllByText('Комментарий по задаче')).toHaveLength(1);
  });

  it('shows task files and uploads a new version', async () => {
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([
      {
        id: 600,
        taskJournalItemId: 101,
        documentKey: 'doc-1',
        version: 1,
        originalFileName: 'document.pdf',
        contentType: 'application/pdf',
        sizeBytes: 1024,
        uploadedByUserId: 7,
        uploadedByName: 'Администратор',
        uploadedAt: todayAt(10),
        isLatestVersion: true,
        clientCaseDocumentId: 42,
      },
    ]);

    await renderJournal();

    await openFirstTaskForEdit();
    expect((await screen.findAllByText('document.pdf')).length).toBeGreaterThan(0);
    expect(await screen.findByText(/Документ дела #42/u)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить файл на Я.Диск' }));
    await waitFor(() => expect(apiMock.uploadTaskJournalFileToYandexDisk).toHaveBeenCalledWith(101, 600));

    const fileInputs = document.querySelectorAll('input[type="file"]');
    fireEvent.change(fileInputs[1], {
      target: {
        files: [new File(['updated'], 'document-v2.pdf', { type: 'application/pdf' })],
      },
    });

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, expect.any(File), 'doc-1'));
  });

  it.each([
    { taskCanExport: false, yandexDiskCanCreate: true, taskCardWindowMode: 'legacy' as const },
    { taskCanExport: true, yandexDiskCanCreate: false, taskCardWindowMode: 'legacy' as const },
    { taskCanExport: false, yandexDiskCanCreate: true, taskCardWindowMode: 'modern' as const },
    { taskCanExport: true, yandexDiskCanCreate: false, taskCardWindowMode: 'modern' as const },
  ])('hides task file yandex upload action when required permissions are denied %#', async ({
    taskCanExport,
    yandexDiskCanCreate,
    taskCardWindowMode,
  }) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Постановщик', email: 'author@example.com' };
    permissionsState.taskCanExport = taskCanExport;
    permissionsState.yandexDiskCanCreate = yandexDiskCanCreate;
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([
      {
        id: 600,
        taskJournalItemId: 101,
        documentKey: 'doc-1',
        version: 1,
        originalFileName: 'document.pdf',
        contentType: 'application/pdf',
        sizeBytes: 1024,
        uploadedByUserId: 7,
        uploadedByName: 'Постановщик',
        uploadedAt: todayAt(10),
        isLatestVersion: true,
        clientCaseDocumentId: 42,
      },
    ]);

    await renderJournal([
      createTask({
        authorUserId: 7,
        authorName: 'Постановщик',
        assigneeUserId: 7,
        assigneeName: 'Постановщик',
      }),
    ], { settings: { taskCardWindowMode } });

    await openFirstTaskForEdit();

    const taskCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') {
      fireEvent.click(within(taskCard).getByRole('tab', { name: 'Файлы 1' }));
    }
    expect((await screen.findAllByText('document.pdf')).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: 'Скачать файл' }).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Отправить файл на Я.Диск' })).toBeNull();
    expect(apiMock.uploadTaskJournalFileToYandexDisk).not.toHaveBeenCalled();
  });
});
