import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseBackupDiscovery } from './DatabaseBackupDiscovery';

const { discover } = vi.hoisted(() => ({ discover: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: { discoverDatabaseBackups: discover } }));

describe('DatabaseBackupDiscovery', () => {
  beforeEach(() => { discover.mockReset(); });
  it('loads on explicit action and passes only unregistered files with a manifest to verification', async () => {
    const onSelect = vi.fn();
    discover.mockResolvedValue({ items: [
      { relativePath: 'old.zip', hasManifest: true, alreadyRegistered: false },
      { relativePath: 'legacy.sql', hasManifest: false, alreadyRegistered: false },
      { relativePath: 'known.sqlite', hasManifest: true, alreadyRegistered: true },
    ], issues: [], nextCursor: null });
    render(<DatabaseBackupDiscovery disabled={false} onSelect={onSelect} />);
    expect(discover).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    await screen.findByText(/Описание найдено, нужна проверка/);
    expect(onSelect).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /Передать найденные/ }));
    expect(onSelect).toHaveBeenCalledWith(['old.zip']);
    expect(screen.getByText(/Нет описания комплекта/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Продолжить поиск' }).hasAttribute('disabled')).toBe(true);
  });

  it('continues an empty page and retains warnings from earlier directories', async () => {
    discover.mockResolvedValueOnce({ items: [], issues: [{ relativePath: 'denied', error: 'Недоступен' }], nextCursor: 'next' })
      .mockResolvedValueOnce({ items: [], issues: [], nextCursor: null });
    render(<DatabaseBackupDiscovery disabled={false} onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    await screen.findByText(/Есть продолжение/);
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить поиск' }));
    await screen.findByText(/Обход доступных каталогов завершён/);
    expect(discover).toHaveBeenLastCalledWith('next');
    expect(screen.getByText('denied: Недоступен')).toBeTruthy();
    expect(screen.getByText(/Всего проблем поиска: 1/)).toBeTruthy();
  });

  it('clears stale results on error and disables selection during import', async () => {
    let reject!: (error: Error) => void;
    discover.mockResolvedValueOnce({ items: [{ relativePath: 'stale.zip', hasManifest: true, alreadyRegistered: false }], issues: [], nextCursor: 'next' })
      .mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail; }));
    const view = render(<DatabaseBackupDiscovery disabled={true} onSelect={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Найти копии в каталоге' }).hasAttribute('disabled')).toBe(true);
    view.rerender(<DatabaseBackupDiscovery disabled={false} onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    await screen.findByText(/stale.zip/);
    view.rerender(<DatabaseBackupDiscovery disabled={true} onSelect={vi.fn()} />);
    expect(screen.getByRole('button', { name: /Передать найденные/ }).hasAttribute('disabled')).toBe(true);
    view.rerender(<DatabaseBackupDiscovery disabled={false} onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить поиск' }));
    await act(async () => { reject(new Error('Ошибка чтения')); });
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Ошибка чтения'));
    expect(screen.queryByText(/stale.zip/)).toBeNull();
    expect(screen.getByRole('button', { name: /Передать найденные/ }).hasAttribute('disabled')).toBe(true);
  });

  it('ignores late responses after unmount and rejects parallel clicks', async () => {
    let resolve!: (value: unknown) => void;
    discover.mockReturnValue(new Promise(done => { resolve = done; }));
    const view = render(<DatabaseBackupDiscovery disabled={false} onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    await waitFor(() => expect(discover).toHaveBeenCalledTimes(1));
    view.unmount();
    await act(async () => resolve({ items: [], issues: [], nextCursor: null }));
  });
});
