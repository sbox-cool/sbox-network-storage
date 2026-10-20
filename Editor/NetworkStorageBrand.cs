using Sandbox;
using Editor;

/// <summary>
/// Network Storage brand mark for editor headers: the lime "Sharp companion" cube from
/// <c>Assets/Branding/icon.svg</c> (viewBox 0 0 64 64, no tile). The geometry is generated
/// from that file with its transforms baked in, so the editor mark matches the owner
/// console and sboxns.com. Painting uses only <c>Paint</c> polygon and line calls over
/// static data; scratch buffers are allocated once, so painting allocates nothing per frame.
/// </summary>
public static class NetworkStorageBrand
{
	/// <summary>Companion face and mouth ink (#132300).</summary>
	private static readonly Color Ink = Hex( 0x132300 );

	/// <summary>Mouth stroke width in icon units (stroke-width 1.37).</summary>
	private const float MouthWidth = 1.37f;

	/// <summary>Filled shapes in document order, in 64-unit icon space.</summary>
	private static readonly Shape[] Shapes = new Shape[]
	{
		new Shape( Hex( 0xa3f55c ), new Vector2[]
		{
			new Vector2( 8f, 18f ), new Vector2( 32f, 10f ), new Vector2( 56f, 18f ), new Vector2( 32f, 26f ),
		} ),
		new Shape( Hex( 0x70e000 ), new Vector2[]
		{
			new Vector2( 8f, 18f ), new Vector2( 32f, 26f ), new Vector2( 32f, 54f ), new Vector2( 8f, 46f ),
		} ),
		new Shape( Hex( 0x58b300 ), new Vector2[]
		{
			new Vector2( 32f, 26f ), new Vector2( 56f, 18f ), new Vector2( 56f, 46f ), new Vector2( 32f, 54f ),
		} ),
		new Shape( Hex( 0xc3ff8a ), new Vector2[]
		{
			new Vector2( 8f, 18f ), new Vector2( 32f, 10f ), new Vector2( 32f, 26f ),
		} ),
		new Shape( Hex( 0xa3f55c ), new Vector2[]
		{
			new Vector2( 8f, 18f ), new Vector2( 32f, 26f ), new Vector2( 32f, 28f ), new Vector2( 8f, 20f ),
		} ),
		new Shape( Hex( 0x64ca00 ), new Vector2[]
		{
			new Vector2( 8f, 42f ), new Vector2( 32f, 50f ), new Vector2( 32f, 54f ), new Vector2( 8f, 46f ),
		} ),
		new Shape( Hex( 0x469000 ), new Vector2[]
		{
			new Vector2( 32f, 50f ), new Vector2( 56f, 42f ), new Vector2( 56f, 46f ), new Vector2( 32f, 54f ),
		} ),
		new Shape( Hex( 0x469000 ), new Vector2[]
		{
			new Vector2( 54f, 18.667f ), new Vector2( 56f, 18f ), new Vector2( 56f, 46f ), new Vector2( 54f, 46.667f ),
		} ),
		new Shape( Hex( 0xfff7ed ), new Vector2[]
		{
			new Vector2( 16.55f, 31.43f ), new Vector2( 16.52f, 31.155f ), new Vector2( 16.43f, 30.88f ), new Vector2( 16.28f, 30.625f ),
			new Vector2( 16.1f, 30.395f ), new Vector2( 15.87f, 30.2233f ), new Vector2( 15.64f, 30.1017f ), new Vector2( 11.98f, 28.8917f ),
			new Vector2( 11.74f, 28.8417f ), new Vector2( 11.52f, 28.8683f ), new Vector2( 11.33f, 28.97f ), new Vector2( 11.19f, 29.1333f ),
			new Vector2( 11.1f, 29.3483f ), new Vector2( 11.07f, 29.6033f ), new Vector2( 11.07f, 31.3133f ), new Vector2( 11.1f, 31.5883f ),
			new Vector2( 11.19f, 31.8633f ), new Vector2( 11.33f, 32.13f ), new Vector2( 11.52f, 32.3483f ), new Vector2( 11.74f, 32.5317f ),
			new Vector2( 11.98f, 32.6417f ), new Vector2( 15.64f, 33.8617f ), new Vector2( 15.87f, 33.9033f ), new Vector2( 16.1f, 33.875f ),
			new Vector2( 16.28f, 33.775f ), new Vector2( 16.43f, 33.61f ), new Vector2( 16.52f, 33.395f ), new Vector2( 16.55f, 33.14f ),
		} ),
		new Shape( Hex( 0x132300 ), new Vector2[]
		{
			new Vector2( 15.64f, 32.5817f ), new Vector2( 15.61f, 32.3767f ), new Vector2( 15.55f, 32.1667f ), new Vector2( 15.44f, 31.965f ),
			new Vector2( 15.3f, 31.7983f ), new Vector2( 15.13f, 31.6667f ), new Vector2( 14.95f, 31.5867f ), new Vector2( 14.19f, 31.3333f ),
			new Vector2( 14.01f, 31.2933f ), new Vector2( 13.85f, 31.32f ), new Vector2( 13.71f, 31.3933f ), new Vector2( 13.6f, 31.5117f ),
			new Vector2( 13.53f, 31.6733f ), new Vector2( 13.5f, 31.8683f ), new Vector2( 13.5f, 32.3783f ), new Vector2( 13.53f, 32.5833f ),
			new Vector2( 13.6f, 32.7917f ), new Vector2( 13.71f, 32.9933f ), new Vector2( 13.85f, 33.16f ), new Vector2( 14.01f, 33.2933f ),
			new Vector2( 14.19f, 33.3733f ), new Vector2( 14.95f, 33.6367f ), new Vector2( 15.13f, 33.6667f ), new Vector2( 15.3f, 33.6383f ),
			new Vector2( 15.44f, 33.565f ), new Vector2( 15.55f, 33.4467f ), new Vector2( 15.61f, 33.2867f ), new Vector2( 15.64f, 33.0917f ),
		} ),
		new Shape( Hex( 0x4c9a00 ), new Vector2[]
		{
			new Vector2( 16.86f, 30.6783f ), new Vector2( 16.84f, 30.5017f ), new Vector2( 16.78f, 30.3117f ), new Vector2( 16.68f, 30.1383f ),
			new Vector2( 16.55f, 29.99f ), new Vector2( 16.41f, 29.8733f ), new Vector2( 16.25f, 29.8f ), new Vector2( 11.37f, 28.1733f ),
			new Vector2( 11.21f, 28.14f ), new Vector2( 11.07f, 28.1633f ), new Vector2( 10.94f, 28.225f ), new Vector2( 10.84f, 28.3417f ),
			new Vector2( 10.78f, 28.4817f ), new Vector2( 10.76f, 28.655f ), new Vector2( 10.76f, 29.675f ), new Vector2( 10.78f, 29.8617f ),
			new Vector2( 10.84f, 30.0417f ), new Vector2( 10.94f, 30.215f ), new Vector2( 11.07f, 30.3633f ), new Vector2( 11.21f, 30.49f ),
			new Vector2( 11.37f, 30.5633f ), new Vector2( 16.25f, 32.19f ), new Vector2( 16.41f, 32.2133f ), new Vector2( 16.55f, 32.2f ),
			new Vector2( 16.68f, 32.1283f ), new Vector2( 16.78f, 32.0217f ), new Vector2( 16.84f, 31.8717f ), new Vector2( 16.86f, 31.7083f ),
		} ),
		new Shape( Hex( 0x132300 ), new Vector2[]
		{
			new Vector2( 16.86f, 32.3083f ), new Vector2( 16.85f, 32.21f ), new Vector2( 16.82f, 32.115f ), new Vector2( 16.77f, 32.0333f ),
			new Vector2( 16.7f, 31.955f ), new Vector2( 16.63f, 31.8967f ), new Vector2( 16.55f, 31.86f ), new Vector2( 11.07f, 30.0333f ),
			new Vector2( 10.99f, 30.0167f ), new Vector2( 10.91f, 30.03f ), new Vector2( 10.85f, 30.06f ), new Vector2( 10.8f, 30.1183f ),
			new Vector2( 10.77f, 30.1833f ), new Vector2( 10.76f, 30.275f ), new Vector2( 10.76f, 30.275f ), new Vector2( 10.77f, 30.3633f ),
			new Vector2( 10.8f, 30.4583f ), new Vector2( 10.85f, 30.54f ), new Vector2( 10.91f, 30.62f ), new Vector2( 10.99f, 30.6767f ),
			new Vector2( 11.07f, 30.7133f ), new Vector2( 16.55f, 32.54f ), new Vector2( 16.63f, 32.5567f ), new Vector2( 16.7f, 32.545f ),
			new Vector2( 16.77f, 32.5133f ), new Vector2( 16.82f, 32.465f ), new Vector2( 16.85f, 32.39f ), new Vector2( 16.86f, 32.3083f ),
		} ),
		new Shape( Hex( 0xfff7ed ), new Vector2[]
		{
			new Vector2( 24.93f, 34.2333f ), new Vector2( 24.9f, 33.9483f ), new Vector2( 24.81f, 33.6733f ), new Vector2( 24.67f, 33.4167f ),
			new Vector2( 24.48f, 33.1883f ), new Vector2( 24.26f, 33.015f ), new Vector2( 24.02f, 32.895f ), new Vector2( 20.36f, 31.685f ),
			new Vector2( 20.13f, 31.6333f ), new Vector2( 19.9f, 31.6617f ), new Vector2( 19.72f, 31.7617f ), new Vector2( 19.57f, 31.9267f ),
			new Vector2( 19.48f, 32.1417f ), new Vector2( 19.45f, 32.3967f ), new Vector2( 19.45f, 34.1067f ), new Vector2( 19.48f, 34.3817f ),
			new Vector2( 19.57f, 34.6567f ), new Vector2( 19.72f, 34.9217f ), new Vector2( 19.9f, 35.1417f ), new Vector2( 20.13f, 35.3233f ),
			new Vector2( 20.36f, 35.435f ), new Vector2( 24.02f, 36.655f ), new Vector2( 24.26f, 36.695f ), new Vector2( 24.48f, 36.6683f ),
			new Vector2( 24.67f, 36.5667f ), new Vector2( 24.81f, 36.4033f ), new Vector2( 24.9f, 36.1883f ), new Vector2( 24.93f, 35.9333f ),
		} ),
		new Shape( Hex( 0x132300 ), new Vector2[]
		{
			new Vector2( 24.02f, 35.375f ), new Vector2( 24f, 35.1683f ), new Vector2( 23.93f, 34.96f ), new Vector2( 23.82f, 34.7683f ),
			new Vector2( 23.68f, 34.5917f ), new Vector2( 23.51f, 34.46f ), new Vector2( 23.33f, 34.38f ), new Vector2( 22.57f, 34.1267f ),
			new Vector2( 22.39f, 34.0867f ), new Vector2( 22.23f, 34.1133f ), new Vector2( 22.09f, 34.1867f ), new Vector2( 21.98f, 34.305f ),
			new Vector2( 21.91f, 34.4767f ), new Vector2( 21.89f, 34.66f ), new Vector2( 21.89f, 35.17f ), new Vector2( 21.91f, 35.3767f ),
			new Vector2( 21.98f, 35.585f ), new Vector2( 22.09f, 35.7867f ), new Vector2( 22.23f, 35.9533f ), new Vector2( 22.39f, 36.0867f ),
			new Vector2( 22.57f, 36.1767f ), new Vector2( 23.33f, 36.43f ), new Vector2( 23.51f, 36.46f ), new Vector2( 23.68f, 36.4317f ),
			new Vector2( 23.82f, 36.3583f ), new Vector2( 23.93f, 36.24f ), new Vector2( 24f, 36.0783f ), new Vector2( 24.02f, 35.885f ),
		} ),
		new Shape( Hex( 0x4c9a00 ), new Vector2[]
		{
			new Vector2( 25.24f, 33.4717f ), new Vector2( 25.22f, 33.295f ), new Vector2( 25.16f, 33.105f ), new Vector2( 25.06f, 32.9317f ),
			new Vector2( 24.93f, 32.7833f ), new Vector2( 24.79f, 32.6667f ), new Vector2( 24.63f, 32.5933f ), new Vector2( 19.75f, 30.9667f ),
			new Vector2( 19.59f, 30.9333f ), new Vector2( 19.45f, 30.9567f ), new Vector2( 19.32f, 31.0183f ), new Vector2( 19.22f, 31.135f ),
			new Vector2( 19.16f, 31.275f ), new Vector2( 19.14f, 31.4483f ), new Vector2( 19.14f, 32.4683f ), new Vector2( 19.16f, 32.655f ),
			new Vector2( 19.22f, 32.835f ), new Vector2( 19.32f, 33.0083f ), new Vector2( 19.45f, 33.1567f ), new Vector2( 19.59f, 33.2833f ),
			new Vector2( 19.75f, 33.3567f ), new Vector2( 24.63f, 34.9833f ), new Vector2( 24.79f, 35.0067f ), new Vector2( 24.93f, 34.9933f ),
			new Vector2( 25.06f, 34.9217f ), new Vector2( 25.16f, 34.815f ), new Vector2( 25.22f, 34.665f ), new Vector2( 25.24f, 34.5017f ),
		} ),
		new Shape( Hex( 0x132300 ), new Vector2[]
		{
			new Vector2( 25.24f, 35.1017f ), new Vector2( 25.23f, 35.0033f ), new Vector2( 25.2f, 34.9083f ), new Vector2( 25.15f, 34.8267f ),
			new Vector2( 25.09f, 34.7467f ), new Vector2( 25.01f, 34.69f ), new Vector2( 24.93f, 34.6533f ), new Vector2( 19.45f, 32.8267f ),
			new Vector2( 19.37f, 32.81f ), new Vector2( 19.3f, 32.8217f ), new Vector2( 19.23f, 32.8533f ), new Vector2( 19.18f, 32.9117f ),
			new Vector2( 19.15f, 32.9767f ), new Vector2( 19.14f, 33.0683f ), new Vector2( 19.14f, 33.0683f ), new Vector2( 19.15f, 33.1567f ),
			new Vector2( 19.18f, 33.2517f ), new Vector2( 19.23f, 33.3333f ), new Vector2( 19.3f, 33.4117f ), new Vector2( 19.37f, 33.47f ),
			new Vector2( 19.45f, 33.5067f ), new Vector2( 24.93f, 35.3333f ), new Vector2( 25.01f, 35.35f ), new Vector2( 25.09f, 35.3467f ),
			new Vector2( 25.15f, 35.3067f ), new Vector2( 25.2f, 35.2583f ), new Vector2( 25.23f, 35.1833f ), new Vector2( 25.24f, 35.1017f ),
		} ),
		new Shape( Hex( 0x132300 ), new Vector2[]
		{
			new Vector2( 38f, 30f ), new Vector2( 52f, 25.3333f ), new Vector2( 52f, 37.3333f ), new Vector2( 48f, 38.6667f ),
			new Vector2( 48f, 41.6667f ), new Vector2( 42f, 43.6667f ), new Vector2( 42f, 40.6667f ), new Vector2( 38f, 42f ),
		} ),
		new Shape( Hex( 0xa3f55c ), new Vector2[]
		{
			new Vector2( 41f, 32f ), new Vector2( 49f, 29.3333f ), new Vector2( 49f, 31.3333f ), new Vector2( 41f, 34f ),
		} ),
	};

	/// <summary>Mouth polyline in 64-unit icon space, stroked at <see cref="MouthWidth"/>.</summary>
	private static readonly Vector2[] Mouth = new Vector2[]
	{
		new Vector2( 11.14f, 35.4317f ), new Vector2( 13.81f, 38.8867f ), new Vector2( 22.19f, 41.68f ), new Vector2( 25.24f, 39.7917f ),
	};

	private static readonly Vector2[] MouthScratch = new Vector2[Mouth.Length];

	/// <summary>
	/// Draw the cube at the given position and size. The 64-unit viewBox maps onto the
	/// square, exactly as <c>icon.svg</c> does. Leaves the pen transparent, so callers must
	/// set their own pen before drawing text afterwards.
	/// </summary>
	public static void DrawIcon( float x, float y, float size )
	{
		var scale = size / 64f;

		for ( int i = 0; i < Shapes.Length; i++ )
		{
			var shape = Shapes[i];

			for ( int p = 0; p < shape.Unit.Length; p++ )
			{
				shape.Scratch[p] = new Vector2( x + shape.Unit[p].x * scale, y + shape.Unit[p].y * scale );
			}

			Paint.SetBrush( shape.Fill );
			Paint.SetPen( Color.Transparent );
			Paint.DrawPolygon( shape.Scratch );
		}

		for ( int p = 0; p < Mouth.Length; p++ )
		{
			MouthScratch[p] = new Vector2( x + Mouth[p].x * scale, y + Mouth[p].y * scale );
		}

		Paint.SetPen( Ink, MouthWidth * scale );
		Paint.DrawLine( MouthScratch );
		Paint.SetPen( Color.Transparent );
	}

	/// <summary>Opaque colour from a 0xRRGGBB literal.</summary>
	private static Color Hex( int rgb ) =>
		new Color( ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f );

	/// <summary>One filled polygon: its colour, its icon-space outline, and a reused draw buffer.</summary>
	private sealed class Shape
	{
		public readonly Color Fill;
		public readonly Vector2[] Unit;
		public readonly Vector2[] Scratch;

		public Shape( Color fill, Vector2[] unit )
		{
			Fill = fill;
			Unit = unit;
			Scratch = new Vector2[unit.Length];
		}
	}
}
