// Original behavior/reference: GNU patch 2.8
// Ported to .NET by Timothy J. Bruce <uniblab@hotmail.com>

namespace Icod.Patch;

using Icod.CommandFramework.CommandLine;
using Icod.CommandFramework.Diagnostics;
using Icod.CommandFramework.IO;

/// <summary>Implements the GNU-compatible <c>patch</c> command front end.</summary>
public static class Command {
	private const string VersionText = "patch (Icod.Patch) 1.0";
	private static readonly HashSet<string> ImplementedOptionKeys = new( StringComparer.Ordinal ) {
		"backup",
		"backup-if-mismatch",
		"basename-prefix",
		"batch",
		"binary",
		"context",
		"directory",
		"dry-run",
		"ed",
		"follow-symlinks",
		"force",
		"forward",
		"fuzz",
		"get",
		"help",
		"ignore-whitespace",
		"input",
		"merge",
		"merge-short",
		"no-backup-if-mismatch",
		"normal",
		"output",
		"posix",
		"prefix",
		"quiet",
		"quoting-style",
		"reject-file",
		"reject-format",
		"remove-empty-files",
		"reverse",
		"set-time",
		"set-utc",
		"strip",
		"suffix",
		"unified",
		"verbose",
		"version",
		"version-control"
	};

	private sealed class PatchUsageException : Exception {
		/// <summary>Initializes a usage exception.</summary>
		/// <param name="message">The diagnostic message.</param>
		public PatchUsageException( string message )
			: base( message ) {
		}
	}

	/// <summary>Runs the command synchronously using supplied text streams.</summary>
	/// <param name="arguments">The command-line arguments.</param>
	/// <param name="stdin">The standard-input reader.</param>
	/// <param name="stdout">The standard-output writer.</param>
	/// <param name="stderr">The standard-error writer.</param>
	/// <returns>The process status.</returns>
	public static int Run(
		IReadOnlyList<string>? arguments,
		TextReader? stdin = null,
		TextWriter? stdout = null,
		TextWriter? stderr = null
	) {
		return RunAsync( arguments, stdin, stdout, stderr ).GetAwaiter().GetResult();
	}

	/// <summary>Runs the command asynchronously using supplied streams.</summary>
	/// <param name="arguments">The command-line arguments.</param>
	/// <param name="stdin">The standard-input reader.</param>
	/// <param name="stdout">The standard-output writer.</param>
	/// <param name="stderr">The standard-error writer.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <param name="stdinStream">The byte-preserving standard-input stream.</param>
	/// <param name="stdoutStream">The byte-preserving standard-output stream.</param>
	/// <returns>A task whose result is the process status.</returns>
	public static async Task<int> RunAsync(
		IReadOnlyList<string>? arguments,
		TextReader? stdin = null,
		TextWriter? stdout = null,
		TextWriter? stderr = null,
		CancellationToken cancellationToken = default,
		Stream? stdinStream = null,
		Stream? stdoutStream = null
	) {
		stdin ??= TextReader.Null;
		stdout ??= TextWriter.Null;
		stderr ??= TextWriter.Null;
		TextReaderStream? adapter = null;
		if ( null == stdinStream ) {
			adapter = new TextReaderStream( stdin, leaveOpen: true );
			stdinStream = adapter;
		}
		try {
			return await RunAsync(
				arguments,
				new CommandContext(
					"patch",
					stdin,
					stdout,
					stderr,
					stdinStream,
					standardOutputStream: stdoutStream,
					cancellationToken: cancellationToken
				)
			).ConfigureAwait( false );
		} finally {
			adapter?.Dispose();
		}
	}

	/// <summary>Runs the command within an existing command context.</summary>
	/// <param name="arguments">The command-line arguments.</param>
	/// <param name="context">The command context.</param>
	/// <returns>A task whose result is the process status.</returns>
	public static async Task<int> RunAsync(
		IReadOnlyList<string>? arguments,
		CommandContext context
	) {
		ArgumentNullException.ThrowIfNull( context );
		try {
			var parsed = CreateParser().Parse( arguments );
			if ( !parsed.IsSuccess ) {
				foreach ( var error in parsed.Errors ) {
					await context.StandardError.WriteLineAsync(
						OptionDiagnosticFormatter.Format( context.ProgramName, error ).AsMemory(),
						context.CancellationToken
					).ConfigureAwait( false );
				}
				await WriteTryHelpAsync( context ).ConfigureAwait( false );
				return (int)PatchExitStatus.Trouble;
			}
			if ( parsed.HasOption( "help" ) ) {
				await WriteHelpAsync( context.StandardOutput, context.CancellationToken ).ConfigureAwait( false );
				return (int)PatchExitStatus.Success;
			}
			if ( parsed.HasOption( "version" ) ) {
				await context.StandardOutput.WriteLineAsync(
					VersionText.AsMemory(),
					context.CancellationToken
				).ConfigureAwait( false );
				return (int)PatchExitStatus.Success;
			}
			ValidateImplementedOptions( parsed );
			var options = CreateOptions( parsed );
			return await PatchApplication.ExecuteAsync( options, context ).ConfigureAwait( false );
		} catch ( PatchUsageException exception ) {
			await context.Diagnostics.ErrorAsync(
				exception.Message,
				CancellationToken.None
			).ConfigureAwait( false );
			await WriteTryHelpAsync( context ).ConfigureAwait( false );
			return (int)PatchExitStatus.Trouble;
		} catch ( OperationCanceledException ) when ( context.CancellationToken.IsCancellationRequested ) {
			return CommandExitCodes.Canceled;
		} catch ( Exception exception ) when ( PatchApplication.IsOperationalException( exception ) ) {
			await context.Diagnostics.ErrorAsync(
				exception.Message,
				CancellationToken.None
			).ConfigureAwait( false );
			return (int)PatchExitStatus.Trouble;
		}
	}

	/// <summary>Creates the declarative option parser used by <c>patch</c>.</summary>
	/// <returns>The option parser.</returns>
	public static OptionParser CreateParser() {
		return new OptionParser(
			new OptionDefinition[] {
				new( "backup", 'b', new[] { "backup" } ),
				new( "prefix", 'B', new[] { "prefix" }, OptionValueArity.Required ),
				new( "context", 'c', new[] { "context" } ),
				new( "directory", 'd', new[] { "directory" }, OptionValueArity.Required ),
				new( "ifdef", 'D', new[] { "ifdef" }, OptionValueArity.Required ),
				new( "ed", 'e', new[] { "ed" } ),
				new( "remove-empty-files", 'E', new[] { "remove-empty-files" } ),
				new( "force", 'f', new[] { "force" } ),
				new( "fuzz", 'F', new[] { "fuzz" }, OptionValueArity.Required ),
				new( "get", 'g', new[] { "get" }, OptionValueArity.Required ),
				new( "input", 'i', new[] { "input" }, OptionValueArity.Required ),
				new( "ignore-whitespace", 'l', new[] { "ignore-whitespace" } ),
				new( "merge-short", 'm' ),
				new( "merge", longNames: new[] { "merge" }, valueArity: OptionValueArity.Optional ),
				new( "normal", 'n', new[] { "normal" } ),
				new( "forward", 'N', new[] { "forward" } ),
				new( "output", 'o', new[] { "output" }, OptionValueArity.Required ),
				new( "strip", 'p', new[] { "strip" }, OptionValueArity.Required ),
				new( "reject-file", 'r', new[] { "reject-file" }, OptionValueArity.Required ),
				new( "reverse", 'R', new[] { "reverse" } ),
				new( "quiet", 's', new[] { "quiet", "silent" } ),
				new( "batch", 't', new[] { "batch" } ),
				new( "set-time", 'T', new[] { "set-time" } ),
				new( "unified", 'u', new[] { "unified" } ),
				new( "version", 'v', new[] { "version" } ),
				new( "version-control", 'V', new[] { "version-control" }, OptionValueArity.Required ),
				new( "debug", 'x', new[] { "debug" }, OptionValueArity.Required ),
				new( "basename-prefix", 'Y', new[] { "basename-prefix" }, OptionValueArity.Required ),
				new( "suffix", 'z', new[] { "suffix" }, OptionValueArity.Required ),
				new( "set-utc", 'Z', new[] { "set-utc" } ),
				new( "dry-run", longNames: new[] { "dry-run" } ),
				new( "verbose", longNames: new[] { "verbose" } ),
				new( "binary", longNames: new[] { "binary" } ),
				new( "help", longNames: new[] { "help" } ),
				new( "backup-if-mismatch", longNames: new[] { "backup-if-mismatch" } ),
				new( "no-backup-if-mismatch", longNames: new[] { "no-backup-if-mismatch" } ),
				new( "posix", longNames: new[] { "posix" } ),
				new( "quoting-style", longNames: new[] { "quoting-style" }, valueArity: OptionValueArity.Required ),
				new( "reject-format", longNames: new[] { "reject-format" }, valueArity: OptionValueArity.Required ),
				new( "read-only", longNames: new[] { "read-only" }, valueArity: OptionValueArity.Required ),
				new( "follow-symlinks", longNames: new[] { "follow-symlinks" } )
			},
			new OptionParserSettings {
				AllowLongOptionAbbreviations = true,
				Ordering = OptionOrdering.Permute
			}
		);
	}

	private static void ValidateImplementedOptions( OptionParseResult parsed ) {
		var unsupported = parsed.Options.FirstOrDefault(
			item => !ImplementedOptionKeys.Contains( item.Definition.Key )
		);
		if ( null != unsupported ) {
			throw new PatchUsageException(
				GetUnsupportedOptionMessage(
					unsupported.Definition.Key,
					unsupported.Spelling
				)
			);
		}
	}

	/// <summary>Formats the final release diagnostic for a source-defined but unavailable option.</summary>
	/// <param name="key">The canonical option key.</param>
	/// <param name="spelling">The spelling supplied by the caller.</param>
	/// <returns>The controlled capability diagnostic.</returns>
	internal static string GetUnsupportedOptionMessage( string key, string spelling ) {
		ArgumentException.ThrowIfNullOrWhiteSpace( key );
		ArgumentException.ThrowIfNullOrWhiteSpace( spelling );
		return key switch {
			"ifdef" => string.Concat(
				spelling,
				": conditional-output mode is not implemented by Icod.Patch 1.0"
			),
			"read-only" => string.Concat(
				spelling,
				": read-only input policy is not implemented; access failures remain controlled trouble"
			),
			"debug" => string.Concat(
				spelling,
				": unavailable in this release because GNU DEBUGGING compatibility is not enabled"
			),
			_ => string.Concat( spelling, ": unsupported by Icod.Patch 1.0" )
		};
	}

	/// <summary>Maps a successful parse and environment lookup into validated command options.</summary>
	/// <param name="parsed">The successful declarative option parse.</param>
	/// <param name="environment">An optional environment-variable lookup used by deterministic tests.</param>
	/// <returns>The validated immutable command options.</returns>
	internal static PatchOptions CreateOptions( OptionParseResult parsed, Func<string, string?>? environment = null ) {
		environment ??= Environment.GetEnvironmentVariable;
		if ( 2 < parsed.Operands.Count ) {
			throw new PatchUsageException( string.Concat( "extra operand '", parsed.Operands[2], "'" ) );
		}
		var optionInput = parsed.GetLastValue( "input" );
		var operandInput = 1 < parsed.Operands.Count ? parsed.Operands[1] : null;
		if ( null != optionInput && null != operandInput ) {
			throw new PatchUsageException( "patch source specified by both -i and an operand" );
		}
		var selectedFormats = new List<PatchFormat>( 4 );
		if ( parsed.HasOption( "unified" ) ) {
			selectedFormats.Add( PatchFormat.Unified );
		}
		if ( parsed.HasOption( "context" ) ) {
			selectedFormats.Add( PatchFormat.Context );
		}
		if ( parsed.HasOption( "normal" ) ) {
			selectedFormats.Add( PatchFormat.Normal );
		}
		if ( parsed.HasOption( "ed" ) ) {
			selectedFormats.Add( PatchFormat.EdScript );
		}
		if ( 1 < selectedFormats.Count ) {
			throw new PatchUsageException( "only one patch input format may be specified" );
		}
		var fuzz = 2;
		var fuzzText = parsed.GetLastValue( "fuzz" );
		if ( null != fuzzText
			&& ( !int.TryParse(
				fuzzText,
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out fuzz
			) || fuzz < 0 ) ) {
			throw new PatchUsageException( string.Concat( "invalid maximum fuzz factor '", fuzzText, "'" ) );
		}
		int? stripCount = null;
		var stripText = parsed.GetLastValue( "strip" );
		if ( null != stripText ) {
			if ( !int.TryParse(
				stripText,
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out var parsedStrip
			) || parsedStrip < 0 ) {
				throw new PatchUsageException( string.Concat( "invalid strip count '", stripText, "'" ) );
			}
			stripCount = parsedStrip;
		}
		var posix = parsed.HasOption( "posix" ) || null != environment( "POSIXLY_CORRECT" );
		var get = posix ? 0 : -1;
		var getText = parsed.GetLastValue( "get" ) ?? environment( "PATCH_GET" );
		if ( null != getText
			&& !int.TryParse(
				getText,
				System.Globalization.NumberStyles.AllowLeadingSign,
				System.Globalization.CultureInfo.InvariantCulture,
				out get
			) ) {
			throw new PatchUsageException( string.Concat( "invalid version-control retrieval policy '", getText, "'" ) );
		}
		if ( parsed.HasOption( "backup-if-mismatch" ) && parsed.HasOption( "no-backup-if-mismatch" ) ) {
			throw new PatchUsageException( "--backup-if-mismatch and --no-backup-if-mismatch are mutually exclusive" );
		}
		if ( parsed.HasOption( "set-time" ) && parsed.HasOption( "set-utc" ) ) {
			throw new PatchUsageException( "--set-time and --set-utc are mutually exclusive" );
		}
		if ( parsed.HasOption( "quiet" ) && parsed.HasOption( "verbose" ) ) {
			throw new PatchUsageException( "--quiet and --verbose are mutually exclusive" );
		}
		var backupSuffix = parsed.GetLastValue( "suffix" )
			?? environment( "SIMPLE_BACKUP_SUFFIX" )
			?? ".orig";
		var backupVersionControl = ParseBackupVersionControl(
			parsed.GetLastValue( "version-control" )
				?? environment( "PATCH_VERSION_CONTROL" )
				?? environment( "VERSION_CONTROL" )
		);
		var rejectFormat = ParseRejectFormat( parsed.GetLastValue( "reject-format" ) );
		var quotingStyle = ParseQuotingStyle(
			parsed.GetLastValue( "quoting-style" ) ?? environment( "QUOTING_STYLE" )
		);
		var verbosity = parsed.HasOption( "quiet" )
			? PatchVerbosity.Quiet
			: parsed.HasOption( "verbose" )
				? PatchVerbosity.Verbose
				: PatchVerbosity.Normal;
		bool? backupIfMismatch = parsed.HasOption( "backup-if-mismatch" )
			? true
			: parsed.HasOption( "no-backup-if-mismatch" )
				? false
				: null;
		var mergeStyle = PatchMergeStyle.None;
		if ( parsed.HasOption( "merge-short" ) ) {
			mergeStyle = PatchMergeStyle.Merge;
		}
		if ( parsed.HasOption( "merge" ) ) {
			var mergeValue = parsed.GetLastValue( "merge" );
			mergeStyle = mergeValue switch {
				null or "merge" => PatchMergeStyle.Merge,
				"diff3" => PatchMergeStyle.Diff3,
				_ => throw new PatchUsageException(
					string.Concat( "invalid merge style '", mergeValue, "'" )
				)
			};
		}
		return new PatchOptions {
			OriginalFile = 0 < parsed.Operands.Count ? parsed.Operands[0] : null,
			PatchFile = optionInput ?? operandInput,
			Directory = parsed.GetLastValue( "directory" ),
			StripCount = stripCount,
			Posix = posix,
			FollowSymbolicLinks = parsed.HasOption( "follow-symlinks" ),
			Get = get,
			Binary = parsed.HasOption( "binary" ),
			ForcedFormat = 0 < selectedFormats.Count ? selectedFormats[0] : null,
			Force = parsed.HasOption( "force" ),
			ForwardOnly = parsed.HasOption( "forward" ),
			Reverse = parsed.HasOption( "reverse" ),
			Batch = parsed.HasOption( "batch" ),
			Fuzz = fuzz,
			IgnoreWhitespace = parsed.HasOption( "ignore-whitespace" ),
			MergeStyle = mergeStyle,
			Backup = parsed.HasOption( "backup" ),
			BackupIfMismatch = backupIfMismatch,
			BackupPrefix = parsed.GetLastValue( "prefix" ),
			BackupBasenamePrefix = parsed.GetLastValue( "basename-prefix" ),
			BackupSuffix = backupSuffix,
			BackupSuffixSpecified = parsed.HasOption( "suffix" ),
			BackupVersionControl = backupVersionControl,
			RejectFile = parsed.GetLastValue( "reject-file" ),
			RejectFormat = rejectFormat,
			OutputFile = parsed.GetLastValue( "output" ),
			RemoveEmptyFiles = parsed.HasOption( "remove-empty-files" ),
			DryRun = parsed.HasOption( "dry-run" ),
			Verbosity = verbosity,
			QuotingStyle = quotingStyle,
			SetTime = parsed.HasOption( "set-time" ),
			SetUtc = parsed.HasOption( "set-utc" )
		};
	}

	private static PatchBackupVersionControl ParseBackupVersionControl( string? value ) {
		if ( null == value ) {
			return PatchBackupVersionControl.Existing;
		}
		var matches = new[] {
			(Name: "existing", Value: PatchBackupVersionControl.Existing),
			(Name: "nil", Value: PatchBackupVersionControl.Existing),
			(Name: "numbered", Value: PatchBackupVersionControl.Numbered),
			(Name: "t", Value: PatchBackupVersionControl.Numbered),
			(Name: "simple", Value: PatchBackupVersionControl.Simple),
			(Name: "never", Value: PatchBackupVersionControl.Simple)
		}.Where( candidate => candidate.Name.StartsWith( value, StringComparison.Ordinal ) )
			.Select( candidate => candidate.Value )
			.Distinct()
			.ToArray();
		return 1 == matches.Length
			? matches[0]
			: throw new PatchUsageException( string.Concat( "invalid version control type '", value, "'" ) );
	}

	private static PatchRejectFormat ParseRejectFormat( string? value ) {
		return value switch {
			null => PatchRejectFormat.Automatic,
			"context" => PatchRejectFormat.Context,
			"unified" => PatchRejectFormat.Unified,
			_ => throw new PatchUsageException( string.Concat( "invalid reject format '", value, "'" ) )
		};
	}

	private static PatchQuotingStyle ParseQuotingStyle( string? value ) {
		return value switch {
			null or "shell" => PatchQuotingStyle.Shell,
			"literal" => PatchQuotingStyle.Literal,
			"shell-always" => PatchQuotingStyle.ShellAlways,
			"c" => PatchQuotingStyle.C,
			"escape" => PatchQuotingStyle.Escape,
			_ => throw new PatchUsageException( string.Concat( "invalid quoting style '", value, "'" ) )
		};
	}

	private static async Task WriteTryHelpAsync( CommandContext context ) {
		await context.StandardError.WriteLineAsync(
			string.Concat(
				context.ProgramName,
				": Try '",
				context.ProgramName,
				" --help' for more information."
			).AsMemory(),
			CancellationToken.None
		).ConfigureAwait( false );
	}

	private static async Task WriteHelpAsync(
		TextWriter output,
		CancellationToken cancellationToken
	) {
		var text = string.Join(
			Environment.NewLine,
			new[] {
				"Usage: patch [OPTION]... [ORIGFILE [PATCHFILE]]",
				"Apply a difference listing to an original file or files.",
				string.Empty,
				"  -b, --backup           make backup files",
				"  -B, --prefix=PREFIX    prepend PREFIX to backup names",
				"  -c, --context          interpret the patch as a context diff",
				"  -d, --directory=DIR    change to DIR before resolving patch filenames",
				"  -e, --ed               interpret the patch as an ed script",
				"  -E, --remove-empty-files remove output files that become empty",
				"  -f, --force            assume the patch is not reversed",
				"  -F, --fuzz=NUM         set the maximum fuzz factor",
				"  -g, --get=NUM          control version-control retrieval policy",
				"  -i, --input=PATCHFILE  read patch from PATCHFILE instead of standard input",
				"  -l, --ignore-whitespace ignore horizontal blank-run changes",
				"  -m, --merge            merge using two-way conflict markers",
				"      --merge[=STYLE]    merge using STYLE 'merge' or 'diff3'",
				"  -n, --normal           interpret the patch as a normal diff",
				"  -N, --forward          ignore patches that seem reversed or applied",
				"  -o, --output=FILE      send patched output to FILE instead of modifying input",
				"  -p, --strip=NUM        strip NUM leading separator-delimited components",
				"  -r, --reject-file=FILE write rejected hunks to FILE",
				"  -R, --reverse          assume the patch was created in reverse",
				"  -s, --quiet            suppress ordinary progress diagnostics",
				"  -t, --batch            ask no questions; skip bad prerequisites",
				"  -T, --set-time         set patched-file times from local header timestamps",
				"  -u, --unified          interpret the patch as a unified diff",
				"  -V, --version-control=STYLE select numbered or simple backups",
				"  -Y, --basename-prefix=PREFIX prepend PREFIX to backup basenames",
				"  -z, --suffix=SUFFIX    use SUFFIX for simple backup names",
				"  -Z, --set-utc          set patched-file times from UTC header timestamps",
				"      --backup-if-mismatch make backups for offset, fuzz, or rejected hunks",
				"      --no-backup-if-mismatch suppress mismatch-triggered backups",
				"      --dry-run          plan and apply in memory without changing files",
				"      --quoting-style=STYLE select diagnostic filename quoting",
				"      --reject-format=FORMAT write context or unified rejects",
				"      --verbose          report artifact-policy details",
				"      --binary           read and write data in binary mode",
				"      --follow-symlinks  follow input and output symbolic links",
				"      --posix            use POSIX filename-selection and retrieval defaults",
				"      --help             display this help and exit",
				"  -v, --version          output version information and exit",
				string.Empty,
				"Filesystem replacement is delegated to the shared E6 transaction provider.",
				"Source-defined options unavailable in this release are diagnosed explicitly."
			}
		);
		await output.WriteLineAsync( text.AsMemory(), cancellationToken ).ConfigureAwait( false );
	}
}
