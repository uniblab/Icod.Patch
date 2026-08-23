namespace Icod.Patch;

using System.Collections.ObjectModel;

/// <summary>Identifies the semantic operation represented by one parsed patch hunk.</summary>
internal enum PatchOperationKind {
	/// <summary>Lines are inserted without consuming original lines.</summary>
	Add,
	/// <summary>Original lines are removed without replacement lines.</summary>
	Delete,
	/// <summary>Original lines are replaced by new lines.</summary>
	Change
}

/// <summary>Identifies whether a file patch modifies, creates, or deletes a file.</summary>
internal enum PatchFileChangeKind {
	/// <summary>An existing file is modified.</summary>
	Modify,
	/// <summary>A new file is created.</summary>
	Create,
	/// <summary>An existing file is deleted.</summary>
	Delete,
	/// <summary>The headerless format does not determine the file-level operation.</summary>
	Unspecified
}

/// <summary>Describes a zero- or one-based patch-format line range.</summary>
internal readonly struct PatchRange {
	/// <summary>Initializes a patch range.</summary>
	/// <param name="start">The format-defined starting line or insertion address.</param>
	/// <param name="count">The number of affected lines.</param>
	public PatchRange( long start, long count ) {
		if ( start < 0 ) {
			throw new ArgumentOutOfRangeException( nameof( start ) );
		}
		if ( count < 0 ) {
			throw new ArgumentOutOfRangeException( nameof( count ) );
		}
		this.Start = start;
		this.Count = count;
	}

	/// <summary>Gets the format-defined starting line or insertion address.</summary>
	public long Start { get; }

	/// <summary>Gets the number of affected lines.</summary>
	public long Count { get; }
}

/// <summary>Contains one exact source record retained for reject serialization.</summary>
internal sealed class PatchRawRecord {
	private readonly byte[] content;

	/// <summary>Initializes an exact patch-source record.</summary>
	/// <param name="location">The original source location.</param>
	/// <param name="content">The record bytes excluding the terminator.</param>
	/// <param name="terminator">The original record terminator.</param>
	public PatchRawRecord(
		PatchSourceLocation location,
		ReadOnlySpan<byte> content,
		PatchLineTerminator terminator
	) {
		this.Location = location;
		this.content = content.ToArray();
		this.Terminator = terminator;
	}

	/// <summary>Gets the original source location.</summary>
	public PatchSourceLocation Location { get; }

	/// <summary>Gets the exact record bytes excluding the terminator.</summary>
	public ReadOnlyMemory<byte> Content => this.content;

	/// <summary>Gets the original record terminator.</summary>
	public PatchLineTerminator Terminator { get; }
}

/// <summary>Contains one logical old- or new-file line from a parsed hunk.</summary>
internal sealed class PatchDataLine {
	private readonly byte[] content;

	/// <summary>Initializes a logical hunk line.</summary>
	/// <param name="content">The line bytes without a diff prefix.</param>
	/// <param name="terminator">The represented file-line terminator.</param>
	/// <param name="isContext">Whether the line is unchanged context.</param>
	/// <param name="sourceLocation">The patch-source location.</param>
	public PatchDataLine(
		ReadOnlySpan<byte> content,
		PatchLineTerminator terminator,
		bool isContext,
		PatchSourceLocation sourceLocation
	) {
		this.content = content.ToArray();
		this.Terminator = terminator;
		this.IsContext = isContext;
		this.SourceLocation = sourceLocation;
	}

	/// <summary>Gets the represented file-line bytes.</summary>
	public ReadOnlyMemory<byte> Content => this.content;

	/// <summary>Gets the represented file-line terminator.</summary>
	public PatchLineTerminator Terminator { get; }

	/// <summary>Gets whether this line is unchanged context.</summary>
	public bool IsContext { get; }

	/// <summary>Gets the patch-source location.</summary>
	public PatchSourceLocation SourceLocation { get; }

	/// <summary>Creates a copy with a different represented terminator.</summary>
	/// <param name="terminator">The replacement terminator.</param>
	/// <returns>The copied logical line.</returns>
	public PatchDataLine WithTerminator( PatchLineTerminator terminator ) {
		return new PatchDataLine(
			this.content,
			terminator,
			this.IsContext,
			this.SourceLocation
		);
	}

	/// <summary>Creates a copy with different represented content.</summary>
	/// <param name="content">The replacement content.</param>
	/// <returns>The copied logical line.</returns>
	public PatchDataLine WithContent( ReadOnlySpan<byte> content ) {
		return new PatchDataLine(
			content,
			this.Terminator,
			this.IsContext,
			this.SourceLocation
		);
	}
}

/// <summary>Contains one parsed old- or new-file header.</summary>
internal sealed class PatchFileHeader {
	/// <summary>Initializes a parsed file header.</summary>
	/// <param name="name">The header filename token.</param>
	/// <param name="timestampText">The optional timestamp or trailing header metadata.</param>
	/// <param name="sourceRecord">The exact source record.</param>
	public PatchFileHeader(
		string name,
		string? timestampText,
		PatchRawRecord sourceRecord
	) {
		ArgumentException.ThrowIfNullOrEmpty( name );
		ArgumentNullException.ThrowIfNull( sourceRecord );
		this.Name = name;
		this.TimestampText = timestampText;
		this.SourceRecord = sourceRecord;
	}

	/// <summary>Gets the header filename token.</summary>
	public string Name { get; }

	/// <summary>Gets the optional timestamp or trailing header metadata.</summary>
	public string? TimestampText { get; }

	/// <summary>Gets the exact source record.</summary>
	public PatchRawRecord SourceRecord { get; }
}

/// <summary>Contains one immutable parsed patch hunk or edit operation.</summary>
internal sealed class PatchHunk {
	/// <summary>Initializes a parsed hunk.</summary>
	/// <param name="operation">The semantic operation.</param>
	/// <param name="oldRange">The old-file range or insertion address.</param>
	/// <param name="newRange">The new-file range when supplied by the format.</param>
	/// <param name="oldLines">The exact old-side logical lines.</param>
	/// <param name="newLines">The exact new-side logical lines.</param>
	/// <param name="sectionText">The optional unified-hunk section text.</param>
	/// <param name="sourceLocation">The first source location of the hunk.</param>
	/// <param name="rawRecords">The exact source records needed to reproduce a reject.</param>
	public PatchHunk(
		PatchOperationKind operation,
		PatchRange oldRange,
		PatchRange? newRange,
		IReadOnlyList<PatchDataLine> oldLines,
		IReadOnlyList<PatchDataLine> newLines,
		string? sectionText,
		PatchSourceLocation sourceLocation,
		IReadOnlyList<PatchRawRecord> rawRecords
	) {
		ArgumentNullException.ThrowIfNull( oldLines );
		ArgumentNullException.ThrowIfNull( newLines );
		ArgumentNullException.ThrowIfNull( rawRecords );
		this.Operation = operation;
		this.OldRange = oldRange;
		this.NewRange = newRange;
		this.OldLines = new ReadOnlyCollection<PatchDataLine>( oldLines.ToArray() );
		this.NewLines = new ReadOnlyCollection<PatchDataLine>( newLines.ToArray() );
		this.SectionText = sectionText;
		this.SourceLocation = sourceLocation;
		this.RawRecords = new ReadOnlyCollection<PatchRawRecord>( rawRecords.ToArray() );
	}

	/// <summary>Gets the semantic operation.</summary>
	public PatchOperationKind Operation { get; }

	/// <summary>Gets the old-file range or insertion address.</summary>
	public PatchRange OldRange { get; }

	/// <summary>Gets the new-file range when supplied by the format.</summary>
	public PatchRange? NewRange { get; }

	/// <summary>Gets the exact old-side logical lines.</summary>
	public IReadOnlyList<PatchDataLine> OldLines { get; }

	/// <summary>Gets the exact new-side logical lines.</summary>
	public IReadOnlyList<PatchDataLine> NewLines { get; }

	/// <summary>Gets the optional unified-hunk section text.</summary>
	public string? SectionText { get; }

	/// <summary>Gets the first source location of the hunk.</summary>
	public PatchSourceLocation SourceLocation { get; }

	/// <summary>Gets the exact source records needed to reproduce a reject.</summary>
	public IReadOnlyList<PatchRawRecord> RawRecords { get; }
}

/// <summary>Contains all parsed hunks for one detected patch section.</summary>
internal sealed class PatchFilePatch {
	/// <summary>Initializes one parsed file patch.</summary>
	/// <param name="format">The source patch format.</param>
	/// <param name="changeKind">The represented file-level operation.</param>
	/// <param name="oldHeader">The old-file header when supplied by the format.</param>
	/// <param name="newHeader">The new-file header when supplied by the format.</param>
	/// <param name="hunks">The parsed hunks or edit operations.</param>
	/// <param name="sourceLocation">The first source location of the section.</param>
	public PatchFilePatch(
		PatchFormat format,
		PatchFileChangeKind changeKind,
		PatchFileHeader? oldHeader,
		PatchFileHeader? newHeader,
		IReadOnlyList<PatchHunk> hunks,
		PatchSourceLocation sourceLocation
	) {
		ArgumentNullException.ThrowIfNull( hunks );
		this.Format = format;
		this.ChangeKind = changeKind;
		this.OldHeader = oldHeader;
		this.NewHeader = newHeader;
		this.Hunks = new ReadOnlyCollection<PatchHunk>( hunks.ToArray() );
		this.SourceLocation = sourceLocation;
	}

	/// <summary>Gets the source patch format.</summary>
	public PatchFormat Format { get; }

	/// <summary>Gets the represented file-level operation.</summary>
	public PatchFileChangeKind ChangeKind { get; }

	/// <summary>Gets the old-file header when supplied by the format.</summary>
	public PatchFileHeader? OldHeader { get; }

	/// <summary>Gets the new-file header when supplied by the format.</summary>
	public PatchFileHeader? NewHeader { get; }

	/// <summary>Gets the parsed hunks or edit operations.</summary>
	public IReadOnlyList<PatchHunk> Hunks { get; }

	/// <summary>Gets the first source location of the section.</summary>
	public PatchSourceLocation SourceLocation { get; }
}

/// <summary>Contains the immutable syntax model parsed from one patch stream.</summary>
internal sealed class PatchDocument {
	/// <summary>Initializes a parsed patch document.</summary>
	/// <param name="files">The parsed file patches in source order.</param>
	/// <param name="leadingText">The leading non-patch text region.</param>
	/// <param name="interstitialText">The non-patch regions between file patches.</param>
	/// <param name="trailingText">The trailing non-patch text region.</param>
	public PatchDocument(
		IReadOnlyList<PatchFilePatch> files,
		PatchTextRegion? leadingText,
		IReadOnlyList<PatchTextRegion> interstitialText,
		PatchTextRegion? trailingText
	) {
		ArgumentNullException.ThrowIfNull( files );
		ArgumentNullException.ThrowIfNull( interstitialText );
		this.Files = new ReadOnlyCollection<PatchFilePatch>( files.ToArray() );
		this.LeadingText = leadingText;
		this.InterstitialText = new ReadOnlyCollection<PatchTextRegion>( interstitialText.ToArray() );
		this.TrailingText = trailingText;
	}

	/// <summary>Gets the parsed file patches in source order.</summary>
	public IReadOnlyList<PatchFilePatch> Files { get; }

	/// <summary>Gets the leading non-patch text region.</summary>
	public PatchTextRegion? LeadingText { get; }

	/// <summary>Gets the non-patch regions between file patches.</summary>
	public IReadOnlyList<PatchTextRegion> InterstitialText { get; }

	/// <summary>Gets the trailing non-patch text region.</summary>
	public PatchTextRegion? TrailingText { get; }
}

/// <summary>Defines bounded-resource limits for syntax parsing and immutable models.</summary>
internal sealed class PatchParseLimits {
	/// <summary>Gets the default syntax-parser limits.</summary>
	public static PatchParseLimits Default { get; } = new();

	/// <summary>Gets or initializes the maximum parsed file-section count.</summary>
	public int MaximumFiles { get; init; } = 100_000;

	/// <summary>Gets or initializes the maximum parsed hunk count.</summary>
	public int MaximumHunks { get; init; } = 1_000_000;

	/// <summary>Gets or initializes the maximum logical hunk-line count.</summary>
	public int MaximumDataLines { get; init; } = 2_000_000;

	/// <summary>Gets or initializes the maximum bytes materialized into syntax models.</summary>
	public long MaximumMaterializedBytes { get; init; } = 128L * 1024L * 1024L;

	/// <summary>Validates the configured parser limits.</summary>
	public void Validate() {
		if ( this.MaximumFiles < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumFiles ) );
		}
		if ( this.MaximumHunks < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumHunks ) );
		}
		if ( this.MaximumDataLines < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumDataLines ) );
		}
		if ( this.MaximumMaterializedBytes < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumMaterializedBytes ) );
		}
	}
}
