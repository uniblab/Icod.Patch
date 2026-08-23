namespace Icod.Patch.Tests;

using System.IO;
using System.Text;
using Xunit;

/// <summary>Exercises Wave D transaction units, recovery outcomes, and the frozen E6 contract.</summary>
public sealed class WaveDTransactionTests {
	/// <summary>Verifies the Patch-facing E6 requirements and failure matrix are explicit.</summary>
	[Fact]
	public void E6RequirementsAreFrozen() {
		var contract = PatchE6TransactionContract.Current;
		Assert.Equal( PatchTransactionRecoveryScope.PatchFile, contract.RecoveryScope );
		Assert.Equal(
			PatchMultiFileCommitPolicy.PreserveCompletedUnits,
			contract.MultiFileCommitPolicy
		);
		Assert.True( contract.RequiresSecureSiblingTemporaries );
		Assert.True( contract.RequiresFlushBeforeCommit );
		Assert.True( contract.RequiresDeterministicCleanup );
		Assert.True( contract.RequiresCancellationRecovery );
		Assert.True( contract.RequiresAtomicityCapabilityReporting );
		Assert.Equal(
			Enum.GetValues<PatchTransactionStage>().OrderBy( value => value ),
			contract.RequiredFailureStages.OrderBy( value => value )
		);
		Assert.Contains( contract.Requirements, item => "containment" == item.Name );
		Assert.Contains( contract.Requirements, item => "multi-file-partial-success" == item.Name );
	}

	/// <summary>Verifies a later file-unit failure preserves a completed earlier file unit.</summary>
	[Fact]
	public async Task LaterFileFailurePreservesEarlierFileUnit() {
		var directory = CreateTemporaryDirectory();
		var first = System.IO.Path.Combine( directory, "first.txt" );
		var second = System.IO.Path.Combine( directory, "second.txt" );
		await File.WriteAllTextAsync( first, "first-old\n" );
		await File.WriteAllTextAsync( second, "second-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = new PatchArtifactPlan(
				new[] {
					await CreateWriteArtifactAsync( fileSystem, first, "first-new\n" ),
					await CreateWriteArtifactAsync( fileSystem, second, "second-new\n" )
				},
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector( (PatchTransactionStage.Commit, 2) )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedPartiallyCommitted, result.Outcome );
			Assert.True( result.HasPartialCommit );
			Assert.Contains( first, result.CommittedUnitIds );
			Assert.Equal( "first-new\n", await File.ReadAllTextAsync( first ) );
			Assert.Equal( "second-old\n", await File.ReadAllTextAsync( second ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}


	/// <summary>Verifies every pre-commit failure boundary leaves the destination unchanged.</summary>
	[Theory]
	[InlineData( PatchTransactionStage.Validate )]
	[InlineData( PatchTransactionStage.PreserveRollback )]
	[InlineData( PatchTransactionStage.CreateTemporary )]
	[InlineData( PatchTransactionStage.WriteTemporary )]
	[InlineData( PatchTransactionStage.FlushTemporary )]
	[InlineData( PatchTransactionStage.Revalidate )]
	[InlineData( PatchTransactionStage.Commit )]
	internal async Task PreCommitFailureMatrixPreservesDestination( PatchTransactionStage stage ) {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = new PatchArtifactPlan(
				new[] { await CreateWriteArtifactAsync( fileSystem, target, "new\n" ) },
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector( (stage, 1) )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedBeforeCommit, result.Outcome );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies cancellation after replacement recovers the active file unit and cleans staging.</summary>
	[Fact]
	public async Task CancellationAfterReplacementRollsBackActiveUnit() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		using var cancellation = new CancellationTokenSource();
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = new PatchArtifactPlan(
				new[] { await CreateWriteArtifactAsync( fileSystem, target, "new\n" ) },
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new CancelingFailureInjector( PatchTransactionStage.ApplyMetadata, cancellation )
			);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => transaction.CommitAsync( cancellation.Token )
			);
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies artifacts in one file unit recover together.</summary>
	[Fact]
	public async Task FileUnitFailureRollsBackEarlierArtifactInSameUnit() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var backup = System.IO.Path.Combine( directory, "target.txt.orig" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var backupObservation = await fileSystem.ObserveAsync( backup, followPathIndirection: false );
			var plan = new PatchArtifactPlan(
				new[] {
					new PatchArtifact(
						PatchArtifactKind.Backup,
						PatchArtifactAction.Write,
						backup,
						PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( "old\n" ) ),
						backupObservation,
						new PatchArtifactMetadata(),
						backup,
						target
					),
					CreateWriteArtifact( target, targetObservation, "new\n", target )
				},
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector( (PatchTransactionStage.Commit, 2) )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedRolledBack, result.Outcome );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			Assert.False( File.Exists( backup ) );
			Assert.Contains( target, result.RolledBackUnitIds );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies a staged flush failure leaves the original untouched.</summary>
	[Fact]
	public async Task FlushFailureOccursBeforeDestinationMutation() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = new PatchArtifactPlan(
				new[] { await CreateWriteArtifactAsync( fileSystem, target, "new\n" ) },
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector( (PatchTransactionStage.FlushTemporary, 1) )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedBeforeCommit, result.Outcome );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies rollback failures are surfaced while cleanup still runs.</summary>
	[Fact]
	public async Task RollbackFailureIsReportedAndCleanupContinues() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var companion = System.IO.Path.Combine( directory, "companion.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		await File.WriteAllTextAsync( companion, "companion-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var companionObservation = await fileSystem.ObserveAsync( companion, followPathIndirection: false );
			var plan = new PatchArtifactPlan(
				new[] {
					CreateWriteArtifact( target, targetObservation, "new\n", "unit" ),
					CreateWriteArtifact( companion, companionObservation, "companion-new\n", "unit" )
				},
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector(
					(PatchTransactionStage.Commit, 2),
					(PatchTransactionStage.Rollback, 1)
				)
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedRollbackIncomplete, result.Outcome );
			Assert.Contains( result.Diagnostics, value => value.Contains( "rollback failed", StringComparison.Ordinal ) );
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies metadata-restoration failure is reported after content recovery.</summary>
	[Fact]
	public async Task MetadataRestoreFailureReportsIncompleteRecovery() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		var companion = System.IO.Path.Combine( directory, "companion.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		await File.WriteAllTextAsync( companion, "companion-old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var targetObservation = await fileSystem.ObserveAsync( target, followPathIndirection: false );
			var companionObservation = await fileSystem.ObserveAsync( companion, followPathIndirection: false );
			var plan = new PatchArtifactPlan(
				new[] {
					CreateWriteArtifact( target, targetObservation, "new\n", "unit" ),
					CreateWriteArtifact( companion, companionObservation, "companion-new\n", "unit" )
				},
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector(
					(PatchTransactionStage.Commit, 2),
					(PatchTransactionStage.RestoreMetadata, 1)
				)
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedRollbackIncomplete, result.Outcome );
			Assert.Equal( "old\n", await File.ReadAllTextAsync( target ) );
			Assert.Contains(
				result.Diagnostics,
				value => value.Contains( "rollback failed", StringComparison.Ordinal )
			);
			AssertNoTemporaryFiles( directory );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Verifies cleanup failure is distinguished from commit failure.</summary>
	[Fact]
	public async Task CleanupFailureDoesNotUndoCommittedFileUnit() {
		var directory = CreateTemporaryDirectory();
		var target = System.IO.Path.Combine( directory, "target.txt" );
		await File.WriteAllTextAsync( target, "old\n" );
		try {
			var fileSystem = new SystemPatchFileSystem();
			var plan = new PatchArtifactPlan(
				new[] { await CreateWriteArtifactAsync( fileSystem, target, "new\n" ) },
				PatchExitStatus.Success,
				Array.Empty<string>()
			);
			await using var transaction = await fileSystem.CreateTransactionAsync(
				plan,
				new SelectedFailureInjector( (PatchTransactionStage.Cleanup, 1) )
			);
			var result = await transaction.CommitAsync();
			Assert.False( result.Succeeded );
			Assert.Equal( PatchTransactionOutcome.FailedCleanupIncomplete, result.Outcome );
			Assert.Equal( "new\n", await File.ReadAllTextAsync( target ) );
			Assert.Contains( target, result.CommittedUnitIds );
			AssertNoTemporaryFiles( directory );
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
		return CreateWriteArtifact( path, observation, value, path );
	}

	private static PatchArtifact CreateWriteArtifact(
		string path,
		PatchFileObservation observation,
		string value,
		string unit
	) {
		return new PatchArtifact(
			PatchArtifactKind.Target,
			PatchArtifactAction.Write,
			path,
			PatchArtifactContent.FromBytes( Encoding.UTF8.GetBytes( value ) ),
			observation,
			new PatchArtifactMetadata(),
			path,
			unit
		);
	}

	private static string CreateTemporaryDirectory() {
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-wave-d-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( path );
		return path;
	}

	private static void AssertNoTemporaryFiles( string directory ) {
		Assert.DoesNotContain(
			Directory.EnumerateFiles( directory ),
			path => System.IO.Path.GetFileName( path ).Contains( ".patch-", StringComparison.Ordinal )
		);
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
			this.cancellation = cancellation ?? throw new ArgumentNullException( nameof( cancellation ) );
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

	private sealed class SelectedFailureInjector : IPatchTransactionFailureInjector {
		private readonly Dictionary<PatchTransactionStage, Queue<int>> occurrences;
		private readonly Dictionary<PatchTransactionStage, int> counts = new();

		/// <summary>Initializes selected deterministic failure occurrences.</summary>
		public SelectedFailureInjector( params (PatchTransactionStage Stage, int Occurrence)[] failures ) {
			this.occurrences = failures
				.GroupBy( item => item.Stage )
				.ToDictionary(
					group => group.Key,
					group => new Queue<int>( group.Select( item => item.Occurrence ).OrderBy( value => value ) )
				);
		}

		/// <inheritdoc/>
		public ValueTask OnStageAsync(
			PatchTransactionStage stage,
			PatchArtifact artifact,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			var count = this.counts.TryGetValue( stage, out var prior ) ? prior + 1 : 1;
			this.counts[stage] = count;
			if ( this.occurrences.TryGetValue( stage, out var selected )
				&& 0 < selected.Count
				&& count == selected.Peek() ) {
				selected.Dequeue();
				throw new IOException( string.Concat( "injected ", stage.ToString() ) );
			}
			return ValueTask.CompletedTask;
		}
	}
}
