namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Icod.CommandFramework.FileSystem.Metadata;
using Icod.CommandFramework.FileSystem.Mutation;
using Icod.CommandFramework.FileSystem.RecursiveMutation;
using Icod.CommandFramework.FileSystem.TransactionalReplacement;
using Icod.CommandFramework.FileSystem.Traversal;
using Xunit;

/// <summary>Closes Phase P11B against the stabilized shared E6 replacement contract.</summary>
public sealed class PhaseP11BTransactionTests {
	/// <summary>Verifies the production factory creates only the shared-E6 Patch adapter.</summary>
	[Fact]
	public async Task ProductionFactoryCreatesSharedE6Adapter() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var artifact = await CreateWriteArtifactAsync( fileSystem, target, "new\n" );
			await using var transaction = await fileSystem.CreateTransactionAsync(
				new PatchArtifactPlan(
					new[] { artifact },
					PatchExitStatus.Success,
					Array.Empty<string>()
				)
			);
			Assert.IsType<PatchE6Transaction>( transaction );
			var result = await transaction.CommitAsync();
			Assert.True( result.Succeeded, string.Join( Environment.NewLine, result.Diagnostics ) );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
			AssertNoE6TemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies Patch permits and reports the shared E6 preferred-atomic fallback policy.</summary>
	[Fact]
	public async Task PreferredAtomicityReportsNonAtomicFallback() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var replacementFileSystem = new ReportingFallbackFileSystem(
				SystemTransactionalReplacementFileSystem.Instance
			);
			var fileSystem = new SystemPatchFileSystem(
				SystemFileSystemMetadataProvider.Instance,
				SystemFileSystemMutationProvider.Instance,
				replacementFileSystem
			);
			Assert.Same( replacementFileSystem.Capabilities, fileSystem.TransactionCapabilities );
			var artifact = await CreateWriteArtifactAsync( fileSystem, target, "new\n" );
			await using var transaction = await fileSystem.CreateTransactionAsync(
				new PatchArtifactPlan(
					new[] { artifact },
					PatchExitStatus.Success,
					Array.Empty<string>()
				)
			);
			var result = await transaction.CommitAsync();
			Assert.True( result.Succeeded, string.Join( Environment.NewLine, result.Diagnostics ) );
			Assert.True( replacementFileSystem.CommitObserved );
			Assert.True( replacementFileSystem.AllowNonAtomicFallbackObserved );
			Assert.Contains(
				result.Diagnostics,
				message => message.Contains( "injected non-atomic fallback", StringComparison.Ordinal )
			);
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
			AssertNoE6TemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	private static async Task<PatchArtifact> CreateWriteArtifactAsync(
		SystemPatchFileSystem fileSystem,
		string path,
		string value
	) {
		var observation = await fileSystem.ObserveAsync( path, followPathIndirection: false );
		return new PatchArtifact(
			PatchArtifactKind.Target,
			PatchArtifactAction.Write,
			path,
			PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( value ) ),
			observation,
			new PatchArtifactMetadata(),
			path
		);
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p11b-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}

	private static void AssertNoE6TemporaryFiles( string directory ) {
		Assert.DoesNotContain(
			Directory.EnumerateFileSystemEntries( directory ),
			path => System.IO.Path.GetFileName( path ).Contains( ".icod-e6-", StringComparison.Ordinal )
		);
	}

	private sealed class ReportingFallbackFileSystem : ITransactionalReplacementFileSystem {
		private readonly ITransactionalReplacementFileSystem inner;

		/// <summary>Initializes a provider that reports a controlled non-atomic commit.</summary>
		public ReportingFallbackFileSystem( ITransactionalReplacementFileSystem inner ) {
			this.inner = inner ?? throw new ArgumentNullException( nameof( inner ) );
			this.Capabilities = new TransactionalReplacementCapabilities(
				SupportsAtomicReplaceExisting: false,
				SupportsAtomicPublishNew: false,
				SupportsAtomicDelete: inner.Capabilities.SupportsAtomicDelete,
				SupportsDirectoryDurability: inner.Capabilities.SupportsDirectoryDurability
			);
		}

		/// <inheritdoc/>
		public TransactionalReplacementCapabilities Capabilities { get; }
		/// <summary>Gets whether a file commit reached the provider.</summary>
		public bool CommitObserved { get; private set; }
		/// <summary>Gets whether E6 allowed its documented non-atomic fallback.</summary>
		public bool AllowNonAtomicFallbackObserved { get; private set; }

		/// <inheritdoc/>
		public ValueTask<TransactionalReplacementObservation> ObserveAsync(
			string path,
			PathDereferenceMode dereferenceMode,
			CancellationToken cancellationToken = default
		) => this.inner.ObserveAsync( path, dereferenceMode, cancellationToken );

		/// <inheritdoc/>
		public ValueTask<bool> AnyNumberedBackupExistsAsync(
			string destinationPath,
			int maximumNumberedBackup,
			CancellationToken cancellationToken = default
		) => this.inner.AnyNumberedBackupExistsAsync(
			destinationPath,
			maximumNumberedBackup,
			cancellationToken
		);

		/// <inheritdoc/>
		public ValueTask<string> CreateSiblingTemporaryFileAsync(
			string destinationPath,
			string purpose,
			CancellationToken cancellationToken = default
		) => this.inner.CreateSiblingTemporaryFileAsync( destinationPath, purpose, cancellationToken );

		/// <inheritdoc/>
		public ValueTask WriteTemporaryFileAsync(
			string path,
			TransactionalReplacementContentWriter writer,
			CancellationToken cancellationToken = default
		) => this.inner.WriteTemporaryFileAsync( path, writer, cancellationToken );

		/// <inheritdoc/>
		public ValueTask CopyTemporaryFileAsync(
			string sourcePath,
			string destinationPath,
			CancellationToken cancellationToken = default
		) => this.inner.CopyTemporaryFileAsync( sourcePath, destinationPath, cancellationToken );

		/// <inheritdoc/>
		public ValueTask<TransactionalReplacementDurabilityResult> FlushFileAsync(
			string path,
			CancellationToken cancellationToken = default
		) => this.inner.FlushFileAsync( path, cancellationToken );

		/// <inheritdoc/>
		public async ValueTask<TransactionalReplacementCommitResult> CommitFileAsync(
			string stagedPath,
			string destinationPath,
			bool replaceExisting,
			bool allowNonAtomicFallback,
			CancellationToken cancellationToken = default
		) {
			this.CommitObserved = true;
			this.AllowNonAtomicFallbackObserved = allowNonAtomicFallback;
			await this.inner.CommitFileAsync(
				stagedPath,
				destinationPath,
				replaceExisting,
				allowNonAtomicFallback: true,
				cancellationToken: cancellationToken
			).ConfigureAwait( false );
			return new TransactionalReplacementCommitResult(
				TransactionalReplacementAtomicity.NonAtomic,
				"injected non-atomic fallback"
			);
		}

		/// <inheritdoc/>
		public ValueTask<TransactionalReplacementCommitResult> DeleteFileAsync(
			string path,
			FileSystemMutationPrecondition precondition,
			CancellationToken cancellationToken = default
		) => this.inner.DeleteFileAsync( path, precondition, cancellationToken );

		/// <inheritdoc/>
		public ValueTask ApplyMetadataAsync(
			string path,
			FileSystemMetadata sourceMetadata,
			RecursiveMetadataPreservationPlan plan,
			CancellationToken cancellationToken = default
		) => this.inner.ApplyMetadataAsync( path, sourceMetadata, plan, cancellationToken );

		/// <inheritdoc/>
		public ValueTask RestoreMetadataAsync(
			string path,
			FileSystemMetadata originalMetadata,
			CancellationToken cancellationToken = default
		) => this.inner.RestoreMetadataAsync( path, originalMetadata, cancellationToken );

		/// <inheritdoc/>
		public ValueTask<TransactionalReplacementDurabilityResult> FlushContainingDirectoryAsync(
			string path,
			CancellationToken cancellationToken = default
		) => this.inner.FlushContainingDirectoryAsync( path, cancellationToken );

		/// <inheritdoc/>
		public ValueTask DeleteTemporaryFileAsync(
			string path,
			CancellationToken cancellationToken = default
		) => this.inner.DeleteTemporaryFileAsync( path, cancellationToken );
	}
}
