import type { ContactDirectoryItemDto } from '../services/api';

export type ContactDuplicateGroup = {
  contacts: ContactDirectoryItemDto[];
  matches: string[];
};

export function normalizeContactPhone(value: string): string | null {
  let digits = value.replace(/\D/g, '');
  if (digits.length === 11 && digits[0] === '8') digits = `7${digits.slice(1)}`;
  if (digits.length < 10 || /(\d)\1{6}/.test(digits)) return null;
  return digits;
}

export function normalizeContactEmail(value: string): string | null {
  const email = value.trim().toLowerCase();
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) return null;
  if (/^(noemail|test|example|unknown|нет|безпочты)@/.test(email)) return null;
  return email;
}

export function findContactDuplicateGroups(contacts: ContactDirectoryItemDto[]): ContactDuplicateGroup[] {
  const activeIds = new Set(contacts.filter((contact) => contact.isActive && !contact.auditIsDeleted)
    .map((contact) => `${contact.sourceType}:${contact.sourceId}`));
  const active = contacts.filter((contact) => contact.isActive && !contact.auditIsDeleted
    && !(contact.sourceType === 'external' && (
      (contact.linkedClientId && activeIds.has(`client:${contact.linkedClientId}`))
      || (contact.linkedLegalEntityId && activeIds.has(`legal:${contact.linkedLegalEntityId}`))
    )));
  const parent = active.map((_, index) => index);
  const find = (index: number): number => {
    while (parent[index] !== index) {
      parent[index] = parent[parent[index]];
      index = parent[index];
    }
    return index;
  };
  const tokens = new Map<string, number[]>();

  active.forEach((contact, index) => {
    const kind = contact.contactType === 'Organization' ? 'organization' : 'person';
    const phones = contact.phones.length > 0 ? contact.phones : contact.phone ? [contact.phone] : [];
    const emails = contact.emails.length > 0 ? contact.emails : contact.email ? [contact.email] : [];
    const keys = new Set<string>();
    phones.forEach((phone) => {
      const normalized = normalizeContactPhone(phone);
      if (normalized) keys.add(`${kind}:телефон ${normalized}`);
    });
    emails.forEach((email) => {
      const normalized = normalizeContactEmail(email);
      if (normalized) keys.add(`${kind}:email ${normalized}`);
    });
    keys.forEach((key) => {
      const indexes = tokens.get(key) ?? [];
      indexes.push(index);
      tokens.set(key, indexes);
    });
  });

  for (const indexes of tokens.values()) {
    if (indexes.length < 2 || indexes.length > 20) continue;
    const first = indexes[0];
    indexes.slice(1).forEach((index) => { parent[find(index)] = find(first); });
  }

  const grouped = new Map<number, ContactDuplicateGroup>();
  active.forEach((contact, index) => {
    const root = find(index);
    const group = grouped.get(root) ?? { contacts: [], matches: [] };
    group.contacts.push(contact);
    grouped.set(root, group);
  });
  for (const [token, indexes] of tokens) {
    if (indexes.length < 2 || indexes.length > 20) continue;
    const group = grouped.get(find(indexes[0]));
    if (group) group.matches.push(token.split(':').slice(1).join(':'));
  }

  return [...grouped.values()]
    .filter((group) => group.contacts.length > 1)
    .map((group) => ({
      contacts: group.contacts.sort((a, b) => a.fullName.localeCompare(b.fullName, 'ru')),
      matches: [...new Set(group.matches)].sort(),
    }))
    .sort((a, b) => b.contacts.length - a.contacts.length || a.contacts[0].fullName.localeCompare(b.contacts[0].fullName, 'ru'));
}
