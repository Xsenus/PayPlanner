import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppearanceSettings } from './AppearanceSettings';
import { APP_THEME_OPTIONS } from '../../utils/appThemes';

const themeMock = vi.hoisted(() => ({
  themeKey: 'standard' as const,
  setGlobalTheme: vi.fn(),
}));

vi.mock('../../contexts/AppThemeContext', () => ({
  useAppTheme: () => ({
    themeKey: themeMock.themeKey,
    themes: APP_THEME_OPTIONS,
    loading: false,
    saving: false,
    setGlobalTheme: themeMock.setGlobalTheme,
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn() }),
}));

describe('AppearanceSettings', () => {
  beforeEach(() => {
    themeMock.setGlobalTheme.mockReset().mockResolvedValue(undefined);
  });

  it('shows all themes and saves the administrator selection globally', async () => {
    render(<AppearanceSettings />);

    expect(screen.getAllByRole('button', { pressed: false })).toHaveLength(7);
    fireEvent.click(screen.getByRole('button', { name: /Графит/i }));
    fireEvent.click(screen.getByRole('button', { name: /Применить для всех/i }));

    await waitFor(() => expect(themeMock.setGlobalTheme).toHaveBeenCalledWith('graphite'));
  });
});
