import { setupTaskJournalTests, apiMock, authState, permissionsState, todayAt, createTask, renderJournal, openFirstTaskForEdit } from './TaskJournal.test-support';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

describe('TaskJournal', () => {
  setupTaskJournalTests();


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

  it.each([
    { taskCardWindowMode: 'legacy' as const, caseDocumentId: 42 },
    { taskCardWindowMode: 'modern' as const, caseDocumentId: 42 },
    { taskCardWindowMode: 'legacy' as const, caseDocumentId: null },
    { taskCardWindowMode: 'modern' as const, caseDocumentId: null },
  ])('shows only the persisted case document reference in the $taskCardWindowMode card ($caseDocumentId)', async ({ taskCardWindowMode, caseDocumentId }) => {
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([{
      id: 600,
      taskJournalItemId: 101,
      documentKey: 'late-case-file',
      version: 2,
      originalFileName: 'late-case.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      uploadedByUserId: 7,
      uploadedByName: 'Автор',
      uploadedAt: todayAt(10),
      isLatestVersion: true,
      clientCaseDocumentId: caseDocumentId,
    }]);
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') fireEvent.click(within(card).getByRole('tab', { name: 'Файлы 1' }));
    expect((await within(card).findAllByText('late-case.pdf')).length).toBeGreaterThan(0);
    if (caseDocumentId !== null) expect(await within(card).findByText(/Документ дела #42/u)).toBeTruthy();
    else expect(within(card).queryByText(/Документ дела #/u)).toBeNull();
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
