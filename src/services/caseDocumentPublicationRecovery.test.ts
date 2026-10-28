import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { webcrypto } from 'node:crypto';
import { apiService, type CaseDocumentUploadState } from './api';
import { API_BASE_URL } from './apiBaseUrl';
import { beginCaseDocumentGeneration, getUnconfirmedCaseDocumentUploadKeys } from './caseDocumentUploadAttempts';
import { AuthSessionChangedError } from './authService';

const token = (id = 1, iat = 1) => `header.${btoa(JSON.stringify({ sub: String(id), iss: 'owned-fixture', iat }))}.signature`;
const scope = (kind = 'invoice', caseId = 7, api = API_BASE_URL, hash = 'b'.repeat(64)) => JSON.stringify(['financial', api, caseId, kind, hash]);
const ready = (key: string, kind: 'invoice' | 'act' | 'contract' = 'invoice'): CaseDocumentUploadState => ({
  uploadKey: key, status: 'uploaded', documentId: 19, version: 1, sha256: 'f'.repeat(64),
  documentKey: `${kind}-19`, requestedDocumentKey: `${kind}-19`, fileName: `case-7-${kind}-19.html`,
  contentType: 'text/html; charset=utf-8', documentType: kind === 'invoice' ? 'Счет' : kind === 'act' ? 'Акт' : 'Договор',
  requestedStatus: 'Черновик', message: 'Документ добавлен в дело.',
  generation: { kind: 'financial', financialKind: kind, requestSha256: 'a'.repeat(64), templateKey: null,
    documentType: kind === 'invoice' ? 'Счет' : kind === 'act' ? 'Акт' : 'Договор', status: 'Черновик', sourceVerified: true },
});
const response = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } });
const empty = { items: [], nextCursor: null };

beforeEach(() => {
  localStorage.clear(); sessionStorage.clear();
  localStorage.setItem('pp_token', token()); vi.stubGlobal('crypto', webcrypto);
});
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); localStorage.clear(); sessionStorage.clear(); });

describe('published local case attempt recovery', () => {
  it('selects only current actor/API/case identities and excludes observed pending keys', () => {
    const own = beginCaseDocumentGeneration(scope(), token());
    const order = beginCaseDocumentGeneration(JSON.stringify(['work-order', API_BASE_URL, 7]), token());
    const template = beginCaseDocumentGeneration(JSON.stringify(['template', API_BASE_URL, 7, 'custom', 'Иск', 'Подготовлен']), token());
    beginCaseDocumentGeneration(scope('invoice', 8), token());
    beginCaseDocumentGeneration(scope('invoice', 7, 'http://other.local/api'), token());
    beginCaseDocumentGeneration(scope(), token(2));
    beginCaseDocumentGeneration(scope('unknown'), token());
    beginCaseDocumentGeneration(JSON.stringify(['version', API_BASE_URL, 7, 'Счет', 'Черновик']), token());
    expect(getUnconfirmedCaseDocumentUploadKeys(7, API_BASE_URL, token(1, 2), [own.key.toUpperCase()]))
      .toEqual([order.key, template.key]);
    expect(getUnconfirmedCaseDocumentUploadKeys(7, API_BASE_URL, null)).toEqual([]);
  });

  it.each(['invoice', 'act', 'contract'] as const)('confirms a lost ready %s reply using GET proof only', async kind => {
    const own = beginCaseDocumentGeneration(scope(kind), token());
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValueOnce(response(empty)).mockResolvedValueOnce(response([ready(own.key, kind)]));
    vi.stubGlobal('fetch', fetchMock);
    expect(await apiService.getPendingCaseDocumentUploads(7)).toEqual({ ...empty, localPublicationConfirmed: true });
    expect(sessionStorage.getItem('pp.case-document-upload-attempts.v1')).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(new URL(String(fetchMock.mock.calls[1][0])).searchParams.getAll('uploadKeys')).toEqual([own.key]);
    expect(fetchMock.mock.calls.every(([, options]) => (options?.method ?? 'GET') === 'GET' && options?.body === undefined)).toBe(true);
  });

  it.each(['pending', 'later-page', 'empty'])('avoids redundant proof reads for %s discovery', async scenario => {
    const own = scenario === 'empty' ? null : beginCaseDocumentGeneration(scope(), token());
    const page = scenario === 'pending' ? { items: [{ ...ready(own!.key), status: 'waiting', documentId: null, version: null }], nextCursor: null } : empty;
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(response(page)); vi.stubGlobal('fetch', fetchMock);
    expect(await apiService.getPendingCaseDocumentUploads(7, scenario === 'later-page' ? 'cursor' : undefined)).toEqual(page);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    if (own) expect(beginCaseDocumentGeneration(scope(), token())).toEqual(own);
  });

  it.each(['unrequested', 'paused', 'sha', 'document-key', 'document-id', 'unverified', 'wrong-kind'])(
    'preserves identity when exact proof is %s', async change => {
      const own = beginCaseDocumentGeneration(scope(), token()); const receipt = ready(own.key);
      if (change === 'unrequested') receipt.uploadKey = crypto.randomUUID();
      if (change === 'paused') receipt.status = 'paused';
      if (change === 'sha') receipt.sha256 = null;
      if (change === 'document-key') receipt.documentKey = '';
      if (change === 'document-id') receipt.documentId = 0;
      if (change === 'unverified') receipt.generation!.sourceVerified = false;
      if (change === 'wrong-kind') receipt.generation!.financialKind = 'contract';
      const fetchMock = vi.fn<typeof fetch>().mockResolvedValueOnce(response(empty)).mockResolvedValueOnce(response([receipt]));
      vi.stubGlobal('fetch', fetchMock);
      expect(await apiService.getPendingCaseDocumentUploads(7)).toEqual(empty);
      expect(beginCaseDocumentGeneration(scope(), token())).toEqual(own);
    });

  it.each(['different-actor', 'same-actor-refresh', 'abort'])('guards %s during proof', async change => {
    const own = beginCaseDocumentGeneration(scope(), token()); const controller = new AbortController();
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValueOnce(response(empty)).mockImplementationOnce(async () => {
      if (change === 'abort') controller.abort();
      else localStorage.setItem('pp_token', change === 'different-actor' ? token(2) : token(1, 2));
      return response([ready(own.key)]);
    }); vi.stubGlobal('fetch', fetchMock);
    const result = apiService.getPendingCaseDocumentUploads(7, undefined, controller.signal);
    if (change === 'different-actor') await expect(result).rejects.toBeInstanceOf(AuthSessionChangedError);
    else if (change === 'abort') await result.catch(() => null);
    else expect(await result).toEqual({ ...empty, localPublicationConfirmed: true });
    if (change !== 'same-actor-refresh') expect(beginCaseDocumentGeneration(scope(), token())).toEqual(own);
    else expect(sessionStorage.getItem('pp.case-document-upload-attempts.v1')).toBeNull();
  });

  it('reads sixty unresolved keys in two bounded batches without history or writes', async () => {
    for (let i = 0; i < 60; i++) beginCaseDocumentGeneration(scope('contract', 7, API_BASE_URL, i.toString(16).padStart(64, '0')), token());
    const sizes: number[] = [];
    const fetchMock = vi.fn<typeof fetch>(async url => {
      const keys = new URL(String(url)).searchParams.getAll('uploadKeys');
      if (!keys.length) return response(empty);
      sizes.push(keys.length); return response(keys.map(key => ready(key, 'contract')));
    }); vi.stubGlobal('fetch', fetchMock);
    expect(await apiService.getPendingCaseDocumentUploads(7)).toEqual({ ...empty, localPublicationConfirmed: true });
    expect(sizes).toEqual([50, 10]); expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(sessionStorage.getItem('pp.case-document-upload-attempts.v1')).toBeNull();
  });

  it.each(['corrupt', 'denied'])('keeps %s identities without disabling authorized server discovery', async change => {
    const pendingKey = crypto.randomUUID();
    const page = { items: [{ ...ready(pendingKey), status: 'waiting', documentId: null, version: null }], nextCursor: null };
    sessionStorage.setItem('pp.case-document-upload-attempts.v1', '{broken');
    const getItem = Storage.prototype.getItem;
    if (change === 'denied') vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key) {
      if (this === sessionStorage) throw new Error('storage denied');
      return getItem.call(this, key);
    });
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(response(page)); vi.stubGlobal('fetch', fetchMock);
    expect(getUnconfirmedCaseDocumentUploadKeys(7, API_BASE_URL, token())).toEqual([]);
    expect(await apiService.getPendingCaseDocumentUploads(7)).toEqual(page); expect(fetchMock).toHaveBeenCalledTimes(1);
    vi.restoreAllMocks(); expect(sessionStorage.getItem('pp.case-document-upload-attempts.v1')).toBe('{broken');
  });

  it('does not send an empty UUID from altered local data or release it', async () => {
    const own = beginCaseDocumentGeneration(scope(), token());
    const raw = sessionStorage.getItem('pp.case-document-upload-attempts.v1')!.replace(own.key, '00000000-0000-0000-0000-000000000000');
    sessionStorage.setItem('pp.case-document-upload-attempts.v1', raw);
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(response(empty)); vi.stubGlobal('fetch', fetchMock);
    expect(await apiService.getPendingCaseDocumentUploads(7)).toEqual(empty); expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(sessionStorage.getItem('pp.case-document-upload-attempts.v1')).toBe(raw);
  });
});
