using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>
/// What a UI framework's designer supplies to the shared canvas: the one thing the canvas cannot
/// work out from the element tree it is given - which element a point lands on, as the framework's
/// own renderer sees it (template parts, clipping, hit-test visibility). Everything else the canvas
/// needs arrives through <see cref="DesignSurfaceController.ApplySnapshot"/>, and every edit the
/// user makes leaves it as an event the designer turns into a source or model change.
/// <para>
/// An out-of-process backend answers from its design host over DDP (WinUI, Uno, WPF, WinForms,
/// MAUI); an in-process one from its own live tree (ProGPU).
/// </para>
/// </summary>
public interface IDesignCanvasBackend
{
	/// <summary>
	/// The element under a design-unit point, or null when the backend cannot answer (no host yet,
	/// timed out). Called on the UI thread from a pointer press, so it must answer quickly.
	/// </summary>
	DesignCanvasHit? HitTest(double x, double y);
}

/// <param name="Hit">Whether anything was hit.</param>
/// <param name="PickPath">Tree path (<c>DesignerElementNode.Path</c>) of the innermost element the
/// DOCUMENT declares under the point - control-template parts skipped - or null.</param>
/// <param name="Chain">Names from the hit element up to the root, for a backend that reports no
/// path.</param>
public sealed record DesignCanvasHit(bool Hit, string? PickPath, IReadOnlyList<string> Chain);

/// <summary>
/// A committed design-surface drag: the named element and its start/end rects in design
/// coordinates. The designer turns the rects into source edits (Margin/Width/Height, Location...).
/// </summary>
public sealed class ElementDragInfo
{
	public string Name { get; set; } = "";
	public double StartX { get; set; }
	public double StartY { get; set; }
	public double StartWidth { get; set; }
	public double StartHeight { get; set; }
	public double EndX { get; set; }
	public double EndY { get; set; }
	public double EndWidth { get; set; }
	public double EndHeight { get; set; }
}

/// <summary>
/// A double-click on a design element: its name and design rect. A null value means the
/// double-click hit empty space.
/// </summary>
public sealed class ElementDoubleClickInfo
{
	public string Name { get; set; } = "";
	public double X { get; set; }
	public double Y { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
}
