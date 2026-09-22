using OpenScan.Core.Hosting;
using Xunit;

namespace OpenScan.Tests;

public class DataDirectoryResolverTests : IDisposable
{
    private readonly string _testDir;

    public DataDirectoryResolverTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OpenScanTests_DataDir_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore clean up errors
        }
    }

    [Fact]
    public void Resolve_WithEnvironmentVariable_PrioritizesEnvVar()
    {
        var expectedCustomDir = Path.Combine(_testDir, "from_env");
        Directory.CreateDirectory(expectedCustomDir);

        var resolved = DataDirectoryResolver.Resolve(
            configuredPath: Path.Combine(_testDir, "from_config"),
            getEnvironmentVariable: key => key == DataDirectoryResolver.EnvironmentVariableName ? expectedCustomDir : null,
            baseDirectory: _testDir);

        Assert.Equal(Path.GetFullPath(expectedCustomDir), resolved);
    }

    [Fact]
    public void Resolve_WithConfiguredPath_UsesConfiguredPathWhenEnvVarMissing()
    {
        var expectedConfigDir = Path.Combine(_testDir, "from_config");
        Directory.CreateDirectory(expectedConfigDir);

        var resolved = DataDirectoryResolver.Resolve(
            configuredPath: expectedConfigDir,
            getEnvironmentVariable: _ => null,
            baseDirectory: _testDir);

        Assert.Equal(Path.GetFullPath(expectedConfigDir), resolved);
    }

    [Fact]
    public void Resolve_Fallback_ReturnsLocalDataDirectoryWhenSystemNotAvailable()
    {
        var baseDir = Path.Combine(_testDir, "app_base");
        Directory.CreateDirectory(baseDir);

        var resolved = DataDirectoryResolver.Resolve(
            configuredPath: null,
            getEnvironmentVariable: _ => null,
            baseDirectory: baseDir);

        if (OperatingSystem.IsWindows())
        {
            // Sous Windows, résout %ProgramData%\OpenScan
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            Assert.Equal(Path.Combine(commonAppData, "OpenScan"), resolved);
        }
        else
        {
            // Sous Linux en contexte utilisateur normal (/var/lib/openscan non accessible), repli sur baseDir/data
            Assert.Equal(Path.Combine(baseDir, "data"), resolved);
        }
    }

    [Fact]
    public void TryEnsureWritableDirectory_WritableDirectory_ReturnsTrue()
    {
        var targetDir = Path.Combine(_testDir, "writable_test");

        var result = DataDirectoryResolver.TryEnsureWritableDirectory(targetDir);

        Assert.True(result);
        Assert.True(Directory.Exists(targetDir));
    }

    [Fact]
    public void TryEnsureWritableDirectory_NonWritableDirectory_ReturnsFalse()
    {
        // Chemin non autorisable sous Linux en mode utilisateur non-root
        var invalidPath = "/sys/kernel/test_forbidden_openscan_dir";

        var result = DataDirectoryResolver.TryEnsureWritableDirectory(invalidPath);

        Assert.False(result);
    }
}
