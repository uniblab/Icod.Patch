namespace Icod.Patch.Tests;

using System.Text;

/// <summary>Provides byte-oriented Patch test helpers.</summary>
internal static class PatchTestSupport {
	/// <summary>Parses one in-memory patch document.</summary>
	/// <param name="text">The patch text.</param>
	/// <param name="forcedFormat">An optional forced format.</param>
	/// <returns>The parsed document.</returns>
	public static async Task<PatchDocument> ParseAsync(
		string text,
		PatchFormat? forcedFormat = null
	) {
		await using var stream = new MemoryStream( Encoding.UTF8.GetBytes( text ) );
		await using var source = await PatchSource.ReadAsync( stream );
		var scan = PatchScanner.Detect( source.Records, source.Probes, forcedFormat );
		return await PatchDocumentParser.ParseAsync( source, scan, PatchParseLimits.Default );
	}

	/// <summary>Creates an existing virtual file from UTF-8 bytes.</summary>
	/// <param name="text">The target text.</param>
	/// <param name="limits">Optional target limits.</param>
	/// <returns>The virtual file.</returns>
	public static async Task<PatchVirtualFile> ExistingAsync(
		string text,
		PatchTargetLimits? limits = null
	) {
		var content = await PatchTargetContent.FromBytesAsync( Encoding.UTF8.GetBytes( text ), limits );
		return new PatchVirtualFile( true, content );
	}

	/// <summary>Reads virtual-file bytes as UTF-8 text.</summary>
	/// <param name="file">The virtual file.</param>
	/// <returns>The decoded text.</returns>
	public static async Task<string> ReadTextAsync( PatchVirtualFile file ) {
		if ( !file.Exists || null == file.Content ) {
			return string.Empty;
		}
		return Encoding.UTF8.GetString( await file.Content.ToArrayAsync() );
	}
}
