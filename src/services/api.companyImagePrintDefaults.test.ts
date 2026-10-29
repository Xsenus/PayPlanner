import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('company image print settings transport', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('round trips all independent values and a new version without coercing false', async () => {
    const value = { invoice: { includeLogo: false, includeSeal: true, includeSignature: false }, act: null, contract: { includeLogo: true, includeSeal: false, includeSignature: true } };
    const payload = { shortName: 'Synthetic', clientIds: [], documentImagePrintDefaults: value, documentImagePrintDefaultsChanged: true, documentImagePrintDefaultsVersion: 'A'.repeat(64) };
    const response = { id: 9, clients: [], documentImagePrintDefaults: value, documentImagePrintDefaultsVersion: 'B'.repeat(64) };
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify(response), { headers: { 'Content-Type': 'application/json' } })); vi.stubGlobal('fetch', request);
    const result = await apiService.updateLegalEntity(9, payload);
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual(payload);
    expect(result).toEqual(response);
  });
  it('keeps existing clients free of default or version writes', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({ id: 9, clients: [] }), { headers: { 'Content-Type': 'application/json' } })); vi.stubGlobal('fetch', request);
    await apiService.updateLegalEntity(9, { shortName: 'Synthetic', clientIds: [] });
    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual({ shortName: 'Synthetic', clientIds: [] });
  });
});
