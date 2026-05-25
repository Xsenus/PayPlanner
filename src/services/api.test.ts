import { afterEach, describe, expect, it, vi } from 'vitest';

import { AUTH_SESSION_EXPIRED_EVENT, apiService, extractResponseErrorMessage } from './api';

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
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

describe('apiService auth handling', () => {
  it('loads feature flags from the backend', async () => {
    const payload = {
      legal: {
        externalCourtSync: false,
        externalCalendarSync: false,
        courtEvents: true,
        documentTemplateLibrary: false,
      },
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getFeatureFlags();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/features');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getLatestAppVersion();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/app-version/latest');
  });

  it('marks app version release as seen', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(response), {
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
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getRolePermissionsAudit();

    expect(result).toEqual(payload);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/role-permissions/audit');
  });

  it('downloads role permission audit csv from the backend', async () => {
    const fetchMock = vi.fn(async () => new Response('RoleId;RoleName', {
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify([]), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    await apiService.getTaskJournalNotifications();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [, init] = fetchMock.mock.calls[0];
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer main-token');
  });

  it('loads the notification inbox with filters', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({
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
    expect(String(url)).toContain('page=2');
    expect(String(url)).toContain('pageSize=10');
  });

  it('marks all filtered inbox notifications as read', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({
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
    expect(init?.method).toBe('POST');
  });

  it('emits a session-expired event when the API returns 401', async () => {
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn(async () => new Response('', { status: 401, statusText: 'Unauthorized' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getTaskJournalNotifications()).rejects.toThrow('Сессия истекла. Войдите снова.');

    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
  });
});

describe('apiService case calendar', () => {
  it('downloads case calendar ICS with filters', async () => {
    const fetchMock = vi.fn(async () => new Response('case-calendar', {
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
    });

    expect(result.fileName).toBe('case-calendar.ics');
    expect(await result.blob.text()).toBe('case-calendar');
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/case-calendar/events.ics?');
    expect(String(url)).toContain('from=2026-06-01');
    expect(String(url)).toContain('to=2026-06-30');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('includeTasks=false');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashFlowReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      accountName: 'Основной банк',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-flow?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashBook({
      from: '2026-05-01',
      to: '2026-05-31',
      accountName: 'Основная касса',
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getMoneyCategoryReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/categories?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCounterpartyReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/counterparties?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getProjectReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/projects?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('downloads legal work report exports with filters', async () => {
    const fetchMock = vi.fn(async () => new Response('excel-data', {
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getOverdueProceduralDeadlinesReport({
      asOf: '2026-05-26',
      deadlineType: 'appeal',
      responsibleUserId: 7,
      limit: 25,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/overdue-deadlines?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('deadlineType=appeal');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('limit=25');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCurrentCasesReport({
      asOf: '2026-05-26',
      status: 'Open',
      responsibleUserId: 7,
      caseType: 'bankruptcy',
      limit: 50,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/current-cases?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=50');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getLawyerWorkloadReport({
      asOf: '2026-05-26',
      horizonDays: 14,
      lawyerUserId: 7,
      limit: 25,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/lawyer-workload?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('horizonDays=14');
    expect(String(url)).toContain('lawyerUserId=7');
    expect(String(url)).toContain('limit=25');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getDirectionReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/directions?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getSalesPointReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/sales-points?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getVatReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/vat?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPlanFactReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/plan-fact?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getAdvanceReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/advances?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentPriorityReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-priorities?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaidAccountReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-accounts?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaidInvoiceReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-invoices?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getInvoiceStatusReport({
      from: '2026-05-01',
      to: '2026-05-31',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/invoice-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getActStatusReport({
      from: '2026-05-01',
      to: '2026-05-31',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/act-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentOrderReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-orders?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentPurposeReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-purposes?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getMatchStatusReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/match-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getJournalStatusReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/journal-statuses?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCurrencyReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/currencies?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashOrderReport({
      from: '2026-05-01',
      to: '2026-05-31',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-orders?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getBankStatementImportReport({
      from: '2026-05-01',
      to: '2026-05-31',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/bank-statement-imports?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
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
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getSpreadsheetImportReport({
      from: '2026-05-01',
      to: '2026-05-31',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/spreadsheet-imports?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
  });
});
