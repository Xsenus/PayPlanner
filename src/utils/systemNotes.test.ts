import { describe, expect, it } from 'vitest';
import { getVisibleSystemNoteLines, getVisibleSystemNotes } from './systemNotes';
import { INSTALLMENT_PAYMENT_MARKER } from './installmentPlan';

describe('systemNotes helpers', () => {
  it('hides technical invoice and installment markers', () => {
    const notes = [
      'Regular system note',
      '[pp:invoice-items] [{"name":"Document preparation","quantity":1}]',
      `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1,"months":6}`,
      '\u2022 Payment moved to a new date',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual([
      'Regular system note',
      'Payment moved to a new date',
    ]);
  });

  it('returns a display string without hidden lines', () => {
    const notes = [
      '[pp:invoice-items] [{"name":"Expert review"}]',
      'Remaining payment moved',
    ].join('\n');

    expect(getVisibleSystemNotes(notes)).toBe('Remaining payment moved');
  });
});
