import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { DocumentImagePrintOptions } from '../../types/documentImagePrint';
import { DocumentImagePrintDialog } from './DocumentImagePrintDialog';

vi.mock('../../services/api', () => ({ apiService: { getDocumentImagePrintDefaults: vi.fn() } }));
const empty = { includeLogo: false, includeSeal: false, includeSignature: false };
function deferred<T>() {
  let resolve!: (value: T) => void; let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const open = (onConfirm = vi.fn().mockResolvedValue(undefined), onClose = vi.fn()) => render(<DocumentImagePrintDialog
  documentName="Счёт 7" initialOptions={empty} defaultsSource={{ section: 'invoices', documentId: 7 }} confirmLabel="Скачать PDF" onConfirm={onConfirm} onClose={onClose} />);
const button = () => screen.getByRole('button', { name: 'Скачать PDF' }) as HTMLButtonElement;

describe('copy session', () => {
  beforeEach(() => vi.mocked(apiService.getDocumentImagePrintDefaults).mockReset().mockResolvedValue(empty));

  it.each(['login', 'actor', 'logout'] as const)('rejects a confirm after %s before React receives auth changes', async mode => {
    const session = documentCopyTestSession(); const confirm = vi.fn();
    try {
      open(confirm); await waitFor(() => expect(button().disabled).toBe(false));
      session.replace(mode); fireEvent.submit(screen.getByRole('dialog'));
      expect(confirm).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it.each(['success', 'error'] as const)('ignores a late defaults %s from the former login', async result => {
    const session = documentCopyTestSession(); const pending = deferred<DocumentImagePrintOptions>();
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(pending.promise);
    try {
      open(); session.replace();
      await act(async () => { if (result === 'error') pending.reject(new Error('Ошибка прежнего входа')); else pending.resolve({ ...empty, includeSeal: true }); });
      expect((screen.getByLabelText('Печать') as HTMLInputElement).checked).toBe(false);
      expect(screen.queryByText('Ошибка прежнего входа')).toBeNull();
      expect(button().disabled).toBe(true);
    } finally { session.restore(); }
  });

  it.each(['success', 'error'] as const)('does not close or unlock on a late copy %s from the former login', async result => {
    const session = documentCopyTestSession(); const pending = deferred<void>();
    const confirm = vi.fn(() => pending.promise); const close = vi.fn();
    try {
      open(confirm, close); await waitFor(() => expect(button().disabled).toBe(false));
      fireEvent.click(button()); expect(confirm).toHaveBeenCalledOnce(); session.replace();
      await act(async () => { if (result === 'error') pending.reject(new Error('Старая ошибка копии')); else pending.resolve(undefined); });
      expect(close).not.toHaveBeenCalled(); expect(screen.queryByText('Старая ошибка копии')).toBeNull();
      fireEvent.submit(screen.getByRole('dialog')); expect(confirm).toHaveBeenCalledOnce();
    } finally { session.restore(); }
  });

  it('does not retry defaults under a replacement login', async () => {
    const session = documentCopyTestSession();
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockRejectedValueOnce(new Error('Недостаточно прав'));
    try {
      open(); await screen.findByText('Недостаточно прав'); session.replace();
      fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
      await act(async () => {});
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledOnce();
    } finally { session.restore(); }
  });

  it('copy session expansion keeps edits and confirmation across a refresh of the same login', async () => {
    const session = documentCopyTestSession(); const confirm = vi.fn().mockResolvedValue(undefined); const close = vi.fn();
    try {
      const view = open(confirm, close); await waitFor(() => expect(button().disabled).toBe(false));
      fireEvent.click(screen.getByLabelText('Подпись')); session.refresh();
      view.rerender(<DocumentImagePrintDialog documentName="Счёт 7" initialOptions={empty} defaultsSource={{ section: 'invoices', documentId: 7 }} confirmLabel="Скачать PDF" onConfirm={confirm} onClose={close} />);
      expect((screen.getByLabelText('Подпись') as HTMLInputElement).checked).toBe(true);
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledOnce();
      fireEvent.click(button()); await waitFor(() => expect(close).toHaveBeenCalledOnce());
      expect(confirm).toHaveBeenCalledExactlyOnceWith({ ...empty, includeSignature: true }, expect.any(AbortSignal));
    } finally { session.restore(); }
  });

  it('copy session expansion disables the old dialog on rerender without adopting the replacement session', async () => {
    const session = documentCopyTestSession(); const confirm = vi.fn();
    try {
      const view = open(confirm); await waitFor(() => expect(button().disabled).toBe(false)); session.replace();
      view.rerender(<DocumentImagePrintDialog documentName="Счёт 7" initialOptions={empty} defaultsSource={{ section: 'invoices', documentId: 7 }} confirmLabel="Скачать PDF" onConfirm={confirm} onClose={vi.fn()} />);
      expect(button().disabled).toBe(true); expect(screen.getByRole('alert').textContent).toContain('сеанс входа');
      fireEvent.submit(screen.getByRole('dialog')); expect(confirm).not.toHaveBeenCalled();
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledOnce();
    } finally { session.restore(); }
  });

  it('copy session expansion refuses a confirm and late error after its parent owner was invalidated', async () => {
    let current = true; const pending = deferred<void>(); const confirm = vi.fn(() => pending.promise);
    render(<DocumentImagePrintDialog owner={{ isCurrent: () => current }} documentName="Счёт 7" initialOptions={empty} confirmLabel="Скачать PDF" onConfirm={confirm} onClose={vi.fn()} />);
    fireEvent.click(button()); expect(confirm).toHaveBeenCalledOnce(); current = false;
    await act(async () => pending.reject(new Error('Ошибка закрытой карточки')));
    expect(screen.queryByText('Ошибка закрытой карточки')).toBeNull();
    fireEvent.submit(screen.getByRole('dialog')); expect(confirm).toHaveBeenCalledOnce();
  });
});
