import { afterEach, describe, expect, it, vi } from 'vitest';
import { webcrypto } from 'node:crypto';

import { AUTH_SESSION_EXPIRED_EVENT, apiService, extractResponseErrorMessage, StickyNoteConflictError, STICKY_NOTE_REALTIME_EVENT_TYPES } from './api';
import { resolveApiBaseUrl } from './apiBaseUrl';

describe('bank import row search requests', () => {
  afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });

  it('encodes an opaque invoice page cursor and preserves cancellation and a fresh first page', async () => {
    localStorage.setItem('pp_token', 'synthetic-token');
    const payload = { items: [], hasMore: false, search: null, cursor: null, nextCursor: null };
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const cancellation = new AbortController();
    expect(await apiService.getMoneyOperationInvoiceCandidatePage(44, '  Ёжик  ', 'opaque?&%_cursor', cancellation.signal)).toEqual(payload);
    const request = new URL(String(fetchMock.mock.calls[0][0]), 'http://fixture.local');
    expect(request.pathname).toContain('/money-operations/44/invoice-candidates/pages');
    expect([...request.searchParams]).toEqual([['search', 'Ёжик'], ['cursor', 'opaque?&%_cursor']]);
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(cancellation.signal);
    expect(fetchMock.mock.calls[0][1]?.cache).toBe('no-store');
    await apiService.getMoneyOperationInvoiceCandidatePage(44);
    expect(String(fetchMock.mock.calls[1][0])).toMatch(/\/money-operations\/44\/invoice-candidates\/pages$/);
  });

  it('uses a distinct cancellable uncached invoice search route and retains default suggestions', async () => {
    localStorage.setItem('pp_token', 'synthetic-token');
    const fetchMock = vi.fn().mockImplementation(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const cancellation = new AbortController();
    await apiService.getMoneyOperationInvoiceCandidates(44, "  Ёжик &%_?\\'  ", cancellation.signal);
    const request = new URL(String(fetchMock.mock.calls[0][0]), 'http://fixture.local');
    expect(request.pathname).toContain('/money-operations/44/invoice-candidates/search');
    expect([...request.searchParams]).toEqual([['search', "Ёжик &%_?\\'"]]);
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(cancellation.signal);
    expect(fetchMock.mock.calls[0][1]?.cache).toBe('no-store');
    await apiService.getMoneyOperationInvoiceCandidates(44, '  ');
    expect(String(fetchMock.mock.calls[1][0])).toMatch(/\/money-operations\/44\/invoice-candidates$/);
  });

  it('loads a fresh imported operation read with cancellation and without browser caching', async () => {
    localStorage.setItem('pp_token', 'synthetic-token');
    const payload = { id: 44, source: 'Bank', moneyAccountId: 101 };
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const cancellation = new AbortController();
    expect(await apiService.getMoneyOperation(44, cancellation.signal)).toEqual(payload);
    expect(String(fetchMock.mock.calls[0][0])).toMatch(/\/money-operations\/44$/);
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(cancellation.signal);
    expect(fetchMock.mock.calls[0][1]?.cache).toBe('no-store');
    expect(fetchMock.mock.calls[0][1]?.method ?? 'GET').toBe('GET');
    await apiService.getMoneyOperation(44);
    expect(fetchMock.mock.calls[1][1]?.cache).toBe('no-store');
    expect(fetchMock.mock.calls[1][1]?.signal).toBeUndefined();
  });

  it('encodes bank import row search as literal query data and forwards cancellation', async () => {
    localStorage.setItem('pp_token', 'synthetic-token');
    const payload = { id: 17, rowsSearch: "Ёжик &%_?\\'", rowsMatched: 0, rows: [] };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const cancellation = new AbortController();
    expect(await apiService.getBankStatementImport(17, 2, "  Ёжик &%_?\\'  ", cancellation.signal)).toEqual(payload);
    const [url, options] = fetchMock.mock.calls[0];
    const request = new URL(String(url), 'http://fixture.local');
    expect(request.pathname).toContain('/money-operations/bank-statement/imports/17');
    expect([...request.searchParams]).toEqual([['page', '2'], ['search', "Ёжик &%_?\\'"]]);
    expect(options?.signal).toBe(cancellation.signal);
    expect(options?.method ?? 'GET').toBe('GET');
  });

  it('omits empty bank import row searches and preserves the existing unfiltered endpoint', async () => {
    localStorage.setItem('pp_token', 'synthetic-token');
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ id: 17, rows: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.getBankStatementImport(17, undefined, '  ');
    expect(String(fetchMock.mock.calls[0][0])).toMatch(/\/bank-statement\/imports\/17$/);
    await apiService.getBankStatementImport(17, 2);
    expect(String(fetchMock.mock.calls[1][0])).toMatch(/\/bank-statement\/imports\/17\?page=2$/);
  });
});

describe('financial work order retry identity', () => {
  const kinds = ['invoice', 'act', 'contract'] as const;
  const login = () => {
    localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '1', iss: 'owned-fixture' }))}.signature`);
    vi.stubGlobal('crypto', webcrypto);
  };
  const key = (options?: RequestInit) => new Headers(options?.headers).get('X-Upload-Key');
  const invoke = (kind: typeof kinds[number], payload = {}) => kind === 'invoice'
    ? apiService.createCaseWorkOrderInvoice(7, payload) : kind === 'act'
      ? apiService.createCaseWorkOrderAct(7, payload) : apiService.createCaseWorkOrderContract(7, payload);
  const ready = (kind: typeof kinds[number]) => ({ number: 'OWNED-42', amount: 35, documentId: 101, date: '2026-10-01',
    workOrder: { id: 42, clientCaseId: 7 }, ...(kind === 'invoice' ? { paymentId: 10, invoiceEntityId: 11, dueDate: '2026-10-01' }
      : kind === 'act' ? { actId: 10, lineItemCount: 2 } : { contractId: 10, startDate: '2026-10-01' }) });
  it.each(kinds)('reuses %s after uncertainty and releases it only after a complete result', async (kind) => {
    login();
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('copies pending', { status: 503 }))
      .mockImplementation(async (_url, options) => new Response(JSON.stringify(ready(kind)), { status: 201,
        headers: { 'content-type': 'application/json', 'X-Upload-Key': key(options)! } }));
    vi.stubGlobal('fetch', request);
    await expect(invoke(kind)).rejects.toThrow('503');
    expect(await invoke(kind)).toEqual(ready(kind));
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
    await invoke(kind);
    expect(key(request.mock.calls[2][1])).not.toBe(key(request.mock.calls[0][1]));
    expect(request.mock.calls.every(([url, options]) => String(url).endsWith(`create-${kind}`)
      && options?.method === 'POST' && options.body === '{}')).toBe(true);
  });
  it.each(kinds)('retains %s on partial finance without its document', async (kind) => {
    login();
    const request = vi.fn<typeof fetch>(async (_url, options) => new Response(JSON.stringify({ ...ready(kind), documentId: null }),
      { status: 201, headers: { 'content-type': 'application/json', 'X-Upload-Key': key(options)! } }));
    vi.stubGlobal('fetch', request);
    await expect(invoke(kind)).rejects.toThrow('ещё не подтверждены');
    await expect(invoke(kind)).rejects.toThrow('ещё не подтверждены');
    expect(request).toHaveBeenCalledTimes(2);
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
  });
  it.each(kinds)('keeps a secure %s retry identity when randomUUID is absent', async kind => {
    login();
    vi.stubGlobal('crypto', { getRandomValues: webcrypto.getRandomValues.bind(webcrypto), subtle: webcrypto.subtle });
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('pending', { status: 503 }))
      .mockImplementation(async (_url, options) => new Response(JSON.stringify(ready(kind)), {
        status: 201, headers: { 'content-type': 'application/json', 'X-Upload-Key': key(options)! },
      }));
    vi.stubGlobal('fetch', request);
    await expect(invoke(kind)).rejects.toThrow('503');
    expect(await invoke(kind)).toEqual(ready(kind));
    const first = key(request.mock.calls[0][1]);
    expect(first).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(key(request.mock.calls[1][1])).toBe(first);
  });
  it('bounds a long financial description and ignores object property order for retry identity', async () => {
    login();
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('pending', { status: 503 }));
    vi.stubGlobal('fetch', request);
    const description = 'Длинное описание'.repeat(1000);
    await expect(invoke('invoice', { number: '42', description })).rejects.toThrow('503');
    await expect(invoke('invoice', { description, number: '42' })).rejects.toThrow('503');
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
    const attempts = JSON.parse(sessionStorage.getItem('pp.case-document-upload-attempts.v1')!);
    expect(attempts).toHaveLength(1); expect(attempts[0].identity.length).toBeLessThan(2048);
    expect(JSON.parse(request.mock.calls[0][1]!.body as string)).toEqual({ number: '42', description });
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
});

describe('apiService backup attempt history', () => {
  it('requests only the selected page with an encoded cursor', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ items: [], nextCursor: null }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.getDatabaseBackupAttempts('cursor/with space', 10);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/database/backups/attempts?pageSize=10&after=cursor%2Fwith+space');
    expect(options?.method ?? 'GET').toBe('GET');
    expect(options?.body).toBeUndefined();
  });
});

describe('apiService pending case document uploads', () => {
  it('reads a bounded operator page with encoded filter/cursor, abort and no cache', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('{"items":[],"nextCursor":null}', { headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    await apiService.getStorageCasePublicationQueue({ status: 'deadLetter', after: 'cursor/next page', signal: controller.signal });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/storage/operations/case-documents?limit=50&status=deadLetter&after=cursor%2Fnext+page');
    expect(options?.signal).toBe(controller.signal); expect(options?.cache).toBe('no-store');
    expect(options?.body).toBeUndefined();
  });
  it('retries only the selected operation using the existing endpoint', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('{"id":"owned","status":"pending"}', { status: 202, headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    expect(await apiService.retryStorageCasePublication('owned/id', controller.signal)).toEqual({ id: 'owned', status: 'pending' });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/storage/operations/owned%2Fid/retry');
    expect(options?.method).toBe('POST'); expect(options?.signal).toBe(controller.signal);
    expect(options?.body).toBeUndefined();
  });
  it('refreshes documents without a cached list and allows aborting a closed card', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('[]', { headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    expect(await apiService.getCaseDocuments(42, controller.signal)).toEqual([]);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/v2\/cases\/42\/documents$/);
    expect(options?.signal).toBe(controller.signal);
    expect(options?.cache).toBe('no-store');
    expect(options?.body).toBeUndefined();
  });
  it('reads one page without caching and preserves the abort signal and encoded cursor', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ items: [], nextCursor: null }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    expect(await apiService.getPendingCaseDocumentUploads(42, 'cursor/with space', controller.signal))
      .toEqual({ items: [], nextCursor: null });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v2/cases/42/documents/uploads/pending?after=cursor%2Fwith+space');
    expect(options?.cache).toBe('no-store');
    expect(options?.signal).toBe(controller.signal);
    expect(options?.method ?? 'GET').toBe('GET');
    expect(options?.body).toBeUndefined();
    await apiService.getPendingCaseDocumentUploads(42);
    expect(String(fetchMock.mock.calls[1][0])).toMatch(/\/v2\/cases\/42\/documents\/uploads\/pending$/);
  });
});

describe('apiService storage stage recovery', () => {
  it('previews by default and sends an encoded cursor for explicit apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      inspected: 0, eligible: 0, settled: 0, retained: 0,
      hasMore: false, nextAfterKey: null, items: [],
    }), { headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.reconcileStorageStages({ afterKey: 'stage:abc/def', limit: 25 });
    await apiService.reconcileStorageStages({ apply: true, limit: 25 });

    const [previewUrl, previewOptions] = fetchMock.mock.calls[0];
    expect(String(previewUrl)).toContain('/storage/capacity/stages/reconcile?apply=false&limit=25&afterKey=stage%3Aabc%2Fdef');
    expect(previewOptions?.method).toBe('POST');
    const [applyUrl, applyOptions] = fetchMock.mock.calls[1];
    expect(String(applyUrl)).toContain('/storage/capacity/stages/reconcile?apply=true&limit=25');
    expect(applyOptions?.method).toBe('POST');
  });
});

describe('apiService task invoice link', () => {
  it('passes the explicit work-in-invoice confirmation to the server', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ id: 42 }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.linkTaskBillingInvoice(42, 901, true);

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/42/billing-invoice');
    expect(options?.method).toBe('PUT');
    expect(options?.body).toBe(JSON.stringify({ invoiceId: 901, confirmedIncludedInInvoice: true }));
  });
});

describe('apiService public messenger document', () => {
  it('sends the one-time token only in a no-referrer POST body', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('pdf', {
      headers: {
        'content-type': 'application/pdf',
        'content-disposition': "attachment; filename*=UTF-8''contract.pdf",
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadPublicMessengerDocument(
      '11111111-1111-4111-8111-111111111111',
      'secret-token',
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).not.toContain('secret-token');
    expect(String(url)).not.toContain('?token=');
    expect(options?.method).toBe('POST');
    expect(options?.body).toBe(JSON.stringify({ token: 'secret-token' }));
    expect(options?.referrerPolicy).toBe('no-referrer');
    expect(options?.cache).toBe('no-store');
    expect(result.fileName).toBe('contract.pdf');
  });
});

describe('apiService object audit', () => {
  it('passes the abort signal to the paged object audit request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 2, pageSize: 25,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getObjectAuditEvents(
      { search: 'телефон', searchAliases: ['Phone', 'PhoneNumber'], objectType: 'client', page: 2, pageSize: 25 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/object-audit?');
    expect(String(url)).toContain('objectType=client');
    expect(String(url)).toContain('searchAliases=Phone');
    expect(String(url)).toContain('searchAliases=PhoneNumber');
    expect(String(url)).toContain('page=2');
    expect(options?.signal).toBe(controller.signal);
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
  });
});

describe('apiService spreadsheet import history', () => {
  it('requests only the selected operations page', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      id: 7,
      operations: [],
      operationsTotal: 2490,
      operationsPage: 3,
      operationsPageSize: 50,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getSpreadsheetImportBatch(7, 3, 50);

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/database/import/spreadsheets/batches/7?');
    expect(String(url)).toContain('page=3');
    expect(String(url)).toContain('pageSize=50');
  });
});

describe('apiService contacts directory', () => {
  it('passes the abort signal to the contacts request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getContacts(
      { scope: 'external', search: 'иванов', syncOnly: true },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/contacts?');
    expect(String(url)).toContain('scope=external');
    expect(String(url)).toContain('syncOnly=true');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService client reference dictionaries', () => {
  it('sends bounded search parameters and abort signal to every dictionary endpoint', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getOkinDictionary({ search: 'семья', limit: 15 }, { signal: controller.signal });
    await apiService.getOksoDictionary({ search: '40.03', limit: 20 }, { signal: controller.signal });
    await apiService.getOkpdtrDictionary({ search: 'юрист', limit: 25 }, { signal: controller.signal });

    expect(String(fetchMock.mock.calls[0][0])).toContain('/client-reference-data/okin?search=');
    expect(String(fetchMock.mock.calls[0][0])).toContain('limit=15');
    expect(String(fetchMock.mock.calls[1][0])).toContain('/client-reference-data/okso?search=40.03&limit=20');
    expect(String(fetchMock.mock.calls[2][0])).toContain('/client-reference-data/okpdtr?search=');
    expect(String(fetchMock.mock.calls[2][0])).toContain('limit=25');
    expect(fetchMock.mock.calls.every(([, options]) => options?.signal === controller.signal)).toBe(true);
  });
});

describe('apiService task journal settings', () => {
  it('deduplicates only simultaneous settings requests', async () => {
    const responseBody = {
      restrictExecutorEditing: true,
      stickerStyles: {},
      contractTaskBillingMode: 'retainContractTasks',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(responseBody), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const [first, second] = await Promise.all([
      apiService.getTaskJournalSettings(),
      apiService.getTaskJournalSettings(),
    ]);

    expect(first).toEqual(responseBody);
    expect(second).toEqual(responseBody);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await apiService.getTaskJournalSettings();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('apiService task journal files', () => {
  it('reads the pending page envelope with a cursor, cancellation and no cache', async () => {
    const page = { items: [], nextCursor: 'next-window' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(page), { headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    expect(await apiService.getTaskFileUploadPage(10, controller.signal, 'older-window')).toEqual(page);
    const [url, init] = fetchMock.mock.calls[0];
    const query = new URL(String(url), 'http://localhost').searchParams;
    expect(query.get('includePageInfo')).toBe('true');
    expect(query.get('pendingOnly')).toBe('true');
    expect(query.get('after')).toBe('older-window');
    expect(query.has('page')).toBe(false);
    expect(init?.signal).toBe(controller.signal);
    expect(init?.cache).toBe('no-store');
    expect(init?.body).toBeUndefined();
  });
  it('passes bounded batch keys and pending pagination without losing upload retry keys', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), { headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.getTaskFileUploadStates(10, undefined, { uploadKeys: ['one', 'two'], pendingOnly: true, page: 2 });
    const query = new URL(String(fetchMock.mock.calls[0][0]), 'http://localhost').searchParams;
    expect(query.getAll('uploadKeys')).toEqual(['one', 'two']);
    expect(query.get('pendingOnly')).toBe('true');
    expect(query.get('page')).toBe('2');
    const file = new File(['test'], 'file.txt');
    expect(apiService.getTaskFileUploadKey(10, file)).toBeUndefined();
    await apiService.uploadTaskJournalFile(10, file);
    expect(apiService.getTaskFileUploadKey(10, file)).toBe(new Headers(fetchMock.mock.calls[1][1]?.headers).get('X-Upload-Key'));
    expect(apiService.getTaskFileUploadKey(11, file)).toBeUndefined();
  });

  it('reads server upload states without resending files and supports cancellation', async () => {
    const states = [{ uploadKey: 'test-key', fileName: 'file.txt', status: 'waiting', fileId: null, message: 'Ожидание копий' }];
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(states), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    expect(await apiService.getTaskFileUploadStates(404, controller.signal)).toEqual(states);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/404/files/uploads');
    expect(init?.signal).toBe(controller.signal);
    expect(init?.cache).toBe('no-store');
    expect(init?.body).toBeUndefined();
  });

  it('keeps upload keys across retries and separates task and document scopes', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ id: 1 }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const file = new File(['test'], 'file.txt');
    await apiService.uploadTaskJournalFile(10, file);
    await apiService.uploadTaskJournalFile(10, file);
    await apiService.uploadTaskJournalFile(11, file);
    await apiService.uploadTaskJournalFile(10, file, 'doc');
    const keys = fetchMock.mock.calls.map(([, init]) => new Headers(init?.headers).get('X-Upload-Key'));
    expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/i);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[0]);
    expect(keys[3]).not.toBe(keys[0]);
    const init = fetchMock.mock.calls[0][1];
    expect(init?.body).toBeInstanceOf(FormData);
    expect(new Headers(init?.headers).has('Content-Type')).toBe(false);
  });

  it.each(['explicitNull', 'omitted', 'nextPage'])('normalizes the pending upload page cursor: %s', async (mode) => {
    const nextCursor = mode === 'nextPage' ? '00000000-0000-0000-0000-000000000001' : null;
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], ...(mode === 'omitted' ? {} : { nextCursor }),
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const page = await apiService.getTaskFileUploadPage(10);
    expect(page).toEqual({ items: [], nextCursor });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each(['explicitNull', 'omitted'])('recognizes the exact accepted task upload with %s file ID without treating its copies as ready', async (mode) => {
    localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '1', iss: 'owned-fixture' }))}.signature`);
    const file = new File(['test'], 'file.txt');
    const fetchMock = vi.fn<typeof fetch>(async (_url, options) => new Response(JSON.stringify({
      code: 'task_file_copies_pending', taskId: 10,
      uploadKey: new Headers(options?.headers).get('X-Upload-Key'), fileId: mode === 'omitted' ? undefined : null,
      message: 'Файл сохранён, обязательные копии ещё не готовы.',
    }), { status: 503, headers: { 'content-type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(apiService.uploadTaskJournalFile(10, file)).rejects.toMatchObject({
      name: 'TaskFileUploadAcceptedError', taskId: 10,
      uploadKey: expect.any(String),
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each(['wrongTask', 'wrongKey', 'missingCode', 'fileAlreadyPresent', 'malformed', 'nonJson', 'serverError', 'changedActor'])(
    'does not acknowledge an uncertain task upload: %s', async (mode) => {
      const token = (id: number) => `header.${btoa(JSON.stringify({ sub: String(id), iss: 'owned-fixture' }))}.signature`;
      localStorage.setItem('pp_token', token(1));
      const file = new File(['test'], 'file.txt');
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(async (_url, options) => {
        if (mode === 'changedActor') localStorage.setItem('pp_token', token(2));
        const payload = { code: mode === 'missingCode' ? undefined : 'task_file_copies_pending',
          taskId: mode === 'wrongTask' ? 999 : 10,
          uploadKey: mode === 'wrongKey' ? 'foreign-key' : new Headers(options?.headers).get('X-Upload-Key'),
          fileId: mode === 'fileAlreadyPresent' ? 77 : null };
        return new Response(mode === 'malformed' ? '{' : JSON.stringify(payload), {
          status: mode === 'serverError' ? 500 : 503,
          headers: { 'content-type': mode === 'nonJson' ? 'text/plain' : 'application/json' },
        });
      }));
      const error = await apiService.uploadTaskJournalFile(10, file).catch((cause: unknown) => cause);
      expect(error).toBeInstanceOf(Error);
      expect((error as Error).name).not.toBe('TaskFileUploadAcceptedError');
      expect((error as Error).message).toContain(mode === 'changedActor'
        ? 'Сессия изменилась' : mode === 'serverError' ? 'HTTP 500' : 'HTTP 503');
    });

  it('rejects an accepted receipt when the employee changes while its body is read', async () => {
    const token = (id: number) => `header.${btoa(JSON.stringify({ sub: String(id), iss: 'owned-fixture' }))}.signature`;
    localStorage.setItem('pp_token', token(1));
    const file = new File(['test'], 'file.txt');
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(async (_url, options) => {
      const payload = { code: 'task_file_copies_pending', taskId: 10,
        uploadKey: new Headers(options?.headers).get('X-Upload-Key'), fileId: null };
      const response = new Response(JSON.stringify(payload), { status: 503, headers: { 'content-type': 'application/json' } });
      vi.spyOn(response, 'clone').mockReturnValue({ json: async () => {
        localStorage.setItem('pp_token', token(2));
        return payload;
      } } as Response);
      return response;
    }));
    const error = await apiService.uploadTaskJournalFile(10, file).catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(Error);
    expect((error as Error).name).not.toBe('TaskFileUploadAcceptedError');
  });

  it('requests only latest document versions when asked', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getTaskJournalFiles(404, { latestOnly: true });

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/404/files?latestOnly=true');
  });
});

describe('apiService case access requests', () => {
  it('passes the abort signal to the pending access requests call', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getCaseAccessRequests({ signal: controller.signal });

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v2/cases/access-requests');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService accounts registry', () => {
  it('passes the abort signal to the paged accounts request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 3, pageSize: 20, summary: {},
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getAccountsRegistry(
      { search: 'счёт', status: 'overdue', page: 3, pageSize: 20 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/accounts/registry?');
    expect(String(url)).toContain('status=overdue');
    expect(String(url)).toContain('page=3');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService invoices', () => {
  it('passes abort signals to the invoice list and summary requests', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (url) => new Response(JSON.stringify(
      String(url).includes('/summary')
        ? { totalAmount: 0 }
        : { items: [], total: 0, page: 2, pageSize: 20 },
    ), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const listController = new AbortController();
    const summaryController = new AbortController();

    await apiService.getInvoices(
      { search: 'счёт', page: 2, pageSize: 20 },
      { signal: listController.signal },
    );
    await apiService.getInvoicesSummary(
      { search: 'счёт' },
      { signal: summaryController.signal },
    );

    expect(String(fetchMock.mock.calls[0][0])).toContain('/invoices?');
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(listController.signal);
    expect(String(fetchMock.mock.calls[1][0])).toContain('/invoices/summary?');
    expect(fetchMock.mock.calls[1][1]?.signal).toBe(summaryController.signal);
  });
});

describe('apiService acts', () => {
  it('passes abort signals to the acts list and summary requests', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (url) => new Response(JSON.stringify(
      String(url).includes('/summary')
        ? { totalAmount: 0, totalCount: 0 }
        : { items: [], total: 0, page: 2, pageSize: 20 },
    ), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const listController = new AbortController();
    const summaryController = new AbortController();

    await apiService.getActs(
      { search: 'акт', page: 2, pageSize: 20 },
      { signal: listController.signal },
    );
    await apiService.getActsSummary(
      { search: 'акт' },
      { signal: summaryController.signal },
    );

    expect(String(fetchMock.mock.calls[0][0])).toContain('/acts?');
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(listController.signal);
    expect(String(fetchMock.mock.calls[1][0])).toContain('/acts/summary?');
    expect(fetchMock.mock.calls[1][1]?.signal).toBe(summaryController.signal);
  });
});

describe('apiService contracts', () => {
  it('passes the abort signal to the contracts request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 2, pageSize: 20,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getContracts(
      { search: 'договор', status: 'Draft', page: 2, pageSize: 20 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/contracts?');
    expect(String(url)).toContain('status=Draft');
    expect(String(url)).toContain('page=2');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService client cases', () => {
  it('passes the abort signal to the v1 cases request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getCasesV1(
      { clientId: 17, search: 'дело', sortBy: 'createdAt', sortDir: 'desc' },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v1/cases?');
    expect(String(url)).toContain('clientId=17');
    expect(String(url)).toContain('sortDir=desc');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService summary stats', () => {
  it('passes the abort signal to the summary request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({}), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getSummaryStats(
      { clientId: 17, from: '2026-07-01', to: '2026-07-31', r: 3 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v2/stats/summary?');
    expect(String(url)).toContain('clientId=17');
    expect(String(url)).toContain('r=3');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService money operations', () => {
  it.each(['read', 'run'])('requests currency-aware saved reconciliation: %s', async (operation) => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(operation === 'read' ? [] : {}), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    if (operation === 'read') await apiService.getMoneyBalanceReconciliationResults({ source: 'Cash' });
    else await apiService.runMoneyBalanceReconciliation({ source: 'Cash', moneyAccountId: 201 });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain(`/money-operations/balance-reconciliation${operation === 'run' ? '/run' : ''}?`);
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
    if (operation === 'run') {
      expect(options?.method).toBe('POST');
      expect(String(url)).toContain('moneyAccountId=201');
    }
  });

  it('requests currency-aware daily balances', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.getMoneyOperationDailyBalances({ source: 'Cash' });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/money-operations/daily-balances?');
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
  });

  it('requests currency-aware account summaries', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    await apiService.getMoneyOperationAccountSummaries({ source: 'Bank' });
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/money-operations/account-summaries?');
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
  });

  it('passes the abort signal to the paged money operations request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 2, pageSize: 25, summary: {},
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getMoneyOperationsPaged(
      { source: 'Bank', direction: 'Income', search: 'аванс', page: 2, pageSize: 25 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/money-operations/paged?');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('page=2');
    expect(options?.signal).toBe(controller.signal);
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
  });
});

describe('apiService paged payments', () => {
  it('passes the abort signal to the v2 payments request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 4, pageSize: 25, summary: {},
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getPaymentsV2(
      { status: 'Pending', type: 'Income', search: 'аванс', page: 4, pageSize: 25 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v2/payments?');
    expect(String(url)).toContain('status=Pending');
    expect(String(url)).toContain('page=4');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('apiService legacy payments', () => {
  it('passes the abort signal without changing the object-style payments query', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getPayments(
      { from: '2026-07-01', to: '2026-07-31', clientId: 17, sortBy: 'date', sortDir: 'desc' },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/payments?');
    expect(String(url)).toContain('from=2026-07-01');
    expect(String(url)).toContain('clientId=17');
    expect(String(url)).toContain('sortDir=desc');
    expect(options?.signal).toBe(controller.signal);
  });

  it('keeps the positional payments signature unchanged', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getPayments('2026-06-01', '2026-06-30', 23);

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/payments?');
    expect(String(url)).toContain('from=2026-06-01');
    expect(String(url)).toContain('to=2026-06-30');
    expect(String(url)).toContain('clientId=23');
  });
});

describe('apiService cash order reads', () => {
  it('passes the abort signal to the cash order register request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 2, pageSize: 10,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getCashOrderRegister(
      { type: 'Incoming', status: 'Posted', accountName: 'Основная касса', page: 2, pageSize: 10 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/cash-orders/register?');
    expect(String(url)).toContain('type=Incoming');
    expect(String(url)).toContain('page=2');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes the abort signal to the cash book request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ days: [] }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getCashBook(
      { from: '2026-07-01', to: '2026-07-31', accountName: 'Основная касса' },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/cash-orders/cashbook?');
    expect(String(url)).toContain('from=2026-07-01');
    expect(String(url)).toContain('accountName=');
    expect(options?.signal).toBe(controller.signal);
    expect(new Headers(options?.headers).get('X-PayPlanner-Currency-Contract')).toBe('1');
  });
});

describe('extractResponseErrorMessage', () => {
  it('prefers problem details detail field for API errors', async () => {
    const response = new Response(
      JSON.stringify({
        title: 'Service Unavailable',
        detail: 'На сервере не найден Chromium/Chrome для генерации PDF.',
      }),
      {
        status: 503,
        headers: {
          'Content-Type': 'application/problem+json',
        },
      },
    );

    await expect(extractResponseErrorMessage(response)).resolves.toBe(
      'На сервере не найден Chromium/Chrome для генерации PDF.',
    );
  });

  it('prefers validation error details over generic problem title', async () => {
    const response = new Response(
      JSON.stringify({
        title: 'One or more validation errors occurred.',
        errors: {
          Comment: ['The field Comment must be a string or array type with a maximum length of 1000.'],
        },
      }),
      {
        status: 400,
        headers: {
          'Content-Type': 'application/problem+json',
        },
      },
    );

    await expect(extractResponseErrorMessage(response)).resolves.toBe(
      'The field Comment must be a string or array type with a maximum length of 1000.',
    );
  });

  it('preserves required field keys so forms can show their user-facing labels', async () => {
    const response = new Response(
      JSON.stringify({
        message: 'Не заполнены обязательные поля',
        missingFields: ['OrganizationForm'],
      }),
      {
        status: 400,
        headers: {
          'Content-Type': 'application/json',
        },
      },
    );

    await expect(extractResponseErrorMessage(response)).resolves.toBe(
      JSON.stringify({
        message: 'Не заполнены обязательные поля',
        missingFields: ['OrganizationForm'],
      }),
    );
  });

  it('falls back to plain text when response is not json', async () => {
    const response = new Response('Короткая текстовая ошибка', {
      status: 400,
      headers: {
        'Content-Type': 'text/plain; charset=utf-8',
      },
    });

    await expect(extractResponseErrorMessage(response)).resolves.toBe('Короткая текстовая ошибка');
  });
});

describe('resolveApiBaseUrl', () => {
  it('uses secure production API on HTTPS pages when configured URL is insecure', () => {
    expect(
      resolveApiBaseUrl({
        configuredUrl: 'http://95.81.124.34:5080/api',
        isProduction: true,
        pageProtocol: 'https:',
      }),
    ).toBe('https://api.elitpravo.com/api');
  });

  it('keeps local API fallback for development', () => {
    expect(resolveApiBaseUrl({ isProduction: false, pageProtocol: 'http:' })).toBe(
      'http://localhost:5080/api',
    );
  });
});

describe('sticky note save conflict response', () => {
  it.each(['text', 'layout'] as const)('returns the current note to the %s conflict UI', async (kind) => {
    const serverNote = { id: 11, boardId: 9, text: 'Актуальная версия', color: 'yellow', x: 150, y: 25,
      width: 220, height: 180, fontSize: 14, isBold: false, isItalic: false, isUnderline: false,
      opacity: 100, zIndex: 1, version: 2, isPinned: false, assignedUserName: '', authorUserName: '',
      lastUpdatedByUserName: '', createdAt: '2026-10-28T00:00:00Z' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(serverNote), {
      status: 409, headers: { 'content-type': 'application/json; charset=utf-8' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const layout = { x: 10, y: 20, width: 220, height: 180, zIndex: 1, version: 1 };
    const request = kind === 'text'
      ? apiService.updateStickyNote(11, { ...layout, text: 'Мой черновик', color: 'yellow', isPinned: false })
      : apiService.updateStickyNoteLayout(11, layout);
    await expect(request).rejects.toMatchObject({ name: 'StickyNoteConflictError', serverNote });
    await expect(request).rejects.toBeInstanceOf(StickyNoteConflictError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each(['text', 'layout'] as const)('treats a temporary %s access lock as a retryable error, not a version conflict', async (kind) => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ message: 'Доступ сейчас изменяется.' }), {
      status: 423, headers: { 'content-type': 'application/json' },
    })));
    const layout = { x: 10, y: 20, width: 220, height: 180, zIndex: 1, version: 1 };
    const request = kind === 'text'
      ? apiService.updateStickyNote(11, { ...layout, text: 'Мой черновик', color: 'yellow', isPinned: false })
      : apiService.updateStickyNoteLayout(11, layout);
    await expect(request).rejects.toThrow('HTTP 423');
    await expect(request).rejects.not.toBeInstanceOf(StickyNoteConflictError);
  });
});

describe('sticky notes realtime contract', () => {
  it('keeps board update events in the shared frontend contract', () => {
    expect(STICKY_NOTE_REALTIME_EVENT_TYPES).toContain('boardUpdated');
    expect(STICKY_NOTE_REALTIME_EVENT_TYPES).toContain('boardMembersChanged');
  });
});

describe('client DaData API', () => {
  it('uses server integration settings endpoints for DaData credentials', async () => {
    const payload = {
      apiKeyConfigured: true,
      secretConfigured: true,
      apiKeyMasked: '••••6314',
      secretMasked: '••••2906',
      canEdit: true,
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getDadataIntegrationSettings()).resolves.toEqual(payload);
    await apiService.updateDadataIntegrationSettings({ apiKey: 'token' });

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(String(fetchMock.mock.calls[0][0])).toContain('/integrations/dadata/settings');
    expect(fetchMock.mock.calls[0][1]?.method).toBeUndefined();
    expect(String(fetchMock.mock.calls[1][0])).toContain('/integrations/dadata/settings');
    expect(fetchMock.mock.calls[1][1]?.method).toBe('PUT');
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({ apiKey: 'token' });
  });

  it('uses the client clean-address endpoint without legacy fallback routes', async () => {
    const payload = {
      result: '446200, Самарская обл, г Новокуйбышевск, ул Дзержинского, д 14, кв 9',
      postalCode: '446200',
      regionWithType: 'Самарская обл',
      cityWithType: 'г Новокуйбышевск',
      streetWithType: 'ул Дзержинского',
      house: '14',
      flat: '9',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.cleanAddress('г.Новокуйбышевск, ул. Дзержинского д.14.кв.9');

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/clients/dadata/clean-address');
    expect(String(url)).not.toContain('/legal-entities/clean/address');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      address: 'г.Новокуйбышевск, ул. Дзержинского д.14.кв.9',
    });
  });
});

describe('payments advance review API', () => {
  it('requests advance diagnostics with search filters', async () => {
    const payload = {
      items: [],
      total: 0,
      totalAmount: 0,
      limit: 20,
      amountTolerance: 0.01,
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentAdvanceDiagnostics({
      q: 'Винокуров',
      from: '2026-03-02',
      to: '2026-03-02',
      amount: 7100,
      amountTolerance: 0.01,
      limit: 20,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    const requestUrl = new URL(String(url));
    expect(requestUrl.pathname).toContain('/payments/advance-review/diagnostics');
    expect(requestUrl.searchParams.get('q')).toBe('Винокуров');
    expect(requestUrl.searchParams.get('from')).toBe('2026-03-02');
    expect(requestUrl.searchParams.get('to')).toBe('2026-03-02');
    expect(requestUrl.searchParams.get('amount')).toBe('7100');
    expect(requestUrl.searchParams.get('amountTolerance')).toBe('0.01');
    expect(requestUrl.searchParams.get('limit')).toBe('20');
  });

  it('refunds an accepted advance with reason', async () => {
    const payload = { id: 77, advanceReviewStatus: 'Refunded' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.refundPaymentAdvance(77, 'Возврат клиенту');

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/payments/77/advance-review/refund');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify({ reason: 'Возврат клиенту' }));
  });
});

describe('apiService auth handling', () => {
  it('treats an empty current appeal decision as not yet decided', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getCurrentCaseAppealDecision(42)).resolves.toBeNull();
    expect(String(fetchMock.mock.calls[0][0])).toContain('/v2/cases/42/appeal-decision');
  });

  it('returns null for missing optional user settings', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('', {
      status: 404,
      statusText: 'Not Found',
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getUserSetting('money.bankStatement.autoImport')).resolves.toBeNull();

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/user-settings/money.bankStatement.autoImport');
  });

  it('returns null for empty optional user settings responses', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getUserSetting('notifications.typePreferences.v1')).resolves.toBeNull();
  });

  it('loads feature flags from the backend', async () => {
    const payload = {
      legal: {
        externalCourtSync: false,
        externalCalendarSync: false,
        courtEvents: true,
        documentTemplateLibrary: false,
        externalWorkOrderApproval: false,
        clientPortal: false,
        offlineCalendarReadOnly: false,
      },
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getFeatureFlags();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/features');
  });

  it('loads calendar sync provider diagnostics from the backend', async () => {
    const payload = [
      {
        key: 'ics-export',
        label: 'ICS-файлы',
        kind: 'ics',
        status: 'active',
        isAvailable: true,
        featureFlagEnabled: true,
        requiresOAuth: false,
        supportsManualExport: true,
        supportsBackgroundSync: false,
        supportsTwoWaySync: false,
        supportedTargets: ['Календарь задач'],
        message: 'ICS доступен',
      },
    ];
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCalendarSyncProviders();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/calendar-sync/providers');
  });

  it('loads calendar sync readiness checklist from the backend', async () => {
    const payload = {
      generatedAt: '2026-05-26T16:00:00Z',
      externalCalendarSyncEnabled: true,
      googleCalendarSyncEnabled: true,
      yandexCalendarSyncEnabled: false,
      readyToConnect: false,
      items: [
        {
          key: 'conflict-policy',
          label: 'Правила конфликтов',
          status: 'pending',
          isReady: false,
          isRequired: true,
          severity: 'warning',
          message: 'Нужно утвердить модель конфликтов.',
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCalendarSyncReadiness();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/calendar-sync/readiness');
  });

  it('loads calendar sync settings from the backend', async () => {
    const payload = {
      externalCalendarSyncEnabled: true,
      googleCalendarSyncEnabled: true,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      manualFullSyncCooldownMinutes: 10,
      completedTaskCalendarAction: 'color',
      completedTaskGoogleColorId: '8',
      syncMode: 'hybrid',
      conflictResolutionMode: 'manualConfirmation',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: '2026-05-26T17:00:00Z',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCalendarSyncSettings();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/calendar-sync/settings');
  });

  it('updates calendar sync settings', async () => {
    const payload = {
      externalCalendarSyncEnabled: true,
      googleCalendarSyncEnabled: true,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      manualFullSyncCooldownMinutes: 15,
      completedTaskCalendarAction: 'hide',
      completedTaskGoogleColorId: '8',
      syncMode: 'crmMaster',
      conflictResolutionMode: 'crmWins',
      requireManualConfirmation: false,
      dryRunConfirmed: true,
      notes: 'Проверено',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ ...payload, updatedAt: '2026-05-26T17:00:00Z' }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.updateCalendarSyncSettings(payload);

    expect(result.syncMode).toBe('crmMaster');
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/calendar-sync/settings');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual(payload);
  });

  it('loads Google Calendar status from the backend', async () => {
    const payload = {
      googleOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/google-calendar/auth/callback',
      connection: {
        id: 1,
        googleEmail: 'user@example.com',
        isEnabled: true,
        syncEnabled: true,
        calendarMode: 'personal',
        conflictResolutionMode: 'latestWins',
        manualFullSyncCooldownMinutes: 10,
        manualFullSyncAvailableAt: '2026-06-01T10:10:00Z',
        createdAt: '2026-06-01T10:00:00Z',
        updatedAt: '2026-06-01T10:00:00Z',
        calendars: [],
      },
      recentLogs: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getGoogleCalendarStatus();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/google-calendar/status');
  });

  it('starts Google Calendar OAuth flow', async () => {
    const payload = {
      state: 'state-1',
      authUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=state-1',
      pollIntervalMs: 2000,
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.startGoogleCalendarAuth('/?ppTab=profile');

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/google-calendar/auth/start');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ returnUrl: '/?ppTab=profile' });
  });

  it('runs Google Calendar sync now', async () => {
    const payload = {
      processedTasks: 2,
      importedEvents: 1,
      errorCount: 0,
      message: 'Синхронизация выполнена.',
      status: {
        googleOAuthReady: true,
        callbackUrl: 'https://crm.example.com/api/google-calendar/auth/callback',
        connection: null,
        recentLogs: [],
      },
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.syncGoogleCalendarNow();

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/google-calendar/sync-now');
    expect(init?.method).toBe('POST');
  });

  it('loads Yandex Calendar status from the backend', async () => {
    const payload = {
      yandexOAuthReady: true,
      callbackUrl: 'https://crm.example.com/api/yandex-calendar/auth/callback',
      connection: {
        id: 1,
        yandexEmail: 'user@yandex.ru',
        isEnabled: true,
        syncEnabled: true,
        calendarMode: 'personal',
        conflictResolutionMode: 'latestWins',
        manualFullSyncCooldownMinutes: 10,
        manualFullSyncAvailableAt: '2026-06-02T10:10:00Z',
        createdAt: '2026-06-02T10:00:00Z',
        updatedAt: '2026-06-02T10:00:00Z',
        calendars: [],
      },
      recentLogs: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getYandexCalendarStatus();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-calendar/status');
  });

  it('starts Yandex Calendar OAuth flow', async () => {
    const payload = {
      state: 'state-1',
      authUrl: 'https://oauth.yandex.ru/authorize?state=state-1',
      pollIntervalMs: 2000,
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.startYandexCalendarAuth('/?ppTab=profile');

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-calendar/auth/start');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ returnUrl: '/?ppTab=profile' });
  });

  it('runs Yandex Calendar sync now', async () => {
    const payload = {
      processedTasks: 2,
      importedEvents: 1,
      errorCount: 0,
      message: 'Синхронизация выполнена.',
      status: {
        yandexOAuthReady: true,
        callbackUrl: 'https://crm.example.com/api/yandex-calendar/auth/callback',
        connection: null,
        recentLogs: [],
      },
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.syncYandexCalendarNow();

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-calendar/sync-now');
    expect(init?.method).toBe('POST');
  });

  it('loads Yandex Disk settings from the backend', async () => {
    const payload = {
      isConfigured: true,
      rootPath: 'disk:/PayPlanner',
      tokenPreview: 'abcd...wxyz',
      updatedAt: '2026-06-01T10:00:00Z',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getYandexDiskSettings();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/settings');
  });

  it('runs Yandex Disk connection test', async () => {
    const payload = {
      success: true,
      rootPath: 'disk:/PayPlanner',
      testFolderPath: '/Acceptance',
      testFilePath: '/Acceptance/payplanner-connection-test.txt',
      checkedAt: '2026-06-01T08:00:00Z',
      steps: [
        {
          key: 'root',
          name: 'Проверка корневой папки',
          success: true,
          message: 'Корневая папка доступна.',
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.testYandexDiskConnection('/Acceptance');

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/connection-test');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ testFolderPath: '/Acceptance' });
  });

  it('loads Yandex Disk CRM sync settings from the backend', async () => {
    const payload = {
      enabled: true,
      syncInvoices: true,
      syncActs: false,
      syncContracts: true,
      syncCaseDocuments: true,
      syncTaskFiles: false,
      overwriteExisting: false,
      conflictResolutionMode: 'yandexDiskMaster',
      conflictResolutionModeText: 'Яндекс.Диск главный',
      folderTemplate: 'CRM/{clientName}',
      fileNameTemplate: '{documentDate}-{documentTitle}',
      availableTokens: ['clientName', 'documentTitle'],
      updatedAt: '2026-06-01T11:00:00Z',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getYandexDiskCrmSyncSettings();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm-sync/settings');
  });

  it('updates Yandex Disk CRM sync settings', async () => {
    const payload = {
      enabled: true,
      syncInvoices: true,
      syncActs: true,
      syncContracts: false,
      syncCaseDocuments: true,
      syncTaskFiles: true,
      overwriteExisting: true,
      conflictResolutionMode: 'crmMaster',
      folderTemplate: 'CRM/{clientName}/{caseNumber}',
      fileNameTemplate: '{documentDate}-{documentTitle}',
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ ...payload, availableTokens: [] }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.updateYandexDiskCrmSyncSettings(payload);

    expect(result.enabled).toBe(true);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm-sync/settings');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual(payload);
  });

  it('loads Yandex Disk CRM sync preview', async () => {
    const payload = {
      syncEnabled: true,
      overwriteExisting: false,
      conflictResolutionMode: 'yandexDiskMaster',
      conflictResolutionModeText: 'Яндекс.Диск главный',
      rootPath: 'disk:/PayPlanner',
      folderTemplate: 'CRM/{clientName}',
      fileNameTemplate: '{documentDate}-{documentTitle}',
      items: [
        {
          documentKind: 'invoice',
          documentType: 'Счет',
          enabled: true,
          originalFileName: 'invoice.pdf',
          targetPath: '/CRM/ООО Клиент/invoice.pdf',
          fullPath: 'disk:/PayPlanner/CRM/ООО Клиент/invoice.pdf',
          operation: 'create',
          operationText: 'Создать новый файл',
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getYandexDiskCrmSyncPreview();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm-sync/preview');
  });

  it('loads Yandex Disk CRM sync status report', async () => {
    const payload = {
      pendingCount: 1,
      failedCount: 2,
      successCount: 3,
      lastSuccessfulSyncAt: '2026-06-01T08:00:00Z',
      recentItems: [
        {
          documentKind: 'contract',
          objectType: 'contract',
          objectId: 10,
          parentObjectId: null,
          fileName: 'contract.pdf',
          yandexDiskPath: '/CRM/contract.pdf',
          status: 'success',
          statusText: 'Успешно',
          lastError: null,
          attemptCount: 1,
          lastSyncedAt: '2026-06-01T08:00:00Z',
          updatedAt: '2026-06-01T08:00:00Z',
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getYandexDiskCrmSyncStatus();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm-sync/status');
  });

  it('uploads a case document to Yandex Disk', async () => {
    const payload = { success: true, path: '/CRM/client/file.pdf' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.uploadCaseDocumentToYandexDisk(42, true);

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm/case-documents/42/upload?overwrite=true');
    expect(init?.method).toBe('POST');
  });

  it('uploads a task file to Yandex Disk', async () => {
    const payload = { success: true, path: '/CRM/tasks/file.pdf' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.uploadTaskJournalFileToYandexDisk(11, 42, false);

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/crm/tasks/11/files/42/upload?overwrite=false');
    expect(init?.method).toBe('POST');
  });

  it('uploads a contract document to Yandex Disk', async () => {
    const payload = { success: true, path: '/CRM/contracts/contract.pdf' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.uploadContractDocumentToYandexDisk(77, 'pdf', true);

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/contracts/77/yandex-disk?format=pdf&overwrite=true');
    expect(init?.method).toBe('POST');
  });

  it('uploads an invoice document to Yandex Disk', async () => {
    const payload = { success: true, path: '/CRM/invoices/invoice.pdf' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.uploadInvoiceDocumentToYandexDisk(88, 'pdf', {
      includeSignatureAndSeal: true,
      overwrite: false,
    });

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/invoices/88/yandex-disk?format=pdf&includeSignatureAndSeal=true&overwrite=false');
    expect(init?.method).toBe('POST');
  });

  it('uploads an act document to Yandex Disk', async () => {
    const payload = { success: true, path: '/CRM/acts/act.pdf' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.uploadActDocumentToYandexDisk(99, 'pdf', {
      includeSignatureAndSeal: false,
      overwrite: true,
    });

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/acts/99/yandex-disk?format=pdf&includeSignatureAndSeal=false&overwrite=true');
    expect(init?.method).toBe('POST');
  });

  it('uploads a Yandex Disk file with form data', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ success: true, path: '/docs/a.txt' }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const file = new File(['hello'], 'a.txt', { type: 'text/plain' });
    await apiService.uploadYandexDiskFile('/docs/a.txt', file, true);

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/yandex-disk/upload');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBeInstanceOf(FormData);
    expect((init?.headers as Record<string, string> | undefined)?.['Content-Type']).toBeUndefined();
  });

  it('loads system job diagnostics from the backend', async () => {
    const payload = {
      generatedAt: '2026-05-26T12:00:00Z',
      items: [
        {
          key: 'database-backup',
          name: 'Резервное копирование базы данных',
          description: 'Проверяет расписание.',
          enabled: true,
          status: 'active',
          statusText: 'Включено',
          intervalText: 'Расписание: 02:00,14:00',
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getSystemJobDiagnostics();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/system/jobs/diagnostics');
  });

  it('runs system job manually through the backend', async () => {
    const payload = {
      jobKey: 'database-backup',
      startedAt: '2026-05-26T12:05:00Z',
      finishedAt: '2026-05-26T12:05:02Z',
      processedCount: 1,
      skippedCount: null,
      summary: 'Создана ручная резервная копия базы данных.',
      diagnostics: {
        generatedAt: '2026-05-26T12:05:02Z',
        items: [],
      },
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.runSystemJob('database-backup');

    expect(result).toEqual(payload);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/system/jobs/database-backup/run');
    expect(init?.method).toBe('POST');
  });

  it('loads latest app version from the backend', async () => {
    const payload = {
      currentVersion: '1.12.0',
      latestRelease: {
        id: 1,
        releaseId: '2026-05-21-accounting-v2',
        version: '1.12.0',
        releasedAt: '2026-05-21T00:00:00',
        title: 'Обновление',
        summary: 'Описание',
        items: [],
      },
      seenByCurrentUser: false,
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getLatestAppVersion();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/app-version/latest');
  });

  it('marks app version release as seen', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.markAppVersionSeen('2026-05-21-accounting-v2');

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/app-version/mark-seen');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify({ releaseId: '2026-05-21-accounting-v2' }));
  });

  it('creates app release through admin API', async () => {
    const payload = {
      releaseId: '2026-05-22-manual',
      version: '1.13.0',
      releasedAt: '2026-05-22T00:00:00',
      title: 'Manual release',
      summary: 'Created manually',
      isActive: true,
      items: [{ type: 'new', text: 'Manual item' }],
    };
    const response = { id: 2, ...payload, source: 'manual', createdAt: '2026-05-22T00:00:00', updatedAt: '2026-05-22T00:00:00' };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(response), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.createAdminAppRelease(payload);

    expect(result).toEqual(response);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/app-version/admin/releases');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify(payload));
  });

  it('deletes app release through admin API', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.deleteAdminAppRelease(7);

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/app-version/admin/releases/7');
    expect(init?.method).toBe('DELETE');
  });

  it('loads role permission audit from the backend', async () => {
    const payload = {
      generatedAtUtc: '2026-05-20T00:00:00Z',
      totalRoles: 2,
      readyRoles: 1,
      rolesWithIssues: 1,
      rolesWithBroadDataScope: 1,
      catalogSections: ['cases'],
      dataScopedSections: ['cases'],
      roles: [
        {
          roleId: 7,
          roleName: 'lawyer',
          isSystemRole: false,
          assignedUsersCount: 3,
          configuredSectionsCount: 13,
          totalSectionsCount: 14,
          hasIssues: true,
          missingSections: ['cases'],
          unknownSections: [],
          duplicateSections: [],
          invalidViewScopeSections: [],
          capabilityWithoutViewSections: [],
          restrictedCapabilitySections: [],
          broadDataScopeSections: ['clients'],
        },
      ],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getRolePermissionsAudit();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/role-permissions/audit');
  });

  it('downloads role permission audit csv from the backend', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('RoleId;RoleName', {
      headers: {
        'content-type': 'text/csv',
        'content-disposition': 'attachment; filename="roles-audit.csv"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.exportRolePermissionsAuditCsv();

    expect(await result.blob.text()).toBe('RoleId;RoleName');
    expect(result.fileName).toBe('roles-audit.csv');
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/role-permissions/audit.csv');
  });

  it('uses the primary pp_token storage key for API requests', async () => {
    localStorage.setItem('pp_token', 'main-token');
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getTaskJournalNotifications();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [, init] = fetchMock.mock.calls[0];
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer main-token');
  });

  it('loads the notification inbox with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [],
      total: 0,
      page: 2,
      pageSize: 10,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getUserNotifications({
      status: 'unread',
      type: 'taskChanged',
      dateFrom: '2026-05-01',
      dateTo: '2026-05-21',
      employeeId: 12,
      clientId: 34,
      taskRole: 'observer',
      domain: 'finance',
      requiresAction: true,
      search: 'договор',
      page: 2,
      pageSize: 10,
    });

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/notifications/inbox?');
    expect(String(url)).toContain('status=unread');
    expect(String(url)).toContain('type=taskChanged');
    expect(String(url)).toContain('search=');
    expect(String(url)).toContain('dateFrom=2026-05-01');
    expect(String(url)).toContain('dateTo=2026-05-21');
    expect(String(url)).toContain('employeeId=12');
    expect(String(url)).toContain('clientId=34');
    expect(String(url)).toContain('taskRole=observer');
    expect(String(url)).toContain('domain=finance');
    expect(String(url)).toContain('requiresAction=true');
    expect(String(url)).toContain('page=2');
    expect(String(url)).toContain('pageSize=10');
  });

  it('marks all filtered inbox notifications as read', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      updatedCount: 3,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.markAllUserNotificationsRead({
      status: 'unread',
      type: 'taskReminder',
      dateFrom: '2026-05-01',
      dateTo: '2026-05-21',
      employeeId: 12,
      clientId: 34,
      taskRole: 'author',
      domain: 'case',
      requiresAction: true,
      search: 'срок',
    });

    expect(result.updatedCount).toBe(3);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/notifications/inbox/read-all?');
    expect(String(url)).toContain('status=unread');
    expect(String(url)).toContain('type=taskReminder');
    expect(String(url)).toContain('search=');
    expect(String(url)).toContain('dateFrom=2026-05-01');
    expect(String(url)).toContain('dateTo=2026-05-21');
    expect(String(url)).toContain('employeeId=12');
    expect(String(url)).toContain('clientId=34');
    expect(String(url)).toContain('taskRole=author');
    expect(String(url)).toContain('domain=case');
    expect(String(url)).toContain('requiresAction=true');
    expect(init?.method).toBe('POST');
  });

  it('sends selected inbox notifications to archive, restore and delete endpoints', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      updatedCount: 2,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const archiveResult = await apiService.archiveUserNotifications([10, 11]);
    const unarchiveResult = await apiService.unarchiveUserNotifications([10, 11]);
    const deleteResult = await apiService.deleteUserNotifications([12, 13]);

    expect(archiveResult.updatedCount).toBe(2);
    expect(unarchiveResult.updatedCount).toBe(2);
    expect(deleteResult.updatedCount).toBe(2);
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const [archiveUrl, archiveInit] = fetchMock.mock.calls[0];
    expect(String(archiveUrl)).toContain('/notifications/inbox/bulk/archive');
    expect(archiveInit?.method).toBe('POST');
    expect(archiveInit?.body).toBe(JSON.stringify({ ids: [10, 11] }));

    const [unarchiveUrl, unarchiveInit] = fetchMock.mock.calls[1];
    expect(String(unarchiveUrl)).toContain('/notifications/inbox/bulk/unarchive');
    expect(unarchiveInit?.method).toBe('POST');
    expect(unarchiveInit?.body).toBe(JSON.stringify({ ids: [10, 11] }));

    const [deleteUrl, deleteInit] = fetchMock.mock.calls[2];
    expect(String(deleteUrl)).toContain('/notifications/inbox/bulk/delete');
    expect(deleteInit?.method).toBe('POST');
    expect(deleteInit?.body).toBe(JSON.stringify({ ids: [12, 13] }));
  });

  it('loads and updates notification role preferences', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (_url: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(
      init?.method === 'PUT'
        ? {
          roleId: 2,
          roleName: 'lawyer',
          isSystemRole: false,
          hasCustomPreferences: true,
          defaultProfileKey: 'lawyer',
          defaultProfileLabel: 'Юрист',
          defaultProfileDescription: 'Стандартный профиль юриста',
          preferences: { disabledTypes: ['taskComment'], disabledChannels: { taskAssigned: ['email'] } },
        }
        : {
          roles: [
            {
              roleId: 2,
              roleName: 'lawyer',
              isSystemRole: false,
              hasCustomPreferences: false,
              defaultProfileKey: 'lawyer',
              defaultProfileLabel: 'Юрист',
              defaultProfileDescription: 'Стандартный профиль юриста',
              preferences: { disabledTypes: [], disabledChannels: {} },
            },
          ],
        },
    ), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const loaded = await apiService.getNotificationRolePreferences();
    const updated = await apiService.updateNotificationRolePreferences(2, {
      disabledTypes: ['taskComment'],
      disabledChannels: { taskAssigned: ['email'] },
    });

    expect(loaded.roles[0].roleName).toBe('lawyer');
    expect(loaded.roles[0].hasCustomPreferences).toBe(false);
    expect(loaded.roles[0].defaultProfileLabel).toBe('Юрист');
    expect(updated.preferences.disabledTypes).toEqual(['taskComment']);
    const [getUrl] = fetchMock.mock.calls[0];
    const [putUrl, putInit] = fetchMock.mock.calls[1];
    expect(String(getUrl)).toContain('/notifications/role-preferences');
    expect(String(putUrl)).toContain('/notifications/role-preferences/2');
    expect(putInit?.method).toBe('PUT');
    expect(putInit?.body).toBe(JSON.stringify({
      disabledTypes: ['taskComment'],
      disabledChannels: { taskAssigned: ['email'] },
    }));
  });

  it('sends default role preference reset flag', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      roleId: 2,
      roleName: 'lawyer',
      isSystemRole: false,
      hasCustomPreferences: false,
      defaultProfileKey: 'lawyer',
      defaultProfileLabel: 'Юрист',
      defaultProfileDescription: 'Стандартный профиль юриста',
      preferences: { disabledTypes: [], disabledChannels: {} },
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.updateNotificationRolePreferences(2, {
      disabledTypes: [],
      disabledChannels: {},
      useDefault: true,
    });

    const [, putInit] = fetchMock.mock.calls[0];
    expect(putInit?.body).toBe(JSON.stringify({
      disabledTypes: [],
      disabledChannels: {},
      useDefault: true,
    }));
  });

  it('loads notification channel catalog from the backend', async () => {
    const payload = [
      {
        key: 'email',
        label: 'Email',
        description: 'SMTP',
        status: 'active',
        isEnabled: true,
        isSendable: true,
        supportsUserPreferences: true,
        supportsDeliveryLog: true,
        requiresProvider: true,
        isFuture: false,
      },
      {
        key: 'sms',
        label: 'SMS',
        description: 'Future adapter',
        status: 'planned',
        isEnabled: false,
        isSendable: false,
        supportsUserPreferences: false,
        supportsDeliveryLog: true,
        requiresProvider: true,
        isFuture: true,
      },
    ];
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getNotificationChannels();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/notifications/channels');
  });

  it('manages user web push subscriptions', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.includes('/notifications/inbox/push/public-key')) {
        return new Response(JSON.stringify({ enabled: true, ready: true, publicKey: 'public-key' }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      if (url.includes('/notifications/inbox/push/diagnostics')) {
        return new Response(JSON.stringify({
          enabled: true,
          ready: false,
          publicKeyConfigured: true,
          privateKeyConfigured: false,
          subjectConfigured: true,
          totalSubscriptionsCount: 2,
          activeSubscriptionsCount: 1,
          revokedSubscriptionsCount: 1,
          failedSubscriptionsCount: 1,
          usersWithActiveSubscriptionsCount: 1,
          lastSuccessfulDeliveryAt: null,
          lastFailedDeliveryAt: '2026-06-17T09:00:00Z',
          lastDeliveryError: 'Network error',
        }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      if (url.includes('/notifications/inbox/push/subscriptions/revoke')) {
        expect(init?.method).toBe('POST');
        expect(init?.body).toBe(JSON.stringify({ endpoint: 'https://push.example/subscription' }));
        return new Response(null, { status: 204 });
      }

      if (url.includes('/notifications/inbox/push/subscriptions') && init?.method === 'POST') {
        expect(init.body).toBe(JSON.stringify({
          endpoint: 'https://push.example/subscription',
          keys: {
            p256dh: 'p256dh',
            auth: 'auth',
          },
        }));
        return new Response(JSON.stringify({
          id: 7,
          endpoint: 'https://push.example/subscription',
          browser: 'Chrome',
          platform: 'Windows',
          deviceName: 'Chrome / Windows',
          isActive: true,
          createdAt: '2026-06-17T08:00:00Z',
          updatedAt: '2026-06-17T08:00:00Z',
        }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      return new Response(JSON.stringify([]), {
        headers: { 'content-type': 'application/json' },
      });
    });
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getWebPushPublicKey()).resolves.toMatchObject({ ready: true });
    await expect(apiService.getWebPushDiagnostics()).resolves.toMatchObject({
      ready: false,
      privateKeyConfigured: false,
      activeSubscriptionsCount: 1,
    });
    await expect(apiService.getUserPushSubscriptions()).resolves.toEqual([]);
    await expect(apiService.upsertUserPushSubscription({
      endpoint: 'https://push.example/subscription',
      keys: {
        p256dh: 'p256dh',
        auth: 'auth',
      },
    })).resolves.toMatchObject({ id: 7, isActive: true });
    await expect(apiService.revokeUserPushSubscriptionByEndpoint('https://push.example/subscription')).resolves.toBeUndefined();

    expect(fetchMock).toHaveBeenCalledTimes(5);
  });

  it('emits a session-expired event when the API returns 401', async () => {
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('', { status: 401, statusText: 'Unauthorized' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getTaskJournalNotifications()).rejects.toThrow('Сессия истекла. Войдите снова.');

    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });

  it('refreshes the session and retries a request once instead of logging the user out', async () => {
    localStorage.setItem('pp_token', 'old-token');
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.includes('/auth/refresh')) {
        expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer old-token');
        return new Response(JSON.stringify({ token: 'renewed-token' }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      const authorization = (init?.headers as Record<string, string>).Authorization;
      if (authorization === 'Bearer old-token') {
        return new Response('', { status: 401, statusText: 'Unauthorized' });
      }

      expect(authorization).toBe('Bearer renewed-token');
      return new Response(JSON.stringify([]), { headers: { 'content-type': 'application/json' } });
    });
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getTaskJournalNotifications()).resolves.toEqual([]);

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(listener).not.toHaveBeenCalled();
    expect(localStorage.getItem('pp_token')).toBe('renewed-token');
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });

  it('keeps a valid session when one endpoint still returns 401 after refresh', async () => {
    localStorage.setItem('pp_token', 'old-token');
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async (input) => {
      if (String(input).includes('/auth/refresh')) {
        return new Response(JSON.stringify({ token: 'renewed-token' }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      return new Response('Endpoint rejected the request', { status: 401, statusText: 'Unauthorized' });
    });
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getTaskJournalNotifications()).rejects.toThrow('Endpoint rejected the request');

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(listener).not.toHaveBeenCalled();
    expect(localStorage.getItem('pp_token')).toBe('renewed-token');
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });

  it('refreshes the session before retrying a Yandex Disk download', async () => {
    localStorage.setItem('pp_token', 'old-token');
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.includes('/auth/refresh')) {
        return new Response(JSON.stringify({ token: 'renewed-token' }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      const authorization = (init?.headers as Record<string, string>).Authorization;
      if (authorization === 'Bearer old-token') {
        return new Response('', { status: 401, statusText: 'Unauthorized' });
      }

      return new Response('file contents', {
        headers: { 'content-disposition': "attachment; filename*=UTF-8''report.txt" },
      });
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadYandexDiskFile('/reports/report.txt');

    expect(result.fileName).toBe('report.txt');
    expect(await result.blob.text()).toBe('file contents');
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(listener).not.toHaveBeenCalled();
    expect(localStorage.getItem('pp_token')).toBe('renewed-token');
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });

  it('does not end a refreshed session when an S3 download endpoint still returns 401', async () => {
    localStorage.setItem('pp_token', 'old-token');
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async (input) => {
      if (String(input).includes('/auth/refresh')) {
        return new Response(JSON.stringify({ token: 'renewed-token' }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      return new Response('Endpoint rejected the request', { status: 401, statusText: 'Unauthorized' });
    });
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.downloadS3StorageFile('storage-1', '/report.txt')).rejects.toThrow(
      'Endpoint rejected the request',
    );

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(listener).not.toHaveBeenCalled();
    expect(localStorage.getItem('pp_token')).toBe('renewed-token');
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });

  it('does not emit a session-expired event for optional web push diagnostics', async () => {
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('', { status: 401, statusText: 'Unauthorized' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getWebPushDiagnostics({ suppressAuthSessionExpired: true })).rejects.toThrow('HTTP 401 Unauthorized');

    expect(listener).not.toHaveBeenCalled();
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });
});

describe('apiService case calendar', () => {
  it('downloads task calendar ICS with client filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('task-calendar', {
      headers: {
        'content-disposition': 'attachment; filename="task-calendar.ics"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadTaskJournalCalendar({
      from: '2026-06-01',
      to: '2026-06-30',
      clientCaseId: 8,
      clientId: 15,
      legalEntityId: 22,
      reminderMinutes: [60, 15],
    });

    expect(result.fileName).toBe('task-calendar.ics');
    expect(await result.blob.text()).toBe('task-calendar');
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/calendar.ics?');
    expect(String(url)).toContain('from=2026-06-01');
    expect(String(url)).toContain('to=2026-06-30');
    expect(String(url)).toContain('clientCaseId=8');
    expect(String(url)).toContain('clientId=15');
    expect(String(url)).toContain('legalEntityId=22');
    expect(String(url)).toContain('reminderMinutes=60');
    expect(String(url)).toContain('reminderMinutes=15');
  });

  it('downloads case calendar ICS with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('case-calendar', {
      headers: {
        'content-disposition': 'attachment; filename="case-calendar.ics"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadCaseCalendarIcs({
      from: '2026-06-01',
      to: '2026-06-30',
      responsibleUserId: 7,
      includeTasks: false,
      reminderMinutes: [120, 30],
    });

    expect(result.fileName).toBe('case-calendar.ics');
    expect(await result.blob.text()).toBe('case-calendar');
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/case-calendar/events.ics?');
    expect(String(url)).toContain('from=2026-06-01');
    expect(String(url)).toContain('to=2026-06-30');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('includeTasks=false');
    expect(String(url)).toContain('reminderMinutes=120');
    expect(String(url)).toContain('reminderMinutes=30');
  });

  it('passes calendar visibility flag to task journal endpoints', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ items: [], total: 0, page: 1, pageSize: 100 }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getTaskJournalItems({ calendarView: true });

    const [itemsUrl] = fetchMock.mock.calls[0];
    expect(String(itemsUrl)).toContain('/tasks?');
    expect(String(itemsUrl)).toContain('calendarView=true');

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), {
      headers: { 'Content-Type': 'application/json' },
    }));

    await apiService.getTaskJournalAssignees({ calendarView: true });

    const [assigneesUrl] = fetchMock.mock.calls[1];
    expect(String(assigneesUrl)).toContain('/tasks/assignees?');
    expect(String(assigneesUrl)).toContain('calendarView=true');
  });

  it('passes server task journal filters, pagination, summary bounds and abort signals', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 2, pageSize: 25,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getTaskJournalItems({
      search: 'Наследство',
      status: 'active',
      assigneeUserId: 7,
      authorUserId: 8,
      participantRole: 'observer',
      approvalScope: true,
      metric: 'highPriority',
      clientId: 10,
      clientCaseId: 100,
      todayFrom: '2026-08-05T00:00:00.000Z',
      todayTo: '2026-08-06T00:00:00.000Z',
      includeDeadlineChangeRequests: true,
      page: 2,
      pageSize: 25,
      sortBy: 'author',
      sortDir: 'desc',
    }, { signal: controller.signal });

    const [itemsUrl, itemsOptions] = fetchMock.mock.calls[0];
    const itemsQuery = new URL(String(itemsUrl)).searchParams;
    expect(itemsQuery.get('search')).toBe('Наследство');
    expect(itemsQuery.get('status')).toBe('active');
    expect(itemsQuery.get('assigneeUserId')).toBe('7');
    expect(itemsQuery.get('authorUserId')).toBe('8');
    expect(itemsQuery.get('participantRole')).toBe('observer');
    expect(itemsQuery.get('approvalScope')).toBe('true');
    expect(itemsQuery.get('metric')).toBe('highPriority');
    expect(itemsQuery.get('clientCaseId')).toBe('100');
    expect(itemsQuery.get('page')).toBe('2');
    expect(itemsQuery.get('pageSize')).toBe('25');
    expect(itemsQuery.get('sortBy')).toBe('author');
    expect(itemsQuery.get('sortDir')).toBe('desc');
    expect(itemsOptions?.signal).toBe(controller.signal);

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      active: 2, overdue: 1, today: 1, highPriority: 1, unbilled: 3,
    }), { headers: { 'Content-Type': 'application/json' } }));
    await apiService.getTaskJournalSummary({
      status: 'all',
      participantRole: 'approver',
      todayFrom: '2026-08-05T00:00:00.000Z',
      todayTo: '2026-08-06T00:00:00.000Z',
    }, { signal: controller.signal });

    const [summaryUrl, summaryOptions] = fetchMock.mock.calls[1];
    const summaryQuery = new URL(String(summaryUrl)).searchParams;
    expect(String(summaryUrl)).toContain('/tasks/summary?');
    expect(summaryQuery.get('status')).toBe('all');
    expect(summaryQuery.get('participantRole')).toBe('approver');
    expect(summaryQuery.get('todayFrom')).toBe('2026-08-05T00:00:00.000Z');
    expect(summaryQuery.get('todayTo')).toBe('2026-08-06T00:00:00.000Z');
    expect(summaryQuery.has('page')).toBe(false);
    expect(summaryOptions?.signal).toBe(controller.signal);
  });

  it('passes an abort signal to the case automation search request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getTaskJournalCaseAutomationCandidates(
      { search: 'ИВАНОВ', offset: 100, automationState: 'waiting' },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/automation/case-candidates?');
    expect(String(url)).toContain('search=');
    expect(String(url)).toContain('offset=100');
    expect(String(url)).toContain('automationState=waiting');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes an abort signal to the case automation summary request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      totalCount: 4,
      readyCount: 2,
      waitingCount: 1,
      pausedCount: 1,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getTaskJournalCaseAutomationSummary(
      { inactiveDays: 14, escalationDays: 30, search: 'ПЕТРОВ' },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/automation/case-candidates/summary?inactiveDays=14');
    expect(String(url)).toContain('escalationDays=30');
    expect(String(url)).toContain('search=');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes the active search and critical scope to bulk case automation creation', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      createdTasks: [],
      skippedCount: 0,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.createTaskJournalCaseAutomationTasks({
      limit: 25,
      search: 'КОШЕЛЕВА',
      automationState: 'critical',
    });

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/tasks/automation/case-candidates/tasks?limit=25');
    expect(String(url)).toContain('search=');
    expect(String(url)).toContain('automationState=critical');
    expect(options?.method).toBe('POST');
  });

  it('passes an abort signal to the cases registry request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 1, pageSize: 25,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getCasesV2(
      { search: 'ЗЕМЦОВ', page: 1, pageSize: 25 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/v2/cases?');
    expect(String(url)).toContain('search=');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes an abort signal to the user activity request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 1, pageSize: 25,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getUserActivityLogs(
      {
        search: 'клиенты',
        searchAliases: ['Clients', 'ClientsV2'],
        status: 'Success',
        page: 1,
        pageSize: 25,
      },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/user-activity?');
    expect(String(url)).toContain('search=');
    expect(String(url)).toContain('searchAliases=Clients');
    expect(String(url)).toContain('searchAliases=ClientsV2');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes timezone-safe UTC boundaries to the user activity request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 1, pageSize: 25,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getUserActivityLogs({
      fromUtc: '2026-08-15T17:00:00.000Z',
      toUtc: '2026-08-16T16:59:00.000Z',
    });

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('fromUtc=2026-08-15T17%3A00%3A00.000Z');
    expect(String(url)).toContain('toUtc=2026-08-16T16%3A59%3A00.000Z');
  });

  it('passes an abort signal to the paged legal entities request', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      items: [], total: 0, page: 1, pageSize: 12,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    await apiService.getLegalEntitiesPaged(
      { search: 'ромашка', page: 1, pageSize: 12 },
      { signal: controller.signal },
    );

    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/legal-entities/paged?');
    expect(String(url)).toContain('search=');
    expect(options?.signal).toBe(controller.signal);
  });

  it('passes abort signals to both client list endpoints', async () => {
    const fetchMock = vi.fn<typeof fetch>();
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), {
      headers: { 'Content-Type': 'application/json' },
    }));
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      items: [], total: 0, page: 1, pageSize: 25,
    }), {
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const firstController = new AbortController();
    const secondController = new AbortController();

    await apiService.getClients({ signal: firstController.signal });
    await apiService.getClientsV2(
      { search: 'иванов', page: 1, pageSize: 25 },
      { signal: secondController.signal },
    );

    expect(String(fetchMock.mock.calls[0][0])).toContain('/clients');
    expect(fetchMock.mock.calls[0][1]?.signal).toBe(firstController.signal);
    expect(String(fetchMock.mock.calls[1][0])).toContain('/v2/clients?');
    expect(fetchMock.mock.calls[1][1]?.signal).toBe(secondController.signal);
  });
});

describe('apiService reports', () => {
  it('loads cash flow report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      accountName: 'Основной банк',
      openingBalance: 4000,
      income: 6000,
      expense: 1000,
      netCashFlow: 5000,
      closingBalance: 9000,
      incomeCount: 1,
      expenseCount: 1,
      bySource: [],
      byAccount: [],
      days: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCashFlowReport(
      {
        from: '2026-05-01', to: '2026-05-31', source: 'Bank', accountName: 'Основной банк',
        counterpartyName: 'Merkulov', clientId: 10, caseId: 20, contractId: 30, incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-flow?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads cash flow forecast report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      accountName: 'Основной банк',
      openingBalance: 8000,
      expectedIncome: 10000,
      expectedExpense: 4000,
      netProjectedFlow: 6000,
      projectedClosingBalance: 14000,
      incomeCount: 1,
      expenseCount: 1,
      days: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCashFlowForecastReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        accountName: 'Основной банк',
        counterpartyName: 'Merkulov',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-flow-forecast?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('accountName=');
    expect(String(url)).toContain('counterpartyName=Merkulov');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads cash book report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      accountName: 'Основная касса',
      openingBalance: 15000,
      income: 30000,
      expense: 12000,
      closingBalance: 33000,
      incomingCount: 3,
      outgoingCount: 2,
      days: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashBook({
      from: '2026-05-01',
      to: '2026-05-31',
      accountName: 'Основная касса',
      counterpartyName: 'Merkulov',
      clientId: 10,
      caseId: 20,
      contractId: 30,
      incomeTypeId: 40,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/cash-orders/cashbook?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('accountName=');
  });

  it('loads money category report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      incomeTotal: 15000,
      expenseTotal: 4000,
      netTotal: 11000,
      income: [{ categoryName: 'Услуги', amount: 15000, count: 2, share: 100 }],
      expense: [{ categoryName: 'Налоги', amount: 4000, count: 1, share: 100 }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getMoneyCategoryReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/categories?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads counterparty report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 15000,
      expenseTotal: 2000,
      netTotal: 13000,
      counterparties: [{
        counterpartyName: 'ООО Ромашка',
        income: 15000,
        expense: 2000,
        netTotal: 13000,
        totalAmount: 17000,
        incomeCount: 2,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCounterpartyReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
        counterpartyName: 'ООО Ромашка',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/counterparties?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('counterpartyName=%D0%9E%D0%9E%D0%9E+%D0%A0%D0%BE%D0%BC%D0%B0%D1%88%D0%BA%D0%B0');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads project report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 12000,
      expenseTotal: 3000,
      netTotal: 9000,
      projects: [{
        projectName: 'Проект Альфа',
        income: 12000,
        expense: 3000,
        netTotal: 9000,
        totalAmount: 15000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getProjectReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/projects?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads legal work report with filters and abort signal', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      statusFilter: 'Open',
      responsibleUserIdFilter: 7,
      caseTypeFilter: 'bankruptcy',
      totalCases: 1,
      activeCases: 1,
      closedCases: 0,
      lostCases: 0,
      onHoldCases: 0,
      overdueDeadlineCount: 0,
      upcomingDeadlineCount: 0,
      openTaskCount: 0,
      overdueTaskCount: 0,
      onApprovalTaskCount: 0,
      feeTotal: 30000,
      paidTotal: 12000,
      outstandingTotal: 18000,
      casesByStatus: [],
      casesByType: [],
      responsibles: [],
      financeByStatus: [],
      deadlinesByType: [],
      cases: [],
      overdueDeadlines: [],
      upcomingDeadlines: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getLegalWorkReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        status: 'Open',
        responsibleUserId: 7,
        caseType: 'bankruptcy',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/legal-work?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(options?.signal).toBe(controller.signal);
  });

  it('downloads legal work report exports with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('excel-data', {
      headers: {
        'content-disposition': 'attachment; filename="legal-work-report.xls"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadLegalWorkReport({
      format: 'excel',
      from: '2026-05-01',
      to: '2026-05-31',
      status: 'Open',
      responsibleUserId: 7,
      caseType: 'bankruptcy',
    });

    expect(result.fileName).toBe('legal-work-report.xls');
    expect(await result.blob.text()).toBe('excel-data');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/legal-work.xls?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
  });

  it('downloads management report exports with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('pdf-data', {
      headers: {
        'content-disposition': 'attachment; filename="contract-status-report.pdf"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadManagementReport({
      report: 'contract-statuses',
      format: 'pdf',
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Card',
      accountName: 'Основная карта',
      counterpartyName: 'ООО Ромашка',
      responsibleUserId: 7,
      limit: 100,
    });

    expect(result.fileName).toBe('contract-status-report.pdf');
    expect(await result.blob.text()).toBe('pdf-data');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/management.pdf?');
    expect(String(url)).toContain('report=contract-statuses');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Card');
    expect(String(url)).toContain('accountName=%D0%9E%D1%81%D0%BD%D0%BE%D0%B2%D0%BD%D0%B0%D1%8F+%D0%BA%D0%B0%D1%80%D1%82%D0%B0');
    expect(String(url)).toContain('counterpartyName=%D0%9E%D0%9E%D0%9E+%D0%A0%D0%BE%D0%BC%D0%B0%D1%88%D0%BA%D0%B0');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('limit=100');
  });

  it('downloads management report CSV exports with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('csv-data', {
      headers: {
        'content-disposition': 'attachment; filename="cash-flow-report.csv"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadManagementReport({
      report: 'cash-flow',
      format: 'csv',
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      accountName: 'Main account',
      clientId: 25,
      caseId: 12,
      contractId: 7,
      incomeTypeId: 3,
    });

    expect(result.fileName).toBe('cash-flow-report.csv');
    expect(await result.blob.text()).toBe('csv-data');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/management.csv?');
    expect(String(url)).toContain('report=cash-flow');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('clientId=25');
    expect(String(url)).toContain('caseId=12');
    expect(String(url)).toContain('contractId=7');
    expect(String(url)).toContain('incomeTypeId=3');
  });

  it('loads management report print HTML with filters', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('<!doctype html><html><body>print</body></html>', {
      headers: {
        'content-type': 'text/html; charset=utf-8',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getManagementReportPrintHtml({
      report: 'cash-flow-forecast',
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      accountName: 'Main account',
      clientId: 25,
      caseId: 12,
      contractId: 7,
      incomeTypeId: 3,
      horizonDays: 45,
    });

    expect(result).toContain('print');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/management.print?');
    expect(String(url)).toContain('report=cash-flow-forecast');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('clientId=25');
    expect(String(url)).toContain('caseId=12');
    expect(String(url)).toContain('contractId=7');
    expect(String(url)).toContain('incomeTypeId=3');
    expect(String(url)).toContain('horizonDays=45');
  });

  it('loads case activity report with filters and abort signal', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      inactivityDays: 14,
      escalationDays: 30,
      activity: 'inactive',
      totalVisibleCases: 10,
      activeCaseCount: 8,
      finishedCaseCount: 2,
      inactiveCaseCount: 3,
      escalatedCaseCount: 1,
      disabledCaseCount: 0,
      withActiveTasksCount: 5,
      withoutResponsibleCount: 1,
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCaseActivityReport(
      {
        inactiveDays: 14,
        escalationDays: 30,
        activity: 'inactive',
        limit: 25,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/case-activity?');
    expect(String(url)).toContain('inactiveDays=14');
    expect(String(url)).toContain('escalationDays=30');
    expect(String(url)).toContain('activity=inactive');
    expect(String(url)).toContain('limit=25');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads overdue procedural deadlines report with filters', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      asOf: '2026-05-26T00:00:00',
      deadlineTypeFilter: 'appeal',
      responsibleUserIdFilter: 7,
      limit: 25,
      totalOverdueCount: 1,
      criticalOverdueCount: 1,
      withoutResponsibleCount: 0,
      byType: [],
      byResponsible: [],
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getOverdueProceduralDeadlinesReport(
      {
        asOf: '2026-05-26',
        deadlineType: 'appeal',
        responsibleUserId: 7,
        limit: 25,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/overdue-deadlines?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('deadlineType=appeal');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('limit=25');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads current cases report with filters', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      asOf: '2026-05-26T00:00:00',
      statusFilter: 'Open',
      responsibleUserIdFilter: 7,
      caseTypeFilter: 'bankruptcy',
      limit: 50,
      totalCurrentCases: 1,
      openCases: 1,
      onHoldCases: 0,
      overdueCases: 1,
      withoutResponsibleCount: 0,
      withOpenTasksCount: 1,
      withOverdueTasksCount: 1,
      withOverdueDeadlinesCount: 0,
      byStatus: [],
      byResponsible: [],
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCurrentCasesReport(
      {
        asOf: '2026-05-26',
        status: 'Open',
        responsibleUserId: 7,
        caseType: 'bankruptcy',
        limit: 50,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/current-cases?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=50');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads task report with filters', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      asOf: '2026-05-26T00:00:00',
      scope: 'closed',
      assigneeUserIdFilter: 7,
      limit: 50,
      totalTaskCount: 3,
      openTaskCount: 1,
      closedTaskCount: 2,
      overdueTaskCount: 1,
      dueTodayTaskCount: 0,
      withoutAssigneeCount: 0,
      byStatus: [],
      byResponsible: [],
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getTaskReport(
      {
        asOf: '2026-05-26',
        scope: 'closed',
        assigneeUserId: 7,
        limit: 50,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/tasks?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('scope=closed');
    expect(String(url)).toContain('assigneeUserId=7');
    expect(String(url)).toContain('limit=50');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads case finance report with filters and abort signal', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      statusFilter: 'Open',
      responsibleUserIdFilter: 7,
      caseTypeFilter: 'bankruptcy',
      limit: 25,
      totalCases: 1,
      activeCases: 1,
      closedCases: 0,
      feeTotal: 30000,
      paidTotal: 12000,
      outstandingTotal: 18000,
      collectionRate: 40,
      fullyPaidCaseCount: 0,
      partiallyPaidCaseCount: 1,
      unpaidCaseCount: 0,
      overpaidCaseCount: 0,
      byStatus: [],
      byResponsible: [],
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCaseFinanceReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        status: 'Open',
        responsibleUserId: 7,
        caseType: 'bankruptcy',
        limit: 25,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/case-finance?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=25');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads case success report with filters and abort signal', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      statusFilter: 'Closed',
      responsibleUserIdFilter: 7,
      caseTypeFilter: 'bankruptcy',
      limit: 25,
      totalCases: 2,
      completedCases: 2,
      successfulCases: 1,
      lostCases: 1,
      inProgressCases: 0,
      onHoldCases: 0,
      successRate: 50,
      lossRate: 50,
      completionRate: 100,
      formulaNote: 'Closed = закрыто',
      byStatus: [],
      byResponsible: [],
      byType: [],
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCaseSuccessReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        status: 'Closed',
        responsibleUserId: 7,
        caseType: 'bankruptcy',
        limit: 25,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/case-success?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Closed');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=25');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads lawyer workload report with filters', async () => {
    const payload = {
      generatedAt: '2026-05-26T10:00:00Z',
      asOf: '2026-05-26T00:00:00',
      horizonDays: 14,
      lawyerUserIdFilter: 7,
      limit: 25,
      lawyerCount: 1,
      totalActiveCases: 2,
      totalOpenTasks: 3,
      totalOverdueTasks: 1,
      totalOverdueDeadlines: 1,
      overloadedLawyerCount: 1,
      totalPlannedHours: 6,
      items: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getLawyerWorkloadReport(
      {
        asOf: '2026-05-26',
        horizonDays: 14,
        lawyerUserId: 7,
        limit: 25,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/lawyer-workload?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('horizonDays=14');
    expect(String(url)).toContain('lawyerUserId=7');
    expect(String(url)).toContain('limit=25');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads direction report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 9000,
      expenseTotal: 2000,
      netTotal: 7000,
      directions: [{
        directionName: 'Консалтинг',
        income: 9000,
        expense: 2000,
        netTotal: 7000,
        totalAmount: 11000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getDirectionReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/directions?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads sales point report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 8000,
      expenseTotal: 1500,
      netTotal: 6500,
      salesPoints: [{
        salesPointName: 'Офис',
        income: 8000,
        expense: 1500,
        netTotal: 6500,
        totalAmount: 9500,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getSalesPointReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/sales-points?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads VAT report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeAmountTotal: 12000,
      incomeVatTotal: 2000,
      expenseAmountTotal: 6000,
      expenseVatTotal: 1000,
      vatBalance: 1000,
      rates: [{
        vatRate: 20,
        vatRateLabel: '20%',
        incomeBase: 10000,
        incomeVat: 2000,
        incomeGross: 12000,
        expenseBase: 5000,
        expenseVat: 1000,
        expenseGross: 6000,
        netVat: 1000,
        incomeCount: 1,
        expenseCount: 1,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getVatReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/vat?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads plan fact report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      planIncome: 10000,
      factIncome: 8000,
      incomeVariance: -2000,
      incomeCompletionPercent: 80,
      planExpense: 4000,
      factExpense: 5000,
      expenseVariance: 1000,
      expenseExecutionPercent: 125,
      planNetTotal: 6000,
      factNetTotal: 3000,
      netVariance: -3000,
      days: [],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPlanFactReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/plan-fact?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads advance report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 16000,
      expenseTotal: 4000,
      recognizedTotal: 10000,
      unrecognizedTotal: 10000,
      recognizedCount: 1,
      unrecognizedCount: 2,
      counterparties: [{
        counterpartyName: 'ООО Ромашка',
        incomeTotal: 16000,
        expenseTotal: 0,
        recognizedTotal: 10000,
        unrecognizedTotal: 6000,
        incomeCount: 2,
        expenseCount: 0,
        recognizedCount: 1,
        unrecognizedCount: 1,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getAdvanceReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/advances?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads payment priority report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 9000,
      expenseTotal: 3000,
      netTotal: 6000,
      priorities: [{
        priority: 1,
        priorityLabel: 'Высокий приоритет',
        income: 9000,
        expense: 3000,
        netTotal: 6000,
        totalAmount: 12000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPaymentPriorityReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-priorities?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads paid account report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 7000,
      expenseTotal: 2000,
      netTotal: 5000,
      accounts: [{
        paidAccountName: 'Расчетный счет',
        income: 7000,
        expense: 2000,
        netTotal: 5000,
        totalAmount: 9000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPaidAccountReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-accounts?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads paid invoice report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 5000,
      expenseTotal: 2000,
      netTotal: 3000,
      paidAmountTotal: 6400,
      invoices: [{
        paidInvoiceNumber: 'INV-10',
        income: 5000,
        expense: 2000,
        netTotal: 3000,
        operationTotal: 7000,
        paidAmountTotal: 6400,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPaidInvoiceReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-invoices?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads invoice status report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      invoiceCount: 2,
      incomeCount: 1,
      expenseCount: 1,
      paidCount: 1,
      overdueCount: 0,
      totalAmount: 5000,
      paidAmount: 3000,
      outstandingAmount: 2000,
      statuses: [{
        status: 'Paid',
        statusLabel: 'Оплачен',
        invoiceCount: 1,
        incomeCount: 1,
        expenseCount: 0,
        paidCount: 1,
        overdueCount: 0,
        totalAmount: 3000,
        paidAmount: 3000,
        outstandingAmount: 0,
        share: 60,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getInvoiceStatusReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
        accountName: 'Main account',
        counterpartyName: 'Target',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/invoice-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('counterpartyName=Target');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads act status report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      actCount: 2,
      createdCount: 1,
      transferredCount: 0,
      signedCount: 1,
      terminatedCount: 0,
      totalAmount: 4000,
      signedAmount: 3000,
      pendingAmount: 1000,
      statuses: [{
        workflowStatus: 'Signed',
        workflowStatusLabel: 'Подписан',
        actCount: 1,
        createdCount: 0,
        transferredCount: 0,
        signedCount: 1,
        terminatedCount: 0,
        totalAmount: 3000,
        signedAmount: 3000,
        pendingAmount: 0,
        share: 75,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getActStatusReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
        accountName: 'Main account',
        counterpartyName: 'Target',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/act-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('counterpartyName=Target');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads contract status report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      contractCount: 1,
      draftCount: 0,
      notSignedCount: 0,
      onSigningCount: 0,
      concludedCount: 1,
      terminatedCount: 0,
      cancelledCount: 0,
      openEndedCount: 0,
      totalAmount: 5000,
      activeAmount: 5000,
      closedAmount: 0,
      statuses: [{
        status: 'Concluded',
        statusLabel: 'Подписан',
        contractCount: 1,
        openEndedCount: 0,
        expiringCount: 0,
        totalAmount: 5000,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getContractStatusReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
        accountName: 'Main account',
        counterpartyName: 'Target',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/contract-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('counterpartyName=Target');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads payment order report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 6000,
      expenseTotal: 2000,
      netTotal: 4000,
      orders: [{
        paymentOrderNumber: 'PO-1',
        income: 6000,
        expense: 2000,
        netTotal: 4000,
        totalAmount: 8000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPaymentOrderReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-orders?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads payment purpose report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 8000,
      expenseTotal: 2000,
      netTotal: 6000,
      purposes: [{
        purpose: 'Оплата услуг',
        income: 8000,
        expense: 2000,
        netTotal: 6000,
        totalAmount: 10000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getPaymentPurposeReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Bank',
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-purposes?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads match status report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 6000,
      expenseTotal: 2000,
      netTotal: 4000,
      operationCount: 2,
      matchedCount: 1,
      needsAttentionCount: 1,
      statuses: [{
        status: 'Matched',
        statusLabel: 'Сверено',
        income: 6000,
        expense: 2000,
        netTotal: 4000,
        totalAmount: 8000,
        incomeCount: 1,
        expenseCount: 1,
        operationCount: 2,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getMatchStatusReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/match-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads journal status report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 6000,
      expenseTotal: 2000,
      netTotal: 4000,
      operationCount: 2,
      postedCount: 1,
      processingCount: 1,
      errorCount: 0,
      statuses: [{
        status: 'Posted',
        statusLabel: 'Проведено',
        income: 6000,
        expense: 2000,
        netTotal: 4000,
        totalAmount: 8000,
        incomeCount: 1,
        expenseCount: 1,
        operationCount: 2,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getJournalStatusReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/journal-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads currency report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 7000,
      expenseTotal: 2000,
      netTotal: 5000,
      currencies: [{
        currency: 'RUB',
        income: 7000,
        expense: 2000,
        netTotal: 5000,
        totalAmount: 9000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCurrencyReport(
      { from: '2026-05-01', to: '2026-05-31', source: 'Bank' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/currencies?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads cash order report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      postedIncomingTotal: 5000,
      postedOutgoingTotal: 2000,
      postedNetTotal: 3000,
      orderCount: 2,
      draftCount: 0,
      postedCount: 2,
      cancelledCount: 0,
      statuses: [{
        status: 'Posted',
        statusLabel: 'Проведен',
        incomingTotal: 5000,
        outgoingTotal: 2000,
        netTotal: 3000,
        totalAmount: 7000,
        incomingCount: 1,
        outgoingCount: 1,
        orderCount: 2,
        share: 100,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getCashOrderReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        source: 'Cash',
        accountName: 'Касса',
        counterpartyName: 'ООО Ромашка',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-orders?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Cash');
    expect(String(url)).toContain('accountName=%D0%9A%D0%B0%D1%81%D1%81%D0%B0');
    expect(String(url)).toContain('counterpartyName=%D0%9E%D0%9E%D0%9E+%D0%A0%D0%BE%D0%BC%D0%B0%D1%88%D0%BA%D0%B0');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads bank statement import report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      batchCount: 2,
      rowsReadTotal: 22,
      createdRowsTotal: 15,
      skippedRowsTotal: 4,
      errorRowsTotal: 3,
      rollbackRowsTotal: 0,
      appliedCount: 1,
      appliedWithErrorsCount: 1,
      rolledBackCount: 0,
      rollbackBlockedCount: 0,
      statuses: [{
        status: 'AppliedWithErrors',
        statusLabel: 'Применен с ошибками',
        batchCount: 1,
        rowsRead: 12,
        createdRows: 7,
        skippedRows: 2,
        errorRows: 3,
        rollbackRows: 0,
        share: 50,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getBankStatementImportReport(
      {
        from: '2026-05-01',
        to: '2026-05-31',
        accountName: 'Main account',
        counterpartyName: 'Target',
        clientId: 10,
        caseId: 20,
        contractId: 30,
        incomeTypeId: 40,
      },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/bank-statement-imports?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('accountName=Main+account');
    expect(String(url)).toContain('counterpartyName=Target');
    expect(String(url)).toContain('clientId=10');
    expect(String(url)).toContain('caseId=20');
    expect(String(url)).toContain('contractId=30');
    expect(String(url)).toContain('incomeTypeId=40');
    expect(options?.signal).toBe(controller.signal);
  });

  it('loads spreadsheet import report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      batchCount: 2,
      fileCount: 3,
      totalRows: 32,
      createdRows: 15,
      updatedRows: 6,
      skippedRows: 4,
      errorRows: 7,
      rollbackRows: 0,
      appliedCount: 1,
      appliedWithErrorsCount: 1,
      rolledBackCount: 0,
      rolledBackWithConflictsCount: 0,
      statuses: [{
        status: 'AppliedWithErrors',
        statusLabel: 'Применен с ошибками',
        batchCount: 1,
        fileCount: 1,
        totalRows: 12,
        createdRows: 5,
        updatedRows: 2,
        skippedRows: 2,
        errorRows: 3,
        rollbackRows: 0,
        share: 50,
      }],
    };
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();

    const result = await apiService.getSpreadsheetImportReport(
      { from: '2026-05-01', to: '2026-05-31' },
      { signal: controller.signal },
    );

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/spreadsheet-imports?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(options?.signal).toBe(controller.signal);
  });
});

describe('generated template retry identity', () => {
  const payload = { templateKey: ' custom ', documentType: ' Иск ', status: ' Подготовлен ' };
  const document = { id: 101, clientCaseId: 7, documentKey: 'template-custom', version: 1,
    documentType: 'Иск', status: 'Подготовлен' };
  const login = () => localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '1', iss: 'owned-fixture' }))}.signature`);
  const key = (options?: RequestInit) => (options?.headers as Record<string, string>)['X-Upload-Key'];
  it('retains the exact attempt after storage failure and starts a new generation after confirmed success', async () => {
    login();
    let count = 0;
    const request = vi.fn<typeof fetch>(async (_url, options) => ++count === 1
      ? new Response('{"message":"copies pending"}', { status: 503, headers: { 'content-type': 'application/json' } })
      : new Response(JSON.stringify(document), { status: 201,
        headers: { 'content-type': 'application/json', 'X-Upload-Key': key(options) } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.generateCaseDocumentTemplate(7, payload)).rejects.toThrow('503');
    const first = key(request.mock.calls[0][1]);
    expect(first).toMatch(/^[a-f0-9-]{36}$/i);
    expect(await apiService.generateCaseDocumentTemplate(7, payload)).toEqual(document);
    expect(key(request.mock.calls[1][1])).toBe(first);
    expect(JSON.parse(String(request.mock.calls[1][1]?.body))).toEqual({ templateKey: 'custom', documentType: 'Иск', status: 'Подготовлен' });
    await apiService.generateCaseDocumentTemplate(7, payload);
    expect(key(request.mock.calls[2][1])).not.toBe(first);
  });
  it.each(['pending', 'foreign-case', 'foreign-key'])('keeps identity when a %s response does not prove completion', async (failure) => {
    login();
    const request = vi.fn<typeof fetch>(async (_url, options) => new Response(JSON.stringify(
      failure === 'foreign-case' ? { ...document, clientCaseId: 8 } : document), {
      status: failure === 'pending' ? 202 : 201,
      headers: { 'content-type': 'application/json', 'X-Upload-Key': failure === 'foreign-key' ? 'foreign' : key(options) },
    }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.generateCaseDocumentTemplate(7, payload)).rejects.toThrow();
    await expect(apiService.generateCaseDocumentTemplate(7, payload)).rejects.toThrow();
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
  });
  it('does not send a generation when its retry identity cannot be persisted', async () => {
    login();
    const request = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', request);
    const spy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('owned persistence failure'); });
    try {
      await expect(apiService.generateCaseDocumentTemplate(7, payload)).rejects.toThrow('попытка не отправлена');
      expect(request).not.toHaveBeenCalled();
    } finally { spy.mockRestore(); }
  });
});

describe('generated work order retry identity', () => {
  const document = { id: 101, clientCaseId: 7, documentKey: 'work-order-42', version: 1,
    documentType: 'Заказ-наряд', status: 'Согласован' };
  const login = () => localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '1', iss: 'owned-fixture' }))}.signature`);
  const key = (options?: RequestInit) => new Headers(options?.headers).get('X-Upload-Key');
  it('keeps a failed attempt and starts a new one only after confirmed publication', async () => {
    login();
    const request = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('copies pending', { status: 503 }))
      .mockImplementation(async (_url, options) => new Response(JSON.stringify(document), { status: 201,
        headers: { 'content-type': 'application/json', 'X-Upload-Key': key(options)! } }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.generateCaseWorkOrderDocument(7)).rejects.toThrow('503');
    expect(await apiService.generateCaseWorkOrderDocument(7)).toEqual(document);
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
    await apiService.generateCaseWorkOrderDocument(7);
    expect(key(request.mock.calls[2][1])).not.toBe(key(request.mock.calls[0][1]));
    expect(request.mock.calls.every((call) => call[1]?.body === undefined && call[1]?.method === 'POST')).toBe(true);
  });
  it.each(['pending', 'foreign-case', 'wrong-type', 'foreign-key'])('retains identity on %s without automatic retries', async (failure) => {
    login();
    const value = failure === 'foreign-case' ? { ...document, clientCaseId: 8 }
      : failure === 'wrong-type' ? { ...document, documentType: 'Счёт' } : document;
    const request = vi.fn<typeof fetch>(async (_url, options) => new Response(JSON.stringify(value), {
      status: failure === 'pending' ? 202 : 201,
      headers: { 'content-type': 'application/json', 'X-Upload-Key': failure === 'foreign-key' ? 'foreign' : key(options)! },
    }));
    vi.stubGlobal('fetch', request);
    await expect(apiService.generateCaseWorkOrderDocument(7)).rejects.toThrow();
    expect(request).toHaveBeenCalledTimes(1);
    await expect(apiService.generateCaseWorkOrderDocument(7)).rejects.toThrow();
    expect(key(request.mock.calls[1][1])).toBe(key(request.mock.calls[0][1]));
  });
});
