import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('company deletion conflict transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('returns a readable deletion conflict without a retry', async () => {
    const message = 'Компания или её связи изменились. Удаление не выполнено.';
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ code: 'CompanyDeletionConflict', message }), { status: 409, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.deleteLegalEntity(7)).rejects.toThrow(message);
    expect(request).toHaveBeenCalledOnce();
    expect(String(request.mock.calls[0][0])).toMatch(/\/legal-entities\/7$/);
    expect(request.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it('accepts a successful empty response as a completed deletion', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.deleteLegalEntity(7)).resolves.toBeUndefined();
    expect(request).toHaveBeenCalledOnce();
  });
});
