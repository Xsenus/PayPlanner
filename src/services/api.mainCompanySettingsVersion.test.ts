import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('main company settings version transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('sends the captured version and list in one PUT without fetching another baseline', async () => {
    const version = 'A'.repeat(64);
    const payload = { expectedVersion: version, companyIds: [7, 8], defaultCompanyId: 8, sellerSnapshotTiming: 'on-create' as const };
    const response = { settingsVersion: 'B'.repeat(64), companies: [{ id: 7 }, { id: 8 }], defaultCompanyId: 8, sellerSnapshotTiming: 'on-create' };
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify(response), { headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.updateMainCompanySettings(payload)).resolves.toEqual(response);
    expect(request).toHaveBeenCalledOnce();
    expect(String(request.mock.calls[0][0])).toMatch(/\/legal-entities\/main-companies\/settings$/);
    expect(request.mock.calls[0][1]?.method).toBe('PUT');
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual(payload);
  });

  it('preserves a readable conflict and never retries or refreshes its version', async () => {
    const message = 'Список собственных компаний изменился. Ваш выбор не сохранён.';
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ code: 'MainCompanySettingsChanged', message }), { status: 409, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.updateMainCompanySettings({ expectedVersion: 'A'.repeat(64), companyIds: [] })).rejects.toThrow(message);
    expect(request).toHaveBeenCalledOnce();
  });
});
