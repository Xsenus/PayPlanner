import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';

import { documentCopyTestSession } from '../../test/documentCopySession';
import { captureAuthSessionIdentity } from '../../services/authService';
import { StrictMode } from 'react';

function detailViewStateKey(caseId: number) {
  return `pp.case-detail.${caseId}.view.v2.${encodeURIComponent(captureAuthSessionIdentity().actor!)}`;
}
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import type { ClientCase, ClientCaseRelatedParty, ClientCaseCourtEvent, ClientCaseDocument, ClientCaseWorkOrder } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CaseDetailPage, CourtSourceProvenance } from './CaseDetailPage';
const detailAuth = vi.hoisted(() => ({ user: { id: 7, role: { id: 10 } } }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => detailAuth }));

function createJournalEvent(overrides: Partial<ClientCaseCourtEvent> = {}): ClientCaseCourtEvent {
  return { id: 80, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание', title: 'Заседание',
    scheduledAt: '2026-10-12T09:30:00Z', allDay: false, courtInstance: 'first', courtInstanceLabel: 'Первая инстанция',
    movementStatus: 'scheduled', movementStatusLabel: 'Назначено', sourceType: 'manual',
    createdAt: '2026-10-01T00:00:00Z', isOverdue: false, canRecordHearingOutcome: false, ...overrides };
}

function createCaseDocument(overrides: Partial<ClientCaseDocument> = {}): ClientCaseDocument {
  return { id: 7, clientCaseId: 15, documentKey: 'claim', version: 1,
    documentType: 'Заявление', status: 'Готово', originalFileName: 'Заявление.docx',
    contentType: 'application/pdf', sizeBytes: 1024, uploadedByName: 'Синтетический автор',
    uploadedAt: '2026-05-02T10:00:00Z', isLatestVersion: true, ...overrides };
}

function mockCaseTaskServer(items: TaskJournalItem[]) {
  const active = items.filter((item) => item.status !== 'done' && item.status !== 'cancelled');
  const overdue = active.filter((item) => Date.parse(item.dueAt) < Date.now());
  const completed = items.filter((item) => item.status === 'done' || item.status === 'cancelled');
  vi.mocked(apiService.getTaskJournalSummary).mockResolvedValue({
    total: items.length, completed: completed.length, active: active.length, overdue: overdue.length,
    today: 0, highPriority: 0, unbilled: 0,
  });
  vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => {
    const selected = query?.status === 'active' ? active : query?.status === 'overdue' ? overdue
      : query?.status === 'completed' ? completed : items;
    const page = query?.page ?? 1;
    const pageSize = query?.pageSize ?? 50;
    return { items: selected.slice((page - 1) * pageSize, page * pageSize), total: selected.length, page, pageSize };
  });
}

async function openDocumentDetails(fileName = 'Заявление.docx') {
  const name = `Сведения и действия документа ${fileName}`;
  const toggle = await screen.findByRole('button', { name });
  if (toggle.getAttribute('aria-expanded') !== 'true') fireEvent.click(toggle);
  return screen.getByRole('region', { name });
}

function setupDocumentUser() {
  const clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
  onTestFinished(() => {
    if (clipboard) Object.defineProperty(navigator, 'clipboard', clipboard);
    else Reflect.deleteProperty(navigator, 'clipboard');
  });
  return userEvent.setup();
}

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
    getTaskJournalSummary: vi.fn(),
    bulkUpdateTaskJournalItems: vi.fn(),
    getTaskJournalSettings: vi.fn(),
    getTaskJournalAssignees: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    getClients: vi.fn(),
    getClientsV2: vi.fn(),
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
    getMessengerDocumentDeliveryConfiguration: vi.fn(),
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
    setCourtRecoveryPayment: vi.fn(),
    removeCourtRecoveryPayment: vi.fn(),
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
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
      window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
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
    detailAuth.user = { id: 7, role: { id: 10 } };
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockReset();
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
    vi.mocked(apiService.getTaskJournalSummary).mockReset();
    mockCaseTaskServer([createTask()]);
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
    const directoryClients = [
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
    ];
    vi.mocked(apiService.getClients).mockResolvedValue(directoryClients);
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: directoryClients, total: directoryClients.length, page: 1, pageSize: 50 });
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
      pageSize: 50,
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
    vi.mocked(apiService.uploadCaseDocumentToYandexDisk).mockReset();
    vi.mocked(apiService.uploadCaseDocumentToYandexDisk).mockResolvedValue({ success: true, path: '/synthetic/document.pdf' });
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

  it('uses the supplied return label and keeps the case registry label by default', async () => {
    const onBack = vi.fn();
    const view = render(<CaseDetailPage caseId={15} onBack={onBack} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Реестр дел' }));
    view.rerender(<CaseDetailPage caseId={15} onBack={onBack} backLabel="К анализу оплаты задач" />);
    fireEvent.click(screen.getByRole('button', { name: 'К анализу оплаты задач' }));
    expect(onBack).toHaveBeenCalledTimes(2);
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

  it('retries a failed case load without navigating away', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValueOnce(new Error('Временно не удалось загрузить дело'));
    const onBack = vi.fn();
    render(<CaseDetailPage caseId={15} onBack={onBack} />);
    expect((await screen.findByRole('alert')).textContent).toContain('Временно не удалось загрузить дело');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    expect(onBack).not.toHaveBeenCalled();
  });

  it('keeps the error and lets the user return when a case remains unavailable', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValue(new Error('Дело недоступно'));
    const onBack = vi.fn();
    render(<CaseDetailPage caseId={15} onBack={onBack} />);
    await screen.findByText('Дело недоступно');
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Дело недоступно')).toBeTruthy();
    expect(onBack).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Вернуться к реестру' }));
    expect(onBack).toHaveBeenCalledTimes(1);
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
    await waitFor(() => expect(apiService.reviewCasePosition).toHaveBeenCalledWith(15, createCase().strategy));
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
      15, expect.objectContaining({ strategy: expect.stringContaining('Проверить доказательства'), courtRegisteredAt: '2026-05-13', clearCourtRegisteredAt: false }), expect.objectContaining({ id: 15 })
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
    await waitFor(() => expect(apiService.confirmNextHearing).toHaveBeenCalledWith(15, nextHearingAt));
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

  it('PAY-25 review: sorts UTC timestamps without a suffix consistently with explicit UTC timestamps', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      { canRecordHearingOutcome: false, id: 51, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание',
        title: 'Граница: ранее', scheduledAt: '2026-10-12T09:30:00Z', allDay: false, courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция', movementStatus: 'scheduled', movementStatusLabel: 'Назначено',
        sourceType: 'manual', createdAt: '2026-10-01T00:00:00Z', isOverdue: false },
      { canRecordHearingOutcome: false, id: 52, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание',
        title: 'Граница: позже', scheduledAt: '2026-10-12T10:00:00', allDay: false, courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция', movementStatus: 'scheduled', movementStatusLabel: 'Назначено',
        sourceType: 'manual', createdAt: '2026-10-01T00:00:00Z', isOverdue: false },
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await waitFor(() => expect(within(journal).getAllByText(/^Граница:/u).map(node => node.textContent))
      .toEqual(['Граница: позже', 'Граница: ранее']));
  });

  it('PAY-25 review: uses the loaded profile time zone, preserves all-day dates and skips forbidden data', async () => {
    let resolveTimeZone!: (value: { key: string; value: string }) => void;
    const pendingTimeZone = new Promise<{ key: string; value: string }>(resolve => { resolveTimeZone = resolve; });
    vi.mocked(apiService.getUserSetting).mockImplementation(key => key === 'profile.timeZoneId' ? pendingTimeZone : Promise.resolve(null));
    const event = { canRecordHearingOutcome: false, id: 61, clientCaseId: 15, eventType: 'hearing', eventTypeLabel: 'Заседание',
      title: 'Заседание у границы суток', scheduledAt: '2026-10-12T23:30:00Z', allDay: false, courtInstance: 'first',
      courtInstanceLabel: 'Первая инстанция', movementStatus: 'scheduled', movementStatusLabel: 'Назначено',
      sourceType: 'manual', createdAt: '2026-10-01T00:00:00Z', isOverdue: false };
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([event,
      { ...event, id: 62, title: 'Календарный день', scheduledAt: '2026-10-12T00:00:00Z', allDay: true }]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewTasks={false} canViewFinance={false} />);
    expect(screen.queryByRole('button', { name: 'События' })).toBeNull();
    await act(async () => { resolveTimeZone({ key: 'profile.timeZoneId', value: 'Europe/Samara' }); });
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('Заседание у границы суток');
    await waitFor(() => expect(within(journal).getByText('13.10.2026, 03:30')).toBeTruthy());
    expect(within(journal).getByText('12.10.2026 · весь день')).toBeTruthy();
    expect(apiService.getCaseFinance).not.toHaveBeenCalled();
    expect(apiService.getTaskJournalItems).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Быстрый доступ: Финансы' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Быстрый доступ: Задачи' })).toBeNull();
  });

  it.each([false, true])('court parties read: retries only the failed list in edit mode %s', async editMode => {
    vi.mocked(apiService.getCaseCourtParties).mockRejectedValueOnce(new Error('Failed to fetch'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    await screen.findByText('Не удалось загрузить участников процесса.');
    if (!editMode) {
      fireEvent.click(screen.getByRole('button', { name: 'Вернуться к обзору' }));
      expect(screen.getByRole('region', { name: 'Участники процесса' })).toBeTruthy();
    } else {
      expect(screen.getByText('Режим редактирования судебных данных')).toBeTruthy();
    }
    expect(screen.queryByText('Активные участники процесса не добавлены.')).toBeNull();
    expect(screen.queryByText('Стороны процесса пока не добавлены.')).toBeNull();
    const eventReads = vi.mocked(apiService.getCaseCourtEvents).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку участников' }));
    expect(await screen.findByText('ООО Ответчик')).toBeTruthy();
    expect(apiService.getCaseCourtParties).toHaveBeenCalledTimes(2);
    expect(apiService.getCaseCourtEvents).toHaveBeenCalledTimes(eventReads);
    expect(apiService.createCaseCourtParty).not.toHaveBeenCalled();
    expect(apiService.updateCaseV2).not.toHaveBeenCalled();
  });

  it('court parties read: ignores the previous case failure after switching cases', async () => {
    type Parties = Awaited<ReturnType<typeof apiService.getCaseCourtParties>>;
    let failOld!: (error: Error) => void;
    const previous = new Promise<Parties>((_, reject) => { failOld = reject; });
    const currentParty = { id: 81, clientCaseId: 16, displayName: 'Сторона текущего дела', side: 'respondent',
      sideLabel: 'Ответчик', processRole: 'Ответчик', isArchived: false, createdAt: '2026-10-28T09:00:00Z' };
    vi.mocked(apiService.getCaseCourtParties).mockImplementation(id => id === 15 ? previous : Promise.resolve([currentParty]));
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Дело 15');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    await waitFor(() => expect(apiService.getCaseCourtParties).toHaveBeenCalledWith(15, false, expect.objectContaining({ signal: expect.any(AbortSignal) })));
    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} />);
    await screen.findByText('Дело 16');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    expect(await screen.findByText('Сторона текущего дела')).toBeTruthy();
    await act(async () => { failOld(new Error('Ошибка прежнего дела')); });
    expect(screen.queryByText('Ошибка прежнего дела')).toBeNull();
    expect(screen.getByText('Сторона текущего дела')).toBeTruthy();
  });

  it('PAY-25 review: ignores delayed court events after switching cases', async () => {
    type Events = Awaited<ReturnType<typeof apiService.getCaseCourtEvents>>;
    let finishOld!: (events: Events) => void;
    const oldEvents = new Promise<Events>(resolve => { finishOld = resolve; });
    const event = { canRecordHearingOutcome: false, id: 71, clientCaseId: 16, eventType: 'hearing', eventTypeLabel: 'Заседание',
      title: 'Событие текущего дела', scheduledAt: '2026-10-12T10:00:00Z', allDay: false, courtInstance: 'first',
      courtInstanceLabel: 'Первая инстанция', movementStatus: 'scheduled', movementStatusLabel: 'Назначено',
      sourceType: 'manual', createdAt: '2026-10-01T00:00:00Z', isOverdue: false };
    vi.mocked(apiService.getCaseCourtEvents).mockImplementation(id => id === 15 ? oldEvents : Promise.resolve([event]));
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Дело 15');
    fireEvent.click(screen.getByRole('button', { name: 'События' }));
    await waitFor(() => expect(apiService.getCaseCourtEvents).toHaveBeenCalledWith(15, expect.objectContaining({ signal: expect.any(AbortSignal) })));
    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} />);
    await screen.findByText('Дело 16');
    fireEvent.click(screen.getByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('Событие текущего дела');
    await act(async () => { finishOld([{ ...event, id: 70, clientCaseId: 15, title: 'Запоздалое событие прошлого дела' }]); });
    expect(within(journal).getByText('Событие текущего дела')).toBeTruthy();
    expect(screen.queryByText('Запоздалое событие прошлого дела')).toBeNull();
  });

  it('PAY-25: shows the event journal newest first, marks the source and leads to the court tab to add an event', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      {
        canRecordHearingOutcome: false,
        id: 51,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Старое заседание',
        scheduledAt: '2026-06-01T09:00:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        sourceType: 'manual',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
      {
        canRecordHearingOutcome: false,
        id: 52,
        clientCaseId: 15,
        eventType: 'hearing',
        eventTypeLabel: 'Заседание',
        title: 'Новое заседание',
        scheduledAt: '2026-07-01T09:00:00Z',
        allDay: false,
        courtInstance: 'first',
        courtInstanceLabel: 'Первая инстанция',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        sourceType: 'sudrf',
        createdAt: '2026-05-21T00:00:00Z',
        isOverdue: false,
      },
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    const titles = within(journal).getAllByText(/заседание$/u).map((node) => node.textContent);
    expect(titles).toEqual(['Новое заседание', 'Старое заседание']);
    expect(within(journal).getByText('ГАС «Правосудие»')).toBeTruthy();
    expect(within(journal).getByRole('heading', { name: 'Судебные события (2)' })).toBeTruthy();

    fireEvent.click(within(journal).getByRole('button', { name: /Добавить событие/ }));
    expect(screen.queryByLabelText('Журнал событий дела')).toBeNull();
  });

  it('PAY-25 filters: combines search and type, resets the list and never refetches or grants creation', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      createJournalEvent({ title: 'Проверка заявления', description: 'Подготовка материалов', courtName: 'Арбитражный суд', resultSummary: 'Принято к рассмотрению' }),
      createJournalEvent({ id: 81, title: 'Получено определение', eventType: 'ruling', eventTypeLabel: 'Определение', description: 'Подготовка ответа' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('Получено определение');
    const reads = vi.mocked(apiService.getCaseCourtEvents).mock.calls.length;
    const search = within(journal).getByRole('searchbox', { name: 'Поиск событий' });
    for (const query of [' ПРОВЕРКА ', 'арбитражный', 'ПРИНЯТО']) {
      fireEvent.change(search, { target: { value: query } });
      expect(within(journal).getByText('Проверка заявления')).toBeTruthy();
      expect(within(journal).queryByText('Получено определение')).toBeNull();
    }
    fireEvent.change(search, { target: { value: 'подготовка' } });
    fireEvent.change(within(journal).getByLabelText('Тип события', { selector: 'select' }), { target: { value: 'ruling' } });
    expect(within(journal).getByText('Получено определение')).toBeTruthy();
    expect(within(journal).queryByText('Проверка заявления')).toBeNull();
    expect(within(journal).getByRole('status').textContent).toContain('Показано 1 из 2');
    fireEvent.change(search, { target: { value: 'нет совпадений' } });
    expect(within(journal).getByText('Нет судебных событий, соответствующих фильтрам.')).toBeTruthy();
    fireEvent.click(within(journal).getByRole('button', { name: 'Сбросить фильтры событий' }));
    expect(within(journal).getByText('Проверка заявления')).toBeTruthy();
    expect(within(journal).getByText('Получено определение')).toBeTruthy();
    expect(apiService.getCaseCourtEvents).toHaveBeenCalledTimes(reads);
    expect(within(journal).queryByRole('button', { name: /Добавить событие/u })).toBeNull();
  });

  it('PAY-25 filters: uses inclusive profile dates and keeps all-day dates without shifting UTC or invalid data', async () => {
    vi.mocked(apiService.getUserSetting).mockImplementation(key => key === 'profile.timeZoneId'
      ? Promise.resolve({ key, value: 'Europe/Samara' }) : Promise.resolve(null));
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([
      createJournalEvent({ title: 'Событие после полуночи', scheduledAt: '2026-10-12T23:30:00' }),
      createJournalEvent({ id: 81, title: 'Календарный день 12', scheduledAt: '2026-10-12T00:00:00Z', allDay: true }),
      createJournalEvent({ id: 82, title: 'Конец выбранного дня', scheduledAt: '2026-10-13T19:59:59Z' }),
      createJournalEvent({ id: 83, title: 'Следующий день', scheduledAt: '2026-10-13T20:00:00Z' }),
      createJournalEvent({ id: 84, title: 'Неизвестная дата', scheduledAt: 'invalid-date', allDay: true }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('13.10.2026, 03:30');
    fireEvent.change(within(journal).getByLabelText('События с даты', { selector: 'input' }), { target: { value: '2026-10-13' } });
    fireEvent.change(within(journal).getByLabelText('События по дату', { selector: 'input' }), { target: { value: '2026-10-13' } });
    expect(within(journal).getByText('Событие после полуночи')).toBeTruthy();
    expect(within(journal).getByText('Конец выбранного дня')).toBeTruthy();
    for (const title of ['Календарный день 12', 'Следующий день', 'Неизвестная дата']) expect(within(journal).queryByText(title)).toBeNull();
    fireEvent.change(within(journal).getByLabelText('События с даты', { selector: 'input' }), { target: { value: '2026-10-12' } });
    fireEvent.change(within(journal).getByLabelText('События по дату', { selector: 'input' }), { target: { value: '2026-10-12' } });
    expect(within(journal).getByText('Календарный день 12')).toBeTruthy();
    expect(within(journal).queryByText('Событие после полуночи')).toBeNull();
  });

  it('PAY-25 filters: explains a reversed period and recovers when its dates are corrected', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockResolvedValue([createJournalEvent({ allDay: true })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    const journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('12.10.2026 · весь день');
    fireEvent.change(within(journal).getByLabelText('События с даты', { selector: 'input' }), { target: { value: '2026-10-13' } });
    fireEvent.change(within(journal).getByLabelText('События по дату', { selector: 'input' }), { target: { value: '2026-10-12' } });
    expect(within(journal).getByRole('alert').textContent).toContain('Начало периода должно быть не позже окончания.');
    expect(within(journal).getByRole('status').textContent).toContain('Показано 0 из 1');
    fireEvent.change(within(journal).getByLabelText('События с даты', { selector: 'input' }), { target: { value: '2026-10-12' } });
    expect(within(journal).queryByRole('alert')).toBeNull();
    expect(within(journal).getByRole('status').textContent).toContain('Показано 1 из 1');
  });

  it('PAY-25 filters: resets restrictions when changing to another case', async () => {
    vi.mocked(apiService.getCaseCourtEvents).mockImplementation(async id => [createJournalEvent({ clientCaseId: id, title: `Событие дела ${id}` })]);
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'События' }));
    let journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('Событие дела 15');
    fireEvent.change(within(journal).getByRole('searchbox', { name: 'Поиск событий' }), { target: { value: 'дела 15' } });
    fireEvent.change(within(journal).getByLabelText('События с даты', { selector: 'input' }), { target: { value: '2027-01-01' } });
    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} />);
    await screen.findByText('Дело 16');
    fireEvent.click(screen.getByRole('button', { name: 'События' }));
    journal = await screen.findByLabelText('Журнал событий дела');
    await within(journal).findByText('Событие дела 16');
    expect((within(journal).getByRole('searchbox', { name: 'Поиск событий' }) as HTMLInputElement).value).toBe('');
    expect((within(journal).getByLabelText('События с даты', { selector: 'input' }) as HTMLInputElement).value).toBe('');
    expect(within(journal).queryByRole('button', { name: 'Сбросить фильтры событий' })).toBeNull();
  });

  it('PAY-24: opens a case task through the common task card and shows how many tasks are listed', async () => {
    const onOpenTask = vi.fn();
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} onOpenTask={onOpenTask} />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Проверить движение по делу')).toBeTruthy();
    expect(screen.getByText(/Показано \d+ из \d+/u)).toBeTruthy();
    expect(screen.getByRole('heading', { name: /Задачи по делу \(\d+\)/u })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Открыть задачу: Проверить движение по делу' }));
    expect(onOpenTask).toHaveBeenCalledTimes(1);
    expect(onOpenTask).toHaveBeenCalledWith(expect.any(Number));
  });

  it('PAY-23 review: uses one document search and keeps filters available after an empty result', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByText('Заявление.docx');
    expect(screen.getAllByLabelText(/Поиск (?:по )?документ(?:ам|ов) дела/u)).toHaveLength(1);

    const search = screen.getByLabelText('Поиск документов дела');
    fireEvent.change(search, { target: { value: 'несуществующий файл' } });
    expect(screen.queryByText('Заявление.docx')).toBeNull();
    expect(screen.getByText('Показано 0 из 1')).toBeTruthy();
    expect(screen.getByLabelText('Фильтр документов по типу')).toBeTruthy();
    expect(screen.getByLabelText('Фильтр документов по статусу')).toBeTruthy();
    expect(screen.getByLabelText('Только актуальные версии')).toBeTruthy();
    fireEvent.change(search, { target: { value: '' } });
    expect(screen.getByText('Заявление.docx')).toBeTruthy();
  });

  it('shows a document loading error and retries without treating it as an empty list', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    vi.mocked(apiService.getCaseDocuments)
      .mockRejectedValueOnce(new Error('Network unavailable'))
      .mockRejectedValueOnce(new Error('Still unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Не удалось загрузить документы дела');
    expect(screen.queryByText('Заявление.docx')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку документов' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Не удалось загрузить документы дела');
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку документов' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ignores a late document error after leaving and reopening the documents tab', async () => {
    let reject!: (error: Error) => void;
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    vi.mocked(apiService.getCaseDocuments).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Обзор' }));
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    await act(async () => { reject(new Error('Late failure')); });
    expect(screen.getByText('Заявление.docx')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('finds canonically equivalent document names without changing their stored names', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 71, documentKey: 'one', originalFileName: 'Ёлка.pdf' }),
      createCaseDocument({ id: 72, documentKey: 'two', originalFileName: 'Е\u0308лка.docx' }),
      createCaseDocument({ id: 73, documentKey: 'three', originalFileName: 'Другой.pdf' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByRole('button', { name: 'Открыть предпросмотр документа Ёлка.pdf' });
    fireEvent.change(screen.getByLabelText('Поиск документов дела'), { target: { value: 'Е\u0308ЛКА' } });
    expect(screen.getByRole('button', { name: 'Открыть предпросмотр документа Ёлка.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Открыть предпросмотр документа Е\u0308лка.docx' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Открыть предпросмотр документа Другой.pdf' })).toBeNull();
    expect(screen.getByText('Показано 2 из 3')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Поиск документов дела'), { target: { value: 'Ёлка' } });
    expect(screen.getByRole('button', { name: 'Открыть предпросмотр документа Ёлка.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Открыть предпросмотр документа Е\u0308лка.docx' })).toBeTruthy();
    expect(screen.getByText('Показано 2 из 3')).toBeTruthy();
    expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(1);
  });

  it('shows one version history when document keys differ only in letter case', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 11, documentKey: 'Claim', version: 1, originalFileName: 'Версия 1.pdf', isLatestVersion: false }),
      createCaseDocument({ id: 12, documentKey: 'claim', version: 2, originalFileName: 'Версия 2.pdf', isLatestVersion: false }),
      createCaseDocument({ id: 13, documentKey: 'CLAIM', version: 3, originalFileName: 'Версия 3.pdf', isLatestVersion: true }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const details = await openDocumentDetails('Версия 3.pdf');
    expect(within(details).getByText('История версий: v1, v2, v3 · последняя v3')).toBeTruthy();
  });

  it('does not combine a missing document key with a real key resembling its fallback', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 11, documentKey: '', version: 1, originalFileName: 'Без ключа.pdf' }),
      createCaseDocument({ id: 12, documentKey: 'document-11', version: 2, originalFileName: 'Другой.pdf' }),
      createCaseDocument({ id: 13, documentKey: 'DOCUMENT-11', version: 3, originalFileName: 'Другой v3.pdf' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const missing = await openDocumentDetails('Без ключа.pdf');
    expect(within(missing).queryByText(/История версий/)).toBeNull();
    const other = await openDocumentDetails('Другой v3.pdf');
    expect(within(other).getByText('История версий: v2, v3 · последняя v3')).toBeTruthy();
  });

  it('does not merge distinct document keys merely because of a multi-letter uppercase expansion', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 21, documentKey: 'Straße', version: 1, originalFileName: 'Первая v1.pdf', versionGroupId: 21 }),
      createCaseDocument({ id: 22, documentKey: 'straße', version: 2, originalFileName: 'Первая v2.pdf', versionGroupId: 21 }),
      createCaseDocument({ id: 23, documentKey: 'STRASSE', version: 4, originalFileName: 'Вторая v4.pdf', versionGroupId: 23 }),
      createCaseDocument({ id: 24, documentKey: 'strasse', version: 5, originalFileName: 'Вторая v5.pdf', versionGroupId: 23 }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    expect(within(await openDocumentDetails('Первая v2.pdf')).getByText('История версий: v1, v2 · последняя v2')).toBeTruthy();
    expect(within(await openDocumentDetails('Вторая v5.pdf')).getByText('История версий: v4, v5 · последняя v5')).toBeTruthy();
  });

  it('uses server version groups when Unicode case rules differ', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 31, documentKey: 'ß', version: 1, originalFileName: 'Первая v1.pdf', versionGroupId: 31 } as Partial<ClientCaseDocument>),
      createCaseDocument({ id: 32, documentKey: 'ß', version: 2, originalFileName: 'Первая v2.pdf', versionGroupId: 31 } as Partial<ClientCaseDocument>),
      createCaseDocument({ id: 33, documentKey: 'ẞ', version: 4, originalFileName: 'Вторая v4.pdf', versionGroupId: 33 } as Partial<ClientCaseDocument>),
      createCaseDocument({ id: 34, documentKey: 'ẞ', version: 5, originalFileName: 'Вторая v5.pdf', versionGroupId: 33 } as Partial<ClientCaseDocument>),
      createCaseDocument({ id: 41, documentKey: 'ς', version: 1, originalFileName: 'Третья v1.pdf', versionGroupId: 41 } as Partial<ClientCaseDocument>),
      createCaseDocument({ id: 42, documentKey: 'σ', version: 2, originalFileName: 'Третья v2.pdf', versionGroupId: 41 } as Partial<ClientCaseDocument>),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    expect(within(await openDocumentDetails('Первая v2.pdf')).getByText('История версий: v1, v2 · последняя v2')).toBeTruthy();
    expect(within(await openDocumentDetails('Вторая v5.pdf')).getByText('История версий: v4, v5 · последняя v5')).toBeTruthy();
    expect(within(await openDocumentDetails('Третья v2.pdf')).getByText('История версий: v1, v2 · последняя v2')).toBeTruthy();
  });

  it('does not merge non-ASCII keys when the server does not provide groups', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 51, documentKey: 'ß', version: 1, originalFileName: 'Первая.pdf' }),
      createCaseDocument({ id: 52, documentKey: 'ẞ', version: 2, originalFileName: 'Вторая.pdf' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    expect(within(await openDocumentDetails('Первая.pdf')).queryByText(/История версий/)).toBeNull();
    expect(within(await openDocumentDetails('Вторая.pdf')).queryByText(/История версий/)).toBeNull();
  });

  it('finds and opens a synthetic document beyond the first 50 in the current complete list', async () => {
    const documents = Array.from({ length: 60 }, (_, index) => createCaseDocument({
      id: index + 1,
      documentKey: `doc-${index + 1}`,
      originalFileName: index === 59 ? 'Искомый договор.pdf' : `Документ ${index + 1}.pdf`,
    }));
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue(documents);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByRole('table', { name: 'Документы дела' });
    fireEvent.change(screen.getByLabelText('Поиск документов дела'), { target: { value: 'искомый договор' } });
    expect(screen.getByText('Показано 1 из 60')).toBeTruthy();
    expect((await openDocumentDetails('Искомый договор.pdf')).textContent).toContain('Искомый договор.pdf');
  });

  it('PAY-23 table: sorts instants with stable ID ties and invalid dates last without changing data or fetching files', async () => {
    const documents = [
      createCaseDocument({ id: 90, documentKey: 'invalid', originalFileName: 'Некорректная.pdf', uploadedAt: 'not-a-date' }),
      createCaseDocument({ id: 20, documentKey: 'utc', originalFileName: 'UTC.pdf', uploadedAt: '2026-05-05T10:00:00Z' }),
      createCaseDocument({ id: 50, documentKey: 'newest', originalFileName: 'Новый.pdf', uploadedAt: '2026-05-06T10:00:00Z' }),
      createCaseDocument({ id: 40, documentKey: 'null', originalFileName: 'Без даты.pdf', uploadedAt: null as unknown as string }),
      createCaseDocument({ id: 10, documentKey: 'naive', originalFileName: 'Без суффикса.pdf', uploadedAt: '2026-05-05T10:00:00' }),
      createCaseDocument({ id: 60, documentKey: 'empty', originalFileName: 'Пустая дата.pdf', uploadedAt: '' }),
      createCaseDocument({ id: 30, documentKey: 'offset', originalFileName: 'Со смещением.pdf', uploadedAt: '2026-05-05T14:00:00+04:00' }),
    ];
    const originalIds = documents.map(item => item.id);
    documents.forEach(Object.freeze);
    Object.freeze(documents);
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue(documents);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const table = await screen.findByRole('table', { name: 'Документы дела' });
    const rows = () => within(table).getAllByRole('row', { name: /^Документ /u })
      .map(row => row.getAttribute('aria-label')?.replace(/^Документ /u, ''));
    const missing = ['Без даты.pdf', 'Пустая дата.pdf', 'Некорректная.pdf'];
    const sameInstant = ['Без суффикса.pdf', 'UTC.pdf', 'Со смещением.pdf'];
    expect(rows()).toEqual(['Новый.pdf', ...sameInstant, ...missing]);
    const sort = within(table).getByRole('button', { name: 'Сортировать документы по дате' });
    expect(sort.closest('th')?.getAttribute('aria-sort')).toBe('descending');
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    fireEvent.click(sort);
    expect(rows()).toEqual([...sameInstant, 'Новый.pdf', ...missing]);
    expect(sort.closest('th')?.getAttribute('aria-sort')).toBe('ascending');
    fireEvent.click(sort);
    expect(rows()).toEqual(['Новый.pdf', ...sameInstant, ...missing]);
    for (const name of missing) {
      expect(within(within(table).getByRole('row', { name: `Документ ${name}` })).getAllByRole('cell')[1].textContent).toBe('—');
    }
    expect(documents.map(item => item.id)).toEqual(originalIds);
    expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads);
    expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();
    expect(apiService.uploadCaseDocumentToYandexDisk).not.toHaveBeenCalled();
  });

  it('PAY-23 table: combines filters while retaining the complete version history and court source details', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument({ id: 11, documentKey: 'history', version: 1, originalFileName: 'История v1.pdf', status: 'Черновик', isLatestVersion: false }),
      createCaseDocument({ id: 13, documentKey: 'history', version: 3, originalFileName: 'История v3.pdf', documentType: 'Иск', status: 'Подписан',
        uploadedByName: 'Автор для поиска', templateLabel: 'Полный шаблон', sourceType: 'sudrf',
        sourceUrl: 'https://example.sudrf.ru/document/13', sourceConfidence: 'official', sourceCheckedAt: '2026-05-03T10:00:00Z' }),
      createCaseDocument({ id: 22, documentKey: 'unrelated', originalFileName: 'Другой.pdf', documentType: 'Иск', status: 'Подписан' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByRole('table', { name: 'Документы дела' });
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    fireEvent.change(screen.getByLabelText('Поиск документов дела'), { target: { value: '  АВТОР ДЛЯ  ' } });
    fireEvent.change(screen.getByLabelText('Фильтр документов по типу'), { target: { value: 'Иск' } });
    fireEvent.change(screen.getByLabelText('Фильтр документов по статусу'), { target: { value: 'Подписан' } });
    fireEvent.click(screen.getByLabelText('Только актуальные версии'));
    expect(screen.getAllByRole('row', { name: /^Документ /u })).toHaveLength(1);
    expect(screen.getByText('Показано 1 из 3')).toBeTruthy();
    const details = await openDocumentDetails('История v3.pdf');
    expect(within(details).getByText('История версий: v1, v3 · последняя v3')).toBeTruthy();
    expect(within(details).getByText('Шаблон: Полный шаблон')).toBeTruthy();
    expect(within(details).getByText('ГАС «Правосудие»')).toBeTruthy();
    expect(within(details).getByRole('link', { name: 'Открыть источник' }).getAttribute('href')).toBe('https://example.sudrf.ru/document/13');
    fireEvent.click(screen.getByRole('button', { name: 'Сортировать документы по дате' }));
    expect(within(details).getByText('История версий: v1, v3 · последняя v3')).toBeTruthy();
    expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads);
    expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();
  });

  it('PAY-23 table: preserves metadata drafts across collapse and sorting and restores keyboard focus on Escape', async () => {
    const user = setupDocumentUser();
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const toggle = await screen.findByRole('button', { name: 'Сведения и действия документа Заявление.docx' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('region', { name: 'Сведения и действия документа Заявление.docx' })).toBeNull();
    toggle.focus();
    await user.keyboard('{Enter}');
    const details = screen.getByRole('region', { name: 'Сведения и действия документа Заявление.docx' });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(document.getElementById(toggle.getAttribute('aria-controls')!)).toBe(details);
    await user.keyboard('{Escape}');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(toggle);
    const closedEscape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    expect(toggle.dispatchEvent(closedEscape)).toBe(true);
    expect(closedEscape.defaultPrevented).toBe(false);
    await user.keyboard('{Enter}');
    const type = within(details).getByLabelText('Тип документа Заявление.docx');
    fireEvent.change(type, { target: { value: 'Несохранённый тип' } });
    fireEvent.change(within(details).getByLabelText('Статус документа Заявление.docx'), { target: { value: 'Подписан' } });
    type.focus();
    await user.keyboard('{Escape}');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(toggle);
    expect(screen.queryByRole('region', { name: 'Сведения и действия документа Заявление.docx' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Сортировать документы по дате' }));
    toggle.focus();
    await user.keyboard(' ');
    const reopened = screen.getByRole('region', { name: 'Сведения и действия документа Заявление.docx' });
    expect((within(reopened).getByLabelText('Тип документа Заявление.docx') as HTMLInputElement).value).toBe('Несохранённый тип');
    expect((within(reopened).getByLabelText('Статус документа Заявление.docx') as HTMLSelectElement).value).toBe('Подписан');
    fireEvent.click(within(reopened).getByRole('button', { name: 'Сохранить документ Заявление.docx' }));
    await waitFor(() => expect(apiService.updateCaseDocument).toHaveBeenCalledExactlyOnceWith(15, 7, { documentType: 'Несохранённый тип', status: 'Подписан' }));
  });

  it.each([
    { clientId: 12, canSendMessenger: false, visible: false },
    { clientId: null, canSendMessenger: true, visible: false },
    { clientId: 12, canSendMessenger: true, visible: true },
  ])('PAY-23 table: preserves messenger access for client=$clientId permission=$canSendMessenger', async ({ clientId, canSendMessenger, visible }) => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ clientId }));
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({ enabled: false, linkLifetimeHours: 24, providers: [] });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canSendMessenger={canSendMessenger} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const details = await openDocumentDetails();
    const action = within(details).queryByRole('button', { name: 'Отправить документ Заявление.docx в мессенджер' });
    expect(Boolean(action)).toBe(visible);
    if (action) {
      fireEvent.click(action);
      const modal = await screen.findByRole('region', { name: 'Отправка документа в мессенджер' });
      expect(within(modal).getByText('Заявление.docx')).toBeTruthy();
      expect(within(modal).getByText(/Заявление · версия 1/u)).toBeTruthy();
      expect(await within(modal).findByText(/доставка документов выключена/i)).toBeTruthy();
      fireEvent.click(within(modal).getByRole('button', { name: 'Закрыть мастер отправки' }));
    }
  });

  it('PAY-23 table: routes same-name document actions to the selected ID and version', async () => {
    const documents = [
      createCaseDocument({ id: 71, documentKey: 'first', originalFileName: 'Одинаковое.pdf', version: 1, uploadedByName: 'Первый автор' }),
      createCaseDocument({ id: 72, documentKey: 'second', originalFileName: 'Одинаковое.pdf', version: 4, uploadedByName: 'Второй автор' }),
    ];
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue(documents);
    const createUrl = vi.fn(() => 'blob:selected-document');
    const revokeUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeUrl });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    try {
      render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
      const rows = await screen.findAllByRole('row', { name: 'Документ Одинаковое.pdf' });
      const selected = rows.find(row => within(row).queryByText('Второй автор'))!;
      fireEvent.click(within(selected).getByRole('button', { name: 'Сведения и действия документа Одинаковое.pdf' }));
      const details = screen.getByRole('region', { name: 'Сведения и действия документа Одинаковое.pdf' });
      expect(within(details).getByText('Версия 4')).toBeTruthy();
      fireEvent.click(within(details).getByRole('button', { name: 'Скачать документ Одинаковое.pdf' }));
      await waitFor(() => expect(apiService.downloadCaseDocument).toHaveBeenCalledExactlyOnceWith(15, 72, { signal: expect.any(AbortSignal) }));
      await waitFor(() => expect(revokeUrl).toHaveBeenCalledWith('blob:selected-document'), { timeout: 2000 });
      const file = new File(['synthetic replacement'], 'replacement.pdf', { type: 'application/pdf' });
      fireEvent.change(within(details).getByLabelText('Загрузить новую версию документа Одинаковое.pdf'), { target: { files: [file] } });
      await waitFor(() => expect(apiService.uploadCaseDocumentVersion).toHaveBeenCalledExactlyOnceWith(72, file, {
        clientCaseId: 15, documentKey: 'second', documentType: 'Заявление', status: 'Готово',
      }));
    } finally {
      clickSpy.mockRestore();
    }
  });

  it('PAY-23 table: blocks only the deleting document actions and recovers the same draft after failure', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      createCaseDocument(), createCaseDocument({ id: 8, documentKey: 'other', originalFileName: 'Другой.pdf' }),
    ]);
    let rejectDelete!: (reason: Error) => void;
    vi.mocked(apiService.deleteCaseDocument).mockReturnValueOnce(new Promise((_resolve, reject) => { rejectDelete = reject; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canSendMessenger canSendToYandexDisk />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const details = await openDocumentDetails();
    fireEvent.change(within(details).getByLabelText('Тип документа Заявление.docx'), { target: { value: 'Черновик до удаления' } });
    fireEvent.click(within(details).getByRole('button', { name: 'Удалить документ Заявление.docx' }));
    await waitFor(() => expect(apiService.deleteCaseDocument).toHaveBeenCalledExactlyOnceWith(15, 7));
    for (const action of ['Предпросмотр документа Заявление.docx', 'Скачать документ Заявление.docx',
      'Отправить документ Заявление.docx на Яндекс.Диск', 'Отправить документ Заявление.docx в мессенджер',
      'Сохранить документ Заявление.docx', 'Удалить документ Заявление.docx']) {
      const button = within(details).getByRole('button', { name: action }) as HTMLButtonElement;
      expect(button.disabled).toBe(true);
      fireEvent.click(button);
    }
    expect((within(details).getByLabelText('Загрузить новую версию документа Заявление.docx') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Открыть предпросмотр документа Заявление.docx' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Открыть предпросмотр документа Другой.pdf' }) as HTMLButtonElement).disabled).toBe(false);
    expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();
    expect(apiService.updateCaseDocument).not.toHaveBeenCalled();
    expect(apiService.uploadCaseDocumentToYandexDisk).not.toHaveBeenCalled();
    expect(apiService.deleteCaseDocument).toHaveBeenCalledTimes(1);
    await act(async () => { rejectDelete(new Error('Синтетический отказ удаления')); });
    expect(await screen.findByText('Синтетический отказ удаления')).toBeTruthy();
    expect((within(details).getByRole('button', { name: 'Удалить документ Заявление.docx' }) as HTMLButtonElement).disabled).toBe(false);
    expect((within(details).getByLabelText('Тип документа Заявление.docx') as HTMLInputElement).value).toBe('Черновик до удаления');
  });

  it('PAY-23 table: permits selecting the same version file again after an upload failure', async () => {
    const user = setupDocumentUser();
    vi.mocked(apiService.uploadCaseDocumentVersion).mockRejectedValueOnce(new Error('Синтетический отказ загрузки версии'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const details = await openDocumentDetails();
    const input = within(details).getByLabelText('Загрузить новую версию документа Заявление.docx') as HTMLInputElement;
    const file = new File(['synthetic retry content'], 'same-version.pdf', { type: 'application/pdf' });
    await user.upload(input, file);
    expect(await screen.findByText('Синтетический отказ загрузки версии')).toBeTruthy();
    expect(input.value).toBe('');
    expect(input.disabled).toBe(false);
    await user.upload(input, file);
    await waitFor(() => expect(apiService.uploadCaseDocumentVersion).toHaveBeenCalledTimes(2));
    for (const call of vi.mocked(apiService.uploadCaseDocumentVersion).mock.calls) {
      expect(call).toEqual([7, file, { clientCaseId: 15, documentKey: 'claim', documentType: 'Заявление', status: 'Готово' }]);
    }
    expect(await screen.findByText('Новая версия документа «Заявление.docx» загружена.')).toBeTruthy();
  });

  it('PAY-23 review: renders the upload panel and document list with distinct React keys', async () => {
    const consoleError = vi.spyOn(console, 'error');
    try {
      render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
      fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
      await screen.findByText('Заявление.docx');
      const duplicateKeys = consoleError.mock.calls.filter(([message]) => String(message).includes('same key'));
      expect(duplicateKeys).toHaveLength(0);
    } finally {
      consoleError.mockRestore();
    }
  });

  it('PAY-23 review: searches template metadata without losing document version history', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([1, 2].map((version) => ({
      id: 90 + version, clientCaseId: 15, documentKey: 'template-history', version,
      originalFileName: `Синтетический документ v${version}.pdf`, documentType: 'Иск', status: 'Подготовлен',
      uploadedByName: 'Синтетический автор', templateLabel: version === 2 ? 'Шаблон для поиска' : undefined,
      contentType: 'application/pdf', sizeBytes: 1, uploadedAt: '2026-10-11T12:00:00Z', isLatestVersion: version === 2,
    })));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByText('Синтетический документ v2.pdf');
    const calls = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    fireEvent.change(screen.getByRole('searchbox', { name: 'Поиск документов дела' }), { target: { value: 'ШАБЛОН ДЛЯ' } });
    expect(screen.queryByText('Синтетический документ v1.pdf')).toBeNull();
    expect(screen.getByText('Синтетический документ v2.pdf')).toBeTruthy();
    await openDocumentDetails('Синтетический документ v2.pdf');
    expect(screen.getByText('История версий: v1, v2 · последняя v2')).toBeTruthy();
    expect(screen.getByText('Показано 1 из 2')).toBeTruthy();
    expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(calls);
  });

  it('PAY-23 review: searches legacy documents with missing text fields without crashing', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([{
      id: 99, clientCaseId: 15, documentKey: 'legacy-doc', version: 1,
      originalFileName: 'legacy.pdf', documentType: null, status: 'Draft', uploadedByName: null,
      contentType: 'application/pdf', sizeBytes: 1, uploadedAt: '2026-10-11T12:00:00Z', isLatestVersion: true,
    } as unknown as ClientCaseDocument]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await screen.findByText('legacy.pdf');
    const search = screen.getByRole('searchbox', { name: 'Поиск документов дела' });
    fireEvent.change(search, { target: { value: 'отсутствует' } });
    expect(screen.getByText('По текущим фильтрам документов нет.')).toBeTruthy();
    fireEvent.change(search, { target: { value: 'LEGACY' } });
    expect(screen.getByText('legacy.pdf')).toBeTruthy();
  });

  it('PAY-23: searches the loaded case documents and shows an honest empty state', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect(await screen.findByText('Заявление.docx')).toBeTruthy();
    expect(screen.getByText(/Показано \d+ из \d+/u)).toBeTruthy();

    const search = screen.getByRole('searchbox', { name: 'Поиск документов дела' });
    fireEvent.change(search, { target: { value: 'ЗАЯВЛ' } });
    expect(screen.getByText('Заявление.docx')).toBeTruthy();
    expect(screen.getByText(/Показано \d+ из \d+/u)).toBeTruthy();

    fireEvent.change(search, { target: { value: 'несуществующий файл' } });
    expect(screen.queryByText('Заявление.docx')).toBeNull();
    expect(screen.getByText('По текущим фильтрам документов нет.')).toBeTruthy();

    fireEvent.change(search, { target: { value: '' } });
    expect(screen.getByText('Заявление.docx')).toBeTruthy();
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

  it('PAY-21: every available quick link selects the matching case section', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    const menu = screen.getByRole('navigation', { name: 'Быстрый доступ к разделам дела' });
    const sections = ['Участники', 'Задачи', 'Суд', 'События', 'Финансы', 'Заказ-наряд', 'Документы', 'Переписка', 'История'];
    for (const section of sections) {
      const link = within(menu).getByRole('button', { name: `Быстрый доступ: ${section}` });
      fireEvent.click(link);
      expect(link.getAttribute('aria-current')).toBe('page');
      expect(screen.getAllByRole('button', { name: section }).some(button => button.className.includes('bg-blue-600'))).toBe(true);
      expect(within(menu).getAllByRole('button', { current: 'page' })).toHaveLength(1);
    }
  });

  it('does not show documents from the previous case while the new case documents load', async () => {
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court', courtOverview: true }));
    window.sessionStorage.setItem(detailViewStateKey(16), JSON.stringify({ activeTab: 'court', courtOverview: true }));
    let finishNew!: (documents: ClientCaseDocument[]) => void;
    const pending = new Promise<ClientCaseDocument[]>(resolve => { finishNew = resolve; });
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    vi.mocked(apiService.getCaseDocuments).mockImplementation(id => id === 15
      ? Promise.resolve([createCaseDocument({ id: 501, clientCaseId: 15, originalFileName: 'Документ первого дела.pdf' })])
      : pending);
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Дело 15');
    await screen.findByText('Документ первого дела.pdf');

    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} />);
    await screen.findByText('Дело 16');
    expect(screen.queryByText('Документ первого дела.pdf')).toBeNull();
    await act(async () => finishNew([createCaseDocument({ id: 502, clientCaseId: 16, originalFileName: 'Документ второго дела.pdf' })]));
    expect(await screen.findByText('Документ второго дела.pdf')).toBeTruthy();
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
    await openDocumentDetails('Архивное определение.pdf');
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

  it('does not expose task counts in the court overview without task access', async () => {
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court', courtOverview: true }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewTasks={false} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(screen.queryByText('Активные задачи')).toBeNull();
    expect(screen.queryByText('Просрочено')).toBeNull();
  });

  it('does not say there are no overdue tasks when task visibility is partial', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ taskSummaryVisibility: 'partial', overdueTaskCount: 0 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    expect(await screen.findByLabelText('Контроль сроков: Не все задачи доступны')).toBeTruthy();
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
        }), expect.objectContaining({ id: 15 })
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
        }), expect.objectContaining({ id: 15 })
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
        }), expect.objectContaining({ id: 15 })
      );
    });
  });

  it.each(['bank', 'fssp'] as const)('case draft boundary: full editor preserves an untouched historical writ destination without a date (%s)', async destination => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Другое название' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u }).at(-1) as HTMLElement);
    await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalled());
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[0][1]).toMatchObject({ title: 'Другое название', executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination, clearExecutiveWritSubmission: false });
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
    })).mockResolvedValueOnce(createCase({ stage: 'Апелляция', cassationDeadlineAt: null }));
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ stage: 'Кассация', cassationDeadlineAt: null }));

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

  it('does not describe restricted control information as paused control', async () => {
    const reason = 'Сведения автоконтроля недоступны из-за ограничений доступа';
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ inactivityControlExclusionReason: reason }));
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: null, lastViewedByUserId: null, lastViewedByUserName: null,
      activityConfirmedAt: null, activityConfirmedByUserId: null, activityConfirmedByUserName: null,
      inactivityRemindersEnabled: true, lastMeaningfulActivityAt: null, nextInactivityControlAt: null,
      inactivityControlExclusionReason: reason, eventHistory: '',
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    expect(await screen.findByLabelText(/Автоконтроль: Сведения недоступны/u)).toBeTruthy();
    expect(screen.queryByText('Контроль приостановлен')).toBeNull();
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
      expect(apiService.getCaseFinance).toHaveBeenCalledWith(15, expect.objectContaining({ signal: expect.any(AbortSignal) }));
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
      detailViewStateKey(15),
      JSON.stringify({ activeTab: 'finance' }),
    );

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewFinance={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    expect(screen.queryByRole('button', { name: 'Финансы' })).toBeNull();
    expect(apiService.getCaseFinance).not.toHaveBeenCalled();
  });

  it('PAY-21: hides task and finance summaries and shortcuts after access is revoked', async () => {
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(screen.getByText('Оплачено / долг')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Быстрый доступ: Задачи' }));
    expect(await screen.findByText(/Задачи по делу \(/u)).toBeTruthy();

    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} canViewTasks={false} canViewFinance={false} />);
    await waitFor(() => expect(screen.queryByText(/Задачи по делу \(/u)).toBeNull());
    expect(screen.queryByText('Оплачено / долг')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Быстрый доступ: Задачи' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Быстрый доступ: Финансы' })).toBeNull();
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
    await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledWith(15, expect.any(AbortSignal)));

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

    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.getCaseWorkOrder).toHaveBeenCalledWith(15, expect.any(AbortSignal)));
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

    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
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

    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
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

  it.each([
    ['task', 'Задача'], ['budget', 'Бюджет'], ['contact', 'Связаться'], ['document', 'Документ'],
    ['reminder', 'Не забыть'], ['control', 'Контроль'], ['courtHearing', 'Суд. заседание'], ['meeting', 'Встреча'],
    ['legacyUnknown', 'Задача'],
  ])('PAY-24 rows: presents type %s with the journal label %s', async (type, label) => {
    mockCaseTaskServer([createTask({ type: type as TaskJournalItem['type'] })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByRole('img', { name: `Тип задачи: ${label}` })).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1);
    expect(apiService.getActResponsibles).toHaveBeenCalledTimes(1);
  });

  it('PAY-24 rows: shows full long names with initials and a safe empty assignee', async () => {
    const name = 'Синтетический ОченьДлинныйИсполнитель'.repeat(3);
    mockCaseTaskServer([createTask({ id: 11, title: 'С именем', assigneeName: name }), createTask({ id: 12, title: 'Без имени', assigneeName: '  ' })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText(name)).toBeTruthy();
    const table = screen.getByRole('table', { name: 'Задачи дела' });
    expect(within(table).getByText('СО').getAttribute('aria-hidden')).toBe('true');
    const emptyRow = within(table).getByText('Без имени').closest('tr')!;
    expect(emptyRow.querySelector('td:nth-child(2)')?.textContent).toBe('—');
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1);
  });

  it('PAY-24 filters: collapses the controls without changing the active filter or fetching again', async () => {
    mockCaseTaskServer([createTask({ id: 51, title: 'Задача фильтров', dueAt: '2100-01-01T10:00:00Z' })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByRole('button', { name: 'Активные · 1' });
    const requests = vi.mocked(apiService.getTaskJournalItems).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Фильтры задач' }));
    expect(screen.queryByRole('button', { name: 'Активные · 1' })).toBeNull();
    expect(screen.getByRole('status', { name: 'Текущий фильтр задач: Активные' })).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(requests);
    fireEvent.click(screen.getByRole('button', { name: 'Фильтры задач' }));
    expect(screen.getByRole('button', { name: 'Активные · 1' }).getAttribute('aria-pressed')).toBe('true');
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(requests);
  });
  it('PAY-24 filters: restores collapsed controls on return to the same case', async () => {
    mockCaseTaskServer([createTask({ id: 51, title: 'Задача сохранённых фильтров' })]);
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByRole('button', { name: 'Активные · 1' });
    fireEvent.click(screen.getByRole('button', { name: 'Фильтры задач' }));
    await waitFor(() => expect(JSON.parse(sessionStorage.getItem(detailViewStateKey(15))!).taskFiltersExpanded).toBe(false));
    view.unmount(); render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Задача сохранённых фильтров');
    expect(screen.getByRole('button', { name: 'Фильтры задач' }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('status', { name: 'Текущий фильтр задач: Активные' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Активные · 1' })).toBeNull();
  });

  it('PAY-24 filters: ignores a string masquerading as a saved boolean', async () => {
    sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'tasks', taskFiltersExpanded: 'false' }));
    mockCaseTaskServer([createTask({ id: 51, title: 'Задача неправильного состояния' })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByRole('button', { name: 'Активные · 1' });
    expect(screen.getByRole('button', { name: 'Фильтры задач' }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('group', { name: 'Фильтры списка задач' })).toBeTruthy();
  });

  it('PAY-24 menu: opens the same common card without exposing writes to a read-only role', async () => {
    const onOpenTask = vi.fn(); mockCaseTaskServer([createTask({ id: 51, title: 'Задача меню чтения' })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} onOpenTask={onOpenTask} />);
    await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    const trigger = await screen.findByRole('button', { name: 'Действия задачи: Задача меню чтения' });
    fireEvent.click(trigger); expect(screen.getAllByRole('menuitem')).toHaveLength(1);
    expect(screen.queryByRole('checkbox', { name: 'Выбрать задачу: Задача меню чтения' })).toBeNull();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Открыть карточку' }));
    expect(onOpenTask).toHaveBeenCalledExactlyOnceWith(51);
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1);
    expect(apiService.bulkUpdateTaskJournalItems).not.toHaveBeenCalled();
  });

  it('PAY-24 menu: selects the existing row and closes when the filter changes', async () => {
    mockCaseTaskServer([createTask({ id: 51, title: 'Задача меню выбора' })]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEditTasks onOpenTask={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    const trigger = await screen.findByRole('button', { name: 'Действия задачи: Задача меню выбора' });
    fireEvent.click(trigger); fireEvent.click(screen.getByRole('menuitem', { name: 'Выбрать задачу' }));
    expect((screen.getByRole('checkbox', { name: 'Выбрать задачу: Задача меню выбора' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('Выбрано на этой странице: 1')).toBeTruthy();
    fireEvent.click(trigger); expect(screen.getByRole('menuitem', { name: 'Снять выбор задачи' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Все · 1' }));
    await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
    await screen.findByText('Задача меню выбора');
    expect(screen.queryByText('Выбрано на этой странице: 1')).toBeNull();
    expect(apiService.bulkUpdateTaskJournalItems).not.toHaveBeenCalled();
  });

  it('PAY-24 bulk: reports partial changes after reloading the case and preserves its filter', async () => {
    mockCaseTaskServer([createTask({ id: 11, title: 'Массовая первая' }), createTask({ id: 12, title: 'Массовая вторая' })]);
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockResolvedValue({ requested: 2, updated: 1, skipped: 1 });
    dialogMock.confirm.mockResolvedValue(true);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEditTasks canExportTasks />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Массовая первая');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать все задачи на этой странице' }));
    fireEvent.click(screen.getByRole('button', { name: 'Применить к выбранным' }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    await screen.findByText('Массовая первая');
    expect(screen.getByText(/Изменено: 1. Пропущено: 1/)).toBeTruthy();
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledWith({ ids: [11, 12], status: 'inProgress' });
    expect(screen.getByRole('button', { name: 'Активные · 2' }).getAttribute('aria-pressed')).toBe('true');
    expect((screen.getByRole('checkbox', { name: 'Выбрать все задачи на этой странице' }) as HTMLInputElement).checked).toBe(false);
  });

  it('PAY-24 billing: keeps its page mounted and blocks tabs/back/edit while the bulk response is pending', async () => {
    vi.mocked(apiService.getTaskJournalSettings).mockResolvedValue({ contractTaskBillingMode: 'retainContractTasks' } as Awaited<ReturnType<typeof apiService.getTaskJournalSettings>>);
    const back = vi.fn();
    mockCaseTaskServer([createTask({ id: 11, title: 'Ожидающая смена учёта' })]);
    let resolve!: (value: Awaited<ReturnType<typeof apiService.bulkUpdateTaskJournalItems>>) => void;
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
    dialogMock.confirm.mockResolvedValue(true);
    render(<CaseDetailPage caseId={15} onBack={back} canEditTasks canExportTasks />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' })); await screen.findByText('Ожидающая смена учёта');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать все задачи на этой странице' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    await waitFor(() => expect(screen.queryByText('Проверяем настройку учёта договорных задач…')).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await waitFor(() => expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledOnce());
    for (const name of ['Реестр дел', 'Редактировать дело', 'Документы', 'Быстрый доступ: Документы']) {
      expect(screen.getByRole('button', { name }).matches(':disabled')).toBe(true);
    }
    fireEvent.keyDown(screen.getByRole('dialog', { name: 'Массовая смена учёта задач' }), { key: 'Escape' });
    expect(screen.getByRole('dialog', { name: 'Массовая смена учёта задач' })).toBeTruthy(); expect(back).not.toHaveBeenCalled();
    await act(async () => resolve({ requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0,
      items: [{ id: 11, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' }] }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Реестр дел' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByRole('dialog', { name: 'Массовая смена учёта задач' })).toBeTruthy();
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('Учёт изменён.');
    expect(apiService.getCaseV2).toHaveBeenCalledOnce();
  });

  it('PAY-24 pages: reaches task 61 with whole-case counters and the same open callback', async () => {
    const onOpenTask = vi.fn();
    mockCaseTaskServer(Array.from({ length: 61 }, (_, index) => createTask({
      id: index + 1, title: `Строка задачи ${index + 1}`, status: 'new', dueAt: '2100-01-01T10:00:00Z',
    })));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} onOpenTask={onOpenTask} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Задачи по делу (61)')).toBeTruthy();
    await screen.findByText('Строка задачи 50');
    expect(screen.getByRole('table', { name: 'Задачи дела' }).querySelectorAll('tbody tr')).toHaveLength(50);
    expect(screen.queryByText('Строка задачи 61')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница задач' }));
    const open = await screen.findByRole('button', { name: 'Открыть задачу: Строка задачи 61' });
    expect(screen.getByText('Показано 11 из 61')).toBeTruthy();
    expect(screen.getByRole('table', { name: 'Задачи дела' }).querySelectorAll('tbody tr')).toHaveLength(11);
    fireEvent.click(open);
    expect(onOpenTask).toHaveBeenCalledExactlyOnceWith(61);
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(2);
    expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ clientCaseId: 15, status: 'active', page: 2, pageSize: 50 }), { signal: expect.any(AbortSignal) });
    expect(apiService.getTaskJournalSummary).toHaveBeenCalledTimes(1);
  });

  it('PAY-24 pages: finds overdue and cancelled tasks beyond the first unfiltered page', async () => {
    mockCaseTaskServer([
      ...Array.from({ length: 50 }, (_, index) => createTask({ id: index + 1, title: `Будущая ${index + 1}`, status: 'new', dueAt: '2100-01-01T10:00:00Z' })),
      createTask({ id: 51, title: 'Просроченная за пределом страницы', status: 'inProgress', dueAt: '2020-01-01T10:00:00Z' }),
      createTask({ id: 52, title: 'Выполненная за пределом страницы', status: 'done' }),
      createTask({ id: 53, title: 'Отменённая за пределом страницы', status: 'cancelled' }),
    ]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Будущая 50');
    expect(screen.queryByText('Просроченная за пределом страницы')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Просроченные · 1' }));
    await screen.findByText('Просроченная за пределом страницы');
    expect(screen.getByText('Показано 1 из 1')).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'overdue', page: 1 }), expect.anything());
    fireEvent.click(screen.getByRole('button', { name: 'Закрытые · 2' }));
    await screen.findByText('Отменённая за пределом страницы');
    expect(screen.getByText('Выполненная за пределом страницы')).toBeTruthy();
    expect(screen.getByText('Показано 2 из 2')).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'completed', page: 1 }), expect.anything());
    expect(screen.getByRole('button', { name: 'Все · 53' })).toBeTruthy();
  });

  it('PAY-24 pages: reports a failed page without a false empty result and retries', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockRejectedValueOnce(new Error('Unavailable'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Не удалось загрузить задачи дела.');
    expect(screen.queryByText('По выбранному фильтру задач нет.')).toBeNull();
    expect(screen.queryByText(/Показано 0 из/u)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку задач' }));
    await screen.findByText('Проверить движение по делу');
    expect(screen.queryByText('Не удалось загрузить задачи дела.')).toBeNull();
  });

  it('PAY-24 pages: keeps loaded rows when counters fail and does not invent zero counters', async () => {
    vi.mocked(apiService.getTaskJournalSummary).mockRejectedValueOnce(new Error('Summary unavailable'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Проверить движение по делу');
    await screen.findByText('Не удалось получить счётчики задач.');
    expect(screen.getByRole('button', { name: 'Все · —' })).toBeTruthy();
    expect(screen.queryByText('Задачи по делу (0)')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку счётчиков' }));
    await screen.findByText('Задачи по делу (1)');
  });

  it('PAY-24 pages: retains filters during loading and discards a late response after changing filter', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getTaskJournalItems>>) => void;
    const old = new Promise<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>((resolve) => { finish = resolve; });
    vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => query?.status === 'active'
      ? old : { items: [createTask({ id: 302, title: 'Новый результат фильтра', dueAt: '2020-01-01T10:00:00Z' })], total: 1, page: 1, pageSize: 50 });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await waitFor(() => expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.getTaskJournalItems).mock.calls[0][1]!.signal!;
    expect(screen.getByRole('button', { name: /Просроченные ·/u })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Просроченные ·/u }));
    await screen.findByText('Новый результат фильтра');
    expect(signal.aborted).toBe(true);
    await act(async () => finish({ items: [createTask({ id: 301, title: 'Устаревшая строка' })], total: 1, page: 1, pageSize: 50 }));
    expect(screen.queryByText('Устаревшая строка')).toBeNull();
    expect(screen.getByRole('button', { name: /Просроченные ·/u }).getAttribute('aria-pressed')).toBe('true');
  });

  it('PAY-24 pages: aborts the old case read before exposing rows from the new case', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getTaskJournalItems>>) => void;
    const old = new Promise<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>((resolve) => { finish = resolve; });
    vi.mocked(apiService.getCaseV2).mockImplementation(async (id) => createCase({ id, title: id === 15 ? 'Первое дело' : 'Второе дело' }));
    vi.mocked(apiService.getTaskJournalItems).mockImplementation(async (query) => query?.clientCaseId === 15
      ? old : { items: [createTask({ id: 602, title: 'Строка второго дела', clientCaseId: 16 })], total: 1, page: 1, pageSize: 50 });
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Первое дело');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await waitFor(() => expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.getTaskJournalItems).mock.calls[0][1]!.signal!;
    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} />);
    await screen.findByText('Второе дело');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Строка второго дела');
    expect(signal.aborted).toBe(true);
    await act(async () => finish({ items: [createTask({ id: 601, title: 'Строка прежнего дела' })], total: 1, page: 1, pageSize: 50 }));
    expect(screen.queryByText('Строка прежнего дела')).toBeNull();
    expect(apiService.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ clientCaseId: 16 }), expect.anything());
  });

  it('PAY-24 pages: hides the previous task scope immediately when access narrows', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getTaskJournalItems>>) => void;
    const pending = new Promise<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>(resolve => { finish = resolve; });
    vi.mocked(apiService.getTaskJournalItems)
      .mockResolvedValueOnce({ items: [createTask({ id: 701, title: 'Задача прежней области' })], total: 1, page: 1, pageSize: 50 })
      .mockReturnValueOnce(pending);
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} taskAccessKey="all" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Задача прежней области');
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} taskAccessKey="own" />);
    expect(screen.queryByText('Задача прежней области')).toBeNull();
    await act(async () => finish({ items: [createTask({ id: 702, title: 'Разрешённая задача' })], total: 1, page: 1, pageSize: 50 }));
    expect(await screen.findByText('Разрешённая задача')).toBeTruthy();
  });

  it('hides task sources from the old access scope in document uploads', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getTaskJournalItems>>) => void;
    const pending = new Promise<Awaited<ReturnType<typeof apiService.getTaskJournalItems>>>(resolve => { finish = resolve; });
    vi.mocked(apiService.getTaskJournalItems)
      .mockResolvedValueOnce({ items: [createTask({ id: 711, title: 'Старый источник' })], total: 1, page: 1, pageSize: 50 })
      .mockReturnValueOnce(pending);
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit taskAccessKey="all" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    fireEvent.change(await screen.findByLabelText('Источник документа'), { target: { value: 'task' } });
    await screen.findByRole('option', { name: 'Старый источник', hidden: true });
    fireEvent.change(screen.getByLabelText('Объект документа'), { target: { value: '711' } });
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit taskAccessKey="own" />);
    expect(screen.queryByRole('option', { name: 'Старый источник', hidden: true })).toBeNull();
    await act(async () => finish({ items: [createTask({ id: 712, title: 'Доступный источник' })], total: 1, page: 1, pageSize: 50 }));
    fireEvent.change(await screen.findByLabelText('Источник документа'), { target: { value: 'task' } });
    expect(await screen.findByRole('option', { name: 'Доступный источник', hidden: true })).toBeTruthy();
    expect((screen.getByLabelText('Объект документа') as HTMLSelectElement).value).not.toBe('711');
  });

  it('PAY-24 pages: preserves a selected document source and upload draft across task pages', async () => {
    mockCaseTaskServer(Array.from({ length: 61 }, (_, index) => createTask({ id: index + 1, title: `Источник ${index + 1}`, status: 'new', dueAt: '2100-01-01T10:00:00Z' })));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    const file = new File(['synthetic'], 'source.pdf', { type: 'application/pdf' });
    fireEvent.change(await screen.findByLabelText('Файл документа'), { target: { files: [file] } });
    fireEvent.change(screen.getByLabelText('Тип документа'), { target: { value: 'Несохранённый тип' } });
    fireEvent.change(screen.getByLabelText('Источник документа'), { target: { value: 'task' } });
    await screen.findByRole('option', { name: 'Источник 11', hidden: true });
    fireEvent.change(screen.getByLabelText('Объект документа'), { target: { value: '11' } });
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    await screen.findByText('Источник 50');
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница задач' }));
    await screen.findByText('Источник 61');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    expect((await screen.findByLabelText('Объект документа') as HTMLSelectElement).value).toBe('11');
    expect((screen.getByLabelText('Тип документа') as HTMLInputElement).value).toBe('Несохранённый тип');
    fireEvent.click(screen.getByRole('button', { name: /^Загрузить$/u }));
    await waitFor(() => expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(15, file, expect.objectContaining({ documentType: 'Несохранённый тип', documentKey: expect.stringContaining('task-11-') })));
  });

  it('filters case tasks and creates a task from the tasks tab', async () => {
    mockCaseTaskServer([
        createTask({ id: 101, title: 'Будущая задача', status: 'inProgress', dueAt: '2100-01-01T10:00:00Z' }),
        createTask({ id: 102, title: 'Просроченная задача', status: 'inProgress', dueAt: '2020-01-01T10:00:00Z' }),
        createTask({ id: 103, title: 'Закрытая задача', status: 'done', dueAt: '2026-05-01T10:00:00Z' }),
    ]);

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Будущая задача')).toBeTruthy();
    expect(await screen.findByText('Просроченная задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Просроченные/u }));
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(await screen.findByText('Просроченная задача')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Закрытые/u }));
    expect(await screen.findByText('Закрытая задача')).toBeTruthy();

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
    mockCaseTaskServer([
        createTask({ id: 101, title: 'Будущая задача', status: 'inProgress', dueAt: '2100-01-01T10:00:00Z' }),
        createTask({ id: 102, title: 'Просроченная задача', status: 'inProgress', dueAt: '2020-01-01T10:00:00Z' }),
    ]);

    const firstRender = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Задачи' }));
    expect(await screen.findByText('Будущая задача')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Просроченные/u }));
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(await screen.findByText('Просроченная задача')).toBeTruthy();
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
    expect(screen.getByRole('row', { name: 'Документ Иск v2.pdf' })).toBeTruthy();
    expect(screen.getByText('Фото.jpg')).toBeTruthy();
    const oldDetails = await openDocumentDetails('Иск v1.docx');
    await openDocumentDetails('Иск v2.pdf');
    expect(screen.getAllByText('История версий: v1, v2 · последняя v2')).toHaveLength(2);
    expect(within(oldDetails).getByText('Не актуальная версия')).toBeTruthy();

    fireEvent.click(screen.getByLabelText('Только актуальные версии'));
    expect(screen.queryByText('Иск v1.docx')).toBeNull();
    expect(screen.getByRole('row', { name: 'Документ Иск v2.pdf' })).toBeTruthy();
    expect(screen.getByText('Фото.jpg')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Фильтр документов по типу'), { target: { value: 'Заявление' } });
    expect(screen.getByRole('row', { name: 'Документ Иск v2.pdf' })).toBeTruthy();
    expect(screen.queryByText('Фото.jpg')).toBeNull();

    fireEvent.change(screen.getByLabelText('Фильтр документов по статусу'), { target: { value: 'Подписан' } });
    expect(screen.getByRole('row', { name: 'Документ Иск v2.pdf' })).toBeTruthy();

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

  it.each(['document', 'version'] as const)('refreshes only documents without restarting pending reads after %s upload', async kind => {
    const signals: AbortSignal[] = [];
    let finishRead!: (value: Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>) => void;
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockImplementation((_id, _after, signal) => {
      signals.push(signal!);
      return signals.length === 1 ? Promise.resolve({ items: [], nextCursor: null })
        : new Promise(resolve => { finishRead = resolve; });
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await waitFor(() => expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1));
    const financeReads = vi.mocked(apiService.getCaseFinance).mock.calls.length;
    const courtReads = vi.mocked(apiService.getCaseCourtDirectory).mock.calls.length;
    const file = new File(['owned upload'], 'owned-upload.pdf', { type: 'application/pdf' });
    if (kind === 'version') {
      await openDocumentDetails();
      fireEvent.change(screen.getByLabelText('Загрузить новую версию документа Заявление.docx'), { target: { files: [file] } });
    } else {
      fireEvent.change(screen.getByLabelText('Файл документа'), { target: { files: [file] } });
      fireEvent.click(screen.getByRole('button', { name: /^Загрузить$/u }));
    }
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(2));
    await screen.findByText(kind === 'version' ? 'Новая версия документа «Заявление.docx» загружена.' : 'Документ «owned-upload.pdf» загружен.');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(1);
    expect(apiService.getCaseFinance).toHaveBeenCalledTimes(financeReads);
    expect(apiService.getCaseCourtDirectory).toHaveBeenCalledTimes(courtReads);
    expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(2);
    expect(signals[1].aborted).toBe(false);
    await act(async () => { finishRead({ items: [], nextCursor: null }); });
    expect(screen.queryByText('Загрузка карточки дела...')).toBeNull();
  });

  it('uploads a new case document version from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();

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

  it('keeps a case document downloadable until the browser consumes its URL', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    onTestFinished(() => clickSpy.mockRestore());

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(await screen.findByRole('button', { name: 'Скачать документ Заявление.docx' }));

    await waitFor(() => {
      expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7, { signal: expect.any(AbortSignal) });
      expect(createObjectUrl).toHaveBeenCalledTimes(1);
      expect(clickSpy).toHaveBeenCalledTimes(1);
      expect(screen.getByText('Документ «Заявление.docx» подготовлен к скачиванию.')).toBeTruthy();
    });
    expect(revokeObjectUrl).not.toHaveBeenCalled();
    expect(window.document.querySelector('a[download]')).toBeNull();
    await waitFor(() => expect(revokeObjectUrl).toHaveBeenCalledWith('blob:case-document'), { timeout: 2000 });
  });

  it('opens a case document preview from the documents tab', async () => {
    const createObjectUrl = vi.fn(() => 'blob:case-document-preview');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(await screen.findByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));

    expect(await screen.findByText('Предпросмотр документа')).toBeTruthy();
    expect(screen.getByTitle('Предпросмотр Исковое заявление.pdf')).toBeTruthy();
    expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7, { signal: expect.any(AbortSignal) });
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
    await openDocumentDetails();
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
    await openDocumentDetails();
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

    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canSendToYandexDisk />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(await screen.findByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' }));

    expect(await screen.findByText('Включите правила синхронизации CRM в разделе Я.Диск.')).toBeTruthy();
    expect(screen.queryByText(/HTTP 400 Bad Request/)).toBeNull();
  });

  it('shows case documents in read-only mode without mutation actions', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();

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
    await openDocumentDetails();

    expect(await screen.findByRole('button', { name: 'Сохранить документ Заявление.docx' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Удалить документ Заявление.docx' })).toBeNull();
  });

  it('updates and deletes a case document from the documents tab', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
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
      expect(apiService.getCaseRelatedParties).toHaveBeenCalledWith(15, true, expect.objectContaining({ signal: expect.any(AbortSignal) }));
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court', courtOverview: overview }));
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court', courtOverview: overview }));
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
    expect(apiService.getCaseTimeline).toHaveBeenCalledWith(15, expect.objectContaining({ signal: expect.any(AbortSignal) }));
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
    expect(apiService.getCaseCourtSyncLogs).toHaveBeenCalledWith(15, expect.objectContaining({ signal: expect.any(AbortSignal) }));

    fireEvent.click(screen.getByRole('button', { name: /Проверить источник/u }));
    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(15);
    });
  });

  it('opens the court card in a read-first overview from the court registry', async () => {
    window.sessionStorage.setItem(
      detailViewStateKey(15),
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
      detailViewStateKey(15),
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
      detailViewStateKey(15),
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
      detailViewStateKey(15),
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
      detailViewStateKey(15),
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court' }));
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court' }));
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
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'court' }));
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
    expect(JSON.parse(window.sessionStorage.getItem(detailViewStateKey(15)) || '{}').courtOverview).toBe(true);
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
  it('nested parent boundary: a pending writ upload blocks saving the full editor',async()=>{
    let finish!: (row:ClientCaseDocument)=>void;vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(new Promise(resolve=>{finish=resolve;}));render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit/>);await screen.findByText('ЗЕМЦОВ - банкротство');fireEvent.click(screen.getByRole('button',{name:'Редактировать дело'}));await screen.findByLabelText('Загрузить скан исполнительного листа');fireEvent.change(screen.getByLabelText('Стратегия и план действий'),{target:{value:'Ручная позиция во время скана'}});fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});await waitFor(()=>expect(apiService.uploadCaseDocument).toHaveBeenCalledOnce());for(const button of screen.getAllByRole('button',{name:/Сохранить дело/})){expect(button).toHaveProperty('disabled',true);fireEvent.click(button);}expect(apiService.updateCaseV2).not.toHaveBeenCalled();await act(async()=>finish(createCaseDocument({documentType:'Исполнительный лист',status:'Получен'})));await waitFor(()=>expect(screen.getAllByRole('button',{name:/Сохранить дело/}).every(button=>!(button as HTMLButtonElement).disabled)).toBe(true));expect(screen.getByLabelText('Стратегия и план действий')).toHaveProperty('value','Ручная позиция во время скана');
  });
  it('nested parent boundary: revoked edit access prevents old writ followup reads',async()=>{
    let finish!:(row:ClientCaseDocument)=>void;vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(new Promise(resolve=>{finish=resolve;}));const props={caseId:15,onBack:vi.fn(),canEdit:true};const view=render(<CaseDetailPage {...props}/>);await screen.findByText('ЗЕМЦОВ - банкротство');fireEvent.click(screen.getByRole('button',{name:'Редактировать дело'}));fireEvent.change(await screen.findByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});view.rerender(<CaseDetailPage {...props} canEdit={false}/>);await act(async()=>{});const reads=vi.mocked(apiService.getCaseDocuments).mock.calls.length;await act(async()=>finish(createCaseDocument({documentType:'Исполнительный лист',status:'Получен'})));expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads);expect(screen.queryByRole('heading',{name:'Редактирование дела'})).toBeNull();
  });
  it('nested parent boundary: an already started writ refresh cannot replace files after revocation',async()=>{
    let revoked=false;let finish!:(rows:ClientCaseDocument[])=>void;const refresh=new Promise<ClientCaseDocument[]>(resolve=>{finish=resolve;});vi.mocked(apiService.getCaseDocuments).mockImplementation(async()=>revoked?[]:vi.mocked(apiService.uploadCaseDocument).mock.calls.length?refresh:[]);const props={caseId:15,onBack:vi.fn(),canEdit:true};const view=render(<CaseDetailPage {...props}/>);await screen.findByText('ЗЕМЦОВ - банкротство');fireEvent.click(screen.getByRole('button',{name:'Документы'}));await act(async()=>{});fireEvent.click(screen.getByRole('button',{name:'Редактировать дело'}));fireEvent.change(await screen.findByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});await waitFor(()=>expect(vi.mocked(apiService.getCaseDocuments).mock.calls.length).toBeGreaterThanOrEqual(2));const signal=vi.mocked(apiService.getCaseDocuments).mock.calls.slice().reverse().find(([,signal])=>!!signal)?.[1];revoked=true;view.rerender(<CaseDetailPage {...props} canEdit={false}/>);expect(signal?.aborted).toBe(true);await act(async()=>finish([createCaseDocument({originalFileName:'ПРЕЖНИЙ СПИСОК.pdf'})]));expect(screen.getByRole('heading',{name:'Документы дела'})).toBeTruthy();expect(document.body.textContent).not.toContain('ПРЕЖНИЙ СПИСОК.pdf');
  });
  it('nested parent boundary: old full-editor save cannot replace another case',async()=>{
    let finish!:(value:ClientCase)=>void;vi.mocked(apiService.updateCaseV2).mockReturnValueOnce(new Promise(resolve=>{finish=resolve;}));vi.mocked(apiService.getCaseV2).mockImplementation(async id=>createCase({id,title:id===16?'Следующее дело':'ЗЕМЦОВ - банкротство'}));const props={caseId:15,onBack:vi.fn(),canEdit:true};const view=render(<CaseDetailPage {...props}/>);await screen.findByText('ЗЕМЦОВ - банкротство');fireEvent.click(screen.getByRole('button',{name:'Редактировать дело'}));fireEvent.click((await screen.findAllByRole('button',{name:/Сохранить дело/}))[0]);await waitFor(()=>expect(apiService.updateCaseV2).toHaveBeenCalledOnce());view.rerender(<CaseDetailPage {...props} caseId={16}/>);await act(async()=>{});await act(async()=>finish(createCase({title:'ПРЕЖНИЙ ОТВЕТ СОХРАНЕНИЯ'})));expect(document.body.textContent).not.toContain('ПРЕЖНИЙ ОТВЕТ СОХРАНЕНИЯ');expect(screen.queryByText('Карточка дела обновлена.')).toBeNull();
  });

  it.each(['hearing', 'appeal'] as const)('task child bridge: %s ignores completion after the parent access key changes', async kind => {
    let finish!: (value: never) => void; const pending = new Promise<never>(resolve => { finish = resolve; });
    if (kind === 'hearing') vi.mocked(apiService.createTaskJournalItem).mockReturnValueOnce(pending);
    else vi.mocked(apiService.createCaseAppealTask).mockReturnValueOnce(pending);
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt: '2099-10-20T15:00:00Z', courtDecisionAt: '2026-09-20', appealDeadlineAt: '2099-10-20' }));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true, canViewTasks: true, canCreateTasks: true, taskAccessKey: 'before', onOpenTask: vi.fn() };
    const view = render(<CaseDetailPage {...props} />); await screen.findByText('ЗЕМЦОВ - банкротство');
    if (kind === 'hearing') fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: kind === 'hearing' ? 'Запланировать командировку' : 'Обжаловать — создать задачу' }));
    view.rerender(<CaseDetailPage {...props} taskAccessKey="after" canViewTasks={false} canCreateTasks={false} canEdit={false} />); await act(async () => {});
    const reads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    await act(async () => finish((kind === 'hearing' ? { id: 91, clientCaseId: 15 } : { id: 8, caseId: 15, choice: 'appeal', taskId: 91, decidedAt: '2026-11-02T00:00:00Z' }) as never));
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(reads); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
  });
  it('task child bridge: appeal remains available with case edit but without generic task creation', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ courtDecisionAt: '2026-09-20', appealDeadlineAt: '2099-10-20' }));
    vi.mocked(apiService.createCaseAppealTask).mockResolvedValue({ id: 8, caseId: 15, choice: 'appeal', taskId: 91, decidedAt: '2026-11-02T00:00:00Z' });
    const open = vi.fn(); render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canViewTasks={false} canCreateTasks={false} onOpenTask={open} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Обжаловать — создать задачу' })); await screen.findByText(/Решено обжаловать/); expect(apiService.createCaseAppealTask).toHaveBeenCalledOnce(); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull(); expect(open).not.toHaveBeenCalled();
  });

  it('recovery parent bridge: allocation completion cannot refresh a new full-page case',async()=>{
    let finish!:()=>void;vi.mocked(apiService.setCourtRecoveryPayment).mockReset().mockReturnValueOnce(new Promise<void>(resolve=>{finish=resolve;}));vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({awardedAmount:100000,receivedAmount:0,remainingAmount:100000,linkedPayments:[],availablePayments:[{paymentId:7,date:'2026-09-24',description:'Поступление',paidAmount:25000,allocatedAmount:null,effectiveAmount:0,warning:null}],hasMoreAvailablePayments:false,hasCompletePaymentVisibility:true});vi.mocked(apiService.getCaseV2).mockImplementation(async id=>createCase({id,courtAwardedAmount:100000}));const props={caseId:15,onBack:vi.fn(),canViewFinance:true,canEdit:true};const view=render(<CaseDetailPage {...props}/>);fireEvent.change(await screen.findByRole('combobox',{name:'Платёж для взыскания'}),{target:{value:'7'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить связь'}));await waitFor(()=>expect(apiService.setCourtRecoveryPayment).toHaveBeenCalledExactlyOnceWith(15,7,25000));view.rerender(<CaseDetailPage {...props} caseId={16}/>);await act(async()=>{});const reads=vi.mocked(apiService.getCourtRecoveries).mock.calls.length;await act(async()=>finish());expect(apiService.getCourtRecoveries).toHaveBeenCalledTimes(reads);expect(screen.queryByText(/Связь с платежом сохранена/)).toBeNull();
  });

  it.each(['login', 'actor', 'logout'] as const)('page read owner baseline: ignores a late case after %s before React renders', async (change) => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let resolve!: (value: ClientCase) => void;
    vi.mocked(apiService.getCaseV2).mockReturnValue(new Promise<ClientCase>(done => { resolve = done; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    session.replace(change);
    await act(async () => { resolve(createCase({ title: 'Прежнее закрытое дело' })); });
    expect(screen.queryByText('Прежнее закрытое дело')).toBeNull();
    expect(apiService.markCaseActivity).not.toHaveBeenCalled();
  });

  it('page read owner baseline: discards the old page and reloads on a role change', async () => {
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    vi.mocked(apiService.getCaseV2).mockReturnValue(new Promise(() => {}));
    detailAuth.user = { id: 7, role: { id: 20 } };
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    expect(screen.queryByText('ЗЕМЦОВ - банкротство')).toBeNull();
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
  });

  it.each(['role', 'access', 'finance', 'edit', 'department'] as const)('page read owner: aborts previous reads and hides an old editor after %s changes', async change => {
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canViewFinance caseAccessKey="first" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Прежний секретный черновик' } });
    const signal = vi.mocked(apiService.getCaseV2).mock.calls[0][1]?.signal;
    expect(signal?.aborted).toBe(false);
    vi.mocked(apiService.getCaseV2).mockReturnValue(new Promise(() => {}));
    if (change === 'role') detailAuth.user = { id: 7, role: { id: 20 } };
    if (change === 'department') Object.assign(detailAuth.user, { departmentId: 20 });
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={change !== 'edit'} canViewFinance={change !== 'finance'} caseAccessKey={change === 'access' ? 'second' : 'first'} />);
    expect(screen.queryByDisplayValue('Прежний секретный черновик')).toBeNull();
    expect(signal?.aborted).toBe(true);
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    expect(apiService.updateCaseV2).not.toHaveBeenCalled();
  });

  it('page read owner: preserves the current editor and request counts through a token refresh and harmless parent render', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Текущий черновик' } });
    const counts = [vi.mocked(apiService.getCaseV2).mock.calls.length, vi.mocked(apiService.getActResponsibles).mock.calls.length, vi.mocked(apiService.getCaseDocumentTemplates).mock.calls.length];
    session.refresh();
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit backLabel="К списку" />);
    expect(screen.getByDisplayValue('Текущий черновик')).toBeTruthy();
    expect([vi.mocked(apiService.getCaseV2).mock.calls.length, vi.mocked(apiService.getActResponsibles).mock.calls.length, vi.mocked(apiService.getCaseDocumentTemplates).mock.calls.length]).toEqual(counts);
  });

  it('page read owner: recovers the current lifetime in StrictMode and records a loaded case only once', async () => {
    render(<StrictMode><CaseDetailPage caseId={15} onBack={vi.fn()} /></StrictMode>);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.markCaseActivity).toHaveBeenCalledTimes(1));
    const calls = vi.mocked(apiService.getCaseV2).mock.calls;
    expect(calls.at(-1)?.[1]?.signal?.aborted).toBe(false);
    expect(calls.slice(0, -1).every(call => call[1]?.signal?.aborted)).toBe(true);
  });

  it('page read owner: does not continue external-approval reads after a new login before React renders', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let resolve!: (value: Awaited<ReturnType<typeof apiService.getFeatureFlags>>) => void;
    vi.mocked(apiService.getFeatureFlags).mockReturnValue(new Promise(done => { resolve = done; }));
    window.sessionStorage.setItem(detailViewStateKey(15), JSON.stringify({ activeTab: 'workOrder' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.getFeatureFlags).toHaveBeenCalledTimes(2));
    session.replace();
    await act(async () => { resolve({ legal: { externalWorkOrderApproval: true } } as Awaited<ReturnType<typeof apiService.getFeatureFlags>>); });
    expect(apiService.getExternalWorkOrderApproval).not.toHaveBeenCalled();
  });

  it('page read owner: isolates saved tab preferences per actor and ignores unowned legacy preferences', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    window.sessionStorage.setItem('pp.case-detail.15.view.v1', JSON.stringify({ activeTab: 'conversation', participantRole: 'Прежняя роль' }));
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(apiService.getCaseConversation).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /^Переписка/ }));
    await waitFor(() => expect(apiService.getCaseConversation).toHaveBeenCalledTimes(1));
    const oldKey = detailViewStateKey(15);
    expect(JSON.parse(window.sessionStorage.getItem(oldKey)!).activeTab).toBe('conversation');
    session.replace('actor');
    detailAuth.user = { id: 8, role: { id: 10 } };
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(JSON.parse(window.sessionStorage.getItem(detailViewStateKey(15))!).activeTab).toBe('overview');
    expect(JSON.parse(window.sessionStorage.getItem(oldKey)!).activeTab).toBe('conversation');
    expect(apiService.getCaseConversation).toHaveBeenCalledTimes(1);
  });

  it('page read owner: does not repeat a view write on an access reload but records a new login', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} caseAccessKey="first" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.markCaseActivity).toHaveBeenCalledTimes(1));
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} caseAccessKey="second" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(apiService.markCaseActivity).toHaveBeenCalledTimes(1);
    session.replace();
    view.rerender(<CaseDetailPage caseId={15} onBack={vi.fn()} caseAccessKey="second" />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => expect(apiService.markCaseActivity).toHaveBeenCalledTimes(2));
  });

  it('header owner baseline: readonly fields and Save/Archive cannot issue a mutation', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: /^Архив$/u })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Стадия дела')).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    fireEvent.click(screen.getByRole('button', { name: /^Архив$/u }));
    expect(apiService.bulkUpdateCases).not.toHaveBeenCalled();
  });

  it('header owner baseline: reserves a write synchronously before a second click', async () => {
    vi.mocked(apiService.bulkUpdateCases).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    const save = screen.getByRole('button', { name: /^Сохранить$/u });
    act(() => { fireEvent.click(save); fireEvent.click(save); });
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
  });

  it('header owner baseline: a header write excludes review and editor actions', async () => {
    vi.mocked(apiService.bulkUpdateCases).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(apiService.reviewCasePosition).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading', { name: 'Редактирование дела' })).toBeNull();
  });

  it('header owner baseline: old access prompt cannot send for a previous page', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValue(new Error('Дело скрыто'));
    let finish!: (value: string | null) => void;
    dialogMock.prompt.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: /Запросить доступ/u }));
    view.rerender(<CaseDetailPage {...props} caseId={16} />);
    await act(async () => finish('Причина старого дела'));
    expect(apiService.requestCaseAccess).not.toHaveBeenCalled();
  });

  it('header owner baseline: blank access reason is not sent', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValue(new Error('Дело скрыто'));
    dialogMock.prompt.mockResolvedValue('   ');
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: /Запросить доступ/u }));
    await act(async () => {});
    expect(apiService.requestCaseAccess).not.toHaveBeenCalled();
  });

  it('header owner baseline: unavailable clipboard does not report a copied link', async () => {
    const old = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    onTestFinished(() => { if (old) Object.defineProperty(navigator, 'clipboard', old); else Reflect.deleteProperty(navigator, 'clipboard'); });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Ссылка' }));
    await act(async () => {});
    expect(screen.queryByRole('button', { name: 'Ссылка скопирована' })).toBeNull();
  });

  it('header owner: keeps one reservation through the authoritative post-write GET', async () => {
    let finish!: (value: ClientCase) => void;
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase()).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: /^Архив$/u })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Обновить' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: /^Архив$/u }));
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
    await act(async () => finish(createCase({ stage: 'Актуальная стадия' })));
    expect(screen.getByLabelText('Стадия дела')).toHaveProperty('value', 'Актуальная стадия');
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', false);
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
  });

  it.each(['write', 'read', 'zero'] as const)('header owner: %s uncertainty requires a read without repeating the write', async kind => {
    if (kind === 'write') vi.mocked(apiService.bulkUpdateCases).mockRejectedValueOnce(new Error('Ответ сохранения потерян'));
    if (kind === 'zero') vi.mocked(apiService.bulkUpdateCases).mockResolvedValueOnce({ updated: 0, requested: 1 });
    if (kind === 'read') vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase()).mockRejectedValueOnce(new Error('Не удалось перечитать дело'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(screen.getByLabelText('Стадия дела'), { target: { value: 'Несохранённый ввод' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Обновить' })).toHaveProperty('disabled', false));
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Стадия дела')).toHaveProperty('value', 'Несохранённый ввод');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
    const reads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(reads + 1));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', false));
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
  });

  it.each(['case', 'role', 'edit', 'login'] as const)('header owner: an old write cannot refresh after %s changes', async change => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let finish!: (value: { updated: number; requested: number }) => void;
    vi.mocked(apiService.bulkUpdateCases).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true };
    const view = render(<CaseDetailPage {...props} />);
    await screen.findByText('Дело 15');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    if (change === 'role') detailAuth.user = { id: 7, role: { id: 44 } };
    if (change === 'login') session.replace();
    if (change !== 'login') { view.rerender(<CaseDetailPage {...props} caseId={change === 'case' ? 16 : 15} canEdit={change !== 'edit'} />); await screen.findByText(change === 'case' ? 'Дело 16' : 'Дело 15'); }
    const reads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    await act(async () => finish({ updated: 1, requested: 1 }));
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(reads);
    expect(screen.queryByText('Этап дела изменён')).toBeNull();
  });

  it('header owner: rejects a post-write GET for another case', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase()).mockResolvedValue(createCase({ id: 16, title: 'Чужой ответ' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await screen.findByText('Не удалось подтвердить данные текущего дела. Обновите карточку.');
    expect(screen.queryByText('Чужой ответ')).toBeNull();
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
  });

  it('header owner: primary reads cannot admit another case or mark its activity', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ id: 16, title: 'Чужой ответ' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('Не удалось подтвердить данные текущего дела.');
    expect(screen.queryByText('Чужой ответ')).toBeNull();
    expect(apiService.markCaseActivity).not.toHaveBeenCalled();
  });

  it.each(['case', 'role', 'edit', 'login'] as const)('header owner: cancels the inactivity prompt after %s changes', async change => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let finish!: (reason: string | null) => void;
    dialogMock.prompt.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id }));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true };
    const view = render(<CaseDetailPage {...props} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByLabelText('Учитывать дело в автоконтроле'));
    const signal = dialogMock.prompt.mock.calls[0][0].signal as AbortSignal;
    expect(signal.aborted).toBe(false);
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    if (change === 'role') detailAuth.user = { id: 7, role: { id: 44 } };
    if (change === 'login') session.replace();
    else { view.rerender(<CaseDetailPage {...props} caseId={change === 'case' ? 16 : 15} canEdit={change !== 'edit'} />); await screen.findByText('ЗЕМЦОВ - банкротство'); expect(signal.aborted).toBe(true); }
    await act(async () => finish('Причина прежнего окна'));
    expect(screen.queryByText('Причина прежнего окна')).toBeNull();
    expect(screen.getByLabelText('Учитывать дело в автоконтроле')).toHaveProperty('checked', true);
    expect(apiService.bulkUpdateCases).not.toHaveBeenCalled();
  });

  it('header owner: duplicate access clicks share one prompt and trimmed request', async () => {
    vi.mocked(apiService.getCaseV2).mockRejectedValue(new Error('Дело скрыто'));
    let finish!: (reason: string | null) => void;
    dialogMock.prompt.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    const button = await screen.findByRole('button', { name: /Запросить доступ/u });
    act(() => { fireEvent.click(button); fireEvent.click(button); });
    expect(dialogMock.prompt).toHaveBeenCalledTimes(1);
    await act(async () => finish('  Нужна проверка сроков  '));
    expect(apiService.requestCaseAccess).toHaveBeenCalledTimes(1);
    expect(apiService.requestCaseAccess).toHaveBeenCalledWith(15, { reason: 'Нужна проверка сроков' });
  });

  it('header owner: full-editor failure requires refresh while preserving its draft', async () => {
    vi.mocked(apiService.updateCaseV2).mockRejectedValueOnce(new Error('Ответ сохранения потерян'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Мой сохранённый черновик' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u })[0]);
    await screen.findByRole('button', { name: 'Обновить данные дела' });
    for (const button of screen.getAllByRole('button', { name: /Сохранить дело/u })) expect(button).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить данные дела' }));
    await waitFor(() => expect(screen.getByLabelText('Стратегия и план действий')).toHaveProperty('value', 'Мой сохранённый черновик'));
    expect(apiService.updateCaseV2).toHaveBeenCalledTimes(1);
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
    for (const button of screen.getAllByRole('button', { name: /Сохранить дело/u })) expect(button).toHaveProperty('disabled', false);
  });

  it.each(['reject', 'missing'] as const)('header owner: %s clipboard outcome never claims a copy', async mode => {
    const old = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const clipboard = mode === 'reject' ? { writeText: vi.fn().mockRejectedValue(new Error('Копирование запрещено')) } : undefined;
    Object.defineProperty(navigator, 'clipboard', { value: clipboard, configurable: true });
    onTestFinished(() => { if (old) Object.defineProperty(navigator, 'clipboard', old); else Reflect.deleteProperty(navigator, 'clipboard'); });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Ссылка' }));
    await screen.findByText(mode === 'reject' ? 'Копирование запрещено' : 'Буфер обмена недоступен. Скопируйте адрес из строки браузера.');
    expect(screen.queryByRole('button', { name: 'Ссылка скопирована' })).toBeNull();
  });

  it.each(['bad-time', 'null-time', 'missing-name'] as const)('header owner: rejects %s review proof without a refresh or notice', async mode => {
    vi.mocked(apiService.reviewCasePosition).mockResolvedValue({ positionReviewedAt: mode === 'bad-time' ? 'bad' : mode === 'null-time' ? null : '2026-11-03T00:00:00Z', positionReviewedByName: mode === 'missing-name' ? undefined : 'Юрист' } as unknown as Awaited<ReturnType<typeof apiService.reviewCasePosition>>);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    await screen.findByText('Не удалось подтвердить сведения о проверке позиции. Обновите карточку.');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Позиция по делу отмечена как проверенная.')).toBeNull();
  });

  it.each(['wrong-hearing', 'null-time', 'missing-name'] as const)('header owner: rejects %s hearing proof', async mode => {
    const hearing = '2099-11-03T12:00:00Z';
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt: hearing }));
    vi.mocked(apiService.confirmNextHearing).mockResolvedValue({ nextHearingConfirmedAt: mode === 'null-time' ? null : '2026-11-03T00:00:00Z', nextHearingConfirmedByName: mode === 'missing-name' ? undefined : 'Юрист', nextHearingConfirmedForAt: mode === 'wrong-hearing' ? '2099-11-04T12:00:00Z' : hearing } as unknown as Awaited<ReturnType<typeof apiService.confirmNextHearing>>);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Подтвердить дату' }));
    await screen.findByText('Дата заседания изменилась или не подтверждена. Обновите карточку дела.');
    expect(screen.queryByText('Дата следующего заседания подтверждена.')).toBeNull();
  });

  it('header owner: uses UTC equivalence for a naive saved hearing and explicit proof', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt: '2099-11-03T12:00:00' }));
    vi.mocked(apiService.confirmNextHearing).mockResolvedValue({ nextHearingConfirmedAt: '2026-11-03T00:00:00', nextHearingConfirmedByName: 'Юрист', nextHearingConfirmedForAt: '2099-11-03T12:00:00Z', nextHearingConfirmedByUserId: 7 });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Подтвердить дату' }));
    await screen.findByText('Дата следующего заседания подтверждена.');
    expect(apiService.confirmNextHearing).toHaveBeenCalledTimes(1);
  });

  it('header owner: an admitted recovery write also blocks the header until its read finishes', async () => {
    vi.mocked(apiService.setCourtRecoveryPayment).mockReset();
    let finish!: () => void;
    vi.mocked(apiService.setCourtRecoveryPayment).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({ awardedAmount: 100000, receivedAmount: 0, remainingAmount: 100000, linkedPayments: [], availablePayments: [{ paymentId: 7, date: '2026-09-24', description: 'Поступление', paidAmount: 25000, allocatedAmount: null, effectiveAmount: 0, warning: null }], hasMoreAvailablePayments: false, hasCompletePaymentVisibility: true });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ courtAwardedAmount: 100000 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.change(await screen.findByRole('combobox', { name: 'Платёж для взыскания' }), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связь' }));
    await waitFor(() => expect(apiService.setCourtRecoveryPayment).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    expect(apiService.bulkUpdateCases).not.toHaveBeenCalled();
    await act(async () => finish());
    await waitFor(() => expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', false));
  });

  it('header owner: a reviewed response without a fresh review mark cannot claim success', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    await screen.findByText('Не удалось подтвердить актуальную отметку проверки позиции. Обновите карточку.');
    expect(screen.queryByText('Позиция по делу отмечена как проверенная.')).toBeNull();
    expect(apiService.reviewCasePosition).toHaveBeenCalledTimes(1);
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(2);
  });

  it('header owner: an old successful clipboard write cannot mark a new case', async () => {
    const old = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    let finish!: () => void;
    const writeText = vi.fn().mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    onTestFinished(() => { if (old) Object.defineProperty(navigator, 'clipboard', old); else Reflect.deleteProperty(navigator, 'clipboard'); });
    vi.mocked(apiService.getCaseV2).mockImplementation(async id => createCase({ id, title: `Дело ${id}` }));
    const view = render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    await screen.findByText('Дело 15');
    fireEvent.click(screen.getByRole('button', { name: 'Ссылка' }));
    expect(writeText).toHaveBeenCalledWith('http://localhost:3000/cases/15');
    view.rerender(<CaseDetailPage caseId={16} onBack={vi.fn()} canEdit={false} />);
    await screen.findByText('Дело 16');
    await act(async () => finish());
    expect(screen.queryByRole('button', { name: 'Ссылка скопирована' })).toBeNull();
  });

  it('header owner: a new login before React renders prevents clipboard dispatch', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    const old = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    onTestFinished(() => { if (old) Object.defineProperty(navigator, 'clipboard', old); else Reflect.deleteProperty(navigator, 'clipboard'); });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    session.replace();
    fireEvent.click(screen.getByRole('button', { name: 'Ссылка' }));
    expect(writeText).not.toHaveBeenCalled();
  });

  it.each([401, 403, 409, 503])('header owner: handles HTTP%s without replay and hides denied data', async status => {
    vi.mocked(apiService.bulkUpdateCases).mockRejectedValueOnce(new Error(`HTTP ${status} Error - Изменение не принято`));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    const message = status === 401 ? 'Сессия истекла. Войдите снова.' : status === 403 ? 'Недостаточно прав для выполнения действия.' : 'Изменение не принято';
    await screen.findByText(message);
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(1);
    if (status === 401 || status === 403) expect(screen.queryByText('ЗЕМЦОВ - банкротство')).toBeNull();
    else expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
  });

  it('header owner: archive uses case edit permission even without document deletion permission', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase()).mockResolvedValue(createCase({ status: 'Closed' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canDelete={false} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Архив' }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({ ids: [15], status: 'Closed' });
    expect(screen.getByRole('button', { name: 'Архив' })).toHaveProperty('disabled', true);
  });

  it('header owner: a header write blocks recovery without losing its selected amount', async () => {
    vi.mocked(apiService.setCourtRecoveryPayment).mockReset();
    let finish!: (result: { updated: number; requested: number }) => void;
    vi.mocked(apiService.bulkUpdateCases).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({ awardedAmount: 100000, receivedAmount: 0, remainingAmount: 100000, linkedPayments: [], availablePayments: [{ paymentId: 7, date: '2026-09-24', description: 'Поступление', paidAmount: 25000, allocatedAmount: null, effectiveAmount: 0, warning: null }], hasMoreAvailablePayments: false, hasCompletePaymentVisibility: true });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ courtAwardedAmount: 100000 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.change(await screen.findByRole('combobox', { name: 'Платёж для взыскания' }), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Сумма платежа в счёт взыскания'), { target: { value: '123,45' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    expect(screen.getByRole('button', { name: 'Сохранить связь' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связь' }));
    expect(apiService.setCourtRecoveryPayment).not.toHaveBeenCalled();
    await act(async () => finish({ updated: 1, requested: 1 }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сохранить связь' })).toHaveProperty('disabled', false));
    expect(screen.getByLabelText('Сумма платежа в счёт взыскания')).toHaveProperty('value', '123,45');
    expect(apiService.getCourtRecoveries).toHaveBeenCalledTimes(1);
  });

  it('header owner metadata: preserves profile time-zone refresh and a shared read signal', async () => {
    vi.mocked(apiService.getUserSetting).mockImplementation(async () => ({ key: 'profile.timeZoneId', value: vi.mocked(apiService.getCaseV2).mock.calls.length >= 2 ? 'Asia/Yakutsk' : 'UTC' }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', false));
    const signal = vi.mocked(apiService.getCaseV2).mock.calls[1][1]?.signal;
    expect(vi.mocked(apiService.getUserSetting).mock.calls.filter(([, options]) => options?.signal === signal)).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(await screen.findByDisplayValue('2026-05-20T18:30')).toBeTruthy();
  });

  it('header owner metadata: holds admission until the profile member of the read settles', async () => {
    let finish!: (value: { key: string; value: string } | null) => void;
    const pending = new Promise<{ key: string; value: string } | null>(resolve => { finish = resolve; });
    vi.mocked(apiService.getUserSetting).mockImplementation(() => vi.mocked(apiService.getCaseV2).mock.calls.length >= 2 ? pending : Promise.resolve(null));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await waitFor(() => expect(apiService.getCaseV2).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
    await act(async () => finish(null));
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', false);
    expect(apiService.bulkUpdateCases).toHaveBeenCalledTimes(1);
  });

  it('header owner metadata: failed case read aborts its pending profile sibling', async () => {
    vi.mocked(apiService.getUserSetting).mockImplementation(() => vi.mocked(apiService.getCaseV2).mock.calls.length >= 2 ? new Promise(() => {}) : Promise.resolve(null));
    vi.mocked(apiService.getCaseV2).mockResolvedValueOnce(createCase()).mockRejectedValue(new Error('Повторное чтение дела не удалось'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    await screen.findByText('Повторное чтение дела не удалось');
    const signal = vi.mocked(apiService.getCaseV2).mock.calls[1][1]?.signal;
    expect(signal?.aborted).toBe(true);
    expect(vi.mocked(apiService.getUserSetting).mock.calls.filter(([, options]) => options?.signal === signal)).toHaveLength(1);
    expect(screen.getByRole('button', { name: /^Сохранить$/u })).toHaveProperty('disabled', true);
  });

  it('header time-zone baseline: a restored draft keeps its original hearing instant after profile changes', async () => {
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ nextHearingAt: '2099-11-03T12:00:00Z' }));
    vi.mocked(apiService.getUserSetting).mockImplementation(async () => ({ key: 'profile.timeZoneId', value: vi.mocked(apiService.getCaseV2).mock.calls.length >= 2 ? 'Asia/Yakutsk' : 'UTC' }));
    vi.mocked(apiService.updateCaseV2).mockRejectedValueOnce(new Error('Ответ сохранения потерян'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(screen.getByLabelText('Заседание')).toHaveProperty('value', '2099-11-03T12:00');
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u })[0]);
    fireEvent.click(await screen.findByRole('button', { name: 'Обновить данные дела' }));
    await waitFor(() => expect(screen.getAllByRole('button', { name: /Сохранить дело/u })[0]).toHaveProperty('disabled', false));
    expect(screen.getByLabelText('Заседание')).toHaveProperty('value', '2099-11-03T12:00');
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/u })[0]);
    await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[1][1].nextHearingAt).toBe('2099-11-03T12:00:00Z');
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[1][1]).not.toHaveProperty('timeZoneId');
  });

  it.each(['position', 'hearing'] as const)('checked confirmation source: full page %s conflict retains source without retry', async kind => {
    const source = createCase({ strategy: '  Сохранённая позиция  ', nextHearingAt: '2099-11-03T12:00:00.1234567' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(source);
    const failure = new Error('HTTP 409 - {"message":"Данные изменены. Обновите карточку дела."}');
    if (kind === 'position') vi.mocked(apiService.reviewCasePosition).mockRejectedValueOnce(failure);
    else vi.mocked(apiService.confirmNextHearing).mockRejectedValueOnce(failure);
    render(<CaseDetailPage caseId={source.id} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    if (kind === 'hearing') fireEvent.click(screen.getByRole('button', { name: 'Суд' }));
    fireEvent.click(await screen.findByRole('button', { name: kind === 'position' ? 'Подтвердить проверку позиции' : 'Подтвердить дату' }));
    await screen.findByText('Данные изменены. Обновите карточку дела.');
    if (kind === 'position') expect(apiService.reviewCasePosition).toHaveBeenCalledExactlyOnceWith(source.id, source.strategy);
    else expect(apiService.confirmNextHearing).toHaveBeenCalledExactlyOnceWith(source.id, source.nextHearingAt);
    expect(screen.queryByText(/отмечена как проверенная|Дата следующего заседания подтверждена/)).toBeNull();
  });

  it('case edit source: explicit read preserves the original history baseline of the draft', async () => {
    const original = createCase({ eventHistory: 'Исходная история' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(original);
    vi.mocked(apiService.updateCaseV2).mockRejectedValueOnce(new Error('HTTP 503 - Ответ потерян'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.click((await screen.findAllByRole('button', { name: /Сохранить дело/ }))[0]);
    await screen.findByRole('button', { name: 'Обновить данные дела' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(createCase({ eventHistory: 'Новая запись коллеги' }));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить данные дела' }));
    await waitFor(() => expect(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]).not.toHaveProperty('disabled', true));
    vi.mocked(apiService.updateCaseV2).mockResolvedValue(createCase({ eventHistory: 'Новая запись коллеги' }));
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]);
    await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[1][2]).toMatchObject({ id: 15, eventHistory: 'Исходная история' });
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[1][1].eventHistory).toBe('Исходная история');
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[1][1]).not.toHaveProperty('originalSource');
  });

  it.each([false, true])('untouched case values baseline: full editor preserves raw text and date precision, change back %s', async changeBack => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    const source = createCase({ strategy: '  Исходная позиция  ', eventHistory: '  История\nсотрудника  ', documentsSummary: '  Материалы  ', nextHearingAt: '2099-11-03T12:34:56.1234567', startDate: '2026-10-01T12:34:56.1234567Z' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(source);vi.mocked(apiService.updateCaseV2).mockResolvedValue(source);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />); await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    await waitFor(() => expect(screen.getByLabelText('Заседание')).toHaveProperty('value', '2099-11-03T12:34'));
    if (changeBack) {
      fireEvent.change(screen.getByLabelText('Заседание'), { target: { value: '2099-11-04T12:34' } });
      fireEvent.change(screen.getByLabelText('Заседание'), { target: { value: '2099-11-03T12:34' } });
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Другая позиция' } });
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: source.strategy } });
    }
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Новое название' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]); await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledOnce());
    expect(vi.mocked(apiService.updateCaseV2).mock.calls[0][1]).toMatchObject({ strategy: source.strategy, eventHistory: source.eventHistory, documentsSummary: source.documentsSummary, nextHearingAt: source.nextHearingAt, startDate: source.startDate });
  });

  it.each(['2099-11-04T13:45', ''])('case exact explicit edit: full editor saves hearing %s and normalized new text', async hearing => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    const source = createCase({ strategy: '  Исходная позиция  ', eventHistory: '  История  ', nextHearingAt: '2099-11-03T12:34:56.1234567Z' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(source); vi.mocked(apiService.updateCaseV2).mockResolvedValue(source); render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />); await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    await waitFor(() => expect(screen.getByLabelText('Заседание')).toHaveProperty('value', '2099-11-03T12:34'));
    fireEvent.change(screen.getByLabelText('Заседание'), { target: { value: hearing } });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: '  Новая позиция  ' } });
    fireEvent.change(screen.getByLabelText('История событий'), { target: { value: '  Новая история  ' } });
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]); await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledOnce()); const patch = vi.mocked(apiService.updateCaseV2).mock.calls[0][1];
    expect(patch.nextHearingAt).toBe(hearing ? '2099-11-04T13:45:00.000Z' : null); expect(patch.strategy).toBe('Новая позиция'); expect(patch.eventHistory).toBe('Новая история');
  });

  it('case exact nullable: full editor retains absent identifiers and court role as null', async () => {
    const source = createCase({ claimantInn: null, claimantOgrn: null, defendantInn: null, defendantOgrn: null, ourCourtRole: null });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(source); vi.mocked(apiService.updateCaseV2).mockResolvedValue(source); render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />); await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]); await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledOnce()); const body = vi.mocked(apiService.updateCaseV2).mock.calls[0][1];
    expect(body).toMatchObject({ claimantInn: null, claimantOgrn: null, defendantInn: null, defendantOgrn: null, ourCourtRole: null });
  });

  it('case exact newline: full editor keeps original CRLF after a visible edit and return', async () => {
    const source = createCase({ strategy: '  Первая строка\r\nВторая строка  ', eventHistory: '  Запись\r\nИстория  ' });
    vi.mocked(apiService.getCaseV2).mockResolvedValue(source); vi.mocked(apiService.updateCaseV2).mockResolvedValue(source); render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />); await screen.findByText('ЗЕМЦОВ - банкротство'); fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    for (const [label, original] of [['Стратегия и план действий', source.strategy!], ['История событий', source.eventHistory!]]) {
      fireEvent.change(screen.getByLabelText(label), { target: { value: 'Временный текст' } });
      fireEvent.change(screen.getByLabelText(label), { target: { value: original.replace(/\r\n/g, '\n') } });
    }
    fireEvent.click(screen.getAllByRole('button', { name: /Сохранить дело/ })[0]); await waitFor(() => expect(apiService.updateCaseV2).toHaveBeenCalledOnce()); const body = vi.mocked(apiService.updateCaseV2).mock.calls[0][1];
    expect(body.strategy).toBe(source.strategy); expect(body.eventHistory).toBe(source.eventHistory);
  });


  it('page document owner baseline: an old delete confirmation cannot dispatch for another page', async () => {
    let approve!: (value: boolean) => void;
    dialogMock.confirm.mockReturnValue(new Promise(resolve => { approve = resolve; }));
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить документ Заявление.docx' }));
    await waitFor(() => expect(dialogMock.confirm).toHaveBeenCalledOnce());
    view.rerender(<CaseDetailPage {...props} caseId={16} />);
    await act(async () => approve(true));
    expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });

  it('page document owner baseline: a replaced login prevents a confirmed deletion before context rerenders', async () => {
    const session = documentCopyTestSession();
    onTestFinished(() => session.restore());
    let approve!: (value: boolean) => void;
    dialogMock.confirm.mockReturnValue(new Promise(resolve => { approve = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить документ Заявление.docx' }));
    session.replace('actor');
    await act(async () => approve(true));
    expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });

  it('page document owner baseline: one pending deletion prompt excludes another prompt and header save', async () => {
    dialogMock.confirm.mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    const remove = screen.getByRole('button', { name: 'Удалить документ Заявление.docx' });
    act(() => { fireEvent.click(remove); fireEvent.click(remove); });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    expect(dialogMock.confirm).toHaveBeenCalledOnce();
    expect(apiService.bulkUpdateCases).not.toHaveBeenCalled();
  });

  it('page document owner baseline: a late download cannot allocate a URL after leaving the case', async () => {
    let answer!: (value: { blob: Blob; fileName: string }) => void;
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise(resolve => { answer = resolve; }));
    const old = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createUrl = vi.fn(() => 'blob:stale-document');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    onTestFinished(() => { if (old) Object.defineProperty(URL, 'createObjectURL', old); else Reflect.deleteProperty(URL, 'createObjectURL'); });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    onTestFinished(() => click.mockRestore());
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать документ Заявление.docx' }));
    view.rerender(<CaseDetailPage {...props} caseId={16} />);
    await act(async () => answer({ blob: new Blob(['private']), fileName: 'private.pdf' }));
    expect(createUrl).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });

  it('page document owner baseline: download cancellation uses the existing API signal', async () => {
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise(() => {}));
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать документ Заявление.docx' }));
    const signal = vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal;
    expect(signal).toBeInstanceOf(AbortSignal);
    view.rerender(<CaseDetailPage {...props} canEdit={false} />);
    expect(signal?.aborted).toBe(true);
  });

  it('page document owner baseline: text preview allocates its URL only after the current content settles', async () => {
    let finishText!: (value: string) => void;
    const text = vi.fn(() => new Promise<string>(resolve => { finishText = resolve; }));
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({ blob: { type: 'text/plain', size: 12, text } as unknown as Blob, fileName: 'private.txt' });
    const old = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createUrl = vi.fn(() => 'blob:stale-preview');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    onTestFinished(() => { if (old) Object.defineProperty(URL, 'createObjectURL', old); else Reflect.deleteProperty(URL, 'createObjectURL'); });
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    await waitFor(() => expect(text).toHaveBeenCalledOnce());
    expect(createUrl).not.toHaveBeenCalled();
    view.rerender(<CaseDetailPage {...props} caseId={16} />);
    await act(async () => finishText('private text'));
    expect(createUrl).not.toHaveBeenCalled();
    expect(screen.queryByTitle('Предпросмотр private.txt')).toBeNull();
  });

  it('page document owner baseline: disk delivery needs its own explicit admission', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    expect(screen.queryByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' })).toBeNull();
    expect(apiService.uploadCaseDocumentToYandexDisk).not.toHaveBeenCalled();
  });

  it('page document owner baseline: old disk click cannot dispatch after login replacement', async () => {
    const session = documentCopyTestSession();
    onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canSendToYandexDisk />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    const send = screen.getByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' });
    session.replace('actor');
    fireEvent.click(send);
    expect(apiService.uploadCaseDocumentToYandexDisk).not.toHaveBeenCalled();
  });

  it('page document owner baseline: double generation reserves one operation synchronously', async () => {
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const generate = await screen.findByRole('button', { name: /^Сформировать$/u });
    await waitFor(() => expect(generate).toHaveProperty('disabled', false));
    act(() => { fireEvent.click(generate); fireEvent.click(generate); });
    expect(apiService.generateCaseDocumentTemplate).toHaveBeenCalledOnce();
  });

  it('page document owner baseline: pending header mutation prevents a document upload', async () => {
    vi.mocked(apiService.bulkUpdateCases).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    fireEvent.change(screen.getByLabelText('Файл документа'), { target: { files: [new File(['body'], 'new.pdf')] } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    fireEvent.click(screen.getByRole('button', { name: /^Загрузить$/u }));
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled();
  });

  it('page document owner baseline: a cached version input cannot send for a replaced login', async () => {
    const session = documentCopyTestSession();
    onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    const input = screen.getByLabelText('Загрузить новую версию документа Заявление.docx');
    session.replace('actor');
    fireEvent.change(input, { target: { files: [new File(['body'], 'new-version.pdf')] } });
    expect(apiService.uploadCaseDocumentVersion).not.toHaveBeenCalled();
  });


  it('page document owner: an allowed readonly user can send to disk independently of edit access', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} canSendToYandexDisk />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' }));
    await waitFor(() => expect(apiService.uploadCaseDocumentToYandexDisk).toHaveBeenCalledExactlyOnceWith(7));
  });

  it('page document owner: revoking disk admission cancels its pending UI adoption', async () => {
    let finish!: (value: { success: boolean; path: string }) => void;
    vi.mocked(apiService.uploadCaseDocumentToYandexDisk).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const props = { caseId: 15, onBack: vi.fn(), canSendToYandexDisk: true };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' }));
    view.rerender(<CaseDetailPage {...props} canSendToYandexDisk={false} />);
    await act(async () => finish({ success: true, path: '/old-owner/secret' }));
    expect(screen.queryByText(/old-owner\/secret/u)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Отправить документ Заявление.docx на Яндекс.Диск' })).toBeNull();
  });

  it('page document owner: cancelled deletion releases the reservation and permission replacement aborts the next prompt', async () => {
    dialogMock.confirm.mockResolvedValueOnce(false).mockReturnValueOnce(new Promise(() => {}));
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    const remove = screen.getByRole('button', { name: 'Удалить документ Заявление.docx' });
    fireEvent.click(remove);
    await waitFor(() => expect(remove).toHaveProperty('disabled', false));
    fireEvent.click(remove);
    await waitFor(() => expect(dialogMock.confirm).toHaveBeenCalledTimes(2));
    const signal = dialogMock.confirm.mock.calls[1][0].signal as AbortSignal;
    expect(signal.aborted).toBe(false);
    view.rerender(<CaseDetailPage {...props} canDelete={false} />);
    expect(signal.aborted).toBe(true);
    expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });

  it('page document owner: failed upload retains the selected file and an explicit retry clears it only after success', async () => {
    let fail!: (error: Error) => void;
    vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    const input = screen.getByLabelText('Файл документа');
    const file = new File(['private body'], 'my-document.pdf');
    fireEvent.change(input, { target: { files: [file] } });
    const submit = screen.getByRole('button', { name: /^Загрузить$/u });
    fireEvent.click(submit);
    expect(input.matches(':disabled')).toBe(true);
    await act(async () => fail(new Error('Ошибка загрузки')));
    expect(await screen.findByText('Ошибка загрузки')).toBeTruthy();
    expect(input.matches(':disabled')).toBe(false);
    fireEvent.click(submit);
    await waitFor(() => expect(apiService.uploadCaseDocument).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.uploadCaseDocument).mock.calls[0]).toEqual(vi.mocked(apiService.uploadCaseDocument).mock.calls[1]);
    await screen.findByText('Документ «my-document.pdf» загружен.');
  });

  it.each(['download', 'preview', 'update', 'delete', 'generate', 'upload'] as const)('page document owner: stale %s control cannot dispatch after a replaced login', async action => {
    const session = documentCopyTestSession();
    onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    if (action === 'upload') fireEvent.change(screen.getByLabelText('Файл документа'), { target: { files: [new File(['body'], 'private.pdf')] } });
    const labels = {
      download: 'Скачать документ Заявление.docx', preview: 'Предпросмотр документа Заявление.docx',
      update: 'Сохранить документ Заявление.docx', delete: 'Удалить документ Заявление.docx',
      generate: 'Сформировать', upload: 'Загрузить',
    };
    const button = screen.getByRole('button', { name: labels[action] });
    session.replace('actor');
    fireEvent.click(button);
    expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();
    expect(apiService.updateCaseDocument).not.toHaveBeenCalled();
    expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
    expect(apiService.generateCaseDocumentTemplate).not.toHaveBeenCalled();
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled();
    expect(dialogMock.confirm).not.toHaveBeenCalled();
  });


  async function choosePageParticipantSource() {
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '25')).toBe(true));
    fireEvent.change(screen.getByLabelText('Участник дела'), { target: { value: '25' } });
    return screen.getByRole('button', { name: /^Добавить$/u });
  }

  async function showPageArchivedParticipant(overrides: Partial<ClientCaseRelatedParty> = {}) {
    vi.mocked(apiService.getCaseRelatedParties).mockImplementation(async (_caseId, includeArchived) => includeArchived ? [{
      id: 42, clientCaseId: 15, sourceType: 'client', sourceId: 31, clientId: 31,
      displayName: 'Архивный участник', role: 'Третье лицо', notes: 'Сохранённое основание',
      createdAt: '2026-05-01T00:00:00Z', isArchived: true, ...overrides,
    }] : []);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Показать архив' }));
    return await screen.findByRole('button', { name: 'Восстановить связь с участником Архивный участник' });
  }

  it('page participant owner baseline: readonly access hides add and relation mutations', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit={false} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await screen.findByRole('heading', { name: 'Связанные лица' });
    expect(screen.queryByRole('button', { name: /^Добавить$/u })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' })).toBeNull();
    expect(apiService.getClients).not.toHaveBeenCalled();
    expect(apiService.getClientsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(apiService.getContacts).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: a replaced login cannot use the old add control', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    session.replace('actor');
    fireEvent.click(add);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: pending add reserves one write before duplicate clicks', async () => {
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    act(() => { fireEvent.click(add); fireEvent.click(add); });
    expect(apiService.addCaseRelatedParty).toHaveBeenCalledOnce();
  });

  it('page participant owner baseline: a pending header write blocks add', async () => {
    vi.mocked(apiService.bulkUpdateCases).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/u }));
    fireEvent.click(add);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: source changes clear an equal ID from another directory', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([{ id: 25, name: 'Клиент 25', phone: '', email: '', company: '', address: '', notes: '', isActive: true, createdAt: '2026-05-01T00:00:00Z' }]);
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [{ ...createCase().client!, id: 25, name: 'Клиент 25' }], total: 1, page: 1, pageSize: 50 });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '25')).toBe(true));
    fireEvent.change(screen.getByLabelText('Участник дела'), { target: { value: '25' } });
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '');
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: an old delete prompt cannot dispatch after case replacement', async () => {
    let approve!: (value: boolean) => void;
    dialogMock.confirm.mockReturnValue(new Promise(resolve => { approve = resolve; }));
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' }));
    view.rerender(<CaseDetailPage {...props} caseId={16} />);
    await act(async () => approve(true));
    expect(apiService.deleteCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: a replaced actor invalidates a pending delete prompt before render', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let approve!: (value: boolean) => void;
    dialogMock.confirm.mockReturnValue(new Promise(resolve => { approve = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' }));
    session.replace('actor');
    await act(async () => approve(true));
    expect(apiService.deleteCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: delete confirmation prevents a second prompt and add', async () => {
    dialogMock.confirm.mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    const remove = await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' });
    act(() => { fireEvent.click(remove); fireEvent.click(remove); fireEvent.click(add); });
    expect(dialogMock.confirm).toHaveBeenCalledOnce();
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: cached restore cannot dispatch for a replaced actor', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const restore = await showPageArchivedParticipant();
    session.replace('actor');
    fireEvent.click(restore);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner baseline: pending restore excludes a second restoration', async () => {
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const restore = await showPageArchivedParticipant();
    act(() => { fireEvent.click(restore); fireEvent.click(restore); });
    expect(apiService.addCaseRelatedParty).toHaveBeenCalledOnce();
  });

  it('page participant owner baseline: list failures are explicit and do not pretend to be empty', async () => {
    vi.mocked(apiService.getCaseRelatedParties).mockRejectedValue(new Error('Private server diagnostics'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(screen.queryByText('Загрузка участников дела...')).toBeNull());
    expect(screen.queryByRole('alert')).not.toBeNull();
    expect(screen.queryByText(/Private server diagnostics/u)).toBeNull();
    expect(screen.queryByText('Связанные лица пока не добавлены.')).toBeNull();
  });

  it('page participant owner baseline: stale contact disclosure cannot reveal the old party phone', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    const reveal = await screen.findByRole('button', { name: 'Показать контакты' });
    session.replace('actor');
    fireEvent.click(reveal);
    expect(screen.queryByText(/\+79991234567/u)).toBeNull();
  });

  it('page participant owner baseline: stale audit disclosure cannot start a private read', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    const audit = await screen.findByRole('button', { name: 'История участника Земцов Виктор Александрович' });
    session.replace('actor');
    fireEvent.click(audit);
    expect(apiService.getObjectAuditHistory).not.toHaveBeenCalled();
  });


  it('page participant owner: current failure preserves source, role and notes for explicit retry', async () => {
    vi.mocked(apiService.addCaseRelatedParty).mockRejectedValueOnce(new Error('Ошибка связи'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: '  Третье лицо  ' } });
    fireEvent.change(screen.getByLabelText('Основание связи участника'), { target: { value: '  Основание  ' } });
    const reads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    fireEvent.click(add);
    await screen.findByText('Ошибка связи');
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '25');
    expect(screen.getByLabelText('Роль участника')).toHaveProperty('value', '  Третье лицо  ');
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(reads);
    fireEvent.click(add);
    await waitFor(() => expect(apiService.addCaseRelatedParty).toHaveBeenCalledTimes(2));
    const calls = vi.mocked(apiService.addCaseRelatedParty).mock.calls;
    expect(calls[1]).toEqual(calls[0]);
    expect(calls[1]).toEqual([15, { sourceType: 'legalEntity', sourceId: 25, role: 'Третье лицо', notes: 'Основание' }]);
  });

  it('page participant owner: loaded source membership is required before an injected selection can be sent', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    const select = screen.getByLabelText('Участник дела') as HTMLSelectElement;
    select.add(new Option('Прежняя запись', '9999'));
    fireEvent.change(select, { target: { value: '9999' } });
    fireEvent.click(add);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
    expect(await screen.findByText('Выберите участника дела из текущего справочника.')).toBeTruthy();
  });

  it('page participant owner: case-source scope replacement invalidates its old confirmation', async () => {
    let approve!: (value: boolean) => void;
    dialogMock.confirm.mockReturnValue(new Promise(resolve => { approve = resolve; }));
    const props = { caseId: 15, onBack: vi.fn(), caseAccessKey: 'sources-before' };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' }));
    const signal = dialogMock.confirm.mock.calls[0][0].signal as AbortSignal;
    view.rerender(<CaseDetailPage {...props} caseAccessKey="sources-after" />);
    expect(signal.aborted).toBe(true);
    await act(async () => approve(true));
    expect(apiService.deleteCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('page participant owner: deleting a relation uses case edit independently of document deletion', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canDelete={false} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' }));
    await waitFor(() => expect(apiService.deleteCaseRelatedParty).toHaveBeenCalledExactlyOnceWith(15, 1));
  });

  it.each([null, 0])('page participant owner: legacy sourceId=%s restores through its unambiguous stored FK', async sourceId => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const restore = await showPageArchivedParticipant({ sourceId });
    fireEvent.click(restore);
    await waitFor(() => expect(apiService.addCaseRelatedParty).toHaveBeenCalledExactlyOnceWith(15, {
      sourceType: 'client', sourceId: 31, role: 'Третье лицо', notes: 'Сохранённое основание',
    }));
  });

  it.each([
    { sourceId: null, clientId: null }, { sourceId: -1 }, { sourceId: 31.5 }, { sourceId: 32 },
  ])('page participant owner: inconsistent archived source %j is rejected without guessing', async overrides => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const restore = await showPageArchivedParticipant(overrides);
    fireEvent.click(restore);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
    expect(await screen.findByText('Не удалось однозначно определить участника. Обновите карточку дела.')).toBeTruthy();
  });

  it('page participant owner: list retry reads only current participants and hides raw diagnostics', async () => {
    vi.mocked(apiService.getCaseRelatedParties).mockRejectedValueOnce(new Error('Private diagnostics'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    const retry = await screen.findByRole('button', { name: 'Повторить загрузку участников' });
    const directoryReads = vi.mocked(apiService.getClientsV2).mock.calls.length;
    const caseReads = vi.mocked(apiService.getCaseV2).mock.calls.length;
    fireEvent.click(retry);
    await screen.findByRole('heading', { name: 'Связанные лица' });
    expect(apiService.getCaseRelatedParties).toHaveBeenCalledTimes(2);
    expect(apiService.getCaseRelatedParties).toHaveBeenLastCalledWith(15, false, { signal: expect.any(AbortSignal) });
    expect(apiService.getClientsV2).toHaveBeenCalledTimes(directoryReads);
    expect(apiService.getCaseV2).toHaveBeenCalledTimes(caseReads);
    expect(screen.queryByText('Private diagnostics')).toBeNull();
  });

  it('page participant owner: a stale retry cannot issue a read after an actor replacement', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    vi.mocked(apiService.getCaseRelatedParties).mockRejectedValue(new Error('Unavailable'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    const retry = await screen.findByRole('button', { name: 'Повторить загрузку участников' });
    session.replace('actor');
    fireEvent.click(retry);
    expect(apiService.getCaseRelatedParties).toHaveBeenCalledTimes(1);
  });

  it.each([{ id: 1, clientCaseId: 16 }, { id: 0, clientCaseId: 15 }])('page participant owner: unexpected row %j is an explicit load refusal', async row => {
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([{ ...row, sourceType: 'client', sourceId: 12, displayName: 'Чужая строка', createdAt: '2026-05-01T00:00:00Z' }]);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await screen.findByRole('alert');
    expect(screen.queryByText('Чужая строка')).toBeNull();
    expect(screen.queryByRole('button', { name: /^Добавить$/u })).toHaveProperty('disabled', true);
  });

  it('page participant owner: cancelled delete releases the reservation for the next explicit attempt', async () => {
    dialogMock.confirm.mockResolvedValueOnce(false).mockResolvedValueOnce(true);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    const remove = await screen.findByRole('button', { name: 'Удалить связь с участником Земцов Виктор Александрович' });
    fireEvent.click(remove);
    await waitFor(() => expect(remove).toHaveProperty('disabled', false));
    expect(apiService.deleteCaseRelatedParty).not.toHaveBeenCalled();
    fireEvent.click(remove);
    await waitFor(() => expect(apiService.deleteCaseRelatedParty).toHaveBeenCalledExactlyOnceWith(15, 1));
  });

  it('page participant owner: late add failure cannot show its message after the actor changed', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let reject!: (error: Error) => void;
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise((_, fail) => { reject = fail; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} />);
    const add = await choosePageParticipantSource();
    fireEvent.click(add);
    session.replace('actor');
    await act(async () => reject(new Error('Private old response')));
    expect(screen.queryByText('Private old response')).toBeNull();
  });

  it('page participant owner: a harmless parent render and token refresh preserve the selected draft without extra reads', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    const props = { caseId: 15, onBack: vi.fn() };
    const view = render(<CaseDetailPage {...props} />);
    await choosePageParticipantSource();
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: 'Мой черновик' } });
    const reads = vi.mocked(apiService.getCaseRelatedParties).mock.calls.length;
    session.refresh();
    view.rerender(<CaseDetailPage {...props} />);
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '25');
    expect(screen.getByLabelText('Роль участника')).toHaveProperty('value', 'Мой черновик');
    expect(apiService.getCaseRelatedParties).toHaveBeenCalledTimes(reads);
  });

  it.each(['add', 'restore', 'generate', 'disk', 'delete'] as const)('page editor composition baseline: pending %s reserves the editor transition before React renders', async kind => {
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(() => {}));
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReturnValue(new Promise(() => {}));
    vi.mocked(apiService.uploadCaseDocumentToYandexDisk).mockReturnValue(new Promise(() => {}));
    dialogMock.confirm.mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit canSendToYandexDisk />);
    let start: HTMLElement;
    if (kind === 'add') start = await choosePageParticipantSource();
    else if (kind === 'restore') start = await showPageArchivedParticipant();
    else {
      fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
      await openDocumentDetails();
      const names = {
        generate: 'Сформировать',
        disk: 'Отправить документ Заявление.docx на Яндекс.Диск',
        delete: 'Удалить документ Заявление.docx',
      };
      start = screen.getByRole('button', { name: names[kind] });
    }
    const edit = screen.getByRole('button', { name: 'Редактировать дело' });
    act(() => { fireEvent.click(start); fireEvent.click(edit); });
    expect(screen.queryByRole('heading', { name: 'Редактирование дела' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Редактировать дело' })).toHaveProperty('disabled', true);
  });

  it.each(['add', 'generate'] as const)('page editor composition baseline: opening the editor rejects a cached %s callback in the same turn', async kind => {
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(() => {}));
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    let start: HTMLElement;
    if (kind === 'add') start = await choosePageParticipantSource();
    else {
      fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
      await openDocumentDetails();
      start = screen.getByRole('button', { name: /^Сформировать$/u });
    }
    act(() => { fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' })); fireEvent.click(start); });
    expect(screen.getByRole('heading', { name: 'Редактирование дела' })).toBeTruthy();
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
    expect(apiService.generateCaseDocumentTemplate).not.toHaveBeenCalled();
  });

  it.each(['download', 'preview'] as const)('page editor composition baseline: opening the editor cancels a pending %s and rejects its eventual file', async kind => {
    let finish!: (result: { blob: Blob; fileName: string }) => void;
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const old = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createUrl = vi.fn(() => 'blob:old-editor-file');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    onTestFinished(() => { if (old) Object.defineProperty(URL, 'createObjectURL', old); else Reflect.deleteProperty(URL, 'createObjectURL'); });
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    onTestFinished(() => anchorClick.mockRestore());
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: kind === 'download' ? 'Скачать документ Заявление.docx' : 'Предпросмотр документа Заявление.docx' }));
    const signal = vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    const abortedOnOpen = signal?.aborted;
    await act(async () => finish({ blob: new Blob(['old editor response'], { type: 'application/pdf' }), fileName: 'old-editor.pdf' }));
    expect(abortedOnOpen).toBe(true);
    expect(createUrl).not.toHaveBeenCalled();
    expect(anchorClick).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Карточка дела' }));
    await openDocumentDetails();
    expect(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' })).toHaveProperty('disabled', false);
    expect(screen.getByRole('button', { name: 'Скачать документ Заявление.docx' })).toHaveProperty('disabled', false);
  });

  it('page editor composition baseline: pending text cannot allocate a preview after the editor opens', async () => {
    let finishText!: (value: string) => void;
    const text = vi.fn(() => new Promise<string>(resolve => { finishText = resolve; }));
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({ blob: { type: 'text/plain', size: 8, text } as unknown as Blob, fileName: 'old-editor.txt' });
    const old = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createUrl = vi.fn(() => 'blob:old-editor-text');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    onTestFinished(() => { if (old) Object.defineProperty(URL, 'createObjectURL', old); else Reflect.deleteProperty(URL, 'createObjectURL'); });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    await waitFor(() => expect(text).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    await act(async () => finishText('old editor private text'));
    expect(createUrl).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Карточка дела' }));
    expect(screen.queryByText('old editor private text')).toBeNull();
  });

  it('page editor composition baseline: an open preview is released and cannot reappear after returning from the editor', async () => {
    const oldCreate = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const oldRevoke = Object.getOwnPropertyDescriptor(URL, 'revokeObjectURL');
    const createUrl = vi.fn(() => 'blob:preview-before-editor');
    const revokeUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeUrl });
    onTestFinished(() => {
      if (oldCreate) Object.defineProperty(URL, 'createObjectURL', oldCreate); else Reflect.deleteProperty(URL, 'createObjectURL');
      if (oldRevoke) Object.defineProperty(URL, 'revokeObjectURL', oldRevoke); else Reflect.deleteProperty(URL, 'revokeObjectURL');
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    await waitFor(() => expect(createUrl).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(revokeUrl).toHaveBeenCalledWith('blob:preview-before-editor');
    fireEvent.click(screen.getByRole('button', { name: 'Карточка дела' }));
    expect(screen.queryByTitle('Предпросмотр Исковое заявление.pdf')).toBeNull();
  });


  it('page editor composition: a completed participant write releases the editor transition', async () => {
    let finish!: (party: ClientCaseRelatedParty) => void;
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    const add = await choosePageParticipantSource();
    fireEvent.click(add);
    expect(screen.getByRole('button', { name: 'Редактировать дело' })).toHaveProperty('disabled', true);
    await act(async () => finish({ id: 72, clientCaseId: 15, sourceType: 'legalEntity', sourceId: 25, legalEntityId: 25, displayName: 'Организация', role: 'Участник', createdAt: '2026-11-04T00:00:00Z' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Редактировать дело' })).toHaveProperty('disabled', false));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(screen.getByRole('heading', { name: 'Редактирование дела' })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Сохранить дело/u })[0]).toHaveProperty('disabled', false);
  });

  it('page editor composition: cancelling a pending deletion leaves the editor available', async () => {
    dialogMock.confirm.mockResolvedValue(false);
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить документ Заявление.docx' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Редактировать дело' })).toHaveProperty('disabled', false));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(screen.getByRole('heading', { name: 'Редактирование дела' })).toBeTruthy();
    expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });

  it.each(['download', 'preview'] as const)('page editor composition: a cached %s cannot start after the editor opens in the same turn', async kind => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    const start = screen.getByRole('button', { name: kind === 'download' ? 'Скачать документ Заявление.docx' : 'Предпросмотр документа Заявление.docx' });
    act(() => { fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' })); fireEvent.click(start); });
    expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();
  });

  it('page editor composition: a cancelled read failure cannot replace the editor or appear on return', async () => {
    let fail!: (reason: Error) => void;
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise((_resolve, reject) => { fail = reject; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Текущий черновик редактора' } });
    await act(async () => fail(new Error('Ошибка прежнего чтения')));
    expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Текущий черновик редактора');
    expect(screen.queryByText('Ошибка прежнего чтения')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Карточка дела' }));
    expect(screen.queryByText('Ошибка прежнего чтения')).toBeNull();
  });

  it('page editor composition: returning from the editor admits a new current file read', async () => {
    let finish!: (result: { blob: Blob; fileName: string }) => void;
    vi.mocked(apiService.downloadCaseDocument).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const old = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createUrl = vi.fn(() => 'blob:current-after-editor');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    onTestFinished(() => { if (old) Object.defineProperty(URL, 'createObjectURL', old); else Reflect.deleteProperty(URL, 'createObjectURL'); });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    fireEvent.click(screen.getByRole('button', { name: 'Карточка дела' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    await screen.findByTitle('Предпросмотр Исковое заявление.pdf');
    expect(apiService.downloadCaseDocument).toHaveBeenCalledTimes(2);
    expect(createUrl).toHaveBeenCalledOnce();
    await act(async () => finish({ blob: new Blob(['obsolete'], { type: 'application/pdf' }), fileName: 'obsolete.pdf' }));
    expect(createUrl).toHaveBeenCalledOnce();
    expect(screen.getByTitle('Предпросмотр Исковое заявление.pdf')).toBeTruthy();
  });

  it('page editor composition: a token refresh and harmless render retain a current file read', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise(() => {}));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Документы' }));
    await openDocumentDetails();
    fireEvent.click(screen.getByRole('button', { name: 'Предпросмотр документа Заявление.docx' }));
    const signal = vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal;
    session.refresh();
    view.rerender(<CaseDetailPage {...props} />);
    expect(signal?.aborted).toBe(false);
    expect(apiService.downloadCaseDocument).toHaveBeenCalledOnce();
  });

  it('page editor composition: an attachment read is cancelled when entering the editor', async () => {
    vi.mocked(apiService.getCaseConversation).mockResolvedValue([{
      id: 71, clientCaseId: 15, kind: 'message', text: 'Сообщение с вложением', occurredAt: '2026-11-04T00:00:00Z',
      attachments: [{ documentId: 7, fileName: 'scan.pdf', contentType: 'application/pdf', sizeBytes: 12 }],
    }]);
    vi.mocked(apiService.downloadCaseDocument).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Переписка' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Скачать вложение scan.pdf' }));
    const signal = vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать дело' }));
    expect(signal?.aborted).toBe(true);
  });


  it('participant directory baseline: loads only the selected bounded client source', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledWith({ search: undefined, isActive: true, page: 1, pageSize: 50 }, { signal: expect.any(AbortSignal) }));
    expect(apiService.getClients).not.toHaveBeenCalled();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(apiService.getContacts).not.toHaveBeenCalled();
  });

  it('participant directory baseline: server search reaches an organization outside the first 200 rows', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => ({ items: query?.search === 'Организация 250' ? [{ id: 250, shortName: 'Организация 250', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }] : [], total: query?.search ? 1 : 250, page: query?.page ?? 1, pageSize: query?.pageSize ?? 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    fireEvent.change(screen.getByLabelText('Поиск участника дела'), { target: { value: 'Организация 250' } });
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '250')).toBe(true));
    expect(apiService.getLegalEntitiesPaged).toHaveBeenLastCalledWith({ search: 'Организация 250', page: 1, pageSize: 50 }, { signal: expect.any(AbortSignal) });
  });

  it('participant directory baseline: next page keeps earlier organizations available', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => ({ items: [{ id: query?.page === 2 ? 251 : 25, shortName: query?.page === 2 ? 'Следующая организация' : 'Первая организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 51, page: query?.page ?? 1, pageSize: 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Ещё записи справочника участников' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).map(option => option.value)).toEqual(expect.arrayContaining(['25', '251'])));
  });

  it('participant directory baseline: a failed selected source offers an explicit retry and preserves role', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockRejectedValueOnce(new Error('Скрытая диагностика справочника'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: 'Представитель' } });
    const retry = await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' });
    expect(screen.queryByText('Скрытая диагностика справочника')).toBeNull();
    fireEvent.click(retry);
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '25')).toBe(true));
    expect(screen.getByLabelText('Роль участника')).toHaveProperty('value', 'Представитель');
  });


  function directoryContact(id: number): ContactDirectoryItemDto {
    return { sourceType: 'external', category: 'external', sourceId: id, contactType: 'Person', roles: ['Contact'],
      fullName: `Контакт ${id}`, hasFullProfile: false, phones: [], emails: [], isActive: true, syncToPhonebook: false };
  }

  it('participant directory: external contacts use bounded external scope and an explicit final page', async () => {
    vi.mocked(apiService.getContacts).mockImplementation(async query => query?.page === 2 ? [directoryContact(1050)] : Array.from({ length: 50 }, (_, index) => directoryContact(1000 + index)));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'externalContact' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Ещё записи справочника участников' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options)).toHaveLength(52));
    expect(apiService.getContacts).toHaveBeenLastCalledWith({ scope: 'external', search: undefined, page: 2, pageSize: 50 }, { signal: expect.any(AbortSignal) });
    expect(screen.queryByRole('button', { name: 'Ещё записи справочника участников' })).toBeNull();
  });

  it('participant directory: a pending unrelated source cannot hold the selected client directory', async () => {
    vi.mocked(apiService.getContacts).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '30')).toBe(true));
    expect(apiService.getContacts).not.toHaveBeenCalled();
  });

  it('participant directory: changing source aborts and rejects the prior directory response', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getLegalEntitiesPaged>>) => void;
    vi.mocked(apiService.getLegalEntitiesPaged).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledOnce());
    const signal = vi.mocked(apiService.getLegalEntitiesPaged).mock.calls[0][1]?.signal;
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'client' } });
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '30')).toBe(true));
    await act(async () => finish({ items: [{ id: 888, shortName: 'Прежняя организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 1, page: 1, pageSize: 50 }));
    expect(signal?.aborted).toBe(true);
    expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '888')).toBe(false);
    expect(screen.getByLabelText('Тип участника')).toHaveProperty('value', 'client');
    expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '30')).toBe(true);
  });

  it('participant directory: an old search response cannot replace a newer query', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getLegalEntitiesPaged>>) => void;
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => query?.search === 'прежний' ? new Promise(resolve => { finish = resolve; }) : ({ items: [{ id: 250, shortName: 'Новая организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 1, page: query?.page ?? 1, pageSize: 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    fireEvent.change(screen.getByLabelText('Поиск участника дела'), { target: { value: 'прежний' } });
    await waitFor(() => expect(finish).toBeTypeOf('function'));
    const signal = vi.mocked(apiService.getLegalEntitiesPaged).mock.calls.at(-1)?.[1]?.signal;
    fireEvent.change(screen.getByLabelText('Поиск участника дела'), { target: { value: 'новый' } });
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenLastCalledWith({ search: 'новый', page: 1, pageSize: 50 }, { signal: expect.any(AbortSignal) }));
    await act(async () => finish({ items: [{ id: 888, shortName: 'Прежняя организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 1, page: 1, pageSize: 50 }));
    expect(signal?.aborted).toBe(true);
    expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '888')).toBe(false);
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '250')).toBe(true));
  });

  it('participant directory: current server matches are retained for fields absent from the option label', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await choosePageParticipantSource();
    fireEvent.change(screen.getByLabelText('Поиск участника дела'), { target: { value: 'адрес из серверного поиска' } });
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenLastCalledWith({ search: 'адрес из серверного поиска', page: 1, pageSize: 50 }, { signal: expect.any(AbortSignal) }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '25')).toBe(true));
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '');
  });

  it('participant directory: a source failure does not prevent switching to another permitted source', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockRejectedValue(new Error('Hidden source failure'));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' });
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'client' } });
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '30')).toBe(true));
    expect(screen.queryByRole('button', { name: 'Повторить загрузку справочника участников' })).toBeNull();
  });

  it('participant directory: failed next-page retry preserves the previous selection and draft', async () => {
    let failed = false;
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => {
      if (query?.page === 2 && !failed) { failed = true; throw new Error('Hidden page failure'); }
      return { items: [{ id: query?.page === 2 ? 251 : 25, shortName: 'Организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 51, page: query?.page ?? 1, pageSize: 50 };
    });
    vi.mocked(apiService.addCaseRelatedParty).mockReturnValue(new Promise(() => {}));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await choosePageParticipantSource();
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: 'Ответчик' } });
    fireEvent.change(screen.getByLabelText('Основание связи участника'), { target: { value: 'Сохранённое основание' } });
    fireEvent.click(screen.getByRole('button', { name: 'Ещё записи справочника участников' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '251')).toBe(true));
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '25');
    expect(screen.getByLabelText('Роль участника')).toHaveProperty('value', 'Ответчик');
    expect(screen.getByLabelText('Основание связи участника')).toHaveProperty('value', 'Сохранённое основание');
    fireEvent.click(screen.getByRole('button', { name: /^Добавить$/u }));
    expect(apiService.addCaseRelatedParty).toHaveBeenCalledWith(15, { sourceType: 'legalEntity', sourceId: 25, role: 'Ответчик', notes: 'Сохранённое основание' });
  });

  it('participant directory: duplicate next-page records are merged without dropping new choices', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => ({ items: (query?.page === 2 ? [25, 251] : [25]).map(id => ({ id, shortName: `Организация ${id}`, clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' })), total: 51, page: query?.page ?? 1, pageSize: 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await choosePageParticipantSource();
    fireEvent.click(screen.getByRole('button', { name: 'Ещё записи справочника участников' }));
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).map(option => option.value)).toEqual(['', '25', '251']));
  });

  it.each(['total', 'page', 'size', 'id', 'duplicate'] as const)('participant directory: invalid %s result is an explicit refusal', async kind => {
    const item = { ...createCase().client!, id: kind === 'id' ? 0 : 31 };
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: kind === 'duplicate' ? [item, item] : [item], total: kind === 'total' ? -1 : 2, page: kind === 'page' ? 2 : 1, pageSize: kind === 'size' ? 200 : 50 });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' });
    expect(screen.getByRole('button', { name: /^Добавить$/u })).toHaveProperty('disabled', true);
    expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).map(option => option.value)).toEqual(['']);
  });

  it.each(['foreign', 'oversized'] as const)('participant directory: invalid external %s payload cannot provide a selection', async kind => {
    vi.mocked(apiService.getContacts).mockResolvedValue(kind === 'foreign' ? [{ ...directoryContact(44), sourceType: 'client' }] : Array.from({ length: 51 }, (_, index) => directoryContact(index + 1000)));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'externalContact' } });
    await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' });
    expect(screen.getByRole('button', { name: /^Добавить$/u })).toHaveProperty('disabled', true);
  });

  it('participant directory: double next-page click reserves only one page and excludes a cached add', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => query?.page === 2 ? new Promise(() => {}) : ({ items: [{ id: 25, shortName: 'Организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 101, page: 1, pageSize: 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    const add = await choosePageParticipantSource();
    const more = screen.getByRole('button', { name: 'Ещё записи справочника участников' });
    act(() => { fireEvent.click(more); fireEvent.click(more); fireEvent.click(add); });
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.getLegalEntitiesPaged).mock.calls.map(([query]) => query?.page)).toEqual([1, 2]);
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  it('participant directory: an immediate query change rejects the cached add and clears selection', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    const add = await choosePageParticipantSource();
    act(() => { fireEvent.change(screen.getByLabelText('Поиск участника дела'), { target: { value: 'новый поиск' } }); fireEvent.click(add); });
    expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '');
  });

  it.each(['retry', 'more'] as const)('participant directory: replaced login rejects old %s control before rerender', async control => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    if (control === 'retry') vi.mocked(apiService.getLegalEntitiesPaged).mockRejectedValue(new Error('Unavailable'));
    else vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [{ id: 25, shortName: 'Организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 51, page: 1, pageSize: 50 });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    const button = await screen.findByRole('button', { name: control === 'retry' ? 'Повторить загрузку справочника участников' : 'Ещё записи справочника участников' });
    const reads = vi.mocked(apiService.getLegalEntitiesPaged).mock.calls.length;
    session.replace('actor');
    fireEvent.click(button);
    expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads);
  });


  it('participant directory lifetime: revoking edit aborts a pending read and hides its late source', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getClientsV2>>) => void;
    vi.mocked(apiService.getClientsV2).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledOnce());
    const signal = vi.mocked(apiService.getClientsV2).mock.calls[0][1]?.signal;
    view.rerender(<CaseDetailPage {...props} canEdit={false} />);
    await act(async () => finish({ items: [{ ...createCase().client!, id: 31, name: 'Поздний источник' }], total: 1, page: 1, pageSize: 50 }));
    expect(signal?.aborted).toBe(true);
    expect(screen.queryByLabelText('Участник дела')).toBeNull();
    expect(screen.queryByText('Поздний источник')).toBeNull();
    expect(apiService.getClientsV2).toHaveBeenCalledOnce();
  });

  it('participant directory lifetime: token refresh retains the current query and avoids a duplicate read', async () => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    vi.mocked(apiService.getClientsV2).mockReturnValue(new Promise(() => {}));
    const props = { caseId: 15, onBack: vi.fn(), canEdit: true };
    const view = render(<CaseDetailPage {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledOnce());
    const signal = vi.mocked(apiService.getClientsV2).mock.calls[0][1]?.signal;
    session.refresh();
    view.rerender(<CaseDetailPage {...props} />);
    expect(signal?.aborted).toBe(false);
    expect(apiService.getClientsV2).toHaveBeenCalledOnce();
  });

  it('participant directory lifetime: rapid input sends only the final debounced search', async () => {
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledOnce());
    const search = screen.getByLabelText('Поиск участника дела');
    act(() => {
      fireEvent.change(search, { target: { value: 'a' } });
      fireEvent.change(search, { target: { value: 'ab' } });
      fireEvent.change(search, { target: { value: ' abc ' } });
    });
    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiService.getClientsV2).mock.calls.map(([query]) => query?.search)).toEqual([undefined, 'abc']);
  });


  it.each([401, 403])('participant directory denial baseline: HTTP %s on the next page clears private choices', async status => {
    let allowed = true;
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => {
      if (query?.page === 2 && allowed) { allowed = false; throw new Error(`HTTP ${status} Private directory denial`); }
      return { items: [{ id: 25, shortName: 'Закрытая организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 51, page: query?.page ?? 1, pageSize: 50 };
    });
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    await choosePageParticipantSource();
    fireEvent.change(screen.getByLabelText('Роль участника'), { target: { value: 'Представитель' } });
    fireEvent.click(screen.getByRole('button', { name: 'Ещё записи справочника участников' }));
    const retry = await screen.findByRole('button', { name: 'Повторить загрузку справочника участников' });
    expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).map(option => option.value)).toEqual(['']);
    expect(screen.getByLabelText('Участник дела')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Роль участника')).toHaveProperty('value', 'Представитель');
    fireEvent.click(retry);
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(3));
    expect(vi.mocked(apiService.getLegalEntitiesPaged).mock.calls.map(([query]) => query?.page)).toEqual([1, 2, 1]);
  });

  it('participant directory linked page: an excluded first portion still exposes the next available choices', async () => {
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([{ id: 61, clientCaseId: 15, sourceType: 'legalEntity', sourceId: 25, legalEntityId: 25, displayName: 'Связанная организация', role: 'Ответчик', createdAt: '2026-11-04T00:00:00Z' }]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation(async query => ({ items: [{ id: query?.page === 2 ? 251 : 25, shortName: 'Организация', clientsCount: 0, createdAt: '2026-11-04T00:00:00Z' }], total: 51, page: query?.page ?? 1, pageSize: 50 }));
    render(<CaseDetailPage caseId={15} onBack={vi.fn()} canEdit />);
    fireEvent.click(await screen.findByRole('button', { name: 'Участники' }));
    fireEvent.change(screen.getByLabelText('Тип участника'), { target: { value: 'legalEntity' } });
    const more = await screen.findByRole('button', { name: 'Ещё записи справочника участников' });
    expect((screen.getByLabelText('Участник дела') as HTMLSelectElement).options[0].textContent).toBe('В этой порции нет доступных записей');
    fireEvent.click(more);
    await waitFor(() => expect(Array.from((screen.getByLabelText('Участник дела') as HTMLSelectElement).options).some(option => option.value === '251')).toBe(true));
  });

});
