import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ForcedTaskRevisionModal } from './ForcedTaskRevisionModal';
import type { PagedResult, UserNotificationDto } from '../../services/api';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';

const apiMock = vi.hoisted(() => ({
  getUserNotifications: vi.fn(),
  markUserNotificationRead: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

function createNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return {
    id: 31,
    type: 'taskRevisionRequested',
    typeLabel: 'Возврат на доработку',
    eventKey: 'task:101:approval-decision',
    title: 'Задача возвращена на доработку: Подготовить иск',
    bodyPreview: 'Исправить дату и приложить документ.',
    tone: 'orange',
    toastEnabled: false,
    objectType: 'task',
    objectId: 101,
    taskId: 101,
    metadataJson: '{"resolution":"Уточнить дату заседания."}',
    requiresAction: true,
    isRead: false,
    readAt: null,
    isArchived: false,
    archivedAt: null,
    createdAt: '2026-06-29T12:00:00Z',
    ...overrides,
  };
}

function createPage(items: UserNotificationDto[]): PagedResult<UserNotificationDto> {
  return {
    items,
    total: items.length,
    page: 1,
    pageSize: 10,
  };
}

function createCompletedApprovalNotification(overrides: Partial<UserNotificationDto> = {}): UserNotificationDto {
  return createNotification({
    id: 41,
    type: 'taskApproved',
    typeLabel: 'Задача согласована',
    eventKey: 'task:202:approval-decision',
    title: 'Задача согласована и завершена: Проверить договор',
    bodyPreview: 'Согласующий согласовал и завершил задачу.',
    tone: 'emerald',
    objectId: 202,
    taskId: 202,
    metadataJson: '{"decision":"approved","resultingStatus":"done","completed":true}',
    ...overrides,
  });
}

function mockNotificationPages(revisions: UserNotificationDto[] = [], approvals: UserNotificationDto[] = []) {
  apiMock.getUserNotifications.mockImplementation(async (params?: { type?: string }) => {
    if (params?.type === 'taskApproved') {
      return createPage(approvals);
    }

    return createPage(revisions);
  });
}

describe('ForcedTaskRevisionModal', () => {
  beforeEach(() => {
    apiMock.getUserNotifications.mockReset();
    apiMock.markUserNotificationRead.mockReset();
    apiMock.markUserNotificationRead.mockImplementation(async (id: number) => createNotification({ id, isRead: true }));
  });

  it('shows unread revision requests as a mandatory modal and acknowledges them', async () => {
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Задача на доработку')).toBeTruthy();
    expect(screen.getByText('Задача вернулась на доработку')).toBeTruthy();
    expect(screen.getByText('Подготовить иск')).toBeTruthy();
    expect(screen.getByText('Уточнить дату заседания.')).toBeTruthy();
    expect(screen.getByTestId('forced-task-message-box').className).toContain('min-h-[120px]');

    fireEvent.click(screen.getByRole('button', { name: 'Информация прочитана' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(31));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('marks the message read before opening the task', async () => {
    const onOpenTask = vi.fn();
    mockNotificationPages([createNotification()]);

    render(<ForcedTaskRevisionModal onOpenTask={onOpenTask} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(31));
    expect(onOpenTask).toHaveBeenCalledWith(101);
  });

  it('shows completed approvals as a mandatory read confirmation', async () => {
    mockNotificationPages([], [createCompletedApprovalNotification()]);

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Согласование завершено')).toBeTruthy();
    expect(screen.getByText('Задача согласована и завершена')).toBeTruthy();
    expect(screen.getByText('Проверить договор')).toBeTruthy();
    expect(screen.getByText('Согласующий согласовал и завершил задачу.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Ознакомлен' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(41));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('loads configured forced notifications into one queue and keeps the oldest item first', async () => {
    mockNotificationPages(
      [createNotification({ id: 32, createdAt: '2026-06-29T12:05:00Z' })],
      [createCompletedApprovalNotification({ id: 41, createdAt: '2026-06-29T12:00:00Z' })],
    );

    render(<ForcedTaskRevisionModal />);

    expect(await screen.findByTestId('forced-task-action-queue')).toBeTruthy();
    expect(screen.getByText('Еще обязательных уведомлений: 1')).toBeTruthy();
    expect(screen.getByText('Согласование завершено')).toBeTruthy();
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskRevisionRequested',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));
    expect(apiMock.getUserNotifications).toHaveBeenCalledWith(expect.objectContaining({
      status: 'unread',
      type: 'taskApproved',
      requiresAction: true,
      page: 1,
      pageSize: 25,
    }));

    fireEvent.click(screen.getByRole('button', { name: 'Ознакомлен' }));

    await waitFor(() => expect(apiMock.markUserNotificationRead).toHaveBeenCalledWith(41));
    expect(await screen.findByText('Задача на доработку')).toBeTruthy();
  });

  it('restores a forced notification from the unread inbox after the notification event', async () => {
    let revisions: UserNotificationDto[] = [];
    mockNotificationPages(revisions, []);

    render(<ForcedTaskRevisionModal />);

    await waitFor(() => expect(apiMock.getUserNotifications).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();

    revisions.splice(0, revisions.length, createNotification({ id: 51 }));
    window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT));

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('Задача на доработку')).toBeTruthy();
  });
});
