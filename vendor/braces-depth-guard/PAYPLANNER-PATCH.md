# PayPlanner: local security patch for braces 3.0.3

This is a private, source-visible derivative of the official MIT-licensed
`braces@3.0.3`, not an upstream release. The package retains the `braces` name;
`3.0.3-payplanner.2` identifies this actual downstream patch. Nothing is
published to npm. The upstream LICENSE and original authorship are retained;
local changes use the same MIT license.

Upstream git revision: `74b2db2938fad48a2ea54a9c8bf27a37a62c350d`.
Official archive: `https://registry.npmjs.org/braces/-/braces-3.0.3.tgz`.
Original SHA512 integrity:
`sha512-yQbXgO/OSZVD2IsiLlro+7Hf6Q18EJrKSEsdoMzKePKXct3gvD8oLcOQdIzGupr5Fj+EDe8gO/lxc1BzfMpxvA==`.
Per-file upstream and patched hashes are in `provenance.json`.

The patch addresses [GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm):
the upstream recursive walkers overflow on deeply nested input under the
existing character limit. The upstream [draft PR77](https://github.com/micromatch/braces/pull/77)
is opt-in; chokidar's default calls do not enable its protection. That draft
has not been substituted for a released upstream fix.

## Actual changes

- `parse` checks combined brace/parenthesis nesting before adding a container.
  Maximum structural nesting is 100. Escapes, quotes and character classes
  retain their original literal treatment.
- `compile`, `expand` and `stringify`, including direct private-module entry
  points, check caller ASTs iteratively before recursive work. AST depth is
  limited to102 edges; at most20002 nodes can be scheduled. The node budget is
  checked before work-list growth, including shared/cyclic graphs.
- `expand` also bounds both ancestor walks. A cyclic parent chain cannot hang
  those loops.
- Limits are mandatory and cannot be disabled by `maxDepth`, `rangeLimit` or
  other caller options. Rejection is a controlled `SyntaxError`, rather than
  an engine stack-overflow `RangeError`.
- Existing range limits, normal glob results and APIs are preserved. This
  patch does not claim to eliminate every possible regex or expansion-size
  denial of service.

## Installation and verification

Use the explicit local tarball override, with its SHA512 in package-lock.
Sources, license, provenance and the reproducible packed artifact stay
together in this directory. `artifact.json` records the archive and provenance
hashes; the archive excludes itself and that separate artifact record.

The standard frontend unit gate contains `src/build/bracesSecurity.test.ts`.
It verifies the archive/source provenance and resolves the real `braces`
imports used by Tailwind/chokidar/micromatch/fast-glob. It checks mandatory
bounds in a bounded child process, normal behavior against the unmodified
upstream test fixture, and exact boundaries. The fixture is test-only and must
never replace the patched installed dependency.

`npm audit --audit-level=moderate` remains mandatory. A clean audit of a local
or downstream package version is **not** proof that its code is safe: the
security and provenance tests must pass too. No advisory is ignored, no audit
threshold is raised, and no unpatched package is merely renamed.

Before release, verify clean `npm ci`, the complete applicable frontend gate,
the generated CSS against the saved pre-patch bytes, and all backend/Linux
gates required by the release plan. Preserve the current Tailwind3 browser
requirements. Replace this derivative when a released upstream/parent chain
provides equivalent default protection and passes those same checks.

Packaging revision2 canonicalizes package metadata to LF before packing.
Git filter/checkout bytes must match provenance; guard implementation is unchanged.
