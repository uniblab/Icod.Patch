namespace Icod.Patch;

using System.Text;

/// <summary>Parses unified and context diff sections into immutable syntax models.</summary>
internal static class UnifiedContextPatchParser {
	/// <summary>Parses one detected unified or context section.</summary>
	/// <param name="section">The detected section metadata.</param>
	/// <param name="records">The exact records in the section.</param>
	/// <param name="budget">The parser resource budget.</param>
	/// <returns>The parsed file patch.</returns>
	public static PatchFilePatch Parse(
		PatchSection section,
		IReadOnlyList<PatchRawRecord> records,
		PatchParseBudget budget
	) {
		ArgumentNullException.ThrowIfNull( section );
		ArgumentNullException.ThrowIfNull( records );
		ArgumentNullException.ThrowIfNull( budget );
		return section.Format switch {
			PatchFormat.Unified => ParseUnified( section, records, budget ),
			PatchFormat.Context => ParseContext( section, records, budget ),
			_ => throw new ArgumentException( "section is not a unified or context patch", nameof( section ) )
		};
	}

	private static PatchFilePatch ParseUnified(
		PatchSection section,
		IReadOnlyList<PatchRawRecord> records,
		PatchParseBudget budget
	) {
		RequireHeaderPair( records, section );
		var oldHeader = PatchParsing.ParseFileHeader( records[0], "---", section.OldFileName );
		var newHeader = PatchParsing.ParseFileHeader( records[1], "+++", section.NewFileName );
		var hunks = new List<PatchHunk>();
		var index = 2;
		while ( index < records.Count ) {
			var hunkStart = index;
			var header = ParseUnifiedHunkHeader( records[index++] );
			var oldLines = new List<PatchDataLine>();
			var newLines = new List<PatchDataLine>();
			var sawChange = false;
			PatchDataLineTarget lastTarget = PatchDataLineTarget.None;
			while ( index < records.Count && !PatchParsing.StartsWithAscii( records[index].Content.Span, "@@" ) ) {
				var record = records[index];
				if ( PatchParsing.IsNoNewlineMarker( record ) ) {
					ApplyNoNewlineMarker( oldLines, newLines, lastTarget, record.Location );
					index++;
					lastTarget = PatchDataLineTarget.None;
					continue;
				}
				if ( 0 == record.Content.Length ) {
					throw new PatchInputException( "missing unified-diff line prefix", record.Location );
				}
				switch ( record.Content.Span[0] ) {
					case (byte)' ':
						var oldContext = PatchParsing.CreateDataLine( record, 1, true, budget );
						var newContext = new PatchDataLine(
							oldContext.Content.Span,
							oldContext.Terminator,
							true,
							oldContext.SourceLocation
						);
						budget.AddDataLine( newContext.Content.Length, record.Location );
						oldLines.Add( oldContext );
						newLines.Add( newContext );
						lastTarget = PatchDataLineTarget.Both;
						break;
					case (byte)'-':
						oldLines.Add( PatchParsing.CreateDataLine( record, 1, false, budget ) );
						sawChange = true;
						lastTarget = PatchDataLineTarget.Old;
						break;
					case (byte)'+':
						newLines.Add( PatchParsing.CreateDataLine( record, 1, false, budget ) );
						sawChange = true;
						lastTarget = PatchDataLineTarget.New;
						break;
					default:
						throw new PatchInputException( "malformed unified-diff hunk body", record.Location );
				}
				index++;
			}
			ValidateLineCount( header.OldRange.Count, oldLines.Count, "old", records[hunkStart].Location );
			ValidateLineCount( header.NewRange.Count, newLines.Count, "new", records[hunkStart].Location );
			if ( !sawChange ) {
				throw new PatchInputException(
					"unified-diff hunk contains no changes",
					records[hunkStart].Location
				);
			}
			budget.AddHunk( records[hunkStart].Location );
			hunks.Add(
				new PatchHunk(
					PatchParsing.DetermineOperation( header.OldRange.Count, header.NewRange.Count ),
					header.OldRange,
					header.NewRange,
					oldLines,
					newLines,
					header.SectionText,
					records[hunkStart].Location,
					CopyRecords( records, hunkStart, index )
				)
			);
		}
		if ( 0 == hunks.Count ) {
			throw new PatchInputException( "unified patch contains no hunks", records[0].Location );
		}
		return new PatchFilePatch(
			PatchFormat.Unified,
			PatchParsing.DetermineFileChangeKind( oldHeader, newHeader, records[0].Location ),
			oldHeader,
			newHeader,
			hunks,
			records[0].Location
		);
	}

	private static PatchFilePatch ParseContext(
		PatchSection section,
		IReadOnlyList<PatchRawRecord> records,
		PatchParseBudget budget
	) {
		RequireHeaderPair( records, section );
		var oldHeader = PatchParsing.ParseFileHeader( records[0], "***", section.OldFileName );
		var newHeader = PatchParsing.ParseFileHeader( records[1], "---", section.NewFileName );
		var hunks = new List<PatchHunk>();
		var index = 2;
		while ( index < records.Count ) {
			var hunkStart = index;
			if ( !IsContextSeparator( records[index] ) ) {
				throw new PatchInputException( "missing context-diff hunk separator", records[index].Location );
			}
			index++;
			if ( index >= records.Count ) {
				throw new PatchInputException( "missing old context-diff range", records[hunkStart].Location );
			}
			var oldRange = ParseContextRange( records[index++], oldSide: true );
			var oldLines = new List<PatchDataLine>();
			PatchDataLineTarget lastTarget = PatchDataLineTarget.None;
			while ( index < records.Count && !IsContextRange( records[index], oldSide: false ) ) {
				var record = records[index];
				if ( IsContextSeparator( record ) ) {
					throw new PatchInputException( "missing new context-diff range", record.Location );
				}
				if ( PatchParsing.IsNoNewlineMarker( record ) ) {
					ApplyNoNewlineMarker( oldLines, Array.Empty<PatchDataLine>(), lastTarget, record.Location );
					lastTarget = PatchDataLineTarget.None;
					index++;
					continue;
				}
				oldLines.Add( ParseContextDataLine( record, oldSide: true, budget ) );
				lastTarget = PatchDataLineTarget.Old;
				index++;
			}
			if ( index >= records.Count ) {
				throw new PatchInputException( "missing new context-diff range", records[hunkStart].Location );
			}
			var newRange = ParseContextRange( records[index++], oldSide: false );
			if ( 0 == oldRange.Count && 0 == newRange.Count ) {
				throw new PatchInputException(
					"context-diff hunk changes no lines",
					records[hunkStart].Location
				);
			}
			var newLines = new List<PatchDataLine>();
			lastTarget = PatchDataLineTarget.None;
			while ( index < records.Count && !IsContextSeparator( records[index] ) ) {
				var record = records[index];
				if ( PatchParsing.IsNoNewlineMarker( record ) ) {
					ApplyNoNewlineMarker( Array.Empty<PatchDataLine>(), newLines, lastTarget, record.Location );
					lastTarget = PatchDataLineTarget.None;
					index++;
					continue;
				}
				newLines.Add( ParseContextDataLine( record, oldSide: false, budget ) );
				lastTarget = PatchDataLineTarget.New;
				index++;
			}
			ValidateLineCount( oldRange.Count, oldLines.Count, "old", records[hunkStart].Location );
			ValidateLineCount( newRange.Count, newLines.Count, "new", records[hunkStart].Location );
			ValidateContextCopies( oldLines, newLines, records[hunkStart].Location );
			budget.AddHunk( records[hunkStart].Location );
			hunks.Add(
				new PatchHunk(
					PatchParsing.DetermineOperation( oldRange.Count, newRange.Count ),
					oldRange,
					newRange,
					oldLines,
					newLines,
					null,
					records[hunkStart].Location,
					CopyRecords( records, hunkStart, index )
				)
			);
		}
		if ( 0 == hunks.Count ) {
			throw new PatchInputException( "context patch contains no hunks", records[0].Location );
		}
		return new PatchFilePatch(
			PatchFormat.Context,
			PatchParsing.DetermineFileChangeKind( oldHeader, newHeader, records[0].Location ),
			oldHeader,
			newHeader,
			hunks,
			records[0].Location
		);
	}

	private static UnifiedHunkHeader ParseUnifiedHunkHeader( PatchRawRecord record ) {
		var value = record.Content.Span;
		if ( !PatchParsing.StartsWithAscii( value, "@@" ) ) {
			throw new PatchInputException( "missing unified-diff hunk header", record.Location );
		}
		var index = 2;
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index >= value.Length || (byte)'-' != value[index++] ) {
			throw new PatchInputException( "malformed unified-diff old range", record.Location );
		}
		var oldRange = ParseUnifiedRange( value, ref index, record.Location );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index >= value.Length || (byte)'+' != value[index++] ) {
			throw new PatchInputException( "malformed unified-diff new range", record.Location );
		}
		var newRange = ParseUnifiedRange( value, ref index, record.Location );
		if ( 0 == oldRange.Count && 0 == newRange.Count ) {
			throw new PatchInputException( "unified-diff hunk changes no lines", record.Location );
		}
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( !PatchParsing.StartsWithAscii( value[index..], "@@" ) ) {
			throw new PatchInputException( "unterminated unified-diff hunk header", record.Location );
		}
		index += 2;
		var sectionText = PatchParsing.DecodeTrimmedUtf8( value[index..] );
		return new UnifiedHunkHeader( oldRange, newRange, sectionText );
	}

	private static PatchRange ParseUnifiedRange(
		ReadOnlySpan<byte> value,
		ref int index,
		PatchSourceLocation location
	) {
		PatchParsing.SkipHorizontalSpace( value, ref index );
		var start = PatchParsing.ParseDecimal( value, ref index, location );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		long count = 1;
		if ( index < value.Length && (byte)',' == value[index] ) {
			index++;
			PatchParsing.SkipHorizontalSpace( value, ref index );
			count = PatchParsing.ParseDecimal( value, ref index, location );
		}
		if ( 0 == start && 0 != count ) {
			throw new PatchInputException( "nonempty unified range starts at zero", location );
		}
		return new PatchRange( start, count );
	}

	private static PatchRange ParseContextRange( PatchRawRecord record, bool oldSide ) {
		var value = record.Content.Span;
		var marker = oldSide ? "***" : "---";
		var suffix = oldSide ? "****" : "----";
		if ( !PatchParsing.StartsWithAscii( value, marker ) ) {
			throw new PatchInputException( "malformed context-diff range", record.Location );
		}
		var index = marker.Length;
		if ( index >= value.Length || !PatchParsing.IsHorizontalSpace( value[index] ) ) {
			throw new PatchInputException( "malformed context-diff range", record.Location );
		}
		PatchParsing.SkipHorizontalSpace( value, ref index );
		var start = PatchParsing.ParseDecimal( value, ref index, record.Location );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		var end = start;
		var hasComma = false;
		if ( index < value.Length && (byte)',' == value[index] ) {
			hasComma = true;
			index++;
			PatchParsing.SkipHorizontalSpace( value, ref index );
			end = PatchParsing.ParseDecimal( value, ref index, record.Location );
			PatchParsing.SkipHorizontalSpace( value, ref index );
		}
		if ( !PatchParsing.StartsWithAscii( value[index..], suffix ) ) {
			throw new PatchInputException( "malformed context-diff range terminator", record.Location );
		}
		index += suffix.Length;
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index != value.Length ) {
			throw new PatchInputException( "trailing garbage in context-diff range", record.Location );
		}
		if ( 0 == start ) {
			if ( hasComma && 0 != end ) {
				throw new PatchInputException( "invalid zero context-diff range", record.Location );
			}
			return new PatchRange( 0, 0 );
		}
		if ( end < start ) {
			throw new PatchInputException( "line range is reversed", record.Location );
		}
		long count;
		try {
			count = checked( end - start + 1 );
		} catch ( OverflowException ) {
			throw new PatchInputException( "line range is too large", record.Location );
		}
		return new PatchRange( start, count );
	}

	private static PatchDataLine ParseContextDataLine(
		PatchRawRecord record,
		bool oldSide,
		PatchParseBudget budget
	) {
		var value = record.Content.Span;
		if ( value.Length < 2 || (byte)' ' != value[1] ) {
			throw new PatchInputException( "malformed context-diff data line", record.Location );
		}
		var marker = value[0];
		var valid = oldSide
			? marker is (byte)' ' or (byte)'-' or (byte)'!'
			: marker is (byte)' ' or (byte)'+' or (byte)'!';
		if ( !valid ) {
			throw new PatchInputException( "invalid context-diff data marker", record.Location );
		}
		return PatchParsing.CreateDataLine( record, 2, (byte)' ' == marker, budget );
	}

	private static void ValidateContextCopies(
		IReadOnlyList<PatchDataLine> oldLines,
		IReadOnlyList<PatchDataLine> newLines,
		PatchSourceLocation location
	) {
		var oldContext = oldLines.Where( item => item.IsContext ).ToArray();
		var newContext = newLines.Where( item => item.IsContext ).ToArray();
		if ( oldContext.Length != newContext.Length ) {
			throw new PatchInputException( "context-diff copies have different lengths", location );
		}
		for ( var index = 0; index < oldContext.Length; index++ ) {
			if (
				oldContext[index].Terminator != newContext[index].Terminator
				|| !oldContext[index].Content.Span.SequenceEqual( newContext[index].Content.Span )
			) {
				throw new PatchInputException( "context-diff copies do not match", location );
			}
		}
	}

	private static void ApplyNoNewlineMarker(
		IList<PatchDataLine> oldLines,
		IList<PatchDataLine> newLines,
		PatchDataLineTarget target,
		PatchSourceLocation location
	) {
		if ( PatchDataLineTarget.Old == target || PatchDataLineTarget.Both == target ) {
			if ( 0 == oldLines.Count ) {
				throw new PatchInputException( "orphaned incomplete-line marker", location );
			}
			oldLines[^1] = oldLines[^1].WithTerminator( PatchLineTerminator.None );
		}
		if ( PatchDataLineTarget.New == target || PatchDataLineTarget.Both == target ) {
			if ( 0 == newLines.Count ) {
				throw new PatchInputException( "orphaned incomplete-line marker", location );
			}
			newLines[^1] = newLines[^1].WithTerminator( PatchLineTerminator.None );
		}
		if ( PatchDataLineTarget.None == target ) {
			throw new PatchInputException( "orphaned incomplete-line marker", location );
		}
	}

	private static void ValidateLineCount(
		long expected,
		int actual,
		string side,
		PatchSourceLocation location
	) {
		if ( expected != actual ) {
			throw new PatchInputException(
				string.Concat(
					"declared ",
					side,
					" hunk count does not match its body"
				),
				location
			);
		}
	}

	private static void RequireHeaderPair(
		IReadOnlyList<PatchRawRecord> records,
		PatchSection section
	) {
		if ( records.Count < 2 ) {
			var location = 0 < records.Count
				? records[0].Location
				: new PatchSourceLocation( 0, 1 );
			throw new PatchInputException( "incomplete patch file-header pair", location );
		}
		if ( string.IsNullOrEmpty( section.OldFileName ) || string.IsNullOrEmpty( section.NewFileName ) ) {
			throw new PatchInputException( "missing patch filename", records[0].Location );
		}
	}

	private static bool IsContextSeparator( PatchRawRecord record ) {
		var value = record.Content.Span;
		if ( value.Length < 8 ) {
			return false;
		}
		foreach ( var item in value ) {
			if ( (byte)'*' != item ) {
				return false;
			}
		}
		return true;
	}

	private static bool IsContextRange( PatchRawRecord record, bool oldSide ) {
		return PatchParsing.StartsWithAscii( record.Content.Span, oldSide ? "***" : "---" );
	}

	private static IReadOnlyList<PatchRawRecord> CopyRecords(
		IReadOnlyList<PatchRawRecord> records,
		int start,
		int end
	) {
		var result = new PatchRawRecord[end - start];
		for ( var index = start; index < end; index++ ) {
			result[index - start] = records[index];
		}
		return result;
	}

	private enum PatchDataLineTarget {
		None,
		Old,
		New,
		Both
	}

	private readonly struct UnifiedHunkHeader {
		/// <summary>Initializes a parsed unified-hunk header.</summary>
		/// <param name="oldRange">The old-side range.</param>
		/// <param name="newRange">The new-side range.</param>
		/// <param name="sectionText">The optional section heading.</param>
		public UnifiedHunkHeader( PatchRange oldRange, PatchRange newRange, string? sectionText ) {
			this.OldRange = oldRange;
			this.NewRange = newRange;
			this.SectionText = sectionText;
		}

		/// <summary>Gets the old-side range.</summary>
		public PatchRange OldRange { get; }

		/// <summary>Gets the new-side range.</summary>
		public PatchRange NewRange { get; }

		/// <summary>Gets the optional section heading.</summary>
		public string? SectionText { get; }
	}
}
