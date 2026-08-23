# GNU patch 2.8 final option and invocation matrix

This is the final Phase P12 conformance inventory for the `Icod.Patch` implementation. GNU patch 2.8 source remains the spelling, arity, alias, and status baseline. The table distinguishes supported release behavior from deliberate, controlled limitations; unsupported source-defined options remain in the parser so long-option abbreviation and ambiguity are resolved against the complete GNU 2.8 surface.

## Source evidence

- GNU release archive: `patch-2.8.tar.xz`
- Release tag: `v2.8`
- Source declarations: `src/patch.c`, `shortopts`, `longopts`, `option_help`, and `get_some_switches`
- Source browser: <https://sources.debian.org/src/patch/2.8-2/src/patch.c/>
- Upstream tests: <https://sources.debian.org/src/patch/2.8-2/tests/>
- Installed manual source: <https://sources.debian.org/src/patch/2.8-2/patch.man/>

The signed GNU release archive and checksum recorded in [`GNU-patch-2.8.md`](GNU-patch-2.8.md) remain authoritative.

## Invocation and status contract

```text
patch [OPTION]... [ORIGFILE [PATCHFILE]]
```

- Without `PATCHFILE`, patch text is read as bytes from standard input.
- `-i PATCHFILE` conflicts with a patch-file operand; `-` means standard input.
- More than two operands is usage trouble.
- Exit status is `0` for success, `1` for rejected, conflicted, skipped, or otherwise partially applied work, and `2` for usage, malformed input, containment, I/O, or transaction trouble.
- Cancellation uses the command-framework canceled status rather than collapsing into GNU status `2`.

## Final option matrix

| Short | Long | Argument | Final Icod.Patch 1.0 state |
|---|---|---|---|
| `-b` | `--backup` | none | **Implemented.** Retains the original according to selected backup naming. The obsolete GNU compatibility form `-b SUFFIX ORIGFILE PATCHFILE` is not implemented. |
| `-B` | `--prefix` | required | **Implemented.** Prefixes the complete backup pathname; containment still applies. |
| `-c` | `--context` | none | **Implemented.** Forces complete context-diff parsing. |
| `-d` | `--directory` | required | **Implemented.** Establishes the canonical working root for source resolution, target selection, and artifact containment. |
| `-D` | `--ifdef` | required | **Not implemented.** Conditional if/then/else output is diagnosed as a controlled unsupported capability. |
| `-e` | `--ed` | none | **Implemented.** Uses the managed patch-compatible ed parser and applier; native `ed` is never invoked. |
| `-E` | `--remove-empty-files` | none | **Implemented.** Deletes successfully patched files that become empty through the artifact transaction. |
| `-f` | `--force` | none | **Implemented.** Suppresses automatic reversal and prerequisite refusal policy. |
| `-F` | `--fuzz` | nonnegative integer | **Implemented.** Bounded context-fuzz matching. |
| `-g` | `--get` | signed integer | **Implemented through an injected retrieval boundary.** No shell interpolation or implicit host VCS command is performed. `PATCH_GET` and POSIX defaults are honored. |
| `-i` | `--input` | required | **Implemented.** Reads an explicit file or standard input for `-`. |
| `-l` | `--ignore-whitespace` | none | **Implemented.** Canonicalizes nonempty horizontal blank runs during matching. |
| `-m` | `--merge[=STYLE]` | optional on long form | **Implemented.** Supports `merge` and `diff3`; short `-m` selects `merge`. |
| `-n` | `--normal` | none | **Implemented.** Forces complete normal-diff parsing. |
| `-N` | `--forward` | none | **Implemented.** Skips patches that appear reversed or already applied. |
| `-o` | `--output` | required | **Implemented.** Writes an alternate transactional output or byte output for `-`. |
| `-p` | `--strip` | nonnegative integer | **Implemented.** Uses platform-aware separator-run component counting. |
| `-r` | `--reject-file` | required | **Implemented.** Writes explicit reject output; `-` discards rejects. |
| `-R` | `--reverse` | none | **Implemented.** Reverses formats with old/new models; reverse ed scripts are rejected explicitly. |
| `-s` | `--quiet`, `--silent` | none | **Implemented.** Suppresses ordinary progress while retaining errors. |
| `-t` | `--batch` | none | **Implemented.** Applies noninteractive defaults for prerequisites and reversal decisions. |
| `-T` | `--set-time` | none | **Implemented.** Applies local-time patch-header timestamps through neutral metadata policy. |
| `-u` | `--unified` | none | **Implemented.** Forces complete unified-diff parsing. |
| `-v` | `--version` | none | **Implemented.** Reports `patch (Icod.Patch) 1.0`. |
| `-V` | `--version-control` | required | **Implemented.** Supports GNU-compatible unique abbreviations for existing/nil, numbered/t, and simple/never. |
| `-x` | `--debug` | signed integer | **Unavailable in the normal release.** GNU handles it only under its `DEBUGGING` build; Icod.Patch diagnoses that release capability explicitly. |
| `-Y` | `--basename-prefix` | required | **Implemented.** Prefixes only the backup basename. |
| `-z` | `--suffix` | required | **Implemented.** Selects simple backup suffix naming. |
| `-Z` | `--set-utc` | none | **Implemented.** Applies UTC patch-header timestamps, including post-2038 values where the host supports them. |
| — | `--dry-run` | none | **Implemented.** Completes parse, selection, matching, and artifact planning without mutation. |
| — | `--verbose` | none | **Implemented.** Emits artifact-policy diagnostics. |
| — | `--binary` | none | **Implemented.** Patch input, target content, output, line endings, incomplete records, and invalid byte values remain byte-oriented. |
| — | `--help` | none | **Implemented.** Final help contains no provisional phase language. |
| — | `--backup-if-mismatch` | none | **Implemented.** Requests mismatch-triggered backup policy. |
| — | `--no-backup-if-mismatch` | none | **Implemented.** Suppresses mismatch-triggered backups unless another option requires one. |
| — | `--posix` | none | **Implemented for the command policies owned by this port:** filename evidence ordering, retrieval default, and backup-policy defaults. Exact GNU locale text and every interactive transcript are not claimed byte-for-byte. |
| — | `--quoting-style` | required | **Implemented.** Supports `literal`, `shell`, `shell-always`, `c`, and `escape`. |
| — | `--reject-format` | required | **Implemented.** Supports `context` and `unified`. |
| — | `--read-only` | required | **Not implemented.** `ignore`, `warn`, and `fail` policy selection is diagnosed explicitly; ordinary access failures remain controlled status `2`. |
| — | `--follow-symlinks` | none | **Implemented.** Terminal target/output links are rejected by default and followed only after canonical containment checks when requested. |

## Aliases, conditional surface, and compatibility decisions

1. `-b` always means backup. Binary mode is long-only `--binary`.
2. `--quiet` and `--silent` alias `-s`.
3. Only the long `--merge` spelling carries an optional style.
4. `-x`/`--debug` remains in the source-defined inventory but is unavailable in the normal release, matching GNU's conditional build intent rather than inventing an unrelated debug contract.
5. `--follow-symlinks` is accepted even though GNU's ordinary help omits it.
6. Unambiguous long abbreviations are resolved against the complete inventory, including controlled unsupported options.
7. The obsolete three-operand `-b SUFFIX ORIGFILE PATCHFILE` compatibility path is a documented divergence rather than an ambiguous parser special case.

## P12 differential and corpus closure

The CoreUtils source snapshot from which this production migration was taken includes managed parser/application corpora covering unified, context, normal, and ed syntax; LF, CRLF, CR, incomplete records, invalid bytes, malformed ranges, quoted names, multi-file mail envelopes, creation/deletion, and deterministic resource limits. The dedicated test project and fixture corpus are intentionally deferred from this first standalone migration tranche and will be migrated separately.

See [`P12-closure-audit.md`](P12-closure-audit.md) for residual functionality and platform limitations.
