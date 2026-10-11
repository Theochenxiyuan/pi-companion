using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class SkillContentHasherTests
{
    [Fact]
    public void InspectCached_MatchesFullInspectionAndTracksChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-companion-hasher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllText(Path.Combine(root, "SKILL.md"), "---\nname: demo\n---\n");
            var asset = Path.Combine(root, "assets", "a.txt");
            File.WriteAllText(asset, "first");

            var initial = SkillContentHasher.InspectCached(root, isSingleFile: false);
            Assert.Equal(SkillContentHasher.Inspect(root, isSingleFile: false), initial);
            Assert.Equal(initial, SkillContentHasher.InspectCached(root, isSingleFile: false));

            File.WriteAllText(asset, "second, longer");
            var changed = SkillContentHasher.InspectCached(root, isSingleFile: false);
            Assert.NotEqual(initial.Hash, changed.Hash);
            Assert.Equal(SkillContentHasher.Inspect(root, isSingleFile: false), changed);

            File.WriteAllText(Path.Combine(root, "assets", "b.txt"), "added");
            var added = SkillContentHasher.InspectCached(root, isSingleFile: false);
            Assert.Equal(3, added.FileCount);
            Assert.Equal(SkillContentHasher.Inspect(root, isSingleFile: false), added);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
