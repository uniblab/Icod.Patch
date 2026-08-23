namespace Icod.Patch;

using Icod.Path;

/// <summary>Combines parsed patch files with E2 canonical paths and the pure P5/P6 application engine.</summary>
internal sealed class PatchApplicationPlanner {
	private sealed class EvaluatedCandidate {
		/// <summary>Initializes an evaluated candidate.</summary>
		public EvaluatedCandidate( PatchPathCandidate candidate, PatchVirtualFile? virtualState ) {
			this.Candidate = candidate;
			this.VirtualState = virtualState;
		}
		/// <summary>Gets the public evidence record.</summary>
		public PatchPathCandidate Candidate { get; }
		/// <summary>Gets prior planned state for the canonical target.</summary>
		public PatchVirtualFile? VirtualState { get; }
	}

	private readonly IPatchPathFileSystem fileSystem;
	private readonly CanonicalPathResolver resolver;
	private readonly IPatchVersionControlProvider versionControl;

	/// <summary>Initializes a planner over the host filesystem.</summary>
	public PatchApplicationPlanner()
		: this( SystemPatchPathFileSystem.Instance, NullPatchVersionControlProvider.Instance ) {
	}

	/// <summary>Initializes a planner over injected path and version-control providers.</summary>
	public PatchApplicationPlanner(
		IPatchPathFileSystem fileSystem,
		IPatchVersionControlProvider? versionControl = null
	) {
		this.fileSystem = fileSystem ?? throw new ArgumentNullException( nameof( fileSystem ) );
		this.resolver = new CanonicalPathResolver( fileSystem );
		this.versionControl = versionControl ?? NullPatchVersionControlProvider.Instance;
	}

	/// <summary>Builds a multi-file virtual application plan without committing filesystem changes.</summary>
	public async Task<PatchApplicationPlan> BuildAsync(
		PatchSource source,
		PatchDocument document,
		PatchPathPlanningOptions options,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( document );
		ArgumentNullException.ThrowIfNull( options );
		options.Validate();
		var working = await this.ResolveWorkingDirectoryAsync(
			options.Directory,
			cancellationToken
		).ConfigureAwait( false );
		var plans = new List<PatchFilePlan>( document.Files.Count );
		var owned = new List<IAsyncDisposable>();
		var states = new Dictionary<string, PatchVirtualFile>( this.fileSystem.Semantics.PathComparer );
		var aggregate = new PatchExitStatusAccumulator();
		try {
			for ( var fileIndex = 0; fileIndex < document.Files.Count; fileIndex++ ) {
				cancellationToken.ThrowIfCancellationRequested();
				var patch = document.Files[fileIndex];
				var precedingText = GetPrecedingText( document, fileIndex );
				var candidates = await this.BuildCandidatesAsync(
					source,
					patch,
					precedingText,
					working,
					options,
					states,
					cancellationToken
				).ConfigureAwait( false );
				var effectiveChange = EffectiveChangeKind( patch.ChangeKind, options.EngineOptions.Reverse );
				var selected = SelectCandidate( candidates, effectiveChange, options.Posix, this.fileSystem.Semantics );
				var retrieved = false;
				PatchVirtualFile? input = selected?.VirtualState;
				if ( null == selected ) {
					var retrieval = await this.TryRetrieveCandidateAsync(
						candidates,
						effectiveChange,
						options,
						cancellationToken
					).ConfigureAwait( false );
					selected = retrieval.Candidate;
					input = retrieval.File;
					retrieved = null != input;
					if ( null != input ) {
						owned.Add( input );
					}
				}
				if ( null == selected ) {
					var message = CreateSelectionFailureMessage( candidates );
					plans.Add(
						new PatchFilePlan(
							patch,
							null,
							candidates.Select( value => value.Candidate ).ToArray(),
							null,
							null,
							PatchExitStatus.PartialFailure,
							false,
							message
						)
					);
					aggregate.Add( PatchExitStatus.PartialFailure );
					continue;
				}
				var canonicalPath = selected.Candidate.CanonicalPath!;
				if ( null == input && states.TryGetValue( canonicalPath, out var priorState ) ) {
					input = priorState;
				}
				if ( null == input ) {
					input = await this.OpenVirtualFileAsync(
						selected.Candidate,
						options.TargetLimits,
						cancellationToken
					).ConfigureAwait( false );
					owned.Add( input );
				}
				var prerequisite = await PatchPrerequisite.ExtractAsync(
					source,
					precedingText,
					cancellationToken
				).ConfigureAwait( false );
				var engineOptions = WithPrerequisite( options.EngineOptions, prerequisite );
				var result = await PatchApplicationEngine.ApplyAsync(
					input,
					patch,
					engineOptions,
					cancellationToken
				).ConfigureAwait( false );
				owned.Add( result );
				states[canonicalPath] = result.File;
				var action = DetermineAction( input.Exists, result.File.Exists );
				plans.Add(
					new PatchFilePlan(
						patch,
						selected.Candidate,
						candidates.Select( value => value.Candidate ).ToArray(),
						action,
						result,
						result.Status,
						retrieved,
						null
					)
				);
				aggregate.Add( result.Status );
			}
			return new PatchApplicationPlan( working, plans, aggregate.Status, owned );
		} catch {
			for ( var index = owned.Count - 1; 0 <= index; index-- ) {
				await owned[index].DisposeAsync().ConfigureAwait( false );
			}
			throw;
		}
	}

	private async Task<string> ResolveWorkingDirectoryAsync(
		string? directory,
		CancellationToken cancellationToken
	) {
		var result = await this.resolver.ResolvePhysicalAsync(
			directory ?? this.fileSystem.CurrentDirectory,
			new CanonicalPathResolutionOptions {
				BasePath = this.fileSystem.CurrentDirectory,
				MissingComponentPolicy = MissingPathComponentPolicy.RequireExisting,
				FollowSymbolicLinks = true,
				RequireFinalDirectory = true
			},
			cancellationToken
		).ConfigureAwait( false );
		if ( !result.Succeeded ) {
			throw new PatchApplicationException( FormatFailure( result.Failure! ) );
		}
		return result.Path!;
	}

	private async Task<List<EvaluatedCandidate>> BuildCandidatesAsync(
		PatchSource source,
		PatchFilePatch patch,
		PatchTextRegion? precedingText,
		string workingDirectory,
		PatchPathPlanningOptions options,
		IReadOnlyDictionary<string, PatchVirtualFile> states,
		CancellationToken cancellationToken
	) {
		var raw = new List<(PatchPathCandidateSource Source, string Name, int Ordinal)>();
		if ( null != options.OriginalFile ) {
			raw.Add( ( PatchPathCandidateSource.ExplicitOperand, options.OriginalFile, 0 ) );
		} else {
			if ( null != patch.OldHeader && !PatchPathSelection.IsNullDevice( patch.OldHeader.Name ) ) {
				raw.Add( ( PatchPathCandidateSource.OldHeader, patch.OldHeader.Name, 0 ) );
			}
			if ( null != patch.NewHeader && !PatchPathSelection.IsNullDevice( patch.NewHeader.Name ) ) {
				raw.Add( ( PatchPathCandidateSource.NewHeader, patch.NewHeader.Name, 1 ) );
			}
			if ( options.Posix || 0 == raw.Count ) {
				var indexName = await PatchPathSelection.ExtractIndexNameAsync(
					source,
					precedingText,
					cancellationToken
				).ConfigureAwait( false );
				if ( null != indexName ) {
					raw.Add( ( PatchPathCandidateSource.Index, indexName, 2 ) );
				}
			}
		}
		var result = new List<EvaluatedCandidate>( raw.Count );
		foreach ( var item in raw ) {
			cancellationToken.ThrowIfCancellationRequested();
			string decoded;
			try {
				decoded = PatchPathSelection.DecodeName( item.Name, patch.SourceLocation );
			} catch ( PatchInputException exception ) {
				result.Add(
					new EvaluatedCandidate(
						new PatchPathCandidate(
							item.Source,
							item.Name,
							item.Name,
							item.Ordinal,
							null,
							false,
							0,
							new CanonicalPathFailure(
								CanonicalPathFailureCode.InvalidPath,
								item.Name,
								exception.Message,
								exception
							)
						),
						null
					)
				);
				continue;
			}
			var stripped = PatchPathCandidateSource.ExplicitOperand == item.Source
				? decoded
				: PatchPathSelection.Strip( decoded, options.StripCount, this.fileSystem.Semantics )
			;
			if ( string.IsNullOrEmpty( stripped ) ) {
				result.Add(
					new EvaluatedCandidate(
						new PatchPathCandidate(
							item.Source,
							decoded,
							string.Empty,
							item.Ordinal,
							null,
							false,
							0,
							new CanonicalPathFailure(
								CanonicalPathFailureCode.InvalidPath,
								decoded,
								"the filename does not contain enough components for the selected strip count"
							)
						),
						null
					)
				);
				continue;
			}
			result.Add(
				await this.EvaluateCandidateAsync(
					item.Source,
					decoded,
					stripped,
					item.Ordinal,
					workingDirectory,
					options.FollowSymbolicLinks,
					states,
					cancellationToken
				).ConfigureAwait( false )
			);
		}
		return result;
	}

	private async Task<EvaluatedCandidate> EvaluateCandidateAsync(
		PatchPathCandidateSource source,
		string originalName,
		string selectedName,
		int ordinal,
		string workingDirectory,
		bool followSymbolicLinks,
		IReadOnlyDictionary<string, PatchVirtualFile> states,
		CancellationToken cancellationToken
	) {
		var lexical = this.resolver.NormalizeLexically( selectedName, workingDirectory );
		if ( !lexical.Succeeded ) {
			return Failed( lexical.Failure! );
		}
		var lexicalContainment = this.resolver.EvaluateContainment( workingDirectory, lexical.Path! );
		if ( !lexicalContainment.Succeeded ) {
			return Failed( lexicalContainment.Failure! );
		}
		if ( !lexicalContainment.IsContained ) {
			return Failed(
				new CanonicalPathFailure(
					CanonicalPathFailureCode.InvalidPath,
					lexical.Path!,
					"the selected pathname escapes the patch working directory"
				)
			);
		}
		if ( !followSymbolicLinks ) {
			var inspection = await this.resolver.InspectLinkAsync(
				lexical.Path!,
				workingDirectory,
				cancellationToken
			).ConfigureAwait( false );
			if ( inspection.Succeeded && ( inspection.IsSymbolicLink || inspection.IsReparsePoint ) ) {
				return Failed(
					new CanonicalPathFailure(
						CanonicalPathFailureCode.UnsupportedReparsePoint,
						lexical.Path!,
						"the selected target is a link or reparse point; use --follow-symlinks to follow it"
					)
				);
			}
			if ( !inspection.Succeeded
				&& inspection.Failure!.Code is not CanonicalPathFailureCode.NotFound ) {
				return Failed( inspection.Failure! );
			}
		}
		var physical = await this.resolver.ResolvePhysicalAsync(
			lexical.Path!,
			new CanonicalPathResolutionOptions {
				BasePath = workingDirectory,
				MissingComponentPolicy = MissingPathComponentPolicy.AllowMissingSuffix,
				FollowSymbolicLinks = true,
				FollowFinalSymbolicLink = true
			},
			cancellationToken
		).ConfigureAwait( false );
		if ( !physical.Succeeded ) {
			return Failed( physical.Failure! );
		}
		var containment = this.resolver.EvaluateContainment( workingDirectory, physical.Path! );
		if ( !containment.Succeeded ) {
			return Failed( containment.Failure! );
		}
		if ( !containment.IsContained ) {
			return Failed(
				new CanonicalPathFailure(
					CanonicalPathFailureCode.InvalidPath,
					physical.Path!,
					"the selected pathname resolves outside the patch working directory"
				)
			);
		}
		states.TryGetValue( physical.Path!, out var virtualState );
		var exists = null != virtualState
			? virtualState.Exists
			: 0 == physical.MissingComponentCount
		;
		if ( exists && null == virtualState ) {
			var observation = await this.fileSystem.ObserveAsync(
				physical.Path!,
				cancellationToken
			).ConfigureAwait( false );
			if ( !observation.ObservationSucceeded ) {
				return Failed(
					observation.Failure
					?? new CanonicalPathFailure(
						CanonicalPathFailureCode.IoError,
						physical.Path!,
						"the target could not be inspected"
					)
				);
			}
			if ( observation.Exists && CanonicalPathEntryKind.File != observation.Kind ) {
				return Failed(
					new CanonicalPathFailure(
						CanonicalPathFailureCode.NotDirectory,
						physical.Path!,
						"the selected target is not a regular file"
					)
				);
			}
		}
		return new EvaluatedCandidate(
			new PatchPathCandidate(
				source,
				originalName,
				selectedName,
				ordinal,
				physical.Path!,
				exists,
				physical.MissingComponentCount,
				null
			),
			virtualState
		);

		EvaluatedCandidate Failed( CanonicalPathFailure failure ) => new(
			new PatchPathCandidate(
				source,
				originalName,
				selectedName,
				ordinal,
				null,
				false,
				0,
				failure
			),
			null
		);
	}

	private async Task<(EvaluatedCandidate? Candidate, PatchVirtualFile? File)> TryRetrieveCandidateAsync(
		IReadOnlyList<EvaluatedCandidate> candidates,
		PatchFileChangeKind change,
		PatchPathPlanningOptions options,
		CancellationToken cancellationToken
	) {
		if ( PatchFileChangeKind.Create == change || 0 == options.Get ) {
			return ( null, null );
		}
		var ordered = candidates
			.Where( value => value.Candidate.IsEligible )
			.OrderBy( value => value.Candidate.Ordinal )
		;
		foreach ( var candidate in ordered ) {
			if ( !await this.versionControl.IsRetrievableAsync(
				candidate.Candidate.CanonicalPath!,
				cancellationToken
			).ConfigureAwait( false ) ) {
				continue;
			}
			if ( options.Get < 0 ) {
				var decisionProvider = options.EngineOptions.DecisionProvider;
				if ( null == decisionProvider
					|| !await decisionProvider.DecideAsync(
						new PatchDecisionRequest(
							PatchDecisionKind.RetrieveFromVersionControl,
							string.Concat(
								"retrieve '",
								candidate.Candidate.CanonicalPath,
								"' from version control"
							)
						),
						cancellationToken
					).ConfigureAwait( false ) ) {
					continue;
				}
			}
			await using var retrieval = await this.versionControl.RetrieveAsync(
				candidate.Candidate.CanonicalPath!,
				cancellationToken
			).ConfigureAwait( false );
			if ( PatchVersionControlOutcome.Retrieved != retrieval.Outcome ) {
				continue;
			}
			var content = await PatchTargetContent.ReadAsync(
				retrieval.Content!,
				options.TargetLimits,
				cancellationToken
			).ConfigureAwait( false );
			return ( candidate, new PatchVirtualFile( true, content ) );
		}
		return ( null, null );
	}

	private async Task<PatchVirtualFile> OpenVirtualFileAsync(
		PatchPathCandidate candidate,
		PatchTargetLimits limits,
		CancellationToken cancellationToken
	) {
		if ( !candidate.Exists ) {
			return new PatchVirtualFile( false, null );
		}
		await using var input = await this.fileSystem.OpenReadAsync(
			candidate.CanonicalPath!,
			cancellationToken
		).ConfigureAwait( false );
		var content = await PatchTargetContent.ReadAsync(
			input,
			limits,
			cancellationToken
		).ConfigureAwait( false );
		return new PatchVirtualFile( true, content );
	}

	private static EvaluatedCandidate? SelectCandidate(
		IReadOnlyList<EvaluatedCandidate> candidates,
		PatchFileChangeKind change,
		bool posix,
		PathPlatformSemantics semantics
	) {
		var eligible = candidates.Where( value => value.Candidate.IsEligible ).ToArray();
		var existing = eligible.Where( value => value.Candidate.Exists ).ToArray();
		if ( 0 < existing.Length ) {
			return posix
				? existing.OrderBy( value => value.Candidate.Ordinal ).First()
				: OrderBest( existing, semantics ).First()
			;
		}
		if ( PatchFileChangeKind.Create == change ) {
			var explicitOperand = eligible.FirstOrDefault(
				value => PatchPathCandidateSource.ExplicitOperand == value.Candidate.Source
			);
			if ( null != explicitOperand ) {
				return explicitOperand;
			}
		}
		if ( PatchFileChangeKind.Create == change && !posix ) {
			return eligible
				.OrderBy( value => value.Candidate.MissingComponentCount )
				.ThenBy( value => PatchPathSelection.CountComponents( value.Candidate.SelectedName, semantics ) )
				.ThenBy( value => PatchPathSelection.GetBasename( value.Candidate.SelectedName, semantics ).Length )
				.ThenBy( value => value.Candidate.SelectedName.Length )
				.ThenBy( value => value.Candidate.Ordinal )
				.FirstOrDefault()
			;
		}
		return null;
	}

	private static IOrderedEnumerable<EvaluatedCandidate> OrderBest(
		IEnumerable<EvaluatedCandidate> candidates,
		PathPlatformSemantics semantics
	) => candidates
		.OrderBy( value => PatchPathSelection.CountComponents( value.Candidate.SelectedName, semantics ) )
		.ThenBy( value => PatchPathSelection.GetBasename( value.Candidate.SelectedName, semantics ).Length )
		.ThenBy( value => value.Candidate.SelectedName.Length )
		.ThenBy( value => value.Candidate.Ordinal )
	;

	private static PatchTextRegion? GetPrecedingText( PatchDocument document, int fileIndex ) {
		if ( 0 == fileIndex ) {
			return document.LeadingText;
		}
		return fileIndex - 1 < document.InterstitialText.Count
			? document.InterstitialText[fileIndex - 1]
			: null
		;
	}

	private static PatchFileChangeKind EffectiveChangeKind(
		PatchFileChangeKind change,
		bool reverse
	) {
		if ( !reverse ) {
			return change;
		}
		return change switch {
			PatchFileChangeKind.Create => PatchFileChangeKind.Delete,
			PatchFileChangeKind.Delete => PatchFileChangeKind.Create,
			_ => change
		};
	}

	private static PatchPlannedFileAction DetermineAction( bool existed, bool exists ) {
		if ( !existed && exists ) {
			return PatchPlannedFileAction.Create;
		}
		if ( existed && !exists ) {
			return PatchPlannedFileAction.Delete;
		}
		return PatchPlannedFileAction.Modify;
	}

	private static PatchEngineOptions WithPrerequisite(
		PatchEngineOptions source,
		string? prerequisite
	) => new() {
		Reverse = source.Reverse,
		Force = source.Force,
		ForwardOnly = source.ForwardOnly,
		Batch = source.Batch,
		Fuzz = source.Fuzz,
		IgnoreWhitespace = source.IgnoreWhitespace,
		MergeStyle = source.MergeStyle,
		PrerequisiteToken = prerequisite,
		DecisionProvider = source.DecisionProvider,
		Limits = source.Limits
	};

	private static string CreateSelectionFailureMessage(
		IReadOnlyList<EvaluatedCandidate> candidates
	) {
		var failure = candidates
			.Select( value => value.Candidate.Failure )
			.FirstOrDefault( value => null != value )
		;
		return null == failure
			? "no usable file name was found in the patch"
			: FormatFailure( failure )
		;
	}

	private static string FormatFailure( CanonicalPathFailure failure ) => string.Concat(
		failure.Path,
		": ",
		failure.Message
	);
}
