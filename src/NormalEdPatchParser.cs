namespace Icod.Patch;

/// <summary>Parses normal-diff commands and the minimal GNU patch ed-script grammar.</summary>
internal static class NormalEdPatchParser {
	/// <summary>Parses one detected normal or ed-script section.</summary>
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
			PatchFormat.Normal => ParseNormal( records, budget ),
			PatchFormat.EdScript => ParseEdScript( records, budget ),
			_ => throw new ArgumentException( "section is not a normal or ed-script patch", nameof( section ) )
		};
	}

	private static PatchFilePatch ParseNormal(
		IReadOnlyList<PatchRawRecord> records,
		PatchParseBudget budget
	) {
		if ( 0 == records.Count ) {
			throw new PatchInputException( "normal patch contains no commands", new PatchSourceLocation( 0, 1 ) );
		}
		var hunks = new List<PatchHunk>();
		var index = 0;
		while ( index < records.Count ) {
			var hunkStart = index;
			var command = ParseNormalCommand( records[index++] );
			var oldLines = new List<PatchDataLine>();
			var newLines = new List<PatchDataLine>();
			switch ( command.Operation ) {
				case PatchOperationKind.Add:
					ReadNormalDataBlock(
						records,
						ref index,
						(byte)'>',
						command.NewRange.Count,
						newLines,
						budget
					);
					break;
				case PatchOperationKind.Delete:
					ReadNormalDataBlock(
						records,
						ref index,
						(byte)'<',
						command.OldRange.Count,
						oldLines,
						budget
					);
					break;
				case PatchOperationKind.Change:
					ReadNormalDataBlock(
						records,
						ref index,
						(byte)'<',
						command.OldRange.Count,
						oldLines,
						budget
					);
					if ( index >= records.Count || !PatchParsing.EqualsAscii( records[index], "---" ) ) {
						var location = index < records.Count
							? records[index].Location
							: records[hunkStart].Location;
						throw new PatchInputException( "missing normal-diff change separator", location );
					}
					index++;
					ReadNormalDataBlock(
						records,
						ref index,
						(byte)'>',
						command.NewRange.Count,
						newLines,
						budget
					);
					break;
				default:
					throw new InvalidOperationException( "unsupported normal-diff operation" );
			}
			if ( index < records.Count && !LooksNumericCommand( records[index] ) ) {
				throw new PatchInputException( "trailing garbage after normal-diff hunk", records[index].Location );
			}
			budget.AddHunk( records[hunkStart].Location );
			hunks.Add(
				new PatchHunk(
					command.Operation,
					command.OldRange,
					command.NewRange,
					oldLines,
					newLines,
					null,
					records[hunkStart].Location,
					CopyRecords( records, hunkStart, index )
				)
			);
		}
		return new PatchFilePatch(
			PatchFormat.Normal,
			PatchFileChangeKind.Unspecified,
			null,
			null,
			hunks,
			records[0].Location
		);
	}

	private static PatchFilePatch ParseEdScript(
		IReadOnlyList<PatchRawRecord> records,
		PatchParseBudget budget
	) {
		if ( 0 == records.Count ) {
			throw new PatchInputException( "ed script contains no commands", new PatchSourceLocation( 0, 1 ) );
		}
		var hunks = new List<PatchHunk>();
		var index = 0;
		long? previousAddress = null;
		while ( index < records.Count ) {
			var hunkStart = index;
			var command = ParseEdCommand( records[index++] );
			if ( previousAddress.HasValue && previousAddress.Value < command.OldRange.Start ) {
				throw new PatchInputException(
					"ed-script commands are not in reverse address order",
					records[hunkStart].Location
				);
			}
			previousAddress = command.OldRange.Start;
			var newLines = new List<PatchDataLine>();
			if ( PatchOperationKind.Delete != command.Operation ) {
				ReadEdTextBlock( records, ref index, newLines, budget, records[hunkStart].Location );
				FoldGnuDotProtection( records, ref index, newLines, budget, records[hunkStart].Location );
			}
			budget.AddHunk( records[hunkStart].Location );
			hunks.Add(
				new PatchHunk(
					command.Operation,
					command.OldRange,
					null,
					Array.Empty<PatchDataLine>(),
					newLines,
					null,
					records[hunkStart].Location,
					CopyRecords( records, hunkStart, index )
				)
			);
			if ( index < records.Count && !LooksNumericCommand( records[index] ) ) {
				throw new PatchInputException( "unsupported or trailing ed command", records[index].Location );
			}
		}
		return new PatchFilePatch(
			PatchFormat.EdScript,
			PatchFileChangeKind.Unspecified,
			null,
			null,
			hunks,
			records[0].Location
		);
	}

	private static NormalCommand ParseNormalCommand( PatchRawRecord record ) {
		var value = record.Content.Span;
		var index = 0;
		PatchParsing.SkipHorizontalSpace( value, ref index );
		var oldRange = PatchParsing.ParseInclusiveRange( value, ref index, record.Location, allowZero: true );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index >= value.Length ) {
			throw new PatchInputException( "missing normal-diff operation", record.Location );
		}
		var operationByte = value[index++];
		if ( operationByte != (byte)'a' && operationByte != (byte)'c' && operationByte != (byte)'d' ) {
			throw new PatchInputException( "invalid normal-diff operation", record.Location );
		}
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index >= value.Length ) {
			throw new PatchInputException( "missing new range in normal-diff command", record.Location );
		}
		var newRange = PatchParsing.ParseInclusiveRange( value, ref index, record.Location, allowZero: true );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index != value.Length ) {
			throw new PatchInputException( "trailing garbage in normal-diff command", record.Location );
		}
		return operationByte switch {
			(byte)'a' => CreateAddCommand( oldRange, newRange, record.Location ),
			(byte)'d' => CreateDeleteCommand( oldRange, newRange, record.Location ),
			_ => CreateChangeCommand( oldRange, newRange, record.Location )
		};
	}

	private static NormalCommand CreateAddCommand(
		PatchRange oldRange,
		PatchRange newRange,
		PatchSourceLocation location
	) {
		if ( 1 != oldRange.Count || 0 == newRange.Count ) {
			throw new PatchInputException( "invalid append ranges in normal diff", location );
		}
		return new NormalCommand(
			PatchOperationKind.Add,
			new PatchRange( oldRange.Start, 0 ),
			newRange
		);
	}

	private static NormalCommand CreateDeleteCommand(
		PatchRange oldRange,
		PatchRange newRange,
		PatchSourceLocation location
	) {
		if ( 0 == oldRange.Count || 1 != newRange.Count ) {
			throw new PatchInputException( "invalid delete ranges in normal diff", location );
		}
		return new NormalCommand(
			PatchOperationKind.Delete,
			oldRange,
			new PatchRange( newRange.Start, 0 )
		);
	}

	private static NormalCommand CreateChangeCommand(
		PatchRange oldRange,
		PatchRange newRange,
		PatchSourceLocation location
	) {
		if ( 0 == oldRange.Start || 0 == newRange.Start ) {
			throw new PatchInputException( "change ranges must start at line one or later", location );
		}
		return new NormalCommand( PatchOperationKind.Change, oldRange, newRange );
	}

	private static EdCommand ParseEdCommand( PatchRawRecord record ) {
		var value = record.Content.Span;
		var index = 0;
		PatchParsing.SkipHorizontalSpace( value, ref index );
		var parsedRange = PatchParsing.ParseInclusiveRange( value, ref index, record.Location, allowZero: true );
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index >= value.Length ) {
			throw new PatchInputException( "missing ed command", record.Location );
		}
		var command = value[index++];
		PatchParsing.SkipHorizontalSpace( value, ref index );
		if ( index != value.Length ) {
			throw new PatchInputException( "trailing garbage in ed command", record.Location );
		}
		return command switch {
			(byte)'a' => 1 == parsedRange.Count
				? new EdCommand(
					PatchOperationKind.Add,
					new PatchRange( parsedRange.Start, 0 )
				)
				: throw new PatchInputException( "ed append command has a range", record.Location ),
			(byte)'c' => 0 < parsedRange.Start
				? new EdCommand( PatchOperationKind.Change, parsedRange )
				: throw new PatchInputException( "ed change range starts at zero", record.Location ),
			(byte)'d' => 0 < parsedRange.Start
				? new EdCommand( PatchOperationKind.Delete, parsedRange )
				: throw new PatchInputException( "ed delete range starts at zero", record.Location ),
			_ => throw new PatchInputException( "unsupported ed command", record.Location )
		};
	}

	private static void ReadNormalDataBlock(
		IReadOnlyList<PatchRawRecord> records,
		ref int index,
		byte marker,
		long expectedCount,
		IList<PatchDataLine> destination,
		PatchParseBudget budget
	) {
		long count = 0;
		while ( count < expectedCount ) {
			if ( index >= records.Count ) {
				throw new PatchInputException(
					"normal-diff data block ended early",
					0 < records.Count ? records[^1].Location : new PatchSourceLocation( 0, 1 )
				);
			}
			var record = records[index];
			if (
				record.Content.Length < 2
				|| marker != record.Content.Span[0]
				|| (byte)' ' != record.Content.Span[1]
			) {
				throw new PatchInputException( "malformed normal-diff data line", record.Location );
			}
			destination.Add( PatchParsing.CreateDataLine( record, 2, false, budget ) );
			count++;
			index++;
			if ( index < records.Count && PatchParsing.IsNoNewlineMarker( records[index] ) ) {
				destination[^1] = destination[^1].WithTerminator( PatchLineTerminator.None );
				index++;
			}
		}
	}

	private static void ReadEdTextBlock(
		IReadOnlyList<PatchRawRecord> records,
		ref int index,
		IList<PatchDataLine> destination,
		PatchParseBudget budget,
		PatchSourceLocation commandLocation
	) {
		while ( index < records.Count ) {
			var record = records[index++];
			if ( PatchParsing.EqualsAscii( record, "." ) ) {
				if ( PatchLineTerminator.None == record.Terminator ) {
					throw new PatchInputException( "unterminated ed text-block delimiter", record.Location );
				}
				return;
			}
			if ( PatchLineTerminator.None == record.Terminator ) {
				throw new PatchInputException( "unterminated ed text block", record.Location );
			}
			destination.Add( PatchParsing.CreateDataLine( record, 0, false, budget ) );
		}
		throw new PatchInputException( "missing ed text-block delimiter", commandLocation );
	}

	private static void FoldGnuDotProtection(
		IReadOnlyList<PatchRawRecord> records,
		ref int index,
		IList<PatchDataLine> destination,
		PatchParseBudget budget,
		PatchSourceLocation commandLocation
	) {
		while ( index < records.Count && PatchParsing.EqualsAscii( records[index], "s/.//" ) ) {
			if (
				0 == destination.Count
				|| !destination[^1].Content.Span.SequenceEqual( ".."u8 )
			) {
				throw new PatchInputException(
					"GNU ed dot-protection command has no protected dot line",
					records[index].Location
				);
			}
			destination[^1] = destination[^1].WithContent( "."u8 );
			index++;
			if ( index < records.Count && PatchParsing.EqualsAscii( records[index], "a" ) ) {
				index++;
				ReadEdTextBlock( records, ref index, destination, budget, commandLocation );
			}
		}
	}

	private static bool LooksNumericCommand( PatchRawRecord record ) {
		var value = record.Content.Span;
		var index = 0;
		PatchParsing.SkipHorizontalSpace( value, ref index );
		return index < value.Length && PatchParsing.IsAsciiDigit( value[index] );
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

	private readonly struct NormalCommand {
		/// <summary>Initializes a parsed normal-diff command.</summary>
		/// <param name="operation">The semantic operation.</param>
		/// <param name="oldRange">The normalized old range.</param>
		/// <param name="newRange">The normalized new range.</param>
		public NormalCommand(
			PatchOperationKind operation,
			PatchRange oldRange,
			PatchRange newRange
		) {
			this.Operation = operation;
			this.OldRange = oldRange;
			this.NewRange = newRange;
		}

		/// <summary>Gets the semantic operation.</summary>
		public PatchOperationKind Operation { get; }

		/// <summary>Gets the normalized old range or append address.</summary>
		public PatchRange OldRange { get; }

		/// <summary>Gets the normalized new range.</summary>
		public PatchRange NewRange { get; }
	}

	private readonly struct EdCommand {
		/// <summary>Initializes a parsed ed command.</summary>
		/// <param name="operation">The semantic operation.</param>
		/// <param name="oldRange">The normalized old range or append address.</param>
		public EdCommand( PatchOperationKind operation, PatchRange oldRange ) {
			this.Operation = operation;
			this.OldRange = oldRange;
		}

		/// <summary>Gets the semantic operation.</summary>
		public PatchOperationKind Operation { get; }

		/// <summary>Gets the normalized old range or append address.</summary>
		public PatchRange OldRange { get; }
	}
}
