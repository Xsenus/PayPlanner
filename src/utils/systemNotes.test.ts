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

  it('hides technical markers even when they are prefixed for display', () => {
    const notes = [
      'System: [pp:invoice-items] [{"name":"Составление"}]',
      'Системно: [pp:installment-payment] {"sequence":1,"months":6}',
      'System: Remaining payment moved to next month',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual(['Remaining payment moved to next month']);
  });

  it('hides legacy invoice-items payload even when marker is already stripped', () => {
    const notes = [
      'Системно: [{"Name":"Составление документов","Quantity":7,"Unit":"шт","Price":1357.14}]',
      'Системно: Payment source updated',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual(['Payment source updated']);
  });

  it('hides installment payload even without explicit marker', () => {
    const notes = [
      'System: {"sequence":2,"months":6,"dueDate":"2026-06-07"}',
      'System: Remaining payment moved',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual(['Remaining payment moved']);
  });
});
