namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Xunit;

/// <summary>Exercises the Phase P11A adapter over the shared E6 transaction boundary.</summary>
public sealed class WaveCTransactionTests {
	/// <summary>Verifies a post-replacement metadata failure restores the original file.</summary>
	[Fact]
	public async Task MetadataFailureRollsBackCommittedReplacement() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = await CreateWritePlanAsync( fileSystem, target, "new\n" );
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new ThrowingFailureInjector( PatchTransactionStage.ApplyMetadata )
			);
			await transaction.StageAsync();
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a later artifact failure rolls back earlier committed artifacts.</summary>
	[Fact]
	public async Task LaterCommitFailureRollsBackEarlierArtifact() {
		var directory = CreateTemporaryDirectory();
		var first = System.IO.Path.Combine( directory, "first.txt" );
		var second = System.IO.Path.Combine( directory, "second.txt" );
		await File.WriteAllTextAsync( first, "first-old\n" );
		await File.WriteAllTextAsync( second, "second-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var firstObservation = await fileSystem.ObserveAsync( first, followPathIndirection: false );
			var secondObservation = await fileSystem.ObserveAsync( second, followPathIndirection: false );
			var artifacts = new[] {
				CreateWriteArtifact( first, firstObservation, "first-new\n", "operation" ),
				CreateWriteArtifact( second, secondObservation, "second-new\n", "operation" )
			};
			var plan = new PatchArtifactPlan( artifacts, PatchExitStatus.Success, Array.Empty<string>(), directory );
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new ThrowingFailureInjector( PatchTransactionStage.Commit, occurrence: 2 )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( "first-old\n", await File.ReadAllTextAsync( first ) );
			Assert.Equal( "second-old\n", await File.ReadAllTextAsync( second ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies destination changes after staging are detected before replacement.</summary>
	[Fact]
	public async Task RevalidationRejectsDestinationChangedAfterStaging() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = await CreateWritePlanAsync( fileSystem, target, "new\n" );
			await using var transaction = await fileSystem.CreateTransactionAsync( plan );
			await transaction.StageAsync();
			await File.WriteAllTextAsync( target, "external-change-with-different-size\n" );
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Contains( "changed after staging", string.Join( "\n", result.Diagnostics ) );
			Assert.Equal( "external-change-with-different-size\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a validation-only input guard prevents committing stale output-mode results.</summary>
	[Fact]
	public async Task ValidationOnlyGuardDetectsInputChange() {
		var directory = CreateTemporaryDirectory();
		var input = System.IO.Path.Combine( directory, "input.txt" );
		var output = System.IO.Path.Combine( directory, "output.txt" );
		await File.WriteAllTextAsync( input, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var inputObservation = await fileSystem.ObserveAsync( input, followPathIndirection: false );
			var outputObservation = await fileSystem.ObserveAsync( output, followPathIndirection: false );
			var plan = new PatchArtifactPlan(
				new[] {
					new PatchArtifact(
						PatchArtifactKind.Target,
						PatchArtifactAction.ValidateOnly,
						input,
						null,
						inputObservation,
						new PatchArtifactMetadata(),
						input
					),
					CreateWriteArtifact( output, outputObservation, "new\n", input )
				},
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync( plan );
			await transaction.StageAsync();
			await File.WriteAllTextAsync( input, "external-change-with-different-size\n" );
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.False( File.Exists( output ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies cancellation after staging cleans temporary files and preserves the target.</summary>
	[Fact]
	public async Task CancellationCleansStagedFiles() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		using var cancellation = new CancellationTokenSource();
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = await CreateWritePlanAsync( fileSystem, target, "new\n" );
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new CancelingFailureInjector( PatchTransactionStage.Commit, cancellation )
			);
			await transaction.StageAsync( cancellation.Token );
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => transaction.CommitAsync( cancellation.Token )
			);
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies creation and deletion are both delegated to E6.</summary>
	[Fact]
	public async Task CommitsCreationAndDeletionThroughE6() {
		var directory = CreateTemporaryDirectory();
		var created = System.IO.Path.Combine( directory, "created.txt" );
		var deleted = System.IO.Path.Combine( directory, "deleted.txt" );
		await File.WriteAllTextAsync( deleted, "delete-me\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var createdObservation = await fileSystem.ObserveAsync( created, followPathIndirection: false );
			var deletedObservation = await fileSystem.ObserveAsync( deleted, followPathIndirection: false );
			var artifacts = new[] {
				CreateWriteArtifact( created, createdObservation, "created\n", "create" ),
				new PatchArtifact(
					PatchArtifactKind.Target,
					PatchArtifactAction.Delete,
					deleted,
					null,
					deletedObservation,
					new PatchArtifactMetadata(),
					deleted,
					"delete"
				)
			};
			var plan = new PatchArtifactPlan(
				artifacts,
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync( plan );
			var result = await transaction.CommitAsync();
			Assert.Equal( PatchTransactionOutcome.Succeeded, result.Outcome );
			Assert.Equal( "created\n", await File.ReadAllTextAsync( created ) );
			Assert.False( File.Exists( deleted ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a successful replacement retains the original at Patch's selected backup pathname.</summary>
	[Fact]
	public async Task RetainsBackupOnSuccessfulReplacement() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var backup = System.IO.Path.Combine( directory, "target.txt.orig" );
		await File.WriteAllTextAsync( target, "target-old\n" );
		await File.WriteAllTextAsync( backup, "backup-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var backupObservation = await fileSystem.ObserveAsync( backup, followPathIndirection: false );
			const string unit = "file";
			var plan = new PatchArtifactPlan(
				new[] {
					CreateWriteArtifact( target, targetObservation, "target-new\n", unit ),
					new PatchArtifact(
						PatchArtifactKind.Backup,
						PatchArtifactAction.Write,
						backup,
						PatchArtifactContent.FromExistingFile( target ),
						backupObservation,
						new PatchArtifactMetadata(),
						backup,
						unit
					)
				},
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync( plan );
			var result = await transaction.CommitAsync();
			Assert.Equal( PatchTransactionOutcome.Succeeded, result.Outcome );
			Assert.Equal( "target-new\n", await File.ReadAllTextAsync( target ) );
			Assert.Equal( "target-old\n", await File.ReadAllTextAsync( backup ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies backup-publication failure restores both the target and the previous backup.</summary>
	[Fact]
	public async Task BackupPublicationFailureRollsBackTargetAndBackup() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var backup = System.IO.Path.Combine( directory, "target.txt.orig" );
		await File.WriteAllTextAsync( target, "target-old\n" );
		await File.WriteAllTextAsync( backup, "backup-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var backupObservation = await fileSystem.ObserveAsync( backup, followPathIndirection: false );
			const string unit = "file";
			var plan = new PatchArtifactPlan(
				new[] {
					CreateWriteArtifact( target, targetObservation, "target-new\n", unit ),
					new PatchArtifact(
						PatchArtifactKind.Backup,
						PatchArtifactAction.Write,
						backup,
						PatchArtifactContent.FromExistingFile( target ),
						backupObservation,
						new PatchArtifactMetadata(),
						backup,
						unit
					)
				},
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new ThrowingFailureInjector( PatchTransactionStage.PublishBackup )
			);
			var result = await transaction.CommitAsync();
			Assert.Equal( PatchTransactionOutcome.FailedRolledBack, result.Outcome );
			Assert.Equal( "target-old\n", await File.ReadAllTextAsync( target ) );
			Assert.Equal( "backup-old\n", await File.ReadAllTextAsync( backup ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies retained backup, reject, output, and target recover together.</summary>
	[Fact]
	public async Task BackupRejectAndOutputRollbackWithTarget() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var backup = System.IO.Path.Combine( directory, "target.txt.orig" );
		var reject = System.IO.Path.Combine( directory, "target.txt.rej" );
		var output = System.IO.Path.Combine( directory, "output.txt" );
		await File.WriteAllTextAsync( target, "target-old\n" );
		await File.WriteAllTextAsync( backup, "backup-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var backupObservation = await fileSystem.ObserveAsync( backup, followPathIndirection: false );
			var rejectObservation = await fileSystem.ObserveAsync( reject, followPathIndirection: false );
			var outputObservation = await fileSystem.ObserveAsync( output, followPathIndirection: false );
			const string unit = "file";
			var artifacts = new[] {
				CreateWriteArtifact( target, targetObservation, "target-new\n", unit ),
				new PatchArtifact(
					PatchArtifactKind.Backup,
					PatchArtifactAction.Write,
					backup,
					PatchArtifactContent.FromExistingFile( target ),
					backupObservation,
					new PatchArtifactMetadata(),
					backup,
					unit
				),
				CreateWriteArtifact( reject, rejectObservation, "reject\n", unit, PatchArtifactKind.Reject ),
				CreateWriteArtifact( output, outputObservation, "output\n", unit, PatchArtifactKind.Output )
			};
			var plan = new PatchArtifactPlan(
				artifacts,
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new ThrowingFailureInjector( PatchTransactionStage.ApplyMetadata, occurrence: 3 )
			);
			var result = await transaction.CommitAsync();
			Assert.Equal( PatchTransactionOutcome.FailedRolledBack, result.Outcome );
			Assert.Equal( "target-old\n", await File.ReadAllTextAsync( target ) );
			Assert.Equal( "backup-old\n", await File.ReadAllTextAsync( backup ) );
			Assert.False( File.Exists( reject ) );
			Assert.False( File.Exists( output ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies an independent file can remain committed after a later unit fails.</summary>
	[Fact]
	public async Task IndependentFileFailureReportsPartialSuccess() {
		var directory = CreateTemporaryDirectory();
		var first = System.IO.Path.Combine( directory, "first.txt" );
		var second = System.IO.Path.Combine( directory, "second.txt" );
		await File.WriteAllTextAsync( first, "first-old\n" );
		await File.WriteAllTextAsync( second, "second-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var firstObservation = await fileSystem.ObserveAsync( first, followPathIndirection: false );
			var secondObservation = await fileSystem.ObserveAsync( second, followPathIndirection: false );
			var plan = new PatchArtifactPlan(
				new[] {
					CreateWriteArtifact( first, firstObservation, "first-new\n", "first" ),
					CreateWriteArtifact( second, secondObservation, "second-new\n", "second" )
				},
				PatchExitStatus.Success,
				Array.Empty<string>(),
				directory
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new ThrowingFailureInjector( PatchTransactionStage.Commit, occurrence: 2 )
			);
			var result = await transaction.CommitAsync();
			Assert.Equal( PatchTransactionOutcome.FailedPartiallyCommitted, result.Outcome );
			Assert.Equal( new[] { "first" }, result.CommittedUnitIds );
			Assert.Equal( "first-new\n", await File.ReadAllTextAsync( first ) );
			Assert.Equal( "second-old\n", await File.ReadAllTextAsync( second ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	private static async Task<PatchArtifactPlan> CreateWritePlanAsync(
		SystemPatchFileSystem fileSystem,
		string target,
		string value
	) {
		var observation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
		return new PatchArtifactPlan(
			new[] { CreateWriteArtifact( target, observation, value ) },
			PatchExitStatus.Success,
			Array.Empty<string>(),
			Path.GetDirectoryName( target )
		);
	}

	private static PatchArtifact CreateWriteArtifact(
		string target,
		PatchFileObservation observation,
		string value,
		string? transactionUnitId = null,
		PatchArtifactKind kind = PatchArtifactKind.Target
	) {
		return new PatchArtifact(
			kind,
			PatchArtifactAction.Write,
			target,
			PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( value ) ),
			observation,
			new PatchArtifactMetadata {
				Mode = observation.Mode ?? 0x01a4
			},
			target,
			transactionUnitId
		);
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-p11a-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}

	private static void AssertNoTemporaryFiles( string directory ) {
		Assert.DoesNotContain(
			Directory.EnumerateFiles( directory ),
			path => {
				var name = System.IO.Path.GetFileName( path );
				return name.Contains( ".patch-", StringComparison.Ordinal )
					|| name.Contains( ".icod-e6-", StringComparison.Ordinal );
			}
		);
	}

	private sealed class ThrowingFailureInjector : IPatchTransactionFailureInjector {
		private readonly PatchTransactionStage selectedStage;
		private readonly int occurrence;
		private int count;

		/// <summary>Initializes a deterministic throwing injector.</summary>
		public ThrowingFailureInjector( PatchTransactionStage selectedStage, int occurrence = 1 ) {
			this.selectedStage = selectedStage;
			this.occurrence = occurrence;
		}

		/// <inheritdoc/>
		public ValueTask OnStageAsync(
			PatchTransactionStage stage,
			PatchArtifact artifact,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			if ( this.selectedStage != stage ) {
				return ValueTask.CompletedTask;
			}
			this.count++;
			if ( this.occurrence == this.count ) {
				throw new IOException( string.Concat( "injected ", stage.ToString() ) );
			}
			return ValueTask.CompletedTask;
		}
	}

	private sealed class CancelingFailureInjector : IPatchTransactionFailureInjector {
		private readonly PatchTransactionStage selectedStage;
		private readonly CancellationTokenSource cancellation;

		/// <summary>Initializes a deterministic cancellation injector.</summary>
		public CancelingFailureInjector(
			PatchTransactionStage selectedStage,
			CancellationTokenSource cancellation
		) {
			this.selectedStage = selectedStage;
			this.cancellation = cancellation;
		}

		/// <inheritdoc/>
		public ValueTask OnStageAsync(
			PatchTransactionStage stage,
			PatchArtifact artifact,
			CancellationToken cancellationToken = default
		) {
			if ( this.selectedStage == stage ) {
				this.cancellation.Cancel();
			}
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}
	}
}
