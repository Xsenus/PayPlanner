import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const root = process.cwd();
const mockupRoot = path.join(root, 'docs/yougile-sources/mockups');
const manifest = JSON.parse(fs.readFileSync(path.join(mockupRoot, 'manifest.json'), 'utf8'));
const requiredCards = [19,20,21,22,23,24,25,26,27,28,29,32].map(id => `PAY-${id}`);
if (manifest.length !== 18 || requiredCards.some(card => !manifest.some(m => m.card === card))) throw new Error('Incomplete mockup inventory');
for (const row of manifest) {
  const pixels = fs.readFileSync(path.join(mockupRoot, row.repositoryFile));
  if (pixels.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a') throw new Error(`Not PNG: ${row.repositoryFile}`);
  if (pixels.readUInt32BE(16) !== row.width || pixels.readUInt32BE(20) !== row.height) throw new Error(`Resized: ${row.repositoryFile}`);
  if (crypto.createHash('sha256').update(pixels).digest('hex') !== row.repositorySha256) throw new Error(`Hash mismatch: ${row.repositoryFile}`);
}
const maps = [
  'docs/action-map-pay-19-journal-2026-10-12.md',
  'docs/action-map-pay-20-task-cards-2026-10-12.md',
  'docs/action-map-pay-32-individual-client-2026-10-12.md',
];
const documents = [...maps, 'docs/dev-environment-claude-2026-10-12.md', 'docs/owner-decisions-2026-10-12.md',
  'docs/codex-preparation-report-2026-10-12.md', 'docs/yougile-sources/mockups/README.md'];
let links = 0;
const lineCounts = new Map();
function lineCount(filename) {
  if (!lineCounts.has(filename)) lineCounts.set(filename, fs.readFileSync(filename, 'utf8').split('\n').length);
  return lineCounts.get(filename);
}
for (const document of documents) {
  const content = fs.readFileSync(path.join(root, document), 'utf8');
  for (const match of content.matchAll(/\[[^\]\n]*\]\(([^)\n]+)\)/g)) {
    const target = match[1];
    if (/^https?:|^#/.test(target)) continue;
    const [filename, fragment] = target.split('#');
    const resolved = path.resolve(root, path.dirname(document), filename);
    if (!fs.existsSync(resolved)) throw new Error(`Missing link in ${document}: ${target}`);
    if (fragment?.match(/^L\d+$/) && Number(fragment.slice(1)) > lineCount(resolved)) throw new Error(`Invalid line: ${target}`);
    links++;
  }
}
const inventory = JSON.parse(fs.readFileSync('docs/yougile-sources/action-inventory/2026-10-12.json', 'utf8'));
for (const row of [...inventory.rows, ...inventory.schemas]) {
  if (row.line < 1 || row.line > lineCount(path.resolve(root, row.file))) throw new Error(`Invalid source position: ${row.file}:${row.line}`);
}
console.log(`Verified: ${requiredCards.length} cards, ${manifest.length} original-size PNGs, ${links} local links, ${inventory.rows.length} JSX declarations, ${inventory.schemas.length} model fields.`);
