import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ObjectAuditPage } from './ObjectAuditPage';
import { apiService, type ObjectAuditEventDto } from '../../services/api';

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Moscow',
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getObjectAuditEvents: vi.fn(),
  },
}));

function createEvent(id: number, description: string): ObjectAuditEventDto {
  return {
    id,
    objectType: 'client',
    objectId: id,
    action: 'created',
    description,
    actorUserName: 'Администратор',
    createdAt: '2026-07-17T10:00:00Z',
  };
}

describe('ObjectAuditPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('aborts an obsolete filtered request and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: {
      items: ObjectAuditEventDto[];
      total: number;
      page: number;
      pageSize: number;
    }) => void> = [];
    vi.mocked(apiService.getObjectAuditEvents).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });

    render(<ObjectAuditPage />);

    await waitFor(() => expect(apiService.getObjectAuditEvents).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(screen.getByLabelText('Объект'), { target: { value: 'client' } });

    await waitFor(() => expect(apiService.getObjectAuditEvents).toHaveBeenCalledTimes(2));
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await act(async () => {
      resolvers[1]({
        items: [createEvent(2, 'Текущее событие')],
        total: 1,
        page: 1,
        pageSize: 25,
      });
    });
    expect(await screen.findByText('Текущее событие')).toBeTruthy();

    await act(async () => {
      resolvers[0]({
        items: [createEvent(1, 'Устаревшее событие')],
        total: 1,
        page: 1,
        pageSize: 25,
      });
    });
    expect(screen.queryByText('Устаревшее событие')).toBeNull();
    expect(screen.getByText('Текущее событие')).toBeTruthy();
  });

  it('changes filters from a later page without requesting that obsolete page', async () => {
    vi.mocked(apiService.getObjectAuditEvents).mockImplementation(async (params) => ({
      items: [createEvent(params?.page ?? 1, `Событие страницы ${params?.page ?? 1}`)],
      total: 50,
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 25,
    }));

    render(<ObjectAuditPage />);

    expect(await screen.findByText('Событие страницы 1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Вперёд' }));
    await waitFor(() => {
      expect(apiService.getObjectAuditEvents).toHaveBeenCalledTimes(2);
      expect(apiService.getObjectAuditEvents).toHaveBeenLastCalledWith(
        expect.objectContaining({ page: 2 }),
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );
    });

    fireEvent.change(screen.getByLabelText('Объект'), { target: { value: 'client' } });

    await waitFor(() => expect(apiService.getObjectAuditEvents).toHaveBeenCalledTimes(3));
    expect(apiService.getObjectAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ objectType: 'client', page: 1 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(
      vi.mocked(apiService.getObjectAuditEvents).mock.calls.some(
        ([params]) => params?.objectType === 'client' && params.page === 2,
      ),
    ).toBe(false);
  });

  it('aborts the active request when the page unmounts', async () => {
    let signal: AbortSignal | undefined;
    vi.mocked(apiService.getObjectAuditEvents).mockImplementation((_params, options) => {
      signal = options?.signal;
      return new Promise(() => undefined);
    });

    const { unmount } = render(<ObjectAuditPage />);

    await waitFor(() => expect(apiService.getObjectAuditEvents).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
