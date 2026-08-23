namespace Icod.Patch;

using System.Text;

/// <summary>Extracts and checks traditional <c>Prereq:</c> revision tokens.</summary>
internal static class PatchPrerequisite {
	private static readonly byte[] Prefix = "Prereq:"u8.ToArray();

	private sealed class DelimitedTokenSearchStream : Stream {
		private readonly byte[] needle;
		private readonly byte[] window;
		private int start;
		private int count;

		/// <summary>Initializes a whitespace-delimited token scanner.</summary>
		/// <param name="needle">The nonempty token bytes.</param>
		public DelimitedTokenSearchStream( byte[] needle ) {
			ArgumentNullException.ThrowIfNull( needle );
			if ( 0 == needle.Length ) {
				throw new ArgumentException( "the prerequisite token is empty", nameof( needle ) );
			}
			this.needle = needle.ToArray();
			this.window = new byte[checked( needle.Length + 2 )];
			this.Feed( (byte)' ' );
		}

		/// <summary>Gets whether the token was found.</summary>
		public bool Found { get; private set; }

		/// <inheritdoc/>
		public override bool CanRead => false;

		/// <inheritdoc/>
		public override bool CanSeek => false;

		/// <inheritdoc/>
		public override bool CanWrite => true;

		/// <inheritdoc/>
		public override long Length => throw new NotSupportedException();

		/// <inheritdoc/>
		public override long Position {
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		/// <summary>Supplies the synthetic record-end delimiter.</summary>
		public void Complete() => this.Feed( (byte)' ' );

		/// <inheritdoc/>
		public override void Flush() {
		}

		/// <inheritdoc/>
		public override int Read( byte[] buffer, int offset, int count ) {
			throw new NotSupportedException();
		}

		/// <inheritdoc/>
		public override long Seek( long offset, SeekOrigin origin ) {
			throw new NotSupportedException();
		}

		/// <inheritdoc/>
		public override void SetLength( long value ) {
			throw new NotSupportedException();
		}

		/// <inheritdoc/>
		public override void Write( byte[] buffer, int offset, int count ) {
			ArgumentNullException.ThrowIfNull( buffer );
			ArgumentOutOfRangeException.ThrowIfNegative( offset );
			ArgumentOutOfRangeException.ThrowIfNegative( count );
			if ( buffer.Length - offset < count ) {
				throw new ArgumentException( "the selected byte range is outside the buffer", nameof( count ) );
			}
			this.Write( buffer.AsSpan( offset, count ) );
		}

		/// <inheritdoc/>
		public override void Write( ReadOnlySpan<byte> buffer ) {
			foreach ( var value in buffer ) {
				this.Feed( value );
				if ( this.Found ) {
					return;
				}
			}
		}

		/// <inheritdoc/>
		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		) {
			cancellationToken.ThrowIfCancellationRequested();
			this.Write( buffer, offset, count );
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			this.Write( buffer.Span );
			return ValueTask.CompletedTask;
		}

		private void Feed( byte value ) {
			if ( this.Found ) {
				return;
			}
			this.window[( this.start + this.count ) % this.window.Length] = value;
			this.count++;
			if ( this.window.Length != this.count ) {
				return;
			}
			this.Found = this.IsMatch();
			this.start = ( this.start + 1 ) % this.window.Length;
			this.count--;
		}

		private bool IsMatch() {
			if ( !IsAsciiWhitespace( this.At( 0 ) )
				|| !IsAsciiWhitespace( this.At( this.window.Length - 1 ) ) ) {
				return false;
			}
			for ( var index = 0; index < this.needle.Length; index++ ) {
				if ( this.needle[index] != this.At( index + 1 ) ) {
					return false;
				}
			}
			return true;
		}

		private byte At( int offset ) {
			return this.window[( this.start + offset ) % this.window.Length];
		}
	}

	/// <summary>Extracts the first prerequisite token from a patch text region.</summary>
	/// <param name="source">The patch source.</param>
	/// <param name="region">The text region to inspect.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The token, or <see langword="null"/>.</returns>
	public static async Task<string?> ExtractAsync(
		PatchSource source,
		PatchTextRegion? region,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( source );
		if ( null == region ) {
			return null;
		}
		for ( var offset = 0; offset < region.RecordCount; offset++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			var bytes = await source.ReadRecordAsync(
				checked( region.FirstRecordIndex + offset ),
				includeTerminator: false,
				cancellationToken
			).ConfigureAwait( false );
			var prefixIndex = bytes.AsSpan().IndexOf( Prefix );
			if ( prefixIndex < 0 ) {
				continue;
			}
			var index = prefixIndex + Prefix.Length;
			while ( index < bytes.Length && IsHorizontalBlank( bytes[index] ) ) {
				index++;
			}
			var start = index;
			while ( index < bytes.Length && !IsAsciiWhitespace( bytes[index] ) ) {
				index++;
			}
			if ( start < index ) {
				return Encoding.UTF8.GetString( bytes, start, index - start );
			}
		}
		return null;
	}

	/// <summary>Checks whether a prerequisite token occurs as a whitespace-delimited word.</summary>
	/// <param name="target">The indexed target.</param>
	/// <param name="token">The prerequisite token.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns><see langword="true"/> when the token is present.</returns>
	public static async Task<bool> ContainsAsync(
		PatchTargetContent target,
		string token,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( target );
		ArgumentException.ThrowIfNullOrEmpty( token );
		var needle = Encoding.UTF8.GetBytes( token );
		for ( var index = 0; index < target.Records.Count; index++ ) {
			cancellationToken.ThrowIfCancellationRequested();
			using var search = new DelimitedTokenSearchStream( needle );
			await target.WriteRecordToAsync(
				index,
				search,
				includeTerminator: false,
				cancellationToken
			).ConfigureAwait( false );
			search.Complete();
			if ( search.Found ) {
				return true;
			}
		}
		return false;
	}

	private static bool IsHorizontalBlank( byte value ) => (byte)' ' == value || (byte)'\t' == value;

	private static bool IsAsciiWhitespace( byte value ) {
		return value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f';
	}
}
