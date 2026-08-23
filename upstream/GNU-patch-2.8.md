# GNU patch 2.8 baseline

The authoritative baseline for `Icod.Patch` is GNU patch 2.8, released March 29, 2025.

## Pinned release

- Archive: `patch-2.8.tar.xz`
- Official archive directory: <https://ftp.gnu.org/gnu/patch/>
- Release announcement: <https://lists.gnu.org/archive/html/info-gnu/2025-03/msg00014.html>
- Size: `907208` bytes
- SHA-256: `f87cee69eec2b4fcbf60a396b030ad6aa3415f192aa5f7ee84cad5e11f7f5ae3`
- Detached signature: `patch-2.8.tar.xz.sig`
- Signing-key fingerprint: `259B 3792 B3D6 D319 212C C4DC D5BF 9FEB 0313 653A`
- Release source ref: GNU patch tag `v2.8`
- Release commit: `48ceda8` (`Version 2.8`, abbreviated source identifier)

The checksum and signing-key fingerprint are taken from the official GNU release announcement. The tag and abbreviated release commit are recorded for source navigation; the signed release archive, tests, documentation, and installed manual remain the primary conformance evidence for this workstream.

## Fixture policy

GNU-derived fixtures identify their provenance in `tests/Patch.Tests/fixtures/README.md`. Ordinary repository tests do not depend on a locally installed `patch` executable and do not shell out to GNU patch or `ed`. The separately labeled Linux differential suite is opt-in and first verifies that the installed command identifies itself specifically as GNU patch 2.8.

## Option inventory

The exact GNU patch 2.8 option spellings, aliases, arities, value domains, conditional entries, compatibility traps, implementation owners, and upstream test map are recorded in [`GNU-patch-2.8-option-matrix.md`](GNU-patch-2.8-option-matrix.md).
