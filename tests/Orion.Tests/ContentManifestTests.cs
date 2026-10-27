using Orion.Core.Mods;

namespace Orion.Tests;

/// <summary>内容清单与更新检查（全部离线，用假的版本来源）。</summary>
public sealed class ContentManifestTests : IDisposable
{
    private readonly string _root;

    public ContentManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orion-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ManifestRoundTripsThroughDisk()
    {
        var manifest = ContentManifest.Load(_root);
        manifest.Upsert(new ContentEntry
        {
            ProjectId = "sodium",
            VersionId = "v1",
            FileName = "sodium-0.5.11.jar",
            Sha1 = "abc",
            Title = "Sodium",
            VersionNumber = "0.5.11",
            Source = "modrinth",
        });

        await manifest.SaveAsync();

        var reloaded = ContentManifest.Load(_root);
        var entry = reloaded.Find("sodium");
        Assert.NotNull(entry);
        Assert.Equal("sodium-0.5.11.jar", entry!.FileName);
        Assert.Equal("0.5.11", entry.VersionNumber);
        Assert.True(entry.IsTracked);
    }

    [Fact]
    public void UpsertReplacesByProjectIdThenFileName()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { ProjectId = "p1", FileName = "a.jar", VersionId = "v1" });
        manifest.Upsert(new ContentEntry { ProjectId = "p1", FileName = "a-2.jar", VersionId = "v2" });

        Assert.Single(manifest.Entries);
        Assert.Equal("v2", manifest.Entries[0].VersionId);
        Assert.Equal("a-2.jar", manifest.Entries[0].FileName);
    }

    [Fact]
    public void ManualEntriesAreNotTracked()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { FileName = "handmade.jar", Source = "manual" });

        Assert.False(manifest.Entries[0].IsTracked);
    }

    [Fact]
    public void CorruptManifestFallsBackToEmpty()
    {
        File.WriteAllText(Path.Combine(_root, ContentManifest.FileName), "{ not json");
        var manifest = ContentManifest.Load(_root);

        Assert.Empty(manifest.Entries);
        Assert.Equal(ContentManifest.CurrentVersion, manifest.Version);
    }

    [Fact]
    public async Task CheckerReportsNewerVersionWithUrlAndDependencies()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry
        {
            ProjectId = "sodium",
            VersionId = "old",
            FileName = "sodium-old.jar",
            Title = "Sodium",
            VersionNumber = "0.5.11",
        });

        var source = new FakeVersionSource
        {
            ["sodium"] =
            [
                new ModrinthVersion
                {
                    Id = "new",
                    ProjectId = "sodium",
                    VersionNumber = "0.6.0",
                    VersionType = "release",
                    DatePublished = DateTimeOffset.Now,
                    Files = [new ModrinthFile { Filename = "sodium-new.jar", Url = "https://example/sodium.jar", Primary = true, Hashes = { ["sha1"] = "deadbeef" } }],
                    Dependencies = [new ModrinthDependency { ProjectId = "fabric-api", DependencyType = "required" }],
                },
            ],
        };

        var checker = new ModUpdateChecker(source);
        var candidates = await checker.CheckAsync(manifest, "1.21.1", "fabric");

        var candidate = Assert.Single(candidates);
        Assert.Equal("new", candidate.LatestVersionId);
        Assert.Equal("sodium-new.jar", candidate.LatestFileName);
        Assert.Equal("https://example/sodium.jar", candidate.DownloadUrl);
        Assert.Equal("deadbeef", candidate.Sha1);
        Assert.Contains("fabric-api", candidate.Dependencies);
    }

    [Fact]
    public async Task CheckerSkipsProjectsAlreadyOnTheLatestVersion()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { ProjectId = "sodium", VersionId = "same", FileName = "sodium.jar" });

        var source = new FakeVersionSource
        {
            ["sodium"] =
            [
                new ModrinthVersion
                {
                    Id = "same",
                    VersionNumber = "0.6.0",
                    Files = [new ModrinthFile { Filename = "sodium.jar", Url = "https://example/s.jar", Primary = true }],
                },
            ],
        };

        var candidates = await new ModUpdateChecker(source).CheckAsync(manifest, "1.21.1", "fabric");
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task CheckerOnlyLooksAtTrackedEntries()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { FileName = "handmade.jar", Source = "manual" });

        var source = new FakeVersionSource();
        var candidates = await new ModUpdateChecker(source).CheckAsync(manifest, "1.21.1", "fabric");

        Assert.Empty(candidates);
        Assert.Empty(source.Requested);
    }

    [Fact]
    public async Task CheckerSurvivesAProjectThatFailsToFetch()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { ProjectId = "broken", VersionId = "v1", FileName = "broken.jar" });
        manifest.Upsert(new ContentEntry { ProjectId = "sodium", VersionId = "old", FileName = "sodium.jar" });

        var source = new FakeVersionSource
        {
            ["broken"] = null!,
            ["sodium"] =
            [
                new ModrinthVersion
                {
                    Id = "new",
                    VersionNumber = "2",
                    Files = [new ModrinthFile { Filename = "sodium-2.jar", Url = "https://example/s2.jar", Primary = true }],
                },
            ],
        };

        var candidates = await new ModUpdateChecker(source).CheckAsync(manifest, "1.21.1", "fabric");
        Assert.Single(candidates);
        Assert.Equal("sodium", candidates[0].ProjectId);
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/AABBCC/versions/112233/sodium-0.6.0.jar", "AABBCC", "112233")]
    [InlineData("https://cdn.modrinth.com/data/abc/versions/v1/file.jar?x=1", "abc", "v1")]
    [InlineData("https://api.modrinth.com/v2/version/zzz", "", "")]
    [InlineData("https://example.com/files/foo.jar", "", "")]
    public void ModrinthUrlParsesCdnLinks(string url, string expectedProject, string expectedVersion)
    {
        var parsed = ModrinthUrl.TryParse(url, out var project, out var version);

        Assert.Equal(expectedProject.Length > 0, parsed);
        if (parsed)
        {
            Assert.Equal(expectedProject, project);
            Assert.Equal(expectedVersion, version);
        }
    }

    [Fact]
    public void PackEntriesFromModrinthAreTracked()
    {
        var manifest = new ContentManifest();
        manifest.Upsert(new ContentEntry { ProjectId = "p", VersionId = "v", FileName = "a.jar", Source = "pack" });

        Assert.True(manifest.Entries[0].IsTracked);
    }

    private sealed class FakeVersionSource : IVersionSource
    {
        public Dictionary<string, IReadOnlyList<ModrinthVersion>> Versions { get; } = [];

        public List<string> Requested { get; } = [];

        public IReadOnlyList<ModrinthVersion> this[string projectId]
        {
            set => Versions[projectId] = value;
        }

        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, string gameVersion, string loader, CancellationToken ct = default)
        {
            Requested.Add(projectId);
            if (!Versions.TryGetValue(projectId, out var versions) || versions is null)
            {
                throw new HttpRequestException("boom");
            }

            return Task.FromResult(versions);
        }
    }
}
