using System;
using System.Collections.Generic;

namespace Sandbox;

public static partial class NetworkStorage
{
	private sealed class EndpointReference
	{
		public string Raw { get; init; }
		public string Slug { get; init; }
		public bool IsUrl { get; init; }
		public string ProjectId { get; init; }
		public string PublicApiKey { get; init; }
		public string BaseUrl { get; init; }
		public string ApiVersion { get; init; }
		public bool SecretKeyRequested { get; init; }
	}

	private static EndpointReference ResolveEndpointReference( string endpoint )
	{
		var raw = endpoint?.Trim() ?? "";
		if ( !Uri.TryCreate( raw, UriKind.Absolute, out var uri ) ||
			(uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) )
		{
			return new EndpointReference { Raw = raw, Slug = raw };
		}

		var query = ParseQueryString( uri.Query );
		var segments = uri.AbsolutePath.Split( '/', StringSplitOptions.RemoveEmptyEntries );
		var endpointsIndex = Array.FindIndex( segments, s => string.Equals( s, "endpoints", StringComparison.OrdinalIgnoreCase ) );
		var projectId = endpointsIndex >= 0 && endpointsIndex + 1 < segments.Length
			? Uri.UnescapeDataString( segments[endpointsIndex + 1] )
			: "";
		var slug = endpointsIndex >= 0 && endpointsIndex + 2 < segments.Length
			? Uri.UnescapeDataString( segments[endpointsIndex + 2] )
			: ReadQueryValue( query, "slug", "endpoint", "endpointSlug", "_endpointSlug" );
		var apiVersion = endpointsIndex > 0 && IsApiVersionSegment( segments[endpointsIndex - 1] )
			? Uri.UnescapeDataString( segments[endpointsIndex - 1] )
			: null;
		var baseUrl = BuildBaseUrlFromEndpointUri( uri, segments, endpointsIndex, apiVersion );
		var publicApiKey = ReadQueryValue( query, "apiKey", "publicKey", "public-key", "x-public-key" );
		var secretRequested = query.ContainsKey( "secret-key" );
		foreach ( var key in DedicatedSecretLaunchKeys )
			secretRequested |= query.ContainsKey( key );

		return new EndpointReference
		{
			Raw = raw,
			Slug = slug,
			IsUrl = true,
			ProjectId = projectId,
			PublicApiKey = publicApiKey,
			BaseUrl = baseUrl,
			ApiVersion = apiVersion,
			SecretKeyRequested = secretRequested
		};
	}

	private static void ApplyEndpointReferenceConfiguration( EndpointReference endpoint )
	{
		if ( endpoint?.IsUrl != true ) return;

		if ( string.IsNullOrWhiteSpace( endpoint.Slug ) )
			throw new ArgumentException( "Endpoint URL must include a slug after /endpoints/{projectId}/{slug}, or a slug query parameter." );

		if ( !IsConfigured && !string.IsNullOrWhiteSpace( endpoint.ProjectId ) && !string.IsNullOrWhiteSpace( endpoint.PublicApiKey ) )
		{
			Configure( endpoint.ProjectId, endpoint.PublicApiKey, endpoint.BaseUrl, endpoint.ApiVersion );
			return;
		}

		if ( IsConfigured && !string.IsNullOrWhiteSpace( endpoint.ProjectId ) &&
			!string.Equals( endpoint.ProjectId, ProjectId, StringComparison.Ordinal ) && NetworkStorageLogConfig.LogConfig )
		{
			Log.Warning( "[NetworkStorage] Endpoint URL project id differs from configured project id; using configured credentials." );
		}
	}

	private static Dictionary<string, string> ParseQueryString( string query )
	{
		var result = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( string.IsNullOrWhiteSpace( query ) ) return result;

		var trimmed = query[0] == '?' ? query[1..] : query;
		foreach ( var pair in trimmed.Split( '&', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var separator = pair.IndexOf( '=' );
			var key = separator >= 0 ? pair[..separator] : pair;
			var value = separator >= 0 ? pair[(separator + 1)..] : "";
			result[Uri.UnescapeDataString( key.Replace( '+', ' ' ) )] = Uri.UnescapeDataString( value.Replace( '+', ' ' ) );
		}

		return result;
	}

	private static string ReadQueryValue( Dictionary<string, string> query, params string[] keys )
	{
		foreach ( var key in keys )
		{
			if ( query.TryGetValue( key, out var value ) && !string.IsNullOrWhiteSpace( value ) )
				return value;
		}

		return null;
	}

	private static string BuildBaseUrlFromEndpointUri( Uri uri, string[] segments, int endpointsIndex, string apiVersion )
	{
		if ( string.IsNullOrWhiteSpace( apiVersion ) || endpointsIndex <= 0 )
			return uri.GetLeftPart( UriPartial.Authority );

		var prefixCount = endpointsIndex - 1;
		if ( prefixCount <= 0 )
			return uri.GetLeftPart( UriPartial.Authority );

		return uri.GetLeftPart( UriPartial.Authority ) + "/" + string.Join( '/', segments[..prefixCount] );
	}

	private static bool IsApiVersionSegment( string segment )
		=> !string.IsNullOrWhiteSpace( segment ) && segment.Length >= 2 && segment[0] == 'v' && char.IsDigit( segment[1] );

	private static string NormalizeSecretKey( string secretKey )
		=> string.IsNullOrWhiteSpace( secretKey ) ? null : secretKey.Trim();

	private static bool _ignoredEndpointUrlSecretLogged;
	private static bool _insecureSecretTransportLogged;
	private static bool _dedicatedSecretTransportLogged;

	private static void LogIgnoredEndpointUrlSecretOnce( EndpointReference endpoint )
	{
		if ( endpoint?.SecretKeyRequested != true || _ignoredEndpointUrlSecretLogged || !NetworkStorageLogConfig.LogConfig )
			return;

		_ignoredEndpointUrlSecretLogged = true;
		Log.Warning( "[NetworkStorage] Ignoring endpoint URL secret key flag because this process is not a dedicated server host." );
	}

	private static void LogDedicatedSecretTransportOnce( string headerName )
	{
		if ( _dedicatedSecretTransportLogged || !NetworkStorageLogConfig.LogConfig ) return;

		_dedicatedSecretTransportLogged = true;
		Log.Info( $"[NetworkStorage] Dedicated server secret key enabled for Network Storage requests (source={DedicatedServerSecretKeySource}, transport=https-header:{headerName}, urlFlag=secret-key=1)." );
	}

	private static void LogInsecureSecretTransportOnce()
	{
		if ( _insecureSecretTransportLogged || !NetworkStorageLogConfig.LogConfig ) return;

		_insecureSecretTransportLogged = true;
		Log.Warning( "[NetworkStorage] Dedicated endpoint secret key was not sent because the API root is not HTTPS." );
	}
}
