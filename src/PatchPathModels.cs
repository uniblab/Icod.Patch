namespace Icod.Patch;

using System.Collections.ObjectModel;
using Icod.Path;

/// <summary>Identifies the evidence that supplied a candidate target pathname.</summary>
internal enum PatchPathCandidateSource {
	/// <summary>The explicit original-file command operand.</summary>
	ExplicitOperand,
	/// <summary>The old-file diff header.</summary>
	OldHeader,
	/// <summary>The new-file diff header.</summary>
	NewHeader,
	/// <summary>An <c>Index:</c> record in the text preceding a patch.</summary>
	Index
}

/// <summary>Describes one target-name candidate and the evidence used to evaluate it.</summary>
internal sealed class PatchPathCandidate {
	/// <summary>Initializes a target-name candidate.</summary>
	public PatchPathCandidate(
		PatchPathCandidateSource source,
		string originalName,
		string selectedName,
		int ordinal,
		string? canonicalPath,
		bool exists,
		int missingComponentCount,
		CanonicalPathFailure? failure
	) {
		this.Source = source;
		this.OriginalName = originalName ?? throw new ArgumentNullException( nameof( originalName ) );
		this.SelectedName = selectedName ?? throw new ArgumentNullException( nameof( selectedName ) );
		this.Ordinal = ordinal;
		this.CanonicalPath = canonicalPath;
		this.Exists = exists;
		this.MissingComponentCount = missingComponentCount;
		this.Failure = failure;
	}

	/// <summary>Gets the evidence source.</summary>
	public PatchPathCandidateSource Source { get; }
	/// <summary>Gets the decoded name before prefix stripping.</summary>
	public string OriginalName { get; }
	/// <summary>Gets the name after component-aware prefix stripping.</summary>
	public string SelectedName { get; }
	/// <summary>Gets the stable old/new/index ordering position.</summary>
	public int Ordinal { get; }
	/// <summary>Gets the physically canonical candidate path.</summary>
	public string? CanonicalPath { get; }
	/// <summary>Gets whether the candidate exists in live or previously planned state.</summary>
	public bool Exists { get; }
	/// <summary>Gets the admitted missing component count.</summary>
	public int MissingComponentCount { get; }
	/// <summary>Gets the structured rejection reason.</summary>
	public CanonicalPathFailure? Failure { get; }
	/// <summary>Gets whether the candidate survived syntax, security, and path evaluation.</summary>
	public bool IsEligible => null != this.CanonicalPath && null == this.Failure;
}

/// <summary>Identifies how a planned file result changes the selected target.</summary>
internal enum PatchPlannedFileAction {
	/// <summary>An existing file remains present with modified content.</summary>
	Modify,
	/// <summary>A previously missing file becomes present.</summary>
	Create,
	/// <summary>An existing file becomes absent.</summary>
	Delete
}

/// <summary>Contains one path-selection and virtual-application result in patch-stream order.</summary>
internal sealed class PatchFilePlan {
	/// <summary>Initializes one planned or failed file application.</summary>
	public PatchFilePlan(
		PatchFilePatch patch,
		PatchPathCandidate? selectedCandidate,
		IReadOnlyList<PatchPathCandidate> candidates,
		PatchPlannedFileAction? action,
		PatchFileApplicationResult? result,
		PatchExitStatus status,
		bool retrievedFromVersionControl,
		string? failureMessage
	) {
		this.Patch = patch ?? throw new ArgumentNullException( nameof( patch ) );
		ArgumentNullException.ThrowIfNull( candidates );
		if ( PatchExitStatus.Success == status && ( null == selectedCandidate || null == action || null == result ) ) {
			throw new ArgumentException( "a successful file plan requires a selected path, action, and result", nameof( status ) );
		}
		this.SelectedCandidate = selectedCandidate;
		this.Candidates = new ReadOnlyCollection<PatchPathCandidate>( candidates.ToArray() );
		this.Action = action;
		this.Result = result;
		this.Status = status;
		this.RetrievedFromVersionControl = retrievedFromVersionControl;
		this.FailureMessage = failureMessage;
	}

	/// <summary>Gets the parsed source file patch.</summary>
	public PatchFilePatch Patch { get; }
	/// <summary>Gets the selected target candidate, when selection succeeded.</summary>
	public PatchPathCandidate? SelectedCandidate { get; }
	/// <summary>Gets all evaluated candidates in source order.</summary>
	public IReadOnlyList<PatchPathCandidate> Candidates { get; }
	/// <summary>Gets the planned target action.</summary>
	public PatchPlannedFileAction? Action { get; }
	/// <summary>Gets the immutable virtual application result.</summary>
	public PatchFileApplicationResult? Result { get; }
	/// <summary>Gets this file plan's GNU-style status.</summary>
	public PatchExitStatus Status { get; }
	/// <summary>Gets whether input content came from an injected version-control provider.</summary>
	public bool RetrievedFromVersionControl { get; }
	/// <summary>Gets a deterministic planning failure, when present.</summary>
	public string? FailureMessage { get; }
}

/// <summary>Owns all virtual content produced while planning a multi-file patch stream.</summary>
internal sealed class PatchApplicationPlan : IAsyncDisposable {
	private readonly IReadOnlyList<IAsyncDisposable> ownedResources;

	/// <summary>Initializes a multi-file application plan.</summary>
	public PatchApplicationPlan(
		string workingDirectory,
		IReadOnlyList<PatchFilePlan> files,
		PatchExitStatus status,
		IReadOnlyList<IAsyncDisposable> ownedResources
	) {
		this.WorkingDirectory = workingDirectory ?? throw new ArgumentNullException( nameof( workingDirectory ) );
		ArgumentNullException.ThrowIfNull( files );
		ArgumentNullException.ThrowIfNull( ownedResources );
		this.Files = new ReadOnlyCollection<PatchFilePlan>( files.ToArray() );
		this.Status = status;
		this.ownedResources = new ReadOnlyCollection<IAsyncDisposable>( ownedResources.ToArray() );
	}

	/// <summary>Gets the physically canonical <c>-d</c> working root.</summary>
	public string WorkingDirectory { get; }
	/// <summary>Gets planned file applications in patch-stream order.</summary>
	public IReadOnlyList<PatchFilePlan> Files { get; }
	/// <summary>Gets the aggregate GNU-style status.</summary>
	public PatchExitStatus Status { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		for ( var index = this.ownedResources.Count - 1; 0 <= index; index-- ) {
			await this.ownedResources[index].DisposeAsync().ConfigureAwait( false );
		}
	}
}

/// <summary>Provides path observation and read-only target acquisition for P7 planning.</summary>
internal interface IPatchPathFileSystem : ICanonicalPathFileSystemProvider {
	/// <summary>Opens an existing regular file for asynchronous sequential reading.</summary>
	ValueTask<Stream> OpenReadAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Uses the host filesystem for canonical observations and read-only target acquisition.</summary>
internal sealed class SystemPatchPathFileSystem : IPatchPathFileSystem {
	private SystemPatchPathFileSystem() {
	}

	/// <summary>Gets the shared host provider.</summary>
	public static SystemPatchPathFileSystem Instance { get; } = new();
	/// <inheritdoc/>
	public PathPlatformSemantics Semantics => SystemCanonicalPathFileSystemProvider.Instance.Semantics;
	/// <inheritdoc/>
	public string CurrentDirectory => SystemCanonicalPathFileSystemProvider.Instance.CurrentDirectory;
	/// <inheritdoc/>
	public ValueTask<PathComponentObservation> ObserveAsync(
		string path,
		CancellationToken cancellationToken = default
	) => SystemCanonicalPathFileSystemProvider.Instance.ObserveAsync( path, cancellationToken );

	/// <inheritdoc/>
	public ValueTask<Stream> OpenReadAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	) {
		ArgumentException.ThrowIfNullOrEmpty( canonicalPath );
		cancellationToken.ThrowIfCancellationRequested();
		Stream stream = new FileStream(
			canonicalPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			64 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan
		);
		return ValueTask.FromResult( stream );
	}
}

/// <summary>Identifies a version-control retrieval outcome.</summary>
internal enum PatchVersionControlOutcome {
	/// <summary>No revision-control master was found.</summary>
	NotFound,
	/// <summary>The provider supplied file content.</summary>
	Retrieved,
	/// <summary>The provider does not support the discovered system.</summary>
	Unsupported,
	/// <summary>Retrieval was declined by policy.</summary>
	Declined,
	/// <summary>Retrieval failed operationally.</summary>
	Failed
}

/// <summary>Contains a read-only version-control retrieval result.</summary>
internal sealed class PatchVersionControlResult : IAsyncDisposable {
	/// <summary>Initializes a retrieval result.</summary>
	public PatchVersionControlResult(
		PatchVersionControlOutcome outcome,
		Stream? content = null,
		string? message = null
	) {
		if ( PatchVersionControlOutcome.Retrieved == outcome && null == content ) {
			throw new ArgumentNullException( nameof( content ) );
		}
		if ( PatchVersionControlOutcome.Retrieved != outcome && null != content ) {
			throw new ArgumentException( "only a retrieved result may carry content", nameof( content ) );
		}
		this.Outcome = outcome;
		this.Content = content;
		this.Message = message;
	}

	/// <summary>Gets the retrieval outcome.</summary>
	public PatchVersionControlOutcome Outcome { get; }
	/// <summary>Gets retrieved file content.</summary>
	public Stream? Content { get; }
	/// <summary>Gets an optional provider diagnostic.</summary>
	public string? Message { get; }
	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if ( null != this.Content ) {
			await this.Content.DisposeAsync().ConfigureAwait( false );
		}
	}
}

/// <summary>Supplies optional revision-control content without shell interpolation or command coupling.</summary>
internal interface IPatchVersionControlProvider {
	/// <summary>Reports whether a supported revision-control master is available for one missing target.</summary>
	ValueTask<bool> IsRetrievableAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	);

	/// <summary>Retrieves one previously discovered target as a read-only stream.</summary>
	ValueTask<PatchVersionControlResult> RetrieveAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Represents an environment with no configured revision-control retrieval provider.</summary>
internal sealed class NullPatchVersionControlProvider : IPatchVersionControlProvider {
	private NullPatchVersionControlProvider() {
	}

	/// <summary>Gets the shared disabled provider.</summary>
	public static NullPatchVersionControlProvider Instance { get; } = new();
	/// <inheritdoc/>
	public ValueTask<bool> IsRetrievableAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	) {
		ArgumentException.ThrowIfNullOrEmpty( canonicalPath );
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult( false );
	}

	/// <inheritdoc/>
	public ValueTask<PatchVersionControlResult> RetrieveAsync(
		string canonicalPath,
		CancellationToken cancellationToken = default
	) {
		ArgumentException.ThrowIfNullOrEmpty( canonicalPath );
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(
			new PatchVersionControlResult( PatchVersionControlOutcome.NotFound )
		);
	}
}

/// <summary>Configures P7 path selection and multi-file planning.</summary>
internal sealed class PatchPathPlanningOptions {
	/// <summary>Gets or initializes the optional explicit original-file operand.</summary>
	public string? OriginalFile { get; init; }
	/// <summary>Gets or initializes the optional <c>-d</c> directory.</summary>
	public string? Directory { get; init; }
	/// <summary>Gets or initializes an explicit separator-run strip count; <see langword="null"/> selects basenames.</summary>
	public int? StripCount { get; init; }
	/// <summary>Gets or initializes whether filename selection follows POSIX ordering.</summary>
	public bool Posix { get; init; }
	/// <summary>Gets or initializes whether terminal symbolic links may be followed.</summary>
	public bool FollowSymbolicLinks { get; init; }
	/// <summary>Gets or initializes GNU <c>-g</c> retrieval policy.</summary>
	public int Get { get; init; }
	/// <summary>Gets or initializes pure hunk-application policy.</summary>
	public PatchEngineOptions EngineOptions { get; init; } = new();
	/// <summary>Gets or initializes target storage limits.</summary>
	public PatchTargetLimits TargetLimits { get; init; } = PatchTargetLimits.Default;

	/// <summary>Validates the planning options.</summary>
	public void Validate() {
		if ( this.StripCount is < 0 ) {
			throw new ArgumentOutOfRangeException( nameof( this.StripCount ) );
		}
		ArgumentNullException.ThrowIfNull( this.EngineOptions );
		this.EngineOptions.Validate();
		ArgumentNullException.ThrowIfNull( this.TargetLimits );
		this.TargetLimits.Validate();
	}
}
