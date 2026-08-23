namespace Icod.Patch.Tests;

using System.Diagnostics;
using System.IO;
using Xunit;

/// <summary>Exercises the built command through the .NET process host.</summary>
public sealed class ProcessHostTests {
	/// <summary>Verifies executable help output and success status.</summary>
	[Fact]
	public async Task HelpRunsThroughProcessHost() {
		var result = await RunProcessAsync( "--help" );
		Assert.Equal( 0, result.Status );
		Assert.Contains( "Usage: patch", result.Output );
		Assert.Empty( result.Error );
	}

	/// <summary>Verifies executable option diagnostics and trouble status.</summary>
	[Fact]
	public async Task InvalidOptionRunsThroughProcessHost() {
		var result = await RunProcessAsync( "--definitely-invalid" );
		Assert.Equal( 2, result.Status );
		Assert.Contains( "patch:", result.Error );
		Assert.Contains( "Try 'patch --help'", result.Error );
	}

	private static async Task<(int Status, string Output, string Error)> RunProcessAsync(
		params string[] arguments
	) {
		var assembly = FindExecutableAssembly();
		var start = new ProcessStartInfo {
			FileName = Environment.GetEnvironmentVariable( "DOTNET_HOST_PATH" ) ?? "dotnet",
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add( assembly );
		foreach ( var argument in arguments ) {
			start.ArgumentList.Add( argument );
		}
		using var process = Process.Start( start )
			?? throw new InvalidOperationException( "unable to start patch process" );
		process.StandardInput.Close();
		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return ( process.ExitCode, await outputTask, await errorTask );
	}

	private static string FindExecutableAssembly() {
		var loaded = typeof( Command ).Assembly.Location;
		var loadedRuntimeConfig = Path.ChangeExtension( loaded, ".runtimeconfig.json" );
		if ( File.Exists( loadedRuntimeConfig ) ) {
			return loaded;
		}
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( null != directory && !File.Exists( System.IO.Path.Combine( directory.FullName, "Icod.Patch.sln" ) ) ) {
			directory = directory.Parent;
		}
		if ( null == directory ) {
			throw new FileNotFoundException( "unable to locate repository root" );
		}
		var runtimeConfig = Directory.GetFiles(
			directory.FullName,
			"patch.runtimeconfig.json",
			SearchOption.AllDirectories
		).FirstOrDefault();
		if ( null == runtimeConfig ) {
			throw new FileNotFoundException( "unable to locate patch.runtimeconfig.json" );
		}
		var assembly = System.IO.Path.Combine(
			Path.GetDirectoryName( runtimeConfig )!,
			"patch.dll"
		);
		if ( !File.Exists( assembly ) ) {
			throw new FileNotFoundException( "unable to locate patch.dll", assembly );
		}
		return assembly;
	}
}
