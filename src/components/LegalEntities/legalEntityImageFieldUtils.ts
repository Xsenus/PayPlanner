export interface LegalEntityImageFieldStateInput {
  value: string;
  persistedValue: string;
  changed: boolean;
  readOnly: boolean;
}

export interface LegalEntityImageFieldState {
  hasImage: boolean;
  removalPending: boolean;
  canUpload: boolean;
  canRemove: boolean;
  showReplaceHint: boolean;
  showMissingPlaceholder: boolean;
}

export function getLegalEntityImageFieldState({
  value,
  persistedValue,
  changed,
  readOnly,
}: LegalEntityImageFieldStateInput): LegalEntityImageFieldState {
  const hasImage = value.trim().length > 0;
  const removalPending = !hasImage && changed && persistedValue.trim().length > 0;

  return {
    hasImage,
    removalPending,
    canUpload: !readOnly && !hasImage,
    canRemove: !readOnly && hasImage,
    showReplaceHint: !readOnly && hasImage,
    showMissingPlaceholder: readOnly && !hasImage,
  };
}
