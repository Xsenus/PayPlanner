import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';
import { pendingActCreates } from './actCreateAttempts';
import type { ActInput } from '../types';

const key = '11111111-1111-4111-8111-111111111111';
const body: ActInput = { number: 'INV-999', date: '2027-01-01', amount: 100, status: 'Created', clientId: 7 };
const saved = { id: 91, number: '1', date: body.date, amount: 100, status: 'Created', clientId: 7 };
beforeEach(() => {
  localStorage.clear(); sessionStorage.clear();
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'act-create-session');
  vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async () => localStorage.getItem('pp_token'));
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); sessionStorage.clear(); localStorage.clear(); });

describe('annual act HTTP contract', () => {
  it('POST sends the persisted UUID header and leaves numbering to the server', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(saved, { status: 201, headers: { 'X-Upload-Key': key } }));
    vi.stubGlobal('fetch', fetch);
    await expect(apiService.createAct(body, { attemptKey: key })).resolves.toEqual(saved);
    const [url, init] = fetch.mock.calls[0];
    expect(String(url)).toMatch(/\/acts$/);
    expect(init?.method).toBe('POST');
    expect((init?.headers as Record<string, string>)['X-Upload-Key']).toBe(key);
    expect(JSON.parse(String(init?.body))).toEqual({ ...body, number: '' });
    expect(pendingActCreates()).toMatchObject([{ key, state: 'confirmed', actId: 91 }]);
  });
  it('the existing no-options caller also gets a durable UUID', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (_url, init) => Response.json(saved,
      { status: 201, headers: { 'X-Upload-Key': (init?.headers as Record<string, string>)['X-Upload-Key'] } }));
    vi.stubGlobal('fetch', fetch);
    await apiService.createAct(body);
    expect((fetch.mock.calls[0][1]?.headers as Record<string, string>)['X-Upload-Key']).toMatch(/^[0-9a-f-]{36}$/);
  });
  it('a conflicting response key is uncertain until lookup returns the canonical act', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(Response.json({ ...saved, id: 999 }, { status: 201, headers: { 'X-Upload-Key': '22222222-2222-4222-8222-222222222222' } }))
      .mockResolvedValueOnce(Response.json(saved));
    vi.stubGlobal('fetch', fetch);
    await expect(apiService.createAct(body, { attemptKey: key })).resolves.toEqual(saved);
    expect(String(fetch.mock.calls[1][0])).toMatch(new RegExp(`/acts/create-attempts/${key}$`));
  });
  it('a missing cross-origin response key remains unknown when lookup is unavailable', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(Response.json(saved, { status: 201 }))
      .mockResolvedValueOnce(Response.json({ message: 'Not found' }, { status: 404 }));
    vi.stubGlobal('fetch', fetch);
    await expect(apiService.createAct(body, { attemptKey: key })).rejects.toThrow('Ответ не подтверждает');
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(String(fetch.mock.calls[1][0])).toMatch(new RegExp(`/acts/create-attempts/${key}$`));
  });
  it('preview is a read-only date query and PUT preserves manual numbers', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(Response.json({ year: 2027, nextNumber: '1' }))
      .mockResolvedValueOnce(Response.json({ ...saved, number: 'HISTORICAL-8' }));
    vi.stubGlobal('fetch', fetch);
    await apiService.getActNextNumber('2027-01-01');
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/acts\/next-number\?date=2027-01-01$/);
    expect(fetch.mock.calls[0][1]?.method).toBeUndefined();
    expect(fetch.mock.calls[0][1]?.body).toBeUndefined();
    await apiService.updateAct(91, { ...body, number: 'HISTORICAL-8' });
    expect(fetch.mock.calls[1][1]?.method).toBe('PUT');
    expect(JSON.parse(String(fetch.mock.calls[1][1]?.body)).number).toBe('HISTORICAL-8');
    expect((fetch.mock.calls[1][1]?.headers as Record<string, string>)['X-Upload-Key']).toBeUndefined();
  });
});
