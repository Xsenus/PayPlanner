import { setupTaskJournalTests, apiMock, dialogMock, casesState, legalEntitiesState, todayAt, toDatetimeLocalInputValue, dateKey, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal, continueTaskWizardForClient } from './TaskJournal.test-support';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ClientCase, LegalEntitySummary } from '../../types';
import { fromTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { type TaskJournalAssignee } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();


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

  it.each(['legacy', 'modern'] as const)('opens the weekly schedule, blocks overlaps and applies a free slot in the %s card', async (taskCardWindowMode) => {
    const busySlot = new Date();
    const daysUntilTuesday = (2 - busySlot.getDay() + 7) % 7 || 7;
    busySlot.setDate(busySlot.getDate() + daysUntilTuesday);
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
    ], { waitForTitle: null, settings: { taskCardWindowMode } });

    await openNewTaskFormAsGlobalInternal();

    fireEvent.change(screen.getByTestId('task-due-at-input'), {
      target: { value: toDatetimeLocalInputValue(selectedSlot) },
    });
    fireEvent.click(screen.getByTestId(taskCardWindowMode === 'modern' ? 'task-week-schedule-toggle' : 'task-day-schedule-toggle'));

    const dialog = screen.getByTestId('task-week-schedule-dialog');
    expect(await within(dialog).findByText('Busy slot')).toBeTruthy();
    expect(within(dialog).queryByText('Other assignee same day task')).toBeNull();
    expect(within(dialog).getByText('Other day task')).toBeTruthy();
    expect(within(dialog).getByText(/Найдено пересечений: 1/)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);

    const selectedDateKey = dateKey(selectedSlot);
    fireEvent.click(within(dialog).getByTestId(`task-week-schedule-day-${selectedDateKey}`), { clientY: 64 });
    expect(within(dialog).getByText('Время свободно')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: '− 15 мин' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Применить расписание' }));

    const dueInput = screen.getByTestId('task-due-at-input') as HTMLInputElement;
    expect(dueInput.value).toContain('T09:00');
  });

  it.each(['legacy', 'modern'] as const)('loads busy slots excluded by the journal filters in the %s card', async (taskCardWindowMode) => {
    const busySlot = new Date();
    busySlot.setHours(14, 0, 0, 0);
    await renderJournal([
      createTask({ title: 'Скрытая фильтром занятость', dueAt: busySlot.toISOString(), isUnderControl: false }),
    ], { underControlOnly: true, waitForTitle: null, settings: { taskCardWindowMode } });
    expect(screen.queryByText('Скрытая фильтром занятость')).toBeNull();
    await openNewTaskFormAsGlobalInternal();
    fireEvent.change(screen.getByTestId('task-due-at-input'), { target: { value: toDatetimeLocalInputValue(busySlot) } });
    fireEvent.click(screen.getByTestId(taskCardWindowMode === 'modern' ? 'task-week-schedule-toggle' : 'task-day-schedule-toggle'));
    const dialog = screen.getByTestId('task-week-schedule-dialog');
    expect(await within(dialog).findByText('Скрытая фильтром занятость')).toBeTruthy();
    expect(within(dialog).getByText('Найдено пересечений: 1')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);
    const scheduleRequest = apiMock.getTaskJournalItems.mock.calls.find(([params]) => params.assigneeUserId === 7 && params.from && params.to);
    expect(scheduleRequest?.[0]).toMatchObject({ status: 'active', assigneeUserId: 7, page: 1, pageSize: 200 });
    expect(scheduleRequest?.[0].underControl).toBeUndefined();
  });

  it.each(['legacy', 'modern'] as const)('detects a week-boundary overlap in the profile time zone in the %s card', async (taskCardWindowMode) => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-09-28T08:00:00.000Z'));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId' ? { key, value: 'Europe/Samara' } : null
    ));
    await renderJournal([
      createTask({ title: 'Занято в понедельник', dueAt: '2026-09-27T20:00:00.000Z' }),
    ], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    fireEvent.change(screen.getByTestId('task-due-at-input'), { target: { value: '2026-09-27T20:00' } });
    const durationSelect = screen.getByTestId(`task-${taskCardWindowMode}-card-window`).querySelector('option[value="480"]')?.closest('select');
    expect(durationSelect).not.toBeNull();
    fireEvent.change(durationSelect!, { target: { value: '480' } });
    fireEvent.click(screen.getByTestId(taskCardWindowMode === 'modern' ? 'task-week-schedule-toggle' : 'task-day-schedule-toggle'));
    const dialog = screen.getByTestId('task-week-schedule-dialog');
    expect(await within(dialog).findByText('Найдено пересечений: 1')).toBeTruthy();
    expect(within(dialog).getByRole('alert').textContent).toContain('Занято в понедельник');
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);
    const scheduleRequests = apiMock.getTaskJournalItems.mock.calls.filter(([params]) => params.assigneeUserId === 7 && params.from && params.to);
    expect(scheduleRequests).toHaveLength(1);
    expect(scheduleRequests[0][0]).toMatchObject({
      from: '2026-09-20T12:00:00.000Z',
      to: '2026-09-28T03:59:59.999Z',
    });
  });

  it.each(['legacy', 'modern'] as const)('shows a 15-minute schedule adjustment in the %s card duration field', async (taskCardWindowMode) => {
    await renderJournal([createTask({ plannedDurationMinutes: 60 })], {
      settings: { taskCardWindowMode },
    });
    await openFirstTaskForEdit();
    const card = screen.getByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByTestId(taskCardWindowMode === 'modern'
      ? 'task-week-schedule-toggle'
      : 'task-day-schedule-toggle'));
    const dialog = screen.getByTestId('task-week-schedule-dialog');
    expect(await within(dialog).findByText('Время свободно')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: '+ 15 мин' }));
    expect(within(dialog).getByText('1 ч 15 мин', { exact: false })).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Применить расписание' }));
    const durationSelect = card.querySelector('option[value="75"]')?.closest('select');
    expect(durationSelect).not.toBeNull();
    expect(durationSelect?.value).toBe('75');
    expect(durationSelect?.selectedOptions[0]?.textContent).toBe('1 ч 15 мин');
  });

  it('does not claim a free slot while loading or after a schedule error, and allows retry', async () => {
    await renderJournal([], { waitForTitle: null });
    await openNewTaskFormAsGlobalInternal();
    let rejectSchedule: (reason: Error) => void = () => undefined;
    apiMock.getTaskJournalItems.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectSchedule = reject; }));
    fireEvent.click(screen.getByTestId('task-day-schedule-toggle'));
    const dialog = screen.getByTestId('task-week-schedule-dialog');
    expect(within(dialog).getByText('Загрузка занятости…')).toBeTruthy();
    expect(within(dialog).queryByText('Время свободно')).toBeNull();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);
    rejectSchedule(new Error('Не удалось загрузить занятость исполнителей.'));
    expect(await within(dialog).findByText('Занятость не проверена')).toBeTruthy();
    expect(within(dialog).queryByText('Время свободно')).toBeNull();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Повторить загрузку' }));
    expect(await within(dialog).findByText('Время свободно')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Следующая неделя' }));
    expect(await within(dialog).findByText('Выберите время на этой неделе')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Применить расписание' }) as HTMLButtonElement).disabled).toBe(true);
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
      recurrenceEnabled: false,
    }));
  });

  it.each(['legacy', 'modern'] as const)('configures and saves monthly recurrence in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask({ recurrenceTimeZoneId: 'Europe/Moscow' })], {
      settings: { taskCardWindowMode },
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') {
      fireEvent.click(within(card).getByRole('button', { name: 'Повторение задачи' }));
    }

    const editor = await within(card).findByTestId('task-recurrence-editor');
    fireEvent.click(within(editor).getByRole('checkbox', { name: 'Повторять задачу' }));
    fireEvent.change(within(editor).getByRole('spinbutton', { name: 'Число месяца повторения' }), { target: { value: '21' } });
    fireEvent.change(within(editor).getByLabelText('Время повторения'), { target: { value: '09:30' } });
    expect(within(editor).getByText(/Новая копия появится только после завершения текущей задачи/)).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      recurrenceEnabled: true,
      recurrenceFrequency: 'monthly',
      recurrenceInterval: 1,
      recurrenceDayOfMonth: 21,
      recurrenceTimeMinutes: 570,
      recurrenceSkipWeekends: true,
      recurrenceTimeZoneId: 'Europe/Moscow',
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

  it('keeps duplicate and cancel actions in the compact modern card menu', async () => {
    await renderJournal([createTask({ status: 'new' })], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    const card = await screen.findByTestId('task-modern-card-window');
    expect(within(card).queryByRole('button', { name: 'Дублировать задачу' })).toBeNull();
    fireEvent.click(within(card).getByRole('button', { name: 'Еще' }));

    const duplicateButton = screen.getByRole('button', { name: 'Дублировать задачу' });
    const cancelButton = screen.getByRole('button', { name: 'Отменить задачу' });
    expect(duplicateButton.getAttribute('title')).toContain('Создать отдельную копию');
    expect(cancelButton.getAttribute('title')).toContain('статусом «Отменена»');
    expect(within(duplicateButton).getByTestId('task-duplicate-icon')).toBeTruthy();
    expect(within(cancelButton).getByTestId('task-cancel-icon')).toBeTruthy();

    fireEvent.click(cancelButton);
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'cancelled',
    })));
  });
});
