import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { authService } from './authService';
import type { ClientCase } from '../types';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); session.restore(); });
const original = () => ({ id: 19, clientId: 3, title: 'Исходное дело', status: 'Open', strategy: '  Прежняя позиция  ', eventHistory: 'Прежняя история', nextHearingAt: '2099-11-03T12:00:00.1234567', client: { id: 3, name: 'Клиент' }, payments: [{ id: 6 }] }) as unknown as ClientCase;
const writers = [
  { name: 'legacy', path: '/cases/19/checked', write: (source: ClientCase) => apiService.updateCase(19, { ...source, title: 'Моя правка' }, source) },
  { name: 'V2', path: '/v2/cases/19/checked', write: (source: ClientCase) => apiService.updateCaseV2(19, { ...source, title: 'Моя правка' }, source) },
];

describe('case delete write', () => {
  it('accepts one successful no-content deletion', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(null, { status: 204 })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.deleteCase(19)).resolves.toBeUndefined();
    expect(fetch).toHaveBeenCalledOnce(); expect(String(fetch.mock.calls[0][0]).endsWith('/cases/19')).toBe(true);
    expect(fetch.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it.each([400, 403, 404, 409, 503])('HTTP %s keeps the failure and never falls back or replays deletion', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ message: 'Дело и связи сохранены. Обновите карточку.' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(apiService.deleteCase(19)).rejects.toMatchObject({ status });
    expect(fetch).toHaveBeenCalledOnce(); expect(String(fetch.mock.calls[0][0]).endsWith('/cases/19')).toBe(true);
    expect(fetch.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it('does not replay deletion after a lost response', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError('Failed to fetch')); vi.stubGlobal('fetch', fetch);
    await expect(apiService.deleteCase(19)).rejects.toThrow(); expect(fetch).toHaveBeenCalledOnce();
  });
});

describe('checked case write source', () => {
  it.each(writers)('$name accepts a saved case with only owner and responsible captions', async ({ write, path }) => {
    const saved = { id: 19, clientId: 3, title: 'Моя правка', status: 'Open', responsibleUserId: 7,
      strategy: '  Прежняя позиция  ', eventHistory: 'Прежняя история',
      client: { id: 3, name: 'Клиент', responsibleUserId: 7 },
      responsibleUser: { id: 7, fullName: 'Ответственный', email: 'member@example.local' } };
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(saved)); vi.stubGlobal('fetch', fetch);
    const source = original(); const result = await write(source);
    expect(result).toMatchObject(saved); expect(fetch).toHaveBeenCalledOnce();
    expect(String(fetch.mock.calls[0][0]).endsWith(path)).toBe(true);
    expect(result.client).not.toHaveProperty('notes'); expect(result.responsibleUser).not.toHaveProperty('passwordHash');
    expect(source).toMatchObject({ title: 'Исходное дело', payments: [{ id: 6 }] });
  });

  it.each(writers)('$name sends original scalars separately from requested edits without a new read', async ({ path, write }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ id: 19 })); vi.stubGlobal('fetch', fetch);
    const source = original(); await write(source);
    expect(fetch).toHaveBeenCalledOnce(); const [url, options] = fetch.mock.calls[0];
    expect(String(url).endsWith(path)).toBe(true); expect(options?.method).toBe('PUT');
    const body = JSON.parse(String(options?.body)); expect(body.title).toBe('Моя правка');
    expect(body.expectedSource).toMatchObject({ id: 19, title: 'Исходное дело', strategy: '  Прежняя позиция  ', eventHistory: 'Прежняя история', nextHearingAt: '2099-11-03T12:00:00.1234567' });
    expect(body.expectedSource).not.toHaveProperty('client'); expect(body.expectedSource).not.toHaveProperty('payments');
    expect(source.title).toBe('Исходное дело');
  });

  it.each(writers.flatMap(row => [400, 403, 404, 409, 503].map(status => ({ ...row, status }))))('$name HTTP $status cannot fall back or replay a write', async ({ path, write, status }) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ message: 'Обновите данные' }, { status })); vi.stubGlobal('fetch', fetch);
    await expect(write(original())).rejects.toMatchObject({ status });
    expect(fetch).toHaveBeenCalledOnce(); expect(String(fetch.mock.calls[0][0]).endsWith(path)).toBe(true);
  });

  it.each(writers)('$name retains the original source through a same-session refresh', async ({ write, path }) => {
    vi.spyOn(authService, 'ensureFreshSession').mockImplementation(async force => { if (force) session.refresh(); return localStorage.getItem('pp_token'); });
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json({}, { status: 401 })).mockResolvedValueOnce(Response.json({ id: 19 })); vi.stubGlobal('fetch', fetch);
    await write(original()); expect(fetch).toHaveBeenCalledTimes(2);
    const bodies = fetch.mock.calls.map(([url, options]) => { expect(String(url).endsWith(path)).toBe(true); return JSON.parse(String(options?.body)); });
    expect(bodies[0]).toEqual(bodies[1]); expect(bodies[1].expectedSource.eventHistory).toBe('Прежняя история');
  });

  it.each(writers)('$name refuses a source belonging to another case before sending', async ({ write }) => {
    const fetch = vi.fn<typeof globalThis.fetch>(); vi.stubGlobal('fetch', fetch);
    await expect(write({ ...original(), id: 20 })).rejects.toThrow('Обновите карточку'); expect(fetch).not.toHaveBeenCalled();
  });
});
