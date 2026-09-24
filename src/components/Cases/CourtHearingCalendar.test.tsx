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
  courtName: 'Районный суд', courtCaseNumber: '2-123/2026', documentsPrepared: true, materialsReviewed: true,
  sourceCheckedAt: '2026-09-23T17:00:00Z',
  previousScheduledAt: '2026-09-20T03:00:00Z', previousAllDay: false,
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
    expect(agenda.textContent).toContain('Задача закрыта · материалы проверены');
    expect(agenda.textContent).toContain('Источник проверен 24 сент. 2026 г.');
    expect(agenda.textContent).toContain('Перенесено: 20 сент. 2026 г., 10:00 → 24 сент. 2026 г., 10:00');
    expect(screen.getAllByText(/Перенесено: 20 сент/)).toHaveLength(2);
    expect(agenda.querySelectorAll('button')).toHaveLength(1);
    expect(apiService.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({ responsibleUserId: 7, includeTasks: false }));
    fireEvent.click(agenda.querySelector('button')!);
    expect(onOpenCase).toHaveBeenCalledWith(42);
  });

  it('does not claim a source check for a manually entered hearing', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([{ ...hearing, sourceCheckedAt: null, previousScheduledAt: null }]);
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} onOpenCase={vi.fn()} />);
    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).not.toContain('Источник проверен');
    expect(agenda.textContent).not.toContain('Перенесено:');
  });

  it('keeps the hearing in attention when the task is done but materials are not checked', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([{ ...hearing, materialsReviewed: false }]);
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} onOpenCase={vi.fn()} />);
    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).toContain('Задача закрыта · материалы не проверены');
    expect(screen.getByText('Задача и материалы: 0')).toBeTruthy();
    expect(screen.getByText('Внимание: 1')).toBeTruthy();
  });

  it('keeps the hearing in attention when materials are checked but the task is open', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([{ ...hearing, documentsPrepared: false }]);
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} onOpenCase={vi.fn()} />);
    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).toContain('Материалы проверены · задача открыта');
    expect(screen.getByText('Задача и материалы: 0')).toBeTruthy();
    expect(screen.getByText('Внимание: 1')).toBeTruthy();
  });

  it('shows an empty month without a wide empty mobile grid', async () => {
    vi.mocked(apiService.getCaseCalendarEvents).mockResolvedValue([]);
    render(<CourtHearingCalendar timeZoneId="Asia/Novosibirsk" reloadToken={0} onOpenCase={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Месяц' }));
    const agenda = await screen.findByLabelText('Расписание заседаний по датам');
    expect(agenda.textContent).toContain('В этом месяце заседаний нет.');
  });
});
