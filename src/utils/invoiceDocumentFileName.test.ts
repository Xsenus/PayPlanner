// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { buildInvoiceDocumentFileName } from './invoiceDocumentFileName';

describe('buildInvoiceDocumentFileName', () => {
  it('uses KUB-like Russian names for every invoice document type', () => {
    expect(buildInvoiceDocumentFileName('28', '2026-04-05', 'pdf')).toBe('Счет_№28_05.04.2026.pdf');
    expect(buildInvoiceDocumentFileName('32', '2026-06-09', 'word', 'invoice-offer'))
      .toBe('Счет-договор_№32_09.06.2026.docx');
    expect(buildInvoiceDocumentFileName('1/25', '2025-01-15', 'pdf', 'invoice-order'))
      .toBe('Заказ_№1_25_15.01.2025.pdf');
  });

  it('keeps filenames valid when document data is incomplete', () => {
    expect(buildInvoiceDocumentFileName('', '', 'pdf')).toBe('Счет_№без_номера_без_даты.pdf');
  });
});
