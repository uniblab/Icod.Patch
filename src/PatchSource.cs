namespace Icod.Patch;

using System.Buffers;
using System.IO;

/// <summary>Owns a byte-preserved, spill-backed patch stream and its source map.</summary>
internal sealed class PatchSource : IAsyncDisposable {
	private const int BufferSize = 64 * 1024;
	private readonly FileStream spool;
	private readonly string spoolPath;
	private bool disposed;

	private PatchSource(
		FileStream spool,
		string spoolPath,
		IReadOnlyList<PatchRecord> records,
		IReadOnlyList<PatchLineProbe> probes
	) {
		this.spool = spool;
		this.spoolPath = spoolPath;
		this.Records = records;
		this.Probes = probes;
	}

	/// <summary>Gets the byte-preserved source records.</summary>
	public IReadOnlyList<PatchRecord> Records { get; }

	/// <summary>Gets the structural probes associated with the records.</summary>
	public IReadOnlyList<PatchLineProbe> Probes { get; }

	/// <summary>Gets the private temporary-spool path for lifecycle verification.</summary>
	internal string TemporaryPath => this.spoolPath;

	/// <summary>Reads and indexes a patch stream using bounded memory.</summary>
	/// <param name="input">The binary patch input.</param>
	/// <param name="limits">The resource limits.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The spill-backed patch source.</returns>
	public static async Task<PatchSource> ReadAsync(
		Stream input,
		PatchScanLimits? limits = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( input );
		limits ??= PatchScanLimits.Default;
		limits.Validate();
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-", Guid.NewGuid().ToString( "N" ), ".tmp" )
		);
		FileStream? spool = null;
		try {
			spool = PatchTemporaryFile.CreateNew(
				path,
				FileOptions.Asynchronous | FileOptions.SequentialScan
			);
			var records = new List<PatchRecord>();
			var probes = new List<PatchLineProbe>();
			var buffer = ArrayPool<byte>.Shared.Rent( BufferSize );
			try {
				using var line = new MemoryStream();
				long totalBytes = 0;
				long lineOffset = 0;
				long lineNumber = 1;
				var pendingCarriageReturn = false;
				while ( true ) {
					cancellationToken.ThrowIfCancellationRequested();
					var read = await input.ReadAsync(
						buffer.AsMemory( 0, BufferSize ),
						cancellationToken
					).ConfigureAwait( false );
					if ( 0 == read ) {
						break;
					}
					try {
						totalBytes = checked( totalBytes + read );
					} catch ( OverflowException ) {
						throw new PatchInputException(
							"patch input size is too large",
							new PatchSourceLocation( long.MaxValue, lineNumber )
						);
					}
					if ( limits.MaximumBytes < totalBytes ) {
						throw new PatchInputException(
							"patch input exceeds the configured byte limit",
							new PatchSourceLocation( totalBytes - read, lineNumber )
						);
					}
					await spool.WriteAsync(
						buffer.AsMemory( 0, read ),
						cancellationToken
					).ConfigureAwait( false );
					for ( var index = 0; index < read; index++ ) {
						var value = buffer[index];
						var absoluteOffset = totalBytes - read + index;
						if ( pendingCarriageReturn ) {
							pendingCarriageReturn = false;
							if ( (byte)'\n' == value ) {
								AddRecord(
									records,
									probes,
									line,
									lineOffset,
									lineNumber,
									PatchLineTerminator.CarriageReturnLineFeed,
									limits
								);
								lineOffset = checked( absoluteOffset + 1 );
								lineNumber++;
								continue;
							}
							AddRecord(
								records,
								probes,
								line,
								lineOffset,
								lineNumber,
								PatchLineTerminator.CarriageReturn,
								limits
							);
							lineOffset = absoluteOffset;
							lineNumber++;
						}
						if ( (byte)'\r' == value ) {
							pendingCarriageReturn = true;
							continue;
						}
						if ( (byte)'\n' == value ) {
							AddRecord(
								records,
								probes,
								line,
								lineOffset,
								lineNumber,
								PatchLineTerminator.LineFeed,
								limits
							);
							lineOffset = checked( absoluteOffset + 1 );
							lineNumber++;
							continue;
						}
						line.WriteByte( value );
						if ( limits.MaximumRecordBytes < line.Length ) {
							throw new PatchInputException(
								"patch record exceeds the configured byte limit",
								new PatchSourceLocation( lineOffset, lineNumber )
							);
						}
					}
				}
				if ( pendingCarriageReturn ) {
					AddRecord(
						records,
						probes,
						line,
						lineOffset,
						lineNumber,
						PatchLineTerminator.CarriageReturn,
						limits
					);
				} else if ( 0 < line.Length ) {
					AddRecord(
						records,
						probes,
						line,
						lineOffset,
						lineNumber,
						PatchLineTerminator.None,
						limits
					);
				}
			} finally {
				ArrayPool<byte>.Shared.Return( buffer );
			}
			await spool.FlushAsync( cancellationToken ).ConfigureAwait( false );
			spool.Position = 0;
			return new PatchSource( spool, path, records, probes );
		} catch {
			if ( null != spool ) {
				await spool.DisposeAsync().ConfigureAwait( false );
			}
			TryDelete( path );
			throw;
		}
	}

	/// <summary>Reads one original record from the spill file.</summary>
	/// <param name="recordIndex">The zero-based record index.</param>
	/// <param name="includeTerminator">Whether to include the original terminator.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The original record bytes.</returns>
	public async Task<byte[]> ReadRecordAsync(
		int recordIndex,
		bool includeTerminator,
		CancellationToken cancellationToken = default
	) {
		ObjectDisposedException.ThrowIf( this.disposed, this );
		if ( recordIndex < 0 || this.Records.Count <= recordIndex ) {
			throw new ArgumentOutOfRangeException( nameof( recordIndex ) );
		}
		var record = this.Records[recordIndex];
		var length = includeTerminator ? record.TotalLength : record.ContentLength;
		if ( int.MaxValue < length ) {
			throw new InvalidOperationException( "record is too large to materialize" );
		}
		var bytes = new byte[(int)length];
		this.spool.Position = record.Location.ByteOffset;
		var offset = 0;
		while ( offset < bytes.Length ) {
			var read = await this.spool.ReadAsync(
				bytes.AsMemory( offset ),
				cancellationToken
			).ConfigureAwait( false );
			if ( 0 == read ) {
				throw new EndOfStreamException( "unexpected end of patch spool" );
			}
			offset += read;
		}
		return bytes;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if ( this.disposed ) {
			return;
		}
		this.disposed = true;
		await this.spool.DisposeAsync().ConfigureAwait( false );
		File.Delete( this.spoolPath );
	}

	private static void AddRecord(
		List<PatchRecord> records,
		List<PatchLineProbe> probes,
		MemoryStream line,
		long lineOffset,
		long lineNumber,
		PatchLineTerminator terminator,
		PatchScanLimits limits
	) {
		if ( limits.MaximumRecords <= records.Count ) {
			throw new PatchInputException(
				"patch input exceeds the configured record limit",
				new PatchSourceLocation( lineOffset, lineNumber )
			);
		}
		var location = new PatchSourceLocation( lineOffset, lineNumber );
		var content = line.GetBuffer().AsSpan( 0, checked( (int)line.Length ) );
		probes.Add( PatchScanner.ClassifyLine( content, location ) );
		records.Add( new PatchRecord( location, line.Length, terminator ) );
		line.SetLength( 0 );
		line.Position = 0;
	}

	private static void TryDelete( string path ) {
		try {
			File.Delete( path );
		} catch ( IOException ) {
		} catch ( UnauthorizedAccessException ) {
		}
	}
}
