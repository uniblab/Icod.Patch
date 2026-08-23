namespace Icod.Patch;

using System.Collections.ObjectModel;
using Icod.CommandFramework.FileSystem;
using Icod.CommandFramework.FileSystem.Metadata;
using Icod.CommandFramework.FileSystem.Mutation;
using Icod.CommandFramework.FileSystem.Traversal;
using Icod.CommandFramework.FileSystem.TransactionalReplacement;
using Icod.CommandFramework.Temporary;
using Icod.Path;

/// <summary>Contains the E3 state observed for one potential artifact destination.</summary>
internal sealed class PatchFileObservation {
	/// <summary>Initializes a missing-path observation.</summary>
	public PatchFileObservation( string path ) {
		this.Path = path ?? throw new ArgumentNullException( nameof( path ) );
	}

	/// <summary>Initializes an existing-path observation.</summary>
	public PatchFileObservation( string path, FileSystemMetadata metadata ) {
		this.Path = path ?? throw new ArgumentNullException( nameof( path ) );
		this.Metadata = metadata ?? throw new ArgumentNullException( nameof( metadata ) );
	}

	/// <summary>Gets the observed path.</summary>
	public string Path { get; }
	/// <summary>Gets whether the pathname existed.</summary>
	public bool Exists => null != this.Metadata;
	/// <summary>Gets the authoritative E3 metadata.</summary>
	public FileSystemMetadata? Metadata { get; }
	/// <summary>Gets the effective observed entry kind.</summary>
	public FileSystemEntryKind? Kind => this.Metadata?.Kind;
	/// <summary>Gets the dereference policy represented by the observation.</summary>
	public PathDereferenceMode DereferenceMode => this.Metadata?.WasDereferenced == true
		? PathDereferenceMode.FollowEligiblePathIndirection
		: PathDereferenceMode.NoFollow;
	/// <summary>Gets the portable mode bits when available.</summary>
	public int? Mode => this.Metadata?.Mode.IsAvailable == true
		? checked( (int)(this.Metadata.Mode.GetRequiredValue() & 0x0fffU) )
		: null;
	/// <summary>Gets the access time when available.</summary>
	public DateTimeOffset? AccessTime => this.Metadata?.AccessTime.IsAvailable == true
		? this.Metadata.AccessTime.GetRequiredValue()
		: null;
	/// <summary>Gets the modification time when available.</summary>
	public DateTimeOffset? ModificationTime => this.Metadata?.ModificationTime.IsAvailable == true
		? this.Metadata.ModificationTime.GetRequiredValue()
		: null;
	/// <summary>Gets the numeric owner identifier when available.</summary>
	public uint? UserId => this.Metadata?.UserId.IsAvailable == true
		? this.Metadata.UserId.GetRequiredValue()
		: null;
	/// <summary>Gets the numeric group identifier when available.</summary>
	public uint? GroupId => this.Metadata?.GroupId.IsAvailable == true
		? this.Metadata.GroupId.GetRequiredValue()
		: null;
}

/// <summary>Identifies a testable transaction lifecycle boundary.</summary>
internal enum PatchTransactionStage {
	/// <summary>Before observing and validating destinations.</summary>
	Validate,
	/// <summary>Before creating a secure sibling temporary file.</summary>
	CreateTemporary,
	/// <summary>Before writing staged artifact bytes.</summary>
	WriteTemporary,
	/// <summary>Before flushing staged artifact bytes to stable storage.</summary>
	FlushTemporary,
	/// <summary>Before preserving a rollback copy.</summary>
	PreserveRollback,
	/// <summary>Before revalidating one destination immediately prior to commit.</summary>
	Revalidate,
	/// <summary>Before committing one staged artifact.</summary>
	Commit,
	/// <summary>Before applying mode or timestamp metadata.</summary>
	ApplyMetadata,
	/// <summary>Before publishing a retained backup.</summary>
	PublishBackup,
	/// <summary>Before restoring metadata during rollback.</summary>
	RestoreMetadata,
	/// <summary>Before rolling back a committed artifact.</summary>
	Rollback,
	/// <summary>Before deleting temporary files.</summary>
	Cleanup,
	/// <summary>Before flushing a containing directory.</summary>
	FlushDirectory
}

/// <summary>Injects deterministic failures into the Patch-facing E6 transaction boundary.</summary>
internal interface IPatchTransactionFailureInjector {
	/// <summary>Observes one lifecycle stage and may throw a test exception.</summary>
	ValueTask OnStageAsync(
		PatchTransactionStage stage,
		PatchArtifact artifact,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Represents a transaction without injected failures.</summary>
internal sealed class NullPatchTransactionFailureInjector : IPatchTransactionFailureInjector {
	private NullPatchTransactionFailureInjector() {
	}

	/// <summary>Gets the shared no-op injector.</summary>
	public static NullPatchTransactionFailureInjector Instance { get; } = new();

	/// <inheritdoc/>
	public ValueTask OnStageAsync(
		PatchTransactionStage stage,
		PatchArtifact artifact,
		CancellationToken cancellationToken = default
	) {
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.CompletedTask;
	}
}

/// <summary>Identifies the terminal outcome of one Patch transaction.</summary>
internal enum PatchTransactionOutcome {
	/// <summary>Every transaction unit committed and cleaned up.</summary>
	Succeeded,
	/// <summary>No transaction unit committed.</summary>
	FailedBeforeCommit,
	/// <summary>The failing transaction unit was fully rolled back.</summary>
	FailedRolledBack,
	/// <summary>Earlier transaction units committed before a later unit failed and was rolled back.</summary>
	FailedPartiallyCommitted,
	/// <summary>Rollback did not completely recover the failing transaction unit.</summary>
	FailedRollbackIncomplete,
	/// <summary>Commit completed but deterministic temporary cleanup was incomplete.</summary>
	FailedCleanupIncomplete,
	/// <summary>Atomic publication was mandatory but unavailable.</summary>
	FailedAtomicityUnavailable
}

/// <summary>Contains the result of one Patch artifact transaction.</summary>
internal sealed class PatchTransactionResult {
	/// <summary>Initializes a legacy success-or-failure transaction result.</summary>
	public PatchTransactionResult(
		bool succeeded,
		IReadOnlyList<string> diagnostics,
		Exception? exception = null
	) : this(
		succeeded ? PatchTransactionOutcome.Succeeded : PatchTransactionOutcome.FailedBeforeCommit,
		diagnostics,
		exception: exception
	) {
	}

	/// <summary>Initializes a detailed transaction result.</summary>
	public PatchTransactionResult(
		PatchTransactionOutcome outcome,
		IReadOnlyList<string> diagnostics,
		IReadOnlyList<string>? committedUnitIds = null,
		IReadOnlyList<string>? rolledBackUnitIds = null,
		Exception? exception = null
	) {
		ArgumentNullException.ThrowIfNull( diagnostics );
		this.Outcome = outcome;
		this.Diagnostics = new ReadOnlyCollection<string>( diagnostics.ToArray() );
		this.CommittedUnitIds = new ReadOnlyCollection<string>(
			(committedUnitIds ?? Array.Empty<string>()).ToArray()
		);
		this.RolledBackUnitIds = new ReadOnlyCollection<string>(
			(rolledBackUnitIds ?? Array.Empty<string>()).ToArray()
		);
		this.Exception = exception;
	}

	/// <summary>Gets whether every requested transaction unit committed and cleaned up successfully.</summary>
	public bool Succeeded => PatchTransactionOutcome.Succeeded == this.Outcome;
	/// <summary>Gets the terminal transaction outcome.</summary>
	public PatchTransactionOutcome Outcome { get; }
	/// <summary>Gets deterministic controlled diagnostics.</summary>
	public IReadOnlyList<string> Diagnostics { get; }
	/// <summary>Gets transaction units that remain committed.</summary>
	public IReadOnlyList<string> CommittedUnitIds { get; }
	/// <summary>Gets transaction units recovered after a failed commit attempt.</summary>
	public IReadOnlyList<string> RolledBackUnitIds { get; }
	/// <summary>Gets whether at least one earlier patch-file unit remains committed.</summary>
	public bool HasPartialCommit => 0 < this.CommittedUnitIds.Count && !this.Succeeded;
	/// <summary>Gets the underlying operational exception.</summary>
	public Exception? Exception { get; }
}

/// <summary>Models one staged Patch transaction.</summary>
internal interface IPatchTransaction : IAsyncDisposable {
	/// <summary>Stages every artifact before any destination is changed.</summary>
	Task StageAsync( CancellationToken cancellationToken = default );

	/// <summary>Commits staged artifacts and rolls back completed changes after a later failure.</summary>
	Task<PatchTransactionResult> CommitAsync( CancellationToken cancellationToken = default );
}

/// <summary>Provides the Patch-facing E2-through-E6 filesystem boundary.</summary>
internal interface IPatchFileSystem {
	/// <summary>Gets the frozen Patch-facing Completion Gate E6 requirements.</summary>
	PatchE6TransactionContract TransactionContract => PatchE6TransactionContract.Current;

	/// <summary>Gets the Patch-facing transaction capability profile.</summary>
	TransactionalReplacementCapabilities TransactionCapabilities => SystemTransactionalReplacementFileSystem.Instance.Capabilities;

	/// <summary>Observes one artifact path using explicit terminal-indirection policy.</summary>
	ValueTask<PatchFileObservation> ObserveAsync(
		string path,
		bool followPathIndirection,
		CancellationToken cancellationToken = default
	);

	/// <summary>Resolves one user-selected artifact pathname under explicit final-indirection policy.</summary>
	ValueTask<string> ResolveArtifactPathAsync(
		string path,
		string workingDirectory,
		bool followPathIndirection,
		CancellationToken cancellationToken = default
	);

	/// <summary>Creates a transaction over an immutable artifact plan.</summary>
	ValueTask<IPatchTransaction> CreateTransactionAsync(
		PatchArtifactPlan plan,
		IPatchTransactionFailureInjector? failureInjector = null,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Uses Completion Gates E2 through E6 for path resolution, observation, mutation, and replacement.</summary>
internal sealed class SystemPatchFileSystem : IPatchFileSystem {
	private readonly IFileSystemMetadataProvider metadataProvider;
	private readonly ITransactionalReplacementFileSystem replacementFileSystem;
	private readonly CanonicalPathResolver pathResolver;

	/// <inheritdoc/>
	public PatchE6TransactionContract TransactionContract => PatchE6TransactionContract.Current;

	/// <inheritdoc/>
	public TransactionalReplacementCapabilities TransactionCapabilities => this.replacementFileSystem.Capabilities;

	/// <summary>Initializes the host adapter.</summary>
	public SystemPatchFileSystem()
		: this( SystemFileSystemMetadataProvider.Instance, SystemFileSystemMutationProvider.Instance ) {
	}

	/// <summary>Initializes an adapter over injected E3 and E4 providers.</summary>
	public SystemPatchFileSystem(
		IFileSystemMetadataProvider metadataProvider,
		IFileSystemMutationProvider mutationProvider
	) : this(
		metadataProvider,
		mutationProvider,
		new SystemTransactionalReplacementFileSystem(
			metadataProvider,
			mutationProvider,
			SystemFileSystemOperations.Instance,
			SecureTemporaryObjectCreator.System
		)
	) {
	}

	/// <summary>Initializes an adapter over injected E3, E4, and E6 providers.</summary>
	public SystemPatchFileSystem(
		IFileSystemMetadataProvider metadataProvider,
		IFileSystemMutationProvider mutationProvider,
		ITransactionalReplacementFileSystem replacementFileSystem
	) {
		this.metadataProvider = metadataProvider ?? throw new ArgumentNullException( nameof( metadataProvider ) );
		ArgumentNullException.ThrowIfNull( mutationProvider );
		this.replacementFileSystem = replacementFileSystem ?? throw new ArgumentNullException( nameof( replacementFileSystem ) );
		this.pathResolver = new CanonicalPathResolver( SystemCanonicalPathFileSystemProvider.Instance );
	}

	/// <inheritdoc/>
	public async ValueTask<PatchFileObservation> ObserveAsync(
		string path,
		bool followPathIndirection,
		CancellationToken cancellationToken = default
	) {
		ArgumentException.ThrowIfNullOrEmpty( path );
		try {
			var metadata = await this.metadataProvider.GetMetadataAsync(
				path,
				followPathIndirection
					? PathDereferenceMode.FollowEligiblePathIndirection
					: PathDereferenceMode.NoFollow,
				cancellationToken
			).ConfigureAwait( false );
			return new PatchFileObservation( path, metadata );
		} catch ( FileNotFoundException ) {
			return new PatchFileObservation( path );
		} catch ( DirectoryNotFoundException ) {
			return new PatchFileObservation( path );
		}
	}

	/// <inheritdoc/>
	public async ValueTask<string> ResolveArtifactPathAsync(
		string path,
		string workingDirectory,
		bool followPathIndirection,
		CancellationToken cancellationToken = default
	) {
		ArgumentException.ThrowIfNullOrEmpty( path );
		ArgumentException.ThrowIfNullOrEmpty( workingDirectory );
		if ( 0 <= path.IndexOfAny( new[] { '\r', '\n' } ) ) {
			throw new PatchApplicationException( "an artifact pathname cannot contain a newline" );
		}
		var lexicalRoot = this.pathResolver.NormalizeLexically(
			workingDirectory,
			Directory.GetCurrentDirectory()
		);
		if ( !lexicalRoot.Succeeded ) {
			throw new PatchApplicationException( FormatPathFailure( lexicalRoot.Failure! ) );
		}
		var physicalRoot = await this.pathResolver.ResolvePhysicalAsync(
			lexicalRoot.Path!,
			new CanonicalPathResolutionOptions {
				BasePath = Directory.GetCurrentDirectory(),
				MissingComponentPolicy = MissingPathComponentPolicy.RequireExisting,
				FollowSymbolicLinks = true,
				RequireFinalDirectory = true
			},
			cancellationToken
		).ConfigureAwait( false );
		if ( !physicalRoot.Succeeded ) {
			throw new PatchApplicationException( FormatPathFailure( physicalRoot.Failure! ) );
		}
		var lexical = this.pathResolver.NormalizeLexically( path, lexicalRoot.Path! );
		if ( !lexical.Succeeded ) {
			throw new PatchApplicationException( FormatPathFailure( lexical.Failure! ) );
		}
		EnsureContained( this.pathResolver, lexicalRoot.Path!, lexical.Path!, "artifact pathname" );
		if ( !followPathIndirection ) {
			var inspection = await this.pathResolver.InspectLinkAsync(
				lexical.Path!,
				lexicalRoot.Path!,
				cancellationToken
			).ConfigureAwait( false );
			if ( inspection.Succeeded && (inspection.IsSymbolicLink || inspection.IsReparsePoint) ) {
				throw new PatchApplicationException(
					string.Concat( lexical.Path, ": artifact pathname is a link or reparse point; use --follow-symlinks to follow it" )
				);
			}
			if ( !inspection.Succeeded
				&& inspection.Failure!.Code is not CanonicalPathFailureCode.NotFound ) {
				throw new PatchApplicationException( FormatPathFailure( inspection.Failure ) );
			}
		}
		var physical = await this.pathResolver.ResolvePhysicalAsync(
			lexical.Path!,
			new CanonicalPathResolutionOptions {
				BasePath = physicalRoot.Path!,
				MissingComponentPolicy = MissingPathComponentPolicy.AllowMissingSuffix,
				FollowSymbolicLinks = true,
				FollowFinalSymbolicLink = followPathIndirection
			},
			cancellationToken
		).ConfigureAwait( false );
		if ( !physical.Succeeded ) {
			throw new PatchApplicationException( FormatPathFailure( physical.Failure! ) );
		}
		EnsureContained( this.pathResolver, physicalRoot.Path!, physical.Path!, "resolved artifact pathname" );
		return physical.Path!;
	}

	private static void EnsureContained(
		CanonicalPathResolver resolver,
		string workingDirectory,
		string path,
		string description
	) {
		var containment = resolver.EvaluateContainment( workingDirectory, path );
		if ( !containment.Succeeded ) {
			throw new PatchApplicationException( FormatPathFailure( containment.Failure! ) );
		}
		if ( !containment.IsContained ) {
			throw new PatchApplicationException(
				string.Concat( path, ": ", description, " escapes the patch working directory" )
			);
		}
	}

	private static string FormatPathFailure( CanonicalPathFailure failure ) {
		return string.Concat( failure.Path, ": ", failure.Message );
	}

	/// <inheritdoc/>
	public ValueTask<IPatchTransaction> CreateTransactionAsync(
		PatchArtifactPlan plan,
		IPatchTransactionFailureInjector? failureInjector = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( plan );
		cancellationToken.ThrowIfCancellationRequested();
		IPatchTransaction transaction = new PatchE6Transaction(
			plan,
			this.replacementFileSystem,
			failureInjector ?? NullPatchTransactionFailureInjector.Instance
		);
		return ValueTask.FromResult( transaction );
	}
}
