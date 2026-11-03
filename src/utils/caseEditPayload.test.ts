import { describe, expect, it } from 'vitest';
import { buildCaseUpdatePayload } from './caseEditPayload';
import type { ClientCase } from '../types';

describe('case exact payload', () => {
  it('keeps historical scalars and financial/control state without sending navigation or read metadata', () => {
    const source = { id: 7, clientId: 3, title: '  Дело  ', description: '  Описание  ', strategy: '  Позиция  ', eventHistory: '  История\n  ', nextHearingAt: '2099-11-03T12:34:56.1234567', feeAmount: 12.125, courtAwardedAmount: 123.45, isUnderControl: true, positionReviewedAt: '2026-11-03T00:00:00Z', client: { id: 3 }, payments: [{ id: 8 }] } as unknown as ClientCase;
    const saved = buildCaseUpdatePayload(source, { title: 'Новое название' });
    expect(saved).toMatchObject({ title: 'Новое название', description: source.description, strategy: source.strategy, eventHistory: source.eventHistory, nextHearingAt: source.nextHearingAt, feeAmount: 12.125, courtAwardedAmount: 123.45, isUnderControl: true, client: null, legalEntity: null });
    for (const key of ['id', 'createdAt', 'positionReviewedAt', 'payments', 'originalSource', 'expectedSource']) expect(saved).not.toHaveProperty(key);
    expect(source).toMatchObject({ title: '  Дело  ', payments: [{ id: 8 }] });
  });
  it('keeps explicit date and assignee removals separate from omitted fields', () => {
    const source = { responsibleUserId: 5, nextHearingAt: '2099-11-03T12:34:56.1234567', startDate: '2026-11-03', dueDate: '2026-11-04', claimFiledAt: '2026-10-01', courtDecisionAt: '2026-11-01', appealDeadlineAt: '2026-12-01', cassationDeadlineAt: '2027-02-01', motionDeadlineAt: '2026-11-15', objectionDeadlineAt: '2026-11-16' };
    const patch = { responsibleUserId: null, nextHearingAt: null, startDate: null, dueDate: null, claimFiledAt: null, courtDecisionAt: null, appealDeadlineAt: null, cassationDeadlineAt: null, motionDeadlineAt: null, objectionDeadlineAt: null };
    expect(buildCaseUpdatePayload(source, patch)).toMatchObject(patch);
    expect(buildCaseUpdatePayload(source, {})).toMatchObject(source);
  });
  it('preserves explicit owner selection and clear flags without turning a legal entity into a client', () => {
    const saved = buildCaseUpdatePayload({ clientId: 3, legalEntityId: null, courtClaimAmount: 123.45 }, { clientId: null, legalEntityId: 9, courtClaimAmount: null, clearCourtClaimAmount: true });
    expect(saved).toMatchObject({ clientId: null, legalEntityId: 9, courtClaimAmount: null, clearCourtClaimAmount: true });
  });
  it('creates the same complete writable shape for a new card with no prior case', () => {
    expect(buildCaseUpdatePayload(null, { clientId: null, legalEntityId: 9, title: 'Новое дело', strategy: 'Позиция', eventHistory: 'История', documentsSummary: 'Материалы' })).toMatchObject({ clientId: null, legalEntityId: 9, title: 'Новое дело', strategy: 'Позиция', eventHistory: 'История', documentsSummary: 'Материалы', status: 'Open', feeAmount: 0, isUnderControl: false });
  });
});
