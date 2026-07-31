import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { ExternalWorkOrderApprovalPage } from './ExternalWorkOrderApprovalPage';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

const PUBLIC_ID = '11111111-1111-4111-8111-111111111111';

describe('ExternalWorkOrderApprovalPage', () => {
  beforeEach(() => {
    resetApiServiceMock('openPublicWorkOrderApproval', 'decidePublicWorkOrderApproval');
    sessionStorage.clear();
    window.history.replaceState(null, '', `/external/work-order-approval/${PUBLIC_ID}#token=safe-token`);
    apiServiceMock.openPublicWorkOrderApproval.mockResolvedValue({
      publicId: PUBLIC_ID,
      status: 'opened',
      expiresAt: '2026-08-03T10:00:00Z',
      recipientHint: 'c***t@example.test',
      snapshot: {
        organizationName: 'ЭлитПраво',
        clientName: 'Иван Иванов',
        caseTitle: 'Тестовое дело',
        caseNumber: 'A-1',
        version: 2,
        title: 'Заказ-наряд',
        currency: 'RUB',
        totalAmount: 15000,
        items: [{
          serviceName: 'Подготовка заявления',
          description: 'Описание',
          dueDate: null,
          quantity: 1,
          unitPrice: 15000,
          totalAmount: 15000,
        }],
      },
    });
    apiServiceMock.decidePublicWorkOrderApproval.mockResolvedValue({
      status: 'approved',
      decision: 'approved',
      comment: '',
      decisionAt: '2026-07-31T05:00:00Z',
    });
  });

  it('removes token from URL and records explicit OTP confirmation', async () => {
    render(<ExternalWorkOrderApprovalPage />);

    expect(await screen.findByText('Заказ-наряд')).toBeTruthy();
    expect(window.location.hash).toBe('');
    expect(apiServiceMock.openPublicWorkOrderApproval).toHaveBeenCalledWith(PUBLIC_ID, 'safe-token');

    fireEvent.change(screen.getByLabelText('Код из письма'), { target: { value: '123456' } });
    fireEvent.click(screen.getByLabelText(/Я проверил/));
    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));

    await waitFor(() => {
      expect(apiServiceMock.decidePublicWorkOrderApproval).toHaveBeenCalledWith(PUBLIC_ID, {
        token: 'safe-token',
        otp: '123456',
        decision: 'approved',
        comment: '',
      });
    });
    expect(await screen.findByText('Заказ-наряд согласован')).toBeTruthy();
  });
});
