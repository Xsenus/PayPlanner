import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TaskJournal } from './TaskJournal';
import type { Client, ClientCase, LegalEntitySummary } from '../../types';
import type { TaskJournalAssignee, TaskJournalItem } from '../../utils/taskJournalStore';

const apiMock = vi.hoisted(() => ({
  getTaskJournalItems: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getTaskJournalSettings: vi.fn(),
  getUserSetting: vi.fn(),
  updateTaskJournalSettings: vi.fn(),
  getTaskJournalCaseAutomationCandidates: vi.fn(),
  createTaskJournalCaseAutomationTask: vi.fn(),
  createTaskJournalCaseAutomationTasks: vi.fn(),
  downloadTaskJournalCalendar: vi.fn(),
  getTaskJournalItem: vi.fn(),
  createTaskJournalItem: vi.fn(),
  updateTaskJournalItem: vi.fn(),
  updateTaskJournalStatus: vi.fn(),
  updateTaskJournalApproval: vi.fn(),
  getTaskJournalComments: vi.fn(),
  createTaskJournalComment: vi.fn(),
  createTaskJournalDeadlineChangeRequest: vi.fn(),
  getTaskJournalFiles: vi.fn(),
  uploadTaskJournalFile: vi.fn(),
  downloadTaskJournalFile: vi.fn(),
  deleteTaskJournalFile: vi.fn(),
  deleteTaskJournalItem: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
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

const authState = vi.hoisted((): { user: { id: number; fullName: string; email: string }; isAdmin: boolean } => ({
  user: { id: 7, fullName: 'Администратор', email: 'admin@example.com' },
  isAdmin: true,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
  }),
}));

vi.mock('../../hooks/useClients', () => ({
  useClients: () => ({
    clients: clientsState.clients,
    loading: false,
  }),
}));

vi.mock('../../hooks/useClientCases', () => ({
  useClientCases: () => ({
    cases: casesState.cases,
    loading: false,
  }),
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: () => ({
    legalEntities: legalEntitiesState.legalEntities,
    loading: false,
  }),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: authState.user,
    isAdmin: () => authState.isAdmin,
  }),
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

async function renderJournal(
  tasks: TaskJournalItem[] = [createTask()],
  options: {
    initialView?: 'list' | 'board' | 'calendar' | 'timeline';
    mode?: 'journal' | 'approvals';
    assignees?: TaskJournalAssignee[];
    withRoute?: boolean;
    waitForTitle?: string;
    onOpenClient?: (clientId: number, caseId?: number | null) => void;
    onOpenLegalEntity?: (legalEntityId: number) => void;
  } = {},
) {
  apiMock.getTaskJournalItems.mockResolvedValue({
    items: tasks,
    total: tasks.length,
    page: 1,
    pageSize: 200,
  });
  apiMock.getTaskJournalAssignees.mockResolvedValue(options.assignees ?? [
    { id: 7, name: 'Администратор', email: 'admin@example.com' },
  ]);
  apiMock.getTaskJournalSettings.mockResolvedValue({
    restrictExecutorEditing: true,
    allowExecutorDeadlineChangeRequests: true,
  });

  function RoutedJournal() {
    const [routeTaskId, setRouteTaskId] = useState<number | null>(null);
    return (
      <TaskJournal
        initialView={options.initialView}
        mode={options.mode}
        routeTaskId={routeTaskId}
        onRouteTaskChange={setRouteTaskId}
        onOpenClient={options.onOpenClient}
        onOpenLegalEntity={options.onOpenLegalEntity}
      />
    );
  }

  render(options.withRoute ? (
    <RoutedJournal />
  ) : (
    <TaskJournal
      initialView={options.initialView}
      mode={options.mode}
      onOpenClient={options.onOpenClient}
      onOpenLegalEntity={options.onOpenLegalEntity}
    />
  ));
  await screen.findAllByText(options.waitForTitle ?? tasks[0]?.title ?? 'Тестовая задача');
}

describe('TaskJournal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    authState.user = { id: 7, fullName: 'Администратор', email: 'admin@example.com' };
    authState.isAdmin = true;
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
    apiMock.getUserSetting.mockRejectedValue(new Error('not found'));
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
    apiMock.createTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalStatus.mockResolvedValue(createTask({ status: 'done', result: 'Готово' }));
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
    apiMock.deleteTaskJournalFile.mockResolvedValue(undefined);
    apiMock.deleteTaskJournalItem.mockResolvedValue(undefined);
  });

  it('shows client, case and renamed task title field in the journal', async () => {
    await renderJournal();

    expect(screen.getByText('Контрагент / дело')).toBeTruthy();
    expect(screen.getByText('Наименование задачи')).toBeTruthy();
    expect(screen.getByText('Иванов Иван Иванович')).toBeTruthy();
    expect(screen.getByText('БАНКРОТСТВО')).toBeTruthy();
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
    expect(screen.getByText(approvalTask.title)).toBeTruthy();
    expect(screen.queryByText(laterApprovalTask.title)).toBeNull();
    expect(screen.queryByText(regularTask.title)).toBeNull();
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

  it('opens with current user quick filter and remembers switching to all tasks', async () => {
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
    expect(screen.queryByText('Чужая задача')).toBeNull();

    fireEvent.click(screen.getByTestId('task-quick-filter-all'));

    expect(await screen.findByText('Чужая задача')).toBeTruthy();
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        quickFilter: 'all',
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
    const rowButtons = within(row as HTMLTableRowElement).getAllByRole('button');
    fireEvent.click(rowButtons[1]);

    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    expect(searchInput.value).toBe('EGOROV');
    expect(screen.getByText('Egor task')).toBeTruthy();
    expect(screen.queryByText('Other task')).toBeNull();

    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        search: 'EGOROV',
      });
    });
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
    ]);

    expect(screen.getByText('May task')).toBeTruthy();
    expect(screen.getByText('June task')).toBeTruthy();

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

  it('keeps date filters in a separate responsive row from view controls', async () => {
    await renderJournal();

    const mainFilterRow = screen.getByTestId('task-main-filter-row');
    const dateAndViewRow = screen.getByTestId('task-date-and-view-row');
    const dateFilterRow = screen.getByTestId('task-date-filter-row');
    const viewControls = screen.getByTestId('task-view-controls');
    const dateFrom = screen.getByTestId('task-date-from-filter');
    const dateTo = screen.getByTestId('task-date-to-filter');

    expect(mainFilterRow.contains(dateFrom)).toBe(false);
    expect(dateFilterRow.contains(dateFrom)).toBe(true);
    expect(dateFilterRow.contains(dateTo)).toBe(true);
    expect(dateAndViewRow.contains(viewControls)).toBe(true);
    expect(dateFilterRow.contains(viewControls)).toBe(false);
    expect(dateAndViewRow.className).toContain('flex-col');
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
    const overdue = new Date();
    overdue.setDate(overdue.getDate() - 1);
    overdue.setHours(10, 0, 0, 0);

    const future = new Date();
    future.setDate(future.getDate() + 2);
    future.setHours(10, 0, 0, 0);

    await renderJournal([
      createTask({ dueAt: overdue.toISOString() }),
      createTask({
        id: 102,
        title: 'Будущая задача',
        dueAt: future.toISOString(),
        assigneeUserId: 7,
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ]);

    expect(screen.getByText('Будущая задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Просрочены/i }));

    expect(screen.getByText('Тестовая задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Будущая задача')).toBeNull());
  });

  it('does not save a new task without a required counterparty', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    expect(screen.getByText('Контрагент')).toBeTruthy();
    expect(screen.getAllByText('Наименование задачи').length).toBeGreaterThan(0);
    expect(screen.getByText('Дело')).toBeTruthy();

    const submitButtons = screen.getAllByRole('button', { name: 'Создать задачу' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
    expect(toastMock.error).toHaveBeenCalledWith('Укажите контрагента задачи.', 'Журнал задач');
  });

  it('creates an internal task without a counterparty', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByTestId('task-internal-checkbox'));
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
    ], { waitForTitle: 'Busy slot' });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.change(screen.getByTestId('task-due-at-input'), {
      target: { value: toDatetimeLocalInputValue(selectedSlot) },
    });
    fireEvent.click(screen.getByTestId('task-day-schedule-toggle'));

    const panel = screen.getByTestId('task-day-schedule-panel');
    expect(within(panel).getByText('Busy slot')).toBeTruthy();
    expect(within(panel).queryByText('Other day task')).toBeNull();
    expect(within(panel).getByText(/Пересечения: 1/)).toBeTruthy();
  });

  it('stacks description and result fields vertically in the task form', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

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
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите контрагента' }));
    fireEvent.click(await screen.findByRole('button', { name: /Иванов Иван Иванович/ }));
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Подготовить документы' } });

    fireEvent.change(screen.getByRole('combobox', { name: 'Дело' }), { target: { value: '100' } });
    const submitButtons = screen.getAllByRole('button', { name: 'Создать задачу' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      clientCaseId: 100,
      title: 'Подготовить документы',
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

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите контрагента' }));
    fireEvent.click(await screen.findByRole('button', { name: /Иванов Иван Иванович/ }));
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
      { id: 103, clientId: 10, title: 'Закрытое импортом', status: 'Закрытое', createdAt: todayAt(8) } as ClientCase,
      { id: 104, clientId: 10, title: 'Завершенное дело', status: 'Завершено', createdAt: todayAt(8) } as ClientCase,
      { id: 105, clientId: 10, title: 'Архивное дело', status: 'Archived', createdAt: todayAt(8) } as ClientCase,
      { id: 106, clientId: 10, title: 'Числовой закрытый статус', status: '2', createdAt: todayAt(8) } as ClientCase,
    ];

    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите контрагента' }));
    fireEvent.click(await screen.findByRole('button', { name: /Иванов Иван Иванович/ }));

    const caseSelect = screen.getByRole('combobox', { name: 'Дело' });
    const options = Array.from(caseSelect.querySelectorAll('option')).map((option) => option.textContent);

    expect(options).toContain('Открытое дело');
    expect(options).toContain('Приостановленное дело');
    expect(options).not.toContain('Закрытое дело');
    expect(options).not.toContain('Закрытое импортом');
    expect(options).not.toContain('Завершенное дело');
    expect(options).not.toContain('Архивное дело');
    expect(options).not.toContain('Числовой закрытый статус');
  });

  it('saves selected legal entity and legal entity case from the task form', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите контрагента' }));
    fireEvent.click(await screen.findByRole('button', { name: /ООО Ромашка/ }));
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Проверить документы юрлица' } });

    fireEvent.change(screen.getByRole('combobox', { name: 'Дело' }), { target: { value: '200' } });
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
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите контрагента' }));
    const searchInput = screen.getByPlaceholderText('Поиск по ФИО, названию, ИНН, телефону или email');

    fireEvent.change(searchInput, { target: { value: '5400000000' } });

    expect(await screen.findByRole('button', { name: /ООО Ромашка/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /АО Василек/ })).toBeNull();

    fireEvent.change(searchInput, { target: { value: 'ooo' } });

    expect(await screen.findByRole('button', { name: /ООО Ромашка/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /АО Василек/ })).toBeNull();
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
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
        suggestedDueAt: todayAt(9),
      },
    ]);
    await renderJournal();

    expect(screen.getByText('Автоконтроль дел')).toBeTruthy();
    fireEvent.click(screen.getByTestId('task-automation-create-100'));

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTask).toHaveBeenCalledWith(100));
    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith('Контрольная задача создана.', 'Автоконтроль дел');
    expect(await screen.findByRole('heading', { name: 'Редактировать задачу' })).toBeTruthy();
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

    fireEvent.click(screen.getByRole('button', { name: 'Создать все' }));

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTasks).toHaveBeenCalledWith({ limit: 1 }));
    expect(toastMock.success).toHaveBeenCalledWith('Создано контрольных задач: 1.', 'Автоконтроль дел');
  });

  it('opens edit form instead of completing an active task without result', async () => {
    await renderJournal([createTask({ result: '' })]);

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(toastMock.error).toHaveBeenCalledWith('Перед завершением задачи заполните результат.', 'Журнал задач');
    expect(await screen.findByRole('heading', { name: 'Редактировать задачу' })).toBeTruthy();
    expect(screen.getAllByText('Результат').length).toBeGreaterThan(0);
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
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Согласующий', email: 'approver@example.com' },
      ],
    });

    fireEvent.click(screen.getByTitle('Редактировать'));
    expect(await screen.findByRole('heading', { name: 'Редактировать задачу' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'На согласование' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'approver', userId: 8, approvalStatus: 'pending' }),
      ]),
    }));
  });

  it('adds an observer from the task actions menu', async () => {
    await renderJournal([createTask()], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Наблюдатель', email: 'observer@example.com' },
      ],
    });

    fireEvent.click(screen.getByTitle('Редактировать'));
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

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

    fireEvent.click(screen.getByTitle('Редактировать'));
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    const extraExecutorSelect = screen.getByLabelText('Добавить: Дополнительные исполнители');
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
    expect(payload.participants.filter((participant) => participant.role === 'assignee' && participant.userId === 7)).toHaveLength(1);
  });

  it('sends a change comment with task edits', async () => {
    await renderJournal();

    fireEvent.click(screen.getByTitle('Редактировать'));
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

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

    fireEvent.click(screen.getByTitle('Редактировать'));
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    fireEvent.click(screen.getByRole('button', { name: 'Действия с задачей' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дублировать' }));

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Тестовая задача (копия)',
      status: 'new',
      result: '',
    }));
  });

  it('opens executor task in view mode and saves only result', async () => {
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

    const titleInput = screen.getByDisplayValue('Тестовая задача') as HTMLInputElement;
    expect(titleInput.disabled).toBe(true);

    const resultInput = screen.getByPlaceholderText('Итог выполнения или причина ожидания') as HTMLTextAreaElement;
    expect(resultInput.disabled).toBe(false);
    fireEvent.change(resultInput, { target: { value: 'Результат исполнителя' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить результат' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'new', 'Результат исполнителя'));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('allows executor to request deadline change without moving task directly', async () => {
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
    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    fireEvent.change(screen.getByLabelText('Новый срок для запроса переноса'), { target: { value: '2026-05-15T10:30' } });
    fireEvent.change(screen.getByLabelText('Причина переноса срока'), { target: { value: 'Нужны документы от клиента' } });
    fireEvent.click(screen.getByRole('button', { name: 'Запросить перенос' }));

    await waitFor(() => expect(apiMock.createTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(101, {
      requestedDueAt: expect.any(String),
      reason: 'Нужны документы от клиента',
    }));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('renders task calendar as a journal subgroup', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Календарь/i }));

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    expect(screen.getByText('Загрузка за день')).toBeTruthy();
    expect(screen.getAllByText('Тестовая задача').length).toBeGreaterThan(0);
  });

  it('can open directly in calendar mode from the journal submenu', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    expect(screen.getByText('Загрузка за день')).toBeTruthy();
  });

  it('opens quick create form from a calendar date', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getAllByTitle('Создать задачу на дату')[0]);

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

  it('updates task duration from the calendar load panel', async () => {
    await renderJournal([createTask({ plannedDurationMinutes: 60 })], { initialView: 'calendar' });

    fireEvent.click(screen.getByRole('button', { name: '+15 мин' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      plannedDurationMinutes: 75,
    }));
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
    expect(screen.getByTestId('task-timeline-task-101')).toBeTruthy();
    expect(screen.getByTestId('task-timeline-task-102')).toBeTruthy();
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

    fireEvent.click(screen.getAllByTitle('Редактировать')[0]);
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    expect(await screen.findByText('Задача создана.')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Введите комментарий'), { target: { value: 'Комментарий по задаче' } });
    fireEvent.click(screen.getByRole('button', { name: /Добавить комментарий/i }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Комментарий по задаче'));
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
      },
    ]);

    await renderJournal();

    fireEvent.click(screen.getAllByTitle('Редактировать')[0]);
    await screen.findByRole('heading', { name: 'Редактировать задачу' });
    expect((await screen.findAllByText('document.pdf')).length).toBeGreaterThan(0);

    const fileInputs = document.querySelectorAll('input[type="file"]');
    fireEvent.change(fileInputs[1], {
      target: {
        files: [new File(['updated'], 'document-v2.pdf', { type: 'application/pdf' })],
      },
    });

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, expect.any(File), 'doc-1'));
  });
});
