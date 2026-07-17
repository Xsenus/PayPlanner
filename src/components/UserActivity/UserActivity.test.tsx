import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { UserActivityLogItem, UserActivityLogResponse } from '../../types/userActivity';
import { UserActivity } from './UserActivity';

const authState = vi.hoisted(() => ({ admin: true }));
const isAdmin = () => authState.admin;

vi.mock('../../services/api', () => ({
  apiService: {
    getUserActivityLogs: vi.fn(),
    getUserActivityFilters: vi.fn(),
    deleteUserActivityLogs: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7 },
    isAdmin,
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn() }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn() }),
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Asia/Novosibirsk',
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['createdAt'],
    orderedVisibleColumns: ['createdAt'],
    visibleColumns: ['createdAt'],
    pageSize: 25,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: () => 190,
    getTableWidth: () => 1280,
    resetTableSettings: vi.fn(),
  }),
}));

const emptyResponse: UserActivityLogResponse = {
  items: [],
  total: 0,
  page: 1,
  pageSize: 25,
};

const activityItem: UserActivityLogItem = {
  id: 1,
  userId: 7,
  userEmail: 'admin@example.com',
  userFullName: 'Администратор',
  category: 'Безопасность',
  action: 'Просмотр',
  section: 'Настройки',
  objectType: null,
  objectId: null,
  description: 'Открыт журнал активности',
  status: 'Success',
  ipAddress: '127.0.0.1',
  userAgent: null,
  httpMethod: 'GET',
  path: '/api/user-activity',
  queryString: null,
  httpStatusCode: 200,
  durationMs: 25,
  metadata: null,
  createdAt: '2026-07-17T06:00:00Z',
};

describe('UserActivity', () => {
  beforeEach(() => {
    authState.admin = true;
    vi.mocked(apiService.getUserActivityLogs).mockReset();
    vi.mocked(apiService.getUserActivityLogs).mockResolvedValue(emptyResponse);
    vi.mocked(apiService.getUserActivityFilters).mockReset();
    vi.mocked(apiService.getUserActivityFilters).mockResolvedValue({
      statuses: ['Info', 'Success', 'Warning', 'Failure'],
      categories: ['Безопасность'],
      actions: ['Просмотр'],
      sections: ['Настройки'],
      httpMethods: ['GET'],
      actors: [],
    });
    vi.mocked(apiService.deleteUserActivityLogs).mockReset();
  });

  it('combines rapid search input into one request with an abort signal', async () => {
    render(<UserActivity />);
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1));
    vi.mocked(apiService.getUserActivityLogs).mockClear();

    fireEvent.click(screen.getByRole('button', { name: /Показать фильтры/u }));
    const searchInput = screen.getByPlaceholderText('Описание, объект, раздел');
    fireEvent.change(searchInput, { target: { value: 'вх' } });
    fireEvent.change(searchInput, { target: { value: 'вход' } });
    fireEvent.change(searchInput, { target: { value: 'вход в систему' } });

    expect(apiService.getUserActivityLogs).not.toHaveBeenCalled();
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1), { timeout: 2_000 });
    expect(apiService.getUserActivityLogs).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'вход в систему', page: 1, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('aborts an active request when filters change', async () => {
    render(<UserActivity />);
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1));
    vi.mocked(apiService.getUserActivityLogs).mockClear();
    const signals: AbortSignal[] = [];
    const resolvePending: Array<(response: UserActivityLogResponse) => void> = [];
    vi.mocked(apiService.getUserActivityLogs).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvePending.push(resolve));
    });

    fireEvent.click(screen.getByRole('button', { name: /Показать фильтры/u }));
    const statusSelect = screen.getByLabelText('Статус');
    fireEvent.change(statusSelect, { target: { value: 'Success' } });
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(statusSelect, { target: { value: 'Failure' } });

    await waitFor(() => expect(signals[0]?.aborted).toBe(true));
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(2));
    resolvePending.forEach((resolve) => resolve(emptyResponse));
  });

  it('loads a changed search directly on the first page', async () => {
    vi.mocked(apiService.getUserActivityLogs).mockResolvedValue({
      ...emptyResponse,
      items: [activityItem],
      total: 60,
    });
    render(<UserActivity />);
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(2));
    expect(apiService.getUserActivityLogs).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    vi.mocked(apiService.getUserActivityLogs).mockClear();

    fireEvent.click(screen.getByRole('button', { name: /Показать фильтры/u }));
    fireEvent.change(screen.getByPlaceholderText('Описание, объект, раздел'), {
      target: { value: 'новый поиск' },
    });

    await waitFor(() => expect(apiService.getUserActivityLogs).toHaveBeenCalledTimes(1), { timeout: 2_000 });
    expect(apiService.getUserActivityLogs).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'новый поиск', page: 1 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('does not request activity data without administrator rights', async () => {
    authState.admin = false;

    render(<UserActivity />);

    expect(screen.getByText('Доступ запрещён')).toBeTruthy();
    expect(apiService.getUserActivityLogs).not.toHaveBeenCalled();
    expect(apiService.getUserActivityFilters).not.toHaveBeenCalled();
  });
});
