namespace Icod.Patch;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

/// <summary>Identifies command output detail.</summary>
internal enum PatchVerbosity {
	/// <summary>Suppress ordinary progress diagnostics.</summary>
	Quiet,
	/// <summary>Emit ordinary GNU-style progress diagnostics.</summary>
	Normal,
	/// <summary>Emit progress and artifact-policy details.</summary>
	Verbose
}

/// <summary>Identifies the backup-version selection policy.</summary>
internal enum PatchBackupVersionControl {
	/// <summary>Use numbered backups only when numbered backups already exist.</summary>
	Existing,
	/// <summary>Always use numbered backups.</summary>
	Numbered,
	/// <summary>Always use the simple suffix backup.</summary>
	Simple
}

/// <summary>Identifies the requested reject representation.</summary>
internal enum PatchRejectFormat {
	/// <summary>Use unified rejects for unified input and context rejects otherwise.</summary>
	Automatic,
	/// <summary>Write context-style rejects.</summary>
	Context,
	/// <summary>Write unified-style rejects.</summary>
	Unified
}

/// <summary>Identifies filename diagnostic quoting.</summary>
internal enum PatchQuotingStyle {
	/// <summary>Write the pathname literally.</summary>
	Literal,
	/// <summary>Use shell quoting when the pathname requires it.</summary>
	Shell,
	/// <summary>Always use shell quoting.</summary>
	ShellAlways,
	/// <summary>Use C string-literal quoting.</summary>
	C,
	/// <summary>Escape nonprinting and ambiguous characters.</summary>
	Escape
}

/// <summary>Identifies one externally visible patch artifact.</summary>
internal enum PatchArtifactKind {
	/// <summary>The selected target file.</summary>
	Target,
	/// <summary>A retained copy of the pre-patch target.</summary>
	Backup,
	/// <summary>Rejected hunks.</summary>
	Reject,
	/// <summary>The destination selected by <c>--output</c>.</summary>
	Output
}

/// <summary>Identifies how an artifact changes its destination.</summary>
internal enum PatchArtifactAction {
	/// <summary>Validate an observed pathname without mutating it.</summary>
	ValidateOnly,
	/// <summary>Create or replace the destination with staged content.</summary>
	Write,
	/// <summary>Remove the destination pathname object.</summary>
	Delete,
	/// <summary>Write content to the command's standard-output stream.</summary>
	WriteStandardOutput
}

/// <summary>Identifies the source of staged artifact bytes.</summary>
internal enum PatchArtifactContentKind {
	/// <summary>Content comes from an immutable virtual patch result.</summary>
	VirtualFile,
	/// <summary>Content comes from an existing pathname.</summary>
	ExistingFile,
	/// <summary>Content is an immutable byte sequence.</summary>
	Bytes
}

/// <summary>Describes metadata to apply after an artifact is committed.</summary>
internal sealed class PatchArtifactMetadata {
	/// <summary>Gets or initializes the numeric owner identifier.</summary>
	public uint? UserId { get; init; }

	/// <summary>Gets or initializes the numeric group identifier.</summary>
	public uint? GroupId { get; init; }

	/// <summary>Gets or initializes the portable mode value.</summary>
	public int? Mode { get; init; }

	/// <summary>Gets or initializes the access time.</summary>
	public DateTimeOffset? AccessTime { get; init; }

	/// <summary>Gets or initializes the modification time.</summary>
	public DateTimeOffset? ModificationTime { get; init; }

	/// <summary>Gets or initializes whether a requested timestamp is command-mandatory.</summary>
	public bool RequireTimestamps { get; init; }
}

/// <summary>Provides immutable staged bytes for one artifact.</summary>
internal sealed class PatchArtifactContent {
	private readonly byte[]? bytes;

	private PatchArtifactContent(
		PatchArtifactContentKind kind,
		PatchTargetContent? virtualContent,
		string? existingPath,
		byte[]? bytes
	) {
		this.Kind = kind;
		this.VirtualContent = virtualContent;
		this.ExistingPath = existingPath;
		this.bytes = bytes;
	}

	/// <summary>Gets the content source kind.</summary>
	public PatchArtifactContentKind Kind { get; }

	/// <summary>Gets the virtual content source.</summary>
	public PatchTargetContent? VirtualContent { get; }

	/// <summary>Gets the existing pathname content source.</summary>
	public string? ExistingPath { get; }

	/// <summary>Gets immutable direct bytes.</summary>
	public ReadOnlyMemory<byte> Bytes => this.bytes ?? ReadOnlyMemory<byte>.Empty;

	/// <summary>Creates content backed by an immutable virtual result.</summary>
	public static PatchArtifactContent FromVirtualFile( PatchTargetContent content ) {
		ArgumentNullException.ThrowIfNull( content );
		return new PatchArtifactContent( PatchArtifactContentKind.VirtualFile, content, null, null );
	}

	/// <summary>Creates content copied from an existing pathname.</summary>
	public static PatchArtifactContent FromExistingFile( string path ) {
		ArgumentException.ThrowIfNullOrEmpty( path );
		return new PatchArtifactContent( PatchArtifactContentKind.ExistingFile, null, path, null );
	}

	/// <summary>Creates content from a copied immutable byte sequence.</summary>
	public static PatchArtifactContent FromBytes( ReadOnlySpan<byte> value ) {
		return new PatchArtifactContent( PatchArtifactContentKind.Bytes, null, null, value.ToArray() );
	}

	/// <summary>Writes the immutable content to a supplied destination stream.</summary>
	public async Task WriteToAsync( Stream output, CancellationToken cancellationToken = default ) {
		ArgumentNullException.ThrowIfNull( output );
		switch ( this.Kind ) {
			case PatchArtifactContentKind.VirtualFile:
				await this.VirtualContent!.WriteToAsync( output, cancellationToken ).ConfigureAwait( false );
				break;
			case PatchArtifactContentKind.ExistingFile:
				await using ( var input = new FileStream(
					this.ExistingPath!,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					64 * 1024,
					FileOptions.Asynchronous | FileOptions.SequentialScan
				) ) {
					await input.CopyToAsync( output, 64 * 1024, cancellationToken ).ConfigureAwait( false );
				}
				break;
			case PatchArtifactContentKind.Bytes:
				await output.WriteAsync( this.Bytes, cancellationToken ).ConfigureAwait( false );
				break;
			default:
				throw new InvalidOperationException( "unknown Patch artifact content kind" );
		}
	}
}

/// <summary>Describes one target, backup, reject, or output artifact.</summary>
internal sealed class PatchArtifact {
	/// <summary>Initializes an artifact.</summary>
	public PatchArtifact(
		PatchArtifactKind kind,
		PatchArtifactAction action,
		string path,
		PatchArtifactContent? content,
		PatchFileObservation expectedDestination,
		PatchArtifactMetadata metadata,
		string displayName,
		string? transactionUnitId = null
	) {
		var requiresContent = PatchArtifactAction.Write == action
			|| PatchArtifactAction.WriteStandardOutput == action;
		if ( requiresContent && null == content ) {
			throw new ArgumentNullException( nameof( content ) );
		}
		if ( !requiresContent && null != content ) {
			throw new ArgumentException( "a non-writing artifact cannot carry content", nameof( content ) );
		}
		this.Kind = kind;
		this.Action = action;
		this.Path = path ?? throw new ArgumentNullException( nameof( path ) );
		this.Content = content;
		this.ExpectedDestination = expectedDestination ?? throw new ArgumentNullException( nameof( expectedDestination ) );
		this.Metadata = metadata ?? throw new ArgumentNullException( nameof( metadata ) );
		this.DisplayName = displayName ?? throw new ArgumentNullException( nameof( displayName ) );
		this.TransactionUnitId = string.IsNullOrEmpty( transactionUnitId )
			? path
			: transactionUnitId;
	}

	/// <summary>Gets the artifact kind.</summary>
	public PatchArtifactKind Kind { get; }
	/// <summary>Gets the requested destination action.</summary>
	public PatchArtifactAction Action { get; }
	/// <summary>Gets the canonical or fully qualified destination path.</summary>
	public string Path { get; }
	/// <summary>Gets the bytes to stage for a write artifact.</summary>
	public PatchArtifactContent? Content { get; }
	/// <summary>Gets the E3 observation that must still hold before commit.</summary>
	public PatchFileObservation ExpectedDestination { get; }
	/// <summary>Gets metadata to apply after commit.</summary>
	public PatchArtifactMetadata Metadata { get; }
	/// <summary>Gets the safely quoted user-facing pathname.</summary>
	public string DisplayName { get; }
	/// <summary>Gets the per-file recovery unit that owns this artifact.</summary>
	public string TransactionUnitId { get; }
}

/// <summary>Contains all P8 artifacts derived from one P7 application plan.</summary>
internal sealed class PatchArtifactPlan {
	/// <summary>Initializes an artifact plan.</summary>
	public PatchArtifactPlan(
		IReadOnlyList<PatchArtifact> artifacts,
		PatchExitStatus status,
		IReadOnlyList<string> diagnostics,
		string? containmentRootPath = null
	) {
		ArgumentNullException.ThrowIfNull( artifacts );
		ArgumentNullException.ThrowIfNull( diagnostics );
		this.Artifacts = new ReadOnlyCollection<PatchArtifact>( artifacts.ToArray() );
		this.Status = status;
		this.Diagnostics = new ReadOnlyCollection<string>( diagnostics.ToArray() );
		this.ContainmentRootPath = containmentRootPath;
	}

	/// <summary>Gets artifacts in deterministic commit order.</summary>
	public IReadOnlyList<PatchArtifact> Artifacts { get; }
	/// <summary>Gets the aggregate patch status before filesystem commit.</summary>
	public PatchExitStatus Status { get; }
	/// <summary>Gets deterministic artifact-policy diagnostics.</summary>
	public IReadOnlyList<string> Diagnostics { get; }
	/// <summary>Gets the E2-resolved working-directory containment root.</summary>
	public string? ContainmentRootPath { get; }
}

/// <summary>Builds explicit target, backup, reject, and output artifacts from P7 virtual results.</summary>
internal sealed class PatchArtifactPlanner {
	private readonly IPatchFileSystem fileSystem;

	/// <summary>Initializes an artifact planner.</summary>
	public PatchArtifactPlanner( IPatchFileSystem fileSystem ) {
		this.fileSystem = fileSystem ?? throw new ArgumentNullException( nameof( fileSystem ) );
	}

	/// <summary>Builds the immutable P8 artifact plan.</summary>
	public async Task<PatchArtifactPlan> BuildAsync(
		PatchApplicationPlan applicationPlan,
		PatchOptions options,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( applicationPlan );
		ArgumentNullException.ThrowIfNull( options );
		var artifacts = new List<PatchArtifact>();
		var diagnostics = new List<string>();
		var status = new PatchExitStatusAccumulator();
		status.Add( applicationPlan.Status );
		var usableFiles = applicationPlan.Files.Where(
			file => null != file.SelectedCandidate && null != file.Result && null != file.Action
		).ToArray();
		if ( null != options.OutputFile && 1 != usableFiles.Length ) {
			throw new PatchApplicationException( "--output requires exactly one selected file patch" );
		}
		var pathComparer = GetPathComparer();
		var finalFileByTarget = new Dictionary<string, PatchFilePlan>( pathComparer );
		var mismatchByTarget = new Dictionary<string, bool>( pathComparer );
		var changedByTarget = new Dictionary<string, bool>( pathComparer );
		var backupEligibleByTarget = new Dictionary<string, bool>( pathComparer );
		foreach ( var file in usableFiles ) {
			var targetPath = file.SelectedCandidate!.CanonicalPath!;
			finalFileByTarget[targetPath] = file;
			var hunks = file.Result!.Hunks;
			var mismatch = hunks.Any(
				hunk => PatchHunkOutcome.Failed == hunk.Outcome
					|| PatchHunkOutcome.Merged == hunk.Outcome
					|| (PatchHunkOutcome.Applied == hunk.Outcome && (0 != hunk.Offset || 0 != hunk.Fuzz))
			);
			var changed = PatchPlannedFileAction.Create == file.Action
				|| PatchPlannedFileAction.Delete == file.Action
				|| hunks.Any( hunk => hunk.Outcome is PatchHunkOutcome.Applied or PatchHunkOutcome.Merged );
			var backupEligible = PatchPlannedFileAction.Create == file.Action
				|| PatchPlannedFileAction.Delete == file.Action
				|| hunks.Any( hunk => PatchHunkOutcome.Skipped != hunk.Outcome );
			mismatchByTarget[targetPath] = mismatchByTarget.TryGetValue( targetPath, out var priorMismatch )
				? priorMismatch || mismatch
				: mismatch;
			changedByTarget[targetPath] = changedByTarget.TryGetValue( targetPath, out var priorChanged )
				? priorChanged || changed
				: changed;
			backupEligibleByTarget[targetPath] = backupEligibleByTarget.TryGetValue(
				targetPath,
				out var priorBackupEligible
			)
				? priorBackupEligible || backupEligible
				: backupEligible;
		}
		var rejectStreams = new Dictionary<string, MemoryStream>( pathComparer );
		var rejectTransactionUnits = new Dictionary<string, string?>( pathComparer );
		try {
			foreach ( var file in usableFiles ) {
				cancellationToken.ThrowIfCancellationRequested();
				var targetPath = file.SelectedCandidate!.CanonicalPath!;
				var targetObservation = await this.fileSystem.ObserveAsync(
					targetPath,
					options.FollowSymbolicLinks,
					cancellationToken
				).ConfigureAwait( false );
				var result = file.Result!;
				var rejectedHunks = result.Hunks.Where(
					hunk => hunk.Outcome is PatchHunkOutcome.Failed or PatchHunkOutcome.Skipped
				).ToArray();
				if ( 0 < rejectedHunks.Length ) {
					status.Add( PatchExitStatus.PartialFailure );
					if ( "-" != options.RejectFile ) {
						var rejectBytes = PatchRejectWriter.Write(
							file.Patch,
							rejectedHunks,
							options.RejectFormat,
							targetPath,
							result.Direction
						);
						var rejectPath = await this.ResolveRejectPathAsync(
							applicationPlan.WorkingDirectory,
							targetPath,
							options,
							cancellationToken
						).ConfigureAwait( false );
						if ( !rejectStreams.TryGetValue( rejectPath, out var rejectStream ) ) {
							rejectStream = new MemoryStream();
							rejectStreams.Add( rejectPath, rejectStream );
							rejectTransactionUnits.Add( rejectPath, targetPath );
						} else if ( rejectTransactionUnits.TryGetValue( rejectPath, out var priorUnit )
							&& !pathComparer.Equals( priorUnit, targetPath ) ) {
							rejectTransactionUnits[rejectPath] = null;
						}
						await rejectStream.WriteAsync( rejectBytes, cancellationToken ).ConfigureAwait( false );
					}
				}
				if ( !ReferenceEquals( finalFileByTarget[targetPath], file ) ) {
					continue;
				}
				var aggregateMismatch = mismatchByTarget[targetPath];
				var aggregateChanged = changedByTarget[targetPath];
				var aggregateBackupEligible = backupEligibleByTarget[targetPath];
				if ( null == options.OutputFile ) {
					if ( aggregateBackupEligible && ShouldCreateBackup( options, aggregateMismatch ) ) {
						artifacts.Add(
							await this.CreateBackupArtifactAsync(
								targetPath,
								targetObservation,
								applicationPlan.WorkingDirectory,
								options,
								cancellationToken
							).ConfigureAwait( false )
						);
					}
					var targetMetadata = CreateTargetMetadata(
						file,
						targetObservation,
						targetObservation,
						aggregateMismatch,
						options,
						diagnostics,
						PatchFileNameQuoter.Quote( targetPath, options.QuotingStyle )
					);
					var remove = PatchPlannedFileAction.Delete == file.Action
						|| (options.RemoveEmptyFiles && result.File.Exists && 0 == result.File.Content!.Length);
					if ( aggregateChanged && remove ) {
						artifacts.Add(
							new PatchArtifact(
								PatchArtifactKind.Target,
								PatchArtifactAction.Delete,
								targetPath,
								null,
								targetObservation,
								new PatchArtifactMetadata(),
								PatchFileNameQuoter.Quote( targetPath, options.QuotingStyle )
							)
						);
					} else if ( aggregateChanged && result.File.Exists && null != result.File.Content ) {
						artifacts.Add(
							new PatchArtifact(
								PatchArtifactKind.Target,
								PatchArtifactAction.Write,
								targetPath,
								PatchArtifactContent.FromVirtualFile( result.File.Content ),
								targetObservation,
								targetMetadata,
								PatchFileNameQuoter.Quote( targetPath, options.QuotingStyle )
							)
						);
					}
				} else {
					artifacts.Add(
						new PatchArtifact(
							PatchArtifactKind.Target,
							PatchArtifactAction.ValidateOnly,
							targetPath,
							null,
							targetObservation,
							new PatchArtifactMetadata(),
							PatchFileNameQuoter.Quote( targetPath, options.QuotingStyle )
						)
					);
					if ( !result.File.Exists || null == result.File.Content ) {
						throw new PatchApplicationException( "--output cannot represent a deleted file" );
					}
					var outputContent = result.Hunks.Count > 0
						&& result.Hunks.All( hunk => PatchHunkOutcome.Skipped == hunk.Outcome )
						? PatchArtifactContent.FromBytes( ReadOnlySpan<byte>.Empty )
						: PatchArtifactContent.FromVirtualFile( result.File.Content );
					if ( "-" == options.OutputFile ) {
						var outputMetadata = CreateTargetMetadata(
							file,
							targetObservation,
							targetObservation,
							aggregateMismatch,
							options,
							diagnostics,
							"standard output"
						);
						artifacts.Add(
							new PatchArtifact(
								PatchArtifactKind.Output,
								PatchArtifactAction.WriteStandardOutput,
								"-",
								outputContent,
								new PatchFileObservation( "-" ),
								outputMetadata,
								"standard output",
								targetPath
							)
						);
					} else {
						var outputPath = await this.fileSystem.ResolveArtifactPathAsync(
							options.OutputFile,
							applicationPlan.WorkingDirectory,
							options.FollowSymbolicLinks,
							cancellationToken
						).ConfigureAwait( false );
						if ( pathComparer.Equals( outputPath, targetPath ) ) {
							throw new PatchApplicationException( "--output must not name an input file" );
						}
						var outputObservation = await this.fileSystem.ObserveAsync(
							outputPath,
							options.FollowSymbolicLinks,
							cancellationToken
						).ConfigureAwait( false );
						var metadataSource = outputObservation.Exists ? outputObservation : targetObservation;
						var outputMetadata = CreateTargetMetadata(
							file,
							targetObservation,
							metadataSource,
							aggregateMismatch,
							options,
							diagnostics,
							PatchFileNameQuoter.Quote( outputPath, options.QuotingStyle )
						);
						if ( !outputObservation.Exists ) {
							outputMetadata = new PatchArtifactMetadata {
								Mode = 0x0180,
								AccessTime = outputMetadata.AccessTime,
								ModificationTime = outputMetadata.ModificationTime,
								RequireTimestamps = outputMetadata.RequireTimestamps
							};
						}
						artifacts.Add(
							new PatchArtifact(
								PatchArtifactKind.Output,
								PatchArtifactAction.Write,
								outputPath,
								outputContent,
								outputObservation,
								outputMetadata,
								PatchFileNameQuoter.Quote( outputPath, options.QuotingStyle ),
								targetPath
							)
						);
					}
				}
				if ( PatchVerbosity.Verbose == options.Verbosity ) {
					diagnostics.Add(
						string.Concat(
							"planned ",
							file.Action!.Value.ToString().ToLowerInvariant(),
							" of ",
							PatchFileNameQuoter.Quote( targetPath, options.QuotingStyle ),
							" with ",
							rejectedHunks.Length.ToString( CultureInfo.InvariantCulture ),
							" rejected hunk(s)"
						)
					);
				}
			}
			foreach ( var item in rejectStreams.OrderBy( item => item.Key, pathComparer ) ) {
				var rejectObservation = await this.fileSystem.ObserveAsync(
					item.Key,
					followPathIndirection: false,
					cancellationToken
				).ConfigureAwait( false );
				artifacts.Add(
					new PatchArtifact(
						PatchArtifactKind.Reject,
						PatchArtifactAction.Write,
						item.Key,
						PatchArtifactContent.FromBytes( item.Value.ToArray() ),
						rejectObservation,
						new PatchArtifactMetadata { Mode = rejectObservation.Mode ?? 0x01a4 },
						PatchFileNameQuoter.Quote( item.Key, options.QuotingStyle ),
						rejectTransactionUnits[item.Key] ?? item.Key
					)
				);
			}
			ValidateDistinctDestinations( artifacts, pathComparer );
			return new PatchArtifactPlan( OrderArtifacts( artifacts, pathComparer ), status.Status, diagnostics, applicationPlan.WorkingDirectory );
		} finally {
			foreach ( var stream in rejectStreams.Values ) {
				stream.Dispose();
			}
		}
	}

	private async Task<PatchArtifact> CreateBackupArtifactAsync(
		string destinationPath,
		PatchFileObservation destinationObservation,
		string workingDirectory,
		PatchOptions options,
		CancellationToken cancellationToken
	) {
		var backupPath = await this.SelectBackupPathAsync(
			destinationPath,
			workingDirectory,
			options,
			cancellationToken
		).ConfigureAwait( false );
		backupPath = await this.fileSystem.ResolveArtifactPathAsync(
			backupPath,
			workingDirectory,
			options.FollowSymbolicLinks,
			cancellationToken
		).ConfigureAwait( false );
		var backupObservation = await this.fileSystem.ObserveAsync(
			backupPath,
			followPathIndirection: false,
			cancellationToken
		).ConfigureAwait( false );
		return new PatchArtifact(
			PatchArtifactKind.Backup,
			PatchArtifactAction.Write,
			backupPath,
			destinationObservation.Exists
				? PatchArtifactContent.FromExistingFile( destinationPath )
				: PatchArtifactContent.FromBytes( ReadOnlySpan<byte>.Empty ),
			backupObservation,
			destinationObservation.Exists
				? CreatePreservedMetadata( destinationObservation, requireTimestamps: false )
				: new PatchArtifactMetadata { Mode = 0x01a4 },
			PatchFileNameQuoter.Quote( backupPath, options.QuotingStyle ),
			destinationPath
		);
	}

	private static IReadOnlyList<PatchArtifact> OrderArtifacts(
		IEnumerable<PatchArtifact> artifacts,
		StringComparer pathComparer
	) {
		return artifacts
			.OrderBy( artifact => PatchArtifactAction.ValidateOnly == artifact.Action
				? -1
				: artifact.Kind switch {
				PatchArtifactKind.Backup => 0,
				PatchArtifactKind.Reject => 1,
				PatchArtifactKind.Output => 2,
				_ => 3
			} )
			.ThenBy( artifact => artifact.Path, pathComparer )
			.ToArray();
	}

	private static void ValidateDistinctDestinations(
		IEnumerable<PatchArtifact> artifacts,
		StringComparer pathComparer
	) {
		var destinations = new Dictionary<string, PatchArtifact>( pathComparer );
		foreach ( var artifact in artifacts ) {
			if ( PatchArtifactAction.WriteStandardOutput == artifact.Action
				|| PatchArtifactAction.ValidateOnly == artifact.Action ) {
				continue;
			}
			if ( destinations.TryGetValue( artifact.Path, out var prior ) ) {
				throw new PatchApplicationException(
					string.Concat(
						artifact.DisplayName,
						": artifact destination conflicts with ",
						prior.Kind.ToString().ToLowerInvariant()
					)
				);
			}
			destinations.Add( artifact.Path, artifact );
		}
	}

	private static bool ShouldCreateBackup( PatchOptions options, bool mismatch ) {
		if ( options.Backup ) {
			return true;
		}
		if ( options.BackupIfMismatch.HasValue ) {
			return options.BackupIfMismatch.Value && mismatch;
		}
		return mismatch && !options.Posix;
	}

	private async Task<string> SelectBackupPathAsync(
		string destinationPath,
		string workingDirectory,
		PatchOptions options,
		CancellationToken cancellationToken
	) {
		if ( null != options.BackupPrefix ) {
			var relative = System.IO.Path.GetRelativePath( workingDirectory, destinationPath );
			if ( IsEscapingRelativePath( relative ) ) {
				throw new PatchApplicationException( "backup prefix would escape the patch working directory" );
			}
			return ResolveArtifactPath(
				workingDirectory,
				string.Concat(
					options.BackupPrefix,
					relative,
					options.BackupSuffixSpecified ? options.BackupSuffix : string.Empty
				)
			);
		}
		if ( null != options.BackupBasenamePrefix ) {
			var directory = System.IO.Path.GetDirectoryName( destinationPath ) ?? workingDirectory;
			return System.IO.Path.GetFullPath(
				System.IO.Path.Combine(
					directory,
					string.Concat(
						options.BackupBasenamePrefix,
						System.IO.Path.GetFileName( destinationPath ),
						options.BackupSuffixSpecified ? options.BackupSuffix : string.Empty
					)
				)
			);
		}
		var simple = string.Concat( destinationPath, options.BackupSuffix );
		if ( options.BackupSuffixSpecified
			|| PatchBackupVersionControl.Simple == options.BackupVersionControl ) {
			return simple;
		}
		var firstNumbered = string.Concat( destinationPath, ".~1~" );
		if ( PatchBackupVersionControl.Existing == options.BackupVersionControl ) {
			var first = await this.fileSystem.ObserveAsync(
				firstNumbered,
				followPathIndirection: false,
				cancellationToken
			).ConfigureAwait( false );
			if ( !first.Exists ) {
				return simple;
			}
		}
		for ( var number = 1; number < int.MaxValue; number++ ) {
			var candidate = string.Concat(
				destinationPath,
				".~",
				number.ToString( CultureInfo.InvariantCulture ),
				"~"
			);
			var observation = await this.fileSystem.ObserveAsync(
				candidate,
				followPathIndirection: false,
				cancellationToken
			).ConfigureAwait( false );
			if ( !observation.Exists ) {
				return candidate;
			}
		}
		throw new PatchApplicationException( "no numbered backup name is available" );
	}

	private static PatchArtifactMetadata CreateTargetMetadata(
		PatchFilePlan file,
		PatchFileObservation source,
		PatchFileObservation metadataSource,
		bool mismatch,
		PatchOptions options,
		ICollection<string> diagnostics,
		string displayName
	) {
		var mode = metadataSource.Mode ?? source.Mode;
		if ( !mode.HasValue ) {
			mode = 0x01a4;
			diagnostics.Add( string.Concat( displayName, ": mode unavailable; using 0644" ) );
		}
		DateTimeOffset? accessTime = null;
		DateTimeOffset? modificationTime = null;
		var requireTimestamps = false;
		if ( options.SetTime || options.SetUtc ) {
			var reverse = PatchDirection.Reverse == file.Result!.Direction;
			var sourceHeader = reverse ? file.Patch.NewHeader : file.Patch.OldHeader;
			var destinationHeader = reverse ? file.Patch.OldHeader : file.Patch.NewHeader;
			var requestedTime = ParseHeaderTimestamp( destinationHeader?.TimestampText, options.SetUtc );
			var expectedSourceTime = ParseHeaderTimestamp( sourceHeader?.TimestampText, options.SetUtc );
			string? reason = null;
			if ( !requestedTime.HasValue ) {
				reason = "the output header has no usable timestamp";
			} else if ( !options.Force && mismatch ) {
				reason = "the patch did not match exactly";
			} else if ( !options.Force && source.Exists ) {
				if ( !source.ModificationTime.HasValue || !expectedSourceTime.HasValue ) {
					reason = "the input timestamp cannot be verified";
				} else if ( TimeDiffers( source.ModificationTime.Value, expectedSourceTime.Value ) ) {
					reason = "the input timestamp does not match the patch header";
				}
			}
			if ( null == reason ) {
				accessTime = requestedTime;
				modificationTime = requestedTime;
				requireTimestamps = true;
			} else {
				diagnostics.Add( string.Concat( displayName, ": not setting timestamps because ", reason ) );
			}
		}
		return new PatchArtifactMetadata {
			UserId = metadataSource.UserId ?? source.UserId,
			GroupId = metadataSource.GroupId ?? source.GroupId,
			Mode = mode,
			AccessTime = accessTime,
			ModificationTime = modificationTime,
			RequireTimestamps = requireTimestamps
		};
	}

	private static PatchArtifactMetadata CreatePreservedMetadata(
		PatchFileObservation observation,
		bool requireTimestamps
	) {
		return new PatchArtifactMetadata {
			UserId = observation.UserId,
			GroupId = observation.GroupId,
			Mode = observation.Mode,
			AccessTime = observation.AccessTime,
			ModificationTime = observation.ModificationTime,
			RequireTimestamps = requireTimestamps
		};
	}

	private static bool TimeDiffers( DateTimeOffset left, DateTimeOffset right ) {
		return (left - right).Duration() > TimeSpan.FromSeconds( 1 );
	}

	private static DateTimeOffset? ParseHeaderTimestamp( string? text, bool utc ) {
		if ( string.IsNullOrWhiteSpace( text ) ) {
			return null;
		}
		var styles = DateTimeStyles.AllowWhiteSpaces;
		styles |= utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal;
		return DateTimeOffset.TryParse( text, CultureInfo.InvariantCulture, styles, out var value )
			? value
			: null;
	}

	private async Task<string> ResolveRejectPathAsync(
		string workingDirectory,
		string targetPath,
		PatchOptions options,
		CancellationToken cancellationToken
	) {
		var rawPath = options.RejectFile;
		if ( null == rawPath ) {
			var basis = null != options.OutputFile && "-" != options.OutputFile
				? options.OutputFile
				: targetPath;
			rawPath = string.Concat( basis, ".rej" );
		}
		return await this.fileSystem.ResolveArtifactPathAsync(
			rawPath,
			workingDirectory,
			options.FollowSymbolicLinks,
			cancellationToken
		).ConfigureAwait( false );
	}

	private static bool IsEscapingRelativePath( string value ) {
		return ".." == value
			|| value.StartsWith( string.Concat( "..", System.IO.Path.DirectorySeparatorChar ), StringComparison.Ordinal )
			|| value.StartsWith( string.Concat( "..", System.IO.Path.AltDirectorySeparatorChar ), StringComparison.Ordinal );
	}

	private static StringComparer GetPathComparer() {
		return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	}

	private static string ResolveArtifactPath( string workingDirectory, string value ) {
		return System.IO.Path.IsPathFullyQualified( value )
			? System.IO.Path.GetFullPath( value )
			: System.IO.Path.GetFullPath( value, workingDirectory );
	}
}

/// <summary>Serializes failed hunks without losing hostile or non-UTF-8 payload bytes.</summary>
internal static class PatchRejectWriter {
	/// <summary>Writes one reject payload.</summary>
	public static byte[] Write(
		PatchFilePatch file,
		IReadOnlyList<PatchHunkResult> failedHunks,
		PatchRejectFormat format,
		string targetPath,
		PatchDirection direction
	) {
		ArgumentNullException.ThrowIfNull( file );
		ArgumentNullException.ThrowIfNull( failedHunks );
		var effectiveFormat = PatchRejectFormat.Automatic == format
			? PatchFormat.Unified == file.Format ? PatchRejectFormat.Unified : PatchRejectFormat.Context
			: format;
		using var output = new MemoryStream();
		if ( 0 == failedHunks.Count ) {
			return output.ToArray();
		}
		if ( PatchRejectFormat.Unified == effectiveFormat ) {
			WriteUtf8Line( output, string.Concat( "--- ", targetPath ) );
			WriteUtf8Line( output, string.Concat( "+++ ", targetPath ) );
			foreach ( var failed in failedHunks ) {
				WriteUnifiedHunk( output, failed.Hunk, direction );
			}
		} else {
			WriteUtf8Line( output, string.Concat( "*** ", targetPath ) );
			WriteUtf8Line( output, string.Concat( "--- ", targetPath ) );
			foreach ( var failed in failedHunks ) {
				WriteContextHunk( output, failed.Hunk, direction );
			}
		}
		return output.ToArray();
	}

	private static void WriteUnifiedHunk(
		Stream output,
		PatchHunk hunk,
		PatchDirection direction
	) {
		var oldRange = PatchDirection.Reverse == direction
			? hunk.NewRange ?? hunk.OldRange
			: hunk.OldRange;
		var newRange = PatchDirection.Reverse == direction
			? hunk.OldRange
			: hunk.NewRange ?? hunk.OldRange;
		var oldLines = PatchDirection.Reverse == direction ? hunk.NewLines : hunk.OldLines;
		var newLines = PatchDirection.Reverse == direction ? hunk.OldLines : hunk.NewLines;
		WriteUtf8Line(
			output,
			string.Concat(
				"@@ -",
				FormatRange( oldRange ),
				" +",
				FormatRange( newRange ),
				" @@",
				hunk.SectionText is null ? string.Empty : string.Concat( " ", hunk.SectionText )
			)
		);
		foreach ( var line in oldLines ) {
			output.WriteByte( line.IsContext ? (byte)' ' : (byte)'-' );
			output.Write( line.Content.Span );
			WriteTerminator( output, line.Terminator );
		}
		foreach ( var line in newLines.Where( line => !line.IsContext ) ) {
			output.WriteByte( (byte)'+' );
			output.Write( line.Content.Span );
			WriteTerminator( output, line.Terminator );
		}
	}

	private static void WriteContextHunk(
		Stream output,
		PatchHunk hunk,
		PatchDirection direction
	) {
		var oldRange = PatchDirection.Reverse == direction
			? hunk.NewRange ?? hunk.OldRange
			: hunk.OldRange;
		var newRange = PatchDirection.Reverse == direction
			? hunk.OldRange
			: hunk.NewRange ?? hunk.OldRange;
		var oldLines = PatchDirection.Reverse == direction ? hunk.NewLines : hunk.OldLines;
		var newLines = PatchDirection.Reverse == direction ? hunk.OldLines : hunk.NewLines;
		var operation = PatchDirection.Reverse == direction
			? hunk.Operation switch {
				PatchOperationKind.Add => PatchOperationKind.Delete,
				PatchOperationKind.Delete => PatchOperationKind.Add,
				_ => PatchOperationKind.Change
			}
			: hunk.Operation;
		var oldMarker = PatchOperationKind.Change == operation ? "! "u8 : "- "u8;
		var newMarker = PatchOperationKind.Change == operation ? "! "u8 : "+ "u8;
		WriteUtf8Line( output, "***************" );
		WriteUtf8Line( output, string.Concat( "*** ", FormatContextRange( oldRange ), " ****" ) );
		foreach ( var line in oldLines ) {
			output.Write( line.IsContext ? "  "u8 : oldMarker );
			output.Write( line.Content.Span );
			WriteTerminator( output, line.Terminator );
		}
		WriteUtf8Line( output, string.Concat( "--- ", FormatContextRange( newRange ), " ----" ) );
		foreach ( var line in newLines ) {
			output.Write( line.IsContext ? "  "u8 : newMarker );
			output.Write( line.Content.Span );
			WriteTerminator( output, line.Terminator );
		}
	}

	private static string FormatRange( PatchRange range ) {
		return 1 == range.Count
			? range.Start.ToString( CultureInfo.InvariantCulture )
			: string.Concat(
				range.Start.ToString( CultureInfo.InvariantCulture ),
				",",
				range.Count.ToString( CultureInfo.InvariantCulture )
			);
	}

	private static string FormatContextRange( PatchRange range ) {
		if ( 0 == range.Count ) {
			return range.Start.ToString( CultureInfo.InvariantCulture );
		}
		var end = checked( range.Start + range.Count - 1 );
		return range.Start == end
			? range.Start.ToString( CultureInfo.InvariantCulture )
			: string.Concat(
				range.Start.ToString( CultureInfo.InvariantCulture ),
				",",
				end.ToString( CultureInfo.InvariantCulture )
			);
	}

	private static void WriteUtf8Line( Stream output, string value ) {
		output.Write( Encoding.UTF8.GetBytes( value ) );
		output.WriteByte( (byte)'\n' );
	}

	private static void WriteTerminator( Stream output, PatchLineTerminator terminator ) {
		switch ( terminator ) {
			case PatchLineTerminator.LineFeed:
				output.WriteByte( (byte)'\n' );
				break;
			case PatchLineTerminator.CarriageReturn:
				output.WriteByte( (byte)'\r' );
				break;
			case PatchLineTerminator.CarriageReturnLineFeed:
				output.WriteByte( (byte)'\r' );
				output.WriteByte( (byte)'\n' );
				break;
		}
	}
}

/// <summary>Quotes untrusted patch filenames for deterministic diagnostics.</summary>
internal static class PatchFileNameQuoter {
	/// <summary>Quotes one pathname.</summary>
	public static string Quote( string value, PatchQuotingStyle style ) {
		ArgumentNullException.ThrowIfNull( value );
		return style switch {
			PatchQuotingStyle.Literal => value,
			PatchQuotingStyle.Shell => NeedsShellQuoting( value ) ? QuoteShell( value ) : value,
			PatchQuotingStyle.ShellAlways => QuoteShell( value ),
			PatchQuotingStyle.C => QuoteC( value ),
			PatchQuotingStyle.Escape => Escape( value ),
			_ => throw new ArgumentOutOfRangeException( nameof( style ) )
		};
	}

	private static bool NeedsShellQuoting( string value ) {
		return 0 == value.Length || value.Any(
			character => !char.IsLetterOrDigit( character )
				&& "_+-./,:@%{}~#]".IndexOf( character ) < 0
		);
	}

	private static string QuoteShell( string value ) {
		if ( 0 <= value.IndexOf( '\'' )
			&& 0 > value.IndexOfAny( new[] { '"', '$', '`', '\\', '\n', '\r' } ) ) {
			return string.Concat( "\"", value, "\"" );
		}
		return string.Concat( "'", value.Replace( "'", "'\\''", StringComparison.Ordinal ), "'" );
	}

	private static string QuoteC( string value ) {
		return string.Concat( "\"", Escape( value ).Replace( "\"", "\\\"", StringComparison.Ordinal ), "\"" );
	}

	private static string Escape( string value ) {
		var builder = new StringBuilder( value.Length );
		foreach ( var character in value ) {
			switch ( character ) {
				case '\\': builder.Append( "\\\\" ); break;
				case '\n': builder.Append( "\\n" ); break;
				case '\r': builder.Append( "\\r" ); break;
				case '\t': builder.Append( "\\t" ); break;
				default:
					if ( char.IsControl( character ) ) {
						builder.Append( "\\u" );
						builder.Append( ((int)character).ToString( "x4", CultureInfo.InvariantCulture ) );
					} else {
						builder.Append( character );
					}
					break;
			}
		}
		return builder.ToString();
	}
}
