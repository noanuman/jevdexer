namespace Jevdexer.Discovery.Tests;

public class ParsingTests
{
    [Fact]
    public void Vdf_reads_library_paths_with_escapes_and_comments()
    {
        const string text = """
            "libraryfolders"
            {
                // comment
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "228980" "123" } }
                "1" { "path" "D:\\rrardfds" }
            }
            """;
        var root = Vdf.Parse(text).Child("libraryfolders")!;

        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Child("0")!.Get("path"));
        Assert.Equal(@"D:\rrardfds", root.Child("1")!.Get("path"));
        Assert.Equal("123", root.Child("0")!.Child("apps")!.Get("228980"));
    }

    [Theory]
    [InlineData(@"C:\x\app.exe,0", @"C:\x\app.exe")]
    [InlineData(@"""C:\x\app.exe"",-101", @"C:\x\app.exe")]
    [InlineData(@"C:\x\app.ico", null)]
    [InlineData(@"C:\x,y\app.exe", @"C:\x,y\app.exe")]
    public void DisplayIcon_exe_extraction(string icon, string? expected) =>
        Assert.Equal(expected, PathUtil.ExeFromIconSpec(icon));

    [Theory]
    [InlineData(@"C:\ProgramData\Package Cache\{guid}\dotnet-sdk.exe", true)]
    [InlineData(@"C:\Program Files\App\unins000.exe", true)]
    [InlineData(@"C:\Program Files\App\app.exe", false)]
    public void Installer_exes_are_not_apps(string exe, bool expected) =>
        Assert.Equal(expected, NotAnApp.ByExe(exe));
}
