import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SalesRequestsPage } from './SalesRequestsPage';
import { apiService } from '../../services/api';
import type { SalesRequestItem, SalesRequestStatusItem } from '../../types';

const confirm = vi.fn().mockResolvedValue(true);
const toast = { success: vi.fn(), error: vi.fn(), info: vi.fn() };

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toast,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm, prompt: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getSalesRequestStatuses: vi.fn(),
    getSalesRequests: vi.fn(),
    getSalesRequest: vi.fn(),
    getTaskJournalAssignees: vi.fn(),
    searchSalesRequestClients: vi.fn(),
    getSalesRequestClientCases: vi.fn(),
    updateSalesRequest: vi.fn(),
    createSalesRequest: vi.fn(),
    transitionSalesRequest: vi.fn(),
    createSalesRequestTask: vi.fn(),
    getSalesRequestReport: vi.fn(),
    replaceSalesRequestRules: vi.fn(),
    saveSalesRequestStatus: vi.fn(),
  },
}));

const statuses: SalesRequestStatusItem[] = [
  { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [] },
  { id: 2, key: 'paid', title: 'Оплачено', sortOrder: 2, finalKind: 'Success', rules: [] },
  { id: 3, key: 'abandoned', title: 'Брошен', sortOrder: 3, finalKind: 'Failure', rules: [] },
];

const request = (overrides: Partial<SalesRequestItem> = {}): SalesRequestItem => ({
  id: 7,
  createdAt: '2026-10-10T09:00:00Z',
  source: 'Avito',
  clientId: 3,
  clientName: 'Иванов Иван',
  clientPhone: '+7 900 111-22-33',
  linkedCaseWasClosed: false,
  type: 'New',
  summary: 'Спор с застройщиком',
  statusId: 1,
  statusEnteredAt: '2026-10-10T09:00:00Z',
  responsibleUserId: 1,
  finalKind: 'None',
  version: 0,
  ...overrides,
});

const detail = (item = request()) => ({ request: item, tasks: [], events: [] });

describe('SalesRequestsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    confirm.mockResolvedValue(true);
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue(statuses);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 1, name: 'Аня', email: 'a@x' }]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 1, page: 1, pageSize: 50, items: [request()] });
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail());
  });

  it('lists requests with status and responsible names', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    expect(await screen.findByText('Иванов Иван')).toBeTruthy();
    expect((await screen.findAllByText('Аня')).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Отчёт за месяц' })).toBeNull();
  });

  it('opens the next page and resets pagination when filtering', async () => {
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 51, page: 1, pageSize: 50, items: [request()] });
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Следующие заявки' }));
    await waitFor(() => expect(apiService.getSalesRequests).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 })));
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Источник' }), 'Call');
    await waitFor(() => expect(apiService.getSalesRequests).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, source: 'Call' })));
  });

  it('opens the request from a task link', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);

    expect(await screen.findByTestId('sales-request-detail')).toBeTruthy();
    expect(apiService.getSalesRequest).toHaveBeenCalledWith(7);
  });

  it('does not overwrite a request changed by someone else and reloads on demand', async () => {
    vi.mocked(apiService.updateSalesRequest).mockRejectedValue(new Error('Заявку уже изменили, обновите карточку.'));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.type(screen.getByLabelText('Суть запроса (правка)'), ' ещё');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правки' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Ваши правки не сохранены');

    vi.mocked(apiService.getSalesRequest).mockClear();
    await userEvent.click(screen.getByRole('button', { name: 'Загрузить актуальную карточку' }));
    await waitFor(() => expect(apiService.getSalesRequest).toHaveBeenCalledWith(7));
    await waitFor(() => expect(screen.queryByLabelText('Суть запроса (правка)')).toBeNull());
  });

  it('hides creation without the create right', async () => {
    render(<SalesRequestsPage allowCreate={false} allowEdit={false} allowAnalytics={false} />);

    await screen.findByText('Иванов Иван');
    expect(screen.queryByRole('button', { name: 'Новая заявка' })).toBeNull();
  });

  it('creates a request for a found client and offers existing cases', async () => {
    vi.mocked(apiService.searchSalesRequestClients).mockResolvedValue([
      { id: 3, name: 'Иванов Иван', phone: '+7 900', email: '', casesCount: 21 },
    ]);
    vi.mocked(apiService.getSalesRequestClientCases)
      .mockResolvedValueOnce({ items: [{ id: 87, caseNumber: '87', title: 'Старое дело', status: 'Closed', lawyerName: 'Юрист', contractMode: 'Fixed' }], hasMore: true })
      .mockResolvedValueOnce({ items: [{ id: 88, caseNumber: '88', title: 'Другое дело', status: 'Open', contractMode: 'None' }], hasMore: false });
    vi.mocked(apiService.createSalesRequest).mockResolvedValue(request({ id: 8 }));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Новая заявка' }));
    await userEvent.type(screen.getByLabelText('Поиск клиента'), 'Иванов');
    await userEvent.click(await screen.findByRole('button', { name: /Иванов Иван/ }));
    expect(await screen.findByTestId('sales-request-cases')).toBeTruthy();
    await userEvent.click(await screen.findByRole('button', { name: 'Показать ещё дела' }));
    expect(await screen.findByLabelText(/Другое дело/)).toBeTruthy();
    expect(apiService.getSalesRequestClientCases).toHaveBeenLastCalledWith(3, 1, 20);
    expect(screen.queryByRole('button', { name: 'Показать ещё дела' })).toBeNull();
    await userEvent.click(screen.getByLabelText(/Старое дело/));
    await userEvent.selectOptions(screen.getByLabelText('Источник заявки'), 'Call');
    await userEvent.type(screen.getByLabelText('Суть запроса'), 'Нужен дубликат акта');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() =>
      expect(apiService.createSalesRequest).toHaveBeenCalledWith(
        expect.objectContaining({ source: 'Call', clientId: 3, clientCaseId: 87, summary: 'Нужен дубликат акта' }),
      ),
    );
  });

  it('requires a payment id before moving to the success status', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByText('Иванов Иван'));
    const paid = await screen.findByRole('button', { name: 'Оплачено' });
    expect((paid as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(screen.getByLabelText('Номер платежа'), '55');
    expect((paid as HTMLButtonElement).disabled).toBe(false);
    vi.mocked(apiService.transitionSalesRequest).mockResolvedValue(request({ statusId: 2, finalKind: 'Success', version: 1 }));
    await userEvent.click(paid);

    await waitFor(() =>
      expect(apiService.transitionSalesRequest).toHaveBeenCalledWith(7, expect.objectContaining({ toStatusId: 2, paidPaymentId: 55, version: 0 })),
    );
  });

  it('offers forced close when open tasks block a final status', async () => {
    vi.mocked(apiService.transitionSalesRequest)
      .mockRejectedValueOnce(new Error('По заявке есть незакрытые задачи. Закройте их или подтвердите принудительное закрытие.'))
      .mockResolvedValueOnce(request({ statusId: 3, finalKind: 'Failure', version: 1 }));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByText('Иванов Иван'));
    await userEvent.click(await screen.findByRole('button', { name: 'Брошен' }));

    await waitFor(() => expect(confirm).toHaveBeenCalled());
    await waitFor(() =>
      expect(apiService.transitionSalesRequest).toHaveBeenLastCalledWith(7, expect.objectContaining({ toStatusId: 3, forceCloseTasks: true })),
    );
  });
});

describe('SalesRequestFunnelSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [{ id: 5, title: 'Принять заявку', dueInMinutes: 30 }] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 1, name: 'Аня', email: 'a@x' }]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
  });

  it('is hidden without the configure right and saves edited rules for admins', async () => {
    const { unmount } = render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);
    await screen.findByText('Заявок нет.');
    expect(screen.queryByRole('button', { name: 'Настройка воронки' })).toBeNull();
    unmount();

    vi.mocked(apiService.replaceSalesRequestRules).mockResolvedValue([]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);
    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /правил: 1/ }));
    await userEvent.selectOptions(screen.getByLabelText('Исполнитель правила'), '1');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правила' }));

    await waitFor(() =>
      expect(apiService.replaceSalesRequestRules).toHaveBeenCalledWith(1, [{ title: 'Принять заявку', assigneeUserId: 1, dueInMinutes: 30, amountGreaterThan: null, source: null, requestType: null }]),
    );
  });
});

describe('SalesRequestFunnelSettings auto advance', () => {
  it('saves the auto advance target of a working status', async () => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'prep', title: 'Подготовка', sortOrder: 1, finalKind: 'None', rules: [] },
      { id: 2, key: 'sent', title: 'Отправлено', sortOrder: 2, finalKind: 'None', rules: [] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
    vi.mocked(apiService.saveSalesRequestStatus).mockResolvedValue(statuses[0]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);

    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /Подготовка/ }));
    await userEvent.selectOptions(screen.getByLabelText('Автопереход'), '2');

    await waitFor(() => expect(apiService.saveSalesRequestStatus).toHaveBeenCalledWith(1, { autoAdvanceToStatusId: 2 }));
  });
});

describe('SalesRequestFunnelSettings conditions and SLA', () => {
  it('saves a rule condition and the stage SLA', async () => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [{ id: 5, title: 'Принять заявку', dueInMinutes: 30 }] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
    vi.mocked(apiService.replaceSalesRequestRules).mockResolvedValue([]);
    vi.mocked(apiService.saveSalesRequestStatus).mockResolvedValue(statuses[0]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);

    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /правил: 1/ }));
    await userEvent.type(screen.getByLabelText('Условие: сумма больше'), '50000');
    await userEvent.selectOptions(screen.getByLabelText('Условие: источник'), 'Avito');
    await userEvent.type(screen.getByLabelText('SLA в минутах'), '120');
    await userEvent.tab();
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правила' }));

    await waitFor(() =>
      expect(apiService.replaceSalesRequestRules).toHaveBeenCalledWith(1, [
        { title: 'Принять заявку', assigneeUserId: null, dueInMinutes: 30, amountGreaterThan: 50000, source: 'Avito', requestType: null },
      ]),
    );
    await waitFor(() => expect(apiService.saveSalesRequestStatus).toHaveBeenCalledWith(1, { slaMinutes: 120 }));
  });
});
