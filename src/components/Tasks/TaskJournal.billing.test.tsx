import { setupTaskJournalTests, apiMock, authState, permissionsState, todayAt, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal } from './TaskJournal.test-support';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

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

    await waitFor(() => expect(apiMock.updateTaskBillingClassification).toHaveBeenCalledWith(task.id, 'internal', null));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Учёт задачи и счёт' })).toBeNull());
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
    expect(within(analysis).getByTestId('task-billing-counter-help').textContent).toContain('количество клиентов');
    expect(within(analysis).getByTestId('task-billing-counter-help').textContent).toContain('количество отдельных работ');

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
      onCreateInvoiceDraft,
      waitForTitle: null,
      settings: {
        financialDocumentsResponsibleUserId: 7,
        financialDocumentsResponsibleUserName: 'Администратор',
      },
    });

    expect(screen.getByTestId('task-unbilled-reminder').textContent).toContain('Платные задачи без счёта: 1');
    fireEvent.click(within(screen.getByTestId('task-unbilled-reminder')).getByRole('button', { name: 'Платные задачи без счёта: 1' }));
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

    await renderJournal([activeTask, completedTask], { onCreateInvoiceDraft: vi.fn() });

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    expect(within(analysis).getByText('Платные задачи без счёта: 2')).toBeTruthy();
    expect(screen.queryByText('Выполненная платная работа')).toBeNull();

    fireEvent.click(within(analysis).getByRole('button', { name: 'Платные задачи без счёта: 2' }));

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
    await renderJournal([task], { onCreateInvoiceDraft });

    const analysis = await screen.findByTestId('task-unbilled-reminder');
    fireEvent.click(within(analysis).getByRole('button', { name: 'Платные задачи без счёта: 1' }));

    const checkbox = await screen.findByRole('checkbox', { name: 'Выбрать задачу Платная задача Анны' }) as HTMLInputElement;
    await waitFor(() => expect(checkbox.disabled).toBe(false));
    fireEvent.click(checkbox);
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить счёт (1)' }));

    expect(onCreateInvoiceDraft).toHaveBeenCalledWith(expect.objectContaining({
      billingTaskIds: [322],
    }));
  });
});
