#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sandbox;

public partial class SyncToolWindow
{
	private sealed class DataSourceStatusInfo
	{
		public bool SourceExists { get; init; }
		public bool OutputExists { get; init; }
		public bool IsStale { get; init; }
		public string Icon { get; init; } = "?";
		public Color Color { get; init; } = Color.White.WithAlpha( 0.6f );
		public string Label { get; init; } = "Unknown";
		public string Detail { get; init; } = "";
	}

	private readonly Dictionary<string, DataSourceStatusInfo> _dataSourceStatusCache = new();

	private DataSourceStatusInfo GetDataSourceStatus( SyncToolConfig.SyncMapping mapping )
	{
		var csFiles = GetMappingSourceFiles( mapping, out var sourceExists );
		var outputPath = SyncToolConfig.GetMappingCollectionPath( mapping );
		var outputExists = File.Exists( outputPath );
		var sourceTicks = sourceExists ? csFiles.Max( File.GetLastWriteTimeUtc ).Ticks : 0;
		var outputTicks = outputExists ? File.GetLastWriteTimeUtc( outputPath ).Ticks : 0;
		var cacheKey = $"{mapping.CsFile}|{mapping.Collection}|{csFiles.Length}|{sourceTicks}|{outputPath}|{outputTicks}";
		if ( _dataSourceStatusCache.TryGetValue( cacheKey, out var cached ) )
			return cached;

		var status = BuildDataSourceStatus( mapping, csFiles, sourceExists, outputPath, outputExists, sourceTicks, outputTicks );
		if ( _dataSourceStatusCache.Count > 64 )
			_dataSourceStatusCache.Clear();
		_dataSourceStatusCache[cacheKey] = status;
		return status;
	}

	private static DataSourceStatusInfo BuildDataSourceStatus(
		SyncToolConfig.SyncMapping mapping,
		string[] csFiles,
		bool sourceExists,
		string outputPath,
		bool outputExists,
		long sourceTicks,
		long outputTicks )
	{
		if ( !sourceExists )
		{
			return new DataSourceStatusInfo
			{
				SourceExists = false,
				OutputExists = outputExists,
				Icon = "x",
				Color = Color.Red.WithAlpha( 0.75f ),
				Label = "Missing source",
				Detail = $"No .cs files found at {mapping.CsFile}"
			};
		}

		if ( !outputExists )
		{
			return new DataSourceStatusInfo
			{
				SourceExists = true,
				OutputExists = false,
				IsStale = true,
				Icon = "!",
				Color = Color.Yellow.WithAlpha( 0.85f ),
				Label = "Needs generate",
				Detail = $"{mapping.Collection}.collection.yml does not exist yet"
			};
		}

		var generatedHash = TryReadGeneratedSourceHash( outputPath );
		if ( !string.IsNullOrWhiteSpace( generatedHash ) )
		{
			var currentHash = ComputeDataSourceHash( csFiles );
			var matches = string.Equals( currentHash, generatedHash, StringComparison.OrdinalIgnoreCase );
			return new DataSourceStatusInfo
			{
				SourceExists = true,
				OutputExists = true,
				IsStale = !matches,
				Icon = matches ? "+" : "!",
				Color = matches ? Color.Green.WithAlpha( 0.65f ) : Color.Yellow.WithAlpha( 0.85f ),
				Label = matches ? "Generated" : "Needs generate",
				Detail = matches ? "C# source matches generated collection" : "C# source changed since the collection was generated"
			};
		}

		var staleByTime = sourceTicks > outputTicks;
		return new DataSourceStatusInfo
		{
			SourceExists = true,
			OutputExists = true,
			IsStale = staleByTime,
			Icon = staleByTime ? "!" : "+",
			Color = staleByTime ? Color.Yellow.WithAlpha( 0.85f ) : Color.Green.WithAlpha( 0.55f ),
			Label = staleByTime ? "Needs generate" : "Generated",
			Detail = staleByTime
				? "C# source is newer than the collection output"
				: "No generated hash found; timestamp check passed"
		};
	}

	private static string[] GetMappingSourceFiles( SyncToolConfig.SyncMapping mapping, out bool sourceExists )
	{
		var csPath = SyncToolConfig.GetMappingCsPath( mapping );
		if ( File.Exists( csPath ) )
		{
			sourceExists = true;
			return new[] { csPath };
		}

		if ( Directory.Exists( csPath ) )
		{
			var files = Directory.GetFiles( csPath, "*.cs" )
				.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
				.ToArray();
			sourceExists = files.Length > 0;
			return files;
		}

		sourceExists = false;
		return Array.Empty<string>();
	}

	private static string TryReadGeneratedSourceHash( string outputPath )
	{
		try
		{
			foreach ( var line in File.ReadLines( outputPath ).Take( 20 ) )
			{
				var trimmed = line.Trim();
				const string key = "# generatedSourceHash:";
				if ( trimmed.StartsWith( key, StringComparison.OrdinalIgnoreCase ) )
					return trimmed[key.Length..].Trim();
			}
		}
		catch
		{
		}

		return "";
	}

	private static string ComputeDataSourceHash( string[] csFiles )
	{
		using var buffer = new MemoryStream();
		foreach ( var file in csFiles.OrderBy( path => ProjectRelativePath( path ), StringComparer.OrdinalIgnoreCase ) )
		{
			WriteUtf8( buffer, ProjectRelativePath( file ) );
			WriteUtf8( buffer, "\n" );
			var bytes = File.ReadAllBytes( file );
			buffer.Write( bytes, 0, bytes.Length );
			WriteUtf8( buffer, "\n" );
		}

		using var sha = SHA256.Create();
		return ToHex( sha.ComputeHash( buffer.ToArray() ) );
	}

	private static string ProjectRelativePath( string absolutePath )
	{
		return Path.GetRelativePath( SyncToolConfig.ProjectRoot, absolutePath )
			.Replace( Path.DirectorySeparatorChar, '/' )
			.Replace( Path.AltDirectorySeparatorChar, '/' );
	}

	private static void WriteUtf8( Stream stream, string text )
	{
		var bytes = Encoding.UTF8.GetBytes( text );
		stream.Write( bytes, 0, bytes.Length );
	}

	private static string ToHex( byte[] bytes )
	{
		var builder = new StringBuilder( bytes.Length * 2 );
		foreach ( var b in bytes )
			builder.Append( b.ToString( "x2" ) );
		return builder.ToString();
	}
}
