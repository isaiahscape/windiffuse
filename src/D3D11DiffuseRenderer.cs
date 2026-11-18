using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DiffuseWindows
{
    internal sealed class D3D11DiffuseRenderer : IDisposable
    {
        public const int AlbumFboSize = 192;
        private const double TimeWrap = 10000.0;

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderParams
        {
            public float ResX;
            public float ResY;
            public float BrightnessOffset;
            public float Level;
            public float LerpFactor;
            public float Scale;
            public float Border;
            public float TimeScaled;
            public float NumSegments;
            public int PreCompute;
            public float Pad0;
            public float Pad1;
        }

        private sealed class RenderTarget : IDisposable
        {
            public ID3D11Texture2D Texture;
            public ID3D11RenderTargetView Rtv;
            public ID3D11ShaderResourceView Srv;
            public int Width;
            public int Height;

            public void Dispose()
            {
                if (Srv != null) { Srv.Dispose(); Srv = null; }
                if (Rtv != null) { Rtv.Dispose(); Rtv = null; }
                if (Texture != null) { Texture.Dispose(); Texture = null; }
            }
        }

        private const string FullscreenVsHlsl = @"
struct VSOut {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};
VSOut main(uint id : SV_VertexID) {
    VSOut o;
    o.uv = float2((id == 1) ? 2.0 : 0.0, (id == 2) ? 2.0 : 0.0);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}";

        private const string ParamsHeaderHlsl = @"
cbuffer Params : register(b0) {
    float2 u_resolution;
    float  u_brightnessOffset;
    float  u_level;
    float  u_lerp;
    float  u_scale;
    float  u_border;
    float  u_timeScaled;
    float  u_numSegments;
    int    u_pre_compute;
    float2 _pad;
};
struct PSIn {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};";

        private const string ColorPsHlsl = ParamsHeaderHlsl + @"
Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);
float4 main(PSIn input) : SV_Target {
    return tex0.Sample(samp0, input.uv);
}";

        private const string KawaseBlurPsHlsl = ParamsHeaderHlsl + @"
Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);
float4 main(PSIn input) : SV_Target {
    float2 step1 = (u_level + 0.5) / u_resolution;
    float2 step2 = float2(-step1.x, step1.y);
    float4 color = float4(0.0, 0.0, 0.0, 0.0);
    color += tex0.Sample(samp0, input.uv + step1) * 0.25;
    color += tex0.Sample(samp0, input.uv - step1) * 0.25;
    color += tex0.Sample(samp0, input.uv + step2) * 0.25;
    color += tex0.Sample(samp0, input.uv - step2) * 0.25;
    color.rgb *= (1.0 + u_brightnessOffset);
    color.a = 1.0;
    return color;
}";

        private const string FadePsHlsl = ParamsHeaderHlsl + @"
Texture2D tex0 : register(t0);
Texture2D texTarget : register(t1);
SamplerState samp0 : register(s0);
float4 main(PSIn input) : SV_Target {
    float4 color = tex0.Sample(samp0, input.uv);
    float4 colorTarget = texTarget.Sample(samp0, input.uv);
    float diff = length(color.rgb - colorTarget.rgb);
    float blend = max(u_lerp, min(0.01 / max(diff, 0.0001), 1.0));
    return float4(lerp(color.rgb, colorTarget.rgb, blend), 1.0);
}";

        private const string NoisePsHlsl = ParamsHeaderHlsl + @"
Texture2D tex0 : register(t0);
Texture2D texRand : register(t2);
SamplerState sampMirror : register(s0);
SamplerState sampWrap : register(s1);

float glsl_mod(float x, float y) {
    return x - y * floor(x / y);
}

float random_fn(float2 st) {
    float dt = dot(st, float2(12.9898, 78.233));
    float sn = glsl_mod(dt, 3.14);
    return frac(sin(sn) * 43758.5453);
}

float noise_fn(float2 st) {
    if (u_pre_compute == 0) {
        float2 i = floor(st);
        float2 f = frac(st);
        float a = random_fn(i);
        float b = random_fn(i + float2(1.0, 0.0));
        float c = random_fn(i + float2(0.0, 1.0));
        float d = random_fn(i + float2(1.0, 1.0));
        float2 u = f * f * (3.0 - 2.0 * f);
        return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
    } else {
        return texRand.Sample(sampWrap, st / 8.0).r;
    }
}

float fbm(float2 st) {
    float v = 0.0;
    float a = 0.5;
    float2 shift = st * 0.5;
    float c = cos(0.5);
    float s = sin(0.5);
    float2x2 rot = float2x2(c, -s, s, c);
    [unroll]
    for (int i = 0; i < 3; ++i) {
        v += a * noise_fn(st);
        st = mul(rot, st) * 1.3 + shift;
        a *= 0.5;
    }
    return v;
}

float4 main(PSIn input) : SV_Target {
    float2 uv = input.uv;
    if (u_resolution.x > u_resolution.y) {
        uv.x = uv.x * (u_resolution.x / u_resolution.y);
    } else {
        uv.y = uv.y / (u_resolution.x / u_resolution.y);
    }
    float2 q = float2(
        fbm(uv + float2(u_timeScaled * 0.1, cos(u_timeScaled))),
        fbm(uv + float2(u_timeScaled * 0.1, u_timeScaled))
    );
    float2 r = float2(
        fbm(0.5 * uv + 2.0 * q + float2(u_timeScaled * 0.05, 0.0)),
        fbm(0.5 * uv + 2.0 * q + float2(u_timeScaled * 0.1, 0.0))
    );
    r = (r - 0.5) * (u_scale + u_border) + 0.5;
    float4 color = tex0.Sample(sampMirror, r);
    return float4(color.rgb, 1.0);
}";

        private const string FlutedGlassPsHlsl = ParamsHeaderHlsl + @"
Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);
float4 main(PSIn input) : SV_Target {
    const float u_inputOutputRatio = 1.8;
    const float u_overlap = 0.8;
    const float u_light_strength = 0.10;

    float2 uv = input.uv;
    float segmentWidth = 1.0 / max(u_numSegments, 1.0);
    float inputSegmentWidth = segmentWidth * u_inputOutputRatio;
    float overlapWidth = segmentWidth * u_overlap;

    float segmentIndex = floor(uv.x / segmentWidth);
    float segmentStart = segmentIndex * segmentWidth;
    float localUVx = (uv.x - segmentStart) / segmentWidth;

    float compressedX = log(1.0 + localUVx * 9.0) / log(10.0);
    float inputSegmentStart = segmentIndex * (inputSegmentWidth - overlapWidth);
    float2 inputUV = float2(inputSegmentStart + compressedX * inputSegmentWidth, uv.y);

    float4 color = tex0.Sample(samp0, inputUV);

    float gradY = 1.0 - uv.y;
    float gradientStrength = smoothstep(0.8, 1.0, gradY);
    color = lerp(color, float4(0.0, 0.0, 0.0, 0.5), gradientStrength * 0.5);

    float rightGradientStrength = smoothstep(0.8, 1.0, localUVx);
    color = lerp(color, float4(0.0, 0.0, 0.0, rightGradientStrength), rightGradientStrength * u_light_strength);

    float leftGradientStrength = smoothstep(0.1, 0.0, localUVx);
    color = lerp(color, float4(1.0, 1.0, 1.0, leftGradientStrength), leftGradientStrength * u_light_strength);

    return float4(color.rgb, 1.0);
}";

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private IDXGISwapChain _swapChain;
        private ID3D11RenderTargetView _backBufferRtv;
        private IDCompositionDevice _dcompDevice;
        private IDCompositionTarget _dcompTarget;
        private IDCompositionVisual _dcompVisual;
        private bool _usingDComp;

        private ID3D11VertexShader _fullscreenVs;
        private ID3D11PixelShader _colorPs;
        private ID3D11PixelShader _blurPs;
        private ID3D11PixelShader _fadePs;
        private ID3D11PixelShader _noisePs;
        private ID3D11PixelShader _glassPs;
        private ID3D11Buffer _constantBuffer;

        private ID3D11SamplerState _samplerClamp;
        private ID3D11SamplerState _samplerMirror;
        private ID3D11SamplerState _samplerWrap;

        private RenderTarget _fboA;
        private RenderTarget _fboB;
        private RenderTarget _fboC;
        private RenderTarget _screenFbo;

        private ID3D11Texture2D _randTexture;
        private ID3D11ShaderResourceView _randSrv;

        private int _width;
        private int _height;

        private bool _playing;
        private float _minSpeed = 0.05f;
        private float _maxSpeed = 0.50f;
        private float _targetSpeed = 0.20f;
        private float _currentSpeed = 1.0f;

        private float _noiseScale = 1.0f;
        private string _postProcessingMode = "blur";
        private float _brightnessOffset = 0.0f;
        private bool _performanceMode;

        private float _beatStrength = 1.0f;
        private float _beatLerpAlpha = 0.08f;
        private float _targetBeat;
        private float _currentBeat;

        private double _scaledTime;
        private bool _hasInitialTexture;

        public void Initialize(IntPtr hwnd, int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            FeatureLevel[] levels = new FeatureLevel[]
            {
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            bool dcompReady = TryInitializeDirectComposition(hwnd, levels);
            if (!dcompReady)
            {
                InitializeHwndSwapChainFallback(hwnd, levels);
            }

            CreateBackBufferRtv();

            _fullscreenVs = CompileVertexShader(FullscreenVsHlsl);
            _colorPs = CompilePixelShader(ColorPsHlsl);
            _blurPs = CompilePixelShader(KawaseBlurPsHlsl);
            _fadePs = CompilePixelShader(FadePsHlsl);
            _noisePs = CompilePixelShader(NoisePsHlsl);
            _glassPs = CompilePixelShader(FlutedGlassPsHlsl);

            _constantBuffer = _device.CreateBuffer(
                new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default)
            );

            _samplerClamp = CreateSampler(TextureAddressMode.Clamp);
            _samplerMirror = CreateSampler(TextureAddressMode.Mirror);
            _samplerWrap = CreateSampler(TextureAddressMode.Wrap);

            _fboA = CreateRenderTarget(AlbumFboSize, AlbumFboSize);
            _fboB = CreateRenderTarget(AlbumFboSize, AlbumFboSize);
            _fboC = CreateRenderTarget(AlbumFboSize, AlbumFboSize);
            _screenFbo = CreateRenderTarget(_width, _height);

            LoadRandTexture();
        }

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;

            _width = width;
            _height = height;

            _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            if (_backBufferRtv != null) { _backBufferRtv.Dispose(); _backBufferRtv = null; }
            _swapChain.ResizeBuffers(0, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateBackBufferRtv();
            if (_usingDComp && _dcompDevice != null)
            {
                _dcompDevice.Commit();
            }

            if (_screenFbo != null) { _screenFbo.Dispose(); _screenFbo = null; }
            _screenFbo = CreateRenderTarget(_width, _height);
        }

        public void ApplySettings(AppSettings settings)
        {
            _noiseScale = settings.NoiseScale * 1.2f + 0.4f;
            _minSpeed = settings.SpeedMin;
            _maxSpeed = settings.SpeedMax;
            _targetSpeed = _playing ? _maxSpeed : _minSpeed;
            _brightnessOffset = settings.BrightnessOffset * 0.05f;
            _postProcessingMode = settings.PostProcessingMode;
            _performanceMode = settings.PerformanceMode;
            _beatStrength = settings.BeatStrength;
            _beatLerpAlpha = 0.06f * settings.BeatStrength + 0.04f;
            if (!settings.EnableLiveBeats)
            {
                _targetBeat = 0.0f;
            }
        }

        public void SetPlaybackState(bool playing)
        {
            _playing = playing;
            _targetSpeed = playing ? _maxSpeed : _minSpeed;
        }

        public void SetAudioBands(double[] bands)
        {
            if (bands == null || bands.Length < 4) return;
            _targetBeat = (
                ((float)bands[0] * 1.5f) -
                ((float)bands[1] * 0.1f) -
                ((float)bands[2] * 0.2f) -
                ((float)bands[3] * 0.3f)
            ) * _beatStrength;
        }

        public void ResetAudioBeat()
        {
            _targetBeat = 0.0f;
        }

        public void SetAlbumBitmap(Bitmap sourceBitmap)
        {
            if (sourceBitmap == null) return;

            ID3D11Texture2D srcTex;
            ID3D11ShaderResourceView srcSrv;
            if (!CreateTextureFromBitmap(sourceBitmap, AlbumFboSize, AlbumFboSize, out srcTex, out srcSrv))
                return;

            var tempA = CreateRenderTarget(AlbumFboSize, AlbumFboSize);
            var tempB = CreateRenderTarget(AlbumFboSize, AlbumFboSize);

            try
            {
                DrawPass(tempA.Rtv, AlbumFboSize, AlbumFboSize, _colorPs, srcSrv, null, null, _samplerClamp, _samplerWrap, new ShaderParams());

                int[] levels = new int[] { 0, 1, 1, 2, 2, 3, 3, 4 };
                for (int i = 0; i < levels.Length; i++)
                {
                    bool even = (i % 2) == 0;
                    RenderTarget dst = even ? tempB : tempA;
                    RenderTarget src = even ? tempA : tempB;

                    var p = new ShaderParams
                    {
                        ResX = AlbumFboSize,
                        ResY = AlbumFboSize,
                        BrightnessOffset = _brightnessOffset,
                        Level = levels[i] * 2
                    };
                    DrawPass(dst.Rtv, AlbumFboSize, AlbumFboSize, _blurPs, src.Srv, null, null, _samplerClamp, _samplerWrap, p);
                }

                DrawPass(_fboB.Rtv, AlbumFboSize, AlbumFboSize, _colorPs, tempA.Srv, null, null, _samplerClamp, _samplerWrap, new ShaderParams());
                if (!_hasInitialTexture)
                {
                    _hasInitialTexture = true;
                    DrawPass(_fboA.Rtv, AlbumFboSize, AlbumFboSize, _colorPs, tempA.Srv, null, null, _samplerClamp, _samplerWrap, new ShaderParams());
                }
            }
            finally
            {
                tempA.Dispose();
                tempB.Dispose();
                srcSrv.Dispose();
                srcTex.Dispose();
            }
        }

        public void Render(float deltaSeconds, bool useVsync)
        {
            _scaledTime = (_scaledTime + (double)(deltaSeconds * _currentSpeed)) % TimeWrap;
            float speedAlpha = 1.0f - (float)Math.Pow(1.0 - 0.01, deltaSeconds * 60.0f);
            float beatAlpha = 1.0f - (float)Math.Pow(1.0 - _beatLerpAlpha, deltaSeconds * 60.0f);
            _currentSpeed = Lerp(_currentSpeed, _targetSpeed, speedAlpha);
            _currentBeat = Lerp(_currentBeat, _targetBeat, beatAlpha);

            // Pass 1: Copy fboA -> fboC, then crossfade fboC -> fboA toward fboB
            DrawPass(_fboC.Rtv, AlbumFboSize, AlbumFboSize, _colorPs, _fboA.Srv, null, null, _samplerClamp, _samplerWrap, new ShaderParams());

            var fadeParams = new ShaderParams { LerpFactor = 0.0f };
            DrawPass(_fboA.Rtv, AlbumFboSize, AlbumFboSize, _fadePs, _fboC.Srv, _fboB.Srv, null, _samplerClamp, _samplerWrap, fadeParams);

            // Pass 2: Domain-warp fboA -> native screenFbo
            var noiseParams = new ShaderParams
            {
                ResX = _screenFbo.Width,
                ResY = _screenFbo.Height,
                Scale = _noiseScale,
                Border = _currentBeat,
                TimeScaled = (float)_scaledTime,
                PreCompute = _performanceMode ? 1 : 0
            };
            DrawPass(_screenFbo.Rtv, _screenFbo.Width, _screenFbo.Height, _noisePs, _fboA.Srv, null, _randSrv, _samplerMirror, _samplerWrap, noiseParams);

            // Pass 3: Upscale/post-process screenFbo -> DXGI SwapChain backbuffer
            float aspectRatio = (float)_width / (float)_height;
            if (_postProcessingMode == "blur")
            {
                var blurParams = new ShaderParams
                {
                    ResX = Math.Max(1f, _width * 0.5f),
                    ResY = Math.Max(1f, _height * 0.5f),
                    BrightnessOffset = _brightnessOffset,
                    Level = 1f
                };
                DrawPass(_backBufferRtv, _width, _height, _blurPs, _screenFbo.Srv, null, null, _samplerClamp, _samplerWrap, blurParams);
            }
            else if (_postProcessingMode == "flutedGlass")
            {
                var glassParams = new ShaderParams
                {
                    NumSegments = 16.0f * aspectRatio
                };
                DrawPass(_backBufferRtv, _width, _height, _glassPs, _screenFbo.Srv, null, null, _samplerClamp, _samplerWrap, glassParams);
            }
            else
            {
                DrawPass(_backBufferRtv, _width, _height, _colorPs, _screenFbo.Srv, null, null, _samplerClamp, _samplerWrap, new ShaderParams());
            }

            _swapChain.Present(useVsync ? 1u : 0u, PresentFlags.None);
        }

        private void DrawPass(
            ID3D11RenderTargetView rtv,
            int vpWidth,
            int vpHeight,
            ID3D11PixelShader ps,
            ID3D11ShaderResourceView srv0,
            ID3D11ShaderResourceView srv1,
            ID3D11ShaderResourceView srv2,
            ID3D11SamplerState sampler0,
            ID3D11SamplerState sampler1,
            ShaderParams parameters)
        {
            _context.OMSetRenderTargets(rtv);
            _context.RSSetViewport(new Viewport(0, 0, vpWidth, vpHeight));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_fullscreenVs);
            _context.PSSetShader(ps);

            _context.UpdateSubresource(parameters, _constantBuffer);
            _context.PSSetConstantBuffer(0, _constantBuffer);

            _context.PSSetShaderResource(0, srv0);
            _context.PSSetShaderResource(1, srv1);
            _context.PSSetShaderResource(2, srv2);
            _context.PSSetSampler(0, sampler0);
            _context.PSSetSampler(1, sampler1);

            _context.Draw(3, 0);

            // Unbind SRVs so the same resource is never simultaneously bound as input and output.
            _context.PSSetShaderResource(0, null);
            _context.PSSetShaderResource(1, null);
            _context.PSSetShaderResource(2, null);
        }

        private bool TryInitializeDirectComposition(IntPtr hwnd, FeatureLevel[] levels)
        {
            try
            {
                Result hr = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    levels,
                    out _device,
                    out _context
                );
                if (hr.Failure)
                {
                    D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Warp,
                        DeviceCreationFlags.BgraSupport,
                        levels,
                        out _device,
                        out _context
                    ).CheckError();
                }

                using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
                using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
                using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
                {
                    DComp.DCompositionCreateDevice(dxgiDevice, out _dcompDevice).CheckError();
                    _dcompDevice.CreateTargetForHwnd(hwnd, true, out _dcompTarget).CheckError();
                    _dcompDevice.CreateVisual(out _dcompVisual).CheckError();

                    var desc = new SwapChainDescription1
                    {
                        Width = (uint)_width,
                        Height = (uint)_height,
                        Format = Format.B8G8R8A8_UNorm,
                        Stereo = false,
                        SampleDescription = new SampleDescription(1, 0),
                        BufferUsage = Usage.RenderTargetOutput,
                        BufferCount = 2,
                        Scaling = Scaling.Stretch,
                        SwapEffect = SwapEffect.FlipDiscard,
                        AlphaMode = AlphaMode.Ignore
                    };

                    _swapChain = factory.CreateSwapChainForComposition(_device, desc, null);
                    _dcompVisual.SetContent(_swapChain).CheckError();
                    _dcompTarget.SetRoot(_dcompVisual).CheckError();
                    _dcompDevice.Commit().CheckError();
                    _usingDComp = true;

                    AppLogger.Info(string.Format("DX11 DirectComposition initialized adapter='{0}'", adapter.Description.Description));
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("DirectComposition init failed, falling back to HWND SwapChain", ex);
                CleanupSwapChainAndDevice();
                return false;
            }
        }

        private void InitializeHwndSwapChainFallback(IntPtr hwnd, FeatureLevel[] levels)
        {
            var swapDesc = new SwapChainDescription
            {
                BufferCount = 1,
                BufferDescription = new ModeDescription((uint)_width, (uint)_height, Format.B8G8R8A8_UNorm),
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = hwnd,
                SampleDescription = new SampleDescription(1, 0),
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                Flags = SwapChainFlags.None
            };

            Result hr = D3D11.D3D11CreateDeviceAndSwapChain(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                levels,
                swapDesc,
                out _swapChain,
                out _device,
                out _,
                out _context
            );
            if (hr.Failure)
            {
                D3D11.D3D11CreateDeviceAndSwapChain(
                    null,
                    DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    levels,
                    swapDesc,
                    out _swapChain,
                    out _device,
                    out _,
                    out _context
                ).CheckError();
            }
            _usingDComp = false;
            AppLogger.Info("DX11 HWND Discard SwapChain fallback initialized");
        }

        private void CleanupSwapChainAndDevice()
        {
            _usingDComp = false;
            if (_dcompVisual != null) { _dcompVisual.Dispose(); _dcompVisual = null; }
            if (_dcompTarget != null) { _dcompTarget.Dispose(); _dcompTarget = null; }
            if (_dcompDevice != null) { _dcompDevice.Dispose(); _dcompDevice = null; }
            if (_backBufferRtv != null) { _backBufferRtv.Dispose(); _backBufferRtv = null; }
            if (_swapChain != null) { _swapChain.Dispose(); _swapChain = null; }
            if (_context != null) { _context.Dispose(); _context = null; }
            if (_device != null) { _device.Dispose(); _device = null; }
        }

        private void CreateBackBufferRtv()
        {
            using (ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                _backBufferRtv = _device.CreateRenderTargetView(backBuffer);
            }
        }

        private RenderTarget CreateRenderTarget(int width, int height)
        {
            var desc = new Texture2DDescription(
                Format.R16G16B16A16_Float,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.RenderTarget | BindFlags.ShaderResource
            );
            ID3D11Texture2D tex = _device.CreateTexture2D(desc);
            return new RenderTarget
            {
                Texture = tex,
                Rtv = _device.CreateRenderTargetView(tex),
                Srv = _device.CreateShaderResourceView(tex),
                Width = width,
                Height = height
            };
        }

        private void LoadRandTexture()
        {
            string randPath = AssetPaths.RandTexturePath();
            if (!File.Exists(randPath)) return;
            using (var bmp = new Bitmap(randPath))
            {
                CreateTextureFromBitmap(bmp, bmp.Width, bmp.Height, out _randTexture, out _randSrv);
            }
        }

        private bool CreateTextureFromBitmap(Bitmap source, int width, int height, out ID3D11Texture2D tex, out ID3D11ShaderResourceView srv)
        {
            tex = null;
            srv = null;

            using (var prepared = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(prepared))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, 0, 0, width, height);
                }

                BitmapData data = prepared.LockBits(
                    new Rectangle(0, 0, prepared.Width, prepared.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb
                );
                try
                {
                    var desc = new Texture2DDescription(
                        Format.B8G8R8A8_UNorm,
                        (uint)prepared.Width,
                        (uint)prepared.Height,
                        1,
                        1,
                        BindFlags.ShaderResource,
                        ResourceUsage.Immutable
                    );
                    var subres = new SubresourceData(data.Scan0, (uint)data.Stride);
                    tex = _device.CreateTexture2D(desc, new SubresourceData[] { subres });
                    srv = _device.CreateShaderResourceView(tex);
                    return true;
                }
                finally
                {
                    prepared.UnlockBits(data);
                }
            }
        }

        private ID3D11SamplerState CreateSampler(TextureAddressMode addressMode)
        {
            var desc = new SamplerDescription
            {
                Filter = Vortice.Direct3D11.Filter.MinMagMipLinear,
                AddressU = addressMode,
                AddressV = addressMode,
                AddressW = addressMode,
                MaxLOD = float.MaxValue
            };
            return _device.CreateSamplerState(desc);
        }

        private ID3D11VertexShader CompileVertexShader(string hlsl)
        {
            ReadOnlyMemory<byte> bytecode = Compiler.Compile(hlsl, "main", "FullscreenVS.hlsl", "vs_4_0");
            return _device.CreateVertexShader(bytecode.Span);
        }

        private ID3D11PixelShader CompilePixelShader(string hlsl)
        {
            ReadOnlyMemory<byte> bytecode = Compiler.Compile(hlsl, "main", "ShaderPS.hlsl", "ps_4_0");
            return _device.CreatePixelShader(bytecode.Span);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        public void Dispose()
        {
            if (_context != null)
            {
                try { _context.ClearState(); _context.Flush(); } catch { }
            }

            if (_fboA != null) { _fboA.Dispose(); _fboA = null; }
            if (_fboB != null) { _fboB.Dispose(); _fboB = null; }
            if (_fboC != null) { _fboC.Dispose(); _fboC = null; }
            if (_screenFbo != null) { _screenFbo.Dispose(); _screenFbo = null; }
            if (_randSrv != null) { _randSrv.Dispose(); _randSrv = null; }
            if (_randTexture != null) { _randTexture.Dispose(); _randTexture = null; }

            if (_samplerClamp != null) { _samplerClamp.Dispose(); _samplerClamp = null; }
            if (_samplerMirror != null) { _samplerMirror.Dispose(); _samplerMirror = null; }
            if (_samplerWrap != null) { _samplerWrap.Dispose(); _samplerWrap = null; }

            if (_constantBuffer != null) { _constantBuffer.Dispose(); _constantBuffer = null; }
            if (_fullscreenVs != null) { _fullscreenVs.Dispose(); _fullscreenVs = null; }
            if (_colorPs != null) { _colorPs.Dispose(); _colorPs = null; }
            if (_blurPs != null) { _blurPs.Dispose(); _blurPs = null; }
            if (_fadePs != null) { _fadePs.Dispose(); _fadePs = null; }
            if (_noisePs != null) { _noisePs.Dispose(); _noisePs = null; }
            if (_glassPs != null) { _glassPs.Dispose(); _glassPs = null; }

            CleanupSwapChainAndDevice();
        }
    }
}
