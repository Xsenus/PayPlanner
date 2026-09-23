import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { CourtWorkloadDialog } from './CourtWorkloadDialog';

vi.mock('../../services/api', () => ({
  apiService: {
    getCourtWorkload: vi.fn(),
    assignCourtCase: vi.fn(),
  },
}));

describe('CourtWorkloadDialog', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCourtWorkload).mockReset();
    vi.mocked(apiService.assignCourtCase).mockReset();
    vi.mocked(apiService.getCourtWorkload).mockResolvedValue([
      { userId: 7, userName: 'Анна', activeCases: 3, hearingsNextSevenDays: 1, overdueCourtTasks: 0, canAssign: true },
      { userId: 8, userName: 'Олег', activeCases: 9, hearingsNextSevenDays: 2, overdueCourtTasks: 1, canAssign: false },
    ]);
    vi.mocked(apiService.assignCourtCase).mockResolvedValue({ caseId: 42, responsibleUserId: 7 });
  });

  it('shows workload and blocks assignment to a lawyer without case access', async () => {
    const onAssigned = vi.fn();
    render(<CourtWorkloadDialog open caseId={42} caseTitle="Дело" onClose={vi.fn()} onAssigned={onAssigned} />);

    expect(await screen.findAllByText('Анна')).toHaveLength(2);
    expect(screen.getAllByText('Олег')).toHaveLength(2);
    expect(screen.getAllByText('Нет доступа к делу')).toHaveLength(2);
    expect(screen.getAllByText('Высокая')).toHaveLength(2);
    expect(apiService.getCourtWorkload).toHaveBeenCalledWith(42, expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(screen.getAllByRole('button', { name: 'Назначить' })).toHaveLength(2);

    fireEvent.click(screen.getAllByRole('button', { name: 'Назначить' })[0]);
    await waitFor(() => expect(apiService.assignCourtCase).toHaveBeenCalledWith(42, 7));
    await waitFor(() => expect(onAssigned).toHaveBeenCalledOnce());
  });

  it('shows a read-only overview without a selected case', async () => {
    render(<CourtWorkloadDialog open onClose={vi.fn()} onAssigned={vi.fn()} />);
    expect(await screen.findAllByText('Анна')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: 'Назначить' })).toBeNull();
    expect(apiService.getCourtWorkload).toHaveBeenCalledWith(undefined, expect.any(Object));
  });

  it('keeps the dialog open when assignment fails', async () => {
    vi.mocked(apiService.assignCourtCase).mockRejectedValue(new Error('Недостаточно прав'));
    const onAssigned = vi.fn();
    render(<CourtWorkloadDialog open caseId={42} onClose={vi.fn()} onAssigned={onAssigned} />);
    fireEvent.click((await screen.findAllByRole('button', { name: 'Назначить' }))[0]);
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(onAssigned).not.toHaveBeenCalled();
  });
});
