import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument } from '../../types';

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCaseDocuments: vi.fn(),
    getCaseDocumentTemplates: vi.fn(),
    getTaskJournalItems: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getLegalEntityCaseFinanceSummaries: vi.fn(),
    markCaseActivity: vi.fn(),
    getUserSetting: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    addCaseRelatedParty: vi.fn(),
    deleteCaseRelatedParty: vi.fn(),
    getContacts: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    uploadCaseDocument: vi.fn(),
    generateCaseDocumentTemplate: vi.fn(),
    updateCaseDocument: vi.fn(),
    downloadCaseDocument: vi.fn(),
    deleteCaseDocument: vi.fn(),
  },
}));

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 1,
    clientId: 10,
    title: 'Тестовое дело',
    description: 'Описание',
    status: 'Open',
    createdAt: '2026-04-21T10:00:00Z',
    ...overrides,
  } as ClientCase;
}

describe('CaseModal', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCaseDocuments).mockReset();
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([]);
    vi.mocked(apiService.getTaskJournalItems).mockReset();
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.markCaseActivity).mockReset();
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: '2026-05-18T10:00:00Z',
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: '',
      inactivityRemindersEnabled: true,
      eventHistory: '',
    });
    vi.mocked(apiService.getUserSetting).mockReset();
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null);
    vi.mocked(apiService.getCaseRelatedParties).mockReset();
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([]);
    vi.mocked(apiService.addCaseRelatedParty).mockReset();
    vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({
      id: 5,
      clientCaseId: 1,
      sourceType: 'client',
      sourceId: 10,
      clientId: 10,
      displayName: 'Земцов Виктор Александрович',
      role: 'Свидетель',
      organizationName: '',
      phone: '89990000000',
      email: '',
      notes: '',
      createdAt: '2026-05-18T10:00:00Z',
    });
    vi.mocked(apiService.deleteCaseRelatedParty).mockReset();
    vi.mocked(apiService.deleteCaseRelatedParty).mockResolvedValue(undefined);
    vi.mocked(apiService.getContacts).mockReset();
    vi.mocked(apiService.getContacts).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100 });
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([
      { key: 'case-summary', label: 'Справка по делу', documentType: 'Документ', heading: 'Справка по делу' },
      { key: 'claim', label: 'Исковое заявление', documentType: 'Иск', heading: 'Исковое заявление' },
      { key: 'motion', label: 'Ходатайство', documentType: 'Ходатайство', heading: 'Ходатайство' },
    ]);
    vi.mocked(apiService.uploadCaseDocument).mockReset();
    vi.mocked(apiService.uploadCaseDocument).mockResolvedValue({
      id: 2,
      clientCaseId: 1,
      documentKey: 'doc-key',
      version: 1,
      documentType: 'Документ',
      status: 'Подготовлен',
      originalFileName: 'file.pdf',
      contentType: 'application/pdf',
      sizeBytes: 10,
      uploadedByName: 'Тест',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.generateCaseDocumentTemplate).mockResolvedValue({
      id: 3,
      clientCaseId: 1,
      documentKey: 'template-claim',
      version: 1,
      documentType: 'Иск',
      status: 'Подготовлен',
      originalFileName: 'Исковое заявление - дело 1.html',
      contentType: 'text/html; charset=utf-8',
      sizeBytes: 128,
      uploadedByName: 'Тест',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.updateCaseDocument).mockReset();
    vi.mocked(apiService.updateCaseDocument).mockResolvedValue({
      id: 1,
      clientCaseId: 1,
      documentKey: 'claim',
      version: 1,
      documentType: 'Иск',
      status: 'Отправлен',
      originalFileName: 'Исковое заявление.pdf',
      contentType: 'application/pdf',
      sizeBytes: 2048,
      uploadedByUserId: 7,
      uploadedByName: 'Кожеваткин Андрей',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.downloadCaseDocument).mockReset();
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({ blob: new Blob(['test']), fileName: 'file.pdf' });
    vi.mocked(apiService.deleteCaseDocument).mockReset();
    vi.mocked(apiService.deleteCaseDocument).mockResolvedValue(undefined);
  });

  it('applies a custom overlay class when provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
        overlayClassName="z-[2300]"
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[2300]');
  });

  it('uses the default overlay class when no override is provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[1000]');
  });

  it('marks view and confirms activity for an opened case', async () => {
    vi.mocked(apiService.markCaseActivity)
      .mockResolvedValueOnce({
        lastViewedAt: '2026-05-18T10:00:00Z',
        lastViewedByUserId: 7,
        lastViewedByUserName: 'Кожеваткин Андрей',
        activityConfirmedAt: null,
        activityConfirmedByUserId: null,
        activityConfirmedByUserName: '',
        inactivityRemindersEnabled: true,
        eventHistory: '',
      })
      .mockResolvedValueOnce({
        lastViewedAt: '2026-05-18T10:05:00Z',
        lastViewedByUserId: 7,
        lastViewedByUserName: 'Кожеваткин Андрей',
        activityConfirmedAt: '2026-05-18T10:05:00Z',
        activityConfirmedByUserId: 7,
        activityConfirmedByUserName: 'Кожеваткин Андрей',
        inactivityRemindersEnabled: true,
        eventHistory: '18.05.2026 10:05: Активность по делу подтверждена.',
      });

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase({ lastViewedAt: '2026-05-15T10:00:00Z' })}
        onSave={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.markCaseActivity).toHaveBeenCalledWith(1, { confirmed: false });
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Подтвердить активность' }));

    await waitFor(() => {
      expect(apiService.markCaseActivity).toHaveBeenLastCalledWith(1, { confirmed: true });
    });
    expect(await screen.findByText(/Подтверждено:/)).toBeTruthy();
  });

  it('adds a related party from the directory', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([
      {
        sourceType: 'client',
        category: 'clients',
        sourceId: 10,
        fullName: 'Земцов Виктор Александрович',
        organizationName: null,
        phones: [],
        emails: [],
        isActive: true,
        syncToPhonebook: true,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'Земцов Виктор Александрович' })).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Связанное лицо'), { target: { value: 'client:10' } });
    fireEvent.change(screen.getByLabelText('Роль связанного участника'), { target: { value: 'Свидетель' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить' }));

    await waitFor(() => {
      expect(apiService.addCaseRelatedParty).toHaveBeenCalledWith(1, {
        sourceType: 'client',
        sourceId: 10,
        role: 'Свидетель',
      });
    });
    expect((await screen.findAllByText('Земцов Виктор Александрович')).length).toBeGreaterThan(1);
  });

  it('does not load related party directories when clients and legal entities are hidden', async () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
        canUseContactRelatedParties={false}
        canUseLegalEntityRelatedParties={false}
      />,
    );

    expect(await screen.findByText('Справочники клиентов и организаций закрыты для вашей роли.')).toBeTruthy();
    expect(apiService.getContacts).not.toHaveBeenCalled();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Тип связанного участника') as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByLabelText('Роль связанного участника') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Добавить' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('saves registry, deadline and court fields', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={onSave}
        responsibles={[{ id: 7, fullName: 'Кожеваткин Андрей Владимирович' }]}
      />,
    );

    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Земцов - банкротство' } });
    fireEvent.change(screen.getByLabelText('Номер дела'), { target: { value: ' 2026-001 ' } });
    fireEvent.change(screen.getByLabelText('Тип дела'), { target: { value: 'Банкротство' } });
    fireEvent.change(screen.getByLabelText('Текущий этап'), { target: { value: 'Подготовка заявления' } });
    fireEvent.change(screen.getByLabelText('Ответственный'), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Приоритет'), { target: { value: 'high' } });
    fireEvent.change(screen.getByLabelText('Дата начала'), { target: { value: '2026-05-10' } });
    fireEvent.change(screen.getByLabelText('Крайний срок'), { target: { value: '2026-06-15' } });
    fireEvent.change(screen.getByLabelText('Сумма гонорара'), { target: { value: '150000.50' } });
    fireEvent.change(screen.getByLabelText('Суд'), { target: { value: 'Арбитражный суд Самарской области' } });
    fireEvent.change(screen.getByLabelText('Номер в суде'), { target: { value: 'А55-12345/2026' } });
    fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2026-05-20T12:30' } });
    fireEvent.change(screen.getByLabelText('Ссылка на карточку суда'), { target: { value: 'https://kad.arbitr.ru/card' } });
    fireEvent.change(screen.getByLabelText('Истец / заявитель'), { target: { value: ' Земцов Виктор Александрович ' } });
    fireEvent.change(screen.getByLabelText('Ответчик / должник'), { target: { value: ' Банк Точка ' } });
    fireEvent.change(screen.getByLabelText('Третьи лица'), { target: { value: 'ФНС\nСРО' } });
    fireEvent.change(screen.getByLabelText('Представители'), { target: { value: 'Кожеваткин А.В.' } });
    fireEvent.change(screen.getByLabelText('Судья'), { target: { value: ' Иванова И.И. ' } });
    fireEvent.change(screen.getByLabelText('Зал / кабинет'), { target: { value: ' зал 12 ' } });
    fireEvent.change(screen.getByLabelText('Контакты секретаря суда'), { target: { value: ' secretary@example.test ' } });
    fireEvent.change(screen.getByLabelText('Дата подачи заявления'), { target: { value: '2026-05-11' } });
    fireEvent.change(screen.getByLabelText('Дата решения суда'), { target: { value: '2026-04-01' } });
    expect((screen.getByLabelText('Срок апелляции') as HTMLInputElement).value).toBe('2026-05-04');
    expect((screen.getByLabelText('Срок кассации') as HTMLInputElement).value).toBe('2026-07-01');
    fireEvent.change(screen.getByLabelText('Срок апелляции'), { target: { value: '2026-07-01' } });
    fireEvent.change(screen.getByLabelText('Срок подачи ходатайств'), { target: { value: '2026-06-01' } });
    fireEvent.change(screen.getByLabelText('Срок возражений'), { target: { value: '2026-06-05' } });
    fireEvent.change(screen.getByLabelText('Документы по делу'), { target: { value: 'Паспорт, договор' } });
    fireEvent.change(screen.getByLabelText('История событий'), { target: { value: 'Заявление принято' } });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Подать ходатайство' } });

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Земцов - банкротство',
      caseNumber: '2026-001',
      caseType: 'Банкротство',
      stage: 'Подготовка заявления',
      responsibleUserId: 7,
      priority: 'high',
      feeAmount: 150000.5,
      courtName: 'Арбитражный суд Самарской области',
      courtCaseNumber: 'А55-12345/2026',
      courtUrl: 'https://kad.arbitr.ru/card',
      claimant: 'Земцов Виктор Александрович',
      defendant: 'Банк Точка',
      thirdParties: 'ФНС\nСРО',
      representatives: 'Кожеваткин А.В.',
      judgeName: 'Иванова И.И.',
      courtroom: 'зал 12',
      courtSecretaryContacts: 'secretary@example.test',
      courtDecisionAt: expect.any(String),
      cassationDeadlineAt: expect.any(String),
      documentsSummary: 'Паспорт, договор',
      eventHistory: 'Заявление принято',
      strategy: 'Подать ходатайство',
    }));
  });

  it('loads case documents and uploads selected file with type and status', async () => {
    const documents: ClientCaseDocument[] = [
      {
        id: 1,
        clientCaseId: 1,
        documentKey: 'claim',
        version: 1,
        documentType: 'Иск',
        status: 'Подготовлен',
        originalFileName: 'Исковое заявление.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Кожеваткин Андрей',
        uploadedAt: '2026-05-18T10:00:00Z',
        isLatestVersion: true,
      },
    ];
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue(documents);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Исковое заявление.pdf')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Тип файла дела'), { target: { value: 'Иск' } });
    fireEvent.change(screen.getByLabelText('Статус файла дела'), { target: { value: 'Отправлен' } });

    const file = new File(['claim'], 'new-claim.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByLabelText('Загрузить файл дела'), { target: { files: [file] } });

    await waitFor(() => {
      expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(1, file, {
        documentType: 'Иск',
        status: 'Отправлен',
        documentKey: undefined,
      });
    });

    fireEvent.change(screen.getByLabelText('Статус документа Исковое заявление.pdf'), {
      target: { value: 'Отправлен' },
    });

    await waitFor(() => {
      expect(apiService.updateCaseDocument).toHaveBeenCalledWith(1, 1, { status: 'Отправлен' });
    });

    fireEvent.change(screen.getByLabelText('Тип документа Исковое заявление.pdf'), {
      target: { value: 'Решение суда' },
    });

    await waitFor(() => {
      expect(apiService.updateCaseDocument).toHaveBeenCalledWith(1, 1, { documentType: 'Решение суда' });
    });
  });

  it('shows document upload time in the profile time zone', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 1,
        documentKey: 'claim',
        version: 1,
        documentType: 'Иск',
        status: 'Подготовлен',
        originalFileName: 'claim.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Кожеваткин Андрей',
        uploadedAt: '2026-05-18T10:00:00Z',
        isLatestVersion: true,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('claim.pdf')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return element?.tagName === 'DIV'
          && element.className === 'text-xs text-gray-500'
          && value.includes('18.05.2026')
          && value.includes('14:00');
      }).length).toBeGreaterThan(0);
    });
  });

  it('generates a case document from the selected template', async () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Шаблон документа дела'), { target: { value: 'claim' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сформировать' }));

    await waitFor(() => {
      expect(apiService.generateCaseDocumentTemplate).toHaveBeenCalledWith(1, {
        templateKey: 'claim',
        documentType: 'Иск',
        status: 'Подготовлен',
      });
    });
    expect(apiService.getCaseDocuments).toHaveBeenCalledWith(1);
  });

  it('shows finance summary for the current case', async () => {
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([
      {
        caseId: 1,
        clientId: 10,
        clientName: 'Земцов Виктор Александрович',
        caseTitle: 'Тестовое дело',
        caseStatus: 'Open',
        caseCreatedAt: '2026-04-21T10:00:00Z',
        incomeTotal: 25000,
        incomePaid: 15000,
        incomeOutstanding: 10000,
        incomeCount: 2,
        expenseTotal: 0,
        expensePaid: 0,
        expenseOutstanding: 0,
        expenseCount: 0,
        plannedMargin: 25000,
        paidMargin: 15000,
        outstandingBalance: 10000,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase({ feeAmount: 30000 })}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Финансы по делу')).toBeTruthy();
    expect(apiService.getClientCaseFinanceSummaries).toHaveBeenCalledWith(10);
    expect(screen.getByText('Счета по делу')).toBeTruthy();
    expect(screen.getByText((text) => text.includes('25') && text.includes('000'))).toBeTruthy();
    expect(screen.getByText((text) => text.includes('10') && text.includes('000'))).toBeTruthy();
  });

  it('shows tasks linked to the case', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({
      items: [
        {
          id: 11,
          clientId: 10,
          clientCaseId: 1,
          objectName: 'Земцов - банкротство',
          title: 'Подготовить заявление',
          description: '',
          assigneeUserId: 7,
          assigneeName: 'Кожеваткин Андрей',
          authorUserId: 1,
          authorName: 'Администратор',
          dueAt: '2026-05-20T09:30:00Z',
          plannedDurationMinutes: 60,
          type: 'task',
          priority: 'high',
          status: 'inProgress',
          result: '',
          reminders: ['atDue'],
          channels: ['inApp'],
          tags: [],
          createdAt: '2026-05-18T09:00:00Z',
          updatedAt: '2026-05-18T09:00:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
    });

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Подготовить заявление')).toBeTruthy();
    expect(screen.getByText('В работе')).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenCalledWith({
      clientCaseId: 1,
      page: 1,
      pageSize: 25,
    });
  });
});
