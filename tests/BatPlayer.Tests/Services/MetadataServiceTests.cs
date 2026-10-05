using System.Threading.Tasks;
using BatPlayer.Services;
using Xunit;

namespace BatPlayer.Tests.Services;

public class MetadataServiceTests
{
    [Fact]
    public async Task ReadAsync_NonExistentFile_ReturnsNull()
    {
        var svc = new MetadataService();
        var result = await svc.ReadAsync("C:\\nonexistent\\no.mp3");
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadAsync_CorruptFile_ReturnsNull()
    {
        var svc = new MetadataService();
        // Reading an empty / random-bytes file should not throw
        var tmp = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllBytesAsync(tmp, new byte[] { 0x00, 0x01, 0x02 });
            var result = await svc.ReadAsync(tmp);
            Assert.Null(result);
        }
        finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
    }

    [Fact]
    public void ExtractCoverBytes_NonExistentFile_ReturnsNull()
    {
        var svc = new MetadataService();
        var bytes = svc.ExtractCoverBytes("C:\\nonexistent\\no.mp3");
        Assert.Null(bytes);
    }
}
