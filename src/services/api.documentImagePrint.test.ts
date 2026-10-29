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
