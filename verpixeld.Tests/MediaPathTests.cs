using verpixeld.MediaPlayer;

namespace verpixeld.Tests;

public class MediaPathTests
{
    [Fact]
    public void Sanitize_drops_directory_components()
    {
        Assert.Equal("evil.mp4", MediaPath.SanitizeFileName(@"..\..\evil.mp4"));
        Assert.Equal("clip.mp4", MediaPath.SanitizeFileName("folder/clip.mp4"));
    }

    [Fact]
    public void SafeUploadPath_stays_under_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "verpixeld-upload-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dest = MediaPath.SafeUploadPath(root, @"..\..\evil.mp4");
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "evil.mp4")), dest);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Redact_strips_smb_userinfo()
    {
        var raw = "smb://domain;user:s3cret@nas/media/clip.mp4";
        Assert.Equal("smb://nas/media/clip.mp4", MediaPath.Redact(raw));
    }

    [Fact]
    public void Redact_leaves_local_paths_alone()
    {
        Assert.Equal(@"D:\videos\a.mp4", MediaPath.Redact(@"D:\videos\a.mp4"));
    }
}
