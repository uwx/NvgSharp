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
public class MoonWorksRenderer : INvgRenderer, IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct NvgVertex : IVertexType
	{
		public Vector2 Position;
		public Vector2 TexCoord;

		public static VertexElementFormat[] Formats =>
		[
			VertexElementFormat.Float2,
			VertexElementFormat.Float2
		];

		public static uint[] Offsets => [0, 8];
	}

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

	public GraphicsDevice GraphicsDevice => _device;

	private readonly GraphicsDevice _device;
	private readonly bool _edgeAntiAlias;

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
	private GpuBuffer _indexBuffer;
	private int _vertexBufferCapacity;
	private int _indexBufferCapacity;
	private short[] _triangleFanIndices;

	// The current render pass context (set by caller)
	private RenderPass _renderPass;
	private GpuCommandBuffer _commandBuffer;

	// Viewport dimensions for orthographic projection
	private uint _viewportWidth, _viewportHeight;

	// Depth-stencil texture for stencil operations
	private Texture _depthStencilTexture;

	// The color target format (must match the render target)
	private TextureFormat _colorTargetFormat;

	public MoonWorksRenderer(
		GraphicsDevice device,
		TitleStorage storage,
		string shaderDir,
		TextureFormat colorTargetFormat,
		bool edgeAntiAlias = true
	)
	{
		_device = device;
		_edgeAntiAlias = edgeAntiAlias;
		_colorTargetFormat = colorTargetFormat;

		_pointClampSampler = Sampler.Create(device, SamplerCreateInfo.PointClamp);

		LoadShaders(storage, shaderDir);
		CreatePipelines();

		_vertexBufferCapacity = 4096;
		_indexBufferCapacity = 4096 * 6;
		_vertexBuffer = GpuBuffer.Create<NvgVertex>(device, BufferUsageFlags.Vertex, (uint)_vertexBufferCapacity);
		_indexBuffer = GpuBuffer.Create<short>(device, BufferUsageFlags.Index, (uint)_indexBufferCapacity);
		_triangleFanIndices = BuildTriangleFanIndexBuffer(2048 * 6);
	}

	private void LoadShaders(TitleStorage storage, string shaderDir)
	{
		var defines = _edgeAntiAlias
			? new ShaderCross.HLSLDefine[] { new("EDGE_AA", "1") }
			: Array.Empty<ShaderCross.HLSLDefine>();

		_vertexShader = ShaderCross.Create(
			_device, storage,
			$"{shaderDir}/Nvg.vert.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Vertex,
			name: "NvgVert",
			includeDir: shaderDir,
			defines: defines
		);

		_fragFillGradient = ShaderCross.Create(_device, storage,
			$"{shaderDir}/NvgFillGradient.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgFillGradient",
			includeDir: shaderDir,
			defines: defines);

		_fragFillImage = ShaderCross.Create(_device, storage,
			$"{shaderDir}/NvgFillImage.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgFillImage",
			includeDir: shaderDir,
			defines: defines);

		_fragSimple = ShaderCross.Create(_device, storage,
			$"{shaderDir}/NvgSimple.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgSimple",
			includeDir: shaderDir,
			defines: defines);

		_fragTriangles = ShaderCross.Create(_device, storage,
			$"{shaderDir}/NvgTriangles.frag.hlsl", "main",
			ShaderCross.ShaderFormat.HLSL, ShaderStage.Fragment,
			name: "NvgTriangles",
			includeDir: shaderDir,
			defines: defines);
	}

	private void CreatePipelines()
	{
		var vertexInput = VertexInputState.CreateSingleBinding<NvgVertex>();

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
		return GraphicsPipeline.Create(_device, new GraphicsPipelineCreateInfo
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
			_device, width, height,
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

	// INvgRenderer: texture management (platform-agnostic path)
	public object CreateTexture(int width, int height)
	{
		return Texture.Create2D(_device, (uint)width, (uint)height,
			TextureFormat.R8G8B8A8Unorm, TextureUsageFlags.Sampler);
	}

	public Point GetTextureSize(object texture)
	{
		var tex = (Texture)texture;
		return new Point((int)tex.Width, (int)tex.Height);
	}

	public void SetTextureData(object texture, Rectangle bounds, byte[] data)
	{
		var tex = (Texture)texture;
		var dataSize = (uint)(bounds.Width * bounds.Height * 4);

		var transferBuffer = TransferBuffer.Create<byte>(_device, TransferBufferUsage.Upload, dataSize);
		var span = transferBuffer.Map<byte>(false);
		data.AsSpan(0, (int)dataSize).CopyTo(span);
		transferBuffer.Unmap();

		var cmd = _device.AcquireCommandBuffer();
		var copyPass = cmd.BeginCopyPass();
		copyPass.UploadToTexture(
			new TextureTransferInfo { TransferBuffer = transferBuffer, Offset = 0 },
			new TextureRegion
			{
				Texture = tex,
				X = (uint)bounds.X, Y = (uint)bounds.Y,
				W = (uint)bounds.Width, H = (uint)bounds.Height, D = 1
			},
			false
		);
		cmd.EndCopyPass(copyPass);
		_device.Submit(cmd);
		transferBuffer.Dispose();
	}

	public void Draw(float devicePixelRatio, IEnumerable<CallInfo> calls, Vertex[] vertexes)
	{
		if (_renderPass == null)
			return;

		// Upload vertex data
		UploadVertices(vertexes);

		// Set orthographic transform
		var transform = Matrix4x4.CreateOrthographicOffCenter(0, _viewportWidth, _viewportHeight, 0, 0, -1);
		_commandBuffer.PushVertexUniformData(new VertexUniforms { TransformMat = transform });

		_renderPass.SetViewport(new Viewport { X = 0, Y = 0, W = _viewportWidth, H = _viewportHeight, MinDepth = 0, MaxDepth = 1 });
		_renderPass.BindVertexBuffers(new BufferBinding(_vertexBuffer, 0));

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
			_vertexBuffer = GpuBuffer.Create<NvgVertex>(_device, BufferUsageFlags.Vertex, (uint)_vertexBufferCapacity);
		}

		var transferBuffer = TransferBuffer.Create<NvgVertex>(_device, TransferBufferUsage.Upload, (uint)vertexes.Length);
		var span = transferBuffer.Map<NvgVertex>(false);

		for (int i = 0; i < vertexes.Length; i++)
		{
			span[i] = new NvgVertex
			{
				Position = vertexes[i].Position,
				TexCoord = vertexes[i].TextureCoordinate
			};
		}

		transferBuffer.Unmap();

		var cmd = _device.AcquireCommandBuffer();
		var copyPass = cmd.BeginCopyPass();
		copyPass.UploadToBuffer(
			new TransferBufferLocation(transferBuffer, 0),
			new BufferRegion(_vertexBuffer, 0, (uint)(vertexes.Length * Marshal.SizeOf<NvgVertex>())),
			true
		);
		cmd.EndCopyPass(copyPass);
		_device.Submit(cmd);
		transferBuffer.Dispose();
	}

	private void UploadIndices(int vertexCount)
	{
		// Ensure triangle fan indices are big enough
		if (vertexCount > _triangleFanIndices.Length / 3 + 2)
		{
			_triangleFanIndices = BuildTriangleFanIndexBuffer(vertexCount);
		}

		int indexCount = (vertexCount - 2) * 3;
		if (indexCount <= 0) return;

		if (indexCount > _indexBufferCapacity)
		{
			_indexBuffer.Dispose();
			_indexBufferCapacity = indexCount * 2;
			_indexBuffer = GpuBuffer.Create<short>(_device, BufferUsageFlags.Index, (uint)_indexBufferCapacity);
		}

		var transferBuffer = TransferBuffer.Create<short>(_device, TransferBufferUsage.Upload, (uint)indexCount);
		var span = transferBuffer.Map<short>(false);
		_triangleFanIndices.AsSpan(0, indexCount).CopyTo(span);
		transferBuffer.Unmap();

		var cmd = _device.AcquireCommandBuffer();
		var copyPass = cmd.BeginCopyPass();
		copyPass.UploadToBuffer(
			new TransferBufferLocation(transferBuffer, 0),
			new BufferRegion(_indexBuffer, 0, (uint)(indexCount * sizeof(short))),
			true
		);
		cmd.EndCopyPass(copyPass);
		_device.Submit(cmd);
		transferBuffer.Dispose();
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

	private void BindTexture(object image)
	{
		if (image is Texture tex)
		{
			_renderPass.BindFragmentSamplers(new TextureSamplerBinding(tex, _pointClampSampler));
		}
	}

	private void DrawTriangleFan(int vertexOffset, int vertexCount)
	{
		if (vertexCount < 3) return;

		int maxFanVerts = _triangleFanIndices.Length / 3 + 2;
		if (vertexCount > maxFanVerts)
		{
			_triangleFanIndices = BuildTriangleFanIndexBuffer(vertexCount);
		}

		// For triangle fans with offset, we need to use indices relative to 0
		// and pass vertexOffset to DrawIndexedPrimitives
		int indexCount = (vertexCount - 2) * 3;
		UploadFanIndices(indexCount);

		_renderPass.BindIndexBuffer(new BufferBinding(_indexBuffer, 0), IndexElementSize.Sixteen);
		_renderPass.DrawIndexedPrimitives((uint)indexCount, 1, 0, vertexOffset, 0);
	}

	private void UploadFanIndices(int indexCount)
	{
		if (indexCount > _indexBufferCapacity)
		{
			_indexBuffer.Dispose();
			_indexBufferCapacity = indexCount * 2;
			_indexBuffer = GpuBuffer.Create<short>(_device, BufferUsageFlags.Index, (uint)_indexBufferCapacity);
		}

		var transferBuffer = TransferBuffer.Create<short>(_device, TransferBufferUsage.Upload, (uint)indexCount);
		var span = transferBuffer.Map<short>(false);
		_triangleFanIndices.AsSpan(0, indexCount).CopyTo(span);
		transferBuffer.Unmap();

		var cmd = _device.AcquireCommandBuffer();
		var copyPass = cmd.BeginCopyPass();
		copyPass.UploadToBuffer(
			new TransferBufferLocation(transferBuffer, 0),
			new BufferRegion(_indexBuffer, 0, (uint)(indexCount * sizeof(short))),
			true
		);
		cmd.EndCopyPass(copyPass);
		_device.Submit(cmd);
		transferBuffer.Dispose();
	}

	private void DrawTriangleStrip(int vertexOffset, int vertexCount)
	{
		if (vertexCount < 3) return;

		// Convert triangle strip to triangle list via indices
		int triangleCount = vertexCount - 2;
		int indexCount = triangleCount * 3;
		var indices = new short[indexCount];

		for (int i = 0; i < triangleCount; i++)
		{
			if (i % 2 == 0)
			{
				indices[i * 3 + 0] = (short)i;
				indices[i * 3 + 1] = (short)(i + 1);
				indices[i * 3 + 2] = (short)(i + 2);
			}
			else
			{
				indices[i * 3 + 0] = (short)(i + 1);
				indices[i * 3 + 1] = (short)i;
				indices[i * 3 + 2] = (short)(i + 2);
			}
		}

		if (indexCount > _indexBufferCapacity)
		{
			_indexBuffer.Dispose();
			_indexBufferCapacity = indexCount * 2;
			_indexBuffer = GpuBuffer.Create<short>(_device, BufferUsageFlags.Index, (uint)_indexBufferCapacity);
		}

		var transferBuffer = TransferBuffer.Create<short>(_device, TransferBufferUsage.Upload, (uint)indexCount);
		var span = transferBuffer.Map<short>(false);
		indices.AsSpan().CopyTo(span);
		transferBuffer.Unmap();

		var cmd = _device.AcquireCommandBuffer();
		var copyPass = cmd.BeginCopyPass();
		copyPass.UploadToBuffer(
			new TransferBufferLocation(transferBuffer, 0),
			new BufferRegion(_indexBuffer, 0, (uint)(indexCount * sizeof(short))),
			true
		);
		cmd.EndCopyPass(copyPass);
		_device.Submit(cmd);
		transferBuffer.Dispose();

		_renderPass.BindIndexBuffer(new BufferBinding(_indexBuffer, 0), IndexElementSize.Sixteen);
		_renderPass.DrawIndexedPrimitives((uint)indexCount, 1, 0, vertexOffset, 0);
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

		for (var i = 0; i < call.FillStrokeInfos.Count; i++)
		{
			var info = call.FillStrokeInfos[i];
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
		if (_edgeAntiAlias)
		{
			PushFragmentUniforms(ref Unsafe.AsRef(in call.UniformInfo2));
			BindTexture(call.UniformInfo2.Image);

			var aaPipeline = call.UniformInfo2.Image != null ? _pipelineStencilFill2Image : _pipelineStencilFill2;
			_renderPass.BindGraphicsPipeline(aaPipeline);
			_renderPass.SetStencilReference(0);

			for (var i = 0; i < call.FillStrokeInfos.Count; i++)
			{
				var info = call.FillStrokeInfos[i];
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
		var result = new short[(maxVertexCount - 2) * 3];
		for (var j = 2; j < maxVertexCount; ++j)
		{
			result[((j - 2) * 3) + 0] = 0;
			result[((j - 2) * 3) + 1] = (short)(j - 1);
			result[((j - 2) * 3) + 2] = (short)j;
		}
		return result;
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
		_indexBuffer?.Dispose();
		_depthStencilTexture?.Dispose();
	}
}

// Helper for ref readonly access
file static class Unsafe
{
	public static ref T AsRef<T>(in T value) => ref System.Runtime.CompilerServices.Unsafe.AsRef(in value);
}
