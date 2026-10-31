import { describe, expect, it } from 'vitest';
import {
  getVisibleNoteLines,
  getVisibleNotes,
  getVisibleSystemNoteLines,
  getVisibleSystemNotes,
  getAppliedClientBalanceAmount,
} from './systemNotes';
import { INSTALLMENT_PAYMENT_MARKER } from './installmentPlan';
import { PARTIAL_REMAINDER_PAYMENT_MARKER, PARTIAL_REMAINDER_SOURCE_MARKER } from './invoicePayments';

describe('systemNotes helpers', () => {
  it('hides technical invoice and installment markers', () => {
    const notes = [
      'Regular system note',
      '[pp:invoice-items] [{"name":"Document preparation","quantity":1}]',
      `${INSTALLMENT_PAYMENT_MARKER} {"sequence":1,"months":6}`,
      `${PARTIAL_REMAINDER_PAYMENT_MARKER} {"sourcePaymentId":548}`,
      `${PARTIAL_REMAINDER_SOURCE_MARKER} {"sourcePaymentId":547}`,
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
      'System: [pp:invoice-items] [{"name":"Document preparation"}]',
      'Системно: [pp:installment-payment] {"sequence":1,"months":6}',
      'System: Remaining payment moved to next month',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual(['Remaining payment moved to next month']);
  });

  it('hides legacy invoice-items payload even when marker is already stripped', () => {
    const notes = [
      'Системно: [{"Name":"Document preparation","Quantity":7,"Unit":"шт","Price":1357.14}]',
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

  it('hides invoice item markers from regular notes too', () => {
    const notes = [
      'Системно: [pp:invoice-items] [{"Name":"\\u0421\\u043e\\u0441\\u0442\\u0430\\u0432\\u043b\\u0435\\u043d\\u0438\\u0435","Quantity":7,"Unit":"\\u0448\\u0442","Price":1357.14}]',
      'Комментарий клиента',
    ].join('\r\n');

    expect(getVisibleNoteLines(notes)).toEqual(['Комментарий клиента']);
    expect(getVisibleNotes(notes)).toBe('Комментарий клиента');
  });

  it('formats legacy installment amounts for display', () => {
    const notes = [
      'Основной долг: 15000.00',
      'Проценты: 0.00',
      'Остаток: 47 500,50 ₽',
    ].join('\n');

    expect(getVisibleNotes(notes).replaceAll('\u00a0', ' ')).toBe([
      'Основной долг: 15 000,00 ₽',
      'Проценты: 0,00 ₽',
      'Остаток: 47 500,50 ₽',
    ].join('\n'));
  });

  it('hides escaped legacy invoice payloads without explicit marker', () => {
    const notes = [
      'Системно: [{\\"Name\\":\\"\\u0421\\u043e\\u0441\\u0442\\u0430\\u0432\\u043b\\u0435\\u043d\\u0438\\u0435 \\u0434\\u043e\\u043a\\u0443\\u043c\\u0435\\u043d\\u0442\\u043e\\u0432\\",\\"Quantity\\":7,\\"Unit\\":\\"\\u0448\\u0442\\",\\"Price\\":1357.14}]',
      'Комментарий клиента',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual(['Комментарий клиента']);
    expect(getVisibleNoteLines(notes)).toEqual(['Комментарий клиента']);
    expect(getVisibleNotes(notes)).toBe('Комментарий клиента');
  });

  it('repairs old windows-1251 mojibake system notes for display', () => {
    const notes = [
      '\u0421\u0438\u0441\u0442\u0435\u043c\u043d\u043e: \u0420\u2014\u0420\xb0\u0421\u2021\u0421\u201a\u0420\xb5\u0420\u0405\u0420\u0455 \u0421\u0403 \u0420\xb1\u0420\xb0\u0420\xbb\u0420\xb0\u0420\u0405\u0421\u0403\u0420\xb0 \u0420\u0454\u0420\xbb\u0420\u0451\u0420\xb5\u0420\u0405\u0421\u201a\u0420\xb0: 750,00',
      '\u0420\u045f\u0421\u0402\u0420\u0455\u0421\u0403\u0421\u0402\u0420\u0455\u0421\u2021\u0420\xb5\u0420\u0405 \u0420\u0406\u0421\u2039\u0420\u0457\u0420\u0455\u0420\xbb\u0420\u0405\u0420\xb5\u0420\u0405',
    ].join('\n');

    expect(getVisibleSystemNoteLines(notes)).toEqual([
      'Зачтено с баланса клиента: 750,00',
      'Просрочен выполнен',
    ]);
  });

  it('calculates applied client balance from visible system notes', () => {
    const notes = [
      'Системно: Зачтено с баланса клиента: 750,00',
      'Зачтено с баланса клиента: 1 250.50',
      'Системно: источник платежа изменен',
    ].join('\n');

    expect(getAppliedClientBalanceAmount(notes)).toBe(2000.5);
  });

  it('recognizes current counterparty notes together with legacy notes without counting other financial events', () => {
    const notes = ['Зачтено с баланса контрагента: 1\u00a0250,50', 'Зачтено с баланса клиента: 100,25',
      'Зачтено с баланса: 5.25', 'Возвращено с баланса контрагента: 900,00', 'Зачтено с баланса контрагента: -3,00'].join('\n');
    expect(getAppliedClientBalanceAmount(notes)).toBe(1356);
  });
});
