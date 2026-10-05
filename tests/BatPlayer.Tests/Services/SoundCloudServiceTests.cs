using System;
using System.IO;
using System.Linq;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты разбора ответов неофициального API SoundCloud и выбора стрима.
/// Фикстура ниже повторяет реальный ответ /users/{id}/likes: обёртка с created_at,
/// вложенный track, соседний элемент-плейлист, transcodings progressive + hls,
/// next_href — полный URL с курсором (пагинация по времени лайка).
/// </summary>
public class SoundCloudServiceTests
{
    private const string LikesFixture = """
        {
          "collection": [
            {
              "created_at": "2026-08-01T10:15:00Z",
              "track": {
                "id": 912345678,
                "kind": "track",
                "title": "Midnight Drive",
                "duration": 213456,
                "artwork_url": "https://i1.sndcdn.com/artworks-000123456789-large.jpg",
                "permalink_url": "https://soundcloud.com/neon-fox/midnight-drive",
                "streamable": true,
                "created_at": "2026-05-10T08:00:00Z",
                "user": { "username": "Neon Fox", "full_name": "Neon Fox Music", "avatar_url": "https://i1.sndcdn.com/avatars-1-large.jpg" },
                "media": {
                  "transcodings": [
                    { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:912345678/hls", "quality": "hq", "format": { "protocol": "hls", "mime_type": "audio/mpeg" } },
                    { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:912345678/progressive", "quality": "sq", "format": { "protocol": "progressive", "mime_type": "audio/mpeg" } }
                  ]
                }
              }
            },
            {
              "created_at": "2026-07-20T09:00:00Z",
              "playlist": { "id": 555000111, "kind": "playlist", "title": "Late Night Set" }
            },
            {
              "created_at": "2026-07-01T12:00:00Z",
              "track": {
                "id": 445566778,
                "kind": "track",
                "title": "Acoustic Session (Live) [Remastered 2026]",
                "duration": 98765,
                "artwork_url": "https://i1.sndcdn.com/artworks-000987654321-t500x500.jpg",
                "permalink_url": "https://soundcloud.com/other/acoustic-session",
                "streamable": false,
                "created_at": "2026-06-01T00:00:00Z",
                "user": { "username": "", "full_name": "Ivan Petrov", "avatar_url": "" },
                "media": {
                  "transcodings": [
                    { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:445566778/hls", "format": { "protocol": "hls", "mime_type": "audio/mp4" } }
                  ]
                }
              }
            }
          ],
          "next_href": "https://api-v2.soundcloud.com/users/1234567/likes?offset=992735%3A910055%3A0&limit=20"
        }
        """;

    [Fact]
    public void ParseLikesJson_ReadsCollectionAndNextHref()
    {
        var response = SoundCloudService.ParseLikesJson(LikesFixture);

        Assert.NotNull(response);
        Assert.Equal(3, response!.Collection.Count);
        // next_href — полный URL /users/{id}/likes с курсором в offset (пагинация по времени лайка).
        Assert.NotNull(response.NextHref);
        Assert.Contains("users/1234567/likes", response.NextHref);
        Assert.Contains("offset=", response.NextHref);
    }

    [Fact]
    public void ParseLikesJson_InvalidJson_ReturnsNull()
    {
        Assert.Null(SoundCloudService.ParseLikesJson("{ not json at all"));
    }

    [Fact]
    public void ExtractLikeRows_SkipsPlaylists_AndMapsMetadata()
    {
        var response = SoundCloudService.ParseLikesJson(LikesFixture)!;
        var rows = SoundCloudService.ExtractLikeRows(response, "2026-09-15T00:00:00Z");

        // Элемент collection[].playlist не даёт строки БД.
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("912345678", first.ScId);
        Assert.Equal("Midnight Drive", first.Title);
        Assert.Equal("Neon Fox", first.Artist);
        Assert.Equal(213456, first.DurationMs);
        Assert.Equal("https://soundcloud.com/neon-fox/midnight-drive", first.PermalinkUrl);
        Assert.True(first.Streamable);
        // liked_at — created_at обёртки (дата лайка), а не дата загрузки трека.
        Assert.Equal("2026-08-01T10:15:00Z", first.LikedAt);
        Assert.Equal("2026-09-15T00:00:00Z", first.SyncedAt);
        // Обложка апгрейдится с -large до -t500x500.
        Assert.Contains("-t500x500.jpg", first.ArtworkUrl);

        var second = rows[1];
        Assert.Equal("445566778", second.ScId);
        // username пустой -> берём full_name.
        Assert.Equal("Ivan Petrov", second.Artist);
        Assert.False(second.Streamable);
        // Уже большой размер — без изменений.
        Assert.Contains("-t500x500.jpg", second.ArtworkUrl);
    }

    [Fact]
    public void ExtractLikeRows_TrackWithoutId_IsSkipped()
    {
        var response = SoundCloudService.ParseLikesJson(
            """{"collection":[{"track":{"title":"Broken","id":0}}]}""")!;

        Assert.Empty(SoundCloudService.ExtractLikeRows(response, "now"));
    }

    [Fact]
    public void PickProgressiveUrl_PrefersProgressiveOverHls()
    {
        var response = SoundCloudService.ParseLikesJson(LikesFixture)!;
        var track = response.Collection[0].Track!;

        var url = SoundCloudService.PickProgressiveUrl(track);

        Assert.Equal("https://api-v2.soundcloud.com/media/soundcloud:tracks:912345678/progressive", url);
    }

    [Fact]
    public void PickProgressiveUrl_HlsOnly_ReturnsNull()
    {
        var response = SoundCloudService.ParseLikesJson(LikesFixture)!;
        var track = response.Collection[2].Track!;

        Assert.Null(SoundCloudService.PickProgressiveUrl(track));
    }

    [Fact]
    public void PickProgressiveUrl_NoMedia_ReturnsNull()
    {
        var track = SoundCloudService.ParseTrackJson("""{"id":1,"title":"x"}""")!;
        Assert.Null(SoundCloudService.PickProgressiveUrl(track));
    }

    [Fact]
    public void BuildArtworkUrl_UpgradesLargeToT500x500()
    {
        Assert.Equal("https://i1.sndcdn.com/artworks-1-t500x500.jpg",
            SoundCloudService.BuildArtworkUrl("https://i1.sndcdn.com/artworks-1-large.jpg"));
        Assert.Equal("https://i1.sndcdn.com/artworks-1-t500x500.jpg",
            SoundCloudService.BuildArtworkUrl("https://i1.sndcdn.com/artworks-1-t500x500.jpg"));
        Assert.Null(SoundCloudService.BuildArtworkUrl(null));
    }

    [Fact]
    public void AppendClientId_HandlesQueryAndExistingId()
    {
        Assert.Equal("https://api-v2.soundcloud.com/me?client_id=abc",
            SoundCloudService.AppendClientId("https://api-v2.soundcloud.com/me", "abc"));
        Assert.Equal("https://api-v2.soundcloud.com/me?limit=200&client_id=abc",
            SoundCloudService.AppendClientId("https://api-v2.soundcloud.com/me?limit=200", "abc"));
        // Не дублируем, если client_id уже в ссылке (transcoding url приходит с ним).
        Assert.Equal("https://x/y?client_id=existing",
            SoundCloudService.AppendClientId("https://x/y?client_id=existing", "abc"));
    }

    // ======================= oauth_token из cookies =======================

    [Fact]
    public void ExtractOAuthToken_ReadsPairFromCookieString()
    {
        Assert.Equal("abc", SoundCloudService.ExtractOAuthToken("a=1; oauth_token=abc; b=2"));
    }

    [Fact]
    public void ExtractOAuthToken_HandlesPositionsAndWhitespace()
    {
        // Первый cookie в строке, лишние пробелы вокруг пары и значения.
        Assert.Equal("tok123", SoundCloudService.ExtractOAuthToken("oauth_token=tok123; _sc_session=x"));
        Assert.Equal("tok  x", SoundCloudService.ExtractOAuthToken("  oauth_token = tok  x ; y=1"));
        // Регистр имени пары не важен; похожее имя с префиксом не совпадает.
        Assert.Equal("V", SoundCloudService.ExtractOAuthToken("OAUTH_TOKEN=V"));
        Assert.Null(SoundCloudService.ExtractOAuthToken("not_oauth_token=V"));
    }

    [Fact]
    public void ExtractOAuthToken_NoToken_ReturnsNull()
    {
        Assert.Null(SoundCloudService.ExtractOAuthToken("a=1; b=2"));
        Assert.Null(SoundCloudService.ExtractOAuthToken("oauth_token=")); // пустое значение
        Assert.Null(SoundCloudService.ExtractOAuthToken(null));
        Assert.Null(SoundCloudService.ExtractOAuthToken(""));
    }

    // ======================= client_id regex =======================

    [Fact]
    public void ExtractClientId_FromQuotedJsonInMinifiedJs()
    {
        // Реалистичный кусок ассета: конфиг плеера внутри минифицированного бандла.
        const string js = """
            !function(e){var t={api:"https://api-v2.soundcloud.com",client_id:"1a2B3c4D5e6F7g8H9i0Jk",app:"sc-web"};e.config=t}(window);
            """;

        Assert.Equal("1a2B3c4D5e6F7g8H9i0Jk", SoundCloudClientIdProvider.ExtractClientId(js));
    }

    [Fact]
    public void ExtractClientId_FromAssignmentWithoutQuotedKey()
    {
        const string js = "var c={};c.client_id=\"AbCdEfGhIjKlMnOpQrSt\";c.api=\"https://api-v2.soundcloud.com\";";

        Assert.Equal("AbCdEfGhIjKlMnOpQrSt", SoundCloudClientIdProvider.ExtractClientId(js));
    }

    [Fact]
    public void ExtractClientId_FromUrl()
    {
        const string js = "fetch(\"https://api-v2.soundcloud.com/tracks?client_id=ZzYyXxWwVvUuTtSsRrQq&limit=10\")";

        Assert.Equal("ZzYyXxWwVvUuTtSsRrQq", SoundCloudClientIdProvider.ExtractClientId(js));
    }

    [Fact]
    public void ExtractClientId_NoMatch_ReturnsNull()
    {
        Assert.Null(SoundCloudClientIdProvider.ExtractClientId("var a = 1; // no id here"));
        Assert.Null(SoundCloudClientIdProvider.ExtractClientId(""));
        // Слишком короткий id не считается валидным.
        Assert.Null(SoundCloudClientIdProvider.ExtractClientId("client_id:\"short\""));
    }

    [Fact]
    public void ExtractAssetUrls_ReturnsDistinctJsAssetsInOrder()
    {
        const string html = """
            <html><head>
            <script crossorigin src="https://a-v2.sndcdn.com/assets/0-1a2b3c.js"></script>
            <script crossorigin src="https://a-v2.sndcdn.com/assets/47-9f8e7d.js"></script>
            <script crossorigin src="https://a-v2.sndcdn.com/assets/0-1a2b3c.js"></script>
            <script src="https://example.com/other.js"></script>
            </head></html>
            """;

        var assets = SoundCloudClientIdProvider.ExtractAssetUrls(html);

        Assert.Equal(2, assets.Count);
        Assert.Equal("https://a-v2.sndcdn.com/assets/0-1a2b3c.js", assets[0]);
        Assert.Equal("https://a-v2.sndcdn.com/assets/47-9f8e7d.js", assets[1]);
    }

    [Fact]
    public void IsConnected_WithoutAuthFile_IsFalse()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "obsidian_sc_auth_" + System.Guid.NewGuid().ToString("N") + ".json");
        var service = new SoundCloudService(path);

        Assert.False(service.HasAuthFile);
        Assert.Null(service.GetLastSyncedUtc());
    }

    [Fact]
    public void SaveSessionCookies_AndLastSynced_PersistToAuthFile()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "obsidian_sc_auth_" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SoundCloudService(path);
            var syncedAt = new System.DateTime(2026, 9, 15, 12, 30, 0, System.DateTimeKind.Utc);

            service.SaveSessionCookies("oauth_token=secret; _sc_cookie=1");
            service.SetLastSyncedUtc(syncedAt);

            Assert.True(service.HasAuthFile);
            Assert.Equal("oauth_token=secret; _sc_cookie=1", service.GetCookies());
            Assert.Equal(syncedAt, service.GetLastSyncedUtc());

            // Disconnect удаляет файл сессии.
            service.Disconnect();
            Assert.False(service.HasAuthFile);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AuthStore_UserId_PersistsAcrossLoadSave()
    {
        // UserId (кэш GET /me) должен пережить перезапись auth-файла при сохранении last_synced.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "obsidian_sc_auth_" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SoundCloudAuthStore(path);
            var file = store.Load();
            Assert.Null(file.UserId); // пустой файл — UserId ещё нет

            file.UserId = "1234567";
            file.Cookies = "oauth_token=secret; _sc_cookie=1";
            store.Save(file);

            // Перезапись других полей не теряет UserId (Load → мутация → Save).
            var reloaded = store.Load();
            reloaded.LastSyncedAtUtc = "2026-09-15T00:00:00Z";
            store.Save(reloaded);

            Assert.Equal("1234567", store.Load().UserId);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    // ==================== AAC HLS (качество веб-плеера) ====================

    /// <summary>Реальный набор транскодингов 2025+: AAC sq/lq, mp3 hls, progressive mp3.</summary>
    private const string AacTranscodingsFixture = """
        {
          "id": 2203706807,
          "title": "Holding",
          "duration": 142420,
          "streamable": true,
          "media": {
            "transcodings": [
              { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:1/aac-sq", "quality": "sq", "format": { "protocol": "hls", "mime_type": "audio/mp4; codecs=\"mp4a.40.2\"" } },
              { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:1/aac-lq", "quality": "lq", "format": { "protocol": "hls", "mime_type": "audio/mp4; codecs=\"mp4a.40.2\"" } },
              { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:1/hls-mp3", "quality": "sq", "format": { "protocol": "hls", "mime_type": "audio/mpeg" } },
              { "url": "https://api-v2.soundcloud.com/media/soundcloud:tracks:1/progressive", "quality": "sq", "format": { "protocol": "progressive", "mime_type": "audio/mpeg" } }
            ]
          }
        }
        """;

    [Fact]
    public void PickHlsAacUrl_PrefersSqQuality()
    {
        var track = SoundCloudService.ParseTrackJson(AacTranscodingsFixture)!;

        var url = SoundCloudService.PickHlsAacUrl(track);

        Assert.Equal("https://api-v2.soundcloud.com/media/soundcloud:tracks:1/aac-sq", url);
    }

    [Fact]
    public void PickHlsAacUrl_FallsBackToLq_WhenSqMissing()
    {
        var track = SoundCloudService.ParseTrackJson("""
            {"id":1,"media":{"transcodings":[
              {"url":"https://x/aac-lq","quality":"lq","format":{"protocol":"hls","mime_type":"audio/mp4; codecs=\"mp4a.40.2\""}},
              {"url":"https://x/hls-mp3","quality":"sq","format":{"protocol":"hls","mime_type":"audio/mpeg"}}
            ]}}
            """)!;

        // Только lq AAC (96 kbps ≈ mp3 128 по качеству): берём его, mp3-варианты
        // не путаем с AAC по mime.
        Assert.Equal("https://x/aac-lq", SoundCloudService.PickHlsAacUrl(track));
    }

    [Fact]
    public void PickHlsAacUrl_NoAacTranscoding_ReturnsNull()
    {
        // Старый/усечённый ответ: только mp3-варианты.
        var track = SoundCloudService.ParseTrackJson("""
            {"id":1,"media":{"transcodings":[
              {"url":"https://x/hls-mp3","format":{"protocol":"hls","mime_type":"audio/mpeg"}},
              {"url":"https://x/progressive","format":{"protocol":"progressive","mime_type":"audio/mpeg"}}
            ]}}
            """)!;

        Assert.Null(SoundCloudService.PickHlsAacUrl(track));
    }

    [Fact]
    public void PickHlsAacUrl_ProgressiveAac_IsIgnored()
    {
        // AAC вне HLS (progressive audio/mp4) не подходит для склейки сегментов.
        var track = SoundCloudService.ParseTrackJson("""
            {"id":1,"media":{"transcodings":[
              {"url":"https://x/aac-prog","format":{"protocol":"progressive","mime_type":"audio/mp4"}}
            ]}}
            """)!;

        Assert.Null(SoundCloudService.PickHlsAacUrl(track));
    }

    private const string AacMediaPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-TARGETDURATION:10
        #EXT-X-PLAYLIST-TYPE:VOD
        #EXT-X-MAP:URI="init/123456.m4s"
        #EXTINF:9.984,
        playlist/seg0.m4s
        #EXTINF:10.011,
        playlist/seg1.m4s
        #EXTINF:2.005,
        playlist/seg2.m4s
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void ParseHlsAacPlaylist_ReadsInitSegmentsAndDuration()
    {
        var pl = SoundCloudService.ParseHlsAacPlaylist(AacMediaPlaylist,
            new System.Uri("https://cdn.hls/hi/playlist.m3u8"))!;

        Assert.Equal("https://cdn.hls/hi/init/123456.m4s", pl.InitUrl);
        Assert.Equal(3, pl.Segments.Count);
        Assert.Equal("https://cdn.hls/hi/playlist/seg0.m4s", pl.Segments[0]);
        Assert.Equal("https://cdn.hls/hi/playlist/seg2.m4s", pl.Segments[2]);
        // Сумма EXTINF — точная длительность для патча fMP4.
        Assert.Equal(9.984 + 10.011 + 2.005, pl.TotalSeconds, 3);
    }

    [Fact]
    public void ParseHlsAacPlaylist_AbsoluteUrlsKeptAsIs()
    {
        var pl = SoundCloudService.ParseHlsAacPlaylist(
            """
            #EXTM3U
            #EXT-X-MAP:URI="https://cdn.hls/init.m4s"
            #EXTINF:4.2,
            https://cdn.hls/media/seg0.m4s
            #EXT-X-ENDLIST
            """,
            new System.Uri("https://other.example/playlist.m3u8"))!;

        Assert.Equal("https://cdn.hls/init.m4s", pl.InitUrl);
        Assert.Equal("https://cdn.hls/media/seg0.m4s", pl.Segments.Single());
        Assert.Equal(4.2, pl.TotalSeconds, 3);
    }

    [Fact]
    public void ParseHlsAacPlaylist_NoMapOrExtinf_DegradesGracefully()
    {
        var pl = SoundCloudService.ParseHlsAacPlaylist(
            """
            #EXTM3U
            #EXTINF:3.0
            seg0.m4s
            #EXT-X-ENDLIST
            """,
            new System.Uri("https://cdn/playlist.m3u8"))!;

        // Без EXT-X-MAP init нет (каскад отвергнет склейку), без запятой в EXTINF
        // длительность всё равно считана.
        Assert.Null(pl.InitUrl);
        Assert.Single(pl.Segments);
        Assert.Equal(3.0, pl.TotalSeconds, 3);
    }
}

/// <summary>
/// Отбор играбельной копии DRM-трека (/search/tracks): оригинал исключается,
/// slowed/instrumental-варианты фильтруются, побеждает копия с совпадающей
/// длительностью без рекламной модели. Фикстура повторяет реальный ответ поиска
/// по «ksuuvi jiggy» (оригинал AD_SUPPORTED, копия от Didi — BLACKBOX).
/// </summary>
public class SoundCloudReuploadRankingTests
{
    private const string SearchFixture = """
        {"collection":[
          {"id":1960321179,"title":"jiggy (feat. xaviersobased)","duration":102852,"streamable":true,"playback_count":52000,"policy":"MONETIZE","monetization_model":"AD_SUPPORTED","user":{"username":"ksuuvi"},"media":{"transcodings":[]}},
          {"id":1961998451,"title":"Jiggy Wit It Xaviersobased/Ksuuvi","duration":102864,"streamable":true,"playback_count":3100,"policy":"MONETIZE","monetization_model":"BLACKBOX","user":{"username":"Didi"},"media":{"transcodings":[]}},
          {"id":2019821309,"title":"jiggy - ksuuvi (Instrumental) (ft. xaviersobased)","duration":112174,"streamable":true,"playback_count":900,"policy":"MONETIZE","monetization_model":"BLACKBOX","user":{"username":"LDXBZ"},"media":{"transcodings":[]}},
          {"id":2093601288,"title":"ksuuvi, xaviersobased - jiggy (slowed & reverb)","duration":128647,"streamable":true,"playback_count":700,"policy":"MONETIZE","monetization_model":"BLACKBOX","user":{"username":"mr white"},"media":{"transcodings":[]}},
          {"id":2313227855,"title":"ksuuvi - jiggy","duration":95302,"streamable":true,"playback_count":140,"policy":"MONETIZE","monetization_model":"BLACKBOX","user":{"username":"z0kas"},"media":{"transcodings":[]}},
          {"id":2000000001,"title":"jiggy ksuuvi","duration":102900,"streamable":false,"playback_count":50,"policy":null,"monetization_model":"BLACKBOX","user":{"username":"shadow"},"media":{"transcodings":[]}},
          {"id":2000000002,"title":"jiggy ksuuvi reupload","duration":102900,"streamable":true,"playback_count":80,"policy":"MONETIZE","monetization_model":"AD_SUPPORTED","user":{"username":"adboy"},"media":{"transcodings":[]}}
        ]}
        """;

    [Fact]
    public void RankReuploadCandidates_PicksDurationMatchWithoutAdModel()
    {
        var ranked = SoundCloudService.RankReuploadCandidates(
            SearchFixture, "jiggy (feat. xaviersobased)", "ksuuvi",
            durationMs: 102852, excludeScId: "1960321179");

        // Копия с совпадающей длительностью и без рекламной модели — первая;
        // укороченная копия z0kas проходит, но после неё.
        Assert.Equal(1961998451, ranked[0].Id);
        Assert.Contains(ranked, r => r.Id == 2313227855);
        Assert.Equal(2, ranked.Count);

        // Фильтры: оригинал, slowed, instrumental, не-streamable и AD-копии не в списке.
        Assert.DoesNotContain(ranked, r => r.Id == 1960321179);
        Assert.DoesNotContain(ranked, r => r.Id == 2093601288);
        Assert.DoesNotContain(ranked, r => r.Id == 2019821309);
        Assert.DoesNotContain(ranked, r => r.Id == 2000000001);
        Assert.DoesNotContain(ranked, r => r.Id == 2000000002);
    }

    [Fact]
    public void RankReuploadCandidates_TooFarDuration_Rejected()
    {
        // Копия с расхождением длительности больше 10 c — не кандидат (чужая версия:
        // ускорение/обрезка), даже если название и артист совпадают.
        var fixture = """
            {"collection":[
              {"id":3000000001,"title":"struggle gang","duration":87275,"streamable":true,"playback_count":5000,"policy":"MONETIZE","monetization_model":"BLACKBOX","user":{"username":"someone"},"media":{"transcodings":[]}}
            ]}
            """;
        var ranked = SoundCloudService.RankReuploadCandidates(
            fixture, "Struggle Gang", "xaviersobased",
            durationMs: 107275, excludeScId: "2385890832");

        Assert.Empty(ranked);
    }
}

/// <summary>
/// Жизненный цикл диагностики веб-сессии: сохранение новых cookies (вход/переподключение
/// аккаунта) сбрасывает флаг «сессия истекла» и поднимает SessionChanged — по нему
/// MainViewModel чистит чёрный список провалов резолва (MONETIZE-треки, помеченные
/// мёртвыми при протухшей сессии, ретраятся сразу).
/// </summary>
public class SoundCloudSessionTests
{
    private static string TempAuthDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "obsidian-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveSessionCookies_RaisesSessionChanged()
    {
        var dir = TempAuthDir();
        try
        {
            var svc = new SoundCloudService(Path.Combine(dir, "sc_auth.json"));
            Assert.False(svc.IsWebSessionExpired);

            var fired = 0;
            svc.SessionChanged += (_, _) => fired++;

            svc.SaveSessionCookies("oauth_token=tok; sc_anonymous_id=abc");

            Assert.Equal(1, fired);
            Assert.False(svc.IsWebSessionExpired);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Disconnect_ClearsSessionState()
    {
        var dir = TempAuthDir();
        try
        {
            var path = Path.Combine(dir, "sc_auth.json");
            var svc = new SoundCloudService(path);
            svc.SaveSessionCookies("oauth_token=tok");
            Assert.True(svc.HasAuthFile);

            svc.Disconnect();

            Assert.False(svc.HasAuthFile);
            Assert.False(svc.IsWebSessionExpired);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
