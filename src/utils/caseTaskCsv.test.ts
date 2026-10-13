import { describe, expect, it, vi } from 'vitest';
import { buildCaseTaskCsv, caseTaskCsvCell, downloadCaseTaskCsv } from './caseTaskCsv';
import type { TaskJournalItem } from './taskJournalStore';

describe('selected case task CSV', () => {
  it.each(['=HYPERLINK("https://example.test")', '  +SUM(1)', '-1+2', '@SUM(1)', '\ttext', '\ntext', '\uFEFF=1'])('neutralizes spreadsheet input %j', (value) => {
    expect(caseTaskCsvCell(value)).toBe(`"'${value.replaceAll('"', '""')}"`);
  });
  it('preserves ordinary Unicode, separators, quotes and empty values', () => {
    expect(caseTaskCsvCell('Задача; "А"\nБ')).toBe('"Задача; ""А""\nБ"');
    expect(caseTaskCsvCell(null)).toBe('""');
    expect(caseTaskCsvCell(123)).toBe('"123"');
  });
  it('exports only provided rows with BOM, explicit UTC header and readable statuses', () => {
    const csv = buildCaseTaskCsv([{ id: 9, title: '=1', assigneeName: 'Иван', dueAt: '2026-10-13T12:00:00Z', status: 'done', result: 'Готово' } as TaskJournalItem], { done: 'Выполнена' });
    expect(csv.startsWith('\uFEFF')).toBe(true);
    expect(csv.split('\r\n')).toHaveLength(2);
    expect(csv).toContain('"Срок (UTC)"');
    expect(csv).toContain('"9";"\'=1";"Иван";"2026-10-13T12:00:00Z";"Выполнена";"Готово"');
  });
  it('keeps the object URL alive during the native download and removes the temporary link', () => {
    vi.useFakeTimers();
    const create = vi.fn(() => 'blob:owned-csv');
    const revoke = vi.fn();
    vi.stubGlobal('URL', { createObjectURL: create, revokeObjectURL: revoke });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      expect(this.isConnected).toBe(true);
      expect(this.download).toBe('selected.csv');
      expect(revoke).not.toHaveBeenCalled();
    });
    try {
      downloadCaseTaskCsv('selected.csv', '\uFEFFданные');
      expect(click).toHaveBeenCalledOnce();
      expect(document.querySelector('a[download]')).toBeNull();
      vi.advanceTimersByTime(1000);
      expect(revoke).toHaveBeenCalledWith('blob:owned-csv');
    } finally { vi.useRealTimers(); vi.unstubAllGlobals(); click.mockRestore(); }
  });
});
