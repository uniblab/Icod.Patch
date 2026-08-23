namespace Icod.Patch;

using System.Collections.ObjectModel;

/// <summary>Identifies a patch syntax recognized by the Wave A detector.</summary>
internal enum PatchFormat {
	/// <summary>Unified diff syntax.</summary>
	Unified,
	/// <summary>Context diff syntax.</summary>
	Context,
	/// <summary>Normal diff command syntax.</summary>
	Normal,
	/// <summary>Patch-compatible ed script syntax.</summary>
	EdScript
}

/// <summary>Identifies the record terminator preserved from a patch stream.</summary>
internal enum PatchLineTerminator {
	/// <summary>The final record has no terminator.</summary>
	None,
	/// <summary>A line-feed terminator.</summary>
	LineFeed,
	/// <summary>A carriage-return terminator.</summary>
	CarriageReturn,
	/// <summary>A carriage-return and line-feed terminator.</summary>
	CarriageReturnLineFeed
}

/// <summary>Describes a byte and line location in the original patch stream.</summary>
internal readonly struct PatchSourceLocation {
	/// <summary>Initializes a source location.</summary>
	/// <param name="byteOffset">The zero-based byte offset.</param>
	/// <param name="lineNumber">The one-based line number.</param>
	public PatchSourceLocation( long byteOffset, long lineNumber ) {
		this.ByteOffset = byteOffset;
		this.LineNumber = lineNumber;
	}

	/// <summary>Gets the zero-based byte offset.</summary>
	public long ByteOffset { get; }

	/// <summary>Gets the one-based line number.</summary>
	public long LineNumber { get; }
}

/// <summary>Describes one byte-preserved record in a patch stream.</summary>
internal readonly struct PatchRecord {
	/// <summary>Initializes a patch record.</summary>
	/// <param name="location">The source location.</param>
	/// <param name="contentLength">The number of bytes before the terminator.</param>
	/// <param name="terminator">The preserved terminator.</param>
	public PatchRecord(
		PatchSourceLocation location,
		long contentLength,
		PatchLineTerminator terminator
	) {
		this.Location = location;
		this.ContentLength = contentLength;
		this.Terminator = terminator;
	}

	/// <summary>Gets the source location.</summary>
	public PatchSourceLocation Location { get; }

	/// <summary>Gets the number of content bytes before the terminator.</summary>
	public long ContentLength { get; }

	/// <summary>Gets the preserved record terminator.</summary>
	public PatchLineTerminator Terminator { get; }

	/// <summary>Gets the number of bytes in the record terminator.</summary>
	public int TerminatorLength => this.Terminator switch {
		PatchLineTerminator.None => 0,
		PatchLineTerminator.CarriageReturnLineFeed => 2,
		_ => 1
	};

	/// <summary>Gets the complete byte length of the record.</summary>
	public long TotalLength => checked( this.ContentLength + this.TerminatorLength );
}

/// <summary>Describes non-patch text adjacent to recognized patch sections.</summary>
internal sealed class PatchTextRegion {
	/// <summary>Initializes a text region.</summary>
	/// <param name="firstRecordIndex">The zero-based first record index.</param>
	/// <param name="recordCount">The number of records in the region.</param>
	public PatchTextRegion( int firstRecordIndex, int recordCount ) {
		this.FirstRecordIndex = firstRecordIndex;
		this.RecordCount = recordCount;
	}

	/// <summary>Gets the zero-based first record index.</summary>
	public int FirstRecordIndex { get; }

	/// <summary>Gets the number of records in the region.</summary>
	public int RecordCount { get; }
}

/// <summary>Describes a candidate patch section found in a source stream.</summary>
internal sealed class PatchSection {
	/// <summary>Initializes a detected patch section.</summary>
	/// <param name="format">The detected format.</param>
	/// <param name="firstRecordIndex">The zero-based first record index.</param>
	/// <param name="recordCount">The number of records in the section.</param>
	/// <param name="oldFileName">The old-file name, when present.</param>
	/// <param name="newFileName">The new-file name, when present.</param>
	public PatchSection(
		PatchFormat format,
		int firstRecordIndex,
		int recordCount,
		string? oldFileName,
		string? newFileName
	) {
		this.Format = format;
		this.FirstRecordIndex = firstRecordIndex;
		this.RecordCount = recordCount;
		this.OldFileName = oldFileName;
		this.NewFileName = newFileName;
	}

	/// <summary>Gets the detected patch format.</summary>
	public PatchFormat Format { get; }

	/// <summary>Gets the zero-based first record index.</summary>
	public int FirstRecordIndex { get; }

	/// <summary>Gets the number of records in the section.</summary>
	public int RecordCount { get; }

	/// <summary>Gets the old-file name, when present.</summary>
	public string? OldFileName { get; }

	/// <summary>Gets the new-file name, when present.</summary>
	public string? NewFileName { get; }
}

/// <summary>Defines bounded-resource limits for patch-stream scanning.</summary>
internal sealed class PatchScanLimits {
	/// <summary>Gets the default patch-stream limits.</summary>
	public static PatchScanLimits Default { get; } = new();

	/// <summary>Gets or initializes the maximum patch-stream byte count.</summary>
	public long MaximumBytes { get; init; } = 64L * 1024L * 1024L;

	/// <summary>Gets or initializes the maximum record count.</summary>
	public int MaximumRecords { get; init; } = 1_000_000;

	/// <summary>Gets or initializes the maximum content bytes in one record.</summary>
	public int MaximumRecordBytes { get; init; } = 1024 * 1024;

	/// <summary>Validates the configured limits.</summary>
	public void Validate() {
		if ( this.MaximumBytes < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumBytes ) );
		}
		if ( this.MaximumRecords < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumRecords ) );
		}
		if ( this.MaximumRecordBytes < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumRecordBytes ) );
		}
	}
}

/// <summary>Contains the source map and candidate sections found by the Wave A scanner.</summary>
internal sealed class PatchScanResult {
	/// <summary>Initializes a scan result.</summary>
	/// <param name="records">The source records.</param>
	/// <param name="sections">The detected patch sections.</param>
	/// <param name="leadingText">The leading non-patch text, when present.</param>
	/// <param name="trailingText">The trailing non-patch text, when present.</param>
	public PatchScanResult(
		IReadOnlyList<PatchRecord> records,
		IReadOnlyList<PatchSection> sections,
		PatchTextRegion? leadingText,
		PatchTextRegion? trailingText
	) {
		ArgumentNullException.ThrowIfNull( records );
		ArgumentNullException.ThrowIfNull( sections );
		this.Records = records;
		this.Sections = new ReadOnlyCollection<PatchSection>( sections.ToArray() );
		this.LeadingText = leadingText;
		this.TrailingText = trailingText;
	}

	/// <summary>Gets the byte-preserved source records.</summary>
	public IReadOnlyList<PatchRecord> Records { get; }

	/// <summary>Gets the detected patch sections.</summary>
	public IReadOnlyList<PatchSection> Sections { get; }

	/// <summary>Gets the leading non-patch text, when present.</summary>
	public PatchTextRegion? LeadingText { get; }

	/// <summary>Gets the trailing non-patch text, when present.</summary>
	public PatchTextRegion? TrailingText { get; }

	/// <summary>Gets whether at least one patch section was detected.</summary>
	public bool HasPatch => 0 < this.Sections.Count;
}

/// <summary>Represents malformed or resource-exhausting patch input.</summary>
internal sealed class PatchInputException : Exception {
	/// <summary>Initializes a patch-input exception.</summary>
	/// <param name="message">The diagnostic message.</param>
	/// <param name="location">The source location associated with the error.</param>
	public PatchInputException( string message, PatchSourceLocation location )
		: base( message ) {
		this.Location = location;
	}

	/// <summary>Gets the source location associated with the error.</summary>
	public PatchSourceLocation Location { get; }
}

/// <summary>Defines GNU patch process-status categories.</summary>
internal enum PatchExitStatus {
	/// <summary>All requested work succeeded.</summary>
	Success = 0,
	/// <summary>At least one hunk or file failed while processing continued.</summary>
	PartialFailure = 1,
	/// <summary>Invocation, input, or operational trouble prevented normal processing.</summary>
	Trouble = 2
}

/// <summary>Accumulates the most severe patch status without losing partial-failure state.</summary>
internal sealed class PatchExitStatusAccumulator {
	/// <summary>Gets the accumulated status.</summary>
	public PatchExitStatus Status { get; private set; } = PatchExitStatus.Success;

	/// <summary>Adds a status to the accumulator.</summary>
	/// <param name="status">The status to add.</param>
	public void Add( PatchExitStatus status ) {
		if ( (int)this.Status < (int)status ) {
			this.Status = status;
		}
	}
}
