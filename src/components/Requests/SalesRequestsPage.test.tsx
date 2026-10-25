import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SalesRequestsPage } from './SalesRequestsPage';
import { apiService } from '../../services/api';
import type { SalesRequestItem, SalesRequestStatusItem } from '../../types';
import { formatLocalYMD } from '../../utils/dateUtils';

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
    createTaskJournalItem: vi.fn(),
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

  it.each(['-1', 'wrong', '1000000000001'])('rejects invalid amount %s instead of silently omitting it', async (amount) => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.type(screen.getByLabelText('Сумма (правка)'), amount);
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правки' }));

    expect(toast.error).toHaveBeenCalledWith('Укажите сумму от 0 до 1 000 000 000 000.', 'Заявки');
    expect(apiService.updateSalesRequest).not.toHaveBeenCalled();
  });

  it('does not reopen a card closed while its edit is being saved', async () => {
    let resolveSave!: (value: SalesRequestItem) => void;
    vi.mocked(apiService.updateSalesRequest).mockImplementation(() => new Promise((resolve) => { resolveSave = resolve; }));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правки' }));
    await userEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    vi.mocked(apiService.getSalesRequest).mockClear();
    await act(async () => { resolveSave(request()); });

    expect(apiService.getSalesRequest).not.toHaveBeenCalled();
    expect(screen.queryByTestId('sales-request-detail')).toBeNull();
  });

  it('drops unsaved edits when another request is opened', async () => {
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 2, page: 1, pageSize: 50, items: [request(), request({ id: 8, clientName: 'Другой клиент' })] });
    vi.mocked(apiService.getSalesRequest).mockImplementation(async (id) => detail(request({ id })));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.type(screen.getByLabelText('Суть запроса (правка)'), ' правки первого');
    await userEvent.click(await screen.findByText('Другой клиент'));

    await waitFor(() => expect(screen.queryByLabelText('Суть запроса (правка)')).toBeNull());
    expect(apiService.updateSalesRequest).not.toHaveBeenCalled();
  });

  it('uses calendar dates for the monthly report without shifting the first day into the previous month', async () => {
    const today = new Date();
    vi.mocked(apiService.getSalesRequestReport).mockResolvedValue({ total: 0, succeeded: 0, failed: 0, inProgress: 0, conversionPercent: 0, wonAmount: 0, lostAmount: 0, bySource: [], lossByStage: [] });
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics />);
    await userEvent.click(await screen.findByRole('button', { name: 'Отчёт за месяц' }));

    expect(apiService.getSalesRequestReport).toHaveBeenCalledWith(formatLocalYMD(new Date(today.getFullYear(), today.getMonth(), 1)), formatLocalYMD(today));
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

  it('opens only the existing case tasks for a readonly request without creating or copying sales data', async () => {
    const onOpenCaseTasks = vi.fn();
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({
      clientCaseId: 45, resultClientCaseId: 99, amount: 123456, comment: 'Private sales negotiations',
    })));
    render(<SalesRequestsPage allowCreate={false} allowEdit={false} allowAnalytics={false} initialRequestId={7} currentUserId={1} onOpenCaseTasks={onOpenCaseTasks} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Открыть задачи дела' }));

    expect(onOpenCaseTasks.mock.calls).toEqual([[45]]);
    expect(screen.getByText(/Рабочая задача создаётся в деле и не открывает заявку юристу/)).toBeTruthy();
    expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
    expect(apiService.createSalesRequestTask).not.toHaveBeenCalled();
    expect(apiService.updateSalesRequest).not.toHaveBeenCalled();
    expect(confirm).not.toHaveBeenCalled();
  });

  it.each([undefined, null, 0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1])(
    'does not substitute a result case when the existing case id is %s', async (clientCaseId) => {
      const onOpenCaseTasks = vi.fn();
      vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId, resultClientCaseId: 99 })));
      render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} currentUserId={1} onOpenCaseTasks={onOpenCaseTasks} />);

      await screen.findByTestId('sales-request-detail');

      expect(screen.queryByRole('button', { name: 'Открыть задачи дела' })).toBeNull();
      expect(onOpenCaseTasks).not.toHaveBeenCalled();
      expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
    },
  );

  it('does not offer case navigation without the host cases and tasks view permission', async () => {
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId: 45 })));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} currentUserId={1} />);

    await screen.findByTestId('sales-request-detail');

    expect(screen.queryByRole('button', { name: 'Открыть задачи дела' })).toBeNull();
  });

  it('keeps a request draft when case navigation is cancelled', async () => {
    const onOpenCaseTasks = vi.fn();
    confirm.mockResolvedValue(false);
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId: 45 })));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} currentUserId={1} onOpenCaseTasks={onOpenCaseTasks} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.type(screen.getByLabelText('Суть запроса (правка)'), ' несохранённый текст');
    const originalAddress = window.location.href;

    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));
    await waitFor(() => expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Перейти к задачам дела?' })));

    expect(onOpenCaseTasks).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Суть запроса (правка)') as HTMLTextAreaElement).value).toContain('несохранённый текст');
    expect(window.location.href).toBe(originalAddress);
    expect(apiService.updateSalesRequest).not.toHaveBeenCalled();
  });

  it('opens the case only after confirmation without saving a pending sales task', async () => {
    const onOpenCaseTasks = vi.fn();
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId: 45 })));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} currentUserId={1} onOpenCaseTasks={onOpenCaseTasks} />);
    await userEvent.type(await screen.findByRole('textbox', { name: 'Текст задачи' }), 'Незавершённое поручение');

    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));

    await waitFor(() => expect(onOpenCaseTasks.mock.calls).toEqual([[45]]));
    expect(confirm).toHaveBeenCalledOnce();
    expect(apiService.createSalesRequestTask).not.toHaveBeenCalled();
    expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('does not navigate after case rights are revoked while confirmation is pending', async () => {
    let finishConfirmation!: (confirmed: boolean) => void;
    confirm.mockImplementationOnce(() => new Promise<boolean>((resolve) => { finishConfirmation = resolve; }));
    const onOpenCaseTasks = vi.fn();
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId: 45 })));
    const props = { allowCreate: true, allowEdit: true, allowAnalytics: false, initialRequestId: 7, currentUserId: 1 };
    const view = render(<SalesRequestsPage {...props} onOpenCaseTasks={onOpenCaseTasks} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled());

    view.rerender(<SalesRequestsPage {...props} />);
    await act(async () => { finishConfirmation(true); });

    expect(onOpenCaseTasks).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Открыть задачи дела' })).toBeNull();
    expect(screen.getByLabelText('Суть запроса (правка)')).toBeTruthy();
  });

  it('does not navigate an old detail after another request opens during confirmation', async () => {
    let finishConfirmation!: (confirmed: boolean) => void;
    confirm.mockImplementationOnce(() => new Promise<boolean>((resolve) => { finishConfirmation = resolve; }));
    const onOpenCaseTasks = vi.fn();
    vi.mocked(apiService.getSalesRequest).mockImplementation(async (id) => detail(request({ id, clientCaseId: id === 7 ? 45 : 88 })));
    const props = { allowCreate: true, allowEdit: true, allowAnalytics: false, currentUserId: 1, onOpenCaseTasks };
    const view = render(<SalesRequestsPage {...props} initialRequestId={7} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled());

    view.rerender(<SalesRequestsPage {...props} initialRequestId={8} />);
    await waitFor(() => expect(screen.getByRole('heading', { name: /Заявка №8/ })).toBeTruthy());
    await act(async () => { finishConfirmation(true); });

    expect(onOpenCaseTasks).not.toHaveBeenCalled();
  });

  it('ignores the previous actor detail response and navigates only the fresh actor detail', async () => {
    let finishOldRead!: (value: ReturnType<typeof detail>) => void;
    vi.mocked(apiService.getSalesRequest)
      .mockImplementationOnce(() => new Promise((resolve) => { finishOldRead = resolve; }))
      .mockResolvedValueOnce(detail(request({ clientCaseId: 88 })));
    const onOpenCaseTasks = vi.fn();
    const props = { allowCreate: true, allowEdit: true, allowAnalytics: false, initialRequestId: 7, onOpenCaseTasks };
    const view = render(<SalesRequestsPage {...props} currentUserId={1} />);
    await waitFor(() => expect(apiService.getSalesRequest).toHaveBeenCalledOnce());

    view.rerender(<SalesRequestsPage {...props} currentUserId={2} />);
    await screen.findByRole('button', { name: 'Открыть задачи дела' });
    await act(async () => { finishOldRead(detail(request({ clientCaseId: 45 }))); });
    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));

    expect(onOpenCaseTasks.mock.calls).toEqual([[88]]);
    expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('does not navigate after the actor changes during confirmation', async () => {
    let finishConfirmation!: (confirmed: boolean) => void;
    confirm.mockImplementationOnce(() => new Promise<boolean>((resolve) => { finishConfirmation = resolve; }));
    const onOpenCaseTasks = vi.fn();
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail(request({ clientCaseId: 45 })));
    const props = { allowCreate: true, allowEdit: true, allowAnalytics: false, initialRequestId: 7, onOpenCaseTasks };
    const view = render(<SalesRequestsPage {...props} currentUserId={1} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить данные заявки' }));
    await userEvent.click(screen.getByRole('button', { name: 'Открыть задачи дела' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled());

    view.rerender(<SalesRequestsPage {...props} currentUserId={2} />);
    await act(async () => { finishConfirmation(true); });

    expect(onOpenCaseTasks).not.toHaveBeenCalled();
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
