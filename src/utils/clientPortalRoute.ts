export function getClientPortalChallengeId(pathname = window.location.pathname) {
  return pathname.match(/^\/portal\/login\/([0-9a-f-]{36})\/?$/i)?.[1] ?? '';
}

export function isClientPortalPath(pathname = window.location.pathname) {
  return /^\/portal(?:\/login\/[0-9a-f-]{36})?\/?$/i.test(pathname);
}
