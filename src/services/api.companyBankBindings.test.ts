import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { parseCompanyBankBindingReceipt, parseCompanyBankMatchReport } from './companyBankBindings';
import { bankReportFixture, bankVersion, bindingFixture } from '../test/companyBankBindingFixtures';
import { documentCopyTestSession } from '../test/documentCopySession';

describe('scoped bank binding transport', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { session = documentCopyTestSession(); });
  afterEach(() => { session.restore(); vi.unstubAllGlobals(); });

  it('reads the exact connection with cancellation/no-store and verifies its company', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(bankReportFixture())); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); expect(await apiService.getCompanyBankAccountMatches(5, 11, controller.signal)).toEqual(bankReportFixture());
    const [url, options] = fetch.mock.calls[0]; expect(String(url)).toMatch(/\/bank-connections\/11\/profile-account-matches$/);
    expect(options?.cache).toBe('no-store'); expect(options?.signal).toBe(controller.signal);
    fetch.mockResolvedValueOnce(Response.json(bankReportFixture(6)));
    await expect(apiService.getCompanyBankAccountMatches(5, 11)).rejects.toThrow('сверку выбранной компании');
  });

  it('preserves the full chosen local ID and version; revoke has only its version', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json({ bindingId: 77, state: 'Bound', isReplay: false }))
      .mockResolvedValueOnce(Response.json({ bindingId: 77, state: 'Revoked', isReplay: true })); vi.stubGlobal('fetch', fetch);
    const controller = new AbortController(); const payload = { bankAccountId: 101, profileAccountId: 'Case-Sensitive-' + 'x'.repeat(15000), expectedVersion: bankVersion() };
    await apiService.bindCompanyBankAccount(11, payload, controller.signal); await apiService.revokeCompanyBankAccount(11, 77, bankVersion('B'), controller.signal);
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/11\/profile-account-bindings$/); expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual(payload);
    expect(String(fetch.mock.calls[1][0])).toMatch(/\/11\/profile-account-bindings\/77\/revoke$/); expect(JSON.parse(String(fetch.mock.calls[1][1]?.body))).toEqual({ expectedVersion: bankVersion('B') });
    for (const [, options] of fetch.mock.calls) { expect(options?.method).toBe('POST'); expect(options?.cache).toBe('no-store'); expect(options?.signal).toBe(controller.signal); }
  });

  it.each([204, 202])('does not treat HTTP %s as a committed acknowledgement', async status => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status })));
    await expect(apiService.bindCompanyBankAccount(11, { bankAccountId: 101, profileAccountId: 'profile-A', expectedVersion: bankVersion() })).rejects.toThrow();
  });

  it('cannot adopt an old-session response', async () => {
    let finish!: (response: Response) => void; vi.stubGlobal('fetch', vi.fn().mockReturnValue(new Promise<Response>(resolve => { finish = resolve; })));
    const pending = apiService.getCompanyBankAccountMatches(5, 11); session.replace(); finish(Response.json(bankReportFixture())); await expect(pending).rejects.toThrow();
  });

  it.each(['owner', 'connection', 'v1', 'duplicate-account', 'duplicate-group', 'candidate-id', 'duplicate-candidate', 'group-status-array', 'cardinality', 'group-reference', 'unrelated-group-bank', 'duplicate-binding', 'bad-date', 'too-many-accounts', 'too-long-id', 'missing-source', 'oversized-source', 'invalid-hidden'] as const)('rejects a malformed %s report', mode => {
    const value = bankReportFixture(); value.activeBindings = [bindingFixture()];
    switch (mode) {
      case 'owner': value.companyId++; break; case 'connection': value.bankConnectionId++; break;
      case 'v1': value.version = value.version.replace('-v2:', '-v1:'); break;
      case 'duplicate-account': value.accounts.push(value.accounts[0]); break;
      case 'duplicate-group': value.groups.push(value.groups[0]); break;
      case 'candidate-id': value.groups[0].profileCandidates[0].id = ' '; break;
      case 'duplicate-candidate': value.groups[0].profileCandidates.push(value.groups[0].profileCandidates[0]); break;
      case 'group-status-array': Object.assign(value.groups[0], { profileMatchStatus: ['UniqueCandidate'] }); break;
      case 'cardinality': value.groups[0].profileMatchStatus = 'Ambiguous'; break;
      case 'group-reference': value.accounts[0].groupId = 999; break;
      case 'unrelated-group-bank': value.accounts[0].bankAccountId = 999; break;
      case 'duplicate-binding': value.activeBindings.push(value.activeBindings[0]); break;
      case 'bad-date': value.activeBindings[0].confirmedAtUtc = 'not-a-date'; break;
      case 'too-many-accounts': value.accounts = Array.from({ length: 501 }, (_, i) => ({ ...value.accounts[0], bankAccountId: i + 1 })); break;
      case 'too-long-id': value.groups[0].profileCandidates[0].id = 'x'.repeat(16001); break;
      case 'missing-source': Object.assign(value.accounts[0], { accountNumber: undefined }); break;
      case 'oversized-source': value.accounts[0].externalAccountId = 'x'.repeat(129); break;
      case 'invalid-hidden': Object.assign(value.accounts[0], { isHidden: 'false' }); break;
    }
    expect(() => parseCompanyBankMatchReport(value, 5, 11)).toThrow('сверку выбранной компании');
  });

  it.each([null, undefined])('accepts cleanup with old profile ID %s and a bank absent from the current connection', profileAccountId => {
    const value = bankReportFixture(); value.canBind = false; value.groups = []; value.accounts = []; value.profileIssues = ['CompanyUnavailable', 'CompanyNotOwn'];
    value.activeBindings = [{ ...bindingFixture(), profileAccountId, bankAccountId: 999 }];
    const wire = JSON.parse(JSON.stringify(value));
    expect(parseCompanyBankMatchReport(wire, 5, 11)).toEqual(wire);
  });

  it.each([{ bindingId: 0, state: 'Bound', isReplay: false }, { bindingId: 77, state: 'Revoked', isReplay: false }, { bindingId: 77, state: 'Bound', isReplay: 'false' }])('rejects an invalid receipt %j', value => {
    expect(() => parseCompanyBankBindingReceipt(value, 'Bound')).toThrow();
  });
  it('rejects revocation for another confirmation ID', () => expect(() => parseCompanyBankBindingReceipt({ bindingId: 78, state: 'Revoked', isReplay: false }, 'Revoked', 77)).toThrow());
});
