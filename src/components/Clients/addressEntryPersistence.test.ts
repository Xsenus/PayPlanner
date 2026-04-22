import { describe, expect, it } from "vitest";

import {
  ensureSinglePrimaryAddressEntryList,
  setPrimaryAddressEntry,
  stripAddressEntryValidity,
  stripAddressEntryValidityList,
} from "./addressEntryPersistence";

describe("addressEntryPersistence", () => {
  it("clears hidden validity dates before persistence", () => {
    expect(
      stripAddressEntryValidity({
        type: "registration",
        validFrom: "2024-01-01",
        validTo: "2024-12-31",
      }),
    ).toEqual({
      type: "registration",
      validFrom: "",
      validTo: "",
    });
  });

  it("clears validity dates for every address entry in the saved payload", () => {
    expect(
      stripAddressEntryValidityList([
        {
          type: "registration",
          validFrom: "2024-01-01",
          validTo: "2024-12-31",
        },
        {
          type: "actual",
          validFrom: "2025-01-01",
          validTo: "",
        },
      ]),
    ).toEqual([
      {
        type: "registration",
        validFrom: "",
        validTo: "",
      },
      {
        type: "actual",
        validFrom: "",
        validTo: "",
      },
    ]);
  });

  it("keeps validity dates when the feature is turned back on", () => {
    expect(
      stripAddressEntryValidity(
        {
          type: "registration",
          validFrom: "2024-01-01",
          validTo: "2024-12-31",
        },
        true,
      ),
    ).toEqual({
      type: "registration",
      validFrom: "2024-01-01",
      validTo: "2024-12-31",
    });
  });

  it("keeps a single explicit primary address and promotes the first one when needed", () => {
    expect(
      ensureSinglePrimaryAddressEntryList([
        { isPrimary: false, type: "registration" },
        { isPrimary: true, type: "actual" },
        { isPrimary: true, type: "legal" },
      ]),
    ).toEqual([
      { isPrimary: false, type: "registration" },
      { isPrimary: true, type: "actual" },
      { isPrimary: false, type: "legal" },
    ]);

    expect(
      ensureSinglePrimaryAddressEntryList([
        { isPrimary: false, type: "registration" },
        { isPrimary: false, type: "actual" },
      ]),
    ).toEqual([
      { isPrimary: true, type: "registration" },
      { isPrimary: false, type: "actual" },
    ]);
  });

  it("switches the primary address to the selected row", () => {
    expect(
      setPrimaryAddressEntry(
        [
          { isPrimary: true, type: "registration" },
          { isPrimary: false, type: "actual" },
        ],
        1,
      ),
    ).toEqual([
      { isPrimary: false, type: "registration" },
      { isPrimary: true, type: "actual" },
    ]);
  });
});
