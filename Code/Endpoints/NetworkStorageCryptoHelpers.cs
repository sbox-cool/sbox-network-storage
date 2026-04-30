using System;
using System.Text;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static string Base64UrlEncode( byte[] value )
		=> Convert.ToBase64String( value ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

	private static string TruncateForLog( string value, int maxLength )
	{
		if ( string.IsNullOrEmpty( value ) || value.Length <= maxLength )
			return value;

		return $"{value[..maxLength]}...";
	}

	private static string ToHex( byte[] value )
	{
		var sb = new StringBuilder( value.Length * 2 );
		foreach ( var b in value )
			sb.Append( b.ToString( "x2" ) );
		return sb.ToString();
	}

	private static (byte[] Ciphertext, byte[] Tag) Aes256GcmEncrypt( byte[] key, byte[] iv, byte[] plaintext, byte[] aad )
	{
		var roundKeys = Aes256ExpandKey( key );
		var h = AesEncryptBlock( new byte[16], roundKeys );
		var j0 = new byte[16];
		Array.Copy( iv, 0, j0, 0, 12 );
		j0[15] = 1;

		var ciphertext = new byte[plaintext.Length];
		var counter = CopyBytes( j0 );
		var offset = 0;
		while ( offset < plaintext.Length )
		{
			IncrementCounter32( counter );
			var stream = AesEncryptBlock( counter, roundKeys );
			var count = Math.Min( 16, plaintext.Length - offset );
			for ( var i = 0; i < count; i++ )
				ciphertext[offset + i] = (byte)(plaintext[offset + i] ^ stream[i]);
			offset += count;
		}

		var ghash = GHash( h, aad, ciphertext );
		var s = AesEncryptBlock( j0, roundKeys );
		var tag = new byte[16];
		for ( var i = 0; i < 16; i++ )
			tag[i] = (byte)(s[i] ^ ghash[i]);
		return (ciphertext, tag);
	}

	private static void IncrementCounter32( byte[] counter )
	{
		for ( var i = 15; i >= 12; i-- )
		{
			counter[i]++;
			if ( counter[i] != 0 )
				break;
		}
	}

	private static byte[] GHash( byte[] h, byte[] aad, byte[] ciphertext )
	{
		var y = new byte[16];
		GHashBlocks( y, h, aad );
		GHashBlocks( y, h, ciphertext );
		var len = new byte[16];
		WriteUInt64BE( len, 0, (ulong)aad.Length * 8 );
		WriteUInt64BE( len, 8, (ulong)ciphertext.Length * 8 );
		XorBlock( y, len );
		return GfMultiply( y, h );
	}

	private static void GHashBlocks( byte[] y, byte[] h, byte[] data )
	{
		for ( var offset = 0; offset < data.Length; offset += 16 )
		{
			var block = new byte[16];
			Array.Copy( data, offset, block, 0, Math.Min( 16, data.Length - offset ) );
			XorBlock( y, block );
			var next = GfMultiply( y, h );
			Array.Copy( next, y, 16 );
		}
	}

	private static byte[] GfMultiply( byte[] x, byte[] y )
	{
		var z = new byte[16];
		var v = CopyBytes( y );
		for ( var i = 0; i < 128; i++ )
		{
			if ( (x[i / 8] & (1 << (7 - (i % 8)))) != 0 )
				XorBlock( z, v );
			var lsb = (v[15] & 1) != 0;
			ShiftRightOne( v );
			if ( lsb )
				v[0] ^= 0xe1;
		}
		return z;
	}

	private static void XorBlock( byte[] target, byte[] value )
	{
		for ( var i = 0; i < 16; i++ )
			target[i] ^= value[i];
	}

	private static void ShiftRightOne( byte[] value )
	{
		var carry = 0;
		for ( var i = 0; i < value.Length; i++ )
		{
			var nextCarry = value[i] & 1;
			value[i] = (byte)((value[i] >> 1) | (carry << 7));
			carry = nextCarry;
		}
	}

	private static void WriteUInt64BE( byte[] buffer, int offset, ulong value )
	{
		for ( var i = 7; i >= 0; i-- )
		{
			buffer[offset + i] = (byte)value;
			value >>= 8;
		}
	}

}
