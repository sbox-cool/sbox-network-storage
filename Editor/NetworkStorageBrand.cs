using Sandbox;
using Editor;

/// <summary>
/// Approved brand mark for s&amp;box Network Storage (Demo green).
/// Vector redraw of <c>Assets/Branding/icon.svg</c> using only the supported
/// <c>Paint</c> rect APIs, so editor headers never depend on texture imports.
/// Tile is demo green (#10b981), glyph is dark ink (#04120c); all glyph data is
/// static: painting allocates nothing per frame.
/// </summary>
public static class NetworkStorageBrand
{
	/// <summary>Demo green primary tile (#10b981).</summary>
	private static readonly Color TileColor = new Color( 16f / 255f, 185f / 255f, 129f / 255f );

	/// <summary>Dark ink glyph (#04120c).</summary>
	private static readonly Color GlyphColor = new Color( 4f / 255f, 18f / 255f, 12f / 255f );

	// Glyph bars in 64x64 icon space (x, y, w, h). Their union reproduces the
	// approved path exactly; overlaps share one brush so seams are invisible.
	private static readonly Rect[] GlyphBars = new Rect[]
	{
		new Rect( 6, 6, 28, 8 ),   // top bar
		new Rect( 6, 14, 8, 28 ),  // left stem
		new Rect( 26, 14, 8, 28 ), // middle stem
		new Rect( 26, 22, 32, 8 ), // upper step
		new Rect( 26, 34, 32, 8 ), // lower step
		new Rect( 50, 42, 8, 8 ),  // right stem
		new Rect( 26, 50, 32, 8 ), // bottom bar
	};

	/// <summary>
	/// Draw the green tile plus dark ink glyph at the given position and size.
	/// Only allocation-free <c>Paint</c> rect calls; leaves the pen transparent,
	/// so callers must set their own pen before drawing text afterwards.
	/// </summary>
	public static void DrawIcon( float x, float y, float size )
	{
		Paint.SetBrush( TileColor );
		Paint.SetPen( TileColor );
		Paint.DrawRect( new Rect( x, y, size, size ), 4 );

		var pad = size / 8f;
		var scale = (size - pad * 2f) / 64f;

		Paint.SetBrush( GlyphColor );
		Paint.SetPen( Color.Transparent );
		for ( int i = 0; i < GlyphBars.Length; i++ )
		{
			var bar = GlyphBars[i];
			Paint.DrawRect( new Rect( x + pad + bar.Left * scale, y + pad + bar.Top * scale, bar.Width * scale, bar.Height * scale ) );
		}
	}
}
