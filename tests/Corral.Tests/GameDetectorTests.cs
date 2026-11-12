using Microsoft.Win32;
using Corral.Core;

namespace Corral.Tests;

public class GameDetectorTests
{
    /// <summary>Sans Game Bar ni lanceur : seuls les dossiers par défaut comptent (indépendant de la machine de test).</summary>
    static GameDetector NoGameBar() => new() { Hive = () => null, LauncherFolders = Array.Empty<string> };

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe")]
    [InlineData(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    [InlineData(@"C:\Program Files (x86)\GOG Galaxy\Games\The Witcher 3 Wild Hunt\bin\x64\witcher3.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 2042\BF2042.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Rainbow Six Siege\RainbowSix.exe")]
    [InlineData(@"E:\XboxGames\Halo Infinite\Content\HaloInfinite.exe")]
    [InlineData(@"C:\Program Files\Rockstar Games\Grand Theft Auto V\GTA5.exe")]
    [InlineData(@"C:\Users\x\AppData\Roaming\itch\apps\celeste\Celeste.exe")]
    // Des mots qui ressemblent à des outils sans en être
    [InlineData(@"C:\Steam\steamapps\common\Crash Bandicoot N. Sane Trilogy\CrashBandicootNSaneTrilogy.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Dispatch\Dispatch.exe")]
    [InlineData(@"c:/steam/STEAMAPPS/COMMON/game/game.exe")] // casse et séparateurs
    public void GamesInLibrariesAreRecognized(string path) => Assert.True(NoGameBar().IsGame(path), path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Cyberpunk 2077\bin\x64\CrashReporter\CrashReporter.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\_CommonRedist\vcredist\2019\VC_redist.x64.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\BattlEye\BEService_x64.exe")]
    [InlineData(@"C:\Steam\steamapps\common\SteamVR\bin\win64\vrserver.exe")]
    [InlineData(@"C:\Steam\steamapps\common\wallpaper_engine\ui32.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Epic Online Services\EpicOnlineServices.exe")]
    [InlineData(@"C:\Riot Games\Riot Client\RiotClientServices.exe")]
    [InlineData(@"C:\Riot Games\League of Legends\LeagueClient.exe")]
    [InlineData(@"C:\Program Files\Rockstar Games\Launcher\Launcher.exe")]
    [InlineData(@"C:\Program Files\Rockstar Games\Social Club\SocialClubHelper.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 2042\__Installer\Touchup.exe")]
    [InlineData(@"C:\Program Files\Epic Games\UE_5.4\Engine\Binaries\Win64\UnrealEditor.exe")]
    public void ToolsAndOtherProgramsAreNotGames(string? path) => Assert.False(NoGameBar().IsGame(path), path);

    [Fact]
    public void GamesKnownToTheGameBarAreRecognized()
    {
        // Fausse ruche : HKCU\Software\CorralTests_…\System\GameConfigStore\Children\1
        var root = @"Software\CorralTests_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var child = Registry.CurrentUser.CreateSubKey(root + @"\System\GameConfigStore\Children\1"))
                child.SetValue("MatchedExeFullPath", @"C:\Games\Standalone\Game.exe");
            using (var launcher = Registry.CurrentUser.CreateSubKey(root + @"\System\GameConfigStore\Children\2"))
                launcher.SetValue("MatchedExeFullPath", @"C:\Games\Standalone\GameLauncher.exe");
            var detector = new GameDetector { Hive = () => Registry.CurrentUser.OpenSubKey(root), LauncherFolders = Array.Empty<string> };

            Assert.True(detector.IsGame(@"c:\games\standalone\game.exe"));
            Assert.False(detector.IsGame(@"C:\Games\Standalone\GameLauncher.exe")); // lanceur, même listé par Windows
            Assert.False(detector.IsGame(@"C:\Games\Standalone\Other.exe"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void UnreadableHiveOnlyLeavesLibraries()
    {
        var detector = new GameDetector { Hive = () => throw new UnauthorizedAccessException(), LauncherFolders = () => throw new IOException() };
        Assert.False(detector.IsGame(@"C:\Games\Standalone\Game.exe"));
        Assert.True(detector.IsGame(@"C:\Steam\steamapps\common\Game\Game.exe"));
    }

    [Fact]
    public void GamesInFoldersDeclaredByLaunchersAreRecognized()
    {
        var detector = new GameDetector
        {
            Hive = () => null,
            // Epic dans D:\Fortnite, Ubisoft avec des « / », et des dossiers trop larges qui doivent être ignorés
            LauncherFolders = () => new[] { @"D:\Fortnite", "E:/Jeux/Far Cry 6/", @"C:\", @"C:\Program Files", @"C:\Users\moi", "relatif\\jeu", "" },
        };
        Assert.True(detector.IsGame(@"D:\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"));
        Assert.True(detector.IsGame(@"e:\jeux\far cry 6\bin\FarCry6.exe"));
        Assert.False(detector.IsGame(@"D:\Fortnite\FortniteGame\Binaries\Win64\CrashReportClient.exe"));
        Assert.False(detector.IsGame(@"D:\FortniteTools\tool.exe")); // même début de nom, autre dossier
        Assert.False(detector.IsGame(@"C:\Program Files\Mozilla Firefox\firefox.exe"));
        Assert.False(detector.IsGame(@"C:\Users\moi\Downloads\app.exe"));
    }

    [Theory]
    [InlineData(@"D:\Fortnite", @"D:\Fortnite\")]
    [InlineData(@"D:/Jeux/Far Cry 6/", @"D:\Jeux\Far Cry 6\")]
    [InlineData(@"C:\Users\moi\Jeux\Celeste", @"C:\Users\moi\Jeux\Celeste\")]
    [InlineData(@"D:\", null)]
    [InlineData(@"C:\Windows", null)]
    [InlineData(@"C:\Program Files (x86)\", null)]
    [InlineData(@"C:\Users\moi", null)]
    [InlineData(@"jeux\celeste", null)]
    [InlineData("  ", null)]
    public void InstallFoldersThatAreTooBroadAreIgnored(string folder, string? expected) =>
        Assert.Equal(expected, GameDetector.AsInstallFolder(folder));

    [Fact]
    public void EpicManifestGivesItsInstallLocation()
    {
        Assert.Equal(@"D:\Fortnite", GameDetector.EpicInstallLocation("""{ "DisplayName": "Fortnite", "InstallLocation": "D:\\Fortnite" }"""));
        Assert.Null(GameDetector.EpicInstallLocation("""{ "DisplayName": "Sans dossier" }"""));
        Assert.Null(GameDetector.EpicInstallLocation("[1, 2]"));
        Assert.Null(GameDetector.EpicInstallLocation("pas du JSON"));
    }
}
