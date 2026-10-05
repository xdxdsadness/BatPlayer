using BatPlayer.Services.YandexMusic;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты разбора download-info Яндекс Музыки: парсер (YmJsonParser.ParseDownloadInfo)
/// и выбор лучшего варианта (YmService.PickBestOption). Регрессия: в современной
/// раскладке поле downloadInfoUrl — дескриптор вместо готовой ссылки (Url пуст) —
/// PickBestOption раньше отбрасывал такие варианты, и трек не играл вовсе.
/// Чистые функции — сеть не используется.
/// </summary>
public class YmDownloadInfoTests
{
    /// <summary>Реальная раскладка API (2025+): result[] с downloadInfoUrl и bitrateInKbps.</summary>
    private const string ModernLayout = """
        {"result":[
          {"codec":"mp3","gain":false,"preview":false,
           "downloadInfoUrl":"https://storage.mds.yandex.net/file-download-info/67443_fb800ea7.215744789.6.148176491/320?sign=abc&ts=6aaadd6a",
           "direct":false,"bitrateInKbps":320},
          {"codec":"mp3","gain":false,"preview":false,
           "downloadInfoUrl":"https://storage.mds.yandex.net/file-download-info/67443_fb800ea7.215744789.1.148176491/2?sign=def&ts=6aaadd6a",
           "direct":false,"bitrateInKbps":192}],
         "invocationInfo":{"hostname":"music-resource-provider","exec-duration-millis":4}}
        """;

    [Fact]
    public void ParseDownloadInfo_ModernLayout_ParsesDescriptorOptions()
    {
        var options = YmJsonParser.ParseDownloadInfo(ModernLayout);

        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.Equal("mp3", o.Codec));
        Assert.All(options, o => Assert.Empty(o.Url));
        Assert.All(options, o => Assert.NotEmpty(o.DownloadInfoUrl));
        Assert.Equal(320, options[0].Bitrate);
        Assert.Equal(192, options[1].Bitrate);
    }

    [Fact]
    public void PickBestOption_DescriptorOnlyLayout_ReturnsHighestBitrateDescriptor()
    {
        var options = YmJsonParser.ParseDownloadInfo(ModernLayout);

        var best = YmService.PickBestOption(options);

        Assert.NotNull(best);
        Assert.Equal(320, best!.Bitrate);
        Assert.NotEmpty(best.DownloadInfoUrl);
    }

    [Fact]
    public void PickBestOption_MixedUrlAndDescriptor_PrefersMp3HighestBitrate()
    {
        var options = new List<YmDownloadOption>
        {
            new() { Codec = "aac", Bitrate = 192, Url = "https://cdn/aac192" },
            new() { Codec = "mp3", Bitrate = 192, DownloadInfoUrl = "https://storage/mp3-192" },
            new() { Codec = "mp3", Bitrate = 320, DownloadInfoUrl = "https://storage/mp3-320" }
        };

        var best = YmService.PickBestOption(options);

        Assert.NotNull(best);
        Assert.Equal("mp3", best!.Codec);
        Assert.Equal(320, best.Bitrate);
        Assert.Equal("https://storage/mp3-320", best.DownloadInfoUrl);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("<html>gateway error</html>")]
    [InlineData("")]
    public void ParseDownloadInfo_Garbage_ReturnsEmptyList(string json)
    {
        Assert.Empty(YmJsonParser.ParseDownloadInfo(json));
    }
}
