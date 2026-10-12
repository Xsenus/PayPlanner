import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument, ClientCaseWorkOrder } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CaseDetailPage, CourtSourceProvenance } from './CaseDetailPage';

const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
  prompt: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  getAuthToken: () => 'owned-form-session',
  apiService: {
    getCaseV2: vi.fn(),
    updateCaseV2: vi.fn(),
    reviewCasePosition: vi.fn(),
    getCasePositionTemplates: vi.fn(),
    createCasePositionTemplate: vi.fn(),
    updateCasePositionTemplate: vi.fn(),
    archiveCasePositionTemplate: vi.fn(),
    confirmNextHearing: vi.fn(),
    getCurrentCaseAppealDecision: vi.fn(),
    createCaseAppealTask: vi.fn(),
    getActResponsibles: vi.fn(),
    getTaskJournalItems: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    getClients: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getContacts: vi.fn(),
    getCaseDocuments: vi.fn(),
    getPendingCaseDocumentUploads: vi.fn(),
    retireCaseDocumentUpload: vi.fn(),
    getCaseDocumentUploadStates: vi.fn(),
    getCaseHearingPreparation: vi.fn(),
    updateCaseHearingPreparation: vi.fn(),
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
    recordCaseHearingOutcome: vi.fn(),
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
    getCourtRecoveries: vi.fn(),
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
  it.each(['documents', 'edit'] as const)('shows the current invoice work order after %s publication proof without another financial action', async (form) => {
    const approved = {
      id: 31, clientCaseId: 15, title: 'Первоначальный наряд', status: 'approved', statusLabel: 'Согласован',
      currency: 'RUB', notes: null, totalAmount: 35,
      items: [{ id: 41, clientCaseWorkOrderId: 31, serviceName: 'Первоначальная услуга', description: null,
        executorUserId: null, executorName: null, dueDate: null, quantity: 1, unitPrice: 35, totalAmount: 35, sortOrder: 1 }],
    };
    let publicationReady = false;
    vi.mocked(apiService.getCaseWorkOrder).mockImplementation(async () => publicationReady
      ? { ...approved, status: 'invoice_created', statusLabel: 'Счет выставлен' } : approved);
    let confirm!: (value: Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>) => void;
    const proof = new Promise<Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>>((resolve) => { confirm = resolve; });
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockReturnValue(proof);
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByRole('button', { name: 'Создать счет' });
    if (form === 'edit') {
      fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая стратегия' } });
    } else {
      fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    }
    await waitFor(() => expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalled());
    const orderReads = vi.mocked(apiService.getCaseWorkOrder).mock.calls.length;
    const documentReads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    publicationReady = true;
    confirm({ items: [], nextCursor: null, localPublicationConfirmed: true });
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(documentReads + 1));
    if (form === 'edit') {
      await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledTimes(orderReads + 1));
      expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая стратегия');
      fireEvent.click(screen.getAllByRole('button', { name: 'Отмена' })[0]);
    } else {
      fireEvent.click(screen.getByRole('button', { name: 'Заказ-наряд' }));
    }
    await waitFor(() => expect((screen.getByRole('button', { name: 'Создать счет' }) as HTMLButtonElement).disabled).toBe(true));
    expect(apiService.createCaseWorkOrderInvoice).not.toHaveBeenCalled();
  });

  it.each(['close edit', 'close edit before documents', 'late initial read', 'unmount', 'change case'] as const)(
    'keeps the invoice publication read scoped to its case during %s', async (boundary) => {
      const approved: ClientCaseWorkOrder = {
        id: 31, clientCaseId: 15, title: 'Первоначальный наряд', status: 'approved', statusLabel: 'Согласован',
        currency: 'RUB', notes: null, totalAmount: 35,
        items: [{ id: 41, clientCaseWorkOrderId: 31, serviceName: 'Первоначальная услуга', description: null,
          executorUserId: null, executorName: null, dueDate: null, quantity: 1, unitPrice: 35, totalAmount: 35, sortOrder: 1 }],
      };
      const updated = { ...approved, status: 'invoice_created', statusLabel: 'Счет выставлен' };
      let finishInitial!: (value: ClientCaseWorkOrder) => void;
      let finishRefresh!: (value: ClientCaseWorkOrder) => void;
      let finishProof!: (value: Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>) => void;
      const initial = new Promise<ClientCaseWorkOrder>((resolve) => { finishInitial = resolve; });
      const refresh = new Promise<ClientCaseWorkOrder>((resolve) => { finishRefresh = resolve; });
      const proof = new Promise<Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>>((resolve) => { finishProof = resolve; });
      let refreshSignal: AbortSignal | undefined;
      vi.mocked(apiService.getCaseWorkOrder).mockImplementation(async (caseId, signal) => {
        if (caseId !== 15) return { ...approved, id: 0, clientCaseId: caseId, title: '', items: [] };
        if (signal) { refreshSignal = signal; return refresh; }
        return boundary === 'late initial read' ? initial : approved;
      });
      vi.mocked(apiService.getPendingCaseDocumentUploads).mockReturnValue(proof);
      window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'workOrder' }));
      const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
      await screen.findByText('ЗЕМЦОВ - банкротство');
      await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledTimes(1));
      fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Сохранить мой черновик' } });
      await waitFor(() => expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalled());
      const panelSignal = vi.mocked(apiService.getPendingCaseDocumentUploads).mock.calls[0][2]!;
      let finishDocuments: ((items: ClientCaseDocument[]) => void) | undefined;
      if (boundary === 'close edit before documents') {
        vi.mocked(apiService.getCaseDocuments).mockImplementationOnce(() => new Promise((resolve) => { finishDocuments = resolve; }));
      }
      await act(async () => { finishProof({ items: [], nextCursor: null, localPublicationConfirmed: true }); });
      if (boundary === 'close edit before documents') {
        expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Сохранить мой черновик');
        fireEvent.click(screen.getAllByRole('button', { name: 'Отмена' })[0]);
        expect(panelSignal.aborted).toBe(true);
        await act(async () => { finishDocuments?.([]); });
      }
      await waitFor(() => expect(refreshSignal).toBeDefined());
      if (boundary !== 'close edit before documents') expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Сохранить мой черновик');
      if (boundary === 'change case') {
        const nextCase = { ...await apiService.getCaseV2(15), id: 16, title: 'Другое контрольное дело' };
        vi.mocked(apiService.getCaseV2).mockResolvedValue(nextCase);
        view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} canEdit />);
        await screen.findByText('Другое контрольное дело');
        expect(refreshSignal!.aborted).toBe(true);
        await act(async () => { finishRefresh(updated); });
        expect(screen.queryByDisplayValue('Первоначальный наряд')).toBeNull();
        expect(screen.queryByDisplayValue('Сохранить мой черновик')).toBeNull();
      } else if (boundary === 'unmount') {
        view.unmount();
        expect(refreshSignal!.aborted).toBe(true);
        await act(async () => { finishRefresh(updated); });
        expect(screen.queryByText('Первоначальный наряд')).toBeNull();
      } else {
        if (boundary === 'close edit') {
          fireEvent.click(screen.getAllByRole('button', { name: 'Отмена' })[0]);
          expect(panelSignal.aborted).toBe(true);
          expect(refreshSignal!.aborted).toBe(false);
        }
        await act(async () => { finishRefresh(updated); });
        if (boundary === 'late initial read') {
          await act(async () => { finishInitial(approved); });
          fireEvent.click(screen.getAllByRole('button', { name: 'Отмена' })[0]);
        }
        await waitFor(() => expect((screen.getByRole('button', { name: 'Создать счет' }) as HTMLButtonElement).disabled).toBe(true));
        const parameters = screen.getByDisplayValue('Первоначальный наряд').closest('section')!;
        expect((within(parameters).getByLabelText('Статус') as HTMLSelectElement).value).toBe('invoice_created');
      }
      expect(apiService.createCaseWorkOrderInvoice).not.toHaveBeenCalled();
    },
  );

  it.each(['retired', 'uploaded'] as const)('handles work order %s proof without regenerating or replacing unsaved fields', async (outcome) => {
    const pending = { uploadKey: '11111111-1111-4111-8111-111111111111', fileName: 'case-15-work-order.html', status: 'waiting' as const,
      documentId: null, version: null, sha256: null, contentType: null, requestedDocumentKey: null, documentKey: null,
      documentType: null, requestedStatus: null, message: 'Ожидаем копии заказ-наряда.',
      generation: { kind: 'work-order', requestSha256: 'a'.repeat(64), templateKey: null, documentType: null, status: 'Согласован', sourceVerified: true } };
    const terminal = { ...pending, status: outcome, documentId: outcome === 'uploaded' ? 601 : null, version: outcome === 'uploaded' ? 1 : null };
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValueOnce({ items: [pending], nextCursor: null })
      .mockResolvedValue({ items: [terminal], nextCursor: null });
    vi.mocked(apiService.retireCaseDocumentUpload).mockResolvedValue(terminal);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    const button = await screen.findByRole('button', { name: 'Прекратить формирование' });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая позиция по наряду' } });
    const caseReads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    fireEvent.click(button);
    await screen.findByText(outcome === 'uploaded' ? /Новое формирование запустите отдельно/ : /Прежняя попытка формирования прекращена/);
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая позиция по наряду');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(caseReads);
    expect(apiService.generateCaseWorkOrderDocument).not.toHaveBeenCalled();
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled();
  });
  it.each(['retired', 'uploaded'] as const)('handles explicit %s proof inside the full editor while preserving unsaved fields', async (outcome) => {
    const pending = { uploadKey: '11111111-1111-4111-8111-111111111111', fileName: 'own-version.pdf', status: 'waiting' as const,
      documentId: null, version: null, sha256: null, contentType: null, requestedDocumentKey: null, documentKey: null,
      documentType: null, requestedStatus: null, message: 'Ожидаем копии.' };
    const terminal = { ...pending, status: outcome, documentId: outcome === 'uploaded' ? 601 : null, version: outcome === 'uploaded' ? 2 : null };
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValueOnce({ items: [pending], nextCursor: null })
      .mockResolvedValue({ items: [terminal], nextCursor: null });
    vi.mocked(apiService.retireCaseDocumentUpload).mockResolvedValue(terminal);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    const button = await screen.findByRole('button', { name: 'Подготовить новую загрузку' });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая новая позиция' } });
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    const caseReads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    fireEvent.click(button);
    await screen.findByText(outcome === 'uploaded' ? /Прежняя попытка уже добавила документ/ : /Прежняя попытка прекращена\. Новый файл/);
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads + (outcome === 'uploaded' ? 1 : 0)));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая новая позиция');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(caseReads);
    expect(apiService.retireCaseDocumentUpload).toHaveBeenCalledExactlyOnceWith(15, pending.uploadKey, expect.any(AbortSignal), 'owned-form-session');
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled(); expect(apiService.uploadCaseDocumentVersion).not.toHaveBeenCalled();
  });
  it('refreshes confirmed documents in the full editor without replacing unsaved fields or reloading the case', async () => {
    let finish!: (page: Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>) => void;
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    await waitFor(() => expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая позиция' } });
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    const caseReads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    await act(async () => finish({ items: [{ uploadKey: 'receipt', fileName: 'ready.pdf', status: 'uploaded', documentId: 601,
      version: 2, sha256: null, contentType: null, requestedDocumentKey: null, documentKey: null, documentType: null,
      requestedStatus: null, message: 'Документ добавлен.' }], nextCursor: null }));
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads + 1));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая позиция');
    expect(apiService.getCaseDocuments).toHaveBeenLastCalledWith(15, expect.any(AbortSignal));
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(caseReads);
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled();
    expect(apiService.uploadCaseDocumentVersion).not.toHaveBeenCalled();
  });
  beforeEach(() => {
    vi.mocked(apiService.retireCaseDocumentUpload).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValue({ items: [], nextCursor: null });
    vi.mocked(apiService.getCaseDocumentUploadStates).mockReset();
    vi.mocked(apiService.getCaseDocumentUploadStates).mockResolvedValue([]);
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
    vi.mocked(apiService.reviewCasePosition).mockReset();
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [], canManageShared: false });
    vi.mocked(apiService.reviewCasePosition).mockResolvedValue({ positionReviewedAt: '2026-09-23T10:00:00Z', positionReviewedByUserId: 7, positionReviewedByName: 'Юрист' });
    vi.mocked(apiService.confirmNextHearing).mockReset();
    vi.mocked(apiService.getCaseHearingPreparation).mockReset();
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValue({
      hearingAt: '2099-10-20T15:00:00Z', contractCheckedAt: null, contractCheckedByName: '',
      authorityCheckedAt: null, authorityCheckedByName: '', materialsCheckedAt: null,
      materialsCheckedByName: '', documentIds: [],
    });
    vi.mocked(apiService.updateCaseHearingPreparation).mockReset();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockReset();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue(null);
    vi.mocked(apiService.createCaseAppealTask).mockReset();
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
        canRecordHearingOutcome: false,
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
    vi.mocked(apiService.recordCaseHearingOutcome).mockReset();
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
      filedBy: 'opponent',
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
      filedBy: 'opponent',
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
      canRecordHearingOutcome: true,
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
      lastMeaningfulActivityAt: '2026-05-12T10:15:00Z',
      nextInactivityControlAt: '2026-05-26T10:15:00Z',
      inactivityControlExclusionReason: '',
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
    vi.mocked(apiService.getCourtRecoveries).mockReset();
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({ awardedAmount: null, receivedAmount: 0, remainingAmount: null, linkedPayments: [], availablePayments: [], hasMoreAvailablePayments: false, hasCompletePaymentVisibility: true });
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

  it('does not offer a result action for a hearing that has not occurred or was cancelled', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        id: 42, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание',
        title: 'Будущее заседание', scheduledAt: '2027-06-12T09:30:00Z', allDay: false,
        courtInstance: 'first', courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'scheduled', movementStatusLabel: 'Назначено', sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z', isOverdue: false, canRecordHearingOutcome: false,
      },
      {
        id: 43, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание',
        title: 'Отменённое заседание', scheduledAt: '2026-06-12T09:30:00Z', allDay: false,
        courtInstance: 'first', courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'cancelled', movementStatusLabel: 'Отменено', sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z', isOverdue: false, canRecordHearingOutcome: false,
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    await screen.findByText('Будущее заседание');
    expect(screen.getAllByText('Отменённое заседание').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Зафиксировать результат' })).toBeNull();
  });

  it('shows saved judicial amounts as currency without mixing them with the fee', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      courtClaimAmount: 100000.5,
      courtAwardedAmount: 0,
      courtStateFeeAmount: 3500.25,
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));

    expect(screen.queryAllByText('Сумма иска').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/100\s?000,50\s?₽/u).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/0\s?₽/u).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/3\s?500,25\s?₽/u).length).toBeGreaterThan(0);
  });

  it('confirms the saved position and refreshes its review mark', async () => {
    let reviewed = false;
    vi.mocked(apiService.getCaseV2).mockImplementation(async () => createCase(reviewed ? {
      positionReviewedAt: '2026-09-23T10:00:00Z',
      positionReviewedByUserId: 7,
      positionReviewedByName: 'Юрист',
    } : {}));
    vi.mocked(apiService.reviewCasePosition).mockImplementation(async () => {
      reviewed = true;
      return { positionReviewedAt: '2026-09-23T10:00:00Z', positionReviewedByUserId: 7, positionReviewedByName: 'Юрист' };
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('Позиция заполнена, но ещё не подтверждена.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    await waitFor(() => expect(apiService.reviewCasePosition).toHaveBeenCalledWith(15));
    expect(await screen.findByText(/Проверена.*Юрист/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Подтвердить проверку позиции' })).toBeNull();
  });

  it('copies a selected position template into the new case card draft before saving', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({
      items: [{ id: 4, title: 'Возражения', text: 'Проверить доказательства', scope: 'shared', updatedAt: '2026-09-25T00:00:00Z', canManage: false }],
      canManageShared: false,
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByLabelText('Дата регистрации в суде'), { target: { value: '2026-05-13' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '4' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toContain('Проверить доказательства');
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);
    await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledWith(
      15, expect.objectContaining({ strategy: expect.stringContaining('Проверить доказательства'), courtRegisteredAt: '2026-05-13', clearCourtRegisteredAt: false }),
    ));
  });

  it('shows the next hearing confirmation on the court tab and records it once', async () => {
    const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt }));
    vi.mocked(apiService.confirmNextHearing).mockResolvedValue({
      nextHearingConfirmedForAt: nextHearingAt,
      nextHearingConfirmedAt: new Date().toISOString(),
      nextHearingConfirmedByUserId: 7,
      nextHearingConfirmedByName: 'Юрист',
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Подтвердить дату' }));
    await waitFor(() => expect(apiService.confirmNextHearing).toHaveBeenCalledWith(15));
    expect(await screen.findByText(/подтверждена.*Юрист/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Подтвердить дату' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));
    expect(screen.getByText(/подтверждена.*Юрист/i)).toBeTruthy();
  });

  it('lets a lawyer plan hearing travel from the court tab', async () => {
    const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString();
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canViewTasks canCreateTasks />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Запланировать командировку' }));

    await waitFor(() => expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientCaseId: 15,
      assigneeUserId: 7,
      title: 'Организовать поездку на судебное заседание',
    })));
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

  it('PAY-21: quick access opens existing case sections and marks the current one', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    const menu = screen.getByRole('navigation', { name: 'Быстрый доступ к разделам дела' });
    expect(within(menu).queryByRole('button', { name: /ЕФРСБ|Почта/ })).toBeNull();

    fireEvent.click(within(menu).getByRole('button', { name: 'Быстрый доступ: Документы' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Быстрый доступ: Документы' }).getAttribute('aria-current')).toBe('page');
    expect(within(menu).getByRole('button', { name: 'Быстрый доступ: История' }).getAttribute('aria-current')).toBeNull();
  });

  it('shows imported court data that moved to the archive as inactive', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        canRecordHearingOutcome: false,
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

    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
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
        canRecordHearingOutcome: false,
        id: 41,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Судебное заседание',
        scheduledAt: '2026-06-05T12:00:00',
        allDay: false,
        previousScheduledAt: '2026-06-02T12:00:00Z',
        previousAllDay: false,
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

    expect(await screen.findByText('Фактически получено по решению суда')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('Судебное заседание')).toBeTruthy();
    expect(await screen.findByText(/Перенесено: 02.06.2026, 16:00 → 05.06.2026, 16:00/)).toBeTruthy();
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
    fireEvent.click(screen.getByRole('combobox', { name: /Наша роль в деле/u }));
    fireEvent.click(screen.getByRole('option', { name: 'Третье лицо' }));
    fireEvent.change(screen.getByLabelText('ИНН истца'), { target: { value: '1234567890' } });
    fireEvent.change(screen.getByLabelText('ОГРН/ОГРНИП ответчика'), { target: { value: '1234567890123' } });
    fireEvent.change(screen.getByLabelText('Сумма иска'), { target: { value: '100000,50' } });
    fireEvent.change(screen.getByLabelText('Присуждено судом'), { target: { value: '75000' } });
    fireEvent.change(screen.getByLabelText('Госпошлина по делу'), { target: { value: '3500,25' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        15,
        expect.objectContaining({
          nextHearingAt: '2026-06-05T13:30:00.000Z',
          ourCourtRole: 'third-party',
          claimantInn: '1234567890',
          defendantOgrn: '1234567890123',
          courtClaimAmount: 100000.5,
          courtAwardedAmount: 75000,
          courtStateFeeAmount: 3500.25,
        }),
      );
    });
  });

  it('records a hearing result from the court tab without editing the case', async () => {
    const hearing = {
      id: 41,
      clientCaseId: 15,
      eventType: 'hearing',
      eventTypeLabel: 'Заседание',
      title: 'Заседание с результатом',
      scheduledAt: '2026-06-12T09:30:00Z',
      allDay: false,
      courtInstance: 'first',
      courtInstanceLabel: 'Первая инстанция',
      movementStatus: 'scheduled',
      movementStatusLabel: 'Назначено',
      sourceType: 'sudrf',
      createdAt: '2026-05-21T00:00:00Z',
      isOverdue: false,
      canRecordHearingOutcome: true,
    } as const;
    const savedHearing = {
      ...hearing,
      hearingOutcome: 'postponed',
      hearingOutcomeNote: 'Назначат новую дату',
      hearingOutcomeRecordedByUserName: 'Юрист',
    } as const;
    let currentHearing: typeof hearing | typeof savedHearing = hearing;
    vi.mocked(apiService.getCaseCourtEvents).mockImplementation(async () => [currentHearing]);
    vi.mocked(apiService.recordCaseHearingOutcome).mockImplementation(async () => {
      currentHearing = savedHearing;
      return savedHearing;
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    await screen.findByText('Заседание с результатом');
    fireEvent.click(screen.getByRole('button', { name: 'Зафиксировать результат' }));
    fireEvent.change(screen.getByLabelText('Результат заседания'), { target: { value: 'postponed' } });
    fireEvent.change(screen.getByLabelText('Пояснение'), { target: { value: 'Назначат новую дату' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить итог' }));

    await waitFor(() => expect(apiService.recordCaseHearingOutcome).toHaveBeenCalledWith(
      15, 41, { outcome: 'postponed', note: 'Назначат новую дату' },
    ));
    expect(await screen.findByText('Итог юриста: отложено')).toBeTruthy();
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
    fireEvent.change(screen.getByLabelText('Дата вступления решения в силу'), { target: { value: '2026-10-01' } });
    fireEvent.change(screen.getByLabelText('Дата получения исполнительного листа'), { target: { value: '2026-10-03' } });
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '2026-10-04' } });
    fireEvent.change(screen.getByLabelText('Куда подан исполнительный лист'), { target: { value: 'bank' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        15,
        expect.objectContaining({
          title: 'ЗЕМЦОВ - обновленная карточка',
          courtDecisionEffectiveAt: '2026-10-01',
          executiveWritReceivedAt: '2026-10-03',
          executiveWritSubmittedAt: '2026-10-04',
          executiveWritSubmittedTo: 'bank',
          clearExecutiveWritSubmission: false,
          clientId: 12,
          legalEntityId: null,
          status: 'Open',
        }),
      );
    });
    expect(await screen.findByText('Карточка дела обновлена.')).toBeTruthy();
  });

  it('uploads the executive writ scan from the full case editor', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([{
      id: 27, clientCaseId: 15, documentKey: 'writ', version: 1,
      documentType: 'Исполнительный лист', status: 'Получен', originalFileName: 'Лист.pdf',
      contentType: 'application/pdf', sizeBytes: 1024, uploadedByName: 'Юрист',
      uploadedAt: '2026-09-27T10:00:00Z', isLatestVersion: true,
    }]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(await screen.findByRole('button', { name: 'Скачать скан Лист.pdf' })).toBeTruthy();

    const file = new File(['writ'], 'Лист.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'), { target: { files: [file] } });

    await waitFor(() => expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(15, file, {
      documentType: 'Исполнительный лист',
      status: 'Получен',
    }));
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalled());
  });

  it('allows clearing both writ workflow dates in the full case editor', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      courtDecisionEffectiveAt: '2026-10-01',
      executiveWritReceivedAt: '2026-10-03',
      executiveWritSubmittedAt: '2026-10-04',
      executiveWritSubmittedTo: 'bank',
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Дата вступления решения в силу'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Дата получения исполнительного листа'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        15,
        expect.objectContaining({
          courtDecisionEffectiveAt: null,
          executiveWritReceivedAt: null,
          executiveWritSubmittedAt: null,
          clearExecutiveWritSubmission: true,
        }),
      );
    });
  });

  it('prevents submitting the writ before its receipt in the full editor', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ executiveWritReceivedAt: '2026-10-03' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '2026-10-02' } });
    fireEvent.change(screen.getByLabelText('Куда подан исполнительный лист'), { target: { value: 'fssp' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);
    expect(await screen.findByText('Дата подачи исполнительного листа должна быть не раньше даты получения.')).toBeTruthy();
    expect(apiService.updateCaseV2).not.toHaveBeenCalled();
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
    const latestActivityAt = new Date();
    const nextControlAt = new Date(latestActivityAt.getTime() + 14 * 24 * 60 * 60 * 1000);
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      lastTaskAt: latestActivityAt.toISOString(),
      lastMeaningfulActivityAt: latestActivityAt.toISOString(),
      nextInactivityControlAt: nextControlAt.toISOString(),
      eventHistory: '18.05.2026 10:30: Ручная проверка судебного источника зафиксирована.',
    }));
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: latestActivityAt.toISOString(),
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
      lastViewedByResponsibleAt: latestActivityAt.toISOString(),
      lastViewedByResponsibleUserId: 7,
      lastViewedByResponsibleUserName: 'Кожеваткин Андрей Владимирович',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: null,
      inactivityRemindersEnabled: true,
      lastMeaningfulActivityAt: latestActivityAt.toISOString(),
      nextInactivityControlAt: nextControlAt.toISOString(),
      inactivityControlExclusionReason: '',
      eventHistory: '',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const activitySummary = await screen.findByLabelText(/Автоконтроль: Проверка .+ Последнее действие:/u);
    expect(activitySummary.className).toContain('border-l-cyan-500');
    const manualSummary = await screen.findByLabelText('Судебная проверка: Отмечено 18.05.2026');
    expect(manualSummary.className).toContain('border-l-cyan-500');
  });

  it('shows why inactivity control is not needed instead of applying a local day threshold', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      lastMeaningfulActivityAt: '2026-01-01T10:00:00Z',
      nextInactivityControlAt: null,
      inactivityControlExclusionReason: 'Есть активная задача',
    }));
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: '2026-01-01T10:00:00Z',
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
      lastViewedByResponsibleAt: '2026-01-01T10:00:00Z',
      lastViewedByResponsibleUserId: 7,
      lastViewedByResponsibleUserName: 'Кожеваткин Андрей Владимирович',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: null,
      inactivityRemindersEnabled: true,
      lastMeaningfulActivityAt: '2026-01-01T10:00:00Z',
      nextInactivityControlAt: null,
      inactivityControlExclusionReason: 'Есть активная задача',
      eventHistory: '',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const activitySummary = await screen.findByLabelText(/Автоконтроль: Есть активная задача/u);
    expect(activitySummary.className).toContain('border-l-emerald-500');
    expect(screen.getByText('Отдельная контрольная задача пока не требуется')).toBeTruthy();
  });

  it('highlights an inactivity control whose server deadline has arrived', async () => {
    const latestActivityAt = '2026-01-01T10:00:00Z';
    const nextControlAt = '2026-01-15T10:00:00Z';
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      lastMeaningfulActivityAt: latestActivityAt,
      nextInactivityControlAt: nextControlAt,
      inactivityControlExclusionReason: '',
    }));
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: latestActivityAt,
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
      lastViewedByResponsibleAt: latestActivityAt,
      lastViewedByResponsibleUserId: 7,
      lastViewedByResponsibleUserName: 'Кожеваткин Андрей Владимирович',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: null,
      inactivityRemindersEnabled: true,
      lastMeaningfulActivityAt: latestActivityAt,
      nextInactivityControlAt: nextControlAt,
      inactivityControlExclusionReason: '',
      eventHistory: '',
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const activitySummary = await screen.findByLabelText(/Автоконтроль: Пора проверить\. Срок контроля:/u);
    expect(activitySummary.className).toContain('border-l-amber-500');
  });

  it('shows the latest confirmed source check instead of a newer manual mark', async () => {
    vi.mocked(apiService.getCaseCourtSyncLogs).mockResolvedValue([
      {
        id: 2,
        clientCaseId: 15,
        status: 'manual-check-recorded',
        message: 'Ручная проверка судебного источника зафиксирована.',
        occurredAt: '2026-05-19T10:30:00Z',
      },
      {
        id: 1,
        clientCaseId: 15,
        status: 'external-sync-completed',
        message: 'Судебный источник проверен, новых изменений не найдено.',
        occurredAt: '2026-05-18T10:30:00Z',
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const sourceSummary = await screen.findByLabelText('Судебная проверка: Проверен 18.05.2026');
    expect(sourceSummary.className).toContain('border-l-emerald-500');
  });

  it('shows a later court sync error even when case history contains a successful phrase', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      eventHistory: '18.05.2026 10:30: Ручная проверка судебного источника зафиксирована.',
    }));
    vi.mocked(apiService.getCaseCourtSyncLogs).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 15,
        status: 'external-sync-completed',
        message: 'Судебный источник проверен, новых изменений не найдено.',
        occurredAt: '2026-05-18T10:30:00Z',
      },
      {
        id: 2,
        clientCaseId: 15,
        status: 'external-sync-error',
        message: 'Проверка судебного источника не выполнена.',
        occurredAt: '2026-05-20T10:30:00Z',
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const errorSummary = await screen.findByLabelText('Судебная проверка: Ошибка 20.05.2026');
    expect(errorSummary.className).toContain('border-l-rose-500');
    expect(screen.queryByLabelText(/Судебная проверка: Проверен/u)).toBeNull();
  });

  it('reports an unavailable court sync status when its journal cannot be loaded', async () => {
    vi.mocked(apiService.getCaseCourtSyncLogs)
      .mockRejectedValueOnce(new Error('network unavailable'))
      .mockResolvedValue([]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const unavailableSummary = await screen.findByLabelText('Судебная проверка: Статус недоступен');
    expect(unavailableSummary.className).toContain('border-l-amber-500');

    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('network unavailable')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить' }));

    await waitFor(() => {
      expect(apiService.getCaseCourtSyncLogs).toHaveBeenCalledTimes(2);
      expect(screen.getByText('Проверок судебного источника по делу пока не было.')).toBeTruthy();
    });
  });

  it('does not require a court binding for a pretrial case', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      stage: 'Досудебная работа',
      caseType: 'Претензионная работа',
      courtName: null,
      courtCaseNumber: null,
      courtUrl: null,
      judicialUid: null,
    }));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    const notRequiredSummary = await screen.findByLabelText('Судебная проверка: Не требуется');
    expect(notRequiredSummary.className).toContain('border-l-slate-300');
    expect(apiService.getCaseCourtSyncLogs).not.toHaveBeenCalled();
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

  it('warns when finance totals include only visible payments', async () => {
    const fullFinance = await apiService.getCaseFinance(15);
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ hasCompletePaymentVisibility: false }));
    vi.mocked(apiService.getCaseFinance).mockResolvedValue({
      ...fullFinance,
      hasCompletePaymentVisibility: false,
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(await screen.findByText(/Финансовые суммы выше учитывают только доступные вам платежи/u)).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Финансы' }).at(-1)!);

    expect(await screen.findByText(/Показаны только доступные вам платежи/u)).toBeTruthy();
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
    expect(screen.queryByRole('combobox', { name: 'Шаблон позиции' })).toBeNull();
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
        clientCaseId: 15,
        documentKey: 'claim',
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

  it.each([false, true])('shows pending court summaries while deadline controls are already saved (overview=%s)', async (overview) => {
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'court', courtOverview: overview }));
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValue({
      clientCaseId: 15, status: 'external-sync-completed', hasCourtBinding: true,
      message: 'Судебный источник обновлен.', checkedAt: '2026-05-18T10:00:00Z',
      checkedByUserName: 'Автор', courtName: 'Суд', courtCaseNumber: '1', courtUrl: 'https://court.example.test/case',
      createdCourtActDocuments: 0, pendingCourtActDocuments: 2, createdProceduralDeadlines: 1,
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Проверить суд по делу' }));
    expect(await screen.findByText(/сводки ожидают обязательных копий: 2; сроки и напоминания сохранены/u)).toBeTruthy();
    expect(screen.queryByText(/новых изменений не найдено/u)).toBeNull();
  });

  it.each([false, true])('preserves the court source conflict message when no documents were published (overview=%s)', async (overview) => {
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'court', courtOverview: overview }));
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValue({
      clientCaseId: 15, status: 'external-sync-completed', hasCourtBinding: true,
      message: 'Судебный источник обновлен: требует проверки конфликтов: 1.', checkedAt: '2026-05-18T10:00:00Z',
      checkedByUserName: 'Автор', courtName: 'Суд', courtCaseNumber: '1', courtUrl: 'https://court.example.test/case',
      createdCourtActDocuments: 0, pendingCourtActDocuments: 0, conflictItems: ['Сводка требует проверки источника'],
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Проверить суд по делу' }));
    expect(await screen.findByText('Судебный источник обновлен: требует проверки конфликтов: 1.')).toBeTruthy();
    expect(screen.queryByText(/новых изменений не найдено/u)).toBeNull();
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

  it('opens the court card in a read-first overview from the court registry', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'court', courtOverview: true }),
    );
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([{
      id: 91,
      clientCaseId: 15,
      deadlineType: 'motion',
      deadlineTypeLabel: 'Ходатайство',
      title: 'Подать ходатайство до конца дня',
      dueAt: '2099-06-20T00:00:00Z',
      allDay: true,
      status: 'active',
      statusLabel: 'Активен',
      warningDaysBefore: 7,
      sourceType: 'sudrf',
      sourceConfidence: 'official-source',
      createdAt: '2026-05-21T00:00:00Z',
      isOverdue: false,
      isWarning: false,
    }]);
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValue([
      {
        id: 81,
        clientCaseId: 15,
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        judicialUid: '63RS0018-01-2026-001242-18',
        courtUrl: 'https://kad.arbitr.ru/card',
        createdAt: '2026-05-21T00:00:00Z',
        complaints: [],
      },
      {
        id: 0,
        clientCaseId: 15,
        instance: 'appeal',
        instanceLabel: 'Апелляция',
        judicialUid: '63RS0018-01-2026-001242-18',
        createdAt: '2026-05-21T00:00:00Z',
        complaints: [],
      },
    ]);
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        canRecordHearingOutcome: false,
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
        movementStatus: 'completed',
        movementStatusLabel: 'Завершено',
        sourceType: 'sudrf',
        sourceConfidence: 'official-source',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
      {
        canRecordHearingOutcome: false,
        id: 42,
        clientCaseId: 15,
        eventType: 'movement',
        eventTypeLabel: 'Движение дела',
        title: 'Срок для представления документов',
        scheduledAt: '2026-06-13T00:00:00Z',
        allDay: true,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        movementStatus: 'completed',
        movementStatusLabel: 'Завершено',
        sourceType: 'sudrf',
        sourceConfidence: 'official-source',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
      {
        canRecordHearingOutcome: false,
        id: 43,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Будущее судебное заседание',
        scheduledAt: '2099-06-20T09:30:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        sourceType: 'sudrf',
        sourceConfidence: 'official-source',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
    ]);
    vi.mocked(apiService.getCaseCourtSyncLogs).mockResolvedValue([
      {
        id: 91,
        clientCaseId: 15,
        status: 'external-sync-completed',
        message: 'Судебный источник успешно проверен, изменений нет.',
        occurredAt: '2026-09-15T06:30:00Z',
        actorName: 'Система',
      },
      {
        id: 90,
        clientCaseId: 15,
        status: 'manual-check-recorded',
        message: 'Ручная проверка зафиксирована.',
        occurredAt: '2026-09-16T07:30:00Z',
        actorName: 'Администратор',
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Сводка по судебному делу')).toBeTruthy();
    expect(screen.getByText('Что происходит по делу')).toBeTruthy();
    expect(screen.getByText('Следующее действие')).toBeTruthy();
    expect(screen.getByText('Риск')).toBeTruthy();
    expect(screen.getByText('Решение не обнаружено')).toBeTruthy();
    expect(screen.getByText('Обжалование пока не определяется')).toBeTruthy();
    expect(screen.getAllByText('Основание: актуальное движение дела').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Будущее судебное заседание').length).toBeGreaterThan(0);
    expect(screen.getByText(/Дата: 20\.06\.2099, \d{2}:\d{2}/u)).toBeTruthy();
    expect(screen.getByText('до 20.06.2099 · весь день')).toBeTruthy();
    expect(await screen.findByText(/Источник проверен 15\.09\.2026/u)).toBeTruthy();
    expect(screen.getByText('Инстанции и судебные карточки')).toBeTruthy();
    expect(screen.getAllByText('Заседание назначено').length).toBeGreaterThan(0);
    expect(screen.getByText('Стадия по событиям')).toBeTruthy();
    expect(screen.getByText('Заседания и движения')).toBeTruthy();
    expect(screen.getByText('Лента процессуальной истории')).toBeTruthy();
    expect(screen.getAllByText('13.06.2026 · весь день').length).toBeGreaterThan(0);
    expect(screen.getByText('1 заполнено')).toBeTruthy();
    expect(screen.getByTestId('court-event-cards')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Редактировать данные' })).toBeTruthy();
    expect(screen.queryByText('Режим редактирования судебных данных')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Редактировать данные' }));
    expect(await screen.findByText('Режим редактирования судебных данных')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));
    expect(await screen.findByText('Сводка по судебному делу')).toBeTruthy();
  });

  it('keeps archived source events in history without showing them as current movement', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'court', courtOverview: true }),
    );
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        canRecordHearingOutcome: false,
        id: 41,
        clientCaseId: 15,
        eventType: 'movement',
        eventTypeLabel: 'Движение дела',
        title: 'Актуальная передача материалов судье',
        scheduledAt: '2026-06-10T09:30:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'completed',
        movementStatusLabel: 'Завершено',
        sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
      {
        canRecordHearingOutcome: false,
        id: 42,
        clientCaseId: 15,
        eventType: 'decision',
        eventTypeLabel: 'Судебное решение',
        title: 'Архивное решение суда',
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

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Сводка по судебному делу')).toBeTruthy();
    expect(screen.getByText('Решение не обнаружено')).toBeTruthy();
    const currentMovementSection = screen.getByText('Заседания и движения').closest('section');
    expect(currentMovementSection).toBeTruthy();
    expect(within(currentMovementSection!).getAllByText('Актуальная передача материалов судье').length).toBe(2);
    expect(within(currentMovementSection!).queryByText('Архивное решение суда')).toBeNull();
    expect(within(currentMovementSection!).getByText('В истории: 1')).toBeTruthy();
    const historySection = screen.getByText('Лента процессуальной истории').closest('section');
    expect(historySection).toBeTruthy();
    expect(within(historySection!).getByText('Архивное решение суда')).toBeTruthy();
    expect(within(historySection!).getByText('Архив источника')).toBeTruthy();
  });

  it('shows an issued appeal decision from the result of a completed complaint hearing', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'court', courtOverview: true }),
    );
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([{
      canRecordHearingOutcome: false,
      id: 51,
      clientCaseId: 15,
      eventType: 'hearing',
      eventTypeLabel: 'Заседание',
      title: 'Рассмотрение апелляционной жалобы',
      scheduledAt: '2026-09-14T10:00:00Z',
      allDay: false,
      courtInstance: 'appeal',
      courtInstanceLabel: 'Апелляция',
      movementStatus: 'completed',
      movementStatusLabel: 'Завершено',
      resultSummary: 'Вынесено решение по делу: решение оставлено без изменения',
      sourceType: 'sudrf',
      createdAt: '2026-09-14T10:00:00Z',
      isOverdue: false,
    }]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Сводка по судебному делу')).toBeTruthy();
    expect(screen.getByText('Решение апелляции зафиксировано')).toBeTruthy();
    expect(screen.getAllByText('Решение зафиксировано').length).toBeGreaterThan(0);
  });

  it('does not show court conclusions while source data is still loading', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'court', courtOverview: true }),
    );
    vi.mocked(apiService.getCaseCourtEvents).mockImplementation(() => new Promise(() => {}));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByLabelText('Проверяем судебные данные')).toBeTruthy();
    expect(screen.queryByText('Решение не обнаружено')).toBeNull();
    expect(screen.queryByText('Обжалование пока не определяется')).toBeNull();
  });

  it('shows a source warning instead of unsupported court conclusions', async () => {
    window.sessionStorage.setItem(
      'pp.case-detail.15.view.v1',
      JSON.stringify({ activeTab: 'court', courtOverview: true }),
    );
    vi.mocked(apiService.getCaseCourtEvents).mockRejectedValue(new Error('Failed to fetch'));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Автоматическая оценка недоступна')).toBeTruthy();
    expect(screen.getAllByText(/Не удалось загрузить движение судебного дела/u).length).toBeGreaterThan(0);
    expect(screen.queryByText('Решение не обнаружено')).toBeNull();
    expect(screen.queryByText('Обжалование пока не определяется')).toBeNull();
  });

  it('includes overdue procedural deadlines in the case-wide risk indicator', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ overdueTaskCount: 0 }));
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([{
      id: 91,
      clientCaseId: 15,
      deadlineType: 'appeal',
      deadlineTypeLabel: 'Апелляция',
      title: 'Срок апелляционного обжалования',
      dueAt: '2026-05-19T00:00:00Z',
      allDay: true,
      status: 'active',
      statusLabel: 'Просрочен',
      warningDaysBefore: 7,
      sourceType: 'sudrf',
      createdAt: '2026-05-01T00:00:00Z',
      isOverdue: true,
      isWarning: true,
    }]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    const riskValue = await screen.findByText('Просрочено сроков: 1');
    expect(riskValue.parentElement?.className).toContain('border-l-rose-500');
    expect(riskValue.parentElement?.className).toContain('bg-rose-50/50');
    expect(screen.getByLabelText('Контроль сроков: Просрочено сроков: 1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('19.05.2026 · весь день · Просрочен')).toBeTruthy();
    expect(screen.queryByText(/19\.05\.2026, 00:00/u)).toBeNull();
  });

  it('does not show an unverified safe state while procedural deadlines are loading', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ overdueTaskCount: 0 }));
    vi.mocked(apiService.getCaseProceduralDeadlines).mockImplementation(() => new Promise(() => {}));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(screen.getByLabelText('Контроль сроков: Проверяем сроки')).toBeTruthy();
    expect(screen.getByLabelText('Ближайший срок: Проверяем сроки')).toBeTruthy();
    expect(screen.queryByText('Без риска')).toBeNull();
  });

  it('shows a visible warning when procedural deadlines cannot be checked', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ overdueTaskCount: 0 }));
    vi.mocked(apiService.getCaseProceduralDeadlines).mockRejectedValue(new Error('Failed to fetch'));

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByLabelText('Контроль сроков: Сроки недоступны')).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toContain('Не удалось проверить процессуальные сроки.');
    expect(screen.getByRole('alert').textContent).toContain('Нажмите «Обновить»');
  });

  it('shows the earliest upcoming case date across the case, hearing and procedural deadlines', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      dueDate: '2099-07-01',
      nextHearingAt: '2099-06-20T09:30:00Z',
      overdueTaskCount: 0,
    }));
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([{
      id: 92,
      clientCaseId: 15,
      deadlineType: 'appeal',
      deadlineTypeLabel: 'Апелляция',
      title: 'Срок апелляционного обжалования',
      dueAt: '2099-06-10T00:00:00Z',
      allDay: true,
      status: 'active',
      statusLabel: 'Активен',
      warningDaysBefore: 7,
      sourceType: 'sudrf',
      createdAt: '2099-05-01T00:00:00Z',
      isOverdue: false,
      isWarning: false,
    }]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('10.06.2099 · весь день · Апелляция')).toBeTruthy();
  });

  it('prioritizes an exact hearing time over all-day dates in the same day', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({
      dueDate: '2099-06-20',
      nextHearingAt: '2099-06-20T09:30:00Z',
      overdueTaskCount: 0,
    }));
    vi.mocked(apiService.getCaseProceduralDeadlines).mockResolvedValue([{
      id: 93,
      clientCaseId: 15,
      deadlineType: 'motion',
      deadlineTypeLabel: 'Ходатайство',
      title: 'Подать ходатайство до конца дня',
      dueAt: '2099-06-20T00:00:00Z',
      allDay: true,
      status: 'active',
      statusLabel: 'Активен',
      warningDaysBefore: 7,
      sourceType: 'sudrf',
      createdAt: '2099-05-01T00:00:00Z',
      isOverdue: false,
      isWarning: false,
    }]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('20.06.2099, 13:30 · Заседание')).toBeTruthy();
    expect(screen.getByLabelText('Контроль сроков: Просрочек нет')).toBeTruthy();
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

  it('lets the lawyer identify who filed a court complaint', async () => {
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'court' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('Режим редактирования судебных данных')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Кем подана жалоба'), { target: { value: 'opponent' } });
    fireEvent.change(screen.getByPlaceholderText('Например, частная жалоба на определение суда'), { target: { value: 'Апелляция против нас' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить жалобу' }));

    await waitFor(() => expect(apiService.createCaseCourtComplaint).toHaveBeenCalledWith(
      15,
      'first',
      expect.objectContaining({ title: 'Апелляция против нас', filedBy: 'opponent' }),
    ));
  });

  it('requires the filing date before saving a filed appeal with a known side', async () => {
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'court' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);

    expect(await screen.findByText('Режим редактирования судебных данных')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Кем подана жалоба'), { target: { value: 'ours' } });
    fireEvent.change(screen.getByLabelText('Статус жалобы'), { target: { value: 'filed' } });
    fireEvent.change(screen.getByPlaceholderText('Например, частная жалоба на определение суда'), { target: { value: 'Наша апелляция' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить жалобу' }));

    expect(await screen.findByText(/Укажите дату подачи апелляционной жалобы/)).toBeTruthy();
    expect(apiService.createCaseCourtComplaint).not.toHaveBeenCalled();
  });

  it('shows a compact court sync summary in overview without duplicating the technical message', async () => {
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'court' }));
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValueOnce({
      clientCaseId: 15,
      status: 'external-sync-completed',
      hasCourtBinding: true,
      message: 'Технический подробный результат синхронизации, который не должен занимать место над карточкой.',
      checkedAt: '2026-07-29T15:00:00Z',
      syncedFromExternalSource: true,
      createdCourtEvents: 13,
      createdCourtParties: 3,
      createdCourtActDocuments: 2,
      createdProceduralDeadlines: 1,
      newItems: [],
      changedItems: [],
      removedItems: [],
      conflictItems: [],
    });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    expect(await screen.findByText('Режим редактирования судебных данных')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));
    expect(await screen.findByText('Сводка по судебному делу')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Проверить источник/u }));

    expect(await screen.findByText(/Судебный источник обновлён: 13 событий, 3 участника, 2 сводки судебных событий, 1 процессуальный срок/u)).toBeTruthy();
    expect(screen.queryByText(/Технический подробный результат синхронизации/u)).toBeNull();
    expect(screen.getByText('Сводка по судебному делу')).toBeTruthy();
    expect(screen.queryByText('Режим редактирования судебных данных')).toBeNull();
    expect(JSON.parse(window.sessionStorage.getItem('pp.case-detail.15.view.v1') || '{}').courtOverview).toBe(true);
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
    // The court-instance request initializes this draft after the tab renders.
    // Wait for that initialization before editing the source and searching.
    await screen.findByDisplayValue('Арбитражный суд Самарской области');
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

  it('does not show first-instance court details in an appeal card with missing fields', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase({ judicialUid: 'UID-FIRST-ONLY' }));
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValueOnce([
      {
        id: 81,
        clientCaseId: 15,
        instance: 'first',
        instanceLabel: 'Первая инстанция',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        judicialUid: 'UID-FIRST-ONLY',
        courtUrl: 'https://kad.arbitr.ru/card',
        createdAt: '2026-05-21T00:00:00Z',
        complaints: [],
      },
      {
        id: 82,
        clientCaseId: 15,
        instance: 'appeal',
        instanceLabel: 'Апелляция',
        courtName: 'Самарский областной суд',
        createdAt: '2026-05-21T00:00:00Z',
        complaints: [],
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Вернуться к обзору' }));
    fireEvent.click(screen.getByRole('button', { name: /Апелляция Номер не указан/u }));

    expect(screen.getAllByText('Самарский областной суд').length).toBeGreaterThan(0);
    expect(screen.queryByText('UID-FIRST-ONLY')).toBeNull();
    expect(within(screen.getByText('Номер карточки').parentElement!).getByText('Не указан')).toBeTruthy();
    expect(within(screen.getByText('Последний зафиксированный итог').parentElement!).getByText('—')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Открыть официальную карточку суда' })).toBeNull();
  });

  it('keeps the first-instance tab visible before an appeal without inventing its result', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([]);
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase({
      courtName: 'Апелляционный суд', courtCaseNumber: '33-42/2026', courtUrl: 'https://court.example.test/appeal', judicialUid: 'UID-APPEAL',
    }));
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValueOnce([{
      id: 82,
      clientCaseId: 15,
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      courtName: 'Апелляционный суд',
      courtCaseNumber: '33-42/2026',
      courtUrl: 'https://court.example.test/appeal',
      judicialUid: 'UID-APPEAL',
      createdAt: '2026-05-21T00:00:00Z',
      complaints: [],
    }]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Вернуться к обзору' }));
    const section = screen.getByText('Инстанции и судебные карточки').closest('section');
    expect(section).toBeTruthy();
    const courtTabs = within(section!);
    expect(courtTabs.getByRole('button', { name: /1-я инстанция/u })).toBeTruthy();
    expect(courtTabs.getByRole('button', { name: /Апелляция/u })).toBeTruthy();
    expect(courtTabs.getByText('1 заполнено')).toBeTruthy();

    fireEvent.click(courtTabs.getByRole('button', { name: /1-я инстанция/u }));
    expect(courtTabs.getByRole('note').textContent).toContain('Данные и результат первой инстанции пока не подтверждены');
    expect(within(courtTabs.getByText('Суд').parentElement!).getByText('Не указан')).toBeTruthy();
    expect(courtTabs.queryByText('UID-APPEAL')).toBeNull();
  });

  it('keeps a selected empty appeal visible in the court overview', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    fireEvent.click(screen.getByRole('button', { name: /Апелляция/u }));
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));

    const section = screen.getByText('Инстанции и судебные карточки').closest('section');
    expect(section).toBeTruthy();
    const courtTabs = within(section!);
    expect(courtTabs.getByRole('button', { name: /1-я инстанция/u })).toBeTruthy();
    expect(courtTabs.getByRole('button', { name: /Апелляция/u })).toBeTruthy();
  });

  it('counts a first-instance UID as recorded data even without a court number', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([]);
    vi.mocked(apiService.getCaseCourtInstances).mockResolvedValueOnce([
      {
        id: 81, clientCaseId: 15, instance: 'first', instanceLabel: '1-я инстанция', judicialUid: 'UID-FIRST',
        createdAt: '2026-05-20T00:00:00Z', complaints: [],
      },
      {
        id: 82, clientCaseId: 15, instance: 'appeal', instanceLabel: 'Апелляция', courtName: 'Апелляционный суд',
        courtCaseNumber: '33-42/2026', createdAt: '2026-05-21T00:00:00Z', complaints: [],
      },
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Вернуться к обзору' }));
    const section = screen.getByText('Инстанции и судебные карточки').closest('section');
    expect(section).toBeTruthy();
    const courtTabs = within(section!);
    expect(courtTabs.getByText('2 заполнено')).toBeTruthy();
    expect(courtTabs.queryByRole('note')).toBeNull();
  });

  it('does not include another instance details when searching an appeal court card', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase({ judicialUid: 'UID-FIRST-ONLY' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    const instancesSection = screen.getByText('Инстанции судов').closest('section');
    expect(instancesSection).toBeTruthy();
    fireEvent.click(within(instancesSection!).getByRole('button', { name: /Апелляция/u }));
    fireEvent.change(screen.getByPlaceholderText('https://...sudrf.ru/...'), {
      target: { value: 'https://example.test/appeal' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Найти/u }));

    await waitFor(() => expect(apiService.searchCaseCourtSyncCases).toHaveBeenCalledWith(15, {
      sourceUrl: 'https://example.test/appeal',
      courtCaseNumber: null,
      judicialUid: null,
      courtName: null,
      instance: 'appeal',
    }));
  });

  it('does not copy first-instance court details into a new appeal event', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Суд' }));
    const instancesSection = screen.getByText('Инстанции судов').closest('section');
    expect(instancesSection).toBeTruthy();
    fireEvent.click(within(instancesSection!).getByRole('button', { name: /Апелляция/u }));

    const titleInput = screen.getByPlaceholderText('Например, предварительное заседание');
    const eventForm = titleInput.closest('.grid');
    expect(eventForm).toBeTruthy();
    fireEvent.change(titleInput, { target: { value: 'Заседание апелляции' } });
    fireEvent.change(eventForm!.querySelector('input[type="datetime-local"]')!, {
      target: { value: '2026-09-25T10:00' },
    });
    fireEvent.click(within(eventForm as HTMLElement).getByRole('button', { name: 'Добавить' }));

    await waitFor(() => expect(apiService.createCaseCourtEvent).toHaveBeenCalledWith(15, expect.objectContaining({
      courtInstance: 'appeal',
      title: 'Заседание апелляции',
      courtName: undefined,
      courtCaseNumber: undefined,
      courtroom: undefined,
    })));
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
