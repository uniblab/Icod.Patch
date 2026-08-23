namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Icod.CommandFramework.FileSystem.Mutation;
using Icod.CommandFramework.FileSystem.TransactionalReplacement;
using Xunit;

/// <summary>Verifies Patch conformance against the stabilized shared E2 through E6 contracts.</summary>
public sealed class WaveDConformanceTests {
	/// <summary>Verifies terminal symbolic links are rejected unless explicitly followed.</summary>
	[Fact]
	public async Task ArtifactLinkPolicyUsesSharedCanonicalResolver() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var link = System.IO.Path.Combine( directory, "link.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			try {
				File.CreateSymbolicLink( link, target );
			} catch ( Exception exception ) when (
				exception is UnauthorizedAccessException
					or PlatformNotSupportedException
					or IOException
			) {
				return;
			}
			var fileSystem = new SystemPatchFileSystem();
			await Assert.ThrowsAsync<PatchApplicationException>(
				async () => await fileSystem.ResolveArtifactPathAsync(
					link,
					directory,
					followPathIndirection: false
				)
			);
			var resolvedLink = await fileSystem.ResolveArtifactPathAsync(
				link,
				directory,
				followPathIndirection: true
			);
			var resolvedTarget = await fileSystem.ResolveArtifactPathAsync(
				target,
				directory,
				followPathIndirection: true
			);
			Assert.Equal( resolvedTarget, resolvedLink );
		} finally {
			DeleteLinkIfPresent( link );
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a symbolic-link spelling of the working root is canonicalized before containment.</summary>
	[Fact]
	public async Task WorkingRootAliasUsesPhysicalContainmentRoot() {
		var parent = CreateTemporaryDirectory();
		var actual = System.IO.Path.Combine( parent, "actual" );
		var alias = System.IO.Path.Combine( parent, "alias" );
		Directory.CreateDirectory( actual );
		var target = System.IO.Path.Combine( actual, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			try {
				Directory.CreateSymbolicLink( alias, actual );
			} catch ( Exception exception ) when (
				exception is UnauthorizedAccessException
					or PlatformNotSupportedException
					or IOException
			) {
				return;
			}
			var fileSystem = new SystemPatchFileSystem();
			var resolved = await fileSystem.ResolveArtifactPathAsync(
				"target.txt",
				alias,
				followPathIndirection: false
			);
			var expected = await fileSystem.ResolveArtifactPathAsync(
				target,
				actual,
				followPathIndirection: false
			);
			Assert.Equal( expected, resolved );
		} finally {
			DeleteLinkIfPresent( alias );
			Directory.Delete( parent, recursive: true );
		}
	}

	/// <summary>Verifies lexical and physically resolved artifact paths cannot escape the working root.</summary>
	[Fact]
	public async Task ArtifactContainmentUsesSharedCanonicalModel() {
		var directory = CreateTemporaryDirectory();
		var outside = CreateTemporaryDirectory();
		var link = System.IO.Path.Combine( directory, "outside-link" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			await Assert.ThrowsAsync<PatchApplicationException>(
				async () => await fileSystem.ResolveArtifactPathAsync(
					System.IO.Path.Combine( "..", System.IO.Path.GetFileName( outside ), "escaped.txt" ),
					directory,
					followPathIndirection: false
				)
			);
			try {
				Directory.CreateSymbolicLink( link, outside );
			} catch ( Exception exception ) when (
				exception is UnauthorizedAccessException
					or PlatformNotSupportedException
					or IOException
			) {
				return;
			}
			await Assert.ThrowsAsync<PatchApplicationException>(
				async () => await fileSystem.ResolveArtifactPathAsync(
					System.IO.Path.Combine( link, "escaped.txt" ),
					directory,
					followPathIndirection: true
				)
			);
		} finally {
			DeleteLinkIfPresent( link );
			Directory.Delete( directory, recursive: true );
			Directory.Delete( outside, recursive: true );
		}
	}

	/// <summary>Verifies E3 owner and group identifiers are retained through E4 replacement on Unix hosts.</summary>
	[Fact]
	public async Task UnixOwnershipIsPreservedThroughMutationProvider() {
		if ( OperatingSystem.IsWindows() ) {
			return;
		}
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var observation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			if ( !observation.UserId.HasValue || !observation.GroupId.HasValue ) {
				return;
			}
			var artifact = new PatchArtifact(
				PatchArtifactKind.Target,
				PatchArtifactAction.Write,
				target,
				PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( "new\n" ) ),
				observation,
				new PatchArtifactMetadata {
					UserId = observation.UserId,
					GroupId = observation.GroupId
				},
				target
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				new PatchArtifactPlan(
					new[] { artifact },
					PatchExitStatus.Success,
					Array.Empty<string>()
				)
			);
			var result = await transaction.CommitAsync();
			Assert.True( result.Succeeded, string.Join( Environment.NewLine, result.Diagnostics ) );
			var current = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			Assert.Equal( observation.UserId, current.UserId );
			Assert.Equal( observation.GroupId, current.GroupId );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies post-2038 timestamps flow through E3 timestamp mutation.</summary>
	[Fact]
	public async Task Post2038TimestampIsAppliedThroughMetadataProvider() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		var timestamp = new DateTimeOffset( 2042, 7, 8, 9, 10, 11, TimeSpan.Zero );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var observation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var artifact = new PatchArtifact(
				PatchArtifactKind.Target,
				PatchArtifactAction.Write,
				target,
				PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( "new\n" ) ),
				observation,
				new PatchArtifactMetadata {
					AccessTime = timestamp,
					ModificationTime = timestamp,
					RequireTimestamps = true
				},
				target
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				new PatchArtifactPlan(
					new[] { artifact },
					PatchExitStatus.Success,
					Array.Empty<string>()
				)
			);
			var result = await transaction.CommitAsync();
			Assert.True( result.Succeeded, string.Join( Environment.NewLine, result.Diagnostics ) );
			var actual = File.GetLastWriteTimeUtc( target );
			Assert.Equal( timestamp.Year, actual.Year );
			Assert.Equal( timestamp.Month, actual.Month );
			Assert.Equal( timestamp.Day, actual.Day );
			Assert.Equal( timestamp.Hour, actual.Hour );
			Assert.Equal( timestamp.Minute, actual.Minute );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies portable modes flow through E4 rather than a Patch-local chmod implementation.</summary>
	[Fact]
	public async Task UnixModeIsAppliedThroughMutationProvider() {
		if ( OperatingSystem.IsWindows() ) {
			return;
		}
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var observation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var artifact = new PatchArtifact(
				PatchArtifactKind.Target,
				PatchArtifactAction.Write,
				target,
				PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( "new\n" ) ),
				observation,
				new PatchArtifactMetadata { Mode = 0x0180 },
				target
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				new PatchArtifactPlan(
					new[] { artifact },
					PatchExitStatus.Success,
					Array.Empty<string>()
				)
			);
			var result = await transaction.CommitAsync();
			Assert.True( result.Succeeded, string.Join( Environment.NewLine, result.Diagnostics ) );
			var actual = File.GetUnixFileMode( target );
			Assert.True( actual.HasFlag( UnixFileMode.UserRead ) );
			Assert.True( actual.HasFlag( UnixFileMode.UserWrite ) );
			Assert.False( actual.HasFlag( UnixFileMode.GroupRead ) );
			Assert.False( actual.HasFlag( UnixFileMode.OtherRead ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies the shared E4 mode capability follows the host platform profile.</summary>
	[Fact]
	public void HostModeCapabilityMatchesPlatformProfile() {
		var capabilities = SystemFileSystemMutationProvider.Instance.Capabilities;
		if ( OperatingSystem.IsWindows() ) {
			Assert.False( capabilities.CanSetModes );
		} else if ( OperatingSystem.IsLinux()
			|| OperatingSystem.IsMacOS()
			|| OperatingSystem.IsFreeBSD() ) {
			Assert.True( capabilities.CanSetModes );
		}
	}

	/// <summary>Verifies the host adapter exposes the frozen contract and the stabilized shared E6 capability record.</summary>
	[Fact]
	public void HostAdapterExposesStabilizedE6Contract() {
		IPatchFileSystem fileSystem = new SystemPatchFileSystem();
		Assert.Same( PatchE6TransactionContract.Current, fileSystem.TransactionContract );
		var expected = SystemTransactionalReplacementFileSystem.Instance.Capabilities;
		Assert.Equal( expected, fileSystem.TransactionCapabilities );
		var native = OperatingSystem.IsWindows()
			|| OperatingSystem.IsLinux()
			|| OperatingSystem.IsMacOS()
			|| OperatingSystem.IsFreeBSD();
		Assert.Equal( native, fileSystem.TransactionCapabilities.SupportsAtomicReplaceExisting );
		Assert.Equal( native, fileSystem.TransactionCapabilities.SupportsAtomicPublishNew );
		Assert.Equal( native, fileSystem.TransactionCapabilities.SupportsAtomicDelete );
		Assert.Equal( native, fileSystem.TransactionCapabilities.SupportsDirectoryDurability );
	}

	/// <summary>Verifies Phase P11B removed the unreachable provisional P9 implementation.</summary>
	[Fact]
	public void ProvisionalP9TransactionTypeIsAbsent() {
		Assert.Null( typeof( Command ).Assembly.GetType(
			"Icod.Patch.SystemPatchTransaction",
			throwOnError: false
		) );
	}

	private static void DeleteLinkIfPresent( string path ) {
		try {
			File.Delete( path );
		} catch ( Exception exception ) when (
			exception is UnauthorizedAccessException or IOException
		) {
			if ( Directory.Exists( path ) ) {
				Directory.Delete( path, recursive: false );
			}
		}
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p10-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}
}
