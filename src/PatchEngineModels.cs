namespace Icod.Patch;

using System.Collections.ObjectModel;

/// <summary>Identifies the direction used to apply a file patch.</summary>
internal enum PatchDirection {
	/// <summary>Apply the patch as written.</summary>
	Forward,
	/// <summary>Exchange the old and new sides before application.</summary>
	Reverse
}

/// <summary>Identifies optional three-way conflict output.</summary>
internal enum PatchMergeStyle {
	/// <summary>Do not synthesize conflict markers.</summary>
	None,
	/// <summary>Write two-way merge conflict markers.</summary>
	Merge,
	/// <summary>Write diff3 conflict markers including the common ancestor.</summary>
	Diff3
}

/// <summary>Identifies a policy question raised by the pure application engine.</summary>
internal enum PatchDecisionKind {
	/// <summary>The first hunk appears to be reversed or already applied.</summary>
	ReversePatch,
	/// <summary>A prerequisite token was not found.</summary>
	IgnoreMissingPrerequisite,
	/// <summary>A missing file may be retrieved from version control.</summary>
	RetrieveFromVersionControl
}

/// <summary>Describes a policy decision requested by the application engine.</summary>
internal sealed class PatchDecisionRequest {
	/// <summary>Initializes a decision request.</summary>
	/// <param name="kind">The decision kind.</param>
	/// <param name="message">The user-facing question context.</param>
	public PatchDecisionRequest( PatchDecisionKind kind, string message ) {
		this.Kind = kind;
		this.Message = message ?? throw new ArgumentNullException( nameof( message ) );
	}

	/// <summary>Gets the requested decision kind.</summary>
	public PatchDecisionKind Kind { get; }

	/// <summary>Gets the question context.</summary>
	public string Message { get; }
}

/// <summary>Supplies interactive policy decisions without coupling the engine to console input.</summary>
internal interface IPatchDecisionProvider {
	/// <summary>Answers one application-policy question.</summary>
	/// <param name="request">The decision request.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns><see langword="true"/> to accept the proposed action.</returns>
	ValueTask<bool> DecideAsync(
		PatchDecisionRequest request,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Defines bounded work limits for target matching and result construction.</summary>
internal sealed class PatchApplicationLimits {
	/// <summary>Gets the default application limits.</summary>
	public static PatchApplicationLimits Default { get; } = new();

	/// <summary>Gets or initializes the maximum candidate positions examined for one hunk across all fuzz levels.</summary>
	public int MaximumCandidateChecks { get; init; } = 1_000_000;

	/// <summary>Gets or initializes the maximum output byte count.</summary>
	public long MaximumOutputBytes { get; init; } = 1024L * 1024L * 1024L;

	/// <summary>Gets or initializes target-content storage limits for the immutable result.</summary>
	public PatchTargetLimits TargetLimits { get; init; } = PatchTargetLimits.Default;

	/// <summary>Validates the limits.</summary>
	public void Validate() {
		if ( this.MaximumCandidateChecks < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumCandidateChecks ) );
		}
		if ( this.MaximumOutputBytes < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumOutputBytes ) );
		}
		ArgumentNullException.ThrowIfNull( this.TargetLimits );
		this.TargetLimits.Validate();
	}
}

/// <summary>Contains policy and matching options for one pure file-patch application.</summary>
internal sealed class PatchEngineOptions {
	/// <summary>Gets or initializes whether reverse direction is explicitly requested.</summary>
	public bool Reverse { get; init; }

	/// <summary>Gets or initializes whether automatic reversal is forbidden.</summary>
	public bool Force { get; init; }

	/// <summary>Gets or initializes whether reversed or already-applied patches are skipped.</summary>
	public bool ForwardOnly { get; init; }

	/// <summary>Gets or initializes whether policy questions use noninteractive GNU batch defaults.</summary>
	public bool Batch { get; init; }

	/// <summary>Gets or initializes the maximum context fuzz factor.</summary>
	public int Fuzz { get; init; } = 2;

	/// <summary>Gets or initializes whether horizontal blank runs are compared canonically.</summary>
	public bool IgnoreWhitespace { get; init; }

	/// <summary>Gets or initializes optional conflict-marker output.</summary>
	public PatchMergeStyle MergeStyle { get; init; }

	/// <summary>Gets or initializes a prerequisite token extracted from leading patch text.</summary>
	public string? PrerequisiteToken { get; init; }

	/// <summary>Gets or initializes the decision provider for interactive policy.</summary>
	public IPatchDecisionProvider? DecisionProvider { get; init; }

	/// <summary>Gets or initializes bounded application limits.</summary>
	public PatchApplicationLimits Limits { get; init; } = PatchApplicationLimits.Default;

	/// <summary>Validates the configured options.</summary>
	public void Validate() {
		if ( this.Fuzz < 0 ) {
			throw new ArgumentOutOfRangeException( nameof( this.Fuzz ) );
		}
		ArgumentNullException.ThrowIfNull( this.Limits );
		this.Limits.Validate();
	}
}

/// <summary>Identifies the outcome of one hunk.</summary>
internal enum PatchHunkOutcome {
	/// <summary>The hunk was applied exactly or at a nearby offset.</summary>
	Applied,
	/// <summary>The hunk was represented by conflict markers.</summary>
	Merged,
	/// <summary>The hunk was skipped as reversed or already applied.</summary>
	Skipped,
	/// <summary>The hunk could not be matched.</summary>
	Failed
}

/// <summary>Contains the immutable result for one hunk.</summary>
internal sealed class PatchHunkResult {
	/// <summary>Initializes a hunk result.</summary>
	/// <param name="hunk">The source hunk.</param>
	/// <param name="outcome">The outcome.</param>
	/// <param name="appliedIndex">The zero-based applied index, when available.</param>
	/// <param name="offset">The line offset from the predicted position.</param>
	/// <param name="fuzz">The fuzz factor used.</param>
	public PatchHunkResult(
		PatchHunk hunk,
		PatchHunkOutcome outcome,
		int? appliedIndex,
		long offset,
		int fuzz
	) {
		this.Hunk = hunk ?? throw new ArgumentNullException( nameof( hunk ) );
		this.Outcome = outcome;
		this.AppliedIndex = appliedIndex;
		this.Offset = offset;
		this.Fuzz = fuzz;
	}

	/// <summary>Gets the source hunk.</summary>
	public PatchHunk Hunk { get; }

	/// <summary>Gets the hunk outcome.</summary>
	public PatchHunkOutcome Outcome { get; }

	/// <summary>Gets the zero-based applied index, when available.</summary>
	public int? AppliedIndex { get; }

	/// <summary>Gets the line offset from the predicted position.</summary>
	public long Offset { get; }

	/// <summary>Gets the context fuzz factor used.</summary>
	public int Fuzz { get; }
}

/// <summary>Contains an immutable virtual file and its indexed bytes.</summary>
internal sealed class PatchVirtualFile : IAsyncDisposable {
	/// <summary>Initializes a virtual file.</summary>
	/// <param name="exists">Whether the file exists.</param>
	/// <param name="content">The content when the file exists.</param>
	public PatchVirtualFile( bool exists, PatchTargetContent? content ) {
		if ( exists && null == content ) {
			throw new ArgumentNullException( nameof( content ) );
		}
		if ( !exists && null != content ) {
			throw new ArgumentException( "a missing virtual file cannot carry content", nameof( content ) );
		}
		this.Exists = exists;
		this.Content = content;
	}

	/// <summary>Gets whether the virtual file exists.</summary>
	public bool Exists { get; }

	/// <summary>Gets the indexed content when the virtual file exists.</summary>
	public PatchTargetContent? Content { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if ( null != this.Content ) {
			await this.Content.DisposeAsync().ConfigureAwait( false );
		}
	}
}

/// <summary>Contains the immutable result of applying one parsed file patch.</summary>
internal sealed class PatchFileApplicationResult : IAsyncDisposable {
	/// <summary>Initializes a file-application result.</summary>
	/// <param name="file">The resulting virtual file.</param>
	/// <param name="direction">The selected direction.</param>
	/// <param name="hunks">The hunk results.</param>
	/// <param name="status">The GNU-style status category.</param>
	public PatchFileApplicationResult(
		PatchVirtualFile file,
		PatchDirection direction,
		IReadOnlyList<PatchHunkResult> hunks,
		PatchExitStatus status
	) {
		this.File = file ?? throw new ArgumentNullException( nameof( file ) );
		this.Direction = direction;
		ArgumentNullException.ThrowIfNull( hunks );
		this.Hunks = new ReadOnlyCollection<PatchHunkResult>( hunks.ToArray() );
		this.Status = status;
	}

	/// <summary>Gets the resulting virtual file.</summary>
	public PatchVirtualFile File { get; }

	/// <summary>Gets the selected direction.</summary>
	public PatchDirection Direction { get; }

	/// <summary>Gets the immutable hunk results.</summary>
	public IReadOnlyList<PatchHunkResult> Hunks { get; }

	/// <summary>Gets the GNU-style status category.</summary>
	public PatchExitStatus Status { get; }

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => this.File.DisposeAsync();
}

/// <summary>Represents bounded or semantically invalid application work.</summary>
internal sealed class PatchApplicationException : Exception {
	/// <summary>Initializes an application exception.</summary>
	/// <param name="message">The diagnostic message.</param>
	public PatchApplicationException( string message )
		: base( message ) {
	}
}
