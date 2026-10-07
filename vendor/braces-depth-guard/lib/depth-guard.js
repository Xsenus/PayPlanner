'use strict';

// Local security patch for GHSA-vfj7-8cjw-p6xm. These ceilings are mandatory;
// caller options cannot restore the vulnerable unbounded traversal.
const MAX_NESTING_DEPTH = 100;
const MAX_AST_DEPTH = MAX_NESTING_DEPTH + 2;
const MAX_AST_NODES = 20002;

const assertCanNest = stack => {
  if (stack.length > MAX_NESTING_DEPTH) {
    throw new SyntaxError(`Brace or parenthesis nesting exceeds ${MAX_NESTING_DEPTH}`);
  }
};

const assertSafeAst = ast => {
  // Iterative preorder traversal: do not recurse into an untrusted tree while
  // checking the bound. Parent/prev references are deliberately not traversed.
  const pending = [{ node: ast, depth: 0 }];
  let scheduled = 1;
  while (pending.length > 0) {
    const { node, depth } = pending.pop();
    if (depth > MAX_AST_DEPTH) {
      throw new SyntaxError('Brace AST exceeds the mandatory depth or node limit');
    }
    if (node == null || typeof node !== 'object') {
      throw new TypeError('Expected a brace AST node');
    }
    if (node.nodes == null) continue;
    if (!Array.isArray(node.nodes)) throw new TypeError('Expected brace AST nodes array');
    // Reject oversized child arrays before adding them to the work list.
    if (node.nodes.length > MAX_AST_NODES - scheduled) {
      throw new SyntaxError('Brace AST exceeds the mandatory depth or node limit');
    }
    scheduled += node.nodes.length;
    for (let i = node.nodes.length - 1; i >= 0; i--) {
      pending.push({ node: node.nodes[i], depth: depth + 1 });
    }
  }
};

module.exports = { assertCanNest, assertSafeAst, MAX_NESTING_DEPTH, MAX_AST_DEPTH, MAX_AST_NODES };
