import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TomorrowCourtHearingsDialog } from './TomorrowCourtHearingsDialog';

const apiMock = vi.hoisted(() => ({ getTomorrowCourtHearings: vi.fn() }));
vi.mock('../../services/api', () => ({ apiService: apiMock }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

describe('TomorrowCourtHearingsDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    apiMock.getTomorrowCourtHearings.mockResolvedValue([
      { caseId: 24, caseTitle: 'Дело клиента', hearingAt: '2026-09-24T03:30:00Z', allDay: false, courtName: 'Районный суд', judgeName: 'Петрова', courtroom: '12', documentsPrepared: false, materialsReviewed: false, positionReviewed: false },
    ]);
  });

  it('shows an accessible hearing and opens its case', async () => {
    const onOpenCase = vi.fn();
    render(<TomorrowCourtHearingsDialog userId={3} enabled onOpenCase={onOpenCase} onOpenCalendar={vi.fn()} />);

    expect(await screen.findByRole('dialog', { name: 'Заседания завтра' })).toBeTruthy();
    expect(screen.getByText('Дело клиента')).toBeTruthy();
    expect(screen.getByText(/Районный суд · Петрова · зал 12/)).toBeTruthy();
    expect(screen.getByText(/Контрольная задача.*не завершена/)).toBeTruthy();
    expect(screen.getByText(/Материалы не подтверждены/)).toBeTruthy();
    expect(screen.getByText(/Позиция по делу не проверена/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /К делу/ }));
    expect(onOpenCase).toHaveBeenCalledWith(24);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('shows the completed task and checked materials as separate signals', async () => {
    apiMock.getTomorrowCourtHearings.mockResolvedValueOnce([
      { caseId: 24, caseTitle: 'Дело клиента', hearingAt: '2026-09-24T03:30:00Z', allDay: false, courtName: 'Районный суд', judgeName: '', courtroom: '', documentsPrepared: true, materialsReviewed: true, positionReviewed: true },
    ]);
    render(<TomorrowCourtHearingsDialog userId={3} enabled onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    expect(await screen.findByText('Контрольная задача подготовки завершена')).toBeTruthy();
    expect(screen.getByText('Материалы к этому заседанию проверены')).toBeTruthy();
    expect(screen.getByText('Позиция по делу проверена')).toBeTruthy();
    expect(screen.queryByText(/Материалы не подтверждены/)).toBeNull();
  });

  it('does not treat a closed task as proof that materials were checked', async () => {
    apiMock.getTomorrowCourtHearings.mockResolvedValueOnce([
      { caseId: 24, caseTitle: 'Дело клиента', hearingAt: '2026-09-24T03:30:00Z', allDay: false, courtName: '', judgeName: '', courtroom: '', documentsPrepared: true, materialsReviewed: false, positionReviewed: false },
    ]);
    render(<TomorrowCourtHearingsDialog userId={3} enabled onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    expect(await screen.findByText('Контрольная задача подготовки завершена')).toBeTruthy();
    expect(screen.getByText('Материалы не подтверждены в чек-листе дела.')).toBeTruthy();
  });

  it('does not query without access or reopen after dismissal on the same date', async () => {
    const denied = render(<TomorrowCourtHearingsDialog userId={3} enabled={false} onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    expect(apiMock.getTomorrowCourtHearings).not.toHaveBeenCalled();
    denied.unmount();

    const first = render(<TomorrowCourtHearingsDialog userId={3} enabled onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Позже' }));
    first.unmount();

    render(<TomorrowCourtHearingsDialog userId={3} enabled onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.getTomorrowCourtHearings).toHaveBeenCalledTimes(1);
  });

  it('uses the court registry label when the task calendar is unavailable', async () => {
    const onOpenCalendar = vi.fn();
    render(<TomorrowCourtHearingsDialog userId={4} enabled calendarAvailable={false} onOpenCase={vi.fn()} onOpenCalendar={onOpenCalendar} />);
    fireEvent.click(await screen.findByRole('button', { name: 'К судебным делам' }));
    expect(onOpenCalendar).toHaveBeenCalledTimes(1);
  });

  it('releases the next startup reminder only after this one is handled or empty', async () => {
    const onDone = vi.fn();
    const first = render(<TomorrowCourtHearingsDialog userId={5} enabled onDone={onDone} onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    expect(await screen.findByRole('dialog', { name: 'Заседания завтра' })).toBeTruthy();
    expect(onDone).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Позже' }));
    expect(onDone).toHaveBeenCalledTimes(1);
    first.unmount();

    apiMock.getTomorrowCourtHearings.mockResolvedValueOnce([]);
    const noHearingsDone = vi.fn();
    render(<TomorrowCourtHearingsDialog userId={6} enabled onDone={noHearingsDone} onOpenCase={vi.fn()} onOpenCalendar={vi.fn()} />);
    await waitFor(() => expect(noHearingsDone).toHaveBeenCalledTimes(1));
  });
});
