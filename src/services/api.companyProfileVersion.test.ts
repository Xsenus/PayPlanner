import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('company profile version transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(['detail', 'profile'] as const)('loads the %s input and its opaque version together without caching', async route => {
    const value = { id: 9, shortName: 'Company', profileEditVersion: 'A'.repeat(64) };
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    const saved = route === 'detail' ? await apiService.getLegalEntity(9) : await apiService.getCompanyProfileLegalEntity();
    expect(saved).toEqual(value); expect(request).toHaveBeenCalledOnce();
    expect(String(request.mock.calls[0][0])).toMatch(route === 'detail' ? /\/legal-entities\/9$/ : /\/legal-entities\/company-profile$/);
    expect(request.mock.calls[0][1]?.cache).toBe('no-store');
  });

  it('sends the captured version once and returns the next version without a separate read', async () => {
    const payload = { shortName: 'Edited', clientIds: [], profileEditVersion: 'A'.repeat(64) };
    const value = { id: 9, ...payload, profileEditVersion: 'B'.repeat(64) };
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.updateLegalEntity(9, payload)).resolves.toEqual(value);
    expect(request).toHaveBeenCalledOnce(); expect(request.mock.calls[0][1]?.method).toBe('PUT');
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual(payload);
  });

  it('preserves a conflict without refreshing the version or retrying the write', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ code: 'CompanyProfileChanged', message: 'Откройте карточку заново и сравните данные.' }), { status: 409, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.updateLegalEntity(9, { shortName: 'Stale', clientIds: [], profileEditVersion: 'A'.repeat(64) })).rejects.toThrow('Откройте карточку заново и сравните данные.');
    expect(request).toHaveBeenCalledOnce();
  });
});
