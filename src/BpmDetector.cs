using System;

namespace DiffuseWindows
{
    internal readonly struct BpmResult
    {
        public readonly float Bpm;
        public readonly float Confidence;

        public BpmResult(float bpm, float confidence)
        {
            Bpm = bpm;
            Confidence = confidence;
        }
    }

    /// <summary>
    /// Real-time tempo tracker using multi-band spectral flux and resampled autocorrelation
    /// with a 120 BPM perceptual prior and parabolic sub-sample lag refinement.
    /// </summary>
    internal sealed class BpmDetector
    {
        private const double GridRateHz = 40.0;
        private const double WindowSeconds = 6.0;
        private const double MinSpanSeconds = 2.5;
        private const double EstimateIntervalSec = 0.25;
        private const double SilenceTimeoutSec = 1.5;
        private const double MinBpm = 60.0;
        private const double MaxBpm = 180.0;
        private const int MaxSamples = 512;
        private const int GridSize = 240; // WindowSeconds * GridRateHz

        private readonly double[] _times = new double[MaxSamples];
        private readonly double[] _onsets = new double[MaxSamples];
        private readonly double[] _grid = new double[GridSize];
        private readonly double[] _prevBands = new double[4];

        private int _head;
        private int _count;
        private double _avgFlux;
        private double _lastActiveSec;
        private double _lastEstimateSec;
        private double _lastSampleSec;
        private float _smoothedBpm;
        private float _confidence;

        public void Reset()
        {
            _head = 0;
            _count = 0;
            _avgFlux = 0.0;
            _lastActiveSec = 0.0;
            _lastEstimateSec = 0.0;
            _lastSampleSec = 0.0;
            _smoothedBpm = 0.0f;
            _confidence = 0.0f;
            Array.Clear(_prevBands, 0, _prevBands.Length);
        }

        public BpmResult Update(double[] bands, double nowSec)
        {
            if (bands == null || bands.Length < 4)
            {
                return new BpmResult(_smoothedBpm, _confidence);
            }

            double dt = _lastSampleSec > 0.0 ? Math.Max(0.0, Math.Min(0.25, nowSec - _lastSampleSec)) : 0.02;
            _lastSampleSec = nowSec;

            double totalEnergy = bands[0] + bands[1] + bands[2] + bands[3];
            double f0 = Math.Max(0.0, bands[0] - _prevBands[0]);
            double f1 = Math.Max(0.0, bands[1] - _prevBands[1]);
            double f2 = Math.Max(0.0, bands[2] - _prevBands[2]);
            double flux = f0 * 1.4 + f1 * 0.9 + f2 * 0.35;

            _prevBands[0] = bands[0];
            _prevBands[1] = bands[1];
            _prevBands[2] = bands[2];
            _prevBands[3] = bands[3];

            if (totalEnergy > 1e-4)
            {
                _lastActiveSec = nowSec;
            }
            else if (_lastActiveSec > 0.0 && (nowSec - _lastActiveSec) > SilenceTimeoutSec)
            {
                _confidence = Math.Max(0.0f, _confidence - (float)(dt * 0.8));
                if (_confidence < 0.05f)
                {
                    _smoothedBpm = 0.0f;
                    _confidence = 0.0f;
                }
                return new BpmResult(_smoothedBpm, _confidence);
            }

            _avgFlux += 0.08 * (flux - _avgFlux);
            double onset = Math.Max(0.0, flux - _avgFlux * 1.12);

            _times[_head] = nowSec;
            _onsets[_head] = onset;
            _head = (_head + 1) % MaxSamples;
            if (_count < MaxSamples) _count++;

            if ((nowSec - _lastEstimateSec) >= EstimateIntervalSec)
            {
                _lastEstimateSec = nowSec;
                EstimateTempo(nowSec);
            }

            return new BpmResult(_smoothedBpm, _confidence);
        }

        private void EstimateTempo(double nowSec)
        {
            if (_count < 32) return;

            int oldestIdx = (_head - _count + MaxSamples) % MaxSamples;
            double oldestTime = _times[oldestIdx];
            double span = nowSec - oldestTime;
            if (span < MinSpanSeconds) return;

            double window = Math.Min(WindowSeconds, span);
            int n = Math.Min(GridSize, (int)Math.Round(window * GridRateHz));
            if (n < 80) return;

            double startTime = nowSec - (n - 1) / GridRateHz;
            int cursor = oldestIdx;
            int visited = 0;

            double mean = 0.0;
            for (int g = 0; g < n; g++)
            {
                double t = startTime + g / GridRateHz;
                while (visited + 1 < _count)
                {
                    int nextIdx = (cursor + 1) % MaxSamples;
                    if (_times[nextIdx] > t) break;
                    cursor = nextIdx;
                    visited++;
                }

                double val;
                if (visited + 1 >= _count)
                {
                    val = _onsets[cursor];
                }
                else
                {
                    int nextIdx = (cursor + 1) % MaxSamples;
                    double t0 = _times[cursor];
                    double t1 = _times[nextIdx];
                    double alpha = (t1 > t0) ? Math.Max(0.0, Math.Min(1.0, (t - t0) / (t1 - t0))) : 0.0;
                    val = _onsets[cursor] + (_onsets[nextIdx] - _onsets[cursor]) * alpha;
                }

                _grid[g] = val;
                mean += val;
            }
            mean /= n;

            double variance = 0.0;
            for (int g = 0; g < n; g++)
            {
                _grid[g] -= mean;
                variance += _grid[g] * _grid[g];
            }
            if (variance < 1e-6)
            {
                _confidence = Math.Max(0.0f, _confidence - 0.1f);
                if (_confidence < 0.05f) _smoothedBpm = 0.0f;
                return;
            }

            int lagMin = Math.Max(1, (int)Math.Floor(GridRateHz * 60.0 / MaxBpm));
            int lagMax = Math.Min(n / 2 - 1, (int)Math.Ceiling(GridRateHz * 60.0 / MinBpm));
            if (lagMax <= lagMin + 2) return;

            double[] corr = new double[lagMax + 2];
            int bestLag = lagMin;
            double bestScore = double.NegativeInfinity;

            for (int lag = lagMin - 1; lag <= lagMax + 1; lag++)
            {
                if (lag < 1 || lag >= n / 2) continue;
                double num = 0.0;
                double d1 = 0.0;
                double d2 = 0.0;
                int limit = n - lag;
                for (int i = 0; i < limit; i++)
                {
                    double a = _grid[i];
                    double b = _grid[i + lag];
                    num += a * b;
                    d1 += a * a;
                    d2 += b * b;
                }
                double denom = Math.Sqrt(d1 * d2);
                double r = denom > 1e-9 ? num / denom : 0.0;
                corr[lag] = r;

                if (lag >= lagMin && lag <= lagMax)
                {
                    double bpm = 60.0 * GridRateHz / lag;
                    double oct = Math.Log(bpm / 120.0, 2.0);
                    double prior = Math.Exp(-0.5 * (oct / 0.55) * (oct / 0.55));
                    double score = r * (0.75 + 0.25 * prior);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestLag = lag;
                    }
                }
            }

            double refinedLag = bestLag;
            if (bestLag > lagMin && bestLag < lagMax)
            {
                double y0 = corr[bestLag - 1];
                double y1 = corr[bestLag];
                double y2 = corr[bestLag + 1];
                double denom = y0 - 2.0 * y1 + y2;
                if (Math.Abs(denom) > 1e-6)
                {
                    double delta = 0.5 * (y0 - y2) / denom;
                    refinedLag += Math.Max(-0.5, Math.Min(0.5, delta));
                }
            }

            float candBpm = (float)(60.0 * GridRateHz / refinedLag);
            float candConf = (float)Math.Max(0.0, Math.Min(1.0, (corr[bestLag] - 0.12) / 0.38));

            if (_smoothedBpm <= 0.0f)
            {
                if (candConf >= 0.30f)
                {
                    _smoothedBpm = candBpm;
                }
            }
            else if (candConf >= 0.22f)
            {
                float relDiff = Math.Abs(candBpm - _smoothedBpm) / _smoothedBpm;
                if (relDiff < 0.16f)
                {
                    _smoothedBpm += 0.22f * (candBpm - _smoothedBpm);
                }
                else if (candConf >= 0.52f)
                {
                    _smoothedBpm += 0.35f * (candBpm - _smoothedBpm);
                }
            }

            _confidence += 0.25f * (candConf - _confidence);
            if (_confidence < 0.08f)
            {
                _smoothedBpm = 0.0f;
            }
        }
    }
}
