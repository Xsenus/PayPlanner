import { beforeEach, describe, expect, it } from 'vitest';

import {
  getStoredCompanyProfileCompanyId,
  getStoredCompanyProfileMode,
  setStoredCompanyProfileCompanyId,
  setStoredCompanyProfileMode,
} from './companyProfileBridge';

describe('companyProfileBridge', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('stores and restores selected company id', () => {
    setStoredCompanyProfileCompanyId(17);
    expect(getStoredCompanyProfileCompanyId()).toBe('17');

    setStoredCompanyProfileCompanyId(null);
    expect(getStoredCompanyProfileCompanyId()).toBeNull();
  });

  it('stores and restores company profile mode', () => {
    setStoredCompanyProfileMode('edit');
    expect(getStoredCompanyProfileMode()).toBe('edit');

    setStoredCompanyProfileMode('view');
    expect(getStoredCompanyProfileMode()).toBe('view');

    setStoredCompanyProfileMode(null);
    expect(getStoredCompanyProfileMode()).toBeNull();
  });
});
