# PayPlanner security patch for picomatch

These private MIT-licensed packages are derived from picomatch 2.3.2 and 4.0.7. The upstream license, source revision and archive integrity for each version are recorded in `provenance.json`. The two archives retain their upstream package names and distinguish the downstream code with the `-payplanner.1` version suffix.

The parser applies its existing literal fallback to a repeated extglob containing a single branch with an unbounded glob or nested expression. It retains the existing flat character-class rewrite for supported star sequences. This guard runs even when callers opt out of the older configurable recursion heuristic. Ordinary globs, escaped and quoted characters, and other callers retain their upstream paths. [Security advisory](https://github.com/advisories/GHSA-fc5q-39xc-vjpf).

The lockfile uses the 2.x archive for anymatch, micromatch and readdirp. Vite, Vitest, tinyglobby and its fdir peer use 4.x. Both archives are reproducible from the adjacent source directories with sorted `package/` member paths, USTAR, mode 0644 and zero timestamps. The ordinary frontend unit gate verifies each archive and the installed copies used by the consumers. The existing npm audit threshold applies to the full tree.
