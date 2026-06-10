import { vi, type Mock } from 'vitest';

import type { ApiService } from '../services/api';

export type ApiServiceMock = {
  [K in keyof ApiService]: Mock;
};

const mocks = new Map<PropertyKey, Mock>();

function ensureApiMock(property: PropertyKey): Mock {
  let mock = mocks.get(property);
  if (!mock) {
    mock = vi.fn();
    mocks.set(property, mock);
  }

  return mock;
}

export const apiServiceMock = new Proxy(Object.create(null) as Record<PropertyKey, Mock>, {
  get(_target, property) {
    if (property === 'then') {
      return undefined;
    }

    return ensureApiMock(property);
  },
  has(_target, property) {
    return mocks.has(property);
  },
  ownKeys() {
    return Array.from(mocks.keys()).filter((key): key is string | symbol => typeof key !== 'number');
  },
  getOwnPropertyDescriptor(_target, property) {
    if (!mocks.has(property)) {
      return undefined;
    }

    return {
      configurable: true,
      enumerable: true,
    };
  },
}) as ApiServiceMock;

export function apiMockMethod<K extends keyof ApiService>(method: K): Mock {
  return ensureApiMock(method);
}

export function resetApiServiceMock(...methods: Array<keyof ApiService>) {
  if (methods.length === 0) {
    mocks.forEach((mock) => mock.mockReset());
    return;
  }

  methods.forEach((method) => ensureApiMock(method).mockReset());
}

export function clearApiServiceMock(...methods: Array<keyof ApiService>) {
  if (methods.length === 0) {
    mocks.forEach((mock) => mock.mockClear());
    return;
  }

  methods.forEach((method) => ensureApiMock(method).mockClear());
}
