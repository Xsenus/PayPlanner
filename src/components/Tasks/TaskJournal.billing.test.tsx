import { setupTaskJournalTests, apiMock, toastMock, dialogMock, authState, permissionsState, todayAt, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal } from './TaskJournal.test-support';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TaskJournal } from './TaskJournal';
import type { TaskJournalItem } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it.each(['legacy', 'modern'] as const)('changes the billing decision from the %s task card', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'unbilled' });
    await renderJournal([task], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Тип оплаты задачи/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Неоплачиваемая / внутренняя' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить тип оплаты' }));

    await waitFor(() => expect(apiMock.updateTaskBillingClassification).toHaveBeenCalledWith(task.id, 'internal', null, { expectedUpdatedAt: task.updatedAt ?? null }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Учёт задачи и счёт' })).toBeNull());
  });

  it.each((['legacy', 'modern'] as const).flatMap((cardMode) => (['Fixed', 'Actual'] as const).map((billingMode) => [cardMode, billingMode] as const)))('preserves the %s card contract snapshot for explicit future-only %s classification and retains an unsaved title', async (taskCardWindowMode, billingMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: billingMode === 'Fixed' ? 'fixed' : 'unbilled',
      billingContractId: 301, billingContractNumber: 'SYNTHETIC-301', billingMode, updatedAt: '2026-10-24T01:02:03.1234567Z' });
    apiMock.updateTaskBillingClassification.mockResolvedValueOnce({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal' });
    await renderJournal([task], { settings: { taskCardWindowMode, contractTaskBillingMode: 'allowFutureClassification' } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Несохранённое название договорной задачи' } });
    apiMock.getTaskBillingContext.mockClear();
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    expect(apiMock.getTaskBillingContext).not.toHaveBeenCalled();
    expect(within(dialog).queryByRole('combobox', { name: 'Договор для оплаты задачи' })).toBeNull();
    expect(within(dialog).getByText(/Договор №SYNTHETIC-301/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Тип оплаты задачи/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Неоплачиваемая / внутренняя' }));
    const save = within(dialog).getByRole('button', { name: 'Сохранить тип оплаты' });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    expect(apiMock.updateTaskBillingClassification).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Подтверждаю смену только будущего учёта/ }));
    fireEvent.click(save);
    await waitFor(() => expect(apiMock.updateTaskBillingClassification).toHaveBeenCalledExactlyOnceWith(task.id, 'internal', 301,
      { expectedUpdatedAt: '2026-10-24T01:02:03.1234567Z', allowContractClassification: true }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Учёт задачи и счёт' })).toBeNull());
    expect((within(card).getByTestId('task-title-input') as HTMLInputElement).value).toBe('Несохранённое название договорной задачи');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.linkTaskBillingInvoice).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('keeps a grandfathered contract intact when future-only editing is disabled in the %s card', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'internal', billingStatus: 'internal', billingContractId: 301, billingContractNumber: 'SYNTHETIC-301', billingMode: 'Fixed' });
    await renderJournal([task], { settings: { taskCardWindowMode, contractTaskBillingMode: 'retainContractTasks' } });
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    expect(within(dialog).getByText(/Изменение договорных задач запрещено настройкой/)).toBeTruthy();
    expect(within(dialog).queryByRole('checkbox', { name: /Подтверждаю смену только будущего учёта/ })).toBeNull();
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Тип оплаты задачи/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Платная' }));
    expect((within(dialog).getByRole('button', { name: 'Сохранить тип оплаты' }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiMock.updateTaskBillingClassification).not.toHaveBeenCalled();
    expect(within(dialog).getByText(/Договор №SYNTHETIC-301/)).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('rechecks admin policy before the %s card submits a contract decision', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'fixed', billingContractId: 301, billingMode: 'Fixed' });
    await renderJournal([task], { settings: { taskCardWindowMode, contractTaskBillingMode: 'allowFutureClassification' } });
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Тип оплаты задачи/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Неоплачиваемая / внутренняя' }));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Подтверждаю смену только будущего учёта/ }));
    apiMock.getTaskJournalSettings.mockResolvedValueOnce({ contractTaskBillingMode: 'retainContractTasks' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить тип оплаты' }));
    await within(dialog).findByText(/Изменение договорных задач отключено в настройках/);
    expect(apiMock.updateTaskBillingClassification).not.toHaveBeenCalled();
    expect(within(dialog).getByRole('checkbox', { name: /Подтверждаю смену только будущего учёта/ })).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('does not apply a pending contract decision to another actor in the %s card', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'fixed', billingContractId: 301, billingMode: 'Fixed' });
    const view = await renderJournal([task], { settings: { taskCardWindowMode, contractTaskBillingMode: 'allowFutureClassification' } });
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Тип оплаты задачи/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Неоплачиваемая / внутренняя' }));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Подтверждаю смену только будущего учёта/ }));
    let finish!: (value: TaskJournalItem) => void;
    apiMock.updateTaskBillingClassification.mockImplementationOnce(() => new Promise<TaskJournalItem>((resolve) => { finish = resolve; }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить тип оплаты' }));
    await waitFor(() => expect(apiMock.updateTaskBillingClassification).toHaveBeenCalledTimes(1));
    toastMock.success.mockClear();
    authState.user = { ...authState.user, id: 8 };
    view.rerender(<TaskJournal />);
    await act(async () => { finish({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal' }); });
    expect(toastMock.success).not.toHaveBeenCalledWith('Учёт задачи обновлён.', 'Журнал задач');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('links an earlier invoice from the %s task card only after confirmation', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'unbilled' });
    apiMock.getTaskBillingInvoices.mockResolvedValue([{ id: 901, number: 'INV-901', date: todayAt(9), amount: 4200, workflowStatus: 'Draft' }]);
    await renderJournal([task], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Учёт и счёт' }));
    const dialog = await screen.findByRole('dialog', { name: 'Учёт задачи и счёт' });
    await waitFor(() => expect(apiMock.getTaskBillingInvoices).toHaveBeenCalledWith(task.id));
    fireEvent.click(within(dialog).getByRole('combobox', { name: /^Ранее выставленный счёт/ }));
    fireEvent.click(screen.getByRole('option', { name: /INV-901/ }));
    const linkButton = within(dialog).getByRole('button', { name: 'Привязать счёт' });
    expect((linkButton as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Подтверждаю, что работа по задаче/ }));
    fireEvent.click(linkButton);

    await waitFor(() => expect(apiMock.linkTaskBillingInvoice).toHaveBeenCalledWith(task.id, 901, true));
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
    expect(screen.getByTestId('task-billing-create-wizard').classList.contains('pp-task-billing-wizard')).toBe(true);
    expect(screen.getByTestId('task-billing-context').textContent).toContain('Иванов Иван Иванович');
    expect(screen.getByTestId('task-billing-context').textContent).toContain('БАНКРОТСТВО');
    expect(screen.getByTestId('task-billing-notice').classList.contains('pp-task-billing-notice-special')).toBe(true);
    expect(screen.getByRole('button', { name: /Клиентская задача/ }).classList.contains('pp-task-billing-choice-special')).toBe(true);
    expect(screen.getByRole('button', { name: /^Внутренняя задача/ }).classList.contains('pp-task-billing-choice-neutral')).toBe(true);
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
    ['legacy', 'done', false], ['modern', 'done', false],
    ['legacy', 'onApproval', false], ['modern', 'onApproval', false],
    ['legacy', 'onApproval', true], ['modern', 'onApproval', true],
  ] as const)('retains the result after status-save failure in the %s card for %s (editable: %s) without uploading twice', async (taskCardWindowMode, targetStatus, editable) => {
    authState.isAdmin = editable;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([createTask({ assigneeUserId: 7, authorUserId: 8, result: '',
      participants: [{ userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' }] })],
      { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: targetStatus === 'done' ? [7] : [] } });
    if (editable) await openFirstTaskForEdit();
    else fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const actionName = targetStatus === 'done' ? 'Завершить задачу' : 'Отправить на согласование';
    fireEvent.click(within(card).getAllByRole('button', { name: targetStatus === 'done' ? 'Завершить' : actionName }).at(-1)!);
    await screen.findByRole('heading', { name: 'Добавить результат по задаче' });
    const file = new File(['Результат'], 'итог.txt', { type: 'text/plain' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), { target: { files: [file] } });
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Результат не должен исчезнуть.' } });
    const saveRequest = editable ? apiMock.updateTaskJournalItem : apiMock.updateTaskJournalStatus;
    saveRequest.mockRejectedValueOnce(new Error('Сохранение статуса недоступно.'));
    fireEvent.click(screen.getAllByRole('button', { name: actionName }).at(-1)!);
    expect(await screen.findByText(/Не удалось подтвердить сохранение результата задачи/)).toBeTruthy();
    expect(saveRequest).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect((document.getElementById('task-completion-result') as HTMLTextAreaElement).value).toBe('Результат не должен исчезнуть.');
    expect(screen.getByTitle('итог.txt')).toBeTruthy();
    const savedFile = await apiMock.uploadTaskJournalFile.mock.results.at(-1)!.value;
    apiMock.getTaskJournalFiles.mockResolvedValue([savedFile]);
    fireEvent.click(screen.getAllByRole('button', { name: actionName }).at(-1)!);
    await waitFor(() => expect(saveRequest).toHaveBeenCalledTimes(2));
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
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

  it('keeps payment analysis out of the ordinary task journal', async () => {
    await renderJournal([createTask({ id: 301, billingStatus: 'unbilled' })]);
    expect(screen.queryByTestId('task-unbilled-reminder')).toBeNull();
    expect(screen.getByTestId('task-view-controls')).toBeTruthy();
  });

  it('explains an empty accounting queue without inventing counts', async () => {
    await renderJournal([], { billingAnalysisMode: true, waitForTitle: null });
    expect(await screen.findByText('Нет задач для анализа оплаты по текущим фильтрам.')).toBeTruthy();
    expect(screen.queryByTestId('task-unbilled-reminder')).toBeNull();
  });

  it('keeps accounting queue filters separate from journal preferences', async () => {
    const key = 'pp.task_journal.filters.v1.user:7';
    const original = JSON.stringify({ quickFilter: 'all', viewMode: 'list', periodMode: 'all', search: 'личный поиск' });
    window.localStorage.setItem(key, original);
    await renderJournal([createTask({ id: 301, billingStatus: 'unbilled' })], { billingAnalysisMode: true });
    fireEvent.click(within(screen.getByTestId('task-unbilled-reminder')).getByRole('button', { name: '1 задача без счёта у 1 клиента' }));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ billingStatus: 'unbilled' }), expect.anything()));
    expect(window.localStorage.getItem(key)).toBe(original);
    expect(window.localStorage.getItem('pp.task_journal.filters.v1.billing.user:7')).toBeTruthy();
  });

  it('shows unbilled work and distinct clients in one filter', async () => {
    await renderJournal([
      createTask({ id: 301, clientId: 10, billingStatus: 'unbilled' }),
      createTask({ id: 302, clientId: 10, billingStatus: 'unbilled' }),
      createTask({ id: 303, clientId: null, legalEntityId: 20, billingStatus: 'unbilled' }),
      createTask({ id: 304, billingStatus: 'fixed' }),
      createTask({ id: 305, billingStatus: 'internal' }),
      createTask({ id: 306, billingStatus: 'invoiced' }),
      createTask({ id: 307, billingStatus: 'legacy' }),
    ], { billingAnalysisMode: true });

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    expect(within(analysis).getByText('3 задачи без счёта у 2 клиентов')).toBeTruthy();
    expect(within(analysis).getByText('По фиксированному договору: 1')).toBeTruthy();
    expect(within(analysis).getByText('Неоплачиваемые: 1')).toBeTruthy();
    expect(within(analysis).getByText('Уже включены в счёт: 1')).toBeTruthy();
    expect(within(analysis).getByText('Нужно распределить: 1')).toBeTruthy();
    fireEvent.click(within(analysis).getByRole('button', { name: '3 задачи без счёта у 2 клиентов' }));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ billingStatus: 'unbilled', sortBy: 'counterparty', sortDir: 'asc' }),
      expect.anything(),
    ));
    expect(within(analysis).getByText(/Категория открыта в таблице ниже/i)).toBeTruthy();
  });

  it('reviews existing invoice lines and links a confirmed group from the selected client', async () => {
    const first = createTask({ id: 341, title: 'Подготовка заявления', clientCaseTitle: 'Сверяемое дело', billingStatus: 'unbilled',
      updatedAt: '2026-10-24T01:02:03.1234567Z' });
    const second = createTask({ ...first, id: 342, title: 'Подача заявления', status: 'done', updatedAt: undefined });
    const other = createTask({ id: 343, title: 'Работа другого клиента', clientId: 11, billingStatus: 'unbilled' });
    const tasks = [first, second, other];
    const invoice = { id: 987, number: 'SYNTHETIC-987', date: '2026-10-30', amount: 1000, type: 'Income', status: 'Pending',
      clientId: first.clientId, clientCaseId: first.clientCaseId, billingReconciliationUpdatedAt: '2026-11-01T08:00:00Z',
      lineItems: [{ id: 1, name: 'Подготовка и подача заявления', quantity: 1, unit: 'шт' }] };
    apiMock.getInvoices.mockResolvedValue({ items: [invoice], total: 1 });
    apiMock.getInvoice.mockResolvedValue(invoice);
    apiMock.bulkLinkTaskBillingInvoice.mockImplementation(async () => {
      tasks[0] = { ...first, billingStatus: 'invoiced', billingInvoiceId: invoice.id };
      tasks[1] = { ...second, billingStatus: 'invoiced', billingInvoiceId: invoice.id };
      return tasks.slice(0, 2);
    });
    await renderJournal(tasks, { billingAnalysisMode: true });
    fireEvent.click(screen.getByRole('button', { name: '3 задачи без счёта у 2 клиентов' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выбрать задачу Подготовка заявления' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сверка по клиенту' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Сверка по клиенту' }));
    expect(await dialog.findByText('Подача заявления')).toBeTruthy();
    expect(dialog.queryByText(other.title)).toBeNull();
    expect(apiMock.getInvoices).toHaveBeenCalledWith(expect.objectContaining({ type: 'Income', clientRole: 'counterparty', clientId: first.clientId }), expect.anything());
    fireEvent.click(dialog.getByRole('button', { name: 'Выбрать все задачи: Сверяемое дело на странице' }));
    fireEvent.click(dialog.getByRole('combobox', { name: /^Счёт для сверки/ }));
    fireEvent.click(await screen.findByRole('option', { name: /SYNTHETIC-987/ }));
    expect(await dialog.findByText(/Подготовка и подача заявления/)).toBeTruthy();

    const linkButton = dialog.getByRole('button', { name: 'Привязать к счёту' });
    dialogMock.confirm.mockResolvedValueOnce(false);
    fireEvent.click(linkButton);
    await waitFor(() => expect((linkButton as HTMLButtonElement).disabled).toBe(false));
    expect(apiMock.bulkLinkTaskBillingInvoice).not.toHaveBeenCalled();
    fireEvent.click(linkButton);
    expect(await dialog.findByText('К счёту №SYNTHETIC-987 привязано задач: 2.')).toBeTruthy();
    expect(apiMock.bulkLinkTaskBillingInvoice).toHaveBeenCalledExactlyOnceWith(987, invoice.billingReconciliationUpdatedAt, {
      341: first.updatedAt, 342: null,
    });
    await waitFor(() => expect(dialog.queryByText(first.title)).toBeNull());
  });

  it('prepares one invoice draft from selected unbilled client tasks', async () => {
    const onCreateInvoiceDraft = vi.fn();
    const unbilledTask = createTask({
      id: 321,
      title: 'Подготовить иск',
      status: 'done',
      result: 'Иск подготовлен\nи направлен клиенту',
      createdAt: '2026-09-10T09:00:00Z',
      updatedAt: '2026-09-20T14:30:00Z',
      clientBillingClassification: 'billable',
      billingContractId: 501,
      billingContractNumber: '15/26',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });
    await renderJournal([unbilledTask], {
      billingAnalysisMode: true,
      onCreateInvoiceDraft,
      waitForTitle: null,
      settings: {
        financialDocumentsResponsibleUserId: 7,
        financialDocumentsResponsibleUserName: 'Администратор',
      },
    });

    expect(screen.getByTestId('task-unbilled-reminder').textContent).toContain('1 задача без счёта у 1 клиента');
    fireEvent.click(within(screen.getByTestId('task-unbilled-reminder')).getByRole('button', { name: '1 задача без счёта у 1 клиента' }));
    expect(await screen.findByText('Подготовить иск')).toBeTruthy();
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

    const draft = onCreateInvoiceDraft.mock.calls[0][0];
    expect(draft.form.description).toContain('Счёт подготовлен на основе фактически выполненных услуг по задачам:');
    expect(draft.form.description).toContain('1. Задача №321 от 10.09.2026 — выполнена 20.09.2026. Подготовить иск.');
    expect(draft.form.description).toContain('Результат: Иск подготовлен и направлен клиенту');
    expect(draft.form.description).toContain(`${window.location.origin}/tasks/321`);
    expect(draft.form.description).toContain('Задачи созданы по поручению клиента и хранятся в электронном виде в CRM-системе исполнителя.');
  });

  it('refreshes the accounting queue after the selected task is invoiced', async () => {
    const tasks = [createTask({ id: 321, title: 'Работа для счёта', clientBillingClassification: 'billable', billingStatus: 'unbilled' })];
    await renderJournal(tasks, { billingAnalysisMode: true, onCreateInvoiceDraft: vi.fn() });
    fireEvent.click(within(screen.getByTestId('task-unbilled-reminder')).getByRole('button', { name: '1 задача без счёта у 1 клиента' }));
    const checkbox = await screen.findByRole('checkbox', { name: 'Выбрать задачу Работа для счёта' });
    fireEvent.click(checkbox);
    expect(screen.getByRole('button', { name: 'Подготовить счёт (1)' })).toBeTruthy();
    tasks[0] = { ...tasks[0], billingStatus: 'invoiced', billingInvoiceId: 987 };
    act(() => window.dispatchEvent(new CustomEvent('pp.task_journal.changed', { detail: { bulkBillingIds: [321] } })));
    await waitFor(() => expect(screen.queryByRole('checkbox', { name: 'Выбрать задачу Работа для счёта' })).toBeNull());
    expect(screen.queryByRole('button', { name: 'Подготовить счёт (1)' })).toBeNull();
  });

  it('keeps a completed paid task in the unbilled queue until an invoice is prepared', async () => {
    const activeTask = createTask({
      id: 331,
      title: 'Текущая платная работа',
      clientBillingClassification: 'billable',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });
    const completedTask = createTask({
      id: 332,
      title: 'Выполненная платная работа',
      status: 'done',
      result: 'Работа выполнена',
      clientBillingClassification: 'billable',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });

    await renderJournal([activeTask, completedTask], { billingAnalysisMode: true, onCreateInvoiceDraft: vi.fn() });

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    expect(within(analysis).getByText('2 задачи без счёта у 1 клиента')).toBeTruthy();
    expect(screen.queryByText('Выполненная платная работа')).toBeNull();

    fireEvent.click(within(analysis).getByRole('button', { name: '2 задачи без счёта у 1 клиента' }));

    expect(await screen.findByText('Выполненная платная работа')).toBeTruthy();
    expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ billingStatus: 'unbilled', status: 'all' }),
      expect.anything(),
    );
  });

  it('allows an invoice creator to select a read-only task from the unbilled list', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Бухгалтер', email: 'accountant@example.com' };
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskViewScope = 'all';
    const onCreateInvoiceDraft = vi.fn();
    const task = createTask({
      id: 322,
      title: 'Платная задача Анны',
      assigneeUserId: 9,
      authorUserId: 8,
      clientBillingClassification: 'billable',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });
    await renderJournal([task], { billingAnalysisMode: true, onCreateInvoiceDraft });

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    fireEvent.click(within(analysis).getByRole('button', { name: '1 задача без счёта у 1 клиента' }));

    const checkbox = await screen.findByRole('checkbox', { name: 'Выбрать задачу Платная задача Анны' }) as HTMLInputElement;
    await waitFor(() => expect(checkbox.disabled).toBe(false));
    fireEvent.click(checkbox);
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить счёт (1)' }));

    expect(onCreateInvoiceDraft).toHaveBeenCalledWith(expect.objectContaining({
      billingTaskIds: [322],
    }));
  });
});
