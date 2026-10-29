import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { DocumentImagePrintDialog } from './DocumentImagePrintDialog';
import type { DocumentImagePrintOptions } from '../../types/documentImagePrint';

const empty = { includeLogo: false, includeSeal: false, includeSignature: false };
const labels = ['Логотип', 'Печать', 'Подпись'];

describe('DocumentImagePrintDialog', () => {
  it.each(Array.from({ length: 8 }, (_, i) => i))('confirms independent image combination %s without changing defaults', async mask => {
    const user = userEvent.setup();
    const onConfirm = vi.fn<(options: DocumentImagePrintOptions, signal: AbortSignal) => Promise<void>>().mockResolvedValue();
    const onClose = vi.fn();
    const initial = { ...empty };
    render(<DocumentImagePrintDialog documentName="Счёт № 7" initialOptions={initial} confirmLabel="Скачать PDF" onConfirm={onConfirm} onClose={onClose} />);
    for (let bit = 0; bit < 3; bit++) if (mask & (1 << bit)) await user.click(screen.getByLabelText(labels[bit]));
    await user.click(screen.getByRole('button', { name: 'Скачать PDF' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(onConfirm).toHaveBeenCalledExactlyOnceWith({ includeLogo: !!(mask & 1), includeSeal: !!(mask & 2), includeSignature: !!(mask & 4) }, expect.any(AbortSignal));
    expect(initial).toEqual(empty);
  });

  it('cancels with Escape without submitting or closing the underlying card', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn(); const onClose = vi.fn(); const parentKey = vi.fn();
    render(<div onKeyDown={parentKey}><DocumentImagePrintDialog documentName="Счёт" initialOptions={empty} confirmLabel="Печатать" onConfirm={onConfirm} onClose={onClose} /></div>);
    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledTimes(1); expect(onConfirm).not.toHaveBeenCalled(); expect(parentKey).not.toHaveBeenCalled();
  });

  it('does not submit the underlying document form through its portal', async () => {
    const saveDocument = vi.fn(); const onConfirm = vi.fn().mockResolvedValue(undefined);
    render(<form onSubmit={saveDocument}><DocumentImagePrintDialog documentName="Счёт" initialOptions={empty} confirmLabel="Печатать" onConfirm={onConfirm} onClose={vi.fn()} /></form>);
    fireEvent.submit(screen.getByRole('dialog'));
    await waitFor(() => expect(onConfirm).toHaveBeenCalledTimes(1)); expect(saveDocument).not.toHaveBeenCalled();
  });

  it('fences duplicate confirmation and cancels only its running request', async () => {
    const user = userEvent.setup(); let finish!: () => void;
    let signal!: AbortSignal;
    const onConfirm = vi.fn((_options: DocumentImagePrintOptions, value: AbortSignal) => { signal = value; return new Promise<void>(resolve => { finish = resolve; }); }); const onClose = vi.fn();
    render(<DocumentImagePrintDialog documentName="Счёт" initialOptions={empty} confirmLabel="Печатать" onConfirm={onConfirm} onClose={onClose} />);
    const form = screen.getByRole('dialog'); fireEvent.submit(form); fireEvent.submit(form);
    await user.keyboard('{Escape}'); await user.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(onConfirm).toHaveBeenCalledTimes(1); expect(onClose).toHaveBeenCalledTimes(1); expect(signal.aborted).toBe(true);
    expect(screen.getByLabelText<HTMLInputElement>('Логотип').disabled || screen.getByLabelText('Логотип').closest('fieldset')?.disabled).toBe(true);
    await act(async () => finish()); await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  it('keeps choices after an error and retries with a fresh signal', async () => {
    const user = userEvent.setup(); const signals: AbortSignal[] = [];
    const onConfirm = vi.fn(async (_options: DocumentImagePrintOptions, signal: AbortSignal) => {
      signals.push(signal); if (signals.length === 1) throw new Error('Не удалось скачать файл.');
    }); const onClose = vi.fn();
    render(<DocumentImagePrintDialog documentName="Счёт" initialOptions={empty} confirmLabel="Скачать Word" onConfirm={onConfirm} onClose={onClose} />);
    await user.click(screen.getByLabelText('Подпись')); await user.click(screen.getByRole('button', { name: 'Скачать Word' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Не удалось скачать файл.');
    expect(screen.getByLabelText<HTMLInputElement>('Подпись').checked).toBe(true); expect(onClose).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Скачать Word' })); await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(signals[0]).not.toBe(signals[1]); expect(onConfirm.mock.calls[1][0]).toEqual({ ...empty, includeSignature: true });
  });

  it('aborts its owned request on unmount and ignores its late completion', async () => {
    let signal!: AbortSignal; let finish!: () => void; const onClose = vi.fn();
    const onConfirm = vi.fn((_options: DocumentImagePrintOptions, value: AbortSignal) => {
      signal = value; return new Promise<void>(resolve => { finish = resolve; });
    });
    const view = render(<DocumentImagePrintDialog documentName="Счёт" initialOptions={empty} confirmLabel="Печатать" onConfirm={onConfirm} onClose={onClose} />);
    fireEvent.submit(screen.getByRole('dialog')); view.unmount(); expect(signal.aborted).toBe(true);
    await act(async () => finish()); expect(onClose).not.toHaveBeenCalled();
  });
});
