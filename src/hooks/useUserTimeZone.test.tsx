import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../services/api';
import { useUserTimeZone } from './useUserTimeZone';

vi.mock('../services/api', () => ({
  apiService: {
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
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
    expect(apiService.upsertUserSetting).not.toHaveBeenCalled();
  });

  it('saves the detected time zone when the profile setting is missing', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('HTTP 404 Not Found'));
    vi.mocked(apiService.upsertUserSetting).mockResolvedValue({
      key: 'profile.timeZoneId',
      value: 'Europe/Samara',
      updatedAt: '2026-06-05T10:00:00Z',
    });

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId');
    });
    await waitFor(() => {
      expect(apiService.upsertUserSetting).toHaveBeenCalledWith(
        'profile.timeZoneId',
        screen.getByTestId('time-zone').textContent,
      );
    });
    expect(screen.getByTestId('time-zone').textContent).toBeTruthy();
  });

  it('keeps a normalized fallback without saving when the setting cannot be loaded for another reason', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('HTTP 500 Server Error'));

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId');
    });
    expect(apiService.upsertUserSetting).not.toHaveBeenCalled();
    expect(screen.getByTestId('time-zone').textContent).toBeTruthy();
  });
});
