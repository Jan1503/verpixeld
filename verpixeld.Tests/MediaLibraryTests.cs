using verpixeld.Configuration;

namespace verpixeld.Tests;

public class MediaLibraryTests
{
    [Fact]
    public void Resolve_rejects_parent_segment()
    {
        var root = Path.GetTempPath();
        Assert.Null(MediaLibrary.Resolve(root, "../secret.mp4"));
        Assert.Null(MediaLibrary.Resolve(root, "..\\secret.mp4"));
    }

    [Fact]
    public void Resolve_finds_a_file_under_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "verpixeld-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Movies"));
        var file = Path.Combine(root, "Movies", "clip.mp4");
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(file, MediaLibrary.Resolve(root, "Movies/clip.mp4"));
            Assert.Equal(file, MediaLibrary.Resolve(root, "Movies%2Fclip.mp4"));
            var listed = MediaLibrary.ListRelative(root, MediaLibrary.VideoExtensions);
            Assert.Contains("Movies/clip.mp4", listed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Browse_lists_only_the_current_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "verpixeld-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Movies", "Action"));
        File.WriteAllText(Path.Combine(root, "Movies", "clip.mp4"), "x");
        File.WriteAllText(Path.Combine(root, "Movies", "Action", "nested.mkv"), "x");
        File.WriteAllText(Path.Combine(root, "Movies", "song.mp3"), "x");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "no");
        try
        {
            var top = MediaLibrary.Browse(root, "");
            Assert.Null(top.Error);
            Assert.Equal("", top.CurrentPath);
            Assert.Null(top.ParentPath);
            Assert.Contains(top.Directories, d => d.Name == "Movies" && d.Path == "Movies");
            Assert.Empty(top.Videos);

            var movies = MediaLibrary.Browse(root, "Movies");
            Assert.Equal("Movies", movies.CurrentPath);
            Assert.Equal("", movies.ParentPath);
            Assert.Contains(movies.Directories, d => d.Path == "Movies/Action");
            Assert.Contains(movies.Videos, v => v.Path == "Movies/clip.mp4");
            Assert.Contains(movies.AudioFiles, a => a.Path == "Movies/song.mp3");
            Assert.DoesNotContain(movies.Videos, v => v.Name == "nested.mkv");

            Assert.Null(MediaLibrary.ResolveDirectory(root, "../secret"));
            var escaped = MediaLibrary.Browse(root, "../secret");
            Assert.NotNull(escaped.Error);
            Assert.Empty(escaped.Directories);
            Assert.Empty(escaped.Videos);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParentPath_stops_at_root()
    {
        Assert.Null(MediaLibrary.ParentPath(""));
        Assert.Equal("", MediaLibrary.ParentPath("Movies"));
        Assert.Equal("Movies", MediaLibrary.ParentPath("Movies/Action"));
    }
}
