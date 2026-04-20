using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

internal static class JsonDiffUtilities
{
	internal sealed class FieldDifference
	{
		public string Path { get; set; } = "";
		public string LocalValue { get; set; }
		public string RemoteValue { get; set; }
		public bool IsAdded { get; set; }
	}

	internal struct LineDiffCounts
	{
		public int Added;
		public int Removed;
		public int Changed;

		public bool HasChanges => Added > 0 || Removed > 0 || Changed > 0;
	}

	internal sealed class ComparisonResult
	{
		public List<FieldDifference> Added { get; } = new();
		public List<FieldDifference> Changed { get; } = new();
		public LineDiffCounts LineCounts { get; set; }

		public bool IsRemoteAdditiveOnly => Added.Count > 0 && Changed.Count == 0;
	}

	private enum LineOpKind
	{
		Same,
		Added,
		Removed
	}

	private struct LineOperation
	{
		public LineOpKind Kind;
		public int? LocalIndex;
		public int? RemoteIndex;
	}

	internal static ComparisonResult Analyze( string localJson, string remoteJson )
	{
		var result = new ComparisonResult
		{
			LineCounts = CountLineDifferences( localJson, remoteJson )
		};

		try
		{
			var local = JsonSerializer.Deserialize<JsonElement>( localJson );
			var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );
			CompareElements( local, remote, "", result );
		}
		catch
		{
			if ( result.LineCounts.HasChanges )
			{
				result.Changed.Add( new FieldDifference
				{
					Path = "content",
					LocalValue = Truncate( localJson ),
					RemoteValue = Truncate( remoteJson ),
					IsAdded = false
				} );
			}
		}

		return result;
	}

	internal static LineDiffCounts CountLineDifferences( string localJson, string remoteJson )
	{
		var localLines = NormalizePretty( localJson ).Split( '\n' );
		var remoteLines = NormalizePretty( remoteJson ).Split( '\n' );
		var ops = BuildLineOperations( localLines, remoteLines );
		var counts = new LineDiffCounts();
		var index = 0;

		while ( index < ops.Count )
		{
			if ( ops[index].Kind == LineOpKind.Same )
			{
				index++;
				continue;
			}

			var removed = 0;
			var added = 0;

			while ( index < ops.Count && ops[index].Kind != LineOpKind.Same )
			{
				if ( ops[index].Kind == LineOpKind.Removed ) removed++;
				else added++;
				index++;
			}

			var changed = Math.Min( removed, added );
			counts.Changed += changed;
			counts.Removed += removed - changed;
			counts.Added += added - changed;
		}

		return counts;
	}

	internal static string SummarizeLineDifferences( LineDiffCounts counts )
	{
		var parts = new List<string>();
		if ( counts.Added > 0 ) parts.Add( $"{counts.Added} line{Plural( counts.Added )} added" );
		if ( counts.Changed > 0 ) parts.Add( $"{counts.Changed} line{Plural( counts.Changed )} changed" );
		if ( counts.Removed > 0 ) parts.Add( $"{counts.Removed} line{Plural( counts.Removed )} removed" );

		return parts.Count == 0 ? "No line differences" : $"Remote {string.Join( ", ", parts )}";
	}

	internal static string PreviewPaths( IEnumerable<FieldDifference> differences, int maxCount = 2 )
	{
		var names = differences
			.Select( x => x.Path )
			.Where( x => !string.IsNullOrWhiteSpace( x ) )
			.Distinct()
			.ToList();

		if ( names.Count == 0 )
			return "";

		var shown = names.Take( maxCount ).ToList();
		var preview = string.Join( ", ", shown );
		var remaining = names.Count - shown.Count;
		if ( remaining > 0 )
			preview += $", +{remaining} more";

		return preview;
	}

	private static void CompareElements( JsonElement local, JsonElement remote, string path, ComparisonResult result )
	{
		if ( local.ValueKind == JsonValueKind.Object && remote.ValueKind == JsonValueKind.Object )
		{
			var localProps = local.EnumerateObject().ToDictionary( x => x.Name, x => x.Value );
			var remoteProps = remote.EnumerateObject().ToDictionary( x => x.Name, x => x.Value );

			foreach ( var key in localProps.Keys.OrderBy( x => x ) )
			{
				var childPath = CombinePath( path, key );
				if ( !remoteProps.TryGetValue( key, out var remoteValue ) )
				{
					result.Changed.Add( new FieldDifference
					{
						Path = childPath,
						LocalValue = FormatShort( localProps[key] ),
						RemoteValue = "(missing)",
						IsAdded = false
					} );
					continue;
				}

				CompareElements( localProps[key], remoteValue, childPath, result );
			}

			foreach ( var key in remoteProps.Keys.Except( localProps.Keys ).OrderBy( x => x ) )
			{
				result.Added.Add( new FieldDifference
				{
					Path = CombinePath( path, key ),
					LocalValue = null,
					RemoteValue = FormatShort( remoteProps[key] ),
					IsAdded = true
				} );
			}

			return;
		}

		if ( NormalizeJson( local.GetRawText() ) == NormalizeJson( remote.GetRawText() ) )
			return;

		result.Changed.Add( new FieldDifference
		{
			Path = string.IsNullOrEmpty( path ) ? "content" : path,
			LocalValue = FormatShort( local ),
			RemoteValue = FormatShort( remote ),
			IsAdded = false
		} );
	}

	private static List<LineOperation> BuildLineOperations( string[] localLines, string[] remoteLines )
	{
		var localCount = localLines.Length;
		var remoteCount = remoteLines.Length;
		var lcs = new int[localCount + 1, remoteCount + 1];

		for ( int local = localCount - 1; local >= 0; local-- )
		{
			var localText = GetLineText( localLines, local );
			for ( int remote = remoteCount - 1; remote >= 0; remote-- )
			{
				if ( localText == GetLineText( remoteLines, remote ) )
				{
					lcs[local, remote] = lcs[local + 1, remote + 1] + 1;
				}
				else
				{
					lcs[local, remote] = Math.Max( lcs[local + 1, remote], lcs[local, remote + 1] );
				}
			}
		}

		var ops = new List<LineOperation>();
		var localIndex = 0;
		var remoteIndex = 0;

		while ( localIndex < localCount && remoteIndex < remoteCount )
		{
			var localText = GetLineText( localLines, localIndex );
			var remoteText = GetLineText( remoteLines, remoteIndex );

			if ( localText == remoteText )
			{
				ops.Add( new LineOperation
				{
					Kind = LineOpKind.Same,
					LocalIndex = localIndex,
					RemoteIndex = remoteIndex
				} );
				localIndex++;
				remoteIndex++;
			}
			else if ( lcs[localIndex + 1, remoteIndex] >= lcs[localIndex, remoteIndex + 1] )
			{
				ops.Add( new LineOperation
				{
					Kind = LineOpKind.Removed,
					LocalIndex = localIndex
				} );
				localIndex++;
			}
			else
			{
				ops.Add( new LineOperation
				{
					Kind = LineOpKind.Added,
					RemoteIndex = remoteIndex
				} );
				remoteIndex++;
			}
		}

		while ( localIndex < localCount )
		{
			ops.Add( new LineOperation
			{
				Kind = LineOpKind.Removed,
				LocalIndex = localIndex
			} );
			localIndex++;
		}

		while ( remoteIndex < remoteCount )
		{
			ops.Add( new LineOperation
			{
				Kind = LineOpKind.Added,
				RemoteIndex = remoteIndex
			} );
			remoteIndex++;
		}

		return ops;
	}

	private static string NormalizePretty( string json )
	{
		if ( string.IsNullOrEmpty( json ) ) return "";
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			return JsonSerializer.Serialize( SortElement( el ), new JsonSerializerOptions { WriteIndented = true } );
		}
		catch
		{
			return json.Replace( "\r\n", "\n" ).Replace( '\r', '\n' );
		}
	}

	private static string NormalizeJson( string json )
	{
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			return JsonSerializer.Serialize( SortElement( el ), new JsonSerializerOptions { WriteIndented = false } );
		}
		catch
		{
			return json.Trim();
		}
	}

	private static object SortElement( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				var dict = new SortedDictionary<string, object>();
				foreach ( var prop in el.EnumerateObject() )
					dict[prop.Name] = SortElement( prop.Value );
				return dict;
			case JsonValueKind.Array:
				var arr = new List<object>();
				foreach ( var item in el.EnumerateArray() )
					arr.Add( SortElement( item ) );
				return arr;
			case JsonValueKind.String:
				return el.GetString();
			case JsonValueKind.Number:
				return el.TryGetInt64( out var l ) ? (object)l : el.GetDouble();
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			default:
				return null;
		}
	}

	private static string CombinePath( string prefix, string name )
	{
		return string.IsNullOrEmpty( prefix ) ? name : $"{prefix}.{name}";
	}

	private static string GetLineText( string[] lines, int index )
	{
		return lines[index].TrimEnd( '\r' );
	}

	private static string FormatShort( JsonElement el )
	{
		if ( el.ValueKind == JsonValueKind.String )
			return $"\"{el.GetString()}\"";

		return Truncate( el.GetRawText() );
	}

	private static string Truncate( string text, int maxLength = 80 )
	{
		if ( string.IsNullOrEmpty( text ) || text.Length <= maxLength )
			return text ?? "";

		return text[..(maxLength - 3)] + "...";
	}

	private static string Plural( int count )
	{
		return count == 1 ? "" : "s";
	}
}
