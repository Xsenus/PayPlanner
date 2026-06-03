import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../services/api';
import { useUserTimeZone } from './useUserTimeZone';

vi.mock('../services/api', () => ({
  apiService: {
    getUserSetting: vi.fn(),
  },
}));

function TimeZoneProbe() {
  const timeZoneId = useUserTimeZone();
  return <div data-testid="time-zone">{timeZoneId}</div>;
}

describe('useUserTimeZone', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('loads the user profile time zone setting', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(screen.getByTestId('time-zone').textContent).toBe('Europe/Samara');
    });
  });

  it('keeps a normalized fallback when the setting cannot be loaded', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('not found'));

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId');
    });
    expect(screen.getByTestId('time-zone').textContent).toBeTruthy();
  });
});
