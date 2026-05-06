import type { Client } from '../../types';
import type { LookupSelectOption } from '../ui/LookupSelect';

function compactUnique(parts: Array<string | null | undefined>) {
  const seen = new Set<string>();

  return parts
    .map((part) => part?.trim())
    .filter((part): part is string => Boolean(part))
    .filter((part) => {
      const key = part.toLocaleLowerCase('ru');
      if (seen.has(key)) {
        return false;
      }

      seen.add(key);
      return true;
    });
}

export function buildClientLookupOptions(clients: Client[]): LookupSelectOption[] {
  return clients
    .filter((client) => client?.id && client?.name)
    .map((client) => {
      const legalEntityName = client.legalEntity?.shortName || client.legalEntity?.fullName || null;
      const description = compactUnique([
        client.company,
        legalEntityName ? `Юрлицо: ${legalEntityName}` : null,
        client.phone,
        client.email,
      ]).join(' / ');

      return {
        value: String(client.id),
        label: client.name ?? `#${client.id}`,
        description: description || undefined,
        keywords: compactUnique([
          client.company,
          client.phone,
          client.email,
          client.address,
          legalEntityName,
          client.legalEntity?.inn,
        ]),
      };
    })
    .sort((left, right) => left.label.localeCompare(right.label, 'ru'));
}
