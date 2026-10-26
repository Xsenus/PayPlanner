import { afterEach, describe, expect, it } from 'vitest';
import { clearInvoiceEditorDraft, INVOICE_EDITOR_STORAGE_KEY, peekInvoiceEditorDraft, storeInvoiceEditorDraft } from './invoiceEditorBridge';

describe('invoice editor seller draft', () => {
  afterEach(() => clearInvoiceEditorDraft());

  it('keeps the selected company and branch across a nested-page draft roundtrip', () => {
    localStorage.setItem(INVOICE_EDITOR_STORAGE_KEY, JSON.stringify({ mode: 'create', presentation: 'page', form: { number: 'DRAFT-1', amount: '100' },
      lineItems: [{ id: 'row-1', name: 'Услуга', quantity: '1', price: '100' }], sellerSelection: { companyId: 41, branchId: 42 } }));
    const restored = peekInvoiceEditorDraft()!;
    expect(restored.sellerSelection).toEqual({ companyId: 41, branchId: 42 });
    storeInvoiceEditorDraft(restored);
    expect(peekInvoiceEditorDraft()).toEqual(restored);
    expect(restored.form.number).toBe('DRAFT-1');
    expect(restored.lineItems[0]).toMatchObject({ name: 'Услуга', price: '100' });
  });

  it('does not restore an orphan branch or an unsafe company identifier', () => {
    localStorage.setItem(INVOICE_EDITOR_STORAGE_KEY, JSON.stringify({ mode: 'create', form: {}, sellerSelection: { companyId: Number.MAX_SAFE_INTEGER + 1, branchId: 42 } }));
    expect(peekInvoiceEditorDraft()?.sellerSelection).toEqual({ companyId: null, branchId: null });
  });
});
