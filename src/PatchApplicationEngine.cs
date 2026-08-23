namespace Icod.Patch;

using System.IO;

/// <summary>Applies parsed patch hunks to virtual, byte-preserved target content.</summary>
internal static class PatchApplicationEngine {
	private sealed class LineReference {
		private readonly PatchTargetContent? source;
		private readonly int sourceIndex;
		private readonly byte[]? content;

		/// <summary>Initializes a source-backed or materialized output record.</summary>
		/// <param name="source">The source content, when source-backed.</param>
		/// <param name="sourceIndex">The source record index.</param>
		/// <param name="content">The materialized content, when not source-backed.</param>
		/// <param name="terminator">The represented record terminator.</param>
		/// <param name="contentLength">The content byte count.</param>
		public LineReference(
			PatchTargetContent? source,
			int sourceIndex,
			byte[]? content,
			PatchLineTerminator terminator,
			long contentLength
		) {
			this.source = source;
			this.sourceIndex = sourceIndex;
			this.content = content;
			this.Terminator = terminator;
			this.ContentLength = contentLength;
		}

		/// <summary>Gets the represented record terminator.</summary>
		public PatchLineTerminator Terminator { get; }

		/// <summary>Gets the content byte count excluding the terminator.</summary>
		public long ContentLength { get; }

		/// <summary>Gets the complete represented record byte count.</summary>
		public long TotalLength => checked( this.ContentLength + TerminatorLength( this.Terminator ) );

		/// <summary>Creates a source-backed record reference.</summary>
		/// <param name="source">The indexed target source.</param>
		/// <param name="index">The source record index.</param>
		/// <returns>The record reference.</returns>
		public static LineReference FromSource( PatchTargetContent source, int index ) {
			var record = source.Records[index];
			return new LineReference( source, index, null, record.Terminator, record.ContentLength );
		}

		/// <summary>Creates a materialized record from a parsed patch line.</summary>
		/// <param name="line">The patch line.</param>
		/// <returns>The record reference.</returns>
		public static LineReference FromPatch( PatchDataLine line ) {
			return new LineReference(
				null,
				-1,
				line.Content.ToArray(),
				line.Terminator,
				line.Content.Length
			);
		}

		/// <summary>Creates an LF-terminated ASCII merge marker.</summary>
		/// <param name="text">The marker text.</param>
		/// <returns>The marker record.</returns>
		public static LineReference Marker( string text ) {
			var bytes = System.Text.Encoding.ASCII.GetBytes( text );
			return new LineReference( null, -1, bytes, PatchLineTerminator.LineFeed, bytes.Length );
		}

		/// <summary>Reads the represented content bytes without the terminator.</summary>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The content bytes.</returns>
		public async Task<byte[]> ReadContentAsync( CancellationToken cancellationToken ) {
			if ( null != this.content ) {
				return this.content.ToArray();
			}
			return await this.source!.ReadRecordAsync(
				this.sourceIndex,
				includeTerminator: false,
				cancellationToken
			).ConfigureAwait( false );
		}

		/// <summary>Creates a reference with the same content and a different terminator.</summary>
		/// <param name="terminator">The replacement terminator.</param>
		/// <returns>The adjusted record reference.</returns>
		public LineReference WithTerminator( PatchLineTerminator terminator ) {
			return new LineReference(
				this.source,
				this.sourceIndex,
				this.content,
				terminator,
				this.ContentLength
			);
		}

		/// <summary>Writes the complete represented record.</summary>
		/// <param name="output">The destination stream.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>A task representing completion.</returns>
		public async Task WriteAsync( Stream output, CancellationToken cancellationToken ) {
			if ( null != this.content ) {
				await output.WriteAsync( this.content, cancellationToken ).ConfigureAwait( false );
			} else {
				await this.source!.WriteRecordToAsync(
					this.sourceIndex,
					output,
					includeTerminator: false,
					cancellationToken
				).ConfigureAwait( false );
			}
			await WriteTerminatorAsync( output, this.Terminator, cancellationToken ).ConfigureAwait( false );
		}
	}

	private sealed class HunkProjection {
		/// <summary>Gets the effective old-side range.</summary>
		public required PatchRange OldRange { get; init; }
		/// <summary>Gets the effective new-side range.</summary>
		public required PatchRange NewRange { get; init; }
		/// <summary>Gets the effective old-side logical records.</summary>
		public required IReadOnlyList<PatchDataLine> OldLines { get; init; }
		/// <summary>Gets the effective new-side logical records.</summary>
		public required IReadOnlyList<PatchDataLine> NewLines { get; init; }
	}

	private readonly struct MatchResult {
		/// <summary>Initializes a successful candidate match.</summary>
		/// <param name="index">The zero-based target index.</param>
		/// <param name="offset">The offset from the predicted position.</param>
		/// <param name="fuzz">The selected fuzz factor.</param>
		public MatchResult( int index, long offset, int fuzz ) {
			this.Index = index;
			this.Offset = offset;
			this.Fuzz = fuzz;
		}

		/// <summary>Gets the zero-based target index.</summary>
		public int Index { get; }
		/// <summary>Gets the offset from the predicted position.</summary>
		public long Offset { get; }
		/// <summary>Gets the selected fuzz factor.</summary>
		public int Fuzz { get; }
	}

	private sealed class ComparisonBudget {
		private readonly int maximum;
		private int used;

		/// <summary>Initializes a bounded comparison budget.</summary>
		/// <param name="maximum">The maximum number of comparisons.</param>
		public ComparisonBudget( int maximum ) {
			this.maximum = maximum;
		}

		/// <summary>Consumes one comparison allowance.</summary>
		public void Consume() {
			if ( this.maximum <= this.used ) {
				throw new PatchApplicationException( "merge matching exceeds the configured candidate limit" );
			}
			this.used++;
		}
	}


	/// <summary>Applies one parsed file patch without reading or writing live filesystem paths.</summary>
	/// <param name="input">The virtual input file.</param>
	/// <param name="patch">The parsed file patch.</param>
	/// <param name="options">The application options.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The immutable virtual result.</returns>
	public static async Task<PatchFileApplicationResult> ApplyAsync(
		PatchVirtualFile input,
		PatchFilePatch patch,
		PatchEngineOptions? options = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( input );
		ArgumentNullException.ThrowIfNull( patch );
		options ??= new PatchEngineOptions();
		options.Validate();
		if ( patch.Format == PatchFormat.EdScript && options.Reverse ) {
			throw new PatchApplicationException( "ed scripts cannot be reverse-applied without an old-side model" );
		}

		PatchTargetContent? emptyTarget = null;
		var source = input.Content;
		if ( null == source ) {
			emptyTarget = await PatchTargetContent.FromBytesAsync(
				ReadOnlyMemory<byte>.Empty,
				options.Limits.TargetLimits,
				cancellationToken
			).ConfigureAwait( false );
			source = emptyTarget;
		}
		try {
			var direction = options.Reverse ? PatchDirection.Reverse : PatchDirection.Forward;
			var effectiveChange = EffectiveChangeKind( patch.ChangeKind, direction );

			if ( null != options.PrerequisiteToken
				&& !await PatchPrerequisite.ContainsAsync( source, options.PrerequisiteToken, cancellationToken ).ConfigureAwait( false )
				&& !options.Force ) {
				var proceed = false;
				if ( !options.Batch && null != options.DecisionProvider ) {
					proceed = await options.DecisionProvider.DecideAsync(
						new PatchDecisionRequest(
							PatchDecisionKind.IgnoreMissingPrerequisite,
							string.Concat( "prerequisite '", options.PrerequisiteToken, "' was not found" )
						),
						cancellationToken
					).ConfigureAwait( false );
				}
				if ( !proceed ) {
					return await CreateSkippedAsync(
						input,
						source,
						patch,
						direction,
						options,
						PatchExitStatus.Trouble,
						cancellationToken
					).ConfigureAwait( false );
				}
			}

			var lines = new List<LineReference>( source.Records.Count );
			for ( var index = 0; index < source.Records.Count; index++ ) {
				lines.Add( LineReference.FromSource( source, index ) );
			}

			if ( !options.Reverse
				&& !options.Force
				&& PatchMergeStyle.None == options.MergeStyle
				&& patch.Format != PatchFormat.EdScript
				&& 0 < patch.Hunks.Count ) {
				var first = patch.Hunks[0];
				var forwardProjection = Project( first, PatchDirection.Forward );
				var predicted = PredictedIndex( forwardProjection.OldRange, 0, 0 );
				MatchResult? forwardMatch = null;
				if ( ValidateVirtualExistence(
					input.Exists,
					EffectiveChangeKind( patch.ChangeKind, PatchDirection.Forward )
				) ) {
					forwardMatch = await FindMatchAsync(
						lines,
						forwardProjection,
						predicted,
						patch.Format,
						options,
						cancellationToken
					).ConfigureAwait( false );
				}
				if ( null == forwardMatch ) {
					var reverseProjection = Project( first, PatchDirection.Reverse );
					var reversePredicted = PredictedIndex( reverseProjection.OldRange, 0, 0 );
					MatchResult? reverseMatch = null;
					if ( ValidateVirtualExistence(
						input.Exists,
						EffectiveChangeKind( patch.ChangeKind, PatchDirection.Reverse )
					) ) {
						reverseMatch = await FindMatchAsync(
							lines,
							reverseProjection,
							reversePredicted,
							patch.Format,
							options,
							cancellationToken
						).ConfigureAwait( false );
					}
					if ( null != reverseMatch ) {
						if ( options.ForwardOnly ) {
							return await CreateSkippedAsync(
								input,
								source,
								patch,
								PatchDirection.Forward,
								options,
								PatchExitStatus.PartialFailure,
								cancellationToken
							).ConfigureAwait( false );
						}
						var reverse = options.Batch;
						if ( !reverse && null != options.DecisionProvider ) {
							reverse = await options.DecisionProvider.DecideAsync(
								new PatchDecisionRequest(
									PatchDecisionKind.ReversePatch,
									"the first hunk appears to be reversed or already applied"
								),
								cancellationToken
							).ConfigureAwait( false );
						}
						if ( reverse ) {
							direction = PatchDirection.Reverse;
							effectiveChange = EffectiveChangeKind( patch.ChangeKind, direction );
						} else {
							return await CreateSkippedAsync(
								input,
								source,
								patch,
								PatchDirection.Forward,
								options,
								PatchExitStatus.PartialFailure,
								cancellationToken
							).ConfigureAwait( false );
						}
					}
				}
			}

			if ( !ValidateVirtualExistence( input.Exists, effectiveChange ) ) {
				return await CreateUnchangedFailureAsync(
					input,
					source,
					patch,
					direction,
					options,
					cancellationToken
				).ConfigureAwait( false );
			}

			var results = new List<PatchHunkResult>( patch.Hunks.Count );
			var status = new PatchExitStatusAccumulator();
			long cumulativeDelta = 0;
			long previousOffset = 0;
			foreach ( var hunk in patch.Hunks ) {
				cancellationToken.ThrowIfCancellationRequested();
				var projection = Project( hunk, direction );
				if ( patch.Format == PatchFormat.EdScript ) {
					var edIndex = EdIndex( projection.OldRange, lines.Count );
					ApplyEdAt( lines, edIndex, projection );
					results.Add( new PatchHunkResult( hunk, PatchHunkOutcome.Applied, edIndex, 0, 0 ) );
					continue;
				}
				var predicted = PredictedIndex( projection.OldRange, cumulativeDelta, previousOffset );
				var match = await FindMatchAsync(
					lines,
					projection,
					predicted,
					patch.Format,
					options,
					cancellationToken
				).ConfigureAwait( false );
				if ( null == match ) {
					if ( PatchMergeStyle.None != options.MergeStyle ) {
						var mergeIndex = ClampIndex( predicted, lines.Count );
						var mergeDelta = await MergeAtAsync(
							lines,
							mergeIndex,
							projection,
							options,
							cancellationToken
						).ConfigureAwait( false );
						results.Add( new PatchHunkResult( hunk, PatchHunkOutcome.Merged, mergeIndex, mergeIndex - predicted, 0 ) );
						status.Add( PatchExitStatus.PartialFailure );
						cumulativeDelta = checked( cumulativeDelta + mergeDelta );
						previousOffset = mergeIndex - predicted;
						continue;
					}
					results.Add( new PatchHunkResult( hunk, PatchHunkOutcome.Failed, null, 0, 0 ) );
					status.Add( PatchExitStatus.PartialFailure );
					continue;
				}
				ApplyAt( lines, match.Value.Index, projection );
				results.Add(
					new PatchHunkResult(
						hunk,
						PatchHunkOutcome.Applied,
						match.Value.Index,
						match.Value.Offset,
						match.Value.Fuzz
					)
				);
				cumulativeDelta = checked( cumulativeDelta + projection.NewLines.Count - projection.OldLines.Count );
				previousOffset = match.Value.Offset;
			}

			var resultExists = DetermineResultExists(
				input.Exists,
				effectiveChange,
				status.Status,
				results
			);
			PatchTargetContent? outputContent = null;
			if ( resultExists ) {
				outputContent = await BuildContentAsync( lines, options, cancellationToken ).ConfigureAwait( false );
			}
			return new PatchFileApplicationResult(
				new PatchVirtualFile( resultExists, outputContent ),
				direction,
				results,
				status.Status
			);
		} finally {
			if ( null != emptyTarget ) {
				await emptyTarget.DisposeAsync().ConfigureAwait( false );
			}
		}
	}

	private static HunkProjection Project( PatchHunk hunk, PatchDirection direction ) {
		if ( PatchDirection.Forward == direction ) {
			return new HunkProjection {
				OldRange = hunk.OldRange,
				NewRange = hunk.NewRange ?? new PatchRange( hunk.OldRange.Start, hunk.NewLines.Count ),
				OldLines = hunk.OldLines,
				NewLines = hunk.NewLines
			};
		}
		if ( null == hunk.NewRange ) {
			throw new PatchApplicationException( "the selected patch format does not contain reverse range information" );
		}
		return new HunkProjection {
			OldRange = hunk.NewRange.Value,
			NewRange = hunk.OldRange,
			OldLines = hunk.NewLines,
			NewLines = hunk.OldLines
		};
	}

	private static long PredictedIndex( PatchRange range, long cumulativeDelta, long previousOffset ) {
		try {
			var baseIndex = 0 == range.Count ? range.Start : checked( range.Start - 1 );
			return checked( checked( baseIndex + cumulativeDelta ) + previousOffset );
		} catch ( OverflowException ) {
			throw new PatchApplicationException( "predicted hunk position is outside the supported range" );
		}
	}

	private static async Task<MatchResult?> FindMatchAsync(
		IReadOnlyList<LineReference> lines,
		HunkProjection projection,
		long predicted,
		PatchFormat format,
		PatchEngineOptions options,
		CancellationToken cancellationToken
	) {
		var oldCount = projection.OldLines.Count;
		if ( 0 == oldCount ) {
			if ( 0 <= predicted && predicted <= lines.Count ) {
				return new MatchResult( checked( (int)predicted ), 0, 0 );
			}
			return null;
		}
		var maximumStart = lines.Count - oldCount;
		if ( maximumStart < 0 ) {
			return null;
		}
		var maximumContext = Math.Max(
			CountLeadingContext( projection.OldLines ),
			CountTrailingContext( projection.OldLines )
		);
		var maximumFuzz = PatchMergeStyle.None == options.MergeStyle
			&& ( format is PatchFormat.Unified or PatchFormat.Context )
			? Math.Min( options.Fuzz, maximumContext )
			: 0;
		var checks = 0;
		for ( var fuzz = 0; fuzz <= maximumFuzz; fuzz++ ) {
			if ( predicted < 0 ) {
				for ( long candidate = 0; candidate <= maximumStart; candidate++ ) {
					var match = await TryCandidateAsync( checked( (int)candidate ), fuzz ).ConfigureAwait( false );
					if ( null != match ) {
						return match;
					}
				}
				continue;
			}
			if ( maximumStart < predicted ) {
				for ( long candidate = maximumStart; 0 <= candidate; candidate-- ) {
					var match = await TryCandidateAsync( checked( (int)candidate ), fuzz ).ConfigureAwait( false );
					if ( null != match ) {
						return match;
					}
				}
				continue;
			}
			var directIndex = checked( (int)predicted );
			var direct = await TryCandidateAsync( directIndex, fuzz ).ConfigureAwait( false );
			if ( null != direct ) {
				return direct;
			}
			for ( long distance = 1;
				(long)directIndex + distance <= maximumStart || 0 <= (long)directIndex - distance;
				distance++ ) {
				cancellationToken.ThrowIfCancellationRequested();
				var forward = (long)directIndex + distance;
				if ( forward <= maximumStart ) {
					var match = await TryCandidateAsync( checked( (int)forward ), fuzz ).ConfigureAwait( false );
					if ( null != match ) {
						return match;
					}
				}
				var backward = (long)directIndex - distance;
				if ( 0 <= backward ) {
					var match = await TryCandidateAsync( checked( (int)backward ), fuzz ).ConfigureAwait( false );
					if ( null != match ) {
						return match;
					}
				}
			}
		}
		return null;

		async Task<MatchResult?> TryCandidateAsync( int candidate, int fuzz ) {
			cancellationToken.ThrowIfCancellationRequested();
			if ( options.Limits.MaximumCandidateChecks <= checks ) {
				throw new PatchApplicationException( "hunk matching exceeds the configured candidate limit" );
			}
			checks++;
			var matches = await MatchesAtAsync(
				lines,
				candidate,
				projection.OldLines,
				fuzz,
				options.IgnoreWhitespace,
				cancellationToken
			).ConfigureAwait( false );
			if ( !matches ) {
				return null;
			}
			long offset;
			try {
				offset = checked( (long)candidate - predicted );
			} catch ( OverflowException ) {
				throw new PatchApplicationException( "hunk offset is outside the supported range" );
			}
			return new MatchResult( candidate, offset, fuzz );
		}
	}

	private static async Task<bool> MatchesAtAsync(
		IReadOnlyList<LineReference> target,
		int start,
		IReadOnlyList<PatchDataLine> expected,
		int fuzz,
		bool ignoreWhitespace,
		CancellationToken cancellationToken
	) {
		var leading = CountLeadingContext( expected );
		var trailing = CountTrailingContext( expected );
		var skipLeading = Math.Min( fuzz, leading );
		var skipTrailing = Math.Min( fuzz, trailing );
		var end = expected.Count - skipTrailing;
		for ( var index = skipLeading; index < end; index++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			var actual = target[start + index];
			var wanted = expected[index];
			if ( actual.Terminator != wanted.Terminator
				|| ( !ignoreWhitespace && actual.ContentLength != wanted.Content.Length ) ) {
				return false;
			}
			var actualBytes = await actual.ReadContentAsync( cancellationToken ).ConfigureAwait( false );
			if ( !ContentEquals( actualBytes, wanted.Content.Span, ignoreWhitespace ) ) {
				return false;
			}
		}
		return true;
	}

	private static void ApplyAt( List<LineReference> lines, int index, HunkProjection projection ) {
		if ( index < 0 || lines.Count < index + projection.OldLines.Count ) {
			throw new PatchApplicationException( "hunk range is outside the virtual target" );
		}
		var replacement = new List<LineReference>( projection.NewLines.Count );
		var oldIndex = 0;
		var newIndex = 0;
		while ( oldIndex < projection.OldLines.Count || newIndex < projection.NewLines.Count ) {
			while ( oldIndex < projection.OldLines.Count && !projection.OldLines[oldIndex].IsContext ) {
				oldIndex++;
			}
			while ( newIndex < projection.NewLines.Count && !projection.NewLines[newIndex].IsContext ) {
				replacement.Add( LineReference.FromPatch( projection.NewLines[newIndex] ) );
				newIndex++;
			}
			if ( oldIndex < projection.OldLines.Count || newIndex < projection.NewLines.Count ) {
				if ( projection.OldLines.Count <= oldIndex
					|| projection.NewLines.Count <= newIndex
					|| !projection.OldLines[oldIndex].IsContext
					|| !projection.NewLines[newIndex].IsContext ) {
					throw new PatchApplicationException( "old and new context anchors are inconsistent" );
				}
				replacement.Add( lines[index + oldIndex] );
				oldIndex++;
				newIndex++;
			}
		}
		lines.RemoveRange( index, projection.OldLines.Count );
		lines.InsertRange( index, replacement );
	}

	private static void ApplyEdAt( List<LineReference> lines, int index, HunkProjection projection ) {
		if ( int.MaxValue < projection.OldRange.Count ) {
			throw new PatchApplicationException( "ed command range is too large" );
		}
		var removeCount = checked( (int)projection.OldRange.Count );
		if ( index < 0 || lines.Count < index + removeCount ) {
			throw new PatchApplicationException( "ed command range is outside the virtual target" );
		}
		lines.RemoveRange( index, removeCount );
		lines.InsertRange( index, projection.NewLines.Select( LineReference.FromPatch ) );
	}

	private static int EdIndex( PatchRange range, int lineCount ) {
		long index = 0 == range.Count ? range.Start : range.Start - 1;
		if ( index < 0 || lineCount < index || lineCount < index + range.Count ) {
			throw new PatchApplicationException( "ed command range is outside the virtual target" );
		}
		return checked( (int)index );
	}

	private static async Task<int> MergeAtAsync(
		List<LineReference> lines,
		int index,
		HunkProjection projection,
		PatchEngineOptions options,
		CancellationToken cancellationToken
	) {
		var style = options.MergeStyle;
		var baseLines = projection.OldLines.Select( LineReference.FromPatch ).ToList();
		var newLines = projection.NewLines.Select( LineReference.FromPatch ).ToList();
		var leadingContext = CountLeadingContext( projection.OldLines );
		var trailingContext = CountTrailingContext( projection.OldLines );
		var budget = new ComparisonBudget( options.Limits.MaximumCandidateChecks );

		var prefix = await MatchingPrefixAsync(
			lines,
			index,
			baseLines,
			leadingContext,
			options.IgnoreWhitespace,
			budget,
			cancellationToken
		).ConfigureAwait( false );

		int? suffixStart = null;
		if ( 0 < trailingContext ) {
			var expectedSuffix = Math.Min(
				lines.Count - trailingContext,
				checked( index + Math.Max( prefix, baseLines.Count - trailingContext ) )
			);
			suffixStart = await FindSequenceNearAsync(
				lines,
				baseLines,
				baseLines.Count - trailingContext,
				trailingContext,
				expectedSuffix,
				checked( index + prefix ),
				options.IgnoreWhitespace,
				budget,
				cancellationToken
			).ConfigureAwait( false );
		}

		var currentStart = checked( index + prefix );
		var currentEnd = currentStart;
		var consumed = prefix;
		var suffix = 0;
		if ( null != suffixStart ) {
			currentEnd = suffixStart.Value;
			suffix = trailingContext;
			consumed = checked( suffixStart.Value + suffix - index );
		} else {
			var coreStart = leadingContext;
			var coreCount = Math.Max( 0, baseLines.Count - leadingContext - trailingContext );
			var matchedCoreEnd = await FindOrderedSequenceEndAsync(
				lines,
				currentStart,
				baseLines,
				coreStart,
				coreCount,
				options.IgnoreWhitespace,
				budget,
				cancellationToken
			).ConfigureAwait( false );
			if ( null != matchedCoreEnd ) {
				currentEnd = matchedCoreEnd.Value;
				consumed = checked( currentEnd - index );
			} else if ( 0 == trailingContext && 0 < prefix ) {
				var remaining = lines.Count - currentStart;
				if ( remaining == coreCount ) {
					currentEnd = lines.Count;
					consumed = checked( currentEnd - index );
				} else {
					// A hunk anchored only at its beginning is not allowed to
					// absorb unrelated trailing input merely because that prefix
					// happens to agree.
					prefix = 0;
					currentStart = index;
					currentEnd = index;
					consumed = 0;
				}
			}
		}

		var baseConflictStart = prefix;
		var newConflictStart = prefix;
		var baseConflictEnd = suffixStart.HasValue
			? baseLines.Count - suffix
			: baseLines.Count;
		var newConflictEnd = suffixStart.HasValue
			? newLines.Count - suffix
			: newLines.Count;
		var currentCount = Math.Max( 0, currentEnd - currentStart );
		var replacement = new List<LineReference>();
		replacement.AddRange( lines.Skip( index ).Take( prefix ) );
		EnsureTerminatedBeforeMarker( replacement );
		replacement.Add( LineReference.Marker( "<<<<<<<" ) );
		replacement.AddRange( lines.Skip( currentStart ).Take( currentCount ) );
		if ( PatchMergeStyle.Diff3 == style ) {
			EnsureTerminatedBeforeMarker( replacement );
			replacement.Add( LineReference.Marker( "|||||||" ) );
			replacement.AddRange(
				baseLines.Skip( baseConflictStart ).Take( Math.Max( 0, baseConflictEnd - baseConflictStart ) )
			);
		}
		EnsureTerminatedBeforeMarker( replacement );
		replacement.Add( LineReference.Marker( "=======" ) );
		replacement.AddRange(
			newLines.Skip( newConflictStart ).Take( Math.Max( 0, newConflictEnd - newConflictStart ) )
		);
		EnsureTerminatedBeforeMarker( replacement );
		replacement.Add( LineReference.Marker( ">>>>>>>" ) );
		if ( 0 < suffix && null != suffixStart ) {
			replacement.AddRange( lines.Skip( suffixStart.Value ).Take( suffix ) );
		}
		lines.RemoveRange( index, consumed );
		lines.InsertRange( index, replacement );
		return checked( replacement.Count - consumed );
	}

	private static void EnsureTerminatedBeforeMarker( List<LineReference> output ) {
		if ( 0 < output.Count && PatchLineTerminator.None == output[^1].Terminator ) {
			output[^1] = output[^1].WithTerminator( PatchLineTerminator.LineFeed );
		}
	}

	private static async Task<int> MatchingPrefixAsync(
		IReadOnlyList<LineReference> target,
		int targetStart,
		IReadOnlyList<LineReference> expected,
		int maximum,
		bool ignoreWhitespace,
		ComparisonBudget budget,
		CancellationToken cancellationToken
	) {
		var limit = Math.Min( maximum, Math.Min( expected.Count, target.Count - targetStart ) );
		var count = 0;
		while ( count < limit ) {
			budget.Consume();
			if ( !await LinesEqualAsync(
				target[targetStart + count],
				expected[count],
				ignoreWhitespace,
				cancellationToken
			).ConfigureAwait( false ) ) {
				break;
			}
			count++;
		}
		return count;
	}

	private static async Task<int?> FindSequenceNearAsync(
		IReadOnlyList<LineReference> target,
		IReadOnlyList<LineReference> expected,
		int expectedStart,
		int expectedCount,
		int predicted,
		int minimumStart,
		bool ignoreWhitespace,
		ComparisonBudget budget,
		CancellationToken cancellationToken
	) {
		if ( 0 == expectedCount ) {
			return Math.Clamp( predicted, minimumStart, target.Count );
		}
		var maximumStart = target.Count - expectedCount;
		if ( maximumStart < minimumStart ) {
			return null;
		}
		predicted = Math.Clamp( predicted, minimumStart, maximumStart );
		for ( long distance = 0; ; distance++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			var any = false;
			var forward = (long)predicted + distance;
			if ( forward <= maximumStart ) {
				any = true;
				if ( await SequenceEqualsAtAsync(
					target,
					checked( (int)forward ),
					expected,
					expectedStart,
					expectedCount,
					ignoreWhitespace,
					budget,
					cancellationToken
				).ConfigureAwait( false ) ) {
					return checked( (int)forward );
				}
			}
			if ( 0 < distance ) {
				var backward = (long)predicted - distance;
				if ( minimumStart <= backward ) {
					any = true;
					if ( await SequenceEqualsAtAsync(
						target,
						checked( (int)backward ),
						expected,
						expectedStart,
						expectedCount,
						ignoreWhitespace,
						budget,
						cancellationToken
					).ConfigureAwait( false ) ) {
						return checked( (int)backward );
					}
				}
			}
			if ( !any ) {
				return null;
			}
		}
	}

	private static async Task<bool> SequenceEqualsAtAsync(
		IReadOnlyList<LineReference> target,
		int targetStart,
		IReadOnlyList<LineReference> expected,
		int expectedStart,
		int count,
		bool ignoreWhitespace,
		ComparisonBudget budget,
		CancellationToken cancellationToken
	) {
		for ( var offset = 0; offset < count; offset++ ) {
			budget.Consume();
			if ( !await LinesEqualAsync(
				target[targetStart + offset],
				expected[expectedStart + offset],
				ignoreWhitespace,
				cancellationToken
			).ConfigureAwait( false ) ) {
				return false;
			}
		}
		return true;
	}

	private static async Task<int?> FindOrderedSequenceEndAsync(
		IReadOnlyList<LineReference> target,
		int targetStart,
		IReadOnlyList<LineReference> expected,
		int expectedStart,
		int expectedCount,
		bool ignoreWhitespace,
		ComparisonBudget budget,
		CancellationToken cancellationToken
	) {
		if ( 0 == expectedCount ) {
			return null;
		}
		var expectedOffset = 0;
		for ( var targetIndex = targetStart; targetIndex < target.Count; targetIndex++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			budget.Consume();
			if ( await LinesEqualAsync(
				target[targetIndex],
				expected[expectedStart + expectedOffset],
				ignoreWhitespace,
				cancellationToken
			).ConfigureAwait( false ) ) {
				expectedOffset++;
				if ( expectedCount == expectedOffset ) {
					return checked( targetIndex + 1 );
				}
			}
		}
		return null;
	}

	private static async Task<bool> LinesEqualAsync(
		LineReference left,
		LineReference right,
		bool ignoreWhitespace,
		CancellationToken cancellationToken
	) {
		if ( left.Terminator != right.Terminator ) {
			return false;
		}
		if ( !ignoreWhitespace && left.ContentLength != right.ContentLength ) {
			return false;
		}
		var leftBytes = await left.ReadContentAsync( cancellationToken ).ConfigureAwait( false );
		var rightBytes = await right.ReadContentAsync( cancellationToken ).ConfigureAwait( false );
		return ContentEquals( leftBytes, rightBytes, ignoreWhitespace );
	}


	private static async Task<PatchTargetContent> BuildContentAsync(
		IReadOnlyList<LineReference> lines,
		PatchEngineOptions options,
		CancellationToken cancellationToken
	) {
		if ( options.Limits.TargetLimits.MaximumRecords < lines.Count ) {
			throw new PatchApplicationException( "patched output exceeds the configured record limit" );
		}
		long length = 0;
		foreach ( var line in lines ) {
			try {
				length = checked( length + line.TotalLength );
			} catch ( OverflowException ) {
				throw new PatchApplicationException( "patched output size is too large" );
			}
		}
		if ( options.Limits.MaximumOutputBytes < length ) {
			throw new PatchApplicationException( "patched output exceeds the configured byte limit" );
		}
		if ( length <= options.Limits.TargetLimits.MemoryThresholdBytes && length <= int.MaxValue ) {
			using var memory = new MemoryStream( checked( (int)length ) );
			foreach ( var line in lines ) {
				await line.WriteAsync( memory, cancellationToken ).ConfigureAwait( false );
			}
			memory.Position = 0;
			return await PatchTargetContent.ReadAsync(
				memory,
				options.Limits.TargetLimits,
				cancellationToken
			).ConfigureAwait( false );
		}
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-result-", Guid.NewGuid().ToString( "N" ), ".tmp" )
		);
		try {
			await using ( var temporary = PatchTemporaryFile.CreateNew(
				path,
				FileOptions.Asynchronous | FileOptions.SequentialScan
			) ) {
				foreach ( var line in lines ) {
					await line.WriteAsync( temporary, cancellationToken ).ConfigureAwait( false );
				}
				await temporary.FlushAsync( cancellationToken ).ConfigureAwait( false );
				temporary.Position = 0;
				return await PatchTargetContent.ReadAsync(
					temporary,
					options.Limits.TargetLimits,
					cancellationToken
				).ConfigureAwait( false );
			}
		} finally {
			try {
				File.Delete( path );
			} catch ( IOException ) {
			} catch ( UnauthorizedAccessException ) {
			}
		}
	}

	private static async Task<PatchFileApplicationResult> CreateSkippedAsync(
		PatchVirtualFile input,
		PatchTargetContent source,
		PatchFilePatch patch,
		PatchDirection direction,
		PatchEngineOptions options,
		PatchExitStatus status,
		CancellationToken cancellationToken
	) {
		var file = await CloneInputAsync( input, source, options, cancellationToken ).ConfigureAwait( false );
		var results = patch.Hunks.Select(
			hunk => new PatchHunkResult( hunk, PatchHunkOutcome.Skipped, null, 0, 0 )
		).ToArray();
		return new PatchFileApplicationResult( file, direction, results, status );
	}

	private static async Task<PatchFileApplicationResult> CreateUnchangedFailureAsync(
		PatchVirtualFile input,
		PatchTargetContent source,
		PatchFilePatch patch,
		PatchDirection direction,
		PatchEngineOptions options,
		CancellationToken cancellationToken
	) {
		var file = await CloneInputAsync( input, source, options, cancellationToken ).ConfigureAwait( false );
		var results = patch.Hunks.Select(
			hunk => new PatchHunkResult( hunk, PatchHunkOutcome.Failed, null, 0, 0 )
		).ToArray();
		return new PatchFileApplicationResult( file, direction, results, PatchExitStatus.PartialFailure );
	}

	private static async Task<PatchVirtualFile> CloneInputAsync(
		PatchVirtualFile input,
		PatchTargetContent source,
		PatchEngineOptions options,
		CancellationToken cancellationToken
	) {
		if ( !input.Exists ) {
			return new PatchVirtualFile( false, null );
		}
		var lines = new List<LineReference>( source.Records.Count );
		for ( var index = 0; index < source.Records.Count; index++ ) {
			lines.Add( LineReference.FromSource( source, index ) );
		}
		var clone = await BuildContentAsync( lines, options, cancellationToken ).ConfigureAwait( false );
		return new PatchVirtualFile( true, clone );
	}

	private static PatchFileChangeKind EffectiveChangeKind(
		PatchFileChangeKind changeKind,
		PatchDirection direction
	) {
		if ( PatchDirection.Forward == direction ) {
			return changeKind;
		}
		return changeKind switch {
			PatchFileChangeKind.Create => PatchFileChangeKind.Delete,
			PatchFileChangeKind.Delete => PatchFileChangeKind.Create,
			_ => changeKind
		};
	}

	private static bool ValidateVirtualExistence( bool exists, PatchFileChangeKind changeKind ) {
		return changeKind switch {
			PatchFileChangeKind.Create => !exists,
			PatchFileChangeKind.Delete or PatchFileChangeKind.Modify => exists,
			_ => true
		};
	}

	private static bool DetermineResultExists(
		bool inputExists,
		PatchFileChangeKind changeKind,
		PatchExitStatus status,
		IReadOnlyList<PatchHunkResult> results
	) {
		if ( PatchExitStatus.Success == status ) {
			return changeKind switch {
				PatchFileChangeKind.Create => true,
				PatchFileChangeKind.Delete => false,
				_ => inputExists
			};
		}
		if ( PatchFileChangeKind.Create == changeKind ) {
			return results.Any(
				item => item.Outcome is PatchHunkOutcome.Applied or PatchHunkOutcome.Merged
			);
		}
		return inputExists;
	}

	private static int CountLeadingContext( IReadOnlyList<PatchDataLine> lines ) {
		var count = 0;
		while ( count < lines.Count && lines[count].IsContext ) {
			count++;
		}
		return count;
	}

	private static int CountTrailingContext( IReadOnlyList<PatchDataLine> lines ) {
		var count = 0;
		while ( count < lines.Count && lines[lines.Count - 1 - count].IsContext ) {
			count++;
		}
		return count;
	}

	private static bool ContentEquals(
		ReadOnlySpan<byte> actual,
		ReadOnlySpan<byte> expected,
		bool ignoreWhitespace
	) {
		if ( !ignoreWhitespace ) {
			return actual.SequenceEqual( expected );
		}
		var actualIndex = 0;
		var expectedIndex = 0;
		while ( actualIndex < actual.Length && expectedIndex < expected.Length ) {
			var actualBlank = IsHorizontalBlank( actual[actualIndex] );
			var expectedBlank = IsHorizontalBlank( expected[expectedIndex] );
			if ( actualBlank || expectedBlank ) {
				if ( !actualBlank || !expectedBlank ) {
					return false;
				}
				while ( actualIndex < actual.Length && IsHorizontalBlank( actual[actualIndex] ) ) {
					actualIndex++;
				}
				while ( expectedIndex < expected.Length && IsHorizontalBlank( expected[expectedIndex] ) ) {
					expectedIndex++;
				}
				continue;
			}
			if ( actual[actualIndex] != expected[expectedIndex] ) {
				return false;
			}
			actualIndex++;
			expectedIndex++;
		}
		return actualIndex == actual.Length && expectedIndex == expected.Length;
	}

	private static bool IsHorizontalBlank( byte value ) => (byte)' ' == value || (byte)'	' == value;

	private static int ClampIndex( long value, int lineCount ) {
		if ( value <= 0 ) {
			return 0;
		}
		if ( lineCount <= value ) {
			return lineCount;
		}
		return checked( (int)value );
	}

	private static int TerminatorLength( PatchLineTerminator terminator ) {
		return terminator switch {
			PatchLineTerminator.None => 0,
			PatchLineTerminator.CarriageReturnLineFeed => 2,
			_ => 1
		};
	}

	private static async Task WriteTerminatorAsync(
		Stream output,
		PatchLineTerminator terminator,
		CancellationToken cancellationToken
	) {
		switch ( terminator ) {
			case PatchLineTerminator.None:
				return;
			case PatchLineTerminator.LineFeed:
				await output.WriteAsync( new byte[] { (byte)'\n' }, cancellationToken ).ConfigureAwait( false );
				return;
			case PatchLineTerminator.CarriageReturn:
				await output.WriteAsync( new byte[] { (byte)'\r' }, cancellationToken ).ConfigureAwait( false );
				return;
			case PatchLineTerminator.CarriageReturnLineFeed:
				await output.WriteAsync( new byte[] { (byte)'\r', (byte)'\n' }, cancellationToken ).ConfigureAwait( false );
				return;
			default:
				throw new InvalidOperationException( "unknown line terminator" );
		}
	}
}
