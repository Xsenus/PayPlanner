// The browser imports the same production modules as the application. Keep
// this entry stable across scenarios; no test data, endpoint or credentials.
export { beginCaseDocumentUpload } from '../src/services/caseDocumentUploadAttempts.ts';
export { apiService, getAuthToken } from '../src/services/api.ts';
