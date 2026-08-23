namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Xunit;

/// <summary>Exercises Phase P8 artifacts, user-facing policy, and command statuses.</summary>
public sealed class WaveCCommandTests {
	/// <summary>Verifies <c>-b</c> retains the original target contents.</summary>
	[Fact]
	public async Task BackupRetainsOriginalTarget() {
		var directory = await CreatePatchDirectoryAsync();
		try {
			var result = await RunAsync( directory, new[] { "-b", "target.txt", "change.patch" } );
			Assert.Equal( 0, result.Status );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt.orig" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies numbered backup policy selects the first available version.</summary>
	[Fact]
	public async Task NumberedBackupSelectsFirstAvailableVersion() {
		var directory = await CreatePatchDirectoryAsync();
		try {
			var result = await RunAsync(
				directory,
				new[] { "-b", "-V", "numbered", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt.~1~" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a full-name prefix uses simple naming and honors an explicit suffix.</summary>
	[Fact]
	public async Task BackupPrefixHonorsExplicitSuffix() {
		var directory = await CreatePatchDirectoryAsync();
		Directory.CreateDirectory( System.IO.Path.Combine( directory, "backups" ) );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-b", "-B", string.Concat( "backups", System.IO.Path.DirectorySeparatorChar ), "-z", ".ignored", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "backups", "target.txt.ignored" ) ) );
			Assert.False( File.Exists( System.IO.Path.Combine( directory, "backups", "target.txt" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies <c>-o</c> leaves both the input and prior output unbacked.</summary>
	[Fact]
	public async Task OutputModeDoesNotBackUpInputOrOutput() {
		var directory = await CreatePatchDirectoryAsync();
		var outputPath = System.IO.Path.Combine( directory, "result.txt" );
		await File.WriteAllTextAsync( outputPath, "previous\n" );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-b", "-o", "result.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( outputPath ) );
			Assert.False( File.Exists( string.Concat( outputPath, ".orig" ) ) );
			Assert.False( File.Exists( System.IO.Path.Combine( directory, "target.txt.orig" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies dry-run planning leaves all artifact destinations untouched.</summary>
	[Fact]
	public async Task DryRunDoesNotMutateArtifacts() {
		var directory = await CreatePatchDirectoryAsync();
		try {
			var result = await RunAsync(
				directory,
				new[] { "--dry-run", "-b", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
			Assert.False( File.Exists( System.IO.Path.Combine( directory, "target.txt.orig" ) ) );
			Assert.Contains( "dry run", result.Error, StringComparison.OrdinalIgnoreCase );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies failed hunks are written as unified rejects by default for unified input.</summary>
	[Fact]
	public async Task FailedHunkCreatesUnifiedReject() {
		var directory = await CreatePatchDirectoryAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-missing\n+new\n"
		);
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var originalTime = new DateTimeOffset( 2002, 3, 4, 5, 6, 7, TimeSpan.Zero );
		File.SetLastWriteTimeUtc( target, originalTime.UtcDateTime );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-f", "target.txt", "change.patch" }
			);
			Assert.Equal( 1, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			Assert.Equal( originalTime.UtcDateTime, File.GetLastWriteTimeUtc( target ) );
			var reject = await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt.rej" ) );
			Assert.StartsWith( "--- ", reject, StringComparison.Ordinal );
			Assert.Contains( "@@ -1 +1 @@", reject, StringComparison.Ordinal );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies rejects from explicit reverse application are serialized in reverse direction.</summary>
	[Fact]
	public async Task ReverseRejectSwapsHunkSides() {
		var directory = await CreatePatchDirectoryAsync();
		await File.WriteAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ), "other\n" );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-R", "-f", "target.txt", "change.patch" }
			);
			Assert.Equal( 1, result.Status );
			var reject = await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt.rej" ) );
			Assert.Contains( "-new\n+old\n", reject, StringComparison.Ordinal );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies <c>-r -</c> discards rejected hunks without creating a pathname named dash.</summary>
	[Fact]
	public async Task RejectDashDiscardsRejects() {
		var directory = await CreatePatchDirectoryAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-missing\n+new\n"
		);
		try {
			var result = await RunAsync(
				directory,
				new[] { "-f", "-r", "-", "target.txt", "change.patch" }
			);
			Assert.Equal( 1, result.Status );
			Assert.False( File.Exists( System.IO.Path.Combine( directory, "-" ) ) );
			Assert.False( File.Exists( System.IO.Path.Combine( directory, "target.txt.rej" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a forward-only skipped patch neither rewrites nor backs up its target.</summary>
	[Fact]
	public async Task ForwardOnlySkipDoesNotRewriteOrBackUpTarget() {
		var directory = await CreatePatchDirectoryAsync();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "new\n" );
		var originalTime = new DateTimeOffset( 2003, 4, 5, 6, 7, 8, TimeSpan.Zero );
		File.SetLastWriteTimeUtc( target, originalTime.UtcDateTime );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-N", "-b", "target.txt", "change.patch" }
			);
			Assert.Equal( 1, result.Status );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
			Assert.Equal( originalTime.UtcDateTime, File.GetLastWriteTimeUtc( target ) );
			Assert.False( File.Exists( string.Concat( target, ".orig" ) ) );
			Assert.True( File.Exists( string.Concat( target, ".rej" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a forward-only skipped patch creates an empty alternate output.</summary>
	[Fact]
	public async Task ForwardOnlySkipLeavesAlternateOutputEmpty() {
		var directory = await CreatePatchDirectoryAsync();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var output = System.IO.Path.Combine( directory, "result.txt" );
		await File.WriteAllTextAsync( target, "new\n" );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-N", "-o", "result.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 1, result.Status );
			Assert.Empty( await File.ReadAllBytesAsync( output ) );
			Assert.True( File.Exists( string.Concat( output, ".rej" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies <c>-o -</c> writes byte-oriented output without modifying the input.</summary>
	[Fact]
	public async Task OutputDashWritesBinaryStandardOutput() {
		var directory = await CreatePatchDirectoryAsync();
		await using var binaryOutput = new MemoryStream();
		try {
			var result = await RunAsync(
				directory,
				new[] { "-o", "-", "target.txt", "change.patch" },
				binaryOutput: binaryOutput
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "new\n", Encoding.UTF8.GetString( binaryOutput.ToArray() ) );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a broken output stream reports trouble before committing filesystem artifacts.</summary>
	[Fact]
	public async Task BrokenStandardOutputReportsTrouble() {
		var directory = await CreatePatchDirectoryAsync();
		await using var binaryOutput = new BrokenWriteStream();
		try {
			var result = await RunAsync(
				directory,
				new[] { "-o", "-", "target.txt", "change.patch" },
				binaryOutput: binaryOutput
			);
			Assert.Equal( 2, result.Status );
			Assert.Contains( "broken output", result.Error, StringComparison.OrdinalIgnoreCase );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies an affirmative reversal prompt is consumed from command standard input.</summary>
	[Fact]
	public async Task InteractiveReversePromptMayBeAccepted() {
		var directory = await CreatePatchDirectoryAsync();
		await File.WriteAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ), "new\n" );
		try {
			var result = await RunAsync(
				directory,
				new[] { "target.txt", "change.patch" },
				promptInput: "yes\n"
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) ) );
			Assert.Contains( "Assume -R?", result.Error, StringComparison.Ordinal );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies environment backup and quoting policy precedence.</summary>
	[Fact]
	public void EnvironmentPolicyMapsWithoutLoss() {
		var parsed = Command.CreateParser().Parse( Array.Empty<string>() );
		Assert.True( parsed.IsSuccess );
		var options = Command.CreateOptions(
			parsed,
			name => name switch {
				"PATCH_VERSION_CONTROL" => "numbered",
				"VERSION_CONTROL" => "simple",
				"QUOTING_STYLE" => "c",
				_ => null
			}
		);
		Assert.Equal( PatchBackupVersionControl.Numbered, options.BackupVersionControl );
		Assert.Equal( PatchQuotingStyle.C, options.QuotingStyle );
	}

	/// <summary>Verifies hostile filename bytes are rendered deterministically.</summary>
	[Fact]
	public void FileNameQuotingEscapesControlCharacters() {
		Assert.Equal( "name\\npart", PatchFileNameQuoter.Quote( "name\npart", PatchQuotingStyle.Escape ) );
		Assert.Equal( "'two words'", PatchFileNameQuoter.Quote( "two words", PatchQuotingStyle.Shell ) );
		Assert.Equal( "a:b", PatchFileNameQuoter.Quote( "a:b", PatchQuotingStyle.Shell ) );
		Assert.Equal( "\"a'b\"", PatchFileNameQuoter.Quote( "a'b", PatchQuotingStyle.Shell ) );
	}

	/// <summary>Verifies unique version-control abbreviations are accepted and ambiguous ones are rejected.</summary>
	[Fact]
	public void VersionControlUsesUniqueAbbreviations() {
		var numbered = Command.CreateParser().Parse( new[] { "-V", "num" } );
		Assert.True( numbered.IsSuccess );
		Assert.Equal(
			PatchBackupVersionControl.Numbered,
			Command.CreateOptions( numbered, _ => null ).BackupVersionControl
		);
		var ambiguous = Command.CreateParser().Parse( new[] { "-V", "n" } );
		Assert.True( ambiguous.IsSuccess );
		Assert.ThrowsAny<Exception>( () => Command.CreateOptions( ambiguous, _ => null ) );
	}

	/// <summary>Verifies ordinary replacement receives a current modification time.</summary>
	[Fact]
	public async Task DefaultReplacementDoesNotPreserveOldModificationTime() {
		var directory = await CreatePatchDirectoryAsync();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var oldTime = new DateTimeOffset( 2001, 2, 3, 4, 5, 6, TimeSpan.Zero );
		File.SetLastWriteTimeUtc( target, oldTime.UtcDateTime );
		try {
			var before = DateTimeOffset.UtcNow.AddMinutes( -1 );
			var result = await RunAsync( directory, new[] { "target.txt", "change.patch" } );
			Assert.Equal( 0, result.Status );
			Assert.True( File.GetLastWriteTimeUtc( target ) >= before.UtcDateTime );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies UTC header timestamps after 2038 can be applied.</summary>
	[Fact]
	public async Task SetUtcSupportsPost2038Timestamp() {
		const string sourceStamp = "2040-01-02 03:04:05 +0000";
		const string destinationStamp = "2042-06-07 08:09:10 +0000";
		var directory = await CreatePatchDirectoryAsync(
			string.Concat(
				"--- target.txt\t", sourceStamp, "\n",
				"+++ target.txt\t", destinationStamp, "\n",
				"@@ -1 +1 @@\n-old\n+new\n"
			)
		);
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var sourceTime = DateTimeOffset.Parse( sourceStamp, System.Globalization.CultureInfo.InvariantCulture );
		var destinationTime = DateTimeOffset.Parse( destinationStamp, System.Globalization.CultureInfo.InvariantCulture );
		File.SetLastWriteTimeUtc( target, sourceTime.UtcDateTime );
		try {
			var result = await RunAsync( directory, new[] { "-Z", "target.txt", "change.patch" } );
			Assert.Equal( 0, result.Status );
			var actual = new DateTimeOffset( File.GetLastWriteTimeUtc( target ), TimeSpan.Zero );
			Assert.True( (actual - destinationTime).Duration() <= TimeSpan.FromSeconds( 2 ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a patched Unix file retains its portable mode.</summary>
	[Fact]
	public async Task ReplacementPreservesUnixMode() {
		if ( OperatingSystem.IsWindows() ) {
			return;
		}
		var directory = await CreatePatchDirectoryAsync();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
		File.SetUnixFileMode( target, expected );
		try {
			var result = await RunAsync( directory, new[] { "target.txt", "change.patch" } );
			Assert.Equal( 0, result.Status );
			Assert.Equal( expected, File.GetUnixFileMode( target ) & (UnixFileMode)0x0fff );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a newly created alternate output uses GNU's private mode on Unix.</summary>
	[Fact]
	public async Task NewAlternateOutputUsesPrivateUnixMode() {
		if ( OperatingSystem.IsWindows() ) {
			return;
		}
		var directory = await CreatePatchDirectoryAsync();
		var output = System.IO.Path.Combine( directory, "result.txt" );
		try {
			var result = await RunAsync(
				directory,
				new[] { "-o", "result.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode( output ) & (UnixFileMode)0x01ff
			);
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies output links require explicit following and remain links after replacement.</summary>
	[Fact]
	public async Task OutputSymbolicLinkRequiresFollowOption() {
		var directory = await CreatePatchDirectoryAsync();
		var actualOutput = System.IO.Path.Combine( directory, "actual-output.txt" );
		var linkOutput = System.IO.Path.Combine( directory, "result-link.txt" );
		await File.WriteAllTextAsync( actualOutput, "previous\n" );
		try {
			try {
				File.CreateSymbolicLink( linkOutput, actualOutput );
			} catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException ) {
				return;
			}
			var denied = await RunAsync(
				directory,
				new[] { "-o", "result-link.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 2, denied.Status );
			Assert.Contains( "--follow-symlinks", denied.Error, StringComparison.Ordinal );
			Assert.Equal( "previous\n", await File.ReadAllTextAsync( actualOutput ) );

			var followed = await RunAsync(
				directory,
				new[] { "--follow-symlinks", "-o", "result-link.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 0, followed.Status );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( actualOutput ) );
			Assert.NotNull( File.ResolveLinkTarget( linkOutput, returnFinalTarget: false ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies output artifact names cannot contain line breaks.</summary>
	[Fact]
	public async Task OutputFileRejectsNewlineName() {
		var directory = await CreatePatchDirectoryAsync();
		try {
			var result = await RunAsync(
				directory,
				new[] { "-o", "bad\nname.txt", "target.txt", "change.patch" }
			);
			Assert.Equal( 2, result.Status );
			Assert.Contains( "cannot contain a newline", result.Error, StringComparison.Ordinal );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	private static async Task<string> CreatePatchDirectoryAsync(
		string patchText = "--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
	) {
		var directory = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p8-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( directory );
		await File.WriteAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ), "old\n" );
		await File.WriteAllTextAsync( System.IO.Path.Combine( directory, "change.patch" ), patchText );
		return directory;
	}

	private static async Task<(int Status, string Error)> RunAsync(
		string directory,
		IReadOnlyList<string> arguments,
		string promptInput = "",
		Stream? binaryOutput = null
	) {
		using var input = new StringReader( promptInput );
		using var error = new StringWriter();
		var allArguments = new List<string> { "-d", directory };
		allArguments.AddRange( arguments );
		var status = await Command.RunAsync(
			allArguments,
			stdin: input,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: Stream.Null,
			stdoutStream: binaryOutput
		);
		return ( status, error.ToString() );
	}

	private sealed class BrokenWriteStream : Stream {
		/// <inheritdoc/>
		public override bool CanRead => false;
		/// <inheritdoc/>
		public override bool CanSeek => false;
		/// <inheritdoc/>
		public override bool CanWrite => true;
		/// <inheritdoc/>
		public override long Length => 0;
		/// <inheritdoc/>
		public override long Position {
			get => 0;
			set => throw new NotSupportedException();
		}
		/// <inheritdoc/>
		public override void Flush() {
		}
		/// <inheritdoc/>
		public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		/// <inheritdoc/>
		public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();
		/// <inheritdoc/>
		public override void SetLength( long value ) => throw new NotSupportedException();
		/// <inheritdoc/>
		public override void Write( byte[] buffer, int offset, int count ) => throw new IOException( "broken output" );
		/// <inheritdoc/>
		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default
		) => new( Task.FromException( new IOException( "broken output" ) ) );
	}
}
