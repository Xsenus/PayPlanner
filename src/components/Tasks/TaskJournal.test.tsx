import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TaskJournal } from './TaskJournal';
import type { Client, ClientCase } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';

const apiMock = vi.hoisted(() => ({
  getTaskJournalItems: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getTaskJournalItem: vi.fn(),
  createTaskJournalItem: vi.fn(),
  updateTaskJournalItem: vi.fn(),
  updateTaskJournalStatus: vi.fn(),
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

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, fullName: 'Администратор', email: 'admin@example.com' },
  }),
}));

function todayAt(hours: number, minutes = 0) {
  const value = new Date();
  value.setHours(hours, minutes, 0, 0);
  return value.toISOString();
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

async function renderJournal(tasks: TaskJournalItem[] = [createTask()]) {
  apiMock.getTaskJournalItems.mockResolvedValue({
    items: tasks,
    total: tasks.length,
    page: 1,
    pageSize: 200,
  });
  apiMock.getTaskJournalAssignees.mockResolvedValue([
    { id: 7, name: 'Администратор', email: 'admin@example.com' },
  ]);

  render(<TaskJournal />);
  await screen.findByText('Тестовая задача');
}

describe('TaskJournal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    clientsState.clients = [
      { id: 10, name: 'Иванов Иван Иванович', phone: '+7 900 000-00-00', email: 'client@example.com' } as Client,
    ];
    casesState.cases = [
      { id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
    ];
    apiMock.createTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalItem.mockResolvedValue(createTask());
    apiMock.updateTaskJournalStatus.mockResolvedValue(createTask({ status: 'done', result: 'Готово' }));
    apiMock.deleteTaskJournalItem.mockResolvedValue(undefined);
  });

  it('shows client, case and renamed task title field in the journal', async () => {
    await renderJournal();

    expect(screen.getByText('Клиент / дело')).toBeTruthy();
    expect(screen.getByText('Наименование задачи')).toBeTruthy();
    expect(screen.getByText('Иванов Иван Иванович')).toBeTruthy();
    expect(screen.getByText('БАНКРОТСТВО')).toBeTruthy();
  });

  it('does not save a new task without a required client', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    expect(screen.getByText('Клиент')).toBeTruthy();
    expect(screen.getAllByText('Наименование задачи').length).toBeGreaterThan(0);
    expect(screen.getByText('Дело')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Создать задачу' }));

    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
    expect(toastMock.error).toHaveBeenCalledWith('Укажите клиента задачи.', 'Журнал задач');
  });

  it('saves selected client and case from the task form', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Новая задача' });

    fireEvent.click(screen.getByRole('button', { name: 'Выберите клиента' }));
    fireEvent.click(await screen.findByRole('button', { name: /Иванов Иван Иванович/ }));
    fireEvent.change(screen.getByPlaceholderText('Что нужно сделать'), { target: { value: 'Подготовить документы' } });

    fireEvent.change(screen.getByRole('combobox', { name: 'Дело' }), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      clientCaseId: 100,
      title: 'Подготовить документы',
    }));
  });

  it('opens edit form instead of completing an active task without result', async () => {
    await renderJournal([createTask({ result: '' })]);

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(toastMock.error).toHaveBeenCalledWith('Перед завершением задачи заполните результат.', 'Журнал задач');
    expect(await screen.findByRole('heading', { name: 'Редактировать задачу' })).toBeTruthy();
    expect(screen.getAllByText('Результат').length).toBeGreaterThan(0);
  });

  it('renders task calendar as a journal subgroup', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Календарь/i }));

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    expect(screen.getByText('Загрузка за день')).toBeTruthy();
    expect(screen.getAllByText('Тестовая задача').length).toBeGreaterThan(0);
  });
});
