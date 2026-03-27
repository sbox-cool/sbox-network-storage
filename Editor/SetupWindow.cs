using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Editor;

/// <summary>
/// Setup window for entering Network Storage credentials.
/// Uses Paint for layout/chrome but real LineEdit widgets for text inputs,
/// giving native text selection, copy/paste, drag, and unlimited key length.
///
/// Access via Editor menu → Network Storage → Setup, or the Setup button in the Sync Tool.
/// </summary>
[Dock( "Editor", "Network Storage Setup", "key" )]
public class SetupWindow : DockWindow
{
	// ── Real input widgets (native text selection, copy/paste, any length) ──
	private LineEdit _projectIdInput;
	private LineEdit _publicKeyInput;
	private LineEdit _secretKeyInput;
	private LineEdit _baseUrlInput;
	private LineEdit _cdnUrlInput;
	private LineEdit _dataFolderInput;

	// ── UI state ──
	private string _status = "";
	private string _statusColor = "white";
	private int _dataSourceIndex;
	private List<ButtonRect> _buttons = new();
	private Vector2 _mousePos;

	// ── Test results ──
	private string _testProjectId;
	private string _testSecretKey;
	private string _testPublicKey;
	private string _testProjectTitle;

	private struct ButtonRect
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public SetupWindow()
	{
		Title = "Network Storage Setup";
		Size = new Vector2( 520, 720 );
		MinimumSize = new Vector2( 400, 580 );

		SyncToolConfig.Load();
		_dataSourceIndex = SyncToolConfig.DataSource switch
		{
			SyncToolConfig.DataSourceMode.ApiOnly => 1,
			SyncToolConfig.DataSourceMode.JsonOnly => 2,
			_ => 0
		};

		CreateInputWidgets();
	}

	[Menu( "Editor", "Network Storage/Setup" )]
	public static void OpenWindow()
	{
		var window = new SetupWindow();
		window.Show();
	}

	// ──────────────────────────────────────────────────────
	//  Create real LineEdit widgets (children of the window)
	// ──────────────────────────────────────────────────────

	private void CreateInputWidgets()
	{
		_projectIdInput = CreateLineEdit( SyncToolConfig.ProjectId, "From sbox.cool dashboard" );
		_publicKeyInput = CreateLineEdit( SyncToolConfig.PublicApiKey, "Public key — used by game client" );
		_secretKeyInput = CreateLineEdit( SyncToolConfig.SecretKey, "Secret key — editor only, NEVER ships" );
		_baseUrlInput = CreateLineEdit( SyncToolConfig.BaseUrl, "Default: https://api.sboxcool.com" );
		_cdnUrlInput = CreateLineEdit( SyncToolConfig.CdnUrl, "Optional: storage.sboxcool.com" );
		_dataFolderInput = CreateLineEdit( SyncToolConfig.DataFolder, "Subfolder under Editor/ (default: Network Storage)" );
	}

	private LineEdit CreateLineEdit( string value, string placeholder )
	{
		var input = new LineEdit( this );
		input.Text = value ?? "";
		input.PlaceholderText = placeholder;
		input.Visible = true;
		return input;
	}

	/// <summary>
	/// Position a LineEdit at the given rect. Called during OnPaint so they track layout.
	/// </summary>
	private void PositionInput( LineEdit input, float x, float y, float w, float h )
	{
		input.Position = new Vector2( x, y );
		input.Size = new Vector2( w, h );
	}

	// ──────────────────────────────────────────────────────
	//  Rendering (Paint for chrome, real widgets for inputs)
	// ──────────────────────────────────────────────────────

	protected override void OnPaint()
	{
		base.OnPaint();
		_buttons.Clear();

		var y = 38f;
		var pad = 20f;
		var w = Width - pad * 2;
		var fieldH = 28f;

		// ── Title ──
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "Network Storage Setup", TextFlag.LeftCenter );
		y += 32;

		// ── Description ──
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		Paint.DrawText( new Rect( pad, y, w, 14 ), "Credentials are stored in Editor/ config files (gitignored, never published)", TextFlag.LeftCenter );
		y += 22;

		DrawSeparator( ref y, w, pad );

		// ── Input fields (label + real LineEdit widget) ──
		DrawFieldLabel( ref y, pad, w, "Project ID" );
		PositionInput( _projectIdInput, pad, y, w, fieldH );
		y += fieldH + 6;

		DrawFieldLabel( ref y, pad, w, "Public API Key" );
		PositionInput( _publicKeyInput, pad, y, w, fieldH );
		y += fieldH + 2;

		// Public key prefix hint
		var pubKey = _publicKeyInput.Text?.Trim() ?? "";
		if ( !string.IsNullOrEmpty( pubKey ) && !pubKey.StartsWith( "sbox_ns_" ) )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.Orange.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 12 ), "Standard public keys start with sbox_ns_", TextFlag.LeftCenter );
			y += 14;
		}
		y += 4;

		DrawFieldLabel( ref y, pad, w, "Secret Key" );
		PositionInput( _secretKeyInput, pad, y, w, fieldH );
		y += fieldH + 2;

		// Secret key prefix hint
		var secKey = _secretKeyInput.Text?.Trim() ?? "";
		if ( !string.IsNullOrEmpty( secKey ) && !secKey.StartsWith( "sbox_sk_" ) )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.Orange.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 12 ), "Standard secret keys start with sbox_sk_", TextFlag.LeftCenter );
			y += 14;
		}
		y += 4;

		DrawFieldLabel( ref y, pad, w, "Base URL" );
		PositionInput( _baseUrlInput, pad, y, w, fieldH );
		y += fieldH + 6;

		DrawFieldLabel( ref y, pad, w, "CDN URL" );
		PositionInput( _cdnUrlInput, pad, y, w, fieldH );
		y += fieldH + 6;

		DrawFieldLabel( ref y, pad, w, "Editor Data Folder" );
		PositionInput( _dataFolderInput, pad, y, w, fieldH );
		y += fieldH + 8;

		DrawSeparator( ref y, w, pad );

		// ── Data Source Preference ──
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Data Source (GET requests)", TextFlag.LeftCenter );
		y += 24;

		var thirdW = ( w - 16 ) / 3;
		DrawToggleButton( pad, y, thirdW, 0, "API + Fallback", _dataSourceIndex == 0 );
		DrawToggleButton( pad + thirdW + 8, y, thirdW, 1, "API Only", _dataSourceIndex == 1 );
		DrawToggleButton( pad + ( thirdW + 8 ) * 2, y, thirdW, 2, "JSON Only", _dataSourceIndex == 2 );
		y += 38 + 12;

		DrawSeparator( ref y, w, pad );

		// ── Save button ──
		var saveBtnH = 34f;
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

		// ── Test results ──
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

	private void DrawFieldLabel( ref float y, float pad, float w, string label )
	{
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( pad, y, w, 16 ), label, TextFlag.LeftCenter );
		y += 18;
	}

	private void DrawToggleButton( float x, float y, float w, int index, string label, bool active )
	{
		var h = 36f;
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
		Paint.DrawText( rect, label, TextFlag.Center );

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
	}

	private void DrawSeparator( ref float y, float w, float pad )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 10;
	}

	private void DrawTestResult( ref float y, float pad, float w, string label, string result, string extra = null )
	{
		if ( result == null ) return;

		var isOk = result.StartsWith( "Valid" ) || result.StartsWith( "Found" );
		var color = isOk ? Color.Green : Color.Red;

		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( color.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( pad, y, 16, 15 ), isOk ? "+" : "x", TextFlag.Center );

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + 18, y, 80, 15 ), label, TextFlag.LeftCenter );

		var display = !string.IsNullOrEmpty( extra ) ? $"{result} — {extra}" : result;
		Paint.SetPen( color.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + 100, y, w - 100, 15 ), display, TextFlag.LeftCenter );

		y += 17;
	}

	// ──────────────────────────────────────────────────────
	//  Input handling
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		foreach ( var btn in _buttons )
		{
			if ( btn.Rect.IsInside( e.LocalPosition ) )
			{
				btn.OnClick?.Invoke();
				return;
			}
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
		Update();
	}

	// ──────────────────────────────────────────────────────
	//  Actions
	// ──────────────────────────────────────────────────────

	private void SaveConfig()
	{
		var projectId = _projectIdInput.Text?.Trim() ?? "";
		var publicKey = _publicKeyInput.Text?.Trim() ?? "";
		var secretKey = _secretKeyInput.Text?.Trim() ?? "";
		var baseUrl = _baseUrlInput.Text?.Trim() ?? "";
		var cdnUrl = _cdnUrlInput.Text?.Trim() ?? "";
		var dataFolder = _dataFolderInput.Text?.Trim() ?? "";

		if ( string.IsNullOrEmpty( projectId ) )
		{
			_status = "Project ID is required";
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

		SyncToolConfig.Save( secretKey, publicKey, projectId, baseUrl, dataSource, dataFolder, cdnUrl );

		// Warn about non-standard prefixes but still save
		var warnings = "";
		if ( !string.IsNullOrEmpty( publicKey ) && !publicKey.StartsWith( "sbox_ns_" ) )
			warnings += " (public key: non-standard prefix)";
		if ( !string.IsNullOrEmpty( secretKey ) && !secretKey.StartsWith( "sbox_sk_" ) )
			warnings += " (secret key: non-standard prefix)";

		if ( !string.IsNullOrEmpty( warnings ) )
		{
			_status = $"Saved with warnings:{warnings}";
			_statusColor = "green";
		}
		else
		{
			_status = "Configuration saved successfully";
			_statusColor = "green";
		}

		Update();
	}

	private async Task TestConnection()
	{
		SaveConfig();

		var projectId = _projectIdInput.Text?.Trim() ?? "";
		var secretKey = _secretKeyInput.Text?.Trim() ?? "";
		var publicKey = _publicKeyInput.Text?.Trim() ?? "";

		if ( string.IsNullOrEmpty( projectId ) || string.IsNullOrEmpty( secretKey ) )
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

		var resp = await SyncToolApi.Validate( publicKey );

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

		if ( result.TryGetProperty( "checks", out var checks ) )
		{
			_testProjectId = GetCheckMessage( checks, "projectId" );
			_testSecretKey = GetCheckMessage( checks, "secretKey" );
			_testPublicKey = GetCheckMessage( checks, "publicKey" );
		}

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
