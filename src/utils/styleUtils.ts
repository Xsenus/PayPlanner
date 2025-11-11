import type { CSSProperties } from 'react';

const HEX_COLOR_REGEXP = /^#([0-9a-fA-F]{6})$/;

export function buildStatusBadgeStyle(colorHex?: string): CSSProperties | undefined {
  if (!colorHex || !HEX_COLOR_REGEXP.test(colorHex)) {
    return undefined;
  }

  const r = parseInt(colorHex.slice(1, 3), 16);
  const g = parseInt(colorHex.slice(3, 5), 16);
  const b = parseInt(colorHex.slice(5, 7), 16);

  return {
    color: colorHex,
    borderColor: `rgba(${r}, ${g}, ${b}, 0.4)`,
    backgroundColor: `rgba(${r}, ${g}, ${b}, 0.12)`,
  };
}
