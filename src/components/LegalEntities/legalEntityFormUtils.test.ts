import { describe, expect, it } from "vitest";

import {
  buildLegalEntityAddressKey,
  dedupeLegalEntityAddressInputs,
  mergeLegalEntityAddressEntry,
  upsertLegalEntityAddressEntry,
} from "./legalEntityFormUtils";

describe("legalEntityFormUtils", () => {
  it("matches equivalent addresses with spacing and punctuation differences", () => {
    const firstKey = buildLegalEntityAddressKey({
      type: "legal",
      fullAddress: "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
      postalCode: "446200",
      city: "г Новокуйбышевск",
      street: "ул Миронова",
      house: "9",
    });

    const secondKey = buildLegalEntityAddressKey({
      type: "legal",
      fullAddress: "  Самарская обл г Новокуйбышевск ул Миронова дом 9 ",
      postalCode: "446200",
      city: "Новокуйбышевск",
      street: "Миронова",
      house: "9",
    });

    expect(firstKey).toBe(secondKey);
  });

  it("upserts an existing address instead of appending a duplicate", () => {
    const result = upsertLegalEntityAddressEntry(
      [
        {
          type: "legal",
          isPrimary: true,
          additionalTypes: [],
          fullAddress: "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
          postalCode: "",
          region: "",
          district: "",
          city: "",
          settlement: "",
          street: "",
          house: "",
          flat: "",
          validFrom: "",
          validTo: "",
        },
      ],
      {
        type: "legal",
        isPrimary: false,
        additionalTypes: [],
        fullAddress:
          "446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
        postalCode: "446200",
        region: "Самарская обл.",
        district: "",
        city: "г. Новокуйбышевск",
        settlement: "",
        street: "ул. Миронова",
        house: "9",
        flat: "",
        validFrom: "",
        validTo: "",
      },
    );

    expect(result).toHaveLength(1);
    expect(result[0].postalCode).toBe("446200");
    expect(result[0].city).toContain("Новокуйбышевск");
  });

  it("deduplicates payload addresses and keeps a single primary record", () => {
    const deduped = dedupeLegalEntityAddressInputs([
      {
        type: 3,
        value: "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
        unrestrictedValue:
          "446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
        postalCode: "446200",
        cityWithType: "г Новокуйбышевск",
        streetWithType: "ул Миронова",
        house: "9",
        isPrimary: true,
      },
      {
        type: 3,
        value: "446200 Самарская обл г Новокуйбышевск ул Миронова дом 9",
        unrestrictedValue:
          "446200 Самарская обл г Новокуйбышевск ул Миронова дом 9",
        postalCode: "446200",
        cityWithType: "Новокуйбышевск",
        streetWithType: "Миронова",
        house: "9",
        isPrimary: false,
      },
    ]);

    expect(deduped).toHaveLength(1);
    expect(deduped[0].isPrimary).toBe(true);
  });

  it("keeps the fuller legal entity address when merging a shorter normalized variant", () => {
    const merged = mergeLegalEntityAddressEntry(
      {
        type: "legal",
        isPrimary: true,
        additionalTypes: [],
        fullAddress:
          "633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19",
        postalCode: "633208",
        country: "Россия",
        region: "Новосибирская обл",
        district: "",
        city: "г Искитим",
        settlement: "",
        street: "Южный мкр",
        house: "37",
        flat: "19",
        fiasId: "fias-1",
        kladrId: "kladr-1",
        geoLat: "",
        geoLon: "",
        validFrom: "",
        validTo: "",
      },
      {
        fullAddress: "633208, Новосибирская обл, г Искитим",
        postalCode: "633208",
        region: "Новосибирская обл",
        city: "г Искитим",
      },
    );

    expect(merged.fullAddress).toBe(
      "633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19",
    );
    expect(merged.fiasId).toBe("fias-1");
  });

  it("uses district and settlement when deduplicating legal entity address payloads", () => {
    const deduped = dedupeLegalEntityAddressInputs([
      {
        type: 3,
        value:
          "633208, Россия, Новосибирская обл, Искитимский р-н, г Искитим, Южный мкр, д 37, кв 19",
        unrestrictedValue:
          "633208, Россия, Новосибирская обл, Искитимский р-н, г Искитим, Южный мкр, д 37, кв 19",
        postalCode: "633208",
        country: "Россия",
        regionWithType: "Новосибирская обл",
        areaWithType: "Искитимский р-н",
        cityWithType: "г Искитим",
        settlementWithType: "Южный мкр",
        streetWithType: "Южный мкр",
        house: "37",
        flat: "19",
        isPrimary: true,
      },
      {
        type: 3,
        value: "Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19",
        unrestrictedValue:
          "Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19",
        postalCode: "633208",
        country: "Россия",
        regionWithType: "Новосибирская обл",
        areaWithType: "Искитимский р-н",
        cityWithType: "г Искитим",
        settlementWithType: "Южный мкр",
        streetWithType: "Южный мкр",
        house: "37",
        flat: "19",
        isPrimary: false,
      },
    ]);

    expect(deduped).toHaveLength(1);
    expect(deduped[0].areaWithType).toBe("Искитимский р-н");
    expect(deduped[0].settlementWithType).toBe("Южный мкр");
  });
});
