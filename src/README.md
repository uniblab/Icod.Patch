# Icod.Patch source layout

The source directory contains the closed P0–P12 implementation: complete Wave A parsers, the Wave B1 pure application engine, the Wave B2 path-planning layer, Phase P8 artifact policy, and the stabilized P11A/P11B adapter over shared E6:

- `Command.cs` owns public invocation, shared option parsing, environment policy, final 1.0 capability diagnostics, compatibility wrappers, help, version, cancellation, and GNU option validation.
- `PatchApplication.cs` acquires the byte-oriented patch source, coordinates scanning and parsing, invokes P7 planning and P8 artifact planning, handles dry runs and byte-oriented standard output, and commits through the injected E6-backed boundary.
- `PatchArtifacts.cs` derives explicit target, backup, reject, and output artifacts from final P7 virtual state; implements GNU backup/reject/output naming and metadata policy; assigns per-file recovery units; quotes hostile pathnames; and consolidates repeated patches to one canonical target.
- `PatchFileSystem.cs` defines `IPatchFileSystem` and `IPatchTransaction`, consumes neutral path, metadata, mutation, and transactional-replacement providers, and enforces lexical and physical artifact containment.
- `PatchE6Transaction.cs` adapts immutable Patch artifacts and per-file recovery units to `TransactionalFileReplacementTransaction`.
- `PatchE6Contract.cs` retains the frozen Patch requirement matrix used by P10 and P11 validation.
- `PatchInteraction.cs` supplies deterministic command-line answers for reversal, prerequisite, and version-control questions.
- `PatchSource.cs` streams patch input into an owner-private temporary spool while retaining bounded line metadata and exact record terminators.
- `PatchScanner.cs` classifies structural records and finds count-aware unified, context, normal, and ed-script sections.
- `PatchModels.cs`, `PatchSyntaxModels.cs`, and `PatchEngineModels.cs` contain the immutable scan, syntax, virtual-file, policy, and result models.
- `PatchParser.cs`, `UnifiedContextPatchParser.cs`, and `NormalEdPatchParser.cs` materialize and parse all supported patch grammars.
- `PatchTargetContent.cs` owns in-memory or spill-backed byte-preserved target records and deterministic temporary-storage cleanup.
- `PatchApplicationEngine.cs` performs exact and heuristic virtual application, ed interpretation, offsets, fuzz, reversal, prerequisites, and merge output without selecting paths or mutating the filesystem.
- `PatchPrerequisite.cs` extracts and checks GNU-style `Prereq:` tokens.
- `PatchPathSelection.cs`, `PatchPathModels.cs`, and `PatchApplicationPlanner.cs` implement filename evidence, canonical target selection, virtual state, and multi-file planning over `Icod.Path`.
- `PatchTemporaryFile.cs` creates exclusive owner-private temporary files shared by source, target, and result storage.
- `AssemblyInfo.cs` reserves internals visibility for the dedicated test assembly that will be migrated in the later test tranche.

P8 consumes the P7 plan and does not repeat filename selection or matching. P11A keeps GNU-visible backup, reject, output, and multi-file partial-success policy in Patch while delegating transaction mechanics to the neutral transactional-replacement layer. P11B removed the unreachable P9 implementation; no command-local replacement engine remains.

The final behavior and residual-gap ledgers live under `upstream/`.
