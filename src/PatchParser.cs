namespace Icod.Patch;

using System.Text;

/// <summary>Parses all detected patch sections into immutable Wave A syntax models.</summary>
internal static class PatchDocumentParser {
	/// <summary>Parses a detected patch stream without accessing target files.</summary>
	/// <param name="source">The byte-preserved patch source.</param>
	/// <param name="scanResult">The detected source sections.</param>
	/// <param name="limits">The optional syntax-parser limits.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The immutable patch document.</returns>
	public static async Task<PatchDocument> ParseAsync(
		PatchSource source,
		PatchScanResult scanResult,
		PatchParseLimits? limits = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( scanResult );
		limits ??= PatchParseLimits.Default;
		limits.Validate();
		var budget = new PatchParseBudget( limits );
		var files = new List<PatchFilePatch>( scanResult.Sections.Count );
		foreach ( var section in scanResult.Sections ) {
			cancellationToken.ThrowIfCancellationRequested();
			budget.AddFile( GetSectionLocation( source.Records, section ) );
			var records = await PatchSectionMaterializer.LoadAsync(
				source,
				section,
				budget,
				cancellationToken
			).ConfigureAwait( false );
			var file = section.Format switch {
				PatchFormat.Unified or PatchFormat.Context =>
					UnifiedContextPatchParser.Parse( section, records, budget ),
				PatchFormat.Normal or PatchFormat.EdScript =>
					NormalEdPatchParser.Parse( section, records, budget ),
				_ => throw new PatchInputException(
					"unsupported patch format",
					records[0].Location
				)
			};
			files.Add( file );
		}
		return new PatchDocument(
			files,
			scanResult.LeadingText,
			GetInterstitialText( scanResult.Sections ),
			scanResult.TrailingText
		);
	}

	private static IReadOnlyList<PatchTextRegion> GetInterstitialText(
		IReadOnlyList<PatchSection> sections
	) {
		var regions = new List<PatchTextRegion>();
		for ( var index = 1; index < sections.Count; index++ ) {
			var previous = sections[index - 1];
			var start = checked( previous.FirstRecordIndex + previous.RecordCount );
			var count = sections[index].FirstRecordIndex - start;
			if ( 0 < count ) {
				regions.Add( new PatchTextRegion( start, count ) );
			}
		}
		return regions;
	}

	private static PatchSourceLocation GetSectionLocation(
		IReadOnlyList<PatchRecord> records,
		PatchSection section
	) {
		if ( section.FirstRecordIndex < 0 || records.Count <= section.FirstRecordIndex ) {
			throw new ArgumentOutOfRangeException( nameof( section ) );
		}
		return records[section.FirstRecordIndex].Location;
	}
}

/// <summary>Tracks bounded parser allocations and syntax-model cardinality.</summary>
internal sealed class PatchParseBudget {
	private readonly PatchParseLimits limits;
	private int files;
	private int hunks;
	private int dataLines;
	private long materializedBytes;

	/// <summary>Initializes a parser budget.</summary>
	/// <param name="limits">The enforced limits.</param>
	public PatchParseBudget( PatchParseLimits limits ) {
		ArgumentNullException.ThrowIfNull( limits );
		this.limits = limits;
	}

	/// <summary>Accounts for one parsed file section.</summary>
	/// <param name="location">The location used for limit diagnostics.</param>
	public void AddFile( PatchSourceLocation location ) {
		this.files = CheckedIncrement( this.files, "patch contains too many file sections", location );
		if ( this.limits.MaximumFiles < this.files ) {
			throw new PatchInputException( "patch contains too many file sections", location );
		}
	}

	/// <summary>Accounts for one parsed hunk.</summary>
	/// <param name="location">The location used for limit diagnostics.</param>
	public void AddHunk( PatchSourceLocation location ) {
		this.hunks = CheckedIncrement( this.hunks, "patch contains too many hunks", location );
		if ( this.limits.MaximumHunks < this.hunks ) {
			throw new PatchInputException( "patch contains too many hunks", location );
		}
	}

	/// <summary>Accounts for one materialized logical data line.</summary>
	/// <param name="contentBytes">The represented content-byte count.</param>
	/// <param name="location">The location used for limit diagnostics.</param>
	public void AddDataLine( int contentBytes, PatchSourceLocation location ) {
		this.dataLines = CheckedIncrement(
			this.dataLines,
			"patch contains too many data lines",
			location
		);
		if ( this.limits.MaximumDataLines < this.dataLines ) {
			throw new PatchInputException( "patch contains too many data lines", location );
		}
		this.AddBytes( contentBytes, location );
	}

	/// <summary>Accounts for bytes materialized into retained syntax models.</summary>
	/// <param name="count">The byte count.</param>
	/// <param name="location">The location used for limit diagnostics.</param>
	public void AddBytes( long count, PatchSourceLocation location ) {
		try {
			this.materializedBytes = checked( this.materializedBytes + count );
		} catch ( OverflowException ) {
			throw new PatchInputException( "patch syntax model is too large", location );
		}
		if ( this.limits.MaximumMaterializedBytes < this.materializedBytes ) {
			throw new PatchInputException(
				"patch syntax model exceeds the configured byte limit",
				location
			);
		}
	}

	private static int CheckedIncrement(
		int value,
		string message,
		PatchSourceLocation location
	) {
		try {
			return checked( value + 1 );
		} catch ( OverflowException ) {
			throw new PatchInputException( message, location );
		}
	}
}

/// <summary>Materializes one bounded detected section from the spill-backed source.</summary>
internal static class PatchSectionMaterializer {
	/// <summary>Loads all exact records belonging to a detected patch section.</summary>
	/// <param name="source">The spill-backed patch source.</param>
	/// <param name="section">The detected section.</param>
	/// <param name="budget">The parser budget.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The exact section records.</returns>
	public static async Task<IReadOnlyList<PatchRawRecord>> LoadAsync(
		PatchSource source,
		PatchSection section,
		PatchParseBudget budget,
		CancellationToken cancellationToken
	) {
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( section );
		ArgumentNullException.ThrowIfNull( budget );
		if ( section.FirstRecordIndex < 0 || section.RecordCount < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( section ) );
		}
		var end = checked( section.FirstRecordIndex + section.RecordCount );
		if ( source.Records.Count < end ) {
			throw new ArgumentOutOfRangeException( nameof( section ) );
		}
		var records = new List<PatchRawRecord>( section.RecordCount );
		for ( var index = section.FirstRecordIndex; index < end; index++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			var sourceRecord = source.Records[index];
			var content = await source.ReadRecordAsync(
				index,
				includeTerminator: false,
				cancellationToken
			).ConfigureAwait( false );
			budget.AddBytes( content.Length, sourceRecord.Location );
			records.Add(
				new PatchRawRecord(
					sourceRecord.Location,
					content,
					sourceRecord.Terminator
				)
			);
		}
		return records;
	}
}

/// <summary>Provides byte-oriented parsing primitives shared by all Patch formats.</summary>
internal static class PatchParsing {
	/// <summary>Gets whether a record is the GNU incomplete-line marker.</summary>
	/// <param name="record">The source record.</param>
	/// <returns><see langword="true"/> when the record is the marker.</returns>
	public static bool IsNoNewlineMarker( PatchRawRecord record ) {
		ArgumentNullException.ThrowIfNull( record );
		return StartsWithAscii( record.Content.Span, "\\ No newline at end of file" );
	}

	/// <summary>Gets whether a record contains exactly the supplied ASCII text.</summary>
	/// <param name="record">The source record.</param>
	/// <param name="text">The ASCII text.</param>
	/// <returns><see langword="true"/> when the content matches.</returns>
	public static bool EqualsAscii( PatchRawRecord record, string text ) {
		ArgumentNullException.ThrowIfNull( record );
		return record.Content.Span.SequenceEqual( Encoding.ASCII.GetBytes( text ) );
	}

	/// <summary>Gets whether bytes begin with supplied ASCII text.</summary>
	/// <param name="value">The bytes.</param>
	/// <param name="text">The ASCII prefix.</param>
	/// <returns><see langword="true"/> when the prefix matches.</returns>
	public static bool StartsWithAscii( ReadOnlySpan<byte> value, string text ) {
		if ( value.Length < text.Length ) {
			return false;
		}
		for ( var index = 0; index < text.Length; index++ ) {
			if ( value[index] != (byte)text[index] ) {
				return false;
			}
		}
		return true;
	}

	/// <summary>Skips spaces and horizontal tabs.</summary>
	/// <param name="value">The bytes.</param>
	/// <param name="index">The cursor to advance.</param>
	public static void SkipHorizontalSpace( ReadOnlySpan<byte> value, ref int index ) {
		while ( index < value.Length && IsHorizontalSpace( value[index] ) ) {
			index++;
		}
	}

	/// <summary>Parses one checked nonnegative decimal integer.</summary>
	/// <param name="value">The bytes.</param>
	/// <param name="index">The cursor to advance.</param>
	/// <param name="location">The source location.</param>
	/// <returns>The parsed value.</returns>
	public static long ParseDecimal(
		ReadOnlySpan<byte> value,
		ref int index,
		PatchSourceLocation location
	) {
		if ( index >= value.Length || !IsAsciiDigit( value[index] ) ) {
			throw new PatchInputException( "missing line number in patch directive", location );
		}
		long result = 0;
		try {
			while ( index < value.Length && IsAsciiDigit( value[index] ) ) {
				result = checked( result * 10 + value[index] - (byte)'0' );
				index++;
			}
		} catch ( OverflowException ) {
			throw new PatchInputException( "line number is too large", location );
		}
		return result;
	}

	/// <summary>Parses a comma-separated inclusive range.</summary>
	/// <param name="value">The bytes.</param>
	/// <param name="index">The cursor to advance.</param>
	/// <param name="location">The source location.</param>
	/// <param name="allowZero">Whether a zero address is permitted.</param>
	/// <returns>The normalized start and count.</returns>
	public static PatchRange ParseInclusiveRange(
		ReadOnlySpan<byte> value,
		ref int index,
		PatchSourceLocation location,
		bool allowZero
	) {
		var start = ParseDecimal( value, ref index, location );
		SkipHorizontalSpace( value, ref index );
		var end = start;
		if ( index < value.Length && (byte)',' == value[index] ) {
			index++;
			SkipHorizontalSpace( value, ref index );
			end = ParseDecimal( value, ref index, location );
		}
		if ( !allowZero && 0 == start ) {
			throw new PatchInputException( "line range starts at zero", location );
		}
		if ( end < start ) {
			throw new PatchInputException( "line range is reversed", location );
		}
		long count;
		try {
			count = checked( end - start + 1 );
		} catch ( OverflowException ) {
			throw new PatchInputException( "line range is too large", location );
		}
		return new PatchRange( start, count );
	}

	/// <summary>Parses one old- or new-file header and its optional timestamp text.</summary>
	/// <param name="record">The exact header record.</param>
	/// <param name="marker">The expected three-character marker.</param>
	/// <param name="fileName">The already validated filename token.</param>
	/// <returns>The parsed header.</returns>
	public static PatchFileHeader ParseFileHeader(
		PatchRawRecord record,
		string marker,
		string? fileName
	) {
		ArgumentNullException.ThrowIfNull( record );
		if ( string.IsNullOrEmpty( fileName ) ) {
			throw new PatchInputException( "missing filename in patch header", record.Location );
		}
		var content = record.Content.Span;
		if ( !StartsWithAscii( content, marker ) ) {
			throw new PatchInputException( "malformed patch file header", record.Location );
		}
		var index = marker.Length;
		if ( index >= content.Length || !IsHorizontalSpace( content[index] ) ) {
			throw new PatchInputException( "malformed patch file header", record.Location );
		}
		SkipHorizontalSpace( content, ref index );
		string? timestamp = null;
		if ( index < content.Length && (byte)'"' == content[index] ) {
			index++;
			var escaped = false;
			var closed = false;
			for ( ; index < content.Length; index++ ) {
				if ( escaped ) {
					escaped = false;
					continue;
				}
				if ( (byte)'\\' == content[index] ) {
					escaped = true;
					continue;
				}
				if ( (byte)'"' == content[index] ) {
					index++;
					closed = true;
					break;
				}
			}
			if ( !closed ) {
				throw new PatchInputException(
					"unterminated quoted filename in patch header",
					record.Location
				);
			}
			SkipHorizontalSpace( content, ref index );
			if ( index < content.Length ) {
				timestamp = DecodeTrimmedUtf8( content[index..] );
			}
		} else {
			var tab = content[index..].IndexOf( (byte)'\t' );
			if ( 0 <= tab ) {
				var metadataIndex = checked( index + tab + 1 );
				if ( metadataIndex < content.Length ) {
					timestamp = DecodeTrimmedUtf8( content[metadataIndex..] );
				}
			}
		}
		return new PatchFileHeader( fileName, timestamp, record );
	}

	/// <summary>Determines a file-level operation from parsed headers.</summary>
	/// <param name="oldHeader">The old-file header.</param>
	/// <param name="newHeader">The new-file header.</param>
	/// <param name="location">The source location.</param>
	/// <returns>The file-level operation.</returns>
	public static PatchFileChangeKind DetermineFileChangeKind(
		PatchFileHeader oldHeader,
		PatchFileHeader newHeader,
		PatchSourceLocation location
	) {
		ArgumentNullException.ThrowIfNull( oldHeader );
		ArgumentNullException.ThrowIfNull( newHeader );
		var oldNull = IsNullDeviceName( oldHeader.Name );
		var newNull = IsNullDeviceName( newHeader.Name );
		if ( oldNull && newNull ) {
			throw new PatchInputException( "both patch filenames name the null device", location );
		}
		if ( oldNull ) {
			return PatchFileChangeKind.Create;
		}
		if ( newNull ) {
			return PatchFileChangeKind.Delete;
		}
		return PatchFileChangeKind.Modify;
	}

	/// <summary>Determines a hunk operation from old and new line counts.</summary>
	/// <param name="oldCount">The old-side count.</param>
	/// <param name="newCount">The new-side count.</param>
	/// <returns>The semantic hunk operation.</returns>
	public static PatchOperationKind DetermineOperation( long oldCount, long newCount ) {
		if ( 0 == oldCount && 0 < newCount ) {
			return PatchOperationKind.Add;
		}
		if ( 0 < oldCount && 0 == newCount ) {
			return PatchOperationKind.Delete;
		}
		return PatchOperationKind.Change;
	}

	/// <summary>Creates a logical data line and accounts for its retained bytes.</summary>
	/// <param name="record">The exact source record.</param>
	/// <param name="prefixLength">The diff-prefix byte count.</param>
	/// <param name="isContext">Whether the line is unchanged context.</param>
	/// <param name="budget">The parser budget.</param>
	/// <returns>The logical data line.</returns>
	public static PatchDataLine CreateDataLine(
		PatchRawRecord record,
		int prefixLength,
		bool isContext,
		PatchParseBudget budget
	) {
		ArgumentNullException.ThrowIfNull( record );
		ArgumentNullException.ThrowIfNull( budget );
		if ( prefixLength < 0 || record.Content.Length < prefixLength ) {
			throw new ArgumentOutOfRangeException( nameof( prefixLength ) );
		}
		var content = record.Content.Span[prefixLength..];
		budget.AddDataLine( content.Length, record.Location );
		return new PatchDataLine(
			content,
			record.Terminator,
			isContext,
			record.Location
		);
	}

	/// <summary>Decodes trimmed UTF-8 metadata without changing retained patch bytes.</summary>
	/// <param name="value">The metadata bytes.</param>
	/// <returns>The decoded text, or <see langword="null"/> when empty.</returns>
	public static string? DecodeTrimmedUtf8( ReadOnlySpan<byte> value ) {
		var start = 0;
		var end = value.Length;
		while ( start < end && IsHorizontalSpace( value[start] ) ) {
			start++;
		}
		while ( start < end && IsHorizontalSpace( value[end - 1] ) ) {
			end--;
		}
		return start == end ? null : Encoding.UTF8.GetString( value[start..end] );
	}

	/// <summary>Gets whether a byte is a space or horizontal tab.</summary>
	/// <param name="value">The byte.</param>
	/// <returns><see langword="true"/> for horizontal whitespace.</returns>
	public static bool IsHorizontalSpace( byte value ) {
		return value is (byte)' ' or (byte)'\t';
	}

	/// <summary>Gets whether a byte is an ASCII decimal digit.</summary>
	/// <param name="value">The byte.</param>
	/// <returns><see langword="true"/> for a decimal digit.</returns>
	public static bool IsAsciiDigit( byte value ) {
		return value is >= (byte)'0' and <= (byte)'9';
	}

	private static bool IsNullDeviceName( string name ) {
		var value = name;
		if ( 1 < value.Length && '"' == value[0] && '"' == value[^1] ) {
			value = value[1..^1];
		}
		return string.Equals( value, "/dev/null", StringComparison.Ordinal );
	}
}
