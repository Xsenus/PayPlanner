import { documentCopyTestSession } from '../../test/documentCopySession';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi, afterEach } from 'vitest';
let sharedTimezoneTestSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { sharedTimezoneTestSession = documentCopyTestSession(); });
afterEach(() => sharedTimezoneTestSession.restore());


import { NotificationCenter } from './NotificationCenter';
import type { PagedResult, UserNotificationDto } from '../../services/api';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';

const apiMock = vi.hoisted(() => ({
  getUserNotificationTypes: vi.fn(),
  getUserNotifications: vi.fn(),
  getUnreadUserNotificationCount: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getClientsV2: vi.fn(),
  getUserSetting: vi.fn(),
  upsertUserSetting: vi.fn(),
  markUserNotificationRead: vi.fn(),
  markUserNotificationUnread: vi.fn(),
  markAllUserNotificationsRead: vi.fn(),
  archiveUserNotification: vi.fn(),
  archiveUserNotifications: vi.fn(),
  unarchiveUserNotification: vi.fn(),
  unarchiveUserNotifications: vi.fn(),
  deleteUserNotification: vi.fn(),
  deleteUserNotifications: vi.fn(),
  snoozeUserNotification: vi.fn(),
  markCaseActivity: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

function createNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return {
    id: 1,
    type: 'taskReminder',
    typeLabel: 'Напоминание по задаче',
    eventKey: 'task:1:reminder',
    title: 'Первое уведомление',
    bodyPreview: 'Описание события',
    tone: 'blue',
    toastEnabled: true,
    objectType: 'task',
    objectId: 1,
    taskId: 1,
    isRead: false,
    readAt: null,
    isArchived: false,
    archivedAt: null,
    createdAt: '2026-06-07T10:00:00Z',
    ...overrides,
  };
}

function createPage(items: UserNotificationDto[]): PagedResult<UserNotificationDto> {
  return {
    items,
    total: items.length,
    page: 1,
    pageSize: 25,
  };
}

describe('NotificationCenter', () => {
  beforeEach(() => {
    Object.values(apiMock).forEach((mock) => mock.mockReset());
    toastMock.error.mockReset();
    toastMock.success.mockReset();

    apiMock.getUserNotificationTypes.mockResolvedValue([
      {
        key: 'taskReminder',
        label: 'Напоминание по задаче',
        description: 'Напоминания по задачам',
        tone: 'blue',
        priority: 'normal',
        channels: ['inApp'],
        deduplicate: true,
        digestible: false,
        showToastByDefault: false,
        toastPriority: 'normal',
        pushEligible: false,
        pushPriority: 'normal',
        groupingWindowSeconds: 60,
        requiresAction: true,
      },
    ]);
    apiMock.getTaskJournalAssignees.mockResolvedValue([]);
    apiMock.getClientsV2.mockResolvedValue({ items: [] });
    apiMock.getUserSetting.mockResolvedValue(null);
    apiMock.markUserNotificationRead.mockImplementation(async (id: number) => createNotification({ id, isRead: true }));
    apiMock.markUserNotificationUnread.mockImplementation(async (id: number) => createNotification({ id, isRead: false }));
    apiMock.markAllUserNotificationsRead.mockResolvedValue({ updatedCount: 0 });
    apiMock.archiveUserNotifications.mockResolvedValue({ updatedCount: 0 });
    apiMock.unarchiveUserNotification.mockImplementation(async (id: number) =>
      createNotification({ id, isRead: true, isArchived: false, archivedAt: null }));
    apiMock.unarchiveUserNotifications.mockResolvedValue({ updatedCount: 0 });
    apiMock.deleteUserNotifications.mockResolvedValue({ updatedCount: 0 });
    apiMock.snoozeUserNotification.mockImplementation(async (id: number) =>
      createNotification({ id, snoozedUntil: '2026-06-07T10:30:00Z' }));
    apiMock.markCaseActivity.mockResolvedValue({});
  });

  it('lists SMS and MAX among channels affected by personal quiet hours', async () => {
    apiMock.getUserNotifications.mockResolvedValue(createPage([]));
    render(<NotificationCenter />);
    fireEvent.click(await screen.findByRole('button', { name: 'Типы' }));
    expect(await screen.findByText(/Вне рабочего времени CRM оставит события.*Push, Email, Telegram, SMS и MAX/)).toBeTruthy();
    expect(screen.getByText('Срочные уведомления показывать сразу')).toBeTruthy();
  });

  it('selects notifications on the page and archives selected items', async () => {
    const first = createNotification({ id: 10, title: 'Первое уведомление' });
    const second = createNotification({ id: 11, title: 'Второе уведомление' });
    apiMock.getUserNotifications.mockResolvedValue(createPage([first, second]));
    apiMock.archiveUserNotifications.mockResolvedValueOnce({ updatedCount: 2 });

    render(<NotificationCenter />);

    expect(await screen.findByText('Первое уведомление')).toBeTruthy();

    const selectPage = screen.getByLabelText('Выбрать все уведомления на странице') as HTMLInputElement;
    const selectFirst = screen.getByLabelText('Выбрать уведомление Первое уведомление') as HTMLInputElement;
    const selectSecond = screen.getByLabelText('Выбрать уведомление Второе уведомление') as HTMLInputElement;

    fireEvent.click(selectPage);
    expect(selectPage.checked).toBe(true);
    expect(selectFirst.checked).toBe(true);
    expect(selectSecond.checked).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'В архив выбранные уведомления: 2' }));

    await waitFor(() => expect(apiMock.archiveUserNotifications).toHaveBeenCalledWith([10, 11]));
    expect(toastMock.success).toHaveBeenCalledWith('Отправлено в архив: 2.');
  });

  it('shows an indeterminate page selector when only some notifications are selected', async () => {
    apiMock.getUserNotifications.mockResolvedValue(createPage([
      createNotification({ id: 14, title: 'Первое частичное' }),
      createNotification({ id: 15, title: 'Второе частичное' }),
    ]));

    render(<NotificationCenter />);
    await screen.findByText('Первое частичное');

    const selectPage = screen.getByLabelText('Выбрать все уведомления на странице') as HTMLInputElement;
    fireEvent.click(screen.getByLabelText('Выбрать уведомление Первое частичное'));

    expect(selectPage.checked).toBe(false);
    expect(selectPage.indeterminate).toBe(true);
    expect(screen.getByText('Выбрано: 1')).toBeTruthy();
  });

  it('separates notifications with solid date headings', async () => {
    apiMock.getUserNotifications.mockResolvedValue(createPage([
      createNotification({ id: 16, title: 'Первое за седьмое июня', createdAt: '2026-06-07T10:00:00Z' }),
      createNotification({ id: 17, title: 'Второе за седьмое июня', createdAt: '2026-06-07T08:00:00Z' }),
      createNotification({ id: 18, title: 'Уведомление за шестое июня', createdAt: '2026-06-06T12:00:00Z' }),
    ]));

    render(<NotificationCenter />);

    expect(await screen.findByText('Первое за седьмое июня')).toBeTruthy();
    expect(screen.getAllByRole('separator')).toHaveLength(2);
    expect(screen.getByRole('separator', { name: 'Уведомления за 7 июня 2026 года' })).toBeTruthy();
    expect(screen.getByRole('separator', { name: 'Уведомления за 6 июня 2026 года' })).toBeTruthy();
    expect(screen.getAllByText('7 июня 2026 года')).toHaveLength(1);
  });

  it('groups by the saved time zone and starts a date heading on every page', async () => {
    let zone = 'Asia/Yakutsk';
    apiMock.getUserSetting.mockImplementation(async (key: string) => key === 'profile.timeZoneId' ? { key, value: zone } : null);
    const first = createNotification({ id: 30, title: 'После полуночи', createdAt: '2026-06-07T18:00:00Z' });
    const second = createNotification({ id: 31, title: 'До полуночи', createdAt: '2026-06-07T14:00:00Z' });
    const third = createNotification({ id: 32, title: 'Продолжение дня', createdAt: '2026-06-07T13:00:00Z' });
    const remainingFirstPage = Array.from({ length: 23 }, (_, index) => createNotification({
      id: 100 + index, title: `Сообщение ${index}`, createdAt: '2026-06-07T13:30:00Z',
    }));
    apiMock.getUserNotifications.mockImplementation(async ({ page }: { page: number }) => ({
      items: page === 1 ? [first, second, ...remainingFirstPage] : [third], total: 26, page, pageSize: 25,
    }));
    const delayedSearchTimers: Array<() => void> = [];
    const originalTimeout = window.setTimeout.bind(window);
    const timerSpy = vi.spyOn(window, 'setTimeout').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 300 && typeof handler === 'function') {
        delayedSearchTimers.push(() => handler());
        return 0;
      }
      return originalTimeout(handler, delay);
    }) as typeof globalThis.setTimeout);
    const view = render(<NotificationCenter />);
    await waitFor(() => {
      expect(screen.getByRole('separator', { name: 'Уведомления за 8 июня 2026 года' })).toBeTruthy();
      expect(screen.getByRole('separator', { name: 'Уведомления за 7 июня 2026 года' })).toBeTruthy();
      expect(screen.getAllByRole('separator')).toHaveLength(2);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
    await screen.findByText('Продолжение дня');
    act(() => delayedSearchTimers.forEach((callback) => callback()));
    timerSpy.mockRestore();
    await waitFor(() => expect(screen.getByText('Продолжение дня')).toBeTruthy());
    expect(screen.getByRole('separator', { name: 'Уведомления за 7 июня 2026 года' })).toBeTruthy();
    expect(screen.queryByRole('separator', { name: 'Уведомления за 8 июня 2026 года' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await screen.findByText('После полуночи');
    expect(screen.getAllByRole('separator')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
    await screen.findByText('Продолжение дня');
    fireEvent.change(screen.getByPlaceholderText('Поиск по уведомлению, задаче или событию'), { target: { value: 'важное' } });
    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, search: 'важное' })));
    view.unmount();

    zone = 'UTC';
    render(<NotificationCenter />);
    await screen.findByText('После полуночи');
    await waitFor(() => expect(screen.getAllByRole('separator')).toHaveLength(1));
    expect(screen.getByRole('separator', { name: 'Уведомления за 7 июня 2026 года' })).toBeTruthy();
  });

  it('confirms case activity and snoozes inactivity notifications', async () => {
    const confirmNotification = createNotification({
      id: 12,
      type: 'caseInactivityControl',
      typeLabel: 'Автоконтроль дела',
      title: 'Проверьте движение по делу',
      objectType: 'case',
      objectId: 55,
      taskId: 88,
    });
    const snoozeNotification = createNotification({
      id: 13,
      type: 'caseInactivityReminder',
      typeLabel: 'Напоминание о неактивности',
      title: 'Нет активности по делу',
      objectType: 'case',
      objectId: 56,
      taskId: null,
    });
    apiMock.getUserNotifications.mockResolvedValue(
      createPage([confirmNotification, snoozeNotification]),
    );

    render(<NotificationCenter onOpenCase={vi.fn()} />);

    await screen.findByText('Проверьте движение по делу');
    fireEvent.click(screen.getAllByRole('button', { name: 'Подтвердить активность' })[0]);
    await waitFor(() => {
      expect(apiMock.markCaseActivity).toHaveBeenCalledWith(55, { confirmed: true });
      expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(12);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Отложить 30 мин' }));
    await waitFor(() => {
      expect(apiMock.snoozeUserNotification).toHaveBeenCalledWith(13, 30);
    });
    expect(toastMock.success).toHaveBeenCalledWith('Напоминание отложено на 30 минут.');
  });

  it('deletes only manually selected notifications', async () => {
    const first = createNotification({ id: 20, title: 'Удалить первое' });
    const second = createNotification({ id: 21, title: 'Оставить второе' });
    apiMock.getUserNotifications.mockResolvedValue(createPage([first, second]));
    apiMock.deleteUserNotifications.mockResolvedValueOnce({ updatedCount: 1 });

    render(<NotificationCenter />);

    expect(await screen.findByText('Удалить первое')).toBeTruthy();

    fireEvent.click(screen.getByLabelText('Выбрать уведомление Удалить первое'));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить выбранные уведомления: 1' }));

    await waitFor(() => expect(apiMock.deleteUserNotifications).toHaveBeenCalledWith([20]));
    expect(apiMock.deleteUserNotifications).not.toHaveBeenCalledWith([20, 21]);
    expect(toastMock.success).toHaveBeenCalledWith('Удалено уведомлений: 1.');
  });

  it('clearly labels the action that returns a read notification to unread', async () => {
    apiMock.getUserNotifications.mockResolvedValue(
      createPage([createNotification({ id: 30, isRead: true, readAt: '2026-06-07T10:05:00Z' })]),
    );

    render(<NotificationCenter />);

    fireEvent.click(await screen.findByRole('button', { name: 'В непрочитанные' }));

    await waitFor(() => expect(apiMock.markUserNotificationUnread).toHaveBeenCalledWith(30));
  });

  it('returns one or several archived notifications to the active journal', async () => {
    const archived = createNotification({
      id: 31,
      title: 'Архивное уведомление',
      isRead: true,
      isArchived: true,
      archivedAt: '2026-06-07T10:06:00Z',
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([archived]));
    apiMock.unarchiveUserNotifications.mockResolvedValueOnce({ updatedCount: 1 });

    render(<NotificationCenter />);

    const statusCombobox = (await screen.findAllByRole('combobox'))[0];
    fireEvent.click(statusCombobox);
    fireEvent.click(await screen.findByRole('option', { name: 'Архив' }));

    fireEvent.click(await screen.findByLabelText('Выбрать уведомление Архивное уведомление'));
    fireEvent.click(screen.getByRole('button', { name: 'Вернуть выбранные уведомления из архива: 1' }));
    await waitFor(() => expect(apiMock.unarchiveUserNotifications).toHaveBeenCalledWith([31]));

    fireEvent.click(await screen.findByRole('button', { name: 'Вернуть' }));
    await waitFor(() => expect(apiMock.unarchiveUserNotification).toHaveBeenCalledWith(31));
    expect(toastMock.success).toHaveBeenCalledWith('Уведомление возвращено из архива.');
  });

  it('refreshes the open list when a realtime notification arrives', async () => {
    const initial = createNotification({ id: 40, title: 'Первое уведомление' });
    const realtime = createNotification({ id: 41, title: 'Новая задача из realtime' });
    apiMock.getUserNotifications
      .mockResolvedValueOnce(createPage([initial]))
      .mockResolvedValueOnce(createPage([realtime, initial]));

    render(<NotificationCenter />);

    expect(await screen.findByText('Первое уведомление')).toBeTruthy();

    act(() => {
      window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT, {
        detail: { source: 'realtime', reason: 'created', unreadDelta: 1, notificationId: 41 },
      }));
    });

    expect(await screen.findByText('Новая задача из realtime')).toBeTruthy();
    expect(apiMock.getUserNotifications).toHaveBeenCalledTimes(2);
  });

  it('passes domain and action filters to inbox requests and read-all action', async () => {
    const finance = createNotification({
      id: 50,
      type: 'paymentAdvanceReviewRequired',
      typeLabel: 'Контроль аванса',
      title: 'Финансовое уведомление',
      objectType: 'payment',
      objectId: 50,
      taskId: null,
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([finance]));
    apiMock.markAllUserNotificationsRead.mockResolvedValueOnce({ updatedCount: 1 });

    render(<NotificationCenter />);

    expect(await screen.findByText('Финансовое уведомление')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Финансы/ }));
    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenLastCalledWith(
        expect.objectContaining({
          domain: 'finance',
          requiresAction: undefined,
        }),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Требуют действия' }));
    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenLastCalledWith(
        expect.objectContaining({
          domain: 'finance',
          requiresAction: true,
        }),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: /Прочитать все/ }));
    await waitFor(() =>
      expect(apiMock.markAllUserNotificationsRead).toHaveBeenCalledWith(
        expect.objectContaining({
          domain: 'finance',
          requiresAction: true,
        }),
      ),
    );
  });

  it('passes inbox search, status, type, dates, employee, client and task role filters to requests and reset', async () => {
    const item = createNotification({
      id: 60,
      title: 'Срочная задача клиента',
      bodyPreview: 'Нужно проверить параметры фильтров',
    });
    apiMock.getTaskJournalAssignees.mockResolvedValueOnce([
      { id: 7, name: 'Анна Иванова', email: 'anna@example.test' },
    ]);
    apiMock.getClientsV2.mockResolvedValueOnce({
      items: [{ id: 15, name: 'ООО Ромашка' }],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter />);

    expect(await screen.findByText('Срочная задача клиента')).toBeTruthy();
    expect(await screen.findByText('Анна Иванова', { selector: 'option' })).toBeTruthy();
    expect(await screen.findByText('ООО Ромашка', { selector: 'option' })).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText('Поиск по уведомлению, задаче или событию'), {
      target: { value: '  срочная задача  ' },
    });
    fireEvent.change(screen.getByDisplayValue('Активные'), { target: { value: 'unread' } });
    fireEvent.change(screen.getByDisplayValue('Все типы'), { target: { value: 'taskReminder' } });
    fireEvent.change(screen.getByLabelText('С даты'), { target: { value: '2026-06-01' } });
    fireEvent.change(screen.getByLabelText('По дату'), { target: { value: '2026-06-15' } });
    fireEvent.change(screen.getByLabelText('Сотрудник'), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Клиент'), { target: { value: '15' } });
    fireEvent.change(screen.getByLabelText('Роль'), { target: { value: 'assignee' } });

    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenLastCalledWith(
        expect.objectContaining({
          status: 'unread',
          type: 'taskReminder',
          search: 'срочная задача',
          dateFrom: '2026-06-01',
          dateTo: '2026-06-15',
          employeeId: 7,
          clientId: 15,
          taskRole: 'assignee',
          page: 1,
        }),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Сбросить' }));

    await waitFor(() =>
      expect(apiMock.getUserNotifications).toHaveBeenLastCalledWith(
        expect.objectContaining({
          status: 'active',
          type: undefined,
          domain: undefined,
          requiresAction: undefined,
          search: '',
          dateFrom: undefined,
          dateTo: undefined,
          employeeId: undefined,
          clientId: undefined,
          taskRole: undefined,
          page: 1,
        }),
      ),
    );
  });

  it.each([
    ['payment', 'К платежу', 'onOpenPayment'],
    ['invoice', 'К счету', 'onOpenInvoice'],
    ['act', 'К акту', 'onOpenAct'],
    ['contract', 'К договору', 'onOpenContract'],
  ] as const)('opens linked %s notification object', async (objectType, buttonLabel, handlerName) => {
    const handlers = {
      onOpenPayment: vi.fn(),
      onOpenInvoice: vi.fn(),
      onOpenAct: vi.fn(),
      onOpenContract: vi.fn(),
    };
    const item = createNotification({
      id: 30,
      title: `Связанный объект ${objectType}`,
      objectType,
      objectId: 777,
      taskId: null,
      ...(objectType === 'payment'
        ? { metadataJson: JSON.stringify({ paymentType: 'Expense' }) }
        : {}),
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter {...handlers} />);

    expect(await screen.findByText(`Связанный объект ${objectType}`)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: buttonLabel }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(30));
    if (objectType === 'payment') {
      expect(handlers[handlerName]).toHaveBeenCalledWith(777, 'Expense');
    } else {
      expect(handlers[handlerName]).toHaveBeenCalledWith(777);
    }
  });

  it('opens linked task notification from the inbox action', async () => {
    const onOpenTask = vi.fn();
    const item = createNotification({
      id: 88,
      title: 'Новая задача: проверить документы',
      objectType: 'task',
      objectId: 479,
      taskId: 479,
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter onOpenTask={onOpenTask} />);

    expect(await screen.findByText('Новая задача: проверить документы')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'К задаче' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(88, { suppressAuthSessionExpired: true }));
    expect(onOpenTask).toHaveBeenCalledWith(479);
  });

  it('opens linked task even when marking the notification as read fails', async () => {
    const onOpenTask = vi.fn();
    const item = createNotification({
      id: 89,
      title: 'Новая задача: проверить уведомления',
      objectType: 'task',
      objectId: 480,
      taskId: 480,
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));
    apiMock.markUserNotificationRead.mockRejectedValueOnce(new Error('HTTP 401 Unauthorized'));

    render(<NotificationCenter onOpenTask={onOpenTask} />);

    expect(await screen.findByText('Новая задача: проверить уведомления')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'К задаче' }));

    expect(onOpenTask).toHaveBeenCalledWith(480);
    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(89, { suppressAuthSessionExpired: true }));
    expect(toastMock.error).toHaveBeenCalledWith('HTTP 401 Unauthorized');
  });

  it.each([
    ['payment', 'К платежу'],
    ['invoice', 'К счету'],
    ['act', 'К акту'],
    ['contract', 'К договору'],
  ] as const)('hides linked %s action when the section handler is unavailable', async (objectType, buttonLabel) => {
    const item = createNotification({
      id: 33,
      title: `Закрытый связанный объект ${objectType}`,
      objectType,
      objectId: 778,
      taskId: null,
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter />);

    expect(await screen.findByText(`Закрытый связанный объект ${objectType}`)).toBeTruthy();
    expect(screen.queryByRole('button', { name: buttonLabel })).toBeNull();
  });

  it('opens case document notifications on the documents tab', async () => {
    const onOpenCase = vi.fn();
    const onOpenCaseDocument = vi.fn();
    const item = createNotification({
      id: 31,
      type: 'caseDocument',
      typeLabel: 'Документы дела',
      title: 'Загружен документ дела',
      objectType: 'case',
      objectId: 88,
      taskId: null,
      metadataJson: '{"clientCaseDocumentId":991,"action":"uploaded"}',
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter onOpenCase={onOpenCase} onOpenCaseDocument={onOpenCaseDocument} />);

    expect(await screen.findByText('Загружен документ дела')).toBeTruthy();
    expect(screen.getByText('Документ #991')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'К документам' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(31));
    expect(onOpenCaseDocument).toHaveBeenCalledWith(88, 991);
    expect(onOpenCase).not.toHaveBeenCalled();
  });

  it('falls back to opening the case when document metadata is absent', async () => {
    const onOpenCase = vi.fn();
    const onOpenCaseDocument = vi.fn();
    const item = createNotification({
      id: 32,
      type: 'caseDocument',
      typeLabel: 'Документы дела',
      title: 'Документ без metadata',
      objectType: 'case',
      objectId: 89,
      taskId: null,
      metadataJson: '{}',
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter onOpenCase={onOpenCase} onOpenCaseDocument={onOpenCaseDocument} />);

    expect(await screen.findByText('Документ без metadata')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'К делу' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(32));
    expect(onOpenCase).toHaveBeenCalledWith(89);
    expect(onOpenCaseDocument).not.toHaveBeenCalled();
  });
});
