using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiffuseWindows
{
    internal sealed class OnlineBpmResult
    {
        public float Bpm { get; }
        public string Source { get; }

        public OnlineBpmResult(float bpm, string source)
        {
            Bpm = bpm;
            Source = source;
        }
    }

    /// <summary>
    /// Resolves track tempo online via Cider local playback RPC, Apple Music (iTunes Search)
    /// metadata normalization, Deezer's public track/ISRC API, and ReccoBeats preview analysis fallback.
    /// </summary>
    internal sealed class OnlineBpmService : IDisposable
    {
        private static readonly Regex ParenNoise = new Regex(@"\s*[\(\[].*?[\)\]]", RegexOptions.Compiled);
        private static readonly Regex FeatNoise = new Regex(@"\s+(feat\.?|ft\.?)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N}\s]+", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly HttpClient _ciderHttp;
        private readonly ConcurrentDictionary<string, OnlineBpmResult> _cache = new ConcurrentDictionary<string, OnlineBpmResult>(StringComparer.OrdinalIgnoreCase);

        public OnlineBpmService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiffuseWindows/1.0");
            _ciderHttp = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
        }

        public async Task<OnlineBpmResult> LookupAsync(string title, string artist, string sourceApp, CancellationToken ct)
        {
            string rawTitle = title ?? "";
            string rawArtist = artist ?? "";
            string cacheKey = NormalizeKey(rawArtist) + "|" + NormalizeKey(rawTitle);
            if (string.IsNullOrWhiteSpace(rawTitle)) return null;

            OnlineBpmResult cached;
            if (_cache.TryGetValue(cacheKey, out cached))
            {
                return cached != null && cached.Bpm > 20f ? cached : null;
            }

            OnlineBpmResult found = await ResolveUncachedAsync(rawTitle, rawArtist, sourceApp ?? "", ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                _cache[cacheKey] = found ?? new OnlineBpmResult(0f, "");
            }
            return found != null && found.Bpm > 20f ? found : null;
        }

        private async Task<OnlineBpmResult> ResolveUncachedAsync(string title, string artist, string sourceApp, CancellationToken ct)
        {
            string lookupTitle = CleanTrackText(title);
            string lookupArtist = CleanTrackText(artist);
            string isrc = null;
            bool usedCider = false;
            bool usedAppleMusic = false;

            if (sourceApp.IndexOf("cider", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cider = await TryQueryCiderAsync(ct).ConfigureAwait(false);
                if (cider != null)
                {
                    usedCider = true;
                    if (!string.IsNullOrEmpty(cider.Title)) lookupTitle = CleanTrackText(cider.Title);
                    if (!string.IsNullOrEmpty(cider.Artist)) lookupArtist = CleanTrackText(cider.Artist);
                    isrc = cider.Isrc;
                }
            }

            if (!string.IsNullOrEmpty(isrc))
            {
                var byIsrc = await TryDeezerByIsrcAsync(isrc, ct).ConfigureAwait(false);
                if (byIsrc != null && byIsrc.Bpm > 20f)
                {
                    return new OnlineBpmResult(byIsrc.Bpm, usedCider ? "Cider + Deezer" : "Deezer ISRC");
                }
            }

            // Canonicalize via Apple Music / iTunes Search when metadata is noisy or incomplete.
            var itunes = await TryItunesSearchAsync(lookupArtist, lookupTitle, ct).ConfigureAwait(false);
            if (itunes != null && !string.IsNullOrEmpty(itunes.Title))
            {
                usedAppleMusic = true;
            }

            var deezer = await TryDeezerSearchAsync(lookupArtist, lookupTitle, ct).ConfigureAwait(false);
            if ((deezer == null || deezer.Bpm <= 20f) && itunes != null)
            {
                string itArtist = CleanTrackText(itunes.Artist);
                string itTitle = CleanTrackText(itunes.Title);
                if (!string.Equals(itTitle, lookupTitle, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(itArtist, lookupArtist, StringComparison.OrdinalIgnoreCase))
                {
                    var retry = await TryDeezerSearchAsync(itArtist, itTitle, ct).ConfigureAwait(false);
                    if (retry != null) deezer = retry;
                }
            }

            if (deezer != null && deezer.Bpm > 20f)
            {
                string src = usedCider ? "Cider + Deezer" : (usedAppleMusic ? "Apple Music + Deezer" : "Deezer");
                return new OnlineBpmResult(deezer.Bpm, src);
            }

            string previewMp3 = deezer != null ? deezer.PreviewUrl : null;
            if (!string.IsNullOrEmpty(previewMp3))
            {
                float tempo = await TryReccoBeatsPreviewAsync(previewMp3, ct).ConfigureAwait(false);
                if (tempo > 20f)
                {
                    string src = usedCider ? "Cider + ReccoBeats" : (usedAppleMusic ? "Apple Music + ReccoBeats" : "Online Audio");
                    return new OnlineBpmResult(tempo, src);
                }
            }

            return null;
        }

        private async Task<CiderTrackInfo> TryQueryCiderAsync(CancellationToken ct)
        {
            try
            {
                string json = await _ciderHttp.GetStringAsync("http://127.0.0.1:10767/api/v1/playback/now-playing", ct).ConfigureAwait(false);
                var doc = Deserialize<CiderNowPlayingResponse>(json);
                return doc != null ? doc.Info : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<DeezerTrackCandidate> TryDeezerByIsrcAsync(string isrc, CancellationToken ct)
        {
            try
            {
                string json = await _http.GetStringAsync("https://api.deezer.com/track/isrc:" + Uri.EscapeDataString(isrc), ct).ConfigureAwait(false);
                var track = Deserialize<DeezerTrackResponse>(json);
                if (track != null && track.Id > 0)
                {
                    return new DeezerTrackCandidate { Id = track.Id, Bpm = track.Bpm, PreviewUrl = track.Preview };
                }
            }
            catch { }
            return null;
        }

        private async Task<ItunesResult> TryItunesSearchAsync(string artist, string title, CancellationToken ct)
        {
            try
            {
                string term = string.IsNullOrEmpty(artist) ? title : (artist + " " + title);
                string url = "https://itunes.apple.com/search?entity=song&limit=5&term=" + Uri.EscapeDataString(term);
                string json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var resp = Deserialize<ItunesSearchResponse>(json);
                if (resp == null || resp.Results == null) return null;

                foreach (var r in resp.Results)
                {
                    if (IsTrackMatch(artist, title, r.ArtistName, r.TrackName))
                    {
                        return new ItunesResult { Artist = r.ArtistName, Title = r.TrackName };
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task<DeezerTrackCandidate> TryDeezerSearchAsync(string artist, string title, CancellationToken ct)
        {
            try
            {
                string term = string.IsNullOrEmpty(artist) ? title : (artist + " " + title);
                string url = "https://api.deezer.com/search?limit=5&q=" + Uri.EscapeDataString(term);
                string json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var resp = Deserialize<DeezerSearchResponse>(json);
                if (resp == null || resp.Data == null || resp.Data.Length == 0) return null;

                DeezerSearchItem match = null;
                foreach (var item in resp.Data)
                {
                    string itemArtist = item.Artist != null ? item.Artist.Name : "";
                    if (IsTrackMatch(artist, title, itemArtist, item.Title))
                    {
                        match = item;
                        break;
                    }
                }
                if (match == null) return null;

                string trackJson = await _http.GetStringAsync("https://api.deezer.com/track/" + match.Id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                var track = Deserialize<DeezerTrackResponse>(trackJson);
                if (track == null) return null;
                return new DeezerTrackCandidate
                {
                    Id = track.Id,
                    Bpm = track.Bpm,
                    PreviewUrl = !string.IsNullOrEmpty(track.Preview) ? track.Preview : match.Preview
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<float> TryReccoBeatsPreviewAsync(string previewUrl, CancellationToken ct)
        {
            try
            {
                byte[] mp3 = await _http.GetByteArrayAsync(previewUrl, ct).ConfigureAwait(false);
                if (mp3 == null || mp3.Length < 1024 || mp3.Length > 4_800_000) return 0f;

                using (var form = new MultipartFormDataContent())
                using (var content = new ByteArrayContent(mp3))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                    form.Add(content, "audioFile", "preview.mp3");
                    using (var resp = await _http.PostAsync("https://api.reccobeats.com/v1/analysis/audio-features", form, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return 0f;
                        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var parsed = Deserialize<ReccoAudioFeaturesResponse>(json);
                        return parsed != null && parsed.Tempo > 20f && parsed.Tempo <= 250f ? parsed.Tempo : 0f;
                    }
                }
            }
            catch
            {
                return 0f;
            }
        }

        private static bool IsTrackMatch(string expectedArtist, string expectedTitle, string candArtist, string candTitle)
        {
            string normExpTitle = NormalizeKey(CleanTrackText(expectedTitle));
            string normCandTitle = NormalizeKey(CleanTrackText(candTitle));
            if (string.IsNullOrEmpty(normExpTitle) || string.IsNullOrEmpty(normCandTitle)) return false;
            if (!normCandTitle.Contains(normExpTitle) && !normExpTitle.Contains(normCandTitle)) return false;

            string normExpArtist = NormalizeKey(CleanTrackText(expectedArtist));
            if (string.IsNullOrEmpty(normExpArtist)) return true;
            string normCandArtist = NormalizeKey(CleanTrackText(candArtist));
            return !string.IsNullOrEmpty(normCandArtist) &&
                   (normCandArtist.Contains(normExpArtist) || normExpArtist.Contains(normCandArtist));
        }

        private static string CleanTrackText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = ParenNoise.Replace(text, "");
            s = FeatNoise.Replace(s, "");
            return s.Trim();
        }

        private static string NormalizeKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = NonWord.Replace(text.ToLowerInvariant(), " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return ser.ReadObject(ms) as T;
            }
        }

        public void Dispose()
        {
            _http.Dispose();
            _ciderHttp.Dispose();
        }

        private sealed class DeezerTrackCandidate
        {
            public long Id;
            public float Bpm;
            public string PreviewUrl;
        }

        private sealed class ItunesResult
        {
            public string Artist;
            public string Title;
        }

        [DataContract]
        private sealed class CiderNowPlayingResponse
        {
            [DataMember(Name = "info")] public CiderTrackInfo Info { get; set; }
        }

        [DataContract]
        private sealed class CiderTrackInfo
        {
            [DataMember(Name = "name")] public string Title { get; set; }
            [DataMember(Name = "artistName")] public string Artist { get; set; }
            [DataMember(Name = "isrc")] public string Isrc { get; set; }
        }

        [DataContract]
        private sealed class ItunesSearchResponse
        {
            [DataMember(Name = "results")] public ItunesTrack[] Results { get; set; }
        }

        [DataContract]
        private sealed class ItunesTrack
        {
            [DataMember(Name = "trackName")] public string TrackName { get; set; }
            [DataMember(Name = "artistName")] public string ArtistName { get; set; }
        }

        [DataContract]
        private sealed class DeezerSearchResponse
        {
            [DataMember(Name = "data")] public DeezerSearchItem[] Data { get; set; }
        }

        [DataContract]
        private sealed class DeezerSearchItem
        {
            [DataMember(Name = "id")] public long Id { get; set; }
            [DataMember(Name = "title")] public string Title { get; set; }
            [DataMember(Name = "preview")] public string Preview { get; set; }
            [DataMember(Name = "artist")] public DeezerArtist Artist { get; set; }
        }

        [DataContract]
        private sealed class DeezerArtist
        {
            [DataMember(Name = "name")] public string Name { get; set; }
        }

        [DataContract]
        private sealed class DeezerTrackResponse
        {
            [DataMember(Name = "id")] public long Id { get; set; }
            [DataMember(Name = "bpm")] public float Bpm { get; set; }
            [DataMember(Name = "preview")] public string Preview { get; set; }
        }

        [DataContract]
        private sealed class ReccoAudioFeaturesResponse
        {
            [DataMember(Name = "tempo")] public float Tempo { get; set; }
        }
    }
}
