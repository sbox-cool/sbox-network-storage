using Sandbox;
using Editor;

/// <summary>
/// Approved brand mark for s&amp;box Network Storage (P5 Sky).
/// Vector redraw of <c>Assets/Branding/icon.svg</c> using only the supported
/// <c>Paint</c> rect APIs, so editor headers never depend on texture imports.
/// Tile is P5 primary (#0f6a86), glyph is white (#ffffff); all glyph data is
/// static: painting allocates nothing per frame.
/// </summary>
public static class NetworkStorageBrand
{
	/// <summary>P5 primary tile (#0f6a86).</summary>
	private static readonly Color TileColor = new Color( 0.0588f, 0.4157f, 0.5255f );

	/// <summary>White glyph (#ffffff).</summary>
	private static readonly Color GlyphColor = Color.White;

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
	/// Draw the sky tile plus white glyph at the given position and size.
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
