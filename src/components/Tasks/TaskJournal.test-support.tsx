import { fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, vi } from 'vitest';
import { TaskJournal } from './TaskJournal';
import type { TaskJournalSettingsDto } from '../../services/api';
import type { Client, ClientCase, LegalEntitySummary } from '../../types';
import { toTimeZoneDateKey } from '../../utils/timeZoneDate';
import { type TaskJournalAssignee, type TaskJournalItem } from '../../utils/taskJournalStore';
import { cloneDefaultTaskStickerStyles } from '../../utils/taskStickerStyles';

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
  getTaskJournalCaseAutomationSummary: vi.fn(),
  createTaskJournalCaseAutomationTask: vi.fn(),
  createTaskJournalCaseAutomationTasks: vi.fn(),
  downloadTaskJournalCalendar: vi.fn(),
  downloadCaseCalendarIcs: vi.fn(),
  downloadTaskPlanningReport: vi.fn(),
  getCaseCalendarEvents: vi.fn(),
  getCaseCalendarSummary: vi.fn(),
  getTaskJournalItem: vi.fn(),
  getTaskBillingContext: vi.fn(),
  getTaskBillingInvoices: vi.fn(),
  updateTaskBillingClassification: vi.fn(),
  linkTaskBillingInvoice: vi.fn(),
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
  getTaskFileUploadStates: vi.fn(),
  getTaskFileUploadPage: vi.fn(),
  getTaskFileUploadKey: vi.fn(),
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
  revokeOfflineCalendarSnapshot: vi.fn(),
  restoreOfflineCalendarAccess: vi.fn(),
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

const authState = vi.hoisted((): { user: { id: number; fullName: string; email: string; role?: { id: number }; departmentId?: number | null; department?: string | null }; isAdmin: boolean } => ({
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

vi.mock('../../services/offlineCalendarStore', () => ({ ...offlineCalendarMock, OFFLINE_CALENDAR_DENIED_KEY_PREFIX: 'payplanner-offline-calendar-denied:', OFFLINE_CALENDAR_OWNER_KEY: 'payplanner-offline-calendar-owner' }));

vi.mock('../Messenger/InternalMessenger', () => ({
  TaskChatPanel: ({ taskId }: { taskId: number }) => <div data-testid="task-chat-panel">Чат задачи {taskId}</div>,
}));

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
    const participantRoleMatches = typeof params.participantRole !== 'string'
      || task.participants?.some((participant) => participant.role === params.participantRole && participant.userId === 7);
    const searchMatches = !search || [task.objectName, task.clientName, task.legalEntityName, task.clientCaseTitle, task.title]
      .some((value) => value?.toLocaleLowerCase('ru').includes(search));
    const billingMatches = typeof params.billingStatus !== 'string'
      || (task.billingStatus === params.billingStatus
        && (params.billingStatus === 'unbilled' ? task.status !== 'cancelled' : active));
    const untriagedMatches = params.untriaged !== true
      || (task.clientId == null && task.legalEntityId == null
        && task.assigneeUserId == null && !task.assigneeName?.trim());

    return statusMatches
      && metricMatches
      && untriagedMatches
      && assigneeMatches
      && participantRoleMatches
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
    mode?: 'journal' | 'approvals' | 'untriaged';
    assignees?: TaskJournalAssignee[];
    withRoute?: boolean;
    withRoleRerender?: boolean;
    unmountOnTaskClose?: boolean;
    nextRouteTaskId?: number;
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
    settingsDelay?: Promise<void>;
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
    const billingFiltered = filterTaskJournalMockItems(tasks, { ...params, status: 'all' });
    const active = filtered.filter((task) => task.status !== 'done' && task.status !== 'cancelled');
    const unbilled = billingFiltered.filter((task) => task.status !== 'cancelled' && task.billingStatus === 'unbilled');
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
  apiMock.getTaskJournalSettings.mockImplementation(async () => {
    await options.settingsDelay;
    return {
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
    };
  });
  const currentUserId = authState.user.id;
  const filterStorageKey = options.mode && options.mode !== 'journal'
    ? `pp.task_journal.filters.v1.${options.mode}.user:${currentUserId}`
    : `pp.task_journal.filters.v1.user:${currentUserId}`;
  if (!window.localStorage.getItem(filterStorageKey)) {
    window.localStorage.setItem(filterStorageKey, JSON.stringify({
      quickFilter: options.mode === 'untriaged' ? 'untriaged' : 'all',
      viewMode: options.initialView ?? 'list',
      periodMode: options.periodMode ?? 'all',
      statusFilter: 'active',
    }));
  }

  function RoutedJournal() {
    const [routeTaskId, setRouteTaskId] = useState<number | null>(null);
    const [returned, setReturned] = useState(false);
    const [, setRoleRevision] = useState(0);
    if (returned) return <div data-testid="returned-to-case">Возврат в дело</div>;
    return (<>
      {options.withRoleRerender ? <button onClick={() => setRoleRevision((value) => value + 1)}>Обновить роль для теста</button> : null}
      {options.nextRouteTaskId ? <button onClick={() => setRouteTaskId(options.nextRouteTaskId!)}>Перейти в другую задачу</button> : null}
      <TaskJournal
        initialView={options.initialView}
        mode={options.mode}
        embedded={options.embedded}
        contextFilter={options.contextFilter}
        routeTaskId={routeTaskId}
        onRouteTaskChange={(id) => { setRouteTaskId(id); if (id === null && routeTaskId !== null && options.unmountOnTaskClose) setReturned(true); }}
        onOpenClient={options.onOpenClient}
        onOpenCase={options.onOpenCase}
        onOpenLegalEntity={options.onOpenLegalEntity}
        onCreateInvoiceDraft={options.onCreateInvoiceDraft}
        underControlOnly={options.underControlOnly}
        overdueFilterSignal={options.overdueFilterSignal}
      />
    </>);
  }

  const result = render(options.withRoute ? (
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
  return result;
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

export function setupTaskJournalTests() {
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
    offlineCalendarMock.revokeOfflineCalendarSnapshot.mockResolvedValue(undefined);
    offlineCalendarMock.restoreOfflineCalendarAccess.mockResolvedValue(true);
    offlineCalendarMock.saveOfflineCalendarSnapshot.mockResolvedValue(null);
    offlineCalendarMock.setOfflineCalendarTrustedDevice.mockResolvedValue(undefined);
    apiMock.getTaskBillingContext.mockResolvedValue({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      contracts: [{ id: 501, number: '15/26', title: 'Юридические услуги', billingMode: 'Actual', status: 'Concluded' }],
    });
    apiMock.getTaskBillingInvoices.mockResolvedValue([]);
    apiMock.updateTaskBillingClassification.mockResolvedValue(createTask({ clientBillingClassification: 'internal', billingStatus: 'internal' }));
    apiMock.linkTaskBillingInvoice.mockResolvedValue(createTask({ billingStatus: 'invoiced', billingInvoiceId: 901, billingInvoiceNumber: 'INV-901' }));
    apiMock.getTaskJournalItem.mockReset();
    apiMock.getTaskJournalItem.mockResolvedValue(createTask());
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 0,
      readyCount: 0,
      waitingCount: 0,
      pausedCount: 0,
    });
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
    apiMock.getTaskFileUploadStates.mockResolvedValue([]);
    apiMock.getTaskFileUploadPage.mockImplementation(async () => ({ items: await apiMock.getTaskFileUploadStates(), nextCursor: null }));
    apiMock.getTaskFileUploadKey.mockReturnValue(undefined);
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
}

export { apiMock, toastMock, dialogMock, offlineCalendarMock, clientsState, casesState, legalEntitiesState, authState, permissionsState, todayAt, toDatetimeLocalInputValue, dateKey, dateAt, getSameWeekDateOutsideTodayForTest, createDataTransfer, createTask, filterTaskJournalMockItems, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal, continueTaskWizardForClient };
