import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { ElectronicSignatureModal } from './ElectronicSignatureModal';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('ElectronicSignatureModal', () => {
  beforeEach(() => {
    resetApiServiceMock(
      'getElectronicSignatureConfiguration',
      'getElectronicSignatureRecipients',
      'getElectronicSignatureHistory',
      'prepareElectronicSignatureEnvelope',
    );
    apiServiceMock.getElectronicSignatureConfiguration.mockResolvedValue({
      enabled: false,
      preparationEnabled: true,
      defaultSignatureType: 'qualified',
      defaultSigningOrder: 'sequential',
      defaultArchiveRetentionYears: 10,
      providers: [{ name: 'KonturDiadoc', enabled: false }],
    });
    apiServiceMock.getElectronicSignatureRecipients.mockResolvedValue([
      { id: 7, displayName: 'Иван Иванов', email: 'ivan@example.test', accountType: 'client', isRepresentative: false },
      { id: 8, displayName: 'Анна Петрова', email: 'anna@example.test', accountType: 'representative', isRepresentative: true },
    ]);
    apiServiceMock.getElectronicSignatureHistory.mockResolvedValue([]);
    apiServiceMock.prepareElectronicSignatureEnvelope.mockResolvedValue({ id: 1 });
  });

  it('prepares an immutable snapshot while the external provider is disabled', async () => {
    const preparePdf = vi.fn(async () => new Blob(['%PDF-1.7'], { type: 'application/pdf' }));
    render(<ElectronicSignatureModal documentType="invoice" documentId={15} documentName="Счёт 15" clientId={3} preparePdf={preparePdf} onClose={() => undefined} />);

    expect(await screen.findByText('Иван Иванов')).toBeTruthy();
    expect(screen.getByText(/Внешняя отправка безопасно выключена/)).toBeTruthy();
    fireEvent.click(screen.getByText('Иван Иванов'));
    fireEvent.click(screen.getByText(/Подтверждаю выбранную неизменяемую PDF-версию/));
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }));

    await waitFor(() => expect(apiServiceMock.prepareElectronicSignatureEnvelope).toHaveBeenCalledWith(
      'invoice',
      15,
      expect.any(Blob),
      expect.objectContaining({
        clientId: 3,
        signerAccountIds: [7],
        signatureType: 'qualified',
        signingOrder: 'sequential',
        provider: 'KonturDiadoc',
        confirmSnapshotAndSigners: true,
      }),
    ));
    expect(await screen.findByText(/Отправка останется выключенной/)).toBeTruthy();
  });

  it('requires an explicitly granted portal account', async () => {
    apiServiceMock.getElectronicSignatureRecipients.mockResolvedValue([]);
    render(<ElectronicSignatureModal documentType="act" documentId={2} documentName="Акт 2" clientId={3} preparePdf={vi.fn()} onClose={() => undefined} />);

    expect(await screen.findByText(/Сначала выдайте клиенту или представителю явный доступ/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
