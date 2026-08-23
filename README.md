# PATCH(1)

## NAME

**patch** — apply diff files to original files

## SYNOPSIS

```text
patch [OPTION]... [ORIGFILE [PATCHFILE]]
```

## DESCRIPTION

`Icod.Patch` is a managed .NET implementation of GNU `patch(1)`, modeled on GNU patch 2.8 and targeting .NET 10 with C# 13.

`patch` reads a difference listing and applies its changes to an original file or set of files. Patch input may come from standard input, an explicit `-i` input file, or the `PATCHFILE` operand. When `ORIGFILE` is omitted, file names are selected from patch headers and `Index:` evidence according to GNU- or POSIX-style policy.

The implementation accepts unified, context, normal, and patch-compatible ed-script input. It preserves patch bytes, source offsets, LF/CRLF/CR line endings, incomplete final records, and invalid non-directive byte values. Matching supports exact placement, offsets, bounded fuzz, horizontal-whitespace canonicalization, reversal detection, prerequisites, and merge output.

Filesystem selection and mutation are implemented through `Icod.Path` and `Icod.CommandFramework`. `Icod.Patch` does not require native `patch`, native `ed`, `Icod.DiffUtils.Shared`, or `Icod.CoreUtils.Shared` at runtime.

The path-containment policy is intentionally stricter than historical GNU patch behavior. Selected targets, alternate outputs, backups, and reject files must remain beneath the physically canonical `-d` working root. Parent traversal, cross-volume escape, and link or reparse-point resolution outside that root are rejected.

## INPUT FORMATS

When no input format is forced, `patch` detects supported patch sections while retaining surrounding non-patch text.

```text
-c, --context
    Interpret input as a context diff.

-e, --ed
    Interpret input as a patch-compatible ed script. The script is applied by
    the managed engine; no external ed executable is invoked.

-n, --normal
    Interpret input as a normal diff.

-u, --unified
    Interpret input as a unified diff.

--binary
    Select binary-compatible processing. Patch and target content remain
    byte-oriented, including non-UTF-8 data, exact line terminators, and
    incomplete final records.
```

## OPTIONS

### Input and directory selection

```text
-i FILE, --input=FILE
    Read patch input from FILE. FILE may be - for standard input. An explicit
    input file conflicts with a PATCHFILE operand.

-d DIR, --directory=DIR
    Use DIR as the working directory and canonical containment root.

-p NUM, --strip=NUM
    Strip NUM leading pathname-component separator runs from names found in
    patch input. Component handling follows the active platform semantics.

-g NUM, --get=NUM
    Select version-control retrieval policy. Positive values permit retrieval,
    zero disables it, and negative values request an interactive decision.
    Retrieval is exposed through the managed provider boundary; Icod.Patch does
    not implicitly shell out to a host version-control command.

--posix
    Select the supported POSIX policy defaults for filename candidate ordering,
    version-control retrieval, and mismatch backup behavior.

--follow-symlinks
    Permit terminal target and output links to be followed after canonical
    containment checks. Terminal links are rejected by default.
```

### Matching and application

```text
-f, --force
    Suppress automatic reversal and prerequisite refusal policy.

-F NUM, --fuzz=NUM
    Set the maximum nonnegative context fuzz factor used during hunk matching.

-l, --ignore-whitespace
    Match nonempty horizontal runs of spaces and tabs canonically.

-m, --merge[=STYLE]
    Merge changes instead of producing ordinary rejected hunks. STYLE may be
    merge or diff3. Short -m selects merge style.

-N, --forward
    Skip patches that appear to be reversed or already applied.

-R, --reverse
    Apply the patch in reverse. Reverse application of ed scripts is rejected
    because the script lacks enough old/new information.

-t, --batch
    Use noninteractive defaults for reversal and prerequisite decisions.

-E, --remove-empty-files
    Remove a successfully patched file when its resulting content is empty.

--dry-run
    Perform parsing, path selection, matching, and artifact planning without
    committing filesystem changes.
```

### Output, rejects, and backups

```text
-o FILE, --output=FILE
    Write patched content to FILE instead of replacing the selected input.
    FILE may be - for standard output.

-r FILE, --reject-file=FILE
    Write rejected hunks to FILE instead of the default reject pathname.
    FILE may be - to discard rejects.

--reject-format=FORMAT
    Select context or unified reject output.

-b, --backup
    Retain the original file using the selected backup naming policy.

-B PREFIX, --prefix=PREFIX
    Prefix the complete simple-backup pathname with PREFIX.

-Y PREFIX, --basename-prefix=PREFIX
    Prefix only the basename when constructing a simple backup pathname.

-z SUFFIX, --suffix=SUFFIX
    Use SUFFIX for simple backup names.

-V METHOD, --version-control=METHOD
    Select backup naming: existing/nil, numbered/t, or simple/never. Unique
    abbreviations are accepted.

--backup-if-mismatch
    Request a backup when patch application requires mismatch heuristics.

--no-backup-if-mismatch
    Suppress mismatch-triggered backups unless another option requires one.
```

### Timestamps, diagnostics, and display

```text
-T, --set-time
    Apply patch-header access and modification times using local-time policy.

-Z, --set-utc
    Apply patch-header access and modification times using UTC policy,
    including post-2038 timestamps where the host supports them.

-s, --quiet, --silent
    Suppress ordinary progress output while retaining errors.

--verbose
    Emit additional artifact and application diagnostics.

--quoting-style=STYLE
    Select filename quoting style: literal, shell, shell-always, c, or escape.

--help
    Display command help.

-v, --version
    Display Icod.Patch version information.
```

### Recognized but unavailable GNU surfaces

The following GNU patch options are retained in the parser so GNU long-option abbreviation and ambiguity rules remain well defined, but Icod.Patch 1.0 reports them as unavailable capabilities:

```text
-D NAME, --ifdef=NAME
    Conditional preprocessor output is not implemented.

--read-only=BEHAVIOR
    GNU's selectable ignore/warn/fail read-only policy is not implemented.

-x NUM, --debug=NUM
    GNU DEBUGGING-build flags are not available in the normal managed release.
```

The obsolete GNU compatibility invocation `-b SUFFIX ORIGFILE PATCHFILE` is also not supported; `-b` always means `--backup`.

## FILE SELECTION

If `ORIGFILE` is supplied, it is the authoritative target operand for the patch sections being applied. Otherwise, `patch` derives candidate names from old/new headers and `Index:` records, applies `-p`, and resolves the result beneath the canonical working root.

`/dev/null` headers represent creation or deletion where supported by the input format. Multiple file sections are applied independently, and completed earlier file units may remain committed if a later independent unit fails.

## BACKUPS AND REJECTS

Simple and numbered backup naming is supported. Backup, reject, target, and alternate-output paths are canonicalized and subjected to the same working-root containment policy as input targets.

Rejected hunks are written in unified format for unified input by default and in context form otherwise, unless `--reject-format` selects a format explicitly.

## ENVIRONMENT

```text
PATCH_GET
    Supplies the default value for version-control retrieval policy.

PATCH_VERSION_CONTROL
    Selects backup version-control style. Takes precedence over VERSION_CONTROL.

VERSION_CONTROL
    Selects backup version-control style when PATCH_VERSION_CONTROL is unset.

SIMPLE_BACKUP_SUFFIX
    Supplies the default simple backup suffix when no explicit suffix is given.

QUOTING_STYLE
    Supplies the default filename quoting style.

POSIXLY_CORRECT
    Enables the supported POSIX policy defaults.
```

## EXIT STATUS

```text
0   All requested patch work completed successfully.
1   One or more hunks were rejected, conflicted, skipped, or otherwise only
    partially applied.
2   Usage, malformed input, containment, I/O, or transaction trouble occurred.
```

Cooperative cancellation uses the shared `Icod.CommandFramework` cancellation status rather than being collapsed into GNU status `2`.

## PLATFORM NOTES

The implementation targets .NET 10 and is intended to run on Windows, Linux, and macOS. Path grammar, roots, volumes, symbolic links, junctions/reparse points, filesystem identity, metadata, transactional replacement, and durability capabilities are delegated to published neutral Icod libraries.

Atomic publication and directory-durability guarantees are capability-reported by the active filesystem provider. Unix owner, group, mode, and timestamp fidelity depends on host capabilities and process privileges. Windows symbolic-link behavior depends on host permissions and policy.

## LIMITATIONS

Icod.Patch 1.0 does not implement conditional `-D` output, GNU `--read-only` policy selection, normal-release `-x` debugging flags, the obsolete three-operand `-b` form, Git binary/copy/rename payloads, hard-link-set topology updates, FIFO/device/socket targets, or persistent crash-recovery journaling.

The implementation does not claim byte-for-byte parity with every GNU locale-specific diagnostic or interactive transcript. The complete conformance and residual-gap ledger is maintained in `upstream/GNU-patch-2.8-option-matrix.md` and `upstream/P12-closure-audit.md`.

## AUTHORS

Larry Wall wrote the original `patch`. Paul Eggert substantially developed GNU patch, removing arbitrary limits and adding support including binary files, file timestamps, file deletion, and improved POSIX conformance. Wayne Davison contributed unified-diff support, David MacKenzie contributed configuration and backup support, and Andreas Grünbacher contributed merge support and serves as a GNU patch co-maintainer. GNU patch 2.8 also incorporated contributions from Bruno Haible, Collin Funk, Eli Schwartz, Jean Delvare, Jim Meyering, Kerin Millar, Petr Vaněk, Sam James, Takashi Iwai, Andreas Grünbacher, and Paul Eggert.

Migrated to .Net by Timothy J. Bruce <uniblab@hotmail.com>.

## COPYRIGHT

Copyright (c) 2026 Timothy J. Bruce

See the repository `LICENSE` file for licensing terms applicable to this managed implementation. GNU patch provenance and release information are recorded under `upstream/`.

## SEE ALSO

`diff(1)`, `diff3(1)`, `ed(1)`, `patch(1)`

Repository architecture, extraction history, build/test instructions, dependency details, and upstream attribution sources are preserved in [`doc/Repository-Overview.md`](doc/Repository-Overview.md). The development history is maintained in [`Icod.Patch-Development-Roadmap.md`](Icod.Patch-Development-Roadmap.md).
