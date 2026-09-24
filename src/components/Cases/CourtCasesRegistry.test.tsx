import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { CaseCalendarEvent, ClientCase } from '../../types';
import { CourtCasesRegistry } from './CourtCasesRegistry';

vi.mock('../../services/api', () => ({
  apiService: {
    getCasesV2: vi.fn(),
    getCourtCasesRegistrySummary: vi.fn(),
    getCourtReportOverview: vi.fn(),
    getActResponsibles: vi.fn(),
    getCourtSyncProviders: vi.fn(),
    getCourtSyncHealth: vi.fn(),
    getCaseCalendarEvents: vi.fn(),
  },
}));

vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

const courtCase: ClientCase = {
  id: 77,
  clientId: 12,
  client: { id: 12, name: 'Иванов Иван Иванович' } as ClientCase['client'],
  caseNumber: 'CRM-77',
  caseType: 'Гражданское дело',
  stage: 'Первая инстанция',
  responsibleUserId: 5,
  responsibleUserName: 'Петров Пётр Петрович',
  priority: 'high',
  courtName: 'Новокуйбышевский городской суд',
  courtCaseNumber: '2-1502/2026',
  judicialUid: 'UID-COURT-77',
  courtUrl: 'https://example.test/court/77',
  nextHearingAt: '2026-09-25T03:00:00Z',
  claimant: 'Иванов И.И.',
  defendant: 'Банк',
  judgeName: 'Судья С.С.',
  courtroom: '12',
  openTaskCount: 2,
  overdueTaskCount: 1,
  isUnderControl: true,
  title: 'ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА',
  status: 'Open',
  createdAt: '2026-09-01T00:00:00Z',
};

describe('CourtCasesRegistry', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCasesV2).mockReset();
    vi.mocked(apiService.getCourtCasesRegistrySummary).mockReset();
    vi.mocked(apiService.getCourtReportOverview).mockReset();
    vi.mocked(apiService.getActResponsibles).mockReset();
    vi.mocked(apiService.getCourtSyncProviders).mockReset();
    vi.mocked(apiService.getCourtSyncHealth).mockReset();
    vi.mocked(apiService.getCaseCalendarEvents).mockReset();
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
    vi.mocked(apiService.getCourtSyncProviders).mockResolvedValue([]);
    vi.mocked(apiService.getCourtSyncHealth).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50 });
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([]);
    vi.mocked(apiService.getCasesV2).mockResolvedValue({ items: [courtCase], total: 1, page: 1, pageSize: 12 });
    vi.mocked(apiService.getCourtCasesRegistrySummary).mockResolvedValue({
      total: 1,
      active: 1,
      upcoming: 1,
      soon: 1,
      withoutHearing: 0,
      underControl: 1,
    });
    const amounts = { claimTotal: 100000.5, claimCases: 1, awardedTotal: 75000, awardedCases: 1, stateFeeTotal: 0, stateFeeCases: 1 };
    vi.mocked(apiService.getCourtReportOverview).mockResolvedValue({ total: 2, active: 1, completed: 1, amounts, byResponsible: [{ name: 'Петров Пётр Петрович', total: 2, active: 1, completed: 1, decisionDurationSampleCount: 1, averageDaysFromClaimToDecision: 12, amounts }], byCourt: [{ name: 'Новокуйбышевский городской суд', total: 2, active: 1, completed: 1, decisionDurationSampleCount: 1, averageDaysFromClaimToDecision: 12, amounts }], otherResponsibleCases: 0, otherCourtCases: 0 });
  });

  it('loads the court report only when opened and shows CRM completion separately from court outcome', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    expect(apiService.getCourtReportOverview).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Сводка по делам' }));
    expect(await screen.findByRole('dialog', { name: 'Сводка судебных дел' })).toBeTruthy();
    expect(await screen.findByText(/не выигрыш в суде/u)).toBeTruthy();
    expect(screen.getAllByText('12 дн. · выборка: 1')).toHaveLength(2);
    expect(screen.getAllByText('Дел с заполненным полем: 1')).toHaveLength(3);
    expect(screen.getAllByText(/100\s?000,50\s?₽/u).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/0\s?₽/u).length).toBeGreaterThan(0);
    expect(apiService.getCourtReportOverview).toHaveBeenCalledWith(
      expect.objectContaining({ courtState: 'all' }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    fireEvent.change(screen.getByLabelText('Дела созданы с'), { target: { value: '2026-09-02' } });
    fireEvent.change(screen.getByLabelText('Дела созданы по'), { target: { value: '2026-09-02' } });
    await waitFor(() => expect(apiService.getCourtReportOverview).toHaveBeenLastCalledWith(
      expect.objectContaining({ from: '2026-09-01T17:00:00.000Z', toExclusive: '2026-09-02T17:00:00.000Z' }),
      expect.any(Object),
    ));
  });

  it('shows a complete court card and opens the court tab', async () => {
    const onOpenCaseCourt = vi.fn();
    render(<CourtCasesRegistry onOpenCaseCourt={onOpenCaseCourt} />);

    expect(await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА')).toBeTruthy();
    expect(screen.getByText('Новокуйбышевский городской суд')).toBeTruthy();
    expect(screen.getByText('Суд № 2-1502/2026')).toBeTruthy();
    expect(screen.getByText('UID UID-COURT-77')).toBeTruthy();
    expect(screen.getByText(/25\.09\.2026.*10:00/)).toBeTruthy();
    expect(screen.getByText('Задачи 2 / !1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Открыть дело/i }));
    expect(onOpenCaseCourt).toHaveBeenCalledWith(77);
    expect(apiService.getCasesV2).toHaveBeenCalledWith(
      expect.objectContaining({ courtOnly: true, scope: 'active', sortBy: 'nextHearingAt' }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('shows all visible hearings for the selected period instead of only the registry page', async () => {
    const onOpenCaseCourt = vi.fn();
    const hearingAt = new Date().toISOString();
    const baseEvent: CaseCalendarEvent = {
      id: 'court-event:1', kind: 'court', sourceType: 'hearing', title: 'Судебное заседание',
      startAt: hearingAt, endAt: hearingAt, allDay: false, clientCaseId: 77,
      caseTitle: 'Первое дело', responsibleName: 'Юрист', status: 'scheduled', priority: 'normal',
      isOverdue: false, isWarning: true, documentsPrepared: true,
    };
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([
      baseEvent,
      { ...baseEvent, id: 'court-event:2', clientCaseId: 99, caseTitle: 'Дело с другой страницы', documentsPrepared: false },
      { ...baseEvent, id: 'case-deadline:1', kind: 'case-deadline', sourceType: 'due', caseTitle: 'Не заседание' },
    ]);
    render(<CourtCasesRegistry onOpenCaseCourt={onOpenCaseCourt} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    fireEvent.click(screen.getByRole('button', { name: 'Календарь заседаний' }));

    expect(await screen.findByRole('region', { name: 'Календарь судебных заседаний' })).toBeTruthy();
    expect(screen.getByText('Подготовлено: 1')).toBeTruthy();
    expect(screen.getByText('Внимание: 1')).toBeTruthy();
    expect(screen.getAllByText(/Дело с другой страницы/u).length).toBeGreaterThan(0);
    expect(screen.queryByText('Не заседание')).toBeNull();
    expect(apiService.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({ includeTasks: false }));

    fireEvent.click(screen.getByRole('button', { name: 'Месяц' }));
    await waitFor(() => expect(apiService.getCaseCalendarEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ from: expect.stringMatching(/^\d{4}-\d{2}-01$/), includeTasks: false }),
    ));
    fireEvent.click(screen.getByRole('button', { name: 'К карточкам дел' }));
    expect(screen.queryByRole('region', { name: 'Календарь судебных заседаний' })).toBeNull();
  });

  it('filters the registry and report by our role in the case', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    fireEvent.click(screen.getByRole('combobox', { name: /Наша роль в деле/u }));
    fireEvent.click(screen.getByRole('option', { name: 'Ответчик' }));
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
      expect.objectContaining({ ourCourtRole: 'respondent' }), expect.any(Object),
    ));
    expect(apiService.getCourtCasesRegistrySummary).toHaveBeenLastCalledWith(
      expect.objectContaining({ ourCourtRole: 'respondent' }), expect.any(Object),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Сводка по делам' }));
    await waitFor(() => expect(apiService.getCourtReportOverview).toHaveBeenCalledWith(
      expect.objectContaining({ ourCourtRole: 'respondent' }), expect.any(Object),
    ));
  });

  it('passes the selected court registry filters to the full-period hearing calendar', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    fireEvent.change(screen.getByPlaceholderText('Название, клиент, суд, номер или UID'), { target: { value: 'Иванов' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма иска от' }), { target: { value: '1000' } });
    fireEvent.click(screen.getByRole('combobox', { name: /Наша роль в деле/u }));
    fireEvent.click(screen.getByRole('option', { name: 'Истец' }));
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Иванов', ourCourtRole: 'claimant', minClaimAmount: 1000 }), expect.any(Object),
    ));

    fireEvent.click(screen.getByRole('button', { name: 'Календарь заседаний' }));
    await waitFor(() => expect(apiService.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      search: 'Иванов', scope: 'active', courtState: 'all', ourCourtRole: 'claimant', minClaimAmount: 1000,
      includeTasks: false,
    })));
  });

  it('filters cards, counters and report by claim amount without confusing it with the fee', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');

    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма иска от' }), { target: { value: '100000,50' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма иска до' }), { target: { value: '200000' } });
    await waitFor(() => expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
      expect.objectContaining({ minClaimAmount: 100000.5, maxClaimAmount: 200000 }), expect.any(Object),
    ));
    expect(apiService.getCourtCasesRegistrySummary).toHaveBeenLastCalledWith(
      expect.objectContaining({ minClaimAmount: 100000.5, maxClaimAmount: 200000 }), expect.any(Object),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Сводка по делам' }));
    await waitFor(() => expect(apiService.getCourtReportOverview).toHaveBeenCalledWith(
      expect.objectContaining({ minClaimAmount: 100000.5, maxClaimAmount: 200000 }), expect.any(Object),
    ));
  });

  it('does not request an inverted claim amount range', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    vi.mocked(apiService.getCasesV2).mockClear();

    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма иска от' }), { target: { value: '300' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма иска до' }), { target: { value: '100' } });
    await screen.findByText('Минимальная сумма иска не может быть больше максимальной.');
    expect(apiService.getCasesV2).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Сводка по делам' }).hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Календарь заседаний' }));
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Минимальная сумма иска не может быть больше максимальной.');
    expect(apiService.getCaseCalendarEvents).not.toHaveBeenCalled();
  });

  it('shows a recorded hearing result instead of asking to check a past hearing again', async () => {
    vi.mocked(apiService.getCasesV2).mockResolvedValue({
      items: [{ ...courtCase, nextHearingAt: '2020-09-25T03:00:00Z', nextHearingOutcome: 'postponed' }],
      total: 1,
      page: 1,
      pageSize: 18,
    });
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);

    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');
    expect(await screen.findByText(/Итог: заседание отложено/u)).toBeTruthy();
    expect(screen.queryByText('Дата прошла — проверьте результат')).toBeNull();
  });

  it('opens the sync health window on entry when errors older than a day exist', async () => {
    vi.mocked(apiService.getCourtSyncHealth).mockResolvedValue({ items: [], total: 1, page: 1, pageSize: 50 });
    render(<CourtCasesRegistry allowAssign onOpenCaseCourt={vi.fn()} />);
    expect(await screen.findByRole('dialog', { name: 'Состояние судебной синхронизации' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Синхронизация · 1/ })).toBeTruthy();
  });

  it('filters cards by court state from the summary', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');

    fireEvent.click(screen.getByRole('combobox', { name: /Состояние заседания/i }));
    fireEvent.click(screen.getByRole('option', { name: 'Ближайшие 14 дней' }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ courtOnly: true, courtState: 'soon' }),
        expect.any(Object),
      );
    });
  });

  it('filters the registry by court instance and exposes quick tracking', async () => {
    render(
      <CourtCasesRegistry
        onOpenCaseCourt={vi.fn()}
        allowCreate
        ownerScopes={['client']}
      />,
    );
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');

    expect(screen.getByRole('button', { name: /Поставить дело на отслеживание/i })).toBeTruthy();
    fireEvent.click(screen.getByRole('combobox', { name: /Инстанция/i }));
    fireEvent.click(screen.getByRole('option', { name: 'Апелляция' }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ courtOnly: true, courtInstance: 'appeal' }),
        expect.any(Object),
      );
    });
  });
});
