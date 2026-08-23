namespace Icod.Patch;

using System.Security;
using Icod.CommandFramework.Diagnostics;

/// <summary>Contains validated Patch Wave C invocation options.</summary>
internal sealed class PatchOptions {
	/// <summary>Gets or initializes the optional original-file operand.</summary>
	public string? OriginalFile { get; init; }

	/// <summary>Gets or initializes the selected patch source, or <see langword="null"/> for standard input.</summary>
	public string? PatchFile { get; init; }


	/// <summary>Gets or initializes the optional working directory selected by <c>-d</c>.</summary>
	public string? Directory { get; init; }

	/// <summary>Gets or initializes the explicit component strip count, or <see langword="null"/> for basename selection.</summary>
	public int? StripCount { get; init; }

	/// <summary>Gets or initializes whether POSIX filename-selection policy is active.</summary>
	public bool Posix { get; init; }

	/// <summary>Gets or initializes whether target symbolic links are followed.</summary>
	public bool FollowSymbolicLinks { get; init; }

	/// <summary>Gets or initializes GNU version-control retrieval policy.</summary>
	public int Get { get; init; }

	/// <summary>Gets or initializes whether binary mode was requested.</summary>
	public bool Binary { get; init; }

	/// <summary>Gets or initializes an explicitly selected input format.</summary>
	public PatchFormat? ForcedFormat { get; init; }

	/// <summary>Gets or initializes whether automatic reversal is suppressed.</summary>
	public bool Force { get; init; }

	/// <summary>Gets or initializes whether reversed or already-applied patches are skipped.</summary>
	public bool ForwardOnly { get; init; }

	/// <summary>Gets or initializes whether reverse application is explicit.</summary>
	public bool Reverse { get; init; }

	/// <summary>Gets or initializes whether interactive questions use batch defaults.</summary>
	public bool Batch { get; init; }

	/// <summary>Gets or initializes the maximum context fuzz factor.</summary>
	public int Fuzz { get; init; } = 2;

	/// <summary>Gets or initializes whether horizontal blank runs compare canonically.</summary>
	public bool IgnoreWhitespace { get; init; }

	/// <summary>Gets or initializes optional merge-conflict output.</summary>
	public PatchMergeStyle MergeStyle { get; init; }

	/// <summary>Gets or initializes whether every changed existing target receives a backup.</summary>
	public bool Backup { get; init; }

	/// <summary>Gets or initializes whether mismatched applications receive a backup.</summary>
	public bool? BackupIfMismatch { get; init; }

	/// <summary>Gets or initializes a prefix applied to the complete backup pathname.</summary>
	public string? BackupPrefix { get; init; }

	/// <summary>Gets or initializes a prefix applied only to the backup basename.</summary>
	public string? BackupBasenamePrefix { get; init; }

	/// <summary>Gets or initializes the simple backup suffix.</summary>
	public string BackupSuffix { get; init; } = ".orig";

	/// <summary>Gets or initializes whether <c>--suffix</c> explicitly selected simple backup naming.</summary>
	public bool BackupSuffixSpecified { get; init; }

	/// <summary>Gets or initializes the backup version-selection policy.</summary>
	public PatchBackupVersionControl BackupVersionControl { get; init; } = PatchBackupVersionControl.Existing;

	/// <summary>Gets or initializes the explicit reject destination.</summary>
	public string? RejectFile { get; init; }

	/// <summary>Gets or initializes reject serialization policy.</summary>
	public PatchRejectFormat RejectFormat { get; init; } = PatchRejectFormat.Automatic;

	/// <summary>Gets or initializes the alternate patched-output destination.</summary>
	public string? OutputFile { get; init; }

	/// <summary>Gets or initializes whether empty patched files are removed.</summary>
	public bool RemoveEmptyFiles { get; init; }

	/// <summary>Gets or initializes whether artifact planning runs without mutation.</summary>
	public bool DryRun { get; init; }

	/// <summary>Gets or initializes command diagnostic verbosity.</summary>
	public PatchVerbosity Verbosity { get; init; } = PatchVerbosity.Normal;

	/// <summary>Gets or initializes filename diagnostic quoting.</summary>
	public PatchQuotingStyle QuotingStyle { get; init; } = PatchQuotingStyle.Shell;

	/// <summary>Gets or initializes whether target timestamps come from patch headers.</summary>
	public bool SetTime { get; init; }

	/// <summary>Gets or initializes whether patch-header timestamps are interpreted as UTC.</summary>
	public bool SetUtc { get; init; }

	/// <summary>Gets whether later interactive prompts may own standard input.</summary>
	public bool PromptInputAvailable => null != this.PatchFile && "-" != this.PatchFile;
}

/// <summary>Coordinates source acquisition, P7 planning, P8 artifacts, and the E6-backed P11A transaction.</summary>
internal static class PatchApplication {
	/// <summary>Parses the selected patch source without mutating target files.</summary>
	/// <param name="options">The validated invocation options.</param>
	/// <param name="context">The command context.</param>
	/// <param name="planner">An optional injected path planner.</param>
	/// <param name="fileSystem">An optional injected Patch filesystem and transaction boundary.</param>
	/// <param name="failureInjector">An optional transaction failure injector used by deterministic tests.</param>
	/// <returns>The process status.</returns>
	public static async Task<int> ExecuteAsync(
		PatchOptions options,
		CommandContext context,
		PatchApplicationPlanner? planner = null,
		IPatchFileSystem? fileSystem = null,
		IPatchTransactionFailureInjector? failureInjector = null
	) {
		ArgumentNullException.ThrowIfNull( options );
		ArgumentNullException.ThrowIfNull( context );
		planner ??= new PatchApplicationPlanner();
		fileSystem ??= new SystemPatchFileSystem();
		Stream? ownedInput = null;
		try {
			var input = context.StandardInputStream;
			if ( null != options.PatchFile && "-" != options.PatchFile ) {
				ownedInput = new FileStream(
					ResolvePatchFilePath( options ),
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					64 * 1024,
					FileOptions.Asynchronous | FileOptions.SequentialScan
				);
				input = ownedInput;
			}
			if ( null == input ) {
				throw new InvalidOperationException( "a binary standard-input stream was not supplied" );
			}
			await using var source = await PatchSource.ReadAsync(
				input,
				PatchScanLimits.Default,
				context.CancellationToken
			).ConfigureAwait( false );
			var result = PatchScanner.Detect(
				source.Records,
				source.Probes,
				options.ForcedFormat
			);
			if ( !result.HasPatch ) {
				await context.Diagnostics.ErrorAsync(
					"Only garbage was found in the patch input.",
					context.CancellationToken
				).ConfigureAwait( false );
				return (int)PatchExitStatus.Trouble;
			}
			var document = await PatchDocumentParser.ParseAsync(
				source,
				result,
				PatchParseLimits.Default,
				context.CancellationToken
			).ConfigureAwait( false );
			IPatchDecisionProvider? decisionProvider = null;
			if ( options.PromptInputAvailable && !options.Batch && !options.Force ) {
				decisionProvider = new CommandPatchDecisionProvider( context );
			}
			await using var plan = await planner.BuildAsync(
				source,
				document,
				new PatchPathPlanningOptions {
					OriginalFile = options.OriginalFile,
					Directory = options.Directory,
					StripCount = options.StripCount,
					Posix = options.Posix,
					FollowSymbolicLinks = options.FollowSymbolicLinks,
					Get = options.Get,
					EngineOptions = CreateEngineOptions( options, prerequisiteToken: null, decisionProvider )
				},
				context.CancellationToken
			).ConfigureAwait( false );
			foreach ( var file in plan.Files.Where( value => null != value.FailureMessage ) ) {
				await context.Diagnostics.ErrorAsync(
					file.FailureMessage!,
					context.CancellationToken
				).ConfigureAwait( false );
			}
			var artifactPlanner = new PatchArtifactPlanner( fileSystem );
			var artifactPlan = await artifactPlanner.BuildAsync(
				plan,
				options,
				context.CancellationToken
			).ConfigureAwait( false );
			if ( PatchVerbosity.Verbose == options.Verbosity ) {
				foreach ( var diagnostic in artifactPlan.Diagnostics ) {
					await context.Diagnostics.ErrorAsync(
						diagnostic,
						context.CancellationToken
					).ConfigureAwait( false );
				}
			}
			if ( options.DryRun ) {
				if ( PatchVerbosity.Quiet != options.Verbosity ) {
					await context.Diagnostics.ErrorAsync(
						string.Concat(
							"dry run: planned ",
							artifactPlan.Artifacts.Count(
								artifact => PatchArtifactAction.ValidateOnly != artifact.Action
							).ToString( System.Globalization.CultureInfo.InvariantCulture ),
							" artifact operation(s)"
						),
						context.CancellationToken
					).ConfigureAwait( false );
				}
				return (int)artifactPlan.Status;
			}
			await using var transaction = await fileSystem.CreateTransactionAsync(
				artifactPlan,
				failureInjector,
				context.CancellationToken
			).ConfigureAwait( false );
			await transaction.StageAsync( context.CancellationToken ).ConfigureAwait( false );
			foreach ( var outputArtifact in artifactPlan.Artifacts.Where(
				artifact => PatchArtifactAction.WriteStandardOutput == artifact.Action
			) ) {
				if ( null == context.StandardOutputStream ) {
					await context.Diagnostics.ErrorAsync(
						"binary standard output is unavailable for --output=-",
						CancellationToken.None
					).ConfigureAwait( false );
					return (int)PatchExitStatus.Trouble;
				}
				try {
					await outputArtifact.Content!.WriteToAsync(
						context.StandardOutputStream,
						context.CancellationToken
					).ConfigureAwait( false );
					await context.StandardOutputStream.FlushAsync( context.CancellationToken ).ConfigureAwait( false );
				} catch ( IOException exception ) {
					await context.Diagnostics.ErrorAsync( exception.Message, CancellationToken.None ).ConfigureAwait( false );
					return (int)PatchExitStatus.Trouble;
				}
			}
			var transactionResult = await transaction.CommitAsync(
				context.CancellationToken
			).ConfigureAwait( false );
			foreach ( var diagnostic in transactionResult.Diagnostics ) {
				await context.Diagnostics.ErrorAsync(
					diagnostic,
					CancellationToken.None
				).ConfigureAwait( false );
			}
			if ( !transactionResult.Succeeded ) {
				return (int)PatchExitStatus.Trouble;
			}
			if ( PatchVerbosity.Quiet != options.Verbosity ) {
				foreach ( var artifact in artifactPlan.Artifacts.Where(
					artifact => PatchArtifactAction.ValidateOnly != artifact.Action
				) ) {
					await context.Diagnostics.ErrorAsync(
						FormatArtifactDiagnostic( artifact ),
						context.CancellationToken
					).ConfigureAwait( false );
				}
			}
			return (int)artifactPlan.Status;
		} catch ( PatchInputException exception ) {
			await context.Diagnostics.ErrorAsync(
				string.Concat(
					"patch input line ",
					exception.Location.LineNumber.ToString( System.Globalization.CultureInfo.InvariantCulture ),
					": ",
					exception.Message
				),
				CancellationToken.None
			).ConfigureAwait( false );
			return (int)PatchExitStatus.Trouble;
		} finally {
			if ( null != ownedInput ) {
				await ownedInput.DisposeAsync().ConfigureAwait( false );
			}
		}
	}

	/// <summary>Formats one successful artifact diagnostic.</summary>
	private static string FormatArtifactDiagnostic( PatchArtifact artifact ) {
		var verb = artifact.Action switch {
			PatchArtifactAction.Delete => "removed",
			_ when PatchArtifactKind.Backup == artifact.Kind => "saved backup",
			_ when PatchArtifactKind.Reject == artifact.Kind => "saved rejects",
			_ when PatchArtifactKind.Output == artifact.Kind => "wrote output",
			_ => "patched"
		};
		return string.Concat( verb, " ", artifact.DisplayName );
	}

	/// <summary>Resolves a relative patch-source argument as though <c>-d</c> changed directory first.</summary>
	private static string ResolvePatchFilePath( PatchOptions options ) {
		var patchFile = options.PatchFile!;
		if ( System.IO.Path.IsPathFullyQualified( patchFile ) || null == options.Directory ) {
			return patchFile;
		}
		var directory = System.IO.Path.GetFullPath( options.Directory );
		return System.IO.Path.GetFullPath( patchFile, directory );
	}

	/// <summary>Maps validated command options into pure application-engine policy.</summary>
	/// <param name="options">The validated command options.</param>
	/// <param name="prerequisiteToken">The optional prerequisite token from leading patch text.</param>
	/// <param name="decisionProvider">An optional interactive decision provider.</param>
	/// <returns>The corresponding engine options.</returns>
	public static PatchEngineOptions CreateEngineOptions(
		PatchOptions options,
		string? prerequisiteToken,
		IPatchDecisionProvider? decisionProvider = null
	) {
		ArgumentNullException.ThrowIfNull( options );
		return new PatchEngineOptions {
			Reverse = options.Reverse,
			Force = options.Force,
			ForwardOnly = options.ForwardOnly,
			Batch = options.Batch,
			Fuzz = options.Fuzz,
			IgnoreWhitespace = options.IgnoreWhitespace,
			MergeStyle = options.MergeStyle,
			PrerequisiteToken = prerequisiteToken,
			DecisionProvider = decisionProvider
		};
	}

	/// <summary>Determines whether an exception represents an expected operational failure.</summary>
	/// <param name="exception">The exception to classify.</param>
	/// <returns><see langword="true"/> for a controlled operational failure.</returns>
	public static bool IsOperationalException( Exception exception ) {
		return exception is IOException
			or PatchApplicationException
			or UnauthorizedAccessException
			or ArgumentException
			or InvalidOperationException
			or NotSupportedException
			or SecurityException;
	}
}
