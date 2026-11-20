using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiffuseWindows
{
    internal sealed class WasapiAudioMonitor : IDisposable
    {
        private const int FftSize = 1024;
        private const int AudclntStreamFlagsLoopback = 0x00020000;
        private const int AudclntBufferFlagsSilent = 0x00000002;
        private const int ClsctxAll = 0x17;
        private const ushort WaveFormatPcm = 1;
        private const ushort WaveFormatIeeeFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;

        private static readonly Guid ClsidMmDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IidIAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IidIAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        private static readonly Guid SubFormatIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubFormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
        private enum AudioClientShareMode { Shared = 0, Exclusive = 1 }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(AudioClientShareMode ShareMode, int StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
            int GetBufferSize(out uint pNumBufferFrames);
            int GetStreamLatency(out long phnsLatency);
            int GetCurrentPadding(out uint pNumPaddingFrames);
            int IsFormatSupported(AudioClientShareMode ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
            int GetMixFormat(out IntPtr ppDeviceFormat);
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
            int ReleaseBuffer(uint NumFramesRead);
            int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }

        private readonly Action<double[]> _onBands;
        private readonly Action<float, float> _onBpm;
        private readonly BpmDetector _bpmDetector = new BpmDetector();
        private readonly float[] _ringBuffer = new float[FftSize];
        private readonly double[] _fftReal = new double[FftSize];
        private readonly double[] _fftImag = new double[FftSize];
        private readonly double[] _hann = new double[FftSize];
        private readonly double[] _magnitudes = new double[FftSize / 2];

        private int _ringPos;
        private Thread _worker;
        private volatile bool _running;
        private volatile bool _enabled;

        public WasapiAudioMonitor(Action<double[]> onBands, Action<float, float> onBpm = null)
        {
            _onBands = onBands;
            _onBpm = onBpm;
            for (int i = 0; i < FftSize; i++)
            {
                _hann[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;

            if (enabled)
            {
                StartWorker();
            }
            else
            {
                StopWorker();
                _bpmDetector.Reset();
                _onBands(new double[4]);
                _onBpm?.Invoke(0f, 0f);
            }
        }

        private void StartWorker()
        {
            if (_running) return;
            _running = true;
            _worker = new Thread(CaptureLoop);
            _worker.IsBackground = true;
            _worker.Name = "DiffuseWasapiLoopback";
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        private void StopWorker()
        {
            _running = false;
            if (_worker != null && _worker.IsAlive)
            {
                _worker.Join(500);
            }
            _worker = null;
        }

        private void CaptureLoop()
        {
            while (_running && _enabled)
            {
                IMMDeviceEnumerator enumerator = null;
                IMMDevice device = null;
                IAudioClient audioClient = null;
                IAudioCaptureClient captureClient = null;
                IntPtr mixFormatPtr = IntPtr.Zero;

                try
                {
                    Type enumType = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator);
                    enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);

                    int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
                    if (hr != 0 || device == null)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }

                    Guid iidClient = IidIAudioClient;
                    object clientObj;
                    hr = device.Activate(ref iidClient, ClsctxAll, IntPtr.Zero, out clientObj);
                    if (hr != 0 || clientObj == null)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }
                    audioClient = (IAudioClient)clientObj;

                    hr = audioClient.GetMixFormat(out mixFormatPtr);
                    if (hr != 0 || mixFormatPtr == IntPtr.Zero)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }

                    var fmt = (WAVEFORMATEX)Marshal.PtrToStructure(mixFormatPtr, typeof(WAVEFORMATEX));
                    bool isFloat = IsIeeeFloat(mixFormatPtr, fmt);
                    bool isPcm16 = !isFloat && fmt.wBitsPerSample == 16;

                    // 100ms buffer in 100-ns units
                    hr = audioClient.Initialize(
                        AudioClientShareMode.Shared,
                        AudclntStreamFlagsLoopback,
                        1000000,
                        0,
                        mixFormatPtr,
                        IntPtr.Zero
                    );
                    if (hr != 0)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }

                    Guid iidCapture = IidIAudioCaptureClient;
                    object capObj;
                    hr = audioClient.GetService(ref iidCapture, out capObj);
                    if (hr != 0 || capObj == null)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }
                    captureClient = (IAudioCaptureClient)capObj;

                    audioClient.Start();

                    int sampleRate = (int)fmt.nSamplesPerSec;
                    int channels = Math.Max(1, (int)fmt.nChannels);
                    int blockAlign = Math.Max(1, (int)fmt.nBlockAlign);
                    Stopwatch clock = Stopwatch.StartNew();

                    while (_running && _enabled)
                    {
                        uint packetLength;
                        hr = captureClient.GetNextPacketSize(out packetLength);
                        if (hr != 0) break;

                        if (packetLength == 0)
                        {
                            Thread.Sleep(12);
                            continue;
                        }

                        bool gotSamples = false;
                        while (packetLength > 0 && _running && _enabled)
                        {
                            IntPtr dataPtr;
                            uint numFrames;
                            uint flags;
                            ulong devPos;
                            ulong qpcPos;

                            hr = captureClient.GetBuffer(out dataPtr, out numFrames, out flags, out devPos, out qpcPos);
                            if (hr != 0) break;

                            try
                            {
                                bool silent = (flags & AudclntBufferFlagsSilent) != 0 || dataPtr == IntPtr.Zero;
                                if (silent)
                                {
                                    for (uint f = 0; f < numFrames; f++)
                                    {
                                        PushSample(0f);
                                    }
                                }
                                else if (isFloat)
                                {
                                    int floatCount = (int)numFrames * channels;
                                    float[] buf = new float[floatCount];
                                    Marshal.Copy(dataPtr, buf, 0, floatCount);
                                    for (int f = 0; f < (int)numFrames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = f * channels;
                                        for (int c = 0; c < channels; c++) sum += buf[baseIdx + c];
                                        PushSample(sum / channels);
                                    }
                                }
                                else if (isPcm16)
                                {
                                    int shortCount = (int)numFrames * channels;
                                    short[] buf = new short[shortCount];
                                    Marshal.Copy(dataPtr, buf, 0, shortCount);
                                    for (int f = 0; f < (int)numFrames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = f * channels;
                                        for (int c = 0; c < channels; c++) sum += buf[baseIdx + c] / 32768f;
                                        PushSample(sum / channels);
                                    }
                                }
                                gotSamples = true;
                            }
                            finally
                            {
                                captureClient.ReleaseBuffer(numFrames);
                            }

                            hr = captureClient.GetNextPacketSize(out packetLength);
                            if (hr != 0) break;
                        }

                        if (gotSamples)
                        {
                            double[] bands = ComputeBands(sampleRate);
                            BpmResult bpm = _bpmDetector.Update(bands, clock.Elapsed.TotalSeconds);
                            _onBands(bands);
                            _onBpm?.Invoke(bpm.Bpm, bpm.Confidence);
                        }
                    }

                    audioClient.Stop();
                }
                catch
                {
                    Thread.Sleep(1500);
                }
                finally
                {
                    if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
                    if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                    if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                    if (device != null) Marshal.ReleaseComObject(device);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                }
            }
        }

        private void PushSample(float s)
        {
            _ringBuffer[_ringPos] = s;
            _ringPos = (_ringPos + 1) % FftSize;
        }

        private double[] ComputeBands(int sampleRate)
        {
            for (int i = 0; i < FftSize; i++)
            {
                int idx = (_ringPos + i) % FftSize;
                _fftReal[i] = _ringBuffer[idx] * _hann[i];
                _fftImag[i] = 0.0;
            }

            FftInPlace(_fftReal, _fftImag);

            for (int i = 0; i < _magnitudes.Length; i++)
            {
                double re = _fftReal[i];
                double im = _fftImag[i];
                _magnitudes[i] = Math.Sqrt(re * re + im * im) / (FftSize * 0.25);
            }

            double[] bands = new double[4];
            bands[0] = BandEnergy(60f, 250f, sampleRate);
            bands[1] = BandEnergy(250f, 500f, sampleRate);
            bands[2] = BandEnergy(500f, 2000f, sampleRate);
            bands[3] = BandEnergy(2000f, 16000f, sampleRate);
            return bands;
        }

        private double BandEnergy(float lowHz, float highHz, int sampleRate)
        {
            int lowBin = Math.Max(1, Math.Min((int)(lowHz * FftSize / sampleRate), _magnitudes.Length - 1));
            int highBin = Math.Max(lowBin, Math.Min((int)(highHz * FftSize / sampleRate), _magnitudes.Length - 1));
            double sum = 0.0;
            int count = 0;
            for (int i = lowBin; i <= highBin; i++)
            {
                sum += _magnitudes[i];
                count++;
            }
            double mean = count > 0 ? sum / count : 0.0;
            return Math.Min(4.0, mean * mean * 16.0);
        }

        private static void FftInPlace(double[] real, double[] imag)
        {
            int n = real.Length;
            int j = 0;
            for (int i = 0; i < n; i++)
            {
                if (i < j)
                {
                    double tr = real[i]; real[i] = real[j]; real[j] = tr;
                    double ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
                int m = n >> 1;
                while (m >= 1 && j >= m)
                {
                    j -= m;
                    m >>= 1;
                }
                j += m;
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wlenR = Math.Cos(ang);
                double wlenI = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double wR = 1.0;
                    double wI = 0.0;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        double uR = real[i + k];
                        double uI = imag[i + k];
                        double vR = real[i + k + half] * wR - imag[i + k + half] * wI;
                        double vI = real[i + k + half] * wI + imag[i + k + half] * wR;

                        real[i + k] = uR + vR;
                        imag[i + k] = uI + vI;
                        real[i + k + half] = uR - vR;
                        imag[i + k + half] = uI - vI;

                        double nextWR = wR * wlenR - wI * wlenI;
                        wI = wR * wlenI + wI * wlenR;
                        wR = nextWR;
                    }
                }
            }
        }

        private static bool IsIeeeFloat(IntPtr formatPtr, WAVEFORMATEX fmt)
        {
            if (fmt.wFormatTag == WaveFormatIeeeFloat) return true;
            if (fmt.wFormatTag == WaveFormatPcm) return false;
            if (fmt.wFormatTag == WaveFormatExtensible && fmt.cbSize >= 22)
            {
                IntPtr subFormatPtr = new IntPtr(formatPtr.ToInt64() + Marshal.SizeOf(typeof(WAVEFORMATEX)) + 6);
                var subFormat = (Guid)Marshal.PtrToStructure(subFormatPtr, typeof(Guid));
                if (subFormat == SubFormatIeeeFloat) return true;
                if (subFormat == SubFormatPcm) return false;
            }
            return fmt.wBitsPerSample == 32;
        }

        public void Dispose()
        {
            SetEnabled(false);
        }
    }
}
