import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCaseDocument } from '../../types';
import { MessengerDocumentDeliveryModal } from './MessengerDocumentDeliveryModal';

vi.mock('../../services/api', () => ({
  apiService: {
    getMessengerDocumentDeliveryConfiguration: vi.fn(),
    getMessengerRecipients: vi.fn(),
    getMessengerDocumentDeliveryHistory: vi.fn(),
    prepareCaseDocumentMessengerDelivery: vi.fn(),
    queueMessengerDocumentDelivery: vi.fn(),
    createMessengerRecipient: vi.fn(),
  },
}));

const document: ClientCaseDocument = {
  id: 17,
  clientCaseId: 5,
  documentKey: 'claim',
  version: 2,
  documentType: 'Исковое заявление',
  status: 'Готов',
  originalFileName: 'Исковое заявление.pdf',
  contentType: 'application/pdf',
  sizeBytes: 2048,
  uploadedByName: 'Администратор',
  uploadedAt: '2026-07-31T10:00:00Z',
  isLatestVersion: true,
};

describe('MessengerDocumentDeliveryModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('безопасно блокирует отправку, когда интеграция выключена', async () => {
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({
      enabled: false,
      linkLifetimeHours: 24,
      providers: [
        { channel: 'telegram', enabled: false },
        { channel: 'max', enabled: false },
      ],
    });

    render(<MessengerDocumentDeliveryModal document={document} clientId={11} onClose={vi.fn()} />);

    expect(await screen.findByText(/доставка документов выключена/i)).toBeTruthy();
    expect((screen.getByRole('button', { name: /подготовить и отправить/i }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiService.getMessengerRecipients).not.toHaveBeenCalled();
  });

  it('требует подтверждение и ставит выбранную версию в очередь', async () => {
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({
      enabled: true,
      linkLifetimeHours: 24,
      providers: [{ channel: 'telegram', enabled: true }],
    });
    vi.mocked(apiService.getMessengerRecipients).mockResolvedValue([{
      id: 8,
      clientId: 11,
      channel: 'telegram',
      provider: 'telegram',
      displayName: 'Иван Иванов',
      maskedAddress: '***4567',
      verificationStatus: 'verified',
      verificationMethod: 'admin-confirmed',
      verifiedAt: '2026-07-31T10:00:00Z',
    }]);
    vi.mocked(apiService.getMessengerDocumentDeliveryHistory).mockResolvedValue([]);
    vi.mocked(apiService.prepareCaseDocumentMessengerDelivery).mockResolvedValue({
      id: 101,
      publicId: '00000000-0000-0000-0000-000000000101',
      documentType: 'case-document',
      documentId: 17,
      documentVersion: 2,
      originalFileName: document.originalFileName,
      checksumSha256: 'ABC',
      sizeBytes: 2048,
      channel: 'telegram',
      provider: 'telegram',
      status: 'prepared',
      linkExpiresAt: '2026-08-01T10:00:00Z',
    });
    vi.mocked(apiService.queueMessengerDocumentDelivery).mockResolvedValue({ id: 101, status: 'queued' });

    render(<MessengerDocumentDeliveryModal document={document} clientId={11} onClose={vi.fn()} />);
    expect((await screen.findAllByText(/иван иванов/i)).length).toBeGreaterThan(0);

    const submit = screen.getByRole('button', { name: /подготовить и отправить/i }) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    await userEvent.click(screen.getByText(/подтверждаю выбранный документ/i));
    expect(submit.disabled).toBe(false);
    await userEvent.click(submit);

    await waitFor(() => expect(apiService.prepareCaseDocumentMessengerDelivery).toHaveBeenCalledWith(17, expect.objectContaining({
      recipientBindingId: 8,
      confirmRecipientAndDocument: true,
    })));
    expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledWith(101);
    expect(await screen.findByText(/поставлен в безопасную очередь/i)).toBeTruthy();
  });
});
