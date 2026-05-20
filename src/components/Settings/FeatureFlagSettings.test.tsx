import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { FeatureFlagSettings } from './FeatureFlagSettings';

vi.mock('../../services/api', () => ({
  apiService: {
    getFeatureFlags: vi.fn(),
  },
}));

describe('FeatureFlagSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getFeatureFlags).mockReset();
  });

  it('shows legal feature flag states', async () => {
    vi.mocked(apiService.getFeatureFlags).mockResolvedValue({
      legal: {
        externalCourtSync: false,
        externalCalendarSync: true,
        courtEvents: false,
        documentTemplateLibrary: true,
      },
    });

    render(<FeatureFlagSettings />);

    expect(await screen.findByText('Юридический контур')).toBeTruthy();
    expect(screen.getByText('Google/Яндекс календарь')).toBeTruthy();
    expect(screen.getByText('Библиотека шаблонов документов')).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText('Активно: 2 из 4. Источник: backend `FeatureFlags:Legal`.')).toBeTruthy();
    });
  });

  it('shows a fallback error when flags cannot be loaded', async () => {
    vi.mocked(apiService.getFeatureFlags).mockRejectedValue(new Error('network down'));

    render(<FeatureFlagSettings />);

    expect(await screen.findByText(/network down/i)).toBeTruthy();
    expect(screen.getByText('Активно: 0 из 4. Источник: backend `FeatureFlags:Legal`.')).toBeTruthy();
  });
});
