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
/// Кандидат волны (все кандидаты сейчас приходят из графа Яндекса; источник сохранён
/// в модели на случай будущих пулов из других сервисов).
/// </summary>
public sealed class WaveItem
{
    public required string Source { get; init; }
    /// <summary>Идентификатор платформы: ym_id / vk_id / sc_id; для local — не используется.</summary>
    public required string PlatformId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Шаблон обложки с "%%" (ЯМ) или готовый URL (VK/SC); null — обложки нет.</summary>
    public string? CoverUri { get; init; }
    public bool Available { get; init; } = true;
    /// <summary>Для local — путь файла; для VK/SC — уже скачанная обложка; null по умолчанию.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Ключ журнала предложений (PK wave_suggested): у ЯМ — ym_id,
    /// у остальных — префикс источника, чтобы id разных платформ не сталкивались.</summary>
    public string JournalId => Source == Track.SourceYandex
        ? PlatformId
        : $"{Source}:{PlatformId}";
}

/// <summary>
/// «Моя волна»: локальная генерация рекомендаций по вкусу — без сервера. Анализ
/// идёт по ИСПОЛНИТЕЛЯМ и трекам, которые пользователь реально слушает:
///
///   A. «Треки слушаемых исполнителей» — топ play_log → /artists/{id}/tracks:
///      треки артистов, которых я слушаю, но ещё не добавил (самый весомый пул).
///   B. «Общая тусовка» — /artists/{id}/similar для топ-артистов: похожие
///      исполнители той же сцены, их треки тем же эндпоинтом.
///   C. «Радио сцены» — /rotor/station/artist:{id}/tracks: батч похожего звука
///      из ротора Яндекса, обновляется каждую генерацию.
///   D. «Похожие треки» — /tracks/{id}/similar по сидам из библиотек (ЯМ + VK/SC/
///      локальные, сопоставленные с ym_id через /search с кэшем в wave_seed_map).
///
/// Пулы сливаются с дедупликацией (знакомые исполнители выигрывают), фильтруются
/// от уже имеющегося/недавно игравшего/недавно предложенного и ранжируются: вес
/// пула + аффинность исполнителя по play_log + джиттер. Всё кэшируется (14 дней),
/// повторные генерации почти не ходят в сеть.
///
/// Обратная связь без хуков плеера: предложенное запоминается (wave_suggested);
/// трек, прослушанный после предложения, снова предлагается, а непрослушанные
/// предложения неделю не попадают в волну — пассивный негативный сигнал.
/// </summary>
public sealed class RecommendationService
{
    /// <summary>Сколько сидов участвует в пуле D (по ним запрашивается /similar).
    /// Каждый сид — путь к соседним исполнителям: чем их больше, тем шире круг.</summary>
    public const int SeedCount = 20;

    /// <summary>Размер микса (длина очереди). Очередь всегда полной длины: если
    /// знакомых кандидатов меньше, хвост добирается новизной (она ранжируется
    /// последней), а материал знакомых расширяется глубокими каталогами.</summary>
    public const int WaveSize = 20;

    /// <summary>Лимит треков одного «лёгкого» исполнителя в выдаче — иначе волна
    /// вырождается в альбом. Для «тяжёлых» (вес сида ≥ HeavyCapShare от максимума —
    /// т.е. исполнители из топа play_log) действует HeavyPerArtist: знакомое
    /// занимает большую часть волны, novelty — дозированный остаток.
    /// Лимиты работают в первых двух проходах отбора; если и ослабленные лимиты
    /// не заполняют очередь до WaveSize, финальный проход добирает без лимитов —
    /// короткий микс хуже вырожденного.</summary>
    public const int LightPerArtist = 1;
    public const int HeavyPerArtist = 2;
    internal const double HeavyCapShare = 0.55;

    /// <summary>Дополнительные слоты на исполнителя в проходе добора: хвост микса
    /// заполняется с ослабленным лимитом (а не снятым). +1 держит семейство в
    /// пределах 2 треков «лёгкому» / 3 «тяжёлому» за весь микс — серии одного
    /// исполнителя, даже разведённые раскладкой, воспринимаются как мусор.
    /// Финальный проход без лимитов включается, только если и этого не хватает
    /// до WaveSize при очень тонком пуле.</summary>
    internal const int BackfillExtraPerArtist = 1;

    /// <summary>Сколько топ-исполнителей play_log анализируется за генерацию.</summary>
    public const int MaxTopArtists = 20;

    /// <summary>Минимальные органические прослушки (сырые, без дисконта), чтобы
    /// артист считался «прослушанным» и попал в анализ каталога (пул A):
    /// 3-4 пассивные прослушки из микса это не «вкус».</summary>
    public const double MinTopArtistPlays = 4.0;

    /// <summary>Планка «сильного вкуса» (сырые прослушки, без дисконта): семейства
    /// выше неё проходят гейт аудитории без проверки — нишевые любимцы остаются.
    /// Ниже — «слабый сигнал» (пара фоновых проигрываний, накликанные тестами плеера):
    /// нужен счётчик ≥ MinArtistListeners на карточке артиста, иначе в миксе нет
    /// места.</summary>
    public const double StrongTastePlays = 10.0;

    /// <summary>Минимальная аудитория исполнителя (ЯМ brief-info) для слабого сигнала
    /// и добора новизны: ноунеймы и «нейро-треки» не попадают в микс даже в хвосте.</summary>
    public const int MinArtistListeners = 10_000;

    /// <summary>Минимум прослушиваний трека для related-кандидатов SoundCloud.</summary>
    public const int MinTrackPlays = 10_000;

    /// <summary>Бюджет проверок аудитории за генерацию (поиск + brief-info на семейство);
    /// результаты кэшируются в wave_seed_map навсегда, бюджет расходуется один раз.
    /// 60 — широкий круг требует быстрого прогрева: каждый проверенный ≥10k артист
    /// навсегда расширяет оборот, непроверенное в микс не проходит.</summary>
    public const int MaxListenerResolutions = 60;


    /// <summary>Сколько «артистов сцены» (похожих исполнителей) подключается к волне.
    /// Каждая сцена — новое семейство в миксе: чем их больше, тем шире круг.
    /// Кандидаты сэмплируются заново каждую генерацию, их каталоги кэшируются —
    /// проверенный аудиторией круг растёт от генерации к генерации.</summary>
    public const int MaxSceneArtists = 24;

    /// <summary>Сколько похожих берётся у каждого топ-артиста перед отбором сцены.</summary>
    public const int ScenePerArtist = 3;

    /// <summary>Для скольких топ-артистов запрашивается радио-батч ротора.
    /// Ротор — единственный источник СВЕЖЕГО материала каждую генерацию (не кэшируется),
    /// поэтому его квота — главная защита пула от выедания кулдауном.</summary>
    public const int MaxRadioArtists = 15;

    /// <summary>Сколько треков запрашивается у одного исполнителя (/artists/{id}/tracks).</summary>
    public const int ArtistPageSize = 50;

    /// <summary>Пауза между перекачками каталогов артистов, мс: перекачка двух страниц
    /// по всем сидам подряд упирается в rate-limit Яндекса и роняла часть каталогов —
    /// пул тогда собирался из пары уцелевших семейств.</summary>
    public const int ArtistCatalogFetchDelayMs = 250;

    /// <summary>Бюджет поисков /search за генерацию: сопоставление сидов и имён
    /// исполнителей не должно внезапно гонять десятки запросов (кэш расходует его один раз).</summary>
    public const int MaxSearchResolutions = 12;
    public const int MaxArtistResolutions = 8;

    /// <summary>Базовые веса пулов в скоринге (нормируются на максимум).</summary>
    internal const double ScenePoolWeight = 2.5;
    internal const double RadioPoolWeight = 2.0;
    internal const double ArtistPoolBase = 3.0;
    internal const double ScPoolWeight = 2.6;

    /// <summary>Пул E «сцена SoundCloud»: сколько лайков-сидов и сколько похожих берётся.</summary>
    public const int ScSeedCount = 8;
    public const int ScRelatedLimit = 20;

    /// <summary>Вес аффинности исполнителя кандидата и разброс джиттера: знакомое
    /// стабильно поднимается над шумом, novelty дозирована.</summary>
    internal const double AffinityBonusWeight = 1.2;
    internal const double SeedBonusWeight = 0.6;
    internal const double JitterAmplitude = 0.75;

    /// <summary>Множитель бонуса артистам, звучавшим в недавних миксах — не исключение,
    /// а понижение: при тонком пуле знакомых они всё ещё доступны, но в хвосте.</summary>
    internal const double DemotedArtistFactor = 0.25;

    /// <summary>Сколько сидов берётся с каждого библиотечного источника за генерацию
    /// (пул D, взвешенно по play_log; сиды от никогда не игравшихся исполнителей
    /// отсеиваются — лайк без прослушек это не вкус, а шум).</summary>
    internal static readonly (int Yandex, int Vk, int SoundCloud, int Local) SeedQuota = (8, 5, 2, 2);

    /// <summary>TTL кэша /similar и негативных матчей поиска.</summary>
    public static readonly TimeSpan SimilarCacheTtl = TimeSpan.FromDays(14);
    public static readonly TimeSpan SeedMapRetryTtl = TimeSpan.FromDays(14);

    /// <summary>Окно аффинности по play_log и порог «недавно играло» (не предлагать).</summary>
    public const int AffinityWindowDays = 90;
    public const int RecentPlayedDays = 2;

    /// <summary>Сколько дней непрослушанное предложение держится вне микса —
    /// пассивный негативный сигнал и ротация материала между миксами. 3, а не 7:
    /// при частых генерациях кулдаун выедает пул быстрее, чем он пополняется,
    /// и волна тает от обновления к обновлению.</summary>
    public const int SuggestedCooldownDays = 3;

    /// <summary>Прослушанное предложение возвращается уже через день: прослушка —
    /// позитивный сигнал, и именно возврат понравившегося держит длину микса при
    /// активной ротации (иначе волна выедает пул и тает от генерации к генерации).</summary>
    public const int PlayedSuggestedCooldownDays = 1;

    /// <summary>Grace-добор: если волна вышла короче полной (пул выеден кулдауном
    /// или семейств осталось мало), предложения старше этого возраста возвращаются
    /// в оборот — старые первыми. Час — чтобы не повторять микс, который слушали
    /// только что. Повтор вчерашнего материала лучше микса из одного трека.</summary>
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

    /// <summary>Один сид пула D. YmId известен сразу для лайков ЯМ, остальные
    /// разрешаются через /search (с кэшем в wave_seed_map).</summary>
    private sealed record Seed(string Source, string SeedId, string Artist, string Title,
                               long DurationMs, string? YmId);

    /// <summary>Исполнитель для пулов A/B/C: ключ play_log, отображаемое имя, ym-id, частота.</summary>
    private sealed record TopArtist(string Key, string Name, string YmId, double Plays);

    /// <summary>
    /// Источники сидов, собранные ДО фоновой генерации: чтения идут через общее
    /// SQLite-соединение других сервисов и должны выполняться на потоке вызывающего
    /// (UI), как загрузки всех остальных страниц — генерация же работает в фоне
    /// только с собственными соединениями RecommendationRepository.
    /// </summary>
    public sealed class WaveSources
    {
        public IReadOnlyList<YmTrackRow> YmRows { get; init; } = Array.Empty<YmTrackRow>();
        public IReadOnlyList<VkTrackRow> VkRows { get; init; } = Array.Empty<VkTrackRow>();
        public IReadOnlyList<SoundCloudLikeRow> ScRows { get; init; } = Array.Empty<SoundCloudLikeRow>();
        public IReadOnlyList<Track> LocalTracks { get; init; } = Array.Empty<Track>();
    }

    /// <summary>Собрать источники сидов (быстрые индексированные выборки; UI-поток).</summary>
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
    /// Сгенерировать волну: анализ исполнителей из play_log → пулы A/B/C → сиды из
    /// библиотек → пул D → фильтры → ранжирование. Требует подключённый Яндекс Музыки
    /// (проверяет вызывающий через YmService.HasToken). Возвращает пустой список,
    /// если кандидатов нет. Тяжёлые участки — на пуле потоков; БД в фоне доступается
    /// ТОЛЬКО через собственные соединения RecommendationRepository.
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

        // ===== Сигналы вкуса =====
        var plays = await _repo.GetRecentPlaysAsync(AffinityWindowDays);
        var suggested = await _repo.GetSuggestedAsync();
        // Прослушки ранее предложенного весят меньше — микс не выращивает себе
        // «любимых» артистов из того, что сам навязал.
        var suggestedKeys = suggested
            .Select(s => s.ArtistKey + "|" + s.TitleKey)
            .Where(k => k.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
        var artistPlayCounts = ComputePlayCounts(plays, suggestedKeys);
        // Сырые счётчики — для порогов «известности»/«сильного вкуса» и приоритета
        // проверок гейта: дисконт в них не входит.
        var rawPlayCounts = ComputeRawPlayCounts(plays);
        var playKeysAll = plays
            .Select(p => PlayKey(p.Artist, p.Title))
            .Where(k => k != null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

        // Отображаемые имена ключей play_log (для поисковых запросов артистов).
        var artistNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var play in plays)
            foreach (var name in ArtistHelper.Split(play.Artist))
            {
                var key = ArtistHelper.Key(name);
                if (key.Length > 0) artistNames.TryAdd(key, name);
            }

        // ===== Пулы A/B/C: анализ исполнителей =====
        var topArtists = await ResolveTopArtistsAsync(rawPlayCounts, artistNames, ct);
        var seedWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var artist in topArtists)
            seedWeights[$"artist:{artist.YmId}"] = ArtistPoolBase
                                                    + 2 * Math.Log(1 + artist.Plays) / Math.Log(2);

        // B: «общая тусовка» — похожие исполнители топ-артистов, их треки тем же эндпоинтом.
        await CollectSceneArtistsAsync(topArtists, artistPlayCounts, seedWeights, rng, ct);

        // C: «радио сцены» — батч ротора по топ-артистам (свежий каждую генерацию).
        await CollectRadioBatchesAsync(topArtists, seedWeights, ct);

        // ===== Пул D: сиды из библиотек (взвешенно по play_log) =====
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

        // ===== Пул E «сцена SoundCloud»: related-tracks по игравшимся лайкам =====
        // Треки берутся из каталога SC (не из библиотеки пользователя) и играют через
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

        // ===== Обновление кэша /similar, /artists/{id}/tracks и SC related =====
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
                    // Каталог глубже одной страницы (2 × 50): больше материала
                    // для ротации между миксами в пределах одного семейства.
                    // Пауза между каталогами: перекачка двух страниц по всем
                    // сидам подряд упирается в rate-limit Яндекса (429).
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
                throw; // токен отозван — VM покажет «подключите аккаунт»
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

        // SC related: свои кандидаты (ScTrack) → те же строки кэша; сессия SC мертва —
        // пул просто пропускается, волну собирают остальные.
        foreach (var seedId in staleSc)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var related = await _soundCloud.GetRelatedTracksAsync(seedId["sc:".Length..], ScRelatedLimit, ct)
                               ?? new List<ScTrack>();
                await _repo.ReplaceSimilarAsync(seedId, related
                    .Where(t => t.Streamable && t.Policy == null && t.Id > 0
                                && t.PlaybackCount >= MinTrackPlays) // ноунеймы/нейро-треки мимо
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

        // Ротор: батчи случайные — не кэшируем на TTL, берём свежие каждую генерацию.
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

        // ===== Кандидаты всех пулов: дедуп по «источник:id», приоритет — порядок сидов
        // (артисты play_log вытесняют сцену, сцена — ротор, тот — похожие треки) =====
        var allSeedIds = artistSeedIds.Concat(rotorSeedIds).Concat(trackSeedIds).Concat(scSeedIds).ToList();
        var maxSeedWeight = seedWeights.Count > 0 ? seedWeights.Values.Max() : 1.0;
        var similarMap = await _repo.GetSimilarAsync(allSeedIds, SimilarCacheTtl);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Кросс-источниковый дедуп песен: одна и та же песня, найденная и в YM-каталоге,
        // и в related SC, не должна задваиваться в миксе (приоритет — более ранний пул).
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

        // База «знакомых» из всех библиотек: исполнители, которые пользователь
        // хотя бы добавил, получают минимальный бонус (добавление — тоже сигнал).
        var maxPlayCount = artistPlayCounts.Count > 0 ? artistPlayCounts.Values.Max() : 0;
        foreach (var key in LibraryArtistKeys(ymRows.Select(r => r.Artist),
                     vkRows.Select(r => r.Artist),
                     scRows.Select(r => r.Artist),
                     localTracks.Select(t => t.Artist)))
            artistPlayCounts[key] = Math.Max(artistPlayCounts.GetValueOrDefault(key), 0.05);

        // ===== Обратная связь: прослушанное после предложения — не исключение =====
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

        // ===== Фильтры =====
        var libraryYmIds = ymRows.Select(r => r.YmId).ToHashSet(StringComparer.Ordinal);
        var localIndex = MatchHelper.BuildIndex(localTracks);
        var vkKeys = vkRows.Select(r => MatchHelper.BuildKey(r.Artist, r.Title))
            .Where(k => k.Length > 1).ToHashSet(StringComparer.Ordinal);
        var scKeys = scRows.Select(r => MatchHelper.BuildKey(r.Artist, r.Title))
            .Where(k => k.Length > 1).ToHashSet(StringComparer.Ordinal);
        // Кулдаун предложений раздвоен: непрослушанное отдыхает 7 дней (пассивный
        // негатив), прослушанное возвращается через день — оно уже понравилось,
        // и именно его возврат не даёт волне таять при частых генерациях.
        var cooldownIds = suggested
            .Where(s => s.SuggestedAt >= DateTime.UtcNow.AddDays(-(s.Played
                ? PlayedSuggestedCooldownDays
                : SuggestedCooldownDays)))
            .Select(s => s.YmId)
            .ToHashSet(StringComparer.Ordinal);
        // Артисты недавних миксов получают пониженный бонус: следующий микс
        // заводит других знакомых исполнителей, а не тех же в том же порядке.
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

        // ===== Базовые фильтры (без «известности») =====
        var baseFiltered = pool
            .Where(c => c.Source != Track.SourceYandex || !libraryYmIds.Contains(c.PlatformId))
            .Where(c => MatchHelper.FindLocalMatch(localIndex, c.Artist, c.Title) == null)
            .Where(c => !recentPlayedKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !vkKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !scKeys.Contains(MatchHelper.BuildKey(c.Artist, c.Title)))
            .Where(c => !cooldownIds.Contains(c.JournalId))
            .ToList();

        // ===== Только прослушанные исполнители =====
        // Кандидат проходит как «знакомый», если хотя бы один его исполнитель имеет
        // ≥ MinTopArtistPlays органических прослушек в play_log. Членство в библиотеке
        // само по себе НЕ считается: рекомендации строятся на том, что пользователь
        // реально слушает — «добавил, но не слушает» в микс не попадает. Всё остальное —
        // новизна (хвост микса), и она фильтруется гейтом аудитории.
        var playedKeys = rawPlayCounts
            .Where(kv => kv.Value >= MinTopArtistPlays)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var knownKeys = playedKeys;

        // ===== Единый гейт аудитории для всего пула =====
        // Сильный вкус (≥ StrongTastePlays органических прослушек) не проверяется —
        // нишевые любимцы с маленькой аудиторией остаются. Слабый сигнал (пара
        // фоновых проигрываний, накликанные тестами плеера) и новизна проверяются:
        // мелкие исполнители (< MinArtistListeners слушателей) в микс не проходят.
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

        // ===== Ранжирование =====
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

            // Хвост микса добирается новизной (см. WaveSize): ноунеймы ранжируются
            // после всего знакомого материала, короткий микс из-за них хуже полного.
            if (result.Count < WaveSize && novelty.Count > 0)
                result.AddRange(RankCandidates(novelty, artistPlayCounts, maxPlayCount, candidateWeights,
                    maxSeedWeight, WaveSize - result.Count, LightPerArtist, rng, demotedArtistKeys));
            return result;
        }

        var wave = RankAll();

        // ===== Grace-добор: волна вышла короче полной =====
        // Кулдаун выел пул (или семейств осталось мало) — возвращаем в оборот
        // предложения старше GraceRefillHours, старые первыми. Повтор вчерашнего
        // материала лучше микса из одного трека; свежее часа по-прежнему отдыхает.
        if (wave.Count < WaveSize)
        {
            var graceCutoff = DateTime.UtcNow.AddHours(-GraceRefillHours);
            var suggestedAtById = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var s in suggested)
                suggestedAtById.TryAdd(s.YmId, s.SuggestedAt);
            var baseSet = new HashSet<WaveItem>(baseFiltered);

            var grace = pool
                .Where(c => !baseSet.Contains(c)) // отсеяны именно кулдауном
                .Where(c => suggestedAtById.TryGetValue(c.JournalId, out var at) && at <= graceCutoff)
                .OrderBy(c => suggestedAtById[c.JournalId])
                .ToList();
            if (grace.Count > 0)
            {
                // Возвратный материал проходит тот же гейт аудитории: иначе
                // однажды накликанный слабый артист возвращается через grace.
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
    /// Топ play_log-исполнителей → ym-artist-id (кэш в wave_seed_map, source='ym_artist',
    /// негативный кэш с TTL). Порядок — по частоте прослушек, не более MaxTopArtists.
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
    /// Лучший результат поиска исполнителя: точное совпадение нормализованных имён,
    /// иначе первый, чьё имя содержит запрос (или наоборот). null — ничего похожего.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Единый гейт аудитории, закрытый по умолчанию: семейство проходит только если
    /// оно сильное (≥ StrongTastePlays сырых прослушек) или ПРОВЕРЕНО с аудиторией
    /// ≥ MinArtistListeners (ЯМ brief-info). Непроверенное выбывает из этой генерации —
    /// мелкие артисты не просачиваются, пока кэш не прогрелся. Проверки (бюджет
    /// MaxListenerResolutions за генерацию) идут сначала на слабый сигнал
    /// пользователя по убыванию прослушек — их результат сразу наполняет микс, —
    /// затем на семейства без прослушек (вероятный «нейро»-спам). Кэш навсегда
    /// в wave_seed_map (source='ym_listeners'). Сбой одного запроса не роняет
    /// генерацию.
    /// </summary>
    private async Task<List<WaveItem>> FilterByListenersAsync(
        List<WaveItem> candidates, HashSet<string> strongKeys,
        Dictionary<string, double> playCounts, CancellationToken ct)
    {
        // Отображаемые имена семейств — из самих кандидатов (в play_log их нет).
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
            .OrderBy(kv => kv.Value.Plays == 0 ? 1 : 0) // сначала слабый сигнал пользователя
            .ThenByDescending(kv => kv.Value.Plays)
            .ToList();
        foreach (var (key, entry) in toCheck)
        {
            ct.ThrowIfCancellationRequested();
            var cached = await _repo.GetSeedMapAsync("ym_listeners", key);
            if (cached != null)
            {
                // "0" — проверен и забракован (ноунейм или поиск не нашёл): не тратим бюджет.
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
                throw; // токен отозван — VM покажет «подключите аккаунт»
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

        // Fail-closed и по ВСЕМ исполнителям карточки: «сильный X feat. мелкий гость»
        // всё равно показывает мелкое имя на карточке — такой трек не проходит.
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
    /// Пул B «общая тусовка»: у КАЖДОГО топ-артиста берём ScenePerArtist похожих
    /// исполнителей, вычитаем тех, кого пользователь уже слушает (они и так в пуле A),
    /// сэмплим до MaxSceneArtists новых и забираем их каталоги (кэш 'artist:{id}',
    /// вес сцены). Это главный источник ШИРОКОГО круга: каждое новое семейство сцены
    /// проходит гейт аудитории и навсегда остаётся в обороте.
    /// Сбой одного запроса не роняет генерацию.
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
    /// Пул C «радио сцены»: батч ротора по топ-артистам (свежий каждую генерацию,
    /// вес радиопула). Сбой не роняет генерацию.
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
    /// ym_id сида через кэш/поиск: известный матч — сразу; просроченный негативный кэш
    /// или отсутствие записи — поиск (если бюджет разрешает). Возвращаемые значения:
    /// ym_id — найден; '' — найден негативный кэш/поиск не дал матча; null — бюджета
    /// нет или сид без исполнителя/названия.
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
            // Протухший негативный кэш — ищем снова (каталоги Яндекса пополняются).
        }

        if (!searchAllowed) return null;

        var results = await _ym.SearchTracksAsync($"{seed.Artist} {seed.Title}", limit: 8, ct);
        var match = PickSearchMatch(results, seed.Artist, seed.Title, seed.DurationMs);
        await _repo.SaveSeedYmIdAsync(seed.Source, seed.SeedId, match?.Id ?? string.Empty);
        return match?.Id ?? string.Empty;
    }

    /// <summary>
    /// Лучший результат поиска для сида: нормализованное совпадение названия И пересечение
    /// исполнителей; длительность (если известна обеим сторонам) — фильтр ±15 c. Строгий
    /// матч не найден — первый результат с совпавшим исполнителем (названия треков в
    /// каталогах расходятся чаще, чем имена). null — совпадений по исполнителю нет:
    /// чужой трек в волне хуже пропуска.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Годится ли трек в сиды пула D: исполнитель встречался в play_log ИЛИ сам трек
    /// игрался. Лайк/трек библиотеки, который пользователь никогда не слушал, вкуса
    /// не отражает — от таких сидов волна уезжает в сторону. Спец-источники
    /// (лайки SC и избранное) фильтруются так же: явный сигнал слабее факта прослушки.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Бонус аффинности исполнителя — ОТНОСИТЕЛЬНЫЙ: доля от самого играемого
    /// артиста (логарифмическое сглаживание обеих сторон). Раньше логарифм
    /// насыщался уже к 3-4 прослушкам, и артисты с парой прослушек получали почти
    /// максимальный буст, вытесняя действительно играемых. count=0 → 0;
    /// count=max → полный <see cref="AffinityBonusWeight"/>.
    /// Чистая функция — покрыта юнит-тестами.
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

    /// <summary>Частоты исполнителей по play_log (соавторы учитываются каждому).
    /// Прослушки из журнала предложений (discountedKeys) весят 0.4: трек, который
    /// пользователь просто дал дограть миксу, не становится «вкусом» — иначе волна
    /// сама выращивает себе топ-артистов из навязанного (петля самозагрязнения).
    /// Чистая функция — покрыта юнит-тестами.</summary>
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

    /// <summary>Вес прослушки трека, ранее предложенного миксом (см. ComputePlayCounts).</summary>
    internal const double SuggestedPlayWeight = 0.25;

    /// <summary>Сырые частоты прослушек (без дисконта) — для порогов «известности» и
    /// «сильного вкуса»: прослушка есть прослушка, гейты не должны зависеть от того,
    /// из микса ли звучал трек (иначе сильный вкус занижался до пары артистов).
    /// Дисконт остаётся только в ранжировании. Чистая функция — покрыта юнит-тестами.</summary>
    internal static Dictionary<string, double> ComputeRawPlayCounts(
        IReadOnlyList<(string Artist, string Title, DateTime PlayedAt)> plays)
    {
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in plays)
            foreach (var key in ArtistKeysOf(play.Artist))
                counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts;
    }

    /// <summary>Семейные ключи трека: все его исполнители после отрезания фитов.
    /// Треки "X feat. B", "X &amp; B" и "X" принадлежат семейству {x, b} — для лимитов
    /// и раскладки это один исполнитель в любых написаниях.</summary>
    internal static List<string> TrackArtistKeys(WaveItem track)
        => ArtistKeysOf(track.Artist);

    /// <summary>Семейные ключи строки исполнителей (Split → StripFeatures → Key).</summary>
    internal static List<string> ArtistKeysOf(string artist)
        => ArtistHelper.Split(artist)
            .Select(n => ArtistHelper.Key(ArtistHelper.StripFeatures(n)))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Ранжирование кандидатов волны: скоринг (вес пула/сида + аффинность + джиттер) →
    /// сортировка → отбор в три прохода. Первый — с лимитами на исполнителя
    /// («тяжёлым» — HeavyPerArtist, остальным — maxPerArtist): знакомое занимает
    /// большую часть волны и не вырождается в альбом. Второй и третий — с
    /// ослабленными лимитами (+1 и +2): очередь добирается с сохранением разброса,
    /// потолок 3 «лёгким» / 4 «тяжёлым» за весь микс. Джиттер гарантирует отличие
    /// повторных генераций. Чистая функция — покрыта юнит-тестами.
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
            // Бонус — по самому играемому из исполнителей трека (коллаборация с
            // любимым артистом релевантна, даже если ведущий неизвестен).
            var artistBonus = keys.Count > 0
                ? keys.Max(k => ArtistBonus(artistPlayCounts, maxPlayCount, k))
                : 0;
            // Демоушн артистов недавних миксов: ротация головного состава.
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

        // Отбор в три прохода с общими счётчиками: каждый следующий ослабляет лимит,
        // а не сбрасывает его, поэтому семейство не может набрать лишние слоты в доборе.
        void SelectPass(int extraPerArtist)
        {
            foreach (var item in scored.OrderByDescending(s => s.Score))
            {
                if (selected.Count >= waveSize) break;
                if (picked.Contains(item.Track)) continue;

                var keys = TrackArtistKeys(item.Track);
                if (keys.Count > 0 && extraPerArtist != int.MaxValue)
                {
                    // «Тяжёлые» исполнители (топ play_log) получают расширенный лимит.
                    // Лимит семейный: трек учитывается против КАЖДОГО своего исполнителя —
                    // соло "X", "X & B" и "X feat. C" вместе не превысят лимит семейства X.
                    var baseCap = item.WeightNorm >= HeavyCapShare ? HeavyPerArtist : maxPerArtist;
                    var cap = baseCap + extraPerArtist;
                    if (keys.Any(k => perArtist.GetValueOrDefault(k) >= cap)) continue;
                    foreach (var k in keys) perArtist[k] = perArtist.GetValueOrDefault(k) + 1;
                }

                picked.Add(item.Track);
                selected.Add(item.Track);
            }
        }

        // Проход 1 — основные лимиты: разнообразие среди лучшего материала.
        SelectPass(0);
        // Проход 2 — ослабленные лимиты (+1): хвост добирается
        // с сохранением разброса, исполнитель не собирается в блок.
        if (selected.Count < waveSize) SelectPass(BackfillExtraPerArtist);
        // Проход 3 — ещё +1 (потолок 3 «лёгким» / 4 «тяжёлым» за весь микс).
        // Прохода «без лимитов» нет намеренно: блок одного исполнителя в хвосте
        // хуже микса, не добравшего полную длину.
        if (selected.Count < waveSize) SelectPass(BackfillExtraPerArtist * 2);

        return SpreadByArtist(selected, rng);
    }

    /// <summary>
    /// Раскладка выбранных треков «веером» по исполнителям с СОХРАНЕНИЕМ рейтинга:
    /// трек отодвигается дальше, если пересекается семействами с любым из последних
    /// ArtistSpreadWindow размещённых («через трек» невозможно). Когда конфликтуют
    /// все остатки (пул тонкий), берём хотя бы не тот же артист, что предыдущий —
    /// при двух семействах получается чередование, а не слипшийся блок. Смежность
    /// остаётся лишь там, где её не избежать (все остатки — одно семейство).
    /// Чистая функция — покрыта юнит-тестами.
    /// </summary>
    internal static List<WaveItem> SpreadByArtist(IReadOnlyList<WaveItem> selected, Random rng)
    {
        if (selected.Count < 3) return selected.ToList();

        var pending = new LinkedList<WaveItem>(selected);
        var result = new List<WaveItem>(selected.Count);
        // Множества семейств последних ArtistSpreadWindow размещённых треков.
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
                chosen ??= pending.First; // все остатки — одно семейство
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

    /// <summary>Окно раскладки: семейство не повторяется среди последних N размещённых
    /// треков. N=3: «через трек» невозможно, и два самых слушаемых артиста не могут
    /// пинг-понговать головой микса — третий по рейтингу подключается уже третьим
    /// треком (A,B,C,A,D,…), а не после того, как парные слоты исчерпаются.</summary>
    internal const int ArtistSpreadWindow = 3;

    /// <summary>Совпадает ли основной (первый) исполнитель у двух треков — для тестов
    /// раскладки и диагностики.</summary>
    /// <summary>Взвешенный сэмпл без повторений: элемент с весом в 2 раза выше попадает
    /// в выборку примерно вдвое чаще. Нулевые/отрицательные веса зажимаются к минимуму.
    /// Чистая функция — покрыта юнит-тестами.</summary>
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

    /// <summary>Ключ пары «исполнитель|название» для сверки play_log с wave_suggested
    /// (совпадает со схемой artist_key/title_key в wave_suggested). null — пустые части.</summary>
    private static string? PlayKey(string artist, string title)
    {
        var artistKey = ArtistKeysOf(artist).FirstOrDefault() ?? string.Empty;
        var titleKey = MatchHelper.Normalize(title);
        return artistKey.Length == 0 || titleKey.Length == 0 ? null : artistKey + "|" + titleKey;
    }
}
