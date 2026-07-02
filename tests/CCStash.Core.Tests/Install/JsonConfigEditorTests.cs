using System.Text.Json.Nodes;
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class JsonConfigEditorTests
{
    [Fact]
    public void LoadOrEmpty_returns_empty_object_when_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var root = JsonConfigEditor.LoadOrEmpty(path);

        Assert.Empty(root);
    }

    [Fact]
    public void LoadOrEmpty_returns_empty_object_when_file_malformed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var root = JsonConfigEditor.LoadOrEmpty(path);
            Assert.Empty(root);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_then_LoadOrEmpty_round_trips_and_creates_parent_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ccstash-jce-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "nested", "settings.json");
        var root = new JsonObject { ["a"] = 1 };

        JsonConfigEditor.Save(path, root);
        var loaded = JsonConfigEditor.LoadOrEmpty(path);

        Assert.Equal(1, (int)loaded["a"]!);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void EnsureArrayEntry_appends_when_absent_and_is_idempotent()
    {
        var container = new JsonObject();

        var firstAlreadyPresent = JsonConfigEditor.EnsureArrayEntry(
            container, "items",
            isMatch: e => (string?)e["id"] == "x",
            buildEntry: () => new JsonObject { ["id"] = "x" });
        var secondAlreadyPresent = JsonConfigEditor.EnsureArrayEntry(
            container, "items",
            isMatch: e => (string?)e["id"] == "x",
            buildEntry: () => new JsonObject { ["id"] = "x" });

        Assert.False(firstAlreadyPresent);
        Assert.True(secondAlreadyPresent);
        Assert.Single(container["items"]!.AsArray());
    }

    [Fact]
    public void EnsureChild_sets_when_absent_reports_already_present_when_equal()
    {
        var container = new JsonObject();

        var first = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v"));
        var second = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v"));
        var third = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v2"));

        Assert.False(first);
        Assert.True(second);
        Assert.False(third);
        Assert.Equal("v2", (string?)container["k"]);
    }

    [Fact]
    public void RemoveArrayEntries_removes_only_matching_entries()
    {
        var container = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "x" },
                new JsonObject { ["id"] = "y" }),
        };

        var removed = JsonConfigEditor.RemoveArrayEntries(container, "items", e => (string?)e["id"] == "x");

        Assert.Equal(1, removed);
        Assert.Single(container["items"]!.AsArray());
        Assert.Equal("y", (string?)container["items"]![0]!["id"]);
    }

    [Fact]
    public void RemoveChild_removes_key_and_reports_whether_it_existed()
    {
        var container = new JsonObject { ["k"] = "v" };

        Assert.True(JsonConfigEditor.RemoveChild(container, "k"));
        Assert.False(JsonConfigEditor.RemoveChild(container, "k"));
    }
}
