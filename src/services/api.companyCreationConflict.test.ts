import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('company creation conflict transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(['plain', 'json'] as const)('retains a readable %s conflict without retrying the creation', async format => {
    const message = 'Карточка не создана. Проверьте список компаний и повторите действие.';
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(format === 'plain' ? message : JSON.stringify({ code: 'CompanyCreationConflict', message }), {
      status: 409, headers: { 'Content-Type': format === 'plain' ? 'text/plain' : 'application/json' },
    }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.createLegalEntity({ shortName: 'Unsaved draft', inn: '7700000101', clientIds: [] })).rejects.toThrow(message);
    expect(request).toHaveBeenCalledOnce(); expect(request.mock.calls[0][1]?.method).toBe('POST');
    expect(String(request.mock.calls[0][0])).toMatch(/\/legal-entities$/);
  });
});
