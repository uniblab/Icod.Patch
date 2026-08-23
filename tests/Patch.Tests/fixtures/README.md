# Patch fixture provenance

The fixture tree deliberately separates producers and failure classes.

- `gnu/` contains original minimal textual forms transcribed from the documented GNU diff/patch grammars, including all four input formats, multiple hunks, creation, and ed single-dot protection. They are not copies of complete upstream test files; P12 supplements them with opt-in GNU 2.8 differential application tests.
- `icod-diffutils/` contains unified, context, normal, and ed output shaped as produced by the co-resident Icod Diffutils commands. P12 applies all four through the public command. Production and test projects have no project reference to Diffutils.
- `independent/` contains hand-authored interoperability examples, including surrounding mail text and multiple file sections.
- `malformed/` contains intentionally invalid directives, unsafe filenames, range/count mismatches, and unterminated ed text.
- `binary/` contains byte-oriented and mixed-line-ending inputs generated specifically for this test project.

The authoritative upstream archive and checksum are recorded in `patch/upstream/GNU-patch-2.8.md`.

Parser fixtures that GNU patch can consume were smoke-checked with the pinned GNU patch 2.8 executable. Ordinary tests remain fully managed and do not shell out to GNU `patch` or `ed`; the separately labeled Linux differential suite is opt-in and first verifies that the installed command identifies itself as GNU patch 2.8.
