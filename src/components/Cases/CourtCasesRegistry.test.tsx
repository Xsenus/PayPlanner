import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { CourtCasesRegistry } from './CourtCasesRegistry';

vi.mock('../../services/api', () => ({
  apiService: {
    getCasesV2: vi.fn(),
    getCourtCasesRegistrySummary: vi.fn(),
    getActResponsibles: vi.fn(),
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
    vi.mocked(apiService.getActResponsibles).mockReset();
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
    vi.mocked(apiService.getCasesV2).mockResolvedValue({ items: [courtCase], total: 1, page: 1, pageSize: 12 });
    vi.mocked(apiService.getCourtCasesRegistrySummary).mockResolvedValue({
      total: 1,
      active: 1,
      upcoming: 1,
      soon: 1,
      withoutHearing: 0,
      underControl: 1,
    });
  });

  it('shows a complete court card and opens the court tab', async () => {
    const onOpenCaseCourt = vi.fn();
    render(<CourtCasesRegistry onOpenCaseCourt={onOpenCaseCourt} />);

    expect(await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА')).toBeTruthy();
    expect(screen.getByText('Новокуйбышевский городской суд')).toBeTruthy();
    expect(screen.getByText('Суд № 2-1502/2026')).toBeTruthy();
    expect(screen.getByText('UID UID-COURT-77')).toBeTruthy();
    expect(screen.getByText(/25\.09\.2026.*10:00/)).toBeTruthy();
    expect(screen.getByText('Задачи: 2 · просрочено 1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Открыть судебную карточку/i }));
    expect(onOpenCaseCourt).toHaveBeenCalledWith(77);
    expect(apiService.getCasesV2).toHaveBeenCalledWith(
      expect.objectContaining({ courtOnly: true, scope: 'active', sortBy: 'nextHearingAt' }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('filters cards by court state from the summary', async () => {
    render(<CourtCasesRegistry onOpenCaseCourt={vi.fn()} />);
    await screen.findByText('ИВАНОВ — РАЗДЕЛ ИМУЩЕСТВА');

    fireEvent.click(screen.getByRole('button', { name: /В ближайшие 14 дней/i }));

    await waitFor(() => {
      expect(apiService.getCasesV2).toHaveBeenLastCalledWith(
        expect.objectContaining({ courtOnly: true, courtState: 'soon' }),
        expect.any(Object),
      );
    });
  });
});
