namespace Icod.Patch.Tests;

using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

/// <summary>Provides opt-in differential checks against an installed GNU patch 2.8 executable.</summary>
public sealed class GnuPatchDifferentialTests {
	/// <summary>Compares exact and nearby-offset unified replacement bytes with GNU patch 2.8.</summary>
	[Fact]
	public async Task ExactAndOffsetApplicationMatchInstalledGnuPatch28() {
		if ( !OperatingSystem.IsLinux() || !await IsGnuPatch28AvailableAsync() ) {
			return;
		}
		await AssertMatchesGnuAsync(
			"--- target.txt\n+++ target.txt\n@@ -1,2 +1,2 @@\n old\n-left\n+right\n",
			"old\nleft\n",
			new PatchEngineOptions { Batch = true },
			Array.Empty<string>(),
			0
		);
		await AssertMatchesGnuAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n",
			"zero\nold\n",
			new PatchEngineOptions { Batch = true },
			Array.Empty<string>(),
			0
		);
	}

	/// <summary>Compares fuzz, whitespace, and reverse-direction behavior with GNU patch 2.8.</summary>
	[Fact]
	public async Task MatchingPoliciesMatchInstalledGnuPatch28() {
		if ( !OperatingSystem.IsLinux() || !await IsGnuPatch28AvailableAsync() ) {
			return;
		}
		await AssertMatchesGnuAsync(
			string.Concat(
				"--- target.txt\n+++ target.txt\n@@ -1,3 +1,3 @@\n",
				" head\n-old\n+new\n tail\n"
			),
			"different-head\nold\ndifferent-tail\n",
			new PatchEngineOptions { Batch = true, Fuzz = 1 },
			new[] { "--fuzz=1" },
			0
		);
		await AssertMatchesGnuAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-a b\n+changed\n",
			"a \t  b\n",
			new PatchEngineOptions { Batch = true, IgnoreWhitespace = true },
			new[] { "--ignore-whitespace" },
			0
		);
		await AssertMatchesGnuAsync(
			"--- target.txt\n+++ target.txt\n@@ -1 +1 @@\n-old\n+new\n",
			"new\n",
			new PatchEngineOptions { Reverse = true },
			new[] { "--reverse" },
			0
		);
	}

	/// <summary>Compares context, normal, and ed input application with GNU patch 2.8.</summary>
	[Fact]
	public async Task AllSupportedTextFormatsMatchInstalledGnuPatch28() {
		if ( !OperatingSystem.IsLinux() || !await IsGnuPatch28AvailableAsync() ) {
			return;
		}
		await AssertMatchesGnuAsync(
			string.Concat(
				"*** target.txt\n",
				"--- target.txt\n",
				"***************\n",
				"*** 1,2 ****\n",
				"  same\n",
				"! left\n",
				"--- 1,2 ----\n",
				"  same\n",
				"! right\n"
			),
			"same\nleft\n",
			new PatchEngineOptions { Batch = true },
			new[] { "--context" },
			0
		);
		await AssertMatchesGnuAsync(
			"2c2\n< left\n---\n> right\n",
			"same\nleft\n",
			new PatchEngineOptions { Batch = true },
			new[] { "--normal" },
			0
		);
		await AssertMatchesGnuAsync(
			"2c\nright\n.\n",
			"same\nleft\n",
			new PatchEngineOptions { Batch = true },
			new[] { "--ed" },
			0
		);
	}

	/// <summary>Compares context-anchored diff3 conflict output with GNU patch 2.8.</summary>
	[Fact]
	public async Task Diff3MergeMatchesInstalledGnuPatch28() {
		if ( !OperatingSystem.IsLinux() || !await IsGnuPatch28AvailableAsync() ) {
			return;
		}
		await AssertMatchesGnuAsync(
			string.Concat(
				"--- target.txt\n+++ target.txt\n@@ -1,3 +1,3 @@\n",
				" before\n-old\n+new\n after\n"
			),
			"before\ncurrent\nafter\n",
			new PatchEngineOptions {
				Batch = true,
				Force = true,
				MergeStyle = PatchMergeStyle.Diff3
			},
			new[] { "--force", "--merge=diff3" },
			1
		);
		await AssertMatchesGnuAsync(
			string.Concat(
				"--- target.txt\n+++ target.txt\n@@ -1,3 +1,3 @@\n",
				" a\n-old\n+new\n b\n"
			),
			"a\nlocal\nold\nb\n",
			new PatchEngineOptions {
				Batch = true,
				Force = true,
				MergeStyle = PatchMergeStyle.Diff3
			},
			new[] { "--force", "--merge=diff3" },
			1
		);
		await AssertMatchesGnuAsync(
			string.Concat(
				"--- target.txt\n+++ target.txt\n@@ -1,2 +1,2 @@\n",
				" a\n-old\n+new\n"
			),
			"a\ncurrent\n",
			new PatchEngineOptions {
				Batch = true,
				Force = true,
				MergeStyle = PatchMergeStyle.Diff3
			},
			new[] { "--force", "--merge=diff3" },
			1
		);
	}

	private static async Task AssertMatchesGnuAsync(
		string patchText,
		string targetText,
		PatchEngineOptions engineOptions,
		IReadOnlyList<string> gnuOptions,
		int expectedExitCode
	) {
		var document = await PatchTestSupport.ParseAsync( patchText );
		await using var input = await PatchTestSupport.ExistingAsync( targetText );
		await using var managed = await PatchApplicationEngine.ApplyAsync(
			input,
			Assert.Single( document.Files ),
			engineOptions
		);
		var managedBytes = await managed.File.Content!.ToArrayAsync();

		var directory = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			string.Concat( "icod-patch-diff-", Guid.NewGuid().ToString( "N" ) )
		);
		Directory.CreateDirectory( directory );
		try {
			var targetPath = System.IO.Path.Combine( directory, "target.txt" );
			var patchPath = System.IO.Path.Combine( directory, "change.patch" );
			await File.WriteAllBytesAsync( targetPath, Encoding.UTF8.GetBytes( targetText ) );
			await File.WriteAllBytesAsync( patchPath, Encoding.UTF8.GetBytes( patchText ) );
			var start = new ProcessStartInfo {
				FileName = "patch",
				WorkingDirectory = directory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};
			start.ArgumentList.Add( "--batch" );
			foreach ( var option in gnuOptions ) {
				start.ArgumentList.Add( option );
			}
			start.ArgumentList.Add( "--input" );
			start.ArgumentList.Add( patchPath );
			start.ArgumentList.Add( targetPath );
			using var process = Process.Start( start );
			Assert.NotNull( process );
			var outputTask = process!.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
			_ = await outputTask;
			_ = await errorTask;
			Assert.Equal( expectedExitCode, process.ExitCode );
			Assert.Equal( managedBytes, await File.ReadAllBytesAsync( targetPath ) );
		} finally {
			Directory.Delete( directory, recursive: true );
		}
	}

	private static async Task<bool> IsGnuPatch28AvailableAsync() {
		try {
			var start = new ProcessStartInfo {
				FileName = "patch",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};
			start.ArgumentList.Add( "--version" );
			using var process = Process.Start( start );
			if ( null == process ) {
				return false;
			}
			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
			var output = await outputTask;
			_ = await errorTask;
			return 0 == process.ExitCode && output.Contains( "GNU patch 2.8", StringComparison.Ordinal );
		} catch ( System.ComponentModel.Win32Exception ) {
			return false;
		}
	}
}
