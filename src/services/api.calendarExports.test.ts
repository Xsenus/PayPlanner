import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { vi.unstubAllGlobals(); session.restore(); });

const filters = {
  search: 'Работа клиента', status: 'active', type: 'call', priority: 'high', underControl: false,
  assigneeUserId: 8, assigneeUserIds: [8, 9], authorUserId: 7, participantRole: 'observer' as const,
  approvalScope: true, metric: 'today' as const, billingStatus: 'unbilled' as const, untriaged: true,
  from: '2026-03-08T05:00:00.000Z', to: '2026-03-09T03:59:59.999Z',
  todayFrom: '2026-03-08T05:00:00.000Z', todayTo: '2026-03-09T04:00:00.000Z',
  clientCaseId: 100, clientId: 10, legalEntityId: 20, calendarView: true,
};

function expectSharedFilters(query: URLSearchParams) {
  for (const [key, value] of Object.entries(filters)) {
    expect(query.getAll(key)).toEqual(Array.isArray(value) ? value.map(String) : [String(value)]);
  }
}

describe('visible calendar export transport', () => {
  it('sends all task filters, inclusive UTC bounds, calendar visibility and repeated reminders to task ICS', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response('calendar'));
    vi.stubGlobal('fetch', fetch);
    await apiService.downloadTaskJournalCalendar({ ...filters, reminderMinutes: [15, 60] });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid');
    expect(url.pathname).toBe('/api/tasks/calendar.ics');
    expectSharedFilters(url.searchParams);
    expect(url.searchParams.getAll('reminderMinutes')).toEqual(['15', '60']);
    expect(url.searchParams.has('timeZoneId')).toBe(false);
  });

  it.each(['html', 'word', 'pdf'] as const)('sends the same task filters and selected timezone to planning %s', async format => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response('report'));
    vi.stubGlobal('fetch', fetch);
    await apiService.downloadTaskPlanningReport({ ...filters, format, timeZoneId: 'America/New_York' });
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid');
    expect(url.pathname).toBe(`/api/tasks/planning-report.${format}`);
    expectSharedFilters(url.searchParams);
    expect(url.searchParams.get('timeZoneId')).toBe('America/New_York');
    expect(url.searchParams.has('format')).toBe(false);
  });

  it('keeps ordinary ICS calls free of optional calendar filters', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response('calendar'));
    vi.stubGlobal('fetch', fetch);
    await apiService.downloadTaskJournalCalendar({ clientId: 10 });
    const query = new URL(String(fetch.mock.calls[0][0]), 'http://local.invalid').searchParams;
    expect([...query.entries()]).toEqual([['clientId', '10']]);
  });
});
