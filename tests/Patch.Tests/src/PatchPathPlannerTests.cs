namespace Icod.Patch.Tests;

using System.Text;
using Icod.Path;
using Xunit;

/// <summary>Exercises P7 filename selection, canonical paths, security, and multi-file state.</summary>
public sealed class PatchPathPlannerTests {
	private sealed class TestFileSystem : IPatchPathFileSystem {
		private readonly Dictionary<string, PathComponentObservation> entries;
		private readonly Dictionary<string, byte[]> contents;

		/// <summary>Initializes a deterministic filesystem.</summary>
		public TestFileSystem( PathPlatformSemantics semantics, string currentDirectory ) {
			this.Semantics = semantics;
			this.CurrentDirectory = currentDirectory;
			this.entries = new Dictionary<string, PathComponentObservation>( semantics.PathComparer );
			this.contents = new Dictionary<string, byte[]>( semantics.PathComparer );
			var normalized = PathLexicalNormalizer.Normalize( currentDirectory, currentDirectory, semantics );
			this.AddDirectory( normalized.Root!.RootPath );
		}

		/// <inheritdoc/>
		public PathPlatformSemantics Semantics { get; }
		/// <inheritdoc/>
		public string CurrentDirectory { get; }

		/// <summary>Adds a directory.</summary>
		public TestFileSystem AddDirectory( string path ) {
			this.entries[path] = PathComponentObservation.Existing(
				path,
				CanonicalPathEntryKind.Directory
			);
			return this;
		}

		/// <summary>Adds a UTF-8 regular file.</summary>
		public TestFileSystem AddFile( string path, string content ) {
			this.entries[path] = PathComponentObservation.Existing(
				path,
				CanonicalPathEntryKind.File
			);
			this.contents[path] = Encoding.UTF8.GetBytes( content );
			return this;
		}

		/// <summary>Adds a symbolic link.</summary>
		public TestFileSystem AddLink( string path, string target, bool reparsePoint = false ) {
			this.entries[path] = PathComponentObservation.Existing(
				path,
				CanonicalPathEntryKind.Unknown,
				isSymbolicLink: true,
				linkTarget: target,
				isReparsePoint: reparsePoint
			);
			return this;
		}

		/// <inheritdoc/>
		public ValueTask<PathComponentObservation> ObserveAsync(
			string path,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(
				this.entries.TryGetValue( path, out var observation )
					? observation
					: PathComponentObservation.Missing( path )
			);
		}

		/// <summary>Reads stored bytes without exposing mutable test state.</summary>
		public ValueTask<byte[]> ReadBytesAsync(
			string canonicalPath,
			CancellationToken cancellationToken = default
		) {
			ArgumentException.ThrowIfNullOrEmpty( canonicalPath );
			cancellationToken.ThrowIfCancellationRequested();
			if ( !this.contents.TryGetValue( canonicalPath, out var bytes ) ) {
				throw new FileNotFoundException( "the synthetic target is missing", canonicalPath );
			}
			return ValueTask.FromResult( bytes.ToArray() );
		}

		/// <inheritdoc/>
		public ValueTask<Stream> OpenReadAsync(
			string canonicalPath,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			if ( !this.contents.TryGetValue( canonicalPath, out var bytes ) ) {
				throw new FileNotFoundException( "the synthetic target is missing", canonicalPath );
			}
			Stream stream = new MemoryStream( bytes, writable: false );
			return ValueTask.FromResult( stream );
		}
	}

	private sealed class TestVersionControlProvider : IPatchVersionControlProvider {
		private readonly Dictionary<string, byte[]> contents;

		/// <summary>Initializes a provider for the selected path grammar.</summary>
		public TestVersionControlProvider( StringComparer comparer ) {
			this.contents = new Dictionary<string, byte[]>( comparer );
		}

		/// <summary>Adds retrievable UTF-8 content.</summary>
		public TestVersionControlProvider Add( string path, string content ) {
			this.contents[path] = Encoding.UTF8.GetBytes( content );
			return this;
		}

		/// <inheritdoc/>
		public ValueTask<bool> IsRetrievableAsync(
			string canonicalPath,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult( this.contents.ContainsKey( canonicalPath ) );
		}

		/// <inheritdoc/>
		public ValueTask<PatchVersionControlResult> RetrieveAsync(
			string canonicalPath,
			CancellationToken cancellationToken = default
		) {
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(
				this.contents.TryGetValue( canonicalPath, out var bytes )
					? new PatchVersionControlResult(
						PatchVersionControlOutcome.Retrieved,
						new MemoryStream( bytes, writable: false )
					)
					: new PatchVersionControlResult( PatchVersionControlOutcome.NotFound )
			);
		}
	}

	private sealed class FixedDecisionProvider : IPatchDecisionProvider {
		private readonly bool answer;

		/// <summary>Initializes a fixed policy answer.</summary>
		public FixedDecisionProvider( bool answer ) {
			this.answer = answer;
		}

		/// <summary>Gets the number of decisions requested.</summary>
		public int Calls { get; private set; }

		/// <inheritdoc/>
		public ValueTask<bool> DecideAsync(
			PatchDecisionRequest request,
			CancellationToken cancellationToken = default
		) {
			ArgumentNullException.ThrowIfNull( request );
			cancellationToken.ThrowIfCancellationRequested();
			this.Calls++;
			return ValueTask.FromResult( this.answer );
		}
	}

	/// <summary>Verifies GNU separator-run stripping, including repeated and alternate separators.</summary>
	[Theory]
	[InlineData( "/gnu/src/file", 0, "/gnu/src/file", false )]
	[InlineData( "/gnu/src/file", 1, "gnu/src/file", false )]
	[InlineData( "/gnu//src/file", 2, "src/file", false )]
	[InlineData( "a\\b/file", 2, "file", true )]
	[InlineData( "a/b/file", null, "file", false )]
	public void StripCountsSeparatorRuns( string name, int? count, string expected, bool windows ) {
		var semantics = windows ? PathPlatformSemantics.Windows : PathPlatformSemantics.Posix;
		Assert.Equal( expected, PatchPathSelection.Strip( name, count, semantics ) );
	}

	/// <summary>Verifies POSIX backslashes remain filename characters rather than separators.</summary>
	[Fact]
	public void PosixBackslashIsNotASeparator() {
		Assert.Equal(
			"a\\b",
			PatchPathSelection.Strip( "a\\b", null, PathPlatformSemantics.Posix )
		);
	}

	/// <summary>Verifies non-POSIX filename selection uses GNU's best-name ranking.</summary>
	[Fact]
	public async Task NonPosixSelectionUsesBestExistingName() {
		var fileSystem = PosixFileSystem()
			.AddDirectory( "/work/src" )
			.AddDirectory( "/work/src/deep" )
			.AddFile( "/work/src/deep/file.txt", "old\n" )
			.AddFile( "/work/new.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- src/deep/file.txt\n+++ new.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 0 }
		);
		Assert.Equal( PatchPathCandidateSource.NewHeader, plan.Files[0].SelectedCandidate!.Source );
		Assert.Equal( "/work/new.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( plan.Files[0].Result!.File ) );
	}

	/// <summary>Verifies POSIX mode selects the first existing old/new/index candidate.</summary>
	[Fact]
	public async Task PosixSelectionUsesFirstExistingName() {
		var fileSystem = PosixFileSystem()
			.AddDirectory( "/work/src" )
			.AddDirectory( "/work/src/deep" )
			.AddFile( "/work/src/deep/file.txt", "old\n" )
			.AddFile( "/work/new.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- src/deep/file.txt\n+++ new.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 0, Posix = true }
		);
		Assert.Equal( PatchPathCandidateSource.OldHeader, plan.Files[0].SelectedCandidate!.Source );
		Assert.Equal( "/work/src/deep/file.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies an explicit original operand supplies every patch section and carries virtual state forward.</summary>
	[Fact]
	public async Task ExplicitOperandCarriesStateAcrossPatchSections() {
		var fileSystem = PosixFileSystem().AddFile( "/work/target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- a\n+++ b\n@@ -1 +1 @@\n-old\n+middle\n"
			+ "--- b\n+++ c\n@@ -1 +1 @@\n-middle\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { OriginalFile = "target.txt" }
		);
		Assert.Equal( 2, plan.Files.Count );
		Assert.All(
			plan.Files,
			value => Assert.Equal( PatchPathCandidateSource.ExplicitOperand, value.SelectedCandidate!.Source )
		);
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( plan.Files[1].Result!.File ) );
	}

	/// <summary>Verifies an explicit missing target remains authoritative for file creation in POSIX mode.</summary>
	[Fact]
	public async Task ExplicitOperandSelectsMissingCreationTargetInPosixMode() {
		var fileSystem = PosixFileSystem();
		await using var parsed = await ParseAsync(
			"--- /dev/null\n+++ ignored.txt\n@@ -0,0 +1 @@\n+created\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions {
				OriginalFile = "chosen.txt",
				Posix = true
			}
		);
		Assert.Equal( "/work/chosen.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
		Assert.Equal( PatchPlannedFileAction.Create, plan.Files[0].Action );
	}

	/// <summary>Verifies <c>-d</c>, quoted names, and component stripping are combined through E2.</summary>
	[Fact]
	public async Task DirectoryQuotedNameAndStripAreCombined() {
		var fileSystem = PosixFileSystem()
			.AddDirectory( "/work/tree" )
			.AddFile( "/work/tree/file name.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- \"a/file name.txt\"\n+++ \"b/file name.txt\"\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { Directory = "tree", StripCount = 1 }
		);
		Assert.Equal( "/work/tree/file name.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies an Index record supplies headerless normal-format target evidence.</summary>
	[Fact]
	public async Task IndexLineSuppliesHeaderlessTarget() {
		var fileSystem = PosixFileSystem().AddFile( "/work/target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"Index: target.txt\n1c1\n< old\n---\n> new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions()
		);
		Assert.Equal( PatchPathCandidateSource.Index, plan.Files[0].SelectedCandidate!.Source );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( plan.Files[0].Result!.File ) );
	}

	/// <summary>Verifies POSIX mode considers an <c>Index:</c> name after missing header names.</summary>
	[Fact]
	public async Task PosixSelectionConsidersIndexAfterHeaders() {
		var fileSystem = PosixFileSystem().AddFile( "/work/actual.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"Index: actual.txt\n--- missing-old.txt\n+++ missing-new.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { Posix = true }
		);
		Assert.Equal( PatchPathCandidateSource.Index, plan.Files[0].SelectedCandidate!.Source );
		Assert.Equal( "/work/actual.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies quoted <c>Index:</c> evidence is decoded exactly once.</summary>
	[Fact]
	public async Task QuotedIndexNameIsDecodedOnce() {
		var fileSystem = PosixFileSystem().AddFile( "/work/\"target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"Index: \"\\\"target.txt\"\n1c1\n< old\n---\n> new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions()
		);
		Assert.Equal( "/work/\"target.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies creation and deletion remain virtual plan actions.</summary>
	[Fact]
	public async Task CreationAndDeletionArePlannedWithoutMutation() {
		var fileSystem = PosixFileSystem()
			.AddDirectory( "/work/new" )
			.AddFile( "/work/delete.txt", "gone\n" );
		await using var parsed = await ParseAsync(
			"--- /dev/null\n+++ new/create.txt\n@@ -0,0 +1 @@\n+created\n"
			+ "--- delete.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 0 }
		);
		Assert.Equal( PatchPlannedFileAction.Create, plan.Files[0].Action );
		Assert.True( plan.Files[0].Result!.File.Exists );
		Assert.Equal( PatchPlannedFileAction.Delete, plan.Files[1].Action );
		Assert.False( plan.Files[1].Result!.File.Exists );
		Assert.Equal( "gone\n", Encoding.UTF8.GetString( await fileSystem.ReadBytesAsync( "/work/delete.txt" ) ) );
	}

	/// <summary>Verifies lexical parent traversal is rejected before target acquisition.</summary>
	[Fact]
	public async Task ParentTraversalIsRejected() {
		var fileSystem = PosixFileSystem().AddFile( "/escape.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- ../escape.txt\n+++ ../escape.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 0 }
		);
		Assert.Equal( PatchExitStatus.PartialFailure, plan.Status );
		Assert.Null( plan.Files[0].SelectedCandidate );
		Assert.Contains(
			plan.Files[0].Candidates,
			value => null != value.Failure && value.Failure.Message.Contains( "escapes", StringComparison.Ordinal )
		);
	}

	/// <summary>Verifies physical containment rejects an intermediate link that escapes the working root.</summary>
	[Fact]
	public async Task SymlinkEscapeIsRejected() {
		var fileSystem = PosixFileSystem()
			.AddDirectory( "/outside" )
			.AddFile( "/outside/file.txt", "old\n" )
			.AddLink( "/work/link", "/outside" );
		await using var parsed = await ParseAsync(
			"--- link/file.txt\n+++ link/file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 0, FollowSymbolicLinks = true }
		);
		Assert.Equal( PatchExitStatus.PartialFailure, plan.Status );
		Assert.Null( plan.Files[0].SelectedCandidate );
	}

	/// <summary>Verifies terminal links require the explicit GNU follow policy.</summary>
	[Fact]
	public async Task TerminalLinkRequiresFollowSymlinks() {
		var fileSystem = PosixFileSystem()
			.AddFile( "/work/real.txt", "old\n" )
			.AddLink( "/work/target.txt", "real.txt" );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var rejected = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions()
		);
		Assert.Null( rejected.Files[0].SelectedCandidate );
		await using var followed = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { FollowSymbolicLinks = true }
		);
		Assert.Equal( "/work/real.txt", followed.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies positive retrieval supplies a virtual target without shelling out or writing it.</summary>
	[Fact]
	public async Task VersionControlProviderSuppliesMissingTarget() {
		var fileSystem = PosixFileSystem();
		var versionControl = new TestVersionControlProvider( StringComparer.Ordinal )
			.Add( "/work/target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem, versionControl ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { Get = 1 }
		);
		Assert.True( plan.Files[0].RetrievedFromVersionControl );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( plan.Files[0].Result!.File ) );
	}

	/// <summary>Verifies revision-control lookup follows stable old, new, and index candidate order.</summary>
	[Fact]
	public async Task VersionControlLookupUsesCandidateOrder() {
		var fileSystem = PosixFileSystem();
		var versionControl = new TestVersionControlProvider( StringComparer.Ordinal )
			.Add( "/work/long/path/old.txt", "old\n" )
			.Add( "/work/new.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- long/path/old.txt\n+++ new.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem, versionControl ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { Get = 1, StripCount = 0 }
		);
		Assert.Equal( PatchPathCandidateSource.OldHeader, plan.Files[0].SelectedCandidate!.Source );
		Assert.Equal( "/work/long/path/old.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies negative retrieval policy requires an injected affirmative decision.</summary>
	[Fact]
	public async Task NegativeRetrievalPolicyRequiresDecision() {
		var fileSystem = PosixFileSystem();
		var versionControl = new TestVersionControlProvider( StringComparer.Ordinal )
			.Add( "/work/target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var declined = await new PatchApplicationPlanner( fileSystem, versionControl ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions {
				Get = -1,
				EngineOptions = new PatchEngineOptions {
					DecisionProvider = new FixedDecisionProvider( false )
				}
			}
		);
		Assert.Null( declined.Files[0].SelectedCandidate );
		await using var accepted = await new PatchApplicationPlanner( fileSystem, versionControl ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions {
				Get = -1,
				EngineOptions = new PatchEngineOptions {
					DecisionProvider = new FixedDecisionProvider( true )
				}
			}
		);
		Assert.True( accepted.Files[0].RetrievedFromVersionControl );
	}

	/// <summary>Verifies negative retrieval policy asks only after a revision-control master is found.</summary>
	[Fact]
	public async Task NegativeRetrievalDoesNotAskWithoutMaster() {
		var fileSystem = PosixFileSystem();
		var versionControl = new TestVersionControlProvider( StringComparer.Ordinal );
		var decisions = new FixedDecisionProvider( true );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem, versionControl ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions {
				Get = -1,
				EngineOptions = new PatchEngineOptions { DecisionProvider = decisions }
			}
		);
		Assert.Equal( 0, decisions.Calls );
		Assert.Null( plan.Files[0].SelectedCandidate );
	}

	/// <summary>Verifies per-file application status is accumulated across a multi-file stream.</summary>
	[Fact]
	public async Task MultiFileStatusAggregatesPartialFailure() {
		var fileSystem = PosixFileSystem()
			.AddFile( "/work/one.txt", "old\n" )
			.AddFile( "/work/two.txt", "different\n" );
		await using var parsed = await ParseAsync(
			"--- one.txt\n+++ one.txt\n@@ -1 +1 @@\n-old\n+new\n"
			+ "--- two.txt\n+++ two.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions()
		);
		Assert.Equal( PatchExitStatus.Success, plan.Files[0].Status );
		Assert.Equal( PatchExitStatus.PartialFailure, plan.Files[1].Status );
		Assert.Equal( PatchExitStatus.PartialFailure, plan.Status );
	}

	/// <summary>Verifies reparse-point targets use the same explicit follow policy as symbolic links.</summary>
	[Fact]
	public async Task TerminalReparsePointRequiresFollowSymlinks() {
		var fileSystem = new TestFileSystem( PathPlatformSemantics.Windows, @"C:\work" )
			.AddDirectory( @"C:\work" )
			.AddFile( @"C:\work\real.txt", "old\n" )
			.AddLink( @"C:\work\target.txt", "real.txt", reparsePoint: true );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var rejected = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions()
		);
		Assert.Null( rejected.Files[0].SelectedCandidate );
		await using var followed = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { FollowSymbolicLinks = true }
		);
		Assert.Equal( @"C:\work\real.txt", followed.Files[0].SelectedCandidate!.CanonicalPath );
	}

	/// <summary>Verifies link loops fail deterministically when following is enabled.</summary>
	[Fact]
	public async Task LinkLoopFailsDeterministically() {
		var fileSystem = PosixFileSystem()
			.AddLink( "/work/a", "b" )
			.AddLink( "/work/b", "a" );
		await using var parsed = await ParseAsync(
			"--- a\n+++ a\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { FollowSymbolicLinks = true }
		);
		Assert.Null( plan.Files[0].SelectedCandidate );
		Assert.Contains(
			plan.Files[0].Candidates,
			value => CanonicalPathFailureCode.SymbolicLinkLoop == value.Failure?.Code
				|| CanonicalPathFailureCode.TooManySymbolicLinks == value.Failure?.Code
		);
	}

	/// <summary>Verifies planner cancellation is propagated before any mutation boundary.</summary>
	[Fact]
	public async Task CancellationIsPropagated() {
		var fileSystem = PosixFileSystem().AddFile( "/work/target.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => new PatchApplicationPlanner( fileSystem ).BuildAsync(
				parsed.Source,
				parsed.Document,
				new PatchPathPlanningOptions(),
				cancellation.Token
			)
		);
	}

	/// <summary>Verifies an explicit operand cannot bypass the canonical working-root boundary.</summary>
	[Fact]
	public async Task ExplicitAbsoluteOperandOutsideWorkingRootIsRejected() {
		var fileSystem = PosixFileSystem().AddFile( "/outside.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- ignored\n+++ ignored\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { OriginalFile = "/outside.txt" }
		);
		Assert.Null( plan.Files[0].SelectedCandidate );
	}

	/// <summary>Verifies Windows alternate separators are accepted while another volume is rejected.</summary>
	[Fact]
	public async Task WindowsSeparatorsAndVolumesUseSharedSemantics() {
		var fileSystem = new TestFileSystem( PathPlatformSemantics.Windows, @"C:\work" )
			.AddDirectory( @"C:\work" )
			.AddFile( @"C:\work\file.txt", "old\n" );
		await using var parsed = await ParseAsync(
			"--- a/b/file.txt\n+++ a/b/file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var plan = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			parsed.Source,
			parsed.Document,
			new PatchPathPlanningOptions { StripCount = 2 }
		);
		Assert.Equal( @"C:\work\file.txt", plan.Files[0].SelectedCandidate!.CanonicalPath );

		await using var crossVolume = await ParseAsync(
			"--- D:\\file.txt\n+++ D:\\file.txt\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var rejected = await new PatchApplicationPlanner( fileSystem ).BuildAsync(
			crossVolume.Source,
			crossVolume.Document,
			new PatchPathPlanningOptions { StripCount = 0 }
		);
		Assert.Null( rejected.Files[0].SelectedCandidate );
	}

	private static TestFileSystem PosixFileSystem() => new TestFileSystem(
		PathPlatformSemantics.Posix,
		"/work"
	).AddDirectory( "/work" );

	private static async Task<ParsedPatch> ParseAsync( string text ) {
		var stream = new MemoryStream( Encoding.UTF8.GetBytes( text ), writable: false );
		var source = await PatchSource.ReadAsync( stream );
		await stream.DisposeAsync();
		var scan = PatchScanner.Detect( source.Records, source.Probes );
		var document = await PatchDocumentParser.ParseAsync(
			source,
			scan,
			PatchParseLimits.Default
		);
		return new ParsedPatch( source, document );
	}

	private sealed class ParsedPatch : IAsyncDisposable {
		/// <summary>Initializes retained source and syntax models.</summary>
		public ParsedPatch( PatchSource source, PatchDocument document ) {
			this.Source = source;
			this.Document = document;
		}
		/// <summary>Gets the retained patch source.</summary>
		public PatchSource Source { get; }
		/// <summary>Gets the parsed patch document.</summary>
		public PatchDocument Document { get; }
		/// <inheritdoc/>
		public ValueTask DisposeAsync() => this.Source.DisposeAsync();
	}
}
