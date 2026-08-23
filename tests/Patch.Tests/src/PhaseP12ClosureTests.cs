namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Icod.CommandFramework.Diagnostics;
using Xunit;

/// <summary>Closes Phase P12 conformance, hardening, and extraction-readiness contracts.</summary>
public sealed class PhaseP12ClosureTests {
	/// <summary>Verifies final help and version output contain no provisional phase language.</summary>
	[Fact]
	public async Task HelpAndVersionDescribeFinalReleaseSurface() {
		var help = await RunAsync( new[] { "--help" } );
		Assert.Equal( 0, help.Status );
		Assert.Contains( "shared E6 transaction provider", help.Output, StringComparison.Ordinal );
		Assert.DoesNotContain( "Wave C", help.Output, StringComparison.Ordinal );
		Assert.DoesNotContain( "initial P9", help.Output, StringComparison.Ordinal );
		Assert.DoesNotContain( "later Icod.Patch phase", help.Output, StringComparison.Ordinal );

		var version = await RunAsync( new[] { "--version" } );
		Assert.Equal( 0, version.Status );
		Assert.Contains( "patch (Icod.Patch) 1.0", version.Output, StringComparison.Ordinal );
		Assert.Empty( version.Error );
	}

	/// <summary>Verifies source-defined unavailable options report final capability diagnostics.</summary>
	/// <param name="option">The option spelling.</param>
	/// <param name="value">The required option value.</param>
	/// <param name="expected">Text identifying the final capability decision.</param>
	[Theory]
	[InlineData( "-D", "FEATURE", "conditional-output mode is not implemented" )]
	[InlineData( "--read-only", "fail", "read-only input policy is not implemented" )]
	[InlineData( "-x", "1", "GNU DEBUGGING compatibility is not enabled" )]
	public async Task UnavailableOptionsReportFinalCapabilityDiagnostics(
		string option,
		string value,
		string expected
	) {
		var result = await RunAsync( new[] { option, value } );
		Assert.Equal( 2, result.Status );
		Assert.Contains( expected, result.Error, StringComparison.Ordinal );
		Assert.DoesNotContain( "reserved for a later", result.Error, StringComparison.Ordinal );
	}

	/// <summary>Verifies command-line and environment POSIX policy select the same final defaults.</summary>
	[Fact]
	public void PosixModesSelectFinalRetrievalDefaults() {
		var explicitParse = Command.CreateParser().Parse( new[] { "--posix" } );
		Assert.True( explicitParse.IsSuccess );
		var explicitOptions = Command.CreateOptions( explicitParse, _ => null );
		Assert.True( explicitOptions.Posix );
		Assert.Equal( 0, explicitOptions.Get );

		var environmentParse = Command.CreateParser().Parse( Array.Empty<string>() );
		Assert.True( environmentParse.IsSuccess );
		var environmentOptions = Command.CreateOptions(
			environmentParse,
			name => "POSIXLY_CORRECT" == name ? string.Empty : null
		);
		Assert.True( environmentOptions.Posix );
		Assert.Equal( 0, environmentOptions.Get );
	}

	/// <summary>Verifies captured Icod Diffutils output applies through every supported patch syntax.</summary>
	/// <param name="option">The explicit input-format option.</param>
	/// <param name="fixture">The Diffutils-produced fixture name.</param>
	[Theory]
	[InlineData( "-u", "unified-basic.patch" )]
	[InlineData( "-c", "context-basic.patch" )]
	[InlineData( "-n", "normal-basic.patch" )]
	[InlineData( "-e", "ed-basic.patch" )]
	public async Task IcodDiffutilsFixturesApplyWithoutRuntimeDependency(
		string option,
		string fixture
	) {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var fixturePath = System.IO.Path.Combine(
			AppContext.BaseDirectory,
			"fixtures",
			"icod-diffutils",
			fixture
		);
		var fixtureText = await File.ReadAllTextAsync( fixturePath );
		var lineEnding = fixtureText.Contains( "\r\n", StringComparison.Ordinal )
			? "\r\n"
			: "\n";
		await File.WriteAllTextAsync(
			target,
			string.Concat( "same", lineEnding, "left", lineEnding )
		);
		try {
			var result = await RunAsync(
				new[] { "-d", directory, option, "target.txt", fixturePath }
			);
			Assert.Equal( 0, result.Status );
			Assert.Equal(
				string.Concat( "same", lineEnding, "right", lineEnding ),
				await File.ReadAllTextAsync( target )
			);
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies canonical containment prevents a header-selected parent escape.</summary>
	[Fact]
	public async Task HeaderSelectedParentEscapeCannotMutateOutsideWorkingRoot() {
		var parent = CreateTemporaryDirectory();
		var root = System.IO.Path.Combine( parent, "root" );
		var outside = System.IO.Path.Combine( parent, "outside.txt" );
		Directory.CreateDirectory( root );
		await File.WriteAllTextAsync( outside, "old\n" );
		await File.WriteAllTextAsync(
			System.IO.Path.Combine( root, "change.patch" ),
			"--- ../outside.txt\n+++ ../outside.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		try {
			var result = await RunAsync(
				new[] { "-d", root, "-u", "-p", "0", "-i", "change.patch", "--batch" }
			);
			Assert.Equal( 1, result.Status );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( outside ) );
			Assert.Contains( "escapes", result.Error, StringComparison.OrdinalIgnoreCase );
		} finally {
			Directory.Delete( parent, recursive: true );
		}
	}

	/// <summary>Verifies an overlong patch record fails as controlled trouble.</summary>
	[Fact]
	public async Task OverlongPatchRecordReportsControlledTrouble() {
		var bytes = new byte[(1024 * 1024) + 1];
		Array.Fill( bytes, (byte)'x' );
		var result = await RunAsync( Array.Empty<string>(), bytes );
		Assert.Equal( 2, result.Status );
		Assert.Contains(
			"patch record exceeds the configured byte limit",
			result.Error,
			StringComparison.Ordinal
		);
	}

	/// <summary>Verifies cancellation while the patch stream is blocked returns the repository cancellation status.</summary>
	[Fact]
	public async Task CancellationDuringSourceReadReturnsCanceledStatus() {
		await using var input = new CancellationBlockingStream();
		using var cancellation = new CancellationTokenSource();
		using var output = new StringWriter();
		using var error = new StringWriter();
		var run = Command.RunAsync(
			Array.Empty<string>(),
			TextReader.Null,
			output,
			error,
			cancellation.Token,
			input
		);
		await input.ReadStarted;
		cancellation.Cancel();
		Assert.Equal( CommandExitCodes.Canceled, await run );
	}

	/// <summary>Verifies the final assembly exposes only the supported command facade.</summary>
	[Fact]
	public void FinalPublicSurfaceContainsOnlyCommandFacade() {
		var exported = typeof( Command ).Assembly.GetExportedTypes();
		Assert.Contains( typeof( Command ), exported );
		Assert.DoesNotContain(
			exported,
			type => "Icod.Patch" == type.Namespace && typeof( Command ) != type
		);
	}

	private static async Task<(int Status, string Output, string Error)> RunAsync(
		IReadOnlyList<string> arguments,
		byte[]? inputBytes = null
	) {
		await using var input = new MemoryStream( inputBytes ?? Array.Empty<byte>(), writable: false );
		using var output = new StringWriter();
		using var error = new StringWriter();
		var status = await Command.RunAsync(
			arguments,
			TextReader.Null,
			output,
			error,
			stdinStream: input
		);
		return ( status, output.ToString(), error.ToString() );
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p12-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}

	private sealed class CancellationBlockingStream : Stream {
		private readonly TaskCompletionSource<bool> readStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously
		);

		/// <summary>Gets a task completed when the first asynchronous read begins.</summary>
		public Task ReadStarted => this.readStarted.Task;
		/// <inheritdoc/>
		public override bool CanRead => true;
		/// <inheritdoc/>
		public override bool CanSeek => false;
		/// <inheritdoc/>
		public override bool CanWrite => false;
		/// <inheritdoc/>
		public override long Length => throw new NotSupportedException();
		/// <inheritdoc/>
		public override long Position {
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		/// <inheritdoc/>
		public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException();

		/// <inheritdoc/>
		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default
		) {
			this.readStarted.TrySetResult( true );
			await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken ).ConfigureAwait( false );
			return 0;
		}

		/// <inheritdoc/>
		public override void Flush() {
		}

		/// <inheritdoc/>
		public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();

		/// <inheritdoc/>
		public override void SetLength( long value ) => throw new NotSupportedException();

		/// <inheritdoc/>
		public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
	}
}
