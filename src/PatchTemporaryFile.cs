namespace Icod.Patch;

/// <summary>Creates private temporary files with exclusive access and owner-only Unix permissions.</summary>
internal static class PatchTemporaryFile {
	/// <summary>Creates a new asynchronous temporary file at an already unique path.</summary>
	/// <param name="path">The path that must not already exist.</param>
	/// <param name="options">Additional file options.</param>
	/// <returns>The exclusively owned temporary stream.</returns>
	public static FileStream CreateNew( string path, FileOptions options ) {
		ArgumentException.ThrowIfNullOrEmpty( path );
		var streamOptions = new FileStreamOptions {
			Mode = FileMode.CreateNew,
			Access = FileAccess.ReadWrite,
			Share = FileShare.None,
			BufferSize = 64 * 1024,
			Options = options
		};
		if ( !OperatingSystem.IsWindows() ) {
			streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
		return new FileStream( path, streamOptions );
	}
}
