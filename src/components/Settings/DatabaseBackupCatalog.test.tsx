import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseBackupCatalog } from './DatabaseBackupCatalog';
import type { DatabaseBackupCatalogItemDto } from '../../services/api';

const { getCatalog } = vi.hoisted(() => ({ getCatalog: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: { getDatabaseBackupCatalog: getCatalog } }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));

function item(id: string): DatabaseBackupCatalogItemDto {
  return {
    id, backupRelativePath: `${id}.zip`, databaseProvider: 'postgresql', backupFormat: 'zip',
    status: 'noLocalCopy', createdAt: '2026-09-28T00:00:00Z',
    copies: [
      { providerKey: 'local', connectionId: '', locator: `${id}.zip`, status: 'deleted', attemptCount: 0, updatedAt: '2026-09-28T00:00:00Z' },
      { providerKey: 's3', connectionId: 'main', locator: `${id}.zip`, status: 'verified', attemptCount: 2,
        lastVerifiedAt: '2026-09-28T00:00:00Z', updatedAt: '2026-09-28T00:00:00Z' },
    ],
  };
}

describe('DatabaseBackupCatalog', () => {
  beforeEach(() => { getCatalog.mockReset(); });

  it.each([
    ['localVerified', 'Подтверждена локальная целостность'],
    ['replicating', 'Отправка не завершена'],
    ['awaitingCopies', 'Нет всех выбранных копий'],
    ['verifiedConfigured', 'Выбранные копии подтверждены'],
    ['degraded', 'Есть ошибка или неполная настройка'],
    ['unknown', 'Состояние комплекта не определено'],
  ])('shows the configured-destination state %s separately from individual history', async (status, label) => {
    getCatalog.mockResolvedValue({ items: [{ ...item('state'), status }], nextCursor: null });
    render(<DatabaseBackupCatalog />);
    expect(await screen.findByText(label)).toBeTruthy();
    expect(screen.getByText('Проверено после отправки')).toBeTruthy();
    expect(screen.getByText(/не подтверждение текущей доступности/)).toBeTruthy();
  });

  it('shows per-copy verification without claiming a deleted local file is available', async () => {
    getCatalog.mockResolvedValue({ items: [item('first')], nextCursor: null });
    render(<DatabaseBackupCatalog timeZoneId="Europe/Samara" connectionNames={{ main: 'Основной архив' }} />);
    expect(await screen.findByText('first.zip')).toBeTruthy();
    expect(screen.getByText('Нет подтверждённой локальной копии')).toBeTruthy();
    expect(screen.getByText('S3: Основной архив')).toBeTruthy();
    expect(screen.getByText('Проверено после отправки')).toBeTruthy();
    expect(screen.getByText(/Попыток: 2/).textContent).toContain('04:00');
    expect(screen.getByText(/не подтверждена/)).toBeTruthy();
    expect(screen.getByText(/не подтверждение текущей доступности/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Следующие копии' }).hasAttribute('disabled')).toBe(true);
  });

  it('loads only one page and returns using the previous cursor', async () => {
    getCatalog.mockResolvedValueOnce({ items: [item('first')], nextCursor: 'first' })
      .mockResolvedValueOnce({ items: [item('second')], nextCursor: null })
      .mockResolvedValueOnce({ items: [item('first')], nextCursor: 'first' });
    render(<DatabaseBackupCatalog />);
    await screen.findByText('first.zip');
    fireEvent.click(screen.getByRole('button', { name: 'Следующие копии' }));
    await screen.findByText('second.zip');
    expect(screen.queryByText('first.zip')).toBeNull();
    expect(getCatalog).toHaveBeenLastCalledWith('first');
    expect(screen.getByText('Страница 2')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await screen.findByText('first.zip');
    expect(getCatalog).toHaveBeenLastCalledWith(undefined);
    expect(screen.getByRole('button', { name: 'Назад' }).hasAttribute('disabled')).toBe(true);
  });

  it('shows an error instead of stale rows and refreshes from the first page', async () => {
    getCatalog.mockResolvedValueOnce({ items: [item('first')], nextCursor: 'first' })
      .mockRejectedValueOnce(new Error('Связь прервана'))
      .mockResolvedValueOnce({ items: [], nextCursor: null });
    render(<DatabaseBackupCatalog />);
    await screen.findByText('first.zip');
    fireEvent.click(screen.getByRole('button', { name: 'Следующие копии' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Связь прервана');
    expect(screen.queryByText('first.zip')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить журнал' }));
    await screen.findByText(/Зарегистрированных комплектов пока нет/);
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText('Страница 1')).toBeTruthy();
    expect(getCatalog).toHaveBeenLastCalledWith(undefined);
  });

  it('ignores a late response after the catalog was remounted', async () => {
    let resolveOld!: (value: unknown) => void;
    getCatalog.mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve; }))
      .mockResolvedValueOnce({ items: [item('new')], nextCursor: null });
    const view = render(<DatabaseBackupCatalog key="old" />);
    await waitFor(() => expect(getCatalog).toHaveBeenCalledTimes(1));
    view.rerender(<DatabaseBackupCatalog key="new" />);
    await screen.findByText('new.zip');
    resolveOld({ items: [item('old')], nextCursor: null });
    await waitFor(() => expect(screen.queryByText('old.zip')).toBeNull());
    expect(screen.getByText('new.zip')).toBeTruthy();
  });
});
