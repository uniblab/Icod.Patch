namespace Icod.Patch.Tests;

using Xunit;

/// <summary>Exercises pure P5/P6 target application and GNU-style matching policy.</summary>
public sealed class PatchApplicationEngineTests {
	private sealed class FixedDecisionProvider : IPatchDecisionProvider {
		private readonly bool decision;

		/// <summary>Initializes a provider that always returns one decision.</summary>
		/// <param name="decision">The decision result.</param>
		public FixedDecisionProvider( bool decision ) {
			this.decision = decision;
		}

		/// <inheritdoc/>
		public ValueTask<bool> DecideAsync(
			PatchDecisionRequest request,
			CancellationToken cancellationToken = default
		) {
			_ = request;
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult( this.decision );
		}
	}

	/// <summary>Verifies an exact unified replacement produces independent output.</summary>
	[Fact]
	public async Task ExactUnifiedHunkAppliesWithoutMutatingInput() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( PatchHunkOutcome.Applied, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
		Assert.Equal( "old\n", await PatchTestSupport.ReadTextAsync( input ) );
	}

	/// <summary>Verifies exact context and normal operations share the common application engine.</summary>
	[Theory]
	[InlineData( "*** old\n--- new\n***************\n*** 1 ****\n! old\n--- 1 ----\n! new\n", "old\n", "new\n" )]
	[InlineData( "1a2\n> added\n", "first\n", "first\nadded\n" )]
	[InlineData( "1c1\n< old\n---\n> new\n", "old\n", "new\n" )]
	[InlineData( "1d0\n< removed\n", "removed\n", "" )]
	public async Task ExactContextAndNormalOperationsApply(
		string patchText,
		string targetText,
		string expectedText
	) {
		var document = await PatchTestSupport.ParseAsync( patchText );
		await using var input = await PatchTestSupport.ExistingAsync( targetText );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( expectedText, await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies patch-provided CRLF records and incomplete final records remain byte exact.</summary>
	[Theory]
	[InlineData( "--- old\r\n+++ new\r\n@@ -1 +1 @@\r\n-old\r\n+new\r\n", "old\r\n", "new\r\n" )]
	[InlineData( "--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new", "old\n", "new" )]
	public async Task AppliedRecordsPreservePatchTerminators(
		string patchText,
		string targetText,
		string expectedText
	) {
		var document = await PatchTestSupport.ParseAsync( patchText );
		await using var input = await PatchTestSupport.ExistingAsync( targetText );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		Assert.Equal( expectedText, await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies result content remains usable after the independently owned input is disposed.</summary>
	[Fact]
	public async Task ResultOwnershipIsIndependentFromInput() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		var input = await PatchTestSupport.ExistingAsync( "old\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		await input.DisposeAsync();
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies candidate search prefers a nearby forward offset.</summary>
	[Fact]
	public async Task HunkSearchReportsForwardOffset() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "zero\nnoise\nold\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync( input, Assert.Single( document.Files ) );
		var hunk = Assert.Single( result.Hunks );
		Assert.Equal( 2L, hunk.Offset );
		Assert.Equal( "zero\nnoise\nnew\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies equally near matches prefer the forward candidate, matching GNU search order.</summary>
	[Fact]
	public async Task EquidistantMatchesPreferForwardCandidate() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -3 +3 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync(
			"zero\nold\nmiddle\nold\nend\n"
		);
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		var hunk = Assert.Single( result.Hunks );
		Assert.Equal( 1L, hunk.Offset );
		Assert.Equal( "zero\nold\nmiddle\nnew\nend\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies candidate search can move backward when no forward match exists.</summary>
	[Fact]
	public async Task HunkSearchReportsBackwardOffset() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -3 +3 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\nnoise\nend\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files )
		);
		var hunk = Assert.Single( result.Hunks );
		Assert.Equal( -2L, hunk.Offset );
		Assert.Equal( "new\nnoise\nend\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies exact matching at an offset is preferred before fuzz at the predicted location.</summary>
	[Fact]
	public async Task ExactOffsetMatchPrecedesPredictedFuzzMatch() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,3 +1,3 @@\n",
				" head\n-old\n+new\n tail\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync(
			string.Concat(
				"different-head\nold\ndifferent-tail\nnoise\n",
				"head\nold\ntail\n"
			)
		);
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Fuzz = 1 }
		);
		var hunk = Assert.Single( result.Hunks );
		Assert.Equal( 4L, hunk.Offset );
		Assert.Equal( 0, hunk.Fuzz );
		Assert.Equal(
			string.Concat(
				"different-head\nold\ndifferent-tail\nnoise\n",
				"head\nnew\ntail\n"
			),
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies cumulative line-count changes feed the next predicted hunk position.</summary>
	[Fact]
	public async Task MultipleHunksAccumulateLineDelta() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n",
				"@@ -1,0 +2 @@\n+inserted\n",
				"@@ -3 +4 @@\n-c\n+C\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a\nb\nc\nd\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync( input, Assert.Single( document.Files ) );
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "a\ninserted\nb\nC\nd\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies fuzz ignores unmatched outer context while preserving actual target context.</summary>
	[Fact]
	public async Task FuzzPreservesUnmatchedOuterContext() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,3 +1,3 @@\n",
				" head\n-old\n+new\n tail\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "different-head\nold\ndifferent-tail\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Fuzz = 1 }
		);
		var hunk = Assert.Single( result.Hunks );
		Assert.Equal( 1, hunk.Fuzz );
		Assert.Equal( "different-head\nnew\ndifferent-tail\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies canonical horizontal blank-run comparison.</summary>
	[Fact]
	public async Task IgnoreWhitespaceCanonicalizesNonemptyBlankRuns() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-a b\n+changed\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a \t  b\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { IgnoreWhitespace = true }
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "changed\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies batch mode automatically accepts a clearly reversed first hunk.</summary>
	[Fact]
	public async Task BatchModeAutomaticallyReversesFirstHunk() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "new\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Batch = true }
		);
		Assert.Equal( PatchDirection.Reverse, result.Direction );
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "old\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies an already-applied creation patch can reverse to virtual deletion in batch mode.</summary>
	[Fact]
	public async Task BatchModeReversesAlreadyAppliedCreation() {
		var document = await PatchTestSupport.ParseAsync(
			"--- /dev/null\n+++ created\n@@ -0,0 +1 @@\n+created\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "created\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Batch = true }
		);
		Assert.Equal( PatchDirection.Reverse, result.Direction );
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.False( result.File.Exists );
	}

	/// <summary>Verifies forward-only mode skips an already-applied patch.</summary>
	[Fact]
	public async Task ForwardOnlySkipsAlreadyAppliedPatch() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "new\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { ForwardOnly = true }
		);
		Assert.Equal( PatchExitStatus.PartialFailure, result.Status );
		Assert.Equal( PatchHunkOutcome.Skipped, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies force mode suppresses automatic reversal.</summary>
	[Fact]
	public async Task ForceSuppressesAutomaticReversal() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "new\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true }
		);
		Assert.Equal( PatchDirection.Forward, result.Direction );
		Assert.Equal( PatchHunkOutcome.Failed, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies an injected interactive decision can accept reversal.</summary>
	[Fact]
	public async Task DecisionProviderCanAcceptReversal() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "new\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { DecisionProvider = new FixedDecisionProvider( true ) }
		);
		Assert.Equal( PatchDirection.Reverse, result.Direction );
		Assert.Equal( "old\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies ed append/change/delete operations are interpreted internally.</summary>
	[Fact]
	public async Task EdScriptAppliesWithoutAnExternalEditor() {
		var document = await PatchTestSupport.ParseAsync( "2c\nnew\n.\n", PatchFormat.EdScript );
		await using var input = await PatchTestSupport.ExistingAsync( "a\nb\nc\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync( input, Assert.Single( document.Files ) );
		Assert.Equal( "a\nnew\nc\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies virtual creation and deletion semantics.</summary>
	[Fact]
	public async Task NullDeviceHeadersCreateAndDeleteVirtualFiles() {
		var createDocument = await PatchTestSupport.ParseAsync(
			"--- /dev/null\n+++ created\n@@ -0,0 +1 @@\n+created\n"
		);
		await using var missing = new PatchVirtualFile( false, null );
		await using var created = await PatchApplicationEngine.ApplyAsync( missing, Assert.Single( createDocument.Files ) );
		Assert.True( created.File.Exists );
		Assert.Equal( "created\n", await PatchTestSupport.ReadTextAsync( created.File ) );

		var deleteDocument = await PatchTestSupport.ParseAsync(
			"--- removed\n+++ /dev/null\n@@ -1 +0,0 @@\n-removed\n"
		);
		await using var existing = await PatchTestSupport.ExistingAsync( "removed\n" );
		await using var deleted = await PatchApplicationEngine.ApplyAsync( existing, Assert.Single( deleteDocument.Files ) );
		Assert.False( deleted.File.Exists );
		Assert.Null( deleted.File.Content );
	}

	/// <summary>Verifies two-way merge output omits the common-ancestor section.</summary>
	[Fact]
	public async Task TwoWayMergeWritesConflictMarkers() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "current\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Merge }
		);
		Assert.Equal( PatchHunkOutcome.Merged, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal(
			"<<<<<<<\n=======\nnew\n>>>>>>>\ncurrent\n",
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies diff3 merge markers include current, base, and new sides.</summary>
	[Fact]
	public async Task Diff3MergeWritesConflictMarkers() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "current\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal( PatchHunkOutcome.Merged, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal(
			"<<<<<<<\n|||||||\nold\n=======\nnew\n>>>>>>>\ncurrent\n",
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies merge mode takes precedence over automatic already-applied reversal.</summary>
	[Fact]
	public async Task MergeModeSuppressesAutomaticReversal() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "new\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Batch = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal( PatchDirection.Forward, result.Direction );
		Assert.Equal( PatchHunkOutcome.Merged, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal(
			"<<<<<<<\n|||||||\nold\n=======\nnew\n>>>>>>>\nnew\n",
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies trailing context anchors the current side of a diff3 conflict.</summary>
	[Fact]
	public async Task Diff3MergeUsesContextAnchors() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,3 +1,3 @@\n",
				" before\n-old\n+new\n after\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "before\ncurrent\nafter\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal(
			"before\n<<<<<<<\ncurrent\n|||||||\nold\n=======\nnew\n>>>>>>>\nafter\n",
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies merge alignment retains input-only lines between matching context anchors.</summary>
	[Fact]
	public async Task Diff3MergeRetainsInsertedCurrentLinesBetweenAnchors() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,3 +1,3 @@\n",
				" a\n-old\n+new\n b\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a\nlocal\nold\nb\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal(
			string.Concat(
				"a\n",
				"<<<<<<<\n",
				"local\n",
				"old\n",
				"|||||||\n",
				"old\n",
				"=======\n",
				"new\n",
				">>>>>>>\n",
				"b\n"
			),
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies a leading-only end-of-file anchor keeps its agreed prefix outside the conflict.</summary>
	[Fact]
	public async Task Diff3MergeUsesLeadingContextAtEndOfFile() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,2 +1,2 @@\n",
				" a\n-old\n+new\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a\ncurrent\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal(
			string.Concat(
				"a\n",
				"<<<<<<<\n",
				"current\n",
				"|||||||\n",
				"old\n",
				"=======\n",
				"new\n",
				">>>>>>>\n"
			),
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies conflict markers begin on new records after incomplete current and patch lines.</summary>
	[Fact]
	public async Task Diff3MergeTerminatesIncompleteConflictSides() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,2 +1,2 @@\n",
				" a\n-old\n\\ No newline at end of file\n",
				"+new\n\\ No newline at end of file\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a\ncurrent" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal(
			string.Concat(
				"a\n",
				"<<<<<<<\n",
				"current\n",
				"|||||||\n",
				"old\n",
				"=======\n",
				"new\n",
				">>>>>>>\n"
			),
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies an unmatched trailing anchor remains after a diff3 conflict.</summary>
	[Fact]
	public async Task Diff3MergeLeavesUnmatchedTrailingTargetAfterConflict() {
		var document = await PatchTestSupport.ParseAsync(
			string.Concat(
				"--- old\n+++ new\n@@ -1,3 +1,3 @@\n",
				" before\n-old\n+new\n after\n"
			)
		);
		await using var input = await PatchTestSupport.ExistingAsync(
			"before\ncurrent\ndifferent-after\n"
		);
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, MergeStyle = PatchMergeStyle.Diff3 }
		);
		Assert.Equal(
			string.Concat(
				"before\n",
				"<<<<<<<\n",
				"|||||||\n",
				"old\n",
				"after\n",
				"=======\n",
				"new\n",
				"after\n",
				">>>>>>>\n",
				"current\n",
				"different-after\n"
			),
			await PatchTestSupport.ReadTextAsync( result.File )
		);
	}

	/// <summary>Verifies a present prerequisite token permits ordinary application.</summary>
	[Fact]
	public async Task PresentPrerequisitePermitsApplication() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -2 +2 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "revision-2\nold\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { PrerequisiteToken = "revision-2" }
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "revision-2\nnew\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies force policy ignores a missing prerequisite token.</summary>
	[Fact]
	public async Task ForceIgnoresMissingPrerequisite() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Force = true, PrerequisiteToken = "revision-2" }
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies an injected decision can accept a missing prerequisite token.</summary>
	[Fact]
	public async Task DecisionProviderCanIgnoreMissingPrerequisite() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions {
				PrerequisiteToken = "revision-2",
				DecisionProvider = new FixedDecisionProvider( true )
			}
		);
		Assert.Equal( PatchExitStatus.Success, result.Status );
		Assert.Equal( "new\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies missing prerequisites follow batch skip policy.</summary>
	[Fact]
	public async Task MissingPrerequisiteIsSkippedInBatchMode() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\n" );
		await using var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions { Batch = true, PrerequisiteToken = "revision-2" }
		);
		Assert.Equal( PatchExitStatus.Trouble, result.Status );
		Assert.Equal( PatchHunkOutcome.Skipped, Assert.Single( result.Hunks ).Outcome );
		Assert.Equal( "old\n", await PatchTestSupport.ReadTextAsync( result.File ) );
	}

	/// <summary>Verifies spill-backed input and output support exact application without materializing the file model.</summary>
	[Fact]
	public async Task SpillBackedTargetAppliesAndCleansResultStorage() {
		var original = string.Concat(
			string.Join( "\n", Enumerable.Range( 0, 512 ).Select( value => string.Concat( "line-", value ) ) ),
			"\n"
		);
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -257 +257 @@\n-line-256\n+replacement\n"
		);
		var targetLimits = new PatchTargetLimits {
			MemoryThresholdBytes = 32,
			MaximumBytes = 64 * 1024,
			MaximumRecords = 1024
		};
		await using var input = await PatchTestSupport.ExistingAsync( original, targetLimits );
		Assert.True( input.Content!.IsSpillBacked );
		string? resultPath = null;
		await using ( var result = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			new PatchEngineOptions {
				Limits = new PatchApplicationLimits {
					MaximumCandidateChecks = 2048,
					MaximumOutputBytes = 64 * 1024,
					TargetLimits = targetLimits
				}
			}
		) ) {
			Assert.True( result.File.Content!.IsSpillBacked );
			resultPath = result.File.Content.TemporaryPath;
			Assert.NotNull( resultPath );
			Assert.Contains( "replacement\n", await PatchTestSupport.ReadTextAsync( result.File ) );
		}
		Assert.False( File.Exists( resultPath! ) );
	}

	/// <summary>Verifies patched output byte and record limits fail before result publication.</summary>
	[Theory]
	[InlineData( 3L, 10 )]
	[InlineData( 1024L, 1 )]
	public async Task OutputLimitsAreEnforced( long maximumBytes, int maximumRecords ) {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1,0 +2 @@\n+added\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "first\n" );
		await Assert.ThrowsAsync<PatchApplicationException>(
			() => PatchApplicationEngine.ApplyAsync(
				input,
				Assert.Single( document.Files ),
				new PatchEngineOptions {
					Limits = new PatchApplicationLimits {
						MaximumCandidateChecks = 10,
						MaximumOutputBytes = maximumBytes,
						TargetLimits = new PatchTargetLimits {
							MemoryThresholdBytes = 16,
							MaximumBytes = 1024,
							MaximumRecords = maximumRecords
						}
					}
				}
			)
		);
	}

	/// <summary>Verifies bounded candidate work prevents adversarial scans.</summary>
	[Fact]
	public async Task CandidateLimitIsEnforced() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-missing\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "a\nb\nc\n" );
		await Assert.ThrowsAsync<PatchApplicationException>(
			() => PatchApplicationEngine.ApplyAsync(
				input,
				Assert.Single( document.Files ),
				new PatchEngineOptions {
					Force = true,
					Limits = new PatchApplicationLimits {
						MaximumCandidateChecks = 1,
						MaximumOutputBytes = 1024,
						TargetLimits = PatchTargetLimits.Default
					}
				}
			)
		);
	}

	/// <summary>Exercises deterministic randomized exact replacements and input immutability.</summary>
	[Fact]
	public async Task RandomizedExactReplacementsPreserveUntouchedRecords() {
		var random = new Random( 1701 );
		for ( var iteration = 0; iteration < 64; iteration++ ) {
			var count = random.Next( 1, 24 );
			var selected = random.Next( count );
			var original = Enumerable.Range( 0, count ).Select( value => string.Concat( "line-", value ) ).ToArray();
			var replacement = string.Concat( "replacement-", iteration );
			var patchText = string.Concat(
				"--- old\n+++ new\n@@ -",
				( selected + 1 ).ToString( System.Globalization.CultureInfo.InvariantCulture ),
				" +",
				( selected + 1 ).ToString( System.Globalization.CultureInfo.InvariantCulture ),
				" @@\n-",
				original[selected],
				"\n+",
				replacement,
				"\n"
			);
			var originalText = string.Concat( string.Join( "\n", original ), "\n" );
			var expected = original.ToArray();
			expected[selected] = replacement;
			var expectedText = string.Concat( string.Join( "\n", expected ), "\n" );
			var document = await PatchTestSupport.ParseAsync( patchText );
			await using var input = await PatchTestSupport.ExistingAsync( originalText );
			await using var result = await PatchApplicationEngine.ApplyAsync( input, Assert.Single( document.Files ) );
			Assert.Equal( expectedText, await PatchTestSupport.ReadTextAsync( result.File ) );
			Assert.Equal( originalText, await PatchTestSupport.ReadTextAsync( input ) );
		}
	}

	/// <summary>Verifies cancellation is observed before matching work.</summary>
	[Fact]
	public async Task CancellationIsObserved() {
		var document = await PatchTestSupport.ParseAsync(
			"--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n"
		);
		await using var input = await PatchTestSupport.ExistingAsync( "old\n" );
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => PatchApplicationEngine.ApplyAsync(
				input,
				Assert.Single( document.Files ),
				cancellationToken: cancellation.Token
			)
		);
	}
}
