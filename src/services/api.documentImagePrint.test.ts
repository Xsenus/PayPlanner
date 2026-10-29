import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';

describe('invoice independent image transport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(['word', 'pdf', 'html'] as const)('sends explicit false values, template and abort signal for %s', async format => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('document')); vi.stubGlobal('fetch', request);
    const controller = new AbortController();
    await apiService.downloadInvoiceDocument(7, format, { includeLogo: false, includeSeal: true, includeSignature: false, templateKey: 'invoice-offer', signal: controller.signal });
    const url = new URL(String(request.mock.calls[0][0]), 'https://local.invalid');
    expect(Object.fromEntries(url.searchParams)).toEqual({ format, templateKey: 'invoice-offer', includeLogo: 'false', includeSeal: 'true', includeSignature: 'false' });
    expect(request.mock.calls[0][1]?.signal).toBe(controller.signal);
  });

  it('keeps the old combined flag and omits new options for unchanged callers', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('document')); vi.stubGlobal('fetch', request);
    await apiService.downloadInvoiceDocument(7, 'pdf', { includeSignatureAndSeal: false });
    const url = new URL(String(request.mock.calls[0][0]), 'https://local.invalid');
    expect(Object.fromEntries(url.searchParams)).toEqual({ format: 'pdf', includeSignatureAndSeal: 'false' });
  });
});

describe('act and contract independent image transport', () => {
  afterEach(() => vi.unstubAllGlobals());
  it.each(['acts', 'contracts'] as const)('sends explicit false and cancellation for every %s format', async section => {
    const request = vi.fn<typeof fetch>().mockImplementation(async () => new Response('document')); vi.stubGlobal('fetch', request);
    for (const format of ['word', 'pdf', 'html'] as const) {
      const controller = new AbortController();
      const options = { includeLogo: false, includeSeal: true, includeSignature: false, signal: controller.signal };
      if (section === 'acts') await apiService.downloadActDocument(7, format, options);
      else await apiService.downloadContractDocument(7, format, options);
      const [target, init] = request.mock.calls.at(-1)!;
      const url = new URL(String(target), 'https://local.invalid');
      expect(url.pathname).toMatch(new RegExp(`/${section}/7/document$`));
      expect(Object.fromEntries(url.searchParams)).toEqual({ format, includeLogo: 'false', includeSeal: 'true', includeSignature: 'false' });
      expect(init?.signal).toBe(controller.signal);
    }
  });
  it('preserves old act combined flags and the old contract request', async () => {
    const request = vi.fn<typeof fetch>().mockImplementation(async () => new Response('document')); vi.stubGlobal('fetch', request);
    await apiService.downloadActDocument(7, 'pdf', { includeSignatureAndSeal: false });
    expect(Object.fromEntries(new URL(String(request.mock.calls[0][0]), 'https://local.invalid').searchParams)).toEqual({ format: 'pdf', includeSignatureAndSeal: 'false' });
    await apiService.downloadContractDocument(7, 'word');
    expect(Object.fromEntries(new URL(String(request.mock.calls[1][0]), 'https://local.invalid').searchParams)).toEqual({ format: 'word' });
    expect(request.mock.calls[1][1]?.signal).toBeUndefined();
  });
});
