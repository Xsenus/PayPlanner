import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { TaskTemplatesPanel } from './TaskTemplatesPanel';
import type { TaskJournalAssignee, TaskJournalTemplate } from '../../utils/taskJournalStore';

const apiMock = vi.hoisted(() => ({
  getTaskJournalTemplates: vi.fn(),
  createTaskJournalTemplate: vi.fn(),
  updateTaskJournalTemplate: vi.fn(),
  deleteTaskJournalTemplate: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

const assignees: TaskJournalAssignee[] = [
  { id: 11, name: 'Юрист', email: 'lawyer@example.com' },
  { id: 12, name: 'Согласующий', email: 'approver@example.com' },
  { id: 13, name: 'Наблюдатель', email: 'observer@example.com' },
];

function createTemplate(overrides: Partial<TaskJournalTemplate> = {}): TaskJournalTemplate {
  return {
    id: 31,
    name: 'Проверка дела',
    description: 'Базовый шаблон проверки материалов.',
    title: 'Проверить материалы дела',
    taskDescription: 'Сверить документы и подготовить следующий шаг.',
    type: 'document',
    priority: 'high',
    plannedDurationMinutes: 90,
    dueOffsetMinutes: 180,
    assigneeUserId: 11,
    assigneeName: 'Юрист',
    bindingType: 'caseStage',
    bindingKey: 'appeal',
    isActive: true,
    reminders: ['before1h', 'atDue'],
    channels: ['inApp', 'email'],
    tags: ['дело', 'документы'],
    participants: [
      { userId: 12, role: 'approver', displayName: 'Согласующий', sortOrder: 1 },
      { userId: 13, role: 'observer', displayName: 'Наблюдатель', sortOrder: 2 },
    ],
    createdByUserId: 7,
    createdByName: 'Администратор',
    createdAt: '2026-07-09T10:00:00.000Z',
    updatedAt: '2026-07-09T10:00:00.000Z',
    ...overrides,
  };
}

function renderPanel(onUseTemplate = vi.fn()) {
  render(
    <TaskTemplatesPanel
      assignees={assignees}
      canCreate
      canEdit
      canDelete
      onUseTemplate={onUseTemplate}
    />,
  );
  return onUseTemplate;
}

describe('TaskTemplatesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getTaskJournalTemplates.mockResolvedValue([createTemplate()]);
    apiMock.createTaskJournalTemplate.mockImplementation(async (payload) => createTemplate({
      id: 40,
      name: payload.name,
      title: payload.title,
      taskDescription: payload.taskDescription,
      participants: [
        { userId: 12, role: 'approver', displayName: 'Согласующий', sortOrder: 1 },
      ],
    }));
    apiMock.updateTaskJournalTemplate.mockImplementation(async (id, payload) => createTemplate({
      id,
      name: payload.name,
      title: payload.title,
      taskDescription: payload.taskDescription,
      priority: payload.priority,
      participants: [
        { userId: 12, role: 'approver', displayName: 'Согласующий', sortOrder: 1 },
        { userId: 13, role: 'observer', displayName: 'Наблюдатель', sortOrder: 2 },
      ],
    }));
    apiMock.deleteTaskJournalTemplate.mockResolvedValue(undefined);
    dialogMock.confirm.mockResolvedValue(true);
  });

  afterEach(() => {
    cleanup();
  });

  it('loads saved task templates and applies one to a new task', async () => {
    const onUseTemplate = renderPanel();

    expect(await screen.findByText('Проверка дела')).toBeTruthy();
    expect(screen.getByText('Проверить материалы дела')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Применить/u }));

    expect(onUseTemplate).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Проверить материалы дела',
      description: 'Сверить документы и подготовить следующий шаг.',
      type: 'document',
      priority: 'high',
      plannedDurationMinutes: 90,
      assigneeUserId: 11,
      assigneeName: 'Юрист',
      tags: ['дело', 'документы'],
      reminders: ['before1h', 'atDue'],
      channels: ['inApp', 'email'],
    }));
    expect(onUseTemplate.mock.calls[0][0].participants).toEqual([
      expect.objectContaining({ userId: 12, role: 'approver', displayName: 'Согласующий' }),
      expect.objectContaining({ userId: 13, role: 'observer', displayName: 'Наблюдатель' }),
    ]);
    expect(onUseTemplate.mock.calls[0][0].dueAt).toEqual(expect.any(String));
  });

  it('creates and edits a template with visible selected participants', async () => {
    renderPanel();

    expect(await screen.findByText('Проверка дела')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Новый шаблон/u }));

    const form = screen.getByTestId('task-template-form');
    fireEvent.change(within(form).getByLabelText('Название шаблона'), { target: { value: 'Сбор документов' } });
    fireEvent.change(within(form).getByLabelText('Наименование задачи'), { target: { value: 'Собрать документы' } });
    fireEvent.change(within(form).getByLabelText('Описание задачи'), { target: { value: 'Запросить и проверить комплект документов.' } });
    fireEvent.change(within(form).getByLabelText('Исполнитель'), { target: { value: '11' } });
    fireEvent.change(within(form).getByLabelText('Добавить согласующего в шаблон'), { target: { value: '12' } });

    expect(within(form).getByText('Согласующий: Согласующий')).toBeTruthy();

    fireEvent.click(within(form).getByRole('button', { name: /Сохранить шаблон/u }));

    await waitFor(() => expect(apiMock.createTaskJournalTemplate).toHaveBeenCalledWith(expect.objectContaining({
      name: 'Сбор документов',
      title: 'Собрать документы',
      taskDescription: 'Запросить и проверить комплект документов.',
      assigneeUserId: 11,
      assigneeName: 'Юрист',
      participants: [expect.objectContaining({ userId: 12, role: 'approver' })],
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Шаблон задачи создан.', 'Шаблоны задач');

    fireEvent.click(screen.getAllByRole('button', { name: /Изменить/u })[0]);
    const editForm = screen.getByTestId('task-template-form');
    fireEvent.change(within(editForm).getByLabelText('Наименование задачи'), { target: { value: 'Проверить документы повторно' } });
    fireEvent.change(within(editForm).getByLabelText('Приоритет'), { target: { value: 'urgent' } });
    fireEvent.change(within(editForm).getByLabelText('Добавить наблюдателя в шаблон'), { target: { value: '13' } });

    expect(within(editForm).getByText('Наблюдатель: Наблюдатель')).toBeTruthy();

    fireEvent.click(within(editForm).getByRole('button', { name: /Сохранить шаблон/u }));

    await waitFor(() => expect(apiMock.updateTaskJournalTemplate).toHaveBeenCalledWith(31, expect.objectContaining({
      title: 'Проверить документы повторно',
      priority: 'urgent',
      participants: expect.arrayContaining([
        expect.objectContaining({ userId: 12, role: 'approver' }),
        expect.objectContaining({ userId: 13, role: 'observer' }),
      ]),
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Шаблон задачи обновлен.', 'Шаблоны задач');
  });
});
