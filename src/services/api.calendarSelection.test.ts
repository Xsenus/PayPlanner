import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });

describe('calendar employee selection transport', () => {
  it.each(['events', 'summary', 'ics'] as const)('serializes repeated employee IDs for case %s with the legacy single filter', async mode => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => mode === 'ics' ? new Response('calendar') : Response.json(mode === 'events' ? [] : {}));
    vi.stubGlobal('fetch', fetch);
    const params = { responsibleUserId: 8, responsibleUserIds: [8, 9], timeZoneId: 'Asia/Kamchatka', includeTasks: false };
    if (mode === 'events') await apiService.getCaseCalendarEvents(params);
    else if (mode === 'summary') await apiService.getCaseCalendarSummary(params);
    else await apiService.downloadCaseCalendarIcs(params);
    const query = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid').searchParams;
    expect(query.getAll('responsibleUserIds')).toEqual(['8', '9']);
    expect(query.get('responsibleUserId')).toBe('8');
    expect(query.get('includeTasks')).toBe('false');
    expect(query.get('timeZoneId')).toBe('Asia/Kamchatka');
  });

  it.each(['html', 'word', 'pdf'] as const)('serializes the executor selection for planning %s', async format => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => new Response('report'));
    vi.stubGlobal('fetch', fetch);
    await apiService.downloadTaskPlanningReport({ format, assigneeUserIds: [8, 9], assigneeUserId: 8 });
    const query = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid').searchParams;
    expect(query.getAll('assigneeUserIds')).toEqual(['8', '9']);
    expect(query.get('assigneeUserId')).toBe('8');
  });

  it('serializes the same executor selection for task ICS', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => new Response('calendar'));
    vi.stubGlobal('fetch', fetch);
    await apiService.downloadTaskJournalCalendar({ assigneeUserIds: [8, 9], clientCaseId: 100 });
    const query = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid').searchParams;
    expect(query.getAll('assigneeUserIds')).toEqual(['8', '9']);
    expect(query.get('clientCaseId')).toBe('100');
  });
});
