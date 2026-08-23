namespace Icod.Patch.Tests;

using System.Text;
using Xunit;

/// <summary>Exercises indexed in-memory and spill-backed target content.</summary>
public sealed class PatchTargetContentTests {
	/// <summary>Verifies mixed line endings and an incomplete final record survive indexing.</summary>
	[Fact]
	public async Task MixedTerminatorsRoundTripExactly() {
		var bytes = Encoding.UTF8.GetBytes( "one\r\ntwo\rthree\nfour" );
		await using var content = await PatchTargetContent.FromBytesAsync( bytes );
		Assert.Equal( 4, content.Records.Count );
		Assert.Equal( PatchLineTerminator.CarriageReturnLineFeed, content.Records[0].Terminator );
		Assert.Equal( PatchLineTerminator.CarriageReturn, content.Records[1].Terminator );
		Assert.Equal( PatchLineTerminator.LineFeed, content.Records[2].Terminator );
		Assert.Equal( PatchLineTerminator.None, content.Records[3].Terminator );
		Assert.Equal( bytes, await content.ToArrayAsync() );
	}

	/// <summary>Verifies large targets use private spill storage that is deleted on disposal.</summary>
	[Fact]
	public async Task LargeTargetSpillsAndCleansTemporaryStorage() {
		var bytes = Encoding.UTF8.GetBytes( new string( 'x', 4096 ) + "\n" );
		string? path = null;
		await using ( var content = await PatchTargetContent.FromBytesAsync(
			bytes,
			new PatchTargetLimits {
				MemoryThresholdBytes = 8,
				MaximumBytes = 8192,
				MaximumRecords = 10
			}
		) ) {
			Assert.True( content.IsSpillBacked );
			path = content.TemporaryPath;
			Assert.NotNull( path );
			Assert.True( File.Exists( path ) );
			Assert.Equal( bytes, await content.ToArrayAsync() );
		}
		Assert.False( File.Exists( path! ) );
	}

	/// <summary>Verifies a long spill-backed record can be copied and searched without record materialization.</summary>
	[Fact]
	public async Task LongSpillRecordStreamsForOutputAndPrerequisiteSearch() {
		var bytes = Encoding.UTF8.GetBytes(
			string.Concat( new string( 'x', 2 * 1024 * 1024 ), " revision-9\n" )
		);
		await using var content = await PatchTargetContent.FromBytesAsync(
			bytes,
			new PatchTargetLimits {
				MemoryThresholdBytes = 16,
				MaximumBytes = 4 * 1024 * 1024,
				MaximumRecords = 4
			}
		);
		Assert.True( content.IsSpillBacked );
		Assert.Equal( bytes.LongLength, content.Records[0].TotalLength );
		await content.WriteRecordToAsync( 0, Stream.Null, includeTerminator: true );
		Assert.True( await PatchPrerequisite.ContainsAsync( content, "revision-9" ) );
		Assert.False( await PatchPrerequisite.ContainsAsync( content, "revision" ) );
	}

	/// <summary>Verifies configured target byte limits fail deterministically.</summary>
	[Fact]
	public async Task TargetByteLimitIsEnforced() {
		await Assert.ThrowsAsync<PatchApplicationException>(
			() => PatchTargetContent.FromBytesAsync(
				Encoding.UTF8.GetBytes( "too large" ),
				new PatchTargetLimits {
					MemoryThresholdBytes = 4,
					MaximumBytes = 3,
					MaximumRecords = 10
				}
			)
		);
	}
}
