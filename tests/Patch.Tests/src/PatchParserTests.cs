namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Xunit;

/// <summary>Exercises the pure Wave A parsers for all GNU patch input syntaxes.</summary>
public sealed class PatchParserTests {
	/// <summary>Verifies unified headers, section text, multiple hunks, and normalized operations.</summary>
	[Fact]
	public async Task UnifiedParserPreservesHeadersAndMultipleHunks() {
		var document = await ParseFixtureAsync( "gnu/unified-multiple.patch" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFormat.Unified, file.Format );
		Assert.Equal( PatchFileChangeKind.Modify, file.ChangeKind );
		var oldHeader = Assert.IsType<PatchFileHeader>( file.OldHeader );
		var newHeader = Assert.IsType<PatchFileHeader>( file.NewHeader );
		Assert.Equal( "old.txt", oldHeader.Name );
		Assert.NotNull( oldHeader.TimestampText );
		Assert.Contains( "2026-08-01", oldHeader.TimestampText! );
		Assert.Equal( "new.txt", newHeader.Name );
		Assert.Equal( 2, file.Hunks.Count );
		Assert.Equal( "first function", file.Hunks[0].SectionText );
		Assert.Equal( PatchOperationKind.Change, file.Hunks[0].Operation );
		Assert.Equal( 2L, file.Hunks[0].OldRange.Count );
		Assert.Equal( "old", Text( file.Hunks[0].OldLines[1] ) );
		Assert.Equal( "new", Text( file.Hunks[0].NewLines[1] ) );
		Assert.Equal( PatchOperationKind.Add, file.Hunks[1].Operation );
		Assert.Equal( 0L, file.Hunks[1].OldRange.Count );
		Assert.Equal( 2, file.Hunks[1].NewLines.Count );
	}

	/// <summary>Verifies incomplete final lines are represented on the correct unified sides.</summary>
	[Fact]
	public async Task UnifiedNoNewlineMarkersUpdateRepresentedTerminators() {
		var document = await ParseTextAsync(
			"--- old.txt\n+++ new.txt\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n"
		);
		var hunk = Assert.Single( Assert.Single( document.Files ).Hunks );
		Assert.Equal( PatchLineTerminator.None, Assert.Single( hunk.OldLines ).Terminator );
		Assert.Equal( PatchLineTerminator.None, Assert.Single( hunk.NewLines ).Terminator );
		Assert.Equal( 5, hunk.RawRecords.Count );
	}

	/// <summary>Verifies null-device headers establish a file-creation operation.</summary>
	[Fact]
	public async Task UnifiedNullDeviceHeaderRepresentsCreation() {
		var document = await ParseTextAsync(
			"--- /dev/null\n+++ created.txt\n@@ -0,0 +1,1 @@\n+created\n"
		);
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFileChangeKind.Create, file.ChangeKind );
		Assert.Equal( PatchOperationKind.Add, Assert.Single( file.Hunks ).Operation );
	}

	/// <summary>Verifies a null new-file header establishes a file-deletion operation.</summary>
	[Fact]
	public async Task UnifiedNullDeviceNewHeaderRepresentsDeletion() {
		var document = await ParseTextAsync(
			"--- removed.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-removed\n"
		);
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFileChangeKind.Delete, file.ChangeKind );
		Assert.Equal( PatchOperationKind.Delete, Assert.Single( file.Hunks ).Operation );
	}

	/// <summary>Verifies header-looking hunk data does not create false file sections.</summary>
	[Fact]
	public async Task UnifiedHeaderLookingDataRemainsInsideItsHunk() {
		var document = await ParseTextAsync(
			string.Concat(
				"--- first.old\n+++ first.new\n@@ -1,2 +1,2 @@\n",
				"--- old-looking data\n+++ new-looking data\n same\n",
				"--- second.old\n+++ second.new\n@@ -1 +1 @@\n-left\n+right\n"
			)
		);
		Assert.Equal( 2, document.Files.Count );
		var firstHunk = Assert.Single( document.Files[0].Hunks );
		Assert.Equal( "-- old-looking data", Text( firstHunk.OldLines[0] ) );
		Assert.Equal( "++ new-looking data", Text( firstHunk.NewLines[0] ) );
	}

	/// <summary>Verifies empty unified hunks are rejected.</summary>
	[Fact]
	public async Task UnifiedHunkMustChangeAtLeastOneLine() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseTextAsync( "--- old\n+++ new\n@@ -1,0 +1,0 @@\n" )
		);
		Assert.Contains( "changes no lines", exception.Message );
	}

	/// <summary>Verifies a context-only unified hunk is rejected as a no-op.</summary>
	[Fact]
	public async Task UnifiedHunkMustContainAChangeMarker() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseTextAsync( "--- old\n+++ new\n@@ -1 +1 @@\n same\n" )
		);
		Assert.Contains( "contains no changes", exception.Message );
	}

	/// <summary>Verifies unified declared line counts are checked.</summary>
	[Fact]
	public async Task UnifiedCountMismatchIsRejected() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseFixtureAsync( "malformed/unified-count.patch" )
		);
		Assert.Contains( "count does not match", exception.Message );
	}

	/// <summary>Verifies context change markers and duplicated context lines are normalized.</summary>
	[Fact]
	public async Task ContextParserNormalizesOldAndNewBlocks() {
		var document = await ParseFixtureAsync( "icod-diffutils/context-basic.patch" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFormat.Context, file.Format );
		var hunk = Assert.Single( file.Hunks );
		Assert.Equal( 2, hunk.OldLines.Count );
		Assert.Equal( 2, hunk.NewLines.Count );
		Assert.True( hunk.OldLines[0].IsContext );
		Assert.True( hunk.NewLines[0].IsContext );
		Assert.Equal( "same", Text( hunk.OldLines[0] ) );
		Assert.Equal( "left", Text( hunk.OldLines[1] ) );
		Assert.Equal( "right", Text( hunk.NewLines[1] ) );
	}

	/// <summary>Verifies GNU's zero old range for a context-format creation.</summary>
	[Fact]
	public async Task ContextZeroRangeRepresentsCreation() {
		var document = await ParseFixtureAsync( "gnu/context-create.patch" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFileChangeKind.Create, file.ChangeKind );
		var hunk = Assert.Single( file.Hunks );
		Assert.Equal( 0L, hunk.OldRange.Count );
		Assert.Equal( 2L, hunk.NewRange!.Value.Count );
		Assert.Equal( PatchOperationKind.Add, hunk.Operation );
	}

	/// <summary>Verifies context format represents deletion through the null new-file header.</summary>
	[Fact]
	public async Task ContextNullDeviceNewHeaderRepresentsDeletion() {
		var document = await ParseTextAsync(
			"*** removed.txt\n--- /dev/null\n***************\n*** 1 ****\n- removed\n--- 0 ----\n"
		);
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFileChangeKind.Delete, file.ChangeKind );
		Assert.Equal( PatchOperationKind.Delete, Assert.Single( file.Hunks ).Operation );
	}

	/// <summary>Verifies context copies must agree exactly.</summary>
	[Fact]
	public async Task ContextCopiesMustMatch() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseTextAsync(
				"*** old.txt\n--- new.txt\n***************\n*** 1 ****\n  old\n--- 1 ----\n  different\n"
			)
		);
		Assert.Contains( "copies do not match", exception.Message );
	}

	/// <summary>Verifies context declared line counts are checked.</summary>
	[Fact]
	public async Task ContextCountMismatchIsRejected() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseFixtureAsync( "malformed/context-count.patch" )
		);
		Assert.Contains( "count does not match", exception.Message );
	}

	/// <summary>Verifies normal append, change, and delete commands and their data blocks.</summary>
	[Fact]
	public async Task NormalParserSupportsAllThreeOperations() {
		var document = await ParseFixtureAsync( "gnu/normal-operations.patch" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFormat.Normal, file.Format );
		Assert.Equal( PatchFileChangeKind.Unspecified, file.ChangeKind );
		Assert.Equal( 3, file.Hunks.Count );
		Assert.Equal( PatchOperationKind.Add, file.Hunks[0].Operation );
		Assert.Equal( 0L, file.Hunks[0].OldRange.Count );
		Assert.Equal( new[] { "first", "second" }, file.Hunks[0].NewLines.Select( Text ).ToArray() );
		Assert.Equal( PatchOperationKind.Change, file.Hunks[1].Operation );
		Assert.Equal( "old", Text( Assert.Single( file.Hunks[1].OldLines ) ) );
		Assert.Equal( "new", Text( Assert.Single( file.Hunks[1].NewLines ) ) );
		Assert.Equal( PatchOperationKind.Delete, file.Hunks[2].Operation );
		Assert.Equal( 0L, file.Hunks[2].NewRange!.Value.Count );
	}

	/// <summary>Verifies normal-format incomplete-line markers attach to data lines.</summary>
	[Fact]
	public async Task NormalNoNewlineMarkerIsPreserved() {
		var document = await ParseTextAsync(
			"1c1\n< old\n\\ No newline at end of file\n---\n> new\n\\ No newline at end of file\n"
		);
		var hunk = Assert.Single( Assert.Single( document.Files ).Hunks );
		Assert.Equal( PatchLineTerminator.None, Assert.Single( hunk.OldLines ).Terminator );
		Assert.Equal( PatchLineTerminator.None, Assert.Single( hunk.NewLines ).Terminator );
	}

	/// <summary>Verifies normal data-block counts are checked.</summary>
	[Fact]
	public async Task NormalCountMismatchIsRejected() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseFixtureAsync( "malformed/normal-count.patch" )
		);
		Assert.Contains( "malformed normal-diff data line", exception.Message );
	}

	/// <summary>Verifies GNU Diffutils' protected single-dot sequence is folded internally.</summary>
	[Fact]
	public async Task EdParserFoldsGnuDotProtectionWithoutInvokingEd() {
		var document = await ParseFixtureAsync( "gnu/ed-dot.patch" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFormat.EdScript, file.Format );
		Assert.Equal( PatchFileChangeKind.Unspecified, file.ChangeKind );
		Assert.Equal( 3, file.Hunks.Count );
		Assert.Equal( 3L, file.Hunks[0].OldRange.Start );
		Assert.Equal( "z", Text( Assert.Single( file.Hunks[0].NewLines ) ) );
		Assert.Equal( PatchOperationKind.Change, file.Hunks[1].Operation );
		Assert.Equal( new[] { ".", "y" }, file.Hunks[1].NewLines.Select( Text ).ToArray() );
		Assert.Equal( PatchOperationKind.Add, file.Hunks[2].Operation );
		Assert.Equal( 0L, file.Hunks[2].OldRange.Start );
	}

	/// <summary>Verifies ed text that resembles normal-diff data does not alter autodetection.</summary>
	[Fact]
	public async Task EdTextMayBeginWithNormalDiffMarkers() {
		var document = await ParseTextAsync( "1c\n< old-looking\n> new-looking\n.\n" );
		var file = Assert.Single( document.Files );
		Assert.Equal( PatchFormat.EdScript, file.Format );
		var hunk = Assert.Single( file.Hunks );
		Assert.Equal( new[] { "< old-looking", "> new-looking" }, hunk.NewLines.Select( Text ).ToArray() );
	}

	/// <summary>Verifies a complete header-looking pair remains data inside an ed text block.</summary>
	[Fact]
	public async Task EdHeaderLookingTextRemainsInsideItsTextBlock() {
		var document = await ParseTextAsync(
			string.Concat(
				"1c\n--- old-looking\n+++ new-looking\n.\n",
				"--- actual.old\n+++ actual.new\n@@ -1 +1 @@\n-left\n+right\n"
			)
		);
		Assert.Equal( 2, document.Files.Count );
		var edFile = document.Files[0];
		Assert.Equal( PatchFormat.EdScript, edFile.Format );
		Assert.Equal(
			new[] { "--- old-looking", "+++ new-looking" },
			Assert.Single( edFile.Hunks ).NewLines.Select( Text ).ToArray()
		);
		Assert.Equal( PatchFormat.Unified, document.Files[1].Format );
	}

	/// <summary>Verifies ed delete commands require no text block.</summary>
	[Fact]
	public async Task EdDeleteRangeIsParsed() {
		var document = await ParseTextAsync( "4,6d\n", PatchFormat.EdScript );
		var hunk = Assert.Single( Assert.Single( document.Files ).Hunks );
		Assert.Equal( PatchOperationKind.Delete, hunk.Operation );
		Assert.Equal( 4L, hunk.OldRange.Start );
		Assert.Equal( 3L, hunk.OldRange.Count );
		Assert.Empty( hunk.NewLines );
	}

	/// <summary>Verifies ed commands must be ordered from later addresses to earlier addresses.</summary>
	[Fact]
	public async Task EdCommandsMustUseReverseAddressOrder() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseTextAsync( "1d\n2d\n", PatchFormat.EdScript )
		);
		Assert.Contains( "reverse address order", exception.Message );
	}

	/// <summary>Verifies unterminated ed text blocks are rejected.</summary>
	[Fact]
	public async Task EdTextBlockMustBeTerminated() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseFixtureAsync( "malformed/ed-unterminated.patch", PatchFormat.EdScript )
		);
		Assert.Contains( "missing ed text-block delimiter", exception.Message );
	}

	/// <summary>Verifies a mail envelope and signature remain outside two parsed file patches.</summary>
	[Fact]
	public async Task MailMultipartFixturePreservesEnvelopeAndTrailer() {
		var document = await ParseFixtureAsync( "independent/mail-multipart.patch" );
		Assert.Equal( 2, document.Files.Count );
		Assert.NotNull( document.LeadingText );
		Assert.Equal( 3, document.LeadingText!.RecordCount );
		Assert.Empty( document.InterstitialText );
		Assert.NotNull( document.TrailingText );
		Assert.Equal( 2, document.TrailingText!.RecordCount );
	}

	/// <summary>Verifies producer text between normal-format file patches is retained.</summary>
	[Fact]
	public async Task InterstitialTextBetweenNormalSectionsIsRetained() {
		var document = await ParseTextAsync(
			"1c1\n< old one\n---\n> new one\nOnly in directory: another.txt\n1c1\n< old two\n---\n> new two\n"
		);
		Assert.Equal( 2, document.Files.Count );
		var region = Assert.Single( document.InterstitialText );
		Assert.Equal( 1, region.RecordCount );
	}

	/// <summary>Verifies retained raw records can reproduce a parsed reject hunk.</summary>
	[Fact]
	public async Task ParsedHunkRetainsExactRawRecords() {
		var document = await ParseFixtureAsync( "gnu/normal-basic.patch" );
		var hunk = Assert.Single( Assert.Single( document.Files ).Hunks );
		Assert.Equal( 4, hunk.RawRecords.Count );
		Assert.Equal( "1c1", RawText( hunk.RawRecords[0] ) );
		Assert.Equal( "< old", RawText( hunk.RawRecords[1] ) );
		Assert.Equal( "> new", RawText( hunk.RawRecords[3] ) );
	}

	/// <summary>Verifies each producer-separated fixture family reaches the complete parser.</summary>
	/// <param name="relativePath">The fixture path.</param>
	/// <param name="formatValue">The expected format numeric value.</param>
	[Theory]
	[InlineData( "gnu/unified-basic.patch", 0 )]
	[InlineData( "gnu/context-basic.patch", 1 )]
	[InlineData( "gnu/normal-basic.patch", 2 )]
	[InlineData( "gnu/ed-basic.patch", 3 )]
	[InlineData( "icod-diffutils/unified-basic.patch", 0 )]
	[InlineData( "icod-diffutils/context-basic.patch", 1 )]
	[InlineData( "icod-diffutils/normal-basic.patch", 2 )]
	[InlineData( "icod-diffutils/ed-basic.patch", 3 )]
	[InlineData( "independent/git-unified.patch", 0 )]
	public async Task FixtureCorpusParsesExpectedFormat( string relativePath, int formatValue ) {
		var document = await ParseFixtureAsync( relativePath );
		Assert.Equal( (PatchFormat)formatValue, Assert.Single( document.Files ).Format );
	}

	/// <summary>Verifies invalid UTF-8 and incomplete records remain byte-exact through parsing.</summary>
	[Fact]
	public async Task BinaryUnifiedFixtureRemainsByteExact() {
		var document = await ParseFixtureAsync( "binary/crlf-incomplete.patch" );
		var hunk = Assert.Single( Assert.Single( document.Files ).Hunks );
		Assert.Equal( new byte[] { 0, (byte)'o', (byte)'l', (byte)'d' }, Assert.Single( hunk.OldLines ).Content.ToArray() );
		Assert.Equal( new byte[] { 0xff, (byte)'n', (byte)'e', (byte)'w' }, Assert.Single( hunk.NewLines ).Content.ToArray() );
		Assert.Equal( PatchLineTerminator.None, Assert.Single( hunk.NewLines ).Terminator );
	}

	/// <summary>Verifies parser model limits fail deterministically.</summary>
	[Fact]
	public async Task HunkLimitIsEnforced() {
		var exception = await Assert.ThrowsAsync<PatchInputException>(
			() => ParseTextAsync(
				"--- old\n+++ new\n@@ -1 +1 @@\n-a\n+b\n@@ -2 +2 @@\n-c\n+d\n",
				null,
				new PatchParseLimits {
					MaximumFiles = 10,
					MaximumHunks = 1,
					MaximumDataLines = 100,
					MaximumMaterializedBytes = 1024 * 1024
				}
			)
		);
		Assert.Contains( "too many hunks", exception.Message );
	}

	private static string Text( PatchDataLine line ) {
		return Encoding.UTF8.GetString( line.Content.Span );
	}

	private static string RawText( PatchRawRecord record ) {
		return Encoding.UTF8.GetString( record.Content.Span );
	}

	private static async Task<PatchDocument> ParseFixtureAsync(
		string relativePath,
		PatchFormat? forcedFormat = null
	) {
		var path = System.IO.Path.Combine(
			AppContext.BaseDirectory,
			"fixtures",
			relativePath.Replace( '/', System.IO.Path.DirectorySeparatorChar )
		);
		return await ParseBytesAsync( await File.ReadAllBytesAsync( path ), forcedFormat );
	}

	private static Task<PatchDocument> ParseTextAsync(
		string text,
		PatchFormat? forcedFormat = null,
		PatchParseLimits? limits = null
	) {
		return ParseBytesAsync( Encoding.UTF8.GetBytes( text ), forcedFormat, limits );
	}

	private static async Task<PatchDocument> ParseBytesAsync(
		byte[] bytes,
		PatchFormat? forcedFormat = null,
		PatchParseLimits? limits = null
	) {
		using var input = new MemoryStream( bytes );
		await using var source = await PatchSource.ReadAsync( input );
		var scan = PatchScanner.Detect( source.Records, source.Probes, forcedFormat );
		Assert.True( scan.HasPatch );
		return await PatchDocumentParser.ParseAsync( source, scan, limits );
	}
}
