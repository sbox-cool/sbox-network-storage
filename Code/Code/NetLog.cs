using System;
using System.Collections.Generic;

namespace Sandbox;

/// <summary>
/// Static ring buffer that captures Network Storage events for in-game debug windows.
/// Used internally by NetworkStorage and available for game code to log custom events.
/// </summary>
public static class NetLog
{
	public enum EntryKind { Request, Response, Error, Info }

	public record Entry(
		DateTime Time,
		EntryKind Kind,
		string Tag,
		string Message
	);

	private const int MaxEntries = 200;
	private static readonly List<Entry> _entries = new();
	private static int _version;

	public static IReadOnlyList<Entry> Entries => _entries;
	public static int Version => _version;

	public static void Add( EntryKind kind, string tag, string message )
	{
		_entries.Add( new Entry( DateTime.Now, kind, tag, message ) );
		if ( _entries.Count > MaxEntries )
			_entries.RemoveAt( 0 );
		_version++;
	}

	public static void Request( string tag, string message ) => Add( EntryKind.Request, tag, message );
	public static void Response( string tag, string message ) => Add( EntryKind.Response, tag, message );
	public static void Error( string tag, string message ) => Add( EntryKind.Error, tag, message );
	public static void Info( string tag, string message ) => Add( EntryKind.Info, tag, message );

	public static void Clear()
	{
		_entries.Clear();
		_version++;
	}
}
