import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import type { DocumentImagePrintOptions } from '../../types/documentImagePrint';
import { DocumentImagePrintDialog } from './DocumentImagePrintDialog';

const empty = { includeLogo: false, includeSeal: false, includeSignature: false };
const source = { section: 'invoices' as const, documentId: 7 };
const deferred = () => {
  let resolve!: (value: DocumentImagePrintOptions) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<DocumentImagePrintOptions>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
};
const confirmButton = () => screen.getByRole('button', { name: 'Скачать PDF' }) as HTMLButtonElement;
const flags = () => Object.fromEntries(['Логотип', 'Печать', 'Подпись'].map(label => [label, (screen.getByLabelText(label) as HTMLInputElement).checked]));

describe('document image defaults loading', () => {
  beforeEach(() => vi.spyOn(apiService, 'getDocumentImagePrintDefaults').mockResolvedValue(empty));
  afterEach(() => vi.restoreAllMocks());
  const open = (onConfirm = vi.fn().mockResolvedValue(undefined), onClose = vi.fn(), defaultsSource = source) =>
    render(<DocumentImagePrintDialog documentName="Счёт № 7" initialOptions={empty} defaultsSource={defaultsSource}
      confirmLabel="Скачать PDF" onConfirm={onConfirm} onClose={onClose} />);

  it.each(Array.from({ length: 8 }, (_, mask) => mask))('loads independent combination %s before explicit confirmation', async mask => {
    const pending = deferred();
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(pending.promise);
    const confirm = vi.fn().mockResolvedValue(undefined); const close = vi.fn();
    open(confirm, close);
    expect(confirmButton().disabled).toBe(true);
    expect(screen.getByRole('status').textContent).toContain('Загружаем');
    fireEvent.submit(screen.getByRole('dialog')); expect(confirm).not.toHaveBeenCalled();
    const value = Object.freeze({ includeLogo: !!(mask & 1), includeSeal: !!(mask & 2), includeSignature: !!(mask & 4) });
    await act(async () => pending.resolve(value));
    expect(confirmButton().disabled).toBe(false);
    expect(flags()).toEqual({ Логотип: value.includeLogo, Печать: value.includeSeal, Подпись: value.includeSignature });
    expect(confirm).not.toHaveBeenCalled(); expect(close).not.toHaveBeenCalled();
    fireEvent.click(screen.getByLabelText('Логотип')); fireEvent.click(confirmButton());
    await waitFor(() => expect(confirm).toHaveBeenCalledExactlyOnceWith({ ...value, includeLogo: !value.includeLogo }, expect.any(AbortSignal)));
    expect(value.includeLogo).toBe(!!(mask & 1));
  });

  it.each(['invoices', 'acts', 'contracts'] as const)('requests only the selected %s defaults', async section => {
    render(<DocumentImagePrintDialog documentName="Документ" initialOptions={empty} defaultsSource={{ section, documentId: 9 }}
      confirmLabel="Скачать PDF" onConfirm={vi.fn()} onClose={vi.fn()} />);
    await waitFor(() => expect(confirmButton().disabled).toBe(false));
    expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledExactlyOnceWith(section, 9, expect.any(AbortSignal));
  });

  it('keeps printing blocked after an access error and retries only when requested', async () => {
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockRejectedValueOnce(new Error('Недостаточно прав'));
    const confirm = vi.fn(); open(confirm);
    expect((await screen.findByRole('alert')).textContent).toContain('Недостаточно прав');
    expect(confirmButton().disabled).toBe(true);
    fireEvent.submit(screen.getByRole('dialog')); expect(confirm).not.toHaveBeenCalled();
    expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledTimes(1);
    const first = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockResolvedValueOnce({ ...empty, includeSeal: true });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    await waitFor(() => expect(confirmButton().disabled).toBe(false));
    expect(first.aborted).toBe(true);
    expect(vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[1][2]).not.toBe(first);
    expect(flags()).toEqual({ Логотип: false, Печать: true, Подпись: false });
    expect(confirm).not.toHaveBeenCalled();
  });

  it.each([undefined, null, { includeLogo: true }, { ...empty, includeSeal: 'false' }])('refuses malformed defaults %j', async value => {
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockResolvedValueOnce(value as DocumentImagePrintOptions);
    const confirm = vi.fn(); open(confirm);
    expect((await screen.findByRole('alert')).textContent).toContain('Не удалось прочитать набор');
    expect(confirmButton().disabled).toBe(true);
    fireEvent.submit(screen.getByRole('dialog')); expect(confirm).not.toHaveBeenCalled();
  });

  it.each(['close', 'unmount'] as const)('cancels defaults and ignores a late response after %s', async action => {
    const pending = deferred(); vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(pending.promise);
    const confirm = vi.fn(); const close = vi.fn(); const view = open(confirm, close);
    const signal = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
    if (action === 'close') fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); else view.unmount();
    expect(signal.aborted).toBe(true);
    await act(async () => pending.resolve({ ...empty, includeLogo: true }));
    expect(confirm).not.toHaveBeenCalled(); expect(close).toHaveBeenCalledTimes(action === 'close' ? 1 : 0);
    expect(screen.queryByRole('alert')).toBeNull();
    if (action === 'close') expect(flags().Логотип).toBe(false);
  });

  it.each(['success', 'failure'] as const)('rejects the former document’s late %s after a source switch', async outcome => {
    const previous = deferred(); const current = deferred();
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(previous.promise).mockReturnValueOnce(current.promise);
    const confirm = vi.fn(); const view = open(confirm);
    const previousSignal = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
    view.rerender(<DocumentImagePrintDialog documentName="Счёт № 8" initialOptions={empty} defaultsSource={{ ...source, documentId: 8 }}
      confirmLabel="Скачать PDF" onConfirm={confirm} onClose={vi.fn()} />);
    expect(previousSignal.aborted).toBe(true); expect(confirmButton().disabled).toBe(true);
    fireEvent.submit(screen.getByRole('dialog')); expect(confirm).not.toHaveBeenCalled();
    await act(async () => current.resolve({ ...empty, includeSeal: true }));
    await act(async () => { if (outcome === 'success') previous.resolve({ ...empty, includeLogo: true }); else previous.reject(new Error('Ошибка прежнего документа')); });
    expect(flags()).toEqual({ Логотип: false, Печать: true, Подпись: false });
    expect(screen.queryByRole('alert')).toBeNull(); expect(confirmButton().disabled).toBe(false);
  });

  it('does not reload or overwrite per-copy edits on an unrelated parent render', async () => {
    const confirm = vi.fn(); const view = open(confirm);
    await waitFor(() => expect(confirmButton().disabled).toBe(false));
    fireEvent.click(screen.getByLabelText('Подпись'));
    view.rerender(<DocumentImagePrintDialog documentName="Счёт № 7" initialOptions={{ ...empty }} defaultsSource={{ ...source }}
      confirmLabel="Скачать PDF" onConfirm={confirm} onClose={vi.fn()} />);
    expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledTimes(1);
    expect(flags().Подпись).toBe(true);
  });

  it('preserves a loaded and edited choice after a document error without reloading defaults', async () => {
    const confirm = vi.fn().mockRejectedValueOnce(new Error('Документ изменился')).mockResolvedValue(undefined); open(confirm);
    await waitFor(() => expect(confirmButton().disabled).toBe(false));
    fireEvent.click(screen.getByLabelText('Подпись')); fireEvent.click(confirmButton());
    expect((await screen.findByRole('alert')).textContent).toContain('Документ изменился');
    expect(flags().Подпись).toBe(true); expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledTimes(1);
    fireEvent.click(confirmButton()); await waitFor(() => expect(confirm).toHaveBeenCalledTimes(2));
    expect(confirm.mock.calls[1][0]).toEqual({ ...empty, includeSignature: true });
  });

  it('ignores the cancelled StrictMode startup response', async () => {
    const old = deferred(); vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(old.promise).mockResolvedValueOnce({ ...empty, includeSeal: true });
    render(<StrictMode><DocumentImagePrintDialog documentName="Документ" initialOptions={empty} defaultsSource={source}
      confirmLabel="Скачать PDF" onConfirm={vi.fn()} onClose={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(confirmButton().disabled).toBe(false));
    expect(vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!.aborted).toBe(true);
    await act(async () => old.resolve({ ...empty, includeLogo: true }));
    expect(flags()).toEqual({ Логотип: false, Печать: true, Подпись: false });
  });
});
