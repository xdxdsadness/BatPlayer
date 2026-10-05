using BatPlayer.Services;
using BatPlayer.Services.YandexMusic;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты «Моей волны» без сети: разбор /search и /similar (YmJsonParser), выбор
/// матча поиска (RecommendationService.PickSearchMatch), аффинность исполнителей,
/// ранжирование с весами сидов и лимитом на исполнителя, взвешенный сэмпл и
/// чередование «реанимации» (RecommendationService). Чистые функции.
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

    /// <summary>Яндекс-кандидат для тестов ранжирования (веса ключуются «yandex:{id}»).</summary>
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

        // Сид исключён: в кандидаты попадает только похожий трек.
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
        Assert.Equal(1, tracks.Count(t => t.Id == "901")); // дубликат по id схлопнулся
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
            Track("1", "My Song", "Seed Artist", durationMs: 300_000) // 100 c разница
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

        // Бонус относителен: артист с 7 прослушками против топа с 50 получает
        // log2(8)/log2(51) ≈ 0.3 от максимума, а не насыщенный почти-максимум —
        // артисты с парой прослушек больше не вытесняют играемых.
        var top = RecommendationService.ArtistBonus(counts, 50, "top");
        var mid = RecommendationService.ArtistBonus(counts, 50, "mid");
        var rare = RecommendationService.ArtistBonus(counts, 50, "rare");
        var none = RecommendationService.ArtistBonus(counts, 50, "unknown");

        Assert.Equal(1.2, top, 1);       // топ — полный бонус
        Assert.True(mid < top * 0.6);    // 7 прослушек — ~53% отношения логарифмов
        Assert.True(rare < top * 0.2);   // 1 прослушка — почти ничего
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
            "wave fed artist|given track" // как в wave_suggested
        };

        var counts = RecommendationService.ComputePlayCounts(plays, suggestedKeys);

        // 3 предложенных прослушки весят 3×0.25=0.75 (без дисконта было бы 3.0) —
        // теперь они меньше одной органической: пассивное прослушивание почти
        // не становится вкусом.
        Assert.Equal(0.75, counts["wave fed artist"], 2);
        Assert.Equal(1.0, counts["organic artist"], 1);
        Assert.True(counts["organic artist"] > counts["wave fed artist"]);
    }

    [Fact]
    public void ComputeRawPlayCounts_IgnoresDiscount()
    {
        // Сырые счётчики не дисконтируют прослушки из микса: пороги «сильного вкуса»
        // и «известности» не должны зависеть от источника прослушивания.
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
        // Ранжирующий порядок сохраняется: сдвигается только трек, чей исполнитель
        // совпал с предыдущим. Два «гиганта» больше не чередуются в голове.
        var selected = new List<WaveItem>
        {
            Item("yandex", "a1", "Artist A", "T1"),
            Item("yandex", "a2", "Artist A", "T2"),
            Item("yandex", "b1", "Artist B", "T3"),
            Item("yandex", "a3", "Artist A", "T4"),
            Item("yandex", "c1", "Artist C", "T5"),
        };

        var spread = RecommendationService.SpreadByArtist(selected, new Random(1));

        // Окно 2: A не возвращается в последних двух треках — a2 уходит за c1.
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

        Assert.Equal(4, selected.Count);           // 3 × «One Artist» + 1 × «Another Artist»
        Assert.Equal(3, selected.Count(t => t.Artist == "One Artist"));
    }

    [Fact]
    public void RankCandidates_AffinityRises_AboveJitter()
    {
        var liked = Wave("liked", "Beloved Artist", "Liked");
        var other = Wave("other", "Unknown Artist", "Other");
        var rng = new Random(7);
        var playCounts = new Dictionary<string, double> { ["beloved artist"] = 50 };

        // Джиттер ≤ 0.3, а бонус топ-артиста = 1.2: любимый исполнитель всегда первый.
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
        // Оба кандидата незнакомых исполнителей, но у первого вес сида максимальный,
        // у второго — ноль: первый должен всегда выходить выше джиттера.
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

        // Вес 20 из суммы 24 → ~83%; при честном сэмплинге в 200 испытаниях
        // отклонение за 65% практически невозможно.
        Assert.True(heavyWins > 130, $"heavy picked only {heavyWins}/200");
    }

    [Fact]
    public void WeightedSample_NoRepeats_AndRespectsCount()
    {
        var items = Enumerable.Range(0, 10).Select(i => $"i{i}").ToList();
        var sample = RecommendationService.WeightedSample(items, _ => 1.0, count: 25, new Random(5));

        Assert.Equal(10, sample.Count); // count > pool → весь пул без повторов
        Assert.Equal(10, sample.Distinct().Count());
    }

    // ========================= Парсеры исполнителей и каталога =========================

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
        // Реальная структура brief-info: result.stats.lastMonthListeners.
        var json = """
            {"result":{"artist":{"id":"555","name":"Seed Artist"},
             "stats":{"usedTracksCount":21,"directAlbumsCount":3,
                      "lastMonthListeners":48213,"lastMonthListenersDelta":194}}}
            """;

        Assert.Equal(48213, YmJsonParser.ParseArtistListeners(json));
        // Фолбэк на прежнее имя поля.
        Assert.Equal(777, YmJsonParser.ParseArtistListeners(
            "{\"result\":{\"stats\":{\"listeners\":777}}}"));
        Assert.Null(YmJsonParser.ParseArtistListeners("{\"result\":{}}")); // поля нет
        Assert.Null(YmJsonParser.ParseArtistListeners("not json"));       // мусор
    }

    [Fact]
    public void CollectArtistObjects_LabelsAreNotArtists()
    {
        // label имеет пару id+name, но без признаков артиста — в исполнители не попадает
        var json = """
            {"result":{"something":{"label":{"id":"9","name":"Fake Label"}},
             "artistList":[{"id":"13","name":"Real One","cover":{"type":"from-artist-photos"}}]}}
            """;

        var artists = YmJsonParser.ParseSearchArtists(json); // точный путь не сошёлся → DFS

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
        Assert.Equal("2", RecommendationService.PickArtistMatch(results, "madk1d & crew")!.Id); // запрос шире
        Assert.Null(RecommendationService.PickArtistMatch(results, "totally other"));
    }

    [Fact]
    public void RankCandidates_HeavyArtistsGetBiggerCap()
    {
        // 2 трека одного исполнителя с максимальным весом пула (топ play_log):
        // у «тяжёлых» лимит 2 — оба попадают; «лёгкий» лимит 1 отрезал бы часть.
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
        // «Лёгкий» лимит 1 действует в основном проходе: из 6 треков новизны
        // выбирается один, остальные слоты уходят другим исполнителям.
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
        // Лимиты насытились, а очередь короче waveSize: проходы добора ослабляют
        // лимит до 3 на семейство — микс полной длины при достаточном разнообразии.
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
            c => Assert.True(c <= 3, $"семейство набрало {c} > 3"));
    }

    [Fact]
    public void RankCandidates_SingleArtistPool_CappedAtThree()
    {
        // Пул из одного исполнителя: добор поднимает лимит максимум до 3 —
        // микс не превращается в альбом, даже когда других исполнителей нет.
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
        // Добор не выходит за waveSize: по слоту на исполнителя, потом +1 и +2
        // на семейство — ровно до 8.
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
        // Добор ослабляет лимит (1 + BackfillExtraPerArtist), а не снимает:
        // 6 семейств по 6 треков заполняют микс из 12 максимум по 2 на семейство —
        // хвост не собирается в сплошной блок одного исполнителя.
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
                $"семейство набрало {c} треков — добор не должен снимать лимит"));
    }

    // ============================ IsSeedPlayed ============================

    [Fact]
    public void IsSeedPlayed_PlayedArtistOrTrack_True()
    {
        var counts = new Dictionary<string, double> { ["known artist"] = 5 };
        var playKeys = new HashSet<string>(StringComparer.Ordinal) { "other artist|some title" };

        Assert.True(RecommendationService.IsSeedPlayed("Known Artist", "Anything", counts, playKeys));
        Assert.True(RecommendationService.IsSeedPlayed(
            "feat & Known Artist", "Anything", counts, playKeys)); // соавтор тоже считается
        Assert.True(RecommendationService.IsSeedPlayed(
            "Other Artist", "Some Title!", counts, playKeys)); // точная прослушка трека
        Assert.False(RecommendationService.IsSeedPlayed("Never Played", "New Song", counts, playKeys));
    }

    [Fact]
    public void SpreadByArtist_SameArtistTracksNotAdjacent()
    {
        // 3 трека исполнителя A (лучшие по рейтингу) + по одному B и C:
        // подряд могут стоять максимум 2 разных, треки A разнесены по списку.
        // Семейный лимит 2 в бою: A максимум дважды — раскладка разводит по списку.
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
        // Семейная проверка: у соседних треков не пересекаются исполнители
        // (после отрезания фитов) — "X & Y" и "X" считаются одним семейством.
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
        // Хвост из двух семейств, все конфликтуют с окном: даже в форс-мажоре
        // раскладка чередует A/B, а не собирает блок одного артиста.
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
        // Артист недавнего микса (демоушн ×0.25) проигрывает конкуренту с вдвое
        // меньшим бонусом: голова следующего микса меняется.
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

        // Без демоушна: recent 1.2 > fresh 0.48. С демоушном: 0.3 < 0.48.
        Assert.Equal("fresh", selected[0].PlatformId);
    }

    [Fact]
    public void RankCandidates_FeatVariantsShareFamilyCap()
    {
        // "X", "X & B", "X feat. C" — одно семейство X: из 4 вариантов выбираются
        // только 2 (тяжёлый лимит), остальные слоты уходят другим артистам.
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
