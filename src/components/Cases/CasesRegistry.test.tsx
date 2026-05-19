import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DialogProvider } from '../../contexts/DialogContext';
import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { CasesRegistry } from './CasesRegistry';

vi.mock('../../services/api', () => ({
  apiService: {
    getCasesV2: vi.fn(),
    getActResponsibles: vi.fn(),
    bulkUpdateCases: vi.fn(),
    createTaskJournalCaseAutomationTask: vi.fn(),
    runCaseCourtSync: vi.fn(),
    createCaseV2: vi.fn(),
    getCaseDocumentTemplates: vi.fn(),
    getClients: vi.fn(),
    getLegalEntities: vi.fn(),
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
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
    vi.mocked(apiService.getCasesV2).mockReset();
    vi.mocked(apiService.getCasesV2).mockResolvedValue({
      items: [createCase()],
      total: 1,
      page: 1,
      pageSize: 25,
    });
    vi.mocked(apiService.getActResponsibles).mockReset();
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([
      { id: 7, fullName: 'Кожеваткин Андрей Владимирович' },
      { id: 8, fullName: 'Диденко Анна Владимировна' },
    ]);
    vi.mocked(apiService.bulkUpdateCases).mockReset();
    vi.mocked(apiService.bulkUpdateCases).mockResolvedValue({ updated: 1, requested: 1 });
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
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([]);
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getClients).mockResolvedValue([createCase().client!]);
    vi.mocked(apiService.getLegalEntities).mockReset();
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([]);
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
