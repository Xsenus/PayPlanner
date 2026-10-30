import { StrictMode, useEffect } from 'react';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DialogProvider } from './DialogContext';
import { useDialog } from '../hooks/useDialog';
import type { DialogContextValue } from './dialogContextCore';

function setup(strict = false) {
  let api!: DialogContextValue;
  function Consumer() { api = useDialog(); return <span>Current screen</span>; }
  const content = <DialogProvider><Consumer /></DialogProvider>;
  const view = render(strict ? <StrictMode>{content}</StrictMode> : content);
  return { view, api };
}

describe('DialogProvider owned prompt', () => {
  it('prompt owner: never opens an already aborted prompt', async () => {
    const { api } = setup(); const controller = new AbortController(); controller.abort();
    await expect(api.prompt({ title: 'Old reason', signal: controller.signal })).resolves.toBeNull(); expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('prompt owner: removes typed private text and resolves null on abort', async () => {
    const { api } = setup(); const controller = new AbortController(); let result!: Promise<string | null>;
    act(() => { result = api.prompt({ title: 'Owned reason', label: 'Reason', signal: controller.signal }); });
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Private financial reason' } });
    await act(async () => { controller.abort(); expect(await result).toBeNull(); });
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByDisplayValue('Private financial reason')).toBeNull();
  });
  it('prompt owner: an old abort never closes a newer prompt', async () => {
    const { api } = setup(); const controller = new AbortController(); let old!: Promise<string | null>; let current!: Promise<string | null>;
    act(() => { old = api.prompt({ title: 'Old', signal: controller.signal }); });
    act(() => { current = api.prompt({ title: 'New', defaultValue: 'Current value' }); });
    await act(async () => { controller.abort(); expect(await old).toBeNull(); });
    fireEvent.submit(screen.getByRole('dialog', { name: 'New' }));
    await expect(current).resolves.toBe('Current value');
  });
  it.each(['accept', 'cancel', 'escape'] as const)('prompt owner: cleans up the signal after %s and leaves the next prompt intact', async outcome => {
    const { api } = setup(); const controller = new AbortController(); const remove = vi.spyOn(controller.signal, 'removeEventListener'); let result!: Promise<string | null>;
    act(() => { result = api.prompt({ title: 'Owned', defaultValue: 'Value', signal: controller.signal }); });
    const dialog = screen.getByRole('dialog', { name: 'Owned' });
    if (outcome === 'accept') fireEvent.submit(dialog); else if (outcome === 'cancel') fireEvent.click(within(dialog).getByRole('button', { name: 'Отмена' })); else fireEvent.keyDown(dialog, { key: 'Escape' });
    await expect(result).resolves.toBe(outcome === 'accept' ? 'Value' : null); expect(remove).toHaveBeenCalledWith('abort', expect.any(Function));
    act(() => { void api.prompt({ title: 'Next' }); controller.abort(); }); expect(screen.getByRole('dialog', { name: 'Next' })).toBeTruthy();
  });
  it('prompt owner: provider unmount settles an outstanding prompt', async () => {
    const { api, view } = setup(); const controller = new AbortController(); let result!: Promise<string | null>;
    act(() => { result = api.prompt({ title: 'Pending', signal: controller.signal }); }); view.unmount(); await expect(result).resolves.toBeNull();
  });
  it('prompt owner: StrictMode cleanup cancels only the abandoned lifetime', async () => {
    const calls: Promise<string | null>[] = [];
    function Consumer() {
      const api = useDialog();
      useEffect(() => { const controller = new AbortController(); calls.push(api.prompt({ title: 'Mounted reason', defaultValue: 'Current', signal: controller.signal })); return () => controller.abort(); }, [api]);
      return null;
    }
    render(<StrictMode><DialogProvider><Consumer /></DialogProvider></StrictMode>);
    expect(calls).toHaveLength(2); await expect(calls[0]).resolves.toBeNull(); fireEvent.submit(screen.getByRole('dialog', { name: 'Mounted reason' })); await expect(calls[1]).resolves.toBe('Current');
  });
});

describe('DialogProvider owned confirmation', () => {
  it('does not display an already cancelled confirmation', async () => {
    const { api } = setup(); const controller = new AbortController(); controller.abort();
    await expect(api.confirm({ title: 'Old screen', signal: controller.signal })).resolves.toBe(false);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('removes old private text and settles false when its owner aborts', async () => {
    const { api } = setup(); const controller = new AbortController(); let result!: Promise<boolean>;
    act(() => { result = api.confirm({ title: 'Old owner', description: 'Private old company', signal: controller.signal }); });
    expect(screen.getByText('Private old company')).toBeTruthy();
    await act(async () => { controller.abort(); expect(await result).toBe(false); });
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByText('Private old company')).toBeNull();
  });

  it('does not close another confirmation when an older owner aborts', async () => {
    const { api } = setup(); const old = new AbortController(); let first!: Promise<boolean>; let second!: Promise<boolean>;
    act(() => { first = api.confirm({ title: 'Old owner', signal: old.signal }); });
    act(() => { second = api.confirm({ title: 'Current owner' }); });
    await act(async () => { old.abort(); expect(await first).toBe(false); });
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Current owner' })).getByRole('button', { name: 'Подтвердить' }));
    await expect(second).resolves.toBe(true);
  });

  it.each(['approve', 'cancel', 'escape'])('cleans up a settled %s signal without affecting a later dialog', async outcome => {
    const { api } = setup(); const controller = new AbortController(); const remove = vi.spyOn(controller.signal, 'removeEventListener');
    let result!: Promise<boolean>;
    act(() => { result = api.confirm({ title: 'Owned', signal: controller.signal }); });
    const dialog = screen.getByRole('dialog', { name: 'Owned' });
    if (outcome === 'escape') fireEvent.keyDown(dialog, { key: 'Escape' });
    else fireEvent.click(within(dialog).getByRole('button', { name: outcome === 'approve' ? 'Подтвердить' : 'Отмена' }));
    await expect(result).resolves.toBe(outcome === 'approve');
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function));
    let next!: Promise<boolean>; act(() => { next = api.confirm({ title: 'Next owner' }); });
    act(() => controller.abort()); expect(screen.getByRole('dialog', { name: 'Next owner' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); await expect(next).resolves.toBe(false);
  });

  it('settles all pending owned confirmations when the provider unmounts', async () => {
    const { api, view } = setup(); const a = new AbortController(); const b = new AbortController(); let first!: Promise<boolean>; let second!: Promise<boolean>;
    const removeA = vi.spyOn(a.signal, 'removeEventListener'); const removeB = vi.spyOn(b.signal, 'removeEventListener');
    act(() => { first = api.confirm({ title: 'A', signal: a.signal }); second = api.confirm({ title: 'B', signal: b.signal }); });
    view.unmount(); await expect(first).resolves.toBe(false); await expect(second).resolves.toBe(false);
    expect(removeA).toHaveBeenCalled(); expect(removeB).toHaveBeenCalled();
  });

  it('accepts an owned user action under StrictMode', async () => {
    const { api } = setup(true); const controller = new AbortController(); let pending!: Promise<boolean>;
    act(() => { pending = api.confirm({ title: 'Strict owner', signal: controller.signal }); });
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить' })); await expect(pending).resolves.toBe(true);
  });

  it('does not leave a settled popup after StrictMode repeats mount effects', async () => {
    const controller = new AbortController(); const calls: Promise<boolean>[] = [];
    function Consumer() {
      const api = useDialog();
      useEffect(() => { calls.push(api.confirm({ title: 'Mounted owner', signal: controller.signal })); }, [api]);
      return null;
    }
    render(<StrictMode><DialogProvider><Consumer /></DialogProvider></StrictMode>);
    expect(calls).toHaveLength(2); await expect(calls[0]).resolves.toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить' })); await expect(calls[1]).resolves.toBe(true);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('keeps the existing string confirmation contract', async () => {
    const { api } = setup(); let result!: Promise<boolean>;
    act(() => { result = api.confirm('Continue?'); });
    expect(screen.getByText('Continue?')).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await expect(result).resolves.toBe(false);
  });

  it('keeps an alert independent of an aborted confirmation', async () => {
    const { api } = setup(); const controller = new AbortController(); let confirmation!: Promise<boolean>; let alert!: Promise<void>;
    act(() => { confirmation = api.confirm({ title: 'Owned', signal: controller.signal }); alert = api.alert({ title: 'Alert', description: 'Keep this alert' }); });
    await act(async () => { controller.abort(); expect(await confirmation).toBe(false); });
    const dialog = screen.getByRole('dialog', { name: 'Alert' }); fireEvent.click(within(dialog).getByRole('button', { name: 'ОК' }));
    await expect(alert).resolves.toBeUndefined();
  });

  it('keeps prompt editing and acceptance unchanged', async () => {
    const { api } = setup(); let result!: Promise<string | null>;
    act(() => { result = api.prompt({ title: 'Prompt', label: 'Value', defaultValue: 'Original' }); });
    const dialog = screen.getByRole('dialog', { name: 'Prompt' });
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Changed' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Подтвердить' })); await expect(result).resolves.toBe('Changed');
  });
});
