namespace Icod.Patch;

using Icod.CommandFramework.Diagnostics;

/// <summary>Provides the process entry point for <c>patch</c>.</summary>
internal static class Program {
	/// <summary>Runs <c>patch [OPTION]... [ORIGFILE [PATCHFILE]]</c>.</summary>
	/// <param name="args">The command-line arguments.</param>
	/// <returns>A task whose result is the process status.</returns>
	public static async Task<int> Main( string[] args ) {
		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler handler = ( _, eventArgs ) => {
			eventArgs.Cancel = true;
			cancellation.Cancel();
		};
		Console.CancelKeyPress += handler;
		try {
			return await Command.RunAsync(
				args,
				CommandContext.CreateConsole( "patch", cancellation.Token )
			).ConfigureAwait( false );
		} finally {
			Console.CancelKeyPress -= handler;
		}
	}
}
