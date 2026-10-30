export function documentCopyTestSession() {
  const previousToken = localStorage.getItem('pp_token');
  const previousEpoch = localStorage.getItem('pp.auth-session-epoch.v1');
  const token = (actor: string, extra = '') => `synthetic.${btoa(JSON.stringify({ iss: 'document-copy-tests', sub: actor, extra }))}.signature`;
  localStorage.setItem('pp_token', token('7'));
  localStorage.setItem('pp.auth-session-epoch.v1', 'copy-first');
  return {
    replace(mode: 'login' | 'actor' | 'logout' = 'login') {
      if (mode === 'login') localStorage.setItem('pp.auth-session-epoch.v1', 'copy-second');
      if (mode === 'actor') localStorage.setItem('pp_token', token('8'));
      if (mode === 'logout') localStorage.removeItem('pp_token');
    },
    refresh() { localStorage.setItem('pp_token', token('7', 'refreshed')); },
    restore() {
      if (previousToken === null) localStorage.removeItem('pp_token'); else localStorage.setItem('pp_token', previousToken);
      if (previousEpoch === null) localStorage.removeItem('pp.auth-session-epoch.v1'); else localStorage.setItem('pp.auth-session-epoch.v1', previousEpoch);
    },
  };
}
