import { describe, expect, it } from 'vitest';
import type { ActWorkflowStatus, ContractStatus, DocumentStatusEntity, InvoiceWorkflowStatus } from '../types';
import { getDocumentStatusCodes, getDocumentStatusLabels, isDocumentStatusCodeAllowedForKind } from './documentStatusSettings';

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

  it('uses client signed label for act and contract signing statuses', () => {
    const actLabels = getDocumentStatusLabels<ActWorkflowStatus>(
      [documentStatus({ code: 'Signed', name: 'Подписан', useForActs: true })],
      'act',
      {
        Draft: 'Черновик',
        NotSigned: 'Не подписан',
        RequiresClientSignature: 'Требуется подпись клиента',
        OnApproval: 'На согласовании',
        Approved: 'Согласован',
        ApprovalRejected: 'Отказ в согласовании',
        NeedsCorrection: 'Требует исправления',
        OnSigning: 'На подписании',
        Signed: 'Подписан',
        Cancelled: 'Аннулирован',
      },
    );
    const contractLabels = getDocumentStatusLabels<ContractStatus>(
      [documentStatus({ code: 'Concluded', name: 'Подписан', useForContracts: true })],
      'contract',
      {
        Draft: 'Черновик',
        NotSigned: 'Не подписан',
        RequiresClientSignature: 'Требуется подпись клиента',
        OnApproval: 'На согласовании',
        Approved: 'Согласован',
        ApprovalRejected: 'Отказ в согласовании',
        NeedsCorrection: 'Требует исправления',
        OnSigning: 'На подписании',
        Concluded: 'Подписан',
        Terminated: 'Расторгнут',
        Cancelled: 'Аннулирован',
      },
    );

    expect(actLabels.Signed).toBe('Подписан клиентом');
    expect(contractLabels.Concluded).toBe('Подписан клиентом');
  });

  it('marks signing status codes as available only for their document kind', () => {
    expect(isDocumentStatusCodeAllowedForKind('SignedByClient', 'invoice')).toBe(true);
    expect(isDocumentStatusCodeAllowedForKind('SignedByClient', 'act')).toBe(false);
    expect(isDocumentStatusCodeAllowedForKind('Signed', 'act')).toBe(true);
    expect(isDocumentStatusCodeAllowedForKind('Signed', 'invoice')).toBe(false);
    expect(isDocumentStatusCodeAllowedForKind('Concluded', 'contract')).toBe(true);
    expect(isDocumentStatusCodeAllowedForKind('Concluded', 'act')).toBe(false);
  });
});
