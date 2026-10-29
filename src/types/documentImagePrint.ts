export interface DocumentImagePrintOptions {
  includeLogo: boolean;
  includeSeal: boolean;
  includeSignature: boolean;
}

export interface CompanyDocumentImagePrintDefaults {
  invoice: DocumentImagePrintOptions | null;
  act: DocumentImagePrintOptions | null;
  contract: DocumentImagePrintOptions | null;
}
