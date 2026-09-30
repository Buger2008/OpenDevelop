using System;
using System.Linq;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using WinUIElement = Microsoft.UI.Xaml.FrameworkElement;

namespace ICSharpCode.WinUIXamlDesigner.ProGPUHost;

/// <summary>
/// Renders a ProGPU WinUI tree to BGRA pixels on an offscreen WebGPU texture. It presents nothing
/// itself: the pixels become a <c>DesignerRenderFrame</c> the shared design canvas shows, exactly
/// like a frame from an out-of-process design host - which is what gives ProGPU the canvas's
/// toolbar, zoom, selection and gestures.
/// </summary>
unsafe sealed class ProGpuOffscreenRenderer : IDisposable
{
	WgpuContext context;
	Compositor compositor;
	GpuTexture texture;
	Silk.NET.WebGPU.Buffer* stagingBuffer;
	uint stagingBufferSize;
	uint bytesPerRow;
	uint pixelWidth;
	uint pixelHeight;
	bool disposed;

	/// <summary>
	/// Renders <paramref name="root"/> at <paramref name="width"/> x <paramref name="height"/> design
	/// units and <paramref name="dpi"/> pixels per unit. Returns tightly packed premultiplied BGRA
	/// rows (stride = pixel width x 4), the layout <c>DesignerFrameCodec.DecodeBgra32</c> expects.
	/// </summary>
	public (byte[] Pixels, int Width, int Height, double Milliseconds) Render(WinUIElement root, double width, double height, double dpi)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		EnsureContext();
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		EnsureSurface((uint)Math.Max(1, Math.Ceiling(width * dpi)), (uint)Math.Max(1, Math.Ceiling(height * dpi)));
		// A design preview is a still: finish entrance transitions instead of showing their first
		// frame (the old in-window host advanced them one 60 Hz tick per repaint).
		root.UpdateAnimations(5f);
		root.Measure(new Vector2((float)width, (float)height));
		root.Arrange(new ProGPU.Scene.Rect(0, 0, (float)width, (float)height));
		compositor.RenderOffscreen(root, pixelWidth, pixelHeight, texture, 0, (float)dpi);
		var pixels = CopyTextureToBytes();
		return (pixels, (int)pixelWidth, (int)pixelHeight, stopwatch.Elapsed.TotalMilliseconds);
	}

	void EnsureContext()
	{
		if (context != null)
			return;
		// WgpuContext.Initialize sets the thread-static WgpuContext.Current to *this* context.
		// Leaving it there would make LibreWPF's BitmapSource image adapter (which prefers Current
		// when creating GPU textures) upload the IDE's own bitmaps onto THIS context, while the WPF
		// window is composited on LibreWPF's context - a cross-device texture that renders nothing.
		// Save the caller's Current and restore it after creating the offscreen context.
		var previousCurrent = WgpuContext.Current;
		context = new WgpuContext();
		context.Initialize(null);
		WgpuContext.Current = previousCurrent;
		compositor = new Compositor(context, TextureFormat.Bgra8Unorm);
		WgpuContext.OnWebGpuError += OnWebGpuError;
		WgpuContext.OnWebGpuDeviceLost += OnWebGpuDeviceLost;
	}

	void OnWebGpuError(ErrorType type, string message) =>
		System.Diagnostics.Debug.WriteLine($"[OpenDevelop-ProGPU] WebGPU error {type}: {message}");

	void OnWebGpuDeviceLost(DeviceLostReason reason, string message) =>
		System.Diagnostics.Debug.WriteLine($"[OpenDevelop-ProGPU] WebGPU device lost {reason}: {message}");

	void EnsureSurface(uint width, uint height)
	{
		if (texture != null && pixelWidth == width && pixelHeight == height)
			return;
		ReleaseSurface();
		texture = new GpuTexture(context, width, height, TextureFormat.Bgra8Unorm,
			TextureUsage.RenderAttachment | TextureUsage.CopySrc, "OpenDevelop WinUI Designer",
			alphaMode: GpuTextureAlphaMode.Premultiplied);
		bytesPerRow = (width * 4 + 255) & ~255u;
		stagingBufferSize = bytesPerRow * height;
		var descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = stagingBufferSize };
		stagingBuffer = context.Api.DeviceCreateBuffer(context.Device, &descriptor);
		pixelWidth = width;
		pixelHeight = height;
	}

	byte[] CopyTextureToBytes()
	{
		var encoderDescriptor = new CommandEncoderDescriptor();
		var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
		var source = new ImageCopyTexture { Texture = texture.TexturePtr, Aspect = TextureAspect.All };
		var destination = new ImageCopyBuffer { Buffer = stagingBuffer, Layout = new TextureDataLayout { BytesPerRow = bytesPerRow, RowsPerImage = pixelHeight } };
		var extent = new Extent3D { Width = pixelWidth, Height = pixelHeight, DepthOrArrayLayers = 1 };
		context.Api.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
		var commandDescriptor = new CommandBufferDescriptor();
		var command = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
		context.Submit(1, &command);
		context.Api.CommandBufferRelease(command);
		context.Api.CommandEncoderRelease(encoder);

		var pending = true;
		var callback = PfnBufferMapCallback.From((_, _) => pending = false);
		context.Api.BufferMapAsync(stagingBuffer, MapMode.Read, 0, stagingBufferSize, callback, null);
		while (pending)
			context.PollDevice(false);
		var mapped = (byte*)context.Api.BufferGetConstMappedRange(stagingBuffer, 0, stagingBufferSize);
		// The staging rows are padded to 256 bytes (a WebGPU copy requirement); the frame is not.
		var rowBytes = (int)pixelWidth * 4;
		var pixels = new byte[rowBytes * (int)pixelHeight];
		fixed (byte* destinationBytes = pixels)
		{
			for (var row = 0; row < pixelHeight; row++)
				System.Buffer.MemoryCopy(mapped + row * bytesPerRow, destinationBytes + row * rowBytes, rowBytes, rowBytes);
		}
		context.Api.BufferUnmap(stagingBuffer);
		GC.KeepAlive(callback);
		return pixels;
	}

	/// <summary>What the ProGPU compositor compiled for the last offscreen render (DevFlow).</summary>
	public string CompositorMetricsDump()
	{
		if (compositor == null)
			return "no compositor";
		var m = compositor.Metrics;
		return $"target={m.RenderTargetWidth}x{m.RenderTargetHeight} dpi={m.DpiScale} " +
			$"drawCalls={m.DrawCallsCount} vectorVerts={m.VectorVerticesCount} vectorIdx={compositor.VectorIndexCount} " +
			$"textVerts={m.TextVerticesCount} textStyles={m.ActiveTextStyleCount} brushes={m.ActiveBrushCount} " +
			$"glyphBatches={m.GlyphRasterBatchSubmissions} glyphs={m.GlyphOutlineCompiledCount} " +
			$"compileMs={m.VisualTreeCompileTimeMs:F1} uploadMs={m.GpuUploadTimeMs:F1} renderMs={m.RenderPassTimeMs:F1} frameMs={m.FrameTimeMs:F1}";
	}

	/// <summary>The compositor's compiled draw calls, via reflection (DevFlow diagnostic).</summary>
	public string DumpDrawCalls()
	{
		if (compositor == null)
			return "no compositor";
		var listField = typeof(Compositor).GetField("_drawCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		if (listField == null || listField.GetValue(compositor) is not System.Collections.IList list)
			return "no draw call list";
		var parts = new System.Collections.Generic.List<string> { $"count={list.Count}" };
		foreach (var dc in list)
		{
			var t = dc?.GetType();
			if (t == null)
				continue;
			object G(string name) => t.GetField(name)?.GetValue(dc);
			parts.Add($"type={G("Type")} idxStart={G("IndexStart")} idxCount={G("IndexCount")} brush={G("Brush") ?? "null"}");
		}
		return string.Join(" | ", parts);
	}

	/// <summary>Walks the WinUI visual tree, calls OnRender on every node and reports the recorded
	/// commands (DevFlow diagnostic for "renders but draws nothing").</summary>
	public static string WinUICommandProbe(WinUIElement root)
	{
		if (root == null)
			return "no root";
		var ctx = new DrawingContext();
		var byType = new System.Collections.Generic.Dictionary<string, int>();
		var nodeTypes = new System.Collections.Generic.Dictionary<string, int>();
		var nodeCount = 0;
		void Walk(Visual visual)
		{
			nodeCount++;
			var typeName = visual.GetType().Name;
			nodeTypes[typeName] = nodeTypes.TryGetValue(typeName, out var c) ? c + 1 : 1;
			visual.OnRender(ctx);
			if (visual is ContainerVisual container)
			{
				foreach (var child in container.Children)
					Walk(child);
			}
		}
		Walk(root);
		foreach (var command in ctx.Commands)
		{
			var name = command.Type.ToString();
			byType[name] = byType.TryGetValue(name, out var n) ? n + 1 : 1;
		}
		var nodeSummary = string.Join(",", nodeTypes.Select(static kv => kv.Key + ":" + kv.Value));
		var cmdSummary = byType.Count == 0 ? "none" : string.Join(",", byType.Select(static kv => kv.Key + "=" + kv.Value));
		return $"nodes={nodeCount} [{nodeSummary}] commands={ctx.Commands.Count} [{cmdSummary}]";
	}

	void ReleaseSurface()
	{
		if (stagingBuffer != null && context != null)
		{
			context.Api.BufferDestroy(stagingBuffer);
			context.Api.BufferRelease(stagingBuffer);
			stagingBuffer = null;
		}
		texture?.Dispose();
		texture = null;
		pixelWidth = pixelHeight = 0;
	}

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		ReleaseSurface();
		if (context != null)
		{
			WgpuContext.OnWebGpuError -= OnWebGpuError;
			WgpuContext.OnWebGpuDeviceLost -= OnWebGpuDeviceLost;
		}
		compositor?.Dispose();
		compositor = null;
		context?.Dispose();
		context = null;
	}
}
