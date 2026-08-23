namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Xunit;

/// <summary>Exercises the byte-preserving phase P2 source and format detector.</summary>
public sealed class PatchScannerTests {
	/// <summary>Verifies CRLF preservation, offsets, filenames, and an incomplete final record.</summary>
	[Fact]
	public async Task UnifiedInputPreservesSourceMapAndTerminators() {
		var bytes = Encoding.UTF8.GetBytes(
			"preamble\r\n--- old.txt\r\n+++ new.txt\r\n@@ -1 +1 @@\r\n-old\r\n+new"
		);
		await WithScanAsync(
			bytes,
			async ( source, result ) => {
				Assert.Single( result.Sections );
				var section = result.Sections[0];
				Assert.Equal( PatchFormat.Unified, section.Format );
				Assert.Equal( "old.txt", section.OldFileName );
				Assert.Equal( "new.txt", section.NewFileName );
				Assert.NotNull( result.LeadingText );
				Assert.Equal( PatchLineTerminator.CarriageReturnLineFeed, result.Records[0].Terminator );
				Assert.Equal( PatchLineTerminator.None, result.Records[^1].Terminator );
				Assert.Equal( 10L, result.Records[1].Location.ByteOffset );
				Assert.Equal( 2L, result.Records[1].Location.LineNumber );
				Assert.Equal(
					"--- old.txt\r\n",
					Encoding.UTF8.GetString( await source.ReadRecordAsync( 1, includeTerminator: true ) )
				);
			}
		);
	}

	/// <summary>Verifies multiple patch sections and surrounding non-patch text.</summary>
	[Fact]
	public async Task MultipleUnifiedSectionsRetainLeadingAndTrailingText() {
		var bytes = Encoding.UTF8.GetBytes(
			string.Concat(
				"mail preamble\n",
				"--- a/one\n+++ b/one\n@@ -1 +1 @@\n-old\n+new\n",
				"--- a/two\n+++ b/two\n@@ -1 +1 @@\n-left\n+right\n",
				"mail trailer\n"
			)
		);
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.Equal( 2, result.Sections.Count );
				Assert.All( result.Sections, item => Assert.Equal( PatchFormat.Unified, item.Format ) );
				Assert.NotNull( result.LeadingText );
				Assert.NotNull( result.TrailingText );
				Assert.Equal( 1, result.LeadingText!.RecordCount );
				Assert.Equal( 1, result.TrailingText!.RecordCount );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies incomplete-line markers remain structural section records.</summary>
	[Fact]
	public async Task IncompleteLineMarkersAreRecognized() {
		var bytes = Encoding.UTF8.GetBytes(
			"--- old.txt\n+++ new.txt\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n"
		);
		await WithScanAsync(
			bytes,
			( source, result ) => {
				Assert.Single( result.Sections );
				Assert.Equal(
					2,
					source.Probes.Count( item => PatchProbeKind.NoNewlineMarker == item.Kind )
				);
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies the independent multi-file mail fixture.</summary>
	[Fact]
	public async Task IndependentMailFixtureRetainsEnvelopeAndSections() {
		var bytes = await ReadFixtureAsync( "independent/mail-multipart.patch" );
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.Equal( 2, result.Sections.Count );
				Assert.NotNull( result.LeadingText );
				Assert.NotNull( result.TrailingText );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies the binary fixture remains byte-preserved and incomplete.</summary>
	[Fact]
	public async Task BinaryFixturePreservesInvalidBytesAndIncompleteRecord() {
		var bytes = await ReadFixtureAsync( "binary/crlf-incomplete.patch" );
		await WithScanAsync(
			bytes,
			async ( source, result ) => {
				Assert.Single( result.Sections );
				Assert.Equal( PatchLineTerminator.None, result.Records[^1].Terminator );
				Assert.Equal(
					new byte[] { (byte)'+', 0xff, (byte)'n', (byte)'e', (byte)'w' },
					await source.ReadRecordAsync( result.Records.Count - 1, includeTerminator: true )
				);
			}
		);
	}

	/// <summary>Verifies context-format candidate detection.</summary>
	[Fact]
	public async Task ContextFormatIsDetected() {
		var bytes = Encoding.UTF8.GetBytes(
			"*** old.txt\n--- new.txt\n***************\n*** 1 ****\n--- 1 ----\n- old\n+ new\n"
		);
		await WithScanAsync(
			bytes,
			( _, result ) => {
				var section = Assert.Single( result.Sections );
				Assert.Equal( PatchFormat.Context, section.Format );
				Assert.Equal( "old.txt", section.OldFileName );
				Assert.Equal( "new.txt", section.NewFileName );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies required horizontal spacing in normal directives.</summary>
	[Fact]
	public async Task NormalDirectiveAllowsHorizontalSpacing() {
		var bytes = Encoding.UTF8.GetBytes( "  1 , 2 c 3 , 4\n< old\n---\n> new\n" );
		await WithScanAsync(
			bytes,
			( _, result ) => {
				var section = Assert.Single( result.Sections );
				Assert.Equal( PatchFormat.Normal, section.Format );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies GNU context-range spacing remains structurally detectable.</summary>
	[Fact]
	public async Task ContextRangeAllowsUnusualHorizontalSpacing() {
		var bytes = Encoding.UTF8.GetBytes(
			"*** old.txt\n--- new.txt\n***************\n***  1 ,\t 3  ****\n! old\n--- 1\t,\t3 ----\n! new\n"
		);
		await WithScanAsync(
			bytes,
			( _, result ) => {
				var section = Assert.Single( result.Sections );
				Assert.Equal( PatchFormat.Context, section.Format );
				Assert.Equal( 7, section.RecordCount );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies checked arithmetic for context range headers.</summary>
	[Fact]
	public async Task ContextRangeOverflowIsRejected() {
		var bytes = Encoding.ASCII.GetBytes(
			"*** old.txt\n--- new.txt\n***************\n*** 999999999999999999999999 ****\n"
		);
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "line number is too large", exception.Message );
	}

	/// <summary>Verifies patch-compatible ed-script candidate detection.</summary>
	[Fact]
	public async Task EdScriptIsDetectedWithoutInvokingEd() {
		var bytes = Encoding.UTF8.GetBytes( "2c\nreplacement\n.\n" );
		await WithScanAsync(
			bytes,
			( _, result ) => {
				var section = Assert.Single( result.Sections );
				Assert.Equal( PatchFormat.EdScript, section.Format );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies that the checked fixture families remain wired into the test output.</summary>
	/// <param name="relativePath">The fixture path below the fixture root.</param>
	/// <param name="expectedFormatValue">The expected format numeric value.</param>
	[Theory]
	[InlineData( "gnu/unified-basic.patch", 0 )]
	[InlineData( "gnu/context-basic.patch", 1 )]
	[InlineData( "gnu/normal-basic.patch", 2 )]
	[InlineData( "gnu/ed-basic.patch", 3 )]
	[InlineData( "icod-diffutils/unified-basic.patch", 0 )]
	public async Task FixtureCorpusDetectsExpectedFormat(
		string relativePath,
		int expectedFormatValue
	) {
		var bytes = await ReadFixtureAsync( relativePath );
		await WithScanAsync(
			bytes,
			( _, result ) => {
				var section = Assert.Single( result.Sections );
				Assert.Equal( (PatchFormat)expectedFormatValue, section.Format );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies that ordinary numeric prose is not mistaken for malformed patch syntax.</summary>
	[Fact]
	public async Task NumericProseRemainsOrdinaryText() {
		var bytes = Encoding.UTF8.GetBytes(
			"202608011725 this is a timestamp, not a patch directive\n"
		);
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.False( result.HasPatch );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies GNU-style file-header directives reject embedded NUL bytes.</summary>
	[Fact]
	public async Task HeaderDirectiveNulIsRejected() {
		var bytes = new byte[] {
			(byte)'-', (byte)'-', (byte)'-', (byte)' ', (byte)'a', (byte)'\n',
			(byte)'+', (byte)'+', (byte)'+', (byte)' ', (byte)'a', 0, (byte)'b', (byte)'\n'
		};
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "NUL byte", exception.Message );
		Assert.Equal( 2L, exception.Location.LineNumber );
	}

	/// <summary>Verifies that a NUL in a directive is rejected deterministically.</summary>
	[Fact]
	public async Task DirectiveNulIsRejected() {
		var bytes = new byte[] {
			(byte)'1', (byte)'c', (byte)'1', 0, (byte)'\n'
		};
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "NUL byte", exception.Message );
		Assert.Equal( 1L, exception.Location.LineNumber );
	}

	/// <summary>Verifies NUL bytes embedded before a numeric directive command are rejected.</summary>
	[Fact]
	public async Task NumericDirectiveNulBeforeCommandIsRejected() {
		var bytes = new byte[] {
			(byte)'1', 0, (byte)'c', (byte)'1', (byte)'\n'
		};
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "NUL byte", exception.Message );
	}

	/// <summary>Verifies marker-like binary prose is not mistaken for a malformed directive.</summary>
	[Fact]
	public async Task MarkerLikeBinaryProseRemainsOrdinaryText() {
		var bytes = new byte[] {
			(byte)'-', (byte)'-', (byte)'-', (byte)'n', (byte)'o', (byte)'t', 0, (byte)'x', (byte)'\n'
		};
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.False( result.HasPatch );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies a lone opening quote is not accepted as a header filename.</summary>
	[Fact]
	public async Task LoneOpeningQuoteIsRejected() {
		var bytes = Encoding.UTF8.GetBytes( "--- \"\n+++ good\n" );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "unterminated quoted filename", exception.Message );
	}

	/// <summary>Verifies numeric binary prose without a directive command remains ordinary text.</summary>
	[Fact]
	public async Task NumericBinaryProseRemainsOrdinaryText() {
		var bytes = new byte[] {
			(byte)'2', (byte)'0', (byte)'2', (byte)'6', 0, (byte)' ',
			(byte)'n', (byte)'o', (byte)'t', (byte)'e', (byte)'\n'
		};
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.False( result.HasPatch );
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies malformed fixture families fail with controlled diagnostics.</summary>
	/// <param name="relativePath">The malformed fixture path.</param>
	/// <param name="expectedMessage">The expected diagnostic fragment.</param>
	[Theory]
	[InlineData( "malformed/nul-directive.patch", "NUL byte" )]
	[InlineData( "malformed/unsafe-name.patch", "filename contains a newline" )]
	public async Task MalformedFixturesAreRejected(
		string relativePath,
		string expectedMessage
	) {
		var bytes = await ReadFixtureAsync( relativePath );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( expectedMessage, exception.Message );
	}

	/// <summary>Verifies that quoted filenames cannot encode newlines.</summary>
	[Fact]
	public async Task QuotedFilenameNewlineEscapeIsRejected() {
		var bytes = Encoding.UTF8.GetBytes( "--- \"bad\\nname\"\n+++ good\n" );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "filename contains a newline", exception.Message );
	}

	/// <summary>Verifies that NUL bytes in ordinary binary records are preserved.</summary>
	[Fact]
	public async Task OrdinaryBinaryDataIsPreservedAsNonPatchText() {
		var bytes = new byte[] { (byte)'x', 0, (byte)'y', (byte)'\n' };
		await WithScanAsync(
			bytes,
			async ( source, result ) => {
				Assert.False( result.HasPatch );
				Assert.Equal( bytes, await source.ReadRecordAsync( 0, includeTerminator: true ) );
			}
		);
	}

	/// <summary>Verifies carriage-return-only records.</summary>
	[Fact]
	public async Task CarriageReturnRecordsArePreserved() {
		var bytes = Encoding.ASCII.GetBytes( "one\rtwo\r" );
		await WithScanAsync(
			bytes,
			( _, result ) => {
				Assert.Equal( 2, result.Records.Count );
				Assert.All(
					result.Records,
					record => Assert.Equal( PatchLineTerminator.CarriageReturn, record.Terminator )
				);
				return Task.CompletedTask;
			}
		);
	}

	/// <summary>Verifies configured record limits.</summary>
	[Fact]
	public async Task OversizedRecordFailsAtConfiguredLimit() {
		var bytes = Encoding.ASCII.GetBytes( new string( 'x', 17 ) );
		var limits = new PatchScanLimits {
			MaximumBytes = 1024,
			MaximumRecords = 100,
			MaximumRecordBytes = 16
		};
		using var input = new MemoryStream( bytes );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => PatchSource.ReadAsync( input, limits )
		);
		Assert.Contains( "record exceeds", exception.Message );
	}

	/// <summary>Verifies configured record-count limits.</summary>
	[Fact]
	public async Task ExcessiveRecordCountFailsAtConfiguredLimit() {
		var limits = new PatchScanLimits {
			MaximumBytes = 1024,
			MaximumRecords = 2,
			MaximumRecordBytes = 100
		};
		using var input = new MemoryStream( Encoding.ASCII.GetBytes( "one\ntwo\nthree\n" ) );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => PatchSource.ReadAsync( input, limits )
		);
		Assert.Contains( "record limit", exception.Message );
		Assert.Equal( 3L, exception.Location.LineNumber );
	}

	/// <summary>Verifies checked arithmetic for numeric patch directives.</summary>
	[Fact]
	public async Task NumericDirectiveOverflowIsRejected() {
		var bytes = Encoding.ASCII.GetBytes( "999999999999999999999999c1\n" );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ReadAndDetectAsync( bytes )
		);
		Assert.Contains( "line number is too large", exception.Message );
	}

	/// <summary>Verifies configured total-input limits.</summary>
	[Fact]
	public async Task OversizedInputFailsAtConfiguredLimit() {
		var limits = new PatchScanLimits {
			MaximumBytes = 4,
			MaximumRecords = 100,
			MaximumRecordBytes = 100
		};
		using var input = new MemoryStream( Encoding.ASCII.GetBytes( "12345" ) );
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => PatchSource.ReadAsync( input, limits )
		);
		Assert.Contains( "byte limit", exception.Message );
	}

	/// <summary>Feeds deterministic arbitrary byte strings through the bounded scanner.</summary>
	[Fact]
	public async Task DeterministicFuzzCorpusDoesNotEscapeExpectedFailures() {
		var random = new Random( 0x50A7C2 );
		for ( var iteration = 0; iteration < 256; iteration++ ) {
			var bytes = new byte[random.Next( 0, 512 )];
			random.NextBytes( bytes );
			try {
				await ReadAndDetectAsync( bytes );
			} catch ( PatchInputException ) {
				// Malformed directive-like random data is an expected controlled result.
			}
		}
	}

	/// <summary>Verifies temporary spool cleanup after successful disposal.</summary>
	[Fact]
	public async Task DisposedSourceCleansTemporarySpool() {
		using var input = new MemoryStream( Encoding.UTF8.GetBytes( "plain text\n" ) );
		string temporaryPath;
		await using ( var source = await PatchSource.ReadAsync( input ) ) {
			temporaryPath = source.TemporaryPath;
			Assert.Single( source.Records );
			Assert.True( File.Exists( temporaryPath ) );
		}
		Assert.False( File.Exists( temporaryPath ) );
	}


	private static Task<byte[]> ReadFixtureAsync( string relativePath ) {
		var path = System.IO.Path.Combine(
			AppContext.BaseDirectory,
			"fixtures",
			relativePath.Replace( '/', System.IO.Path.DirectorySeparatorChar )
		);
		return File.ReadAllBytesAsync( path );
	}

	private static async Task ReadAndDetectAsync( byte[] bytes ) {
		using var input = new MemoryStream( bytes );
		await using var source = await PatchSource.ReadAsync( input );
		_ = PatchScanner.Detect( source.Records, source.Probes );
	}

	private static async Task WithScanAsync(
		byte[] bytes,
		Func<PatchSource, PatchScanResult, Task> assertion
	) {
		using var input = new MemoryStream( bytes );
		await using var source = await PatchSource.ReadAsync( input );
		var result = PatchScanner.Detect( source.Records, source.Probes );
		await assertion( source, result );
	}
}
