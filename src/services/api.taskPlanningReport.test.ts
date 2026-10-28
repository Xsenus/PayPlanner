import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';

afterEach(() => vi.unstubAllGlobals());

it('serializes every planning filter, including false and conflicting identifiers', async () => {
  const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response('synthetic', { status: 200 }));
  vi.stubGlobal('fetch', fetchMock);
  await apiService.downloadTaskPlanningReport({
    format: 'pdf', search: 'иск & проверка', status: 'all', type: 'task', priority: 'high',
    underControl: false, assigneeUserId: -1, authorUserId: 7, participantRole: 'observer',
    approvalScope: true, metric: 'today', billingStatus: 'internal', untriaged: true,
    clientCaseId: 3, clientId: 4, legalEntityId: 5,
    from: '2026-06-07T18:00:00Z', to: '2026-06-08T18:00:00Z',
    todayFrom: '2026-06-07T15:00:00Z', todayTo: '2026-06-08T15:00:00Z',
  });
  const url = new URL(String(fetchMock.mock.calls[0][0]), 'https://example.test');
  expect(url.pathname).toMatch(/\/tasks\/planning-report\.pdf$/);
  expect(Object.fromEntries(url.searchParams)).toEqual({
    search: 'иск & проверка', status: 'all', type: 'task', priority: 'high',
    underControl: 'false', assigneeUserId: '-1', authorUserId: '7', participantRole: 'observer',
    approvalScope: 'true', metric: 'today', billingStatus: 'internal', untriaged: 'true',
    clientCaseId: '3', clientId: '4', legalEntityId: '5',
    from: '2026-06-07T18:00:00Z', to: '2026-06-08T18:00:00Z',
    todayFrom: '2026-06-07T15:00:00Z', todayTo: '2026-06-08T15:00:00Z',
  });
});
