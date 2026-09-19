using OpenScan.Backends.Twain;

namespace OpenScan.Tests;

public class TwainDsmLocatorTests
{
    [Fact]
    public void GetCandidateDsmPaths_DefaultOrdering_PlacesTwainDsmBeforeTwain32()
    {
        var candidates = TwainDsmLocator.GetCandidateDsmPaths(
            windowsDir: @"C:\Windows",
            systemDir: @"C:\Windows\System32",
            appBaseDir: @"C:\OpenScan\Agent",
            preferLegacy: false);

        Assert.NotEmpty(candidates);

        // twaindsm.dll doit être investigué avant twain_32.dll par défaut
        var firstModernIndex = candidates.FindIndex(c => c.Contains("twaindsm.dll", StringComparison.OrdinalIgnoreCase));
        var firstLegacyIndex = candidates.FindIndex(c => c.Contains("twain_32.dll", StringComparison.OrdinalIgnoreCase));

        Assert.True(firstModernIndex >= 0, "twaindsm.dll doit figurer parmi les candidats");
        Assert.True(firstLegacyIndex >= 0, "twain_32.dll doit figurer parmi les candidats");
        Assert.True(firstModernIndex < firstLegacyIndex, "twaindsm.dll doit avoir la priorité par défaut sur twain_32.dll");
    }

    [Fact]
    public void GetCandidateDsmPaths_PreferLegacy_PlacesTwain32BeforeTwainDsm()
    {
        var candidates = TwainDsmLocator.GetCandidateDsmPaths(
            windowsDir: @"C:\Windows",
            systemDir: @"C:\Windows\System32",
            appBaseDir: @"C:\OpenScan\Agent",
            preferLegacy: true);

        Assert.NotEmpty(candidates);

        var firstModernIndex = candidates.FindIndex(c => c.Contains("twaindsm.dll", StringComparison.OrdinalIgnoreCase));
        var firstLegacyIndex = candidates.FindIndex(c => c.Contains("twain_32.dll", StringComparison.OrdinalIgnoreCase));

        Assert.True(firstModernIndex >= 0);
        Assert.True(firstLegacyIndex >= 0);
        Assert.True(firstLegacyIndex < firstModernIndex, "twain_32.dll doit avoir la priorité quand preferLegacy est vrai");
    }

    [Fact]
    public void GetCandidateDsmPaths_WithCustomPath_PlacesCustomPathFirst()
    {
        var custom = @"D:\CustomTwain\twaindsm.dll";
        var candidates = TwainDsmLocator.GetCandidateDsmPaths(
            customPath: custom,
            windowsDir: @"C:\Windows",
            systemDir: @"C:\Windows\System32");

        Assert.NotEmpty(candidates);
        Assert.Equal(custom, candidates[0]);
    }

    [Fact]
    public void GetCandidateDsmPaths_IncludesStandardLocations()
    {
        var candidates = TwainDsmLocator.GetCandidateDsmPaths(
            windowsDir: @"C:\Windows",
            systemDir: @"C:\Windows\System32",
            appBaseDir: @"C:\OpenScan\Agent");

        // Doit inclure System32, SysWOW64, Windows, appBaseDir, et les noms nus
        Assert.Contains(candidates, c => c.EndsWith(@"System32\twaindsm.dll", StringComparison.OrdinalIgnoreCase) || c.EndsWith(@"System32/twaindsm.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => c.EndsWith(@"SysWOW64\twaindsm.dll", StringComparison.OrdinalIgnoreCase) || c.EndsWith(@"SysWOW64/twaindsm.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => c.EndsWith(@"Windows\twain_32.dll", StringComparison.OrdinalIgnoreCase) || c.EndsWith(@"Windows/twain_32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => c.EndsWith(@"twain_32\twain_32.dll", StringComparison.OrdinalIgnoreCase) || c.EndsWith(@"twain_32/twain_32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => string.Equals(c, "twaindsm.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => string.Equals(c, "twain_32.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDiagnosticSummary_ContainsProcessBitnessAndCandidateLogs()
    {
        var logs = new List<string>
        {
            "[FICHIER INTROUVABLE] C:\\Windows\\System32\\twaindsm.dll",
            "[FICHIER INTROUVABLE] C:\\Windows\\twain_32.dll"
        };

        var summary = TwainDsmLocator.BuildDiagnosticSummary(logs);

        Assert.Contains("twaindsm.dll", summary);
        Assert.Contains("twain_32.dll", summary);
        Assert.Contains("Processus=", summary);
        Assert.Contains("OS=", summary);
        Assert.Contains("Diagnostics et solutions recommandées", summary);
    }

    [Fact]
    public void TryLoadDsm_WithNonExistentPath_FailsWithDiagnosticSummary()
    {
        var dummyPath = Path.Combine(Path.GetTempPath(), $"missing-dsm-{Guid.NewGuid():n}.dll");

        var result = TwainDsmLocator.TryLoadDsm(customPath: dummyPath);

        Assert.False(result.Success);
        Assert.Equal(IntPtr.Zero, result.Handle);
        Assert.Null(result.LoadedPath);
        Assert.NotEmpty(result.AttemptLogs);
        Assert.Contains(result.AttemptLogs, l => l.Contains(dummyPath));
        Assert.Contains("Impossible de localiser ou charger le gestionnaire de sources TWAIN", result.DiagnosticSummary);
    }
}
