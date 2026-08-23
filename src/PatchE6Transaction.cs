namespace Icod.Patch;

using Icod.CommandFramework.FileSystem.Metadata;
using Icod.CommandFramework.FileSystem.Mutation;
using Icod.CommandFramework.FileSystem.RecursiveMutation;
using Icod.CommandFramework.FileSystem.TransactionalReplacement;
using Icod.CommandFramework.FileSystem.Traversal;
using System.IO;

/// <summary>Adapts immutable Patch artifacts to the shared Completion Gate E6 transaction engine.</summary>
internal sealed class PatchE6Transaction : IPatchTransaction {
	private readonly IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> patchArtifacts;
	private readonly IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> retainedBackups;
	private readonly IReadOnlyDictionary<string, PatchArtifact> diagnosticArtifacts;
	private readonly TransactionalFileReplacementTransaction? transaction;

	/// <summary>Initializes one E6-backed Patch transaction.</summary>
	public PatchE6Transaction(
		PatchArtifactPlan plan,
		ITransactionalReplacementFileSystem fileSystem,
		IPatchTransactionFailureInjector failureInjector
	) {
		ArgumentNullException.ThrowIfNull( plan );
		ArgumentNullException.ThrowIfNull( fileSystem );
		ArgumentNullException.ThrowIfNull( failureInjector );
		var projection = CreateProjection( plan );
		this.patchArtifacts = projection.PatchArtifacts;
		this.retainedBackups = projection.RetainedBackups;
		this.diagnosticArtifacts = projection.DiagnosticArtifacts;
		if ( 0 == projection.Artifacts.Count ) {
			return;
		}
		this.transaction = new TransactionalFileReplacementTransaction(
			projection.Artifacts,
			fileSystem,
			new TransactionalReplacementOptions {
				ContainmentRootPath = plan.ContainmentRootPath,
				AtomicityPolicy = TransactionalReplacementAtomicityPolicy.PreferAtomic,
				CommitPolicy = TransactionalReplacementCommitPolicy.ContinueIndependentUnits,
				BackupPolicy = TransactionalReplacementBackupPolicy.None,
				RequireStagedDurability = true,
				RequireDirectoryDurability = false
			},
			failureInjector: new FailureInjectorAdapter(
				failureInjector,
				this.patchArtifacts,
				this.retainedBackups
			)
		);
	}

	/// <inheritdoc/>
	public async Task StageAsync( CancellationToken cancellationToken = default ) {
		if ( null == this.transaction ) {
			cancellationToken.ThrowIfCancellationRequested();
			return;
		}
		try {
			await this.transaction.StageAsync( cancellationToken ).ConfigureAwait( false );
		} catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested ) {
			throw;
		} catch ( Exception exception ) {
			throw new IOException( exception.Message, exception );
		}
	}

	/// <inheritdoc/>
	public async Task<PatchTransactionResult> CommitAsync(
		CancellationToken cancellationToken = default
	) {
		if ( null == this.transaction ) {
			cancellationToken.ThrowIfCancellationRequested();
			return new PatchTransactionResult(
				PatchTransactionOutcome.Succeeded,
				Array.Empty<string>()
			);
		}
		var result = await this.transaction.CommitAsync( cancellationToken ).ConfigureAwait( false );
		var outcome = MapOutcome( result.Outcome );
		if ( PatchTransactionOutcome.Succeeded == outcome
			&& result.Diagnostics.Any(
				diagnostic => TransactionalReplacementDiagnosticCode.CleanupFailed == diagnostic.Code
			) ) {
			outcome = PatchTransactionOutcome.FailedCleanupIncomplete;
		}
		return new PatchTransactionResult(
			outcome,
			result.Diagnostics.Select( this.FormatDiagnostic ).ToArray(),
			result.CommittedRecoveryUnitIds,
			result.RolledBackRecoveryUnitIds,
			result.Diagnostics.Select( value => value.Exception ).FirstOrDefault( value => null != value )
		);
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		return null == this.transaction
			? ValueTask.CompletedTask
			: this.transaction.DisposeAsync();
	}

	private string FormatDiagnostic( TransactionalReplacementDiagnostic diagnostic ) {
		var normalized = NormalizePath( diagnostic.Path );
		var displayName = this.diagnosticArtifacts.TryGetValue( normalized, out var artifact )
			? artifact.DisplayName
			: diagnostic.Path;
		var message = TransactionalReplacementDiagnosticCode.RollbackFailed == diagnostic.Code
			&& !diagnostic.Message.Contains( "rollback failed", StringComparison.Ordinal )
				? string.Concat( "rollback failed: ", diagnostic.Message )
				: diagnostic.Message;
		return string.Concat( displayName, ": ", message );
	}

	private static Projection CreateProjection( PatchArtifactPlan plan ) {
		var comparer = HostPathComparer();
		var mutableArtifacts = plan.Artifacts.Where(
			artifact => PatchArtifactAction.WriteStandardOutput != artifact.Action
		).ToArray();
		var consumedBackups = new HashSet<PatchArtifact>();
		var backupForTarget = new Dictionary<PatchArtifact, PatchArtifact>();
		foreach ( var unit in mutableArtifacts.GroupBy( artifact => artifact.TransactionUnitId ) ) {
			var backup = unit.FirstOrDefault( artifact => PatchArtifactKind.Backup == artifact.Kind );
			var target = unit.FirstOrDefault(
				artifact => PatchArtifactKind.Target == artifact.Kind
					&& PatchArtifactAction.ValidateOnly != artifact.Action
			);
			if ( null != backup && null != target && target.ExpectedDestination.Exists ) {
				consumedBackups.Add( backup );
				backupForTarget.Add( target, backup );
			}
		}

		var ordered = mutableArtifacts
			.Where( artifact => !consumedBackups.Contains( artifact ) )
			.Select( (artifact, index) => new { Artifact = artifact, Index = index } )
			.GroupBy( value => value.Artifact.TransactionUnitId )
			.SelectMany( unit => unit
				.OrderBy( value => ArtifactOrder( value.Artifact ) )
				.ThenBy( value => value.Index ) )
			.ToArray();
		var artifacts = new List<TransactionalReplacementArtifact>( ordered.Length );
		var patchArtifacts = new Dictionary<TransactionalReplacementArtifact, PatchArtifact>();
		var retainedBackups = new Dictionary<TransactionalReplacementArtifact, PatchArtifact>();
		var diagnosticArtifacts = new Dictionary<string, PatchArtifact>( comparer );
		foreach ( var item in ordered ) {
			var patchArtifact = item.Artifact;
			backupForTarget.TryGetValue( patchArtifact, out var retainedBackup );
			var replacementArtifact = CreateArtifact( patchArtifact, retainedBackup );
			artifacts.Add( replacementArtifact );
			patchArtifacts.Add( replacementArtifact, patchArtifact );
			if ( null != retainedBackup ) {
				retainedBackups.Add( replacementArtifact, retainedBackup );
				diagnosticArtifacts[NormalizePath( retainedBackup.Path )] = retainedBackup;
			}
			diagnosticArtifacts[NormalizePath( patchArtifact.Path )] = patchArtifact;
		}
		return new Projection( artifacts, patchArtifacts, retainedBackups, diagnosticArtifacts );
	}

	private static int ArtifactOrder( PatchArtifact artifact ) {
		if ( PatchArtifactAction.ValidateOnly == artifact.Action ) {
			return 0;
		}
		return artifact.Kind switch
		{
			PatchArtifactKind.Target => 1,
			PatchArtifactKind.Reject => 2,
			PatchArtifactKind.Output => 3,
			PatchArtifactKind.Backup => 4,
			_ => 5
		};
	}

	private static TransactionalReplacementArtifact CreateArtifact(
		PatchArtifact artifact,
		PatchArtifact? retainedBackup
	) {
		var action = artifact.Action switch
		{
			PatchArtifactAction.Write => TransactionalReplacementAction.Replace,
			PatchArtifactAction.Delete => TransactionalReplacementAction.Delete,
			PatchArtifactAction.ValidateOnly => TransactionalReplacementAction.ValidateOnly,
			_ => throw new InvalidOperationException( "standard-output artifacts do not belong to E6" )
		};
		var precondition = CreatePrecondition( artifact.ExpectedDestination );
		TransactionalReplacementContentWriter? writer = null;
		if ( TransactionalReplacementAction.Replace == action ) {
			writer = (stream, token) => new ValueTask(
				artifact.Content!.WriteToAsync( stream, token )
			);
		}
		var metadata = TransactionalReplacementAction.Replace == action
			? CreateMetadata( artifact )
			: (Source: (FileSystemMetadata?)null, Plan: (RecursiveMetadataPreservationPlan?)null);
		return new TransactionalReplacementArtifact(
			artifact.TransactionUnitId,
			artifact.Path,
			action,
			precondition,
			writer,
			artifact.DisplayName,
			metadata.Source,
			metadata.Plan,
			explicitBackupPath: retainedBackup?.Path,
			retainBackup: null != retainedBackup
		);
	}

	private static FileSystemMutationPrecondition CreatePrecondition(
		PatchFileObservation observation
	) {
		if ( !observation.Exists ) {
			return FileSystemMutationPrecondition.DestinationMustNotExist();
		}
		return FileSystemMutationPrecondition.FromObservation(
			observation.Metadata!.Kind,
			observation.Metadata.EntryIdentity,
			PathDereferenceMode.NoFollow
		);
	}

	private static (FileSystemMetadata? Source, RecursiveMetadataPreservationPlan? Plan) CreateMetadata(
		PatchArtifact artifact
	) {
		var requested = RecursiveMetadataFields.None;
		var required = RecursiveMetadataFields.None;
		if ( artifact.Metadata.Mode.HasValue ) {
			requested |= RecursiveMetadataFields.Mode;
		}
		if ( artifact.Metadata.UserId.HasValue && artifact.Metadata.GroupId.HasValue ) {
			requested |= RecursiveMetadataFields.Ownership;
		}
		if ( artifact.Metadata.AccessTime.HasValue ) {
			requested |= RecursiveMetadataFields.AccessTime;
			if ( artifact.Metadata.RequireTimestamps ) {
				required |= RecursiveMetadataFields.AccessTime;
			}
		}
		if ( artifact.Metadata.ModificationTime.HasValue ) {
			requested |= RecursiveMetadataFields.ModificationTime;
			if ( artifact.Metadata.RequireTimestamps ) {
				required |= RecursiveMetadataFields.ModificationTime;
			}
		}
		if ( RecursiveMetadataFields.None == requested ) {
			return (null, null);
		}
		var observed = artifact.ExpectedDestination.Metadata;
		var source = new FileSystemMetadata(
			artifact.Path,
			observed?.Kind ?? FileSystemEntryKind.File,
			false,
			false,
			observed?.EntryIdentity ?? new FileSystemEntryIdentity( "patch", artifact.Path ),
			observed?.FileSystemIdentity ?? new FileSystemIdentity( "patch", GetFileSystemKey( artifact.Path ) )
		) {
			Mode = artifact.Metadata.Mode.HasValue
				? FileSystemMetadataValue<uint>.Available( checked( (uint)artifact.Metadata.Mode.Value ) )
				: default,
			UserId = artifact.Metadata.UserId.HasValue
				? FileSystemMetadataValue<uint>.Available( artifact.Metadata.UserId.Value )
				: default,
			GroupId = artifact.Metadata.GroupId.HasValue
				? FileSystemMetadataValue<uint>.Available( artifact.Metadata.GroupId.Value )
				: default,
			AccessTime = artifact.Metadata.AccessTime.HasValue
				? FileSystemMetadataValue<DateTimeOffset>.Available( artifact.Metadata.AccessTime.Value )
				: default,
			ModificationTime = artifact.Metadata.ModificationTime.HasValue
				? FileSystemMetadataValue<DateTimeOffset>.Available( artifact.Metadata.ModificationTime.Value )
				: default
		};
		return (source, RecursiveMetadataPreservationPlan.Create( source, requested, required ));
	}

	private static string GetFileSystemKey( string path ) {
		return System.IO.Path.GetPathRoot( System.IO.Path.GetFullPath( path ) ) ?? Directory.GetCurrentDirectory();
	}

	private static PatchTransactionOutcome MapOutcome( TransactionalReplacementOutcome outcome ) {
		return outcome switch
		{
			TransactionalReplacementOutcome.Succeeded => PatchTransactionOutcome.Succeeded,
			TransactionalReplacementOutcome.FailedBeforeCommit => PatchTransactionOutcome.FailedBeforeCommit,
			TransactionalReplacementOutcome.FailedRolledBack => PatchTransactionOutcome.FailedRolledBack,
			TransactionalReplacementOutcome.FailedPartiallyCommitted => PatchTransactionOutcome.FailedPartiallyCommitted,
			TransactionalReplacementOutcome.FailedRollbackIncomplete => PatchTransactionOutcome.FailedRollbackIncomplete,
			TransactionalReplacementOutcome.FailedCleanupIncomplete => PatchTransactionOutcome.FailedCleanupIncomplete,
			TransactionalReplacementOutcome.FailedAtomicityUnavailable => PatchTransactionOutcome.FailedAtomicityUnavailable,
			_ => throw new ArgumentOutOfRangeException( nameof( outcome ) )
		};
	}

	private static string NormalizePath( string path ) {
		try {
			return System.IO.Path.GetFullPath( path );
		} catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException or PathTooLongException ) {
			return path;
		}
	}

	private static StringComparer HostPathComparer() {
		return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	}

	private sealed record Projection(
		IReadOnlyList<TransactionalReplacementArtifact> Artifacts,
		IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> PatchArtifacts,
		IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> RetainedBackups,
		IReadOnlyDictionary<string, PatchArtifact> DiagnosticArtifacts
	);

	private sealed class FailureInjectorAdapter : ITransactionalReplacementFailureInjector {
		private readonly IPatchTransactionFailureInjector injector;
		private readonly IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> patchArtifacts;
		private readonly IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> retainedBackups;
		private readonly HashSet<TransactionalReplacementArtifact> metadataStages = new();

		/// <summary>Initializes a Patch-to-E6 failure-injection adapter.</summary>
		public FailureInjectorAdapter(
			IPatchTransactionFailureInjector injector,
			IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> patchArtifacts,
			IReadOnlyDictionary<TransactionalReplacementArtifact, PatchArtifact> retainedBackups
		) {
			this.injector = injector;
			this.patchArtifacts = patchArtifacts;
			this.retainedBackups = retainedBackups;
		}

		/// <inheritdoc/>
		public async ValueTask OnStageAsync(
			TransactionalReplacementStage stage,
			TransactionalReplacementArtifact artifact,
			CancellationToken cancellationToken = default
		) {
			var patchArtifact = this.patchArtifacts[artifact];
			if ( TransactionalReplacementStage.ApplyMetadata == stage ) {
				this.metadataStages.Add( artifact );
			}
			if ( TransactionalReplacementStage.FlushDirectory == stage
				&& PatchArtifactAction.Write == patchArtifact.Action
				&& this.metadataStages.Add( artifact ) ) {
				await this.injector.OnStageAsync(
					PatchTransactionStage.ApplyMetadata,
					patchArtifact,
					cancellationToken
				).ConfigureAwait( false );
			}
			if ( TransactionalReplacementStage.PublishBackup == stage
				&& this.retainedBackups.TryGetValue( artifact, out var backup ) ) {
				await this.injector.OnStageAsync(
					PatchTransactionStage.Commit,
					backup,
					cancellationToken
				).ConfigureAwait( false );
				patchArtifact = backup;
			}
			await this.injector.OnStageAsync(
				MapStage( stage ),
				patchArtifact,
				cancellationToken
			).ConfigureAwait( false );
		}

		private static PatchTransactionStage MapStage( TransactionalReplacementStage stage ) {
			return stage switch
			{
				TransactionalReplacementStage.Validate => PatchTransactionStage.Validate,
				TransactionalReplacementStage.CreateTemporary => PatchTransactionStage.CreateTemporary,
				TransactionalReplacementStage.WriteTemporary => PatchTransactionStage.WriteTemporary,
				TransactionalReplacementStage.FlushTemporary => PatchTransactionStage.FlushTemporary,
				TransactionalReplacementStage.PreserveRollback => PatchTransactionStage.PreserveRollback,
				TransactionalReplacementStage.Revalidate => PatchTransactionStage.Revalidate,
				TransactionalReplacementStage.Commit => PatchTransactionStage.Commit,
				TransactionalReplacementStage.ApplyMetadata => PatchTransactionStage.ApplyMetadata,
				TransactionalReplacementStage.PublishBackup => PatchTransactionStage.PublishBackup,
				TransactionalReplacementStage.RestoreMetadata => PatchTransactionStage.RestoreMetadata,
				TransactionalReplacementStage.Rollback => PatchTransactionStage.Rollback,
				TransactionalReplacementStage.Cleanup => PatchTransactionStage.Cleanup,
				TransactionalReplacementStage.FlushDirectory => PatchTransactionStage.FlushDirectory,
				_ => throw new ArgumentOutOfRangeException( nameof( stage ) )
			};
		}
	}
}
