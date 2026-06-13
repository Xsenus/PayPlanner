import { afterEach, describe, expect, it, vi } from 'vitest';

import { AUTH_SESSION_EXPIRED_EVENT, apiService, extractResponseErrorMessage } from './api';
import { resolveApiBaseUrl } from './apiBaseUrl';

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

describe('apiService auth handling', () => {
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

  it('sends selected inbox notifications to archive and delete endpoints', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      updatedCount: 2,
    }), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const archiveResult = await apiService.archiveUserNotifications([10, 11]);
    const deleteResult = await apiService.deleteUserNotifications([12, 13]);

    expect(archiveResult.updatedCount).toBe(2);
    expect(deleteResult.updatedCount).toBe(2);
    expect(fetchMock).toHaveBeenCalledTimes(2);

    const [archiveUrl, archiveInit] = fetchMock.mock.calls[0];
    expect(String(archiveUrl)).toContain('/notifications/inbox/bulk/archive');
    expect(archiveInit?.method).toBe('POST');
    expect(archiveInit?.body).toBe(JSON.stringify({ ids: [10, 11] }));

    const [deleteUrl, deleteInit] = fetchMock.mock.calls[1];
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
          preferences: { disabledTypes: ['taskComment'], disabledChannels: { taskAssigned: ['email'] } },
        }
        : {
          roles: [
            {
              roleId: 2,
              roleName: 'lawyer',
              isSystemRole: false,
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

  it('emits a session-expired event when the API returns 401', async () => {
    const listener = vi.fn();
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, listener);
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('', { status: 401, statusText: 'Unauthorized' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiService.getTaskJournalNotifications()).rejects.toThrow('Сессия истекла. Войдите снова.');

    expect(listener).toHaveBeenCalledTimes(1);
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

    const result = await apiService.getCashFlowForecastReport({
      from: '2026-05-01',
      to: '2026-05-31',
      accountName: 'Основной банк',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-flow-forecast?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
        'content-disposition': 'attachment; filename="vat-report.pdf"',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.downloadManagementReport({
      report: 'vat',
      format: 'pdf',
      from: '2026-05-01',
      to: '2026-05-31',
      responsibleUserId: 7,
      limit: 100,
    });

    expect(result.fileName).toBe('vat-report.pdf');
    expect(await result.blob.text()).toBe('pdf-data');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/management.pdf?');
    expect(String(url)).toContain('report=vat');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('limit=100');
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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

    const result = await apiService.getTaskReport({
      asOf: '2026-05-26',
      scope: 'closed',
      assigneeUserId: 7,
      limit: 50,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/tasks?');
    expect(String(url)).toContain('asOf=2026-05-26');
    expect(String(url)).toContain('scope=closed');
    expect(String(url)).toContain('assigneeUserId=7');
    expect(String(url)).toContain('limit=50');
  });

  it('loads case finance report with filters', async () => {
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

    const result = await apiService.getCaseFinanceReport({
      from: '2026-05-01',
      to: '2026-05-31',
      status: 'Open',
      responsibleUserId: 7,
      caseType: 'bankruptcy',
      limit: 25,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/case-finance?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Open');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=25');
  });

  it('loads case success report with filters', async () => {
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

    const result = await apiService.getCaseSuccessReport({
      from: '2026-05-01',
      to: '2026-05-31',
      status: 'Closed',
      responsibleUserId: 7,
      caseType: 'bankruptcy',
      limit: 25,
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/case-success?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('status=Closed');
    expect(String(url)).toContain('responsibleUserId=7');
    expect(String(url)).toContain('caseType=bankruptcy');
    expect(String(url)).toContain('limit=25');
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(payload), {
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
