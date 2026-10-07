using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Models;
using BatPlayer.Services.SoundCloud;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.Services;

/// <summary>
/// Wave candidate (all candidates currently come from the Yandex graph; the source
/// is kept in the model in case of future pools from other services).
/// </summary>
public sealed class WaveItem
{
    public required string Source { get; init; }
    /// <summary>Platform identifier: ym_id / vk_id / sc_id; unused for local.</summary>
    public required string PlatformId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Cover template with "%%" (YM) or a ready URL (VK/SC); null — no cover.</summary>
    public string? CoverUri { get; init; }
    public bool Available { get; init; } = true;
    /// <summary>For local — the file path; for VK/SC — an already downloaded cover; null by default.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Suggestion journal key (PK wave_suggested): for YM — ym_id,
    /// otherwise a source prefix so ids of different platforms do not collide.</summary>
    public string JournalId => Source == Track.SourceYandex
        ? PlatformId
        : $"{Source}:{PlatformId}";
}

/// <summary>
/// "My wave": local taste-based recommendation generation — no server. Analysis is
/// based on the ARTISTS and tracks the user actually listens to:
///
///   A. "Tracks of listened artists" — top of play_log → /artists/{id}/tracks:
///      tracks by artists I listen to but have not added yet (the heaviest pool).
///   B. "Shared scene" — /artists/{id}/similar for top artists: similar artists of
///      the same scene, their tracks via the same endpoint.
///   C. "Scene radio" — /rotor/station/artist:{id}/tracks: a batch of similar sound
///      from the Yandex rotor, refreshed every generation.
///   D. "Similar tracks" — /tracks/{id}/similar over seeds from the libraries
///      (YM + VK/SC/local matched to ym_id via /search with a cache in wave_seed_map).
///
/// Pools are merged with deduplication (familiar artists win), filtered against
/// already owned/recently played/recently suggested material, and ranked: pool
/// weight + artist affinity from play_log + jitter. Everything is cached (14 days),
/// so repeat generations barely touch the network.
///
/// Feedback without player hooks: suggestions are remembered (wave_suggested); a
/// track played after being suggested gets suggested again, while unplayed
/// suggestions stay out of the wave for a week — a passive negative signal.
/// </summary>
public sealed class RecommendationService
{
    /// <summary>How many seeds participate in pool D (they drive the /similar requests).
    /// Each seed is a path to neighboring artists: the more of them, the wider the circle.</summary>
    public const int SeedCount = 20;

    /// <summary>Mix size (queue length). The queue is always full length: if there are
    /// fewer familiar candidates, the tail is filled with novelty (ranked last) and
    /// familiar material is expanded via deep catalogs.</summary>
    public const int WaveSize = 20;

    /// <summary>Per-artist limit for a "light" artist in the output — otherwise the wave
    /// degenerates into an album. For "heavy" ones (seed weight ≥ HeavyCapShare of the
    /// max, i.e. artists from the play_log top) HeavyPerArtist applies: familiar
    /// material takes most of the wave, novelty is a dosed remainder.
    /// The limits work in the first two selection passes; if even the relaxed limits
    /// cannot fill the queue to WaveSize, the final pass backfills without limits —
    /// a short mix is worse than a degenerate one.</summary>
    public const int LightPerArtist = 1;
    public const int HeavyPerArtist = 2;
    internal const double HeavyCapShare = 0.55;

    /// <summary>Extra slots per artist in the backfill pass: the mix tail is filled with
    /// a relaxed (not removed) limit. +1 keeps a family within 2 tracks for "light"
    /// / 3 for "heavy" over the whole mix — streaks of one artist, even when spread
    /// out, read as clutter. The no-limits final pass only kicks in if even this
    /// cannot reach WaveSize with a very thin pool.</summary>
    internal const int BackfillExtraPerArtist = 1;

    /// <summary>How many top play_log artists are analyzed per generation.</summary>
    public const int MaxTopArtists = 20;

    /// <summary>Minimum organic plays (raw, no discount) for an artist to count as
    /// "listened" and enter the catalog analysis (pool A):
    /// 3-4 passive plays out of a mix are not "taste".</summary>
    public const double MinTopArtistPlays = 4.0;

    /// <summary>Threshold of "strong taste" (raw plays, no discount): families above it
    /// pass the audience gate without a check — niche favorites stay. Below — a "weak
    /// signal" (a couple of background plays, click-tested in the player): a counter
    /// ≥ MinArtistListeners on the artist card is required, otherwise it gets no
    /// place in the mix.</summary>
    public const double StrongTastePlays = 10.0;

    /// <summary>Minimum artist audience (YM brief-info) for a weak signal and novelty
    /// backfill: no-names and "AI tracks" stay out of the mix, even in the tail.</summary>
    public const int MinArtistListeners = 10_000;

    /// <summary>Minimum track plays for SoundCloud related-candidates.</summary>
    public const int MinTrackPlays = 10_000;

    /// <summary>Budget of audience checks per generation (search + brief-info per family);
    /// results are cached in wave_seed_map forever, the budget is spent once.
    /// 60 — a wide circle needs a fast warm-up: every verified ≥10k artist forever
    /// widens the rotation, unverified material does not enter the mix.</summary>
    public const int MaxListenerResolutions = 60;


    /// <summary>How many "scene artists" (similar artists) join the wave.
    /// Each scene is a new family in the mix: the more of them, the wider the circle.
    /// Candidates are re-sampled every generation and their catalogs are cached —
    /// the audience-verified circle grows from generation to generation.</summary>
    public const int MaxSceneArtists = 24;

    /// <summary>How many similar artists are taken from each top artist before scene selection.</summary>
    public const int ScenePerArtist = 3;

    /// <summary>For how many top artists a rotor radio batch is requested.
    /// The rotor is the only source of FRESH material each generation (not cached),
    /// so its quota is the main protection of the pool against cooldown depletion.</summary>
    public const int MaxRadioArtists = 15;

    /// <summary>How many tracks are requested from one artist (/artists/{id}/tracks).</summary>
    public const int ArtistPageSize = 50;

    /// <summary>Pause between artist catalog fetches, ms: fetching two pages across all
    /// seeds back-to-back hits the Yandex rate limit and dropped part of the catalogs —
    /// the pool was then assembled from a couple of surviving families.</summary>
    public const int ArtistCatalogFetchDelayMs = 250;

    /// <summary>Budget of /search calls per generation: seed and artist-name matching
    /// must not suddenly fire dozens of requests (the cache spends it once).</summary>
    public const int MaxSearchResolutions = 12;
    public const int MaxArtistResolutions = 8;

    /// <summary>Base pool weights in scoring (normalized to the max).</summary>
    internal const double ScenePoolWeight = 2.5;
    internal const double RadioPoolWeight = 2.0;
    internal const double ArtistPoolBase = 3.0;
    internal const double ScPoolWeight = 2.6;

    /// <summary>Pool E "SoundCloud scene": how many like-seeds and how many related tracks are taken.</summary>
    public const int ScSeedCount = 8;
    public const int ScRelatedLimit = 20;

    /// <summary>Artist-affinity weight of a candidate and jitter spread: familiar material
    /// reliably rises above noise, novelty stays dosed.</summary>
    internal const double AffinityBonusWeight = 1.2;
    internal const double SeedBonusWeight = 0.6;
    internal const double JitterAmplitude = 0.75;

    /// <summary>Multiplier for artists played in recent mixes — not an exclusion but a
    /// demotion: with a thin familiar pool they remain reachable, just in the tail.</summary>
    internal const double DemotedArtistFactor = 0.25;

    /// <summary>How many seeds are taken from each library source per generation
    /// (pool D, weighted by play_log; seeds from never-played artists are dropped —
    /// a like without plays is noise, not taste).</summary>
    internal static readonly (int Yandex, int Vk, int SoundCloud, int Local) SeedQuota = (8, 5, 2, 2);

    /// <summary>TTL of the /similar cache and negative search matches.</summary>
    public static readonly TimeSpan SimilarCacheTtl = TimeSpan.FromDays(14);
    public static readonly TimeSpan SeedMapRetryTtl = TimeSpan.FromDays(14);

    /// <summary>Affinity window over play_log and the "recently played" threshold (do not suggest).</summary>
    public const int AffinityWindowDays = 90;
    public const int RecentPlayedDays = 2;

    /// <summary>For how many days an unplayed suggestion stays out of the mix —
    /// a passive negative signal and rotation between mixes. 3, not 7: with frequent
    /// generations the cooldown drains the pool faster than it refills, and the wave
    /// shrinks from update to update.</summary>
    public const int SuggestedCooldownDays = 3;

    /// <summary>A played suggestion returns after just one day: a play is a positive
    /// signal, and it is exactly the return of liked material that keeps the mix full
    /// under active rotation (otherwise the wave drains the pool and shrinks from
    /// generation to generation).</summary>
    public const int PlayedSuggestedCooldownDays = 1;

    /// <summary>Grace backfill: if the wave came out shorter than full (the pool was
    /// drained by cooldown or few families remain), suggestions older than this age
    /// return to rotation — oldest first. One hour, so a mix the user just heard is
    /// not repeated. Repeating yesterday's material beats a one-track mix.</summary>
    public const int GraceRefillHours = 1;

    private readonly YmService _ym;
    private readonly SoundCloudService _soundCloud;
    private readonly RecommendationRepository _repo;
    private readonly YmTracksRepository _ymTracks;
    private readonly VkTracksRepository _vkTracks;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly LibraryService _library;

    public RecommendationService(YmService ym, SoundCloudService soundCloud,
        RecommendationRepository repo, YmTracksRepository ymTracks,
        VkTracksRepository vkTracks, SoundCloudLikesRepository scLikes, LibraryService library)
    {
        _ym = ym;
        _soundCloud = soundCloud;
        _repo = repo;
        _ymTracks = ymTracks;
        _vkTracks = vkTracks;
        _scLikes = scLikes;
        _library = library;
    }

    /// <summary>A single pool D seed. YmId is known up front for YM likes; the rest
    /// are resolved via /search (cached in wave_seed_map).</summary>
    private sealed record Seed(string Source, string SeedId, string Artist, string Title,
                               long DurationMs, string? YmId);

    /// <summary>An artist for pools A/B/C: play_log key, display name, ym-id, play count.</summary>
    private sealed record TopArtist(string Key, string Name, string YmId, double Plays);

    /// <summary>
    /// Seed sources gathered BEFORE background generation: the reads go through the
    /// shared SQLite connection of other services and must run on the caller's (UI)
    /// thread, like the loads of all other pages — generation itself works in the
    /// background only with RecommendationRepository's own connections.
    /// </summary>
    public sealed class WaveSources
    {
        public IReadOnlyList<YmTrackRow> YmRows { get; init; } = Array.Empty<YmTrackRow>();
        public IReadOnlyList<VkTrackRow> VkRows { get; init; } = Array.Empty<VkTrackRow>();
        public IReadOnlyList<SoundCloudLikeRow> ScRows { get; init; } = Array.Empty<SoundCloudLikeRow>();
        public IReadOnlyList<Track> LocalTracks { get; init; } = Array.Empty<Track>();
    }

    /// <summary>Gathers seed sources (fast indexed queries; UI thread).</summary>
    public async Task<WaveSources> GatherSourcesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new WaveSources
        {
            YmRows = await _ymTracks.GetAllAsync(),
            VkRows = await _vkTracks.GetAllAsync(),
            ScRows = await _scLikes.GetAllAsync(),
            LocalTracks = await _library.GetAllTracksAsync()
        };
    }

    /// <summary>
    /// Generates the wave: play_log artist analysis → pools A/B/C → library seeds →
    /// pool D → filters → ranking. Requires a connected Yandex Music account (the
    /// caller checks via YmService.HasToken). Returns an empty list when there are
    /// no candidates. Heavy sections run on the thread pool; background DB access
    /// happens ONLY through RecommendationRepository's own connections.
    /// </summary>
    public async Task<List<WaveItem>> GenerateWaveAsync(WaveSources sources, CancellationToken ct)
        => await Task.Run(() => GenerateCoreAsync(sources, ct), ct);

    private async Task<List<WaveItem>> GenerateCoreAsync(WaveSources sources, CancellationToken ct)
    {
        var ymRows = sources.YmRows;
        var vkRows = sources.VkRows;
        var scRows = sources.ScRows;
        var localTracks = sources.LocalTracks;

        ct.ThrowIfCancellationRequested();
        var rng = Random.Shared;

        // ===== Taste signals =====
        var plays = await _repo.GetRecentPlaysAsync(AffinityWindowDays);
        var suggested = await _repo.GetSuggestedAsync();
        // Plays of previously suggested material weigh less — the mix must not grow
        // its own "favorite" artists out of what it pushed itself.
        var suggestedKeys = suggested
            .Select(s => s.ArtistKey + "|" + s.TitleKey)
            .Where(k => k.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
        var artistPlayCounts = ComputePlayCounts(plays, suggestedKeys);
        // Raw counters — for the "fame"/"strong taste" thresholds and gate-check
        // priority: the discount is not included in them.
        var rawPlayCounts = ComputeRawPlayCounts(plays);
        var playKeysAll = plays
            .Select(p => PlayKey(p.Artist, p.Title))
            .Where(k => k != null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

        // Display names of play_log keys (for artist search queries).
        var artistNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var play in plays)
            foreach (var name in ArtistHelper.Split(play.Artist))
            {
                var key = ArtistHelper.Key(name);
                if (key.Length > 0) artistNames.TryAdd(key, name);
            }

        // ===== Pools A/B/C: artist analysis =====
        var topArtists = await ResolveTopArtistsAsync(rawPlayCounts, artistNames, ct);
        var seedWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var artist in topArtists)
            seedWeights[$"artist:{artist.YmId}"] = ArtistPoolBase
                                                    + 2 * Math.Log(1 + artist.Plays) / Math.Log(2);

        // B: "shared scene" — similar artists of top artists, their tracks via the same endpoint.
        await CollectSceneArtistsAsync(topArtists, artistPlayCounts, seedWeights, rng, ct);

        // C: "scene radio" — rotor batch over top artists (fresh every generation).
        await CollectRadioBatchesAsync(topArtists, seedWeights, ct);

        // ===== Pool D: library seeds (weighted by play_log) =====
        double SeedWeight(Seed seed)
        {
            var artistSum = ArtistKeysOf(seed.Artist)
                .Sum(k => artistPlayCounts.GetValueOrDefault(k));
            var w = 1 + 2 * Math.Log(1 + artistSum) / Math.Log(2);
            var key = PlayKey(seed.Artist, seed.Title);
            if (key != null && playKeysAll.Contains(key)) w += 2;
            return w;
        }

        var yandexSeeds = ymRows
            .Where(r => !string.IsNullOrEmpty(r.YmId))
            .Select(r => new Seed(Track.SourceYandex, r.YmId, r.Artist, r.Title, r.DurationMs, r.YmId))
            .Where(s => IsSeedPlayed(s.Artist, s.Title, artistPlayCounts, playKeysAll));
        var vkSeeds = vkRows
            .Where(r => !string.IsNullOrEmpty(r.VkId) && !string.IsNullOrEmpty(r.Title))
            .Select(r => new Seed(Track.SourceVk, r.VkId, r.Artist, r.Title, r.DurationMs, null))
            .Where(s => IsSeedPlayed(s.Artist, s.Title, artistPlayCounts, playKeysAll));
        var scSeeds = scRows
            .Where(r => !string.IsNullOrEmpty(r.ScId) && !string.IsNullOrEmpty(r.Title))
            .Select(r => new Seed(Track.SourceSoundCloud, r.ScId, r.Artist, r.Title, r.DurationMs, null))
            .Where(s => IsSeedPlayed(s.Artist, s.Title, artistPlayCounts, playKeysAll));
        var localSeeds = localTracks
            .Where(t => t.PlayCount > 0 || t.IsFavorite)
            .Select(t => new Seed(Track.SourceLocal, $"local:{t.Id}", t.Artist, t.Title,
                t.DurationTicks / TimeSpan.TicksPerMillisecond, null))
            .Where(s => IsSeedPlayed(s.Artist, s.Title, artistPlayCounts, playKeysAll));

        var trackSeedIds = await Task.Run(async () =>
        {
            var seeds = new List<Seed>();
            seeds.AddRange(WeightedSample(yandexSeeds.ToList(), SeedWeight, SeedQuota.Yandex, rng));
            seeds.AddRange(WeightedSample(vkSeeds.ToList(), SeedWeight, SeedQuota.Vk, rng));
            seeds.AddRange(WeightedSample(scSeeds.ToList(), SeedWeight, SeedQuota.SoundCloud, rng));
            seeds.AddRange(WeightedSample(localSeeds.ToList(), SeedWeight, SeedQuota.Local, rng));

            var resolved = new List<Seed>(seeds.Count);
            var budget = MaxSearchResolutions;
            foreach (var seed in seeds)
            {
                ct.ThrowIfCancellationRequested();
                if (seed.YmId != null)
                {
                    resolved.Add(seed);
                    continue;
                }

                var ymId = await ResolveSeedYmIdAsync(seed, budget > 0, ct);
                if (ymId == null || ymId.Length == 0) continue;
                budget--;
                resolved.Add(seed with { YmId = ymId });
            }

            foreach (var seed in resolved)
                seedWeights[seed.YmId!] = SeedWeight(seed);

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            return resolved.Where(s => seenIds.Add(s.YmId!)).Take(SeedCount).Select(s => s.YmId!).ToList();
        });

        // ===== Pool E "SoundCloud scene": related tracks over played likes =====
        // Tracks come from the SC catalog (not the user's library) and play through
        // the player's regular SC resolve (transcodings).
        var scSeedIds = new List<string>();
        var scRelatedSeeds = scRows
            .Where(r => !string.IsNullOrEmpty(r.ScId) && !string.IsNullOrEmpty(r.Title))
            .Select(r => new Seed(Track.SourceSoundCloud, r.ScId, r.Artist, r.Title, r.DurationMs, null))
            .Where(s => IsSeedPlayed(s.Artist, s.Title, artistPlayCounts, playKeysAll))
            .ToList();
        foreach (var seed in WeightedSample(scRelatedSeeds, SeedWeight, ScSeedCount, rng))
        {
            if (seedWeights.TryAdd($"sc:{seed.SeedId}", ScPoolWeight))
                scSeedIds.Add($"sc:{seed.SeedId}");
        }

        // ===== Refresh of the /similar, /artists/{id}/tracks and SC related caches =====
        var artistSeedIds = seedWeights.Keys
            .Where(k => k.StartsWith("artist:", StringComparison.Ordinal))
            .ToList();
        var rotorSeedIds = seedWeights.Keys
            .Where(k => k.StartsWith("rotor:", StringComparison.Ordinal))
            .ToList();

        var staleArtist = await _repo.GetStaleSeedsAsync(artistSeedIds, SimilarCacheTtl);
        var staleTrackSeeds = await _repo.GetStaleSeedsAsync(trackSeedIds, SimilarCacheTtl);
        var staleSc = await _repo.GetStaleSeedsAsync(scSeedIds, SimilarCacheTtl);

        foreach (var seedId in staleArtist.Concat(staleTrackSeeds).Concat(staleSc))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                List<YmTrackDto> tracks;
                if (seedId.StartsWith("artist:", StringComparison.Ordinal))
                {
                    // Catalog deeper than one page (2 × 50): more material for
                    // rotation between mixes within one family.
                    // Pause between catalogs: fetching two pages across all seeds
                    // back-to-back hits the Yandex rate limit (429).
                    var page0 = await _ym.GetArtistTracksAsync(seedId["artist:".Length..], ArtistPageSize, 0, ct);
                    var page1 = await _ym.GetArtistTracksAsync(seedId["artist:".Length..], ArtistPageSize, 1, ct);
                    tracks = page0.Concat(page1.Where(p => page0.All(t => t.Id != p.Id))).ToList();
                    await Task.Delay(ArtistCatalogFetchDelayMs, ct);
                }
                else
                {
                    tracks = await _ym.GetSimilarTracksAsync(seedId, ct);
                }

                await _repo.ReplaceSimilarAsync(seedId, tracks
                    .Select(t => new WaveCandidateRow
                    {
                        YmId = t.Id,
                        Title = t.Title,
                        Artist = t.Artist,
                        DurationMs = t.DurationMs,
                        CoverUri = t.CoverUri ?? string.Empty,
                        Available = t.Available
                    })
                    .ToList());
            }
            catch (YmApiException ex) when (YmApiException.IsSessionError(ex.HttpCode))
            {
                throw; // token revoked — the VM will show "connect account"
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: refresh failed for seed {seedId} — skipped");
            }
        }

        // SC related: its own candidates (ScTrack) → the same cache rows; if the SC
        // session is dead the pool is simply skipped, the rest assemble the wave.
        foreach (var seedId in staleSc)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var related = await _soundCloud.GetRelatedTracksAsync(seedId["sc:".Length..], ScRelatedLimit, ct)
                               ?? new List<ScTrack>();
                await _repo.ReplaceSimilarAsync(seedId, related
                    .Where(t => t.Streamable && t.Policy == null && t.Id > 0
                                && t.PlaybackCount >= MinTrackPlays) // no-names/AI tracks out
                    .Select(t => new WaveCandidateRow
                    {
                        YmId = t.Id.ToString(),
                        Title = t.Title,
                        Artist = !string.IsNullOrWhiteSpace(t.User?.Username)
                            ? t.User.Username
                            : (t.User?.FullName ?? string.Empty),
                        DurationMs = t.DurationMs,
                        CoverUri = t.ArtworkUrl ?? string.Empty,
                        Available = true
                    })
                    .ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: SC related refresh failed for {seedId} — skipped");
            }
        }

        // Rotor: batches are random — not cached on TTL, fresh ones every generation.
        foreach (var seedId in rotorSeedIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tracks = await _ym.GetArtistRadioTracksAsync(seedId["rotor:".Length..], ct);
                await _repo.ReplaceSimilarAsync(seedId, tracks
                    .Select(t => new WaveCandidateRow
                    {
                        YmId = t.Id,
                        Title = t.Title,
                        Artist = t.Artist,
                        DurationMs = t.DurationMs,
                        CoverUri = t.CoverUri ?? string.Empty,
                        Available = t.Available
                    })
                    .ToList());
            }
            catch (YmApiException ex) when (YmApiException.IsSessionError(ex.HttpCode))
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: rotor batch failed for {seedId} — skipped");
            }
        }

        // ===== Candidates of all pools: dedup by "source:id", priority — seed order
        // (play_log artists outrank the scene, the scene outranks the rotor, that
        // outranks similar tracks) =====
        var allSeedIds = artistSeedIds.Concat(rotorSeedIds).Concat(trackSeedIds).Concat(scSeedIds).ToList();
        var maxSeedWeight = seedWeights.Count > 0 ? seedWeights.Values.Max() : 1.0;
        var similarMap = await _repo.GetSimilarAsync(allSeedIds, SimilarCacheTtl);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Cross-source song dedup: the same song found both in the YM catalog and in
        // SC related must not appear twice in the mix (priority — the earlier pool).
        var seenSongs = new HashSet<string>(StringComparer.Ordinal);
        var pool = new List<WaveItem>();
        foreach (var seedId in allSeedIds)
        {
            if (!similarMap.TryGetValue(seedId, out var rows)) continue;
            var source = seedId.StartsWith("sc:", StringComparison.Ordinal)
                ? Track.SourceSoundCloud
                : Track.SourceYandex;
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.YmId)) continue;
                var dedupKey = source + ":" + row.YmId;
                if (!seen.Add(dedupKey)) continue;
                var songKey = MatchHelper.BuildKey(row.Artist, row.Title);
                if (songKey.Length > 1 && !seenSongs.Add(songKey)) continue;
                pool.Add(new WaveItem
                {
                    Source = source,
                    PlatformId = row.YmId,
                    Title = row.Title,
                    Artist = row.Artist,
                    DurationMs = row.DurationMs,
                    CoverUri = row.CoverUri.Length > 0 ? row.CoverUri : null,
                    Available = row.Available
                });
            }
        }
        if (pool.Count == 0) return new List<WaveItem>();

        // Base of "familiar" material from all libraries: artists the user has at
        // least added get a minimal bonus (adding is a signal too).
        var maxPlayCount = artistPlayCounts.Count > 0 ? artistPlayCounts.Values.Max() : 0;
        foreach (var key in LibraryArtistKeys(ymRows.Select(r => r.Artist),
                     vkRows.Select(r => r.Artist),
                     scRows.Select(r => r.Artist),
                     localTracks.Select(t => t.Artist)))
            artistPlayCounts[key] = Math.Max(artistPlayCounts.GetValueOrDefault(key), 0.05);

        // ===== Feedback: played-after-suggested is not an exception =====
        var playKeysSince = plays
            .Select(p => (Key: PlayKey(p.Artist, p.Title), p.PlayedAt))
            .Where(p => p.Key != null)
            .ToList();
        var nowPlaying = new List<string>();
        foreach (var row in suggested.Where(s => !s.Played))
        {
            if (playKeysSince.Any(pk => pk.Key == (row.ArtistKey + "|" + row.TitleKey)
                                        && pk.PlayedAt > row.SuggestedAt))
                nowPlaying.Add(row.YmId);
        }
        await _repo.MarkPlayedAsync(nowPlaying);

        // ===== Filters =====
        var libraryYmIds = ymRows.Select(r => r.YmId).ToHashSet(StringComparer.Ordinal);
        var localIndex = MatchHelper.BuildIndex(localTracks);
        var vkKeys = vkRows.Select(r => MatchHelper.BuildKey(r.Artist, r.Title))
            .Where(k => k.Length > 1).ToHashSet(StringComparer.Ordinal);
        var scKeys = scRows.Select(r => MatchHelper.BuildKey(r.Artist, r.Title))
            .Where(k => k.Length > 1).ToHashSet(StringComparer.Ordinal);
        // The suggestion cooldown is split: unplayed suggestions rest 7 days (passive
        // negative), played ones return after a day — they were already liked, and
        // their return is what keeps the wave from shrinking under frequent generation.
        var cooldownIds = suggested
            .Where(s => s.SuggestedAt >= DateTime.UtcNow.AddDays(-(s.Played
                ? PlayedSuggestedCooldownDays
                : SuggestedCooldownDays)))
            .Select(s => s.YmId)
            .ToHashSet(StringComparer.Ordinal);
        // Artists of recent mixes get a reduced bonus: the next mix introduces other
        // familiar artists instead of the same ones in the same order.
        var demotedArtistKeys = suggested
            .Where(s => s.SuggestedAt >= DateTime.UtcNow.AddDays(-(s.Played
                ? PlayedSuggestedCooldownDays
                : SuggestedCooldownDays)))
            .Select(s => s.ArtistKey)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var recentPlayedKeys = plays
            .Where(p => p.PlayedAt >= DateTime.UtcNow.AddDays(-RecentPlayedDays))
            .Select(p => MatchHelper.BuildKey(p.Artist, p.Title))
            .Where(k => k.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

        // ===== Base filters (no "fame") =====
        var baseFiltered = pool
            .Where(c => c.Source != Track.SourceYandex || !libraryYmIds.Contains(c.PlatformId))
            .Where(c => MatchHelper.FindLocalMatch(localIndex, c.Artist, c.Title) == null)
            .Where(c => !recentPlayedKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !vkKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !scKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !cooldownIds.Contains(c.JournalId))
            .ToList();

        // ===== Only listened-to artists =====
        // A candidate counts as "familiar" if at least one of its artists has
        // ≥ MinTopArtistPlays organic plays in play_log. Library membership alone
        // does NOT count: recommendations are built on what the user actually
        // listens to — "added but not played" does not enter the mix. Everything
        // else is novelty (the mix tail), and it is filtered by the audience gate.
        var playedKeys = rawPlayCounts
            .Where(kv => kv.Value >= MinTopArtistPlays)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var knownKeys = playedKeys;

        // ===== Single audience gate for the whole pool =====
        // Strong taste (≥ StrongTastePlays organic plays) is not checked — niche
        // favorites with small audiences stay. Weak signal (a couple of background
        // plays, click-tested in the player) and novelty are checked: small artists
        // (< MinArtistListeners listeners) do not pass into the mix.
        var strongKeys = rawPlayCounts
            .Where(kv => kv.Value >= StrongTastePlays)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var gated = baseFiltered.Count > 0
            ? await FilterByListenersAsync(baseFiltered, strongKeys, rawPlayCounts, ct)
            : baseFiltered;
        var filtered = gated
            .Where(c => TrackArtistKeys(c).Any(knownKeys.Contains))
            .ToList();
        var novelty = gated
            .Where(c => !TrackArtistKeys(c).Any(knownKeys.Contains))
            .ToList();

        // ===== Ranking =====
        var candidateWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var seedId in allSeedIds)
        {
            if (!similarMap.TryGetValue(seedId, out var rows) || !seedWeights.TryGetValue(seedId, out var w))
                continue;
            var source = seedId.StartsWith("sc:", StringComparison.Ordinal)
                ? Track.SourceSoundCloud
                : Track.SourceYandex;
            foreach (var row in rows)
                candidateWeights.TryAdd(source + ":" + row.YmId, w);
        }

        List<WaveItem> RankAll()
        {
            var result = RankCandidates(filtered, artistPlayCounts, maxPlayCount, candidateWeights,
                maxSeedWeight, WaveSize, LightPerArtist, rng, demotedArtistKeys);

            // The mix tail is backfilled with novelty (see WaveSize): no-names rank
            // after all familiar material — a short mix caused by them is worse than
            // a full one.
            if (result.Count < WaveSize && novelty.Count > 0)
                result.AddRange(RankCandidates(novelty, artistPlayCounts, maxPlayCount, candidateWeights,
                    maxSeedWeight, WaveSize - result.Count, LightPerArtist, rng, demotedArtistKeys));
            return result;
        }

        var wave = RankAll();

        // ===== Grace backfill: the wave came out shorter than full =====
        // The cooldown drained the pool (or few families remain) — suggestions older
        // than GraceRefillHours return to rotation, oldest first. Repeating
        // yesterday's material beats a one-track mix; anything newer than an hour
        // still rests.
        if (wave.Count < WaveSize)
        {
            var graceCutoff = DateTime.UtcNow.AddHours(-GraceRefillHours);
            var suggestedAtById = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var s in suggested)
                suggestedAtById.TryAdd(s.YmId, s.SuggestedAt);
            var baseSet = new HashSet<WaveItem>(baseFiltered);

            var grace = pool
                .Where(c => !baseSet.Contains(c)) // exactly the cooldown-filtered ones
                .Where(c => suggestedAtById.TryGetValue(c.JournalId, out var at) && at <= graceCutoff)
                .OrderBy(c => suggestedAtById[c.JournalId])
                .ToList();
            if (grace.Count > 0)
            {
                // Returning material passes the same audience gate: otherwise a weak
                // artist once click-tested returns via grace.
                var graceGated = await FilterByListenersAsync(grace, strongKeys, rawPlayCounts, ct);
                filtered.AddRange(graceGated
                    .Where(c => TrackArtistKeys(c).Any(knownKeys.Contains)));
                novelty.AddRange(graceGated
                    .Where(c => !TrackArtistKeys(c).Any(knownKeys.Contains)));
                wave = RankAll();
            }
        }

        await _repo.MarkSuggestedAsync(wave
            .Select(t => (t.JournalId, t.Artist, t.Title)));
        await _repo.PruneAsync(months: 6);

        return wave;
    }

    /// <summary>
    /// Top play_log artists → ym-artist-id (cache in wave_seed_map, source='ym_artist',
    /// negative cache with TTL). Ordered by play count, at most MaxTopArtists.
    /// </summary>
    private async Task<List<TopArtist>> ResolveTopArtistsAsync(
        Dictionary<string, double> artistPlayCounts,
        Dictionary<string, string> artistNames,
        CancellationToken ct)
    {
        var top = artistPlayCounts
            .Where(kv => artistNames.ContainsKey(kv.Key) && kv.Value >= MinTopArtistPlays)
            .OrderByDescending(kv => kv.Value)
            .Take(MaxTopArtists)
            .ToList();

        var resolved = new List<TopArtist>(top.Count);
        var budget = MaxArtistResolutions;
        foreach (var (key, count) in top)
        {
            ct.ThrowIfCancellationRequested();
            var name = artistNames[key];

            var cached = await _repo.GetSeedMapAsync("ym_artist", key);
            if (cached != null)
            {
                if (cached.Value.YmId.Length > 0)
                {
                    resolved.Add(new TopArtist(key, name, cached.Value.YmId, count));
                    continue;
                }
                if (DateTime.UtcNow - cached.Value.ResolvedAt < SeedMapRetryTtl) continue;
            }

            if (budget <= 0) continue;

            try
            {
                var results = await _ym.SearchArtistsAsync(name, limit: 5, ct);
                var match = PickArtistMatch(results, name);
                await _repo.SaveSeedYmIdAsync("ym_artist", key, match?.Id ?? string.Empty);
                if (match != null)
                {
                    budget--;
                    resolved.Add(new TopArtist(key, name, match.Id, count));
                }
            }
            catch (YmApiException ex) when (YmApiException.IsSessionError(ex.HttpCode))
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: artist search failed for '{name}' — skipped");
            }
        }
        return resolved;
    }

    /// <summary>
    /// Best artist search result: exact match of normalized names, otherwise the first
    /// whose name contains the query (or vice versa). null — nothing similar.
    /// </summary>
    internal static YmArtistDto? PickArtistMatch(IReadOnlyList<YmArtistDto> results, string queryName)
    {
        var queryKey = ArtistHelper.Key(queryName);
        if (queryKey.Length == 0) return null;

        foreach (var a in results)
        {
            var key = ArtistHelper.Key(a.Name);
            if (key == queryKey) return a;
        }
        return results.FirstOrDefault(a => ArtistHelper.Key(a.Name).Contains(queryKey, StringComparison.Ordinal)
                                           || queryKey.Contains(ArtistHelper.Key(a.Name), StringComparison.Ordinal)
                                           && ArtistHelper.Key(a.Name).Length > 0);
    }

    /// <summary>
    /// Single audience gate, closed by default: a family passes only if it is strong
    /// (≥ StrongTastePlays raw plays) or VERIFIED with an audience ≥ MinArtistListeners
    /// (YM brief-info). Unverified material drops out of this generation — small
    /// artists do not leak in while the cache is cold. Checks (budget of
    /// MaxListenerResolutions per generation) first cover the user's weak signal in
    /// descending play order — their results immediately fill the mix — then families
    /// without plays (likely "AI" spam). Cache lives forever in wave_seed_map
    /// (source='ym_listeners'). A single failed request does not kill the generation.
    /// </summary>
    private async Task<List<WaveItem>> FilterByListenersAsync(
        List<WaveItem> candidates, HashSet<string> strongKeys,
        Dictionary<string, double> playCounts, CancellationToken ct)
    {
        // Display names of families — from the candidates themselves (play_log has none).
        var names = new Dictionary<string, (string Name, double Plays)>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            foreach (var name in ArtistHelper.Split(candidate.Artist))
            {
                var key = ArtistHelper.Key(ArtistHelper.StripFeatures(name));
                if (key.Length > 0 && !names.ContainsKey(key))
                    names[key] = (name, playCounts.GetValueOrDefault(key));
            }

        var listeners = new Dictionary<string, long>(StringComparer.Ordinal);
        var budget = MaxListenerResolutions;
        var toCheck = names
            .Where(kv => !strongKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Value.Plays == 0 ? 1 : 0) // user's weak signal first
            .ThenByDescending(kv => kv.Value.Plays)
            .ToList();
        foreach (var (key, entry) in toCheck)
        {
            ct.ThrowIfCancellationRequested();
            var cached = await _repo.GetSeedMapAsync("ym_listeners", key);
            if (cached != null)
            {
                // "0" — checked and rejected (no-name or search found nothing): budget not spent.
                if (long.TryParse(cached.Value.YmId, out var count)) listeners[key] = count;
                continue;
            }
            if (budget <= 0) continue;

            budget--;
            try
            {
                var results = await _ym.SearchArtistsAsync(entry.Name, limit: 5, ct);
                var match = PickArtistMatch(results, entry.Name);
                var count = match != null
                    ? await _ym.GetArtistListenersAsync(match.Id, ct) ?? 0
                    : 0;
                await _repo.SaveSeedYmIdAsync("ym_listeners", key, count.ToString());
                listeners[key] = count;
            }
            catch (YmApiException ex) when (YmApiException.IsSessionError(ex.HttpCode))
            {
                throw; // token revoked — the VM will show "connect account"
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: listeners check failed for '{entry.Name}' — skipped");
            }
        }

        // Fail-closed across ALL artists of the card: "strong X feat. minor guest"
        // still shows the small name on the card — such a track does not pass.
        bool FamilyPasses(string key)
            => strongKeys.Contains(key)
               || listeners.TryGetValue(key, out var count) && count >= MinArtistListeners;

        return candidates
            .Where(c =>
            {
                var keys = TrackArtistKeys(c);
                return keys.Count > 0 && keys.All(FamilyPasses);
            })
            .ToList();
    }

    /// <summary>
    /// Pool B "shared scene": for EACH top artist take ScenePerArtist similar artists,
    /// subtract those the user already plays (they are in pool A anyway), sample up to
    /// MaxSceneArtists new ones and fetch their catalogs (cache 'artist:{id}', scene
    /// weight). This is the main source of a WIDE circle: each new scene family passes
    /// the audience gate and stays in rotation forever.
    /// A single failed request does not kill the generation.
    /// </summary>
    private async Task CollectSceneArtistsAsync(
        IReadOnlyList<TopArtist> topArtists,
        Dictionary<string, double> artistPlayCounts,
        Dictionary<string, double> seedWeights,
        Random rng,
        CancellationToken ct)
    {
        var knownKeys = artistPlayCounts.Keys.ToHashSet(StringComparer.Ordinal);
        var scene = new List<YmArtistDto>();

        foreach (var artist in topArtists)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var similar = await _ym.GetSimilarArtistsAsync(artist.YmId, ct);
                var fresh = similar
                    .Where(a => !knownKeys.Contains(ArtistHelper.Key(a.Name)))
                    .ToList();
                scene.AddRange(WeightedSample(fresh, _ => 1.0, ScenePerArtist, rng));
            }
            catch (YmApiException ex) when (YmApiException.IsSessionError(ex.HttpCode))
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave: similar-artists failed for {artist.Name} — skipped");
            }
        }

        foreach (var sceneArtist in scene
                     .DistinctBy(a => a.Id)
                     .OrderBy(_ => rng.Next())
                     .Take(MaxSceneArtists))
        {
            seedWeights.TryAdd($"artist:{sceneArtist.Id}", ScenePoolWeight);
        }
    }

    /// <summary>
    /// Pool C "scene radio": a rotor batch over top artists (fresh each generation,
    /// radio-pool weight). A failure does not kill the generation.
    /// </summary>
    private async Task CollectRadioBatchesAsync(
        IReadOnlyList<TopArtist> topArtists,
        Dictionary<string, double> seedWeights,
        CancellationToken ct)
    {
        foreach (var artist in topArtists.Take(MaxRadioArtists))
        {
            ct.ThrowIfCancellationRequested();
            seedWeights.TryAdd($"rotor:{artist.YmId}", RadioPoolWeight);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Seed's ym_id via cache/search: a known match returns immediately; an expired
    /// negative cache or a missing entry triggers a search (if the budget allows).
    /// Return values: ym_id — found; '' — a negative cache hit or no search match;
    /// null — no budget left or the seed lacks artist/title.
    /// </summary>
    private async Task<string?> ResolveSeedYmIdAsync(Seed seed, bool searchAllowed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seed.Artist) || string.IsNullOrWhiteSpace(seed.Title))
            return null;

        var cached = await _repo.GetSeedMapAsync(seed.Source, seed.SeedId);
        if (cached != null)
        {
            if (cached.Value.YmId.Length > 0) return cached.Value.YmId;
            if (DateTime.UtcNow - cached.Value.ResolvedAt < SeedMapRetryTtl) return string.Empty;
            // Expired negative cache — search again (Yandex catalogs grow).
        }

        if (!searchAllowed) return null;

        var results = await _ym.SearchTracksAsync($"{seed.Artist} {seed.Title}", limit: 8, ct);
        var match = PickSearchMatch(results, seed.Artist, seed.Title, seed.DurationMs);
        await _repo.SaveSeedYmIdAsync(seed.Source, seed.SeedId, match?.Id ?? string.Empty);
        return match?.Id ?? string.Empty;
    }

    /// <summary>
    /// Best search result for a seed: a normalized title match AND artist intersection;
    /// duration (when known to both sides) filters at ±15s. No strict match — the first
    /// result with a matching artist (track titles diverge across catalogs more often
    /// than artist names). null — no artist matches: someone else's track in the wave
    /// is worse than a skip.
    /// </summary>
    internal static YmTrackDto? PickSearchMatch(
        IReadOnlyList<YmTrackDto> results, string seedArtist, string seedTitle, long seedDurationMs)
    {
        var seedTitleKey = MatchHelper.Normalize(seedTitle);
        if (seedTitleKey.Length == 0) return null;

        var seedArtistKeys = ArtistHelper.Split(seedArtist)
            .Select(ArtistHelper.Key)
            .Where(k => k.Length > 0)
            .ToHashSet();
        if (seedArtistKeys.Count == 0) return null;

        bool ArtistMatches(YmTrackDto t)
            => ArtistHelper.Split(t.Artist).Any(a => seedArtistKeys.Contains(ArtistHelper.Key(a)));

        bool DurationMatches(YmTrackDto t)
            => seedDurationMs <= 0 || t.DurationMs <= 0
               || Math.Abs(t.DurationMs - seedDurationMs) <= 15_000;

        foreach (var t in results)
        {
            if (!DurationMatches(t)) continue;
            if (MatchHelper.Normalize(t.Title) == seedTitleKey && ArtistMatches(t)) return t;
        }

        return results.FirstOrDefault(t => DurationMatches(t) && ArtistMatches(t));
    }

    /// <summary>
    /// Whether a track qualifies as a pool D seed: its artist appears in play_log OR
    /// the track itself was played. A library like/track the user never played does
    /// not reflect taste — seeds like that pull the wave off course. Special sources
    /// (SC likes and favorites) are filtered the same way: an explicit signal is
    /// weaker than an actual listen.
    /// </summary>
    internal static bool IsSeedPlayed(
        string artist, string title,
        Dictionary<string, double> artistPlayCounts,
        HashSet<string> playKeysAll)
    {
        if (ArtistHelper.Split(artist).Any(n => artistPlayCounts.GetValueOrDefault(ArtistHelper.Key(n)) > 0))
            return true;
        var key = PlayKey(artist, title);
        return key != null && playKeysAll.Contains(key);
    }

    /// <summary>
    /// Artist affinity bonus is RELATIVE: a share of the most-played artist
    /// (logarithmic smoothing on both sides). Previously the logarithm saturated by
    /// 3-4 plays, so artists with a couple of plays got nearly the max boost,
    /// displacing genuinely played ones. count=0 → 0;
    /// count=max → the full <see cref="AffinityBonusWeight"/>.
    /// </summary>
    internal static double ArtistBonus(
        Dictionary<string, double> artistPlayCounts, double maxPlayCount, string artistKey)
    {
        if (maxPlayCount <= 0 || artistKey.Length == 0) return 0;
        var count = artistPlayCounts.GetValueOrDefault(artistKey);
        if (count <= 0) return 0;
        var relative = Math.Min(1.0, Math.Log2(1 + count) / Math.Log2(1 + maxPlayCount));
        return AffinityBonusWeight * relative;
    }

    /// <summary>Artist play frequencies from play_log (featured artists count for each).
    /// Plays from the suggestion journal (discountedKeys) weigh 0.4: a track the user
    /// merely let the mix finish must not become "taste" — otherwise the wave grows
    /// its own top artists out of what it pushed (a self-polluting loop).</summary>
    internal static Dictionary<string, double> ComputePlayCounts(
        IReadOnlyList<(string Artist, string Title, DateTime PlayedAt)> plays,
        HashSet<string>? discountedKeys = null)
    {
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in plays)
        {
            var playKey = PlayKey(play.Artist, play.Title);
            var weight = playKey != null && discountedKeys != null && discountedKeys.Contains(playKey)
                ? SuggestedPlayWeight
                : 1.0;
            foreach (var key in ArtistKeysOf(play.Artist))
                counts[key] = counts.GetValueOrDefault(key) + weight;
        }
        return counts;
    }

    /// <summary>Weight of a play for a track previously suggested by the mix (see ComputePlayCounts).</summary>
    internal const double SuggestedPlayWeight = 0.25;

    /// <summary>Raw play frequencies (no discount) — for the "fame" and "strong taste"
    /// thresholds: a play is a play, gates must not depend on whether the track was
    /// played from the mix (otherwise strong taste was understated to a couple of
    /// artists). The discount remains only in ranking.</summary>
    internal static Dictionary<string, double> ComputeRawPlayCounts(
        IReadOnlyList<(string Artist, string Title, DateTime PlayedAt)> plays)
    {
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in plays)
            foreach (var key in ArtistKeysOf(play.Artist))
                counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts;
    }

    /// <summary>Family keys of a track: all its artists after stripping features.
    /// Tracks "X feat. B", "X &amp; B" and "X" belong to the family {x, b} — for limits
    /// and spreading they count as one artist under any spelling.</summary>
    internal static List<string> TrackArtistKeys(WaveItem track)
        => ArtistKeysOf(track.Artist);

    /// <summary>Family keys of an artist string (Split → StripFeatures → Key).</summary>
    internal static List<string> ArtistKeysOf(string artist)
        => ArtistHelper.Split(artist)
            .Select(n => ArtistHelper.Key(ArtistHelper.StripFeatures(n)))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Wave candidate ranking: scoring (pool/seed weight + affinity + jitter) →
    /// sort → selection in three passes. The first uses per-artist limits ("heavy"
    /// artists get HeavyPerArtist, the rest maxPerArtist): familiar material takes
    /// most of the wave without degenerating into an album. The second and third use
    /// relaxed limits (+1 and +2): the queue backfills while keeping spread, with a
    /// ceiling of 3 "light" / 4 "heavy" per artist over the whole mix. Jitter
    /// guarantees repeat generations differ.
    /// </summary>
    internal static List<WaveItem> RankCandidates(
        IReadOnlyList<WaveItem> candidates,
        Dictionary<string, double> artistPlayCounts,
        double maxPlayCount,
        Dictionary<string, double> candidateWeights,
        double maxCandidateWeight,
        int waveSize,
        int maxPerArtist,
        Random rng,
        HashSet<string>? demotedArtistKeys = null)
    {
        var scored = candidates.Select(c =>
        {
            var weightNorm = candidateWeights.TryGetValue(c.Source + ":" + c.PlatformId, out var w)
                             && maxCandidateWeight > 0
                ? w / maxCandidateWeight
                : 0;
            var poolBonus = SeedBonusWeight * weightNorm;
            var keys = TrackArtistKeys(c);
            // Bonus — based on the most-played artist of the track (a collaboration
            // with a favorite is relevant even if the lead is unknown).
            var artistBonus = keys.Count > 0
                ? keys.Max(k => ArtistBonus(artistPlayCounts, maxPlayCount, k))
                : 0;
            // Demotion of artists from recent mixes: rotate the head of the lineup.
            if (demotedArtistKeys != null && keys.Any(demotedArtistKeys.Contains))
                artistBonus *= DemotedArtistFactor;
            return new
            {
                Track = c,
                Score = 1.0 + poolBonus + artistBonus + rng.NextDouble() * JitterAmplitude,
                WeightNorm = weightNorm
            };
        }).ToList();

        var selected = new List<WaveItem>(Math.Min(waveSize, candidates.Count));
        var picked = new HashSet<WaveItem>();
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);

        // Selection in three passes with shared counters: each pass relaxes the limit
        // rather than resetting it, so a family cannot accumulate extra slots in backfill.
        void SelectPass(int extraPerArtist)
        {
            foreach (var item in scored.OrderByDescending(s => s.Score))
            {
                if (selected.Count >= waveSize) break;
                if (picked.Contains(item.Track)) continue;

                var keys = TrackArtistKeys(item.Track);
                if (keys.Count > 0 && extraPerArtist != int.MaxValue)
                {
                    // "Heavy" artists (the play_log top) get an extended limit.
                    // The limit is family-wide: a track counts against EACH of its
                    // artists — "X", "X & B" and "X feat. C" together cannot exceed
                    // the X family limit.
                    var baseCap = item.WeightNorm >= HeavyCapShare ? HeavyPerArtist : maxPerArtist;
                    var cap = baseCap + extraPerArtist;
                    if (keys.Any(k => perArtist.GetValueOrDefault(k) >= cap)) continue;
                    foreach (var k in keys) perArtist[k] = perArtist.GetValueOrDefault(k) + 1;
                }

                picked.Add(item.Track);
                selected.Add(item.Track);
            }
        }

        // Pass 1 — main limits: variety among the best material.
        SelectPass(0);
        // Pass 2 — relaxed limits (+1): the tail backfills while keeping spread;
        // an artist does not gather into a block.
        if (selected.Count < waveSize) SelectPass(BackfillExtraPerArtist);
        // Pass 3 — another +1 (ceiling of 3 "light" / 4 "heavy" per whole mix).
        // There is deliberately no "no limits" pass: a block of one artist in the
        // tail is worse than a mix that did not reach full length.
        if (selected.Count < waveSize) SelectPass(BackfillExtraPerArtist * 2);

        return SpreadByArtist(selected, rng);
    }

    /// <summary>
    /// Fans the selected tracks out by artist while PRESERVING ranking: a track is
    /// pushed further back if it shares families with any of the last
    /// ArtistSpreadWindow placed tracks ("every other track" is not always possible).
    /// When all remaining options conflict (thin pool), at least avoid the same artist
    /// as the previous track — with two families this yields alternation instead of a
    /// clumped block. Adjacency remains only where unavoidable (all remaining are one
    /// family).
    /// </summary>
    internal static List<WaveItem> SpreadByArtist(IReadOnlyList<WaveItem> selected, Random rng)
    {
        if (selected.Count < 3) return selected.ToList();

        var pending = new LinkedList<WaveItem>(selected);
        var result = new List<WaveItem>(selected.Count);
        // Family sets of the last ArtistSpreadWindow placed tracks.
        var window = new List<HashSet<string>>();

        while (pending.Count > 0)
        {
            LinkedListNode<WaveItem>? chosen = null;
            for (var node = pending.First; node != null; node = node.Next)
            {
                if (!HitsAny(node.Value)) { chosen = node; break; }
            }

            if (chosen == null)
            {
                var last = window[^1];
                for (var node = pending.First; node != null; node = node.Next)
                {
                    var keys = TrackArtistKeys(node.Value);
                    if (keys.Count == 0 || !keys.Any(last.Contains)) { chosen = node; break; }
                }
                chosen ??= pending.First!; // everything left is one family
            }

            var chosenKeys = TrackArtistKeys(chosen.Value).ToHashSet(StringComparer.Ordinal);
            window.Add(chosenKeys);
            if (window.Count > ArtistSpreadWindow) window.RemoveAt(0);
            result.Add(chosen.Value);
            pending.Remove(chosen);
        }
        return result;

        bool HitsAny(WaveItem track)
        {
            var keys = TrackArtistKeys(track);
            return keys.Count != 0 && keys.Any(k => window.Any(w => w.Contains(k)));
        }
    }

    /// <summary>Spreading window: a family does not repeat among the last N placed
    /// tracks. N=3: "every other track" is impossible, and the two most-played artists
    /// cannot ping-pong at the head of the mix — the third-ranked artist joins as early
    /// as the third track (A,B,C,A,D,…), rather than after the paired slots run out.</summary>
    internal const int ArtistSpreadWindow = 3;

    /// <summary>Whether the lead (first) artist matches between two tracks — for spread
    /// tests and diagnostics.</summary>
    /// <summary>Weighted sample without repetition: an element with twice the weight is
    /// picked roughly twice as often. Zero/negative weights are clamped to a minimum.
    /// </summary>
    internal static List<T> WeightedSample<T>(IReadOnlyList<T> items, Func<T, double> weight, int count, Random rng)
    {
        var pool = items.ToList();
        var result = new List<T>(Math.Min(count, pool.Count));
        while (result.Count < count && pool.Count > 0)
        {
            var weights = new double[pool.Count];
            double total = 0;
            for (var i = 0; i < pool.Count; i++)
            {
                weights[i] = Math.Max(1e-6, weight(pool[i]));
                total += weights[i];
            }

            var roll = rng.NextDouble() * total;
            var acc = 0.0;
            var picked = pool.Count - 1;
            for (var i = 0; i < pool.Count; i++)
            {
                acc += weights[i];
                if (roll <= acc)
                {
                    picked = i;
                    break;
                }
            }
            result.Add(pool[picked]);
            pool.RemoveAt(picked);
        }
        return result;
    }

    private static IEnumerable<string> LibraryArtistKeys(
        params IEnumerable<string>[] artistSequences)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in artistSequences)
            foreach (var artist in sequence)
                foreach (var key in ArtistKeysOf(artist))
                    keys.Add(key);
        return keys;
    }

    /// <summary>Key of the "artist|title" pair for matching play_log against wave_suggested
    /// (matches the artist_key/title_key schema in wave_suggested). null — empty parts.</summary>
    private static string? PlayKey(string artist, string title)
    {
        var artistKey = ArtistKeysOf(artist).FirstOrDefault() ?? string.Empty;
        var titleKey = MatchHelper.Normalize(title);
        return artistKey.Length == 0 || titleKey.Length == 0 ? null : artistKey + "|" + titleKey;
    }
}
