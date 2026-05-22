import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DialogProvider } from '../../contexts/DialogContext';
import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
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
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCasesV2: vi.fn(),
    exportCasesRegistryCsv: vi.fn(),
    getCasesRegistryPrintHtml: vi.fn(),
    getActResponsibles: vi.fn(),
    bulkUpdateCases: vi.fn(),
    getCaseAccessRequests: vi.fn(),
    resolveCaseAccessRequest: vi.fn(),
    createTaskJournalCaseAutomationTask: vi.fn(),
    runCaseCourtSync: vi.fn(),
    createCaseV2: vi.fn(),
    updateCaseV2: vi.fn(),
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
      legalEntities: { ...section },
      clients: { ...section },
      cases: { ...section, canExport: permissionsState.casesCanExport },
      accounts: { ...section },
      acts: { ...section },
      payments: { ...section },
      contracts: { ...section },
      dictionaries: { ...section },
      roles: { ...section },
      audit: { ...section },
    };
  },
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
    authState.isAdmin = true;
    permissionsState.casesCanExport = true;
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
    vi.mocked(apiService.createTaskJournalCaseAutomationTask).mockResolvedValue({ id: 501 });
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
    expect(apiService.getCasesV2).toHaveBeenCalledWith(expect.objectContaining({
      page: 1,
      pageSize: 25,
      sortBy: 'createdAt',
      sortDir: 'desc',
    }));
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
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
        responsibleUserId: 7,
      }));
    });
  });

  it('passes registry scope to the cases api', async () => {
    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getByRole('button', { name: 'Архив' }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(expect.objectContaining({
        scope: 'archive',
        status: undefined,
        page: 1,
      }));
    });
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
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
      }));
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
    expect(screen.queryByRole('button', { name: 'Печать' })).toBeNull();
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
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => {
      expect(apiService.bulkUpdateCases).toHaveBeenCalledWith({
        ids: [1],
        status: 'OnHold',
        responsibleUserId: 8,
        stage: 'Судебное производство',
        priority: 'urgent',
      });
    });
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
    const prompt = vi.spyOn(window, 'prompt').mockReturnValue('Активные Земцов');

    render(<CasesRegistry />);

    await screen.findByText('ЗЕМЦОВ - банкротство');
    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, клиенту, суду'), {
      target: { value: 'земцов' },
    });
    fireEvent.change(screen.getByDisplayValue('Все статусы'), {
      target: { value: 'Open' },
    });

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
      }));
    });

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить вид' }));

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
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(expect.objectContaining({
        search: 'земцов',
        status: 'Open',
        page: 1,
      }));
    });

    prompt.mockRestore();
  });

  it('runs row quick actions for task creation and case finance', async () => {
    const onOpenCaseFinance = vi.fn();

    render(<CasesRegistry onOpenCaseFinance={onOpenCaseFinance} />);

    await screen.findByText('ЗЕМЦОВ - банкротство');

    fireEvent.click(screen.getAllByLabelText('Создать задачу по делу')[0]);
    await waitFor(() => {
      expect(apiService.createTaskJournalCaseAutomationTask).toHaveBeenCalledWith(1);
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
