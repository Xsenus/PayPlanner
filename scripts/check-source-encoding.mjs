import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { extname } from 'node:path';

const textExtensions = new Set([
  '.cs', '.css', '.html', '.js', '.json', '.jsx', '.md', '.mjs', '.ps1',
  '.sh', '.sql', '.ts', '.tsx', '.txt', '.yaml', '.yml',
]);
const utf8Decoder = new TextDecoder('utf-8', { fatal: true });
const windows1251Decoder = new TextDecoder('windows-1251');
const windows1251Bytes = buildWindows1251ReverseMap();

export function findEncodingIssues(fileName, content) {
  const issues = [];
  let text;
  try {
    text = utf8Decoder.decode(content);
  } catch (error) {
    return [{ fileName, line: 0, kind: 'invalid-utf8', detail: error.message }];
  }

  for (const [index, line] of text.split(/\r?\n/u).entries()) {
    if (line.includes('\uFFFD')) {
      issues.push({ fileName, line: index + 1, kind: 'replacement-character', detail: line.trim() });
      continue;
    }
    if (/[\u0080-\u009F]/u.test(line)) {
      issues.push({ fileName, line: index + 1, kind: 'c1-control-character', detail: line.trim() });
      continue;
    }

    const recovered = tryRecoverMojibake(line);
    if (recovered !== null && recovered !== line) {
      issues.push({
        fileName,
        line: index + 1,
        kind: 'recoverable-mojibake',
        detail: `${line.trim()} -> ${recovered.trim()}`,
      });
    }
  }

  return issues;
}

function tryRecoverMojibake(value) {
  if (!value.includes('Р') && !value.includes('С')) return null;

  const bytes = [];
  for (const character of value) {
    const byte = windows1251Bytes.get(character);
    if (byte === undefined) return null;
    bytes.push(byte);
  }

  try {
    const recovered = utf8Decoder.decode(Uint8Array.from(bytes));
    const originalMarkers = countMojibakeMarkers(value);
    const recoveredMarkers = countMojibakeMarkers(recovered);
    return recoveredMarkers < originalMarkers ? recovered : null;
  } catch {
    return null;
  }
}

function countMojibakeMarkers(value) {
  return [...value].reduce((count, character) => count + (character === 'Р' || character === 'С' ? 1 : 0), 0);
}

function buildWindows1251ReverseMap() {
  const map = new Map();
  for (let byte = 0; byte <= 255; byte += 1) {
    const character = windows1251Decoder.decode(Uint8Array.of(byte));
    if (character !== '\uFFFD' && !map.has(character)) map.set(character, byte);
  }
  return map;
}

function getTrackedTextFiles() {
  const output = execFileSync('git', ['ls-files', '-z']);
  return output.toString('utf8')
    .split('\0')
    .filter(Boolean)
    .filter((fileName) => textExtensions.has(extname(fileName).toLowerCase()));
}

function runSelfTest() {
  const original = 'Готовая задача';
  const corrupted = windows1251Decoder.decode(new TextEncoder().encode(original));
  const corruptedIssues = findEncodingIssues('corrupted.txt', new TextEncoder().encode(corrupted));
  const validIssues = findEncodingIssues('valid.txt', new TextEncoder().encode('Россия: готовая задача'));
  const controlIssues = findEncodingIssues(
    'control.txt',
    new TextEncoder().encode(`До${String.fromCharCode(0x80)}После`),
  );

  if (!corruptedIssues.some((issue) => issue.kind === 'recoverable-mojibake')) {
    throw new Error('Self-test did not detect recoverable mojibake.');
  }
  if (validIssues.length !== 0) {
    throw new Error(`Self-test rejected valid UTF-8: ${JSON.stringify(validIssues)}`);
  }
  if (!controlIssues.some((issue) => issue.kind === 'c1-control-character')) {
    throw new Error('Self-test did not detect a C1 control character.');
  }
  console.log('Source encoding audit self-test passed.');
}

function runAudit() {
  const files = getTrackedTextFiles();
  const issues = files.flatMap((fileName) => findEncodingIssues(fileName, readFileSync(fileName)));

  if (issues.length > 0) {
    for (const issue of issues) {
      console.error(`${issue.fileName}:${issue.line} [${issue.kind}] ${issue.detail}`);
    }
    console.error(`Source encoding audit failed: ${issues.length} issue(s).`);
    process.exitCode = 1;
    return;
  }

  console.log(`Source encoding audit passed: ${files.length} tracked text files.`);
}

if (process.argv.includes('--self-test')) runSelfTest();
else runAudit();
