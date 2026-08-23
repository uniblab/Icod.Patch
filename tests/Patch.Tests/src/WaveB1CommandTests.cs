namespace Icod.Patch.Tests;

using System.Text;
using Xunit;

/// <summary>Exercises Wave B1 option validation at the command boundary.</summary>
public sealed class WaveB1CommandTests {
	/// <summary>Verifies P6 matching and policy options are accepted by the GNU-style parser.</summary>
	[Fact]
	public async Task WaveB1OptionsReachTheVirtualApplicationBoundary() {
		var error = new StringWriter();
		var bytes = Encoding.UTF8.GetBytes(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = new MemoryStream( bytes, writable: false );
		var status = await Command.RunAsync(
			new[] { "-f", "-F", "4", "-l", "--merge=diff3", "-N", "-t" },
			stdin: TextReader.Null,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: input
		);
		Assert.Equal( 1, status );
		Assert.Contains( "no usable file name", error.ToString() );
	}

	/// <summary>Verifies command options map without loss into pure application policy.</summary>
	[Fact]
	public void CommandOptionsMapToEnginePolicy() {
		var commandOptions = new PatchOptions {
			Force = true,
			ForwardOnly = true,
			Reverse = true,
			Batch = true,
			Fuzz = 7,
			IgnoreWhitespace = true,
			MergeStyle = PatchMergeStyle.Diff3
		};
		var engineOptions = PatchApplication.CreateEngineOptions( commandOptions, "revision-7" );
		Assert.True( engineOptions.Force );
		Assert.True( engineOptions.ForwardOnly );
		Assert.True( engineOptions.Reverse );
		Assert.True( engineOptions.Batch );
		Assert.Equal( 7, engineOptions.Fuzz );
		Assert.True( engineOptions.IgnoreWhitespace );
		Assert.Equal( PatchMergeStyle.Diff3, engineOptions.MergeStyle );
		Assert.Equal( "revision-7", engineOptions.PrerequisiteToken );
	}

	/// <summary>Verifies invalid fuzz values are rejected before source acquisition.</summary>
	[Theory]
	[InlineData( "-1" )]
	[InlineData( "invalid" )]
	public async Task InvalidFuzzValueIsRejected( string value ) {
		var error = new StringWriter();
		var status = await Command.RunAsync(
			new[] { "--fuzz", value },
			stdin: TextReader.Null,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: Stream.Null
		);
		Assert.Equal( 2, status );
		Assert.Contains( "invalid maximum fuzz factor", error.ToString() );
	}

	/// <summary>Verifies GNU's two supported merge-style names.</summary>
	[Theory]
	[InlineData( "merge" )]
	[InlineData( "diff3" )]
	public async Task MergeStylesAreAccepted( string style ) {
		var error = new StringWriter();
		var bytes = Encoding.UTF8.GetBytes( "1c1\n< old\n---\n> new\n" );
		await using var input = new MemoryStream( bytes, writable: false );
		var status = await Command.RunAsync(
			new[] { string.Concat( "--merge=", style ) },
			stdin: TextReader.Null,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: input
		);
		Assert.Equal( 1, status );
		Assert.DoesNotContain( "invalid merge style", error.ToString() );
	}

	/// <summary>Verifies unknown merge styles are diagnosed.</summary>
	[Fact]
	public async Task InvalidMergeStyleIsRejected() {
		var error = new StringWriter();
		var status = await Command.RunAsync(
			new[] { "--merge=unknown" },
			stdin: TextReader.Null,
			stdout: TextWriter.Null,
			stderr: error,
			stdinStream: Stream.Null
		);
		Assert.Equal( 2, status );
		Assert.Contains( "invalid merge style 'unknown'", error.ToString() );
	}
}
