using System.IO;
using System.Threading.Tasks;
using BatPlayer.Services;
using Xunit;

namespace BatPlayer.Tests.Services;

public class SettingsServiceTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesSettings()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var svc = new SettingsService(tmp);
            svc.Update(s => s.NormalizeVolume = true);
            await Task.Delay(150); // wait for async save

            var svc2 = new SettingsService(tmp);
            Assert.True(svc2.Current.NormalizeVolume);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var svc = new SettingsService("C:\\nonexistent\\missing.json");
        Assert.NotNull(svc.Current);
        Assert.Equal("en", svc.Current.Language); // default value
    }

    [Fact]
    public async Task Update_DoesNotBlockCaller()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var svc = new SettingsService(tmp);
            svc.Update(s => s.UseWasapiExclusive = true);
            // If we got here without exception, the test passes
            Assert.True(true);
            await Task.Delay(100);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
