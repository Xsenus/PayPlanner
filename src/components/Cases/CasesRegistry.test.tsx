import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DialogProvider } from '../../contexts/DialogContext';
import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CasesRegistry } from './CasesRegistry';

const authState = vi.hoisted(() => ({
  user: {
    id: 7,
    fullName: 'Администратор',
    email: 'admin@example.com',
    role: { id: 1, name: 'admin' },
  },
  isAdmin: true,
}));

const permissionsState = vi.hoisted(() => ({
  casesCanExport: true,
  clientsCanView: true,
  legalEntitiesCanView: true,
  paymentsCanView: true,
}));

const dialogMock = vi.hoisted(() => ({
  prompt: vi.fn(),
}));

function createTaskJournalItem(overrides: Partial<TaskJournalItem> = {}): TaskJournalItem {
  return {
    id: 501,
    objectName: 'Дело',
    title: 'Проверить дело',
    description: '',
    assigneeName: 'Администратор',
    authorName: 'Система',
    dueAt: '2026-05-20T10:00:00Z',
    plannedDurationMinutes: 60,
    type: 'task',
    priority: 'normal',
    status: 'new',
    result: '',
    reminders: [],
    channels: [],
    tags: [],
    createdAt: '2026-05-19T10:00:00Z',
    updatedAt: '2026-05-19T10:00:00Z',
    ...overrides,
  };
}

vi.mock('../../services/api', () => ({
  apiService: {
    getCasesV2: vi.fn(),
    exportCasesRegistryCsv: vi.fn(),
    exportCasesRegistryExcel: vi.fn(),
    exportCasesRegistryPdf: vi.fn(),
    getCasesRegistryPrintHtml: vi.fn(),
    getActResponsibles: vi.fn(),
    bulkUpdateCases: vi.fn(),
    getCaseAccessRequests: vi.fn(),
    resolveCaseAccessRequest: vi.fn(),
    createTaskJournalCaseAutomationTask: vi.fn(),
    createTaskJournalItem: vi.fn(),
    runCaseCourtSync: vi.fn(),
    createCaseV2: vi.fn(),
    updateCaseV2: vi.fn(),
    updateCaseControl: vi.fn(),
    getCaseDocumentTemplates: vi.fn(),
    getCaseDocuments: vi.fn(),
    getTaskJournalItems: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getLegalEntityCaseFinanceSummaries: vi.fn(),
    markCaseActivity: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    getContacts: vi.fn(),
    getClients: vi.fn(),
    getLegalEntities: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: authState.user,
    isAdmin: () => authState.isAdmin,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => {
    const section = {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: true,
      viewScope: 'all',
    };

    return {
      calendar: { ...section, canViewAnalytics: true },
      tasks: { ...section },
      reports: { ...section },
      calculator: { ...section },
      legalEntities: { ...section, canView: permissionsState.legalEntitiesCanView },
      clients: { ...section, canView: permissionsState.clientsCanView },
      cases: { ...section, canExport: permissionsState.casesCanExport },
      accounts: { ...section },
      acts: { ...section },
      payments: { ...section, canView: permissionsState.paymentsCanView },
      contracts: { ...section },
      dictionaries: { ...section },
      roles: { ...section },
      audit: { ...section },
    };
  },
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 1,
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
    caseNumber: '2026-001',
    caseType: 'Банкротство',
    stage: 'Подготовка заявления',
    responsibleUserId: 7,
    responsibleUserName: 'Кожеваткин Андрей Владимирович',
    startDate: '2026-05-10',
    dueDate: '2026-06-15',
    feeAmount: 150000,
    incomeTotal: 50000,
    incomePaid: 35000,
    incomeOutstanding: 15000,
    incomeCount: 2,
    lastViewedAt: '2026-05-12T10:15:00Z',
    lastViewedByUserName: 'Кожеваткин Андрей Владимирович',
    activityConfirmedAt: '2026-05-12T10:20:00Z',
    activityConfirmedByUserName: 'Кожеваткин Андрей Владимирович',
    inactivityRemindersEnabled: true,
    priority: 'high',
    courtName: 'Арбитражный суд Самарской области',
    courtCaseNumber: 'А55-12345/2026',
    nextHearingAt: '2026-05-20T09:30:00Z',
    title: 'ЗЕМЦОВ - банкротство',
    description: 'Подготовить заявление',
    status: 'Open',
    createdAt: '2026-05-01T10:00:00Z',
    ...overrides,
  } as ClientCase;
}

describe('CasesRegistry', () => {
  beforeEach(() => {
    dialogMock.prompt.mockReset();
    dialogMock.prompt.mockResolvedValue('\u0410\u043a\u0442\u0438\u0432\u043d\u044b\u0435 \u0417\u0435\u043c\u0446\u043e\u0432');
    authState.isAdmin = true;
    permissionsState.casesCanExport = true;
    permissionsState.clientsCanView = true;
    permissionsState.legalEntitiesCanView = true;
    permissionsState.paymentsCanView = true;
    window.localStorage.removeItem('pp.cases_registry_views.v1');
    vi.mocked(apiService.getCasesV2).mockReset();
    vi.mocked(apiService.getCasesV2).mockResolvedValue({
      items: [createCase()],
      total: 1,
      page: 1,
      pageSize: 25,
    });
    vi.mocked(apiService.exportCasesRegistryCsv).mockReset();
    vi.mocked(apiService.exportCasesRegistryCsv).mockResolvedValue({
      blob: new Blob(['csv'], { type: 'text/csv' }),
      fileName: 'cases-registry.csv',
    });
    vi.mocked(apiService.exportCasesRegistryExcel).mockReset();
    vi.mocked(apiService.exportCasesRegistryExcel).mockResolvedValue({
      blob: new Blob(['xls'], { type: 'application/vnd.ms-excel' }),
      fileName: 'cases-registry.xls',
    });
    vi.mocked(apiService.exportCasesRegistryPdf).mockReset();
    vi.mocked(apiService.exportCasesRegistryPdf).mockResolvedValue({
      blob: new Blob(['pdf'], { type: 'application/pdf' }),
      fileName: 'cases-registry.pdf',
    });
    vi.mocked(apiService.getCasesRegistryPrintHtml).mockReset();
    vi.mocked(apiService.getCasesRegistryPrintHtml).mockResolvedValue('<html><body>cases</body></html>');
    vi.mocked(apiService.getActResponsibles).mockReset();
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([
      { id: 7, fullName: 'Кожеваткин Андрей Владимирович' },
      { id: 8, fullName: 'Диденко Анна Владимировна' },
    ]);
    vi.mocked(apiService.bulkUpdateCases).mockReset();
    vi.mocked(apiService.bulkUpdateCases).mockResolvedValue({ updated: 1, requested: 1 });
    vi.mocked(apiService.getCaseAccessRequests).mockReset();
    vi.mocked(apiService.getCaseAccessRequests).mockResolvedValue([]);
    vi.mocked(apiService.resolveCaseAccessRequest).mockReset();
    vi.mocked(apiService.resolveCaseAccessRequest).mockResolvedValue({
      clientCaseId: 1,
      granted: true,
      resolvedAt: '2026-05-19T10:00:00Z',
      resolvedByUserName: 'Администратор',
      isHiddenFromExecutor: false,
    });
    vi.mocked(apiService.createTaskJournalCaseAutomationTask).mockReset();
    vi.mocked(apiService.createTaskJournalCaseAutomationTask).mockResolvedValue(createTaskJournalItem());
    vi.mocked(apiService.createTaskJournalItem).mockReset();
    vi.mocked(apiService.createTaskJournalItem).mockResolvedValue(createTaskJournalItem());
    vi.mocked(apiService.runCaseCourtSync).mockReset();
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValue({
      clientCaseId: 1,
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
    vi.mocked(apiService.createCaseV2).mockReset();
    vi.mocked(apiService.createCaseV2).mockResolvedValue(createCase({ id: 99, title: 'Новое дело' }));
    vi.mocked(apiService.updateCaseV2).mockReset();
    vi.mocked(apiService.updateCaseV2).mockImplementation(async (id, payload) => createCase({ ...payload, id }));
    vi.mocked(apiService.updateCaseControl).mockReset();
    vi.mocked(apiService.updateCaseControl).mockResolvedValue(createCase({ isUnderControl: true }));
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([]);
    vi.mocked(apiService.getCaseDocuments).mockReset();
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([]);
    vi.mocked(apiService.getTaskJournalItems).mockReset();
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 10 });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
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
    vi.mocked(apiService.getCaseRelatedParties).mockReset();
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([]);
    vi.mocked(apiService.getContacts).mockReset();
    vi.mocked(apiService.getContacts).mockResolvedValue([]);
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getClients).mockResolvedValue([createCase().client!]);
    vi.mocked(apiService.getLegalEntities).mockReset();
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100 });
    vi.mocked(apiService.getUserSetting).mockReset();
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('not found'));
    vi.mocked(apiService.upsertUserSetting).mockReset();
    vi.mocked(apiService.upsertUserSetting).mockResolvedValue({ key: 'cases_registry_table_settings_v1', value: '{}' });
  });

  it('wraps scope controls and saved views without expanding tablet and mobile pages', async () => {
    render(<CasesRegistry />);

    expect(await screen.findByText('Реестр дел')).toBeTruthy();

    const scopeBar = screen.getByRole('button', { name: 'Все' }).parentElement;
    expect(scopeBar?.className).toContain('w-full');
    expect(scopeBar?.className).toContain('flex-wrap');
    expect(scopeBar?.className).toContain('sm:w-fit');

    const savedViewBar = screen.getByRole('button', { name: 'Сохранить вид' }).parentElement;
    const filterHeader = savedViewBar?.parentElement;
    expect(filterHeader?.className).toContain('xl:flex-row');
    expect(filterHeader?.className).not.toContain('lg:flex-row');
  });

  it('loads the registry and opens the related client case', async () => {
    const onOpenClient = vi.fn();

    render(<CasesRegistry onOpenClient={onOpenClient} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(screen.getAllByText('Земцов Виктор Александрович').length).toBeGreaterThan(0);
    expect(screen.getByText('Подготовка заявления')).toBeTruthy();
    expect(screen.getByText('Арбитражный суд Самарской области')).toBeTruthy();
    expect(screen.getAllByText('Кожеваткин Андрей Владимирович').length).toBeGreaterThan(0);
    expect(screen.getByText(/35\s*000\s*₽/u)).toBeTruthy();
    expect(screen.getAllByText(/15\s*000\s*₽/u).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: '2026-001' }));

    expect(onOpenClient).toHaveBeenCalledWith(12, 1);
    expect(apiService.getCasesV2).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 25,
        sortBy: 'createdAt',
        sortDir: 'desc',
      }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('shows the last activity, next control and exclusion reason in the registry', async () => {
    const futureControlAt = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString();
    const dueControlAt = new Date(Date.now() - 60 * 60 * 1000).toISOString();
    vi.mocked(apiService.getCasesV2).mockResolvedValue({
      items: [
        createCase({
          id: 1,
          lastMeaningfulActivityAt: '2026-05-12T10:20:00Z',
          nextInactivityControlAt: futureControlAt,
        }),
        createCase({
          id: 2,
          caseNumber: '2026-002',
          title: 'Дело без автоконтроля',
          inactivityRemindersEnabled: false,
          inactivityControlExclusionReason: 'Ожидаем решение клиента',
          nextInactivityControlAt: null,
        }),
        createCase({
          id: 3,
          caseNumber: '2026-003',
          title: 'Дело с активной задачей',
          inactivityControlExclusionReason: 'Есть активная задача',
          nextInactivityControlAt: null,
        }),
        createCase({
          id: 4,
          caseNumber: '2026-004',
          title: 'Дело с наступившим контролем',
          nextInactivityControlAt: dueControlAt,
        }),
      ],
      total: 4,
      page: 1,
      pageSize: 25,
    });

    render(<CasesRegistry />);

    expect((await screen.findAllByText(/Последняя:/u)).length).toBe(4);
    expect(screen.getByText('Проверка запланирована').className).toContain('bg-cyan-50');
    expect(screen.getByText('Пора проверить').className).toContain('bg-amber-50');
    expect(screen.getByText('Контроль выключен')).toBeTruthy();
    expect(screen.getByText('Ожидаем решение клиента')).toBeTruthy();
    expect(screen.getByText('Есть активная задача').className).toContain('bg-emerald-50');
    expect(screen.getByText('Отдельная контрольная задача пока не требуется')).toBeTruthy();
  });

  it('lets the server use the configured inactivity period instead of sending a fixed threshold', async () => {
    render(<CasesRegistry />);
    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.change(screen.getByDisplayValue('Любая активность'), {
      target: { value: 'inactive' },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ activity: 'inactive' }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });
    const lastParams = vi.mocked(apiService.getCasesV2).mock.calls.at(-1)?.[0];
    expect(lastParams).not.toHaveProperty('inactiveDays');
  });

  it('keeps case owners closed when clients and legal entities are hidden', async () => {
    authState.isAdmin = false;
    permissionsState.clientsCanView = false;
    permissionsState.legalEntitiesCanView = false;
    const onOpenClient = vi.fn();
    const onOpenLegalEntity = vi.fn();

    render(<CasesRegistry onOpenClient={onOpenClient} onOpenLegalEntity={onOpenLegalEntity} />);

    expect(await screen.findByText('ЗЕМЦОВ - банкротство')).toBeTruthy();
    expect(screen.getAllByText('Земцов Виктор Александрович').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: '2026-001' }));

    expect(onOpenClient).not.toHaveBeenCalled();
    expect(onOpenLegalEntity).not.toHaveBeenCalled();

    const createButton = screen.getByRole('button', { name: 'Создать дело' }) as HTMLButtonElement;
    expect(createButton.disabled).toBe(true);
    fireEvent.click(createButton);

    expect(screen.queryByText('Выбор владельца дела')).toBeNull();
    expect(apiService.getClients).not.toHaveBeenCalled();
    expect(apiService.getLegalEntities).not.toHaveBeenCalled();
  });

  it('passes filters to the cases api', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, клиенту, суду'), {
      target: { value: 'земцов' },
    });
    fireEvent.change(screen.getByDisplayValue('Все статусы'), {
      target: { value: 'Open' },
    });
    fireEvent.change(screen.getByDisplayValue('Все ответственные'), {
      target: { value: '7' },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({
          search: 'земцов',
          status: 'Open',
          responsibleUserId: 7,
        }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });
  });

  it('combines rapid registry search input and does not reload auxiliary data', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    vi.mocked(apiService.getCasesV2).mockClear();
    vi.mocked(apiService.getActResponsibles).mockClear();
    vi.mocked(apiService.getCaseAccessRequests).mockClear();

    const searchInput = screen.getByPlaceholderText('Поиск по делу, клиенту, суду');
    fireEvent.change(searchInput, { target: { value: 'зе' } });
    fireEvent.change(searchInput, { target: { value: 'земц' } });
    fireEvent.change(searchInput, { target: { value: 'земцов' } });

    expect(apiService.getCasesV2).not.toHaveBeenCalled();
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenCalledTimes(1));
    expect(apiService.getCasesV2).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'земцов', page: 1 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(apiService.getActResponsibles).not.toHaveBeenCalled();
    expect(apiService.getCaseAccessRequests).not.toHaveBeenCalled();
  });

  it('aborts the pending access requests call when the registry closes', async () => {
    let requestSignal: AbortSignal | undefined;
    vi.mocked(apiService.getCaseAccessRequests).mockImplementation((options) => {
      requestSignal = options?.signal;
      return new Promise(() => undefined);
    });

    const { unmount } = render(<CasesRegistry />);

    await waitFor(() => expect(requestSignal).toBeInstanceOf(AbortSignal));
    expect(requestSignal?.aborted).toBe(false);

    unmount();

    expect(requestSignal?.aborted).toBe(true);
  });

  it('aborts an obsolete active registry request', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    vi.mocked(apiService.getCasesV2).mockClear();
    const signals: AbortSignal[] = [];
    const resolvePending: Array<(result: { items: ClientCase[]; total: number; page: number; pageSize: number }) => void> = [];
    vi.mocked(apiService.getCasesV2).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvePending.push(resolve));
    });

    const searchInput = screen.getByPlaceholderText('Поиск по делу, клиенту, суду');
    fireEvent.change(searchInput, { target: { value: 'иван' } });
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(searchInput, { target: { value: 'иванов' } });

    await waitFor(() => expect(signals[0]?.aborted).toBe(true));
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenCalledTimes(2));
    resolvePending.forEach((resolve) => resolve({ items: [], total: 0, page: 1, pageSize: 25 }));
  });

  it('passes registry scope to the cases api', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getByRole('button', { name: 'Архив' }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ scope: 'archive', status: undefined, page: 1 }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });
  });

  it('passes the manual control scope to the cases api', async () => {
    render(<CasesRegistry />);

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenCalled();
    });

    fireEvent.click(screen.getByRole('button', { name: 'На контроле' }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ scope: 'control', status: undefined, page: 1 }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });
  });

  it('marks a case for manual control from the row actions', async () => {
    render(<CasesRegistry />);

    const controlButtons = await screen.findAllByLabelText('Поставить дело на контроль');
    fireEvent.click(controlButtons[0]);

    await waitFor(() => {
      expect(apiService.updateCaseControl).toHaveBeenCalledWith(1, true);
    });
    expect(await screen.findByText('Дело поставлено на контроль.')).toBeTruthy();
  });

  it('exports the filtered registry through the backend export endpoint', async () => {
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:cases-registry');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, клиенту, суду'), {
      target: { value: 'земцов' },
    });
    fireEvent.change(screen.getByDisplayValue('Все статусы'), {
      target: { value: 'Open' },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ search: 'земцов', status: 'Open' }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });

    fireEvent.click(screen.getByRole('button', { name: /^CSV$/u }));

    await waitFor(() => {
      expect(apiService.exportCasesRegistryCsv).toHaveBeenCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
        sortBy: 'createdAt',
        sortDir: 'desc',
      }));
    });
    expect(createObjectUrl).toHaveBeenCalled();
    expect(anchorClick).toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: /^Excel$/u }));
    await waitFor(() => {
      expect(apiService.exportCasesRegistryExcel).toHaveBeenCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
        sortBy: 'createdAt',
        sortDir: 'desc',
      }));
    });

    fireEvent.click(screen.getByRole('button', { name: /^PDF$/u }));
    await waitFor(() => {
      expect(apiService.exportCasesRegistryPdf).toHaveBeenCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
        sortBy: 'createdAt',
        sortDir: 'desc',
      }));
    });

    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
    anchorClick.mockRestore();
  });

  it('opens the filtered registry print form through the backend endpoint', async () => {
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:cases-print');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: 'Печать' }));

    await waitFor(() => {
      expect(apiService.getCasesRegistryPrintHtml).toHaveBeenCalledWith(expect.objectContaining({
        sortBy: 'createdAt',
        sortDir: 'desc',
      }));
    });
    expect(createObjectUrl).toHaveBeenCalled();
    expect(open).toHaveBeenCalledWith('blob:cases-print', '_blank', 'noopener,noreferrer');

    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
    open.mockRestore();
  });

  it('hides registry export actions when the role cannot export cases', async () => {
    authState.isAdmin = false;
    permissionsState.casesCanExport = false;

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    expect(screen.queryByRole('button', { name: /^CSV$/u })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Excel$/u })).toBeNull();
    expect(screen.queryByRole('button', { name: /^PDF$/u })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Печать' })).toBeNull();
  });

  it('hides case finance data and actions when the role cannot view payments', async () => {
    authState.isAdmin = false;
    permissionsState.paymentsCanView = false;
    const onOpenCaseFinance = vi.fn();

    render(<CasesRegistry onOpenCaseFinance={onOpenCaseFinance} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    expect(screen.queryByText('Гонорар')).toBeNull();
    expect(screen.queryByText('Счета')).toBeNull();
    expect(screen.queryByText('Оплачено')).toBeNull();
    expect(screen.queryByText('Долг')).toBeNull();
    expect(screen.queryByPlaceholderText('Гонорар от')).toBeNull();
    expect(screen.queryByPlaceholderText('Гонорар до')).toBeNull();
    expect(screen.queryByLabelText('Открыть финансы дела')).toBeNull();
    expect(screen.queryByText(/150\s*000\s*₽/u)).toBeNull();
    expect(screen.queryByText(/35\s*000\s*₽/u)).toBeNull();
    expect(screen.queryByText(/15\s*000\s*₽/u)).toBeNull();

    expect(apiService.getCasesV2).toHaveBeenCalledWith(
      expect.objectContaining({ sortBy: 'createdAt', minFee: undefined, maxFee: undefined }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(onOpenCaseFinance).not.toHaveBeenCalled();
  });

  it('opens the standalone case card when the registry callback is provided', async () => {
    const onOpenCase = vi.fn();

    render(<CasesRegistry onOpenCase={onOpenCase} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getByRole('button', { name: '2026-001' }));

    expect(onOpenCase).toHaveBeenCalledWith(1);
  });

  it('runs a bulk status and responsible update for selected cases', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getAllByLabelText('Выбрать дело 2026-001')[0]);
    fireEvent.change(screen.getByDisplayValue('Не менять статус'), {
      target: { value: 'OnHold' },
    });
    fireEvent.change(screen.getByDisplayValue('Не менять ответственного'), {
      target: { value: '8' },
    });
    fireEvent.change(screen.getByLabelText('Массово сменить стадию'), {
      target: { value: 'Судебное производство' },
    });
    fireEvent.change(screen.getByLabelText('Массово сменить приоритет'), {
      target: { value: 'urgent' },
    });
    fireEvent.change(screen.getByLabelText('Массово изменить контроль'), {
      target: { value: 'on' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({
        ids: [1],
        status: 'OnHold',
        responsibleUserId: 8,
        stage: 'Судебное производство',
        priority: 'urgent',
        isUnderControl: true,
      });
    });
    expect(await screen.findByText('Массовое изменение применено: обновлено дел 1.')).toBeTruthy();
  });

  it('archives selected cases through the bulk actions panel', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getAllByLabelText('Выбрать дело 2026-001')[0]);
    fireEvent.click(screen.getAllByRole('button', { name: 'В архив' })[0]);

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({
        ids: [1],
        status: 'Closed',
      });
    });
  });

  it('shows pending hidden case access requests and grants access', async () => {
    const onOpenCase = vi.fn();
    vi.mocked(apiService.getCaseAccessRequests).mockResolvedValueOnce([
      {
        clientCaseId: 15,
        caseNumber: 'CASE-15',
        title: 'Скрытое дело',
        ownerName: 'Земцов Виктор Александрович',
        responsibleUserId: 7,
        responsibleName: 'Кожеваткин Андрей Владимирович',
        requestedAt: '2026-05-19T09:00:00Z',
        requestedByUserId: 8,
        requestedByUserName: 'Диденко Анна Владимировна',
        reason: 'Нужно проверить срок.',
        isHiddenFromExecutor: true,
      },
    ]);

    render(<CasesRegistry onOpenCase={onOpenCase} />);

    await screen.findByText('Запросы доступа к скрытым делам');
    expect(screen.getByText('Нужно проверить срок.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /№ CASE-15/u }));
    expect(onOpenCase).toHaveBeenCalledWith(15);

    fireEvent.click(screen.getByRole('button', { name: 'Разрешить' }));

    await waitFor(() => {
      expect(apiService.resolveCaseAccessRequest).toHaveBeenCalledWith(15, { grant: true });
    });
    expect(await screen.findByText('Доступ к делу «Скрытое дело» открыт.')).toBeTruthy();
  });

  it('can deny a hidden case access request', async () => {
    vi.mocked(apiService.getCaseAccessRequests).mockResolvedValueOnce([
      {
        clientCaseId: 16,
        caseNumber: 'CASE-16',
        title: 'Дело на проверке',
        ownerName: 'Земцов Виктор Александрович',
        responsibleUserId: 7,
        responsibleName: 'Кожеваткин Андрей Владимирович',
        requestedAt: '2026-05-19T09:00:00Z',
        requestedByUserId: 8,
        requestedByUserName: 'Диденко Анна Владимировна',
        reason: '',
        isHiddenFromExecutor: true,
      },
    ]);

    render(<CasesRegistry />);

    await screen.findByText(/Дело на проверке/u);
    fireEvent.click(screen.getByRole('button', { name: 'Отклонить' }));

    await waitFor(() => {
      expect(apiService.resolveCaseAccessRequest).toHaveBeenCalledWith(16, { grant: false });
    });
  });

  it('exports only selected cases from the bulk actions panel', async () => {
    let exportedBlob: Blob | null = null;
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      exportedBlob = blob as Blob;
      return 'blob:cases-selected';
    });
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    vi.mocked(apiService.getCasesV2).mockResolvedValueOnce({
      items: [
        createCase(),
        createCase({ id: 2, caseNumber: '2026-002', title: 'ИВАНОВ - спор', client: { ...createCase().client!, id: 13, name: 'Иванов Иван Иванович' } }),
      ],
      total: 2,
      page: 1,
      pageSize: 25,
    });

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.click(screen.getAllByLabelText('Выбрать дело 2026-001')[0]);
    fireEvent.click(screen.getByRole('button', { name: /CSV выбранных/u }));

    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(anchorClick).toHaveBeenCalledTimes(1);
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:cases-selected');
    expect(exportedBlob).not.toBeNull();

    const text = await exportedBlob!.text();
    expect(text).toContain('2026-001');
    expect(text).toContain('ЗЕМЦОВ - банкротство');
    expect(text).not.toContain('2026-002');
    expect(text).not.toContain('ИВАНОВ - спор');

    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
    anchorClick.mockRestore();
  });

  it('allows the user to hide columns and persists the registry preset', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('cases_registry_table_settings_v1');
    });
    vi.mocked(apiService.upsertUserSetting).mockClear();

    fireEvent.click(screen.getByRole('button', { name: 'Колонки' }));
    fireEvent.click(screen.getByLabelText('Показать колонку Суд'));

    expect(screen.queryByRole('columnheader', { name: /Суд/u })).toBeNull();

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 700));
    });

    expect(apiService.upsertUserSetting).toHaveBeenCalledWith(
      'cases_registry_table_settings_v1',
      expect.stringContaining('"visibleColumns"'),
    );
  });

  it('saves and applies registry filter views', async () => {

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, клиенту, суду'), {
      target: { value: 'земцов' },
    });
    fireEvent.change(screen.getByDisplayValue('Все статусы'), {
      target: { value: 'Open' },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ search: 'земцов', status: 'Open' }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить вид' }));

    await waitFor(() => {
      expect(window.localStorage.getItem('pp.cases_registry_views.v1')).not.toBeNull();
    });
    const savedRaw = window.localStorage.getItem('pp.cases_registry_views.v1');
    expect(savedRaw).not.toBeNull();
    const savedViews = JSON.parse(savedRaw!);
    expect(savedViews[0]).toEqual(expect.objectContaining({
      name: 'Активные Земцов',
    }));
    expect(savedViews[0].filters).toEqual(expect.objectContaining({
      search: 'земцов',
      status: 'Open',
      sortBy: 'createdAt',
      sortDir: 'desc',
    }));

    fireEvent.click(screen.getByRole('button', { name: 'Сбросить' }));
    fireEvent.change(screen.getByLabelText('Сохраненные представления'), {
      target: { value: savedViews[0].id },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ search: 'земцов', status: 'Open', page: 1 }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Удалить сохраненное представление' }));

    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.cases_registry_views.v1') ?? '[]')).toHaveLength(0);
    });
    expect(screen.getByText('Представление «Активные Земцов» удалено.')).toBeTruthy();
  });

  it('runs row quick actions for task creation and case finance', async () => {
    const onOpenCaseFinance = vi.fn();

    render(<CasesRegistry onOpenCaseFinance={onOpenCaseFinance} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getAllByLabelText('Создать задачу по делу')[0]);
    await waitFor(() => {
      expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: 1,
        clientId: 12,
        title: expect.stringContaining('ЗЕМЦОВ - банкротство'),
        type: 'task',
      }));
      expect(apiService.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
    });

    fireEvent.click(screen.getAllByLabelText('Проверить суд по делу')[0]);
    await waitFor(() => {
      expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(1);
    });

    fireEvent.click(screen.getAllByLabelText('Открыть финансы дела')[0]);
    expect(onOpenCaseFinance).toHaveBeenCalledWith(expect.objectContaining({ id: 1, caseNumber: '2026-001' }));
  });

  it('opens a right-click action menu for a registry row and edits the case', async () => {
    render(
      <DialogProvider>
        <CasesRegistry />
      </DialogProvider>,
    );

    await screen.findByText('ЗЕМЦОВ - банкротство');

    const row = screen.getAllByRole('button', { name: '2026-001' })[0].closest('tr');
    expect(row).toBeTruthy();

    fireEvent.contextMenu(row as HTMLElement, { clientX: 120, clientY: 160 });

    expect(screen.getByRole('menu', { name: 'Действия по делу 2026-001' })).toBeTruthy();
    expect(screen.getByRole('menuitem', { name: 'Открыть карточку' })).toBeTruthy();

    fireEvent.click(screen.getByRole('menuitem', { name: 'Редактировать дело' }));

    const titleInput = await screen.findByLabelText('Название дела');
    fireEvent.change(titleInput, { target: { value: 'ЗЕМЦОВ - обновленное дело' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Сохранить' }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(apiService.updateCaseV2).toHaveBeenCalledWith(
        1,
        expect.objectContaining({
          title: 'ЗЕМЦОВ - обновленное дело',
          clientId: 12,
          status: 'Open',
        }),
      );
    });
  });

  it('renders a compact mobile case list with the key fields', async () => {
    render(<CasesRegistry />);

    await screen.findByText('Краткий список дел');

    expect(screen.getByText(/2026-001 · ЗЕМЦОВ - банкротство/u)).toBeTruthy();
    expect(screen.getByText('Ближайший срок')).toBeTruthy();
    expect(screen.getByText('Индикатор риска')).toBeTruthy();
  });

  it('provides a mobile filter drawer toggle', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getByRole('button', { name: /Показать фильтры/u }));
    expect(screen.getByRole('button', { name: /Скрыть фильтры/u })).toBeTruthy();
  });

  it('creates a case from the registry after choosing an owner', async () => {
    render(
      <DialogProvider>
        <CasesRegistry />
      </DialogProvider>,
    );

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenCalled();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Создать дело' }));
    expect(await screen.findByText('Выбор владельца дела')).toBeTruthy();

    fireEvent.click(await screen.findByRole('button', { name: 'Выбрать' }));

    const titleInput = await screen.findByLabelText('Название дела');
    fireEvent.change(titleInput, { target: { value: 'ЗЕМЦОВ - новое производство' } });
    fireEvent.change(screen.getByLabelText('Ответственный'), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Сумма гонорара'), { target: { value: '25000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(apiService.createCaseV2).toHaveBeenCalledWith(expect.objectContaining({
        clientId: 12,
        legalEntityId: null,
        title: 'ЗЕМЦОВ - новое производство',
        responsibleUserId: 7,
        feeAmount: 25000,
        status: 'Open',
        priority: 'normal',
      }));
    });
  });
});
