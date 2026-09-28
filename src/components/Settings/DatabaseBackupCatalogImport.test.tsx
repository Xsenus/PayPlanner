import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseBackupCatalogImport } from './DatabaseBackupCatalogImport';

const { importCatalog, discover, confirm } = vi.hoisted(() => ({ importCatalog: vi.fn(), discover: vi.fn(), confirm: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: { importDatabaseBackupCatalog: importCatalog, discoverDatabaseBackups: discover } }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));

function setup() {
  const onImported = vi.fn();
  const view = render(<DatabaseBackupCatalogImport onImported={onImported} />);
  fireEvent.click(screen.getByText('Добавить старые локальные копии'));
  return { ...view, onImported, input: screen.getByLabelText('Пути старых копий'),
    preview: () => screen.getByRole('button', { name: 'Проверить перед добавлением' }),
    apply: () => screen.getByRole('button', { name: /Добавить проверенные/ }) };
}

describe('DatabaseBackupCatalogImport', () => {
  beforeEach(() => { importCatalog.mockReset(); discover.mockReset(); confirm.mockReset(); confirm.mockResolvedValue(true); });

  it('requires a new preview after discovered paths replace a verified manual selection', async () => {
    importCatalog.mockResolvedValue({ dryRun: true, items: [{ relativePath: 'manual.zip', status: 'ready' }] });
    discover.mockResolvedValue({ items: [{ relativePath: 'found.zip', hasManifest: true, alreadyRegistered: false }], issues: [], nextCursor: null });
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'manual.zip' } });
    fireEvent.click(view.preview());
    await waitFor(() => expect(view.apply().hasAttribute('disabled')).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Найти копии в каталоге' }));
    await screen.findByText(/found.zip/);
    fireEvent.click(screen.getByRole('button', { name: /Передать найденные/ }));
    expect((view.input as HTMLTextAreaElement).value).toBe('found.zip');
    expect(screen.queryByText('Можно добавить')).toBeNull();
    expect(view.apply().hasAttribute('disabled')).toBe(true);
    expect(importCatalog).toHaveBeenCalledTimes(1);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('previews without writes and applies only ready files after confirmation', async () => {
    importCatalog.mockResolvedValueOnce({ dryRun: true, items: [
      { relativePath: 'valid.zip', status: 'ready' }, { relativePath: 'broken.zip', status: 'failed', error: 'Нет файловой части' },
      { relativePath: 'known.zip', status: 'alreadyRegistered' },
    ] }).mockResolvedValueOnce({ dryRun: false, items: [{ relativePath: 'valid.zip', status: 'imported' }] });
    const view = setup();
    fireEvent.change(view.input, { target: { value: ' valid.zip\nbroken.zip\nvalid.zip\nknown.zip' } });
    fireEvent.click(view.preview());
    await screen.findByText('Нет файловой части');
    expect(importCatalog).toHaveBeenCalledWith(['valid.zip', 'broken.zip', 'known.zip'], true);
    expect(confirm).not.toHaveBeenCalled();
    expect(view.onImported).not.toHaveBeenCalled();
    fireEvent.click(view.apply());
    await waitFor(() => expect(importCatalog).toHaveBeenLastCalledWith(['valid.zip'], false));
    await waitFor(() => expect(view.onImported).toHaveBeenCalledTimes(1));
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(view.apply().hasAttribute('disabled')).toBe(true);
  });

  it('does not apply when confirmation is cancelled', async () => {
    importCatalog.mockResolvedValue({ dryRun: true, items: [{ relativePath: 'valid.zip', status: 'ready' }] });
    confirm.mockResolvedValue(false);
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'valid.zip' } });
    fireEvent.click(view.preview());
    await waitFor(() => expect(view.apply().hasAttribute('disabled')).toBe(false));
    fireEvent.click(view.apply());
    await waitFor(() => expect(view.preview().hasAttribute('disabled')).toBe(false));
    expect(importCatalog).toHaveBeenCalledTimes(1);
    expect(view.onImported).not.toHaveBeenCalled();
  });

  it('resets the verified selection when paths change and limits the batch', async () => {
    importCatalog.mockResolvedValue({ dryRun: true, items: [{ relativePath: 'valid.zip', status: 'ready' }] });
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'valid.zip' } });
    fireEvent.click(view.preview());
    await waitFor(() => expect(view.apply().hasAttribute('disabled')).toBe(false));
    fireEvent.change(view.input, { target: { value: Array.from({ length: 21 }, (_, index) => `${index}.zip`).join('\n') } });
    expect(view.apply().hasAttribute('disabled')).toBe(true);
    expect(view.preview().hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('alert').textContent).toContain('20');
    expect(importCatalog).toHaveBeenCalledTimes(1);
  });

  it('shows a request error and allows another preview', async () => {
    importCatalog.mockRejectedValue(new Error('Связь прервана'));
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'valid.zip' } });
    fireEvent.click(view.preview());
    expect((await screen.findByRole('alert')).textContent).toContain('Связь прервана');
    expect(view.preview().hasAttribute('disabled')).toBe(false);
    expect(view.apply().hasAttribute('disabled')).toBe(true);
  });

  it('blocks parallel requests and ignores a response after unmount', async () => {
    let resolve!: (value: unknown) => void;
    importCatalog.mockReturnValue(new Promise(done => { resolve = done; }));
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'valid.zip' } });
    fireEvent.click(view.preview());
    fireEvent.click(view.preview());
    expect(importCatalog).toHaveBeenCalledTimes(1);
    expect(view.input.hasAttribute('disabled')).toBe(true);
    view.unmount();
    await act(async () => resolve({ dryRun: false, items: [{ relativePath: 'valid.zip', status: 'imported' }] }));
    expect(view.onImported).not.toHaveBeenCalled();
  });

  it('keeps server-side failures visible if a file changes after preview', async () => {
    importCatalog.mockResolvedValueOnce({ dryRun: true, items: [{ relativePath: 'valid.zip', status: 'ready' }] })
      .mockResolvedValueOnce({ dryRun: false, items: [{ relativePath: 'valid.zip', status: 'failed', error: 'Контрольная сумма изменилась' }] });
    const view = setup();
    fireEvent.change(view.input, { target: { value: 'valid.zip' } });
    fireEvent.click(view.preview());
    await waitFor(() => expect(view.apply().hasAttribute('disabled')).toBe(false));
    fireEvent.click(view.apply());
    await screen.findByText('Контрольная сумма изменилась');
    expect(view.onImported).not.toHaveBeenCalled();
    expect(view.apply().hasAttribute('disabled')).toBe(true);
  });
});
