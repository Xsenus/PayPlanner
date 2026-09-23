import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { CaseAppealDecisionAction } from './CaseAppealDecisionAction';

vi.mock('../../services/api', () => ({
  apiService: {
    getCurrentCaseAppealDecision: vi.fn(),
    createCaseAppealTask: vi.fn(),
  },
}));

const clientCase = {
  id: 42,
  title: 'Спор клиента',
  status: 'Open',
  courtDecisionAt: '2026-09-20T00:00:00',
  appealDeadlineAt: '2026-10-20T00:00:00',
} as ClientCase;

describe('CaseAppealDecisionAction', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockReset();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue(null);
    vi.mocked(apiService.createCaseAppealTask).mockReset();
    vi.mocked(apiService.createCaseAppealTask).mockResolvedValue({
      id: 8, caseId: 42, choice: 'appeal', taskId: 91, decidedAt: '2026-09-24T00:00:00Z', decidedByUserName: 'Юрист',
    });
  });

  it('lets the lawyer decide before the last-three-days popup and shows the created task', async () => {
    const onTaskCreated = vi.fn();
    render(<CaseAppealDecisionAction clientCase={clientCase} canEdit onTaskCreated={onTaskCreated} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Обжаловать — создать задачу' }));
    await waitFor(() => expect(apiService.createCaseAppealTask).toHaveBeenCalledWith(42, {
      courtDecisionAt: clientCase.courtDecisionAt,
      appealDeadlineAt: clientCase.appealDeadlineAt,
    }));
    expect(onTaskCreated).toHaveBeenCalledWith(91);
    expect(screen.getByText(/Решено обжаловать.*Юрист/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Обжаловать — создать задачу' })).toBeNull();
  });

  it('does not offer a second choice for a recorded act or unsaved dates', async () => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue({
      id: 8, caseId: 42, choice: 'appeal', taskId: 91, decidedAt: '2026-09-24T00:00:00Z',
    });
    const recorded = render(<CaseAppealDecisionAction clientCase={clientCase} canEdit />);
    expect(await screen.findByText(/Решено обжаловать/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Обжаловать — создать задачу' })).toBeNull();
    recorded.unmount();

    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue(null);
    render(<CaseAppealDecisionAction clientCase={clientCase} canEdit datesSaved={false} />);
    const button = await screen.findByRole('button', { name: 'Обжаловать — создать задачу' });
    expect(button.hasAttribute('disabled')).toBe(true);
    expect(screen.getByText(/Сначала сохраните изменённые даты/)).toBeTruthy();
    expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });

  it('opens the task created for an appeal decision', async () => {
    const onOpenTask = vi.fn();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue({
      id: 8, caseId: 42, choice: 'appeal', taskId: 91, decidedAt: '2026-09-24T00:00:00Z',
    });

    render(<CaseAppealDecisionAction clientCase={clientCase} canEdit onOpenTask={onOpenTask} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' }));
    expect(onOpenTask).toHaveBeenCalledExactlyOnceWith(91);
  });
});
