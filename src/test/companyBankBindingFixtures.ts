import type { BankConnectionItem } from '../types';
import type { CompanyBankActiveBinding, CompanyBankMatchReport } from '../services/companyBankBindings';

export const bankVersion = (value = 'A') => `bank-profile-match-v2:${value.repeat(64)}`;
export const bindingFixture = (): CompanyBankActiveBinding => ({ id: 77, profileAccountId: 'profile-A', bankAccountId: 101,
  accountNumber: '40702810200000000001', bik: '044525225', currency: 'RUB', confirmedAtUtc: '2026-11-01T02:00:00Z' });
export const bankConnectionFixture = (companyId = 5): BankConnectionItem => ({ id: companyId + 6, legalEntityId: companyId, provider: 'Sber', status: 'Active',
  accounts: [{ id: companyId + 96, externalAccountId: 'synthetic', accountNumber: `4070281020000000000${companyId - 4}`, bik: '044525225', currency: 'RUB', status: 'Open', isDefault: true, isHidden: false, statementsEnabled: true }] });
export const bankReportFixture = (companyId = 5): CompanyBankMatchReport => ({ companyId, bankConnectionId: companyId + 6, version: bankVersion(), canBind: true, profileIssues: [],
  groups: [{ id: companyId + 96, profileMatchStatus: 'UniqueCandidate', profileCandidates: [{ id: 'profile-A', name: 'Основной расчётный' }], bankAccountIds: [companyId + 96], manualMoneyAccountCandidateIds: [], hasArchivedProfileCandidates: false }],
  accounts: [{ bankAccountId: companyId + 96, groupId: companyId + 96, linkedMoneyAccountIds: [55], issues: [],
    provider: 'Sber', externalAccountId: 'synthetic', accountNumber: bankConnectionFixture(companyId).accounts[0].accountNumber,
    bik: '044525225', currency: 'RUB', status: 'Open', isHidden: false, connectionStatus: 'Active' }], activeBindings: [] });
