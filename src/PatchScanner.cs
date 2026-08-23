namespace Icod.Patch;

using System.Text;

/// <summary>Classifies structural patch lines without interpreting hunk contents.</summary>
internal enum PatchProbeKind {
	/// <summary>An ordinary non-structural line.</summary>
	Other,
	/// <summary>An empty line.</summary>
	Empty,
	/// <summary>A line beginning with the unified/context dash-header marker.</summary>
	DashHeader,
	/// <summary>A unified new-file header.</summary>
	UnifiedNewHeader,
	/// <summary>A context old-file header.</summary>
	ContextOldHeader,
	/// <summary>A unified hunk header.</summary>
	UnifiedHunk,
	/// <summary>A context separator line.</summary>
	ContextSeparator,
	/// <summary>A context hunk range line.</summary>
	ContextRange,
	/// <summary>A normal-diff or ed command line.</summary>
	NumericDirective,
	/// <summary>A normal-diff old-data line.</summary>
	NormalOldData,
	/// <summary>A normal-diff new-data line.</summary>
	NormalNewData,
	/// <summary>The normal-diff change separator.</summary>
	NormalSeparator,
	/// <summary>An incomplete-final-line marker.</summary>
	NoNewlineMarker,
	/// <summary>A single dot terminating ed input text.</summary>
	EdDot,
	/// <summary>GNU Diffutils' dot-unprotection substitution.</summary>
	EdSubstitute,
	/// <summary>The append command used after GNU dot unprotection.</summary>
	EdAppend
}

/// <summary>Contains the bounded structural classification of one source record.</summary>
internal readonly struct PatchLineProbe {
	/// <summary>Initializes a line probe.</summary>
	/// <param name="kind">The structural kind.</param>
	/// <param name="fileName">A parsed file name, when present.</param>
	/// <param name="firstByte">The first content byte, or zero for an empty line.</param>
	/// <param name="secondByte">The second content byte, or zero when absent.</param>
	/// <param name="oldLineCount">The declared unified old-side count, when parsed.</param>
	/// <param name="newLineCount">The declared unified new-side count, when parsed.</param>
	/// <param name="rangeLineCount">The declared context-range count, when parsed.</param>
	/// <param name="hasRightRange">Whether a numeric directive has a normal-diff right range.</param>
	/// <param name="directiveOperation">The numeric directive operation byte, when present.</param>
	public PatchLineProbe(
		PatchProbeKind kind,
		string? fileName,
		byte firstByte,
		byte secondByte,
		long oldLineCount = -1,
		long newLineCount = -1,
		long rangeLineCount = -1,
		bool hasRightRange = false,
		byte directiveOperation = 0
	) {
		this.Kind = kind;
		this.FileName = fileName;
		this.FirstByte = firstByte;
		this.SecondByte = secondByte;
		this.OldLineCount = oldLineCount;
		this.NewLineCount = newLineCount;
		this.RangeLineCount = rangeLineCount;
		this.HasRightRange = hasRightRange;
		this.DirectiveOperation = directiveOperation;
	}

	/// <summary>Gets the structural kind.</summary>
	public PatchProbeKind Kind { get; }

	/// <summary>Gets the parsed header file name, when present.</summary>
	public string? FileName { get; }

	/// <summary>Gets the first content byte, or zero for an empty line.</summary>
	public byte FirstByte { get; }

	/// <summary>Gets the second content byte, or zero when absent.</summary>
	public byte SecondByte { get; }

	/// <summary>Gets the declared unified old-side count, or -1 when unavailable.</summary>
	public long OldLineCount { get; }

	/// <summary>Gets the declared unified new-side count, or -1 when unavailable.</summary>
	public long NewLineCount { get; }

	/// <summary>Gets the declared context-range count, or -1 when unavailable.</summary>
	public long RangeLineCount { get; }

	/// <summary>Gets whether a numeric directive has a normal-diff right range.</summary>
	public bool HasRightRange { get; }

	/// <summary>Gets the numeric directive operation byte, or zero when unavailable.</summary>
	public byte DirectiveOperation { get; }
}

/// <summary>Detects patch-section candidates while preserving parsing for later phases.</summary>
internal static class PatchScanner {

	/// <summary>Classifies one byte-oriented source record.</summary>
	/// <param name="line">The record bytes excluding its terminator.</param>
	/// <param name="location">The source location.</param>
	/// <returns>The bounded structural probe.</returns>
	public static PatchLineProbe ClassifyLine(
		ReadOnlySpan<byte> line,
		PatchSourceLocation location
	) {
		var first = 0 < line.Length ? line[0] : (byte)0;
		var second = 1 < line.Length ? line[1] : (byte)0;
		if ( 0 == line.Length ) {
			return new PatchLineProbe( PatchProbeKind.Empty, null, first, second );
		}
		var containsNul = 0 <= line.IndexOf( (byte)0 );
		if (
			containsNul
			&& ( LooksHeaderDirectiveLike( line ) || LooksNumericDirectiveLikeWithNul( line ) )
		) {
			throw new PatchInputException( "NUL byte in patch directive", location );
		}
		if ( IsNoNewlineMarker( line ) ) {
			return new PatchLineProbe( PatchProbeKind.NoNewlineMarker, null, first, second );
		}
		if ( IsContextSeparator( line ) ) {
			return new PatchLineProbe( PatchProbeKind.ContextSeparator, null, first, second );
		}
		if ( StartsWithAscii( line, "@@" ) ) {
			TryParseUnifiedHunkCounts( line, location, out var oldCount, out var newCount );
			return new PatchLineProbe(
				PatchProbeKind.UnifiedHunk,
				null,
				first,
				second,
				oldCount,
				newCount
			);
		}
		if ( TryParseContextRange( line, location, out var rangeCount ) ) {
			return new PatchLineProbe(
				PatchProbeKind.ContextRange,
				null,
				first,
				second,
				rangeLineCount: rangeCount
			);
		}
		if ( StartsHeader( line, "+++" ) ) {
			return new PatchLineProbe(
				PatchProbeKind.UnifiedNewHeader,
				ParseHeaderFileName( line[3..], location ),
				first,
				second
			);
		}
		if ( StartsHeader( line, "***" ) ) {
			return new PatchLineProbe(
				PatchProbeKind.ContextOldHeader,
				ParseHeaderFileName( line[3..], location ),
				first,
				second
			);
		}
		if ( StartsHeader( line, "---" ) ) {
			return new PatchLineProbe(
				PatchProbeKind.DashHeader,
				ParseHeaderFileName( line[3..], location ),
				first,
				second
			);
		}
		if (
			TryParseNumericDirective(
				line,
				location,
				out var candidate,
				out var hasRightRange,
				out var directiveOperation
			)
		) {
			return new PatchLineProbe(
				PatchProbeKind.NumericDirective,
				null,
				first,
				second,
				hasRightRange: hasRightRange,
				directiveOperation: directiveOperation
			);
		}
		if ( candidate ) {
			if ( containsNul ) {
				throw new PatchInputException( "NUL byte in patch directive", location );
			}
			throw new PatchInputException( "malformed patch directive", location );
		}
		if ( StartsWithAscii( line, "< " ) ) {
			return new PatchLineProbe( PatchProbeKind.NormalOldData, null, first, second );
		}
		if ( StartsWithAscii( line, "> " ) ) {
			return new PatchLineProbe( PatchProbeKind.NormalNewData, null, first, second );
		}
		if ( line.SequenceEqual( "---"u8 ) ) {
			return new PatchLineProbe( PatchProbeKind.NormalSeparator, null, first, second );
		}
		if ( line.SequenceEqual( "."u8 ) ) {
			return new PatchLineProbe( PatchProbeKind.EdDot, null, first, second );
		}
		if ( line.SequenceEqual( "s/.//"u8 ) ) {
			return new PatchLineProbe( PatchProbeKind.EdSubstitute, null, first, second );
		}
		if ( line.SequenceEqual( "a"u8 ) ) {
			return new PatchLineProbe( PatchProbeKind.EdAppend, null, first, second );
		}
		return new PatchLineProbe( PatchProbeKind.Other, null, first, second );
	}

	/// <summary>Builds patch sections and adjacent text regions from source probes.</summary>
	/// <param name="records">The source records.</param>
	/// <param name="probes">The structural probes.</param>
	/// <param name="forcedFormat">The explicitly selected input format, when supplied.</param>
	/// <returns>The completed scan result.</returns>
	public static PatchScanResult Detect(
		IReadOnlyList<PatchRecord> records,
		IReadOnlyList<PatchLineProbe> probes,
		PatchFormat? forcedFormat = null
	) {
		ArgumentNullException.ThrowIfNull( records );
		ArgumentNullException.ThrowIfNull( probes );
		if ( records.Count != probes.Count ) {
			throw new ArgumentException( "record and probe counts differ", nameof( probes ) );
		}
		var sections = FindSections( probes, forcedFormat );
		if ( 0 == sections.Count ) {
			return new PatchScanResult( records, Array.Empty<PatchSection>(), null, null );
		}
		var first = sections[0].FirstRecordIndex;
		var lastSection = sections[^1];
		var afterLast = checked( lastSection.FirstRecordIndex + lastSection.RecordCount );
		var leading = 0 < first ? new PatchTextRegion( 0, first ) : null;
		var trailing = afterLast < records.Count
			? new PatchTextRegion( afterLast, records.Count - afterLast )
			: null;
		return new PatchScanResult( records, sections, leading, trailing );
	}

	private static List<PatchSection> FindSections(
		IReadOnlyList<PatchLineProbe> probes,
		PatchFormat? forcedFormat
	) {
		var sections = new List<PatchSection>();
		var index = 0;
		while ( index < probes.Count ) {
			if (
				( null == forcedFormat || PatchFormat.Unified == forcedFormat )
				&& IsUnifiedHeaderPairStart( probes, index )
			) {
				var end = FindUnifiedSectionEnd( probes, index );
				sections.Add(
					new PatchSection(
						PatchFormat.Unified,
						index,
						Math.Max( 2, end - index ),
						probes[index].FileName,
						probes[index + 1].FileName
					)
				);
				index = Math.Max( index + 2, end );
				continue;
			}
			if (
				( null == forcedFormat || PatchFormat.Context == forcedFormat )
				&& IsContextHeaderPairStart( probes, index )
			) {
				var end = FindContextSectionEnd( probes, index );
				sections.Add(
					new PatchSection(
						PatchFormat.Context,
						index,
						Math.Max( 2, end - index ),
						probes[index].FileName,
						probes[index + 1].FileName
					)
				);
				index = Math.Max( index + 2, end );
				continue;
			}
			if (
				PatchProbeKind.NumericDirective == probes[index].Kind
				&& ( null == forcedFormat || forcedFormat is PatchFormat.Normal or PatchFormat.EdScript )
			) {
				var format = forcedFormat ?? ( probes[index].HasRightRange
					? PatchFormat.Normal
					: PatchFormat.EdScript );
				var end = PatchFormat.EdScript == format
					? FindEdSectionEnd( probes, index )
					: SkipNumericSection( probes, index + 1, format );
				sections.Add(
					new PatchSection( format, index, Math.Max( 1, end - index ), null, null )
				);
				index = Math.Max( index + 1, end );
				continue;
			}
			index++;
		}
		return sections;
	}

	private static int FindUnifiedSectionEnd(
		IReadOnlyList<PatchLineProbe> probes,
		int start
	) {
		var index = checked( start + 2 );
		while ( index < probes.Count && PatchProbeKind.UnifiedHunk == probes[index].Kind ) {
			var header = probes[index++];
			if ( header.OldLineCount < 0 || header.NewLineCount < 0 ) {
				return FindRecognizedSectionEnd( PatchFormat.Unified, probes, index );
			}
			var oldRemaining = header.OldLineCount;
			var newRemaining = header.NewLineCount;
			while ( index < probes.Count && ( 0 < oldRemaining || 0 < newRemaining ) ) {
				var probe = probes[index];
				if ( PatchProbeKind.NoNewlineMarker == probe.Kind ) {
					index++;
					continue;
				}
				switch ( probe.FirstByte ) {
					case (byte)' ':
						oldRemaining--;
						newRemaining--;
						break;
					case (byte)'-':
						oldRemaining--;
						break;
					case (byte)'+':
						newRemaining--;
						break;
					default:
						return FindRecognizedSectionEnd( PatchFormat.Unified, probes, index );
				}
				index++;
			}
			while ( index < probes.Count && PatchProbeKind.NoNewlineMarker == probes[index].Kind ) {
				index++;
			}
		}
		return index;
	}

	private static int FindContextSectionEnd(
		IReadOnlyList<PatchLineProbe> probes,
		int start
	) {
		var index = checked( start + 2 );
		while ( index < probes.Count && PatchProbeKind.ContextSeparator == probes[index].Kind ) {
			index++;
			if (
				index >= probes.Count
				|| PatchProbeKind.ContextRange != probes[index].Kind
				|| (byte)'*' != probes[index].FirstByte
				|| probes[index].RangeLineCount < 0
			) {
				return FindRecognizedSectionEnd( PatchFormat.Context, probes, index );
			}
			var oldCount = probes[index++].RangeLineCount;
			index = SkipContextDataLines( probes, index, oldCount );
			if (
				index >= probes.Count
				|| PatchProbeKind.ContextRange != probes[index].Kind
				|| (byte)'-' != probes[index].FirstByte
				|| probes[index].RangeLineCount < 0
			) {
				return FindRecognizedSectionEnd( PatchFormat.Context, probes, index );
			}
			var newCount = probes[index++].RangeLineCount;
			index = SkipContextDataLines( probes, index, newCount );
		}
		return index;
	}

	private static int SkipContextDataLines(
		IReadOnlyList<PatchLineProbe> probes,
		int index,
		long count
	) {
		while ( index < probes.Count && 0 < count ) {
			if ( PatchProbeKind.NoNewlineMarker == probes[index].Kind ) {
				index++;
				continue;
			}
			count--;
			index++;
		}
		while ( index < probes.Count && PatchProbeKind.NoNewlineMarker == probes[index].Kind ) {
			index++;
		}
		return index;
	}

	private static int FindRecognizedSectionEnd(
		PatchFormat format,
		IReadOnlyList<PatchLineProbe> probes,
		int index
	) {
		var sawBody = false;
		for ( ; index < probes.Count; index++ ) {
			if ( IsHeaderPairStart( probes, index ) ) {
				return index;
			}
			if ( IsSectionBodyLine( format, probes[index] ) ) {
				sawBody = true;
				continue;
			}
			if ( sawBody ) {
				return index;
			}
		}
		return probes.Count;
	}

	private static int FindEdSectionEnd(
		IReadOnlyList<PatchLineProbe> probes,
		int start
	) {
		var index = start;
		while (
			index < probes.Count
			&& PatchProbeKind.NumericDirective == probes[index].Kind
			&& !probes[index].HasRightRange
		) {
			var operation = probes[index].DirectiveOperation;
			index++;
			if ( (byte)'d' == operation ) {
				continue;
			}
			index = SkipEdTextBlock( probes, index );
			while ( index < probes.Count && PatchProbeKind.EdSubstitute == probes[index].Kind ) {
				index++;
				if ( index < probes.Count && PatchProbeKind.EdAppend == probes[index].Kind ) {
					index = SkipEdTextBlock( probes, index + 1 );
				}
			}
		}
		return index;
	}

	private static int SkipEdTextBlock(
		IReadOnlyList<PatchLineProbe> probes,
		int index
	) {
		while ( index < probes.Count ) {
			if ( PatchProbeKind.EdDot == probes[index].Kind ) {
				return index + 1;
			}
			index++;
		}
		return probes.Count;
	}

	private static int SkipNumericSection(
		IReadOnlyList<PatchLineProbe> probes,
		int index,
		PatchFormat format
	) {
		var sawBody = false;
		for ( ; index < probes.Count; index++ ) {
			if ( IsHeaderPairStart( probes, index ) ) {
				return index;
			}
			if ( IsSectionBodyLine( format, probes[index] ) ) {
				sawBody = true;
				continue;
			}
			if ( sawBody ) {
				return index;
			}
		}
		return probes.Count;
	}

	private static bool IsHeaderPairStart( IReadOnlyList<PatchLineProbe> probes, int index ) {
		return IsUnifiedHeaderPairStart( probes, index )
			|| IsContextHeaderPairStart( probes, index );
	}

	private static bool IsUnifiedHeaderPairStart(
		IReadOnlyList<PatchLineProbe> probes,
		int index
	) {
		return index + 1 < probes.Count
			&& PatchProbeKind.DashHeader == probes[index].Kind
			&& PatchProbeKind.UnifiedNewHeader == probes[index + 1].Kind;
	}

	private static bool IsContextHeaderPairStart(
		IReadOnlyList<PatchLineProbe> probes,
		int index
	) {
		return index + 1 < probes.Count
			&& PatchProbeKind.ContextOldHeader == probes[index].Kind
			&& PatchProbeKind.DashHeader == probes[index + 1].Kind;
	}

	private static bool IsSectionBodyLine( PatchFormat format, PatchLineProbe probe ) {
		return format switch {
			PatchFormat.Unified => probe.Kind is
				PatchProbeKind.UnifiedHunk or
				PatchProbeKind.NoNewlineMarker
				|| probe.FirstByte is (byte)' ' or (byte)'+' or (byte)'-',
			PatchFormat.Context => probe.Kind is
				PatchProbeKind.ContextSeparator or
				PatchProbeKind.ContextRange or
				PatchProbeKind.NoNewlineMarker
				|| probe.FirstByte is (byte)' ' or (byte)'+' or (byte)'-' or (byte)'!',
			PatchFormat.Normal => probe.Kind is
				PatchProbeKind.NumericDirective or
				PatchProbeKind.NormalOldData or
				PatchProbeKind.NormalNewData or
				PatchProbeKind.NormalSeparator or
				PatchProbeKind.NoNewlineMarker,
			PatchFormat.EdScript => probe.Kind is
				PatchProbeKind.NumericDirective or
				PatchProbeKind.EdDot or
				PatchProbeKind.EdSubstitute or
				PatchProbeKind.EdAppend or
				PatchProbeKind.NoNewlineMarker or
				PatchProbeKind.Other or
				PatchProbeKind.Empty,
			_ => false
		};
	}

	private static bool LooksHeaderDirectiveLike( ReadOnlySpan<byte> line ) {
		var index = 0;
		while ( index < line.Length && IsHorizontalSpace( line[index] ) ) {
			index++;
		}
		if ( index >= line.Length ) {
			return false;
		}
		var candidate = line[index..];
		return LooksMarkerDirectiveLike( candidate, "---" )
			|| LooksMarkerDirectiveLike( candidate, "+++" )
			|| LooksMarkerDirectiveLike( candidate, "***" )
			|| LooksMarkerDirectiveLike( candidate, "@@" );
	}

	private static bool LooksMarkerDirectiveLike(
		ReadOnlySpan<byte> line,
		string marker
	) {
		if ( !StartsWithAscii( line, marker ) || line.Length <= marker.Length ) {
			return false;
		}
		return IsHorizontalSpace( line[marker.Length] ) || 0 == line[marker.Length];
	}

	private static bool LooksNumericDirectiveLikeWithNul( ReadOnlySpan<byte> line ) {
		var index = 0;
		SkipSpaceAndNul( line, ref index );
		if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
			return false;
		}
		while ( index < line.Length && IsAsciiDigit( line[index] ) ) {
			index++;
		}
		SkipSpaceAndNul( line, ref index );
		if ( index < line.Length && (byte)',' == line[index] ) {
			index++;
			SkipSpaceAndNul( line, ref index );
			if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
				return false;
			}
			while ( index < line.Length && IsAsciiDigit( line[index] ) ) {
				index++;
			}
			SkipSpaceAndNul( line, ref index );
		}
		return index < line.Length
			&& ( line[index] == (byte)'a' || line[index] == (byte)'c' || line[index] == (byte)'d' );
	}

	private static bool TryParseNumericDirective(
		ReadOnlySpan<byte> line,
		PatchSourceLocation location,
		out bool candidate,
		out bool hasRightRange,
		out byte directiveOperation
	) {
		candidate = false;
		hasRightRange = false;
		directiveOperation = 0;
		var index = 0;
		SkipSpace( line, ref index );
		if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
			return false;
		}
		var firstNumberOverflowed = ScanNumber( line, ref index );
		SkipSpace( line, ref index );
		if ( index < line.Length && (byte)',' == line[index] ) {
			candidate = true;
			if ( firstNumberOverflowed ) {
				throw new PatchInputException( "line number is too large", location );
			}
			index++;
			SkipSpace( line, ref index );
			ParseNumber( line, ref index, location );
			SkipSpace( line, ref index );
		}
		if ( index >= line.Length || ( line[index] != (byte)'a' && line[index] != (byte)'c' && line[index] != (byte)'d' ) ) {
			return false;
		}
		directiveOperation = line[index];
		candidate = true;
		if ( firstNumberOverflowed ) {
			throw new PatchInputException( "line number is too large", location );
		}
		index++;
		SkipSpace( line, ref index );
		if ( index == line.Length ) {
			return true;
		}
		hasRightRange = true;
		ParseNumber( line, ref index, location );
		SkipSpace( line, ref index );
		if ( index < line.Length && (byte)',' == line[index] ) {
			index++;
			SkipSpace( line, ref index );
			ParseNumber( line, ref index, location );
			SkipSpace( line, ref index );
		}
		return index == line.Length;
	}

	private static bool ScanNumber( ReadOnlySpan<byte> line, ref int index ) {
		var overflowed = false;
		long value = 0;
		while ( index < line.Length && IsAsciiDigit( line[index] ) ) {
			if ( !overflowed ) {
				try {
					value = checked( value * 10 + line[index] - (byte)'0' );
				} catch ( OverflowException ) {
					overflowed = true;
				}
			}
			index++;
		}
		return overflowed;
	}

	private static long ParseNumber(
		ReadOnlySpan<byte> line,
		ref int index,
		PatchSourceLocation location
	) {
		if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
			throw new PatchInputException( "missing line number in patch directive", location );
		}
		long value = 0;
		try {
			while ( index < line.Length && IsAsciiDigit( line[index] ) ) {
				value = checked( value * 10 + line[index] - (byte)'0' );
				index++;
			}
		} catch ( OverflowException ) {
			throw new PatchInputException( "line number is too large", location );
		}
		return value;
	}

	private static string ParseHeaderFileName(
		ReadOnlySpan<byte> remainder,
		PatchSourceLocation location
	) {
		var index = 0;
		SkipSpace( remainder, ref index );
		if ( index >= remainder.Length ) {
			throw new PatchInputException( "missing filename in patch header", location );
		}
		ReadOnlySpan<byte> name;
		if ( (byte)'"' == remainder[index] ) {
			var start = index;
			index++;
			var escaped = false;
			var closed = false;
			for ( ; index < remainder.Length; index++ ) {
				var value = remainder[index];
				if ( escaped ) {
					if ( value is (byte)'n' or (byte)'r' ) {
						throw new PatchInputException( "patch filename contains a newline", location );
					}
					if ( value is >= (byte)'0' and <= (byte)'7' ) {
						var octal = value - (byte)'0';
						var digits = 1;
						while ( digits < 3 && index + 1 < remainder.Length && remainder[index + 1] is >= (byte)'0' and <= (byte)'7' ) {
							index++;
							octal = checked( octal * 8 + remainder[index] - (byte)'0' );
							digits++;
						}
						if ( octal is 10 or 13 ) {
							throw new PatchInputException( "patch filename contains a newline", location );
						}
					}
					escaped = false;
					continue;
				}
				if ( (byte)'\\' == value ) {
					escaped = true;
					continue;
				}
				if ( (byte)'"' == value ) {
					closed = true;
					index++;
					break;
				}
			}
			if ( !closed ) {
				throw new PatchInputException( "unterminated quoted filename in patch header", location );
			}
			name = remainder[start..index];
		} else {
			var start = index;
			while ( index < remainder.Length && (byte)'\t' != remainder[index] ) {
				index++;
			}
			name = TrimEndSpace( remainder[start..index] );
		}
		if ( 0 == name.Length ) {
			throw new PatchInputException( "missing filename in patch header", location );
		}
		return Encoding.UTF8.GetString( name );
	}

	private static ReadOnlySpan<byte> TrimEndSpace( ReadOnlySpan<byte> value ) {
		var length = value.Length;
		while ( 0 < length && IsHorizontalSpace( value[length - 1] ) ) {
			length--;
		}
		return value[..length];
	}

	private static bool StartsHeader( ReadOnlySpan<byte> line, string marker ) {
		if ( !StartsWithAscii( line, marker ) || line.Length <= marker.Length ) {
			return false;
		}
		return IsHorizontalSpace( line[marker.Length] );
	}

	private static bool IsContextSeparator( ReadOnlySpan<byte> line ) {
		if ( line.Length < 8 ) {
			return false;
		}
		foreach ( var value in line ) {
			if ( (byte)'*' != value ) {
				return false;
			}
		}
		return true;
	}

	private static void TryParseUnifiedHunkCounts(
		ReadOnlySpan<byte> line,
		PatchSourceLocation location,
		out long oldCount,
		out long newCount
	) {
		oldCount = -1;
		newCount = -1;
		var index = 2;
		SkipSpace( line, ref index );
		if ( index >= line.Length || (byte)'-' != line[index++] ) {
			return;
		}
		if ( !TryParseUnifiedRangeCount( line, ref index, location, out oldCount ) ) {
			oldCount = -1;
			return;
		}
		SkipSpace( line, ref index );
		if ( index >= line.Length || (byte)'+' != line[index++] ) {
			oldCount = -1;
			return;
		}
		if ( !TryParseUnifiedRangeCount( line, ref index, location, out newCount ) ) {
			oldCount = -1;
			newCount = -1;
			return;
		}
		SkipSpace( line, ref index );
		if ( !StartsWithAscii( line[index..], "@@" ) ) {
			oldCount = -1;
			newCount = -1;
		}
	}

	private static bool TryParseUnifiedRangeCount(
		ReadOnlySpan<byte> line,
		ref int index,
		PatchSourceLocation location,
		out long count
	) {
		count = -1;
		SkipSpace( line, ref index );
		if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
			return false;
		}
		if ( ScanNumber( line, ref index ) ) {
			throw new PatchInputException( "line number is too large", location );
		}
		SkipSpace( line, ref index );
		count = 1;
		if ( index < line.Length && (byte)',' == line[index] ) {
			index++;
			SkipSpace( line, ref index );
			if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
				return false;
			}
			count = ParseNumber( line, ref index, location );
		}
		return true;
	}

	private static bool TryParseContextRange(
		ReadOnlySpan<byte> line,
		PatchSourceLocation location,
		out long count
	) {
		count = -1;
		var isOldRange = StartsWithAscii( line, "***" );
		var isNewRange = StartsWithAscii( line, "---" );
		if ( !isOldRange && !isNewRange ) {
			return false;
		}
		var index = 3;
		if ( index >= line.Length || !IsHorizontalSpace( line[index] ) ) {
			return false;
		}
		SkipSpace( line, ref index );
		if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
			return false;
		}
		var startIndex = index;
		var overflowed = ScanNumber( line, ref index );
		var start = ParseNumberAt( line[startIndex..index], location );
		SkipSpace( line, ref index );
		var end = start;
		var hasComma = false;
		if ( index < line.Length && (byte)',' == line[index] ) {
			hasComma = true;
			index++;
			SkipSpace( line, ref index );
			if ( index >= line.Length || !IsAsciiDigit( line[index] ) ) {
				return false;
			}
			var endIndex = index;
			overflowed |= ScanNumber( line, ref index );
			end = ParseNumberAt( line[endIndex..index], location );
			SkipSpace( line, ref index );
		}
		if ( !StartsWithAscii( line[index..], isOldRange ? "****" : "----" ) ) {
			return false;
		}
		if ( overflowed ) {
			throw new PatchInputException( "line number is too large", location );
		}
		if ( 0 == start ) {
			if ( hasComma && 0 != end ) {
				return false;
			}
			count = 0;
			return true;
		}
		if ( end < start ) {
			return false;
		}
		try {
			count = checked( end - start + 1 );
		} catch ( OverflowException ) {
			throw new PatchInputException( "line range is too large", location );
		}
		return true;
	}

	private static long ParseNumberAt(
		ReadOnlySpan<byte> value,
		PatchSourceLocation location
	) {
		var index = 0;
		return ParseNumber( value, ref index, location );
	}

	private static bool IsNoNewlineMarker( ReadOnlySpan<byte> line ) {
		return StartsWithAscii( line, "\\ No newline at end of file" );
	}

	private static bool StartsWithAscii( ReadOnlySpan<byte> line, string value ) {
		if ( line.Length < value.Length ) {
			return false;
		}
		for ( var index = 0; index < value.Length; index++ ) {
			if ( line[index] != (byte)value[index] ) {
				return false;
			}
		}
		return true;
	}

	private static void SkipSpace( ReadOnlySpan<byte> line, ref int index ) {
		while ( index < line.Length && IsHorizontalSpace( line[index] ) ) {
			index++;
		}
	}

	private static void SkipSpaceAndNul( ReadOnlySpan<byte> line, ref int index ) {
		while (
			index < line.Length
			&& ( IsHorizontalSpace( line[index] ) || 0 == line[index] )
		) {
			index++;
		}
	}

	private static bool IsHorizontalSpace( byte value ) {
		return value is (byte)' ' or (byte)'\t';
	}

	private static bool IsAsciiDigit( byte value ) {
		return value is >= (byte)'0' and <= (byte)'9';
	}
}
