namespace Icod.Patch.Tests;

using System.Text;
using Icod.CommandFramework.Diagnostics;
using System.IO;
using Xunit;

/// <summary>Exercises the final public command boundary and historical compatibility contracts.</summary>
public sealed class CommandTests {
	/// <summary>Verifies help and version short-circuit source acquisition.</summary>
	/// <param name="option">The standard information option.</param>
	/// <param name="expected">Text expected on standard output.</param>
	[Theory]
	[InlineData( "--help", "Usage: patch" )]
	[InlineData( "--version", "patch (Icod.Patch)" )]
	public async Task HelpAndVersionSucceed( string option, string expected ) {
		var result = await RunAsync( new[] { option } );
		Assert.Equal( 0, result.Status );
		Assert.Contains( expected, result.Output );
		Assert.Empty( result.Error );
	}

	/// <summary>Verifies that a second patch-source selector is rejected.</summary>
	[Fact]
	public async Task InputOptionConflictsWithPatchOperand() {
		var result = await RunAsync( new[] { "-i", "one.patch", "target", "two.patch" } );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "specified by both -i and an operand", result.Error );
		Assert.Contains( "Try 'patch --help'", result.Error );
	}

	/// <summary>Verifies that the input option requires its patch-file value.</summary>
	[Fact]
	public async Task InputOptionRequiresValue() {
		var result = await RunAsync( new[] { "-i" } );
		Assert.Equal( 2, result.Status );
		Assert.True( result.Error.Contains( "require", StringComparison.OrdinalIgnoreCase ) );
		Assert.Contains( "Try 'patch --help'", result.Error );
	}

	/// <summary>Verifies GNU's long-only binary option spelling.</summary>
	[Fact]
	public async Task BinaryLongOptionIsAccepted() {
		var bytes = Encoding.UTF8.GetBytes(
			"--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		var result = await RunAsync( new[] { "--binary" }, bytes );
		Assert.Equal( 1, result.Status );
		Assert.Contains( "no usable file name", result.Error );
	}

	/// <summary>Verifies that GNU's short backup option is accepted as backup policy.</summary>
	[Fact]
	public async Task BackupShortOptionIsAcceptedAsBackupPolicy() {
		var result = await RunAsync( new[] { "-b" } );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "Only garbage was found", result.Error );
		Assert.DoesNotContain( "reserved for a later", result.Error, StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Verifies abbreviations are resolved against the complete GNU 2.8 option inventory.</summary>
	[Fact]
	public async Task VersionAndVerboseAbbreviationIsAmbiguous() {
		var result = await RunAsync( new[] { "--ver" } );
		Assert.Equal( 2, result.Status );
		Assert.True( result.Error.Contains( "ambiguous", StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Verifies an unambiguous source-option abbreviation.</summary>
	[Fact]
	public async Task InputLongOptionMayBeUnambiguouslyAbbreviated() {
		var path = await WriteTemporaryAsync(
			Encoding.UTF8.GetBytes( "1c1\n< old\n---\n> new\n" )
		);
		try {
			var result = await RunAsync( new[] { "--inp", path } );
			Assert.Equal( 1, result.Status );
			Assert.Contains( "no usable file name", result.Error );
		} finally {
			File.Delete( path );
		}
	}

	/// <summary>Verifies the synchronous compatibility wrapper.</summary>
	[Fact]
	public void SynchronousCompatibilityWrapperRemainsAvailable() {
		using var input = new StringReader(
			"--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var status = Command.Run( Array.Empty<string>(), input, output, error );
		Assert.Equal( 1, status );
		Assert.Contains( "no usable file name", error.ToString() );
	}

	/// <summary>Verifies that excess operands are diagnosed.</summary>
	[Fact]
	public async Task ExtraOperandIsRejected() {
		var result = await RunAsync( new[] { "target", "one.patch", "extra" } );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "extra operand 'extra'", result.Error );
	}

	/// <summary>Verifies that the patch stream defaults to binary standard input.</summary>
	[Fact]
	public async Task StandardInputIsDefaultPatchSource() {
		var bytes = Encoding.UTF8.GetBytes(
			"--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		var result = await RunAsync( Array.Empty<string>(), bytes );
		Assert.Equal( 1, result.Status );
		Assert.Contains( "no usable file name", result.Error );
	}

	/// <summary>Verifies an original-file operand may accompany a patch stream on standard input.</summary>
	[Fact]
	public async Task OriginalFileOperandMayUseStandardInputPatchSource() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target" );
		await File.WriteAllTextAsync( target, "old\n" );
		var bytes = Encoding.UTF8.GetBytes(
			"--- target\n+++ target\n@@ -1 +1 @@\n-old\n+new\n"
		);
		try {
			var result = await RunAsync( new[] { "-d", directory, "target" }, bytes );
			Assert.Equal( 0, result.Status );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies that the input option selects a file source.</summary>
	[Fact]
	public async Task InputOptionSelectsPatchFile() {
		var path = await WriteTemporaryAsync(
			Encoding.UTF8.GetBytes( "1c1\n< old\n---\n> new\n" )
		);
		try {
			var result = await RunAsync( new[] { "-i", path } );
			Assert.Equal( 1, result.Status );
			Assert.Contains( "no usable file name", result.Error );
		} finally {
			File.Delete( path );
		}
	}

	/// <summary>Characterizes retirement of the former private plus/minus format.</summary>
	[Fact]
	public async Task HistoricalPrivateFormatIsRejectedAsGarbage() {
		var bytes = Encoding.UTF8.GetBytes( "-old\n+new\n" );
		var result = await RunAsync( Array.Empty<string>(), bytes );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "Only garbage was found", result.Error );
	}

	/// <summary>Verifies the final command commits a successfully applied target artifact.</summary>
	[Fact]
	public async Task RecognizedPatchMutatesTarget() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target" );
		var patch = System.IO.Path.Combine( directory, "change.patch" );
		await File.WriteAllTextAsync( target, "old\n" );
		await File.WriteAllTextAsync(
			patch,
			"--- target\n+++ target\n@@ -1 +1 @@\n-old\n+new\n"
		);
		try {
			var result = await RunAsync( new[] { "-d", directory, "target", "change.patch" } );
			Assert.Equal( 0, result.Status );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
			Assert.False( File.Exists( string.Concat( target, ".orig" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies each implemented format-selection option reaches its parser.</summary>
	/// <param name="option">The GNU format-selection option.</param>
	/// <param name="patchText">A patch in the selected format.</param>
	[Theory]
	[InlineData( "-u", "--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n" )]
	[InlineData( "-c", "*** old\n--- new\n***************\n*** 1 ****\n- old\n--- 1 ----\n+ new\n" )]
	[InlineData( "-n", "1c1\n< old\n---\n> new\n" )]
	[InlineData( "-e", "1c\nnew\n.\n" )]
	public async Task ExplicitFormatOptionsAreAccepted( string option, string patchText ) {
		var result = await RunAsync( new[] { option }, Encoding.UTF8.GetBytes( patchText ) );
		Assert.Equal( 1, result.Status );
		Assert.DoesNotContain( "Only garbage was found", result.Error );
	}

	/// <summary>Verifies mutually exclusive format-selection options are diagnosed.</summary>
	[Fact]
	public async Task MultipleFormatOptionsAreRejected() {
		var result = await RunAsync( new[] { "-u", "-c" } );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "only one patch input format", result.Error );
		Assert.Contains( "Try 'patch --help'", result.Error );
	}

	/// <summary>Verifies a forced format does not silently fall back to autodetection.</summary>
	[Fact]
	public async Task ForcedFormatDoesNotFallBackToAnotherSyntax() {
		var result = await RunAsync(
			new[] { "-u" },
			Encoding.UTF8.GetBytes( "1c1\n< old\n---\n> new\n" )
		);
		Assert.Equal( 2, result.Status );
		Assert.Contains( "Only garbage was found", result.Error );
	}

	/// <summary>Verifies the repository-wide cancellation status.</summary>
	[Fact]
	public async Task CancellationReturnsCanceledStatus() {
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var result = await RunAsync(
			Array.Empty<string>(),
			Encoding.UTF8.GetBytes( "--- a\n+++ b\n" ),
			cancellation.Token
		);
		Assert.Equal( CommandExitCodes.Canceled, result.Status );
	}

	/// <summary>Verifies that status accumulation retains the most severe status.</summary>
	[Fact]
	public void ExitStatusAccumulatorRetainsSeverity() {
		var accumulator = new PatchExitStatusAccumulator();
		accumulator.Add( PatchExitStatus.PartialFailure );
		accumulator.Add( PatchExitStatus.Success );
		accumulator.Add( PatchExitStatus.Trouble );
		Assert.Equal( PatchExitStatus.Trouble, accumulator.Status );
	}

	private static async Task<(int Status, string Output, string Error)> RunAsync(
		IReadOnlyList<string> arguments,
		byte[]? stdinBytes = null,
		CancellationToken cancellationToken = default
	) {
		using var input = new MemoryStream( stdinBytes ?? Array.Empty<byte>() );
		using var output = new StringWriter();
		using var error = new StringWriter();
		var status = await Command.RunAsync(
			arguments,
			TextReader.Null,
			output,
			error,
			cancellationToken,
			input
		);
		return ( status, output.ToString(), error.ToString() );
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "patch-test-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}

	private static async Task<string> WriteTemporaryAsync( byte[] bytes ) {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "patch-test-", Guid.NewGuid().ToString( "N" ) )
		);
		await File.WriteAllBytesAsync( path, bytes );
		return path;
	}
}
