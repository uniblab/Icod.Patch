namespace Icod.Patch;

using System.Collections.ObjectModel;

/// <summary>Identifies the recoverability scope required from Completion Gate E6.</summary>
internal enum PatchTransactionRecoveryScope {
	/// <summary>Only one individual artifact is recoverable.</summary>
	Artifact,
	/// <summary>All artifacts belonging to one selected patch target are recoverable together.</summary>
	PatchFile,
	/// <summary>Every artifact in the invocation is recoverable as one unit.</summary>
	Invocation
}

/// <summary>Identifies how a completed transaction relates to a failed later unit.</summary>
internal enum PatchMultiFileCommitPolicy {
	/// <summary>Roll back every previously completed patch-file unit.</summary>
	RollBackCompletedUnits,
	/// <summary>Retain completed patch-file units and recover only the failing unit.</summary>
	PreserveCompletedUnits
}

/// <summary>Describes one Patch-visible requirement for the shared E6 replacement contract.</summary>
internal sealed class PatchE6Requirement {
	/// <summary>Initializes a requirement.</summary>
	public PatchE6Requirement( string name, string description ) {
		this.Name = name ?? throw new ArgumentNullException( nameof( name ) );
		this.Description = description ?? throw new ArgumentNullException( nameof( description ) );
	}

	/// <summary>Gets the stable requirement name.</summary>
	public string Name { get; }

	/// <summary>Gets the behavioral requirement.</summary>
	public string Description { get; }
}

/// <summary>
/// Freezes the Patch-facing requirements that Completion Gate E6 must satisfy.
/// </summary>
internal sealed class PatchE6TransactionContract {
	private PatchE6TransactionContract() {
		this.RequiredFailureStages = new ReadOnlyCollection<PatchTransactionStage>(
			new[] {
				PatchTransactionStage.Validate,
				PatchTransactionStage.CreateTemporary,
				PatchTransactionStage.WriteTemporary,
				PatchTransactionStage.FlushTemporary,
				PatchTransactionStage.PreserveRollback,
				PatchTransactionStage.Revalidate,
				PatchTransactionStage.Commit,
				PatchTransactionStage.ApplyMetadata,
				PatchTransactionStage.PublishBackup,
				PatchTransactionStage.RestoreMetadata,
				PatchTransactionStage.Rollback,
				PatchTransactionStage.Cleanup,
				PatchTransactionStage.FlushDirectory
			}
		);
		this.Requirements = new ReadOnlyCollection<PatchE6Requirement>(
			new[] {
				new PatchE6Requirement(
					"secure-sibling-temporary",
					"Create every replacement and rollback file exclusively in the destination directory."
				),
				new PatchE6Requirement(
					"complete-before-replace",
					"Write and flush complete content before changing the destination pathname."
				),
				new PatchE6Requirement(
					"identity-revalidation",
					"Revalidate E3 identity and observable state immediately before each commit."
				),
				new PatchE6Requirement(
					"no-follow-default",
					"Operate on the observed pathname object unless Patch explicitly selected --follow-symlinks."
				),
				new PatchE6Requirement(
					"per-file-recovery",
					"Group each target with its backup, per-target reject, and file output; model shared destinations as explicit independent units."
				),
				new PatchE6Requirement(
					"multi-file-partial-success",
					"Retain completed patch-file units when a later unit fails."
				),
				new PatchE6Requirement(
					"metadata-restoration",
					"Apply requested metadata after commit and restore observed metadata during rollback."
				),
				new PatchE6Requirement(
					"deterministic-cleanup",
					"Attempt every temporary and rollback cleanup after success, failure, and cancellation."
				),
				new PatchE6Requirement(
					"containment",
					"Reject temporary, backup, reject, and output paths that escape their approved containment roots."
				),
				new PatchE6Requirement(
					"atomicity-reporting",
					"Report whether replacement is atomic instead of silently treating an unknown fallback as atomic."
				),
				new PatchE6Requirement(
					"durability-reporting",
					"Report content and directory durability capabilities separately from logical commit success."
				),
				new PatchE6Requirement(
					"artifact-policy-separation",
					"Keep GNU Patch naming, prompts, reject policy, and partial-application status above E6."
				)
			}
		);
	}

	/// <summary>Gets the frozen Patch-facing E6 contract.</summary>
	public static PatchE6TransactionContract Current { get; } = new();

	/// <summary>Gets the required recoverability scope.</summary>
	public PatchTransactionRecoveryScope RecoveryScope => PatchTransactionRecoveryScope.PatchFile;

	/// <summary>Gets the required multi-file completion policy.</summary>
	public PatchMultiFileCommitPolicy MultiFileCommitPolicy => PatchMultiFileCommitPolicy.PreserveCompletedUnits;

	/// <summary>Gets whether secure exclusive sibling temporaries are mandatory.</summary>
	public bool RequiresSecureSiblingTemporaries => true;

	/// <summary>Gets whether a complete flushed replacement is mandatory before commit.</summary>
	public bool RequiresFlushBeforeCommit => true;

	/// <summary>Gets whether deterministic cleanup is mandatory.</summary>
	public bool RequiresDeterministicCleanup => true;

	/// <summary>Gets whether cancellation must recover the currently committing unit.</summary>
	public bool RequiresCancellationRecovery => true;

	/// <summary>Gets whether explicit atomicity capability reporting is mandatory.</summary>
	public bool RequiresAtomicityCapabilityReporting => true;

	/// <summary>Gets the complete deterministic transaction failure matrix.</summary>
	public IReadOnlyList<PatchTransactionStage> RequiredFailureStages { get; }

	/// <summary>Gets the Patch-facing behavioral requirements.</summary>
	public IReadOnlyList<PatchE6Requirement> Requirements { get; }
}
