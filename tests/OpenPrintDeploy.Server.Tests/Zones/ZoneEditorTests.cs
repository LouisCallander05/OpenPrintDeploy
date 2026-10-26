using System.Reflection;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Server.Directory;
using Xunit;
using ZonePage = OpenPrintDeploy.Server.Components.Pages.Zones;

namespace OpenPrintDeploy.Server.Tests.Zones;

public sealed class ZoneEditorTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public async Task Edit_OpensFromSnapshot_WhileDirectoryIsStillPending()
    {
        var directory = new DelayedDirectory();
        using var page = CreatePage(directory, out var zone);

        var opening = Edit(page, zone.Id);

        Assert.False(opening.IsCompleted);
        Assert.True(Field<bool>(page, "_showForm"));
        var model = Field<ZoneInput>(page, "_model");
        Assert.Equal(zone.Name, model.Name);
        Assert.Equal(zone.Printers.Single().Id, Assert.Single(model.PrinterIds));
        Assert.Equal(Sid, Assert.Single(model.Rules).GroupName);
        model.Name = "Changed while LDAP is pending";
        Assert.Equal("Staff", zone.Name);

        directory.Complete();
        await opening;
        Assert.Equal("Staff group", model.Rules[0].GroupName);
        Assert.Equal("Changed while LDAP is pending", model.Name);
    }

    [Fact]
    public async Task Edit_ReusesNamesAndCatalog_WithoutMoreDirectoryRequests()
    {
        var directory = new DelayedDirectory();
        using var page = CreatePage(directory, out var zone);
        var opening = Edit(page, zone.Id);
        directory.Complete();
        await opening;

        await Edit(page, zone.Id);

        Assert.Equal("Staff group", Field<ZoneInput>(page, "_model").Rules[0].GroupName);
        Assert.Equal(1, directory.Searches);
        Assert.Equal(1, directory.NameLookups);
    }

    [Fact]
    public async Task LateDirectoryResults_DoNotOverwriteTypedGroup()
    {
        var directory = new DelayedDirectory();
        using var page = CreatePage(directory, out var zone);
        var opening = Edit(page, zone.Id);
        var model = Field<ZoneInput>(page, "_model");
        model.Rules[0].GroupName = "Another group";

        directory.Complete();
        await opening;

        Assert.Equal("Another group", model.Rules[0].GroupName);
    }

    [Fact]
    public async Task SwitchingZones_SharesPendingCatalog_AndLeavesNewEditorIntact()
    {
        var directory = new DelayedDirectory();
        using var page = CreatePage(directory, out var first);
        var second = new ZoneEntity { Id = Guid.NewGuid(), Name = "Library" };
        SetField(page, "_zones", new List<ZoneEntity> { first, second });
        var firstOpening = Edit(page, first.Id);
        var firstModel = Field<ZoneInput>(page, "_model");
        var secondOpening = Edit(page, second.Id);
        var secondModel = Field<ZoneInput>(page, "_model");
        Assert.Equal("Library", secondModel.Name);
        Assert.Equal(1, directory.Searches);

        directory.Complete();
        await Task.WhenAll(firstOpening, secondOpening);

        Assert.Same(secondModel, Field<ZoneInput>(page, "_model"));
        Assert.Null(secondModel.Rules[0].GroupName);
        Assert.Equal(Sid, firstModel.Rules[0].GroupName);
    }

    [Fact]
    public async Task InitialCatalog_DoesNotReplaceNewerSearchResults()
    {
        var directory = new DelayedDirectory();
        using var page = CreatePage(directory, out var zone);
        var opening = Edit(page, zone.Id);
        // Simulate a newer typeahead result arriving before the initial catalog.
        SetField(page, "_groupSearchVersion", Field<int>(page, "_groupSearchVersion") + 1);
        IReadOnlyList<DirectoryGroup> newer = [new("S-1-5-21-99", "Library")];
        SetField(page, "_groups", newer);

        directory.Complete();
        await opening;

        Assert.Same(newer, Field<IReadOnlyList<DirectoryGroup>>(page, "_groups"));
    }

    // Exercise the event handler with deliberately suspended LDAP calls. No
    // ZoneService is injected: opening an existing row must use its snapshot.
    private static ZonePage CreatePage(DelayedDirectory directory, out ZoneEntity zone)
    {
        var page = new ZonePage();
        typeof(ZonePage).GetProperty("Directory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, directory);
        zone = new ZoneEntity
        {
            Id = Guid.NewGuid(), Name = "Staff",
            Rules = [new ZoneRuleEntity { GroupSid = Sid }],
            Printers = [new PrinterEntity { Id = Guid.NewGuid(), DisplayName = "Copier", UncPath = @"\\server\copier" }],
        };
        SetField(page, "_zones", new List<ZoneEntity> { zone });
        return page;
    }

    private static Task Edit(ZonePage page, Guid id) => (Task)typeof(ZonePage)
        .GetMethod("StartEditAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [id])!;
    private static T Field<T>(ZonePage page, string name) => (T)typeof(ZonePage)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static void SetField(ZonePage page, string name, object value) => typeof(ZonePage)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private sealed class DelayedDirectory : IDirectoryService
    {
        private readonly TaskCompletionSource<IReadOnlyList<DirectoryGroup>> _catalog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _name = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Searches { get; private set; }
        public int NameLookups { get; private set; }
        public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, int limit, CancellationToken ct = default)
        {
            Searches++;
            return _catalog.Task;
        }
        public Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default)
        {
            NameLookups++;
            return _name.Task;
        }
        public void Complete()
        {
            _catalog.SetResult([new DirectoryGroup(Sid, "Staff group")]);
            _name.SetResult("Staff group");
        }
        public Task<GroupResolution> GetGroupSidsAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
