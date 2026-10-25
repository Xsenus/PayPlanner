import { setupTaskJournalTests, apiMock, toastMock, todayAt, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal, continueTaskWizardForClient } from './TaskJournal.test-support';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it.each([
    ['legacy', 'assignees', 'pending'], ['modern', 'assignees', 'pending'],
    ['legacy', 'automation', 'pending'], ['modern', 'automation', 'pending'],
    ['legacy', 'assignees', 'failed'], ['modern', 'assignees', 'failed'],
    ['legacy', 'automation', 'failed'], ['modern', 'automation', 'failed'],
  ] as const)('ancillary regression: %s card with %s %s', async (mode, dependency, outcome) => {
    let release: (() => void) | undefined;
    const delayed = new Promise<never>((_resolve, reject) => {
      release = () => reject(new Error('Synthetic ancillary metadata unavailable'));
    });
    const dependencyMock = dependency === 'assignees'
      ? apiMock.getTaskJournalAssignees : apiMock.getTaskJournalCaseAutomationCandidates;
    dependencyMock.mockImplementationOnce(() => delayed);
    if (outcome === 'failed') release?.();
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(screen.queryByTestId(`task-${mode === 'modern' ? 'legacy' : 'modern'}-card-window`)).toBeNull();
      expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
      expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(1);
    } finally {
      if (outcome === 'pending') release?.();
    }
  });
  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings readiness: %s card stays unopened until settings arrive, routed=%s', async (mode, withRoute) => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay, withRoute });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
      expect(await screen.findByText('Загружаем настройки карточки задачи...')).toBeTruthy();
      release?.();
      expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
    } finally {
      release?.();
    }
  });
  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings retry: %s card never uses defaults after a settings failure, routed=%s', async (mode, withRoute) => {
    apiMock.getTaskJournalSettings.mockRejectedValueOnce(new Error('Synthetic initial settings failure'));
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute });
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByText('Не удалось загрузить настройки карточки задачи.')).toBeTruthy();
    expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
    expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    fireEvent.click(within(screen.getByTestId('task-card-settings-gate')).getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings close: %s card stays closed when delayed settings arrive, routed=%s', async (mode, withRoute) => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay, withRoute });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByTestId('task-card-settings-gate');
      fireEvent.click(within(gate).getByRole('button', { name: withRoute ? 'К журналу' : 'Закрыть' }));
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });

  it.each(['legacy', 'modern'] as const)('settings focus: %s waiting dialog focuses close and supports Escape', async mode => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByRole('dialog', { name: 'Открытие карточки задачи' });
      expect(document.activeElement).toBe(within(gate).getByRole('button', { name: 'Закрыть' }));
      fireEvent.keyDown(gate, { key: 'Escape' });
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });
  it.each(['legacy', 'modern'] as const)('settings retry focus: %s dialog keeps keyboard focus while retry is pending', async mode => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    apiMock.getTaskJournalSettings.mockRejectedValueOnce(new Error('Synthetic initial settings failure'));
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByRole('dialog', { name: 'Открытие карточки задачи' });
      const retry = within(gate).getByRole('button', { name: 'Повторить загрузку' });
      retry.focus();
      fireEvent.click(retry);
      expect(await within(gate).findByText('Загружаем настройки карточки задачи...')).toBeTruthy();
      expect(document.activeElement).toBe(within(gate).getByRole('button', { name: 'Закрыть' }));
      fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });
  it('opens the modern task card when the global setting is enabled', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    expect(await screen.findByTestId('task-modern-card-window')).toBeTruthy();
    expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
  });

  it('switches between compact and expanded modern card layouts and remembers the choice', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    let card = await screen.findByTestId('task-modern-card-window');
    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('compact');

    fireEvent.click(within(card).getByRole('button', { name: 'Еще' }));
    const expandedLayoutButton = within(card).getByRole('button', { name: 'Расширенный вид' });
    expect(expandedLayoutButton.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(expandedLayoutButton);

    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('expanded');
    expect(window.localStorage.getItem('pp.task_journal.card_layout.v1.user:7')).toBe('expanded');

    fireEvent.click(within(card).getByRole('button', { name: 'Отмена' }));
    await openFirstTaskForEdit();
    card = await screen.findByTestId('task-modern-card-window');
    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('expanded');
  });

  it.each(['legacy', 'modern'] as const)('restores pending server uploads in the %s task card', async (taskCardWindowMode) => {
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'ожидающий.pdf',
      status: 'waiting', fileId: null, message: 'Система завершит загрузку автоматически.' }]);
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    await waitFor(() => expect(within(card).getByRole('region', { name: 'Состояние ваших загрузок' })).toBeTruthy());
    expect(within(card).getByText('ожидающий.pdf')).toBeTruthy();
    expect(within(card).getByText('Система завершит загрузку автоматически.')).toBeTruthy();
    expect(apiMock.getTaskFileUploadPage).toHaveBeenCalledWith(101, expect.any(AbortSignal), undefined);
    cleanup();
  });

  it.each(['legacy', 'modern'] as const)('refreshes published files without a second result toast in the %s card', async (taskCardWindowMode) => {
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'готовый.pdf',
      status: 'waiting', fileId: null, message: 'Ожидание загрузки.' }]);
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(await within(card).findByText('Ожидание загрузки.')).toBeTruthy();
    const readsBeforePublication = apiMock.getTaskJournalFiles.mock.calls.length;
    apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 601, taskJournalItemId: 101, documentKey: 'doc-1', version: 1,
      originalFileName: 'готовый.pdf', contentType: 'application/pdf', sizeBytes: 9,
      uploadedByName: 'Исполнитель', uploadedAt: todayAt(10), isLatestVersion: true }]);
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'готовый.pdf',
      status: 'uploaded', fileId: 601, message: 'Файл прикреплён к задаче.' }]);
    await waitFor(() => expect(within(card).getByText('Файл прикреплён к задаче.')).toBeTruthy(), { timeout: 7000 });
    expect(apiMock.getTaskJournalFiles.mock.calls.length).toBeGreaterThan(readsBeforePublication);
    expect(apiMock.uploadTaskJournalFile).not.toHaveBeenCalled();
    expect(toastMock.success).not.toHaveBeenCalledWith('Список файлов обновлён после завершения загрузки.', 'Журнал задач');
    cleanup();
  }, 15000);

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
    expect(within(within(card).getByRole('tabpanel')).getByText('1 ч 15 мин')).toBeTruthy();
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

  it.each(['legacy', 'modern'] as const)('creates a task together with its first comment and files in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], {
      waitForTitle: null,
      settings: { taskCardWindowMode },
    });
    await openNewTaskFormAsGlobalInternal();

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с материалами' } });
    fireEvent.change(within(card).getByPlaceholderText(taskCardWindowMode === 'modern'
      ? 'Напишите комментарий...'
      : 'Введите первый комментарий'), { target: { value: 'Первый комментарий' } });
    fireEvent.click(within(card).getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' }));

    const files = [
      new File(['one'], 'первый.pdf', { type: 'application/pdf' }),
      new File(['two'], 'второй.docx', { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }),
    ];
    const fileInput = card.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(fileInput, { target: { files } });

    expect(await within(card).findByText('первый.pdf')).toBeTruthy();
    expect(within(card).getByText('второй.docx')).toBeTruthy();
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Первый комментарий', true));
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(2);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenNthCalledWith(1, 101, files[0], undefined);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenNthCalledWith(2, 101, files[1], undefined);
  });

  it.each(['legacy', 'modern'] as const)('keeps a newly created task open with accepted pending files in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable'));
    apiMock.getTaskFileUploadKey.mockReturnValue('pending-key');
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', fileName: 'первый.pdf',
      status: 'waiting', fileId: null, message: 'Завершение загрузки выполняется автоматически.' }]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с ожидающим файлом' } });
    const file = new File(['document'], 'первый.pdf', { type: 'application/pdf' });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));
    await waitFor(() => expect(toastMock.info).toHaveBeenCalledWith(expect.stringContaining('Задача создана. Файлы ещё ожидают прикрепления'), 'Журнал задач'));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['pending-key'] });
    expect(await screen.findByRole('region', { name: 'Состояние ваших загрузок' })).toBeTruthy();
    expect(toastMock.error).not.toHaveBeenCalled();
    cleanup();
  });

  it.each(['legacy', 'modern'] as const)('keeps the created task open when its first file fails in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('Загрузка недоступна'));

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с недогруженным файлом' } });
    const file = new File(['document'], 'первый.pdf', { type: 'application/pdf' });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      expect.stringContaining('Задача создана, но не удалось добавить: первый.pdf'),
      'Журнал задач',
    ));
    const savedCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(savedCard).getAllByText('Просмотр задачи').length).toBeGreaterThan(0);
    expect(within(savedCard).queryByRole('button', { name: 'Создать задачу' })).toBeNull();
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('keeps the created task open when its first comment fails in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.createTaskJournalComment.mockRejectedValueOnce(new Error('Комментарий недоступен'));

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с недобавленным комментарием' } });
    fireEvent.change(within(card).getByPlaceholderText(taskCardWindowMode === 'modern'
      ? 'Напишите комментарий...'
      : 'Введите первый комментарий'), { target: { value: 'Первый комментарий' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      expect.stringContaining('Задача создана, но не удалось добавить: комментарий'),
      'Журнал задач',
    ));
    const savedCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(savedCard).getAllByText('Просмотр задачи').length).toBeGreaterThan(0);
    expect(within(savedCard).queryByRole('button', { name: 'Создать задачу' })).toBeNull();
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('opens linked client and case profiles from the %s task card', async (taskCardWindowMode) => {
    const onOpenClient = vi.fn();
    const onOpenCase = vi.fn();
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode },
      onOpenClient,
      onOpenCase,
    });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByRole('button', { name: 'Открыть карточку контрагента' }));
    fireEvent.click(within(card).getByRole('button', { name: 'Открыть карточку дела' }));

    expect(onOpenClient).toHaveBeenCalledWith(10, 100);
    expect(onOpenCase).toHaveBeenCalledWith(100, 10);
  });

  it.each(['legacy', 'modern'] as const)('links a task to its sales request from the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask({ salesRequestId: 77 })], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const link = within(card).getByRole('link', { name: 'Открыть заявку' }) as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/?ppTab=requests&requestId=77');
    expect(link.target).toBe('_blank');
    expect(link.rel).toContain('noopener');
  });

  it.each(['legacy', 'modern'] as const)('does not show a sales request link for an ordinary task in the %s card', async (taskCardWindowMode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('link', { name: 'Открыть заявку' })).toBeNull();
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
});
