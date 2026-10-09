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

  it('renders nothing when everything is fine or the check fails', async () => {
    vi.mocked(apiService.getCompanyRequisitesCheck).mockResolvedValueOnce({ legalEntityId: 5, issues: [] });
    const first = render(<CompanyRequisitesCheck companyId={5} />);
    await waitFor(() => expect(apiService.getCompanyRequisitesCheck).toHaveBeenCalledTimes(1));
    expect(first.container.textContent).toBe('');

    vi.mocked(apiService.getCompanyRequisitesCheck).mockRejectedValueOnce(new Error('403'));
    const second = render(<CompanyRequisitesCheck companyId={6} />);
    await waitFor(() => expect(apiService.getCompanyRequisitesCheck).toHaveBeenCalledTimes(2));
    expect(second.container.textContent).toBe('');
  });
});
