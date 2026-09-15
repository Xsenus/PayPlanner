import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseWorkOrder } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CaseDetailPage, CourtSourceProvenance } from './CaseDetailPage';

const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
  prompt: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCaseV2: vi.fn(),
    updateCaseV2: vi.fn(),
    getActResponsibles: vi.fn(),
    getTaskJournalItems: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    getClients: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getContacts: vi.fn(),
    getCaseDocuments: vi.fn(),
    getCaseDocumentTemplates: vi.fn(),
    getCaseDocumentTypes: vi.fn(),
    getDocumentStatuses: vi.fn(),
    createTaskJournalCaseAutomationTask: vi.fn(),
    createTaskJournalItem: vi.fn(),
    bulkUpdateCases: vi.fn(),
    generateCaseDocumentTemplate: vi.fn(),
    uploadCaseDocument: vi.fn(),
    uploadCaseDocumentVersion: vi.fn(),
    downloadCaseDocument: vi.fn(),
    uploadCaseDocumentToYandexDisk: vi.fn(),
    updateCaseDocument: vi.fn(),
    deleteCaseDocument: vi.fn(),
    addCaseHistoryEntry: vi.fn(),
    getCaseConversation: vi.fn(),
    addCaseConversationEntry: vi.fn(),
    runCaseCourtSync: vi.fn(),
    searchCaseCourtSyncCases: vi.fn(),
    bindCaseCourtSyncCase: vi.fn(),
    getCaseCourtSyncLogs: vi.fn(),
    getCaseCourtDirectory: vi.fn(),
    getCaseCourtInstances: vi.fn(),
    updateCaseCourtInstance: vi.fn(),
    createCaseCourtComplaint: vi.fn(),
    updateCaseCourtComplaint: vi.fn(),
    archiveCaseCourtComplaint: vi.fn(),
    getCaseCourtEvents: vi.fn(),
    createCaseCourtEvent: vi.fn(),
    updateCaseCourtEvent: vi.fn(),
    getCaseCourtEventHistory: vi.fn(),
    getCaseCourtParties: vi.fn(),
    createCaseCourtParty: vi.fn(),
    archiveCaseCourtParty: vi.fn(),
    getCaseProceduralDeadlines: vi.fn(),
    calculateCaseProceduralDeadline: vi.fn(),
    createCaseProceduralDeadline: vi.fn(),
    updateCaseProceduralDeadline: vi.fn(),
    closeCaseProceduralDeadline: vi.fn(),
    getCaseTimeline: vi.fn(),
    getCaseFinance: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getLegalEntityCaseFinanceSummaries: vi.fn(),
    getCaseWorkOrder: vi.fn(),
    getFeatureFlags: vi.fn(),
    getExternalWorkOrderApproval: vi.fn(),
    createExternalWorkOrderApproval: vi.fn(),
    revokeExternalWorkOrderApproval: vi.fn(),
    createCaseWorkOrder: vi.fn(),
    updateCaseWorkOrder: vi.fn(),
    deleteCaseWorkOrder: vi.fn(),
    addCaseWorkOrderItem: vi.fn(),
    updateCaseWorkOrderItem: vi.fn(),
    deleteCaseWorkOrderItem: vi.fn(),
    createCaseWorkOrderInvoice: vi.fn(),
    createCaseWorkOrderAct: vi.fn(),
    createCaseWorkOrderContract: vi.fn(),
    submitCaseWorkOrderApproval: vi.fn(),
    approveCaseWorkOrder: vi.fn(),
    requestCaseWorkOrderRevision: vi.fn(),
    getCaseWorkOrderPrintHtml: vi.fn(),
    generateCaseWorkOrderDocument: vi.fn(),
    getObjectAuditHistory: vi.fn(),
    addCaseRelatedParty: vi.fn(),
    deleteCaseRelatedParty: vi.fn(),
    requestCaseAccess: vi.fn(),
    markCaseActivity: vi.fn(),
    getUserSetting: vi.fn(),
  },
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 15,
    clientId: 12,
    legalEntityId: null,
    client: {
      id: 12,
      name: 'Земцов Виктор Александрович',
      email: '',
      phone: '',
      company: '',
      address: '',
      notes: '',
      createdAt: '2026-05-01T00:00:00Z',
      isActive: true,
    },
    caseNumber: '2026-015',
    title: 'ЗЕМЦОВ - банкротство',
    description: 'Подготовка дела',
    status: 'Open',
    priority: 'high',
    caseType: 'Банкротство',
    stage: 'Судебное производство',
    responsibleUserName: 'Кожеваткин Андрей Владимирович',
    responsibleUserId: 7,
    dueDate: '2026-06-15',
    nextHearingAt: '2026-05-20T09:30:00Z',
    courtName: 'Арбитражный суд Самарской области',
    courtCaseNumber: 'А55-12345/2026',
    courtUrl: 'https://kad.arbitr.ru/card',
    appealDeadlineAt: '2026-07-01',
    feeAmount: 150000,
    incomeTotal: 50000,
    incomePaid: 35000,
    incomeOutstanding: 15000,
    incomeCount: 2,
    expenseTotal: 10000,
    expensePaid: 5000,
    expenseOutstanding: 5000,
    openTaskCount: 1,
    completedTaskCount: 1,
    overdueTaskCount: 1,
    taskProgressPercent: 50,
    claimant: 'Истец',
    defendant: 'Ответчик',
    strategy: 'План действий',
    documentsSummary: 'Есть заявление',
    eventHistory: 'Создано дело\nМассовое изменение',
    createdAt: '2026-05-01T10:00:00Z',
    ...overrides,
  } as ClientCase;
}

function createTask(overrides: Partial<TaskJournalItem> = {}): TaskJournalItem {
  return {
    id: 101,
    clientId: 12,
    clientCaseId: 15,
    objectName: 'ЗЕМЦОВ - банкротство',
    title: 'Проверить движение по делу',
    description: '',
    assigneeName: 'Кожеваткин Андрей Владимирович',
    authorName: 'Администратор',
    dueAt: '2026-05-20T10:00:00Z',
    plannedDurationMinutes: 60,
    type: 'control',
    priority: 'high',
    status: 'inProgress',
    result: '',
    reminders: [],
    channels: [],
    tags: [],
    createdAt: '2026-05-10T10:00:00Z',
    updatedAt: '2026-05-10T10:00:00Z',
    ...overrides,
  };
}

describe('CourtSourceProvenance', () => {
  it('shows the official source, check time and a safe source link', () => {
    render(
      <CourtSourceProvenance
        item={{
          sourceType: 'sudrf',
          sourceUrl: 'https://example.sudrf.ru/modules.php?name=sud_delo',
          sourceCheckedAt: '2026-07-29T12:30:00Z',
          sourceConfidence: 'official-source',
        }}
      />,
    );

    expect(screen.getByText('ГАС «Правосудие»')).toBeTruthy();
    expect(screen.getByText('официальный источник')).toBeTruthy();
    expect(screen.getByText(/проверено/u)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Открыть источник' }).getAttribute('href'))
      .toBe('https://example.sudrf.ru/modules.php?name=sud_delo');
  });

  it('does not render provenance for a regular manual object', () => {
    const { container } = render(<CourtSourceProvenance item={{ sourceType: 'manual' }} />);
    expect(container.innerHTML).toBe('');
  });

  it('shows a user-facing label for a deadline linked to a court event', () => {
    render(<CourtSourceProvenance item={{ sourceType: 'court-event', sourceConfidence: 'manual' }} />);

    expect(screen.getByText('Судебное событие')).toBeTruthy();
    expect(screen.getByText('внесено вручную')).toBeTruthy();
    expect(screen.queryByText('court-event')).toBeNull();
  });
});

describe('CaseDetailPage', () => {
  beforeEach(() => {
    dialogMock.confirm.mockReset();
    dialogMock.confirm.mockResolvedValue(true);
    dialogMock.prompt.mockReset();
    dialogMock.prompt.mockResolvedValue(null);
    window.sessionStorage.clear();
    vi.mocked(apiService.getCaseV2).mockReset();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase());
    vi.mocked(apiService.getFeatureFlags).mockReset();
    vi.mocked(apiService.getFeatureFlags).mockResolvedValue({
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
    vi.mocked(apiService.getExternalWorkOrderApproval).mockReset();
    vi.mocked(apiService.getExternalWorkOrderApproval).mockResolvedValue(undefined);
    vi.mocked(apiService.getUserSetting).mockReset();
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null);
    vi.mocked(apiService.updateCaseV2).mockReset();
    vi.mocked(apiService.updateCaseV2).mockImplementation(async (id, payload) => createCase({ ...payload, id }));
    vi.mocked(apiService.getActResponsibles).mockReset();
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([
      { id: 7, fullName: 'Кожеваткин Андрей Владимирович' },
      { id: 8, fullName: 'Диденко Анна Владимировна' },
    ]);
    vi.mocked(apiService.getTaskJournalItems).mockReset();
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({ items: [createTask()], total: 1, page: 1, pageSize: 50 });
    vi.mocked(apiService.getCaseRelatedParties).mockReset();
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 15,
        sourceType: 'client',
        sourceId: 12,
        clientId: 12,
        displayName: 'Земцов Виктор Александрович',
        role: 'Должник',
        phone: '+79991234567',
        email: 'zemtsov@example.test',
        notes: 'Представитель по доверенности',
        createdAt: '2026-05-01T10:00:00Z',
      },
    ]);
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getClients).mockResolvedValue([
      createCase().client!,
      {
        id: 30,
        name: 'Иванов Иван Иванович',
        email: 'ivanov@example.test',
        phone: '+79990000000',
        company: '',
        address: '',
        notes: '',
        createdAt: '2026-05-01T00:00:00Z',
        isActive: true,
      },
    ]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({
      items: [
        {
          id: 25,
          shortName: 'ООО "Процесс"',
          fullName: 'Общество с ограниченной ответственностью "Процесс"',
          inn: '6310000000',
          phone: '+79991112233',
          email: 'office@example.test',
          clientsCount: 0,
          createdAt: '2026-05-01T00:00:00Z',
          clients: [],
        },
      ],
      total: 1,
      page: 1,
      pageSize: 200,
    });
    vi.mocked(apiService.getContacts).mockReset();
    vi.mocked(apiService.getContacts).mockResolvedValue([
      {
        sourceType: 'external',
        category: 'external',
        sourceId: 44,
        contactType: 'Person',
        roles: ['Contact'],
        hasFullProfile: false,
        fullName: 'Петров Петр Петрович',
        organizationName: 'ООО "Процесс"',
        phone: '+79992223344',
        email: 'petrov@example.test',
        phones: ['+79992223344'],
        emails: ['petrov@example.test'],
        isActive: true,
        syncToPhonebook: false,
      },
    ]);
    vi.mocked(apiService.getCaseDocuments).mockReset();
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      {
        id: 7,
        clientCaseId: 15,
        documentKey: 'claim',
        version: 1,
        documentType: 'Заявление',
        status: 'Готово',
        originalFileName: 'Заявление.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        sizeBytes: 1024,
        uploadedByName: 'Администратор',
        uploadedAt: '2026-05-02T10:00:00Z',
        isLatestVersion: true,
      },
    ]);
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([
      { key: 'case-summary', label: 'Справка по делу', documentType: 'Карточка дела', heading: 'Справка по делу' },
      { key: 'claim', label: 'Исковое заявление', documentType: 'Иск', heading: 'Исковое заявление' },
      { key: 'motion', label: 'Ходатайство', documentType: 'Ходатайство', heading: 'Ходатайство' },
    ]);
    vi.mocked(apiService.getCaseDocumentTypes).mockReset();
    vi.mocked(apiService.getCaseDocumentTypes).mockResolvedValue(['Акт сверки', 'Иск', 'Ходатайство']);
    vi.mocked(apiService.getCaseProceduralDeadlines).mockReset();
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([
      {
        id: 31,
        clientCaseId: 15,
        deadlineType: 'appeal',
        deadlineTypeLabel: 'Апелляция',
        title: 'Апелляционная жалоба',
        dueAt: '2026-07-01',
        allDay: true,
        status: 'active',
        statusLabel: 'Активен',
        warningDaysBefore: 7,
        sourceType: 'manual',
        notes: '',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
        isWarning: false,
      },
    ]);
    vi.mocked(apiService.getCaseCourtEvents).mockReset();
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        id: 41,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Предварительное заседание',
        scheduledAt: '2026-06-12T09:30:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        sourceType: 'manual',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
    ]);
    vi.mocked(apiService.getCaseCourtDirectory).mockReset();
    vi.mocked(apiService.getCaseCourtDirectory).mockResolvedValue([
      {
        id: 71,
        name: 'Арбитражный суд города Москвы',
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        code: 'msk-arbitrage',
        isActive: true,
        sortOrder: 1,
        createdAt: '2026-05-21T00:00:00Z',
      },
    ]);
    vi.mocked(apiService.getCaseCourtInstances).mockReset();
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValue([
      {
        id: 0,
        clientCaseId: 15,
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        courtUrl: 'https://kad.arbitr.ru/card',
        judgeName: '',
        courtroom: '',
        secretaryName: '',
        notes: '',
        createdAt: '2026-05-21T00:00:00Z',
        complaints: [],
      },
      { id: 0, clientCaseId: 15, instance: 'appeal', instanceLabel: 'Апелляция', createdAt: '2026-05-21T00:00:00Z', complaints: [] },
      { id: 0, clientCaseId: 15, instance: 'cassation', instanceLabel: 'Кассация', createdAt: '2026-05-21T00:00:00Z', complaints: [] },
      { id: 0, clientCaseId: 15, instance: 'supreme', instanceLabel: 'Верховный суд', createdAt: '2026-05-21T00:00:00Z', complaints: [] },
    ]);
    vi.mocked(apiService.updateCaseCourtInstance).mockReset();
    vi.mocked(apiService.updateCaseCourtInstance).mockResolvedValue({
      id: 81,
      clientCaseId: 15,
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      courtName: 'Самарский областной суд',
      courtCaseNumber: '33-123/2026',
      courtUrl: 'https://example.test/appeal',
      createdAt: '2026-05-21T00:00:00Z',
      complaints: [],
    });
    vi.mocked(apiService.createCaseCourtComplaint).mockReset();
    vi.mocked(apiService.createCaseCourtComplaint).mockResolvedValue({
      id: 91,
      clientCaseId: 15,
      courtInstanceId: 81,
      complaintType: 'privateComplaint',
      complaintTypeLabel: 'Частная жалоба',
      title: 'Частная жалоба',
      targetInstance: 'appeal',
      targetInstanceLabel: 'Апелляция',
      status: 'draft',
      statusLabel: 'Черновик',
      isArchived: false,
      createdAt: '2026-05-21T00:00:00Z',
    });
    vi.mocked(apiService.updateCaseCourtComplaint).mockReset();
    vi.mocked(apiService.updateCaseCourtComplaint).mockResolvedValue({
      id: 91,
      clientCaseId: 15,
      courtInstanceId: 81,
      complaintType: 'privateComplaint',
      complaintTypeLabel: 'Частная жалоба',
      title: 'Частная жалоба',
      targetInstance: 'appeal',
      targetInstanceLabel: 'Апелляция',
      status: 'accepted',
      statusLabel: 'Принята',
      isArchived: false,
      createdAt: '2026-05-21T00:00:00Z',
    });
    vi.mocked(apiService.archiveCaseCourtComplaint).mockReset();
    vi.mocked(apiService.archiveCaseCourtComplaint).mockResolvedValue(undefined);
    vi.mocked(apiService.createCaseCourtEvent).mockReset();
    vi.mocked(apiService.createCaseCourtEvent).mockResolvedValue({
      id: 42,
      clientCaseId: 15,
      eventType: 'hearing',
      eventTypeLabel: 'Заседание',
      title: 'Новое заседание',
      scheduledAt: '2026-06-20T10:00:00Z',
      allDay: false,
      courtInstance: 'first',
      courtInstanceLabel: 'Первая инстанция',
      movementStatus: 'scheduled',
      movementStatusLabel: 'Назначено',
      sourceType: 'manual',
      createdAt: '2026-05-21T00:00:00Z',
      isOverdue: false,
    });
    vi.mocked(apiService.getCaseCourtParties).mockReset();
    vi.mocked(apiService.getCaseCourtParties).mockResolvedValue([
      {
        id: 51,
        clientCaseId: 15,
        displayName: 'ООО Ответчик',
        side: 'respondent',
        sideLabel: 'Ответчик',
        processRole: 'Ответчик',
        representativeName: 'Петров Петр',
        notes: '',
        isArchived: false,
        createdAt: '2026-05-21T00:00:00Z',
      },
    ]);
    vi.mocked(apiService.createCaseCourtParty).mockReset();
    vi.mocked(apiService.createCaseCourtParty).mockResolvedValue({
      id: 52,
      clientCaseId: 15,
      displayName: 'АО Истец',
      side: 'claimant',
      sideLabel: 'Истец',
      processRole: 'Истец',
      representativeName: '',
      notes: '',
      isArchived: false,
      createdAt: '2026-05-21T00:00:00Z',
    });
    vi.mocked(apiService.archiveCaseCourtParty).mockReset();
    vi.mocked(apiService.archiveCaseCourtParty).mockResolvedValue(undefined);
    vi.mocked(apiService.createCaseProceduralDeadline).mockReset();
    vi.mocked(apiService.createCaseProceduralDeadline).mockResolvedValue({
      id: 32,
      clientCaseId: 15,
      deadlineType: 'motion',
      deadlineTypeLabel: 'Ходатайство',
      title: 'Подать ходатайство',
      dueAt: '2026-06-10',
      allDay: true,
      status: 'active',
      statusLabel: 'Активен',
      warningDaysBefore: 7,
      sourceType: 'manual',
      notes: '',
      createdAt: '2026-05-21T00:00:00Z',
      isOverdue: false,
      isWarning: false,
    });
    vi.mocked(apiService.calculateCaseProceduralDeadline).mockReset();
    vi.mocked(apiService.calculateCaseProceduralDeadline).mockResolvedValue({
      deadlineAt: '2026-06-10T00:00:00Z',
      rule: 'workingDays',
      monthsToAdd: 0,
      daysToAdd: 5,
      rollForwardToWorkingDay: true,
      startAt: '2026-05-20T00:00:00Z',
      rawDeadlineAt: '2026-06-09T00:00:00Z',
      skippedNonWorkingDays: 2,
      rollForwardDays: 1,
      explanation: 'Старт: 20.05.2026. Правило: 5 рабочих дней. Базовая дата: 09.06.2026. Дата перенесена на ближайший рабочий день: 10.06.2026.',
    });
    vi.mocked(apiService.closeCaseProceduralDeadline).mockReset();
    vi.mocked(apiService.closeCaseProceduralDeadline).mockResolvedValue({
      id: 31,
      clientCaseId: 15,
      deadlineType: 'appeal',
      deadlineTypeLabel: 'Апелляция',
      title: 'Апелляционная жалоба',
      dueAt: '2026-07-01',
      allDay: true,
      status: 'closed',
      statusLabel: 'Закрыт',
      warningDaysBefore: 7,
      sourceType: 'manual',
      notes: '',
      createdAt: '2026-05-21T00:00:00Z',
      closedAt: '2026-05-22T00:00:00Z',
      closeReason: 'Подано',
      isOverdue: false,
      isWarning: false,
    });
    vi.mocked(apiService.getDocumentStatuses).mockReset();
    vi.mocked(apiService.getDocumentStatuses).mockResolvedValue([
      {
        id: 1,
        code: 'Draft',
        name: 'Подготовлен',
        description: '',
        colorHex: '#94A3B8',
        isActive: true,
        useForInvoices: true,
        useForActs: true,
        useForContracts: true,
        sortOrder: 10,
        createdAt: '2026-05-01T00:00:00Z',
      },
      {
        id: 2,
        code: 'Signed',
        name: 'Подписан',
        description: '',
        colorHex: '#10B981',
        isActive: true,
        useForInvoices: true,
        useForActs: true,
        useForContracts: true,
        sortOrder: 20,
        createdAt: '2026-05-01T00:00:00Z',
      },
    ]);
    vi.mocked(apiService.createTaskJournalCaseAutomationTask).mockReset();
    vi.mocked(apiService.createTaskJournalCaseAutomationTask).mockResolvedValue(createTask());
    vi.mocked(apiService.createTaskJournalItem).mockReset();
    vi.mocked(apiService.createTaskJournalItem).mockResolvedValue(createTask());
    vi.mocked(apiService.bulkUpdateCases).mockReset();
    vi.mocked(apiService.bulkUpdateCases).mockResolvedValue({ requested: 1, updated: 1 });
    vi.mocked(apiService.requestCaseAccess).mockReset();
    vi.mocked(apiService.requestCaseAccess).mockResolvedValue({
      clientCaseId: 15,
      requestedAt: '2026-05-18T12:00:00Z',
      requestedByUserId: 7,
      requestedByUserName: 'Кожеваткин Андрей Владимирович',
      reason: 'Нужно посмотреть или продолжить работу по делу.',
      isHiddenFromExecutor: true,
    });
    vi.mocked(apiService.markCaseActivity).mockReset();
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: '2026-05-12T10:15:00Z',
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: null,
      inactivityRemindersEnabled: true,
      eventHistory: '',
    });
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.generateCaseDocumentTemplate).mockResolvedValue({
      id: 8,
      clientCaseId: 15,
      documentKey: 'case-summary',
      version: 2,
      documentType: 'Карточка дела',
      status: 'Подготовлен',
      originalFileName: 'Карточка дела 2026-015.html',
      contentType: 'text/html',
      sizeBytes: 2048,
      uploadedByName: 'Администратор',
      uploadedAt: '2026-05-03T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.uploadCaseDocument).mockReset();
    vi.mocked(apiService.uploadCaseDocument).mockResolvedValue({
      id: 9,
      clientCaseId: 15,
      documentKey: 'claim-upload',
      version: 1,
      documentType: 'Иск',
      status: 'Подготовлен',
      originalFileName: 'claim.pdf',
      contentType: 'application/pdf',
      sizeBytes: 512,
      uploadedByName: 'Администратор',
      uploadedAt: '2026-05-05T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.uploadCaseDocumentVersion).mockReset();
    vi.mocked(apiService.uploadCaseDocumentVersion).mockResolvedValue({
      id: 10,
      clientCaseId: 15,
      documentKey: 'claim',
      version: 2,
      documentType: 'Заявление',
      status: 'Подготовлен',
      originalFileName: 'claim-v2.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      uploadedByName: 'Администратор',
      uploadedAt: '2026-05-06T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.downloadCaseDocument).mockReset();
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({
      blob: new Blob(['claim'], { type: 'application/pdf' }),
      fileName: 'Исковое заявление.pdf',
    });
    vi.mocked(apiService.updateCaseDocument).mockReset();
    vi.mocked(apiService.updateCaseDocument).mockResolvedValue({
      id: 7,
      clientCaseId: 15,
      documentKey: 'claim',
      version: 1,
      documentType: 'Заявление',
      status: 'Подписан',
      originalFileName: 'Заявление.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      sizeBytes: 1024,
      uploadedByName: 'Администратор',
      uploadedAt: '2026-05-02T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.deleteCaseDocument).mockReset();
    vi.mocked(apiService.deleteCaseDocument).mockResolvedValue(undefined);
    vi.mocked(apiService.addCaseHistoryEntry).mockReset();
    vi.mocked(apiService.addCaseHistoryEntry).mockResolvedValue({
      lastViewedAt: '2026-05-01T10:00:00Z',
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
      activityConfirmedAt: '2026-05-02T10:00:00Z',
      activityConfirmedByUserId: 7,
      activityConfirmedByUserName: 'Кожеваткин Андрей Владимирович',
      inactivityRemindersEnabled: true,
      eventHistory: 'Комментарий по делу: Проверить срок. Автор: Кожеваткин Андрей Владимирович.',
    });
    vi.mocked(apiService.getCaseConversation).mockReset();
    vi.mocked(apiService.getCaseConversation).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 15,
        occurredAt: '2026-05-18T10:30:00Z',
        text: 'Проверить документы перед заседанием.',
        authorName: 'Администратор',
        kind: 'comment',
      },
    ]);
    vi.mocked(apiService.addCaseConversationEntry).mockReset();
    vi.mocked(apiService.addCaseConversationEntry).mockResolvedValue([
      {
        id: 2,
        clientCaseId: 15,
        occurredAt: '2026-05-18T11:00:00Z',
        text: 'Коллеги, срок подачи уточнен.',
        authorName: 'Кожеваткин Андрей Владимирович',
        kind: 'message',
      },
      {
        id: 1,
        clientCaseId: 15,
        occurredAt: '2026-05-18T10:30:00Z',
        text: 'Проверить документы перед заседанием.',
        authorName: 'Администратор',
        kind: 'comment',
      },
    ]);
    vi.mocked(apiService.runCaseCourtSync).mockReset();
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValue({
      clientCaseId: 15,
      status: 'manual-check-recorded',
      hasCourtBinding: true,
      message: 'Ручная проверка судебного источника зафиксирована.',
      checkedAt: '2026-05-18T10:00:00Z',
      checkedByUserId: 7,
      checkedByUserName: 'Кожеваткин Андрей Владимирович',
      courtName: 'Арбитражный суд Самарской области',
      courtCaseNumber: 'А55-12345/2026',
      courtUrl: 'https://kad.arbitr.ru/card',
    });
    vi.mocked(apiService.searchCaseCourtSyncCases).mockReset();
    vi.mocked(apiService.searchCaseCourtSyncCases).mockResolvedValue([]);
    vi.mocked(apiService.bindCaseCourtSyncCase).mockReset();
    vi.mocked(apiService.bindCaseCourtSyncCase).mockResolvedValue({
      clientCaseId: 15,
      instance: 'first',
      providerKey: 'sudrf',
      sourceType: 'sudrf',
      courtName: 'Арбитражный суд Самарской области',
      courtCaseNumber: 'А55-12345/2026',
      courtUrl: 'https://kad.arbitr.ru/card',
      judicialUid: '',
      message: 'Судебная карточка привязана к делу.',
      courtInstance: {
        id: 81,
        clientCaseId: 15,
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        courtId: 3,
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        courtUrl: 'https://kad.arbitr.ru/card',
        judgeName: '',
        courtroom: '',
        secretaryName: '',
        notes: '',
        createdAt: '2026-05-18T10:00:00Z',
        complaints: [],
      },
    });
    vi.mocked(apiService.getCaseCourtSyncLogs).mockReset();
    vi.mocked(apiService.getCaseCourtSyncLogs).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 15,
        status: 'manual-check-recorded',
        message: 'Ручная проверка судебного источника зафиксирована.',
        occurredAt: '2026-05-18T10:30:00Z',
        actorName: 'Администратор',
      },
    ]);
    vi.mocked(apiService.getCaseTimeline).mockReset();
    vi.mocked(apiService.getCaseTimeline).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 15,
        occurredAt: '2026-05-18T11:15:00Z',
        text: 'Комментарий по делу: Проверить срок.',
      },
      {
        id: 2,
        clientCaseId: 15,
        occurredAt: '2026-05-18T10:30:00Z',
        text: 'Создано дело.',
      },
      {
        id: 3,
        clientCaseId: 15,
        occurredAt: null,
        text: 'Массовое изменение',
      },
    ]);
    vi.mocked(apiService.getCaseFinance).mockReset();
    vi.mocked(apiService.getCaseFinance).mockResolvedValue({
      caseId: 15,
      caseTitle: 'ЗЕМЦОВ - банкротство',
      ownerName: 'Земцов Виктор Александрович',
      totals: {
        feeAmount: 150000,
        incomeTotal: 50000,
        incomePaid: 35000,
        incomeOutstanding: 15000,
        incomeCount: 2,
        expenseTotal: 10000,
        expensePaid: 5000,
        expenseOutstanding: 5000,
        expenseCount: 1,
        plannedMargin: 40000,
        paidMargin: 30000,
        outstandingBalance: 115000,
      },
      calendar: {
        paymentCount: 1,
        planTotal: 35000,
        paidTotal: 35000,
        outstandingTotal: 0,
        overdueTotal: 0,
        paidCount: 1,
        plannedCount: 0,
        overdueCount: 0,
        cancelledCount: 0,
        nextPaymentDate: null,
      },
      invoices: [
        {
          id: 8,
          number: '8',
          date: '2026-05-01',
          amount: 50000,
          paidAmount: 35000,
          outstandingAmount: 15000,
          status: 'Pending',
          workflowStatus: 'RequiresClientSignature',
          contractId: 77,
          contractNumber: '1-2026',
        },
      ],
      payments: [
        {
          id: 101,
          documentKind: 'Payment',
          type: 'Income',
          amount: 35000,
          paidAmount: 35000,
          outstandingAmount: 0,
          status: 'Completed',
          date: '2026-05-10',
          plannedDate: '2026-05-10',
          paidDate: '2026-05-10',
          invoiceNumber: '8',
          paymentSourceName: 'Касса',
          description: 'Оплата по счету №8',
        },
      ],
      contracts: [
        {
          id: 77,
          number: '1-2026',
          title: 'Банкротство',
          date: '2026-05-01',
          amount: 150000,
          status: 'Concluded',
        },
      ],
    });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getCaseWorkOrder).mockReset();
    vi.mocked(apiService.getCaseWorkOrder).mockResolvedValue(null as unknown as ClientCaseWorkOrder);
    vi.mocked(apiService.createCaseWorkOrder).mockReset();
    vi.mocked(apiService.updateCaseWorkOrder).mockReset();
    vi.mocked(apiService.deleteCaseWorkOrder).mockReset();
    vi.mocked(apiService.addCaseWorkOrderItem).mockReset();
    vi.mocked(apiService.updateCaseWorkOrderItem).mockReset();
    vi.mocked(apiService.deleteCaseWorkOrderItem).mockReset();
    vi.mocked(apiService.createCaseWorkOrderInvoice).mockReset();
    vi.mocked(apiService.createCaseWorkOrderAct).mockReset();
    vi.mocked(apiService.createCaseWorkOrderContract).mockReset();
    vi.mocked(apiService.submitCaseWorkOrderApproval).mockReset();
    vi.mocked(apiService.approveCaseWorkOrder).mockReset();
    vi.mocked(apiService.requestCaseWorkOrderRevision).mockReset();
    vi.mocked(apiService.getCaseWorkOrderPrintHtml).mockReset();
    vi.mocked(apiService.getCaseWorkOrderPrintHtml).mockResolvedValue('<!doctype html><html><body>Заказ-наряд</body></html>');
    vi.mocked(apiService.generateCaseWorkOrderDocument).mockReset();
    vi.mocked(apiService.generateCaseWorkOrderDocument).mockResolvedValue({
      id: 91,
      clientCaseId: 15,
      documentKey: 'work-order-31',
      sourceWorkOrderId: 31,
      version: 1,
      documentType: 'Заказ-наряд',
      status: 'Подготовлен',
      originalFileName: 'case-15-work-order.html',
      contentType: 'text/html; charset=utf-8',
      sizeBytes: 1200,
      uploadedByName: 'Владелец',
      uploadedAt: '2026-05-21T00:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.getObjectAuditHistory).mockReset();
    vi.mocked(apiService.getObjectAuditHistory).mockResolvedValue([]);
    vi.mocked(apiService.addCaseRelatedParty).mockReset();
    vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({
      id: 2,
      clientCaseId: 15,
      sourceType: 'legalEntity',
      sourceId: 25,
      legalEntityId: 25,
      displayName: 'ООО "Процесс"',
      role: 'Третье лицо',
      createdAt: '2026-05-04T10:00:00Z',
    });
    vi.mocked(apiService.deleteCaseRelatedParty).mockReset();
    vi.mocked(apiService.deleteCaseRelatedParty).mockResolvedValue(undefined);
  });

  it('keeps quick case settings in a wrapping responsive grid', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    const settingsGrid = screen.getByRole('button', { name: 'Сохранить' }).parentElement;
    expect(settingsGrid?.className).toContain('md:grid-cols-2');
    expect(settingsGrid?.className).toContain('xl:grid-cols-4');
    expect(settingsGrid?.className).not.toContain('xl:grid-cols-[');
  });

  it('wraps dense court and document forms before the wide desktop breakpoint', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    const tabBar = screen.getByRole('button', { name: 'Обзор' }).parentElement;
    expect(tabBar).toBeTruthy();

    fireEvent.click(within(tabBar!).getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('Судебные события')).toBeTruthy();

    const courtPartyGrid = screen.getByPlaceholderText('Например, ООО Ответчик').closest('label')?.parentElement;
    const courtEventGrid = screen.getByPlaceholderText('Например, предварительное заседание').closest('label')?.parentElement;
    const deadlineGrid = screen.getByPlaceholderText('Например, подать жалобу').closest('label')?.parentElement;
    for (const grid of [courtPartyGrid, courtEventGrid, deadlineGrid]) {
      expect(grid?.className).toContain('lg:grid-cols-2');
      expect(grid?.className).toContain('2xl:grid-cols-[');
      expect(grid?.className).not.toContain('lg:grid-cols-[');
    }

    fireEvent.click(within(tabBar!).getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Загрузить файл')).toBeTruthy();
    const documentUploadGrid = screen.getByRole('button', { name: 'Загрузить' }).parentElement;
    expect(documentUploadGrid?.className).toContain('lg:grid-cols-2');
    expect(documentUploadGrid?.className).toContain('2xl:grid-cols-[');
    expect(documentUploadGrid?.className).not.toContain('lg:grid-cols-[');
  });

  it('loads case summary, tasks, documents and history', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    await waitFor(() => {
      expect(apiService.markCaseActivity).toHaveBeenCalledWith(15, { confirmed: false });
      expect(apiService.markCaseActivity).toHaveBeenCalledTimes(1);
    });
    expect(screen.getByText('2026-015')).toBeTruthy();
    expect(screen.getAllByText('Земцов Виктор Александрович').length).toBeGreaterThan(0);
    expect(screen.getByText(/35\s*000\s*₽ \/ 15\s*000\s*₽/u)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Проверить движение по делу')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'История' }));
    expect(await screen.findByText('Массовое изменение')).toBeTruthy();
    expect(screen.getByText('3 записи')).toBeTruthy();
  });

  it('shows imported court data that moved to the archive as inactive', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        id: 41,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Заседание из судебного источника',
        scheduledAt: '2026-06-12T09:30:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'archived',
        movementStatusLabel: 'В архиве',
        sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
    ]);
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([
      {
        id: 31,
        clientCaseId: 15,
        deadlineType: 'cassation',
        deadlineTypeLabel: 'Кассация',
        title: 'Архивный срок',
        dueAt: '2026-07-01',
        allDay: true,
        status: 'archived',
        statusLabel: 'В архиве',
        warningDaysBefore: 7,
        sourceType: 'sudrf',
        notes: '',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
        isWarning: false,
      },
    ]);
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      {
        id: 7,
        clientCaseId: 15,
        documentKey: 'sudrf-act',
        version: 1,
        documentType: 'Судебный акт',
        status: 'archived',
        originalFileName: 'Архивное определение.pdf',
        contentType: 'application/pdf',
        sizeBytes: 1024,
        uploadedByName: 'Судебный источник',
        uploadedAt: '2026-05-02T10:00:00Z',
        sourceType: 'sudrf',
        isLatestVersion: true,
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('Заседание из судебного источника')).toBeTruthy();
    expect(screen.getByText('Закрыто и в архиве: 1')).toBeTruthy();
    expect(screen.queryByText('Архивный срок')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Архивное определение.pdf')).toBeTruthy();
    expect(screen.getByText(/Судебный акт · v1 · В архиве/u)).toBeTruthy();
  });

  it('hides case tasks and does not load task data when task access is closed', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewTasks={false} canCreateTasks={false} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Задачи' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Создать задачу/u })).toBeNull();
    expect(apiService.getTaskJournalItems).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Задача' })).toBeNull();
    expect(apiService.getTaskJournalItems).not.toHaveBeenCalled();
  });

  it('keeps case tasks read-only when task creation is closed', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewTasks canCreateTasks={false} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Создать задачу/u })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Проверить движение по делу')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Создать$/u })).toBeNull();
  });

  it('shows case activity and document upload timestamps in the profile time zone', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      lastViewedByResponsibleAt: '2026-05-12T10:15:00Z',
      lastViewedByResponsibleUserName: 'Кожеваткин Андрей',
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return value.includes('14:15') && value.includes('Кожеваткин');
      }).length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: /Документы/u }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return value.includes('Администратор') && value.includes('02.05.2026') && value.includes('14:00');
      }).length).toBeGreaterThan(0);
    });
  });

  it('shows and saves case hearing times with the profile time zone', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      nextHearingAt: '2026-06-05T12:00:00',
    }));
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        id: 41,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Судебное заседание',
        scheduledAt: '2026-06-05T12:00:00',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return value.includes('Заседание') && value.includes('05.06.2026') && value.includes('16:00');
      }).length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('Судебное заседание')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return value.includes('05.06.2026') && value.includes('16:00') && value.includes('Первая инстанция');
      }).length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    const hearingInput = await screen.findByLabelText('Заседание') as HTMLInputElement;
    expect(hearingInput.value).toBe('2026-06-05T16:00');

    fireEvent.change(hearingInput, { target: { value: '2026-06-05T17:30' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        15,
        expect.objectContaining({
          nextHearingAt: '2026-06-05T13:30:00.000Z',
        }),
      );
    });
  });

  it('opens full case editing from the detail header and saves changes', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(await screen.findByRole('heading', { name: 'Редактирование дела' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Карточка дела' })).toBeTruthy();

    const titleInput = await screen.findByLabelText('Название дела');
    fireEvent.change(titleInput, { target: { value: 'ЗЕМЦОВ - обновленная карточка' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        15,
        expect.objectContaining({
          title: 'ЗЕМЦОВ - обновленная карточка',
          clientId: 12,
          legalEntityId: null,
          status: 'Open',
        }),
      );
    });
    expect(await screen.findByText('Карточка дела обновлена.')).toBeTruthy();
  });

  it('offers stage tasks and procedural deadlines after full case stage change', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      stage: 'Первая инстанция',
      appealDeadlineAt: null,
      motionDeadlineAt: null,
      objectionDeadlineAt: null,
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));

    const stageInput = await screen.findByLabelText('Стадия') as HTMLInputElement;
    fireEvent.change(stageInput, { target: { value: 'Апелляция' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    expect(await screen.findByText('Этап дела изменён')).toBeTruthy();
    expect(screen.getByText('Первая инстанция → Апелляция')).toBeTruthy();
    expect(screen.getByText('Проверить срок и материалы для апелляции')).toBeTruthy();
    expect(screen.getByText('Добавить срок апелляции')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Создать задачу: Проверить срок и материалы для апелляции/u }));

    await waitFor(() => {
      expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: 15,
        title: 'Проверить срок и материалы для апелляции',
        priority: 'urgent',
        tags: expect.arrayContaining(['апелляция']),
        description: expect.stringContaining('Этап: Апелляция'),
      }));
    });

    fireEvent.click(screen.getByRole('button', { name: /Подготовить срок: Добавить срок апелляции/u }));

    expect(await screen.findByText('Процессуальные сроки 2.0')).toBeTruthy();
    expect((screen.getByPlaceholderText('Например, подать жалобу') as HTMLInputElement).value).toBe('Добавить срок апелляции');
    expect(screen.getByDisplayValue('Проверьте дату решения суда и зафиксируйте срок подачи апелляционной жалобы.')).toBeTruthy();
  });

  it('offers stage actions after quick stage save in the header', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      stage: 'Апелляция',
      cassationDeadlineAt: null,
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Стадия дела'), { target: { value: 'Кассация' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith(expect.objectContaining({
        ids: [15],
        stage: 'Кассация',
      }));
    });
    expect(await screen.findByText('Этап дела изменён')).toBeTruthy();
    expect(screen.getByText('Апелляция → Кассация')).toBeTruthy();
    expect(screen.getByText('Проверить срок и материалы для кассации')).toBeTruthy();
    expect(screen.getByText('Добавить срок кассации')).toBeTruthy();
  });

  it('shows inactivity and court sync indicators in the summary panel', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      lastTaskAt: new Date().toISOString(),
      eventHistory: '18.05.2026 10:30: Ручная проверка судебного источника зафиксирована.',
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Сегодня')).toBeTruthy();
    expect(screen.getByText('Проверен вручную')).toBeTruthy();
  });

  it('loads linked finance documents for the finance tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    const financeTab = screen.getAllByRole('button', { name: 'Финансы' }).at(-1);
    expect(financeTab).toBeTruthy();
    fireEvent.click(financeTab!);

    await waitFor(() => {
      expect(apiService.getCaseFinance).toHaveBeenCalledWith(15);
    });
    expect(await screen.findByText('Договор №1-2026')).toBeTruthy();
    expect(screen.getByText('Счет №8')).toBeTruthy();
    expect(screen.getAllByText('Оплата по счету №8').length).toBeGreaterThan(0);
    expect(screen.getByText('Календарь оплат')).toBeTruthy();
    expect(screen.getByText(/май 2026/u)).toBeTruthy();
    expect(screen.getByText('План оплат')).toBeTruthy();
    expect(screen.getByText('Следующая дата')).toBeTruthy();
    expect(screen.getByText('Просрочка')).toBeTruthy();
    expect(screen.getAllByText('Оплачено').length).toBeGreaterThan(0);
    expect(screen.getByText(/115\s*000\s*₽/u)).toBeTruthy();
  });

  it('hides finance tab and skips finance loading without finance permission', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'finance' }),
    );

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewFinance={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    expect(screen.queryByRole('button', { name: 'Финансы' })).toBeNull();
    expect(apiService.getCaseFinance).not.toHaveBeenCalled();
  });

  it('hides case sidebar finance shortcuts without finance permission', async () => {
    const onCreateInvoice = vi.fn();
    const onCreatePayment = vi.fn();

    render(
      <CaseDetailPage
        caseId={15}
        onBack={vi.fn()}
        canViewFinance={false}
        onCreateInvoice={onCreateInvoice}
        onCreatePayment={onCreatePayment}
      />,
    );

    await screen.findByText('Клиент по делу');

    expect(screen.queryByRole('button', { name: 'Счет' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Платеж' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Финансы дела' })).toBeNull();
    expect(onCreateInvoice).not.toHaveBeenCalled();
    expect(onCreatePayment).not.toHaveBeenCalled();
  });

  it('hides work order document creation actions without finance permission', async () => {
    vi.mocked(apiService.getCaseWorkOrder).mockResolvedValue({
      id: 31,
      clientCaseId: 15,
      title: 'Предварительный расчет',
      status: 'approved',
      statusLabel: 'Согласован',
      currency: 'RUB',
      notes: null,
      totalAmount: 24000,
      items: [
        {
          id: 41,
          clientCaseWorkOrderId: 31,
          serviceName: 'Подготовка заявления',
          description: 'Пакет документов',
          executorUserId: 7,
          executorName: 'Кожеваткин Андрей Владимирович',
          dueDate: '2026-06-01',
          quantity: 2,
          unitPrice: 12000,
          totalAmount: 24000,
          sortOrder: 1,
        },
      ],
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewFinance={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Заказ-наряд' }));
    await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledWith(15));

    expect(await screen.findByDisplayValue('Предварительный расчет')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Создать счет' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Создать акт' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Создать договор' })).toBeNull();
  });

  it('opens the work order print form from the work order tab', async () => {
    vi.mocked(apiService.getCaseWorkOrder).mockResolvedValue({
      id: 31,
      clientCaseId: 15,
      title: 'Предварительный расчет',
      status: 'approved',
      statusLabel: 'Согласован',
      currency: 'RUB',
      notes: null,
      totalAmount: 24000,
      items: [
        {
          id: 41,
          clientCaseWorkOrderId: 31,
          serviceName: 'Подготовка заявления',
          description: 'Пакет документов',
          executorUserId: 7,
          executorName: 'Кожеваткин Андрей Владимирович',
          dueDate: '2026-06-01',
          quantity: 2,
          unitPrice: 12000,
          totalAmount: 24000,
          sortOrder: 1,
        },
      ],
    });
    vi.mocked(apiService.getObjectAuditHistory).mockImplementation(async (objectType, objectId) => [
      {
        id: objectType === 'client-case-work-order-item' ? 92 : 91,
        objectType,
        objectId,
        action: 'Updated',
        description: objectType === 'client-case-work-order-item' ? 'Обновлена услуга' : 'Обновлен заказ-наряд',
        actorUserName: 'Администратор',
        metadataJson: objectType === 'client-case-work-order-item'
          ? '{"changedFields":["UnitPrice"],"reason":"Уточнена цена услуги"}'
          : '{"changedFields":["Status"],"reason":"Отправлено на согласование"}',
        createdAt: '2026-05-20T08:10:00Z',
      },
    ]);
    const createObjectUrl = vi.fn(() => 'blob:case-work-order');
    const revokeObjectUrl = vi.fn();
    const openSpy = vi.spyOn(window, 'open').mockReturnValue(window);
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });

    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledWith(15));
    await waitFor(() => expect(apiService.getObjectAuditHistory).toHaveBeenCalledWith('client-case-work-order', 31));
    expect(await screen.findByText('Причина: Отправлено на согласование')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'История услуги Подготовка заявления' }));
    await waitFor(() => expect(apiService.getObjectAuditHistory).toHaveBeenCalledWith('client-case-work-order-item', 41));
    expect(await screen.findByText('Причина: Уточнена цена услуги')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Печать' }));

    await waitFor(() => {
      expect(apiService.getCaseWorkOrderPrintHtml).toHaveBeenCalledWith(15);
      expect(createObjectUrl).toHaveBeenCalledTimes(1);
      expect(openSpy).toHaveBeenCalledWith('blob:case-work-order', '_blank', 'noopener,noreferrer');
    });
  });

  it('locks work order editing after approval', async () => {
    vi.mocked(apiService.getCaseWorkOrder).mockResolvedValue({
      id: 31,
      clientCaseId: 15,
      title: 'Предварительный расчет',
      status: 'approved',
      statusLabel: 'Согласован',
      currency: 'RUB',
      notes: 'Согласовано клиентом',
      totalAmount: 24000,
      items: [
        {
          id: 41,
          clientCaseWorkOrderId: 31,
          serviceName: 'Подготовка заявления',
          description: 'Пакет документов',
          executorUserId: 7,
          executorName: 'Кожеваткин Андрей Владимирович',
          dueDate: '2026-06-01',
          quantity: 2,
          unitPrice: 12000,
          totalAmount: 24000,
          sortOrder: 1,
        },
      ],
    });

    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(await screen.findByText('Согласованный заказ-наряд нельзя редактировать. Можно создать счет, акт, договор или вернуть расчет на доработку.')).toBeTruthy();

    const saveButtons = screen.getAllByRole('button', { name: 'Сохранить' }) as HTMLButtonElement[];
    expect(saveButtons.some((button) => button.disabled)).toBe(true);
    expect((screen.getByRole('button', { name: 'Удалить' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Добавить' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByLabelText('Название') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getAllByLabelText('Услуга')[0] as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('Сохранить услугу') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'На доработку' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Создать счет' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('submits and approves a work order from the work order tab', async () => {
    const draftWorkOrder = {
      id: 31,
      clientCaseId: 15,
      title: 'Предварительный расчет',
      status: 'draft',
      statusLabel: 'Черновик',
      currency: 'RUB',
      notes: null,
      totalAmount: 24000,
      items: [
        {
          id: 41,
          clientCaseWorkOrderId: 31,
          serviceName: 'Подготовка заявления',
          description: 'Пакет документов',
          executorUserId: 7,
          executorName: 'Кожеваткин Андрей Владимирович',
          dueDate: '2026-06-01',
          quantity: 2,
          unitPrice: 12000,
          totalAmount: 24000,
          sortOrder: 1,
        },
      ],
    };
    vi.mocked(apiService.getCaseWorkOrder).mockResolvedValue(draftWorkOrder);
    vi.mocked(apiService.submitCaseWorkOrderApproval).mockResolvedValue({
      ...draftWorkOrder,
      status: 'pending_approval',
      statusLabel: 'На согласовании',
      approvalRequestedByUserName: 'Администратор',
      approvalRequestedAt: '2026-05-20T08:00:00Z',
      approvalComment: 'Проверить расчет',
    });
    vi.mocked(apiService.approveCaseWorkOrder).mockResolvedValue({
      ...draftWorkOrder,
      status: 'approved',
      statusLabel: 'Согласован',
      approvalDecisionByUserName: 'Администратор',
      approvalDecisionAt: '2026-05-20T08:05:00Z',
      approvalComment: 'Согласовано',
    });

    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(await screen.findByLabelText('Комментарий к согласованию'), { target: { value: 'Проверить расчет' } });
    fireEvent.click(screen.getByRole('button', { name: 'На согласование' }));

    await waitFor(() => {
      expect(apiService.submitCaseWorkOrderApproval).toHaveBeenCalledWith(15, { comment: 'Проверить расчет' });
      expect(screen.getByText('Заказ-наряд отправлен на согласование.')).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Комментарий к согласованию'), { target: { value: 'Согласовано' } });
    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));

    await waitFor(() => {
      expect(apiService.approveCaseWorkOrder).toHaveBeenCalledWith(15, { comment: 'Согласовано' });
      expect(screen.getByText('Заказ-наряд согласован.')).toBeTruthy();
    });
  });

  it('starts invoice and payment creation from the finance tab', async () => {
    const onCreateInvoice = vi.fn();
    const onCreatePayment = vi.fn();
    render(
      <CaseDetailPage
        caseId={15}
        onBack={vi.fn()}
        onCreateInvoice={onCreateInvoice}
        onCreatePayment={onCreatePayment}
      />,
    );

    await screen.findByText('ЗЕМЦОВ - банкротство');
    const financeTab = screen.getAllByRole('button', { name: 'Финансы' }).at(-1);
    expect(financeTab).toBeTruthy();
    fireEvent.click(financeTab!);

    fireEvent.click(await screen.findByRole('button', { name: 'Новый счет' }));
    fireEvent.click(screen.getByRole('button', { name: 'Новый платеж' }));

    expect(onCreateInvoice).toHaveBeenCalledWith(expect.objectContaining({ id: 15 }));
    expect(onCreatePayment).toHaveBeenCalledWith(expect.objectContaining({ id: 15 }));
  });

  it('shows the client summary sidebar with masked contacts and quick actions', async () => {
    const baseCase = createCase();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      client: {
        ...baseCase.client!,
        phone: '89332428978',
        email: 'zemcov@example.com',
        company: 'ООО "Земцов"',
        address: '443001, Самара, ул. Полевая, д. 10, кв. 7',
        legalEntity: {
          id: 25,
          shortName: 'ООО "Земцов"',
          fullName: 'Общество с ограниченной ответственностью "Земцов"',
          inn: '6310000000',
          address: '443001, Самара, ул. Полевая, д. 10',
          clientsCount: 1,
          createdAt: '2026-05-01T00:00:00Z',
        },
      },
    }));
    const onOpenClient = vi.fn();
    const onCreateInvoice = vi.fn();
    const onCreatePayment = vi.fn();

    render(
      <CaseDetailPage
        caseId={15}
        onBack={vi.fn()}
        onOpenClient={onOpenClient}
        onCreateInvoice={onCreateInvoice}
        onCreatePayment={onCreatePayment}
      />,
    );

    expect(await screen.findByText('Клиент по делу')).toBeTruthy();
    expect(screen.queryByText('89332428978')).toBeNull();
    expect(screen.queryByText('6310000000')).toBeNull();
    expect(screen.queryByText('443001, Самара, ул. Полевая, д. 10, кв. 7')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Показать' }));
    expect(screen.getByText('89332428978')).toBeTruthy();
    expect(screen.getByText('zemcov@example.com')).toBeTruthy();
    expect(screen.getByText('6310000000')).toBeTruthy();
    expect(screen.getByText('443001, Самара, ул. Полевая, д. 10, кв. 7')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Карточка клиента' }));
    fireEvent.click(screen.getByRole('button', { name: 'Счет' }));
    fireEvent.click(screen.getByRole('button', { name: 'Платеж' }));

    expect(onOpenClient).toHaveBeenCalledWith(12, 15);
    expect(onCreateInvoice).toHaveBeenCalledWith(expect.objectContaining({ id: 15 }));
    expect(onCreatePayment).toHaveBeenCalledWith(expect.objectContaining({ id: 15 }));
  });

  it('keeps sensitive owner and participant contacts locked for read-only case access', async () => {
    const baseCase = createCase();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      client: {
        ...baseCase.client!,
        phone: '89332428978',
        email: 'zemcov@example.com',
        address: '443001, Самара, ул. Полевая, д. 10, кв. 7',
        legalEntity: {
          id: 25,
          shortName: 'ООО "Земцов"',
          fullName: 'Общество с ограниченной ответственностью "Земцов"',
          inn: '6310000000',
          address: '443001, Самара, ул. Полевая, д. 10',
          clientsCount: 1,
          createdAt: '2026-05-01T00:00:00Z',
        },
      },
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);

    expect(await screen.findByText('Клиент по делу')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Показать' })).toBeNull();
    expect(screen.getByText('Скрыто по роли')).toBeTruthy();
    expect(screen.queryByText('89332428978')).toBeNull();
    expect(screen.queryByText('zemcov@example.com')).toBeNull();
    expect(screen.queryByText('6310000000')).toBeNull();
    expect(screen.queryByText('443001, Самара, ул. Полевая, д. 10, кв. 7')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Позвонить' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Написать email' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Участники' }));
    expect((await screen.findByRole('button', { name: 'Контакты скрыты по роли' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByText('+79991234567')).toBeNull();
    expect(screen.queryByText('zemtsov@example.test')).toBeNull();
  });

  it('keeps sensitive data locked when backend denies reveal permission', async () => {
    const baseCase = createCase();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      canRevealSensitiveData: false,
      client: {
        ...baseCase.client!,
        phone: '89332428978',
        email: 'zemcov@example.com',
        address: '443001, Самара, ул. Полевая, д. 10, кв. 7',
        legalEntity: {
          id: 25,
          shortName: 'ООО "Земцов"',
          fullName: 'Общество с ограниченной ответственностью "Земцов"',
          inn: '6310000000',
          address: '443001, Самара, ул. Полевая, д. 10',
          clientsCount: 1,
          createdAt: '2026-05-01T00:00:00Z',
        },
      },
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('Клиент по делу')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Показать' })).toBeNull();
    expect(screen.getByText('Скрыто по роли')).toBeTruthy();
    expect(screen.queryByText('89332428978')).toBeNull();
    expect(screen.queryByText('zemcov@example.com')).toBeNull();
    expect(screen.queryByText('6310000000')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Участники' }));
    expect((await screen.findByRole('button', { name: 'Контакты скрыты по роли' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByText('+79991234567')).toBeNull();
    expect(screen.queryByText('zemtsov@example.test')).toBeNull();
  });

  it('creates a regular task from the case card', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /Создать задачу/u }));

    await waitFor(() => {
      expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: 15,
        clientId: 12,
        title: expect.stringContaining('ЗЕМЦОВ - банкротство'),
        type: 'task',
      }));
      expect(apiService.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
      expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    });
  });

  it('creates a stage template task from the case tasks tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));

    expect(await screen.findByText('Подсказки по этапу')).toBeTruthy();
    expect(screen.getByText('Подготовиться к заседанию первой инстанции')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Создать по шаблону/u }));

    await waitFor(() => {
      expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: 15,
        clientId: 12,
        title: 'Подготовиться к заседанию первой инстанции',
        priority: 'high',
        tags: expect.arrayContaining(['суд', 'первая инстанция']),
        description: expect.stringContaining('Этап: Судебное производство'),
      }));
      expect(apiService.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
      expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    });
  });

  it('filters case tasks and creates a task from the tasks tab', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({
      items: [
        createTask({ id: 101, title: 'Будущая задача', status: 'inProgress', dueAt: '2100-01-01T10:00:00Z' }),
        createTask({ id: 102, title: 'Просроченная задача', status: 'inProgress', dueAt: '2020-01-01T10:00:00Z' }),
        createTask({ id: 103, title: 'Закрытая задача', status: 'done', dueAt: '2026-05-01T10:00:00Z' }),
      ],
      total: 3,
      page: 1,
      pageSize: 50,
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Будущая задача')).toBeTruthy();
    expect(screen.getByText('Просроченная задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Просроченные/u }));
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(screen.getByText('Просроченная задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Закрытые/u }));
    expect(screen.getByText('Закрытая задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /^Создать$/u }));
    await waitFor(() => {
      expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: 15,
        clientId: 12,
      }));
      expect(apiService.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
    });
  });

  it('restores the last opened tab and task filter for the case card', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({
      items: [
        createTask({ id: 101, title: 'Будущая задача', status: 'inProgress', dueAt: '2100-01-01T10:00:00Z' }),
        createTask({ id: 102, title: 'Просроченная задача', status: 'inProgress', dueAt: '2020-01-01T10:00:00Z' }),
      ],
      total: 2,
      page: 1,
      pageSize: 50,
    });

    const firstRender = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Будущая задача')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Просроченные/u }));
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(screen.getByText('Просроченная задача')).toBeTruthy();
    firstRender.unmount();

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Просроченная задача')).toBeTruthy();
    expect(screen.queryByText('Будущая задача')).toBeNull();
  });

  it('shows and adds internal case conversation messages', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Переписка' }));

    expect(await screen.findByText('Проверить документы перед заседанием.')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Сообщение во внутреннюю переписку дела'), {
      target: { value: 'Коллеги, срок подачи уточнен.' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Отправить/u }));

    await waitFor(() => {
      expect(apiService.addCaseConversationEntry).toHaveBeenCalledWith(15, {
        text: 'Коллеги, срок подачи уточнен.',
      });
    });
    expect(await screen.findByText('Коллеги, срок подачи уточнен.')).toBeTruthy();
    expect(screen.getByText('Сообщение добавлено в переписку по делу.')).toBeTruthy();
  });

  it('adds files to internal case conversation messages', async () => {
    const file = new File(['scan body'], 'scan.pdf', { type: 'application/pdf' });
    vi.mocked(apiService.addCaseConversationEntry).mockResolvedValueOnce([
      {
        id: 2,
        clientCaseId: 15,
        occurredAt: '2026-05-18T11:00:00Z',
        text: 'Прикрепил скан заявления.',
        authorName: 'Кожеваткин Андрей Владимирович',
        kind: 'message',
        attachments: [
          {
            documentId: 19,
            fileName: 'scan.pdf',
            contentType: 'application/pdf',
            sizeBytes: file.size,
            uploadedByName: 'Кожеваткин Андрей Владимирович',
            uploadedAt: '2026-05-18T11:00:00Z',
          },
        ],
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Переписка' }));
    fireEvent.change(screen.getByLabelText('Сообщение во внутреннюю переписку дела'), {
      target: { value: 'Прикрепил скан заявления.' },
    });
    fireEvent.change(screen.getByLabelText('Файлы к сообщению'), {
      target: { files: [file] },
    });
    expect(screen.getByText('scan.pdf')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Отправить/u }));

    await waitFor(() => {
      expect(apiService.addCaseConversationEntry).toHaveBeenCalledWith(15, {
        text: 'Прикрепил скан заявления.',
        files: [file],
      });
    });
    expect(await screen.findByText('Прикрепил скан заявления.')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Скачать вложение scan.pdf/u })).toBeTruthy();
  });

  it('shows an error when internal case conversation file upload fails', async () => {
    const file = new File(['large body'], 'large.pdf', { type: 'application/pdf' });
    vi.mocked(apiService.addCaseConversationEntry).mockRejectedValueOnce(new Error('Файл вложения не должен превышать 25 МБ.'));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Переписка' }));
    fireEvent.change(screen.getByLabelText('Сообщение во внутреннюю переписку дела'), {
      target: { value: 'Пробую отправить файл.' },
    });
    fireEvent.change(screen.getByLabelText('Файлы к сообщению'), {
      target: { files: [file] },
    });
    fireEvent.click(screen.getByRole('button', { name: /Отправить/u }));

    expect(await screen.findByText('Файл вложения не должен превышать 25 МБ.')).toBeTruthy();
    expect(screen.getByText('large.pdf')).toBeTruthy();
  });

  it('keeps internal case conversation read-only without edit access', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Переписка' }));

    expect(await screen.findByText('Проверить документы перед заседанием.')).toBeTruthy();
    expect(screen.getByText('Переписка доступна только для просмотра.')).toBeTruthy();
    expect(screen.queryByLabelText('Сообщение во внутреннюю переписку дела')).toBeNull();
    expect(screen.queryByRole('button', { name: /Отправить/u })).toBeNull();
  });

  it('updates status, stage, priority and responsible from the case card header', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(screen.getByLabelText('Статус дела'), { target: { value: 'OnHold' } });
    fireEvent.change(screen.getByLabelText('Стадия дела'), { target: { value: 'Апелляция' } });
    fireEvent.change(screen.getByLabelText('Приоритет дела'), { target: { value: 'urgent' } });
    fireEvent.change(screen.getByLabelText('Ответственный по делу'), { target: { value: '8' } });
    fireEvent.click(screen.getByLabelText('Скрыть дело от исполнителя'));
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/u }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({
        ids: [15],
        status: 'OnHold',
        priority: 'urgent',
        stage: 'Апелляция',
        responsibleUserId: 8,
        isHiddenFromExecutor: true,
        inactivityRemindersEnabled: true,
        inactivityRemindersDisabledReason: '',
      });
      expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    });
  });

  it('saves the inactivity control exclusion reason from the new case card', async () => {
    dialogMock.prompt.mockResolvedValueOnce('Ожидаем решение клиента');
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByLabelText('Учитывать дело в автоконтроле'));

    expect(await screen.findByText('Ожидаем решение клиента')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/u }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith(expect.objectContaining({
        ids: [15],
        inactivityRemindersEnabled: false,
        inactivityRemindersDisabledReason: 'Ожидаем решение клиента',
      }));
    });
  });

  it('offers an access request when the case is hidden or unavailable', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValueOnce(new Error('HTTP 403 Forbidden - Дело скрыто от исполнителя.'));
    dialogMock.prompt.mockResolvedValueOnce('Нужно проверить сроки');

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText(/Дело скрыто от исполнителя/u)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Запросить доступ/u }));

    await waitFor(() => {
      expect(apiService.requestCaseAccess).toHaveBeenCalledWith(15, { reason: 'Нужно проверить сроки' });
    });
    expect(await screen.findByText(/Запрос доступа отправлен/u)).toBeTruthy();
  });

  it('archives the case through the header action', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /Архив/u }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({ ids: [15], status: 'Closed' });
    });
  });

  it('generates a case document template from the case card', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Документ$/u }));

    await waitFor(() => {
      expect(apiService.generateCaseDocumentTemplate).toHaveBeenCalledWith(15, {
        templateKey: 'case-summary',
        documentType: 'Карточка дела',
        status: 'Подготовлен',
      });
      expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(1);
    });
  });

  it('generates a selected document template from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.change(await screen.findByLabelText('Шаблон документа дела'), { target: { value: 'claim' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сформировать$/u }));

    await waitFor(() => {
      expect(apiService.generateCaseDocumentTemplate).toHaveBeenCalledWith(15, {
        templateKey: 'claim',
        documentType: 'Иск',
        status: 'Подготовлен',
      });
    });
    expect(await screen.findByText('Документ по шаблону «Исковое заявление» сформирован.')).toBeTruthy();
  });

  it('filters case documents by search, type, status and latest version', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      {
        id: 7,
        clientCaseId: 15,
        documentKey: 'claim',
        version: 1,
        documentType: 'Заявление',
        status: 'Черновик',
        originalFileName: 'Иск v1.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        sizeBytes: 1024,
        uploadedByName: 'Администратор',
        uploadedAt: '2026-05-02T10:00:00Z',
        isLatestVersion: false,
      },
      {
        id: 8,
        clientCaseId: 15,
        documentKey: 'claim',
        version: 2,
        documentType: 'Заявление',
        status: 'Подписан',
        originalFileName: 'Иск v2.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByName: 'Юрист',
        uploadedAt: '2026-05-03T10:00:00Z',
        isLatestVersion: true,
      },
      {
        id: 9,
        clientCaseId: 15,
        documentKey: 'evidence',
        version: 1,
        documentType: 'Доказательство',
        status: 'Готово',
        originalFileName: 'Фото.jpg',
        contentType: 'image/jpeg',
        sizeBytes: 4096,
        uploadedByName: 'Администратор',
        uploadedAt: '2026-05-04T10:00:00Z',
        isLatestVersion: true,
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Иск v1.docx')).toBeTruthy();
    expect(screen.getByText('Иск v2.pdf')).toBeTruthy();
    expect(screen.getByText('Фото.jpg')).toBeTruthy();
    expect(screen.getAllByText('История версий: v1, v2 · последняя v2')).toHaveLength(2);
    expect(screen.getByText('Не актуальная версия')).toBeTruthy();

    fireEvent.click(screen.getByLabelText('Только актуальные версии'));
    expect(screen.queryByText('Иск v1.docx')).toBeNull();
    expect(screen.getByText('Иск v2.pdf')).toBeTruthy();
    expect(screen.getByText('Фото.jpg')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Фильтр документов по типу'), { target: { value: 'Заявление' } });
    expect(screen.getByText('Иск v2.pdf')).toBeTruthy();
    expect(screen.queryByText('Фото.jpg')).toBeNull();

    fireEvent.change(screen.getByLabelText('Фильтр документов по статусу'), { target: { value: 'Подписан' } });
    expect(screen.getByText('Иск v2.pdf')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Поиск документов дела'), { target: { value: 'фото' } });
    expect(screen.queryByText('Иск v2.pdf')).toBeNull();
    expect(screen.getByText('По текущим фильтрам документов нет.')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Фильтр документов по типу'), { target: { value: 'all' } });
    fireEvent.change(screen.getByLabelText('Фильтр документов по статусу'), { target: { value: 'all' } });
    expect(screen.getByText('Фото.jpg')).toBeTruthy();
  });

  it('uploads a document file from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));

    const file = new File(['claim'], 'claim.pdf', { type: 'application/pdf' });
    fireEvent.change(await screen.findByLabelText('Файл документа'), { target: { files: [file] } });
    fireEvent.change(screen.getByLabelText('Тип документа'), { target: { value: 'Иск' } });
    fireEvent.change(screen.getByLabelText('Статус документа'), { target: { value: 'Подготовлен' } });
    fireEvent.click(screen.getByRole('button', { name: /^Загрузить$/u }));

    await waitFor(() => {
      expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(15, file, {
        documentType: 'Иск',
        status: 'Подготовлен',
      });
      expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(2);
    });
    expect(await screen.findByText('Документ «claim.pdf» загружен.')).toBeTruthy();
  });

  it('uploads a new case document version from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));

    const file = new File(['claim-v2'], 'claim-v2.pdf', { type: 'application/pdf' });
    fireEvent.change(await screen.findByLabelText('Загрузить новую версию документа Заявление.docx'), { target: { files: [file] } });

    await waitFor(() => {
      expect(apiService.uploadCaseDocumentVersion).toHaveBeenCalledWith(7, file, {
        documentType: 'Заявление',
        status: 'Готово',
      });
      expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(2);
      expect(screen.getByText('Новая версия документа «Заявление.docx» загружена.')).toBeTruthy();
    });
  });

  it('downloads a case document from the documents tab', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Скачать документ Заявление.docx' }));

    await waitFor(() => {
      expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7);
      expect(createObjectUrl).toHaveBeenCalledTimes(1);
      expect(clickSpy).toHaveBeenCalledTimes(1);
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:case-document');
      expect(screen.getByText('Документ «Заявление.docx» подготовлен к скачиванию.')).toBeTruthy();
    });
  });

  it('opens a case document preview from the documents tab', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document-preview');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));

    expect(await screen.findByText('Предпросмотр документа')).toBeTruthy();
    expect(screen.getByTitle('Предпросмотр Исковое заявление.pdf')).toBeTruthy();
    expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7);
    expect(createObjectUrl).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Закрыть предпросмотр' }));

    await waitFor(() => {
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:case-document-preview');
    });
  });

  it('renders generated HTML documents through a sandboxed srcDoc preview', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document-html-preview');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValueOnce({
      blob: new Blob(['<!doctype html><html><body><h1>Заказ-наряд</h1></body></html>'], { type: 'text/html' }),
      fileName: 'Заказ-наряд.html',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));

    const frame = await screen.findByTitle('Предпросмотр Заказ-наряд.html');
    expect(frame.getAttribute('srcdoc')).toContain('<h1>Заказ-наряд</h1>');
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.getAttribute('src')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Закрыть предпросмотр' }));

    await waitFor(() => {
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:case-document-html-preview');
    });
  });

  it('renders text documents without relying on a blob iframe', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document-text-preview');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValueOnce({
      blob: new Blob(['# Карточка дела\n\nТекстовая версия документа.'], { type: 'text/markdown' }),
      fileName: 'README.md',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));

    expect(await screen.findByText(/Текстовая версия документа/)).toBeTruthy();
    expect(screen.queryByTitle('Предпросмотр README.md')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Закрыть предпросмотр' }));

    await waitFor(() => {
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:case-document-text-preview');
    });
  });

  it('shows a human-readable Yandex Disk error without HTTP internals', async () => {
    vi.mocked(apiService.uploadCaseDocumentToYandexDisk).mockRejectedValueOnce(
      new Error('HTTP 400 Bad Request at http://localhost:5080/api/yandex-disk/upload - Включите правила синхронизации CRM в разделе Я.Диск.'),
    );

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' }));

    expect(await screen.findByText('Включите правила синхронизации CRM в разделе Я.Диск.')).toBeTruthy();
    expect(screen.queryByText(/HTTP 400 Bad Request/)).toBeNull();
  });

  it('shows case documents in read-only mode without mutation actions', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));

    expect(await screen.findByText(/Документы доступны только для просмотра/u)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Сформировать$/u })).toBeNull();
    expect(screen.queryByLabelText('Файл документа')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить документ Заявление.docx' })).toBeNull();
    expect(screen.queryByLabelText('Загрузить новую версию документа Заявление.docx')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить документ Заявление.docx' })).toBeNull();
    expect((screen.getByRole('button', { name: 'Скачать документ Заявление.docx' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('hides document delete action when case delete permission is disabled', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canDelete={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));

    expect(await screen.findByRole('button', { name: 'Сохранить документ Заявление.docx' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Удалить документ Заявление.docx' })).toBeNull();
  });

  it('updates and deletes a case document from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.change(await screen.findByLabelText('Статус документа Заявление.docx'), { target: { value: 'Подписан' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить документ Заявление.docx' }));

    await waitFor(() => {
      expect(apiService.updateCaseDocument).toHaveBeenCalledWith(15, 7, {
        documentType: 'Заявление',
        status: 'Подписан',
      });
      expect(screen.getByText('Документ «Заявление.docx» обновлен.')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Удалить документ Заявление.docx' }));

    await waitFor(() => {
      expect(dialogMock.confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Удалить документ?', variant: 'danger' }));
      expect(apiService.deleteCaseDocument).toHaveBeenCalledWith(15, 7);
      expect(screen.getByText('Документ «Заявление.docx» удален.')).toBeTruthy();
    });
  });

  it('adds a related participant from the participants tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    await waitFor(() => {
      expect((screen.getByLabelText('Участник дела') as HTMLSelectElement).options.length).toBeGreaterThan(1);
    });
    fireEvent.change(screen.getByLabelText('Участник дела'), { target: { value: '25' } });
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: 'Третье лицо' } });
    fireEvent.change(screen.getByLabelText('Основание связи участника'), { target: { value: 'Контрагент по делу' } });
    fireEvent.click(screen.getByRole('button', { name: /^Добавить$/u }));

    await waitFor(() => {
      expect(apiService.addCaseRelatedParty).toHaveBeenCalledWith(15, {
        sourceType: 'legalEntity',
        sourceId: 25,
        role: 'Третье лицо',
        notes: 'Контрагент по делу',
      });
      expect(apiService.getCaseRelatedParties).toHaveBeenCalledTimes(2);
    });
  });

  it('opens a related participant from the participants tab', async () => {
    const onOpenClient = vi.fn();
    vi.mocked(apiService.getObjectAuditHistory).mockResolvedValue([
      {
        id: 92,
        objectType: 'client-case-related-party',
        objectId: 1,
        action: 'Updated',
        description: 'Связанное лицо архивировано',
        actorUserName: 'Администратор',
        metadataJson: '{"changedFields":["IsArchived","ArchiveReason"],"reason":"Связанное лицо архивировано"}',
        createdAt: '2026-05-20T09:10:00Z',
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} onOpenClient={onOpenClient} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть участника Земцов Виктор Александрович' }));

    expect(onOpenClient).toHaveBeenCalledWith(12, 15);
    expect(screen.getByText('Физическое лицо')).toBeTruthy();
    expect(screen.getByText('Представитель по доверенности')).toBeTruthy();
    expect(screen.queryByText(/\+79991234567/u)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Показать контакты' }));
    expect(screen.getByText(/\+79991234567/u)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'История участника Земцов Виктор Александрович' }));

    await waitFor(() => {
      expect(apiService.getObjectAuditHistory).toHaveBeenCalledWith('client-case-related-party', 1);
    });
    expect(await screen.findByText('Причина: Связанное лицо архивировано')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' }));

    await waitFor(() => {
      expect(dialogMock.confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Удалить участника из дела?', variant: 'danger' }));
      expect(apiService.deleteCaseRelatedParty).toHaveBeenCalledWith(15, 1);
    });
  });

  it('shows archived related participants on demand', async () => {
    const activeParty = {
      id: 1,
      clientCaseId: 15,
      sourceType: 'client' as const,
      sourceId: 12,
      clientId: 12,
      displayName: 'Земцов Виктор Александрович',
      role: 'Должник',
      phone: '+79991234567',
      email: 'zemtsov@example.test',
      notes: 'Представитель по доверенности',
      createdAt: '2026-05-01T10:00:00Z',
    };
    const archivedParty = {
      ...activeParty,
      id: 2,
      sourceId: 31,
      clientId: 31,
      displayName: 'Архивный участник',
      isArchived: true,
      archivedAt: '2026-05-02T10:00:00Z',
      archivedByUserName: 'Администратор',
      archiveReason: 'Дубль связи',
    };
    vi.mocked(apiService.getCaseRelatedParties).mockImplementation(async (_caseId, includeArchived) => (
      includeArchived ? [activeParty, archivedParty] : [activeParty]
    ));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText(/ЗЕМЦОВ/u);
    fireEvent.click(screen.getByRole('button', { name: 'Участники' }));
    expect((await screen.findAllByText(activeParty.displayName)).length).toBeGreaterThan(0);
    expect(screen.queryByText('Архивный участник')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Показать архив' }));

    await waitFor(() => {
      expect(apiService.getCaseRelatedParties).toHaveBeenCalledWith(15, true);
    });
    expect(await screen.findByText('Архивный участник')).toBeTruthy();
    expect(screen.getByText('Архивная связь')).toBeTruthy();
    expect(screen.getByText(/Архивировано: 02\.05\.2026.*Администратор/u)).toBeTruthy();
    expect(screen.getByText('Причина: Дубль связи')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Восстановить связь с участником Архивный участник' }));

    await waitFor(() => {
      expect(apiService.addCaseRelatedParty).toHaveBeenCalledWith(15, expect.objectContaining({
        sourceType: 'client',
        sourceId: 31,
      }));
      expect(screen.getByText('Связь с участником «Архивный участник» восстановлена.')).toBeTruthy();
    });
  });

  it('runs a manual court source check from the case card', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Проверить суд по делу' }));

    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(15);
      expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    });
    expect(screen.getByText('Ручная проверка судебного источника зафиксирована.')).toBeTruthy();
  });

  it('adds a user comment to the case history tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'История' }));
    expect(await screen.findByText('Комментарий по делу: Проверить срок.')).toBeTruthy();
    expect(apiService.getCaseTimeline).toHaveBeenCalledWith(15);
    fireEvent.change(screen.getByLabelText('Комментарий по истории дела'), { target: { value: 'Проверить срок апелляции' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить комментарий' }));

    await waitFor(() => {
      expect(apiService.addCaseHistoryEntry).toHaveBeenCalledWith(15, { text: 'Проверить срок апелляции' });
      expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
      expect(screen.getByText('Комментарий добавлен в историю дела.')).toBeTruthy();
    });
  });

  it('shows the court tab with source and procedural deadlines', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));

    expect(screen.getByText('Судебный блок')).toBeTruthy();
    expect(await screen.findByDisplayValue('Арбитражный суд Самарской области')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getAllByDisplayValue('https://kad.arbitr.ru/card').length).toBeGreaterThan(0);
    });
    expect(screen.getByText('Судебная хронология')).toBeTruthy();
    expect(screen.getAllByText('Ближайшее заседание').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Апелляция').length).toBeGreaterThan(0);
    expect(screen.getByText('01.07.2026')).toBeTruthy();
    expect(await screen.findByText('Журнал судебных проверок')).toBeTruthy();
    expect(screen.getAllByText('Проверка записана').length).toBeGreaterThan(0);
    expect(screen.getByText('Инициатор: Администратор')).toBeTruthy();
    expect(apiService.getCaseCourtSyncLogs).toHaveBeenCalledWith(15);

    fireEvent.click(screen.getByRole('button', { name: /Проверить источник/u }));
    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(15);
    });
  });

  it('keeps the court tab in safe mode for pretrial cases without court data', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      caseType: 'Внесудебное урегулирование',
      stage: 'Внесудебное производство',
      courtName: '',
      courtCaseNumber: '',
      courtUrl: '',
      nextHearingAt: null,
      claimFiledAt: null,
      courtDecisionAt: null,
      appealDeadlineAt: null,
      cassationDeadlineAt: null,
      motionDeadlineAt: null,
      objectionDeadlineAt: null,
    }));
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValue([]);
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([]);
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([]);
    vi.mocked(apiService.getCaseCourtParties).mockResolvedValue([]);
    vi.mocked(apiService.getCaseCourtSyncLogs).mockResolvedValue([]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));

    expect(await screen.findByText('Внесудебный режим')).toBeTruthy();
    expect(screen.getByText('Судебные данные сейчас не обязательны')).toBeTruthy();
    expect(screen.queryByText('Поиск судебной карточки')).toBeNull();
    expect(screen.queryByText('Инстанции судов')).toBeNull();
    expect(screen.queryByText('Процессуальные сроки 2.0')).toBeNull();
    expect(screen.queryByRole('button', { name: /Проверить источник/u })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Заполнить судебный блок/u }));
    expect(await screen.findByText('Поиск судебной карточки')).toBeTruthy();
    expect(screen.getByText('Инстанции судов')).toBeTruthy();
  });

  it('shows an error when a court source check fails from the court tab', async () => {
    vi.mocked(apiService.runCaseCourtSync).mockRejectedValueOnce(new Error('Судебный источник временно недоступен.'));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: /Проверить источник/u }));

    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(15);
      expect(screen.getByText('Судебный источник временно недоступен.')).toBeTruthy();
    });
  });

  it('keeps the court tab available and shows an error when court card search fails', async () => {
    vi.mocked(apiService.searchCaseCourtSyncCases).mockRejectedValueOnce(
      new Error('Не удалось получить результаты поиска судебной карточки.'),
    );

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: /Найти/u }));

    await waitFor(() => {
      expect(apiService.searchCaseCourtSyncCases).toHaveBeenCalledWith(15, expect.objectContaining({
        sourceUrl: 'https://kad.arbitr.ru/card',
        courtCaseNumber: 'А55-12345/2026',
        instance: 'first',
      }));
      expect(screen.getByText('Не удалось получить результаты поиска судебной карточки.')).toBeTruthy();
    });
    expect(screen.getByRole('button', { name: /Найти/u })).toBeTruthy();
  });

  it('shows classified court sync changes and keeps user data conflicts untouched', async () => {
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValueOnce({
      clientCaseId: 15,
      status: 'external-sync-completed',
      hasCourtBinding: true,
      message: 'Судебный источник обновлен.',
      checkedAt: '2026-07-29T15:00:00Z',
      syncedFromExternalSource: true,
      newItems: ['судебное событие «Заседание»'],
      changedItems: ['судья'],
      removedItems: ['участник процесса «Иванов И.И.»'],
      conflictItems: ['ручное событие «Подготовка» совпало с событием источника'],
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: /Проверить источник/u }));

    expect(await screen.findByText('Результат последней проверки')).toBeTruthy();
    expect(screen.getByText('Новые · 1')).toBeTruthy();
    expect(screen.getByText('Изменённые · 1')).toBeTruthy();
    expect(screen.getByText('Исчезли из источника · 1')).toBeTruthy();
    expect(screen.getByText('Конфликты · 1')).toBeTruthy();
    expect(screen.getByText(/не удаляются и не перезаписываются автоматически/u)).toBeTruthy();
    expect(screen.getByRole('button', { name: /Проверить источник/u })).toBeTruthy();
  });

  it('searches and binds an external court card from the court tab', async () => {
    vi.mocked(apiService.searchCaseCourtSyncCases).mockResolvedValueOnce([
      {
        providerKey: 'sudrf',
        sourceType: 'sudrf',
        sourceUrl: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42',
        courtName: 'Хабаровский краевой суд',
        courtCaseNumber: '33а-3390/2026',
        judicialUid: '27RS0005-01-2025-002659-56',
        instance: 'appeal',
        instanceLabel: 'Апелляция',
        title: 'Хабаровский краевой суд',
        summary: 'Хабаровский краевой суд; № 33а-3390/2026; Судья: Казарикова О.В.',
        canFetch: true,
        requiresReview: true,
        differences: [
          {
            field: 'courtName',
            label: 'Суд',
            currentValue: 'Арбитражный суд Самарской области',
            sourceValue: 'Хабаровский краевой суд',
            kind: 'replace',
          },
          {
            field: 'judgeName',
            label: 'Судья',
            currentValue: '',
            sourceValue: 'Казарикова О.В.',
            kind: 'fill',
          },
        ],
      },
    ]);
    vi.mocked(apiService.bindCaseCourtSyncCase).mockResolvedValueOnce({
      clientCaseId: 15,
      instance: 'first',
      providerKey: 'sudrf',
      sourceType: 'sudrf',
      courtName: 'Хабаровский краевой суд',
      courtCaseNumber: '33а-3390/2026',
      courtUrl: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42',
      judicialUid: '27RS0005-01-2025-002659-56',
      message: 'Судебная карточка привязана к делу.',
      courtInstance: {
        id: 81,
        clientCaseId: 15,
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        courtId: null,
        courtName: 'Хабаровский краевой суд',
        courtCaseNumber: '33а-3390/2026',
        courtUrl: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42',
        judgeName: '',
        courtroom: '',
        secretaryName: '',
        notes: '',
        createdAt: '2026-05-18T10:00:00Z',
        complaints: [],
      },
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    const sourceInput = await screen.findByPlaceholderText('https://...sudrf.ru/...');
    fireEvent.change(sourceInput, {
      target: { value: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42' },
    });
    fireEvent.change(screen.getByPlaceholderText('33а-3390/2026'), {
      target: { value: '33а-3390/2026' },
    });
    fireEvent.change(screen.getByPlaceholderText('63RS0018-01-2025-003508-91'), {
      target: { value: '27RS0005-01-2025-002659-56' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Найти/u }));

    expect((await screen.findAllByText('Хабаровский краевой суд')).length).toBeGreaterThan(0);
    expect(screen.getByText(/UID 27RS0005-01-2025-002659-56/u)).toBeTruthy();
    expect(screen.getByText('Требуется проверка изменений')).toBeTruthy();
    expect(screen.getByText('Арбитражный суд Самарской области')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Привязать/u }));

    await waitFor(() => {
      expect(dialogMock.confirm).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Заменить данные дела?',
        confirmLabel: 'Подтвердить замену',
      }));
      expect(apiService.searchCaseCourtSyncCases).toHaveBeenCalledWith(15, {
        sourceUrl: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42',
        courtCaseNumber: '33а-3390/2026',
        judicialUid: '27RS0005-01-2025-002659-56',
        courtName: 'Арбитражный суд Самарской области',
        instance: 'first',
      });
      expect(apiService.bindCaseCourtSyncCase).toHaveBeenCalledWith(15, {
        instance: 'first',
        providerKey: 'sudrf',
        sourceType: 'sudrf',
        sourceUrl: 'https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42',
        courtName: 'Хабаровский краевой суд',
        courtCaseNumber: '33а-3390/2026',
        judicialUid: '27RS0005-01-2025-002659-56',
        judgeName: 'Казарикова О.В.',
        confirmDifferences: true,
      });
      expect(screen.getByText('Судебная карточка привязана к делу.')).toBeTruthy();
    });
  });

  it('calculates a procedural deadline and shows calculation details', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: /Рассчитать/u }));

    await waitFor(() => {
      expect(apiService.calculateCaseProceduralDeadline).toHaveBeenCalledWith({
        startAt: '2026-05-20T09:30:00Z',
        rule: 'workingDays',
        monthsToAdd: 0,
        daysToAdd: 5,
        rollForwardToWorkingDay: true,
      });
      expect(screen.getByDisplayValue('2026-06-10')).toBeTruthy();
      expect(screen.getByText('Дата процессуального срока рассчитана.')).toBeTruthy();
      expect(screen.getByText('Расчет срока: 20.05.2026 → 10.06.2026')).toBeTruthy();
      expect(screen.getAllByText(/Базовая дата: 09.06.2026/u).length).toBeGreaterThan(0);
      expect(screen.getByText('Пропущено нерабочих дней: 2')).toBeTruthy();
      expect(screen.getByText('Перенос: 1 дн.')).toBeTruthy();
    });
  });

  it('records a manual court check with a comment from the court tab', async () => {
    dialogMock.prompt.mockResolvedValueOnce('Проверено по телефону суда');
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValueOnce({
      clientCaseId: 15,
      status: 'manual-check-recorded',
      hasCourtBinding: true,
      message: 'Ручная проверка судебного источника зафиксирована без автоматического обновления. Комментарий: Проверено по телефону суда.',
      checkedAt: '2026-05-18T10:00:00Z',
      checkedByUserId: 7,
      checkedByUserName: 'Кожеваткин Андрей Владимирович',
      courtName: 'Арбитражный суд Самарской области',
      courtCaseNumber: 'А55-12345/2026',
      courtUrl: 'https://kad.arbitr.ru/card',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: /Зафиксировать вручную/u }));

    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(15, {
        manualOnly: true,
        comment: 'Проверено по телефону суда',
      });
    });
    expect(screen.getByText(/Комментарий: Проверено по телефону суда/u)).toBeTruthy();
  });

  it('copies a direct case link', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Ссылка' }));

    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith('http://localhost:3000/cases/15');
    });
    expect(screen.getByRole('button', { name: 'Ссылка скопирована' })).toBeTruthy();
  });
});
