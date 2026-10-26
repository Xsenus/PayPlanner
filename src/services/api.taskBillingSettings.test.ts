import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiService, type UpdateTaskJournalSettingsRequest } from './api';
import { cloneDefaultTaskStickerStyles } from '../utils/taskStickerStyles';

describe('task contract billing settings and decision transport', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it.each(['retainContractTasks', 'allowFutureClassification'] as const)('loads and coalesces the canonical %s mode', async mode => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ contractTaskBillingMode: mode, restrictExecutorEditing: true }));
    vi.stubGlobal('fetch', request);

    const [first, second] = await Promise.all([apiService.getTaskJournalSettings(), apiService.getTaskJournalSettings()]);

    expect(request).toHaveBeenCalledTimes(1);
    expect(String(request.mock.calls[0][0]).endsWith('/tasks/settings')).toBe(true);
    expect(first).toMatchObject({ contractTaskBillingMode: mode, restrictExecutorEditing: true });
    expect(second).toEqual(first);
  });

  it.each([undefined, null, '', 'unknown', 'ALLOWFUTURECLASSIFICATION', 'allowFutureClassification '])('retains contractual tasks for a missing or unknown GET value %s', async mode => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ contractTaskBillingMode: mode }));
    vi.stubGlobal('fetch', request);

    expect((await apiService.getTaskJournalSettings()).contractTaskBillingMode).toBe('retainContractTasks');
    expect(request).toHaveBeenCalledTimes(1);
  });

  it('sends the chosen mode in a settings update and normalises an unknown response conservatively', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ contractTaskBillingMode: 'future-policy' }));
    vi.stubGlobal('fetch', request);
    const payload = { contractTaskBillingMode: 'allowFutureClassification' } as const;

    const settings: UpdateTaskJournalSettingsRequest = {
      restrictExecutorEditing: true,
      allowExecutorDeadlineChangeRequests: true,
      executorCompletionAllowedUserIds: [],
      requireCompletionResult: true,
      dailyOverdueReminderEnabled: false,
      birthdayTaskAutomationEnabled: false,
      employeeBirthdayTaskAutomationEnabled: false,
      birthdayTaskLeadDays: 7,
      birthdayClientUseSelectedAssignee: false,
      allowAdminCompleteAnyTask: false,
      approvalDueDays: 5,
      taskCardWindowMode: 'legacy',
      approvalQuickActionMode: 'completionButton',
      caseInactivityAutomationEnabled: false,
      caseInactivityDays: 14,
      caseInactivityLimit: 25,
      caseInactivityEscalationDays: 30,
      stickerStyles: cloneDefaultTaskStickerStyles(),
    };
    const response = await apiService.updateTaskJournalSettings({ ...settings, ...payload });

    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0];
    expect(String(url).endsWith('/tasks/settings')).toBe(true);
    expect(options?.method).toBe('PUT');
    expect(JSON.parse(String(options?.body))).toMatchObject(payload);
    expect(response.contractTaskBillingMode).toBe('retainContractTasks');
  });

  it.each(['2026-10-24T01:02:03.1234567Z', null])('sends the exact explicit task version %s with its concurrency marker', async expectedUpdatedAt => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ id: 91 }));
    vi.stubGlobal('fetch', request);

    await apiService.updateTaskBillingClassification(91, 'internal', 43, { expectedUpdatedAt });

    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0];
    expect(String(url).endsWith('/tasks/91/billing-classification')).toBe(true);
    expect(options?.method).toBe('PUT');
    expect(JSON.parse(String(options?.body))).toEqual({ classification: 'internal', billingContractId: 43, expectedUpdatedAt, hasExpectedUpdatedAt: true });
  });

  it('does not fabricate concurrency fields for existing callers that omit the version options', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ id: 91 }));
    vi.stubGlobal('fetch', request);

    await apiService.updateTaskBillingClassification(91, 'billable', null);

    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual({ classification: 'billable', billingContractId: null });
  });

  it('propagates a task version conflict without retrying or treating it as a successful decision', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ message: 'Версия задачи изменилась.' }, { status: 409 }));
    vi.stubGlobal('fetch', request);

    await expect(apiService.updateTaskBillingClassification(91, 'internal', 43, { expectedUpdatedAt: '2026-10-24T01:02:03.1234567Z' })).rejects.toThrow();
    expect(request).toHaveBeenCalledTimes(1);
  });
  it.each([true, false, undefined])('sends the contract flag only for an explicit true option (%s)', async allowContractClassification => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ id: 91 }));
    vi.stubGlobal('fetch', request);
    const expectedUpdatedAt = '2026-10-24T01:02:03.1234567Z';

    await apiService.updateTaskBillingClassification(91, 'internal', 43, { expectedUpdatedAt, allowContractClassification });

    expect(JSON.parse(String(request.mock.calls[0][1]?.body))).toEqual({ classification: 'internal', billingContractId: 43,
      expectedUpdatedAt, hasExpectedUpdatedAt: true, ...(allowContractClassification === true ? { allowContractClassification: true } : {}) });
    expect(request).toHaveBeenCalledTimes(1);
  });

});
