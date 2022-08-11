using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using static WGPU.NET.Wgpu;
using WGPU.NET;
using VertexBufferLayout = WGPU.NET.VertexBufferLayout;
using VertexState = WGPU.NET.VertexState;
using ColorTargetState = WGPU.NET.ColorTargetState;
using FragmentState = WGPU.NET.FragmentState;
using BindGroupEntry = WGPU.NET.BindGroupEntry;
using ImageCopyTexture = WGPU.NET.ImageCopyTexture;
using Buffer = WGPU.NET.Buffer;
using ImGuiNET;
using System.Linq;
using System.Diagnostics;

namespace WGPU.Tests
{
    public enum ColorSpaceHandling
    {
        /// <summary>
        /// Legacy-style color space handling. In this mode, the renderer will not convert sRGB vertex colors into linear space
        /// before blending them.
        /// </summary>
        Legacy = 0,
        /// <summary>
        /// Improved color space handling. In this mode, the render will convert sRGB vertex colors into linear space before
        /// blending them with colors from user Textures.
        /// </summary>
        Linear = 1,
    }

    /// <summary>
    /// Can render draw lists produced by ImGui.
    /// Also provides functions for updating ImGui input.
    /// </summary>
    public class ImGuiRenderer : IDisposable
    {
        private Device _gd;
        private readonly Assembly _assembly;

        // Device objects
        private Buffer _vertexBuffer;
        private Buffer _indexBuffer;
        private Buffer _projMatrixBuffer;
        private Texture _fontTexture;
        private ShaderModule _shader;
        private BindGroupLayout _layout;
        private BindGroupLayout _textureLayout;
        private RenderPipeline _pipeline;
        private BindGroup _mainResourceSet;
        private BindGroup _fontTextureResourceSet;
        private IntPtr _fontAtlasID = (IntPtr)1;
        private bool _controlDown;
        private bool _shiftDown;
        private bool _altDown;

        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        // Image trackers
        private readonly Dictionary<TextureView, ResourceSetInfo> _setsByView
            = new Dictionary<TextureView, ResourceSetInfo>();
        private readonly Dictionary<Texture, TextureView> _autoViewsByTexture
            = new Dictionary<Texture, TextureView>();
        private readonly Dictionary<IntPtr, ResourceSetInfo> _viewsById = new Dictionary<IntPtr, ResourceSetInfo>();

        private int _lastAssignedID = 100;
        private bool _frameBegun;

        /// <summary>
        /// Constructs a new ImGuiRenderer.
        /// </summary>
        /// <param name="gd">The Device used to create and update resources.</param>
        /// <param name="outputDescription">The output format.</param>
        /// <param name="width">The initial width of the rendering target. Can be resized.</param>
        /// <param name="height">The initial height of the rendering target. Can be resized.</param>
        public ImGuiRenderer(Device gd, (ColorTargetState[] colorTargets, TextureFormat? depthFormat) outputDescription, int width, int height)
            : this(gd, outputDescription, width, height, ColorSpaceHandling.Legacy) { }

        /// <summary>
        /// Constructs a new ImGuiRenderer.
        /// </summary>
        /// <param name="gd">The Device used to create and update resources.</param>
        /// <param name="outputDescription">The output format.</param>
        /// <param name="width">The initial width of the rendering target. Can be resized.</param>
        /// <param name="height">The initial height of the rendering target. Can be resized.</param>
        /// <param name="colorSpaceHandling">Identifies how the renderer should treat vertex colors.</param>
        public ImGuiRenderer(Device gd, (ColorTargetState[] colorTargets, TextureFormat? depthFormat) outputDescription, int width, int height, ColorSpaceHandling colorSpaceHandling)
        {
            _gd = gd;
            _assembly = typeof(ImGuiRenderer).GetTypeInfo().Assembly;
            _windowWidth = width;
            _windowHeight = height;

            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            ImGui.GetIO().Fonts.AddFontDefault();

            CreateDeviceResources(outputDescription);

            //TODO do we need this?
            //SetOpenTKKeyMappings();

            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        private unsafe void CreateDeviceResources((ColorTargetState[] colorTargets, TextureFormat? depthFormat) outputDescription)
        {
            _vertexBuffer = _gd.CreateBuffer("ImGui.NET Vertex Buffer", false, 10000, BufferUsage.Vertex | BufferUsage.CopyDst);

            _indexBuffer = _gd.CreateBuffer("ImGui.NET Index Buffer", false, 2000, BufferUsage.Index | BufferUsage.CopyDst);

            _projMatrixBuffer = _gd.CreateBuffer("ImGui.NET Projection Buffer", false, 64, BufferUsage.Uniform | BufferUsage.CopyDst);

            string shaderCode = LoadEmbeddedWgslShaderCode("imgui");
            _shader = _gd.CreateWgslShaderModule("ImGui.NET Shader",
                shaderCode);

            VertexBufferLayout[] vertexBufferLayouts = new VertexBufferLayout[]
            {
                new VertexBufferLayout
                {
                    ArrayStride = (ulong)(sizeof(Vector2)+sizeof(Vector2)+sizeof(uint)),
                    Attributes = new VertexAttribute[]
                    {
                        new VertexAttribute
                        {
                            //in_position
                            format = VertexFormat.Float32x2,
                            offset = 0,
                            shaderLocation = 0
                        },
                        new VertexAttribute
                        {
                            //in_texCoord
                            format = VertexFormat.Float32x2,
                            offset = (ulong)sizeof(Vector2),
                            shaderLocation = 1
                        },
                        new VertexAttribute
                        {
                            //in_color
                            format = VertexFormat.Unorm8x4,
                            offset = (ulong)(sizeof(Vector2)+sizeof(Vector2)),
                            shaderLocation = 2
                        }
                    }
                }
            };

            _layout = _gd.CreateBindgroupLayout(null,
                new BindGroupLayoutEntry[]
                {
                    new BindGroupLayoutEntry
                    {
                        binding = 0,
                        buffer = new BufferBindingLayout
                        {
                            type = BufferBindingType.Uniform,
                        },
                        visibility = (uint)ShaderStage.Vertex
                    },
                    new BindGroupLayoutEntry
                    {
                        binding = 1,
                        sampler = new SamplerBindingLayout
                        {
                            type = SamplerBindingType.Filtering
                        },
                        visibility = (uint)ShaderStage.Fragment
                    }
                }
            );

            _textureLayout = _gd.CreateBindgroupLayout(null,
                new BindGroupLayoutEntry[]
                {
                    new BindGroupLayoutEntry
                    {
                        binding = 0,
                        texture = new TextureBindingLayout
                        {
                            viewDimension = TextureViewDimension.TwoDimensions,
                            sampleType = TextureSampleType.Float
                        },
                        visibility = (uint)ShaderStage.Fragment
                    }
                }
            );

            var pipelineLayout = _gd.CreatePipelineLayout(null,
                new BindGroupLayout[]
                {
                    _layout,
                    _textureLayout
                }
            );

            _pipeline = _gd.CreateRenderPipeline("ImGui.NET Pipeline",
                pipelineLayout,
                new VertexState
                {
                    Module = _shader,
                    EntryPoint = "vs_main",
                    bufferLayouts = vertexBufferLayouts
                },
                primitiveState: new PrimitiveState()
                {
                    topology = PrimitiveTopology.TriangleList,
                    stripIndexFormat = IndexFormat.Undefined,
                    frontFace = FrontFace.CCW,
                    cullMode = CullMode.None
                },
                multisampleState: new MultisampleState
                {
                    count = 1,
                    mask = uint.MaxValue,
                    alphaToCoverageEnabled = false
                },
                depthStencilState: outputDescription.depthFormat==null ? null : new DepthStencilState
                {
                    format = outputDescription.depthFormat.Value,
                    depthCompare = CompareFunction.Always,
                    stencilBack = new Wgpu.StencilFaceState
                    {
                        depthFailOp = Wgpu.StencilOperation.Keep,
                        failOp = Wgpu.StencilOperation.Keep,
                        passOp = Wgpu.StencilOperation.Keep,
                        compare = Wgpu.CompareFunction.Always
                    },
                    stencilFront = new Wgpu.StencilFaceState
                    {
                        depthFailOp = Wgpu.StencilOperation.Keep,
                        failOp = Wgpu.StencilOperation.Keep,
                        passOp = Wgpu.StencilOperation.Keep,
                        compare = Wgpu.CompareFunction.Always
                    },
                },
                fragmentState: new FragmentState
                {
                    Module = _shader,
                    EntryPoint = "fs_main_linear",
                    colorTargets = outputDescription.colorTargets
                }
            );

            Sampler pointSampler = _gd.CreateSampler("ImGui.NET PointSampler",
                AddressMode.Repeat, AddressMode.Repeat, AddressMode.Repeat,
                FilterMode.Nearest, FilterMode.Nearest, MipmapFilterMode.Nearest,
                0, 0, default, 1);

            _mainResourceSet = _gd.CreateBindGroup("ImGui.NET",
                _layout,
                new BindGroupEntry[]
                {
                    new BindGroupEntry
                    {
                        Binding = 0,
                        Offset = 0,
                        Buffer = _projMatrixBuffer
                    },
                    new BindGroupEntry
                    {
                        Binding = 1,
                        Sampler = pointSampler
                    }
                });

            RecreateFontDeviceTexture();
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// Pass the returned handle to Image() or ImageButton().
        /// </summary>
        public IntPtr GetOrCreateImGuiBinding(TextureView textureView)
        {
            if (!_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                BindGroup resourceSet = _gd.CreateBindGroup(null, _textureLayout,
                    new BindGroupEntry[]
                    {
                        new BindGroupEntry
                        {
                            Binding= 0,
                            TextureView = textureView
                        }
                    }
                );
                rsi = new ResourceSetInfo(GetNextImGuiBindingID(), resourceSet);

                _setsByView.Add(textureView, rsi);
                _viewsById.Add(rsi.ImGuiBinding, rsi);
            }

            return rsi.ImGuiBinding;
        }

        public void RemoveImGuiBinding(TextureView textureView)
        {
            if (_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                _setsByView.Remove(textureView);
                _viewsById.Remove(rsi.ImGuiBinding);
                rsi.ResourceSet.FreeHandle();
            }
        }

        private IntPtr GetNextImGuiBindingID()
        {
            int newID = _lastAssignedID++;
            return (IntPtr)newID;
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// Pass the returned handle to Image() or ImageButton().
        /// </summary>
        public IntPtr GetOrCreateImGuiBinding(Texture texture)
        {
            if (!_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                //TODO how do we get this information, maybe through parameters
                textureView = texture.CreateTextureView();
                _autoViewsByTexture.Add(texture, textureView);
            }

            return GetOrCreateImGuiBinding(textureView);
        }

        public void RemoveImGuiBinding(Texture texture)
        {
            if (_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                _autoViewsByTexture.Remove(texture);
                textureView.FreeHandle();
                RemoveImGuiBinding(textureView);
            }
        }

        /// <summary>
        /// Retrieves the shader texture binding for the given helper handle.
        /// </summary>
        public BindGroup GetImageResourceSet(IntPtr imGuiBinding)
        {
            if (!_viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo rsi))
            {
                throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
            }

            return rsi.ResourceSet;
        }

        public void ClearCachedImageResources()
        {
            foreach (var rsi in _setsByView.Values)
            {
                rsi.ResourceSet.FreeHandle();
            }

            foreach (var view in _autoViewsByTexture.Values)
            {
                view.FreeHandle();
            }

            _setsByView.Clear();
            _viewsById.Clear();
            _autoViewsByTexture.Clear();
            _lastAssignedID = 100;
        }

        private byte[] LoadEmbeddedSpirVShaderCode(string name)
        {
            string resourceName = name + ".spv";
            return GetEmbeddedResourceBytes(resourceName);
        }

        private string LoadEmbeddedWgslShaderCode(string name)
        {
            string resourceName = name + ".wgsl";
            return GetEmbeddedResourceText(resourceName);
        }

        private string GetEmbeddedResourceText(string resourceName)
        {
            using (StreamReader sr = new StreamReader(_assembly.GetManifestResourceStream(_assembly.GetManifestResourceNames().First(x => x.EndsWith(resourceName)))))
            {
                return sr.ReadToEnd();
            }
        }

        private byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            using (Stream s = _assembly.GetManifestResourceStream(_assembly.GetManifestResourceNames().First(x => x.EndsWith(resourceName))))
            {
                byte[] ret = new byte[s.Length];
                s.Read(ret, 0, (int)s.Length);
                return ret;
            }
        }

        /// <summary>
        /// Recreates the device texture used to render text.
        /// </summary>
        public unsafe void RecreateFontDeviceTexture()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            // Build
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);

            // Store our identifier
            io.Fonts.SetTexID(_fontAtlasID);

            _fontTexture?.DestroyResource();
            _fontTexture = _gd.CreateTexture("ImGui.NET Font Texture",
                TextureUsage.TextureBinding | TextureUsage.CopyDst,
                TextureDimension.TwoDimensions,
                new Extent3D
                {
                    width = (uint)width,
                    height = (uint)height,
                    depthOrArrayLayers = 1
                },
                TextureFormat.RGBA8Unorm,
                mipLevelCount: 1,
                sampleCount: 1
            );

            _gd.GetQueue().WriteTexture(
                destination: new ImageCopyTexture
                {
                    Texture = _fontTexture,
                    Aspect = TextureAspect.All,
                    MipLevel = 0,
                    Origin = new Origin3D()
                },
                data: new ReadOnlySpan<byte>(pixels, bytesPerPixel * width * height),
                dataLayout: new TextureDataLayout
                {
                    offset = 0,
                    bytesPerRow = (uint)width * sizeof(uint),
                    rowsPerImage = (uint)height
                },
                writeSize: _fontTexture.Size
            );

            _fontTextureResourceSet?.FreeHandle();
            _fontTextureResourceSet = _gd.CreateBindGroup("ImGui.NET Font Texture ResourceSet",
                _textureLayout,
                new BindGroupEntry[]
                {
                    new BindGroupEntry
                    {
                        Binding = 0,
                        TextureView = _fontTexture.CreateTextureView()
                    }
                }
            );

            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// Renders the ImGui draw list data.
        /// </summary>
        public unsafe void Render(RenderPassEncoder cl)
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData(), cl);
            }
        }

        /// <summary>
        /// Updates ImGui input and IO configuration state.
        /// </summary>
        public void Update(float deltaSeconds)
        {
            BeginUpdate(deltaSeconds);
            //TODO
            //UpdateImGuiInput(snapshot);
            EndUpdate();
        }

        /// <summary>
        /// Called before we handle the input in <see cref="Update(float, InputSnapshot)"/>.
        /// This render ImGui and update the state.
        /// </summary>
        protected void BeginUpdate(float deltaSeconds)
        {
            if (_frameBegun)
            {
                ImGui.Render();
            }

            SetPerFrameImGuiData(deltaSeconds);
        }

        /// <summary>
        /// Called at the end of <see cref="Update(float, InputSnapshot)"/>.
        /// This tells ImGui that we are on the next frame.
        /// </summary>
        protected void EndUpdate()
        {
            _frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// Sets per-frame data based on the associated window.
        /// This is called by Update(float).
        /// </summary>
        private unsafe void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
        }

        private unsafe void RenderImDrawData(ImDrawDataPtr draw_data, RenderPassEncoder cl)
        {
            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            uint totalVBSize = (uint)(draw_data.TotalVtxCount * sizeof(ImDrawVert));
            if (totalVBSize > _vertexBuffer.SizeInBytes)
            {
                _vertexBuffer.DestroyResource();
                _vertexBuffer = _gd.CreateBuffer("ImGui.Net VertexBuffer", false, (ulong)NextValidBufferSize((int)(totalVBSize * 1.5f)), BufferUsage.Vertex | BufferUsage.CopyDst);
            }

            uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > _indexBuffer.SizeInBytes)
            {
                _indexBuffer.DestroyResource();
                _indexBuffer = _gd.CreateBuffer("ImGui.Net IndexBuffer", false, (uint)NextValidBufferSize((int)(totalIBSize * 1.5f)), BufferUsage.Index | BufferUsage.CopyDst);
            }

            var queue = _gd.GetQueue();

            var vertexData = new ImDrawVert[NextValidBufferSize(draw_data.TotalVtxCount)];
            var indexData = new ushort[NextValidBufferSize(draw_data.TotalIdxCount)];

            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[i];

                new ReadOnlySpan<ImDrawVert>((void*)cmd_list.VtxBuffer.Data, cmd_list.VtxBuffer.Size)
                .CopyTo(new Span<ImDrawVert>(vertexData, (int)vertexOffsetInVertices, cmd_list.VtxBuffer.Size));

                new ReadOnlySpan<ushort>((void*)cmd_list.IdxBuffer.Data, cmd_list.IdxBuffer.Size)
                .CopyTo(new Span<ushort>(indexData, (int)indexOffsetInElements, cmd_list.IdxBuffer.Size));

                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            queue.WriteBuffer<ImDrawVert>(
                    _vertexBuffer,
                    bufferOffset: 0,
                    data: vertexData
                );

            queue.WriteBuffer<ushort>(
                _indexBuffer,
                bufferOffset: 0,
                data: indexData
            );

            // Setup orthographic projection matrix into our constant buffer
            {
                var io = ImGui.GetIO();

                ReadOnlySpan<Matrix4x4> span = stackalloc Matrix4x4[]
                {
                    Matrix4x4.CreateOrthographicOffCenter(
                    0f,
                    io.DisplaySize.X,
                    io.DisplaySize.Y,
                    0f,
                    -1.0f,
                    1.0f)
                };

                queue.WriteBuffer(_projMatrixBuffer, 0, span);
            }

            cl.SetVertexBuffer(0, _vertexBuffer, 0, totalVBSize);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.Uint16, 0, totalIBSize);
            cl.SetPipeline(_pipeline);
            cl.SetBindGroup(0, _mainResourceSet, Array.Empty<uint>());

            draw_data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            // Render command lists
            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        if (pcmd.TextureId != IntPtr.Zero)
                        {
                            if (pcmd.TextureId == _fontAtlasID)
                            {
                                cl.SetBindGroup(1, _fontTextureResourceSet, Array.Empty<uint>());
                            }
                            else
                            {
                                cl.SetBindGroup(1, GetImageResourceSet(pcmd.TextureId), Array.Empty<uint>());
                            }
                        }

                        cl.SetScissorRect(
                            (uint)pcmd.ClipRect.X,
                            (uint)pcmd.ClipRect.Y,
                            (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                            (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                        cl.DrawIndexed(pcmd.ElemCount, 1, (uint)idx_offset, vtx_offset, 0);
                    }

                    idx_offset += (int)pcmd.ElemCount;
                }
                vtx_offset += cmd_list.VtxBuffer.Size;
            }
        }

        /// <summary>
        /// Frees all graphics resources used by the renderer.
        /// </summary>
        public void Dispose()
        {
            _vertexBuffer.DestroyResource();
            _indexBuffer.DestroyResource();
            _projMatrixBuffer.DestroyResource();
            _fontTexture.DestroyResource();
            _shader.FreeHandle();
            _layout.FreeHandle();
            _textureLayout.FreeHandle();
            _pipeline.FreeHandle();
            _mainResourceSet.FreeHandle();
            _fontTextureResourceSet.FreeHandle();

            foreach (var rsi in _setsByView.Values)
            {
                rsi.ResourceSet.FreeHandle();
            }

            foreach (var view in _autoViewsByTexture.Values)
            {
                view.FreeHandle();
            }
        }

        private static int NextValidBufferSize(int size) => size - size % 64 + 64;

        private struct ResourceSetInfo
        {
            public readonly IntPtr ImGuiBinding;
            public readonly BindGroup ResourceSet;

            public ResourceSetInfo(IntPtr imGuiBinding, BindGroup resourceSet)
            {
                ImGuiBinding = imGuiBinding;
                ResourceSet = resourceSet;
            }
        }
    }
}
