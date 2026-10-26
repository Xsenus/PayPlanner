import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('document seller snapshot policy transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(['first-file', 'on-create'] as const)('reads the explicit %s policy without company profile data', async timing => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ timing }));
    vi.stubGlobal('fetch', request);

    expect(await apiService.getDocumentSellerSnapshotPolicy()).toBe(timing);
    expect(request).toHaveBeenCalledTimes(1);
    expect(String(request.mock.calls[0][0]).endsWith('/own-companies/snapshot-policy')).toBe(true);
  });

  it.each([null, undefined, '', 'ON-CREATE', 'on-create ', 'unknown'])('rejects an unknown policy %s instead of silently changing the timing', async timing => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ timing }));
    vi.stubGlobal('fetch', request);

    await expect(apiService.getDocumentSellerSnapshotPolicy()).rejects.toThrow();
    expect(request).toHaveBeenCalledTimes(1);
  });
});
