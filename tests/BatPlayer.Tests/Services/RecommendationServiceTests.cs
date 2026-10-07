using BatPlayer.Services;
using BatPlayer.Services.YandexMusic;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Offline tests for "My Wave": /search and /similar parsing (YmJsonParser), search match
/// picking, artist affinity, weighted ranking with per-artist caps, weighted sampling and
/// spread. Pure functions.
/// </summary>
public class RecommendationServiceTests
{
    private static YmTrackDto Track(string id, string title, string artist,
                                    long durationMs = 200_000, bool available = true)
        => new()
        {
            Id = id,
            Title = title,
            Artist = artist,
            DurationMs = durationMs,
            CoverUri = "avatars.yandex.net/get-music-misc/%%",
            Available = available
        };

    private static WaveItem Item(string source, string id, string artist, string title)
        => new()
        {
            Source = source,
            PlatformId = id,
            Title = title,
            Artist = artist,
            DurationMs = 200_000
        };

    /// <summary>Yandex candidate for ranking tests (weights keyed by "yandex:{id}").</summary>
    private static WaveItem Wave(string id, string artist, string title)
        => Item("yandex", id, artist, title);

    // ============================ ParseSearchTracks ============================

    [Fact]
    public void ParseSearchTracks_StandardLayout_ParsesResults()
    {
        var json = """
            {"result":{"tracks":{"total":2,"results":[
                {"id":"101","title":"Song One","durationMs":200000,"available":true,
                 "artists":[{"name":"Artist A"}],"albums":[{"coverUri":"a%%"}]},
                {"id":"102","title":"Song Two","durationMs":190000,"available":false,
                 "artists":[{"name":"Artist B"},{"name":"Artist C"}],"albums":[{"coverUri":"b%%"}]}]}}}
            """;

        var tracks = YmJsonParser.ParseSearchTracks(json);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("101", tracks[0].Id);
        Assert.Equal("Artist A", tracks[0].Artist);
        Assert.True(tracks[0].Available);
        Assert.Equal("Artist B, Artist C", tracks[1].Artist);
        Assert.False(tracks[1].Available);
    }

    [Fact]
    public void ParseSearchTracks_Garbage_ReturnsEmpty()
    {
        Assert.Empty(YmJsonParser.ParseSearchTracks("<html>not json</html>"));
        Assert.Empty(YmJsonParser.ParseSearchTracks(null));
    }

    // ============================ ParseSimilarTracks ============================

    [Fact]
    public void ParseSimilarTracks_ResultObjectWithSimilarTracks_ParsesAndSkipsSeed()
    {
        var json = """
            {"result":[
              {"type":"track","id":"555","title":"Seed Track","available":true,
               "artists":[{"name":"Seed Artist"}],
               "similarTracks":[
                 {"id":"901","title":"Similar One","durationMs":180000,
                  "artists":[{"name":"Other Artist"}],"albums":[{"coverUri":"c%%"}]},
                 {"id":"555","title":"Seed Track","durationMs":200000,
                  "artists":[{"name":"Seed Artist"}]}]}]}
            """;

        var tracks = YmJsonParser.ParseSimilarTracks(json, seedYmId: "555");

        // Seed excluded: only the similar track becomes a candidate.
        var track = Assert.Single(tracks);
        Assert.Equal("901", track.Id);
        Assert.Equal("Other Artist", track.Artist);
        Assert.Equal("c%%", track.CoverUri);
    }

    [Fact]
    public void ParseSimilarTracks_DuplicatesCollapseById()
    {
        var json = """
            {"result":[
              {"similarTracks":[
                 {"id":"901","title":"A","artists":[{"name":"X"}]},
                 {"id":"901","title":"A","artists":[{"name":"X"}]},
                 {"id":"902","title":"B","artists":[{"name":"Y"}]}]}]}
            """;

        var tracks = YmJsonParser.ParseSimilarTracks(json, seedYmId: null);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(1, tracks.Count(t => t.Id == "901")); // duplicate by id collapsed
        Assert.Contains(tracks, t => t.Id == "902");
    }

    // ============================ PickSearchMatch ============================

    [Fact]
    public void PickSearchMatch_ExactTitleAndArtist_ReturnsMatch()
    {
        var results = new List<YmTrackDto>
        {
            Track("1", "Song (Remix)", "Wrong Artist"),
            Track("2", "My Song", "Seed Artist")
        };

        var match = RecommendationService.PickSearchMatch(results, "Seed Artist", "My Song", 200_000);

        Assert.NotNull(match);
        Assert.Equal("2", match!.Id);
    }

    [Fact]
    public void PickSearchMatch_TitleNormalized_MatchesIgnoringCaseAndBrackets()
    {
        var results = new List<YmTrackDto> { Track("7", "my  song!", "another & Seed Artist") };

        var match = RecommendationService.PickSearchMatch(results, "Seed Artist", "My Song", 0);

        Assert.NotNull(match);
        Assert.Equal("7", match!.Id);
    }

    [Fact]
    public void PickSearchMatch_DurationBeyond15s_Skipped()
    {
        var results = new List<YmTrackDto>
        {
            Track("1", "My Song", "Seed Artist", durationMs: 300_000) // 100s apart
        };

        Assert.Null(RecommendationService.PickSearchMatch(results, "Seed Artist", "My Song", 200_000));
    }

    [Fact]
    public void PickSearchMatch_NoArtistOverlap_ReturnsNull()
    {
        var results = new List<YmTrackDto> { Track("1", "My Song", "Totally Other") };

        Assert.Null(RecommendationService.PickSearchMatch(results, "Seed Artist", "My Song", 200_000));
    }

    // ============================ ComputeAffinity ============================

    [Fact]
    public void ArtistBonus_RelativeToTopArtist()
    {
        var counts = new Dictionary<string, double> { ["top"] = 50, ["mid"] = 7, ["rare"] = 1 };

        // Bonus is relative: 7 plays against a top of 50 give log2(8)/log2(51) ≈ 0.3 of max,
        // not a near-max: barely-played artists no longer displace played ones.
        var top = RecommendationService.ArtistBonus(counts, 50, "top");
        var mid = RecommendationService.ArtistBonus(counts, 50, "mid");
        var rare = RecommendationService.ArtistBonus(counts, 50, "rare");
        var none = RecommendationService.ArtistBonus(counts, 50, "unknown");

        Assert.Equal(1.2, top, 1);       // top — full bonus
        Assert.True(mid < top * 0.6);    // 7 plays — ~53% log ratio
        Assert.True(rare < top * 0.2);   // 1 play — almost nothing
        Assert.Equal(0, none);
    }

    [Fact]
    public void ComputePlayCounts_SuggestedPlaysAreDiscounted()
    {
        var now = DateTime.UtcNow;
        var plays = new List<(string, string, DateTime)>
        {
            ("Wave Fed Artist", "Given Track", now),
            ("Wave Fed Artist", "Given Track", now),
            ("Wave Fed Artist", "Given Track", now),
            ("Organic Artist", "Real Jam", now),
        };
        var suggestedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "wave fed artist|given track" // same key as wave_suggested
        };

        var counts = RecommendationService.ComputePlayCounts(plays, suggestedKeys);

        // 3 suggested plays weigh 0.75 (3.0 undiscounted) — less than one organic play.
        Assert.Equal(0.75, counts["wave fed artist"], 2);
        Assert.Equal(1.0, counts["organic artist"], 1);
        Assert.True(counts["organic artist"] > counts["wave fed artist"]);
    }

    [Fact]
    public void ComputeRawPlayCounts_IgnoresDiscount()
    {
        // Raw counts do not discount suggested plays: taste/fame thresholds must not
        // depend on the play source.
        var now = DateTime.UtcNow;
        var plays = new List<(string, string, DateTime)>
        {
            ("Wave Fed Artist", "Given Track", now),
            ("Wave Fed Artist", "Given Track", now),
            ("Wave Fed Artist", "Given Track", now),
        };
        var suggestedKeys = new HashSet<string>(StringComparer.Ordinal) { "wave fed artist|given track" };

        var raw = RecommendationService.ComputeRawPlayCounts(plays);
        var weighted = RecommendationService.ComputePlayCounts(plays, suggestedKeys);

        Assert.Equal(3.0, raw["wave fed artist"], 1);
        Assert.Equal(0.75, weighted["wave fed artist"], 2);
    }

    [Fact]
    public void SpreadByArtist_PreservesRankingOrder()
    {
        // Ranking order preserved: only a track whose artist repeats the previous one moves.
        var selected = new List<WaveItem>
        {
            Item("yandex", "a1", "Artist A", "T1"),
            Item("yandex", "a2", "Artist A", "T2"),
            Item("yandex", "b1", "Artist B", "T3"),
            Item("yandex", "a3", "Artist A", "T4"),
            Item("yandex", "c1", "Artist C", "T5"),
        };

        var spread = RecommendationService.SpreadByArtist(selected, new Random(1));

        // Window 2: A never returns within the last two tracks — a2 moves behind c1.
        Assert.Equal("a1", spread[0].PlatformId);
        Assert.Equal("b1", spread[1].PlatformId);
        Assert.Equal("c1", spread[2].PlatformId);
        Assert.Equal("a2", spread[3].PlatformId);
        Assert.Equal(5, spread.Count);
    }

    // ============================ RankCandidates ============================

    private static Dictionary<string, double> NoSeeds()
        => new(StringComparer.Ordinal);

    [Fact]
    public void RankCandidates_PerArtistCap_Applied()
    {
        var candidates = Enumerable.Range(0, 10)
            .Select(i => Wave($"id{i}", "One Artist", $"Song{i}"))
            .Concat(new[] { Wave("x1", "Another Artist", "Other") })
            .ToList();
        var rng = new Random(42);

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 4, maxPerArtist: 3, rng: rng,
            demotedArtistKeys: null);

        Assert.Equal(4, selected.Count);           // 3 x "One Artist" + 1 x "Another Artist"
        Assert.Equal(3, selected.Count(t => t.Artist == "One Artist"));
    }

    [Fact]
    public void RankCandidates_AffinityRises_AboveJitter()
    {
        var liked = Wave("liked", "Beloved Artist", "Liked");
        var other = Wave("other", "Unknown Artist", "Other");
        var rng = new Random(7);
        var playCounts = new Dictionary<string, double> { ["beloved artist"] = 50 };

        // Jitter ≤ 0.3 vs top-artist bonus 1.2: the favorite always ranks first.
        for (var i = 0; i < 20; i++)
        {
            var selected = RecommendationService.RankCandidates(
                new[] { liked, other }, playCounts, 50, NoSeeds(), 0,
                waveSize: 2, maxPerArtist: 3, rng,
            demotedArtistKeys: null);
            Assert.Equal("liked", selected[0].PlatformId);
        }
    }

    [Fact]
    public void RankCandidates_SeedWeightPulls_Up()
    {
        // Both candidates are unknown artists, but the first carries max seed weight:
        // it must always outrank jitter.
        var heavy = Wave("heavy", "Brand New Artist", "Heavy");
        var light = Wave("light", "Another New Artist", "Light");
        var rng = new Random(11);
        var seedWeights = new Dictionary<string, double> { ["yandex:heavy"] = 5.0 };

        for (var i = 0; i < 20; i++)
        {
            var selected = RecommendationService.RankCandidates(
                new[] { heavy, light }, new Dictionary<string, double>(), 0, seedWeights, 5.0,
                waveSize: 2, maxPerArtist: 3, rng,
            demotedArtistKeys: null);
            Assert.Equal("heavy", selected[0].PlatformId);
        }
    }

    [Fact]
    public void RankCandidates_WaveSizeLimit_Applied()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => Wave($"id{i}", $"Artist{i % 5}", $"Song{i}"))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 10, maxPerArtist: 10, new Random(1),
            demotedArtistKeys: null);

        Assert.Equal(10, selected.Count);
    }

    // ============================ WeightedSample ============================

    [Fact]
    public void WeightedSample_HeavyItemsDominates()
    {
        var items = new[] { "heavy", "light1", "light2", "light3", "light4" };
        var rng = new Random(3);

        var heavyWins = 0;
        for (var trial = 0; trial < 200; trial++)
        {
            var sample = RecommendationService.WeightedSample(
                items, item => item == "heavy" ? 20.0 : 1.0, count: 1, rng);
            if (sample[0] == "heavy") heavyWins++;
        }

        // Weight 20 of 24 → ~83%; in 200 fair trials a deviation below 65% is
        // practically impossible.
        Assert.True(heavyWins > 130, $"heavy picked only {heavyWins}/200");
    }

    [Fact]
    public void WeightedSample_NoRepeats_AndRespectsCount()
    {
        var items = Enumerable.Range(0, 10).Select(i => $"i{i}").ToList();
        var sample = RecommendationService.WeightedSample(items, _ => 1.0, count: 25, new Random(5));

        Assert.Equal(10, sample.Count); // count > pool → whole pool without repeats
        Assert.Equal(10, sample.Distinct().Count());
    }

    // ===================== Artist and catalog parsers =====================

    [Fact]
    public void ParseArtistTracks_ResultTracks_ParsesTracks()
    {
        var json = """
            {"result":{"pager":{"total":21,"page":0,"perPage":20},
             "tracks":[
               {"id":"301","title":"Old Track","durationMs":190000,
                "artists":[{"name":"Top Artist"}],"albums":[{"coverUri":"x%%"}]},
               {"id":"302","title":"New Track","durationMs":180000,"available":false,
                "artists":[{"name":"Top Artist, Friend"}]}]}}
            """;

        var tracks = YmJsonParser.ParseArtistTracks(json);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("301", tracks[0].Id);
        Assert.Equal("Top Artist, Friend", tracks[1].Artist);
        Assert.False(tracks[1].Available);
    }

    [Fact]
    public void ParseSimilarArtists_ResultArray_ParsesAndSkipsSelf()
    {
        var json = """
            {"result":{"artist":{"id":"555","name":"Seed Artist"},
             "similarArtists":[
               {"id":"801","name":"Scene One"},
               {"id":"555","name":"Seed Artist"},
               {"id":"802","name":"Scene Two"}]}}
            """;

        var artists = YmJsonParser.ParseSimilarArtists(json, artistId: "555");

        Assert.Equal(2, artists.Count);
        Assert.DoesNotContain(artists, a => a.Id == "555");
        Assert.Equal("Scene One", artists[0].Name);
    }

    [Fact]
    public void ParseSearchArtists_StandardLayout_ParsesResults()
    {
        var json = """
            {"result":{"artists":{"total":3,"results":[
                {"id":"11","name":"madk1d"},
                {"id":"12","name":"VILLIAN"}]}}}
            """;

        var artists = YmJsonParser.ParseSearchArtists(json);

        Assert.Equal(2, artists.Count);
        Assert.Equal("11", artists[0].Id);
        Assert.Equal("madk1d", artists[0].Name);
    }

    [Fact]
    public void ParseArtistListeners_BriefInfo_ReadsStatsListeners()
    {
        // Real brief-info layout: result.stats.lastMonthListeners.
        var json = """
            {"result":{"artist":{"id":"555","name":"Seed Artist"},
             "stats":{"usedTracksCount":21,"directAlbumsCount":3,
                      "lastMonthListeners":48213,"lastMonthListenersDelta":194}}}
            """;

        Assert.Equal(48213, YmJsonParser.ParseArtistListeners(json));
        // Fallback to the legacy field name.
        Assert.Equal(777, YmJsonParser.ParseArtistListeners(
            "{\"result\":{\"stats\":{\"listeners\":777}}}"));
        Assert.Null(YmJsonParser.ParseArtistListeners("{\"result\":{}}")); // no field
        Assert.Null(YmJsonParser.ParseArtistListeners("not json"));       // garbage
    }

    [Fact]
    public void CollectArtistObjects_LabelsAreNotArtists()
    {
        // label has id+name but no artist markers — excluded from artists
        var json = """
            {"result":{"something":{"label":{"id":"9","name":"Fake Label"}},
             "artistList":[{"id":"13","name":"Real One","cover":{"type":"from-artist-photos"}}]}}
            """;

        var artists = YmJsonParser.ParseSearchArtists(json); // exact path missed → DFS

        var artist = Assert.Single(artists);
        Assert.Equal("13", artist.Id);
    }

    [Fact]
    public void ParseRadioTracks_SequenceTracks_ParsesLeniently()
    {
        var json = """
            {"result":{"id":{"type":"artist","tag":"13992820"},
             "batchId":"abc","pumpkin":false,
             "sequence":[
               {"__type":"track","track":{"id":"701","title":"Radio One","durationMs":200000,
                  "artists":[{"name":"Scene Artist"}],"albums":[{"coverUri":"r%%"}],"available":true}},
               {"__type":"track","track":{"id":"702","title":"Radio Two","durationMs":190000,
                  "artists":[{"name":"Another"}],"albums":[{"coverUri":"s%%"}],"available":true}}]}}
            """;

        var tracks = YmJsonParser.ParseRadioTracks(json);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("701", tracks[0].Id);
        Assert.Equal("Scene Artist", tracks[0].Artist);
    }

    [Fact]
    public void PickArtistMatch_ExactThenContainment()
    {
        var results = new List<YmArtistDto>
        {
            new() { Id = "1", Name = "Wrong Artist" },
            new() { Id = "2", Name = "madk1d" }
        };

        Assert.Equal("2", RecommendationService.PickArtistMatch(results, "Madk1D")!.Id);
        Assert.Equal("2", RecommendationService.PickArtistMatch(results, "madk1d & crew")!.Id); // broader query
        Assert.Null(RecommendationService.PickArtistMatch(results, "totally other"));
    }

    [Fact]
    public void RankCandidates_HeavyArtistsGetBiggerCap()
    {
        // 2 tracks by one artist with max pool weight (top play_log): the "heavy"
        // cap is 2 — both fit; the "light" cap of 1 would cut some.
        var candidates = Enumerable.Range(0, 2)
            .Select(i => Wave($"id{i}", "Top Playlog Artist", $"Song{i}"))
            .ToList();
        var weights = candidates.ToDictionary(c => "yandex:" + c.PlatformId, _ => 10.0);

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, weights, 10.0,
            waveSize: 10, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void RankCandidates_LightArtistsStillCapped()
    {
        // The "light" cap of 1 applies in the main pass: one of 6 novelty tracks is
        // chosen, the remaining slots go to other artists.
        var candidates = Enumerable.Range(0, 6)
            .Select(i => Wave($"id{i}", "Random New Artist", $"Song{i}"))
            .Concat(Enumerable.Range(0, 5)
                .Select(i => Wave($"f{i}", $"Filler {i}", $"Other{i}")))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, new Dictionary<string, double>(), 0,
            waveSize: 4, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(4, selected.Count);
        Assert.Equal(1, selected.Count(t => t.Artist == "Random New Artist"));
    }

    [Fact]
    public void RankCandidates_CapsSaturated_BackfillsToFullLength()
    {
        // Caps saturated but queue shorter than waveSize: backfill passes relax the
        // limit to 3 per family — full-length mix with enough variety.
        var candidates = Enumerable.Range(0, 7)
            .SelectMany(a => Enumerable.Range(0, 5)
                .Select(i => Wave($"a{a}t{i}", $"Family {a}", $"Song{a}-{i}")))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 20, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(20, selected.Count);
        Assert.Equal(20, selected.Distinct().Count());
        Assert.All(selected.GroupBy(t => t.Artist).Select(g => g.Count()),
            c => Assert.True(c <= 3, $"family reached {c} > 3"));
    }

    [Fact]
    public void RankCandidates_SingleArtistPool_CappedAtThree()
    {
        // Single-artist pool: backfill raises the cap to at most 3 — the mix never
        // becomes one album, even with no other artists.
        var candidates = Enumerable.Range(0, 6)
            .Select(i => Wave($"id{i}", "One Artist", $"Song{i}"))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 20, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(3, selected.Count);
    }

    [Fact]
    public void RankCandidates_Backfill_RespectsWaveSize()
    {
        // Backfill stays within waveSize: one slot per artist, then +1 and +2 per
        // family — exactly 8.
        var candidates = Enumerable.Range(0, 10)
            .Select(i => Wave($"id{i}", "One Artist", $"Song{i}"))
            .Concat(Enumerable.Range(0, 6)
                .Select(i => Wave($"b{i}", "Artist B", $"Tune{i}")))
            .Concat(Enumerable.Range(0, 4)
                .Select(i => Wave($"c{i}", "Artist C", $"Track{i}")))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 8, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(8, selected.Count);
        Assert.Equal(8, selected.Distinct().Count());
    }

    [Fact]
    public void RankCandidates_Backfill_KeepsArtistSpread()
    {
        // Backfill relaxes the limit (1 + BackfillExtraPerArtist) instead of removing it:
        // 6 families of 6 fill a 12-mix at most 2 per family — the tail never becomes
        // one artist block.
        var candidates = Enumerable.Range(0, 6)
            .SelectMany(a => Enumerable.Range(0, 6)
                .Select(i => Wave($"a{a}t{i}", $"Family {a}", $"Song{a}-{i}")))
            .ToList();

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, NoSeeds(), 0,
            waveSize: 12, maxPerArtist: 1, new Random(4),
            demotedArtistKeys: null);

        Assert.Equal(12, selected.Count);
        Assert.All(selected.GroupBy(t => t.Artist).Select(g => g.Count()),
            c => Assert.True(c <= 1 + RecommendationService.BackfillExtraPerArtist,
                $"family reached {c} tracks — backfill must not remove the limit"));
    }

    // ============================ IsSeedPlayed ============================

    [Fact]
    public void IsSeedPlayed_PlayedArtistOrTrack_True()
    {
        var counts = new Dictionary<string, double> { ["known artist"] = 5 };
        var playKeys = new HashSet<string>(StringComparer.Ordinal) { "other artist|some title" };

        Assert.True(RecommendationService.IsSeedPlayed("Known Artist", "Anything", counts, playKeys));
        Assert.True(RecommendationService.IsSeedPlayed(
            "feat & Known Artist", "Anything", counts, playKeys)); // co-author counts too
        Assert.True(RecommendationService.IsSeedPlayed(
            "Other Artist", "Some Title!", counts, playKeys)); // exact track play
        Assert.False(RecommendationService.IsSeedPlayed("Never Played", "New Song", counts, playKeys));
    }

    [Fact]
    public void SpreadByArtist_SameArtistTracksNotAdjacent()
    {
        // 3 tracks by artist A (top rated) + one each B and C: at most two of the same
        // in a row, A tracks spread across the list.
        var selected = new List<WaveItem>
        {
            Item("yandex", "a1", "Artist A", "T1"),
            Item("yandex", "a2", "Artist A & B", "T2"),
            Item("yandex", "b1", "Artist B", "T4"),
            Item("yandex", "c1", "Artist C", "T5"),
            Item("yandex", "d1", "Artist D", "T6"),
            Item("yandex", "e1", "Artist E", "T7"),
        };

        var spread = RecommendationService.SpreadByArtist(selected, new Random(1));

        Assert.Equal(6, spread.Count);
        // Family check: adjacent tracks share no artists (after stripping feats) —
        // "X & Y" and "X" count as one family.
        for (var i = 1; i < spread.Count; i++)
        {
            var prevKeys = RecommendationService.TrackArtistKeys(spread[i - 1]);
            var curKeys = RecommendationService.TrackArtistKeys(spread[i]);
            Assert.False(prevKeys.Any(curKeys.Contains),
                $"adjacent same family at {i}: {spread[i - 1].Artist} / {spread[i].Artist}");
        }
    }

    [Fact]
    public void SpreadByArtist_ShortList_ReturnsAsIs()
    {
        var selected = new List<WaveItem>
        {
            Item("yandex", "a1", "A", "T1"),
            Item("yandex", "a2", "A", "T2"),
        };

        var spread = RecommendationService.SpreadByArtist(selected, new Random(1));

        Assert.Equal(2, spread.Count);
    }

    [Fact]
    public void SpreadByArtist_ForcedTail_TwoFamiliesAlternate()
    {
        // Tail of two families, all conflicting with the window: even in the forced
        // case the spread alternates A/B instead of one artist block.
        var selected = new List<WaveItem>
        {
            Item("yandex", "a1", "Artist A", "T1"),
            Item("yandex", "a2", "Artist A", "T2"),
            Item("yandex", "a3", "Artist A", "T3"),
            Item("yandex", "b1", "Artist B", "T4"),
            Item("yandex", "b2", "Artist B", "T5"),
            Item("yandex", "b3", "Artist B", "T6"),
        };

        var spread = RecommendationService.SpreadByArtist(selected, new Random(1));

        Assert.Equal(6, spread.Count);
        for (var i = 1; i < spread.Count; i++)
            Assert.NotEqual(
                RecommendationService.TrackArtistKeys(spread[i - 1]).FirstOrDefault(),
                RecommendationService.TrackArtistKeys(spread[i]).FirstOrDefault());
    }

    [Fact]
    public void RankCandidates_DemotedArtistRanksBelow()
    {
        // An artist from the recent mix (demotion x0.25) loses to a competitor with
        // half the bonus: the next mix head changes.
        var recent = Wave("recent", "Recently Played Artist", "R1");
        var fresh = Wave("fresh", "Resting Artist", "F1");
        var rng = new Random(9);
        var playCounts = new Dictionary<string, double>
        {
            ["recently played artist"] = 50,
            ["resting artist"] = 10
        };
        var demoted = new HashSet<string>(StringComparer.Ordinal) { "recently played artist" };

        var selected = RecommendationService.RankCandidates(
            new[] { recent, fresh }, playCounts, 50, NoSeeds(), 0,
            waveSize: 2, maxPerArtist: 3, rng, demotedArtistKeys: demoted);

        // Without demotion: recent 1.2 > fresh 0.48. With demotion: 0.3 < 0.48.
        Assert.Equal("fresh", selected[0].PlatformId);
    }

    [Fact]
    public void RankCandidates_FeatVariantsShareFamilyCap()
    {
        // "X", "X & B", "X feat. C" — one family X: only 2 of 4 variants are chosen
        // (heavy cap), the remaining slots go to other artists.
        var candidates = new List<WaveItem>
        {
            Wave("x1", "X", "Solo"),
            Wave("x2", "X & B", "Collab"),
            Wave("x3", "X feat. C", "Featuring"),
            Wave("x4", "X (with D)", "With"),
            Wave("d1", "Artist D", "Other1"),
            Wave("e1", "Artist E", "Other2"),
        };
        var weights = candidates.ToDictionary(c => "yandex:" + c.PlatformId, _ => 10.0);

        var selected = RecommendationService.RankCandidates(
            candidates, new Dictionary<string, double>(), 0, weights, 10.0,
            waveSize: 4, maxPerArtist: 1, new Random(4), demotedArtistKeys: null);

        Assert.Equal(2, selected.Count(t => t.Artist.StartsWith("X")));
        Assert.Contains(selected, t => t.Artist == "Artist D");
        Assert.Contains(selected, t => t.Artist == "Artist E");
    }
}
