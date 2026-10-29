import { describe, expect, it, vi } from 'vitest';
import { awaitOwnedUiResult, StaleUiOperationError } from './StaleUiOperationError';

describe('owned UI results', () => {
  it('does not send a request from an already stale callback', async () => {
    const write = vi.fn().mockResolvedValue(7);
    await expect(awaitOwnedUiResult(write, () => false)).rejects.toBeInstanceOf(StaleUiOperationError);
    expect(write).not.toHaveBeenCalled();
  });
  it('returns an acknowledged current result', async () => {
    await expect(awaitOwnedUiResult(async () => ({ id: 7 }), () => true)).resolves.toEqual({ id: 7 });
  });
  it.each(['success', 'error'] as const)('does not expose a stale %s after the owner changes', async outcome => {
    let current = true; let finish!: (value: number) => void; let fail!: (error: Error) => void;
    const request = new Promise<number>((resolve, reject) => { finish = resolve; fail = reject; });
    const pending = awaitOwnedUiResult(() => request, () => current).catch(error => error);
    current = false;
    if (outcome === 'success') finish(7); else fail(new Error('Previous user private server details'));
    const result = await pending;
    expect(result).toBeInstanceOf(StaleUiOperationError);
    expect(result.message).not.toContain('private');
  });
  it('preserves a genuine current server error for the form', async () => {
    const error = new Error('Current validation failure');
    await expect(awaitOwnedUiResult(async () => { throw error; }, () => true)).rejects.toBe(error);
  });
});
