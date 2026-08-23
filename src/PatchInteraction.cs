namespace Icod.Patch;

using Icod.CommandFramework.Diagnostics;

/// <summary>Supplies deterministic command-line answers to Patch policy questions.</summary>
internal sealed class CommandPatchDecisionProvider : IPatchDecisionProvider {
	private readonly CommandContext context;

	/// <summary>Initializes a decision provider over command standard streams.</summary>
	public CommandPatchDecisionProvider( CommandContext context ) {
		this.context = context ?? throw new ArgumentNullException( nameof( context ) );
	}

	/// <inheritdoc/>
	public async ValueTask<bool> DecideAsync(
		PatchDecisionRequest request,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( request );
		var question = request.Kind switch {
			PatchDecisionKind.ReversePatch => "Assume -R?",
			PatchDecisionKind.IgnoreMissingPrerequisite => "Proceed anyway?",
			PatchDecisionKind.RetrieveFromVersionControl => "Get the file from version control?",
			_ => "Proceed?"
		};
		while ( true ) {
			cancellationToken.ThrowIfCancellationRequested();
			await this.context.StandardError.WriteAsync(
				string.Concat( request.Message, ".  ", question, " [n] " ).AsMemory(),
				cancellationToken
			).ConfigureAwait( false );
			await this.context.StandardError.FlushAsync( cancellationToken ).ConfigureAwait( false );
			var response = await this.context.StandardInput.ReadLineAsync( cancellationToken ).ConfigureAwait( false );
			if ( string.IsNullOrWhiteSpace( response ) ) {
				return false;
			}
			switch ( response.Trim().ToLowerInvariant() ) {
				case "y":
				case "yes":
					return true;
				case "n":
				case "no":
					return false;
				default:
					await this.context.StandardError.WriteLineAsync(
						"Please answer yes or no.".AsMemory(),
						cancellationToken
					).ConfigureAwait( false );
					break;
			}
		}
	}
}
