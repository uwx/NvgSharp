using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using MoonWorks.Graphics;
using MoonWorks.Storage;
using GpuBuffer = MoonWorks.Graphics.Buffer;
using GpuCommandBuffer = MoonWorks.Graphics.CommandBuffer;

namespace NvgSharp;

/// <summary>
/// MoonWorks implementation of INvgRenderer for NanoVG 2D vector rendering.
/// Handles stencil-based path fills, gradient/image painting, and anti-aliased strokes.
/// </summary>
public class MoonWorksRenderer : IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct VertexUniforms
	{
		public Matrix4x4 TransformMat;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FragmentUniforms
	{
		public Matrix4x4 ScissorMat;
		public Matrix4x4 PaintMat;
		public Vector4 InnerCol;
		public Vector4 OuterCol;
		public Vector2 ScissorExt;
		public Vector2 ScissorScale;
		public Vector2 Extent;
		public float Radius;
		public float Feather;
		public float StrokeMult;
		public float StrokeThr;
		// Padding to align to 16 bytes
		public float _pad0;
		public float _pad1;
	}

	public readonly GraphicsDevice GraphicsDevice;

	internal readonly bool EdgeAntiAlias;

	private Shader _vertexShader;
	private Shader _fragFillGradient, _fragFillImage, _fragSimple, _fragTriangles;

	// Pipelines for different rendering types × blend × stencil combinations
	private GraphicsPipeline _pipelineFillGradient;
	private GraphicsPipeline _pipelineFillImage;
	private GraphicsPipeline _pipelineSimple;
	private GraphicsPipeline _pipelineTriangles;

	// Stencil pipelines for complex fills
	private GraphicsPipeline _pipelineStencilFill1; // No color write, increment/decrement stencil
	private GraphicsPipeline _pipelineStencilFill2; // Anti-alias (FillGradient with stencil equal)
	private GraphicsPipeline _pipelineStencilFill2Image; // Anti-alias (FillImage with stencil equal)
	private GraphicsPipeline _pipelineStencilFill3; // Clear stencil (FillGradient with stencil notEqual)

	private Sampler _pointClampSampler;

	private GpuBuffer _vertexBuffer;
	private GpuBuffer _fanIndexBuffer;
	private GpuBuffer _stripIndexBuffer;
	private int _vertexBufferCapacity;
	private int _fanIndexBufferCapacity;
	private int _stripIndexBufferCapacity;
	private short[] _triangleFanIndices;
	private short[] _triangleStripIndices;

	// The current render pass context (set by caller)
	private RenderPass _renderPass;
	private GpuCommandBuffer _commandBuffer;

	// Viewport dimensions for orthographic projection
	private uint _viewportWidth, _viewportHeight;

	// Depth-stencil texture for stencil operations
	private Texture _depthStencilTexture;

	// The color target format (must match the render target)
	private TextureFormat _colorTargetFormat;

	// The possibly-shared resource uploader for uploading textures & buffers
	public readonly ResourceUploader ResourceUploader;

	public MoonWorksRenderer(
		GraphicsDevice device,
		TitleStorage storage,
		string shaderDir,
		TextureFormat colorTargetFormat,
		ResourceUploader resourceUploader,
		bool edgeAntiAlias = true
	)
	{
		GraphicsDevice = device;
		EdgeAntiAlias = edgeAntiAlias;
		_colorTargetFormat = colorTargetFormat;
		ResourceUploader = resourceUploader;

		_pointClampSampler = Sampler.Create(device, SamplerCreateInfo.PointClamp);

		LoadShaders(storage, shaderDir);
		CreatePipelines();

		_vertexBufferCapacity = 4096;
		_fanIndexBufferCapacity = ((4096 * 6) - 2) * 3;
		_stripIndexBufferCapacity  = ((4096 * 4) - 2) * 3;
		_vertexBuffer = GpuBuffer.Create<Vertex>(device, BufferUsageFlags.Vertex, (uint)_vertexBufferCapacity);
		_fanIndexBuffer = GpuBuffer.Create<short>(device, BufferUsageFlags.Index, (uint)_fanIndexBufferCapacity);
		_stripIndexBuffer = GpuBuffer.Create<short>(device, BufferUsageFlags.Index, (uint)_stripIndexBufferCapacity);
		_triangleFanIndices = BuildTriangleFanIndexBuffer(2048 * 6);
		_triangleStripIndices = BuildTriangleStripIndexBuffer(2048 * 4);

		ResourceUploader.SetBufferData(_fanIndexBuffer, 0, _triangleFanIndices);
		ResourceUploader.SetBufferData(_stripIndexBuffer, 0, _triangleStripIndices);
	}

	private void LoadShaders(TitleStorage storage, string shaderDir)
	{
		var defines = EdgeAntiAlias
			? new ShaderCross.HLSLDefine[] { new("EDGE_AA", "1") }
			: Array.Empty<ShaderCross.HLSLDefine>();

		_vertexShader = ShaderCross.Create(
			GraphicsDevice, storage,
			$"{shaderDir}/Nvg.vert.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Vertex,
			name: "NvgVert",
			includeDir: shaderDir,
			defines: defines
		);

		_fragFillGradient = ShaderCross.Create(GraphicsDevice, storage,
			$"{shaderDir}/NvgFillGradient.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgFillGradient",
			includeDir: shaderDir,
			defines: defines);

		_fragFillImage = ShaderCross.Create(GraphicsDevice, storage,
			$"{shaderDir}/NvgFillImage.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgFillImage",
			includeDir: shaderDir,
			defines: defines);

		_fragSimple = ShaderCross.Create(GraphicsDevice, storage,
			$"{shaderDir}/NvgSimple.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgSimple",
			includeDir: shaderDir,
			defines: defines);

		_fragTriangles = ShaderCross.Create(GraphicsDevice, storage,
			$"{shaderDir}/NvgTriangles.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgTriangles",
			includeDir: shaderDir,
			defines: defines);
	}

	private void CreatePipelines()
	{
		var vertexInput = VertexInputState.CreateSingleBinding<Vertex>();

		var colorTargetAlphaBlend = new ColorTargetDescription
		{
			Format = _colorTargetFormat,
			BlendState = ColorTargetBlendState.NonPremultipliedAlphaBlend
		};

		var colorTargetNoWrite = new ColorTargetDescription
		{
			Format = _colorTargetFormat,
			BlendState = ColorTargetBlendState.NoWrite
		};

		var dsFormat = TextureFormat.D24UnormS8Uint;
		var rasterCullNone = RasterizerState.CCW_CullNone;

		// Standard draw pipelines (no stencil)
		_pipelineFillGradient = CreatePipeline("NvgFillGradient", _fragFillGradient, vertexInput,
			colorTargetAlphaBlend, DepthStencilState.Disable, rasterCullNone, dsFormat);

		_pipelineFillImage = CreatePipeline("NvgFillImage", _fragFillImage, vertexInput,
			colorTargetAlphaBlend, DepthStencilState.Disable, rasterCullNone, dsFormat);

		_pipelineSimple = CreatePipeline("NvgSimple", _fragSimple, vertexInput,
			colorTargetAlphaBlend, DepthStencilState.Disable, rasterCullNone, dsFormat);

		_pipelineTriangles = CreatePipeline("NvgTriangles", _fragTriangles, vertexInput,
			colorTargetAlphaBlend, DepthStencilState.Disable, rasterCullNone, dsFormat);

		// Stencil fill pass 1: No color write, stencil increment (front) / decrement (back)
		var dsFill1 = new DepthStencilState
		{
			EnableStencilTest = true,
			EnableDepthTest = false,
			EnableDepthWrite = false,
			CompareMask = 0xff,
			WriteMask = 0xff,
			FrontStencilState = new StencilOpState
			{
				CompareOp = CompareOp.Always,
				FailOp = StencilOp.Keep,
				DepthFailOp = StencilOp.Keep,
				PassOp = StencilOp.IncrementAndWrap
			},
			BackStencilState = new StencilOpState
			{
				CompareOp = CompareOp.Always,
				FailOp = StencilOp.Keep,
				DepthFailOp = StencilOp.Keep,
				PassOp = StencilOp.DecrementAndWrap
			}
		};

		_pipelineStencilFill1 = CreatePipeline("NvgStencilFill1", _fragSimple, vertexInput,
			colorTargetNoWrite, dsFill1, rasterCullNone, dsFormat);

		// Stencil fill pass 2: stencil == 0, write color (anti-alias fringes)
		var dsFill2 = new DepthStencilState
		{
			EnableStencilTest = true,
			EnableDepthTest = false,
			EnableDepthWrite = false,
			CompareMask = 0xff,
			WriteMask = 0xff,
			FrontStencilState = new StencilOpState
			{
				CompareOp = CompareOp.Equal,
				FailOp = StencilOp.Keep,
				DepthFailOp = StencilOp.Keep,
				PassOp = StencilOp.Keep
			},
			BackStencilState = new StencilOpState
			{
				CompareOp = CompareOp.Equal,
				FailOp = StencilOp.Keep,
				DepthFailOp = StencilOp.Keep,
				PassOp = StencilOp.Keep
			}
		};

		_pipelineStencilFill2 = CreatePipeline("NvgStencilFill2Grad", _fragFillGradient, vertexInput,
			colorTargetAlphaBlend, dsFill2, rasterCullNone, dsFormat);

		_pipelineStencilFill2Image = CreatePipeline("NvgStencilFill2Img", _fragFillImage, vertexInput,
			colorTargetAlphaBlend, dsFill2, rasterCullNone, dsFormat);

		// Stencil fill pass 3: stencil != 0, clear stencil to zero, write color
		var dsFill3 = new DepthStencilState
		{
			EnableStencilTest = true,
			EnableDepthTest = false,
			EnableDepthWrite = false,
			CompareMask = 0xff,
			WriteMask = 0xff,
			FrontStencilState = new StencilOpState
			{
				CompareOp = CompareOp.NotEqual,
				FailOp = StencilOp.Zero,
				DepthFailOp = StencilOp.Zero,
				PassOp = StencilOp.Zero
			},
			BackStencilState = new StencilOpState
			{
				CompareOp = CompareOp.NotEqual,
				FailOp = StencilOp.Zero,
				DepthFailOp = StencilOp.Zero,
				PassOp = StencilOp.Zero
			}
		};

		_pipelineStencilFill3 = CreatePipeline("NvgStencilFill3", _fragFillGradient, vertexInput,
			colorTargetAlphaBlend, dsFill3, rasterCullNone, dsFormat);
	}

	private GraphicsPipeline CreatePipeline(
		string name, Shader fragmentShader, VertexInputState vertexInput,
		ColorTargetDescription colorTarget, DepthStencilState depthStencil,
		RasterizerState rasterizer, TextureFormat dsFormat)
	{
		return GraphicsPipeline.Create(GraphicsDevice, new GraphicsPipelineCreateInfo
		{
			Name = name,
			VertexShader = _vertexShader,
			FragmentShader = fragmentShader,
			VertexInputState = vertexInput,
			PrimitiveType = PrimitiveType.TriangleList,
			RasterizerState = rasterizer,
			MultisampleState = MultisampleState.None,
			DepthStencilState = depthStencil,
			TargetInfo = new GraphicsPipelineTargetInfo
			{
				ColorTargetDescriptions = [colorTarget],
				HasDepthStencilTarget = depthStencil.EnableStencilTest,
				DepthStencilFormat = dsFormat
			}
		});
	}

	/// <summary>
	/// Ensures the depth-stencil texture matches the given dimensions. Recreates if needed.
	/// </summary>
	public void EnsureDepthStencilTexture(uint width, uint height)
	{
		if (_depthStencilTexture != null && _depthStencilTexture.Width == width && _depthStencilTexture.Height == height)
			return;

		_depthStencilTexture?.Dispose();
		_depthStencilTexture = Texture.Create2D(
			GraphicsDevice, width, height,
			TextureFormat.D24UnormS8Uint,
			TextureUsageFlags.DepthStencilTarget
		);
	}

	/// <summary>
	/// Sets the render pass context. Must be called by the application before NvgContext.Flush().
	/// The render pass MUST have been begun with the appropriate depth-stencil target.
	/// </summary>
	public void SetRenderContext(GpuCommandBuffer commandBuffer, RenderPass renderPass, uint viewportWidth, uint viewportHeight)
	{
		_commandBuffer = commandBuffer;
		_renderPass = renderPass;
		_viewportWidth = viewportWidth;
		_viewportHeight = viewportHeight;
	}

	public void Draw(float devicePixelRatio, List<CallInfo> calls, Vertex[] vertexes)
	{
		if (_renderPass == null)
			throw new InvalidOperationException($"Called {nameof(Draw)} outside of a render pass, call {nameof(SetRenderContext)} first");

		// Upload vertex data
		UploadVertices(vertexes);

		// Scan calls to determine max fan and strip vertex counts needed
		int maxFanVerts = 0;
		int maxStripVerts = 0;
		foreach (var call in calls)
		{
			foreach (var info in call.FillStrokeInfos)
			{
				if (info.FillCount > maxFanVerts) maxFanVerts = info.FillCount;
				if (info.StrokeCount > maxStripVerts) maxStripVerts = info.StrokeCount;
			}

			if (call.TriangleCount > maxStripVerts && call.Type is CallType.Fill)
				maxStripVerts = call.TriangleCount;
		}
		UploadIndices(maxFanVerts, maxStripVerts);

		// Flush uploads so the cycle completes and buffer bindings pick up the new backing store.
		ResourceUploader.Upload();

		// Set orthographic transform
		var transform = Matrix4x4.CreateOrthographicOffCenter(0, _viewportWidth, _viewportHeight, 0, 0, -1);
		_commandBuffer.PushVertexUniformData(new VertexUniforms { TransformMat = transform });

		_renderPass.SetViewport(new Viewport { X = 0, Y = 0, W = _viewportWidth, H = _viewportHeight, MinDepth = 0, MaxDepth = 1 });
		_renderPass.BindVertexBuffers(_vertexBuffer);

		foreach (var call in calls)
		{
			switch (call.Type)
			{
				case CallType.Fill:
					RenderFill(call, vertexes);
					break;
				case CallType.ConvexFill:
					RenderConvexFill(call, vertexes);
					break;
				case CallType.Stroke:
					RenderStroke(call, vertexes);
					break;
				case CallType.Triangles:
					RenderTriangles(call, vertexes);
					break;
			}
		}
	}

	private void UploadVertices(Vertex[] vertexes)
	{
		if (vertexes.Length == 0) return;

		// Ensure vertex buffer is large enough
		if (vertexes.Length > _vertexBufferCapacity)
		{
			_vertexBuffer.Dispose();
			_vertexBufferCapacity = vertexes.Length * 2;
			_vertexBuffer = GpuBuffer.Create<Vertex>(GraphicsDevice, BufferUsageFlags.Vertex, (uint)_vertexBufferCapacity);
		}

		ResourceUploader.SetBufferData(_vertexBuffer, 0, vertexes);
	}

	private void UploadIndices(int fanVertexCount, int stripVertexCount)
	{
		{
			// Ensure triangle fan indices are big enough
			if (fanVertexCount > _triangleFanIndices.Length / 3 + 2)
			{
				_triangleFanIndices = BuildTriangleFanIndexBuffer(fanVertexCount);
			}

			int indexCount = (fanVertexCount - 2) * 3;
			if (indexCount <= 0) return;

			if (indexCount > _fanIndexBufferCapacity)
			{
				_fanIndexBuffer.Dispose();
				_fanIndexBufferCapacity = indexCount * 2;
				_fanIndexBuffer =
					GpuBuffer.Create<short>(GraphicsDevice, BufferUsageFlags.Index, (uint)_fanIndexBufferCapacity);
			}

			ResourceUploader.SetBufferData(_fanIndexBuffer, 0, _triangleFanIndices);
		}

		{
			if (stripVertexCount > _triangleStripIndices.Length / 3 + 2)
			{
				_triangleStripIndices = BuildTriangleStripIndexBuffer(fanVertexCount);
			}

			int indexCount = (stripVertexCount - 2) * 3;
			if (indexCount <= 0) return;

			if (indexCount > _stripIndexBufferCapacity)
			{
				_stripIndexBuffer.Dispose();
				_stripIndexBufferCapacity = indexCount * 2;
				_stripIndexBuffer =
					GpuBuffer.Create<short>(GraphicsDevice, BufferUsageFlags.Index, (uint)_stripIndexBufferCapacity);
			}

			ResourceUploader.SetBufferData(_stripIndexBuffer, 0, _triangleStripIndices);
		}

	}

	private void PushFragmentUniforms(ref UniformInfo uniform)
	{
		_commandBuffer.PushFragmentUniformData(new FragmentUniforms
		{
			ScissorMat = uniform.scissorMat,
			PaintMat = uniform.paintMat,
			InnerCol = uniform.innerCol,
			OuterCol = uniform.outerCol,
			ScissorExt = uniform.scissorExt,
			ScissorScale = uniform.scissorScale,
			Extent = uniform.extent,
			Radius = uniform.radius,
			Feather = uniform.feather,
			StrokeMult = uniform.strokeMult,
			StrokeThr = uniform.strokeThr
		});
	}

	private void BindTexture(Texture image)
	{
		if (image != null)
		{
			_renderPass.BindFragmentSamplers(new TextureSamplerBinding(image, _pointClampSampler));
		}
	}

	private void DrawTriangleFan(int vertexOffset, int vertexCount)
	{
		if (vertexCount < 3) return;

		int maxFanVerts = _triangleFanIndices.Length / 3 + 2;
		if (vertexCount > maxFanVerts)
		{
			ThrowInvalidVerts(nameof(maxFanVerts));
		}

		// For triangle fans with offset, we need to use indices relative to 0
		// and pass vertexOffset to DrawIndexedPrimitives
		int indexCount = (vertexCount - 2) * 3;

		_renderPass.BindIndexBuffer(_fanIndexBuffer, IndexElementSize.Sixteen);
		_renderPass.DrawIndexedPrimitives((uint)indexCount, 1, 0, vertexOffset, 0);
	}

	private void DrawTriangleStrip(int vertexOffset, int vertexCount)
	{
		if (vertexCount < 3) return;

		int maxStripVerts = _triangleStripIndices.Length / 3 + 2;
		if (vertexCount > maxStripVerts)
		{
			ThrowInvalidVerts(nameof(maxStripVerts));
		}

		// For triangle strips with offset, we need to use indices relative to 0
		// and pass vertexOffset to DrawIndexedPrimitives
		int indexCount = (vertexCount - 2) * 3;

		_renderPass.BindIndexBuffer(_stripIndexBuffer, IndexElementSize.Sixteen);
		_renderPass.DrawIndexedPrimitives((uint)indexCount, 1, 0, vertexOffset, 0);
	}

	private static void ThrowInvalidVerts(string paramName)
	{
		throw new InvalidOperationException($"vertex count > {paramName} which should be impossible if the correct buffer is uploaded");
	}

	private void DrawTriangleList(int vertexOffset, int vertexCount)
	{
		if (vertexCount < 3) return;
		_renderPass.DrawPrimitives((uint)vertexCount, 1, (uint)vertexOffset, 0);
	}

	private void RenderConvexFill(CallInfo call, Vertex[] vertexes)
	{
		PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo));
		BindTexture(call.UniformInfo.Image);

		var pipeline = call.UniformInfo.Image != null ? _pipelineFillImage : _pipelineFillGradient;
		_renderPass.BindGraphicsPipeline(pipeline);

		foreach (var info in call.FillStrokeInfos)
		{
			DrawTriangleFan(info.FillOffset, info.FillCount);
			if (info.StrokeCount > 0)
				DrawTriangleStrip(info.StrokeOffset, info.StrokeCount);
		}
	}

	private void RenderFill(CallInfo call, Vertex[] vertexes)
	{
		// Pass 1: Stencil fill (no color write)
		PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo));
		_renderPass.BindGraphicsPipeline(_pipelineStencilFill1);
		_renderPass.SetStencilReference(0);

		for (var i = 0; i < call.FillStrokeInfos.Count; i++)
		{
			var info = call.FillStrokeInfos[i];
			DrawTriangleFan(info.FillOffset, info.FillCount);
		}

		// Pass 2: Anti-aliased fringes (stencil == 0)
		if (EdgeAntiAlias)
		{
			PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo2));
			BindTexture(call.UniformInfo2.Image);

			var aaPipeline = call.UniformInfo2.Image != null ? _pipelineStencilFill2Image : _pipelineStencilFill2;
			_renderPass.BindGraphicsPipeline(aaPipeline);
			_renderPass.SetStencilReference(0);

			foreach (var info in call.FillStrokeInfos)
			{
				if (info.StrokeCount > 0)
					DrawTriangleStrip(info.StrokeOffset, info.StrokeCount);
			}
		}

		// Pass 3: Clear stencil (stencil != 0), fill the quad
		PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo2));
		_renderPass.BindGraphicsPipeline(_pipelineStencilFill3);
		_renderPass.SetStencilReference(0);
		DrawTriangleStrip(call.TriangleOffset, call.TriangleCount);
	}

	private void RenderStroke(CallInfo call, Vertex[] vertexes)
	{
		PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo));
		BindTexture(call.UniformInfo.Image);

		var pipeline = call.UniformInfo.Image != null ? _pipelineFillImage : _pipelineFillGradient;
		_renderPass.BindGraphicsPipeline(pipeline);

		for (var i = 0; i < call.FillStrokeInfos.Count; i++)
		{
			var info = call.FillStrokeInfos[i];
			if (info.StrokeCount > 0)
				DrawTriangleStrip(info.StrokeOffset, info.StrokeCount);
		}
	}

	private void RenderTriangles(CallInfo call, Vertex[] vertexes)
	{
		PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo));
		BindTexture(call.UniformInfo.Image);

		_renderPass.BindGraphicsPipeline(_pipelineTriangles);
		DrawTriangleList(call.TriangleOffset, call.TriangleCount);
	}

	private static short[] BuildTriangleFanIndexBuffer(int maxVertexCount)
	{
		if (maxVertexCount < 3) return [];

		// Convert triangle fan to triangle list via indices
		var result = new short[(maxVertexCount - 2) * 3];
		for (var j = 2; j < maxVertexCount; ++j)
		{
			result[((j - 2) * 3) + 0] = 0;
			result[((j - 2) * 3) + 1] = (short)(j - 1);
			result[((j - 2) * 3) + 2] = (short)j;
		}
		return result;
	}

	private static short[] BuildTriangleStripIndexBuffer(int maxVertexCount)
	{
		if (maxVertexCount < 3) return [];

		// Convert triangle strip to triangle list via indices
		int triangleCount = maxVertexCount - 2;
		int indexCount = triangleCount * 3;
		var indices = new short[indexCount];

		for (int i = 0; i < triangleCount; i++)
		{
			if (i % 2 == 0)
			{
				indices[i * 3 + 0] = (short)i;
				indices[i * 3 + 1] = (short)(i + 1);
			}
			else
			{
				indices[i * 3 + 0] = (short)(i + 1);
				indices[i * 3 + 1] = (short)i;
			}

			indices[i * 3 + 2] = (short)(i + 2);
		}

		return indices;
	}

	public void Dispose()
	{
		_vertexShader?.Dispose();
		_fragFillGradient?.Dispose();
		_fragFillImage?.Dispose();
		_fragSimple?.Dispose();
		_fragTriangles?.Dispose();

		_pipelineFillGradient?.Dispose();
		_pipelineFillImage?.Dispose();
		_pipelineSimple?.Dispose();
		_pipelineTriangles?.Dispose();
		_pipelineStencilFill1?.Dispose();
		_pipelineStencilFill2?.Dispose();
		_pipelineStencilFill2Image?.Dispose();
		_pipelineStencilFill3?.Dispose();

		_pointClampSampler?.Dispose();
		_vertexBuffer?.Dispose();
		_fanIndexBuffer?.Dispose();
		_depthStencilTexture?.Dispose();
	}
}

// Helper for ref readonly access
file static class Unsafe
{
	public static ref T AsRef<T>(in T value) => ref System.Runtime.CompilerServices.Unsafe.AsRef(in value);
}
