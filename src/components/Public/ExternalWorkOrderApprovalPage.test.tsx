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
    resetApiServiceMock(
      'openPublicWorkOrderApproval',
      'decidePublicWorkOrderApproval',
      'createPublicWorkOrderAttachmentGrant',
      'downloadPublicWorkOrderAttachment',
    );
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
        attachments: [{
          publicId: '22222222-2222-4222-8222-222222222222',
          fileName: 'Расчёт.pdf',
          contentType: 'application/pdf',
          sizeBytes: 1024,
        }],
      },
    });
    apiServiceMock.decidePublicWorkOrderApproval.mockResolvedValue({
      status: 'approved',
      decision: 'approved',
      comment: '',
      decisionAt: '2026-07-31T05:00:00Z',
    });
    apiServiceMock.createPublicWorkOrderAttachmentGrant.mockResolvedValue({
      grantToken: 'short-lived-grant',
      expiresAt: '2026-07-31T05:05:00Z',
    });
    apiServiceMock.downloadPublicWorkOrderAttachment.mockResolvedValue(new Blob(['pdf']));
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

  it('downloads only an explicitly listed attachment through a short-lived grant', async () => {
    const createObjectUrl = vi.fn(() => 'blob:work-order-attachment');
    const revokeObjectUrl = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    vi.stubGlobal('URL', Object.assign(URL, {
      createObjectURL: createObjectUrl,
      revokeObjectURL: revokeObjectUrl,
    }));

    render(<ExternalWorkOrderApprovalPage />);
    fireEvent.click(await screen.findByRole('button', { name: /Расчёт\.pdf/ }));

    await waitFor(() => {
      expect(apiServiceMock.createPublicWorkOrderAttachmentGrant).toHaveBeenCalledWith(
        PUBLIC_ID,
        '22222222-2222-4222-8222-222222222222',
        'safe-token',
      );
      expect(apiServiceMock.downloadPublicWorkOrderAttachment).toHaveBeenCalledWith(
        PUBLIC_ID,
        '22222222-2222-4222-8222-222222222222',
        'short-lived-grant',
      );
    });
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(click).toHaveBeenCalledTimes(1);
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:work-order-attachment');
    click.mockRestore();
    vi.unstubAllGlobals();
  });
});
