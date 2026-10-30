import { vi } from 'vitest';
import { apiService } from '../services/api';

// Synthetic recipients and provider responses only; no delivery transport is started.
export function configureDocumentDeliveryTestApi(kind: 'invoice' | 'act' | 'contract', id: number, clientId: number, enabled = true) {
  vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockReset().mockResolvedValue({ enabled, linkLifetimeHours: 24, providers: [{ channel: 'telegram', enabled }] });
  vi.mocked(apiService.getMessengerRecipients).mockReset().mockResolvedValue([{ id: 8, clientId, channel: 'telegram', provider: 'telegram', displayName: 'Получатель PDF', maskedAddress: '***0000', verificationStatus: 'verified', verificationMethod: 'admin-confirmed' }]);
  vi.mocked(apiService.getMessengerDocumentDeliveryHistory).mockReset().mockResolvedValue([]);
  vi.mocked(apiService.prepareFinancialDocumentMessengerDelivery).mockReset().mockResolvedValue({ id: 102, publicId: '00000000-0000-4000-8000-000000000102', documentType: kind, documentId: id, documentVersion: 1, originalFileName: `${kind}.pdf`, checksumSha256: 'synthetic', sizeBytes: 8, channel: 'telegram', provider: 'telegram', status: 'prepared', linkExpiresAt: '2026-10-31T00:00:00Z' });
  vi.mocked(apiService.queueMessengerDocumentDelivery).mockReset().mockResolvedValue({ id: 102, status: 'queued' });
  vi.mocked(apiService.getElectronicSignatureConfiguration).mockReset().mockResolvedValue({ enabled: false, preparationEnabled: enabled, defaultSignatureType: 'qualified', defaultSigningOrder: 'sequential', defaultArchiveRetentionYears: 10, providers: [{ name: 'KonturDiadoc', enabled: false }] });
  vi.mocked(apiService.getElectronicSignatureRecipients).mockReset().mockResolvedValue([{ id: 7, displayName: 'Подписант PDF', email: 'signer@example.test', accountType: 'client', isRepresentative: false }]);
  vi.mocked(apiService.getElectronicSignatureHistory).mockReset().mockResolvedValue([]);
  vi.mocked(apiService.prepareElectronicSignatureEnvelope).mockReset().mockResolvedValue({ id: 103, publicId: '00000000-0000-4000-8000-000000000103', documentType: kind, documentId: id, documentVersion: 1, signatureType: 'qualified', signingOrder: 'sequential', provider: 'KonturDiadoc', status: 'prepared', originalFileName: `${kind}.pdf`, sizeBytes: 8, checksumSha256: 'synthetic', archiveRetentionYears: 10, requiresMachineReadablePowerOfAttorney: false, createdAt: '2026-10-30T00:00:00Z', signers: [], events: [] });
  return new Blob(['%PDF-1.7'], { type: 'application/pdf' });
}
