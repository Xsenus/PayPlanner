import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CompanyRequisitesCheck } from './CompanyRequisitesCheck';
import { apiService } from '../../services/api';

vi.mock('../../services/api', () => ({
  apiService: { getCompanyRequisitesCheck: vi.fn() },
}));

describe('CompanyRequisitesCheck', () => {
  beforeEach(() => vi.clearAllMocks());

  it('lists the issues returned by the server', async () => {
    vi.mocked(apiService.getCompanyRequisitesCheck).mockResolvedValue({
      legalEntityId: 5,
      issues: [{ field: 'inn', message: 'ИНН не проходит проверку контрольных разрядов.' }],
    });
    render(<CompanyRequisitesCheck companyId={5} />);

    expect(await screen.findByText('ИНН не проходит проверку контрольных разрядов.')).toBeTruthy();
  });

  it('shows that the saved data check completed without issues', async () => {
    vi.mocked(apiService.getCompanyRequisitesCheck).mockResolvedValueOnce({ legalEntityId: 5, issues: [] });
    render(<CompanyRequisitesCheck companyId={5} />);
    await waitFor(() => expect(apiService.getCompanyRequisitesCheck).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('По проверяемым реквизитам замечаний нет.')).toBeTruthy();
  });

  it('does not present an access failure as a successful check', async () => {
    vi.mocked(apiService.getCompanyRequisitesCheck).mockRejectedValueOnce(new Error('403'));
    render(<CompanyRequisitesCheck companyId={6} />);
    expect((await screen.findByRole('alert')).textContent).toContain('403');
    expect(screen.queryByText('По проверяемым реквизитам замечаний нет.')).toBeNull();
  });
});
