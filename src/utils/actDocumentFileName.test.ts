// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { buildActDocumentFileName } from './actDocumentFileName';

describe('buildActDocumentFileName', () => {
  it('uses a KUB-like Russian act filename', () => {
    expect(buildActDocumentFileName('1198', '2025-12-25', 'pdf'))
      .toBe('Акт_№1198_25.12.2025.pdf');
    expect(buildActDocumentFileName('1/25', '2025-01-15', 'word'))
      .toBe('Акт_№1_25_15.01.2025.docx');
  });

  it('keeps the filename valid when act data is incomplete', () => {
    expect(buildActDocumentFileName('', '', 'pdf'))
      .toBe('Акт_№без_номера_без_даты.pdf');
  });
});
