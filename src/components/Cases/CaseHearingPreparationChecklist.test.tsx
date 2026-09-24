import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseHearingPreparation } from '../../types';
import { CaseHearingPreparationChecklist } from './CaseHearingPreparationChecklist';

vi.mock('../../services/api', () => ({
  apiService: {
    getCaseHearingPreparation: vi.fn(),
    getCaseDocuments: vi.fn(),
    updateCaseHearingPreparation: vi.fn(),
  },
}));

const hearingAt = '2099-10-20T15:00:00Z';
const caseItem = { id: 42, title: 'Дело', status: 'Open', nextHearingAt: hearingAt } as ClientCase;
const preparation: ClientCaseHearingPreparation = {
  hearingAt,
  contractCheckedAt: null,
  contractCheckedByName: '',
  authorityCheckedAt: null,
  authorityCheckedByName: '',
  materialsCheckedAt: null,
  materialsCheckedByName: '',
  documentIds: [],
};

describe('CaseHearingPreparationChecklist', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCaseHearingPreparation).mockReset().mockResolvedValue(preparation);
    vi.mocked(apiService.getCaseDocuments).mockReset().mockResolvedValue([{
      id: 7, originalFileName: 'Позиция.pdf',
    }] as never);
    vi.mocked(apiService.updateCaseHearingPreparation).mockReset().mockImplementation(async (_caseId, payload) => ({
      ...preparation,
      ...(payload.checkKind === 'contract' ? { contractCheckedAt: '2099-10-19T10:00:00Z', contractCheckedByName: 'Юрист' } : {}),
      ...(payload.checkKind === 'materials' ? { materialsCheckedAt: '2099-10-19T10:00:00Z', documentIds: payload.documentIds ?? [] } : {}),
    }));
  });

  it('records separate checks and links documents for the saved hearing', async () => {
    render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    expect(await screen.findByText('Позиция.pdf')).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Подтвердить' })[0]);
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'contract', checked: true,
    }));
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getAllByRole('button', { name: 'Подтвердить' })[1]);
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'materials', checked: true, documentIds: [7],
    }));
  });

  it('shows checks without editing to a viewer and disables saving an unsaved date', async () => {
    const view = render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit={false} />);
    await screen.findByText('Позиция.pdf');
    expect(screen.queryByRole('button', { name: 'Подтвердить' })).toBeNull();
    view.rerender(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit datesSaved={false} />);
    expect(screen.getByText(/Сначала сохраните новую дату/)).toBeTruthy();
    expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });

  it('loads separate preparation after hearing date moves', async () => {
    const view = render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    await screen.findByText('Позиция.pdf');
    const movedAt = '2099-10-22T15:00:00Z';
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({ ...preparation, hearingAt: movedAt });
    view.rerender(<CaseHearingPreparationChecklist caseItem={{ ...caseItem, nextHearingAt: movedAt }} timeZoneId="UTC" canEdit />);
    await waitFor(() => expect(apiService.getCaseHearingPreparation).toHaveBeenCalledTimes(2));
    expect(screen.getAllByText('Не проверено').length).toBe(3);
  });

  it('removes materials approval when the linked document set changes', async () => {
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({
      ...preparation, materialsCheckedAt: '2099-10-19T10:00:00Z',
      materialsCheckedByName: 'Юрист', documentIds: [7],
    });
    render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    await screen.findByText('Позиция.pdf');
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связи с документами' }));
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'materials', checked: false, documentIds: [],
    }));
  });
});
