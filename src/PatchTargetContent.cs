namespace Icod.Patch;

using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;

/// <summary>Defines bounded-resource limits for indexed target content.</summary>
internal sealed class PatchTargetLimits {
	/// <summary>Gets the default target-content limits.</summary>
	public static PatchTargetLimits Default { get; } = new();

	/// <summary>Gets or initializes the byte count retained in memory before spilling.</summary>
	public int MemoryThresholdBytes { get; init; } = 1024 * 1024;

	/// <summary>Gets or initializes the maximum target byte count.</summary>
	public long MaximumBytes { get; init; } = 1024L * 1024L * 1024L;

	/// <summary>Gets or initializes the maximum target record count.</summary>
	public int MaximumRecords { get; init; } = 10_000_000;

	/// <summary>Validates the configured limits.</summary>
	public void Validate() {
		if ( this.MemoryThresholdBytes < 0 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MemoryThresholdBytes ) );
		}
		if ( this.MaximumBytes < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumBytes ) );
		}
		if ( this.MaximumRecords < 1 ) {
			throw new ArgumentOutOfRangeException( nameof( this.MaximumRecords ) );
		}
	}
}

/// <summary>Owns byte-preserved, line-indexed target content using memory or a private spill file.</summary>
internal sealed class PatchTargetContent : IAsyncDisposable {
	private const int BufferSize = 64 * 1024;
	private readonly byte[]? memory;
	private readonly FileStream? spool;
	private readonly string? spoolPath;
	private readonly SemaphoreSlim access = new( 1, 1 );
	private bool disposed;

	private PatchTargetContent(
		byte[]? memory,
		FileStream? spool,
		string? spoolPath,
		IReadOnlyList<PatchRecord> records,
		long length
	) {
		this.memory = memory;
		this.spool = spool;
		this.spoolPath = spoolPath;
		this.Records = new ReadOnlyCollection<PatchRecord>( records.ToArray() );
		this.Length = length;
	}

	/// <summary>Gets the indexed target records.</summary>
	public IReadOnlyList<PatchRecord> Records { get; }

	/// <summary>Gets the complete byte count.</summary>
	public long Length { get; }

	/// <summary>Gets whether the content is backed by a private spill file.</summary>
	public bool IsSpillBacked => null != this.spool;

	/// <summary>Gets the temporary path for lifecycle tests, when spill-backed.</summary>
	internal string? TemporaryPath => this.spoolPath;

	/// <summary>Reads and indexes target content while preserving every byte and record terminator.</summary>
	/// <param name="input">The target byte stream.</param>
	/// <param name="limits">The resource limits.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The indexed target content.</returns>
	public static async Task<PatchTargetContent> ReadAsync(
		Stream input,
		PatchTargetLimits? limits = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( input );
		limits ??= PatchTargetLimits.Default;
		limits.Validate();
		using var retained = new MemoryStream();
		FileStream? spool = null;
		string? spoolPath = null;
		var records = new List<PatchRecord>();
		var buffer = ArrayPool<byte>.Shared.Rent( BufferSize );
		try {
			long totalBytes = 0;
			long lineOffset = 0;
			long lineNumber = 1;
			long lineLength = 0;
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
					throw new PatchApplicationException( "target content size is too large" );
				}
				if ( limits.MaximumBytes < totalBytes ) {
					throw new PatchApplicationException( "target content exceeds the configured byte limit" );
				}
				if ( null == spool && limits.MemoryThresholdBytes < totalBytes ) {
					spoolPath = CreateTemporaryPath();
					spool = PatchTemporaryFile.CreateNew(
						spoolPath,
						FileOptions.Asynchronous | FileOptions.SequentialScan
					);
					retained.Position = 0;
					await retained.CopyToAsync( spool, BufferSize, cancellationToken ).ConfigureAwait( false );
				}
				if ( null == spool ) {
					await retained.WriteAsync( buffer.AsMemory( 0, read ), cancellationToken ).ConfigureAwait( false );
				} else {
					await spool.WriteAsync( buffer.AsMemory( 0, read ), cancellationToken ).ConfigureAwait( false );
				}
				for ( var index = 0; index < read; index++ ) {
					var value = buffer[index];
					var absoluteOffset = totalBytes - read + index;
					if ( pendingCarriageReturn ) {
						pendingCarriageReturn = false;
						if ( (byte)'\n' == value ) {
							AddRecord( records, lineLength, lineOffset, lineNumber, PatchLineTerminator.CarriageReturnLineFeed, limits );
							lineLength = 0;
							lineOffset = checked( absoluteOffset + 1 );
							lineNumber++;
							continue;
						}
						AddRecord( records, lineLength, lineOffset, lineNumber, PatchLineTerminator.CarriageReturn, limits );
						lineLength = 0;
						lineOffset = absoluteOffset;
						lineNumber++;
					}
					if ( (byte)'\r' == value ) {
						pendingCarriageReturn = true;
					} else if ( (byte)'\n' == value ) {
						AddRecord( records, lineLength, lineOffset, lineNumber, PatchLineTerminator.LineFeed, limits );
						lineLength = 0;
						lineOffset = checked( absoluteOffset + 1 );
						lineNumber++;
					} else {
						try {
							lineLength = checked( lineLength + 1 );
						} catch ( OverflowException ) {
							throw new PatchApplicationException( "target record size is too large" );
						}
					}
				}
			}
			if ( pendingCarriageReturn ) {
				AddRecord( records, lineLength, lineOffset, lineNumber, PatchLineTerminator.CarriageReturn, limits );
			} else if ( 0 < lineLength ) {
				AddRecord( records, lineLength, lineOffset, lineNumber, PatchLineTerminator.None, limits );
			}
			if ( null != spool ) {
				await spool.FlushAsync( cancellationToken ).ConfigureAwait( false );
				spool.Position = 0;
				return new PatchTargetContent( null, spool, spoolPath, records, totalBytes );
			}
			return new PatchTargetContent( retained.ToArray(), null, null, records, totalBytes );
		} catch {
			if ( null != spool ) {
				await spool.DisposeAsync().ConfigureAwait( false );
			}
			TryDelete( spoolPath );
			throw;
		} finally {
			ArrayPool<byte>.Shared.Return( buffer );
		}
	}

	/// <summary>Creates indexed content from a byte sequence.</summary>
	/// <param name="bytes">The complete target bytes.</param>
	/// <param name="limits">The resource limits.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The indexed target content.</returns>
	public static async Task<PatchTargetContent> FromBytesAsync(
		ReadOnlyMemory<byte> bytes,
		PatchTargetLimits? limits = null,
		CancellationToken cancellationToken = default
	) {
		using var input = new MemoryStream( bytes.ToArray(), writable: false );
		return await ReadAsync( input, limits, cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Reads one complete record or its content bytes.</summary>
	/// <param name="recordIndex">The zero-based record index.</param>
	/// <param name="includeTerminator">Whether the original terminator is included.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The selected bytes.</returns>
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
			throw new PatchApplicationException( "target record is too large to materialize" );
		}
		return await this.ReadBytesAsync(
			record.Location.ByteOffset,
			checked( (int)length ),
			cancellationToken
		).ConfigureAwait( false );
	}

	/// <summary>Streams one indexed record to an output without materializing it.</summary>
	/// <param name="recordIndex">The zero-based record index.</param>
	/// <param name="output">The destination stream.</param>
	/// <param name="includeTerminator">Whether the original terminator is included.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task representing completion.</returns>
	public async Task WriteRecordToAsync(
		int recordIndex,
		Stream output,
		bool includeTerminator,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( output );
		ObjectDisposedException.ThrowIf( this.disposed, this );
		if ( recordIndex < 0 || this.Records.Count <= recordIndex ) {
			throw new ArgumentOutOfRangeException( nameof( recordIndex ) );
		}
		var record = this.Records[recordIndex];
		var length = includeTerminator ? record.TotalLength : record.ContentLength;
		if ( null != this.memory ) {
			await output.WriteAsync(
				this.memory.AsMemory( checked( (int)record.Location.ByteOffset ), checked( (int)length ) ),
				cancellationToken
			).ConfigureAwait( false );
			return;
		}
		await this.access.WaitAsync( cancellationToken ).ConfigureAwait( false );
		var buffer = ArrayPool<byte>.Shared.Rent( BufferSize );
		try {
			this.spool!.Position = record.Location.ByteOffset;
			var remaining = length;
			while ( 0 < remaining ) {
				cancellationToken.ThrowIfCancellationRequested();
				var requested = checked( (int)Math.Min( remaining, buffer.Length ) );
				var read = await this.spool.ReadAsync(
					buffer.AsMemory( 0, requested ),
					cancellationToken
				).ConfigureAwait( false );
				if ( 0 == read ) {
					throw new EndOfStreamException( "unexpected end of target spool" );
				}
				await output.WriteAsync( buffer.AsMemory( 0, read ), cancellationToken ).ConfigureAwait( false );
				remaining -= read;
			}
		} finally {
			ArrayPool<byte>.Shared.Return( buffer );
			this.access.Release();
		}
	}

	/// <summary>Writes the complete byte-preserved content to an output stream.</summary>
	/// <param name="output">The destination stream.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task representing completion.</returns>
	public async Task WriteToAsync( Stream output, CancellationToken cancellationToken = default ) {
		ArgumentNullException.ThrowIfNull( output );
		ObjectDisposedException.ThrowIf( this.disposed, this );
		if ( null != this.memory ) {
			await output.WriteAsync( this.memory, cancellationToken ).ConfigureAwait( false );
			return;
		}
		await this.access.WaitAsync( cancellationToken ).ConfigureAwait( false );
		try {
			this.spool!.Position = 0;
			await this.spool.CopyToAsync( output, BufferSize, cancellationToken ).ConfigureAwait( false );
		} finally {
			this.access.Release();
		}
	}

	/// <summary>Materializes the complete target content when it fits in a managed array.</summary>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The complete bytes.</returns>
	public async Task<byte[]> ToArrayAsync( CancellationToken cancellationToken = default ) {
		if ( int.MaxValue < this.Length ) {
			throw new PatchApplicationException( "target content is too large to materialize" );
		}
		using var output = new MemoryStream( checked( (int)this.Length ) );
		await this.WriteToAsync( output, cancellationToken ).ConfigureAwait( false );
		return output.ToArray();
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if ( this.disposed ) {
			return;
		}
		this.disposed = true;
		if ( null != this.spool ) {
			await this.spool.DisposeAsync().ConfigureAwait( false );
		}
		this.access.Dispose();
		TryDelete( this.spoolPath );
	}

	private async Task<byte[]> ReadBytesAsync(
		long offset,
		int length,
		CancellationToken cancellationToken
	) {
		var bytes = new byte[length];
		if ( null != this.memory ) {
			this.memory.AsSpan( checked( (int)offset ), length ).CopyTo( bytes );
			return bytes;
		}
		await this.access.WaitAsync( cancellationToken ).ConfigureAwait( false );
		try {
			this.spool!.Position = offset;
			var copied = 0;
			while ( copied < length ) {
				var read = await this.spool.ReadAsync( bytes.AsMemory( copied ), cancellationToken ).ConfigureAwait( false );
				if ( 0 == read ) {
					throw new EndOfStreamException( "unexpected end of target spool" );
				}
				copied += read;
			}
			return bytes;
		} finally {
			this.access.Release();
		}
	}

	private static void AddRecord(
		List<PatchRecord> records,
		long contentLength,
		long offset,
		long lineNumber,
		PatchLineTerminator terminator,
		PatchTargetLimits limits
	) {
		if ( limits.MaximumRecords <= records.Count ) {
			throw new PatchApplicationException( "target content exceeds the configured record limit" );
		}
		records.Add(
			new PatchRecord(
				new PatchSourceLocation( offset, lineNumber ),
				contentLength,
				terminator
			)
		);
	}

	private static string CreateTemporaryPath() {
		return System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-target-", Guid.NewGuid().ToString( "N" ), ".tmp" )
		);
	}

	private static void TryDelete( string? path ) {
		if ( null == path ) {
			return;
		}
		try {
			File.Delete( path );
		} catch ( IOException ) {
		} catch ( UnauthorizedAccessException ) {
		}
	}
}
