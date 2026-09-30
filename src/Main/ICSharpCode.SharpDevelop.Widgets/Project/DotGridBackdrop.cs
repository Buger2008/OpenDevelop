using System.Windows;
using System.Windows.Media;

namespace ICSharpCode.SharpDevelop.Widgets
{
	/// <summary>
	/// The designer canvas's empty-area backdrop: a solid fill with a dot every
	/// <see cref="Spacing"/> pixels, drawn directly rather than as a brush. LibreWPF (measured with
	/// LibreWPF.Sdk 0.1.0-preview.65 on macOS) paints no TileBrush used as a Panel/Border background -
	/// neither a DrawingBrush nor an ImageBrush, tiled or not (<c>TileMode.None</c> included) - so the
	/// <c>EdgePattern</c> DrawingBrush this replaces rendered nothing and the canvas showed whatever sat
	/// behind it. A SolidColorBrush, a DrawingImage in an Image, and plain ellipses all render.
	/// </summary>
	sealed class DotGridBackdrop : FrameworkElement
	{
		public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
			nameof(Background), typeof(Brush), typeof(DotGridBackdrop),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

		public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
			nameof(DotBrush), typeof(Brush), typeof(DotGridBackdrop),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

		const double Spacing = 16;
		// Between the light theme's former 1.4px and the dark theme's 1.75px: a hairline dot
		// vanished under anti-aliasing, a larger one reads as busy.
		const double Radius = 1.6;

		public DotGridBackdrop() => IsHitTestVisible = false;

		public Brush Background {
			get => (Brush)GetValue(BackgroundProperty);
			set => SetValue(BackgroundProperty, value);
		}

		public Brush DotBrush {
			get => (Brush)GetValue(DotBrushProperty);
			set => SetValue(DotBrushProperty, value);
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			var size = RenderSize;
			if (size.Width <= 0 || size.Height <= 0)
				return;
			if (Background != null)
				drawingContext.DrawRectangle(Background, null, new Rect(size));
			if (DotBrush == null)
				return;
			for (var y = Spacing / 2; y < size.Height; y += Spacing) {
				for (var x = Spacing / 2; x < size.Width; x += Spacing)
					drawingContext.DrawEllipse(DotBrush, null, new Point(x, y), Radius, Radius);
			}
		}
	}
}
