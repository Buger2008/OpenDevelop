using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Designer.Surface;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler, IFilterableToolbox, IDesignCanvasBackend
{
	public static readonly string[] ToolNames = { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkLabel", "GtkButton", "GtkEntry", "GtkPasswordEntry", "GtkCheckButton", "GtkSwitch", "GtkSpinButton", "GtkDropDown", "GtkListBox", "GtkListView", "GtkGridView", "GtkImage", "GtkPicture", "GtkProgressBar", "GtkSeparator" };
	readonly DocumentOutlineControl outline = new(); readonly ListBox toolbox = new() { DisplayMemberPath = nameof(DesignerToolboxItemInfo.DisplayName) }; readonly PropertyContainer properties = new();
	readonly DesignerToolboxController toolboxModel = new();
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
	// The shared design canvas (ICSharpCode.DesignerCanvas addin), keyed by GtkBuilder object id.
	// GTK lays every widget out itself, so the canvas shows no resize handles and a drag is a
	// reorder among siblings (see CommitCanvasDrag).
	readonly DesignSurface canvas = new();
	readonly DesignSurfaceController canvasController;
	Dictionary<string, string> pathById = new(StringComparer.Ordinal);
	bool draggingFromToolbox; bool syncingToolbox; string? pressedToolboxType;
	GtkDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string loadedText = "";
	CancellationTokenSource? renderCancellation; long requestedRenderRevision; long renderedRevision;

	public GtkDesignerViewContent(OpenedFile file) : base(file)
	{
		toolboxModel.SetItems(ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = "GTK 4" }));
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		toolbox.Tag = this;
		toolboxModel.ItemsChanged += (_, _) => { syncingToolbox = true; toolbox.ItemsSource = toolboxModel.VisibleItems; toolbox.SelectedItem = toolboxModel.SelectedItem; syncingToolbox = false; };
		toolboxModel.SelectionChanged += (_, _) => { syncingToolbox = true; toolbox.SelectedItem = toolboxModel.SelectedItem; syncingToolbox = false; };
		toolbox.SelectionChanged += (_, _) => { if (!syncingToolbox) toolboxModel.Select((toolbox.SelectedItem as DesignerToolboxItemInfo)?.TypeName); };
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => host?.IsAlive == true && state.CanUndo, () => { Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => host?.IsAlive == true && state.CanRedo, () => { Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; canvasController?.RestoreSelection(selection.SelectedIds); });
		canvasController = new DesignSurfaceController(canvas, this, DesignSurfaceKeying.Id);
		TabPageText = "Design"; ConfigureCanvas(); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);
		toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) Add(item.TypeName); }; toolbox.KeyDown += (_, e) => { if (e.Key == Key.Enter && toolboxModel.SelectedItem is { } item) { Add(item.TypeName); e.Handled = true; } };
		// Latch what was pressed, rather than reading toolbox.SelectedItem when the drag actually
		// starts: leaving the list drags the pointer across neighbouring rows, and ListBox's own
		// drag-selection retargets SelectedItem to each one it passes over. Measured: pressing
		// GtkSwitch and dragging up to the canvas dropped a GtkCheckButton (the row above) instead.
		toolbox.PreviewMouseDown += (_, e) => { draggingFromToolbox = false; pressedToolboxType = ToolboxTypeAt(e.GetPosition(toolbox)); };
		// Guard against re-entrancy: WPF only supports one active DoDragDrop session at a time,
		// so calling it again on every subsequent PreviewMouseMove while the button stays down
		// (which fires repeatedly for a real or synthetic multi-step drag) would cancel the prior,
		// still-in-flight session before it reaches the drop target.
		toolbox.PreviewMouseMove += (_, e) => {
			if (e.LeftButton != MouseButtonState.Pressed) { draggingFromToolbox = false; return; }
			var type = pressedToolboxType ?? toolboxModel.SelectedItem?.TypeName;
			if (draggingFromToolbox || type == null) return;
			draggingFromToolbox = true;
			DragDrop.DoDragDrop(toolbox, new DataObject(DataFormats.StringFormat, type), DragDropEffects.Copy);
			draggingFromToolbox = false;
		};
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Redo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Delete)));
	}
	public object OutlineContent => outline; public object ToolsContent => toolbox; public ListBox ToolboxControl => toolbox; public int ZoomComboSelectedIndex => canvas.ZoomCombo.SelectedIndex; public PropertyContainer PropertyContainer => properties;
	public string? SelectedToolboxType => toolboxModel.SelectedItem?.TypeName;
	public DesignerToolboxItemInfo? SelectedToolboxItem => toolboxModel.SelectedItem;
	public bool SelectToolboxType(string type) { var ok = toolboxModel.Select(type); if (ok) toolbox.SelectedItem = toolboxModel.SelectedItem; return ok; }
	public void FilterToolbox(string text) => toolboxModel.Filter(text);
	void IFilterableToolbox.Filter(string text) => FilterToolbox(text);
	int IFilterableToolbox.VisibleItemCount => ToolboxItemCount;
	string IFilterableToolbox.FilterText => ToolboxFilterText;
	public string ToolboxFilterText => toolboxModel.FilterText;
	/// <summary>A rendered object's bounds in screen coordinates, through the canvas's viewport
	/// (null when it has none, or nothing is rendered).</summary>
	public Rect? ScreenBoundsOf(string id)
	{
		var node = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id);
		if (node == null || node.Width <= 0 || node.Height <= 0 || !canvas.HasRender) return null;
		var topLeft = canvas.SurfacePointToScreen(node.X, node.Y);
		var bottomRight = canvas.SurfacePointToScreen(node.X + node.Width, node.Y + node.Height);
		return new Rect(topLeft, bottomRight);
	}
	string? ToolboxTypeAt(Point point)
	{
		for (var hit = toolbox.InputHitTest(point) as DependencyObject; hit != null; hit = VisualTreeHelper.GetParent(hit))
			if (hit is ListBoxItem row) return (row.DataContext as DesignerToolboxItemInfo)?.TypeName;
		return null;
	}
	public int ToolboxItemCount => toolbox.Items.Count; public bool IsToolboxHosted => ReferenceEquals((SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HostedContent, toolbox);
	public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Id != "$interface"); public string SelectedId => selected?.Id ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
	public string[] ElementIds => state.Tree == null ? Array.Empty<string>() : Flatten(state.Tree).Where(n => n.Id != "$interface").Select(n => n.Id).ToArray();
	public string RootId => state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault()?.Id ?? "" : state.Tree?.Id ?? "";
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => canvas.ViewportScale; set => canvas.SetViewport(Math.Clamp(value, .25, 2), 0, 0); }
	public bool Gridlines => canvas.Gridlines; public bool FitMeasured { get; private set; } public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; canvas.SetGridlines(show); }
	public bool HasNativeFrame => !string.IsNullOrEmpty(state.Render?.PngBase64); public int NativeFrameWidth => state.Render?.Width ?? 0; public int NativeFrameHeight => state.Render?.Height ?? 0; public int NativeBoundsCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Width > 0 && n.Height > 0);
	public string NativeFrameFingerprint => HasNativeFrame ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(state.Render!.PngBase64))) : "";
	public string[] Diagnostics => state.Diagnostics.Select(d => d.Message).ToArray();
	public string LoadError => HasLoadError && Control is ContentPresenter presenter && presenter.Content is TextBox error ? error.Text : "";
	public string HostLog => host?.ChildLog ?? "";
	public string HostSessionId => host?.SessionId ?? ""; public string HostDocumentId => host?.DocumentId ?? ""; public string HostPoolKey => host?.PoolKey ?? "gtk4"; public int ActiveHostLeases => GtkDesignerHostClient.ActiveLeaseCount; public int HostRecoveryCount => host?.RecoveryCount ?? 0;
	public long RequestedRenderRevision => requestedRenderRevision; public long RenderedRevision => renderedRevision; public bool IsRenderPending => requestedRenderRevision > renderedRevision;
	public string Status => state.Accepted ? $"Ready: {ElementCount} GTK objects (host {host?.ProcessId}, native frame {(HasNativeFrame ? $"{NativeFrameWidth}x{NativeFrameHeight}" : "unavailable")})" : state.Error;
	public bool EnableUndo => commands.CanExecute("Undo"); public bool EnableRedo => commands.CanExecute("Redo");
	public void Undo() => commands.Execute("Undo"); public void Redo() => commands.Execute("Redo");
	public bool SelectById(string id) => selection.Select(id);
	public IReadOnlyList<string> SelectedIds => selection.SelectedIds;
	public bool SelectByIds(IEnumerable<string> ids) => pads.CommitSelection(ids);
	public DesignerElementNode? FindById(string id) => selection.Find(id);
	public bool HitTest(double x, double y) { if (host == null) return false; var result = host.HitTestAsync(state.Version, x, y).GetAwaiter().GetResult(); return result.Hit && SelectById(result.ComponentName); }
	public bool SetSelectedProperty(string name, string value) => selected != null && SetProperty(selected.Id, name, value);
	bool SetProperty(string id, string name, string value) { if (host == null) return false; Mutate(() => host.SetPropertyAsync(state.Version, id, name, value).GetAwaiter().GetResult()); return name == "$id" ? SelectById(value) : selection.Find(id) != null; }
	public bool Add(string type) { if (host == null || state.Tree == null) return false; var parent = selected == null ? Flatten(state.Tree).FirstOrDefault(IsContainer) : NearestContainer(selected); if (parent == null) return false; var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet(StringComparer.Ordinal); Mutate(() => host.AddElementAsync(state.Version, parent.Id, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult()); var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id)); if (added != null) Select(added); return added != null; }
	public bool DeleteSelected() => commands.Execute("Delete");
	bool DeleteSelectedCore() { if (selection.SelectedIds.Count == 0 || host == null) return false; var ids = selection.SelectedIds.ToArray(); Mutate(() => host.DeleteElementsAsync(state.Version, ids).GetAwaiter().GetResult()); return true; }
	public bool SetSelectedSignal(string signal, string handler) { if (selected == null || host == null) return false; var id = selected.Id; Mutate(() => host.SetEventAsync(state.Version, id, signal, handler).GetAwaiter().GetResult()); return SelectById(id); }
	GtkPropertyAdapter Adapter(DesignerElementNode node) => new(node, (name, value) => SetProperty(node.Id, name, value), (name, value) => SetEvent(node.Id, name, value));
	void SetEvent(string id, string signal, string handler) { if (host == null) return; Mutate(() => host.SetEventAsync(state.Version, id, signal, handler).GetAwaiter().GetResult()); }
	public bool ReorderSelected(int delta) { if (selected == null || host == null) return false; var id = selected.Id; Mutate(() => host.ReorderAsync(state.Version, id, delta).GetAwaiter().GetResult()); return SelectById(id); }
	public bool PointerReorder(string sourceId, string targetId) { if (state.Tree == null) return false; var source = FindById(sourceId); var target = FindById(targetId); return source != null && target != null && ReorderBetween(state.Tree, source, target); }
	public void RefreshDesign() { if (host == null) return; var text = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; state = host.UpdateAsync(Snapshot(text, state.Version + 1)).GetAwaiter().GetResult(); loadedText = text; Rebuild(); }
	public void RestartDesignHost() { if (host == null) return; state = host.RestartPoolAsync().GetAwaiter().GetResult(); loadedText = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; Rebuild(); }
	public void TerminateDesignHost() { if (host == null) return; state = host.TerminateAndRecoverAsync().GetAwaiter().GetResult(); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild(); }
	public void ShowSource() { var window = WorkbenchWindow; if (window == null) return; for (var i = 0; i < window.ViewContents.Count; i++) if (!ReferenceEquals(window.ViewContents[i], this)) { window.SwitchView(i); return; } }
	void Mutate(Func<DesignerSessionState> action) { state = action(); PrimaryFile?.MakeDirty(); Rebuild(); QueueRender(); commands.Invalidate(); }
	void QueueRender()
	{
		if (host == null || !state.Accepted) return;
		var version = state.Version; requestedRenderRevision = version;
		renderCancellation?.Cancel(); renderCancellation?.Dispose(); renderCancellation = new CancellationTokenSource(); var token = renderCancellation.Token;
		_ = RenderLatestAsync(host, version, token);
	}
	async Task RenderLatestAsync(GtkDesignerHostClient renderingHost, long version, CancellationToken token)
	{
		try {
			var rendered = await renderingHost.RenderAsync(version, token).ConfigureAwait(false);
			await Application.Current.Dispatcher.InvokeAsync(() => { if (token.IsCancellationRequested || host != renderingHost || state.Version != version || rendered.Render?.Sequence != version) return; state = rendered; renderedRevision = version; Rebuild(); });
		} catch (OperationCanceledException) { } catch (Exception ex) { await Application.Current.Dispatcher.InvokeAsync(() => diagnostic.Text = "GTK render failed: " + ex.Message); }
	}
	void Rebuild()
	{
		diagnostic.Text = HasNativeFrame ? Status : Status + " - no native frame: the GTK 4 runtime could not render this interface.";
		pads.UpdateRoots(state.Tree == null ? null : state.Tree.Id == "$interface" ? state.Tree.Children : new[] { state.Tree });
		canvasController.ApplySnapshot(CanvasSnapshot());
		canvasController.RestoreSelection(selection.SelectedIds);
	}

	/// <summary>The session as the canvas shows it: the native frame, and the rendered root object
	/// (not the "$interface" wrapper) with a tree path on every node, which the canvas's hit test
	/// answers with.</summary>
	DesignerSessionState CanvasSnapshot()
	{
		var root = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		var paths = new Dictionary<string, string>(StringComparer.Ordinal);
		DesignerElementNode Copy(DesignerElementNode node, string path)
		{
			paths[node.Id] = path;
			var copy = new DesignerElementNode { Id = node.Id, Name = node.Name, Type = node.Type, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height, Path = path, IsDesignable = node.Id != "$interface", IsVisible = node.IsVisible };
			for (var index = 0; index < node.Children.Count; index++)
				copy.Children.Add(Copy(node.Children[index], path.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "," + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			return copy;
		}
		var tree = root == null ? null : Copy(root, "");
		pathById = paths;
		return new DesignerSessionState { Accepted = true, Version = state.Version, Render = HasNativeFrame ? state.Render : null, Tree = tree };
	}

	/// <summary>The canvas's hit test. It runs on the UI thread from a pointer press, so it is
	/// answered locally from the native GTK bounds the host already sent with the frame (the same
	/// bounds its design/hit-test RPC uses), never with a blocking round-trip.</summary>
	DesignCanvasHit? IDesignCanvasBackend.HitTest(double x, double y)
	{
		var root = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		var hit = root == null ? null : NativeNodeAt(root, new Point(x, y));
		return hit != null && pathById.TryGetValue(hit.Id, out var path) ? new DesignCanvasHit(true, path, new[] { hit.Id }) : new DesignCanvasHit(false, null, Array.Empty<string>());
	}

	/// <summary>A committed canvas drag: GTK positions nothing freely, so dropping an object onto a
	/// sibling moves it to that sibling's place; anything else snaps back.</summary>
	void CommitCanvasDrag(ElementDragInfo drag)
	{
		var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		var source = previewRoot == null ? null : Flatten(previewRoot).FirstOrDefault(n => n.Id == drag.Name);
		var over = previewRoot == null ? null : NativeNodeAt(previewRoot, new Point(drag.EndX + drag.EndWidth / 2, drag.EndY + drag.EndHeight / 2), source);
		if (previewRoot == null || source == null || over == null || !ReorderBetween(previewRoot, source, over))
			canvasController.RestoreSelection(selection.SelectedIds);
	}

	void Select(DesignerElementNode? node, bool toggle = false) => selection.Select(node == null ? Array.Empty<DesignerElementNode>() : new[] { node }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
	void ConfigureCanvas()
	{
		// Only what the GTK designer does: GTK owns layout, so no resize handles, design sizes,
		// themes or visual states.
		canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines;
		canvas.ResizeHandlesEnabled = false;
		canvas.SetContextCommands(new[] { ("Delete", "delete") });
		canvasController.ClearsSelectionOnEmptyClick = true;
		canvasController.SelectionChanged += (_, ids) => { if (!ids.SequenceEqual(selection.SelectedIds)) pads.CommitSelection(ids); };
		canvasController.ElementPicked += (_, id) => { if (!selection.SelectedIds.Contains(id)) SelectById(id); };
		canvasController.ElementDragCommitted += (_, drag) => CommitCanvasDrag(drag);
		canvasController.ElementGroupDragCommitted += (_, _) => canvasController.RestoreSelection(selection.SelectedIds);
		canvasController.ContextCommandRequested += (_, command) => { if (command.Command == "delete") DeleteSelected(); };
		canvasController.UndoRedoRequested += (_, undo) => { if (undo) Undo(); else Redo(); };
		canvas.AllowDrop = true;
		canvas.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		canvas.Drop += (_, e) => {
			if (e.Data.GetData(DataFormats.StringFormat) is not string type || !ToolNames.Contains(type, StringComparer.Ordinal)) return;
			var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
			var design = canvas.ToDesignPoint(e.GetPosition(canvas));
			var over = previewRoot == null ? null : NativeNodeAt(previewRoot, new Point(design.X, design.Y));
			if (over != null) Select(over);
			Add(type);
			e.Handled = true;
		};
	}
	void FitView() { canvas.FitView(); FitMeasured = canvas.HasRender && canvas.IsFitMode; }
	static bool IsContainer(DesignerElementNode n) => n.Type is "GtkBox" or "GtkGrid" or "GtkCenterBox" or "GtkPaned" or "GtkScrolledWindow" or "GtkApplicationWindow" or "GtkWindow";
	DesignerElementNode? NearestContainer(DesignerElementNode node) { if (state.Tree == null) return null; for (var current = node; ; ) { if (IsContainer(current)) return current; var parent = Flatten(state.Tree).FirstOrDefault(p => p.Children.Contains(current)); if (parent == null) return null; current = parent; } }
	static DesignerElementNode? NativeNodeAt(DesignerElementNode root, Point point, DesignerElementNode? except = null) => Flatten(root).Where(n => !ReferenceEquals(n, except) && n.Width > 0 && n.Height > 0 && point.X >= n.X && point.Y >= n.Y && point.X <= n.X + n.Width && point.Y <= n.Y + n.Height).OrderBy(n => n.Width * n.Height).FirstOrDefault();
	bool ReorderBetween(DesignerElementNode root, DesignerElementNode source, DesignerElementNode target) { var parent = Flatten(root).FirstOrDefault(p => p.Children.Contains(source) && p.Children.Contains(target)); if (parent == null) return false; var delta = parent.Children.IndexOf(target) - parent.Children.IndexOf(source); if (delta == 0) return false; Select(source); return ReorderSelected(delta); }
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", Files = { new DesignerSourceFileSnapshot { FileName = PrimaryFile?.FileName.ToString() ?? "", Kind = "Designer", Text = text } } };
	protected override void LoadInternal(OpenedFile file, Stream stream) { using var reader = new StreamReader(stream, leaveOpen: true); loadedText = reader.ReadToEnd(); if (host == null) { host = GtkDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); host.Recovered += HostRecovered; } state = host.OpenAsync(Snapshot(loadedText, 1)).GetAwaiter().GetResult(); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild();
		OutputChannel.Write("GtkDesigner", "Host started for " + System.IO.Path.GetFileName(file.FileName.ToString())); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { var text = host == null ? loadedText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedText = text; }
	void HostRecovered(object? sender, DesignerSessionState recovered) { Application.Current.Dispatcher.BeginInvoke(new Action(() => { OutputChannel.Write("GtkDesigner", "Host recovered"); state = recovered; requestedRenderRevision = renderedRevision = recovered.Render?.Sequence ?? 0; Rebuild(); })); }
	public override void Dispose() { OutputChannel.Write("GtkDesigner", "Designer view disposed"); renderCancellation?.Cancel(); renderCancellation?.Dispose(); pads.Dispose(); properties.Clear(); if (host != null) host.Recovered -= HostRecovered; host?.Dispose(); base.Dispose(); }
}
