namespace Icod.Patch;

using System.Text;
using Icod.Path;

/// <summary>Decodes patch filename evidence and applies GNU component-aware stripping.</summary>
internal static class PatchPathSelection {
	private static readonly byte[] IndexPrefix = "Index:"u8.ToArray();

	/// <summary>Extracts the first <c>Index:</c> filename from a leading-text region.</summary>
	public static async Task<string?> ExtractIndexNameAsync(
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
			var recordIndex = checked( region.FirstRecordIndex + offset );
			var bytes = await source.ReadRecordAsync(
				recordIndex,
				includeTerminator: false,
				cancellationToken
			).ConfigureAwait( false );
			var span = bytes.AsSpan();
			var index = 0;
			while ( index < span.Length && span[index] is (byte)' ' or (byte)'\t' ) {
				index++;
			}
			if ( span.Length - index < IndexPrefix.Length
				|| !span[index..].StartsWith( IndexPrefix ) ) {
				continue;
			}
			index += IndexPrefix.Length;
			while ( index < span.Length && span[index] is (byte)' ' or (byte)'\t' ) {
				index++;
			}
			if ( index >= span.Length ) {
				continue;
			}
			return Encoding.UTF8.GetString( span[index..] );
		}
		return null;
	}

	/// <summary>Decodes a quoted or unquoted filename token and rejects unsafe control bytes.</summary>
	public static string DecodeName( string name, PatchSourceLocation location ) {
		ArgumentNullException.ThrowIfNull( name );
		var value = name.Trim();
		if ( 0 == value.Length ) {
			throw new PatchInputException( "missing patch filename", location );
		}
		if ( '"' == value[0] ) {
			value = DecodeQuotedName( value, location );
		}
		ValidateSafeName( value, location );
		return value;
	}

	/// <summary>Applies GNU <c>-p</c> separator-run semantics using the selected pathname grammar.</summary>
	public static string? Strip(
		string name,
		int? stripCount,
		PathPlatformSemantics semantics
	) {
		ArgumentNullException.ThrowIfNull( name );
		ArgumentNullException.ThrowIfNull( semantics );
		if ( null == stripCount ) {
			var lastSeparator = -1;
			for ( var index = 0; index < name.Length; index++ ) {
				if ( semantics.IsDirectorySeparator( name[index] ) ) {
					lastSeparator = index;
				}
			}
			var basename = name[( lastSeparator + 1 )..];
			return 0 == basename.Length ? null : basename;
		}
		if ( 0 == stripCount.Value ) {
			return name;
		}
		var remaining = stripCount.Value;
		var cursor = 0;
		while ( cursor < name.Length ) {
			if ( !semantics.IsDirectorySeparator( name[cursor] ) ) {
				cursor++;
				continue;
			}
			while ( cursor < name.Length && semantics.IsDirectorySeparator( name[cursor] ) ) {
				cursor++;
			}
			remaining--;
			if ( 0 == remaining ) {
				return cursor < name.Length ? name[cursor..] : null;
			}
		}
		return null;
	}

	/// <summary>Counts nonempty filename components using the selected pathname grammar.</summary>
	public static int CountComponents( string name, PathPlatformSemantics semantics ) {
		ArgumentNullException.ThrowIfNull( name );
		ArgumentNullException.ThrowIfNull( semantics );
		var count = 0;
		var inside = false;
		foreach ( var value in name ) {
			if ( semantics.IsDirectorySeparator( value ) ) {
				inside = false;
			} else if ( !inside ) {
				inside = true;
				count++;
			}
		}
		return count;
	}

	/// <summary>Gets the final component using the selected pathname grammar.</summary>
	public static string GetBasename( string name, PathPlatformSemantics semantics ) {
		ArgumentNullException.ThrowIfNull( name );
		ArgumentNullException.ThrowIfNull( semantics );
		var last = -1;
		for ( var index = 0; index < name.Length; index++ ) {
			if ( semantics.IsDirectorySeparator( name[index] ) ) {
				last = index;
			}
		}
		return name[( last + 1 )..];
	}

	/// <summary>Reports whether a candidate is GNU's null-device sentinel.</summary>
	public static bool IsNullDevice( string name ) {
		ArgumentNullException.ThrowIfNull( name );
		var value = name.Trim();
		if ( 1 < value.Length && '"' == value[0] && '"' == value[^1] ) {
			value = DecodeQuotedName( value, default );
		}
		return string.Equals( value, "/dev/null", StringComparison.Ordinal );
	}

	private static string DecodeQuotedName( string value, PatchSourceLocation location ) {
		var output = new StringBuilder( value.Length );
		var closed = false;
		for ( var index = 1; index < value.Length; index++ ) {
			var current = value[index];
			if ( '"' == current ) {
				for ( index++; index < value.Length; index++ ) {
					if ( value[index] is not ( ' ' or '\t' ) ) {
						throw new PatchInputException( "trailing garbage after quoted filename", location );
					}
				}
				closed = true;
				break;
			}
			if ( '\\' != current ) {
				output.Append( current );
				continue;
			}
			if ( ++index >= value.Length ) {
				throw new PatchInputException( "unterminated quoted filename", location );
			}
			current = value[index];
			if ( current is >= '0' and <= '7' ) {
				var octal = current - '0';
				var digits = 1;
				while ( digits < 3 && index + 1 < value.Length && value[index + 1] is >= '0' and <= '7' ) {
					index++;
					octal = checked( octal * 8 + value[index] - '0' );
					digits++;
				}
				output.Append( (char)octal );
				continue;
			}
			output.Append(
				current switch {
					'a' => '\a',
					'b' => '\b',
					'f' => '\f',
					'n' => '\n',
					'r' => '\r',
					't' => '\t',
					'v' => '\v',
					_ => current
				}
			);
		}
		if ( !closed ) {
			throw new PatchInputException( "unterminated quoted filename", location );
		}
		return output.ToString();
	}

	private static void ValidateSafeName( string value, PatchSourceLocation location ) {
		if ( 0 == value.Length ) {
			throw new PatchInputException( "missing patch filename", location );
		}
		foreach ( var character in value ) {
			if ( character is '\0' or '\n' or '\r' ) {
				throw new PatchInputException( "patch filename contains an unsafe control character", location );
			}
		}
	}

}
