import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { NotificationCenter } from './NotificationCenter';
import type { PagedResult, UserNotificationDto } from '../../services/api';

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
  deleteUserNotification: vi.fn(),
  deleteUserNotifications: vi.fn(),
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
      },
    ]);
    apiMock.getTaskJournalAssignees.mockResolvedValue([]);
    apiMock.getClientsV2.mockResolvedValue({ items: [] });
    apiMock.getUserSetting.mockResolvedValue(null);
    apiMock.markUserNotificationRead.mockImplementation(async (id: number) => createNotification({ id, isRead: true }));
    apiMock.markUserNotificationUnread.mockImplementation(async (id: number) => createNotification({ id, isRead: false }));
    apiMock.markAllUserNotificationsRead.mockResolvedValue({ updatedCount: 0 });
    apiMock.archiveUserNotifications.mockResolvedValue({ updatedCount: 0 });
    apiMock.deleteUserNotifications.mockResolvedValue({ updatedCount: 0 });
  });

  it('selects notifications on the page and archives selected items', async () => {
    const first = createNotification({ id: 10, title: 'Первое уведомление' });
    const second = createNotification({ id: 11, title: 'Второе уведомление' });
    apiMock.getUserNotifications.mockResolvedValue(createPage([first, second]));
    apiMock.archiveUserNotifications.mockResolvedValueOnce({ updatedCount: 2 });

    render(<NotificationCenter />);

    expect(await screen.findByText('Первое уведомление')).toBeTruthy();

    fireEvent.click(screen.getByLabelText('Выбрать все уведомления на странице'));
    fireEvent.click(screen.getByRole('button', { name: 'В архив выбранные уведомления: 2' }));

    await waitFor(() => expect(apiMock.archiveUserNotifications).toHaveBeenCalledWith([10, 11]));
    expect(toastMock.success).toHaveBeenCalledWith('Отправлено в архив: 2.');
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
    });
    apiMock.getUserNotifications.mockResolvedValue(createPage([item]));

    render(<NotificationCenter {...handlers} />);

    expect(await screen.findByText(`Связанный объект ${objectType}`)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: buttonLabel }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(30));
    expect(handlers[handlerName]).toHaveBeenCalledWith(777);
  });
});
