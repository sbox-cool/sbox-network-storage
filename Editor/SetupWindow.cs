using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Editor;

/// <summary>
/// Setup window for entering Network Storage credentials.
/// Stores project ID, public API key, and secret key safely in Editor/SyncTools/.env.
/// The .env file is in Editor/ which is excluded from publishing — secrets never ship with the game.
///
/// Access via Editor menu → Network Storage → Setup, or the Setup button in the Sync Tool.
/// </summary>
[Dock( "Editor", "Network Storage Setup", "key" )]
public class SetupWindow : DockWindow
{
	// ── Input state ──
	private string _projectId = "";
	private string _publicKey = "";
	private string _secretKey = "";
	private string _baseUrl = "https://api.sboxcool.com";
	private string _dataFolder = "Network Storage";
	private int _dataSourceIndex;

	// ── UI state ──
	private string _status = "";
	private string _statusColor = "white";

	// ── Test results ──
	private string _testProjectId;
	private string _testSecretKey;
	private string _testPublicKey;
	private string _testProjectTitle;
	private int _focusedField = -1;
	private List<FieldRect> _fields = new();
	private List<ButtonRect> _buttons = new();
	private Vector2 _mousePos;

	private struct FieldRect
	{
		public Rect Rect;
		public int Index;
	}

	private struct ButtonRect
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public SetupWindow()
	{
		Title = "Network Storage Setup";
		Size = new Vector2( 480, 620 );
		MinimumSize = new Vector2( 400, 520 );

		// Load existing config
		SyncToolConfig.Load();
		_projectId = SyncToolConfig.ProjectId;
		_publicKey = SyncToolConfig.PublicApiKey;
		_secretKey = SyncToolConfig.SecretKey;
		_baseUrl = SyncToolConfig.BaseUrl;
		_dataFolder = SyncToolConfig.DataFolder;
		_dataSourceIndex = SyncToolConfig.DataSource switch
		{
			SyncToolConfig.DataSourceMode.ApiOnly => 1,
			SyncToolConfig.DataSourceMode.JsonOnly => 2,
			_ => 0
		};
	}

	[Menu( "Editor", "Network Storage/Setup" )]
	public static void OpenWindow()
	{
		var window = new SetupWindow();
		window.Show();
	}

	// ──────────────────────────────────────────────────────
	//  Rendering
	// ──────────────────────────────────────────────────────

	protected override void OnPaint()
	{
		base.OnPaint();
		_fields.Clear();
		_buttons.Clear();

		var y = 38f;
		var pad = 20f;
		var w = Width - pad * 2;

		// ── Title ──
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "Network Storage Setup", TextFlag.LeftCenter );
		y += 32;

		// ── Description ──
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		Paint.DrawText( new Rect( pad, y, w, 14 ), "Credentials are stored in Editor/SyncTools/.env (gitignored, never published)", TextFlag.LeftCenter );
		y += 22;

		DrawSeparator( ref y, w, pad );

		// ── Project ID ──
		DrawInputField( ref y, pad, w, 0, "Project ID", _projectId, "From sbox.cool dashboard" );

		// ── Public API Key ──
		DrawInputField( ref y, pad, w, 1, "Public API Key", _publicKey, "sbox_ns_ prefix — used by game client" );

		// ── Secret Key ──
		DrawInputField( ref y, pad, w, 2, "Secret Key", MaskSecret( _secretKey ), "sbox_sk_ prefix — editor only, NEVER ships" );

		// ── Base URL ──
		DrawInputField( ref y, pad, w, 3, "Base URL", _baseUrl, "Default: https://api.sboxcool.com" );

		// ── Editor Data Folder ──
		DrawInputField( ref y, pad, w, 4, "Editor Data Folder", _dataFolder, "Subfolder under Editor/ (default: Network Storage)" );

		y += 4;
		DrawSeparator( ref y, w, pad );

		// ── Data Source Preference (for GET requests) ──
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Data Source (GET requests)", TextFlag.LeftCenter );
		y += 24;

		// Three-option toggle (all on one row)
		var thirdW = ( w - 16 ) / 3;
		var toggleY = y;
		DrawToggleButton( ref toggleY, pad, thirdW, 0, "API + Fallback", "API first, JSON fallback", _dataSourceIndex == 0 );
		toggleY = y;
		DrawToggleButton( ref toggleY, pad + thirdW + 8, thirdW, 1, "API Only", "Direct API, no fallback", _dataSourceIndex == 1 );
		toggleY = y;
		DrawToggleButton( ref toggleY, pad + ( thirdW + 8 ) * 2, thirdW, 2, "JSON Only", "Local files only", _dataSourceIndex == 2 );
		y += 42 + 12;

		DrawSeparator( ref y, w, pad );

		// ── Save button ──
		var saveBtnH = 32f;
		var saveBtnRect = new Rect( pad, y, w, saveBtnH );
		var saveHovered = saveBtnRect.IsInside( _mousePos );

		Paint.SetBrush( Color.Green.WithAlpha( saveHovered ? 0.25f : 0.15f ) );
		Paint.SetPen( Color.Green.WithAlpha( saveHovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( saveBtnRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.Green.WithAlpha( saveHovered ? 1f : 0.8f ) );
		Paint.DrawText( saveBtnRect, "Save Configuration", TextFlag.Center );

		_buttons.Add( new ButtonRect { Rect = saveBtnRect, Id = "save", OnClick = SaveConfig } );
		y += saveBtnH + 8;

		// ── Test Connection button ──
		var testBtnH = 28f;
		var testBtnRect = new Rect( pad, y, w, testBtnH );
		var testHovered = testBtnRect.IsInside( _mousePos );

		Paint.SetBrush( Color.White.WithAlpha( testHovered ? 0.1f : 0.04f ) );
		Paint.SetPen( Color.White.WithAlpha( testHovered ? 0.3f : 0.15f ) );
		Paint.DrawRect( testBtnRect, 4 );
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( testHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( testBtnRect, "Test Connection", TextFlag.Center );

		_buttons.Add( new ButtonRect { Rect = testBtnRect, Id = "test", OnClick = () => _ = TestConnection() } );
		y += testBtnH + 8;

		// ── Test results (per-credential breakdown) ──
		if ( _testProjectId != null )
		{
			DrawTestResult( ref y, pad, w, "Project ID", _testProjectId, _testProjectTitle );
			DrawTestResult( ref y, pad, w, "Secret Key", _testSecretKey );
			DrawTestResult( ref y, pad, w, "Public Key", _testPublicKey );
			y += 4;
		}

		// ── Status ──
		if ( !string.IsNullOrEmpty( _status ) )
		{
			Paint.SetDefaultFont( size: 9 );
			var col = _statusColor == "green" ? Color.Green : _statusColor == "red" ? Color.Red : Color.White.WithAlpha( 0.6f );
			Paint.SetPen( col );
			Paint.DrawText( new Rect( pad, y, w, 16 ), _status, TextFlag.LeftCenter );
			y += 22;
		}

		// ── File location ──
		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
		Paint.DrawText( new Rect( pad, y, w, 14 ), $"Saved to: {SyncToolConfig.EnvFilePath}", TextFlag.LeftCenter );
	}

	private void DrawInputField( ref float y, float pad, float w, int fieldIndex, string label, string value, string hint )
	{
		// Label
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( pad, y, w, 16 ), label, TextFlag.LeftCenter );
		y += 18;

		// Paste + Clear buttons on the right
		var btnW = 42f;
		var btnH = 22f;
		var btnGap = 4f;
		var btnY = y + 2;
		var clearBtnRect = new Rect( pad + w - btnW, btnY, btnW, btnH );
		var pasteBtnRect = new Rect( pad + w - btnW * 2 - btnGap, btnY, btnW, btnH );
		var fieldW = w - btnW * 2 - btnGap * 2 - 4;

		// Input box (narrower to make room for buttons)
		var fieldH = 26f;
		var fieldRect = new Rect( pad, y, fieldW, fieldH );
		var focused = _focusedField == fieldIndex;
		var hovered = fieldRect.IsInside( _mousePos );

		var bgAlpha = focused ? 0.12f : hovered ? 0.08f : 0.04f;
		var borderColor = focused ? Color.Cyan.WithAlpha( 0.5f ) : Color.White.WithAlpha( hovered ? 0.2f : 0.1f );

		Paint.SetBrush( Color.White.WithAlpha( bgAlpha ) );
		Paint.SetPen( borderColor );
		Paint.DrawRect( fieldRect, 3 );

		// Value or placeholder
		Paint.SetDefaultFont( size: 10 );
		if ( string.IsNullOrEmpty( value ) )
		{
			Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
			Paint.DrawText( new Rect( pad + 8, y, fieldW - 16, fieldH ), hint, TextFlag.LeftCenter );
		}
		else
		{
			Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
			Paint.DrawText( new Rect( pad + 8, y, fieldW - 16, fieldH ), value, TextFlag.LeftCenter );
		}

		// Cursor blink when focused
		if ( focused )
		{
			var cursorX = pad + 8 + MeasureTextWidth( value ?? "" );
			if ( cursorX > pad + fieldW - 8 ) cursorX = pad + fieldW - 8;
			Paint.SetPen( Color.Cyan.WithAlpha( 0.8f ) );
			Paint.DrawLine( new Vector2( cursorX, y + 5 ), new Vector2( cursorX, y + fieldH - 5 ) );
		}

		_fields.Add( new FieldRect { Rect = fieldRect, Index = fieldIndex } );

		// ── Paste button ──
		var pasteHovered = pasteBtnRect.IsInside( _mousePos );
		Paint.SetBrush( Color.Cyan.WithAlpha( pasteHovered ? 0.2f : 0.08f ) );
		Paint.SetPen( Color.Cyan.WithAlpha( pasteHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( pasteBtnRect, 3 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Cyan.WithAlpha( pasteHovered ? 1f : 0.7f ) );
		Paint.DrawText( pasteBtnRect, "Paste", TextFlag.Center );

		var fi = fieldIndex; // capture for lambda
		_buttons.Add( new ButtonRect { Rect = pasteBtnRect, Id = $"paste_{fieldIndex}", OnClick = () => PasteIntoField( fi ) } );

		// ── Clear button ──
		var clearHovered = clearBtnRect.IsInside( _mousePos );
		Paint.SetBrush( Color.Red.WithAlpha( clearHovered ? 0.2f : 0.08f ) );
		Paint.SetPen( Color.Red.WithAlpha( clearHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( clearBtnRect, 3 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Red.WithAlpha( clearHovered ? 1f : 0.7f ) );
		Paint.DrawText( clearBtnRect, "Clear", TextFlag.Center );

		_buttons.Add( new ButtonRect { Rect = clearBtnRect, Id = $"clear_{fieldIndex}", OnClick = () => { SetFieldValue( fi, "" ); Update(); } } );

		y += fieldH + 4;

		// Hint below (for key validation)
		if ( fieldIndex == 1 && !string.IsNullOrEmpty( _publicKey ) && !_publicKey.StartsWith( "sbox_ns_" ) )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.Red.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 12 ), "Must start with sbox_ns_", TextFlag.LeftCenter );
			y += 14;
		}
		else if ( fieldIndex == 2 && !string.IsNullOrEmpty( _secretKey ) && !_secretKey.StartsWith( "sbox_sk_" ) )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.Red.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 12 ), "Must start with sbox_sk_", TextFlag.LeftCenter );
			y += 14;
		}

		y += 4;
	}

	private void DrawToggleButton( ref float y, float x, float w, int index, string label, string desc, bool active )
	{
		var h = 42f;
		var rect = new Rect( x, y, w, h );
		var hovered = rect.IsInside( _mousePos );

		var color = active ? Color.Cyan : Color.White;
		var bgAlpha = active ? 0.12f : hovered ? 0.06f : 0.02f;
		var borderAlpha = active ? 0.4f : hovered ? 0.15f : 0.08f;

		Paint.SetBrush( color.WithAlpha( bgAlpha ) );
		Paint.SetPen( color.WithAlpha( borderAlpha ) );
		Paint.DrawRect( rect, 4 );

		Paint.SetDefaultFont( size: 10, weight: active ? 700 : 400 );
		Paint.SetPen( color.WithAlpha( active ? 0.9f : 0.5f ) );
		Paint.DrawText( new Rect( x, y + 4, w, 18 ), label, TextFlag.Center );

		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( color.WithAlpha( active ? 0.5f : 0.3f ) );
		Paint.DrawText( new Rect( x, y + 20, w, 14 ), desc, TextFlag.Center );

		if ( !active )
		{
			_buttons.Add( new ButtonRect
			{
				Rect = rect,
				Id = $"ds_{index}",
				OnClick = () =>
				{
					_dataSourceIndex = index;
					Update();
				}
			} );
		}

		y += h + 4;
	}

	private void DrawSeparator( ref float y, float w, float pad )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 10;
	}

	private string MaskSecret( string key )
	{
		if ( string.IsNullOrEmpty( key ) ) return "";
		if ( key.Length <= 12 ) return key;
		return key[..12] + new string( '*', Math.Min( key.Length - 12, 20 ) );
	}

	private float MeasureTextWidth( string text )
	{
		return text.Length * 6.5f;
	}

	// ──────────────────────────────────────────────────────
	//  Clipboard (via PowerShell — bypasses s&box sandbox)
	// ──────────────────────────────────────────────────────

	private static string GetClipboardText()
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = "-NoProfile -Command \"Get-Clipboard\"",
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var proc = Process.Start( psi );
			var text = proc?.StandardOutput.ReadToEnd()?.Trim();
			proc?.WaitForExit();
			return text ?? "";
		}
		catch
		{
			return "";
		}
	}

	private void PasteIntoField( int fieldIndex )
	{
		var clip = GetClipboardText();
		if ( !string.IsNullOrEmpty( clip ) )
		{
			// Replace the field entirely with clipboard content (paste replaces)
			SetFieldValue( fieldIndex, clip );
			_focusedField = fieldIndex;
			Update();
		}
	}

	// ──────────────────────────────────────────────────────
	//  Input handling
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		// Check buttons
		foreach ( var btn in _buttons )
		{
			if ( btn.Rect.IsInside( e.LocalPosition ) )
			{
				btn.OnClick?.Invoke();
				return;
			}
		}

		// Check input fields
		_focusedField = -1;
		foreach ( var field in _fields )
		{
			if ( field.Rect.IsInside( e.LocalPosition ) )
			{
				_focusedField = field.Index;
				break;
			}
		}
		Update();
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
		Update();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( _focusedField < 0 )
		{
			base.OnKeyPress( e );
			return;
		}

		var handled = true;

		if ( e.HasCtrl && e.Key == KeyCode.V )
		{
			PasteIntoField( _focusedField );
		}
		else if ( e.HasCtrl && e.Key == KeyCode.A )
		{
			// Select all = no-op visually, but next paste/type will replace
		}
		else if ( e.Key == KeyCode.Backspace )
		{
			RemoveChar();
		}
		else if ( e.Key == KeyCode.Tab )
		{
			_focusedField = ( _focusedField + 1 ) % 5;
		}
		else if ( e.Key == KeyCode.Escape )
		{
			_focusedField = -1;
		}
		else if ( e.Key == KeyCode.Enter )
		{
			SaveConfig();
		}
		else if ( !e.HasCtrl )
		{
			var c = KeyToChar( e.Key, e.HasShift );
			if ( c.HasValue )
				AppendChar( c.Value );
			else
				handled = false;
		}
		else
		{
			handled = false;
		}

		if ( handled )
		{
			Update();
		}
		else
		{
			base.OnKeyPress( e );
		}
	}

	/// <summary>
	/// Map a KeyCode to a character for text input.
	/// Handles alphanumerics, underscore, dash, dot, colon, slash — enough for API keys and URLs.
	/// </summary>
	private static char? KeyToChar( KeyCode key, bool shift )
	{
		// Letters
		if ( key >= KeyCode.A && key <= KeyCode.Z )
		{
			var c = (char)( 'a' + ( key - KeyCode.A ) );
			return shift ? char.ToUpper( c ) : c;
		}

		// Digits (top row)
		if ( key >= KeyCode.Num0 && key <= KeyCode.Num9 )
			return (char)( '0' + ( key - KeyCode.Num0 ) );

		// Common symbols for URLs and API keys
		return key switch
		{
			KeyCode.Minus => shift ? '_' : '-',
			KeyCode.Underscore => '_',
			KeyCode.Period => '.',
			KeyCode.Slash => '/',
			KeyCode.Semicolon => shift ? ':' : ';',
			KeyCode.Space => ' ',
			KeyCode.Equal => shift ? '+' : '=',
			_ => null
		};
	}

	private void AppendChar( char c )
	{
		switch ( _focusedField )
		{
			case 0: _projectId += c; break;
			case 1: _publicKey += c; break;
			case 2: _secretKey += c; break;
			case 3: _baseUrl += c; break;
			case 4: _dataFolder += c; break;
		}
	}

	private void RemoveChar()
	{
		switch ( _focusedField )
		{
			case 0: if ( _projectId.Length > 0 ) _projectId = _projectId[..^1]; break;
			case 1: if ( _publicKey.Length > 0 ) _publicKey = _publicKey[..^1]; break;
			case 2: if ( _secretKey.Length > 0 ) _secretKey = _secretKey[..^1]; break;
			case 3: if ( _baseUrl.Length > 0 ) _baseUrl = _baseUrl[..^1]; break;
			case 4: if ( _dataFolder.Length > 0 ) _dataFolder = _dataFolder[..^1]; break;
		}
	}

	private string GetFieldValue( int field ) => field switch
	{
		0 => _projectId,
		1 => _publicKey,
		2 => _secretKey,
		3 => _baseUrl,
		4 => _dataFolder,
		_ => ""
	};

	private void SetFieldValue( int field, string value )
	{
		switch ( field )
		{
			case 0: _projectId = value; break;
			case 1: _publicKey = value; break;
			case 2: _secretKey = value; break;
			case 3: _baseUrl = value; break;
			case 4: _dataFolder = value; break;
		}
	}

	// ──────────────────────────────────────────────────────
	//  Actions
	// ──────────────────────────────────────────────────────

	private void SaveConfig()
	{
		// Validate
		if ( string.IsNullOrEmpty( _projectId ) )
		{
			_status = "Project ID is required";
			_statusColor = "red";
			Update();
			return;
		}

		if ( !string.IsNullOrEmpty( _secretKey ) && !_secretKey.StartsWith( "sbox_sk_" ) )
		{
			_status = "Secret key must start with sbox_sk_";
			_statusColor = "red";
			Update();
			return;
		}

		if ( !string.IsNullOrEmpty( _publicKey ) && !_publicKey.StartsWith( "sbox_ns_" ) )
		{
			_status = "Public key must start with sbox_ns_";
			_statusColor = "red";
			Update();
			return;
		}

		var dataSource = _dataSourceIndex switch
		{
			1 => SyncToolConfig.DataSourceMode.ApiOnly,
			2 => SyncToolConfig.DataSourceMode.JsonOnly,
			_ => SyncToolConfig.DataSourceMode.ApiThenJson
		};

		SyncToolConfig.Save( _secretKey, _publicKey, _projectId, _baseUrl, dataSource, _dataFolder );

		_status = "Configuration saved successfully";
		_statusColor = "green";
		_focusedField = -1;
		Update();
	}

	private void DrawTestResult( ref float y, float pad, float w, string label, string result, string extra = null )
	{
		if ( result == null ) return;

		var isOk = result.StartsWith( "Valid" ) || result.StartsWith( "Found" );
		var color = isOk ? Color.Green : Color.Red;
		var icon = isOk ? "✓" : "✗";

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( color.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( pad, y, 16, 15 ), icon, TextFlag.Center );

		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + 18, y, 80, 15 ), label, TextFlag.LeftCenter );

		Paint.SetPen( color.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + 100, y, w - 100, 15 ), result, TextFlag.LeftCenter );

		if ( !string.IsNullOrEmpty( extra ) )
		{
			Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
			Paint.DrawText( new Rect( pad + 100, y, w - 100, 15 ), $"{result} — {extra}", TextFlag.LeftCenter );
		}

		y += 17;
	}

	private async Task TestConnection()
	{
		// Save first so the config is up to date
		SaveConfig();

		if ( string.IsNullOrEmpty( _projectId ) || string.IsNullOrEmpty( _secretKey ) )
		{
			_status = "Need at least Project ID + Secret Key to test";
			_statusColor = "red";
			_testProjectId = null;
			Update();
			return;
		}

		_status = "Testing credentials...";
		_statusColor = "white";
		_testProjectId = null;
		_testSecretKey = null;
		_testPublicKey = null;
		_testProjectTitle = null;
		Update();

		var resp = await SyncToolApi.Validate( _publicKey );

		if ( !resp.HasValue )
		{
			_status = "Connection failed — could not reach server";
			_statusColor = "red";
			_testProjectId = "Connection failed";
			_testSecretKey = "Connection failed";
			_testPublicKey = "Connection failed";
			Update();
			return;
		}

		var result = resp.Value;

		// Parse checks
		if ( result.TryGetProperty( "checks", out var checks ) )
		{
			_testProjectId = GetCheckMessage( checks, "projectId" );
			_testSecretKey = GetCheckMessage( checks, "secretKey" );
			_testPublicKey = GetCheckMessage( checks, "publicKey" );
		}

		// Get project title
		if ( result.TryGetProperty( "project", out var proj ) && proj.ValueKind == JsonValueKind.Object )
		{
			_testProjectTitle = proj.TryGetProperty( "title", out var t ) ? t.GetString() : null;
		}

		var allOk = result.TryGetProperty( "ok", out var okEl ) && okEl.ValueKind == JsonValueKind.True;
		_status = allOk ? "All credentials validated" : "Some credentials failed — see details above";
		_statusColor = allOk ? "green" : "red";
		Update();
	}

	private static string GetCheckMessage( JsonElement checks, string key )
	{
		if ( !checks.TryGetProperty( key, out var check ) ) return "Not checked";
		var msg = check.TryGetProperty( "message", out var m ) ? m.GetString() : "Unknown";
		return msg ?? "Unknown";
	}
}
