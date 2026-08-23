namespace Icod.Patch.Tests;

using Xunit;

/// <summary>Exercises P7 path-selection options and environment policy at the command boundary.</summary>
public sealed class WaveB2CommandTests {
	/// <summary>Verifies implemented P7 options map into one immutable command policy.</summary>
	[Fact]
	public void PathOptionsMapWithoutLoss() {
		var parsed = Command.CreateParser().Parse(
			new[] {
				"-d", "tree",
				"-p", "2",
				"-g", "1",
				"--posix",
				"--follow-symlinks",
				"target.txt",
				"change.patch"
			}
		);
		Assert.True( parsed.IsSuccess );
		var options = Command.CreateOptions( parsed, _ => null );
		Assert.Equal( "tree", options.Directory );
		Assert.Equal( 2, options.StripCount );
		Assert.Equal( 1, options.Get );
		Assert.True( options.Posix );
		Assert.True( options.FollowSymbolicLinks );
		Assert.Equal( "target.txt", options.OriginalFile );
		Assert.Equal( "change.patch", options.PatchFile );
	}

	/// <summary>Verifies <c>POSIXLY_CORRECT</c> changes filename and retrieval defaults.</summary>
	[Fact]
	public void PosixEnvironmentSelectsPosixDefaults() {
		var parsed = Command.CreateParser().Parse( Array.Empty<string>() );
		Assert.True( parsed.IsSuccess );
		var options = Command.CreateOptions(
			parsed,
			name => "POSIXLY_CORRECT" == name ? "1" : null
		);
		Assert.True( options.Posix );
		Assert.Equal( 0, options.Get );
	}

	/// <summary>Verifies <c>PATCH_GET</c> overrides the non-POSIX retrieval default.</summary>
	[Fact]
	public void PatchGetEnvironmentSelectsRetrievalPolicy() {
		var parsed = Command.CreateParser().Parse( Array.Empty<string>() );
		Assert.True( parsed.IsSuccess );
		var options = Command.CreateOptions(
			parsed,
			name => "PATCH_GET" == name ? "3" : null
		);
		Assert.False( options.Posix );
		Assert.Equal( 3, options.Get );
	}

	/// <summary>Verifies invalid P7 numeric option values are diagnosed before source acquisition.</summary>
	[Theory]
	[InlineData( "--strip", "-1", "invalid strip count" )]
	[InlineData( "--strip", "invalid", "invalid strip count" )]
	[InlineData( "--get", "invalid", "invalid version-control retrieval policy" )]
	public async Task InvalidNumericPathOptionsAreRejected(
		string option,
		string value,
		string expected
	) {
		var error = new StringWriter();
		var status = await Command.RunAsync(
			new[] { option, value },
			stdin: TextReader.Null,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: Stream.Null
		);
		Assert.Equal( 2, status );
		Assert.Contains( expected, error.ToString() );
	}

	/// <summary>Verifies <c>-d</c> is applied to a relative patch source as well as target names.</summary>
	[Fact]
	public async Task DirectoryOptionControlsRelativePatchSource() {
		var directory = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p7-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( directory );
		try {
			await File.WriteAllTextAsync(
				System.IO.Path.Combine( directory, "target.txt" ),
				"old\n"
			);
			await File.WriteAllTextAsync(
				System.IO.Path.Combine( directory, "change.patch" ),
				"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
			);
			var error = new StringWriter();
			var status = await Command.RunAsync(
				new[] { "-d", directory, "target.txt", "change.patch" },
				stdin: TextReader.Null,
				stdout: TextWriter.Null,
				stderr: error,
				stdinStream: Stream.Null
			);
			Assert.Equal( 0, status );
			Assert.Equal(
				"new\n",
				await File.ReadAllTextAsync( System.IO.Path.Combine( directory, "target.txt" ) )
			);
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}
}
