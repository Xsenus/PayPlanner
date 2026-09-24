import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { CaseCalendarEvent } from '../../types';
import { CourtHearingCalendar } from './CourtHearingCalendar';

vi.mock('../../services/api', () => ({ apiService: { getCaseCalendarEvents: vi.fn() } }));

const hearing: CaseCalendarEvent = {
  id: 'hearing-1', kind: 'court', sourceType: 'hearing', title: 'Заседание',
  startAt: '2026-09-24T03:00:00Z', endAt: '2026-09-24T04:00:00Z', allDay: false,
  clientCaseId: 42, caseTitle: 'Дело о разделе', responsibleName: 'Юрист',
  status: 'scheduled', priority: 'normal', isOverdue: false, isWarning: false,
  courtName: 'Районный суд', courtCaseNumber: '2-123/2026', documentsPrepared: true,
};

describe('CourtHearingCalendar', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-24T04:00:00Z'));
    vi.mocked(apiService.getCaseCalendarEvents).mockReset();
  });

  afterEach(() => vi.useRealTimers());

  it('shows date-grouped mobile hearing cards and opens the case', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([hearing]);
    const onOpenCase = vi.fn();
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} responsibleUserId={7} onOpenCase={onOpenCase} />);

    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).toContain('24 сентября');
    expect(agenda.textContent).toContain('10:00');
    expect(agenda.textContent).toContain('Дело о разделе');
    expect(agenda.textContent).toContain('Документы подготовлены');
    expect(agenda.querySelectorAll('button')).toHaveLength(1);
    expect(apiService.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({ responsibleUserId: 7, includeTasks: false }));
    fireEvent.click(agenda.querySelector('button')!);
    expect(onOpenCase).toHaveBeenCalledWith(42);
  });

  it('shows an empty month without a wide empty mobile grid', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([]);
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} onOpenCase={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Месяц' }));
    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).toContain('В этом месяце заседаний нет.');
  });
});
