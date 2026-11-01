export interface CompanyBankMatchGroup {
  id: number;
  profileMatchStatus: 'Missing' | 'UniqueCandidate' | 'Ambiguous';
  profileCandidates: Array<{ id: string; name: string }>;
  bankAccountIds: number[];
  manualMoneyAccountCandidateIds: number[];
  hasArchivedProfileCandidates: boolean;
}
export interface CompanyBankActiveBinding {
  id: number;
  profileAccountId?: string | null;
  bankAccountId: number;
  accountNumber: string;
  bik: string;
  currency: string;
  confirmedAtUtc: string;
}
export interface CompanyBankMatchAccount {
  bankAccountId: number; groupId: number | null; linkedMoneyAccountIds: number[]; issues: string[];
  provider: string; externalAccountId: string; accountNumber: string; bik?: string | null; currency: string;
  status: string; isHidden: boolean; connectionStatus: string;
}
export interface CompanyBankMatchReport {
  companyId: number;
  bankConnectionId: number;
  version: string;
  profileIssues: string[];
  groups: CompanyBankMatchGroup[];
  accounts: CompanyBankMatchAccount[];
  canBind: boolean;
  activeBindings: CompanyBankActiveBinding[];
}
export interface CompanyBankBindingReceipt { bindingId: number; state: 'Bound' | 'Revoked'; isReplay: boolean }

const record = (value: unknown): value is Record<string, unknown> => Boolean(value) && typeof value === 'object' && !Array.isArray(value);
const id = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) > 0;
const text = (value: unknown, max: number): value is string => typeof value === 'string' && value.length <= max;
const array = (value: unknown, max: number): value is unknown[] => Array.isArray(value) && value.length <= max;
const ids = (value: unknown, max: number) => array(value, max) && value.every(id) && new Set(value).size === value.length;
const issues = (value: unknown) => array(value, 32) && value.every(x => text(x, 128));
const fail = () => new Error('Не удалось подтвердить сверку выбранной компании. Перезагрузите данные подключений и повторите сверку.');

/** A report is a scoped server snapshot, never authority inferred from a matching label. */
export function parseCompanyBankMatchReport(value: unknown, companyId: number, connectionId: number): CompanyBankMatchReport {
  if (!record(value) || value.companyId !== companyId || value.bankConnectionId !== connectionId
    || !text(value.version, 86) || !/^bank-profile-match-v2:[A-F0-9]{64}$/.test(value.version)
    || typeof value.canBind !== 'boolean' || !issues(value.profileIssues)
    || !array(value.groups, 500) || !array(value.accounts, 500) || !array(value.activeBindings, 628)) throw fail();
  const groupIds = new Map<number, Set<number>>();
  for (const group of value.groups) {
    if (!record(group) || !id(group.id) || groupIds.has(group.id)
      || !text(group.profileMatchStatus, 32) || !['Missing', 'UniqueCandidate', 'Ambiguous'].includes(group.profileMatchStatus)
      || !ids(group.bankAccountIds, 500) || !(group.bankAccountIds as number[]).includes(group.id)
      || !ids(group.manualMoneyAccountCandidateIds, 1000) || typeof group.hasArchivedProfileCandidates !== 'boolean'
      || !array(group.profileCandidates, 128)) throw fail();
    const candidates = new Set<string>();
    for (const candidate of group.profileCandidates) {
      if (!record(candidate) || !text(candidate.id, 16000) || !candidate.id.trim() || candidate.id !== candidate.id.trim()
        || candidates.has(candidate.id) || !text(candidate.name, 200)) throw fail();
      candidates.add(candidate.id);
    }
    if ((group.profileMatchStatus === 'Missing' && candidates.size !== 0)
      || (group.profileMatchStatus === 'UniqueCandidate' && candidates.size !== 1)
      || (group.profileMatchStatus === 'Ambiguous' && candidates.size < 2)) throw fail();
    groupIds.set(group.id, new Set(group.bankAccountIds as number[]));
  }
  const accountIds = new Set<number>();
  for (const account of value.accounts) {
    if (!record(account) || !id(account.bankAccountId) || accountIds.has(account.bankAccountId)
      || !ids(account.linkedMoneyAccountIds, 1000) || !issues(account.issues)
      || !text(account.provider, 16) || !text(account.externalAccountId, 128) || !text(account.accountNumber, 34)
      || (account.bik != null && !text(account.bik, 16)) || !text(account.currency, 8)
      || !text(account.status, 16) || typeof account.isHidden !== 'boolean' || !text(account.connectionStatus, 16)
      || (account.groupId !== null && (!id(account.groupId) || !groupIds.has(account.groupId)))) throw fail();
    if (account.groupId !== null && !groupIds.get(account.groupId as number)?.has(account.bankAccountId)) throw fail();
    accountIds.add(account.bankAccountId);
  }
  const bindingIds = new Set<number>();
  for (const binding of value.activeBindings) {
    if (!record(binding) || !id(binding.id) || bindingIds.has(binding.id) || !id(binding.bankAccountId)
      || (binding.profileAccountId != null && !text(binding.profileAccountId, 16000))
      || !text(binding.accountNumber, 34) || !text(binding.bik, 16) || !text(binding.currency, 8)
      || !text(binding.confirmedAtUtc, 64) || !Number.isFinite(Date.parse(binding.confirmedAtUtc))) throw fail();
    bindingIds.add(binding.id);
  }
  return value as unknown as CompanyBankMatchReport;
}

export function parseCompanyBankBindingReceipt(value: unknown, state: CompanyBankBindingReceipt['state'], bindingId?: number): CompanyBankBindingReceipt {
  if (!record(value) || !id(value.bindingId) || value.state !== state || typeof value.isReplay !== 'boolean'
    || (bindingId !== undefined && value.bindingId !== bindingId)) throw fail();
  return value as unknown as CompanyBankBindingReceipt;
}
