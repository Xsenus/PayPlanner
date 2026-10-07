'use strict';

// Called by the normal unit gate in an owned child with a hard process timeout.
const assert = require('node:assert/strict');
const path = require('node:path');
const modulePath = process.argv[2] || require.resolve('braces');
const braces = require(modulePath);
const lib = path.join(path.dirname(modulePath), 'lib');
const checks = [];
const rejected = action => assert.throws(action, error => error instanceof SyntaxError && !/call stack/i.test(error.message));
const operations = ['parse', 'compile', 'expand', 'stringify'];
const deepStrings = [
  '{'.repeat(4000) + 'a' + '}'.repeat(4000),
  '('.repeat(4000) + 'a' + ')'.repeat(4000),
  '{'.repeat(60) + '('.repeat(41) + 'a' + ')'.repeat(41) + '}'.repeat(60),
  '{'.repeat(101) + 'a',
];
for (const input of deepStrings) {
  for (const operation of operations) {
    for (const options of [{}, { maxDepth: Infinity }, { maxDepth: false }, { maxDepth: 10000 }]) {
      rejected(() => braces[operation](input, options));
      checks.push(`${operation}: mandatory string bound`);
    }
  }
}
const deep = () => {
  let node = { type: 'text', value: 'a' };
  for (let i = 0; i < 5000; i++) node = { type: 'root', nodes: [node] };
  return node;
};
for (const operation of ['compile', 'expand', 'stringify']) {
  rejected(() => braces[operation](deep(), { maxDepth: Infinity }));
  checks.push(`${operation}: caller AST bound`);
  const cyclic = { type: 'root', nodes: [] };
  cyclic.nodes.push(cyclic);
  rejected(() => braces[operation](cyclic));
  checks.push(`${operation}: cyclic nodes`);
  const wide = { type: 'root', nodes: Array.from({ length: 20002 }, () => ({ type: 'text', value: 'a' })) };
  rejected(() => braces[operation](wide));
  checks.push(`${operation}: wide nodes`);
  const internal = require(path.join(lib, `${operation}.js`));
  rejected(() => internal(deep()));
  checks.push(`${operation}: private entry bound`);
}
const parentCycle = { type: 'paren', nodes: [] };
parentCycle.parent = parentCycle;
rejected(() => braces.expand(parentCycle));
checks.push('expand: cyclic parent chain');
const breadth = { type: 'root', nodes: [] };
for (let i = 0; i < 100; i++) {
  breadth.nodes.push({ type: 'root', nodes: Array.from({ length: 1000 }, () => ({ type: 'text', value: 'a' })) });
}
rejected(() => braces.compile(breadth));
checks.push('scheduled nodes bounded before growth');
process.stdout.write(JSON.stringify({ passed: checks.length, checks }));
