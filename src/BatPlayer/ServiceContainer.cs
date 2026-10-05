using System;
using System.IO;
using Microsoft.Data.Sqlite;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;
using BatPlayer.Services.Vk;
using BatPlayer.Services.YandexMusic;
using BatPlayer.Services.Spotify;

namespace BatPlayer;

/// <summary>
/// Lightweight manual dependency container — no extra DI framework needed.
/// Resolves services as singletons. Built once at startup.
/// </summary>
public sealed class ServiceContainer : IServiceProvider
{
    private readonly Dictionary<Type, object> _singletons = new();
    private readonly Dictionary<Type, Func<IServiceProvider, object>> _factories = new();

    public static IServiceProvider Build(string dbPath, string settingsPath, string coverCacheDir)
    {
        var c = new ServiceContainer();

        // Database connection (singleton, thread-safe via SqliteConnection string with pooling)
        var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared;Pooling=True;");
        conn.Open();
        c.RegisterSingleton(_ => conn);

        // Core services
        c.RegisterSingleton<SettingsService>(_ => new SettingsService(settingsPath));
        c.RegisterSingleton<MetadataService>(_ => new MetadataService());
        c.RegisterSingleton<CoverCacheService>(sp => new CoverCacheService(coverCacheDir, sp.GetRequiredService<MetadataService>()));
        c.RegisterSingleton<LibraryService>(sp => new LibraryService(sp.GetRequiredService<SqliteConnection>(), sp.GetRequiredService<MetadataService>(), coverCacheDir));
        c.RegisterSingleton<PlaylistService>(sp => new PlaylistService(sp.GetRequiredService<SqliteConnection>()));
        c.RegisterSingleton<HistoryService>(sp => new HistoryService(sp.GetRequiredService<SqliteConnection>()));
        c.RegisterSingleton<SearchService>(sp => new SearchService(sp.GetRequiredService<SqliteConnection>(), coverCacheDir));
        c.RegisterSingleton<EqualizerService>(sp => new EqualizerService(sp.GetRequiredService<SettingsService>()));

        // SoundCloud: веб-сессия (cookies) в sc_auth.json рядом с settings.json;
        // SettingsService нужен сетевому слою (настройка SoundCloudProxy).
        var scAuthPath = Path.Combine(App.AppDataDir, SoundCloudService.AuthFileName);
        // Официальный SoundCloud API (pairing-подключение + стримы/related/лайки).
        var scApiAuthPath = Path.Combine(App.AppDataDir, "sc_api_auth.json");
        c.RegisterSingleton<SoundCloudApiAuthStore>(_ => new SoundCloudApiAuthStore(scApiAuthPath));
        c.RegisterSingleton<SoundCloudOfficialAuth>(sp => new SoundCloudOfficialAuth(
            sp.GetRequiredService<SoundCloudApiAuthStore>()));
        c.RegisterSingleton<SoundCloudOfficialApi>(sp => new SoundCloudOfficialApi(
            sp.GetRequiredService<SoundCloudOfficialAuth>()));
        c.RegisterSingleton<SoundCloudService>(sp => new SoundCloudService(
            scAuthPath, sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<SoundCloudOfficialApi>()));
        c.RegisterSingleton<SoundCloudLoginService>(sp => new SoundCloudLoginService(sp.GetRequiredService<SoundCloudService>()));
        c.RegisterSingleton<SoundCloudLikesRepository>(sp => new SoundCloudLikesRepository(sp.GetRequiredService<SqliteConnection>()));
        // Дисковый кэш mp3-стримов: один синглтон на все VM (общие замки на scId, общий лимит каталога).
        c.RegisterSingleton<SoundCloudStreamCache>(_ => new SoundCloudStreamCache());

        // VK: OAuth-токен в vk_auth.json рядом с settings.json; отдельный дисковый кэш
        // стримов vk_cache (механика общая со SC-кэшем, каталог свой).
        var vkAuthPath = Path.Combine(App.AppDataDir, VkService.AuthFileName);
        c.RegisterSingleton<VkService>(_ => new VkService(vkAuthPath));
        c.RegisterSingleton<VkLoginService>(sp => new VkLoginService(sp.GetRequiredService<VkService>()));
        c.RegisterSingleton<VkTracksRepository>(sp => new VkTracksRepository(sp.GetRequiredService<SqliteConnection>()));
        c.RegisterSingleton<VkStreamCache>(_ => new VkStreamCache());

        // Yandex Music: cookies веб-сессии .yandex.ru в ym_auth.json рядом с settings.json;
        // отдельный дисковый кэш стримов ym_cache (механика общая со SC-кэшем, каталог свой).
        var ymAuthPath = Path.Combine(App.AppDataDir, YmService.AuthFileName);
        c.RegisterSingleton<YmService>(_ => new YmService(ymAuthPath));
        c.RegisterSingleton<YmLoginService>(sp => new YmLoginService(sp.GetRequiredService<YmService>()));
        c.RegisterSingleton<YmTracksRepository>(sp => new YmTracksRepository(sp.GetRequiredService<SqliteConnection>()));
        c.RegisterSingleton<YmStreamCache>(_ => new YmStreamCache());

        // «Моя волна»: локальные рекомендации по кэшу /similar Яндекса + сигналам из
        // play_log/библиотек. Свой репозиторий с отдельными соединениями на операцию —
        // генерация идёт в фоне и не спорит за общее соединение с UI.
        c.RegisterSingleton<RecommendationRepository>(_ => new RecommendationRepository(dbPath));
        c.RegisterSingleton<RecommendationService>(sp => new RecommendationService(
            sp.GetRequiredService<YmService>(),
            sp.GetRequiredService<SoundCloudService>(),
            sp.GetRequiredService<RecommendationRepository>(),
            sp.GetRequiredService<YmTracksRepository>(),
            sp.GetRequiredService<VkTracksRepository>(),
            sp.GetRequiredService<SoundCloudLikesRepository>(),
            sp.GetRequiredService<LibraryService>()));

        // Spotify: OAuth токены в spotify_auth.json рядом с settings.json;
        // синхронизация через официальный Spotify Web API.
        var spotifyAuthPath = Path.Combine(App.AppDataDir, SpotifyService.AuthFileName);
        c.RegisterSingleton<SpotifyService>(_ => new SpotifyService(spotifyAuthPath));
        c.RegisterSingleton<SpotifyTracksRepository>(sp => new SpotifyTracksRepository(sp.GetRequiredService<SqliteConnection>()));

        // Audio
        c.RegisterSingleton<AudioEngine>(_ => new AudioEngine());
        c.RegisterSingleton<AudioService>(sp => new AudioService(
            sp.GetRequiredService<AudioEngine>(),
            sp.GetRequiredService<EqualizerService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<LibraryService>(),
            sp.GetRequiredService<HistoryService>()));

        // OS integration
        c.RegisterSingleton<TrayService>(sp => new TrayService(sp.GetRequiredService<AudioService>()));
        c.RegisterSingleton<GlobalHotkeyService>(sp => new GlobalHotkeyService(sp.GetRequiredService<AudioService>()));

        return c;
    }

    public void RegisterSingleton<T>(Func<IServiceProvider, T> factory) where T : class
        => _factories[typeof(T)] = sp => factory(sp);

    public void RegisterSingleton<TInterface, TImplementation>(Func<IServiceProvider, TImplementation> factory)
        where TImplementation : class, TInterface
        where TInterface : class
        => _factories[typeof(TInterface)] = sp => factory(sp);

    public T GetRequiredService<T>() => (T)GetService(typeof(T))!;

    public object? GetService(Type serviceType)
    {
        if (_singletons.TryGetValue(serviceType, out var instance))
            return instance;

        if (_factories.TryGetValue(serviceType, out var factory))
        {
            instance = factory(this);
            _singletons[serviceType] = instance;
            return instance;
        }

        return null;
    }
}
