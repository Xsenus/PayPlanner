import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { FeatureFlagSettings } from './FeatureFlagSettings';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('FeatureFlagSettings', () => {
  beforeEach(() => {
    resetApiServiceMock('getFeatureFlags');
  });

  it('shows legal feature flag states', async () => {
    apiServiceMock.getFeatureFlags.mockResolvedValue({
      legal: {
        externalCourtSync: false,
        externalCalendarSync: true,
        courtEvents: false,
        documentTemplateLibrary: true,
        externalWorkOrderApproval: false,
        clientPortal: false,
        offlineCalendarReadOnly: false,
      },
    });

    render(<FeatureFlagSettings />);

    expect(await screen.findByText('Юридический контур')).toBeTruthy();
    expect(screen.getByText('Google/Яндекс календарь')).toBeTruthy();
    expect(screen.getByText('Библиотека шаблонов документов')).toBeTruthy();
    expect(screen.getByText('Внешнее согласование заказ-нарядов')).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText('Активно: 2 из 7. Источник: backend `FeatureFlags:Legal`.')).toBeTruthy();
    });
  });

  it('shows a fallback error when flags cannot be loaded', async () => {
    apiServiceMock.getFeatureFlags.mockRejectedValue(new Error('network down'));

    render(<FeatureFlagSettings />);

    expect(await screen.findByText(/network down/i)).toBeTruthy();
    expect(screen.getByText('Активно: 0 из 7. Источник: backend `FeatureFlags:Legal`.')).toBeTruthy();
  });
});
