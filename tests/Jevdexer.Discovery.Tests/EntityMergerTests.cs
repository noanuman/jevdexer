namespace Jevdexer.Discovery.Tests;

/// <summary>Each case is a mis-merge or duplicate seen on a real machine.</summary>
public class EntityMergerTests
{
    private const string RiotExe = @"C:\Riot Games\Riot Client\RiotClientServices.exe";

    private static RawApp App(string source, string name, string? exe = null, string? args = null,
        string? aumid = null, string? dir = null, string? id = null, string? publisher = null) =>
        new() { Source = source, Name = name, Exe = exe, Args = args, Aumid = aumid, InstallDir = dir, SourceId = id, Publisher = publisher };

    private static Entity Single(EntityMerger.Result r, string name) =>
        Assert.Single(r.Entities, e => e.Name == name);

    [Fact]
    public void Launcher_products_sharing_one_exe_stay_separate()
    {
        var r = EntityMerger.Merge([
            App("appsfolder", "Riot Client", RiotExe),
            App("shortcut", "Riot Client", RiotExe),
            App("uninstall", "Riot Client", RiotExe, dir: @"C:\Riot Games\Riot Client"),
            App("appsfolder", "League of Legends", RiotExe),
            App("shortcut", "League of Legends", RiotExe, args: "--launch-product=league_of_legends --launch-patchline=live"),
            App("riot", "League of Legends", dir: @"D:\Riot\Riot Games\League of Legends", id: "league_of_legends"),
        ]);

        Assert.Equal(2, r.Entities.Count);
        Assert.Equal("--launch-product=league_of_legends --launch-patchline=live", Single(r, "League of Legends").Args);
        Assert.Contains("riot", Single(r, "League of Legends").Sources);
        Assert.Null(Single(r, "Riot Client").Args);
    }

    [Fact]
    public void Uninstall_entry_with_a_different_name_joins_the_app_using_its_exe()
    {
        // Telegram registers an AUMID with the AppsFolder; its exe arrives only through the shortcut.
        var r = EntityMerger.Merge([
            App("appsfolder", "Telegram", aumid: "TelegramDesktop"),
            App("shortcut", "Telegram", @"D:\Telegram Desktop\Telegram.exe"),
            App("uninstall", "Telegram Desktop", @"D:\Telegram Desktop\Telegram.exe", publisher: "Telegram FZ-LLC"),
        ]);

        var e = Assert.Single(r.Entities);
        Assert.Equal("Telegram", e.Name);
        Assert.Equal("Telegram FZ-LLC", e.Publisher);
    }

    [Fact]
    public void Several_apps_in_one_install_folder_stay_separate()
    {
        var r = EntityMerger.Merge([
            App("uninstall", "Git", dir: @"C:\Program Files\Git"),
            App("appsfolder", "Git Bash", @"C:\Program Files\Git\git-bash.exe"),
            App("appsfolder", "Git GUI", @"C:\Program Files\Git\cmd\git-gui.exe"),
        ]);

        Assert.Equal(2, r.Entities.Count);
        Assert.Single(r.NotLaunchable, e => e.Name == "Git");
    }

    [Fact]
    public void A_games_only_shortcut_joins_its_launcher_record()
    {
        var r = EntityMerger.Merge([
            App("steam", "Counter-Strike 2", dir: @"D:\rrardfds\steamapps\common\Counter-Strike Global Offensive", id: "730"),
            App("shortcut", "CS2", @"D:\rrardfds\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"),
        ]);

        var e = Assert.Single(r.Entities);
        Assert.Equal("Counter-Strike 2", e.Name);
        Assert.Equal(["steam", "shortcut"], e.Sources);
    }

    [Fact]
    public void Games_inside_the_steam_folder_do_not_join_steam()
    {
        var r = EntityMerger.Merge([
            App("uninstall", "Steam", dir: @"C:\Program Files (x86)\Steam"),
            App("appsfolder", "Steam", @"C:\Program Files (x86)\Steam\steam.exe"),
            App("steam", "Rocket League", dir: @"C:\Program Files (x86)\Steam\steamapps\common\rocketleague", id: "252950"),
            App("shortcut", "Rocket League", @"C:\Program Files (x86)\Steam\steamapps\common\rocketleague\Binaries\Win64\RocketLeague.exe"),
        ]);

        Assert.Equal(2, r.Entities.Count);
        Assert.Contains("steam", Single(r, "Rocket League").Sources);
    }

    [Fact]
    public void Per_user_suffix_and_versions_do_not_split_an_app()
    {
        var user = Environment.UserName;
        var r = EntityMerger.Merge([
            App("appsfolder", "Roblox Player", @"C:\Users\x\AppData\Local\Roblox\RobloxPlayerBeta.exe"),
            App("uninstall", $"Roblox Player for {user} "),
            App("appsfolder", "Blender", @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"),
            App("uninstall", "Blender 4.2.1 (x64)"),
        ]);

        Assert.Equal(2, r.Entities.Count);
        Assert.Empty(r.NotLaunchable);
    }

    [Fact]
    public void Ids_are_stable_across_runs()
    {
        RawApp[] raws = [App("appsfolder", "Apex Legends", @"D:\g\Apex\r5apex.exe")];
        Assert.Equal(EntityMerger.Merge(raws).Entities[0].Id, EntityMerger.Merge(raws).Entities[0].Id);
    }
}
