import { describe, expect, it } from 'vitest';
import { captureCaseEditSource, caseWritSubmissionInput } from './caseEditInput';
import type { ClientCase } from '../types';

describe('case draft source and writ submission', () => {
  it.each(['bank', 'fssp', null] as const)('keeps the original destination %s without manufacturing a clear for an empty date', destination => {
    expect(caseWritSubmissionInput('', destination ?? '', { executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination })).toEqual({ executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination, clearExecutiveWritSubmission: false });
  });
  it('does not silently discard an explicitly changed destination when a newly entered date was removed', () => {
    expect(caseWritSubmissionInput('', 'fssp', { executiveWritSubmittedAt: null, executiveWritSubmittedTo: 'bank' })).toEqual({ executiveWritSubmittedAt: null, executiveWritSubmittedTo: 'fssp', clearExecutiveWritSubmission: false });
  });
  it.each(['bank', 'fssp'] as const)('clears both submission fields only after removing an originally saved date (%s)', destination => {
    expect(caseWritSubmissionInput('', destination, { executiveWritSubmittedAt: '2026-10-01', executiveWritSubmittedTo: destination })).toEqual({ executiveWritSubmittedAt: null, executiveWritSubmittedTo: '', clearExecutiveWritSubmission: true });
  });
  it('preserves the original precision when a submission date has not changed', () => {
    expect(caseWritSubmissionInput('2026-10-01', 'bank', { executiveWritSubmittedAt: '2026-10-01T12:34:56.1234567Z' })).toEqual({ executiveWritSubmittedAt: '2026-10-01T12:34:56.1234567Z', executiveWritSubmittedTo: 'bank', clearExecutiveWritSubmission: false });
  });
  it('retains a newly entered date and its destination', () => {
    expect(caseWritSubmissionInput('2026-10-02', 'fssp', {})).toEqual({ executiveWritSubmittedAt: '2026-10-02', executiveWritSubmittedTo: 'fssp', clearExecutiveWritSubmission: false });
  });
  it('does not mutate history or retain navigation references in the original snapshot', () => {
    const original = { id: 7, title: 'Исходное', eventHistory: '  История\r\n  ', nextHearingAt: null, client: { id: 3 }, payments: [{ id: 4 }] } as unknown as ClientCase;
    const snapshot = captureCaseEditSource(original); original.title = 'Другая правка';
    expect(snapshot).toEqual({ id: 7, title: 'Исходное', eventHistory: '  История\r\n  ', nextHearingAt: null });
  });
});
