using Editor;

/// <summary>
/// Standalone editor window whose content is rendered and interacted with through
/// the window's central widget. Current s&amp;box windows reserve their root widget
/// for window chrome, so custom content must live on <see cref="Window.Canvas"/>.
/// </summary>
public abstract class PaintedWindow : Window
{
	private readonly ContentWidget _content;

	protected PaintedWindow()
	{
		_content = new ContentWidget( this );
		Canvas = _content;
	}

	/// <summary>
	/// Parent for native controls that must appear above the painted content.
	/// </summary>
	protected Widget Content => _content;

	public new float Width
	{
		get => _content.Width;
		set => base.Width = value;
	}

	public new float Height
	{
		get => _content.Height;
		set => base.Height = value;
	}

	public new bool MouseTracking
	{
		get => _content.MouseTracking;
		set
		{
			base.MouseTracking = value;
			_content.MouseTracking = value;
		}
	}

	public override void Update()
	{
		base.Update();
		_content.Update();
	}

	protected virtual void OnContentPaint()
	{
	}

	protected virtual void OnContentMousePress( MouseEvent e )
	{
	}

	protected virtual void OnContentMouseMove( MouseEvent e )
	{
	}

	protected virtual void OnContentMouseWheel( WheelEvent e )
	{
	}

	protected virtual void OnContentKeyPress( KeyEvent e )
	{
	}

	private sealed class ContentWidget : Widget
	{
		private readonly PaintedWindow _owner;

		public ContentWidget( PaintedWindow owner )
		{
			_owner = owner;
			MouseTracking = true;
			FocusMode = FocusMode.TabOrClickOrWheel;
		}

		protected override void OnPaint()
		{
			base.OnPaint();
			_owner.OnContentPaint();
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );
			_owner.OnContentMousePress( e );
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );
			_owner.OnContentMouseMove( e );
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			base.OnMouseWheel( e );
			_owner.OnContentMouseWheel( e );
		}

		protected override void OnKeyPress( KeyEvent e )
		{
			base.OnKeyPress( e );
			_owner.OnContentKeyPress( e );
		}
	}
}
