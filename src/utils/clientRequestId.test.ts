import { afterEach, describe, expect, it, vi } from 'vitest';
import { createClientRequestId } from './clientRequestId';

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('secure request IDs on browsers from 2022', () => {
  it('preserves native UUID and the crypto receiver', () => {
    const source = { randomUUID: vi.fn(function(this: unknown) {
      expect(this).toBe(source);
      return '319c433e-7dcb-46eb-bc7b-32b4f6fc8333';
    }) };
    vi.stubGlobal('crypto', source);
    expect(createClientRequestId()).toBe('319c433e-7dcb-46eb-bc7b-32b4f6fc8333');
    expect(source.randomUUID).toHaveBeenCalledTimes(1);
  });
  it.each([0, 255])('uses secure bytes and sets UUIDv4/variant bits for byte %s', byte => {
    const source = { getRandomValues: vi.fn(function(this: unknown, bytes: Uint8Array) {
      expect(this).toBe(source);
      expect(bytes).toHaveLength(16);
      return bytes.fill(byte);
    }) };
    vi.stubGlobal('crypto', source);
    const expected = byte === 0 ? '00000000-0000-4000-8000-000000000000' : 'ffffffff-ffff-4fff-bfff-ffffffffffff';
    expect(createClientRequestId()).toBe(expected);
    expect(source.getRandomValues).toHaveBeenCalledTimes(1);
  });
  it('generates distinct keys from native secure random bytes without randomUUID', () => {
    const source = globalThis.crypto;
    vi.stubGlobal('crypto', { getRandomValues: source.getRandomValues.bind(source) });
    const keys = Array.from({ length: 100 }, () => createClientRequestId());
    expect(new Set(keys).size).toBe(100);
    for (const key of keys) expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  });
  it.each([undefined, {}])('rejects unavailable crypto instead of using Math.random: %j', source => {
    const weak = vi.spyOn(Math, 'random');
    vi.stubGlobal('crypto', source);
    expect(createClientRequestId).toThrow('безопасное создание');
    expect(weak).not.toHaveBeenCalled();
  });
  it('preserves secure generator failure without producing a weak fallback ID', () => {
    const failure = new Error('secure generator failed');
    vi.stubGlobal('crypto', { getRandomValues: () => { throw failure; } });
    expect(createClientRequestId).toThrow(failure);
  });
});
