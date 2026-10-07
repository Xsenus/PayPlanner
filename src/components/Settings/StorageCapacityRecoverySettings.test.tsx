import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { StorageCapacityRecoverySettings } from './StorageCapacityRecoverySettings';

const mocks = vi.hoisted(() => ({
  confirm: vi.fn(),
  success: vi.fn(),
  getHealth: vi.fn(),
  reconcile: vi.fn(),
  reconcileMarkers: vi.fn(),
}));

vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: mocks.confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: mocks.success }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));
vi.mock('../../services/api', () => ({
  apiService: {
    getStorageCapacityHealth: mocks.getHealth,
    reconcileStorageStages: mocks.reconcile,
    reconcileStorageStageMarkers: mocks.reconcileMarkers,
  },
}));

const eligible = { key: 'stage:' + 'a'.repeat(64), status: 'eligible', relativePath: '.task-uploads/complete.tmp',
  expectedBytes: 4, observedBytes: 4, updatedAt: '2026-09-29T12:00:00Z' };
const partial = { key: 'stage:' + 'b'.repeat(64), status: 'incompleteFile', relativePath: '.task-uploads/partial.tmp',
  expectedBytes: 4, observedBytes: 2, updatedAt: '2026-09-29T12:00:00Z' };

describe('StorageCapacityRecoverySettings', () => {
  it('previews released markers independently and confirms only their removal', async () => {
    const marker = { ...eligible, key: 'stage:writer-v1:' + 'e'.repeat(64), status: 'eligibleReleasedMarker' };
    mocks.reconcileMarkers.mockResolvedValueOnce({ inspected: 1, eligible: 1, removed: 0, hasMore: false, items: [marker] });
    render(<StorageCapacityRecoverySettings releasedMarkers />);
    expect(await screen.findByText('Резерв освобождён — служебный маркер можно удалить')).toBeTruthy();
    expect(mocks.getHealth).not.toHaveBeenCalled();
    expect(mocks.reconcile).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /Очистить маркеры/ }));
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledTimes(1));
    expect(mocks.reconcileMarkers).toHaveBeenCalledTimes(1);
    mocks.confirm.mockResolvedValueOnce(true);
    mocks.reconcileMarkers.mockResolvedValueOnce({ inspected: 1, eligible: 1, removed: 1, hasMore: false,
      items: [{ ...marker, status: 'releasedMarkerRemoved' }] });
    mocks.reconcileMarkers.mockResolvedValueOnce({ inspected: 1, eligible: 0, removed: 0, hasMore: false,
      items: [{ ...marker, status: 'markerAlreadyAbsent' }] });
    fireEvent.click(screen.getByRole('button', { name: /Очистить маркеры/ }));
    await waitFor(() => expect(mocks.reconcileMarkers).toHaveBeenCalledWith({ apply: true, afterKey: null, limit: 50 }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledWith('Удалено служебных маркеров: 1.', 'Хранилище'));
    expect(mocks.confirm.mock.calls[1][0].description).toContain('Документы и содержимое временных файлов сохранятся');
  });

  it('keeps unproven markers and disables removal after a failed fresh preview', async () => {
    mocks.reconcileMarkers.mockResolvedValueOnce({ inspected: 1, eligible: 1, removed: 0, hasMore: false,
      items: [{ ...eligible, status: 'eligibleReleasedMarker' }] });
    render(<StorageCapacityRecoverySettings releasedMarkers />);
    expect(await screen.findByText('Резерв освобождён — служебный маркер можно удалить')).toBeTruthy();
    mocks.reconcileMarkers.mockRejectedValueOnce(new Error('Недоступно'));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: /Очистить маркеры/ }).disabled).toBe(true);
    expect(screen.queryByText('Резерв освобождён — служебный маркер можно удалить')).toBeNull();
    mocks.reconcileMarkers.mockResolvedValueOnce({ inspected: 1, eligible: 0, removed: 0, hasMore: false,
      items: [{ ...eligible, status: 'markerNotProvenStopped' }] });
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    expect(await screen.findByText('Запись или подлинность маркера не подтверждены — маркер сохранён')).toBeTruthy();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: /Очистить маркеры/ }).disabled).toBe(true);
  });

  it('pages released marker metadata with the server cursor', async () => {
    const marker = { ...eligible, key: 'stage:writer-v1:' + 'e'.repeat(64), status: 'markerAlreadyAbsent' };
    mocks.reconcileMarkers.mockImplementation(async ({ afterKey }: { afterKey?: string | null }) => ({
      inspected: 1, eligible: 0, removed: 0, hasMore: !afterKey, nextAfterKey: afterKey ? null : marker.key,
      items: [marker],
    }));
    render(<StorageCapacityRecoverySettings releasedMarkers />);
    expect(await screen.findByText('Служебного маркера нет')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Далее' }));
    await waitFor(() => expect(mocks.reconcileMarkers).toHaveBeenCalledWith({ afterKey: marker.key, limit: 50 }));
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await waitFor(() => expect(mocks.reconcileMarkers).toHaveBeenLastCalledWith({ afterKey: null, limit: 50 }));
  });

  it('retains the reservation when a writer has not been proven stopped', async () => {
    mocks.reconcile.mockResolvedValueOnce({ inspected: 1, eligible: 0, settled: 0, retained: 1,
      hasMore: false, items: [{ ...partial, key: 'stage:writer-v1:' + 'c'.repeat(64), status: 'writerNotProvenStopped' }] });
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Запись файла может продолжаться — резерв сохранён')).toBeTruthy();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: /Подтвердить восстановление/ }).disabled).toBe(true);
  });

  it('confirms an absent file only when the server proves its writer stopped', async () => {
    const missing = { ...partial, key: 'stage:writer-v1:' + 'd'.repeat(64), status: 'eligibleMissingStage', observedBytes: null };
    mocks.reconcile.mockResolvedValueOnce({ inspected: 1, eligible: 1, settled: 0, retained: 1,
      hasMore: false, items: [missing] });
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Файл отсутствует, запись остановлена — резерв можно освободить')).toBeTruthy();
    expect(screen.getByText('Проверено: 1. Можно восстановить резервов: 1. Без изменений: 1.')).toBeTruthy();
    mocks.confirm.mockResolvedValueOnce(true);
    mocks.reconcile.mockResolvedValueOnce({ inspected: 1, eligible: 1, settled: 1, retained: 0,
      hasMore: false, items: [{ ...missing, status: 'releasedMissingStage' }] });
    mocks.reconcile.mockResolvedValueOnce({ inspected: 0, eligible: 0, settled: 0, retained: 0,
      hasMore: false, items: [] });
    fireEvent.click(screen.getByRole('button', { name: /Подтвердить восстановление/ }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledWith('Восстановлено резервов: 1.', 'Хранилище'));
    expect(mocks.confirm.mock.calls[0][0].description).toContain('отсутствие файлов остановившихся загрузок');
  });

  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getHealth.mockResolvedValue({
      status: 'degraded', pendingCapacityReservations: 2,
      oldestCapacityReservationAt: '2026-09-29T12:00:00Z',
      pendingStageCapacityReservations: 1,
      oldestStageCapacityReservationAt: '2026-09-29T12:00:00Z',
    });
    mocks.reconcile.mockResolvedValue({
      inspected: 2, eligible: 1, settled: 0, retained: 2,
      hasMore: false, nextAfterKey: null, items: [eligible, partial],
    });
    mocks.confirm.mockResolvedValue(false);
  });

  it('previews reservations without applying until the administrator confirms', async () => {
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Файл неполный — резерв сохранён')).toBeTruthy();
    expect(screen.getByText(partial.key)).toBeTruthy();
    expect(screen.getByText('Файл: .task-uploads/partial.tmp')).toBeTruthy();
    expect(screen.getByText(/найдено: 2 Б/)).toBeTruthy();
    expect(screen.getByText(/Ожидающих резервов всех загрузок:/)).toBeTruthy();
    expect(screen.getByText(/Из них для временных файлов:/)).toBeTruthy();
    expect(mocks.reconcile).toHaveBeenCalledWith({ afterKey: null, limit: 50 });
    fireEvent.click(screen.getByRole('button', { name: /Подтвердить восстановление/ }));
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledTimes(1));
    expect(mocks.reconcile).toHaveBeenCalledTimes(1);

    mocks.confirm.mockResolvedValue(true);
    mocks.reconcile.mockResolvedValueOnce({
      inspected: 2, eligible: 1, settled: 1, retained: 1,
      hasMore: false, nextAfterKey: null, items: [{ ...eligible, status: 'settled' }, partial],
    });
    fireEvent.click(screen.getByRole('button', { name: /Подтвердить восстановление/ }));
    await waitFor(() => expect(mocks.reconcile).toHaveBeenCalledWith({ apply: true, afterKey: null, limit: 50 }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledWith('Восстановлено резервов: 1.', 'Хранилище'));
  });

  it('uses the server cursor to reach later reservations', async () => {
    mocks.reconcile.mockImplementation(async ({ afterKey }: { afterKey?: string | null }) => ({
      inspected: 1, eligible: 0, settled: 0, retained: 1,
      hasMore: !afterKey, nextAfterKey: afterKey ? null : partial.key,
      items: [partial],
    }));
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Файл неполный — резерв сохранён')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Далее' }));
    await waitFor(() => expect(mocks.reconcile).toHaveBeenCalledWith({ afterKey: partial.key, limit: 50 }));
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await waitFor(() => expect(mocks.reconcile).toHaveBeenLastCalledWith({ afterKey: null, limit: 50 }));
  });

  it('does not allow applying an old preview after refresh fails', async () => {
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Полный файл — можно подтвердить')).toBeTruthy();
    mocks.reconcile.mockRejectedValueOnce(new Error('Сервер недоступен'));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('Полный файл — можно подтвердить')).toBeNull();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: /Подтвердить восстановление/ }).disabled).toBe(true);
  });

  it('renders an unsafe reservation when the server omits nullable fields', async () => {
    mocks.reconcile.mockResolvedValueOnce({
      inspected: 1, eligible: 0, settled: 0, retained: 1,
      hasMore: false, items: [{ key: 'stage:' + 'c'.repeat(64), status: 'invalidAuthority',
        expectedBytes: 4, updatedAt: '2026-09-29T12:00:00Z' }],
    });
    render(<StorageCapacityRecoverySettings />);
    expect(await screen.findByText('Путь требует разбора — резерв сохранён')).toBeTruthy();
    expect(screen.getByText('stage:' + 'c'.repeat(64))).toBeTruthy();
    expect(screen.queryByText(/найдено:/)).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
