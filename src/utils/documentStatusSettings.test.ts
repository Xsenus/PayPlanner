import { describe, expect, it } from 'vitest';
import type { DocumentStatusEntity, InvoiceWorkflowStatus } from '../types';
import { getDocumentStatusCodes, getDocumentStatusLabels } from './documentStatusSettings';

const invoiceFallback: InvoiceWorkflowStatus[] = [
  'Draft',
  'NotSigned',
  'RequiresClientSignature',
  'OnApproval',
  'Approved',
  'ApprovalRejected',
  'NeedsCorrection',
  'OnSigning',
  'SignedByClient',
  'Cancelled',
];

function documentStatus(overrides: Partial<DocumentStatusEntity>): DocumentStatusEntity {
  return {
    id: overrides.id ?? 1,
    code: overrides.code ?? 'Draft',
    name: overrides.name ?? 'Черновик',
    description: overrides.description ?? '',
    colorHex: overrides.colorHex ?? '#64748B',
    isActive: overrides.isActive ?? true,
    useForInvoices: overrides.useForInvoices ?? true,
    useForActs: overrides.useForActs ?? true,
    useForContracts: overrides.useForContracts ?? true,
    sortOrder: overrides.sortOrder ?? 10,
    createdAt: overrides.createdAt ?? '2026-05-13T00:00:00Z',
  };
}

describe('documentStatusSettings', () => {
  it('keeps required client signature status visible when dictionary config hides it', () => {
    const codes = getDocumentStatusCodes<InvoiceWorkflowStatus>(
      [
        documentStatus({ code: 'Draft', sortOrder: 10 }),
        documentStatus({ code: 'RequiresClientSignature', isActive: false, useForInvoices: false, sortOrder: 30 }),
        documentStatus({ code: 'OnApproval', sortOrder: 40 }),
      ],
      'invoice',
      invoiceFallback,
    );

    expect(codes).toContain('RequiresClientSignature');
    expect(codes.indexOf('RequiresClientSignature')).toBeLessThan(codes.indexOf('OnApproval'));
  });

  it('uses canonical label for required client signature status', () => {
    const labels = getDocumentStatusLabels<InvoiceWorkflowStatus>(
      [
        documentStatus({
          code: 'RequiresClientSignature',
          name: 'Ожидает клиента',
          useForInvoices: true,
        }),
      ],
      'invoice',
      {
        Draft: 'Черновик',
        NotSigned: 'Не подписан',
        RequiresClientSignature: 'Требуется подпись клиента',
        OnApproval: 'На согласовании',
        Approved: 'Согласован',
        ApprovalRejected: 'Отказ в согласовании',
        NeedsCorrection: 'Требует исправления',
        OnSigning: 'На подписании',
        SignedByClient: 'Подписан клиентом',
        Paid: 'Оплачен',
        PartiallyPaid: 'Частично оплачен',
        Overdue: 'Просрочен',
        Cancelled: 'Аннулирован',
      },
    );

    expect(labels.RequiresClientSignature).toBe('Требуется подпись клиента');
  });
});
