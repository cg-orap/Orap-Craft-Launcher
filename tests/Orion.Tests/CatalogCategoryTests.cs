using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 下载中心的分类筛选：
/// · 每个页签（整合包/模组/光影/材质）必须发出不同的 project_type facet，否则分类切了列表还一样；
/// · 选中的分类要真的进 facets（Modrinth）并能在 CurseForge 里解析出 id；
/// · 分类名在界面上是中文。
/// </summary>
public sealed class CatalogCategoryTests
{
    [Theory]
    [InlineData("modpack")]
    [InlineData("mod")]
    [InlineData("shader")]
    [InlineData("resourcepack")]
    public void EachKindSendsItsOwnProjectTypeFacet(string projectType)
    {
        var query = new ModrinthSearchQuery { ProjectType = projectType };
        var facets = query.BuildFacets();

        Assert.Contains($"project_type:{projectType}", facets, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentKindsProduceDifferentFacets()
    {
        var pack = new ModrinthSearchQuery { ProjectType = "modpack" }.BuildFacets();
        var mod = new ModrinthSearchQuery { ProjectType = "mod" }.BuildFacets();

        Assert.NotEqual(pack, mod);
    }

    [Fact]
    public void SelectedCategoryIsPartOfTheFacets()
    {
        var query = new ModrinthSearchQuery { ProjectType = "modpack", Category = "technology" };
        var facets = query.BuildFacets();

        Assert.Contains("categories:technology", facets, StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryFacetChangesWithTheSelection()
    {
        var a = new ModrinthSearchQuery { ProjectType = "modpack", Category = "technology" }.BuildFacets();
        var b = new ModrinthSearchQuery { ProjectType = "modpack", Category = "adventure" }.BuildFacets();

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("adventure", "冒险")]
    [InlineData("technology", "科技")]
    [InlineData("magic", "魔法")]
    [InlineData("optimization", "优化")]
    [InlineData("quests", "任务")]
    [InlineData("realistic", "写实")]
    public void SlugsHaveChineseNames(string slug, string expected)
    {
        Assert.Equal(expected, ModCategories.DisplayName(slug));
    }

    [Fact]
    public void ChineseSelectionMapsBackToTheSlug()
    {
        Assert.Equal("technology", ModCategories.SlugOf("科技"));
        Assert.Equal("adventure", ModCategories.SlugOf("冒险"));
        Assert.Null(ModCategories.SlugOf("全部分类"));
    }

    [Fact]
    public void UnknownSlugStillGetsReadableLabel()
    {
        Assert.Equal("Some New Category", ModCategories.DisplayName("some-new-category"));
    }

    [Fact]
    public void CurseForgeCandidatesCoverTheCommonSpellings()
    {
        var candidates = ModCategories.CurseForgeNameCandidates("technology");

        Assert.Contains("科技", candidates);
        Assert.Contains("technology", candidates);
        Assert.Contains("Technology", candidates);
    }

    [Fact]
    public void DisplayNamesDeduplicateAndCap()
    {
        var names = ModCategories.DisplayNames(["technology", "科技", "adventure", "magic", "quests"], 3);

        Assert.Equal(3, names.Count);
        Assert.Equal(["科技", "冒险", "魔法"], names);
    }
}
