#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public sealed class StepIdFixPlan
{
	public string FilePath { get; init; }
	public string ResourceId { get; init; }
	public string OriginalText { get; init; }
	public string UpdatedText { get; init; }
	public int AddedCount { get; init; }
	public bool HasChanges => AddedCount > 0 && OriginalText != UpdatedText;
}

public static class StepIdAutoFixer
{
	public static StepIdFixPlan BuildPlan( string filePath, string resourceId )
	{
		var original = File.ReadAllText( filePath );
		var updated = AddMissingStepIds( original, out var added );
		return new StepIdFixPlan
		{
			FilePath = filePath,
			ResourceId = resourceId,
			OriginalText = original,
			UpdatedText = updated,
			AddedCount = added
		};
	}

	public static void ApplyPlanWithBackup( StepIdFixPlan plan )
	{
		if ( plan == null || !plan.HasChanges ) return;
		var backupPath = $"{plan.FilePath}.bak.{DateTime.Now:yyyyMMddHHmmss}";
		File.Copy( plan.FilePath, backupPath, overwrite: false );
		File.WriteAllText( plan.FilePath, plan.UpdatedText );
	}

	private static string AddMissingStepIds( string source, out int added )
	{
		added = 0;
		var newline = source.Contains( "\r\n" ) ? "\r\n" : "\n";
		var lines = source.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Split( '\n' ).ToList();
		var usedIds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < lines.Count; i++ )
		{
			if ( !IsStepsLine( lines[i], out var stepsIndent ) ) continue;

			var itemIndent = -1;
			for ( var j = i + 1; j < lines.Count; j++ )
			{
				var line = lines[j];
				if ( string.IsNullOrWhiteSpace( line ) ) continue;
				var indent = CountIndent( line );
				var trimmed = line.TrimStart();
				if ( indent <= stepsIndent && !trimmed.StartsWith( "#" ) ) break;
				if ( !trimmed.StartsWith( "-" ) ) continue;
				if ( itemIndent < 0 ) itemIndent = indent;
				if ( indent != itemIndent ) continue;

				var blockEnd = FindBlockEnd( lines, j + 1, itemIndent, stepsIndent );
				CollectExistingIds( lines, j, blockEnd, usedIds );
				if ( StepHasId( lines, j, blockEnd ) )
				{
					j = blockEnd - 1;
					continue;
				}

				var id = UniqueId( BuildReadableId( lines, j, blockEnd, added + 1 ), usedIds );
				usedIds.Add( id );
				InsertId( lines, j, id );
				added++;
				j = blockEnd;
			}
		}

		return string.Join( newline, lines );
	}

	private static bool IsStepsLine( string line, out int indent )
	{
		indent = CountIndent( line );
		var trimmed = line.Trim();
		return trimmed == "steps:";
	}

	private static int FindBlockEnd( List<string> lines, int start, int itemIndent, int stepsIndent )
	{
		for ( var i = start; i < lines.Count; i++ )
		{
			if ( string.IsNullOrWhiteSpace( lines[i] ) ) continue;
			var indent = CountIndent( lines[i] );
			var trimmed = lines[i].TrimStart();
			if ( indent <= stepsIndent && !trimmed.StartsWith( "#" ) ) return i;
			if ( indent == itemIndent && trimmed.StartsWith( "-" ) ) return i;
		}
		return lines.Count;
	}

	private static bool StepHasId( List<string> lines, int start, int end )
	{
		var first = lines[start].TrimStart();
		if ( Regex.IsMatch( first, @"^-\s+id\s*:\s*\S+" ) ) return true;
		var itemIndent = CountIndent( lines[start] );
		for ( var i = start + 1; i < end; i++ )
		{
			var trimmed = lines[i].TrimStart();
			if ( CountIndent( lines[i] ) > itemIndent && Regex.IsMatch( trimmed, @"^id\s*:\s*\S+" ) ) return true;
		}
		return false;
	}

	private static void CollectExistingIds( List<string> lines, int start, int end, HashSet<string> usedIds )
	{
		for ( var i = start; i < end; i++ )
		{
			var match = Regex.Match( lines[i], @"(?:^|[-\s])id\s*:\s*['\""']?([A-Za-z0-9_-]+)" );
			if ( match.Success ) usedIds.Add( match.Groups[1].Value );
		}
	}

	private static void InsertId( List<string> lines, int index, string id )
	{
		var line = lines[index];
		var indent = new string( ' ', CountIndent( line ) );
		var afterDash = line.TrimStart()[1..].TrimStart();
		if ( string.IsNullOrWhiteSpace( afterDash ) )
		{
			lines.Insert( index + 1, $"{indent}  id: {id}" );
			return;
		}
		lines[index] = $"{indent}- id: {id}";
		lines.Insert( index + 1, $"{indent}  {afterDash}" );
	}

	private static string BuildReadableId( List<string> lines, int start, int end, int ordinal )
	{
		var type = FindValue( lines, start, end, "type" );
		var collection = FindValue( lines, start, end, "collection" );
		var target = FindValue( lines, start, end, "target" ) ?? FindValue( lines, start, end, "path" );
		var parts = new[] { VerbForType( type ), collection, target }.Where( x => !string.IsNullOrWhiteSpace( x ) ).ToList();
		var candidate = Slugify( string.Join( "-", parts ) );
		return string.IsNullOrWhiteSpace( candidate ) ? $"step-{ordinal:000}" : candidate;
	}

	private static string FindValue( List<string> lines, int start, int end, string key )
	{
		var pattern = new Regex( $@"(?:^|[-\s]){Regex.Escape( key )}\s*:\s*['\""']?([^'\""#]+)" );
		for ( var i = start; i < end; i++ )
		{
			var match = pattern.Match( lines[i] );
			if ( match.Success ) return match.Groups[1].Value.Trim();
		}
		return null;
	}

	private static string VerbForType( string type ) => (type ?? "").ToLowerInvariant() switch
	{
		"read" => "read",
		"write" => "save",
		"set" => "set",
		"create" => "create",
		"transform" => "set",
		"lookup" => "lookup",
		"condition" => "check",
		"assert" => "validate",
		_ => type
	};

	private static string UniqueId( string baseId, HashSet<string> used )
	{
		var id = string.IsNullOrWhiteSpace( baseId ) ? "step" : baseId;
		if ( !used.Contains( id ) ) return id;
		for ( var i = 2; i < 999; i++ )
		{
			var next = $"{id}-{i:00}";
			if ( !used.Contains( next ) ) return next;
		}
		return $"{id}-{used.Count + 1}";
	}

	private static string Slugify( string value )
	{
		value = Regex.Replace( value ?? "", @"\{\{.*?\}\}", "" );
		value = Regex.Replace( value.ToLowerInvariant(), @"[^a-z0-9]+", "-" ).Trim( '-' );
		return string.IsNullOrWhiteSpace( value ) ? "" : value;
	}

	private static int CountIndent( string line )
	{
		var count = 0;
		while ( count < line.Length && line[count] == ' ' ) count++;
		return count;
	}
}
