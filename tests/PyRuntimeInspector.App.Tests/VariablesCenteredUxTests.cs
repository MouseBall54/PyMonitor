using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

[Collection("WPF UI")]
public sealed class VariablesCenteredUxTests
{
    [Fact]
    public async Task ObjectBreadcrumbShowsAncestryAndNavigatesDirectlyWithoutLosingHistory()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("selected", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        var rootCrumb = Assert.Single(viewModel.ObjectBreadcrumbs);
        Assert.Equal("selected", rootCrumb.Label);
        Assert.True(rootCrumb.IsCurrent);
        Assert.Equal("Level 0 · Root", viewModel.ObjectDepthLabel);
        Assert.Equal("History 1 / 1", viewModel.NavigationHistoryLabel);
        Assert.False(viewModel.NavigateBackCommand.CanExecute(null));
        Assert.False(viewModel.NavigateParentCommand.CanExecute(null));

        var root = Assert.Single(viewModel.ObjectRoots);
        var child = root.Children
            .Where(node => node.Kind == ObjectNodeKind.Group)
            .SelectMany(group => group.Children)
            .Single(node => node.Value?.Name == "child");
        await viewModel.SelectObjectNodeAsync(child);

        Assert.Equal("child", viewModel.SelectedObjectName);
        Assert.Collection(viewModel.ObjectBreadcrumbs,
            ancestor =>
            {
                Assert.Equal("selected", ancestor.Label);
                Assert.False(ancestor.IsCurrent);
                Assert.Contains("Go directly", ancestor.ToolTip);
                Assert.Contains("Navigate to ancestor", ancestor.AccessibilityName);
            },
            current =>
            {
                Assert.Equal("child", current.Label);
                Assert.True(current.IsCurrent);
                Assert.Contains("Current object", current.AccessibilityName);
            });
        Assert.Equal("Level 1", viewModel.ObjectDepthLabel);
        Assert.Equal("History 2 / 2", viewModel.NavigationHistoryLabel);
        Assert.Contains("selected", viewModel.BackNavigationLabel);
        Assert.Contains("selected", viewModel.ParentNavigationLabel);
        Assert.Contains("selected", viewModel.ParentNavigationToolTip);
        Assert.Contains("parent selected", viewModel.NavigationLocationDescription);
        Assert.True(viewModel.NavigateBackCommand.CanExecute(null));
        Assert.True(viewModel.NavigateParentCommand.CanExecute(null));

        var ancestorCrumb = viewModel.ObjectBreadcrumbs[0];
        await viewModel.NavigateBreadcrumbAsync(ancestorCrumb);

        Assert.Equal("selected", viewModel.SelectedObjectName);
        Assert.Single(viewModel.ObjectBreadcrumbs);
        Assert.Equal("History 3 / 3", viewModel.NavigationHistoryLabel);
        Assert.Contains("child", viewModel.BackNavigationLabel);

        await viewModel.NavigateBackCommand.ExecuteAsync();
        Assert.Equal("child", viewModel.SelectedObjectName);
        Assert.Equal("History 2 / 3", viewModel.NavigationHistoryLabel);
        Assert.Contains("selected", viewModel.ForwardNavigationLabel);
        Assert.Contains("Alt+Right", viewModel.ForwardNavigationToolTip);

        await viewModel.NavigateForwardCommand.ExecuteAsync();
        Assert.Equal("selected", viewModel.SelectedObjectName);
        Assert.Equal("History 3 / 3", viewModel.NavigationHistoryLabel);
        Assert.DoesNotContain("root-handle", ReleasedHandles(session));
        Assert.DoesNotContain("child-handle", ReleasedHandles(session));
    }

    [Fact]
    public async Task ObjectNameSearchesFilterLoadedNamesIndependentlyWithoutLosingContext()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("dict_test", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        var selected = Assert.Single(viewModel.Variables);
        viewModel.SelectedVariable = selected;
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        var root = Assert.Single(viewModel.ObjectRoots);
        var group = Assert.Single(root.Children, node => node.Kind == ObjectNodeKind.Group);
        var child = Assert.Single(group.Children, node => node.Value?.Name == "child");
        var deep = Assert.Single(group.Children, node => node.Value?.Name == "deep");
        var loop = Assert.Single(group.Children, node => node.Value?.Name == "loop");
        var requestCount = session.Requests.Count;
        var history = viewModel.NavigationHistoryLabel;
        var path = viewModel.SelectedObjectPath;

        Assert.Equal(0, root.Depth);
        Assert.Equal("L0", root.LevelLabel);
        Assert.Equal("Object root", root.ParentContext);
        Assert.Null(root.Parent);
        Assert.Contains("object level 0", root.AccessibilityName, StringComparison.Ordinal);
        Assert.Equal(root, group.Parent);
        Assert.Equal(0, group.Depth);
        Assert.Equal("Section of: dict_test", group.ParentContext);
        Assert.Equal(root, child.Parent);
        Assert.Equal(1, child.Depth);
        Assert.Equal("L1", child.LevelLabel);
        Assert.Equal("Object parent: dict_test", child.ParentContext);
        Assert.Contains(child.Path, child.HierarchyHelpText, StringComparison.Ordinal);

        viewModel.ObjectChildrenSearchText = "CHILD";

        Assert.Equal("child", Assert.Single(viewModel.FilteredObjectChildren).Name);
        Assert.Equal(3, viewModel.ObjectChildren.Count);
        Assert.Contains("1 of 3", viewModel.ObjectChildrenSearchResultLabel);

        group.IsExpanded = false;
        viewModel.ObjectTreeSearchText = "DEEP";

        Assert.True(root.IsSearchVisible);
        Assert.True(group.IsSearchVisible);
        Assert.True(group.IsExpanded);
        Assert.False(child.IsSearchVisible);
        Assert.True(deep.IsSearchVisible);
        Assert.True(deep.IsSearchMatch);
        Assert.False(deep.IsSearchAncestor);
        Assert.True(group.IsSearchAncestor);
        Assert.True(root.IsSearchAncestor);
        Assert.False(loop.IsSearchVisible);
        Assert.Contains("1", viewModel.ObjectTreeSearchResultLabel);
        Assert.Same(root, Assert.Single(viewModel.ObjectRoots));
        Assert.Same(selected, viewModel.SelectedVariable);
        Assert.Equal(path, viewModel.SelectedObjectPath);
        Assert.Equal(history, viewModel.NavigationHistoryLabel);
        Assert.Equal(requestCount, session.Requests.Count);

        viewModel.ClearObjectChildrenSearchCommand.Execute(null);

        Assert.Equal("", viewModel.ObjectChildrenSearchText);
        Assert.Equal(3, viewModel.FilteredObjectChildren.Count);
        Assert.Equal("DEEP", viewModel.ObjectTreeSearchText);
        Assert.False(child.IsSearchVisible);

        viewModel.ClearObjectTreeSearchCommand.Execute(null);

        Assert.Equal("", viewModel.ObjectTreeSearchText);
        Assert.All(group.Children, node => Assert.True(node.IsSearchVisible));
        Assert.All(group.Children, node => Assert.False(node.IsSearchMatch));
        Assert.All(group.Children, node => Assert.False(node.IsSearchAncestor));
        Assert.False(group.IsSearchAncestor);
        Assert.False(root.IsSearchAncestor);
        Assert.False(group.IsExpanded);
        Assert.Equal(requestCount, session.Requests.Count);
    }

    [Fact]
    public async Task ObjectNameSearchesSurviveRefreshAndRestorePreSearchExpansion()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        var rootValue = Value(
            "dict_test",
            "root-handle",
            "root-identity",
            "root-metadata",
            address: "0x100",
            expandable: true);
        session.EnqueueScope(ScopeResult((JsonObject)rootValue.DeepClone()));
        session.EnqueueScope(ScopeResult((JsonObject)rootValue.DeepClone()));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        var initialRoot = Assert.Single(viewModel.ObjectRoots);
        var initialGroup = Assert.Single(initialRoot.Children, node => node.Kind == ObjectNodeKind.Group);
        initialGroup.IsExpanded = false;
        viewModel.ObjectChildrenSearchText = "CHILD";
        viewModel.ObjectTreeSearchText = "DEEP";
        Assert.True(initialGroup.IsExpanded);

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("CHILD", viewModel.ObjectChildrenSearchText);
        Assert.Equal("child", Assert.Single(viewModel.FilteredObjectChildren).Name);
        Assert.Equal("DEEP", viewModel.ObjectTreeSearchText);
        var refreshedRoot = Assert.Single(viewModel.ObjectRoots);
        var refreshedGroup = Assert.Single(refreshedRoot.Children, node => node.Kind == ObjectNodeKind.Group);
        var refreshedDeep = Assert.Single(refreshedGroup.Children, node => node.Value?.Name == "deep");
        var refreshedChild = Assert.Single(refreshedGroup.Children, node => node.Value?.Name == "child");
        Assert.True(refreshedGroup.IsExpanded);
        Assert.True(refreshedDeep.IsSearchVisible);
        Assert.True(refreshedDeep.IsSearchMatch);
        Assert.False(refreshedChild.IsSearchVisible);

        viewModel.ClearObjectTreeSearchCommand.Execute(null);

        Assert.False(refreshedGroup.IsExpanded);
        Assert.All(refreshedGroup.Children, node => Assert.True(node.IsSearchVisible));
    }

    [Fact]
    public async Task ClassTreeSearchMatchesAllNodeFieldsPreservesPathsAndRestoresExpansion()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(membersTruncated: true),
        };
        session.EnqueueScope(ScopeResult(
            Value("selected", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        var allNodes = FlattenClassTree(viewModel.ClassTree).ToArray();
        var semanticNodes = allNodes.Where(IsSemanticClassTreeNode).ToArray();
        var overview = Assert.Single(viewModel.ClassTree, node => node.Label == "Class overview");
        var metaclass = Assert.Single(overview.Children, node => node.Label == "Metaclass");
        var methods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        var render = Assert.Single(methods.Children, node => node.Label == "render");
        var parameters = Assert.Single(render.Children, node => node.Label == "Parameters");
        var width = Assert.Single(parameters.Children, node => node.Label == "width");
        var inheritedGroup = Assert.Single(viewModel.ClassTree, node => node.Label == "Inherited members");
        var inheritedName = Assert.Single(inheritedGroup.Children, node => node.Label == "name");
        var truncatedStatus = Assert.Single(viewModel.ClassTree, node => node.Kind == "status");
        Assert.Equal(0, overview.Depth);
        Assert.Equal("L0", overview.LevelLabel);
        Assert.Null(overview.Parent);
        Assert.Equal("Class tree root", overview.ParentContext);
        Assert.Equal(overview, metaclass.Parent);
        Assert.Equal(1, metaclass.Depth);
        Assert.Equal(methods, render.Parent);
        Assert.Equal(1, render.Depth);
        Assert.Equal(render, parameters.Parent);
        Assert.Equal(2, parameters.Depth);
        Assert.Equal(parameters, width.Parent);
        Assert.Equal(3, width.Depth);
        Assert.Equal("L3", width.LevelLabel);
        Assert.Equal("Tree parent: Parameters", width.ParentContext);
        Assert.Equal("Instance methods > render > Parameters > width", width.HierarchyPath);
        Assert.Contains("tree level 3", width.AccessibilityName, StringComparison.Ordinal);
        Assert.Contains(render.DeclaredBy, render.HierarchyHelpText, StringComparison.Ordinal);
        Assert.Contains(render.Detail, render.HierarchyHelpText, StringComparison.Ordinal);
        Assert.Contains(render.Source, render.HierarchyHelpText, StringComparison.Ordinal);
        Assert.Equal(0, truncatedStatus.Depth);
        overview.IsExpanded = true;
        methods.IsExpanded = false;
        render.IsExpanded = false;
        parameters.IsExpanded = false;
        var requestCount = session.Requests.Count;

        viewModel.ClassTreeSearchText = "  WIDTH   positionalOrKeyword  80 ";

        Assert.True(width.IsSearchVisible);
        Assert.True(width.IsSearchMatch);
        Assert.False(width.IsSearchAncestor);
        Assert.True(parameters.IsSearchVisible);
        Assert.False(parameters.IsSearchMatch);
        Assert.True(parameters.IsSearchAncestor);
        Assert.True(parameters.IsExpanded);
        Assert.True(render.IsSearchAncestor);
        Assert.True(render.IsExpanded);
        Assert.True(methods.IsSearchAncestor);
        Assert.True(methods.IsExpanded);
        Assert.False(overview.IsSearchVisible);
        Assert.Contains($"1 of {semanticNodes.Length} loaded class details match", viewModel.ClassTreeSearchResultLabel);
        Assert.Contains("additional class members omitted", viewModel.ClassTreeSearchResultLabel);
        Assert.Contains("2 of 300 members loaded", viewModel.ClassTreeSearchResultLabel);
        Assert.False(viewModel.IsClassTreeSearchEmpty);

        viewModel.ClassTreeSearchText = "RENDER sample.PY";

        Assert.True(render.IsSearchMatch);
        Assert.False(render.IsSearchAncestor);
        Assert.True(methods.IsSearchAncestor);
        Assert.False(width.IsSearchVisible);

        viewModel.ClassTreeSearchText = "BASE property";

        Assert.True(inheritedName.IsSearchMatch);
        Assert.True(inheritedGroup.IsSearchAncestor);
        Assert.False(methods.IsSearchVisible);

        viewModel.ClassTreeSearchText = "BUILTINS metadata";

        Assert.True(metaclass.IsSearchMatch);
        Assert.True(overview.IsSearchAncestor);

        viewModel.ClassTreeSearchText = "MRO";

        var mro = Assert.Single(overview.Children, node => node.Label == "MRO");
        Assert.False(mro.IsSearchVisible);
        Assert.False(mro.IsSearchMatch);
        Assert.False(mro.IsSearchAncestor);
        Assert.True(viewModel.IsClassTreeSearchEmpty);
        Assert.StartsWith($"0 of {semanticNodes.Length} loaded class details match", viewModel.ClassTreeSearchResultLabel);

        viewModel.ClassTreeSearchText = "Additional class members omitted";

        Assert.False(truncatedStatus.IsSearchVisible);
        Assert.False(truncatedStatus.IsSearchMatch);
        Assert.True(viewModel.IsClassTreeSearchEmpty);

        viewModel.ClassTreeSearchText = "render positionalOrKeyword";

        Assert.True(viewModel.IsClassTreeSearchEmpty);
        Assert.StartsWith($"0 of {semanticNodes.Length} loaded class details match", viewModel.ClassTreeSearchResultLabel);

        viewModel.ClassTreeSearchText = "render missing-token";

        Assert.True(viewModel.IsClassTreeSearchEmpty);
        Assert.StartsWith($"0 of {semanticNodes.Length} loaded class details match", viewModel.ClassTreeSearchResultLabel);
        Assert.All(viewModel.ClassTree, node => Assert.False(node.IsSearchVisible));

        viewModel.ClearClassTreeSearchCommand.Execute(null);

        Assert.Equal("", viewModel.ClassTreeSearchText);
        Assert.False(viewModel.IsClassTreeSearchEmpty);
        Assert.StartsWith($"{semanticNodes.Length} loaded class details", viewModel.ClassTreeSearchResultLabel);
        Assert.Contains("additional class members omitted", viewModel.ClassTreeSearchResultLabel);
        Assert.All(allNodes, node =>
        {
            Assert.True(node.IsSearchVisible);
            Assert.False(node.IsSearchMatch);
            Assert.False(node.IsSearchAncestor);
        });
        Assert.True(overview.IsExpanded);
        Assert.False(methods.IsExpanded);
        Assert.False(render.IsExpanded);
        Assert.False(parameters.IsExpanded);
        Assert.Equal(requestCount, session.Requests.Count);
    }

    [Fact]
    public async Task ClassTreeSearchResetsWhenSelectionBuildsANewTree()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("first", "first-handle", "first-identity", "first-metadata", address: "0x100", expandable: true),
            Value("second", "second-handle", "second-identity", "second-metadata", address: "0x200", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "first");
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        var firstMethods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        firstMethods.IsExpanded = false;
        viewModel.ClassTreeSearchText = "width positionalOrKeyword";
        Assert.True(firstMethods.IsExpanded);

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "second");
        await EventuallyAsync(() => viewModel.SelectedObjectName == "second"
            && viewModel.InspectorState == InspectorPaneState.Ready);

        Assert.Equal("", viewModel.ClassTreeSearchText);
        var secondMethods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        Assert.NotSame(firstMethods, secondMethods);
        Assert.False(secondMethods.IsExpanded);
        Assert.All(FlattenClassTree(viewModel.ClassTree), node =>
        {
            Assert.True(node.IsSearchVisible);
            Assert.False(node.IsSearchMatch);
            Assert.False(node.IsSearchAncestor);
        });
        Assert.False(viewModel.IsClassTreeSearchEmpty);
    }

    [Fact]
    public async Task ClassTreeSearchSurvivesF5AndRestoresStructuralExpansionSnapshot()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        var rootValue = Value(
            "selected",
            "root-handle",
            "root-identity",
            "root-metadata",
            address: "0x100",
            expandable: true);
        session.EnqueueScope(ScopeResult((JsonObject)rootValue.DeepClone()));
        session.EnqueueScope(ScopeResult((JsonObject)rootValue.DeepClone()));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        var firstOverview = Assert.Single(viewModel.ClassTree, node => node.Label == "Class overview");
        var firstMethods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        var firstRender = Assert.Single(firstMethods.Children, node => node.Label == "render");
        var firstParameters = Assert.Single(firstRender.Children, node => node.Label == "Parameters");
        firstOverview.IsExpanded = true;
        firstMethods.IsExpanded = false;
        firstRender.IsExpanded = true;
        firstParameters.IsExpanded = false;
        viewModel.ClassTreeSearchText = "width positionalOrKeyword";
        Assert.True(firstMethods.IsExpanded);
        Assert.True(firstParameters.IsExpanded);

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("width positionalOrKeyword", viewModel.ClassTreeSearchText);
        var refreshedOverview = Assert.Single(viewModel.ClassTree, node => node.Label == "Class overview");
        var refreshedMethods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        var refreshedRender = Assert.Single(refreshedMethods.Children, node => node.Label == "render");
        var refreshedParameters = Assert.Single(refreshedRender.Children, node => node.Label == "Parameters");
        Assert.NotSame(firstMethods, refreshedMethods);
        Assert.True(refreshedMethods.IsSearchAncestor);
        Assert.True(refreshedMethods.IsExpanded);
        Assert.True(refreshedParameters.IsExpanded);
        Assert.True(Assert.Single(refreshedParameters.Children, node => node.Label == "width").IsSearchMatch);
        var classRequestCount = session.Requests.Count(request => request.Method == "classes.describe");

        viewModel.ClearClassTreeSearchCommand.Execute(null);

        Assert.True(refreshedOverview.IsExpanded);
        Assert.False(refreshedMethods.IsExpanded);
        Assert.True(refreshedRender.IsExpanded);
        Assert.False(refreshedParameters.IsExpanded);
        Assert.All(FlattenClassTree(viewModel.ClassTree), node => Assert.True(node.IsSearchVisible));
        Assert.Equal(classRequestCount, session.Requests.Count(request => request.Method == "classes.describe"));
    }

    [Fact]
    public async Task LargeClassTreeSearchDebouncesAndAppliesOnlyTheLatestQuery()
    {
        var session = new UxSession
        {
            ClassDescription = LargeStructuredClassDescription(memberCount: 256, parametersPerMember: 8),
        };
        session.EnqueueScope(ScopeResult(
            Value("selected", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        var semanticNodes = FlattenClassTree(viewModel.ClassTree).Where(IsSemanticClassTreeNode).ToArray();
        Assert.True(semanticNodes.Length > 2_000);
        var methods = Assert.Single(viewModel.ClassTree, node => node.Label == "Instance methods");
        var first = Assert.Single(methods.Children, node => node.Label == "member_0000");
        var latest = Assert.Single(methods.Children, node => node.Label == "member_0255");
        var initialResultLabel = viewModel.ClassTreeSearchResultLabel;

        viewModel.ClassTreeSearchText = "member_0000";
        viewModel.ClassTreeSearchText = "member_0255";

        Assert.Equal(initialResultLabel, viewModel.ClassTreeSearchResultLabel);
        Assert.False(first.IsSearchMatch);
        Assert.False(latest.IsSearchMatch);
        Assert.True(first.IsSearchVisible);
        Assert.True(latest.IsSearchVisible);

        await EventuallyAsync(() => latest.IsSearchMatch);

        Assert.Equal("member_0255", viewModel.ClassTreeSearchText);
        Assert.False(first.IsSearchVisible);
        Assert.False(first.IsSearchMatch);
        Assert.True(latest.IsSearchVisible);
        Assert.True(latest.IsSearchMatch);
        Assert.StartsWith($"1 of {semanticNodes.Length:N0} loaded class details match", viewModel.ClassTreeSearchResultLabel);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(first.IsSearchMatch);
        Assert.True(latest.IsSearchMatch);
    }

    [Fact]
    public async Task ObjectPathNavigationLabelsAndCopiedValuesPreserveUnderscores()
    {
        var session = new UxSession
        {
            ObjectChildren = new JsonObject
            {
                ["items"] = new JsonArray(
                    Child("child_value", "child-handle", "child-id", "0x201", depth: 1, canExpand: false)),
                ["offset"] = 0,
                ["total"] = 1,
            },
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("dict_test", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        var clipboard = new RecordingClipboard();
        await using var viewModel = await ConnectedViewModelAsync(session, clipboard);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        var root = Assert.Single(viewModel.ObjectRoots);
        var child = root.Children
            .Where(node => node.Kind == ObjectNodeKind.Group)
            .SelectMany(node => node.Children)
            .Single(node => node.Value?.Name == "child_value");

        await viewModel.SelectObjectNodeAsync(child);

        Assert.EndsWith("dict_test.child_value", viewModel.SelectedObjectPath, StringComparison.Ordinal);
        Assert.Collection(viewModel.ObjectBreadcrumbs,
            item => Assert.Equal("dict_test", item.Label),
            item => Assert.Equal("child_value", item.Label));
        Assert.Contains("dict_test", viewModel.BackNavigationLabel);
        Assert.Contains("dict_test", viewModel.ParentNavigationLabel);

        viewModel.CopyDisplayedValue("dict_test.child_value");
        Assert.Equal("dict_test.child_value", clipboard.Text);
        Assert.Equal("Value copied", viewModel.Status);
    }

    [Fact]
    public async Task ObjectNavigationHistoryCapsAt128KeepsBackForwardAndReleasesEvictedHandles()
    {
        const int historyLimit = 128;
        var values = Enumerable.Range(0, historyLimit + 2)
            .Select(index => Value(
                $"object_{index}",
                $"object-{index}-handle",
                $"object-{index}-identity",
                $"object-{index}-metadata",
                address: $"0x{index + 0x100:X}"))
            .ToArray();
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(values));
        session.EnqueueScope(ScopeResult((JsonObject)values[^1].DeepClone()));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        foreach (var row in viewModel.Variables.ToArray())
        {
            viewModel.SelectedVariable = row;
            await EventuallyAsync(() => viewModel.SelectedObjectName == row.Name
                && viewModel.InspectorState != InspectorPaneState.Loading);
        }

        Assert.Equal($"History {historyLimit} / {historyLimit}", viewModel.NavigationHistoryLabel);
        Assert.Contains("object_128", viewModel.BackNavigationLabel);
        await viewModel.NavigateBackCommand.ExecuteAsync();
        Assert.Equal("object_128", viewModel.SelectedObjectName);
        Assert.Equal($"History {historyLimit - 1} / {historyLimit}", viewModel.NavigationHistoryLabel);
        Assert.Contains("object_129", viewModel.ForwardNavigationLabel);

        await viewModel.NavigateForwardCommand.ExecuteAsync();
        Assert.Equal("object_129", viewModel.SelectedObjectName);
        Assert.Equal($"History {historyLimit} / {historyLimit}", viewModel.NavigationHistoryLabel);

        await viewModel.LoadScopeAsync(scope);
        await EventuallyAsync(() =>
            ReleasedHandles(session).Contains("object-0-handle")
            && ReleasedHandles(session).Contains("object-1-handle"));
        Assert.DoesNotContain("object-2-handle", ReleasedHandles(session));
        Assert.DoesNotContain("object-129-handle", ReleasedHandles(session));
    }

    [Fact]
    public async Task VariableRowsAndSelectionStayStableAcrossUnchangedAndMetadataRefreshes()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value("counter", "counter-handle", "counter-id", "counter-meta", preview: "1")));
        session.EnqueueScope(ScopeResult(Value("counter", "counter-handle", "counter-id", "counter-meta", preview: "1")));
        session.EnqueueScope(ScopeResult(Value("counter", "counter-handle", "counter-id", "counter-meta-2", preview: "2")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        var row = Assert.Single(viewModel.Variables);
        viewModel.SelectedVariable = row;
        await EventuallyAsync(() => viewModel.InspectorState != InspectorPaneState.Loading);
        var variableCollectionChanges = 0;
        var filteredCollectionChanges = 0;
        viewModel.Variables.CollectionChanged += (_, _) => variableCollectionChanges++;
        viewModel.FilteredVariables.CollectionChanged += (_, _) => filteredCollectionChanges++;

        await viewModel.LoadScopeAsync(scope);
        Assert.Same(row, Assert.Single(viewModel.Variables));
        Assert.Same(row, Assert.Single(viewModel.FilteredVariables));
        Assert.Same(row, viewModel.SelectedVariable);

        await viewModel.LoadScopeAsync(scope);
        Assert.Same(row, Assert.Single(viewModel.Variables));
        Assert.Same(row, Assert.Single(viewModel.FilteredVariables));
        Assert.Same(row, viewModel.SelectedVariable);
        Assert.Equal("2", row.SafePreview);
        Assert.Equal(VariableChangeKind.MetadataChanged, row.ChangeKind);
        Assert.Equal("Δ Updated", row.ChangeDisplay);
        Assert.Equal(0, variableCollectionChanges);
        Assert.Equal(0, filteredCollectionChanges);
    }

    [Fact]
    public async Task AutomaticRefreshDoesNotShowTableLoadingAndRefreshesSelectedArrayBitmap()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value(
            "frame", "array-handle", "array-id", "array-meta", adapterKind: "numpy.ndarray", preview: "frame 1")));
        session.EnqueueScope(ScopeResult(Value(
            "frame", "array-handle", "array-id", "array-meta-2", adapterKind: "numpy.ndarray", preview: "frame 2")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        var row = Assert.Single(viewModel.Variables);
        viewModel.SelectedVariable = row;
        await EventuallyAsync(() => viewModel.ArrayPreview is not null && viewModel.InspectorState == InspectorPaneState.Ready);
        var firstBitmap = viewModel.ArrayPreview;
        var loadingWasShown = false;
        var variableCollectionChanges = 0;
        var filteredCollectionChanges = 0;
        viewModel.Variables.CollectionChanged += (_, _) => variableCollectionChanges++;
        viewModel.FilteredVariables.CollectionChanged += (_, _) => filteredCollectionChanges++;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsScopeLoading) && viewModel.IsScopeLoading)
                loadingWasShown = true;
        };

        viewModel.RefreshIntervalSeconds = 1;
        viewModel.AutoRefreshEnabled = true;
        await EventuallyAsync(() => session.Requests.Count(request => request.Method == "scopes.list") >= 2
            && session.Requests.Count(request => request.Method == "arrays.preview") >= 2);
        viewModel.AutoRefreshEnabled = false;

        Assert.False(loadingWasShown);
        Assert.False(viewModel.IsScopeLoading);
        Assert.Same(row, Assert.Single(viewModel.Variables));
        Assert.Same(row, viewModel.SelectedVariable);
        Assert.Equal("frame 2", row.SafePreview);
        Assert.Equal(VariableChangeKind.MetadataChanged, row.ChangeKind);
        Assert.Equal("Δ Updated", row.ChangeDisplay);
        Assert.Equal(0, variableCollectionChanges);
        Assert.Equal(0, filteredCollectionChanges);
        Assert.NotSame(firstBitmap, viewModel.ArrayPreview);
        Assert.Contains("Live image refreshed", viewModel.SelectedObjectStatus);
    }

    [Fact]
    public async Task DataFrameSelectionLoadsBoundedTableAndPagesRowsAndColumns()
    {
        var session = new UxSession
        {
            DataFrameDescriptionResult = DataFrameDescription(totalRows: 120, totalColumns: 30),
        };
        session.EnqueueScope(ScopeResult(Value(
            "orders",
            "dataframe-handle",
            "dataframe-id",
            "dataframe-meta",
            typeName: "DataFrame",
            moduleName: "pandas.core.frame",
            adapterKind: "pandas.DataFrame",
            preview: "DataFrame(rows=120, columns=30)")));
        session.EnqueueDataFramePreview(DataFramePreview(0, 50, 120, 0, 20, 30));
        session.EnqueueDataFramePreview(DataFramePreview(50, 50, 120, 0, 20, 30));
        session.EnqueueDataFramePreview(DataFramePreview(50, 50, 120, 20, 10, 30));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.DataFrameState == DataFramePaneState.Ready);

        Assert.True(viewModel.HasDataFrameSelection);
        Assert.False(viewModel.HasArraySelection);
        Assert.Equal("120 rows × 30 columns", viewModel.DataFrameShape);
        Assert.Equal(20, viewModel.DataFrameColumns.Count);
        Assert.Equal("column_0", viewModel.DataFrameColumns[0].Name);
        Assert.Equal("int64", viewModel.DataFrameColumns[0].DType);
        var firstPage = Assert.IsType<System.Data.DataView>(viewModel.DataFrameRows);
        Assert.Equal(50, firstPage.Count);
        Assert.Equal("index_0", firstPage[0]["__index__"]);
        Assert.Equal("value_0_0", firstPage[0]["Column_0"]);
        Assert.Equal("Rows 1–50 of 120", viewModel.DataFrameRowPageLabel);
        Assert.Equal("Columns 1–20 of 30", viewModel.DataFrameColumnPageLabel);
        Assert.True(viewModel.NextDataFrameRowsCommand.CanExecute(null));
        Assert.False(viewModel.PreviousDataFrameRowsCommand.CanExecute(null));
        Assert.True(viewModel.NextDataFrameColumnsCommand.CanExecute(null));

        await viewModel.NextDataFrameRowsCommand.ExecuteAsync();
        Assert.Equal("index_50", viewModel.DataFrameRows![0]["__index__"]);
        Assert.Equal("Rows 51–100 of 120", viewModel.DataFrameRowPageLabel);
        Assert.True(viewModel.PreviousDataFrameRowsCommand.CanExecute(null));
        var rowPageRequest = session.Requests.Last(request => request.Method == "dataframes.preview");
        Assert.Equal((50, 0, 50, 20),
            (rowPageRequest.RowOffset, rowPageRequest.ColumnOffset, rowPageRequest.RowCount, rowPageRequest.ColumnCount));

        await viewModel.NextDataFrameColumnsCommand.ExecuteAsync();
        Assert.Equal(10, viewModel.DataFrameColumns.Count);
        Assert.Equal("column_20", viewModel.DataFrameColumns[0].Name);
        Assert.Equal("value_50_20", viewModel.DataFrameRows![0]["Column_20"]);
        Assert.Equal("Columns 21–30 of 30", viewModel.DataFrameColumnPageLabel);
        Assert.False(viewModel.NextDataFrameColumnsCommand.CanExecute(null));
        Assert.True(viewModel.PreviousDataFrameColumnsCommand.CanExecute(null));
        var columnPageRequest = session.Requests.Last(request => request.Method == "dataframes.preview");
        Assert.Equal((50, 20, 50, 20),
            (columnPageRequest.RowOffset, columnPageRequest.ColumnOffset, columnPageRequest.RowCount, columnPageRequest.ColumnCount));

        session.RaiseDisconnected("Target exited");
        await EventuallyAsync(() => !viewModel.IsConnected);
        Assert.False(viewModel.HasDataFrameSelection);
        Assert.Null(viewModel.DataFrameRows);
        Assert.Empty(viewModel.DataFrameColumns);
        Assert.Equal(DataFramePaneState.NoSelection, viewModel.DataFrameState);
        Assert.False(viewModel.RefreshDataFrameCommand.CanExecute(null));
    }

    [Fact]
    public async Task DataFrameEmptyAndErrorStatesRemainInsideTheSelectionInspector()
    {
        var emptySession = new UxSession
        {
            DataFrameDescriptionResult = DataFrameDescription(totalRows: 0, totalColumns: 3),
            DataFramePreviewResult = DataFramePreview(0, 0, 0, 0, 3, 3),
        };
        emptySession.EnqueueScope(ScopeResult(Value(
            "empty_frame", "empty-frame-handle", "empty-frame-id", "empty-frame-meta",
            typeName: "DataFrame", moduleName: "pandas.core.frame", adapterKind: "pandas.DataFrame")));
        await using (var emptyViewModel = await ConnectedViewModelAsync(emptySession))
        {
            await emptyViewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
            emptyViewModel.SelectedVariable = Assert.Single(emptyViewModel.Variables);
            await EventuallyAsync(() => emptyViewModel.DataFrameState == DataFramePaneState.Empty);

            Assert.True(emptyViewModel.HasDataFrameSelection);
            Assert.Equal(InspectorPaneState.Ready, emptyViewModel.InspectorState);
            Assert.Equal("0 rows × 3 columns", emptyViewModel.DataFrameShape);
            Assert.Equal("Rows 0 of 0", emptyViewModel.DataFrameRowPageLabel);
            Assert.NotNull(emptyViewModel.DataFrameRows);
            Assert.Empty(emptyViewModel.DataFrameRows!);
        }

        var errorSession = new UxSession
        {
            DataFrameDescriptionResult = DataFrameDescription(totalRows: 10, totalColumns: 2),
            DataFramePreviewResponder = _ => Task.FromException<JsonObject>(
                new RemoteInspectionException("INVALID_ARGUMENT", "The bounded DataFrame preview is unavailable.")),
        };
        errorSession.EnqueueScope(ScopeResult(Value(
            "broken_frame", "broken-frame-handle", "broken-frame-id", "broken-frame-meta",
            typeName: "DataFrame", moduleName: "pandas.core.frame", adapterKind: "pandas.DataFrame")));
        await using var errorViewModel = await ConnectedViewModelAsync(errorSession);
        await errorViewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        errorViewModel.SelectedVariable = Assert.Single(errorViewModel.Variables);
        await EventuallyAsync(() => errorViewModel.DataFrameState == DataFramePaneState.Error);

        Assert.True(errorViewModel.HasDataFrameSelection);
        Assert.Equal(InspectorPaneState.Ready, errorViewModel.InspectorState);
        Assert.Contains("INVALID_ARGUMENT", errorViewModel.DataFrameErrorMessage);
        Assert.Contains("refresh", errorViewModel.DataFrameStatus, StringComparison.OrdinalIgnoreCase);
        Assert.True(errorViewModel.RefreshDataFrameCommand.CanExecute(null));
    }

    [Fact]
    public async Task AutomaticRefreshUpdatesCurrentDataFramePageInPlaceWithoutLoadingOverlay()
    {
        var session = new UxSession
        {
            DataFrameDescriptionResult = DataFrameDescription(totalRows: 120, totalColumns: 30),
        };
        session.EnqueueScope(ScopeResult(Value(
            "orders", "dataframe-handle", "dataframe-id", "meta-1",
            typeName: "DataFrame", moduleName: "pandas.core.frame", adapterKind: "pandas.DataFrame")));
        session.EnqueueScope(ScopeResult(Value(
            "orders", "dataframe-handle", "dataframe-id", "meta-2",
            typeName: "DataFrame", moduleName: "pandas.core.frame", adapterKind: "pandas.DataFrame")));
        session.EnqueueDataFramePreview(DataFramePreview(0, 50, 120, 0, 20, 30, valuePrefix: "initial"));
        session.EnqueueDataFramePreview(DataFramePreview(50, 50, 120, 0, 20, 30, valuePrefix: "page"));
        session.EnqueueDataFramePreview(DataFramePreview(50, 50, 120, 0, 20, 30, valuePrefix: "updated"));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        var selected = Assert.Single(viewModel.Variables);
        viewModel.SelectedVariable = selected;
        await EventuallyAsync(() => viewModel.DataFrameState == DataFramePaneState.Ready);
        await viewModel.NextDataFrameRowsCommand.ExecuteAsync();
        var dataView = viewModel.DataFrameRows;
        Assert.Equal("page_50_0", dataView![0]["Column_0"]);
        var loadingWasShown = false;
        var variableCollectionChanges = 0;
        var filteredCollectionChanges = 0;
        viewModel.Variables.CollectionChanged += (_, _) => variableCollectionChanges++;
        viewModel.FilteredVariables.CollectionChanged += (_, _) => filteredCollectionChanges++;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.DataFrameState)
                && viewModel.DataFrameState == DataFramePaneState.Loading)
            {
                loadingWasShown = true;
            }
        };

        viewModel.RefreshIntervalSeconds = 1;
        viewModel.AutoRefreshEnabled = true;
        await EventuallyAsync(() => session.Requests.Count(request => request.Method == "scopes.list") >= 2
            && session.Requests.Count(request => request.Method == "dataframes.preview") >= 3);
        viewModel.AutoRefreshEnabled = false;

        Assert.False(loadingWasShown);
        Assert.Same(selected, viewModel.SelectedVariable);
        Assert.Same(dataView, viewModel.DataFrameRows);
        Assert.Equal(VariableChangeKind.MetadataChanged, selected.ChangeKind);
        Assert.Equal("Δ Updated", selected.ChangeDisplay);
        Assert.Equal(0, variableCollectionChanges);
        Assert.Equal(0, filteredCollectionChanges);
        Assert.Equal("updated_50_0", viewModel.DataFrameRows![0]["Column_0"]);
        Assert.Equal("Rows 51–100 of 120", viewModel.DataFrameRowPageLabel);
        var automaticRequest = session.Requests.Last(request => request.Method == "dataframes.preview");
        Assert.Equal(50, automaticRequest.RowOffset);
        Assert.Equal(0, automaticRequest.ColumnOffset);
        Assert.Contains("Live DataFrame refreshed", viewModel.SelectedObjectStatus);
    }

    [Fact]
    public async Task WpfGridKeepsSelectedContainerAndScrollPositionAcrossStableRefreshes()
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            Window? window = null;
            MainViewModel? viewModel = null;
            try
            {
                var session = new UxSession();
                session.EnqueueScope(ScopeResult(VariablePage("value")));
                session.EnqueueScope(ScopeResult(VariablePage("value")));
                session.EnqueueScope(ScopeResult(VariablePage("updated")));
                viewModel = ConnectedViewModelAsync(session).GetAwaiter().GetResult();
                var scope = ScopeNode();
                viewModel.LoadScopeAsync(scope, resetPage: true).GetAwaiter().GetResult();
                var selected = viewModel.Variables[120];
                viewModel.SelectedVariable = selected;

                var grid = new DataGrid
                {
                    DataContext = viewModel,
                    ItemsSource = viewModel.FilteredVariables,
                    IsReadOnly = true,
                    EnableRowVirtualization = true,
                };
                grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedVariable))
                {
                    Mode = BindingMode.TwoWay,
                });
                window = new Window { Width = 640, Height = 320, Content = grid, ShowInTaskbar = false };
                window.Show();
                grid.ScrollIntoView(selected);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var scrollViewer = FindVisualDescendant<ScrollViewer>(grid)
                    ?? throw new InvalidOperationException("The variables grid scroll viewer was not created.");
                var initialOffset = scrollViewer.VerticalOffset;
                var selectedContainer = grid.ItemContainerGenerator.ContainerFromItem(selected);
                Assert.NotNull(selectedContainer);

                viewModel.LoadScopeAsync(scope).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Same(selected, grid.SelectedItem);
                Assert.Same(selectedContainer, grid.ItemContainerGenerator.ContainerFromItem(selected));
                Assert.Equal(initialOffset, scrollViewer.VerticalOffset, precision: 3);

                viewModel.LoadScopeAsync(scope).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal("updated 120", selected.SafePreview);
                Assert.Same(selected, grid.SelectedItem);
                Assert.Same(selectedContainer, grid.ItemContainerGenerator.ContainerFromItem(selected));
                Assert.Equal(initialOffset, scrollViewer.VerticalOffset, precision: 3);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    window?.Close();
                    if (viewModel is not null)
                        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(exception);
        thread.Join(TimeSpan.FromSeconds(2));

        static JsonObject[] VariablePage(string previewPrefix) => Enumerable.Range(0, 150)
            .Select(index => Value(
                $"variable_{index}",
                $"handle-{index}",
                $"identity-{index}",
                index == 120 && previewPrefix == "updated" ? "metadata-updated" : $"metadata-{index}",
                preview: $"{previewPrefix} {index}"))
            .ToArray();
    }

    [Fact]
    public async Task SelectingVariableLoadsChildrenAndStructuredClassForSameHandle()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("selected", "root-handle", "root-identity", "root-metadata", address: "0x100", expandable: true)));
        var clipboard = new RecordingClipboard();
        await using var viewModel = await ConnectedViewModelAsync(session, clipboard);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        Assert.False(viewModel.IsSnapshotStale);
        Assert.Contains("fresh", viewModel.SnapshotStatus);
        Assert.Contains("Snapshot", viewModel.SnapshotStatusBar);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        Assert.Contains(session.Requests, request => request.Method == "objects.listChildren" && request.HandleId == "root-handle");
        Assert.Contains(session.Requests, request => request.Method == "classes.describe" && request.HandleId == "root-handle");
        Assert.Equal(new[] { "child", "deep", "loop" }, viewModel.ObjectChildren.Select(child => child.Name).Order().ToArray());

        var root = Assert.Single(viewModel.ObjectRoots);
        var objectNodes = root.Children
            .Where(node => node.Kind == ObjectNodeKind.Group)
            .SelectMany(group => group.Children)
            .ToArray();
        var cycle = Assert.Single(objectNodes, node => node.Value?.Name == "loop");
        Assert.True(cycle.IsCycle);
        Assert.True(cycle.IsLoaded);
        Assert.Contains("cycle", cycle.Label, StringComparison.OrdinalIgnoreCase);
        var depthLimited = Assert.Single(objectNodes, node => node.Value?.Name == "deep");
        Assert.Equal(8, depthLimited.Depth);
        Assert.True(depthLimited.IsLoaded);
        Assert.Contains("depth limit", depthLimited.Label, StringComparison.OrdinalIgnoreCase);

        var childrenRequestCount = session.Requests.Count(request => request.Method == "objects.listChildren");
        await viewModel.ExpandObjectNodeAsync(cycle);
        await viewModel.ExpandObjectNodeAsync(depthLimited);
        Assert.Equal(childrenRequestCount, session.Requests.Count(request => request.Method == "objects.listChildren"));

        var method = Assert.Single(viewModel.ClassMembers, member => member.Name == "render");
        Assert.Equal("(self, width: int = 80)", method.Signature);
        Assert.Equal("sample.py:41", method.Source);
        Assert.False(method.Inherited);
        var inherited = Assert.Single(viewModel.ClassMembers, member => member.Name == "name");
        Assert.True(inherited.Inherited);

        var methods = Assert.Single(viewModel.ClassTree, group => group.Label == "Instance methods");
        var render = Assert.Single(methods.Children, member => member.Label == "render");
        var parameters = Assert.Single(render.Children, group => group.Label == "Parameters");
        Assert.Collection(parameters.Children,
            parameter => Assert.Equal("self", parameter.Label),
            parameter =>
            {
                Assert.Equal("width", parameter.Label);
                Assert.Contains("int", parameter.Detail);
                Assert.Contains("80", parameter.Detail);
            });
        var inheritedGroup = Assert.Single(viewModel.ClassTree, group => group.Label == "Inherited members");
        Assert.Contains(inheritedGroup.Children, member => member.Label == "name" && member.DeclaredBy == "base.BaseExample");

        viewModel.CopyObjectPathCommand.Execute(null);
        Assert.Equal(viewModel.SelectedObjectPath, clipboard.Text);
        viewModel.CopyObjectTypeCommand.Execute(null);
        Assert.Equal("sample.Example", clipboard.Text);
        viewModel.CopyObjectAddressCommand.Execute(null);
        Assert.Equal("0x100", clipboard.Text);
    }

    [Fact]
    public async Task ConsecutiveSnapshotsClassifyAddedRemovedReboundAndMetadataChanges()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(
            Value("stable", "stable-handle", "stable-id", "stable-meta"),
            Value("rebound", "rebound-old", "rebound-id-1", "rebound-meta"),
            Value("metadata", "metadata-handle", "metadata-id", "metadata-1"),
            Value("removed", "removed-handle", "removed-id", "removed-meta")));
        session.EnqueueScope(ScopeResult(
            Value("stable", "stable-handle", "stable-id", "stable-meta"),
            Value("rebound", "rebound-new", "rebound-id-2", "rebound-meta"),
            Value("metadata", "metadata-handle", "metadata-id", "metadata-2", preview: "updated"),
            Value("added", "added-handle", "added-id", "added-meta")));
        session.EnqueueScope(ScopeResult(
            Value("stable", "stable-handle", "stable-id", "stable-meta"),
            Value("rebound", "rebound-new", "rebound-id-2", "rebound-meta"),
            Value("metadata", "metadata-handle", "metadata-id", "metadata-2", preview: "updated"),
            Value("added", "added-handle", "added-id", "added-meta")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        Assert.All(viewModel.Variables, row => Assert.Equal(VariableChangeKind.Unchanged, row.ChangeKind));

        await viewModel.LoadScopeAsync(scope);

        Assert.Equal(VariableChangeKind.Unchanged, Row("stable").ChangeKind);
        Assert.Equal(VariableChangeKind.Rebound, Row("rebound").ChangeKind);
        Assert.Equal(VariableChangeKind.MetadataChanged, Row("metadata").ChangeKind);
        Assert.Equal(VariableChangeKind.Added, Row("added").ChangeKind);
        Assert.Equal("Unchanged", Row("stable").ChangeDisplay);
        Assert.Equal("↻ Rebound", Row("rebound").ChangeDisplay);
        Assert.Equal("Δ Updated", Row("metadata").ChangeDisplay);
        Assert.Equal("+ Added", Row("added").ChangeDisplay);
        var removed = Row("removed");
        Assert.Equal(VariableChangeKind.Removed, removed.ChangeKind);
        Assert.Equal("− Removed", removed.ChangeDisplay);
        Assert.EndsWith(" · since refresh", removed.ChangeTimeDisplay);
        Assert.Contains("since previous refresh", removed.ChangeDetail);
        Assert.True(removed.IsRemoved);
        Assert.False(removed.Expandable);
        Assert.All(viewModel.Variables.Where(row => row.Changed), row => Assert.NotNull(row.ChangedAt));
        var removedAt = removed.ChangedAt;

        await viewModel.LoadScopeAsync(scope);

        var persistedRemoved = Row("removed");
        Assert.Equal(VariableChangeKind.Removed, persistedRemoved.ChangeKind);
        Assert.True(persistedRemoved.IsRemoved);
        Assert.Equal(removedAt, persistedRemoved.ChangedAt);

        VariableRow Row(string name) => Assert.Single(viewModel.Variables, row => row.Name == name);
    }

    [Fact]
    public async Task SelectionWithoutSafeChildrenClassMembersOrArrayUsesEmptyState()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(
            Value("empty", "empty-handle", "empty-id", "empty-meta", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Empty);

        Assert.True(viewModel.HasSelectedObject);
        Assert.False(viewModel.HasArraySelection);
        Assert.Empty(viewModel.ObjectChildren);
        Assert.Empty(viewModel.ClassMembers);
        Assert.Contains("No safe child values", viewModel.SelectedObjectStatus);
    }

    [Fact]
    public async Task ExpiredObjectShowsRecoverableExpiredState()
    {
        var session = new UxSession
        {
            ObjectChildrenResponder = (_, _) => Task.FromException<JsonObject>(
                new RemoteInspectionException("OBJECT_EXPIRED", "The selected object is no longer available.")),
        };
        session.EnqueueScope(ScopeResult(
            Value("expired", "expired-handle", "expired-id", "expired-meta", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Expired);

        Assert.Contains("refresh", viewModel.SelectedObjectStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewModel.ObjectRoots);
        Assert.True(viewModel.HasSelectedObject);
    }

    [Fact]
    public async Task LateDetailResponseCannotOverwriteNewerObjectSelection()
    {
        var firstResponse = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new UxSession
        {
            ClassDescription = StructuredClassDescription(),
            ObjectChildrenResponder = (handle, _) => handle == "first-handle"
                ? firstResponse.Task
                : Task.FromResult(SingleChildResult("second-child", "second-child-handle")),
        };
        session.EnqueueScope(ScopeResult(
            Value("first", "first-handle", "first-id", "first-meta", expandable: true),
            Value("second", "second-handle", "second-id", "second-meta", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "first");
        await EventuallyAsync(() => session.Requests.Any(request => request.Method == "objects.listChildren" && request.HandleId == "first-handle"));
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "second");
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready && viewModel.SelectedObjectName == "second");

        firstResponse.SetResult(SingleChildResult("first-child", "first-child-handle"));
        await Task.Delay(50);

        Assert.Equal("second", viewModel.SelectedObjectName);
        Assert.DoesNotContain(viewModel.ObjectChildren, child => child.Name == "first-child");
        Assert.Contains(viewModel.ObjectChildren, child => child.Name == "second-child");
        await EventuallyAsync(() => ReleasedHandles(session).Contains("first-child-handle"));
        Assert.DoesNotContain("first-handle", ReleasedHandles(session));
        Assert.DoesNotContain("second-child-handle", ReleasedHandles(session));
    }

    [Fact]
    public async Task RepeatedLargeListRebindingReleasesOnlyObsoleteUnpinnedHandlesAndDrainsOnDetach()
    {
        const int lastVersion = 20;
        var session = new UxSession();
        for (var version = 0; version <= lastVersion; version++)
        {
            session.EnqueueScope(ScopeResult(Value(
                "payload",
                $"payload-{version}-handle",
                $"payload-{version}-identity",
                $"payload-{version}-metadata",
                typeName: "list",
                moduleName: "builtins",
                preview: $"<list len=2,000,000 version={version}>",
                payloadSize: 128L * 1024 * 1024)));
        }
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.HasSelectedObject);
        viewModel.TogglePinCommand.Execute(null);
        Assert.True(viewModel.IsSelectedObjectPinned);

        for (var version = 1; version <= lastVersion; version++)
            await viewModel.LoadScopeAsync(scope);

        await EventuallyAsync(() => Enumerable.Range(1, lastVersion - 1)
            .All(version => ReleasedHandles(session).Contains($"payload-{version}-handle")));
        var releasedWhilePinned = ReleasedHandles(session);
        Assert.DoesNotContain("payload-0-handle", releasedWhilePinned);
        Assert.DoesNotContain($"payload-{lastVersion}-handle", releasedWhilePinned);

        viewModel.TogglePinCommand.Execute(null);
        await EventuallyAsync(() => ReleasedHandles(session).Contains("payload-0-handle"));
        Assert.DoesNotContain($"payload-{lastVersion}-handle", ReleasedHandles(session));

        await viewModel.DetachCommand.ExecuteAsync();
        await EventuallyAsync(() => ReleasedHandles(session).Contains($"payload-{lastVersion}-handle"));
    }

    [Fact]
    public async Task ScopeLoadExposesLoadingStateUntilVariablesArrive()
    {
        var session = new UxSession { ScopeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        session.EnqueueScope(ScopeResult(Value("ready", "ready-handle", "ready-id", "ready-meta")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        var load = viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        await EventuallyAsync(() => viewModel.IsScopeLoading);

        Assert.False(viewModel.ShowVariableListEmpty);
        session.ScopeRelease.SetResult();
        await load;
        Assert.False(viewModel.IsScopeLoading);
        Assert.False(viewModel.ShowVariableListEmpty);
        Assert.Single(viewModel.FilteredVariables);
    }

    [Fact]
    public async Task OldScopeTimestampIsReportedAsStale()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResultAt(
            DateTimeOffset.Now.AddMinutes(-1),
            Value("stale", "stale-handle", "stale-id", "stale-meta")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);

        Assert.True(viewModel.IsSnapshotStale);
        Assert.Contains("stale", viewModel.SnapshotStatus);
        Assert.Contains("stale", viewModel.SnapshotStatusBar);
    }

    [Fact]
    public async Task ResetComparisonUsesCurrentValuesAsUnchangedBaseline()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value("seed", "seed-handle", "seed-id", "seed-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("added", "added-handle", "added-id", "added-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("added", "added-handle", "added-id", "added-meta")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        await viewModel.LoadScopeAsync(scope);
        Assert.Equal(VariableChangeKind.Added, Assert.Single(viewModel.Variables, row => row.Name == "added").ChangeKind);

        viewModel.ResetBaselineCommand.Execute(null);
        await EventuallyAsync(() => session.Requests.Count(request => request.Method == "scopes.list") == 3);
        await EventuallyAsync(() => viewModel.Variables.All(row => row.ChangeKind == VariableChangeKind.Unchanged));

        Assert.Contains("baseline reset", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeHighlightRemainsVisibleForTenSecondsAndExpiresAfterDefaultWindow()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value("seed", "seed-handle", "seed-id", "seed-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("added", "added-handle", "added-id", "added-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("added", "added-handle", "added-id", "added-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("added", "added-handle", "added-id", "added-meta")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        await viewModel.LoadScopeAsync(scope);
        Assert.Equal(VariableChangeKind.Added, Assert.Single(viewModel.Variables, row => row.Name == "added").ChangeKind);

        await Task.Delay(TimeSpan.FromSeconds(10.1));
        await viewModel.LoadScopeAsync(scope);

        Assert.Equal(VariableChangeKind.Added, Assert.Single(viewModel.Variables, row => row.Name == "added").ChangeKind);

        await Task.Delay(TimeSpan.FromSeconds(2.1));
        await viewModel.LoadScopeAsync(scope);

        Assert.All(viewModel.Variables, row => Assert.Equal(VariableChangeKind.Unchanged, row.ChangeKind));
    }

    [Fact]
    public async Task MetadataRefreshPreservesExpandedInspectionContextUntilExplicitRefresh()
    {
        var session = new UxSession
        {
            ObjectChildren = ObjectChildrenResult(),
            ClassDescription = StructuredClassDescription(),
        };
        session.EnqueueScope(ScopeResult(
            Value("pipeline", "root-handle", "root-identity", "metadata-1", typeName: "Pipeline", expandable: true)));
        session.EnqueueScope(ScopeResult(
            Value("pipeline", "root-handle", "root-identity", "metadata-2", typeName: "Pipeline", preview: "updated", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();

        await viewModel.LoadScopeAsync(scope, resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        var classRequestCount = session.Requests.Count(request => request.Method == "classes.describe");
        var classOverview = Assert.Single(viewModel.ClassTree, group => group.Label == "Class overview");

        await viewModel.LoadScopeAsync(scope);

        Assert.Equal("pipeline", viewModel.SelectedVariable?.Name);
        Assert.Same(classOverview, Assert.Single(viewModel.ClassTree, group => group.Label == "Class overview"));
        Assert.Equal(classRequestCount, session.Requests.Count(request => request.Method == "classes.describe"));
        Assert.Contains("F5", viewModel.SelectedObjectStatus);
    }

    [Fact]
    public async Task SearchAndAllFiltersComposeWithPinnedAddedArrayThenDisconnectClearsState()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value("seed", "seed-handle", "seed-id", "seed-meta")));
        session.EnqueueScope(ScopeResult(
            Value("seed", "seed-handle", "seed-id", "seed-meta"),
            Value("target_array", "target-handle", "target-id", "target-meta", typeName: "ndarray", moduleName: "numpy", adapterKind: "numpy.ndarray", preview: "target pixels", expandable: true),
            Value("other_array", "other-handle", "other-id", "other-meta", typeName: "ndarray", moduleName: "numpy", adapterKind: "numpy.ndarray", preview: "target pixels", expandable: true),
            Value("target_scalar", "scalar-handle", "scalar-id", "scalar-meta", preview: "target pixels", expandable: true)));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var scope = ScopeNode();
        await viewModel.LoadScopeAsync(scope, resetPage: true);
        await viewModel.LoadScopeAsync(scope);

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "target_array");
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        viewModel.TogglePinCommand.Execute(null);
        Assert.True(viewModel.IsSelectedObjectPinned);

        viewModel.SearchText = "target";
        viewModel.ScopeFilter = "locals";
        viewModel.ChangeFilter = "Added";
        viewModel.TypeFilter = "ndarray";
        viewModel.ArraysOnly = true;
        viewModel.ExpandableOnly = true;
        viewModel.PinnedOnly = true;

        var visible = Assert.Single(viewModel.FilteredVariables);
        Assert.Equal("target_array", visible.Name);
        Assert.True(visible.IsPinned);
        Assert.Contains("1 of 4 visible", viewModel.FilterResultLabel);

        viewModel.SearchText = "no-such-variable";
        Assert.True(viewModel.IsSearchPending);
        await EventuallyAsync(() => !viewModel.IsSearchPending);
        Assert.Empty(viewModel.FilteredVariables);
        Assert.True(viewModel.ShowVariableListEmpty);
        Assert.Contains("No variables match", viewModel.VariableListStatusMessage);
        viewModel.SearchText = "target";
        await EventuallyAsync(() => !viewModel.IsSearchPending);
        Assert.False(viewModel.ShowVariableListEmpty);

        session.RaiseDisconnected("Target exited");
        await EventuallyAsync(() => !viewModel.IsConnected);

        Assert.False(viewModel.IsConnected);
        Assert.Empty(viewModel.Variables);
        Assert.Empty(viewModel.FilteredVariables);
        Assert.Empty(viewModel.ObjectRoots);
        Assert.Empty(viewModel.ClassTree);
        Assert.Empty(viewModel.PinnedObjects);
        Assert.False(viewModel.HasSelectedObject);
        Assert.Equal(InspectorPaneState.NoSelection, viewModel.InspectorState);
        Assert.Null(viewModel.LastSnapshotAt);
        Assert.Equal("Target exited", viewModel.Status);
    }

    [Fact]
    public async Task SameNameInDifferentFramesHasDistinctSelectionAndPinIdentity()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(
            Value("shared", "first-handle", "first-id", "first-meta", preview: "first")));
        session.EnqueueScope(ScopeResult(
            Value("shared", "second-handle", "second-id", "second-meta", preview: "second")));
        await using var viewModel = await ConnectedViewModelAsync(session);
        var firstScope = ScopeNode("frame-1", "worker A / Locals");
        var secondScope = ScopeNode("frame-2", "worker B / Locals");

        await viewModel.LoadScopeAsync(firstScope, resetPage: true);
        var first = Assert.Single(viewModel.Variables);
        Assert.Equal("frame-1:locals:shared", first.StableKey);
        viewModel.SelectedVariable = first;
        await EventuallyAsync(() => viewModel.InspectorState != InspectorPaneState.Loading);
        viewModel.TogglePinCommand.Execute(null);
        Assert.True(first.IsPinned);

        await viewModel.LoadScopeAsync(secondScope, resetPage: true);

        var second = Assert.Single(viewModel.Variables);
        Assert.Equal("frame-2:locals:shared", second.StableKey);
        Assert.NotEqual(first.StableKey, second.StableKey);
        Assert.Null(viewModel.SelectedVariable);
        Assert.False(viewModel.HasSelectedObject);
        Assert.False(second.IsPinned);
        Assert.Single(viewModel.PinnedObjects);

        viewModel.SelectedVariable = second;
        await EventuallyAsync(() => viewModel.InspectorState != InspectorPaneState.Loading);
        Assert.Contains("worker B", viewModel.SelectedObjectPath);
        viewModel.TogglePinCommand.Execute(null);
        Assert.Equal(2, viewModel.PinnedObjects.Select(item => item.StableKey).Distinct().Count());
    }

    [Fact]
    public async Task UnsupportedNdarrayRemainsReadyWithMetadataAndSkipsPreview()
    {
        var session = new UxSession
        {
            ArrayDescriptionResult = new JsonObject
            {
                ["shape"] = new JsonArray(4),
                ["dtype"] = "complex128",
                ["strides"] = new JsonArray(16),
                ["dataAddressHex"] = "0x400",
                ["ownsData"] = true,
                ["layoutGuess"] = "unsupported",
                ["layoutConfidence"] = "none",
                ["supportedPreviewModes"] = new JsonArray(),
            },
        };
        session.EnqueueScope(ScopeResult(
            Value("spectrum", "array-handle", "array-id", "array-meta", typeName: "ndarray", moduleName: "numpy", adapterKind: "numpy.ndarray")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);

        Assert.True(viewModel.HasArraySelection);
        Assert.Equal("(4)", viewModel.ArrayShape);
        Assert.Equal("complex128", viewModel.ArrayDType);
        Assert.Null(viewModel.ArrayPreview);
        Assert.NotEmpty(viewModel.ObjectRoots);
        Assert.NotEmpty(viewModel.ClassTree);
        Assert.Contains("metadata only", viewModel.SelectedObjectStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.Requests, request => request.Method == "arrays.preview");
        Assert.False(viewModel.ReloadPreviewCommand.CanExecute(null));
        Assert.False(viewModel.LoadTileCommand.CanExecute(null));
        Assert.False(viewModel.LoadHistogramCommand.CanExecute(null));
    }

    [Fact]
    public async Task MatplotlibFigureSelectionLoadsSafeBgraPreviewAndMetadata()
    {
        var binary = BgraBytes(2, 1);
        var session = new UxSession();
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", sourceWidth: 640, sourceHeight: 480));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure", 2, 1, 640, 480), binary);
        session.EnqueueScope(ScopeResult(Value(
            "figure",
            "figure-handle",
            "figure-id",
            "figure-meta",
            typeName: "Figure",
            moduleName: "matplotlib.figure",
            adapterKind: "matplotlib.Figure",
            preview: "Figure(rendered=640x480)",
            payloadSize: 640L * 480 * 4)));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);

        Assert.True(viewModel.HasMatplotlibSelection);
        Assert.False(viewModel.HasArraySelection);
        Assert.False(viewModel.HasDataFrameSelection);
        Assert.Equal(4, viewModel.SelectedObjectDetailTabIndex);
        Assert.Equal("Figure", viewModel.MatplotlibSourceKind);
        Assert.Equal("640 × 480 px", viewModel.MatplotlibSourceDimensions);
        Assert.Equal("matplotlib.backends.backend_agg.FigureCanvasAgg", viewModel.MatplotlibCanvasType);
        Assert.Equal("ready", viewModel.MatplotlibAvailabilityReason);
        Assert.False(viewModel.MatplotlibUsesOwningFigure);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(viewModel.MatplotlibPreview);
        Assert.Equal((2, 1, PixelFormats.Bgra32), (bitmap.PixelWidth, bitmap.PixelHeight, bitmap.Format));
        var copied = new byte[binary.Length];
        bitmap.CopyPixels(copied, bitmap.PixelWidth * 4, 0);
        Assert.Equal(binary, copied);
        var previewRequest = Assert.Single(session.Requests, request => request.Method == "figures.preview");
        Assert.Equal((1024, 1024), (previewRequest.MaxWidth, previewRequest.MaxHeight));
        Assert.DoesNotContain(session.Requests, request => request.Method.Contains("draw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("figure-handle", ReleasedHandles(session));
        Assert.True(viewModel.RefreshMatplotlibCommand.CanExecute(null));
    }

    [Fact]
    public async Task SpecializedSelectionOpensItsMatchingDetailTab()
    {
        var session = new UxSession();
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure"));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure"), BgraBytes(2, 1));
        session.EnqueueScope(ScopeResult(
            Value("plain", "plain-handle", "plain-id", "plain-meta"),
            Value("array", "array-handle", "array-id", "array-meta", typeName: "ndarray", moduleName: "numpy", adapterKind: "numpy.ndarray"),
            Value("figure", "figure-handle", "figure-id", "figure-meta", typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "array");
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        Assert.Equal(5, viewModel.SelectedObjectDetailTabIndex);

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "figure");
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);
        Assert.Equal(4, viewModel.SelectedObjectDetailTabIndex);

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "plain");
        await EventuallyAsync(() => viewModel.SelectedObjectName == "plain");
        Assert.Equal(0, viewModel.SelectedObjectDetailTabIndex);
    }

    [Fact]
    public async Task MatplotlibSelectionExposesLoadingUntilInspectionResponsesArrive()
    {
        var childrenRelease = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new UxSession
        {
            ObjectChildrenResponder = (_, _) => childrenRelease.Task,
        };
        session.EnqueueScope(ScopeResult(Value(
            "figure",
            "figure-handle",
            "figure-id",
            "figure-meta",
            typeName: "Figure",
            moduleName: "matplotlib.figure",
            adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.HasMatplotlibSelection
            && viewModel.MatplotlibState == MatplotlibPaneState.Loading);

        Assert.Null(viewModel.MatplotlibPreview);
        Assert.Contains("Inspecting", viewModel.MatplotlibStatus);

        childrenRelease.SetResult(new JsonObject
        {
            ["items"] = new JsonArray(),
            ["offset"] = 0,
            ["total"] = 0,
        });
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);
    }

    [Fact]
    public async Task MatplotlibAxesStaleStateShowsOwningFigureGuidanceAndRefreshesAfterTargetDraw()
    {
        var session = new UxSession();
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Axes", "unavailable", "stale"));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Axes", sourceWidth: 4, sourceHeight: 2));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Axes", 4, 2), BgraBytes(4, 2, 30));
        session.EnqueueScope(ScopeResult(Value(
            "axes",
            "axes-handle",
            "axes-id",
            "axes-meta",
            typeName: "Axes",
            moduleName: "matplotlib.axes._axes",
            adapterKind: "matplotlib.Axes",
            preview: "Axes(preview unavailable: stale)")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Unavailable);

        Assert.True(viewModel.HasMatplotlibSelection);
        Assert.True(viewModel.MatplotlibUsesOwningFigure);
        Assert.Equal("Axes", viewModel.MatplotlibSourceKind);
        Assert.Equal("stale", viewModel.MatplotlibAvailabilityReason);
        Assert.Contains("pending changes", viewModel.MatplotlibStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("figure.canvas.draw()", viewModel.MatplotlibNextAction, StringComparison.Ordinal);
        Assert.Contains("refresh", viewModel.MatplotlibNextAction, StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.MatplotlibPreview);
        Assert.DoesNotContain(session.Requests, request => request.Method == "figures.preview");

        await viewModel.RefreshMatplotlibCommand.ExecuteAsync();

        Assert.Equal(MatplotlibPaneState.Ready, viewModel.MatplotlibState);
        Assert.NotNull(viewModel.MatplotlibPreview);
        Assert.Equal("4 × 2 px", viewModel.MatplotlibSourceDimensions);
        Assert.True(viewModel.MatplotlibUsesOwningFigure);
        Assert.Contains("refreshed", viewModel.SelectedObjectStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.Requests, request => request.Method.Contains("draw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidMatplotlibBinaryBecomesErrorAndSelectionClearRemovesFigureState()
    {
        var session = new UxSession();
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure"));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure", 2, 1), [1, 2, 3, 4]);
        session.EnqueueScope(ScopeResult(Value(
            "figure",
            "figure-handle",
            "figure-id",
            "figure-meta",
            typeName: "Figure",
            moduleName: "matplotlib.figure",
            adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Error);

        Assert.Equal(InspectorPaneState.Ready, viewModel.InspectorState);
        Assert.True(viewModel.HasMatplotlibSelection);
        Assert.Null(viewModel.MatplotlibPreview);
        Assert.Contains("binary length", viewModel.MatplotlibErrorMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedVariable = null;

        Assert.False(viewModel.HasMatplotlibSelection);
        Assert.Equal(MatplotlibPaneState.NoSelection, viewModel.MatplotlibState);
        Assert.Null(viewModel.MatplotlibPreview);
        Assert.Equal("—", viewModel.MatplotlibSourceKind);
        Assert.False(viewModel.RefreshMatplotlibCommand.CanExecute(null));
    }

    [Fact]
    public async Task LateMatplotlibPreviewCannotOverwriteNewSelectionAndItsTokenIsCancelled()
    {
        var firstPreview = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new UxSession
        {
            MatplotlibPreviewResponder = (handle, cancellationToken) =>
            {
                if (handle == "first-figure-handle")
                {
                    cancellationToken.Register(() => firstCancelled.TrySetResult());
                    return firstPreview.Task;
                }
                return Task.FromResult(new ProtocolFrame(
                    new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 1, 1) },
                    BgraBytes(1, 1, 80)));
            },
        };
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", sourceWidth: 3, sourceHeight: 1));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", sourceWidth: 1, sourceHeight: 1));
        session.EnqueueScope(ScopeResult(
            Value("first", "first-figure-handle", "first-id", "first-meta", typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure"),
            Value("second", "second-figure-handle", "second-id", "second-meta", typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "first");
        await EventuallyAsync(() => session.Requests.Any(request => request.Method == "figures.preview" && request.HandleId == "first-figure-handle"));

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "second");
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => viewModel.SelectedObjectName == "second"
            && viewModel.MatplotlibState == MatplotlibPaneState.Ready);

        firstPreview.SetResult(new ProtocolFrame(
            new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 3, 1) },
            BgraBytes(3, 1, 120)));
        await Task.Delay(50);

        Assert.Equal("second", viewModel.SelectedObjectName);
        Assert.Equal("1 × 1 px", viewModel.MatplotlibSourceDimensions);
        Assert.Equal(1, Assert.IsAssignableFrom<BitmapSource>(viewModel.MatplotlibPreview).PixelWidth);
    }

    [Fact]
    public async Task CancelledMatplotlibFailureCannotOverwriteNewSelection()
    {
        var firstPreview = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var session = new UxSession
        {
            MatplotlibPreviewResponder = (handle, cancellationToken) =>
            {
                if (handle == "first-figure-handle")
                {
                    firstToken = cancellationToken;
                    cancellationToken.Register(() => firstCancelled.TrySetResult());
                    return firstPreview.Task;
                }
                return Task.FromResult(new ProtocolFrame(
                    new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 1, 1) },
                    BgraBytes(1, 1, 80)));
            },
        };
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", sourceWidth: 3, sourceHeight: 1));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", sourceWidth: 1, sourceHeight: 1));
        session.EnqueueScope(ScopeResult(
            Value("first", "first-figure-handle", "first-id", "first-meta", typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure"),
            Value("second", "second-figure-handle", "second-id", "second-meta", typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "first");
        await EventuallyAsync(() => session.Requests.Any(request => request.Method == "figures.preview" && request.HandleId == "first-figure-handle"));

        viewModel.SelectedVariable = Assert.Single(viewModel.Variables, row => row.Name == "second");
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => viewModel.SelectedObjectName == "second"
            && viewModel.MatplotlibState == MatplotlibPaneState.Ready);

        firstPreview.SetException(new OperationCanceledException(firstToken));
        await Task.Delay(50);

        Assert.Equal("second", viewModel.SelectedObjectName);
        Assert.Equal(MatplotlibPaneState.Ready, viewModel.MatplotlibState);
        Assert.Equal("1 × 1 px", viewModel.MatplotlibSourceDimensions);
        Assert.Equal(1, Assert.IsAssignableFrom<BitmapSource>(viewModel.MatplotlibPreview).PixelWidth);
    }

    [Fact]
    public async Task AutomaticRefreshUpdatesMatplotlibBitmapInPlaceWithoutLoadingState()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-1",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-2",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure"));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure"));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure"), BgraBytes(2, 1, 1));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure"), BgraBytes(2, 1, 90));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        var selected = Assert.Single(viewModel.Variables);
        viewModel.SelectedVariable = selected;
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);
        var firstBitmap = viewModel.MatplotlibPreview;
        var loadingWasShown = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.MatplotlibState)
                && viewModel.MatplotlibState == MatplotlibPaneState.Loading)
            {
                loadingWasShown = true;
            }
        };

        viewModel.RefreshIntervalSeconds = 1;
        viewModel.AutoRefreshEnabled = true;
        await EventuallyAsync(() => session.Requests.Count(request => request.Method == "scopes.list") >= 2
            && session.Requests.Count(request => request.Method == "figures.preview") >= 2);
        viewModel.AutoRefreshEnabled = false;

        Assert.False(loadingWasShown);
        Assert.Same(selected, viewModel.SelectedVariable);
        Assert.NotSame(firstBitmap, viewModel.MatplotlibPreview);
        Assert.Equal(MatplotlibPaneState.Ready, viewModel.MatplotlibState);
        Assert.Contains("Live Matplotlib preview refreshed", viewModel.SelectedObjectStatus);
    }

    [Fact]
    public async Task AutomaticBufferChangeKeepsLastCompleteMatplotlibPreviewWithoutFlicker()
    {
        var session = new UxSession();
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-1",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-2",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure"));
        session.EnqueueMatplotlibDescription(MatplotlibDescription("Figure", "unavailable", "buffer-changed"));
        session.EnqueueMatplotlibPreview(MatplotlibPreview("Figure"), BgraBytes(2, 1, 25));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);
        var completePreview = viewModel.MatplotlibPreview;

        viewModel.RefreshIntervalSeconds = 1;
        viewModel.AutoRefreshEnabled = true;
        await EventuallyAsync(() => viewModel.MatplotlibAvailabilityReason == "buffer-changed"
            && session.Requests.Count(request => request.Method == "figures.describe") >= 2);
        viewModel.AutoRefreshEnabled = false;

        Assert.Same(completePreview, viewModel.MatplotlibPreview);
        Assert.Equal(MatplotlibPaneState.Ready, viewModel.MatplotlibState);
        Assert.Contains("keeping the last complete preview", viewModel.MatplotlibStatus);
        Assert.Equal(1, session.Requests.Count(request => request.Method == "figures.preview"));
    }

    [Fact]
    public async Task OlderAutomaticMatplotlibRefreshCannotOverwriteNewerManualRefresh()
    {
        var stalePreview = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var automaticPreviewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewCall = 0;
        var session = new UxSession
        {
            MatplotlibDescriptionResponder = (_, _) => Task.FromResult(MatplotlibDescription("Figure")),
            MatplotlibPreviewResponder = (_, _) => Interlocked.Increment(ref previewCall) switch
            {
                1 => Task.FromResult(new ProtocolFrame(
                    new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 1, 1) },
                    BgraBytes(1, 1, 10))),
                2 => WaitForStalePreviewAsync(),
                _ => Task.FromResult(new ProtocolFrame(
                    new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 2, 1) },
                    BgraBytes(2, 1, 90))),
            },
        };
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-1",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta-2",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Ready);

        viewModel.RefreshIntervalSeconds = 1;
        viewModel.AutoRefreshEnabled = true;
        await automaticPreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.AutoRefreshEnabled = false;

        await Assert.IsType<AsyncCommand>(viewModel.RefreshMatplotlibCommand).ExecuteAsync();
        Assert.Equal(2, Assert.IsAssignableFrom<BitmapSource>(viewModel.MatplotlibPreview).PixelWidth);
        var latestStatus = viewModel.SelectedObjectStatus;

        stalePreview.SetResult(new ProtocolFrame(
            new JsonObject { ["ok"] = true, ["result"] = MatplotlibPreview("Figure", 3, 1) },
            BgraBytes(3, 1, 140)));
        await Task.Delay(50);

        Assert.Equal(MatplotlibPaneState.Ready, viewModel.MatplotlibState);
        Assert.Equal(2, Assert.IsAssignableFrom<BitmapSource>(viewModel.MatplotlibPreview).PixelWidth);
        Assert.Equal(latestStatus, viewModel.SelectedObjectStatus);
        Assert.DoesNotContain("unavailable", viewModel.SelectedObjectStatus, StringComparison.OrdinalIgnoreCase);

        Task<ProtocolFrame> WaitForStalePreviewAsync()
        {
            automaticPreviewStarted.TrySetResult();
            return stalePreview.Task;
        }
    }

    [Theory]
    [InlineData("adapterKind", "matplotlib.Axes")]
    [InlineData("renderedKind", "Axes")]
    [InlineData("sourcePixelFormat", "RGB24")]
    public async Task MatplotlibContractMismatchIsRejected(string propertyName, string invalidValue)
    {
        var invalid = MatplotlibDescription("Figure");
        invalid[propertyName] = invalidValue;
        var session = new UxSession();
        session.EnqueueMatplotlibDescription(invalid);
        session.EnqueueScope(ScopeResult(Value(
            "figure", "figure-handle", "figure-id", "meta",
            typeName: "Figure", moduleName: "matplotlib.figure", adapterKind: "matplotlib.Figure")));
        await using var viewModel = await ConnectedViewModelAsync(session);

        await viewModel.LoadScopeAsync(ScopeNode(), resetPage: true);
        viewModel.SelectedVariable = Assert.Single(viewModel.Variables);
        await EventuallyAsync(() => viewModel.MatplotlibState == MatplotlibPaneState.Error);

        Assert.Null(viewModel.MatplotlibPreview);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.MatplotlibErrorMessage));
    }

    private static async Task<MainViewModel> ConnectedViewModelAsync(UxSession session, IClipboardService? clipboard = null)
    {
        var viewModel = new MainViewModel(session, new EmptyProcessDiscovery(), clipboardService: clipboard)
        {
            AutoRefreshEnabled = false,
        };
        await viewModel.AttachCommand.ExecuteAsync();
        Assert.True(viewModel.IsConnected);
        Assert.Equal("Cooperative listener", viewModel.ConnectionMode);
        return viewModel;
    }

    private static RuntimeTreeNode ScopeNode(string frameHandle = "frame-1", string label = "worker / Locals") => new(label, RuntimeNodeKind.Scope)
    {
        FrameHandle = frameHandle,
        ScopeType = "locals",
    };

    private static JsonObject ScopeResult(params JsonObject[] values) => ScopeResultAt(DateTimeOffset.Now, values);

    private static JsonObject ScopeResultAt(DateTimeOffset timestamp, params JsonObject[] values) => new()
    {
        ["items"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()),
        ["total"] = values.Length,
        ["snapshotTimestamp"] = timestamp.ToString("O"),
    };

    private static JsonObject Value(
        string name,
        string handleId,
        string identityToken,
        string metadataToken,
        string typeName = "Example",
        string moduleName = "sample",
        string? adapterKind = null,
        string preview = "<Example>",
        string address = "0x200",
        bool expandable = false,
        long? payloadSize = null)
    {
        var value = new JsonObject
        {
            ["handleId"] = handleId,
            ["typeName"] = typeName,
            ["moduleName"] = moduleName,
            ["qualifiedTypeName"] = $"{moduleName}.{typeName}",
            ["safePreview"] = preview,
            ["addressHex"] = address,
            ["shallowSizeBytes"] = 64L,
            ["payloadSizeBytes"] = payloadSize ?? (adapterKind == "numpy.ndarray" ? 4L : null),
            ["expandable"] = expandable,
            ["adapterKind"] = adapterKind,
            ["identityToken"] = identityToken,
            ["metadataToken"] = metadataToken,
            ["changeToken"] = identityToken,
        };
        if (adapterKind == "numpy.ndarray")
        {
            value["shape"] = new JsonArray(2, 2);
            value["dtype"] = "uint8";
        }
        return new JsonObject { ["name"] = name, ["value"] = value };
    }

    private static JsonObject DataFrameDescription(int totalRows, int totalColumns, bool consistent = true) => new()
    {
        ["adapterKind"] = "pandas.DataFrame",
        ["totalRows"] = totalRows,
        ["totalColumns"] = totalColumns,
        ["columns"] = new JsonArray(),
        ["columnsTruncated"] = totalColumns > 100,
        ["mutationDetected"] = !consistent,
        ["snapshotConsistent"] = consistent,
        ["maxPreviewRows"] = 200,
        ["maxPreviewColumns"] = 100,
    };

    private static JsonObject DataFramePreview(
        int rowOffset,
        int rowCount,
        int totalRows,
        int columnOffset,
        int columnCount,
        int totalColumns,
        string valuePrefix = "value",
        bool consistent = true)
    {
        var columns = new JsonArray(Enumerable.Range(columnOffset, columnCount)
            .Select(position => (JsonNode)new JsonObject
            {
                ["position"] = position,
                ["name"] = $"column_{position}",
                ["dtype"] = position % 2 == 0 ? "int64" : "string",
            }).ToArray());
        var indexLabels = new JsonArray(Enumerable.Range(rowOffset, rowCount)
            .Select(row => (JsonNode)JsonValue.Create($"index_{row}")!)
            .ToArray());
        var rows = new JsonArray(Enumerable.Range(rowOffset, rowCount)
            .Select(row => (JsonNode)new JsonArray(Enumerable.Range(columnOffset, columnCount)
                .Select(column => (JsonNode)JsonValue.Create($"{valuePrefix}_{row}_{column}")!)
                .ToArray()))
            .ToArray());
        return new JsonObject
        {
            ["adapterKind"] = "pandas.DataFrame",
            ["columns"] = columns,
            ["indexLabels"] = indexLabels,
            ["rows"] = rows,
            ["rowOffset"] = rowOffset,
            ["rowCount"] = rowCount,
            ["totalRows"] = totalRows,
            ["columnOffset"] = columnOffset,
            ["columnCount"] = columnCount,
            ["totalColumns"] = totalColumns,
            ["hasMoreRows"] = rowOffset + rowCount < totalRows,
            ["hasMoreColumns"] = columnOffset + columnCount < totalColumns,
            ["rowsTruncated"] = rowOffset > 0 || rowOffset + rowCount < totalRows,
            ["columnsTruncated"] = columnOffset > 0 || columnOffset + columnCount < totalColumns,
            ["cellLimitApplied"] = false,
            ["mutationDetected"] = !consistent,
            ["snapshotConsistent"] = consistent,
        };
    }

    private static JsonObject MatplotlibDescription(
        string sourceKind,
        string availabilityState = "ready",
        string? reason = null,
        int sourceWidth = 2,
        int sourceHeight = 1,
        string canvasType = "matplotlib.backends.backend_agg.FigureCanvasAgg")
    {
        var ready = availabilityState == "ready";
        var message = reason switch
        {
            "stale" => "The owning Figure has pending changes or has not been drawn yet.",
            "not-rendered" => "The owning Figure does not have a completed Agg render.",
            _ when ready => "A current, completed Agg render is available.",
            _ => "The Matplotlib render is unavailable.",
        };
        var nextAction = reason switch
        {
            "stale" => "Call figure.canvas.draw() in the target code after its final changes, then refresh the preview.",
            "not-rendered" => "Call figure.canvas.draw() in the target code, then refresh the preview.",
            _ => null,
        };
        return new JsonObject
        {
            ["adapterKind"] = sourceKind == "Axes" ? "matplotlib.Axes" : "matplotlib.Figure",
            ["sourceKind"] = sourceKind,
            ["renderedKind"] = "Figure",
            ["axesUsesOwningFigure"] = sourceKind == "Axes",
            ["objectAddressHex"] = "0x500",
            ["figureAddressHex"] = "0x500",
            ["canvasType"] = canvasType,
            ["rendererType"] = ready ? "matplotlib.backends.backend_agg.RendererAgg" : null,
            ["stale"] = reason == "stale",
            ["previewAvailable"] = ready,
            ["availability"] = new JsonObject
            {
                ["state"] = availabilityState,
                ["reason"] = reason,
                ["message"] = message,
                ["nextAction"] = nextAction,
            },
            ["sourceWidth"] = ready ? sourceWidth : null,
            ["sourceHeight"] = ready ? sourceHeight : null,
            ["sourceChannels"] = 4,
            ["sourcePixelFormat"] = "RGBA32",
            ["sourceBufferBytes"] = ready ? sourceWidth * sourceHeight * 4 : null,
            ["maxPreviewWidth"] = 1024,
            ["maxPreviewHeight"] = 1024,
            ["maxPreviewBytes"] = 4 * 1024 * 1024,
            ["snapshotConsistent"] = ready,
        };
    }

    private static JsonObject MatplotlibPreview(
        string sourceKind,
        int width = 2,
        int height = 1,
        int? sourceWidth = null,
        int? sourceHeight = null)
    {
        var result = MatplotlibDescription(
            sourceKind,
            sourceWidth: sourceWidth ?? width,
            sourceHeight: sourceHeight ?? height);
        result["width"] = width;
        result["height"] = height;
        result["stride"] = width * 4;
        result["pixelFormat"] = "BGRA32";
        result["rowStep"] = 1;
        result["columnStep"] = 1;
        result["originX"] = 0;
        result["originY"] = 0;
        result["snapshotConsistent"] = true;
        return result;
    }

    private static byte[] BgraBytes(int width, int height, byte seed = 1) =>
        Enumerable.Range(0, checked(width * height * 4))
            .Select(index => (byte)(seed + index))
            .ToArray();

    private static JsonObject ObjectChildrenResult() => new()
    {
        ["items"] = new JsonArray(
            Child("child", "child-handle", "child-id", "0x201", depth: 1, canExpand: true),
            Child("loop", "loop-handle", "root-identity", "0x100", depth: 1, canExpand: true, isCycle: true),
            Child("deep", "deep-handle", "deep-id", "0x208", depth: 8, canExpand: true)),
        ["offset"] = 0,
        ["total"] = 3,
    };

    private static JsonObject SingleChildResult(string name, string handleId) => new()
    {
        ["items"] = new JsonArray(Child(name, handleId, $"{name}-id", "0x250", depth: 1, canExpand: false)),
        ["offset"] = 0,
        ["total"] = 1,
    };

    private static JsonObject Child(
        string name,
        string handleId,
        string identityToken,
        string address,
        int depth,
        bool canExpand,
        bool isCycle = false) => new()
    {
        ["name"] = name,
        ["origin"] = "instance",
        ["pathSegment"] = name,
        ["depth"] = depth,
        ["canExpand"] = canExpand,
        ["isCycle"] = isCycle,
        ["value"] = new JsonObject
        {
            ["handleId"] = handleId,
            ["typeName"] = "Example",
            ["moduleName"] = "sample",
            ["qualifiedTypeName"] = "sample.Example",
            ["safePreview"] = $"<{name}>",
            ["addressHex"] = address,
            ["shallowSizeBytes"] = 64L,
            ["expandable"] = canExpand,
            ["identityToken"] = identityToken,
            ["metadataToken"] = $"meta-{name}",
        },
    };

    private static JsonObject StructuredClassDescription(bool membersTruncated = false) => new()
    {
        ["name"] = "Example",
        ["qualifiedName"] = "Example",
        ["module"] = "sample",
        ["metaclass"] = "type",
        ["metaclassRef"] = new JsonObject
        {
            ["module"] = "builtins",
            ["qualifiedName"] = "type",
            ["displayName"] = "builtins.type",
        },
        ["baseClassRefs"] = new JsonArray(new JsonObject { ["displayName"] = "base.BaseExample" }),
        ["mroRefs"] = new JsonArray(
            new JsonObject { ["displayName"] = "sample.Example" },
            new JsonObject { ["displayName"] = "base.BaseExample" }),
        ["members"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "render",
                ["kind"] = "instance method",
                ["declaredBy"] = "Example",
                ["declaredByRef"] = new JsonObject
                {
                    ["module"] = "sample",
                    ["qualifiedName"] = "Example",
                    ["displayName"] = "sample.Example",
                },
                ["inherited"] = false,
                ["signatureDetails"] = new JsonObject
                {
                    ["display"] = "(self, width: int = 80)",
                    ["parameters"] = new JsonArray(
                        new JsonObject { ["name"] = "self", ["kind"] = "positionalOrKeyword" },
                        new JsonObject
                        {
                            ["name"] = "width",
                            ["kind"] = "positionalOrKeyword",
                            ["annotationText"] = "int",
                            ["defaultPreview"] = "80",
                        }),
                },
                ["source"] = new JsonObject { ["file"] = "sample.py", ["line"] = 41 },
            },
            new JsonObject
            {
                ["name"] = "name",
                ["kind"] = "property",
                ["declaredBy"] = "BaseExample",
                ["declaredByRef"] = new JsonObject
                {
                    ["module"] = "base",
                    ["qualifiedName"] = "BaseExample",
                    ["displayName"] = "base.BaseExample",
                },
                ["inherited"] = true,
                ["signature"] = "—",
            }),
        ["memberTotal"] = membersTruncated ? 300 : 2,
        ["memberLimit"] = 256,
        ["membersTruncated"] = membersTruncated,
    };

    private static JsonObject LargeStructuredClassDescription(int memberCount, int parametersPerMember)
    {
        var members = new JsonArray();
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            var parameters = new JsonArray();
            for (var parameterIndex = 0; parameterIndex < parametersPerMember; parameterIndex++)
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = $"argument_{memberIndex:D4}_{parameterIndex:D2}",
                    ["kind"] = "positionalOrKeyword",
                    ["annotationText"] = "int",
                });
            }

            members.Add(new JsonObject
            {
                ["name"] = $"member_{memberIndex:D4}",
                ["kind"] = "instance method",
                ["declaredBy"] = "Example",
                ["declaredByRef"] = new JsonObject
                {
                    ["module"] = "sample",
                    ["qualifiedName"] = "Example",
                    ["displayName"] = "sample.Example",
                },
                ["inherited"] = false,
                ["signature"] = "(self, ...)",
                ["parameters"] = parameters.DeepClone(),
                ["signatureDetails"] = new JsonObject
                {
                    ["display"] = "(self, ...)",
                    ["parameters"] = parameters,
                },
            });
        }

        var description = StructuredClassDescription();
        description["members"] = members;
        description["memberTotal"] = memberCount;
        description["memberLimit"] = memberCount;
        return description;
    }

    private static IEnumerable<ClassTreeNode> FlattenClassTree(IEnumerable<ClassTreeNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in FlattenClassTree(root.Children))
                yield return child;
        }
    }

    private static bool IsSemanticClassTreeNode(ClassTreeNode node) =>
        node.Kind is not ("group" or "metadata-group" or "parameters" or "status");

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FindVisualDescendant<T>(child) is T descendant)
                return descendant;
        }
        return null;
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static HashSet<string> ReleasedHandles(UxSession session) => session.Requests
        .Where(request => request.Method == "objects.release" && request.HandleId is not null)
        .Select(request => request.HandleId!)
        .ToHashSet(StringComparer.Ordinal);

    private sealed class EmptyProcessDiscovery : IProcessDiscovery
    {
        public IReadOnlyList<ProcessItem> GetPythonProcesses() => [];
        public ProcessMemoryInfo? GetMemoryInfo(int pid) => null;
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    private sealed class UxSession : IInspectorSession
    {
        private readonly Queue<JsonObject> _scopeResults = [];
        private readonly Queue<JsonObject> _dataFramePreviewResults = [];
        private readonly Queue<JsonObject> _matplotlibDescriptionResults = [];
        private readonly Queue<(JsonObject Result, byte[] Binary)> _matplotlibPreviewResults = [];

        public event EventHandler<string>? Disconnected;
        public bool IsConnected { get; private set; }
        public ConcurrentQueue<RequestRecord> Requests { get; } = [];
        public TaskCompletionSource? ScopeRelease { get; init; }
        public Func<string?, CancellationToken, Task<JsonObject>>? ObjectChildrenResponder { get; init; }
        public Func<CancellationToken, Task<JsonObject>>? DataFramePreviewResponder { get; init; }
        public Func<string?, CancellationToken, Task<JsonObject>>? MatplotlibDescriptionResponder { get; init; }
        public Func<string?, CancellationToken, Task<ProtocolFrame>>? MatplotlibPreviewResponder { get; init; }
        public JsonObject ObjectChildren { get; init; } = new()
        {
            ["items"] = new JsonArray(),
            ["offset"] = 0,
            ["total"] = 0,
        };
        public JsonObject ClassDescription { get; init; } = new()
        {
            ["name"] = "object",
            ["qualifiedName"] = "object",
            ["module"] = "builtins",
            ["members"] = new JsonArray(),
        };
        public JsonObject ArrayDescriptionResult { get; init; } = SupportedArrayDescription();
        public JsonObject DataFrameDescriptionResult { get; init; } = DataFrameDescription(0, 0);
        public JsonObject DataFramePreviewResult { get; init; } = DataFramePreview(0, 0, 0, 0, 0, 0);
        public JsonObject MatplotlibDescriptionResult { get; init; } = MatplotlibDescription("Figure");
        public JsonObject MatplotlibPreviewResult { get; init; } = MatplotlibPreview("Figure");
        public byte[] MatplotlibPreviewBinary { get; init; } = [1, 2, 3, 255, 4, 5, 6, 255];

        public void EnqueueScope(JsonObject result) => _scopeResults.Enqueue(result);
        public void EnqueueDataFramePreview(JsonObject result) => _dataFramePreviewResults.Enqueue(result);
        public void EnqueueMatplotlibDescription(JsonObject result) => _matplotlibDescriptionResults.Enqueue(result);
        public void EnqueueMatplotlibPreview(JsonObject result, byte[] binary) => _matplotlibPreviewResults.Enqueue((result, binary));

        public Task<JsonObject> AttachAsync(int port, string token, int? expectedPid, CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.FromResult(RuntimeInfo());
        }

        public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
        {
            var handleId = parameters?["handleId"]?.GetValue<string>();
            Requests.Enqueue(new RequestRecord(
                method,
                handleId,
                parameters?["rowOffset"]?.GetValue<int>(),
                parameters?["columnOffset"]?.GetValue<int>(),
                parameters?["rowCount"]?.GetValue<int>(),
                parameters?["columnCount"]?.GetValue<int>(),
                parameters?["maxWidth"]?.GetValue<int>(),
                parameters?["maxHeight"]?.GetValue<int>()));
            if (method == "scopes.list" && ScopeRelease is not null)
                return await AwaitScopeAsync(cancellationToken);
            if (method == "objects.listChildren" && ObjectChildrenResponder is not null)
                return Frame(await ObjectChildrenResponder(handleId, cancellationToken));
            if (method == "dataframes.preview" && DataFramePreviewResponder is not null)
                return Frame(await DataFramePreviewResponder(cancellationToken));
            if (method == "figures.describe" && MatplotlibDescriptionResponder is not null)
                return Frame(await MatplotlibDescriptionResponder(handleId, cancellationToken));
            if (method == "figures.preview" && MatplotlibPreviewResponder is not null)
                return await MatplotlibPreviewResponder(handleId, cancellationToken);
            if (method == "figures.preview")
            {
                var (preview, binary) = _matplotlibPreviewResults.Count > 0
                    ? _matplotlibPreviewResults.Dequeue()
                    : ((JsonObject)MatplotlibPreviewResult.DeepClone(), MatplotlibPreviewBinary.ToArray());
                return Frame(preview, binary);
            }
            var result = method switch
            {
                "threads.list" or "frames.list" => new JsonObject { ["items"] = new JsonArray() },
                "modules.list" => new JsonObject { ["items"] = new JsonArray(), ["total"] = 0 },
                "scopes.list" => _scopeResults.Dequeue(),
                "objects.listChildren" => (JsonObject)ObjectChildren.DeepClone(),
                "classes.describe" => (JsonObject)ClassDescription.DeepClone(),
                "runtime.getInfo" => RuntimeInfo(),
                "memory.status" => MemoryStatus(),
                "execution.status" => ExecutionStatus(),
                "arrays.describe" => (JsonObject)ArrayDescriptionResult.DeepClone(),
                "arrays.preview" => ArrayPreview(),
                "dataframes.describe" => (JsonObject)DataFrameDescriptionResult.DeepClone(),
                "dataframes.preview" => _dataFramePreviewResults.Count > 0
                    ? _dataFramePreviewResults.Dequeue()
                    : (JsonObject)DataFramePreviewResult.DeepClone(),
                "figures.describe" => _matplotlibDescriptionResults.Count > 0
                    ? _matplotlibDescriptionResults.Dequeue()
                    : (JsonObject)MatplotlibDescriptionResult.DeepClone(),
                _ => new JsonObject(),
            };
            return Frame(result, method == "arrays.preview" ? [128] : null);
        }

        private async Task<ProtocolFrame> AwaitScopeAsync(CancellationToken cancellationToken)
        {
            await ScopeRelease!.Task.WaitAsync(cancellationToken);
            return Frame(_scopeResults.Dequeue());
        }

        public Task DetachAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseDisconnected(string message)
        {
            IsConnected = false;
            Disconnected?.Invoke(this, message);
        }

        private static JsonObject RuntimeInfo() => new()
        {
            ["pid"] = 1234,
            ["version"] = "3.12.9",
            ["processArchitecture"] = "64-bit",
            ["executable"] = "python.exe",
        };

        private static JsonObject MemoryStatus() => new()
        {
            ["tracing"] = false,
            ["startedAt"] = null,
            ["startedByInspector"] = false,
            ["tracebackDepth"] = 0,
            ["currentBytes"] = 0L,
            ["peakBytes"] = 0L,
            ["overheadBytes"] = 0L,
        };

        private static JsonObject ExecutionStatus() => new()
        {
            ["available"] = true,
            ["active"] = false,
            ["toolId"] = null,
            ["bufferedCount"] = 0,
            ["bufferCapacity"] = 5000,
            ["droppedCount"] = 0L,
        };

        private static JsonObject SupportedArrayDescription() => new()
        {
            ["shape"] = new JsonArray(1, 1),
            ["dtype"] = "uint8",
            ["strides"] = new JsonArray(1, 1),
            ["dataAddressHex"] = "0x300",
            ["ownsData"] = true,
            ["layoutGuess"] = "GRAY",
            ["layoutConfidence"] = "certain",
            ["supportedPreviewModes"] = new JsonArray("GRAY"),
        };

        private static JsonObject ArrayPreview() => new()
        {
            ["width"] = 1,
            ["height"] = 1,
            ["stride"] = 1,
            ["pixelFormat"] = "Gray8",
            ["rowStep"] = 1,
            ["columnStep"] = 1,
            ["originX"] = 0,
            ["originY"] = 0,
            ["sourceWidth"] = 1,
            ["sourceHeight"] = 1,
            ["normalization"] = new JsonObject
            {
                ["mode"] = "NONE",
                ["displayMinimum"] = 0.0,
                ["displayMaximum"] = 255.0,
                ["nanCount"] = 0,
                ["positiveInfinityCount"] = 0,
                ["negativeInfinityCount"] = 0,
            },
        };

        private static ProtocolFrame Frame(JsonObject result, byte[]? binary = null) => new(new JsonObject
        {
            ["ok"] = true,
            ["result"] = result,
        }, binary ?? []);
    }

    private sealed record RequestRecord(
        string Method,
        string? HandleId,
        int? RowOffset = null,
        int? ColumnOffset = null,
        int? RowCount = null,
        int? ColumnCount = null,
        int? MaxWidth = null,
        int? MaxHeight = null);
}
